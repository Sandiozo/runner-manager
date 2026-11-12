using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RunnerManager.Tests;

/// <summary>The joining PC accepts a code only after its own user has confirmed the 6-digit code.</summary>
public class JoinConfirmationTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private static Pairing.PairingHost StartHost(byte[] key, Action<Pairing.JoinRequest> answer, out string address)
    {
        var port = TestPorts.FreeRange(2);
        var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        host.JoinRequested += answer;
        host.Start();
        address = $"127.0.0.1:{port}";
        return host;
    }

    [Fact]
    public async Task TheCode_IsReturnedOnlyAfterThisPcConfirms()
    {
        var key = CommandSigner.NewKey();
        using var host = StartHost(key, request => request.Allow(), out var address);
        var sent = new TaskCompletionSource();
        host.Confirming += _ => sent.TrySetResult();

        var asked = new TaskCompletionSource<Pairing.JoinOffer>();
        var codesMatch = new TaskCompletionSource<bool>();
        var join = Pairing.JoinAsync(address, "NEWPC", _ => { },
            offer => { asked.TrySetResult(offer); return codesMatch.Task; }, CancellationToken.None);

        // The host allowed at once and has sent its code, but nobody has confirmed on this PC.
        await sent.Task.WaitAsync(Step);
        var offer = await asked.Task.WaitAsync(Step);
        Assert.Equal(new Pairing.JoinOffer("HOSTPC", Managed: false), offer);
        await Task.Delay(200);
        Assert.False(join.IsCompleted);

        codesMatch.SetResult(true);
        Assert.Equal(key, (await join.WaitAsync(Step)).PairingKey);
    }

    [Fact]
    public async Task ACodeThatIsNotConfirmed_IsNotReturned()
    {
        // Any device on the network can answer a search under another PC's name and allow by itself.
        using var host = StartHost(CommandSigner.NewKey(), request => request.Allow(), out var address);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => Task.FromResult(false), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_WhileTheCodeWaitsForConfirmation_EndsTheJoin()
    {
        using var host = StartHost(CommandSigner.NewKey(), request => request.Allow(), out var address);
        using var cancel = new CancellationTokenSource();
        var asked = new TaskCompletionSource();
        var never = new TaskCompletionSource<bool>();

        var join = Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked.TrySetResult(); return never.Task; }, cancel.Token);
        await asked.Task.WaitAsync(Step);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join.WaitAsync(Step));
        // A late click on "The codes match" changes nothing.
        never.SetResult(true);
        Assert.True(join.IsCanceled || join.IsFaulted);
    }

    [Fact]
    public async Task ThisPc_IsAskedOnlyAfterTheHostAllowed()
    {
        var requested = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(CommandSigner.NewKey(), request => requested.TrySetResult(request), out var address);
        var asked = false;

        var join = Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked = true; return Task.FromResult(true); }, CancellationToken.None);
        var request = await requested.Task.WaitAsync(Step);
        await Task.Delay(200);
        Assert.False(asked);

        request.Allow();
        await join.WaitAsync(Step);
        Assert.True(asked);
    }

    [Fact]
    public async Task ThisPc_IsNotAsked_WhenTheHostDenies()
    {
        using var host = StartHost(CommandSigner.NewKey(), request => request.Deny(), out var address);
        var asked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => { asked = true; return Task.FromResult(true); }, CancellationToken.None));
        Assert.False(asked);
    }

    [Fact]
    public async Task TheQuestion_SaysWhenThisPcWillBeManagedOneWay()
    {
        var ownKey = CommandSigner.NewKey();
        using var host = StartHost(CommandSigner.NewKey(), request => request.AllowManaged(ownKey), out var address);
        Pairing.JoinOffer? offer = null;

        var result = await Pairing.JoinAsync(address, "FRIENDPC", _ => { }, o => { offer = o; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(new Pairing.JoinOffer("HOSTPC", Managed: true), offer);
        Assert.True(result.Managed);
        Assert.Equal(ownKey, result.PairingKey);
    }
}

/// <summary>The host's name and the one-way flag in "allow" are sealed with the code, and PCs from before that still pair.</summary>
public class SealedAllowTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private static Pairing.PairingHost StartHost(byte[] key, Action<Pairing.JoinRequest> answer, out int port)
    {
        port = TestPorts.FreeRange(2);
        var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        host.JoinRequested += answer;
        host.Start();
        return host;
    }

    /// <summary>A device on the path: it passes each pairing message on after <paramref name="change"/>.</summary>
    internal static TcpListener StartRelay(int hostPort, Func<Pairing.PairMessage, Pairing.PairMessage> change, out string address)
    {
        var port = TestPorts.FreeRange(1);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        address = $"127.0.0.1:{port}";
        _ = Task.Run(async () =>
        {
            try
            {
                using var joinerSide = await listener.AcceptTcpClientAsync();
                using var hostSide = new TcpClient();
                await hostSide.ConnectAsync(IPAddress.Loopback, hostPort);
                var joiner = joinerSide.GetStream();
                var host = hostSide.GetStream();
                // commit and hello, then reveal and the host's answer, then confirmed and done.
                for (var round = 0; round < 3; round++)
                {
                    await Pairing.WriteAsync(host, change(await Pairing.ReadAsync(joiner, CancellationToken.None, Step)), CancellationToken.None);
                    await Pairing.WriteAsync(joiner, change(await Pairing.ReadAsync(host, CancellationToken.None, Step)), CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        });
        return listener;
    }

    private static byte[] SharedSecret(ECDiffieHellman mine, byte[] otherPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeviceOnThePath_CannotFlipTheOneWayFlag(bool managed)
    {
        var ownKey = CommandSigner.NewKey();
        using var host = StartHost(CommandSigner.NewKey(), request => { if (managed) request.AllowManaged(ownKey); else request.Allow(); }, out var port);
        var relay = StartRelay(port, message => message.Type == "allow" ? message with { Managed = !managed } : message, out var address);
        try
        {
            await Assert.ThrowsAsync<CryptographicException>(() => Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None));
        }
        finally { relay.Stop(); }
    }

    [Fact]
    public async Task ADeviceOnThePath_CannotRenameTheHost()
    {
        var key = CommandSigner.NewKey();
        using var host = StartHost(key, request => request.Allow(), out var port);

        var relay = StartRelay(port, message => message.Type == "allow" ? message with { Name = "OTHERPC" } : message, out var address);
        try
        {
            await Assert.ThrowsAsync<CryptographicException>(() => Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None));
        }
        finally { relay.Stop(); }

        // The name in "hello" is not protected, and is not the one that is used.
        relay = StartRelay(port, message => message.Type == "hello" ? message with { Name = "OTHERPC" } : message, out address);
        try
        {
            var result = await Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None);
            Assert.Equal("HOSTPC", result.HostName);
            Assert.Equal(key, result.PairingKey);
        }
        finally { relay.Stop(); }
    }

    [Fact]
    public async Task ADeviceOnThePath_CannotMakeTheHostSealTheOlderWay()
    {
        // Without the joiner's features the host would seal the code alone, and the flag could be flipped again.
        using var host = StartHost(CommandSigner.NewKey(), request => request.Allow(), out var port);
        var relay = StartRelay(port, message => message.Type == "reveal"
            ? message with { Nonce = Convert.ToBase64String(Convert.FromBase64String(message.Nonce)[..Pairing.NonceRandomBytes]) }
            : message, out var address);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None));
            Assert.Contains("pairing check failed", error.Message);
        }
        finally { relay.Stop(); }
    }

    [Fact]
    public async Task ADeviceOnThePath_CannotHideThatTheJoinerCanBeManaged()
    {
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(CommandSigner.NewKey(), request => { seen.TrySetResult(request); request.Allow(); }, out var port);
        var relay = StartRelay(port, message => message.Type == "commit" ? message with { CanBeManaged = false } : message, out var address);
        try
        {
            await Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None);
            Assert.True((await seen.Task.WaitAsync(Step)).CanBeManaged);
        }
        finally { relay.Stop(); }
    }

    [Fact]
    public async Task AJoinerFromBeforeTheSeal_StillGetsItsCode()
    {
        var ownKey = CommandSigner.NewKey();
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(CommandSigner.NewKey(), request => { seen.TrySetResult(request); request.AllowManaged(ownKey); }, out var port);
        var joined = new TaskCompletionSource();
        host.Joined += _ => joined.TrySetResult();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        // What 2.8.0 to 2.8.2 send: 32 random bytes with no features, and "can be managed" in the commit message only.
        var nonce = RandomNumberGenerator.GetBytes(32);
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "commit", Version = Pairing.Version, Name = "OLDPC", Commit = Convert.ToBase64String(Pairing.Commitment(myPublic, nonce)), CanBeManaged = true
        }, CancellationToken.None);
        var hello = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "reveal", PublicKey = Convert.ToBase64String(myPublic), Nonce = Convert.ToBase64String(nonce)
        }, CancellationToken.None);
        var allow = await Pairing.ReadAsync(stream, CancellationToken.None, Step);

        Assert.True((await seen.Task.WaitAsync(Step)).CanBeManaged);
        Assert.Equal("allow", allow.Type);
        Assert.True(allow.Managed);
        // It opens the code the only way it knows.
        var transferKey = Pairing.TransferKey(SharedSecret(ecdh, Convert.FromBase64String(hello.PublicKey)));
        Assert.Equal(ownKey, FleetCrypto.Open(transferKey, Convert.FromBase64String(allow.Payload), "rw-pair-transfer"));
        // It keeps the code at once and says nothing more, so the host adds it without waiting.
        await joined.Task.WaitAsync(Step);
        Assert.False((await seen.Task).Confirms);
    }

    [Fact]
    public async Task AHostFromBeforeTheSeal_IsStillJoined()
    {
        var ownKey = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(1);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // What a 2.8.2 host does: it checks the commitment over the nonce as it comes, and seals the code alone.
            var oldHost = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var commit = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                await Pairing.WriteAsync(stream, new Pairing.PairMessage
                {
                    Type = "hello", Version = Pairing.Version, Name = "OLDHOST", PublicKey = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo())
                }, CancellationToken.None);
                var reveal = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                var joinerPublic = Convert.FromBase64String(reveal.PublicKey);
                Assert.Equal(Convert.FromBase64String(commit.Commit), Pairing.Commitment(joinerPublic, Convert.FromBase64String(reveal.Nonce)));
                var payload = FleetCrypto.Seal(Pairing.TransferKey(SharedSecret(ecdh, joinerPublic)), ownKey, "rw-pair-transfer");
                await Pairing.WriteAsync(stream, new Pairing.PairMessage
                {
                    Type = "allow", Name = "OLDHOST", Payload = Convert.ToBase64String(payload), Managed = true
                }, CancellationToken.None);
                // It does not wait for "confirmed", and the joiner sends none: the next thing is the end of the connection.
                Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Step));
            });

            // With a machine ID, as the app sends it: the nonce is at its longest, and the old host takes it as it is.
            var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => Task.FromResult(true), CancellationToken.None,
                "0f8fad5bd9cb469fa16570867728950e");
            await oldHost;

            Assert.Equal(ownKey, result.PairingKey);
            Assert.Equal("OLDHOST", result.HostName);
            Assert.True(result.Managed);
        }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// The host adds a PC only once that PC says that its user confirmed the codes, and the PC keeps its code only once the
/// host has answered: both have the pairing, or neither.
/// </summary>
public class ConfirmedMessageTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    internal const Pairing.JoinerFeatures Current =
        Pairing.JoinerFeatures.SealedAllow | Pairing.JoinerFeatures.CanBeManaged | Pairing.JoinerFeatures.Confirms;

    internal static Pairing.PairingHost StartHost(Action<Pairing.JoinRequest> answer, out int port, TimeSpan? confirmTimeout = null)
    {
        port = TestPorts.FreeRange(2);
        var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1) { ConfirmTimeout = confirmTimeout ?? Pairing.ConfirmTimeout };
        host.JoinRequested += answer;
        host.Start();
        return host;
    }

    /// <summary>A joiner by hand, up to the host's answer: that answer, and the key that both PCs derived.</summary>
    internal static async Task<(Pairing.PairMessage Answer, byte[] TransferKey)> JoinByHandAsync(NetworkStream stream, Pairing.JoinerFeatures features,
        string? machineId = null)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonce = Pairing.NewNonce(features, machineId);
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "commit", Version = Pairing.Version, Name = "NEWPC", Commit = Convert.ToBase64String(Pairing.Commitment(myPublic, nonce)), CanBeManaged = true
        }, CancellationToken.None);
        var hello = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "reveal", PublicKey = Convert.ToBase64String(myPublic), Nonce = Convert.ToBase64String(nonce)
        }, CancellationToken.None);
        var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
        return (answer, TransferKey(ecdh, Convert.FromBase64String(hello.PublicKey)));
    }

    private static byte[] TransferKey(ECDiffieHellman mine, byte[] otherPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return Pairing.TransferKey(mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task TheHost_AddsThePc_OnlyAfterItsUserConfirmed()
    {
        using var host = StartHost(request => request.Allow(), out var port);
        var confirming = new TaskCompletionSource<Pairing.JoinRequest>();
        var joined = new TaskCompletionSource();
        host.Confirming += request => confirming.TrySetResult(request);
        host.Joined += _ => joined.TrySetResult();
        var codesMatch = new TaskCompletionSource<bool>();

        var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => codesMatch.Task, CancellationToken.None);

        // The PC has its code and asks its user. The host has not added it.
        var request = await confirming.Task.WaitAsync(Step);
        Assert.True(request.Confirms);
        Assert.True(request.CodeSent);
        await Task.Delay(200);
        Assert.False(joined.Task.IsCompleted);
        Assert.False(request.Ended.IsCompleted);

        codesMatch.SetResult(true);
        await join.WaitAsync(Step);
        // The PC got its answer only after the host had added it.
        Assert.True(joined.Task.IsCompleted);
        await request.Ended.WaitAsync(Step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APcWhoseUserDoesNotConfirm_IsNotAdded(bool leaves)
    {
        using var host = StartHost(request => request.Allow(), out var port);
        var confirming = new TaskCompletionSource<Pairing.JoinRequest>();
        var joined = false;
        host.Confirming += request => confirming.TrySetResult(request);
        host.Joined += _ => joined = true;
        using var cancel = new CancellationTokenSource();
        var asked = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();

        var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => { asked.TrySetResult(); return answer.Task; }, cancel.Token);
        await asked.Task.WaitAsync(Step);
        // Back, Cancel or a closed window end the join; the question can also be answered with no.
        if (leaves) cancel.Cancel(); else answer.SetResult(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join.WaitAsync(Step));

        var request = await confirming.Task.WaitAsync(Step);
        await request.Ended.WaitAsync(Step);
        Assert.True(request.CodeSent);
        Assert.False(joined);
    }

    [Fact]
    public async Task WhenTheHostStops_TheQuestionEnds_AndTheCodeIsNotUsed()
    {
        var host = StartHost(request => request.Allow(), out var port);
        var joined = false;
        host.Joined += _ => joined = true;
        var asked = new TaskCompletionSource();
        var never = new TaskCompletionSource<bool>();

        var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => { asked.TrySetResult(); return never.Task; }, CancellationToken.None);
        await asked.Task.WaitAsync(Step);
        // "Add a PC" is closed while the other PC still asks its user.
        await host.StopAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => join.WaitAsync(Step));
        Assert.Contains("HOSTPC closed “Add a PC”", error.Message);
        Assert.False(joined);
    }

    [Fact]
    public async Task WhenTheUserTakesTooLong_TheHostStopsWaiting()
    {
        using var host = StartHost(request => request.Allow(), out var port, confirmTimeout: TimeSpan.FromMilliseconds(300));
        var joined = false;
        host.Joined += _ => joined = true;
        var never = new TaskCompletionSource<bool>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => never.Task, CancellationToken.None).WaitAsync(Step));

        Assert.Contains("did not get the confirmation in time", error.Message);
        Assert.False(joined);
    }

    [Fact]
    public async Task WhenTheHostCannotSaveThePc_ThePcIsTold_AndDropsItsCode()
    {
        using var host = StartHost(request => request.Allow(), out var port);
        host.Joined += _ => throw new IOException("The disk is full.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None).WaitAsync(Step));

        Assert.Contains("HOSTPC could not save this PC", error.Message);
    }

    [Theory]
    [InlineData("another key")]
    [InlineData("another name")]
    [InlineData("genuine")]
    public async Task TheHost_AddsThePc_OnlyForAConfirmedThatThePcItselfMade(string proof)
    {
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(request => { seen.TrySetResult(request); request.Allow(); }, out var port);
        var joined = false;
        host.Joined += _ => joined = true;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var (allow, transferKey) = await JoinByHandAsync(stream, Current);
        Assert.Equal("allow", allow.Type);
        // The seal says that the host waits.
        Assert.NotNull(FleetCrypto.Open(transferKey, Convert.FromBase64String(allow.Payload), Pairing.AllowContext("HOSTPC", managed: false, confirms: true)));

        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "confirmed",
            Payload = proof switch
            {
                // From a device that has seen every message, but not the key that the two PCs derived.
                "another key" => Pairing.Proof(CommandSigner.NewKey(), Pairing.ConfirmedContext("NEWPC")),
                // From the PC itself, but it joined under another name than the one that arrived at the host.
                "another name" => Pairing.Proof(transferKey, Pairing.ConfirmedContext("OTHERPC")),
                _ => Pairing.Proof(transferKey, Pairing.ConfirmedContext("NEWPC"))
            }
        }, CancellationToken.None);

        var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
        var request = await seen.Task.WaitAsync(Step);
        await request.Ended.WaitAsync(Step);
        if (proof == "genuine")
        {
            Assert.Equal("done", answer.Type);
            Assert.NotNull(FleetCrypto.Open(transferKey, Convert.FromBase64String(answer.Payload), Pairing.DoneContext));
            Assert.True(joined);
            Assert.False(request.CheckFailed);
        }
        else
        {
            Assert.Equal("deny", answer.Type);
            Assert.Contains("pairing check failed", answer.Error);
            Assert.False(joined);
            Assert.True(request.CheckFailed);
        }
    }

    [Fact]
    public async Task ADeviceOnThePath_CannotRenameTheJoiningPc()
    {
        // The host saves a PC under the name in "commit", which nothing protects on the way, and a PC that is managed
        // one way replaces the one of that name. Both users then approve a request that looks right to each of them.
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = StartHost(request => { seen.TrySetResult(request); request.Allow(); }, out var port);
        var joined = false;
        host.Joined += _ => joined = true;
        var relay = SealedAllowTests.StartRelay(port, message => message.Type == "commit" ? message with { Name = "BUILD-SERVER" } : message, out var address);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Pairing.JoinAsync(address, "NEWPC", _ => { }, CancellationToken.None).WaitAsync(Step));
            Assert.Contains("pairing check failed", error.Message);

            var request = await seen.Task.WaitAsync(Step);
            await request.Ended.WaitAsync(Step);
            Assert.Equal("BUILD-SERVER", request.Name);
            Assert.True(request.CheckFailed);
            Assert.False(joined);
        }
        finally { relay.Stop(); }
    }

    [Fact]
    public async Task AfterItsYes_ThePcWaitsForTheHostsAnswer_AlsoWhenTheJoinIsCancelled()
    {
        // The host adds the PC before it answers. A PC that left in that moment would have no code, and the host a PC.
        using var cancel = new CancellationTokenSource();
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        host.JoinRequested += request => request.Allow();
        // Back or Cancel, while the host saves the PC.
        host.Joined += _ => cancel.Cancel();
        host.Start();

        var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => Task.FromResult(true), cancel.Token).WaitAsync(Step);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(key, result.PairingKey);
    }

    [Fact]
    public async Task AJoinerThatDoesNotConfirm_IsNotToldThatTheHostWaits()
    {
        // A joiner that knows the seal but not "confirmed": the host adds it when it has its code, as before.
        using var host = StartHost(request => request.Allow(), out var port);
        var joined = new TaskCompletionSource<Pairing.JoinRequest>();
        host.Joined += request => joined.TrySetResult(request);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        var (allow, transferKey) = await JoinByHandAsync(client.GetStream(), Pairing.JoinerFeatures.SealedAllow | Pairing.JoinerFeatures.CanBeManaged);

        Assert.NotNull(FleetCrypto.Open(transferKey, Convert.FromBase64String(allow.Payload), Pairing.AllowContext("HOSTPC", managed: false)));
        Assert.False((await joined.Task.WaitAsync(Step)).Confirms);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("forged")]
    [InlineData("silent")]
    public async Task ThePc_KeepsItsCode_OnlyWhenTheHostItselfAnswersDone(string answer)
    {
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(1);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // A host that waits for "confirmed", by hand.
            var host = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                await Pairing.WriteAsync(stream, new Pairing.PairMessage
                {
                    Type = "hello", Version = Pairing.Version, Name = "HOSTPC", PublicKey = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo())
                }, CancellationToken.None);
                var reveal = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                var transferKey = TransferKey(ecdh, Convert.FromBase64String(reveal.PublicKey));
                var payload = FleetCrypto.Seal(transferKey, key, Pairing.AllowContext("HOSTPC", managed: false, confirms: true));
                await Pairing.WriteAsync(stream, new Pairing.PairMessage { Type = "allow", Name = "HOSTPC", Payload = Convert.ToBase64String(payload) }, CancellationToken.None);

                var confirmed = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                Assert.Equal("confirmed", confirmed.Type);
                // The proof holds the name that the PC sent in "commit".
                Assert.NotNull(FleetCrypto.Open(transferKey, Convert.FromBase64String(confirmed.Payload), Pairing.ConfirmedContext("NEWPC")));
                if (answer == "silent") return;
                // "forged": from a device that has not made the exchange.
                var proof = Pairing.Proof(answer == "done" ? transferKey : CommandSigner.NewKey(), Pairing.DoneContext);
                await Pairing.WriteAsync(stream, new Pairing.PairMessage { Type = "done", Payload = proof }, CancellationToken.None);
                try { await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(Step); }
                catch (IOException) { }
            });

            var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None).WaitAsync(Step);
            if (answer == "done") Assert.Equal(key, (await join).PairingKey);
            else if (answer == "forged") await Assert.ThrowsAsync<CryptographicException>(() => join);
            else Assert.Contains("did not answer after the codes were confirmed", (await Assert.ThrowsAsync<InvalidOperationException>(() => join)).Message);
            await host.WaitAsync(Step);
        }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// A joining PC sends its machine ID, so that "Add a PC" can tell it from another PC with the same name before the
/// user allows it.
/// </summary>
public class JoinerMachineIdTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private const string Id = "0f8fad5bd9cb469fa16570867728950e";

    [Fact]
    public async Task TheHost_HasTheMachineId_WhenItShowsTheRequest()
    {
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        // Not answered yet: the ID is there before the user of the host decides.
        using var host = ConfirmedMessageTests.StartHost(request => seen.TrySetResult(request), out var port);
        var joined = false;
        host.Joined += _ => joined = true;

        var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => Task.FromResult(true), CancellationToken.None, Id);
        var request = await seen.Task.WaitAsync(Step);
        Assert.Equal(Id, request.MachineId);

        request.Allow();
        await join.WaitAsync(Step);
        Assert.True(joined);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("an id with spaces")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e0f8fad5bd9cb469fa16570867728950e0")]
    public async Task APcWithoutAnIdThatCanBeSent_PairsAsBefore(string? machineId)
    {
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = ConfirmedMessageTests.StartHost(request => { seen.TrySetResult(request); request.Allow(); }, out var port);
        var joined = false;
        host.Joined += _ => joined = true;

        await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, _ => Task.FromResult(true), CancellationToken.None, machineId).WaitAsync(Step);

        Assert.Null((await seen.Task.WaitAsync(Step)).MachineId);
        Assert.True(joined);
    }

    [Fact]
    public void TheNonce_CarriesTheId_AfterTheFeatures()
    {
        var nonce = Pairing.NewNonce(ConfirmedMessageTests.Current, Id);

        Assert.Equal(Pairing.NonceRandomBytes + 2 + Id.Length, nonce.Length);
        Assert.Equal(ConfirmedMessageTests.Current | Pairing.JoinerFeatures.MachineId, Pairing.FeaturesOf(nonce));
        Assert.Equal(Id, Pairing.MachineIdOf(nonce));
        // Bytes after the ID are left for what a later version adds.
        Assert.Equal(Id, Pairing.MachineIdOf(nonce.Concat(new byte[] { 1, 2, 3 }).ToArray()));
    }

    [Fact]
    public void ANonceWithoutAnId_GivesNone()
    {
        // An older joiner (before 2.9): random bytes only.
        Assert.Null(Pairing.MachineIdOf(RandomNumberGenerator.GetBytes(Pairing.NonceRandomBytes)));
        // Features, but not this one.
        Assert.Null(Pairing.MachineIdOf(Pairing.NewNonce(ConfirmedMessageTests.Current)));
        // The feature without what it promises, cut off at each place.
        var nonce = Pairing.NewNonce(ConfirmedMessageTests.Current, Id);
        for (var length = Pairing.NonceRandomBytes + 1; length < nonce.Length; length++)
            Assert.Null(Pairing.MachineIdOf(nonce[..length]));
        // An ID that is not made of letters, digits and hyphens.
        var odd = (byte[])nonce.Clone();
        odd[^1] = (byte)'\n';
        Assert.Null(Pairing.MachineIdOf(odd));
        odd[^1] = 0xE9;
        Assert.Null(Pairing.MachineIdOf(odd));
        // An empty one.
        var empty = Pairing.NewNonce(ConfirmedMessageTests.Current | Pairing.JoinerFeatures.MachineId).Append((byte)0).ToArray();
        Assert.Null(Pairing.MachineIdOf(empty));
    }

    [Fact]
    public async Task ADeviceOnThePath_CannotChangeTheMachineId()
    {
        // With the ID of a PC of the fleet in its place, "Add a PC" would show no warning for a PC of the same name.
        var requests = 0;
        using var host = ConfirmedMessageTests.StartHost(request => { requests++; request.Allow(); }, out var port);
        var relay = SealedAllowTests.StartRelay(port, message =>
        {
            if (message.Type != "reveal") return message;
            var nonce = Convert.FromBase64String(message.Nonce);
            System.Text.Encoding.ASCII.GetBytes("ffffffffffffffffffffffffffffffff").CopyTo(nonce, Pairing.NonceRandomBytes + 2);
            return message with { Nonce = Convert.ToBase64String(nonce) };
        }, out var address);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Pairing.JoinAsync(address, "NEWPC", _ => { }, _ => Task.FromResult(true), CancellationToken.None, Id).WaitAsync(Step));
            Assert.Contains("pairing check failed", error.Message);
            Assert.Equal(0, requests);
        }
        finally { relay.Stop(); }
    }

    [Theory]
    [InlineData("without the id")]
    [InlineData("another id")]
    [InlineData("genuine")]
    public async Task TheHost_AddsThePc_OnlyForAConfirmedThatHoldsItsId(string proof)
    {
        var seen = new TaskCompletionSource<Pairing.JoinRequest>();
        using var host = ConfirmedMessageTests.StartHost(request => { seen.TrySetResult(request); request.Allow(); }, out var port);
        var joined = false;
        host.Joined += _ => joined = true;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        var (allow, transferKey) = await ConfirmedMessageTests.JoinByHandAsync(stream, ConfirmedMessageTests.Current, Id);
        Assert.Equal("allow", allow.Type);
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "confirmed",
            Payload = Pairing.Proof(transferKey, proof switch
            {
                "without the id" => Pairing.ConfirmedContext("NEWPC"),
                "another id" => Pairing.ConfirmedContext("NEWPC", "ffffffffffffffffffffffffffffffff"),
                _ => Pairing.ConfirmedContext("NEWPC", Id)
            })
        }, CancellationToken.None);

        var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
        var request = await seen.Task.WaitAsync(Step);
        await request.Ended.WaitAsync(Step);
        Assert.Equal(Id, request.MachineId);
        Assert.Equal(proof == "genuine" ? "done" : "deny", answer.Type);
        Assert.Equal(proof == "genuine", joined);
        Assert.Equal(proof != "genuine", request.CheckFailed);
    }

    private static FleetConfig Fleet() => new()
    {
        KnownPeers = { new PeerRecord { Name = "OFFICE-PC", MachineId = Id }, new PeerRecord { Name = "OLD-PC" } },
        ManagedPcs = { new ManagedPcRecord { Name = "GUEST-PC", MachineId = "aaaa1111" }, new ManagedPcRecord { Name = "NEW-GUEST" } }
    };

    private static string? Warning(string name, string? machineId) => PairingHostWindow.SameNameWarning(Fleet(), name, machineId, thisPc: "HOST-PC");

    [Fact]
    public void AddAPc_DoesNotWarn_WhenTheNameIsFree()
    {
        Assert.Null(Warning("LAPTOP", Id));
        Assert.Null(Warning("LAPTOP", null));
    }

    [Fact]
    public void AddAPc_DoesNotWarn_WhenThePcOfThatNameJoinsAgain()
    {
        Assert.Null(Warning("OFFICE-PC", Id));
        Assert.Null(Warning("OFFICE-PC", Id.ToUpperInvariant()));
        Assert.Null(Warning("GUEST-PC", "aaaa1111"));
    }

    [Theory]
    // Another PC with the name of a PC of the fleet.
    [InlineData("OFFICE-PC", "ffffffffffffffffffffffffffffffff", "A PC named OFFICE-PC is already in this fleet. Allow only if this is the same PC.")]
    // An older PC, which sends no ID.
    [InlineData("OFFICE-PC", null, "A PC named OFFICE-PC is already in this fleet. Allow only if this is the same PC.")]
    // Runner Manager takes names that differ only in punctuation as one name.
    [InlineData("office_pc", "ffffffffffffffffffffffffffffffff", "A PC named office_pc is already in this fleet. Allow only if this is the same PC.")]
    // The saved PC has no ID yet, so it cannot be told to be the same one.
    [InlineData("OLD-PC", Id, "A PC named OLD-PC is already in this fleet. Allow only if this is the same PC.")]
    [InlineData("GUEST-PC", Id, "This fleet already manages a PC named GUEST-PC. Allow only if this is the same PC.")]
    [InlineData("GUEST-PC", null, "This fleet already manages a PC named GUEST-PC. Allow only if this is the same PC.")]
    [InlineData("NEW-GUEST", Id, "This fleet already manages a PC named NEW-GUEST. Allow only if this is the same PC.")]
    public void AddAPc_Warns_WhenTheNameIsTaken_AndThePcDidNotShowThatItIsThatPc(string name, string? machineId, string expected)
        => Assert.Equal(expected, Warning(name, machineId));

    [Fact]
    public void AddAPc_Warns_WhenAnotherPcOfThatNameIsKnownToo()
    {
        // The ID is the one of the fleet's PC of that name, but a managed PC has the name as well.
        var fleet = Fleet();
        fleet.ManagedPcs.Add(new ManagedPcRecord { Name = "OFFICE-PC", MachineId = "bbbb2222" });

        Assert.Equal("This fleet already manages a PC named OFFICE-PC. Allow only if this is the same PC.",
            PairingHostWindow.SameNameWarning(fleet, "OFFICE-PC", Id, thisPc: "HOST-PC"));
    }

    [Fact]
    public void AddAPc_Warns_WhenTheJoiningPcHasTheNameOfThisPc()
    {
        Assert.StartsWith("This PC is also named HOST-PC.", Warning("HOST-PC", Id));
        Assert.StartsWith("This PC is also named host_pc.", Warning("host_pc", null));
    }
}

public class PairingCommitmentTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ACommitment_OpensToOneKeyOnly()
    {
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        var requested = false;
        host.JoinRequested += request => { requested = true; request.Deny(); };
        host.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        using var first = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var nonce = RandomNumberGenerator.GetBytes(32);
        // One hash over nonce + key 1 + key 2 could be opened as key 1 (with key 2 left over) or as key 2 (with key 1 as
        // part of the nonce): a device in the middle could then choose its key after it has seen the host's.
        var twoKeys = FleetCrypto.Concat(first.PublicKey.ExportSubjectPublicKeyInfo(), second.PublicKey.ExportSubjectPublicKeyInfo());
        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "commit", Version = Pairing.Version, Name = "MITM", Commit = Convert.ToBase64String(Pairing.Commitment(twoKeys, nonce))
        }, CancellationToken.None);
        Assert.Equal("hello", (await Pairing.ReadAsync(stream, CancellationToken.None, Step)).Type);

        await Pairing.WriteAsync(stream, new Pairing.PairMessage
        {
            Type = "reveal", PublicKey = Convert.ToBase64String(twoKeys), Nonce = Convert.ToBase64String(nonce)
        }, CancellationToken.None);

        // The host ends the connection without showing a request.
        await Assert.ThrowsAnyAsync<IOException>(() => Pairing.ReadAsync(stream, CancellationToken.None, Step));
        Assert.False(requested);
    }
}

public class PairingHostLifetimeTests
{
    [Fact]
    public async Task AStoppedHost_DoesNotStart_AndLeavesThePortsFree()
    {
        var port = TestPorts.FreeRange(2);
        // "Add a PC" was closed before it started listening.
        var stopped = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        await stopped.StopAsync();

        Assert.Throws<ObjectDisposedException>(stopped.Start);

        using var next = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        next.Start();
    }

    [Fact]
    public void AHostThatCannotStart_KeepsNoPort()
    {
        var port = TestPorts.FreeRange(2);
        using var taken = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1));
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);

        Assert.Throws<SocketException>(host.Start);

        var tcp = new TcpListener(IPAddress.Any, port);
        tcp.Start();
        tcp.Stop();
    }
}

/// <summary>The list of PCs in the join window changes only where a search differs from it.</summary>
public class FoundHostsTests
{
    private static Pairing.Host Host(string name, string address = "10.0.0.5:47823") => new(name, address);

    private static List<NotifyCollectionChangedEventArgs> Changes(FoundHosts hosts)
    {
        var changes = new List<NotifyCollectionChangedEventArgs>();
        hosts.Items.CollectionChanged += (_, e) => changes.Add(e);
        return changes;
    }

    [Fact]
    public void TheSameAnswers_ChangeNothing()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("B"), Host("A") });
        var rows = hosts.Items.ToList();
        var changes = Changes(hosts);

        hosts.Update(new[] { Host("A"), Host("B") });

        Assert.Empty(changes);
        Assert.Equal(new[] { "A", "B" }, hosts.Items.Select(h => h.Name));
        // The same rows, so a button keeps its keyboard focus and a click that began before the search still counts.
        Assert.All(rows, row => Assert.Contains(hosts.Items, item => ReferenceEquals(item, row)));
    }

    [Fact]
    public void AHost_StaysThroughMissedAnswers_AndLeavesAfterOneMore()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("A"), Host("B") });
        var changes = Changes(hosts);

        for (var search = 0; search < FoundHosts.MissesAllowed; search++) hosts.Update(new[] { Host("B") });
        Assert.Empty(changes);
        Assert.Equal(new[] { "A", "B" }, hosts.Items.Select(h => h.Name));

        hosts.Update(new[] { Host("B") });
        Assert.Equal(NotifyCollectionChangedAction.Remove, Assert.Single(changes).Action);
        Assert.Equal(new[] { "B" }, hosts.Items.Select(h => h.Name));
    }

    [Fact]
    public void AnAnswer_StartsTheCountOfMissedAnswersAgain()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("A") });
        for (var round = 0; round < 3; round++)
        {
            for (var search = 0; search < FoundHosts.MissesAllowed; search++) hosts.Update(Array.Empty<Pairing.Host>());
            hosts.Update(new[] { Host("A") });
        }
        Assert.Single(hosts.Items);

        for (var search = 0; search <= FoundHosts.MissesAllowed; search++) hosts.Update(Array.Empty<Pairing.Host>());
        Assert.Empty(hosts.Items);
    }

    [Fact]
    public void ANewHost_IsAddedInNameOrder_AndTheOtherRowsStay()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("A"), Host("C") });
        var changes = Changes(hosts);

        hosts.Update(new[] { Host("C"), Host("b"), Host("A") });

        var change = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Add, 1), (change.Action, change.NewStartingIndex));
        Assert.Equal(new[] { "A", "b", "C" }, hosts.Items.Select(h => h.Name));
    }

    [Fact]
    public void AHostThatAnswersFromAnotherAddress_ChangesOnlyItsRow()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("A"), Host("B") });
        var changes = Changes(hosts);

        hosts.Update(new[] { Host("A", "10.0.0.9:47823"), Host("B") });

        var change = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Replace, 0), (change.Action, change.NewStartingIndex));
        Assert.Equal("10.0.0.9:47823", hosts.Items[0].Address);
    }

    [Fact]
    public void Clear_EmptiesTheList_AndForgetsTheMissedAnswers()
    {
        var hosts = new FoundHosts();
        hosts.Update(new[] { Host("A") });
        for (var search = 0; search < FoundHosts.MissesAllowed; search++) hosts.Update(Array.Empty<Pairing.Host>());

        hosts.Clear();
        Assert.Empty(hosts.Items);

        // Found again later, it gets the full count again.
        hosts.Update(new[] { Host("A") });
        hosts.Update(Array.Empty<Pairing.Host>());
        Assert.Single(hosts.Items);
    }
}
