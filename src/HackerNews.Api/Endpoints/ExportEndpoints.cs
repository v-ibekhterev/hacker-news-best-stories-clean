using System.Globalization;
using System.Text.Json;
using HackerNews.Api.Exports;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HackerNews.Api.Endpoints;

public static class ExportEndpoints
{
    public static void MapExports(this WebApplication app)
    {
        var group = app.MapGroup("/api/story-exports");
        group.MapPost("", CreateAsync).WithName("CreateStoryExport")
            .WithSummary("Queue an export of the persisted dataset")
            .Accepts<ExportRequest>("application/json")
            .Produces<ExportAccepted>(202).ProducesProblem(400).ProducesProblem(409)
            .ProducesProblem(415).ProducesProblem(429).ProducesProblem(503);
        group.MapGet("/{id:guid}", StatusAsync).WithName("GetStoryExport")
            .Produces<ExportStatus>().ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
        group.MapGet("/{id:guid}/results", ResultsAsync).WithName("GetStoryExportResults")
            .Produces<ExportPage>().ProducesProblem(400).ProducesProblem(404)
            .ProducesProblem(409).ProducesProblem(410).ProducesProblem(503);
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, HttpResponse response,
        IExportStore store, IOptions<ExportOptions> options, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return Results.Problem(statusCode: 415, title: "JSON content required");
        }
        var keys = request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || keys[0] is not { Length: > 0 and <= 128 } key ||
            key.Any(character => character < '!' || character > '~'))
        {
            return Results.Problem(statusCode: 400, title: "Invalid idempotency key",
                detail: "Supply one Idempotency-Key header containing 1..128 printable ASCII characters without spaces.");
        }
        ExportRequest? input;
        try
        {
            input = await request.ReadFromJsonAsync<ExportRequest>(ExportJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid export request");
        }
        if (input is null || input.Count < 1 || input.Count > options.Value.MaximumCount)
        {
            return Results.Problem(statusCode: 400, title: "Invalid export count",
                detail: $"Supply an integer count between 1 and {options.Value.MaximumCount}.");
        }
        try
        {
            var creation = await store.CreateAsync(input.Count, key, cancellationToken);
            if (creation.FailureStatus is { } failure)
            {
                if (failure == 429)
                {
                    response.Headers.RetryAfter = "5";
                }
                return Results.Problem(statusCode: failure, title: failure == 409
                    ? "Idempotency key conflicts with a different count" : "Export capacity unavailable");
            }
            var operation = creation.Operation ?? throw new InvalidOperationException("Export creation returned no operation.");
            response.Headers.RetryAfter = "5";
            var path = $"/api/story-exports/{operation.OperationId}";
            return Results.Accepted(path, new ExportAccepted(operation.OperationId, operation.Status, path));
        }
        catch (NpgsqlException exception)
        {
            logging.CreateLogger("ExportEndpoints").LogError(exception, "Unable to persist export request");
            return Unavailable();
        }
    }

    private static async Task<IResult> StatusAsync(Guid id, IExportStore store, ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await store.GetAsync(id, cancellationToken);
            return operation is null ? NotFound()
                : operation.Status == "expired" ? Expired() : Results.Ok(operation);
        }
        catch (NpgsqlException exception)
        {
            logging.CreateLogger("ExportEndpoints").LogError(exception, "Unable to read export {OperationId}", id);
            return Unavailable();
        }
    }

    private static async Task<IResult> ResultsAsync(Guid id, HttpRequest request, IExportStore store,
        ExportTokens tokens, IOptions<ExportOptions> options, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var sizes = request.Query["pageSize"];
        var size = Math.Min(100, settings.MaximumPageSize);
        if (sizes.Count > 0 && (sizes.Count != 1 ||
            !int.TryParse(sizes[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
            size < 1 || size > settings.MaximumPageSize))
        {
            return Results.Problem(statusCode: 400, title: "Invalid page size",
                detail: $"Supply one pageSize between 1 and {settings.MaximumPageSize}.");
        }
        var values = request.Query["continuationToken"];
        ExportCursor? cursor = null;
        if (values.Count > 0)
        {
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]) ||
                (cursor = tokens.Unprotect(values[0]!, id, size)) is null)
            {
                return InvalidToken();
            }
        }
        try
        {
            var rows = await store.ReadAsync(id, cursor?.Position ?? 0, size, cancellationToken);
            var operation = rows.Operation;
            if (operation is null)
            {
                return NotFound();
            }
            if (operation.Status == "expired")
            {
                return Expired();
            }
            if (operation.Status != "succeeded")
            {
                return Results.Problem(statusCode: 409, title: "Export results are not available",
                    detail: "Read the operation status; only succeeded operations expose results.");
            }
            if (cursor is not null && (cursor.Generation != operation.DatasetGeneration ||
                cursor.Position >= operation.ResultCount || cursor.ExpiresUnixSeconds != operation.ExpiresAt!.Value.ToUnixTimeSeconds()))
            {
                return InvalidToken();
            }
            var items = rows.Stories.ToList();
            if (items.Count == 0 && operation.ResultCount > (cursor?.Position ?? 0))
            {
                logging.CreateLogger("ExportEndpoints").LogError("Export {OperationId} has no readable next row within the response budget", id);
                return Results.Problem(statusCode: 503, title: "Export result rows unavailable");
            }
            byte[] Serialize(int count)
            {
                var position = (cursor?.Position ?? 0) + count;
                var next = position < operation.ResultCount
                    ? tokens.Protect(new(1, id, operation.DatasetGeneration!.Value, position, size,
                        operation.ExpiresAt!.Value.ToUnixTimeSeconds()))
                    : null;
                return JsonSerializer.SerializeToUtf8Bytes(new ExportPage(id, items.Take(count).ToArray(), next), ExportJson.Options);
            }
            var bytes = Serialize(items.Count);
            if (bytes.Length <= settings.MaximumResponseBytes)
            {
                return Results.Bytes(bytes, "application/json");
            }
            byte[]? best = null;
            var low = 1;
            var high = items.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = Serialize(middle);
                if (candidate.Length <= settings.MaximumResponseBytes)
                {
                    best = candidate;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }
            if (best is not null)
            {
                return Results.Bytes(best, "application/json");
            }
            logging.CreateLogger("ExportEndpoints").LogError("Export {OperationId} cannot fit one record within the response budget", id);
            return Results.Problem(statusCode: 503, title: "Result response exceeds the configured byte budget");
        }
        catch (NpgsqlException exception)
        {
            logging.CreateLogger("ExportEndpoints").LogError(exception, "Unable to read export results {OperationId}", id);
            return Unavailable();
        }
    }

    private static IResult InvalidToken() => Results.Problem(statusCode: 400, title: "Invalid or expired continuation token");
    private static IResult NotFound() => Results.Problem(statusCode: 404, title: "Export not found");
    private static IResult Expired() => Results.Problem(statusCode: 410, title: "Export expired");
    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Export storage unavailable");
}
