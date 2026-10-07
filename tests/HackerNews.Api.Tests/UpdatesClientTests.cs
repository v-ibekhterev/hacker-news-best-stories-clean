using System.Net;
using System.Net.Http.Headers;
using System.IO.Pipelines;
using System.Text;
using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public sealed class UpdatesClientTests
{
    [Fact]
    public async Task Listen_ConnectionHeadersNeverArrive_RequestTimeoutCancelsConnect()
    {
        var time = new FakeTimeProvider();
        var entered = ServiceHarness.Gate();
        using var handler = new StubHandler
        {
            Send = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return EventResponse("");
            }
        };
        using var http = CreateHttp(handler);
        var client = new HackerNewsUpdatesClient(http, Options.Create(new HackerNewsOptions()), time);
        var listen = client.ListenAsync(_ => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(11));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
    }

    [Theory]
    [InlineData("put", """{"path":"/","data":{"items":[1,2,2],"profiles":["alice"]}}""", new long[] { 1, 2 })]
    [InlineData("put", """{"path":"/items","data":{"0":3,"1":4}}""", new long[] { 3, 4 })]
    [InlineData("put", """{"path":"/items/0","data":5}""", new long[] { 5 })]
    [InlineData("patch", """{"path":"/","data":{"items/0":6,"profiles/0":"alice"}}""", new long[] { 6 })]
    [InlineData("patch", """{"path":"/items","data":{"0":7,"1":null}}""", new long[] { 7 })]
    [InlineData("patch", """{"path":"/","data":{"items":[8],"profiles":["bob"]}}""", new long[] { 8 })]
    [InlineData("put", """{"path":"/profiles","data":["alice"]}""", new long[] { })]
    [InlineData("put", """{"path":"/items","data":null}""", new long[] { })]
    public async Task Listen_FirebaseEvents_ExtractsItemValuesAndIgnoresProfiles(
        string eventName, string data, long[] expected)
    {
        using var handler = new StubHandler
        {
            Send = (request, _) =>
            {
                Assert.Equal("/v0/updates.json", request.RequestUri!.AbsolutePath);
                Assert.Contains(request.Headers.Accept, header => header.MediaType == "text/event-stream");
                return Task.FromResult(EventResponse($"event: keep-alive\ndata: null\n\n: comment\n\nevent: {eventName}\ndata: {data}\n\n"));
            }
        };
        using var http = CreateHttp(handler);
        var client = CreateClient(http);
        var seen = new List<long>();
        await Assert.ThrowsAsync<IOException>(() => client.ListenAsync(ids => seen.AddRange(ids), default));
        Assert.Equal(expected, seen.Order());
    }

    [Fact]
    public async Task Listen_MultilineDataAndCrLf_ParsesCompleteEvent()
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(EventResponse(
                "event: put\r\ndata: {\"path\":\"/\",\r\ndata: \"data\":{\"items\":[42]}}\r\n\r\n"))
        };
        using var http = CreateHttp(handler);
        var seen = new List<long>();
        await Assert.ThrowsAsync<IOException>(() => CreateClient(http).ListenAsync(ids => seen.AddRange(ids), default));
        Assert.Equal([42L], seen);
    }

    [Theory]
    [InlineData("cancel", "null")]
    [InlineData("auth_revoked", "\"revoked\"")]
    public async Task Listen_TerminalFirebaseEvent_FailsForReconnect(string eventName, string data)
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(EventResponse($"event: {eventName}\ndata: {data}\n\n"))
        };
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(http).ListenAsync(_ => { }, default));
    }

    [Fact]
    public async Task Listen_MalformedEvent_FailsExplicitly()
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(EventResponse("event: put\ndata: not json\n\n"))
        };
        using var http = CreateHttp(handler);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => CreateClient(http).ListenAsync(_ => { }, default));
    }

    [Theory]
    [InlineData("put", "[]")]
    [InlineData("put", """{"path":"items","data":[1]}""")]
    [InlineData("patch", """{"path":"/items","data":[1]}""")]
    [InlineData("put", """{"path":"/items","data":["wrong"]}""")]
    public async Task Listen_InvalidEnvelopeOrItemIds_FailsForReconnect(string eventName, string payload)
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(EventResponse($"event: {eventName}\ndata: {payload}\n\n"))
        };
        using var http = CreateHttp(handler);
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => CreateClient(http).ListenAsync(_ => { }, default));
    }

    [Fact]
    public async Task Listen_NonSuccessResponse_FailsExplicitly()
    {
        using var handler = new StubHandler
        {
            Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))
        };
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(http).ListenAsync(_ => { }, default));
    }

    [Fact]
    public async Task Listen_NotEventStream_FailsExplicitly()
    {
        using var handler = new StubHandler { Send = (_, _) => Task.FromResult(StubHandler.Json(new { items = new[] { 1 } })) };
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(http).ListenAsync(_ => { }, default));
    }

    [Fact]
    public async Task Listen_IdleConnection_TimeoutCancelsRead()
    {
        var pipe = new Pipe();
        var time = new FakeTimeProvider();
        var received = ServiceHarness.Gate();
        using var handler = new StubHandler { Send = (_, _) => Task.FromResult(EventResponse(pipe.Reader.AsStream())) };
        using var http = CreateHttp(handler);
        var client = new HackerNewsUpdatesClient(http, Options.Create(new HackerNewsOptions()), time);
        var listen = client.ListenAsync(_ => received.TrySetResult(), default);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("event: put\ndata: {\"path\":\"/items\",\"data\":[1]}\n\n"));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Listen_ActiveConnection_CallerCancellationClosesRead()
    {
        var pipe = new Pipe();
        using var handler = new StubHandler { Send = (_, _) => Task.FromResult(EventResponse(pipe.Reader.AsStream())) };
        using var http = CreateHttp(handler);
        using var cancellation = new CancellationTokenSource();
        var listen = CreateClient(http).ListenAsync(_ => { }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
        await pipe.Writer.CompleteAsync();
    }

    internal static HttpResponseMessage EventResponse(string text) =>
        EventResponse(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    internal static HttpResponseMessage EventResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static HttpClient CreateHttp(StubHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://fake.test/v0/"), Timeout = Timeout.InfiniteTimeSpan };

    private static HackerNewsUpdatesClient CreateClient(HttpClient http) =>
        new(http, Options.Create(new HackerNewsOptions()), new FakeTimeProvider());
}
