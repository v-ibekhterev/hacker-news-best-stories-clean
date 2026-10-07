using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using HackerNews.Api.Endpoints;
using HackerNews.Api.Health;
using HackerNews.Api.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using HackerNews.Api.Storage;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<HackerNewsOptions>()
    .BindConfiguration("HackerNews")
    .Validate(settings => settings.IsValid(), "Invalid HackerNews settings: require an absolute HTTP(S) BaseUrl ending in '/', positive supported timeouts/intervals, and positive counts.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<HackerNewsClient>((services, client) =>
{
    var settings = services.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    client.BaseAddress = new Uri(settings.BaseUrl);
    client.Timeout = settings.RequestTimeout;
});
builder.Services.AddSingleton<BestStoriesService>();
builder.Services.AddSingleton(services =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException("Configure ConnectionStrings:Postgres; no in-memory storage fallback is allowed.");
    }
    return NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton<IStoryStateStore, PostgresStoryStateStore>();
builder.Services.AddHttpClient<HackerNewsUpdatesClient>((services, client) =>
{
    client.BaseAddress = new Uri(services.GetRequiredService<IOptions<HackerNewsOptions>>().Value.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddHostedService<StoryUpdatesWorker>();
builder.Services.AddHostedService<StoryRefreshWorker>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddOperationTransformer((operation, context, _) =>
{
    if (context.Description.RelativePath == "api/stories/best")
    {
        var settings = context.ApplicationServices.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
        operation.Parameters =
        [
            new OpenApiParameter
            {
                Name = "count",
                In = ParameterLocation.Query,
                Required = true,
                Description = $"Number of stories, from 1 to {settings.MaximumStoryCount}.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "1",
                    Maximum = settings.MaximumStoryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        ];
    }
    return Task.CompletedTask;
}));
builder.Services.AddHealthChecks()
    .AddCheck<SnapshotReadinessCheck>("snapshot", tags: ["ready"])
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();
app.UseExceptionHandler();
app.MapStories();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
await app.Services.GetRequiredService<BestStoriesService>().InitializeAsync();
app.Run();

public partial class Program;
