namespace HackerNews.Api.Models;

public sealed record HackerNewsItem(
    long Id,
    string? Type,
    string? Title,
    string? Url,
    string? By,
    long? Time,
    int? Score,
    int? Descendants,
    bool Deleted = false,
    bool Dead = false);
