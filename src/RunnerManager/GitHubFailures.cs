using System.Globalization;

namespace RunnerManager;

/// <summary>
/// Why GitHub could not be read, for the UI. <paramref name="Reason"/> is a full sentence in the app's words and
/// <paramref name="Detail"/> the first line of what gh said (may be empty). Until <paramref name="RetryUtc"/> the
/// request is not sent again.
/// </summary>
public sealed record GitHubProblem(string Reason, DateTime RetryUtc, bool RateLimited, string Detail);

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
    private const int MaxEndpoints = 500;

    internal const string GhMissingMessage = "GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and restart Runner Manager.";
    /// <summary>How the message of a gh call that did not finish in time starts.</summary>
    internal const string TimedOutStart = "GitHub CLI timed out";

    /// <summary>The time, which tests set.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, GitHubProblem> Endpoints = new(StringComparer.OrdinalIgnoreCase);
    private static GitHubProblem? _pause;
    /// <summary>The pause is about the sign-in, so it ends when a sign-in check passes.</summary>
    private static bool _pauseIsSignIn;

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
            Endpoints.Remove(endpoint);
            // GitHub answers again, so a pause that has ended is over. One that still runs came from a later answer.
            if (_pause is { } pause && now >= pause.RetryUtc) _pause = null;
        }
    }

    /// <summary>A poll of this endpoint failed.</summary>
    /// <param name="status">The HTTP status, or 0 when gh gave no HTTP answer.</param>
    /// <param name="remaining">The x-ratelimit-remaining header, when there was one.</param>
    /// <param name="reset">The x-ratelimit-reset header (seconds since 1970).</param>
    /// <param name="retryAfter">The retry-after header (seconds).</param>
    internal static void Failed(string endpoint, int status, string? remaining, string? reset, string? retryAfter, CommandResult result)
    {
        var now = UtcNow();
        var (problem, scope) = Describe(status, remaining, reset, retryAfter, result, now);
        lock (Gate)
        {
            if (scope != FailureScope.Endpoint)
            {
                _pause = problem;
                _pauseIsSignIn = scope == FailureScope.SignIn;
                return;
            }
            if (Endpoints.Count >= MaxEndpoints)
                foreach (var old in Endpoints.Where(e => !IsCurrent(e.Value, now)).Select(e => e.Key).ToList()) Endpoints.Remove(old);
            if (Endpoints.Count >= MaxEndpoints) Endpoints.Clear();
            Endpoints[endpoint] = problem;
        }
    }

    /// <summary>A sign-in check passed, so polls that were paused for the sign-in may run again.</summary>
    internal static void SignInWorks()
    {
        lock (Gate)
            if (_pauseIsSignIn) (_pause, _pauseIsSignIn) = (null, false);
    }

    /// <summary>Forgets every failure, so the next polls ask GitHub again.</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            Endpoints.Clear();
            (_pause, _pauseIsSignIn) = (null, false);
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
    internal static (GitHubProblem Problem, FailureScope Scope) Describe(
        int status, string? remaining, string? reset, string? retryAfter, CommandResult result, DateTime nowUtc)
    {
        var detail = FirstLine(result.Error);
        GitHubProblem Problem(string reason, TimeSpan pause, bool rateLimited = false)
            => new(reason, nowUtc + pause, rateLimited, detail);

        if (status == 0)
        {
            // gh printed an answer without a status line: not what gh api -i does, so only this request is doubted.
            if (result.ExitCode == 0) return (Problem("GitHub's answer could not be read.", EndpointPause), FailureScope.Endpoint);
            if (result.Error.StartsWith(GhMissingMessage, StringComparison.Ordinal)) return (Problem(GhMissingMessage, EndpointPause), FailureScope.Everything);
            if (result.Error.StartsWith(TimedOutStart, StringComparison.Ordinal))
                return (Problem("GitHub did not answer in time. Check the internet connection.", ConnectionPause), FailureScope.Everything);
            // gh exits with 4 and names its login command when it has no sign-in to use.
            if (result.ExitCode == 4 || result.Error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase))
                return (Problem("GitHub is not connected.", EndpointPause), FailureScope.SignIn);
            return (Problem("GitHub could not be reached. Check the internet connection.", ConnectionPause), FailureScope.Everything);
        }

        var limited = status is 403 or 429
                      && (remaining == "0" || retryAfter != null || result.Error.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
        if (limited)
            return (Problem("GitHub's request limit is reached.", RateLimitPause(reset, retryAfter, nowUtc), rateLimited: true), FailureScope.Everything);

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
    private static TimeSpan RateLimitPause(string? reset, string? retryAfter, DateTime nowUtc)
    {
        var wait = TimeSpan.FromMinutes(1);
        if (int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        else if (long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && epoch < 100_000_000_000)
            wait = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime - nowUtc;
        wait += TimeSpan.FromSeconds(2);
        return wait < EndpointPause ? EndpointPause : wait > LongestPause ? LongestPause : wait;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length <= 300 ? line : line[..300];
    }
}
