using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RunnerManager;

/// <summary>
/// Bluetooth-style pairing. A PC already in the fleet "hosts" pairing for a few minutes; a new PC finds it on the
/// network, both run an ECDH key exchange and show the same 6-digit number, and once the user clicks Allow on the host
/// the fleet pairing code is sent across encrypted with the exchanged key. A passive listener learns nothing, and a
/// man in the middle would show different numbers on the two screens.
/// </summary>
public static class Pairing
{
    public const int PairingPort = 47823;
    public const int AnnouncePort = 47824;
    private static readonly byte[] FindMagic = Encoding.ASCII.GetBytes("RWPAIR?1");
    private static readonly byte[] HereMagic = Encoding.ASCII.GetBytes("RWPAIR!1");

    public sealed record Host(string Name, string Address);

    public sealed record JoinResult(byte[] PairingKey, string HostName, string HostAddress);

    /// <summary>The 6-digit number both PCs show; derived from the exchanged secret and both public keys.</summary>
    public static string ComparisonCode(byte[] sharedSecret, byte[] hostPublicKey, byte[] joinerPublicKey)
    {
        var hash = SHA256.HashData(Concat(Encoding.ASCII.GetBytes("rw-pair-code"), sharedSecret, hostPublicKey, joinerPublicKey));
        var value = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return $"{value / 1000:000} {value % 1000:000}";
    }

    public static byte[] TransferKey(byte[] sharedSecret)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: Encoding.ASCII.GetBytes("rw-pair-key"));

    // ================= Joiner (new PC) =================

    /// <summary>PCs on the network that are waiting for a new PC to join.</summary>
    public static async Task<List<Host>> FindHostsAsync(TimeSpan wait, int announcePort = AnnouncePort, IEnumerable<string>? extraHosts = null)
    {
        var found = new Dictionary<string, Host>(StringComparer.OrdinalIgnoreCase);
        using var udp = FleetNetwork.CreateProbeSocket();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var address in FleetNetworkBroadcasts()) targets.Add(address);
        foreach (var host in extraHosts ?? Array.Empty<string>())
        {
            try
            {
                foreach (var ip in await Dns.GetHostAddressesAsync(host.Split(':')[0]))
                    if (ip.AddressFamily == AddressFamily.InterNetwork) targets.Add(ip);
            }
            catch { }
        }
        foreach (var target in targets)
        {
            try { await udp.SendAsync(FindMagic, FindMagic.Length, new IPEndPoint(target, announcePort)); } catch { }
        }
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token); }
                catch (SocketException) { continue; } // an unreachable target must not end the search
                if (packet.Buffer.Length <= HereMagic.Length || !packet.Buffer.AsSpan(0, HereMagic.Length).SequenceEqual(HereMagic)) continue;
                try
                {
                    using var json = JsonDocument.Parse(packet.Buffer.AsMemory(HereMagic.Length));
                    var name = json.RootElement.GetProperty("name").GetString() ?? string.Empty;
                    var port = json.RootElement.GetProperty("port").GetInt32();
                    if (name.Length == 0 || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && announcePort == AnnouncePort) continue;
                    found[name] = new Host(name, $"{packet.RemoteEndPoint.Address}:{port}");
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        return found.Values.OrderBy(h => h.Name).ToList();
    }

    /// <summary>
    /// Connects to a hosting PC, shows the comparison code through <paramref name="showCode"/>, and waits for the
    /// user to allow the join on the host. Returns the fleet's pairing code.
    /// </summary>
    public static async Task<JoinResult> JoinAsync(string address, string myName, Action<string> showCode, CancellationToken cancellationToken)
    {
        var parts = address.Split(':');
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : PairingPort;
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(parts[0], port, connect.Token);
        }
        using var stream = tcp.GetStream();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        await WriteAsync(stream, new PairMessage { Type = "hello", Name = myName, PublicKey = Convert.ToBase64String(myPublic) }, cancellationToken);
        var hello = await ReadAsync(stream, cancellationToken, TimeSpan.FromSeconds(10));
        if (hello.Type != "hello") throw new InvalidOperationException(hello.Error.Length > 0 ? hello.Error : "That PC is not accepting new PCs.");
        var hostPublic = Convert.FromBase64String(hello.PublicKey);
        var shared = DeriveShared(ecdh, hostPublic);
        showCode(ComparisonCode(shared, hostPublic, myPublic));

        // The user now compares the numbers and clicks Allow on the host.
        var decision = await ReadAsync(stream, cancellationToken, TimeSpan.FromMinutes(3));
        if (decision.Type != "allow") throw new InvalidOperationException(decision.Error.Length > 0 ? decision.Error : "The other PC did not allow this PC to join.");
        var key = FleetCrypto.Open(TransferKey(shared), Convert.FromBase64String(decision.Payload), "rw-pair-transfer")
                  ?? throw new CryptographicException("Could not decrypt the pairing code.");
        if (key.Length != 32) throw new CryptographicException("Received an invalid pairing code.");
        var remote = (IPEndPoint?)tcp.Client.RemoteEndPoint;
        return new JoinResult(key, hello.Name, remote == null ? parts[0] : $"{remote.Address}:{FleetNetwork.AgentPort}");
    }

    // ================= Host (PC already in the fleet) =================

    public sealed class JoinRequest
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        internal Task<bool> Decision => _decision.Task;
        public void Allow() => _decision.TrySetResult(true);
        public void Deny() => _decision.TrySetResult(false);
    }

    /// <summary>Accepts join requests while open: answers find-probes on UDP and runs the exchange on TCP.</summary>
    public sealed class PairingHost : IDisposable
    {
        private readonly byte[] _pairingKey;
        private readonly string _name;
        private readonly int _pairingPort;
        private readonly int _announcePort;
        private readonly CancellationTokenSource _cts = new();
        private TcpListener? _tcp;
        private UdpClient? _udp;

        /// <summary>Raised on a background thread; call Allow() or Deny() on the request.</summary>
        public event Action<JoinRequest>? JoinRequested;
        public event Action<string>? Joined;

        public PairingHost(byte[] pairingKey, string name, int pairingPort = PairingPort, int announcePort = AnnouncePort)
        {
            _pairingKey = pairingKey;
            _name = name;
            _pairingPort = pairingPort;
            _announcePort = announcePort;
        }

        public void Start()
        {
            _tcp = new TcpListener(IPAddress.Any, _pairingPort);
            _tcp.Start();
            _ = Task.Run(AcceptLoopAsync);
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _announcePort)) { EnableBroadcast = true };
            FleetNetwork.IgnoreConnectionResets(_udp);
            _ = Task.Run(AnnounceLoopAsync);
        }

        private async Task AnnounceLoopAsync()
        {
            var answer = Concat(HereMagic, JsonSerializer.SerializeToUtf8Bytes(new { name = _name, port = _pairingPort }));
            while (!_cts.IsCancellationRequested && _udp != null)
            {
                try
                {
                    var packet = await _udp.ReceiveAsync(_cts.Token);
                    if (packet.Buffer.AsSpan().SequenceEqual(FindMagic))
                        await _udp.SendAsync(answer, answer.Length, packet.RemoteEndPoint);
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(100); }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested && _tcp != null)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    var hello = await ReadAsync(stream, _cts.Token, TimeSpan.FromSeconds(10));
                    if (hello.Type != "hello" || hello.PublicKey.Length == 0) return;
                    using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
                    var joinerPublic = Convert.FromBase64String(hello.PublicKey);
                    var shared = DeriveShared(ecdh, joinerPublic);
                    await WriteAsync(stream, new PairMessage { Type = "hello", Name = _name, PublicKey = Convert.ToBase64String(myPublic) }, _cts.Token);

                    var request = new JoinRequest
                    {
                        Name = string.IsNullOrWhiteSpace(hello.Name) ? "A PC" : hello.Name.Trim(),
                        Code = ComparisonCode(shared, myPublic, joinerPublic),
                        Address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? string.Empty
                    };
                    JoinRequested?.Invoke(request);
                    var allowed = await request.Decision.WaitAsync(TimeSpan.FromMinutes(3), _cts.Token);
                    if (!allowed)
                    {
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} did not allow this PC to join." }, _cts.Token);
                        return;
                    }
                    var payload = FleetCrypto.Seal(TransferKey(shared), _pairingKey, "rw-pair-transfer");
                    await WriteAsync(stream, new PairMessage { Type = "allow", Name = _name, Payload = Convert.ToBase64String(payload) }, _cts.Token);
                    Joined?.Invoke(request.Name);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
        }
    }

    // ================= Plumbing =================

    private sealed record PairMessage
    {
        public string Type { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string PublicKey { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }

    private static byte[] DeriveShared(ECDiffieHellman mine, byte[] otherPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
    }

    private static async Task WriteAsync(Stream stream, PairMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var length = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<PairMessage> ReadAsync(Stream stream, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cts.Token);
        var size = BitConverter.ToInt32(length);
        if (size is <= 0 or > 64 * 1024) throw new InvalidDataException("Invalid pairing message.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, cts.Token);
        return JsonSerializer.Deserialize<PairMessage>(body) ?? throw new InvalidDataException("Empty pairing message.");
    }

    private static IEnumerable<IPAddress> FleetNetworkBroadcasts()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
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

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
