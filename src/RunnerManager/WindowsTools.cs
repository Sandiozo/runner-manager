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
}
