using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RunnerWidget;

public partial class MainWindow : Window
{
    public ObservableCollection<RunnerInfo> Runners { get; } = new();
    public ObservableCollection<MachineInfo> Machines { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    private readonly Dictionary<string, string> _lastRunnerStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _runnerExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _machineRunnerExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private bool? _lastRemoteRunning;
    private bool _refreshing;
    private bool _operationRunning;
    private bool _githubAuthenticated;
    private DateTime _lastAuthCheck = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(forceAuthCheck: true); _refreshTimer.Start(); };
    }

    private async Task RefreshAsync(bool forceAuthCheck = false)
    {
        if (_refreshing || _operationRunning) return;
        _refreshing = true;
        try
        {
            if (forceAuthCheck || DateTime.Now - _lastAuthCheck > TimeSpan.FromSeconds(30))
            {
                _githubAuthenticated = await GitHubCli.IsAuthenticatedAsync();
                _lastAuthCheck = DateTime.Now;
                ApplyGitHubStatus();
            }

            var runners = await RunnerManager.ReadSnapshotAsync(_githubAuthenticated);
            var remote = await Task.Run(RemoteDesktopManager.ReadSnapshot);
            var machines = _githubAuthenticated ? await MachineRelay.ReadMachinesAsync() : new List<MachineInfo>();
            foreach (var existingMachine in Machines)
                foreach (var existingRunner in existingMachine.Runners)
                    _machineRunnerExpansionStates[$"{existingMachine.MachineKey}|{existingRunner.ServiceName}"] = existingRunner.IsExpanded;
            Machines.Clear();
            foreach (var machine in machines)
            {
                foreach (var machineRunner in machine.Runners)
                    if (_machineRunnerExpansionStates.TryGetValue($"{machine.MachineKey}|{machineRunner.ServiceName}", out var expanded))
                        machineRunner.IsExpanded = expanded;
                Machines.Add(machine);
            }
            foreach (var existing in Runners)
                _runnerExpansionStates[existing.InstallPath] = existing.IsExpanded;
            Runners.Clear();
            foreach (var runner in runners)
            {
                if (_runnerExpansionStates.TryGetValue(runner.InstallPath, out var expanded))
                    runner.IsExpanded = expanded;
                Runners.Add(runner);
                var state = $"{runner.StatusText}/{runner.ActivityText}/{runner.GitHubText}/{runner.CurrentWorkText}/{runner.QueueText}";
                if (!_lastRunnerStates.TryGetValue(runner.InstallPath, out var previous) || previous != state)
                {
                    AddActivity($"{runner.DisplayName}: {state}");
                    _lastRunnerStates[runner.InstallPath] = state;
                }
            }

            ApplyRemoteSnapshot(remote);
            LastUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { AddActivity($"Refresh failed: {ex.Message}"); }
        finally { _refreshing = false; }
    }

    private void ApplyGitHubStatus()
    {
        GitHubStatusText.Text = _githubAuthenticated ? "GitHub: Connected" : "GitHub: Not connected";
        GitHubStatusText.Foreground = _githubAuthenticated ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
        GitHubConnectButton.Content = _githubAuthenticated ? "Disconnect" : "Connect GitHub";
        GitHubReconnectButton.Visibility = _githubAuthenticated ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void GitHubConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_githubAuthenticated)
        {
            await RunOperationAsync("Disconnect GitHub", async log =>
            {
                await GitHubCli.DisconnectAsync();
                log("GitHub disconnected.");
            });
            return;
        }
        GitHubCli.LaunchLogin();
        AddActivity("Opened GitHub login. Complete authentication in the browser/terminal window.");
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
            AddActivity("Reconnecting GitHub. Complete authentication in the browser/terminal window.");
            _githubAuthenticated = false;
            ApplyGitHubStatus();
            await Task.Delay(1500);
            await RefreshAsync(forceAuthCheck: true);
        }
        catch (Exception ex)
        {
            AddActivity($"GitHub reconnect failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Reconnect GitHub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddRunner_Click(object sender, RoutedEventArgs e)
    {
        if (!_githubAuthenticated)
        {
            MessageBox.Show(this, "Connect GitHub first so the app can request temporary runner tokens.",
                "GitHub connection required", MessageBoxButton.OK, MessageBoxImage.Information);
            GitHubCli.LaunchLogin();
            return;
        }
        var dialog = new AddRunnerWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Setup == null) return;
        await RunOperationAsync("Create runner", log => RunnerManager.CreateRunnerAsync(dialog.Setup, log));
    }

    private async Task RunOperationAsync(string title, Func<Action<string>, Task> operation)
    {
        if (_operationRunning) return;
        _operationRunning = true;
        try
        {
            AddActivity($"{title} started.");
            await operation(AddActivity);
            AddActivity($"{title} completed.");
        }
        catch (Exception ex)
        {
            AddActivity($"{title} failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _operationRunning = false; await RefreshAsync(forceAuthCheck: true); }
    }

    private async Task ControlRunnerAsync(string serviceName, string action)
    {
        await RunOperationAsync($"{action} runner", async _ =>
        {
            await RunnerManager.ControlServiceAsync(serviceName, action);
        });
    }

    private async void RunnerPower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RunnerInfo runner })
            await ControlRunnerAsync(runner.Name, runner.IsRunning ? "Stop" : "Start");
    }

    private void RunnerCard_Click(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is ButtonBase) return;
            current = VisualTreeHelper.GetParent(current);
        }
        if (sender is Border { Tag: RunnerInfo runner })
        {
            runner.IsExpanded = !runner.IsExpanded;
            _runnerExpansionStates[runner.InstallPath] = runner.IsExpanded;
        }
    }

    private async void RunnerRestart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) await ControlRunnerAsync(name, "Restart");
    }

    private async void RunnerRepair_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RunnerInfo runner }) return;
        var confirm = MessageBox.Show(this,
            $"Repair {runner.DisplayName} as a Windows service?\n\nThis unregisters it briefly and re-registers it using the same runner name.",
            "Repair runner service", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        await RunOperationAsync("Repair runner", log => RunnerManager.RepairRunnerAsync(runner, log));
    }

    private async void RunnerWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RunnerInfo runner }) return;
        await RunOperationAsync("Check workflows", log => RunnerManager.CheckWorkflowsAsync(runner, log));
    }

    private async void RunnerRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RunnerInfo runner }) return;
        var choice = MessageBox.Show(this,
            $"Remove {runner.DisplayName} from GitHub?\n\nYes: unregister and delete {runner.InstallPath}\nNo: unregister but keep the local files\nCancel: do nothing",
            "Remove runner", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel) return;
        var deleteFolder = choice == MessageBoxResult.Yes;
        await RunOperationAsync("Remove runner",
            log => RunnerManager.RemoveRunnerAsync(runner, deleteFolder, log));
    }

    private void RunnerLogs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string root }) return;
        var path = Path.Combine(root, "_diag");
        if (!Directory.Exists(path))
        {
            AddActivity($"Runner log folder not found: {path}");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async void InstallAgent_Click(object sender, RoutedEventArgs e)
    {
        if (!_githubAuthenticated)
        {
            MessageBox.Show(this, "Connect GitHub first.", "GitHub connection required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunOperationAsync("Enable fleet agent", async log =>
        {
            await MachineRelay.InstallAndStartAgentTaskAsync();
            log($"Fleet agent enabled on {Environment.MachineName}.");
        });
    }

    private async void MachineRdcPower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineInfo machine }) return;
        var action = machine.RdcRunning ? "Stop" : "Start";
        await MachineRelay.SendCommandAsync(machine.MachineKey, "rdc", string.Empty, action);
        AddActivity($"{machine.MachineName}: queued RDC {action}.");
    }

    private async void MachineRdcRestart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineInfo machine }) return;
        await MachineRelay.SendCommandAsync(machine.MachineKey, "rdc", string.Empty, "Restart");
        AddActivity($"{machine.MachineName}: queued RDC Restart.");
    }

    private void MachineRunnerCard_Click(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is ButtonBase) return;
            current = VisualTreeHelper.GetParent(current);
        }
        if (sender is Border { Tag: MachineRunnerStatus runner })
        {
            runner.IsExpanded = !runner.IsExpanded;
            _machineRunnerExpansionStates[$"{runner.MachineKey}|{runner.ServiceName}"] = runner.IsExpanded;
        }
    }

    private async void MachineRunnerPower_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MachineRunnerStatus runner }) return;
        var action = runner.IsRunning ? "Stop" : "Start";
        await MachineRelay.SendCommandAsync(runner.MachineKey, "runner", runner.ServiceName, action);
        AddActivity($"{runner.AgentName}: queued {action} on {runner.MachineKey}.");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(forceAuthCheck: true);

    private void ApplyRemoteSnapshot(RemoteInfo remote)
    {
        RemoteStatusDot.Fill = remote.Running ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        RemoteStatusText.Text = remote.Running ? "Running" : "Stopped";
        RemoteStatusText.Foreground = remote.Running ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        RemotePowerButton.Content = remote.Running ? "Stop" : "Start";
        RemoteDetailsText.Text = remote.Running
            ? $"{remote.Pids.Length} process(es)  |  PID {string.Join(", ", remote.Pids)}" +
              (remote.Started.HasValue ? $"  |  started {remote.Started:HH:mm:ss}" : string.Empty)
            : "No Remote Desktop Commander process detected";
        if (_lastRemoteRunning != remote.Running)
        {
            AddActivity($"Remote Desktop Commander: {(remote.Running ? "Running" : "Stopped")}");
            _lastRemoteRunning = remote.Running;
        }
    }

    private async void RemotePower_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRemoteRunning == true)
        {
            await RemoteDesktopManager.StopAsync();
            AddActivity("Stopped Remote Desktop Commander");
            await Task.Delay(700);
        }
        else
        {
            await RemoteDesktopManager.StartAsync();
            AddActivity("Started Remote Desktop Commander");
            await Task.Delay(1000);
        }
        await RefreshAsync();
    }

    private async void RemoteRestart_Click(object sender, RoutedEventArgs e)
    {
        await RemoteDesktopManager.RestartAsync();
        AddActivity("Restarted Remote Desktop Commander");
        await Task.Delay(1000);
        await RefreshAsync();
    }

    private void AddActivity(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddActivity(message));
            return;
        }
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityLog.Count > 150) ActivityLog.RemoveAt(ActivityLog.Count - 1);
    }
}
