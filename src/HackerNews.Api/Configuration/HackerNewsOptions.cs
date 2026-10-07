namespace HackerNews.Api.Configuration;

public sealed class HackerNewsOptions
{
    public string BaseUrl { get; set; } = "https://hacker-news.firebaseio.com/v0/";
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitialLoadWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxUpstreamConcurrency { get; set; } = 10;
    public int MaximumStoryCount { get; set; } = 100;
    public int UpdateBatchSize { get; set; } = 100;
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan StreamReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsValid() =>
        System.Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && BaseUrl.EndsWith('/') &&
        RefreshInterval > TimeSpan.Zero &&
        RefreshInterval.TotalMilliseconds <= uint.MaxValue - 1 &&
        RequestTimeout > TimeSpan.Zero &&
        RequestTimeout.TotalMilliseconds <= int.MaxValue &&
        InitialLoadWaitTimeout > TimeSpan.Zero &&
        InitialLoadWaitTimeout.TotalMilliseconds <= uint.MaxValue - 1 &&
        MaxUpstreamConcurrency >= 1 && MaximumStoryCount >= 1 && UpdateBatchSize >= 1 &&
        IsTimerInterval(ReconciliationInterval) && IsTimerInterval(StreamReconnectDelay) &&
        IsTimerInterval(StreamIdleTimeout);

    private static bool IsTimerInterval(TimeSpan value) =>
        value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1;
}
