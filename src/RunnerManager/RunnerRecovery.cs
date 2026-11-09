namespace RunnerManager;

/// <summary>
/// What auto-recovery remembers about each runner service: whether the agent has seen it running, and when it last
/// restarted it. A runner is restarted when it is seen stopped at two checks in a row, at most
/// <see cref="MaxAttempts"/> times in <see cref="Window"/>; older attempts expire, so recovery resumes after that.
/// </summary>
internal sealed class RunnerRecovery
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public enum Step
    {
        None,
        /// <summary>Start the runner's service.</summary>
        Start,
        /// <summary>Report that recovery stopped for this runner (once, until it may recover again).</summary>
        GiveUp
    }

    private readonly object _gate = new();
    private readonly HashSet<string> _wantRunning = new(StringComparer.OrdinalIgnoreCase);
    // Seen stopped at the last check. A Start or Restart made on this PC's dashboard passes through stopped too.
    private readonly HashSet<string> _seenStopped = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The runner was stopped on purpose (a Stop command, or a move): leave it alone until it runs again.</summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _wantRunning.Remove(key);
            _seenStopped.Remove(key);
        }
    }

    /// <param name="interactive">It runs from a console window, so starting its service would run it twice.</param>
    /// <param name="pending">Its service is starting or stopping: something is acting on it right now.</param>
    /// <param name="mayRecover">Auto-recovery is on, and the user did not stop the runner.</param>
    public Step Check(string key, bool interactive, bool running, bool pending, bool mayRecover, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (interactive)
            {
                Forget(key);
                return Step.None;
            }
            if (running)
            {
                _wantRunning.Add(key);
                _seenStopped.Remove(key);
                // A later give-up is reported again. The attempts stay until they expire: a runner that stops soon
                // after each start would otherwise be restarted without limit.
                _reported.Remove(key);
                return Step.None;
            }
            if (pending || !mayRecover || !_wantRunning.Contains(key))
            {
                _seenStopped.Remove(key);
                return Step.None;
            }
            if (_seenStopped.Add(key)) return Step.None;

            if (!_attempts.TryGetValue(key, out var attempts)) _attempts[key] = attempts = new List<DateTime>();
            attempts.RemoveAll(t => nowUtc - t >= Window);
            if (attempts.Count >= MaxAttempts) return _reported.Add(key) ? Step.GiveUp : Step.None;
            _reported.Remove(key);
            attempts.Add(nowUtc);
            return Step.Start;
        }
    }
}
