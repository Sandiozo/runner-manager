using System.Text.RegularExpressions;

namespace RunnerManager;

public static class WorkflowManager
{
    private static readonly Regex RunsOnRegex = new(
        // [ \t] instead of \s, and (?=\r?$) at the end: in files with CRLF line endings a \r comes before each line end.
        @"^(?<indent>[ \t]*)runs-on[ \t]*:[ \t]*(?<value>[^#\r\n]+?)(?<comment>[ \t]+#[^\r\n]*)?(?=\r?$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task CheckAndUpdateAsync(
        string repositoryPath, IEnumerable<string> customLabels, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var list = await GitHubApi.GetWorkflowFilesAsync(repositoryPath);
        foreach (var path in list.Skipped)
            log($"{path}: the file could not be read and was skipped.");
        var files = list.Files;
        if (files.Count == 0)
        {
            log(list.Skipped.Count == 0 ? "No workflow YAML files were found." : "No workflow YAML files could be read.");
            return;
        }

        var selector = BuildSelector(customLabels);
        var changed = 0;
        var warnings = 0;
        foreach (var file in files)
        {
            var result = Transform(file.Content, selector, customLabels);
            warnings += result.Warnings.Count;
            foreach (var warning in result.Warnings)
                log($"{file.Path}: {warning}");

            if (!result.Changed) continue;
            log($"Updating {file.Path} to use the self-hosted Windows runner…");
            await GitHubApi.UpdateRepositoryFileAsync(
                repositoryPath, file, result.Content, "Configure workflow for self-hosted Windows runner");
            changed++;
        }

        var skipped = list.Skipped.Count == 0 ? string.Empty : $" {list.Skipped.Count} file(s) were skipped.";
        if (changed == 0 && warnings == 0)
            log($"Workflow check complete. No changes are needed.{skipped}");
        else
            log($"Workflow check complete. {changed} file(s) updated. {warnings} item(s) need review.{skipped}");
    }

    internal static WorkflowTransformResult Transform(
        string content, string selector, IEnumerable<string> customLabels)
    {
        var warnings = new List<string>();
        var available = RunnerLabels.Available(customLabels);

        var changed = false;
        var updated = RunsOnRegex.Replace(content, match =>
        {
            var value = match.Groups["value"].Value.Trim();
            var plain = Unquote(value);
            if (IsHostedWindows(plain))
            {
                changed = true;
                return $"{match.Groups["indent"].Value}runs-on: {selector}{match.Groups["comment"].Value}";
            }

            if (plain.Equals("self-hosted", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var required = ParseInlineLabels(value);
                if (RunnerLabels.CanRun(required, available))
                    return match.Value;

                if (required.Any(x => x.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)))
                    warnings.Add($"left self-hosted selector {value} unchanged because its labels do not match this runner");
                return match.Value;
            }
            if (plain.Contains("${{", StringComparison.Ordinal)
                || plain.StartsWith("matrix.", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"left dynamic runs-on expression {value} unchanged");

            return match.Value;
        });

        return new WorkflowTransformResult
        {
            Changed = changed,
            Content = updated,
            Warnings = warnings
        };
    }

    private static bool IsHostedWindows(string value)
    {
        if (value.Equals("windows-latest", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(value, @"^windows-(2019|2022|2025)$", RegexOptions.IgnoreCase);
    }

    private static string[] ParseInlineLabels(string value)
        => value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    internal static string BuildSelector(IEnumerable<string> customLabels)
    {
        var labels = new List<string>(RunnerLabels.Defaults);
        labels.AddRange(customLabels.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct());
        return "[" + string.Join(", ", labels.Select(FormatYamlLabel)) + "]";
    }

    private static string FormatYamlLabel(string value)
    {
        if (Regex.IsMatch(value, @"^[A-Za-z0-9_.-]+$")) return value;
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[^1] == '"')
            || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            return trimmed[1..^1];
        return trimmed;
    }
}

/// <summary>The labels every runner registered by Runner Manager has, and how GitHub matches jobs to runners.</summary>
internal static class RunnerLabels
{
    internal static readonly string[] Defaults = { "self-hosted", "Windows", "X64" };

    /// <summary>The default labels plus the runner's custom labels.</summary>
    internal static HashSet<string> Available(IEnumerable<string> customLabels)
    {
        var labels = new HashSet<string>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var label in customLabels.Where(x => !string.IsNullOrWhiteSpace(x)))
            labels.Add(label.Trim());
        return labels;
    }

    /// <summary>A job can run on a runner when it asks for a self-hosted runner and the runner has every label it needs.</summary>
    internal static bool CanRun(IEnumerable<string> required, ISet<string> available)
    {
        var labels = required.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return labels.Any(x => x.Equals("self-hosted", StringComparison.OrdinalIgnoreCase))
               && labels.All(available.Contains);
    }
}

public sealed class WorkflowTransformResult
{
    public bool Changed { get; init; }
    public string Content { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}
