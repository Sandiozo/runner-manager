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
    /// <summary>Agent starts of this version so far, so a rollback can say whether it ever started.</summary>
    public int Starts { get; init; }
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
/// RunnerManager.exe.previous and marks the update as pending: in update-state.json, and with RunnerManager.exe.pending
/// beside the exe, without which the pending update does not count (see <see cref="PendingCounts"/>). Each start of the
/// new fleet agent is counted; when it
/// keeps ending before it worked, or never starts at all, the previous exe is put back. Once an agent has run for
/// <see cref="HealthyAfter"/> and answered a status request on this PC, the version is recorded as good; one that runs on
/// for <see cref="AnswerWaitLimit"/> more without answering is kept and no longer watched.
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

    // ================= Whether a pending update counts =================

    /// <summary>
    /// The first version that writes <see cref="PendingMarkerPath"/> when it installs an update. An update that an earlier
    /// version installed has no marker (see <see cref="PendingCounts"/>).
    /// </summary>
    internal const string MarkerSince = "2.10.0";

    /// <summary>How long after an update that a version before <see cref="MarkerSince"/> installed it counts without a marker.</summary>
    internal static readonly TimeSpan UnmarkedLimit = TimeSpan.FromHours(24);

    /// <summary>The file beside the installed exe that names the version of the update that waits to prove itself.</summary>
    internal static string PendingMarkerPath(string exe) => exe + ".pending";

    /// <summary>
    /// What the install folder shows about the last update: the version that <see cref="PendingMarkerPath"/> names (empty
    /// without one), the version of the previous exe (empty without one), and when the installed exe was written. In
    /// Program Files a program without administrator rights can change none of it.
    /// </summary>
    internal sealed record InstallFacts(string MarkerVersion, string PreviousVersion, DateTime? ExeWrittenUtc);

    internal static InstallFacts ReadInstallFacts(string exe)
    {
        var marker = string.Empty;
        try { marker = File.ReadAllText(PendingMarkerPath(exe)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var previous = string.Empty;
        try
        {
            if (File.Exists(exe + ".previous"))
                previous = AppUpdater.Normalize(FileVersionInfo.GetVersionInfo(exe + ".previous").ProductVersion ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        DateTime? written = null;
        try { if (File.Exists(exe)) written = File.GetLastWriteTimeUtc(exe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new InstallFacts(marker, previous, written);
    }

    /// <summary>
    /// Whether a pending update from update-state.json counts, so that its fleet agent is watched and the update can be
    /// undone. That file is in the data folder, which any program of the user can change, so the install folder decides:
    /// the marker that the update wrote beside the exe must name the same version. Versions before
    /// <see cref="MarkerSince"/> wrote no marker. So when the previous exe is older than that (one of them installed the
    /// update), a pending update without a marker counts while the installed exe was written at most
    /// <see cref="UnmarkedLimit"/> ago, which covers the update from 2.9.0.
    /// </summary>
    internal static bool PendingCounts(PendingUpdate pending, InstallFacts facts, DateTime nowUtc)
    {
        if (facts.MarkerVersion.Length > 0) return AppUpdater.CompareVersions(facts.MarkerVersion, pending.Version) == 0;
        return AppUpdater.CompareVersions(facts.PreviousVersion, MarkerSince) < 0
               && facts.ExeWrittenUtc is { } written && nowUtc - written <= UnmarkedLimit;
    }

    /// <summary>The log line for a pending update that does not count (see <see cref="PendingCounts"/>), which is removed.</summary>
    internal static string NotCountedText(string version, string installDir)
        => $"The update state in the data folder names an update to v{version}, but {installDir} has no mark of that update. " +
           "Runner Manager removed it from the update state, so it does not go back to the previous version because of it.";

    internal static void WritePendingMarker(string exe, string version)
    {
        try { File.WriteAllText(PendingMarkerPath(exe), version); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MachineRelay.AppendAgentLog($"Could not mark the update to v{version} in {Path.GetDirectoryName(exe)}, so it is not watched: {ex.Message}");
        }
    }

    internal static void DeletePendingMarker(string exe)
    {
        try { File.Delete(PendingMarkerPath(exe)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

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
            : (AgentStartDecision.Watch, pending with { FailedStarts = failed, Running = true, Starts = pending.Starts + 1 });
    }

    /// <summary>
    /// Why the agent undoes an update after <paramref name="stops"/> starts that ended before it worked. With a fleet code
    /// it works once it answers a status request on this PC; without one, once it has run for <see cref="HealthyAfter"/>.
    /// </summary>
    internal static string AgentStopsReason(int stops, bool answersStatus) => answersStatus
        ? $"the fleet agent stopped {stops} times before it answered a status request on this PC"
        : $"the fleet agent stopped {stops} times within {HealthyAfter.TotalMinutes:0} minutes of starting";

    /// <summary>Why the dashboard undoes an update whose agent is not running: it never started, or it stopped and did not start again.</summary>
    internal static string DashboardRollbackReason(PendingUpdate pending)
        => pending.Starts > 0 || pending.Running || pending.FailedStarts > 0
            ? $"its fleet agent stopped before it worked and was not running again for {AgentDownLimit.TotalMinutes:0} minutes"
            : $"its fleet agent did not start within {AgentStartDeadline.TotalMinutes:0} minutes";

    /// <summary>
    /// Stops watching <paramref name="version"/>: its agent ran for the whole wait without stopping, so later stops (a
    /// restart of the PC, a sign-out) are not failed starts of the update. Another version's pending update stays.
    /// </summary>
    internal static UpdateState StopWatching(UpdateState state, string version)
        => state.Pending is { } pending && AppUpdater.CompareVersions(pending.Version, version) == 0
            ? state with { Pending = null }
            : state;

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

    /// <summary>Changes the saved state. When that removes the pending update, its marker beside the exe goes too.</summary>
    private static void Change(Func<UpdateState, UpdateState> change)
    {
        bool removed;
        lock (Gate)
        {
            var before = Load();
            var after = change(before);
            removed = before.Pending != null && after.Pending == null;
            Save(after);
        }
        if (removed) DeletePendingMarker(AppUpdater.InstalledExe);
    }

    /// <summary>
    /// Called after a release replaced the installed exe. Also writes the marker beside the exe (see
    /// <see cref="PendingCounts"/>). It is written after the state, so a crash in between leaves at worst an update that
    /// is not watched, never a marker that a planted update state could use.
    /// </summary>
    internal static void MarkPending(string version, string previousVersion)
    {
        Change(state => state with
        {
            Pending = new PendingUpdate { Version = version, PreviousVersion = previousVersion, InstalledUtc = DateTime.UtcNow },
            RolledBackFrom = string.Empty,
            RolledBackTo = string.Empty
        });
        WritePendingMarker(AppUpdater.InstalledExe, version);
    }

    private static bool Counts(PendingUpdate pending) => Counts(pending, AppUpdater.InstalledExe, DateTime.UtcNow);

    /// <summary>
    /// Whether the saved pending update counts for the install of <paramref name="exe"/> (see <see cref="PendingCounts"/>).
    /// One that does not is removed from the saved state, any marker beside the exe is deleted, and it is logged.
    /// </summary>
    internal static bool Counts(PendingUpdate pending, string exe, DateTime nowUtc)
    {
        if (PendingCounts(pending, ReadInstallFacts(exe), nowUtc)) return true;
        lock (Gate)
        {
            var state = Load();
            if (state.Pending == pending) Save(state with { Pending = null });
        }
        DeletePendingMarker(exe);
        MachineRelay.AppendAgentLog(NotCountedText(pending.Version, Path.GetDirectoryName(exe) ?? exe));
        return false;
    }

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
        if (decision == AgentStartDecision.Nothing || !Counts(state.Pending!)) return true;
        Save(state with { Pending = pending });
        if (decision == AgentStartDecision.RollBack)
            return !RollBack(AgentStopsReason(pending!.FailedStarts, answersStatus: FleetConfig.Current.FleetKey != null));

        var version = AppUpdater.CurrentVersion;
        _ = Task.Run(async () =>
        {
            if (await WaitUntilAnsweringAsync(AgentAnswersStatusAsync, HealthyAfter, AnswerRetryInterval, AnswerWaitLimit))
            {
                MarkHealthy(version);
                return;
            }
            // It stayed up all this time, so this version starts and runs. A status request can fail for reasons that
            // another version would not fix (for example another program on the fleet port), so the update is kept.
            Change(s => StopWatching(s, version));
            MachineRelay.AppendAgentLog($"The fleet agent of v{version} has run for {(HealthyAfter + AnswerWaitLimit).TotalMinutes:0} minutes " +
                                        "but does not answer status requests on this PC. The update is kept and is no longer watched.");
        });
        return true;
    }

    /// <summary>After <see cref="HealthyAfter"/>, how often the agent asks itself for its status until it answers.</summary>
    internal static readonly TimeSpan AnswerRetryInterval = TimeSpan.FromSeconds(30);
    /// <summary>How long after <see cref="HealthyAfter"/> the agent keeps asking.</summary>
    internal static readonly TimeSpan AnswerWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Waits <paramref name="after"/>, then asks until <paramref name="answers"/> is true (answered) or null (nothing to ask),
    /// for at most <paramref name="limit"/>. Returns false when it never answered.
    /// </summary>
    internal static async Task<bool> WaitUntilAnsweringAsync(Func<Task<bool?>> answers, TimeSpan after, TimeSpan retry, TimeSpan limit)
    {
        await Task.Delay(after);
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            if (await answers() != false) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(retry);
        }
    }

    /// <summary>
    /// Whether this PC's fleet agent answers a status request over the fleet connection, as the dashboard asks it: true when
    /// it answered, false when not, null without a pairing code (nothing can ask it then, so staying up is all it can show).
    /// </summary>
    private static async Task<bool?> AgentAnswersStatusAsync()
    {
        if (FleetConfig.Current.FleetKey == null) return null;
        try
        {
            var response = await FleetNetwork.CallAsync("127.0.0.1", MachineRelay.LocalMachineKey, new FleetRequest { Op = "status" },
                TimeSpan.FromSeconds(30));
            return response.Status != null;
        }
        catch { return false; }
    }

    /// <summary>Called when the fleet agent ends normally, so the exit does not count as a crash.</summary>
    public static void OnAgentExiting()
        => Change(state => state.Pending is { Running: true } pending && AppUpdater.CompareVersions(pending.Version, AppUpdater.CurrentVersion) == 0
            ? state with { Pending = pending with { Running = false } }
            : state);

    // ================= Dashboard =================

    /// <summary>Forgets when the agent was last seen down, after the dashboard restarted it (checks are skipped meanwhile).</summary>
    public static void ForgetAgentDown() => _agentDownSinceUtc = null;

    /// <summary>Checks a pending update from the dashboard (see <see cref="DecideForDashboard"/>). Returns a message to show, if any.</summary>
    public static async Task<string?> CheckFromDashboardAsync(bool agentInstalled)
    {
        // A swap cut off by a crash of the agent leaves no exe for the tasks to start; this dashboard still runs.
        if (await Task.Run(() => RepairMissingExe(tellUser: true)) is { } missing) return missing;
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
            case DashboardUpdateDecision.MarkHealthy when Counts(state.Pending):
                MarkHealthy(state.Pending.Version);
                return null;
            case DashboardUpdateDecision.RollBack when Counts(state.Pending):
                var reason = DashboardRollbackReason(state.Pending);
                if (!await Task.Run(() => RollBack(reason))) return null;
                await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
                return TakeRollbackNotice();
            default:
                return null;
        }
    }

    private static bool _missingLogged;
    private static bool _missingTold;

    /// <summary>
    /// Finishes a swap that was cut off while the exe was renamed away (see <see cref="AppUpdater.RepairMissingExe()"/>) and
    /// records it: a finished update is watched like any other, a finished rollback blocks its version. An exe that went
    /// missing otherwise is logged once, and with <paramref name="tellUser"/> a message for the user is returned once.
    /// </summary>
    public static string? RepairMissingExe(bool tellUser)
    {
        var repair = AppUpdater.RepairMissingExe();
        var swap = repair.Marker;
        var exe = AppPaths.ExeName;
        switch (repair.Source)
        {
            case RepairSource.New:
                MachineRelay.AppendAgentLog($"{exe} was missing after an interrupted update. Put the new version{VersionText(swap?.To)} in its place.");
                if (swap is { Kind: SwapKind.Update, To.Length: > 0 }) MarkPending(swap.To, swap.From);
                return null;
            case RepairSource.Previous:
                MachineRelay.AppendAgentLog($"{exe} was missing after an interrupted {(swap?.Kind is SwapKind.Rollback or SwapKind.Undo ? "rollback" : "update")}. " +
                                            $"Put the previous version{VersionText(swap?.Kind is SwapKind.Rollback or SwapKind.Undo ? swap.To : swap?.From)} back in its place.");
                if (swap is { Kind: SwapKind.Rollback, From.Length: > 0 })
                    Change(s => s with { Pending = null, RolledBackFrom = swap.From, RolledBackTo = swap.To, RollbackReported = false });
                DeletePendingMarker(AppUpdater.InstalledExe);
                return null;
            case RepairSource.Unexplained:
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    MachineRelay.AppendAgentLog(swap == null
                        ? $"{exe} is missing from {AppUpdater.InstallDir}. No update was being installed, so no other version was put in its place."
                        : $"{exe} is missing from {AppUpdater.InstallDir} after an interrupted update, and no copy was left to put in its place.");
                }
                if (!tellUser || _missingTold) return null;
                _missingTold = true;
                return $"{exe} is missing from {AppUpdater.InstallDir}, so Runner Manager and its fleet agent cannot start from there. " +
                       "Run Runner Manager setup again to repair it.";
            default:
                return null;
        }
    }

    private static string VersionText(string? version) => string.IsNullOrEmpty(version) ? string.Empty : $" (v{version})";

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
            DeletePendingMarker(AppUpdater.InstalledExe);
            return false;
        }
        string previousVersion;
        try { previousVersion = AppUpdater.Normalize(FileVersionInfo.GetVersionInfo(PreviousExe).ProductVersion ?? string.Empty); }
        catch { previousVersion = state.Pending?.PreviousVersion ?? string.Empty; }
        try
        {
            AppUpdater.RestorePrevious(new SwapMarker(SwapKind.Rollback, bad, previousVersion));
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Could not go back to v{previousVersion}: {ex.Message}");
            return false;
        }
        Save(state with { Pending = null, RolledBackFrom = bad, RolledBackTo = previousVersion, RollbackReported = false });
        DeletePendingMarker(AppUpdater.InstalledExe);
        MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Went back to v{previousVersion}.");
        return true;
    }
}
