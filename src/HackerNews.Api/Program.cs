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
using HackerNews.Api.Exports;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<HackerNewsOptions>()
    .BindConfiguration("HackerNews")
    .Validate(settings => settings.IsValid(), "Invalid HackerNews settings: require an absolute HTTP(S) BaseUrl ending in '/', positive supported timeouts/intervals, and positive counts.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ExportOptions>()
    .BindConfiguration("Exports")
    .Validate(settings => settings.IsValid(), "Invalid Exports settings: require positive supported intervals and valid count, chunk, page and capacity limits.")
    .ValidateOnStart();
builder.Services.AddSingleton<IExportStore, PostgresExportStore>();
builder.Services.AddSingleton<ExportTokens>();
builder.Services.AddHostedService<ExportWorker>();
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
    else if (context.Description.RelativePath == "api/story-exports")
    {
        var settings = context.ApplicationServices.GetRequiredService<IOptions<ExportOptions>>().Value;
        operation.Description = $"Local-only unauthenticated export of at most {settings.MaximumCount} persisted stories. " +
            "Succeed with fewer available records. Idempotency keys are retained until operation expiration.";
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "1..128 printable ASCII characters, excluding spaces; same key/count replays the operation.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128 }
        });
    }
    else if (context.Description.RelativePath?.EndsWith("/results", StringComparison.Ordinal) == true)
    {
        var settings = context.ApplicationServices.GetRequiredService<IOptions<ExportOptions>>().Value;
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "pageSize",
            In = ParameterLocation.Query,
            Description = $"Optional; defaults to {Math.Min(100, settings.MaximumPageSize)}. Keep unchanged with a continuation token.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Minimum = "1",
                Maximum = settings.MaximumPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "continuationToken",
            In = ParameterLocation.Query,
            Description = "Optional opaque token from the previous page; bound to operation, generation, page size, and expiry.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
    return Task.CompletedTask;
}));
builder.Services.AddHealthChecks()
    .AddCheck<SnapshotReadinessCheck>("snapshot", tags: ["ready"])
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();
app.UseExceptionHandler();
app.MapStories();
app.MapExports();
app.MapOpenApi();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
await app.Services.GetRequiredService<BestStoriesService>().InitializeAsync();
await app.Services.GetRequiredService<IExportStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<ExportTokens>().InitializeAsync(CancellationToken.None);
app.Run();

public partial class Program;
