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
            Assert.True(StillOnClick.KeepsStill(focused, focused, Mouse.PrimaryDevice, pointerDown: true));
            // Tab: the focused element is brought into view.
            Assert.False(StillOnClick.KeepsStill(focused, focused, Keyboard.PrimaryDevice, pointerDown: false));
            // No input yet, or a request that is not for the focused element (code that shows a part of the list).
            Assert.False(StillOnClick.KeepsStill(focused, focused, null, pointerDown: false));
            Assert.False(StillOnClick.KeepsStill(other, focused, Mouse.PrimaryDevice, pointerDown: true));
            Assert.False(StillOnClick.KeepsStill(null, null, Mouse.PrimaryDevice, pointerDown: true));
        });

    [Fact]
    public void FocusAScreenReaderMovesAfterAClick_IsBroughtIntoView()
        => WpfTestApp.Run(() =>
        {
            var focused = new Button();
            // The mouse was used last, but no button is down any more: a screen reader (or code) moved the focus.
            Assert.False(StillOnClick.KeepsStill(focused, focused, Mouse.PrimaryDevice, pointerDown: false));
            Assert.False(StillOnClick.KeepsStill(focused, focused, Stylus.CurrentStylusDevice, pointerDown: false));
            // It reads the mouse buttons and the pen without a window.
            _ = StillOnClick.PointerDown();
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
            var pointerDown = StillOnClick.PointerDown;
            try
            {
                StillOnClick.Focused = () => header;
                StillOnClick.LastInput = () => Mouse.PrimaryDevice;
                StillOnClick.PointerDown = () => true;
                header.BringIntoView();
                Assert.Equal(0, reached);

                StillOnClick.LastInput = () => Keyboard.PrimaryDevice;
                StillOnClick.PointerDown = () => false;
                header.BringIntoView();
                Assert.Equal(1, reached);

                // After the click the mouse button is up: focus that a screen reader moves now is brought into view.
                StillOnClick.LastInput = () => Mouse.PrimaryDevice;
                header.BringIntoView();
                Assert.Equal(2, reached);

                // Turned off, every request goes on.
                StillOnClick.PointerDown = () => true;
                StillOnClick.SetIsEnabled(panel, false);
                header.BringIntoView();
                Assert.Equal(3, reached);
            }
            finally
            {
                StillOnClick.LastInput = lastInput;
                StillOnClick.Focused = focusedElement;
                StillOnClick.PointerDown = pointerDown;
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
    public void TheJobTitleAndTheRepository_AreLinksOnlyWithAnAddress()
    {
        var unknown = LiveJobView.Build(null, string.Empty, null, string.Empty, DateTime.UtcNow);
        Assert.Equal(string.Empty, unknown.Url);
        Assert.Equal(Visibility.Collapsed, unknown.TitleLinkVisibility);
        Assert.Equal(Visibility.Visible, unknown.TitleTextVisibility);
        var known = new LiveJobView { Title = "CI → build", Url = "https://github.com/octocat/web/actions/runs/1/job/2" };
        Assert.Equal(Visibility.Visible, known.TitleLinkVisibility);
        Assert.Equal(Visibility.Collapsed, known.TitleTextVisibility);

        var noRepository = new MachineRunnerStatus { AgentName = "web" };
        Assert.Equal("Repository unknown", noRepository.RepositoryText);
        Assert.Equal(Visibility.Collapsed, noRepository.RepositoryLinkVisibility);
        Assert.Equal(Visibility.Visible, noRepository.RepositoryTextVisibility);
        var repository = new MachineRunnerStatus { AgentName = "web", RepositoryPath = "octocat/web" };
        Assert.Equal(Visibility.Visible, repository.RepositoryLinkVisibility);
        Assert.Equal(Visibility.Collapsed, repository.RepositoryTextVisibility);
    }

    [Fact]
    public void AnOpenBusyRunner_ShowsNoLinkWithoutAnAddress_AndTheTextInstead()
    {
        var runner = Busy(repositoryPath: string.Empty, LiveJobView.Build(null, string.Empty, null, string.Empty, DateTime.UtcNow));
        WithCard(runner, host =>
        {
            // No button that would do nothing is shown, and none is announced as a link to GitHub.
            var links = LinksOf(runner, host);
            Assert.Contains(links, b => System.Windows.Automation.AutomationProperties.GetHelpText(b) == "Open the repository on GitHub");
            Assert.Contains(links, b => System.Windows.Automation.AutomationProperties.GetHelpText(b) == "Open the job on GitHub");
            Assert.All(links, b => Assert.False(b.ActualWidth > 0, $"{System.Windows.Automation.AutomationProperties.GetHelpText(b)} is shown"));
            Assert.True(ShownAsText(runner, host, "Running a job"));
            Assert.True(ShownAsText(runner, host, "Repository unknown"));
        });

        var linked = Busy(repositoryPath: "octocat/web", new LiveJobView { Title = "CI → build", Url = "https://github.com/octocat/web/actions/runs/1/job/2" });
        WithCard(linked, host =>
        {
            var shown = LinksOf(linked, host).Where(b => b.ActualWidth > 0).ToList();
            Assert.Equal(new[] { "https://github.com/octocat/web", "https://github.com/octocat/web/actions/runs/1/job/2" },
                shown.Select(b => (string)b.Tag).Order());
            Assert.False(ShownAsText(linked, host, "CI → build"));
            Assert.False(ShownAsText(linked, host, "octocat/web"));
        });
    }

    private static MachineRunnerStatus Busy(string repositoryPath, LiveJobView job) => new()
    {
        ServiceName = "actions.runner.web", DisplayName = "web", ServiceInstalled = true, IsRunning = true, ActivityText = "Busy",
        RepositoryPath = repositoryPath, LiveJob = job
    };

    private static void WithCard(MachineRunnerStatus runner, Action<FrameworkElement> check)
    {
        var pc = MachineInfo.FromPayload("build-60", new MachineStatusPayload
        {
            MachineName = "BUILD-60", AgentProtocolVersion = 14, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = new[] { runner }
        });
        var window = WpfTestApp.Run(() => new MainWindow());
        WpfTestApp.Run(() =>
        {
            runner.IsExpanded = true;
            window.Machines.Add(pc);
            var content = (UIElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content, DataContext = window };
            host.Measure(new Size(1200, 900));
            host.Arrange(new Rect(0, 0, 1200, 900));
            host.UpdateLayout();
            Assert.Equal(Visibility.Visible, runner.LiveJobVisibility);
            check(host);
        });
    }

    /// <summary>The link buttons in the runner's details: the job title, the repository and the current job of a runner on this PC.</summary>
    private static List<Button> LinksOf(MachineRunnerStatus runner, FrameworkElement host)
        => WpfTestApp.Descendants<Button>(host)
            .Where(b => ReferenceEquals(b.DataContext, runner)
                        && System.Windows.Automation.AutomationProperties.GetHelpText(b) is "Open the job on GitHub" or "Open the repository on GitHub")
            .ToList();

    /// <summary>The text is laid out outside any button.</summary>
    private static bool ShownAsText(MachineRunnerStatus runner, FrameworkElement host, string text)
        => WpfTestApp.Descendants<TextBlock>(host).Any(t => t.Text == text && t.ActualWidth > 0 && ReferenceEquals(t.DataContext, runner) && !InAButton(t));

    private static bool InAButton(DependencyObject element)
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(element); parent != null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is Button) return true;
        return false;
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
