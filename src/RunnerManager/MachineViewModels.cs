using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace RunnerManager;

public sealed class MachineRunnerStatus : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _matchesFilter = true;
    private string _pendingAction = string.Empty;
    private RunnerInfo? _localRunner;
    private IReadOnlyList<WorkflowRunSummary> _recentRuns = Array.Empty<WorkflowRunSummary>();
    private LiveJobView? _liveJob;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Latest GitHub Actions runner release, set by the dashboard for "update available" hints.</summary>
    public static string LatestRunnerVersion { get; set; } = string.Empty;

    [JsonIgnore] public string MachineKey { get; set; } = string.Empty;
    [JsonIgnore] public string MachineName { get; set; } = string.Empty;
    [JsonIgnore] public int AgentProtocolVersion { get; set; }
    [JsonIgnore] public bool IsLocalMachine { get; set; }
    [JsonIgnore] public bool MachineOnline { get; set; }
    public string ServiceName { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    /// <summary>Running from a console window (run.cmd) rather than as a Windows service.</summary>
    public bool IsInteractive { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    /// <summary>"repos/owner/repo" or "orgs/name".</summary>
    public string Scope { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public long AgentId { get; init; }
    /// <summary>Labels besides self-hosted, Windows and X64 (empty from older agents, or when GitHub could not be read).</summary>
    public string[] CustomLabels { get; init; } = Array.Empty<string>();
    /// <summary>Runner group, ephemeral and the other registration settings, kept when the runner moves (null from older agents).</summary>
    public RunnerRegistrationOptions? RegistrationOptions { get; init; }
    public bool IsRunning { get; init; }
    public string ActivityText { get; init; } = string.Empty;
    public bool ServiceInstalled { get; init; }
    /// <summary>The old copy of a runner that was moved to another PC (false from agents before protocol 13).</summary>
    public bool MovedAway { get; init; }
    /// <summary>What starts the runner after a restart (a <see cref="RunnerStart"/> value; empty from agents before protocol 13).</summary>
    public string StartsAt { get; init; } = RunnerStart.Unknown;
    public string RunnerVersion { get; init; } = string.Empty;
    public long? WorkFolderBytes { get; init; }
    public bool ManuallyStopped { get; init; }

    [JsonIgnore] public bool IsBusy => IsRunning && ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase);
    /// <summary>How commands address this runner: its service name, or its folder when it has no service.</summary>
    [JsonIgnore] public string CommandKey => string.IsNullOrEmpty(ServiceName) ? InstallPath : ServiceName;
    /// <summary>Delete, move and convert need protocol 9 agents on other PCs.</summary>
    [JsonIgnore] private bool SupportsLifecycle => IsLocalMachine || (MachineOnline && AgentProtocolVersion >= 9);
    [JsonIgnore] public bool CanRemove => SupportsLifecycle && string.IsNullOrEmpty(_pendingAction) && !IsBusy
        && !string.IsNullOrEmpty(CommandKey);
    [JsonIgnore] public bool CanMove => CanRemove && !string.IsNullOrWhiteSpace(Scope) && (ServiceInstalled || IsInteractive);
    /// <summary>A console runner that is not running now, but a startup entry opens it at sign-in (or did, until it was turned off).</summary>
    [JsonIgnore] private bool IsStoppedConsole => !IsInteractive && !ServiceInstalled
        && StartsAt is RunnerStart.SignIn or RunnerStart.SignInShared or RunnerStart.Off;
    [JsonIgnore] public bool CanConvert => (IsInteractive || IsStoppedConsole) && !ServiceInstalled && SupportsLifecycle && string.IsNullOrEmpty(_pendingAction) && !IsBusy;
    [JsonIgnore] public Visibility ConvertVisibility => (IsInteractive || IsStoppedConsole) && !ServiceInstalled ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Start with Windows (services) or at sign-in (console runners) can be turned on and off from protocol 13.</summary>
    /// <summary>Started by a console window's startup entries rather than by its Windows service.</summary>
    [JsonIgnore] private bool StartsFromConsole => !ServiceInstalled || IsInteractive;
    [JsonIgnore] public bool CanChangeStartup => (IsLocalMachine || (MachineOnline && AgentProtocolVersion >= 13))
        && string.IsNullOrEmpty(_pendingAction) && !string.IsNullOrEmpty(CommandKey) && !MovedAway
        && (StartsFromConsole ? StartsAt is RunnerStart.SignIn or RunnerStart.Off
            : StartsAt is RunnerStart.WithWindows or RunnerStart.Off or RunnerStart.Disabled);
    [JsonIgnore] public bool StartsAutomatically => StartsAt is RunnerStart.WithWindows or RunnerStart.SignIn or RunnerStart.SignInShared;
    [JsonIgnore] public string StartupHeader => StartsFromConsole ? "Start when you sign in" : "Start with Windows";
    [JsonIgnore] public string StartupTip => !StartsFromConsole
        ? "When this is off, the runner stays stopped after a restart until you start it. Turning it off does not stop it now."
        : StartsAt == RunnerStart.SignInShared
            ? "A startup entry that also starts other runners opens this runner. Remove it from that entry by hand to change this."
        : "When this is off, the runner's startup entries are moved aside, so it does not open when you sign in. Turning it off does not close it now.";
    [JsonIgnore]
    public string StartsAtText => StartsAt switch
    {
        RunnerStart.WithWindows => "With Windows",
        RunnerStart.SignIn => "When you sign in",
        RunnerStart.SignInShared => "When you sign in, from a startup entry that also starts other runners",
        RunnerStart.Off => StartsFromConsole ? "Not automatically. Its startup entries are turned off." : "Not automatically. Start it from Runner Manager.",
        RunnerStart.Disabled => "Never. Its Windows service is disabled.",
        _ => !IsLocalMachine && AgentProtocolVersion < 13 ? "Update the fleet agent to see this" : "Nothing starts it automatically"
    };
    [JsonIgnore] public Visibility PowerVisibility => IsInteractive ? Visibility.Collapsed : Visibility.Visible;
    [JsonIgnore] public Visibility ConsoleNoteVisibility => IsExpanded && IsInteractive ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public bool IsOrgRunner => Scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] private string OrgName => IsOrgRunner ? Scope[5..] : string.Empty;
    [JsonIgnore] public string PendingAction => _pendingAction;
    [JsonIgnore] public string PowerButtonText => IsRunning ? "Stop" : "Start";
    [JsonIgnore] public string PowerGlyph => IsRunning ? "" : "";
    [JsonIgnore]
    public string StatusText => _pendingAction switch
    {
        "Start" => "Starting…",
        "Stop" => "Stopping…",
        "Restart" => "Restarting…",
        "Edit" => "Updating…",
        "CleanWork" => "Cleaning…",
        "Remove" => "Removing…",
        "Move" => "Moving…",
        "Convert" => "Converting…",
        "Startup" => "Updating…",
        _ => IsInteractive ? (IsBusy ? "Busy · console" : "Console")
            : IsStoppedConsole ? "Console · stopped"
            : !ServiceInstalled ? "No service" : IsBusy ? "Busy" : IsRunning ? "Idle" : "Stopped"
    };
    [JsonIgnore]
    public Brush StatusBrush => !string.IsNullOrEmpty(_pendingAction) ? Palette.Warning
        : !ServiceInstalled ? Palette.Warning
        : IsBusy ? Palette.Info
        : IsRunning ? Palette.Success
        : Palette.Danger;
    [JsonIgnore] public Brush StatusTint => Palette.Tint(StatusBrush);
    [JsonIgnore] public string StatusTip => IsInteractive && ServiceInstalled
        ? "Runs in a console window in your session. Its Windows service is not used; starting it would run the runner twice."
        : IsInteractive
        ? "Runs in a console window in your session, so its jobs can use your desktop. It goes offline when the window closes or you sign out."
        : IsStoppedConsole && StartsAt == RunnerStart.Off
            ? "Runs in a console window, which is set not to open when you sign in. Turn on “Start when you sign in” and sign in again, or use “Run as Windows service”."
        : IsStoppedConsole ? "Runs in a console window that opens when you sign in. It is not running now."
        : !ServiceInstalled ? "This runner is configured but has no Windows service. Use “Repair Windows service” to install one."
        : IsBusy ? "Running a job."
        : IsRunning ? "Online and waiting for jobs."
        : "The Windows service is stopped. GitHub does not send it jobs.";
    // Not while it runs from a console window: starting or restarting its service would run it twice.
    [JsonIgnore] public bool CanControl => ServiceInstalled && !IsInteractive && !string.IsNullOrWhiteSpace(ServiceName)
        && string.IsNullOrEmpty(_pendingAction) && (IsLocalMachine || MachineOnline);
    [JsonIgnore] public bool CanEdit => (IsLocalMachine || AgentProtocolVersion >= 2) && CanControl && !IsBusy;
    [JsonIgnore] public bool CanCleanWork => (IsLocalMachine || AgentProtocolVersion >= 9) && CanControl && !IsBusy;
    [JsonIgnore] public bool CanViewLog => IsLocalMachine || (MachineOnline && AgentProtocolVersion >= 9);
    [JsonIgnore] public string ServiceText => ServiceInstalled && IsInteractive ? $"{ServiceName} (not used: the runner runs in a console window)"
        : ServiceInstalled ? ServiceName
        : IsInteractive || IsStoppedConsole ? "None. Runs in a console window that closes when you sign out." : "Not installed";
    [JsonIgnore] public string TitleText => string.IsNullOrWhiteSpace(AgentName) ? DisplayName : AgentName;
    [JsonIgnore] public string RepositoryText => IsOrgRunner ? $"{OrgName}  ·  organization runner"
        : string.IsNullOrWhiteSpace(RepositoryPath) ? "Repository unknown" : RepositoryPath;
    [JsonIgnore] public string RepositoryUrl => IsOrgRunner ? $"https://github.com/{OrgName}"
        : string.IsNullOrWhiteSpace(RepositoryPath) ? string.Empty : $"https://github.com/{RepositoryPath}";
    [JsonIgnore] public string RunnersSettingsUrl => IsOrgRunner ? $"https://github.com/organizations/{OrgName}/settings/actions/runners"
        : string.IsNullOrWhiteSpace(RepositoryPath) ? string.Empty : $"{RepositoryUrl}/settings/actions/runners";
    [JsonIgnore] public string ActionsUrl => string.IsNullOrWhiteSpace(RepositoryPath) ? string.Empty : $"{RepositoryUrl}/actions";
    [JsonIgnore] public Visibility RepoOnlyVisibility => string.IsNullOrWhiteSpace(RepositoryPath) ? Visibility.Collapsed : Visibility.Visible;
    [JsonIgnore] public Visibility LocalVisibility => IsLocalMachine ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility RepairVisibility => IsLocalMachine && !ServiceInstalled && !IsInteractive && !IsStoppedConsole ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility RowVisibility => _matchesFilter ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility LocalDetailsVisibility => IsExpanded && LocalRunner != null ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility ManuallyStoppedVisibility => ManuallyStopped && !IsRunning ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public string ExpandGlyph => IsExpanded ? "" : "";
    [JsonIgnore] public string LabelsText => LocalRunner is { CustomLabels.Length: > 0 } local
        ? string.Join("  ·  ", local.CustomLabels) : $"self-hosted, Windows, {(IsLocalMachine ? RunnerService.ArchitectureLabel : "X64")}";
    [JsonIgnore] public bool HasCurrentJob => !string.IsNullOrWhiteSpace(LocalRunner?.CurrentJobUrl);
    // Runners on this PC use the dashboard's own readings, so the details are complete even before the agent is updated.
    [JsonIgnore] private long? EffectiveWorkFolderBytes => LocalRunner?.WorkFolderBytes ?? WorkFolderBytes;
    [JsonIgnore] private string EffectiveRunnerVersion => string.IsNullOrWhiteSpace(LocalRunner?.RunnerVersion) ? RunnerVersion : LocalRunner!.RunnerVersion;
    [JsonIgnore] public string WorkFolderText => EffectiveWorkFolderBytes == null ? "Not measured yet" : SystemHealth.FormatBytes(EffectiveWorkFolderBytes);
    [JsonIgnore] public Brush WorkFolderBrush => EffectiveWorkFolderBytes > 20L * 1024 * 1024 * 1024 ? Palette.Warning : Palette.Text;
    [JsonIgnore]
    public string RunnerVersionText
    {
        get
        {
            var version = EffectiveRunnerVersion;
            if (string.IsNullOrWhiteSpace(version)) return AgentProtocolVersion >= 9 || IsLocalMachine ? "Unknown" : "Update the fleet agent to see it";
            return AppUpdater.IsNewer(LatestRunnerVersion, version)
                ? $"{version}  ·  {LatestRunnerVersion} available (installs automatically when idle)"
                : version;
        }
    }

    [JsonIgnore]
    public IReadOnlyList<WorkflowRunSummary> RecentRuns
    {
        get => _recentRuns;
        set
        {
            if (ReferenceEquals(_recentRuns, value)) return;
            _recentRuns = value;
            Notify(nameof(RecentRuns));
            Notify(nameof(RecentRunsVisibility));
            Notify(nameof(NoJobsVisibility));
        }
    }
    [JsonIgnore] public Visibility RecentRunsVisibility => IsExpanded && _recentRuns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The running job, its steps, output and processes; set by the dashboard while the runner is busy.</summary>
    [JsonIgnore]
    public LiveJobView? LiveJob
    {
        get => _liveJob;
        set
        {
            if (ReferenceEquals(_liveJob, value)) return;
            _liveJob = value;
            Notify(nameof(LiveJob));
            Notify(nameof(LiveJobVisibility));
            Notify(nameof(JobLineVisibility));
            Notify(nameof(NoJobsVisibility));
        }
    }
    [JsonIgnore] public Visibility LiveJobVisibility => IsExpanded && IsBusy && _liveJob != null ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility JobLineVisibility => IsBusy && !string.IsNullOrEmpty(_liveJob?.Summary) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The details' left column has no job, runs or note to show.</summary>
    [JsonIgnore] public Visibility NoJobsVisibility => IsExpanded && LiveJobVisibility != Visibility.Visible
        && RecentRunsVisibility != Visibility.Visible && ConsoleNoteVisibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The live output shown in the dashboard's bottom panel while the runner is open (empty otherwise).</summary>
    [JsonIgnore] public string OutputText => LiveJobVisibility == Visibility.Visible ? _liveJob?.Output ?? string.Empty : string.Empty;

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify();
        }
    }

    [JsonIgnore]
    public bool MatchesFilter
    {
        get => _matchesFilter;
        set
        {
            if (_matchesFilter == value) return;
            _matchesFilter = value;
            Notify(nameof(RowVisibility));
        }
    }

    /// <summary>The full local runner record when this runner lives on this PC (enables Logs/Repair/Remove and GitHub job details).</summary>
    [JsonIgnore]
    public RunnerInfo? LocalRunner
    {
        get => _localRunner;
        set
        {
            if (ReferenceEquals(_localRunner, value)) return;
            _localRunner = value;
            Notify();
        }
    }

    public void SetPending(string action)
    {
        if (_pendingAction == action) return;
        _pendingAction = action;
        Notify();
    }

    /// <summary>Re-evaluates every binding (used when machine reachability or the theme changes).</summary>
    public void Refresh() => Notify();

    public bool Matches(string filter)
        => string.IsNullOrWhiteSpace(filter)
           || TitleText.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || RepositoryText.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (LocalRunner?.CustomLabels.Any(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false);

    private void Notify(string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}

public sealed class MachineInfo : INotifyPropertyChanged
{
    private string _pendingAction = string.Empty;
    private DateTime _updatedUtc;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Newest Runner Manager release, set by the dashboard.</summary>
    public static string LatestAppVersion { get; set; } = string.Empty;
    /// <summary>PCs (by name) whose cards are collapsed to their header, set by the dashboard from its settings.</summary>
    public static HashSet<string> CollapsedPcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string MachineKey { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    /// <summary>The PC's own ID; empty for older agents and for PCs that never answered.</summary>
    public string MachineId { get; init; } = string.Empty;
    public int AgentProtocolVersion { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get => _updatedUtc; init => _updatedUtc = value; }
    public bool FleetAgentRunning { get; init; }
    public string LastCommandId { get; init; } = string.Empty;
    public string LastCommandResult { get; init; } = string.Empty;
    /// <summary>Results of the agent's last commands (protocol 12).</summary>
    public FleetCommandResult[] RecentCommands { get; init; } = Array.Empty<FleetCommandResult>();
    /// <summary>Commands queued or running on the agent (protocol 12).</summary>
    public string[] PendingCommandIds { get; init; } = Array.Empty<string>();
    /// <summary>The agent is running a command or an AI assistant call (protocol 12).</summary>
    public bool Busy { get; init; }

    /// <summary>
    /// Whether the agent reported a result for a command. Several commands can finish between two refreshes, so the
    /// recent results are checked, not only the last command.
    /// </summary>
    public bool HasFinished(string commandId)
        => LastCommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase)
           || RecentCommands.Any(r => r.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
    private int _cpuPercent;
    private int _memoryPercent;
    public int CpuPercent { get => _cpuPercent; init => _cpuPercent = value; }
    public int MemoryPercent { get => _memoryPercent; init => _memoryPercent = value; }
    public double MemoryTotalGb { get; init; }
    public DateTime? BootUtc { get; init; }
    public bool RebootPending { get; init; }
    public DiskStatus[] Disks { get; init; } = Array.Empty<DiskStatus>();
    public bool WatchdogEnabled { get; init; }
    public string SigningKeyId { get; init; } = string.Empty;
    public AgentEvent[] RecentEvents { get; init; } = Array.Empty<AgentEvent>();
    public MachineRunnerStatus[] Runners { get; private set; } = Array.Empty<MachineRunnerStatus>();
    /// <summary>Built from this PC's own state rather than read from the relay (PC not enrolled, or not published yet).</summary>
    public bool IsLocalOnly { get; init; }
    public string Fingerprint { get; private set; } = string.Empty;
    /// <summary>Where this PC was reached ("this PC", an IP:port, or a Tailscale name).</summary>
    public string Address { get; init; } = string.Empty;
    /// <summary>Why the PC couldn't be reached on the last refresh (empty when it answered).</summary>
    public string ConnectionProblem { get; init; } = string.Empty;
    /// <summary>This PC's fleet agent is installed but older than this dashboard (or not answering), so it needs updating.</summary>
    public bool LocalAgentOutdated { get; init; }

    /// <summary>
    /// This PC's relay status plus local runners its agent didn't report (e.g. console runners an older agent
    /// can't see), so everything on this PC is listed right away.
    /// </summary>
    public MachineInfo WithLocalRunners(IEnumerable<RunnerInfo> local)
    {
        var extra = local
            .Where(r => !Runners.Any(s => (!string.IsNullOrEmpty(r.Name) && s.ServiceName.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                                          || (!string.IsNullOrEmpty(s.InstallPath) && s.InstallPath.Equals(r.InstallPath, StringComparison.OrdinalIgnoreCase))))
            .Select(r => new MachineRunnerStatus
            {
                ServiceName = r.Name,
                InstallPath = r.InstallPath,
                IsInteractive = r.IsInteractive,
                DisplayName = r.DisplayName,
                RepositoryPath = r.RepositoryPath,
                Scope = r.ApiScope,
                AgentName = r.AgentName,
                AgentId = r.AgentId,
                CustomLabels = r.CustomLabels,
                RegistrationOptions = r.RegistrationOptions,
                IsRunning = r.IsRunning,
                ActivityText = r.ActivityText,
                ServiceInstalled = r.ServiceInstalled,
                MovedAway = r.MovedAway,
                StartsAt = r.StartsAt,
                RunnerVersion = r.RunnerVersion,
                WorkFolderBytes = r.WorkFolderBytes,
                MachineKey = MachineKey,
                MachineName = MachineName,
                AgentProtocolVersion = MachineRelay.ProtocolVersion,
                IsLocalMachine = true,
                MachineOnline = true
            })
            .ToArray();
        if (extra.Length == 0) return this;
        var merged = (MachineInfo)MemberwiseClone();
        merged.PropertyChanged = null;
        merged.Runners = Runners.Concat(extra).OrderBy(r => r.TitleText, StringComparer.OrdinalIgnoreCase).ToArray();
        merged.Fingerprint = Fingerprint + "|local:" + string.Join(';', extra.Select(r => $"{r.CommandKey}/{r.IsRunning}/{r.ActivityText}/{r.IsInteractive}/{r.ServiceInstalled}/{r.StartsAt}"));
        return merged;
    }
    public string PendingAction => _pendingAction;

    public bool IsLocal => MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public bool IsOnline => !IsLocalOnly && ConnectionProblem.Length == 0
        && DateTime.UtcNow - UpdatedUtc < MachineRelay.HeartbeatInterval * 2.5
        && FleetAgentRunning;
    /// <summary>Online for the purpose of fleet counts: a local-only PC is always reachable from here.</summary>
    public bool IsReachable => IsLocalOnly || IsOnline;
    /// <summary>Remote features added in protocol 9 (logs, reboot, update, auto-recovery) are available.</summary>
    public bool SupportsFleetTools => IsLocal || (IsOnline && AgentProtocolVersion >= 9);
    public Visibility ForgetVisibility => IsLocal ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>This PC, reported by its own agent, i.e. it's in a fleet it can leave.</summary>
    public Visibility LeaveVisibility => IsLocal && !IsLocalOnly ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPairingProblem => ConnectionProblem.Contains("airing", StringComparison.OrdinalIgnoreCase);

    // ----- Collapsed to its header -----
    public bool IsCollapsed => CollapsedPcs.Contains(MachineName);
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public Visibility BodyVisibility => IsCollapsed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Collapses or expands the card; returns whether it is collapsed now.</summary>
    public bool ToggleCollapsed()
    {
        if (!CollapsedPcs.Remove(MachineName)) CollapsedPcs.Add(MachineName);
        Notify(new[] { nameof(IsCollapsed), nameof(CollapseGlyph), nameof(BodyVisibility), nameof(HealthVisibility) });
        return IsCollapsed;
    }

    // ----- Machine header -----
    public string StatusText => _pendingAction switch
    {
        "Update" => "Updating…",
        "Reboot" => "Restarting PC…",
        _ => IsLocalOnly ? "Local only" : IsPairingProblem ? "Not paired" : IsOnline ? "Online" : "Offline"
    };
    public Brush StatusBrush => _pendingAction.Length > 0 ? Palette.Warning
        : IsLocalOnly ? Palette.Info : IsPairingProblem ? Palette.Warning : IsOnline ? Palette.Success : Palette.Danger;
    public Brush StatusTint => Palette.Tint(StatusBrush);
    public string AgentText => ConnectionProblem.Length > 0 ? ConnectionProblem
        : IsLocalOnly
            ? (LocalAgentOutdated ? "Fleet agent outdated. Other PCs cannot reach this PC." : "This PC")
        : IsLocal ? "Visible to other PCs in the fleet"
        : Address.Length > 0 ? $"Connected at {Address}" : "Connected";
    public string SyncText
    {
        get
        {
            if (IsLocalOnly || IsLocal) return "Live";
            if (UpdatedUtc == DateTime.MinValue) return "Never reached";
            var age = DateTime.UtcNow - UpdatedUtc;
            if (age.TotalSeconds < 10) return "Updated just now";
            if (age.TotalMinutes < 1) return $"Updated {(int)age.TotalSeconds}s ago";
            if (age.TotalHours < 1) return $"Updated {(int)age.TotalMinutes}m ago";
            if (age.TotalDays < 1) return $"Updated {(int)age.TotalHours}h ago";
            return $"Updated {UpdatedUtc.ToLocalTime():g}";
        }
    }
    public Visibility ThisPcVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
    public string MachineGlyph => IsLocal ? "" : "";

    // ----- Versions and updates -----
    public string VersionText => IsLocalOnly ? $"v{AppUpdater.CurrentVersion}"
        : string.IsNullOrWhiteSpace(AppVersion) ? $"agent protocol {AgentProtocolVersion}" : $"v{AppVersion}";
    public bool UpdateAvailable => !IsLocalOnly && AppVersion.Length > 0 && AppUpdater.IsNewer(LatestAppVersion, AppVersion);
    public Visibility UpdateBadgeVisibility => LocalAgentOutdated || UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
    public string UpdateBadgeText => LocalAgentOutdated ? "Update fleet agent" : $"Runner Manager v{LatestAppVersion} available";
    public string UpdateBadgeTip => LocalAgentOutdated
        ? "The fleet agent on this PC is older than this app. Other PCs use it to connect to this PC. " +
          "Click to update it. Runners keep running. This does not update GitHub runners."
        : $"Runner Manager v{LatestAppVersion} is available. Click to install it on {MachineName}. Its fleet agent restarts automatically. This does not update GitHub runners.";
    public bool CanUpdateAgent => (LocalAgentOutdated || UpdateAvailable) && string.IsNullOrEmpty(_pendingAction) && (IsLocal || IsOnline);

    // ----- Health -----
    public Visibility HealthVisibility => !IsCollapsed && (IsLocalOnly || AgentProtocolVersion >= 9) && Disks.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CpuText => $"CPU {CpuPercent}%";
    public Brush CpuBrush => CpuPercent >= 90 ? Palette.Warning : Palette.TextMuted;
    public string MemoryText => MemoryTotalGb > 0 ? $"RAM {MemoryPercent}% of {MemoryTotalGb:0.#} GB" : $"RAM {MemoryPercent}%";
    public Brush MemoryBrush => MemoryPercent >= 90 ? Palette.Warning : Palette.TextMuted;
    public string DiskText => string.Join("   ", Disks.Select(d => $"{d.Name} {d.FreeGb:0} GB free"));
    public Brush DiskBrush => Disks.Any(d => d.IsLow) ? Palette.Warning : Palette.TextMuted;
    public string DiskTip => string.Join("\n", Disks.Select(d => $"{d.Name}  {d.FreeGb:0} GB free of {d.TotalGb:0} GB" + (d.IsLow ? "  (low)" : "")));
    public string UptimeText
    {
        get
        {
            if (BootUtc == null) return string.Empty;
            var up = DateTime.UtcNow - BootUtc.Value;
            return up.TotalDays >= 1 ? $"Up {(int)up.TotalDays}d {up.Hours}h" : up.TotalHours >= 1 ? $"Up {(int)up.TotalHours}h {up.Minutes}m" : $"Up {Math.Max(0, (int)up.TotalMinutes)}m";
        }
    }
    public Visibility RebootPendingVisibility => RebootPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WatchdogVisibility => (IsLocalOnly || AgentProtocolVersion >= 9) ? Visibility.Visible : Visibility.Collapsed;
    public string WatchdogText => WatchdogEnabled ? "Auto-recovery on" : "Auto-recovery off";
    public Brush WatchdogBrush => WatchdogEnabled ? Palette.Success : Palette.TextMuted;
    public Visibility SignedVisibility => string.IsNullOrEmpty(SigningKeyId) ? Visibility.Collapsed : Visibility.Visible;
    public string SignedTip => $"Paired. Connections to other PCs are encrypted with pairing code {SigningKeyId}.";

    // ----- Runner summary -----
    public int RunningCount => Runners.Count(r => r.IsRunning);
    public int BusyCount => Runners.Count(r => r.IsBusy);
    public string RunnerCountText => $"{RunningCount}/{Runners.Length}";
    public string RunnerSummary => Runners.Length == 0
        ? "No runners"
        : $"{RunningCount} of {Runners.Length} running" + (BusyCount > 0 ? $"  ·  {BusyCount} busy" : string.Empty);
    public string SidebarSubtitle => $"{RunnerCountText} runners running";
    public Visibility NoRunnersVisibility => Runners.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string LastResultText
    {
        get
        {
            var latestEvent = RecentEvents.LastOrDefault();
            return latestEvent != null && DateTime.UtcNow - latestEvent.Utc < TimeSpan.FromHours(1)
                ? $"{latestEvent.Text} ({latestEvent.Utc.ToLocalTime():HH:mm})"
                : LastCommandResult;
        }
    }
    public Visibility LastResultVisibility => string.IsNullOrWhiteSpace(LastResultText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Properties that change with time, load readings or online state (which follows the heartbeat age).</summary>
    private static readonly string[] TimeBasedProperties =
    {
        nameof(UpdatedUtc), nameof(SyncText), nameof(CpuPercent), nameof(CpuText), nameof(CpuBrush),
        nameof(MemoryPercent), nameof(MemoryText), nameof(MemoryBrush), nameof(UptimeText),
        nameof(LastResultText), nameof(LastResultVisibility),
        nameof(IsOnline), nameof(IsReachable), nameof(StatusText), nameof(StatusBrush), nameof(StatusTint),
        nameof(SupportsFleetTools), nameof(CanUpdateAgent)
    };

    /// <summary>Properties that depend on the pending PC action (an update or a restart).</summary>
    private static readonly string[] PendingProperties =
    {
        nameof(PendingAction), nameof(StatusText), nameof(StatusBrush), nameof(StatusTint), nameof(CanUpdateAgent)
    };

    /// <summary>
    /// Takes the newer timestamp and load readings from an otherwise identical status and refreshes the
    /// time-based text, without rebuilding the card.
    /// </summary>
    public void Touch(MachineInfo newer)
    {
        _updatedUtc = newer.UpdatedUtc;
        _cpuPercent = newer.CpuPercent;
        _memoryPercent = newer.MemoryPercent;
        RefreshRunnerReachability();
        Notify(TimeBasedProperties);
    }

    /// <summary>Re-evaluates every binding (used when the theme or the latest version changes).</summary>
    public void Refresh()
    {
        RefreshRunnerReachability();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetPending(string action)
    {
        if (_pendingAction == action) return;
        _pendingAction = action;
        Notify(PendingProperties);
    }

    private void RefreshRunnerReachability()
    {
        foreach (var runner in Runners)
        {
            if (runner.MachineOnline == IsReachable) continue;
            runner.MachineOnline = IsReachable;
            runner.Refresh();
        }
    }

    private void Notify(string[] names)
    {
        if (PropertyChanged is not { } handler) return;
        foreach (var name in names) handler(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A known PC that hasn't answered yet, shown as offline with the reason.</summary>
    public static MachineInfo Placeholder(string key, string name, DateTime lastSeenUtc, string address, string problem) => new()
    {
        MachineKey = key,
        MachineName = name,
        UpdatedUtc = lastSeenUtc,
        Address = address,
        ConnectionProblem = problem,
        Fingerprint = $"placeholder|{name}|{address}|{problem}"
    };

    public static MachineInfo FromPayload(string key, MachineStatusPayload payload, bool localOnly = false,
        string address = "", string problem = "", bool localAgentOutdated = false)
    {
        var isLocal = payload.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var machine = new MachineInfo
        {
            MachineKey = key,
            MachineName = payload.MachineName,
            MachineId = payload.MachineId,
            AgentProtocolVersion = payload.AgentProtocolVersion,
            AppVersion = payload.AppVersion,
            UpdatedUtc = payload.UpdatedUtc,
            FleetAgentRunning = payload.FleetAgentRunning,
            LastCommandId = payload.LastCommandId,
            LastCommandResult = payload.LastCommandResult,
            RecentCommands = payload.RecentCommands,
            PendingCommandIds = payload.PendingCommandIds,
            Busy = payload.Busy,
            CpuPercent = payload.CpuPercent,
            MemoryPercent = payload.MemoryPercent,
            MemoryTotalGb = payload.MemoryTotalGb,
            BootUtc = payload.BootUtc,
            RebootPending = payload.RebootPending,
            Disks = payload.Disks,
            WatchdogEnabled = payload.WatchdogEnabled,
            SigningKeyId = payload.SigningKeyId,
            RecentEvents = payload.RecentEvents,
            Runners = payload.Runners,
            IsLocalOnly = localOnly,
            Address = address,
            ConnectionProblem = problem,
            LocalAgentOutdated = localAgentOutdated,
            Fingerprint = $"{(localOnly ? "local" : "peer")}|{address}|{problem}|{localAgentOutdated}|" + payload.Fingerprint()
        };
        foreach (var runner in payload.Runners)
        {
            runner.MachineKey = key;
            runner.MachineName = payload.MachineName;
            runner.AgentProtocolVersion = payload.AgentProtocolVersion;
            runner.IsLocalMachine = isLocal;
            runner.MachineOnline = machine.IsReachable;
        }
        return machine;
    }
}

/// <summary>What a busy runner is doing: its job and steps from GitHub, and its output and processes from its PC.</summary>
public sealed class LiveJobView
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    /// <summary>One line for the collapsed runner row, for example "Build · step 4 of 9: Compile".</summary>
    public string Summary { get; init; } = string.Empty;
    public string ElapsedText { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public IReadOnlyList<LiveStepView> Steps { get; init; } = Array.Empty<LiveStepView>();
    public IReadOnlyList<LiveProcessView> Processes { get; init; } = Array.Empty<LiveProcessView>();
    public string ProcessSummary { get; init; } = string.Empty;
    public string ActivityNote { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;

    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepsVisibility => Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProcessesVisibility => Processes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivityNoteVisibility => ActivityNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OutputVisibility => Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static LiveJobView Build(GitHubJobState? job, string jobNote, RunnerJobActivity? activity, string activityNote, DateTime nowUtc)
    {
        var steps = job?.Steps.Select(s => LiveStepView.From(s, nowUtc)).ToList() ?? new List<LiveStepView>();
        var current = job?.Steps.FirstOrDefault(s => s.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
                      ?? job?.Steps.LastOrDefault(s => s.Status.Equals("completed", StringComparison.OrdinalIgnoreCase));
        var stepText = current == null || job == null ? string.Empty
            : $"step {job.Steps.ToList().IndexOf(current) + 1} of {job.Steps.Count}: {current.Name}";
        var processes = activity?.Processes.Select(LiveProcessView.From).ToList() ?? new List<LiveProcessView>();
        return new LiveJobView
        {
            Title = job == null ? "Running a job" : $"{job.WorkflowName} → {job.Name}  ·  run #{job.RunNumber}",
            Url = job?.HtmlUrl ?? string.Empty,
            Summary = job == null ? string.Empty
                : stepText.Length == 0 ? job.WorkflowName : $"{job.WorkflowName}  ·  {stepText}",
            ElapsedText = job?.StartedUtc is { } started ? $"Running for {LiveStepView.FormatDuration(nowUtc - started)}" : string.Empty,
            Note = jobNote,
            Steps = steps,
            Processes = processes,
            ProcessSummary = processes.Count == 0 ? string.Empty
                : $"CPU {activity!.Processes.Sum(p => p.CpuPercent):0}%  ·  {SystemHealth.FormatBytes(activity.Processes.Sum(p => p.MemoryBytes))}",
            ActivityNote = activityNote,
            Output = activity == null ? string.Empty : string.Join('\n', activity.Output)
        };
    }
}

public sealed class LiveStepView
{
    public string Name { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    /// <summary>"done", "failed", "running", "skipped" or "waiting"; the XAML picks icon colors from it.</summary>
    public string State { get; init; } = "waiting";
    public string Glyph => State switch
    {
        "done" => "",
        "failed" => "",
        "running" => "",
        "skipped" => "",
        _ => ""
    };

    public static LiveStepView From(GitHubJobStep step, DateTime nowUtc)
    {
        var state = step.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? "running"
            : !step.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "waiting"
            : step.Conclusion switch { "success" => "done", "skipped" => "skipped", "failure" or "cancelled" => "failed", _ => "done" };
        var duration = step.StartedUtc is not { } started ? string.Empty
            : state == "running" ? FormatDuration(nowUtc - started)
            : step.CompletedUtc is { } completed && state != "skipped" ? FormatDuration(completed - started)
            : string.Empty;
        return new LiveStepView { Name = step.Name, DurationText = duration, State = state };
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
            : $"{span.Seconds}s";
    }
}

public sealed class LiveProcessView
{
    public string Name { get; init; } = string.Empty;
    public string Arguments { get; init; } = string.Empty;
    public string CpuText { get; init; } = string.Empty;
    public string MemoryText { get; init; } = string.Empty;

    public static LiveProcessView From(RunnerProcess process) => new()
    {
        Name = process.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.Name[..^4] : process.Name,
        Arguments = process.Arguments,
        CpuText = $"{process.CpuPercent:0}%",
        MemoryText = SystemHealth.FormatBytes(process.MemoryBytes)
    };
}
