using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RunnerWidget;

/// <summary>
/// Installs Runner Manager from the relay repository's GitHub Releases into
/// %LOCALAPPDATA%\Programs\RunnerManager, so PCs don't need the .NET SDK or a git checkout.
/// </summary>
public static class AppUpdater
{
    public const string AssetName = "RunnerWidget.exe";
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RunnerManager");
    public static string InstalledExe => Path.Combine(InstallDir, AssetName);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static ReleaseInfo? _latest;
    private static DateTime _checkedUtc = DateTime.MinValue;
    private static string _checkedRepository = string.Empty;

    /// <summary>Where releases are published (Settings → Updates).</summary>
    public static string ReleaseRepository => FleetConfig.Current.RelayRepository;

    public static string CurrentVersion
    {
        get
        {
            var informational = typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
            return Normalize(informational);
        }
    }

    public static bool IsRunningInstalledCopy => string.Equals(
        Path.GetFullPath(Environment.ProcessPath ?? string.Empty), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>"v2.1.0", "2.1.0+abc" or "2.1.0-beta" → "2.1.0".</summary>
    public static string Normalize(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut > 0) text = text[..cut];
        return Version.TryParse(text, out var parsed)
            ? new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)).ToString(3)
            : text;
    }

    public static bool IsNewer(string candidate, string current)
        => Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(bool force = false)
    {
        var repository = ReleaseRepository;
        if (!force && _checkedRepository == repository && DateTime.UtcNow - _checkedUtc < CheckInterval) return _latest;
        var result = await GitHubCli.RunAsync($"api repos/{repository}/releases/latest");
        _checkedUtc = DateTime.UtcNow;
        _checkedRepository = repository;
        if (result.ExitCode != 0)
        {
            _latest = null;
            return null;
        }
        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                _latest = new ReleaseInfo
                {
                    Tag = tag,
                    Version = Normalize(tag),
                    HtmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                    Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                    Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty,
                    SizeBytes = asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0
                };
                return _latest;
            }
        }
        catch { }
        _latest = null;
        return null;
    }

    /// <summary>Downloads the release, verifies its checksum and swaps it into the install folder.</summary>
    public static async Task<string> DownloadAndInstallAsync(ReleaseInfo release, Action<string> log)
    {
        var staging = Path.Combine(FleetConfig.StateDir, "update");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        log($"Downloading Runner Manager {release.Tag}...");
        var download = await GitHubCli.RunAsync(
            $"release download {release.Tag} -R {ReleaseRepository} -p {AssetName} -D \"{staging}\" --clobber",
            timeout: TimeSpan.FromMinutes(10));
        var downloaded = Path.Combine(staging, AssetName);
        if (download.ExitCode != 0 || !File.Exists(downloaded))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(download.Error) ? "Could not download the update. Check the network connection and try again." : download.Error.Trim());

        if (!string.IsNullOrEmpty(release.Sha256))
        {
            await using (var stream = File.OpenRead(downloaded))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The downloaded update did not pass the SHA-256 check and was deleted. Try again.");
            }
            log("Checksum verified.");
        }

        Directory.CreateDirectory(InstallDir);
        foreach (var old in Directory.EnumerateFiles(InstallDir, "*.old.exe"))
        {
            try { File.Delete(old); } catch { }
        }
        // A running exe can't be overwritten but can be renamed, so the agent can update itself in place.
        if (File.Exists(InstalledExe))
            File.Move(InstalledExe, Path.Combine(InstallDir, $"RunnerWidget.{DateTime.UtcNow.Ticks}.old.exe"));
        File.Move(downloaded, InstalledExe);
        try { Directory.Delete(staging, recursive: true); } catch { }
        log($"Installed {release.Tag} to {InstallDir}.");
        return InstalledExe;
    }

    /// <summary>
    /// Starts a detached helper that waits for this process to exit, then starts the task again. Launched before
    /// the task is re-registered, so the agent comes back even if re-registering ends the running instance.
    /// </summary>
    public static void ScheduleAgentTaskRestart(string taskName)
    {
        var script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 120 -ErrorAction SilentlyContinue; " +
                     $"Start-Sleep -Seconds 2; schtasks.exe /Run /TN '{taskName.Replace("'", "''")}' | Out-Null";
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>
    /// Copies the running build (a release exe or a build folder) into the install folder, so the fleet agent runs from a
    /// stable place that later rebuilds don't lock or overwrite. Returns the installed exe.
    /// </summary>
    public static async Task<string> InstallCurrentBuildAsync(Action<string> log)
    {
        if (IsRunningInstalledCopy) return InstalledExe;
        Directory.CreateDirectory(InstallDir);
        foreach (var old in Directory.EnumerateFiles(InstallDir, "*.old.*"))
        {
            try { File.Delete(old); } catch { }
        }
        log($"Installing Runner Manager v{CurrentVersion} to {InstallDir}...");
        var files = AppFiles();
        await Task.Run(() =>
        {
            // Going from a multi-file build to a single-file exe: clear the old app files so only the exe remains.
            if (files.Count == 1)
                foreach (var stale in Directory.EnumerateFiles(InstallDir)
                             .Where(f => !Path.GetFileName(f).Equals(AssetName, StringComparison.OrdinalIgnoreCase) && !f.Contains(".old.")))
                {
                    try { File.Delete(stale); }
                    catch { try { File.Move(stale, $"{stale}.{DateTime.UtcNow.Ticks}.old{Path.GetExtension(stale)}"); } catch { } }
                }
            foreach (var (source, relative) in files)
            {
                var destination = Path.Combine(InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Files of a running agent can't be overwritten but can be renamed out of the way.
                if (File.Exists(destination))
                {
                    try { File.Delete(destination); }
                    catch { File.Move(destination, $"{destination}.{DateTime.UtcNow.Ticks}.old.{Path.GetExtension(destination).TrimStart('.')}"); }
                }
                File.Copy(source, destination);
            }
        });
        await CreateStartMenuShortcutAsync();
        return InstalledExe;
    }

    /// <summary>
    /// The files that make up the running app. A single-file release (e.g. RunnerManagerSetup.exe in Downloads) is just
    /// its exe, installed as RunnerWidget.exe; a build folder is the app's own files, never whatever else is next to it.
    /// </summary>
    internal static List<(string Source, string Relative)> AppFiles()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not find the path of the running program.");
        var isSingleFile = string.IsNullOrEmpty(typeof(AppUpdater).Assembly.Location);
        if (isSingleFile) return new List<(string, string)> { (exe, AssetName) };

        var folder = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(folder, "RunnerWidget.deps.json")))
            throw new InvalidOperationException($"{folder} is not a Runner Manager build folder.");
        var files = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            var isApp = name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("RunnerWidget.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (isApp) files.Add((file, name));
        }
        var runtimes = Path.Combine(folder, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                files.Add((file, Path.GetRelativePath(folder, file)));
        return files;
    }

    /// <summary>Adds a Start menu shortcut for the installed copy.</summary>
    public static async Task CreateStartMenuShortcutAsync()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var shortcut = Path.Combine(programs, "Runner Manager.lnk").Replace("'", "''");
        var target = InstalledExe.Replace("'", "''");
        var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{shortcut}');$s.TargetPath='{target}';" +
                     $"$s.WorkingDirectory='{InstallDir.Replace("'", "''")}';$s.Description='Runner Manager';$s.Save()";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process != null) await process.WaitForExitAsync();
    }
}

public sealed class ReleaseInfo
{
    public string Tag { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}
