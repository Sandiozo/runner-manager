using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace RunnerManager;

/// <summary>What a busy runner is doing on its PC: the running step's latest output and the processes of its job.</summary>
public sealed record RunnerJobActivity
{
    public string[] Output { get; init; } = Array.Empty<string>();
    public RunnerProcess[] Processes { get; init; } = Array.Empty<RunnerProcess>();
    public DateTime CapturedUtc { get; init; }
}

public sealed record RunnerProcess
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>The first arguments, for example "build App.sln". Arguments that look like secrets are left out.</summary>
    public string Arguments { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public long MemoryBytes { get; init; }
}

/// <summary>Reads a runner's live job activity on this PC. Used by the dashboard for local runners and by the fleet agent.</summary>
public static class RunnerActivity
{
    private const int OutputLines = 40;
    private const int MaxProcesses = 8;
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z ?", RegexOptions.Compiled);
    private static readonly Regex AnsiCodes = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    // actions/checkout passes the repository token to git as -c http.extraheader="AUTHORIZATION: basic …".
    private static readonly string[] SecretHints =
    {
        "token", "password", "passwd", "pwd", "secret", "apikey", "api_key", "api-key", "pat=", "authorization", "extraheader",
        "bearer", "credential", "auth", "pass:", "pass="
    };
    /// <summary>Raw GitHub, GitLab and Slack tokens, alone or as a value (ghp_…, github_pat_…, glpat-…, xoxb-…).</summary>
    private static readonly Regex TokenValue = new(@"(^|[=:])(gh[pousr]_|github_pat_|glpat-|xox[abprs]-)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>A password attached to -p or -P (mysql -pSecret, 7z -pSecret), but not an MSBuild property (-p:Name=Value).</summary>
    private static readonly Regex AttachedPassword = new(@"^-[pP]([^:]|$)", RegexOptions.Compiled);
    /// <summary>User and password with -u or --user (curl -u user:pass, --user=user:pass, -uuser:pass).</summary>
    private static readonly Regex AttachedUser = new(@"^(-u|--user=)\S*:", RegexOptions.Compiled);
    /// <summary>The user and password in a URL (https://user:pass@host/…).</summary>
    private static readonly Regex UrlCredentials = new(@"(?<=://)[^/@\s]+@", RegexOptions.Compiled);
    private static readonly object CpuGate = new();
    // Processor time per process at the previous reading, keyed by ID and start time so a reused ID starts over.
    private static readonly Dictionary<(int Id, string Started), (ulong Cpu, DateTime When)> CpuReadings = new();

    // Finding a runner scans services and processes, so its folder is remembered between the requests of a job view.
    private static readonly Dictionary<string, string> RootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="runnerKey">The runner's service name, or its folder when it has no service.</param>
    public static async Task<RunnerJobActivity> ReadAsync(string runnerKey)
    {
        var root = await FindRootAsync(runnerKey);
        return await Task.Run(() =>
        {
            var processes = ProcessSnapshot.Read();
            return new RunnerJobActivity
            {
                Output = ReadOutput(root, OutputLines, OutputNotBefore(WorkerStartedUtc(root, processes))),
                Processes = ReadProcesses(root, processes),
                CapturedUtc = DateTime.UtcNow
            };
        });
    }

    internal static async Task<string> FindRootAsync(string runnerKey)
    {
        lock (RootCache)
            if (RootCache.TryGetValue(runnerKey, out var cached) && StillHoldsRunner(cached, runnerKey)) return cached;
        var runner = await RunnerService.FindRunnerAsync(runnerKey)
                     ?? throw new InvalidOperationException("The runner was not found on this PC.");
        lock (RootCache) RootCache[runnerKey] = runner.InstallPath;
        return runner.InstallPath;
    }

    /// <summary>
    /// Whether a remembered folder still holds the runner. "Remove and keep files" leaves the folder without .runner, and
    /// a runner created again with the same name (and service name) gets a new folder.
    /// </summary>
    internal static bool StillHoldsRunner(string root, string runnerKey)
    {
        if (!File.Exists(Path.Combine(root, ".runner"))) return false;
        if (!runnerKey.StartsWith("actions.runner.", StringComparison.OrdinalIgnoreCase))
            return root.TrimEnd('\\').Equals(runnerKey.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        // config.cmd writes the service name to .service.
        try { return File.ReadAllText(Path.Combine(root, ".service")).Trim().TrimStart('﻿').Equals(runnerKey, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The file the runner writes the running step's output to. The runner keeps it in _diag\pages until it uploads it
    /// to GitHub, so it only exists while a step runs. A hung step's file can be hours old, so there is no age limit.
    /// </summary>
    /// <param name="notBeforeUtc">When the job's Runner.Worker started: older files are left over from an earlier job.</param>
    internal static OutputFile? CurrentOutputFile(string root, DateTime? notBeforeUtc = null)
    {
        var folder = Path.Combine(root, "_diag", "pages");
        List<string> paths;
        try { paths = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.log").ToList() : new List<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        OutputFile? newest = null;
        foreach (var path in paths)
        {
            if (ReadOutputFile(path) is not { } file || (notBeforeUtc is { } notBefore && file.ChangedUtc < notBefore)) continue;
            if (newest == null || file.ChangedUtc > newest.ChangedUtc) newest = file;
        }
        return newest;
    }

    /// <summary>
    /// The file's size and times, asked from the file itself: while the runner holds it open, the folder listing keeps
    /// the size and time it had when it was opened.
    /// </summary>
    private static OutputFile? ReadOutputFile(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var created = File.GetCreationTimeUtc(handle);
            var written = File.GetLastWriteTimeUtc(handle);
            return new OutputFile(path, RandomAccess.GetLength(handle), written > created ? written : created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <param name="ChangedUtc">When the file was last written, or created when that is later (a new page).</param>
    internal sealed record OutputFile(string Path, long Length, DateTime ChangedUtc)
    {
        /// <summary>Changes whenever the runner writes to the file.</summary>
        public string State => $"{System.IO.Path.GetFileName(Path)}|{Length}|{ChangedUtc.Ticks}";
    }

    /// <summary>The last lines of the running step's output, without timestamps and color codes.</summary>
    internal static string[] ReadOutput(string root, int lines)
    {
        DateTime? workerStarted = null;
        try { workerStarted = WorkerStartedUtc(root, ProcessSnapshot.Read(WorkerName)); }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { }
        return ReadOutput(root, lines, OutputNotBefore(workerStarted));
    }

    private static string[] ReadOutput(string root, int lines, DateTime? notBeforeUtc)
    {
        var file = CurrentOutputFile(root, notBeforeUtc);
        if (file == null) return Array.Empty<string>();
        try { return CleanLines(ReadTail(file.Path, 64 * 1024)).TakeLast(lines).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // the first line is probably cut off
        return lines;
    }

    /// <summary>Removes timestamps, color codes and the runner's group markers from log lines.</summary>
    internal static IEnumerable<string> CleanLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = AnsiCodes.Replace(Timestamp.Replace(raw.TrimEnd('\r').TrimStart('﻿'), string.Empty), string.Empty);
            if (line.StartsWith("##[endgroup]", StringComparison.Ordinal)) continue;
            if (line.StartsWith("##[group]", StringComparison.Ordinal)) yield return "▸ " + line[9..];
            else if (line.StartsWith("##[error]", StringComparison.Ordinal)) yield return "Error: " + line[9..];
            else if (line.StartsWith("##[warning]", StringComparison.Ordinal)) yield return "Warning: " + line[11..];
            else yield return line;
        }
    }

    /// <summary>The runner's job process (Runner.Worker) and everything it started, busiest first.</summary>
    internal static RunnerProcess[] ReadProcesses(string root, ProcessSnapshot? processes = null)
    {
        var job = JobRows(root, (processes ?? ProcessSnapshot.Read()).Rows);
        if (job.Count == 0) return Array.Empty<RunnerProcess>();

        var now = DateTime.UtcNow;
        var cores = Environment.ProcessorCount;
        var result = new List<RunnerProcess>();
        lock (CpuGate)
        {
            foreach (var process in job.Where(p => !p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)))
            {
                var key = (process.Id, process.Started);
                double cpu;
                if (CpuReadings.TryGetValue(key, out var previous) && now > previous.When)
                    cpu = (process.Cpu - Math.Min(previous.Cpu, process.Cpu)) / ((now - previous.When).Ticks * (double)cores) * 100;
                else
                {
                    // First reading: the average since the process started.
                    var started = ParseStarted(process.Started);
                    cpu = started == null || now <= started ? 0 : process.Cpu / ((now - started.Value).Ticks * (double)cores) * 100;
                }
                CpuReadings[key] = (process.Cpu, now);
                result.Add(new RunnerProcess
                {
                    Id = process.Id,
                    Name = process.Name,
                    Arguments = ShortArguments(process.CommandLine),
                    CpuPercent = Math.Round(Math.Clamp(cpu, 0, 100), 1),
                    MemoryBytes = process.Memory
                });
            }
            foreach (var stale in CpuReadings.Where(r => now - r.Value.When > TimeSpan.FromMinutes(2)).Select(r => r.Key).ToList())
                CpuReadings.Remove(stale);
        }
        return result.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryBytes).Take(MaxProcesses).ToArray();
    }

    private const string WorkerName = "Runner.Worker.exe";

    /// <summary>
    /// The PC's processes with their command lines. A full Win32_Process query is slow, so one reading is shared by
    /// everything that looks at jobs in the same update.
    /// </summary>
    internal sealed class ProcessSnapshot(IReadOnlyList<ProcessRow> rows)
    {
        public IReadOnlyList<ProcessRow> Rows { get; } = rows;

        /// <param name="name">Only processes with this file name, for a quick look.</param>
        public static ProcessSnapshot Read(string? name = null)
        {
            var all = new List<ProcessRow>();
            var query = "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, WorkingSetSize, KernelModeTime, UserModeTime, CreationDate FROM Win32_Process"
                        + (name == null ? string.Empty : $" WHERE Name = '{name.Replace("'", string.Empty)}'");
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    all.Add(new ProcessRow(
                        Convert.ToInt32(item["ProcessId"]), Convert.ToInt32(item["ParentProcessId"]),
                        item["Name"]?.ToString() ?? string.Empty, item["ExecutablePath"]?.ToString() ?? string.Empty,
                        item["CommandLine"]?.ToString() ?? string.Empty, Convert.ToInt64(item["WorkingSetSize"] ?? 0L),
                        Convert.ToUInt64(item["KernelModeTime"] ?? 0UL) + Convert.ToUInt64(item["UserModeTime"] ?? 0UL),
                        item["CreationDate"]?.ToString() ?? string.Empty));
                }
            }
            return new ProcessSnapshot(all);
        }
    }

    private static bool IsWorkerOf(ProcessRow process, string root)
        => process.Name.Equals(WorkerName, StringComparison.OrdinalIgnoreCase)
           && process.Path.StartsWith(Path.Combine(root, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>When the runner's earliest running Runner.Worker started, or null when it runs no job.</summary>
    private static DateTime? WorkerStartedUtc(string root, ProcessSnapshot processes)
        => processes.Rows.Where(p => IsWorkerOf(p, root)).Select(p => ParseStarted(p.Started)).Where(s => s != null).Min();

    /// <summary>
    /// The job's processes: the runner's Runner.Worker processes and everything they started. A process whose parent
    /// ended keeps that parent's ID, and Windows can give the ID to a new process, so a child counts only when it was
    /// created at or after its parent (explorer.exe, whose parent userinit.exe always exits, would otherwise join a job).
    /// </summary>
    internal static List<ProcessRow> JobRows(string root, IReadOnlyList<ProcessRow> all)
    {
        var children = all.ToLookup(p => p.ParentId);
        var job = new List<ProcessRow>();
        var queue = new Queue<ProcessRow>(all.Where(p => IsWorkerOf(p, root)));
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            var process = queue.Dequeue();
            if (!seen.Add(process.Id)) continue;
            job.Add(process);
            var parentStarted = ParseStarted(process.Started);
            foreach (var child in children[process.Id])
            {
                if (child.Id == process.Id) continue;
                if (parentStarted is { } parent && ParseStarted(child.Started) is { } started && started < parent) continue;
                queue.Enqueue(child);
            }
        }
        return job;
    }

    /// <summary>
    /// A reading of a running job for <see cref="JobWatch"/>: when its Runner.Worker started, the processor time of each
    /// of its processes, and the state of the running step's output file. Null when the runner runs no job.
    /// </summary>
    internal static JobSample? ReadJobSample(string root, ProcessSnapshot? processes = null)
    {
        var job = JobRows(root, (processes ?? ProcessSnapshot.Read()).Rows);
        var worker = job.FirstOrDefault(p => p.Name.Equals(WorkerName, StringComparison.OrdinalIgnoreCase));
        if (worker == null) return null;
        var workerStarted = ParseStarted(worker.Started);
        var outputState = CurrentOutputFile(root, OutputNotBefore(workerStarted))?.State ?? string.Empty;
        var times = new Dictionary<string, ulong>();
        foreach (var process in job) times[$"{process.Id}|{process.Started}"] = process.Cpu;
        return new JobSample(workerStarted ?? DateTime.UtcNow, times, outputState);
    }

    /// <summary>
    /// Stops everything the runner's running job started (and what those started), leaving the runner and its
    /// Runner.Worker running: the step fails, and the job goes on to its next step or ends. For a hung build tool, such
    /// as a Unity compiler that no longer does anything. Returns the names of the processes it stopped.
    /// </summary>
    public static async Task<List<string>> StopJobProcessesAsync(string runnerKey)
    {
        var root = await FindRootAsync(runnerKey);
        return await Task.Run(() => StopJobProcesses(root));
    }

    internal static List<string> StopJobProcesses(string root)
    {
        var job = JobRows(root, ProcessSnapshot.Read().Rows);
        var workers = job.Where(p => p.Name.Equals(WorkerName, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        if (workers.Count == 0) throw new InvalidOperationException("The runner is not running a job.");
        var parents = job.ToDictionary(p => p.Id, p => p.ParentId);
        var stopped = new List<string>();
        // Not the worker's console host: stopping it ends the worker too.
        foreach (var top in job.Where(p => workers.Contains(p.ParentId) && !workers.Contains(p.Id)
                                           && !p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var running = System.Diagnostics.Process.GetProcessById(top.Id);
                // The same process, not a new one that got its ID after it ended.
                if (ParseStarted(top.Started) is { } started && Math.Abs((running.StartTime.ToUniversalTime() - started).TotalSeconds) > 2) continue;
                var tree = job.Where(p => IsUnder(p.Id, top.Id, parents)).Select(p => Path.GetFileNameWithoutExtension(p.Name)).ToList();
                running.Kill(entireProcessTree: true);
                running.WaitForExit(5000);
                stopped.AddRange(tree);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return stopped.Where(n => !n.Equals("conhost", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether a process is <paramref name="ancestor"/> or was started by it, directly or through others.</summary>
    private static bool IsUnder(int id, int ancestor, Dictionary<int, int> parents)
    {
        for (var hops = 0; hops < 64; hops++)
        {
            if (id == ancestor) return true;
            if (!parents.TryGetValue(id, out var parent) || parent == id) return false;
            id = parent;
        }
        return false;
    }

    /// <summary>
    /// The first few arguments of a command line, without the program itself: enough to tell "dotnet build" from
    /// "dotnet test". Paths are shortened to their file name, and arguments that look like secrets are left out.
    /// </summary>
    internal static string ShortArguments(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.Length == 0) return string.Empty;
        var end = text[0] == '"' ? text.IndexOf('"', 1) : text.IndexOf(' ');
        if (end < 0) return string.Empty;
        var program = Path.GetFileNameWithoutExtension(text[..end].Trim('"').TrimEnd());
        // net use and net user take passwords as plain arguments: only the subcommand is shown.
        var limit = program.Equals("net", StringComparison.OrdinalIgnoreCase) || program.Equals("net1", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
        var tokens = text[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => UrlCredentials.Replace(t.Trim('"'), string.Empty)).Where(t => t.Length > 0).ToList();
        var parts = new List<string>();
        for (var i = 0; i < tokens.Count && parts.Count < limit; i++)
        {
            var clean = tokens[i];
            // Everything from a secret on is left out: the value often follows its option.
            if (LooksSecret(clean, i + 1 < tokens.Count ? tokens[i + 1] : string.Empty)) break;
            parts.Add(clean.Contains('\\') || clean.Contains('/') ? Path.GetFileName(clean.TrimEnd('\\', '/')) : clean);
        }
        var joined = string.Join(' ', parts);
        return joined.Length > 60 ? joined[..57] + "…" : joined;
    }

    private static bool LooksSecret(string token, string next)
        => SecretHints.Any(hint => token.Contains(hint, StringComparison.OrdinalIgnoreCase))
           || TokenValue.IsMatch(token)
           || AttachedPassword.IsMatch(token)
           || AttachedUser.IsMatch(token)
           || ((token is "-u" or "--user") && next.Contains(':'))
           || token.Equals("/p", StringComparison.OrdinalIgnoreCase) || token.Equals("/rp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Output files written before the job's worker started belong to an earlier job (a minute of slack for clocks).</summary>
    private static DateTime? OutputNotBefore(DateTime? workerStartedUtc) => workerStartedUtc?.AddMinutes(-1);

    private static DateTime? ParseStarted(string dmtf)
    {
        try { return dmtf.Length == 0 ? null : ManagementDateTimeConverter.ToDateTime(dmtf).ToUniversalTime(); }
        catch { return null; }
    }

    internal sealed record ProcessRow(int Id, int ParentId, string Name, string Path, string CommandLine, long Memory, ulong Cpu, string Started);

    /// <param name="ProcessorTime">Processor time in 100 ns units, per process ("ID|start time").</param>
    internal sealed record JobSample(DateTime WorkerStartedUtc, Dictionary<string, ulong> ProcessorTime, string OutputState);
}
