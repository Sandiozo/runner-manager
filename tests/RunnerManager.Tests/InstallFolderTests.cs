using System.IO;

namespace RunnerManager.Tests;

public class InstallFolderTests
{
    [Fact]
    public void ChosenFolder_GetsItsOwnSubfolder()
    {
        Assert.Equal(@"D:\Apps\RunnerManager", Installer.InstallFolderFor(@"D:\Apps"));
        Assert.Equal(@"D:\RunnerManager", Installer.InstallFolderFor(@"D:\"));
        Assert.Equal(@"D:\Tools\RunnerManager", Installer.InstallFolderFor(@"D:\Tools\RunnerManager\"));
    }

    [Fact]
    public void FolderWithAnInstalledCopy_IsUsedAsItIs()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"rm-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, AppPaths.ExeName), string.Empty);
            Assert.Equal(folder, Installer.InstallFolderFor(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(@"D:\Apps\RunnerManager", true)]
    [InlineData(@"C:\Program Files\RunnerManager", true)]
    [InlineData(@"Apps\RunnerManager", false)]
    [InlineData(@"\\server\share\RunnerManager", false)]
    [InlineData("", false)]
    public void InstallFolder_IsChecked(string folder, bool valid)
        => Assert.Equal(valid, Installer.InstallFolderProblem(folder) == null);

    [Fact]
    public void FolderInsideWindows_IsRefused()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(Installer.InstallFolderProblem(Path.Combine(windows, "RunnerManager")));
    }

    [Fact]
    public void RemovalScript_KeepsOtherFiles_AndRemovesAFolderLeftEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-remove-{Guid.NewGuid():N}");
        var shared = Path.Combine(root, "shared", "RunnerManager");
        var own = Path.Combine(root, "own", "RunnerManager");
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(own);
        try
        {
            File.WriteAllText(Path.Combine(shared, "RunnerManager.exe"), string.Empty);
            File.WriteAllText(Path.Combine(shared, "notes.txt"), "keep");
            File.WriteAllText(Path.Combine(own, "RunnerManager.exe"), string.Empty);
            File.WriteAllText(Path.Combine(own, "RunnerManager.123.old.exe"), string.Empty);
            // The mark of an update that waits to prove itself (see UpdateGuard.PendingCounts).
            File.WriteAllText(Path.Combine(own, "RunnerManager.exe.pending"), "2.11.0");

            RunPowerShell(Installer.RemoveProgramFilesScript(shared), Installer.FolderEnvironment(shared));
            RunPowerShell(Installer.RemoveProgramFilesScript(own), Installer.FolderEnvironment(own));

            Assert.False(File.Exists(Path.Combine(shared, "RunnerManager.exe")));
            Assert.True(File.Exists(Path.Combine(shared, "notes.txt")));
            Assert.False(Directory.Exists(own));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RemovalScript_RemovesItsOwnDownloadFolder_OutsideProgramFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-remove-update-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "Apps", "RunnerManager");
        // The download folder of an update that was cut off, with a full exe in it.
        var left = Path.Combine(folder, "RunnerManager.update-1234");
        Directory.CreateDirectory(left);
        try
        {
            File.WriteAllText(Path.Combine(folder, AppPaths.ExeName), string.Empty);
            File.WriteAllText(Path.Combine(left, AppPaths.ExeName), "x");
            RunPowerShell(Installer.RemoveProgramFilesScript(folder), Installer.FolderEnvironment(folder));
            Assert.False(Directory.Exists(folder));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [JunctionFact]
    public void RemovalScript_RemovesNoDownloadFolderThatIsOrHoldsALink_NorFoldersOfOtherNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-remove-links-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "Apps", "RunnerManager");
        var elsewhere = Path.Combine(root, "elsewhere");
        var linked = Path.Combine(folder, "RunnerManager.update-3");
        var holdsLink = Path.Combine(folder, "RunnerManager.update-4");
        var other = Path.Combine(folder, "RunnerManager.update-old");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(holdsLink);
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllText(Path.Combine(folder, AppPaths.ExeName), string.Empty);
            File.WriteAllText(Path.Combine(elsewhere, AppPaths.ExeName), "keep");
            File.WriteAllText(Path.Combine(other, "notes.txt"), "keep");
            Junctions.Create(linked, elsewhere);
            Junctions.Create(Path.Combine(holdsLink, "inner"), elsewhere);

            RunPowerShell(Installer.RemoveProgramFilesScript(folder), Installer.FolderEnvironment(folder));

            Assert.False(File.Exists(Path.Combine(folder, AppPaths.ExeName)));
            Assert.True(File.Exists(Path.Combine(elsewhere, AppPaths.ExeName)));
            Assert.True(Directory.Exists(linked));
            Assert.True(Directory.Exists(holdsLink));
            Assert.True(File.Exists(Path.Combine(other, "notes.txt")));
        }
        finally
        {
            Junctions.Delete(linked, Path.Combine(holdsLink, "inner"));
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunPowerShell(string script, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo(AppUpdater.PowerShellExe)
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = System.Diagnostics.Process.Start(start)!;
        Assert.True(process.WaitForExit(30_000), "The removal script did not finish within 30 seconds.");
    }

    [Fact]
    public void Uninstall_RemovesTheDefaultFolder_ButOnlyOwnFilesElsewhere()
    {
        Assert.Contains("-Recurse", Installer.RemoveProgramFilesScript(AppUpdater.DefaultInstallDir));
        var custom = Installer.RemoveProgramFilesScript(@"D:\Apps\RunnerManager");
        Assert.DoesNotContain("-Recurse", custom);
        Assert.Contains("'RunnerManager.exe'", custom);
        Assert.Contains("'RunnerManager.exe.previous'", custom);
    }

    [Fact]
    public void AfterArgument_IsReadFromTheArguments()
    {
        Assert.Equal(1234, App.AfterProcessId(new[] { "--minimized", App.AfterArgument + "1234" }));
        Assert.Null(App.AfterProcessId(new[] { App.AfterArgument + "x" }));
        Assert.Null(App.AfterProcessId(Array.Empty<string>()));
    }
}

public class AppSettingsFileTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rm-settings-{Guid.NewGuid():N}", "settings.json");

    [Fact]
    public void Save_ReplacesTheFileAtOnce_AndLoadReadsItBack()
    {
        var path = TempFile();
        try
        {
            new AppSettings { RefreshSeconds = 30, Theme = AppTheme.Light }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(30, loaded.RefreshSeconds);
            Assert.Equal(AppTheme.Light, loaded.Theme);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void DamagedFile_KeepsWhatCanBeRead_AndACopy()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, """{ "CloseToTray": false, "Theme": "Purple", "RefreshSeconds": 10 }""");
            var loaded = AppSettings.Load(path);
            Assert.False(loaded.CloseToTray);
            Assert.Equal(10, loaded.RefreshSeconds);
            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(path)!), f => f.Contains(".damaged-"));
            Assert.NotNull(AppSettings.LoadProblem);

            // A cut-off file keeps the settings before the cut.
            File.WriteAllText(path, """{ "CloseToTray": false, "Refresh""");
            var cut = AppSettings.Load(path);
            Assert.False(cut.CloseToTray);
            Assert.True(cut.NotifyOnFailedRuns);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void UnreadableFile_IsNeverOverwrittenWithDefaults()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, """{ "RefreshSeconds": 30 }""");
            AppSettings loaded;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                loaded = AppSettings.Load(path);
            loaded.Save(path);
            Assert.Equal(30, AppSettings.Load(path).RefreshSeconds);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}

[Collection(FleetSettingsCollection.Name)]
public class UninstallDataTests
{
    [Fact]
    public void RemovingData_KeepsTheTurnedOffStartupScripts()
    {
        var source = Path.Combine(FleetConfig.StateDir, "disabled-startup");
        var documents = Path.Combine(Path.GetTempPath(), $"rm-documents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(documents);
        try
        {
            Assert.Null(Installer.KeepDisabledStartupScripts(_ => { }, documents).Folder);
            File.WriteAllText(Path.Combine(source, "start-runner.cmd"), "run.cmd");
            Directory.CreateDirectory(Path.Combine(documents, "Runner Manager startup scripts"));

            var (folder, moved) = Installer.KeepDisabledStartupScripts(_ => { }, documents);

            Assert.True(moved);
            Assert.Equal(Path.Combine(documents, "Runner Manager startup scripts (2)"), folder);
            Assert.Equal("run.cmd", File.ReadAllText(Path.Combine(folder!, "start-runner.cmd")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(documents, recursive: true);
        }
    }
}

/// <summary>
/// Uninstall with "delete settings" runs with administrator rights, and any program of the user can change the data
/// folder: nothing is deleted or copied through a junction there. The junctions are in folders of the test's own in %TEMP%.
/// </summary>
public class UninstallDataLinkTests
{
    private sealed class Folders : IDisposable
    {
        public Folders()
        {
            Root = Path.Combine(Path.GetTempPath(), $"rm-data-links-{Guid.NewGuid():N}");
            Data = Path.Combine(Root, "RunnerManager");
            // Another folder, such as another user's Documents, with a file that must stay.
            Elsewhere = Path.Combine(Root, "elsewhere");
            Directory.CreateDirectory(Path.Combine(Elsewhere, "sub"));
            File.WriteAllText(Path.Combine(Elsewhere, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(Elsewhere, "sub", "keep.txt"), "keep");
        }

        public string Root { get; }
        public string Data { get; }
        public string Elsewhere { get; }
        public List<string> Links { get; } = new();

        public string Link(string link)
        {
            Junctions.Create(link, Elsewhere);
            Links.Add(link);
            return link;
        }

        public void AssertElsewhereIsWhole()
        {
            Assert.True(File.Exists(Path.Combine(Elsewhere, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(Elsewhere, "sub", "keep.txt")));
        }

        public Dictionary<string, string> Environment => new() { [Installer.DataVariable] = Data };

        public void Dispose()
        {
            Junctions.Delete(Links.ToArray());
            Directory.Delete(Root, recursive: true);
        }
    }

    [JunctionFact]
    public void TheStartupScripts_AreCopiedWithoutWhatALinkInsideHolds()
    {
        using var folders = new Folders();
        var source = Path.Combine(folders.Data, "disabled-startup");
        var documents = Path.Combine(folders.Root, "Documents");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(source, "start-runner.cmd"), "run.cmd");
        folders.Link(Path.Combine(source, "x"));

        var (folder, moved) = Installer.KeepDisabledStartupScripts(_ => { }, documents, source);

        Assert.True(moved);
        Assert.Equal(new[] { Path.Combine(folder!, "start-runner.cmd") }, Directory.GetFiles(folder!, "*", SearchOption.AllDirectories));
    }

    [JunctionFact]
    public void AStartupScriptsFolderThatIsALink_StaysWhereItIs()
    {
        using var folders = new Folders();
        Directory.CreateDirectory(folders.Data);
        var source = folders.Link(Path.Combine(folders.Data, "disabled-startup"));
        var documents = Path.Combine(folders.Root, "Documents");
        Directory.CreateDirectory(documents);
        var log = new List<string>();

        var (folder, moved) = Installer.KeepDisabledStartupScripts(log.Add, documents, source);

        Assert.False(moved);
        Assert.Equal(source, folder);
        Assert.Empty(Directory.GetFileSystemEntries(documents));
        Assert.DoesNotContain("n't", Assert.Single(log));
        // Uninstall then keeps that entry of the data folder, and deletes nothing through it.
        InstallFolderTests.RunPowerShell(Installer.RemoveDataScript(keepDisabledStartup: true), folders.Environment);
        Assert.True(Directory.Exists(source));
        folders.AssertElsewhereIsWhole();
    }

    [JunctionFact]
    public void DeletingTheDataFolder_RemovesALinkInside_NotWhatItLinksTo()
    {
        foreach (var keep in new[] { false, true })
        {
            using var folders = new Folders();
            Directory.CreateDirectory(Path.Combine(folders.Data, "logs"));
            Directory.CreateDirectory(Path.Combine(folders.Data, "disabled-startup"));
            File.WriteAllText(Path.Combine(folders.Data, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(folders.Data, "disabled-startup", "start.cmd"), "run.cmd");
            var top = folders.Link(Path.Combine(folders.Data, "update-1"));
            var nested = folders.Link(Path.Combine(folders.Data, "logs", "old"));

            InstallFolderTests.RunPowerShell(Installer.RemoveDataScript(keepDisabledStartup: keep), folders.Environment);

            folders.AssertElsewhereIsWhole();
            Assert.False(Directory.Exists(top));
            Assert.False(Directory.Exists(nested));
            Assert.False(File.Exists(Path.Combine(folders.Data, "settings.json")));
            Assert.Equal(keep, File.Exists(Path.Combine(folders.Data, "disabled-startup", "start.cmd")));
            Assert.Equal(keep, Directory.Exists(folders.Data));
        }
    }

    [JunctionFact]
    public void ADataFolderThatIsALink_IsOnlyUnlinked()
    {
        foreach (var keep in new[] { false, true })
        {
            using var folders = new Folders();
            folders.Link(folders.Data);

            InstallFolderTests.RunPowerShell(Installer.RemoveDataScript(keepDisabledStartup: keep), folders.Environment);

            folders.AssertElsewhereIsWhole();
            Assert.Equal(keep, Directory.Exists(folders.Data));
        }
    }
}
