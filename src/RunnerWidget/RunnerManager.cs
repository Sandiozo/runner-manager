using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
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
        // Runners started from a console (run.cmd) instead of a service live anywhere; find them by their
        // running listener and remember the folder so they stay visible while stopped.
        var consoleRoots = processes
            .Where(p => p.Name.Equals("Runner.Listener.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.ExecutablePath))
            .Select(p => RootFromExecutable(p.ExecutablePath))
            .Where(r => File.Exists(Path.Combine(r, ".runner")))
            .ToList();
        var known = FleetConfig.Current.KnownRunnerRoots;
        var newRoots = consoleRoots.Where(r => !known.Contains(r, StringComparer.OrdinalIgnoreCase) && !roots.Contains(r)).ToList();
        if (newRoots.Count > 0) FleetConfig.Update(c => c.KnownRunnerRoots.AddRange(newRoots));
        foreach (var root in consoleRoots) roots.Add(root);
        foreach (var root in FleetConfig.Current.KnownRunnerRoots)
            if (File.Exists(Path.Combine(root, ".runner"))) roots.Add(root);

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
        var consoleListener = serviceInstalled ? null : processes.FirstOrDefault(p =>
            p.Name.Equals("Runner.Listener.exe", StringComparison.OrdinalIgnoreCase)
            && RootFromExecutable(p.ExecutablePath).Equals(root, StringComparison.OrdinalIgnoreCase));
        var interactive = consoleListener != null;
        var running = service?.State.Equals("Running", StringComparison.OrdinalIgnoreCase) == true || interactive;
        var busy = running && HasDescendantWorker(service?.ProcessId ?? consoleListener!.ProcessId, processes);
        var repoPath = RepositoryPath(config.GitHubUrl);
        var scope = ApiScope(config.GitHubUrl);
        RemoteRunnerState? remote = null;
        RunnerWorkState? work = null;
        if (queryGitHub && !string.IsNullOrWhiteSpace(scope) && config.AgentId > 0)
        {
            remote = await GitHubCli.GetRunnerStateAsync(scope, config.AgentId);
            // Job and queue details come from a repository's workflow runs, so they only exist for repository runners.
            if (!string.IsNullOrWhiteSpace(repoPath))
                work = await GitHubApi.GetRunnerWorkAsync(repoPath, config.AgentId, remote?.Labels ?? Array.Empty<string>());
        }
        var workFolder = ResolveWorkFolder(root, config.WorkFolder);

        var label = !string.IsNullOrWhiteSpace(config.GitHubUrl)
            ? $"{RepositoryName(config.GitHubUrl)}  |  {config.AgentName}"
            : service?.DisplayName ?? Path.GetFileName(root);
        return new RunnerInfo
        {
            Name = service?.Name ?? string.Empty,
            DisplayName = label,
            StatusText = serviceInstalled ? service!.State : interactive ? "Console window" : "No service",
            ActivityText = running ? (busy ? "Busy" : "Idle") : "Offline",
            IsInteractive = interactive,
            LastActivityText = GetLatestRunnerActivity(root),
            InstallPath = root,
            GitHubUrl = config.GitHubUrl,
            RepositoryPath = repoPath,
            ApiScope = scope,
            RunnerVersion = SystemHealth.RunnerVersion(root),
            WorkFolderPath = workFolder,
            WorkFolderBytes = SystemHealth.FolderSize(workFolder),
            AgentName = config.AgentName,
            AgentId = config.AgentId,
            WorkFolder = config.WorkFolder,
            ServiceInstalled = serviceInstalled,
            IsRunning = running,
            StatusBrush = interactive ? Palette.Warning : running ? Palette.Success : serviceInstalled ? Palette.Danger : Palette.Warning,
            GitHubText = remote == null
                ? (queryGitHub ? "unavailable" : "connect GitHub to see status")
                : $"{remote.Status}  ·  {(remote.Busy ? "busy" : "idle")}",
            CurrentWorkText = work?.CurrentWorkText ?? (queryGitHub ? "unavailable" : "connect GitHub to see jobs"),
            QueueText = work?.QueueText ?? (queryGitHub ? "unavailable" : "connect GitHub to see the queue"),
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
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath FROM Win32_Process WHERE Name LIKE 'Runner%'");
        foreach (ManagementObject process in searcher.Get())
        {
            result.Add(new ProcessInfo
            {
                ProcessId = ConvertToUInt(process["ProcessId"]),
                ParentProcessId = ConvertToUInt(process["ParentProcessId"]),
                Name = process["Name"]?.ToString() ?? string.Empty,
                ExecutablePath = process["ExecutablePath"]?.ToString() ?? string.Empty
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

    /// <summary>Finds a local runner by service name, or by install folder for runners without a service.</summary>
    public static async Task<RunnerInfo?> FindRunnerAsync(string key)
        => (await ReadSnapshotAsync(queryGitHub: false)).FirstOrDefault(r =>
            (!string.IsNullOrEmpty(r.Name) && r.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            || r.InstallPath.Equals(key.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <param name="key">Service name, or the install folder of a runner that runs in a console window.</param>
    public static async Task ControlServiceAsync(string key, string action)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("This runner has no Windows service.");
        if (!key.StartsWith("actions.runner.", StringComparison.OrdinalIgnoreCase))
        {
            // A console runner can only be stopped; starting it again needs a Windows service.
            if (action != "Stop")
                throw new InvalidOperationException("This runner runs in a console window. Convert it to a Windows service to start or restart it from Runner Manager.");
            await Task.Run(() => StopConsoleRunner(key.TrimEnd('\\')));
            return;
        }
        var serviceName = key;
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
        if (!service.CanStop) throw new InvalidOperationException("The runner service cannot be stopped.");
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
    }

    public static async Task CreateRunnerAsync(RunnerSetup setup, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("GitHub is not connected. Connect GitHub and try again.");
        var repoPath = RepositoryPath(setup.RepositoryUrl);
        var scope = ApiScope(setup.RepositoryUrl);
        if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Enter a valid GitHub repository or organization URL.");
        if (Directory.Exists(setup.InstallPath) && Directory.EnumerateFileSystemEntries(setup.InstallPath).Any())
            throw new InvalidOperationException("The runner folder already contains files. Choose an empty or new folder.");

        Directory.CreateDirectory(setup.InstallPath);
        log("Finding the latest GitHub Actions runner…");
        var package = await GetLatestWindowsRunnerAsync();
        var zipPath = Path.Combine(setup.InstallPath, package.FileName);
        log($"Downloading runner {package.Version}…");
        await DownloadFileAsync(package.DownloadUrl, zipPath);
        if (string.IsNullOrEmpty(package.Sha256))
            log("GitHub did not publish a checksum for this package. Checksum check skipped.");
        else
        {
            await VerifySha256Async(zipPath, package.Sha256);
            log("Package checksum verified.");
        }
        ZipFile.ExtractToDirectory(zipPath, setup.InstallPath, overwriteFiles: true);

        log("Requesting a registration token…");
        var token = await GitHubCli.GetRunnerTokenAsync(scope, removal: false);
        log("Configuring the runner as a Windows service…");
        await ConfigureAsync(setup.InstallPath, setup.RepositoryUrl, token, setup.RunnerName,
            "_work", setup.Labels, runAsService: true, replace: setup.ReplaceExisting);
        TryDelete(zipPath);
        if (setup.CheckWorkflows && !string.IsNullOrWhiteSpace(repoPath))
        {
            log("Checking repository workflows…");
            await WorkflowManager.CheckAndUpdateAsync(repoPath, setup.Labels, log);
        }
        log("Runner created and registered.");
    }

    public static async Task RepairRunnerAsync(RunnerInfo runner, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("GitHub is not connected. Connect GitHub and try again.");
        if (string.IsNullOrWhiteSpace(runner.ApiScope) || string.IsNullOrWhiteSpace(runner.GitHubUrl))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var remote = await GitHubCli.GetRunnerStateAsync(runner.ApiScope, runner.AgentId);
        var labels = remote?.Labels ?? runner.CustomLabels;
        await DetachConsoleRunnerAsync(runner, log);
        log("Requesting a removal token…");
        var removeToken = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: true);
        await RunConfigRemoveAsync(runner.InstallPath, removeToken);

        log("Requesting a registration token…");
        var registerToken = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: false);
        log("Configuring the runner as a Windows service…");
        await ConfigureAsync(runner.InstallPath, runner.GitHubUrl, registerToken,
            runner.AgentName, runner.WorkFolder, labels, runAsService: true);
        log("Runner service repaired.");
    }

    public static async Task EditRunnerAsync(string key, string newName, string[] requestedLabels, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("GitHub is not connected on the target PC. Connect GitHub on that PC and try again.");
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Runner name is required.");

        var local = await FindRunnerAsync(key)
            ?? throw new InvalidOperationException("The runner was not found on the target PC.");
        if (local.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner is busy. Wait for the current job to finish and try again.");
        if (string.IsNullOrWhiteSpace(local.ApiScope) || string.IsNullOrWhiteSpace(local.GitHubUrl))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var remote = await GitHubCli.GetRunnerStateAsync(local.ApiScope, local.AgentId);
        var labels = requestedLabels.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (labels.Length == 0) labels = remote?.Labels ?? Array.Empty<string>();
        var wasRunning = local.IsRunning;
        if (local.IsInteractive) await DetachConsoleRunnerAsync(local, log);
        else if (wasRunning) await ControlServiceAsync(local.Name, "Stop");

        log("Requesting a removal token…");
        var removeToken = await GitHubCli.GetRunnerTokenAsync(local.ApiScope, removal: true);
        await RunConfigRemoveAsync(local.InstallPath, removeToken);
        log("Requesting a registration token…");
        var registerToken = await GitHubCli.GetRunnerTokenAsync(local.ApiScope, removal: false);
        await ConfigureAsync(local.InstallPath, local.GitHubUrl, registerToken, newName.Trim(),
            local.WorkFolder, labels, runAsService: true);

        if (wasRunning)
        {
            var updated = (await ReadSnapshotAsync(queryGitHub: false))
                .FirstOrDefault(r => r.InstallPath.Equals(local.InstallPath, StringComparison.OrdinalIgnoreCase));
            if (updated != null && updated.ServiceInstalled && !updated.IsRunning)
                await ControlServiceAsync(updated.Name, "Start");
        }
        log("Runner settings updated.");
    }

    public static async Task CheckWorkflowsAsync(RunnerInfo runner, Action<string> log)
    {
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("GitHub is not connected. Connect GitHub and try again.");
        if (string.IsNullOrWhiteSpace(runner.RepositoryPath))
            throw new InvalidOperationException("Workflow checks are available only for repository runners.");
        await WorkflowManager.CheckAndUpdateAsync(runner.RepositoryPath, runner.CustomLabels, log);
    }

    public static async Task RemoveRunnerAsync(RunnerInfo runner, bool deleteFolder, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(runner.ApiScope))
        {
            log("The runner repository is unknown, so the runner cannot be removed from GitHub. Removing it from this PC only.");
            await RemoveLocallyAsync(runner, deleteFolder, log);
            return;
        }
        if (!await GitHubCli.IsAuthenticatedAsync())
            throw new InvalidOperationException("GitHub is not connected. Connect GitHub and try again.");

        if (runner.ServiceInstalled && runner.IsRunning)
            await ControlServiceAsync(runner.Name, "Stop");
        await DetachConsoleRunnerAsync(runner, log);
        try
        {
            log("Requesting a removal token…");
            var token = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: true);
            await RunConfigRemoveAsync(runner.InstallPath, token);
        }
        catch (Exception ex) when (IsGoneFromGitHub(ex.Message))
        {
            // The repository (or the runner registration) was deleted on GitHub, so there's nothing left to
            // unregister. Clean up the service and files here instead of failing.
            log($"The runner or repository no longer exists on GitHub ({FirstLine(ex.Message)}). Removing the runner from this PC only.");
            await RemoveLocallyAsync(runner, deleteFolder, log);
            return;
        }
        if (deleteFolder && Directory.Exists(runner.InstallPath))
        {
            log("Deleting the runner folder…");
            DeleteFolder(runner.InstallPath);
        }
        ForgetRoot(runner.InstallPath);
        log("Runner removed.");
    }

    /// <summary>GitHub answers 404 when the repository, organization or runner registration no longer exists.</summary>
    internal static bool IsGoneFromGitHub(string message)
        => message.Contains("404", StringComparison.Ordinal)
           || message.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
           || message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
           || message.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;

    /// <summary>Removes a runner on this PC by service name or install folder (used for remote removal).</summary>
    public static async Task RemoveRunnerAsync(string key, bool deleteFolder, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        if (runner.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner is busy. Wait for the current job to finish and try again.");
        await RemoveRunnerAsync(runner, deleteFolder, log);
    }

    /// <summary>
    /// Cleans up the old copy after a runner was moved: its GitHub registration was already replaced by the new
    /// PC (config --replace), so only the local service, console autostart and files are removed here.
    /// </summary>
    public static async Task RetireMovedRunnerAsync(string key, bool deleteFolder, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        await RemoveLocallyAsync(runner, deleteFolder, log);
    }

    /// <summary>Removes a runner from this PC without contacting GitHub: service, console autostart and files.</summary>
    private static async Task RemoveLocallyAsync(RunnerInfo runner, bool deleteFolder, Action<string> log)
    {
        await DetachConsoleRunnerAsync(runner, log);
        if (runner.ServiceInstalled)
        {
            if (runner.IsRunning) await ControlServiceAsync(runner.Name, "Stop");
            log("Removing the Windows service…");
            var (code, output) = await ScheduledTasks.RunAsync("sc.exe", $"delete \"{runner.Name}\"");
            if (code != 0) throw new InvalidOperationException($"Could not remove the Windows service {runner.Name}: {output.Trim()}");
        }
        if (deleteFolder)
        {
            log("Deleting the runner folder…");
            DeleteFolder(runner.InstallPath);
        }
        else
        {
            // Keep the files but drop the stale registration so the folder isn't listed as a runner any more.
            foreach (var file in new[] { ".runner", ".credentials", ".credentials_rsaparams", ".service" })
                TryDelete(Path.Combine(runner.InstallPath, file));
        }
        ForgetRoot(runner.InstallPath);
        log("Runner removed from this PC.");
    }

    /// <summary>Converts a console (run.cmd) runner to a Windows service, keeping its name and labels.</summary>
    public static async Task ConvertToServiceAsync(string key, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        if (runner.ServiceInstalled) throw new InvalidOperationException("This runner already runs as a Windows service.");
        if (runner.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner is busy. Wait for the current job to finish and try again.");
        await RepairRunnerAsync(runner, log);
    }

    /// <summary>
    /// For runners started from a console: stops the console (and any restart loop around it) and moves startup
    /// scripts that launch it into %LOCALAPPDATA%\RunnerWidget\disabled-startup, so it doesn't run twice.
    /// </summary>
    private static async Task DetachConsoleRunnerAsync(RunnerInfo runner, Action<string> log)
    {
        if (runner.IsInteractive)
        {
            log("Closing the runner console window…");
            await Task.Run(() => StopConsoleRunner(runner.InstallPath));
        }
        foreach (var moved in DisableStartupScripts(runner.InstallPath))
            log($"Disabled the startup entry {Path.GetFileName(moved)}. It was moved to {Path.GetDirectoryName(moved)}.");
    }

    private static void StopConsoleRunner(string root)
    {
        var all = new List<(uint Pid, uint Parent, string Name, string CommandLine, string Path)>();
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process"))
            foreach (ManagementObject p in searcher.Get())
                all.Add((ConvertToUInt(p["ProcessId"]), ConvertToUInt(p["ParentProcessId"]), p["Name"]?.ToString() ?? string.Empty,
                    p["CommandLine"]?.ToString() ?? string.Empty, p["ExecutablePath"]?.ToString() ?? string.Empty));

        foreach (var listener in all.Where(p => p.Name.Equals("Runner.Listener.exe", StringComparison.OrdinalIgnoreCase)
                     && RootFromExecutable(p.Path).Equals(root, StringComparison.OrdinalIgnoreCase)))
        {
            // Walk up through the cmd.exe wrappers (run.cmd, supervisor loops) that belong to this runner folder.
            var top = listener.Pid;
            var current = listener;
            for (var depth = 0; depth < 6; depth++)
            {
                var parent = all.FirstOrDefault(p => p.Pid == current.Parent);
                if (parent.Pid == 0 || !parent.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
                    || !parent.CommandLine.Contains(root, StringComparison.OrdinalIgnoreCase)) break;
                top = parent.Pid;
                current = parent;
            }
            try
            {
                using var process = Process.GetProcessById((int)top);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Startup-folder scripts and shortcuts that launch the runner in this folder, moved aside (reversible).</summary>
    private static List<string> DisableStartupScripts(string root)
    {
        var moved = new List<string>();
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };
        var target = Path.Combine(FleetConfig.StateDir, "disabled-startup");
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".cmd" or ".bat" or ".ps1" or ".vbs" or ".lnk")) continue;
                string content;
                try { content = extension == ".lnk" ? System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(file)) + System.Text.Encoding.Default.GetString(File.ReadAllBytes(file)) : File.ReadAllText(file); }
                catch { continue; }
                if (!content.Contains(root, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.CreateDirectory(target);
                    var destination = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(destination)) destination = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
                    File.Move(file, destination);
                    moved.Add(destination);
                }
                catch { }
            }
        }
        return moved;
    }

    private static void ForgetRoot(string root)
    {
        if (FleetConfig.Current.KnownRunnerRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            FleetConfig.Update(c => c.KnownRunnerRoots.RemoveAll(r => r.Equals(root, StringComparison.OrdinalIgnoreCase)));
    }

    private static void DeleteFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }

    private static string RootFromExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;
        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory)) return string.Empty;
        var info = new DirectoryInfo(directory);
        return info.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && info.Parent != null ? info.Parent.FullName : info.FullName;
    }

    /// <summary>A free folder under C:\GitHub-Runners for a runner of this repository or organization.</summary>
    public static string SuggestInstallPath(string registrationUrl)
    {
        var name = RepositoryPath(registrationUrl).Split('/').LastOrDefault(s => s.Length > 0)
                   ?? OrganizationName(registrationUrl);
        if (string.IsNullOrWhiteSpace(name)) name = "runner";
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        var path = Path.Combine(DefaultRoot, safe);
        for (var i = 2; Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); i++)
            path = Path.Combine(DefaultRoot, $"{safe}-{i}");
        return path;
    }

    private static async Task ConfigureAsync(string root, string repositoryUrl, string token,
        string name, string workFolder, IEnumerable<string> labels, bool runAsService, bool replace = false)
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
        if (replace) args.Add("--replace");
        var result = await RunBatchAsync(Path.Combine(root, "config.cmd"), root, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
    }

    private static async Task RunConfigRemoveAsync(string root, string token)
    {
        var config = Path.Combine(root, "config.cmd");
        if (!File.Exists(config)) throw new FileNotFoundException("config.cmd was not found in the runner folder.", config);
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the runner configuration (config.cmd).");
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
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? string.Empty : string.Empty;
            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
            if (!string.IsNullOrWhiteSpace(url)) return new RunnerPackage(version, name, url, sha256);
        }
        throw new InvalidOperationException("The latest GitHub Actions runner release has no Windows x64 package.");
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

    private static async Task VerifySha256Async(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            stream.Close();
            TryDelete(path);
            throw new InvalidOperationException($"The downloaded runner package did not pass the SHA-256 check (expected {expected}, got {actual}). Try again.");
        }
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
        // Accept https://github.com/owner/repo, .../repo.git and deeper links like .../repo/tree/main.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return string.Empty;
        // github.com/orgs/name/... and github.com/organizations/name/... are organization pages, not repositories.
        if (segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("organizations", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return string.IsNullOrEmpty(repo) ? string.Empty : $"{segments[0]}/{repo}";
    }

    /// <summary>
    /// The REST API prefix for a runner registration URL: "repos/owner/repo" for repository runners,
    /// "orgs/name" for organization runners (https://github.com/name), or empty when the URL isn't GitHub.
    /// </summary>
    public static string ApiScope(string url)
    {
        var repo = RepositoryPath(url);
        if (!string.IsNullOrEmpty(repo)) return $"repos/{repo}";
        var org = OrganizationName(url);
        return string.IsNullOrEmpty(org) ? string.Empty : $"orgs/{org}";
    }

    public static string OrganizationName(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1) return segments[0];
        if (segments.Length >= 2 && (segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase)
                                     || segments[0].Equals("organizations", StringComparison.OrdinalIgnoreCase))) return segments[1];
        return string.Empty;
    }

    /// <summary>Canonical registration URL: organization links like github.com/orgs/name become github.com/name.</summary>
    public static string NormalizeRegistrationUrl(string url)
    {
        var repo = RepositoryPath(url);
        if (!string.IsNullOrEmpty(repo)) return $"https://github.com/{repo}";
        var org = OrganizationName(url);
        return string.IsNullOrEmpty(org) ? url : $"https://github.com/{org}";
    }

    private static string RepositoryName(string url)
    {
        var path = RepositoryPath(url);
        if (!string.IsNullOrWhiteSpace(path)) return path;
        var org = OrganizationName(url);
        return string.IsNullOrWhiteSpace(org) ? url : $"{org} (organization)";
    }

    private static string ResolveWorkFolder(string root, string workFolder)
    {
        var folder = string.IsNullOrWhiteSpace(workFolder) ? "_work" : workFolder;
        return Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder);
    }

    /// <summary>
    /// Deletes everything in the runner's work folder (checkouts, tool caches, build output) to reclaim disk space.
    /// The service is stopped first so no job can start mid-cleanup, then started again if it was running.
    /// </summary>
    public static async Task CleanWorkFolderAsync(string serviceName, Action<string> log)
    {
        var runner = await FindRunnerAsync(serviceName)
            ?? throw new InvalidOperationException("The runner was not found on this PC.");
        if (runner.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner is busy. Wait for the current job to finish and try again.");
        if (runner.IsInteractive)
            throw new InvalidOperationException("This runner runs in a console window. Convert it to a Windows service first.");
        var folder = runner.WorkFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            log("Work folder is already empty.");
            return;
        }
        var fullFolder = Path.GetFullPath(folder).TrimEnd('\\');
        var fullRoot = Path.GetFullPath(runner.InstallPath).TrimEnd('\\') + "\\";
        if (string.Equals(Path.GetPathRoot(fullFolder)?.TrimEnd('\\'), fullFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot clean {fullFolder}: it is a drive root.");
        if (!fullFolder.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot clean {fullFolder}: it is outside the runner folder.");

        var wasRunning = runner.IsRunning;
        if (wasRunning)
        {
            log("Stopping the runner service…");
            await ControlServiceAsync(runner.Name, "Stop");
        }
        try
        {
            log($"Deleting the contents of {fullFolder}…");
            var failures = await Task.Run(() =>
            {
                var failed = 0;
                foreach (var entry in new DirectoryInfo(fullFolder).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (entry is DirectoryInfo directory)
                        {
                            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                                file.Attributes = FileAttributes.Normal;
                            directory.Delete(recursive: true);
                        }
                        else
                        {
                            entry.Attributes = FileAttributes.Normal;
                            entry.Delete();
                        }
                    }
                    catch { failed++; }
                }
                return failed;
            });
            SystemHealth.ForgetFolderSize(fullFolder);
            log(failures == 0 ? "Work folder cleaned." : $"Work folder cleaned. {failures} item(s) were in use and were not deleted.");
        }
        finally
        {
            if (wasRunning)
            {
                log("Starting the runner service…");
                await ControlServiceAsync(runner.Name, "Start");
            }
        }
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
            if (!Directory.Exists(diag)) return "no logs";
            var latest = new DirectoryInfo(diag).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            return latest == null ? "no logs" : FormatAge(latest.LastWriteTime);
        }
        catch { return "cannot read logs"; }
    }

    private static string FormatAge(DateTime timestamp)
    {
        var age = DateTime.Now - timestamp;
        if (age.TotalSeconds < 10) return "just now";
        if (age.TotalMinutes < 1) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return timestamp.ToString("g");
    }

    private static uint ConvertToUInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToUInt32(value); }
        catch { return 0; }
    }
}

public sealed record RunnerPackage(string Version, string FileName, string DownloadUrl, string Sha256);

public sealed class RunnerSetup
{
    public string RepositoryUrl { get; init; } = string.Empty;
    public string RunnerName { get; init; } = Environment.MachineName;
    public string InstallPath { get; init; } = string.Empty;
    public string[] Labels { get; init; } = Array.Empty<string>();
    public bool CheckWorkflows { get; init; }
    /// <summary>Replace an existing runner with the same name (used when moving a runner to this PC).</summary>
    public bool ReplaceExisting { get; init; }
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

public sealed class RunnerInfo : INotifyPropertyChanged
{
    private bool _isExpanded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string ActivityText { get; init; } = string.Empty;
    public string LastActivityText { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public string GitHubUrl { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    /// <summary>"repos/owner/repo" or "orgs/name": the REST prefix for this runner's registration.</summary>
    public string ApiScope { get; init; } = string.Empty;
    public string RunnerVersion { get; init; } = string.Empty;
    public string WorkFolderPath { get; init; } = string.Empty;
    public long? WorkFolderBytes { get; init; }
    public string WorkFolderSizeText => SystemHealth.FormatBytes(WorkFolderBytes);
    public string AgentName { get; init; } = string.Empty;
    public long AgentId { get; init; }
    public string WorkFolder { get; init; } = "_work";
    public bool ServiceInstalled { get; init; }
    /// <summary>Running from a console window (run.cmd) rather than as a Windows service.</summary>
    public bool IsInteractive { get; init; }
    public bool IsRunning { get; init; }
    /// <summary>How commands address this runner: its service name, or its folder when it has no service.</summary>
    public string CommandKey => string.IsNullOrEmpty(Name) ? InstallPath : Name;
    public Brush StatusBrush { get; init; } = Palette.Muted;
    public string GitHubText { get; init; } = string.Empty;
    public string CurrentWorkText { get; init; } = "unavailable";
    public string QueueText { get; init; } = "unavailable";
    public string CurrentJobUrl { get; init; } = string.Empty;
    public int QueueCount { get; init; }
    public string[] CustomLabels { get; init; } = Array.Empty<string>();
    public string PowerButtonText => IsRunning ? "Stop" : "Start";
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility)));
        }
    }
    public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
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
    public string ExecutablePath { get; init; } = string.Empty;
}
