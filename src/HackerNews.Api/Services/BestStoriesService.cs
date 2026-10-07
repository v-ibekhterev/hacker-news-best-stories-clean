using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using HackerNews.Api.Models;
using HackerNews.Api.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HackerNews.Api.Services;

public sealed record StorySnapshot(ImmutableArray<StoryResponse> Stories, DateTimeOffset LoadedAt);

public sealed class BestStoriesService(
    IHttpClientFactory clientFactory,
    ILoggerFactory loggerFactory,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    IStoryStateStore store,
    ILogger<BestStoriesService> logger)
{
    private readonly object gate = new();
    private readonly HackerNewsOptions settings = options.Value;
    private Task? initialization;
    private Task? activeRefresh;
    private Task? initialLoad;
    private StorySnapshot? snapshot;

    public StorySnapshot? Snapshot => Volatile.Read(ref snapshot);

    public Task InitializeAsync()
    {
        lock (gate)
        {
            return initialization ??= RestoreAsync();
        }
    }

    private async Task RestoreAsync()
    {
        await Task.Yield();
        var restored = await store.InitializeAsync(settings.BaseUrl, settings.MaximumStoryCount, lifetime.ApplicationStopping);
        if (restored is not null)
        {
            Volatile.Write(ref snapshot, restored);
            logger.LogInformation("Restored {StoryCount} stories from PostgreSQL", restored.Stories.Length);
        }
    }

    public async Task MarkUpdatedAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await store.MarkUpdatedAsync(ids, cancellationToken);
    }

    public Task RefreshAsync() => StartRefresh(forceRefresh: true);
    public Task RefreshIncrementalAsync() => StartRefresh(forceRefresh: false);

    private Task StartRefresh(bool forceRefresh)
    {
        lock (gate)
        {
            if (activeRefresh is { IsCompleted: false })
            {
                return activeRefresh;
            }
            activeRefresh = RefreshCoreAsync(forceRefresh, lifetime.ApplicationStopping);
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
                load = initialLoad ?? RefreshIncrementalAsync();
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

    private async Task RefreshCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await InitializeAsync();
        try
        {
            using var httpClient = clientFactory.CreateClient(nameof(HackerNewsClient));
            var client = new HackerNewsClient(httpClient, loggerFactory.CreateLogger<HackerNewsClient>());
            var ids = await client.GetBestStoryIdsAsync(cancellationToken);
            var batch = await store.PrepareBatchAsync(ids.Where(id => id > 0).Distinct().ToArray(),
                timeProvider.GetUtcNow(), settings.ReconciliationInterval, settings.UpdateBatchSize,
                forceRefresh, cancellationToken);
            var results = await FetchBatchAsync(client, batch, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var next = await store.CommitBatchAsync(batch, results,
                timeProvider.GetUtcNow(), settings.MaximumStoryCount, cancellationToken);

            PublishSnapshot(next, batch.Items.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Snapshot refresh canceled during shutdown");
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or NpgsqlException)
        {
            logger.LogWarning(exception, "Refresh failed; retaining the previous committed snapshot and pending work");
        }
    }

    private async Task<StoryFetchResult[]> FetchBatchAsync(HackerNewsClient client, StoryBatch batch,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<StoryFetchResult>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.MaxUpstreamConcurrency,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(batch.Items, parallelOptions, async (work, token) =>
        {
            results.Add(await FetchStoryAsync(client, work, token));
        });

        return results.ToArray();
    }

    private async Task<StoryFetchResult> FetchStoryAsync(HackerNewsClient client, PendingStory work,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = await client.GetItemAsync(work.Id, cancellationToken);
            return new StoryFetchResult(work, MapStory(item), true);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or ArgumentOutOfRangeException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Failed Hacker News item {ItemId}", work.Id);
            return new StoryFetchResult(work, null, exception is ArgumentOutOfRangeException);
        }
    }

    private static StoryResponse? MapStory(HackerNewsItem? item)
    {
        if (item is not { Deleted: false, Dead: false, Type: "story", Time: not null, Score: not null } ||
            string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.By))
        {
            return null;
        }

        return new StoryResponse(
            item.Title,
            string.IsNullOrWhiteSpace(item.Url) ? null : item.Url,
            item.By,
            DateTimeOffset.FromUnixTimeSeconds(item.Time.Value),
            item.Score.Value,
            item.Descendants ?? 0);
    }

    private void PublishSnapshot(StorySnapshot? next, int itemCount)
    {
        if (next is null)
        {
            logger.LogWarning("No usable persisted snapshot is available");
            return;
        }

        Volatile.Write(ref snapshot, next);
        logger.LogInformation("Processed {ItemCount} items; serving {StoryCount} persisted stories",
            itemCount, next.Stories.Length);
    }
}
