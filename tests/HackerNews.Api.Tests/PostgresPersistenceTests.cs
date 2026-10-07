using HackerNews.Api.Health;
using HackerNews.Api.Models;
using HackerNews.Api.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace HackerNews.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HACKERNEWS_TEST_POSTGRES")))
        {
            Skip = "Set HACKERNEWS_TEST_POSTGRES to run isolated real PostgreSQL tests.";
        }
    }
}

public sealed class PostgresPersistenceTests : IAsyncLifetime
{
    private string? database;
    private NpgsqlDataSource? dataSource;
    private NpgsqlDataSource? admin;
    private NpgsqlDataSource Source => dataSource ?? throw new InvalidOperationException("No test database.");
    private const string Upstream = "https://fake.test/v0/";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("HACKERNEWS_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }
        admin = NpgsqlDataSource.Create(connection);
        database = "hn_test_" + Guid.NewGuid().ToString("N");
        await using var create = admin.CreateCommand($"CREATE DATABASE \"{database}\"");
        await create.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(connection)
        {
            Database = database,
            Pooling = false
        };
        dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (dataSource is not null)
        {
            await dataSource.DisposeAsync();
        }
        if (admin is not null && database is not null)
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE \"{database}\" WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private static StoryResponse Story(int score) =>
        new("Story", "https://example.test", "author", Now, score, 10);

    [PostgresFact]
    public async Task Initialize_Restart_RestoresBoundedRankingAndPendingWork()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            Assert.Null(await store.InitializeAsync(Upstream, 2, default));
            var batch = await store.PrepareBatchAsync([1, 2, 3], Now, Interval, 3, false, default);
            var snapshot = await store.CommitBatchAsync(batch,
                batch.Items.Select(item => new StoryFetchResult(item, Story((int)item.Id), true)).ToArray(),
                Now, 2, default);
            Assert.Equal([3, 2], snapshot!.Stories.Select(story => story.Score));
            await store.MarkUpdatedAsync([1, 999], default);
        }
        await using (var restarted = new PostgresStoryStateStore(Source))
        {
            var restored = await restarted.InitializeAsync(Upstream, 2, default);
            Assert.Equal([3, 2], restored!.Stories.Select(story => story.Score));
            var work = await restarted.PrepareBatchAsync([1, 2, 3], Now.AddMinutes(1), Interval, 10, false, default);
            Assert.Equal(1, Assert.Single(work.Items).Id);
            var next = await restarted.CommitBatchAsync(work,
                [new StoryFetchResult(work.Items[0], Story(99), true)], Now.AddMinutes(1), 2, default);
            Assert.Equal([99, 3], next!.Stories.Select(story => story.Score));
        }
    }

    [PostgresFact]
    public async Task Initialize_RestartService_RestoresWithoutUpstreamCalls()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            using var initial = new ServiceHarness(store: store);
            await initial.Service.RefreshIncrementalAsync();
            Assert.NotNull(initial.Service.Snapshot);
        }
        await using var restarted = new PostgresStoryStateStore(Source);
        using var harness = new ServiceHarness(store: restarted);
        harness.Handler.Send = (_, _) => throw new InvalidOperationException("Restoration must not call upstream.");
        await harness.Service.InitializeAsync();
        var result = await harness.Service.GetBestAsync(2, default);
        Assert.Equal([3, 2], result!.Value.Select(story => story.Score));
    }

    [PostgresFact]
    public async Task Prepare_InterruptedBootstrap_ResumesMissingItemsWithinBatchLimit()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var first = await store.PrepareBatchAsync([1, 2, 3], Now, Interval, 1, false, default);
            await store.CommitBatchAsync(first, [new StoryFetchResult(first.Items[0], Story(1), true)], Now, 100, default);
            // Selecting work does not acknowledge it; a crash here must not lose it.
            var interrupted = await store.PrepareBatchAsync([1, 2, 3], Now.AddMinutes(1), Interval, 1, false, default);
            Assert.Equal(2, Assert.Single(interrupted.Items).Id);
        }
        await using var resumed = new PostgresStoryStateStore(Source);
        Assert.Single((await resumed.InitializeAsync(Upstream, 100, default))!.Stories);
        var batch = await resumed.PrepareBatchAsync([1, 2, 3], Now.AddMinutes(2), Interval, 1, false, default);
        Assert.Equal(2, Assert.Single(batch.Items).Id);
    }

    [PostgresFact]
    public async Task Commit_NotificationDuringFetch_LeavesNewerVersionPending()
    {
        await using var store = new PostgresStoryStateStore(Source);
        await store.InitializeAsync(Upstream, 100, default);
        var first = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
        await store.MarkUpdatedAsync([1, 1], default);
        await store.CommitBatchAsync(first, [new StoryFetchResult(first.Items[0], Story(1), true)], Now, 100, default);
        var next = await store.PrepareBatchAsync([1], Now.AddMinutes(1), Interval, 1, false, default);
        Assert.Single(next.Items);
        Assert.True(next.Items[0].Version > first.Items[0].Version);
    }

    [PostgresFact]
    public async Task Prepare_RestartDuringReconciliation_ContinuesRemainingCandidates()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var all = await store.PrepareBatchAsync([1, 2, 3], Now, Interval, 3, false, default);
            await store.CommitBatchAsync(all, all.Items.Select(item => new StoryFetchResult(item, Story((int)item.Id), true)).ToArray(),
                Now, 100, default);
            var first = await store.PrepareBatchAsync([1, 2, 3], Now.AddMinutes(30), Interval, 1, false, default);
            Assert.Equal(1, Assert.Single(first.Items).Id);
            await store.CommitBatchAsync(first, [new StoryFetchResult(first.Items[0], Story(1), true)],
                Now.AddMinutes(30), 100, default);
        }
        await using var resumed = new PostgresStoryStateStore(Source);
        await resumed.InitializeAsync(Upstream, 100, default);
        var remaining = await resumed.PrepareBatchAsync([1, 2, 3], Now.AddMinutes(31), Interval, 10, false, default);
        Assert.Equal([2L, 3L], remaining.Items.Select(item => item.Id));
    }

    [PostgresFact]
    public async Task Commit_AllItemsRemoved_RestoresLastUsableSnapshotAfterRestart()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var first = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
            await store.CommitBatchAsync(first, [new StoryFetchResult(first.Items[0], Story(42), true)], Now, 100, default);
            var removed = await store.PrepareBatchAsync([], Now.AddMinutes(1), Interval, 1, false, default);
            var stale = await store.CommitBatchAsync(removed, [], Now.AddMinutes(1), 100, default);
            Assert.Equal(42, Assert.Single(stale!.Stories).Score);
        }
        await using var restarted = new PostgresStoryStateStore(Source);
        var snapshot = await restarted.InitializeAsync(Upstream, 100, default);
        Assert.Equal(42, Assert.Single(snapshot!.Stories).Score);
    }

    [PostgresFact]
    public async Task Commit_DatabaseWriteFailure_RollsBackRecordsProgressAndSnapshot()
    {
        await using var store = new PostgresStoryStateStore(Source);
        await store.InitializeAsync(Upstream, 100, default);
        var first = await store.PrepareBatchAsync([1, 2], Now, Interval, 2, false, default);
        await store.CommitBatchAsync(first, first.Items.Select(item => new StoryFetchResult(item, Story(1), true)).ToArray(),
            Now, 100, default);
        await store.MarkUpdatedAsync([1, 2], default);
        var work = await store.PrepareBatchAsync([1, 2], Now.AddMinutes(1), Interval, 2, false, default);
        await ExecuteAsync("""
            CREATE FUNCTION reject_second() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.id = 2 AND NEW.score = 99 THEN RAISE EXCEPTION 'simulated failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_second BEFORE UPDATE ON hn_candidates FOR EACH ROW EXECUTE FUNCTION reject_second();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => store.CommitBatchAsync(work,
            work.Items.Select(item => new StoryFetchResult(item, Story(99), true)).ToArray(),
            Now.AddMinutes(1), 100, default));
        var restored = await store.InitializeAsync(Upstream, 100, default);
        Assert.All(restored!.Stories, story => Assert.Equal(1, story.Score));
        var retry = await store.PrepareBatchAsync([1, 2], Now.AddMinutes(2), Interval, 2, false, default);
        Assert.Equal(2, retry.Items.Count);
    }

    [PostgresFact]
    public async Task Initialize_SecondIngestionOwner_IsRejected()
    {
        await using var first = new PostgresStoryStateStore(Source);
        await first.InitializeAsync(Upstream, 100, default);
        await using var second = new PostgresStoryStateStore(Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InitializeAsync(Upstream, 100, default));
    }

    [PostgresFact]
    public async Task Initialize_SourceMismatch_FailsInsteadOfReinitializing()
    {
        await using (var first = new PostgresStoryStateStore(Source))
        {
            await first.InitializeAsync(Upstream, 100, default);
        }
        await using var second = new PostgresStoryStateStore(Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InitializeAsync("https://different.test/", 100, default));
    }

    [PostgresFact]
    public async Task Initialize_UnknownSchemaVersion_FailsExplicitly()
    {
        await using (var first = new PostgresStoryStateStore(Source))
        {
            await first.InitializeAsync(Upstream, 100, default);
        }
        await ExecuteAsync("UPDATE hn_state SET schema_version = 999;");
        await using var second = new PostgresStoryStateStore(Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InitializeAsync(Upstream, 100, default));
    }

    [PostgresFact]
    public async Task Initialize_CorruptSnapshot_FailsExplicitly()
    {
        await using (var first = new PostgresStoryStateStore(Source))
        {
            await first.InitializeAsync(Upstream, 100, default);
        }
        await ExecuteAsync("UPDATE hn_state SET snapshot = '{}'::jsonb;");
        await using var second = new PostgresStoryStateStore(Source);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => second.InitializeAsync(Upstream, 100, default));
    }

    [PostgresFact]
    public async Task CheckHealth_ConnectedDatabase_ReportsHealthyWithoutUpstream()
    {
        var check = new DatabaseReadinessCheck(Source);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [PostgresFact]
    public async Task Initialize_PreviousSchema_MigratesWithoutLosingRecords()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var batch = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
            await store.CommitBatchAsync(batch, [new StoryFetchResult(batch.Items[0], Story(42), true)], Now, 100, default);
        }
        await ExecuteAsync("ALTER TABLE hn_state DROP COLUMN generation; UPDATE hn_state SET schema_version = 1;");
        await using var migrated = new PostgresStoryStateStore(Source);
        var snapshot = await migrated.InitializeAsync(Upstream, 100, default);
        Assert.Equal(42, Assert.Single(snapshot!.Stories).Score);
        await using var version = Source.CreateCommand("SELECT schema_version FROM hn_state");
        Assert.Equal(2, await version.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Commit_CanceledOperation_LeavesPendingWorkRecoverable()
    {
        await using var store = new PostgresStoryStateStore(Source);
        await store.InitializeAsync(Upstream, 100, default);
        var batch = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CommitBatchAsync(batch,
            [new StoryFetchResult(batch.Items[0], Story(42), true)], Now, 100, cancellation.Token));
        var retry = await store.PrepareBatchAsync([1], Now.AddMinutes(1), Interval, 1, false, default);
        Assert.Equal(batch.Items[0], Assert.Single(retry.Items));
        Assert.Null(await store.InitializeAsync(Upstream, 100, default));
    }

    [PostgresFact]
    public async Task Commit_CrashedAfterCommitBeforePublication_RestartRestoresCommittedGeneration()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var batch = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
            await store.CommitBatchAsync(batch, [new StoryFetchResult(batch.Items[0], Story(42), true)], Now, 100, default);
            // No memory snapshot is published: simulate a crash immediately after the durable commit.
        }
        await using var restored = new PostgresStoryStateStore(Source);
        var snapshot = await restored.InitializeAsync(Upstream, 100, default);
        Assert.Equal(42, Assert.Single(snapshot!.Stories).Score);
        await using var generation = Source.CreateCommand("SELECT generation FROM hn_state");
        Assert.Equal(1L, await generation.ExecuteScalarAsync());
        var pending = await restored.PrepareBatchAsync([1], Now.AddMinutes(1), Interval, 1, false, default);
        Assert.Empty(pending.Items);
    }

    [PostgresFact]
    public async Task CheckHealth_InvalidDatabase_ReportsUnhealthy()
    {
        var builder = new NpgsqlConnectionStringBuilder(Source.ConnectionString)
        {
            Database = "missing_" + Guid.NewGuid().ToString("N")
        };
        await using var missing = NpgsqlDataSource.Create(builder.ConnectionString);
        var check = new DatabaseReadinessCheck(missing);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [PostgresFact]
    public async Task Initialize_CorruptStoryFields_FailsExplicitly()
    {
        await using (var store = new PostgresStoryStateStore(Source))
        {
            await store.InitializeAsync(Upstream, 100, default);
            var batch = await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
            await store.CommitBatchAsync(batch, [new StoryFetchResult(batch.Items[0], Story(42), true)], Now, 100, default);
        }
        await ExecuteAsync("UPDATE hn_state SET snapshot = jsonb_set(snapshot, '{Stories,0,Title}', 'null'::jsonb);");
        await using var restarted = new PostgresStoryStateStore(Source);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => restarted.InitializeAsync(Upstream, 100, default));
    }

    [PostgresFact]
    public async Task MarkUpdated_LostOwnerSession_FailsWithoutReconnecting()
    {
        await using var store = new PostgresStoryStateStore(Source);
        await store.InitializeAsync(Upstream, 100, default);
        await store.PrepareBatchAsync([1], Now, Interval, 1, false, default);
        await using var terminate = Source.CreateCommand("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid();
            """);
        Assert.True((bool)(await terminate.ExecuteScalarAsync())!);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => store.MarkUpdatedAsync([1], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkUpdatedAsync([1], default));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
