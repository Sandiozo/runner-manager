using System.IO;
using Xunit;

namespace RunnerManager.Tests;

/// <summary>Add a PC: what is checked before Allow (round 4 of the review).</summary>
public class AddAPcCheckTests
{
    private const string OfficeId = "0123456789abcdef0123456789abcdef";
    private const string GuestId = "aaaa1111";
    private const string HostId = "fedcba9876543210fedcba9876543210";

    private static FleetConfig Fleet() => new()
    {
        KnownPeers = { new PeerRecord { Name = "OFFICE-PC", MachineId = OfficeId } },
        ManagedPcs = { new ManagedPcRecord { Name = "GUEST-PC", MachineId = GuestId } }
    };

    private static PairingHostWindow.RequestCheck Check(FleetConfig fleet, string name, string? machineId, params (string Name, string? MachineId)[] allowed)
        => PairingHostWindow.CheckRequest(fleet, name, machineId, allowed, "HOST-PC", HostId);

    // ----- The machine ID is never proof -----

    [Fact]
    public void TheNameAndTheIdOfAFleetMember_GetAWarning_ThatSaysWhatAllowingReplaces()
    {
        // Someone who saw an earlier pairing of OFFICE-PC can send its name and its ID.
        var check = Check(Fleet(), "OFFICE-PC", OfficeId);

        Assert.True(check.CanAllow);
        Assert.Equal("A PC named OFFICE-PC is already in this fleet, and the PC that asks says it is that PC. " +
                     "If you allow it, it replaces the saved OFFICE-PC. Allowed one way, it stops being a fleet member here. " +
                     "Allow only if this is the same PC.", check.Warning);
    }

    [Fact]
    public void TheNameAndTheIdOfAManagedPc_GetAWarning_ThatSaysWhatAllowingReplaces()
    {
        var check = Check(Fleet(), "guest_pc", GuestId.ToUpperInvariant());

        Assert.True(check.CanAllow);
        Assert.Equal("This fleet already manages a PC named guest_pc, and the PC that asks says it is that PC. " +
                     "If you allow it, it replaces the saved GUEST-PC. Allow only if this is the same PC.", check.Warning);
    }

    [Fact]
    public void TheNameAndTheIdOfAPcAllowedAMomentAgo_GetAWarning()
    {
        var check = Check(Fleet(), "LAPTOP", "1111", ("LAPTOP", "1111"));

        Assert.True(check.CanAllow);
        Assert.Contains("the PC that asks says it is that PC", check.Warning);
        Assert.Contains("Allow only if this is the same PC.", check.Warning);
    }

    [Fact]
    public void AFreeName_StillGetsNoWarning()
    {
        Assert.Equal(new PairingHostWindow.RequestCheck(null, CanAllow: true), Check(Fleet(), "LAPTOP", "4444"));
        Assert.Equal(new PairingHostWindow.RequestCheck(null, CanAllow: true), Check(Fleet(), "LAPTOP", null));
    }

    // ----- Characters that cannot be seen -----

    [Theory]
    [InlineData("OFFICE\u200B-PC")]   // zero-width space (format)
    [InlineData("OFFICE-PC\uFEFF")]   // zero-width no-break space (format)
    [InlineData("OFFICE-PC\U000E0041")] // tag letter A, outside the first 65536 code points (format)
    [InlineData("OFFICE-PC\u0007")]   // control
    [InlineData("OFFICE-\u2028PC")]   // line separator
    [InlineData("OFFICE-\u2029PC")]   // paragraph separator
    [InlineData("OFFICE-PC\u034F")]   // combining grapheme joiner (a mark that is not shown)
    [InlineData("OFFICE-PC\uFE0F")]   // variation selector (a mark that is not shown)
    [InlineData("OFFICE-PC\U000E0100")] // variation selector outside the first 65536 code points
    [InlineData("OFFICE-PC\u115F")]   // Hangul fillers
    [InlineData("OFFICE-PC\u1160")]
    [InlineData("OFFICE-PC\u3164")]
    [InlineData("OFFICE-PC\uFFA0")]
    public void ANameWithCharactersThatCannotBeSeen_CannotBeAllowed(string name)
    {
        var check = Check(new FleetConfig(), name, null);

        Assert.False(check.CanAllow);
        Assert.Equal("The name of OFFICE-PC contains characters that cannot be seen, so OFFICE-PC cannot be added. " +
                     "Rename the PC in Windows first.", check.Warning);
    }

    [Fact]
    public void ANameOfCharactersThatCannotBeSeenOnly_IsNotShownEmpty()
        => Assert.Equal(new PairingHostWindow.RequestCheck("The name of the PC that asks contains characters that cannot be seen, " +
                                                           "so the PC that asks cannot be added. Rename the PC in Windows first.", CanAllow: false),
            Check(new FleetConfig(), "\u200B\u3164", null));

    [Theory]
    [InlineData("J\u0100NIS-PC")]                    // JĀNIS-PC
    [InlineData("\u00D6STRA")]                       // ÖSTRA
    [InlineData("\u6771\u4EAC-PC")]                  // Tokyo in Japanese
    [InlineData("\u0395\u03BB\u03BB\u03AC\u03B4\u03B1")] // Greece in Greek, with a composed accent
    [InlineData("OFFICE PC 2")]
    // Marks that can be seen: names in these scripts cannot be written without them.
    [InlineData("JĀNIS-PC")]                   // JĀNIS-PC with A and a combining macron
    [InlineData("กิน-PC")]            // Thai, with a vowel sign above
    [InlineData("कुमार")]   // Devanagari, with a vowel sign below
    public void NamesInOtherAlphabets_CanStillBeAllowed(string name)
        => Assert.Equal(new PairingHostWindow.RequestCheck(null, CanAllow: true), Check(new FleetConfig(), name, null));

    // ----- One name written in two ways -----

    [Fact]
    public void AComposedName_IsWarnedAbout_WhenTheSavedNameIsDecomposed()
    {
        // A saved by discovery as A + combining macron; the PC asks with the composed Ā.
        var fleet = new FleetConfig { KnownPeers = { new PeerRecord { Name = "JA\u0304NIS-PC" } } };

        Assert.Equal(new PairingHostWindow.RequestCheck("A PC named J\u0100NIS-PC is already in this fleet. Allow only if this is the same PC.", CanAllow: true),
            Check(fleet, "J\u0100NIS-PC", null));
    }

    [Fact]
    public void TheWarning_ComparesNamesInNormalizationFormKC()
    {
        var fleet = new FleetConfig { ManagedPcs = { new ManagedPcRecord { Name = "J\u0100NIS-PC" } } };

        // Decomposed against composed.
        Assert.Equal("This fleet already manages a PC named JA\u0304NIS-PC. Allow only if this is the same PC.",
            PairingHostWindow.SameNameWarning(fleet, "JA\u0304NIS-PC", null, thisPc: "HOST-PC"));
        // Full-width letters.
        Assert.Equal("A PC named \uFF2F\uFF26\uFF26\uFF29\uFF23\uFF25-PC is already in this fleet. Allow only if this is the same PC.",
            Check(Fleet(), "\uFF2F\uFF26\uFF26\uFF29\uFF23\uFF25-PC", "4444").Warning);
    }

    [Fact]
    public void APcWithTheIdAndTheNameInAnotherForm_IsNamedOnce()
    {
        var fleet = new FleetConfig { KnownPeers = { new PeerRecord { Name = "JA\u0304NIS-PC", MachineId = OfficeId } } };

        var warning = Check(fleet, "J\u0100NIS-PC", OfficeId).Warning;

        Assert.StartsWith("A PC named J\u0100NIS-PC is already in this fleet, and the PC that asks says it is that PC.", warning);
        Assert.DoesNotContain("with a new name", warning);
    }

    // ----- Every PC with the ID is named -----

    [Fact]
    public void TheIdOfSeveralSavedPcs_NamesEachOfThem()
    {
        var fleet = Fleet();
        fleet.ManagedPcs[0].MachineId = OfficeId;
        fleet.KnownPeers.Add(new PeerRecord { Name = "OLD-NAME", MachineId = OfficeId });

        var check = Check(fleet, "LAPTOP", OfficeId, ("DESKTOP", OfficeId));

        Assert.True(check.CanAllow);
        Assert.Equal("LAPTOP sends the machine ID that these PCs have: OFFICE-PC (in this fleet), OLD-NAME (in this fleet), " +
                     "GUEST-PC (managed one way) and DESKTOP (allowed a moment ago). If you allow it, it takes the place of each of them. " +
                     "Allow only if one of them was renamed to LAPTOP.", check.Warning);
    }

    [Fact]
    public void TheIdOfOneSavedPc_KeepsTheWarningForOne()
        => Assert.Equal("LAPTOP says it is GUEST-PC, a PC that this fleet manages one way, with a new name. If you allow it, " +
                        "it takes the place of GUEST-PC. Allow only if GUEST-PC was renamed to LAPTOP.",
            Check(Fleet(), "LAPTOP", GuestId).Warning);

    // ----- Allow checks again -----

    [Fact]
    public void TheCheck_IsComparedByWhatItSays()
    {
        // Allow compares the check it makes with the one on screen: the same request against the same PCs is equal,
        // and a PC saved meanwhile makes it differ.
        var fleet = Fleet();
        var shown = Check(fleet, "LAPTOP", "4444");
        Assert.Equal(shown, Check(fleet, "LAPTOP", "4444"));

        fleet.KnownPeers.Add(new PeerRecord { Name = "LAPTOP", MachineId = "5555" });
        Assert.NotEqual(shown, Check(fleet, "LAPTOP", "4444"));
    }
}

/// <summary>Settings → AI assistants: the two switches (round 4 of the review).</summary>
[Collection(FleetSettingsCollection.Name)]
public class AssistantSwitchFormTests
{
    private static readonly string ConfigPath = Path.Combine(FleetConfig.StateDir, "fleet.json");

    [Fact]
    public void WithoutAdministratorRights_ItSaysWhatHelpsOnThatPage_AndChangesNothing()
    {
        using var switches = new TestSwitches(enabled: false, administrator: false);

        Assert.Equal(AssistantSwitchForm.NotAdministratorText, AssistantSwitchForm.Problem());
        Assert.Equal(AssistantSwitchForm.NotAdministratorText, AssistantSwitchForm.Set(enabled: true));
        Assert.False(AssistantSwitches.Current.Enabled);
        Assert.Contains("Start Runner Manager as administrator", AssistantSwitchForm.NotAdministratorText);
        Assert.DoesNotContain("Settings", AssistantSwitchForm.NotAdministratorText);
        Assert.DoesNotContain("n't", AssistantSwitchForm.NotAdministratorText);
    }

    [Fact]
    public void AWriteThatIsRefused_SaysWhatHelpsOnThatPage()
    {
        var before = AssistantSwitches.Store;
        AssistantSwitches.Store = new RefusingStore();
        try
        {
            Assert.Null(AssistantSwitchForm.Problem());
            Assert.Equal(AssistantSwitchForm.NotAdministratorText, AssistantSwitchForm.Set(askBeforeChanges: false));
        }
        finally { AssistantSwitches.Store = before; }
    }

    [Fact]
    public async Task AFleetJsonThatCannotBeRead_DoesNotBlockTheSwitches()
    {
        using var switches = new TestSwitches(enabled: false, askBeforeChanges: true);
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            FleetConfig.Update(c => c.Watchdog = false);
            using (new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Through fleet.json, the change fails.
                Assert.ThrowsAny<IOException>(() => FleetConfig.Update(c => c.Watchdog = true));

                Assert.Null(AssistantSwitchForm.Set(enabled: true));
                Assert.Null(AssistantSwitchForm.Set(askBeforeChanges: false));
            }
            Assert.Equal(new AssistantSwitchValues(Enabled: true, AskBeforeChanges: false), AssistantSwitches.Current);
            return Task.CompletedTask;
        });
    }

    /// <summary>A store that this process may write, by its rights, but whose write is refused.</summary>
    private sealed class RefusingStore : IAssistantSwitchStore
    {
        public string Where => "a test store";
        public bool CanWrite => true;
        public AssistantSwitchValues? Read() => null;
        public void Write(AssistantSwitchValues values) => throw new UnauthorizedAccessException("Access is denied.");
        public void Remove() { }
    }
}

/// <summary>Settings → Fleet → Enter fleet code: a code copied from another PC (round 4 of the review).</summary>
[Collection(FleetSettingsCollection.Name)]
public class PastedFleetCodeTests
{
    private const string RemovedId = "0123abcd";

    /// <summary>A PC in a fleet: it knows a PC, an address added by hand, a removed pairing and a PC it manages one way.</summary>
    private static byte[] InAFleet(byte[] key)
    {
        FleetConfig.SetFleetKey(key);
        FleetConfig.Update(c =>
        {
            c.KnownPeers.Add(new PeerRecord { Name = "OLDPC", Address = "10.0.0.5:47821", LastSeenUtc = DateTime.UtcNow });
            c.ManualAddresses.Add("old-pc.example.ts.net");
            c.RemovedManagedPcs.Add(new RemovedManagedPc { KeyId = RemovedId, RemovedUtc = DateTime.UtcNow });
        });
        var managed = CommandSigner.NewKey();
        FleetConfig.AddManagedPc("MANAGEDPC", "10.0.0.9", managed);
        return managed;
    }

    private static void AssertKeepsThePcsAndAddresses(FleetConfig c)
    {
        Assert.Equal("OLDPC", Assert.Single(c.KnownPeers).Name);
        Assert.Equal("old-pc.example.ts.net", Assert.Single(c.ManualAddresses));
        // Nothing is added: entries here go to the other PCs, and would remove the managed PCs across the fleet.
        Assert.Equal(RemovedId, Assert.Single(c.RemovedManagedPcs).KeyId);
    }

    [Fact]
    public async Task PastingAnotherCode_ForgetsTheManagedPcs_AndKeepsTheRest()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            InAFleet(CommandSigner.NewKey());
            var other = CommandSigner.NewKey();

            FleetConfig.SetFleetKey(other, pastedFromAnotherPc: true);

            var config = FleetConfig.Current;
            Assert.Equal(other, config.FleetKey);
            Assert.Empty(config.ManagedPcs);
            AssertKeepsThePcsAndAddresses(config);
            Assert.Empty(config.ManagedBy);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AfterAPastedOtherCode_TheFleetWithThatCodeGetsNoAccessCodes()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            InAFleet(CommandSigner.NewKey());
            Assert.Single(FleetConfig.Current.ShareManaged().Pcs);

            FleetConfig.SetFleetKey(CommandSigner.NewKey(), pastedFromAnotherPc: true);

            var (pcs, removed) = FleetConfig.Current.ShareManaged();
            Assert.Empty(pcs);
            Assert.Equal(RemovedId, Assert.Single(removed).KeyId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PastingTheSameCode_KeepsEverything()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var key = CommandSigner.NewKey();
            var managed = InAFleet(key);

            FleetConfig.SetFleetKey((byte[])key.Clone(), pastedFromAnotherPc: true);

            var config = FleetConfig.Current;
            Assert.Equal(managed, Assert.Single(config.ManagedPcs).Key);
            AssertKeepsThePcsAndAddresses(config);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ACodeSetWithoutTheFlag_KeepsTheManagedPcs()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var managed = InAFleet(CommandSigner.NewKey());

            // Change the fleet code on every PC, or a new code made on this PC.
            FleetConfig.SetFleetKey(CommandSigner.NewKey());

            var config = FleetConfig.Current;
            Assert.Equal(managed, Assert.Single(config.ManagedPcs).Key);
            Assert.Single(config.ShareManaged().Pcs);
            AssertKeepsThePcsAndAddresses(config);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PastingACode_OnAPcWithoutOne_WorksAsBefore()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            // The code removed last, entered again: the same fleet.
            var key = CommandSigner.NewKey();
            InAFleet(key);
            FleetConfig.SetFleetKey(null);
            FleetConfig.SetFleetKey(key, pastedFromAnotherPc: true);
            Assert.Single(FleetConfig.Current.ManagedPcs);
            AssertKeepsThePcsAndAddresses(FleetConfig.Current);

            // Another code: the earlier fleet goes.
            FleetConfig.SetFleetKey(null);
            FleetConfig.SetFleetKey(CommandSigner.NewKey(), pastedFromAnotherPc: true);
            var config = FleetConfig.Current;
            Assert.Empty(config.KnownPeers);
            Assert.Empty(config.ManualAddresses);
            Assert.Empty(config.ManagedPcs);
            Assert.Empty(config.RemovedManagedPcs);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PastingAFleetCode_OnAPcManagedOneWay_MakesItAFleetMember()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            FleetConfig.BecomeManaged(CommandSigner.NewKey(), "HOSTPC");
            var fleet = CommandSigner.NewKey();
            // It manages no PCs, so Settings does not ask.
            Assert.False(CodeRowText.PasteForgetsManagedPcs(FleetConfig.Current, fleet));

            FleetConfig.SetFleetKey(fleet, pastedFromAnotherPc: true);

            var config = FleetConfig.Current;
            Assert.Equal(fleet, config.FleetKey);
            Assert.Empty(config.ManagedBy);
            Assert.Empty(config.ManagedPcs);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Settings_AsksFirst_OnlyForAnotherCode_OnAPcThatManagesPcs()
    {
        await AuditFleetConfig.WithConfigAsync(() =>
        {
            var key = CommandSigner.NewKey();
            Assert.False(CodeRowText.PasteForgetsManagedPcs(FleetConfig.Current, key));

            InAFleet(key);
            Assert.True(CodeRowText.PasteForgetsManagedPcs(FleetConfig.Current, CommandSigner.NewKey()));
            Assert.False(CodeRowText.PasteForgetsManagedPcs(FleetConfig.Current, (byte[])key.Clone()));

            FleetConfig.Update(c => c.ManagedPcs.Clear());
            Assert.False(CodeRowText.PasteForgetsManagedPcs(FleetConfig.Current, CommandSigner.NewKey()));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void TheQuestion_SaysWhatIsForgotten_AndWhenItComesBack()
    {
        Assert.Equal("Use the new fleet code?", CodeRowText.PasteHeading);
        Assert.Equal("This PC manages GUEST-PC one way. With another fleet code, this PC forgets it, so the fleet that has the " +
                     "new code does not get its access code. If your fleet changed its code and this is the new one, it comes back " +
                     "from the other PCs of the fleet.", CodeRowText.PasteMessage(["GUEST-PC"]));
        var two = CodeRowText.PasteMessage(["GUEST-PC", "LAB-PC"]);
        Assert.StartsWith("This PC manages these PCs one way: GUEST-PC, LAB-PC. With another fleet code, this PC forgets them,", two);
        Assert.Contains("does not get their access codes", two);
        Assert.Contains("they come back from the other PCs of the fleet.", two);
        foreach (var text in new[] { CodeRowText.PasteHeading, two, CodeRowText.PasteConfirm })
        {
            Assert.DoesNotContain("n't", text);
            Assert.DoesNotContain("pairing code", text);
        }
    }
}
