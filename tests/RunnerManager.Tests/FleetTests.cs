using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RunnerManager.Tests;

public class PairingProtocolTests
{
    private static async Task<(TcpClient Client, NetworkStream Stream)> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return (client, client.GetStream());
    }

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task HostAndJoiner_OnLoopback_ShowTheSameCode_AndTheRequestEnds()
    {
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        Pairing.JoinRequest? seen = null;
        host.JoinRequested += request => { seen = request; request.Allow(); };
        host.Start();

        string? joinerCode = null;
        var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", code => joinerCode = code, CancellationToken.None);

        Assert.Equal(key, result.PairingKey);
        Assert.Equal("127.0.0.1:47821", result.HostAddress);
        Assert.NotNull(seen);
        Assert.Equal("NEWPC", seen!.Name);
        Assert.Equal(seen.Code, joinerCode);
        await seen.Ended.WaitAsync(Step);
    }

    [Fact]
    public async Task JoinerRevealingAnotherKey_ThanItCommittedTo_IsRefused()
    {
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        var requested = false;
        host.JoinRequested += request => { requested = true; request.Allow(); };
        host.Start();

        var (client, stream) = await ConnectAsync(port);
        using (client)
        {
            using var committed = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var revealed = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var nonce = RandomNumberGenerator.GetBytes(32);
            await Pairing.WriteAsync(stream, new Pairing.PairMessage
            {
                Type = "commit", Version = Pairing.Version, Name = "MITM",
                Commit = Convert.ToBase64String(Pairing.Commitment(committed.PublicKey.ExportSubjectPublicKeyInfo(), nonce))
            }, CancellationToken.None);
            var hello = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
            Assert.Equal("hello", hello.Type);

            // A key chosen after seeing the host's.
            await Pairing.WriteAsync(stream, new Pairing.PairMessage
            {
                Type = "reveal",
                PublicKey = Convert.ToBase64String(revealed.PublicKey.ExportSubjectPublicKeyInfo()),
                Nonce = Convert.ToBase64String(nonce)
            }, CancellationToken.None);
            var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
            Assert.Equal("deny", answer.Type);
            Assert.Empty(answer.Payload);
        }
        Assert.False(requested);
    }

    [Fact]
    public async Task OlderJoiner_IsToldToUpdate()
    {
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        host.JoinRequested += request => request.Allow();
        host.Start();

        var (client, stream) = await ConnectAsync(port);
        using (client)
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            // What version 1 joiners send first: their public key, before seeing the host's.
            await Pairing.WriteAsync(stream, new Pairing.PairMessage
            {
                Type = "hello", Name = "OLDPC", PublicKey = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo())
            }, CancellationToken.None);
            var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
            Assert.Equal("deny", answer.Type);
            Assert.Contains("Update Runner Manager", answer.Error);
        }
    }

    [Fact]
    public async Task Joiner_RefusesAnOlderHost()
    {
        var port = TestPorts.FreeRange(1);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // A version 1 host reads the first message, sees no "hello", and closes the connection.
            var oldHost = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                await Pairing.ReadAsync(stream, CancellationToken.None, Step);
            });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None));
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            await oldHost;

            // One that answers with a version 1 hello is refused as well.
            var answeringHost = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                await Pairing.WriteAsync(stream, new Pairing.PairMessage
                {
                    Type = "hello", Name = "OLDHOST", PublicKey = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo())
                }, CancellationToken.None);
                try { await Pairing.ReadAsync(stream, CancellationToken.None, Step); } catch { }
            });
            error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None));
            Assert.Contains("Update Runner Manager on OLDHOST", error.Message);
            await answeringHost;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StoppingTheHost_TellsWaitingPcsNo()
    {
        var port = TestPorts.FreeRange(2);
        var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        var requested = new TaskCompletionSource<Pairing.JoinRequest>();
        host.JoinRequested += request => requested.TrySetResult(request);
        host.Start();

        var join = Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None);
        var request = await requested.Task.WaitAsync(Step);
        await host.StopAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => join.WaitAsync(Step));
        Assert.Contains("did not allow", error.Message);
        Assert.True(request.Ended.IsCompleted);
    }

    [Fact]
    public async Task Host_TurnsAwayConnectionsBeyondItsLimit()
    {
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        host.Start();
        var waiting = new List<TcpClient>();
        try
        {
            for (var i = 0; i < Pairing.MaxConnections; i++) waiting.Add((await ConnectAsync(port)).Client);
            await Task.Delay(300);
            var (extra, stream) = await ConnectAsync(port);
            using (extra)
            {
                var answer = await Pairing.ReadAsync(stream, CancellationToken.None, Step);
                Assert.Equal("deny", answer.Type);
                Assert.Contains("busy", answer.Error);
            }
        }
        finally { foreach (var client in waiting) client.Dispose(); }
    }
}

public class HostPortTests
{
    [Theory]
    [InlineData("pc", "pc", null)]
    [InlineData("pc:1234", "pc", 1234)]
    [InlineData("10.0.0.5:47821", "10.0.0.5", 47821)]
    [InlineData("fe80::1", "fe80::1", null)]
    [InlineData("[fe80::1]", "fe80::1", null)]
    [InlineData("[fe80::1]:47821", "fe80::1", 47821)]
    public void SplitHostPort_HandlesNamesAndIPv6(string address, string host, int? port)
        => Assert.Equal((host, port), FleetNetwork.SplitHostPort(address));

    [Fact]
    public void WithPort_PutsIPv6AddressesInBrackets()
    {
        Assert.Equal("[fe80::1]:47821", FleetNetwork.WithPort("fe80::1"));
        Assert.Equal("[fe80::1]:5000", FleetNetwork.WithPort("[fe80::1]:5000"));
        Assert.Equal("pc:47821", FleetNetwork.WithPort("pc"));
        Assert.Equal("pc:5000", FleetNetwork.WithPort("pc:5000"));
    }
}
