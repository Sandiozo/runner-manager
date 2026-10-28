using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager.Tests;

// Some of these tests read or change the assistant settings in fleet.json.
[Collection(FleetSettingsCollection.Name)]
public class AssistantTests
{
    [Theory]
    [InlineData("octocat/web-app", "octocat/web-app", false)]
    [InlineData("https://github.com/octocat/web-app", "octocat/web-app", false)]
    [InlineData("https://github.com/octocat/web-app.git", "octocat/web-app", false)]
    [InlineData("github.com/octocat/web-app/", "octocat/web-app", false)]
    [InlineData("https://github.com/octo-org", "octo-org", true)]
    [InlineData("orgs/octo-org", "octo-org", true)]
    [InlineData("octocat/.github", "octocat/.github", false)]
    public void Targets_AcceptRepositoriesOrganizationsAndUrls(string text, string path, bool organization)
    {
        var target = AssistantTools.ParseTarget(text);
        Assert.Equal(path, target.Path);
        Assert.Equal(organization, target.IsOrganization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b/c")]
    [InlineData("octocat/web app")]
    [InlineData("octocat/$(whoami)")]
    [InlineData("../user")]
    [InlineData("octocat/..")]
    [InlineData("octocat/.")]
    [InlineData("..")]
    [InlineData("-octocat/web-app")]
    [InlineData("octo.cat/web-app")]
    [InlineData("octocat//web-app")]
    public void Targets_RejectAnythingElse(string text)
        => Assert.Throws<AssistantException>(() => AssistantTools.ParseTarget(text));

    [Fact]
    public void DefaultRunnerName_IsRepositoryAndPc()
    {
        var name = AssistantTools.DefaultRunnerName(new AssistantTools.Target("octocat/Web.App", false), new MachineInfo { MachineName = "BUILD 01" });
        Assert.Equal("web.app-build-01", name);
        Assert.Null(RunnerService.RunnerNameProblem(name));
    }

    [Fact]
    public void CommandLineArguments_BecomeToolArguments()
    {
        var arguments = AssistantHost.ParseArguments(new[] { "--repository", "octocat/web-app", "--labels", "gpu,cuda", "--even-if-exists", "--count=5" });
        Assert.Equal("octocat/web-app", arguments.GetProperty("repository").GetString());
        Assert.Equal("gpu,cuda", arguments.GetProperty("labels").GetString());
        Assert.True(arguments.GetProperty("even_if_exists").GetBoolean());
        Assert.Equal(5, arguments.GetProperty("count").GetInt32());
        Assert.Throws<AssistantException>(() => AssistantHost.ParseArguments(new[] { "stray" }));
    }

    [Fact]
    public void ToolArguments_AreReadSafely()
    {
        using var document = JsonDocument.Parse("""{"repository":"  octocat/web-app ","labels":["gpu"," ",""],"delete_files":"yes","count":99,"lines":"200","even_if_exists":"true","n":"x"}""");
        var call = new AssistantCall(document.RootElement, (_, _) => Task.FromResult(true), _ => { });
        Assert.Equal("octocat/web-app", call.Text("repository"));
        Assert.Equal(new[] { "gpu" }, call.TextList("labels"));
        Assert.False(call.Flag("delete_files")); // only true or "true" counts
        Assert.True(call.Flag("even_if_exists"));
        Assert.Equal(20, call.Number("count", 10, 1, 20));
        Assert.Equal(200, call.Number("lines", 150, 20, 400));
        Assert.Equal(7, call.Number("n", 7, 1, 20));
        Assert.Throws<AssistantException>(() => call.RequiredText("pc"));
    }

    [Fact]
    public async Task Approval_IsAlwaysAskedForDestructiveChanges_AndADeclineStopsTheTool()
    {
        var asked = new List<string>();
        using var document = JsonDocument.Parse("{}");
        var call = new AssistantCall(document.RootElement, (title, _) => { asked.Add(title); return Task.FromResult(false); }, _ => { });

        await call.ConfirmAsync(AssistantToolKind.Read, "Read", "message");
        var declined = await Assert.ThrowsAsync<AssistantException>(() => call.ConfirmAsync(AssistantToolKind.Destructive, "Delete a runner?", "message"));

        Assert.Equal(new[] { "Delete a runner?" }, asked);
        Assert.Contains("declined", declined.Message);
    }

    [Fact]
    public void EveryTool_HasAnObjectSchemaWithItsRequiredArguments()
    {
        Assert.Equal(AssistantTools.All.Count, AssistantTools.All.Select(t => t.Name).Distinct().Count());
        foreach (var tool in AssistantTools.All)
        {
            Assert.Equal("object", tool.InputSchema["type"]!.GetValue<string>());
            var properties = tool.InputSchema["properties"]!.AsObject();
            foreach (var required in tool.InputSchema["required"]?.AsArray() ?? new JsonArray())
                Assert.True(properties.ContainsKey(required!.GetValue<string>()), $"{tool.Name}: {required}");
            // Every tool that names a runner can also say which repository's, since names repeat across repositories.
            if (properties.ContainsKey("runner")) Assert.True(properties.ContainsKey("repository"), tool.Name);
        }
        Assert.Equal(AssistantToolKind.Destructive, AssistantTools.All.Single(t => t.Name == "remove_runner").Kind);
        Assert.Equal(AssistantToolKind.Destructive, AssistantTools.All.Single(t => t.Name == "move_runner").Kind);
        Assert.Equal(AssistantToolKind.Change, AssistantTools.All.Single(t => t.Name == "create_runner").Kind);
    }

    // ----- Finding PCs and runners -----

    [Fact]
    public void Pcs_AreFoundByNameButNeverGuessedWhenTwoLookAlike()
    {
        var machines = new[] { new MachineInfo { MachineName = "BUILD-01" }, new MachineInfo { MachineName = "BUILD_01" }, new MachineInfo { MachineName = "GPU" } };
        Assert.Equal("BUILD-01", AssistantTools.FindPc(machines, "build-01").MachineName);
        Assert.Equal("GPU", AssistantTools.FindPc(machines, "gpu").MachineName);
        var ambiguous = Assert.Throws<AssistantException>(() => AssistantTools.FindPc(machines, "build 01"));
        Assert.Contains("BUILD-01, BUILD_01", ambiguous.Message);
        Assert.Throws<AssistantException>(() => AssistantTools.FindPc(machines, "nope"));
    }

    [Fact]
    public void Runners_WithTheSameNameNeedTheirRepository()
    {
        var pc = Machine("BUILD", "2.4.0",
            Runner("build", "repos/octocat/web", "svc-web"),
            Runner("build", "repos/octocat/api", "svc-api"),
            Runner("other", "orgs/octo-org", "svc-org"));

        var ambiguous = Assert.Throws<AssistantException>(() =>
            AssistantTools.PickRunner(pc, "build", null, AssistantTools.RunnerCandidates(pc, "build", null, p => p)));
        Assert.Contains("octocat/web", ambiguous.Message);
        Assert.Contains("octocat/api", ambiguous.Message);
        Assert.Contains("repository", ambiguous.Message);

        var api = AssistantTools.ParseTarget("octocat/api");
        Assert.Equal("svc-api", AssistantTools.PickRunner(pc, "build", api, AssistantTools.RunnerCandidates(pc, "build", api, p => p)).ServiceName);
        // The service name is unique on the PC.
        Assert.Single(AssistantTools.RunnerCandidates(pc, "svc-web", null, p => p));
        var org = AssistantTools.ParseTarget("orgs/octo-org");
        Assert.Single(AssistantTools.RunnerCandidates(pc, "other", org, p => p));
    }

    [Fact]
    public void Runners_OfARenamedRepositoryStillMatchIt()
    {
        var pc = Machine("BUILD", "2.4.0", Runner("web", "repos/octocat/old-name", "svc-web"));
        var target = new AssistantTools.Target("octocat/new-name", false);
        Assert.Empty(AssistantTools.RunnersFor(new[] { pc }, target, p => p));
        Assert.Single(AssistantTools.RunnersFor(new[] { pc }, target, p => p == "octocat/old-name" ? "octocat/new-name" : p));
    }

    [Fact]
    public void RunsOn_UsesTheRunnersLabels_AndSaysWhenTheyAreUnknown()
    {
        var gpu = Machine("GPU", "2.4.0", Runner("a", "repos/octocat/web", "svc-a", "gpu", "cuda"), Runner("b", "repos/octocat/web", "svc-b", "gpu"));
        var both = gpu.Runners.Select(r => (gpu, r)).ToList();
        var (labels, note) = AssistantTools.RunsOn(both);
        Assert.Equal(new[] { "self-hosted", "Windows", "X64", "gpu" }, labels);
        Assert.Empty(note);

        var old = Machine("OLD", "2.3.1", Runner("c", "repos/octocat/web", "svc-c"));
        var (mixed, unknown) = AssistantTools.RunsOn(both.Append((old, old.Runners[0])).ToList());
        Assert.Equal(new[] { "self-hosted", "Windows", "X64" }, mixed);
        Assert.Contains("unknown", unknown);
    }

    [Fact]
    public void Creates_AreClaimedPerRepository_SoARetryMakesNoDuplicate()
    {
        var target = new AssistantTools.Target($"octocat/claim-{Guid.NewGuid():N}", false);
        var pc1 = new MachineInfo { MachineKey = "PC1", MachineName = "PC1" };
        var pc2 = new MachineInfo { MachineKey = "PC2", MachineName = "PC2" };

        using (AssistantTools.CreateClaims.Claim(target, pc1, evenIfExists: false))
        {
            var busy = Assert.Throws<AssistantException>(() => AssistantTools.CreateClaims.Claim(target, pc2, evenIfExists: false));
            Assert.Contains("already being created", busy.Message);
            // With even_if_exists another PC may get one, but never two at once on the same PC.
            using (AssistantTools.CreateClaims.Claim(target, pc2, evenIfExists: true)) { }
            Assert.Throws<AssistantException>(() => AssistantTools.CreateClaims.Claim(target, pc1, evenIfExists: true));
        }
        // A failed or declined create frees the claim; a finished one keeps it until fleet status shows the runner.
        using (var claim = AssistantTools.CreateClaims.Claim(target, pc1, evenIfExists: false)) claim.Succeeded();
        Assert.Contains("just created", Assert.Throws<AssistantException>(() => AssistantTools.CreateClaims.Claim(target, pc1, evenIfExists: false)).Message);
    }

    [Fact]
    public async Task McpServer_NegotiatesTheVersionAndListsTools()
    {
        var server = new McpServer((_, _, _) => Task.FromResult(false), _ => Task.CompletedTask);

        var init = await Handle(server, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"Test"}}}""");
        Assert.Equal("2025-03-26", init!["result"]!["protocolVersion"]!.GetValue<string>());
        var future = await Handle(server, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal(McpServer.SupportedVersions[0], future!["result"]!["protocolVersion"]!.GetValue<string>());

        Assert.Null(await Handle(server, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

        var list = await Handle(server, """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""");
        Assert.Equal("a", list!["id"]!.GetValue<string>());
        var tools = list["result"]!["tools"]!.AsArray();
        Assert.Equal(AssistantTools.All.Count, tools.Count);
        var remove = tools.Single(t => t!["name"]!.GetValue<string>() == "remove_runner")!;
        Assert.True(remove["annotations"]!["destructiveHint"]!.GetValue<bool>());

        var unknown = await Handle(server, """{"jsonrpc":"2.0","id":3,"method":"resources/list"}""");
        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task McpServer_ReportsToolErrorsAsResults()
    {
        var server = new McpServer((_, _, _) => Task.FromResult(false), _ => Task.CompletedTask);
        var result = await Handle(server, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_pcs","arguments":{}}}""");
        // Access is off in the test settings, so the tool explains how to turn it on instead of failing the request.
        Assert.True(result!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Settings", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("vscode-webview://abc", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost.evil.example", false)]
    public void HttpEndpoint_OnlyAcceptsLocalOrigins(string origin, bool allowed)
        => Assert.Equal(allowed, AssistantHttpServer.IsLocalOrigin(origin));

    [Fact]
    public async Task HttpEndpoint_NeedsTheTokenAndAnswersMcp()
    {
        var token = AssistantHttpServer.NewToken();
        FleetConfig.Update(c =>
        {
            c.Assistant.Enabled = true;
            c.Assistant.HttpEnabled = true;
            c.Assistant.ProtectedHttpToken = AssistantOptions.ProtectToken(token);
        });
        try
        {
            await CheckEndpointAsync(token);
        }
        finally
        {
            FleetConfig.Update(c => c.Assistant = new AssistantOptions());
        }
    }

    private static async Task CheckEndpointAsync(string token)
    {
        using var server = StartEndpoint(out var url);
        using var http = new HttpClient();
        const string initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"Test"}}}""";

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 50 && response == null; attempt++)
        {
            try { response = await http.PostAsync(url, new StringContent(initialize, Encoding.UTF8, "application/json")); }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        Assert.Equal(HttpStatusCode.Unauthorized, response!.StatusCode);

        async Task<HttpResponseMessage> Send(HttpMethod method, string? body, string? origin = null, string? sessionId = null)
        {
            var message = new HttpRequestMessage(method, url);
            if (body != null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (origin != null) message.Headers.Add("Origin", origin);
            if (sessionId != null) message.Headers.Add("Mcp-Session-Id", sessionId);
            return await http.SendAsync(message);
        }

        var accepted = await Send(HttpMethod.Post, initialize);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var sessionId = accepted.Headers.GetValues("Mcp-Session-Id").Single();
        var json = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!;
        Assert.Equal("runner-manager", json["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", sessionId: sessionId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, initialize, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(HttpMethod.Get, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "{not json")).StatusCode);

        // The token only works while Connect by URL is on.
        FleetConfig.Update(c => c.Assistant.HttpEnabled = false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, initialize)).StatusCode);
    }

    [Fact]
    public async Task LocalSignIn_AcceptsOnlyAChallengeEncryptedForThisUser()
    {
        using var server = StartEndpoint(out var url);
        using var link = new AgentLink(url);
        var answer = await WithRetry(() => link.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"Test"}}}"""));
        Assert.Equal("runner-manager", JsonNode.Parse(answer!)!["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        using var http = new HttpClient();
        var challenge = JsonNode.Parse(await (await http.PostAsync($"{url}/local/challenge", null)).Content.ReadAsStringAsync())!["nonce"]!.GetValue<string>();
        var forged = new JsonObject { ["nonce"] = challenge, ["proof"] = Convert.ToBase64String(new byte[64]) }.ToJsonString();
        var refused = await http.PostAsync($"{url}/local/session", new StringContent(forged, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(AssistantHttpServer.ProofMatches(challenge, Convert.ToBase64String(new byte[64])));
    }

    /// <summary>A real MCP session over stdio with the built program, the way AI tools start it. It forwards to the agent.</summary>
    [Fact]
    public async Task StdioServer_ForwardsAnMcpSessionToTheAgent()
    {
        FleetConfig.Update(c => c.Assistant.Enabled = true);
        try
        {
            using var server = StartEndpoint(out var url);
            await WithRetry(async () => { using var link = new AgentLink(url); return await link.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"ping"}"""); });
            var responses = await RunStdioAsync(url,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Test","version":"1"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"recent_runs","arguments":{"repository":"not a repository"}}}""");

            Assert.Equal("2025-06-18", responses[1]["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal(AssistantTools.All.Count, responses[2]["result"]!["tools"]!.AsArray().Count);
            Assert.True(responses[3]["result"]!["isError"]!.GetValue<bool>());
            // Answered by the agent, which has assistant access on; the child process has no settings of its own.
            Assert.Contains("not a GitHub repository", responses[3]["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            FleetConfig.Update(c => c.Assistant = new AssistantOptions());
        }
    }

    [Fact]
    public async Task StdioServer_ExplainsWhenTheAgentIsNotRunning()
    {
        var responses = await RunStdioAsync($"http://localhost:{TestPorts.FreeRange(1)}/mcp",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"Test"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_pcs","arguments":{}}}""");

        Assert.Equal("runner-manager", responses[1]["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.True(responses[2]["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("fleet agent is not running", responses[2]["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    private static MachineInfo Machine(string name, string version, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name, new MachineStatusPayload { MachineName = name, AppVersion = version, Runners = runners });

    private static MachineRunnerStatus Runner(string name, string scope, string service, params string[] labels) => new()
    {
        AgentName = name,
        Scope = scope,
        RepositoryPath = scope.StartsWith("repos/") ? scope[6..] : string.Empty,
        ServiceName = service,
        CustomLabels = labels
    };

    private static AssistantHttpServer StartEndpoint(out string url)
    {
        var port = TestPorts.FreeRange(1);
        var server = new AssistantHttpServer(port, "localhost");
        _ = server.RunAsync();
        url = $"http://localhost:{port}/mcp";
        return server;
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (AgentLinkException) when (attempt < 50) { await Task.Delay(100); }
        }
    }

    private static async Task<Dictionary<int, JsonNode>> RunStdioAsync(string agentUrl, params string[] lines)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, AppPaths.ExeName);
        Assert.True(File.Exists(exe), $"{exe} is missing");
        var state = Path.Combine(Path.GetTempPath(), $"runner-manager-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(state);
        try
        {
            var start = new ProcessStartInfo(exe, AssistantHost.McpArgument)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };
            start.Environment["RUNNER_MANAGER_STATE_DIR"] = state;
            start.Environment[AgentLink.UrlVariable] = agentUrl;
            using var process = Process.Start(start)!;
            var input = process.StandardInput;
            input.AutoFlush = true;
            foreach (var line in lines) await input.WriteLineAsync(line);
            input.Close();

            var responses = new Dictionary<int, JsonNode>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var message = JsonNode.Parse(line)!;
                responses[message["id"]!.GetValue<int>()] = message;
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            return responses;
        }
        finally
        {
            try { Directory.Delete(state, recursive: true); } catch { }
        }
    }

    private static async Task<JsonObject?> Handle(McpServer server, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await server.HandleAsync(document.RootElement);
    }
}
