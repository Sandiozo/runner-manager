namespace RunnerManager;

/// <summary>What a running Runner Manager process does, told from its command line the way <see cref="App"/> starts it.</summary>
public enum CopyRole { Dashboard, Assistant, Agent, Other }

internal static class RunningCopies
{
    /// <summary>
    /// The role of a Runner Manager process with this command line. An unknown command line (for example of an elevated
    /// process, read without administrator rights) counts as a dashboard.
    /// </summary>
    public static CopyRole RoleOf(string? commandLine)
    {
        var args = Arguments(commandLine ?? string.Empty).Skip(1).ToArray();
        if (args.Length == 0) return CopyRole.Dashboard;
        // The same order as App.OnStartup: an AI tool's host is recognized by its first argument only.
        if (AssistantHost.IsAssistantLaunch(args)) return CopyRole.Assistant;
        if (args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase))) return CopyRole.Agent;
        if (args.Any(a => a.Equals("--install-agent", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("--setup", StringComparison.OrdinalIgnoreCase)
                          || a.Equals(App.ProbeArgument, StringComparison.OrdinalIgnoreCase)))
            return CopyRole.Other;
        return CopyRole.Dashboard;
    }

    /// <summary>Splits a command line at spaces outside double quotes, which is enough to find whole flags.</summary>
    internal static IEnumerable<string> Arguments(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) yield return current.ToString();
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) yield return current.ToString();
    }
}
