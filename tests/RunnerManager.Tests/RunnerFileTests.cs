using System.Diagnostics;
using System.IO;

namespace RunnerManager.Tests;

public sealed class RunnerFileTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"runner-files-test-{Guid.NewGuid():N}");

    public RunnerFileTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { RunnerService.DeleteFolder(_temp); } catch { }
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A runner install: package folders, settings written by config.cmd, logs and a work folder.</summary>
    private string MakeRunner(string relative)
    {
        foreach (var file in new[] { @"bin\Runner.Listener.exe", @"bin.2.330.0\Runner.Listener.exe", @"externals\node\node.exe",
                     @"_diag\Runner_1.log", @"_work\repo\file.txt", ".runner", ".credentials", ".credentials_rsaparams",
                     "config.cmd", "run.cmd", "run-helper.cmd.template", "safe_sleep.sh" })
            Write(Path.Combine(relative, file));
        return Path.Combine(_temp, relative);
    }

    /// <summary>A directory junction, which needs no administrator rights (unlike a symbolic link).</summary>
    private static void MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.WaitForExit(10000);
        Assert.True(Directory.Exists(link), "Could not create a junction for the test.");
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void RunnerAlone_IsDeletedWhole_WithoutFollowingJunctions()
    {
        var root = MakeRunner("runner");
        var shared = Write(@"shared-cache\tool.exe");
        MakeJunction(Path.Combine(root, "_work", "tool-cache"), Path.GetDirectoryName(shared)!);

        var plan = RunnerService.PlanFolderDeletion(root);
        Assert.Equal(RunnerService.FolderDeletion.Whole, plan.Kind);
        RunnerService.DeleteFolder(root);

        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(shared));
    }

    [Fact]
    public void FolderWithAnotherRunner_KeepsTheOtherRunnerAndUnknownFiles()
    {
        // Like D:\Git-Runners (a runner unpacked straight into the folder) holding D:\Git-Runners\Study-Tool.
        var root = MakeRunner("Git-Runners");
        var nested = MakeRunner(@"Git-Runners\Study-Tool");
        Write(@"Git-Runners\notes.txt");
        var shared = Write(@"outside\keep.txt");
        MakeJunction(Path.Combine(root, "_work", "outside"), Path.GetDirectoryName(shared)!);

        var plan = RunnerService.PlanFolderDeletion(root);
        Assert.Equal(RunnerService.FolderDeletion.RunnerItemsOnly, plan.Kind);
        Assert.Contains("Study-Tool", plan.Reason);
        RunnerService.DeleteRunnerItems(root);

        Assert.True(File.Exists(Path.Combine(nested, ".runner")));
        Assert.True(File.Exists(Path.Combine(nested, @"bin\Runner.Listener.exe")));
        Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
        Assert.True(File.Exists(shared));
        foreach (var item in new[] { "bin", "bin.2.330.0", "externals", "_diag", "_work" })
            Assert.False(Directory.Exists(Path.Combine(root, item)), item);
        foreach (var item in new[] { ".runner", ".credentials", ".credentials_rsaparams", "config.cmd", "run.cmd", "run-helper.cmd.template", "safe_sleep.sh" })
            Assert.False(File.Exists(Path.Combine(root, item)), item);
    }

    [Fact]
    public void CustomWorkFolder_CountsAsTheRunnersOwn()
    {
        var root = MakeRunner("custom");
        Write(@"custom\builds\out.txt");
        Assert.Equal(RunnerService.FolderDeletion.RunnerItemsOnly, RunnerService.PlanFolderDeletion(root).Kind);
        Assert.Equal(RunnerService.FolderDeletion.Whole, RunnerService.PlanFolderDeletion(root, "builds").Kind);
        Assert.True(RunnerService.IsRunnerItem("builds", @"builds\nested"));
        Assert.False(RunnerService.IsRunnerItem("builds", @"D:\builds"));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public void DriveRoots_AreRefused(string path)
        => Assert.Equal(RunnerService.FolderDeletion.Refuse, RunnerService.PlanFolderDeletion(path).Kind);

    [Fact]
    public void ProfileAndSystemFolders_KeepEverythingButTheRunner()
    {
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!,
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
            Assert.Equal(RunnerService.FolderDeletion.RunnerItemsOnly, RunnerService.PlanFolderDeletion(folder).Kind);
    }

    [Fact]
    public void DeleteEntry_RemovesAJunctionButNotItsTarget()
    {
        var target = Write(@"target\keep.txt");
        var link = Path.Combine(_temp, "link");
        MakeJunction(link, Path.GetDirectoryName(target)!);

        Assert.Equal(0, RunnerService.DeleteEntry(new DirectoryInfo(link)));
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ReadOnlyFiles_AreDeleted()
    {
        var file = Write(@"ro\a\b.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Assert.Equal(0, RunnerService.DeleteEntry(new DirectoryInfo(Path.Combine(_temp, "ro"))));
        Assert.False(Directory.Exists(Path.Combine(_temp, "ro")));
    }
}
