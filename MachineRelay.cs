using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace RunnerWidget;

public static class MachineRelay
{
    public const string RelayRepository = "Sandiozo/runner-widget";
    private const string StatusPrefix = "RW_STATUS_";
    private const string CommandPrefix = "RW_COMMAND_";
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerWidget");
    private static readonly string AgentStatePath = Path.Combine(StateDir, "agent-state.json");
    private static readonly string AgentLogPath = Path.Combine(StateDir, "agent.log");

    public static string LocalMachineKey => Sanitize(Environment.MachineName);

    public static async Task<List<MachineInfo>> ReadMachinesAsync()
    {
        var result = await GitHubCli.RunAsync($"api repos/{RelayRepository}/actions/variables?per_page=100");
        if (result.ExitCode != 0) return new List<MachineInfo>();

        var machines = new List<MachineInfo>();
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            if (!json.RootElement.TryGetProperty("variables", out var variables)) return machines;
            foreach (var variable in variables.EnumerateArray())
            {
                var name = variable.GetProperty("name").GetString() ?? string.Empty;
                if (!name.StartsWith(StatusPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var encoded = variable.GetProperty("value").GetString() ?? string.Empty;
                var payload = Decode<MachineStatusPayload>(encoded);
                if (payload == null) continue;
                machines.Add(MachineInfo.FromPayload(name[StatusPrefix.Length..], payload));
            }
        }
        catch { }
        return machines.OrderByDescending(m => m.IsLocal).ThenBy(m => m.MachineName).ToList();
    }

    public static async Task<string> SendCommandAsync(string machineKey, string target, string targetKey, string action,
        string runnerName = "", string[]? runnerLabels = null)
    {
        var command = new MachineCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Target = target,
            TargetKey = targetKey,
            Action = action,
            RunnerName = runnerName,
            RunnerLabels = runnerLabels ?? Array.Empty<string>()
        };
        await UpsertVariableAsync(CommandPrefix + Sanitize(machineKey), Encode(command));
        return command.Id;
    }

    public static async Task RunAgentAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StateDir);
        var lastCommandId = LoadAgentState();
        string lastResult = "Agent started";

        if (RemoteDesktopManager.IsConfigured() && !RemoteDesktopManager.ReadSnapshot().Running)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                await RemoteDesktopManager.StartAsync();
                lastResult = "Agent started; RDC auto-start requested";
            }
            catch (TaskCanceledException) { return; }
            catch (Exception ex) { AppendAgentLog($"RDC auto-start failed: {ex.Message}"); }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var command = await ReadCommandAsync(LocalMachineKey);
                if (command != null
                    && command.Id != lastCommandId
                    && DateTime.UtcNow - command.CreatedUtc < TimeSpan.FromMinutes(2))
                {
                    lastResult = await ExecuteCommandAsync(command);
                    lastCommandId = command.Id;
                    SaveAgentState(lastCommandId);
                }

                await PublishLocalStatusAsync(lastCommandId, lastResult);
            }
            catch (Exception ex)
            {
                AppendAgentLog(ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (TaskCanceledException) { }
        }
    }

    public static async Task<bool> IsAgentInstalledAsync()
    {
        var result = await TryRunProcessAsync("schtasks.exe", "/Query /TN \"RunnerWidget Fleet Agent\"");
        return result == 0;
    }

    public static async Task RemoveAgentTaskAsync()
    {
        await TryRunProcessAsync("schtasks.exe", "/End /TN \"RunnerWidget Fleet Agent\"");
        await TryRunProcessAsync("schtasks.exe", "/Delete /F /TN \"RunnerWidget Fleet Agent\"");
        await GitHubCli.RunAsync($"api --method DELETE repos/{RelayRepository}/actions/variables/{StatusPrefix}{LocalMachineKey}");
        await GitHubCli.RunAsync($"api --method DELETE repos/{RelayRepository}/actions/variables/{CommandPrefix}{LocalMachineKey}");
    }

    public static async Task InstallAndStartAgentTaskAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the built RunnerWidget.exe before installing the fleet agent.");

        var taskName = "RunnerWidget Fleet Agent";
        var taskCommand = $"\\\"{exe}\\\" --agent";
        await RunProcessAsync("schtasks.exe",
            $"/Create /F /TN \"{taskName}\" /SC ONLOGON /RL HIGHEST /TR \"{taskCommand}\"");
        await RunProcessAsync("schtasks.exe", $"/Run /TN \"{taskName}\"");
    }

    private static async Task PublishLocalStatusAsync(string lastCommandId, string lastResult)
    {
        var runners = await RunnerManager.ReadSnapshotAsync(queryGitHub: false);
        var remote = RemoteDesktopManager.ReadSnapshot();
        var payload = new MachineStatusPayload
        {
            MachineName = Environment.MachineName,
            AgentProtocolVersion = 2,
            UpdatedUtc = DateTime.UtcNow,
            RdcRunning = remote.Running,
            RdcConfigured = RemoteDesktopManager.IsConfigured(),
            RdcProcessCount = remote.Pids.Length,
            RdcPids = remote.Pids,
            RdcStartedUtc = remote.Started?.ToUniversalTime(),
            LastCommandId = lastCommandId,
            LastCommandResult = lastResult,
            Runners = runners.Select(r => new MachineRunnerStatus
            {
                ServiceName = r.Name,
                DisplayName = r.DisplayName,
                RepositoryPath = r.RepositoryPath,
                AgentName = r.AgentName,
                IsRunning = r.IsRunning,
                ActivityText = r.ActivityText,
                ServiceInstalled = r.ServiceInstalled
            }).ToArray()
        };
        await UpsertVariableAsync(StatusPrefix + LocalMachineKey, Encode(payload));
    }

    private static async Task<MachineCommand?> ReadCommandAsync(string machineKey)
    {
        var name = CommandPrefix + Sanitize(machineKey);
        var result = await GitHubCli.RunAsync($"api repos/{RelayRepository}/actions/variables/{name}");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            return Decode<MachineCommand>(json.RootElement.GetProperty("value").GetString() ?? string.Empty);
        }
        catch { return null; }
    }

    private static async Task<string> ExecuteCommandAsync(MachineCommand command)
    {
        if (command.Target.Equals("rdc", StringComparison.OrdinalIgnoreCase))
        {
            if (command.Action.Equals("Start", StringComparison.OrdinalIgnoreCase))
                await RemoteDesktopManager.StartAsync();
            else if (command.Action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
                await RemoteDesktopManager.StopAsync();
            else if (command.Action.Equals("Restart", StringComparison.OrdinalIgnoreCase))
                await RemoteDesktopManager.RestartAsync();
            else if (command.Action.Equals("Setup", StringComparison.OrdinalIgnoreCase))
                await RemoteDesktopManager.SetupAsync();
            else throw new InvalidOperationException($"Unsupported RDC action: {command.Action}");
            return $"RDC {command.Action} completed";
        }

        if (command.Target.Equals("runner", StringComparison.OrdinalIgnoreCase))
        {
            if (command.Action.Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                await RunnerManager.EditRunnerAsync(command.TargetKey, command.RunnerName, command.RunnerLabels,
                    message => AppendAgentLog($"Runner edit: {message}"));
                return $"Runner edit completed: {command.RunnerName}";
            }
            await RunnerManager.ControlServiceAsync(command.TargetKey, command.Action);
            return $"Runner {command.Action} completed";
        }

        throw new InvalidOperationException($"Unsupported target: {command.Target}");
    }

    private static async Task UpsertVariableAsync(string name, string value)
    {
        var update = await GitHubCli.RunAsync(
            $"api --method PATCH repos/{RelayRepository}/actions/variables/{name} -f name={name} -f value={value}");
        if (update.ExitCode == 0) return;

        var create = await GitHubCli.RunAsync(
            $"api --method POST repos/{RelayRepository}/actions/variables -f name={name} -f value={value}");
        if (create.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(create.Error)
                ? $"Could not update fleet state variable {name}."
                : create.Error.Trim());
    }

    private static string Encode<T>(T value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static T? Decode<T>(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<T>(json);
        }
        catch { return default; }
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string LoadAgentState()
    {
        try
        {
            if (!File.Exists(AgentStatePath)) return string.Empty;
            using var json = JsonDocument.Parse(File.ReadAllText(AgentStatePath));
            return json.RootElement.TryGetProperty("lastCommandId", out var value)
                ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static void SaveAgentState(string commandId)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(AgentStatePath, JsonSerializer.Serialize(new { lastCommandId = commandId }));
    }

    private static void AppendAgentLog(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.AppendAllText(AgentLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task<int> TryRunProcessAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName, Arguments = arguments, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        if (process == null) return -1;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var error = await stderr;
            var output = await stdout;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }
    }
}

public sealed class MachineStatusPayload
{
    public string MachineName { get; init; } = string.Empty;
    public int AgentProtocolVersion { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public bool RdcRunning { get; init; }
    public bool RdcConfigured { get; init; }
    public int RdcProcessCount { get; init; }
    public uint[] RdcPids { get; init; } = Array.Empty<uint>();
    public DateTime? RdcStartedUtc { get; init; }
    public string LastCommandId { get; init; } = string.Empty;
    public string LastCommandResult { get; init; } = string.Empty;
    public MachineRunnerStatus[] Runners { get; init; } = Array.Empty<MachineRunnerStatus>();
}

public sealed class MachineCommand
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public string Target { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string RunnerName { get; init; } = string.Empty;
    public string[] RunnerLabels { get; init; } = Array.Empty<string>();
}

public sealed class MachineRunnerStatus : INotifyPropertyChanged
{
    private bool _isExpanded;
    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore]
    public string MachineKey { get; set; } = string.Empty;
    [JsonIgnore]
    public int AgentProtocolVersion { get; set; }
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public string ActivityText { get; init; } = string.Empty;
    public bool ServiceInstalled { get; init; }
    [JsonIgnore]
    public string PowerButtonText => IsRunning ? "Stop" : "Start";
    [JsonIgnore]
    public Brush StatusBrush => IsRunning ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    [JsonIgnore]
    public bool CanControl => ServiceInstalled && !string.IsNullOrWhiteSpace(ServiceName);
    [JsonIgnore]
    public bool CanEdit => AgentProtocolVersion >= 2 && CanControl && !ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore]
    public string StatusText => IsRunning ? "Running" : "Stopped";
    [JsonIgnore]
    public string ServiceText => ServiceInstalled ? "Windows service installed" : "Windows service missing";
    [JsonIgnore]
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
    [JsonIgnore]
    public Visibility DetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class MachineInfo : INotifyPropertyChanged
{
    private string _rdcPendingAction = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string MachineKey { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public int AgentProtocolVersion { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public bool RdcRunning { get; init; }
    public bool RdcConfigured { get; init; }
    public int RdcProcessCount { get; init; }
    public uint[] RdcPids { get; init; } = Array.Empty<uint>();
    public DateTime? RdcStartedUtc { get; init; }
    public string LastCommandId { get; init; } = string.Empty;
    public string LastCommandResult { get; init; } = string.Empty;
    public MachineRunnerStatus[] Runners { get; init; } = Array.Empty<MachineRunnerStatus>();

    public bool IsLocal => MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public bool IsOnline => DateTime.UtcNow - UpdatedUtc < TimeSpan.FromSeconds(45);
    public Brush StatusBrush => IsOnline ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public string AgentText => IsOnline ? "Agent online" : "Agent offline";
    public string RdcText => _rdcPendingAction switch
    {
        "Start" => "RDC: Starting...",
        "Stop" => "RDC: Stopping...",
        "Restart" => "RDC: Restarting...",
        "Setup" => "RDC: Starting setup...",
        _ => !RdcConfigured ? "RDC: Not set up" : RdcRunning ? "RDC: Running" : "RDC: Stopped"
    };
    public string RdcPowerButtonText => _rdcPendingAction switch
    {
        "Start" => "Starting...",
        "Stop" => "Stopping...",
        _ => RdcRunning ? "Stop RDC" : "Start RDC"
    };
    public string RdcRestartButtonText => _rdcPendingAction == "Restart" ? "Restarting..." : "Restart RDC";
    public string RdcSetupButtonText => _rdcPendingAction == "Setup" ? "Starting setup..."
        : AgentProtocolVersion < 2 ? "Update agent first" : "Set up RDC";
    public Visibility RdcSetupVisibility => !RdcConfigured ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RdcControlsVisibility => RdcConfigured ? Visibility.Visible : Visibility.Collapsed;
    public string RdcProcessText => RdcRunning ? $"Processes: {RdcProcessCount}" : "Processes: 0";
    public string RdcPidText => RdcRunning && RdcPids.Length > 0 ? $"PID: {string.Join(", ", RdcPids)}" : "No active RDC processes";
    public string RdcUptimeText => !RdcRunning || !RdcStartedUtc.HasValue ? "Uptime: --" : (DateTime.UtcNow - RdcStartedUtc.Value).TotalHours >= 1 ? $"Uptime: {(int)(DateTime.UtcNow - RdcStartedUtc.Value).TotalHours}h {(DateTime.UtcNow - RdcStartedUtc.Value).Minutes}m" : $"Uptime: {Math.Max(0,(int)(DateTime.UtcNow - RdcStartedUtc.Value).TotalMinutes)}m";
    public Brush RdcStatusBrush => !string.IsNullOrEmpty(_rdcPendingAction)
        ? Brushes.Goldenrod : !RdcConfigured ? Brushes.Gray : RdcRunning ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public bool CanControl => IsOnline && RdcConfigured && string.IsNullOrEmpty(_rdcPendingAction);
    public bool CanSetupRdc => !RdcConfigured && string.IsNullOrEmpty(_rdcPendingAction);
    public string RunnerSummary
    {
        get
        {
            var running = Runners.Count(r => r.IsRunning);
            var busy = Runners.Count(r => r.ActivityText.Equals("Busy", StringComparison.OrdinalIgnoreCase));
            return $"{running}/{Runners.Length} running  |  {busy} busy";
        }
    }

    public string LastSeenText
    {
        get
        {
            var age = DateTime.UtcNow - UpdatedUtc;
            if (age.TotalSeconds < 10) return "Last seen just now";
            if (age.TotalMinutes < 1) return $"Last seen {(int)age.TotalSeconds}s ago";
            if (age.TotalHours < 1) return $"Last seen {(int)age.TotalMinutes}m ago";
            return $"Last seen {UpdatedUtc.ToLocalTime():g}";
        }
    }
    public void SetRdcPending(string action)
    {
        if (_rdcPendingAction == action) return;
        _rdcPendingAction = action;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RdcText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RdcPowerButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RdcRestartButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RdcSetupButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RdcStatusBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanControl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSetupRdc)));
    }

    public static MachineInfo FromPayload(string key, MachineStatusPayload payload)
    {
        foreach (var runner in payload.Runners)
        {
            runner.MachineKey = key;
            runner.AgentProtocolVersion = payload.AgentProtocolVersion;
        }
        return new MachineInfo
        {
            MachineKey = key,
            MachineName = payload.MachineName,
            AgentProtocolVersion = payload.AgentProtocolVersion,
            UpdatedUtc = payload.UpdatedUtc,
            RdcRunning = payload.RdcRunning,
            RdcConfigured = payload.RdcConfigured,
            RdcProcessCount = payload.RdcProcessCount,
            RdcPids = payload.RdcPids,
            RdcStartedUtc = payload.RdcStartedUtc,
            LastCommandId = payload.LastCommandId,
            LastCommandResult = payload.LastCommandResult,
            Runners = payload.Runners
        };
    }
}
