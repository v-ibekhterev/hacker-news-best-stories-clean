using System.Net;
using HackerNews.Api.Models;

namespace HackerNews.Api.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public async Task GetBest_UnsortedUpstream_MapsSortsAndLimits()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var result = (await harness.Service.GetBestAsync(2, default))!.Value;
        Assert.Equal([3, 2], result.Select(story => story.Score));
        Assert.All(result, story =>
        {
            Assert.Equal("Story", story.Title);
            Assert.Equal("author", story.PostedBy);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), story.Time);
            Assert.Equal(0, story.CommentCount);
            Assert.Null(story.Uri);
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"type":"story","deleted":true,"title":"x","by":"a","time":1,"score":1}""")]
    [InlineData("""{"type":"story","dead":true,"title":"x","by":"a","time":1,"score":1}""")]
    [InlineData("""{"type":"comment","title":"x","by":"a","time":1,"score":1}""")]
    [InlineData("""{"type":"story","by":"a","time":1,"score":1}""")]
    [InlineData("""{"type":"story","title":"x","time":1,"score":1}""")]
    [InlineData("""{"type":"story","title":"x","by":"a","score":1}""")]
    [InlineData("""{"type":"story","title":"x","by":"a","time":1}""")]
    [InlineData("""{"type":"story","title":"x","by":"a","time":9223372036854775807,"score":1}""")]
    public async Task Refresh_UnusableItems_RemainsUnavailable(string payload)
    {
        using var harness = new ServiceHarness();
        harness.Handler.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("beststories.json")
            ? StubHandler.Json(new long[] { 1 })
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        await harness.Service.RefreshAsync();
        Assert.Null(harness.Service.Snapshot);
        Assert.Null(await harness.Service.GetBestAsync(1, default));
    }

    [Fact]
    public async Task Refresh_ItemFailure_PublishesSuccessfulItems()
    {
        using var harness = new ServiceHarness();
        var normal = harness.Handler.Send;
        harness.Handler.Send = (request, token) => request.RequestUri!.AbsolutePath.EndsWith("item/2.json")
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))
            : normal(request, token);
        await harness.Service.RefreshAsync();
        Assert.Equal([3, 1], harness.Service.Snapshot!.Stories.Select(story => story.Score));
    }

    [Fact]
    public async Task Refresh_ValidItem_MapsUrlAndDescendants()
    {
        using var harness = new ServiceHarness();
        harness.Handler.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("beststories.json")
            ? StubHandler.Json(new long[] { 1 })
            : StubHandler.Json(new HackerNewsItem(1, "story", "Title", "https://example.test", "author", 0, 42, 99)));
        await harness.Service.RefreshAsync();
        var story = Assert.Single(harness.Service.Snapshot!.Stories);
        Assert.Equal(42, story.Score);
        Assert.Equal(99, story.CommentCount);
        Assert.Equal("https://example.test", story.Uri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_FailedOrEmptyRefresh_RetainsOldSnapshot(bool empty)
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot;
        harness.Handler.Send = (_, _) => Task.FromResult(empty
            ? StubHandler.Json(Array.Empty<long>())
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await harness.Service.RefreshAsync();
        Assert.Same(old, harness.Service.Snapshot);
    }

    [Fact]
    public async Task Refresh_InProgress_ReadsOldCompleteSnapshotThenAtomicallyReplaces()
    {
        using var harness = new ServiceHarness(settings => settings.MaxUpstreamConcurrency = 1);
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot!;
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                return StubHandler.Json(new long[] { 4, 5 });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("item/4.json"))
            {
                return StubHandler.Json(new { type = "story", title = "First new story", by = "a", time = 1, score = 101 });
            }

            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return StubHandler.Json(new { type = "story", title = "New", by = "a", time = 1, score = 100 });
        };
        var refresh = harness.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var readers = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => harness.Service.GetBestAsync(100, default)));
        Assert.All(readers, result => Assert.True(old.Stories.SequenceEqual(result!.Value)));
        Assert.Same(old, harness.Service.Snapshot);
        release.SetResult();
        await refresh;
        Assert.NotSame(old, harness.Service.Snapshot);
        Assert.Equal(2, harness.Service.Snapshot!.Stories.Length);
        Assert.Equal(3, old.Stories.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public async Task Refresh_ConcurrentTriggers_SharesTaskAndBoundsConcurrency(int limit)
    {
        using var harness = new ServiceHarness(settings => settings.MaxUpstreamConcurrency = limit);
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        int current = 0, maximum = 0, total = 0, idsCalls = 0;
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                Interlocked.Increment(ref idsCalls);
                return StubHandler.Json(Enumerable.Range(1, 30).Select(value => (long)value).ToArray());
            }

            var active = Interlocked.Increment(ref current);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
            } while (active > observed && Interlocked.CompareExchange(ref maximum, active, observed) != observed);
            Interlocked.Increment(ref total);
            if (active == limit)
            {
                entered.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(token);
                return StubHandler.Json(new { type = "story", title = "x", by = "a", time = 1, score = 1 });
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        };
        var refresh = harness.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var triggers = Enumerable.Range(0, 100).Select(_ => harness.Service.RefreshAsync()).ToArray();
        Assert.All(triggers, task => Assert.Same(refresh, task));
        Assert.Equal(limit, maximum);
        release.SetResult();
        await Task.WhenAll(triggers);
        Assert.Equal(30, total);
        Assert.Equal(1, idsCalls);
        Assert.InRange(maximum, Math.Min(2, limit), limit);
        var result = await harness.Service.GetBestAsync(100, default);
        Assert.Equal(30, result!.Value.Length);
        Assert.Equal(1, idsCalls);
    }

    [Fact]
    public async Task GetBest_ConcurrentInitialReaders_OneLoadAndCallerCancellationIsIsolated()
    {
        using var harness = new ServiceHarness();
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        var normal = harness.Handler.Send;
        var calls = 0;
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return await normal(request, token);
        };
        var readers = Enumerable.Range(0, 100).Select(_ => harness.Service.GetBestAsync(2, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var canceledReader = harness.Service.GetBestAsync(1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledReader);
        release.SetResult();
        var results = await Task.WhenAll(readers);
        Assert.All(results, result => Assert.Equal([3, 2], result!.Value.Select(story => story.Score)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetBest_InitialWaitExpires_ReturnsUnavailableWithoutCancelingRefresh()
    {
        using var harness = new ServiceHarness();
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        var normal = harness.Handler.Send;
        harness.Handler.Send = async (request, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return await normal(request, token);
        };
        var reader = harness.Service.GetBestAsync(1, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Time.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(await reader);
        var shared = harness.Service.RefreshAsync();
        release.SetResult();
        await shared;
        Assert.NotNull(harness.Service.Snapshot);
    }

    [Fact]
    public async Task Refresh_Shutdown_CancelsRequestsAndDoesNotPublish()
    {
        using var harness = new ServiceHarness();
        var entered = ServiceHarness.Gate();
        harness.Handler.Send = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return StubHandler.Json(Array.Empty<long>());
        };
        var refresh = harness.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Null(harness.Service.Snapshot);
    }
}
