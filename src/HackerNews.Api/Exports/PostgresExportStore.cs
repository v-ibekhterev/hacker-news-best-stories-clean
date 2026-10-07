using System.Security.Cryptography;
using System.Text.Json;
using HackerNews.Api.Models;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HackerNews.Api.Exports;

public sealed class PostgresExportStore(NpgsqlDataSource dataSource, IOptions<ExportOptions> options,
    TimeProvider timeProvider, ILogger<PostgresExportStore> logger) : IExportStore
{
    private const long QueueLock = 684_103_828;
    private const int ResponseEnvelopeBytes = 2048;
    private readonly ExportOptions settings = options.Value;
    private const string Columns = """
        id, status, requested_count, processed_count, result_count, created_at, updated_at,
        expires_at, dataset_generation, error_code
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, cancellationToken);
        await using var schema = Command("""
            CREATE TABLE IF NOT EXISTS hn_export_settings (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                schema_version integer NOT NULL,
                signing_key bytea NOT NULL CHECK (octet_length(signing_key) = 32)
            );
            CREATE TABLE IF NOT EXISTS hn_exports (
                id uuid PRIMARY KEY,
                idempotency_key text UNIQUE,
                status text NOT NULL CHECK (status IN ('queued','inProgress','succeeded','failed','expired')),
                requested_count integer NOT NULL CHECK (requested_count > 0),
                processed_count integer NOT NULL DEFAULT 0,
                result_count integer,
                reserved_rows bigint NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                deadline_at timestamptz NOT NULL,
                expires_at timestamptz,
                dataset_generation bigint,
                error_code text
            );
            CREATE INDEX IF NOT EXISTS hn_export_queue ON hn_exports(created_at, id)
                WHERE status IN ('queued','inProgress');
            CREATE TABLE IF NOT EXISTS hn_export_rows (
                operation_id uuid NOT NULL REFERENCES hn_exports(id) ON DELETE CASCADE,
                ordinal integer NOT NULL,
                story jsonb NOT NULL,
                PRIMARY KEY (operation_id, ordinal)
            );
            """, connection);
        await schema.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = Command("""
            INSERT INTO hn_export_settings(singleton, schema_version, signing_key)
            VALUES (true, 1, $1) ON CONFLICT DO NOTHING;
            """, connection, RandomNumberGenerator.GetBytes(32));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await using var version = Command("SELECT schema_version FROM hn_export_settings WHERE singleton;", connection);
        if (await version.ExecuteScalarAsync(cancellationToken) is not 1)
        {
            throw new InvalidOperationException("The persisted export schema is incompatible.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<byte[]> GetSigningKeyAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT signing_key FROM hn_export_settings WHERE singleton;");
        var key = await command.ExecuteScalarAsync(cancellationToken);
        return key is byte[] { Length: 32 } bytes
            ? bytes : throw new InvalidOperationException("The persisted export signing key is invalid.");
    }

    public async Task<ExportCreation> CreateAsync(int count, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, cancellationToken);
        await CleanupAsync(connection, cancellationToken);

        var prior = await FindByIdempotencyKeyAsync(connection, idempotencyKey, cancellationToken);

        if (prior is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return prior.RequestedCount == count ? new(prior) : new(null, 409);
        }
        if (!await HasCapacityAsync(connection, count, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(null, 429);
        }

        var created = await InsertOperationAsync(connection, count, idempotencyKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(created);
    }

    private static async Task<ExportStatus?> FindByIdempotencyKeyAsync(NpgsqlConnection connection,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = Command(
            $"SELECT {Columns} FROM hn_exports WHERE idempotency_key = $1;", connection, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadStatus(reader) : null;
    }

    private async Task<bool> HasCapacityAsync(NpgsqlConnection connection, int count, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT count(*) FILTER (WHERE status IN ('queued','inProgress')),
                COALESCE(sum(reserved_rows), 0)::bigint FROM hn_exports;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return reader.GetInt64(0) < settings.MaximumActiveJobs &&
            reader.GetInt64(1) <= settings.MaximumReservedRows - count;
    }

    private async Task<ExportStatus> InsertOperationAsync(NpgsqlConnection connection, int count,
        string idempotencyKey, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await using var create = Command($"""
            INSERT INTO hn_exports(id, idempotency_key, status, requested_count, reserved_rows,
                created_at, updated_at, deadline_at)
            VALUES ($1, $2, 'queued', $3, $3, $4, $4, $5) RETURNING {Columns};
            """, connection, id, idempotencyKey, count, now, now + settings.JobDeadline);
        await using var reader = await create.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return ReadStatus(reader);
    }

    public async Task<ExportStatus?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAsync(connection, id, cancellationToken);
    }

    public async Task<ExportRows> ReadAsync(Guid id, int position, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var operation = await GetAsync(connection, id, cancellationToken);
        var stories = new List<StoryResponse>();
        if (operation?.Status == "succeeded")
        {
            await using var command = Command("""
                SELECT story::text FROM (
                    SELECT ordinal, story, sum(octet_length(story::text) + 1) OVER (ORDER BY ordinal) AS bytes
                    FROM (
                        SELECT ordinal, story FROM hn_export_rows WHERE operation_id = $1 AND ordinal > $2
                        ORDER BY ordinal LIMIT $3
                    ) bounded
                ) sized WHERE bytes <= $4 ORDER BY ordinal;
                """, connection, id, position, pageSize, settings.MaximumResponseBytes - ResponseEnvelopeBytes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stories.Add(ParseStory(reader.GetString(0)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(operation, stories);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, cancellationToken);
        await CleanupAsync(connection, cancellationToken);
        var operation = await FindNextOperationAsync(connection, cancellationToken);

        if (operation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var now = timeProvider.GetUtcNow();

        if (operation.Status == "queued")
        {
            await FreezeResultsAsync(connection, operation, now, cancellationToken);
        }
        else
        {
            await ValidateNextChunkAsync(connection, operation, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<ExportStatus?> FindNextOperationAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = Command($"""
            SELECT {Columns} FROM hn_exports WHERE status IN ('queued','inProgress')
            ORDER BY created_at, id LIMIT 1 FOR UPDATE;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) ? ReadStatus(reader) : null;
    }

    private async Task FreezeResultsAsync(NpgsqlConnection connection, ExportStatus operation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var freeze = Command("""
                WITH selected AS MATERIALIZED (
                    SELECT id, story, score FROM hn_candidates WHERE story IS NOT NULL
                    ORDER BY score DESC, id ASC LIMIT $2
                ), copied AS (
                    INSERT INTO hn_export_rows(operation_id, ordinal, story)
                    SELECT $1, row_number() OVER (ORDER BY score DESC, id ASC)::integer, story FROM selected
                    RETURNING ordinal
                )
                UPDATE hn_exports SET status = 'inProgress', result_count = (SELECT count(*) FROM copied),
                    dataset_generation = (SELECT generation FROM hn_state WHERE singleton), updated_at = $3
                WHERE id = $1;
                """, connection, operation.OperationId, operation.RequestedCount, now);
        freeze.CommandTimeout = (int)Math.Ceiling(settings.JobDeadline.TotalSeconds);
        await freeze.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateNextChunkAsync(NpgsqlConnection connection, ExportStatus operation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (processed, failure) = await ReadValidatedChunkAsync(connection, operation, cancellationToken);

        if (failure is not null)
        {
            logger.LogWarning("Export {OperationId} failed: {ErrorCode}", operation.OperationId, failure);
            await FailAsync(connection, operation.OperationId, failure, cancellationToken);
            return;
        }

        await SaveCheckpointAsync(connection, operation, processed, now, cancellationToken);
    }

    private async Task<(int Processed, string? Failure)> ReadValidatedChunkAsync(NpgsqlConnection connection,
        ExportStatus operation, CancellationToken cancellationToken)
    {
        var processed = operation.ProcessedCount;
        await using var chunk = Command("""
                SELECT CASE WHEN octet_length(story::text) <= $4 THEN story::text ELSE NULL END
                FROM hn_export_rows WHERE operation_id = $1 AND ordinal > $2
                ORDER BY ordinal LIMIT $3;
                """, connection, operation.OperationId, processed, settings.ChunkSize,
                settings.MaximumResponseBytes - ResponseEnvelopeBytes);
        await using var reader = await chunk.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var failure = ValidateResultRow(reader, operation.OperationId);

            if (failure is not null)
            {
                return (processed, failure);
            }

            processed++;
        }

        return (processed, null);
    }

    private string? ValidateResultRow(NpgsqlDataReader reader, Guid operationId)
    {
        if (reader.IsDBNull(0))
        {
            return "recordTooLarge";
        }

        try
        {
            var story = ParseStory(reader.GetString(0));
            var serializedSize = JsonSerializer.SerializeToUtf8Bytes(story, ExportJson.Options).Length;

            return serializedSize + ResponseEnvelopeBytes > settings.MaximumResponseBytes
                ? "recordTooLarge"
                : null;
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Export {OperationId} contains invalid persisted data", operationId);
            return "invalidDataset";
        }
    }

    private async Task SaveCheckpointAsync(NpgsqlConnection connection, ExportStatus operation, int processed,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var done = processed == operation.ResultCount;
        await using var update = Command("""
                    UPDATE hn_exports SET processed_count = $2, updated_at = $3,
                        status = CASE WHEN $4 THEN 'succeeded' ELSE 'inProgress' END,
                        expires_at = CASE WHEN $4 THEN $5 ELSE NULL END,
                        reserved_rows = CASE WHEN $4 THEN result_count ELSE reserved_rows END
                    WHERE id = $1;
                    """, connection, operation.OperationId, processed, now, done, now + settings.Retention);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CleanupAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var deadlines = Command("""
            UPDATE hn_exports SET status = 'failed', error_code = 'deadlineExceeded',
                expires_at = $2, updated_at = $1, reserved_rows = 0
            WHERE status IN ('queued','inProgress') AND deadline_at <= $1;
            """, connection, now, now + settings.Retention);
        var timedOut = await deadlines.ExecuteNonQueryAsync(cancellationToken);
        if (timedOut > 0)
        {
            logger.LogWarning("{ExportCount} exports exceeded their processing deadline", timedOut);
        }
        await using var expire = Command("""
            UPDATE hn_exports SET status = 'expired', idempotency_key = NULL, reserved_rows = 0, updated_at = $1
            WHERE status IN ('succeeded','failed') AND expires_at <= $1;
            """, connection, now);
        await expire.ExecuteNonQueryAsync(cancellationToken);
        await using var removeRows = Command("""
            DELETE FROM hn_export_rows WHERE operation_id IN
                (SELECT id FROM hn_exports WHERE status IN ('expired','failed'));
            """, connection);
        await removeRows.ExecuteNonQueryAsync(cancellationToken);
        await using var tombstones = Command("DELETE FROM hn_exports WHERE status = 'expired' AND expires_at <= $1;",
            connection, now - settings.TombstoneRetention);
        await tombstones.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailAsync(NpgsqlConnection connection, Guid id, string code, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var fail = Command("""
            UPDATE hn_exports SET status = 'failed', error_code = $2, expires_at = $3,
                updated_at = $4, reserved_rows = 0 WHERE id = $1;
            """, connection, id, code, now + settings.Retention, now);
        await fail.ExecuteNonQueryAsync(cancellationToken);
        await using var delete = Command("DELETE FROM hn_export_rows WHERE operation_id = $1;", connection, id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ExportStatus?> GetAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command($"SELECT {Columns} FROM hn_exports WHERE id = $1;", connection, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var operation = ReadStatus(reader);
        if (operation.ExpiresAt is { } expiration && expiration <= timeProvider.GetUtcNow())
        {
            return expiration + settings.TombstoneRetention <= timeProvider.GetUtcNow()
                ? null : operation with { Status = "expired", ResultsPath = null };
        }
        return operation;
    }

    private static ExportStatus ReadStatus(NpgsqlDataReader reader)
    {
        var id = reader.GetGuid(0);
        var status = reader.GetString(1);
        var code = reader.IsDBNull(9) ? null : reader.GetString(9);
        return new ExportStatus(
            OperationId: id,
            Status: status,
            RequestedCount: reader.GetInt32(2),
            ProcessedCount: reader.GetInt32(3),
            ResultCount: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(5),
            LastUpdatedAt: reader.GetFieldValue<DateTimeOffset>(6),
            ExpiresAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            DatasetGeneration: reader.IsDBNull(8) ? null : reader.GetInt64(8),
            ResultsPath: status == "succeeded" ? $"/api/story-exports/{id}/results" : null,
            Error: CreateError(code));
    }

    private static ExportError? CreateError(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var message = code switch
        {
            "deadlineExceeded" => "The export exceeded its processing deadline.",
            "recordTooLarge" => "A story exceeds the configured result-response size budget.",
            _ => "The persisted dataset contains an invalid story."
        };

        return new ExportError(code, message);
    }

    private static StoryResponse ParseStory(string json)
    {
        var story = JsonSerializer.Deserialize<StoryResponse>(json);
        return story is not null && !string.IsNullOrWhiteSpace(story.Title) && !string.IsNullOrWhiteSpace(story.PostedBy)
            ? story : throw new JsonException("Invalid exported story.");
    }

    private static async Task LockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT pg_advisory_xact_lock($1);", connection, QueueLock);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        return command;
    }
}

public static class ExportJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };
}
