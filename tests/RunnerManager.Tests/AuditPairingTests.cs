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
        host.Joined += _ => sent.TrySetResult();

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
    private static TcpListener StartRelay(int hostPort, Func<Pairing.PairMessage, Pairing.PairMessage> change, out string address)
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
                // commit and hello, then reveal and the host's answer.
                for (var round = 0; round < 2; round++)
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
            });

            var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None);
            await oldHost;

            Assert.Equal(ownKey, result.PairingKey);
            Assert.Equal("OLDHOST", result.HostName);
            Assert.True(result.Managed);
        }
        finally { listener.Stop(); }
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
