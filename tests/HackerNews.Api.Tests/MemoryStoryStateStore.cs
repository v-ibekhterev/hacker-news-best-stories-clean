using System.Collections.Immutable;
using HackerNews.Api.Models;
using HackerNews.Api.Services;
using HackerNews.Api.Storage;

namespace HackerNews.Api.Tests;

// Test-only transactional model; production always uses PostgreSQL.
internal sealed class MemoryStoryStateStore : IStoryStateStore
{
    private readonly object gate = new();
    private readonly Dictionary<long, Entry> entries = new();
    private long nextOrder;
    private DateTimeOffset? reconciledAt;
    private StorySnapshot? snapshot;

    private sealed class Entry
    {
        public long Version = 1;
        public long Processed;
        public long Order;
        public StoryResponse? Story;
    }

    public Task<StorySnapshot?> InitializeAsync(string source, int maximumCount, CancellationToken cancellationToken) =>
        Task.FromResult(snapshot);

    public Task MarkUpdatedAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var id in ids.Distinct())
            {
                if (entries.TryGetValue(id, out var entry))
                {
                    if (entry.Version == entry.Processed)
                    {
                        entry.Order = ++nextOrder;
                    }
                    entry.Version++;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<StoryBatch> PrepareBatchAsync(long[] membership, DateTimeOffset now,
        TimeSpan reconciliationInterval, int batchSize, bool forceRefresh, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var changed = false;
            var ids = membership.ToHashSet();
            foreach (var id in entries.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                entries.Remove(id);
                changed = true;
            }
            foreach (var id in ids)
            {
                if (entries.TryAdd(id, new Entry { Order = ++nextOrder }))
                {
                    changed = true;
                }
            }
            if (forceRefresh || reconciledAt is null || now - reconciledAt >= reconciliationInterval)
            {
                foreach (var entry in entries.Values.Where(entry => entry.Version == entry.Processed))
                {
                    entry.Version++;
                    entry.Order = ++nextOrder;
                }
                reconciledAt = now;
            }
            IReadOnlyList<PendingStory> work = entries.Where(entry => entry.Value.Version > entry.Value.Processed)
                .OrderBy(entry => entry.Value.Order).ThenBy(entry => entry.Key).Take(batchSize)
                .Select(entry => new PendingStory(entry.Key, entry.Value.Version)).ToArray();
            return Task.FromResult(new StoryBatch(work, changed));
        }
    }

    public Task<StorySnapshot?> CommitBatchAsync(StoryBatch batch, IReadOnlyCollection<StoryFetchResult> results,
        DateTimeOffset now, int maximumCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var result in results)
            {
                if (!entries.TryGetValue(result.Work.Id, out var entry))
                {
                    continue;
                }
                if (result.Succeeded)
                {
                    entry.Story = result.Story;
                    entry.Processed = result.Work.Version;
                }
                if (entry.Version > entry.Processed)
                {
                    entry.Order = ++nextOrder;
                }
            }
            if (batch.MembershipChanged || results.Any(result => result.Succeeded))
            {
                var top = entries.Where(entry => entry.Value.Story is not null)
                    .OrderByDescending(entry => entry.Value.Story!.Score).ThenBy(entry => entry.Key)
                    .Take(maximumCount).Select(entry => entry.Value.Story!).ToImmutableArray();
                if (!top.IsEmpty)
                {
                    snapshot = new StorySnapshot(top, now);
                }
            }
            return Task.FromResult(snapshot);
        }
    }
}
