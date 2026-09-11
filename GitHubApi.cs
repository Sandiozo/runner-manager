using System.Text;
using System.Text.Json;

namespace RunnerWidget;

public static class GitHubApi
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime Time, RepositoryWorkState State)> WorkCache
        = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<RunnerWorkState> GetRunnerWorkAsync(
        string repositoryPath, long runnerId, IEnumerable<string> customLabels)
    {
        var state = await GetRepositoryWorkAsync(repositoryPath);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "self-hosted", "Windows", "X64"
        };
        foreach (var label in customLabels.Where(x => !string.IsNullOrWhiteSpace(x)))
            labels.Add(label.Trim());

        var current = state.Jobs.FirstOrDefault(j =>
            j.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) && j.RunnerId == runnerId);
        var queued = state.Jobs.Where(j =>
            j.Status.Equals("queued", StringComparison.OrdinalIgnoreCase) && IsEligible(j.Labels, labels)).ToList();

        return new RunnerWorkState
        {
            CurrentWorkText = current == null
                ? "Current job: none"
                : $"Working: {current.WorkflowName}  ->  {current.Name}  |  run #{current.RunNumber}",
            CurrentJobUrl = current?.HtmlUrl ?? string.Empty,
            QueueCount = queued.Count,
            QueueText = queued.Count == 0
                ? "Queue: 0 eligible jobs"
                : $"Queue: {queued.Count}  |  next: {queued[0].WorkflowName}  ->  {queued[0].Name}"
        };
    }
    private static bool IsEligible(IEnumerable<string> required, HashSet<string> available)
    {
        var labels = required.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return labels.Any(x => x.Equals("self-hosted", StringComparison.OrdinalIgnoreCase))
               && labels.All(available.Contains);
    }

    private static async Task<RepositoryWorkState> GetRepositoryWorkAsync(string repositoryPath)
    {
        lock (CacheLock)
        {
            if (WorkCache.TryGetValue(repositoryPath, out var cached)
                && DateTime.UtcNow - cached.Time < TimeSpan.FromSeconds(8))
                return cached.State;
        }

        var runs = new List<WorkflowRunState>();
        runs.AddRange(await GetRunsAsync(repositoryPath, "in_progress"));
        runs.AddRange(await GetRunsAsync(repositoryPath, "queued"));
        runs = runs.GroupBy(r => r.Id).Select(g => g.First()).ToList();

        var state = new RepositoryWorkState();
        foreach (var run in runs)
        {
            var jobs = await GetJobsAsync(repositoryPath, run);
            state.Jobs.AddRange(jobs);
        }

        lock (CacheLock)
            WorkCache[repositoryPath] = (DateTime.UtcNow, state);
        return state;
    }

    private static async Task<List<WorkflowRunState>> GetRunsAsync(string repositoryPath, string status)
    {
        var result = await GitHubCli.RunAsync(
            $"api repos/{repositoryPath}/actions/runs?status={status}&per_page=20");
        if (result.ExitCode != 0) return new List<WorkflowRunState>();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var list = new List<WorkflowRunState>();
            foreach (var run in json.RootElement.GetProperty("workflow_runs").EnumerateArray())
                list.Add(ReadRun(run));
            return list;
        }
        catch { return new List<WorkflowRunState>(); }
    }
    private static WorkflowRunState ReadRun(JsonElement run)
    {
        return new WorkflowRunState
        {
            Id = run.GetProperty("id").GetInt64(),
            WorkflowName = run.TryGetProperty("name", out var name) ? name.GetString() ?? "Workflow" : "Workflow",
            RunNumber = run.TryGetProperty("run_number", out var number) ? number.GetInt32() : 0,
            Branch = run.TryGetProperty("head_branch", out var branch) ? branch.GetString() ?? string.Empty : string.Empty,
            DisplayTitle = run.TryGetProperty("display_title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            HtmlUrl = run.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty
        };
    }

    private static async Task<List<GitHubJobState>> GetJobsAsync(string repositoryPath, WorkflowRunState run)
    {
        var result = await GitHubCli.RunAsync(
            $"api repos/{repositoryPath}/actions/runs/{run.Id}/jobs?filter=latest&per_page=100");
        if (result.ExitCode != 0) return new List<GitHubJobState>();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var list = new List<GitHubJobState>();
            foreach (var job in json.RootElement.GetProperty("jobs").EnumerateArray())
                list.Add(ReadJob(job, run));
            return list;
        }
        catch { return new List<GitHubJobState>(); }
    }
    private static GitHubJobState ReadJob(JsonElement job, WorkflowRunState run)
    {
        var labels = new List<string>();
        if (job.TryGetProperty("labels", out var labelValues) && labelValues.ValueKind == JsonValueKind.Array)
            foreach (var label in labelValues.EnumerateArray())
                if (!string.IsNullOrWhiteSpace(label.GetString())) labels.Add(label.GetString()!);

        return new GitHubJobState
        {
            Id = job.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
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

    public static async Task<List<RepositoryFile>> GetWorkflowFilesAsync(string repositoryPath)
    {
        var result = await GitHubCli.RunAsync($"api repos/{repositoryPath}/contents/.github/workflows");
        if (result.ExitCode != 0) return new List<RepositoryFile>();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var files = new List<RepositoryFile>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                var path = item.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? string.Empty : string.Empty;
                if (type != "file" || !(path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))) continue;
                var file = await GetRepositoryFileAsync(repositoryPath, path);
                if (file != null) files.Add(file);
            }
            return files;
        }
        catch { return new List<RepositoryFile>(); }
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
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(newContent));
        var safeMessage = commitMessage.Replace("\"", "'");
        var result = await GitHubCli.RunAsync(
            $"api --method PUT {endpoint} -f \"message={safeMessage}\" -f content={encoded} -f sha={file.Sha}");
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
    public string CurrentWorkText { get; init; } = "Current job: unavailable";
    public string CurrentJobUrl { get; init; } = string.Empty;
    public int QueueCount { get; init; }
    public string QueueText { get; init; } = "Queue: unavailable";
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
    public string Branch { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
}
public sealed class GitHubJobState
{
    public long Id { get; init; }
    public long RunnerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string[] Labels { get; init; } = Array.Empty<string>();
    public string WorkflowName { get; init; } = string.Empty;
    public int RunNumber { get; init; }
}

public sealed class RepositoryFile
{
    public string Path { get; init; } = string.Empty;
    public string Sha { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}
