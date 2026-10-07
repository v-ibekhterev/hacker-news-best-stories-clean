namespace HackerNews.Api.Configuration;

public sealed class HackerNewsOptions
{
    public string BaseUrl { get; set; } = "https://hacker-news.firebaseio.com/v0/";
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitialLoadWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxUpstreamConcurrency { get; set; } = 10;
    public int MaximumStoryCount { get; set; } = 100;

    public bool IsValid() =>
        System.Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && BaseUrl.EndsWith('/') &&
        RefreshInterval > TimeSpan.Zero &&
        RefreshInterval.TotalMilliseconds <= uint.MaxValue - 1 &&
        RequestTimeout > TimeSpan.Zero &&
        RequestTimeout.TotalMilliseconds <= int.MaxValue &&
        InitialLoadWaitTimeout > TimeSpan.Zero &&
        InitialLoadWaitTimeout.TotalMilliseconds <= uint.MaxValue - 1 &&
        MaxUpstreamConcurrency >= 1 && MaximumStoryCount >= 1;
}
