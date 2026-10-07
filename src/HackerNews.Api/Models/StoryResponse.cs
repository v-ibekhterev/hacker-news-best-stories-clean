namespace HackerNews.Api.Models;

public sealed record StoryResponse(
    string Title,
    string? Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);
