using System.Diagnostics;
using System.Text.Json;
using HackerNews.Api.Exports;
using HackerNews.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace HackerNews.Api.Tests;

public sealed class PostgresExportScaleFactAttribute : PostgresFactAttribute
{
    public PostgresExportScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HACKERNEWS_EXPORT_SCALE_TESTS") != "1")
        {
            Skip = "Set HACKERNEWS_EXPORT_SCALE_TESTS=1 and HACKERNEWS_TEST_POSTGRES for the million-row fixture.";
        }
    }
}

public sealed partial class PostgresPersistenceTests
{
    private async Task<PostgresExportStore> ExportsAsync(FakeTimeProvider? time = null, ExportOptions? options = null)
    {
        await using (var stories = new PostgresStoryStateStore(Source))
        {
            await stories.InitializeAsync(Upstream, 100, default);
            var batch = await stories.PrepareBatchAsync([1, 2, 3], Now, Interval, 3, false, default);
            await stories.CommitBatchAsync(batch, batch.Items.Select(item =>
                new HackerNews.Api.Storage.StoryFetchResult(item, Story((int)item.Id), true)).ToArray(), Now, 100, default);
        }
        var exports = NewExports(time, options);
        await exports.InitializeAsync(default);
        return exports;
    }

    private PostgresExportStore NewExports(FakeTimeProvider? time = null, ExportOptions? options = null) =>
        new(Source, Options.Create(options ?? new ExportOptions { ChunkSize = 1 }),
            time ?? new FakeTimeProvider(Now), NullLogger<PostgresExportStore>.Instance);

    [PostgresFact]
    public async Task Export_RestartBetweenChunks_RestoresProgressAndFrozenValues()
    {
        var exports = await ExportsAsync();
        var creation = await exports.CreateAsync(100, "restart", default);
        var id = creation.Operation!.OperationId;
        Assert.Equal("queued", creation.Operation.Status);
        Assert.Empty((await exports.ReadAsync(id, 0, 10, default)).Stories);
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        Assert.Equal(1, (await exports.GetAsync(id, default))!.ProcessedCount);
        await ExecuteAsync("DELETE FROM hn_candidates; UPDATE hn_state SET generation = generation + 1;");
        var restarted = NewExports();
        await restarted.InitializeAsync(default);
        Assert.Equal(await exports.GetSigningKeyAsync(default), await restarted.GetSigningKeyAsync(default));
        await restarted.ProcessNextAsync(default);
        await restarted.ProcessNextAsync(default);
        var completed = await restarted.GetAsync(id, default);
        Assert.Equal("succeeded", completed!.Status);
        Assert.Equal(3, completed.ProcessedCount);
        Assert.Equal(3, completed.ResultCount);
        Assert.Equal(1, completed.DatasetGeneration);
        var first = await restarted.ReadAsync(id, 0, 2, default);
        var last = await restarted.ReadAsync(id, 2, 2, default);
        Assert.Equal([3, 2, 1], first.Stories.Concat(last.Stories).Select(story => story.Score));
    }

    [PostgresFact]
    public async Task Export_ConcurrentIdempotencyAndConflictingCount_EnqueuesOneOperation()
    {
        var exports = await ExportsAsync();
        var submissions = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => exports.CreateAsync(100, "same-key", default)));
        Assert.Single(submissions.Select(result => result.Operation!.OperationId).Distinct());
        Assert.Equal(409, (await exports.CreateAsync(101, "same-key", default)).FailureStatus);
        await using var count = Source.CreateCommand("SELECT count(*) FROM hn_exports");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Export_QuotasAndExpiration_ReleasesCapacityAndRetainsTombstone()
    {
        var time = new FakeTimeProvider(Now);
        var options = new ExportOptions { MaximumCount = 3, MaximumReservedRows = 3, MaximumActiveJobs = 1, ChunkSize = 3 };
        var exports = await ExportsAsync(time, options);
        var id = (await exports.CreateAsync(3, "quota", default)).Operation!.OperationId;
        Assert.Equal(429, (await exports.CreateAsync(1, "other", default)).FailureStatus);
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        Assert.Equal(429, (await exports.CreateAsync(1, "other", default)).FailureStatus);
        time.Advance(options.Retention);
        Assert.Equal("expired", (await exports.GetAsync(id, default))!.Status);
        Assert.Empty((await exports.ReadAsync(id, 0, 3, default)).Stories);
        var replacement = await exports.CreateAsync(3, "quota", default);
        Assert.NotEqual(id, replacement.Operation!.OperationId);
        await using var rows = Source.CreateCommand("SELECT count(*) FROM hn_export_rows");
        Assert.Equal(0L, await rows.ExecuteScalarAsync());
        time.Advance(options.TombstoneRetention);
        await exports.ProcessNextAsync(default);
        Assert.Null(await exports.GetAsync(id, default));
    }

    [PostgresFact]
    public async Task Export_DeadlineAndOversizedRecord_FailsSafelyWithoutPartialOutput()
    {
        var time = new FakeTimeProvider(Now);
        var options = new ExportOptions { ChunkSize = 1, MaximumResponseBytes = 4096 };
        var exports = await ExportsAsync(time, options);
        var id = (await exports.CreateAsync(3, "deadline", default)).Operation!.OperationId;
        time.Advance(options.JobDeadline);
        await exports.ProcessNextAsync(default);
        var failed = await exports.GetAsync(id, default);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("deadlineExceeded", failed.Error!.Code);
        await ExecuteAsync("UPDATE hn_candidates SET story = jsonb_set(story, '{Title}', to_jsonb(repeat('x', 10000))) WHERE id = 3;");
        var oversized = (await exports.CreateAsync(3, "big", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        failed = await exports.GetAsync(oversized, default);
        Assert.Equal("recordTooLarge", failed!.Error!.Code);
        Assert.Empty((await exports.ReadAsync(oversized, 0, 10, default)).Stories);
        await using var rows = Source.CreateCommand("SELECT count(*) FROM hn_export_rows");
        Assert.Equal(0L, await rows.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Export_InterruptedFreeze_RollsBackAndCanRetry()
    {
        var exports = await ExportsAsync();
        var id = (await exports.CreateAsync(3, "rollback", default)).Operation!.OperationId;
        await ExecuteAsync("""
            CREATE FUNCTION reject_export() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.ordinal = 2 THEN RAISE EXCEPTION 'simulated failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_export BEFORE INSERT ON hn_export_rows FOR EACH ROW EXECUTE FUNCTION reject_export();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => exports.ProcessNextAsync(default));
        Assert.Equal("queued", (await exports.GetAsync(id, default))!.Status);
        await using (var rows = Source.CreateCommand("SELECT count(*) FROM hn_export_rows"))
        {
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        }
        await ExecuteAsync("DROP TRIGGER reject_export ON hn_export_rows;");
        await exports.ProcessNextAsync(default);
        Assert.Equal(3, (await exports.GetAsync(id, default))!.ResultCount);
    }

    [PostgresFact]
    public async Task Export_TiedScores_UsesAscendingStoryIds()
    {
        var exports = await ExportsAsync(options: new ExportOptions { ChunkSize = 3 });
        await ExecuteAsync("""
            UPDATE hn_candidates SET score = 1,
                story = jsonb_set(jsonb_set(story, '{Score}', '1'), '{Title}', to_jsonb(id::text));
            """);
        var id = (await exports.CreateAsync(3, "ties", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        Assert.Equal(["1", "2", "3"], (await exports.ReadAsync(id, 0, 3, default)).Stories.Select(story => story.Title));
    }

    [PostgresFact]
    public async Task Export_NoAvailableRecords_SucceedsWithEmptyResults()
    {
        var exports = await ExportsAsync();
        await ExecuteAsync("DELETE FROM hn_candidates;");
        var id = (await exports.CreateAsync(100, "empty", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        var result = await exports.GetAsync(id, default);
        Assert.Equal("succeeded", result!.Status);
        Assert.Equal(0, result.ResultCount);
        Assert.Empty((await exports.ReadAsync(id, 0, 100, default)).Stories);
    }

    [PostgresFact]
    public async Task Export_ConcurrentProcessing_CommitsEachCheckpointOnce()
    {
        var exports = await ExportsAsync();
        var id = (await exports.CreateAsync(3, "workers", default)).Operation!.OperationId;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => exports.ProcessNextAsync(default)));
        var status = await exports.GetAsync(id, default);
        Assert.Equal("succeeded", status!.Status);
        Assert.Equal(3, status.ProcessedCount);
        Assert.Equal(3, (await exports.ReadAsync(id, 0, 10, default)).Stories.Count);
    }

    [PostgresFact]
    public async Task Export_ApiTraversalAndRestart_RetainsTokenAndStableResults()
    {
        var time = new FakeTimeProvider(Now);
        var exports = await ExportsAsync(time, new ExportOptions { ChunkSize = 3 });
        var id = (await exports.CreateAsync(100, "api", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        string token;
        await using (var api = new ApiFactory { ExportStore = exports, Time = time })
        {
            using var client = api.CreateClient();
            var first = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<ExportPage>(
                client, $"/api/story-exports/{id}/results?pageSize=2");
            Assert.Equal([3, 2], first!.Items.Select(story => story.Score));
            token = first.NextContinuationToken!;
        }
        await ExecuteAsync("DELETE FROM hn_candidates;");
        await using var restarted = new ApiFactory { ExportStore = NewExports(time, new ExportOptions { ChunkSize = 3 }), Time = time };
        using var restartedClient = restarted.CreateClient();
        var last = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<ExportPage>(
            restartedClient, $"/api/story-exports/{id}/results?pageSize=2&continuationToken={token}");
        Assert.Equal(1, Assert.Single(last!.Items).Score);
        Assert.Null(last.NextContinuationToken);
    }

    [PostgresExportScaleFact]
    public async Task Export_MillionSyntheticRecords_UsesBoundedChunksAndPages()
    {
        var options = new ExportOptions { ChunkSize = 10_000, MaximumPageSize = 10_000 };
        var exports = await ExportsAsync(options: options);
        await ExecuteAsync("DELETE FROM hn_candidates;");
        await ExecuteAsync("""
            INSERT INTO hn_candidates(id, story, score, version, processed_version)
            SELECT id, jsonb_build_object('Title', 'Synthetic ' || id, 'Uri', NULL,
                'PostedBy', 'fixture', 'Time', '2026-10-07T00:00:00+00:00',
                'Score', id, 'CommentCount', 0), id, 1, 1 FROM generate_series(1,1000000) AS id;
            """);
        var allocated = GC.GetTotalAllocatedBytes();
        var stopwatch = Stopwatch.StartNew();
        var id = (await exports.CreateAsync(1_000_000, "scale", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        var maximumCheckpointDelta = 0;
        var previous = 0;
        while (await exports.ProcessNextAsync(default))
        {
            var checkpoint = (await exports.GetAsync(id, default))!;
            maximumCheckpointDelta = Math.Max(maximumCheckpointDelta, checkpoint.ProcessedCount - previous);
            previous = checkpoint.ProcessedCount;
        }
        var completed = await exports.GetAsync(id, default);
        Assert.Equal("succeeded", completed!.Status);
        Assert.Equal(1_000_000, completed.ResultCount);
        Assert.Equal(options.ChunkSize, maximumCheckpointDelta);
        var page = await exports.ReadAsync(id, 0, 1000, default);
        Assert.Equal(1000, page.Stories.Count);
        Assert.Equal(1_000_000, page.Stories[0].Score);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new ExportPage(id, page.Stories, null), ExportJson.Options).Length < options.MaximumResponseBytes);
        Console.WriteLine($"Synthetic million-row freeze/validation: {stopwatch.Elapsed}; total managed allocations: {GC.GetTotalAllocatedBytes() - allocated:N0} bytes; chunk ceiling: {maximumCheckpointDelta}; page rows: {page.Stories.Count}.");
    }

    [PostgresFact]
    public async Task Export_ConcurrentReadsAndCleanup_NeverExposesPartialPages()
    {
        var time = new FakeTimeProvider(Now);
        var options = new ExportOptions { ChunkSize = 3 };
        var exports = await ExportsAsync(time, options);
        var id = (await exports.CreateAsync(3, "cleanup-race", default)).Operation!.OperationId;
        await exports.ProcessNextAsync(default);
        await exports.ProcessNextAsync(default);
        var start = ServiceHarness.Gate();
        var reads = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.Task;
            return await exports.ReadAsync(id, 0, 3, default);
        }).ToArray();
        var cleanup = Task.Run(async () =>
        {
            await start.Task;
            time.Advance(options.Retention);
            await exports.ProcessNextAsync(default);
        });
        start.SetResult();
        var pages = await Task.WhenAll(reads);
        await cleanup;
        Assert.All(pages, page =>
        {
            if (page.Operation!.Status == "succeeded")
            {
                Assert.Equal(3, page.Stories.Count);
            }
            else
            {
                Assert.Equal("expired", page.Operation.Status);
                Assert.Empty(page.Stories);
            }
        });
        Assert.Empty((await exports.ReadAsync(id, 0, 3, default)).Stories);
    }
}
