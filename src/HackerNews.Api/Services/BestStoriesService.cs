using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using HackerNews.Api.Models;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Services;

public sealed record StorySnapshot(ImmutableArray<StoryResponse> Stories, DateTimeOffset LoadedAt);

public sealed class BestStoriesService(
    IHttpClientFactory clientFactory,
    ILoggerFactory loggerFactory,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<BestStoriesService> logger)
{
    private readonly object gate = new();
    private readonly HackerNewsOptions settings = options.Value;
    private Task? activeRefresh;
    private Task? initialLoad;
    private StorySnapshot? snapshot;
    private Dictionary<long, StoryResponse> cache = new();
    private HashSet<long> candidates = new();
    private readonly Queue<long> pending = new();
    private readonly HashSet<long> pendingIds = new();
    private DateTimeOffset? reconciliationStartedAt;

    public StorySnapshot? Snapshot => Volatile.Read(ref snapshot);

    public void MarkUpdated(IEnumerable<long> ids)
    {
        lock (gate)
        {
            foreach (var id in ids)
            {
                if (candidates.Contains(id))
                {
                    Enqueue(id);
                }
            }
        }
    }

    public Task RefreshAsync() => StartRefresh(fullRefresh: true);

    public Task RefreshIncrementalAsync() => StartRefresh(fullRefresh: false);

    private Task StartRefresh(bool fullRefresh)
    {
        lock (gate)
        {
            if (activeRefresh is { IsCompleted: false })
            {
                return activeRefresh;
            }

            activeRefresh = RefreshCoreAsync(fullRefresh, lifetime.ApplicationStopping);
            initialLoad ??= activeRefresh;
            return activeRefresh;
        }
    }

    public async Task<ImmutableArray<StoryResponse>?> GetBestAsync(int count, CancellationToken cancellationToken)
    {
        if (count < 1 || count > settings.MaximumStoryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var current = Snapshot;
        if (current is null)
        {
            Task load;
            lock (gate)
            {
                load = initialLoad ?? RefreshAsync();
            }

            try
            {
                await load.WaitAsync(settings.InitialLoadWaitTimeout, timeProvider, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Initial snapshot wait exceeded {Timeout}", settings.InitialLoadWaitTimeout);
            }

            current = Snapshot;
        }

        return current is null ? null : current.Stories.Take(count).ToImmutableArray();
    }

    private void Enqueue(long id)
    {
        if (pendingIds.Add(id))
        {
            pending.Enqueue(id);
        }
    }

    private async Task RefreshCoreAsync(bool fullRefresh, CancellationToken cancellationToken)
    {
        // Yield before network work so the shared task is published under the gate first.
        await Task.Yield();
        try
        {
            using var httpClient = clientFactory.CreateClient(nameof(HackerNewsClient));
            var client = new HackerNewsClient(httpClient, loggerFactory.CreateLogger<HackerNewsClient>());
            var ids = await client.GetBestStoryIdsAsync(cancellationToken);
            var membership = ids.Where(id => id > 0).ToHashSet();
            long[] selected;
            lock (gate)
            {
                var additions = membership.Except(candidates).ToArray();
                candidates = membership;
                // Purge departed IDs so a burst of unrelated updates cannot grow the backlog.
                var retained = pending.Where(membership.Contains).ToArray();
                pending.Clear();
                pendingIds.Clear();
                foreach (var id in retained.Concat(additions))
                {
                    Enqueue(id);
                }

                if (fullRefresh || reconciliationStartedAt is null)
                {
                    selected = membership.ToArray();
                    foreach (var id in selected)
                    {
                        pendingIds.Remove(id);
                    }
                    pending.Clear();
                    reconciliationStartedAt = timeProvider.GetUtcNow();
                }
                else
                {
                    if (timeProvider.GetUtcNow() - reconciliationStartedAt >= settings.ReconciliationInterval)
                    {
                        foreach (var id in membership)
                        {
                            Enqueue(id);
                        }
                        reconciliationStartedAt = timeProvider.GetUtcNow();
                        logger.LogInformation("Queued rolling reconciliation for {CandidateCount} candidates", membership.Count);
                    }

                    var batch = new List<long>();
                    while (batch.Count < settings.UpdateBatchSize && pending.TryDequeue(out var id))
                    {
                        pendingIds.Remove(id);
                        batch.Add(id);
                    }
                    selected = batch.ToArray();
                }
            }

            var changes = new ConcurrentDictionary<long, StoryResponse>();
            var removed = new ConcurrentBag<long>();
            var failed = new ConcurrentBag<long>();
            await Parallel.ForEachAsync(selected, new ParallelOptions
            {
                MaxDegreeOfParallelism = settings.MaxUpstreamConcurrency,
                CancellationToken = cancellationToken
            }, async (id, token) =>
            {
                try
                {
                    var item = await client.GetItemAsync(id, token);
                    if (item is null || item.Deleted || item.Dead || item.Type != "story" ||
                        string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.By) ||
                        item.Time is null || item.Score is null)
                    {
                        removed.Add(id);
                        return;
                    }

                    var story = new StoryResponse(item.Title, string.IsNullOrWhiteSpace(item.Url) ? null : item.Url,
                        item.By, DateTimeOffset.FromUnixTimeSeconds(item.Time.Value),
                        item.Score.Value, item.Descendants ?? 0);
                    changes[id] = story;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or ArgumentOutOfRangeException ||
                                                  exception is OperationCanceledException && !token.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Skipping failed Hacker News item {ItemId}", id);
                    if (exception is ArgumentOutOfRangeException)
                    {
                        removed.Add(id);
                    }
                    else
                    {
                        failed.Add(id);
                    }
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            var nextCache = cache.Where(entry => membership.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var id in removed)
            {
                nextCache.Remove(id);
            }
            foreach (var entry in changes)
            {
                nextCache[entry.Key] = entry.Value;
            }
            cache = nextCache;
            lock (gate)
            {
                foreach (var id in failed)
                {
                    Enqueue(id);
                }
                logger.LogInformation("Processed {ItemCount} candidate items; {PendingCount} remain pending",
                    selected.Length, pendingIds.Count);
            }

            if (cache.Count == 0)
            {
                logger.LogWarning("Refresh produced no valid stories; retaining the previous snapshot");
                return;
            }

            if (changes.IsEmpty && removed.IsEmpty && Snapshot is not null &&
                Snapshot.Stories.Length == cache.Count)
            {
                return;
            }

            var next = new StorySnapshot(cache.OrderByDescending(entry => entry.Value.Score)
                .ThenBy(entry => entry.Key).Select(entry => entry.Value).ToImmutableArray(),
                timeProvider.GetUtcNow());
            Volatile.Write(ref snapshot, next);
            logger.LogInformation("Published {StoryCount} stories at {LoadedAt}", next.Stories.Length, next.LoadedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Snapshot refresh canceled during shutdown");
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Snapshot refresh failed; retaining the previous snapshot");
        }
    }
}
