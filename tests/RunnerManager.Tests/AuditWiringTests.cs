using Xunit;

namespace RunnerManager.Tests;

/// <summary>Places where the dashboard relies on what the fleet code says about a problem.</summary>
public class AuditWiringTests
{
    [Theory]
    [InlineData(FleetNetwork.CodeMismatchText)]
    [InlineData(FleetNetwork.NoCodeThereText)]
    [InlineData(FleetNetwork.NoCodeHereText)]
    [InlineData(FleetNetwork.ResponseNotVerifiedText)]
    // What a fleet agent of 2.4 to 2.8.2 says.
    [InlineData("The pairing code doesn't match. Use the same pairing code on every PC.")]
    public void APcWithACodeProblem_ShowsNotPaired(string problem)
    {
        var pc = new MachineInfo { MachineKey = "BUILD-01", MachineName = "BUILD-01", ConnectionProblem = problem };

        Assert.True(pc.IsPairingProblem);
        Assert.Equal("Not paired", pc.StatusText);
    }

    [Fact]
    public void AManagedPcThatRefusesItsAccessCode_ShowsNotPaired()
    {
        var pc = new MachineInfo { MachineKey = "FRIEND-PC", MachineName = "FRIEND-PC", ConnectionProblem = FleetNetwork.AccessCodeRefusedText("FRIEND-PC") };

        Assert.True(pc.IsPairingProblem);
        Assert.Equal("Not paired", pc.StatusText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Not reachable")]
    [InlineData("The fleet agent did not answer in time.")]
    public void APcWithAnotherProblem_DoesNotShowNotPaired(string problem)
    {
        var pc = new MachineInfo { MachineKey = "BUILD-01", MachineName = "BUILD-01", ConnectionProblem = problem };

        Assert.False(pc.IsPairingProblem);
        Assert.NotEqual("Not paired", pc.StatusText);
    }

    [Fact]
    public void EditRunner_RefusesToRemoveEveryLabel_OfARunnerWithoutDefaultLabels()
    {
        var result = EditRunnerForm.Check("build-01", ["gpu"], "build-01", "", canClearLabels: true, noDefaultLabels: true);

        // The same text RunnerService gives when it refuses the edit, so Save is off before anything is tried.
        Assert.Equal(RunnerService.NoLabelsLeftMessage, result.LabelsProblem);
        Assert.False(result.CanSave);
    }

    [Fact]
    public void EditRunner_StillRemovesEveryCustomLabel_OfARunnerWithDefaultLabels()
    {
        var result = EditRunnerForm.Check("build-01", ["gpu"], "build-01", "", canClearLabels: true, noDefaultLabels: false);

        Assert.Null(result.LabelsProblem);
        Assert.True(result.Request is { ClearLabels: true });
    }

    [Fact]
    public void EditRunner_KeepsALabel_OfARunnerWithoutDefaultLabels()
    {
        var result = EditRunnerForm.Check("build-01", ["gpu", "fast"], "build-01", "gpu", canClearLabels: true, noDefaultLabels: true);

        Assert.Null(result.LabelsProblem);
        Assert.True(result.CanSave);
    }

    [Fact]
    public void AddRunner_OnThisPcWithoutGitHub_SaysWhatToDo()
    {
        Assert.Contains("Connect GitHub first, or choose another PC", AddRunnerForm.ThisPcNeedsGitHub);
        Assert.DoesNotContain("n't", AddRunnerForm.ThisPcNeedsGitHub);
    }

    [Fact]
    public void Setup_OnAPcThatIsAlreadyInAFleet_DoesNotSendTheUserToAnotherPc()
    {
        foreach (var choice in Enum.GetValues<FleetChoice>())
        {
            var text = SetupWindow.WhatComesNext(choice, alreadyInFleet: true);

            Assert.StartsWith("This PC is already in a fleet and stays in it.", text);
            Assert.DoesNotContain("Add a PC", text);
        }
        // Where the PC stays in a fleet against the choice, the page also says how to leave it.
        Assert.Contains("Leave fleet", SetupWindow.WhatComesNext(FleetChoice.NewFleet, alreadyInFleet: true));
        Assert.Contains("Leave fleet", SetupWindow.WhatComesNext(FleetChoice.SinglePc, alreadyInFleet: true));
        Assert.DoesNotContain("Leave fleet", SetupWindow.WhatComesNext(FleetChoice.JoinFleet, alreadyInFleet: true));
    }

    [Fact]
    public void Setup_OnAPcThatIsInNoFleet_SaysWhatComesNext()
    {
        Assert.Contains("click “Add a PC” in the sidebar", SetupWindow.WhatComesNext(FleetChoice.JoinFleet, alreadyInFleet: false));
        Assert.Contains("starts a new fleet", SetupWindow.WhatComesNext(FleetChoice.NewFleet, alreadyInFleet: false));
        Assert.Equal(string.Empty, SetupWindow.WhatComesNext(FleetChoice.SinglePc, alreadyInFleet: false));
    }

    [Fact]
    public void FleetRowsInSettings_AreReadByWhatTheyShow_NotByTheirTypeName()
    {
        var pc = new SettingsWindow.PeerRow("BUILD-01", "10.0.0.5:47821", "Managed one way  ·  10.0.0.5:47821  ·  last seen never", Managed: true);
        var address = new SettingsWindow.AddressRow("build-02.example.ts.net", "No PC answered there.");

        // A list row without a name of its own is read with ToString, which for a record is its type name and fields.
        Assert.Equal("BUILD-01, Managed one way, 10.0.0.5:47821, last seen never", pc.ToString());
        Assert.Equal("build-02.example.ts.net, No PC answered there.", address.ToString());
    }

    [Fact]
    public void AddAPc_OnAManagedPc_SaysWhyItCannotAddPcs()
    {
        var text = MainWindow.ManagedCannotAddText("OFFICE-PC");

        Assert.Contains("OFFICE-PC's fleet manages this PC one way", text);
        Assert.Contains("Disconnect", text);
        Assert.DoesNotContain("n't", text);
    }
}
