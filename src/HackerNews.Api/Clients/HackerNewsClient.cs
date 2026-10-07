using System.Net.Http.Json;
using System.Text.Json;
using HackerNews.Api.Models;

namespace HackerNews.Api.Clients;

public sealed class HackerNewsClient(HttpClient httpClient, ILogger<HackerNewsClient> logger)
{
    public async Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken) =>
        await GetAsync<long[]>("beststories.json", cancellationToken)
        ?? throw new JsonException("The best stories response was null.");

    public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken) =>
        GetAsync<HackerNewsItem>($"item/{id}.json", cancellationToken);

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(path, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Hacker News request failed for {Path}", path);
            throw;
        }
    }
}
