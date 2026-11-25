using StreamOrchestrator.Web.Models;

namespace StreamOrchestrator.Web.Services.Interfaces;

// Exists so tests can replace the media workload with a controllable one.
public interface IPlaybackService
{
    Task PlayAsync(
        PlaybackSession session,
        RunSettings settings,
        CancellationToken cancellationToken
    );
}
