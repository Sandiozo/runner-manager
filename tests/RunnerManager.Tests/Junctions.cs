using System.Diagnostics;
using System.IO;

namespace RunnerManager.Tests;

/// <summary>Junctions (mklink /J) in %TEMP%, for tests that check that nothing is deleted or copied through a link.</summary>
internal static class Junctions
{
    /// <summary>Why this test process cannot create a junction in %TEMP%, or null when it can. Tried once.</summary>
    internal static string? Problem => LazyProblem.Value;

    private static readonly Lazy<string?> LazyProblem = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-junction-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "target"));
            var link = Path.Combine(root, "link");
            var output = Run(link, Path.Combine(root, "target"));
            return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? null
                : $"This test needs a junction, and this test process could not create one in {Path.GetTempPath()} with mklink /J: {output.Trim()}";
        }
        catch (Exception ex)
        {
            return $"This test needs a junction, and this test process could not create one in {Path.GetTempPath()}: {ex.Message}";
        }
        finally
        {
            try
            {
                var link = Path.Combine(root, "link");
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
        }
    });

    /// <summary>Creates a junction at <paramref name="link"/> to <paramref name="target"/>.</summary>
    internal static void Create(string link, string target)
    {
        var output = Run(link, target);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"No junction at {link}: {output}");
    }

    /// <summary>Deletes the junctions (only the links) that exist among <paramref name="links"/>, before a test removes its folders.</summary>
    internal static void Delete(params string[] links)
    {
        foreach (var link in links)
            if (Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(link);
    }

    private static string Run(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "mklink did not finish within 30 seconds.");
        return output + error.Result;
    }
}

/// <summary>A test that needs a junction: skipped, with the reason, when this test process cannot create one.</summary>
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (Junctions.Problem is { } problem) Skip = problem;
    }
}
