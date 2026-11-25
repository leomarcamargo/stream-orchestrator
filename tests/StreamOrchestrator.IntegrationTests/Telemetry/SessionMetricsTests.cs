using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamOrchestrator.IntegrationTests.Support;
using StreamOrchestrator.Web.Enums;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services.Interfaces;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.IntegrationTests.Telemetry;

/// <summary>
/// Validates the application's own instruments. Every test uses its own IMeterFactory, and the collector
/// only listens to the meters created through it, so tests can run in parallel without sharing measurements.
/// </summary>
public sealed class SessionMetricsTests : IDisposable
{
    private const string Started = "stream_orchestrator.sessions.started";
    private const string Completed = "stream_orchestrator.sessions.completed";
    private const string Failed = "stream_orchestrator.sessions.failed";
    private const string Cancelled = "stream_orchestrator.sessions.cancelled";
    private const string Active = "stream_orchestrator.sessions.active";
    private const string PeakConcurrency = "stream_orchestrator.sessions.peak_concurrency";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly TestMeterFactory meterFactory = new();

    public void Dispose() => meterFactory.Dispose();

    private static SessionOrchestrator CreateOrchestrator(IPlaybackService playback, IMeterFactory factory) => new(
        playback,
        Options.Create(new ExecutionSettings()),
        NullLogger<SessionOrchestrator>.Instance,
        factory
    );

    [Fact]
    public async Task Start_WhenRunCompletes_ShouldExportConsistentMetrics()
    {
        var playback = new GatedPlayback(expectedArrivals: 100, shouldFail: id => id % 10 == 0);
        using var orchestrator = CreateOrchestrator(playback, meterFactory);
        using var metrics = new MetricsCollector(meterFactory);

        var run = orchestrator.Start(new RunSettings(100, 100, 30, 0));
        await playback.AllArrived;

        Assert.Equal(100, metrics.Counter(Started));
        Assert.Equal(100, metrics.Gauge(Active));
        Assert.Equal(100, metrics.Gauge(PeakConcurrency));

        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var summary = run.GetSummary();
        Assert.Equal(100, metrics.Counter(Started));
        Assert.Equal(90, metrics.Counter(Completed));
        Assert.Equal(10, metrics.Counter(Failed));
        Assert.Equal(0, metrics.Counter(Cancelled));
        Assert.Equal(0, metrics.Gauge(Active));
        Assert.Equal(100, metrics.Gauge(PeakConcurrency));
        Assert.Equal(100, metrics.DurationRecords);
        Assert.Equal(summary.Started, metrics.Counter(Started));
        Assert.Equal(summary.Completed, metrics.Counter(Completed));
        Assert.Equal(summary.Failed, metrics.Counter(Failed));
        Assert.Equal(summary.Cancelled, metrics.Counter(Cancelled));
        Assert.Equal(summary.Total, metrics.Counter(Completed) + metrics.Counter(Failed) + metrics.Counter(Cancelled));
    }

    [Fact]
    public async Task Cancel_WhenSessionsExceedConcurrencyLimit_ShouldSeparateStartedFromCancelledMetrics()
    {
        var playback = new GatedPlayback(expectedArrivals: 100);
        using var orchestrator = CreateOrchestrator(playback, meterFactory);
        using var metrics = new MetricsCollector(meterFactory);
        var run = orchestrator.Start(new RunSettings(250, 100, 30, 0));
        await playback.AllArrived;
        var activeBeforeCancel = metrics.Gauge(Active);

        orchestrator.Cancel();
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        var summary = snapshot.Summary;
        var cancelledAfterStart = snapshot.Sessions.Count(
            session => session.Status == SessionStatus.Cancelled && session.StartedAt is not null
        );
        var cancelledBeforeStart = snapshot.Sessions.Count(
            session => session.Status == SessionStatus.Cancelled && session.StartedAt is null
        );
        Assert.Equal(100, activeBeforeCancel);
        Assert.Equal(250, summary.Total);
        Assert.Equal(100, summary.Started);
        Assert.Equal(250, summary.Cancelled);
        Assert.Equal(0, summary.Active);
        Assert.Equal(100, cancelledAfterStart);
        Assert.Equal(150, cancelledBeforeStart);
        Assert.Equal(summary.Total, summary.Completed + summary.Failed + summary.Cancelled);
        Assert.Equal(summary.Started, summary.Active + summary.Completed + summary.Failed + cancelledAfterStart);
        Assert.Equal(100, metrics.Counter(Started));
        Assert.Equal(0, metrics.Counter(Completed));
        Assert.Equal(0, metrics.Counter(Failed));
        Assert.Equal(250, metrics.Counter(Cancelled));
        Assert.Equal(0, metrics.Gauge(Active));
        Assert.Equal(100, metrics.Gauge(PeakConcurrency));
        Assert.Equal(100, metrics.DurationRecords);
    }

    [Fact]
    public async Task Start_WhenRunsAreConsecutive_ShouldAccumulateCountersAndReportTheCurrentRunInGauges()
    {
        var playback = new GatedPlayback(expectedArrivals: 3);
        using var orchestrator = CreateOrchestrator(playback, meterFactory);
        using var metrics = new MetricsCollector(meterFactory);
        var first = orchestrator.Start(new RunSettings(3, 3, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await first.Completion.WaitAsync(Timeout);

        var second = orchestrator.Start(new RunSettings(2, 2, 30, 0));
        await second.Completion.WaitAsync(Timeout);

        Assert.Equal(5, metrics.Counter(Started));
        Assert.Equal(5, metrics.Counter(Completed));
        Assert.Equal(second.GetSummary().PeakConcurrency, metrics.Gauge(PeakConcurrency));
        Assert.Equal(0, metrics.Gauge(Active));
    }

    [Fact]
    public async Task Start_WhenAnotherOrchestratorRuns_ShouldNotAffectThisOrchestratorsMetrics()
    {
        using var otherMeterFactory = new TestMeterFactory();
        var playback = new GatedPlayback(expectedArrivals: 4);
        using var orchestrator = CreateOrchestrator(playback, meterFactory);
        using var other = CreateOrchestrator(new GatedPlayback(expectedArrivals: 1), otherMeterFactory);
        using var metrics = new MetricsCollector(meterFactory);
        using var otherMetrics = new MetricsCollector(otherMeterFactory);

        var run = orchestrator.Start(new RunSettings(4, 4, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        Assert.Equal(4, metrics.Counter(Completed));
        Assert.Equal(0, otherMetrics.Counter(Started));
        Assert.Equal(0, otherMetrics.Gauge(PeakConcurrency));
    }
}
