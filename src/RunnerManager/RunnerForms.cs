using System.IO;

namespace RunnerManager;

/// <summary>The Add runner form: how the fields are read and whether each one can be used, before anything is downloaded.</summary>
internal static class AddRunnerForm
{
    internal const string RepositoryHint = "Enter a repository URL or owner/repo. For an organization runner, enter the organization URL.";
    /// <summary>
    /// Runner Manager's limit for a name typed in Add runner and Edit runner. It is not a documented GitHub limit, so a
    /// runner that already has a longer name keeps it: Edit runner checks the length only of a new name.
    /// </summary>
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
    internal static string DefaultRunnerName(string? machineName, RepositoryCheck repository, Func<string, bool> taken)
    {
        machineName ??= string.Empty;
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

    /// <summary>Null when the name has only allowed characters and is not longer than <see cref="MaxNameLength"/>.</summary>
    internal static string? NameRuleProblem(string? name) => RunnerService.RunnerNameProblem(name) ?? LengthProblem(name);

    /// <summary>Null when the name is not longer than <see cref="MaxNameLength"/>.</summary>
    internal static string? LengthProblem(string? name)
        => (name ?? string.Empty).Trim().Length > MaxNameLength ? $"The name is too long. Use at most {MaxNameLength} characters." : null;

    /// <summary>
    /// Null when the name can be registered for this repository or organization. A runner that another PC reports
    /// without a scope or a name (null, whatever the type says) takes no name.
    /// </summary>
    internal static string? NameProblem(string? name, string? scope, IEnumerable<(string Scope, string Name)>? existing)
    {
        if (NameRuleProblem(name) is { } problem) return problem;
        var trimmed = name!.Trim();
        return !string.IsNullOrEmpty(scope) && (existing ?? []).Any(r => string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase)
                                                                         && string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ? "A runner with this name is already registered for this repository. Choose another name."
            : null;
    }

    internal static string[] ParseLabels(string? text)
        => (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
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

    /// <summary>
    /// Null when the folder can be sent to another PC: empty (that PC's folder for new runners) or a full path that is
    /// not a network folder. Whether it exists or has files is checked on that PC, and so are its mapped drives.
    /// </summary>
    internal static string? RemoteFolderProblem(string path, string pcName)
    {
        var folder = path.Trim();
        if (folder.Length == 0) return null;
        if (!Path.IsPathFullyQualified(folder)) return @"Choose a full folder path, such as D:\Runners, or leave it empty.";
        if (folder.IndexOfAny(new[] { '"', '%', '!' }) >= 0 || folder.Any(char.IsControl)) return "The runner folder path cannot contain the characters \", % or !.";
        return RunnerService.NetworkFolderProblem(folder, _ => false) != null
            ? $"Choose a folder on a drive in {pcName}. Runners run as a Windows service, and services cannot use network folders."
            : null;
    }

    /// <summary>
    /// The PCs a runner can be added on: this PC, and online PCs whose fleet agent can create one (the same PCs a
    /// runner can be moved to). This PC comes first.
    /// </summary>
    internal static List<MachineInfo> TargetPcs(IEnumerable<MachineInfo?>? pcs)
        => (pcs ?? []).OfType<MachineInfo>().Where(m => m.MachineName != null && m.SupportsFleetTools)
            .GroupBy(m => m.MachineKey).Select(g => g.First())
            .OrderByDescending(m => m.IsLocal).ThenBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// What to say when the PC selected on the dashboard cannot be chosen: it is offline, or its fleet agent cannot
    /// create runners. Null when it can be chosen, or is this PC. Without a picker (only this PC can be chosen), it also
    /// says where the runner goes.
    /// </summary>
    internal static string? MissingPcHint(MachineInfo? selectedPc, IReadOnlyCollection<MachineInfo> targets)
    {
        if (selectedPc is not { MachineName: not null, IsLocal: false } || targets.Any(m => m.MachineKey == selectedPc.MachineKey)) return null;
        const string reason = "it is offline, or its Runner Manager is too old to add runners from here.";
        if (targets.Count > 1) return $"{selectedPc.MachineName} is not in the list: {reason}";
        var addedOn = targets.FirstOrDefault() is { IsLocal: false } only ? only.MachineName : "this PC";
        return $"{selectedPc.MachineName} cannot be chosen: {reason} The runner is added on {addedOn}.";
    }

    /// <summary>
    /// Shown in the Add runner window while this PC is chosen and is not connected to GitHub: a runner on this PC is
    /// registered with this PC's GitHub sign-in, one on another PC with that PC's.
    /// </summary>
    internal const string ThisPcNeedsGitHub =
        "This PC is not connected to GitHub, so a runner cannot be added on it. Connect GitHub first, or choose another PC.";

    /// <summary>The PC as the picker shows it, and as screen readers read it.</summary>
    internal static (string Label, string AccessibleName) PcLabel(MachineInfo pc)
        => pc.IsLocal ? ($"{pc.MachineName} (this PC)", $"{pc.MachineName}, this PC") : (pc.MachineName, pc.MachineName);

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
    /// <summary>
    /// The custom labels the runner has: those its status reports, or else those read from GitHub for a runner on this
    /// PC. Another PC can send null for the list or for an entry.
    /// </summary>
    internal static string[] CurrentLabels(IEnumerable<string?>? reported, IEnumerable<string?>? local)
        => Labels(reported) is { Length: > 0 } labels ? labels : Labels(local);

    private static string[] Labels(IEnumerable<string?>? labels)
        => (labels ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <param name="currentName">The runner's name; another PC can report none.</param>
    /// <param name="currentLabels">The runner's custom labels; another PC can report none, or a list with empty entries.</param>
    /// <param name="noDefaultLabels">
    /// The runner was set up without the default labels, so it cannot lose every custom label: it would have no label
    /// at all (<see cref="RunnerService.NoLabelsLeftMessage"/>). RunnerService checks this again before it changes anything.
    /// </param>
    internal static Result Check(string? currentName, IEnumerable<string?>? currentLabels, string? name, string? labelsText, bool canClearLabels,
        bool noDefaultLabels = false)
    {
        var newName = (name ?? string.Empty).Trim();
        var labels = AddRunnerForm.ParseLabels(labelsText);
        var current = Labels(currentLabels);
        var clears = labels.Length == 0 && current.Length > 0;
        var labelsChanged = !new HashSet<string>(labels, StringComparer.Ordinal).SetEquals(current);
        var nameChanged = !newName.Equals((currentName ?? string.Empty).Trim(), StringComparison.Ordinal);
        var changed = nameChanged || labelsChanged;

        // The name a runner has stays allowed, whatever its length: only a new name must keep to the limit.
        var nameProblem = RunnerService.RunnerNameProblem(newName) ?? (nameChanged ? AddRunnerForm.LengthProblem(newName) : null);
        var labelsProblem = RunnerService.LabelsProblem(labels) ?? (clears && !canClearLabels ? ClearNotPossible : clears && noDefaultLabels ? RunnerService.NoLabelsLeftMessage : null);
        var request = changed && nameProblem == null && labelsProblem == null
            ? new RunnerEditRequest(newName, labels, ClearLabels: clears)
            : null;
        return new Result(changed, nameProblem, labelsProblem, request);
    }
}
