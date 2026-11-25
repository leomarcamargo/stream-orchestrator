namespace StreamOrchestrator.IntegrationTests.Support;

/// <summary>
/// Observes state of a real workload that no synchronization point is available for.
/// The timeout is only a safety net so a regression fails the test instead of hanging it.
/// </summary>
internal static class Eventually
{
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);

        while (!condition())
        {
            try
            {
                await Task.Delay(5, timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout.TotalSeconds:N0}s waiting for: {description}"
                );
            }
        }
    }
}
