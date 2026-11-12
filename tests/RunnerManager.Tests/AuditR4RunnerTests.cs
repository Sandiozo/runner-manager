using System.IO;

namespace RunnerManager.Tests;

/// <summary>Runner jobs, GitHub reads and the job view: the fixes from the fourth review round of 2.9.0.</summary>
public class AuditR4RunnerTests
{
    // ----- Secrets in the job view's process list -----

    [Theory]
    // The command lines of the review, and the secret each one holds.
    [InlineData(@"curl.exe -H ""x-functions-key: abc123"" https://example.azurewebsites.net/api/f", "abc123", "-H")]
    [InlineData(@"curl.exe -H ""Ocp-Apim-Subscription-Key: abc123"" https://example.azure-api.net/api", "abc123", "-H")]
    [InlineData(@"redis-cli -a Secret ping", "Secret", "")]
    [InlineData(@"ldapsearch -x -w Secret -b dc=example,dc=com", "Secret", "-x")]
    [InlineData(@"choco push --key KEY", "KEY", "push")]
    [InlineData(@"choco push -k KEY pkg", "KEY", "push")]
    [InlineData(@"rabbitmqctl add_user ci Secret", "Secret", "add_user")]
    [InlineData(@"ssh-keygen -N Secret -f k", "Secret", "")]
    [InlineData(@"smbclient //srv/share -U ci%Secret", "Secret", "")]
    [InlineData(@"vault login s.AbCdEfGhIjKlMnOpQrStUvWx", "s.AbCdEfGhIjKlMnOpQrStUvWx", "login")]
    [InlineData(@"tool call sk-proj-AbCdEfGhIjKlMnOpQrStUvWx0123", "sk-proj-AbCdEfGhIjKlMnOpQrStUvWx0123", "call")]
    public void ProcessArguments_LeaveOutTheSecretsTheReviewFound(string commandLine, string secret, string expected)
    {
        var shown = RunnerActivity.ShortArguments(commandLine);

        Assert.DoesNotContain(secret, shown);
        Assert.Equal(expected, shown);
    }

    [Theory]
    // API key headers, also as one quoted argument without a space, and with single quotes.
    [InlineData(@"curl.exe -H ""x-functions-key:abc123"" https://example.azurewebsites.net/api/f", "abc123", "-H")]
    [InlineData(@"curl.exe -H 'Ocp-Apim-Subscription-Key: abc123' https://example.azure-api.net/api", "abc123", "-H")]
    [InlineData(@"curl.exe --header=x-functions-key:abc123 https://example.azurewebsites.net/api/f", "abc123", "")]
    // The options of the table: with or without .exe, a path or quotes, any letter case, the value attached.
    [InlineData(@"REDIS-CLI.EXE -h cache -a Secret ping", "Secret", "-h cache")]
    [InlineData(@"""C:\Program Files\Redis\redis-cli.exe"" --pass Secret ping", "Secret", "")]
    [InlineData(@"redis-cli.exe -h cache -aSecret", "Secret", "-h cache")]
    [InlineData(@"cmd.exe /c redis-cli -a Secret ping", "Secret", "c redis-cli")]
    [InlineData(@"ldapsearch.exe -x -wSecret", "Secret", "-x")]
    [InlineData(@"ldapsearch -xw Secret -b dc=example", "Secret", "")]
    [InlineData(@"ldapmodify -x -w Secret -f change.ldif", "Secret", "-x")]
    [InlineData(@"ldapadd -x -w Secret -f add.ldif", "Secret", "-x")]
    [InlineData(@"ldapdelete -x -w Secret cn=old", "Secret", "-x")]
    [InlineData(@"LdapWhoAmI -x -w Secret", "Secret", "-x")]
    [InlineData(@"ldappasswd -x -w Secret -s NewSecret uid=ci", "Secret", "-x")]
    [InlineData(@"ssh-keygen.exe -p -P OldSecret -N NewSecret -f k", "Secret", "-p")]
    [InlineData(@"ssh-keygen -t ed25519 -NSecret", "Secret", "-t ed25519")]
    [InlineData(@"chocolatey.exe push pkg.nupkg -k=KEY", "KEY", "push pkg.nupkg")]
    [InlineData(@"choco push pkg.nupkg --api-key KEY -s https://push.example.org", "KEY", "push pkg.nupkg")]
    [InlineData(@"choco push --key=KEY", "KEY", "push")]
    [InlineData(@"tool.exe upload --key KEY", "KEY", "upload")]
    [InlineData(@"sshpass -p Secret ssh ci@host", "Secret", "")]
    // User and password with %, as Samba's tools take them.
    [InlineData(@"smbclient.exe -L //srv -Uci%Secret", "Secret", "-L")]
    [InlineData(@"rpcclient -U ci%Secret srv", "Secret", "")]
    [InlineData(@"rpcclient --user=ci%Secret srv", "Secret", "")]
    [InlineData(@"tool.exe sync -U ci%Secret", "Secret", "sync")]
    [InlineData(@"tool.exe sync -Uci%Secret", "Secret", "sync")]
    // smbclient takes the password as a plain argument after the share. (A share is a path root, so it has no file name to show.)
    [InlineData(@"smbclient //srv/share Secret -U ci", "Secret", "")]
    [InlineData(@"smbclient //srv/share/folder Secret -U ci", "Secret", "folder")]
    // rabbitmqctl: everything after the commands that take a password (two of them are also left out for their names).
    [InlineData(@"rabbitmqctl.bat -n rabbit@host add_user ci Secret", "Secret", "-n rabbit@host add_user")]
    [InlineData(@"rabbitmqctl.bat -n rabbit@host change_password ci Secret", "Secret", "-n rabbit@host")]
    [InlineData(@"RabbitMQCtl authenticate_user ci Secret", "Secret", "")]
    [InlineData(@"cmd.exe /c rabbitmqctl add_user ci Secret", "Secret", "c rabbitmqctl add_user")]
    // Raw tokens.
    [InlineData(@"vault.exe login hvb.AAAAAQJabc", "hvb.AAAAAQJabc", "login")]
    [InlineData(@"vault.exe operator generate-root -otp=hvr.AbC", "hvr.AbC", "operator generate-root")]
    [InlineData(@"vault.exe write -address=x value=s.AbCdEfGhIjKlMnOpQrStUvWx", "s.AbCdEfGhIjKlMnOpQrStUvWx", "write -address=x")]
    [InlineData(@"tool.exe call --value=sk-AbCdEfGhIjKlMnOpQrStUvWx", "sk-AbCdEfGhIjKlMnOpQrStUvWx", "call")]
    public void ProcessArguments_LeaveOutMoreForms(string commandLine, string secret, string expected)
    {
        var shown = RunnerActivity.ShortArguments(commandLine);

        Assert.DoesNotContain(secret, shown);
        Assert.Equal(expected, shown);
    }

    [Theory]
    // Ordinary arguments of the same programs stay.
    [InlineData(@"redis-cli.exe -h cache -p 6379 ping", "-h cache -p")]
    [InlineData(@"ldapsearch -x -W -b dc=example", "-x -W -b")]
    [InlineData(@"ssh-keygen -t ed25519 -f key", "-t ed25519 -f")]
    [InlineData(@"choco install git -y", "install git -y")]
    [InlineData(@"rabbitmqctl list_users", "list_users")]
    [InlineData(@"pip.exe install sk-video", "install sk-video")]
    [InlineData(@"tool.exe build s.version", "build s.version")]
    [InlineData(@"psql.exe -U postgres -d app", "-U postgres -d")]
    public void ProcessArguments_KeepOrdinaryArguments(string commandLine, string expected)
        => Assert.Equal(expected, RunnerActivity.ShortArguments(commandLine));

    // ----- The job log without the process list -----

    private static readonly DateTime Noon = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private static string NewRunnerRoot()
    {
        var root = Directory.CreateTempSubdirectory("rm-audit-r4-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "_diag", "pages"));
        return root;
    }

    private static void WritePage(string root, string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(root, "_diag", "pages", name);
        File.WriteAllText(path, text);
        File.SetCreationTimeUtc(path, writtenUtc);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static RunnerActivity.ProcessSnapshot NoProcessList() => throw new System.Runtime.InteropServices.COMException("WMI is not available.");

    [Fact]
    public void JobLog_WithoutTheProcessList_ShowsOnlyAPageWrittenInTheLastMinutes()
    {
        var root = NewRunnerRoot();
        try
        {
            // The last step of an earlier job, which the runner did not upload.
            WritePage(root, "earlier_1.log", "an earlier job\n", Noon);

            Assert.Equal(new[] { "an earlier job", "" }, RunnerActivity.ReadOutput(root, 10, NoProcessList, Noon.AddMinutes(4)));
            Assert.Empty(RunnerActivity.ReadOutput(root, 10, NoProcessList, Noon.AddMinutes(6)));
            Assert.Empty(RunnerActivity.ReadOutput(root, 10, NoProcessList, Noon.AddHours(3)));

            // A step that writes now.
            WritePage(root, "now_1.log", "this job\n", Noon.AddHours(3).AddSeconds(-20));
            Assert.Equal(new[] { "this job", "" }, RunnerActivity.ReadOutput(root, 10, NoProcessList, Noon.AddHours(3)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ----- The job a runner runs -----

    private static GitHubJobState Running(long id, DateTime startedUtc, string runnerName = "r14")
        => new() { Id = id, Name = $"job{id}", WorkflowName = "CI", Status = "in_progress", RunnerId = 14, RunnerName = runnerName,
                   StartedUtc = startedUtc, Labels = new[] { "self-hosted" } };

    [Fact]
    public void RunningJob_ThatStartedLongBeforeTheWorker_IsNotTheWorkersJob_AlsoWhenItIsTheOnlyOne()
    {
        var worker = Noon;
        var givenUp = Running(505, worker.AddHours(-3));

        // With more than 20 runs in progress, the run of the worker's own job may not be read.
        Assert.Null(GitHubApi.RunningJobOf(new[] { givenUp }, 14, "r14", worker));
        Assert.Null(GitHubApi.RunningJobOf(new[] { givenUp, Running(506, worker.AddMinutes(-40)) }, 14, "r14", worker));
        // Without the worker's start, it is still taken.
        Assert.Equal(505, GitHubApi.RunningJobOf(new[] { givenUp }, 14, "r14")!.Id);
    }

    [Theory]
    // Clocks differ a little: a job that started up to 5 minutes before the worker is its job.
    [InlineData(-4, 507L)]
    [InlineData(-5, 507L)]
    [InlineData(-6, null)]
    [InlineData(2, 507L)]
    public void RunningJob_ThatStartedShortlyBeforeTheWorker_IsTheWorkersJob(int minutes, long? expected)
        => Assert.Equal(expected, GitHubApi.RunningJobOf(new[] { Running(507, Noon.AddMinutes(minutes)) }, 14, "r14", Noon)?.Id);

    [Fact]
    public void RunningJob_OfSeveral_IsStillTheOneThatStartedClosestToTheWorker()
    {
        var jobs = new[] { Running(505, Noon.AddHours(-5)), Running(506, Noon.AddMinutes(-3)), Running(507, Noon.AddMinutes(20)) };

        Assert.Equal(506, GitHubApi.RunningJobOf(jobs, 14, "r14", Noon)!.Id);
    }

    private static MachineInfo Pc(string name, MachineRunnerStatus runner)
        => MachineInfo.FromPayload(name, new MachineStatusPayload
        {
            MachineName = name, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = new[] { runner }
        });

    private static MachineRunnerStatus Busy(string repository, long id, DateTime workerStarted)
        => new()
        {
            ServiceName = $"actions.runner.{repository.Replace('/', '-')}.r{id}", AgentName = $"r{id}", AgentId = id,
            RepositoryPath = repository, Scope = $"repos/{repository}", IsRunning = true, ServiceInstalled = true,
            ActivityText = "Busy", JobStartedUtc = workerStarted
        };

    private static RepositoryWorkState Work(params GitHubJobState[] jobs)
    {
        var state = new RepositoryWorkState();
        state.Jobs.AddRange(jobs);
        return state;
    }

    private static JobHealth.GitHubReads Reads(Func<RepositoryWorkState?> jobs, Func<(bool Read, TimeSpan? Usual)> usual)
        => new(_ => Task.FromResult(jobs()), (_, _, _) => Task.FromResult(usual()),
            _ => Task.FromResult<IReadOnlyList<RegisteredRunner>?>(null));

    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task BusyRunner_WhoseOnlyListedJobStartedHoursBeforeItsWorker_IsNotRunningLong()
    {
        var now = Noon;
        var machine = Pc("PC-R4-GIVENUP", Busy("audit-r4/given-up", 14, now.AddMinutes(-10)));
        var key = JobHealth.Key(machine.Runners[0]);
        var reads = Reads(() => Work(Running(505, now.AddHours(-3))), () => (true, TimeSpan.FromMinutes(20)));

        var health = (await JobHealth.EvaluateAsync(new[] { machine }, Quiet, null, reads, now)).Runners[key];

        Assert.False(health.IsProblem, health.Detail);
    }
}
