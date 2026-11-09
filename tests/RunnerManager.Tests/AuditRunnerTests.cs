using System.IO;

namespace RunnerManager.Tests;

/// <summary>Runner jobs, health checks and runner setup: the fixes from the 2.8.2 audit.</summary>
public class AuditRunnerTests
{
    private static string NewRunnerRoot()
    {
        var root = Directory.CreateTempSubdirectory("rm-audit-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "_diag", "pages"));
        return root;
    }

    private static string WritePage(string root, string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(root, "_diag", "pages", name);
        File.WriteAllText(path, text);
        File.SetCreationTimeUtc(path, writtenUtc);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void OutputFile_OfAHungStep_IsStillFound_AfterHalfAnHour()
    {
        var root = NewRunnerRoot();
        try
        {
            var hung = WritePage(root, "a_1.log", "2026-10-19T12:00:00.0000000Z Compiling…\n", DateTime.UtcNow.AddHours(-2));

            var file = RunnerActivity.CurrentOutputFile(root);

            Assert.NotNull(file);
            Assert.Equal(hung, file!.Path);
            Assert.Equal(new[] { "Compiling…", "" }, RunnerActivity.ReadOutput(root, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputFile_IsTheNewestPage_AndOlderJobsPagesAreLeftOut()
    {
        var root = NewRunnerRoot();
        try
        {
            WritePage(root, "old_1.log", "old job\n", DateTime.UtcNow.AddHours(-5));
            var current = WritePage(root, "new_1.log", "this job\n", DateTime.UtcNow.AddHours(-1));

            Assert.Equal(current, RunnerActivity.CurrentOutputFile(root)!.Path);
            // The worker started after both pages were written: neither belongs to its job.
            Assert.Null(RunnerActivity.CurrentOutputFile(root, DateTime.UtcNow.AddMinutes(-30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputState_ChangesWhileTheRunnerWritesToAnOpenPage()
    {
        var root = NewRunnerRoot();
        try
        {
            var path = Path.Combine(root, "_diag", "pages", "a_1.log");
            using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer.Write("first\n"u8);
            writer.Flush();
            var before = RunnerActivity.CurrentOutputFile(root)!.State;
            writer.Write("second\n"u8);
            writer.Flush();

            Assert.NotEqual(before, RunnerActivity.CurrentOutputFile(root)!.State);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputFile_ThatWentAway_IsNoProgress()
    {
        var cpu = new Dictionary<string, ulong> { ["10|a"] = 1_000_000 };
        var elapsed = TimeSpan.FromSeconds(30);

        Assert.False(JobWatch.MadeProgress(cpu, cpu, "a_1.log|10|5", string.Empty, elapsed));
        Assert.True(JobWatch.MadeProgress(cpu, cpu, string.Empty, "a_1.log|10|5", elapsed));
        Assert.True(JobWatch.MadeProgress(cpu, cpu, "a_1.log|10|5", "a_1.log|20|6", elapsed));
    }
}
