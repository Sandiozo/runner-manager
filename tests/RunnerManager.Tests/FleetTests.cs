using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

/// <summary>A test agent on localhost ports, answering with a fixed status.</summary>
internal sealed class TestAgent : IDisposable
{
    public byte[] Key { get; } = CommandSigner.NewKey();
    public int Port { get; } = TestPorts.FreeRange(2);
    public AgentServer Server { get; }
    public string Address => $"localhost:{Port}";

    public TestAgent(string name = "TESTPC", bool start = true)
    {
        Server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = name, AgentProtocolVersion = MachineRelay.ProtocolVersion, FleetAgentRunning = true }),
            command => command.Id,
            _ => Task.FromResult(new LogPayload()),
            () => Key, $"http://localhost:{Port}/rw/", Port + 1, name);
        if (start) Server.Start();
    }

    /// <summary>Posts a request sealed for <paramref name="target"/> at <paramref name="time"/>, as CallAsync would.</summary>
    public async Task<(HttpStatusCode Status, string Text)> PostAsync(string target, long time, byte[]? key = null, string timeHeader = "")
    {
        var body = FleetCrypto.Seal(FleetNetwork.KeysFor(key ?? Key).Transport,
            JsonSerializer.SerializeToUtf8Bytes(new FleetRequest { Op = "status" }), FleetCrypto.RequestContext(target, time));
        using var http = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"http://{Address}/rw/") { Content = new ByteArrayContent(body) };
        message.Headers.Add("X-RW-Time", timeHeader.Length > 0 ? timeHeader : time.ToString());
        message.Headers.Add("X-RW-To", target);
        using var response = await http.SendAsync(message);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => Server.Dispose();
}

public class AgentHardeningTests
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void Freshness_AcceptsAnyValueWithoutThrowing()
    {
        Assert.True(FleetCrypto.IsFresh(Now));
        Assert.False(FleetCrypto.IsFresh(long.MaxValue));
        Assert.False(FleetCrypto.IsFresh(long.MinValue));
        Assert.False(FleetCrypto.IsFresh(Now - (long)TimeSpan.FromMinutes(3).TotalMilliseconds));
    }

    [Fact]
    public async Task OutOfRangeTimestamp_IsRefusedAsAClockProblem()
    {
        using var agent = new TestAgent();
        var (status, text) = await agent.PostAsync("TESTPC", Now, timeHeader: long.MaxValue.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("clocks differ", text);
    }

    [Fact]
    public async Task Strangers_AreNotToldWhetherAPcNameIsRight()
    {
        using var agent = new TestAgent();
        var stranger = CommandSigner.NewKey();
        Assert.Equal("denied", (await agent.PostAsync("OTHERPC", Now, stranger)).Text);
        Assert.Equal("denied", (await agent.PostAsync("TESTPC", Now, stranger)).Text);
        // A fleet member is told.
        Assert.Equal("wrong PC", (await agent.PostAsync("OTHERPC", Now)).Text);
    }

    [Fact]
    public async Task RequestsFromBeforeTheAgentStarted_AreRefused()
    {
        // Captured a minute ago, before this agent (and its memory of seen requests) existed.
        var sentEarlier = Now - (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        using var agent = new TestAgent();
        var (status, text) = await agent.PostAsync("TESTPC", sentEarlier);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("older than the fleet agent", text);
        Assert.Equal(HttpStatusCode.OK, (await agent.PostAsync("TESTPC", Now)).Status);
    }

    [Fact]
    public void ClockRefusals_SayHowFarApartTheClocksAre()
    {
        var fiveMinutesAhead = Now + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        var message = FleetNetwork.RefusalMessage($"the PC clocks differ by more than 2 minutes; set both to the correct time (time there: {fiveMinutesAhead})");
        Assert.Contains("differ by 5 minutes", message);
        // Older agents send no time.
        Assert.Contains("differ by more than 2 minutes", FleetNetwork.RefusalMessage("stale or missing timestamp; check the PC clocks"));
    }

    [Fact]
    public async Task OversizedAnswers_AreNotRead()
    {
        var port = TestPorts.FreeRange(1);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/rw/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.ContentLength64 = FleetNetwork.MaxResponseBytes + 1L;
            try { await context.Response.OutputStream.WriteAsync(new byte[FleetNetwork.MaxResponseBytes + 1]); } catch { }
            try { context.Response.Close(); } catch { }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FleetNetwork.CallAsync($"localhost:{port}", "TESTPC", new FleetRequest(), TimeSpan.FromSeconds(20), pairingKey: CommandSigner.NewKey()));
        await serve;
    }

    [Fact]
    public async Task Listeners_ThatCouldNotStart_AreTriedAgain()
    {
        var previous = AgentServer.ListenRetryInterval;
        AgentServer.ListenRetryInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            using var agent = new TestAgent(start: false);
            // Something else holds the discovery port when the agent starts.
            var blocker = new UdpClient(new IPEndPoint(IPAddress.Any, agent.Port + 1));
            Assert.ThrowsAny<Exception>(agent.Server.Start);
            Assert.False(agent.Server.IsListening);
            blocker.Dispose();

            for (var i = 0; i < 50 && !agent.Server.IsListening; i++) await Task.Delay(100);
            Assert.True(agent.Server.IsListening);
            var found = await FleetNetwork.DiscoverAsync(new[] { "127.0.0.1" }, TimeSpan.FromSeconds(1), agent.Port + 1, agent.Key);
            Assert.Contains(found, p => p.Name == "TESTPC");
        }
        finally { AgentServer.ListenRetryInterval = previous; }
    }
}
