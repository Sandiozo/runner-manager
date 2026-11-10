using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RunnerManager;

public sealed record FleetRequest
{
    /// <summary>
    /// "status", "command", "log", "leave", "job" (protocol 11: a runner's live output and processes), "rekey"
    /// (protocol 12: a new pairing code from Change fleet code), "managed" (protocol 15: the PCs this fleet manages one
    /// way, for the other fleet PCs) or "release" (protocol 15: the fleet stops managing this PC).
    /// </summary>
    public string Op { get; init; } = "status";
    public MachineCommand? Command { get; init; }
    public string LogKey { get; init; } = string.Empty;
    /// <summary>The PC sending the request ("leave" uses it to say who is leaving).</summary>
    public string From { get; init; } = string.Empty;
    /// <summary>
    /// The machine ID of the PC this is meant for (protocol 12; optional). Agents with another ID refuse it, so a
    /// PC that shares a name with the intended one can't act on it. Older agents ignore it.
    /// </summary>
    public string TargetId { get; init; } = string.Empty;
    /// <summary>"rekey": the sender's ECDH public key for this change (base64 SubjectPublicKeyInfo).</summary>
    public string RekeyPublicKey { get; init; } = string.Empty;
    /// <summary>"rekey", second step: the new pairing code, sealed with the key from the exchange (base64).</summary>
    public string NewKey { get; init; } = string.Empty;
}

public sealed record FleetResponse
{
    public bool Ok { get; init; }
    public string Error { get; init; } = string.Empty;
    public MachineStatusPayload? Status { get; init; }
    public LogPayload? Log { get; init; }
    public RunnerJobActivity? Job { get; init; }
    public string CommandId { get; init; } = string.Empty;
    /// <summary>"rekey", first step: the agent's ECDH public key.</summary>
    public string RekeyPublicKey { get; init; } = string.Empty;
    /// <summary>"rekey", second step: the fingerprint of the code the agent received, to confirm it.</summary>
    public string KeyId { get; init; } = string.Empty;
    /// <summary>"managed": the PCs this PC manages one way, with their codes.</summary>
    public List<ManagedPcShare>? ManagedPcs { get; init; }
    /// <summary>"managed": codes of managed PCs that were removed or replaced.</summary>
    public List<RemovedManagedPc>? RemovedManagedPcs { get; init; }
}

/// <summary>Where an agent finds its pairing code, and the new one from "Change fleet code" until it is first used.</summary>
public interface IAgentKeys
{
    byte[]? Current { get; }
    byte[]? Pending { get; }
    void SetPending(byte[] key);
    /// <summary>A request came with the pending code: it becomes the pairing code, and the old one stops working.</summary>
    void UsePending(byte[] key);
    /// <summary>This PC is managed one way: its code is its own, known only to the fleet that manages it.</summary>
    bool IsManaged { get; }
    /// <summary>The managing fleet let this PC go: it switches to a new code of its own, which that fleet doesn't know.</summary>
    void Release();
}

/// <summary>
/// The agent's codes in fleet.json, so this PC's dashboard follows when the agent switches. The pending code is also
/// kept in memory: a Runner Manager before 2.4 that saves fleet.json meanwhile drops it from the file.
/// </summary>
/// <param name="clock">This PC's clock (tests move it on).</param>
internal sealed class ConfigAgentKeys(Func<DateTime>? clock = null) : IAgentKeys
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private volatile KeptCode? _pending;

    /// <param name="Replaces">The fingerprint of the code that was in use when it arrived.</param>
    private sealed record KeptCode(byte[] Key, string Replaces, DateTime ReceivedUtc);

    public byte[]? Current => FleetConfig.Current.FleetKey;

    public byte[]? Pending
    {
        get
        {
            var config = FleetConfig.Current;
            return config.PendingFleetKey ?? Kept(config);
        }
    }

    /// <summary>
    /// The copy in memory, while it is fresh and the code it replaces is still in use: Replace code or Enter pairing code
    /// on this PC ends it, so a code sent earlier by a PC that has left the fleet since can't switch this PC.
    /// </summary>
    private byte[]? Kept(FleetConfig config)
        => _pending is { } kept && FleetConfig.IsPendingFresh(kept.ReceivedUtc, _clock())
           && kept.Replaces == CommandSigner.KeyId(config.FleetKey) ? kept.Key : null;

    public void SetPending(byte[] key)
    {
        _pending = new KeptCode(key, CommandSigner.KeyId(Current), _clock());
        FleetConfig.SetPendingFleetKey(key);
    }

    public void UsePending(byte[] key)
    {
        FleetConfig.UsePendingFleetKey(key, Kept(FleetConfig.Current));
        _pending = null;
    }

    public bool IsManaged => FleetConfig.Current.ManagedBy.Length > 0;

    public void Release()
    {
        _pending = null;
        FleetConfig.StopBeingManaged();
    }
}

/// <summary>Codes kept in memory, for an agent given its code as a function (tests).</summary>
internal sealed class MemoryAgentKeys(Func<byte[]?> current, bool managed = false) : IAgentKeys
{
    private byte[]? _current;
    private byte[]? _pending;
    public byte[]? Current => _current ?? current();
    public byte[]? Pending => _pending;
    public void SetPending(byte[] key) => _pending = key;

    public void UsePending(byte[] key)
    {
        _current = key;
        _pending = null;
    }

    public bool IsManaged { get; private set; } = managed;

    public void Release()
    {
        _current = CommandSigner.NewKey();
        _pending = null;
        IsManaged = false;
    }
}

/// <param name="MachineId">The PC's machine ID; empty from agents before protocol 12.</param>
public sealed record DiscoveredPeer(string Name, string Address, string Version, string MachineId = "");

/// <summary>Thrown when a PC answers but refuses the request (different pairing code, wrong PC, clocks apart).</summary>
public sealed class PairingMismatchException(string message, string reason = "") : Exception(message)
{
    /// <summary>The agent's short reason, for example "denied" or "wrong PC"; empty when it gave none.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Direct PC-to-PC fleet transport. Each PC's agent listens on TCP 47821 (encrypted requests) and UDP 47822
/// (discovery probes); dashboards talk to agents directly over the local network or Tailscale.
/// </summary>
public static class FleetNetwork
{
    public const int AgentPort = 47821;
    public const int DiscoveryPort = 47822;
    private const string FirewallRulePrefix = "Runner Manager agent";
    // Local network plus Tailscale's address range, so the agent isn't reachable from the internet at large.
    private const string AllowedRemotes = "localsubnet,100.64.0.0/10";
    /// <summary>
    /// How long connecting to a PC may take. A PC that is off then fails sooner than the request's timeout, while there is
    /// room for a slow first connection over a Tailscale relay and for one lost connection attempt (Windows tries again
    /// after 3 seconds). Declared before <see cref="Http"/>, which uses it.
    /// </summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly HttpClient Http = new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan HostLookupTimeout = TimeSpan.FromSeconds(1.5);
    private const int MaxRefusalLength = 200;
    /// <summary>
    /// The largest answer read from an agent. Logs and job output are cut to about 30 KB by the agent, and a status
    /// with many runners is well under 1 MB, so this only stops a broken or hostile answer from filling memory.
    /// </summary>
    internal const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxErrorBodyBytes = 4 * 1024;

    /// <summary>The keys derived from one pairing code (HKDF runs once per code, not once per request).</summary>
    internal sealed record DerivedKeys(byte[] Transport, byte[] Discovery);

    private static readonly ConcurrentDictionary<string, DerivedKeys> KeyCache = new(StringComparer.Ordinal);

    internal static DerivedKeys KeysFor(byte[] pairingKey)
    {
        var id = Convert.ToBase64String(pairingKey);
        if (KeyCache.TryGetValue(id, out var keys)) return keys;
        if (KeyCache.Count > 16) KeyCache.Clear(); // a PC only ever has one or two codes; tests make many
        return KeyCache.GetOrAdd(id, _ => new DerivedKeys(FleetCrypto.TransportKey(pairingKey), FleetCrypto.DiscoveryKey(pairingKey)));
    }

    // ================= Client (dashboard) =================

    /// <summary>
    /// Requests go straight to the agents, on the local network or Tailscale: a proxy set up in Windows can't reach those
    /// addresses, and an agent never redirects.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = ConnectTimeout
    };

    internal const string TargetHeaderName = "X-RW-To";

    /// <summary>
    /// The X-RW-To header for a PC name key, which can have letters outside A–Z. A header holds only ASCII here, and the
    /// agent's HttpListener reads other bytes with the PC's code page, so any other name (such as "ÖSTRA" or "ĀBOLS") is
    /// percent-encoded, which only newer agents decode; before, requests to them could not be sent at all. ASCII names
    /// are sent as they are, as before.
    /// </summary>
    internal static string TargetHeader(string machineKey)
        => machineKey.All(char.IsAscii) ? machineKey : Uri.EscapeDataString(machineKey);

    /// <summary>The PC name key in an X-RW-To header. A name key never has a '%', so one means it was percent-encoded.</summary>
    internal static string TargetFromHeader(string header) => header.Contains('%') ? Uri.UnescapeDataString(header) : header;

    /// <summary>A refusal of a request whose PC name was percent-encoded: an older agent can't read it.</summary>
    internal static string RefusalMessage(string reason, string targetMachineKey)
    {
        var message = RefusalMessage(reason);
        return TargetHeader(targetMachineKey) != targetMachineKey && (reason.Length == 0 || reason.Equals("denied", StringComparison.OrdinalIgnoreCase))
            ? $"{message} Or {targetMachineKey} runs an older version of Runner Manager, which cannot read some letters in its name. Update Runner Manager there."
            : message;
    }

    public static async Task<FleetResponse> CallAsync(string address, string targetMachineKey, FleetRequest request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default, byte[]? pairingKey = null)
    {
        pairingKey ??= FleetConfig.Current.FleetKey
            ?? throw new InvalidOperationException("This PC has no pairing code. Set one up in Settings → Fleet.");
        var transportKey = KeysFor(pairingKey).Transport;
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = FleetCrypto.Seal(transportKey, JsonSerializer.SerializeToUtf8Bytes(request),
            FleetCrypto.RequestContext(targetMachineKey, time));

        using var message = new HttpRequestMessage(HttpMethod.Post, $"http://{WithPort(address)}/rw/")
        {
            Content = new ByteArrayContent(body)
        };
        message.Headers.Add("X-RW-Time", time.ToString());
        message.Headers.Add(TargetHeaderName, TargetHeader(targetMachineKey));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxErrorBodyBytes, truncate: true, cts.Token);
            var reason = Encoding.UTF8.GetString(error!).Trim();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new PairingMismatchException(RefusalMessage(reason, targetMachineKey), reason);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new PairingMismatchException("The fleet agent on that PC has no pairing code.", reason);
            if (reason.Length > MaxRefusalLength) reason = reason[..MaxRefusalLength];
            throw new InvalidOperationException($"The fleet agent returned HTTP {(int)response.StatusCode}: {reason}");
        }
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("The fleet agent's answer is too large.");
        var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxResponseBytes, truncate: false, cts.Token)
                    ?? throw new InvalidDataException("The fleet agent's answer is too large.");
        var plain = FleetCrypto.Open(transportKey, bytes, FleetCrypto.ResponseContext(FleetCrypto.NonceOf(body)))
            ?? throw new PairingMismatchException("Could not verify the response with this PC's pairing code.");
        var result = JsonSerializer.Deserialize<FleetResponse>(plain, Json) ?? throw new InvalidOperationException("The fleet agent returned an empty response.");
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>
    /// Explains a 403 answer. Agents send a short reason ("denied" when the pairing code does not match, or for
    /// example a clock or PC name problem); anything else is shown as the reason.
    /// </summary>
    internal static string RefusalMessage(string reason)
    {
        if (reason.Length == 0 || reason.Equals("denied", StringComparison.OrdinalIgnoreCase))
            return "The pairing code does not match. Use the same pairing code on every PC.";
        if (reason.Contains("clock", StringComparison.OrdinalIgnoreCase))
            return ClockMessage(reason);
        if (reason.Length > MaxRefusalLength) reason = reason[..MaxRefusalLength];
        return $"The other PC refused the request: {reason}";
    }

    /// <summary>The time a newer agent reports with a clock refusal, as Unix milliseconds.</summary>
    private static readonly System.Text.RegularExpressions.Regex AgentTimePattern = new(@"\(time there: (\d+)\)");

    private static string ClockMessage(string reason)
    {
        const string fix = "Set both to the correct time (Windows Settings → Time & language → Date & time → Sync now).";
        var match = AgentTimePattern.Match(reason);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var agentTime))
            return $"The clocks on this PC and the other PC differ by more than {(int)FleetCrypto.MaxClockSkew.TotalMinutes} minutes. {fix}";
        var apart = TimeSpan.FromMilliseconds(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - agentTime));
        var text = apart.TotalHours >= 2 ? $"{Math.Round(apart.TotalHours)} hours"
            : apart.TotalMinutes >= 2 ? $"{Math.Round(apart.TotalMinutes)} minutes"
            : $"{Math.Max(1, Math.Round(apart.TotalSeconds))} seconds";
        return $"The clocks on this PC and the other PC differ by {text}. {fix}";
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes. Returns null when there is more, or with
    /// <paramref name="truncate"/> the first <paramref name="limit"/> bytes.
    /// </summary>
    internal static async Task<byte[]?> ReadLimitedAsync(Stream input, int limit, bool truncate, CancellationToken cancellationToken)
    {
        await using (input)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    if (!truncate) return null;
                    buffer.Write(chunk, 0, (int)(limit - buffer.Length));
                    break;
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    /// <summary>Finds fleet agents on the local network (broadcast) and at known or manually added addresses.</summary>
    public static async Task<List<DiscoveredPeer>> DiscoverAsync(IEnumerable<string> unicastHosts, TimeSpan wait,
        int discoveryPort = DiscoveryPort, byte[]? pairingKey = null)
    {
        var found = new Dictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
        pairingKey ??= FleetConfig.Current.FleetKey;
        if (pairingKey == null) return found.Values.ToList();
        var discoveryKey = KeysFor(pairingKey).Discovery;
        var probe = FleetCrypto.Probe(discoveryKey, out var nonce);

        using var udp = CreateProbeSocket();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var broadcast in DirectedBroadcastAddresses()) targets.Add(broadcast);
        foreach (var ip in await ResolveIPv4Async(unicastHosts)) targets.Add(ip);
        foreach (var target in targets)
        {
            try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, discoveryPort)); } catch { }
        }

        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token); }
                catch (SocketException) { continue; } // an offline PC must not end the search
                var body = FleetCrypto.VerifyAnswer(discoveryKey, nonce, packet.Buffer);
                if (body == null) continue;
                try
                {
                    using var json = JsonDocument.Parse(body);
                    var root = json.RootElement;
                    var name = root.GetProperty("name").GetString() ?? string.Empty;
                    var port = root.TryGetProperty("port", out var p) ? p.GetInt32() : AgentPort;
                    var id = root.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                    if (name.Length == 0) continue;
                    // A PC with several network adapters answers once on each; keep its first answer. Two PCs with
                    // the same name (but different IDs) are both kept.
                    found.TryAdd($"{name}|{id}", new DiscoveredPeer(name, JoinHostPort(packet.RemoteEndPoint.Address.ToString(), port),
                        root.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty, id));
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        return found.Values.ToList();
    }

    /// <summary>A broadcast-capable UDP socket for sending probes and collecting answers.</summary>
    public static UdpClient CreateProbeSocket()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        IgnoreConnectionResets(udp);
        return udp;
    }

    /// <summary>
    /// On Windows, sending UDP to a port nobody listens on makes the socket's next receive fail with "connection reset"
    /// (an ICMP port-unreachable echo). Turn that off so one unreachable target can't break discovery.
    /// </summary>
    public static void IgnoreConnectionResets(UdpClient udp)
    {
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); } catch { }
    }

    /// <summary>
    /// The IPv4 addresses of "host" or "host:port" entries. Names are looked up in parallel, each with a short timeout,
    /// so one unreachable name does not delay discovery.
    /// </summary>
    internal static async Task<IPAddress[]> ResolveIPv4Async(IEnumerable<string> hosts)
    {
        var lookups = hosts.Select(HostOf)
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(async name =>
            {
                if (IPAddress.TryParse(name, out var literal))
                    return literal.AddressFamily == AddressFamily.InterNetwork ? new[] { literal } : Array.Empty<IPAddress>();
                try
                {
                    using var cts = new CancellationTokenSource(HostLookupTimeout);
                    return await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, cts.Token).WaitAsync(HostLookupTimeout);
                }
                catch { return Array.Empty<IPAddress>(); }
            });
        return (await Task.WhenAll(lookups)).SelectMany(a => a).ToArray();
    }

    /// <summary>The directed broadcast address of each IPv4 network this PC is on.</summary>
    internal static IEnumerable<IPAddress> DirectedBroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    public static string WithPort(string address)
    {
        var (host, port) = SplitHostPort(address);
        return JoinHostPort(host, port ?? AgentPort);
    }

    /// <summary>
    /// Splits "host", "host:port", "1.2.3.4:port", "[v6]:port", "[v6]" or a bare IPv6 address. The port is null when
    /// none was given.
    /// </summary>
    public static (string Host, int? Port) SplitHostPort(string address)
    {
        var text = address.Trim();
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end > 0)
            {
                var host = text[1..end];
                var rest = text[(end + 1)..];
                return (host, rest.StartsWith(':') && int.TryParse(rest[1..], out var v6Port) ? v6Port : null);
            }
        }
        var colon = text.LastIndexOf(':');
        // More than one colon without brackets is an IPv6 address without a port.
        if (colon < 0 || text.IndexOf(':') != colon) return (text, null);
        return int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text[..colon], null);
    }

    /// <summary>"host:port", with IPv6 addresses in brackets.</summary>
    public static string JoinHostPort(string host, int port)
        => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    /// <summary>The host part of an address, without the port or IPv6 brackets.</summary>
    public static string HostOf(string address) => SplitHostPort(address).Host;

    // ================= Firewall =================

    /// <summary>Allows the agent's ports from the local network and Tailscale only (run elevated).</summary>
    public static async Task EnsureFirewallRulesAsync()
    {
        // Rules from the first direct-connection build were named without the port.
        foreach (var legacy in new[] { "TCP", "UDP" })
            await ScheduledTasks.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRulePrefix} ({legacy})\"");
        foreach (var (protocol, port) in RulePorts)
        {
            var name = RuleName(protocol, port);
            var (exists, _) = await ScheduledTasks.RunAsync("netsh.exe", $"advfirewall firewall show rule name=\"{name}\"");
            if (exists == 0) continue;
            await ScheduledTasks.RunAsync("netsh.exe",
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={port} " +
                $"remoteip={AllowedRemotes} profile=any description=\"Runner Manager fleet agent. Allows the other PCs in the fleet to connect to this PC.\"");
        }
    }

    public static async Task RemoveFirewallRulesAsync()
    {
        foreach (var (protocol, port) in RulePorts)
            await ScheduledTasks.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{RuleName(protocol, port)}\"");
    }

    // Agent (TCP), discovery (UDP), and pairing (TCP exchange + UDP announce while "Add a PC" is open).
    private static readonly (string Protocol, int Port)[] RulePorts =
    {
        ("TCP", AgentPort), ("UDP", DiscoveryPort), ("TCP", Pairing.PairingPort), ("UDP", Pairing.AnnouncePort)
    };

    private static string RuleName(string protocol, int port) => $"{FirewallRulePrefix} ({protocol} {port})";

    public static IEnumerable<string> LocalAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString());
}

/// <summary>The agent's listeners: encrypted HTTP requests on TCP 47821 and discovery probes on UDP 47822.</summary>
public sealed class AgentServer : IDisposable
{
    private readonly Func<Task<MachineStatusPayload>> _status;
    private readonly Func<MachineCommand, string> _enqueue;
    private readonly Func<string, Task<LogPayload>> _log;
    private readonly Func<string, Task<RunnerJobActivity>>? _job;
    private readonly IAgentKeys _keys;
    private readonly string _prefix;
    private readonly int _discoveryPort;
    private readonly string _machineKey;
    private readonly string _displayName;
    private readonly string _machineId;
    private readonly ReplayGuard _replay;
    private readonly byte[] _discoveryBody;
    private readonly CancellationTokenSource _cts = new();
    // Requests that are not authenticated yet; each may buffer up to MaxRequestBytes.
    private readonly SemaphoreSlim _unauthenticated = new(MaxUnauthenticatedRequests);
    private readonly object _listenGate = new();
    private HttpListener? _http;
    private UdpClient? _udp;
    private bool _retrying;
    // The first step of a rekey, by the sender's public key; each is kept for RekeyOfferTime.
    private readonly Dictionary<string, (ECDiffieHellman Key, DateTime CreatedUtc)> _rekeyOffers = new(StringComparer.Ordinal);
    private static readonly TimeSpan RekeyOfferTime = TimeSpan.FromMinutes(2);

    /// <param name="prefix">HTTP prefix to listen on (tests use a localhost one; the agent listens on all interfaces).</param>
    /// <param name="machineId">This PC's machine ID; by default the one in fleet.json for the real agent, none for tests.</param>
    /// <param name="keys">Where the pairing codes are kept; by default fleet.json, or memory when <paramref name="pairingKey"/> is given.</param>
    public AgentServer(Func<Task<MachineStatusPayload>> status, Func<MachineCommand, string> enqueue, Func<string, Task<LogPayload>> log,
        Func<byte[]?>? pairingKey = null, string? prefix = null, int discoveryPort = FleetNetwork.DiscoveryPort, string? machineKey = null,
        Func<string, Task<RunnerJobActivity>>? job = null, string? machineId = null, IAgentKeys? keys = null)
    {
        _status = status;
        _enqueue = enqueue;
        _log = log;
        _job = job;
        _keys = keys ?? (pairingKey != null ? new MemoryAgentKeys(pairingKey) : new ConfigAgentKeys());
        _prefix = prefix ?? $"http://+:{FleetNetwork.AgentPort}/rw/";
        _discoveryPort = discoveryPort;
        // Requests are addressed by the sanitized key; discovery announces the real PC name.
        _displayName = machineKey ?? Environment.MachineName;
        _machineKey = MachineRelay.Sanitize(_displayName);
        _machineId = machineId ?? (prefix == null ? FleetConfig.LocalMachineId : string.Empty);
        // The real agent remembers the requests it has seen across restarts; tests keep them in memory.
        _replay = new ReplayGuard(prefix == null ? Path.Combine(FleetConfig.StateDir, "seen-requests.txt") : null);
        // The answer body never changes; only its signature depends on the probe.
        _discoveryBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = _displayName,
            id = _machineId,
            port = Port,
            protocol = MachineRelay.ProtocolVersion,
            version = AppUpdater.CurrentVersion
        });
    }

    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxUnauthenticatedRequests = 16;
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);
    /// <summary>How often a listener that could not start, or stopped, is tried again.</summary>
    internal static TimeSpan ListenRetryInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>This agent's <see cref="ListenRetryInterval"/>, read when it is created (tests set it per agent).</summary>
    internal TimeSpan RetryInterval { get; init; } = ListenRetryInterval;
    /// <summary>Waiting for a request failed this many times in a row: the listener is broken (http.sys restarted, for example).</summary>
    private const int MaxListenFailures = 3;

    private int Port => new Uri(_prefix.Replace("+", "localhost")).Port;

    /// <summary>Whether both listeners are running.</summary>
    public bool IsListening => _http != null && _udp != null;

    /// <summary>
    /// Starts both listeners. One that can't start (its port is taken, for example) is logged once and tried again
    /// every <see cref="RetryInterval"/>; the first such error is also thrown.
    /// </summary>
    public void Start()
    {
        var httpError = TryStartHttp();
        var udpError = TryStartUdp();
        var retry = $"Trying again every {(int)RetryInterval.TotalSeconds} seconds.";
        if (httpError != null) MachineRelay.AppendAgentLog($"Could not listen on TCP port {Port}: {httpError.Message} {retry}");
        if (udpError != null) MachineRelay.AppendAgentLog($"Could not listen for discovery on UDP port {_discoveryPort}: {udpError.Message} {retry}");
        if (httpError == null && udpError == null) return;
        EnsureRetrying();
        throw httpError ?? udpError!;
    }

    private void EnsureRetrying()
    {
        lock (_listenGate)
        {
            if (_retrying || _cts.IsCancellationRequested) return;
            _retrying = true;
        }
        _ = Task.Run(RetryListenersAsync);
    }

    private Exception? TryStartHttp()
    {
        var http = new HttpListener();
        try
        {
            http.Prefixes.Add(_prefix);
            http.Start();
        }
        catch (Exception ex)
        {
            try { http.Close(); } catch { }
            return ex;
        }
        // A client that sends its request slowly holds a request slot; http.sys ends it sooner with these.
        try
        {
            http.TimeoutManager.EntityBody = BodyTimeout;
            http.TimeoutManager.HeaderWait = BodyTimeout;
        }
        catch { }
        lock (_listenGate)
        {
            // Disposed meanwhile: the port is not held.
            if (_cts.IsCancellationRequested) { try { http.Close(); } catch { } return null; }
            _http = http;
        }
        _ = Task.Run(() => AcceptLoopAsync(http));
        return null;
    }

    private Exception? TryStartUdp()
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort)) { EnableBroadcast = true };
            FleetNetwork.IgnoreConnectionResets(udp);
            lock (_listenGate)
            {
                if (_cts.IsCancellationRequested) { udp.Dispose(); return null; }
                _udp = udp;
            }
            _ = Task.Run(() => DiscoveryLoopAsync(udp));
        }
        catch (Exception ex) { return ex; }
        return null;
    }

    private async Task RetryListenersAsync()
    {
        while (true)
        {
            // Checked under the lock, so a listener that stops just as this loop ends still gets a new one.
            lock (_listenGate)
            {
                if (_cts.IsCancellationRequested || IsListening) { _retrying = false; return; }
            }
            try { await Task.Delay(RetryInterval, _cts.Token); }
            catch (OperationCanceledException) { lock (_listenGate) _retrying = false; return; }
            // Failures were logged once already; only the recovery is.
            if (_http == null && TryStartHttp() == null) MachineRelay.AppendAgentLog($"Listening on TCP port {Port} now.");
            if (_udp == null && TryStartUdp() == null) MachineRelay.AppendAgentLog($"Listening for discovery on UDP port {_discoveryPort} now.");
        }
    }

    /// <summary>A listener stopped after it started: it is closed, and a new one is tried every <see cref="RetryInterval"/>.</summary>
    private void ListenerStopped(IDisposable listener, Exception? error)
    {
        lock (_listenGate)
        {
            if (_cts.IsCancellationRequested) return;
            if (ReferenceEquals(listener, _http)) _http = null;
            else if (ReferenceEquals(listener, _udp)) _udp = null;
            else return;
        }
        try { listener.Dispose(); } catch { }
        var what = listener is HttpListener ? $"on TCP port {Port}" : $"for discovery on UDP port {_discoveryPort}";
        var why = error != null ? $": {error.Message}" : string.Empty;
        MachineRelay.AppendAgentLog($"Stopped listening {what}{why}. Trying again every {(int)RetryInterval.TotalSeconds} seconds.");
        EnsureRetrying();
    }

    private async Task AcceptLoopAsync(HttpListener http)
    {
        var failures = 0;
        Exception? error = null;
        while (!_cts.IsCancellationRequested && http.IsListening && failures < MaxListenFailures)
        {
            HttpListenerContext context;
            try { context = await http.GetContextAsync(); }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                failures++;
                error = ex;
                await Task.Delay(500);
                continue;
            }
            failures = 0;
            _ = Task.Run(() => HandleAsync(context));
        }
        ListenerStopped(http, error);
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        var holdsSlot = false;
        try
        {
            var pairingKey = _keys.Current;
            if (pairingKey == null) { await Reply(response, 503, "not paired"); return; }
            // Only requests with a known, limited length (no chunked bodies).
            if (context.Request.HttpMethod != "POST" || context.Request.ContentLength64 is < 0 or > MaxRequestBytes
                || context.Request.Headers["Transfer-Encoding"] != null)
            { await Reply(response, 400, "bad request"); return; }
            if (!long.TryParse(context.Request.Headers["X-RW-Time"], out var time) || !FleetCrypto.IsFresh(time))
            {
                await Reply(response, 403, $"the PC clocks differ by more than {(int)FleetCrypto.MaxClockSkew.TotalMinutes} minutes; set both to the correct time {TimeHere()}");
                return;
            }
            // The agent restarted after this was sent, and no longer knows whether it saw it before.
            if (_replay.IsFromBeforeStart(time))
            {
                await Reply(response, 403, "the request is older than the fleet agent, which has restarted since; try again, and check that both PCs show the correct time");
                return;
            }

            if (!await _unauthenticated.WaitAsync(SlotWait, _cts.Token)) { await Reply(response, 429, "busy"); return; }
            holdsSlot = true;
            var body = await FleetNetwork.ReadLimitedAsync(context.Request.InputStream, MaxRequestBytes, truncate: false, _cts.Token)
                .WaitAsync(BodyTimeout, _cts.Token);
            if (body == null) { await Reply(response, 400, "bad request"); return; }
            var transportKey = FleetNetwork.KeysFor(pairingKey).Transport;
            var target = FleetNetwork.TargetFromHeader(context.Request.Headers[FleetNetwork.TargetHeaderName] ?? string.Empty);
            var requestContext = FleetCrypto.RequestContext(target, time);
            // Authenticate before saying anything about the target, so strangers can't use the answer to guess PC names.
            var plain = FleetCrypto.Open(transportKey, body, requestContext);
            var pending = plain == null ? _keys.Pending : null;
            if (pending != null)
            {
                // Sent with the new code from Change fleet code: from now on, only that code works.
                plain = FleetCrypto.Open(FleetNetwork.KeysFor(pending).Transport, body, requestContext);
                if (plain != null) transportKey = FleetNetwork.KeysFor(pending).Transport;
                else pending = null;
            }
            if (plain == null || !_replay.TryUse(FleetCrypto.NonceOf(body))) { await Reply(response, 403, "denied"); return; }
            _unauthenticated.Release();
            holdsSlot = false;
            if (pending != null)
            {
                _keys.UsePending(pending);
                MachineRelay.AppendAgentLog("Switched to the new pairing code. The old one no longer works.");
            }

            // "*" is only accepted for status requests (used to identify a PC added by address).
            if (!target.Equals(_machineKey, StringComparison.OrdinalIgnoreCase) && target != "*")
            { await Reply(response, 403, "wrong PC"); return; }
            var request = JsonSerializer.Deserialize<FleetRequest>(plain) ?? new FleetRequest();
            if (target == "*" && request.Op != "status") { await Reply(response, 403, "wrong PC"); return; }
            // Meant for another PC with the same name.
            if (request.TargetId.Length > 0 && _machineId.Length > 0 && !request.TargetId.Equals(_machineId, StringComparison.OrdinalIgnoreCase))
            { await Reply(response, 403, "wrong PC"); return; }
            FleetResponse result;
            try
            {
                result = request.Op switch
                {
                    "status" => new FleetResponse { Ok = true, Status = await _status() },
                    "command" when request.Command != null => new FleetResponse { Ok = true, CommandId = _enqueue(request.Command) },
                    "log" => new FleetResponse { Ok = true, Log = await _log(request.LogKey) },
                    "job" when _job != null => new FleetResponse { Ok = true, Job = await _job(request.LogKey) },
                    "leave" when request.From.Length > 0 => Leave(request.From),
                    "rekey" => Rekey(request),
                    "managed" => ShareManaged(),
                    "release" => Release(request.From),
                    _ => new FleetResponse { Ok = false, Error = $"Unknown request: {request.Op}" }
                };
            }
            catch (Exception ex) { result = new FleetResponse { Ok = false, Error = ex.Message }; }

            var sealedResponse = FleetCrypto.Seal(transportKey, JsonSerializer.SerializeToUtf8Bytes(result),
                FleetCrypto.ResponseContext(FleetCrypto.NonceOf(body)));
            response.StatusCode = 200;
            response.ContentType = "application/octet-stream";
            await response.OutputStream.WriteAsync(sealedResponse);
            response.Close();
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            try { response.Abort(); } catch { }
        }
        finally
        {
            if (holdsSlot) _unauthenticated.Release();
        }
    }

    /// <summary>This PC's clock, so the other PC can say how far apart the clocks are.</summary>
    private static string TimeHere() => $"(time there: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})";

    private static readonly object FailureLogGate = new();
    private static DateTime _lastFailureLogUtc = DateTime.MinValue;
    private static int _unloggedFailures;

    /// <summary>Logs failed requests at most once a minute, so a flood of broken requests can't fill the log.</summary>
    private static void LogFailure(Exception ex)
    {
        lock (FailureLogGate)
        {
            if (DateTime.UtcNow - _lastFailureLogUtc < TimeSpan.FromMinutes(1))
            {
                _unloggedFailures++;
                return;
            }
            var more = _unloggedFailures > 0 ? $" ({_unloggedFailures} more since the last one was logged)" : string.Empty;
            _lastFailureLogUtc = DateTime.UtcNow;
            _unloggedFailures = 0;
            MachineRelay.AppendAgentLog($"Request failed: {ex.Message}{more}");
        }
    }

    /// <summary>
    /// "Change fleet code" on another PC. First step: answer with a key of our own for a key exchange. Second step:
    /// the new code arrives sealed with the exchanged key, so even someone who has the old code can't read it off the
    /// network. It is kept as pending, and used from the first request that comes with it.
    /// </summary>
    private FleetResponse Rekey(FleetRequest request)
    {
        if (request.RekeyPublicKey.Length == 0) return new FleetResponse { Ok = false, Error = "No key exchange." };
        lock (_rekeyOffers)
        {
            foreach (var old in _rekeyOffers.Where(o => DateTime.UtcNow - o.Value.CreatedUtc > RekeyOfferTime).ToList())
            {
                old.Value.Key.Dispose();
                _rekeyOffers.Remove(old.Key);
            }
            if (request.NewKey.Length == 0)
            {
                if (_rekeyOffers.Count >= 8) return new FleetResponse { Ok = false, Error = "Too many fleet code changes at once. Try again in a few minutes." };
                var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                _rekeyOffers[request.RekeyPublicKey] = (ecdh, DateTime.UtcNow);
                return new FleetResponse { Ok = true, RekeyPublicKey = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo()) };
            }
            if (!_rekeyOffers.Remove(request.RekeyPublicKey, out var offer))
                return new FleetResponse { Ok = false, Error = "The new fleet code came too late. Try again." };
            using (offer.Key)
            {
                var shared = FleetCrypto.EcdhSecret(offer.Key, Convert.FromBase64String(request.RekeyPublicKey));
                var newKey = FleetCrypto.Open(FleetCrypto.RekeyKey(shared), Convert.FromBase64String(request.NewKey), FleetCrypto.RekeyContext);
                if (newKey is not { Length: 32 }) return new FleetResponse { Ok = false, Error = "Could not read the new fleet code." };
                _keys.SetPending(newKey);
                MachineRelay.AppendAgentLog($"Received a new pairing code{(request.From.Length > 0 ? $" from {request.From}" : "")}. It is used from the first request that comes with it.");
                return new FleetResponse { Ok = true, KeyId = CommandSigner.KeyId(newKey) };
            }
        }
    }

    /// <summary>
    /// The PCs this PC manages one way, so another fleet PC can manage them too. Only fleet members are told: a managed
    /// PC is reached with its own code, and the fleet managing it has no business with the PCs it manages itself.
    /// </summary>
    private FleetResponse ShareManaged()
    {
        if (_keys.IsManaged) return new FleetResponse { Ok = false, Error = "This PC is managed one way and doesn't share its PCs." };
        var (pcs, removed) = FleetConfig.Current.ShareManaged();
        return new FleetResponse { Ok = true, ManagedPcs = pcs, RemovedManagedPcs = removed };
    }

    /// <summary>
    /// The fleet that manages this PC removed it. It switches to a new code of its own, so a fleet PC that still has the
    /// old one (one that was locked out, for example) can't control it either.
    /// </summary>
    private FleetResponse Release(string from)
    {
        if (!_keys.IsManaged) return new FleetResponse { Ok = false, Error = "This PC is a member of the fleet, not managed by it. Use Leave fleet on it instead." };
        _keys.Release();
        MachineRelay.AppendAgentLog($"{(from.Length > 0 ? from : "The fleet")} stopped managing this PC. Its old pairing code no longer works.");
        return new FleetResponse { Ok = true };
    }

    /// <summary>Another PC left the fleet: forget it here so dashboards stop listing it.</summary>
    private static FleetResponse Leave(string name)
    {
        MachineRelay.ForgetPeer(name);
        MachineRelay.AppendAgentLog($"{name} left the fleet.");
        return new FleetResponse { Ok = true };
    }

    private static async Task Reply(HttpListenerResponse response, int status, string text)
    {
        response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private async Task DiscoveryLoopAsync(UdpClient udp)
    {
        var failures = 0;
        Exception? error = null;
        while (!_cts.IsCancellationRequested && failures < MaxListenFailures)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                failures++;
                error = ex;
                await Task.Delay(200);
                continue;
            }
            failures = 0;
            // Answering one sender can fail (its address is unreachable, for example); the listener still works.
            try
            {
                var pairingKey = _keys.Current;
                if (pairingKey == null) continue;
                var discoveryKey = FleetNetwork.KeysFor(pairingKey).Discovery;
                var nonce = FleetCrypto.VerifyProbe(discoveryKey, packet.Buffer);
                // A PC that already uses the new code from Change fleet code.
                if (nonce == null && _keys.Pending is { } pending)
                {
                    discoveryKey = FleetNetwork.KeysFor(pending).Discovery;
                    nonce = FleetCrypto.VerifyProbe(discoveryKey, packet.Buffer);
                }
                if (nonce == null) continue;
                var answer = FleetCrypto.Answer(discoveryKey, nonce, _discoveryBody);
                await udp.SendAsync(answer, answer.Length, packet.RemoteEndPoint);
            }
            catch (ObjectDisposedException) { return; }
            catch { }
        }
        ListenerStopped(udp, error);
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_rekeyOffers)
        {
            foreach (var offer in _rekeyOffers.Values) offer.Key.Dispose();
            _rekeyOffers.Clear();
        }
        HttpListener? http;
        UdpClient? udp;
        lock (_listenGate) (http, udp) = (_http, _udp);
        // Close also releases the URL, so a new listener on the same port can start right away.
        try { http?.Close(); } catch { }
        try { udp?.Dispose(); } catch { }
    }
}
