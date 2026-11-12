using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>A Windows user's install, as recorded for administrators: the user's SID and the install folder.</summary>
public sealed record InstallRecord(string Sid, string Folder);

/// <summary>
/// The folder setup offers. <paramref name="Unconfirmed"/>: it may be this user's install, but neither this user's record
/// nor this user's own tasks show it (see <see cref="MachineInstall.SetupProposal(string?, string?, IEnumerable{InstallRecord}, string?, string, string, Func{string, bool})"/>).
/// </summary>
public sealed record SetupOffer(string Folder, bool Unconfirmed);

/// <summary>A scheduled task of Runner Manager: the account it runs as, whether that is this user, and the exe it starts.</summary>
public sealed record TaskUse(string Account, bool IsOwn, string Command);

/// <summary>The scheduled tasks of Runner Manager on this PC; <paramref name="Unreadable"/> when some could not be read.</summary>
public sealed record TaskScan(List<TaskUse> Uses, bool Unreadable);

/// <summary>
/// The other Windows users who use an install: their account names, and <paramref name="Unknown"/> when some scheduled
/// tasks could not be read, so another user may use it.
/// </summary>
public sealed record OtherUsers(List<string> Names, bool Unknown)
{
    /// <summary>Whether the program files must stay: another user uses them, or that cannot be told.</summary>
    public bool KeepFiles => Names.Count > 0 || Unknown;

    /// <summary>Why the files stay, to follow "… were kept, because": <paramref name="what"/> is "it" or "them".</summary>
    public string KeptBecause(string what) => Names.Count > 0
        ? $"other Windows users on this PC use {what}: {string.Join(", ", Names)}"
        : $"Runner Manager could not read all scheduled tasks on this PC, so other Windows users may use {what}";
}

/// <summary>The facts the install folder is decided from (see <see cref="MachineInstall.Resolve"/>).</summary>
/// <param name="OwnRecord">This user's recorded install folder, when it exists.</param>
/// <param name="UserEntry">The folder in this user's Settings → Apps entry, when it exists. Any program of the user can change it.</param>
/// <param name="RunningExe">The exe of this process.</param>
/// <param name="Elevated">Whether this process runs with administrator rights.</param>
public sealed record InstallFacts(string? OwnRecord, string? UserEntry, string RunningExe, bool Elevated,
    string ProgramFilesDir, string OldDefaultDir, Func<string, bool> HasExe);

/// <summary>Where the install records are kept: the registry, except in tests.</summary>
internal interface IInstallRecordStore
{
    IReadOnlyList<InstallRecord> ReadAll();

    /// <summary>Records <paramref name="folder"/> for the user with this SID; null removes the record.</summary>
    void Write(string sid, string? folder);
}

/// <summary>
/// Where Runner Manager is installed, as code with administrator rights sees it. The dashboard and the fleet agent run as
/// administrator from a scheduled task, so the folder they update and point the tasks at must never come from a place that
/// a program without administrator rights can change, such as the Settings → Apps entry under HKCU.
/// <list type="bullet">
/// <item>New installs go to Program Files\RunnerManager, where only administrators can change files.</item>
/// <item>Each Windows user's install folder is recorded under HKLM\SOFTWARE\RunnerManager\Installs (value name: the user's
/// SID), which only administrators can change. Setup writes it. Installs of earlier versions get it once, on an elevated
/// start, from the folder of the exe that this user's own scheduled tasks with the highest rights start.</item>
/// <item>Installs that exist stay where they are. Nothing is moved unless the user chooses another folder in setup.</item>
/// <item>Program Files is shared by the Windows users of the PC: a second user's setup uses the install that is there.</item>
/// </list>
/// </summary>
public static class MachineInstall
{
    internal static IInstallRecordStore Records { get; set; } = new RegistryInstallRecordStore();

    /// <summary>
    /// %ProgramFiles%\RunnerManager. Runner Manager is a 64-bit program (published for win-x64), so this is the 64-bit
    /// Program Files folder, never Program Files (x86). The folder is fixed by Windows; a user cannot redirect it.
    /// </summary>
    public static string ProgramFilesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RunnerManager");

    /// <summary>Where versions up to 2.9.0 installed by default: %LOCALAPPDATA%\Programs\RunnerManager, in the user's profile.</summary>
    public static string OldDefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RunnerManager");

    internal static bool IsOldDefaultDir(string folder) => AppUpdater.SameFolder(folder, OldDefaultDir);

    /// <summary>The folders that only ever hold Runner Manager: Program Files\RunnerManager and the default of earlier versions.</summary>
    internal static bool HoldsOnlyThisApp(string folder) => AppUpdater.IsDefaultInstallDir(folder) || IsOldDefaultDir(folder);

    internal static bool HasExe(string folder) => File.Exists(Path.Combine(folder, AppPaths.ExeName));

    // ================= Which folder is the install =================

    /// <summary>
    /// The install folder of this process. In this order: this user's recorded install; the folder of the running copy
    /// when it is an install (see <see cref="RunningInstallFolder"/>); for a copy without administrator rights (an AI tool's
    /// host) the Settings → Apps entry, or the default of earlier versions when it holds the exe; otherwise Program Files.
    /// So the Settings → Apps entry and the files in the default of earlier versions, which any program of the user can
    /// change, never decide it for a copy with administrator rights: they can only confirm the folder that copy runs from.
    /// An install of an earlier version that this user's tasks start is recorded before (see <see cref="RecordRunningInstallAsync(Action{string})"/>).
    /// </summary>
    internal static string Resolve(InstallFacts f)
    {
        if (f.OwnRecord != null) return f.OwnRecord;
        if (RunningInstallFolder(f) is { } running) return running;
        if (f.Elevated) return f.ProgramFilesDir;
        if (f.UserEntry != null) return f.UserEntry;
        return f.HasExe(f.OldDefaultDir) ? f.OldDefaultDir : f.ProgramFilesDir;
    }

    /// <summary>
    /// The folder of the running RunnerManager.exe when it may be taken for the install of this process: Program
    /// Files\RunnerManager, the default of earlier versions, or the folder named in this user's Settings → Apps entry. A
    /// build folder or a downloaded setup is none. Only <see cref="Resolve"/> uses this; the record needs more (see
    /// <see cref="FolderToRecord"/>).
    /// </summary>
    internal static string? RunningInstallFolder(InstallFacts f)
    {
        if (!Path.GetFileName(f.RunningExe).Equals(AppPaths.ExeName, StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Path.GetDirectoryName(f.RunningExe);
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder)) return null;
        var isInstall = AppUpdater.SameFolder(folder, f.ProgramFilesDir) || AppUpdater.SameFolder(folder, f.OldDefaultDir)
                        || f.UserEntry != null && AppUpdater.SameFolder(folder, f.UserEntry);
        return isInstall ? Path.TrimEndingDirectorySeparator(folder) : null;
    }

    private static bool SamePath(string a, string b)
        => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folder to record for this user, who has no record yet (an install of an earlier version): the folder of the
    /// exe that this user's own tasks with the highest rights start (<paramref name="ownTaskExes"/>, see
    /// <see cref="OwnTaskExesAsync"/>). Only administrators can change those tasks; the Settings → Apps entry, the default
    /// of earlier versions and Program Files say nothing about which copy is this user's install. The running exe comes
    /// first when the tasks start it; otherwise (a build folder, a downloaded copy) the first of the tasks' exes whose
    /// folder exists. Null when there is no such task.
    /// </summary>
    internal static string? FolderToRecord(string runningExe, IEnumerable<string> ownTaskExes, Func<string, bool> folderExists)
    {
        var installed = ownTaskExes.Where(IsInstalledExePath).ToList();
        var exe = installed.Any(e => SamePath(e, runningExe)) ? runningExe
            : installed.FirstOrDefault(e => folderExists(Path.GetDirectoryName(e)!));
        return exe == null ? null : Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(exe))!);
    }

    /// <summary>A full path to RunnerManager.exe, or to RunnerWidget.exe of the versions before 2.2.</summary>
    private static bool IsInstalledExePath(string exe)
        => Path.IsPathFullyQualified(exe) && Path.GetFileName(exe) is var name
           && (name.Equals(AppPaths.ExeName, StringComparison.OrdinalIgnoreCase) || name.Equals(AppPaths.LegacyExeName, StringComparison.OrdinalIgnoreCase));

    private static string? _resolved;
    private static readonly Lazy<bool> ElevatedProcess = new(() => Elevation.IsElevated);

    /// <summary>The install folder of this process (see <see cref="Resolve"/>). Read once, and again after this process changes the record.</summary>
    internal static string CurrentInstallDir() => _resolved ??= Resolve(CurrentFacts());

    internal static void Forget() => _resolved = null;

    private static InstallFacts CurrentFacts() => new(ReadOwnRecord(), Installer.RegisteredInstallDir(), Environment.ProcessPath ?? string.Empty,
        ElevatedProcess.Value, ProgramFilesDir, OldDefaultDir, HasExe);

    /// <summary>
    /// Records the folder of an install of an earlier version once, on an elevated start, before anything takes the
    /// install folder: the folder of the exe that this user's own tasks start (see <see cref="FolderToRecord"/>), also
    /// when this copy is not the install (a build folder or a downloaded copy). Without such a task nothing is recorded.
    /// </summary>
    internal static Task RecordRunningInstallAsync(Action<string> log)
        => RecordRunningInstallAsync(ElevatedProcess.Value, Environment.ProcessPath ?? string.Empty, log);

    internal static async Task RecordRunningInstallAsync(bool elevated, string runningExe, Action<string> log)
    {
        try
        {
            if (!elevated || ReadOwnRecord() != null) return;
            if (FolderToRecord(runningExe, await OwnTaskExesAsync(), Directory.Exists) is not { } folder) return;
            WriteOwnRecord(folder);
            log($"Recorded {folder} as the install folder of Runner Manager for {Elevation.CurrentUserName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            log($"Could not record the install folder of Runner Manager: {ex.Message}");
        }
    }

    /// <summary>
    /// The exes that this user's launch, startup and fleet agent tasks start, and those of RunnerWidget's tasks (before
    /// 2.2). Only tasks that run with the highest rights count (see <see cref="ScheduledTasks.ExeOfOwnElevatedTask"/>).
    /// </summary>
    internal static async Task<List<string>> OwnTaskExesAsync()
    {
        var exes = new List<string>();
        foreach (var name in new[] { ScheduledTasks.LaunchTaskName, ScheduledTasks.DashboardTaskName, ScheduledTasks.AgentTaskName,
                     ScheduledTasks.LegacyAgentTaskName, ScheduledTasks.LegacyDashboardTaskName })
        {
            if (await Tasks.ReadAsync(name) is not { Length: > 0 } xml) continue;
            if (ScheduledTasks.ExeOfOwnElevatedTask(xml) is { } command && Path.IsPathFullyQualified(command)) exes.Add(command);
        }
        return exes;
    }

    // ================= The records =================

    /// <summary>This user's recorded install folder, when it is a full path that exists.</summary>
    internal static string? ReadOwnRecord()
        => ReadOwnRecordRaw() is { } folder && Path.IsPathFullyQualified(folder) && Directory.Exists(folder)
            ? Path.TrimEndingDirectorySeparator(folder) : null;

    /// <summary>This user's record as it is stored, also when the folder is gone (setup puts it back after a failure).</summary>
    internal static string? ReadOwnRecordRaw() => RecordOf(Records.ReadAll(), Elevation.CurrentUserSid);

    internal static string? RecordOf(IEnumerable<InstallRecord> records, string sid)
        => records.FirstOrDefault(r => r.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase))?.Folder;

    /// <summary>The records of the other Windows users of this PC.</summary>
    internal static List<InstallRecord> OtherRecords()
        => Records.ReadAll().Where(r => !r.Sid.Equals(Elevation.CurrentUserSid, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Records this user's install folder (needs administrator rights); null removes the record.</summary>
    internal static void WriteOwnRecord(string? folder)
    {
        Records.Write(Elevation.CurrentUserSid, folder == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        Forget();
    }

    // ================= Setup =================

    /// <summary>
    /// The folder setup offers: this user's install (recorded, in Settings → Apps, or in the default of earlier versions,
    /// also with RunnerWidget.exe of the versions before 2.2), so setup run again updates it where it is; otherwise the
    /// install another Windows user of this PC has outside the user profiles (see <see cref="SharedInstall"/>); otherwise
    /// Program Files. The Settings → Apps entry and the default of earlier versions are offered too, so that an install
    /// without a record is not moved by surprise, but as <see cref="SetupOffer.Unconfirmed"/>: any program of the user can
    /// change them. An install that this user's own tasks start is recorded before setup opens.
    /// <paramref name="fileExists"/> tells whether a file exists.
    /// </summary>
    internal static SetupOffer SetupProposal(string? ownRecord, string? userEntry, IEnumerable<InstallRecord> otherRecords, string? profilesRoot,
        string programFilesDir, string oldDefaultDir, Func<string, bool> fileExists)
    {
        if (ownRecord != null) return new SetupOffer(ownRecord, Unconfirmed: false);
        if (userEntry != null) return new SetupOffer(userEntry, Unconfirmed: true);
        if (fileExists(Path.Combine(oldDefaultDir, AppPaths.ExeName)) || fileExists(Path.Combine(oldDefaultDir, AppPaths.LegacyExeName)))
            return new SetupOffer(oldDefaultDir, Unconfirmed: true);
        return new SetupOffer(SharedInstall(otherRecords, profilesRoot, programFilesDir, f => fileExists(Path.Combine(f, AppPaths.ExeName)))
                              ?? programFilesDir, Unconfirmed: false);
    }

    /// <summary>The folder setup offers on this PC (see <see cref="SetupProposal(string?, string?, IEnumerable{InstallRecord}, string?, string, string, Func{string, bool})"/>).</summary>
    internal static SetupOffer SetupProposal() => SetupProposal(ReadOwnRecord(), Installer.RegisteredInstallDir(), OtherRecords(), ProfilesRoot(),
        ProgramFilesDir, OldDefaultDir, File.Exists);

    /// <summary>
    /// Another Windows user's install that this user can share: one that holds the exe and is not inside a user profile
    /// (the default of earlier versions is in its user's own profile). Program Files comes first.
    /// </summary>
    internal static string? SharedInstall(IEnumerable<InstallRecord> otherRecords, string? profilesRoot, string programFilesDir, Func<string, bool> hasExe)
    {
        var usable = otherRecords.Select(r => r.Folder)
            .Where(f => Path.IsPathFullyQualified(f) && IsShareable(f, profilesRoot) && hasExe(f))
            .Select(Path.TrimEndingDirectorySeparator)
            .ToList();
        return usable.FirstOrDefault(f => AppUpdater.SameFolder(f, programFilesDir))
               ?? usable.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>Whether a folder can be shared by Windows users: it is not inside the folder that holds the user profiles.</summary>
    internal static bool IsShareable(string folder, string? profilesRoot)
        => !string.IsNullOrEmpty(profilesRoot) && !IsInside(folder, profilesRoot);

    /// <summary>Whether <paramref name="folder"/> is <paramref name="parent"/> or inside it.</summary>
    internal static bool IsInside(string folder, string parent)
        => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar)
            .StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder that holds the user profiles (usually C:\Users): the parent of this user's profile.</summary>
    internal static string? ProfilesRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile));
    }

    // ================= Other Windows users =================

    /// <summary>
    /// The other Windows users who use the install in <paramref name="folder"/>: those whose record names it, and those
    /// whose own Runner Manager tasks (dashboard, startup or fleet agent) start an exe in it. Both only when they still
    /// have a profile on this PC (see <paramref name="hasProfile"/>): the record and the tasks of a removed account can
    /// stay, and would keep the program files and stop older setups for ever. Account names, each once; unknown when some
    /// tasks could not be read.
    /// </summary>
    internal static OtherUsers OtherUsersOf(string folder, string ownSid, IEnumerable<InstallRecord> records, TaskScan tasks,
        Func<string, string> accountOfSid, Func<string, bool> hasProfile)
    {
        var users = new List<string>();
        void Add(string account)
        {
            if (account.Length > 0 && !users.Contains(account, StringComparer.OrdinalIgnoreCase)) users.Add(account);
        }
        foreach (var record in records)
        {
            if (record.Sid.Equals(ownSid, StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(record.Folder)) continue;
            if (AppUpdater.SameFolder(record.Folder, folder) && hasProfile(record.Sid)) Add(accountOfSid(record.Sid));
        }
        foreach (var task in tasks.Uses)
        {
            if (task.IsOwn || !Path.IsPathFullyQualified(task.Command) || !hasProfile(task.Account)) continue;
            if (Path.GetDirectoryName(task.Command) is { Length: > 0 } dir && AppUpdater.SameFolder(dir, folder)) Add(accountOfSid(task.Account));
        }
        return new OtherUsers(users, tasks.Unreadable);
    }

    /// <summary>
    /// The other Windows users of this PC who use the install in <paramref name="folder"/> (see <see cref="OtherUsersOf"/>).
    /// The records of users without a profile on this PC (their account was removed) are removed first: they would keep
    /// the program files and stop older setups for ever.
    /// </summary>
    internal static async Task<OtherUsers> OtherUsersOfAsync(string folder)
    {
        var records = Records.ReadAll();
        foreach (var gone in records.Where(r => !r.Sid.Equals(Elevation.CurrentUserSid, StringComparison.OrdinalIgnoreCase) && !HasProfile(r.Sid)))
        {
            try
            {
                Records.Write(gone.Sid, null);
                MachineRelay.AppendAgentLog($"Removed the record of the install folder {gone.Folder} of {gone.Sid}: that Windows user has no profile on this PC.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return OtherUsersOf(folder, Elevation.CurrentUserSid, records, await RunnerManagerTasksAsync(), AccountOf, HasProfile);
    }

    /// <summary>Whether the Windows user with this SID has a profile on this PC: the registry, except in tests.</summary>
    internal static Func<string, bool> HasProfile { get; set; } = ProfileExists;

    /// <summary>
    /// Whether the Windows user with this SID has a profile on this PC (an entry in ProfileList). True when that cannot be
    /// read, so a record is removed only when it is certain that its user is gone.
    /// </summary>
    private static bool ProfileExists(string sid)
    {
        if (!sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || sid.IndexOfAny(new[] { '\\', '/' }) >= 0) return true;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var list = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list == null) return true;
            using var profile = list.OpenSubKey(sid);
            return profile != null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    /// <summary>A SID as an account name (DOMAIN\name); anything else, or a SID that cannot be translated, as it is.</summary>
    internal static string AccountOf(string sidOrName)
    {
        if (!sidOrName.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return sidOrName;
        try { return new SecurityIdentifier(sidOrName).Translate(typeof(NTAccount)).Value; }
        catch (SystemException) { return sidOrName; }
    }

    /// <summary>
    /// The scheduled tasks of Runner Manager on this PC, of every Windows user: their names come from Task Scheduler's list in
    /// the registry (readable by administrators), their account and exe from the task definitions. Unreadable when the list
    /// or a task's definition could not be read: then another user may use an install without anyone knowing.
    /// </summary>
    internal static async Task<TaskScan> RunnerManagerTasksAsync()
    {
        var uses = new List<TaskUse>();
        if (Tasks.Names() is not { } names) return new TaskScan(uses, Unreadable: true);
        var unreadable = false;
        foreach (var name in names.Where(IsRunnerManagerTaskName))
        {
            if (await Tasks.ReadAsync(name) is not { } xml)
            {
                unreadable = true;
                continue;
            }
            // Any program can create a task with one of these names; Runner Manager registers its own with the highest rights.
            if (!ScheduledTasks.RunsWithHighestRights(xml)) continue;
            var principal = ScheduledTasks.PrincipalOf(xml);
            var command = ScheduledTasks.CommandOf(xml);
            if (principal.Length == 0 || command.Length == 0) continue;
            uses.Add(new TaskUse(principal, ScheduledTasks.IsCurrentUser(principal), command));
        }
        return new TaskScan(uses, unreadable);
    }

    /// <summary>The task names Runner Manager and the RunnerWidget versions used: "Runner Manager…" and "RunnerWidget…".</summary>
    internal static bool IsRunnerManagerTaskName(string name)
        => name.StartsWith("Runner Manager", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RunnerWidget", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the scheduled tasks are read from: Task Scheduler, except in tests.</summary>
    internal static ITaskReader Tasks { get; set; } = new SchedulerTaskReader();
}

/// <summary>Reads scheduled tasks for <see cref="MachineInstall"/>.</summary>
internal interface ITaskReader
{
    /// <summary>The names of the tasks in Task Scheduler's root folder, or null when they cannot be read.</summary>
    IReadOnlyList<string>? Names();

    /// <summary>A task's XML definition: empty when there is no such task, null when it exists but could not be read.</summary>
    Task<string?> ReadAsync(string name);
}

/// <summary>
/// Task Scheduler: the names come from its list in the registry (readable by administrators), the definitions from
/// schtasks.exe.
/// </summary>
internal sealed class SchedulerTaskReader : ITaskReader
{
    public IReadOnlyList<string>? Names()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var tree = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree");
            return tree?.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public async Task<string?> ReadAsync(string name)
    {
        var xml = await ScheduledTasks.QueryXmlAsync(name);
        if (xml.Length > 0) return xml;
        return ScheduledTasks.IsRegistered(name) == false ? string.Empty : null;
    }
}

/// <summary>
/// HKEY_LOCAL_MACHINE\SOFTWARE\RunnerManager\Installs in the 64-bit registry: one value per Windows user, named by the
/// user's SID, that holds the install folder. Only administrators (and SYSTEM) can change it: a program without
/// administrator rights can neither create the key nor change its values. Every user can read it.
/// </summary>
internal sealed class RegistryInstallRecordStore : IInstallRecordStore
{
    internal const string KeyPath = RegistrySwitchStore.KeyPath + @"\Installs";

    public IReadOnlyList<InstallRecord> ReadAll()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(KeyPath);
            if (key == null) return Array.Empty<InstallRecord>();
            var records = new List<InstallRecord>();
            foreach (var sid in key.GetValueNames())
            {
                if (key.GetValueKind(sid) != RegistryValueKind.String) continue;
                if (key.GetValue(sid) is string { Length: > 0 } folder) records.Add(new InstallRecord(sid, folder));
            }
            return records;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<InstallRecord>(); }
    }

    public void Write(string sid, string? folder)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (folder != null)
        {
            using var key = root.CreateSubKey(KeyPath, writable: true);
            key.SetValue(sid, folder, RegistryValueKind.String);
            return;
        }
        using (var key = root.OpenSubKey(KeyPath, writable: true))
        {
            if (key == null) return;
            key.DeleteValue(sid, throwOnMissingValue: false);
            if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
        }
        root.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
        // The parent key also holds the AI assistant switches, so it goes only when nothing is left in it.
        using (var parent = root.OpenSubKey(RegistrySwitchStore.KeyPath))
            if (parent == null || parent.ValueCount > 0 || parent.SubKeyCount > 0) return;
        root.DeleteSubKey(RegistrySwitchStore.KeyPath, throwOnMissingSubKey: false);
    }
}
