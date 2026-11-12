using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RunnerManager;

/// <summary>
/// The start helper, RunnerManagerLauncher.exe: a small native program (src\RunnerManagerLauncher) that the app embeds
/// and writes beside the installed RunnerManager.exe. It ignores the environment it is started with, builds a clean one
/// from the system's values, starts the RunnerManager.exe beside it, waits for it and returns its exit code. The
/// scheduled tasks and the app's own starts with administrator rights go through it while it is in place, so the
/// variables that any program of the user can set for the user (DOTNET_*, CORECLR_*, PATH and so on) never reach the
/// elevated app. It protects only an install that programs without administrator rights cannot change (Program Files).
/// <list type="bullet">
/// <item>Its file, the path beside an exe, and the mapping both ways: a task whose command is the start helper in a folder
/// starts that folder's RunnerManager.exe (<see cref="ExeOf"/>), and the command for an exe is the start helper beside it
/// while it is in place (<see cref="CommandFor"/>).</item>
/// <item>Writing it (<see cref="EnsureAsync"/>): only by an elevated install (setup, or the installed copy), never through
/// a link, never outside the install folder.</item>
/// <item>A PC where it cannot be kept (the write fails, or antivirus software removes it again) runs without it: the
/// marker <see cref="OffMarkerPath"/> beside it says so for that version, and the tasks start RunnerManager.exe as
/// before.</item>
/// </list>
/// </summary>
internal static class Launcher
{
    public const string FileName = "RunnerManagerLauncher.exe";

    /// <summary>The name of the resource that holds the start helper in the app (see RunnerManager.csproj).</summary>
    internal const string ResourceName = FileName;

    /// <summary>
    /// The folder where the start helper lets a self-contained exe unpack its native files
    /// (DOTNET_BUNDLE_EXTRACT_BASE_DIR), inside the install folder; the exe puts them in a subfolder named after itself.
    /// </summary>
    internal const string ExtractionFolderName = ".net";

    internal static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>The start helper beside <paramref name="exe"/>.</summary>
    internal static string PathBeside(string exe) => PathIn(Path.GetDirectoryName(exe) ?? string.Empty);

    /// <summary>Beside the start helper while this PC runs without it; it holds the version that gave up and why.</summary>
    internal static string OffMarkerPath(string folder) => PathIn(folder) + ".off";

    /// <summary>Whether a task's command is the start helper (a full path to RunnerManagerLauncher.exe).</summary>
    internal static bool IsLauncherPath(string command)
        => Path.IsPathFullyQualified(command) && Path.GetFileName(command).Equals(FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe a task's command starts: the RunnerManager.exe beside the start helper, or the command itself.</summary>
    internal static string ExeOf(string command)
        => IsLauncherPath(command) ? Path.Combine(Path.GetDirectoryName(command)!, AppPaths.ExeName) : command;

    /// <summary>
    /// Whether the start helper is in place in <paramref name="folder"/>: a file of its own there (not a link), and this
    /// PC does not run without it (<see cref="OffMarkerPath"/>).
    /// </summary>
    internal static bool IsInPlace(string folder)
        => MayBeInPlace(folder) && IsRegularFile(PathIn(folder)) && !File.Exists(OffMarkerPath(folder));

    /// <summary>
    /// The folders where the start helper is looked for: all of them, except in tests, which see only their own folders
    /// and never an install of Runner Manager on the PC that runs them.
    /// </summary>
    internal static Func<string, bool> MayBeInPlace { get; set; } = _ => true;

    /// <summary>
    /// The one place that decides what a task, or a start with administrator rights, runs for an installed
    /// RunnerManager.exe: the start helper beside it while it is in place (see <see cref="IsInPlace"/>), otherwise the exe.
    /// </summary>
    internal static string CommandFor(string exe)
        => IsAppExe(exe) && IsInPlace(Path.GetDirectoryName(exe)!) ? PathBeside(exe) : exe;

    private static bool IsAppExe(string exe)
        => Path.IsPathFullyQualified(exe) && Path.GetFileName(exe).Equals(AppPaths.ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a task's command can start: an exe as it is; the start helper only while its file is there.</summary>
    internal static bool CanStart(string command) => !IsLauncherPath(command) || IsRegularFile(command);

    /// <summary>
    /// How the app starts <paramref name="exe"/> itself (the start after setup, a restart of the dashboard, the UAC
    /// prompt, uninstall): through the start helper while it is in place (see <see cref="CommandFor"/>), with the same
    /// arguments, in the install folder. The start helper passes the arguments on as they are.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> arguments, bool elevate = false)
    {
        var start = new ProcessStartInfo(CommandFor(exe)) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty };
        if (elevate) start.Verb = "runas";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    // ================= The file =================

    /// <summary>The start helper's bytes from the app's resources, or null in a build without it. Tests give their own.</summary>
    internal static Func<byte[]?> Embedded { get; set; } = ReadEmbedded;

    internal static byte[]? ReadEmbedded()
    {
        try
        {
            using var stream = typeof(Launcher).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException) { return null; }
    }

    /// <summary>How long after writing the start helper it is checked again: antivirus software removes a new file soon after it appears.</summary>
    internal static Func<Task> Settle { get; set; } = () => Task.Delay(TimeSpan.FromSeconds(3));

    /// <summary>
    /// Runs a write of the start helper under the update lock, waiting for it at most the given time; null when the lock
    /// stayed taken (an update runs). Tests pass their own.
    /// </summary>
    internal static Func<TimeSpan, Func<EnsureResult?>, EnsureResult?> UnderUpdateLock { get; set; }
        = (wait, write) => AppUpdater.TryWithUpdateLock(wait, write, out var written) ? written : null;

    /// <summary>What is at the start helper's path.</summary>
    internal enum FileState { Missing, Same, Different, Link, Folder }

    /// <summary>What is at <paramref name="path"/>, compared with <paramref name="bytes"/>. A link is never read through.</summary>
    internal static FileState Inspect(string path, byte[] bytes)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return FileState.Missing; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) return FileState.Link;
        if (attributes.HasFlag(FileAttributes.Directory)) return FileState.Folder;
        try { return HashOf(path).SequenceEqual(SHA256.HashData(bytes)) ? FileState.Same : FileState.Different; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FileState.Different; }
    }

    private static byte[] HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    /// <summary>What <see cref="EnsureFileAsync"/> did: the file is in place, and whether it was written; or why it could not be kept.</summary>
    internal sealed record EnsureResult(bool InPlace, bool Wrote, string? Problem)
    {
        public static readonly EnsureResult Kept = new(true, false, null);
    }

    /// <summary>Written, and not checked again yet.</summary>
    private static readonly EnsureResult WrittenUnchecked = new(true, true, null);

    /// <summary>
    /// Puts <paramref name="bytes"/> at the start helper's path in <paramref name="folder"/> when the file there is missing
    /// or different. A running start helper cannot be overwritten but can be renamed, so the file there is renamed out of
    /// the way (an .old.exe file, removed later) and the new one, written beside it first, is moved into place, as the
    /// updater replaces the exe. A link in the way (a junction or symbolic link) is removed itself, never followed; a
    /// folder in the way is left, and the start helper is then not kept. After <paramref name="settle"/> the file is
    /// checked again: gone or changed means that something removes it, and it is not kept either. The write runs inside
    /// <paramref name="exclusive"/> (the update lock), which returns null when it cannot run now.
    /// </summary>
    internal static async Task<EnsureResult> EnsureFileAsync(string folder, byte[] bytes, Func<Task> settle,
        Func<Func<EnsureResult?>, EnsureResult?>? exclusive = null)
    {
        if (!Path.IsPathFullyQualified(folder)) return new EnsureResult(false, false, "the install folder is not a full path");
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return new EnsureResult(false, false, "the install folder does not exist");
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new EnsureResult(false, false, "the install folder is a link");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new EnsureResult(false, false, ex.Message); }

        var target = PathIn(folder);
        // The usual case, on every start: it is there as it should be. The update lock is only taken to write.
        if (Inspect(target, bytes) == FileState.Same)
        {
            RemoveOwnOldFiles(folder);
            return EnsureResult.Kept;
        }
        EnsureResult? Write()
        {
            RemoveOwnOldFiles(folder);
            var state = Inspect(target, bytes);
            if (state == FileState.Same) return EnsureResult.Kept;
            if (state == FileState.Folder) return new EnsureResult(false, false, $"a folder named {FileName} is in its place");
            try { Replace(target, bytes, state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new EnsureResult(false, false, $"it could not be written: {ex.Message}");
            }
            return WrittenUnchecked;
        }
        var outcome = await Task.Run(() => exclusive != null ? exclusive(Write) : Write());
        // An update holds the lock: left for a later start. A start helper that is there still starts the exe beside it.
        if (outcome == null) return new EnsureResult(IsRegularFile(target), false, null);
        if (!ReferenceEquals(outcome, WrittenUnchecked)) return outcome;
        await settle();
        return Inspect(target, bytes) switch
        {
            FileState.Same => new EnsureResult(true, true, null),
            FileState.Missing => new EnsureResult(false, true, "it was removed right after it was written"),
            _ => new EnsureResult(false, true, "it was changed right after it was written")
        };
    }

    private static void Replace(string target, byte[] bytes, FileState state)
    {
        var next = target + ".new";
        var partial = next + ".partial";
        // Left by a write that was cut off. A link there is removed itself; a new file is only ever created, never opened.
        DeleteEntry(partial);
        DeleteEntry(next);
        try
        {
            Retry(() =>
            {
                using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            });
            Retry(() => File.Move(partial, next));
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        string? aside = null;
        try
        {
            if (state == FileState.Link) DeleteEntry(target);
            else if (state == FileState.Different)
            {
                aside = OldPathFor(target);
                Retry(() => File.Move(target, aside));
            }
            try { Retry(() => File.Move(next, target)); }
            catch
            {
                if (aside != null)
                {
                    try { File.Move(aside, target); } catch { }
                }
                throw;
            }
        }
        catch
        {
            try { File.Delete(next); } catch { }
            throw;
        }
        // Succeeds when the old one does not run; otherwise a later start removes it.
        if (aside != null)
        {
            try { File.Delete(aside); } catch { }
        }
    }

    /// <summary>Deletes a file or a link (a symbolic link or junction, never what it points at); a real folder stays and fails the write.</summary>
    private static void DeleteEntry(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            if (!attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException($"A folder is in the place of {Path.GetFileName(path)}.");
            Directory.Delete(path, recursive: false);
        }
        else File.Delete(path);
    }

    private static string OldPathFor(string file)
        => Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.UtcNow.Ticks}.old{Path.GetExtension(file)}");

    /// <summary>Removes the start helpers that earlier writes renamed out of the way, once nothing runs them.</summary>
    private static void RemoveOwnOldFiles(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "RunnerManagerLauncher.*.old.exe"))
            {
                if (!AppUpdater.IsOldFile(Path.GetFileName(file), anyAppFile: false)) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A file of its own: it exists, and is neither a folder nor a link.</summary>
    internal static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>Runs a file operation, retrying for about three seconds while another program (an antivirus scanner) holds the file.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException && attempt < 6)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    // ================= Running without it =================

    /// <summary>The version that gave up on the start helper in <paramref name="folder"/> (see <see cref="OffMarkerPath"/>), or null.</summary>
    internal static string? OffVersion(string folder)
    {
        try
        {
            var path = OffMarkerPath(folder);
            if (!IsRegularFile(path)) return File.Exists(path) ? string.Empty : null;
            var lines = File.ReadAllLines(path);
            return lines.Length > 0 ? lines[0].Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>Whether this PC runs without the start helper (see <see cref="OffMarkerPath"/>).</summary>
    internal static bool RunsWithout(string folder) => OffVersion(folder) != null;

    /// <summary>
    /// Records beside the start helper that <paramref name="version"/> runs without it, and why. Written as a new file:
    /// whatever is in its place (also a link) is removed first, so the write never goes through a link.
    /// </summary>
    internal static void TurnOff(string folder, string version, string reason)
    {
        var path = OffMarkerPath(folder);
        DeleteEntry(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file);
        writer.WriteLine(version);
        writer.WriteLine(reason);
    }

    internal static void ClearOff(string folder)
    {
        try { DeleteEntry(OffMarkerPath(folder)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ================= For an install =================

    /// <summary>The once-per-version mark that a start helper that went missing was put back (see <see cref="EnsureAsync"/>).</summary>
    internal const string RepairedCheck = "start-helper-repaired";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Keeps the start helper in the install folder <paramref name="folder"/> for this version: writes it when it is missing
    /// or different (see <see cref="EnsureFileAsync"/>). Only called with administrator rights, by setup and by the
    /// installed copy. <paramref name="afterInstall"/> (setup, or a copy of the build just installed there) tries again
    /// even when this version gave up before. Otherwise a version that gave up keeps running without it, and a start
    /// helper that one of this user's tasks names but that is gone (antivirus software may remove it) is put back once per
    /// version; gone again, this version runs without it. When it cannot be kept, this is recorded beside it
    /// (<see cref="TurnOff"/>) and logged. Returns whether it is in place.
    /// </summary>
    internal static async Task<bool> EnsureAsync(string folder, bool afterInstall, Action<string> log)
    {
        await Gate.WaitAsync();
        try
        {
            var version = AppUpdater.CurrentVersion;
            if (afterInstall) ClearOff(folder);
            if (OffVersion(folder) is { } off && AppUpdater.CompareVersions(off, version) == 0) return false;
            if (Embedded() is not { } bytes)
            {
                LogOnce(log, "This build of Runner Manager has no start helper, so its scheduled tasks start RunnerManager.exe directly.");
                return IsInPlace(folder);
            }
            if (!afterInstall && !IsRegularFile(PathIn(folder)) && await OwnTaskNamesAsync(folder))
            {
                if (InstallChecks.IsDone(RepairedCheck))
                    return GiveUp(folder, version, $"{FileName} was removed again after it was put back", log);
                InstallChecks.MarkDone(RepairedCheck);
                log($"{FileName} was missing from {folder}, although a scheduled task starts it. Runner Manager puts it back.");
            }
            // Under the update lock, so an update in another process never swaps files there at the same moment. A start
            // waits for it only briefly and leaves the write for a later check; setup waits longer.
            var lockWait = TimeSpan.FromSeconds(afterInstall ? 30 : 2);
            var result = await EnsureFileAsync(folder, bytes, Settle, write => UnderUpdateLock(lockWait, write));
            if (result.Problem != null) return GiveUp(folder, version, result.Problem, log);
            if (!result.InPlace) return false;
            if (OffVersion(folder) != null) ClearOff(folder);
            if (result.Wrote) log($"Wrote the start helper {PathIn(folder)}.");
            return true;
        }
        finally { Gate.Release(); }
    }

    private static bool GiveUp(string folder, string version, string reason, Action<string> log)
    {
        try { TurnOff(folder, version, reason); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not record that this PC runs without the start helper: {ex.Message}");
        }
        log(OffLogText(reason));
        return false;
    }

    /// <summary>The fleet agent log's line when this PC runs without the start helper.</summary>
    internal static string OffLogText(string reason)
        => $"Runner Manager runs without its start helper {FileName} ({reason}), so its scheduled tasks start RunnerManager.exe directly. " +
           "Antivirus software may have removed it. Runner Manager tries again after the next update, or when setup runs again.";

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    private static void LogOnce(Action<string> log, string text)
    {
        lock (Logged)
            if (!Logged.Add(text)) return;
        log(text);
    }

    /// <summary>Whether one of this user's own tasks (launch, startup, fleet agent) starts the start helper in <paramref name="folder"/>.</summary>
    internal static async Task<bool> OwnTaskNamesAsync(string folder)
    {
        var helper = PathIn(folder);
        foreach (var name in new[] { ScheduledTasks.LaunchTaskName, ScheduledTasks.DashboardTaskName, ScheduledTasks.AgentTaskName })
            if (ScheduledTasks.CommandOfOwnElevatedTask(await ScheduledTasks.QueryXmlAsync(name)) is { } command && SamePath(command, helper))
                return true;
        return false;
    }

    /// <summary>
    /// Registers this user's own tasks that start the start helper beside <paramref name="exe"/> again for the exe, while
    /// the start helper is not in place (it is gone, or this PC runs without it), so no task names a file that does not
    /// start. A turned-off startup task is left as the user chose. The fleet agent task is started again afterwards; the
    /// agent's single-instance lock makes that a no-op when it runs. Returns the tasks registered again.
    /// </summary>
    internal static async Task<List<string>> PointOwnTasksAtExeAsync(string exe, Action<string> log)
    {
        var moved = new List<string>();
        if (IsInPlace(Path.GetDirectoryName(exe)!)) return moved;
        var helper = PathBeside(exe);
        async Task<string?> NamingTask(string name)
        {
            var xml = await ScheduledTasks.QueryXmlAsync(name);
            return ScheduledTasks.CommandOfOwnElevatedTask(xml) is { } command && SamePath(command, helper) ? xml : null;
        }
        async Task Move(string name, Func<Task> register)
        {
            try
            {
                await register();
                moved.Add(name);
            }
            catch (Exception ex) { log($"Could not point the scheduled task \"{name}\" at {exe}: {ex.Message}"); }
        }
        if (await NamingTask(ScheduledTasks.LaunchTaskName) != null)
            await Move(ScheduledTasks.LaunchTaskName, () => ScheduledTasks.RegisterLaunchTaskAsync(exe));
        if (await NamingTask(ScheduledTasks.DashboardTaskName) is { } startup && ScheduledTasks.IsEnabled(startup))
            await Move(ScheduledTasks.DashboardTaskName, () => Installer.RegisterStartupTaskAsync(exe));
        if (await NamingTask(ScheduledTasks.AgentTaskName) != null && await ScheduledTasks.OtherOwnerAsync(ScheduledTasks.AgentTaskName) == null)
            await Move(ScheduledTasks.AgentTaskName, async () =>
            {
                await MachineRelay.RegisterAgentTaskAsync(exe);
                await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
            });
        if (moved.Count > 0) log($"The scheduled tasks {string.Join(", ", moved.Select(n => $"\"{n}\""))} start {exe} again, without the start helper.");
        return moved;
    }

    /// <summary>
    /// For the installed copy with administrator rights: keeps the start helper (<see cref="EnsureAsync"/>), points this
    /// user's tasks at the exe while it is not in place (<see cref="PointOwnTasksAtExeAsync"/>), and removes what earlier
    /// versions unpacked in the install folder (<see cref="RemoveOldExtractions"/>). Any other copy does nothing.
    /// </summary>
    internal static async Task EnsureForThisCopyAsync()
    {
        if (!Elevation.IsElevated || !AppUpdater.IsRunningInstalledCopy) return;
        var folder = AppUpdater.InstallDir;
        try
        {
            if (!await EnsureAsync(folder, afterInstall: false, MachineRelay.AppendAgentLog))
                await PointOwnTasksAtExeAsync(AppUpdater.InstalledExe, MachineRelay.AppendAgentLog);
        }
        catch (Exception ex) { MachineRelay.AppendAgentLog($"Could not check the start helper: {ex.Message}"); }
        _ = Task.Run(() => RemoveOldExtractions(folder, CurrentExtractionFolders()));
    }

    // ================= What a self-contained exe unpacked =================

    /// <summary>
    /// Removes what self-contained exes of earlier versions unpacked in <paramref name="folder"/>\.net\RunnerManager (one
    /// folder per build): each folder that no running program uses, which means that none of its files is open, and that
    /// is not in <paramref name="inUse"/> (this process's own). Nothing is followed through a link: a link there is removed
    /// itself, and a .net folder that is a link is left as it is.
    /// </summary>
    internal static void RemoveOldExtractions(string folder, IReadOnlyCollection<string> inUse)
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(folder, ExtractionFolderName));
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var app = new DirectoryInfo(Path.Combine(root.FullName, Path.GetFileNameWithoutExtension(AppPaths.ExeName)));
            if (!app.Exists || app.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var build in app.EnumerateDirectories())
            {
                if (build.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    try { build.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    continue;
                }
                if (inUse.Any(used => AppUpdater.SameFolder(used, build.FullName)) || IsInUse(build)) continue;
                DeleteTree(build);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>The folders this process loads native files from that lie in an unpack folder (a self-contained build).</summary>
    internal static IReadOnlyCollection<string> CurrentExtractionFolders()
        => (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .ToList();

    /// <summary>
    /// Whether a program uses a file in the folder: it has the file open, or has loaded it as a DLL. Either way the file
    /// cannot be opened for writing by this process alone, which is tried without changing it. A loaded DLL has no open
    /// file left, so opening it to read alone, or renaming the folder it is in, still works and does not tell (measured on
    /// Windows 11).
    /// </summary>
    internal static bool IsInUse(DirectoryInfo folder)
    {
        foreach (var file in folder.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try { using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    /// <summary>Deletes a folder and what is in it without going into links: a link is removed itself.</summary>
    internal static void DeleteTree(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo directory)
                {
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) directory.Delete();
                    else DeleteTree(directory);
                }
                else entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { folder.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ================= The fleet agent =================

    /// <summary>What a fleet agent does about its own start (see <see cref="AgentStep"/>).</summary>
    internal enum AgentStartStep { Nothing, MoveTaskAndRestart, Restart }

    /// <summary>
    /// What a fleet agent of the installed copy does so that it runs with the clean environment: nothing when the start
    /// helper started it or is not in place, and nothing while an update waits to prove itself
    /// (<paramref name="updateWatched"/>): a rollback in that time then finds the task as the earlier version left it.
    /// Otherwise, when its task starts this exe directly, the task moves to the start helper and the agent restarts through
    /// it. When its task already starts the start helper (registered while this agent ran), it only restarts, once per
    /// version (<paramref name="restartedThisVersion"/>), so a start that is not recognized as the start helper's never
    /// makes it restart again and again. The move itself does not depend on that mark, which is kept in the data folder
    /// that any program of the user can change. A task that starts another copy, or none, is left alone.
    /// </summary>
    internal static AgentStartStep AgentStep(bool startedByHelper, bool helperInPlace, string? taskCommand, string exe, bool updateWatched,
        bool restartedThisVersion)
    {
        if (startedByHelper || !helperInPlace || updateWatched || string.IsNullOrEmpty(taskCommand)) return AgentStartStep.Nothing;
        if (!SamePath(ExeOf(taskCommand), exe)) return AgentStartStep.Nothing;
        if (!IsLauncherPath(taskCommand)) return AgentStartStep.MoveTaskAndRestart;
        return restartedThisVersion ? AgentStartStep.Nothing : AgentStartStep.Restart;
    }

    /// <summary>The once-per-version mark that the fleet agent restarted through the start helper.</summary>
    internal const string AgentRestartCheck = "agent-start-helper";

    /// <summary>Whether the start helper beside this process's exe started it (it is the parent process).</summary>
    internal static bool StartedThisProcess() => HelperProcessId() != null;

    /// <summary>
    /// The process id of the start helper beside this process's exe when it started this process, or null. It waits for
    /// this process and exits a moment after it, so whatever waits for this process to exit before it starts a task or
    /// removes files waits for it too.
    /// </summary>
    internal static int? HelperProcessId()
    {
        if (_helperProcessId is { } known) return known.Id;
        var exe = Environment.ProcessPath;
        int? id = null;
        if (!string.IsNullOrEmpty(exe) && ParentProcess() is { } parent && SamePath(parent.Path, PathBeside(exe))) id = parent.Id;
        _helperProcessId = new HelperId(id);
        return id;
    }

    private sealed record HelperId(int? Id);
    private static HelperId? _helperProcessId;

    private static (int Id, string Path)? ParentProcess()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            var id = (int)info.InheritedFromUniqueProcessId;
            using var parent = Process.GetProcessById(id);
            // The start helper waits for the app, so it still runs; a parent that started later is another process with its id.
            if (parent.StartTime > self.StartTime) return null;
            return parent.MainModule?.FileName is { } path ? (id, path) : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
                   && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
