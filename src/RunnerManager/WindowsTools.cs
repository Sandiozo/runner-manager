using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace RunnerManager;

/// <summary>
/// Windows tools by their full path, the way PowerShell is started (<see cref="AppUpdater.PowerShellExe"/>). Started by
/// its bare name, a tool is looked for in the app's folder and the current folder before the system folder, and then on
/// PATH, so a program of the same name in one of them would run instead, with the app's rights, which are often an
/// administrator's.
/// </summary>
internal static class WindowsTools
{
    /// <summary>A tool in the system folder (System32), such as sc.exe, schtasks.exe or cmd.exe.</summary>
    public static string FullPath(string name) => System.IO.Path.Combine(Environment.SystemDirectory, name);

    /// <summary>Explorer, which is in the Windows folder, not in the system folder.</summary>
    public static string Explorer => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Starts Explorer for <see cref="OpenUrl"/> and <see cref="OpenFolder"/> (Process.Start). Tests pass their own.</summary>
    internal static Func<ProcessStartInfo, Process?> StartExplorer { get; set; } = Process.Start;

    /// <summary>
    /// Opens a web page in the default browser: only an https address, given to Explorer by its full path as its only
    /// argument (see <see cref="ExplorerStartInfo"/>). Explorer hands it to the Windows shell of the signed-in user, so the
    /// browser does not get Runner Manager's administrator rights. The address is passed as Windows' URI parser writes
    /// it, where quotes and spaces are percent-encoded; an address with a comma is not opened, because Explorer reads a
    /// comma as the end of an argument. Returns null when Explorer was started, otherwise what went wrong, for the user.
    /// </summary>
    internal static string? OpenUrl(string? url)
    {
        if (SafeUrl(url) is not { } address) return $"Runner Manager did not open {url}: it opens only https addresses without a comma.";
        var start = ExplorerStartInfo();
        start.ArgumentList.Add(address);
        return Start(start, address);
    }

    /// <summary>The https address to open for <paramref name="url"/> (see <see cref="OpenUrl"/>), or null when it is not opened.</summary>
    internal static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        var address = uri.AbsoluteUri;
        return address.Contains(',') || address.Contains('"') || address.Any(char.IsWhiteSpace) ? null : address;
    }

    /// <summary>
    /// Opens a folder in a File Explorer window: only an existing folder given by its full path, passed to Explorer by its
    /// full path as its only argument, in quotes (which no Windows path can contain), without a backslash at the end that
    /// would end up before the closing quote. Explorer hands it to the Windows shell of the signed-in user, so the window
    /// does not get Runner Manager's administrator rights. Returns null when Explorer was started, otherwise what went
    /// wrong, for the user.
    /// </summary>
    internal static string? OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            return $"Folder not found: {folder}";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return $"Folder not found: {folder}";
        }
        var start = ExplorerStartInfo();
        start.Arguments = $"\"{full}\"";
        return Start(start, full);
    }

    /// <summary>
    /// How Explorer is started to open something: by its full path, without the shell, in the system folder (not the
    /// install folder, which a window that stays open would keep in use). The caller adds the one argument.
    /// </summary>
    internal static ProcessStartInfo ExplorerStartInfo()
        => new(Explorer) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };

    private static string? Start(ProcessStartInfo start, string target)
    {
        try
        {
            using var explorer = StartExplorer(start);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"Could not open {target}: {ex.Message}";
        }
    }
}
