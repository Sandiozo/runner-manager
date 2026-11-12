using System.Diagnostics;
using System.IO;
using System.Security.Principal;

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

    private static SetupOffer Offer(string? ownRecord, string? userEntry, Func<string, bool> fileExists, IEnumerable<InstallRecord>? others = null)
        => MachineInstall.SetupProposal(ownRecord, userEntry, others ?? Array.Empty<InstallRecord>(), Profiles, ProgramFiles, OldDefault, fileExists);

    [Fact]
    public void Setup_OffersProgramFiles_OnAPcWithoutAnInstall()
        => Assert.Equal(new SetupOffer(ProgramFiles, Unconfirmed: false), Offer(null, null, _ => false));

    [Fact]
    public void Setup_RunAgain_OffersTheFolderTheInstallIsIn()
    {
        // Recorded for this user (also from this user's own tasks, before setup opens): confirmed.
        Assert.Equal(new SetupOffer(Chosen, Unconfirmed: false), Offer(Chosen, OldDefault, _ => true));
        // An empty old default (for example after an uninstall that kept other files) is no install.
        Assert.Equal(new SetupOffer(ProgramFiles, Unconfirmed: false), Offer(null, null, _ => false));
    }

    [Fact]
    public void Setup_OffersTheSettingsAppsEntryAndTheOldDefault_OnlyAsUnconfirmed()
    {
        // Any program of the user can set the entry's InstallLocation, or put a RunnerManager.exe in the old default, before
        // the first setup. Setup still offers the folder, so that an install without a record is not moved by surprise,
        // but says that it could not confirm it.
        Assert.Equal(new SetupOffer(Chosen, Unconfirmed: true), Offer(null, Chosen, _ => true));
        Assert.Equal(new SetupOffer(OldDefault, Unconfirmed: true), Offer(null, null, f => f == ExeIn(OldDefault)));
        var note = SetupWindow.FolderNote(OldDefault, OldDefault, ProgramFiles, proposedUnconfirmed: true);
        Assert.NotNull(note);
        Assert.StartsWith("Setup could not confirm that Runner Manager is installed in this folder.", note);
        Assert.Contains("only administrators", note);
    }

    [Fact]
    public void Setup_FindsARunnerWidgetInstallInTheOldDefault()
    {
        // Versions before 2.2 installed RunnerWidget.exe there; setup updates it where it is instead of moving it.
        var offer = Offer(null, null, f => f == Path.Combine(OldDefault, AppPaths.LegacyExeName));
        Assert.Equal(OldDefault, offer.Folder);
        Assert.True(offer.Unconfirmed);
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
    public void Setup_FolderNote_ForAChosenFolderOutsideProgramFiles_AndForAnUnconfirmedInstall()
    {
        // Program Files, and a confirmed install where it is: no note.
        Assert.Null(SetupWindow.FolderNote(ProgramFiles, ProgramFiles, ProgramFiles, proposedUnconfirmed: false));
        Assert.Null(SetupWindow.FolderNote(ProgramFiles, OldDefault, ProgramFiles, proposedUnconfirmed: true));
        Assert.Null(SetupWindow.FolderNote(OldDefault, OldDefault, ProgramFiles, proposedUnconfirmed: false));
        // A folder the user chose outside Program Files.
        var note = SetupWindow.FolderNote(Chosen, ProgramFiles, ProgramFiles, proposedUnconfirmed: false);
        Assert.NotNull(note);
        Assert.Contains("only administrators", note);
        Assert.DoesNotContain("could not confirm", note);
        // The offered folder when setup could not confirm the install in it.
        Assert.Contains("could not confirm", SetupWindow.FolderNote(Chosen, Chosen, ProgramFiles, proposedUnconfirmed: true));
        foreach (var text in new[] { note, SetupWindow.FolderNote(Chosen, Chosen, ProgramFiles, proposedUnconfirmed: true)! })
            Assert.DoesNotContain("n't", text);
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
        // A copy that is neither recorded for this user nor used by another is replaced, never "used too".
        var neither = SetupWindow.ExistingInstallText(false, "2.9.1", "2.9.1", false)!;
        Assert.Contains("replace it with v2.9.1", neither);
        Assert.DoesNotContain("too", neither);
        Assert.Contains("go back", SetupWindow.ExistingInstallText(false, "3.0.0", "2.9.1", false));
        foreach (var text in new[] { shared, neither, SetupWindow.ExistingInstallText(false, "3.0.0", "2.9.1", false)! })
            Assert.DoesNotContain("n't", text);
    }

    [Fact]
    public void Setup_SaysAgainWhatItDoes_ForEachFolderThatIsChosen()
    {
        // The first-page text follows the chosen folder and is decided as the install decides it.
        var source = File.ReadAllText(Path.Combine(InstallEvidenceTests.SourceDir, "SetupWindow.xaml.cs"));
        var show = source[source.IndexOf("private void ShowInstallFolder()", StringComparison.Ordinal)..];
        Assert.Contains("ShowExistingInstallAsync()", show[..show.IndexOf("InstallPathText", StringComparison.Ordinal)]);
        var existing = source[source.IndexOf("private async Task ShowExistingInstallAsync()", StringComparison.Ordinal)..];
        existing = existing[..existing.IndexOf("internal static string? ExistingInstallText", StringComparison.Ordinal)];
        Assert.Contains("MachineInstall.OtherUsersOfAsync(folder)", existing);
        Assert.Contains("Installer.OwnInstall(MachineInstall.ReadOwnRecord())", existing);
        Assert.DoesNotContain("IsInstalled", existing);
    }

    [Fact]
    public void AnInstallInTheOldDefault_StaysWhereItIs()
    {
        // Running from there, elevated, with or without a record: that is the install, never Program Files.
        var running = ExeIn(OldDefault);
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running, userEntry: OldDefault)));
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running)));
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(running, ownRecord: OldDefault)));
        // Recorded once, from the tasks that start it.
        Assert.Equal(OldDefault, MachineInstall.FolderToRecord(running, [running], _ => true));
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
        // A copy that is not installed (a build folder), and an exe in the folder the entry names.
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", userEntry: evil, hasExe: f => f == evil)));
    }

    [Fact]
    public void Elevated_NeverTakesAFileInTheOldDefault_ForTheInstall()
    {
        // Any program of the user can put a RunnerManager.exe there. A copy that is not installed and has no record (its
        // tasks would have given one, see RecordRunningInstallAsync) uses Program Files, so a newer file planted there never
        // becomes "the installed copy" that the fleet agent's task starts.
        const string build = @"D:\src\bin\Release\RunnerManager.exe";
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(build, hasExe: f => f == OldDefault)));
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(build, userEntry: @"C:\Users\me\Evil", hasExe: _ => true)));
        // Without administrator rights (an AI tool's host) it changes nothing, so the old default still counts there.
        Assert.Equal(OldDefault, MachineInstall.Resolve(Facts(build, elevated: false, hasExe: f => f == OldDefault)));
    }

    [Fact]
    public void Elevated_CopyInAChosenFolder_RecognisesItsOwnFolder()
    {
        // An install of 2.9.0 in a folder chosen in setup: no record yet, and the Settings → Apps entry names the folder
        // (here in other letter case and with a trailing backslash). A copy left in the old default must not win.
        var facts = Facts(ExeIn(Chosen), userEntry: @"d:\apps\runnermanager\", hasExe: _ => true);
        Assert.Equal(Chosen, MachineInstall.Resolve(facts));
        // Once recorded, it stays, also when the entry is changed later.
        Assert.Equal(Chosen, MachineInstall.Resolve(Facts(ExeIn(Chosen), ownRecord: Chosen, userEntry: @"C:\Users\me\Evil", hasExe: _ => true)));
    }

    [Fact]
    public void TheRecord_ComesFromTheTasks_WhenSettingsAppsWasChanged()
    {
        // The Settings → Apps entry can be changed without administrator rights; this user's scheduled tasks cannot. The
        // folder keeps the letter case of the running exe.
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(ExeIn(Chosen), [@"d:\apps\runnermanager\RunnerManager.exe"], _ => true));
        // A name without a folder, or another program's exe, is no install.
        Assert.Null(MachineInstall.FolderToRecord(ExeIn(Chosen), [AppPaths.ExeName, @"D:\Apps\RunnerManager\Other.exe"], _ => true));
    }

    [Fact]
    public void TheRecord_OfACopyThatIsNotTheInstall_IsTheFolderItsTasksStart()
    {
        // A 2.9.0 install in a chosen folder, and a build folder or a downloaded copy started with administrator rights.
        const string build = @"D:\src\bin\Release\RunnerManager.exe";
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(build, [ExeIn(Chosen)], f => f == Chosen));
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(@"C:\Users\me\Downloads\RunnerManagerSetup.exe", [ExeIn(Chosen)], f => f == Chosen));
        // The first task whose folder is still there; none, when no folder is.
        Assert.Equal(Chosen, MachineInstall.FolderToRecord(build, [ExeIn(@"E:\Gone\RunnerManager"), ExeIn(Chosen)], f => f == Chosen));
        Assert.Null(MachineInstall.FolderToRecord(build, [ExeIn(Chosen)], _ => false));
        // RunnerWidget, the name of the versions before 2.2.
        Assert.Equal(OldDefault, MachineInstall.FolderToRecord(build, [Path.Combine(OldDefault, AppPaths.LegacyExeName)], _ => true));
    }

    [Fact]
    public void NotElevated_FollowsSettingsApps()
    {
        // An AI tool's host runs without administrator rights; it changes nothing on its own.
        Assert.Equal(Chosen, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", userEntry: Chosen, elevated: false)));
        Assert.Equal(ProgramFiles, MachineInstall.Resolve(Facts(@"D:\src\bin\Release\RunnerManager.exe", ownRecord: ProgramFiles, userEntry: Chosen, elevated: false)));
    }

    [Fact]
    public void WithoutATask_NothingIsRecorded_WhereverTheCopyRuns()
    {
        // Program Files, the old default and the folder of the Settings → Apps entry say nothing about whose install it is:
        // one approved UAC prompt for a copy in Downloads must not make Downloads the install folder.
        foreach (var running in new[] { ExeIn(ProgramFiles), ExeIn(OldDefault), ExeIn(Chosen), @"C:\Users\me\Downloads\RunnerManager.exe", "RunnerManager.exe" })
            Assert.Null(MachineInstall.FolderToRecord(running, [], _ => true));
    }

    [Fact]
    public void Tests_NeverUseTheRealRecordsOrTasks()
    {
        Assert.IsType<MemoryInstallRecords>(MachineInstall.Records);
        Assert.IsType<MemoryTasks>(MachineInstall.Tasks);
    }

    // ================= 3. Where an update is prepared =================

    [Fact]
    public void Updates_ArePreparedInsideTheInstallFolder_NotInTheUsersDataFolder()
    {
        var staging = AppUpdater.StagingFolder(ProgramFiles, 4242);
        Assert.Equal(Path.Combine(ProgramFiles, "RunnerManager.update-4242"), staging);
        Assert.True(MachineInstall.IsInside(staging, ProgramFiles));
        Assert.False(MachineInstall.IsInside(AppUpdater.StagingFolder(MachineInstall.ProgramFilesDir, 1), FleetConfig.StateDir));
        Assert.True(AppUpdater.IsStagingFolderName(Path.GetFileName(staging)));
        // The clean-up after a multi-file build leaves another process's update alone, and only that.
        Assert.False(AppUpdater.IsStagingFolderName("runtimes"));
        Assert.False(AppUpdater.IsStagingFolderName("update-1"));
        Assert.False(AppUpdater.IsStagingFolderName("RunnerManager.update-x"));
    }

    [Fact]
    public void StaleUpdateFolders_AreRemoved_OnlyWithTheirOwnNames_AndNeverThroughALink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-staging-{Guid.NewGuid():N}");
        var install = Path.Combine(root, "Apps");
        var data = Path.Combine(root, "data");
        var elsewhere = Path.Combine(root, "elsewhere");
        string Folder(string parent, string name, bool withFile = true)
        {
            var folder = Path.Combine(parent, name);
            Directory.CreateDirectory(folder);
            if (withFile) File.WriteAllText(Path.Combine(folder, AppPaths.ExeName), "x");
            return folder;
        }
        try
        {
            Folder(elsewhere, "target");
            // A folder chosen in setup may hold the user's own folders, such as one named update-2024.
            var own = Folder(install, "update-2024");
            var stale = Folder(install, "RunnerManager.update-111");
            var running = Folder(install, "RunnerManager.update-222");
            // The data folder of versions up to 2.9.0, which a program of the user can change.
            var oldStale = Folder(data, "update-333");
            var link = Path.Combine(data, "update-444");
            var holdsLink = Folder(data, "update-555", withFile: false);
            Junction(link, Path.Combine(elsewhere, "target"));
            Junction(Path.Combine(holdsLink, "inner"), Path.Combine(elsewhere, "target"));

            AppUpdater.RemoveStaleStagingFolders(install, data, id => id == 222);

            Assert.True(File.Exists(Path.Combine(own, AppPaths.ExeName)));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(running));
            Assert.False(Directory.Exists(oldStale));
            // Nothing was deleted through a link.
            Assert.True(File.Exists(Path.Combine(elsewhere, "target", AppPaths.ExeName)));
            Assert.True(Directory.Exists(holdsLink));
        }
        finally
        {
            // Links first, so the clean-up of the test never follows one.
            foreach (var junction in new[] { Path.Combine(data, "update-444"), Path.Combine(data, "update-555", "inner") })
                if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), $"No junction at {link}");
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

    [Fact]
    public void TheReleaseBuild_LoadsNoStartupHooks()
    {
        // The user's DOTNET_STARTUP_HOOKS would otherwise load an assembly into a process with administrator rights.
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(InstallEvidenceTests.SourceDir, "RunnerManager.csproj"));
        var setting = project.Descendants("StartupHookSupport").Single();
        Assert.Equal("false", setting.Value.Trim());
        Assert.Equal("'$(Configuration)' == 'Release'", setting.Parent!.Attribute("Condition")?.Value);
    }

    [Fact]
    public void PowerShell_IsNeverStartedByItsBareName()
    {
        // A bare name is looked for in the program's folder and the current folder before the Windows folder.
        foreach (var file in Directory.GetFiles(InstallEvidenceTests.SourceDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            Assert.False(code.Contains("\"powershell.exe\"", StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} starts powershell.exe by its bare name.");
            Assert.False(code.Contains("\"powershell\"", StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} starts powershell by its bare name.");
        }
    }

    // ================= 4. Several Windows users =================

    [Fact]
    public void ASecondUser_GetsTheInstallThatIsThere()
    {
        // Another user's record, which only administrators can write: confirmed.
        var others = new[] { new InstallRecord(OtherSid, ProgramFiles) };
        Assert.Equal(new SetupOffer(ProgramFiles, Unconfirmed: false), Offer(null, null, f => f == ExeIn(ProgramFiles), others));
        // A folder another user chose outside the profiles is shared as well.
        var chosen = new[] { new InstallRecord(OtherSid, Chosen) };
        Assert.Equal(new SetupOffer(Chosen, Unconfirmed: false), Offer(null, null, f => f == ExeIn(Chosen), chosen));
        // Another user's copy in their own profile is theirs: this user gets Program Files.
        var theirs = new[] { new InstallRecord(OtherSid, @"C:\Users\them\AppData\Local\Programs\RunnerManager") };
        Assert.Equal(ProgramFiles, Offer(null, null, f => f == ExeIn(theirs[0].Folder), theirs).Folder);
        // A record whose folder has no exe is no install.
        Assert.Equal(ProgramFiles, Offer(null, null, _ => false, chosen).Folder);
        // This user's own install still comes first.
        Assert.Equal(OldDefault, Offer(null, null, _ => true, others).Folder);
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
        string Account(string sid) => sid == OtherSid ? @"PC\bob" : sid;
        var users = MachineInstall.OtherUsersOf(ProgramFiles, MySid, records, new TaskScan(tasks.ToList(), false), Account, _ => true);
        Assert.Equal(new[] { @"PC\bob", @"PC\carol" }, users.Names);
        Assert.False(users.Unknown);
        // The same user found twice is named once.
        var twice = MachineInstall.OtherUsersOf(ProgramFiles, MySid, records,
            new TaskScan(tasks.Append(new TaskUse(OtherSid, false, ExeIn(ProgramFiles))).ToList(), false), Account, _ => true);
        Assert.Equal(2, twice.Names.Count);
        // Nobody else uses this user's own folder in the old default.
        Assert.False(MachineInstall.OtherUsersOf(@"C:\Users\me\Other", MySid, records, new TaskScan(tasks.ToList(), false), Account, _ => true).KeepFiles);
    }

    [Fact]
    public void OtherUsersOf_SkipsTheRecordsOfRemovedAccounts_AndKeepsTheFilesWhenTasksCouldNotBeRead()
    {
        var records = new[] { new InstallRecord(OtherSid, ProgramFiles) };
        var none = new TaskScan(new List<TaskUse>(), Unreadable: false);
        // The account was removed: its record no longer keeps the program files.
        var removed = MachineInstall.OtherUsersOf(ProgramFiles, MySid, records, none, sid => sid, _ => false);
        Assert.Empty(removed.Names);
        Assert.False(removed.KeepFiles);
        // A task that could not be read may be another user's: the files stay, but nobody is named.
        var unknown = MachineInstall.OtherUsersOf(ProgramFiles, MySid, Array.Empty<InstallRecord>(), none with { Unreadable = true }, sid => sid, _ => true);
        Assert.Empty(unknown.Names);
        Assert.True(unknown.KeepFiles);
        Assert.Contains("could not read", unknown.KeptBecause("them"));
        Assert.DoesNotContain("n't", unknown.KeptBecause("them"));
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
    public void OwnInstall_IsTheRecordedOneOnly()
    {
        // A move closes and removes the copy it leaves. The Settings → Apps entry and the old default are no reason to.
        Assert.Equal(Chosen, Installer.OwnInstall(Chosen));
        Assert.Null(Installer.OwnInstall(null));
    }

    [Fact]
    public void SetupAndUninstall_LeaveOtherSessionsAlone()
    {
        const string dashboard = "\"C:\\Program Files\\RunnerManager\\RunnerManager.exe\" --minimized";
        Assert.True(Installer.IsClosedBySetup(dashboard, endAssistants: true, inThisSession: true));
        Assert.False(Installer.IsClosedBySetup(dashboard, endAssistants: true, inThisSession: false));
        Assert.False(Installer.IsClosedBySetup("\"C:\\Program Files\\RunnerManager\\RunnerManager.exe\" --mcp", endAssistants: true, inThisSession: false));

        // The filter that setup and uninstall use on the running processes.
        const string exe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
        var dirs = new[] { @"C:\Program Files\RunnerManager\" };
        Assert.True(Installer.ClosesProcess(12345, dashboard, exe, inThisSession: true, dirs, endAssistants: false));
        // Another Windows user's dashboard of the same install.
        Assert.False(Installer.ClosesProcess(12345, dashboard, exe, inThisSession: false, dirs, endAssistants: true));
        // A copy in another folder, setup itself, and the fleet agent.
        Assert.False(Installer.ClosesProcess(12345, dashboard, @"C:\Program Files\RunnerManager2\RunnerManager.exe", true, dirs, true));
        Assert.False(Installer.ClosesProcess(Environment.ProcessId, dashboard, exe, true, dirs, true));
        Assert.False(Installer.ClosesProcess(12345, $"\"{exe}\" --agent", exe, true, dirs, true));
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
            // Everything the script does is inside the check that the variable is set, for either way of removing.
            foreach (var script in new[] { Installer.RemoveProgramFilesScript(root), Installer.RemoveProgramFilesScript(ProgramFiles) })
            {
                var guard = $"$f = $env:{Installer.FolderVariable}; if ($f) {{ ";
                Assert.StartsWith(guard, script);
                Assert.EndsWith(" }", script);
                Assert.Equal(script.Count(c => c == '{'), script.Count(c => c == '}'));
                var body = script[guard.Length..^2];
                var depth = 0;
                foreach (var c in body)
                {
                    depth += c == '{' ? 1 : c == '}' ? -1 : 0;
                    Assert.True(depth >= 0, "The script leaves the check that the variable is set.");
                }
            }
            var start = new ProcessStartInfo(AppUpdater.PowerShellExe)
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

/// <summary>
/// What counts as evidence of the install: this user's scheduled tasks with the highest rights, and the records. These
/// tests set their own records and tasks in memory (see <see cref="TestInstallState"/>), never the real ones.
/// </summary>
[Collection(InstallStateCollection.Name)]
public class InstallEvidenceTests
{
    private static readonly string Me = WindowsIdentity.GetCurrent().Name;
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private const string ProgramFilesExe = @"C:\Program Files\RunnerManager\RunnerManager.exe";
    private const string ChosenExe = @"D:\Apps\RunnerManager\RunnerManager.exe";

    /// <summary>A task as Runner Manager registers it, or (<paramref name="highest"/> false) as any program of the user can.</summary>
    internal static string TaskOf(string user, string exe, bool highest = true)
    {
        var xml = ScheduledTasks.TaskXml(user, exe, "--agent", "Test task", alwaysOn: false, atLogon: true, parallel: false);
        return highest ? xml : xml.Replace("<RunLevel>HighestAvailable</RunLevel>", "<RunLevel>LeastPrivilege</RunLevel>");
    }

    /// <summary>The app's source folder.</summary>
    internal static string SourceDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RunnerManager.sln"))) return Path.Combine(dir.FullName, "src", "RunnerManager");
            throw new DirectoryNotFoundException("The repository folder was not found.");
        }
    }

    // ================= 1. Tasks as evidence =================

    [Fact]
    public void OnlyThisUsersTaskWithTheHighestRights_GivesItsExe()
    {
        Assert.Equal(ChosenExe, ScheduledTasks.ExeOfOwnElevatedTask(TaskOf(Me, ChosenExe)));
        // The same task as any program of the user can create it: without the highest rights, or without a run level.
        Assert.Null(ScheduledTasks.ExeOfOwnElevatedTask(TaskOf(Me, ChosenExe, highest: false)));
        Assert.Null(ScheduledTasks.ExeOfOwnElevatedTask(TaskOf(Me, ChosenExe).Replace("<RunLevel>HighestAvailable</RunLevel>", string.Empty)));
        // Another Windows user's task, and no task.
        Assert.Null(ScheduledTasks.ExeOfOwnElevatedTask(TaskOf(OtherSid, ChosenExe)));
        Assert.Null(ScheduledTasks.ExeOfOwnElevatedTask(string.Empty));
        Assert.DoesNotContain("n't", ScheduledTasks.NotRegisteredElevatedText(ScheduledTasks.AgentTaskName));
    }

    [Theory]
    [InlineData("MachineRelay.cs", "public static async Task<bool> RepairAgentTaskSettingsAsync()")]
    [InlineData("ScheduledTasks.cs", "public static async Task<bool> UpdateAgentTaskIfOutdatedAsync(bool fromAgent)")]
    public void TheAgentTaskIsRegisteredAgain_OnlyWithTheExeOfATaskWithTheHighestRights(string file, string signature)
    {
        var code = File.ReadAllText(Path.Combine(SourceDir, file));
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var body = code[start..code.IndexOf("RegisterAgentTaskAsync(exe)", start, StringComparison.Ordinal)];
        Assert.Contains("ExeOfOwnElevatedTask(xml)", body);
        Assert.DoesNotContain("CommandOf(", body);
    }

    [Fact]
    public async Task OwnTaskExes_LeaveOutTasksWithoutTheHighestRights()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = TaskOf(Me, ChosenExe);
        // A fleet agent task on a PC without one, created by a program without administrator rights.
        state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, @"C:\Users\me\Downloads\RunnerManager.exe", highest: false);
        state.Tasks.Definitions[ScheduledTasks.DashboardTaskName] = TaskOf(OtherSid, ProgramFilesExe);
        Assert.Equal(new[] { ChosenExe }, await MachineInstall.OwnTaskExesAsync());
    }

    [Fact]
    public async Task OtherUsersTasks_CountOnlyWithTheHighestRights()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions["Runner Manager (bob)"] = TaskOf(OtherSid, ProgramFilesExe);
        state.Tasks.Definitions["Runner Manager (eve)"] = TaskOf("S-1-5-21-1-2-3-1005", ProgramFilesExe, highest: false);
        state.Tasks.Definitions["OneDrive Standalone Update Task"] = TaskOf("S-1-5-21-1-2-3-1006", ProgramFilesExe);
        var scan = await MachineInstall.RunnerManagerTasksAsync();
        Assert.Equal(new[] { OtherSid }, scan.Uses.Select(u => u.Account));
        Assert.False(scan.Unreadable);
    }

    // ================= 6. Other Windows users =================

    [Fact]
    public async Task ATaskThatCannotBeRead_OrNoListOfTasks_IsUnknown_NotNobody()
    {
        using var state = new TestInstallState();
        state.Tasks.Definitions["Runner Manager (bob)"] = null;
        var scan = await MachineInstall.RunnerManagerTasksAsync();
        Assert.True(scan.Unreadable);
        Assert.True((await MachineInstall.OtherUsersOfAsync(MachineInstall.ProgramFilesDir)).KeepFiles);
        state.Tasks.Definitions.Clear();
        state.Tasks.NamesReadable = false;
        Assert.True((await MachineInstall.RunnerManagerTasksAsync()).Unreadable);
        // Read without trouble, and nobody else: the files can go.
        state.Tasks.NamesReadable = true;
        Assert.False((await MachineInstall.OtherUsersOfAsync(MachineInstall.ProgramFilesDir)).KeepFiles);
    }

    [Fact]
    public async Task TheRecordOfARemovedAccount_IsRemoved()
    {
        using var state = new TestInstallState();
        const string gone = "S-1-5-21-1-2-3-1007";
        state.Records.Write(gone, MachineInstall.ProgramFilesDir);
        state.Records.Write(OtherSid, MachineInstall.ProgramFilesDir);
        state.WithoutProfile.Add(gone);
        var others = await MachineInstall.OtherUsersOfAsync(MachineInstall.ProgramFilesDir);
        Assert.Equal(new[] { MachineInstall.AccountOf(OtherSid) }, others.Names);
        Assert.Null(MachineInstall.RecordOf(state.Records.ReadAll(), gone));
        Assert.NotNull(MachineInstall.RecordOf(state.Records.ReadAll(), OtherSid));
    }

    // ================= 2 and 4. The record of an install of an earlier version =================

    /// <summary>A new, empty folder in %TEMP% that the test deletes again.</summary>
    internal static string NewFolder(string name = "install")
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task ACopyThatIsNotTheInstall_RecordsTheFolderThatItsTasksStart()
    {
        // 2.9.0 in a chosen folder (no record yet), and a build folder or a downloaded copy started with administrator rights.
        using var state = new TestInstallState();
        var chosen = NewFolder("chosen");
        try
        {
            state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = TaskOf(Me, Path.Combine(chosen, AppPaths.ExeName));
            var log = new List<string>();
            await MachineInstall.RecordRunningInstallAsync(elevated: true, @"D:\src\bin\Release\RunnerManager.exe", log.Add);
            Assert.Equal(chosen, MachineInstall.ReadOwnRecord());
            Assert.Equal($"Recorded {chosen} as the install folder of Runner Manager for {Elevation.CurrentUserName}.", Assert.Single(log));
        }
        finally { Directory.Delete(chosen, recursive: true); }
    }

    [Fact]
    public async Task TheRunningCopy_IsRecorded_OnlyWhenItsOwnTasksWithTheHighestRightsStartIt()
    {
        using var state = new TestInstallState();
        var folder = NewFolder("downloads");
        var other = NewFolder("other");
        var running = Path.Combine(folder, AppPaths.ExeName);
        try
        {
            // A copy in a folder the user can write, started once with a UAC prompt: nothing to record.
            await MachineInstall.RecordRunningInstallAsync(elevated: true, running, _ => { });
            Assert.Null(MachineInstall.ReadOwnRecordRaw());
            // A task that any program of the user can create does not count.
            state.Tasks.Definitions[ScheduledTasks.AgentTaskName] = TaskOf(Me, running, highest: false);
            await MachineInstall.RecordRunningInstallAsync(elevated: true, running, _ => { });
            Assert.Null(MachineInstall.ReadOwnRecordRaw());
            // Only a copy with administrator rights records.
            state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = TaskOf(Me, running);
            await MachineInstall.RecordRunningInstallAsync(elevated: false, running, _ => { });
            Assert.Null(MachineInstall.ReadOwnRecordRaw());
            await MachineInstall.RecordRunningInstallAsync(elevated: true, running, _ => { });
            Assert.Equal(folder, MachineInstall.ReadOwnRecord());
            // Once recorded, it stays.
            state.Tasks.Definitions[ScheduledTasks.LaunchTaskName] = TaskOf(Me, Path.Combine(other, AppPaths.ExeName));
            await MachineInstall.RecordRunningInstallAsync(elevated: true, running, _ => { });
            Assert.Equal(folder, MachineInstall.ReadOwnRecord());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task ARunnerWidgetInstall_IsConfirmedByItsTasks()
    {
        // Its tasks were registered with the highest rights too, under RunnerWidget's names.
        using var state = new TestInstallState();
        var folder = NewFolder("widget");
        try
        {
            state.Tasks.Definitions[ScheduledTasks.LegacyAgentTaskName] = TaskOf(Me, Path.Combine(folder, AppPaths.LegacyExeName));
            await MachineInstall.RecordRunningInstallAsync(elevated: true, @"C:\Users\me\Downloads\RunnerManagerSetup.exe", _ => { });
            Assert.Equal(folder, MachineInstall.ReadOwnRecord());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Setup_RecordsTheInstallThatItsTasksStart_BeforeItOpens()
    {
        // So setup offers that folder as confirmed (the record), also when the Settings → Apps entry was changed.
        var code = File.ReadAllText(Path.Combine(SourceDir, "App.xaml.cs"));
        var call = code.IndexOf("await MachineInstall.RecordRunningInstallAsync(", StringComparison.Ordinal);
        Assert.True(call > 0);
        var condition = code[code.LastIndexOf("if (", call, StringComparison.Ordinal)..call];
        Assert.DoesNotContain("IsSetupLaunch", condition);
        Assert.True(call < code.IndexOf("new SetupWindow()", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRecords_HoldOneFolderPerWindowsUser()
    {
        using var state = new TestInstallState();
        var folder = NewFolder();
        try
        {
            state.Records.Write(OtherSid, ProgramFilesExe);
            MachineInstall.WriteOwnRecord(folder + Path.DirectorySeparatorChar);
            Assert.Equal(folder, MachineInstall.ReadOwnRecordRaw());
            Assert.Equal(folder, MachineInstall.ReadOwnRecord());
            Assert.Equal(new[] { new InstallRecord(OtherSid, ProgramFilesExe) }, MachineInstall.OtherRecords());
            // A record whose folder is gone is no install, but setup can still put it back after a failure.
            Directory.Delete(folder);
            Assert.Null(MachineInstall.ReadOwnRecord());
            Assert.Equal(folder, MachineInstall.ReadOwnRecordRaw());
            MachineInstall.WriteOwnRecord(null);
            Assert.Null(MachineInstall.ReadOwnRecordRaw());
            Assert.Single(state.Records.ReadAll());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder); }
        // Where only administrators can change it, beside the AI assistant switches.
        Assert.Equal(@"SOFTWARE\RunnerManager\Installs", RegistryInstallRecordStore.KeyPath);
    }
}
