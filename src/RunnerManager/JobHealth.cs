using System.IO;
using System.Text.Json;

namespace RunnerManager;

public enum RunnerConcern { None, Stuck, RunningLong, IdleWithWaitingJobs, RunsTwice, WorkFolderOwner }

/// <summary>
/// Whether a runner needs attention: its job makes no progress or runs far too long, it idles while jobs wait, it runs
/// twice (its Windows service and a console window), or its service cannot use its work folder.
/// </summary>
/// <param name="Label">Short, for the status pill: "Stuck", "Running long", "Idle · jobs waiting", "Runs twice" or "Work folder owner".</param>
/// <param name="Detail">One or two sentences for the details, notifications and AI assistants.</param>
/// <param name="Subject">What the concern is about (a job's ID or start), so it is reported once.</param>
public sealed record RunnerHealth(RunnerConcern Concern, string Label, string Detail, string Subject = "")
{
    public static readonly RunnerHealth Healthy = new(RunnerConcern.None, string.Empty, string.Empty);
    public bool IsProblem => Concern != RunnerConcern.None;
}

/// <summary>A job a runner can take: running on one of the runners, or queued.</summary>
public sealed record QueueEntry(string Title, string Detail, string Url, bool Running)
{
    /// <summary>The row is a link (a button) when GitHub gave the job's address, and plain text otherwise.</summary>
    public System.Windows.Visibility LinkVisibility => string.IsNullOrWhiteSpace(Url) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility TextVisibility => string.IsNullOrWhiteSpace(Url) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

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
    /// <summary>
    /// When GitHub cannot tell a job's usual duration, the job is not judged by <see cref="NoHistoryLimit"/>: a normal
    /// long job would be reported. Once the usual duration has stayed unknown this long, it is, so that a job that runs
    /// away while it keeps printing output is still reported.
    /// </summary>
    public static readonly TimeSpan UnknownUsualLimit = NoHistoryLimit * 2;
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

    /// <summary>
    /// The concern that the runner's own status shows, which comes before the job checks: its Windows service runs and a
    /// console window runs it too or opens it at sign-in, or its running service's work folder has folders of another
    /// account, which git refuses. Healthy otherwise, and for runners from agents before 2.9.1, which do not tell.
    /// </summary>
    public static RunnerHealth ForSetup(MachineRunnerStatus runner)
    {
        if (runner.RunsTwice)
        {
            var what = runner.ConsoleAtSignIn
                ? "Its Windows service runs, and a startup entry also opens it in a console window when you sign in."
                : "Its Windows service runs, and a console window runs it too.";
            var fix = !runner.ConsoleAtSignIn
                ? "Stop the Windows service to keep the console window, or close the console window to keep the service."
                : runner.StartsAt == RunnerStart.SignInShared
                    ? "Stop the Windows service to keep the console window, or remove the runner from the startup entry that also starts other runners and close the console window to keep the service."
                    : "Stop the Windows service to keep the console window, or turn off “Start when you sign in” and close the console window to keep the service.";
            return new RunnerHealth(RunnerConcern.RunsTwice, "Runs twice",
                $"{what} Both copies use one registration, so only one of them gets jobs, and its jobs can fail or wait: " +
                $"the service runs as another account than the console window, and git refuses work folders that another account owns. {fix}",
                SetupSubject + "twice");
        }
        if (runner.WorkFolderOwner.Length > 0 && runner.ServiceInstalled && !runner.IsInteractive && runner.IsRunning)
        {
            var account = runner.ServiceAccount.Length > 0 ? runner.ServiceAccount : "the service's account";
            return new RunnerHealth(RunnerConcern.WorkFolderOwner, "Work folder owner",
                $"Folders in the work folder belong to {runner.WorkFolderOwner}, and the Windows service runs as {account}. " +
                "Git refuses folders that another account owns (“dubious ownership”), so jobs that check out code there fail. " +
                "Use “Clean work folder”: the service makes the folders again as its own.",
                $"{SetupSubject}owner|{runner.WorkFolderOwner}");
        }
        return RunnerHealth.Healthy;
    }

    /// <summary>How the subjects of <see cref="ForSetup"/>'s concerns start: such a concern lasts until it is fixed.</summary>
    internal const string SetupSubject = "setup|";

    /// <summary>A concern of <see cref="ForSetup"/>, which the runner's status shows rather than the dashboard's check.</summary>
    internal static bool IsSetupConcern(RunnerConcern concern) => concern is RunnerConcern.RunsTwice or RunnerConcern.WorkFolderOwner;

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

    /// <summary>What the health checks read from GitHub. Tests replace it.</summary>
    /// <param name="UsualDuration">See <see cref="GitHubApi.ReadUsualJobDurationAsync"/>: Read is false when GitHub could not be read.</param>
    internal sealed record GitHubReads(
        Func<string, Task<RepositoryWorkState?>> RepositoryJobs,
        Func<string, string, string, Task<(bool Read, TimeSpan? Usual)>> UsualDuration,
        Func<string, Task<IReadOnlyList<RegisteredRunner>?>> Runners)
    {
        public static readonly GitHubReads Live = new(GitHubApi.GetRepositoryJobsAsync, GitHubApi.ReadUsualJobDurationAsync, RegisteredRunners.GetAsync);
    }

    /// <summary>
    /// A busy runner's job as GitHub last listed it, by <see cref="Key"/>, with the start of the worker that runs it.
    /// <paramref name="UsualKnown"/> is false when the usual duration could not be read: then <paramref name="Usual"/>
    /// is null without meaning "no history".
    /// </summary>
    private sealed record KnownJob(DateTime? StartedUtc, TimeSpan? Usual, bool UsualKnown, DateTime WorkerStartedUtc);
    private static readonly Dictionary<string, KnownJob> KnownJobs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Since when the usual duration of a busy runner's job has been unknown, by <see cref="Key"/>, with the job's
    /// subject, so that the next job starts over. Guarded by <see cref="KnownJobs"/>'s lock.
    /// </summary>
    private static readonly Dictionary<string, (string Subject, DateTime SinceUtc)> UsualUnknownSince = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The last usual duration GitHub gave per "repository|workflow|job".</summary>
    private static readonly Dictionary<string, TimeSpan> KnownUsual = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The concerns and queues of all runners. GitHub is asked once per repository (its answers are cached for a few
    /// seconds); without GitHub only stuck jobs are found. <paramref name="idleSince"/> holds when each runner was first
    /// seen idle, by <see cref="Key"/>; without it, a job's wait counts from when it was queued.
    /// </summary>
    public static Task<FleetHealth> EvaluateAsync(IReadOnlyCollection<MachineInfo> machines, TimeSpan quietLimit,
        IReadOnlyDictionary<string, DateTime>? idleSince, bool useGitHub, DateTime nowUtc)
        => EvaluateAsync(machines, quietLimit, idleSince, useGitHub ? GitHubReads.Live : null, nowUtc);

    internal static async Task<FleetHealth> EvaluateAsync(IReadOnlyCollection<MachineInfo> machines, TimeSpan quietLimit,
        IReadOnlyDictionary<string, DateTime>? idleSince, GitHubReads? github, DateTime nowUtc)
    {
        var health = new FleetHealth();
        // Asked once, before GitHub is read: a PC counts as online for 30 seconds after its last report, by the clock,
        // and the reads below can take longer. Asked after them, every PC could look offline (as in the AI tools).
        var online = machines.Where(m => m.IsReachable).ToHashSet<MachineInfo>(ReferenceEqualityComparer.Instance);
        var runners = machines.SelectMany(m => m.Runners.Select(r => (Machine: m, Runner: r))).ToList();
        var jobsByRepository = new Dictionary<string, List<GitHubJobState>>(StringComparer.OrdinalIgnoreCase);
        // Repositories whose jobs GitHub did not answer for this time.
        var unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registered = new Dictionary<string, IReadOnlyList<RegisteredRunner>>(StringComparer.OrdinalIgnoreCase);
        if (github != null)
        {
            var repositories = runners.Select(x => x.Runner.RepositoryPath).Where(p => p.Contains('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            var states = await Task.WhenAll(repositories.Select(github.RepositoryJobs));
            for (var i = 0; i < repositories.Count; i++)
            {
                if (states[i] != null) jobsByRepository[repositories[i]] = states[i]!.Jobs.Where(IsActive).ToList();
                else unread.Add(repositories[i]);
            }
            // The runners' exact labels matter only where jobs are active.
            var scopes = jobsByRepository.Where(r => r.Value.Count > 0).Select(r => $"repos/{r.Key}").ToList();
            var lists = await Task.WhenAll(scopes.Select(github.Runners));
            for (var i = 0; i < scopes.Count; i++)
                if (lists[i] != null) registered[scopes[i]] = lists[i]!;
        }
        var labelsOf = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (machine, runner) in runners) labelsOf[Key(runner)] = LabelsOf(machine, runner, registered);
        lock (KnownJobs)
        {
            bool IsGone(string k) => !runners.Any(x => x.Runner.IsBusy && Key(x.Runner).Equals(k, StringComparison.OrdinalIgnoreCase));
            foreach (var gone in KnownJobs.Keys.Where(IsGone).ToList()) KnownJobs.Remove(gone);
            foreach (var gone in UsualUnknownSince.Keys.Where(IsGone).ToList()) UsualUnknownSince.Remove(gone);
        }

        foreach (var (machine, runner) in runners)
        {
            var key = Key(runner);
            var active = jobsByRepository.GetValueOrDefault(runner.RepositoryPath);
            var jobs = active?.Where(j => RunnerLabels.CanRun(j.Labels, labelsOf[key])).ToList() ?? new List<GitHubJobState>();
            if (jobs.Count > 0) health.Queues[key] = QueueFor(jobs, nowUtc);

            if (ForSetup(runner) is { IsProblem: true } setup)
                health.Runners[key] = setup;
            else if (runner.IsBusy)
                health.Runners[key] = await ForBusyRunnerAsync(runner, active, unread.Contains(runner.RepositoryPath), github, quietLimit, nowUtc);
            else if (runner.IsRunning && online.Contains(machine))
            {
                var waiting = jobs.Where(j => j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)).ToList();
                health.Runners[key] = ForIdleRunner(waiting, idleSince != null && idleSince.TryGetValue(key, out var idle) ? idle : null, nowUtc);
            }
        }

        // Runner IDs are numbered per repository or organization, so an ID is compared within its own list only.
        var fleetIds = runners.Where(x => x.Runner.AgentId > 0)
            .Select(x => FleetId(x.Runner.Scope, x.Runner.AgentId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (repository, jobs) in jobsByRepository)
        {
            var owner = repository.Split('/')[0];
            var candidates = runners.Where(x => x.Runner.RepositoryPath.Equals(repository, StringComparison.OrdinalIgnoreCase)
                                                || x.Runner.Scope.Equals($"orgs/{owner}", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var job in jobs.Where(j => j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                                                && nowUtc - (j.CreatedUtc ?? nowUtc) >= WaitLimit))
            {
                var able = candidates.Where(x => RunnerLabels.CanRun(job.Labels, labelsOf[Key(x.Runner)])).ToList();
                if (able.Any(x => x.Runner.IsRunning && online.Contains(x.Machine))) continue;
                if (able.Count == 0 && (!MeantForFleet(job.Labels, candidates.Select(x => labelsOf[Key(x.Runner)]))
                                        || await HasOtherRunnerAsync(github!, job.Labels, repository, owner, fleetIds))) continue;
                var waited = Duration(nowUtc - (job.CreatedUtc ?? nowUtc));
                var title = $"{repository}: {job.WorkflowName} → {job.Name} has waited {waited}.";
                health.Unserved.Add(new UnservedJob(job.Id, repository, able.Count == 0
                    ? $"{title} No runner in the fleet has the labels {string.Join(", ", job.Labels)}."
                    : $"{title} The runners that can take it are stopped or offline: {string.Join(", ", able.Select(x => $"{x.Runner.TitleText} on {x.Machine.MachineName}"))}."));
            }
        }
        return health;
    }

    /// <summary>
    /// A busy runner's concern. The worker's start names the job on its PC whether GitHub answers or not, so a job is
    /// reported once. When GitHub does not list the job this time, the job it listed last is used if the same worker
    /// still runs. When GitHub could not be read (the jobs, or the job's usual duration) and nothing is known, only a
    /// stuck job is found, since the 2-hour limit for jobs without history would report a normal long job as running
    /// long; once that has lasted <see cref="UnknownUsualLimit"/>, the 2-hour limit is used all the same.
    /// </summary>
    private static async Task<RunnerHealth> ForBusyRunnerAsync(MachineRunnerStatus runner, List<GitHubJobState>? active, bool unread,
        GitHubReads? github, TimeSpan quietLimit, DateTime nowUtc)
    {
        var key = Key(runner);
        var subject = runner.JobStartedUtc is { } workerStarted ? $"{runner.CommandKey}@{workerStarted.Ticks}" : null;
        var current = active == null ? null : GitHubApi.RunningJobOf(active, runner.AgentId, runner.AgentName, runner.JobStartedUtc);
        KnownJob? known;
        lock (KnownJobs) known = KnownJobs.GetValueOrDefault(key);
        var sameWorker = known != null && runner.JobStartedUtc == known.WorkerStartedUtc;
        DateTime? started;
        TimeSpan? usual = null;
        bool usualKnown;
        if (current != null && github != null)
        {
            (usualKnown, usual) = await UsualDurationAsync(github, runner.RepositoryPath, current);
            if (!usualKnown && sameWorker && known!.UsualKnown) (usualKnown, usual) = (true, known.Usual);
            started = current.StartedUtc ?? runner.JobStartedUtc;
            subject ??= current.Id.ToString();
            if (runner.JobStartedUtc is { } worker) lock (KnownJobs) KnownJobs[key] = new KnownJob(started, usual, usualKnown, worker);
        }
        else if (sameWorker)
        {
            started = known!.StartedUtc;
            usual = known.Usual;
            usualKnown = known.UsualKnown;
        }
        else
        {
            started = runner.JobStartedUtc;
            usualKnown = !unread;
        }
        subject ??= $"{runner.CommandKey}@";
        // Without the usual duration the limit is not known: the 2-hour limit is for jobs without history only, until
        // the usual duration has stayed unknown for long.
        if (!LimitIsKnown(key, subject, usualKnown, nowUtc)) started = null;
        return ForBusyRunner(runner.QuietSinceUtc, started, usual, quietLimit, subject, nowUtc);
    }

    /// <summary>
    /// Whether a busy runner's job may be judged by <see cref="LongLimit"/>: its usual duration is known (or it has no
    /// history), or it has stayed unknown for <see cref="UnknownUsualLimit"/> during this job.
    /// </summary>
    private static bool LimitIsKnown(string key, string subject, bool usualKnown, DateTime nowUtc)
    {
        lock (KnownJobs)
        {
            if (usualKnown)
            {
                UsualUnknownSince.Remove(key);
                return true;
            }
            if (!UsualUnknownSince.TryGetValue(key, out var unknown) || unknown.Subject != subject)
                UsualUnknownSince[key] = unknown = (subject, nowUtc);
            return nowUtc - unknown.SinceUtc >= UnknownUsualLimit;
        }
    }

    /// <summary>
    /// The job's usual duration, or the last one GitHub gave when it cannot be read now or has too few runs to go by.
    /// Known is false when GitHub could not be read and no earlier duration is known: Usual is then null without
    /// meaning that the job has no history.
    /// </summary>
    private static async Task<(bool Known, TimeSpan? Usual)> UsualDurationAsync(GitHubReads github, string repository, GitHubJobState job)
    {
        var key = $"{repository}|{job.WorkflowName}|{job.Name}";
        (bool Read, TimeSpan? Usual) read = (false, null);
        try { read = await github.UsualDuration(repository, job.WorkflowName, job.Name); }
        catch { }
        lock (KnownUsual)
        {
            if (read is { Read: true, Usual: { } value })
            {
                if (KnownUsual.Count > 500) KnownUsual.Clear();
                KnownUsual[key] = value;
                return (true, value);
            }
            if (KnownUsual.TryGetValue(key, out var last)) return (true, last);
        }
        return (read.Read, null);
    }

    /// <summary>A runner's labels as GitHub lists them, or else as its PC reports them.</summary>
    private static IReadOnlySet<string> LabelsOf(MachineInfo machine, MachineRunnerStatus runner,
        Dictionary<string, IReadOnlyList<RegisteredRunner>> registered)
    {
        if (runner.AgentId > 0 && registered.TryGetValue(runner.Scope, out var list) && list.FirstOrDefault(r => r.Id == runner.AgentId) is { } found)
            return found.Labels;
        // Other PCs do not report their architecture.
        return RunnerLabels.Available(runner.CustomLabels, runner.RegistrationOptions?.NoDefaultLabels != true,
            machine.IsLocal ? new[] { RunnerService.ArchitectureLabel } : new[] { "X64", "ARM64" });
    }

    /// <summary>
    /// Whether a queued job that no runner in the fleet can take was meant for one: it asks for self-hosted or for a
    /// label the fleet's runners have, and not for Linux or macOS. Jobs for GitHub's hosted runners are not.
    /// </summary>
    internal static bool MeantForFleet(IEnumerable<string> jobLabels, IEnumerable<IReadOnlySet<string>> fleetLabels)
    {
        var labels = jobLabels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        if (labels.Any(l => l.Equals("linux", StringComparison.OrdinalIgnoreCase) || l.Equals("macos", StringComparison.OrdinalIgnoreCase)))
            return false;
        var fleet = fleetLabels.ToList();
        return labels.Any(l => l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase) || fleet.Any(set => set.Contains(l)));
    }

    private static string FleetId(string scope, long id) => $"{scope}#{id}";

    /// <summary>
    /// Whether a runner outside the fleet (on a PC without Runner Manager) is registered with the labels a job needs, for
    /// the repository or its owner. A personal account has no organization list: GitHub answers 404, which
    /// <see cref="RegisteredRunners"/> remembers, so it is not asked again every minute.
    /// </summary>
    private static async Task<bool> HasOtherRunnerAsync(GitHubReads github, string[] jobLabels, string repository, string owner, HashSet<string> fleetIds)
    {
        foreach (var scope in new[] { $"repos/{repository}", $"orgs/{owner}" })
        {
            var list = await github.Runners(scope);
            if (list != null && list.Any(r => !fleetIds.Contains(FleetId(scope, r.Id)) && RunnerLabels.CanRun(jobLabels, r.Labels))) return true;
        }
        return false;
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
        /// <summary><see cref="AwakeTicks"/> at the reading.</summary>
        public ulong SampledAwake;
        public DateTime LastActiveUtc;
    }

    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>How long Windows has been awake, in 100 ns units: it stands still while the PC sleeps or hibernates. 0 when unknown.</summary>
    private static ulong AwakeTicks() => QueryUnbiasedInterruptTime(out var ticks) ? ticks : 0;

    /// <summary>Takes a new reading of each busy runner's job when the last one is 30 seconds old, and forgets idle runners.</summary>
    public static void Update(IEnumerable<RunnerInfo> runners) => Update(runners, () => RunnerActivity.ProcessSnapshot.Read());

    /// <param name="readProcesses">Reads the PC's processes: once per update, shared by all busy runners.</param>
    internal static void Update(IEnumerable<RunnerInfo> runners, Func<RunnerActivity.ProcessSnapshot> readProcesses)
    {
        var now = DateTime.UtcNow;
        var busy = runners.Where(r => r.IsRunning && r.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.InstallPath).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (States)
        {
            foreach (var gone in States.Keys.Where(k => !busy.Contains(k)).ToList()) States.Remove(gone);
            busy.RemoveWhere(root => States.TryGetValue(root, out var state) && now - state.SampledUtc < SampleInterval);
        }
        if (busy.Count == 0) return;
        RunnerActivity.ProcessSnapshot processes;
        try { processes = readProcesses(); }
        catch { return; }
        var awake = AwakeTicks();
        foreach (var root in busy)
        {
            RunnerActivity.JobSample? sample;
            try { sample = RunnerActivity.ReadJobSample(root, processes); }
            catch { continue; }
            if (sample == null)
            {
                lock (States) States.Remove(root);
                continue;
            }
            Record(root, sample, now, awake);
        }
    }

    /// <summary>Compares a new reading of a runner's job with the last one.</summary>
    /// <param name="awakeTicks"><see cref="AwakeTicks"/> now.</param>
    internal static void Record(string root, RunnerActivity.JobSample sample, DateTime nowUtc, ulong awakeTicks)
    {
        lock (States)
        {
            if (!States.TryGetValue(root, out var state) || state.WorkerStartedUtc != sample.WorkerStartedUtc)
            {
                States[root] = new State
                {
                    WorkerStartedUtc = sample.WorkerStartedUtc, ProcessorTime = sample.ProcessorTime,
                    OutputState = sample.OutputState, SampledUtc = nowUtc, SampledAwake = awakeTicks, LastActiveUtc = nowUtc
                };
                return;
            }
            var elapsed = nowUtc - state.SampledUtc;
            var awake = awakeTicks > state.SampledAwake && state.SampledAwake > 0
                ? TimeSpan.FromTicks((long)(awakeTicks - state.SampledAwake)) : elapsed;
            // The PC slept since the last reading. Nothing ran meanwhile, so the sleep is not quiet time, and the CPU
            // use is spread over the time the PC was awake only.
            var slept = elapsed - awake;
            if (slept > TimeSpan.FromSeconds(5))
            {
                state.LastActiveUtc = state.LastActiveUtc + slept < nowUtc ? state.LastActiveUtc + slept : nowUtc;
                elapsed = awake;
            }
            if (MadeProgress(state.ProcessorTime, sample.ProcessorTime, state.OutputState, sample.OutputState, elapsed))
                state.LastActiveUtc = nowUtc;
            state.ProcessorTime = sample.ProcessorTime;
            state.OutputState = sample.OutputState;
            state.SampledUtc = nowUtc;
            state.SampledAwake = awakeTicks;
        }
    }

    /// <summary>
    /// Progress between two readings: a new process, new output, or CPU use of at least 5% of one core. An output file
    /// that went away is no progress: it may only have become unreadable.
    /// </summary>
    internal static bool MadeProgress(IReadOnlyDictionary<string, ulong> before, IReadOnlyDictionary<string, ulong> now,
        string outputBefore, string outputNow, TimeSpan elapsed)
    {
        if (outputNow.Length > 0 && outputNow != outputBefore) return true;
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
