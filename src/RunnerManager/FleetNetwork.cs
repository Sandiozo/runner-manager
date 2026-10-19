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
    /// <summary>"status", "command" or "log".</summary>
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
    public string CommandId { get; init; } = string.Empty;
}

public sealed record DiscoveredPeer(string Name, string Address, int Protocol, string Version);

/// <summary>Thrown when a PC answers but can't read our messages (different pairing code).</summary>
public sealed class PairingMismatchException(string message) : Exception(message);

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

    // ================= Client (dashboard) =================

    public static async Task<FleetResponse> CallAsync(string address, string targetMachineKey, FleetRequest request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default, byte[]? pairingKey = null)
    {
        pairingKey ??= FleetConfig.Current.FleetKey
            ?? throw new InvalidOperationException("This PC has no pairing code. Set one up in Settings → Fleet.");
        var transportKey = FleetCrypto.TransportKey(pairingKey);
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
        using var response = await Http.SendAsync(message, cts.Token);
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new PairingMismatchException("The pairing code does not match. Use the same pairing code on every PC.");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new PairingMismatchException("The fleet agent on that PC has no pairing code.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The fleet agent returned HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        var plain = FleetCrypto.Open(transportKey, bytes, FleetCrypto.ResponseContext(FleetCrypto.NonceOf(body)))
            ?? throw new PairingMismatchException("Could not verify the response with this PC's pairing code.");
        var result = JsonSerializer.Deserialize<FleetResponse>(plain, Json) ?? throw new InvalidOperationException("The fleet agent returned an empty response.");
        if (!result.Ok) throw new InvalidOperationException(result.Error);
        return result;
    }

    /// <summary>Finds fleet agents on the local network (broadcast) and at known or manually added addresses.</summary>
    public static async Task<List<DiscoveredPeer>> DiscoverAsync(IEnumerable<string> unicastHosts, TimeSpan wait,
        int discoveryPort = DiscoveryPort, byte[]? pairingKey = null)
    {
        var found = new Dictionary<string, DiscoveredPeer>(StringComparer.OrdinalIgnoreCase);
        pairingKey ??= FleetConfig.Current.FleetKey;
        if (pairingKey == null) return found.Values.ToList();
        var discoveryKey = FleetCrypto.DiscoveryKey(pairingKey);
        var probe = FleetCrypto.Probe(discoveryKey, out var nonce);

        using var udp = CreateProbeSocket();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var broadcast in DirectedBroadcastAddresses()) targets.Add(broadcast);
        foreach (var host in unicastHosts)
        {
            try
            {
                var name = host.Split(':')[0];
                foreach (var ip in await Dns.GetHostAddressesAsync(name))
                    if (ip.AddressFamily == AddressFamily.InterNetwork) targets.Add(ip);
            }
            catch { }
        }
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
                        root.TryGetProperty("protocol", out var protocol) ? protocol.GetInt32() : 0,
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

    private static IEnumerable<IPAddress> DirectedBroadcastAddresses()
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
        => address.Contains(':') ? address : $"{address}:{AgentPort}";

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
    private readonly Func<byte[]?> _pairingKey;
    private readonly string _prefix;
    private readonly int _discoveryPort;
    private readonly string _machineKey;
    private readonly string _displayName;
    private readonly ReplayGuard _replay = new();
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _http;
    private UdpClient? _udp;

    /// <param name="prefix">HTTP prefix to listen on (tests use a localhost one; the agent listens on all interfaces).</param>
    public AgentServer(Func<Task<MachineStatusPayload>> status, Func<MachineCommand, string> enqueue, Func<string, Task<LogPayload>> log,
        Func<byte[]?>? pairingKey = null, string? prefix = null, int discoveryPort = FleetNetwork.DiscoveryPort, string? machineKey = null)
    {
        _status = status;
        _enqueue = enqueue;
        _log = log;
        _pairingKey = pairingKey ?? (() => FleetConfig.Current.FleetKey);
        _prefix = prefix ?? $"http://+:{FleetNetwork.AgentPort}/rw/";
        _discoveryPort = discoveryPort;
        // Requests are addressed by the sanitized key; discovery announces the real PC name.
        _displayName = machineKey ?? Environment.MachineName;
        _machineKey = MachineRelay.Sanitize(_displayName);
    }

    public void Start()
    {
        _http = new HttpListener();
        _http.Prefixes.Add(_prefix);
        _http.Start();
        _ = Task.Run(AcceptLoopAsync);

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort)) { EnableBroadcast = true };
        FleetNetwork.IgnoreConnectionResets(_udp);
        _ = Task.Run(DiscoveryLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _http is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _http.GetContextAsync(); }
            catch { if (_cts.IsCancellationRequested) return; await Task.Delay(500); continue; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var pairingKey = _pairingKey();
            if (pairingKey == null) { await Reply(response, 503, "not paired"); return; }
            if (context.Request.HttpMethod != "POST" || context.Request.ContentLength64 > 1024 * 1024)
            { await Reply(response, 400, "bad request"); return; }
            if (!long.TryParse(context.Request.Headers["X-RW-Time"], out var time) || !FleetCrypto.IsFresh(time))
            { await Reply(response, 403, "stale or missing timestamp; check the PC clocks"); return; }

            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            var body = buffer.ToArray();
            var transportKey = FleetCrypto.TransportKey(pairingKey);
            var target = context.Request.Headers["X-RW-To"] ?? string.Empty;
            var me = _machineKey;
            // "*" is only accepted for status requests (used to identify a PC added by address).
            if (!target.Equals(me, StringComparison.OrdinalIgnoreCase) && target != "*")
            { await Reply(response, 403, "wrong PC"); return; }
            var plain = FleetCrypto.Open(transportKey, body, FleetCrypto.RequestContext(target, time));
            if (plain == null || !_replay.TryUse(FleetCrypto.NonceOf(body))) { await Reply(response, 403, "denied"); return; }

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
            MachineRelay.AppendAgentLog($"Request failed: {ex.Message}");
            try { response.Abort(); } catch { }
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
        while (!_cts.IsCancellationRequested && _udp != null)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(_cts.Token);
                var pairingKey = _pairingKey();
                if (pairingKey == null) continue;
                var discoveryKey = FleetCrypto.DiscoveryKey(pairingKey);
                var nonce = FleetCrypto.VerifyProbe(discoveryKey, packet.Buffer);
                if (nonce == null) continue;
                var body = JsonSerializer.Serialize(new
                {
                    name = _displayName,
                    port = new Uri(_prefix.Replace("+", "localhost")).Port,
                    protocol = MachineRelay.ProtocolVersion,
                    version = AppUpdater.CurrentVersion
                });
                var answer = FleetCrypto.Answer(discoveryKey, nonce, body);
                await _udp.SendAsync(answer, answer.Length, packet.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
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
