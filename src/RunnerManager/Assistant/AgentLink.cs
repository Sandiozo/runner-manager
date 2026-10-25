using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

/// <summary>The fleet agent could not be reached, or refused the request.</summary>
public sealed class AgentLinkException(string message) : Exception(message);

/// <summary>
/// How <c>--mcp</c> and <c>ai</c> reach this PC's fleet agent, which runs the assistant tools. They read nothing from
/// Runner Manager's settings, so they work the same when an AI tool runs them in a sandbox.
/// </summary>
public sealed class AgentLink : IDisposable
{
    /// <summary>Overrides the agent's MCP URL (used by tests).</summary>
    public const string UrlVariable = "RUNNER_MANAGER_AGENT_MCP_URL";

    /// <summary>After a failed connection, later requests fail at once for a while instead of each waiting for Windows.</summary>
    private static readonly TimeSpan UnreachableMemory = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly string _url;
    private string? _token;
    private string? _sessionId;
    private string? _initialize;
    private (DateTime Until, string Message)? _unreachable;

    public AgentLink(string? url = null)
        => _url = (url ?? Environment.GetEnvironmentVariable(UrlVariable) ?? AssistantHttpServer.Url).TrimEnd('/');

    /// <summary>Sends one JSON-RPC message (or batch). Returns the answer, or null when there is none (a notification).</summary>
    public async Task<string?> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (IsInitialize(message)) _initialize = message;
        for (var attempt = 0; ; attempt++)
        {
            var token = await SignInAsync(cancellationToken);
            using var response = await PostAsync(message, token, cancellationToken);
            // The agent restarted and forgot this session: sign in again, and tell it who is asking.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null;
                _sessionId = null;
                if (_initialize != null && !IsInitialize(message))
                {
                    using var again = await PostAsync(_initialize, await SignInAsync(cancellationToken), cancellationToken);
                }
                continue;
            }
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if (!response.IsSuccessStatusCode)
                throw new AgentLinkException($"The fleet agent answered HTTP {(int)response.StatusCode}. {ErrorText(text)}".Trim());
            return text;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string message, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (_sessionId != null) request.Headers.Add("Mcp-Session-Id", _sessionId);
        var response = await Send(request, cancellationToken);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.First();
        return response;
    }

    /// <summary>Proves to the agent that this process runs as the same Windows user: it encrypts a challenge with DPAPI.</summary>
    private async Task<string> SignInAsync(CancellationToken cancellationToken)
    {
        if (_token is { } current) return current;
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } signedIn) return signedIn;
            using var challenge = await Send(new HttpRequestMessage(HttpMethod.Post, $"{_url}/local/challenge"), cancellationToken);
            if (!challenge.IsSuccessStatusCode)
                throw new AgentLinkException($"This PC's fleet agent runs an older Runner Manager (HTTP {(int)challenge.StatusCode}). Update Runner Manager.");
            var nonce = JsonNode.Parse(await challenge.Content.ReadAsStringAsync(cancellationToken))?["nonce"]?.GetValue<string>()
                        ?? throw new AgentLinkException("The fleet agent sent an invalid sign-in challenge.");
            var proof = Convert.ToBase64String(ProtectedData.Protect(Convert.FromBase64String(nonce), AssistantHttpServer.ProofEntropy,
                DataProtectionScope.CurrentUser));
            var body = new JsonObject { ["nonce"] = nonce, ["proof"] = proof }.ToJsonString();
            using var session = await Send(new HttpRequestMessage(HttpMethod.Post, $"{_url}/local/session")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }, cancellationToken);
            var text = await session.Content.ReadAsStringAsync(cancellationToken);
            if (!session.IsSuccessStatusCode) throw new AgentLinkException(ErrorText(text) is { Length: > 0 } error ? error : $"Sign-in failed (HTTP {(int)session.StatusCode}).");
            return _token = JsonNode.Parse(text)?["token"]?.GetValue<string>() ?? throw new AgentLinkException("The fleet agent sent no session token.");
        }
        finally { _signInGate.Release(); }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_unreachable is { } known && DateTime.UtcNow < known.Until) throw new AgentLinkException(known.Message);
        try { return await _http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException ex)
        {
            var message = $"Runner Manager's fleet agent is not running on this PC ({ex.Message}). " +
                          "Open Runner Manager and turn on Settings → AI assistants; it starts the agent.";
            _unreachable = (DateTime.UtcNow + UnreachableMemory, message);
            throw new AgentLinkException(message);
        }
    }

    private static bool IsInitialize(string message)
    {
        if (!message.Contains("\"initialize\"", StringComparison.Ordinal)) return false;
        try { return JsonNode.Parse(message) is JsonObject o && o["method"]?.GetValue<string>() == "initialize"; }
        catch (JsonException) { return false; }
    }

    private static string ErrorText(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _signInGate.Dispose();
    }
}
