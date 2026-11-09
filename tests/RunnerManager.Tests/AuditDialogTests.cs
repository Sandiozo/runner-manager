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
}
