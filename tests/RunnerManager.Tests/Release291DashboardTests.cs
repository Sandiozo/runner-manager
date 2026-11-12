using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RunnerManager.Tests;

/// <summary>
/// While this PC joins or leaves the fleet, no fleet read in the dashboard saves a PC, wherever it started: the main
/// window's refresh, Change fleet code or the address check in Settings.
/// </summary>
[Collection(FleetSettingsCollection.Name)]
public class FleetReadHoldTests
{
    private static void Reset() => FleetConfig.Update(c =>
    {
        c.KnownPeers.Clear();
        c.ProtectedFleetKey = string.Empty;
        c.ProtectedPendingFleetKey = string.Empty;
    });

    [Fact]
    public async Task WhileJoinOrLeaveRuns_AReadStartedAnywhere_IsRefused()
    {
        Assert.False(MachineRelay.FleetReadsHeld);
        using (var hold = await MachineRelay.HoldFleetReadsAsync(() => new TaskCompletionSource().Task))
        {
            // No read ran, so there was nothing to wait for.
            Assert.True(hold.EarlierReadsEnded);
            Assert.True(MachineRelay.FleetReadsHeld);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => MachineRelay.ReadFleetAsync(forceDiscovery: true));
            Assert.Equal(MachineRelay.FleetReadsHeldText, refused.Message);
            // Settings asks addresses that no known PC has with the same status request.
            refused = await Assert.ThrowsAsync<InvalidOperationException>(() => MachineRelay.ReadAddressesAsync(new[] { "127.0.0.1:1" }));
            Assert.Equal(MachineRelay.FleetReadsHeldText, refused.Message);
        }
        Assert.False(MachineRelay.FleetReadsHeld);
        // Once it is done, reads run again.
        Assert.Equal(3, await MachineRelay.StartFleetRead(() => Task.FromResult(3)));
    }

    [Fact]
    public async Task TheHold_WaitsForAReadThatStartedBefore()
    {
        var earlier = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = MachineRelay.StartFleetRead(() => earlier.Task);
        var holding = MachineRelay.HoldFleetReadsAsync(() => new TaskCompletionSource().Task);
        // New reads are held back at once, while the hold waits for the earlier one.
        Assert.True(MachineRelay.FleetReadsHeld);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MachineRelay.StartFleetRead(() => Task.FromResult(2)));
        await Task.Delay(50);
        Assert.False(holding.IsCompleted);
        earlier.SetResult(1);
        Assert.Equal(1, await read);
        using (var hold = await holding) Assert.True(hold.EarlierReadsEnded);
        Assert.False(MachineRelay.FleetReadsHeld);
    }

    [Fact]
    public async Task TheHold_GoesOnWithoutAReadThatDoesNotEnd()
    {
        var stuck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = MachineRelay.StartFleetRead(() => stuck.Task);
        var limit = new TaskCompletionSource();
        var holding = MachineRelay.HoldFleetReadsAsync(() => limit.Task);
        Assert.False(holding.IsCompleted);
        limit.SetResult();
        var hold = await holding;
        Assert.False(hold.EarlierReadsEnded);
        Assert.True(MachineRelay.FleetReadsHeld);
        hold.Dispose();
        // Released once, also when it is disposed twice: a second hold is kept.
        using (await MachineRelay.HoldFleetReadsAsync(() => limit.Task))
        {
            hold.Dispose();
            Assert.True(MachineRelay.FleetReadsHeld);
        }
        Assert.False(MachineRelay.FleetReadsHeld);
        stuck.SetResult(1);
        await read;
    }

    [Fact]
    public void WithoutAFleetCode_NoPcIsSaved_AlsoByAReadThatEndsAfterLeaveFleet()
    {
        try
        {
            Reset();
            // A read in another process (an AI assistant's) is not held back, but it cannot save a PC either.
            MachineRelay.RememberPeer("LATEPC", "10.0.0.9:47821", "2.9.0", "id-late");
            Assert.Empty(FleetConfig.Current.KnownPeers);
            var error = Assert.Throws<InvalidOperationException>(() => MachineRelay.AddKnownPeer("LATEPC", "10.0.0.9", "id-late"));
            Assert.Equal("This PC is not in a fleet any more.", error.Message);
            Assert.Empty(FleetConfig.Current.KnownPeers);

            // In a fleet, it is saved as before.
            FleetConfig.SetFleetKey(CommandSigner.NewKey());
            MachineRelay.RememberPeer("LATEPC", "10.0.0.9:47821", "2.9.0", "id-late");
            Assert.Equal("LATEPC", Assert.Single(FleetConfig.Current.KnownPeers).Name);
        }
        finally
        {
            Reset();
        }
    }

    [Fact]
    public void JoinAndLeave_HoldTheReads_AndTheWindowNoLongerTracksItsOwnRead()
    {
        var code = File.ReadAllText(Path.Combine(DashboardSource.Dir, "MainWindow.xaml.cs"));
        Assert.Equal(2, Regex.Matches(code, @"using var hold = await HoldFleetReadsAsync\(\);").Count);
        Assert.DoesNotContain("_fleetRead", code);
        var settings = File.ReadAllText(Path.Combine(DashboardSource.Dir, "SettingsWindow.xaml.cs"));
        Assert.DoesNotContain("FetchPeerAsync", settings);
    }
}

internal static class DashboardSource
{
    public static string Dir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RunnerManager.sln"))) return Path.Combine(dir.FullName, "src", "RunnerManager");
            throw new DirectoryNotFoundException("The repository folder was not found.");
        }
    }

    public static string Xaml(string name) => File.ReadAllText(Path.Combine(Dir, name));
}

/// <summary>A click that focuses a header or a row in the card list does not scroll the list; Tab still does.</summary>
public class StillOnClickTests
{
    [Fact]
    public void OnlyFocusFromAClick_KeepsTheListStill()
        => WpfTestApp.Run(() =>
        {
            var focused = new Button();
            var other = new Button();
            Assert.True(StillOnClick.KeepsStill(focused, focused, Mouse.PrimaryDevice));
            // Tab: the focused element is brought into view.
            Assert.False(StillOnClick.KeepsStill(focused, focused, Keyboard.PrimaryDevice));
            // No input yet, or a request that is not for the focused element (code that shows a part of the list).
            Assert.False(StillOnClick.KeepsStill(focused, focused, null));
            Assert.False(StillOnClick.KeepsStill(other, focused, Mouse.PrimaryDevice));
            Assert.False(StillOnClick.KeepsStill(null, null, Mouse.PrimaryDevice));
        });

    [Fact]
    public void ARequestFromAClick_DoesNotReachTheScrollViewer()
        => WpfTestApp.Run(() =>
        {
            var header = new Button();
            var panel = new StackPanel { Children = { header } };
            StillOnClick.SetIsEnabled(panel, true);
            var outside = new Border { Child = panel };
            var reached = 0;
            outside.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, _) => reached++));
            var lastInput = StillOnClick.LastInput;
            var focusedElement = StillOnClick.Focused;
            try
            {
                StillOnClick.Focused = () => header;
                StillOnClick.LastInput = () => Mouse.PrimaryDevice;
                header.BringIntoView();
                Assert.Equal(0, reached);

                StillOnClick.LastInput = () => Keyboard.PrimaryDevice;
                header.BringIntoView();
                Assert.Equal(1, reached);

                // Turned off, every request goes on.
                StillOnClick.LastInput = () => Mouse.PrimaryDevice;
                StillOnClick.SetIsEnabled(panel, false);
                header.BringIntoView();
                Assert.Equal(2, reached);
            }
            finally
            {
                StillOnClick.LastInput = lastInput;
                StillOnClick.Focused = focusedElement;
            }
        });

    [Fact]
    public void TheCardList_KeepsStillOnAClick()
        => Assert.Matches(@"<ScrollViewer Grid\.Row=""2""[^>]*>\s*(<!--[^>]*-->\s*)?<StackPanel local:StillOnClick\.IsEnabled=""True"">",
            DashboardSource.Xaml("MainWindow.xaml"));
}

/// <summary>The job title, the repository and the current job in a runner's details are buttons, so Tab and screen readers reach them.</summary>
public class DetailLinkTests
{
    [Fact]
    public void NoTextBlockActsAsALink()
    {
        var xaml = DashboardSource.Xaml("MainWindow.xaml");
        Assert.DoesNotMatch(@"<TextBlock\b[^>]*\b(MouseLeftButtonUp|KeyDown)=", xaml);
        Assert.DoesNotMatch(@"<TextBlock\b[^>]*Focusable=""True""", xaml);
        Assert.DoesNotContain("Link_KeyDown", xaml);
        foreach (var url in new[] { "LiveJob.Url", "RepositoryUrl", "LocalRunner.CurrentJobUrl" })
        {
            var link = Regex.Match(xaml, @"<Button Style=""\{StaticResource FlatButton\}""[^>]*Tag=""\{Binding " + Regex.Escape(url) + @"\}""[^>]*>");
            Assert.True(link.Success, url);
            Assert.Contains(@"Click=""LinkButton_Click""", link.Value);
            Assert.Matches(@"AutomationProperties\.Name=""\{Binding [^}]+\}""", link.Value);
            Assert.Matches(@"AutomationProperties\.HelpText=""Open the (job|repository) on GitHub""", link.Value);
        }
    }

    [Fact]
    public void TheCurrentJob_IsALinkOnlyWhenGitHubGaveItsAddress()
    {
        var idle = new MachineRunnerStatus { AgentName = "web", LocalRunner = new RunnerInfo() };
        Assert.Equal(Visibility.Collapsed, idle.CurrentJobLinkVisibility);
        Assert.Equal(Visibility.Visible, idle.CurrentJobTextVisibility);

        var busy = new MachineRunnerStatus { AgentName = "web", LocalRunner = new RunnerInfo { CurrentJobUrl = "https://github.com/octocat/web/actions/runs/1/job/2" } };
        Assert.Equal(Visibility.Visible, busy.CurrentJobLinkVisibility);
        Assert.Equal(Visibility.Collapsed, busy.CurrentJobTextVisibility);
    }

    [Fact]
    public void ALinkButton_IsReachedByTabAndNamedForScreenReaders()
        => WpfTestApp.Run(() =>
        {
            var link = new Button { Style = (Style)Application.Current.FindResource("FlatButton"), Tag = "https://github.com/octocat/web", Content = new TextBlock { Text = "octocat/web" } };
            System.Windows.Automation.AutomationProperties.SetName(link, "octocat/web");
            Assert.True(link.Focusable);
            Assert.True(link.IsTabStop);
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(link);
            Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Equal("octocat/web", peer.GetName());
        });
}
