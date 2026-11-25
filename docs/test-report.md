# Test report

## Environment

| Item | Value |
| --- | --- |
| CPU | AMD Ryzen 9 5900X, 12 cores / 24 logical processors |
| Memory | 31.9 GB |
| OS | Windows 11 Pro 10.0.26300 |
| .NET | SDK 11.0.100, runtime 11.0.0 |
| Aspire | 17.0.0 (CLI and packages) |
| Build | Release |
| Browser | Built-in browser pane of the Claude desktop app, viewport 800x1100 |

## Methodology

Three kinds of evidence are kept apart:

- **Deterministic concurrency tests** (unit tests). A test double holds every session inside the playback call until the test releases it, so the test can read the state at the moment N sessions are active. No delays are involved.
- **Real local media workload** (integration tests and manual runs). Each session reads a 1 MiB in-memory buffer in 64 chunks of 16 KiB, hashes every chunk with SHA-256, waits between chunks so the session lasts the configured duration and checks the final hash. This is local processing, not playback on YouTube or Spotify. No platform is contacted and no public metric is read or changed.
- **Manual runs** in the dashboard, under `aspire run` and standalone (`dotnet run --project src/StreamOrchestrator.Web`), compared with the application log and the Aspire Dashboard.

YouTube playback is validated manually and is not part of the concurrency results.

## Automated tests

| Project | Test methods | Cases | Result | Duration |
| --- | --- | --- | --- | --- |
| `StreamOrchestrator.UnitTests` | 33 | 74 | 74 passed | 0.1 to 0.25 s |
| `StreamOrchestrator.IntegrationTests` | 24 | 28 | 28 passed | 7 to 8 s |

No test failed or was skipped. The complete solution was run three times in a row with the same result, and each project was also run repeatedly during development without an intermittent failure. The Release build has 0 warnings and 0 errors, `dotnet format --verify-no-changes` passes, and a copy of the delivery files without `bin`, `obj` or `.vs` restored, built and passed all tests.

What each project covers:

- **Unit tests:** the `ExecutionRun` state machine (valid and invalid transitions, timestamps, peak concurrency, summary invariants), the orchestrator with the test double (100 simultaneous sessions, concurrency limit, pending sessions starting as slots free up, failure isolation, cancelling active and pending sessions, concurrent `Start` and `Cancel`, consecutive runs, disposal) and the settings validation boundaries.
- **Integration tests:** the real `PlaybackService` (full consumption with checksum verification, paced progress, cancellation before and during playback, failures at 0% and 100%), the orchestrator with real media (see below), the application's own metrics through `MeterListener`, and the real host in memory (settings binding, overrides, validation on start, health endpoints per environment).

Line coverage of the Web project was collected with `coverlet.collector` after the final package update: 81.7% overall (379 of 464 lines). `ExecutionRun`, `PlaybackSession`, both settings classes and `Program` are at 100%, `SessionOrchestrator` at 97.2% and `PlaybackService` at 95.5%. The Razor components are lower (`Home` 63.1%, `YouTubeDemo` 26.9%) because they are UI and JavaScript interop. The only uncovered logic is the checksum-mismatch exception, which cannot occur with the deterministic media, and a defensive catch in the orchestrator.

`dotnet list package` reports no vulnerable packages and nothing outdated. The only deprecation notice is xunit 2.9.3 (Legacy, replaced by xunit.v3).

## Concurrency results

Integration test `Start_WhenOneHundredSessionsPlayRealMedia_ShouldHaveOneHundredSessionsActiveSimultaneously`: 100 sessions of 5 s each with the real media. The test waited until the application's `stream_orchestrator.sessions.active` gauge read 100 while all 100 sessions were `Playing`. The run ended with 100 started, 100 completed, 0 active and peak concurrency 100, and every session reported progress 100. It passed in every run, in about 5 s.

Manual runs in the dashboard under Aspire (100 sessions, maximum concurrency 100, 30 s per session):

| Run | Injected failures | Requested | Started | Peak concurrency | Completed | Failed | Cancelled | Duration |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 5% | 100 | 100 | 100 | 98 | 2 | 0 | 30.3 s |
| 2 | 0% | 100 | 100 | 100 | 100 | 0 | 0 | 30.3 s |
| 3 (cancelled after about 3.5 s) | 0% | 100 | 100 | 100 | 0 | 0 | 100 | 3.5 s |

In run 2 the dashboard showed 100 active sessions with every row in `Playing` while the run was in progress, and 100 completed with 0 active at the end. The number of failures in run 1 varies between runs because each session fails with 5% probability.

Screenshots in `docs/screenshots`:

- [01-dashboard-idle.jpg](screenshots/01-dashboard-idle.jpg): dashboard before a run
- [02-sessions-running.jpg](screenshots/02-sessions-running.jpg): run 2 in progress, 100 active sessions, peak 100
- [03-final-result-100-sessions.jpg](screenshots/03-final-result-100-sessions.jpg): final result of run 2
- [04-final-result-with-failures.jpg](screenshots/04-final-result-with-failures.jpg): final result of run 1, with a failed session and its error
- [05-aspire-metrics.jpg](screenshots/05-aspire-metrics.jpg): application metrics in the Aspire Dashboard
- [06-youtube-player-demo.jpg](screenshots/06-youtube-player-demo.jpg): application with the YouTube player ready and not started

## Other scenarios

Observed in the dashboard with the real media workload:

| Scenario | Result |
| --- | --- |
| 1 session | 1 started, 1 completed, peak 1 |
| 10 sessions | 10 active during the run, 10 completed |
| 30 sessions, limit 10 | 10 active and 20 pending during the run, peak 10, all 30 completed |
| 20 sessions, limit 5, 50% injected failures | 13 completed, 7 failed, peak 5 |
| 1 session, 100% injected failures | 1 failed with "Injected playback failure.", 0 active |
| 250 sessions, limit 100, cancelled during playback | 100 started, 150 pending before the cancel, then 250 cancelled, 0 active, peak 100 |
| Consecutive runs | each run shows only its own counts |
| 0 sessions | message "Sessions must be between 1 and 1000." |

## Metrics consistency

After every run Active was 0. For a finished run `Total = Completed + Failed + Cancelled`. The cancelled 250-session run shows why Started and Cancelled differ: 100 sessions were cancelled while playing and 150 were cancelled before they started, so `Started = Active + Completed + Failed + cancelled after start` holds (100 = 0 + 0 + 0 + 100). The metric definitions are in the README.

The application log matched the dashboard for every run. In the last Aspire session, after runs of 5, 100, 20 and 250 requested sessions, the Aspire Dashboard showed started 225, completed 118, failed 7, cancelled 250, peak concurrency 100 and active 0, which equals the sum of those runs. The run spans, HTTP request spans and the runtime and ASP.NET Core metrics were also received. The resource was Running and Healthy, and `/health` and `/alive` returned 200 in the Development environment.

Standalone, `/health`, `/alive`, the page and the static files returned 200, and a 100-session run reached 100 `Playing` sessions and peak concurrency 100.

Automated metric tests use one `IMeterFactory` per test and only listen to its meters, so parallel tests do not see each other's measurements. They cover the counters, the gauges, the duration histogram, the 250 requested sessions with a limit of 100 and cancellation, and consecutive runs.

## YouTube player

With the configured video, the player initialized in both run modes and showed the state "Unstarted" with a duration of 09:57 ([screenshot 06](screenshots/06-youtube-player-demo.jpg)). Playback was not started during validation, so the Playing, Paused and Ended states, position progress and the first play and end timestamps are not verified and need a manual check.

## Limitations

- The results come from one machine and one process, with state in memory and one run at a time. They describe the orchestration and concurrency of the local workload, not the capacity of any streaming platform or any public views or streams.
- The largest run in the dashboard was 250 requested sessions, and it was cancelled. A 100-session run with 30 s sessions ran to completion.
- The failures in the reported runs are injected, not real faults.
- Resource release of the media buffer is not asserted, because nothing about it is observable.
- The automated tests do not cover the Blazor UI interaction, the YouTube player in the browser, the Aspire AppHost or the Aspire Dashboard.
- The Aspire metrics page does not refresh while the browser pane is hidden, and exported values lag the application by the export interval. A reload shows current values.
- `GatedPlayback` and `TestMeterFactory` are duplicated in the two test projects, because a third shared project was not created.
