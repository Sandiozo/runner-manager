using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>What starts a runner after a restart (<see cref="RunnerInfo.StartsAt"/>).</summary>
public static class RunnerStart
{
    /// <summary>A Windows service that starts with Windows.</summary>
    public const string WithWindows = "windows";
    /// <summary>A console runner that a startup entry opens when the user signs in.</summary>
    public const string SignIn = "signin";
    /// <summary>A console runner opened at sign-in by a startup entry that also starts other runners (left alone).</summary>
    public const string SignInShared = "signin-shared";
    /// <summary>Set not to start: a service that starts manually, or a console runner whose startup entries were turned off.</summary>
    public const string Off = "off";
    /// <summary>A disabled service (as during a move). It can't be started until it is turned on again.</summary>
    public const string Disabled = "disabled";
    /// <summary>Nothing known starts it, or the fleet agent is too old to tell.</summary>
    public const string Unknown = "";
}

/// <summary>The sc.exe start= values.</summary>
public static class ServiceStart
{
    public const string Automatic = "auto";
    public const string Manual = "demand";
    public const string Disabled = "disabled";
}

public static partial class RunnerService
{
    internal static string ServiceStartsAt(string startMode) => startMode.ToLowerInvariant() switch
    {
        "auto" => RunnerStart.WithWindows,
        "manual" => RunnerStart.Off,
        "disabled" => RunnerStart.Disabled,
        _ => RunnerStart.Unknown
    };

    internal static async Task SetServiceStartModeAsync(string serviceName, string mode)
    {
        var (code, output) = await ScheduledTasks.RunAsync("sc.exe",
            $"config \"{serviceName.Replace("\"", "")}\" start= {mode}", ScheduledTasks.ShortTimeout);
        if (code != 0)
            throw new InvalidOperationException($"Could not change how the Windows service {serviceName} starts: {output.Trim()}");
    }

    /// <summary>
    /// Sets whether a runner starts by itself after a restart. A service is set to start with Windows or manually; a
    /// console runner's startup entries (Startup folder, scheduled tasks, Run values) are moved aside or put back. A
    /// running runner keeps running either way.
    /// </summary>
    public static async Task SetStartAutomaticallyAsync(string key, bool start, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        // A runner with a service that runs from a console window starts from its console's startup entries.
        if (runner.ServiceInstalled && !runner.IsInteractive)
        {
            if (start && runner.MovedAway)
                throw new InvalidOperationException("This is the old copy of a runner that was moved to another PC. Delete it instead.");
            await SetServiceStartModeAsync(runner.Name, start ? ServiceStart.Automatic : ServiceStart.Manual);
            log(start ? "The runner starts with Windows." : "The runner no longer starts with Windows. Start it from Runner Manager when you need it.");
            return;
        }
        if (start) await TurnOnConsoleStartupAsync(runner, log);
        else await TurnOffConsoleStartupAsync(runner, log);
        ForgetStartupTasks();
    }

    private static async Task TurnOnConsoleStartupAsync(RunnerInfo runner, Action<string> log)
    {
        var record = StartupOffPath(runner.InstallPath);
        if (!File.Exists(record))
            throw new InvalidOperationException("Runner Manager did not turn off this runner's startup, so there is nothing to turn on.");
        var saved = ReadStartupOff(record);
        if (saved == null)
        {
            // Unreadable: forget it, so the runner doesn't stay "off" for good. The moved entries stay in the folder.
            TryDelete(record);
            throw new InvalidOperationException($"The list of turned-off startup entries could not be read. The entries are in {Path.GetDirectoryName(record)}; put them back by hand.");
        }
        var failed = await saved.RestoreAsync(log);
        if (failed.IsEmpty)
        {
            TryDelete(record);
            log(runner.IsRunning ? "The runner starts when you sign in." : "The runner starts the next time you sign in.");
            return;
        }
        // Keep only what is still turned off, so trying again doesn't fail on the entries that are back.
        await WriteStartupOffAsync(record, runner.InstallPath, failed);
        throw new InvalidOperationException($"Some startup entries could not be put back. They are kept in {Path.GetDirectoryName(record)}.");
    }

    private static async Task TurnOffConsoleStartupAsync(RunnerInfo runner, Action<string> log)
    {
        var record = StartupOffPath(runner.InstallPath);
        var folder = Path.GetDirectoryName(record)!;
        var others = await Task.Run(() => OtherRunnerRoots(runner.InstallPath));
        var changes = new StartupChanges();
        try
        {
            await Task.Run(() => DisableStartupFiles(runner.InstallPath, others, changes, log, folder));
            await DisableScheduledTasksAsync(runner.InstallPath, others, changes, log);
            await Task.Run(() => DisableRunValues(runner.InstallPath, others, changes, log, folder));

            // Entries shared with other runners, and ones that could not be turned off, would still start it.
            var left = await ReadStartupEntriesAsync(freshTasks: true);
            if (left == null || left.Any(entry => MatchStartupEntry(entry, runner.InstallPath, others) != StartupMatch.None))
                throw new InvalidOperationException(left == null
                    ? "Runner Manager could not check the scheduled tasks for ones that start this runner, so nothing was changed. Try again."
                    : "Something that Runner Manager cannot turn off still starts this runner, so nothing was changed. The runner's log in Runner Manager says what it is.");
            if (changes.IsEmpty && !File.Exists(record))
                throw new InvalidOperationException("Nothing starts this runner when you sign in.");

            // An earlier turn-off that was only partly undone keeps its entries in the same record.
            var all = new StartupChanges();
            all.Add(changes);
            if (ReadStartupOff(record) is { } earlier) all.Add(earlier);
            await WriteStartupOffAsync(record, runner.InstallPath, all);
        }
        catch
        {
            await changes.RestoreAsync(log);
            throw;
        }
        log("The runner no longer starts when you sign in. Its startup entries were moved aside, so turning it on again puts them back.");
    }

    private static async Task WriteStartupOffAsync(string record, string root, StartupChanges changes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        await File.WriteAllTextAsync(record, JsonSerializer.Serialize(StartupOffRecord.From(root, changes), StartupJson));
    }

    // ----- Console runners: what starts them, and the startup entries Runner Manager turned off -----

    private static readonly JsonSerializerOptions StartupJson = new() { WriteIndented = true };

    /// <summary>Where the startup entries of a console runner are kept while it is set not to start: one folder per runner.</summary>
    internal static string StartupOffPath(string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd('\\', '/').ToUpperInvariant())))[..12];
        return Path.Combine(DisabledStartupFolder, $"off-{hash}", "startup-off.json");
    }

    private static void ForgetStartupOff(string root) => TryDelete(StartupOffPath(root));

    /// <summary>
    /// What starts a console runner. A startup entry that still starts it wins over the record of turned-off entries,
    /// so a runner whose entry came back (made again by hand, or partly restored) is not shown as off.
    /// </summary>
    internal static string ConsoleStartsAt(string root, List<string> startupEntries, IEnumerable<string> allRoots)
    {
        var folder = root.TrimEnd('\\', '/');
        var named = startupEntries.Where(entry => entry.Contains(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 0)
        {
            var others = allRoots.Where(r => !r.Equals(root, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = named.Select(entry => MatchStartupEntry(entry, root, others)).ToList();
            if (matches.Contains(StartupMatch.ThisRunner)) return RunnerStart.SignIn;
            if (matches.Contains(StartupMatch.SharedWithOtherRunners)) return RunnerStart.SignInShared;
        }
        return File.Exists(StartupOffPath(root)) ? RunnerStart.Off : RunnerStart.Unknown;
    }

    /// <summary>
    /// The text of everything that starts programs at sign-in: Startup folder scripts and shortcuts, Run values and
    /// scheduled tasks. Scheduled tasks need PowerShell, which is slow, so after the first reading they are read again
    /// in the background at most every five minutes, and right after startup entries were turned off or on. Returns
    /// null when <paramref name="freshTasks"/> is set and the scheduled tasks could not be read.
    /// </summary>
    private static async Task<List<string>?> ReadStartupEntriesAsync(bool freshTasks = false)
    {
        var entries = new List<string>();
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) }
                     .Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is not (".cmd" or ".bat" or ".ps1" or ".vbs" or ".lnk")) continue;
                    try { entries.Add(ReadStartupText(File.ReadAllBytes(file), extension == ".lnk")); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var (hive, path) in new[] { (Registry.CurrentUser, RunKey), (Registry.LocalMachine, RunKey), (Registry.LocalMachine, RunKey32) })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value) entries.Add(value.ToString() ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        if (freshTasks)
        {
            var tasks = await ReadEnabledTaskActionsAsync();
            if (tasks == null) return null;
            entries.AddRange(tasks);
        }
        else entries.AddRange(await StartupTasksAsync());
        return entries;
    }

    private static readonly object StartupTasksGate = new();
    private static List<string>? _startupTasks;
    private static DateTime _startupTasksReadUtc = DateTime.MinValue;
    private static string _startupTasksOffState = string.Empty;
    private static bool _startupTasksReading;

    private static void ForgetStartupTasks()
    {
        lock (StartupTasksGate) _startupTasksReadUtc = DateTime.MinValue;
    }

    /// <summary>The actions of enabled scheduled tasks outside \Microsoft\, or null when they could not be read.</summary>
    private static async Task<List<string>?> ReadEnabledTaskActionsAsync()
    {
        try
        {
            var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(ListTasksScript));
            var (code, output) = await ScheduledTasks.RunAsync("powershell.exe",
                $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {script}", ScheduledTasks.ShortTimeout);
            return code == 0 ? ParseTaskList(output).Select(t => t.Actions).ToList() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The scheduled tasks from the last reading. The first reading is waited for, so a runner started only by a task
    /// isn't shown as one without a service; later ones run in the background when the reading is old.
    /// </summary>
    private static async Task<List<string>> StartupTasksAsync()
    {
        // Another process (the dashboard or the fleet agent) may have turned startup entries off or on: read again then.
        var offState = StartupOffState();
        Task? first = null;
        lock (StartupTasksGate)
        {
            var stale = DateTime.UtcNow - _startupTasksReadUtc > TimeSpan.FromMinutes(5) || offState != _startupTasksOffState;
            if (stale && !_startupTasksReading)
            {
                _startupTasksReading = true;
                var reading = Task.Run(async () =>
                {
                    var tasks = await ReadEnabledTaskActionsAsync();
                    lock (StartupTasksGate)
                    {
                        if (tasks != null) _startupTasks = tasks;
                        _startupTasksReadUtc = DateTime.UtcNow;
                        _startupTasksOffState = offState;
                        _startupTasksReading = false;
                    }
                });
                if (_startupTasks == null) first = reading;
            }
        }
        if (first != null) await first;
        lock (StartupTasksGate) return _startupTasks ?? new List<string>();
    }

    /// <summary>Which console runners are set not to start, as one string that changes when one is turned off or on.</summary>
    private static string StartupOffState()
    {
        try
        {
            return Directory.Exists(DisabledStartupFolder)
                ? string.Join('|', Directory.EnumerateFiles(DisabledStartupFolder, "startup-off.json", SearchOption.AllDirectories)
                    .Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}").Order())
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    private static StartupChanges? ReadStartupOff(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<StartupOffRecord>(File.ReadAllText(path))?.ToChanges(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The startup entries of a console runner that Runner Manager turned off, as saved in startup-off.json.</summary>
    internal sealed class StartupOffRecord
    {
        public string Root { get; set; } = string.Empty;
        public List<MovedFile> Files { get; set; } = new();
        public List<string> Tasks { get; set; } = new();
        public List<RunValue> RunValues { get; set; } = new();

        public sealed class MovedFile
        {
            public string Original { get; set; } = string.Empty;
            public string Moved { get; set; } = string.Empty;
        }

        public sealed class RunValue
        {
            /// <summary>"HKCU" or "HKLM".</summary>
            public string Hive { get; set; } = string.Empty;
            public string Key { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public string SavedTo { get; set; } = string.Empty;
        }

        internal static StartupOffRecord From(string root, StartupChanges changes) => new()
        {
            Root = root,
            Files = changes.Files.Select(f => new MovedFile { Original = f.Original, Moved = f.Moved }).ToList(),
            Tasks = changes.Tasks.ToList(),
            RunValues = changes.RunValues.Select(v => new RunValue
            {
                Hive = v.Hive == Registry.LocalMachine ? "HKLM" : "HKCU",
                Key = v.Key,
                Name = v.Name,
                Value = v.Value.ToString() ?? string.Empty,
                Kind = v.Kind.ToString(),
                SavedTo = v.SavedTo
            }).ToList()
        };

        internal StartupChanges ToChanges()
        {
            var changes = new StartupChanges();
            changes.Files.AddRange(Files.Select(f => (f.Original, f.Moved)));
            changes.Tasks.AddRange(Tasks);
            // Run values are command lines (REG_SZ or REG_EXPAND_SZ).
            foreach (var v in RunValues)
                changes.RunValues.Add((v.Hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser, v.Key, v.Name, v.Value,
                    Enum.TryParse<RegistryValueKind>(v.Kind, out var kind) && kind == RegistryValueKind.ExpandString ? kind : RegistryValueKind.String,
                    v.SavedTo));
            return changes;
        }
    }
}
