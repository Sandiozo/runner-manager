using System.IO;

namespace RunnerManager;

/// <summary>The Add runner form: how the fields are read and whether each one can be used, before anything is downloaded.</summary>
internal static class AddRunnerForm
{
    internal const string RepositoryHint = "Enter a repository URL or owner/repo. For an organization runner, enter the organization URL.";
    /// <summary>GitHub refuses longer runner names.</summary>
    internal const int MaxNameLength = 64;

    /// <param name="Url">The registration URL to use (https://github.com/owner/repo or https://github.com/org).</param>
    /// <param name="Problem">Null when the URL can be registered.</param>
    internal sealed record RepositoryCheck(string Url, string Scope, string RepositoryPath, string Organization, string? Problem)
    {
        public bool IsValid => Problem == null;
    }

    /// <summary>
    /// The URL for what was typed: also owner/repo (with or without a trailing slash), github.com/owner/repo without a
    /// scheme, and the SSH form git@github.com:owner/repo.git that the Code button on GitHub offers.
    /// </summary>
    internal static string ToUrl(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return value;
        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase) && value.IndexOf(':') is var colon and > 4)
            value = $"https://{value[4..colon]}/{value[(colon + 1)..].TrimStart('/')}";
        if (value.Contains("://")) return value;
        if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase))
            return "https://" + value;
        var path = value.Trim('/');
        return path.Count(c => c == '/') == 1 && !path.Any(char.IsWhiteSpace) && !path.Contains(':')
            ? $"https://github.com/{path}"
            : value;
    }

    internal static RepositoryCheck CheckRepository(string text)
    {
        var url = ToUrl(text);
        var repo = RunnerService.RepositoryPath(url);
        var org = repo.Length > 0 ? string.Empty : RunnerService.OrganizationName(url);
        if (url.Length == 0) return new RepositoryCheck(url, string.Empty, string.Empty, string.Empty, RepositoryHint);
        if (repo.Length == 0 && org.Length == 0)
            return new RepositoryCheck(url, string.Empty, string.Empty, string.Empty, "This is not a GitHub repository or organization URL.");
        var registration = RunnerService.NormalizeRegistrationUrl(url);
        return new RepositoryCheck(registration, RunnerService.ApiScope(registration), repo, org, RunnerService.RegistrationUrlProblem(registration));
    }

    /// <summary>
    /// "PC-repository", so a second runner for a repository does not get the name of the first. A name that is taken
    /// for the same repository or organization gets a number.
    /// </summary>
    internal static string DefaultRunnerName(string machineName, RepositoryCheck repository, Func<string, bool> taken)
    {
        var target = repository.RepositoryPath.Length > 0 ? repository.RepositoryPath.Split('/').Last() : repository.Organization;
        var name = Clean(target.Length > 0 && repository.IsValid ? $"{machineName}-{target}" : machineName);
        if (name.Length == 0) name = "runner";
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd('-', '.');
        var candidate = name;
        for (var i = 2; taken(candidate) && i < 100; i++)
        {
            var suffix = $"-{i}";
            candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)] : name) + suffix;
        }
        return candidate;
    }

    /// <summary>Only the characters config.cmd accepts in a runner name.</summary>
    private static string Clean(string value)
        => new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');

    /// <summary>Null when the name has only allowed characters and is not too long for GitHub.</summary>
    internal static string? NameRuleProblem(string name)
        => RunnerService.RunnerNameProblem(name)
           ?? (name.Trim().Length > MaxNameLength ? $"The name is too long. GitHub allows at most {MaxNameLength} characters." : null);

    /// <summary>Null when the name can be registered for this repository or organization.</summary>
    internal static string? NameProblem(string name, string scope, IEnumerable<(string Scope, string Name)> existing)
    {
        if (NameRuleProblem(name) is { } problem) return problem;
        var trimmed = name.Trim();
        return scope.Length > 0 && existing.Any(r => r.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase)
                                                     && r.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ? "A runner with this name is already registered for this repository. Choose another name."
            : null;
    }

    internal static string[] ParseLabels(string text)
        => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Null when a runner can be installed in the folder: a full local path that is new or empty.</summary>
    internal static string? FolderProblem(string path)
    {
        var folder = path.Trim();
        if (folder.Length == 0) return "Choose the folder for the runner.";
        if (RunnerService.RunnerFolderProblem(folder) is { } problem) return problem;
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                return "This folder already contains files. Choose an empty or new folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"The folder cannot be read: {ex.Message}";
        }
        return null;
    }

    /// <summary>A folder in <paramref name="root"/> named after <paramref name="name"/> that is new or empty.</summary>
    internal static string FreeFolder(string root, string name)
    {
        var safe = RunnerService.SafeFolderName(name);
        var path = Path.Combine(root, safe);
        for (var i = 2; i < 1000 && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); i++)
            path = Path.Combine(root, $"{safe}-{i}");
        return path;
    }
}

/// <summary>The Edit runner form: what changed, and whether Save can register the runner again with it.</summary>
internal static class EditRunnerForm
{
    internal const string ClearNotPossible =
        "The runner keeps its labels when this is empty. To remove every custom label, delete the runner and add it again.";

    internal sealed record Result(bool Changed, string? NameProblem, string? LabelsProblem, RunnerEditRequest? Request)
    {
        public bool CanSave => Request != null;
    }

    /// <param name="canClearLabels">Whether the PC of the runner can remove every custom label (an empty list keeps them otherwise).</param>
    internal static Result Check(string currentName, IReadOnlyCollection<string> currentLabels, string name, string labelsText, bool canClearLabels)
    {
        var newName = name.Trim();
        var labels = AddRunnerForm.ParseLabels(labelsText);
        var current = currentLabels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var clears = labels.Length == 0 && current.Length > 0;
        var labelsChanged = !new HashSet<string>(labels, StringComparer.Ordinal).SetEquals(current);
        var changed = !newName.Equals(currentName.Trim(), StringComparison.Ordinal) || labelsChanged;

        var nameProblem = AddRunnerForm.NameRuleProblem(newName);
        var labelsProblem = RunnerService.LabelsProblem(labels) ?? (clears && !canClearLabels ? ClearNotPossible : null);
        var request = changed && nameProblem == null && labelsProblem == null
            ? new RunnerEditRequest(newName, labels, ClearLabels: clears)
            : null;
        return new Result(changed, nameProblem, labelsProblem, request);
    }
}
