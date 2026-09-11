using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RunnerWidget;

public partial class MainWindow : Window
{
    public ObservableCollection<RunnerInfo> Runners { get; } = new();
    public ObservableCollection<string> ActivityLog { get; } = new();

    private readonly Dictionary<string, string> _lastRunnerStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private bool? _lastRemoteRunning;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => { await RefreshAsync(); _refreshTimer.Start(); };
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var runners = await Task.Run(ReadRunnerSnapshot);
            var remote = await Task.Run(ReadRemoteSnapshot);

            Runners.Clear();
            foreach (var runner in runners)
            {
                Runners.Add(runner);
                var state = $"{runner.StatusText}/{runner.ActivityText}";
                if (!_lastRunnerStates.TryGetValue(runner.Name, out var previous) || previous != state)
                {
                    AddActivity($"{runner.DisplayName}: {state}");
                    _lastRunnerStates[runner.Name] = state;
                }
            }

            ApplyRemoteSnapshot(remote);
            LastUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AddActivity($"Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static List<RunnerInfo> ReadRunnerSnapshot()
    {
        var processMap = ReadProcesses();
        var result = new List<RunnerInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, State, PathName, ProcessId FROM Win32_Service WHERE Name LIKE 'actions.runner.%'");

        foreach (ManagementObject service in searcher.Get())
        {
            var name = service["Name"]?.ToString() ?? "Unknown runner";
            var displayName = service["DisplayName"]?.ToString() ?? name;
            var state = service["State"]?.ToString() ?? "Unknown";
            var pathName = service["PathName"]?.ToString() ?? string.Empty;
            var pid = ConvertToUInt(service["ProcessId"]);
            var installPath = ResolveRunnerRoot(pathName);
            var running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            var busy = running && HasDescendantWorker(pid, processMap);

            result.Add(new RunnerInfo
            {
                Name = name,
                DisplayName = displayName,
                StatusText = state,
                ActivityText = running ? (busy ? "Busy" : "Idle") : "Unavailable",
                LastActivityText = GetLatestRunnerActivity(installPath),
                InstallPath = installPath,
                StatusBrush = running ? Brushes.MediumSeaGreen : Brushes.IndianRed
            });
        }

        return result.OrderBy(r => r.DisplayName).ToList();
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
        var visited = new HashSet<uint>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!visited.Add(parent)) continue;
            foreach (var child in processes.Where(p => p.ParentProcessId == parent))
            {
                if (child.Name.Equals("Runner.Worker.exe", StringComparison.OrdinalIgnoreCase)) return true;
                queue.Enqueue(child.ProcessId);
            }
        }
        return false;
    }

    private static string ResolveRunnerRoot(string servicePath)
    {
        var executable = ExtractExecutablePath(servicePath);
        if (string.IsNullOrWhiteSpace(executable)) return "Runner path unavailable";
        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory)) return executable;
        var current = new DirectoryInfo(directory);
        return current.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && current.Parent != null
            ? current.Parent.FullName
            : current.FullName;
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return string.Empty;
        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            return end > 1 ? commandLine[1..end] : commandLine.Trim('"');
        }
        var exe = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? commandLine[..(exe + 4)] : commandLine.Split(' ')[0];
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

    private static RemoteInfo ReadRemoteSnapshot()
    {
        var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
        if (matches.Count == 0) return new RemoteInfo { Running = false };

        var started = matches.Select(p => TryParseWmiDate(p.CreationDate))
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Min();
        return new RemoteInfo
        {
            Running = true,
            Pids = matches.Select(p => p.ProcessId).Distinct().OrderBy(p => p).ToArray(),
            Started = started == default ? null : started
        };
    }

    private static bool IsRemoteDesktopCommanderProcess(ProcessInfo process)
    {
        var command = process.CommandLine;
        return command.Contains("desktop-commander", StringComparison.OrdinalIgnoreCase)
               && command.Contains("remote", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRemoteSnapshot(RemoteInfo remote)
    {
        RemoteStatusDot.Fill = remote.Running ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        RemoteStatusText.Text = remote.Running ? "Running" : "Stopped";
        RemoteStatusText.Foreground = remote.Running ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        RemoteDetailsText.Text = remote.Running
            ? $"{remote.Pids.Length} process(es) · PID {string.Join(", ", remote.Pids)}" +
              (remote.Started.HasValue ? $" · started {remote.Started:HH:mm:ss}" : string.Empty)
            : "No Remote Desktop Commander process detected";

        if (_lastRemoteRunning != remote.Running)
        {
            AddActivity($"Remote Desktop Commander: {(remote.Running ? "Running" : "Stopped")}");
            _lastRemoteRunning = remote.Running;
        }
    }

    private async Task ControlRunnerAsync(string serviceName, string action)
    {
        try
        {
            AddActivity($"{action} runner: {serviceName}");
            await Task.Run(() =>
            {
                using var service = new ServiceController(serviceName);
                service.Refresh();

                if (action == "Start")
                {
                    if (service.Status == ServiceControllerStatus.Stopped)
                    {
                        service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                }
                else if (action == "Stop")
                {
                    if (service.Status != ServiceControllerStatus.Stopped && service.CanStop)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                }
                else if (action == "Restart")
                {
                    if (service.Status != ServiceControllerStatus.Stopped && service.CanStop)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    }
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
            });
        }
        catch (Exception ex) { AddActivity($"{action} failed: {ex.Message}"); }
        await RefreshAsync();
    }

    private async void RunnerStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) await ControlRunnerAsync(name, "Start");
    }

    private async void RunnerStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) await ControlRunnerAsync(name, "Stop");
    }

    private async void RunnerRestart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) await ControlRunnerAsync(name, "Restart");
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void RemoteStart_Click(object sender, RoutedEventArgs e)
    {
        await StartRemoteAsync();
        await Task.Delay(1000);
        await RefreshAsync();
    }

    private async void RemoteStop_Click(object sender, RoutedEventArgs e)
    {
        await StopRemoteAsync();
        await Task.Delay(700);
        await RefreshAsync();
    }

    private async void RemoteRestart_Click(object sender, RoutedEventArgs e)
    {
        await StopRemoteAsync();
        await Task.Delay(900);
        await StartRemoteAsync();
        await Task.Delay(1000);
        await RefreshAsync();
    }

    private Task StartRemoteAsync()
    {
        return Task.Run(() =>
        {
            if (ReadRemoteSnapshot().Running) return;
            var npx = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd");
            if (!File.Exists(npx)) npx = "npx.cmd";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"Remote Desktop Commander\" /min \"{npx}\" @wonderwhy-er/desktop-commander@latest remote",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Dispatcher.Invoke(() => AddActivity("Started Remote Desktop Commander"));
        });
    }

    private Task StopRemoteAsync()
    {
        return Task.Run(() =>
        {
            var matches = ReadProcesses().Where(IsRemoteDesktopCommanderProcess).ToList();
            var ids = matches.Select(p => p.ProcessId).ToHashSet();
            var roots = matches.Where(p => !ids.Contains(p.ParentProcessId)).ToList();

            foreach (var processInfo in roots)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processInfo.ProcessId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AddActivity($"Could not stop PID {processInfo.ProcessId}: {ex.Message}"));
                }
            }
            Dispatcher.Invoke(() => AddActivity("Stopped Remote Desktop Commander"));
        });
    }

    private static DateTime? TryParseWmiDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value); }
        catch { return null; }
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

    private void AddActivity(string message)
    {
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (ActivityLog.Count > 100) ActivityLog.RemoveAt(ActivityLog.Count - 1);
    }
}

public sealed class RunnerInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string ActivityText { get; init; } = string.Empty;
    public string LastActivityText { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public Brush StatusBrush { get; init; } = Brushes.Gray;
}

public sealed class ProcessInfo
{
    public uint ProcessId { get; init; }
    public uint ParentProcessId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string CreationDate { get; init; } = string.Empty;
}

public sealed class RemoteInfo
{
    public bool Running { get; init; }
    public uint[] Pids { get; init; } = Array.Empty<uint>();
    public DateTime? Started { get; init; }
}
