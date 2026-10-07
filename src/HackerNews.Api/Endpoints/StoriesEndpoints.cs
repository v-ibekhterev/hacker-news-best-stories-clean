using System.Globalization;
using HackerNews.Api.Configuration;
using HackerNews.Api.Models;
using HackerNews.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Endpoints;

public static class StoriesEndpoints
{
    public static void MapStories(this WebApplication app)
    {
        app.MapGet("/api/stories/best", async (HttpRequest request, BestStoriesService stories,
            IOptions<HackerNewsOptions> options, CancellationToken cancellationToken) =>
        {
            var values = request.Query["count"];
            if (values.Count != 1 ||
                !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                count < 1 || count > options.Value.MaximumStoryCount)
            {
                return Results.Problem(statusCode: 400, title: "Invalid story count",
                    detail: $"Supply exactly one integer count between 1 and {options.Value.MaximumStoryCount}.");
            }

            var result = await stories.GetBestAsync(count, cancellationToken);
            return result is null
                ? Results.Problem(statusCode: 503, title: "Stories unavailable",
                    detail: "No usable snapshot is available. Try again after the next refresh.")
                : Results.Ok(result.Value);
        })
        .WithName("GetBestStories")
        .WithSummary("Get Hacker News best stories sorted by score descending")
        .WithDescription("Required query parameter count: integer from 1 to HackerNews:MaximumStoryCount (default 100).")
        .Produces<StoryResponse[]>()
        .ProducesProblem(400)
        .ProducesProblem(503);
    }
}
