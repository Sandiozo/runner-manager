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
    public void Uninstall_RemovesTheDefaultFolder_ButOnlyOwnFilesElsewhere()
    {
        Assert.Contains("-Recurse", Installer.RemoveProgramFilesScript(AppUpdater.DefaultInstallDir));
        var custom = Installer.RemoveProgramFilesScript(@"D:\Apps\RunnerManager");
        Assert.DoesNotContain("-Recurse", custom);
        Assert.Contains("'RunnerManager.exe'", custom);
    }
}
