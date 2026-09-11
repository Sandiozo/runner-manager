using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Windows;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Media;

namespace RunnerWidget;

public static class RunnerManager
{
    public const string DefaultRoot = @"C:\GitHub-Runners";

    public static async Task<List<RunnerInfo>> ReadSnapshotAsync(bool queryGitHub)
    {
        var processes = ReadProcesses();
        var services = ReadRunnerServices();
        var roots = new HashSet<string>(services.Select(s => s.Root), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(DefaultRoot))
        {
            foreach (var dir in Directory.GetDirectories(DefaultRoot))
                if (File.Exists(Path.Combine(dir, ".runner"))) roots.Add(dir);
        }

        var runners = new List<RunnerInfo>();
        foreach (var root in roots.OrderBy(x => x))
            runners.Add(await BuildRunnerInfoAsync(root, services, processes, queryGitHub));
        return runners;
    }

    private static async Task<RunnerInfo> BuildRunnerInfoAsync(
        string root, List<RunnerServiceRecord> services, List<ProcessInfo> processes, bool queryGitHub)
    {
        var config = ReadRunnerConfig(root);
        var service = services.FirstOrDefault(s => s.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        var serviceInstalled = service != null;
        var running = service?.State.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        var busy = running && service != null && HasDescendantWorker(service.ProcessId, processes);
        var repoPath = RepositoryPath(config.GitHubUrl);
        RemoteRunnerState? remote = null;
        RunnerWorkState? work = null;
        if (queryGitHub && !string.IsNullOrWhiteSpace(repoPath) && config.AgentId > 0)
        {
            remote = await GitHubCli.GetRunnerStateAsync(repoPath, config.AgentId);
            work = await GitHubApi.GetRunnerWorkAsync(repoPath, config.AgentId, remote?.Labels ?? Array.Empty<string>());
        }

        var label = !string.IsNullOrWhiteSpace(config.GitHubUrl)
            ? $"{RepositoryName(config.GitHubUrl)}  |  {config.AgentName}"
            : service?.DisplayName ?? Path.GetFileName(root);
        return new RunnerInfo
        {
            Name = service?.Name ?? string.Empty,
            DisplayName = label,
            StatusText = serviceInstalled ? service!.State : "Configured  |  No service",
            ActivityText = serviceInstalled ? (running ? (busy ? "Busy" : "Idle") : "Offline") : "Offline",
            LastActivityText = GetLatestRunnerActivity(root),
            InstallPath = root,
            GitHubUrl = config.GitHubUrl,
            RepositoryPath = repoPath,
            AgentName = config.AgentName,
            AgentId = config.AgentId,
            WorkFolder = config.WorkFolder,
            ServiceInstalled = serviceInstalled,
            IsRunning = running,
            StatusBrush = running ? Brushes.MediumSeaGreen : serviceInstalled ? Brushes.IndianRed : Brushes.Goldenrod,
            GitHubText = remote == null
                ? (queryGitHub ? "GitHub: unavailable" : "GitHub: not connected")
                : $"GitHub: {remote.Status}{(remote.Busy ? "  |  Busy" : "  |  Idle")}",
            CurrentWorkText = work?.CurrentWorkText ?? (queryGitHub ? "Current job: unavailable" : "Current job: connect GitHub"),
            QueueText = work?.QueueText ?? (queryGitHub ? "Queue: unavailable" : "Queue: connect GitHub"),
            CurrentJobUrl = work?.CurrentJobUrl ?? string.Empty,
            QueueCount = work?.QueueCount ?? 0,
            CustomLabels = remote?.Labels ?? Array.Empty<string>()
        };
    }

    private static RunnerConfig ReadRunnerConfig(string root)
    {
        var file = Path.Combine(root, ".runner");
        if (!File.Exists(file)) return new RunnerConfig();
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var e = json.RootElement;
            return new RunnerConfig
            {
                AgentId = e.TryGetProperty("agentId", out var id) ? id.GetInt64() : 0,
                AgentName = e.TryGetProperty("agentName", out var name) ? name.GetString() ?? "Runner" : "Runner",
                GitHubUrl = e.TryGetProperty("gitHubUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                WorkFolder = e.TryGetProperty("workFolder", out var work) ? work.GetString() ?? "_work" : "_work"
            };
        }
        catch { return new RunnerConfig(); }
    }

    private static List<RunnerServiceRecord> ReadRunnerServices()
    {
        var result = new List<RunnerServiceRecord>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, State, PathName, ProcessId FROM Win32_Service WHERE Name LIKE 'actions.runner.%'");
        foreach (ManagementObject service in searcher.Get())
        {
            var pathName = service["PathName"]?.ToString() ?? string.Empty;
            result.Add(new RunnerServiceRecord
            {
                Name = service["Name"]?.ToString() ?? string.Empty,
                DisplayName = service["DisplayName"]?.ToString() ?? string.Empty,
                State = service["State"]?.ToString() ?? "Unknown",
                Root = ResolveRunnerRoot(pathName),
                ProcessId = ConvertToUInt(service["ProcessId"])
            });
        }
        return result;
    }

    private static List<ProcessInfo> ReadProcesses()
    {
        var result = new List<ProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process");
        foreach (ManagementObject process in searcher.Get())
        {
            result.Add(new ProcessInfo
            {
                ProcessId = ConvertToUInt(process["ProcessId"]),
                ParentProcessId = ConvertToUInt(process["ParentProcessId"]),
                Name = process["Name"]?.ToString() ?? string.Empty,
                CommandLine = process["CommandLine"]?.ToString() ?? string.Empty,
                CreationDate = process["CreationDate"]?.ToString() ?? string.Empty
            });
        }
        return result;
    }

    private static bool HasDescendantWorker(uint rootPid, List<ProcessInfo> processes)
    {
        if (rootPid == 0) return false;
        var queue = new Queue<uint>();
        var seen = new HashSet<uint>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!seen.Add(parent)) continue;
            foreach (var child in processes.Where(p => p.ParentProcessId == parent))
            {
                if (child.Name.Equals("Runner.Worker.exe", StringComparison.OrdinalIgnoreCase)) return true;
                queue.Enqueue(child.ProcessId);
            }
        }
        return false;
    }

    public static async Task ControlServiceAsync(string serviceName, string action)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new InvalidOperationException("This runner has no Windows service.");
        await Task.Run(() =>
        {
            using var service = new ServiceController(serviceName);
            service.Refresh();
            if (action == "Start") StartService(service);
            else if (action == "Stop") StopService(service);
            else if (action == "Restart") { StopService(service); StartService(service); }
        });
    }

    private static void StartService(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
    }

    private static void StopService(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (!service.CanStop) throw new InvalidOperationException("Runner service cannot be stopped.");
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
    }

    public static async Task CreateRunnerAsync(RunnerSetup setup, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("Connect GitHub first.");
        var repoPath = RepositoryPath(setup.RepositoryUrl);
        if (string.IsNullOrWhiteSpace(repoPath)) throw new InvalidOperationException("Enter a valid GitHub repository URL.");
        if (Directory.Exists(setup.InstallPath) && Directory.EnumerateFileSystemEntries(setup.InstallPath).Any())
            throw new InvalidOperationException("The runner folder already contains files.");

        Directory.CreateDirectory(setup.InstallPath);
        log("Resolving latest GitHub Actions runner...");
        var package = await GetLatestWindowsRunnerAsync();
        var zipPath = Path.Combine(setup.InstallPath, package.FileName);
        log($"Downloading {package.Version}...");
        await DownloadFileAsync(package.DownloadUrl, zipPath);
        ZipFile.ExtractToDirectory(zipPath, setup.InstallPath, overwriteFiles: true);

        log("Requesting temporary registration token...");
        var token = await GitHubCli.GetRunnerTokenAsync(repoPath, removal: false);
        log("Configuring Windows service...");
        await ConfigureAsync(setup.InstallPath, setup.RepositoryUrl, token, setup.RunnerName,
            "_work", setup.Labels, runAsService: true);
        TryDelete(zipPath);
        log("Checking repository workflows...");
        await WorkflowManager.CheckAndUpdateAsync(repoPath, setup.Labels, log);
        log("Runner created and registered.");
    }

    public static async Task RepairRunnerAsync(RunnerInfo runner, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("Connect GitHub first.");
        if (string.IsNullOrWhiteSpace(runner.RepositoryPath) || string.IsNullOrWhiteSpace(runner.GitHubUrl))
            throw new InvalidOperationException("Runner repository information is missing.");

        var remote = await GitHubCli.GetRunnerStateAsync(runner.RepositoryPath, runner.AgentId);
        var labels = remote?.Labels ?? runner.CustomLabels;
        log("Requesting temporary removal token...");
        var removeToken = await GitHubCli.GetRunnerTokenAsync(runner.RepositoryPath, removal: true);
        await RunConfigRemoveAsync(runner.InstallPath, removeToken);

        log("Requesting fresh registration token...");
        var registerToken = await GitHubCli.GetRunnerTokenAsync(runner.RepositoryPath, removal: false);
        log("Reconfiguring runner as a Windows service...");
        await ConfigureAsync(runner.InstallPath, runner.GitHubUrl, registerToken,
            runner.AgentName, runner.WorkFolder, labels, runAsService: true);
        log("Checking repository workflows...");
        await WorkflowManager.CheckAndUpdateAsync(runner.RepositoryPath, labels, log);
        log("Runner service repaired.");
    }

    public static async Task CheckWorkflowsAsync(RunnerInfo runner, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("Connect GitHub first.");
        await WorkflowManager.CheckAndUpdateAsync(runner.RepositoryPath, runner.CustomLabels, log);
    }

    public static async Task RemoveRunnerAsync(RunnerInfo runner, bool deleteFolder, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("Connect GitHub first.");
        if (string.IsNullOrWhiteSpace(runner.RepositoryPath))
            throw new InvalidOperationException("Runner repository information is missing.");

        if (runner.ServiceInstalled && runner.IsRunning)
            await ControlServiceAsync(runner.Name, "Stop");
        log("Requesting temporary removal token...");
        var token = await GitHubCli.GetRunnerTokenAsync(runner.RepositoryPath, removal: true);
        await RunConfigRemoveAsync(runner.InstallPath, token);
        if (deleteFolder && Directory.Exists(runner.InstallPath))
        {
            log("Deleting local runner folder...");
            Directory.Delete(runner.InstallPath, recursive: true);
        }
        log("Runner removed.");
    }

    private static async Task ConfigureAsync(string root, string repositoryUrl, string token,
        string name, string workFolder, IEnumerable<string> labels, bool runAsService)
    {
        var args = new List<string>
        {
            "--unattended", "--url", repositoryUrl, "--token", token,
            "--name", name, "--work", string.IsNullOrWhiteSpace(workFolder) ? "_work" : workFolder
        };
        var labelList = labels.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray();
        if (labelList.Length > 0)
        {
            args.Add("--labels");
            args.Add(string.Join(',', labelList));
        }
        if (runAsService) args.Add("--runasservice");
        var result = await RunBatchAsync(Path.Combine(root, "config.cmd"), root, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
    }

    private static async Task RunConfigRemoveAsync(string root, string token)
    {
        var config = Path.Combine(root, "config.cmd");
        if (!File.Exists(config)) throw new FileNotFoundException("config.cmd was not found.", config);
        var result = await RunBatchAsync(config, root, new[] { "remove", "--token", token });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
    }

    private static string CleanConfigError(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return string.IsNullOrWhiteSpace(text) ? "Runner configuration failed." : text.Trim();
    }

    private static async Task<CommandResult> RunBatchAsync(string batchPath, string workingDirectory, IEnumerable<string> arguments)
    {
        var joined = string.Join(" ", arguments.Select(QuoteArgument));
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{batchPath}\" {joined}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start runner configuration.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static async Task<RunnerPackage> GetLatestWindowsRunnerAsync()
    {
        using var client = CreateHttpClient();
        var jsonText = await client.GetStringAsync("https://api.github.com/repos/actions/runner/releases/latest");
        using var json = JsonDocument.Parse(jsonText);
        var root = json.RootElement;
        var version = root.GetProperty("tag_name").GetString() ?? "latest";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.StartsWith("actions-runner-win-x64-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip"))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!string.IsNullOrWhiteSpace(url)) return new RunnerPackage(version, name, url);
        }
        throw new InvalidOperationException("GitHub's latest release has no Windows x64 runner package.");
    }

    private static async Task DownloadFileAsync(string url, string destination)
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RunnerWidget/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string RepositoryPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return uri.AbsolutePath.Trim('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryName(string url)
    {
        var path = RepositoryPath(url);
        return string.IsNullOrWhiteSpace(path) ? url : path;
    }

    private static string ResolveRunnerRoot(string servicePath)
    {
        var exe = ExtractExecutablePath(servicePath);
        var directory = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(directory)) return exe;
        var current = new DirectoryInfo(directory);
        return current.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && current.Parent != null
            ? current.Parent.FullName : current.FullName;
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? text[..(exe + 4)] : text.Split(' ')[0];
    }

    private static string GetLatestRunnerActivity(string root)
    {
        try
        {
            var diag = Path.Combine(root, "_diag");
            if (!Directory.Exists(diag)) return "No local logs";
            var latest = new DirectoryInfo(diag).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            return latest == null ? "No local logs" : FormatAge(latest.LastWriteTime);
        }
        catch { return "Log access unavailable"; }
    }

    private static string FormatAge(DateTime timestamp)
    {
        var age = DateTime.Now - timestamp;
        if (age.TotalSeconds < 10) return "Activity just now";
        if (age.TotalMinutes < 1) return $"Activity {(int)age.TotalSeconds}s ago";
        if (age.TotalHours < 1) return $"Activity {(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"Activity {(int)age.TotalHours}h ago";
        return $"Activity {timestamp:g}";
    }

    private static uint ConvertToUInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToUInt32(value); }
        catch { return 0; }
    }
}

public sealed record RunnerPackage(string Version, string FileName, string DownloadUrl);

public sealed class RunnerSetup
{
    public string RepositoryUrl { get; init; } = string.Empty;
    public string RunnerName { get; init; } = Environment.MachineName;
    public string InstallPath { get; init; } = string.Empty;
    public string[] Labels { get; init; } = Array.Empty<string>();
}

public sealed class RunnerConfig
{
    public long AgentId { get; init; }
    public string AgentName { get; init; } = "Runner";
    public string GitHubUrl { get; init; } = string.Empty;
    public string WorkFolder { get; init; } = "_work";
}

public sealed class RunnerServiceRecord
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Root { get; init; } = string.Empty;
    public uint ProcessId { get; init; }
}

public sealed class RunnerInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string ActivityText { get; init; } = string.Empty;
    public string LastActivityText { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string GitHubUrl { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public long AgentId { get; init; }
    public string WorkFolder { get; init; } = "_work";
    public bool ServiceInstalled { get; init; }
    public bool IsRunning { get; init; }
    public Brush StatusBrush { get; init; } = Brushes.Gray;
    public string GitHubText { get; init; } = string.Empty;
    public string CurrentWorkText { get; init; } = "Current job: unavailable";
    public string QueueText { get; init; } = "Queue: unavailable";
    public string CurrentJobUrl { get; init; } = string.Empty;
    public int QueueCount { get; init; }
    public string[] CustomLabels { get; init; } = Array.Empty<string>();
    public Visibility ServiceButtonsVisibility => ServiceInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RepairButtonVisibility => ServiceInstalled ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ProcessInfo
{
    public uint ProcessId { get; init; }
    public uint ParentProcessId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string CreationDate { get; init; } = string.Empty;
}
