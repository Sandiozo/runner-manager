namespace RunnerManager.Tests;

public class LiveJobTests
{
    [Fact]
    public void OutputLines_LoseTimestampsColorsAndGroupMarkers()
    {
        var lines = RunnerActivity.CleanLines(new[]
        {
            "\uFEFF2026-10-19T12:00:00.1234567Z ##[group]Run dotnet build\r",
            "2026-10-19T12:00:01.0000000Z \u001b[32mBuild succeeded.\u001b[0m",
            "2026-10-19T12:00:02.0000000Z ##[endgroup]",
            "2026-10-19T12:00:03.0000000Z ##[error]Process completed with exit code 1.",
            "plain line"
        }).ToArray();

        Assert.Equal(new[] { "▸ Run dotnet build", "Build succeeded.", "Error: Process completed with exit code 1.", "plain line" }, lines);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\dotnet\dotnet.exe"" build C:\work\repo\App.sln -c Release", "build App.sln -c")]
    [InlineData(@"node.exe index.js --watch", "index.js --watch")]
    [InlineData(@"git.exe -c http.extraheader=""AUTHORIZATION: basic xyz"" fetch", "-c")]
    [InlineData(@"tool.exe --token abc123 run", "")]
    [InlineData(@"cmd.exe", "")]
    public void ProcessArguments_AreShortAndLeaveOutSecrets(string commandLine, string expected)
        => Assert.Equal(expected, RunnerActivity.ShortArguments(commandLine));

    [Fact]
    public void JobView_ShowsTheRunningStepAndDurations()
    {
        var start = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var job = new GitHubJobState
        {
            WorkflowName = "CI",
            Name = "build",
            RunNumber = 42,
            HtmlUrl = "https://github.com/acme/app/actions/runs/1/job/2",
            StartedUtc = start,
            Steps = new[]
            {
                new GitHubJobStep { Number = 1, Name = "Set up job", Status = "completed", Conclusion = "success", StartedUtc = start, CompletedUtc = start.AddSeconds(4) },
                new GitHubJobStep { Number = 2, Name = "Lint", Status = "completed", Conclusion = "skipped" },
                new GitHubJobStep { Number = 3, Name = "Build", Status = "in_progress", StartedUtc = start.AddSeconds(5) },
                new GitHubJobStep { Number = 4, Name = "Test", Status = "queued" }
            }
        };
        var activity = new RunnerJobActivity
        {
            Output = Enumerable.Range(1, 20).Select(i => $"line {i}").ToArray(),
            Processes = new[] { new RunnerProcess { Name = "dotnet.exe", Arguments = "build", CpuPercent = 41.6, MemoryBytes = 300L * 1024 * 1024 } }
        };

        var view = LiveJobView.Build(job, string.Empty, activity, string.Empty, start.AddMinutes(2).AddSeconds(5));

        Assert.Equal("CI → build  ·  run #42", view.Title);
        Assert.Equal("CI  ·  step 3 of 4: Build", view.Summary);
        Assert.Equal("Running for 2m 5s", view.ElapsedText);
        Assert.Equal(new[] { "done", "skipped", "running", "waiting" }, view.Steps.Select(s => s.State));
        Assert.Equal(new[] { "4s", "", "2m 0s", "" }, view.Steps.Select(s => s.DurationText));
        Assert.Equal("dotnet", view.Processes.Single().Name);
        Assert.Equal("42%", view.Processes.Single().CpuText);
        Assert.StartsWith("line 7\n", view.Output);
        Assert.EndsWith("line 20", view.Output);
    }

    [Fact]
    public void JobView_WithoutGitHubJob_ShowsTheNote()
    {
        var view = LiveJobView.Build(null, "Connect GitHub to see the job and its steps.", null, string.Empty, DateTime.UtcNow);
        Assert.Equal(string.Empty, view.Summary);
        Assert.Equal(System.Windows.Visibility.Visible, view.NoteVisibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, view.StepsVisibility);
    }

    [Fact]
    public async Task JobRequest_RoundTripsThroughTheAgent()
    {
        var key = CommandSigner.NewKey();
        var port = TestPorts.FreeRange(3);
        using var server = new AgentServer(
            () => Task.FromResult(new MachineStatusPayload { MachineName = "TESTPC" }), c => c.Id,
            _ => Task.FromResult(new LogPayload()), () => key, $"http://localhost:{port}/rw/", port + 1, "TESTPC",
            job: runnerKey => Task.FromResult(new RunnerJobActivity
            {
                Output = new[] { $"output of {runnerKey}" },
                Processes = new[] { new RunnerProcess { Id = 7, Name = "node.exe", CpuPercent = 12 } }
            }));
        server.Start();

        var response = await FleetNetwork.CallAsync($"localhost:{port}", "TESTPC",
            new FleetRequest { Op = "job", LogKey = "actions.runner.acme-app.build-01" }, pairingKey: key);

        Assert.True(response.Ok);
        Assert.Equal("output of actions.runner.acme-app.build-01", response.Job!.Output.Single());
        Assert.Equal("node.exe", response.Job.Processes.Single().Name);
    }
}
