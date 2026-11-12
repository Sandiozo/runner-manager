namespace RunnerManager;

/// <summary>What the dashboard says when GitHub could not be read for what is on screen.</summary>
internal static class GitHubNotes
{
    /// <summary>
    /// <paramref name="lead"/>, then why GitHub could not be read and when it is asked again. A failure stays readable
    /// for a while after its pause ended, so the time is left out once it has passed. Without a known reason, only that
    /// it is tried again.
    /// </summary>
    internal static string Describe(string lead, GitHubProblem? problem, DateTime nowUtc)
    {
        if (problem == null) return $"{lead} Trying again.";
        var retry = problem.RetryUtc > nowUtc
            ? $"Trying again at {TimeFormat.WithSeconds(problem.RetryUtc.ToLocalTime())}."
            : "Trying again.";
        return $"{lead} {problem.Reason.Trim()} {retry}";
    }

    /// <summary>The lead of the note in a runner's details.</summary>
    internal const string RunnerLead = "The jobs and runs shown may be out of date.";

    /// <summary>The lead of the status bar while every request to GitHub waits.</summary>
    internal const string PauseLead = "Jobs, queues and recent runs may be out of date.";

    /// <summary>The lead of the job panel when the job could not be read.</summary>
    internal const string JobLead = "Could not read the job from GitHub.";
}
