using System.IO;

namespace RunnerManager.Tests;

public class ManagedPairingTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OneWayPairing_GivesThePcItsOwnCode_NotTheFleets()
    {
        var fleetKey = CommandSigner.NewKey();
        var ownKey = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(fleetKey, "HOSTPC", port, port + 1);
        Pairing.JoinRequest? seen = null;
        host.JoinRequested += request => { seen = request; request.AllowManaged(ownKey); };
        host.Start();

        var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "FRIENDPC", _ => { }, CancellationToken.None);

        Assert.True(seen!.CanBeManaged);
        Assert.Equal("127.0.0.1", seen.Address);
        Assert.True(result.Managed);
        Assert.Equal(ownKey, result.PairingKey);
        Assert.NotEqual(fleetKey, result.PairingKey);
        await seen.Ended.WaitAsync(Step);
    }

    [Fact]
    public async Task FullPairing_IsNotManaged_AndTheHostLearnsWhereThePcIs()
    {
        var fleetKey = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(2);
        using var host = new Pairing.PairingHost(fleetKey, "HOSTPC", port, port + 1);
        var joined = new TaskCompletionSource<Pairing.JoinRequest>();
        host.JoinRequested += request => request.Allow();
        host.Joined += request => joined.TrySetResult(request);
        host.Start();

        var result = await Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None);

        Assert.False(result.Managed);
        Assert.Equal(fleetKey, result.PairingKey);
        // Over Tailscale, discovery can't find the new PC, so the host keeps the address it joined from.
        var request = await joined.Task.WaitAsync(Step);
        Assert.Equal(("NEWPC", "127.0.0.1"), (request.Name, request.Address));
    }
}

public class ManagedAgentTests
{
    private static (AgentServer Server, int Port) Start(Func<byte[]?> key, bool managed)
    {
        var port = TestPorts.FreeRange(2);
        var server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = "FRIENDPC", AgentProtocolVersion = MachineRelay.ProtocolVersion }),
            c => c.Id, _ => Task.FromResult(new LogPayload()), prefix: $"http://localhost:{port}/rw/", discoveryPort: port + 1,
            machineKey: "FRIENDPC", keys: new MemoryAgentKeys(key, managed));
        server.Start();
        return (server, port);
    }

    [Fact]
    public async Task ManagedPc_AnswersWithItsOwnCode_ButNotWithTheFleets_AndSharesNothing()
    {
        var own = CommandSigner.NewKey();
        var (server, port) = Start(() => own, managed: true);
        using (server)
        {
            var status = await FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "status" }, pairingKey: own);
            Assert.Equal("FRIENDPC", status.Status!.MachineName);
            await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "status" }, pairingKey: CommandSigner.NewKey()));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "managed" }, pairingKey: own));
            Assert.Contains("managed one way", refused.Message);
        }
    }

    [Fact]
    public async Task Release_SwitchesTheManagedPcToANewCode()
    {
        var own = CommandSigner.NewKey();
        var keys = new MemoryAgentKeys(() => own, managed: true);
        var port = TestPorts.FreeRange(2);
        using var server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = "FRIENDPC" }), c => c.Id, _ => Task.FromResult(new LogPayload()),
            prefix: $"http://localhost:{port}/rw/", discoveryPort: port + 1, machineKey: "FRIENDPC", keys: keys);
        server.Start();

        await FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "release", From = "HOSTPC" }, pairingKey: own);

        Assert.False(keys.IsManaged);
        Assert.NotEqual(own, keys.Current);
        await Assert.ThrowsAsync<PairingMismatchException>(() =>
            FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "status" }, pairingKey: own));
    }

    [Fact]
    public async Task FleetMember_CantBeReleased()
    {
        var fleet = CommandSigner.NewKey();
        var (server, port) = Start(() => fleet, managed: false);
        using (server)
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "release" }, pairingKey: fleet));
            Assert.Contains("member of the fleet", refused.Message);
            await FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "status" }, pairingKey: fleet);
        }
    }
}

public class ManagedMergeTests
{
    private static ManagedPcShare Share(string name, byte[] key, DateTime added, string machineId = "") => new()
    {
        Name = name, Address = "100.64.0.5:47821", MachineId = machineId, AddedUtc = added, Key = Convert.ToBase64String(key)
    };

    [Fact]
    public void NewPcs_AreTakenIn_Once()
    {
        var config = new FleetConfig();
        var key = CommandSigner.NewKey();

        Assert.True(config.MergeManaged(new[] { Share("FRIENDPC", key, DateTime.UtcNow) }, Array.Empty<RemovedManagedPc>()));
        Assert.False(config.MergeManaged(new[] { Share("FRIENDPC", key, DateTime.UtcNow) }, Array.Empty<RemovedManagedPc>()));

        var pc = Assert.Single(config.ManagedPcs);
        Assert.Equal("FRIENDPC", pc.Name);
        Assert.Equal(key, pc.Key);
        Assert.Equal(CommandSigner.KeyId(key), pc.KeyId);
        // The code is kept encrypted, never in plain.
        Assert.DoesNotContain(Convert.ToBase64String(key), pc.ProtectedKey);
    }

    [Fact]
    public void Removals_Spread_AndARemovedCodeIsNotTakenInAgain()
    {
        var config = new FleetConfig();
        var key = CommandSigner.NewKey();
        config.MergeManaged(new[] { Share("FRIENDPC", key, DateTime.UtcNow) }, Array.Empty<RemovedManagedPc>());
        var removed = new[] { new RemovedManagedPc { KeyId = CommandSigner.KeyId(key), RemovedUtc = DateTime.UtcNow } };

        Assert.True(config.MergeManaged(Array.Empty<ManagedPcShare>(), removed));
        Assert.Empty(config.ManagedPcs);
        // Another fleet PC that hasn't heard yet still shares it: it stays removed.
        Assert.False(config.MergeManaged(new[] { Share("FRIENDPC", key, DateTime.UtcNow) }, Array.Empty<RemovedManagedPc>()));
        Assert.Empty(config.ManagedPcs);
    }

    [Fact]
    public void OfTwoPairingsOfOnePc_TheNewerWins()
    {
        var config = new FleetConfig();
        var older = CommandSigner.NewKey();
        var newer = CommandSigner.NewKey();
        var now = DateTime.UtcNow;
        config.MergeManaged(new[] { Share("FRIENDPC", older, now.AddDays(-1), "id1") }, Array.Empty<RemovedManagedPc>());

        config.MergeManaged(new[] { Share("FRIENDPC", newer, now, "id1") }, Array.Empty<RemovedManagedPc>());
        Assert.Equal(CommandSigner.KeyId(newer), Assert.Single(config.ManagedPcs).KeyId);
        Assert.Contains(config.RemovedManagedPcs, r => r.KeyId == CommandSigner.KeyId(older));

        // The older one arriving from a PC that hasn't heard is ignored.
        config.MergeManaged(new[] { Share("FRIENDPC", older, now.AddDays(-1), "id1") }, Array.Empty<RemovedManagedPc>());
        Assert.Equal(CommandSigner.KeyId(newer), Assert.Single(config.ManagedPcs).KeyId);
    }

    [Fact]
    public void BrokenShares_AreIgnored()
    {
        var config = new FleetConfig();
        Assert.False(config.MergeManaged(new[]
        {
            new ManagedPcShare { Name = "A", Key = "not base64!" },
            new ManagedPcShare { Name = "B", Key = Convert.ToBase64String(new byte[5]) },
            new ManagedPcShare { Name = "", Key = Convert.ToBase64String(CommandSigner.NewKey()) }
        }, new[] { new RemovedManagedPc { KeyId = "" } }));
        Assert.Empty(config.ManagedPcs);
    }
}

/// <summary>The fleet side: a managed PC is listed and controlled with its own code.</summary>
[Collection(FleetSettingsCollection.Name)]
public class ManagedFleetTests
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    private static async Task WithConfigAsync(Func<Task> test)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        var saved = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;
        // Start without other tests' PCs, so only the test's own agents are contacted.
        if (saved != null) File.Delete(ConfigPath);
        try { await test(); }
        finally
        {
            if (saved != null)
            {
                File.WriteAllText(ConfigPath, saved);
                File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddDays(-1).AddTicks(Random.Shared.Next()));
            }
            else if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        }
    }

    [Fact]
    public async Task ManagedPc_IsListed_AndGetsCommands_WithItsOwnCode()
    {
        await WithConfigAsync(async () =>
        {
            var own = CommandSigner.NewKey();
            using var friend = new TestAgent("FRIENDPC", key: own);
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            FleetConfig.AddManagedPc("FRIENDPC", friend.Address, own);

            var machines = await MachineRelay.ReadFleetAsync(forceDiscovery: true);
            var listed = Assert.Single(machines, m => m.MachineName == "FRIENDPC");
            Assert.True(listed.IsManaged);
            Assert.Empty(listed.ConnectionProblem);

            await MachineRelay.SendCommandAsync("FRIENDPC", "runner", "r1", "Start");
            Assert.Single(friend.Commands);
        });
    }

    [Fact]
    public async Task APcThatMissesOneAnswer_StaysOnline_ForAWhile()
    {
        await WithConfigAsync(async () =>
        {
            var own = CommandSigner.NewKey();
            var friend = new TestAgent("FRIENDPC", key: own);
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            FleetConfig.AddManagedPc("FRIENDPC", friend.Address, own);
            Assert.Empty(Assert.Single(await MachineRelay.ReadFleetAsync(), m => m.MachineName == "FRIENDPC").ConnectionProblem);

            // It stops answering (a slow answer looks the same): shown online for now, without a problem.
            friend.Dispose();
            var missed = Assert.Single(await MachineRelay.ReadFleetAsync(), m => m.MachineName == "FRIENDPC");
            Assert.Empty(missed.ConnectionProblem);
            Assert.True(missed.IsOnline);
        });
    }

    [Fact]
    public async Task ManagedPc_CantTakeAFleetPcsName()
    {
        await WithConfigAsync(async () =>
        {
            var own = CommandSigner.NewKey();
            using var friend = new TestAgent("LAPTOPX", key: own);
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            FleetConfig.Update(c => c.KnownPeers.Add(new PeerRecord { Name = "LAPTOPX", Address = "127.0.0.1:1", LastSeenUtc = DateTime.UtcNow }));
            FleetConfig.AddManagedPc("LAPTOPX", friend.Address, own);

            var machines = await MachineRelay.ReadFleetAsync(forceDiscovery: true);
            Assert.Contains(machines, m => m.MachineKey == "MANAGED_LAPTOPX" && m.ConnectionProblem.Contains("same name"));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => MachineRelay.SendCommandAsync("LAPTOPX", "runner", "r1", "Start"));
            Assert.Contains("same name", refused.Message);
            Assert.Empty(friend.Commands);
        });
    }

    [Fact]
    public async Task StopManaging_TellsThePc_AndForgetsIt()
    {
        await WithConfigAsync(async () =>
        {
            var own = CommandSigner.NewKey();
            var keys = new MemoryAgentKeys(() => own, managed: true);
            var port = TestPorts.FreeRange(2);
            using var server = new AgentServer(
                () => Task.FromResult(new MachineStatusPayload { MachineName = "FRIENDPC" }), c => c.Id, _ => Task.FromResult(new LogPayload()),
                prefix: $"http://localhost:{port}/rw/", discoveryPort: port + 1, machineKey: "FRIENDPC", keys: keys);
            server.Start();
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            FleetConfig.AddManagedPc("FRIENDPC", $"localhost:{port}", own);

            Assert.True(await MachineRelay.RemoveManagedPcAsync("FRIENDPC"));

            Assert.False(keys.IsManaged);
            Assert.Empty(FleetConfig.Current.ManagedPcs);
            Assert.Contains(FleetConfig.Current.RemovedManagedPcs, r => r.KeyId == CommandSigner.KeyId(own));
            Assert.Contains(FleetConfig.Current.ShareManaged().Removed, r => r.KeyId == CommandSigner.KeyId(own));
        });
    }

    [Fact]
    public async Task FleetMembers_ShareTheirManagedPcs()
    {
        await WithConfigAsync(async () =>
        {
            var fleet = CommandSigner.NewKey();
            using var member = new TestAgent("MEMBERPC", key: fleet);
            FleetConfig.SetFleetKey(fleet);
            var own = CommandSigner.NewKey();
            FleetConfig.AddManagedPc("FRIENDPC", "100.64.0.5:47821", own);

            var answer = await FleetNetwork.CallAsync(member.Address, "MEMBERPC", new FleetRequest { Op = "managed" }, pairingKey: fleet);

            var shared = Assert.Single(answer.ManagedPcs!);
            Assert.Equal("FRIENDPC", shared.Name);
            Assert.Equal(own, Convert.FromBase64String(shared.Key));
        });
    }

    [Fact]
    public async Task ChangeFleetCode_GivesManagedPcsNewCodesOfTheirOwn()
    {
        await WithConfigAsync(async () =>
        {
            var own = CommandSigner.NewKey();
            var keys = new MemoryAgentKeys(() => own, managed: true);
            var port = TestPorts.FreeRange(2);
            using var server = new AgentServer(
                () => Task.FromResult(new MachineStatusPayload { MachineName = "FRIENDPC" }), c => c.Id, _ => Task.FromResult(new LogPayload()),
                prefix: $"http://localhost:{port}/rw/", discoveryPort: port + 1, machineKey: "FRIENDPC", keys: keys);
            server.Start();
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            FleetConfig.AddManagedPc("FRIENDPC", $"localhost:{port}", own);

            var result = await MachineRelay.ChangeFleetCodeAsync(Array.Empty<MachineInfo>(), Array.Empty<MachineInfo>(), _ => { });

            Assert.Equal(new[] { "FRIENDPC" }, result.ManagedSwitched);
            var pc = Assert.Single(FleetConfig.Current.ManagedPcs);
            Assert.NotEqual(own, pc.Key);
            Assert.Equal(pc.Key, keys.Current);
            await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync($"localhost:{port}", "FRIENDPC", new FleetRequest { Op = "status" }, pairingKey: own));
        });
    }

    [Fact]
    public void JoiningOneWay_AndDisconnecting()
    {
        WithConfigAsync(() =>
        {
            var own = CommandSigner.NewKey();
            FleetConfig.BecomeManaged(own, "HOSTPC");
            Assert.Equal("HOSTPC", FleetConfig.Current.ManagedBy);
            Assert.Equal(own, FleetConfig.Current.FleetKey);

            FleetConfig.StopBeingManaged();
            Assert.Empty(FleetConfig.Current.ManagedBy);
            Assert.NotEqual(own, FleetConfig.Current.FleetKey);
            Assert.NotNull(FleetConfig.Current.FleetKey);

            // Joining a fleet in full ends being managed too.
            FleetConfig.BecomeManaged(own, "HOSTPC");
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            Assert.Empty(FleetConfig.Current.ManagedBy);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }
}

public class StatusCacheTests
{
    private static MachineStatusPayload Payload(int n) => new() { MachineName = "PC", LastCommandId = n.ToString() };

    [Fact]
    public async Task ASlowBuild_DoesNotHoldUpRequests_OnceThereIsAStatus()
    {
        var builds = 0;
        var cache = new StatusCache(async () =>
        {
            var n = Interlocked.Increment(ref builds);
            await Task.Delay(n == 1 ? 0 : 1500);
            return Payload(n);
        });
        Assert.Equal("1", (await cache.GetAsync()).LastCommandId);

        await Task.Delay(StatusCache.FreshFor + TimeSpan.FromMilliseconds(100));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // Old by now: answered right away with the last status, while the next one is built.
        Assert.Equal("1", (await cache.GetAsync()).LastCommandId);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
        // Requests meanwhile don't start more builds.
        await cache.GetAsync();
        Assert.Equal(2, builds);

        await Task.Delay(2000);
        Assert.Equal("2", (await cache.GetAsync()).LastCommandId);
    }

    [Fact]
    public async Task AfterAChange_ARequestWaitsForTheStatusThatShowsIt()
    {
        var builds = 0;
        var cache = new StatusCache(async () =>
        {
            var n = Interlocked.Increment(ref builds);
            await Task.Delay(n == 1 ? 0 : 300);
            return Payload(n);
        });
        await cache.GetAsync();

        cache.Invalidate();
        Assert.Equal("2", (await cache.GetAsync()).LastCommandId);
    }

    [Fact]
    public async Task AfterAChange_ABuildThatTakesTooLong_GivesTheLastStatus()
    {
        var builds = 0;
        var cache = new StatusCache(async () =>
        {
            var n = Interlocked.Increment(ref builds);
            await Task.Delay(n == 1 ? 0 : 5000);
            return Payload(n);
        }) { ChangeWait = TimeSpan.FromMilliseconds(200) };
        await cache.GetAsync();

        cache.Invalidate();
        Assert.Equal("1", (await cache.GetAsync()).LastCommandId);
    }
}
