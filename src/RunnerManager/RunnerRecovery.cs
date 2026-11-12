namespace RunnerManager;

/// <summary>
/// What auto-recovery remembers about each runner service: whether the agent has seen it running while it was not
/// held, and when it last restarted it. A runner is restarted when it is seen stopped at two checks in a row, at most
/// <see cref="MaxAttempts"/> times in <see cref="Window"/>; older attempts expire, so recovery resumes after that.
/// A service that stays starting or stopping for <see cref="PendingLimit"/> is stuck, and counts as stopped.
/// </summary>
internal sealed class RunnerRecovery
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    /// <summary>Starting or stopping a runner service takes seconds; one still at it after this is stuck.</summary>
    public static readonly TimeSpan PendingLimit = TimeSpan.FromMinutes(2);

    public enum Step
    {
        None,
        /// <summary>Start the runner's service.</summary>
        Start,
        /// <summary>Report that recovery stopped for this runner (once, until it is seen running again).</summary>
        GiveUp
    }

    private readonly object _gate = new();
    private readonly HashSet<string> _wantRunning = new(StringComparer.OrdinalIgnoreCase);
    // Seen stopped at the last check. A Start or Restart made on this PC's dashboard passes through stopped too.
    private readonly HashSet<string> _seenStopped = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pendingSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The runner was stopped on purpose (a Stop command, or a move): leave it alone until it runs again.</summary>
    public void Forget(string key)
    {
        lock (_gate)
        {
            _wantRunning.Remove(key);
            _seenStopped.Remove(key);
            _pendingSince.Remove(key);
        }
    }

    /// <param name="interactive">It runs from a console window, so starting its service would run it twice.</param>
    /// <param name="pending">Its service is starting or stopping: something is acting on it right now.</param>
    /// <param name="mayRecover">
    /// Auto-recovery is on, and the runner is not marked as stopped (by the user, or for the time a change such as an
    /// edit holds auto-recovery). A runner is remembered as one to keep running only when it is seen running while
    /// this is true; the overload with <c>held</c> keeps "auto-recovery is off" apart from the mark.
    /// </param>
    public Step Check(string key, bool interactive, bool running, bool pending, bool mayRecover, DateTime nowUtc)
        => Check(key, interactive, running, pending, watchdogOn: true, held: !mayRecover, nowUtc);

    /// <param name="watchdogOn">Auto-recovery is on. A runner seen running while it is off is still remembered.</param>
    /// <param name="held">
    /// The runner is marked as stopped: the user stopped it, or a change holds auto-recovery for it. An edit holds it
    /// while config.cmd registers the runner again, which starts it, and then stops it again when it was stopped. That
    /// start is not the runner's own state: remembered, it would make the watchdog start the runner after the stop.
    /// </param>
    public Step Check(string key, bool interactive, bool running, bool pending, bool watchdogOn, bool held, DateTime nowUtc)
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
                if (!held) _wantRunning.Add(key);
                _seenStopped.Remove(key);
                _pendingSince.Remove(key);
                // A later give-up is reported again. The attempts stay until they expire: a runner that stops soon
                // after each start would otherwise be restarted without limit.
                _reported.Remove(key);
                return Step.None;
            }
            if (pending)
            {
                if (!_pendingSince.TryGetValue(key, out var since)) _pendingSince[key] = since = nowUtc;
                if (nowUtc - since < PendingLimit)
                {
                    _seenStopped.Remove(key);
                    return Step.None;
                }
            }
            else _pendingSince.Remove(key);
            if (!watchdogOn || held || !_wantRunning.Contains(key))
            {
                _seenStopped.Remove(key);
                return Step.None;
            }
            if (_seenStopped.Add(key)) return Step.None;

            if (!_attempts.TryGetValue(key, out var attempts)) _attempts[key] = attempts = new List<DateTime>();
            attempts.RemoveAll(t => nowUtc - t >= Window);
            if (attempts.Count >= MaxAttempts) return _reported.Add(key) ? Step.GiveUp : Step.None;
            attempts.Add(nowUtc);
            return Step.Start;
        }
    }
}
