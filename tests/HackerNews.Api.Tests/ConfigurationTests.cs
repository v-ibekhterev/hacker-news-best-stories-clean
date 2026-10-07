using HackerNews.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void IsValid_DefaultOptions_Accepts()
    {
        Assert.True(new HackerNewsOptions().IsValid());
    }

    [Fact]
    public void Start_InvalidConfiguration_ThrowsOptionsValidationException()
    {
        using var factory = new ApiFactory { ConfigureOptions = settings => settings.RequestTimeout = TimeSpan.Zero };
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("interval")]
    [InlineData("timeout")]
    [InlineData("wait")]
    [InlineData("concurrency")]
    [InlineData("count")]
    [InlineData("url")]
    [InlineData("slash")]
    public void IsValid_InvalidOptions_Rejects(string setting)
    {
        var options = new HackerNewsOptions();
        switch (setting)
        {
            case "interval": options.RefreshInterval = TimeSpan.Zero; break;
            case "timeout": options.RequestTimeout = TimeSpan.FromSeconds(-1); break;
            case "wait": options.InitialLoadWaitTimeout = TimeSpan.Zero; break;
            case "concurrency": options.MaxUpstreamConcurrency = 0; break;
            case "count": options.MaximumStoryCount = 0; break;
            case "url": options.BaseUrl = "file:///tmp/"; break;
            case "slash": options.BaseUrl = "https://fake.test/v0"; break;
        }
        Assert.False(options.IsValid());
    }
}
