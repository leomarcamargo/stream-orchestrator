using StreamOrchestrator.Web.Enums;

namespace StreamOrchestrator.Web.Models;

public sealed record SessionSnapshot(
    int Id,
    SessionStatus Status,
    int Progress,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error
);

public sealed record RunSummary(
    int Total,
    int Started,
    int Active,
    int Completed,
    int Failed,
    int Cancelled,
    int PeakConcurrency,
    TimeSpan Elapsed,
    bool IsFinished
);

public sealed record RunSnapshot(
    RunSummary Summary,
    IReadOnlyList<SessionSnapshot> Sessions
);
