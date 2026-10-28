using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RunnerManager;

public static class MachineRelay
{
    /// <summary>
    /// 11: answers "job" requests (live output and processes of a runner's job).
    /// 12: machine IDs, recent command results, busy state, and "rekey" (Change fleet code).
    /// </summary>
    public const int ProtocolVersion = 12;
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
    // Bumped whenever the cached status goes stale, so a status build already in progress does not store an old result.
    private static int _statusVersion;
    private static int _rdcUnhealthyChecks;
    private static volatile bool _restartAgentAfterUpdate;
    /// <summary>How long an update waits for the agent's work to finish before it restarts the agent anyway.</summary>
    private static readonly TimeSpan UpdateRestartWait = TimeSpan.FromMinutes(15);
    /// <summary>The longest a caller waits for a command its agent still reports as queued or running.</summary>
    private static readonly TimeSpan MaxCommandWait = TimeSpan.FromMinutes(30);

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

        // Discovery answers are signed but not tied to the address they come from: anyone on the network can forward
        // one. So an answer from a new address is only a candidate, used when the PC can't be reached where it was
        // last; the address changes once an encrypted request there succeeds. This also keeps a PC with several
        // network adapters at one address.
        var candidates = new Dictionary<string, List<DiscoveredPeer>>(StringComparer.OrdinalIgnoreCase);
        if (forceDiscovery || DateTime.UtcNow - _lastDiscovery > DiscoveryInterval)
        {
            _lastDiscovery = DateTime.UtcNow;
            var hints = config.KnownPeers.Select(p => FleetNetwork.HostOf(p.Address)).Concat(config.ManualAddresses);
            var localId = FleetConfig.LocalMachineId;
            foreach (var peer in await FleetNetwork.DiscoverAsync(hints, TimeSpan.FromSeconds(1.2)))
            {
                if (peer.MachineId.Length > 0 && peer.MachineId == localId) continue;
                NoteSighting(peer.Name, peer.MachineId, peer.Address);
                var key = Sanitize(peer.Name);
                // Another PC with this PC's name is reported through the sightings.
                if (key == LocalMachineKey) continue;
                var known = config.KnownPeers.FirstOrDefault(p => Sanitize(p.Name) == key);
                if (known != null && SameEndpoint(known.Address, peer.Address)) continue;
                if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = new List<DiscoveredPeer>();
                list.Add(peer);
            }
        }

        // Drop any entry for this PC itself (older builds could record it under its sanitized name).
        if (FleetConfig.Current.KnownPeers.Any(p => Sanitize(p.Name) == LocalMachineKey))
            FleetConfig.Update(c => c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == LocalMachineKey));
        config = FleetConfig.Current;
        var peers = config.KnownPeers.ToList();
        var unnamed = config.ManualAddresses
            .Where(a => !peers.Any(p => SameHost(p.Address, a)) && !IsThisPc(a))
            .ToList();
        var fetches = peers.Select(p => FetchPeerAsync(p, p.Name, p.Address, candidates.GetValueOrDefault(Sanitize(p.Name))))
            // PCs found by discovery that aren't known yet are listed once they answer.
            .Concat(candidates.Where(c => !peers.Any(p => Sanitize(p.Name) == c.Key))
                .Select(c => FetchPeerAsync(null, c.Value[0].Name, string.Empty, c.Value, listIfUnreachable: false)))
            .Concat(unnamed.Select(a => FetchPeerAsync(null, null, a, null)));
        var results = await Task.WhenAll(fetches);
        machines.AddRange(results.OfType<MachineInfo>().Where(m => !m.IsLocal)
            .GroupBy(m => m.MachineKey).Select(g => g.First())
            .OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase));
        if (DuplicateNameProblem(LocalMachineKey) is { } problem)
            machines.Add(MachineInfo.Placeholder("DUPLICATE_" + LocalMachineKey, $"{Environment.MachineName} (another PC)",
                DateTime.UtcNow, OtherAddresses(LocalMachineKey), problem));
        return machines;
    }

    /// <summary>This PC as its agent reports it (includes auto-recovery events), or read directly when there's no agent.</summary>
    private static async Task<MachineInfo> ReadThisPcAsync()
    {
        var installed = await IsAgentInstalledAsync();
        var running = await IsAgentRunningAsync();
        var outdated = false;
        if (installed && running && FleetConfig.Current.FleetKey != null)
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
                // A failed call alone (an agent still starting, a busy PC) is no reason to reinstall it; only an older
                // build is. Agents from before the direct connection do not listen at all, and their build is older.
                outdated = !await LocalAgentIsCurrentAsync();
            }
        }
        else if (installed && running)
        {
            outdated = !await LocalAgentIsCurrentAsync();
        }
        return await ReadLocalMachineAsync(outdated, running);
    }

    /// <summary>Whether the agent task runs this exact build (used before a pairing code exists).</summary>
    private static async Task<bool> LocalAgentIsCurrentAsync()
    {
        var exe = await AgentTaskExecutableAsync();
        return await Task.Run(() =>
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty;
            return !AppUpdater.IsNewer(AppUpdater.CurrentVersion, version);
        });
    }

    /// <summary>
    /// Points this PC's agent at an installed copy of the running build and restarts it, so it matches this dashboard.
    /// </summary>
    public static async Task UpdateLocalAgentAsync(Action<string> log)
    {
        var exe = await AppUpdater.InstallCurrentBuildAsync(log);
        log("Restarting the fleet agent...");
        await ReplaceAndStartAgentTaskAsync(exe, log);
        log($"Fleet agent now runs v{AppUpdater.CurrentVersion} from {exe}.");
    }

    /// <summary>
    /// Asks one PC for its status: a known one at its last address and then at addresses discovery found it at,
    /// a newly discovered one, or one added by address only (<paramref name="name"/> null). Returns null for a
    /// discovered PC that does not answer when <paramref name="listIfUnreachable"/> is false.
    /// </summary>
    internal static async Task<MachineInfo?> FetchPeerAsync(PeerRecord? known, string? name, string address,
        IReadOnlyList<DiscoveredPeer>? candidates, bool listIfUnreachable = true)
    {
        var key = name == null ? "*" : Sanitize(name);
        var addresses = new List<string>();
        if (address.Length > 0) addresses.Add(address);
        if (candidates != null) addresses.AddRange(candidates.Select(c => c.Address).Where(a => !addresses.Any(b => SameEndpoint(a, b))));
        var problem = string.Empty;
        foreach (var attempt in addresses)
        {
            try
            {
                var response = await FleetNetwork.CallAsync(attempt, key, new FleetRequest { Op = "status" });
                return Accept(response.Status, attempt);
            }
            catch (PairingMismatchException ex) when (ex.Reason == "wrong PC" && known is { MachineId.Length: > 0 })
            {
                // Maybe the PC was renamed: ask the PC at this address who it is.
                problem = ex.Message;
                try
                {
                    var response = await FleetNetwork.CallAsync(attempt, "*", new FleetRequest { Op = "status" });
                    if (response.Status is { } status && status.MachineId.Equals(known.MachineId, StringComparison.OrdinalIgnoreCase))
                        return Accept(status, attempt);
                }
                catch { }
            }
            catch (PairingMismatchException ex) { problem = ex.Message; }
            catch (Exception) { if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(attempt)}"; }
        }
        if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(address)}";

        if (name == null)
            return MachineInfo.Placeholder("ADDR_" + Sanitize(address), address, DateTime.MinValue, address, problem);
        if (!listIfUnreachable) return null;
        MachineStatusPayload? last;
        lock (LastStatus) LastStatus.TryGetValue(key, out last);
        return last != null
            ? MachineInfo.FromPayload(key, last, address: address, problem: problem)
            : MachineInfo.Placeholder(key, name, known?.LastSeenUtc ?? DateTime.MinValue, address, problem);
    }

    /// <summary>A PC answered at <paramref name="address"/>: remember it there, and list it (with a warning if its name is taken twice).</summary>
    private static MachineInfo Accept(MachineStatusPayload? payload, string address)
    {
        if (payload == null) throw new InvalidOperationException("The PC returned no status.");
        var machineKey = Sanitize(payload.MachineName);
        lock (LastStatus) LastStatus[machineKey] = payload;
        NoteSighting(payload.MachineName, payload.MachineId, address);
        RememberPeer(payload.MachineName, FleetNetwork.WithPort(address), payload.AppVersion, payload.MachineId);
        return MachineInfo.FromPayload(machineKey, payload, address: address, problem: DuplicateNameProblem(machineKey) ?? string.Empty);
    }

    internal static void RememberPeer(string name, string address, string version, string machineId = "")
    {
        if (Sanitize(name) == LocalMachineKey) return;
        var config = FleetConfig.Current;
        // Match on the sanitized key so "DESKTOP-PC" and "DESKTOP_PC" are the same PC, and keep the real name.
        var existing = config.KnownPeers.Where(p => Sanitize(p.Name) == Sanitize(name))
            .OrderByDescending(p => p.Name != Sanitize(p.Name)).FirstOrDefault();
        if (existing != null && name == Sanitize(name) && existing.Name != name) name = existing.Name;
        if (machineId.Length == 0) machineId = existing?.MachineId ?? string.Empty;
        // The same PC under its old name: it was renamed.
        var renamed = machineId.Length == 0 ? new List<PeerRecord>()
            : config.KnownPeers.Where(p => p.MachineId.Equals(machineId, StringComparison.OrdinalIgnoreCase) && Sanitize(p.Name) != Sanitize(name)).ToList();
        // Only write when something meaningful changed, not on every poll.
        var duplicates = config.KnownPeers.Count(p => Sanitize(p.Name) == Sanitize(name)) > 1;
        if (!duplicates && renamed.Count == 0 && existing != null && existing.Name == name && existing.Address == address
            && existing.Version == version && existing.MachineId == machineId
            && DateTime.UtcNow - existing.LastSeenUtc < TimeSpan.FromMinutes(10)) return;
        FleetConfig.Update(c =>
        {
            c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name) || renamed.Any(r => Sanitize(r.Name) == Sanitize(p.Name)));
            c.KnownPeers.Add(new PeerRecord { Name = name, Address = address, Version = version, MachineId = machineId, LastSeenUtc = DateTime.UtcNow });
        });
        foreach (var old in renamed)
        {
            lock (LastStatus) LastStatus.Remove(Sanitize(old.Name));
            AppendAgentLog($"{old.Name} is now called {name}.");
        }
    }

    /// <summary>Remembers a PC learned during pairing so it's contacted right away.</summary>
    public static void AddKnownPeer(string name, string address) => RememberPeer(name, FleetNetwork.WithPort(address), string.Empty);

    public static void ForgetPeer(string name)
    {
        FleetConfig.Update(c => c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name)));
        lock (LastStatus) LastStatus.Remove(Sanitize(name));
    }

    // ----- Two PCs with one name -----

    private sealed record Sighting(string Name, string Address, DateTime SeenUtc);

    // Dashboard side: the machine IDs that answered under each PC name lately.
    private static readonly Dictionary<string, Dictionary<string, Sighting>> Sightings = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How long an answer counts. Discovery runs every 30 seconds, so both PCs are seen within this.</summary>
    private static readonly TimeSpan SightingTime = TimeSpan.FromMinutes(3);

    internal static void NoteSighting(string name, string machineId, string address)
    {
        if (machineId.Length == 0 || name.Length == 0) return;
        lock (Sightings)
        {
            var key = Sanitize(name);
            if (!Sightings.TryGetValue(key, out var ids)) Sightings[key] = ids = new Dictionary<string, Sighting>(StringComparer.OrdinalIgnoreCase);
            ids[machineId] = new Sighting(name, FleetNetwork.HostOf(address), DateTime.UtcNow);
        }
    }

    /// <summary>The PCs (other than this one) that answered under this name within <see cref="SightingTime"/>.</summary>
    private static List<Sighting> RecentSightings(string machineKey)
    {
        var localId = FleetConfig.Current.MachineId;
        lock (Sightings)
        {
            if (!Sightings.TryGetValue(Sanitize(machineKey), out var ids)) return new List<Sighting>();
            foreach (var old in ids.Where(p => DateTime.UtcNow - p.Value.SeenUtc > SightingTime).Select(p => p.Key).ToList()) ids.Remove(old);
            return ids.Where(p => localId.Length == 0 || !p.Key.Equals(localId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToList();
        }
    }

    private static string OtherAddresses(string machineKey) => string.Join(", ", RecentSightings(machineKey).Select(s => s.Address).Distinct());

    /// <summary>
    /// Why a PC name can't be used right now: two PCs answer to it (or to names that differ only in punctuation,
    /// which Runner Manager treats as the same). Commands to it are refused until one is renamed. Null when the name
    /// is unique.
    /// </summary>
    public static string? DuplicateNameProblem(string machineKey)
    {
        var seen = RecentSightings(machineKey);
        var isThisPc = Sanitize(machineKey) == LocalMachineKey;
        if (isThisPc ? seen.Count == 0 : seen.Count < 2) return null;
        var names = seen.Select(s => s.Name).Append(isThisPc ? Environment.MachineName : seen[0].Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var where = string.Join(" and ", seen.Select(s => s.Address).Distinct());
        var who = names.Count > 1
            ? $"{string.Join(" and ", names)} look like the same name to Runner Manager, which ignores punctuation in PC names"
            : isThisPc ? $"Another PC (at {where}) is also named {names[0]}" : $"Two PCs are named {names[0]} (at {where})";
        return $"{who}. Rename one of them in Windows Settings → System → About. Until then, commands to {names[0]} are refused.";
    }

    // ----- Addresses -----

    private static bool SameHost(string a, string b)
        => FleetNetwork.HostOf(a).Equals(FleetNetwork.HostOf(b), StringComparison.OrdinalIgnoreCase);

    private static bool SameEndpoint(string a, string b)
        => FleetNetwork.WithPort(a).Equals(FleetNetwork.WithPort(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsThisPc(string address)
    {
        var host = FleetNetwork.HostOf(address);
        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || host is "localhost" or "127.0.0.1" or "::1"
               || FleetNetwork.LocalAddresses().Contains(host);
    }

    /// <summary>Where to send requests for a PC, and its machine ID (empty when unknown) so only that PC acts on them.</summary>
    private static (string Address, string MachineId) Route(string machineKey)
    {
        // This PC's own agent: AI assistant tools run without administrator rights and hand their work to it.
        if (Sanitize(machineKey) == LocalMachineKey) return ($"127.0.0.1:{FleetNetwork.AgentPort}", string.Empty);
        if (DuplicateNameProblem(machineKey) is { } problem) throw new InvalidOperationException(problem);
        var peer = FleetConfig.Current.KnownPeers.FirstOrDefault(p => Sanitize(p.Name) == Sanitize(machineKey))
                   ?? throw new InvalidOperationException($"Could not reach {machineKey}. Check that it is on and on the same network.");
        return (peer.Address, peer.MachineId);
    }

    /// <summary>Why commands to a machine would fail, or null when it can be reached.</summary>
    public static string? SigningProblem(MachineInfo machine) => machine.IsLocal ? null : machine.ConnectionProblem.Length > 0 ? machine.ConnectionProblem : null;

    /// <param name="commandId">
    /// The command's ID. Pass the same ID again to retry a send whose answer was lost: the agent runs each ID once.
    /// </param>
    public static async Task<string> SendCommandAsync(string machineKey, string target, string targetKey, string action,
        string runnerName = "", string[]? runnerLabels = null, string value = "", string? commandId = null)
    {
        var command = new MachineCommand
        {
            Id = commandId ?? Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Target = target,
            TargetKey = targetKey,
            Action = action,
            RunnerName = runnerName,
            RunnerLabels = runnerLabels ?? Array.Empty<string>(),
            Value = value
        };
        var (address, machineId) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "command", Command = command, TargetId = machineId });
        return string.IsNullOrEmpty(response.CommandId) ? command.Id : response.CommandId;
    }

    /// <summary>A busy runner's live output and processes from the PC it runs on (agents with protocol 11 or later).</summary>
    public static async Task<RunnerJobActivity> RequestJobActivityAsync(string machineKey, string runnerKey, CancellationToken cancellationToken = default)
    {
        var (address, machineId) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "job", LogKey = runnerKey, TargetId = machineId }, TimeSpan.FromSeconds(10), cancellationToken);
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        return response.Job ?? throw new InvalidOperationException("The PC returned no job details.");
    }

    /// <summary>Reads the tail of one of another PC's logs directly from its agent.</summary>
    public static async Task<LogPayload> RequestRemoteLogAsync(string machineKey, string logKey, CancellationToken cancellationToken)
    {
        var (address, machineId) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "log", LogKey = logKey, TargetId = machineId }, TimeSpan.FromSeconds(45), cancellationToken);
        return response.Log ?? throw new InvalidOperationException("The PC returned no log.");
    }

    /// <summary>
    /// Waits until a machine's agent reports that it processed a command, and returns its result. Throws when the agent
    /// reports a failure, or when there is no result after <paramref name="timeout"/>. Agents that report what is
    /// queued and running (protocol 12) are waited for longer while the command still waits or runs, up to
    /// <see cref="MaxCommandWait"/>; the exception then says that it is still running.
    /// </summary>
    public static async Task<string> WaitForCommandAsync(string machineKey, string commandId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var cap = started + (timeout > MaxCommandWait ? timeout : MaxCommandWait);
        var runningSeenUtc = DateTime.MinValue;
        bool StillRunning() => DateTime.UtcNow - runningSeenUtc < TimeSpan.FromMinutes(1);
        while (DateTime.UtcNow < cap && (DateTime.UtcNow < deadline || StillRunning()))
        {
            await Task.Delay(CommandPollInterval, cancellationToken);
            MachineStatusPayload? status;
            try
            {
                var (address, machineId) = Route(machineKey);
                status = (await FleetNetwork.CallAsync(address, Sanitize(machineKey), new FleetRequest { Op = "status", TargetId = machineId })).Status;
            }
            catch { continue; }
            if (status == null) continue;
            var result = (status.RecentCommands ?? Array.Empty<FleetCommandResult>()).FirstOrDefault(r => r.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase))?.Result;
            if (result == null && (status.PendingCommandIds ?? Array.Empty<string>()).Contains(commandId, StringComparer.OrdinalIgnoreCase))
            {
                runningSeenUtc = DateTime.UtcNow;
                continue;
            }
            // Older agents only report the last command.
            if (result == null && status.LastCommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase)) result = status.LastCommandResult;
            if (result == null) continue;
            if (IsFailedResult(result)) throw new InvalidOperationException(result);
            return result;
        }
        var minutes = Math.Max(1, (int)Math.Round((DateTime.UtcNow - started).TotalMinutes));
        throw new TimeoutException(StillRunning()
            ? $"{machineKey} is still running the command after {minutes} minutes. Check its fleet agent log."
            : $"{machineKey} did not report a result within {minutes} minutes. Check its fleet agent log.");
    }

    /// <summary>How often <see cref="WaitForCommandAsync"/> asks for the status (tests shorten it).</summary>
    internal static TimeSpan CommandPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether a command result reports a failure. Agents (2.1 and later) write failures as
    /// "{target} {action} failed: {reason}"; successful results may mention "failed" elsewhere.
    /// </summary>
    internal static bool IsFailedResult(string result)
        => FailedResultPattern.IsMatch(result);

    private static readonly System.Text.RegularExpressions.Regex FailedResultPattern =
        new(@"^(rdc|runner|agent|machine) \S+ failed: ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

        var queue = Channel.CreateUnbounded<MachineCommand>();
        // Commands that are queued or running, reported in the status so callers keep waiting for them.
        var pending = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Runs on HTTP threads. A command sent again with the same ID (a retry after a lost answer) is queued once;
        // the command loop skips IDs it already processed.
        string Enqueue(MachineCommand command)
        {
            if (pending.TryAdd(command.Id, 0)) queue.Writer.TryWrite(command);
            InvalidateStatus();
            return command.Id;
        }
        async Task<MachineStatusPayload> StatusAsync()
        {
            await StatusGate.WaitAsync();
            try
            {
                // Several dashboards may poll at once; build the (WMI-heavy) status at most every two seconds.
                var cached = _cachedStatus;
                if (cached == null || DateTime.UtcNow - _cachedStatusUtc > TimeSpan.FromSeconds(2))
                {
                    var version = Volatile.Read(ref _statusVersion);
                    cached = BuildPayload(await GatherLocalStateAsync(), state.LastCommandId, lastResult);
                    // Only keep it when nothing changed while it was built.
                    if (version == Volatile.Read(ref _statusVersion))
                    {
                        _cachedStatus = cached;
                        _cachedStatusUtc = DateTime.UtcNow;
                    }
                }
                // Pending before results: a command that finishes in between then shows in both, never in neither.
                var running = pending.Keys.ToArray();
                return cached with
                {
                    UpdatedUtc = DateTime.UtcNow,
                    PendingCommandIds = running,
                    RecentCommands = state.Results,
                    Busy = running.Length > 0 || !AgentWork.IsIdle
                };
            }
            finally { StatusGate.Release(); }
        }

        using var server = new AgentServer(StatusAsync, Enqueue, LogReader.ReadAsync, job: RunnerActivity.ReadAsync);
        // A listener that can't start is logged and tried again in the background.
        try { server.Start(); }
        catch { }

        // After the listener is up, so a freshly started agent answers right away.
        try { await FleetNetwork.EnsureFirewallRulesAsync(); }
        catch (Exception ex) { AppendAgentLog($"Could not add firewall rules: {ex.Message}"); }

        // The MCP endpoint for AI assistants, while it is turned on in Settings → AI assistants.
        using var assistantEndpoint = new AssistantHttpServer();
        _ = assistantEndpoint.RunAsync();

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
        DateTime? restartByUtc = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // While an update waits for work to finish, look again every second.
                    wait.CancelAfter(_restartAgentAfterUpdate ? TimeSpan.FromSeconds(1) : WatchdogInterval);
                    try { await queue.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                }

                while (queue.Reader.TryRead(out var command))
                {
                    if (state.HasProcessed(command.Id))
                    {
                        pending.TryRemove(command.Id, out _);
                        InvalidateStatus();
                        continue;
                    }
                    string result;
                    using (AgentWork.Begin())
                    {
                        try { result = await ExecuteCommandAsync(command); }
                        catch (Exception ex)
                        {
                            result = $"{command.Target} {command.Action} failed: {ex.Message}";
                            AppendAgentLog(result);
                        }
                    }
                    lastResult = result;
                    state.MarkProcessed(command.Id, result);
                    pending.TryRemove(command.Id, out _);
                    InvalidateStatus();
                    try { SaveAgentState(state); }
                    catch (Exception ex) { AppendAgentLog($"Could not save the agent state: {ex.Message}"); }
                }

                if (_restartAgentAfterUpdate)
                {
                    // Exit once queued commands and other work (AI assistant calls) are done, or after a generous wait.
                    var quiet = pending.IsEmpty && AgentWork.IsIdle;
                    if (restartByUtc == null && !quiet) AppendAgentLog("Restarting to finish the update once the current work is done.");
                    restartByUtc ??= DateTime.UtcNow + UpdateRestartWait;
                    if (quiet || DateTime.UtcNow >= restartByUtc)
                    {
                        AppendAgentLog(quiet ? "Restarting to finish the update."
                            : $"Restarting to finish the update, although work is still running after {(int)UpdateRestartWait.TotalMinutes} minutes.");
                        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None); // let the dashboard read the result
                        // The helper started with the update gives up after two minutes; this one starts the new agent.
                        AppUpdater.ScheduleAgentTaskRestart(ScheduledTasks.AgentTaskName);
                        Environment.Exit(0);
                    }
                }

                if (DateTime.UtcNow - lastWatchdogUtc >= WatchdogInterval)
                {
                    lastWatchdogUtc = DateTime.UtcNow;
                    if (await RunWatchdogAsync(await GatherLocalStateAsync())) InvalidateStatus();
                }
            }
            catch (Exception ex)
            {
                AppendAgentLog(ex.Message);
                await Task.Delay(1000, CancellationToken.None);
            }
        }
    }

    private static void InvalidateStatus()
    {
        Interlocked.Increment(ref _statusVersion);
        _cachedStatus = null;
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
                    await RunnerService.EditRunnerAsync(command.TargetKey, command.RunnerName, command.RunnerLabels,
                        message => AppendAgentLog($"Runner edit: {message}"));
                    return $"Runner edit completed: {command.RunnerName}";
                case "CleanWork":
                    await RunnerService.CleanWorkFolderAsync(command.TargetKey, message => AppendAgentLog($"Clean work folder: {message}"));
                    return "Work folder cleaned";
                case "Remove":
                    await RunnerService.RemoveRunnerAsync(command.TargetKey, command.Value == "delete", message => AppendAgentLog($"Remove runner: {message}"));
                    FleetConfig.SetManuallyStopped(command.TargetKey, false);
                    return "Runner removed";
                case "Convert":
                    await RunnerService.ConvertToServiceAsync(command.TargetKey, message => AppendAgentLog($"Convert to service: {message}"));
                    return "Runner converted to a Windows service";
                case "Create":
                    // Used when moving a runner here: same name and labels, replacing the registration on the old PC.
                    var url = RunnerService.NormalizeRegistrationUrl(command.TargetKey);
                    await RunnerService.CreateRunnerAsync(new RunnerSetup
                    {
                        RepositoryUrl = url,
                        RunnerName = command.RunnerName,
                        InstallPath = RunnerService.SuggestInstallPath(url),
                        Labels = command.RunnerLabels,
                        ReplaceExisting = command.Value == "replace"
                    }, message => AppendAgentLog($"Create runner: {message}"));
                    return $"Runner created: {command.RunnerName}";
                case "Retire":
                    try
                    {
                        await RunnerService.RetireMovedRunnerAsync(command.TargetKey, command.Value == "delete", message => AppendAgentLog($"Retire runner: {message}"));
                    }
                    catch (Exception ex)
                    {
                        AppendAgentLog($"Retire runner: {ex.Message} {FleetActions.RetireByHand(Environment.MachineName, "the PC it moved to")}");
                        throw;
                    }
                    FleetConfig.SetManuallyStopped(command.TargetKey, false);
                    return "Runner removed from the old PC";
                case "Start" or "Stop" or "Restart":
                    // A move disables the old copy's service while it runs, and enables it again if the move failed.
                    if (command.Value == "enable") await FleetActions.SetServiceEnabledAsync(command.TargetKey, true);
                    await RunnerService.ControlServiceAsync(command.TargetKey, command.Action);
                    FleetConfig.SetManuallyStopped(command.TargetKey, command.Action == "Stop");
                    if (command.Action == "Stop") WantRunning.Remove(command.TargetKey);
                    if (command.Value == "disable") await FleetActions.SetServiceEnabledAsync(command.TargetKey, false);
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
                await RunnerService.ControlServiceAsync(runner.Name, "Start");
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

    private sealed record InstalledCheck(bool Installed, DateTime CheckedUtc);
    private static InstalledCheck? _agentInstalled;
    private static int _agentInstalledVersion;
    private static readonly TimeSpan AgentInstalledCacheTime = TimeSpan.FromSeconds(60);

    /// <summary>Whether the agent task exists. Cached for a minute (it runs schtasks.exe); changes here clear it.</summary>
    public static async Task<bool> IsAgentInstalledAsync()
    {
        if (_agentInstalled is { } cached && DateTime.UtcNow - cached.CheckedUtc < AgentInstalledCacheTime) return cached.Installed;
        var version = Volatile.Read(ref _agentInstalledVersion);
        var installed = await ScheduledTasks.ExistsAsync(ScheduledTasks.AgentTaskName);
        if (version == Volatile.Read(ref _agentInstalledVersion)) _agentInstalled = new InstalledCheck(installed, DateTime.UtcNow);
        return installed;
    }

    private static void ForgetAgentInstalled()
    {
        Interlocked.Increment(ref _agentInstalledVersion);
        _agentInstalled = null;
    }

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
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
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
        try
        {
            foreach (var task in new[] { ScheduledTasks.AgentTaskName, ScheduledTasks.LegacyAgentTaskName })
            {
                await ScheduledTasks.EndTaskAsync(task);
                await ScheduledTasks.DeleteTaskAsync(task);
            }
        }
        finally { ForgetAgentInstalled(); }
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
            c.ProtectedPendingFleetKey = string.Empty;
            c.KnownPeers.Clear();
        });
        lock (LastStatus) LastStatus.Clear();
        return told;
    }

    /// <summary>The first protocol whose agents can receive a new pairing code from Change fleet code.</summary>
    public const int RekeyProtocolVersion = 12;

    /// <summary>What Change fleet code did, by PC name.</summary>
    /// <param name="Switched">PCs that use the new code now.</param>
    /// <param name="NotConfirmed">PCs that got the new code but didn't answer with it yet; they switch the next time a PC contacts them.</param>
    /// <param name="Failed">PCs that didn't get the new code, with the reason. They must pair again.</param>
    /// <param name="LeftOut">PCs the user left out. They were forgotten and must pair again.</param>
    public sealed record FleetCodeChange(List<string> Switched, List<string> NotConfirmed, List<string> Failed, List<string> LeftOut);

    /// <summary>
    /// Change fleet code: makes a new pairing code and gives it to the PCs in <paramref name="keep"/>, encrypted with
    /// the current code and a fresh key exchange. This PC switches only after at least one of them confirmed it (or
    /// when there are none to give it to). Each of them switches at the first request that uses the new code; this
    /// sends one to each right away. PCs that are left out, offline or too old keep the old code, so they can no
    /// longer reach the fleet and must pair again. The ones left out are forgotten here and on the PCs that switched.
    /// </summary>
    public static async Task<FleetCodeChange> ChangeFleetCodeAsync(IReadOnlyList<MachineInfo> keep, IReadOnlyList<MachineInfo> leaveOut, Action<string> log)
    {
        var oldKey = FleetConfig.Current.FleetKey ?? throw new InvalidOperationException("This PC has no pairing code.");
        var newKey = CommandSigner.NewKey();
        var prepared = new List<MachineInfo>();
        var failed = new List<string>();
        if (keep.Count > 0) log($"Sending the new code to {string.Join(", ", keep.Select(m => m.MachineName))}…");
        await Task.WhenAll(keep.Select(async machine =>
        {
            try
            {
                await SendNewKeyAsync(machine, oldKey, newKey);
                lock (prepared) prepared.Add(machine);
            }
            catch (Exception ex)
            {
                var reason = ex.Message.StartsWith("Unknown request", StringComparison.Ordinal) ? "Update Runner Manager on it first." : ex.Message;
                lock (failed) failed.Add($"{machine.MachineName}: {reason}");
            }
        }));
        if (keep.Count > 0 && prepared.Count == 0)
            throw new InvalidOperationException($"No PC received the new code, so nothing was changed. {string.Join(" ", failed)}");

        FleetConfig.SetFleetKey(newKey);
        log($"This PC uses the new code now (fingerprint {CommandSigner.KeyId(newKey)}).");
        var switched = new List<string>();
        var notConfirmed = new List<string>();
        await Task.WhenAll(prepared.Select(async machine =>
        {
            try
            {
                var (address, machineId) = Route(machine.MachineKey);
                await FleetNetwork.CallAsync(address, Sanitize(machine.MachineKey), new FleetRequest { Op = "status", TargetId = machineId }, pairingKey: newKey);
                lock (switched) switched.Add(machine.MachineName);
            }
            catch { lock (notConfirmed) notConfirmed.Add(machine.MachineName); }
        }));

        // The PCs left out can't reach the fleet any more: forget them here and on the PCs that switched.
        var leftOut = leaveOut.Select(m => m.MachineName).ToList();
        foreach (var name in leftOut) ForgetPeer(name);
        await Task.WhenAll(prepared.Where(m => switched.Contains(m.MachineName)).SelectMany(machine => leftOut.Select(async name =>
        {
            try
            {
                var (address, machineId) = Route(machine.MachineKey);
                await FleetNetwork.CallAsync(address, Sanitize(machine.MachineKey),
                    new FleetRequest { Op = "leave", From = name, TargetId = machineId }, TimeSpan.FromSeconds(4), pairingKey: newKey);
            }
            catch { }
        })));
        lock (LastStatus) LastStatus.Clear();
        return new FleetCodeChange(switched, notConfirmed, failed, leftOut);
    }

    /// <summary>Gives one PC the new code (see <see cref="ChangeFleetCodeAsync"/>), and checks that it got the same one.</summary>
    private static async Task SendNewKeyAsync(MachineInfo machine, byte[] oldKey, byte[] newKey)
    {
        var (address, machineId) = Route(machine.MachineKey);
        var key = Sanitize(machine.MachineKey);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var offer = await FleetNetwork.CallAsync(address, key,
            new FleetRequest { Op = "rekey", RekeyPublicKey = mine, From = Environment.MachineName, TargetId = machineId }, pairingKey: oldKey);
        var shared = FleetCrypto.EcdhSecret(ecdh, Convert.FromBase64String(offer.RekeyPublicKey));
        var sealedKey = FleetCrypto.Seal(FleetCrypto.RekeyKey(shared), newKey, FleetCrypto.RekeyContext);
        var done = await FleetNetwork.CallAsync(address, key, new FleetRequest
        {
            Op = "rekey", RekeyPublicKey = mine, NewKey = Convert.ToBase64String(sealedKey), From = Environment.MachineName, TargetId = machineId
        }, pairingKey: oldKey);
        if (done.KeyId != CommandSigner.KeyId(newKey)) throw new InvalidOperationException("It did not confirm the new code.");
    }

    public static async Task InstallAndStartAgentTaskAsync()
    {
        // Prefer the installed release copy so the agent doesn't depend on a build folder that gets rebuilt.
        var exe = AppUpdater.PreferredExe;
        if (string.IsNullOrWhiteSpace(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run RunnerManager.exe from a build output before installing the fleet agent.");
        await ReplaceAndStartAgentTaskAsync(exe);
    }

    /// <summary>
    /// Replaces a running (possibly older) agent: waits for its work to finish, stops it, re-registers the task for
    /// exe, and starts it.
    /// </summary>
    private static async Task ReplaceAndStartAgentTaskAsync(string exe, Action<string>? log = null)
    {
        // Never stop another Windows user's agent.
        await AgentOwnership.EnsureOursAsync();
        ForgetAgentInstalled();
        await WaitForLocalAgentIdleAsync(log);
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await RegisterAgentTaskAsync(exe);
        await FleetNetwork.EnsureFirewallRulesAsync();
        await Task.Delay(1500);
        var (code, output) = await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
        if (code != 0) throw new InvalidOperationException(output.Trim());
    }

    /// <summary>
    /// Restarts the local agent (after an update or a settings change that needs it). Unless <paramref name="force"/>
    /// is set, it first waits (up to 15 minutes) for the agent to finish commands and AI assistant calls.
    /// </summary>
    public static async Task RestartLocalAgentAsync(bool force = false, Action<string>? log = null)
    {
        if (!force) await WaitForLocalAgentIdleAsync(log);
        await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
        await Task.Run(EndOtherAgents);
        await Task.Delay(1500);
        await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
    }

    /// <summary>
    /// Waits while this PC's agent reports that it is busy (protocol 12), at most <see cref="UpdateRestartWait"/>.
    /// Returns right away when the agent doesn't answer or is older, since there is then nothing to wait for.
    /// </summary>
    private static async Task WaitForLocalAgentIdleAsync(Action<string>? log)
    {
        if (FleetConfig.Current.FleetKey == null) return;
        var deadline = DateTime.UtcNow + UpdateRestartWait;
        var told = false;
        while (true)
        {
            MachineStatusPayload? status;
            try { status = (await FleetNetwork.CallAsync("127.0.0.1", LocalMachineKey, new FleetRequest { Op = "status" })).Status; }
            catch { return; }
            if (status is not { Busy: true }) return;
            if (DateTime.UtcNow >= deadline)
            {
                var message = $"The fleet agent is still busy after {(int)UpdateRestartWait.TotalMinutes} minutes. Restarting it anyway.";
                log?.Invoke(message);
                AppendAgentLog(message);
                return;
            }
            if (!told) log?.Invoke("Waiting for the fleet agent to finish its current work…");
            told = true;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Ends agents the task did not start (for example one started by hand). Otherwise such an agent keeps running the
    /// old build after an update, and the new one exits because only one agent may run.
    /// </summary>
    private static void EndOtherAgents()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{AppPaths.ExeName}' OR Name = '{AppPaths.LegacyExeName}'");
            foreach (System.Management.ManagementObject process in searcher.Get())
            {
                var id = Convert.ToInt32(process["ProcessId"]);
                if (id == Environment.ProcessId || process["CommandLine"]?.ToString()?.Contains("--agent", StringComparison.OrdinalIgnoreCase) != true) continue;
                try
                {
                    using var agent = Process.GetProcessById(id);
                    agent.Kill();
                    agent.WaitForExit(5000);
                    AppendAgentLog($"Ended a fleet agent that was not started by its task (process {id}).");
                }
                catch { }
            }
        }
        catch { }
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

    internal static async Task RegisterAgentTaskAsync(string exe)
    {
        try
        {
            await ScheduledTasks.RegisterLogonTaskAsync(ScheduledTasks.AgentTaskName, exe, "--agent",
                "Runner Manager fleet agent. Reports runner and Remote Desktop Commander status and runs commands from other PCs.", restartOnFailure: true);
        }
        finally { ForgetAgentInstalled(); }
    }

    // ================= Local status =================

    /// <summary>Local status as a MachineInfo, for showing this PC when its agent is not installed, not running or outdated.</summary>
    private static async Task<MachineInfo> ReadLocalMachineAsync(bool agentOutdated, bool agentRunning)
    {
        var local = await GatherLocalStateAsync(agentRunning);
        var payload = await Task.Run(() => BuildPayload(local, LoadAgentState().LastCommandId, string.Empty));
        return MachineInfo.FromPayload(LocalMachineKey, payload, localOnly: true, localAgentOutdated: agentOutdated);
    }

    private sealed record LocalState(List<RunnerInfo> Runners, RemoteInfo Rdc, RemoteRelayProbe Relay, bool AgentRunning, HealthSnapshot Health);

    /// <param name="agentRunning">Whether the agent runs, when the caller already checked.</param>
    private static async Task<LocalState> GatherLocalStateAsync(bool? agentRunning = null)
    {
        // The snapshots start with synchronous WMI and file work, so keep them off the calling (UI) thread.
        var runnersTask = Task.Run(() => RunnerService.ReadSnapshotAsync(queryGitHub: false));
        var relayTask = Task.Run(RemoteDesktopManager.ProbeRelayAsync);
        var agentTask = agentRunning is { } known ? Task.FromResult(known) : IsAgentRunningAsync();
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
            MachineId = FleetConfig.LocalMachineId,
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
                CustomLabels = r.CustomLabels,
                IsRunning = r.IsRunning,
                ActivityText = r.ActivityText,
                ServiceInstalled = r.ServiceInstalled,
                RunnerVersion = r.RunnerVersion,
                WorkFolderBytes = r.WorkFolderBytes,
                ManuallyStopped = config.ManuallyStopped.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
            }).ToArray()
        };
    }

    // ================= Helpers and agent state =================

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
            // Kept across restarts, so the result of an update command can still be read after the agent restarted.
            if (json.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var result in results.Deserialize<FleetCommandResult[]>() ?? Array.Empty<FleetCommandResult>())
                    if (result is { Id.Length: > 0 }) state.MarkProcessed(result.Id, result.Result, result.CompletedUtc);
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
            processed = state.Recent,
            results = state.Results
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

    /// <summary>The commands an agent processed (so one sent again is not run twice) and their latest results.</summary>
    private sealed class AgentState
    {
        private const int MaxProcessed = 50;
        private const int MaxResults = 20;
        private readonly object _gate = new();
        private readonly LinkedList<string> _recent = new();
        private readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FleetCommandResult> _results = new();
        private string _lastCommandId = string.Empty;

        // Read by status requests on other threads while the command loop updates it.
        public string LastCommandId { get { lock (_gate) return _lastCommandId; } }
        public string[] Recent { get { lock (_gate) return _recent.ToArray(); } }
        public FleetCommandResult[] Results { get { lock (_gate) return _results.ToArray(); } }

        public bool HasProcessed(string id)
        {
            lock (_gate) return _set.Contains(id);
        }

        public void MarkProcessed(string id, string? result = null, DateTime? completedUtc = null)
        {
            lock (_gate)
            {
                _lastCommandId = id;
                if (result != null)
                {
                    _results.RemoveAll(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    _results.Add(new FleetCommandResult { Id = id, Result = result, CompletedUtc = completedUtc ?? DateTime.UtcNow });
                    while (_results.Count > MaxResults) _results.RemoveAt(0);
                }
                if (!_set.Add(id)) return;
                _recent.AddLast(id);
                while (_recent.Count > MaxProcessed)
                {
                    _set.Remove(_recent.First!.Value);
                    _recent.RemoveFirst();
                }
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
            var runner = await RunnerService.FindRunnerAsync(service);
            title = $"{runner?.AgentName ?? service} diagnostic log — {Environment.MachineName}";
            path = runner == null ? null : NewestLog(Path.Combine(runner.InstallPath, "_diag"));
            if (path != null) title += $"  ({Path.GetFileName(path)})";
        }
        else if (logKey.StartsWith("job:", StringComparison.OrdinalIgnoreCase))
        {
            var key = logKey[4..];
            var root = await RunnerActivity.FindRootAsync(key);
            var lines = RunnerActivity.ReadOutput(root, MaxLines);
            return new LogPayload
            {
                Title = $"{Path.GetFileName(root)} live output — {Environment.MachineName}",
                Text = lines.Length > 0 ? Truncate(lines) : "(No output yet. The runner writes the output of the running step here while a job runs.)",
                CreatedUtc = DateTime.UtcNow
            };
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
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
}

public sealed record MachineStatusPayload
{
    public string MachineName { get; init; } = string.Empty;
    /// <summary>The PC's own ID (protocol 12; empty from older agents).</summary>
    public string MachineId { get; init; } = string.Empty;
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
    /// <summary>Results of the last commands, oldest first (protocol 12; empty from older agents).</summary>
    public FleetCommandResult[] RecentCommands { get; init; } = Array.Empty<FleetCommandResult>();
    /// <summary>Commands that are queued or running (protocol 12).</summary>
    public string[] PendingCommandIds { get; init; } = Array.Empty<string>();
    /// <summary>The agent is running a command or other work, which a restart would cut off (protocol 12).</summary>
    public bool Busy { get; init; }
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public double MemoryTotalGb { get; init; }
    public DateTime? BootUtc { get; init; }
    public bool RebootPending { get; init; }
    public DiskStatus[] Disks { get; init; } = Array.Empty<DiskStatus>();
    public bool WatchdogEnabled { get; init; }
    /// <summary>Short fingerprint of this PC's pairing code, so dashboards can spot a PC with a different code.</summary>
    public string SigningKeyId { get; init; } = string.Empty;
    public AgentEvent[] RecentEvents { get; init; } = Array.Empty<AgentEvent>();
    public MachineRunnerStatus[] Runners { get; init; } = Array.Empty<MachineRunnerStatus>();

    /// <summary>
    /// Everything except values that tick on their own (our heartbeat, the relay's last_seen, CPU and memory load),
    /// so unchanged status can skip a UI rebuild.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(this with
    {
        UpdatedUtc = default,
        Busy = false,
        RdcConnectionChangedUtc = null,
        CpuPercent = 0,
        MemoryPercent = 0
    });
}

/// <summary>What a finished command reported.</summary>
public sealed record FleetCommandResult
{
    public string Id { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public DateTime CompletedUtc { get; init; }
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
}
