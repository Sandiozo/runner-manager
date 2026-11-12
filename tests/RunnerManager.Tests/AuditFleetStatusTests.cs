using System.Text.Json;

namespace RunnerManager.Tests;

/// <summary>Another PC's status is checked before it is used: odd, older and hostile ones.</summary>
[Collection(FleetSettingsCollection.Name)]
public class PeerStatusTests
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };

    private static string UniqueName(string name) => $"{name}{Random.Shared.Next(100000, 999999)}";

    private static MachineRunnerStatus Runner(int n, string path) => new()
    {
        ServiceName = $"actions.runner.{n}", AgentName = $"runner-{n}", RepositoryPath = path, Scope = $"repos/{path}"
    };

    [Fact]
    public void AStatusWithOddEntries_StillReads_WithoutTheEntriesThatCannotBeRead()
    {
        const string json = """
            {"Ok":true,"Status":{"MachineName":"ODDPC","MachineId":null,"PendingCommandIds":null,
             "RecentCommands":[null,{"Id":null,"Result":"done"}],"Disks":null,"RecentEvents":[null,{"Text":null}],
             "Runners":[
               {"ServiceName":"actions.runner.a","RepositoryPath":"octo/repo","Scope":"repos/octo/repo","CustomLabels":["gpu",null,5]},
               {"ServiceName":"actions.runner.b","AgentId":"not a number"},
               null,
               5,
               {"ServiceName":null,"RepositoryPath":"javascript:alert(1)","Scope":"repos/../../x","CustomLabels":null,"StartsAt":null}
             ]}}
            """;
        var status = JsonSerializer.Deserialize<FleetResponse>(json, WireJson)!.Status!;

        Assert.Equal(3, status.UnreadableRunners);
        Assert.Equal(2, status.Runners.Length);
        Assert.Equal(new[] { "gpu" }, status.Runners[0].CustomLabels);
        Assert.Equal(string.Empty, status.Runners[1].ServiceName);
        Assert.Empty(status.Runners[1].CustomLabels);
        Assert.Equal(new MachineRunnerStatus().StartsAt, status.Runners[1].StartsAt);
        Assert.Equal(string.Empty, status.MachineId);
        Assert.Empty(status.PendingCommandIds);
        Assert.Equal(string.Empty, Assert.Single(status.RecentCommands).Id);
        Assert.Empty(status.Disks);
        Assert.Equal(string.Empty, Assert.Single(status.RecentEvents).Text);
    }

    [Fact]
    public void RunnersAsJsonNull_OrNotAList_ReadAsNone()
    {
        Assert.Empty(JsonSerializer.Deserialize<MachineStatusPayload>("""{"MachineName":"PC","Runners":null}""", WireJson)!.Runners);
        var odd = JsonSerializer.Deserialize<MachineStatusPayload>("""{"MachineName":"PC","Runners":{"a":1}}""", WireJson)!;
        Assert.Empty(odd.Runners);
        Assert.Equal(1, odd.UnreadableRunners);
    }

    [Fact]
    public void TheRunners_AreSentAsBefore()
    {
        var payload = new MachineStatusPayload { MachineName = "PC", Runners = new[] { Runner(1, "octo/repo") } };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var runner = Assert.Single(document.RootElement.GetProperty("Runners").EnumerateArray());
        Assert.Equal("octo/repo", runner.GetProperty("RepositoryPath").GetString());
        var back = JsonSerializer.Deserialize<MachineStatusPayload>(document.RootElement.GetRawText(), WireJson)!;
        Assert.Equal("actions.runner.1", Assert.Single(back.Runners).ServiceName);
        Assert.Equal(0, back.UnreadableRunners);
    }

    [Fact]
    public void ARepositoryOrOrganizationThatIsNotAGitHubName_IsBlanked_AndLoggedOncePerPc()
    {
        var name = UniqueName("HOSTILEPC");
        var payload = new MachineStatusPayload
        {
            MachineName = name,
            Runners = new[]
            {
                Runner(1, "octo/repo"),
                new MachineRunnerStatus { ServiceName = "actions.runner.2", RepositoryPath = "evil.example/../x\" onclick=\"", Scope = "orgs/evil.example/path" },
                new MachineRunnerStatus { ServiceName = "actions.runner.3", Scope = "orgs/octo" }
            }
        };
        var log = new List<string>();

        var used = PeerStatus.Checked(payload, log.Add);
        Assert.Equal(3, used.Runners.Length);
        Assert.Equal("octo/repo", used.Runners[0].RepositoryPath);
        Assert.Equal(string.Empty, used.Runners[1].RepositoryPath);
        Assert.Equal(string.Empty, used.Runners[1].Scope);
        Assert.Equal("actions.runner.2", used.Runners[1].ServiceName);
        Assert.Equal("orgs/octo", used.Runners[2].Scope);
        var line = Assert.Single(log);
        Assert.Contains(name, line);
        Assert.Contains("1 runner had a repository or organization that is not a GitHub name", line);

        // At every poll after that: checked the same way, logged no more.
        Assert.Equal(string.Empty, PeerStatus.Checked(payload, log.Add).Runners[1].Scope);
        Assert.Single(log);
    }

    [Fact]
    public void OnlySoManyRunnersAndRepositories_AreTakenFromOnePc()
    {
        var log = new List<string>();
        var many = new MachineStatusPayload
        {
            MachineName = UniqueName("MANYPC"),
            Runners = Enumerable.Range(0, PeerStatus.MaxRunners + 50).Select(n => Runner(n, "octo/repo")).ToArray()
        };
        Assert.Equal(PeerStatus.MaxRunners, PeerStatus.Checked(many, log.Add).Runners.Length);
        Assert.Contains($"more than {PeerStatus.MaxRunners} runners", Assert.Single(log));

        var spread = new MachineStatusPayload
        {
            MachineName = UniqueName("SPREADPC"),
            Runners = Enumerable.Range(0, PeerStatus.MaxRepositories + 20).Select(n => Runner(n, $"octo/repo-{n}")).ToArray()
        };
        var used = PeerStatus.Checked(spread, log.Add);
        Assert.Equal(PeerStatus.MaxRepositories, used.Runners.Select(r => r.RepositoryPath).Distinct().Count());
        Assert.Contains($"more than {PeerStatus.MaxRepositories} repositories", log[1]);

        // Nothing wrong: the status is used as it came.
        var fine = new MachineStatusPayload { MachineName = UniqueName("FINEPC"), Runners = new[] { Runner(1, "octo/repo") } };
        Assert.Same(fine, PeerStatus.Checked(fine, log.Add));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public async Task AnotherPcsStatus_IsCheckedWhenItIsTakenIn()
    {
        await AuditFleetConfig.WithConfigAsync(async () =>
        {
            var name = UniqueName("PEERPC");
            using var agent = new TestAgent(name);
            agent.Status = s => s with
            {
                Runners = Enumerable.Range(0, PeerStatus.MaxRunners + 1)
                    .Select(n => n == 0
                        ? new MachineRunnerStatus { ServiceName = "actions.runner.0", RepositoryPath = "not a repository", Scope = "repos/not a repository" }
                        : Runner(n, "octo/repo")).ToArray()
            };
            FleetConfig.SetFleetKey(agent.Key);
            FleetConfig.Update(c => c.KnownPeers.Add(new PeerRecord { Name = name, Address = agent.Address, LastSeenUtc = DateTime.UtcNow }));

            var listed = Assert.Single(await MachineRelay.ReadFleetAsync(), m => m.MachineName == name);
            Assert.Empty(listed.ConnectionProblem);
            Assert.Equal(PeerStatus.MaxRunners, listed.Runners.Length);
            Assert.Equal(string.Empty, listed.Runners[0].RepositoryPath);
            Assert.Equal(string.Empty, listed.Runners[0].RepositoryUrl);
            Assert.Equal(string.Empty, listed.Runners[0].Scope);
        });
    }
}
