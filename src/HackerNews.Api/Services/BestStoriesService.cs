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

    public StorySnapshot? Snapshot => Volatile.Read(ref snapshot);

    public Task RefreshAsync()
    {
        lock (gate)
        {
            if (activeRefresh is { IsCompleted: false })
            {
                return activeRefresh;
            }

            activeRefresh = RefreshCoreAsync(lifetime.ApplicationStopping);
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

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        // Yield before network work so the shared task is published under the gate first.
        await Task.Yield();
        try
        {
            using var httpClient = clientFactory.CreateClient(nameof(HackerNewsClient));
            var client = new HackerNewsClient(httpClient, loggerFactory.CreateLogger<HackerNewsClient>());
            var ids = await client.GetBestStoryIdsAsync(cancellationToken);
            var stories = new ConcurrentBag<(long Id, StoryResponse Story)>();
            await Parallel.ForEachAsync(ids.Distinct(), new ParallelOptions
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
                        return;
                    }

                    var story = new StoryResponse(item.Title, string.IsNullOrWhiteSpace(item.Url) ? null : item.Url,
                        item.By, DateTimeOffset.FromUnixTimeSeconds(item.Time.Value),
                        item.Score.Value, item.Descendants ?? 0);
                    stories.Add((id, story));
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or ArgumentOutOfRangeException ||
                                                  exception is OperationCanceledException && !token.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Skipping failed Hacker News item {ItemId}", id);
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            if (stories.IsEmpty)
            {
                logger.LogWarning("Refresh produced no valid stories; retaining the previous snapshot");
                return;
            }

            var next = new StorySnapshot(stories.OrderByDescending(entry => entry.Story.Score)
                .ThenBy(entry => entry.Id).Select(entry => entry.Story).ToImmutableArray(),
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
