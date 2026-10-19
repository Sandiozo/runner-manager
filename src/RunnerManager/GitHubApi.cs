using System.Text;
using System.Text.Json;

namespace RunnerManager;

public static class GitHubApi
{
    private const int RecentRunCount = 6;
    private static readonly TimeSpan WorkCacheLifetime = TimeSpan.FromSeconds(8);
    private static readonly object CacheLock = new();
    /// <summary>
    /// One request per repository and refresh: runners of the same repository share the same task.
    /// A task that failed (null) is not reused.
    /// </summary>
    private static readonly Dictionary<string, (DateTime Time, Task<RepositoryWorkState?> State)> WorkCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>The runner's current job and queue, or null when GitHub could not be read.</returns>
    public static async Task<RunnerWorkState?> GetRunnerWorkAsync(
        string repositoryPath, long runnerId, IEnumerable<string> customLabels)
    {
        var state = await GetRepositoryWorkAsync(repositoryPath);
        if (state == null) return null;
        var labels = RunnerLabels.Available(customLabels);

        var current = state.Jobs.FirstOrDefault(j =>
            j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) && j.RunnerId == runnerId);
        var queued = state.Jobs.Where(j =>
            j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase) && RunnerLabels.CanRun(j.Labels, labels)).ToList();

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

    private static Task<RepositoryWorkState?> GetRepositoryWorkAsync(string repositoryPath)
    {
        lock (CacheLock)
        {
            if (WorkCache.TryGetValue(repositoryPath, out var cached)
                && DateTime.UtcNow - cached.Time < WorkCacheLifetime
                && !HasFailed(cached.State))
                return cached.State;
            var task = Task.Run(() => ReadRepositoryWorkAsync(repositoryPath));
            WorkCache[repositoryPath] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static bool HasFailed(Task<RepositoryWorkState?> task)
        => task.IsFaulted || task.IsCanceled || (task.IsCompletedSuccessfully && task.Result == null);

    /// <returns>All jobs of the repository's active runs, or null when any request failed.</returns>
    private static async Task<RepositoryWorkState?> ReadRepositoryWorkAsync(string repositoryPath)
    {
        var lists = await Task.WhenAll(GetRunsAsync(repositoryPath, "in_progress"), GetRunsAsync(repositoryPath, "queued"));
        if (lists.Any(list => list == null)) return null;
        var runs = lists.SelectMany(list => list!).GroupBy(r => r.Id).Select(g => g.First()).ToList();

        var jobs = await Task.WhenAll(runs.Select(run => GetJobsAsync(repositoryPath, run)));
        if (jobs.Any(list => list == null)) return null;
        var state = new RepositoryWorkState();
        foreach (var list in jobs) state.Jobs.AddRange(list!);
        return state;
    }

    private static async Task<List<WorkflowRunState>?> GetRunsAsync(string repositoryPath, string status)
    {
        var result = await GitHubCli.RunPollAsync(
            $"api repos/{repositoryPath}/actions/runs?status={status}&per_page=20");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var list = new List<WorkflowRunState>();
            foreach (var run in json.RootElement.GetProperty("workflow_runs").EnumerateArray())
                list.Add(ReadRun(run));
            return list;
        }
        catch { return null; }
    }
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

    private static async Task<List<GitHubJobState>?> GetJobsAsync(string repositoryPath, WorkflowRunState run)
    {
        var result = await GitHubCli.RunPollAsync(
            $"api repos/{repositoryPath}/actions/runs/{run.Id}/jobs?filter=latest&per_page=100");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var list = new List<GitHubJobState>();
            foreach (var job in json.RootElement.GetProperty("jobs").EnumerateArray())
                list.Add(ReadJob(job, run));
            return list;
        }
        catch { return null; }
    }
    private static GitHubJobState ReadJob(JsonElement job, WorkflowRunState run)
    {
        var labels = new List<string>();
        if (job.TryGetProperty("labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Array)
            foreach (var label in labelValues.EnumerateArray())
                if (!string.IsNullOrWhiteSpace(label.GetString())) labels.Add(label.GetString()!);

        return new GitHubJobState
        {
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
        (string ETag, List<WorkflowRunSummary> Runs) cached;
        lock (CacheLock)
            cached = RecentRunsCache.TryGetValue(repositoryPath, out var entry) ? entry : (string.Empty, new List<WorkflowRunSummary>());

        var response = await GitHubCli.GetConditionalAsync(
            $"repos/{repositoryPath}/actions/runs?per_page={RecentRunCount}&exclude_pull_requests=true", cached.ETag);
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
        catch { return null; }
    }

    public static async Task RerunFailedJobsAsync(string repositoryPath, long runId)
    {
        var result = await GitHubCli.RunAsync($"api --method POST repos/{repositoryPath}/actions/runs/{runId}/rerun-failed-jobs");
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
        var result = await GitHubCli.RunAsync($"api repos/{repositoryPath}/contents/.github/workflows");
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
        var endpoint = $"repos/{repositoryPath}/contents/{EscapePath(path)}";
        var result = await GitHubCli.RunAsync($"api {endpoint}");
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
        var endpoint = $"repos/{repositoryPath}/contents/{EscapePath(file.Path)}";
        // Sent on stdin: a base64 workflow file can easily exceed Windows' 32K command-line limit.
        var body = JsonSerializer.Serialize(new
        {
            message = commitMessage,
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(newContent)),
            sha = file.Sha
        });
        var result = await GitHubCli.RunAsync($"api --method PUT {endpoint} --input -", body);
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
public sealed class GitHubJobState
{
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
    public System.Windows.Media.Brush Brush => !IsCompleted ? Palette.Warning
        : Conclusion == "success" ? Palette.Success
        : IsFailure ? Palette.Danger
        : Palette.Muted;
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
