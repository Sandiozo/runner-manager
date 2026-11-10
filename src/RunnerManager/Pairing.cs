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
/// the fleet code is sent across encrypted with the exchanged key. The new PC accepts the code only after its
/// user has confirmed the number too, and the host adds the new PC only when it hears that. A passive listener learns
/// nothing, and a man in the middle would show different numbers on the two screens.
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
    /// <summary>How long a host waits for the user of the new PC to confirm the codes there.</summary>
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
    /// <summary>
    /// How long a new PC waits for "done" after its "confirmed". Longer than a step: the host saves the PC before it
    /// answers, and may first wait for its settings file, for up to 10 seconds.
    /// </summary>
    internal static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(30);

    public sealed record Host(string Name, string Address, int Version = Version)
    {
        public bool IsOutdated => Version < Pairing.Version;
        public string Detail => IsOutdated ? $"{Address}  ·  older Runner Manager, update it first" : Address;
    }

    /// <param name="Managed">
    /// The host allowed this PC one way: <paramref name="PairingKey"/> is this PC's own code, not the fleet's, so this PC
    /// can be managed by the fleet but can't reach its PCs.
    /// </param>
    public sealed record JoinResult(byte[] PairingKey, string HostName, string HostAddress, bool Managed = false);

    /// <summary>What a host has allowed, shown to the user of this PC before the code is accepted. It does not hold the code.</summary>
    public sealed record JoinOffer(string HostName, bool Managed);

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

    /// <summary>
    /// What a joiner can do. It sends them as one byte after the random bytes of its nonce: the commitment covers the
    /// nonce, so a device on the path cannot change or remove them, and a host that does not know them (before 2.9)
    /// takes the longer nonce as it is. A joiner that sends none is from before 2.9 too.
    /// </summary>
    [Flags]
    internal enum JoinerFeatures : byte
    {
        None = 0,
        /// <summary>It expects the host's name and the one-way flag to be sealed with the code (see <see cref="AllowContext"/>).</summary>
        SealedAllow = 1,
        /// <summary>It can be allowed one way. Older joiners say so only in "commit", where it is not protected.</summary>
        CanBeManaged = 2,
        /// <summary>
        /// After "allow" it sends "confirmed" once its user has confirmed the codes, and keeps the code only when the
        /// host has answered "done". So the host adds it only then, and under the name that "confirmed" proves (see
        /// <see cref="ConfirmedContext"/>). Needs <see cref="SealedAllow"/>: the seal is how the host says that it waits.
        /// </summary>
        Confirms = 4
    }

    internal const int NonceRandomBytes = 32;

    internal static byte[] NewNonce(JoinerFeatures features)
        => FleetCrypto.Concat(RandomNumberGenerator.GetBytes(NonceRandomBytes), new[] { (byte)features });

    /// <summary>The features in a joiner's nonce, or null from a joiner that sends none.</summary>
    internal static JoinerFeatures? FeaturesOf(byte[] nonce)
        => nonce.Length > NonceRandomBytes ? (JoinerFeatures)nonce[NonceRandomBytes] : null;

    /// <summary>What the code in "allow" was sealed with before the host's name and the one-way flag were sealed with it.</summary>
    internal const string TransferContext = "rw-pair-transfer";

    /// <summary>
    /// What the code in "allow" is sealed with for a joiner with <see cref="JoinerFeatures.SealedAllow"/>: the code opens
    /// only together with the host's name and the one-way flag as the host sent them. With <paramref name="confirms"/>
    /// the host also says that it waits for "confirmed" (see <see cref="JoinerFeatures.Confirms"/>).
    /// </summary>
    internal static string AllowContext(string hostName, bool managed, bool confirms = false)
        => $"rw-pair-allow{(confirms ? "-confirm" : string.Empty)}\n{(managed ? "managed" : "member")}\n{hostName}";

    /// <summary>
    /// What the proof in "confirmed" is sealed with. It holds the joiner's name as the joiner sent it: the name in
    /// "commit" is not protected, and the host saves the PC under the name it got. So a name that was changed on the
    /// path does not get confirmed.
    /// </summary>
    internal static string ConfirmedContext(string joinerName) => $"rw-pair-confirmed\n{joinerName}";

    internal const string DoneContext = "rw-pair-done";

    /// <summary>
    /// What "confirmed" and "done" carry. Only the two PCs of the exchange can seal it, so a device on the path can
    /// answer for neither of them.
    /// </summary>
    internal static string Proof(byte[] transferKey, string context)
        => Convert.ToBase64String(FleetCrypto.Seal(transferKey, Array.Empty<byte>(), context));

    private static bool IsProof(byte[] transferKey, string payload, string context)
    {
        try { return FleetCrypto.Open(transferKey, Convert.FromBase64String(payload), context) != null; }
        catch (FormatException) { return false; }
    }

    private const string NotConfirmed = "The codes were not confirmed on this PC.";

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
    /// user to allow the join on the host and then for <paramref name="confirm"/>: the user of this PC saying that both
    /// PCs show the same code. Returns the fleet code, or this PC's own code when it is managed one way.
    /// </summary>
    /// <remarks>
    /// Hosts are found by unauthenticated UDP, so the PC that answered may not be the one the user means, and that PC
    /// alone decides when to allow. Without the confirmation here, any device on the network could hand this PC a code
    /// of its choice. A host that waits for the confirmation is told the answer, and adds this PC only after a yes.
    /// </remarks>
    public static async Task<JoinResult> JoinAsync(string address, string myName, Action<string> showCode,
        Func<JoinOffer, Task<bool>> confirm, CancellationToken cancellationToken)
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
        var nonce = NewNonce(JoinerFeatures.SealedAllow | JoinerFeatures.CanBeManaged | JoinerFeatures.Confirms);

        // 1. Commit to our key without showing it.
        await WriteAsync(stream, new PairMessage
        {
            Type = "commit", Version = Version, Name = myName, Commit = Convert.ToBase64String(Commitment(myPublic, nonce)), CanBeManaged = true
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
        var transferKey = TransferKey(shared);
        var payload = Convert.FromBase64String(decision.Payload);
        // A host that knows our features seals its name and the one-way flag with the code, and never seals the code
        // alone for us. So when the code opens this way, both are as the host sent them, and when it opens the older
        // way, the host is an older one, which protects neither. The seal also says whether the host waits to hear
        // that the codes were confirmed here.
        var hostWaits = true;
        var sealedAllow = FleetCrypto.Open(transferKey, payload, AllowContext(decision.Name, decision.Managed, confirms: true));
        if (sealedAllow == null)
        {
            hostWaits = false;
            sealedAllow = FleetCrypto.Open(transferKey, payload, AllowContext(decision.Name, decision.Managed));
        }
        // A PC that is managed one way gets a code of its own, which is not the fleet code.
        var code = decision.Managed ? "pairing code" : "fleet code";
        var key = sealedAllow ?? FleetCrypto.Open(transferKey, payload, TransferContext)
                  ?? throw new CryptographicException($"Could not decrypt the {code}.");
        if (key.Length != 32) throw new CryptographicException($"Received an invalid {code}.");
        var result = new JoinResult(key, sealedAllow != null ? decision.Name : hello.Name,
            FleetNetwork.JoinHostPort(AgentHost(tcp, host), FleetNetwork.AgentPort), decision.Managed);
        try
        {
            var offer = new JoinOffer(result.HostName, result.Managed);
            if (hostWaits) await ConfirmWithHostAsync(stream, transferKey, myName, offer, confirm, cancellationToken);
            else
            {
                // An older host: it has added this PC already, and hears nothing more.
                if (!await confirm(offer).WaitAsync(cancellationToken)) throw new OperationCanceledException(NotConfirmed);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result;
        }
        catch
        {
            // Not confirmed: the code must not stay around.
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    /// <summary>As if the user of this PC confirmed at once, for tests of the exchange itself.</summary>
    internal static Task<JoinResult> JoinAsync(string address, string myName, Action<string> showCode, CancellationToken cancellationToken)
        => JoinAsync(address, myName, showCode, _ => Task.FromResult(true), cancellationToken);

    /// <summary>
    /// Asks the user of this PC while the host waits for the answer. The host adds this PC only when it hears
    /// "confirmed", and this PC keeps the code only when the host has answered "done": so both have the pairing, or
    /// neither. Leaving without an answer closes the connection, which the host takes as a no.
    /// </summary>
    /// <remarks>
    /// After a yes there is no leaving: the host may have added this PC already, so <paramref name="cancellationToken"/>
    /// no longer ends the wait for "done". That wait ends by itself after <see cref="DoneTimeout"/>.
    /// </remarks>
    private static async Task ConfirmWithHostAsync(Stream stream, byte[] transferKey, string myName, JoinOffer offer,
        Func<JoinOffer, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        var stopped = $"{offer.HostName} stopped waiting before the codes were confirmed on this PC. This PC was not added. " +
                      $"On {offer.HostName}, click “Add a PC” again, then try again.";
        // The host may stop waiting while the user reads: it then says why, or the connection ends.
        var hostSays = ReadAsync(stream, CancellationToken.None, Timeout.InfiniteTimeSpan);
        _ = hostSays.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var confirmed = false;
        try
        {
            var answer = confirm(offer);
            await Task.WhenAny(answer, hostSays).WaitAsync(cancellationToken);
            if (!answer.IsCompleted)
            {
                var left = await hostSays;
                throw new InvalidOperationException(left.Error.Length > 0 ? left.Error : stopped);
            }
            if (!await answer) throw new OperationCanceledException(NotConfirmed);
            cancellationToken.ThrowIfCancellationRequested();
            confirmed = true;
            await WriteAsync(stream, new PairMessage { Type = "confirmed", Payload = Proof(transferKey, ConfirmedContext(myName)) }, CancellationToken.None);
            var done = await hostSays.WaitAsync(DoneTimeout);
            if (done.Type != "done") throw new InvalidOperationException(done.Error.Length > 0 ? done.Error : stopped);
            if (!IsProof(transferKey, done.Payload, DoneContext)) throw new CryptographicException("The pairing check failed. Try again.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidDataException or JsonException)
        {
            // After the yes it is not known whether the host added this PC. Joining again sets both right.
            throw new InvalidOperationException(confirmed
                ? $"{offer.HostName} did not answer after the codes were confirmed. This PC has not joined. Try again."
                : stopped, ex);
        }
    }

    /// <summary>The host's IPv4 address as connected, or the name or address the user typed otherwise.</summary>
    private static string AgentHost(TcpClient tcp, string typedHost)
    {
        if (tcp.Client.RemoteEndPoint is not IPEndPoint remote) return typedHost;
        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        return address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : typedHost;
    }

    // ================= Host (PC already in the fleet) =================

    /// <summary>The host's answer: deny, allow into the fleet, or allow one way with the PC's own code.</summary>
    internal sealed record Decision(bool Allowed, byte[]? ManagedKey = null);

    public sealed class JoinRequest
    {
        private readonly TaskCompletionSource<Decision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _codeSent;
        private volatile bool _checkFailed;
        /// <summary>
        /// The joiner's name as it arrived in "commit", which is not protected. A joiner that <see cref="Confirms"/>
        /// is added only when its "confirmed" proves that it sent this name.
        /// </summary>
        public string Name { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        /// <summary>The joiner can be allowed one way (2.8 and later). Older PCs only join as fleet members.</summary>
        public bool CanBeManaged { get; init; }
        /// <summary>
        /// The joiner says when its user has confirmed the codes, and keeps its code only then. An older PC (before
        /// 2.9) keeps its code as soon as it arrives, and says nothing more.
        /// </summary>
        public bool Confirms { get; init; }
        /// <summary>The joiner was sent its code. One that <see cref="Confirms"/> has joined only at <see cref="PairingHost.Joined"/>.</summary>
        public bool CodeSent => _codeSent;
        /// <summary>
        /// The joiner's "confirmed" did not pass the check: it was not made by the PC of this exchange, or that PC
        /// sent another name than <see cref="Name"/>. The PC was not added.
        /// </summary>
        public bool CheckFailed => _checkFailed;
        internal Task<Decision> Answer => _decision.Task;
        /// <summary>
        /// Completes when the host is done with the request: it was denied or timed out, the other PC left, or the
        /// other PC has its code and, when it <see cref="Confirms"/>, has said yes or no.
        /// </summary>
        public Task Ended => _ended.Task;
        public void Allow() => _decision.TrySetResult(new Decision(true));
        /// <summary>Allows the PC one way: it gets <paramref name="key"/>, its own code, instead of the fleet's.</summary>
        public void AllowManaged(byte[] key) => _decision.TrySetResult(new Decision(true, key));
        public void Deny() => _decision.TrySetResult(new Decision(false));
        internal void MarkCodeSent() => _codeSent = true;
        internal void MarkCheckFailed() => _checkFailed = true;
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
        private readonly object _gate = new();
        private int _connections;
        private TcpListener? _tcp;
        private UdpClient? _udp;
        private bool _stopped;

        /// <summary>Raised on a background thread; call Allow() or Deny() on the request.</summary>
        public event Action<JoinRequest>? JoinRequested;
        /// <summary>
        /// Raised on a background thread once a PC that confirms (<see cref="JoinRequest.Confirms"/>) has its code,
        /// while its user is asked whether the codes match. <see cref="Joined"/> follows, or the request ends without it.
        /// </summary>
        public event Action<JoinRequest>? Confirming;
        /// <summary>
        /// Raised on a background thread once a PC has joined: it has its code, and its user has confirmed the codes
        /// when the PC can say so. Such a PC waits for the handlers. When one throws, the PC is told that it was not
        /// added, and it drops its code.
        /// </summary>
        public event Action<JoinRequest>? Joined;

        /// <summary>How long this host waits for the user of the new PC to confirm the codes there.</summary>
        internal TimeSpan ConfirmTimeout { get; init; } = Pairing.ConfirmTimeout;

        public PairingHost(byte[] pairingKey, string name, int pairingPort = PairingPort, int announcePort = AnnouncePort)
        {
            _pairingKey = pairingKey;
            _name = name;
            _pairingPort = pairingPort;
            _announcePort = announcePort;
        }

        /// <summary>Starts listening. Throws once the host has been stopped: nothing would be left to free the ports.</summary>
        public void Start()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                try
                {
                    _tcp = new TcpListener(IPAddress.Any, _pairingPort);
                    _tcp.Start();
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _announcePort)) { EnableBroadcast = true };
                    FleetNetwork.IgnoreConnectionResets(_udp);
                }
                catch
                {
                    // One port is taken: do not keep the other.
                    CloseSockets();
                    throw;
                }
            }
            _ = Task.Run(AcceptLoopAsync);
            _ = Task.Run(AnnounceLoopAsync);
        }

        private void CloseSockets()
        {
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
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
                    // Now that the nonce is checked, the features in it are the joiner's own.
                    var features = FeaturesOf(nonce);
                    var sealedAllow = features?.HasFlag(JoinerFeatures.SealedAllow) == true;
                    // Only an "allow" sealed that way can say that this PC waits for the joiner's answer.
                    var confirms = sealedAllow && features?.HasFlag(JoinerFeatures.Confirms) == true;

                    request = new JoinRequest
                    {
                        Name = string.IsNullOrWhiteSpace(commit.Name) ? "A PC" : commit.Name.Trim(),
                        Code = ComparisonCode(shared, myPublic, joinerPublic),
                        Address = RemoteAddress(client),
                        CanBeManaged = features?.HasFlag(JoinerFeatures.CanBeManaged) ?? commit.CanBeManaged,
                        Confirms = confirms
                    };
                    lock (_open) _open.Add(request);
                    if (_listen.IsCancellationRequested) request.Deny();
                    JoinRequested?.Invoke(request);

                    // The joiner sends nothing before it is answered, so a finished read until then means it went away.
                    var first = new byte[1];
                    var gone = stream.ReadAsync(first, _cts.Token).AsTask();
                    _ = gone.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    var finished = await Task.WhenAny(request.Answer, gone, Task.Delay(DecisionTimeout, _cts.Token));
                    if (finished == gone) return;
                    if (finished != request.Answer)
                    {
                        request.End();
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} did not answer in time." }, _cts.Token);
                        return;
                    }
                    var decision = await request.Answer;
                    if (!decision.Allowed)
                    {
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = $"{_name} did not allow this PC to join." }, _cts.Token);
                        return;
                    }
                    var managed = decision.ManagedKey != null;
                    var transferKey = TransferKey(shared);
                    // The name and the flag also go outside the seal: an older joiner reads them only there, unprotected.
                    // A joiner that expects them sealed gets the code sealed with them, and no other way.
                    var context = sealedAllow ? AllowContext(_name, managed, confirms) : TransferContext;
                    var payload = FleetCrypto.Seal(transferKey, decision.ManagedKey ?? _pairingKey, context);
                    await WriteAsync(stream, new PairMessage
                    {
                        Type = "allow", Name = _name, Payload = Convert.ToBase64String(payload), Managed = managed
                    }, _cts.Token);
                    request.MarkCodeSent();
                    if (!confirms)
                    {
                        // An older joiner has kept the code by now, and says nothing more.
                        Joined?.Invoke(request);
                        return;
                    }

                    Confirming?.Invoke(request);
                    var refusal = await AwaitConfirmedAsync(stream, gone, first, transferKey, request, commit.Name);
                    if (refusal == null)
                    {
                        // The joiner keeps its code only once it hears "done", so it also hears when it could not be added.
                        try { Joined?.Invoke(request); }
                        catch { refusal = $"{_name} could not save this PC, so it was not added. Try again."; }
                    }
                    if (refusal == null)
                        await WriteAsync(stream, new PairMessage { Type = "done", Payload = Proof(transferKey, DoneContext) }, _cts.Token);
                    else if (refusal.Length > 0)
                        await WriteAsync(stream, new PairMessage { Type = "deny", Error = refusal }, _cts.Token);
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
        /// Waits for the joiner to say that its user confirmed the codes. Returns null when it did, and otherwise what
        /// to tell it: nothing when it went away, which is how it says no.
        /// </summary>
        /// <param name="next">The read of the first byte of what the joiner sends next, into <paramref name="first"/>.</param>
        /// <param name="sentName">The joiner's name exactly as it arrived in "commit".</param>
        private async Task<string?> AwaitConfirmedAsync(Stream stream, Task<int> next, byte[] first, byte[] transferKey,
            JoinRequest request, string sentName)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_listen.Token);
            try
            {
                if (await Task.WhenAny(next, Task.Delay(ConfirmTimeout, waiting.Token)) != next)
                    return _listen.IsCancellationRequested
                        ? $"{_name} closed “Add a PC” before the codes were confirmed on this PC. This PC was not added."
                        : $"{_name} did not get the confirmation in time. This PC was not added. Try again.";
            }
            finally { waiting.Cancel(); }
            if (await next == 0) return string.Empty;
            var message = await ReadAsync(stream, _cts.Token, StepTimeout, first[0]);
            // The proof holds the name that the joiner sent. This PC is about to save the joiner under the name that
            // arrived, so the two must be the same: a device on the path could have changed it.
            if (message.Type == "confirmed" && IsProof(transferKey, message.Payload, ConfirmedContext(sentName))) return null;
            request.MarkCheckFailed();
            return "The pairing check failed. Try again.";
        }

        /// <summary>
        /// Stops accepting PCs. Requests still waiting are denied, as are PCs whose users have not confirmed the codes
        /// yet, and the other PCs are told so before their connections close.
        /// </summary>
        public async Task StopAsync()
        {
            StopListening();
            lock (_open) foreach (var request in _open) request.Deny();
            Task[] handlers;
            lock (_handlers) handlers = _handlers.ToArray();
            try { await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Dispose();
        }

        public void Dispose()
        {
            StopListening();
            _cts.Cancel();
        }

        private void StopListening()
        {
            lock (_gate)
            {
                _stopped = true;
                _listen.Cancel();
                CloseSockets();
            }
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
        /// <summary>"allow": the sealed code. "confirmed" and "done": the proof that the sender made the exchange (see <see cref="Proof"/>).</summary>
        public string Payload { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        /// <summary>
        /// "commit": the joiner understands being allowed one way. Not protected here, so the host reads it from the
        /// joiner's <see cref="JoinerFeatures"/> when it sends them.
        /// </summary>
        public bool CanBeManaged { get; init; }
        /// <summary>
        /// "allow": the payload is the joiner's own code, and it is managed by the fleet one way. Sealed with the code,
        /// like <see cref="Name"/>, for a joiner with <see cref="JoinerFeatures.SealedAllow"/>.
        /// </summary>
        public bool Managed { get; init; }
    }

    /// <summary>The other PC's IPv4 address (or IPv6 when it has none) as this connection sees it.</summary>
    private static string RemoteAddress(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint remote) return string.Empty;
        return (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
    }

    private static byte[] DeriveShared(ECDiffieHellman mine, byte[] otherPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out var read);
        // Exactly one key. With bytes left over, one commitment would open to several keys (each one a part of the nonce
        // or of the rest for the others), and a device in the middle could pick its key after it has seen the host's.
        if (read != otherPublic.Length) throw new CryptographicException("Invalid public key.");
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

    /// <param name="firstByte">The first byte of the message, when a read that waited for it to begin has taken it already.</param>
    internal static async Task<PairMessage> ReadAsync(Stream stream, CancellationToken cancellationToken, TimeSpan timeout, byte? firstByte = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var length = new byte[4];
        if (firstByte is { } taken) length[0] = taken;
        await stream.ReadExactlyAsync(length.AsMemory(firstByte == null ? 0 : 1), cts.Token);
        var size = BitConverter.ToInt32(length);
        if (size is <= 0 or > 64 * 1024) throw new InvalidDataException("Invalid pairing message.");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, cts.Token);
        return JsonSerializer.Deserialize<PairMessage>(body) ?? throw new InvalidDataException("Empty pairing message.");
    }
}
