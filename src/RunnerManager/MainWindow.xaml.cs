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
    /// <summary>gh has been asked once; until then the dashboard does not say that GitHub is not connected.</summary>
    private bool _githubChecked;
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
        MachineRunnerStatus.QueueCollapsed = _settings.QueueCollapsed;
        MachineRunnerStatus.RecentRunsCollapsed = _settings.RecentRunsCollapsed;
        InitializeComponent();
        WindowTheme.Apply(this);
        DataContext = this;
        WindowPlacement.Restore(this, _settings.MainWindowPlacement, SystemParameters.WorkArea);
        _shownState = WindowState;
        TrackPlacement();

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
        // Coming back from the GitHub sign-in in a browser or console window.
        Activated += async (_, _) => await CheckGitHubSignInAsync();
        ThemeManager.Changed += OnThemeChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += ContentArea_SizeChanged;
        SizeChanged += NavArea_SizeChanged;
    }

    public void ReportError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportError(message));
            return;
        }
        AddActivity($"Error: {message}");
        DialogWindow.Error(this, "An error occurred", message);
    }

    // ================= Refresh =================

    private const string JoinFleetTitle = "Join fleet";
    private const string LeaveFleetTitle = "Leave fleet";

    /// <summary>The fleet read that runs now or ran last, so joining or leaving the fleet can wait for it.</summary>
    private Task? _fleetRead;

    /// <summary>
    /// This PC joins or leaves the fleet. The fleet is not read meanwhile: a read saves the PCs it finds, and could add a PC
    /// back to the list that leaving just cleared. The refresh at the end of the operation reads it again.
    /// </summary>
    private bool FleetMembershipChanging => _operationRunning && !ReadsFleetDuring(_operationTitle);

    /// <summary>Whether the fleet is read while the operation with this title runs on this PC.</summary>
    internal static bool ReadsFleetDuring(string operationTitle) => operationTitle is not (JoinFleetTitle or LeaveFleetTitle);

    /// <summary>Waits until a fleet read that is still running has ended (it never throws).</summary>
    private Task WaitForFleetReadAsync()
        => _fleetRead is { IsCompleted: false } read ? read.ContinueWith(_ => { }, TaskScheduler.Default) : Task.CompletedTask;

    /// <summary>
    /// Reads the fleet and this PC's runners and shows them. Nothing here waits for gh: the GitHub sign-in is checked
    /// beside it (<see cref="StartGitHubCheck"/>), and the job health, update and recent-run checks run after it, so the
    /// next timer tick is not skipped while GitHub is slow.
    /// </summary>
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
        if (FleetMembershipChanging) return;
        _refreshing = true;
        var completed = false;
        try
        {
            // Other PCs are read directly from their agents on the network; GitHub is not involved, so they never
            // wait for it.
            var machinesTask = MachineRelay.ReadFleetAsync(_forceDiscovery);
            _fleetRead = machinesTask;
            _forceDiscovery = false;
            // While an operation runs on this PC, only the other PCs are refreshed: this PC's runners, fleet agent and
            // fleet membership are what operations change, and everything is refreshed when the operation ends.
            if (_operationRunning)
            {
                ApplyFleet(await machinesTask, Runners.ToList(), localRunnersRead: false);
                LastUpdatedText.Text = $"Updated {TimeFormat.WithSeconds(DateTime.Now)}  ·  Refreshes every {_settings.RefreshSeconds}s";
                return;
            }
            if (forceAuthCheck || DateTime.Now - _lastAuthCheck > AuthCheckInterval) StartGitHubCheck();
            // This PC's runners are read with GitHub when it was connected at the last check. When the check that runs
            // now says otherwise, they are read again (see CheckGitHubAsync).
            var refreshLocalRunners = forceAuthCheck || Runners.Count == 0
                || DateTime.Now - _lastLocalRunnerRefresh >= LocalGitHubRefreshInterval;
            var runnersTask = refreshLocalRunners
                ? RunnerService.ReadSnapshotAsync(_githubAuthenticated)
                : Task.FromResult(Runners.ToList());

            _isEnrolled = await MachineRelay.IsAgentInstalledAsync();
            // Another Windows user runs this PC's fleet agent: this dashboard shows the PC but leaves the agent alone.
            _agentOwner = await AgentOwnership.OtherUserAsync();
            if (_agentOwner != null)
            {
                _isEnrolled = false;
                if (!_agentOwnerReported) AddActivity(AgentOwnership.Describe(_agentOwner));
                _agentOwnerReported = true;
            }
            ApplyEnrollmentStatus();

            // This PC's runners wait for GitHub, which can be slow: show the other PCs first, with this PC's last readings.
            if (await Task.WhenAny(runnersTask, machinesTask) == machinesTask && !runnersTask.IsCompleted)
                ApplyFleet(await machinesTask, Runners.ToList(), localRunnersRead: false);
            var runners = await runnersTask;
            var machines = await machinesTask;
            // An operation may have started while this refresh waited.
            if (_operationRunning)
            {
                ApplyFleet(machines, Runners.ToList(), localRunnersRead: false);
                return;
            }
            ApplyFleet(machines, runners, refreshLocalRunners);
            completed = true;
            LastUpdatedText.Text = $"Updated {TimeFormat.WithSeconds(DateTime.Now)}  ·  Refreshes every {_settings.RefreshSeconds}s";
            ShowIdleStatus();
        }
        catch (Exception ex)
        {
            AddActivity($"Refresh failed: {ex.Message}");
            // An operation shows its progress there.
            if (!_operationRunning) SetStatus($"Refresh failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            _refreshing = false;
            // A first refresh that failed says so in the status bar.
            LoadingPanel.Visibility = Visibility.Collapsed;
        }
        if (completed) _ = RunChecksAfterRefreshAsync();
        if (_refreshQueued)
        {
            var force = _refreshQueuedForceAuth;
            _refreshQueued = _refreshQueuedForceAuth = false;
            await RefreshAsync(force);
        }
    }

    /// <summary>
    /// The checks that ask Windows and GitHub, after a refresh has shown the fleet. They are not awaited by the refresh,
    /// and each check runs once at a time, so a slow one never holds the next refresh back or runs twice.
    /// </summary>
    private async Task RunChecksAfterRefreshAsync()
    {
        try
        {
            await EnsureAgentTaskSettingsAsync();
            await CheckRunnerHealthAsync();
            await RefreshFleetExtrasAsync();
        }
        catch (Exception ex) { AddActivity($"Background check failed: {ex.Message}"); }
    }

    /// <summary>What the status bar says when no operation runs and no result is shown.</summary>
    private void ShowIdleStatus()
    {
        if (_operationRunning || DateTime.UtcNow < _noticeUntilUtc) return;
        if (_agentOwner != null) SetStatus($"The fleet agent on this PC runs for {_agentOwner}.", StatusKind.Warning);
        else if (!_githubChecked) SetStatus("Checking the GitHub connection…", StatusKind.Busy);
        // Not the problem of one repository: only a pause that holds back every request to GitHub is about the whole view.
        else if (_githubAuthenticated && GitHubCli.ProblemFor(null) is { } pause)
            SetStatus(GitHubNotes.Describe(GitHubNotes.PauseLead, pause, DateTime.UtcNow), StatusKind.Warning);
        else if (_githubAuthenticated) SetStatus("Ready", StatusKind.Ok);
        else SetStatus("GitHub is not connected. Jobs, queues and recent runs are not shown.", StatusKind.Warning);
    }

    /// <summary>The GitHub sign-in check that runs now or ran last.</summary>
    private Task? _gitHubCheck;

    /// <summary>
    /// Asks gh whether GitHub is connected, beside the refresh: gh can take up to 45 seconds to answer, and the fleet is
    /// shown without it. One check runs at a time.
    /// </summary>
    private void StartGitHubCheck()
    {
        if (_gitHubCheck is { IsCompleted: false }) return;
        _lastAuthCheck = DateTime.Now;
        _gitHubCheck = CheckGitHubAsync();
    }

    private async Task CheckGitHubAsync()
    {
        var wasAuthenticated = _githubAuthenticated;
        var wasChecked = _githubChecked;
        bool authenticated;
        var login = _githubLogin;
        try
        {
            authenticated = await GitHubCli.IsAuthenticatedAsync();
            if (authenticated && (!wasAuthenticated || login.Length == 0))
            {
                var user = await GitHubCli.RunAsync("api user --jq .login");
                // Without an answer (no network, for example) the name read before stays.
                if (user.ExitCode == 0) login = user.Output.Trim();
            }
        }
        catch (Exception ex)
        {
            AddActivity($"Could not check the GitHub connection: {ex.Message}");
            return;
        }
        _githubAuthenticated = authenticated;
        _githubLogin = authenticated ? login : string.Empty;
        _githubChecked = true;
        ApplyGitHubStatus();
        UpdatePageHeader();
        if (authenticated == wasAuthenticated)
        {
            if (!wasChecked) ShowIdleStatus();
            return;
        }
        // Connected now: what GitHub refused before (no sign-in) is asked again at once, not after its pause.
        if (authenticated) GitHubCli.ForgetProblems();
        // This PC's runners were read the other way, with or without GitHub.
        _lastLocalRunnerRefresh = DateTime.MinValue;
        await RefreshAsync();
    }

    /// <summary>
    /// Shows a reading of the fleet, with <paramref name="runners"/> as this PC's runners. While an operation runs on this
    /// PC, nothing on this PC is started or changed, and cards that show a pending action are kept as they are.
    /// </summary>
    private void ApplyFleet(List<MachineInfo> machines, List<RunnerInfo> runners, bool localRunnersRead)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        machines = machines.ToList();
        var localIndex = machines.FindIndex(m => m.IsLocal);
        if (localIndex >= 0) machines[localIndex] = machines[localIndex].WithLocalRunners(runners);
        var duringOperation = _operationRunning;
        if (localRunnersRead)
        {
            _lastLocalRunnerRefresh = DateTime.Now;
            ApplyLocalRunners(runners);
        }
        ApplyMachines(machines, duringOperation);
        UpdateNavShape();
        if (!duringOperation)
        {
            ApplyEnrollmentStatus();
            ScheduleLocalAgentUpdate(machines.FirstOrDefault(m => m.IsLocal));
            StartStoppedLocalAgent(machines.FirstOrDefault(m => m.IsLocal));
        }
        ApplyPendingStates(clearOthers: !duringOperation);
        ReportRemoteResults();
        ApplyFilter();
        UpdateSummary();
        DetectChanges();
        ReportAgentEvents();
    }

    private async Task EnsureAgentTaskSettingsAsync()
    {
        // Not while an operation installs or removes the task; a later refresh checks it.
        if (_agentTaskChecked || !_isEnrolled || _operationRunning) return;
        _agentTaskChecked = true;
        try
        {
            // The task is registered again with all of its current settings, not only the ones the check looks at.
            if (await MachineRelay.RepairAgentTaskSettingsAsync())
                AddActivity("Updated the fleet agent task to the current settings.");
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
            AddActivity($"{title} skipped: “{_agentWorkTitle}” is still running.", ActivityKind.Warning);
            return;
        }
        _agentWorkTitle = title;
        _agentWork = RunAsync();

        async Task RunAsync()
        {
            AddActivity($"{title} started. The fleet agent restarts when it is not busy.", ActivityKind.Info);
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
                AddActivity($"{title} completed.", ActivityKind.Success);
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
    /// <param name="keepBusy">
    /// An operation runs on this PC: this PC's card, and cards with a pending action that the operation may have set, are
    /// kept; a PC that came or went waits for the refresh at the end of the operation.
    /// </param>
    private void ApplyMachines(List<MachineInfo> machines, bool keepBusy = false)
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
        if (!sameShape && keepBusy) return;
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
            if (keepBusy && (existing.IsLocal || existing.PendingAction.Length > 0 || existing.Runners.Any(r => r.PendingAction.Length > 0)))
            {
                existing.Touch(incoming);
                continue;
            }
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
    /// <param name="clearOthers">
    /// Clear the pending state of runners without a queued command; false while an operation on this PC runs, since it
    /// shows its own (for example "Move").
    /// </param>
    private void ApplyPendingStates(bool clearOthers = true)
    {
        foreach (var machine in Machines)
        foreach (var runner in machine.Runners)
        {
            var key = RunnerKey(runner);
            if (!_pendingRunnerCommands.TryGetValue(key, out var pending))
            {
                if (clearOthers) runner.SetPending(string.Empty);
                continue;
            }
            var result = machine.CommandResult(pending.CommandId);
            var done = DateTime.UtcNow - pending.QueuedUtc > PendingCommandTimeout
                // A failed start or stop does not change the state, so it ends with the agent's result (shown by ReportRemoteResults).
                || (result != null && MachineRelay.IsFailedResult(result))
                || (pending.Action == "Start" && runner.IsRunning)
                || (pending.Action == "Stop" && !runner.IsRunning)
                || (pending.Action is not ("Start" or "Stop") && result != null);
            if (done) _pendingRunnerCommands.Remove(key);
            runner.SetPending(done ? string.Empty : pending.Action);
        }
    }

    private static string RunnerKey(MachineRunnerStatus runner)
        => $"{runner.MachineKey}|{(string.IsNullOrEmpty(runner.CommandKey) ? runner.RepositoryPath + "/" + runner.AgentName : runner.CommandKey)}";

    // ================= Summary, filter, navigation =================

    private static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

    internal void UpdateSummary()
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
        // Like the other tiles, over the whole fleet, not the filtered view.
        UpdateAttentionTile(machines);

        _allMachines.Update($"{Plural(machines.Count, "PC")}  ·  {Plural(runners.Count, "runner")}");
        ApplyOptionalSections(machines);
        UpdateOutputTabs();
        UpdatePageHeader();
        _tray?.SetTooltip($"Runner Manager — {running} of {Plural(runners.Count, "runner")} running, {busy.Count} busy");
    }

    private void UpdatePageHeader()
    {
        var selected = Machines.FirstOrDefault(m => m.MachineKey == _selectedMachineKey)
                       ?? (IsSinglePc ? Machines.FirstOrDefault() : null);
        PageTitle.Text = selected?.MachineName ?? "All PCs";
        PageSubtitle.Text = _githubChecked && !_githubAuthenticated
            ? "Connect GitHub to add runners and see job status"
            : selected != null
                ? selected.RunnerSummary
                : "GitHub runners on all PCs";
    }

    private bool FilterMachine(object item)
    {
        if (item is not MachineInfo machine) return false;
        if (_selectedMachineKey != null && machine.MachineKey != _selectedMachineKey) return false;
        return ShownWithoutSelection(machine);
    }

    /// <summary>The search and the Needs attention tile show the PC, whatever is selected in the sidebar.</summary>
    private bool ShownWithoutSelection(MachineInfo machine)
        => (!_attentionOnly || FleetAttention.PcNeedsAttention(machine)) && MatchesSearch(machine);

    private bool MatchesSearch(MachineInfo machine)
        => string.IsNullOrWhiteSpace(_filter)
           || machine.MachineName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
           || machine.Runners.Any(r => r.MatchesFilter);

    private void ApplyFilter()
    {
        var searching = !string.IsNullOrWhiteSpace(_filter);
        foreach (var machine in Machines)
        {
            var machineMatch = searching && machine.MachineName.Contains(_filter, StringComparison.OrdinalIgnoreCase);
            var runnerMatch = false;
            var shown = false;
            foreach (var runner in machine.Runners)
            {
                var match = runner.Matches(_filter);
                runnerMatch |= match;
                runner.MatchesFilter = (machineMatch || match) && (!_attentionOnly || FleetAttention.ShowsRunner(machine, runner));
                shown |= runner.MatchesFilter;
            }
            // A match in a runner's own details, or a runner that needs attention, is otherwise hidden in a collapsed card.
            machine.SetOpenForSearch((runnerMatch && searching) || (_attentionOnly && shown));
        }
        // Refreshing the view rebuilds every card, so only do it when the visible set actually changes.
        var visible = MachinesView.Cast<MachineInfo>().Select(m => m.MachineKey);
        var wanted = Machines.Where(FilterMachine).Select(m => m.MachineKey);
        if (!visible.SequenceEqual(wanted)) MachinesView.Refresh();
        var empty = MachinesView.IsEmpty && (searching || _attentionOnly);
        NoResultsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NoResultsText.Text = NoResultsMessage();
    }

    /// <summary>
    /// Why nothing is shown. With one PC selected in the sidebar, the search and the Needs attention tile cover only
    /// that PC: say so, and whether other PCs have something to show.
    /// </summary>
    private string NoResultsMessage()
    {
        var searching = !string.IsNullOrWhiteSpace(_filter);
        if (_selectedMachineKey == null || Machines.FirstOrDefault(m => m.MachineKey == _selectedMachineKey) is not { } selected)
            return !searching ? "Nothing needs attention."
                : _attentionOnly ? $"Nothing that needs attention matches “{_filter}”."
                : $"No runners or PCs match “{_filter}”.";
        var what = !searching ? "needs attention" : _attentionOnly ? $"needs attention and matches “{_filter}”" : $"matches “{_filter}”";
        if (!Machines.Any(m => m != selected && ShownWithoutSelection(m))) return $"Nothing on {selected.MachineName} {what}.";
        return searching
            ? $"Nothing on {selected.MachineName} {what}. Other PCs have matches. Select All PCs to see them."
            : $"Nothing on {selected.MachineName} {what}. Other PCs do. Select All PCs to see them.";
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

    private void RaiseAlert(string message, bool warning, ActivityKind? kind = null)
    {
        AddActivity(message, kind ?? ActivityEntry.Classify(message));
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
        // GitHub gave no answer to the last check (no network, for example): the sign-in it confirmed before still counts.
        var noAnswer = _githubAuthenticated && GitHubCli.SignInUnconfirmed;
        GitHubStatusText.Text = noAnswer ? "No answer from GitHub" : _githubAuthenticated ? "Connected" : WaitingForSignIn ? "Waiting for sign-in"
            : _githubChecked ? "Not connected" : "Checking…";
        GitHubStatusText.ToolTip = noAnswer
            ? "GitHub could not be reached, so the sign-in could not be checked. Runner Manager uses the last sign-in until GitHub answers again."
            : null;
        GitHubStatusText.Foreground = noAnswer ? Palette.Warning
            : _githubAuthenticated ? Palette.Success : _githubChecked ? Palette.Warning : Palette.TextMuted;
        ConnectGitHubButton.Visibility = _githubChecked && !_githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        AvatarText.Text = _githubAuthenticated && _githubLogin.Length > 0 ? _githubLogin[..1].ToUpperInvariant() : "?";
        AvatarBadge.Background = _githubAuthenticated ? Palette.Tint(Palette.Info) : (Brush)FindResource("Bg3Brush");
        ConnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Collapsed : Visibility.Visible;
        ReconnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        DisconnectMenuItem.Visibility = _githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>In a fleet = the fleet agent is installed and this PC has the fleet code (or, managed one way, its access code).</summary>
    private bool InFleet => _isEnrolled && FleetConfig.Current.FleetKey != null;

    private void ApplyEnrollmentStatus()
    {
        var count = Machines.Count;
        FleetHeaderText.Text = count > 1 ? $"FLEET  ·  {count} PCs" : "THIS PC";
        FleetSecureIcon.Visibility = InFleet && count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var managedBy = InFleet ? FleetConfig.Current.ManagedBy : string.Empty;
        AddPcRowText.Text = InFleet ? "Add a PC" : "Connect another PC";
        AddPcRowHint.Text = managedBy.Length > 0 ? $"Managed by {managedBy}'s fleet"
            : InFleet ? "Pair with a 6-digit code" : "Manage several PCs together";
        AddPcRow.ToolTip = managedBy.Length > 0 ? ManagedCannotAddText(managedBy)
            : InFleet ? "Add a new PC to the fleet. It finds this PC on the network, and you confirm the 6-digit code on both PCs."
            : "Create or join a fleet to view and control several PCs from one place.";
    }

    /// <summary>Why "Add a PC" does nothing on a PC that is managed one way.</summary>
    internal static string ManagedCannotAddText(string managedBy)
        => $"{managedBy}'s fleet manages this PC one way, so this PC cannot add PCs. " +
           "To start or join a fleet with this PC, open Settings → Fleet and click Disconnect first.";

    private async void GitHubConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_githubAuthenticated)
        {
            if (IsOperationRunning("Disconnect GitHub")) return;
            if (!DialogWindow.Confirm(this, "Disconnect GitHub?",
                    "The GitHub CLI on this PC is signed out. Until you connect again, Runner Manager does not show jobs, queues and recent runs from GitHub, and cannot add runners. Other PCs in the fleet are still shown.",
                    "Disconnect", danger: true)) return;
            await RunOperationAsync("Disconnect GitHub", async log =>
            {
                await GitHubCli.DisconnectAsync();
                // So the refresh after it does not ask GitHub; the sign-in check that it starts says the same.
                _githubAuthenticated = false;
                _githubLogin = string.Empty;
                ApplyGitHubStatus();
                log("GitHub disconnected.");
            });
            return;
        }
        GitHubCli.LaunchLogin();
        AddActivity("Opened GitHub sign-in. Complete it in the browser or terminal window.");
        WatchGitHubSignIn();
    }

    /// <summary>With many PCs, the list scrolls so that "Add a PC" under it stays in view.</summary>
    private void NavArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The slot, not ActualHeight: an overfull panel is arranged at the size it wants.
        var height = LayoutInformation.GetLayoutSlot(NavArea).Height;
        if (height <= 0) return;
        var others = NavHeader.ActualHeight + NavHeader.Margin.Top + NavHeader.Margin.Bottom
                     + AddPcRow.ActualHeight + AddPcRow.Margin.Top + AddPcRow.Margin.Bottom;
        MachineNav.MaxHeight = Math.Max(0, height - others);
    }

    private void ConnectGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        // The account menu's item also disconnects; this button only connects.
        if (!_githubAuthenticated) GitHubConnect_Click(sender, e);
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
            WatchGitHubSignIn();
            await RefreshAsync(forceAuthCheck: true);
        }
        catch (Exception ex)
        {
            AddActivity($"GitHub reconnect failed: {ex.Message}");
            DialogWindow.Error(this, "Could not reconnect GitHub", ex.Message);
        }
    }

    // Sign-in ends in a browser or console window, at a time the dashboard cannot see. So after the sign-in window
    // opens, check for it every few seconds for a while, and whenever the user comes back to this window.
    private static readonly TimeSpan SignInWatchTime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SignInCheckInterval = TimeSpan.FromSeconds(4);
    private DispatcherTimer? _signInTimer;
    private DateTime _signInWatchUntilUtc = DateTime.MinValue;
    private DateTime _lastSignInCheckUtc = DateTime.MinValue;
    private bool _checkingSignIn;

    private bool WaitingForSignIn => !_githubAuthenticated && DateTime.UtcNow < _signInWatchUntilUtc;

    internal void WatchGitHubSignIn()
    {
        _signInWatchUntilUtc = DateTime.UtcNow + SignInWatchTime;
        if (_signInTimer == null)
        {
            _signInTimer = new DispatcherTimer { Interval = SignInCheckInterval };
            _signInTimer.Tick += async (_, _) => await CheckGitHubSignInAsync();
        }
        _signInTimer.Start();
        ApplyGitHubStatus();
    }

    /// <summary>Refreshes as soon as gh has a sign-in, instead of at the refresh's next GitHub check.</summary>
    private async Task CheckGitHubSignInAsync()
    {
        if (_signInTimer is { IsEnabled: true } && !WaitingForSignIn)
        {
            _signInTimer.Stop();
            ApplyGitHubStatus();
        }
        if (_githubAuthenticated || _checkingSignIn || !_started
            || DateTime.UtcNow - _lastSignInCheckUtc < TimeSpan.FromSeconds(2)) return;
        _checkingSignIn = true;
        _lastSignInCheckUtc = DateTime.UtcNow;
        try
        {
            // A sign-in that it finds refreshes the dashboard.
            StartGitHubCheck();
            if (_gitHubCheck is { } check) await check;
        }
        finally { _checkingSignIn = false; }
    }

    private async void AddRunner_Click(object sender, RoutedEventArgs e)
    {
        // The first sign-in check runs beside the first refresh; until it answers, the sign-in is not known.
        if (!_githubChecked && _gitHubCheck is { } check) await check;
        if (!_githubAuthenticated)
        {
            if (DialogWindow.Show(this, DialogTone.Info, "Connect GitHub first",
                    "Adding a runner requires a GitHub account. Runner Manager uses your GitHub CLI sign-in to request short-lived registration tokens and does not store them.",
                    "Connect GitHub") == "Connect GitHub")
                GitHubConnect_Click(sender, e);
            return;
        }
        if (IsOperationRunning("Add runner")) return;
        // A PC card's button adds on that PC; the toolbar and Ctrl+N on the PC selected in the sidebar.
        var selected = ItemFrom<MachineInfo>(sender) ?? Machines.FirstOrDefault(m => m.MachineKey == _selectedMachineKey);
        var dialog = new AddRunnerWindow(null, Machines, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Setup is not { } setup) return;
        if (OtherPcToAddOn(dialog.TargetPc, Machines) is not { } pc)
        {
            await RunOperationAsync("Add runner", log => RunnerService.CreateRunnerAsync(setup, log));
            return;
        }
        if (!CanSendTo(pc.MachineKey)) return;
        await RunOperationAsync($"Add runner on {pc.MachineName}", async log =>
        {
            log(RemoteAddRunnerNote(pc.MachineName));
            await _fleetActions.CreateRunnerAsync(pc, setup.RepositoryUrl, setup.RunnerName, setup.Labels, log);
            var path = RunnerService.RepositoryPath(setup.RepositoryUrl);
            if (!setup.CheckWorkflows || string.IsNullOrWhiteSpace(path)) return;
            // The workflows are on GitHub, so this PC checks them; the runner works either way.
            log("Checking repository workflows…");
            try { await WorkflowManager.CheckAndUpdateAsync(path, setup.Labels, log); }
            catch (Exception ex) { log($"Warning: the workflow check failed: {ex.Message.Split('\n')[0].Trim()} The runner was created."); }
        });
    }

    /// <summary>
    /// The PC the Add runner window chose, as the dashboard knows it now, when it is another PC; null for this PC.
    /// The window keeps the reading it was opened with, which a refresh may have replaced meanwhile.
    /// </summary>
    internal static MachineInfo? OtherPcToAddOn(MachineInfo? chosen, IEnumerable<MachineInfo> machines)
        => chosen is not { IsLocal: false } ? null : machines.FirstOrDefault(m => m.MachineKey == chosen.MachineKey) ?? chosen;

    /// <summary>The first line of an Add runner on another PC: its fleet agent sends nothing until it is done.</summary>
    internal static string RemoteAddRunnerNote(string pcName)
        => $"{pcName} downloads and sets up the runner. It reports no progress until it is done, which can take several minutes.";

    // ================= Operations =================

    /// <summary>
    /// Runs one operation at a time on this PC, with progress in the status bar and activity.
    /// Returns true when it ran and succeeded; false when it was skipped or failed. Set pending states inside
    /// <paramref name="operation"/>, so nothing shows as pending when the operation is skipped.
    /// </summary>
    private async Task<bool> RunOperationAsync(string title, Func<Action<string>, Task> operation)
    {
        if (IsOperationRunning(title)) return false;
        _operationRunning = true;
        _operationTitle = title;
        var operationId = ++_operationId;
        SetStatus($"{title}…", StatusKind.Busy);
        var succeeded = false;
        try
        {
            AddActivity($"{title} started.", ActivityKind.Info);
            await operation(OperationLog(title, operationId));
            succeeded = true;
            AddActivity($"{title} completed.", ActivityKind.Success);
            if (!IsActive && _settings.Notifications) _tray?.Notify("Runner Manager", $"{title} completed.");
        }
        catch (Exception ex)
        {
            AddActivity($"{title} failed: {ex.Message}", ActivityKind.Error);
            ShowNotice($"{title} failed: {ex.Message}", StatusKind.Error);
            DialogWindow.Error(this, $"{title} failed", ex.Message);
        }
        finally
        {
            _operationRunning = false;
            await RefreshAsync(forceAuthCheck: true);
        }
        return succeeded;
    }

    private int _operationId;

    /// <summary>
    /// Tells the user when an operation on this PC is still running, since only one runs at a time. Called before a
    /// dialog is filled in, so the work is not lost. Returns true when one is running.
    /// </summary>
    private bool IsOperationRunning(string title)
    {
        if (!_operationRunning) return false;
        AddActivity($"{title} skipped: “{_operationTitle}” is still running.", ActivityKind.Warning);
        DialogWindow.Info(this, "Another operation is running",
            $"“{_operationTitle}” is still running on this PC. Try “{title}” again when it is done. Its progress is shown in the status bar.");
        return true;
    }

    /// <summary>Like <see cref="IsOperationRunning"/>, for an action on a runner: only actions on this PC wait for each other.</summary>
    private bool IsOperationRunning(MachineRunnerStatus runner, string title) => runner.IsLocalMachine && IsOperationRunning(title);

    /// <summary>
    /// What an operation logs to: the activity list and the status bar. Operations log from background threads
    /// (Task.Run), where touching the window throws and would stop the operation halfway, for example after a runner was
    /// unregistered but before its files were deleted. So the messages go to the window's thread.
    /// </summary>
    internal Action<string> OperationLog(string title, int operationId) => message => OnUiThread(() =>
    {
        AddActivity(message);
        // A late message must not turn the status bar back to busy after the operation ended.
        if (_operationRunning && _operationId == operationId) SetStatus($"{title}: {message}", StatusKind.Busy);
    });

    /// <summary>Runs window work now when on the window's thread, or queues it there from any other thread.</summary>
    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
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

    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(30);
    private DateTime _noticeUntilUtc = DateTime.MinValue;

    /// <summary>Shows a result in the status bar and keeps it there for a while, instead of "Ready" on the next refresh.</summary>
    private void ShowNotice(string text, StatusKind kind)
    {
        SetStatus(text, kind);
        _noticeUntilUtc = DateTime.UtcNow + NoticeTime;
    }

    private async Task QueueRunnerCommandAsync(MachineRunnerStatus runner, string action, string title,
        string runnerName = "", string[]? labels = null, string value = "")
    {
        var key = RunnerKey(runner);
        if (!CanSendTo(runner.MachineKey)) return;
        var what = $"{title} on {runner.MachineName}";
        try
        {
            MarkUserAction(key);
            runner.SetPending(action);
            var id = await MachineRelay.SendCommandAsync(runner.MachineKey, "runner", runner.CommandKey, action, runnerName, labels, value);
            _pendingRunnerCommands[key] = new PendingCommand(id, action, DateTime.UtcNow);
            AwaitResult(id, runner.MachineKey, runner.MachineName, what);
            AddActivity($"{what}: sent. Waiting for its fleet agent.", ActivityKind.Info);
            ShowNotice($"{what}: sent. Waiting for its fleet agent.", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            runner.SetPending(string.Empty);
            ReportSendFailure(what, runner.MachineName, ex);
        }
    }

    /// <summary>A command could not be sent to another PC: the user just asked for it, so say so plainly.</summary>
    private void ReportSendFailure(string what, string machineName, Exception ex)
    {
        AddActivity($"{what} failed: {ex.Message}");
        ShowNotice($"{what} failed: {ex.Message}", StatusKind.Error);
        DialogWindow.Error(this, $"Could not reach {machineName}", $"“{what}” was not sent. {ex.Message}");
    }

    // ================= Results of commands sent to other PCs =================

    /// <summary>How long a PC may take to report a result after it last showed the command as running.</summary>
    internal static readonly TimeSpan RemoteResultWait = TimeSpan.FromSeconds(75);
    /// <summary>After this, a command is forgotten without a message (its PC may be offline for a long time).</summary>
    private static readonly TimeSpan RemoteResultMemory = TimeSpan.FromHours(1);

    internal sealed class AwaitedCommand
    {
        public string CommandId { get; init; } = string.Empty;
        public string MachineKey { get; init; } = string.Empty;
        public string MachineName { get; init; } = string.Empty;
        /// <summary>What the command does, for messages: "Start build-01 on BUILD-01".</summary>
        public string What { get; init; } = string.Empty;
        public DateTime QueuedUtc { get; init; }
        /// <summary>When the command was last known to be queued or running on its PC.</summary>
        public DateTime ActiveUtc { get; set; }
    }

    internal enum RemoteOutcome { Waiting, Succeeded, Failed, NoAnswer, Forgotten }

    private readonly Dictionary<string, AwaitedCommand> _awaitedCommands = new(StringComparer.OrdinalIgnoreCase);

    private void AwaitResult(string commandId, string machineKey, string machineName, string what)
    {
        if (string.IsNullOrEmpty(commandId)) return;
        _awaitedCommands[commandId] = new AwaitedCommand
        {
            CommandId = commandId, MachineKey = machineKey, MachineName = machineName, What = what,
            QueuedUtc = DateTime.UtcNow, ActiveUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// How a command sent to another PC ended, as far as its PC's status shows, with the agent's result or the reason it
    /// failed.
    /// </summary>
    internal static (RemoteOutcome Outcome, string Detail) CheckRemoteCommand(MachineInfo? machine, AwaitedCommand command, DateTime nowUtc)
    {
        if (machine?.CommandResult(command.CommandId) is { } result)
        {
            if (!MachineRelay.IsFailedResult(result)) return (RemoteOutcome.Succeeded, result.Trim().TrimEnd('.'));
            var reason = result.IndexOf("failed: ", StringComparison.OrdinalIgnoreCase) is var at and >= 0 ? result[(at + 8)..] : result;
            return (RemoteOutcome.Failed, reason.Trim());
        }
        if (machine != null && machine.IsRunningCommand(command.CommandId)) command.ActiveUtc = nowUtc;
        if (nowUtc - command.QueuedUtc > RemoteResultMemory) return (RemoteOutcome.Forgotten, string.Empty);
        // Only agents that list the commands they run (protocol 12) show that a command was lost; an offline PC may still
        // report the result once it is back.
        if (machine is { IsOnline: true, AgentProtocolVersion: >= 12 } && nowUtc - command.ActiveUtc > RemoteResultWait)
            return (RemoteOutcome.NoAnswer, string.Empty);
        return (RemoteOutcome.Waiting, string.Empty);
    }

    /// <summary>Reports how commands sent to other PCs ended: failures in the status bar, as alerts and in the activity list.</summary>
    private void ReportRemoteResults()
    {
        foreach (var command in _awaitedCommands.Values.ToList())
        {
            var (outcome, detail) = CheckRemoteCommand(Machines.FirstOrDefault(m => m.MachineKey == command.MachineKey), command, DateTime.UtcNow);
            if (outcome == RemoteOutcome.Waiting) continue;
            _awaitedCommands.Remove(command.CommandId);
            switch (outcome)
            {
                case RemoteOutcome.Succeeded:
                    AddActivity($"{command.What}: {detail}.", ActivityKind.Success);
                    break;
                case RemoteOutcome.Failed:
                    RaiseAlert($"{command.What} failed: {detail}", warning: true, ActivityKind.Error);
                    ShowNotice($"{command.What} failed: {detail}", StatusKind.Error);
                    break;
                case RemoteOutcome.NoAnswer:
                    var text = $"{command.What}: the fleet agent on {command.MachineName} did not report a result. Check its fleet agent log.";
                    AddActivity(text, ActivityKind.Warning);
                    ShowNotice(text, StatusKind.Warning);
                    break;
            }
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
            await QueueRunnerCommandAsync(runner, action, title, runnerName, labels, value);
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

    /// <summary>
    /// The click of this flat button itself. A button inside it, such as a card's update badge, its ⋯ menu or a run's
    /// Re-run, raises its own Click, which reaches the outer button too.
    /// </summary>
    internal static bool IsOwnClick(object sender, RoutedEventArgs e) => ReferenceEquals(e.OriginalSource, sender);

    private void RunnerRow_Click(object sender, RoutedEventArgs e)
    {
        if (IsOwnClick(sender, e) && ItemFrom<MachineRunnerStatus>(sender) is { } runner) ToggleRunner(runner);
    }

    /// <summary>A job of the queue or a recent run: opens it on GitHub.</summary>
    private void RunRow_Click(object sender, RoutedEventArgs e)
    {
        if (IsOwnClick(sender, e) && sender is FrameworkElement { Tag: string { Length: > 0 } url }) OpenUrl(url);
    }

    // Enter or Space on a focused link does what a click does.
    private static bool IsActivation(object sender, KeyEventArgs e)
        => e.Key is Key.Enter or Key.Space && Keyboard.Modifiers == ModifierKeys.None && ReferenceEquals(e.OriginalSource, sender);

    private void Link_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsActivation(sender, e) || sender is not FrameworkElement { Tag: string { Length: > 0 } url }) return;
        OpenUrl(url);
        e.Handled = true;
    }

    private void ToggleRunner(MachineRunnerStatus runner)
    {
        runner.IsExpanded = !runner.IsExpanded;
        _machineRunnerExpansionStates[RunnerKey(runner)] = runner.IsExpanded;
        // Opening a runner that is running a job shows its output in the bottom panel once it arrives.
        if (runner.IsExpanded && runner.IsBusy) _selectOutputFor = RunnerKey(runner);
        ApplyLiveJob(runner);
        UpdateOutputTabs();
        if (runner.IsExpanded && runner.IsBusy) _ = RefreshLiveJobsAsync();
    }

    private void QueueHeader_Click(object sender, RoutedEventArgs e)
    {
        _settings.QueueCollapsed = MachineRunnerStatus.QueueCollapsed = !MachineRunnerStatus.QueueCollapsed;
        _settings.Save();
        foreach (var runner in Machines.SelectMany(m => m.Runners)) runner.Refresh();
    }

    private void RecentRunsHeader_Click(object sender, RoutedEventArgs e)
    {
        _settings.RecentRunsCollapsed = MachineRunnerStatus.RecentRunsCollapsed = !MachineRunnerStatus.RecentRunsCollapsed;
        _settings.Save();
        foreach (var runner in Machines.SelectMany(m => m.Runners)) runner.Refresh();
    }

    private void MachineHeader_Click(object sender, RoutedEventArgs e)
    {
        // The update badge and the ⋯ menu keep working without collapsing the card.
        if (IsOwnClick(sender, e) && ItemFrom<MachineInfo>(sender) is { } machine) ToggleCollapsed(machine);
    }

    private void ToggleCollapsed(MachineInfo machine)
    {
        var collapsed = machine.ToggleCollapsed();
        _settings.CollapsedPcs.RemoveAll(n => n.Equals(machine.MachineName, StringComparison.OrdinalIgnoreCase));
        if (collapsed) _settings.CollapsedPcs.Add(machine.MachineName);
        _settings.Save();
    }

    private async void RunnerPower_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { } runner) return;
        var action = runner.IsRunning ? "Stop" : "Start";
        if (IsOperationRunning(runner, $"{action} {runner.TitleText}")) return;
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
        if (IsOperationRunning(runner, $"Restart {runner.TitleText}")) return;
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
        if (IsOperationRunning(runner, $"Edit {runner.TitleText}")) return;
        // Only a runner on this PC can lose every custom label: the fleet's Edit command has no field for it.
        var dialog = new EditRunnerWindow(runner, canClearLabels: runner.IsLocalMachine) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Edit == null) return;
        var edit = dialog.Edit;
        await RunRunnerActionAsync(runner, "Edit", $"Edit {runner.TitleText}",
            log => RunnerService.EditRunnerAsync(runner.CommandKey, edit.RunnerName, edit.Labels, log, clearLabels: edit.ClearLabels),
            edit.RunnerName, edit.Labels, showLocalPending: false);
    }

    private async void RunnerRepair_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender)?.LocalRunner is not { } runner) return;
        if (IsOperationRunning($"Repair {runner.AgentName}")) return;
        if (!DialogWindow.Confirm(this, $"Repair {runner.AgentName}?",
                "The runner is unregistered and registered again with the same name and labels. It is then installed as a Windows service.",
                "Repair")) return;
        await RunOperationAsync($"Repair {runner.AgentName}", log => RunnerService.RepairRunnerAsync(runner, log));
    }

    private async void RunnerWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender)?.LocalRunner is not { } runner) return;
        if (IsOperationRunning("Check workflows")) return;
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
        if (!InFleet || IsAgentWorkRunning() || IsOperationRunning(LeaveFleetTitle)) return;
        var others = Machines.Where(m => !m.IsLocal && !m.IsManaged).Select(m => m.MachineName).ToList();
        if (!DialogWindow.Confirm(this, $"Remove {Environment.MachineName} from the fleet?",
                LeaveFleetMessage(others, managed: FleetConfig.Current.ManagedBy.Length > 0),
                "Leave fleet", danger: true)) return;
        await RunOperationAsync(LeaveFleetTitle, async log =>
        {
            // A fleet read that started before could save a PC again after the list is cleared.
            await WaitForFleetReadAsync();
            var told = await MachineRelay.LeaveFleetAsync(log);
            _isEnrolled = false;
            _agentTaskChecked = false;
            log(told.Count > 0 ? $"{Environment.MachineName} left the fleet. {string.Join(", ", told)} removed it."
                : $"{Environment.MachineName} left the fleet.");
        });
        ApplyEnrollmentStatus();
    }

    private async void InstallAgent_Click(object sender, RoutedEventArgs e) => await SetUpFleetAsync(FleetStart.Ask);

    /// <summary>What leaving the fleet does. A PC managed one way has its own access code instead of the fleet code.</summary>
    internal static string LeaveFleetMessage(IReadOnlyCollection<string> others, bool managed)
        => (others.Count > 0 ? $"{string.Join(", ", others)} will forget this PC. " : string.Empty)
           + (managed ? "The fleet agent and this PC's access code are removed from this PC. " : "The fleet agent and the fleet code are removed from this PC. ")
           + "Runners keep running. You can add this PC again later.";

    private enum FleetStart { Ask, New, Join }

    /// <summary>Joins or starts a fleet (from the sidebar, or right after setup), then installs the agent.</summary>
    private async Task SetUpFleetAsync(FleetStart start)
    {
        if (await AgentOwnership.OtherUserAsync() is { } owner)
        {
            DialogWindow.Info(this, "Another Windows user runs the fleet agent", AgentOwnership.Describe(owner));
            return;
        }
        if (IsAgentWorkRunning() || IsOperationRunning(JoinFleetTitle)) return;
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
            else if (join.PairingKey != null && join.Managed)
            {
                // Its own access code: the fleet can reach this PC, but this PC cannot reach the fleet, so it does not list the host.
                FleetConfig.BecomeManaged(join.PairingKey, join.HostName);
                AddActivity($"{join.HostName}'s fleet manages this PC one way now. It can view and control this PC; this PC cannot see its PCs.");
            }
            else if (join.PairingKey != null)
            {
                FleetConfig.SetFleetKey(join.PairingKey);
                if (join.HostName.Length > 0) MachineRelay.AddKnownPeer(join.HostName, join.HostAddress);
                AddActivity(join.HostName.Length > 0 ? $"Joined the fleet through {join.HostName}." : "Joined the fleet with the fleet code.");
            }
            else return;
        }
        await RunOperationAsync(JoinFleetTitle, async log =>
        {
            await WaitForFleetReadAsync();
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
        var config = FleetConfig.Current;
        if (config.FleetKey is not { } key) return;
        // A PC managed one way has only its access code. Pairing with it would hand that code out as a fleet code.
        if (config.ManagedBy.Length > 0)
        {
            DialogWindow.Info(this, "This PC is managed one way", ManagedCannotAddText(config.ManagedBy));
            return;
        }
        var window = new PairingHostWindow(key) { Owner = this };
        window.ShowDialog();
        foreach (var name in window.JoinedPcs) AddActivity($"{name} joined the fleet.");
        foreach (var name in window.ManagedPcs) AddActivity($"{name} is managed one way. It cannot see or control the fleet's PCs.");
        _forceDiscovery = true;
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await ManualRefreshAsync();

    private async Task ManualRefreshAsync()
    {
        if (_operationRunning || !RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        _noticeUntilUtc = DateTime.MinValue;
        SetStatus("Refreshing…", StatusKind.Busy);
        try
        {
            // GitHub is asked again at once, so the user does not wait out a pause after fixing its cause.
            GitHubCli.ForgetProblems();
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
        // Back to how it was before it was minimized, which may be maximized.
        if (WindowState == WindowState.Minimized) WindowState = _shownState;
        // A screen may have been unplugged while the window was hidden.
        WindowPlacement.EnsureOnScreen(this);
        Activate();
    }

    /// <summary>The window's state when it was last not minimized.</summary>
    private WindowState _shownState;
    /// <summary>The window's place and size when it was last neither minimized nor maximized.</summary>
    private Rect _normalBounds = Rect.Empty;

    private void TrackPlacement()
    {
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _shownState = WindowState;
            // A window saved as maximized opens on a screen that is there; its normal place may be on a screen that
            // is gone. Windows moves it there when it is restored, so it is fitted then, once the move is done.
            if (WindowState == WindowState.Normal)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => WindowPlacement.EnsureOnScreen(this));
        };
        SizeChanged += (_, _) => NoteNormalBounds();
        LocationChanged += (_, _) => NoteNormalBounds();
        // Fit the window on its screen when it first shows (from the tray, ShowFromTray does it).
        ContentRendered += (_, _) => WindowPlacement.EnsureOnScreen(this);
    }

    private void NoteNormalBounds()
    {
        if (WindowState == WindowState.Normal && IsVisible) _normalBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
    }

    private void SaveWindowPlacement()
    {
        if (_normalBounds.IsEmpty || _normalBounds.Width <= 0 || _normalBounds.Height <= 0) return;
        _settings.MainWindowPlacement = new SavedWindowPlacement
        {
            Left = _normalBounds.Left,
            Top = _normalBounds.Top,
            Width = _normalBounds.Width,
            Height = _normalBounds.Height,
            Maximized = (WindowState == WindowState.Minimized ? _shownState : WindowState) == WindowState.Maximized
        };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exiting)
        {
            e.Cancel = true;
            SaveWindowPlacement();
            _settings.Save();
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
        SaveWindowPlacement();
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
        // Only Ctrl on its own: AltGr counts as Ctrl+Alt, and with it the same keys type characters such as @ or {.
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
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
        else if (NavShortcutIndex(e.Key, Keyboard.Modifiers) is { } index)
        {
            if (index < NavItems.Count) MachineNav.SelectedIndex = index;
            e.Handled = true;
        }
    }

    /// <summary>The sidebar entry that Ctrl and a digit, on the main keys or the number pad, selects (0 is all PCs); otherwise null.</summary>
    internal static int? NavShortcutIndex(Key key, ModifierKeys modifiers)
        => modifiers != ModifierKeys.Control ? null
            : key is >= Key.D0 and <= Key.D9 ? key - Key.D0
            : key is >= Key.NumPad0 and <= Key.NumPad9 ? key - Key.NumPad0
            : null;

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

    /// <summary>
    /// The bottom panel keeps the height it was given, but never takes more than leaves the cards their minimum height,
    /// for example after the window was made smaller. Also called when the window or its header changes size.
    /// </summary>
    private void ContentArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The height the window gives the content. Rows that want more make the grid itself taller (and cut off), so
        // its own height cannot be used.
        if (ContentArea.Parent is not FrameworkElement root) return;
        var height = LayoutInformation.GetLayoutSlot(root).Height;
        if (height <= 0) return;
        var others = ContentArea.RowDefinitions.Where(r => r != CardsRow && r != ActivityRow).Sum(r => r.ActualHeight);
        ActivityRow.MaxHeight = Math.Max(ActivityRow.MinHeight, height - others - CardsRow.MinHeight);
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
            if (SelectedOutput is { } output)
            {
                Clipboard.SetText(OutputBox.SelectionLength > 0 ? OutputBox.SelectedText : output.Text.Replace("\n", Environment.NewLine));
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

    /// <summary>Adds a line to the activity list, with an icon chosen from its words.</summary>
    public void AddActivity(string message) => AddActivity(message, ActivityEntry.Classify(message));

    /// <summary>
    /// Adds a line whose kind is known. Words alone mislead: "Turn off auto-recovery completed" is a success, and an
    /// agent's "Stopped build-01" answers a stop the user asked for.
    /// </summary>
    public void AddActivity(string message, ActivityKind kind)
    {
        if (!Dispatcher.CheckAccess())
        {
            // Queued, not waited for: a background step must not wait for a busy window.
            Dispatcher.BeginInvoke(() => AddActivity(message, kind));
            return;
        }
        ActivityLog.Insert(0, new ActivityEntry { Message = message, Kind = kind });
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
    /// <summary>What a screen reader says for the sidebar entry.</summary>
    public override string ToString() => $"All PCs, {Subtitle}";

    public void Update(string subtitle)
    {
        if (Subtitle == subtitle) return;
        Subtitle = subtitle;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
    }
}
