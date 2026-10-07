using HackerNews.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Services;

public sealed class StoryRefreshWorker(
    BestStoriesService storiesService,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RefreshInterval, timeProvider);
        try
        {
            await storiesService.RefreshIncrementalAsync();
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await storiesService.RefreshIncrementalAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the shared refresh and the timer.
        }
    }
}
