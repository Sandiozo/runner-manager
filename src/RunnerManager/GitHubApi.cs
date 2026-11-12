using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace RunnerManager;

public static class GitHubApi
{
    private const int RecentRunCount = 6;
    private static readonly TimeSpan WorkCacheLifetime = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new();

    /// <summary>A read of a repository's jobs, and when its answer was known (until then, when it started).</summary>
    private sealed class WorkRead
    {
        public DateTime Time;
        public Task<RepositoryWorkState?> State = Task.FromResult<RepositoryWorkState?>(null);
        /// <summary>The requests of this read that GitHub did not answer, or that were not sent because of an earlier failure.</summary>
        public readonly ConcurrentBag<string> Unanswered = new();
    }

    /// <summary>One request per repository and refresh: runners of the same repository share the same task.</summary>
    private static readonly Dictionary<string, WorkRead> WorkCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why the jobs and runs of a repository ("owner/repo") could not be read, or null when the last reads worked.
    /// For the UI, which otherwise only knows that they are unavailable. See <see cref="GitHubCli.ProblemFor"/>.
    /// </summary>
    public static GitHubProblem? ProblemFor(string? repositoryPath)
        => GitHubCli.ProblemFor(GitHubNames.IsRepositoryPath(repositoryPath) ? $"repos/{repositoryPath}" : null);

    /// <returns>The runner's current job and queue, or null when GitHub could not be read.</returns>
    /// <param name="runnerName">The runner's name, so a job of another runner with the same ID is not taken (see <see cref="RunningJobOf"/>).</param>
    public static async Task<RunnerWorkState?> GetRunnerWorkAsync(
        string repositoryPath, long runnerId, IEnumerable<string> customLabels, string? runnerName = null)
    {
        var state = await GetRepositoryWorkAsync(repositoryPath);
        if (state == null) return null;
        var labels = RunnerLabels.Available(customLabels);

        var current = RunningJobOf(state.Jobs, runnerId, runnerName);
        // Longest wait first, so the job named as next is the one that waited longest.
        var queued = state.Jobs.Where(j =>
                j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase) && RunnerLabels.CanRun(j.Labels, labels))
            .OrderBy(j => j.CreatedUtc ?? DateTime.MaxValue).ToList();

        return new RunnerWorkState
        {
            CurrentWorkText = current == null
                ? "none"
                : $"{current.WorkflowName} → {current.Name}  ·  run #{current.RunNumber}",
            CurrentJobUrl = current?.HtmlUrl ?? string.Empty,
            QueueCount = queued.Count,
            QueueText = queued.Count == 0
                ? "empty"
                : $"{queued.Count} waiting  ·  next: {queued[0].WorkflowName} → {queued[0].Name}"
        };
    }

    /// <summary>
    /// The job a runner is running now, with its steps. Read is false when GitHub could not be read; Job is null when
    /// GitHub does not (yet) report a running job for this runner, or only jobs that started well before its worker
    /// (see <see cref="RunningJobOf"/>). Shares the few-second cache of the queue view.
    /// </summary>
    /// <param name="runnerName">The runner's name, and <paramref name="workerStartedUtc"/> when its Runner.Worker
    /// started: they tell the right job apart from another one with the same runner ID (see <see cref="RunningJobOf"/>).</param>
    public static async Task<(bool Read, GitHubJobState? Job)> GetCurrentJobAsync(string repositoryPath, long runnerId,
        string? runnerName = null, DateTime? workerStartedUtc = null)
    {
        var state = await GetRepositoryWorkAsync(repositoryPath);
        if (state == null) return (false, null);
        return (true, RunningJobOf(state.Jobs, runnerId, runnerName, workerStartedUtc));
    }

    /// <summary>
    /// The job that GitHub lists as running on a runner, among a repository's jobs.
    /// - Runner IDs are numbered per repository and per organization, so an organization runner that runs a job of the
    ///   repository can have the same ID as the repository's own runner. When GitHub and the caller both give the
    ///   runner's name, it must match too.
    /// - GitHub keeps listing a job as running for a while after its runner gave it up (the runner or the PC was
    ///   restarted), also when the runner already runs its next job. When the start of the runner's Runner.Worker is
    ///   known, a job that started more than <see cref="WorkerStartSlack"/> before it is not the worker's, also when
    ///   it is the only one listed (with many runs in progress, the run of the worker's job may not be read), and of
    ///   several the job that started closest to the worker is taken. Without the worker's start, the one that started
    ///   last is taken.
    /// </summary>
    internal static GitHubJobState? RunningJobOf(IEnumerable<GitHubJobState> jobs, long runnerId, string? runnerName = null,
        DateTime? workerStartedUtc = null)
    {
        if (runnerId <= 0) return null;
        var running = jobs.Where(j => j.RunnerId == runnerId && j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase)
                                      && (string.IsNullOrEmpty(runnerName) || j.RunnerName.Length == 0
                                          || j.RunnerName.Equals(runnerName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (workerStartedUtc is not { } worker) return running.MaxBy(j => j.StartedUtc ?? DateTime.MinValue);
        return running.Where(j => j.StartedUtc is not { } started || worker - started <= WorkerStartSlack)
            .MinBy(j => j.StartedUtc is { } started ? Math.Abs((started - worker).Ticks) : long.MaxValue);
    }

    /// <summary>
    /// How much earlier than the runner's Runner.Worker its job can have started on GitHub: the runner starts the worker
    /// right after GitHub gives it the job, but the worker's start is read on the PC and GitHub's on its own clock, and
    /// clocks differ a little.
    /// </summary>
    internal static readonly TimeSpan WorkerStartSlack = TimeSpan.FromMinutes(5);

    /// <summary>The jobs of a repository's queued and running workflow runs, or null when GitHub could not be read.</summary>
    public static Task<RepositoryWorkState?> GetRepositoryJobsAsync(string repositoryPath) => GetRepositoryWorkAsync(repositoryPath);

    private static readonly Dictionary<string, (DateTime Until, TimeSpan? Usual)> UsualDurations = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The jobs of finished runs, which no longer change.</summary>
    private static readonly Dictionary<long, List<GitHubJobState>> FinishedRunJobs = new();

    /// <summary>
    /// How long a job usually takes: the median of its successful runs among the workflow's latest successful runs
    /// (null with fewer than three, or when GitHub could not be read). Remembered for 10 minutes.
    /// </summary>
    public static async Task<TimeSpan?> GetUsualJobDurationAsync(string repositoryPath, string workflowName, string jobName)
        => (await ReadUsualJobDurationAsync(repositoryPath, workflowName, jobName)).Usual;

    /// <summary>
    /// How long a job usually takes, as <see cref="GetUsualJobDurationAsync"/>, and whether GitHub was read: Usual is
    /// null with Read true when the job has fewer than three successful runs to go by, and with Read false when a
    /// request failed and the runs that were read do not give three. Only a read that worked is remembered.
    /// </summary>
    public static async Task<(bool Read, TimeSpan? Usual)> ReadUsualJobDurationAsync(string repositoryPath, string workflowName, string jobName)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) return (false, null);
        var key = $"{repositoryPath}|{workflowName}|{jobName}";
        lock (CacheLock)
            if (UsualDurations.TryGetValue(key, out var known) && DateTime.UtcNow < known.Until) return (true, known.Usual);

        var endpoint = $"repos/{repositoryPath}/actions/runs?status=success&per_page=30";
        var sentIn = GitHubFailures.Generation;
        var body = await GetPolledAsync(endpoint);
        if (body == null) return (false, null);
        List<WorkflowRunState> runs;
        try
        {
            using var json = JsonDocument.Parse(body);
            runs = json.RootElement.GetProperty("workflow_runs").EnumerateArray().Select(ReadRun)
                .Where(r => r.WorkflowName.Equals(workflowName, StringComparison.Ordinal)).Take(6).ToList();
        }
        catch
        {
            Unreadable(endpoint, sentIn);
            return (false, null);
        }

        var durations = new List<TimeSpan>();
        var failed = false;
        foreach (var run in runs)
        {
            List<GitHubJobState>? jobs;
            lock (CacheLock) jobs = FinishedRunJobs.GetValueOrDefault(run.Id);
            if (jobs == null)
            {
                jobs = await GetJobsAsync(repositoryPath, run);
                if (jobs == null)
                {
                    failed = true;
                    continue;
                }
                lock (CacheLock)
                {
                    if (FinishedRunJobs.Count > 500) FinishedRunJobs.Clear();
                    FinishedRunJobs[run.Id] = jobs;
                }
            }
            durations.AddRange(jobs.Where(j => j.Name.Equals(jobName, StringComparison.Ordinal) && j.Conclusion == "success"
                                               && j.StartedUtc != null && j.CompletedUtc != null)
                .Select(j => j.CompletedUtc!.Value - j.StartedUtc!.Value));
        }
        var usual = JobHealth.Usual(durations);
        // Too few durations because a run's jobs could not be read is not "no history". Not remembered, so the next
        // check reads again (each failed request waits for its own pause).
        if (usual == null && failed) return (false, null);
        lock (CacheLock) UsualDurations[key] = (DateTime.UtcNow + TimeSpan.FromMinutes(10), usual);
        return (true, usual);
    }

    private static Task<RepositoryWorkState?> GetRepositoryWorkAsync(string repositoryPath)
    {
        // Other PCs report the repository of their runners. A name GitHub would not allow is answered like a failed
        // read, before anything is cached or started.
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) return Task.FromResult<RepositoryWorkState?>(null);
        lock (CacheLock)
        {
            var now = GitHubFailures.UtcNow();
            if (WorkCache.TryGetValue(repositoryPath, out var cached) && IsUsable(cached, now)) return cached.State;
            var read = new WorkRead { Time = now };
            read.State = Task.Run(async () =>
            {
                try { return await ReadRepositoryWorkAsync(repositoryPath, read.Unanswered); }
                // The lifetime counts from when the answer was known: gh can take long to give up.
                finally { lock (CacheLock) read.Time = GitHubFailures.UtcNow(); }
            });
            WorkCache[repositoryPath] = read;
            return read.State;
        }
    }

    /// <summary>
    /// A read is shared while it runs and for a few seconds after it worked. One that failed (null) is kept exactly
    /// as long as one of its requests is not sent again anyway (see <see cref="GitHubFailures"/>): the job view asks
    /// every 3 seconds for every busy runner, and reading again would start all the other requests of the repository
    /// only to fail at the same one. Once that request is sent again, or the failures are forgotten, the next tick
    /// reads again. One that threw is not kept.
    /// </summary>
    private static bool IsUsable(WorkRead read, DateTime now)
    {
        if (!read.State.IsCompleted) return true;
        if (!read.State.IsCompletedSuccessfully) return false;
        if (read.State.Result != null) return now - read.Time < WorkCacheLifetime;
        return read.Unanswered.Any(endpoint => GitHubFailures.Blocking(endpoint) != null);
    }

    /// <returns>All jobs of the repository's active runs, or null when any request failed.</returns>
    /// <param name="unanswered">Gets the endpoints that were not answered.</param>
    private static async Task<RepositoryWorkState?> ReadRepositoryWorkAsync(string repositoryPath, ConcurrentBag<string> unanswered)
    {
        var lists = await Task.WhenAll(
            GetRunsAsync(repositoryPath, "in_progress", unanswered), GetRunsAsync(repositoryPath, "queued", unanswered));
        if (lists.Any(list => list == null)) return null;
        var runs = lists.SelectMany(list => list!).GroupBy(r => r.Id).Select(g => g.First()).ToList();

        var first = await Task.WhenAll(runs.Select(run => GetJobPageAsync(repositoryPath, run, 1, unanswered)));
        if (first.Any(page => page == null)) return null;

        // A run with more than a page of jobs gets its further pages from the requests the runs did not use,
        // the oldest run first (run IDs grow).
        var further = new List<(WorkflowRunState Run, int Page)>();
        foreach (var (run, page) in runs.Zip(first, (run, page) => (run, page!.Value)).OrderBy(r => r.run.Id))
        {
            var pages = page.Jobs.Count < JobPageSize ? 1 : Math.Min((page.Total + JobPageSize - 1) / JobPageSize, MaxJobPages);
            for (var number = 2; number <= pages && runs.Count + further.Count < MaxJobRequests; number++)
                further.Add((run, number));
        }
        var rest = await Task.WhenAll(further.Select(next => GetJobPageAsync(repositoryPath, next.Run, next.Page, unanswered)));
        if (rest.Any(page => page == null)) return null;

        var state = new RepositoryWorkState();
        foreach (var page in first.Concat(rest)) state.Jobs.AddRange(page!.Value.Jobs);
        return state;
    }

    private static readonly Dictionary<string, (string ETag, string Body)> PolledBodies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// GET with an ETag for the queue view's requests: while a run and its jobs don't change, GitHub answers 304, which
    /// does not count against the rate limit, and the last answer is reused. Null when the request failed.
    /// </summary>
    /// <param name="unanswered">Gets the endpoint when the request failed or was not sent.</param>
    private static async Task<string?> GetPolledAsync(string endpoint, ConcurrentBag<string>? unanswered = null)
    {
        (string ETag, string Body)? cached;
        lock (CacheLock) cached = PolledBodies.TryGetValue(endpoint, out var entry) ? entry : null;
        var response = await GitHubCli.GetConditionalAsync(endpoint, cached?.ETag);
        if (response.NotModified && cached != null) return cached.Value.Body;
        if (!response.Ok)
        {
            unanswered?.Add(endpoint);
            return null;
        }
        lock (CacheLock)
        {
            // Job lists are per run, so old runs' entries are dropped now and then.
            if (PolledBodies.Count > MaxPolledBodies) PolledBodies.Clear();
            PolledBodies[endpoint] = (response.ETag, response.Body);
        }
        return response.Body;
    }

    /// <summary>
    /// GitHub answered a poll with something that could not be read. The answer is not kept (its ETag would bring the
    /// same answer back as 304), and the request is paused like a failed one, so the job view does not start the
    /// whole read of the repository again on every tick only to fail at the same answer.
    /// </summary>
    /// <param name="sentIn"><see cref="GitHubFailures.Generation"/>, taken before the request was sent.</param>
    /// <param name="unanswered">Gets the endpoint, so the failed read is kept while the request is paused.</param>
    private static void Unreadable(string endpoint, int sentIn, ConcurrentBag<string>? unanswered = null)
    {
        lock (CacheLock) PolledBodies.Remove(endpoint);
        GitHubFailures.Unreadable(endpoint, sentIn);
        unanswered?.Add(endpoint);
    }

    /// <summary>Room for the run and job lists of several busy repositories, so their ETags are not dropped at every refresh.</summary>
    private const int MaxPolledBodies = 1000;
    /// <summary>
    /// How many queued and how many running runs of a repository are read. Every run costs one more request for its
    /// jobs at every refresh, so this is not "all of them". With more runs, the oldest are kept.
    /// </summary>
    internal const int RunPageSize = 20;
    /// <summary>GitHub lists at most 1000 runs for a status.</summary>
    private const int MaxRunPages = 1000 / RunPageSize;
    private const int JobPageSize = 100;
    /// <summary>
    /// One matrix has at most 256 jobs, so three pages hold the jobs of nearly every run. A run with more (several
    /// large matrices) is read up to here.
    /// </summary>
    private const int MaxJobPages = 3;
    /// <summary>
    /// How many requests for jobs one read of a repository makes: one for every run that is read, and what is left
    /// for the further pages of runs with more than <see cref="JobPageSize"/> jobs. A run whose further pages do not
    /// fit is read up to its first page.
    /// </summary>
    internal const int MaxJobRequests = 2 * RunPageSize;

    /// <summary>
    /// The runs of a repository with this status, or null when a request failed. GitHub lists the newest first; with
    /// more than <see cref="RunPageSize"/> the oldest are read from the last pages, because those matter most: the
    /// queued runs that waited longest, and the running ones that may be stuck.
    /// </summary>
    private static async Task<List<WorkflowRunState>?> GetRunsAsync(string repositoryPath, string status, ConcurrentBag<string> unanswered)
    {
        if (await GetRunPageAsync(repositoryPath, status, 1, unanswered) is not { } first) return null;
        var lastPage = Math.Clamp((first.Total + RunPageSize - 1) / RunPageSize, 1, MaxRunPages);
        if (lastPage == 1) return first.Runs;

        if (await GetRunPageAsync(repositoryPath, status, lastPage, unanswered) is not { } last) return null;
        if (last.Runs.Count >= RunPageSize) return last.Runs;
        // The last page is not full: the other oldest runs are at the end of the page before it.
        if ((lastPage == 2 ? first : await GetRunPageAsync(repositoryPath, status, lastPage - 1, unanswered)) is not { } before) return null;
        return before.Runs.TakeLast(RunPageSize - last.Runs.Count).Concat(last.Runs).ToList();
    }

    private static async Task<(int Total, List<WorkflowRunState> Runs)?> GetRunPageAsync(
        string repositoryPath, string status, int page, ConcurrentBag<string> unanswered)
    {
        var endpoint = $"repos/{repositoryPath}/actions/runs?status={status}&per_page={RunPageSize}{PageParameter(page)}";
        var sentIn = GitHubFailures.Generation;
        var body = await GetPolledAsync(endpoint, unanswered);
        if (body == null) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var list = new List<WorkflowRunState>();
            foreach (var run in json.RootElement.GetProperty("workflow_runs").EnumerateArray())
                list.Add(ReadRun(run));
            return (TotalCount(json.RootElement, list.Count), list);
        }
        catch
        {
            Unreadable(endpoint, sentIn, unanswered);
            return null;
        }
    }

    /// <summary>Empty for the first page, which needs no page number.</summary>
    private static string PageParameter(int page) => page <= 1 ? string.Empty : $"&page={GitHubNames.Id(page)}";

    private static int TotalCount(JsonElement root, int fallback)
        => root.TryGetProperty("total_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var total)
            ? total : fallback;
    private static WorkflowRunState ReadRun(JsonElement run)
    {
        return new WorkflowRunState
        {
            Id = run.GetProperty("id").GetInt64(),
            WorkflowName = run.TryGetProperty("name", out var name) ? name.GetString() ?? "Workflow" : "Workflow",
            RunNumber = run.TryGetProperty("run_number", out var number) ? number.GetInt32() : 0,
            HtmlUrl = run.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty
        };
    }

    /// <summary>The jobs of a finished run, read page by page, or null when a request failed.</summary>
    private static async Task<List<GitHubJobState>?> GetJobsAsync(string repositoryPath, WorkflowRunState run)
    {
        var list = new List<GitHubJobState>();
        for (var number = 1; number <= MaxJobPages; number++)
        {
            if (await GetJobPageAsync(repositoryPath, run, number) is not { } page) return null;
            list.AddRange(page.Jobs);
            if (page.Jobs.Count < JobPageSize || list.Count >= page.Total) break;
        }
        return list;
    }

    /// <summary>One page of a run's jobs and how many jobs the run has, or null when the request failed.</summary>
    private static async Task<(int Total, List<GitHubJobState> Jobs)?> GetJobPageAsync(
        string repositoryPath, WorkflowRunState run, int page, ConcurrentBag<string>? unanswered = null)
    {
        var endpoint = $"repos/{repositoryPath}/actions/runs/{GitHubNames.Id(run.Id)}/jobs?filter=latest&per_page={JobPageSize}{PageParameter(page)}";
        var sentIn = GitHubFailures.Generation;
        var body = await GetPolledAsync(endpoint, unanswered);
        if (body == null) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var list = new List<GitHubJobState>();
            foreach (var job in json.RootElement.GetProperty("jobs").EnumerateArray())
                list.Add(ReadJob(job, run));
            // Without a count, the jobs read so far are taken as all of them.
            return (TotalCount(json.RootElement, (page - 1) * JobPageSize + list.Count), list);
        }
        catch
        {
            Unreadable(endpoint, sentIn, unanswered);
            return null;
        }
    }
    private static DateTime? ReadTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var time)
            ? time.ToUniversalTime() : null;

    private static GitHubJobState ReadJob(JsonElement job, WorkflowRunState run)
    {
        var labels = new List<string>();
        if (job.TryGetProperty("labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Array)
            foreach (var label in labelValues.EnumerateArray())
                if (!string.IsNullOrWhiteSpace(label.GetString())) labels.Add(label.GetString()!);

        var steps = new List<GitHubJobStep>();
        if (job.TryGetProperty("steps", out var stepValues) && stepValues.ValueKind == JsonValueKind.Array)
            foreach (var step in stepValues.EnumerateArray())
                steps.Add(new GitHubJobStep
                {
                    Number = step.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : steps.Count + 1,
                    Name = step.TryGetProperty("name", out var stepName) ? stepName.GetString() ?? "Step" : "Step",
                    Status = step.TryGetProperty("status", out var stepStatus) ? stepStatus.GetString() ?? string.Empty : string.Empty,
                    Conclusion = step.TryGetProperty("conclusion", out var conclusion) ? conclusion.GetString() ?? string.Empty : string.Empty,
                    StartedUtc = ReadTime(step, "started_at"),
                    CompletedUtc = ReadTime(step, "completed_at")
                });

        return new GitHubJobState
        {
            Id = job.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
            CreatedUtc = ReadTime(job, "created_at"),
            StartedUtc = ReadTime(job, "started_at"),
            CompletedUtc = ReadTime(job, "completed_at"),
            Conclusion = job.TryGetProperty("conclusion", out var jobConclusion) && jobConclusion.ValueKind == JsonValueKind.String
                ? jobConclusion.GetString() ?? string.Empty : string.Empty,
            RunnerName = job.TryGetProperty("runner_name", out var runnerName) && runnerName.ValueKind == JsonValueKind.String
                ? runnerName.GetString() ?? string.Empty : string.Empty,
            Steps = steps,
            RunnerId = job.TryGetProperty("runner_id", out var runnerId) && runnerId.ValueKind == JsonValueKind.Number
                ? runnerId.GetInt64() : 0,
            Name = job.TryGetProperty("name", out var name) ? name.GetString() ?? "Job" : "Job",
            Status = job.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
            HtmlUrl = job.TryGetProperty("html_url", out var url) ? url.GetString() ?? run.HtmlUrl : run.HtmlUrl,
            Labels = labels.ToArray(),
            WorkflowName = run.WorkflowName,
            RunNumber = run.RunNumber
        };
    }

    private static readonly Dictionary<string, (string ETag, List<WorkflowRunSummary> Runs)> RecentRunsCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The latest workflow runs of a repository. Polled with an ETag, so unchanged polls are free
    /// against the API rate limit.
    /// </summary>
    public static async Task<List<WorkflowRunSummary>?> GetRecentRunsAsync(string repositoryPath)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) return null;
        (string ETag, List<WorkflowRunSummary> Runs) cached;
        lock (CacheLock)
            cached = RecentRunsCache.TryGetValue(repositoryPath, out var entry) ? entry : (string.Empty, new List<WorkflowRunSummary>());

        var endpoint = $"repos/{repositoryPath}/actions/runs?per_page={RecentRunCount}&exclude_pull_requests=true";
        var sentIn = GitHubFailures.Generation;
        var response = await GitHubCli.GetConditionalAsync(endpoint, cached.ETag);
        if (response.NotModified) return cached.Runs;
        if (!response.Ok) return null;
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            var runs = new List<WorkflowRunSummary>();
            foreach (var run in json.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                runs.Add(new WorkflowRunSummary
                {
                    Id = run.GetProperty("id").GetInt64(),
                    RepositoryPath = repositoryPath,
                    WorkflowName = Str(run, "name", "Workflow"),
                    Title = Str(run, "display_title", string.Empty),
                    Branch = Str(run, "head_branch", string.Empty),
                    Status = Str(run, "status", "unknown"),
                    Conclusion = Str(run, "conclusion", string.Empty),
                    RunNumber = run.TryGetProperty("run_number", out var number) ? number.GetInt32() : 0,
                    HtmlUrl = Str(run, "html_url", string.Empty),
                    CreatedUtc = run.TryGetProperty("created_at", out var created) && created.TryGetDateTime(out var c) ? c.ToUniversalTime() : DateTime.MinValue,
                    UpdatedUtc = run.TryGetProperty("updated_at", out var updated) && updated.TryGetDateTime(out var u) ? u.ToUniversalTime() : DateTime.MinValue
                });
            }
            lock (CacheLock) RecentRunsCache[repositoryPath] = (response.ETag, runs);
            return runs;
        }
        catch
        {
            GitHubFailures.Unreadable(endpoint, sentIn);
            return null;
        }
    }

    public static async Task RerunFailedJobsAsync(string repositoryPath, long runId)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) throw new InvalidOperationException(GitHubCli.InvalidNameMessage);
        if (runId <= 0) throw new InvalidOperationException("The workflow run is not known, so GitHub was not asked to run it again.");
        var result = await GitHubCli.ApiAsync($"repos/{repositoryPath}/actions/runs/{GitHubNames.Id(runId)}/rerun-failed-jobs", "--method", "POST");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "GitHub did not accept the re-run request." : result.Error.Trim());
    }

    private static string Str(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    /// <summary>
    /// The workflow YAML files of a repository. A missing .github/workflows folder (404) gives an empty list; any other
    /// failure throws. Files that cannot be read are listed in Skipped instead of failing the whole check.
    /// </summary>
    public static async Task<WorkflowFileList> GetWorkflowFilesAsync(string repositoryPath)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) throw new InvalidOperationException(GitHubCli.InvalidNameMessage);
        var result = await GitHubCli.ApiAsync($"repos/{repositoryPath}/contents/.github/workflows");
        if (result.ExitCode != 0)
        {
            if (GitHubCli.IsNotFound(result.Error)) return new WorkflowFileList();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? "Could not read the workflow files from GitHub. Try again."
                : $"Could not read the workflow files from GitHub: {result.Error.Trim()}");
        }

        var paths = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                var path = item.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? string.Empty : string.Empty;
                if (type == "file" && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))) paths.Add(path);
            }
        }
        catch
        {
            throw new InvalidOperationException("GitHub returned an unexpected answer for the workflow folder. Try again.");
        }

        var list = new WorkflowFileList();
        foreach (var path in paths)
        {
            RepositoryFile? file;
            try { file = await GetRepositoryFileAsync(repositoryPath, path); }
            catch { file = null; }
            if (file != null) list.Files.Add(file);
            else list.Skipped.Add(path);
        }
        return list;
    }

    private static async Task<RepositoryFile?> GetRepositoryFileAsync(string repositoryPath, string path)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) return null;
        // A path with "." or ".." segments is refused by the endpoint check, and the file is then listed as skipped.
        var result = await GitHubCli.ApiAsync($"repos/{repositoryPath}/contents/{EscapePath(path)}");
        if (result.ExitCode != 0) return null;
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var sha = root.TryGetProperty("sha", out var shaValue) ? shaValue.GetString() ?? string.Empty : string.Empty;
        var encoded = root.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        encoded = encoded.Replace("\n", string.Empty).Replace("\r", string.Empty);
        var content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return new RepositoryFile { Path = path, Sha = sha, Content = content };
    }

    public static async Task UpdateRepositoryFileAsync(
        string repositoryPath, RepositoryFile file, string newContent, string commitMessage)
    {
        if (!GitHubNames.IsRepositoryPath(repositoryPath)) throw new InvalidOperationException(GitHubCli.InvalidNameMessage);
        var endpoint = $"repos/{repositoryPath}/contents/{EscapePath(file.Path)}";
        // Sent on stdin: a base64 workflow file can easily exceed Windows' 32K command-line limit.
        var body = JsonSerializer.Serialize(new
        {
            message = commitMessage,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(newContent)),
            sha = file.Sha
        });
        var result = await GitHubCli.ApiAsync(endpoint, new[] { "--method", "PUT", "--input", "-" }, standardInput: body);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? $"Could not update {file.Path}."
                : result.Error.Trim());
    }

    private static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
public sealed class RunnerWorkState
{
    public string CurrentWorkText { get; init; } = "unavailable";
    public string CurrentJobUrl { get; init; } = string.Empty;
    public int QueueCount { get; init; }
    public string QueueText { get; init; } = "unavailable";
}

public sealed class RepositoryWorkState
{
    public List<GitHubJobState> Jobs { get; } = new();
}

public sealed class WorkflowRunState
{
    public long Id { get; init; }
    public string WorkflowName { get; init; } = string.Empty;
    public int RunNumber { get; init; }
    public string HtmlUrl { get; init; } = string.Empty;
}
public sealed class GitHubJobStep
{
    public int Number { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>"queued", "in_progress" or "completed".</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>"success", "failure", "skipped" or "cancelled" once completed.</summary>
    public string Conclusion { get; init; } = string.Empty;
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
}

public sealed class GitHubJobState
{
    public long Id { get; init; }
    /// <summary>When the job was queued: GitHub creates a job once the jobs it needs have finished.</summary>
    public DateTime? CreatedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string Conclusion { get; init; } = string.Empty;
    public string RunnerName { get; init; } = string.Empty;
    public IReadOnlyList<GitHubJobStep> Steps { get; init; } = Array.Empty<GitHubJobStep>();
    public long RunnerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string[] Labels { get; init; } = Array.Empty<string>();
    public string WorkflowName { get; init; } = string.Empty;
    public int RunNumber { get; init; }
}

public sealed class WorkflowRunSummary
{
    public long Id { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
    public string WorkflowName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Conclusion { get; init; } = string.Empty;
    public int RunNumber { get; init; }
    public string HtmlUrl { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }

    public bool IsCompleted => Status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailure => IsCompleted && Conclusion is "failure" or "timed_out" or "startup_failure";
    public bool CanRerun => IsFailure || (IsCompleted && Conclusion == "cancelled");
    public string Glyph => !IsCompleted ? ""
        : Conclusion == "success" ? ""
        : IsFailure ? ""
        : "";
    public string StateText => !IsCompleted ? (Status == "queued" ? "Queued" : "Running")
        : Conclusion switch
        {
            "success" => "Succeeded",
            "failure" => "Failed",
            "cancelled" => "Cancelled",
            "timed_out" => "Timed out",
            "skipped" => "Skipped",
            "startup_failure" => "Startup failure",
            _ => string.IsNullOrEmpty(Conclusion) ? "Completed" : Conclusion
        };
    public string TitleText => $"{WorkflowName}  #{RunNumber}";
    public string DetailText
    {
        get
        {
            var age = DateTime.UtcNow - (IsCompleted ? UpdatedUtc : CreatedUtc);
            var when = age.TotalMinutes < 1 ? "just now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";
            var branch = string.IsNullOrWhiteSpace(Branch) ? string.Empty : $"{Branch}  ·  ";
            return $"{StateText}  ·  {branch}{when}";
        }
    }
    public System.Windows.Visibility RerunVisibility => CanRerun ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    /// <summary>The row is a link (a button) when GitHub gave the run's address, and plain text otherwise.</summary>
    public System.Windows.Visibility LinkVisibility => string.IsNullOrWhiteSpace(HtmlUrl) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility TextVisibility => string.IsNullOrWhiteSpace(HtmlUrl) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

public sealed class RepositoryFile
{
    public string Path { get; init; } = string.Empty;
    public string Sha { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

/// <summary>The workflow files of a repository, and the paths of the files that could not be read.</summary>
public sealed class WorkflowFileList
{
    public List<RepositoryFile> Files { get; } = new();
    public List<string> Skipped { get; } = new();
}
