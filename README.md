# Stream Orchestrator

Stream Orchestrator is a Blazor Interactive Server application that runs many playback sessions at the same time and shows their state while they run. Each session consumes a local media buffer, so the concurrency can be measured and repeated without touching any streaming platform. A separate panel embeds one YouTube video with the official IFrame Player API for manual playback.

## What the sessions do

YouTube is the reference platform, but the concurrent sessions never contact it. Each session reads a generated 1 MiB media buffer in 16 KiB chunks, hashes every chunk with SHA-256, paces the reads to the configured duration and checks the final checksum. This is real work with observable progress, but it is local media consumption, not YouTube playback, and it does not read or change any public view, stream or listener count. Every number in the dashboard, logs and metrics is a local measurement.

The YouTube player demo is independent of the sessions (see [YouTube player demo](#youtube-player-demo)).

## Features

- Start a run with a configurable number of sessions, maximum concurrency and duration. Only one run is active at a time.
- Per session: id, status (Pending, Playing, Completed, Failed, Cancelled), progress, start and end time, error message.
- Summary: total, started, active, completed, failed and cancelled sessions, peak concurrency and elapsed time.
- Cancel a run; active and pending sessions end as Cancelled.
- Optional injected failures, to exercise error handling.
- Application metrics, logs and traces through OpenTelemetry, viewable in the Aspire Dashboard.

## Technology

.NET 11, ASP.NET Core, Blazor Interactive Server, Aspire 17.0.0 (AppHost and ServiceDefaults), OpenTelemetry, xUnit. There is no database and no external service. State is kept in memory and is lost when the application stops.

## Structure

```
src/StreamOrchestrator.Web
  Components/    Blazor dashboard and YouTube player panel
  Enums/         SessionStatus
  Models/        ExecutionRun, PlaybackSession, RunSettings, snapshots
  Orchestration/ SessionOrchestrator
  Services/      PlaybackService, Interfaces/IPlaybackService
  Settings/      ExecutionSettings, YouTubeSettings
src/StreamOrchestrator.AppHost            starts the Web project and the Aspire Dashboard
src/StreamOrchestrator.ServiceDefaults    OpenTelemetry, health checks, resilience
tests/StreamOrchestrator.UnitTests
tests/StreamOrchestrator.IntegrationTests
docs/                                     test report and screenshots
```

`SessionOrchestrator` creates one task per session and a `SemaphoreSlim` limits how many play at once, so no thread is dedicated to a session. `ExecutionRun` holds the state of the current run. Every status change and its counters are updated under one lock, so a snapshot is always consistent. The dashboard polls a snapshot every 500 ms. `IPlaybackService` exists so tests can replace the media workload with a controllable one.

## Prerequisites

- .NET 11 SDK (developed with 11.0.100)
- Aspire CLI 17.0.0, only for `aspire run` ([installation](https://aspire.dev/get-started/install-cli/))

Docker is not needed.

Installation: there is nothing to install besides the prerequisites. Restore and build with:

```powershell
dotnet restore StreamOrchestrator.slnx
dotnet build StreamOrchestrator.slnx --configuration Release
```

## Configuration

`src/StreamOrchestrator.Web/appsettings.json`:

```json
"ExecutionSettings": {
  "DefaultDurationSeconds": 30,
  "DefaultSessions": 100,
  "MaxDurationSeconds": 300,
  "MaxSessions": 1000
},
"YouTubeSettings": {
  "VideoId": "YE7VzlLtp-4"
}
```

`MaxSessions` limits both the session count and the concurrency of a run, and `MaxDurationSeconds` limits the duration of one session. The `Default*` values fill the form. Settings are validated at startup, and run parameters are validated again when a run starts. Values can be overridden with environment variables such as `ExecutionSettings__MaxSessions`. No credentials, tokens or environment variables are required, so there is no example environment file.

## Running

Standalone, without Aspire:

```powershell
dotnet run --project src/StreamOrchestrator.Web --launch-profile http
```

The application listens on http://localhost:5269. Without an OTLP endpoint, telemetry is simply not exported.

With Aspire:

```powershell
aspire run
```

or `dotnet run --project src/StreamOrchestrator.AppHost`. The console prints the Aspire Dashboard URL (http://localhost:15135/login?t=... with a one-time token). The Web project runs as the `stream-orchestrator-web` resource on the same port. The AppHost launch profile sets `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` so the dashboard works over HTTP locally. `/health` and `/alive` are mapped only in the Development environment.

## YouTube player demo

The panel at the bottom of the dashboard embeds one video with the [YouTube IFrame Player API](https://developers.google.com/youtube/iframe_api_reference) through JavaScript interop (`Components/YouTubeDemo.razor`, `wwwroot/youtube-player.js`). It shows the player state, position, duration, ready time, first play time, last state change, end time and any error.

- Playback is started manually with the player's own button. There is no autoplay, repetition or second player.
- The panel is not part of the orchestrator and its playback is not counted in any session metric.
- No API key, OAuth or Google Cloud setup is needed. The browser loads `https://www.youtube.com/iframe_api`, so the panel needs internet access.
- Set the video with `YouTubeSettings:VideoId` (or `YouTubeSettings__VideoId`). Use only a video you own or are authorised to use, because a manual play can count as a normal view. An empty value shows a "No video configured" note.
- The player is destroyed when the component is disposed.

The configured video is "Big Buck Bunny" by the Blender Foundation, licensed under [Creative Commons Attribution 3.0](https://peach.blender.org/about/) ([video](https://www.youtube.com/watch?v=YE7VzlLtp-4)). It is only embedded, not downloaded, copied or modified.

## Metrics and observability

The Aspire Dashboard shows the structured logs (run start and finish, cancellation, failed sessions), ASP.NET Core and runtime metrics, HTTP traces and one `stream_orchestrator.run` span per run with the settings and final counts as tags. The application meter is `StreamOrchestrator`:

| Metric | Type | Meaning |
| --- | --- | --- |
| `stream_orchestrator.sessions.started` | Counter | Sessions that began playing |
| `stream_orchestrator.sessions.completed` | Counter | Sessions that completed |
| `stream_orchestrator.sessions.failed` | Counter | Sessions that failed |
| `stream_orchestrator.sessions.cancelled` | Counter | Sessions that ended Cancelled, including those cancelled before starting |
| `stream_orchestrator.sessions.duration` | Histogram (s) | Duration of sessions that started |
| `stream_orchestrator.sessions.active` | Gauge | Active sessions of the current or last run |
| `stream_orchestrator.sessions.peak_concurrency` | Gauge | Peak concurrency of the current or last run |

Counters and the histogram accumulate for the lifetime of the process. The gauges read the same state as the dashboard, so they describe the current or last run. A run that requests 250 sessions with a limit of 100 and is cancelled ends with started 100 and cancelled 250 (100 cancelled while playing, 150 never started). For a finished run, `Total = Completed + Failed + Cancelled`, and `Started = Active + Completed + Failed + cancelled after start`. A counter that has never been incremented does not appear in the dashboard. No metric uses a session or run id as a tag.

## Tests

```powershell
dotnet test tests/StreamOrchestrator.UnitTests
dotnet test tests/StreamOrchestrator.IntegrationTests
dotnet test StreamOrchestrator.slnx
```

The unit tests (74 cases) use real models and a test double, `GatedPlayback`, that holds every session until the test releases it. That lets a test observe the moment N sessions are active without delays. The integration tests (28 cases) use the real `PlaybackService`, the application's own metrics through `MeterListener`, and the real host through `WebApplicationFactory` in memory. Each telemetry test gets its own `IMeterFactory`, so parallel tests do not see each other's measurements. Neither project needs Aspire, Docker, internet access or YouTube.

Formatting rules are in `.editorconfig`. `dotnet format StreamOrchestrator.slnx --verify-no-changes` checks them, except the convention of one argument per line in multiline calls, which is applied by hand.

### Reproducing the 100-session test

Deterministic test with the test double:

```powershell
dotnet test tests/StreamOrchestrator.UnitTests --filter Start_WhenOneHundredSessionsAreRequested_ShouldKeepAllSessionsActiveSimultaneously
```

Test with the real media workload. It waits until the `stream_orchestrator.sessions.active` gauge reads 100 while all 100 sessions are `Playing`, then checks peak concurrency 100 and 100 completed sessions:

```powershell
dotnet test tests/StreamOrchestrator.IntegrationTests --filter Start_WhenOneHundredSessionsPlayRealMedia_ShouldHaveOneHundredSessionsActiveSimultaneously
```

In the dashboard: start the application, keep Sessions 100, Max concurrent sessions 100 and Duration 30 s, and click Start. Active Sessions and Peak Concurrency reach 100 within a second. When the run ends, Active is 0 and Total equals Completed + Failed + Cancelled.

Measured results and evidence are in [docs/test-report.md](docs/test-report.md).

## Screenshots

- [Dashboard before a run](docs/screenshots/01-dashboard-idle.jpg)
- [100 sessions active at the same time](docs/screenshots/02-sessions-running.jpg)
- [Final result of the 100-session run](docs/screenshots/03-final-result-100-sessions.jpg)
- [Final result with failed sessions](docs/screenshots/04-final-result-with-failures.jpg)
- [Application metrics in the Aspire Dashboard](docs/screenshots/05-aspire-metrics.jpg)
- [YouTube player panel, ready and not started](docs/screenshots/06-youtube-player-demo.jpg)

## Known limitations

- The concurrent sessions are local media consumption. They say nothing about how YouTube or any other platform behaves under load, and they do not exercise a platform player, authentication or rate limits. The YouTube Data API is not used.
- Real playback in the YouTube panel (Playing, Paused, Ended, progress and timestamps) has not been verified. Only the ready state and the video duration were observed.
- Only one run at a time, and state is in memory.
- The dashboard renders every session row, so very large runs make the page heavy. The default limit is 1000 sessions.
- The automated tests do not cover the Blazor UI interaction, the YouTube player in the browser, the Aspire AppHost or the Aspire Dashboard. Those were checked manually.
- xUnit 2.9.3 is marked Legacy on NuGet in favour of xunit.v3.
- The license page of the demonstration video could not be fetched automatically (HTTP 403), so the license and author are stated as provided.

## References

- .NET: https://dotnet.microsoft.com/download/dotnet
- ASP.NET Core and Blazor: https://learn.microsoft.com/aspnet/core/ and https://learn.microsoft.com/aspnet/core/blazor/
- `SemaphoreSlim` and `CancellationToken`: https://learn.microsoft.com/dotnet/api/system.threading.semaphoreslim and https://learn.microsoft.com/dotnet/api/system.threading.cancellationtoken
- OpenTelemetry for .NET: https://opentelemetry.io/docs/languages/dotnet/
- Aspire: https://aspire.dev
- xUnit: https://xunit.net/
- YouTube IFrame Player API: https://developers.google.com/youtube/iframe_api_reference
- YouTube Data API (not used): https://developers.google.com/youtube/v3
- YouTube Terms of Service: https://www.youtube.com/t/terms
