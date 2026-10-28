using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager.Tests;

// Some of these tests read or change the assistant settings in fleet.json.
[Collection(FleetSettingsCollection.Name)]
public class AssistantTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"Test"}}}""";

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
    public void CommandLineArguments_BecomeToolArgumentsOfTheRightType()
    {
        var create = AssistantTools.Find("create_runner");
        var arguments = AssistantHost.ParseArguments(create, new[] { "--repository", "octocat/web-app", "--labels", "gpu,cuda", "--even-if-exists", "--name=2024" });
        Assert.Equal("octocat/web-app", arguments.GetProperty("repository").GetString());
        Assert.Equal(new[] { "gpu", "cuda" }, arguments.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        Assert.True(arguments.GetProperty("even_if_exists").GetBoolean());
        // A name that looks like a number stays the name.
        Assert.Equal("2024", arguments.GetProperty("name").GetString());
        Assert.False(AssistantHost.ParseArguments(create, new[] { "--even-if-exists", "false" }).GetProperty("even_if_exists").GetBoolean());

        var log = AssistantTools.Find("runner_log");
        Assert.Equal(200, AssistantHost.ParseArguments(log, new[] { "--lines", "200" }).GetProperty("lines").GetInt32());
        Assert.Throws<AssistantException>(() => AssistantHost.ParseArguments(log, new[] { "--lines", "many" }));
        Assert.Throws<AssistantException>(() => AssistantHost.ParseArguments(log, new[] { "--line", "200" }));
        Assert.Throws<AssistantException>(() => AssistantHost.ParseArguments(create, new[] { "--repository" }));
        Assert.Throws<AssistantException>(() => AssistantHost.ParseArguments(create, new[] { "stray" }));

        // An unknown tool: the agent says so; the arguments are guessed.
        var guessed = AssistantHost.ParseArguments(null, new[] { "--count=5", "--flag" });
        Assert.Equal(5, guessed.GetProperty("count").GetInt32());
        Assert.True(guessed.GetProperty("flag").GetBoolean());
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

    // ----- MCP messages -----

    [Fact]
    public async Task McpServer_NegotiatesTheVersionAndListsTools()
    {
        var server = NewServer();

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
        var server = NewServer();
        var result = await Handle(server, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_pcs","arguments":{}}}""");
        // Access is off in the test settings, so the tool explains how to turn it on instead of failing the request.
        Assert.True(result!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Settings", result["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""", 1, -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", 1, -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":123}}""", 1, -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"no_such_tool"}}""", 1, -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_pcs","arguments":[1]}}""", 1, -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ping","params":5}""", 1, -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""", null, -32600)]
    [InlineData("""5""", null, -32600)]
    [InlineData("""[]""", null, -32600)]
    public async Task McpServer_AnswersMalformedMessagesWithErrors(string json, int? id, int code)
    {
        var answer = await HandleDocument(NewServer(), json);
        var error = Assert.IsType<JsonObject>(answer);
        Assert.Equal(code, error["error"]!["code"]!.GetValue<int>());
        if (id == null) Assert.Null(error["id"]);
        else Assert.Equal(id, error["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task McpServer_ToleratesOddInitializeValues_AndAnswersEachBatchItem()
    {
        var server = NewServer();
        var init = await Handle(server, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":5,"clientInfo":{"name":7}}}""");
        Assert.Equal(McpServer.SupportedVersions[0], init!["result"]!["protocolVersion"]!.GetValue<string>());

        var batch = Assert.IsType<JsonArray>(await HandleDocument(server, """
            [{"jsonrpc":"2.0","id":1,"method":"ping"},
             {"jsonrpc":"2.0","id":2,"method":5},
             {"jsonrpc":"2.0","method":"notifications/initialized"},
             {"jsonrpc":"2.0","id":4,"method":"initialize","params":{}},
             7]
            """));
        Assert.Equal(4, batch.Count);
        Assert.NotNull(batch.Single(a => a!["id"]?.GetValue<int>() == 1)!["result"]);
        Assert.Equal(-32600, batch.Single(a => a!["id"]?.GetValue<int>() == 2)!["error"]!["code"]!.GetValue<int>());
        Assert.Contains("on its own", batch.Single(a => a!["id"]?.GetValue<int>() == 4)!["error"]!["message"]!.GetValue<string>());
        Assert.Equal(-32600, batch.Single(a => a!["id"] == null)!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task McpServer_CancelsARequestWhenTheClientSaysSo()
    {
        var shown = new TaskCompletionSource();
        var server = new McpServer(async (_, _, _, cancellationToken) =>
        {
            shown.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }, _ => Task.CompletedTask, ApprovingTool);

        var call = Handle(server, """{"jsonrpc":"2.0","id":"c1","method":"tools/call","params":{"name":"create_runner","arguments":{}}}""");
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await Handle(server, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"c1","reason":"timeout"}}"""));
        var answer = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(McpServer.RequestCancelled, answer!["error"]!["code"]!.GetValue<int>());
    }

    // ----- Approvals -----

    [Fact]
    public async Task Approvals_AreShownOneAtATimeInTheOrderAsked()
    {
        var shown = new List<string>();
        var answers = new Dictionary<string, TaskCompletionSource<bool>>();
        var showing = new SemaphoreSlim(0);
        var queue = new ApprovalQueue(async (_, title, _, cancellationToken) =>
        {
            TaskCompletionSource<bool> answer;
            lock (shown)
            {
                shown.Add(title);
                answer = answers[title] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            showing.Release();
            return await answer.Task.WaitAsync(cancellationToken);
        }, TimeSpan.FromMinutes(1));

        using var cancelB = new CancellationTokenSource();
        var a = queue.AskAsync("Test", "A", "", CancellationToken.None);
        var b = queue.AskAsync("Test", "B", "", cancelB.Token);
        var c = queue.AskAsync("Test", "C", "", CancellationToken.None);
        Assert.True(await showing.WaitAsync(TimeSpan.FromSeconds(5)));
        // B gives up while it waits: C must still wait for A.
        cancelB.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.False(await showing.WaitAsync(TimeSpan.FromMilliseconds(300)));
        lock (shown) Assert.Equal(new[] { "A" }, shown);

        answers["A"].SetResult(true);
        Assert.True(await a);
        Assert.True(await showing.WaitAsync(TimeSpan.FromSeconds(5)));
        lock (shown) Assert.Equal(new[] { "A", "C" }, shown);
        answers["C"].SetResult(false);
        Assert.False(await c);
    }

    [Fact]
    public async Task Approvals_NobodyAnswersCountAsDeclined()
    {
        var queue = new ApprovalQueue(async (_, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }, TimeSpan.FromMilliseconds(300));
        var timedOut = await Assert.ThrowsAsync<AssistantException>(() => queue.AskAsync("Test", "A", "", CancellationToken.None));
        Assert.Contains("counts as declined", timedOut.Message);
        // The next one is not held up by it.
        await Assert.ThrowsAsync<AssistantException>(() => queue.AskAsync("Test", "B", "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ----- HTTP endpoint and local sign-in -----

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

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 50 && response == null; attempt++)
        {
            try { response = await http.PostAsync(url, new StringContent(Initialize, Encoding.UTF8, "application/json")); }
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

        var accepted = await Send(HttpMethod.Post, Initialize);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var sessionId = accepted.Headers.GetValues("Mcp-Session-Id").Single();
        var json = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!;
        Assert.Equal("runner-manager", json["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", sessionId: sessionId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, Initialize, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(HttpMethod.Get, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "{not json")).StatusCode);
        // A session this endpoint does not know is a 404, so the client starts a new one.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, """{"jsonrpc":"2.0","id":5,"method":"ping"}""", sessionId: "no-such-session")).StatusCode);
        // A malformed message is answered, not dropped.
        var malformed = await Send(HttpMethod.Post, """{"jsonrpc":"2.0","id":6,"method":5}""", sessionId: sessionId);
        Assert.Equal(HttpStatusCode.OK, malformed.StatusCode);
        Assert.Equal(-32600, JsonNode.Parse(await malformed.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<int>());

        // The token only works while Connect by URL is on.
        FleetConfig.Update(c => c.Assistant.HttpEnabled = false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, Initialize)).StatusCode);
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

    [Fact]
    public async Task LocalSignIn_StillWorksAfterAFloodOfChallenges()
    {
        using var server = StartEndpoint(out var url);
        using var http = new HttpClient();
        await WithRetry(async () => { using var probe = new AgentLink(url); return await probe.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"ping"}"""); });
        for (var i = 0; i < 150; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"{url}/local/challenge", null)).StatusCode);
        using var link = new AgentLink(url);
        Assert.NotNull(await link.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
    }

    [Fact]
    public void AgentProof_OnlyMatchesTheClientsOwnValue()
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        Assert.True(AgentLink.AgentProofMatches(nonce, AssistantHttpServer.AgentProof(nonce)));
        Assert.False(AgentLink.AgentProofMatches(RandomNumberGenerator.GetBytes(32), AssistantHttpServer.AgentProof(nonce)));
        // The client's own kind of proof never stands in for the agent's.
        var clientProof = Convert.ToBase64String(ProtectedData.Protect(AssistantHttpServer.AgentProofText(nonce), AssistantHttpServer.ProofEntropy,
            DataProtectionScope.CurrentUser));
        Assert.False(AgentLink.AgentProofMatches(nonce, clientProof));
        Assert.False(AgentLink.AgentProofMatches(nonce, "not base64"));
    }

    [Theory]
    [InlineData(null, "older Runner Manager")]
    [InlineData("AAAA", "could not prove")]
    public async Task AgentLink_RefusesAnAgentThatCannotProveItself(string? agentProof, string expected)
    {
        // A stand-in that answers the sign-in like the agent, without the agent's DPAPI proof.
        var port = TestPorts.FreeRange(1);
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://localhost:{port}/mcp/");
        fake.Start();
        var posts = 0;
        _ = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext context;
                try { context = await fake.GetContextAsync(); }
                catch { return; }
                var path = context.Request.Url!.AbsolutePath;
                var body = path.EndsWith("/challenge") ? new JsonObject { ["nonce"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
                    : path.EndsWith("/session") ? new JsonObject { ["token"] = "t", ["agent_proof"] = agentProof }
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() };
                if (!path.Contains("/local/")) Interlocked.Increment(ref posts);
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        using var link = new AgentLink($"http://localhost:{port}/mcp");
        var refused = await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(Initialize));
        Assert.Contains(expected, refused.Message);
        Assert.True(refused.AgentUnavailable);
        Assert.Equal(0, posts); // nothing of the session was sent to it
    }

    [Fact]
    public async Task AgentLink_ReportsATooLargeRequestAsSuch()
    {
        using var server = StartEndpoint(out var url);
        using var link = new AgentLink(url);
        await WithRetry(() => link.SendAsync(Initialize));
        var huge = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "ping", ["params"] = new JsonObject { ["pad"] = new string('x', 300 * 1024) } }.ToJsonString();
        var tooLarge = await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(huge));
        Assert.False(tooLarge.AgentUnavailable);
        Assert.Contains("larger than 256 KB", tooLarge.Message);
        // The agent is still used for the next request.
        Assert.NotNull(await link.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}"""));
    }

    [Fact]
    public async Task AgentLink_StartsANewSessionWhenTheAgentForgotIt_AndKeepsTheClientName()
    {
        var clients = new List<string>();
        var initializes = 0;
        using var server = StartEndpoint(out var url, () =>
        {
            Interlocked.Increment(ref initializes);
            return new McpServer((client, _, _, _) => { lock (clients) clients.Add(client); return Task.FromResult(true); }, _ => Task.CompletedTask, ApprovingTool);
        });
        using var link = new AgentLink(url);
        await WithRetry(() => link.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"ping"}"""));
        initializes = 0;

        // Sent at once, the way a script pipes them: the call still belongs to the session initialize starts.
        var init = link.SendAsync(Initialize);
        var call = link.SendAsync(ToolCall(2));
        await init;
        Assert.False(IsError(await call));

        server.ForgetSessions();
        Assert.False(IsError(await link.SendAsync(ToolCall(3))));
        Assert.Equal(2, initializes);
        lock (clients) Assert.Equal(new[] { "Test", "Test" }, clients);
    }

    [Fact]
    public async Task AgentLink_SignsInOnceWhenSeveralRequestsFindTheAgentRestarted()
    {
        var port = TestPorts.FreeRange(1);
        var url = $"http://localhost:{port}/mcp";
        var initializes = 0;
        McpServer Count()
        {
            Interlocked.Increment(ref initializes);
            return new McpServer((_, _, _, _) => Task.FromResult(true), _ => Task.CompletedTask, ApprovingTool);
        }
        using var link = new AgentLink(url);
        using (var first = new AssistantHttpServer(port, "localhost", Count))
        {
            _ = first.RunAsync();
            await WithRetry(() => link.SendAsync(Initialize));
        }

        using var second = new AssistantHttpServer(port, "localhost", Count);
        _ = second.RunAsync();
        initializes = 0;
        await Task.Delay(200);
        var answers = await Task.WhenAll(Enumerable.Range(10, 5).Select(id => WithRetry(() => link.SendAsync(ToolCall(id)))));
        Assert.All(answers, answer => Assert.False(IsError(answer)));
        Assert.Equal(1, initializes);
    }

    // ----- Over stdio, with the built program -----

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
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"recent_runs","arguments":{"repository":"not a repository"}}}""",
                """{"jsonrpc":"2.0","id":4,"method":5}""",
                """{"jsonrpc":"2.0","id":5,"method":"ping"}""");

            Assert.Equal("2025-06-18", responses[1]["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal(AssistantTools.All.Count, responses[2]["result"]!["tools"]!.AsArray().Count);
            Assert.True(responses[3]["result"]!["isError"]!.GetValue<bool>());
            // Answered by the agent, which has assistant access on; the child process has no settings of its own.
            Assert.Contains("not a GitHub repository", responses[3]["result"]!["content"]![0]!["text"]!.GetValue<string>());
            // A malformed message gets its error, and does not take the agent away from the others.
            Assert.Equal(-32600, responses[4]["error"]!["code"]!.GetValue<int>());
            Assert.NotNull(responses[5]["result"]);
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

    [Fact]
    public async Task StdioServer_PassesProgressOnWhileAToolRuns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = StartEndpoint(out var url, () => new McpServer((_, _, _, _) => Task.FromResult(true), _ => Task.CompletedTask,
            async (_, _, _, log, cancellationToken) =>
            {
                log?.Invoke("Downloading the runner…");
                log?.Invoke("Registering…");
                await release.Task.WaitAsync(cancellationToken);
                return new { created = true };
            }));
        await WithRetry(async () => { using var link = new AgentLink(url); return await link.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"ping"}"""); });

        await using var client = StdioClient.Start(url);
        await client.SendAsync(Initialize);
        await client.ReadUntilAsync(m => m["id"]?.GetValue<int>() == 1);
        await client.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"create_runner","arguments":{},"_meta":{"progressToken":"p1"}}}""");

        // Progress arrives while the call is still running, one message per line.
        var first = await client.ReadUntilAsync(m => m["method"]?.GetValue<string>() == "notifications/progress");
        Assert.Equal("p1", first["params"]!["progressToken"]!.GetValue<string>());
        Assert.Equal("Downloading the runner…", first["params"]!["message"]!.GetValue<string>());
        var second = await client.ReadUntilAsync(m => m["method"]?.GetValue<string>() == "notifications/progress");
        Assert.Equal("Registering…", second["params"]!["message"]!.GetValue<string>());

        release.SetResult();
        var result = await client.ReadUntilAsync(m => m["id"]?.GetValue<int>() == 2);
        Assert.False(result["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StdioServer_CancellingACallClosesItsApproval()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acted = false;
        var approvals = new ApprovalQueue(async (_, _, _, cancellationToken) =>
        {
            shown.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { closed.TrySetResult(); }
            return true;
        }, TimeSpan.FromMinutes(5));
        using var server = StartEndpoint(out var url, () => new McpServer(approvals.AskAsync, _ => Task.CompletedTask,
            async (name, arguments, approve, log, cancellationToken) =>
            {
                var result = await ApprovingTool(name, arguments, approve, log, cancellationToken);
                acted = true;
                return result;
            }));
        await WithRetry(async () => { using var link = new AgentLink(url); return await link.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"ping"}"""); });

        await using var client = StdioClient.Start(url);
        await client.SendAsync(Initialize);
        await client.ReadUntilAsync(m => m["id"]?.GetValue<int>() == 1);
        await client.SendAsync(ToolCall(2));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await client.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2,"reason":"Request timed out"}}""");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var answer = await client.ReadUntilAsync(m => m["id"]?.GetValue<int>() == 2);
        Assert.Equal(McpServer.RequestCancelled, answer["error"]!["code"]!.GetValue<int>());
        Assert.False(acted);
    }

    // ----- Helpers -----

    private static McpServer NewServer() => new((_, _, _, _) => Task.FromResult(false), _ => Task.CompletedTask);

    /// <summary>A stand-in for a change tool: asks for approval, then reports what it would do.</summary>
    private static async Task<object> ApprovingTool(string name, JsonElement arguments, Func<string, string, Task<bool>> approve, Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!await approve("Create a runner?", "An AI assistant wants to create a runner.")) throw new AssistantException("Declined.");
        cancellationToken.ThrowIfCancellationRequested();
        return new { done = name };
    }

    private static string ToolCall(int id)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "create_runner", ["arguments"] = new JsonObject() }
        }.ToJsonString();

    private static bool IsError(string? answer)
    {
        var node = JsonNode.Parse(answer!)!;
        return node["error"] != null || node["result"]!["isError"]!.GetValue<bool>();
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

    private static AssistantHttpServer StartEndpoint(out string url, Func<McpServer>? newServer = null)
    {
        var port = TestPorts.FreeRange(1);
        var server = new AssistantHttpServer(port, "localhost", newServer);
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
        await using var client = StdioClient.Start(agentUrl);
        foreach (var line in lines) await client.SendAsync(line);
        client.CloseInput();
        var responses = new Dictionary<int, JsonNode>();
        foreach (var message in await client.ReadToEndAsync())
            if (message["id"] is JsonValue id) responses[id.GetValue<int>()] = message;
        Assert.Equal(0, await client.ExitCodeAsync());
        return responses;
    }

    private static async Task<JsonObject?> Handle(McpServer server, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await server.HandleAsync(document.RootElement);
    }

    private static async Task<JsonNode?> HandleDocument(McpServer server, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await server.HandleDocumentAsync(document.RootElement);
    }

    /// <summary>RunnerManager.exe --mcp, started the way an AI tool starts it, with its own empty state folder.</summary>
    private sealed class StdioClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _state;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

        private StdioClient(Process process, string state)
        {
            _process = process;
            _state = state;
        }

        public static StdioClient Start(string agentUrl)
        {
            var exe = Path.Combine(AppContext.BaseDirectory, AppPaths.ExeName);
            Assert.True(File.Exists(exe), $"{exe} is missing");
            var state = Path.Combine(Path.GetTempPath(), $"runner-manager-mcp-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(state);
            var start = new ProcessStartInfo(exe, AssistantHost.McpArgument)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };
            start.Environment["RUNNER_MANAGER_STATE_DIR"] = state;
            start.Environment[AgentLink.UrlVariable] = agentUrl;
            var process = Process.Start(start)!;
            process.StandardInput.AutoFlush = true;
            return new StdioClient(process, state);
        }

        public Task SendAsync(string line) => _process.StandardInput.WriteLineAsync(line);

        public void CloseInput() => _process.StandardInput.Close();

        /// <summary>Reads messages until one matches. Every line must be one JSON message.</summary>
        public async Task<JsonNode> ReadUntilAsync(Func<JsonNode, bool> match)
        {
            while (await _process.StandardOutput.ReadLineAsync(_timeout.Token) is { } line)
            {
                var message = JsonNode.Parse(line)!;
                if (match(message)) return message;
            }
            throw new InvalidOperationException("The MCP server closed stdout.");
        }

        public async Task<List<JsonNode>> ReadToEndAsync()
        {
            var messages = new List<JsonNode>();
            while (await _process.StandardOutput.ReadLineAsync(_timeout.Token) is { } line) messages.Add(JsonNode.Parse(line)!);
            return messages;
        }

        public async Task<int> ExitCodeAsync()
        {
            await _process.WaitForExitAsync(_timeout.Token);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try { _process.StandardInput.Close(); } catch { }
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _process.WaitForExitAsync(wait.Token);
            }
            catch { try { _process.Kill(); } catch { } }
            _process.Dispose();
            _timeout.Dispose();
            try { Directory.Delete(_state, recursive: true); } catch { }
        }
    }
}
