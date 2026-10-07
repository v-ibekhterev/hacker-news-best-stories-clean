using HackerNews.Api.Exports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public sealed class ExportWorkerTests
{
    [Fact]
    public async Task Worker_BlockedStep_DoesNotStartOverlappingWork()
    {
        var time = new FakeTimeProvider();
        var started = ServiceHarness.Gate();
        var blocked = ServiceHarness.Gate();
        var calls = 0;
        var active = 0;
        var maximum = 0;
        var store = new StubExportStore
        {
            Process = async cancellationToken =>
            {
                Interlocked.Increment(ref calls);
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                started.TrySetResult();
                try
                {
                    await blocked.Task.WaitAsync(cancellationToken);
                    return false;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        };
        using var worker = new ExportWorker(store, time, Options.Create(new ExportOptions()), NullLogger<ExportWorker>.Instance);
        await worker.StartAsync(default);
        await started.Task;
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, calls);
        Assert.Equal(1, maximum);
        await worker.StopAsync(default);
        Assert.Equal(0, active);
    }
}
