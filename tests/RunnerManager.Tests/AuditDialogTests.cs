namespace RunnerManager.Tests;

public class AuditDialogTests
{
    // ----- Dialog default buttons -----

    [Theory]
    [InlineData(DialogTone.Info)]
    [InlineData(DialogTone.Question)]
    [InlineData(DialogTone.Warning)]
    [InlineData(DialogTone.Error)]
    public void OrdinaryDialog_EnterPressesThePrimaryButton(DialogTone tone)
        => Assert.Equal(DialogDefault.Primary, DialogRules.DefaultFor(tone, hasCancel: true));

    [Fact]
    public void DangerDialog_EnterCancels()
        => Assert.Equal(DialogDefault.Cancel, DialogRules.DefaultFor(DialogTone.Danger, hasCancel: true));

    [Fact]
    public void DangerDialogWithoutCancel_HasNoDefaultButton()
        => Assert.Equal(DialogDefault.None, DialogRules.DefaultFor(DialogTone.Danger, hasCancel: false));

    [Fact]
    public void CopiedDialogText_HasHeadingMessageAndDetail()
    {
        var nl = Environment.NewLine;
        Assert.Equal($"Add runner failed{nl}{nl}config.cmd exited with code 1.{nl}{nl}D:\\Runners\\app",
            DialogRules.CopyText("Add runner failed", "config.cmd exited with code 1. ", "D:\\Runners\\app"));
        Assert.Equal($"Done{nl}{nl}Saved.", DialogRules.CopyText("Done", "Saved.", null));
    }

    // ----- Settings → Fleet: addresses and Forget -----

    private static readonly string[] LocalAddresses = { "192.168.1.20", "100.64.1.2" };

    [Theory]
    [InlineData("office-pc")]
    [InlineData("office-pc.example.ts.net")]
    [InlineData("100.101.102.103")]
    [InlineData("100.101.102.103:47821")]
    [InlineData("build_box:8080")]
    public void AddressThatCanBeCalled_IsAccepted(string address)
        => Assert.Null(FleetAddressRules.Problem(address, "THIS-PC", LocalAddresses));

    [Theory]
    [InlineData("")]
    [InlineData("http://office-pc")]
    [InlineData("office-pc/rw")]
    [InlineData("office pc")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("[fd7a:115c:a1e0::1]:47821")]
    [InlineData("office-pc:")]
    [InlineData("office-pc:port")]
    [InlineData("office-pc:70000")]
    [InlineData("office;pc")]
    [InlineData("this-pc")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:47821")]
    [InlineData("192.168.1.20")]
    public void AddressThatCannotWork_IsRefused(string address)
        => Assert.NotNull(FleetAddressRules.Problem(address, "THIS-PC", LocalAddresses));

    [Fact]
    public void AddressStatus_NamesThePcThatAnswersThere()
    {
        var peers = new[] { new PeerRecord { Name = "OFFICE", Address = "office.example.ts.net:47821", LastSeenUtc = new DateTime(2026, 1, 2) } };
        Assert.Equal("OFFICE answers here  ·  last seen seen", FleetAddressRules.Status("office.example.ts.net", peers, null, _ => "seen"));
        Assert.Equal("No PC in this fleet answered. Not reachable at 10.0.0.9:47821.",
            FleetAddressRules.Status("10.0.0.9", peers, "Not reachable at 10.0.0.9:47821", _ => "seen"));
        Assert.Equal("Not checked yet.", FleetAddressRules.Status("10.0.0.9", peers, null, _ => "seen"));
    }

    [Fact]
    public void ForgettingAPc_DropsTheAddressItWasAddedBy()
    {
        var manual = new[] { "office.example.ts.net", "100.101.102.103:47821", "lab-pc" };
        Assert.Equal(new[] { "office.example.ts.net" }, ForgetPc.ManualAddressesFor(manual, new[] { "OFFICE.example.ts.net:47821", "" }));
        Assert.Equal(new[] { "100.101.102.103:47821" }, ForgetPc.ManualAddressesFor(manual, new[] { "100.101.102.103" }));
        Assert.Empty(ForgetPc.ManualAddressesFor(manual, new[] { "other-pc:47821" }));
    }

    [Fact]
    public void ForgetWording_MatchesTheDashboard()
    {
        Assert.Equal("Forget OFFICE?", ForgetPc.Heading("OFFICE"));
        Assert.Contains("still has the pairing code", ForgetPc.Message("OFFICE", online: true));
        Assert.Contains("appears again when it is found", ForgetPc.Message("OFFICE", online: false));
    }
}
