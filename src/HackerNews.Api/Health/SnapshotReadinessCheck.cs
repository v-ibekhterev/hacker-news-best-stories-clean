using HackerNews.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HackerNews.Api.Health;

public sealed class SnapshotReadinessCheck(BestStoriesService stories, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = stories.Snapshot;
        return Task.FromResult(snapshot is null
            ? HealthCheckResult.Unhealthy("No usable snapshot exists.")
            : HealthCheckResult.Healthy("A usable snapshot exists.", new Dictionary<string, object>
            {
                ["loadedAt"] = snapshot.LoadedAt,
                ["ageSeconds"] = (timeProvider.GetUtcNow() - snapshot.LoadedAt).TotalSeconds
            }));
    }
}
