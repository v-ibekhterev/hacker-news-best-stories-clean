using System.Text.Json;
using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HackerNews.Api.Services;

public sealed class StoryUpdatesWorker(
    IHttpClientFactory clientFactory,
    BestStoriesService stories,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider,
    ILogger<StoryUpdatesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await stories.InitializeAsync();
        var delay = options.Value.StreamReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var httpClient = clientFactory.CreateClient(nameof(HackerNewsUpdatesClient));
                var client = new HackerNewsUpdatesClient(httpClient, options, timeProvider);
                await client.ListenAsync(async ids =>
                {
                    delay = options.Value.StreamReconnectDelay;
                    await stories.MarkUpdatedAsync(ids, stoppingToken);
                    logger.LogDebug("Received {ItemCount} item IDs from the updates stream", ids.Count);
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                                              OperationCanceledException or TimeoutException or NpgsqlException)
            {
                logger.LogWarning(exception, "Updates stream interrupted; reconnecting after {Delay}", delay);
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            delay = NextReconnectDelay(delay);
        }
    }

    private TimeSpan NextReconnectDelay(TimeSpan currentDelay)
    {
        var maximumDelay = Math.Max(
            options.Value.StreamReconnectDelay.TotalMilliseconds,
            TimeSpan.FromMinutes(1).TotalMilliseconds);

        return TimeSpan.FromMilliseconds(Math.Min(currentDelay.TotalMilliseconds * 2, maximumDelay));
    }
}
