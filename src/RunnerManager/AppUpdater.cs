using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace RunnerManager;

/// <summary>
/// Installs Runner Manager from its GitHub Releases into its install folder (Program Files\RunnerManager for new
/// installs, see <see cref="MachineInstall"/>), so PCs don't need the .NET SDK or a git checkout.
/// </summary>
public static class AppUpdater
{
    public const string AssetName = AppPaths.ExeName;
    /// <summary>The first version that answers --probe, so an update can be test-started before it replaces this one.</summary>
    private const string ProbeSupportedSince = "2.2.0";
    /// <summary>Where setup installs on a PC without an install: Program Files\RunnerManager.</summary>
    public static string DefaultInstallDir => MachineInstall.ProgramFilesDir;
    private static string? _installDir;
    /// <summary>
    /// Where Runner Manager is installed: the folder chosen in setup, otherwise the folder recorded for this user where only
    /// administrators can change it, or the folder of the running install (see <see cref="MachineInstall.Resolve"/>).
    /// </summary>
    public static string InstallDir => _installDir ?? MachineInstall.CurrentInstallDir();
    /// <summary>Setup installs into the folder the user chose.</summary>
    internal static void UseInstallDir(string folder)
    {
        // A setup tried again in another folder copies the build there too.
        if (_installDir == null || !SameFolder(_installDir, folder)) _installedThisBuild = false;
        _installDir = folder;
    }
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
            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token))
                await CopyDownloadAsync(body, file, DownloadStallLimit, timeout.Token);
            if (release.Size <= 0 || new FileInfo(destination).Length == release.Size) return;
            problem = "The download was incomplete.";
        }
        catch (OperationCanceledException) { problem = "The download did not finish in time."; }
        catch (TimeoutException ex) { problem = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { problem = $"Could not download the update: {ex.GetBaseException().Message}"; }
        try { File.Delete(destination); } catch { }

        var download = await GitHubCli.RunAsync(
            $"release download {release.Tag} -R {ReleaseRepository} -p {AssetName} -D \"{staging}\" --clobber",
            timeout: TimeSpan.FromMinutes(10));
        if (download.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException($"{problem} Check the network connection and try again.");
    }

    /// <summary>A download that receives nothing for this long has stopped, whatever its overall time limit.</summary>
    private static readonly TimeSpan DownloadStallLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Copies a download into <paramref name="destination"/>. Throws <see cref="TimeoutException"/> when no data arrives for
    /// <paramref name="stallLimit"/>; <paramref name="cancellationToken"/> carries the limit for the whole download.
    /// </summary>
    internal static async Task CopyDownloadAsync(Stream source, Stream destination, TimeSpan stallLimit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(stallLimit);
            int read;
            try { read = await source.ReadAsync(buffer, stall.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The download received no data for {stallLimit.TotalSeconds:0} seconds.");
            }
            if (read == 0) return;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>The asset's download link when it is one of this repository's release downloads, otherwise the standard link.</summary>
    internal static string DownloadUrlFor(ReleaseInfo release)
    {
        var expected = $"https://github.com/{ReleaseRepository}/releases/download/";
        return release.DownloadUrl.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            ? release.DownloadUrl
            : $"{expected}{Uri.EscapeDataString(release.Tag)}/{AssetName}";
    }

    private static bool IsInstalled(ReleaseInfo release)
        => InstalledVersion is { } current && CompareVersions(current, release.Version) == 0 && InstalledMatches(release.Sha256);

    /// <summary>Whether the installed exe is exactly the file with this SHA-256.</summary>
    private static bool InstalledMatches(string sha256)
    {
        try
        {
            using var stream = File.OpenRead(InstalledExe);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
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
        // Installing it again would replace the copy of the previous version that a failed update goes back to.
        if (IsInstalled(release))
        {
            log($"Runner Manager {release.Tag} is already installed.");
            return InstalledExe;
        }

        // Each process downloads into its own folder, so the agent and the dashboard never share files. It is inside the
        // install folder: in Program Files only administrators can change files there, so a program without administrator
        // rights can neither swap the file between the checksum and the copy nor put a DLL beside it for the test start.
        var staging = StagingFolder(InstallDir, Environment.ProcessId);
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

            var replacedVersion = string.Empty;
            var swapped = await Task.Run(() => WithUpdateLock(() =>
            {
                // Checked again under the lock: the dashboard and the agent may install the same release at the same
                // moment, and a second swap would keep this release as the previous version instead of the one it replaced.
                if (IsInstalled(release)) return false;
                replacedVersion = InstalledVersion ?? string.Empty;
                Directory.CreateDirectory(InstallDir);
                RemoveOldFiles();
                ReplaceFile(downloaded, InstalledExe, keepPrevious: true, new SwapMarker(SwapKind.Update, replacedVersion, release.Version));
                return true;
            }));
            if (!swapped)
            {
                log($"Runner Manager {release.Tag} is already installed.");
                return InstalledExe;
            }
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

    /// <summary>
    /// Where this process prepares an update: a folder of its own inside the install folder, with a name that only
    /// Runner Manager uses, because a folder chosen in setup may also hold the user's own folders.
    /// </summary>
    internal static string StagingFolder(string installDir, int processId) => Path.Combine(installDir, $"{StagingPrefix}{processId}");

    /// <summary>Whether a folder name is one that <see cref="StagingFolder"/> gives.</summary>
    internal static bool IsStagingFolderName(string name) => StagingName.IsMatch(name);

    private const string StagingPrefix = "RunnerManager.update-";
    private static readonly Regex StagingName = new(@"^RunnerManager\.update-\d+$", RegexOptions.IgnoreCase);
    /// <summary>The download folders of versions up to 2.9.0, in the data folder.</summary>
    private const string OldStagingPrefix = "update-";
    private static readonly Regex OldStagingName = new(@"^update-\d+$", RegexOptions.IgnoreCase);

    private static void RemoveStaleStagingFolders() => RemoveStaleStagingFolders(InstallDir, IsProcessRunning);

    /// <summary>
    /// Removes the download folders (see <see cref="StagingFolder"/>) that processes left in the install folder when they
    /// exited. Nothing is followed or deleted through a reparse point (a junction or link): a download folder only ever
    /// holds files, and one that holds anything else, or is a reparse point itself, is left as it is.
    /// </summary>
    internal static void RemoveStaleStagingFolders(string installDir, Func<int, bool> isRunning)
        => RemoveStale(installDir, StagingPrefix, StagingName, isRunning);

    /// <summary>
    /// Removes the download folders update-&lt;number&gt; that versions up to 2.9.0 left in the data folder, only in a
    /// start without administrator rights (<paramref name="elevated"/> false). Any program of the user can change the
    /// data folder, so it could swap such a folder for a link while it is deleted. Without administrator rights, that
    /// reaches nothing that the user could not delete anyway. With them, the folders are left as they are.
    /// </summary>
    internal static void RemoveOldStagingFolders(string dataDir, bool elevated, Func<int, bool> isRunning)
    {
        if (!elevated) RemoveStale(dataDir, OldStagingPrefix, OldStagingName, isRunning);
    }

    /// <summary><see cref="RemoveOldStagingFolders(string, bool, Func{int, bool})"/> for this user's data folder.</summary>
    internal static void RemoveOldStagingFolders() => RemoveOldStagingFolders(FleetConfig.StateDir, Elevation.IsElevated, IsProcessRunning);

    private static void RemoveStale(string parent, string prefix, Regex name, Func<int, bool> isRunning)
    {
        try
        {
            var folder = new DirectoryInfo(parent);
            if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var staging in folder.EnumerateDirectories(prefix + "*"))
            {
                if (!name.IsMatch(staging.Name) || staging.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (int.TryParse(staging.Name[prefix.Length..], out var id) && isRunning(id)) continue;
                try
                {
                    var entries = staging.EnumerateFileSystemInfos().ToList();
                    if (entries.Any(e => e is DirectoryInfo || e.Attributes.HasFlag(FileAttributes.ReparsePoint))) continue;
                    foreach (var file in entries) file.Delete();
                    staging.Delete(recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>
    /// Whether a process with this id runs. One that exists but cannot be asked (a start without administrator rights
    /// cannot always ask one with them) counts as running, so its download folder is left as it is.
    /// </summary>
    private static bool IsProcessRunning(int id)
    {
        Process process;
        try { process = Process.GetProcessById(id); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        using (process)
        {
            try { return !process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>
    /// Runs <paramref name="swap"/> while holding the update lock. The lock is taken and released on the same thread,
    /// because a mutex belongs to the thread that acquired it.
    /// </summary>
    private static void WithUpdateLock(Action swap) => WithUpdateLock(() =>
    {
        swap();
        return true;
    });

    private static T WithUpdateLock<T>(Func<T> swap)
        => TryWithUpdateLock(UpdateLockTimeout, swap, out var result) ? result
            : throw new InvalidOperationException("Another update of Runner Manager is being installed. Try again in a few minutes.");

    /// <summary>Whether no process installs or undoes an update now (the update lock is free).</summary>
    internal static bool IsUpdateLockFree() => TryWithUpdateLock(TimeSpan.Zero, () => true, out _);

    /// <summary>Runs <paramref name="swap"/> under the update lock when it is free within <paramref name="wait"/>.</summary>
    internal static bool TryWithUpdateLock<T>(TimeSpan wait, Func<T> swap, out T result)
    {
        using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
        bool owned;
        try { owned = mutex.WaitOne(wait); }
        // A process that ended while it held the lock (for example in the middle of a swap) leaves it to the next one.
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            result = default!;
            return false;
        }
        try { result = swap(); }
        finally { mutex.ReleaseMutex(); }
        return true;
    }

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="destination"/>. The new file is copied next to the destination
    /// first and flushed to disk; a running exe can't be overwritten but can be renamed, so the current file is renamed
    /// away, the new one is moved into place, and the current file is renamed back if that fails. The swap starts once
    /// no scanner holds the new file, so the moment without <paramref name="destination"/> is just two renames; a swap cut
    /// off there (a crash or a power cut) leaves a complete "{destination}.new" and the <paramref name="swap"/> marker, which
    /// <see cref="RepairMissingExe"/> uses to finish it. A failed swap removes its .new, so no repair ever puts it in place.
    /// With <paramref name="keepPrevious"/>, the current file is kept as "{destination}.previous", so the update can be undone.
    /// </summary>
    internal static void ReplaceFile(string source, string destination, bool keepPrevious = false, SwapMarker? swap = null)
    {
        var next = destination + ".new";
        var partial = next + ".partial";
        try
        {
            // Antivirus scanners briefly lock new exe files, so file operations are retried.
            Retry(() => CopyFlushed(source, partial));
            // Named .new only once complete, so a repair never puts a half-written file in place.
            Retry(() => File.Move(partial, next, overwrite: true));
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        string? previous = null;
        var marked = false;
        try
        {
            WaitUntilUnused(next, NewFileWait);
            if (File.Exists(destination))
            {
                if (swap != null)
                {
                    WriteSwapMarker(destination, swap);
                    marked = true;
                }
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
                throw;
            }
        }
        catch
        {
            try { File.Delete(next); } catch { }
            // Kept only while the file is missing (it could not be put back), so a repair can finish the swap.
            if (marked && File.Exists(destination)) DeleteSwapMarker(destination);
            throw;
        }
        if (marked) DeleteSwapMarker(destination);
        // Succeeds when the old file is not in use; otherwise the next install removes it.
        if (previous != null && !keepPrevious)
        {
            try { File.Delete(previous); } catch { }
        }
    }

    /// <summary>
    /// Puts RunnerManager.exe.previous back as RunnerManager.exe. The current exe (it may be running) is renamed to an
    /// .old.exe file, which a later install removes. <paramref name="swap"/> says what is put back, for a repair.
    /// </summary>
    internal static void RestorePrevious(SwapMarker swap)
    {
        var previous = InstalledExe + ".previous";
        if (!File.Exists(previous)) throw new FileNotFoundException("There is no previous version to go back to.", previous);
        WithUpdateLock(() =>
        {
            string? aside = null;
            if (File.Exists(InstalledExe))
            {
                WriteSwapMarker(InstalledExe, swap);
                aside = OldPathFor(InstalledExe);
                try { Retry(() => File.Move(InstalledExe, aside)); }
                catch
                {
                    DeleteSwapMarker(InstalledExe);
                    throw;
                }
            }
            try { Retry(() => File.Move(previous, InstalledExe)); }
            catch (Exception ex)
            {
                if (aside != null) PutBack(aside, InstalledExe, ex);
                DeleteSwapMarker(InstalledExe);
                throw;
            }
            DeleteSwapMarker(InstalledExe);
            // This build is no longer the installed one, so a setup tried again copies it again.
            _installedThisBuild = false;
        });
    }

    /// <summary>How long a swap waits for a scanner to let go of the new file before it moves the current one away.</summary>
    private static readonly TimeSpan NewFileWait = TimeSpan.FromSeconds(10);

    /// <summary>Copies a file and flushes it to disk, so a power cut after the swap never leaves a partly written exe in place.</summary>
    private static void CopyFlushed(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Waits, at most <paramref name="limit"/>, until no other program has the file open (antivirus scanners open new exe
    /// files for a moment). Returns false when it is still open; the swap then goes ahead and retries as before.
    /// </summary>
    internal static bool WaitUntilUnused(string path, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true;
            }
            catch (IOException ex) when (ex is not FileNotFoundException && DateTime.UtcNow < deadline) { Thread.Sleep(200); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Puts RunnerManager.exe back when a swap was cut off while the exe was renamed away (a crash or a power cut). Only a
    /// swap leaves its marker (<see cref="SwapMarkerPath"/>) beside the exe for that moment, so an exe that went missing
    /// otherwise (quarantined by antivirus, deleted by hand) is never replaced by the older copy kept for a rollback.
    /// Only where the Settings → Apps entry shows an install, and not while another process holds the update lock.
    /// </summary>
    internal static ExeRepair RepairMissingExe() => RepairMissingExe(InstalledExe, () => Installer.HasUninstallEntry);

    internal static ExeRepair RepairMissingExe(string exe, Func<bool> isInstalled)
    {
        var nothing = new ExeRepair(RepairSource.None, null);
        try
        {
            var marker = SwapMarkerPath(exe);
            if (File.Exists(exe) && !File.Exists(marker) || !isInstalled()) return nothing;
            return TryWithUpdateLock(TimeSpan.FromSeconds(2), () =>
            {
                var next = exe + ".new";
                var previous = exe + ".previous";
                var swap = ReadSwapMarker(exe);
                var source = DecideRepair(File.Exists(exe), File.Exists(next), File.Exists(previous), swap);
                switch (source)
                {
                    case RepairSource.New:
                        File.Move(next, exe);
                        break;
                    case RepairSource.Previous:
                        File.Move(previous, exe);
                        try { File.Delete(next); } catch { }
                        break;
                }
                // Left by a swap that ended (or never reached the gap) without removing it.
                if (source != RepairSource.Unexplained && File.Exists(exe)) DeleteSwapMarker(exe);
                return new ExeRepair(source, swap);
            }, out var repair) ? repair : nothing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return nothing; }
    }

    /// <summary>
    /// Which file to put in place of a missing exe. Without a swap marker nothing is: the exe went missing for another
    /// reason. A cut-off update or install finishes with its complete new file; a cut-off rollback finishes with .previous.
    /// </summary>
    internal static RepairSource DecideRepair(bool exeExists, bool newExists, bool previousExists, SwapMarker? swap)
    {
        if (exeExists) return RepairSource.None;
        if (swap == null) return RepairSource.Unexplained;
        var forward = swap.Kind is SwapKind.Update or SwapKind.Install;
        if (forward && newExists) return RepairSource.New;
        return previousExists ? RepairSource.Previous : RepairSource.Unexplained;
    }

    /// <summary>Written beside the exe while a swap has renamed it away, and removed when the swap ends.</summary>
    internal static string SwapMarkerPath(string exe) => exe + ".swapping";

    internal static void WriteSwapMarker(string exe, SwapMarker swap)
    {
        // Flushed, so a power cut right after the rename still finds it.
        using var file = new FileStream(SwapMarkerPath(exe), FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, swap);
        file.Flush(flushToDisk: true);
    }

    /// <summary>The marker of a swap that did not end, or null. One that cannot be read still counts as a swap.</summary>
    internal static SwapMarker? ReadSwapMarker(string exe)
    {
        var path = SwapMarkerPath(exe);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SwapMarker>(File.ReadAllText(path)) ?? new SwapMarker(SwapKind.Unknown, "", ""); }
        catch (JsonException) { return new SwapMarker(SwapKind.Unknown, "", ""); }
    }

    private static void DeleteSwapMarker(string exe)
    {
        try { File.Delete(SwapMarkerPath(exe)); } catch { }
    }

    /// <summary>
    /// Removes files that earlier swaps renamed out of the way (<see cref="OldPathFor"/>) once nothing uses them. In a folder
    /// chosen in setup, which may hold other files, only Runner Manager's own exe names are removed.
    /// </summary>
    private static void RemoveOldFiles()
    {
        try
        {
            var anyAppFile = MachineInstall.HoldsOnlyThisApp(InstallDir);
            foreach (var file in Directory.EnumerateFiles(InstallDir))
            {
                if (!IsOldFile(Path.GetFileName(file), anyAppFile)) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether a file name is one that a swap gave the file it moved away; <paramref name="anyAppFile"/> also allows other app files than the exe.</summary>
    internal static bool IsOldFile(string name, bool anyAppFile) => (anyAppFile ? AnyOldFile : OwnOldFile).IsMatch(name);

    private static readonly Regex OwnOldFile = new(@"^(RunnerManager|RunnerManagerLauncher|RunnerWidget)\.\d{15,}\.old\.exe$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyOldFile = new(@"\.\d{15,}\.old(\.[^.]+)?$", RegexOptions.IgnoreCase);

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
        // HttpClient's own timeout ends when the headers arrive, so the body has its own limits.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var response = await Http.GetAsync($"https://aka.ms/dotnet/{major}.0/windowsdesktop-runtime-win-x64.exe",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(installer);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            await CopyDownloadAsync(body, file, DownloadStallLimit, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or IOException)
        {
            var reason = ex switch
            {
                OperationCanceledException => "The download did not finish in time.",
                TimeoutException => ex.Message,
                _ => ex.GetBaseException().Message
            };
            throw new InvalidOperationException($"The .NET {major} Desktop Runtime could not be downloaded. {reason} The current version is kept.", ex);
        }

        var signer = await SignerOfAsync(installer);
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

    /// <summary>
    /// The subject of the certificate that signed <paramref name="file"/> when its signature is valid, otherwise empty
    /// output. The path reaches PowerShell as an environment variable, never inside the script's text, so no character in
    /// it (such as ’ or ‘, which also end a PowerShell string) can change what runs.
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> SignerOfAsync(string file)
    {
        var start = new ProcessStartInfo(PowerShellExe)
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", SignerScript }
        };
        start.Environment[PathVariable] = file;
        try
        {
            var result = await ProcessRunner.RunAsync(start, ScheduledTasks.ShortTimeout);
            return result == null ? (-1, "The signature check did not finish in time.") : (result.ExitCode, result.Output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }

    internal const string PathVariable = "RUNNER_MANAGER_PATH";
    internal const string SignerScript =
        "$s = Get-AuthenticodeSignature -LiteralPath $env:RUNNER_MANAGER_PATH; if ($s.Status -eq 'Valid') { $s.SignerCertificate.Subject }";

    /// <summary>Windows PowerShell by its full path, so no powershell.exe in the current or program folder is started instead.</summary>
    internal static string PowerShellExe => WindowsTools.FullPath(@"WindowsPowerShell\v1.0\powershell.exe");

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
    /// shortcut, and switches the start-up task to the new copy. Returns the exe the fleet agent is to run, or null when
    /// it is not restarted. The caller restarts it (<see cref="MachineRelay.RestartLocalAgentAsync"/> with that exe),
    /// which switches the agent's task to the new copy only after the agent finished its work: registering the task
    /// may end the running agent, and with it a runner create it runs for another PC.
    /// </summary>
    public static async Task<string?> InstallReleaseAsync(ReleaseInfo release, Action<string> log)
    {
        var exe = await DownloadAndInstallAsync(release, log);
        await CreateStartMenuShortcutAsync();
        // Only when it needs it, and not while it runs: the dashboard installing this may have been started by it.
        await Installer.UpdateStartupTaskAsync(exe);
        if (!await MachineRelay.IsAgentInstalledAsync() || await AgentOwnership.OtherUserAsync() != null) return null;
        return exe;
    }

    /// <summary>
    /// Starts a detached helper that waits for this process to exit, then starts the task again. Launched before
    /// the task is re-registered, so the agent comes back even if re-registering ends the running instance. The helper
    /// also waits for the start helper that started this process (see <see cref="RunAfterExit"/>), and then until Task
    /// Scheduler no longer counts the task as running, since it refuses to start a running task of the fleet agent's
    /// kind; a start it refuses is tried again (see <see cref="RestartTaskScript"/>).
    /// </summary>
    public static void ScheduleAgentTaskRestart(string taskName)
        => RunAfterExit(RestartTaskScript(RestartTaskSeconds), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1), new Dictionary<string, string>
        {
            [ScheduledTasks.TaskNameVariable] = taskName,
            [SchtasksVariable] = WindowsTools.FullPath("schtasks.exe"),
            [LogVariable] = MachineRelay.AgentLogPath
        });

    internal const string SchtasksVariable = "RUNNER_MANAGER_SCHTASKS";
    internal const string LogVariable = "RUNNER_MANAGER_LOG";

    /// <summary>How long <see cref="RestartTaskScript"/> tries to start the task.</summary>
    internal const int RestartTaskSeconds = 90;

    /// <summary>
    /// PowerShell that starts the task named in <see cref="ScheduledTasks.TaskNameVariable"/> again: whenever Task
    /// Scheduler does not count it as running (TASK_STATE 2 queued, 4 running), it runs schtasks /Run
    /// (<see cref="SchtasksVariable"/>), until that succeeds, for at most <paramref name="seconds"/> seconds. A task that
    /// runs for 15 seconds in a row was started by something else meanwhile (its keep-alive trigger, or a dashboard), which
    /// counts as started too. When it was not started, a line in the fleet agent log (<see cref="LogVariable"/>, written
    /// only when it is a file of its own) says so. The task name and paths reach the script only as environment variables.
    /// </summary>
    internal static string RestartTaskScript(int seconds) =>
        "$n = $env:" + ScheduledTasks.TaskNameVariable + "; $x = $env:" + SchtasksVariable + "; $ok = $false; $since = $null; " +
        "$end = (Get-Date).AddSeconds(" + seconds + "); $s = New-Object -ComObject Schedule.Service; try { $s.Connect() } catch { }; " +
        "while ((Get-Date) -lt $end) { $st = -2; try { $st = [int]$s.GetFolder('\\').GetTask($n).State } catch { }; " +
        "if ($st -eq 2 -or $st -eq 4) { if (-not $since) { $since = Get-Date }; if (((Get-Date) - $since).TotalSeconds -ge 15) { $ok = $true; break } } " +
        "else { $since = $null; & $x /Run /TN $n 2>&1 | Out-Null; if ($LASTEXITCODE -eq 0) { $ok = $true; break } }; " +
        "Start-Sleep -Milliseconds 500 }; " +
        "$l = $env:" + LogVariable + "; if (-not $ok -and $l) { $i = Get-Item -LiteralPath $l -Force -ErrorAction SilentlyContinue; " +
        "if ($i -and -not ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not $i.PSIsContainer) { " +
        "try { [IO.File]::AppendAllText($l, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  The fleet agent did not start again after it exited to restart: " +
        "Task Scheduler did not start its task within " + seconds + " seconds. Its task starts it again within five minutes.' + [Environment]::NewLine) } catch { } } }";

    /// <summary>
    /// Starts a hidden, detached PowerShell helper that waits for this process to exit (at most <paramref name="timeout"/>,
    /// or for as long as it runs when null), waits <paramref name="delay"/> more, then runs <paramref name="script"/>.
    /// It also waits for the start helper that started this process, when one did (see
    /// <see cref="Launcher.HelperProcessId"/>): the start helper exits a moment after this process, and until then its
    /// file cannot be removed and its task counts as running. Paths go to the script in <paramref name="environment"/>,
    /// never inside its text.
    /// </summary>
    internal static void RunAfterExit(string script, TimeSpan? timeout, TimeSpan delay, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = Process.Start(AfterExitStartInfo(script, Environment.ProcessId, Launcher.HelperProcessId(), timeout, delay, environment));
    }

    /// <summary>
    /// How <see cref="RunAfterExit"/> starts PowerShell, waiting for <paramref name="processId"/> and
    /// <paramref name="helperId"/>. Its current folder is the system folder, never the one of this process: a process
    /// that started from the install folder (a task, the start helper and the Start menu shortcut all start it there)
    /// would otherwise pass that folder on, and a folder that is a running process's current folder cannot be removed.
    /// </summary>
    internal static ProcessStartInfo AfterExitStartInfo(string script, int processId, int? helperId, TimeSpan? timeout, TimeSpan delay,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(PowerShellExe)
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command",
                AfterExitScript(script, processId, timeout, delay, helperId) },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        return start;
    }

    internal static string AfterExitScript(string script, int processId, TimeSpan? timeout, TimeSpan delay, int? helperId = null)
    {
        var limit = timeout is { } wait ? $" -Timeout {(int)wait.TotalSeconds}" : string.Empty;
        var ids = helperId is { } helper && helper != processId ? $"{processId},{helper}" : $"{processId}";
        return $"Wait-Process -Id {ids}{limit} -ErrorAction SilentlyContinue; Start-Sleep -Seconds {(int)delay.TotalSeconds}; {script}";
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
        RemoveOldFiles();
        log($"Installing Runner Manager v{CurrentVersion} to {InstallDir}...");
        var files = AppFiles();
        var install = new SwapMarker(SwapKind.Install, InstalledVersion ?? string.Empty, CurrentVersion);
        await Task.Run(() => WithUpdateLock(() =>
        {
            // Going from a multi-file build to a single-file exe: clear the old app files so only the exe remains.
            // Only builds before 2.2 were multi-file, and they always used the default folder of that time, which only
            // holds this app. An update that another process prepares there (see StagingFolder) is left alone, and so
            // are the start helper and the folder it lets a self-contained exe unpack into (see Launcher).
            if (files.Count == 1 && MachineInstall.HoldsOnlyThisApp(InstallDir))
            {
                foreach (var stale in Directory.EnumerateFiles(InstallDir).Where(f => !IsKeptBySingleFileInstall(Path.GetFileName(f), isFolder: false)))
                {
                    try { File.Delete(stale); }
                    catch { try { File.Move(stale, $"{stale}.{DateTime.UtcNow.Ticks}.old{Path.GetExtension(stale)}"); } catch { } }
                }
                foreach (var folder in Directory.EnumerateDirectories(InstallDir).Where(f => !IsKeptBySingleFileInstall(Path.GetFileName(f), isFolder: true)))
                {
                    try { Directory.Delete(folder, recursive: true); } catch { }
                }
            }
            foreach (var (source, relative) in files)
            {
                var destination = Path.Combine(InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Files of a running agent can't be overwritten but can be renamed out of the way. The exe it replaces is
                // kept, so setup can put it back when a later step fails.
                var isExe = relative.Equals(AssetName, StringComparison.OrdinalIgnoreCase);
                ReplaceFile(source, destination, keepPrevious: isExe, isExe ? install : null);
            }
        }));
        _installedThisBuild = true;
        // Before any task is registered for the new copy (setup registers them next), so they start it through the start helper.
        await EnsureStartHelperAsync(log);
        if (createShortcut) await CreateStartMenuShortcutAsync();
        return InstalledExe;
    }

    /// <summary>
    /// Writes the start helper into the install folder after this process installed its build there (see
    /// <see cref="Launcher.EnsureAsync"/>); only with administrator rights, which setup always has. A PC where it cannot
    /// be kept runs without it, as before.
    /// </summary>
    internal static async Task EnsureStartHelperAsync(Action<string> log)
    {
        if (!Elevation.IsElevated) return;
        try
        {
            if (!await Launcher.EnsureAsync(InstallDir, afterInstall: true, MachineRelay.AppendAgentLog) && Launcher.RunsWithout(InstallDir))
                log("Runner Manager could not keep its start helper in the install folder, so it starts without it. Details are in the fleet agent log.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MachineRelay.AppendAgentLog($"Could not write the start helper: {ex.Message}");
        }
    }

    /// <summary>
    /// What setup keeps when it clears the default install folder for a single-file exe: RunnerManager.exe and the files
    /// beside it named after it, the start helper and its files, files renamed out of the way (.old.), update folders, and
    /// the folder that a self-contained exe unpacks into through the start helper.
    /// </summary>
    internal static bool IsKeptBySingleFileInstall(string name, bool isFolder)
        => isFolder
            ? IsStagingFolderName(name) || name.Equals(Launcher.ExtractionFolderName, StringComparison.OrdinalIgnoreCase)
            : name.StartsWith(AssetName, StringComparison.OrdinalIgnoreCase) || name.StartsWith(Launcher.FileName, StringComparison.OrdinalIgnoreCase)
              || name.Contains(".old.", StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// What a swap of the installed exe does, so a repair after a cut-off swap knows how to finish it: an update (watched by
/// <see cref="UpdateGuard"/>) or an install by setup goes forward, a rollback or setup's undo goes back.
/// </summary>
public enum SwapKind { Unknown, Update, Install, Rollback, Undo }

/// <summary>A swap that is under way: its kind, the version it replaces and the version it puts in place.</summary>
public sealed record SwapMarker(SwapKind Kind, string From, string To);

public enum RepairSource { None, New, Previous, Unexplained }

/// <summary>What <see cref="AppUpdater.RepairMissingExe()"/> did: put a file in place, found the exe missing without a swap, or nothing.</summary>
public sealed record ExeRepair(RepairSource Source, SwapMarker? Marker);

/// <summary>The result of an update check: the newest release, or why it could not be checked.</summary>
public sealed record ReleaseCheck(ReleaseInfo? Release, string Problem)
{
    public bool UpdateAvailable => Release != null && AppUpdater.IsNewer(Release.Version, AppUpdater.CurrentVersion);
}
