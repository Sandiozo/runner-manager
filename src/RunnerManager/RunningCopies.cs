namespace RunnerManager;

/// <summary>What a running Runner Manager process does, told from its command line the way <see cref="App"/> starts it.</summary>
public enum CopyRole { Dashboard, Assistant, Agent, Other }

internal static class RunningCopies
{
    /// <summary>
    /// The role of a Runner Manager process with this command line. An unknown command line (for example of an elevated
    /// process, read without administrator rights) counts as a dashboard. <paramref name="programPath"/>, the process's
    /// exe when known, finds where an unquoted program path with spaces ends.
    /// </summary>
    public static CopyRole RoleOf(string? commandLine, string? programPath = null)
    {
        var args = ArgumentsAfterProgram(commandLine ?? string.Empty, programPath).ToArray();
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

    /// <summary>
    /// The arguments after the program in a command line. A program path without quotes may contain spaces
    /// ("C:\Program Files\RunnerManager\RunnerManager.exe --mcp"): it ends at <paramref name="programPath"/> when the
    /// line starts with it, or else at the first piece that ends in ".exe".
    /// </summary>
    internal static IEnumerable<string> ArgumentsAfterProgram(string commandLine, string? programPath)
    {
        var line = commandLine.TrimStart();
        if (!line.StartsWith('"'))
        {
            if (!string.IsNullOrEmpty(programPath) && line.StartsWith(programPath, StringComparison.OrdinalIgnoreCase)
                && (line.Length == programPath.Length || char.IsWhiteSpace(line[programPath.Length])))
                return Arguments(line[programPath.Length..]);
            var pieces = Arguments(line).ToList();
            return pieces.Skip(ProgramPieces(pieces));
        }
        return Arguments(line).Skip(1);
    }

    /// <summary>
    /// How many pieces of an unquoted command line make up the program path: up to the first that ends in ".exe", when the
    /// first piece looks like a path and no argument (a flag or "ai") comes before it. Otherwise just the first piece.
    /// </summary>
    private static int ProgramPieces(List<string> pieces)
    {
        if (pieces.Count == 0 || !pieces[0].Contains('\\') && !pieces[0].Contains(':')) return 1;
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return i + 1;
            if (i > 0 && (pieces[i].StartsWith('-') || pieces[i].Equals(AssistantHost.CliArgument, StringComparison.OrdinalIgnoreCase))) break;
        }
        return 1;
    }

    /// <summary>
    /// The arguments a copy was started with. Windows splits a program path without quotes at its spaces, so a copy
    /// started as C:\Program Files\RunnerManager\RunnerManager.exe --mcp gets "Files\RunnerManager\RunnerManager.exe"
    /// as its first argument; those pieces of its own path are left out.
    /// </summary>
    internal static string[] WithoutProgramPathPieces(string[] args, string commandLine, string? processPath)
    {
        var line = commandLine.TrimStart();
        if (string.IsNullOrEmpty(processPath) || line.StartsWith('"') || !line.StartsWith(processPath, StringComparison.OrdinalIgnoreCase)
            || line.Length > processPath.Length && !char.IsWhiteSpace(line[processPath.Length]))
            return args;
        var pieces = processPath.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 2 || args.Length < pieces.Length - 1) return args;
        for (var i = 1; i < pieces.Length; i++)
            if (!args[i - 1].Equals(pieces[i], StringComparison.OrdinalIgnoreCase)) return args;
        return args[(pieces.Length - 1)..];
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
