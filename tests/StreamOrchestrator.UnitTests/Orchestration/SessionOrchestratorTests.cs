using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamOrchestrator.UnitTests.Support;
using StreamOrchestrator.Web.Enums;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Orchestration;
using StreamOrchestrator.Web.Services.Interfaces;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.UnitTests.Orchestration;

public sealed class SessionOrchestratorTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly TestMeterFactory meterFactory = new();

    public void Dispose() => meterFactory.Dispose();

    private SessionOrchestrator CreateOrchestrator(IPlaybackService playback) => new(
        playback,
        Options.Create(new ExecutionSettings()),
        NullLogger<SessionOrchestrator>.Instance,
        meterFactory
    );

    private static bool IsTerminal(SessionSnapshot session) =>
        session.Status is SessionStatus.Completed or SessionStatus.Failed or SessionStatus.Cancelled;

    [Fact]
    public async Task Start_WhenSettingsAreValid_ShouldRunAllSessionsToCompletion()
    {
        var playback = new GatedPlayback(expectedArrivals: 3);
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(3, 3, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.Same(run, orchestrator.CurrentRun);
        Assert.Equal(3, snapshot.Summary.Total);
        Assert.Equal(3, snapshot.Summary.Started);
        Assert.Equal(3, snapshot.Summary.Completed);
        Assert.Equal(0, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Cancelled);
        Assert.True(snapshot.Summary.IsFinished);
        Assert.All(snapshot.Sessions, session => Assert.Equal(100, session.Progress));
    }

    [Theory]
    [InlineData(0, 1, 30, 0)]
    [InlineData(1001, 10, 30, 0)]
    [InlineData(10, 0, 30, 0)]
    [InlineData(10, 1001, 30, 0)]
    [InlineData(10, 10, 0, 0)]
    [InlineData(10, 10, 301, 0)]
    [InlineData(10, 10, 30, -1)]
    [InlineData(10, 10, 30, 101)]
    public void Start_WhenSettingsAreInvalid_ShouldThrowArgumentException(
        int sessions,
        int concurrency,
        int duration,
        int failureRate
    )
    {
        using var orchestrator = CreateOrchestrator(new GatedPlayback(1));

        Assert.Throws<ArgumentException>(
            () => orchestrator.Start(new RunSettings(sessions, concurrency, duration, failureRate))
        );

        Assert.Null(orchestrator.CurrentRun);
    }

    [Theory]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1000, 1000, 300, 0)]
    [InlineData(10, 10, 1, 100)]
    public async Task Start_WhenSettingsAreAtTheLimits_ShouldAcceptTheRun(
        int sessions,
        int concurrency,
        int duration,
        int failureRate
    )
    {
        var playback = new GatedPlayback(expectedArrivals: Math.Min(sessions, concurrency));
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(sessions, concurrency, duration, failureRate));
        await playback.AllArrived;

        Assert.Equal(sessions, run.GetSummary().Total);
        orchestrator.Cancel();
        await run.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Start_WhenOneHundredSessionsAreRequested_ShouldKeepAllSessionsActiveSimultaneously()
    {
        var playback = new GatedPlayback(expectedArrivals: 100);
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(100, 100, 30, 0));
        await playback.AllArrived;

        // All 100 sessions are blocked inside PlayAsync, so none has finished yet.
        var whileActive = run.Snapshot();
        Assert.Equal(100, whileActive.Summary.Active);
        Assert.Equal(100, whileActive.Summary.Started);
        Assert.All(whileActive.Sessions, session => Assert.Equal(SessionStatus.Playing, session.Status));

        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var final = run.Snapshot().Summary;
        Assert.Equal(100, final.PeakConcurrency);
        Assert.Equal(playback.MaxObserved, final.PeakConcurrency);
        Assert.Equal(100, final.Completed);
        Assert.Equal(0, final.Active);
        Assert.True(final.IsFinished);
    }

    [Fact]
    public async Task Start_WhenSessionsExceedConcurrencyLimit_ShouldNeverExceedTheLimit()
    {
        var playback = new GatedPlayback(expectedArrivals: 100);
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(250, 100, 30, 0));
        await playback.AllArrived;

        var whileActive = run.Snapshot();
        Assert.Equal(100, whileActive.Summary.Active);
        Assert.Equal(150, whileActive.Sessions.Count(session => session.Status == SessionStatus.Pending));

        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var final = run.Snapshot().Summary;
        Assert.Equal(250, final.Started);
        Assert.Equal(250, final.Completed);
        Assert.Equal(100, final.PeakConcurrency);
        Assert.Equal(100, playback.MaxObserved);
    }

    [Fact]
    public async Task Start_WhenSlotsBecomeAvailable_ShouldStartPendingSessions()
    {
        var playback = new GatedPlayback(expectedArrivals: 2);
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(5, 2, 30, 0));
        await playback.AllArrived;
        var beforeRelease = run.Snapshot();

        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var final = run.Snapshot().Summary;
        Assert.Equal(2, beforeRelease.Summary.Started);
        Assert.Equal(3, beforeRelease.Sessions.Count(session => session.Status == SessionStatus.Pending));
        Assert.Equal(5, final.Started);
        Assert.Equal(5, final.Completed);
        Assert.Equal(2, final.PeakConcurrency);
    }

    [Fact]
    public async Task Start_WhenSomeSessionsFail_ShouldCompleteTheRemainingSessions()
    {
        var playback = new GatedPlayback(expectedArrivals: 100, shouldFail: id => id % 10 == 0);
        using var orchestrator = CreateOrchestrator(playback);

        var run = orchestrator.Start(new RunSettings(100, 100, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.Equal(90, snapshot.Summary.Completed);
        Assert.Equal(10, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Cancelled);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.Equal(
            snapshot.Summary.Total,
            snapshot.Summary.Completed + snapshot.Summary.Failed + snapshot.Summary.Cancelled
        );
        Assert.All(snapshot.Sessions, session => Assert.True(IsTerminal(session)));
        Assert.All(
            snapshot.Sessions.Where(session => session.Status == SessionStatus.Failed),
            session =>
            {
                Assert.Equal(0, session.Id % 10);
                Assert.Equal($"Session {session.Id} failed.", session.Error);
            }
        );
        Assert.All(
            snapshot.Sessions.Where(session => session.Status == SessionStatus.Completed),
            session => Assert.Null(session.Error)
        );
    }

    [Fact]
    public async Task Cancel_WhenRunHasActiveAndPendingSessions_ShouldCancelAllSessions()
    {
        var playback = new GatedPlayback(expectedArrivals: 100);
        using var orchestrator = CreateOrchestrator(playback);
        var run = orchestrator.Start(new RunSettings(150, 100, 30, 0));
        await playback.AllArrived;

        var cancelled = orchestrator.Cancel();
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.True(cancelled);
        Assert.Equal(150, snapshot.Summary.Cancelled);
        Assert.Equal(100, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.All(
            snapshot.Sessions,
            session =>
            {
                Assert.Equal(SessionStatus.Cancelled, session.Status);
                Assert.NotNull(session.CompletedAt);
            }
        );
        Assert.Equal(100, snapshot.Sessions.Count(session => session.StartedAt is not null));
        Assert.Equal(50, snapshot.Sessions.Count(session => session.StartedAt is null));
    }

    [Fact]
    public async Task Cancel_WhenRunIsFinished_ShouldReturnFalse()
    {
        var playback = new GatedPlayback(expectedArrivals: 2);
        using var orchestrator = CreateOrchestrator(playback);
        var run = orchestrator.Start(new RunSettings(2, 2, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await run.Completion.WaitAsync(Timeout);

        Assert.False(orchestrator.Cancel());
        Assert.False(orchestrator.Cancel());
    }

    [Fact]
    public void Cancel_WhenNoRunExists_ShouldReturnFalse()
    {
        using var orchestrator = CreateOrchestrator(new GatedPlayback(1));

        Assert.False(orchestrator.Cancel());
    }

    [Fact]
    public async Task Start_WhenRunIsAlreadyActive_ShouldRejectNewRunUntilItEnds()
    {
        var playback = new GatedPlayback(expectedArrivals: 5);
        using var orchestrator = CreateOrchestrator(playback);
        var first = orchestrator.Start(new RunSettings(5, 5, 30, 0));
        await playback.AllArrived;

        Assert.Throws<InvalidOperationException>(() => orchestrator.Start(new RunSettings(5, 5, 30, 0)));
        Assert.Same(first, orchestrator.CurrentRun);

        orchestrator.Cancel();
        await first.Completion.WaitAsync(Timeout);
        var second = orchestrator.Start(new RunSettings(5, 5, 30, 0));

        Assert.NotSame(first, second);
        orchestrator.Cancel();
        await second.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Start_WhenPreviousRunCompleted_ShouldStartNewRun()
    {
        var playback = new GatedPlayback(expectedArrivals: 2);
        using var orchestrator = CreateOrchestrator(playback);
        var first = orchestrator.Start(new RunSettings(2, 2, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await first.Completion.WaitAsync(Timeout);

        var second = orchestrator.Start(new RunSettings(4, 4, 30, 0));
        await second.Completion.WaitAsync(Timeout);

        var summary = second.GetSummary();
        Assert.Same(second, orchestrator.CurrentRun);
        Assert.Equal(4, summary.Total);
        Assert.Equal(4, summary.Completed);
    }

    [Fact]
    public async Task Start_WhenRunsAreConsecutive_ShouldMaintainIndependentState()
    {
        var playback = new GatedPlayback(expectedArrivals: 5, shouldFail: id => id == 1);
        using var orchestrator = CreateOrchestrator(playback);
        var first = orchestrator.Start(new RunSettings(5, 5, 30, 0));
        await playback.AllArrived;
        playback.Release();
        await first.Completion.WaitAsync(Timeout);

        // The gate is already open, so the second run finishes immediately; session 1 fails in both runs.
        var second = orchestrator.Start(new RunSettings(3, 3, 30, 0));
        await second.Completion.WaitAsync(Timeout);

        var firstSummary = first.GetSummary();
        var secondSummary = second.GetSummary();
        Assert.Same(second, orchestrator.CurrentRun);
        Assert.Equal(5, firstSummary.Total);
        Assert.Equal(5, firstSummary.Started);
        Assert.Equal(4, firstSummary.Completed);
        Assert.Equal(1, firstSummary.Failed);
        Assert.Equal(3, secondSummary.Total);
        Assert.Equal(3, secondSummary.Started);
        Assert.Equal(2, secondSummary.Completed);
        Assert.Equal(1, secondSummary.Failed);
        Assert.InRange(secondSummary.PeakConcurrency, 1, 3);
        Assert.Equal(0, secondSummary.Active);
        Assert.All(
            new[] { firstSummary, secondSummary },
            summary => Assert.Equal(summary.Total, summary.Completed + summary.Failed + summary.Cancelled)
        );
    }

    [Fact]
    public async Task Start_WhenCalledConcurrently_ShouldAcceptExactlyOneRun()
    {
        var playback = new GatedPlayback(expectedArrivals: 3);
        using var orchestrator = CreateOrchestrator(playback);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await startGate.Task;
                try
                {
                    orchestrator.Start(new RunSettings(3, 3, 30, 0));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }))
            .ToArray();

        startGate.SetResult();
        var accepted = await Task.WhenAll(attempts).WaitAsync(Timeout);
        await playback.AllArrived;

        Assert.Equal(1, accepted.Count(wasAccepted => wasAccepted));
        Assert.Equal(3, orchestrator.CurrentRun!.GetSummary().Total);
        orchestrator.Cancel();
        await orchestrator.CurrentRun.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Cancel_WhenCalledConcurrently_ShouldLeaveRunInConsistentTerminalState()
    {
        var playback = new GatedPlayback(expectedArrivals: 20);
        using var orchestrator = CreateOrchestrator(playback);
        var run = orchestrator.Start(new RunSettings(40, 20, 30, 0));
        await playback.AllArrived;
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await cancelGate.Task;
                return orchestrator.Cancel();
            }))
            .ToArray();

        cancelGate.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(Timeout);
        await run.Completion.WaitAsync(Timeout);

        var snapshot = run.Snapshot();
        Assert.Contains(true, results);
        Assert.Equal(40, snapshot.Summary.Cancelled);
        Assert.Equal(20, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.All(snapshot.Sessions, session => Assert.Equal(SessionStatus.Cancelled, session.Status));
    }

    [Fact]
    public async Task Dispose_WhenRunIsActive_ShouldCancelTheRun()
    {
        var playback = new GatedPlayback(expectedArrivals: 5);
        var orchestrator = CreateOrchestrator(playback);
        var run = orchestrator.Start(new RunSettings(5, 5, 30, 0));
        await playback.AllArrived;

        orchestrator.Dispose();
        await run.Completion.WaitAsync(Timeout);

        var summary = run.GetSummary();
        Assert.Equal(5, summary.Cancelled);
        Assert.Equal(0, summary.Active);
        Assert.True(summary.IsFinished);
    }
}
