using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace RunnerManager.Tests;

public class RunnerUrlTests
{
    [Theory]
    [InlineData("https://github.com/Owner/repo", "Owner/repo")]
    [InlineData("https://github.com/Owner/repo.git", "Owner/repo")]
    [InlineData("https://github.com/Owner/my.github-tools", "Owner/my.github-tools")]
    [InlineData("https://github.com/Owner/repo/tree/main", "Owner/repo")]
    [InlineData("https://github.com/Owner", "")]
    [InlineData("https://gitlab.com/a/b", "")]
    [InlineData("not a url", "")]
    public void RepositoryPath_ParsesGitHubUrls(string url, string expected)
        => Assert.Equal(expected, RunnerService.RepositoryPath(url));

    [Theory]
    [InlineData("https://github.com/Owner/repo", "repos/Owner/repo")]
    [InlineData("https://github.com/my-org", "orgs/my-org")]
    [InlineData("https://github.com/orgs/my-org", "orgs/my-org")]
    [InlineData("https://github.com/orgs/my-org/settings", "orgs/my-org")]
    [InlineData("https://example.com/x", "")]
    public void ApiScope_DistinguishesRepositoryAndOrganization(string url, string expected)
        => Assert.Equal(expected, RunnerService.ApiScope(url));

    [Theory]
    [InlineData("https://github.com/orgs/my-org", "https://github.com/my-org")]
    [InlineData("https://github.com/Owner/repo.git", "https://github.com/Owner/repo")]
    public void NormalizeRegistrationUrl_ProducesConfigUrl(string url, string expected)
        => Assert.Equal(expected, RunnerService.NormalizeRegistrationUrl(url));
}

public class CommandRelayTests
{
    [Theory]
    [InlineData("runner Stop failed: Access is denied.", true)]
    [InlineData("agent Update failed: no release", true)]
    [InlineData("Runner edit completed: build-failed-tests", false)]
    [InlineData("Runner created: failed", false)]
    [InlineData("rdc Restart failed: timeout", false)]
    public void FailedResults_AreRecognizedByTheAgentFormat(string result, bool expected)
        => Assert.Equal(expected, MachineRelay.IsFailedResult(result));

    [Fact]
    public void Fingerprint_IgnoresHeartbeatAndLoadButSeesStateChanges()
    {
        var p1 = new MachineStatusPayload { MachineName = "PC", UpdatedUtc = DateTime.UtcNow, CpuPercent = 5, MemoryPercent = 40 };
        var p2 = p1 with { UpdatedUtc = DateTime.UtcNow.AddMinutes(1), CpuPercent = 90, MemoryPercent = 80 };
        Assert.Equal(p1.Fingerprint(), p2.Fingerprint());
        Assert.NotEqual(p1.Fingerprint(), (p1 with { RebootPending = true }).Fingerprint());
        Assert.NotEqual(p1.Fingerprint(), (p1 with { WatchdogEnabled = true }).Fingerprint());
    }
}

public class FleetCryptoTests
{
    private static byte[] TransportKey() => FleetCrypto.TransportKey(CommandSigner.NewKey());

    [Fact]
    public void SealedRequest_OpensWithSameKeyAndContext()
    {
        var key = TransportKey();
        var context = FleetCrypto.RequestContext("LAPTOP", 1234);
        var sealedBytes = FleetCrypto.Seal(key, "hello"u8.ToArray(), context);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(FleetCrypto.Open(key, sealedBytes, context)!));
    }

    [Fact]
    public void SealedRequest_FailsWithOtherKeyContextOrTampering()
    {
        var key = TransportKey();
        var context = FleetCrypto.RequestContext("LAPTOP", 1234);
        var sealedBytes = FleetCrypto.Seal(key, "hello"u8.ToArray(), context);
        Assert.Null(FleetCrypto.Open(TransportKey(), sealedBytes, context));
        Assert.Null(FleetCrypto.Open(key, sealedBytes, FleetCrypto.RequestContext("DESKTOP", 1234)));
        Assert.Null(FleetCrypto.Open(key, sealedBytes, FleetCrypto.RequestContext("LAPTOP", 1235)));
        var tampered = (byte[])sealedBytes.Clone();
        tampered[^1] ^= 1;
        Assert.Null(FleetCrypto.Open(key, tampered, context));
        Assert.Null(FleetCrypto.Open(key, new byte[5], context));
    }

    [Fact]
    public void Responses_AreBoundToTheirRequest()
    {
        var key = TransportKey();
        var request = FleetCrypto.Seal(key, Array.Empty<byte>(), "req");
        var response = FleetCrypto.Seal(key, "ok"u8.ToArray(), FleetCrypto.ResponseContext(FleetCrypto.NonceOf(request)));
        Assert.NotNull(FleetCrypto.Open(key, response, FleetCrypto.ResponseContext(FleetCrypto.NonceOf(request))));
        var other = FleetCrypto.Seal(key, Array.Empty<byte>(), "req");
        Assert.Null(FleetCrypto.Open(key, response, FleetCrypto.ResponseContext(FleetCrypto.NonceOf(other))));
    }

    [Fact]
    public void Freshness_RejectsOldAndFutureTimestamps()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(FleetCrypto.IsFresh(now.ToUnixTimeMilliseconds()));
        Assert.False(FleetCrypto.IsFresh(now.AddMinutes(-5).ToUnixTimeMilliseconds()));
        Assert.False(FleetCrypto.IsFresh(now.AddMinutes(5).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void ReplayGuard_AcceptsEachNonceOnce()
    {
        var guard = new ReplayGuard();
        Assert.True(guard.TryUse("abc"));
        Assert.False(guard.TryUse("abc"));
        Assert.True(guard.TryUse("def"));
    }

    [Fact]
    public void Discovery_OnlyFleetMembersCanProbeAndAnswer()
    {
        var pairing = CommandSigner.NewKey();
        var key = FleetCrypto.DiscoveryKey(pairing);
        var probe = FleetCrypto.Probe(key, out var nonce);
        Assert.Equal(nonce, FleetCrypto.VerifyProbe(key, probe));
        Assert.Null(FleetCrypto.VerifyProbe(FleetCrypto.DiscoveryKey(CommandSigner.NewKey()), probe));

        var answer = FleetCrypto.Answer(key, nonce, "{\"name\":\"LAPTOP\"}");
        Assert.Equal("{\"name\":\"LAPTOP\"}", FleetCrypto.VerifyAnswer(key, nonce, answer));
        Assert.Null(FleetCrypto.VerifyAnswer(key, new byte[16], answer));
        Assert.Null(FleetCrypto.VerifyAnswer(FleetCrypto.DiscoveryKey(CommandSigner.NewKey()), nonce, answer));
    }

    [Fact]
    public void PairingCode_FormatsAndParses()
    {
        var key = CommandSigner.NewKey();
        var text = CommandSigner.FormatKey(key);
        Assert.StartsWith("rwk_", text);
        Assert.Equal(key, CommandSigner.ParseKey("  " + text + " "));
        Assert.Null(CommandSigner.ParseKey("rwk_tooShort"));
        Assert.Null(CommandSigner.ParseKey("not-a-key"));
        Assert.Equal(8, CommandSigner.KeyId(key).Length);
        Assert.Equal(string.Empty, CommandSigner.KeyId(null));
    }
}

public class WorkflowManagerTests
{
    [Fact]
    public void HostedWindowsJobs_AreSwitchedToSelfHosted()
    {
        const string yaml = "jobs:\n  build:\n    runs-on: windows-latest # main build\n  lint:\n    runs-on: ubuntu-latest\n";
        var result = WorkflowManager.Transform(yaml, WorkflowManager.BuildSelector(new[] { "gpu" }), new[] { "gpu" });
        Assert.True(result.Changed);
        Assert.Contains("runs-on: [self-hosted, Windows, X64, gpu] # main build", result.Content);
        Assert.Contains("runs-on: ubuntu-latest", result.Content);
    }

    [Fact]
    public void CompatibleSelfHostedSelectors_AreKept()
    {
        const string yaml = "jobs:\n  a:\n    runs-on: [self-hosted, Windows]\n";
        var result = WorkflowManager.Transform(yaml, WorkflowManager.BuildSelector(Array.Empty<string>()), Array.Empty<string>());
        Assert.False(result.Changed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void IncompatibleAndDynamicSelectors_ProduceWarnings()
    {
        const string yaml = "jobs:\n  a:\n    runs-on: [self-hosted, linux]\n  b:\n    runs-on: ${{ matrix.os }}\n";
        var result = WorkflowManager.Transform(yaml, WorkflowManager.BuildSelector(Array.Empty<string>()), Array.Empty<string>());
        Assert.False(result.Changed);
        Assert.Equal(2, result.Warnings.Count);
    }
}

public class MiscTests
{
    [Fact]
    public void KeepRunningWhenClosed_IsOnForNewInstalls_AndAChoiceIsKept()
    {
        Assert.True(new AppSettings().CloseToTray);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.CloseToTray);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("""{"CloseToTray":false}""")!.CloseToTray);
    }

    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.1", "2.1.0")]
    [InlineData("2.1.0+abc123", "2.1.0")]
    [InlineData("2.1.0-beta.1", "2.1.0-beta.1")]
    [InlineData("v2.1-rc.2+abc", "2.1.0-rc.2")]
    public void Normalize_StripsPrefixesAndBuildMetadata(string input, string expected)
        => Assert.Equal(expected, AppUpdater.Normalize(input));

    [Theory]
    [InlineData("v2.1.0", "2.0.9", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("1.9.0", "2.0.0", false)]
    [InlineData("", "2.0.0", false)]
    [InlineData("2.4.0", "2.4.0-beta", true)]
    [InlineData("2.4.0-beta", "2.4.0", false)]
    [InlineData("2.4.0-beta.2", "2.4.0-beta.1", true)]
    [InlineData("2.4.0-beta.10", "2.4.0-beta.2", true)]
    [InlineData("2.4.0-rc.1", "2.4.0-beta.9", true)]
    [InlineData("2.4.0-beta.1", "2.4.0-beta", true)]
    [InlineData("2.4.0-beta", "2.4.0-1", true)]
    [InlineData("2.4.0-beta", "2.3.9", true)]
    [InlineData("2.4.0+abc", "2.4.0", false)]
    public void IsNewer_ComparesSemanticVersions(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppUpdater.IsNewer(candidate, current));

    [Theory]
    [InlineData("2.3.9", "2.4.0", true)]
    [InlineData("2.4.0-beta", "2.4.0", true)]
    [InlineData("2.4.0", "2.4.0", false)]
    [InlineData("junk", "2.4.0", false)]
    public void IsOlder_ComparesSemanticVersions(string candidate, string current, bool expected)
        => Assert.Equal(expected, AppUpdater.IsOlder(candidate, current));

    [Fact]
    public void LogTruncate_KeepsTheNewestLinesWithinBudget()
    {
        var lines = Enumerable.Range(1, 5000).Select(i => $"line {i} " + new string('x', 40));
        var text = LogReader.Truncate(lines);
        Assert.EndsWith("line 5000 " + new string('x', 40), text);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(text) <= 30_000);
        Assert.DoesNotContain("line 1 ", text);
    }

    [Theory]
    [InlineData("Remove runner failed: boom", ActivityKind.Error)]
    [InlineData("BUILD-01 went offline", ActivityKind.Warning)]
    [InlineData("Restart runner completed.", ActivityKind.Success)]
    [InlineData("Runner Manager started.", ActivityKind.Info)]
    public void Activity_IsClassifiedByMessage(string message, ActivityKind expected)
        => Assert.Equal(expected, ActivityEntry.Classify(message));

    [Theory]
    [InlineData(10, 500, true)]
    [InlineData(30, 300, false)]
    [InlineData(20, 1000, true)]
    public void Disk_IsLowBelowAbsoluteOrRelativeThreshold(double freeGb, double totalGb, bool expected)
        => Assert.Equal(expected, new DiskStatus { Name = "C:", FreeGb = freeGb, TotalGb = totalGb }.IsLow);
}

public class RunnerRemovalTests
{
    [Theory]
    [InlineData("gh: Not Found (HTTP 404)", true)]
    [InlineData("HTTP 404: Not Found (https://api.github.com/repos/x/y/actions/runners/remove-token)", true)]
    [InlineData("The repository does not exist", true)]
    [InlineData("gh: Bad credentials (HTTP 401)", false)]
    [InlineData("Runner configuration failed.", false)]
    public void GoneFromGitHub_IsDetectedFromErrors(string message, bool expected)
        => Assert.Equal(expected, RunnerService.IsGoneFromGitHub(message));

    [Fact]
    public void CommandKey_UsesServiceNameOrFolder()
    {
        Assert.Equal("actions.runner.a.b", new MachineRunnerStatus { ServiceName = "actions.runner.a.b", InstallPath = @"C:\r" }.CommandKey);
        Assert.Equal(@"C:\r", new MachineRunnerStatus { InstallPath = @"C:\r" }.CommandKey);
    }
}

public class FleetNetworkTests
{
    private static async Task<(AgentServer Server, byte[] Key, List<MachineCommand> Commands, int Port)> StartAgentAsync()
    {
        var key = CommandSigner.NewKey();
        var commands = new List<MachineCommand>();
        var port = TestPorts.FreeRange(3);
        var server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = "TESTPC", AgentProtocolVersion = MachineRelay.ProtocolVersion, FleetAgentRunning = true }),
            command => { lock (commands) commands.Add(command); return command.Id; },
            logKey => Task.FromResult(new LogPayload { Title = $"log {logKey}", Text = "line 1\nline 2" }),
            () => key, $"http://localhost:{port}/rw/", port + 1, "TESTPC");
        server.Start();
        await Task.Delay(100);
        return (server, key, commands, port);
    }

    [Fact]
    public async Task Status_Command_And_Log_RoundTripOverHttp()
    {
        var (server, key, commands, port) = await StartAgentAsync();
        using (server)
        {
            var address = $"localhost:{port}";
            var status = await FleetNetwork.CallAsync(address, "TESTPC", new FleetRequest { Op = "status" }, pairingKey: key);
            Assert.Equal("TESTPC", status.Status!.MachineName);

            var identified = await FleetNetwork.CallAsync(address, "*", new FleetRequest { Op = "status" }, pairingKey: key);
            Assert.Equal("TESTPC", identified.Status!.MachineName);

            var command = new MachineCommand { Id = "cmd1", Target = "runner", Action = "Stop", TargetKey = "svc" };
            var queued = await FleetNetwork.CallAsync(address, "TESTPC", new FleetRequest { Op = "command", Command = command }, pairingKey: key);
            Assert.Equal("cmd1", queued.CommandId);
            Assert.Single(commands);

            var log = await FleetNetwork.CallAsync(address, "TESTPC", new FleetRequest { Op = "log", LogKey = "agent" }, pairingKey: key);
            Assert.Equal("log agent", log.Log!.Title);
        }
    }

    [Fact]
    public async Task WrongPairingCode_OrWrongTarget_IsRejected()
    {
        var (server, key, commands, port) = await StartAgentAsync();
        using (server)
        {
            var address = $"localhost:{port}";
            var wrongCode = await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync(address, "TESTPC", new FleetRequest { Op = "status" }, pairingKey: CommandSigner.NewKey()));
            Assert.Contains("pairing code does not match", wrongCode.Message);
            var wrongPc = await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync(address, "OTHERPC", new FleetRequest { Op = "status" }, pairingKey: key));
            Assert.Contains("wrong PC", wrongPc.Message);
            // "*" only identifies a PC; it can't be used to send commands.
            await Assert.ThrowsAsync<PairingMismatchException>(() =>
                FleetNetwork.CallAsync(address, "*", new FleetRequest { Op = "command", Command = new MachineCommand { Id = "x" } }, pairingKey: key));
            Assert.Empty(commands);
        }
    }

    [Fact]
    public async Task OversizedOrChunkedBodies_AreRejected()
    {
        var (server, _, _, port) = await StartAgentAsync();
        using (server)
        using (var http = new HttpClient())
        {
            async Task<HttpStatusCode> PostAsync(HttpContent content)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/rw/") { Content = content };
                message.Headers.Add("X-RW-Time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                message.Headers.Add("X-RW-To", "TESTPC");
                using var response = await http.SendAsync(message);
                return response.StatusCode;
            }

            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new ByteArrayContent(new byte[1024 * 1024 + 1])));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new ChunkedContent(new byte[100])));
        }
    }

    /// <summary>Content without a length, so it is sent chunked.</summary>
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    [Fact]
    public async Task Discovery_FindsTheAgentWithTheSameCodeOnly()
    {
        var (server, key, _, port) = await StartAgentAsync();
        using (server)
        {
            var found = await FleetNetwork.DiscoverAsync(new[] { "127.0.0.1" }, TimeSpan.FromSeconds(1), port + 1, key);
            Assert.Contains(found, p => p.Name == "TESTPC" && p.Address.EndsWith($":{port}"));
            var strangers = await FleetNetwork.DiscoverAsync(new[] { "127.0.0.1" }, TimeSpan.FromSeconds(0.5), port + 1, CommandSigner.NewKey());
            Assert.Empty(strangers);
        }
    }
}

public class PairingTests
{
    [Fact]
    public void ComparisonCode_IsSixDigitsAndDependsOnEverything()
    {
        var shared = new byte[32];
        var a = new byte[] { 1 };
        var b = new byte[] { 2 };
        var code = Pairing.ComparisonCode(shared, a, b);
        Assert.Matches(@"^\d{3} \d{3}$", code);
        Assert.NotEqual(code, Pairing.ComparisonCode(shared, b, a));
        Assert.NotEqual(code, Pairing.ComparisonCode(new byte[] { 9 }, a, b));
    }

    [Fact]
    public async Task Join_TransfersTheCodeWhenAllowed_AndBothSidesShowTheSameNumber()
    {
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(3);
        using var host = new Pairing.PairingHost(key, "HOSTPC", port, port + 1);
        string? hostCode = null;
        host.JoinRequested += request => { hostCode = request.Code; request.Allow(); };
        host.Start();

        var hosts = await Pairing.FindHostsAsync(TimeSpan.FromSeconds(1), port + 1);
        var found = Assert.Single(hosts, h => h.Name == "HOSTPC");
        string? joinerCode = null;
        var result = await Pairing.JoinAsync(found.Address, "NEWPC", code => joinerCode = code, CancellationToken.None);

        Assert.Equal(key, result.PairingKey);
        Assert.Equal("HOSTPC", result.HostName);
        Assert.NotNull(joinerCode);
        Assert.Equal(hostCode, joinerCode);
    }

    [Fact]
    public async Task Join_FailsWhenDenied()
    {
        var port = TestPorts.FreeRange(3);
        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port, port + 1);
        host.JoinRequested += request => request.Deny();
        host.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pairing.JoinAsync($"127.0.0.1:{port}", "NEWPC", _ => { }, CancellationToken.None));
    }
}

public class DiscoveryRobustnessTests
{
    [Fact]
    public async Task FindingHosts_SurvivesUnreachableTargets_AndStillFindsTheHost()
    {
        // Probes also go to loopback and unreachable addresses; Windows answers those with "connection reset",
        // which used to abort the whole search.
        var port = TestPorts.FreeRange(3);
        var silentPort = port + 1; // nobody listens here
        Assert.Empty(await Pairing.FindHostsAsync(TimeSpan.FromSeconds(0.5), silentPort));

        using var host = new Pairing.PairingHost(CommandSigner.NewKey(), "HOSTPC", port + 2, port);
        host.Start();
        var found = await Pairing.FindHostsAsync(TimeSpan.FromSeconds(1), port, new[] { "127.0.0.1" });
        Assert.Contains(found, h => h.Name == "HOSTPC");
    }

    [Fact]
    public async Task AgentDiscovery_SurvivesUnreachablePeers()
    {
        var found = await FleetNetwork.DiscoverAsync(new[] { "127.0.0.1", "127.0.0.2" }, TimeSpan.FromSeconds(0.5),
            49990 + Random.Shared.Next(9), CommandSigner.NewKey());
        Assert.Empty(found);
    }
}

[Collection(FleetSettingsCollection.Name)]
public class LeaveTests
{
    [Fact]
    public async Task LeaveRequest_IsAcceptedByTheAgent()
    {
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(3);
        using var server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = "TESTPC" }), c => c.Id,
            _ => Task.FromResult(new LogPayload()), () => key, $"http://localhost:{port}/rw/", port + 1, "TESTPC");
        server.Start();
        // Forgetting a PC that isn't known is a no-op, so this doesn't touch real peers.
        var response = await FleetNetwork.CallAsync($"localhost:{port}", "TESTPC",
            new FleetRequest { Op = "leave", From = "NOT-A-REAL-PC-" + Guid.NewGuid().ToString("N") }, pairingKey: key);
        Assert.True(response.Ok);
    }
}

/// <summary>Tests that read or write fleet.json (in the test state folder) run one at a time.</summary>
[CollectionDefinition(Name)]
public class FleetSettingsCollection
{
    public const string Name = "Fleet settings";
}

[Collection(FleetSettingsCollection.Name)]
public class FleetConfigTests
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    /// <summary>Writes fleet.json directly, with a distinct time stamp so the cached config is always re-read.</summary>
    private static void WriteRaw(string json)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        WithRetry(() =>
        {
            File.WriteAllText(ConfigPath, json);
            File.SetLastWriteTimeUtc(ConfigPath, DateTime.UtcNow.AddDays(-1).AddTicks(Random.Shared.Next()));
        });
    }

    private static void DeleteConfig() => WithRetry(() => File.Delete(ConfigPath));

    /// <summary>Antivirus scanners (Windows Defender on CI machines) briefly hold new files open, so the tests' own file steps retry.</summary>
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

    /// <summary>Checks that fleet.json has no "FleetKey" property and no value holding the key in plain text.</summary>
    private static JsonElement AssertNoPlainKey(byte[] key)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var root = document.RootElement.Clone();
        Assert.False(root.TryGetProperty("FleetKey", out _));
        Assert.DoesNotContain(root.EnumerateObject(), p =>
            p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == Convert.ToBase64String(key));
        return root;
    }

    [Fact]
    public void FleetKey_IsNotWrittenToFleetJson()
    {
        var key = CommandSigner.NewKey();
        try
        {
            FleetConfig.SetFleetKey(key);
            AssertNoPlainKey(key);
            Assert.Equal(key, FleetConfig.Current.FleetKey);
        }
        finally { DeleteConfig(); }
    }

    [Fact]
    public void PlainFleetKeyFromOlderBuilds_IsRemovedWhenLoaded()
    {
        var key = CommandSigner.NewKey();
        var protectedKey = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        try
        {
            WriteRaw($"{{\"ProtectedFleetKey\":\"{protectedKey}\",\"Watchdog\":false,\"FleetKey\":\"{Convert.ToBase64String(key)}\"}}");
            var config = FleetConfig.Current;
            Assert.Equal(key, config.FleetKey);
            Assert.False(config.Watchdog);
            var root = AssertNoPlainKey(key);
            Assert.Equal(protectedKey, root.GetProperty("ProtectedFleetKey").GetString());
        }
        finally { DeleteConfig(); }
    }

    [Fact]
    public void LockedSettings_AreNotReplacedWithDefaults()
    {
        const string saved = "{\"Watchdog\":false}";
        try
        {
            WriteRaw(saved);
            using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<IOException>(() => FleetConfig.Update(c => c.ManualAddresses.Add("example-pc")));
            Assert.Equal(saved, File.ReadAllText(ConfigPath));
        }
        finally { DeleteConfig(); }
    }

    [Fact]
    public void DamagedSettings_AreKeptAsBackup_AndSavingContinues()
    {
        const string broken = "{\"Watchdog\":false,\"ManualAddresses\":[";
        try
        {
            WriteRaw(broken);
            FleetConfig.Update(c => c.ManualAddresses.Add("example-pc"));
            var backup = Assert.Single(Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*"));
            Assert.Equal(broken, File.ReadAllText(backup));
            Assert.Contains("example-pc", FleetConfig.Current.ManualAddresses);
        }
        finally
        {
            DeleteConfig();
            foreach (var file in Directory.GetFiles(FleetConfig.StateDir, "fleet.json.damaged-*")) WithRetry(() => File.Delete(file));
        }
    }

    [Fact]
    public void MissingSettings_GiveDefaults_AndUpdatesAreSaved()
    {
        try
        {
            DeleteConfig();
            Assert.True(FleetConfig.Current.Watchdog);
            FleetConfig.Update(c => c.ManualAddresses.Add("example-pc"));
            Assert.Contains("example-pc", File.ReadAllText(ConfigPath));
            Assert.Contains("example-pc", FleetConfig.Current.ManualAddresses);
            Assert.Empty(Directory.GetFiles(FleetConfig.StateDir, "fleet.json.*.tmp"));
        }
        finally { DeleteConfig(); }
    }
}

/// <summary>
/// Ports for network tests. Each one is checked, because Windows reserves some ranges (Hyper-V, WinNAT), as on CI
/// machines. They come from below the dynamic range, so sockets bound to port 0 during a test cannot take them.
/// </summary>
internal static class TestPorts
{
    /// <summary>A start port where start..start+count-1 are free for both TCP and UDP.</summary>
    public static int FreeRange(int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var start = Random.Shared.Next(20000, 40000);
            if (Enumerable.Range(start, count).All(IsFree)) return start;
        }
        throw new InvalidOperationException("No free port range found.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Any, port);
            tcp.Start();
            tcp.Stop();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException) { return false; }
    }
}

public class MachineCardTests
{
    [Fact]
    public void CollapsingAPc_HidesItsHealthAndRunners_AndAppliesToItsNextStatus()
    {
        var saved = MachineInfo.CollapsedPcs;
        MachineInfo.CollapsedPcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var payload = new MachineStatusPayload { MachineName = "PC-A", AgentProtocolVersion = 13, Disks = new[] { new DiskStatus { Name = "C:" } } };
            var machine = MachineInfo.FromPayload("a", payload);
            Assert.Equal(System.Windows.Visibility.Visible, machine.BodyVisibility);
            Assert.Equal(System.Windows.Visibility.Visible, machine.HealthVisibility);

            Assert.True(machine.ToggleCollapsed());
            Assert.Equal(System.Windows.Visibility.Collapsed, machine.BodyVisibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, machine.HealthVisibility);
            // The card is rebuilt from each new status; it stays collapsed.
            Assert.True(MachineInfo.FromPayload("a", payload with { MachineName = "pc-a" }).IsCollapsed);

            Assert.False(machine.ToggleCollapsed());
            Assert.Equal(System.Windows.Visibility.Visible, machine.BodyVisibility);
        }
        finally { MachineInfo.CollapsedPcs = saved; }
    }

    [Fact]
    public void PendingPcAction_ShowsInTheStatusPill()
    {
        var machine = MachineInfo.FromPayload("a", new MachineStatusPayload { MachineName = "PC-A" });
        machine.SetPending("Reboot");
        Assert.Equal("Restarting PC…", machine.StatusText);
        machine.SetPending("Update");
        Assert.Equal("Updating…", machine.StatusText);
        Assert.False(machine.CanUpdateAgent);
    }
}

public class TaskIndexTests
{
    [Fact]
    public void MissingTask_IsNotReportedAsUnindexed()
        // Only a task that exists and is missing from Task Scheduler's index is repaired.
        => Assert.Null(ScheduledTasks.IsIndexed("Runner Manager test " + Guid.NewGuid().ToString("N")));
}
