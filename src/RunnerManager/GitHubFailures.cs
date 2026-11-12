using System.Globalization;

namespace RunnerManager;

/// <summary>
/// Why GitHub could not be read, for the UI. <paramref name="Reason"/> is a full sentence in the app's words and
/// <paramref name="Detail"/> the first line of what gh said (may be empty). Until <paramref name="RetryUtc"/> the
/// request is not sent again.
/// </summary>
public sealed record GitHubProblem(string Reason, DateTime RetryUtc, bool RateLimited, string Detail);

/// <summary>
/// The headers of GitHub's answer that say how long to wait: x-ratelimit-remaining, x-ratelimit-reset (seconds since
/// 1970), retry-after (seconds) and date. Each is null when the answer did not have it.
/// </summary>
internal readonly record struct LimitHeaders(string? Remaining = null, string? Reset = null, string? RetryAfter = null, string? Date = null);

/// <summary>
/// Remembers failed status polls. The dashboard asks GitHub every few seconds, so without this a rate limit or a lost
/// connection starts gh again on every tick for every repository and runner. A failed endpoint is left alone for a
/// while; a failure that is not about one endpoint (rate limit, no connection, no sign-in) pauses every poll. Requests
/// the user starts (register, remove, re-run) are never held back.
/// </summary>
internal static class GitHubFailures
{
    /// <summary>How long a failed endpoint is not asked again.</summary>
    internal static readonly TimeSpan EndpointPause = TimeSpan.FromSeconds(45);
    /// <summary>How long nothing is asked after GitHub could not be reached at all.</summary>
    internal static readonly TimeSpan ConnectionPause = TimeSpan.FromSeconds(30);
    /// <summary>GitHub's rate limits reset within the hour, so a longer wait in an answer is not believed.</summary>
    internal static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(61);
    /// <summary>How long a failure stays readable after its pause ended, when nothing has asked again since.</summary>
    private static readonly TimeSpan Memory = TimeSpan.FromMinutes(2);
    /// <summary>How many failed endpoints are remembered. A PC can report any number of repositories that do not exist.</summary>
    internal const int MaxEndpoints = 500;

    internal const string GhMissingMessage = "GitHub CLI (gh) was not found. Runner Manager looks for it in Program Files and in the PATH of the PC, " +
        "not in the PATH of one Windows user. Install it for all users from https://cli.github.com/ (its installer does that) and restart Runner Manager.";
    /// <summary>How the message of a gh call that did not finish in time starts.</summary>
    internal const string TimedOutStart = "GitHub CLI timed out";

    /// <summary>The time, which tests set.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, GitHubProblem> Endpoints = new(StringComparer.OrdinalIgnoreCase);
    private static GitHubProblem? _pause;
    /// <summary>The pause is about the sign-in, so it ends when a sign-in check passes.</summary>
    private static bool _pauseIsSignIn;
    /// <summary>When GitHub last answered a poll, with whatever status: it could be reached then.</summary>
    private static DateTime? _lastAnswer;
    /// <summary>Counts the calls of <see cref="Forget"/>, so a poll that was sent before one does not store its failure after it.</summary>
    private static int _generation;

    /// <summary>Taken right before a poll is sent, and given to <see cref="Failed"/> with its answer.</summary>
    internal static int Generation
    {
        get { lock (Gate) return _generation; }
    }

    /// <summary>The remembered failure because of which a poll of this endpoint is not sent now, or null.</summary>
    internal static GitHubProblem? Blocking(string endpoint)
    {
        var now = UtcNow();
        lock (Gate)
        {
            if (_pause is { } pause && now < pause.RetryUtc) return pause;
            return Endpoints.TryGetValue(endpoint, out var failed) && now < failed.RetryUtc ? failed : null;
        }
    }

    /// <summary>A poll of this endpoint was answered.</summary>
    internal static void Succeeded(string endpoint)
    {
        var now = UtcNow();
        lock (Gate)
        {
            _lastAnswer = now;
            Endpoints.Remove(endpoint);
            // GitHub answers again, so a pause that has ended is over. One that still runs came from a later answer.
            if (_pause is { } pause && now >= pause.RetryUtc) _pause = null;
        }
    }

    /// <summary>A poll of this endpoint failed.</summary>
    /// <param name="status">The HTTP status, or 0 when gh gave no HTTP answer.</param>
    /// <param name="sentIn">
    /// <see cref="Generation"/> when the poll was sent. A failure of a poll that was sent before <see cref="Forget"/> is
    /// not stored: the user connected GitHub or asked for a refresh meanwhile, and the next polls must be sent.
    /// </param>
    internal static void Failed(string endpoint, int status, LimitHeaders headers, CommandResult result, int? sentIn = null)
    {
        var now = UtcNow();
        lock (Gate)
        {
            if (sentIn is { } generation && generation != _generation) return;
            // An answer to another poll while this one waited for gh: GitHub can be reached.
            var othersAnswered = _lastAnswer is { } last && last <= now && now - last <= GitHubCli.DefaultTimeout;
            if (status > 0) _lastAnswer = now;
            var (problem, scope) = Describe(status, headers, result, now, othersAnswered);
            if (scope != FailureScope.Endpoint)
            {
                _pause = problem;
                _pauseIsSignIn = scope == FailureScope.SignIn;
                return;
            }
            if (!Endpoints.ContainsKey(endpoint)) MakeRoom(now);
            Endpoints[endpoint] = problem;
        }
    }

    /// <summary>What a poll says when GitHub's answer is not what was asked for.</summary>
    internal const string UnreadableAnswer = "GitHub's answer could not be read.";

    /// <summary>
    /// A poll of this endpoint was answered, but the answer could not be read (it is not the JSON that was asked
    /// for). The endpoint is left alone for a while, as after a failure: asking again at once would most likely get
    /// the same answer.
    /// </summary>
    /// <param name="sentIn">
    /// <see cref="Generation"/>, taken before the poll was sent. As in <see cref="Failed"/>, an answer to a poll that
    /// was sent before <see cref="Forget"/> is not stored: the next polls after a reset must be sent.
    /// </param>
    internal static void Unreadable(string endpoint, int? sentIn = null)
    {
        var now = UtcNow();
        lock (Gate)
        {
            if (sentIn is { } generation && generation != _generation) return;
            if (!Endpoints.ContainsKey(endpoint)) MakeRoom(now);
            Endpoints[endpoint] = new GitHubProblem(UnreadableAnswer, now + EndpointPause, RateLimited: false, Detail: string.Empty);
        }
    }

    /// <summary>
    /// Keeps the memory within its size. The failures that nothing reads any more go first, then the ones that are
    /// asked again soonest. Dropping all of them would start gh again for every endpoint that still fails.
    /// </summary>
    private static void MakeRoom(DateTime now)
    {
        if (Endpoints.Count < MaxEndpoints) return;
        foreach (var old in Endpoints.Where(e => !IsCurrent(e.Value, now)).Select(e => e.Key).ToList()) Endpoints.Remove(old);
        while (Endpoints.Count >= MaxEndpoints) Endpoints.Remove(Endpoints.MinBy(e => e.Value.RetryUtc).Key);
    }

    /// <summary>A sign-in check passed, so polls that were paused for the sign-in may run again.</summary>
    internal static void SignInWorks()
    {
        lock (Gate)
            if (_pauseIsSignIn) (_pause, _pauseIsSignIn) = (null, false);
    }

    /// <summary>Forgets every failure, so the next polls ask GitHub again, also the failures of polls that are still waiting for an answer.</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            _generation++;
            Endpoints.Clear();
            (_pause, _pauseIsSignIn) = (null, false);
            _lastAnswer = null;
        }
    }

    /// <summary>
    /// The latest failure for a scope ("repos/owner/repo" or "orgs/name"), or for anything when the scope is null:
    /// a pause of every poll first, otherwise the failed endpoint under that scope that is asked again last.
    /// </summary>
    internal static GitHubProblem? Current(string? scope)
    {
        var now = UtcNow();
        lock (Gate)
        {
            if (CurrentPause(now) is { } pause) return pause;
            GitHubProblem? latest = null;
            foreach (var (endpoint, problem) in Endpoints)
                if (IsCurrent(problem, now) && (scope == null || IsUnder(endpoint, scope))
                    && (latest == null || problem.RetryUtc > latest.RetryUtc)) latest = problem;
            return latest;
        }
    }

    /// <summary>The failure that pauses every poll, or null when there is none.</summary>
    internal static GitHubProblem? CurrentPause()
    {
        var now = UtcNow();
        lock (Gate) return CurrentPause(now);
    }

    private static GitHubProblem? CurrentPause(DateTime now) => _pause is { } pause && IsCurrent(pause, now) ? pause : null;

    private static bool IsCurrent(GitHubProblem problem, DateTime now) => now < problem.RetryUtc + Memory;

    private static bool IsUnder(string endpoint, string scope)
        => endpoint.StartsWith(scope, StringComparison.OrdinalIgnoreCase)
           && (endpoint.Length == scope.Length || endpoint[scope.Length] is '/' or '?');

    internal enum FailureScope
    {
        /// <summary>Only this endpoint: the repository is gone, the sign-in may not read it, GitHub has an error.</summary>
        Endpoint,
        /// <summary>Every request fails the same way: the rate limit, or no connection.</summary>
        Everything,
        /// <summary>Every request fails until the user connects GitHub again.</summary>
        SignIn
    }

    /// <summary>What a failed poll means: the problem to show, and whether it holds for this endpoint or for all.</summary>
    /// <param name="othersAnswered">GitHub answered another poll while this one waited for gh.</param>
    internal static (GitHubProblem Problem, FailureScope Scope) Describe(
        int status, LimitHeaders headers, CommandResult result, DateTime nowUtc, bool othersAnswered = false)
    {
        var detail = FirstLine(result.Error);
        GitHubProblem Problem(string reason, TimeSpan pause, bool rateLimited = false)
            => new(reason, nowUtc + pause, rateLimited, detail);

        if (status == 0)
        {
            // gh printed an answer without a status line: not what gh api -i does, so only this request is doubted.
            if (result.ExitCode == 0) return (Problem(UnreadableAnswer, EndpointPause), FailureScope.Endpoint);
            if (result.Error.StartsWith(GhMissingMessage, StringComparison.Ordinal)) return (Problem(GhMissingMessage, EndpointPause), FailureScope.Everything);
            if (result.Error.StartsWith(TimedOutStart, StringComparison.Ordinal))
                // One request that hangs while others are answered is that request's problem. When nothing was
                // answered meanwhile, GitHub is out of reach, and the other polls would only wait for gh as well.
                return othersAnswered
                    ? (Problem("GitHub did not answer in time.", EndpointPause), FailureScope.Endpoint)
                    : (Problem("GitHub did not answer in time. Check the internet connection.", ConnectionPause), FailureScope.Everything);
            // gh exits with 4 and names its login command when it has no sign-in to use.
            if (result.ExitCode == 4 || result.Error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase))
                return (Problem("GitHub is not connected.", EndpointPause), FailureScope.SignIn);
            return (Problem("GitHub could not be reached. Check the internet connection.", ConnectionPause), FailureScope.Everything);
        }

        var limited = status is 403 or 429
                      && (headers.Remaining == "0" || headers.RetryAfter != null
                          || result.Error.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
        if (limited)
            return (Problem("GitHub's request limit is reached.", RateLimitPause(headers, nowUtc), rateLimited: true), FailureScope.Everything);

        return status switch
        {
            401 => (Problem("GitHub did not accept the sign-in. Connect GitHub again.", EndpointPause), FailureScope.SignIn),
            403 => (Problem("The GitHub sign-in is not allowed to read this.", EndpointPause), FailureScope.Endpoint),
            404 => (Problem("GitHub did not find this, or the sign-in has no access to it.", EndpointPause), FailureScope.Endpoint),
            >= 500 => (Problem($"GitHub has a problem at the moment (HTTP {status}).", EndpointPause), FailureScope.Endpoint),
            _ => (Problem($"GitHub answered with an error (HTTP {status}).", EndpointPause), FailureScope.Endpoint)
        };
    }

    /// <summary>
    /// How long to wait for the rate limit: what retry-after says, else until the reset time, else a minute (what
    /// GitHub asks for when it says neither). Two seconds are added, so the first request is not one that is refused.
    /// </summary>
    private static TimeSpan RateLimitPause(LimitHeaders headers, DateTime nowUtc)
    {
        var wait = TimeSpan.FromMinutes(1);
        if (int.TryParse(headers.RetryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        else if (long.TryParse(headers.Reset, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch < 100_000_000_000)
            // The reset time is on GitHub's clock, so it is compared with the time GitHub put on the same answer.
            // This PC's clock may be minutes off, and the wait would be off by as much.
            wait = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime - (AnswerTime(headers.Date) ?? nowUtc);
        wait += TimeSpan.FromSeconds(2);
        return wait < EndpointPause ? EndpointPause : wait > LongestPause ? LongestPause : wait;
    }

    /// <summary>The date header of an answer ("Tue, 10 Nov 2026 00:39:33 GMT"), or null when it is missing or not a date.</summary>
    private static DateTime? AnswerTime(string? date)
        => DateTimeOffset.TryParseExact(date, "r", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.UtcDateTime : null;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= 300 ? line : line[..300];
    }
}
