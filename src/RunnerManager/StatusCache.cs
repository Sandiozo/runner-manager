namespace RunnerManager;

/// <summary>
/// The agent's status, built at most every <see cref="FreshFor"/> and in the background. Building it reads WMI, the
/// runners' folders and processes, which takes seconds on a busy PC; a request that waited for it could run past the
/// dashboard's timeout (more so over the internet), and the PC would flicker offline. So a request is answered from
/// the last status while the next one is built.
/// </summary>
/// <param name="log">Where failed and slow builds are reported, once until a build works again in time.</param>
internal sealed class StatusCache(Func<Task<MachineStatusPayload>> build, Action<string>? log = null)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);
    /// <summary>
    /// How old a status may be and still be answered as current. An older one (no build succeeded since) keeps the time
    /// it was built, so dashboards don't show frozen runner states as just updated. Well above <see cref="BuildTimeout"/>,
    /// so a PC whose builds are only slow does not look offline.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    /// <summary>How long a request waits for a new status after a change (a command ran) before it takes the last one.</summary>
    internal TimeSpan ChangeWait { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>
    /// A build that takes longer is given up (a WMI query can hang for good), and the next request starts another. A busy
    /// PC can take half a minute for a build that still finishes.
    /// </summary>
    internal TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How many builds may run at once, given-up ones included, so a PC whose WMI hangs doesn't pile up threads.</summary>
    private const int MaxRunningBuilds = 3;
    private readonly object _gate = new();
    private MachineStatusPayload? _last;
    private DateTime _builtUtc = DateTime.MinValue;
    // Bumped by Invalidate; a status built before a change is older than one built after it.
    private int _version;
    private int _lastVersion = -1;
    // Builds are numbered as they start, so a given-up build that finishes late can't replace a newer status.
    private long _started;
    private long _lastBuild = -1;
    // The newest build, which is the only one not given up. One for a change waits for it instead of running beside it.
    private Task<MachineStatusPayload>? _building;
    private int _buildingVersion = -1;
    private DateTime _buildingSinceUtc;
    // The newest build waits for the one before it; it takes the version current when it starts.
    private bool _queued;
    private int _running;
    private string? _problem;

    /// <summary>Something changed (a command ran): the next request waits briefly for a status that shows it.</summary>
    public void Invalidate()
    {
        lock (_gate) _version++;
    }

    public async Task<MachineStatusPayload> GetAsync()
    {
        MachineStatusPayload? last;
        bool current;
        Task<MachineStatusPayload> building;
        string? note = null;
        lock (_gate)
        {
            last = _last;
            current = last != null && _lastVersion == _version;
            if (current && DateTime.UtcNow - _builtUtc <= FreshFor) return last!;
            var idle = _building == null || _building.IsCompleted;
            var hung = !idle && DateTime.UtcNow - _buildingSinceUtc > BuildTimeout;
            if (idle || (hung && _running < MaxRunningBuilds))
            {
                if (hung) note = Problem(SlowText);
                Start(after: null);
            }
            else if (!hung && !_queued && _buildingVersion != _version)
            {
                // Changed while a build runs: the next one starts when it is done.
                Start(after: _building);
            }
            building = _building!;
        }
        if (note != null) log?.Invoke(note);
        // Nothing to answer with yet: wait for the first status.
        if (last == null) return await building.WaitAsync(BuildTimeout);
        // Only old: answer now; the next request gets the new one.
        if (current) return last;
        // Changed since: wait a little for the status that shows the change.
        try
        {
            if (await Task.WhenAny(building, Task.Delay(ChangeWait)) == building) return await building;
        }
        catch { }
        return last;
    }

    private string SlowText => $"Reading this PC's status took more than {(int)BuildTimeout.TotalSeconds} seconds. Trying again.";

    /// <summary>Starts the next build (under the lock), after <paramref name="after"/> when one is still running.</summary>
    private void Start(Task<MachineStatusPayload>? after)
    {
        _queued = after != null;
        _buildingVersion = _version;
        _buildingSinceUtc = DateTime.UtcNow;
        _building = BuildAsync(++_started, after);
        // A failed build is retried by the next request; nobody may be waiting for this one.
        _ = _building.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<MachineStatusPayload> BuildAsync(long number, Task<MachineStatusPayload>? after)
    {
        if (after != null)
        {
            try { await after; } catch { }
        }
        string? note;
        int version;
        var startedUtc = DateTime.UtcNow;
        lock (_gate)
        {
            version = _version;
            _running++;
            if (number == _started && _queued)
            {
                _queued = false;
                _buildingVersion = version;
                _buildingSinceUtc = startedUtc;
            }
        }
        try
        {
            var built = await Task.Run(build);
            lock (_gate)
            {
                if (number > _lastBuild)
                {
                    _last = built;
                    _lastVersion = version;
                    _lastBuild = number;
                    _builtUtc = DateTime.UtcNow;
                }
                // Only a build that finished in time ends a problem: a slow PC is logged once, not at every build.
                var inTime = DateTime.UtcNow - startedUtc <= BuildTimeout;
                note = inTime && _problem != null ? "Reading this PC's status works again." : null;
                if (inTime) _problem = null;
            }
            if (note != null) log?.Invoke(note);
            return built;
        }
        catch (Exception ex)
        {
            lock (_gate) note = Problem($"Could not read this PC's status: {ex.Message}");
            if (note != null) log?.Invoke(note);
            throw;
        }
        finally
        {
            lock (_gate) _running--;
        }
    }

    /// <summary>The text to log for a problem, or null when it is the one logged last (builds are retried every few seconds).</summary>
    private string? Problem(string text)
    {
        if (text == _problem) return null;
        _problem = text;
        return text;
    }
}
