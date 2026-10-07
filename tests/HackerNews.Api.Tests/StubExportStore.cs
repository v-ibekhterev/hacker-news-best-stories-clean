using HackerNews.Api.Exports;
using HackerNews.Api.Models;

namespace HackerNews.Api.Tests;

internal sealed class StubExportStore : IExportStore
{
    public ExportStatus? Operation { get; set; }
    public IReadOnlyList<StoryResponse> Stories { get; set; } = [];
    public int? CreationFailure { get; set; }
    public int CreatedCount { get; private set; }
    public Func<CancellationToken, Task<bool>> Process { get; set; } = _ => Task.FromResult(false);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<byte[]> GetSigningKeyAsync(CancellationToken cancellationToken) => Task.FromResult(new byte[32]);
    public Task<bool> ProcessNextAsync(CancellationToken cancellationToken) => Process(cancellationToken);

    public Task<ExportCreation> CreateAsync(int count, string idempotencyKey, CancellationToken cancellationToken)
    {
        CreatedCount++;
        var now = DateTimeOffset.UtcNow;
        Operation ??= new(Guid.NewGuid(), "queued", count, 0, null, now, now, null, null, null, null);
        return Task.FromResult(new ExportCreation(CreationFailure is null ? Operation : null, CreationFailure));
    }

    public Task<ExportStatus?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Operation?.OperationId == id ? Operation : null);

    public Task<ExportRows> ReadAsync(Guid id, int position, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(new ExportRows(Operation?.OperationId == id ? Operation : null,
            Stories.Skip(position).Take(pageSize).ToArray()));
}
