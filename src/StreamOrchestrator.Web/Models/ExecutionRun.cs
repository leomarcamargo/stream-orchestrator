using StreamOrchestrator.Web.Enums;

namespace StreamOrchestrator.Web.Models;

/// <summary>
/// In-memory state of one test run. Every status change and its matching counter update
/// happen under a single lock, so a snapshot is always internally consistent.
/// </summary>
public sealed class ExecutionRun : IDisposable
{
    private readonly Lock sync = new();
    private readonly PlaybackSession[] sessions;
    private readonly CancellationTokenSource cts = new();
    private int started;
    private int active;
    private int peak;
    private int completed;
    private int failed;
    private int cancelled;
    private DateTimeOffset? finishedAt;

    public ExecutionRun(RunSettings settings)
    {
        Settings = settings;
        StartedAt = DateTimeOffset.UtcNow;
        sessions = Enumerable.Range(1, settings.SessionCount)
            .Select(id => new PlaybackSession(id))
            .ToArray();
    }

    public RunSettings Settings { get; }

    public DateTimeOffset StartedAt { get; }

    public IReadOnlyList<PlaybackSession> Sessions => sessions;

    public CancellationToken CancellationToken => cts.Token;

    // Set once by the orchestrator right after the run starts; never faults.
    public Task Completion { get; internal set; } = Task.CompletedTask;

    public bool IsFinished
    {
        get
        {
            lock (sync)
            {
                return finishedAt is not null;
            }
        }
    }

    public void Cancel() => cts.Cancel();

    public void MarkPlaying(PlaybackSession session)
    {
        lock (sync)
        {
            Transition(session, SessionStatus.Pending, SessionStatus.Playing);
            session.StartedAt = DateTimeOffset.UtcNow;
            started++;
            active++;
            peak = Math.Max(peak, active);
        }
    }

    public void MarkCompleted(PlaybackSession session) => Finish(session, SessionStatus.Completed, null);

    public void MarkFailed(PlaybackSession session, string error) => Finish(session, SessionStatus.Failed, error);

    public void MarkCancelled(PlaybackSession session) => Finish(session, SessionStatus.Cancelled, null);

    public void MarkFinished()
    {
        lock (sync)
        {
            finishedAt = DateTimeOffset.UtcNow;
        }
    }

    public RunSnapshot Snapshot()
    {
        lock (sync)
        {
            var items = sessions
                .Select(session => new SessionSnapshot(
                    session.Id,
                    session.Status,
                    session.Progress,
                    session.StartedAt,
                    session.CompletedAt,
                    session.Error
                ))
                .ToArray();

            return new RunSnapshot(Summarize(), items);
        }
    }

    public RunSummary GetSummary()
    {
        lock (sync)
        {
            return Summarize();
        }
    }

    public void Dispose() => cts.Dispose();

    private RunSummary Summarize()
    {
        var end = finishedAt ?? DateTimeOffset.UtcNow;

        return new RunSummary(
            sessions.Length,
            started,
            active,
            completed,
            failed,
            cancelled,
            peak,
            end - StartedAt,
            finishedAt is not null
        );
    }

    private void Finish(PlaybackSession session, SessionStatus status, string? error)
    {
        lock (sync)
        {
            var wasPlaying = session.Status == SessionStatus.Playing;
            if (!wasPlaying && session.Status != SessionStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"Session {session.Id} is already {session.Status} and cannot become {status}."
                );
            }

            session.Status = status;
            session.CompletedAt = DateTimeOffset.UtcNow;
            session.Error = error;
            if (wasPlaying)
            {
                active--;
            }

            switch (status)
            {
                case SessionStatus.Completed:
                    completed++;
                    break;
                case SessionStatus.Failed:
                    failed++;
                    break;
                case SessionStatus.Cancelled:
                    cancelled++;
                    break;
            }
        }
    }

    private static void Transition(PlaybackSession session, SessionStatus from, SessionStatus to)
    {
        if (session.Status != from)
        {
            throw new InvalidOperationException(
                $"Session {session.Id} is {session.Status}; expected {from} before {to}."
            );
        }

        session.Status = to;
    }
}
