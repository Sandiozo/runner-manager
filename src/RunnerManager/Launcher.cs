using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
    /// The folders where the start helper is looked for, written, checked and given up on: all of them, except in tests,
    /// which see only their own folders and never look at or change an install of Runner Manager on the PC that runs them.
    /// </summary>
    internal static Func<string, bool> MayBeInPlace { get; set; } = _ => true;

    /// <summary>
    /// Whether <paramref name="exe"/> is the installed RunnerManager.exe (<see cref="AppUpdater.InstalledExe"/>): only that
    /// one is ever started through the start helper beside it. Tests see their own folders.
    /// </summary>
    internal static Func<string, bool> IsInstalledExe { get; set; } = exe => SamePath(exe, AppUpdater.InstalledExe);

    /// <summary>
    /// Whether this process decides about the start helper: it has administrator rights and is the installed build
    /// (<see cref="AppUpdater.IsInstalledBuild"/>). Only such a process writes or replaces the start helper, checks that it
    /// works and gives up on it (<see cref="EnsureAsync"/>), moves a task to it or back to the exe, removes unpack folders,
    /// and starts Runner Manager through it with administrator rights. Tests pass their own.
    /// </summary>
    internal static Func<bool> Decides { get; set; } = () => Elevation.IsElevated && AppUpdater.IsInstalledBuild;

    /// <summary>
    /// The one place that decides what a task, or a start with administrator rights, runs for the installed
    /// RunnerManager.exe. A process that decides (<see cref="Decides"/>) gives the start helper beside it while it is in
    /// place (see <see cref="IsInPlace"/>) and this process saw it start Runner Manager (<see cref="IsProven"/>), and the
    /// exe while it is not in place. While it is in place but not seen to work yet (an update held the update lock), and in
    /// a process that does not decide (an earlier copy that still runs after an update), a task keeps what it runs now
    /// (<paramref name="current"/>), so such a process never moves a task either way. Any other exe is its own command.
    /// </summary>
    internal static string CommandFor(string exe, string? current = null)
    {
        if (!IsAppExe(exe) || !IsInstalledExe(exe)) return exe;
        var folder = Path.GetDirectoryName(exe)!;
        var helper = PathBeside(exe);
        var keeps = current != null && SamePath(current, helper);
        if (!Decides()) return keeps ? helper : exe;
        if (!IsInPlace(folder)) return exe;
        return IsProven(folder) || keeps ? helper : exe;
    }

    /// <summary>
    /// What a process without administrator rights asks for with a UAC prompt for <paramref name="exe"/>: the start helper
    /// beside the installed exe while it is in place, otherwise the exe. Such a process cannot check that the start helper
    /// works, so it relies on the marker of a PC that runs without it (see <see cref="OffMarkerPath"/>) and starts the exe
    /// itself when the start helper cannot be started (see <see cref="Elevation.RelaunchElevated"/>).
    /// </summary>
    internal static string UacCommandFor(string exe)
        => IsAppExe(exe) && IsInstalledExe(exe) && IsInPlace(Path.GetDirectoryName(exe)!) ? PathBeside(exe) : exe;

    private static bool IsAppExe(string exe)
        => Path.IsPathFullyQualified(exe) && Path.GetFileName(exe).Equals(AppPaths.ExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a task's command can start: an exe as it is; the start helper only while its file is there.</summary>
    internal static bool CanStart(string command) => !IsLauncherPath(command) || IsRegularFile(command);

    /// <summary>
    /// How the app starts <paramref name="exe"/> itself (the start after setup, a restart of the dashboard, the UAC
    /// prompt, uninstall), with the same arguments, in the install folder: with <paramref name="elevate"/> (a UAC prompt)
    /// through <see cref="UacCommandFor"/>, otherwise through <see cref="CommandFor"/>, and with <paramref name="direct"/>
    /// always the exe itself. The start helper passes the arguments on as they are.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> arguments, bool elevate = false, bool direct = false)
    {
        var command = direct ? exe : elevate ? UacCommandFor(exe) : CommandFor(exe);
        var start = new ProcessStartInfo(command) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty };
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
    /// or different (see <see cref="EnsureFileAsync"/>), then checks that it starts Runner Manager (see
    /// <see cref="ProveAsync"/>) before anything depends on it. Only called by a process that decides
    /// (<see cref="Decides"/>): setup, and the installed build. <paramref name="afterInstall"/> (setup, or a copy of the
    /// build just installed there) tries again even when this version gave up before; the marker of that stays until the
    /// start helper was seen to work. Otherwise a version that gave up keeps running without it, and a start helper that
    /// one of this user's tasks names but that is gone (antivirus software may remove it) is put back once per version;
    /// gone again, this version runs without it. When it cannot be kept, or does not start Runner Manager, this is recorded
    /// beside it (<see cref="TurnOff"/>) and logged. Returns whether it is in place and works; false without a decision
    /// while an update holds the update lock.
    /// </summary>
    internal static async Task<bool> EnsureAsync(string folder, bool afterInstall, Action<string> log)
    {
        if (!MayBeInPlace(folder)) return false;
        await Gate.WaitAsync();
        try
        {
            var version = AppUpdater.CurrentVersion;
            if (!afterInstall && OffVersion(folder) is { } off && AppUpdater.CompareVersions(off, version) == 0) return false;
            if (Embedded() is not { } bytes)
            {
                LogOnce(log, "This build of Runner Manager has no start helper, so its scheduled tasks start RunnerManager.exe directly.");
                return IsInPlace(folder) && IsProven(folder);
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
            if (result.Wrote) log($"Wrote the start helper {PathIn(folder)}.");
            // Before a task is registered for it or Runner Manager is started through it.
            var proof = await ProveAsync(folder, lockWait);
            if (proof.Outcome == Proof.NotYet) return false;
            if (proof.Outcome == Proof.Fails) return GiveUp(folder, version, proof.Reason!, log);
            if (OffVersion(folder) != null) ClearOff(folder);
            return true;
        }
        finally { Gate.Release(); }
    }

    // ================= Whether it works =================

    /// <summary>What <see cref="ProveAsync"/> found: the start helper starts Runner Manager, it does not, or this cannot be checked now.</summary>
    internal enum Proof { Works, Fails, NotYet }

    /// <summary>The outcome of <see cref="ProveAsync"/>, and for <see cref="Proof.Fails"/> why, for the marker and the log.</summary>
    internal sealed record ProofResult(Proof Outcome, string? Reason = null);

    /// <summary>The probes this process ran, by what they tested (see <see cref="ProofKey"/>): null when it worked, otherwise why not.</summary>
    private static readonly Dictionary<string, string?> Probed = new(StringComparer.Ordinal);

    private static readonly SemaphoreSlim ProofGate = new(1, 1);

    /// <summary>How long a probe of the start helper may take before it counts as not working.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The pause before a probe that failed is tried once more. Tests pass their own.</summary>
    internal static Func<Task> ProbePause { get; set; } = () => Task.Delay(TimeSpan.FromSeconds(5));

    /// <summary>Runs one probe of the start helper at the given path (see <see cref="Probe"/>). Tests pass their own.</summary>
    internal static Func<string, string?> RunProbe { get; set; } = Probe;

    /// <summary>
    /// Whether this process knows that the start helper in <paramref name="folder"/>, as it is now, starts the Runner
    /// Manager beside it: it started this process (<see cref="StartedFromHere"/>), or a probe of these files worked (see
    /// <see cref="ProveAsync"/>). It only reads what is known, so it never waits. Tests pass their own.
    /// </summary>
    internal static Func<string, bool> IsProven { get; set; } = ProvenNow;

    internal static bool ProvenNow(string folder)
    {
        if (StartedFrom(folder)) return true;
        if (ProofKey(folder) is not { } key) return false;
        lock (Probed) return Probed.TryGetValue(key, out var failure) && failure == null;
    }

    /// <summary>
    /// What a probe tests: the start helper's bytes and the file of the RunnerManager.exe beside it, so a start helper that
    /// was written again, or an exe that an update replaced, is probed again. Null when either cannot be read.
    /// </summary>
    private static string? ProofKey(string folder)
    {
        try
        {
            if (!IsRegularFile(PathIn(folder)) || ProcessImage.IdOf(Path.Combine(folder, AppPaths.ExeName)) is not { } exe) return null;
            return $"{Convert.ToHexString(HashOf(PathIn(folder)))} {exe}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Whether the start helper in <paramref name="folder"/> started this process, which is the RunnerManager.exe there,
    /// and is still the file there: a start helper that was replaced while it ran was renamed out of the way first, and its
    /// replacement has not started anything yet.
    /// </summary>
    internal static bool StartedFromHere(string folder)
        => Helper() is { } helper && ProcessImage.RunsFrom(helper.Handle, PathIn(folder))
           && ProcessImage.ThisProcessRunsFrom(Path.Combine(folder, AppPaths.ExeName));

    /// <summary><see cref="StartedFromHere"/>. Tests pass their own.</summary>
    internal static Func<string, bool> StartedFrom { get; set; } = StartedFromHere;

    /// <summary>
    /// Checks that the start helper in <paramref name="folder"/> starts the Runner Manager beside it: it started this
    /// process (<see cref="StartedFromHere"/>), so a start through it costs nothing, or else it is started with
    /// <see cref="App.ProbeArgument"/> and has to exit with 0 (see <see cref="Probe"/>). The result is kept for this process
    /// and these files (see <see cref="ProofKey"/>). A probe that fails is tried once more after a short pause
    /// (<see cref="ProbePause"/>). Each probe runs under the update lock, waiting for it at most
    /// <paramref name="lockWait"/>: while an update holds it, nothing is probed and nothing is decided
    /// (<see cref="Proof.NotYet"/>), and a later check tries again. Only for a process that decides (<see cref="Decides"/>).
    /// </summary>
    internal static async Task<ProofResult> ProveAsync(string folder, TimeSpan lockWait)
    {
        if (!MayBeInPlace(folder)) return new ProofResult(Proof.NotYet);
        if (StartedFrom(folder)) return new ProofResult(Proof.Works);
        await ProofGate.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                string? key = null, failure = null;
                var known = false;
                var ran = await Task.Run(() => UnderUpdateLock(lockWait, () =>
                {
                    key = ProofKey(folder);
                    if (key == null) return EnsureResult.Kept;
                    lock (Probed) known = Probed.TryGetValue(key, out failure);
                    if (!known) failure = RunProbe(PathIn(folder));
                    return EnsureResult.Kept;
                })) != null;
                // An update holds the lock, or the files cannot be read right now (an update or a scanner has them).
                if (!ran || key == null) return new ProofResult(Proof.NotYet);
                if (!known && (failure == null || attempt == 2))
                    lock (Probed) Probed[key] = failure;
                if (known || failure == null || attempt == 2)
                    return failure == null ? new ProofResult(Proof.Works) : new ProofResult(Proof.Fails, failure);
                await ProbePause();
            }
        }
        finally { ProofGate.Release(); }
    }

    /// <summary>
    /// Starts the start helper at <paramref name="helper"/> with <see cref="App.ProbeArgument"/>, without a shell and
    /// without a window, in the system folder, and waits for it at most <see cref="ProbeTimeout"/>. It starts the
    /// RunnerManager.exe beside it with that argument, which exits with 0 before it opens or changes anything (see
    /// App.OnStartup), and returns that exit code; its own failures have codes of its own. Null when it exited with 0,
    /// otherwise why not, for the marker and the log.
    /// </summary>
    internal static string? Probe(string helper)
    {
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
        start.ArgumentList.Add(App.ProbeArgument);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return "it could not be started";
            if (!process.WaitForExit(ProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                return $"it did not start Runner Manager within {(int)ProbeTimeout.TotalSeconds} seconds";
            }
            return process.ExitCode == 0 ? null : $"it did not start Runner Manager (exit code 0x{process.ExitCode:X8})";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"it could not be started: {ex.Message}";
        }
    }

    /// <summary>Forgets the probes this process ran (for tests).</summary>
    internal static void ForgetProbes()
    {
        lock (Probed) Probed.Clear();
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
           "Antivirus software may have removed or blocked it. Runner Manager tries again after the next update, or when the setup runs again.";

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
    /// For the installed build with administrator rights (<see cref="Decides"/>): keeps the start helper and checks that it
    /// works (<see cref="EnsureAsync"/>), points this user's tasks at the exe while it is not in place
    /// (<see cref="PointOwnTasksAtExeAsync"/>), and removes what earlier versions unpacked in the install folder
    /// (<see cref="RemoveOldExtractions"/>). Any other copy does nothing, also one that still runs from the installed path
    /// after an update replaced the file there.
    /// </summary>
    internal static Task EnsureForThisCopyAsync()
        => EnsureForCopyAsync(AppUpdater.InstallDir, MachineRelay.AppendAgentLog, removeExtractions: folder => Task.Run(() => RemoveOldExtractions(folder, CurrentExtractionFolders())));

    /// <summary><see cref="EnsureForThisCopyAsync"/> for the install folder <paramref name="folder"/>. Tests pass their own folder.</summary>
    internal static async Task EnsureForCopyAsync(string folder, Action<string> log, Func<string, Task> removeExtractions)
    {
        if (!Decides() || !MayBeInPlace(folder)) return;
        try
        {
            if (!await EnsureAsync(folder, afterInstall: false, log))
                await PointOwnTasksAtExeAsync(Path.Combine(folder, AppPaths.ExeName), log);
        }
        catch (Exception ex) { log($"Could not check the start helper: {ex.Message}"); }
        _ = removeExtractions(folder);
    }

    // ================= What a self-contained exe unpacked =================

    /// <summary>
    /// Removes what self-contained exes of earlier versions unpacked in <paramref name="folder"/>\.net\RunnerManager (one
    /// folder per build): each folder that no running program uses, which means that none of its files is open or loaded
    /// (see <see cref="IsInUse"/>), and that is not in <paramref name="inUse"/> (this process's own). Nothing is followed through a link: a link there is removed
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
    internal static int? HelperProcessId() => Helper()?.Id;

    /// <summary>The start helper that started this process, found once: its id, and a handle that keeps its id from being reused.</summary>
    private sealed record HelperProcess(int Id, SafeProcessHandle Handle);

    private sealed record HelperFound(HelperProcess? Helper);
    private static HelperFound? _helper;

    private static HelperProcess? Helper()
    {
        if (_helper is { } known) return known.Helper;
        var exe = Environment.ProcessPath;
        var found = new HelperFound(string.IsNullOrEmpty(exe) ? null : ParentProcess(PathBeside(exe)));
        return (Interlocked.CompareExchange(ref _helper, found, null) ?? found).Helper;
    }

    /// <summary>The parent process when it was started from <paramref name="helper"/> and started before this one, otherwise null.</summary>
    private static HelperProcess? ParentProcess(string helper)
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
            // The path it was started by, also when its file was renamed since.
            if (parent.MainModule?.FileName is not { } path || !SamePath(path, helper)) return null;
            var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, id);
            if (!handle.IsInvalid) return new HelperProcess(id, handle);
            handle.Dispose();
            return null;
        }
        catch { return null; }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

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

    internal static bool SamePath(string a, string b)
    {
        try
        {
            return Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
                   && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
