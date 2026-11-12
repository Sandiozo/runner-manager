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
            var worker = new RunnerActivity.ProcessSnapshot(new[] { Worker(root, DateTime.UtcNow.AddHours(-3)) });
            Assert.Equal(new[] { "Compiling…", "" }, RunnerActivity.ReadOutput(root, 10, worker));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Output_IsEmpty_WhenTheRunnerRunsNoJob_AndShowsOnlyThisJobsPage()
    {
        var root = NewRunnerRoot();
        try
        {
            // A page the runner did not upload after an earlier job.
            WritePage(root, "left_1.log", "an earlier job\n", DateTime.UtcNow.AddHours(-5));
            var idle = new RunnerActivity.ProcessSnapshot(Array.Empty<RunnerActivity.ProcessRow>());
            Assert.Empty(RunnerActivity.ReadOutput(root, 10, idle));

            var busy = new RunnerActivity.ProcessSnapshot(new[] { Worker(root, DateTime.UtcNow.AddMinutes(-10)) });
            Assert.Empty(RunnerActivity.ReadOutput(root, 10, busy));
            WritePage(root, "now_1.log", "this job\n", DateTime.UtcNow.AddMinutes(-5));
            Assert.Equal(new[] { "this job", "" }, RunnerActivity.ReadOutput(root, 10, busy));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OutputFile_CreatedBeforeTheWorker_IsLeftOut_AlsoWhenWrittenSince()
    {
        var root = NewRunnerRoot();
        try
        {
            var path = WritePage(root, "old_1.log", "an earlier job\n", DateTime.UtcNow.AddHours(-5));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            // Skipped by the listed creation time, without opening it.
            Assert.Null(RunnerActivity.CurrentOutputFile(root, DateTime.UtcNow.AddHours(-1)));
            Assert.Equal(path, RunnerActivity.CurrentOutputFile(root)!.Path);
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

    private static RunnerActivity.ProcessRow Row(int id, int parent, string name, string path, DateTime startedUtc)
        => new(id, parent, name, path, string.Empty, 0, 0, System.Management.ManagementDateTimeConverter.ToDmtfDateTime(startedUtc.ToLocalTime()));

    private static RunnerActivity.ProcessRow Worker(string root, DateTime startedUtc)
        => Row(1, 0, "Runner.Worker.exe", Path.Combine(root, "bin", "Runner.Worker.exe"), startedUtc);

    private static RunnerInfo Busy(string root) => new() { InstallPath = root, IsRunning = true, ActivityText = "Busy" };

    [Fact]
    public void JobProcesses_LeaveOutAnOlderProcess_WhoseEndedParentHadTheWorkersId()
    {
        const string root = @"C:\GitHub-Runners\app";
        var started = DateTime.UtcNow.AddHours(-1);
        var rows = new[]
        {
            Row(100, 50, "Runner.Worker.exe", root + @"\bin\Runner.Worker.exe", started),
            Row(200, 100, "dotnet.exe", @"C:\dotnet\dotnet.exe", started.AddSeconds(10)),
            Row(201, 200, "MSBuild.exe", @"C:\dotnet\MSBuild.exe", started.AddSeconds(20)),
            // explorer.exe outlives userinit.exe, whose ID Windows later gave to the worker.
            Row(300, 100, "explorer.exe", @"C:\Windows\explorer.exe", started.AddDays(-1)),
            Row(301, 300, "notepad.exe", @"C:\Windows\notepad.exe", started.AddMinutes(5)),
            // Another runner's worker.
            Row(400, 51, "Runner.Worker.exe", @"C:\GitHub-Runners\other\bin\Runner.Worker.exe", started)
        };

        var job = RunnerActivity.JobRows(root, rows);

        Assert.Equal(new[] { 100, 200, 201 }, job.Select(p => p.Id).Order());
    }

    [Fact]
    public void RememberedRunnerFolder_IsUsed_OnlyWhileItStillHoldsTheRunner()
    {
        var root = Directory.CreateTempSubdirectory("rm-audit-").FullName;
        try
        {
            const string service = "actions.runner.owner-repo.build-01";
            File.WriteAllText(Path.Combine(root, ".runner"), "{}");
            File.WriteAllText(Path.Combine(root, ".service"), service);

            Assert.True(RunnerActivity.StillHoldsRunner(root, service));
            Assert.True(RunnerActivity.StillHoldsRunner(root, root + "\\"));
            Assert.False(RunnerActivity.StillHoldsRunner(root, "actions.runner.owner-repo.other"));
            // "Remove and keep files" deletes the registration.
            File.Delete(Path.Combine(root, ".runner"));
            Assert.False(RunnerActivity.StillHoldsRunner(root, service));
            Assert.False(RunnerActivity.StillHoldsRunner(root, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void JobWatch_ReadsTheProcessesOnce_ForAllBusyRunners()
    {
        var a = NewRunnerRoot();
        var b = NewRunnerRoot();
        try
        {
            var started = DateTime.UtcNow.AddMinutes(-3);
            var snapshot = new RunnerActivity.ProcessSnapshot(new[]
            {
                Row(1, 0, "Runner.Worker.exe", Path.Combine(a, "bin", "Runner.Worker.exe"), started),
                Row(2, 0, "Runner.Worker.exe", Path.Combine(b, "bin", "Runner.Worker.exe"), started)
            });
            var reads = 0;
            RunnerActivity.ProcessSnapshot Read() { reads++; return snapshot; }

            JobWatch.Update(new[] { Busy(a), Busy(b) }, Read);
            Assert.Equal(1, reads);
            Assert.NotNull(JobWatch.For(a).StartedUtc);
            Assert.NotNull(JobWatch.For(b).StartedUtc);

            // Both readings are fresh, so nothing is read again.
            JobWatch.Update(new[] { Busy(a), Busy(b) }, Read);
            Assert.Equal(1, reads);
        }
        finally
        {
            JobWatch.Update(Array.Empty<RunnerInfo>());
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public void JobWatch_DoesNotCountASleep_AsQuietTime()
    {
        const ulong second = 10_000_000;
        var worker = DateTime.UtcNow.AddHours(-3);
        RunnerActivity.JobSample Sample(ulong cpu) => new(worker, new Dictionary<string, ulong> { ["1|a"] = cpu }, "page|10|1");
        var now = DateTime.UtcNow;
        var quietRoot = @"C:\audit-sleep\quiet";
        var busyRoot = @"C:\audit-sleep\busy";
        try
        {
            // Two readings 30 s apart, then the PC sleeps for two hours and is awake for 60 s before the next reading.
            foreach (var root in new[] { quietRoot, busyRoot })
            {
                JobWatch.Record(root, Sample(0), now.AddHours(-2).AddSeconds(-30), 1_000 * second);
                JobWatch.Record(root, Sample(0), now.AddHours(-2), 1_030 * second);
            }
            JobWatch.Record(quietRoot, Sample(0), now, 1_090 * second);
            // 3.1 s of CPU in the 60 s the PC was awake is more than 5% of a core.
            JobWatch.Record(busyRoot, Sample(31 * second / 10), now, 1_090 * second);

            // Quiet for the 90 s the PC was awake, not since before the sleep.
            var quietSince = JobWatch.For(quietRoot).QuietSinceUtc;
            Assert.NotNull(quietSince);
            Assert.InRange(quietSince!.Value, now.AddMinutes(-2), now.AddMinutes(-1));
            Assert.Null(JobWatch.For(busyRoot).QuietSinceUtc);
        }
        finally { JobWatch.Update(Array.Empty<RunnerInfo>()); }
    }

    [Theory]
    [InlineData(@"mysql.exe -u root -pSecret shop", "-u root")]
    [InlineData(@"curl.exe -u user:pass https://example.com/api", "")]
    [InlineData(@"curl.exe --user=user:pass https://example.com/api", "")]
    [InlineData(@"curl.exe -uuser:pass https://example.com/api", "")]
    [InlineData(@"sshpass.exe -p hunter2 ssh host", "")]
    [InlineData(@"C:\Windows\system32\net.exe use \\srv\share P@ss /user:dom\bob", "use")]
    [InlineData(@"NET1.EXE user bob S3cret /add", "user")]
    [InlineData(@"tool.exe --password=hunter2 run", "")]
    [InlineData(@"tool.exe deploy token=abc", "deploy")]
    [InlineData(@"tool.exe login ghp_abcdef1234567890", "login")]
    [InlineData(@"git.exe clone https://bob:hunter2@example.com/team/repo.git", "clone repo.git")]
    [InlineData(@"git.exe fetch https://bob:hunter2@example.com", "fetch example.com")]
    [InlineData(@"schtasks.exe /Create /RU bob /RP hunter2", "Create RU bob")]
    [InlineData(@"7z.exe x -pSecret backup.7z", "x")]
    [InlineData(@"sqlcmd.exe -U sa -P Secret", "-U sa")]
    [InlineData(@"tool.exe --pass hunter2 run", "")]
    [InlineData(@"gpg.exe --batch --passphrase hunter2 -c file", "--batch")]
    [InlineData(@"keytool.exe -genkey -storepass hunter2", "-genkey")]
    [InlineData(@"mvn.exe verify -Dsonar.login=abc123", "verify")]
    [InlineData(@"mvn.exe verify -Dsonar.token=abc123", "verify")]
    [InlineData(@"azcopy.exe copy https://acct.blob.core.windows.net/c?sv=2022-11-02&sig=abc123 out", "copy")]
    [InlineData(@"curl.exe -H ""Cookie: session=abc"" https://example.com", "-H")]
    [InlineData(@"curl.exe -H ""Authorization: Bearer abc"" https://example.com", "-H")]
    // Ordinary arguments stay.
    [InlineData(@"dotnet.exe build -p:Configuration=Release", "build -p:Configuration=Release")]
    [InlineData(@"dotnet.exe msbuild -property:Configuration=Release", "msbuild -property:Configuration=Release")]
    [InlineData(@"git.exe push -u origin main", "push -u origin")]
    [InlineData(@"Unity.exe -batchmode -projectPath C:\proj\Game -quit", "-batchmode -projectPath Game")]
    [InlineData(@"mkdir.exe -p build/out", "-p out")]
    [InlineData(@"node.exe server.js -p 8080", "server.js -p 8080")]
    [InlineData(@"gradle.exe build -Pversion=1.2", "build -Pversion=1.2")]
    public void ProcessArguments_LeaveOutCommonPasswordForms(string commandLine, string expected)
        => Assert.Equal(expected, RunnerActivity.ShortArguments(commandLine));

    [Theory]
    // -pw is PuTTY's password option, and it is a password for every program.
    [InlineData(@"pscp.exe -pw Secret file.txt host:/tmp", "")]
    [InlineData(@"plink -ssh -pw Secret host", "-ssh")]
    [InlineData(@"psftp.exe -pw Secret host", "")]
    [InlineData(@"""C:\Program Files\PuTTY\pscp.exe"" -pw Secret a b", "")]
    [InlineData(@"C:\Program Files\PuTTY\plink.exe -batch -pw Secret host", "plink.exe -batch")]
    [InlineData(@"tool.exe -pw=Secret run", "")]
    [InlineData(@"tool.exe -pw:Secret run", "")]
    [InlineData(@"tool.exe -pwSecret run", "")]
    [InlineData(@"tool.exe -PW Secret run", "")]
    [InlineData(@"tool.exe --pw Secret run", "")]
    [InlineData(@"tool.exe /pw:Secret run", "")]
    [InlineData("pscp.exe\t-pw\tSecret file.txt", "")]
    [InlineData(@"tool.exe '-pw' 'Secret'", "")]
    // -p and -P of docker, podman, helm, az, oc, psexec, PuTTY and twine, with or without .exe, a path or quotes.
    [InlineData(@"docker login -p Secret ghcr.io", "login")]
    [InlineData(@"docker.exe login -pSecret", "login")]
    [InlineData(@"DOCKER.EXE login -p=Secret", "login")]
    [InlineData(@"""C:\Program Files\Docker\Docker\resources\bin\docker.exe"" login -P Secret", "login")]
    [InlineData(@"C:\Program Files\Docker\docker.exe login -p Secret", "docker.exe login")]
    [InlineData(@"docker login --password Secret", "login")]
    [InlineData(@"docker login --password=Secret", "login")]
    [InlineData(@"podman.exe login -p Secret quay.io", "login")]
    [InlineData(@"helm registry login -p Secret", "registry login")]
    [InlineData(@"helm.exe repo add --password=Secret", "repo add")]
    [InlineData(@"az.cmd login -p Secret", "login")]
    [InlineData(@"oc login -p Secret", "login")]
    [InlineData(@"C:\Tools\PsExec64.exe -accepteula -p Secret \\host cmd", "-accepteula")]
    [InlineData(@"psexec -p Secret", "")]
    [InlineData(@"""C:\Tools\PSEXEC.EXE"" -pSecret", "")]
    [InlineData(@"plink.exe -P 22 host", "")]
    [InlineData(@"twine upload -p Secret dist", "upload")]
    // A program started through another one, or the rest of a path with spaces that is not in quotes.
    [InlineData(@"cmd.exe /c mysql -pSecret", "c mysql")]
    [InlineData(@"C:\Program Files\MySQL\bin\mysql.exe -pSecret db", "mysql.exe")]
    // Other forms.
    [InlineData(@"curl.exe -U proxyuser:Secret https://example.com", "")]
    [InlineData(@"curl.exe --proxy-user=proxyuser:Secret https://example.com", "")]
    [InlineData(@"curl.exe --proxy-user proxyuser:Secret https://example.com", "")]
    [InlineData(@"nuget.exe push App.1.0.0.nupkg Secret -Source https://example.com", "push App.1.0.0.nupkg")]
    [InlineData(@"tool.exe publish npm_abcdefgh123", "publish")]
    [InlineData(@"vault.exe login hvs.CAESIabc", "login")]
    [InlineData(@"tool.exe upload pypi-AgEIcHlwaS5vcmc", "upload")]
    [InlineData(@"tool.exe login dckr_pat_abc123", "login")]
    [InlineData(@"tool.exe call sk-ant-api03-abc", "call")]
    [InlineData(@"tool.exe run --header=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "run")]
    [InlineData(@"tool.exe send Endpoint=sb://bus.example.net/;SharedAccessKey=abc", "send")]
    [InlineData(@"curl.exe https://maps.example.com/api?key=abc", "")]
    // Ordinary arguments stay.
    [InlineData(@"docker.exe build -t app .", "build -t app")]
    [InlineData(@"pwsh.exe -NoProfile -File build.ps1", "-NoProfile -File build.ps1")]
    [InlineData(@"psql.exe -U postgres -d app", "-U postgres -d")]
    [InlineData(@"cmd.exe /c dotnet build -p:Configuration=Release", "c dotnet build")]
    public void ProcessArguments_LeaveOutPasswordsAfterPw_AndOfMorePrograms(string commandLine, string expected)
        => Assert.Equal(expected, RunnerActivity.ShortArguments(commandLine));

    [Fact]
    public void JobLog_ShowsTheNewestPage_WhenTheProcessListCannotBeRead()
    {
        var root = NewRunnerRoot();
        try
        {
            WritePage(root, "a_1.log", "2026-10-19T12:00:00.0000000Z Compiling…\n", DateTime.UtcNow.AddHours(-2));

            var lines = RunnerActivity.ReadOutput(root, 10, () => throw new System.Runtime.InteropServices.COMException("WMI is not available."));

            Assert.Equal(new[] { "Compiling…", "" }, lines);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A web server on this PC that answers one request with headers for a large file and then sends a byte every
    /// <paramref name="byteEvery"/>, or nothing more when it is null.
    /// </summary>
    private static (string Url, Task Server, CancellationTokenSource Stop) SlowServer(TimeSpan? byteEvery)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                if (await stream.ReadAsync(new byte[8192], stop.Token) == 0) return;
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                await stream.WriteAsync(new byte[10], stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(byteEvery ?? Timeout.InfiniteTimeSpan, stop.Token);
                    await stream.WriteAsync(new byte[1], stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException) { }
            finally { listener.Stop(); }
        });
        return ($"http://127.0.0.1:{port}/actions-runner.zip", server, stop);
    }

    [Fact]
    public async Task RunnerDownload_ThatStalls_StopsWithAClearMessage()
    {
        var (url, server, stop) = SlowServer(null);
        var file = Path.Combine(Path.GetTempPath(), $"rm-audit-{Guid.NewGuid():N}.zip");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunnerService.DownloadFileAsync(url, file, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(700)));
            Assert.Contains("no data arrived", error.Message);
        }
        finally
        {
            stop.Cancel();
            await server;
            File.Delete(file);
        }
    }

    [Fact]
    public async Task RunnerDownload_ThatTrickles_StopsAtTheOverallLimit()
    {
        var (url, server, stop) = SlowServer(TimeSpan.FromMilliseconds(100));
        var file = Path.Combine(Path.GetTempPath(), $"rm-audit-{Guid.NewGuid():N}.zip");
        try
        {
            // A byte every 100 ms against a 30 s stall limit: only the overall limit can end it, also on a busy PC.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunnerService.DownloadFileAsync(url, file, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));
            Assert.Contains("did not finish within", error.Message);
        }
        finally
        {
            stop.Cancel();
            await server;
            File.Delete(file);
        }
    }

    // ----- Fleet health with GitHub -----

    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

    private static MachineInfo Pc(string name, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name, new MachineStatusPayload
        {
            MachineName = name, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = runners
        });

    private static MachineRunnerStatus Runner(string repository, long id, bool busy = false, DateTime? workerStarted = null)
        => new()
        {
            ServiceName = $"actions.runner.{repository.Replace('/', '-')}.r{id}", AgentName = $"r{id}", AgentId = id,
            RepositoryPath = repository, Scope = $"repos/{repository}", IsRunning = true, ServiceInstalled = true,
            ActivityText = busy ? "Busy" : "Idle", JobStartedUtc = workerStarted
        };

    private static RegisteredRunner Registered(long id, params string[] labels)
        => new(id, $"r{id}", new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase));

    private static GitHubJobState Queued(long id, DateTime nowUtc, params string[] labels)
        => new() { Id = id, Name = $"job{id}", WorkflowName = "CI", Status = "queued", CreatedUtc = nowUtc.AddMinutes(-10), Labels = labels };

    /// <param name="usual">The usual duration read: Read is false when GitHub could not be read. Without it, the jobs have no history.</param>
    private static JobHealth.GitHubReads Reads(Func<string, RepositoryWorkState?> jobs, Func<string, IReadOnlyList<RegisteredRunner>?> runners,
        Func<(bool Read, TimeSpan? Usual)>? usual = null)
        => new(repository => Task.FromResult(jobs(repository)), (_, _, _) => Task.FromResult(usual?.Invoke() ?? (true, null)),
            scope => Task.FromResult(runners(scope)));

    private static RepositoryWorkState Work(params GitHubJobState[] jobs)
    {
        var state = new RepositoryWorkState();
        state.Jobs.AddRange(jobs);
        return state;
    }

    [Fact]
    public async Task OneFailedGitHubRead_DoesNotMakeANormalLongJob_RunLong()
    {
        var now = DateTime.UtcNow;
        var worker = now.AddMinutes(-130);
        var machine = Pc("PC-LONG", Runner("audit/long", 11, busy: true, workerStarted: worker));
        var job = new GitHubJobState { Id = 501, Name = "build", WorkflowName = "CI", Status = "in_progress", RunnerId = 11, StartedUtc = worker, Labels = new[] { "self-hosted" } };
        var github = true;
        var reads = Reads(_ => github ? Work(job) : null, _ => null, () => (github, github ? TimeSpan.FromMinutes(90) : (TimeSpan?)null));
        var key = JobHealth.Key(machine.Runners[0]);

        // GitHub knows it usually takes 90 minutes, so 130 minutes is fine.
        Assert.False((await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key].IsProblem);
        // GitHub does not answer: the last known values are used, not the 2-hour limit.
        github = false;
        Assert.False((await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key].IsProblem);

        // A job that started since then, while GitHub does not answer, is not judged by the 2-hour limit either.
        var other = Pc("PC-LONG", Runner("audit/long", 11, busy: true, workerStarted: now.AddMinutes(-125)));
        Assert.False((await JobHealth.EvaluateAsync(new[] { other }, Quiet, null, reads, now)).Runners[key].IsProblem);
    }

    [Fact]
    public async Task RunningLongJob_IsTheSameAlert_WhetherGitHubAnswersOrNot()
    {
        var now = DateTime.UtcNow;
        var worker = now.AddMinutes(-40);
        var machine = Pc("PC-SUBJECT", Runner("audit/subject", 12, busy: true, workerStarted: worker));
        var job = new GitHubJobState { Id = 502, Name = "build", WorkflowName = "CI", Status = "in_progress", RunnerId = 12, StartedUtc = worker, Labels = new[] { "self-hosted" } };
        var github = true;
        var reads = Reads(_ => github ? Work(job) : null, _ => null, () => (true, TimeSpan.FromMinutes(10)));
        var key = JobHealth.Key(machine.Runners[0]);

        var first = (await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key];
        github = false;
        var second = (await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key];

        Assert.Equal(RunnerConcern.RunningLong, first.Concern);
        Assert.Equal(first.Subject, second.Subject);
        Assert.Contains("usually takes 10 min", second.Detail);
    }

    [Fact]
    public async Task FailedReadOfTheUsualDuration_IsNotTakenAsNoHistory()
    {
        var now = DateTime.UtcNow;
        var worker = now.AddMinutes(-130);
        var machine = Pc("PC-USUAL", Runner("audit/usual", 13, busy: true, workerStarted: worker));
        var job = new GitHubJobState { Id = 503, Name = "build", WorkflowName = "CI", Status = "in_progress", RunnerId = 13, StartedUtc = worker, Labels = new[] { "self-hosted" } };
        (bool Read, TimeSpan? Usual) usual = (false, null);
        var reads = Reads(_ => Work(job), _ => null, () => usual);
        var key = JobHealth.Key(machine.Runners[0]);

        // The job is listed, but its earlier runs cannot be read: 130 minutes is not judged by the 2-hour limit.
        Assert.False((await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key].IsProblem);
        Assert.False((await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, Reads(_ => null, _ => null), now)).Runners[key].IsProblem);

        // GitHub answers that the job has no history: then the 2-hour limit is the right one.
        usual = (true, null);
        var health = (await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key];
        Assert.Equal(RunnerConcern.RunningLong, health.Concern);
        Assert.Contains("longer than 2 h", health.Detail);
    }

    [Fact]
    public async Task UnservedJob_IsReported_OnlyWhenItWasMeantForTheFleet()
    {
        var now = DateTime.UtcNow;
        var machine = Pc("PC-UNSERVED", Runner("audit/unserved", 21));
        var jobs = Work(
            Queued(601, now, "self-hosted", "Linux"),          // a Linux runner's job
            Queued(602, now, "ubuntu-latest"),                 // a GitHub-hosted job
            Queued(603, now, "self-hosted", "gpu"),            // a runner outside the fleet has gpu
            Queued(604, now, "self-hosted", "Windows", "cuda") // nobody can take it
        );
        var reads = Reads(_ => jobs, scope => scope == "repos/audit/unserved"
            ? new[] { Registered(21, "self-hosted", "Windows", "X64"), Registered(99, "self-hosted", "gpu") }
            : null);

        var health = await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now);

        var unserved = Assert.Single(health.Unserved);
        Assert.Equal(604, unserved.JobId);
        Assert.Contains("No runner in the fleet has the labels self-hosted, Windows, cuda", unserved.Text);
    }

    [Fact]
    public async Task OtherRunner_WithTheSameIdAsAFleetRunnerOfAnotherRepository_StillCounts()
    {
        var now = DateTime.UtcNow;
        // Runner IDs are numbered per repository: ID 21 in audit/ids-b is not the fleet's runner 21 of audit/ids-a.
        var machine = Pc("PC-IDS", Runner("audit/ids-a", 21), Runner("audit/ids-b", 5));
        var reads = Reads(repository => repository == "audit/ids-b" ? Work(Queued(801, now, "self-hosted", "gpu")) : Work(),
            scope => scope == "repos/audit/ids-b" ? new[] { Registered(5, "self-hosted", "Windows", "X64"), Registered(21, "self-hosted", "gpu") } : null);

        Assert.Empty((await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Unserved);
    }

    [Fact]
    public async Task RunnerLabels_ComeFromGitHub_ForAnArm64PcAndForJobsWithoutSelfHosted()
    {
        var now = DateTime.UtcNow;
        var arm = Runner("audit/labels", 31);
        var bare = Runner("audit/labels", 32);
        var machine = Pc("PC-ARM64", arm, bare);
        var jobs = Work(Queued(701, now, "self-hosted", "Windows", "ARM64"), Queued(702, now, "unity"));
        var reads = Reads(_ => jobs, scope => scope == "repos/audit/labels"
            ? new[] { Registered(31, "self-hosted", "Windows", "ARM64", "unity"), Registered(32, "unity") }
            : null);

        var health = await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now);

        Assert.Equal(2, health.Queues[JobHealth.Key(machine.Runners.Single(r => r.AgentId == 31))].Count);
        // Registered without default labels: only the job that asks for unity.
        Assert.Single(health.Queues[JobHealth.Key(machine.Runners.Single(r => r.AgentId == 32))]);
        Assert.Equal(RunnerConcern.IdleWithWaitingJobs, health.Runners[JobHealth.Key(machine.Runners.Single(r => r.AgentId == 31))].Concern);
        Assert.Empty(health.Unserved);
    }

    [Theory]
    [InlineData(new[] { "self-hosted", "Windows" }, true)]
    [InlineData(new[] { "gpu" }, true)]
    [InlineData(new[] { "windows-latest" }, false)]
    [InlineData(new string[0], false)]
    public void Job_CanRun_WhenTheRunnerHasAllItsLabels(string[] runsOn, bool expected)
        => Assert.Equal(expected, RunnerLabels.CanRun(runsOn, RunnerLabels.Available(new[] { "gpu" })));

    [Fact]
    public void RegisteredRunnerList_HasAllLabels()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"total_count":1,"runners":[{"id":7,"name":"arm","labels":[{"id":1,"name":"self-hosted","type":"read-only"},
            {"id":2,"name":"Windows","type":"read-only"},{"id":3,"name":"ARM64","type":"read-only"},{"id":4,"name":"unity","type":"custom"}]}]}
            """);
        var runner = Assert.Single(RegisteredRunners.Parse(json.RootElement));
        Assert.Equal(7, runner.Id);
        Assert.True(runner.Labels.SetEquals(new[] { "self-hosted", "windows", "arm64", "Unity" }));
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

    [Fact]
    public void StartWithWindows_IsTheDelayedStart_ThatConfigCmdSets_AndStillReadsAsStartsWithWindows()
    {
        Assert.Equal("delayed-auto", ServiceStart.Automatic);
        // WMI reports a delayed automatic start as Auto.
        Assert.Equal(RunnerStart.WithWindows, RunnerService.ServiceStartsAt("Auto"));
    }
}

/// <summary>
/// The runner lists of the health checks. <see cref="GitHubCli.ForgetProblems"/> clears them, and the GitHub CLI tests
/// call it, so these tests do not run at the same time as those.
/// </summary>
[Collection(GitHubCliCollection.Name)]
public class RegisteredRunnerTests
{
    /// <summary>A scope no other test uses: "orgs/" and at most 39 characters, as GitHub allows for a name.</summary>
    private static string NewOrganization() => $"orgs/rr-{Guid.NewGuid():N}";

    private static Task<ConditionalResult> NotFound() => Task.FromResult(new ConditionalResult(404, string.Empty, """{"message":"Not Found"}"""));

    [Fact]
    public async Task OrganizationRunnerList_OfAPersonalAccount_IsNotAskedAgainSoon()
    {
        var scope = NewOrganization();
        var asked = 0;
        Task<ConditionalResult> Get(string endpoint, string? etag)
        {
            asked++;
            return NotFound();
        }
        var now = DateTime.UtcNow;

        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, now));
        // Past the one-minute cache, the 404 is still remembered.
        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, now.AddMinutes(30)));
        Assert.Equal(1, asked);
        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, now.AddHours(7)));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task RunnerList_ThatWasNotFound_IsAskedAgain_AfterForgetProblems()
    {
        var scope = NewOrganization();
        var answers = new Queue<Func<Task<ConditionalResult>>>(new Func<Task<ConditionalResult>>[]
        {
            NotFound,
            () => Task.FromResult(new ConditionalResult(200, "W/\"a\"", """{"total_count":1,"runners":[{"id":3,"name":"gpu","labels":[{"name":"gpu"}]}]}"""))
        });
        var asked = 0;
        Task<ConditionalResult> Get(string endpoint, string? etag)
        {
            asked++;
            return answers.Dequeue()();
        }
        var now = DateTime.UtcNow;

        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, now));
        // After "Connect GitHub" or a refresh by hand, the 404 and the read of the last minute are forgotten.
        GitHubCli.ForgetProblems();
        var list = await RegisteredRunners.GetAsync(scope, Get, now.AddSeconds(10));

        Assert.Equal(2, asked);
        Assert.Equal(3, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task NotFound_OfAReadSentBeforeForgetProblems_IsNotRemembered()
    {
        var scope = NewOrganization();
        var first = new TaskCompletionSource<ConditionalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        Task<ConditionalResult> Get(string endpoint, string? etag) => ++asked == 1 ? first.Task : NotFound();
        var now = DateTime.UtcNow;

        var sent = RegisteredRunners.GetAsync(scope, Get, now);
        while (asked == 0) await Task.Delay(10);
        GitHubCli.ForgetProblems();
        first.SetResult(new ConditionalResult(404, string.Empty, string.Empty));
        Assert.Null(await sent);

        // The answer came after the reset, so it is not kept for 6 hours.
        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, now.AddMinutes(2)));
        Assert.Equal(2, asked);
    }

    [Theory]
    [InlineData("orgs/octo --method DELETE")]
    [InlineData("repos/octo/app/actions")]
    [InlineData("users/octo")]
    [InlineData("")]
    public async Task RunnerList_OfAScopeThatIsNotAGitHubName_IsNotAsked(string scope)
    {
        var asked = 0;
        Task<ConditionalResult> Get(string endpoint, string? etag)
        {
            asked++;
            return NotFound();
        }

        Assert.Null(await RegisteredRunners.GetAsync(scope, Get, DateTime.UtcNow));
        Assert.Equal(0, asked);
    }
}
