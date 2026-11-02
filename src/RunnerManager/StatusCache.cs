namespace RunnerManager;

/// <summary>
/// The agent's status, built at most every <see cref="FreshFor"/> and in the background. Building it reads WMI, the
/// runners' folders and processes, which takes seconds on a busy PC; a request that waited for it could run past the
/// dashboard's timeout (more so over the internet), and the PC would flicker offline. So a request is answered from
/// the last status while the next one is built.
/// </summary>
internal sealed class StatusCache(Func<Task<MachineStatusPayload>> build)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(2);
    /// <summary>How long a request waits for a new status after a change (a command ran) before it takes the last one.</summary>
    internal TimeSpan ChangeWait { get; init; } = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private MachineStatusPayload? _last;
    private DateTime _builtUtc = DateTime.MinValue;
    // Bumped by Invalidate; a status built before a change is older than one built after it.
    private int _version;
    private int _lastVersion = -1;
    private Task<MachineStatusPayload>? _building;
    private int _buildingVersion = -1;

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
        lock (_gate)
        {
            last = _last;
            current = last != null && _lastVersion == _version;
            if (current && DateTime.UtcNow - _builtUtc <= FreshFor) return last!;
            if (_building == null || _building.IsCompleted || _buildingVersion != _version)
            {
                _buildingVersion = _version;
                _building = BuildAsync(_version);
                // A failed build is retried by the next request; nobody may be waiting for this one.
                _ = _building.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            building = _building;
        }
        // Nothing to answer with yet: wait for the first status.
        if (last == null) return await building;
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

    private async Task<MachineStatusPayload> BuildAsync(int version)
    {
        var built = await Task.Run(build);
        lock (_gate)
        {
            if (version >= _lastVersion)
            {
                _last = built;
                _lastVersion = version;
                _builtUtc = DateTime.UtcNow;
            }
        }
        return built;
    }
}
