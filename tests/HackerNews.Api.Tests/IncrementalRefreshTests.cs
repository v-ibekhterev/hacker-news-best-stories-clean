using System.Net;

namespace HackerNews.Api.Tests;

public sealed class IncrementalRefreshTests
{
    [Fact]
    public async Task Refresh_FailedInitialItems_RetriesOnlyABoundedBatchWhileUnavailable()
    {
        using var harness = new ServiceHarness(settings => settings.UpdateBatchSize = 1);
        var reads = 0;
        harness.Handler.Send = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                return Task.FromResult(StubHandler.Json(new long[] { 1, 2, 3 }));
            }
            Interlocked.Increment(ref reads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        };
        await harness.Service.RefreshAsync();
        Assert.Null(harness.Service.Snapshot);
        Assert.Equal(3, reads);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Null(harness.Service.Snapshot);
        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task Refresh_IncrementalBatch_RespectsBatchAndConcurrencyLimitsWhileReadsStayIsolated()
    {
        using var harness = new ServiceHarness(settings =>
        {
            settings.UpdateBatchSize = 2;
            settings.MaxUpstreamConcurrency = 2;
        });
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot!;
        harness.Service.MarkUpdated([1, 2, 3]);
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        int current = 0, maximum = 0, total = 0, membershipCalls = 0;
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                Interlocked.Increment(ref membershipCalls);
                return StubHandler.Json(new long[] { 1, 2, 3 });
            }
            var active = Interlocked.Increment(ref current);
            Interlocked.Increment(ref total);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
            } while (active > observed && Interlocked.CompareExchange(ref maximum, active, observed) != observed);
            if (active == 2)
            {
                entered.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(token);
                return StubHandler.Json(new { type = "story", title = "Updated", by = "a", time = 1, score = 100 });
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        };
        var refresh = harness.Service.RefreshIncrementalAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var duplicate = harness.Service.RefreshIncrementalAsync();
        Assert.Same(refresh, duplicate);
        var results = await Task.WhenAll(Enumerable.Range(0, 120).Select(_ => harness.Service.GetBestAsync(100, default)));
        Assert.All(results, result => Assert.True(old.Stories.SequenceEqual(result!.Value)));
        Assert.Equal(2, total);
        Assert.Equal(1, membershipCalls);
        release.SetResult();
        await refresh;
        Assert.InRange(maximum, 2, 2);
        Assert.Equal(2, total);
        Assert.Equal(3, harness.Service.Snapshot!.Stories.Length);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(3, total);
    }

    [Fact]
    public async Task Refresh_NotificationDuringBootstrap_PreservesDirtyIdForNextBatch()
    {
        using var harness = new ServiceHarness(settings => settings.MaxUpstreamConcurrency = 1);
        var normal = harness.Handler.Send;
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        var reads = 0;
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("item/1.json") && Interlocked.Increment(ref reads) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return await normal(request, token);
        };
        var load = harness.Service.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Service.MarkUpdated([1]);
        release.SetResult();
        await load;
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Refresh_NoNotifications_OnlyFetchesMembership()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot;
        var normal = harness.Handler.Send;
        var paths = new List<string>();
        harness.Handler.Send = (request, token) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return normal(request, token);
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(["/v0/beststories.json"], paths);
        Assert.Same(old, harness.Service.Snapshot);
    }

    [Fact]
    public async Task Refresh_DuplicateAndUnrelatedNotifications_FetchesOnlyRelevantIdsOnce()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        harness.Service.MarkUpdated([2, 2, 999, 999]);
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        harness.Handler.Send = (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("beststories.json")
                ? StubHandler.Json(new long[] { 1, 2, 3 })
                : StubHandler.Json(new { type = "story", title = "Changed", by = "a", time = 1, score = 20 }));
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(["/v0/beststories.json", "/v0/item/2.json"], paths.Order());
        var snapshot = harness.Service.Snapshot!;
        Assert.Equal([20, 3, 1], snapshot.Stories.Select(story => story.Score));
        Assert.Equal("Changed", snapshot.Stories[0].Title);
    }

    [Fact]
    public async Task Refresh_MembershipChanges_FetchesAdditionsAndRemovesDepartures()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        harness.Service.MarkUpdated([1]);
        var paths = new List<string>();
        harness.Handler.Send = (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("beststories.json")
                ? StubHandler.Json(new long[] { 2, 3, 4 })
                : StubHandler.Json(new { type = "story", title = "Added", by = "a", time = 1, score = 40 }));
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(["/v0/beststories.json", "/v0/item/4.json"], paths);
        Assert.Equal([40, 3, 2], harness.Service.Snapshot!.Stories.Select(story => story.Score));
    }

    [Fact]
    public async Task Refresh_UpdateArrivesDuringItemRead_QueuesAnotherReadWithoutPartialPublication()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot;
        harness.Service.MarkUpdated([2]);
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        var calls = 0;
        harness.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                return StubHandler.Json(new long[] { 1, 2, 3 });
            }
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return StubHandler.Json(new { type = "story", title = "Changed", by = "a", time = 1, score = call * 10 });
        };
        var refresh = harness.Service.RefreshIncrementalAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Service.MarkUpdated([2]);
        Assert.Same(old, harness.Service.Snapshot);
        release.SetResult();
        await refresh;
        Assert.Equal(10, harness.Service.Snapshot!.Stories[0].Score);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(2, calls);
        Assert.Equal(20, harness.Service.Snapshot!.Stories[0].Score);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"type":"story","deleted":true}""")]
    [InlineData("""{"type":"story","dead":true}""")]
    [InlineData("""{"type":"comment"}""")]
    public async Task Refresh_ConfirmedUnusableItem_RemovesCachedStory(string payload)
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        harness.Service.MarkUpdated([3]);
        harness.Handler.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("beststories.json")
            ? StubHandler.Json(new long[] { 1, 2, 3 })
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal([2, 1], harness.Service.Snapshot!.Stories.Select(story => story.Score));
    }

    [Fact]
    public async Task Refresh_ItemFailure_RetainsValueAndRetriesNextBatch()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        var old = harness.Service.Snapshot;
        harness.Service.MarkUpdated([3]);
        harness.Handler.Send = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("beststories.json")
            ? StubHandler.Json(new long[] { 1, 2, 3 })
            : new HttpResponseMessage(HttpStatusCode.BadGateway));
        await harness.Service.RefreshIncrementalAsync();
        Assert.Same(old, harness.Service.Snapshot);
        var calls = 0;
        harness.Handler.Send = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                return Task.FromResult(StubHandler.Json(new long[] { 1, 2, 3 }));
            }
            Interlocked.Increment(ref calls);
            return Task.FromResult(StubHandler.Json(new { type = "story", title = "Fixed", by = "a", time = 1, score = 50 }));
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(1, calls);
        Assert.Equal(50, harness.Service.Snapshot!.Stories[0].Score);
    }

    [Fact]
    public async Task Refresh_MembershipFailure_DoesNotLosePendingUpdates()
    {
        using var harness = new ServiceHarness();
        await harness.Service.RefreshAsync();
        harness.Service.MarkUpdated([1]);
        harness.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        await harness.Service.RefreshIncrementalAsync();
        var paths = new List<string>();
        harness.Handler.Send = (request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("beststories.json")
                ? StubHandler.Json(new long[] { 1, 2, 3 })
                : StubHandler.Json(new { type = "story", title = "Changed", by = "a", time = 1, score = 99 }));
        };
        await harness.Service.RefreshIncrementalAsync();
        Assert.Contains("/v0/item/1.json", paths);
    }

    [Fact]
    public async Task Refresh_ReconciliationDue_DrainsAllCandidatesAcrossBoundedBatches()
    {
        using var harness = new ServiceHarness(settings => settings.UpdateBatchSize = 1);
        await harness.Service.RefreshAsync();
        var normal = harness.Handler.Send;
        var itemPaths = new List<string>();
        harness.Handler.Send = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/item/"))
            {
                itemPaths.Add(request.RequestUri.AbsolutePath);
            }
            return normal(request, token);
        };
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        await harness.Service.RefreshIncrementalAsync();
        Assert.Single(itemPaths);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(2, itemPaths.Count);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(3, itemPaths.Count);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(3, itemPaths.Count);
        Assert.Equal(3, itemPaths.Distinct().Count());
    }

    [Fact]
    public async Task Refresh_BacklogWithRepeatedLowIdUpdates_DoesNotStarveOtherCandidates()
    {
        using var harness = new ServiceHarness(settings => settings.UpdateBatchSize = 1);
        await harness.Service.RefreshAsync();
        harness.Service.MarkUpdated([1, 2, 3]);
        var normal = harness.Handler.Send;
        var paths = new List<string>();
        harness.Handler.Send = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/item/"))
            {
                paths.Add(request.RequestUri.AbsolutePath);
            }
            return normal(request, token);
        };
        await harness.Service.RefreshIncrementalAsync();
        harness.Service.MarkUpdated([1]);
        await harness.Service.RefreshIncrementalAsync();
        harness.Service.MarkUpdated([1]);
        await harness.Service.RefreshIncrementalAsync();
        Assert.Equal(["/v0/item/1.json", "/v0/item/2.json", "/v0/item/3.json"], paths);
    }
}
