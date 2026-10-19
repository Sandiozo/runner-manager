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

    /// <summary>
    /// Settings, logs and state: %LOCALAPPDATA%\RunnerManager. RUNNER_MANAGER_STATE_DIR overrides it (used by tests).
    /// </summary>
    public static readonly string StateDir = PrepareStateDir();

    private static string PrepareStateDir()
    {
        var overridden = Environment.GetEnvironmentVariable("RUNNER_MANAGER_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

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
