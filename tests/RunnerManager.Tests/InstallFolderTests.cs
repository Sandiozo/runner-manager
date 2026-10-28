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

            RunPowerShell(Installer.RemoveProgramFilesScript(shared));
            RunPowerShell(Installer.RemoveProgramFilesScript(own));

            Assert.False(File.Exists(Path.Combine(shared, "RunnerManager.exe")));
            Assert.True(File.Exists(Path.Combine(shared, "notes.txt")));
            Assert.False(Directory.Exists(own));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunPowerShell(string script)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
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
