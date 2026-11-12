using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace RunnerManager;

public static class MachineRelay
{
    /// <summary>
    /// 11: answers "job" requests (live output and processes of a runner's job).
    /// 12: machine IDs, recent command results, busy state, and "rekey" (Change fleet code).
    /// 15: "managed" and "release" (PCs managed one way).
    /// </summary>
    public const int ProtocolVersion = 15;
    /// <summary>The first protocol whose agents share the PCs they manage one way.</summary>
    public const int ManagedProtocolVersion = 15;
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
    // When each PC last answered, by this PC's clock (guarded by LastStatus's lock).
    private static readonly Dictionary<string, DateTime> LastAnswerUtc = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// How long a PC that stops answering still counts as online: one slow answer (a busy PC, or a connection over the
    /// internet) shouldn't make it flicker offline. Shorter than the 30 seconds after which its last status is too old.
    /// </summary>
    internal static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(25);
    private static DateTime _lastDiscovery = DateTime.MinValue;

    // Agent-process state
    private static readonly List<AgentEvent> RecentEvents = new();
    private static readonly RunnerRecovery Recovery = new();
    private static StatusCache? _status;
    /// <summary>The agent is about to exit for an update: a command sent now would never run, so it is refused.</summary>
    private static volatile bool _exitingForUpdate;
    /// <summary>The exe an update installed, which the agent's task is registered for when the agent exits to run it.</summary>
    private static volatile string? _agentExeAfterUpdate;
    /// <summary>
    /// How long an update waits for the agent's work to finish before it restarts the agent anyway: as long as the
    /// longest work, an AI assistant's call that waits for its approval and then creates a runner on another PC
    /// (<see cref="AssistantHost.DrainLimit"/>, longer than a create alone), counted again each time the agent finishes
    /// a command. So work that runs when the update asks is not cut off; only an agent that finishes nothing for that
    /// long is, or one that is still busy after <see cref="UpdateRestartLimit"/>.
    /// </summary>
    /// <remarks>
    /// Work also moves on when an AI assistant's call or approval starts or ends, and when another PC finishes a command
    /// queued before a create this agent waits for (<see cref="AgentWork.Moves"/>; a dashboard reads it as
    /// <see cref="MachineStatusPayload.WorkMoves"/>). In the agent, an AI assistant's change that comes after the update
    /// asked is refused (<see cref="AgentWork.RestartingText"/>), so no new work begins that the restart would cut off.
    /// </remarks>
    internal static readonly TimeSpan UpdateRestartWait = AssistantHost.DrainLimit;

    /// <summary>
    /// The longest an update waits for the agent's work in all, from when it asked for the restart. Work that keeps
    /// coming (commands that each finish in time, new AI assistant calls) cannot put the update off for longer, so the
    /// PC does not stay half updated, with the new version on disk and the old one running.
    /// </summary>
    internal static readonly TimeSpan UpdateRestartLimit = TimeSpan.FromHours(3);

    /// <summary>Why a command that comes in the last seconds before the agent exits for an update is refused.</summary>
    internal static string RestartingNowText
        => $"The fleet agent on {Environment.MachineName} is restarting to finish an update and did not take the request. Try again in a minute.";

    /// <summary>
    /// The longest a caller waits for a command its agent still reports as queued or running: as long as the longest
    /// command, a runner create (<see cref="FleetActions.CreateWait"/>), counted again each time the agent finishes a
    /// command that was queued before it.
    /// </summary>
    internal static readonly TimeSpan MaxCommandWait = FleetActions.CreateWait;

    public static string LocalMachineKey => Sanitize(Environment.MachineName);

    // ================= Dashboard side =================

    /// <summary>
    /// This PC plus every paired PC: found by broadcast on the local network, remembered addresses, or addresses
    /// added in Settings (e.g. Tailscale names). All traffic goes directly between the PCs, encrypted with the fleet code.
    /// </summary>
    /// <remarks>
    /// Runs on the thread pool, not on the caller's thread: decrypting and reading the answers and saving fleet.json
    /// (which can wait for the agent's write) would otherwise run on the dashboard's UI thread and make it stutter.
    /// </remarks>
    public static Task<List<MachineInfo>> ReadFleetAsync(bool forceDiscovery = false) => Task.Run(() => ReadFleetOnPoolAsync(forceDiscovery));

    private static async Task<List<MachineInfo>> ReadFleetOnPoolAsync(bool forceDiscovery)
    {
        var machines = new List<MachineInfo> { await ReadThisPcAsync() };
        var config = FleetConfig.Current;
        if (config.FleetKey == null) return machines;

        // Discovery answers are signed but not tied to the address they come from: anyone on the network can forward
        // one. So an answer from a new address is only a candidate, used when the PC can't be reached where it was
        // last; the address changes once an encrypted request there succeeds. This also keeps a PC with several
        // network adapters at one address.
        var candidates = new Dictionary<string, List<DiscoveredPeer>>(StringComparer.OrdinalIgnoreCase);
        var managedCandidates = new Dictionary<string, List<DiscoveredPeer>>(StringComparer.Ordinal);
        var discovered = forceDiscovery || DateTime.UtcNow - _lastDiscovery > DiscoveryInterval;
        if (discovered)
        {
            _lastDiscovery = DateTime.UtcNow;
            var hints = config.KnownPeers.Select(p => FleetNetwork.HostOf(p.Address)).Concat(config.ManualAddresses);
            var localId = FleetConfig.LocalMachineId;
            // Managed PCs answer only probes made with their own code.
            var managedSearch = DiscoverManagedAsync(config.ManagedPcs);
            var fleetPeers = await FleetNetwork.DiscoverAsync(hints, TimeSpan.FromSeconds(1.2));
            managedCandidates = await managedSearch;
            foreach (var peer in fleetPeers)
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
            .Concat(unnamed.Select(a => FetchPeerAsync(null, null, a, null)))
            .Concat(config.ManagedPcs.Select(pc => FetchManagedAsync(pc, managedCandidates.GetValueOrDefault(pc.KeyId))));
        var results = await Task.WhenAll(fetches);
        machines.AddRange(results.OfType<MachineInfo>().Where(m => !m.IsLocal)
            .GroupBy(m => m.MachineKey).Select(g => g.First())
            .OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase));
        if (discovered) await SyncManagedPcsAsync(machines);
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

    /// <summary>Whether the agent task runs this exact build (used before a fleet code exists).</summary>
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
        var refused = false;
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
                refused = true;
                try
                {
                    var response = await FleetNetwork.CallAsync(attempt, "*", new FleetRequest { Op = "status" });
                    if (response.Status is { } status && status.MachineId.Equals(known.MachineId, StringComparison.OrdinalIgnoreCase))
                        return Accept(status, attempt);
                }
                catch { }
            }
            catch (PairingMismatchException ex)
            {
                problem = ex.Message;
                refused = true;
            }
            catch (Exception) { if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(attempt)}"; }
        }
        if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(address)}";

        if (name == null)
            return MachineInfo.Placeholder("ADDR_" + Sanitize(address), address, DateTime.MinValue, address, problem);
        if (!listIfUnreachable) return null;
        return Unanswered(key, address, problem, refused, managed: false)
               ?? MachineInfo.Placeholder(key, name, known?.LastSeenUtc ?? DateTime.MinValue, address, problem);
    }

    /// <summary>
    /// A PC that didn't answer this time, from its last status: still online when it answered within
    /// <see cref="OfflineGrace"/> and only couldn't be reached (a refusal shows right away). Null when it never answered.
    /// </summary>
    private static MachineInfo? Unanswered(string key, string address, string problem, bool refused, bool managed)
    {
        MachineStatusPayload? last;
        DateTime answered;
        lock (LastStatus)
        {
            LastStatus.TryGetValue(key, out last);
            LastAnswerUtc.TryGetValue(key, out answered);
        }
        if (last == null) return null;
        var briefly = !refused && DateTime.UtcNow - answered < OfflineGrace;
        return MachineInfo.FromPayload(key, last, address: address, problem: briefly ? string.Empty : problem, managed: managed);
    }

    private static void NoteAnswer(string key, MachineStatusPayload payload)
    {
        lock (LastStatus)
        {
            LastStatus[key] = payload;
            LastAnswerUtc[key] = DateTime.UtcNow;
        }
    }

    // How far each PC's clock is from this PC's, as used for its answers (guarded by LastStatus's lock).
    private static readonly Dictionary<string, TimeSpan> ClockOffsets = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Clocks this close need no correction (the measurement includes the network's delay). A PC's correction changes
    /// only when its clock moves by more than this, so the times of its jobs, which name their alerts, stay the same.
    /// </summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A PC's status by this PC's clock. The other PC stamps its times with its own clock, which may be off by up to the
    /// two minutes the connection allows, and a dashboard compares them with its own (a PC is online while its status
    /// is under 30 seconds old). The status counts as updated when the answer arrived, less the age the PC reports for
    /// it; its start time and the times of its jobs move by how far its clock is off. Event times stay as the PC
    /// reported them, since they name its alerts.
    /// </summary>
    internal static MachineStatusPayload ByLocalClock(string key, MachineStatusPayload payload, DateTime receivedUtc)
    {
        // Older agents send no AnsweredUtc: they stamp UpdatedUtc when they answer.
        var answered = payload.AnsweredUtc != default ? payload.AnsweredUtc : payload.UpdatedUtc;
        var measured = receivedUtc - answered;
        // The connection refuses clocks further apart, so this is no answer time: keep the status as it is.
        if (answered == default || measured.Duration() > FleetCrypto.MaxClockSkew + ClockTolerance) return payload;
        var age = answered > payload.UpdatedUtc ? answered - payload.UpdatedUtc : TimeSpan.Zero;
        var offset = ClockOffset(key, measured);
        return payload with
        {
            UpdatedUtc = receivedUtc - age,
            AnsweredUtc = receivedUtc,
            BootUtc = payload.BootUtc + offset,
            Runners = offset == TimeSpan.Zero ? payload.Runners : payload.Runners.Select(r => Shifted(r, offset)).ToArray()
        };
    }

    private static TimeSpan ClockOffset(string key, TimeSpan measured)
    {
        lock (LastStatus)
        {
            var known = ClockOffsets.GetValueOrDefault(key);
            if ((measured - known).Duration() <= ClockTolerance) return known;
            var offset = TimeSpan.FromSeconds(Math.Round(measured.TotalSeconds));
            ClockOffsets[key] = offset;
            return offset;
        }
    }

    /// <summary>A runner's job times moved by <paramref name="offset"/>. Its properties are init-only, so it is copied the way it arrived, as JSON.</summary>
    private static MachineRunnerStatus Shifted(MachineRunnerStatus runner, TimeSpan offset)
    {
        if (runner.JobStartedUtc == null && runner.QuietSinceUtc == null) return runner;
        var copy = JsonSerializer.SerializeToNode(runner)!.AsObject();
        if (runner.JobStartedUtc is { } started) copy[nameof(MachineRunnerStatus.JobStartedUtc)] = started + offset;
        if (runner.QuietSinceUtc is { } quiet) copy[nameof(MachineRunnerStatus.QuietSinceUtc)] = quiet + offset;
        return copy.Deserialize<MachineRunnerStatus>() ?? runner;
    }

    /// <summary>A PC answered at <paramref name="address"/>: remember it there, and list it (with a warning if its name is taken twice).</summary>
    private static MachineInfo Accept(MachineStatusPayload? payload, string address)
    {
        if (payload == null) throw new InvalidOperationException("The PC returned no status.");
        payload = PeerStatus.Checked(payload, FleetNetwork.WithPort(address), AppendAgentLog);
        var machineKey = Sanitize(payload.MachineName);
        payload = ByLocalClock(machineKey, payload, DateTime.UtcNow);
        NoteAnswer(machineKey, payload);
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
        // It answered with the fleet's code, so it is a fleet member now: an earlier one-way pairing of it is out of date.
        var wasManaged = machineId.Length > 0 && config.ManagedPcs.Any(p => p.MachineId.Equals(machineId, StringComparison.OrdinalIgnoreCase));
        // Only write when something meaningful changed, not on every poll.
        var duplicates = config.KnownPeers.Count(p => Sanitize(p.Name) == Sanitize(name)) > 1;
        if (!duplicates && renamed.Count == 0 && !wasManaged && existing != null && existing.Name == name && existing.Address == address
            && existing.Version == version && existing.MachineId == machineId
            && DateTime.UtcNow - existing.LastSeenUtc < TimeSpan.FromMinutes(10)) return;
        FleetConfig.Update(c =>
        {
            c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name) || renamed.Any(r => Sanitize(r.Name) == Sanitize(p.Name)));
            c.KnownPeers.Add(new PeerRecord { Name = name, Address = address, Version = version, MachineId = machineId, LastSeenUtc = DateTime.UtcNow });
            if (wasManaged) c.ForgetManagedWhere(p => p.MachineId.Equals(machineId, StringComparison.OrdinalIgnoreCase));
        });
        if (wasManaged) AppendAgentLog($"{name} joined the fleet, so it is no longer managed one way.");
        foreach (var old in renamed)
        {
            lock (LastStatus) LastStatus.Remove(Sanitize(old.Name));
            AppendAgentLog($"{old.Name} is now called {name}.");
        }
    }

    /// <summary>
    /// Remembers a PC learned during pairing so it's contacted right away. With its machine ID, a one-way pairing of the
    /// same PC ends here; a managed PC with only the same name is kept.
    /// </summary>
    /// <param name="machineId">The PC's machine ID, as its status reports it; null when the pairing did not say.</param>
    public static void AddKnownPeer(string name, string address, string? machineId = null)
        => RememberPeer(name, FleetNetwork.WithPort(address), string.Empty, machineId ?? string.Empty);

    public static void ForgetPeer(string name)
    {
        FleetConfig.Update(c => c.KnownPeers.RemoveAll(p => Sanitize(p.Name) == Sanitize(name)));
        lock (LastStatus) LastStatus.Remove(Sanitize(name));
    }

    // ----- PCs managed one way -----

    /// <summary>Why a managed PC can't be used under its name: a PC in the fleet has the same one.</summary>
    internal static string ManagedNameProblem(string name)
        => $"{name} is managed one way, and a PC in the fleet has the same name. Rename one of them in Windows Settings → System → About. " +
           $"Until then, commands to {name} are refused.";

    /// <summary>Why a managed PC that was renamed isn't listed under its new name: another managed PC has that name.</summary>
    internal static string ManagedNameTakenProblem(string name)
        => $"{name} is managed one way, and another PC managed one way has the same name. Rename one of them in Windows Settings → System → About. " +
           "Until then, its runners are not shown.";

    /// <summary>Finds managed PCs that moved to another address on the local network, by key fingerprint.</summary>
    private static async Task<Dictionary<string, List<DiscoveredPeer>>> DiscoverManagedAsync(IReadOnlyList<ManagedPcRecord> pcs)
    {
        var found = new Dictionary<string, List<DiscoveredPeer>>(StringComparer.Ordinal);
        await Task.WhenAll(pcs.Select(async pc =>
        {
            if (pc.Key is not { } key) return;
            var peers = await FleetNetwork.DiscoverAsync(new[] { FleetNetwork.HostOf(pc.Address) }, TimeSpan.FromSeconds(1.2), pairingKey: key);
            var matching = peers.Where(p => Sanitize(p.Name) == Sanitize(pc.Name) && !SameEndpoint(p.Address, pc.Address)
                                            && (pc.MachineId.Length == 0 || p.MachineId.Length == 0 || p.MachineId.Equals(pc.MachineId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matching.Count > 0) lock (found) found[pc.KeyId] = matching;
        }));
        return found;
    }

    /// <summary>Asks a managed PC for its status with its own code, at its last address and then where discovery found it.</summary>
    private static async Task<MachineInfo?> FetchManagedAsync(ManagedPcRecord pc, IReadOnlyList<DiscoveredPeer>? candidates)
    {
        var key = Sanitize(pc.Name);
        // A managed PC can't take a fleet PC's place, not even by answering under its name.
        if (key == LocalMachineKey || FleetConfig.Current.KnownPeers.Any(p => Sanitize(p.Name) == key))
            return MachineInfo.Placeholder("MANAGED_" + key, pc.Name, pc.LastSeenUtc, pc.Address, ManagedNameProblem(pc.Name), managed: true);
        if (pc.Key is not { } pairingKey)
            return MachineInfo.Placeholder(key, pc.Name, pc.LastSeenUtc, pc.Address, FleetNetwork.AccessCodeUnreadableText(pc.Name), managed: true);

        var addresses = new List<string> { pc.Address };
        if (candidates != null) addresses.AddRange(candidates.Select(c => c.Address).Where(a => !addresses.Any(b => SameEndpoint(a, b))));
        var problem = string.Empty;
        var refused = false;
        foreach (var attempt in addresses.Where(a => a.Length > 0))
        {
            try
            {
                var response = await FleetNetwork.CallAsync(attempt, key, new FleetRequest { Op = "status", TargetId = pc.MachineId }, pairingKey: pairingKey);
                return AcceptManaged(pc, response.Status, attempt);
            }
            catch (PairingMismatchException ex) when (ex.Reason == "wrong PC" && pc.MachineId.Length > 0)
            {
                // Maybe it was renamed: ask the PC at this address who it is.
                problem = ex.Message;
                refused = true;
                try
                {
                    var response = await FleetNetwork.CallAsync(attempt, "*", new FleetRequest { Op = "status" }, pairingKey: pairingKey);
                    if (response.Status is { } status && status.MachineId.Equals(pc.MachineId, StringComparison.OrdinalIgnoreCase))
                        return AcceptManaged(pc, status, attempt);
                }
                catch { }
            }
            catch (PairingMismatchException ex)
            {
                refused = true;
                problem = ex.Reason is "" or "denied" ? FleetNetwork.AccessCodeRefusedText(pc.Name) : ex.Message;
            }
            catch (Exception) { if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(attempt)}"; }
        }
        if (problem.Length == 0) problem = $"Not reachable at {FleetNetwork.WithPort(pc.Address)}";
        return Unanswered(key, pc.Address, problem, refused, managed: true)
               ?? MachineInfo.Placeholder(key, pc.Name, pc.LastSeenUtc, pc.Address, problem, managed: true);
    }

    /// <summary>A managed PC answered: remember where (and its new name after a rename), and list it.</summary>
    private static MachineInfo AcceptManaged(ManagedPcRecord pc, MachineStatusPayload? payload, string address)
    {
        if (payload == null) throw new InvalidOperationException("The PC returned no status.");
        payload = PeerStatus.Checked(payload, "managed " + pc.KeyId, AppendAgentLog);
        var machineKey = Sanitize(payload.MachineName);
        if (machineKey != Sanitize(pc.Name) && (machineKey == LocalMachineKey || FleetConfig.Current.KnownPeers.Any(p => Sanitize(p.Name) == machineKey)))
            return MachineInfo.Placeholder("MANAGED_" + machineKey, payload.MachineName, DateTime.UtcNow, address, ManagedNameProblem(payload.MachineName), managed: true);
        // Nor another managed PC's place: its status and record would be mixed up with this one's. The same PC paired twice is let through.
        if (machineKey != Sanitize(pc.Name) && FleetConfig.Current.ManagedPcs.Any(p => p.KeyId != pc.KeyId && Sanitize(p.Name) == machineKey
                && !(p.MachineId.Length > 0 && p.MachineId.Equals(payload.MachineId, StringComparison.OrdinalIgnoreCase))))
            return MachineInfo.Placeholder("MANAGED_" + machineKey, payload.MachineName, DateTime.UtcNow, address, ManagedNameTakenProblem(payload.MachineName), managed: true);
        payload = ByLocalClock(machineKey, payload, DateTime.UtcNow);
        NoteAnswer(machineKey, payload);
        NoteSighting(payload.MachineName, payload.MachineId, address);
        var at = FleetNetwork.WithPort(address);
        if (pc.Name != payload.MachineName || pc.Address != at || pc.Version != payload.AppVersion
            || (payload.MachineId.Length > 0 && pc.MachineId != payload.MachineId) || DateTime.UtcNow - pc.LastSeenUtc > TimeSpan.FromMinutes(10))
        {
            if (Sanitize(pc.Name) != machineKey) AppendAgentLog($"{pc.Name} is now called {payload.MachineName}.");
            FleetConfig.NoteManagedPc(pc.KeyId, payload.MachineName, at, payload.MachineId, payload.AppVersion);
        }
        return MachineInfo.FromPayload(machineKey, payload, address: address, problem: DuplicateNameProblem(machineKey) ?? string.Empty, managed: true);
    }

    /// <summary>
    /// Brings this PC's list of managed PCs up to date with the other fleet PCs', so any of them can manage a PC that one
    /// of them paired. Each takes in the others' lists, so a change spreads as the PCs check on each other.
    /// </summary>
    private static async Task SyncManagedPcsAsync(IEnumerable<MachineInfo> machines)
    {
        var config = FleetConfig.Current;
        if (config.ManagedBy.Length > 0) return;
        var peers = machines.Where(m => !m.IsLocal && !m.IsManaged && m.IsOnline && m.AgentProtocolVersion >= ManagedProtocolVersion)
            .Select(m => config.KnownPeers.FirstOrDefault(p => Sanitize(p.Name) == m.MachineKey)).OfType<PeerRecord>().ToList();
        var answers = await Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                return await FleetNetwork.CallAsync(peer.Address, Sanitize(peer.Name),
                    new FleetRequest { Op = "managed", TargetId = peer.MachineId }, TimeSpan.FromSeconds(4));
            }
            catch { return null; }
        }));
        var shared = answers.OfType<FleetResponse>().ToList();
        if (shared.Count == 0) return;
        try
        {
            FleetConfig.UpdateIf(c => shared.Aggregate(false, (changed, answer) =>
                c.MergeManaged(answer.ManagedPcs ?? new(), answer.RemovedManagedPcs ?? new()) | changed));
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Stops managing a PC: tells it, so it switches to a new code of its own, and removes it here (and, through the sync,
    /// on the other fleet PCs). Returns whether the PC was told; one that is off still has the old code, which the fleet
    /// no longer uses.
    /// </summary>
    public static async Task<bool> RemoveManagedPcAsync(string machineKey)
    {
        var pc = FleetConfig.Current.ManagedPc(machineKey) ?? throw new InvalidOperationException($"{machineKey} is not managed by this fleet.");
        var told = false;
        if (pc.Key is { } key)
        {
            try
            {
                await FleetNetwork.CallAsync(pc.Address, Sanitize(pc.Name),
                    new FleetRequest { Op = "release", From = Environment.MachineName, TargetId = pc.MachineId }, TimeSpan.FromSeconds(4), pairingKey: key);
                told = true;
            }
            catch { }
        }
        FleetConfig.RemoveManagedPc(pc.KeyId);
        lock (LastStatus) LastStatus.Remove(Sanitize(pc.Name));
        return told;
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

    /// <summary>
    /// Where to send requests for a PC, its machine ID (empty when unknown) so only that PC acts on them, and the code to
    /// use: a managed PC's own, or null for the fleet's.
    /// </summary>
    private static (string Address, string MachineId, byte[]? Key) Route(string machineKey)
    {
        // This PC's own agent: AI assistant tools run without administrator rights and hand their work to it.
        if (Sanitize(machineKey) == LocalMachineKey) return ($"127.0.0.1:{FleetNetwork.AgentPort}", string.Empty, null);
        if (DuplicateNameProblem(machineKey) is { } problem) throw new InvalidOperationException(problem);
        var config = FleetConfig.Current;
        var peer = config.KnownPeers.FirstOrDefault(p => Sanitize(p.Name) == Sanitize(machineKey));
        var managed = config.ManagedPc(machineKey);
        if (peer != null && managed != null) throw new InvalidOperationException(ManagedNameProblem(managed.Name));
        if (managed != null)
            return (managed.Address, managed.MachineId, managed.Key
                ?? throw new InvalidOperationException(FleetNetwork.AccessCodeUnreadableText(managed.Name)));
        if (peer == null) throw new InvalidOperationException($"Could not reach {machineKey}. Check that it is on and on the same network.");
        return (peer.Address, peer.MachineId, null);
    }

    /// <summary>Why commands to a machine would fail, or null when it can be reached.</summary>
    public static string? SigningProblem(MachineInfo machine) => machine.IsLocal ? null : machine.ConnectionProblem.Length > 0 ? machine.ConnectionProblem : null;

    /// <param name="commandId">
    /// The command's ID. Pass the same ID again to retry a send whose answer was lost: the agent runs each ID once.
    /// </param>
    public static async Task<string> SendCommandAsync(string machineKey, string target, string targetKey, string action,
        string runnerName = "", string[]? runnerLabels = null, string value = "", string? commandId = null,
        RunnerRegistrationOptions? options = null)
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
            RegistrationOptions = options,
            Value = value
        };
        var (address, machineId, key) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "command", Command = command, TargetId = machineId }, pairingKey: key);
        return string.IsNullOrEmpty(response.CommandId) ? command.Id : response.CommandId;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Labels, DateTime ReadUtc)> LabelCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LabelReads = new();
    private static readonly TimeSpan LabelCacheTime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A runner's custom labels for the agent's status, which is built every few seconds without asking GitHub. They are
    /// read from GitHub in the background, at most every 10 minutes per runner; until then the last known ones are sent.
    /// </summary>
    private static string[] CachedCustomLabels(RunnerInfo runner)
    {
        if (runner.AgentId <= 0 || string.IsNullOrWhiteSpace(runner.ApiScope)) return Array.Empty<string>();
        var key = $"{runner.ApiScope}/{runner.AgentId}";
        LabelCache.TryGetValue(key, out var cached);
        if (DateTime.UtcNow - cached.ReadUtc >= LabelCacheTime && LabelReads.TryAdd(key, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await GitHubCli.GetRunnerStateAsync(runner.ApiScope, runner.AgentId) is { } state)
                        LabelCache[key] = (state.Labels, DateTime.UtcNow);
                    else LabelCache[key] = (cached.Labels ?? Array.Empty<string>(), DateTime.UtcNow - LabelCacheTime + TimeSpan.FromMinutes(1));
                }
                catch { }
                finally { LabelReads.TryRemove(key, out _); }
            });
        }
        return cached.Labels ?? Array.Empty<string>();
    }

    /// <summary>A busy runner's live output and processes from the PC it runs on (agents with protocol 11 or later).</summary>
    public static async Task<RunnerJobActivity> RequestJobActivityAsync(string machineKey, string runnerKey, CancellationToken cancellationToken = default)
    {
        var (address, machineId, key) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "job", LogKey = runnerKey, TargetId = machineId }, TimeSpan.FromSeconds(10), cancellationToken, key);
        if (!response.Ok) throw new InvalidOperationException(response.Error);
        return response.Job ?? throw new InvalidOperationException("The PC returned no job details.");
    }

    /// <summary>Reads the tail of one of another PC's logs directly from its agent.</summary>
    public static async Task<LogPayload> RequestRemoteLogAsync(string machineKey, string logKey, CancellationToken cancellationToken)
    {
        var (address, machineId, key) = Route(machineKey);
        var response = await FleetNetwork.CallAsync(address, Sanitize(machineKey),
            new FleetRequest { Op = "log", LogKey = logKey, TargetId = machineId }, TimeSpan.FromSeconds(45), cancellationToken, key);
        return response.Log ?? throw new InvalidOperationException("The PC returned no log.");
    }

    /// <summary>
    /// Waits until a machine's agent reports that it processed a command, and returns its result. Throws when the agent
    /// reports a failure, or when there is no result after <paramref name="timeout"/>. Agents that report what is
    /// queued and running (protocol 12) are waited for longer while the command still waits or runs, up to
    /// <see cref="MaxCommandWait"/> after the agent last finished a command queued before it; the exception then says
    /// that it is still running.
    /// </summary>
    public static Task<string> WaitForCommandAsync(string machineKey, string commandId, TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitForCommandAsync(machineKey, commandId, timeout, () => DateTime.UtcNow, cancellationToken);

    /// <param name="utcNow">The clock (tests pass their own).</param>
    /// <param name="movedOn">Called when the agent finished a command queued before this one (<see cref="AgentWork.MovedOn"/> unless a test passes its own).</param>
    internal static async Task<string> WaitForCommandAsync(string machineKey, string commandId, TimeSpan timeout, Func<DateTime> utcNow,
        CancellationToken cancellationToken = default, Action? movedOn = null)
    {
        movedOn ??= AgentWork.MovedOn;
        var started = utcNow();
        var deadline = started + timeout;
        var longest = timeout > MaxCommandWait ? timeout : MaxCommandWait;
        var cap = started + longest;
        var runningSeenUtc = DateTime.MinValue;
        // The results the agent reported last time. A new one while this command still waits means the agent works
        // through the commands queued before it, so this one may only now get its turn.
        HashSet<string>? finished = null;
        bool StillRunning() => utcNow() - runningSeenUtc < TimeSpan.FromMinutes(1);
        while (utcNow() < cap && (utcNow() < deadline || StillRunning()))
        {
            await Task.Delay(CommandPollInterval, cancellationToken);
            MachineStatusPayload? status;
            try
            {
                var (address, machineId, key) = Route(machineKey);
                status = (await FleetNetwork.CallAsync(address, Sanitize(machineKey), new FleetRequest { Op = "status", TargetId = machineId }, pairingKey: key)).Status;
            }
            catch { continue; }
            if (status == null) continue;
            var result = status.RecentCommands.FirstOrDefault(r => r.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase))?.Result;
            if (result == null && status.PendingCommandIds.Contains(commandId, StringComparer.OrdinalIgnoreCase))
            {
                runningSeenUtc = utcNow();
                var now = status.RecentCommands.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (finished != null && !now.IsSubsetOf(finished))
                {
                    cap = runningSeenUtc + longest;
                    // In the fleet agent, an update restart then waits for this create longer, too.
                    movedOn();
                }
                finished = now;
                continue;
            }
            // Older agents only report the last command.
            if (result == null && status.LastCommandId.Equals(commandId, StringComparison.OrdinalIgnoreCase)) result = status.LastCommandResult;
            if (result == null) continue;
            if (IsFailedResult(result)) throw new InvalidOperationException(result);
            return result;
        }
        var minutes = Math.Max(1, (int)Math.Round((utcNow() - started).TotalMinutes));
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
        new(@"^(runner|agent|machine) \S+ failed: ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
            // Sent again after a lost answer, a command that is queued or done is still taken.
            if (_exitingForUpdate && !pending.ContainsKey(command.Id) && !state.HasProcessed(command.Id))
                throw new InvalidOperationException(RestartingNowText);
            if (pending.TryAdd(command.Id, 0)) queue.Writer.TryWrite(command);
            InvalidateStatus();
            return command.Id;
        }
        // Several dashboards may poll at once; the (WMI-heavy) status is built at most every two seconds, in the background.
        var status = _status = new StatusCache(async () => BuildPayload(await GatherLocalStateAsync(), state.LastCommandId, lastResult), AppendAgentLog);
        async Task<MachineStatusPayload> StatusAsync()
        {
            var cached = await status.GetAsync();
            // Pending before results: a command that finishes in between then shows in both, never in neither.
            var running = pending.Keys.ToArray();
            return AsAnswer(cached, DateTime.UtcNow) with
            {
                PendingCommandIds = running,
                RecentCommands = state.Results,
                Busy = running.Length > 0 || !AgentWork.IsIdle,
                WorkMoves = AgentWork.Moves
            };
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
        RemoveRemoteDesktopLeftovers();

        var lastWatchdogUtc = DateTime.MinValue;
        // A hung read of this PC's state (WMI) must not hold up the commands this loop runs.
        var watchdogRead = new GuardedRead<LocalState>(() => GatherLocalStateAsync(), StatusCache.DefaultBuildTimeout, AppendAgentLog,
            $"Auto-recovery skipped its check: reading this PC's runners took more than {(int)StatusCache.DefaultBuildTimeout.TotalSeconds} seconds.",
            "Auto-recovery can read this PC's runners again.");
        var restart = new UpdateRestartClock(() => DateTime.UtcNow, UpdateRestartWait, UpdateRestartLimit);
        var toldRestart = false;
        var moves = AgentWork.Moves;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // While an update waits for work to finish, look again every second.
                    wait.CancelAfter(AgentWork.RestartAsked ? TimeSpan.FromSeconds(1) : WatchdogInterval);
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
                    restart.WorkFinished();
                    InvalidateStatus();
                    try { SaveAgentState(state); }
                    catch (Exception ex) { AppendAgentLog($"Could not save the agent state: {ex.Message}"); }
                }

                // An AI assistant's call or approval that started or ended, or a create on another PC that moved on in
                // that PC's queue, is work that moves on, as a finished command is.
                if (AgentWork.Moves != moves)
                {
                    moves = AgentWork.Moves;
                    restart.WorkFinished();
                }

                if (AgentWork.RestartAsked)
                {
                    // Exit once queued commands and other work (AI assistant calls) are done. Commands run in this loop, so
                    // a queued one always runs first; other work is waited for until nothing moved on for a long time,
                    // or at most UpdateRestartLimit in all.
                    var quiet = pending.IsEmpty && AgentWork.IsIdle;
                    if (!toldRestart && !quiet) AppendAgentLog("Restarting to finish the update once the current work is done.");
                    toldRestart = true;
                    if (restart.Due(quiet))
                    {
                        AppendAgentLog(quiet ? "Restarting to finish the update."
                            : restart.PastLimit
                                ? $"Restarting to finish the update, although work is still running. The update has waited {(int)UpdateRestartLimit.TotalHours} hours for it."
                                : $"Restarting to finish the update, although work is still running. Nothing finished in {(int)UpdateRestartWait.TotalMinutes} minutes.");
                        _exitingForUpdate = true;
                        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None); // let the dashboard read the result
                        await SwitchTaskBeforeExitAsync(_agentExeAfterUpdate, () => AppUpdater.ScheduleAgentTaskRestart(ScheduledTasks.AgentTaskName),
                            RegisterAgentTaskAsync, AppendAgentLog);
                        Environment.Exit(0);
                    }
                }

                if (DateTime.UtcNow - lastWatchdogUtc >= WatchdogInterval)
                {
                    lastWatchdogUtc = DateTime.UtcNow;
                    if (await watchdogRead.ReadAsync() is { } local && await RunWatchdogAsync(local)) InvalidateStatus();
                }
            }
            catch (Exception ex)
            {
                AppendAgentLog(ex.Message);
                await Task.Delay(1000, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Versions before 2.6 also managed Remote Desktop Commander. Its launcher script and logs are removed; a copy that
    /// is still running keeps running until it stops or the PC restarts, and is no longer started again.
    /// </summary>
    private static void RemoveRemoteDesktopLeftovers()
    {
        foreach (var name in new[] { "rdc-launch.ps1", "rdc-activity.log", "rdc-runtime.log" })
        {
            try { File.Delete(Path.Combine(FleetConfig.StateDir, name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void InvalidateStatus() => _status?.Invalidate();

    /// <summary>
    /// A built status as the answer to a request: stamped with the time it is sent, as agents always did, unless no
    /// status could be built for <see cref="StatusCache.StaleAfter"/>. Then it keeps the time it was built, so it does
    /// not look current. <see cref="MachineStatusPayload.AnsweredUtc"/> gives newer dashboards this PC's clock.
    /// </summary>
    internal static MachineStatusPayload AsAnswer(MachineStatusPayload built, DateTime nowUtc) => built with
    {
        UpdatedUtc = nowUtc - built.UpdatedUtc <= StatusCache.StaleAfter ? nowUtc : built.UpdatedUtc,
        AnsweredUtc = nowUtc
    };

    private static async Task<string> ExecuteCommandAsync(MachineCommand command)
    {
        AppendAgentLog($"Running command: {command.Target} {command.Action} {command.TargetKey}".TrimEnd());
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
                        ReplaceExisting = command.Value == "replace",
                        Options = command.RegistrationOptions ?? RunnerRegistrationOptions.None
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
                case "Stop" when command.Value == "disable":
                    // Step 1 of a move: the runner leaves this PC.
                    await FleetActions.StopForMoveAsync(command.TargetKey);
                    Recovery.Forget(command.TargetKey);
                    return "Runner stopped for the move";
                case "Start" when command.Value == "enable":
                    // The move failed: this copy keeps the runner.
                    await FleetActions.StartAfterFailedMoveAsync(command.TargetKey);
                    return "Runner start completed";
                case "StopJob":
                    var stopped = await RunnerActivity.StopJobProcessesAsync(command.TargetKey);
                    return stopped.Count == 0 ? "The job had no processes to stop" : $"Stopped the job's processes: {string.Join(", ", stopped)}";
                case "Startup":
                    await RunnerService.SetStartAutomaticallyAsync(command.TargetKey, command.Value == "on", message => AppendAgentLog($"Runner startup: {message}"));
                    return command.Value == "on" ? "The runner starts automatically after a restart" : "The runner no longer starts automatically after a restart";
                case "Start" or "Stop" or "Restart":
                    // Dashboards don't offer these for such a runner, but a command can be sent from an older status.
                    if (await RunnerService.FindRunnerAsync(command.TargetKey) is { } target
                        && FleetActions.ConsoleRunnerProblem(target.IsInteractive, target.ServiceInstalled, command.Action) is { } problem)
                        throw new InvalidOperationException(problem);
                    await RunnerService.ControlServiceAsync(command.TargetKey, command.Action);
                    FleetConfig.SetManuallyStopped(command.TargetKey, command.Action == "Stop");
                    if (command.Action == "Stop") Recovery.Forget(command.TargetKey);
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
        // The task is switched to the new copy when the agent exits, after its work: registering may end this agent.
        _agentExeAfterUpdate = await AppUpdater.DownloadAndInstallAsync(release, message => AppendAgentLog($"Update: {message}"));
        AgentWork.AskRestart();
        AddEvent($"Updated to {release.Tag}. Restarting the fleet agent.");
        return $"Updated to {release.Tag}. Restarting.";
    }

    /// <summary>
    /// Restarts runners that stopped unexpectedly. Only things this agent has seen
    /// running (and the user didn't stop on purpose) are recovered, at most three times an hour each.
    /// </summary>
    private static async Task<bool> RunWatchdogAsync(LocalState local)
    {
        var config = FleetConfig.Current;
        var acted = false;
        foreach (var runner in local.Runners.Where(r => r.ServiceInstalled))
        {
            // A runner that runs from a console window is left alone: when that closes (sign-out), starting the
            // service would run it twice at the next sign-in.
            var step = Recovery.Check(runner.Name, runner.IsInteractive, runner.IsRunning, IsPending(runner),
                watchdogOn: config.Watchdog, held: IsManuallyStopped(runner.Name), DateTime.UtcNow);
            if (step == RunnerRecovery.Step.GiveUp)
                AddEvent($"Auto-recovery stopped for runner {runner.AgentName} after {RunnerRecovery.MaxAttempts} attempts in one hour", warning: true);
            if (step != RunnerRecovery.Step.Start) continue;
            try
            {
                await RunnerService.ControlServiceAsync(runner.Name, "Start");
                AddEvent($"Auto-recovery restarted runner {runner.AgentName}", warning: true);
            }
            catch (Exception ex) { AddEvent($"Auto-recovery could not start runner {runner.AgentName}: {ex.Message}", warning: true); }
            acted = true;
        }

        return acted;
    }

    /// <summary>Its service is starting or stopping (Win32_Service.State "Start Pending", "Stop Pending" and so on).</summary>
    internal static bool IsPending(RunnerInfo runner)
        => runner.StatusText.EndsWith("Pending", StringComparison.OrdinalIgnoreCase);

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
    /// Leaves the fleet: tells the other PCs to forget this one, removes the agent, and clears the fleet code and
    /// remembered PCs, so adding it again goes through pairing. Returns the PCs that were told.
    /// </summary>
    public static async Task<List<string>> LeaveFleetAsync(Action<string> log)
    {
        var told = new List<string>();
        var config = FleetConfig.Current;
        // Without another fleet PC, nobody could manage them any more: let them go.
        if (config.FleetKey != null && config.ManagedPcs.Count > 0 && !config.KnownPeers.Any(p => Sanitize(p.Name) != LocalMachineKey))
        {
            log($"Releasing {string.Join(", ", config.ManagedPcs.Select(p => p.Name))}, which no other fleet PC manages…");
            await Task.WhenAll(config.ManagedPcs.Select(pc => pc.Name).ToList().Select(async name =>
            {
                try { await RemoveManagedPcAsync(name); } catch { }
            }));
        }
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
            c.ManagedPcs.Clear();
            c.RemovedManagedPcs.Clear();
            c.ManagedBy = string.Empty;
        });
        lock (LastStatus) LastStatus.Clear();
        return told;
    }

    /// <summary>The first protocol whose agents can receive a new fleet code from Change fleet code.</summary>
    public const int RekeyProtocolVersion = 12;

    /// <summary>What Change fleet code did, by PC name.</summary>
    /// <param name="Switched">PCs that use the new code now.</param>
    /// <param name="NotConfirmed">PCs that got the new code but didn't answer with it yet; they switch the next time a PC contacts them.</param>
    /// <param name="Failed">PCs that didn't get the new code, with the reason. They must pair again.</param>
    /// <param name="LeftOut">PCs the user left out. They were forgotten and must pair again.</param>
    /// <param name="ManagedSwitched">Managed PCs that got a new code of their own.</param>
    /// <param name="ManagedFailed">Managed PCs that kept their old code (off, or too old), with the reason.</param>
    public sealed record FleetCodeChange(List<string> Switched, List<string> NotConfirmed, List<string> Failed, List<string> LeftOut,
        List<string> ManagedSwitched, List<string> ManagedFailed);

    /// <summary>
    /// Change fleet code: makes a new fleet code and gives it to the PCs in <paramref name="keep"/>, encrypted with
    /// the current code and a fresh key exchange. This PC switches only after at least one of them confirmed it (or
    /// when there are none to give it to). Each of them switches at the first request that uses the new code; this
    /// sends one to each right away. PCs that are left out, offline or too old keep the old code, so they can no
    /// longer reach the fleet and must pair again. The ones left out are forgotten here and on the PCs that switched.
    /// </summary>
    public static async Task<FleetCodeChange> ChangeFleetCodeAsync(IReadOnlyList<MachineInfo> keep, IReadOnlyList<MachineInfo> leaveOut, Action<string> log)
    {
        var oldKey = FleetConfig.Current.FleetKey ?? throw new InvalidOperationException("This PC has no fleet code.");
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
                var (address, machineId, _) = Route(machine.MachineKey);
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
                var (address, machineId, _) = Route(machine.MachineKey);
                await FleetNetwork.CallAsync(address, Sanitize(machine.MachineKey),
                    new FleetRequest { Op = "leave", From = name, TargetId = machineId }, TimeSpan.FromSeconds(4), pairingKey: newKey);
            }
            catch { }
        })));
        // Managed PCs have codes of their own, which a PC that was left out may still have: replace those too.
        var managedSwitched = new List<string>();
        var managedFailed = new List<string>();
        await Task.WhenAll(FleetConfig.Current.ManagedPcs.ToList().Select(async pc =>
        {
            try
            {
                var oldManaged = pc.Key ?? throw new InvalidOperationException("Its access code cannot be read on this PC. Remove it and pair it again.");
                var newManaged = CommandSigner.NewKey();
                await SendNewKeyAsync(pc.Address, Sanitize(pc.Name), pc.MachineId, oldManaged, newManaged);
                FleetConfig.ReplaceManagedKey(pc.KeyId, newManaged);
                // The first request with the new code makes it switch.
                try
                {
                    await FleetNetwork.CallAsync(pc.Address, Sanitize(pc.Name), new FleetRequest { Op = "status", TargetId = pc.MachineId }, pairingKey: newManaged);
                }
                catch { }
                lock (managedSwitched) managedSwitched.Add(pc.Name);
            }
            catch (Exception ex)
            {
                var reason = ex.Message.StartsWith("Unknown request", StringComparison.Ordinal) ? "Update Runner Manager on it first." : ex.Message;
                lock (managedFailed) managedFailed.Add($"{pc.Name}: {reason}");
            }
        }));
        lock (LastStatus) LastStatus.Clear();
        return new FleetCodeChange(switched, notConfirmed, failed, leftOut, managedSwitched, managedFailed);
    }

    /// <summary>Gives one PC the new code (see <see cref="ChangeFleetCodeAsync"/>), and checks that it got the same one.</summary>
    private static async Task SendNewKeyAsync(MachineInfo machine, byte[] oldKey, byte[] newKey)
    {
        var (address, machineId, _) = Route(machine.MachineKey);
        await SendNewKeyAsync(address, Sanitize(machine.MachineKey), machineId, oldKey, newKey);
    }

    private static async Task SendNewKeyAsync(string address, string key, string machineId, byte[] oldKey, byte[] newKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
        var offer = await FleetNetwork.CallAsync(address, key,
            new FleetRequest { Op = "rekey", RekeyPublicKey = mine, From = Environment.MachineName, TargetId = machineId }, pairingKey: oldKey);
        var shared = FleetCrypto.EcdhSecret(ecdh, Convert.FromBase64String(offer.RekeyPublicKey));
        var sealedKey = FleetCrypto.Seal(FleetCrypto.RekeyKey(shared), newKey, FleetCrypto.RekeyContext);
        var done = await FleetNetwork.CallAsync(address, key, new FleetRequest
        {
            Op = "rekey", RekeyPublicKey = mine, NewKey = Convert.ToBase64String(sealedKey), From = Environment.MachineName, TargetId = machineId
        }, TimeSpan.FromSeconds(30), pairingKey: oldKey); // longer than the wait for fleet.json there, so a slow save isn't a failure
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
    /// The last step before the agent exits to finish an update: starts the helper that starts the task again once this
    /// agent has exited, and only then registers the task for <paramref name="exe"/>, since registering may end this
    /// agent. A failed registration is logged; the helper then starts the task as it was.
    /// </summary>
    internal static async Task SwitchTaskBeforeExitAsync(string? exe, Action scheduleRestart, Func<string, Task> register, Action<string> log)
    {
        scheduleRestart();
        if (exe == null) return;
        try { await register(exe); }
        catch (Exception ex) { log($"Could not switch the fleet agent's task to the new version: {ex.Message}"); }
    }

    /// <summary>
    /// Restarts the local agent (after an update or a settings change that needs it). Unless <paramref name="force"/>
    /// is set, it first waits for the agent to finish commands and AI assistant calls (see <see cref="WaitWhileBusyAsync"/>).
    /// </summary>
    /// <param name="switchTo">After an update, the exe the agent's task is registered for, once the agent has stopped.</param>
    public static Task RestartLocalAgentAsync(bool force = false, Action<string>? log = null, string? switchTo = null)
        => RestartAgentAsync(new AgentRestartSteps(
            () => force ? Task.CompletedTask : WaitForLocalAgentIdleAsync(log),
            async () =>
            {
                await ScheduledTasks.EndTaskAsync(ScheduledTasks.AgentTaskName);
                await Task.Run(EndOtherAgents);
            },
            RegisterAgentTaskAsync,
            async () =>
            {
                await Task.Delay(1500);
                await ScheduledTasks.RunTaskAsync(ScheduledTasks.AgentTaskName);
            }), switchTo, log);

    /// <summary>The steps of a restart of this PC's agent (a test passes its own).</summary>
    internal sealed record AgentRestartSteps(Func<Task> WaitForIdle, Func<Task> Stop, Func<string, Task> Register, Func<Task> Start);

    /// <summary>
    /// Waits for the agent's work, stops the agent, registers its task for <paramref name="switchTo"/> (when given),
    /// and starts it. Registering may end a running agent, so it comes only after the wait. When registering fails,
    /// the agent is still started (with the task as it was), and then the failure is thrown.
    /// </summary>
    internal static async Task RestartAgentAsync(AgentRestartSteps steps, string? switchTo, Action<string>? log)
    {
        await steps.WaitForIdle();
        await steps.Stop();
        Exception? failed = null;
        if (switchTo != null)
        {
            log?.Invoke("Switching the fleet agent to the new version...");
            try { await steps.Register(switchTo); }
            catch (Exception ex) { failed = ex; }
        }
        await steps.Start();
        if (failed != null)
            throw new InvalidOperationException($"The fleet agent was started again, but its task could not be switched to the new version: {failed.Message}", failed);
    }

    private static Task WaitForLocalAgentIdleAsync(Action<string>? log)
    {
        if (FleetConfig.Current.FleetKey == null) return Task.CompletedTask;
        return WaitWhileBusyAsync(async () => (await FleetNetwork.CallAsync("127.0.0.1", LocalMachineKey, new FleetRequest { Op = "status" })).Status,
            () => DateTime.UtcNow, () => Task.Delay(TimeSpan.FromSeconds(3)), log);
    }

    /// <summary>
    /// Waits while this PC's agent reports that it is busy (protocol 12): until it is done, or until it finished no
    /// command for <see cref="UpdateRestartWait"/>, so a runner create or an AI assistant's call that is running is not
    /// cut off, but for at most <see cref="UpdateRestartLimit"/> in all.
    /// Returns right away when the agent does not answer or is older, since there is then nothing to wait for.
    /// </summary>
    internal static async Task WaitWhileBusyAsync(Func<Task<MachineStatusPayload?>> readStatus, Func<DateTime> utcNow, Func<Task> pause,
        Action<string>? log)
    {
        var restart = new UpdateRestartClock(utcNow, UpdateRestartWait, UpdateRestartLimit);
        HashSet<string>? finished = null;
        long? moves = null;
        var told = false;
        while (true)
        {
            MachineStatusPayload? status;
            try { status = await readStatus(); }
            catch { return; }
            if (status is not { Busy: true }) return;
            var now = status.RecentCommands.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A finished command, or other work that started, ended or moved on (older agents always send 0).
            if ((finished != null && !now.IsSubsetOf(finished)) || (moves != null && status.WorkMoves != moves)) restart.WorkFinished();
            finished = now;
            moves = status.WorkMoves;
            if (restart.Due(quiet: false))
            {
                var message = restart.PastLimit
                    ? $"The fleet agent is still busy after {(int)UpdateRestartLimit.TotalHours} hours. Restarting it anyway."
                    : $"The fleet agent is still busy, and it finished nothing in {(int)UpdateRestartWait.TotalMinutes} minutes. Restarting it anyway.";
                log?.Invoke(message);
                AppendAgentLog(message);
                return;
            }
            if (!told) log?.Invoke("Waiting for the fleet agent to finish its current work…");
            told = true;
            await pause();
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
                "Runner Manager fleet agent. Reports runner status and runs commands from other PCs.", restartOnFailure: true);
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

    private sealed record LocalState(List<RunnerInfo> Runners, bool AgentRunning, HealthSnapshot Health);

    /// <param name="agentRunning">Whether the agent runs, when the caller already checked.</param>
    private static async Task<LocalState> GatherLocalStateAsync(bool? agentRunning = null)
    {
        // The snapshots start with synchronous WMI and file work, so keep them off the calling (UI) thread.
        var runnersTask = Task.Run(() => RunnerService.ReadSnapshotAsync(queryGitHub: false));
        var agentTask = agentRunning is { } known ? Task.FromResult(known) : IsAgentRunningAsync();
        var runners = await runnersTask;
        var health = await Task.Run(() => SystemHealth.Read(runners.Select(r => r.InstallPath)));
        await Task.Run(() => JobWatch.Update(runners));
        return new LocalState(runners, await agentTask, health);
    }

    private static MachineStatusPayload BuildPayload(LocalState local, string lastCommandId, string lastResult)
    {
        var runners = local.Runners;
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
                CustomLabels = r.CustomLabels.Length > 0 ? r.CustomLabels : CachedCustomLabels(r),
                RegistrationOptions = r.RegistrationOptions,
                IsRunning = r.IsRunning,
                ActivityText = r.ActivityText,
                ServiceInstalled = r.ServiceInstalled,
                MovedAway = r.MovedAway,
                StartsAt = r.StartsAt,
                RunnerVersion = r.RunnerVersion,
                WorkFolderBytes = r.WorkFolderBytes,
                ManuallyStopped = config.ManuallyStopped.Contains(r.Name, StringComparer.OrdinalIgnoreCase),
                JobStartedUtc = JobWatch.For(r.InstallPath).StartedUtc,
                QuietSinceUtc = JobWatch.For(r.InstallPath).QuietSinceUtc
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
        WriteAtomically(AgentStatePath, JsonSerializer.Serialize(new
        {
            lastCommandId = state.LastCommandId,
            processed = state.Recent,
            results = state.Results
        }));
    }

    /// <summary>
    /// Writes a file so a reader finds the old text or the new one, never a part: also after a crash or power cut while
    /// writing. A broken agent-state.json would make the agent forget which commands it ran, and run a resent one twice.
    /// </summary>
    internal static void WriteAtomically(string path, string text)
    {
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Encoding.UTF8.GetBytes(text));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(100); }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
    }

    private static readonly object AgentLogGate = new();
    /// <summary>Called when an append finds the log in use, before it tries again (tests free the log here).</summary>
    internal static Action? AgentLogInUse { get; set; }

    public static void AppendAgentLog(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
        lock (AgentLogGate)
        {
            try
            {
                Directory.CreateDirectory(FleetConfig.StateDir);
                var log = new FileInfo(AgentLogPath);
                if (log.Exists && log.Length > MaxAgentLogBytes)
                    File.Move(AgentLogPath, AgentLogPath + ".1", overwrite: true);
            }
            catch { } // The other process has the log open: it is rotated at a later line.
            // The dashboard and the agent both write the log; while one appends, the other waits a moment.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.AppendAllText(AgentLogPath, line);
                    return;
                }
                catch (IOException)
                {
                    AgentLogInUse?.Invoke();
                    Thread.Sleep(25);
                }
                catch { return; }
            }
        }
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

/// <summary>Reads the tail of the agent or runner logs (locally, or for another PC's dashboard).</summary>
public static class LogReader
{
    private const int MaxLines = 400;
    private const int MaxBytes = 30_000;

    /// <param name="logKey">"agent" or "runner:&lt;service name&gt;".</param>
    public static async Task<LogPayload> ReadAsync(string logKey)
    {
        string title;
        string? path;
        if (logKey.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            title = $"Fleet agent log — {Environment.MachineName}";
            path = MachineRelay.AgentLogPath;
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

/// <summary>
/// When an agent restarts for an update although it still has work: once it finished nothing for <c>wait</c>, or
/// once <c>limit</c> passed since the update asked. Each finished command, and other work that moves on (see
/// <see cref="AgentWork.Moves"/>), starts the wait again, so work that moves on (a long runner create, several queued, an
/// approval that waits behind others) is not cut off; only work that seems stuck is, or work that keeps the agent busy
/// for longer than the limit. Both begin at the first <see cref="Due"/>, which comes right after the update asked for
/// the restart.
/// </summary>
internal sealed class UpdateRestartClock(Func<DateTime> utcNow, TimeSpan wait, TimeSpan limit)
{
    private DateTime? _byUtc;
    private DateTime? _limitUtc;

    /// <summary>Work finished or moved on: the wait, once it began, starts again from now. The limit stays.</summary>
    public void WorkFinished()
    {
        if (_byUtc != null) _byUtc = utcNow() + wait;
    }

    /// <summary>
    /// Set by <see cref="Due"/>: the restart is due only because the limit passed, while the work still moves on.
    /// </summary>
    public bool PastLimit { get; private set; }

    /// <summary>Whether to restart now. <paramref name="quiet"/>: no work is left.</summary>
    public bool Due(bool quiet)
    {
        PastLimit = false;
        if (quiet) return true;
        var now = utcNow();
        _byUtc ??= now + wait;
        _limitUtc ??= now + limit;
        if (now >= _byUtc) return true;
        PastLimit = now >= _limitUtc;
        return PastLimit;
    }
}

public sealed record LogPayload
{
    public string Title { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// A PC's status. Another PC may send a null where a text or a list belongs (an older or odd agent): it reads as empty,
/// so the status still shows. Its values are checked before use (see <see cref="PeerStatus"/>).
/// </summary>
public sealed record MachineStatusPayload
{
    public string MachineName { get; init => field = value ?? string.Empty; } = string.Empty;
    /// <summary>The PC's own ID (protocol 12; empty from older agents).</summary>
    public string MachineId { get; init => field = value ?? string.Empty; } = string.Empty;
    public int AgentProtocolVersion { get; init; }
    public string AppVersion { get; init => field = value ?? string.Empty; } = string.Empty;
    /// <summary>
    /// When the status was current. The agent stamps it when it answers (unless it could not build a status for a
    /// while); dashboards replace it with their own clock's time (see <see cref="MachineRelay.ByLocalClock"/>).
    /// </summary>
    public DateTime UpdatedUtc { get; init; }
    /// <summary>
    /// The agent's clock when it answered, so a dashboard can tell a clock that is off from a status that is old.
    /// Older agents don't send it; their <see cref="UpdatedUtc"/> is always that time. Older dashboards ignore it.
    /// </summary>
    public DateTime AnsweredUtc { get; init; }
    public bool FleetAgentRunning { get; init; }
    public string LastCommandId { get; init => field = value ?? string.Empty; } = string.Empty;
    public string LastCommandResult { get; init => field = value ?? string.Empty; } = string.Empty;
    /// <summary>Results of the last commands, oldest first (protocol 12; empty from older agents).</summary>
    public FleetCommandResult[] RecentCommands { get; init => field = value?.OfType<FleetCommandResult>().ToArray() ?? []; } = [];
    /// <summary>Commands that are queued or running (protocol 12).</summary>
    public string[] PendingCommandIds { get; init => field = value?.Where(id => !string.IsNullOrEmpty(id)).ToArray() ?? []; } = [];
    /// <summary>The agent is running a command or other work, which a restart would cut off (protocol 12).</summary>
    public bool Busy { get; init; }
    /// <summary>
    /// Grows each time the agent's work starts, ends or moves on (<see cref="AgentWork.Moves"/>), so a restart that
    /// waits for the agent's work can tell work that moves on from work that is stuck. Older agents send 0.
    /// </summary>
    public long WorkMoves { get; init; }
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public double MemoryTotalGb { get; init; }
    public DateTime? BootUtc { get; init; }
    public bool RebootPending { get; init; }
    public DiskStatus[] Disks
    {
        get;
        init => field = value?.OfType<DiskStatus>().Select(d => d.Name == null ? d with { Name = string.Empty } : d).ToArray() ?? [];
    } = [];
    public bool WatchdogEnabled { get; init; }
    /// <summary>Short fingerprint of this PC's code, so dashboards can spot a PC with a different code.</summary>
    public string SigningKeyId { get; init => field = value ?? string.Empty; } = string.Empty;
    public AgentEvent[] RecentEvents
    {
        get;
        init => field = value?.OfType<AgentEvent>().Select(e => e.Text == null ? e with { Text = string.Empty } : e).ToArray() ?? [];
    } = [];

    /// <summary>The runners. An entry that could not be read is left out, and counted in <see cref="UnreadableRunners"/>.</summary>
    [JsonConverter(typeof(RunnerListConverter))]
    public MachineRunnerStatus[] Runners
    {
        get;
        init
        {
            var runners = value ?? [];
            var unreadable = runners.Count(r => r == null || ReferenceEquals(r, RunnerListConverter.Unreadable));
            field = unreadable == 0 ? runners : runners.Where(r => r != null && !ReferenceEquals(r, RunnerListConverter.Unreadable)).ToArray();
            if (unreadable > 0) UnreadableRunners = unreadable;
        }
    } = [];

    /// <summary>How many runner entries in the status as it arrived could not be read.</summary>
    [JsonIgnore]
    internal int UnreadableRunners { get; init; }

    /// <summary>
    /// Everything except values that tick on their own (our heartbeat, the relay's last_seen, CPU and memory load),
    /// so unchanged status can skip a UI rebuild.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(this with
    {
        UpdatedUtc = default,
        AnsweredUtc = default,
        Busy = false,
        CpuPercent = 0,
        MemoryPercent = 0
    });
}

/// <summary>What a finished command reported.</summary>
public sealed record FleetCommandResult
{
    public string Id { get; init => field = value ?? string.Empty; } = string.Empty;
    public string Result { get; init => field = value ?? string.Empty; } = string.Empty;
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
    /// <summary>For Create: registration settings to keep (runner group, ephemeral, …). Older agents ignore it.</summary>
    public RunnerRegistrationOptions? RegistrationOptions { get; init; }
    public string Value { get; init; } = string.Empty;
}
