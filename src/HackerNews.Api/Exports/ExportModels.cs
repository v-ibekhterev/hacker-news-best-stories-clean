using HackerNews.Api.Models;

namespace HackerNews.Api.Exports;

public sealed record ExportRequest(int Count);
public sealed record ExportAccepted(Guid OperationId, string Status, string StatusPath);
public sealed record ExportError(string Code, string Message);
public sealed record ExportStatus(Guid OperationId, string Status, int RequestedCount,
    int ProcessedCount, int? ResultCount, DateTimeOffset CreatedAt, DateTimeOffset LastUpdatedAt,
    DateTimeOffset? ExpiresAt, long? DatasetGeneration, string? ResultsPath, ExportError? Error);
public sealed record ExportPage(Guid OperationId, IReadOnlyList<StoryResponse> Items, string? NextContinuationToken);
public sealed record ExportCreation(ExportStatus? Operation, int? FailureStatus = null);
public sealed record ExportRows(ExportStatus? Operation, IReadOnlyList<StoryResponse> Stories);
public sealed record ExportCursor(int Version, Guid OperationId, long Generation, int Position,
    int PageSize, long ExpiresUnixSeconds);

public interface IExportStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<byte[]> GetSigningKeyAsync(CancellationToken cancellationToken);
    Task<ExportCreation> CreateAsync(int count, string idempotencyKey, CancellationToken cancellationToken);
    Task<ExportStatus?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ExportRows> ReadAsync(Guid id, int position, int pageSize, CancellationToken cancellationToken);
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}
