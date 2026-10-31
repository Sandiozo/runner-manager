using System.IO;
using System.Text.Json;

namespace RunnerManager;

public enum RunnerConcern { None, Stuck, RunningLong, IdleWithWaitingJobs }

/// <summary>Whether a runner needs attention: its job makes no progress or runs far too long, or it idles while jobs wait.</summary>
/// <param name="Label">Short, for the status pill: "Stuck", "Running long" or "Idle · jobs waiting".</param>
/// <param name="Detail">One or two sentences for the details, notifications and AI assistants.</param>
/// <param name="Subject">What the concern is about (a job's ID or start), so it is reported once.</param>
public sealed record RunnerHealth(RunnerConcern Concern, string Label, string Detail, string Subject = "")
{
    public static readonly RunnerHealth Healthy = new(RunnerConcern.None, string.Empty, string.Empty);
    public bool IsProblem => Concern != RunnerConcern.None;
}

/// <summary>A job a runner can take: running on one of the runners, or queued.</summary>
public sealed record QueueEntry(string Title, string Detail, string Url, bool Running);

/// <summary>A queued job that no runner in the fleet will take.</summary>
public sealed record UnservedJob(long JobId, string Repository, string Text);

/// <summary>The concerns and queues of every runner in the fleet, and queued jobs that no runner takes.</summary>
public sealed class FleetHealth
{
    public Dictionary<string, RunnerHealth> Runners { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<QueueEntry>> Queues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UnservedJob> Unserved { get; } = new();
}

/// <summary>
/// The rules for stuck jobs, jobs that run far too long, and runners that idle while jobs wait for them. Used by the
/// dashboard (status, details and notifications) and by the AI assistant tools, so both say the same.
/// </summary>
public static class JobHealth
{
    public const int DefaultQuietMinutes = 15;
    /// <summary>A queued job that an idle runner could take has waited this long: GitHub normally assigns it within seconds.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(5);
    /// <summary>The limit for a job without earlier successful runs to compare with.</summary>
    public static readonly TimeSpan NoHistoryLimit = TimeSpan.FromHours(2);
    /// <summary>Short jobs get at least this long before they count as running long.</summary>
    public static readonly TimeSpan MinimumLongLimit = TimeSpan.FromMinutes(20);
    private const int LongFactor = 3;

    public static string Key(MachineRunnerStatus runner) => $"{runner.MachineKey}|{runner.CommandKey}";

    /// <summary>How long a job may run: three times its usual duration (at least 20 minutes), or 2 hours without history.</summary>
    public static TimeSpan LongLimit(TimeSpan? usual)
        => usual is { } u ? TimeSpan.FromTicks(Math.Max(u.Ticks * LongFactor, MinimumLongLimit.Ticks)) : NoHistoryLimit;

    /// <summary>The median of earlier durations, or null with fewer than three to go by.</summary>
    public static TimeSpan? Usual(IEnumerable<TimeSpan> durations)
    {
        var sorted = durations.Where(d => d > TimeSpan.Zero).OrderBy(d => d).ToList();
        return sorted.Count < 3 ? null : sorted[sorted.Count / 2];
    }

    /// <summary>"45 s", "22 min" or "1 h 40 min".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return $"{Math.Max(0, (int)span.TotalSeconds)} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min";
        return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    /// <summary>The concern of a busy runner: a quiet job first (it is the more urgent), then one that runs far too long.</summary>
    public static RunnerHealth ForBusyRunner(DateTime? quietSinceUtc, DateTime? jobStartedUtc, TimeSpan? usual, TimeSpan quietLimit,
        string subject, DateTime nowUtc)
    {
        if (quietSinceUtc is { } quiet && nowUtc - quiet >= quietLimit)
            return new RunnerHealth(RunnerConcern.Stuck, "Stuck",
                $"The job has used no CPU and printed no output for {Duration(nowUtc - quiet)}.", $"stuck|{subject}");
        if (jobStartedUtc is { } started && nowUtc - started >= LongLimit(usual))
            return new RunnerHealth(RunnerConcern.RunningLong, "Running long",
                usual is { } u
                    ? $"The job has run for {Duration(nowUtc - started)}. It usually takes {Duration(u)}."
                    : $"The job has run for {Duration(nowUtc - started)}, longer than {Duration(NoHistoryLimit)}.",
                $"long|{subject}");
        return RunnerHealth.Healthy;
    }

    /// <summary>
    /// The concern of an idle runner: jobs it can take have been queued for <see cref="WaitLimit"/> while it was idle.
    /// <paramref name="idleSinceUtc"/> is when it was first seen idle, or null when that is unknown.
    /// </summary>
    public static RunnerHealth ForIdleRunner(IReadOnlyCollection<GitHubJobState> waiting, DateTime? idleSinceUtc, DateTime nowUtc)
    {
        var waits = waiting
            .Select(j => (Job: j, Since: idleSinceUtc is { } idle && idle > (j.CreatedUtc ?? DateTime.MinValue) ? idle : j.CreatedUtc ?? nowUtc))
            .Where(w => nowUtc - w.Since >= WaitLimit)
            .ToList();
        if (waits.Count == 0) return RunnerHealth.Healthy;
        var longest = nowUtc - waits.Min(w => w.Since);
        var jobs = waits.Count == 1 ? "A job it can run has" : $"{waits.Count} jobs it can run have";
        return new RunnerHealth(RunnerConcern.IdleWithWaitingJobs, "Idle · jobs waiting",
            $"{jobs} waited {Duration(longest)} while it was idle, but GitHub has not given {(waits.Count == 1 ? "it" : "them")} to this runner.",
            "waiting|" + string.Join(',', waits.Select(w => w.Job.Id).Order()));
    }

    /// <summary>The jobs a runner can take, as GitHub lists them for its repository: running ones first, then queued ones by age.</summary>
    public static List<QueueEntry> QueueFor(IEnumerable<GitHubJobState> jobs, DateTime nowUtc)
        => jobs.OrderBy(j => j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(j => j.CreatedUtc ?? nowUtc)
            .Select(j => j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
                ? new QueueEntry($"{j.WorkflowName} → {j.Name}  ·  run #{j.RunNumber}",
                    $"Running{(j.RunnerName.Length > 0 ? $" on {j.RunnerName}" : string.Empty)}" +
                    (j.StartedUtc is { } s ? $"  ·  {Duration(nowUtc - s)}" : string.Empty), j.HtmlUrl, true)
                : new QueueEntry($"{j.WorkflowName} → {j.Name}  ·  run #{j.RunNumber}",
                    $"Waiting  ·  {Duration(nowUtc - (j.CreatedUtc ?? nowUtc))}", j.HtmlUrl, false))
            .ToList();

    private static bool IsActive(GitHubJobState job)
        => job.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) || job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The concerns and queues of all runners. GitHub is asked once per repository (its answers are cached for a few
    /// seconds); without GitHub only stuck jobs are found. <paramref name="idleSince"/> holds when each runner was first
    /// seen idle, by <see cref="Key"/>; without it, a job's wait counts from when it was queued.
    /// </summary>
    public static async Task<FleetHealth> EvaluateAsync(IReadOnlyCollection<MachineInfo> machines, TimeSpan quietLimit,
        IReadOnlyDictionary<string, DateTime>? idleSince, bool useGitHub, DateTime nowUtc)
    {
        var health = new FleetHealth();
        var runners = machines.SelectMany(m => m.Runners.Select(r => (Machine: m, Runner: r))).ToList();
        var jobsByRepository = new Dictionary<string, List<GitHubJobState>>(StringComparer.OrdinalIgnoreCase);
        if (useGitHub)
        {
            var repositories = runners.Select(x => x.Runner.RepositoryPath).Where(p => p.Contains('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            var states = await Task.WhenAll(repositories.Select(GitHubApi.GetRepositoryJobsAsync));
            for (var i = 0; i < repositories.Count; i++)
                if (states[i] != null) jobsByRepository[repositories[i]] = states[i]!.Jobs.Where(IsActive).ToList();
        }

        foreach (var (machine, runner) in runners)
        {
            var key = Key(runner);
            var labels = RunnerLabels.Available(runner.CustomLabels);
            var jobs = jobsByRepository.TryGetValue(runner.RepositoryPath, out var list)
                ? list.Where(j => RunnerLabels.CanRun(j.Labels, labels)).ToList()
                : new List<GitHubJobState>();
            if (jobs.Count > 0) health.Queues[key] = QueueFor(jobs, nowUtc);

            if (runner.IsBusy)
            {
                var current = jobs.FirstOrDefault(j => j.RunnerId != 0 && j.RunnerId == runner.AgentId
                                                       && j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase));
                TimeSpan? usual = null;
                if (current != null && useGitHub)
                {
                    try { usual = await GitHubApi.GetUsualJobDurationAsync(runner.RepositoryPath, current.WorkflowName, current.Name); }
                    catch { }
                }
                var started = current?.StartedUtc ?? runner.JobStartedUtc;
                var subject = current != null ? current.Id.ToString() : $"{runner.CommandKey}@{runner.JobStartedUtc?.Ticks}";
                health.Runners[key] = ForBusyRunner(runner.QuietSinceUtc, started, usual, quietLimit, subject, nowUtc);
            }
            else if (runner.IsRunning && machine.IsReachable)
            {
                var waiting = jobs.Where(j => j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)).ToList();
                health.Runners[key] = ForIdleRunner(waiting, idleSince != null && idleSince.TryGetValue(key, out var idle) ? idle : null, nowUtc);
            }
        }

        foreach (var (repository, jobs) in jobsByRepository)
        {
            var candidates = runners.Where(x => x.Runner.RepositoryPath.Equals(repository, StringComparison.OrdinalIgnoreCase)
                                                || x.Runner.Scope.Equals($"orgs/{repository.Split('/')[0]}", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var job in jobs.Where(j => j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                                                && j.Labels.Contains("self-hosted", StringComparer.OrdinalIgnoreCase)
                                                && nowUtc - (j.CreatedUtc ?? nowUtc) >= WaitLimit))
            {
                var able = candidates.Where(x => RunnerLabels.CanRun(job.Labels, RunnerLabels.Available(x.Runner.CustomLabels))).ToList();
                if (able.Any(x => x.Runner.IsRunning && x.Machine.IsReachable)) continue;
                var waited = Duration(nowUtc - (job.CreatedUtc ?? nowUtc));
                var title = $"{repository}: {job.WorkflowName} → {job.Name} has waited {waited}.";
                health.Unserved.Add(new UnservedJob(job.Id, repository, able.Count == 0
                    ? $"{title} No runner in the fleet has the labels {string.Join(", ", job.Labels)}."
                    : $"{title} The runners that can take it are stopped or offline: {string.Join(", ", able.Select(x => $"{x.Runner.TitleText} on {x.Machine.MachineName}"))}."));
            }
        }
        return health;
    }
}

/// <summary>
/// Watches the jobs of busy runners on this PC: a job makes progress while its processes use CPU, start new processes or
/// write output. Read every 30 seconds at most; the fleet agent reports the result (protocol 14).
/// </summary>
public static class JobWatch
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);
    /// <summary>Less than this share of one processor core, over the time between two readings, counts as no CPU use.</summary>
    internal const double ActiveCorePercent = 5;
    /// <summary>Quiet for less than this is normal between steps, so it is not reported.</summary>
    private static readonly TimeSpan ReportAfter = TimeSpan.FromMinutes(1);

    private sealed class State
    {
        public DateTime WorkerStartedUtc;
        public Dictionary<string, ulong> ProcessorTime = new();
        public string OutputState = string.Empty;
        public DateTime SampledUtc;
        public DateTime LastActiveUtc;
    }

    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes a new reading of each busy runner's job when the last one is 30 seconds old, and forgets idle runners.</summary>
    public static void Update(IEnumerable<RunnerInfo> runners)
    {
        var now = DateTime.UtcNow;
        var busy = runners.Where(r => r.IsRunning && r.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.InstallPath).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (States)
        {
            foreach (var gone in States.Keys.Where(k => !busy.Contains(k)).ToList()) States.Remove(gone);
            busy.RemoveWhere(root => States.TryGetValue(root, out var state) && now - state.SampledUtc < SampleInterval);
        }
        foreach (var root in busy)
        {
            RunnerActivity.JobSample? sample;
            try { sample = RunnerActivity.ReadJobSample(root); }
            catch { continue; }
            lock (States)
            {
                if (sample == null) { States.Remove(root); continue; }
                if (!States.TryGetValue(root, out var state) || state.WorkerStartedUtc != sample.WorkerStartedUtc)
                {
                    States[root] = new State
                    {
                        WorkerStartedUtc = sample.WorkerStartedUtc, ProcessorTime = sample.ProcessorTime,
                        OutputState = sample.OutputState, SampledUtc = now, LastActiveUtc = now
                    };
                    continue;
                }
                if (MadeProgress(state.ProcessorTime, sample.ProcessorTime, state.OutputState, sample.OutputState, now - state.SampledUtc))
                    state.LastActiveUtc = now;
                state.ProcessorTime = sample.ProcessorTime;
                state.OutputState = sample.OutputState;
                state.SampledUtc = now;
            }
        }
    }

    /// <summary>Progress between two readings: a new process, new output, or CPU use of at least 5% of one core.</summary>
    internal static bool MadeProgress(IReadOnlyDictionary<string, ulong> before, IReadOnlyDictionary<string, ulong> now,
        string outputBefore, string outputNow, TimeSpan elapsed)
    {
        if (outputNow != outputBefore) return true;
        if (now.Keys.Any(k => !before.ContainsKey(k))) return true;
        if (elapsed <= TimeSpan.Zero) return false;
        var used = now.Where(p => before.ContainsKey(p.Key)).Sum(p => (double)(p.Value - Math.Min(p.Value, before[p.Key])));
        return used / elapsed.Ticks * 100 >= ActiveCorePercent;
    }

    /// <summary>When the runner's job started, and since when it has made no progress (null while it does).</summary>
    public static (DateTime? StartedUtc, DateTime? QuietSinceUtc) For(string root)
    {
        lock (States)
        {
            if (!States.TryGetValue(root, out var state)) return (null, null);
            var quiet = DateTime.UtcNow - state.LastActiveUtc >= ReportAfter ? state.LastActiveUtc : (DateTime?)null;
            return (state.WorkerStartedUtc, quiet);
        }
    }
}

/// <summary>
/// The alerts Runner Manager showed (failed runs, stuck jobs, PCs going offline, …), kept in a file so that AI assistants
/// can read them with recent_alerts.
/// </summary>
public static class AlertLog
{
    private static readonly string LogPath = Path.Combine(FleetConfig.StateDir, "alerts.log");
    private static readonly object Gate = new();
    private const int Keep = 300;

    public sealed record Entry(DateTime Utc, bool Warning, string Text);

    public static void Append(string text, bool warning)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(FleetConfig.StateDir);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(new Entry(DateTime.UtcNow, warning, text)) + Environment.NewLine);
                var lines = File.ReadAllLines(LogPath);
                if (lines.Length > Keep + 100) File.WriteAllLines(LogPath, lines[^Keep..]);
            }
        }
        catch { }
    }

    /// <summary>The alerts of the last <paramref name="span"/>, newest first.</summary>
    public static List<Entry> Recent(TimeSpan span)
    {
        try
        {
            if (!File.Exists(LogPath)) return new List<Entry>();
            var since = DateTime.UtcNow - span;
            var entries = new List<Entry>();
            foreach (var line in File.ReadLines(LogPath))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line) is { } entry && entry.Utc >= since) entries.Add(entry);
                }
                catch (JsonException) { }
            }
            entries.Reverse();
            return entries;
        }
        catch { return new List<Entry>(); }
    }
}
