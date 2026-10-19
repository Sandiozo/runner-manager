using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace RunnerWidget;

public static class MachineRelay
{
    public const int ProtocolVersion = 10;
    private const string AgentMutexName = @"Global\RunnerWidgetFleetAgent";
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(30);
    /// <summary>How fresh a PC's status must be to count as online (dashboards poll every few seconds).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(12);
    private static readonly string AgentStatePath = Path.Combine(FleetConfig.StateDir, "agent-state.json");
    public static readonly string AgentLogPath = Path.Combine(FleetConfig.StateDir, "agent.log");
    private const long MaxAgentLogBytes = 1024 * 1024;

    // Dashboard-side cache of each PC's last answer, so a PC that drops off stays listed as offline.
    private static readonly Dictionary<string, MachineStatusPayload> LastStatus = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _lastDiscovery = DateTime.MinValue;

    // Agent-process state
    private static readonly List<AgentEvent> RecentEvents = new();
    private static readonly HashSet<string> WantRunning = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<DateTime>> RecoveryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim StatusGate = new(1, 1);
    private static MachineStatusPayload? _cachedStatus;
    private static DateTime _cachedStatusUtc = DateTime.MinValue;
    private static int _rdcUnhealthyChecks;
    private static bool _restartAgentAfterUpdate;

    public static string LocalMachineKey => Sanitize(Environment.MachineName);

    // ================= Dashboard side =================

    /// <summary>
    /// This PC plus every paired PC: found by broadcast on the local network, remembered addresses, or addresses
    /// added in Settings (e.g. Tailscale names). All traffic goes directly between the PCs, encrypted with the pairing code.
    /// </summary>
    public static async Task<List<MachineInfo>> ReadFleetAsync(bool forceDiscovery = false)
    {
        var machines = new List<MachineInfo> { await ReadThisPcAsync() };
        var config = FleetConfig.Current;
        if (config.FleetKey == null) return machines;

        if (forceDiscovery || DateTime.UtcNow - _lastDiscovery > DiscoveryInterval)
        {
            _lastDiscovery = DateTime.UtcNow;
            var hints = config.KnownPeers.Select(p => p.Address.Split(':')[0]).Concat(config.ManualAddresses);
            foreach (var peer in await FleetNetwork.DiscoverAsync(hints, TimeSpan.FromSeconds(1.2)))
                RememberPeer(peer.Name, peer.Address, peer.Version);
        }

        // Drop any entry for this PC itself (older builds could record it under its sanitized name).
        if (FleetConfig.Current.KnownPeers.Any(p => Sanitize(p.Name) == LocalMachineKey))
            FleetConfig.Update(c => c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == LocalMachineKey));
        config = FleetConfig.Current;
        var peers = config.KnownPeers.ToList();
        var unnamed = config.ManualAddresses
            .Where(a => !peers.Any(p => SameHost(p.Address, a)) && !IsThisPc(a))
            .ToList();
        var fetches = peers.Select(p => FetchPeerAsync(p.Name, p.Address, p.LastSeenUtc))
            .Concat(unnamed.Select(a => FetchPeerAsync(null, a, DateTime.MinValue)));
        var results = await Task.WhenAll(fetches);
        machines.AddRange(results.Where(m => m != null && !m.IsLocal).Cast<MachineInfo>()
            .GroupBy(m => m.MachineKey).Select(g => g.First())
            .OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase));
        return machines;
    }

    /// <summary>This PC as its agent reports it (includes auto-recovery events), or read directly when there's no agent.</summary>
    private static async Task<MachineInfo> ReadThisPcAsync()
    {
        var installed = await IsAgentInstalledAsync();
        var outdated = false;
        if (installed && FleetConfig.Current.FleetKey != null && await IsAgentRunningAsync())
        {
            try
            {
                var response = await FleetNetwork.CallAsync("127.0.0.1", LocalMachineKey, new FleetRequest { Op = "status" });
                if (response.Status is { } status)
                {
                    outdated = status.AgentProtocolVersion < ProtocolVersion || AppUpdater.IsNewer(AppUpdater.CurrentVersion, status.AppVersion);
                    if (!outdated) return MachineInfo.FromPayload(LocalMachineKey, status, address: "this PC");
                }
            }
            catch
            {
                // Agents from before the direct connection don't listen at all.
                outdated = true;
            }
        }
        else if (installed && await IsAgentRunningAsync())
        {
            outdated = !await LocalAgentIsCurrentAsync();
        }
        return await ReadLocalMachineAsync(string.Empty, outdated);
    }

    /// <summary>Whether the agent task runs this exact build (used before a pairing code exists).</summary>
    private static async Task<bool> LocalAgentIsCurrentAsync()
    {
        var exe = await AgentTaskExecutableAsync();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty;
        return !AppUpdater.IsNewer(AppUpdater.CurrentVersion, version);
    }

    /// <summary>
    /// Points this PC's agent at an installed copy of the running build and restarts it, so it matches this dashboard.
    /// </summary>
    public static async Task UpdateLocalAgentAsync(Action<string> log)
    {
        var exe = await AppUpdater.InstallCurrentBuildAsync(log);
        log("Restarting the fleet agent...");
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await RegisterAgentTaskAsync(exe);
        await FleetNetwork.EnsureFirewallRulesAsync();
        await Task.Delay(1500);
        var (code, output) = await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        log($"Fleet agent now runs v{AppUpdater.CurrentVersion} from {exe}.");
    }

    private static async Task<MachineInfo?> FetchPeerAsync(string? name, string address, DateTime lastSeenUtc)
    {
        var key = name == null ? "*" : Sanitize(name);
        string problem;
        try
        {
            var response = await FleetNetwork.CallAsync(address, key, new FleetRequest { Op = "status" });
            var payload = response.Status ?? throw new InvalidOperationException("The PC returned no status.");
            var machineKey = Sanitize(payload.MachineName);
            lock (LastStatus) LastStatus[machineKey] = payload;
            RememberPeer(payload.MachineName, FleetNetwork.WithPort(address), payload.AppVersion);
            return MachineInfo.FromPayload(machineKey, payload, address: address);
        }
        catch (PairingMismatchException ex) { problem = ex.Message; }
        catch (Exception) { problem = $"Not reachable at {FleetNetwork.WithPort(address)}"; }

        if (name == null)
            return MachineInfo.Placeholder("ADDR_" + Sanitize(address), address, DateTime.MinValue, address, problem);
        MachineStatusPayload? last;
        lock (LastStatus) LastStatus.TryGetValue(key, out last);
        return last != null
            ? MachineInfo.FromPayload(key, last, address: address, problem: problem)
            : MachineInfo.Placeholder(key, name, lastSeenUtc, address, problem);
    }

    private static void RememberPeer(string name, string address, string version)
    {
        if (Sanitize(name) == LocalMachineKey) return;
        // Match on the sanitized key so "DESKTOP-PC" and "DESKTOP_PC" are the same PC, and keep the real name.
        var existing = FleetConfig.Current.KnownPeers.Where(p => Sanitize(p.Name) == Sanitize(name))
            .OrderByDescending(p => p.Name != Sanitize(p.Name)).FirstOrDefault();
        if (existing != null && name == Sanitize(name) && existing.Name != name) name = existing.Name;
        // Only write when something meaningful changed, not on every poll.
        var duplicates = FleetConfig.Current.KnownPeers.Count(p => Sanitize(p.Name) == Sanitize(name)) > 1;
        if (!duplicates && existing != null && existing.Name == name && existing.Address == address && existing.Version == version
            && DateTime.UtcNow - existing.LastSeenUtc < TimeSpan.FromMinutes(10)) return;
        FleetConfig.Update(c =>
        {
            c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name));
            c.KnownPeers.Add(new PeerRecord { Name = name, Address = address, Version = version, LastSeenUtc = DateTime.UtcNow });
        });
    }

    /// <summary>Remembers a PC learned during pairing so it's contacted right away.</summary>
    public static void AddKnownPeer(string name, string address) => RememberPeer(name, FleetNetwork.WithPort(address), string.Empty);

    public static void ForgetPeer(string name)
    {
        FleetConfig.Update(c => c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name)));
        lock (LastStatus) LastStatus.Remove(Sanitize(name));
    }

    private static bool SameHost(string a, string b)
        => a.Split(':')[0].Equals(b.Split(':')[0], StringComparison.OrdinalIgnoreCase);

    private static bool IsThisPc(string address)
    {
        var host = address.Split(':')[0];
        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || host is "localhost" or "127.0.0.1"
               || FleetNetwork.LocalAddresses().Contains(host);
    }

    private static string AddressOf(string machineKey)
        => FleetConfig.Current.KnownPeers.FirstOrDefault(p => Sanitize(p.Name) == Sanitize(machineKey))?.Address
           ?? throw new InvalidOperationException($"Could not reach {machineKey}. Check that it is on and on the same network.");

    /// <summary>Why commands to a machine would fail, or null when it can be reached.</summary>
    public static string? SigningProblem(MachineInfo machine) => machine.IsLocal ? null : machine.ConnectionProblem.Length > 0 ? machine.ConnectionProblem : null;

    public static async Task<string> SendCommandAsync(string machineKey, string target, string targetKey, string action,
        string runnerName = "", string[]? runnerLabels = null, string value = "")
    {
        var command = new MachineCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Target = target,
            TargetKey = targetKey,
            Action = action,
            RunnerName = runnerName,
            RunnerLabels = runnerLabels ?? Array.Empty<string>(),
            Value = value
        };
        var response = await FleetNetwork.CallAsync(AddressOf(machineKey), Sanitize(machineKey),
            new FleetRequest { Op = "command", Command = command });
        return string.IsNullOrEmpty(response.CommandId) ? command.Id : response.CommandId;
    }

    /// <summary>Reads the tail of one of another PC's logs directly from its agent.</summary>
    public static async Task<LogPayload> RequestRemoteLogAsync(string machineKey, string logKey, CancellationToken cancellationToken)
    {
        var response = await FleetNetwork.CallAsync(AddressOf(machineKey), Sanitize(machineKey),
            new FleetRequest { Op = "log", LogKey = logKey }, TimeSpan.FromSeconds(45), cancellationToken);
        return response.Log ?? throw new InvalidOperationException("The PC returned no log.");
    }

    /// <summary>
    /// Waits until a machine's agent reports that it processed a command, and returns its result.
    /// Throws when the agent reports a failure or doesn't answer in time.
    /// </summary>
    public static async Task<string> WaitForCommandAsync(string machineKey, string commandId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            MachineStatusPayload? status;
            try { status = (await FleetNetwork.CallAsync(AddressOf(machineKey), Sanitize(machineKey), new FleetRequest { Op = "status" })).Status; }
            catch { continue; }
            if (status == null || !status.LastCommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase)) continue;
            if (status.LastCommandResult.Contains("failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(status.LastCommandResult);
            return status.LastCommandResult;
        }
        throw new TimeoutException($"{machineKey} did not report a result within {(int)timeout.TotalMinutes} minutes. Check its fleet agent log.");
    }

    // ================= Agent =================

    public static async Task RunAgentAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        using var mutex = new Mutex(initiallyOwned: true, AgentMutexName, out var createdNew);
        if (!createdNew)
        {
            AppendAgentLog("Another fleet agent is already running. Exiting.");
            return;
        }

        var state = LoadAgentState();
        var lastResult = $"Fleet agent v{AppUpdater.CurrentVersion} started";
        AppendAgentLog($"Fleet agent v{AppUpdater.CurrentVersion} started (protocol {ProtocolVersion}) from {Environment.ProcessPath}.");

        try { await FleetNetwork.EnsureFirewallRulesAsync(); }
        catch (Exception ex) { AppendAgentLog($"Could not add firewall rules: {ex.Message}"); }

        var queue = Channel.CreateUnbounded<MachineCommand>();
        string Enqueue(MachineCommand command)
        {
            if (!state.Processed.Contains(command.Id)) queue.Writer.TryWrite(command);
            return command.Id;
        }
        async Task<MachineStatusPayload> StatusAsync()
        {
            await StatusGate.WaitAsync();
            try
            {
                // Several dashboards may poll at once; build the (WMI-heavy) status at most every two seconds.
                if (_cachedStatus == null || DateTime.UtcNow - _cachedStatusUtc > TimeSpan.FromSeconds(2))
                {
                    _cachedStatus = BuildPayload(await GatherLocalStateAsync(), state.LastCommandId, lastResult);
                    _cachedStatusUtc = DateTime.UtcNow;
                }
                return _cachedStatus with { UpdatedUtc = DateTime.UtcNow };
            }
            finally { StatusGate.Release(); }
        }

        using var server = new AgentServer(StatusAsync, Enqueue, LogReader.ReadAsync);
        try { server.Start(); }
        catch (Exception ex) { AppendAgentLog($"Could not listen on port {FleetNetwork.AgentPort}: {ex.Message}"); }

        var initialRdc = RemoteDesktopManager.ReadSnapshot();
        if (RemoteDesktopManager.IsConfigured() && !IsManuallyStopped("rdc"))
        {
            try
            {
                if (!initialRdc.Running)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    await RemoteDesktopManager.StartAsync();
                    lastResult = "Fleet agent started. Remote Desktop Commander relay connection verified.";
                }
                else if (initialRdc.ConnectionKnown && !initialRdc.Connected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    await RemoteDesktopManager.RestartAsync();
                    lastResult = "Fleet agent started. Remote Desktop Commander relay reconnected.";
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppendAgentLog($"Remote Desktop Commander start failed: {ex.Message}"); }
        }

        var lastWatchdogUtc = DateTime.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    wait.CancelAfter(WatchdogInterval);
                    try { await queue.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }

                while (queue.Reader.TryRead(out var command))
                {
                    if (state.Processed.Contains(command.Id)) continue;
                    try { lastResult = await ExecuteCommandAsync(command); }
                    catch (Exception ex)
                    {
                        lastResult = $"{command.Target} {command.Action} failed: {ex.Message}";
                        AppendAgentLog(lastResult);
                    }
                    finally
                    {
                        state.MarkProcessed(command.Id);
                        SaveAgentState(state);
                        _cachedStatus = null;
                    }
                    if (_restartAgentAfterUpdate)
                    {
                        AppendAgentLog("Restarting to finish the update.");
                        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None); // let the dashboard read the result
                        Environment.Exit(0);
                    }
                }

                if (DateTime.UtcNow - lastWatchdogUtc >= WatchdogInterval)
                {
                    lastWatchdogUtc = DateTime.UtcNow;
                    if (await RunWatchdogAsync(await GatherLocalStateAsync())) _cachedStatus = null;
                }
            }
            catch (Exception ex)
            {
                AppendAgentLog(ex.Message);
                await Task.Delay(1000, CancellationToken.None);
            }
        }
    }

    private static async Task<string> ExecuteCommandAsync(MachineCommand command)
    {
        AppendAgentLog($"Running command: {command.Target} {command.Action} {command.TargetKey}".TrimEnd());
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
            else throw new InvalidOperationException($"Unsupported Remote Desktop Commander action: {command.Action}");
            FleetConfig.SetManuallyStopped("rdc", command.Action.Equals("Stop", StringComparison.OrdinalIgnoreCase));
            if (command.Action.Equals("Stop", StringComparison.OrdinalIgnoreCase))
            {
                WantRunning.Remove("rdc");
                return "Remote Desktop Commander stop completed";
            }
            var relay = await RemoteDesktopManager.ProbeRelayAsync();
            var connected = relay.Known ? relay.Connected : RemoteDesktopManager.ReadSnapshot().Connected;
            return connected
                ? $"Remote Desktop Commander {command.Action.ToLowerInvariant()} completed. Relay connected."
                : $"Remote Desktop Commander {command.Action.ToLowerInvariant()} completed. Relay not connected.";
        }

        if (command.Target.Equals("runner", StringComparison.OrdinalIgnoreCase))
        {
            switch (command.Action)
            {
                case "Edit":
                    await RunnerManager.EditRunnerAsync(command.TargetKey, command.RunnerName, command.RunnerLabels,
                        message => AppendAgentLog($"Runner edit: {message}"));
                    return $"Runner edit completed: {command.RunnerName}";
                case "CleanWork":
                    await RunnerManager.CleanWorkFolderAsync(command.TargetKey, message => AppendAgentLog($"Clean work folder: {message}"));
                    return "Work folder cleaned";
                case "Remove":
                    await RunnerManager.RemoveRunnerAsync(command.TargetKey, command.Value == "delete", message => AppendAgentLog($"Remove runner: {message}"));
                    FleetConfig.SetManuallyStopped(command.TargetKey, false);
                    return "Runner removed";
                case "Convert":
                    await RunnerManager.ConvertToServiceAsync(command.TargetKey, message => AppendAgentLog($"Convert to service: {message}"));
                    return "Runner converted to a Windows service";
                case "Create":
                    // Used when moving a runner here: same name and labels, replacing the registration on the old PC.
                    var url = RunnerManager.NormalizeRegistrationUrl(command.TargetKey);
                    await RunnerManager.CreateRunnerAsync(new RunnerSetup
                    {
                        RepositoryUrl = url,
                        RunnerName = command.RunnerName,
                        InstallPath = RunnerManager.SuggestInstallPath(url),
                        Labels = command.RunnerLabels,
                        ReplaceExisting = command.Value == "replace"
                    }, message => AppendAgentLog($"Create runner: {message}"));
                    return $"Runner created: {command.RunnerName}";
                case "Retire":
                    await RunnerManager.RetireMovedRunnerAsync(command.TargetKey, command.Value == "delete", message => AppendAgentLog($"Retire runner: {message}"));
                    return "Runner removed from the old PC";
                case "Start" or "Stop" or "Restart":
                    await RunnerManager.ControlServiceAsync(command.TargetKey, command.Action);
                    FleetConfig.SetManuallyStopped(command.TargetKey, command.Action == "Stop");
                    if (command.Action == "Stop") WantRunning.Remove(command.TargetKey);
                    return $"Runner {command.Action.ToLowerInvariant()} completed";
                default:
                    throw new InvalidOperationException($"Unsupported runner action: {command.Action}");
            }
        }

        if (command.Target.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            switch (command.Action)
            {
                case "Watchdog":
                    var enabled = !command.Value.Equals("off", StringComparison.OrdinalIgnoreCase);
                    FleetConfig.Update(c => c.Watchdog = enabled);
                    return enabled ? "Auto-recovery turned on" : "Auto-recovery turned off";
                case "Update":
                    return await UpdateSelfAsync(force: command.Value.Equals("force", StringComparison.OrdinalIgnoreCase));
                default:
                    throw new InvalidOperationException($"Unsupported agent action: {command.Action}");
            }
        }

        if (command.Target.Equals("machine", StringComparison.OrdinalIgnoreCase))
        {
            switch (command.Action)
            {
                case "Reboot":
                    var from = string.IsNullOrWhiteSpace(command.Value) ? "another PC" : command.Value;
                    var (code, output) = await ScheduledTasks.RunAsync("shutdown.exe",
                        $"/r /t 60 /c \"Runner Manager: restart requested from {from.Replace("\"", "")}\"");
                    if (code != 0) throw new InvalidOperationException(output.Trim());
                    AddEvent($"Restart requested from {from}. Restarting in 60 seconds.", warning: true);
                    return "Restarting in 60 seconds";
                case "CancelReboot":
                    await ScheduledTasks.RunAsync("shutdown.exe", "/a");
                    return "Restart cancelled";
                default:
                    throw new InvalidOperationException($"Unsupported PC action: {command.Action}");
            }
        }

        throw new InvalidOperationException($"Unsupported target: {command.Target}");
    }

    private static async Task<string> UpdateSelfAsync(bool force)
    {
        var release = await AppUpdater.GetLatestReleaseAsync(force: true)
            ?? throw new InvalidOperationException($"No Runner Manager release found in {AppUpdater.ReleaseRepository}.");
        if (!force && !AppUpdater.IsNewer(release.Version, AppUpdater.CurrentVersion) && AppUpdater.IsRunningInstalledCopy)
            return $"Already up to date (v{AppUpdater.CurrentVersion})";
        var exe = await AppUpdater.DownloadAndInstallAsync(release, message => AppendAgentLog($"Update: {message}"));
        AppUpdater.ScheduleAgentTaskRestart(ScheduledTasks.AgentTaskName);
        await RegisterAgentTaskAsync(exe);
        _restartAgentAfterUpdate = true;
        AddEvent($"Updated to {release.Tag}. Restarting the fleet agent.");
        return $"Updated to {release.Tag}. Restarting.";
    }

    /// <summary>
    /// Restarts runners and Remote Desktop Commander that stopped unexpectedly. Only things this agent has seen
    /// running (and the user didn't stop on purpose) are recovered, at most three times an hour each.
    /// </summary>
    private static async Task<bool> RunWatchdogAsync(LocalState local)
    {
        var config = FleetConfig.Current;
        var acted = false;
        foreach (var runner in local.Runners.Where(r => r.ServiceInstalled))
        {
            if (runner.IsRunning)
            {
                WantRunning.Add(runner.Name);
                continue;
            }
            if (!config.Watchdog || !WantRunning.Contains(runner.Name) || IsManuallyStopped(runner.Name)) continue;
            if (!TryUseRecoveryAttempt(runner.Name, $"runner {runner.AgentName}")) continue;
            try
            {
                await RunnerManager.ControlServiceAsync(runner.Name, "Start");
                AddEvent($"Auto-recovery restarted runner {runner.AgentName}", warning: true);
            }
            catch (Exception ex) { AddEvent($"Auto-recovery could not start runner {runner.AgentName}: {ex.Message}", warning: true); }
            acted = true;
        }

        if (RemoteDesktopManager.IsConfigured())
        {
            var connected = local.Relay.Known ? local.Relay.Connected : local.Rdc.Connected;
            if (local.Rdc.Running && connected)
            {
                WantRunning.Add("rdc");
                _rdcUnhealthyChecks = 0;
            }
            else if (config.Watchdog && WantRunning.Contains("rdc") && !IsManuallyStopped("rdc")
                     && (!local.Rdc.Running || local.Relay.Known))
            {
                // Two checks in a row (about 20 seconds) so a brief relay blip doesn't trigger a restart.
                if (++_rdcUnhealthyChecks >= 2 && TryUseRecoveryAttempt("rdc", "Remote Desktop Commander"))
                {
                    _rdcUnhealthyChecks = 0;
                    try
                    {
                        if (local.Rdc.Running) await RemoteDesktopManager.RestartAsync();
                        else await RemoteDesktopManager.StartAsync();
                        AddEvent("Auto-recovery reconnected Remote Desktop Commander", warning: true);
                    }
                    catch (Exception ex) { AddEvent($"Auto-recovery could not reconnect Remote Desktop Commander: {ex.Message}", warning: true); }
                    acted = true;
                }
            }
        }
        return acted;
    }

    private static bool TryUseRecoveryAttempt(string key, string label)
    {
        if (!RecoveryAttempts.TryGetValue(key, out var attempts)) RecoveryAttempts[key] = attempts = new List<DateTime>();
        attempts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromHours(1));
        if (attempts.Count >= 3)
        {
            if (attempts.Count == 3)
            {
                AddEvent($"Auto-recovery stopped for {label} after 3 attempts in one hour", warning: true);
                attempts.Add(DateTime.UtcNow); // remember that we already reported it
            }
            return false;
        }
        attempts.Add(DateTime.UtcNow);
        return true;
    }

    private static bool IsManuallyStopped(string key)
        => FleetConfig.Current.ManuallyStopped.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static void AddEvent(string text, bool warning = false)
    {
        AppendAgentLog(text);
        lock (RecentEvents)
        {
            RecentEvents.Add(new AgentEvent { Utc = DateTime.UtcNow, Text = text, Warning = warning });
            while (RecentEvents.Count > 8) RecentEvents.RemoveAt(0);
        }
    }

    // ================= Agent task =================

    public static Task<bool> IsAgentInstalledAsync() => ScheduledTasks.ExistsAsync(ScheduledTasks.AgentTaskName);

    public static Task<bool> IsAgentRunningAsync() => Task.Run(() =>
    {
        // Checked via the agent's single-instance mutex instead of parsing schtasks output,
        // which is localized ("Running" is not what a German Windows prints).
        try
        {
            using var mutex = Mutex.OpenExisting(AgentMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { return true; }

        // Agents built before the mutex existed: look for the process itself.
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'RunnerWidget.exe'");
            foreach (System.Management.ManagementObject process in searcher.Get())
            {
                if (Convert.ToInt32(process["ProcessId"]) == Environment.ProcessId) continue;
                // An elevated agent's command line is hidden from non-elevated callers; treat it as the agent.
                var commandLine = process["CommandLine"]?.ToString();
                if (commandLine == null || commandLine.Contains("--agent", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch { return false; }
    });

    public static async Task RemoveAgentTaskAsync()
    {
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.AgentTaskName);
        await FleetNetwork.RemoveFirewallRulesAsync();
    }

    /// <summary>
    /// Leaves the fleet: tells the other PCs to forget this one, removes the agent, and clears the pairing code and
    /// remembered PCs, so adding it again goes through pairing. Returns the PCs that were told.
    /// </summary>
    public static async Task<List<string>> LeaveFleetAsync(Action<string> log)
    {
        var told = new List<string>();
        var config = FleetConfig.Current;
        if (config.FleetKey != null)
        {
            var peers = config.KnownPeers.Where(p => Sanitize(p.Name) != LocalMachineKey).ToList();
            if (peers.Count > 0) log($"Notifying {string.Join(", ", peers.Select(p => p.Name))} that this PC is leaving...");
            await Task.WhenAll(peers.Select(async peer =>
            {
                try
                {
                    await FleetNetwork.CallAsync(peer.Address, Sanitize(peer.Name),
                        new FleetRequest { Op = "leave", From = Environment.MachineName }, TimeSpan.FromSeconds(4));
                    lock (told) told.Add(peer.Name);
                }
                catch { }
            }));
        }
        log("Removing the fleet agent and its firewall rules...");
        await RemoveAgentTaskAsync();
        FleetConfig.Update(c =>
        {
            c.ProtectedFleetKey = string.Empty;
            c.KnownPeers.Clear();
        });
        lock (LastStatus) LastStatus.Clear();
        return told;
    }

    public static async Task InstallAndStartAgentTaskAsync()
    {
        // Prefer the installed release copy so the agent doesn't depend on a build folder that gets rebuilt.
        var exe = File.Exists(AppUpdater.InstalledExe) ? AppUpdater.InstalledExe : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run RunnerWidget.exe from a build output before installing the fleet agent.");
        // Replace a running (possibly older) agent: stop it, re-register, start the new one.
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await RegisterAgentTaskAsync(exe);
        await FleetNetwork.EnsureFirewallRulesAsync();
        await Task.Delay(1000);
        var (code, output) = await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        if (code != 0) throw new InvalidOperationException(output.Trim());
    }

    /// <summary>Restarts the local agent (after an update or a settings change that needs it).</summary>
    public static async Task RestartLocalAgentAsync()
    {
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await Task.Delay(1500);
        await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
    }

    /// <summary>The executable the installed agent task runs, or empty when the task doesn't exist.</summary>
    public static async Task<string> AgentTaskExecutableAsync()
        => ScheduledTasks.CommandOf(await ScheduledTasks.QueryXmlAsync(ScheduledTasks.AgentTaskName));

    /// <summary>
    /// Tasks created by older versions used schtasks defaults: they refuse to start on battery,
    /// are killed when a laptop unplugs, and are force-stopped after 72 hours. Re-register those
    /// with always-on settings, keeping the executable path the task already points to.
    /// </summary>
    public static async Task<bool> RepairAgentTaskSettingsAsync()
    {
        var xml = await ScheduledTasks.QueryXmlAsync(ScheduledTasks.AgentTaskName);
        if (string.IsNullOrWhiteSpace(xml)) return false;
        var healthy = xml.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", StringComparison.OrdinalIgnoreCase)
            && xml.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", StringComparison.OrdinalIgnoreCase)
            && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase);
        if (healthy) return false;

        var exe = ScheduledTasks.CommandOf(xml);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) exe = Environment.ProcessPath ?? string.Empty;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        await RegisterAgentTaskAsync(exe);
        // If re-registering ended the running instance this starts it again; otherwise the task's
        // IgnoreNew policy (and the agent's own mutex) make this a no-op.
        await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        return true;
    }

    internal static Task RegisterAgentTaskAsync(string exe) => ScheduledTasks.RegisterLogonTaskAsync(
        ScheduledTasks.AgentTaskName, exe, "--agent",
        "Runner Manager fleet agent. Reports runner and Remote Desktop Commander status and runs commands from other PCs.", restartOnFailure: true);

    // ================= Local status =================

    /// <summary>Local status as a MachineInfo, for showing this PC before it is enrolled or has published.</summary>
    public static async Task<MachineInfo> ReadLocalMachineAsync(string lastResult, bool agentOutdated = false)
    {
        var payload = BuildPayload(await GatherLocalStateAsync(), LoadAgentState().LastCommandId, lastResult);
        return MachineInfo.FromPayload(LocalMachineKey, payload, localOnly: true, localAgentOutdated: agentOutdated);
    }

    private sealed record LocalState(List<RunnerInfo> Runners, RemoteInfo Rdc, RemoteRelayProbe Relay, bool AgentRunning, HealthSnapshot Health);

    private static async Task<LocalState> GatherLocalStateAsync()
    {
        var runnersTask = RunnerManager.ReadSnapshotAsync(queryGitHub: false);
        var relayTask = RemoteDesktopManager.ProbeRelayAsync();
        var agentTask = IsAgentRunningAsync();
        var remote = await Task.Run(RemoteDesktopManager.ReadSnapshot);
        var runners = await runnersTask;
        var health = await Task.Run(() => SystemHealth.Read(runners.Select(r => r.InstallPath)));
        return new LocalState(runners, remote, await relayTask, await agentTask, health);
    }

    private static MachineStatusPayload BuildPayload(LocalState local, string lastCommandId, string lastResult)
    {
        var (runners, remote, relay) = (local.Runners, local.Rdc, local.Relay);
        var config = FleetConfig.Current;
        AgentEvent[] events;
        lock (RecentEvents) events = RecentEvents.ToArray();
        return new MachineStatusPayload
        {
            MachineName = Environment.MachineName,
            AgentProtocolVersion = ProtocolVersion,
            AppVersion = AppUpdater.CurrentVersion,
            UpdatedUtc = DateTime.UtcNow,
            FleetAgentRunning = local.AgentRunning,
            RdcRunning = remote.Running,
            RdcConnected = relay.Known ? relay.Connected : remote.Connected,
            RdcConnectionKnown = relay.Known || remote.ConnectionKnown,
            RdcConnectionChangedUtc = relay.Known ? relay.ChangedUtc : remote.ConnectionChangedUtc,
            RdcConnectionText = relay.Known ? relay.Detail : remote.ConnectionText,
            RdcConfigured = RemoteDesktopManager.IsConfigured(),
            RdcProcessCount = remote.Pids.Length,
            RdcPids = remote.Pids,
            RdcStartedUtc = remote.Started?.ToUniversalTime(),
            RdcActiveCalls = remote.ActiveCalls,
            RdcLastUsedUtc = remote.LastUsedUtc,
            RdcLastTool = remote.LastTool,
            RdcSecurityText = remote.SecurityText,
            LastCommandId = lastCommandId,
            LastCommandResult = lastResult,
            CpuPercent = local.Health.CpuPercent,
            MemoryPercent = local.Health.MemoryPercent,
            MemoryTotalGb = local.Health.MemoryTotalGb,
            BootUtc = new DateTime(local.Health.BootUtc.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc),
            RebootPending = local.Health.RebootPending,
            Disks = local.Health.Disks,
            WatchdogEnabled = config.Watchdog,
            SigningKeyId = CommandSigner.KeyId(config.FleetKey),
            RecentEvents = events,
            Runners = runners.Select(r => new MachineRunnerStatus
            {
                ServiceName = r.Name,
                InstallPath = r.InstallPath,
                IsInteractive = r.IsInteractive,
                DisplayName = r.DisplayName,
                RepositoryPath = r.RepositoryPath,
                Scope = r.ApiScope,
                AgentName = r.AgentName,
                AgentId = r.AgentId,
                IsRunning = r.IsRunning,
                ActivityText = r.ActivityText,
                ServiceInstalled = r.ServiceInstalled,
                RunnerVersion = r.RunnerVersion,
                WorkFolderBytes = r.WorkFolderBytes,
                ManuallyStopped = config.ManuallyStopped.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
            }).ToArray()
        };
    }

    // ================= Old GitHub relay =================

    /// <summary>
    /// Deletes the RW_STATUS_/RW_COMMAND_/RW_LOG_ Actions variables older versions used as a relay.
    /// Returns how many were deleted.
    /// </summary>
    public static async Task<int> DeleteLegacyRelayDataAsync(string repository)
    {
        var list = await GitHubCli.RunAsync($"api repos/{repository}/actions/variables?per_page=100 --jq \".variables[].name\"");
        if (list.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(list.Error) ? "Could not list variables." : list.Error.Trim());
        var deleted = 0;
        foreach (var name in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!name.StartsWith("RW_STATUS_") && !name.StartsWith("RW_COMMAND_") && !name.StartsWith("RW_LOG_")) continue;
            var result = await GitHubCli.RunAsync($"api --method DELETE repos/{repository}/actions/variables/{name}");
            if (result.ExitCode == 0) deleted++;
        }
        return deleted;
    }

    public static string Encode<T>(T value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static T? Decode<T>(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<T>(json);
        }
        catch { return default; }
    }

    public static string Sanitize(string value)
    {
        var chars = value.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static AgentState LoadAgentState()
    {
        try
        {
            if (!File.Exists(AgentStatePath)) return new AgentState();
            using var json = JsonDocument.Parse(File.ReadAllText(AgentStatePath));
            var state = new AgentState();
            if (json.RootElement.TryGetProperty("processed", out var processed) && processed.ValueKind == JsonValueKind.Array)
                foreach (var id in processed.EnumerateArray())
                    if (id.GetString() is { Length: > 0 } value) state.MarkProcessed(value);
            if (json.RootElement.TryGetProperty("lastCommandId", out var last) && last.GetString() is { Length: > 0 } lastId)
                state.MarkProcessed(lastId);
            return state;
        }
        catch { return new AgentState(); }
    }

    private static void SaveAgentState(AgentState state)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        File.WriteAllText(AgentStatePath, JsonSerializer.Serialize(new
        {
            lastCommandId = state.LastCommandId,
            processed = state.Recent
        }));
    }

    public static void AppendAgentLog(string message)
    {
        try
        {
            Directory.CreateDirectory(FleetConfig.StateDir);
            var log = new FileInfo(AgentLogPath);
            if (log.Exists && log.Length > MaxAgentLogBytes)
                File.Move(AgentLogPath, AgentLogPath + ".1", overwrite: true);
            File.AppendAllText(AgentLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed class AgentState
    {
        private readonly LinkedList<string> _recent = new();
        private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
        public string LastCommandId { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> Processed => _set;
        public string[] Recent => _recent.ToArray();

        public void MarkProcessed(string id)
        {
            LastCommandId = id;
            if (!_set.Add(id)) return;
            _recent.AddLast(id);
            while (_recent.Count > 50)
            {
                _set.Remove(_recent.First!.Value);
                _recent.RemoveFirst();
            }
        }
    }
}

/// <summary>Reads the tail of the agent, Remote Desktop Commander or runner logs (locally, or for another PC's dashboard).</summary>
public static class LogReader
{
    private const int MaxLines = 400;
    private const int MaxBytes = 30_000;

    /// <param name="logKey">"agent", "rdc", or "runner:&lt;service name&gt;".</param>
    public static async Task<LogPayload> ReadAsync(string logKey)
    {
        string title;
        string? path;
        if (logKey.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            title = $"Fleet agent log — {Environment.MachineName}";
            path = MachineRelay.AgentLogPath;
        }
        else if (logKey.Equals("rdc", StringComparison.OrdinalIgnoreCase))
        {
            title = $"Remote Desktop Commander log — {Environment.MachineName}";
            path = RemoteDesktopManager.RuntimeLogFile;
        }
        else if (logKey.StartsWith("runner:", StringComparison.OrdinalIgnoreCase))
        {
            var service = logKey[7..];
            var runner = await RunnerManager.FindRunnerAsync(service);
            title = $"{runner?.AgentName ?? service} diagnostic log — {Environment.MachineName}";
            path = runner == null ? null : NewestLog(Path.Combine(runner.InstallPath, "_diag"));
            if (path != null) title += $"  ({Path.GetFileName(path)})";
        }
        else throw new InvalidOperationException($"Unknown log: {logKey}");

        var text = path != null && File.Exists(path) ? Tail(path) : "(No log file yet.)";
        return new LogPayload { Title = title, Text = text, CreatedUtc = DateTime.UtcNow };
    }

    private static string? NewestLog(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        return new DirectoryInfo(folder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    public static string Tail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - 256 * 1024);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n').ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // first line is probably partial
        return Truncate(lines.TakeLast(MaxLines));
    }

    /// <summary>Joins lines from the end backwards until the UTF-8 size budget is used up.</summary>
    public static string Truncate(IEnumerable<string> lines)
    {
        var kept = new List<string>();
        var bytes = 0;
        foreach (var line in lines.Reverse())
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + size > MaxBytes) break;
            kept.Add(line);
            bytes += size;
        }
        kept.Reverse();
        return string.Join('\n', kept).TrimEnd();
    }
}

public sealed record LogPayload
{
    public string RequestId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
}

public sealed record MachineStatusPayload
{
    public string MachineName { get; init; } = string.Empty;
    public int AgentProtocolVersion { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
    public bool FleetAgentRunning { get; init; }
    public bool RdcRunning { get; init; }
    public bool RdcConnected { get; init; }
    public bool RdcConnectionKnown { get; init; }
    public DateTime? RdcConnectionChangedUtc { get; init; }
    public string RdcConnectionText { get; init; } = string.Empty;
    public bool RdcConfigured { get; init; }
    public int RdcProcessCount { get; init; }
    public uint[] RdcPids { get; init; } = Array.Empty<uint>();
    public DateTime? RdcStartedUtc { get; init; }
    public int RdcActiveCalls { get; init; }
    public DateTime? RdcLastUsedUtc { get; init; }
    public string RdcLastTool { get; init; } = string.Empty;
    public string RdcSecurityText { get; init; } = string.Empty;
    public string LastCommandId { get; init; } = string.Empty;
    public string LastCommandResult { get; init; } = string.Empty;
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public double MemoryTotalGb { get; init; }
    public DateTime? BootUtc { get; init; }
    public bool RebootPending { get; init; }
    public DiskStatus[] Disks { get; init; } = Array.Empty<DiskStatus>();
    public bool WatchdogEnabled { get; init; }
    /// <summary>Fingerprint of this PC's fleet key; non-empty means it only accepts signed commands.</summary>
    public string SigningKeyId { get; init; } = string.Empty;
    public AgentEvent[] RecentEvents { get; init; } = Array.Empty<AgentEvent>();
    public MachineRunnerStatus[] Runners { get; init; } = Array.Empty<MachineRunnerStatus>();

    /// <summary>
    /// Everything except values that tick on their own (our heartbeat, the relay's last_seen, CPU and memory load),
    /// so unchanged status can skip a publish or a UI rebuild.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(this with
    {
        UpdatedUtc = default,
        RdcConnectionChangedUtc = null,
        CpuPercent = 0,
        MemoryPercent = 0
    });
}

public sealed record MachineCommand
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public string Target { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string RunnerName { get; init; } = string.Empty;
    public string[] RunnerLabels { get; init; } = Array.Empty<string>();
    public string Value { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MachineCommand[]? Queue { get; init; }

    public IReadOnlyList<MachineCommand> AllCommands()
        => Queue is { Length: > 0 } ? Queue : string.IsNullOrEmpty(Id) ? Array.Empty<MachineCommand>() : new[] { this with { Queue = null } };
}
