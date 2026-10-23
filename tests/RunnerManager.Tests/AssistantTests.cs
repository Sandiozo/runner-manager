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
        using var document = JsonDocument.Parse("""{"repository":"  octocat/web-app ","labels":["gpu"," ",""],"delete_files":"yes","count":99}""");
        var call = new AssistantCall(document.RootElement, (_, _) => Task.FromResult(true), _ => { });
        Assert.Equal("octocat/web-app", call.Text("repository"));
        Assert.Equal(new[] { "gpu" }, call.TextList("labels"));
        Assert.False(call.Flag("delete_files")); // only a JSON true counts
        Assert.Equal(20, call.Number("count", 10, 1, 20));
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
        }
        Assert.Equal(AssistantToolKind.Destructive, AssistantTools.All.Single(t => t.Name == "remove_runner").Kind);
        Assert.Equal(AssistantToolKind.Destructive, AssistantTools.All.Single(t => t.Name == "move_runner").Kind);
        Assert.Equal(AssistantToolKind.Change, AssistantTools.All.Single(t => t.Name == "create_runner").Kind);
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
        var port = TestPorts.FreeRange(1);
        using var server = new AssistantHttpServer(port, "localhost");
        _ = server.RunAsync();
        using var http = new HttpClient();
        var url = $"http://localhost:{port}/mcp/";
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
    }

    /// <summary>A real MCP session over stdio with the built program, the way AI tools start it.</summary>
    [Fact]
    public async Task StdioServer_AnswersAnMcpSession()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, AppPaths.ExeName);
        Assert.True(File.Exists(exe), $"{exe} is missing");
        var state = Path.Combine(Path.GetTempPath(), $"runner-manager-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "fleet.json"), """{"Assistant":{"Enabled":true}}""");
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
            using var process = Process.Start(start)!;
            var input = process.StandardInput;
            input.AutoFlush = true;
            await input.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Test","version":"1"}}}""");
            await input.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await input.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            await input.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"recent_runs","arguments":{"repository":"not a repository"}}}""");
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
            Assert.Equal("2025-06-18", responses[1]["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal(AssistantTools.All.Count, responses[2]["result"]!["tools"]!.AsArray().Count);
            Assert.True(responses[3]["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("not a GitHub repository", responses[3]["result"]!["content"]![0]!["text"]!.GetValue<string>());
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
