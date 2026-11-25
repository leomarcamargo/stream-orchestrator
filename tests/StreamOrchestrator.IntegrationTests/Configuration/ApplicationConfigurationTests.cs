using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services;
using StreamOrchestrator.Web.Services.Interfaces;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.IntegrationTests.Configuration;

/// <summary>
/// Starts the real application host in memory (TestServer). No port is opened and nothing outside the
/// process is contacted; the YouTube player only loads in a browser, which these tests do not use.
/// </summary>
public class ApplicationConfigurationTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        string? environment = null,
        params (string Key, string Value)[] settings
    ) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        if (environment is not null)
        {
            builder.UseEnvironment(environment);
        }

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    });

    [Fact]
    public void Services_WhenHostStarts_ShouldBindSettingsFromAppsettings()
    {
        using var factory = CreateFactory();

        var execution = factory.Services.GetRequiredService<IOptions<ExecutionSettings>>().Value;
        var youTube = factory.Services.GetRequiredService<IOptions<YouTubeSettings>>().Value;

        Assert.Equal(30, execution.DefaultDurationSeconds);
        Assert.Equal(100, execution.DefaultSessions);
        Assert.Equal(300, execution.MaxDurationSeconds);
        Assert.Equal(1000, execution.MaxSessions);
        Assert.Matches("^[A-Za-z0-9_-]{11}$", youTube.VideoId);
    }

    [Fact]
    public void Services_WhenHostStarts_ShouldResolveEssentialServices()
    {
        using var factory = CreateFactory();

        var orchestrator = factory.Services.GetRequiredService<SessionOrchestrator>();
        var playback = factory.Services.GetRequiredService<IPlaybackService>();
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();

        Assert.Same(orchestrator, factory.Services.GetRequiredService<SessionOrchestrator>());
        Assert.IsType<PlaybackService>(playback);
        Assert.Equal(1000, orchestrator.Limits.MaxSessions);
        Assert.NotNull(meterFactory);
    }

    [Fact]
    public void Configuration_WhenOverridesAreProvided_ShouldReplaceAppsettingsValues()
    {
        using var factory = CreateFactory(
            null,
            ("ExecutionSettings:DefaultSessions", "50"),
            ("ExecutionSettings:MaxSessions", "200"),
            ("YouTubeSettings:VideoId", "abcdefghijk")
        );

        var execution = factory.Services.GetRequiredService<IOptions<ExecutionSettings>>().Value;
        var youTube = factory.Services.GetRequiredService<IOptions<YouTubeSettings>>().Value;

        Assert.Equal(50, execution.DefaultSessions);
        Assert.Equal(200, execution.MaxSessions);
        Assert.Equal(30, execution.DefaultDurationSeconds);
        Assert.Equal("abcdefghijk", youTube.VideoId);
    }

    [Fact]
    public async Task Home_WhenVideoIdIsEmpty_ShouldStartAndRenderWithoutThePlayer()
    {
        using var factory = CreateFactory(null, ("YouTubeSettings:VideoId", ""));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Stream Orchestrator", html);
        Assert.Contains("No video configured", html);
        Assert.DoesNotContain("id=\"yt-player\"", html);
    }

    [Fact]
    public async Task Home_WhenVideoIdIsConfigured_ShouldRenderThePlayerContainer()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"yt-player\"", html);
        Assert.DoesNotContain("youtube.com/embed", html);
    }

    [Theory]
    [InlineData("ExecutionSettings:MaxSessions", "0")]
    [InlineData("ExecutionSettings:MaxDurationSeconds", "3601")]
    [InlineData("ExecutionSettings:DefaultSessions", "5001")]
    [InlineData("YouTubeSettings:VideoId", "not-a-video-id")]
    public void Host_WhenSettingsAreInvalid_ShouldFailValidationOnStart(string key, string value)
    {
        using var factory = CreateFactory(null, (key, value));

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task HealthEndpoints_WhenEnvironmentChanges_ShouldBeMappedOnlyInDevelopment(
        string environment,
        HttpStatusCode expectedStatus
    )
    {
        using var factory = CreateFactory(environment);
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        var alive = await client.GetAsync("/alive");

        Assert.Equal(expectedStatus, health.StatusCode);
        Assert.Equal(expectedStatus, alive.StatusCode);
    }
}
