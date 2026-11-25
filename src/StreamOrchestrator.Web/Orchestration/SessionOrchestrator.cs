using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Services.Interfaces;
using StreamOrchestrator.Web.Settings;

namespace StreamOrchestrator.Web.Orchestration;

/// <summary>
/// Starts and cancels test runs. Only one run exists at a time; the last run stays
/// available until the next one starts.
/// </summary>
public sealed class SessionOrchestrator : IDisposable
{
    /// <summary>
    /// Name of the Meter and the ActivitySource; both are registered with OpenTelemetry in Program.cs.
    /// </summary>
    public const string TelemetryName = "StreamOrchestrator";

    private static readonly ActivitySource ActivitySource = new(TelemetryName);

    private readonly IPlaybackService playback;
    private readonly IOptions<ExecutionSettings> options;
    private readonly ILogger<SessionOrchestrator> logger;
    private readonly Lock gate = new();
    private readonly Counter<long> sessionsStarted;
    private readonly Counter<long> sessionsCompleted;
    private readonly Counter<long> sessionsFailed;
    private readonly Counter<long> sessionsCancelled;
    private readonly Histogram<double> sessionDuration;
    private ExecutionRun? current;

    public SessionOrchestrator(
        IPlaybackService playback,
        IOptions<ExecutionSettings> options,
        ILogger<SessionOrchestrator> logger,
        IMeterFactory meterFactory
    )
    {
        this.playback = playback;
        this.options = options;
        this.logger = logger;
        var meter = meterFactory.Create(TelemetryName);

        // Counters and the histogram are cumulative for the lifetime of the process.
        sessionsStarted = meter.CreateCounter<long>("stream_orchestrator.sessions.started", "{session}");
        sessionsCompleted = meter.CreateCounter<long>("stream_orchestrator.sessions.completed", "{session}");
        sessionsFailed = meter.CreateCounter<long>("stream_orchestrator.sessions.failed", "{session}");
        sessionsCancelled = meter.CreateCounter<long>("stream_orchestrator.sessions.cancelled", "{session}");
        sessionDuration = meter.CreateHistogram<double>("stream_orchestrator.sessions.duration", "s");

        // Gauges describe the current (or last) run and read the same state as the dashboard.
        meter.CreateObservableGauge(
            "stream_orchestrator.sessions.active",
            () => CurrentRun?.GetSummary().Active ?? 0,
            "{session}"
        );
        meter.CreateObservableGauge(
            "stream_orchestrator.sessions.peak_concurrency",
            () => CurrentRun?.GetSummary().PeakConcurrency ?? 0,
            "{session}"
        );
    }

    public ExecutionSettings Limits => options.Value;

    public ExecutionRun? CurrentRun
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public ExecutionRun Start(RunSettings settings)
    {
        Validate(settings);

        lock (gate)
        {
            if (current is { IsFinished: false })
            {
                throw new InvalidOperationException("A run is already in progress.");
            }

            current?.Dispose();
            var run = new ExecutionRun(settings);
            run.Completion = Task.Run(() => ExecuteAsync(run));
            current = run;
            return run;
        }
    }

    public bool Cancel()
    {
        lock (gate)
        {
            if (current is not { IsFinished: false })
            {
                return false;
            }

            logger.LogInformation("Cancellation requested for the current run.");
            current.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (current is { IsFinished: false })
            {
                current.Cancel();
            }
            else
            {
                current?.Dispose();
            }
        }
    }

    private async Task ExecuteAsync(ExecutionRun run)
    {
        var settings = run.Settings;

        // One span per run, started as a root so it is not tied to the UI request that clicked Start.
        using var activity = ActivitySource.StartActivity(
            "stream_orchestrator.run",
            ActivityKind.Internal,
            parentContext: default
        );
        activity?.SetTag("run.sessions", settings.SessionCount);
        activity?.SetTag("run.max_concurrency", settings.MaxConcurrency);
        activity?.SetTag("run.duration_seconds", settings.DurationSeconds);
        activity?.SetTag("run.failure_rate_percent", settings.FailureRatePercent);

        logger.LogInformation(
            "Run started: {Sessions} sessions, max concurrency {MaxConcurrency}, {Duration}s per session, "
            + "{FailureRate}% injected failures.",
            settings.SessionCount,
            settings.MaxConcurrency,
            settings.DurationSeconds,
            settings.FailureRatePercent
        );

        try
        {
            using var slots = new SemaphoreSlim(settings.MaxConcurrency);
            await RunAllSessionsAsync(run, slots);
        }
        catch (Exception ex)
        {
            // RunSessionAsync handles its own errors, so reaching this point means a bug.
            logger.LogError(ex, "Run aborted unexpectedly.");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        finally
        {
            run.MarkFinished();
            var summary = run.GetSummary();
            activity?.SetTag("run.started", summary.Started);
            activity?.SetTag("run.completed", summary.Completed);
            activity?.SetTag("run.failed", summary.Failed);
            activity?.SetTag("run.cancelled", summary.Cancelled);
            activity?.SetTag("run.peak_concurrency", summary.PeakConcurrency);
            logger.LogInformation(
                "Run finished in {Elapsed:N1}s: started {Started}, completed {Completed}, failed {Failed}, "
                + "cancelled {Cancelled}, peak concurrency {Peak}.",
                summary.Elapsed.TotalSeconds,
                summary.Started,
                summary.Completed,
                summary.Failed,
                summary.Cancelled,
                summary.PeakConcurrency
            );
        }
    }

    private async Task RunAllSessionsAsync(ExecutionRun run, SemaphoreSlim slots)
    {
        var sessionTasks = new List<Task>(run.Sessions.Count);
        foreach (var session in run.Sessions)
        {
            sessionTasks.Add(RunSessionAsync(run, session, slots));
        }

        await Task.WhenAll(sessionTasks);
    }

    private async Task RunSessionAsync(ExecutionRun run, PlaybackSession session, SemaphoreSlim slots)
    {
        var token = run.CancellationToken;

        try
        {
            await slots.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            run.MarkCancelled(session);
            sessionsCancelled.Add(1);
            return;
        }

        var timer = Stopwatch.StartNew();
        var started = false;

        try
        {
            // A slot released by a cancelled session can reach a waiter before its own cancellation does.
            token.ThrowIfCancellationRequested();
            run.MarkPlaying(session);
            started = true;
            sessionsStarted.Add(1);
            await playback.PlayAsync(session, run.Settings, token);
            run.MarkCompleted(session);
            sessionsCompleted.Add(1);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            run.MarkCancelled(session);
            sessionsCancelled.Add(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Session {SessionId} failed: {Error}", session.Id, ex.Message);
            run.MarkFailed(session, ex.Message);
            sessionsFailed.Add(1);
        }
        finally
        {
            if (started)
            {
                sessionDuration.Record(timer.Elapsed.TotalSeconds);
            }

            slots.Release();
        }
    }

    private void Validate(RunSettings settings)
    {
        var limits = options.Value;

        if (settings.SessionCount < 1 || settings.SessionCount > limits.MaxSessions)
        {
            throw new ArgumentException($"Sessions must be between 1 and {limits.MaxSessions}.");
        }

        if (settings.MaxConcurrency < 1 || settings.MaxConcurrency > limits.MaxSessions)
        {
            throw new ArgumentException($"Max concurrency must be between 1 and {limits.MaxSessions}.");
        }

        if (settings.DurationSeconds < 1 || settings.DurationSeconds > limits.MaxDurationSeconds)
        {
            throw new ArgumentException($"Duration must be between 1 and {limits.MaxDurationSeconds} seconds.");
        }

        if (settings.FailureRatePercent is < 0 or > 100)
        {
            throw new ArgumentException("Failure rate must be between 0 and 100 percent.");
        }
    }
}
