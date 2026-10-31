using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RunnerManager;

public partial class MainWindow : Window
{
    private static readonly TimeSpan AuthCheckInterval = TimeSpan.FromMinutes(2);
    // Local runner GitHub state (job/queue) costs several API calls per runner, so poll it less often
    // than the cheap local/relay state.
    private static readonly TimeSpan LocalGitHubRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingCommandTimeout = TimeSpan.FromSeconds(75);

    /// <summary>Runners on this PC with their GitHub state (the cards show <see cref="Machines"/>).</summary>
    public List<RunnerInfo> Runners { get; } = new();
    public ObservableCollection<MachineInfo> Machines { get; } = new();
    public ObservableCollection<object> NavItems { get; } = new();
    public ObservableCollection<ActivityEntry> ActivityLog { get; } = new();
    public ListCollectionView MachinesView { get; }

    private readonly AllMachinesEntry _allMachines = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Dictionary<string, string> _lastRunnerStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _machineRunnerExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingCommand> _pendingMachineCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingCommand> _pendingRunnerCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentUserActions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, MachineSnapshot>? _previousSnapshot;
    private readonly DispatcherTimer _refreshTimer;
    private TrayIcon? _tray;
    private bool _refreshing;
    private bool _refreshQueued;
    private bool _refreshQueuedForceAuth;
    private bool _operationRunning;
    private string _operationTitle = string.Empty;
    private bool _githubAuthenticated;
    private string _githubLogin = string.Empty;
    private bool _isEnrolled;
    private bool _agentTaskChecked;
    /// <summary>Another Windows user whose fleet agent runs on this PC, or null.</summary>
    private string? _agentOwner;
    private bool _agentOwnerReported;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _suppressNavSelection;
    private string? _selectedMachineKey;
    private string _filter = string.Empty;
    private DateTime _lastAuthCheck = DateTime.MinValue;
    private DateTime _lastLocalRunnerRefresh = DateTime.MinValue;
    private bool _forceDiscovery = true;

    public MainWindow()
    {
        MachinesView = new ListCollectionView(Machines) { Filter = FilterMachine };
        MachineInfo.CollapsedPcs = new HashSet<string>(_settings.CollapsedPcs ?? new(), StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        WindowTheme.Apply(this);
        DataContext = this;

        NavItems.Add(_allMachines);
        MachineNav.SelectedIndex = 0;
        ApplyActivityPanelState();
        ApplyGitHubStatus();
        ApplyEnrollmentStatus();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds) };
        // A tick during a slow refresh is simply skipped; the next tick comes soon enough.
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!_refreshing) await RefreshAsync();
        };
        // Hidden in the notification area, the dashboard refreshes less often; notifications still come from these refreshes.
        IsVisibleChanged += async (_, _) =>
        {
            ApplyRefreshInterval();
            if (IsVisible && _started && !_refreshing) await RefreshAsync();
        };
        Loaded += async (_, _) => await StartAsync();
        ThemeManager.Changed += OnThemeChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ReportError(string message)
    {
        AddActivity($"Error: {message}");
        DialogWindow.Error(this, "An error occurred", message);
    }

    // ================= Refresh =================

    private async Task RefreshAsync(bool forceAuthCheck = false)
    {
        if (_refreshing)
        {
            // Run it once more when the current refresh ends, so state changed meanwhile (an operation that
            // just finished, a PC that was forgotten) is not missed.
            _refreshQueued = true;
            _refreshQueuedForceAuth |= forceAuthCheck;
            return;
        }
        if (_operationRunning) return;
        _refreshing = true;
        try
        {
            var enrollmentTask = MachineRelay.IsAgentInstalledAsync();
            if (forceAuthCheck || DateTime.Now - _lastAuthCheck > AuthCheckInterval)
            {
                var wasAuthenticated = _githubAuthenticated;
                _githubAuthenticated = await GitHubCli.IsAuthenticatedAsync();
                _lastAuthCheck = DateTime.Now;
                if (_githubAuthenticated && (!wasAuthenticated || string.IsNullOrEmpty(_githubLogin)))
                {
                    var login = await GitHubCli.RunAsync("api user --jq .login");
                    _githubLogin = login.ExitCode == 0 ? login.Output.Trim() : string.Empty;
                }
                if (!_githubAuthenticated) _githubLogin = string.Empty;
                ApplyGitHubStatus();
            }
            _isEnrolled = await enrollmentTask;
            // Another Windows user runs this PC's fleet agent: this dashboard shows the PC but leaves the agent alone.
            _agentOwner = await AgentOwnership.OtherUserAsync();
            if (_agentOwner != null)
            {
                _isEnrolled = false;
                if (!_agentOwnerReported) AddActivity(AgentOwnership.Describe(_agentOwner));
                _agentOwnerReported = true;
            }
            ApplyEnrollmentStatus();
            await EnsureAgentTaskSettingsAsync();

            var refreshLocalRunners = forceAuthCheck || Runners.Count == 0
                || DateTime.Now - _lastLocalRunnerRefresh >= LocalGitHubRefreshInterval;
            var runnersTask = refreshLocalRunners
                ? RunnerService.ReadSnapshotAsync(_githubAuthenticated)
                : Task.FromResult(Runners.ToList());
            // Other PCs are read directly from their agents on the network; GitHub isn't involved.
            var machinesTask = MachineRelay.ReadFleetAsync(_forceDiscovery);
            _forceDiscovery = false;
            await Task.WhenAll(runnersTask, machinesTask);
            var runners = await runnersTask;
            var machines = await machinesTask;
            var localIndex = machines.FindIndex(m => m.IsLocal);
            if (localIndex >= 0) machines[localIndex] = machines[localIndex].WithLocalRunners(runners);

            if (refreshLocalRunners)
            {
                _lastLocalRunnerRefresh = DateTime.Now;
                ApplyLocalRunners(runners);
            }
            ApplyMachines(machines);
            UpdateNavShape();
            ApplyEnrollmentStatus();
            ScheduleLocalAgentUpdate(machines.FirstOrDefault(m => m.IsLocal));
            StartStoppedLocalAgent(machines.FirstOrDefault(m => m.IsLocal));
            ApplyPendingStates();
            ApplyFilter();
            UpdateSummary();
            DetectChanges();
            ReportAgentEvents();
            await CheckRunnerHealthAsync();
            await RefreshFleetExtrasAsync();
            LastUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}  ·  Refreshes every {_settings.RefreshSeconds}s";
            if (!_operationRunning)
            {
                if (_agentOwner != null) SetStatus($"The fleet agent on this PC runs for {_agentOwner}.", StatusKind.Warning);
                else SetStatus(_githubAuthenticated ? "Ready" : "GitHub not connected. Showing this PC only.",
                    _githubAuthenticated ? StatusKind.Ok : StatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            AddActivity($"Refresh failed: {ex.Message}");
            SetStatus($"Refresh failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            _refreshing = false;
            if (_refreshQueued)
            {
                var force = _refreshQueuedForceAuth;
                _refreshQueued = _refreshQueuedForceAuth = false;
                await RefreshAsync(force);
            }
        }
    }

    private async Task EnsureAgentTaskSettingsAsync()
    {
        if (_agentTaskChecked || !_isEnrolled) return;
        _agentTaskChecked = true;
        try
        {
            if (await MachineRelay.RepairAgentTaskSettingsAsync())
                AddActivity("Updated the fleet agent task to run on battery and without the 3-day time limit.");
            if (await ScheduledTasks.UpdateAgentTaskIfOutdatedAsync(fromAgent: false))
                AddActivity("Updated the fleet agent task: it now starts again by itself when it stopped.");
        }
        catch (Exception ex) { AddActivity($"Could not update fleet agent task settings: {ex.Message}"); }
    }

    private DateTime _localAgentStartedUtc = DateTime.MinValue;

    /// <summary>
    /// Starts this PC's fleet agent again when it stopped, for example after it was ended in Task Manager. The scheduled
    /// task restarts it only when it fails to start, not when it is ended.
    /// </summary>
    private async void StartStoppedLocalAgent(MachineInfo? local)
    {
        if (!_isEnrolled || FleetConfig.Current.FleetKey == null || local is not { IsLocalOnly: true, FleetAgentRunning: false }) return;
        if (DateTime.UtcNow - _localAgentStartedUtc < TimeSpan.FromMinutes(2)) return;
        _localAgentStartedUtc = DateTime.UtcNow;
        try
        {
            if (!await MachineRelay.IsAgentInstalledAsync()) return;
            if (Elevation.IsElevated && await ScheduledTasks.RepairIfUnindexedAsync(ScheduledTasks.AgentTaskName))
                AddActivity("Repaired the fleet agent task. Windows could not start it.");
            var (exitCode, output) = await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
            AddActivity(exitCode == 0 ? "The fleet agent on this PC was not running. Started it again." : $"Could not start the fleet agent: {output.Trim()}");
        }
        catch (Exception ex) { AddActivity($"Could not start the fleet agent: {ex.Message}"); }
    }

    private bool _localAgentUpdateTried;

    /// <summary>When this PC's agent is older than this dashboard, update it once, automatically.</summary>
    private void ScheduleLocalAgentUpdate(MachineInfo? local)
    {
        if (_localAgentUpdateTried || _agentOwner != null
            || local is not { LocalAgentOutdated: true } || FleetConfig.Current.FleetKey == null) return;
        // This version was just undone because its agent did not work; do not put it back.
        if (UpdateGuard.WasRolledBack(AppUpdater.CurrentVersion)) return;
        // Another operation is running: try again on a later refresh.
        if (_operationRunning || IsAgentWorkBusy) return;
        _localAgentUpdateTried = true;
        AddActivity("The fleet agent on this PC is outdated. Updating it.");
        RunAgentWorkInBackground("Update fleet agent", MachineRelay.UpdateLocalAgentAsync);
    }

    private Task? _agentWork;
    private string _agentWorkTitle = string.Empty;

    /// <summary>The fleet agent is being updated or restarted in the background.</summary>
    public bool IsAgentWorkBusy => _agentWork is { IsCompleted: false };

    /// <summary>Completes when background work on the fleet agent has finished (it never throws).</summary>
    public Task WaitForAgentWorkAsync()
        => _agentWork is { } work ? work.ContinueWith(_ => { }, TaskScheduler.Default) : Task.CompletedTask;

    /// <summary>Restarts this PC's fleet agent (after an update) without blocking the dashboard.</summary>
    public void RestartLocalAgentInBackground()
        => RunAgentWorkInBackground("Restart the fleet agent", async log =>
        {
            await MachineRelay.RestartLocalAgentAsync(log: log);
            log("The fleet agent runs the new version.");
        });

    /// <summary>
    /// Runs fleet agent work that can take long: the agent restarts only once it has finished its jobs and requests. The
    /// dashboard keeps refreshing and other actions stay available meanwhile; progress goes to the activity list.
    /// </summary>
    private void RunAgentWorkInBackground(string title, Func<Action<string>, Task> work)
    {
        if (IsAgentWorkBusy)
        {
            AddActivity($"{title} skipped: “{_agentWorkTitle}” is still running.");
            return;
        }
        _agentWorkTitle = title;
        _agentWork = RunAsync();

        async Task RunAsync()
        {
            AddActivity($"{title} started. The fleet agent restarts when it is not busy.");
            var waiting = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            waiting.Tick += (_, _) =>
            {
                waiting.Interval = TimeSpan.FromMinutes(30);
                AddActivity($"{title}: waiting for the fleet agent to finish its work.");
            };
            waiting.Start();
            try
            {
                await work(AddActivity);
                AddActivity($"{title} completed.");
            }
            catch (Exception ex)
            {
                RaiseAlert($"{title} failed: {ex.Message}", warning: true);
            }
            finally
            {
                waiting.Stop();
                _forceDiscovery = true;
                await RefreshAsync();
            }
        }
    }

    /// <summary>Tells the user to wait when the fleet agent is busy being updated. Returns true when it is.</summary>
    private bool IsAgentWorkRunning()
    {
        if (!IsAgentWorkBusy) return false;
        DialogWindow.Info(this, "The fleet agent is being updated", $"“{_agentWorkTitle}” is still running. Try again when it is done. Progress is shown in the activity list.");
        return true;
    }

    private void ApplyLocalRunners(List<RunnerInfo> runners)
    {
        var firstLoad = _lastRunnerStates.Count == 0;
        Runners.Clear();
        foreach (var runner in runners)
        {
            Runners.Add(runner);
            var state = $"{runner.StatusText}/{runner.ActivityText}/{runner.CurrentWorkText}";
            if (_lastRunnerStates.TryGetValue(runner.InstallPath, out var previous) && previous != state)
            {
                var job = runner.ActivityText == "Busy" && runner.CurrentWorkText is not ("none" or "unavailable")
                    ? $" — {runner.CurrentWorkText}" : string.Empty;
                AddActivity($"{runner.AgentName}: {runner.ActivityText.ToLowerInvariant()}{job}");
            }
            _lastRunnerStates[runner.InstallPath] = state;
        }
        if (firstLoad && runners.Count > 0)
            AddActivity($"Found {runners.Count} runner{(runners.Count == 1 ? "" : "s")} on this PC.");
    }

    /// <summary>
    /// Updates the Machines list in place: unchanged machines keep their existing cards (so hover,
    /// expansion and scroll position survive the 5-second refresh) and only get their time-based text updated.
    /// </summary>
    private void ApplyMachines(List<MachineInfo> machines)
    {
        foreach (var machine in machines)
        {
            var pending = string.Empty;
            if (_pendingMachineCommands.TryGetValue(machine.MachineKey, out var command))
            {
                if (machine.HasFinished(command.CommandId)
                    || DateTime.UtcNow - command.QueuedUtc > PendingCommandTimeout)
                    _pendingMachineCommands.Remove(machine.MachineKey);
                else
                    pending = command.Action;
            }
            machine.SetPending(pending);
        }

        var sameShape = Machines.Count == machines.Count
            && Machines.Zip(machines).All(pair => pair.First.MachineKey == pair.Second.MachineKey);
        // "All PCs" is only in the sidebar when there are several PCs (see UpdateNavShape).
        var offset = NavItems.Contains(_allMachines) ? 1 : 0;
        if (!sameShape)
        {
            _suppressNavSelection = true;
            Machines.Clear();
            while (NavItems.Count > offset) NavItems.RemoveAt(NavItems.Count - 1);
            foreach (var machine in machines)
            {
                PrepareRunners(machine);
                Machines.Add(machine);
                NavItems.Add(machine);
            }
            RestoreNavSelection();
            return;
        }

        for (var i = 0; i < machines.Count; i++)
        {
            var existing = Machines[i];
            var incoming = machines[i];
            if (existing.Fingerprint == incoming.Fingerprint)
            {
                existing.SetPending(incoming.PendingAction);
                existing.Touch(incoming);
                foreach (var runner in existing.Runners) LinkLocalRunner(runner);
                continue;
            }
            foreach (var runner in existing.Runners)
                _machineRunnerExpansionStates[RunnerKey(runner)] = runner.IsExpanded;
            PrepareRunners(incoming);
            _suppressNavSelection = true;
            Machines[i] = incoming;
            NavItems[i + offset] = incoming;
            RestoreNavSelection();
        }
    }

    /// <summary>One PC: no "All PCs" entry. Several PCs: it comes back at the top.</summary>
    private bool IsSinglePc => Machines.Count <= 1;

    private void UpdateNavShape()
    {
        var hasAll = NavItems.Contains(_allMachines);
        if (IsSinglePc && hasAll)
        {
            _suppressNavSelection = true;
            NavItems.Remove(_allMachines);
            _selectedMachineKey = null;
            RestoreNavSelection();
        }
        else if (!IsSinglePc && !hasAll)
        {
            _suppressNavSelection = true;
            NavItems.Insert(0, _allMachines);
            RestoreNavSelection();
        }
    }

    private void RestoreNavSelection()
    {
        _suppressNavSelection = true;
        object? fallback = NavItems.Contains(_allMachines) ? _allMachines : Machines.FirstOrDefault();
        var selected = _selectedMachineKey == null ? null : Machines.FirstOrDefault(m => m.MachineKey == _selectedMachineKey);
        // The selected PC is gone: show all PCs again instead of filtering every card out.
        var lostSelection = _selectedMachineKey != null && selected == null;
        if (lostSelection) _selectedMachineKey = null;
        MachineNav.SelectedItem = (object?)selected ?? fallback;
        _suppressNavSelection = false;
        if (lostSelection)
        {
            ApplyFilter();
            UpdatePageHeader();
        }
    }

    private void PrepareRunners(MachineInfo machine)
    {
        foreach (var runner in machine.Runners)
        {
            if (_machineRunnerExpansionStates.TryGetValue(RunnerKey(runner), out var expanded))
                runner.IsExpanded = expanded;
            LinkLocalRunner(runner);
            ApplyRecentRuns(runner);
            ApplyLiveJob(runner);
            ApplyHealth(runner);
        }
    }

    private void LinkLocalRunner(MachineRunnerStatus runner)
    {
        runner.LocalRunner = !runner.IsLocalMachine ? null : Runners.FirstOrDefault(r =>
            !string.IsNullOrEmpty(runner.ServiceName) ? r.Name.Equals(runner.ServiceName, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrEmpty(runner.InstallPath) ? r.InstallPath.Equals(runner.InstallPath, StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrEmpty(r.Name)
              && r.RepositoryPath.Equals(runner.RepositoryPath, StringComparison.OrdinalIgnoreCase)
              && r.AgentName.Equals(runner.AgentName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Shows "Stopping…"-style states on runners until the agent reports the result.</summary>
    private void ApplyPendingStates()
    {
        foreach (var machine in Machines)
        foreach (var runner in machine.Runners)
        {
            var key = RunnerKey(runner);
            if (!_pendingRunnerCommands.TryGetValue(key, out var pending))
            {
                runner.SetPending(string.Empty);
                continue;
            }
            var done = DateTime.UtcNow - pending.QueuedUtc > PendingCommandTimeout
                || (pending.Action == "Start" && runner.IsRunning)
                || (pending.Action == "Stop" && !runner.IsRunning)
                || (pending.Action is "Restart" or "Edit" or "CleanWork" or "Remove" or "Convert" or "Startup" or "StopJob" && !string.IsNullOrEmpty(pending.CommandId)
                    && machine.HasFinished(pending.CommandId));
            if (done) _pendingRunnerCommands.Remove(key);
            runner.SetPending(done ? string.Empty : pending.Action);
        }
    }

    private static string RunnerKey(MachineRunnerStatus runner)
        => $"{runner.MachineKey}|{(string.IsNullOrEmpty(runner.CommandKey) ? runner.RepositoryPath + "/" + runner.AgentName : runner.CommandKey)}";

    // ================= Summary, filter, navigation =================

    private void UpdateSummary()
    {
        var machines = Machines.ToList();
        var reachable = machines.Count(m => m.IsReachable);
        var runners = machines.SelectMany(m => m.Runners).ToList();
        var running = runners.Count(r => r.IsRunning);
        var busy = runners.Where(r => r.IsBusy).ToList();
        var queued = Runners.Sum(r => r.QueueCount);

        TileMachines.Text = $"{reachable} / {machines.Count}";
        TileMachinesSub.Text = reachable == machines.Count ? "All PCs reporting" : $"{machines.Count - reachable} not reporting";
        TileMachinesSub.Foreground = reachable == machines.Count ? Palette.TextMuted : Palette.Warning;
        TileRunners.Text = $"{running} / {runners.Count}";
        var stopped = runners.Count - running;
        TileRunnersSub.Text = stopped == 0 ? "All running" : $"{stopped} stopped";
        TileRunnersSub.Foreground = stopped == 0 ? Palette.TextMuted : Palette.Warning;
        TileBusy.Text = busy.Count.ToString();
        TileBusySub.Text = busy.Count > 0 ? string.Join(", ", busy.Select(r => r.TitleText))
            : queued > 0 ? $"{queued} job{(queued == 1 ? "" : "s")} queued on this PC" : "No jobs running";

        _allMachines.Update($"{machines.Count} PC{(machines.Count == 1 ? "" : "s")}  ·  {runners.Count} runners");
        ApplyOptionalSections(machines);
        UpdateOutputTabs();
        UpdatePageHeader();
        _tray?.SetTooltip($"Runner Manager — {running}/{runners.Count} runners running, {busy.Count} busy");
    }

    private void UpdatePageHeader()
    {
        var selected = Machines.FirstOrDefault(m => m.MachineKey == _selectedMachineKey)
                       ?? (IsSinglePc ? Machines.FirstOrDefault() : null);
        PageTitle.Text = selected?.MachineName ?? "All PCs";
        PageSubtitle.Text = !_githubAuthenticated
            ? "Connect GitHub in the account menu (bottom left) to add runners and see job status"
            : selected != null
                ? selected.RunnerSummary
                : "GitHub runners on all PCs";
    }

    private bool FilterMachine(object item)
    {
        if (item is not MachineInfo machine) return false;
        if (_selectedMachineKey != null && machine.MachineKey != _selectedMachineKey) return false;
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        return machine.MachineName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || machine.Runners.Any(r => r.MatchesFilter);
    }

    private void ApplyFilter()
    {
        foreach (var machine in Machines)
        {
            var machineMatch = !string.IsNullOrWhiteSpace(_filter)
                && machine.MachineName.Contains(_filter, StringComparison.OrdinalIgnoreCase);
            foreach (var runner in machine.Runners)
                runner.MatchesFilter = machineMatch || runner.Matches(_filter);
        }
        // Refreshing the view rebuilds every card, so only do it when the visible set actually changes.
        var visible = MachinesView.Cast<MachineInfo>().Select(m => m.MachineKey);
        var wanted = Machines.Where(FilterMachine).Select(m => m.MachineKey);
        if (!visible.SequenceEqual(wanted)) MachinesView.Refresh();
        var empty = MachinesView.IsEmpty && !string.IsNullOrWhiteSpace(_filter);
        NoResultsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Text = $"No runners or PCs match “{_filter}”.";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void MachineNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavSelection) return;
        if (MachineNav.SelectedItem == null) { RestoreNavSelection(); return; }
        // With a single PC there's nothing to filter; keep showing "everything" so a second PC appears automatically.
        _selectedMachineKey = IsSinglePc ? null : (MachineNav.SelectedItem as MachineInfo)?.MachineKey;
        ApplyFilter();
        UpdatePageHeader();
    }

    // ================= Change notifications =================

    private sealed record MachineSnapshot(string Name, bool Reachable, bool IsLocal,
        Dictionary<string, (string Title, bool Running)> Runners);

    private void DetectChanges()
    {
        var current = Machines.ToDictionary(m => m.MachineKey, m => new MachineSnapshot(
            m.MachineName, m.IsReachable, m.IsLocal,
            m.Runners.GroupBy(RunnerKey).ToDictionary(g => g.Key, g => (g.First().TitleText, g.First().IsRunning))));
        var previous = _previousSnapshot;
        _previousSnapshot = current;
        if (previous == null) return;

        foreach (var (key, now) in current)
        {
            if (!previous.TryGetValue(key, out var before)) continue;
            if (!now.IsLocal && before.Reachable != now.Reachable)
                RaiseAlert(now.Reachable ? $"{now.Name} is back online." : $"{now.Name} is offline. Its fleet agent stopped reporting.",
                    warning: !now.Reachable);
            foreach (var (runnerKey, runner) in now.Runners)
            {
                if (!before.Runners.TryGetValue(runnerKey, out var old) || old.Running == runner.Running) continue;
                if (WasUserAction(runnerKey)) continue;
                RaiseAlert(runner.Running
                    ? $"{now.Name}: runner {runner.Title} started."
                    : $"{now.Name}: runner {runner.Title} stopped.", warning: !runner.Running);
            }
        }
    }

    private bool WasUserAction(string key)
        => _recentUserActions.TryGetValue(key, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(2);

    private void MarkUserAction(string key)
    {
        foreach (var old in _recentUserActions.Where(a => DateTime.UtcNow - a.Value > TimeSpan.FromMinutes(2)).Select(a => a.Key).ToList())
            _recentUserActions.Remove(old);
        _recentUserActions[key] = DateTime.UtcNow;
    }

    private static readonly TimeSpan HiddenRefreshInterval = TimeSpan.FromSeconds(30);

    private void ApplyRefreshInterval()
    {
        var interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _refreshTimer.Interval = IsVisible || interval > HiddenRefreshInterval ? interval : HiddenRefreshInterval;
    }

    private void RaiseAlert(string message, bool warning)
    {
        AddActivity(message);
        // AI assistants read the alerts with recent_alerts.
        AlertLog.Append(message, warning);
        if (_settings.Notifications && !IsActive)
            _tray?.Notify("Runner Manager", message, warning);
    }

    // ================= Header / account =================

    private void ApplyGitHubStatus()
    {
        GitHubAccountText.Text = _githubAuthenticated
            ? string.IsNullOrEmpty(_githubLogin) ? "GitHub" : _githubLogin
            : "GitHub";
        GitHubStatusText.Text = _githubAuthenticated ? "Connected" : "Not connected";
        GitHubStatusText.Foreground = _githubAuthenticated ? Palette.Success : Palette.Warning;
        AvatarText.Text = _githubAuthenticated && _githubLogin.Length > 0 ? _githubLogin[..1].ToUpperInvariant() : "?";
        AvatarBadge.Background = _githubAuthenticated ? Palette.Tint(Palette.Info) : (Brush)FindResource("Bg3Brush");
        ConnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Collapsed : Visibility.Visible;
        ReconnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        DisconnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>In a fleet = the agent is installed and this PC has the fleet's pairing code.</summary>
    private bool InFleet => _isEnrolled && FleetConfig.Current.FleetKey != null;

    private void ApplyEnrollmentStatus()
    {
        var count = Machines.Count;
        FleetHeaderText.Text = count > 1 ? $"FLEET  ·  {count} PCs" : "THIS PC";
        FleetSecureIcon.Visibility = InFleet && count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AddPcRowText.Text = InFleet ? "Add a PC" : "Connect another PC";
        AddPcRowHint.Text = InFleet ? "Pair with a 6-digit code" : "Manage several PCs together";
        AddPcRow.ToolTip = InFleet
            ? "Add a new PC to the fleet. It finds this PC on the network, and you confirm the 6-digit code on both PCs."
            : "Create or join a fleet to view and control several PCs from one place.";
    }

    private async void GitHubConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_githubAuthenticated)
        {
            if (!DialogWindow.Confirm(this, "Disconnect GitHub?",
                    "The GitHub CLI on this PC is signed out. Runner Manager stops showing other PCs and GitHub job status until you connect again.",
                    "Disconnect", danger: true)) return;
            await RunOperationAsync("Disconnect GitHub", async log =>
            {
                await GitHubCli.DisconnectAsync();
                log("GitHub disconnected.");
            });
            return;
        }
        GitHubCli.LaunchLogin();
        AddActivity("Opened GitHub sign-in. Complete it in the browser or terminal window.");
        await Task.Delay(1500);
        await RefreshAsync(forceAuthCheck: true);
    }

    private async void GitHubReconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_githubAuthenticated)
                await GitHubCli.DisconnectAsync();
            GitHubCli.LaunchLogin();
            AddActivity("Reconnecting GitHub. Complete sign-in in the browser or terminal window.");
            _githubAuthenticated = false;
            _githubLogin = string.Empty;
            ApplyGitHubStatus();
            await Task.Delay(1500);
            await RefreshAsync(forceAuthCheck: true);
        }
        catch (Exception ex)
        {
            AddActivity($"GitHub reconnect failed: {ex.Message}");
            DialogWindow.Error(this, "Could not reconnect GitHub", ex.Message);
        }
    }

    private async void AddRunner_Click(object sender, RoutedEventArgs e)
    {
        if (!_githubAuthenticated)
        {
            if (DialogWindow.Show(this, DialogTone.Info, "Connect GitHub first",
                    "Adding a runner requires a GitHub account. Runner Manager uses your GitHub CLI sign-in to request short-lived registration tokens and does not store them.",
                    "Connect GitHub") == "Connect GitHub")
                GitHubConnect_Click(sender, e);
            return;
        }
        var dialog = new AddRunnerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Setup == null) return;
        await RunOperationAsync("Add runner", log => RunnerService.CreateRunnerAsync(dialog.Setup, log));
    }

    // ================= Operations =================

    /// <summary>
    /// Runs one operation at a time on this PC, with progress in the status bar and activity.
    /// Returns true when it ran and succeeded; false when it was skipped or failed. Set pending states inside
    /// <paramref name="operation"/>, so nothing shows as pending when the operation is skipped.
    /// </summary>
    private async Task<bool> RunOperationAsync(string title, Func<Action<string>, Task> operation)
    {
        if (_operationRunning)
        {
            AddActivity($"{title} skipped: another operation is still running.");
            return false;
        }
        _operationRunning = true;
        _operationTitle = title;
        SetStatus($"{title}…", StatusKind.Busy);
        var succeeded = false;
        try
        {
            AddActivity($"{title} started.");
            await operation(message =>
            {
                AddActivity(message);
                SetStatus($"{title}: {message}", StatusKind.Busy);
            });
            succeeded = true;
            AddActivity($"{title} completed.");
            if (!IsActive && _settings.Notifications) _tray?.Notify("Runner Manager", $"{title} completed.");
        }
        catch (Exception ex)
        {
            AddActivity($"{title} failed: {ex.Message}");
            SetStatus($"{title} failed", StatusKind.Error);
            DialogWindow.Error(this, $"{title} failed", ex.Message);
        }
        finally
        {
            _operationRunning = false;
            await RefreshAsync(forceAuthCheck: true);
        }
        return succeeded;
    }

    private enum StatusKind { Ok, Busy, Warning, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        OperationProgress.Visibility = kind == StatusKind.Busy ? Visibility.Visible : Visibility.Hidden;
        OperationProgress.IsIndeterminate = kind == StatusKind.Busy;
        (StatusGlyph.Text, StatusGlyph.Foreground) = kind switch
        {
            StatusKind.Busy => ("", Palette.Info),
            StatusKind.Warning => ("", Palette.Warning),
            StatusKind.Error => ("", Palette.Danger),
            _ => ("", Palette.Success)
        };
    }

    private async Task QueueRunnerCommandAsync(MachineRunnerStatus runner, string action,
        string runnerName = "", string[]? labels = null, string value = "")
    {
        var key = RunnerKey(runner);
        if (!CanSendTo(runner.MachineKey)) return;
        try
        {
            MarkUserAction(key);
            runner.SetPending(action);
            var id = await MachineRelay.SendCommandAsync(runner.MachineKey, "runner", runner.CommandKey, action, runnerName, labels, value);
            _pendingRunnerCommands[key] = new PendingCommand(id, action, DateTime.UtcNow);
            AddActivity($"{runner.TitleText}: {action.ToLowerInvariant()} queued on {runner.MachineName}.");
            SetStatus($"{action} queued on {runner.MachineName}. Waiting for its fleet agent.", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            runner.SetPending(string.Empty);
            AddActivity($"{runner.TitleText}: {action} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a runner action directly when the runner is on this PC, or queues it for the runner's PC otherwise.
    /// The local runner shows <paramref name="action"/> as pending only while the operation runs.
    /// </summary>
    private async Task RunRunnerActionAsync(MachineRunnerStatus runner, string action, string title,
        Func<Action<string>, Task> local, string runnerName = "", string[]? labels = null, string value = "",
        bool showLocalPending = true)
    {
        if (!runner.IsLocalMachine)
        {
            await QueueRunnerCommandAsync(runner, action, runnerName, labels, value);
            return;
        }
        await RunOperationAsync(title, log =>
        {
            MarkUserAction(RunnerKey(runner));
            if (showLocalPending) runner.SetPending(action);
            return local(log);
        });
    }

    // ================= Runner actions =================

    private static T? ItemFrom<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    private void RunnerRow_Click(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current != null && current != sender)
        {
            // Buttons, links and the live job panel (so its output can be read and selected) do not toggle the row.
            if (current is ButtonBase || current is TextBlock { Tag: string { Length: > 0 } } || current is FrameworkElement { Name: "LiveJobPanel" }) return;
            // Text pieces such as a Run (a Hyperlink's text) are not visuals; their parent is in the logical tree.
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        runner.IsExpanded = !runner.IsExpanded;
        _machineRunnerExpansionStates[RunnerKey(runner)] = runner.IsExpanded;
        // Opening a runner that is running a job shows its output in the bottom panel once it arrives.
        if (runner.IsExpanded && runner.IsBusy) _selectOutputFor = RunnerKey(runner);
        ApplyLiveJob(runner);
        UpdateOutputTabs();
        if (runner.IsExpanded && runner.IsBusy) _ = RefreshLiveJobsAsync();
    }

    private void MachineHeader_Click(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current != null && current != sender;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            // The update badge, the ⋯ menu and other buttons keep working without collapsing the card.
            if (current is ButtonBase) return;
        }
        if (ItemFrom<MachineInfo>(sender) is not { } machine) return;
        var collapsed = machine.ToggleCollapsed();
        _settings.CollapsedPcs.RemoveAll(n => n.Equals(machine.MachineName, StringComparison.OrdinalIgnoreCase));
        if (collapsed) _settings.CollapsedPcs.Add(machine.MachineName);
        _settings.Save();
    }

    private async void RunnerPower_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        var action = runner.IsRunning ? "Stop" : "Start";
        if (action == "Stop" && runner.IsBusy && !DialogWindow.Confirm(this, $"Stop {runner.TitleText}?",
                "This runner is running a job. Stopping it cancels the job.", "Stop runner", danger: true))
            return;
        await RunRunnerActionAsync(runner, action, $"{action} {runner.TitleText}", _ =>
        {
            // Saved only when the stop or start really runs, so auto-recovery follows what happened.
            FleetConfig.SetManuallyStopped(runner.CommandKey, action == "Stop");
            return RunnerService.ControlServiceAsync(runner.CommandKey, action);
        });
    }

    private async void RunnerRestart_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        if (runner.IsBusy && !DialogWindow.Confirm(this, $"Restart {runner.TitleText}?",
                "This runner is running a job. Restarting it cancels the job.", "Restart runner", danger: true))
            return;
        await RunRunnerActionAsync(runner, "Restart", $"Restart {runner.TitleText}",
            _ => RunnerService.ControlServiceAsync(runner.CommandKey, "Restart"));
    }

    private async void RunnerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        if (!runner.CanEdit)
        {
            DialogWindow.Info(this, "Cannot edit this runner", "Wait for the current job to finish, then try again.");
            return;
        }
        var dialog = new EditRunnerWindow(runner) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Edit == null) return;
        var edit = dialog.Edit;
        await RunRunnerActionAsync(runner, "Edit", $"Edit {runner.TitleText}",
            log => RunnerService.EditRunnerAsync(runner.CommandKey, edit.RunnerName, edit.Labels, log),
            edit.RunnerName, edit.Labels, showLocalPending: false);
    }

    private async void RunnerRepair_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender)?.LocalRunner is not { } runner) return;
        if (!DialogWindow.Confirm(this, $"Repair {runner.AgentName}?",
                "The runner is unregistered and registered again with the same name and labels. It is then installed as a Windows service.",
                "Repair")) return;
        await RunOperationAsync($"Repair {runner.AgentName}", log => RunnerService.RepairRunnerAsync(runner, log));
    }

    private async void RunnerWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender)?.LocalRunner is not { } runner) return;
        if (!DialogWindow.Confirm(this, "Check workflows?",
                $"Jobs in {runner.RepositoryPath} that use a GitHub-hosted Windows runner (windows-latest, windows-2022, …) are switched to this runner. Changes are committed directly to the repository.",
                "Check and update")) return;
        await RunOperationAsync("Check workflows", log => RunnerService.CheckWorkflowsAsync(runner, log));
    }

    private void RunnerFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender)?.LocalRunner is not { } runner) return;
        OpenFolder(runner.InstallPath);
    }

    private void RunnerOpenActions_Click(object sender, RoutedEventArgs e)
        => OpenUrl(ItemFrom<MachineRunnerStatus>(sender)?.ActionsUrl);

    private void RunnerOpenSettings_Click(object sender, RoutedEventArgs e)
        => OpenUrl(ItemFrom<MachineRunnerStatus>(sender)?.RunnersSettingsUrl);

    private void Link_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0)
        {
            OpenUrl(url);
            e.Handled = true;
        }
    }

    private void OpenButtonMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) Menus.OpenBelow(button, alignRight: true);
    }

    // ================= Fleet actions =================

    private void AddPcRow_Click(object sender, RoutedEventArgs e)
    {
        if (InFleet) AddAnotherPc_Click(sender, e);
        else InstallAgent_Click(sender, e);
    }

    private async void LeaveFleet_Click(object sender, RoutedEventArgs e) => await LeaveFleetAsync();

    /// <summary>Takes this PC out of the fleet (the other PCs are told to forget it).</summary>
    public async Task LeaveFleetAsync()
    {
        if (!InFleet || IsAgentWorkRunning()) return;
        var others = Machines.Where(m => !m.IsLocal).Select(m => m.MachineName).ToList();
        if (!DialogWindow.Confirm(this, $"Remove {Environment.MachineName} from the fleet?",
                (others.Count > 0 ? $"{string.Join(", ", others)} will forget this PC. " : string.Empty) +
                "The fleet agent and pairing code are removed from this PC. Runners keep running. " +
                "You can add this PC again later.",
                "Leave fleet", danger: true)) return;
        await RunOperationAsync("Leave fleet", async log =>
        {
            var told = await MachineRelay.LeaveFleetAsync(log);
            _isEnrolled = false;
            _agentTaskChecked = false;
            log(told.Count > 0 ? $"{Environment.MachineName} left the fleet. {string.Join(", ", told)} removed it."
                : $"{Environment.MachineName} left the fleet.");
        });
        ApplyEnrollmentStatus();
    }

    private async void InstallAgent_Click(object sender, RoutedEventArgs e) => await SetUpFleetAsync(FleetStart.Ask);

    private enum FleetStart { Ask, New, Join }

    /// <summary>Joins or starts a fleet (from the sidebar, or right after setup), then installs the agent.</summary>
    private async Task SetUpFleetAsync(FleetStart start)
    {
        if (await AgentOwnership.OtherUserAsync() is { } owner)
        {
            DialogWindow.Info(this, "Another Windows user runs the fleet agent", AgentOwnership.Describe(owner));
            return;
        }
        if (IsAgentWorkRunning()) return;
        var newFleet = false;
        if (start == FleetStart.New && FleetConfig.Current.FleetKey == null)
        {
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            newFleet = true;
        }
        if (FleetConfig.Current.FleetKey == null)
        {
            var join = new JoinFleetWindow(openJoinPage: start == FleetStart.Join) { Owner = this };
            if (join.ShowDialog() != true) return;
            if (join.StartNewFleet)
            {
                FleetConfig.SetFleetKey(CommandSigner.NewKey());
                newFleet = true;
            }
            else if (join.PairingKey != null)
            {
                FleetConfig.SetFleetKey(join.PairingKey);
                if (join.HostName.Length > 0) MachineRelay.AddKnownPeer(join.HostName, join.HostAddress);
                AddActivity(join.HostName.Length > 0 ? $"Joined the fleet through {join.HostName}." : "Joined the fleet with a pairing code.");
            }
            else return;
        }
        await RunOperationAsync("Join fleet", async log =>
        {
            await AppUpdater.InstallCurrentBuildAsync(log);
            log("Starting the fleet agent and opening its ports for the local network and Tailscale…");
            await MachineRelay.InstallAndStartAgentTaskAsync();
            _isEnrolled = true;
            _agentTaskChecked = true;
            _forceDiscovery = true;
            log($"{Environment.MachineName} joined the fleet.");
        });
        ApplyEnrollmentStatus();
        if (newFleet && InFleet) AddAnotherPc_Click(this, new RoutedEventArgs());
    }

    private async void AddAnotherPc_Click(object sender, RoutedEventArgs e)
    {
        if (FleetConfig.Current.FleetKey is not { } key) return;
        var window = new PairingHostWindow(key) { Owner = this };
        window.ShowDialog();
        foreach (var name in window.JoinedPcs) AddActivity($"{name} joined the fleet.");
        _forceDiscovery = true;
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ManualRefreshAsync();

    private async Task ManualRefreshAsync()
    {
        if (_operationRunning || !RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        SetStatus("Refreshing…", StatusKind.Busy);
        try
        {
            _forceDiscovery = true;
            _lastLocalRunnerRefresh = DateTime.MinValue;
            await RefreshAsync(forceAuthCheck: true);
        }
        catch (Exception ex) { AddActivity($"Refresh failed: {ex.Message}"); }
        finally { RefreshButton.IsEnabled = true; }
    }

    // ================= Settings, tray, activity =================

    private void CreateTray()
    {
        _tray = new TrayIcon(_settings);
        _tray.OpenRequested += ShowFromTray;
        _tray.RefreshRequested += async () => await ManualRefreshAsync();
        _tray.ExitRequested += () => ((App)Application.Current).RequestExit();
        _tray.NotificationsToggled += value => { _settings.Notifications = value; _settings.Save(); };
        _tray.CloseToTrayToggled += value => { _settings.CloseToTray = value; _settings.Save(); };
    }

    /// <summary>Lets the next close really close instead of hiding to the tray.</summary>
    public void PrepareExit() => _exiting = true;

    /// <summary>While an operation runs, asks whether to exit anyway. True when nothing runs or the user agreed.</summary>
    public bool ConfirmExitDuringOperation()
    {
        var title = _operationRunning ? _operationTitle : IsAgentWorkBusy ? _agentWorkTitle : null;
        if (title == null) return true;
        if (!IsVisible) ShowFromTray();
        return DialogWindow.Confirm(this, "An operation is running",
            $"“{title}” is still running. Exiting now can leave it half done, for example with the fleet agent stopped. Exit anyway?",
            "Exit anyway", danger: true);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // A screen may have been unplugged while the window was hidden.
        WindowPlacement.EnsureOnScreen(this);
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.Notify("Runner Manager is still running", "Monitoring continues in the notification area. To exit, right-click the icon.");
            }
            return;
        }
        if (!_exiting && !ConfirmExitDuringOperation())
        {
            e.Cancel = true;
            return;
        }
        _refreshTimer.Stop();
        ThemeManager.Changed -= OnThemeChanged;
        SaveActivityPanelHeight();
        _settings.Save();
        _tray?.Dispose();
        _tray = null;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolder(FleetConfig.StateDir);

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
        => OpenUrl(AppUpdater.ReleasesUrl);

    private void ShowShortcuts_Click(object sender, RoutedEventArgs e)
        => DialogWindow.Info(this, "Keyboard shortcuts",
            "F5  —  Refresh\nCtrl+F  —  Search runners\nCtrl+N  —  Add runner\nCtrl+,  —  Settings\nEsc  —  Clear search\nCtrl+1…9  —  Go to a PC (Ctrl+0 for all PCs)");

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F5)
        {
            _ = ManualRefreshAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            OpenSettings_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            AddRunner_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (ctrl && e.Key is >= Key.D0 and <= Key.D9)
        {
            var index = e.Key - Key.D0;
            if (index < NavItems.Count) MachineNav.SelectedIndex = index;
            e.Handled = true;
        }
    }

    private void ToggleActivity_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ActivityPanelOpen) SaveActivityPanelHeight();
        _settings.ActivityPanelOpen = !_settings.ActivityPanelOpen;
        ApplyActivityPanelState();
        _settings.Save();
    }

    private void ApplyActivityPanelState()
    {
        var open = _settings.ActivityPanelOpen;
        ApplyBottomPanel();
        ActivitySplitter.IsEnabled = open;
        ActivityRow.Height = open ? new GridLength(Math.Clamp(_settings.ActivityPanelHeight, 90, 600)) : GridLength.Auto;
        ActivityChevron.Text = open ? "" : "";
    }

    private void SaveActivityPanelHeight()
    {
        if (_settings.ActivityPanelOpen && ActivityRow.ActualHeight > 60)
            _settings.ActivityPanelHeight = ActivityRow.ActualHeight;
    }

    private void ActivitySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveActivityPanelHeight();
        _settings.Save();
    }

    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (OutputRunner is { } runner)
            {
                Clipboard.SetText(OutputBox.SelectionLength > 0 ? OutputBox.SelectedText : runner.OutputText.Replace("\n", Environment.NewLine));
                SetStatus("Output copied to clipboard", StatusKind.Ok);
                return;
            }
            Clipboard.SetText(string.Join(Environment.NewLine, ActivityLog.Reverse().Select(a => a.ToString())));
            SetStatus("Activity copied to clipboard", StatusKind.Ok);
        }
        catch (Exception ex) { AddActivity($"Copy failed: {ex.Message}"); }
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        ActivityLog.Clear();
        ActivityCount.Text = ActivityTabCount.Text = string.Empty;
    }

    public void AddActivity(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddActivity(message));
            return;
        }
        ActivityLog.Insert(0, new ActivityEntry { Message = message, Kind = ActivityEntry.Classify(message) });
        while (ActivityLog.Count > 300) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        ActivityCount.Text = ActivityTabCount.Text = ActivityLog.Count.ToString();
    }

    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            AddActivity($"Folder not found: {path}");
            SetStatus($"Folder not found: {path}", StatusKind.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AddActivity($"Could not open {url}: {ex.Message}"); }
    }

    private sealed record PendingCommand(string CommandId, string Action, DateTime QueuedUtc);
}

/// <summary>The "All PCs" entry at the top of the sidebar.</summary>
public sealed class AllMachinesEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Subtitle { get; private set; } = "Loading…";

    public void Update(string subtitle)
    {
        if (Subtitle == subtitle) return;
        Subtitle = subtitle;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
    }
}
