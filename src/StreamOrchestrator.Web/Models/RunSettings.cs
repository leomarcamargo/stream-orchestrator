namespace StreamOrchestrator.Web.Models;

/// <param name="SessionCount">Number of sessions in the run.</param>
/// <param name="MaxConcurrency">Maximum number of sessions allowed to play at the same time.</param>
/// <param name="DurationSeconds">Playback duration of one session.</param>
/// <param name="FailureRatePercent">Percentage of sessions that fail on purpose, to exercise error handling.</param>
public sealed record RunSettings(
    int SessionCount,
    int MaxConcurrency,
    int DurationSeconds,
    int FailureRatePercent
);
