using StreamOrchestrator.Web.Models;
using StreamOrchestrator.Web.Services.Interfaces;

namespace StreamOrchestrator.IntegrationTests.Support;

/// <summary>
/// Test double that holds every session inside PlayAsync until Release is called, so a test can
/// observe the exact moment all expected sessions are active without relying on timing.
/// It tracks its own concurrency counter, independent from the one under test.
/// </summary>
internal sealed class GatedPlayback(
    int expectedArrivals,
    Func<int, bool>? shouldFail = null
) : IPlaybackService
{
    private readonly TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;
    private int current;
    private int max;

    public Task AllArrived => allArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public int MaxObserved => Volatile.Read(ref max);

    public void Release() => release.TrySetResult();

    public async Task PlayAsync(
        PlaybackSession session,
        RunSettings settings,
        CancellationToken cancellationToken
    )
    {
        var now = Interlocked.Increment(ref current);
        RaiseMax(now);

        try
        {
            if (Interlocked.Increment(ref arrived) == expectedArrivals)
            {
                allArrived.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);

            if (shouldFail?.Invoke(session.Id) == true)
            {
                throw new InvalidOperationException($"Session {session.Id} failed.");
            }

            session.Progress = 100;
        }
        finally
        {
            Interlocked.Decrement(ref current);
        }
    }

    private void RaiseMax(int value)
    {
        var seen = Volatile.Read(ref max);

        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref max, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}
