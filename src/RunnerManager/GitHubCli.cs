using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace RunnerManager;

public static class GitHubCli
{
    /// <summary>How long gh may take when the caller sets no other limit. Every status poll uses it.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
    /// <summary>Status polls share this gate, so a refresh never starts more than four gh processes at a time.</summary>
    private static readonly SemaphoreSlim PollGate = new(4);

    public static string ExecutablePath
    {
        get
        {
            var installed = @"C:\Program Files\GitHub CLI\gh.exe";
            return File.Exists(installed) ? installed : "gh.exe";
        }
    }

    /// <summary>Environment variables that make gh use a token instead of the stored sign-in.</summary>
    private static readonly string[] TokenVariables = { "GH_TOKEN", "GITHUB_TOKEN" };
    /// <summary>Set by <see cref="IsAuthenticatedAsync"/>: use GH_TOKEN or GITHUB_TOKEN, because there is no stored sign-in.</summary>
    private static volatile bool _useEnvironmentToken;
    /// <summary>
    /// The first check of which sign-in to use, for processes that run gh before anything called
    /// <see cref="IsAuthenticatedAsync"/> (the fleet agent, AI assistant tools).
    /// </summary>
    private static Task? _tokenChoice;
    private static readonly object TokenChoiceGate = new();

    /// <summary>
    /// Whether gh can use a github.com sign-in, checked the way the app's own gh calls then run. The stored sign-in
    /// (made with "Connect GitHub") is used when it works: GH_TOKEN or GITHUB_TOKEN would otherwise override it, often
    /// with a token made for something else. They are used only when there is no working stored sign-in.
    /// When GitHub did not answer the check (no network, a timeout, a rate limit, an error at GitHub), the answer is
    /// the last one GitHub gave, and <see cref="SignInUnconfirmed"/> is true: a PC without a network is not signed out.
    /// </summary>
    public static async Task<bool> IsAuthenticatedAsync()
    {
        lock (TokenChoiceGate) _tokenChoice ??= Task.CompletedTask;
        var stored = await CheckSignInAsync(environmentToken: false);
        // Without an answer for the stored sign-in, the token variables would not be answered either, and which
        // sign-in the gh calls use stays as it was.
        if (stored == SignIn.Unconfirmed) return LastKnownSignIn();
        if (stored == SignIn.Works || EnvironmentTokenName() == null)
        {
            _useEnvironmentToken = false;
            return SignInAnswered(stored == SignIn.Works);
        }
        var token = await CheckSignInAsync(environmentToken: true);
        if (token == SignIn.Unconfirmed) return LastKnownSignIn();
        _useEnvironmentToken = token == SignIn.Works;
        return SignInAnswered(token == SignIn.Works);
    }

    /// <summary>
    /// True when the last <see cref="IsAuthenticatedAsync"/> could not get an answer from GitHub, so what it returned
    /// is the last known state, not a new answer. The polls then fail as well, and say why
    /// (<see cref="CurrentProblem"/>).
    /// </summary>
    public static bool SignInUnconfirmed => _signInUnconfirmed;

    private enum SignIn
    {
        Works,
        /// <summary>gh has no sign-in, or GitHub refused it (HTTP 401).</summary>
        Refused,
        /// <summary>gh has a sign-in, but GitHub did not answer whether it works.</summary>
        Unconfirmed
    }

    private static readonly object SignInGate = new();
    /// <summary>What GitHub last answered about the sign-in; null before any answer.</summary>
    private static bool? _lastSignIn;
    private static volatile bool _signInUnconfirmed;

    private static bool SignInAnswered(bool works)
    {
        lock (SignInGate) (_lastSignIn, _signInUnconfirmed) = (works, false);
        if (works) GitHubFailures.SignInWorks();
        return works;
    }

    /// <summary>
    /// The sign-in GitHub could not be asked about. Before any answer, it counts as working: gh has an account for
    /// github.com, and what fails is the connection, which the polls report as such.
    /// </summary>
    private static bool LastKnownSignIn()
    {
        lock (SignInGate)
        {
            _signInUnconfirmed = true;
            return _lastSignIn ?? true;
        }
    }

    /// <summary>For tests, and after a disconnect: forgets what GitHub last answered about the sign-in.</summary>
    internal static void ForgetSignIn()
    {
        lock (SignInGate) (_lastSignIn, _signInUnconfirmed) = (null, false);
    }

    /// <summary>
    /// Why GitHub could not be read for a runner's scope ("repos/owner/repo" or "orgs/name"), or null when its last
    /// status polls worked. The dashboard asks GitHub every few seconds; after a failure it waits (see
    /// <see cref="GitHubFailures"/>), and this says why and until when.
    /// </summary>
    public static GitHubProblem? ProblemFor(string? scope)
        => GitHubNames.IsScope(scope) ? GitHubFailures.Current(scope) : GitHubFailures.CurrentPause();

    /// <summary>Why GitHub could not be read just now, whatever was asked, or null when the last status polls worked.</summary>
    public static GitHubProblem? CurrentProblem => GitHubFailures.Current(null);

    /// <summary>
    /// Forgets the failures, so GitHub is asked again at once (after the user connects GitHub or asks for a refresh).
    /// The job view's failed reads end with them: they are only kept while a failure holds their requests back. The
    /// health checks' runner lists are asked again too, also those that GitHub answered with 404 (see
    /// <see cref="RegisteredRunners"/>).
    /// </summary>
    public static void ForgetProblems()
    {
        GitHubFailures.Forget();
        RegisteredRunners.Forget();
    }

    private static readonly string[] ActiveAuthStatus = { "auth", "status", "--active", "--hostname", "github.com" };
    private static readonly string[] AuthStatus = { "auth", "status", "--hostname", "github.com" };
    /// <summary>gh older than 2.57.0 (September 2024) does not know --active. Remembered, so it is not tried at every check.</summary>
    private static volatile bool _noActiveFlag;

    /// <summary>For tests: checks again whether gh knows --active.</summary>
    internal static void ForgetGhFeatures() => _noActiveFlag = false;

    /// <summary>
    /// gh auth status for the active github.com account. Without --active, gh checks every account it has stored for
    /// github.com and fails when any of them has a problem (a second account whose sign-in expired), although gh
    /// itself keeps working with the active one. A gh that does not know --active is asked about all accounts.
    /// </summary>
    private static async Task<CommandResult> AuthStatusAsync(bool environmentToken)
    {
        if (!_noActiveFlag)
        {
            var active = await RunAsync(ActiveAuthStatus, environmentToken);
            if (active.ExitCode == 0 || !active.Error.Contains("unknown flag: --active", StringComparison.OrdinalIgnoreCase)) return active;
            _noActiveFlag = true;
        }
        return await RunAsync(AuthStatus, environmentToken);
    }

    private static readonly string[] SignedInUser = { "api", "user", "--jq", ".login" };

    /// <summary>Whether the active github.com account works, with the stored sign-in or with the token variables.</summary>
    private static async Task<SignIn> CheckSignInAsync(bool environmentToken)
    {
        var status = await AuthStatusAsync(environmentToken);
        if (status.ExitCode == 0) return SignIn.Works;
        // gh is missing, or says that no account is signed in: nothing to ask GitHub about.
        if (status.Error.StartsWith(GitHubFailures.GhMissingMessage, StringComparison.Ordinal)
            || (status.Error + status.Output).Contains("not logged in", StringComparison.OrdinalIgnoreCase)) return SignIn.Refused;
        // gh did not finish: it waited for GitHub.
        if (status.Error.StartsWith(GitHubFailures.TimedOutStart, StringComparison.Ordinal)) return SignIn.Unconfirmed;
        // The check failed for an account gh has. gh says the same ("Failed to log in", "the token is invalid") when it
        // could not reach GitHub as when GitHub refused the token, and a gh without --active also fails because of
        // another account. Asking GitHub who is signed in uses the active account alone, and tells these apart.
        var user = await RunAsync(SignedInUser, environmentToken);
        if (user.ExitCode == 0) return string.IsNullOrWhiteSpace(user.Output) ? SignIn.Refused : SignIn.Works;
        return SignInRefused(user) ? SignIn.Refused : SignIn.Unconfirmed;
    }

    /// <summary>
    /// GitHub refused the sign-in ("gh: Bad credentials (HTTP 401)", or "gh: HTTP 401"), or gh has none to use (it
    /// exits with 4 and names its login command). No connection, a timeout, a rate limit (403) or an error at GitHub
    /// (5xx) say nothing about the sign-in.
    /// </summary>
    private static bool SignInRefused(CommandResult result)
        => result.ExitCode == 4
           || result.Error.Contains("HTTP 401", StringComparison.Ordinal)
           || result.Error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase)
           || result.Error.StartsWith(GitHubFailures.GhMissingMessage, StringComparison.Ordinal);

    /// <summary>GH_TOKEN or GITHUB_TOKEN when one is set, otherwise null.</summary>
    public static string? EnvironmentTokenName()
        => TokenVariables.FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    public static void LaunchLogin()
        => LaunchInConsole("auth login --web --hostname github.com --git-protocol https");

    /// <summary>Opens gh to add the admin:org permission that organization runners need to the stored sign-in.</summary>
    public static void LaunchOrgPermissionRefresh()
        => LaunchInConsole($"auth refresh --hostname github.com --scopes {OrgScope}");

    /// <summary>
    /// Runs gh in a console window the user can answer. GH_HOST and the token variables are cleared for it, since
    /// gh login refuses to run while a token variable is set, and the app only works with github.com.
    /// </summary>
    private static void LaunchInConsole(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{ExecutablePath}\" {arguments}\"",
            UseShellExecute = false
        };
        PrepareEnvironment(startInfo, environmentToken: false);
        using var process = Process.Start(startInfo);
    }

    /// <summary>
    /// The app only supports github.com, so GH_HOST (which sends gh api calls to another host) is always cleared. The
    /// token variables are cleared unless <see cref="IsAuthenticatedAsync"/> found them to be the only working sign-in.
    /// </summary>
    private static void PrepareEnvironment(ProcessStartInfo startInfo, bool environmentToken)
    {
        startInfo.Environment.Remove("GH_HOST");
        if (environmentToken) return;
        foreach (var name in TokenVariables) startInfo.Environment.Remove(name);
    }

    private const string OrgScope = "admin:org";

    internal const string OrgScopeMessage = "Organization runners need the admin:org permission, which your GitHub sign-in does not have. " +
        "Run gh auth refresh -h github.com -s admin:org in a terminal, then try again. You also need to be an owner of the organization.";

    /// <summary>
    /// True when gh auth status lists the token's scopes and admin:org is not among them. Fine-grained tokens and
    /// GitHub App tokens have no scope list, so they are not reported as missing it.
    /// </summary>
    internal static bool LacksOrgScope(string authStatus)
    {
        var line = authStatus.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.Contains("Token scopes:", StringComparison.OrdinalIgnoreCase));
        return line != null && !line.Contains(OrgScope, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DisconnectAsync()
    {
        var user = await ApiAsync("user", "--jq", ".login");
        var login = user.Output.Trim();
        if (user.ExitCode != 0 || !GitHubNames.IsOwner(login))
            throw new InvalidOperationException("Could not determine the active GitHub account.");
        var logout = await RunAsync(new[] { "auth", "logout", "--hostname", "github.com", "--user", login });
        if (logout.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(logout.Error)
                ? "Could not disconnect the GitHub account." : logout.Error.Trim());
        // The remembered failures were that account's: its rate limit, what it may read. So was its sign-in state.
        ForgetProblems();
        ForgetSignIn();
    }

    /// <summary>What a request says when its repository or organization name failed <see cref="GitHubNames"/>.</summary>
    internal const string InvalidNameMessage =
        "The request was not sent to GitHub, because the repository or organization name is not a valid GitHub name.";
    private const string InvalidEndpointMessage =
        "The request was not sent to GitHub, because its address is not one that Runner Manager uses.";

    /// <param name="scope">"repos/owner/repo" or "orgs/name" (see RunnerService.ApiScope).</param>
    public static async Task<string> GetRunnerTokenAsync(string scope, bool removal)
    {
        if (!GitHubNames.IsScope(scope)) throw new InvalidOperationException(InvalidNameMessage);
        var endpoint = removal
            ? $"{scope}/actions/runners/remove-token"
            : $"{scope}/actions/runners/registration-token";
        var result = await ApiAsync(endpoint, "--method", "POST", "--jq", ".token");
        // A classic token without admin:org gets 403 or 404 from the organization endpoints. A 404 would otherwise look
        // like a deleted organization, so this is checked first.
        if (result.ExitCode != 0 && scope.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase)
            && (IsNotFound(result.Error) || result.Error.Contains("(HTTP 403)", StringComparison.Ordinal)))
        {
            // The active account's scopes: the request above ran with it.
            var status = await AuthStatusAsync(_useEnvironmentToken);
            if (LacksOrgScope(status.Output + "\n" + status.Error)) throw new GitHubPermissionException(OrgScopeMessage);
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            throw new InvalidOperationException(result.Error.Trim().Length > 0
                ? result.Error.Trim()
                : "GitHub did not return a runner token. Try again.");
        return result.Output.Trim();
    }

    /// <summary>
    /// The runner's state for the dashboard. Read from one list per repository or organization, shared by its runners
    /// and polled with an ETag (unchanged answers do not count against the rate limit), instead of one request per
    /// runner. Runners beyond the first 100 of a scope are looked up one by one.
    /// </summary>
    public static async Task<RemoteRunnerState?> GetRunnerStateAsync(string scope, long agentId)
    {
        if (agentId <= 0 || !GitHubNames.IsScope(scope)) return null;
        var list = await GetRunnerListAsync(scope);
        if (list != null && list.Runners.TryGetValue(agentId, out var state)) return state;
        // A complete list without this runner means GitHub has no such registration, as a 404 from the lookup would.
        if (list is { Complete: true }) return null;
        return await PollRunnerAsync(scope, agentId);
    }

    /// <summary>
    /// One runner, for the dashboard. Read as a poll: when the list failed, every runner of the scope comes here on
    /// every refresh, and a failed poll is not sent again right away.
    /// </summary>
    private static async Task<RemoteRunnerState?> PollRunnerAsync(string scope, long agentId)
    {
        var endpoint = $"{scope}/actions/runners/{GitHubNames.Id(agentId)}";
        var sentIn = GitHubFailures.Generation;
        var response = await GetConditionalAsync(endpoint, null);
        if (!response.Ok) return null;
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            return ReadRunnerState(json.RootElement);
        }
        catch
        {
            // Paused like a failed poll, so it is not asked again at every refresh.
            GitHubFailures.Unreadable(endpoint, sentIn);
            return null;
        }
    }

    private sealed record RunnerList(Dictionary<long, RemoteRunnerState> Runners, bool Complete);

    private static readonly TimeSpan RunnerListLifetime = TimeSpan.FromSeconds(8);
    private static readonly object RunnerListLock = new();
    /// <summary>One request per scope and refresh: runners of the same scope share the task. A failed task (null) is not reused.</summary>
    private static readonly Dictionary<string, (DateTime Time, Task<RunnerList?> List)> RunnerLists = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string ETag, RunnerList List)> RunnerListETags = new(StringComparer.OrdinalIgnoreCase);

    private static Task<RunnerList?> GetRunnerListAsync(string scope)
    {
        lock (RunnerListLock)
        {
            if (RunnerLists.TryGetValue(scope, out var cached) && DateTime.UtcNow - cached.Time < RunnerListLifetime
                && !(cached.List.IsFaulted || cached.List.IsCanceled || (cached.List.IsCompletedSuccessfully && cached.List.Result == null)))
                return cached.List;
            var task = Task.Run(() => ReadRunnerListAsync(scope));
            RunnerLists[scope] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<RunnerList?> ReadRunnerListAsync(string scope)
    {
        (string ETag, RunnerList List)? previous;
        lock (RunnerListLock) previous = RunnerListETags.TryGetValue(scope, out var entry) ? entry : null;
        var endpoint = $"{scope}/actions/runners?per_page=100";
        var sentIn = GitHubFailures.Generation;
        var response = await GetConditionalAsync(endpoint, previous?.ETag);
        if (response.NotModified && previous != null) return previous.Value.List;
        if (!response.Ok) return null;
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            var list = ParseRunnerList(json.RootElement);
            lock (RunnerListLock) RunnerListETags[scope] = (response.ETag, list);
            return list;
        }
        catch
        {
            GitHubFailures.Unreadable(endpoint, sentIn);
            return null;
        }
    }

    /// <summary>The runners in a list response by ID; Complete is false when GitHub has more than this page holds.</summary>
    private static RunnerList ParseRunnerList(JsonElement root)
    {
        var runners = new Dictionary<long, RemoteRunnerState>();
        if (root.TryGetProperty("runners", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    runners[id.GetInt64()] = ReadRunnerState(item);
        var total = root.TryGetProperty("total_count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : int.MaxValue;
        return new RunnerList(runners, total <= runners.Count);
    }

    /// <summary>For tests: the IDs in a list response and whether the list is complete.</summary>
    internal static (long[] Ids, bool Complete) ReadRunnerList(JsonElement root)
    {
        var list = ParseRunnerList(root);
        return (list.Runners.Keys.Order().ToArray(), list.Complete);
    }

    /// <summary>
    /// The runner's registration on GitHub. State is null when it could not be read; NotFound tells a confirmed
    /// 404 (the runner or repository is gone) apart from timeouts, rate limits and other failures. For changes the
    /// user starts: it always asks GitHub, also while failed polls are paused.
    /// </summary>
    public static async Task<RunnerLookup> LookUpRunnerAsync(string scope, long agentId)
    {
        if (agentId <= 0 || !GitHubNames.IsScope(scope)) return new RunnerLookup(null, NotFound: false);
        var result = await ApiAsync($"{scope}/actions/runners/{GitHubNames.Id(agentId)}", Array.Empty<string>(), poll: true);
        if (result.ExitCode != 0) return new RunnerLookup(null, IsNotFound(result.Error));
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            return new RunnerLookup(ReadRunnerState(root), NotFound: false);
        }
        catch { return new RunnerLookup(null, NotFound: false); }
    }

    internal static RemoteRunnerState ReadRunnerState(JsonElement root) => new()
    {
        Status = root.TryGetProperty("status", out var status) ? status.GetString() ?? "unknown" : "unknown",
        Busy = root.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
        Labels = ReadCustomLabels(root),
        HasDefaultLabels = HasLabel(root, "self-hosted")
    };

    /// <summary>
    /// The IDs of the runners registered with this name in a repository or organization (names are unique per scope,
    /// ignoring case). Null when GitHub could not be read.
    /// </summary>
    public static async Task<long[]?> FindRunnerIdsByNameAsync(string scope, string name)
    {
        if (!GitHubNames.IsScope(scope) || string.IsNullOrWhiteSpace(name)) return null;
        var result = await ApiAsync($"{scope}/actions/runners?name={Uri.EscapeDataString(name)}&per_page=100");
        if (result.ExitCode != 0) return null;
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            return ReadRunnerIdsNamed(json.RootElement, name);
        }
        catch { return null; }
    }

    /// <summary>The IDs in a runner list response whose name matches, ignoring case (older APIs ignore the name filter).</summary>
    internal static long[] ReadRunnerIdsNamed(JsonElement list, string name)
    {
        if (!list.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array) return Array.Empty<long>();
        return runners.EnumerateArray()
            .Where(r => r.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0)
            .ToArray();
    }

    /// <summary>gh reports a GitHub 404 as "gh: Not Found (HTTP 404)".</summary>
    internal static bool IsNotFound(string error) => error.Contains("(HTTP 404)", StringComparison.Ordinal);

    private static bool HasLabel(JsonElement root, string label)
        => root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
           && labels.EnumerateArray().Any(l => l.TryGetProperty("name", out var n)
                                               && string.Equals(n.GetString(), label, StringComparison.OrdinalIgnoreCase));

    private static string[] ReadCustomLabels(JsonElement root)
    {
        if (!root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var result = new List<string>();
        foreach (var label in labels.EnumerateArray())
        {
            var type = label.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            var name = label.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (type == "custom" && !string.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result.ToArray();
    }

    /// <summary>
    /// GET with an ETag, for status polls. GitHub does not count 304 Not Modified responses against the rate limit,
    /// so polling the same resource this way is effectively free while nothing changes. A poll that failed is not
    /// sent again for a while (see <see cref="GitHubFailures"/>): it is then answered as failed (status 0) at once.
    /// </summary>
    public static async Task<ConditionalResult> GetConditionalAsync(string endpoint, string? etag)
    {
        var known = GitHubNames.IsETag(etag) ? etag! : string.Empty;
        var notSent = new ConditionalResult(0, known, string.Empty);
        if (!GitHubNames.IsApiEndpoint(endpoint) || GitHubFailures.Blocking(endpoint) != null) return notSent;
        var options = new List<string> { "-i" };
        if (known.Length > 0)
        {
            // One argument, so the quotes and spaces of the ETag stay inside the header.
            options.Add("-H");
            options.Add($"If-None-Match: {known}");
        }

        CommandResult result;
        int generation;
        await PollGate.WaitAsync();
        try
        {
            // Polls that waited here while another one found GitHub unreachable or rate-limited do not start gh either.
            if (GitHubFailures.Blocking(endpoint) != null) return notSent;
            // A failure is not stored when the failures were forgotten while this request waited for its answer.
            generation = GitHubFailures.Generation;
            result = await ApiAsync(endpoint, options);
        }
        finally { PollGate.Release(); }

        var text = result.Output.Replace("\r\n", "\n");
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = split >= 0 ? text[..split] : text;
        var body = split >= 0 ? text[(split + 2)..] : string.Empty;

        var status = 0;
        var newEtag = string.Empty;
        string? remaining = null, reset = null, retryAfter = null, date = null;
        foreach (var line in headers.Split('\n'))
        {
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ', 3);
                if (parts.Length >= 2) int.TryParse(parts[1], out status);
            }
            else if (HeaderValue(line, "ETag") is { } tag) newEtag = tag;
            else if (HeaderValue(line, "X-RateLimit-Remaining") is { } left) remaining = left;
            else if (HeaderValue(line, "X-RateLimit-Reset") is { } at) reset = at;
            else if (HeaderValue(line, "Retry-After") is { } wait) retryAfter = wait;
            else if (HeaderValue(line, "Date") is { } sent) date = sent;
        }
        var response = new ConditionalResult(status, string.IsNullOrEmpty(newEtag) ? known : newEtag, body);
        if (response.Ok || response.NotModified) GitHubFailures.Succeeded(endpoint);
        else GitHubFailures.Failed(endpoint, status, new LimitHeaders(remaining, reset, retryAfter, date), result, generation);
        return response;
    }

    /// <summary>The value of a response header line with this name (gh prints "Etag", "X-Ratelimit-Reset"), otherwise null.</summary>
    private static string? HeaderValue(string line, string name)
        => line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            ? line[(name.Length + 1)..].Trim() : null;

    /// <summary>
    /// Runs gh api for an endpoint, with each option as its own argument. The endpoint is checked here once more, so a
    /// caller that forgot a name check still cannot pass a flag or reach another part of the API.
    /// </summary>
    internal static Task<CommandResult> ApiAsync(string endpoint, params string[] options)
        => ApiAsync(endpoint, options, poll: false);

    /// <param name="poll">A status poll: it waits for the shared gate (at most four gh processes at a time).</param>
    /// <param name="standardInput">The request body, for options that end with "--input", "-".</param>
    internal static async Task<CommandResult> ApiAsync(string endpoint, IReadOnlyList<string> options, bool poll = false,
        string? standardInput = null)
    {
        if (!GitHubNames.IsApiEndpoint(endpoint)) return new CommandResult(-1, string.Empty, InvalidEndpointMessage);
        var arguments = new List<string>(options.Count + 2) { "api", endpoint };
        arguments.AddRange(options);
        if (!poll) return await RunAsync(arguments, standardInput);
        await PollGate.WaitAsync();
        try { return await RunAsync(arguments, standardInput); }
        finally { PollGate.Release(); }
    }

    /// <summary>
    /// Runs gh with one command line. Only for text this code wrote itself: a value from anywhere else (another PC,
    /// GitHub, a file, the user) could add arguments here, so it goes through the overload that takes a list.
    /// </summary>
    public static async Task<CommandResult> RunAsync(string arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        await ChooseTokenOnceAsync();
        var startInfo = new ProcessStartInfo { FileName = ExecutablePath, Arguments = arguments };
        return await StartAsync(startInfo, FirstWords(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), 4),
            _useEnvironmentToken, standardInput, timeout);
    }

    /// <summary>
    /// Runs gh with each argument passed on its own. Whatever an argument contains (spaces, quotes, dashes), it stays
    /// that one argument, so a value cannot add a flag or a second endpoint.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        await ChooseTokenOnceAsync();
        return await RunAsync(arguments, _useEnvironmentToken, standardInput, timeout);
    }

    /// <summary>Decides once per process whether a token variable is needed, when one is set and nothing checked yet.</summary>
    private static Task ChooseTokenOnceAsync()
    {
        if (EnvironmentTokenName() == null) return Task.CompletedTask;
        lock (TokenChoiceGate) return _tokenChoice ??= IsAuthenticatedAsync();
    }

    private static Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, bool environmentToken, string? standardInput = null,
        TimeSpan? timeout = null)
    {
        // Windows ends a command line at the first NUL, which would drop the arguments after it.
        if (arguments.Any(argument => argument.Contains('\0')))
            return Task.FromResult(new CommandResult(-1, string.Empty, "GitHub CLI (gh) was not started, because a value cannot be passed to it."));
        var startInfo = new ProcessStartInfo { FileName = ExecutablePath };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return StartAsync(startInfo, FirstWords(arguments, 2), environmentToken, standardInput, timeout);
    }

    /// <summary>Starts gh. Tests replace it to see the arguments without starting a process.</summary>
    internal static Func<ProcessStartInfo, TimeSpan, string?, Task<CommandResult?>> StartProcess { get; set; } = ProcessRunner.RunAsync;

    /// <param name="what">The start of the command, for the message when gh does not finish in time.</param>
    private static async Task<CommandResult> StartAsync(ProcessStartInfo startInfo, string what, bool environmentToken,
        string? standardInput, TimeSpan? timeout)
    {
        CommandResult? result;
        try
        {
            PrepareEnvironment(startInfo, environmentToken);
            result = await StartProcess(startInfo, timeout ?? DefaultTimeout, standardInput);
        }
        catch (Win32Exception)
        {
            return new CommandResult(-1, string.Empty, GitHubFailures.GhMissingMessage);
        }
        return result ?? new CommandResult(-1, string.Empty,
            $"{GitHubFailures.TimedOutStart} (gh {what}). Check the internet connection and try again.");
    }

    private static string FirstWords(IEnumerable<string> arguments, int count) => string.Join(' ', arguments.Take(count));
}

/// <summary>Runs a console process with redirected output and a time limit.</summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Starts the process hidden, captures its output and waits for it to exit. Returns null when it did not finish
    /// within the timeout; the whole process tree is then killed. Start failures (Win32Exception) are not caught.
    /// </summary>
    internal static async Task<CommandResult?> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, string? standardInput = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = standardInput != null;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(startInfo.FileName)}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (standardInput != null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>"60 seconds" or "10 minutes", for timeout messages.</summary>
    internal static string Describe(TimeSpan timeout)
        => timeout.TotalSeconds < 120 ? $"{(int)timeout.TotalSeconds} seconds" : $"{(int)timeout.TotalMinutes} minutes";
}

public sealed record CommandResult(int ExitCode, string Output, string Error);

/// <summary>GitHub refused a request because the sign-in lacks a permission (not because something is gone).</summary>
public sealed class GitHubPermissionException(string message) : InvalidOperationException(message);

public sealed record ConditionalResult(int StatusCode, string ETag, string Body)
{
    public bool NotModified => StatusCode == 304;
    public bool Ok => StatusCode is >= 200 and < 300;
}

/// <summary>A runner registration read from GitHub; see GitHubCli.LookUpRunnerAsync.</summary>
public sealed record RunnerLookup(RemoteRunnerState? State, bool NotFound);

public sealed class RemoteRunnerState
{
    public string Status { get; init; } = "unknown";
    public bool Busy { get; init; }
    public string[] Labels { get; init; } = Array.Empty<string>();
    /// <summary>False when the runner was registered with --no-default-labels (it has no "self-hosted" label).</summary>
    public bool HasDefaultLabels { get; init; } = true;
}
