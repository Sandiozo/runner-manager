using System.Text.RegularExpressions;

namespace RunnerManager;

public static class WorkflowManager
{
    private static readonly Regex RunsOnRegex = new(
        @"^(?<indent>\s*)runs-on\s*:\s*(?<value>[^#\r\n]+?)(?<comment>\s+#.*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task CheckAndUpdateAsync(
        string repositoryPath, IEnumerable<string> customLabels, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new InvalidOperationException("The runner configuration does not include a GitHub repository or organization.");

        var files = await GitHubApi.GetWorkflowFilesAsync(repositoryPath);
        if (files.Count == 0)
        {
            log("No workflow YAML files were found.");
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

        if (changed == 0 && warnings == 0)
            log("Workflow check complete. No changes are needed.");
        else
            log($"Workflow check complete. {changed} file(s) updated. {warnings} item(s) need review.");
    }

    internal static WorkflowTransformResult Transform(
        string content, string selector, IEnumerable<string> customLabels)
    {
        var warnings = new List<string>();
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "self-hosted", "Windows", "X64"
        };
        foreach (var label in customLabels.Where(x => !string.IsNullOrWhiteSpace(x)))
            available.Add(label.Trim());

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
                if (required.Any(x => x.Equals("self-hosted", StringComparison.OrdinalIgnoreCase))
                    && required.All(available.Contains))
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
        var labels = new List<string> { "self-hosted", "Windows", "X64" };
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

public sealed class WorkflowTransformResult
{
    public bool Changed { get; init; }
    public string Content { get; init; } = string.Empty;
    public List<string> Warnings { get; init; } = new();
}
