using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RunnerManager;

/// <summary>Fleet tools: updates, remote logs, auto-recovery, PC restarts, work-folder cleanup, recent runs and alerts.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan RunsRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RunnerReleaseInterval = TimeSpan.FromHours(6);

    private readonly Dictionary<string, IReadOnlyList<WorkflowRunSummary>> _recentRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, bool> _runFailureStates = new();
    private readonly HashSet<string> _primedRunRepos = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Agent events already reported, with their time (events older than <see cref="AgentEventMemory"/> are forgotten).</summary>
    private readonly Dictionary<string, DateTime> _seenAgentEvents = new(StringComparer.Ordinal);
    private static readonly TimeSpan AgentEventAlertAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AgentEventMemory = TimeSpan.FromMinutes(30);
    private bool _agentEventsPrimed;
    private bool _started;
    private DateTime _lastRunsRefresh = DateTime.MinValue;
    private DateTime _lastRunnerReleaseCheck = DateTime.MinValue;
    private static readonly TimeSpan UpdateGuardInterval = TimeSpan.FromMinutes(1);
    private DateTime _lastUpdateGuardCheck = DateTime.MinValue;

    /// <summary>Starts monitoring. Called when the window loads, or directly when starting minimized to the tray.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        CreateTray();
        AddActivity($"Runner Manager v{AppUpdater.CurrentVersion} started.");
        try
        {
            await LegacyInstall.MigrateAsync(AddActivity);
            await Installer.EnsureLaunchTaskAsync(force: App.ElevatedByPrompt);
        }
        catch (Exception ex) { AddActivity($"Could not update the install: {ex.Message}"); }
        if (UpdateGuard.TakeRollbackNotice() is { } notice) RaiseAlert(notice, warning: true);
        if (AppSettings.LoadProblem is { } settingsProblem) AddActivity($"Settings: {settingsProblem}");
        await RefreshAsync(forceAuthCheck: true);
        ApplyRefreshInterval();
        _refreshTimer.Start();
        StartLiveJobs();
        await RunPostSetupStepsAsync();
    }

    /// <summary>Follow-up steps chosen in the setup wizard (passed as --fleet=new|join and --rdc=setup).</summary>
    public string PostSetupFleet { get; set; } = string.Empty;
    public bool PostSetupRdc { get; set; }

    private async Task RunPostSetupStepsAsync()
    {
        var fleet = PostSetupFleet;
        PostSetupFleet = string.Empty;
        if (fleet == "new" && !InFleet) await SetUpFleetAsync(FleetStart.New);
        else if (fleet == "join" && !InFleet) await SetUpFleetAsync(FleetStart.Join);

        if (PostSetupRdc && !RemoteDesktopManager.IsConfigured())
        {
            PostSetupRdc = false;
            await RunOperationAsync("Set up Remote Desktop Commander", async log =>
            {
                await RemoteDesktopManager.SetupAsync();
                log("Setup console opened. Sign in there. Runner Manager detects the connection automatically.");
            });
        }
    }

    // ================= Periodic extras =================

    private async Task RefreshFleetExtrasAsync()
    {
        try
        {
            // Releases are public, so updates are checked whether or not GitHub is connected.
            var release = (await AppUpdater.CheckForUpdateAsync()).Release;
            // A failed check keeps the version found before.
            var latest = release?.Version ?? MachineInfo.LatestAppVersion;
            if (latest != MachineInfo.LatestAppVersion)
            {
                MachineInfo.LatestAppVersion = latest;
                foreach (var machine in Machines) machine.Refresh();
            }
            var showBanner = latest.Length > 0 && AppUpdater.IsNewer(latest, AppUpdater.CurrentVersion)
                && latest != _settings.DismissedUpdateVersion;
            UpdateBanner.Visibility = showBanner ? Visibility.Visible : Visibility.Collapsed;
            if (showBanner) UpdateBannerText.Text = $"Runner Manager v{latest} is available. Current version: v{AppUpdater.CurrentVersion}.";

            if (DateTime.UtcNow - _lastUpdateGuardCheck >= UpdateGuardInterval)
            {
                _lastUpdateGuardCheck = DateTime.UtcNow;
                if (await UpdateGuard.CheckFromDashboardAsync(_isEnrolled) is { } notice) RaiseAlert(notice, warning: true);
            }

            if (!_githubAuthenticated) return;
            if (DateTime.UtcNow - _lastRunnerReleaseCheck > RunnerReleaseInterval)
            {
                _lastRunnerReleaseCheck = DateTime.UtcNow;
                var tag = await GitHubCli.RunAsync("api repos/actions/runner/releases/latest --jq .tag_name");
                if (tag.ExitCode == 0) MachineRunnerStatus.LatestRunnerVersion = AppUpdater.Normalize(tag.Output.Trim());
            }

            if (DateTime.UtcNow - _lastRunsRefresh >= RunsRefreshInterval)
            {
                _lastRunsRefresh = DateTime.UtcNow;
                await RefreshRecentRunsAsync();
            }
        }
        catch (Exception ex) { AddActivity($"Background check failed: {ex.Message}"); }
    }

    private async Task RefreshRecentRunsAsync()
    {
        var repositories = Machines.SelectMany(m => m.Runners)
            .Select(r => r.RepositoryPath)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        foreach (var repository in repositories)
        {
            var runs = await GitHubApi.GetRecentRunsAsync(repository);
            if (runs == null) continue;
            var primed = _primedRunRepos.Contains(repository);
            foreach (var run in runs)
            {
                var wasKnown = _runFailureStates.TryGetValue(run.Id, out var wasFailure);
                _runFailureStates[run.Id] = run.IsFailure;
                // Alert on runs that newly failed: seen before as not failed, or first seen failed within the last 10 minutes.
                var newlyFailed = run.IsFailure && primed
                    && (wasKnown ? !wasFailure : DateTime.UtcNow - run.UpdatedUtc < TimeSpan.FromMinutes(10));
                if (newlyFailed && _settings.NotifyOnFailedRuns)
                    RaiseAlert($"{repository}: {run.WorkflowName} #{run.RunNumber} failed on {run.Branch}.", warning: true);
            }
            _primedRunRepos.Add(repository);
            _recentRuns[repository] = runs;
        }
        PruneRunState(repositories);
        foreach (var runner in Machines.SelectMany(m => m.Runners)) ApplyRecentRuns(runner);
    }

    /// <summary>
    /// Forgets repositories no runner uses any more, and runs that dropped out of every recent list, so a dashboard that
    /// runs for weeks in the tray does not keep growing.
    /// </summary>
    private void PruneRunState(IReadOnlyCollection<string> repositories)
    {
        var wanted = repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _recentRuns.Keys.Where(r => !wanted.Contains(r)).ToList())
        {
            _recentRuns.Remove(stale);
            _primedRunRepos.Remove(stale);
        }
        var live = _recentRuns.Values.SelectMany(runs => runs).Select(run => run.Id).ToHashSet();
        foreach (var id in _runFailureStates.Keys.Where(id => !live.Contains(id)).ToList()) _runFailureStates.Remove(id);
    }

    // ================= Live job view =================

    private sealed class LiveJobState
    {
        public GitHubJobState? Job;
        public string JobNote = string.Empty;
        public RunnerJobActivity? Activity;
        public string ActivityNote = string.Empty;
    }

    private static readonly TimeSpan LiveJobInterval = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, LiveJobState> _liveJobs = new(StringComparer.OrdinalIgnoreCase);
    private bool _liveJobsRefreshing;

    private void StartLiveJobs()
    {
        var timer = new DispatcherTimer { Interval = LiveJobInterval };
        timer.Tick += async (_, _) => await RefreshLiveJobsAsync();
        timer.Start();
    }

    /// <summary>
    /// Updates busy runners: the job and its steps from GitHub (cached for a few seconds), and for expanded runners the
    /// output and processes from the PC the runner is on. Skipped while the window is hidden in the notification area.
    /// </summary>
    private async Task RefreshLiveJobsAsync()
    {
        if (_liveJobsRefreshing || !IsVisible) return;
        _liveJobsRefreshing = true;
        try
        {
            var busy = Machines.SelectMany(m => m.Runners.Where(r => r.IsBusy).Select(r => (Machine: m, Runner: r))).ToList();
            var keys = busy.Select(b => RunnerKey(b.Runner)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var finished in _liveJobs.Keys.Where(k => !keys.Contains(k)).ToList()) _liveJobs.Remove(finished);
            await Task.WhenAll(busy.Select(b => RefreshLiveJobAsync(b.Machine, b.Runner)));
            // Cards may have been rebuilt meanwhile; the state is kept by runner key.
            foreach (var runner in Machines.SelectMany(m => m.Runners)) ApplyLiveJob(runner);
        }
        catch (Exception ex) { AddActivity($"Could not update the job view: {ex.Message}"); }
        finally { _liveJobsRefreshing = false; }
    }

    private async Task RefreshLiveJobAsync(MachineInfo machine, MachineRunnerStatus runner)
    {
        var key = RunnerKey(runner);
        if (!_liveJobs.TryGetValue(key, out var state)) _liveJobs[key] = state = new LiveJobState();

        if (!_githubAuthenticated)
        {
            state.Job = null;
            state.JobNote = "Connect GitHub to see the job and its steps.";
        }
        else if (string.IsNullOrWhiteSpace(runner.RepositoryPath) || runner.AgentId == 0)
        {
            state.Job = null;
            state.JobNote = runner.IsOrgRunner ? "GitHub does not list jobs per organization runner, so the steps are not shown." : string.Empty;
        }
        else
        {
            var (read, job) = await GitHubApi.GetCurrentJobAsync(runner.RepositoryPath, runner.AgentId);
            if (!read) state.JobNote = "Could not read the job from GitHub. Trying again.";
            else
            {
                state.Job = job;
                state.JobNote = job == null ? "Waiting for GitHub to report the job." : string.Empty;
            }
        }

        if (!runner.IsExpanded) return;
        try
        {
            if (machine.IsLocal) state.Activity = await RunnerActivity.ReadAsync(runner.CommandKey);
            else if (!machine.IsReachable)
            {
                state.ActivityNote = $"{machine.MachineName} is offline.";
                return;
            }
            else if (machine.AgentProtocolVersion < 11)
            {
                state.Activity = null;
                state.ActivityNote = $"Update Runner Manager on {machine.MachineName} to see the output and processes.";
                return;
            }
            else state.Activity = await MachineRelay.RequestJobActivityAsync(machine.MachineKey, runner.CommandKey);
            state.ActivityNote = state.Activity is { Output.Length: 0, Processes.Length: 0 }
                ? $"The job has not started on {machine.MachineName} yet." : string.Empty;
        }
        catch (Exception ex) { state.ActivityNote = $"Could not read the output from {machine.MachineName}: {ex.Message}"; }
    }

    private void ApplyLiveJob(MachineRunnerStatus runner)
        => runner.LiveJob = runner.IsBusy && _liveJobs.TryGetValue(RunnerKey(runner), out var state)
            ? LiveJobView.Build(state.Job, state.JobNote, runner.IsExpanded ? state.Activity : null,
                runner.IsExpanded ? state.ActivityNote : string.Empty, DateTime.UtcNow)
            : null;

    private void RunnerLiveOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        var machine = Machines.FirstOrDefault(m => m.MachineKey == runner.MachineKey);
        var folder = runner.LocalRunner == null ? null : Path.Combine(runner.LocalRunner.InstallPath, "_diag", "pages");
        ShowLog(machine, $"job:{runner.CommandKey}", runner.TitleText, folder);
    }

    private void ApplyRecentRuns(MachineRunnerStatus runner)
    {
        if (!string.IsNullOrWhiteSpace(runner.RepositoryPath) && _recentRuns.TryGetValue(runner.RepositoryPath, out var runs))
            runner.RecentRuns = runs;
    }

    /// <summary>Turns auto-recovery and other agent events into activity entries and notifications.</summary>
    private void ReportAgentEvents()
    {
        foreach (var machine in Machines)
        {
            foreach (var agentEvent in machine.RecentEvents)
            {
                var key = $"{machine.MachineKey}|{agentEvent.Utc.Ticks}|{agentEvent.Text}";
                if (!_seenAgentEvents.TryAdd(key, agentEvent.Utc) || !_agentEventsPrimed) continue;
                // Also keeps an event that was forgotten (older than the memory) from being reported again.
                if (DateTime.UtcNow - agentEvent.Utc > AgentEventAlertAge) continue;
                RaiseAlert($"{machine.MachineName}: {agentEvent.Text}", agentEvent.Warning);
            }
        }
        _agentEventsPrimed = true;
        foreach (var old in _seenAgentEvents.Where(e => DateTime.UtcNow - e.Value > AgentEventMemory).Select(e => e.Key).ToList())
            _seenAgentEvents.Remove(old);
    }

    // ================= Signing =================

    /// <summary>Checks that the target agent will accept this PC's commands (fleet keys match).</summary>
    private bool CanSendTo(string machineKey)
    {
        var machine = Machines.FirstOrDefault(m => m.MachineKey == machineKey);
        if (machine == null || machine.IsLocal) return true;
        var problem = MachineRelay.SigningProblem(machine);
        if (problem == null) return true;
        if (DialogWindow.Show(this, DialogTone.Warning, $"Cannot send commands to {machine.MachineName}", problem, "Open settings") == "Open settings")
            OpenSettings_Click(this, new RoutedEventArgs());
        return false;
    }

    // ================= Settings & theme =================

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var existing = OwnedWindows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null)
        {
            existing.Activate();
            return;
        }
        var window = new SettingsWindow(_settings, ApplySettings, OnFleetSettingsChangedAsync, RunOperationAsync) { Owner = this };
        window.Show();
    }

    /// <summary>
    /// Hides what doesn't apply: the machines tile with only one PC, and everything Remote Desktop Commander until it's
    /// set up on at least one PC.
    /// </summary>
    private void ApplyOptionalSections(List<MachineInfo> machines)
    {
        var showRdc = machines.Any(m => m.RdcConfigured);
        if (showRdc != MachineInfo.ShowRdc)
        {
            MachineInfo.ShowRdc = showRdc;
            foreach (var machine in Machines) machine.Refresh();
        }
        TileRdcCard.Visibility = showRdc ? Visibility.Visible : Visibility.Collapsed;
        TileMachinesCard.Visibility = machines.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AppSubtitle.Text = showRdc ? "Self-hosted runners & RDC" : "Self-hosted GitHub runners";
    }

    private void ApplySettings()
    {
        ApplyRefreshInterval();
        UpdateSummary();
        _tray?.SyncSettings(_settings);
        LastUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}  ·  Refreshes every {_settings.RefreshSeconds}s";
    }

    private async Task OnFleetSettingsChangedAsync()
    {
        _previousSnapshot = null;
        _primedRunRepos.Clear();
        _forceDiscovery = true;
        await RefreshAsync(forceAuthCheck: true);
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    /// <summary>Installs the newest release on this PC, then offers to restart the dashboard with it.</summary>
    private async Task InstallUpdateAsync()
    {
        var check = await AppUpdater.CheckForUpdateAsync(force: true);
        if (check.Release is not { } release)
        {
            DialogWindow.Error(this, "Couldn't check for updates", check.Problem);
            return;
        }
        if (!check.UpdateAvailable)
        {
            DialogWindow.Info(this, "Runner Manager is up to date", $"This PC runs the newest version, v{AppUpdater.CurrentVersion}.");
            return;
        }
        if (IsAgentWorkRunning()) return;
        var restartAgent = false;
        var installed = await RunOperationAsync($"Install Runner Manager {release.Tag}",
            async log => restartAgent = await AppUpdater.InstallReleaseAsync(release, log));
        if (!installed) return;
        if (restartAgent) RestartLocalAgentInBackground();
        UpdateBanner.Visibility = Visibility.Collapsed;
        if (DialogWindow.Show(this, DialogTone.Question, $"Runner Manager {release.Tag} is installed",
                "Restart Runner Manager now to use the new version?", "Restart now", cancel: "Later") == "Restart now")
            ((App)Application.Current).RestartDashboard();
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        _settings.DismissedUpdateVersion = MachineInfo.LatestAppVersion;
        _settings.Save();
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Re-reads the view-model brushes. Activity entries and recent runs pick their colors with DynamicResource
    /// triggers in XAML, so they follow the theme by themselves.
    /// </summary>
    private void OnThemeChanged()
    {
        foreach (var machine in Machines)
        {
            machine.Refresh();
            foreach (var runner in machine.Runners) runner.Refresh();
        }
        ApplyGitHubStatus();
        ApplyEnrollmentStatus();
        UpdateSummary();
    }

    // ================= Machine actions =================

    private async void MachineUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineInfo>(sender) is not { CanUpdateAgent: true } machine) return;
        if (machine.LocalAgentOutdated)
        {
            if (_agentOwner != null)
            {
                DialogWindow.Info(this, "Another Windows user runs the fleet agent", AgentOwnership.Describe(_agentOwner));
                return;
            }
            if (!DialogWindow.Confirm(this, "Update the fleet agent on this PC?",
                    $"The fleet agent is updated to v{AppUpdater.CurrentVersion} and installed to {AppUpdater.InstallDir}. " +
                    "Runners and Remote Desktop Commander keep running.", "Update fleet agent")) return;
            if (IsAgentWorkRunning()) return;
            // The agent restarts once its running work is done, so this runs in the background.
            RunAgentWorkInBackground("Update fleet agent", MachineRelay.UpdateLocalAgentAsync);
            return;
        }
        if (machine.IsLocal)
        {
            await InstallUpdateAsync();
            return;
        }
        if (!DialogWindow.Confirm(this, $"Update {machine.MachineName}?",
                $"The fleet agent on {machine.MachineName} downloads, verifies and installs Runner Manager v{MachineInfo.LatestAppVersion}, then restarts. Runners and Remote Desktop Commander keep running.",
                "Update")) return;
        await QueueMachineCommandAsync(machine, "agent", "Update", string.Empty, "Update",
            $"{machine.MachineName}: update to v{MachineInfo.LatestAppVersion} queued.");
    }

    private async void MachineWatchdog_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineInfo>(sender) is not { } machine) return;
        var enable = !machine.WatchdogEnabled;
        if (machine.IsLocal)
        {
            FleetConfig.Update(c => c.Watchdog = enable);
            AddActivity($"Auto-recovery turned {(enable ? "on" : "off")} on {machine.MachineName}.");
            await OnFleetSettingsChangedAsync();
            return;
        }
        await QueueMachineCommandAsync(machine, "agent", "Watchdog", enable ? "on" : "off", string.Empty,
            $"{machine.MachineName}: auto-recovery {(enable ? "on" : "off")} queued.");
    }

    private void MachineAgentLog_Click(object sender, RoutedEventArgs e) => ShowLog(ItemFrom<MachineInfo>(sender), "agent", null, FleetConfig.StateDir);

    private void MachineRdcLog_Click(object sender, RoutedEventArgs e) => ShowLog(ItemFrom<MachineInfo>(sender), "rdc", null, FleetConfig.StateDir);

    private void RunnerViewLog_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        var machine = Machines.FirstOrDefault(m => m.MachineKey == runner.MachineKey);
        var folder = runner.LocalRunner == null ? null : Path.Combine(runner.LocalRunner.InstallPath, "_diag");
        ShowLog(machine, $"runner:{runner.CommandKey}", runner.TitleText, folder);
    }

    private void ShowLog(MachineInfo? machine, string logKey, string? label, string? localFolder)
    {
        if (machine == null) return;
        var what = logKey switch
        {
            "agent" => "fleet agent log",
            "rdc" => "Remote Desktop Commander log",
            _ when logKey.StartsWith("job:", StringComparison.OrdinalIgnoreCase) => $"{label} live output",
            _ => $"{label} diagnostic log"
        };
        LogViewerWindow window;
        if (machine.IsLocal)
        {
            window = new LogViewerWindow($"{machine.MachineName} — {what}", "Reading…", _ => LogReader.ReadAsync(logKey), localFolder);
        }
        else
        {
            if (!CanSendTo(machine.MachineKey)) return;
            window = new LogViewerWindow($"{machine.MachineName} — {what}",
                $"Requesting the {what} from {machine.MachineName}…\nThis usually takes 5–15 seconds.",
                token => MachineRelay.RequestRemoteLogAsync(machine.MachineKey, logKey, token));
        }
        window.Owner = this;
        window.Show();
    }

    private async void MachineForget_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineInfo>(sender) is not { IsLocal: false } machine) return;
        if (!DialogWindow.Confirm(this, $"Forget {machine.MachineName}?",
                machine.IsOnline
                    ? $"{machine.MachineName} is online and still has the pairing code, so it may appear again. To remove it permanently, use \u201cLeave fleet\u201d in its \u22ef menu on {machine.MachineName}."
                    : $"{machine.MachineName} is removed from this list. If it is still in the fleet, it appears again when it is found.",
                "Forget")) return;
        MachineRelay.ForgetPeer(machine.MachineName);
        var host = machine.Address.Split(':')[0];
        FleetConfig.Update(c => c.ManualAddresses.RemoveAll(a => a.Split(':')[0].Equals(host, StringComparison.OrdinalIgnoreCase)));
        AddActivity($"Forgot {machine.MachineName}.");
        if (_selectedMachineKey == machine.MachineKey) _selectedMachineKey = null;
        await RefreshAsync();
    }

    private async void MachineReboot_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineInfo>(sender) is not { } machine) return;
        var busy = machine.Runners.Where(r => r.IsBusy).Select(r => r.TitleText).ToList();
        var warning = busy.Count > 0 ? $"\n\nJobs on these runners are cancelled: {string.Join(", ", busy)}." : string.Empty;
        if (!DialogWindow.Confirm(this, $"Restart {machine.MachineName}?",
                $"Windows restarts in 60 seconds. Runner services start again on their own. {machine.MachineName} rejoins the fleet " +
                $"once the Windows user who runs Runner Manager there signs in.{warning}",
                "Restart PC", danger: true)) return;
        if (machine.IsLocal)
        {
            var (code, output) = await ScheduledTasks.RunAsync("shutdown.exe", "/r /t 60 /c \"Restart requested from Runner Manager\"");
            if (code != 0)
            {
                DialogWindow.Error(this, $"Could not restart {machine.MachineName}", output.Trim());
                return;
            }
            AddActivity($"{machine.MachineName} restarts in 60 seconds.");
            if (DialogWindow.Show(this, DialogTone.Warning, $"{machine.MachineName} restarts in 60 seconds",
                    "Save your work. You can cancel the restart until then.", "OK", cancel: null, secondary: "Cancel restart") == "Cancel restart")
            {
                await ScheduledTasks.RunAsync("shutdown.exe", "/a");
                AddActivity("Restart cancelled.");
            }
            return;
        }
        await QueueMachineCommandAsync(machine, "machine", "Reboot", Environment.MachineName, "Reboot",
            $"{machine.MachineName}: restart queued. The PC restarts 60 seconds after it receives the command.");
    }

    /// <summary>Sends a PC-level command ("agent", "machine" or "rdc") to another PC and shows it as pending until it reports back.</summary>
    private async Task QueueMachineCommandAsync(MachineInfo machine, string target, string action, string value,
        string pendingLabel, string queuedMessage)
    {
        if (!CanSendTo(machine.MachineKey)) return;
        try
        {
            if (pendingLabel.Length > 0) machine.SetRdcPending(pendingLabel);
            var id = await MachineRelay.SendCommandAsync(machine.MachineKey, target, string.Empty, action, value: value);
            if (pendingLabel.Length > 0)
                _pendingRdcCommands[machine.MachineKey] = new PendingCommand(id, pendingLabel, DateTime.UtcNow);
            AddActivity(queuedMessage);
            SetStatus(queuedMessage, StatusKind.Ok);
        }
        catch (Exception ex)
        {
            machine.SetRdcPending(string.Empty);
            _pendingRdcCommands.Remove(machine.MachineKey);
            AddActivity(target == "rdc"
                ? $"{machine.MachineName}: Remote Desktop Commander {action.ToLowerInvariant()} failed: {ex.Message}"
                : $"{machine.MachineName}: {action} failed: {ex.Message}");
            await RefreshAsync();
        }
    }

    // ================= Runner actions =================

    private async void RunnerCleanWork_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { CanCleanWork: true } runner) return;
        var size = runner.WorkFolderBytes == null ? "all" : $"{SystemHealth.FormatBytes(runner.WorkFolderBytes)} of";
        if (!DialogWindow.Confirm(this, $"Clean the work folder of {runner.TitleText}?",
                $"This deletes {size} checkouts, tool caches and build output on {runner.MachineName}. The runner stops during cleanup and starts again afterwards. The next job starts with a fresh checkout.",
                "Clean work folder", danger: true)) return;
        await RunRunnerActionAsync(runner, "CleanWork", $"Clean {runner.TitleText} work folder",
            log => RunnerService.CleanWorkFolderAsync(runner.CommandKey, log));
    }

    private async void RerunFailed_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ItemFrom<WorkflowRunSummary>(sender) is not { } run) return;
        try
        {
            await GitHubApi.RerunFailedJobsAsync(run.RepositoryPath, run.Id);
            AddActivity($"{run.RepositoryPath}: re-running failed jobs of {run.WorkflowName} #{run.RunNumber}.");
            _lastRunsRefresh = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            DialogWindow.Error(this, "Could not re-run failed jobs", ex.Message);
        }
    }
}
