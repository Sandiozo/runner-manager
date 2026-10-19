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
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace RunnerManager;

public static class RunnerService
{
    public const string DefaultRoot = @"C:\GitHub-Runners";

    /// <summary>Where new and moved runners are installed on this PC (Settings → General).</summary>
    public static string RunnersRoot => FleetConfig.Current.RunnerFolder is { Length: > 0 } folder ? folder : DefaultRoot;

    /// <summary>
    /// All runners on this PC. WMI queries and file reads are slow, so the work runs on the thread pool, and the
    /// GitHub queries for the runners run in parallel (GitHubCli limits how many gh processes run at a time).
    /// </summary>
    public static Task<List<RunnerInfo>> ReadSnapshotAsync(bool queryGitHub) => Task.Run(async () =>
    {
        var processes = ReadProcesses();
        var services = ReadRunnerServices();
        var roots = new HashSet<string>(services.Select(s => s.Root), StringComparer.OrdinalIgnoreCase);
        // The default folder is scanned too, for runners installed before the folder setting was changed.
        foreach (var folder in new[] { RunnersRoot, DefaultRoot }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var dir in Directory.GetDirectories(folder))
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

        var runners = await Task.WhenAll(roots.OrderBy(x => x)
            .Select(root => BuildRunnerInfoAsync(root, services, processes, queryGitHub)));
        return runners.ToList();
    });

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
            // A new ServiceController reads the current status on first use, so no Refresh is needed here.
            using var service = new ServiceController(serviceName);
            if (action == "Start") StartService(service);
            else if (action == "Stop") StopService(service);
            else if (action == "Restart") { StopService(service); StartService(service); }
        });
    }

    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(25);

    private static void StartService(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        // A pending service rejects new commands: wait for it to settle instead of sending the command again.
        if (service.Status == ServiceControllerStatus.StartPending)
        {
            WaitForStatus(service, ServiceControllerStatus.Running, "start");
            return;
        }
        if (service.Status == ServiceControllerStatus.StopPending)
            WaitForStatus(service, ServiceControllerStatus.Stopped, "stop");
        service.Start();
        WaitForStatus(service, ServiceControllerStatus.Running, "start");
    }

    private static void StopService(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status == ServiceControllerStatus.StopPending)
        {
            WaitForStatus(service, ServiceControllerStatus.Stopped, "stop");
            return;
        }
        // CanStop is false while the service is still starting.
        if (service.Status == ServiceControllerStatus.StartPending)
        {
            WaitForStatus(service, ServiceControllerStatus.Running, "start");
            service.Refresh();
        }
        if (!service.CanStop) throw new InvalidOperationException("The runner service cannot be stopped.");
        service.Stop();
        WaitForStatus(service, ServiceControllerStatus.Stopped, "stop");
    }

    private static void WaitForStatus(ServiceController service, ServiceControllerStatus status, string verb)
    {
        try { service.WaitForStatus(status, ServiceTimeout); }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new InvalidOperationException($"The runner service did not {verb} within {(int)ServiceTimeout.TotalSeconds} seconds.");
        }
    }

    public static async Task CreateRunnerAsync(RunnerSetup setup, Action<string> log)
    {
        await EnsureAuthenticatedAsync();
        var repoPath = RepositoryPath(setup.RepositoryUrl);
        var scope = ApiScope(setup.RepositoryUrl);
        if (string.IsNullOrWhiteSpace(scope)) throw new InvalidOperationException("Enter a valid GitHub repository or organization URL.");
        EnsureValidConfigInput(setup.RepositoryUrl, setup.RunnerName, setup.Labels);
        EnsureSafeFolder(setup.InstallPath);
        if (Directory.Exists(setup.InstallPath) && Directory.EnumerateFileSystemEntries(setup.InstallPath).Any())
            throw new InvalidOperationException("The runner folder already contains files. Choose an empty or new folder.");

        var createdFolder = !Directory.Exists(setup.InstallPath);
        Directory.CreateDirectory(setup.InstallPath);
        var zipPath = string.Empty;
        try
        {
            log("Finding the latest GitHub Actions runner…");
            var package = await GetLatestWindowsRunnerAsync();
            zipPath = Path.Combine(setup.InstallPath, package.FileName);
            log($"Downloading runner {package.Version}…");
            await DownloadFileAsync(package.DownloadUrl, zipPath);
            if (string.IsNullOrEmpty(package.Sha256))
                log("GitHub did not publish a checksum for this package. Checksum check skipped.");
            else
            {
                await VerifySha256Async(zipPath, package.Sha256);
                log("Package checksum verified.");
            }
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, setup.InstallPath, overwriteFiles: true));

            log("Requesting a registration token…");
            var token = await GitHubCli.GetRunnerTokenAsync(scope, removal: false);
            log("Configuring the runner as a Windows service…");
            await ConfigureAsync(setup.InstallPath, setup.RepositoryUrl, token, setup.RunnerName,
                "_work", setup.Labels, runAsService: true, replace: setup.ReplaceExisting);
        }
        catch
        {
            // Leave the folder as it was, so trying again with the same folder works.
            await Task.Run(() => RemoveFailedInstall(setup.InstallPath, createdFolder));
            throw;
        }
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
        await EnsureAuthenticatedAsync();
        if (string.IsNullOrWhiteSpace(runner.ApiScope) || string.IsNullOrWhiteSpace(runner.GitHubUrl))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var labels = await ReadLabelsBeforeRemovalAsync(runner);
        // Check the input before config remove, so an invalid value cannot leave the runner unregistered.
        EnsureValidConfigInput(runner.GitHubUrl, runner.AgentName, labels);
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
        await EnsureAuthenticatedAsync("GitHub is not connected on the target PC. Connect GitHub on that PC and try again.");
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("Runner name is required.");

        var local = await FindRunnerAsync(key)
            ?? throw new InvalidOperationException("The runner was not found on the target PC.");
        EnsureNotBusy(local);
        if (string.IsNullOrWhiteSpace(local.ApiScope) || string.IsNullOrWhiteSpace(local.GitHubUrl))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var labels = (requestedLabels ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Blank labels mean "keep the current ones", which have to be read from GitHub before config remove.
        if (labels.Length == 0) labels = await ReadLabelsBeforeRemovalAsync(local);
        EnsureValidConfigInput(local.GitHubUrl, newName.Trim(), labels);
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
        await EnsureAuthenticatedAsync();
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
        await EnsureAuthenticatedAsync();

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
            await Task.Run(() => DeleteFolder(runner.InstallPath));
        }
        ForgetRoot(runner.InstallPath);
        log("Runner removed.");
    }

    private static async Task EnsureAuthenticatedAsync(string message = "GitHub is not connected. Connect GitHub and try again.")
    {
        if (!await GitHubCli.IsAuthenticatedAsync()) throw new InvalidOperationException(message);
    }

    private static void EnsureNotBusy(RunnerInfo runner)
    {
        if (runner.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runner is busy. Wait for the current job to finish and try again.");
    }

    /// <summary>
    /// The runner's custom labels from GitHub, read before config remove so that registering it again keeps them.
    /// Stops when GitHub cannot be read (timeout, rate limit); continues only when GitHub confirms the runner is gone.
    /// </summary>
    private static async Task<string[]> ReadLabelsBeforeRemovalAsync(RunnerInfo runner)
    {
        // Without a runner ID there is no registration on GitHub to read labels from.
        if (runner.AgentId <= 0) return runner.CustomLabels;
        var lookup = await GitHubCli.LookUpRunnerAsync(runner.ApiScope, runner.AgentId);
        if (lookup.State != null) return lookup.State.Labels;
        if (lookup.NotFound) return runner.CustomLabels;
        throw new InvalidOperationException("Could not read the runner's labels from GitHub. Try again.");
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
        EnsureNotBusy(runner);
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
            var (code, output) = await ScheduledTasks.RunAsync("sc.exe", $"delete \"{runner.Name}\"", ScheduledTasks.ShortTimeout);
            if (code != 0) throw new InvalidOperationException($"Could not remove the Windows service {runner.Name}: {output.Trim()}");
        }
        if (deleteFolder)
        {
            log("Deleting the runner folder…");
            await Task.Run(() => DeleteFolder(runner.InstallPath));
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
        EnsureNotBusy(runner);
        await RepairRunnerAsync(runner, log);
    }

    /// <summary>
    /// For runners started from a console: stops the console (and any restart loop around it) and moves startup
    /// scripts that launch it into %LOCALAPPDATA%\RunnerManager\disabled-startup, so it doesn't run twice.
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
                try { content = ReadStartupText(File.ReadAllBytes(file), extension == ".lnk"); }
                catch { continue; }
                if (!ReferencesFolder(content, root)) continue;
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

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Text to search for the runner folder. Shortcuts store paths as UTF-16 and as ANSI, so both decodings are
    /// searched. Scripts are read as UTF-8 when they are valid UTF-8, and as Latin-1 otherwise (any byte decodes).
    /// </summary>
    internal static string ReadStartupText(byte[] bytes, bool shortcut)
    {
        if (shortcut) return System.Text.Encoding.Unicode.GetString(bytes) + System.Text.Encoding.Latin1.GetString(bytes);
        try { return StrictUtf8.GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>
    /// True when the text names this folder, and not only a sibling that starts with the same name
    /// (C:\GitHub-Runners\foo must not match C:\GitHub-Runners\foo-2).
    /// </summary>
    internal static bool ReferencesFolder(string content, string root)
    {
        var folder = root.TrimEnd('\\', '/');
        if (folder.Length == 0) return false;
        return Regex.IsMatch(content, Regex.Escape(folder) + @"(?=[\\/""'\s\0]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

    /// <summary>After a failed install: deletes the folder if this install created it, or empties it if it was empty before.</summary>
    private static void RemoveFailedInstall(string path, bool createdFolder)
    {
        try
        {
            if (createdFolder)
            {
                DeleteFolder(path);
                return;
            }
            if (!Directory.Exists(path)) return;
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo directory) DeleteFolder(directory.FullName);
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
        }
        catch { }
    }

    private static string RootFromExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;
        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory)) return string.Empty;
        var info = new DirectoryInfo(directory);
        return info.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && info.Parent != null ? info.Parent.FullName : info.FullName;
    }

    /// <summary>A free folder in the runner folder for a runner of this repository or organization.</summary>
    public static string SuggestInstallPath(string registrationUrl)
    {
        var name = RepositoryPath(registrationUrl).Split('/').LastOrDefault(s => s.Length > 0)
                   ?? OrganizationName(registrationUrl);
        if (string.IsNullOrWhiteSpace(name)) name = "runner";
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        var root = RunnersRoot;
        var path = Path.Combine(root, safe);
        for (var i = 2; Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); i++)
            path = Path.Combine(root, $"{safe}-{i}");
        return path;
    }

    private static async Task ConfigureAsync(string root, string repositoryUrl, string token,
        string name, string workFolder, IEnumerable<string> labels, bool runAsService, bool replace = false)
    {
        repositoryUrl = await CurrentRepositoryUrlAsync(repositoryUrl);
        var arguments = BuildConfigureArguments(repositoryUrl, token, name, workFolder, labels, runAsService, replace);
        var result = await RunConfigAsync(root, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
    }

    /// <summary>
    /// The repository's URL under its current name. The GitHub API follows a renamed repository, but config.cmd does
    /// not, so registering with the old name fails with "Not Found".
    /// </summary>
    private static async Task<string> CurrentRepositoryUrlAsync(string registrationUrl)
    {
        var path = RepositoryPath(registrationUrl);
        if (string.IsNullOrWhiteSpace(path)) return registrationUrl;
        var result = await GitHubCli.RunAsync($"api repos/{path} --jq .html_url");
        var current = result.ExitCode == 0 ? result.Output.Trim() : string.Empty;
        return RegistrationUrlProblem(current) == null && !current.Equals(registrationUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? current : registrationUrl;
    }

    private static async Task RunConfigRemoveAsync(string root, string token)
    {
        var config = Path.Combine(root, "config.cmd");
        if (!File.Exists(config)) throw new FileNotFoundException("config.cmd was not found in the runner folder.", config);
        EnsureSafeValue(token, "runner token");
        var result = await RunConfigAsync(root, $"remove --token {Quote(token)}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
    }

    private static string CleanConfigError(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return string.IsNullOrWhiteSpace(text) ? "Runner configuration failed." : text.Trim();
    }

    // config.cmd runs through cmd.exe as administrator, and the values come from users and from other PCs in the
    // fleet. Every value is checked here and quoted, so none of them can add a command to the line.
    private const string NameRule = "Runner names can only contain letters, digits, '-', '_' and '.'.";
    private const string LabelRule = "Labels can only contain letters, digits, '-', '_', '.' and ','.";
    private const string UrlRule = "The GitHub URL must start with https://github.com/ and can only contain letters, digits, '-', '_', '.' and '/'.";
    private static readonly Regex RegistrationUrlPattern = new(@"^https://github\.com/[A-Za-z0-9._/-]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>Characters that cmd.exe treats specially even inside quotes, or that end a quoted value.</summary>
    private static readonly char[] UnsafeInQuotes = { '"', '%', '!' };
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromMinutes(10);

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    /// <summary>Null when the runner name is valid, otherwise the message to show.</summary>
    internal static string? RunnerNameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Runner name is required.";
        return name.Trim().All(IsNameCharacter) ? null : NameRule;
    }

    /// <summary>Null when the labels are valid, otherwise the message to show. Blank labels are ignored.</summary>
    internal static string? LabelsProblem(IEnumerable<string>? labels)
        => CleanLabels(labels).All(label => label.All(c => IsNameCharacter(c) || c == ',')) ? null : LabelRule;

    /// <summary>Null when the registration URL is a plain https://github.com/... URL, otherwise the message to show.</summary>
    internal static string? RegistrationUrlProblem(string? url)
        => !string.IsNullOrEmpty(url) && RegistrationUrlPattern.IsMatch(url) ? null : UrlRule;

    internal static void EnsureValidConfigInput(string url, string name, IEnumerable<string>? labels)
    {
        var problem = RegistrationUrlProblem(url) ?? RunnerNameProblem(name) ?? LabelsProblem(labels);
        if (problem != null) throw new InvalidOperationException(problem);
    }

    /// <summary>The config.cmd arguments for registering a runner. Throws when a value is not allowed.</summary>
    internal static string BuildConfigureArguments(string url, string token, string name, string workFolder,
        IEnumerable<string>? labels, bool runAsService, bool replace)
    {
        var labelList = CleanLabels(labels);
        EnsureValidConfigInput(url, name, labelList);
        EnsureSafeValue(token, "runner token");
        var work = string.IsNullOrWhiteSpace(workFolder) ? "_work" : workFolder.Trim();
        EnsureSafeValue(work, "work folder");

        var args = new List<string>
        {
            "--unattended", "--url", Quote(url), "--token", Quote(token),
            "--name", Quote(name.Trim()), "--work", Quote(work)
        };
        if (labelList.Length > 0)
        {
            args.Add("--labels");
            args.Add(Quote(string.Join(',', labelList)));
        }
        if (runAsService) args.Add("--runasservice");
        if (replace) args.Add("--replace");
        return string.Join(' ', args);
    }

    private static string[] CleanLabels(IEnumerable<string>? labels)
        => (labels ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray();

    private static void EnsureSafeValue(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny(UnsafeInQuotes) >= 0 || value.Any(char.IsControl))
            throw new InvalidOperationException($"The {what} contains characters that cannot be passed to config.cmd.");
    }

    /// <summary>Null when the folder can be used for runners, otherwise the message to show.</summary>
    internal static string? RunnerFolderProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return @"Choose a full folder path, such as D:\Runners.";
        return path.IndexOfAny(UnsafeInQuotes) >= 0 || path.Any(char.IsControl)
            ? "The runner folder path cannot contain the characters \", % or !."
            : null;
    }

    private static void EnsureSafeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(UnsafeInQuotes) >= 0 || path.Any(char.IsControl))
            throw new InvalidOperationException("The runner folder path cannot contain the characters \", % or !.");
    }

    /// <summary>Quotes a checked value. Trailing backslashes are doubled so they do not escape the closing quote.</summary>
    private static string Quote(string value)
    {
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return $"\"{value}{new string('\\', trailing)}\"";
    }

    private static async Task<CommandResult> RunConfigAsync(string root, string arguments)
    {
        EnsureSafeFolder(root);
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{Path.Combine(root, "config.cmd")}\" {arguments}\"",
            WorkingDirectory = root
        };
        CommandResult? result;
        try { result = await ProcessRunner.RunAsync(startInfo, ConfigTimeout); }
        catch (Win32Exception ex) { throw new InvalidOperationException($"Could not start the runner configuration (config.cmd): {ex.Message}"); }
        return result ?? throw new InvalidOperationException(
            $"config.cmd did not finish within {ProcessRunner.Describe(ConfigTimeout)}. It was stopped.");
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RunnerManager/{AppUpdater.CurrentVersion}");
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
        EnsureNotBusy(runner);
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
        var root = RootFromExecutable(exe);
        return string.IsNullOrEmpty(root) ? exe : root;
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
            var latest = new DirectoryInfo(diag).EnumerateFiles("*.log")
                .Select(f => (DateTime?)f.LastWriteTimeUtc).Max();
            return latest == null ? "no logs" : FormatAge(latest.Value.ToLocalTime());
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
