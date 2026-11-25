using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamOrchestrator.IntegrationTests.Support;
using StreamOrchestrator.Web.Enums;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.IntegrationTests.Orchestration;

public sealed class ConcurrentPlaybackTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TestMeterFactory meterFactory = new();

    public void Dispose() => meterFactory.Dispose();

    [Fact]
    public async Task Start_WhenOneHundredSessionsPlayRealMedia_ShouldHaveOneHundredSessionsActiveSimultaneously()
    {
        using var orchestrator = new SessionOrchestrator(
            new PlaybackService(),
            Options.Create(new ExecutionSettings()),
            NullLogger<SessionOrchestrator>.Instance,
            meterFactory
        );
        using var metrics = new MetricsCollector(meterFactory);

        // Each session consumes the real media for 5 seconds, much longer than it takes to start all of them.
        var run = orchestrator.Start(new RunSettings(100, 100, 5, 0));
        await Eventually.WaitUntilAsync(
            () => metrics.Gauge("stream_orchestrator.sessions.active") == 100,
            TimeSpan.FromSeconds(4),
            "the active sessions gauge to reach 100"
        );
        var whileActive = run.Snapshot();
        await run.Completion.WaitAsync(Timeout);

        var final = run.Snapshot();
        Assert.Equal(100, whileActive.Summary.Active);
        Assert.All(whileActive.Sessions, session => Assert.Equal(SessionStatus.Playing, session.Status));
        Assert.Equal(100, final.Summary.Started);
        Assert.Equal(100, final.Summary.PeakConcurrency);
        Assert.Equal(100, final.Summary.Completed);
        Assert.Equal(0, final.Summary.Active);
        Assert.Equal(100, metrics.Gauge("stream_orchestrator.sessions.peak_concurrency"));
        Assert.Equal(0, metrics.Gauge("stream_orchestrator.sessions.active"));
        Assert.Equal(100, metrics.Counter("stream_orchestrator.sessions.started"));
        Assert.All(final.Sessions, session => Assert.Equal(100, session.Progress));
    }
}
