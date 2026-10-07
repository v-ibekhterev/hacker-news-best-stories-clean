using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HackerNews.Api.Exports;
using HackerNews.Api.Models;

namespace HackerNews.Api.Tests;

public sealed class ExportApiTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"count\":0}")]
    [InlineData("{\"count\":-1}")]
    [InlineData("{\"count\":1000001}")]
    [InlineData("{\"count\":\"100\"}")]
    [InlineData("{")]
    public async Task Create_InvalidBody_ReturnsProblemDetails(string body)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = Create(body);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Create_MissingIdempotencyKey_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/story-exports", new ExportRequest(10));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsAcceptedWithDurableOperationLocation()
    {
        var store = new StubExportStore();
        await using var factory = new ApiFactory { ExportStore = store };
        using var client = factory.CreateClient();
        using var request = Create("{\"count\":1000000}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<ExportAccepted>();
        Assert.Equal($"/api/story-exports/{accepted!.OperationId}", response.Headers.Location!.ToString());
        Assert.Equal("queued", accepted.Status);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter!.Delta);
        Assert.Equal(1, store.CreatedCount);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(429)]
    public async Task Create_AdmissionFailure_ReturnsProblemDetails(int code)
    {
        await using var factory = new ApiFactory { ExportStore = new StubExportStore { CreationFailure = code } };
        using var client = factory.CreateClient();
        using var request = Create("{\"count\":10}");
        using var response = await client.SendAsync(request);
        Assert.Equal(code, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("queued", 409)]
    [InlineData("inProgress", 409)]
    [InlineData("failed", 409)]
    [InlineData("expired", 410)]
    public async Task Results_NotSucceeded_ReturnsKnownProblem(string status, int code)
    {
        var store = CompletedStore();
        store.Operation = store.Operation! with { Status = status };
        await using var factory = new ApiFactory { ExportStore = store };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/story-exports/{store.Operation.OperationId}/results");
        Assert.Equal(code, (int)response.StatusCode);
    }

    [Fact]
    public async Task Status_UnknownOperation_ReturnsNotFound()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/story-exports/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=1001")]
    [InlineData("pageSize=bad")]
    [InlineData("pageSize=2&pageSize=3")]
    [InlineData("continuationToken=broken")]
    [InlineData("continuationToken=")]
    public async Task Results_InvalidQuery_ReturnsBadRequest(string query)
    {
        var store = CompletedStore();
        await using var factory = new ApiFactory { ExportStore = store };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/story-exports/{store.Operation!.OperationId}/results?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Results_Traversal_ReturnsEveryItemExactlyOnceAndFinalNullToken()
    {
        var store = CompletedStore();
        await using var factory = new ApiFactory { ExportStore = store };
        using var client = factory.CreateClient();
        var path = $"/api/story-exports/{store.Operation!.OperationId}/results?pageSize=2";
        var first = await client.GetFromJsonAsync<ExportPage>(path);
        Assert.Equal([3, 2], first!.Items.Select(story => story.Score));
        Assert.NotNull(first.NextContinuationToken);
        var second = await client.GetFromJsonAsync<ExportPage>(path + "&continuationToken=" + first.NextContinuationToken);
        Assert.Equal(1, Assert.Single(second!.Items).Score);
        Assert.Null(second.NextContinuationToken);
        using var changedSize = await client.GetAsync(path.Replace("=2", "=3", StringComparison.Ordinal) +
            "&continuationToken=" + first.NextContinuationToken);
        Assert.Equal(HttpStatusCode.BadRequest, changedSize.StatusCode);
    }

    [Fact]
    public async Task Results_LargeItems_EnforcesExactSerializedResponseBudget()
    {
        var store = CompletedStore();
        store.Stories = Enumerable.Range(0, 3).Select(index =>
            new StoryResponse(new string('x', 2500), null, "author", DateTimeOffset.UnixEpoch, index, 0)).ToArray();
        await using var factory = new ApiFactory
        {
            ExportStore = store,
            ConfigureExports = options => options.MaximumResponseBytes = 4096
        };
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/story-exports/{store.Operation!.OperationId}/results?pageSize=3");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length <= 4096);
        var page = JsonSerializer.Deserialize<ExportPage>(bytes, ExportJson.Options);
        Assert.Single(page!.Items);
        Assert.NotNull(page.NextContinuationToken);
    }

    private static HttpRequestMessage Create(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/story-exports")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "test-key");
        return request;
    }

    private static StubExportStore CompletedStore()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new StubExportStore
        {
            Operation = new(id, "succeeded", 10, 3, 3, now, now, now.AddHours(24), 7,
                $"/api/story-exports/{id}/results", null),
            Stories = Enumerable.Range(1, 3).Reverse().Select(score =>
                new StoryResponse("Story", null, "author", DateTimeOffset.UnixEpoch, score, 0)).ToArray()
        };
    }
}
