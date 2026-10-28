using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace RunnerManager;

/// <summary>An installed update that has not proven itself yet, saved in update-state.json.</summary>
public sealed record PendingUpdate
{
    public string Version { get; init; } = string.Empty;
    public string PreviousVersion { get; init; } = string.Empty;
    public DateTime InstalledUtc { get; init; }
    /// <summary>Agent starts of this version that ended without running healthily and without a clean exit.</summary>
    public int FailedStarts { get; init; }
    /// <summary>An agent of this version has started and has not yet become healthy or exited cleanly.</summary>
    public bool Running { get; init; }
}

public sealed record UpdateState
{
    public PendingUpdate? Pending { get; init; }
    /// <summary>The last version that ran healthily after an update.</summary>
    public string GoodVersion { get; init; } = string.Empty;
    /// <summary>The version of the last update that was undone (cleared by the next update).</summary>
    public string RolledBackFrom { get; init; } = string.Empty;
    public string RolledBackTo { get; init; } = string.Empty;
    public bool RollbackReported { get; init; }
}

public enum AgentStartDecision { Nothing, Watch, RollBack }
public enum DashboardUpdateDecision { Nothing, MarkHealthy, RollBack, Forget }

/// <summary>
/// Undoes an update that does not work on this PC. Installing a release keeps the exe it replaces as
/// RunnerManager.exe.previous and marks the update as pending. Each start of the new fleet agent is counted; when it
/// keeps ending before it ran for <see cref="HealthyAfter"/>, or never starts at all, the previous exe is put back.
/// Once an agent has run that long, the version is recorded as good.
/// </summary>
public static class UpdateGuard
{
    /// <summary>How long an agent must run before its version counts as working.</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(2);
    /// <summary>Starts that end early (crashes) before the update is undone.</summary>
    internal const int MaxFailedStarts = 3;
    /// <summary>When no agent of the new version runs this long after the install, the dashboard undoes the update.</summary>
    internal static readonly TimeSpan AgentStartDeadline = TimeSpan.FromMinutes(10);
    /// <summary>The dashboard waits this long after its own start, so an agent that starts at sign-in is not missed.</summary>
    internal static readonly TimeSpan DashboardGrace = TimeSpan.FromMinutes(3);
    /// <summary>The agent must have been seen stopped this long, so a restart in progress is not mistaken for a failure.</summary>
    internal static readonly TimeSpan AgentDownLimit = TimeSpan.FromMinutes(2);

    private static readonly string StatePath = Path.Combine(FleetConfig.StateDir, "update-state.json");
    private static readonly object Gate = new();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    /// <summary>When the dashboard first saw the agent stopped (it checks once a minute), or null while it runs.</summary>
    private static DateTime? _agentDownSinceUtc;

    public static string PreviousExe => AppUpdater.InstalledExe + ".previous";

    // ================= Decisions (pure) =================

    /// <summary>What a starting fleet agent does about a pending update, and the pending update to save.</summary>
    internal static (AgentStartDecision Decision, PendingUpdate? Pending) DecideAgentStart(PendingUpdate? pending, string runningVersion,
        bool runningInstalledCopy)
    {
        if (pending == null || !runningInstalledCopy || AppUpdater.CompareVersions(pending.Version, runningVersion) != 0)
            return (AgentStartDecision.Nothing, pending);
        // The last start neither ran healthily nor exited cleanly: it crashed or was ended.
        var failed = pending.FailedStarts + (pending.Running ? 1 : 0);
        return failed >= MaxFailedStarts
            ? (AgentStartDecision.RollBack, pending with { FailedStarts = failed, Running = false })
            : (AgentStartDecision.Watch, pending with { FailedStarts = failed, Running = true });
    }

    /// <summary>
    /// What a dashboard does about a pending update: forget it when another version was installed since, mark it healthy
    /// on a PC without a fleet agent, or undo it when the new agent never got going.
    /// </summary>
    internal static DashboardUpdateDecision DecideForDashboard(PendingUpdate? pending, string runningVersion, string? installedVersion,
        bool agentInstalled, TimeSpan agentDownFor, DateTime nowUtc, TimeSpan uptime)
    {
        if (pending == null) return DashboardUpdateDecision.Nothing;
        if (installedVersion == null || AppUpdater.CompareVersions(pending.Version, installedVersion) != 0) return DashboardUpdateDecision.Forget;
        if (!agentInstalled)
            return AppUpdater.CompareVersions(pending.Version, runningVersion) == 0 && uptime >= HealthyAfter
                ? DashboardUpdateDecision.MarkHealthy : DashboardUpdateDecision.Nothing;
        // An old agent may still run while it waits for a job to finish; that is no reason to undo anything.
        if (agentDownFor < AgentDownLimit || uptime < DashboardGrace || nowUtc - pending.InstalledUtc < AgentStartDeadline)
            return DashboardUpdateDecision.Nothing;
        return DashboardUpdateDecision.RollBack;
    }

    // ================= State =================

    internal static UpdateState Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StatePath)) ?? new UpdateState();
            }
            catch { }
            return new UpdateState();
        }
    }

    private static void Save(UpdateState state)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(FleetConfig.StateDir);
                var temp = $"{StatePath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, StatePath, overwrite: true);
            }
            catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not save the update state: {ex.Message}"); }
        }
    }

    private static void Change(Func<UpdateState, UpdateState> change)
    {
        lock (Gate) Save(change(Load()));
    }

    /// <summary>Called after a release replaced the installed exe.</summary>
    internal static void MarkPending(string version, string previousVersion)
        => Change(state => state with
        {
            Pending = new PendingUpdate { Version = version, PreviousVersion = previousVersion, InstalledUtc = DateTime.UtcNow },
            RolledBackFrom = string.Empty,
            RolledBackTo = string.Empty
        });

    private static void MarkHealthy(string version)
    {
        Change(state => state.Pending is { } pending && AppUpdater.CompareVersions(pending.Version, version) == 0
            ? state with { Pending = null, GoodVersion = version }
            : state);
        MachineRelay.AppendAgentLog($"Update to v{version} works on this PC.");
    }

    // ================= Agent =================

    /// <summary>
    /// Called when the fleet agent starts. Returns false when the update was undone; the agent then exits, and its task
    /// starts the previous version again.
    /// </summary>
    public static bool OnAgentStarting()
    {
        var state = Load();
        var (decision, pending) = DecideAgentStart(state.Pending, AppUpdater.CurrentVersion, AppUpdater.IsRunningInstalledCopy);
        if (decision == AgentStartDecision.Nothing) return true;
        Save(state with { Pending = pending });
        if (decision == AgentStartDecision.RollBack)
            return !RollBack($"the fleet agent stopped {pending!.FailedStarts} times within {HealthyAfter.TotalMinutes:0} minutes of starting");

        var version = AppUpdater.CurrentVersion;
        _ = Task.Run(async () =>
        {
            await Task.Delay(HealthyAfter);
            MarkHealthy(version);
        });
        return true;
    }

    /// <summary>Called when the fleet agent ends normally, so the exit does not count as a crash.</summary>
    public static void OnAgentExiting()
        => Change(state => state.Pending is { Running: true } pending && AppUpdater.CompareVersions(pending.Version, AppUpdater.CurrentVersion) == 0
            ? state with { Pending = pending with { Running = false } }
            : state);

    // ================= Dashboard =================

    /// <summary>Checks a pending update from the dashboard (see <see cref="DecideForDashboard"/>). Returns a message to show, if any.</summary>
    public static async Task<string?> CheckFromDashboardAsync(bool agentInstalled)
    {
        var state = Load();
        if (state.Pending == null) return null;
        var running = !agentInstalled || await MachineRelay.IsAgentRunningAsync();
        if (running) _agentDownSinceUtc = null;
        else _agentDownSinceUtc ??= DateTime.UtcNow;
        var downFor = _agentDownSinceUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
        switch (DecideForDashboard(state.Pending, AppUpdater.CurrentVersion, AppUpdater.InstalledVersion, agentInstalled, downFor,
                    DateTime.UtcNow, DateTime.UtcNow - StartedUtc))
        {
            case DashboardUpdateDecision.Forget:
                Change(s => s with { Pending = null });
                return null;
            case DashboardUpdateDecision.MarkHealthy:
                MarkHealthy(state.Pending.Version);
                return null;
            case DashboardUpdateDecision.RollBack:
                if (!await Task.Run(() => RollBack($"its fleet agent did not start within {AgentStartDeadline.TotalMinutes:0} minutes"))) return null;
                await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
                return TakeRollbackNotice();
            default:
                return null;
        }
    }

    /// <summary>A message about an update that was undone, once.</summary>
    public static string? TakeRollbackNotice()
    {
        var state = Load();
        if (state.RolledBackFrom.Length == 0 || state.RollbackReported) return null;
        Change(s => s with { RollbackReported = true });
        var restart = AppUpdater.CompareVersions(state.RolledBackFrom, AppUpdater.CurrentVersion) == 0 ? " Restart Runner Manager to use it." : string.Empty;
        return $"The update to v{state.RolledBackFrom} did not work on this PC, so Runner Manager went back to v{state.RolledBackTo}. " +
               $"Details are in the fleet agent log.{restart}";
    }

    /// <summary>Whether this version is one that was just undone (so it must not install itself again).</summary>
    public static bool WasRolledBack(string version)
        => Load().RolledBackFrom is { Length: > 0 } from && AppUpdater.CompareVersions(from, version) == 0;

    // ================= Rollback =================

    /// <summary>Puts RunnerManager.exe.previous back in place. Returns false when there is nothing to go back to.</summary>
    private static bool RollBack(string reason)
    {
        var state = Load();
        var bad = state.Pending?.Version ?? AppUpdater.CurrentVersion;
        if (!File.Exists(PreviousExe))
        {
            MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. There is no previous version to go back to.");
            Save(state with { Pending = null });
            return false;
        }
        string previousVersion;
        try { previousVersion = AppUpdater.Normalize(FileVersionInfo.GetVersionInfo(PreviousExe).ProductVersion ?? string.Empty); }
        catch { previousVersion = state.Pending?.PreviousVersion ?? string.Empty; }
        try
        {
            AppUpdater.RestorePrevious();
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Could not go back to v{previousVersion}: {ex.Message}");
            return false;
        }
        Save(state with { Pending = null, RolledBackFrom = bad, RolledBackTo = previousVersion, RollbackReported = false });
        MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Went back to v{previousVersion}.");
        return true;
    }
}
