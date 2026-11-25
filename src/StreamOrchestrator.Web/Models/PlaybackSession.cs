using StreamOrchestrator.Web.Enums;

namespace StreamOrchestrator.Web.Models;

public sealed class PlaybackSession(int id)
{
    private int progress;

    public int Id { get; } = id;

    public int Progress
    {
        get => Volatile.Read(ref progress);
        set => Volatile.Write(ref progress, value);
    }

    // Status, timestamps and error are written only by ExecutionRun, under its lock.
    internal SessionStatus Status { get; set; } = SessionStatus.Pending;
    internal DateTimeOffset? StartedAt { get; set; }
    internal DateTimeOffset? CompletedAt { get; set; }
    internal string? Error { get; set; }
}
