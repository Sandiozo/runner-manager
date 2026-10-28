using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RunnerManager;

public sealed record FleetRequest
{
    /// <summary>"status", "command", "log", "leave", or "job" (protocol 11: a runner's live output and processes).</summary>
    public string Op { get; init; } = "status";
    public MachineCommand? Command { get; init; }
    public string LogKey { get; init; } = string.Empty;
    /// <summary>The PC sending the request ("leave" uses it to say who is leaving).</summary>
    public string From { get; init; } = string.Empty;
}

public sealed record FleetResponse
{
    public bool Ok { get; init; }
    public string Error { get; init; } = string.Empty;
    public MachineStatusPayload? Status { get; init; }
    public LogPayload? Log { get; init; }
    public RunnerJobActivity? Job { get; init; }
    public string CommandId { get; init; } = string.Empty;
}

public sealed record DiscoveredPeer(string Name, string Address, string Version);

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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
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
        message.Headers.Add("X-RW-To", targetMachineKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cts.Token), MaxErrorBodyBytes, truncate: true, cts.Token);
            var reason = Encoding.UTF8.GetString(error!).Trim();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new PairingMismatchException(RefusalMessage(reason), reason);
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
                    if (name.Length == 0) continue;
                    found[name] = new DiscoveredPeer(name, $"{packet.RemoteEndPoint.Address}:{port}",
                        root.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty);
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
    private readonly Func<byte[]?> _pairingKey;
    private readonly string _prefix;
    private readonly int _discoveryPort;
    private readonly string _machineKey;
    private readonly string _displayName;
    private readonly ReplayGuard _replay = new();
    private readonly byte[] _discoveryBody;
    private readonly CancellationTokenSource _cts = new();
    // Requests that are not authenticated yet; each may buffer up to MaxRequestBytes.
    private readonly SemaphoreSlim _unauthenticated = new(MaxUnauthenticatedRequests);
    private readonly object _listenGate = new();
    private HttpListener? _http;
    private UdpClient? _udp;
    private bool _retrying;

    /// <param name="prefix">HTTP prefix to listen on (tests use a localhost one; the agent listens on all interfaces).</param>
    public AgentServer(Func<Task<MachineStatusPayload>> status, Func<MachineCommand, string> enqueue, Func<string, Task<LogPayload>> log,
        Func<byte[]?>? pairingKey = null, string? prefix = null, int discoveryPort = FleetNetwork.DiscoveryPort, string? machineKey = null,
        Func<string, Task<RunnerJobActivity>>? job = null)
    {
        _status = status;
        _enqueue = enqueue;
        _log = log;
        _job = job;
        _pairingKey = pairingKey ?? (() => FleetConfig.Current.FleetKey);
        _prefix = prefix ?? $"http://+:{FleetNetwork.AgentPort}/rw/";
        _discoveryPort = discoveryPort;
        // Requests are addressed by the sanitized key; discovery announces the real PC name.
        _displayName = machineKey ?? Environment.MachineName;
        _machineKey = MachineRelay.Sanitize(_displayName);
        // The answer body never changes; only its signature depends on the probe.
        _discoveryBody = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = _displayName,
            port = Port,
            protocol = MachineRelay.ProtocolVersion,
            version = AppUpdater.CurrentVersion
        });
    }

    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxUnauthenticatedRequests = 16;
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(2);
    /// <summary>How often a listener that could not start is tried again.</summary>
    internal static TimeSpan ListenRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    private int Port => new Uri(_prefix.Replace("+", "localhost")).Port;

    /// <summary>Whether both listeners are running.</summary>
    public bool IsListening => _http != null && _udp != null;

    /// <summary>
    /// Starts both listeners. One that can't start (its port is taken, for example) is logged once and tried again
    /// every <see cref="ListenRetryInterval"/>; the first such error is also thrown.
    /// </summary>
    public void Start()
    {
        var httpError = TryStartHttp();
        var udpError = TryStartUdp();
        var retry = $"Trying again every {(int)ListenRetryInterval.TotalSeconds} seconds.";
        if (httpError != null) MachineRelay.AppendAgentLog($"Could not listen on TCP port {Port}: {httpError.Message} {retry}");
        if (udpError != null) MachineRelay.AppendAgentLog($"Could not listen for discovery on UDP port {_discoveryPort}: {udpError.Message} {retry}");
        if (httpError == null && udpError == null) return;
        lock (_listenGate)
        {
            if (!_retrying) _ = Task.Run(RetryListenersAsync);
            _retrying = true;
        }
        throw httpError ?? udpError!;
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
        _http = http;
        _ = Task.Run(AcceptLoopAsync);
        return null;
    }

    private Exception? TryStartUdp()
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort)) { EnableBroadcast = true };
            FleetNetwork.IgnoreConnectionResets(udp);
            _udp = udp;
        }
        catch (Exception ex) { return ex; }
        _ = Task.Run(DiscoveryLoopAsync);
        return null;
    }

    private async Task RetryListenersAsync()
    {
        while (!_cts.IsCancellationRequested && !IsListening)
        {
            try { await Task.Delay(ListenRetryInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
            // Failures were logged once already; only the recovery is.
            if (_http == null && TryStartHttp() == null) MachineRelay.AppendAgentLog($"Listening on TCP port {Port} now.");
            if (_udp == null && TryStartUdp() == null) MachineRelay.AppendAgentLog($"Listening for discovery on UDP port {_discoveryPort} now.");
        }
        lock (_listenGate) _retrying = false;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _http is { IsListening: true } http)
        {
            HttpListenerContext context;
            try { context = await http.GetContextAsync(); }
            catch { if (_cts.IsCancellationRequested) return; await Task.Delay(500); continue; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        var holdsSlot = false;
        try
        {
            var pairingKey = _pairingKey();
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
            var target = context.Request.Headers["X-RW-To"] ?? string.Empty;
            // Authenticate before saying anything about the target, so strangers can't use the answer to guess PC names.
            var plain = FleetCrypto.Open(transportKey, body, FleetCrypto.RequestContext(target, time));
            if (plain == null || !_replay.TryUse(FleetCrypto.NonceOf(body))) { await Reply(response, 403, "denied"); return; }
            _unauthenticated.Release();
            holdsSlot = false;

            // "*" is only accepted for status requests (used to identify a PC added by address).
            if (!target.Equals(_machineKey, StringComparison.OrdinalIgnoreCase) && target != "*")
            { await Reply(response, 403, "wrong PC"); return; }
            var request = JsonSerializer.Deserialize<FleetRequest>(plain) ?? new FleetRequest();
            if (target == "*" && request.Op != "status") { await Reply(response, 403, "wrong PC"); return; }
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

    private async Task DiscoveryLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _udp is { } udp)
        {
            try
            {
                var packet = await udp.ReceiveAsync(_cts.Token);
                var pairingKey = _pairingKey();
                if (pairingKey == null) continue;
                var discoveryKey = FleetNetwork.KeysFor(pairingKey).Discovery;
                var nonce = FleetCrypto.VerifyProbe(discoveryKey, packet.Buffer);
                if (nonce == null) continue;
                var answer = FleetCrypto.Answer(discoveryKey, nonce, _discoveryBody);
                await udp.SendAsync(answer, answer.Length, packet.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { await Task.Delay(200); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _http?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
    }
}
