namespace RunnerManager;

/// <summary>
/// Work the fleet agent is in the middle of (a queued command, an AI assistant call, an approval waiting for the user),
/// so a restart for an update can wait until it is done instead of cutting it off.
/// </summary>
public static class AgentWork
{
    /// <summary>
    /// Why an AI assistant's change is refused while the fleet agent waits to restart for an update: work that began
    /// after the update asked would be cut off by the restart.
    /// </summary>
    public const string RestartingText =
        "The fleet agent on this PC restarts to finish an update as soon as its current work is done, so it takes no new changes until then. " +
        "Try again in a few minutes.";

    private static int _count;
    private static long _moves;
    private static volatile bool _restartAsked;

    /// <summary>Marks work as started; dispose the result when it ends.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        Interlocked.Increment(ref _moves);
        return new Scope();
    }

    public static int Count => Volatile.Read(ref _count);

    public static bool IsIdle => Count == 0;

    /// <summary>
    /// Grows each time work starts or ends, or moves on while it waits (see <see cref="MovedOn"/>). A restart for an
    /// update starts its wait again when this changes, so work that moves on is not taken for work that is stuck.
    /// </summary>
    public static long Moves => Interlocked.Read(ref _moves);

    /// <summary>
    /// Work that waits moved on without ending: another PC finished a command that was queued before the one this PC
    /// waits for, so that one may only now get its turn.
    /// </summary>
    public static void MovedOn() => Interlocked.Increment(ref _moves);

    /// <summary>
    /// An update asked the fleet agent to restart once its work is done. From then on it takes no new AI assistant
    /// changes (<see cref="RestartingText"/>), and new reads do not hold up the restart.
    /// </summary>
    public static bool RestartAsked => _restartAsked;

    public static void AskRestart() => _restartAsked = true;

    /// <summary>For tests: the agent no longer waits to restart.</summary>
    internal static void ForgetRestart() => _restartAsked = false;

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Increment(ref _moves);
            Interlocked.Decrement(ref _count);
        }
    }
}
