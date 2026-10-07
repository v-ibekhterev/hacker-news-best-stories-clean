using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Channels;
using HackerNews.Api.Configuration;
using HackerNews.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public sealed class UpdatesWorkerTests
{
    [Fact]
    public async Task Execute_StreamNotification_RefreshesOnlyChangedCandidateAndStops()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var pipe = new Pipe();
        harness.UpdatesHandler.Send = (_, _) => Task.FromResult(UpdatesClientTests.EventResponse(pipe.Reader.AsStream()));
        var logger = new RecordingLogger();
        using var worker = new StoryUpdatesWorker(harness.ClientFactory, harness.Service,
            Options.Create(new HackerNewsOptions()), harness.Time, logger);
        await worker.StartAsync(default);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            "event: put\ndata: {\"path\":\"/\",\"data\":{\"items\":[2,999],\"profiles\":[\"a\"]}}\n\n"));
        await logger.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var paths = new List<string>();
        harness.Handler.Send = (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("beststories.json")
                ? StubHandler.Json(new long[] { 1, 2, 3 })
                : StubHandler.Json(new { type = "story", title = "Changed", by = "a", time = 1, score = 25 }));
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(["/v0/beststories.json", "/v0/item/2.json"], paths);
        Assert.Equal(25, harness.Service.Snapshot!.Stories[0].Score);
        await worker.StopAsync(default);
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Execute_ConnectionFailures_ReconnectsWithBackoffWithoutDiscardingSnapshot()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot;
        var requests = Channel.CreateUnbounded<int>();
        var calls = 0;
        harness.UpdatesHandler.Send = (_, _) =>
        {
            requests.Writer.TryWrite(Interlocked.Increment(ref calls));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        };
        var time = new ObservedTimeProvider(harness.Time);
        var logger = new RecordingLogger();
        using var worker = new StoryUpdatesWorker(harness.ClientFactory, harness.Service,
            Options.Create(new HackerNewsOptions()), time, logger);
        await worker.StartAsync(default);
        Assert.Equal(1, await requests.Reader.ReadAsync());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        time.Inner.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10));
        Assert.Same(old, harness.Service.Snapshot);
        await worker.StopAsync(default);
        time.Inner.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, calls);
        Assert.True(logger.Warnings >= 2);
    }

    [Fact]
    public async Task Execute_EndOfStream_ReconnectsAndProcessesNewConnection()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var pipe = new Pipe();
        var calls = 0;
        harness.UpdatesHandler.Send = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? UpdatesClientTests.EventResponse("")
            : UpdatesClientTests.EventResponse(pipe.Reader.AsStream()));
        var time = new ObservedTimeProvider(harness.Time);
        var logger = new RecordingLogger();
        using var worker = new StoryUpdatesWorker(harness.ClientFactory, harness.Service,
            Options.Create(new HackerNewsOptions()), time, logger);
        await worker.StartAsync(default);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        time.Inner.Advance(TimeSpan.FromSeconds(5));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            "event: patch\ndata: {\"path\":\"/items\",\"data\":{\"0\":1}}\n\n"));
        await logger.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        await worker.StopAsync(default);
        await pipe.Writer.CompleteAsync();
    }

    private sealed class RecordingLogger : ILogger<StoryUpdatesWorker>
    {
        public TaskCompletionSource Received { get; } = ServiceHarness.Gate();
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug)
            {
                Received.TrySetResult();
            }
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref Warnings);
            }
        }
    }

    private sealed class ObservedTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly Channel<TimeSpan> timers = Channel.CreateUnbounded<TimeSpan>();
        public FakeTimeProvider Inner => inner;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            timers.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task WaitForTimerAsync(TimeSpan expected)
        {
            while (await timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) != expected)
            {
            }
        }
    }
}
