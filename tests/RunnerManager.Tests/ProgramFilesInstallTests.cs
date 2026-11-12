using System.Diagnostics;
using System.IO;

namespace RunnerManager.Tests;

/// <summary>
/// Program Files as the default for new installs: where the install folder comes from, where updates are prepared, and
/// how several Windows users share one install. Nothing here touches the registry, Program Files or the real install.
/// </summary>
public class ProgramFilesInstallTests
{
    private const string ProgramFiles = @"C:\Program Files\RunnerManager";
    private const string OldDefault = @"C:\Users\me\AppData\Local\Programs\RunnerManager";
    private const string Chosen = @"D:\Apps\RunnerManager";
    private const string Profiles = @"C:\Users";
    private const string MySid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";

    private static InstallFacts Facts(string runningExe, string? ownRecord = null, string? userEntry = null, bool elevated = true,
        Func<string, bool>? hasExe = null)
        => new(ownRecord, userEntry, runningExe, elevated, ProgramFiles, OldDefault, hasExe ?? (_ => false));

    private static string ExeIn(string folder) => Path.Combine(folder, AppPaths.ExeName);

    // ================= 1. New installs go to Program Files =================

    [Fact]
    public void NewInstalls_DefaultToThe64BitProgramFilesFolder()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RunnerManager");
        Assert.Equal(expected, MachineInstall.ProgramFilesDir);
        Assert.Equal(MachineInstall.ProgramFilesDir, AppUpdater.DefaultInstallDir);
        Assert.DoesNotContain("(x86)", AppUpdater.DefaultInstallDir);
        // Set by Windows for 32-bit and 64-bit processes alike: the 64-bit Program Files.
        if (Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } programFiles64)
            Assert.True(AppUpdater.SameFolder(Path.Combine(programFiles64, "RunnerManager"), AppUpdater.DefaultInstallDir));
    }

    [Fact]
    public void TheOldDefault_IsTheProfileFolderOfEarlierVersions_AndOnlyHoldsThisApp()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RunnerManager");
        Assert.Equal(old, MachineInstall.OldDefaultDir);
        Assert.True(MachineInstall.IsOldDefaultDir(old + Path.DirectorySeparatorChar));
        Assert.False(AppUpdater.IsDefaultInstallDir(old));
        // Old files of any kind are cleaned up in both folders that hold only Runner Manager, never in a chosen folder.
        Assert.True(MachineInstall.HoldsOnlyThisApp(old));
        Assert.True(MachineInstall.HoldsOnlyThisApp(MachineInstall.ProgramFilesDir));
        Assert.False(MachineInstall.HoldsOnlyThisApp(Chosen));
        // Versions before 2.2 (RunnerWidget) always installed there.
        Assert.Equal(Path.Combine(old, AppPaths.LegacyExeName), LegacyInstall.LegacyExe);
    }

    [Fact]
    public void Setup_OffersProgramFiles_OnAPcWithoutAnInstall()
        => Assert.Equal(ProgramFiles, MachineInstall.SetupProposal(null, null, Array.Empty<InstallRecord>(), Profiles, ProgramFiles, OldDefault, _ => false));

    [Fact]
    public void Setup_RunAgain_OffersTheFolderTheInstallIsIn()
    {
        var none = Array.Empty<InstallRecord>();
        // Recorded for this user, then the Settings → Apps entry, then the default of earlier versions when it has the exe.
        Assert.Equal(Chosen, MachineInstall.SetupProposal(Chosen, OldDefault, none, Profiles, ProgramFiles, OldDefault, _ => true));
        Assert.Equal(Chosen, MachineInstall.SetupProposal(null, Chosen, none, Profiles, ProgramFiles, OldDefault, _ => true));
        Assert.Equal(OldDefault, MachineInstall.SetupProposal(null, null, none, Profiles, ProgramFiles, OldDefault, f => f == OldDefault));
        // An empty old default (for example after an uninstall that kept other files) is no install.
        Assert.Equal(ProgramFiles, MachineInstall.SetupProposal(null, null, none, Profiles, ProgramFiles, OldDefault, _ => false));
    }

    [Fact]
    public void Setup_FolderButton_GoesBackToTheOfferedFolder_AndOffersNoMove()
    {
        // A new install: no button until the folder is changed, and then it is "Default".
        Assert.Equal((false, "Default"), SetupWindow.FolderResetButton(ProgramFiles, ProgramFiles, ProgramFiles));
        Assert.Equal((true, "Default"), SetupWindow.FolderResetButton(Chosen, ProgramFiles, ProgramFiles));
        // An install in the old default: setup shows it where it is, with no button that would move it.
        Assert.Equal((false, "Current folder"), SetupWindow.FolderResetButton(OldDefault, OldDefault, ProgramFiles));
        Assert.Equal((true, "Current folder"), SetupWindow.FolderResetButton(ProgramFiles, OldDefault, ProgramFiles));
    }

    [Fact]
    public void Setup_FolderNote_OnlyForAFolderTheUserChoseOutsideProgramFiles()
    {
        Assert.Null(SetupWindow.FolderNote(ProgramFiles, ProgramFiles, ProgramFiles));
        Assert.Null(SetupWindow.FolderNote(OldDefault, OldDefault, ProgramFiles));
        Assert.Null(SetupWindow.FolderNote(ProgramFiles, OldDefault, ProgramFiles));
        var note = SetupWindow.FolderNote(Chosen, ProgramFiles, ProgramFiles);
        Assert.NotNull(note);
        Assert.Contains("only administrators", note);
        Assert.DoesNotContain("n't", note);
    }

    [Fact]
    public void Setup_ExistingInstallText_SaysWhatSetupDoes()
    {
        Assert.Null(SetupWindow.ExistingInstallText(ownInstall: false, installedVersion: null, "2.9.1", sharedWithOthers: false));
        Assert.Contains("Continue to update it", SetupWindow.ExistingInstallText(true, "2.9.0", "2.9.1", false));
        Assert.Contains("Continue only to go back", SetupWindow.ExistingInstallText(true, "3.0.0", "2.9.1", false));
        // Another Windows user runs the newer copy: setup keeps it.
        var shared = SetupWindow.ExistingInstallText(true, "3.0.0", "2.9.1", true)!;
        Assert.Contains("setup keeps v3.0.0", shared);
        Assert.DoesNotContain("go back", shared);
        // A second Windows user: the copy that is there is used.
        Assert.Contains("update it to v2.9.1", SetupWindow.ExistingInstallText(false, "2.9.0", "2.9.1", true));
        Assert.Contains("use it for your Windows user too", SetupWindow.ExistingInstallText(false, "2.9.1", "2.9.1", true));
        Assert.Contains("setup keeps v3.0.0", SetupWindow.ExistingInstallText(false, "3.0.0", "2.9.1", true));
        foreach (var text in new[] { shared, SetupWindow.ExistingInstallText(false, "3.0.0", "2.9.1", false)! })
            Assert.DoesNotContain("n't", text);
    }

    [Fact]
    public void AnInstallInTheOldDefault_StaysWhereItIs()
    {
        // Running from there, elevated, with or without a record: that is the install, never Program Files.
        var running = ExeIn(OldDefault);
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running, userEntry: OldDefault)));
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running)));
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running, ownRecord: OldDefault)));
        Assert.Equal(OldDefault, MachineInstall.FolderToRecord(Facts(running, userEntry: OldDefault)));
        // Uninstall there removes Runner Manager's own files only, never recursively: the folder is in the user's profile.
        var removal = Installer.RemoveProgramFilesScript(OldDefault);
        Assert.DoesNotContain("-Recurse", removal);
        Assert.Contains("'RunnerManager.exe'", removal);
    }

    // ================= 2. The record that administrators trust =================

    [Fact]
    public void Elevated_UsesItsRecord_WhateverSettingsAppsSays()
    {
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(ExeIn(ProgramFiles), ownRecord: ProgramFiles, userEntry: @"C:\Users\me\Evil")));
        // Also a copy that runs from somewhere else, such as a build folder.
        Assert.Equal(Chosen, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", ownRecord: Chosen, userEntry: @"C:\Users\me\Evil")));
    }

    [Fact]
    public void Elevated_NeverTakesTheFolderFromAChangedSettingsAppsEntry()
    {
        const string evil = @"C:\Users\me\Evil";
        // The installed copy runs from the old default; the entry was changed to point elsewhere.
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(ExeIn(OldDefault), userEntry: evil, hasExe: _ => true)));
        Assert.Null(MachineInstall.FolderToRecord(Facts(ExeIn(Chosen), userEntry: evil)));
        // A copy that is not installed (a build folder), and an exe in the folder the entry names.
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", userEntry: evil, hasExe: f => f == evil)));
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", userEntry: evil,
            hasExe: f => f == evil || f == OldDefault)));
    }

    [Fact]
    public void Elevated_CopyInAChosenFolder_RecognisesItsOwnFolder()
    {
        // An install of 2.9.0 in a folder chosen in setup: no record yet, and the Settings → Apps entry names the folder
        // (here in other letter case and with a trailing backslash). A copy left in the old default must not win.
        var facts = Facts(ExeIn(Chosen), userEntry: @"d:\apps\runnermanager\", hasExe: _ => true);
        Assert.Equal(Chosen, MachineInstall.Resolve(facts));
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(facts));
        // Once recorded, it stays, also when the entry is changed later.
        Assert.Equal(Chosen, MachineInstall.Resolve(Facts(ExeIn(Chosen), ownRecord: Chosen, userEntry: @"C:\Users\me\Evil", hasExe: _ => true)));
        Assert.Null(MachineInstall.FolderToRecord(Facts(ExeIn(Chosen), ownRecord: Chosen, userEntry: Chosen)));
    }

    [Fact]
    public void Elevated_CopyInAChosenFolder_IsRecognisedByItsTasks_WhenSettingsAppsWasChanged()
    {
        // The Settings → Apps entry can be changed without administrator rights; this user's scheduled tasks cannot.
        var changed = Facts(ExeIn(Chosen), userEntry: @"C:\Users\me\Evil");
        Assert.Null(MachineInstall.FolderToRecord(changed));
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(changed with { OwnTaskExes = [@"d:\apps\runnermanager\RunnerManager.exe"] }));
        Assert.Equal(Chosen, MachineInstall.Resolve(changed with { OwnTaskExes = [ExeIn(Chosen)] }));
        // Tasks that start another exe, or a name without a folder, do not make this copy the install.
        Assert.Null(MachineInstall.FolderToRecord(changed with { OwnTaskExes = [ExeIn(ProgramFiles), AppPaths.ExeName] }));
        Assert.Null(MachineInstall.FolderToRecord(Facts(@"D:\src\bin\Release\RunnerManager.exe") with { OwnTaskExes = [ExeIn(Chosen)] }));
    }

    [Fact]
    public void NotElevated_FollowsSettingsApps()
    {
        // An AI tool's host runs without administrator rights; it changes nothing on its own.
        Assert.Equal(Chosen, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", userEntry: Chosen, elevated: false)));
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", ownRecord: ProgramFiles, userEntry: Chosen, elevated: false)));
    }

    [Fact]
    public void ARecordIsWritten_OnlyForAnElevatedInstalledCopyWithoutOne()
    {
        Assert.Equal(ProgramFiles, MachineInstall.FolderToRecord(Facts(ExeIn(ProgramFiles))));
        Assert.Null(MachineInstall.FolderToRecord(Facts(ExeIn(ProgramFiles), elevated: false)));
        Assert.Null(MachineInstall.FolderToRecord(Facts(ExeIn(ProgramFiles), ownRecord: ProgramFiles)));
        // A downloaded setup and a build folder are no install.
        Assert.Null(MachineInstall.FolderToRecord(Facts(@"C:\Users\me\Downloads\RunnerManagerSetup.exe", userEntry: @"C:\Users\me\Downloads")));
        Assert.Null(MachineInstall.FolderToRecord(Facts(@"D:\src\bin\Release\RunnerManager.exe")));
        Assert.Null(MachineInstall.FolderToRecord(Facts("RunnerManager.exe")));
    }

    [Fact]
    public void Records_HoldOneFolderPerWindowsUser()
    {
        var store = new MemoryInstallRecords();
        store.Write(MySid, OldDefault);
        store.Write(OtherSid, ProgramFiles);
        store.Write(MySid, ProgramFiles);
        Assert.Equal(ProgramFiles, MachineInstall.RecordOf(store.ReadAll(), MySid));
        Assert.Equal(ProgramFiles, MachineInstall.RecordOf(store.ReadAll(), OtherSid.ToLowerInvariant()));
        store.Write(MySid, null);
        Assert.Null(MachineInstall.RecordOf(store.ReadAll(), MySid));
        Assert.Single(store.ReadAll());
        // Where only administrators can change it, beside the AI assistant switches.
        Assert.Equal(@"SOFTWARE\RunnerManager\Installs", RegistryInstallRecordStore.KeyPath);
    }

    [Fact]
    public void Tests_NeverUseTheRealRecords()
        => Assert.IsType<MemoryInstallRecords>(MachineInstall.Records);

    // ================= 3. Where an update is prepared =================

    [Fact]
    public void Updates_ArePreparedInsideTheInstallFolder_NotInTheUsersDataFolder()
    {
        var staging = AppUpdater.StagingFolder(ProgramFiles, 4242);
        Assert.Equal(Path.Combine(ProgramFiles, "update-4242"), staging);
        Assert.True(MachineInstall.IsInside(staging, ProgramFiles));
        Assert.False(MachineInstall.IsInside(AppUpdater.StagingFolder(MachineInstall.ProgramFilesDir, 1), FleetConfig.StateDir));
        Assert.True(AppUpdater.IsStagingFolderName(Path.GetFileName(staging)));
        // The clean-up after a multi-file build leaves another process's update alone, and only that.
        Assert.False(AppUpdater.IsStagingFolderName("runtimes"));
        Assert.False(AppUpdater.IsStagingFolderName("update-x"));
        Assert.False(AppUpdater.IsStagingFolderName("update-1.old"));
    }

    [Fact]
    public async Task SignatureCheck_TakesThePathFromTheEnvironment()
    {
        Assert.DoesNotContain("'", AppUpdater.SignerScript.Replace("'Valid'", string.Empty));
        Assert.Contains("$env:" + AppUpdater.PathVariable, AppUpdater.SignerScript);
        var root = Path.Combine(Path.GetTempPath(), $"rm-signer-{Guid.NewGuid():N}");
        // Each of ’ ‘ ‚ ‛ ends a PowerShell string as ' does; a script built from this name would write the marker.
        var (name, marker) = InjectingName("a’b‘c‚d‛e");
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "windowsdesktop-runtime.exe");
            File.WriteAllText(file, "not signed");
            var (exitCode, output) = await AppUpdater.SignerOfAsync(file);
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, output.Trim());
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(marker);
        }
    }

    [Fact]
    public void PowerShell_IsStartedByItsFullPath()
    {
        Assert.True(Path.IsPathFullyQualified(AppUpdater.PowerShellExe));
        Assert.True(File.Exists(AppUpdater.PowerShellExe));
    }

    // ================= 4. Several Windows users =================

    [Fact]
    public void ASecondUser_GetsTheInstallThatIsThere()
    {
        var others = new[] { new InstallRecord(OtherSid, ProgramFiles) };
        Assert.Equal(ProgramFiles, MachineInstall.SetupProposal(null, null, others, Profiles, ProgramFiles, OldDefault, f => f == ProgramFiles));
        // A folder another user chose outside the profiles is shared as well.
        var chosen = new[] { new InstallRecord(OtherSid, Chosen) };
        Assert.Equal(Chosen, MachineInstall.SetupProposal(null, null, chosen, Profiles, ProgramFiles, OldDefault, f => f == Chosen));
        // Another user's copy in their own profile is theirs: this user gets Program Files.
        var theirs = new[] { new InstallRecord(OtherSid, @"C:\Users\them\AppData\Local\Programs\RunnerManager") };
        Assert.Equal(ProgramFiles, MachineInstall.SetupProposal(null, null, theirs, Profiles, ProgramFiles, OldDefault, f => f == theirs[0].Folder));
        // A record whose folder has no exe is no install.
        Assert.Equal(ProgramFiles, MachineInstall.SetupProposal(null, null, chosen, Profiles, ProgramFiles, OldDefault, _ => false));
        // This user's own install still comes first.
        Assert.Equal(OldDefault, MachineInstall.SetupProposal(null, null, others, Profiles, ProgramFiles, OldDefault, _ => true));
    }

    [Fact]
    public void SharedInstall_PrefersProgramFiles()
    {
        var records = new[] { new InstallRecord("S-1-5-21-9", Chosen), new InstallRecord(OtherSid, ProgramFiles + @"\") };
        Assert.Equal(ProgramFiles, MachineInstall.SharedInstall(records, Profiles, ProgramFiles, _ => true));
        Assert.Null(MachineInstall.SharedInstall(Array.Empty<InstallRecord>(), Profiles, ProgramFiles, _ => true));
    }

    [Theory]
    [InlineData(@"C:\Users\them\AppData\Local\Programs\RunnerManager", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\UsersData\RunnerManager", true)]
    [InlineData(@"D:\RunnerManager", true)]
    [InlineData(@"C:\Program Files\RunnerManager", true)]
    public void IsShareable_LeavesUserProfilesOut(string folder, bool shareable)
        => Assert.Equal(shareable, MachineInstall.IsShareable(folder, Profiles));

    [Fact]
    public void IsShareable_WithoutAKnownProfilesFolder_SharesNothing()
        => Assert.False(MachineInstall.IsShareable(ProgramFiles, null));

    [Fact]
    public void OtherUsersOf_FindsTheirRecordsAndTheirTasks()
    {
        var records = new[]
        {
            new InstallRecord(MySid, ProgramFiles),
            new InstallRecord(OtherSid, ProgramFiles + @"\"),
            new InstallRecord("S-1-5-21-1-2-3-1003", Chosen),
            new InstallRecord("S-1-5-21-1-2-3-1004", "not a path")
        };
        var tasks = new[]
        {
            new TaskUse(@"PC\me", IsOwn: true, ExeIn(ProgramFiles)),
            new TaskUse(@"PC\carol", IsOwn: false, ExeIn(@"c:\program files\runnermanager")),
            new TaskUse(@"PC\dave", IsOwn: false, ExeIn(OldDefault)),
            new TaskUse(@"PC\erin", IsOwn: false, "RunnerManager.exe")
        };
        var users = MachineInstall.OtherUsersOf(ProgramFiles, MySid, records, tasks, sid => sid == OtherSid ? @"PC\bob" : sid);
        Assert.Equal(new[] { @"PC\bob", @"PC\carol" }, users);
        // The same user found twice is named once.
        var twice = MachineInstall.OtherUsersOf(ProgramFiles, MySid, records, tasks.Append(new TaskUse(OtherSid, false, ExeIn(ProgramFiles))),
            sid => sid == OtherSid ? @"PC\bob" : sid);
        Assert.Equal(2, twice.Count);
        // Nobody else uses this user's own folder in the old default.
        Assert.Empty(MachineInstall.OtherUsersOf(@"C:\Users\me\Other", MySid, records, tasks, sid => sid));
    }

    [Fact]
    public void Setup_NeverPutsAnOlderVersionOverOneThatOthersUse()
    {
        Assert.True(Installer.KeepsInstalledCopy(shared: true, installedVersion: "3.0.0", setupVersion: "2.9.1"));
        Assert.False(Installer.KeepsInstalledCopy(shared: true, installedVersion: "2.9.1", setupVersion: "2.9.1"));
        Assert.False(Installer.KeepsInstalledCopy(shared: true, installedVersion: "2.9.0", setupVersion: "2.9.1"));
        Assert.False(Installer.KeepsInstalledCopy(shared: true, installedVersion: null, setupVersion: "2.9.1"));
        // This user's own copy: setup warns on its first page, and going back is the user's choice.
        Assert.False(Installer.KeepsInstalledCopy(shared: false, installedVersion: "3.0.0", setupVersion: "2.9.1"));
    }

    [Fact]
    public void OwnInstall_IsThisUsersInstallOnly()
    {
        Assert.Equal(Chosen, Installer.OwnInstall(Chosen, OldDefault, OldDefault, _ => true));
        Assert.Equal(Chosen, Installer.OwnInstall(null, Chosen, OldDefault, _ => true));
        Assert.Equal(OldDefault, Installer.OwnInstall(null, null, OldDefault, _ => true));
        // A copy in Program Files that only another user installed is not this user's to move or remove.
        Assert.Null(Installer.OwnInstall(null, null, OldDefault, f => f == ProgramFiles));
    }

    [Fact]
    public void SetupAndUninstall_LeaveOtherSessionsAlone()
    {
        const string dashboard = "\"C:\\Program Files\\RunnerManager\\RunnerManager.exe\" --minimized";
        Assert.True(Installer.IsClosedBySetup(dashboard, endAssistants: true, inThisSession: true));
        Assert.False(Installer.IsClosedBySetup(dashboard, endAssistants: true, inThisSession: false));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Program Files\\RunnerManager\\RunnerManager.exe\" --mcp", endAssistants: true, inThisSession: false));
    }

    [Theory]
    [InlineData("Runner Manager (bob)", true)]
    [InlineData("Runner Manager Startup (bob)", true)]
    [InlineData("Runner Manager Fleet Agent", true)]
    [InlineData("RunnerWidget Dashboard", true)]
    [InlineData("OneDrive Standalone Update Task", false)]
    public void TheTasksOfOtherUsers_AreFoundByName(string name, bool found)
        => Assert.Equal(found, MachineInstall.IsRunnerManagerTaskName(name));

    // ================= Helper scripts get paths from the environment =================

    [Fact]
    public void RemovalScript_TakesTheFolderFromTheEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-quotes-{Guid.NewGuid():N}");
        var (name, marker) = InjectingName("x’");
        var folder = Path.Combine(root, name, "RunnerManager");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, AppPaths.ExeName), string.Empty);
            var script = Installer.RemoveProgramFilesScript(folder);
            Assert.DoesNotContain(root, script);
            InstallFolderTests.RunPowerShell(script, Installer.FolderEnvironment(folder));
            Assert.False(Directory.Exists(folder));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(marker);
        }
    }

    /// <summary>
    /// A folder name that, put into a script between quotes, would end the string and write a marker file in the current
    /// folder (a file name can hold neither : nor \). Returns the name and where the marker would be.
    /// </summary>
    private static (string Name, string Marker) InjectingName(string start)
    {
        var marker = $"rm-injected-{Guid.NewGuid():N}.txt";
        return ($"{start}'; Set-Content -LiteralPath {marker} -Value x; ‘", Path.Combine(Environment.CurrentDirectory, marker));
    }

    [Fact]
    public void RemovalScript_WithoutAFolder_RemovesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-nofolder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, AppPaths.ExeName), string.Empty);
            var start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", Installer.RemoveProgramFilesScript(root) },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            };
            start.Environment.Remove(Installer.FolderVariable);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(30_000));
            Assert.True(File.Exists(Path.Combine(root, AppPaths.ExeName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DataScript_TakesTheFolderFromTheEnvironment_AndCanKeepTheStartupScripts()
    {
        Assert.Contains("$env:" + Installer.DataVariable, Installer.RemoveDataScript(keepDisabledStartup: false));
        var root = Path.Combine(Path.GetTempPath(), $"rm-data-{Guid.NewGuid():N}");
        var data = Path.Combine(root, "O’Brien", "RunnerManager");
        Directory.CreateDirectory(Path.Combine(data, "disabled-startup"));
        try
        {
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(data, "disabled-startup", "start.cmd"), "run.cmd");
            var environment = new Dictionary<string, string> { [Installer.DataVariable] = data };

            InstallFolderTests.RunPowerShell(Installer.RemoveDataScript(keepDisabledStartup: true), environment);
            Assert.False(File.Exists(Path.Combine(data, "settings.json")));
            Assert.True(File.Exists(Path.Combine(data, "disabled-startup", "start.cmd")));

            InstallFolderTests.RunPowerShell(Installer.RemoveDataScript(keepDisabledStartup: false), environment);
            Assert.False(Directory.Exists(data));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Shortcuts_WorkWithQuotesInTheirPaths()
    {
        Assert.DoesNotContain(@":\", Installer.ShortcutScript);
        var root = Path.Combine(Path.GetTempPath(), $"rm-shortcut-{Guid.NewGuid():N}");
        var shortcut = Path.Combine(root, "O’Brien's ‘Start’ menu", "Runner Manager.lnk");
        var target = Path.Combine(root, "Program ’Files‘", "RunnerManager", AppPaths.ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.WriteAllText(target, string.Empty);
            await Installer.CreateShortcutAsync(shortcut, target);
            Assert.True(File.Exists(shortcut));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ================= 5. What names the folder =================

    [Fact]
    public void AiToolSetups_KeepAProgramFilesPathInOnePiece()
    {
        var exe = ExeIn(ProgramFiles);
        foreach (var client in AssistantSetup.Clients.Where(c => !c.NeedsHttp))
        {
            var snippet = client.Snippet(exe, "token");
            // Quoted for a shell, or written as a JSON or TOML string: the space in "Program Files" stays inside it.
            Assert.True(snippet.Contains($"\"{exe}\"") || snippet.Contains($"\"{exe.Replace(@"\", @"\\")}\"") || snippet.Contains($"'{exe}'"),
                $"{client.Name}: {snippet}");
        }
    }
}
