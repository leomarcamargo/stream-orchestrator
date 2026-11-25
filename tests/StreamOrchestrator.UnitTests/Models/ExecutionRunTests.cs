using StreamOrchestrator.Web.Enums;
using StreamOrchestrator.Web.Models;

namespace StreamOrchestrator.UnitTests.Models;

public class ExecutionRunTests
{
    private static ExecutionRun CreateRun(int sessions = 3) => new(new RunSettings(sessions, sessions, 1, 0));

    private static void MoveTo(ExecutionRun run, PlaybackSession session, SessionStatus status)
    {
        switch (status)
        {
            case SessionStatus.Pending:
                break;
            case SessionStatus.Playing:
                run.MarkPlaying(session);
                break;
            default:
                run.MarkPlaying(session);
                Finish(run, session, status);
                break;
        }
    }

    private static void Finish(ExecutionRun run, PlaybackSession session, SessionStatus status)
    {
        switch (status)
        {
            case SessionStatus.Completed:
                run.MarkCompleted(session);
                break;
            case SessionStatus.Failed:
                run.MarkFailed(session, "Playback failed.");
                break;
            case SessionStatus.Cancelled:
                run.MarkCancelled(session);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Not a terminal status.");
        }
    }

    private static RunSummary WithoutElapsed(RunSummary summary) => summary with { Elapsed = TimeSpan.Zero };

    [Fact]
    public void Constructor_WhenRunIsCreated_ShouldInitializePendingSessions()
    {
        using var run = CreateRun(5);

        var snapshot = run.Snapshot();

        Assert.Equal(5, snapshot.Summary.Total);
        Assert.Equal([1, 2, 3, 4, 5], snapshot.Sessions.Select(session => session.Id));
        Assert.All(
            snapshot.Sessions,
            session =>
            {
                Assert.Equal(SessionStatus.Pending, session.Status);
                Assert.Equal(0, session.Progress);
                Assert.Null(session.StartedAt);
                Assert.Null(session.CompletedAt);
                Assert.Null(session.Error);
            }
        );
        Assert.Equal(0, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.Equal(0, snapshot.Summary.Completed);
        Assert.Equal(0, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Cancelled);
        Assert.Equal(0, snapshot.Summary.PeakConcurrency);
        Assert.False(snapshot.Summary.IsFinished);
    }

    [Fact]
    public void MarkPlaying_WhenSessionIsPending_ShouldStartSession()
    {
        using var run = CreateRun();
        var before = DateTimeOffset.UtcNow;

        run.MarkPlaying(run.Sessions[0]);

        var snapshot = run.Snapshot();
        var session = snapshot.Sessions[0];
        Assert.Equal(SessionStatus.Playing, session.Status);
        Assert.InRange(session.StartedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Null(session.CompletedAt);
        Assert.Equal(1, snapshot.Summary.Started);
        Assert.Equal(1, snapshot.Summary.Active);
        Assert.Equal(1, snapshot.Summary.PeakConcurrency);
    }

    [Fact]
    public void MarkCompleted_WhenSessionIsPlaying_ShouldCompleteSession()
    {
        using var run = CreateRun();
        var playing = run.Sessions[0];
        run.MarkPlaying(playing);

        run.MarkCompleted(playing);

        var snapshot = run.Snapshot();
        var session = snapshot.Sessions[0];
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.True(session.CompletedAt >= session.StartedAt);
        Assert.Null(session.Error);
        Assert.Equal(1, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.Equal(1, snapshot.Summary.Completed);
        Assert.Equal(1, snapshot.Summary.PeakConcurrency);
    }

    [Fact]
    public void MarkFailed_WhenSessionFails_ShouldPreserveErrorMessage()
    {
        using var run = CreateRun();
        var playing = run.Sessions[0];
        const string error = "Consumed media does not match the source checksum.";
        run.MarkPlaying(playing);

        run.MarkFailed(playing, error);

        var snapshot = run.Snapshot();
        var session = snapshot.Sessions[0];
        Assert.Equal(SessionStatus.Failed, session.Status);
        Assert.Equal(error, session.Error);
        Assert.True(session.CompletedAt >= session.StartedAt);
        Assert.Equal(1, snapshot.Summary.Failed);
        Assert.Equal(0, snapshot.Summary.Active);
    }

    [Fact]
    public void MarkCancelled_WhenSessionIsPending_ShouldCancelWithoutStarting()
    {
        using var run = CreateRun();

        run.MarkCancelled(run.Sessions[0]);

        var snapshot = run.Snapshot();
        var session = snapshot.Sessions[0];
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Null(session.StartedAt);
        Assert.NotNull(session.CompletedAt);
        Assert.Equal(0, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.Equal(1, snapshot.Summary.Cancelled);
    }

    [Fact]
    public void MarkCancelled_WhenSessionIsPlaying_ShouldCancelAndReleaseActiveSlot()
    {
        using var run = CreateRun();
        var playing = run.Sessions[0];
        run.MarkPlaying(playing);

        run.MarkCancelled(playing);

        var snapshot = run.Snapshot();
        var session = snapshot.Sessions[0];
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.NotNull(session.StartedAt);
        Assert.True(session.CompletedAt >= session.StartedAt);
        Assert.Equal(1, snapshot.Summary.Started);
        Assert.Equal(0, snapshot.Summary.Active);
        Assert.Equal(1, snapshot.Summary.Cancelled);
    }

    [Theory]
    [InlineData(SessionStatus.Playing)]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Failed)]
    [InlineData(SessionStatus.Cancelled)]
    public void MarkPlaying_WhenSessionIsNotPending_ShouldThrow(SessionStatus currentStatus)
    {
        using var run = CreateRun();
        var session = run.Sessions[0];
        MoveTo(run, session, currentStatus);
        var before = run.Snapshot().Summary;

        Assert.Throws<InvalidOperationException>(() => run.MarkPlaying(session));

        Assert.Equal(WithoutElapsed(before), WithoutElapsed(run.Snapshot().Summary));
    }

    [Theory]
    [InlineData(SessionStatus.Completed, SessionStatus.Completed)]
    [InlineData(SessionStatus.Completed, SessionStatus.Failed)]
    [InlineData(SessionStatus.Completed, SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Failed, SessionStatus.Completed)]
    [InlineData(SessionStatus.Failed, SessionStatus.Failed)]
    [InlineData(SessionStatus.Failed, SessionStatus.Cancelled)]
    [InlineData(SessionStatus.Cancelled, SessionStatus.Completed)]
    [InlineData(SessionStatus.Cancelled, SessionStatus.Failed)]
    [InlineData(SessionStatus.Cancelled, SessionStatus.Cancelled)]
    public void Finish_WhenSessionIsAlreadyTerminal_ShouldRejectFurtherTransitions(
        SessionStatus terminalStatus,
        SessionStatus attemptedStatus
    )
    {
        using var run = CreateRun();
        var session = run.Sessions[0];
        MoveTo(run, session, terminalStatus);
        var before = run.Snapshot();

        Assert.Throws<InvalidOperationException>(() => Finish(run, session, attemptedStatus));

        var after = run.Snapshot();
        Assert.Equal(terminalStatus, after.Sessions[0].Status);
        Assert.Equal(before.Sessions[0], after.Sessions[0]);
        Assert.Equal(WithoutElapsed(before.Summary), WithoutElapsed(after.Summary));
    }

    [Fact]
    public void Snapshot_WhenActiveCountDecreases_ShouldPreservePeakConcurrency()
    {
        using var run = CreateRun(4);
        foreach (var session in run.Sessions.Take(3))
        {
            run.MarkPlaying(session);
        }

        run.MarkCompleted(run.Sessions[0]);
        run.MarkCompleted(run.Sessions[1]);
        run.MarkPlaying(run.Sessions[3]);

        var summary = run.Snapshot().Summary;
        Assert.Equal(3, summary.PeakConcurrency);
        Assert.Equal(2, summary.Active);
        Assert.Equal(4, summary.Started);
    }

    [Fact]
    public void GetSummary_WhenSessionsAreInMixedStates_ShouldKeepCountsConsistent()
    {
        using var run = CreateRun(7);
        MoveTo(run, run.Sessions[0], SessionStatus.Playing);
        MoveTo(run, run.Sessions[1], SessionStatus.Playing);
        MoveTo(run, run.Sessions[2], SessionStatus.Completed);
        MoveTo(run, run.Sessions[3], SessionStatus.Failed);
        MoveTo(run, run.Sessions[4], SessionStatus.Cancelled);
        run.MarkCancelled(run.Sessions[5]);

        var snapshot = run.Snapshot();
        var summary = run.GetSummary();
        var pending = snapshot.Sessions.Count(session => session.Status == SessionStatus.Pending);
        var cancelledAfterStart = snapshot.Sessions.Count(
            session => session.Status == SessionStatus.Cancelled && session.StartedAt is not null
        );

        Assert.Equal(7, summary.Total);
        Assert.Equal(5, summary.Started);
        Assert.Equal(2, summary.Active);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.Cancelled);
        Assert.Equal(1, pending);
        Assert.Equal(summary.Total, pending + summary.Active + summary.Completed + summary.Failed + summary.Cancelled);
        Assert.Equal(summary.Started, summary.Active + summary.Completed + summary.Failed + cancelledAfterStart);
    }

    [Fact]
    public void Snapshot_WhenSessionChangesAfterwards_ShouldNotAffectEarlierSnapshot()
    {
        using var run = CreateRun();
        var earlier = run.Snapshot();

        run.MarkPlaying(run.Sessions[0]);

        Assert.Equal(SessionStatus.Pending, earlier.Sessions[0].Status);
        Assert.Equal(0, earlier.Summary.Active);
        Assert.Equal(SessionStatus.Playing, run.Snapshot().Sessions[0].Status);
    }

    [Fact]
    public void MarkFinished_WhenCalled_ShouldMarkRunFinishedAndFreezeElapsedTime()
    {
        using var run = CreateRun();
        Assert.False(run.IsFinished);

        run.MarkFinished();

        var first = run.GetSummary();
        var second = run.GetSummary();
        Assert.True(run.IsFinished);
        Assert.True(first.IsFinished);
        Assert.Equal(first.Elapsed, second.Elapsed);
    }

    [Fact]
    public void Cancel_WhenCalled_ShouldCancelTheRunToken()
    {
        using var run = CreateRun();
        Assert.False(run.CancellationToken.IsCancellationRequested);

        run.Cancel();

        Assert.True(run.CancellationToken.IsCancellationRequested);
    }
}
