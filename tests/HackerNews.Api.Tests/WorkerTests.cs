using System.Net;
using System.Threading.Channels;

namespace HackerNews.Api.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task Execute_TimerTicksDuringRefresh_CoalescesWithoutOverlapAndStopsCleanly()
    {
        using var harness = new ServiceHarness();
        var starts = Channel.CreateUnbounded<int>();
        using var release = new SemaphoreSlim(0);
        var calls = 0;
        var current = 0;
        harness.Handler.Send = async (_, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref current));
            try
            {
                starts.Writer.TryWrite(Interlocked.Increment(ref calls));
                await release.WaitAsync(token);
                return StubHandler.Json(Array.Empty<long>());
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        };
        await harness.Worker.StartAsync(default);
        Assert.Equal(1, await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        harness.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Release();
        Assert.Equal(2, await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        harness.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(2, Volatile.Read(ref calls));
        harness.Lifetime.StopApplication();
        await harness.Worker.StopAsync(default);
        Assert.Equal(0, current);
        Assert.Null(harness.Service.Snapshot);
    }

    [Fact]
    public async Task Execute_InitialFailure_NextTimerTickRecovers()
    {
        using var harness = new ServiceHarness();
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        harness.Handler.Send = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        };
        await harness.Worker.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstLoad = harness.Service.RefreshAsync();
        release.SetResult();
        await firstLoad;
        Assert.Null(harness.Service.Snapshot);
        var recovered = ServiceHarness.Gate();
        harness.Handler.Send = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                recovered.TrySetResult();
                return Task.FromResult(StubHandler.Json(new long[] { 1 }));
            }
            return Task.FromResult(StubHandler.Json(new { type = "story", title = "Recovered", by = "a", time = 1, score = 5 }));
        };
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Service.RefreshAsync();
        Assert.NotNull(harness.Service.Snapshot);
        harness.Lifetime.StopApplication();
        await harness.Worker.StopAsync(default);
    }
}
