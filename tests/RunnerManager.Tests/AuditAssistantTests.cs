using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager.Tests;

/// <summary>Tests for the fixes that followed the audit of the AI assistant code at 2.8.2.</summary>
public class AuditAssistantTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"Test"}}}""";
    private const string Ping = """{"jsonrpc":"2.0","id":9,"method":"ping"}""";
    private const string TestClient = "An AI assistant (\"Test\")";

    // ----- Local sign-in -----

    /// <summary>
    /// Why the sign-in changed: DPAPI opens a value that was sealed for the whole PC for every account, whatever scope
    /// the reader names. So a sealed value that a caller hands over shows nothing about the caller.
    /// </summary>
    [Fact]
    public void Dpapi_OpensAValueSealedForTheWholePc_WhateverScopeTheReaderNames()
    {
        var secret = RandomNumberGenerator.GetBytes(LocalSignIn.SecretBytes);
        var forEveryAccount = ProtectedData.Protect(secret, LocalSignIn.AgentChallenge, DataProtectionScope.LocalMachine);
        Assert.Equal(secret, ProtectedData.Unprotect(forEveryAccount, LocalSignIn.AgentChallenge, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void SealedSecret_OpensOnlyForItsOwnPurpose()
    {
        var secret = RandomNumberGenerator.GetBytes(LocalSignIn.SecretBytes);
        Assert.Equal(secret, LocalSignIn.Open(LocalSignIn.Seal(secret, LocalSignIn.AgentChallenge), LocalSignIn.AgentChallenge));
        // Neither side opens the other side's kind, so a secret cannot be sent back to the side that sealed it.
        Assert.Null(LocalSignIn.Open(LocalSignIn.Seal(secret, LocalSignIn.AgentChallenge), LocalSignIn.ClientChallenge));
        Assert.Null(LocalSignIn.Open(LocalSignIn.Seal(secret, LocalSignIn.ClientChallenge), LocalSignIn.AgentChallenge));
        // Other DPAPI data of this user does not open: the pairing code and the URL token are sealed without a purpose.
        Assert.Null(LocalSignIn.Open(ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser), LocalSignIn.AgentChallenge));
        Assert.Null(LocalSignIn.Open(ProtectedData.Protect(secret, Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v1"), DataProtectionScope.CurrentUser),
            LocalSignIn.ClientChallenge));
        // Only a secret of the size this sign-in uses.
        Assert.Null(LocalSignIn.Open(ProtectedData.Protect(new byte[200], LocalSignIn.AgentChallenge, DataProtectionScope.CurrentUser), LocalSignIn.AgentChallenge));
        Assert.Null(LocalSignIn.Open(new byte[64], LocalSignIn.AgentChallenge));
    }

    [Fact]
    public void Proof_DependsOnEveryPartOfTheExchange()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var agentNonce = RandomNumberGenerator.GetBytes(32);
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var agentSealed = RandomNumberGenerator.GetBytes(200);
        var clientSealed = RandomNumberGenerator.GetBytes(200);
        var proof = LocalSignIn.Proof(secret, byAgent: false, 47830, agentNonce, clientNonce, agentSealed, clientSealed);

        Assert.Equal(LocalSignIn.ProofBytes, proof.Length);
        Assert.True(LocalSignIn.Matches(proof, LocalSignIn.Proof(secret, false, 47830, agentNonce, clientNonce, agentSealed, clientSealed)));
        var others = new[]
        {
            LocalSignIn.Proof(RandomNumberGenerator.GetBytes(32), false, 47830, agentNonce, clientNonce, agentSealed, clientSealed),
            // A client's proof never stands in for the agent's, also with the same secret.
            LocalSignIn.Proof(secret, true, 47830, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Proof(secret, false, 47831, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Proof(secret, false, 47830, RandomNumberGenerator.GetBytes(32), clientNonce, agentSealed, clientSealed),
            LocalSignIn.Proof(secret, false, 47830, agentNonce, RandomNumberGenerator.GetBytes(32), agentSealed, clientSealed),
            LocalSignIn.Proof(secret, false, 47830, clientNonce, agentNonce, agentSealed, clientSealed),
            LocalSignIn.Proof(secret, false, 47830, agentNonce, clientNonce, RandomNumberGenerator.GetBytes(200), clientSealed),
            LocalSignIn.Proof(secret, false, 47830, agentNonce, clientNonce, agentSealed, RandomNumberGenerator.GetBytes(200)),
            LocalSignIn.Proof(secret, false, 47830, agentNonce, clientNonce, clientSealed, agentSealed)
        };
        Assert.All(others, other => Assert.False(LocalSignIn.Matches(proof, other)));
        Assert.False(LocalSignIn.Matches(proof, proof[..16]));
    }

    [Fact]
    public void Token_NeedsBothSecrets_AndBelongsToOneExchange()
    {
        var agentSecret = RandomNumberGenerator.GetBytes(32);
        var clientSecret = RandomNumberGenerator.GetBytes(32);
        var agentNonce = RandomNumberGenerator.GetBytes(32);
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var agentSealed = RandomNumberGenerator.GetBytes(200);
        var clientSealed = RandomNumberGenerator.GetBytes(200);
        var token = LocalSignIn.Token(agentSecret, clientSecret, 47830, agentNonce, clientNonce, agentSealed, clientSealed);

        Assert.Matches("^rmai_[0-9a-f]{64}$", token);
        Assert.Equal(token, LocalSignIn.Token(agentSecret, clientSecret, 47830, agentNonce, clientNonce, agentSealed, clientSealed));
        var others = new[]
        {
            // Someone who knows one secret, as a program that seals its own for the whole PC does, has not got it.
            LocalSignIn.Token(new byte[32], clientSecret, 47830, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, new byte[32], 47830, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(clientSecret, agentSecret, 47830, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, 47831, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, 47830, RandomNumberGenerator.GetBytes(32), clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, 47830, agentNonce, RandomNumberGenerator.GetBytes(32), agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, 47830, agentNonce, clientNonce, RandomNumberGenerator.GetBytes(200), clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, 47830, agentNonce, clientNonce, agentSealed, RandomNumberGenerator.GetBytes(200))
        };
        Assert.All(others, other => Assert.NotEqual(token, other));
        // It is not one of the proofs that are sent.
        foreach (var byAgent in new[] { false, true })
        foreach (var secret in new[] { agentSecret, clientSecret })
            Assert.DoesNotContain(Convert.ToHexString(LocalSignIn.Proof(secret, byAgent, 47830, agentNonce, clientNonce, agentSealed, clientSealed)).ToLowerInvariant(), token);
    }

    [Fact]
    public async Task SignIn_Works_AndTheSameAnswerIsRefusedTheSecondTime()
    {
        using var server = StartEndpoint(out var url, out var port);
        using var http = new HttpClient();
        var signIn = await SignInByHandAsync(http, url, port);

        Assert.Equal(HttpStatusCode.OK, signIn.Status);
        var answer = JsonNode.Parse(signIn.Answer)!;
        // The agent shows that it opened the secret this side sealed. It does not send the token.
        Assert.True(LocalSignIn.Matches(signIn.ExpectedAgentProof, Convert.FromBase64String(answer["agent_proof"]!.GetValue<string>())));
        Assert.Equal(new[] { "agent_proof" }, answer.AsObject().Select(p => p.Key));
        Assert.Equal(HttpStatusCode.OK, (await PostMcpAsync(http, url, signIn.Token, Ping)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(http, url, "rmai_not-a-token", Ping)).StatusCode);

        // The same request again, as someone who saw it go by would send it: the sign-in it answers is used up.
        var replayed = await http.PostAsync($"{url}/local/session", Json(signIn.Request));
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        Assert.Null(JsonNode.Parse(await replayed.Content.ReadAsStringAsync())!["agent_proof"]);
    }

    /// <summary>
    /// Windows lets another program listen on a longer address under the agent's, on the same port. It then gets the
    /// sign-in requests of this user's client and can pass them on to the agent, so both sides see a sign-in that
    /// works. It must end up with nothing it can use.
    /// </summary>
    [Fact]
    public async Task SignIn_PassedOnByAnotherProgram_GivesThatProgramNoSession()
    {
        using var server = StartEndpoint(out var url, out var port);
        using var http = new HttpClient();
        var seen = new List<string>();
        using var between = new HttpListener();
        between.Prefixes.Add($"http://localhost:{port}/mcp/local/");
        between.Start();
        _ = Task.Run(async () =>
        {
            while (between.IsListening)
            {
                HttpListenerContext context;
                try { context = await between.GetContextAsync(); }
                catch { return; }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var asked = await reader.ReadToEndAsync();
                    // Under another path the request reaches the agent itself.
                    var route = context.Request.Url!.AbsolutePath.Replace("/mcp/local/", "/mcp/passed-on/local/");
                    using var passedOn = await http.PostAsync($"http://localhost:{port}{route}", Json(asked));
                    var answered = await passedOn.Content.ReadAsStringAsync();
                    lock (seen) { seen.Add(asked); seen.Add(answered); }
                    context.Response.StatusCode = (int)passedOn.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(answered));
                    context.Response.Close();
                }
                catch { try { context.Response.Abort(); } catch { } }
            }
        });

        // This user's client signs in through it without noticing, and its session works.
        using var link = new AgentLink(url);
        var answer = await link.SendAsync(Initialize);
        Assert.Equal("runner-manager", JsonNode.Parse(answer!)!["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        List<string> messages;
        lock (seen) messages = seen.ToList();
        // It saw the whole sign-in: the challenge and its answer, the session request and its answer. Nothing else.
        Assert.Equal(4, messages.Count);
        Assert.Contains("client_challenge", messages[2]);
        Assert.Contains("agent_proof", messages[3]);
        // No value in any of it is a token, as text or as the hex form a token has.
        var values = messages.Where(m => m.Length > 0).SelectMany(m => JsonNode.Parse(m)!.AsObject().Select(p => p.Value!.GetValue<string>())).ToList();
        Assert.Equal(7, values.Count);
        foreach (var value in values)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(http, url, value, Ping)).StatusCode);
            var hex = "rmai_" + Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant();
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(http, url, hex, Ping)).StatusCode);
        }
        // What it saw does not sign it in a second time either.
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"{url}/passed-on/local/session", Json(messages[2]))).StatusCode);
    }

    [Fact]
    public async Task Agent_RefusesACallerThatCannotOpenItsSecret_WhateverTheCallerSeals()
    {
        using var server = StartEndpoint(out var url, out var port);
        using var http = new HttpClient();
        // What another Windows account can do: it reads the challenge, but cannot open the agent's secret. It can
        // seal values for the whole PC, which open here like this user's own.
        var mine = RandomNumberGenerator.GetBytes(32);
        var mineSealed = ProtectedData.Protect(mine, LocalSignIn.ClientChallenge, DataProtectionScope.LocalMachine);
        var clientNonce = RandomNumberGenerator.GetBytes(32);

        async Task<HttpResponseMessage> Try(Func<byte[], byte[], byte[]> proof)
        {
            var challenge = await ChallengeAsync(http, url);
            return await http.PostAsync($"{url}/local/session", Json(new JsonObject
            {
                ["nonce"] = Convert.ToBase64String(challenge.Nonce),
                ["client_nonce"] = Convert.ToBase64String(clientNonce),
                ["client_challenge"] = Convert.ToBase64String(mineSealed),
                ["proof"] = Convert.ToBase64String(proof(challenge.Nonce, challenge.Sealed))
            }.ToJsonString()));
        }

        var attempts = new[]
        {
            // A proof keyed with its own secret instead of the agent's.
            await Try((nonce, agentSealed) => LocalSignIn.Proof(mine, byAgent: false, port, nonce, clientNonce, agentSealed, mineSealed)),
            // The agent's own kind of proof for the secret it would open.
            await Try((nonce, agentSealed) => LocalSignIn.Proof(mine, byAgent: true, port, nonce, clientNonce, agentSealed, mineSealed)),
            // The random value sealed for the whole PC, which is what signed in before.
            await Try((nonce, _) => ProtectedData.Protect(nonce, LocalSignIn.AgentChallenge, DataProtectionScope.LocalMachine)),
            await Try((_, _) => new byte[32])
        };
        foreach (var attempt in attempts)
        {
            Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
            // The agent did not open what the caller sealed: nothing keyed with it comes back.
            Assert.Null(JsonNode.Parse(await attempt.Content.ReadAsStringAsync())!["agent_proof"]);
        }
        // The endpoint still signs in a program of this user.
        Assert.Equal(HttpStatusCode.OK, (await SignInByHandAsync(http, url, port)).Status);
    }

    /// <summary>
    /// A <c>--mcp</c> or <c>ai</c> process from 2.8.2 or older, still running after an update. Its way to sign in
    /// is not accepted any more, also not from this user, and it is told what to do.
    /// </summary>
    [Theory]
    [InlineData(DataProtectionScope.CurrentUser)]
    [InlineData(DataProtectionScope.LocalMachine)]
    public async Task Agent_RefusesTheOlderSignIn_AndSaysToRestartTheAiTool(DataProtectionScope scope)
    {
        using var server = StartEndpoint(out var url, out _);
        using var http = new HttpClient();
        var challenge = await ChallengeAsync(http, url);
        // The older program checks only this much of the challenge, so it gets as far as the answer below.
        Assert.Equal(32, challenge.Nonce.Length);

        var olderProof = ProtectedData.Protect(challenge.Nonce, Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.v1"), scope);
        var refused = await http.PostAsync($"{url}/local/session", Json(new JsonObject
        {
            ["nonce"] = Convert.ToBase64String(challenge.Nonce),
            ["proof"] = Convert.ToBase64String(olderProof),
            ["client_nonce"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }.ToJsonString()));

        // Not 403, which the older program takes for a lost challenge and tries again.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var answer = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!;
        Assert.Null(answer["token"]);
        Assert.Null(answer["agent_proof"]);
        Assert.Contains("Restart the AI tool", answer["error"]!["message"]!.GetValue<string>());
        Assert.All(AssistantHttpServer.OlderProgram, c => Assert.InRange(c, ' ', '~'));
    }

    /// <summary>A program that took the agent's port seals its secret for the whole PC, so this user's client can open it.</summary>
    [Theory]
    [InlineData("own secret")]
    [InlineData("older proof")]
    [InlineData("client proof")]
    public async Task Client_RefusesAnAgentThatCannotOpenTheClientsSecret(string answerWith)
    {
        var port = TestPorts.FreeRange(1);
        var secret = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var secretSealed = ProtectedData.Protect(secret, LocalSignIn.AgentChallenge, DataProtectionScope.LocalMachine);
        var posts = 0;
        using var fake = StartFake(port, (path, body) =>
        {
            if (path.EndsWith("/local/challenge"))
                return new JsonObject { ["nonce"] = Convert.ToBase64String(nonce), ["challenge"] = Convert.ToBase64String(secretSealed) };
            if (!path.EndsWith("/local/session"))
            {
                Interlocked.Increment(ref posts);
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() };
            }
            var request = JsonNode.Parse(body)!;
            var clientNonce = Convert.FromBase64String(request["client_nonce"]!.GetValue<string>());
            var clientSealed = Convert.FromBase64String(request["client_challenge"]!.GetValue<string>());
            var proof = answerWith switch
            {
                // Keyed with the secret it knows, since it cannot open the client's.
                "own secret" => LocalSignIn.Proof(secret, byAgent: true, port, nonce, clientNonce, secretSealed, clientSealed),
                // What an agent sent before: the client's random value, sealed. Here for the whole PC.
                "older proof" => ProtectedData.Protect(clientNonce.Concat(Encoding.UTF8.GetBytes("Runner Manager fleet agent")).ToArray(),
                    Encoding.UTF8.GetBytes("RunnerManager.LocalAssistant.Agent.v1"), DataProtectionScope.LocalMachine),
                // The client's own proof, sent back to it.
                _ => Convert.FromBase64String(request["proof"]!.GetValue<string>())
            };
            return new JsonObject { ["token"] = "t", ["agent_proof"] = Convert.ToBase64String(proof) };
        });

        using var link = new AgentLink($"http://localhost:{port}/mcp");
        var refused = await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(Initialize));
        Assert.Contains("could not prove", refused.Message);
        Assert.True(refused.AgentUnavailable);
        Assert.Equal(0, posts); // nothing of the session was sent to it
    }

    [Fact]
    public async Task Client_RefusesAnAgentsAnswerFromAnEarlierSignIn()
    {
        // A whole sign-in with the real endpoint, kept by someone who saw it.
        string challengeText;
        SignIn earlier;
        int port;
        using (var server = StartEndpoint(out var url, out port))
        using (var http = new HttpClient())
        {
            challengeText = await (await http.PostAsync($"{url}/local/challenge", null)).Content.ReadAsStringAsync();
            earlier = await SignInByHandAsync(http, url, port, challengeText);
            Assert.Equal(HttpStatusCode.OK, earlier.Status);
        }

        // Played back on the same port to a client that signs in now: the answer was made for other random values.
        var posts = 0;
        using var fake = StartFake(port, (path, _) =>
        {
            if (path.EndsWith("/local/challenge")) return JsonNode.Parse(challengeText)!.AsObject();
            if (path.EndsWith("/local/session")) return JsonNode.Parse(earlier.Answer)!.AsObject();
            Interlocked.Increment(ref posts);
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() };
        });
        using var link = new AgentLink($"http://localhost:{port}/mcp");
        var refused = await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(Initialize));
        Assert.Contains("could not prove", refused.Message);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task Client_DoesNotAnswerAChallengeItCannotOpen()
    {
        var port = TestPorts.FreeRange(1);
        var sessions = 0;
        using var fake = StartFake(port, (path, _) =>
        {
            if (path.EndsWith("/local/session")) Interlocked.Increment(ref sessions);
            // Sealed for another purpose, as other DPAPI data of this user is: not opened, so nothing is answered.
            return new JsonObject
            {
                ["nonce"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["challenge"] = Convert.ToBase64String(ProtectedData.Protect(RandomNumberGenerator.GetBytes(32), null, DataProtectionScope.CurrentUser))
            };
        });
        using var link = new AgentLink($"http://localhost:{port}/mcp");
        var refused = await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(Initialize));
        Assert.Contains("another Windows user", refused.Message);
        Assert.Equal(0, sessions);
    }

    [Fact]
    public async Task Challenges_AreLimited_SoAFloodPushesOutTheOldest()
    {
        using var server = StartEndpoint(out var url, out var port);
        using var http = new HttpClient();
        var first = await (await http.PostAsync($"{url}/local/challenge", null)).Content.ReadAsStringAsync();
        for (var i = 0; i < 120; i++)
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync($"{url}/local/challenge", null)).StatusCode);

        // The first one made room for the others, so its right answer comes too late. A new sign-in works.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignInByHandAsync(http, url, port, first)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SignInByHandAsync(http, url, port)).Status);
    }

    // ----- Sessions -----

    [Fact]
    public async Task AgentLink_StartsTheSessionLater_WhenInitializeDidNotReachTheAgent()
    {
        var port = TestPorts.FreeRange(1);
        var url = $"http://localhost:{port}/mcp";
        using var link = new AgentLink(url);
        // The agent is not there for the client's initialize: something else answers on the port.
        using (var busy = new HttpListener())
        {
            busy.Prefixes.Add($"http://localhost:{port}/mcp/");
            busy.Start();
            _ = Task.Run(async () =>
            {
                while (busy.IsListening)
                {
                    try
                    {
                        var context = await busy.GetContextAsync();
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                    }
                    catch { return; }
                }
            });
            await Assert.ThrowsAsync<AgentLinkException>(() => link.SendAsync(Initialize));
        }

        var clients = new List<string>();
        var servers = 0;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new AssistantHttpServer(port, "localhost", () =>
        {
            Interlocked.Increment(ref servers);
            return new McpServer(async (client, title, _, cancellationToken) =>
            {
                lock (clients) clients.Add(client);
                if (title != "Wait") return true;
                shown.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                finally { closed.TrySetResult(); }
                return true;
            }, _ => Task.CompletedTask, async (_, arguments, approve, _, cancellationToken) =>
            {
                var wait = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("wait", out _);
                if (!await approve(wait ? "Wait" : "Go", "An AI assistant wants to do something.")) throw new AssistantException("Declined.");
                cancellationToken.ThrowIfCancellationRequested();
                return new { done = true };
            });
        });
        _ = server.RunAsync();

        // The first request after that starts the session with the client's own initialize.
        var answer = await WithRetry(() => link.SendAsync(ToolCall(2)));
        Assert.False(JsonNode.Parse(answer!)!["result"]!["isError"]!.GetValue<bool>());
        lock (clients) Assert.Equal(new[] { TestClient }, clients);

        // So a cancellation reaches the call it is meant for, and its approval closes.
        var waiting = link.SendAsync(ToolCall(3, wait: true));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await link.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3,"reason":"timeout"}}""");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(McpServer.RequestCancelled, JsonNode.Parse((await waiting.WaitAsync(TimeSpan.FromSeconds(30)))!)!["error"]!["code"]!.GetValue<int>());
        // One session for all of it.
        Assert.Equal(1, servers);
    }

    // ----- Tools -----

    [Fact]
    public void ControlRunner_IsRefusedForARunnerThatRunsInAConsoleWindow()
    {
        var pc = Online("BUILD");
        var service = new MachineRunnerStatus { AgentName = "web", ServiceName = "actions.runner.octocat-web.web", ServiceInstalled = true, IsRunning = true };
        var console = new MachineRunnerStatus { AgentName = "web", ServiceName = "actions.runner.octocat-web.web", ServiceInstalled = true, IsInteractive = true, IsRunning = true };
        var noService = new MachineRunnerStatus { AgentName = "web", InstallPath = @"C:\runners\web", IsInteractive = true, IsRunning = true };

        foreach (var action in new[] { "Start", "Stop", "Restart" }) AssistantTools.RequireService(pc, service, action);

        // Starting the service of a runner that runs in a console window would run the runner twice.
        Assert.Contains("twice", Assert.Throws<AssistantException>(() => AssistantTools.RequireService(pc, console, "Start")).Message);
        Assert.Contains("twice", Assert.Throws<AssistantException>(() => AssistantTools.RequireService(pc, console, "Restart")).Message);
        // Stopping it would stop nothing, so it is not reported as done.
        var stop = Assert.Throws<AssistantException>(() => AssistantTools.RequireService(pc, console, "Stop")).Message;
        Assert.Contains("console window", stop);
        Assert.Contains("Nothing was changed", stop);
        Assert.Contains("no Windows service", Assert.Throws<AssistantException>(() => AssistantTools.RequireService(pc, noService, "Stop")).Message);
    }

    // ----- Names and the action log -----

    [Fact]
    public void ClientName_IsShownAsAQuotedNameInAFixedFrame()
    {
        Assert.Equal("An AI assistant (\"Claude Code\")", McpServer.ClientLabel("  Claude Code "));
        Assert.Equal("An AI assistant", McpServer.ClientLabel(null));
        Assert.Equal("An AI assistant", McpServer.ClientLabel(" \r\n\t "));

        // A name that tries to look like the app's own text: line breaks, a closed quotation, hidden characters.
        var label = McpServer.ClientLabel("Tool\") wants nothing.\r\n\r\nRunner Manager (\u201Ctrusted\u201D)\u202E\u2028\u0007 asks:\tAllow it");
        Assert.Equal("An AI assistant (\"Tool') wants nothing. Runner Manager ('trusted') asks: Allow it\")", label);
        Assert.Equal(2, label.Count(c => c == '"'));

        var cut = McpServer.ClientLabel(new string('x', 500));
        Assert.Equal("An AI assistant (\"" + new string('x', 80) + "…\")", cut);

        // In an approval only the start of the message is replaced by it.
        Assert.Equal("An AI assistant (\"Test\") wants to delete the runner “An AI assistant”.",
            AssistantHost.NameClient("An AI assistant wants to delete the runner “An AI assistant”.", TestClient));
    }

    [Fact]
    public async Task McpServer_NamesTheClientInTheFrame_ForApprovalsAndTheLog()
    {
        var clients = new List<string>();
        var server = new McpServer((client, _, _, _) => { clients.Add(client); return Task.FromResult(true); }, _ => Task.CompletedTask,
            async (_, _, approve, _, _) => await approve("Create a runner?", "An AI assistant wants to create a runner."));
        using var initialize = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"Evil\n2026-01-01 00:00:00  An AI assistant: Delete a runner? Allowed."}}}""");
        await server.HandleAsync(initialize.RootElement);
        using var call = JsonDocument.Parse(ToolCall(2));
        await server.HandleAsync(call.RootElement);

        var client = Assert.Single(clients);
        Assert.Equal("An AI assistant (\"Evil 2026-01-01 00:00:00 An AI assistant: Delete a runner? Allowed.\")", client);
    }

    // ----- The endpoint and the command line -----

    [Fact]
    public void Endpoint_LogsATakenPortOnce_AndWhenItListensAgain()
    {
        var port = TestPorts.FreeRange(1);
        using var server = new AssistantHttpServer(port, "localhost");
        using (var taken = new HttpListener())
        {
            taken.Prefixes.Add($"http://localhost:{port}/mcp/");
            taken.Start();
            // Tried again every 30 seconds while the agent runs.
            for (var i = 0; i < 4; i++) server.Start();
        }
        Assert.Single(AgentLogLines($"Could not start the AI assistant endpoint on port {port}:"));
        Assert.Empty(AgentLogLines($"The AI assistant endpoint listens on port {port} now."));

        server.Start();
        Assert.Single(AgentLogLines($"Could not start the AI assistant endpoint on port {port}:"));
        Assert.Single(AgentLogLines($"The AI assistant endpoint listens on port {port} now."));
    }

    // ----- Helpers -----

    private static MachineInfo Online(string name, params MachineRunnerStatus[] runners) => Online(name, managed: false, runners);

    private static MachineInfo Online(string name, bool managed, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name, Status(name, runners), managed: managed);

    /// <summary>A PC that does not answer now, listed with the last status it sent.</summary>
    private static MachineInfo Offline(string name, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name, Status(name, runners), problem: "Not reachable at 192.168.1.20:47823");

    private static MachineStatusPayload Status(string name, MachineRunnerStatus[] runners) => new()
    {
        MachineName = name,
        AppVersion = AppUpdater.CurrentVersion,
        AgentProtocolVersion = MachineRelay.ProtocolVersion,
        UpdatedUtc = DateTime.UtcNow,
        FleetAgentRunning = true,
        Runners = runners
    };

    /// <summary>An idle runner with a Windows service.</summary>
    private static MachineRunnerStatus Runner(string name, string repository) => new()
    {
        AgentName = name,
        Scope = $"repos/{repository}",
        RepositoryPath = repository,
        ServiceName = $"actions.runner.{repository.Replace('/', '-')}.{name}",
        ServiceInstalled = true,
        IsRunning = true,
        ActivityText = "Idle"
    };

    private static string ToolCall(int id, bool wait = false)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "create_runner", ["arguments"] = wait ? new JsonObject { ["wait"] = true } : new JsonObject() }
        }.ToJsonString();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static List<string> AgentLogLines(string text)
        => File.Exists(MachineRelay.AgentLogPath) ? File.ReadLines(MachineRelay.AgentLogPath).Where(line => line.Contains(text, StringComparison.Ordinal)).ToList() : new List<string>();

    private static AssistantHttpServer StartEndpoint(out string url, out int port)
    {
        port = TestPorts.FreeRange(1);
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

    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient http, string url, string token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<(byte[] Nonce, byte[] Sealed)> ChallengeAsync(HttpClient http, string url)
        => ReadChallenge(await (await http.PostAsync($"{url}/local/challenge", null)).Content.ReadAsStringAsync());

    private static (byte[] Nonce, byte[] Sealed) ReadChallenge(string text)
    {
        var challenge = JsonNode.Parse(text)!;
        return (Convert.FromBase64String(challenge["nonce"]!.GetValue<string>()), Convert.FromBase64String(challenge["challenge"]!.GetValue<string>()));
    }

    /// <param name="Token">The token this side worked out; the agent has the same one when the sign-in worked.</param>
    private sealed record SignIn(string Request, HttpStatusCode Status, string Answer, byte[] ExpectedAgentProof, string Token);

    /// <summary>Signs in the way <see cref="AgentLink"/> does, as a program of this Windows user, and keeps what was sent.</summary>
    /// <param name="challengeText">A challenge asked for earlier; a new one when null.</param>
    private static async Task<SignIn> SignInByHandAsync(HttpClient http, string url, int port, string? challengeText = null)
    {
        var (agentNonce, agentSealed) = ReadChallenge(challengeText ?? await (await http.PostAsync($"{url}/local/challenge", null)).Content.ReadAsStringAsync());
        var agentSecret = LocalSignIn.Open(agentSealed, LocalSignIn.AgentChallenge);
        Assert.NotNull(agentSecret);
        var clientNonce = RandomNumberGenerator.GetBytes(LocalSignIn.NonceBytes);
        var clientSecret = RandomNumberGenerator.GetBytes(LocalSignIn.SecretBytes);
        var clientSealed = LocalSignIn.Seal(clientSecret, LocalSignIn.ClientChallenge);
        var request = new JsonObject
        {
            ["nonce"] = Convert.ToBase64String(agentNonce),
            ["client_nonce"] = Convert.ToBase64String(clientNonce),
            ["client_challenge"] = Convert.ToBase64String(clientSealed),
            ["proof"] = Convert.ToBase64String(LocalSignIn.Proof(agentSecret, byAgent: false, port, agentNonce, clientNonce, agentSealed, clientSealed))
        }.ToJsonString();
        var response = await http.PostAsync($"{url}/local/session", Json(request));
        return new SignIn(request, response.StatusCode, await response.Content.ReadAsStringAsync(),
            LocalSignIn.Proof(clientSecret, byAgent: true, port, agentNonce, clientNonce, agentSealed, clientSealed),
            LocalSignIn.Token(agentSecret, clientSecret, port, agentNonce, clientNonce, agentSealed, clientSealed));
    }

    /// <summary>A program on the agent's port that is not the agent. It answers every request with what <paramref name="answer"/> gives.</summary>
    private static HttpListener StartFake(int port, Func<string, string, JsonObject> answer)
    {
        var fake = new HttpListener();
        fake.Prefixes.Add($"http://localhost:{port}/mcp/");
        fake.Start();
        _ = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext context;
                try { context = await fake.GetContextAsync(); }
                catch { return; }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var bytes = Encoding.UTF8.GetBytes(answer(context.Request.Url!.AbsolutePath, await reader.ReadToEndAsync()).ToJsonString());
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch { try { context.Response.Abort(); } catch { } }
            }
        });
        return fake;
    }
}

/// <summary>The action log is one file in the test state folder, so these run one at a time with the other tests that write it.</summary>
[Collection(FleetSettingsCollection.Name)]
public class AuditAssistantLogTests
{
    [Fact]
    public void ActionLog_KeepsEveryEntryOnOneLineOfLimitedLength()
    {
        var mark = Guid.NewGuid().ToString("N");
        var forged = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  An AI assistant: Delete a runner? Allowed.";
        // What a client can put into an entry: its name and a call's arguments, with line breaks and no end.
        AssistantLog.Append($"create_runner {mark}: done. {{\"name\":\"x\r\n{forged}\u2028{forged}\",\n  \"pad\":\"{new string('y', 300 * 1024)}\"}}");
        AssistantLog.Append($"second {mark}");

        var lines = File.ReadAllLines(AssistantLog.LogPath).Where(line => line.Contains(mark)).ToList();
        Assert.Equal(2, lines.Count);
        // The forged text stays inside the entry it came with: no line starts with it.
        Assert.DoesNotContain(File.ReadAllLines(AssistantLog.LogPath), line => line.StartsWith(forged, StringComparison.Ordinal));
        Assert.DoesNotContain('\u2028', lines[0]);
        Assert.EndsWith("…", lines[0]);
        Assert.InRange(lines[0].Length, 100, "2026-01-01 00:00:00  ".Length + AssistantLog.MaxEntryLength + 1);

        Assert.Equal("a b c…", AssistantLog.OneLine(" a\r\n\tb \u202E c d", 5));
        Assert.Equal(string.Empty, AssistantLog.OneLine(null, 10));
    }

    [Fact]
    public void ActionLog_StartsAgainWhenItIsLarge_AndStillShowsTheLatestEntries()
    {
        var older = AssistantLog.LogPath + ".1";
        var mark = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(FleetConfig.StateDir);
        // A log from before, which grew without limit.
        var line = $"2026-01-01 00:00:00  old {mark} {new string('x', 1000)}{Environment.NewLine}";
        File.WriteAllText(AssistantLog.LogPath, string.Concat(Enumerable.Repeat(line, (int)(AssistantLog.MaxLogBytes / line.Length) + 2)));
        try
        {
            AssistantLog.Append($"new {mark}");

            Assert.True(File.Exists(older));
            Assert.True(new FileInfo(older).Length > AssistantLog.MaxLogBytes);
            Assert.Single(File.ReadAllLines(AssistantLog.LogPath));
            // Settings shows the latest entries, newest first; the rest comes from the older file.
            var recent = AssistantLog.Recent(8);
            Assert.Equal(8, recent.Count);
            Assert.EndsWith($"new {mark}", recent[0]);
            Assert.All(recent.Skip(1), entry => Assert.Contains($"old {mark}", entry));
        }
        finally
        {
            File.Delete(AssistantLog.LogPath);
            File.Delete(older);
        }
    }
}
