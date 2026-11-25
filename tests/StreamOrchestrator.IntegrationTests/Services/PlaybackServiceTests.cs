using StreamOrchestrator.IntegrationTests.Support;
using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Services;

namespace StreamOrchestrator.IntegrationTests.Services;

public class PlaybackServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly PlaybackService service = new();

    [Fact]
    public async Task PlayAsync_WhenMediaIsFullyConsumed_ShouldCompleteAfterChecksumVerification()
    {
        var session = new PlaybackSession(1);

        // PlayAsync throws InvalidDataException when the consumed media does not match the source checksum,
        // so completing without an exception means the whole workload was read and verified.
        await service.PlayAsync(session, new RunSettings(1, 1, 0, 0), CancellationToken.None);

        Assert.Equal(100, session.Progress);
    }

    [Fact]
    public async Task PlayAsync_WhenPlaybackIsPaced_ShouldAdvanceProgressMonotonically()
    {
        var session = new PlaybackSession(1);
        var samples = new List<int>();

        var playback = service.PlayAsync(session, new RunSettings(1, 1, 1, 0), CancellationToken.None);
        while (!playback.IsCompleted)
        {
            samples.Add(session.Progress);
            await Task.Delay(10);
        }

        await playback.WaitAsync(Timeout);
        Assert.All(samples, progress => Assert.InRange(progress, 0, 100));
        Assert.Equal(samples.Order(), samples);
        Assert.True(samples.Distinct().Count() > 2, "Progress should advance while the media is being consumed.");
        Assert.Equal(100, session.Progress);
    }

    [Fact]
    public async Task PlayAsync_WhenFailureRateIsHundredPercent_ShouldThrowBeforeReachingTheEnd()
    {
        var session = new PlaybackSession(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PlayAsync(session, new RunSettings(1, 1, 0, 100), CancellationToken.None)
        );

        Assert.Equal("Injected playback failure.", exception.Message);
        Assert.InRange(session.Progress, 1, 99);
    }

    [Fact]
    public async Task PlayAsync_WhenCancelledBeforeStart_ShouldThrowWithoutCompleting()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var session = new PlaybackSession(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PlayAsync(session, new RunSettings(1, 1, 5, 0), cancellation.Token)
        );

        Assert.True(session.Progress < 100);
    }

    [Fact]
    public async Task PlayAsync_WhenCancellationIsRequested_ShouldStopConsumingMedia()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new PlaybackSession(1);
        var playback = service.PlayAsync(session, new RunSettings(1, 1, 30, 0), cancellation.Token);
        await Eventually.WaitUntilAsync(() => session.Progress >= 5, Timeout, "playback to advance");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback.WaitAsync(Timeout));
        Assert.InRange(session.Progress, 5, 99);
    }

    [Fact]
    public async Task PlayAsync_WhenCalledConcurrently_ShouldConsumeMediaIndependentlyPerSession()
    {
        var sessions = Enumerable.Range(1, 50)
            .Select(id => new PlaybackSession(id))
            .ToArray();

        await Task.WhenAll(
            sessions.Select(
                session => service.PlayAsync(session, new RunSettings(50, 50, 0, 0), CancellationToken.None)
            )
        ).WaitAsync(Timeout);

        Assert.All(sessions, session => Assert.Equal(100, session.Progress));
    }
}
