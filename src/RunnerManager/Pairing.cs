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
/// <remarks>
/// As in Bluetooth numeric comparison, the joiner first sends only a hash of its public key and a random nonce, then
/// the host sends its public key, and only then does the joiner reveal its key. Neither side can choose its key after
/// seeing the other's, so a man in the middle can't search for keys that make the two numbers match.
/// </remarks>
public static class Pairing
{
    public const int PairingPort = 47823;
    public const int AnnouncePort = 47824;
    /// <summary>2: the joiner commits to its key first. PCs that pair with version 1 are told to update.</summary>
    public const int Version = 2;
    private static readonly byte[] FindMagic = Encoding.ASCII.GetBytes("RWPAIR?2");
    private static readonly byte[] HereMagic = Encoding.ASCII.GetBytes("RWPAIR!2");
    // Version 1 probes and answers, only so that older PCs can be told to update.
    private static readonly byte[] FindMagicV1 = Encoding.ASCII.GetBytes("RWPAIR?1");
    private static readonly byte[] HereMagicV1 = Encoding.ASCII.GetBytes("RWPAIR!1");
    /// <summary>Connections a host handles at once; more are turned away until one is answered.</summary>
    internal const int MaxConnections = 4;
    /// <summary>How long a host waits for the user to allow or deny a request.</summary>
    internal static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    public sealed record Host(string Name, string Address, int Version = Version)
    {
        public bool IsOutdated => Version < Pairing.Version;
        public string Detail => IsOutdated ? $"{Address}  ·  older Runner Manager, update it first" : Address;
    }

    public sealed record JoinResult(byte[] PairingKey, string HostName, string HostAddress);

    /// <summary>The 6-digit number both PCs show; derived from the exchanged secret and both public keys.</summary>
    public static string ComparisonCode(byte[] sharedSecret, byte[] hostPublicKey, byte[] joinerPublicKey)
    {
        var hash = SHA256.HashData(FleetCrypto.Concat(Encoding.ASCII.GetBytes("rw-pair-code"), sharedSecret, hostPublicKey, joinerPublicKey));
        var value = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        return $"{value / 1000:000} {value % 1000:000}";
    }

    public static byte[] TransferKey(byte[] sharedSecret)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, info: Encoding.ASCII.GetBytes("rw-pair-key"));

    /// <summary>What the joiner sends before it reveals its public key.</summary>
    internal static byte[] Commitment(byte[] publicKey, byte[] nonce)
        => SHA256.HashData(FleetCrypto.Concat(Encoding.ASCII.GetBytes("rw-pair-commit"), nonce, publicKey));

    private static string UpdateMessage(string pc) => $"{pc} runs an older Runner Manager. Update Runner Manager on {pc}, then try again.";

    // ================= Joiner (new PC) =================

    /// <summary>PCs on the network that are waiting for a new PC to join.</summary>
    public static async Task<List<Host>> FindHostsAsync(TimeSpan wait, int announcePort = AnnouncePort, IEnumerable<string>? extraHosts = null)
    {
        var found = new Dictionary<string, Host>(StringComparer.OrdinalIgnoreCase);
        using var udp = FleetNetwork.CreateProbeSocket();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var address in FleetNetwork.DirectedBroadcastAddresses()) targets.Add(address);
        foreach (var ip in await FleetNetwork.ResolveIPv4Async(extraHosts ?? Array.Empty<string>())) targets.Add(ip);
        foreach (var target in targets)
        {
            // The version 1 probe finds older hosts too, so the list can say they need an update.
            foreach (var probe in new[] { FindMagic, FindMagicV1 })
                try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, announcePort)); } catch { }
        }
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token); }
                catch (SocketException) { continue; } // an unreachable target must not end the search
                var version = StartsWith(packet.Buffer, HereMagic) ? Version : StartsWith(packet.Buffer, HereMagicV1) ? 1 : 0;
                if (version == 0) continue;
                try
                {
                    using var json = JsonDocument.Parse(packet.Buffer.AsMemory(HereMagic.Length));
                    var name = json.RootElement.GetProperty("name").GetString() ?? string.Empty;
                    var port = json.RootElement.GetProperty("port").GetInt32();
                    if (name.Length == 0 || name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && announcePort == AnnouncePort) continue;
                    // A current host answers both probes; keep its newest answer.
                    if (found.TryGetValue(name, out var known) && known.Version >= version) continue;
                    found[name] = new Host(name, FleetNetwork.JoinHostPort(packet.RemoteEndPoint.Address.ToString(), port), version);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        return found.Values.OrderBy(h => h.Name).ToList();
    }

    private static bool StartsWith(byte[] packet, byte[] magic)
        => packet.Length > magic.Length && packet.AsSpan(0, magic.Length).SequenceEqual(magic);

    /// <summary>
    /// Connects to a hosting PC, shows the comparison code through <paramref name="showCode"/>, and waits for the
    /// user to allow the join on the host. Returns the fleet's pairing code.
    /// </summary>
    public static async Task<JoinResult> JoinAsync(string address, string myName, Action<string> showCode, CancellationToken cancellationToken)
    {
        var (host, typedPort) = FleetNetwork.SplitHostPort(address);
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(host, typedPort ?? PairingPort, connect.Token);
        }
        using var stream = tcp.GetStream();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(32);

        // 1. Commit to our key without showing it.
        await WriteAsync(stream, new PairMessage
        {
            Type = "commit", Version = Version, Name = myName, Commit = Convert.ToBase64String(Commitment(myPublic, nonce))
        }, cancellationToken);
        PairMessage hello;
        // A version 1 host does not know "commit" and closes the connection without an answer.
        try { hello = await ReadAsync(stream, cancellationToken, StepTimeout); }
        catch (IOException) { throw new InvalidOperationException($"{host} closed the connection. If it runs an older Runner Manager, update it there, then try again."); }
        if (hello.Type != "hello") throw new InvalidOperationException(hello.Error.Length > 0 ? hello.Error : "That PC is not accepting new PCs.");
        if (hello.Version < Version) throw new InvalidOperationException(UpdateMessage(hello.Name.Length > 0 ? hello.Name : host));
        var hostPublic = Convert.FromBase64String(hello.PublicKey);

        // 2. Now that the host's key is fixed, reveal ours.
        await WriteAsync(stream, new PairMessage
        {
            Type = "reveal", PublicKey = Convert.ToBase64String(myPublic), Nonce = Convert.ToBase64String(nonce)
        }, cancellationToken);
        var shared = DeriveShared(ecdh, hostPublic);
        showCode(ComparisonCode(shared, hostPublic, myPublic));

        // The user now compares the numbers and clicks Allow on the host.
        var decision = await ReadAsync(stream, cancellationToken, DecisionTimeout + TimeSpan.FromSeconds(15));
        if (decision.Type != "allow") throw new InvalidOperationException(decision.Error.Length > 0 ? decision.Error : "The other PC did not allow this PC to join.");
        var key = FleetCrypto.Open(TransferKey(shared), Convert.FromBase64String(decision.Payload), "rw-pair-transfer")
                  ?? throw new CryptographicException("Could not decrypt the pairing code.");
        if (key.Length != 32) throw new CryptographicException("Received an invalid pairing code.");
        return new JoinResult(key, hello.Name, FleetNetwork.JoinHostPort(AgentHost(tcp, host), FleetNetwork.AgentPort));
    }

    /// <summary>The host's IPv4 address as connected, or the name or address the user typed otherwise.</summary>
    private static string AgentHost(TcpClient tcp, string typedHost)
    {
        if (tcp.Client.RemoteEndPoint is not IPEndPoint remote) return typedHost;
        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        return address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : typedHost;
    }

    // ================= Host (PC already in the fleet) =================

    public sealed class JoinRequest
    {
        private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        internal Task<bool> Decision => _decision.Task;
        /// <summary>Completes when the host no longer waits for an answer: it was answered, timed out, or the other PC left.</summary>
        public Task Ended => _ended.Task;
        public void Allow() => _decision.TrySetResult(true);
        public void Deny() => _decision.TrySetResult(false);
        internal void End() => _ended.TrySetResult();
    }

    /// <summary>Accepts join requests while open: answers find-probes on UDP and runs the exchange on TCP.</summary>
    public sealed class PairingHost : IDisposable
    {
        private readonly byte[] _pairingKey;
        private readonly string _name;
        private readonly int _pairingPort;
        private readonly int _announcePort;
        // Listening ends first; open connections keep _cts until StopAsync has told them no.
        private readonly CancellationTokenSource _listen = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly HashSet<JoinRequest> _open = new();
        private readonly HashSet<Task> _handlers = new();
        private int _connections;
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
            var body = JsonSerializer.SerializeToUtf8Bytes(new { name = _name, port = _pairingPort, version = Version });
            var answer = FleetCrypto.Concat(HereMagic, body);
            // Older joiners are answered too: they connect, and are told to update.
            var answerV1 = FleetCrypto.Concat(HereMagicV1, body);
            while (!_listen.IsCancellationRequested && _udp is { } udp)
            {
                try
                {
                    var packet = await udp.ReceiveAsync(_listen.Token);
                    if (packet.Buffer.AsSpan().SequenceEqual(FindMagic))
                        await udp.SendAsync(answer, answer.Length, packet.RemoteEndPoint);
                    else if (packet.Buffer.AsSpan().SequenceEqual(FindMagicV1))
                        await udp.SendAsync(answerV1, answerV1.Length, packet.RemoteEndPoint);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch { await Task.Delay(100); }
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_listen.IsCancellationRequested && _tcp != null)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(_listen.Token); }
                catch { return; }
                var handler = Task.Run(() => HandleAsync(client));
                lock (_handlers) _handlers.Add(handler);
                _ = handler.ContinueWith(t => { lock (_handlers) _handlers.Remove(t); }, TaskScheduler.Default);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            JoinRequest? request = null;
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    if (Interlocked.Increment(ref _connections) > MaxConnections)
                    {
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} is busy with other PCs. Try again in a minute." }, _cts.Token);
                        return;
                    }
                    var commit = await ReadAsync(stream, _cts.Token, StepTimeout);
                    if (commit.Type == "hello")
                    {
                        // A version 1 joiner: it shows this message.
                        await WriteAsync(stream, new PairMessage
                        {
                            Type = "deny",
                            Error = $"{_name} runs a newer Runner Manager. Update Runner Manager on this PC, then try again."
                        }, _cts.Token);
                        return;
                    }
                    if (commit.Type != "commit") return;
                    var commitment = Convert.FromBase64String(commit.Commit);
                    if (commitment.Length != 32) return;

                    using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
                    await WriteAsync(stream, new PairMessage { Type = "hello", Version = Version, Name = _name, PublicKey = Convert.ToBase64String(myPublic) }, _cts.Token);

                    var reveal = await ReadAsync(stream, _cts.Token, StepTimeout);
                    if (reveal.Type != "reveal") return;
                    var joinerPublic = Convert.FromBase64String(reveal.PublicKey);
                    var nonce = Convert.FromBase64String(reveal.Nonce);
                    if (!CryptographicOperations.FixedTimeEquals(Commitment(joinerPublic, nonce), commitment))
                    {
                        // The key differs from the one the joiner committed to: someone is in between, or it's broken.
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = "The pairing check failed. Try again." }, _cts.Token);
                        return;
                    }
                    var shared = DeriveShared(ecdh, joinerPublic);

                    request = new JoinRequest
                    {
                        Name = string.IsNullOrWhiteSpace(commit.Name) ? "A PC" : commit.Name.Trim(),
                        Code = ComparisonCode(shared, myPublic, joinerPublic),
                        Address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? string.Empty
                    };
                    lock (_open) _open.Add(request);
                    if (_listen.IsCancellationRequested) request.Deny();
                    JoinRequested?.Invoke(request);

                    // The joiner sends nothing more, so a finished read means it went away.
                    var gone = stream.ReadAsync(new byte[1], _cts.Token).AsTask();
                    _ = gone.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    var finished = await Task.WhenAny(request.Decision, gone, Task.Delay(DecisionTimeout, _cts.Token));
                    if (finished == gone) return;
                    if (finished != request.Decision)
                    {
                        request.End();
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} did not answer in time." }, _cts.Token);
                        return;
                    }
                    if (!await request.Decision)
                    {
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} did not allow this PC to join." }, _cts.Token);
                        return;
                    }
                    var payload = FleetCrypto.Seal(TransferKey(shared), _pairingKey, "rw-pair-transfer");
                    await WriteAsync(stream, new PairMessage { Type = "allow", Name = _name, Payload = Convert.ToBase64String(payload) }, _cts.Token);
                    Joined?.Invoke(request.Name);
                }
                catch { }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                    if (request != null)
                    {
                        lock (_open) _open.Remove(request);
                        request.End();
                    }
                }
            }
        }

        /// <summary>
        /// Stops accepting PCs. Requests still waiting are denied, and the other PCs are told so before their
        /// connections close.
        /// </summary>
        public async Task StopAsync()
        {
            _listen.Cancel();
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
            lock (_open) foreach (var request in _open) request.Deny();
            Task[] handlers;
            lock (_handlers) handlers = _handlers.ToArray();
            try { await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Dispose();
        }

        public void Dispose()
        {
            _listen.Cancel();
            _cts.Cancel();
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
        }
    }

    // ================= Plumbing =================

    internal sealed record PairMessage
    {
        public string Type { get; init; } = string.Empty;
        /// <summary>Pairing version (2 and later; missing means 1).</summary>
        public int Version { get; init; } = 1;
        public string Name { get; init; } = string.Empty;
        public string PublicKey { get; init; } = string.Empty;
        /// <summary>The joiner's commitment: SHA-256 of its nonce and public key.</summary>
        public string Commit { get; init; } = string.Empty;
        public string Nonce { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }

    private static byte[] DeriveShared(ECDiffieHellman mine, byte[] otherPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
    }

    internal static async Task WriteAsync(Stream stream, PairMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var length = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<PairMessage> ReadAsync(Stream stream, CancellationToken cancellationToken, TimeSpan timeout)
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
}
