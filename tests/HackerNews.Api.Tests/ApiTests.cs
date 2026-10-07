using System.Net;
using System.Text.Json;

namespace HackerNews.Api.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData("")]
    [InlineData("?count=0")]
    [InlineData("?count=-1")]
    [InlineData("?count=101")]
    [InlineData("?count=hello")]
    [InlineData("?count=1&count=2")]
    [InlineData("?count=2147483648")]
    public async Task GetBest_InvalidCount_ReturnsProblemDetails(string query)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/stories/best" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
        Assert.NotNull(json.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetBest_Unavailable_Returns503ProblemDetails()
    {
        using var factory = new ApiFactory();
        factory.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/stories/best?count=1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task GetBest_ManyConcurrentRequests_ReturnsContractWithOneInitialLoad()
    {
        using var factory = new ApiFactory();
        var normal = factory.Handler.Send;
        var entered = ServiceHarness.Gate();
        var release = ServiceHarness.Gate();
        var calls = 0;
        factory.Handler.Send = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("beststories.json"))
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return await normal(request, token);
        };
        using var client = factory.CreateClient();
        var requests = Enumerable.Range(0, 120).Select(_ => client.GetAsync("/api/stories/best?count=2")).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(2, json.RootElement.GetArrayLength());
                Assert.Equal(3, json.RootElement[0].GetProperty("score").GetInt32());
                Assert.Equal(["commentCount", "postedBy", "score", "time", "title", "uri"],
                    json.RootElement[0].EnumerateObject().Select(property => property.Name).Order());
            }
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Health_SnapshotTransitions_ProbesStayLocal()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        int calls = 0;
        var normal = factory.Handler.Send;
        factory.Handler.Send = (request, token) =>
        {
            Interlocked.Increment(ref calls);
            return normal(request, token);
        };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(0, calls);
        await factory.Stories.RefreshAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(4, calls);
        factory.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        await factory.Stories.RefreshAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_Document_DescribesEndpointAndResponses()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = json.RootElement.GetProperty("paths").GetProperty("/api/stories/best").GetProperty("get");
        Assert.True(operation.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("503", out _));
        var parameter = operation.GetProperty("parameters")[0];
        Assert.Equal("count", parameter.GetProperty("name").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("integer", parameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(100, parameter.GetProperty("schema").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task GetBest_ConfiguredMaximum_UsesConfiguredBoundary()
    {
        using var factory = new ApiFactory { ConfigureOptions = settings => settings.MaximumStoryCount = 2 };
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stories/best?count=3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stories/best?count=2")).StatusCode);
    }
}
