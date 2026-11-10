using System.IO;

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
    [InlineData("JĀNIS-PC")]
    [InlineData("ОФИС-ПК:47821")]
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

    private static readonly PeerRecord[] Peers =
        { new() { Name = "OFFICE", Address = "office.example.ts.net:47821", LastSeenUtc = new DateTime(2026, 1, 2) } };

    private static readonly MachineInfo[] NoPcs = Array.Empty<MachineInfo>();

    private static MachineInfo NoAnswerAt(string address, string problem)
        => MachineInfo.Placeholder("ADDR_" + MachineRelay.Sanitize(address), address, DateTime.MinValue, address, problem);

    [Fact]
    public void AddressStatus_NamesThePcThatAnswersThere()
    {
        Assert.Equal("OFFICE answers here  ·  last seen seen",
            FleetAddressRules.Status("office.example.ts.net", Peers, Array.Empty<ManagedPcRecord>(), NoPcs, null, _ => "seen"));
        Assert.Equal("No PC in this fleet answered. Not reachable at 10.0.0.9:47821.",
            FleetAddressRules.Status("10.0.0.9", Peers, Array.Empty<ManagedPcRecord>(), new[] { NoAnswerAt("10.0.0.9", "Not reachable at 10.0.0.9:47821") }, null, _ => "seen"));
        Assert.Equal("Not checked yet.", FleetAddressRules.Status("10.0.0.9", Peers, Array.Empty<ManagedPcRecord>(), NoPcs, null, _ => "seen"));
    }

    [Fact]
    public void AddressStatus_ForAPcKnownAtAnotherAddress_NamesItAndWhereItIsListed()
    {
        // OFFICE answers at its Tailscale name, but the fleet lists it at its address on the local network.
        var peers = new[] { new PeerRecord { Name = "OFFICE", Address = "192.168.1.30:47821" } };
        var answer = new MachineInfo { MachineKey = MachineRelay.Sanitize("OFFICE"), MachineName = "OFFICE", Address = "office.example.ts.net" };
        Assert.Equal("OFFICE answers here. It is listed at 192.168.1.30:47821.",
            FleetAddressRules.Status("office.example.ts.net", peers, Array.Empty<ManagedPcRecord>(), NoPcs, answer, _ => "seen"));
        Assert.Equal("OFFICE answers here.",
            FleetAddressRules.Status("office.example.ts.net", Array.Empty<PeerRecord>(), Array.Empty<ManagedPcRecord>(), NoPcs, answer, _ => "seen"));
        // When it was asked, nothing answered.
        Assert.Equal("No PC in this fleet answered. The PC refused the fleet code.",
            FleetAddressRules.Status("10.0.0.9", peers, Array.Empty<ManagedPcRecord>(), NoPcs, NoAnswerAt("10.0.0.9", "The PC refused the fleet code"), _ => "seen"));
        // The newest refresh did not reach it, after it answered when it was asked.
        Assert.StartsWith("No PC in this fleet answered.",
            FleetAddressRules.Status("office.example.ts.net", peers, Array.Empty<ManagedPcRecord>(),
                new[] { NoAnswerAt("office.example.ts.net", "Not reachable") }, answer, _ => "seen"));
    }

    [Fact]
    public void AddressStatus_ForAManagedPc_SaysTheAddressIsNotNeeded()
    {
        var managed = new[] { new ManagedPcRecord { Name = "KIOSK", Address = "kiosk.example.ts.net:47821" } };
        Assert.Equal("This is the address of KIOSK, a PC managed one way. It is found without this address, so you can remove it.",
            FleetAddressRules.Status("KIOSK.example.ts.net", Peers, managed,
                new[] { NoAnswerAt("KIOSK.example.ts.net", "The PC refused the fleet code") }, null, _ => "seen"));
    }

    [Fact]
    public void ForgettingAPc_DropsTheAddressItWasAddedBy()
    {
        var manual = new[] { "office.example.ts.net", "100.101.102.103:47821", "lab-pc" };
        Assert.Equal(new[] { "office.example.ts.net" }, ForgetPc.ManualAddressesFor(manual, new[] { "OFFICE.example.ts.net:47821", "" }));
        Assert.Equal(new[] { "100.101.102.103:47821" }, ForgetPc.ManualAddressesFor(manual, new[] { "100.101.102.103" }));
        Assert.Empty(ForgetPc.ManualAddressesFor(manual, new[] { "other-pc:47821" }));
    }

    // ----- Add runner -----

    [Theory]
    [InlineData("owner/repo", "https://github.com/owner/repo")]
    [InlineData("owner/repo/", "https://github.com/owner/repo")]
    [InlineData(" owner/repo.git ", "https://github.com/owner/repo")]
    [InlineData("github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("www.github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/tree/main", "https://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("github.com/my-org", "https://github.com/my-org")]
    [InlineData("https://github.com/orgs/my-org", "https://github.com/my-org")]
    public void RepositoryInput_GivesTheRegistrationUrl(string text, string url)
    {
        var check = AddRunnerForm.CheckRepository(text);
        Assert.True(check.IsValid, check.Problem);
        Assert.Equal(url, check.Url);
        Assert.Null(RunnerService.RegistrationUrlProblem(check.Url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("git@gitlab.com:owner/repo.git")]
    [InlineData("https://github.com/own%22er/repo")]
    public void RepositoryInputThatCannotBeRegistered_IsNotValid(string text)
        => Assert.False(AddRunnerForm.CheckRepository(text).IsValid);

    [Fact]
    public void DefaultRunnerName_IsPcAndRepository_AndFreeForTheRepository()
    {
        var repo = AddRunnerForm.CheckRepository("owner/web.app");
        Assert.Equal("BUILD-PC-web.app", AddRunnerForm.DefaultRunnerName("BUILD-PC", repo, _ => false));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BUILD-PC-web.app", "build-pc-web.app-2" };
        Assert.Equal("BUILD-PC-web.app-3", AddRunnerForm.DefaultRunnerName("BUILD-PC", repo, taken.Contains));
        Assert.Equal("BUILD-PC-my-org", AddRunnerForm.DefaultRunnerName("BUILD-PC", AddRunnerForm.CheckRepository("github.com/my-org"), _ => false));
        Assert.Equal("BUILD-PC", AddRunnerForm.DefaultRunnerName("BUILD-PC", AddRunnerForm.CheckRepository(""), _ => false));
    }

    [Fact]
    public void DefaultRunnerName_IsAValidNameOfAtMost64Characters()
    {
        var repo = AddRunnerForm.CheckRepository("owner/" + new string('r', 80));
        var name = AddRunnerForm.DefaultRunnerName("PC NAME", repo, n => !n.EndsWith("-2"));
        Assert.Null(RunnerService.RunnerNameProblem(name));
        Assert.True(name.Length <= 64, name);
        Assert.EndsWith("-2", name);
        Assert.StartsWith("PC-NAME-", name);
    }

    [Fact]
    public void RunnerName_TakenForTheSameRepository_IsRefused()
    {
        var existing = new[] { ("repos/owner/repo", "BUILD-PC-repo") };
        Assert.NotNull(AddRunnerForm.NameProblem("build-pc-repo", "repos/owner/repo", existing));
        Assert.Null(AddRunnerForm.NameProblem("build-pc-repo", "repos/owner/other", existing));
        Assert.NotNull(AddRunnerForm.NameProblem("bad name", "repos/owner/repo", existing));
        Assert.NotNull(AddRunnerForm.NameProblem(" ", "repos/owner/repo", existing));
    }

    [Fact]
    public void TypedRunnerName_LongerThanGitHubAllows_IsRefused()
    {
        var longest = new string('r', AddRunnerForm.MaxNameLength);
        Assert.Null(AddRunnerForm.NameProblem(longest, "repos/owner/repo", Array.Empty<(string, string)>()));
        Assert.Equal("The name is too long. GitHub allows at most 64 characters.",
            AddRunnerForm.NameProblem(longest + "r", "repos/owner/repo", Array.Empty<(string, string)>()));
        Assert.Null(AddRunnerForm.NameProblem($"  {longest}  ", "repos/owner/repo", Array.Empty<(string, string)>()));

        var edit = EditRunnerForm.Check("pc-app", Array.Empty<string>(), longest + "r", "", false);
        Assert.Equal("The name is too long. GitHub allows at most 64 characters.", edit.NameProblem);
        Assert.False(edit.CanSave);
        Assert.True(EditRunnerForm.Check("pc-app", Array.Empty<string>(), longest, "", false).CanSave);
    }

    [Theory]
    [InlineData("MyRunner")]
    [InlineData(@"Runners\app")]
    [InlineData(@"D:\Runners\100%")]
    [InlineData(@"D:\Runners\a!b")]
    [InlineData("D:\\Runners\\\"app\"")]
    [InlineData(@"\\server\share\runner")]
    [InlineData("")]
    public void InstallFolderThatCannotBeUsed_IsRefusedBeforeTheDownload(string path)
        => Assert.NotNull(AddRunnerForm.FolderProblem(path));

    [Fact]
    public void InstallFolder_MustBeNewOrEmpty_AndTheSuggestionIsFree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-add-runner-{Guid.NewGuid():N}");
        try
        {
            Assert.Null(AddRunnerForm.FolderProblem(Path.Combine(root, "app")));
            Directory.CreateDirectory(Path.Combine(root, "app"));
            Assert.Null(AddRunnerForm.FolderProblem(Path.Combine(root, "app")));
            File.WriteAllText(Path.Combine(root, "app", "config.cmd"), string.Empty);
            Assert.NotNull(AddRunnerForm.FolderProblem(Path.Combine(root, "app")));
            Assert.Equal(Path.Combine(root, "app-2"), AddRunnerForm.FreeFolder(root, "app"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Labels_AreSplitTrimmedAndDeduplicated()
        => Assert.Equal(new[] { "gpu", "big" }, AddRunnerForm.ParseLabels(" gpu, ,big,GPU "));

    // ----- Edit runner -----

    [Fact]
    public void EditWithNothingChanged_CannotBeSaved()
    {
        var result = EditRunnerForm.Check("pc-app", new[] { "gpu", "big" }, " pc-app ", "big, gpu", canClearLabels: false);
        Assert.False(result.Changed);
        Assert.False(result.CanSave);
    }

    [Fact]
    public void EditWithANewNameOrLabels_CanBeSaved()
    {
        var renamed = EditRunnerForm.Check("pc-app", new[] { "gpu" }, "pc-app-2", "gpu", canClearLabels: false);
        Assert.Equal(new RunnerEditRequest("pc-app-2", new[] { "gpu" }).RunnerName, renamed.Request?.RunnerName);
        Assert.Equal(new[] { "gpu" }, renamed.Request?.Labels);
        Assert.False(renamed.Request?.ClearLabels);

        var relabelled = EditRunnerForm.Check("pc-app", new[] { "gpu" }, "pc-app", "gpu, big", canClearLabels: false);
        Assert.Equal(new[] { "gpu", "big" }, relabelled.Request?.Labels);
    }

    [Fact]
    public void EmptyingTheLabels_ClearsThemOnlyWhereThatIsPossible()
    {
        var cleared = EditRunnerForm.Check("pc-app", new[] { "gpu" }, "pc-app", " ", canClearLabels: true);
        Assert.True(cleared.Request?.ClearLabels);
        Assert.Empty(cleared.Request!.Labels);

        var refused = EditRunnerForm.Check("pc-app", new[] { "gpu" }, "pc-app", "", canClearLabels: false);
        Assert.False(refused.CanSave);
        Assert.Equal(EditRunnerForm.ClearNotPossible, refused.LabelsProblem);

        // Labels that were not known (an older agent) stay as they are when the box is left empty.
        var unknown = EditRunnerForm.Check("pc-app", Array.Empty<string>(), "pc-app-2", "", canClearLabels: false);
        Assert.True(unknown.CanSave);
        Assert.False(unknown.Request!.ClearLabels);
    }

    [Fact]
    public void EditWithAnInvalidNameOrLabel_CannotBeSaved()
    {
        Assert.NotNull(EditRunnerForm.Check("pc-app", Array.Empty<string>(), "pc app", "", false).NameProblem);
        Assert.NotNull(EditRunnerForm.Check("pc-app", Array.Empty<string>(), "", "", false).NameProblem);
        var badLabel = EditRunnerForm.Check("pc-app", Array.Empty<string>(), "pc-app", "gpu;rm", false);
        Assert.NotNull(badLabel.LabelsProblem);
        Assert.False(badLabel.CanSave);
    }

    [Fact]
    public void ForgetWording_MatchesTheDashboard()
    {
        Assert.Equal("Forget OFFICE?", ForgetPc.Heading("OFFICE"));
        Assert.Contains("still has the fleet code", ForgetPc.Message("OFFICE", online: true));
        Assert.Contains("appears again when it is found", ForgetPc.Message("OFFICE", online: false));
    }
}
