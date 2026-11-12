using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RunnerManager;

/// <summary>
/// An installed update that has not proven itself yet, saved in update-state.json. The counts and the install time are
/// a copy there: the file beside the exe decides (see <see cref="UpdateGuard.UpdateMark"/>), except for an update that a
/// version before <see cref="UpdateGuard.MarkerSince"/> installed.
/// </summary>
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
    /// <summary>
    /// The version of the last update that was undone (cleared by the next update). Kept for the message and for 2.9.0
    /// after a rollback to it; what stops the automatic update is the file beside the exe (see <see cref="UpdateGuard.WasRolledBack(string)"/>).
    /// </summary>
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
/// new fleet agent is counted in that file beside the exe (see <see cref="UpdateMark"/>); when it
/// keeps ending before it worked, or never starts at all, the previous exe is put back. Once an agent has run for
/// <see cref="HealthyAfter"/> and answered a status request on this PC, the version is recorded as good; one that runs on
/// for <see cref="AnswerWaitLimit"/> more without answering is kept and no longer watched. Either way the file beside the
/// exe is deleted.
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

    /// <summary>
    /// The file beside the installed exe that records the update that waits to prove itself, or the update that was
    /// undone (see <see cref="UpdateMark"/>). Uninstall and the install of a single file already know this name, so the
    /// record of an undone update is kept in the same file.
    /// </summary>
    internal static string PendingMarkerPath(string exe) => exe + ".pending";

    /// <summary>
    /// What <see cref="PendingMarkerPath"/> records. Either an update that waits to prove itself (<see cref="Pending"/>
    /// names its version), with the values that decide whether it is undone: when it was installed, how often its fleet
    /// agent started, how many of those starts ended before it worked, and whether one runs now. Or an update that was
    /// undone (<see cref="RolledBackFrom"/>), which stops the automatic update of the fleet agent to that version (see
    /// <see cref="WasRolledBack(string)"/>). Only the updater, the fleet agent and the dashboard write it, and in Program
    /// Files only with administrator rights, so a program of the user can change none of it. update-state.json keeps a
    /// copy for the messages, and for versions before <see cref="MarkerSince"/>, which do not read this file.
    /// <para>
    /// In the user's profile any program of the user can change the install folder too, so there this file protects
    /// nothing that update-state.json does not; it is written the same way so that both installs work alike.
    /// </para>
    /// <para>
    /// The first line is the version of the pending update (all that a marker of the first form holds, which counts as no
    /// starts), followed by <c>installed=</c>, <c>starts=</c>, <c>failed=</c> and <c>running=</c> lines. For an undone
    /// update the first line is <c>rolledback=</c> with its version, followed by <c>to=</c> with the version that was put
    /// back.
    /// </para>
    /// </summary>
    internal sealed record UpdateMark
    {
        public string Pending { get; init; } = string.Empty;
        public DateTime? InstalledUtc { get; init; }
        public int Starts { get; init; }
        public int FailedStarts { get; init; }
        public bool Running { get; init; }
        public string RolledBackFrom { get; init; } = string.Empty;
        public string RolledBackTo { get; init; } = string.Empty;
    }

    private const string RolledBackKey = "rolledback=";

    internal static string MarkText(UpdateMark mark) => mark.RolledBackFrom.Length > 0
        ? $"{RolledBackKey}{mark.RolledBackFrom}\nto={mark.RolledBackTo}\n"
        : $"{mark.Pending}\ninstalled={mark.InstalledUtc?.ToString("o", CultureInfo.InvariantCulture)}\nstarts={mark.Starts}\n" +
          $"failed={mark.FailedStarts}\nrunning={(mark.Running ? 1 : 0)}\n";

    /// <summary>Reads <see cref="MarkText"/>. Unknown lines are skipped, and a count that is not a number counts as 0.</summary>
    internal static UpdateMark? ParseMark(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).ToArray();
        if (lines[0].Length == 0) return null;
        string? Value(string key) => lines.Skip(1).FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        int Count(string key) => int.TryParse(Value(key), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
        if (lines[0].StartsWith(RolledBackKey, StringComparison.Ordinal))
            return new UpdateMark { RolledBackFrom = lines[0][RolledBackKey.Length..].Trim(), RolledBackTo = Value("to") ?? string.Empty };
        return new UpdateMark
        {
            Pending = lines[0],
            InstalledUtc = DateTime.TryParse(Value("installed"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var installed) ? installed : null,
            Starts = Count("starts"),
            FailedStarts = Count("failed"),
            Running = Value("running") == "1"
        };
    }

    /// <summary>The record beside <paramref name="exe"/>, or null when there is none or it cannot be read.</summary>
    internal static UpdateMark? ReadMark(string exe)
    {
        try
        {
            // Shared for writing and deleting, so the move that replaces the file (see WriteMark) is never held up.
            using var stream = new FileStream(PendingMarkerPath(exe), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ParseMark(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether <paramref name="mark"/> is the marker of the pending update to <paramref name="version"/>.</summary>
    private static bool Names(UpdateMark? mark, string version)
        => mark is { Pending.Length: > 0 } && AppUpdater.CompareVersions(mark.Pending, version) == 0;

    /// <summary>
    /// Replaces the record beside <paramref name="exe"/> as a whole: written to a file of its own first and then moved
    /// over it, so a reader sees the old record or the new one, never part of one. When that fails, logs
    /// "Could not record <paramref name="what"/> in the install folder" with the reason, and returns false.
    /// </summary>
    private static bool WriteMark(string exe, UpdateMark mark, string what)
    {
        var path = PendingMarkerPath(exe);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, MarkText(mark));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            MachineRelay.AppendAgentLog($"Could not record {what} in {Path.GetDirectoryName(exe)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// What the install folder shows about the last update: the version that <see cref="PendingMarkerPath"/> names (empty
    /// without one, and for the record of an undone update), the version of the previous exe (empty without one), and when
    /// the installed exe was written. In Program Files a program without administrator rights can change none of it.
    /// </summary>
    internal sealed record InstallFacts(string MarkerVersion, string PreviousVersion, DateTime? ExeWrittenUtc);

    internal static InstallFacts ReadInstallFacts(string exe)
    {
        var marker = ReadMark(exe)?.Pending ?? string.Empty;
        var previous = AppUpdater.VersionOfFile(exe + ".previous") ?? string.Empty;
        DateTime? written = null;
        try { if (File.Exists(exe)) written = File.GetLastWriteTimeUtc(exe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new InstallFacts(marker, previous, written);
    }

    /// <summary>
    /// Whether a pending update from update-state.json counts, so that its fleet agent is watched and the update can be
    /// undone. That file is in the data folder, which any program of the user can change, so the install folder decides:
    /// the marker that the update wrote beside the exe must name the same version, and then the counts in the marker
    /// decide (see <see cref="WithMarkedCounts"/>). Versions before
    /// <see cref="MarkerSince"/> wrote no marker. So when the previous exe is older than that (one of them installed the
    /// update), a pending update without a marker counts while the installed exe was written at most
    /// <see cref="UnmarkedLimit"/> ago, which covers the update from 2.9.0; its counts are those in update-state.json.
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

    /// <summary>
    /// <paramref name="pending"/> from update-state.json with the counts and the install time from the marker beside
    /// <paramref name="exe"/>, when that marker names it: those decide, and a program of the user cannot change them. A
    /// marker of the first form, with the version only, counts as no starts since the installed exe was written. Without
    /// such a marker <paramref name="pending"/> stays as it is (see <see cref="PendingCounts"/> for when it counts then).
    /// </summary>
    internal static PendingUpdate WithMarkedCounts(PendingUpdate pending, string exe)
    {
        if (ReadMark(exe) is not { } mark || !Names(mark, pending.Version)) return pending;
        return pending with
        {
            InstalledUtc = mark.InstalledUtc ?? ReadInstallFacts(exe).ExeWrittenUtc ?? pending.InstalledUtc,
            Starts = mark.Starts,
            FailedStarts = mark.FailedStarts,
            Running = mark.Running
        };
    }

    internal static void WritePendingMarker(string exe, string version) => WritePendingMarker(exe, version, DateTime.UtcNow);

    /// <summary>Writes the marker of a new pending update, with no starts. It replaces the record of an undone update.</summary>
    internal static void WritePendingMarker(string exe, string version, DateTime installedUtc)
    {
        if (!WriteMark(exe, new UpdateMark { Pending = version, InstalledUtc = installedUtc }, $"the update to v{version}"))
            MachineRelay.AppendAgentLog($"The update to v{version} is not watched.");
    }

    /// <summary>
    /// Deletes the marker of a pending update beside <paramref name="exe"/>: of <paramref name="version"/>, or of any
    /// version when it is null. The record of an undone update stays.
    /// </summary>
    internal static void DeletePendingMarker(string exe, string? version = null)
    {
        var path = PendingMarkerPath(exe);
        var mark = ReadMark(exe);
        if ((mark == null && !File.Exists(path)) || mark is { RolledBackFrom.Length: > 0 } || (version != null && !Names(mark, version))) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MachineRelay.AppendAgentLog($"Could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>Records beside <paramref name="exe"/> that the update to <paramref name="from"/> was undone, replacing its marker.</summary>
    internal static void WriteRollbackMark(string exe, string from, string to)
        => WriteMark(exe, new UpdateMark { RolledBackFrom = from, RolledBackTo = to }, $"that the update to v{from} was undone");

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
    /// on a PC without a fleet agent, or undo it when the new agent never got going. A dashboard of the version before the
    /// update, which was open while the update was installed, decides the same way with the version of the installed file
    /// (see <see cref="AppUpdater.InstalledVersion"/>): it keeps the update and undoes it when its fleet agent does not
    /// work. Only a dashboard of the new version marks it healthy on a PC without a fleet agent, because only that one has
    /// run it.
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

    /// <summary>
    /// Changes the saved state. The marker beside the exe is not touched: whoever ends the watch of an update deletes its
    /// marker, whatever the saved state says (see <see cref="MarkHealthy(string, string)"/>).
    /// </summary>
    private static void Change(Func<UpdateState, UpdateState> change)
    {
        lock (Gate) Save(change(Load()));
    }

    internal static void MarkPending(string version, string previousVersion)
        => MarkPending(AppUpdater.InstalledExe, version, previousVersion, DateTime.UtcNow);

    /// <summary>
    /// Called after a release replaced the installed exe. Also writes the marker beside the exe (see
    /// <see cref="PendingCounts"/>), which replaces the record of an update that was undone, so an update by hand puts an
    /// undone version back for good. It is written after the state, so a crash in between leaves at worst an update that
    /// is not watched, never a marker that a planted update state could use.
    /// </summary>
    internal static void MarkPending(string exe, string version, string previousVersion, DateTime nowUtc)
    {
        Change(state => state with
        {
            Pending = new PendingUpdate { Version = version, PreviousVersion = previousVersion, InstalledUtc = nowUtc },
            RolledBackFrom = string.Empty,
            RolledBackTo = string.Empty
        });
        WritePendingMarker(exe, version, nowUtc);
    }

    private static bool Counts(PendingUpdate pending) => Counts(pending, AppUpdater.InstalledExe, DateTime.UtcNow);

    /// <summary>
    /// Whether an update waits to prove itself on this PC: a pending update that counts (see <see cref="PendingCounts"/>).
    /// Only reads; the fleet agent's task stays where it is meanwhile (see <see cref="MachineRelay.AgentTaskUsesStartHelper"/>).
    /// </summary>
    internal static bool IsWatching()
        => Load().Pending is { } pending && PendingCounts(pending, ReadInstallFacts(AppUpdater.InstalledExe), DateTime.UtcNow);

    /// <summary>
    /// Whether the saved pending update counts for the install of <paramref name="exe"/> (see <see cref="PendingCounts"/>).
    /// One that does not is removed from the saved state and logged. A marker beside the exe then belongs to another
    /// update and stays.
    /// </summary>
    internal static bool Counts(PendingUpdate pending, string exe, DateTime nowUtc)
    {
        if (PendingCounts(pending, ReadInstallFacts(exe), nowUtc)) return true;
        lock (Gate)
        {
            var state = Load();
            if (state.Pending == pending) Save(state with { Pending = null });
        }
        MachineRelay.AppendAgentLog(NotCountedText(pending.Version, Path.GetDirectoryName(exe) ?? exe));
        return false;
    }

    private static void MarkHealthy(string version) => MarkHealthy(AppUpdater.InstalledExe, version);

    /// <summary>Records <paramref name="version"/> as working, and deletes its marker beside <paramref name="exe"/> whatever the saved state says.</summary>
    internal static void MarkHealthy(string exe, string version)
    {
        Change(state => state.Pending is { } pending && AppUpdater.CompareVersions(pending.Version, version) == 0
            ? state with { Pending = null, GoodVersion = version }
            : state);
        DeletePendingMarker(exe, version);
        MachineRelay.AppendAgentLog($"Update to v{version} works on this PC.");
    }

    /// <summary>Stops watching <paramref name="version"/> (see <see cref="StopWatching(UpdateState, string)"/>), and deletes its marker beside <paramref name="exe"/> whatever the saved state says.</summary>
    internal static void StopWatching(string exe, string version)
    {
        Change(state => StopWatching(state, version));
        DeletePendingMarker(exe, version);
    }

    // ================= Agent =================

    /// <summary>
    /// Called when the fleet agent starts. Returns false when the update was undone; the agent then exits, and its task
    /// starts the previous version again.
    /// </summary>
    public static bool OnAgentStarting()
    {
        var exe = AppUpdater.InstalledExe;
        var version = AppUpdater.CurrentVersion;
        var (decision, pending) = AgentStarting(exe, version, AppUpdater.IsRunningInstalledCopy, DateTime.UtcNow);
        if (decision == AgentStartDecision.Nothing) return true;
        if (decision == AgentStartDecision.RollBack)
            return !RollBack(AgentStopsReason(pending!.FailedStarts, answersStatus: FleetConfig.Current.FleetKey != null));

        _ = Task.Run(async () =>
        {
            if (await WaitUntilAnsweringAsync(AgentAnswersStatusAsync, HealthyAfter, AnswerRetryInterval, AnswerWaitLimit))
            {
                MarkHealthy(exe, version);
                return;
            }
            // It stayed up all this time, so this version starts and runs. A status request can fail for reasons that
            // another version would not fix (for example another program on the fleet port), so the update is kept.
            StopWatching(exe, version);
            MachineRelay.AppendAgentLog($"The fleet agent of v{version} has run for {(HealthyAfter + AnswerWaitLimit).TotalMinutes:0} minutes " +
                                        "but does not answer status requests on this PC. The update is kept and is no longer watched.");
        });
        return true;
    }

    /// <summary>
    /// What a fleet agent of <paramref name="runningVersion"/> that starts does about the update of the install of
    /// <paramref name="exe"/> (see <see cref="DecideAgentStart"/>), with the counts from the marker beside the exe (see
    /// <see cref="WithMarkedCounts"/>). The new counts are written to the marker, and a copy to the saved state. When the
    /// saved state has no pending update, a marker of the running version is deleted: the watch of that update has ended,
    /// and the marker must not make a pending update count that is written into update-state.json later.
    /// </summary>
    internal static (AgentStartDecision Decision, PendingUpdate? Pending) AgentStarting(string exe, string runningVersion,
        bool runningInstalledCopy, DateTime nowUtc)
    {
        var state = Load();
        if (state.Pending is not { } saved)
        {
            if (runningInstalledCopy) DeletePendingMarker(exe, runningVersion);
            return (AgentStartDecision.Nothing, null);
        }
        var marked = Names(ReadMark(exe), saved.Version);
        var (decision, pending) = DecideAgentStart(WithMarkedCounts(saved, exe), runningVersion, runningInstalledCopy);
        if (decision == AgentStartDecision.Nothing || !Counts(saved, exe, nowUtc)) return (AgentStartDecision.Nothing, saved);
        if (marked)
            WriteMark(exe, new UpdateMark
            {
                Pending = pending!.Version, InstalledUtc = pending.InstalledUtc, Starts = pending.Starts,
                FailedStarts = pending.FailedStarts, Running = pending.Running
            }, $"the start of the fleet agent of v{pending.Version}");
        Save(state with { Pending = pending });
        return (decision, pending);
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
    public static void OnAgentExiting() => AgentExiting(AppUpdater.InstalledExe, AppUpdater.CurrentVersion);

    /// <summary>Records in the marker beside <paramref name="exe"/>, and in the saved state, that the agent of <paramref name="version"/> ended normally.</summary>
    internal static void AgentExiting(string exe, string version)
    {
        Change(state => state.Pending is { Running: true } pending && AppUpdater.CompareVersions(pending.Version, version) == 0
            ? state with { Pending = pending with { Running = false } }
            : state);
        if (ReadMark(exe) is { Running: true } mark && Names(mark, version))
            WriteMark(exe, mark with { Running = false }, $"the clean exit of the fleet agent of v{version}");
    }

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
        var exe = AppUpdater.InstalledExe;
        // The install time and the counts in the marker beside the exe decide (see WithMarkedCounts).
        var pending = WithMarkedCounts(state.Pending, exe);
        var running = !agentInstalled || await MachineRelay.IsAgentRunningAsync();
        if (running) _agentDownSinceUtc = null;
        else _agentDownSinceUtc ??= DateTime.UtcNow;
        var downFor = _agentDownSinceUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero;
        switch (DecideForDashboard(pending, AppUpdater.CurrentVersion, AppUpdater.InstalledVersion, agentInstalled, downFor,
                    DateTime.UtcNow, DateTime.UtcNow - StartedUtc))
        {
            case DashboardUpdateDecision.Forget:
                // Another version was installed since: only a marker of the forgotten update goes.
                Change(s => s with { Pending = null });
                DeletePendingMarker(exe, pending.Version);
                return null;
            case DashboardUpdateDecision.MarkHealthy when Counts(state.Pending):
                MarkHealthy(exe, pending.Version);
                return null;
            case DashboardUpdateDecision.RollBack when Counts(state.Pending):
                var reason = DashboardRollbackReason(pending);
                if (!await Task.Run(() => RollBack(reason))) return null;
                // An agent that just ended through the start helper counts as running for a moment longer.
                var (code, output) = await ScheduledTasks.StartAgainAsync(ScheduledTasks.AgentTaskName, MachineRelay.AgentStartWait);
                if (code != 0) MachineRelay.AppendAgentLog($"Could not start the fleet agent after going back to the previous version: {output.Trim()}");
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
                {
                    Change(s => s with { Pending = null, RolledBackFrom = swap.From, RolledBackTo = swap.To, RollbackReported = false });
                    WriteRollbackMark(AppUpdater.InstalledExe, swap.From, swap.To);
                }
                else DeletePendingMarker(AppUpdater.InstalledExe);
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

    /// <summary>
    /// Whether this version is one that was just undone (so it must not install itself again). Only the record beside the
    /// exe counts (see <see cref="UpdateMark"/>), not update-state.json, which any program of the user can change.
    /// </summary>
    public static bool WasRolledBack(string version) => WasRolledBack(AppUpdater.InstalledExe, version, AppUpdater.InstalledVersion);

    /// <summary>
    /// Whether the record beside <paramref name="exe"/> says that the update to <paramref name="version"/> was undone,
    /// while another version is installed. The next update replaces the record; when a version that does not write it
    /// installed <paramref name="version"/> again, the installed version shows that.
    /// </summary>
    internal static bool WasRolledBack(string exe, string version, string? installedVersion)
        => ReadMark(exe) is { RolledBackFrom.Length: > 0 } mark && AppUpdater.CompareVersions(mark.RolledBackFrom, version) == 0
           && (installedVersion == null || AppUpdater.CompareVersions(installedVersion, version) != 0);

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
            DeletePendingMarker(AppUpdater.InstalledExe, bad);
            return false;
        }
        // The version of the file that goes back; when it has none or cannot be read, the one the update state recorded.
        var previousVersion = AppUpdater.VersionOfFile(PreviousExe) ?? state.Pending?.PreviousVersion ?? string.Empty;
        try
        {
            AppUpdater.RestorePrevious(new SwapMarker(SwapKind.Rollback, bad, previousVersion));
        }
        catch (Exception ex)
        {
            MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Could not go back to v{previousVersion}: {ex.Message}");
            return false;
        }
        // The copy in update-state.json is for the message, and for a version before 2.10.0 that was put back; the record
        // beside the exe stops the automatic update to the undone version (see WasRolledBack).
        Save(state with { Pending = null, RolledBackFrom = bad, RolledBackTo = previousVersion, RollbackReported = false });
        WriteRollbackMark(AppUpdater.InstalledExe, bad, previousVersion);
        MachineRelay.AppendAgentLog($"Update to v{bad} failed: {reason}. Went back to v{previousVersion}.");
        return true;
    }
}
