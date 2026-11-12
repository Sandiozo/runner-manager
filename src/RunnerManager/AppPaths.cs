using System.IO;

namespace RunnerManager;

/// <summary>
/// Where Runner Manager keeps its files. Versions before 2.2 were called RunnerWidget; their names are kept here so
/// existing installs can be moved over (see <see cref="LegacyInstall"/>).
/// </summary>
public static class AppPaths
{
    public const string ExeName = "RunnerManager.exe";
    public const string LegacyExeName = "RunnerWidget.exe";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string LegacyStateDir => Path.Combine(LocalAppData, "RunnerWidget");

    /// <summary>The environment variable that moves the data folder of a process without administrator rights (development).</summary>
    internal const string StateDirVariable = "RUNNER_MANAGER_STATE_DIR";

    /// <summary>
    /// Settings, logs and state: %LOCALAPPDATA%\RunnerManager. <see cref="StateDirVariable"/> overrides it only for a
    /// process without administrator rights (see <see cref="StateDirOverride"/>). Read on first use.
    /// </summary>
    public static string StateDir => _stateDir ??= PrepareStateDir();

    private static string? _stateDir;

    /// <summary>
    /// The data folder that the tests use, set by the test assembly before anything reads <see cref="StateDir"/>. Code,
    /// not the environment: GitHub's runners run the tests with administrator rights, where the variable does not count.
    /// Runner Manager itself never sets it.
    /// </summary>
    internal static string? TestStateDir { get; set; }

    /// <summary>
    /// The folder that replaces %LOCALAPPDATA%\RunnerManager, or null for none: the tests' folder, otherwise the value of
    /// <see cref="StateDirVariable"/> in a process without administrator rights. Any program of the user can set that
    /// variable for the user, and the elevated parts (the fleet agent, the dashboard) would then read their settings and
    /// write their logs where it says, so a process with administrator rights ignores it.
    /// </summary>
    internal static string? StateDirOverride(string? variable, bool elevated, string? testStateDir)
        => !string.IsNullOrWhiteSpace(testStateDir) ? testStateDir
            : !elevated && !string.IsNullOrWhiteSpace(variable) ? variable
            : null;

    private static string PrepareStateDir()
    {
        if (StateDirOverride(Environment.GetEnvironmentVariable(StateDirVariable), Elevation.IsElevated, TestStateDir) is { } overridden)
            return overridden;

        var dir = Path.Combine(LocalAppData, "RunnerManager");
        if (Directory.Exists(dir) || !Directory.Exists(LegacyStateDir)) return dir;
        try
        {
            Directory.Move(LegacyStateDir, dir);
        }
        catch
        {
            // An older fleet agent may still hold its log open. Copy what can be read into a temporary folder and move
            // that into place in one step, so a process starting at the same time never sees a half-copied folder.
            var staging = $"{dir}.migrating-{Environment.ProcessId}";
            try
            {
                foreach (var file in Directory.EnumerateFiles(LegacyStateDir, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(staging, Path.GetRelativePath(LegacyStateDir, file));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target);
                    }
                    catch { }
                }
                Directory.CreateDirectory(staging);
                Directory.Move(staging, dir);
            }
            catch
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }
        return dir;
    }
}
