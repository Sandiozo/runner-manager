namespace RunnerManager;

/// <summary>
/// Work the fleet agent is in the middle of (a queued command, an AI assistant call, an approval waiting for the user),
/// so a restart for an update can wait until it is done instead of cutting it off.
/// </summary>
public static class AgentWork
{
    private static int _count;

    /// <summary>Marks work as started; dispose the result when it ends.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        return new Scope();
    }

    public static int Count => Volatile.Read(ref _count);

    public static bool IsIdle => Count == 0;

    /// <summary>Waits until no work is running, or the timeout passes. Returns whether the agent is idle.</summary>
    public static async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsIdle)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(500, cancellationToken);
        }
        return true;
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _count);
        }
    }
}
