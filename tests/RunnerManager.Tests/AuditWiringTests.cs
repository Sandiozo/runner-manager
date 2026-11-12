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
    public void AddAPc_OnAManagedPc_SaysWhyItCannotAddPcs()
    {
        var text = MainWindow.ManagedCannotAddText("OFFICE-PC");

        Assert.Contains("OFFICE-PC's fleet manages this PC one way", text);
        Assert.Contains("Disconnect", text);
        Assert.DoesNotContain("n't", text);
    }
}
