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
using Microsoft.Win32;

namespace RunnerManager;

public static partial class RunnerService
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
            // An offline network folder or one without access must not hide the other runners.
            string[] dirs;
            try { dirs = Directory.GetDirectories(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce($"runners-folder:{folder}", $"Could not read the runners folder {folder}: {ex.Message}");
                continue;
            }
            foreach (var dir in dirs)
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
            if (File.Exists(Path.Combine(root, ".runner"))) roots.Add(LongPath(root));

        // What starts console runners at sign-in, read once for all of them.
        var startup = roots.Any(root => !services.Any(s => s.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                      || processes.Any(p => p.SessionId != 0 && p.Name.Equals("Runner.Listener.exe", StringComparison.OrdinalIgnoreCase))
            ? await ReadStartupEntriesAsync() ?? new List<string>() : new List<string>();
        var runners = await Task.WhenAll(roots.OrderBy(x => x)
            .Select(root => BuildRunnerInfoAsync(root, services, processes, queryGitHub, startup, roots)));
        return runners.ToList();
    });

    private static readonly HashSet<string> LoggedProblems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes a problem to the agent log once per run of the app, since the runner list is read every few seconds.</summary>
    private static void LogOnce(string key, string message)
    {
        lock (LoggedProblems)
            if (!LoggedProblems.Add(key)) return;
        MachineRelay.AppendAgentLog(message);
    }

    /// <summary>How long a console runner that stopped is still shown as a console runner (it restarts while it updates itself).</summary>
    private static readonly TimeSpan ConsoleRestartGrace = TimeSpan.FromMinutes(3);
    /// <summary>Folders of console runners and when their listener was last seen running.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ConsoleSeenUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a console runner's listener is gone but was running a moment ago. A console runner that updates itself
    /// has no listener for a few seconds; it must not look like a runner without a service, or Repair would install a
    /// second copy of it as a service.
    /// </summary>
    internal static bool IsConsoleRestarting(string root, bool listenerRunning, DateTime nowUtc)
    {
        if (listenerRunning)
        {
            ConsoleSeenUtc[root] = nowUtc;
            return false;
        }
        return ConsoleSeenUtc.TryGetValue(root, out var seen) && nowUtc - seen < ConsoleRestartGrace;
    }

    private static async Task<RunnerInfo> BuildRunnerInfoAsync(
        string root, List<RunnerServiceRecord> services, List<ProcessInfo> processes, bool queryGitHub,
        List<string> startupEntries, IEnumerable<string> allRoots)
    {
        var config = ReadRunnerConfig(root);
        var service = services.FirstOrDefault(s => s.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        var serviceInstalled = service != null;
        var serviceRunning = service?.State.Equals("Running", StringComparison.OrdinalIgnoreCase) == true;
        // A runner can have a Windows service and still run from a console window in the user's session instead (a
        // runner moved to the session for a GPU or desktop app). Its stopped service must not be started then: that
        // would run the runner twice.
        var consoleListener = serviceRunning ? null : processes.FirstOrDefault(p =>
            p.Name.Equals("Runner.Listener.exe", StringComparison.OrdinalIgnoreCase)
            && (!serviceInstalled || p.SessionId != 0)
            && RootFromExecutable(p.ExecutablePath).Equals(root, StringComparison.OrdinalIgnoreCase));
        var restarting = !serviceRunning && IsConsoleRestarting(root, consoleListener != null, DateTime.UtcNow);
        var interactive = consoleListener != null || restarting;
        var running = serviceRunning || consoleListener != null;
        var busy = running && HasDescendantWorker(consoleListener?.ProcessId ?? service?.ProcessId ?? 0, processes);
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
            StatusText = restarting ? "Console window (restarting)" : interactive ? "Console window" : serviceInstalled ? service!.State : "No service",
            ActivityText = running ? (busy ? "Busy" : "Idle") : restarting ? "Restarting" : "Offline",
            IsInteractive = interactive,
            ConsoleRestarting = restarting,
            MovedAway = File.Exists(Path.Combine(root, MovedAwayMarker)),
            ManualStartBeforeMove = WasManualStartBeforeMove(root),
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
            RunnerGroup = config.RunnerGroup,
            Ephemeral = config.Ephemeral,
            DisableUpdate = config.DisableUpdate,
            NoDefaultLabels = remote is { HasDefaultLabels: false },
            ServiceInstalled = serviceInstalled,
            StartsAt = serviceInstalled && !interactive ? ServiceStartsAt(service!.StartMode) : ConsoleStartsAt(root, startupEntries, allRoots),
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
        try { return ParseRunnerConfig(File.ReadAllText(file)); }
        catch { return new RunnerConfig(); }
    }

    /// <summary>
    /// The settings config.cmd saved in .runner. poolName is the runner group; ephemeral and disableUpdate are only
    /// written when they were set.
    /// </summary>
    internal static RunnerConfig ParseRunnerConfig(string json)
    {
        using var document = JsonDocument.Parse(json.TrimStart('﻿'));
        var e = document.RootElement;
        return new RunnerConfig
        {
            AgentId = e.TryGetProperty("agentId", out var id) ? id.GetInt64() : 0,
            AgentName = e.TryGetProperty("agentName", out var name) ? name.GetString() ?? "Runner" : "Runner",
            GitHubUrl = e.TryGetProperty("gitHubUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            WorkFolder = e.TryGetProperty("workFolder", out var work) ? work.GetString() ?? "_work" : "_work",
            RunnerGroup = e.TryGetProperty("poolName", out var pool) && pool.ValueKind == JsonValueKind.String ? pool.GetString() ?? string.Empty : string.Empty,
            Ephemeral = e.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == JsonValueKind.True,
            DisableUpdate = e.TryGetProperty("disableUpdate", out var disableUpdate) && disableUpdate.ValueKind == JsonValueKind.True
        };
    }

    private static List<RunnerServiceRecord> ReadRunnerServices()
    {
        var result = new List<RunnerServiceRecord>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, State, StartMode, PathName, ProcessId FROM Win32_Service WHERE Name LIKE 'actions.runner.%'");
        foreach (ManagementObject service in searcher.Get())
        {
            var pathName = service["PathName"]?.ToString() ?? string.Empty;
            result.Add(new RunnerServiceRecord
            {
                Name = service["Name"]?.ToString() ?? string.Empty,
                DisplayName = service["DisplayName"]?.ToString() ?? string.Empty,
                State = service["State"]?.ToString() ?? "Unknown",
                StartMode = service["StartMode"]?.ToString() ?? string.Empty,
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
            "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, SessionId FROM Win32_Process WHERE Name LIKE 'Runner%'");
        foreach (ManagementObject process in searcher.Get())
        {
            result.Add(new ProcessInfo
            {
                ProcessId = ConvertToUInt(process["ProcessId"]),
                ParentProcessId = ConvertToUInt(process["ParentProcessId"]),
                Name = process["Name"]?.ToString() ?? string.Empty,
                ExecutablePath = process["ExecutablePath"]?.ToString() ?? string.Empty,
                SessionId = ConvertToUInt(process["SessionId"])
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
        if (NetworkFolderProblem(setup.InstallPath) is { } networkProblem) throw new InvalidOperationException(networkProblem);
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
                "_work", setup.Labels, runAsService: true, replace: setup.ReplaceExisting, setup.Options);
        }
        catch
        {
            // Leave the folder as it was, so trying again with the same folder works.
            await UndoFailedRegistrationAsync(setup, scope, log);
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

        var current = await ReadRegistrationBeforeRemovalAsync(runner);
        // Check everything before config remove, so an invalid value or a taken name cannot leave the runner unregistered.
        EnsureValidConfigInput(current.Url, current.Name, current.Labels);
        await EnsureNameIsFreeAsync(runner, current.Name);
        EnsureNotBusyNow(runner);
        await DetachConsoleRunnerAsync(runner, log);
        await RegisterAgainAsync(runner, current, current, log);
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
        // The current name, labels and settings are read before config remove: blank labels mean "keep the current
        // ones", and a failed registration is rolled back to them.
        var current = await ReadRegistrationBeforeRemovalAsync(local);
        var wanted = current with { Name = newName.Trim(), Labels = labels.Length == 0 ? current.Labels : labels };
        EnsureValidConfigInput(wanted.Url, wanted.Name, wanted.Labels);
        await EnsureNameIsFreeAsync(local, wanted.Name);
        var wasRunning = local.IsRunning;
        var restoreRecovery = HoldAutoRecovery(local.Name);
        try
        {
            // A job may have started while GitHub was read.
            EnsureNotBusyNow(local);
            if (local.IsInteractive) await DetachConsoleRunnerAsync(local, log);
            else if (wasRunning) await ControlServiceAsync(local.Name, "Stop");
            await RegisterAgainAsync(local, wanted, current, log);
        }
        finally { restoreRecovery(); }

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
        if (deleteFolder) EnsureFilesCanBeDeleted(runner);
        if (string.IsNullOrWhiteSpace(runner.ApiScope))
        {
            log(UnknownScopeMessage(runner.GitHubUrl));
            await RemoveLocallyAsync(runner, deleteFolder, log);
            return;
        }
        await EnsureAuthenticatedAsync();

        if (IsInUseElsewhere(runner))
        {
            // config remove here would unregister the copy that uses the registration now (after a move, the old copy
            // keeps the same runner ID).
            log("This is the old copy of a runner that was moved to another PC, which uses the same registration now, so it " +
                "stays registered. Removing this copy from this PC only.");
            await RemoveLocallyAsync(runner, deleteFolder, log);
            return;
        }

        var wasRunning = runner.ServiceInstalled && runner.IsRunning && !runner.IsInteractive;
        var restoreRecovery = HoldAutoRecovery(runner.Name);
        try
        {
            if (wasRunning) await ControlServiceAsync(runner.Name, "Stop");
            var startup = await DetachConsoleRunnerAsync(runner, log);
            try
            {
                log("Requesting a removal token…");
                var token = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: true);
                await RunConfigRemoveAsync(runner.InstallPath, token);
            }
            catch (Exception ex) when (IsGoneFromGitHub(ex))
            {
                // The repository (or the runner registration) was deleted on GitHub, or the account lost access to it,
                // so there's nothing left to unregister. Clean up the service and files here instead of failing.
                log($"GitHub reports that the runner's repository or organization does not exist, or that you no longer have access to it " +
                    $"({FirstLine(ex.Message)}). Removing the runner from this PC only. If it still exists, remove the runner in its settings on GitHub.");
                await RemoveLocallyAsync(runner, deleteFolder, log, detach: false);
                return;
            }
            catch
            {
                // Leave a working runner behind: start it again and put its startup entries back.
                log("Removing the runner failed. Restoring it…");
                await startup.RestoreAsync(log);
                if (runner.IsInteractive) log("The runner's console window was closed. It starts again at the next sign-in, or start it with run.cmd.");
                if (wasRunning)
                {
                    try { await ControlServiceAsync(runner.Name, "Start"); }
                    catch (Exception startError) { log($"Could not start the runner service again: {startError.Message}"); }
                }
                throw;
            }
        }
        finally { restoreRecovery(); }
        if (deleteFolder) await Task.Run(() => DeleteRunnerFiles(runner, log));
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

    /// <summary>What config.cmd needs to register a runner: used for the new values and to roll back to the old ones.</summary>
    private sealed record Registration(string Url, string Name, string[] Labels, RunnerRegistrationOptions Options);

    /// <summary>
    /// The runner's current registration: custom labels from GitHub (read before config remove so that registering it
    /// again keeps them), and its group and other settings from .runner. Stops when GitHub cannot be read (timeout, rate
    /// limit); continues only when GitHub confirms the runner is gone.
    /// </summary>
    private static async Task<Registration> ReadRegistrationBeforeRemovalAsync(RunnerInfo runner)
    {
        var labels = runner.CustomLabels;
        var noDefaultLabels = runner.NoDefaultLabels;
        if (IsInUseElsewhere(runner)) throw new InvalidOperationException(InUseElsewhereMessage);
        // Without a runner ID there is no registration on GitHub to read labels from.
        if (runner.AgentId > 0)
        {
            var lookup = await GitHubCli.LookUpRunnerAsync(runner.ApiScope, runner.AgentId);
            if (lookup.State != null)
            {
                labels = lookup.State.Labels;
                noDefaultLabels = !lookup.State.HasDefaultLabels;
            }
            else if (!lookup.NotFound)
                throw new InvalidOperationException("Could not read the runner's labels from GitHub. Try again.");
        }
        return new Registration(NormalizeRegistrationUrl(runner.GitHubUrl), runner.AgentName, labels,
            runner.RegistrationOptions with { NoDefaultLabels = noDefaultLabels });
    }

    private const string InUseElsewhereMessage = "This is the old copy of a runner that was moved to another PC, which uses the " +
        "same registration now. Delete this copy instead.";

    /// <summary>
    /// True for the old copy of a runner that was moved to another PC (marked when the move stopped it). config --replace
    /// keeps the runner ID, so that copy still has the ID of the registration the new copy uses. GitHub's online status
    /// can't tell this: a runner that was just stopped or crashed shows online for minutes, and the new copy shows
    /// offline while its PC is off.
    /// </summary>
    internal static bool IsInUseElsewhere(RunnerInfo runner) => runner.MovedAway && !runner.IsRunning;

    /// <summary>File in a runner's folder that marks it as the old copy of a moved runner.</summary>
    internal const string MovedAwayMarker = ".moved-away";

    /// <summary>
    /// Marks a runner as moving away (its registration goes to another PC), or clears the mark when the move failed.
    /// </summary>
    /// <param name="manualStart">The runner was set not to start with Windows, which a failed move puts back.</param>
    public static async Task MarkMovedAwayAsync(string key, bool movedAway, bool manualStart = false)
    {
        var runner = await FindRunnerAsync(key);
        if (runner == null || string.IsNullOrEmpty(runner.InstallPath)) return;
        var marker = Path.Combine(runner.InstallPath, MovedAwayMarker);
        if (movedAway)
            await File.WriteAllTextAsync(marker, $"Moved to another PC on {DateTime.UtcNow:O}. Its registration belongs to that PC now."
                                                 + (manualStart ? $"{Environment.NewLine}{ManualStartNote}" : string.Empty));
        else TryDelete(marker);
    }

    private const string ManualStartNote = "start=manual";

    /// <summary>Whether the old copy of a moved runner was set not to start with Windows before the move.</summary>
    private static bool WasManualStartBeforeMove(string root)
    {
        var marker = Path.Combine(root, MovedAwayMarker);
        try { return File.Exists(marker) && File.ReadAllLines(marker).Any(l => l.Trim() == ManualStartNote); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Refuses a name that another runner in the repository or organization already has. Checked before config remove:
    /// registering with a taken name fails only after the old registration is gone.
    /// </summary>
    private static async Task EnsureNameIsFreeAsync(RunnerInfo runner, string name)
    {
        var ids = await GitHubCli.FindRunnerIdsByNameAsync(runner.ApiScope, name)
                  ?? throw new InvalidOperationException($"Could not check on GitHub whether the name {name} is free. Try again.");
        if (ids.Any(id => id != runner.AgentId))
            throw new InvalidOperationException($"Another runner named {name} is already registered for {RepositoryName(runner.GitHubUrl)}. Choose a different name.");
    }

    /// <summary>
    /// Unregisters the runner and registers it again with new values. If registering fails after config remove, the
    /// runner is registered again with its old values so it doesn't disappear, and both errors are reported.
    /// </summary>
    private static async Task RegisterAgainAsync(RunnerInfo runner, Registration wanted, Registration current, Action<string> log)
    {
        log("Requesting a removal token…");
        var removeToken = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: true);
        await RunConfigRemoveAsync(runner.InstallPath, removeToken);
        try
        {
            await RegisterAsync(runner, wanted, log);
        }
        catch (Exception error)
        {
            log($"Registering failed: {FirstLine(error.Message)} Registering the runner again with its old name and labels…");
            try
            {
                // A half-finished config.cmd run can leave .runner behind, and config.cmd refuses to configure over it.
                if (File.Exists(Path.Combine(runner.InstallPath, ".runner")))
                    await RunConfigRemoveAsync(runner.InstallPath, await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: true));
                await RegisterAsync(runner, current, log);
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException(
                    $"{error.Message}\nRegistering the runner again with its old name and labels also failed: {rollback.Message}");
            }
            throw new InvalidOperationException($"{error.Message}\nThe runner was registered again with its old name and labels.");
        }
    }

    private static async Task RegisterAsync(RunnerInfo runner, Registration registration, Action<string> log)
    {
        log("Requesting a registration token…");
        var token = await GitHubCli.GetRunnerTokenAsync(runner.ApiScope, removal: false);
        log("Configuring the runner as a Windows service…");
        await ConfigureAsync(runner.InstallPath, registration.Url, token, registration.Name, runner.WorkFolder,
            registration.Labels, runAsService: true, options: registration.Options);
    }

    /// <summary>
    /// Checks again right before a stop that no job started since the runner list was read: a job's worker process runs
    /// from the runner's bin folder.
    /// </summary>
    private static void EnsureNotBusyNow(RunnerInfo runner)
    {
        if (ReadProcesses().Any(p => p.Name.Equals("Runner.Worker.exe", StringComparison.OrdinalIgnoreCase)
                                     && RootFromExecutable(p.ExecutablePath).Equals(runner.InstallPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The runner just started a job. Wait for it to finish and try again.");
    }

    /// <summary>
    /// Stops a runner only when it is idle, checked right before stopping. For callers that stop a runner to change it
    /// (such as a move), so that a job that started a moment ago is not cut off.
    /// </summary>
    public static async Task StopIfIdleAsync(string key)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        EnsureNotBusy(runner);
        EnsureNotBusyNow(runner);
        await ControlServiceAsync(runner.CommandKey, "Stop");
        // A runner with a service that runs from a console window instead: stopping the service doesn't stop it.
        if (runner.ServiceInstalled && runner.IsInteractive) await Task.Run(() => StopConsoleRunner(runner.InstallPath));
    }

    /// <summary>
    /// Marks the runner as stopped on purpose while an operation has it stopped, so the fleet agent's auto-recovery (a
    /// separate process that checks every few seconds) doesn't start it again halfway. Returns how to restore the mark.
    /// </summary>
    private static Action HoldAutoRecovery(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return () => { };
        bool was;
        try
        {
            was = FleetConfig.Current.ManuallyStopped.Contains(serviceName, StringComparer.OrdinalIgnoreCase);
            FleetConfig.SetManuallyStopped(serviceName, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return () => { }; }
        return () =>
        {
            try { FleetConfig.SetManuallyStopped(serviceName, was); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }

    /// <summary>
    /// GitHub answers 404 when the repository, organization or runner registration no longer exists, and also for a
    /// private repository the account lost access to. Only GitHub's own answers count: the app's errors (such as a
    /// missing config.cmd) and a missing permission must not make a removal skip GitHub.
    /// </summary>
    internal static bool IsGoneFromGitHub(Exception error)
        => error is not (FileNotFoundException or DirectoryNotFoundException or GitHubPermissionException)
           && IsGoneFromGitHub(error.Message);

    /// <summary>gh ("gh: Not Found (HTTP 404)", "HTTP 404: Not Found (...)") and config.cmd ("Http response code: NotFound") 404 output.</summary>
    internal static bool IsGoneFromGitHub(string message)
        => Regex.IsMatch(message, @"\bHTTP 404\b|\(HTTP 404\)|\b404 \(Not Found\)|response code: NotFound\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
           || message.Contains("Could not resolve to a Repository", StringComparison.OrdinalIgnoreCase)
           || Regex.IsMatch(message, @"\brepository (does not|doesn't) exist\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What a removal logs when the runner's registration URL is not a github.com repository or organization.</summary>
    internal static string UnknownScopeMessage(string registrationUrl)
    {
        var host = Uri.TryCreate(registrationUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        return host.Length > 0 && !IsGitHubHost(host)
            ? $"The runner is registered on {host}. Runner Manager only works with github.com, so its registration there is left in place: " +
              $"remove it in the runner settings on {host}. Removing the runner from this PC only."
            : "The runner repository is unknown, so the runner cannot be removed from GitHub. Removing it from this PC only.";
    }

    private static bool IsGitHubHost(string host)
        => host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;

    /// <summary>Removes a runner on this PC by service name or install folder (used for remote removal).</summary>
    public static async Task RemoveRunnerAsync(string key, bool deleteFolder, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        EnsureNotBusy(runner);
        if (runner.IsRunning) EnsureNotBusyNow(runner);
        await RemoveRunnerAsync(runner, deleteFolder, log);
    }

    /// <summary>
    /// Cleans up the old copy after a runner was moved: its GitHub registration was already replaced by the new
    /// PC (config --replace), so only the local service, console autostart and files are removed here.
    /// </summary>
    public static async Task RetireMovedRunnerAsync(string key, bool deleteFolder, Action<string> log)
    {
        var runner = await FindRunnerAsync(key) ?? throw new InvalidOperationException("The runner was not found on this PC.");
        if (deleteFolder) EnsureFilesCanBeDeleted(runner);
        await RemoveLocallyAsync(runner, deleteFolder, log);
    }

    /// <summary>Removes a runner from this PC without contacting GitHub: service, console autostart and files.</summary>
    private static async Task RemoveLocallyAsync(RunnerInfo runner, bool deleteFolder, Action<string> log, bool detach = true)
    {
        if (detach) await DetachConsoleRunnerAsync(runner, log);
        if (runner.ServiceInstalled)
        {
            if (runner.IsRunning) await ControlServiceAsync(runner.Name, "Stop");
            log("Removing the Windows service…");
            var (code, output) = await ScheduledTasks.RunAsync("sc.exe", $"delete \"{runner.Name}\"", ScheduledTasks.ShortTimeout);
            if (code != 0) throw new InvalidOperationException($"Could not remove the Windows service {runner.Name}: {output.Trim()}");
        }
        if (deleteFolder) await Task.Run(() => DeleteRunnerFiles(runner, log));
        else
        {
            // Keep the files but drop the stale registration so the folder isn't listed as a runner any more.
            foreach (var file in new[] { ".runner", ".credentials", ".credentials_rsaparams", ".service", MovedAwayMarker })
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
        // A runner set not to start keeps that as a service (RegistrationOptions.ManualStart); its turned-off startup
        // entries would start the console again, so they are not put back later.
        await RepairRunnerAsync(runner, log);
        ForgetStartupOff(runner.InstallPath);
    }

    /// <summary>
    /// For runners started from a console: stops the console (and any restart loop around it) and disables what starts
    /// it at sign-in, so it doesn't run twice: startup scripts and shortcuts are moved into
    /// %LOCALAPPDATA%\RunnerManager\disabled-startup, scheduled tasks are disabled, and Run registry values are moved
    /// aside (saved in that folder). Returns the changes, so a failed operation can put them back.
    /// </summary>
    private static async Task<StartupChanges> DetachConsoleRunnerAsync(RunnerInfo runner, Action<string> log)
    {
        if (runner.IsInteractive)
        {
            log("Closing the runner console window…");
            await Task.Run(() => StopConsoleRunner(runner.InstallPath));
        }
        var changes = new StartupChanges();
        var others = await Task.Run(() => OtherRunnerRoots(runner.InstallPath));
        await Task.Run(() => DisableStartupFiles(runner.InstallPath, others, changes, log));
        await DisableScheduledTasksAsync(runner.InstallPath, others, changes, log);
        await Task.Run(() => DisableRunValues(runner.InstallPath, others, changes, log));
        return changes;
    }

    /// <summary>Startup entries that were disabled for a runner, and how to enable them again.</summary>
    internal sealed class StartupChanges
    {
        public List<(string Original, string Moved)> Files { get; } = new();
        public List<string> Tasks { get; } = new();
        public List<(RegistryKey Hive, string Key, string Name, object Value, RegistryValueKind Kind, string SavedTo)> RunValues { get; } = new();

        public bool IsEmpty => Files.Count == 0 && Tasks.Count == 0 && RunValues.Count == 0;

        /// <summary>Puts the entries back. Returns the ones that could not be restored (each is logged); empty when all were.</summary>
        public async Task<StartupChanges> RestoreAsync(Action<string> log)
        {
            var failed = new StartupChanges();
            foreach (var (original, moved) in Files)
            {
                // The entry is already back (created again by hand): the runner starts, so it counts as restored.
                if (File.Exists(original))
                {
                    log(File.Exists(moved)
                        ? $"The startup entry {Path.GetFileName(original)} already exists again. The turned-off copy stays in {Path.GetDirectoryName(moved)}."
                        : $"The startup entry {Path.GetFileName(original)} is already back.");
                    continue;
                }
                try
                {
                    File.Move(moved, original);
                    log($"Restored the startup entry {Path.GetFileName(original)}.");
                }
                catch (Exception ex)
                {
                    log($"Could not restore the startup entry {original} from {moved}: {ex.Message}");
                    failed.Files.Add((original, moved));
                }
            }
            foreach (var task in Tasks)
            {
                var (code, output) = await ScheduledTasks.RunAsync("schtasks.exe", $"/Change /TN \"{task}\" /ENABLE", ScheduledTasks.ShortTimeout);
                log(code == 0 ? $"Enabled the scheduled task {task} again." : $"Could not enable the scheduled task {task} again: {output.Trim()}");
                if (code != 0) failed.Tasks.Add(task);
            }
            foreach (var value in RunValues)
            {
                try
                {
                    using var key = value.Hive.CreateSubKey(value.Key, writable: true);
                    key.SetValue(value.Name, value.Value, value.Kind);
                    TryDelete(value.SavedTo);
                    log($"Restored the startup entry {value.Name} in {value.Hive.Name}\\{value.Key}.");
                }
                catch (Exception ex)
                {
                    log($"Could not restore the startup entry {value.Name} (saved in {value.SavedTo}): {ex.Message}");
                    failed.RunValues.Add(value);
                }
            }
            return failed;
        }

        public void Add(StartupChanges other)
        {
            Files.AddRange(other.Files);
            Tasks.AddRange(other.Tasks);
            RunValues.AddRange(other.RunValues);
        }
    }

    private static string DisabledStartupFolder => Path.Combine(FleetConfig.StateDir, "disabled-startup");

    /// <summary>Folders of the other runners on this PC, so a startup entry that launches several runners can be told apart.</summary>
    private static List<string> OtherRunnerRoots(string root)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var service in ReadRunnerServices()) roots.Add(service.Root); } catch { }
        foreach (var folder in new[] { RunnersRoot, DefaultRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(folder))
                    if (File.Exists(Path.Combine(dir, ".runner"))) roots.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var known in FleetConfig.Current.KnownRunnerRoots) roots.Add(known);
        // A runner in a folder inside this one (D:\Runners\b inside D:\Runners) is another runner even when it never ran.
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(dir, ".runner"))) roots.Add(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        roots.RemoveWhere(r => string.IsNullOrWhiteSpace(r) || r.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        return roots.ToList();
    }

    internal enum StartupMatch { None, ThisRunner, SharedWithOtherRunners }

    /// <summary>
    /// Whether a startup entry launches the runner in this folder, and whether it launches other runners too. The longest
    /// folders are matched first, so D:\Runners\b inside D:\Runners does not count as D:\Runners, and the other way round.
    /// </summary>
    internal static StartupMatch MatchStartupEntry(string content, string root, IEnumerable<string> otherRoots)
    {
        var self = root.TrimEnd('\\', '/');
        if (self.Length == 0) return StartupMatch.None;
        var text = content;
        bool matchesSelf = false, matchesOther = false;
        foreach (var folder in otherRoots.Select(r => r.TrimEnd('\\', '/')).Where(r => r.Length > 0).Append(self)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(r => r.Length))
        {
            var pattern = new Regex(Regex.Escape(folder) + @"(?=[\\/""'\s\0]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!pattern.IsMatch(text)) continue;
            if (folder.Equals(self, StringComparison.OrdinalIgnoreCase)) matchesSelf = true;
            else matchesOther = true;
            text = pattern.Replace(text, "\u0001");
        }
        return !matchesSelf ? StartupMatch.None : matchesOther ? StartupMatch.SharedWithOtherRunners : StartupMatch.ThisRunner;
    }

    /// <summary>Startup-folder scripts and shortcuts that launch the runner in this folder, moved aside (reversible).</summary>
    private static void DisableStartupFiles(string root, List<string> others, StartupChanges changes, Action<string> log, string? into = null)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };
        var target = into ?? DisabledStartupFolder;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".cmd" or ".bat" or ".ps1" or ".vbs" or ".lnk")) continue;
                string content;
                try { content = ReadStartupText(File.ReadAllBytes(file), extension == ".lnk"); }
                catch { continue; }
                var match = MatchStartupEntry(content, root, others);
                if (match == StartupMatch.None) continue;
                if (match == StartupMatch.SharedWithOtherRunners)
                {
                    log($"The startup entry {file} also starts other runners, so it was left in place. Remove this runner from it by hand.");
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(target);
                    var destination = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(destination)) destination = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
                    File.Move(file, destination);
                    changes.Files.Add((file, destination));
                    log($"Disabled the startup entry {Path.GetFileName(file)}. It was moved to {target}.");
                }
                catch (Exception ex) { log($"Could not disable the startup entry {file}: {ex.Message}"); }
            }
        }
    }

    /// <summary>Lists scheduled tasks outside \Microsoft\ that are enabled: one line per task, its path and its actions.</summary>
    private const string ListTasksScript =
        "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and $_.State -ne 'Disabled' } | ForEach-Object { " +
        "$a = ($_.Actions | ForEach-Object { \"$($_.Execute) $($_.Arguments) $($_.WorkingDirectory)\" }) -join ' '; " +
        "\"$($_.TaskPath)$($_.TaskName)`t$a\" }";

    /// <summary>Task names and their action text from <see cref="ListTasksScript"/> output.</summary>
    internal static List<(string Name, string Actions)> ParseTaskList(string output)
        => output.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[0].StartsWith('\\'))
            .Select(parts => (parts[0].Trim(), parts[1].Trim()))
            .ToList();

    /// <summary>Scheduled tasks whose actions run the runner in this folder are disabled (and enabled again on undo).</summary>
    private static async Task DisableScheduledTasksAsync(string root, List<string> others, StartupChanges changes, Action<string> log)
    {
        var script = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ListTasksScript));
        var (code, output) = await ScheduledTasks.RunAsync("powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {script}", ScheduledTasks.ShortTimeout);
        if (code != 0)
        {
            log("Could not check scheduled tasks for ones that start this runner.");
            return;
        }
        foreach (var (name, actions) in ParseTaskList(output))
        {
            var match = MatchStartupEntry(actions, root, others);
            if (match == StartupMatch.None || name.Contains('"')) continue;
            if (match == StartupMatch.SharedWithOtherRunners)
            {
                log($"The scheduled task {name} also starts other runners, so it was left enabled. Remove this runner from it by hand.");
                continue;
            }
            var (changeCode, changeOutput) = await ScheduledTasks.RunAsync("schtasks.exe", $"/Change /TN \"{name}\" /DISABLE", ScheduledTasks.ShortTimeout);
            if (changeCode == 0)
            {
                changes.Tasks.Add(name);
                log($"Disabled the scheduled task {name}, which started this runner.");
            }
            else log($"Could not disable the scheduled task {name}, which starts this runner: {changeOutput.Trim()}");
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Run registry values that start the runner in this folder are deleted after saving them in a file in the
    /// disabled-startup folder, so they can be put back.
    /// </summary>
    private static void DisableRunValues(string root, List<string> others, StartupChanges changes, Action<string> log, string? into = null)
    {
        var target = into ?? DisabledStartupFolder;
        foreach (var (hive, path) in new[] { (Registry.CurrentUser, RunKey), (Registry.LocalMachine, RunKey), (Registry.LocalMachine, RunKey32) })
        {
            try
            {
                // Opened for writing only when something matches: without administrator rights HKLM is read-only.
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value) continue;
                    var match = MatchStartupEntry(value.ToString() ?? string.Empty, root, others);
                    if (match == StartupMatch.None) continue;
                    var where = $"{hive.Name}\\{path}";
                    if (match == StartupMatch.SharedWithOtherRunners)
                    {
                        log($"The startup entry {name} in {where} also starts other runners, so it was left in place. Remove this runner from it by hand.");
                        continue;
                    }
                    var kind = key.GetValueKind(name);
                    Directory.CreateDirectory(target);
                    var saved = Path.Combine(target, $"run-{string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c))}-{DateTime.Now:yyyyMMddHHmmss}.json");
                    File.WriteAllText(saved, JsonSerializer.Serialize(new { key = where, name, value = value.ToString(), kind = kind.ToString() },
                        new JsonSerializerOptions { WriteIndented = true }));
                    using (var writable = hive.OpenSubKey(path, writable: true)) writable?.DeleteValue(name);
                    changes.RunValues.Add((hive, path, name, value, kind, saved));
                    log($"Disabled the startup entry {name} in {where}. Its command was saved to {saved}.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log($"Could not check {hive.Name}\\{path} for entries that start this runner: {ex.Message}");
            }
        }
    }

    private static void StopConsoleRunner(string root)
    {
        // Stopped on purpose, so it is not shown as restarting.
        ConsoleSeenUtc.TryRemove(root, out _);
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

        // A restart loop around run.cmd (such as run-in-session.cmd) keeps running while the listener is down, for example
        // while the runner updates itself, and would start it again later. End those too, but not the loops of a runner
        // in a folder inside this one.
        var others = OtherRunnerRoots(root);
        var loops = all.Where(p => p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
                                   && MatchStartupEntry(p.CommandLine, root, others) == StartupMatch.ThisRunner).ToList();
        foreach (var loop in loops.Where(p => !loops.Any(other => other.Pid == p.Parent)))
        {
            try
            {
                using var process = Process.GetProcessById((int)loop.Pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static readonly System.Text.Encoding StrictUtf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Text to search for the runner folder. Shortcuts store paths as UTF-16 and as ANSI, so both decodings are
    /// searched; UTF-16 strings can start at an odd offset, so it is decoded from both. Scripts are read as UTF-8 when
    /// they are valid UTF-8, and as Latin-1 otherwise (any byte decodes).
    /// </summary>
    internal static string ReadStartupText(byte[] bytes, bool shortcut)
    {
        if (shortcut)
            return System.Text.Encoding.Unicode.GetString(bytes) + "\0"
                   + (bytes.Length > 1 ? System.Text.Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) : string.Empty) + "\0"
                   + System.Text.Encoding.Latin1.GetString(bytes);
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
        ForgetStartupOff(root);
        if (FleetConfig.Current.KnownRunnerRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            FleetConfig.Update(c => c.KnownRunnerRoots.RemoveAll(r => r.Equals(root, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The files and folders a runner install consists of: the actions/runner package, what config.cmd and the runner
    /// write next to it (settings, logs, work folder, self-update copies), and the package zip.
    /// </summary>
    private static readonly string[] RunnerItemPatterns =
    {
        "bin", "bin.*", "externals", "externals.*", "_diag", "_work", "_update",
        ".runner", ".runner_migrated", ".credentials*", ".service", ".env", ".path", ".setup_info", MovedAwayMarker,
        "config.cmd", "config.sh", "run.cmd", "run.sh", "run-helper.*", "svc.*", "safe_sleep.*", "env.sh", "runsvc.sh",
        "actions-runner-*.zip"
    };

    /// <summary>True when a top-level entry of a runner folder belongs to the runner install.</summary>
    internal static bool IsRunnerItem(string name, string workFolder = "_work")
    {
        var work = string.IsNullOrWhiteSpace(workFolder) || Path.IsPathRooted(workFolder) ? "_work"
            : workFolder.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "_work";
        return name.Equals(work, StringComparison.OrdinalIgnoreCase)
               || RunnerItemPatterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }

    internal enum FolderDeletion { Refuse, Whole, RunnerItemsOnly }

    /// <param name="Reason">Why the folder is refused or kept; empty when it can be deleted whole.</param>
    internal sealed record FolderDeletionPlan(FolderDeletion Kind, string Reason, IReadOnlyList<string> OtherItems);

    /// <summary>
    /// How to delete a runner's files. The folder comes from the runner's service or listener path, so it can be a drive
    /// root, a profile folder, or a folder that holds other runners and files. Those are never deleted whole: drive roots
    /// are refused, and elsewhere only the runner's own items are deleted when anything else is there.
    /// </summary>
    internal static FolderDeletionPlan PlanFolderDeletion(string root, string workFolder = "_work")
    {
        var full = Path.GetFullPath(root).TrimEnd('\\');
        if (IsDriveRoot(full))
            return new FolderDeletionPlan(FolderDeletion.Refuse, $"{full}\\ is a drive root", Array.Empty<string>());
        var others = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
                if (!IsRunnerItem(entry.Name, workFolder)) others.Add(entry.Name);
        }
        catch (DirectoryNotFoundException) { }
        if (IsProtectedFolder(full))
            return new FolderDeletionPlan(FolderDeletion.RunnerItemsOnly, $"{full} is a Windows or user folder", others);
        if (others.Count == 0) return new FolderDeletionPlan(FolderDeletion.Whole, string.Empty, others);
        var runners = others.Where(o => File.Exists(Path.Combine(full, o, ".runner"))).ToList();
        var reason = runners.Count > 0
            ? $"{full} also holds other runners ({string.Join(", ", runners.Take(3))})"
            : $"{full} also holds other files ({string.Join(", ", others.Take(3))}{(others.Count > 3 ? ", …" : string.Empty)})";
        return new FolderDeletionPlan(FolderDeletion.RunnerItemsOnly, reason, others);
    }

    private static bool IsDriveRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? string.Empty;
        return root.Length == 0 || root.Equals(fullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Windows, Program Files, profile folders and their parents (such as C:\Users).</summary>
    private static bool IsProtectedFolder(string fullPath)
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData
        };
        var path = fullPath.TrimEnd('\\') + "\\";
        return folders.Select(Environment.GetFolderPath).Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f.TrimEnd('\\') + "\\")
            .Any(f => f.StartsWith(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Throws when the runner's files must not be deleted at all (a drive root), before anything is changed.</summary>
    private static void EnsureFilesCanBeDeleted(RunnerInfo runner)
    {
        if (!Directory.Exists(runner.InstallPath)) return;
        var plan = PlanFolderDeletion(runner.InstallPath, runner.WorkFolder);
        if (plan.Kind == FolderDeletion.Refuse)
            throw new InvalidOperationException($"The runner's files cannot be deleted: {plan.Reason}. Delete the runner and keep its files instead.");
    }

    /// <summary>Deletes a runner's files as planned by <see cref="PlanFolderDeletion"/>.</summary>
    private static void DeleteRunnerFiles(RunnerInfo runner, Action<string> log)
    {
        if (!Directory.Exists(runner.InstallPath)) return;
        var plan = PlanFolderDeletion(runner.InstallPath, runner.WorkFolder);
        if (plan.Kind == FolderDeletion.Refuse) throw new InvalidOperationException($"The runner's files were not deleted: {plan.Reason}.");
        if (plan.Kind == FolderDeletion.Whole)
        {
            log("Deleting the runner folder…");
            DeleteFolder(runner.InstallPath);
            return;
        }
        log($"Deleting only the runner's own files, because {plan.Reason}…");
        DeleteRunnerItems(runner.InstallPath, runner.WorkFolder);
    }

    /// <summary>Deletes the runner's own items in a folder and keeps everything else.</summary>
    internal static void DeleteRunnerItems(string root, string workFolder = "_work")
    {
        var failed = 0;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos().Where(e => IsRunnerItem(e.Name, workFolder)))
            failed += DeleteEntry(entry);
        if (failed > 0) throw new IOException($"{failed} item(s) in {root} were in use and were not deleted.");
    }

    /// <summary>Deletes a folder and everything in it. Junctions and symbolic links inside are removed, not followed.</summary>
    internal static void DeleteFolder(string path)
    {
        var folder = new DirectoryInfo(path);
        if (!folder.Exists) return;
        var failed = 0;
        foreach (var entry in folder.EnumerateFileSystemInfos()) failed += DeleteEntry(entry);
        if (failed > 0) throw new IOException($"{failed} item(s) in {path} were in use and were not deleted.");
        folder.Attributes &= ~FileAttributes.ReadOnly;
        folder.Delete();
    }

    /// <summary>
    /// Deletes a file or folder tree and returns how many items could not be deleted. A junction or symbolic link is
    /// deleted itself; its target is never entered.
    /// </summary>
    internal static int DeleteEntry(FileSystemInfo entry)
    {
        try
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo link) Directory.Delete(link.FullName, recursive: false);
                else File.Delete(entry.FullName);
                return 0;
            }
            if (entry is DirectoryInfo directory)
            {
                var failed = 0;
                foreach (var child in directory.EnumerateFileSystemInfos()) failed += DeleteEntry(child);
                if (failed > 0) return failed;
                directory.Attributes &= ~FileAttributes.ReadOnly;
                directory.Delete();
                return 0;
            }
            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
    }

    /// <summary>
    /// After a failed install: config.cmd may have registered the runner, or created its Windows service, before it
    /// failed. The registration is removed, unless it replaced the registration of a runner being moved here (the old
    /// copy needs it), and a service that points into the folder is deleted.
    /// </summary>
    private static async Task UndoFailedRegistrationAsync(RunnerSetup setup, string scope, Action<string> log)
    {
        if (File.Exists(Path.Combine(setup.InstallPath, ".runner")) && File.Exists(Path.Combine(setup.InstallPath, "config.cmd")))
        {
            if (setup.ReplaceExisting)
                log("The runner registration is left in place, because it replaced the registration of the runner being moved.");
            else
            {
                try
                {
                    log("Removing the registration the failed install made…");
                    await RunConfigRemoveAsync(setup.InstallPath, await GitHubCli.GetRunnerTokenAsync(scope, removal: true));
                }
                catch (Exception ex)
                {
                    log($"Could not remove the registration ({FirstLine(ex.Message)}). Remove the runner {setup.RunnerName} in the runner settings on GitHub.");
                }
            }
        }
        List<RunnerServiceRecord> services;
        try { services = await Task.Run(ReadRunnerServices); }
        catch { return; }
        foreach (var service in services.Where(s => SameFolder(s.Root, setup.InstallPath)))
        {
            log($"Removing the Windows service {service.Name} the failed install created…");
            try { await ControlServiceAsync(service.Name, "Stop"); } catch { }
            var (code, output) = await ScheduledTasks.RunAsync("sc.exe", $"delete \"{service.Name}\"", ScheduledTasks.ShortTimeout);
            if (code != 0) log($"Could not remove the Windows service {service.Name}: {output.Trim()}");
        }
    }

    private static bool SameFolder(string a, string b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && Path.GetFullPath(a).TrimEnd('\\').Equals(Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

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
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos()) DeleteEntry(entry);
        }
        catch { }
    }

    /// <summary>
    /// The runner folder of a runner executable: the parent of its bin folder (or of bin.2.x.y, where a runner that
    /// updated itself can run from). Short 8.3 names (C:\GITHUB~1) are expanded, so a runner is not listed twice.
    /// </summary>
    internal static string RootFromExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;
        var directory = Path.GetDirectoryName(LongPath(executable));
        if (string.IsNullOrEmpty(directory)) return string.Empty;
        var info = new DirectoryInfo(directory);
        var isBin = info.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) || info.Name.StartsWith("bin.", StringComparison.OrdinalIgnoreCase);
        return isBin && info.Parent != null ? info.Parent.FullName : info.FullName;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint bufferLength);

    /// <summary>The path with 8.3 short names expanded, or the path unchanged when it has none or does not exist.</summary>
    internal static string LongPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains('~')) return path;
        var buffer = new System.Text.StringBuilder(260);
        var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        if (length > buffer.Capacity)
        {
            buffer = new System.Text.StringBuilder((int)length);
            length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        }
        return length == 0 || length > buffer.Capacity ? path : buffer.ToString();
    }

    /// <summary>
    /// A folder name for a repository or organization name. Windows drops trailing dots and spaces from folder names
    /// (a repository can be named "tools."), so they are removed here, or the folder would not be the one asked for.
    /// </summary>
    internal static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).TrimEnd('.', ' ').TrimStart(' ');
        return safe.Length == 0 ? "runner" : safe;
    }

    /// <summary>A free folder in the runner folder for a runner of this repository or organization.</summary>
    public static string SuggestInstallPath(string registrationUrl)
    {
        var name = RepositoryPath(registrationUrl).Split('/').LastOrDefault(s => s.Length > 0)
                   ?? OrganizationName(registrationUrl);
        var safe = SafeFolderName(name);
        var root = RunnersRoot;
        var path = Path.Combine(root, safe);
        for (var i = 2; Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); i++)
            path = Path.Combine(root, $"{safe}-{i}");
        return path;
    }

    private static async Task ConfigureAsync(string root, string repositoryUrl, string token, string name, string workFolder,
        IEnumerable<string> labels, bool runAsService, bool replace = false, RunnerRegistrationOptions? options = null)
    {
        repositoryUrl = await CurrentRepositoryUrlAsync(repositoryUrl);
        var arguments = BuildConfigureArguments(repositoryUrl, token, name, workFolder, labels, runAsService, replace, options);
        var result = await RunConfigAsync(root, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(CleanConfigError(result.Error, result.Output));
        // config.cmd sets the service to start with Windows; a runner that was set not to keeps that.
        // Only a setting: failing here must not undo a registration that worked.
        if (runAsService && options?.ManualStart == true)
        {
            try
            {
                if (ReadRunnerServices().FirstOrDefault(s => s.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) is { } service)
                    await SetServiceStartModeAsync(service.Name, ServiceStart.Manual);
            }
            catch (Exception ex) { MachineRelay.AppendAgentLog($"The runner in {root} was set not to start with Windows, but that could not be kept: {ex.Message}"); }
        }
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
        IEnumerable<string>? labels, bool runAsService, bool replace, RunnerRegistrationOptions? options = null)
    {
        options ??= RunnerRegistrationOptions.None;
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
        // Without --runnergroup, config.cmd uses the default group, so only another group needs passing.
        var group = options.RunnerGroup.Trim();
        if (group.Length > 0 && !group.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSafeValue(group, "runner group");
            args.Add("--runnergroup");
            args.Add(Quote(group));
        }
        if (options.NoDefaultLabels && labelList.Length > 0) args.Add("--no-default-labels");
        if (options.Ephemeral) args.Add("--ephemeral");
        if (options.DisableUpdate) args.Add("--disableupdate");
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
            : NetworkFolderProblem(path);
    }

    private const string NetworkFolderRule = "Choose a folder on a drive in this PC. Runners run as a Windows service, " +
        "and services cannot use network folders or mapped drives.";

    /// <summary>
    /// Null when the folder is on a drive in this PC. Services run without the user's mapped drives, and the service
    /// account usually cannot reach network shares, so UNC paths and mapped drives are refused.
    /// </summary>
    internal static string? NetworkFolderProblem(string path, Func<string, bool>? isNetworkDrive = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var text = path.Trim();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return NetworkFolderRule;
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal)) text = text[4..];
        else if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return NetworkFolderRule;
        if (text.Length < 2 || text[1] != ':' || !char.IsAsciiLetter(text[0])) return null;
        return (isNetworkDrive ?? IsNetworkDrive)($"{char.ToUpperInvariant(text[0])}:\\") ? NetworkFolderRule : null;
    }

    private static bool IsNetworkDrive(string root)
    {
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
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
        var prefix = $"actions-runner-win-{(IsArm64 ? "arm64" : "x64")}-";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip"))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? string.Empty : string.Empty;
            var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty;
            if (!string.IsNullOrWhiteSpace(url)) return new RunnerPackage(version, name, url, sha256);
        }
        throw new InvalidOperationException($"The latest GitHub Actions runner release has no Windows {ArchitectureLabel} package.");
    }

    private static bool IsArm64 => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                                   == System.Runtime.InteropServices.Architecture.Arm64;

    /// <summary>The architecture label runners on this PC get from GitHub: "ARM64" on ARM64 Windows, otherwise "X64".</summary>
    public static string ArchitectureLabel => IsArm64 ? "ARM64" : "X64";

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
        // www.github.com is the same site; NormalizeRegistrationUrl turns it into github.com for config.cmd.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsGitHubHost(uri.Host))
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsGitHubHost(uri.Host))
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
        var restoreRecovery = HoldAutoRecovery(runner.Name);
        try
        {
            if (wasRunning)
            {
                EnsureNotBusyNow(runner);
                log("Stopping the runner service…");
                await ControlServiceAsync(runner.Name, "Stop");
            }
            log($"Deleting the contents of {fullFolder}…");
            var failures = await Task.Run(() =>
            {
                // Links (for example a junction to a shared tool cache) are removed without touching their targets.
                var failed = 0;
                foreach (var entry in new DirectoryInfo(fullFolder).EnumerateFileSystemInfos())
                    if (DeleteEntry(entry) > 0) failed++;
                return failed;
            });
            SystemHealth.ForgetFolderSize(fullFolder);
            log(failures == 0 ? "Work folder cleaned." : $"Work folder cleaned. {failures} item(s) were in use and were not deleted.");
        }
        finally
        {
            try
            {
                if (wasRunning)
                {
                    log("Starting the runner service…");
                    await ControlServiceAsync(runner.Name, "Start");
                }
            }
            finally { restoreRecovery(); }
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
    /// <summary>Runner group, ephemeral and the other settings to keep (used when moving a runner to this PC).</summary>
    public RunnerRegistrationOptions Options { get; init; } = RunnerRegistrationOptions.None;
}

public sealed class RunnerConfig
{
    public long AgentId { get; init; }
    public string AgentName { get; init; } = "Runner";
    public string GitHubUrl { get; init; } = string.Empty;
    public string WorkFolder { get; init; } = "_work";
    /// <summary>The runner group (poolName in .runner); "Default" for repository runners.</summary>
    public string RunnerGroup { get; init; } = string.Empty;
    public bool Ephemeral { get; init; }
    public bool DisableUpdate { get; init; }
}

/// <summary>
/// Registration settings besides the name and labels, which config.cmd needs again when a runner is registered again
/// (edit, repair, convert, move).
/// </summary>
public sealed record RunnerRegistrationOptions(string RunnerGroup = "", bool Ephemeral = false, bool DisableUpdate = false,
    bool NoDefaultLabels = false, bool ManualStart = false)
{
    public static readonly RunnerRegistrationOptions None = new();
}

public sealed class RunnerServiceRecord
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    /// <summary>"Auto", "Manual" or "Disabled" (Win32_Service.StartMode).</summary>
    public string StartMode { get; init; } = string.Empty;
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
    /// <summary>The runner group from .runner ("Default" for repository runners).</summary>
    public string RunnerGroup { get; init; } = string.Empty;
    /// <summary>Registered with --ephemeral (from .runner).</summary>
    public bool Ephemeral { get; init; }
    /// <summary>Registered with --disableupdate (from .runner).</summary>
    public bool DisableUpdate { get; init; }
    /// <summary>Registered with --no-default-labels: GitHub lists no "self-hosted" label. Only known when GitHub was read.</summary>
    public bool NoDefaultLabels { get; init; }
    /// <summary>The settings to pass to config.cmd when this runner is registered again, on this PC or another one.</summary>
    public RunnerRegistrationOptions RegistrationOptions
        => new(RunnerGroup, Ephemeral, DisableUpdate, NoDefaultLabels,
            ManualStart: StartsAt == RunnerStart.Off || (StartsAt == RunnerStart.Disabled && ManualStartBeforeMove));
    /// <summary>The old copy of a moved runner (disabled) was set not to start with Windows before the move.</summary>
    public bool ManualStartBeforeMove { get; init; }
    public bool ServiceInstalled { get; init; }
    /// <summary>What starts the runner after a restart (a <see cref="RunnerStart"/> value).</summary>
    public string StartsAt { get; init; } = RunnerStart.Unknown;
    /// <summary>
    /// Running from a console window (run.cmd) rather than as a Windows service. Also true for a few minutes after the
    /// console's listener stopped (see <see cref="ConsoleRestarting"/>).
    /// </summary>
    public bool IsInteractive { get; init; }
    /// <summary>
    /// A console runner whose listener stopped a moment ago, as it does while it updates itself. It is shown as a console
    /// runner (not "No service", which would offer Repair and install a second copy) and is not running.
    /// </summary>
    public bool ConsoleRestarting { get; init; }
    /// <summary>The old copy of a runner that was moved to another PC (see <see cref="RunnerService.IsInUseElsewhere"/>).</summary>
    public bool MovedAway { get; init; }
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
    public string ExecutablePath { get; init; } = string.Empty;
    /// <summary>0 for services; a signed-in user's session for programs they started.</summary>
    public uint SessionId { get; init; }
}
