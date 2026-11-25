using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamOrchestrator.IntegrationTests.Support;
using StreamOrchestrator.Web.Enums;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.IntegrationTests.Orchestration;

/// <summary>SessionOrchestrator together with the real PlaybackService and its local media workload.</summary>
public sealed class SessionOrchestratorPlaybackTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly TestMeterFactory meterFactory = new();

    public void Dispose() => meterFactory.Dispose();

    private SessionOrchestrator CreateOrchestrator() => new(
        new PlaybackService(),
        Options.Create(new ExecutionSettings()),
        NullLogger<SessionOrchestrator>.Instance,
        meterFactory
    );

    [Fact]
    public async Task Start_WhenSessionsPlayRealMedia_ShouldCompleteEverySessionWithFullProgress()
    {
        using var orchestrator = CreateOrchestrator();

        var run = orchestrator.Start(new RunSettings(5, 5, 1, 0));
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.Equal(5, snapshot.Summary.Total);
        Assert.Equal(5, snapshot.Summary.Started);
        Assert.Equal(5, snapshot.Summary.Completed);
        Assert.Equal(0, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Cancelled);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.All(
            snapshot.Sessions,
            session =>
            {
                Assert.Equal(SessionStatus.Completed, session.Status);
                Assert.Equal(100, session.Progress);
                Assert.Null(session.Error);
                Assert.True(session.CompletedAt >= session.StartedAt);
            }
        );
    }

    [Fact]
    public async Task Start_WhenSessionsExceedConcurrencyLimit_ShouldRespectTheLimitWithRealMedia()
    {
        using var orchestrator = CreateOrchestrator();
        var maxActiveObserved = 0;

        var run = orchestrator.Start(new RunSettings(12, 4, 1, 0));
        while (!run.Completion.IsCompleted)
        {
            maxActiveObserved = Math.Max(maxActiveObserved, run.GetSummary().Active);
            await Task.Delay(5);
        }

        await run.Completion.WaitAsync(Timeout);
        var summary = run.GetSummary();
        Assert.InRange(maxActiveObserved, 1, 4);
        Assert.Equal(12, summary.Total);
        Assert.Equal(12, summary.Completed);
        Assert.Equal(4, summary.PeakConcurrency);
    }

    [Fact]
    public async Task Cancel_WhenRealWorkloadIsActive_ShouldCancelEverySession()
    {
        using var orchestrator = CreateOrchestrator();
        var run = orchestrator.Start(new RunSettings(50, 50, 30, 0));
        await Eventually.WaitUntilAsync(() => run.GetSummary().Active == 50, Timeout, "all 50 sessions to be active");

        var cancelled = orchestrator.Cancel();
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.True(cancelled);
        Assert.Equal(50, snapshot.Summary.Cancelled);
        Assert.Equal(50, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.All(
            snapshot.Sessions,
            session =>
            {
                Assert.Equal(SessionStatus.Cancelled, session.Status);
                Assert.InRange(session.Progress, 1, 99);
            }
        );
        Assert.True(snapshot.Summary.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Start_WhenEveryPlaybackFails_ShouldMarkSessionsFailedWithErrorMessage()
    {
        using var orchestrator = CreateOrchestrator();

        var run = orchestrator.Start(new RunSettings(5, 5, 1, 100));
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.Equal(5, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Completed);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.All(
            snapshot.Sessions,
            session =>
            {
                Assert.Equal(SessionStatus.Failed, session.Status);
                Assert.Equal("Injected playback failure.", session.Error);
                Assert.InRange(session.Progress, 1, 99);
            }
        );
    }

    [Fact]
    public async Task Start_WhenRunsAreConsecutive_ShouldKeepEachRunIndependent()
    {
        using var orchestrator = CreateOrchestrator();
        var first = orchestrator.Start(new RunSettings(5, 5, 1, 100));
        await first.Completion.WaitAsync(Timeout);
        var firstSummary = first.GetSummary();

        var second = orchestrator.Start(new RunSettings(3, 3, 1, 0));
        await second.Completion.WaitAsync(Timeout);

        var secondSummary = second.GetSummary();
        Assert.Same(second, orchestrator.CurrentRun);
        Assert.Equal(5, firstSummary.Total);
        Assert.Equal(5, firstSummary.Failed);
        Assert.Equal(0, firstSummary.Completed);
        Assert.Equal(3, secondSummary.Total);
        Assert.Equal(0, secondSummary.Failed);
        Assert.Equal(3, secondSummary.Completed);
        Assert.Equal(firstSummary, first.GetSummary());
    }

    [Fact]
    public async Task Start_WhenSessionsFinish_ShouldMatchSessionStatesWithSummary()
    {
        using var orchestrator = CreateOrchestrator();

        var run = orchestrator.Start(new RunSettings(20, 20, 1, 50));
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        var summary = snapshot.Summary;
        int Count(SessionStatus status) => snapshot.Sessions.Count(session => session.Status == status);
        Assert.Equal(summary.Completed, Count(SessionStatus.Completed));
        Assert.Equal(summary.Failed, Count(SessionStatus.Failed));
        Assert.Equal(summary.Cancelled, Count(SessionStatus.Cancelled));
        Assert.Equal(0, Count(SessionStatus.Pending));
        Assert.Equal(0, Count(SessionStatus.Playing));
        Assert.Equal(summary.Total, summary.Completed + summary.Failed);
        Assert.Equal(summary.Started, summary.Active + summary.Completed + summary.Failed);
        Assert.All(
            snapshot.Sessions.Where(session => session.Status == SessionStatus.Failed),
            session => Assert.False(string.IsNullOrEmpty(session.Error))
        );
    }
}
