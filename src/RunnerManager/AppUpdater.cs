using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace RunnerManager;

/// <summary>
/// Installs Runner Manager from its GitHub Releases into
/// %LOCALAPPDATA%\Programs\RunnerManager, so PCs don't need the .NET SDK or a git checkout.
/// </summary>
public static class AppUpdater
{
    public const string AssetName = AppPaths.ExeName;
    /// <summary>The first version that answers --probe, so an update can be test-started before it replaces this one.</summary>
    private const string ProbeSupportedSince = "2.2.0";
    public static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RunnerManager");
    private static string? _installDir;
    /// <summary>Where Runner Manager is installed: the folder chosen in setup (kept in the Settings → Apps entry), or the default.</summary>
    public static string InstallDir => _installDir ??= Installer.RegisteredInstallDir() ?? DefaultInstallDir;
    /// <summary>Setup installs into the folder the user chose.</summary>
    internal static void UseInstallDir(string folder) => _installDir = folder;
    internal static bool IsDefaultInstallDir(string folder) => SameFolder(folder, DefaultInstallDir);
    internal static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    public static string InstalledExe => Path.Combine(InstallDir, AssetName);
    /// <summary>The installed copy when there is one, otherwise the running exe.</summary>
    public static string PreferredExe => File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    /// <summary>A failed check is tried again soon, so a short outage does not hide updates for half an hour.</summary>
    private static readonly TimeSpan FailedCheckInterval = TimeSpan.FromMinutes(2);
    private static ReleaseCheck? _lastCheck;
    private static DateTime _checkedUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    // The last answer and its ETag: GitHub answers an unchanged release with 304, which does not count against its limit.
    private static string? _releaseEtag;
    private static string? _releaseJson;
    private static readonly HttpClient Http = CreateHttpClient();
    /// <summary>Held while files in the install folder are replaced, so the agent and the dashboard never swap at once.</summary>
    private const string UpdateMutexName = @"Global\RunnerManagerUpdate";
    private static readonly TimeSpan UpdateLockTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Set once this process has copied its build into the install folder, so setup copies it only once.</summary>
    private static bool _installedThisBuild;

    /// <summary>Where Runner Manager's releases are published.</summary>
    public const string ReleaseRepository = "Sandiozo/runner-manager";
    public static string ReleasesUrl => $"https://github.com/{ReleaseRepository}/releases";

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

    /// <summary>"v2.1.0", "2.1" or "2.1.0+abc" → "2.1.0". A pre-release keeps its label: "2.4.0-beta.1".</summary>
    public static string Normalize(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '+', ' ' });
        if (cut > 0) text = text[..cut];
        var dash = text.IndexOf('-');
        var core = dash > 0 ? text[..dash] : text;
        var label = dash > 0 ? text[(dash + 1)..] : string.Empty;
        if (!Version.TryParse(core, out var parsed)) return text;
        var normalized = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0)).ToString(3);
        return label.Length > 0 ? $"{normalized}-{label}" : normalized;
    }

    public static bool IsNewer(string candidate, string current)
        => CompareVersions(candidate, current) is > 0;

    public static bool IsOlder(string candidate, string current)
        => CompareVersions(candidate, current) is < 0;

    /// <summary>
    /// Compares two versions like semantic versioning does: 2.4.0-beta &lt; 2.4.0-beta.2 &lt; 2.4.0-rc.1 &lt; 2.4.0.
    /// Null when either is not a version.
    /// </summary>
    internal static int? CompareVersions(string a, string b)
    {
        if (!TrySplit(a, out var coreA, out var labelA) || !TrySplit(b, out var coreB, out var labelB)) return null;
        var core = coreA.CompareTo(coreB);
        if (core != 0) return core;
        // A release is newer than its pre-releases.
        if (labelA.Length == 0 || labelB.Length == 0) return labelB.Length.CompareTo(labelA.Length) switch { > 0 => 1, < 0 => -1, _ => 0 };
        var partsA = labelA.Split('.');
        var partsB = labelB.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var numericA = long.TryParse(partsA[i], out var numberA);
            var numericB = long.TryParse(partsB[i], out var numberB);
            var part = numericA && numericB ? numberA.CompareTo(numberB)
                : numericA ? -1
                : numericB ? 1
                : string.CompareOrdinal(partsA[i], partsB[i]);
            if (part != 0) return Math.Sign(part);
        }
        return partsA.Length.CompareTo(partsB.Length);
    }

    private static bool TrySplit(string version, out Version core, out string label)
    {
        var text = Normalize(version);
        var dash = text.IndexOf('-');
        label = dash > 0 ? text[(dash + 1)..] : string.Empty;
        return Version.TryParse(dash > 0 ? text[..dash] : text, out core!);
    }

    /// <summary>The version of the installed copy, or null when there is none.</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                return File.Exists(InstalledExe) && FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion is { Length: > 0 } version
                    ? Normalize(version) : null;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// The newest release. Throws with the reason when it could not be checked, so a failed check is never mistaken for
    /// "up to date". See <see cref="CheckForUpdateAsync"/>.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(bool force = false)
    {
        var check = await CheckForUpdateAsync(force);
        return check.Release ?? throw new InvalidOperationException(check.Problem);
    }

    /// <summary>
    /// Reads the newest release from GitHub without signing in (the repository is public), and through the GitHub CLI
    /// when that fails. A result is kept for 30 minutes, a failure for 2.
    /// </summary>
    public static async Task<ReleaseCheck> CheckForUpdateAsync(bool force = false)
    {
        await CheckGate.WaitAsync();
        try
        {
            if (!force && _lastCheck is { } last
                && DateTime.UtcNow - _checkedUtc < (last.Release != null ? CheckInterval : FailedCheckInterval)) return last;
            var check = await FetchLatestReleaseAsync();
            _lastCheck = check;
            _checkedUtc = DateTime.UtcNow;
            return check;
        }
        finally { CheckGate.Release(); }
    }

    private static async Task<ReleaseCheck> FetchLatestReleaseAsync()
    {
        var (json, problem) = await FetchReleaseJsonAsync();
        if (json != null) return ParseRelease(json);
        // The GitHub CLI has a higher limit when it is signed in.
        var cli = await GitHubCli.RunAsync($"api repos/{ReleaseRepository}/releases/latest");
        if (cli.ExitCode == 0) return ParseRelease(cli.Output);
        return new ReleaseCheck(null, problem);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RunnerManager/{CurrentVersion} (+https://github.com/{ReleaseRepository})");
        return http;
    }

    /// <summary>Reads releases/latest anonymously. Returns the JSON, or null with the reason.</summary>
    private static async Task<(string? Json, string Problem)> FetchReleaseJsonAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleaseRepository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_releaseEtag != null && _releaseJson != null) request.Headers.TryAddWithoutValidation("If-None-Match", _releaseEtag);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Http.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && _releaseJson != null) return (_releaseJson, string.Empty);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                _releaseEtag = response.Headers.ETag?.ToString();
                _releaseJson = json;
                return (json, string.Empty);
            }
            return (null, DescribeFailure((int)response.StatusCode, Header(response, "x-ratelimit-remaining"),
                Header(response, "x-ratelimit-reset"), response.Headers.RetryAfter?.Delta, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) { return (null, "GitHub did not answer in time."); }
        catch (HttpRequestException ex) { return (null, $"Could not reach GitHub: {ex.GetBaseException().Message}"); }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Why GitHub refused a release check, from its status code and rate-limit headers.</summary>
    internal static string DescribeFailure(int status, string? remaining, string? reset, TimeSpan? retryAfter, DateTimeOffset now)
    {
        var limited = status == 429 || status == 403 && (remaining == "0" || retryAfter != null);
        if (limited)
        {
            var until = retryAfter is { } delay ? now + delay
                : long.TryParse(reset, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            return until is { } when && when > now
                ? $"GitHub limits checks from this network. Try again after {when.ToLocalTime():HH:mm}."
                : "GitHub limits checks from this network. Try again later.";
        }
        return status == 404
            ? $"No Runner Manager release found in {ReleaseRepository}."
            : $"GitHub answered with an error (HTTP {status}).";
    }

    /// <summary>Reads a release from GitHub's JSON. Fails when it has no <see cref="AssetName"/>.</summary>
    internal static ReleaseCheck ParseRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                return new ReleaseCheck(new ReleaseInfo
                {
                    Tag = tag,
                    Version = Normalize(tag),
                    HtmlUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                    DownloadUrl = asset.TryGetProperty("browser_download_url", out var download) ? download.GetString() ?? string.Empty : string.Empty,
                    Size = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
                    Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : string.Empty
                }, string.Empty);
            }
            return new ReleaseCheck(null, $"Release {tag} has no {AssetName}.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ReleaseCheck(null, "GitHub's answer could not be read.");
        }
    }

    /// <summary>
    /// Downloads the release's exe. Uses the public download link, and the GitHub CLI when that fails.
    /// </summary>
    private static async Task DownloadReleaseAsync(ReleaseInfo release, string staging, string destination)
    {
        string problem;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var response = await Http.GetAsync(DownloadUrlFor(release), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(destination))
                await response.Content.CopyToAsync(file, timeout.Token);
            if (release.Size <= 0 || new FileInfo(destination).Length == release.Size) return;
            problem = "The download was incomplete.";
        }
        catch (OperationCanceledException) { problem = "The download did not finish in time."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { problem = $"Could not download the update: {ex.GetBaseException().Message}"; }
        try { File.Delete(destination); } catch { }

        var download = await GitHubCli.RunAsync(
            $"release download {release.Tag} -R {ReleaseRepository} -p {AssetName} -D \"{staging}\" --clobber",
            timeout: TimeSpan.FromMinutes(10));
        if (download.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException($"{problem} Check the network connection and try again.");
    }

    /// <summary>The asset's download link when it is one of this repository's release downloads, otherwise the standard link.</summary>
    internal static string DownloadUrlFor(ReleaseInfo release)
    {
        var expected = $"https://github.com/{ReleaseRepository}/releases/download/";
        return release.DownloadUrl.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            ? release.DownloadUrl
            : $"{expected}{Uri.EscapeDataString(release.Tag)}/{AssetName}";
    }

    /// <summary>
    /// Downloads the release, verifies its checksum and swaps it into the install folder. Refuses to replace a newer
    /// installed copy unless <paramref name="allowOlder"/> is set (the user picked that version).
    /// </summary>
    public static async Task<string> DownloadAndInstallAsync(ReleaseInfo release, Action<string> log, bool allowOlder = false)
    {
        if (string.IsNullOrEmpty(release.Sha256))
            throw new InvalidOperationException("This release has no checksum, so it was not installed.");
        if (!allowOlder && InstalledVersion is { } installed && IsOlder(release.Version, installed))
            throw new InvalidOperationException($"Runner Manager v{installed} is installed, which is newer than {release.Tag}. It was kept.");

        // Each process downloads into its own folder, so the agent and the dashboard never share files.
        var staging = Path.Combine(FleetConfig.StateDir, $"update-{Environment.ProcessId}");
        RemoveStaleStagingFolders();
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            log($"Downloading Runner Manager {release.Tag}...");
            var downloaded = Path.Combine(staging, AssetName);
            await DownloadReleaseAsync(release, staging, downloaded);

            string actual;
            await using (var stream = File.OpenRead(downloaded))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(downloaded);
                throw new InvalidOperationException("The downloaded update did not pass the SHA-256 check and was deleted. Try again.");
            }
            log("Checksum verified.");

            if (!IsNewer(ProbeSupportedSince, release.Version))
                await EnsureRuntimeAsync(downloaded, staging, log);

            var replacedVersion = InstalledVersion ?? string.Empty;
            await Task.Run(() => WithUpdateLock(() =>
            {
                Directory.CreateDirectory(InstallDir);
                foreach (var old in Directory.EnumerateFiles(InstallDir, "*.old.exe"))
                {
                    try { File.Delete(old); } catch { }
                }
                ReplaceFile(downloaded, InstalledExe, keepPrevious: true);
            }));
            // The fleet agent of the new version proves that it works, or the previous exe is put back (see UpdateGuard).
            UpdateGuard.MarkPending(release.Version, replacedVersion);
            log($"Installed {release.Tag} to {InstallDir}.");
            return InstalledExe;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>Removes download folders left by processes that have exited.</summary>
    private static void RemoveStaleStagingFolders()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(FleetConfig.StateDir, "update-*"))
            {
                if (int.TryParse(Path.GetFileName(folder)["update-".Length..], out var id) && IsProcessRunning(id)) continue;
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }
        catch { }
    }

    private static bool IsProcessRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs <paramref name="swap"/> while holding the update lock. The lock is taken and released on the same thread,
    /// because a mutex belongs to the thread that acquired it.
    /// </summary>
    private static void WithUpdateLock(Action swap)
    {
        using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
        bool owned;
        try { owned = mutex.WaitOne(UpdateLockTimeout); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
            throw new InvalidOperationException("Another update of Runner Manager is being installed. Try again in a few minutes.");
        try { swap(); }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="destination"/> without a moment where neither exists. The new file
    /// is copied next to the destination first; a running exe can't be overwritten but can be renamed, so the current file
    /// is renamed away, the new one is moved into place, and the current file is renamed back if that fails.
    /// With <paramref name="keepPrevious"/>, the current file is kept as "{destination}.previous", so the update can be undone.
    /// </summary>
    private static void ReplaceFile(string source, string destination, bool keepPrevious = false)
    {
        var next = destination + ".new";
        // Antivirus scanners briefly lock new exe files, so moves are retried.
        Retry(() => File.Copy(source, next, overwrite: true));
        string? previous = null;
        if (File.Exists(destination))
        {
            var aside = keepPrevious ? destination + ".previous" : OldPathFor(destination);
            // The version kept last time may still run (a dashboard that was not restarted): rename it out of the way.
            if (keepPrevious && File.Exists(aside)) Retry(() => File.Move(aside, OldPathFor(destination)));
            Retry(() => File.Move(destination, aside));
            previous = aside;
        }
        try
        {
            Retry(() => File.Move(next, destination));
        }
        catch (Exception ex)
        {
            if (previous != null) PutBack(previous, destination, ex);
            try { File.Delete(next); } catch { }
            throw;
        }
        // Succeeds when the old file is not in use; otherwise the next install removes it.
        if (previous != null && !keepPrevious)
        {
            try { File.Delete(previous); } catch { }
        }
    }

    /// <summary>
    /// Puts RunnerManager.exe.previous back as RunnerManager.exe. The current exe (it may be running) is renamed to an
    /// .old.exe file, which a later install removes.
    /// </summary>
    internal static void RestorePrevious()
    {
        var previous = InstalledExe + ".previous";
        if (!File.Exists(previous)) throw new FileNotFoundException("There is no previous version to go back to.", previous);
        WithUpdateLock(() =>
        {
            string? aside = null;
            if (File.Exists(InstalledExe))
            {
                aside = OldPathFor(InstalledExe);
                Retry(() => File.Move(InstalledExe, aside));
            }
            try { Retry(() => File.Move(previous, InstalledExe)); }
            catch (Exception ex)
            {
                if (aside != null) PutBack(aside, InstalledExe, ex);
                throw;
            }
        });
    }

    private static string OldPathFor(string file) => Path.Combine(Path.GetDirectoryName(file)!,
        $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.UtcNow.Ticks}.old{Path.GetExtension(file)}");

    /// <summary>
    /// Moves <paramref name="aside"/> back to <paramref name="destination"/> after a failed swap. When even that fails
    /// (for example an antivirus scanner holds it), a copy is tried, so the install folder is never left without the file.
    /// </summary>
    private static void PutBack(string aside, string destination, Exception cause)
    {
        try
        {
            Retry(() => File.Move(aside, destination));
            return;
        }
        catch { }
        try
        {
            Retry(() => File.Copy(aside, destination, overwrite: false));
            return;
        }
        catch { }
        throw new InvalidOperationException(
            $"{cause.Message} {Path.GetFileName(destination)} could not be put back either. Rename {aside} to {Path.GetFileName(destination)} to repair it.", cause);
    }

    /// <summary>Runs a file operation, retrying for about three seconds while the file is in use.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException && attempt < 6)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>
    /// Test-starts a downloaded version. When it needs a .NET runtime this PC does not have, installs Microsoft's
    /// signed runtime first, so an update never replaces a working version with one that cannot start.
    /// </summary>
    private static async Task EnsureRuntimeAsync(string exe, string staging, Action<string> log)
    {
        var probe = await ProbeAsync(exe);
        if (probe.ExitCode == 0) return;

        var missing = Regex.Match(probe.Output, @"Framework: '[^']+', version '(?<major>\d+)\.");
        if (!missing.Success)
            throw new InvalidOperationException($"The update could not start on this PC (exit code {probe.ExitCode}). The current version is kept.");

        var major = missing.Groups["major"].Value;
        log($"The update needs the .NET {major} Desktop Runtime. Downloading it from Microsoft...");
        var installer = Path.Combine(staging, $"windowsdesktop-runtime-{major}-win-x64.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        await using (var file = File.Create(installer))
        await using (var stream = await http.GetStreamAsync($"https://aka.ms/dotnet/{major}.0/windowsdesktop-runtime-win-x64.exe"))
            await stream.CopyToAsync(file);

        var signer = await ScheduledTasks.RunAsync("powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -Command \"$s = Get-AuthenticodeSignature -LiteralPath '{installer.Replace("'", "''")}'; " +
            "if ($s.Status -eq 'Valid') { $s.SignerCertificate.Subject }\"");
        if (signer.ExitCode != 0 || !signer.Output.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded .NET runtime is not signed by Microsoft and was not installed. The current version is kept.");

        log($"Installing the .NET {major} Desktop Runtime...");
        var install = await ScheduledTasks.RunAsync(installer, "/install /quiet /norestart");
        // 1641 and 3010: installed, a restart is recommended.
        if (install.ExitCode is not (0 or 1641 or 3010))
            throw new InvalidOperationException($"The .NET {major} Desktop Runtime could not be installed (exit code {install.ExitCode}). The current version is kept.");

        probe = await ProbeAsync(exe);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException($"The update still could not start after installing .NET {major} (exit code {probe.ExitCode}). The current version is kept.");
        log($".NET {major} Desktop Runtime installed.");
    }

    private static async Task<(int ExitCode, string Output)> ProbeAsync(string exe)
    {
        var start = new ProcessStartInfo(exe, App.ProbeArgument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Without this, a missing runtime opens a dialog that waits for someone to click it.
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the update to check it.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "The update did not respond.");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>
    /// Installs a release on this PC, as "Install update" does: downloads and verifies it, refreshes the Start menu
    /// shortcut, and switches the fleet agent and the start-up task to the new copy. Returns the installed exe.
    /// </summary>
    public static async Task<string> InstallReleaseAsync(ReleaseInfo release, Action<string> log)
    {
        var exe = await DownloadAndInstallAsync(release, log);
        await CreateStartMenuShortcutAsync();
        if (await MachineRelay.IsAgentInstalledAsync())
        {
            log("Switching the fleet agent to the new version...");
            await MachineRelay.RegisterAgentTaskAsync(exe);
            await MachineRelay.RestartLocalAgentAsync();
        }
        if (await ScheduledTasks.ExistsAsync(ScheduledTasks.DashboardTaskName))
            await Installer.RegisterStartupTaskAsync(exe);
        return exe;
    }

    /// <summary>
    /// Starts a detached helper that waits for this process to exit, then starts the task again. Launched before
    /// the task is re-registered, so the agent comes back even if re-registering ends the running instance.
    /// </summary>
    public static void ScheduleAgentTaskRestart(string taskName)
        => RunAfterExit($"schtasks.exe /Run /TN '{taskName.Replace("'", "''")}' | Out-Null", TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(2));

    /// <summary>
    /// Starts a hidden, detached PowerShell helper that waits for this process to exit (at most <paramref name="timeout"/>),
    /// waits <paramref name="delay"/> more, then runs <paramref name="script"/>.
    /// </summary>
    internal static void RunAfterExit(string script, TimeSpan timeout, TimeSpan delay)
    {
        var wrapped = $"Wait-Process -Id {Environment.ProcessId} -Timeout {(int)timeout.TotalSeconds} -ErrorAction SilentlyContinue; " +
                      $"Start-Sleep -Seconds {(int)delay.TotalSeconds}; {script}";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", wrapped },
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>
    /// Copies the running build (a release exe or a build folder) into the install folder, so the fleet agent runs from a
    /// stable place that later rebuilds don't lock or overwrite. Returns the installed exe.
    /// </summary>
    public static Task<string> InstallCurrentBuildAsync(Action<string> log) => InstallCurrentBuildAsync(log, createShortcut: true);

    /// <summary>
    /// Like <see cref="InstallCurrentBuildAsync(Action{string})"/>; setup passes false for <paramref name="createShortcut"/>
    /// because it creates the shortcuts itself. Copies the build only once per process. A newer installed copy is kept
    /// and used instead, unless <paramref name="allowOlder"/> is set (setup, after the user was told).
    /// </summary>
    internal static async Task<string> InstallCurrentBuildAsync(Action<string> log, bool createShortcut, bool allowOlder = false)
    {
        if (IsRunningInstalledCopy) return InstalledExe;
        if (_installedThisBuild && File.Exists(InstalledExe)) return InstalledExe;
        if (!allowOlder && InstalledVersion is { } installed && IsOlder(CurrentVersion, installed))
        {
            log($"Runner Manager v{installed} is installed, which is newer than this copy (v{CurrentVersion}). Using the installed copy.");
            return InstalledExe;
        }
        Directory.CreateDirectory(InstallDir);
        foreach (var old in Directory.EnumerateFiles(InstallDir, "*.old.*"))
        {
            try { File.Delete(old); } catch { }
        }
        log($"Installing Runner Manager v{CurrentVersion} to {InstallDir}...");
        var files = AppFiles();
        await Task.Run(() => WithUpdateLock(() =>
        {
            // Going from a multi-file build to a single-file exe: clear the old app files so only the exe remains.
            // Only builds before 2.2 were multi-file, and they always used the default folder, which only holds this app.
            if (files.Count == 1 && IsDefaultInstallDir(InstallDir))
            {
                foreach (var stale in Directory.EnumerateFiles(InstallDir)
                             .Where(f => !Path.GetFileName(f).StartsWith(AssetName, StringComparison.OrdinalIgnoreCase) && !f.Contains(".old.")))
                {
                    try { File.Delete(stale); }
                    catch { try { File.Move(stale, $"{stale}.{DateTime.UtcNow.Ticks}.old{Path.GetExtension(stale)}"); } catch { } }
                }
                foreach (var folder in Directory.EnumerateDirectories(InstallDir))
                {
                    try { Directory.Delete(folder, recursive: true); } catch { }
                }
            }
            foreach (var (source, relative) in files)
            {
                var destination = Path.Combine(InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Files of a running agent can't be overwritten but can be renamed out of the way.
                ReplaceFile(source, destination);
            }
        }));
        _installedThisBuild = true;
        if (createShortcut) await CreateStartMenuShortcutAsync();
        return InstalledExe;
    }

    /// <summary>
    /// The files that make up the running app. A single-file release (e.g. RunnerManagerSetup.exe in Downloads) is just
    /// its exe, installed as RunnerManager.exe; a build folder is the app's own files, never whatever else is next to it.
    /// </summary>
    internal static List<(string Source, string Relative)> AppFiles()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not find the path of the running program.");
        // An empty Location is exactly how a single-file build is recognized here.
#pragma warning disable IL3000
        var isSingleFile = string.IsNullOrEmpty(typeof(AppUpdater).Assembly.Location);
#pragma warning restore IL3000
        if (isSingleFile) return new List<(string, string)> { (exe, AssetName) };

        var folder = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(folder, "RunnerManager.deps.json")))
            throw new InvalidOperationException($"{folder} is not a Runner Manager build folder.");
        var files = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            var isApp = name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("RunnerManager.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (isApp) files.Add((file, name));
        }
        var runtimes = Path.Combine(folder, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                files.Add((file, Path.GetRelativePath(folder, file)));
        return files;
    }

    /// <summary>Adds a Start menu shortcut for the installed copy.</summary>
    public static Task CreateStartMenuShortcutAsync() => Installer.CreateShortcutAsync(Installer.StartMenuShortcut, InstalledExe);
}

public sealed class ReleaseInfo
{
    public string Tag { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    /// <summary>The exe's public download link (browser_download_url).</summary>
    public string DownloadUrl { get; init; } = string.Empty;
    /// <summary>The exe's size in bytes, or 0 when unknown.</summary>
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>The result of an update check: the newest release, or why it could not be checked.</summary>
public sealed record ReleaseCheck(ReleaseInfo? Release, string Problem)
{
    public bool UpdateAvailable => Release != null && AppUpdater.IsNewer(Release.Version, AppUpdater.CurrentVersion);
}
