using System.Collections.Immutable;
using System.Text.Json;
using HackerNews.Api.Models;
using HackerNews.Api.Services;
using Npgsql;
using NpgsqlTypes;

namespace HackerNews.Api.Storage;

public sealed class PostgresStoryStateStore(NpgsqlDataSource dataSource) : IStoryStateStore, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private NpgsqlConnection? owner;
    private bool initialized;
    private const long OwnerLock = 684_103_827;

    public async Task<StorySnapshot?> InitializeAsync(string source, int maximumCount, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return await ReadSnapshotAsync(Connection, maximumCount, cancellationToken);
            }
            owner = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var acquire = Command("SELECT pg_try_advisory_lock($1)", owner, OwnerLock);
            if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException("Another ingestion instance already owns this database.");
            }
            await using var transaction = await owner.BeginTransactionAsync(cancellationToken);
            await using (var schema = Command("""
                CREATE TABLE IF NOT EXISTS hn_state (
                    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                    schema_version integer NOT NULL,
                    source text NOT NULL,
                    reconciliation_at timestamptz,
                    generation bigint NOT NULL DEFAULT 0,
                    snapshot jsonb
                );
                """, owner))
            {
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insert = Command("""
                INSERT INTO hn_state (singleton, schema_version, source)
                VALUES (true, 1, $1) ON CONFLICT DO NOTHING;
                """, owner, source))
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var identity = Command("SELECT schema_version, source FROM hn_state WHERE singleton;", owner))
            await using (var reader = await identity.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) is not (1 or 2) || reader.GetString(1) != source)
                {
                    throw new InvalidOperationException("Persisted Hacker News schema or source identity is incompatible.");
                }
            }
            await using (var migration = Command("""
                CREATE SEQUENCE IF NOT EXISTS hn_work_order;
                CREATE TABLE IF NOT EXISTS hn_candidates (
                    id bigint PRIMARY KEY CHECK (id > 0),
                    story jsonb,
                    score integer,
                    acquired_at timestamptz,
                    version bigint NOT NULL DEFAULT 1,
                    processed_version bigint NOT NULL DEFAULT 0,
                    work_order bigint DEFAULT nextval('hn_work_order'),
                    CHECK ((story IS NULL) = (score IS NULL))
                );
                CREATE INDEX IF NOT EXISTS hn_ranking ON hn_candidates (score DESC, id ASC)
                    WHERE story IS NOT NULL;
                CREATE INDEX IF NOT EXISTS hn_pending ON hn_candidates (work_order, id)
                    WHERE version > processed_version;
                ALTER TABLE hn_state ADD COLUMN IF NOT EXISTS generation bigint NOT NULL DEFAULT 0;
                UPDATE hn_state SET schema_version = 2 WHERE singleton AND schema_version = 1;
                """, owner))
            {
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            initialized = true;
            return await ReadSnapshotAsync(owner, maximumCount, cancellationToken);
        }
        catch
        {
            if (!initialized && owner is not null)
            {
                await owner.DisposeAsync();
                owner = null;
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkUpdatedAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = Command("""
                UPDATE hn_candidates
                SET version = version + 1, work_order = COALESCE(work_order, nextval('hn_work_order'))
                WHERE id = ANY($1);
                """, Connection, ids.Distinct().ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoryBatch> PrepareBatchAsync(long[] membership, DateTimeOffset now,
        TimeSpan reconciliationInterval, int batchSize, bool forceRefresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = Connection;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var delete = Command("DELETE FROM hn_candidates WHERE NOT (id = ANY($1));", connection, membership);
            var deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
            await using var add = Command("""
                INSERT INTO hn_candidates (id) SELECT DISTINCT unnest($1::bigint[])
                ON CONFLICT DO NOTHING;
                """, connection, membership);
            var added = await add.ExecuteNonQueryAsync(cancellationToken);
            await using var reconcile = Command("""
                UPDATE hn_state SET reconciliation_at = $1
                WHERE singleton AND ($2 OR reconciliation_at IS NULL OR reconciliation_at <= $3)
                RETURNING singleton;
                """, connection, now, forceRefresh, now - reconciliationInterval);
            if (await reconcile.ExecuteScalarAsync(cancellationToken) is true)
            {
                await using var queue = Command("""
                    UPDATE hn_candidates SET version = version + 1, work_order = nextval('hn_work_order')
                    WHERE version = processed_version;
                    """, connection);
                await queue.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var select = Command("""
                SELECT id, version FROM hn_candidates WHERE version > processed_version
                ORDER BY work_order, id LIMIT $1;
                """, connection, batchSize);
            var work = new List<PendingStory>();
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    work.Add(new PendingStory(reader.GetInt64(0), reader.GetInt64(1)));
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return new StoryBatch(work, added > 0 || deleted > 0);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StorySnapshot?> CommitBatchAsync(StoryBatch batch, IReadOnlyCollection<StoryFetchResult> results,
        DateTimeOffset now, int maximumCount, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = Connection;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var updates = new NpgsqlBatch(connection, transaction);
            foreach (var result in results)
            {
                var update = new NpgsqlBatchCommand(result.Succeeded
                    ? """
                      UPDATE hn_candidates SET story = $1::jsonb, score = $2, acquired_at = $3,
                          processed_version = $4,
                          work_order = CASE WHEN version = $4 THEN NULL ELSE nextval('hn_work_order') END
                      WHERE id = $5;
                      """
                    : "UPDATE hn_candidates SET work_order = nextval('hn_work_order') WHERE id = $1;");
                if (result.Succeeded)
                {
                    update.Parameters.Add(new NpgsqlParameter { Value = result.Story is null ? DBNull.Value : JsonSerializer.Serialize(result.Story), NpgsqlDbType = NpgsqlDbType.Text });
                    update.Parameters.Add(new NpgsqlParameter { Value = result.Story is null ? DBNull.Value : result.Story.Score, NpgsqlDbType = NpgsqlDbType.Integer });
                    update.Parameters.Add(new NpgsqlParameter { Value = now });
                    update.Parameters.Add(new NpgsqlParameter { Value = result.Work.Version });
                }
                update.Parameters.Add(new NpgsqlParameter { Value = result.Work.Id });
                updates.BatchCommands.Add(update);
            }
            if (updates.BatchCommands.Count > 0)
            {
                await updates.ExecuteNonQueryAsync(cancellationToken);
            }

            StorySnapshot? snapshot;
            if (batch.MembershipChanged || results.Any(result => result.Succeeded))
            {
                var stories = await ReadTopAsync(connection, maximumCount, cancellationToken);
                if (stories.Length > 0)
                {
                    snapshot = new StorySnapshot(stories, now);
                    await using var publish = Command("UPDATE hn_state SET snapshot = $1::jsonb, generation = generation + 1 WHERE singleton;",
                        connection, JsonSerializer.Serialize(snapshot));
                    await publish.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    snapshot = await ReadSnapshotAsync(connection, maximumCount, cancellationToken);
                }
            }
            else
            {
                snapshot = await ReadSnapshotAsync(connection, maximumCount, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private NpgsqlConnection Connection => initialized && owner?.State == System.Data.ConnectionState.Open
        ? owner
        : throw new InvalidOperationException("The PostgreSQL ingestion-owner connection is not available.");

    private static async Task<StorySnapshot?> ReadSnapshotAsync(NpgsqlConnection connection, int maximumCount,
        CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT snapshot::text FROM hn_state WHERE singleton;", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }
        var snapshot = JsonSerializer.Deserialize<StorySnapshot>((string)value)
            ?? throw new JsonException("The durable snapshot is invalid.");
        if (snapshot.Stories.IsDefaultOrEmpty || snapshot.Stories.Any(story =>
            story is null || string.IsNullOrWhiteSpace(story.Title) || string.IsNullOrWhiteSpace(story.PostedBy)))
        {
            throw new JsonException("The durable serving snapshot must contain valid stories.");
        }
        // A higher count must not silently serve fewer rows just because a previous process cached fewer.
        var top = await ReadTopAsync(connection, maximumCount, cancellationToken);
        return new StorySnapshot(top.IsEmpty ? snapshot.Stories.Take(maximumCount).ToImmutableArray() : top, snapshot.LoadedAt);
    }

    private static async Task<ImmutableArray<StoryResponse>> ReadTopAsync(NpgsqlConnection connection, int maximumCount,
        CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT story::text FROM hn_candidates WHERE story IS NOT NULL
            ORDER BY score DESC, id ASC LIMIT $1;
            """, connection, maximumCount);
        var stories = ImmutableArray.CreateBuilder<StoryResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var story = JsonSerializer.Deserialize<StoryResponse>(reader.GetString(0));
            if (story is null || string.IsNullOrWhiteSpace(story.Title) || string.IsNullOrWhiteSpace(story.PostedBy))
            {
                throw new JsonException("A persisted story is invalid.");
            }
            stories.Add(story);
        }
        return stories.ToImmutable();
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        return command;
    }

    public async ValueTask DisposeAsync()
    {
        if (owner is not null)
        {
            try
            {
                if (owner.State == System.Data.ConnectionState.Open)
                {
                    await using var unlock = Command("SELECT pg_advisory_unlock($1)", owner, OwnerLock);
                    await unlock.ExecuteScalarAsync();
                }
            }
            finally
            {
                await owner.DisposeAsync();
            }
        }
        gate.Dispose();
    }
}
