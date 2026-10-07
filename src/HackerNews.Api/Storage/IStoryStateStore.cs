using HackerNews.Api.Models;
using HackerNews.Api.Services;

namespace HackerNews.Api.Storage;

public sealed record PendingStory(long Id, long Version);
public sealed record StoryBatch(IReadOnlyList<PendingStory> Items, bool MembershipChanged);
public sealed record StoryFetchResult(PendingStory Work, StoryResponse? Story, bool Succeeded);

// Acquisition-specific operations, not a general repository abstraction.
public interface IStoryStateStore
{
    Task<StorySnapshot?> InitializeAsync(string source, int maximumCount, CancellationToken cancellationToken);
    Task MarkUpdatedAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);
    Task<StoryBatch> PrepareBatchAsync(long[] membership, DateTimeOffset now,
        TimeSpan reconciliationInterval, int batchSize, bool forceRefresh, CancellationToken cancellationToken);
    Task<StorySnapshot?> CommitBatchAsync(StoryBatch batch, IReadOnlyCollection<StoryFetchResult> results,
        DateTimeOffset now, int maximumCount, CancellationToken cancellationToken);
}
