using System.Net;
using System.Net.Http.Json;
using HackerNews.Api.Clients;
using HackerNews.Api.Configuration;
using HackerNews.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using HackerNews.Api.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HackerNews.Api.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    public new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
        (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("beststories.json")
            ? Json(new long[] { 1, 2, 3 })
            : Json(new { id = 1, type = "story", title = "Story", by = "author", time = 1000, score = int.Parse(request.RequestUri.Segments[^1].Split('.')[0]) }));

    public static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Send(request, cancellationToken);
}

internal sealed class TestLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => stopping.Token;
    public CancellationToken ApplicationStopped => stopping.Token;
    public void StopApplication() => stopping.Cancel();
    public void Dispose() => stopping.Dispose();
}

internal sealed class ServiceHarness : IDisposable
{
    private readonly ServiceProvider provider;
    public StubHandler Handler { get; } = new();
    public FakeTimeProvider Time { get; } = new();
    public TestLifetime Lifetime { get; } = new();
    public BestStoriesService Service { get; }
    public StoryRefreshWorker Worker { get; }
    public StoryUpdatesWorker UpdatesWorker { get; }
    public StubHandler UpdatesHandler { get; } = new();
    public IHttpClientFactory ClientFactory => provider.GetRequiredService<IHttpClientFactory>();

    public ServiceHarness(Action<HackerNewsOptions>? configure = null, IStoryStateStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IHostApplicationLifetime>(Lifetime);
        services.Configure<HackerNewsOptions>(settings => configure?.Invoke(settings));
        services.AddHttpClient<HackerNewsClient>(client => client.BaseAddress = new Uri("https://fake.test/v0/"))
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        services.AddSingleton<BestStoriesService>();
        services.AddSingleton<IStoryStateStore>(store ?? new MemoryStoryStateStore());
        services.AddSingleton<StoryRefreshWorker>();
        services.AddHttpClient<HackerNewsUpdatesClient>(client =>
        {
            client.BaseAddress = new Uri("https://fake.test/v0/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => UpdatesHandler);
        services.AddSingleton<StoryUpdatesWorker>();
        provider = services.BuildServiceProvider();
        Service = provider.GetRequiredService<BestStoriesService>();
        Worker = provider.GetRequiredService<StoryRefreshWorker>();
        UpdatesWorker = provider.GetRequiredService<StoryUpdatesWorker>();
    }

    public void Dispose()
    {
        Lifetime.StopApplication();
        provider.Dispose();
        Lifetime.Dispose();
    }

    public static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    public StubHandler Handler { get; } = new();
    public Action<HackerNewsOptions>? ConfigureOptions { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IStoryStateStore>();
            services.AddSingleton<IStoryStateStore, MemoryStoryStateStore>();
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                var database = options.Registrations.Single(check => check.Name == "database");
                options.Registrations.Remove(database);
            });
            if (ConfigureOptions is not null)
            {
                services.PostConfigure(ConfigureOptions);
            }
            services.AddHttpClient<HackerNewsClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        });

    public BestStoriesService Stories => Services.GetRequiredService<BestStoriesService>();
}
