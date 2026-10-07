using System.Net;
using System.Text.Json;
using HackerNews.Api.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerNews.Api.Tests;

public sealed class ClientTests
{
    [Theory]
    [InlineData(true, "/v0/beststories.json", "[1,2]")]
    [InlineData(false, "/v0/item/42.json", """{"id":42,"type":"story","score":7,"extra":"ignored"}""")]
    public async Task Get_ValidJson_UsesExpectedPathAndDeserializes(bool ids, string path, string payload)
    {
        using var handler = new StubHandler
        {
            Send = (request, _) =>
            {
                Assert.Equal(path, request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test/v0/") };
        var client = new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance);
        if (ids)
        {
            var result = await client.GetBestStoryIdsAsync(default);
            Assert.Equal([1L, 2L], result);
        }
        else
        {
            Assert.Equal(7, (await client.GetItemAsync(42, default))!.Score);
        }
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "null")]
    public async Task GetBestIds_InvalidResponse_ThrowsKnownError(int status, string payload)
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(payload) })
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test/") };
        var client = new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance);
        if (status != 200)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBestStoryIdsAsync(default));
        }
        else
        {
            await Assert.ThrowsAsync<JsonException>(() => client.GetBestStoryIdsAsync(default));
        }
    }

    [Fact]
    public async Task GetItem_NullResponse_ReturnsNull()
    {
        using var handler = new StubHandler { Send = (_, _) => Task.FromResult(StubHandler.Json<object?>(null)) };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test/") };
        var client = new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance);
        Assert.Null(await client.GetItemAsync(1, default));
    }

    [Fact]
    public async Task GetItem_CallerCancellation_Propagates()
    {
        using var handler = new StubHandler
        {
            Send = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return StubHandler.Json<object?>(null);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test/") };
        var client = new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetItemAsync(1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetItem_UpstreamTimeout_Propagates()
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => throw new TaskCanceledException("Simulated HttpClient timeout")
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test/") };
        var client = new HackerNewsClient(http, NullLogger<HackerNewsClient>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetItemAsync(1, default));
    }
}
