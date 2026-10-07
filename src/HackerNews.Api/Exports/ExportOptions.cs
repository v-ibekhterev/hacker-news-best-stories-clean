namespace HackerNews.Api.Exports;

public sealed class ExportOptions
{
    public int MaximumCount { get; set; } = 1_000_000;
    public int MaximumActiveJobs { get; set; } = 10;
    public long MaximumReservedRows { get; set; } = 2_000_000;
    public int ChunkSize { get; set; } = 500;
    public int MaximumPageSize { get; set; } = 1_000;
    public int MaximumResponseBytes { get; set; } = 1_048_576;
    public TimeSpan JobDeadline { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid() =>
        MaximumCount > 0 && MaximumActiveJobs > 0 && MaximumReservedRows >= MaximumCount &&
        ChunkSize > 0 && ChunkSize <= MaximumPageSize && MaximumPageSize > 0 &&
        MaximumResponseBytes >= 4096 && Timer(JobDeadline) && Timer(Retention) &&
        Timer(TombstoneRetention) && Timer(PollInterval);

    private static bool Timer(TimeSpan value) =>
        value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1;
}
