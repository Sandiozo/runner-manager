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

    public List<MachineCommand> Commands { get; } = new();

    /// <summary>Changes what the agent reports, for example its command results.</summary>
    public Func<MachineStatusPayload, MachineStatusPayload> Status { get; set; } = s => s;

    public TestAgent(string name = "TESTPC", bool start = true, string machineId = "", byte[]? key = null)
    {
        if (key != null) Key = key;
        Server = new AgentServer(
            () => Task.FromResult(Status(new MachineStatusPayload
            {
                MachineName = name, MachineId = machineId, AgentProtocolVersion = MachineRelay.ProtocolVersion, FleetAgentRunning = true,
                UpdatedUtc = DateTime.UtcNow
            })),
            command => { lock (Commands) Commands.Add(command); return command.Id; },
            _ => Task.FromResult(new LogPayload()),
            () => Key, $"http://localhost:{Port}/rw/", Port + 1, name, machineId: machineId);
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

[Collection(FleetSettingsCollection.Name)]
public class FleetConfigRobustnessTests
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    private static void WriteRaw(string json)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        WithRetry(() =>
        {
            File.WriteAllText(ConfigPath, json);
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddDays(-1).AddTicks(Random.Shared.Next()));
        });
    }

    private static void Cleanup()
    {
        WithRetry(() => File.Delete(ConfigPath));
        foreach (var file in Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*")) WithRetry(() => File.Delete(file));
    }

    private static void WithRetry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }

    private static string Protect(byte[] key) => Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));

    [Fact]
    public void NullsInAHandEditedFile_BecomeDefaults()
    {
        try
        {
            WriteRaw("{\"Assistant\":null,\"KnownPeers\":[null,{\"Name\":\"PC2\",\"Address\":null}],\"ManualAddresses\":null}");
            var config = FleetConfig.Current;
            Assert.NotNull(config.Assistant);
            Assert.False(config.Assistant.Enabled);
            Assert.Equal("", Assert.Single(config.KnownPeers).Address);
            Assert.Empty(config.ManualAddresses);
            FleetConfig.Update(c => c.Assistant.AskBeforeChanges = false);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void AValueOfTheWrongType_KeepsThePairingCodeAndTheOtherSettings()
    {
        var key = CommandSigner.NewKey();
        try
        {
            WriteRaw($"{{\"ProtectedFleetKey\":\"{Protect(key)}\",\"Watchdog\":\"yes\",\"ManualAddresses\":[\"pc-a\"]}}");
            var config = FleetConfig.Current;
            Assert.Equal(key, config.FleetKey);
            Assert.Contains("pc-a", config.ManualAddresses);
            Assert.True(config.Watchdog);
            Assert.Single(Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*"));
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void ACutOffFile_StillKeepsThePairingCode()
    {
        var key = CommandSigner.NewKey();
        try
        {
            WriteRaw($"{{\"ProtectedFleetKey\":\"{Protect(key)}\",\"KnownPeers\":[{{\"Name\":\"PC");
            Assert.Equal(key, FleetConfig.Current.FleetKey);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task Updates_WaitForAnotherProcessHoldingTheLock_AndSurviveOneThatDied()
    {
        try
        {
            FleetConfig.Update(c => c.ManualAddresses.Clear());
            using var other = Mutex.OpenExisting(FleetConfig.FileLockName);
            var held = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            // Mutexes belong to a thread, so a thread of its own stands in for the other process.
            var holder = new Thread(() =>
            {
                other.WaitOne();
                held.SetResult();
                release.Task.Wait();
                other.ReleaseMutex();
            });
            holder.Start();
            await held.Task;
            var update = Task.Run(() => FleetConfig.Update(c => c.ManualAddresses.Add("after-lock")));
            await Task.Delay(300);
            Assert.False(update.IsCompleted);
            release.SetResult();
            await update.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("after-lock", FleetConfig.Current.ManualAddresses);

            // A holder that ends without releasing the lock.
            var died = new Thread(() => other.WaitOne());
            died.Start();
            died.Join();
            await Task.Run(() => FleetConfig.Update(c => c.ManualAddresses.Add("after-abandoned"))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("after-abandoned", FleetConfig.Current.ManualAddresses);
        }
        finally { Cleanup(); }
    }
}

[Collection(FleetSettingsCollection.Name)]
public class PeerIdentityTests
{
    private static void Reset(byte[]? key = null) => FleetConfig.Update(c =>
    {
        c.KnownPeers.Clear();
        c.ProtectedFleetKey = key == null ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
    });

    [Fact]
    public async Task Requests_ForAnotherPcWithTheSameName_AreRefused()
    {
        using var agent = new TestAgent("TESTPC", machineId: "id-a");
        var command = new MachineCommand { Id = "c1", Target = "runner", Action = "Stop" };
        var error = await Assert.ThrowsAsync<PairingMismatchException>(() => FleetNetwork.CallAsync(agent.Address, "TESTPC",
            new FleetRequest { Op = "command", Command = command, TargetId = "id-b" }, pairingKey: agent.Key));
        Assert.Equal("wrong PC", error.Reason);
        Assert.Empty(agent.Commands);
        await FleetNetwork.CallAsync(agent.Address, "TESTPC", new FleetRequest { Op = "command", Command = command, TargetId = "id-a" }, pairingKey: agent.Key);
        Assert.Single(agent.Commands);
    }

    [Fact]
    public async Task Discovery_ReportsMachineIds_AndKeepsTwoPcsWithOneName()
    {
        var key = CommandSigner.NewKey();
        using var agent = new TestAgent("TESTPC", machineId: "id-a", key: key);
        var found = await FleetNetwork.DiscoverAsync(new[] { "127.0.0.1" }, TimeSpan.FromSeconds(1), agent.Port + 1, key);
        Assert.Contains(found, p => p.Name == "TESTPC" && p.MachineId == "id-a");
    }

    [Fact]
    public async Task ANewAddressFromDiscovery_IsUsedOnlyAfterAStatusRequestThereSucceeds()
    {
        var key = CommandSigner.NewKey();
        try
        {
            Reset(key);
            using var agent = new TestAgent("MOVEDPC", machineId: "id-moved", key: key);
            var dead = $"127.0.0.1:{TestPorts.FreeRange(1)}";
            var known = new PeerRecord { Name = "MOVEDPC", Address = dead, MachineId = "id-moved" };
            FleetConfig.Update(c => c.KnownPeers.Add(known));

            // A discovery answer forwarded from an address where nobody answers is not saved.
            var forwarded = new DiscoveredPeer("MOVEDPC", $"127.0.0.1:{TestPorts.FreeRange(1)}", "", "id-moved");
            var machine = await MachineRelay.FetchPeerAsync(known, known.Name, known.Address, new[] { forwarded });
            Assert.NotEqual(string.Empty, machine!.ConnectionProblem);
            Assert.Equal(dead, Assert.Single(FleetConfig.Current.KnownPeers).Address);

            // The real new address answers, so it is saved.
            var real = new DiscoveredPeer("MOVEDPC", agent.Address, "", "id-moved");
            machine = await MachineRelay.FetchPeerAsync(known, known.Name, known.Address, new[] { forwarded, real });
            Assert.Equal(string.Empty, machine!.ConnectionProblem);
            Assert.Equal(agent.Address, Assert.Single(FleetConfig.Current.KnownPeers).Address);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task ARenamedPc_IsRecognizedByItsMachineId()
    {
        var key = CommandSigner.NewKey();
        try
        {
            Reset(key);
            using var agent = new TestAgent("NEWNAME", machineId: "id-renamed", key: key);
            var known = new PeerRecord { Name = "OLDNAME", Address = agent.Address, MachineId = "id-renamed" };
            FleetConfig.Update(c => c.KnownPeers.Add(known));

            var machine = await MachineRelay.FetchPeerAsync(known, known.Name, known.Address, null);
            Assert.Equal("NEWNAME", machine!.MachineName);
            var peer = Assert.Single(FleetConfig.Current.KnownPeers);
            Assert.Equal("NEWNAME", peer.Name);
            Assert.Equal("id-renamed", peer.MachineId);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task TwoPcsWithOneName_AreReported_AndCommandsToThemRefused()
    {
        try
        {
            Reset(CommandSigner.NewKey());
            MachineRelay.RememberPeer("TWIN-PC", "10.0.0.1:47821", "", "id-1");
            MachineRelay.NoteSighting("TWIN-PC", "id-1", "10.0.0.1:47821");
            Assert.Null(MachineRelay.DuplicateNameProblem("TWIN_PC"));
            MachineRelay.NoteSighting("TWIN_PC", "id-2", "10.0.0.2:47821");
            var problem = MachineRelay.DuplicateNameProblem("TWIN_PC");
            Assert.Contains("TWIN-PC and TWIN_PC", problem);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MachineRelay.SendCommandAsync("TWIN_PC", "runner", "svc", "Stop"));
            Assert.Contains("Rename one of them", error.Message);
        }
        finally { Reset(); }
    }
}

[Collection(FleetSettingsCollection.Name)]
public class CommandWaitTests
{
    private static async Task WithAgentAsync(Func<TestAgent, Task> test)
    {
        var previous = MachineRelay.CommandPollInterval;
        MachineRelay.CommandPollInterval = TimeSpan.FromMilliseconds(100);
        using var agent = new TestAgent("WAITPC", machineId: "id-wait");
        try
        {
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = Convert.ToBase64String(ProtectedData.Protect(agent.Key, null, DataProtectionScope.CurrentUser));
                c.KnownPeers.Clear();
                c.KnownPeers.Add(new PeerRecord { Name = "WAITPC", Address = agent.Address, MachineId = "id-wait" });
            });
            await test(agent);
        }
        finally
        {
            MachineRelay.CommandPollInterval = previous;
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = string.Empty;
                c.KnownPeers.Clear();
            });
        }
    }

    private static FleetCommandResult Done(string id, string result) => new() { Id = id, Result = result, CompletedUtc = DateTime.UtcNow };

    [Fact]
    public Task ACommandFinishedBeforeAnother_IsStillFound() => WithAgentAsync(async agent =>
    {
        agent.Status = s => s with
        {
            LastCommandId = "second",
            LastCommandResult = "Runner stop completed",
            RecentCommands = new[] { Done("first", "Runner start completed"), Done("second", "Runner stop completed") }
        };
        Assert.Equal("Runner start completed", await MachineRelay.WaitForCommandAsync("WAITPC", "first", TimeSpan.FromSeconds(5)));
    });

    [Fact]
    public Task ACommandThatIsStillQueued_IsWaitedForPastTheTimeout() => WithAgentAsync(async agent =>
    {
        var finishAt = DateTime.UtcNow.AddSeconds(2);
        agent.Status = s => DateTime.UtcNow < finishAt
            ? s with { PendingCommandIds = new[] { "slow" }, Busy = true }
            : s with { RecentCommands = new[] { Done("slow", "Runner created: r1") } };
        Assert.Equal("Runner created: r1", await MachineRelay.WaitForCommandAsync("WAITPC", "slow", TimeSpan.FromMilliseconds(500)));
    });

    [Fact]
    public Task FailuresAreThrown_AndOlderAgentsAreUnderstood() => WithAgentAsync(async agent =>
    {
        // Agents before protocol 12 only report their last command.
        agent.Status = s => s with { LastCommandId = "old", LastCommandResult = "runner Stop failed: access denied" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MachineRelay.WaitForCommandAsync("WAITPC", "old", TimeSpan.FromSeconds(5)));
        Assert.Contains("access denied", error.Message);
    });

    [Fact]
    public Task AnUnknownCommand_TimesOut() => WithAgentAsync(async _ =>
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            MachineRelay.WaitForCommandAsync("WAITPC", "never-sent", TimeSpan.FromMilliseconds(500)));
        Assert.Contains("did not report a result", error.Message);
    });

    [Fact]
    public Task Commands_CarryTheIdOfTheIntendedPc_AndCanBeSentAgainWithTheSameId() => WithAgentAsync(async agent =>
    {
        var id = await MachineRelay.SendCommandAsync("WAITPC", "runner", "svc", "Stop", commandId: "retry-me");
        Assert.Equal("retry-me", id);
        await MachineRelay.SendCommandAsync("WAITPC", "runner", "svc", "Stop", commandId: "retry-me");
        Assert.All(agent.Commands, c => Assert.Equal("retry-me", c.Id));
        Assert.Equal(2, agent.Commands.Count);
    });
}

[Collection(FleetSettingsCollection.Name)]
public class RunnerMoveTests
{
    /// <summary>Runs a move between two test agents that answer each command with <paramref name="answer"/>.</summary>
    private static async Task<(List<MachineCommand> Source, List<MachineCommand> Target, Exception? Error)> MoveAsync(
        Func<MachineCommand, string> answer)
    {
        var key = CommandSigner.NewKey();
        var previous = MachineRelay.CommandPollInterval;
        MachineRelay.CommandPollInterval = TimeSpan.FromMilliseconds(100);
        using var source = new TestAgent("SOURCEPC", machineId: "id-source", key: key);
        using var target = new TestAgent("TARGETPC", machineId: "id-target", key: key);
        foreach (var agent in new[] { source, target })
            agent.Status = s =>
            {
                lock (agent.Commands)
                    return s with
                    {
                        RecentCommands = agent.Commands.Select(c => new FleetCommandResult { Id = c.Id, Result = answer(c) }).ToArray()
                    };
            };
        try
        {
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
                c.KnownPeers.Clear();
                c.KnownPeers.Add(new PeerRecord { Name = "SOURCEPC", Address = source.Address, MachineId = "id-source" });
                c.KnownPeers.Add(new PeerRecord { Name = "TARGETPC", Address = target.Address, MachineId = "id-target" });
            });
            var runner = new MachineRunnerStatus
            {
                ServiceName = "actions.runner.owner-repo.r1", AgentName = "r1", Scope = "repos/owner/repo", CustomLabels = new[] { "gpu" },
                ServiceInstalled = true
            };
            Exception? error = null;
            try
            {
                await new FleetActions(viaLocalAgent: true).MoveRunnerAsync(new MachineInfo { MachineKey = "SOURCEPC", MachineName = "SOURCEPC" }, runner,
                    new MachineInfo { MachineKey = "TARGETPC", MachineName = "TARGETPC" }, deleteOldFiles: false, null, _ => { });
            }
            catch (Exception ex) { error = ex; }
            return (source.Commands, target.Commands, error);
        }
        finally
        {
            MachineRelay.CommandPollInterval = previous;
            FleetConfig.Update(c =>
            {
                c.ProtectedFleetKey = string.Empty;
                c.KnownPeers.Clear();
            });
        }
    }

    private static string Steps(IEnumerable<MachineCommand> commands) => string.Join(" ", commands.Select(c => $"{c.Action}:{c.Value}"));

    [Fact]
    public async Task AMove_DisablesTheOldCopy_KeepsTheLabels_AndRetiresIt()
    {
        var (source, target, error) = await MoveAsync(c => $"Runner {c.Action} completed");
        Assert.Null(error);
        Assert.Equal("Stop:disable Retire:keep", Steps(source));
        var create = Assert.Single(target);
        Assert.Equal("Create", create.Action);
        Assert.Equal(new[] { "gpu" }, create.RunnerLabels);
    }

    [Fact]
    public async Task WhenTheTargetReportsAFailure_TheOldCopyIsEnabledAndStartedAgain()
    {
        var (source, _, error) = await MoveAsync(c => c.Action == "Create" ? "runner Create failed: no token" : $"Runner {c.Action} completed");
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("Stop:disable Start:enable", Steps(source));
    }

    [Fact]
    public async Task WhenRetiringFails_TheErrorSaysToDeleteTheOldCopyByHand()
    {
        var (source, _, error) = await MoveAsync(c => c.Action == "Retire" ? "runner Retire failed: in use" : $"Runner {c.Action} completed");
        Assert.Contains("now runs on TARGETPC", error!.Message);
        Assert.Contains("Don't unregister it from GitHub", error.Message);
        Assert.Equal("Stop:disable Retire:keep", Steps(source));
    }
}
