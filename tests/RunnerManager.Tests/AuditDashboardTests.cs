using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RunnerManager.Tests;

/// <summary>
/// Runs WPF code on one STA thread that owns an <see cref="Application"/> with the app's color and theme dictionaries,
/// as the dashboard has them. Implicit styles in application resources behave differently from window resources, so
/// theme tests need a real Application.
/// </summary>
internal static class WpfTestApp
{
    private static readonly Lazy<Dispatcher> AppDispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        Exception? failure = null;
        AppDispatcher.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => { result = func(); });
        return result;
    }

    /// <summary>Lets the app thread run what was queued on it (Run itself would go first, at Send priority).</summary>
    public static void Flush() => AppDispatcher.Value.Invoke(() => { }, DispatcherPriority.ContextIdle);

    /// <summary>The last exception that escaped work queued on the app thread (it is caught, so the test run goes on).</summary>
    public static Exception? UnhandledError { get; set; }

    public static ResourceDictionary Load(string name)
        => new() { Source = new Uri($"pack://application:,,,/RunnerManager;component/Themes/{name}") };

    /// <summary>Puts the dark or light colors in place (index 0, as ThemeManager does). Call on the app thread.</summary>
    public static void UseColors(bool light)
        => Application.Current.Resources.MergedDictionaries[0] = Load(light ? "Colors.Light.xaml" : "Colors.Dark.xaml");

    private static Dispatcher Start()
    {
        if (Application.Current is { } existing)
        {
            existing.Dispatcher.Invoke(() => AddTheme(existing));
            return existing.Dispatcher;
        }
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                AddTheme(app);
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex) { failure = ex; }
            ready.Set();
            if (failure == null) Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();
        if (failure != null) throw new InvalidOperationException("Could not start the WPF test application.", failure);
        return dispatcher!;
    }

    private static void AddTheme(Application app)
    {
        app.Dispatcher.UnhandledException += (_, e) =>
        {
            UnhandledError = e.Exception;
            e.Handled = true;
        };
        if (app.Resources.MergedDictionaries.Count > 0) return;
        app.Resources.MergedDictionaries.Add(Load("Colors.Dark.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Theme.xaml"));
    }

    /// <summary>Lays out an element tree that is not in a window, which applies templates and styles.</summary>
    public static T Layout<T>(T element) where T : FrameworkElement
    {
        element.Measure(new Size(800, 600));
        element.Arrange(new Rect(0, 0, 800, 600));
        element.UpdateLayout();
        return element;
    }

    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    public static Color Resource(string key) => ColorOf((Brush)Application.Current.FindResource(key));
}

/// <summary>
/// A dashboard sets process-wide settings (collapsed PCs and lists) that other tests change too, so these tests run
/// on their own, after the others.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class DashboardCollection
{
    public const string Name = "Dashboard";
}

[Collection(DashboardCollection.Name)]
public class AuditDashboardTests
{
    // ================= The dashboard window =================

    /// <summary>A dashboard that is not shown: it loads its XAML (every StaticResource must exist) but starts nothing.</summary>
    private static MainWindow NewDashboard() => WpfTestApp.Run(() => new MainWindow());

    [Fact]
    public async Task OperationLog_WorksFromABackgroundThread()
    {
        var window = NewDashboard();
        WpfTestApp.UnhandledError = null;
        var log = WpfTestApp.Run(() => window.OperationLog("Delete runner", operationId: 1));
        await Task.Run(() => log("Deleting the runner's files…"));
        WpfTestApp.Flush();
        Assert.Null(WpfTestApp.UnhandledError);
        WpfTestApp.Run(() =>
        {
            Assert.Equal("Deleting the runner's files…", window.ActivityLog[0].Message);
            // No operation runs any more, so a late message leaves the status bar alone.
            Assert.DoesNotContain("Deleting", window.StatusText.Text);
        });
    }

    // ================= Results of commands sent to other PCs =================

    private static MachineInfo Pc(int protocol = 14, bool online = true, FleetCommandResult[]? results = null, string[]? running = null) => new()
    {
        MachineKey = "build-01",
        MachineName = "BUILD-01",
        AgentProtocolVersion = protocol,
        UpdatedUtc = DateTime.UtcNow,
        FleetAgentRunning = online,
        RecentCommands = results ?? Array.Empty<FleetCommandResult>(),
        PendingCommandIds = running ?? Array.Empty<string>()
    };

    private static MainWindow.AwaitedCommand Sent(DateTime when) => new()
    {
        CommandId = "c1", MachineKey = "build-01", MachineName = "BUILD-01", What = "Start build-01-api on BUILD-01",
        QueuedUtc = when, ActiveUtc = when
    };

    [Fact]
    public void RemoteCommand_FailureGivesTheAgentsReason()
    {
        var pc = Pc(results: new[] { new FleetCommandResult { Id = "c1", Result = "runner Start failed: Access is denied." } });
        Assert.Equal((MainWindow.RemoteOutcome.Failed, "Access is denied."), MainWindow.CheckRemoteCommand(pc, Sent(DateTime.UtcNow), DateTime.UtcNow));
        Assert.True(pc.HasFinished("c1"));
    }

    [Fact]
    public void RemoteCommand_SuccessGivesTheAgentsResult()
    {
        var pc = Pc(results: new[] { new FleetCommandResult { Id = "C1", Result = "Runner start completed" } });
        Assert.Equal((MainWindow.RemoteOutcome.Succeeded, "Runner start completed"), MainWindow.CheckRemoteCommand(pc, Sent(DateTime.UtcNow), DateTime.UtcNow));
    }

    [Fact]
    public void RemoteCommand_WithoutAResult_WaitsWhileItRunsOrItsPcIsAway()
    {
        var now = DateTime.UtcNow;
        var command = Sent(now - TimeSpan.FromMinutes(5));
        // Still listed as running: it keeps waiting, and the wait starts again.
        Assert.Equal(MainWindow.RemoteOutcome.Waiting, MainWindow.CheckRemoteCommand(Pc(running: new[] { "c1" }), command, now).Outcome);
        Assert.Equal(now, command.ActiveUtc);
        Assert.Equal(MainWindow.RemoteOutcome.Waiting, MainWindow.CheckRemoteCommand(Pc(), command, now + TimeSpan.FromSeconds(30)).Outcome);
        // Not running and no result long after it ran: lost.
        var later = now + MainWindow.RemoteResultWait + TimeSpan.FromSeconds(1);
        Assert.Equal(MainWindow.RemoteOutcome.NoAnswer, MainWindow.CheckRemoteCommand(Pc(), command, later).Outcome);
        // An offline PC or an older agent that does not list its commands may still report it.
        Assert.Equal(MainWindow.RemoteOutcome.Waiting, MainWindow.CheckRemoteCommand(Pc(online: false), command, later).Outcome);
        Assert.Equal(MainWindow.RemoteOutcome.Waiting, MainWindow.CheckRemoteCommand(Pc(protocol: 11), command, later).Outcome);
        Assert.Equal(MainWindow.RemoteOutcome.Waiting, MainWindow.CheckRemoteCommand(null, command, later).Outcome);
        Assert.Equal(MainWindow.RemoteOutcome.Forgotten, MainWindow.CheckRemoteCommand(Pc(online: false), command, now + TimeSpan.FromHours(2)).Outcome);
    }

    [Fact]
    public void OlderAgents_ReportOnlyTheLastCommand()
    {
        var pc = new MachineInfo { LastCommandId = "c1", LastCommandResult = "Runner restart completed" };
        Assert.Equal("Runner restart completed", pc.CommandResult("c1"));
        Assert.Null(pc.CommandResult("c2"));
        Assert.Null(new MachineInfo().CommandResult(string.Empty));
    }

    /// <summary>The app's source folder, for checks of the XAML text.</summary>
    private static string SourceDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RunnerManager.sln"))) return Path.Combine(dir.FullName, "src", "RunnerManager");
            throw new DirectoryNotFoundException("The repository folder was not found.");
        }
    }

    private static string Xaml(string name) => File.ReadAllText(Path.Combine(SourceDir, name));

    // ================= Menu check marks =================

    [Fact]
    public void MenuItemsWithABoundCheck_AreCheckable()
    {
        // The menu template shows the check only for checkable items.
        var items = Regex.Matches(Xaml("MainWindow.xaml"), @"<MenuItem\b[^>]*IsChecked=""\{Binding[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Contains(@"IsCheckable=""True""", item));
    }

    private sealed class Flag { public bool Value { get; set; } = true; }

    [Fact]
    public void ClickingACheckableItem_KeepsTheBoundState()
        => WpfTestApp.Run(() =>
        {
            var item = new MenuItem { Header = "Start with Windows", IsCheckable = true, DataContext = new Flag() };
            item.SetBinding(MenuItem.IsCheckedProperty, new System.Windows.Data.Binding(nameof(Flag.Value)) { Mode = System.Windows.Data.BindingMode.OneWay });
            var check = WpfTestApp.Descendants<TextBlock>(WpfTestApp.Layout(new Border { Child = item })).First(t => t.Text == "");
            Assert.Equal(Visibility.Visible, check.Visibility);
            // What a click does before the Click handler runs.
            item.SetCurrentValue(MenuItem.IsCheckedProperty, false);
            MainWindow.KeepBoundCheck(item);
            Assert.True(item.IsChecked);
            Assert.NotNull(item.GetBindingExpression(MenuItem.IsCheckedProperty));
        });

    // ================= Theme: control text colors =================

    private static TextBlock TextIn(FrameworkElement control, string text)
        => WpfTestApp.Descendants<TextBlock>(WpfTestApp.Layout(new Border { Child = control })).First(t => t.Text == text);

    private static Style Style(string key) => (Style)Application.Current.FindResource(key);

    [Theory]
    [InlineData("DangerButton", "DangerBrush")]
    [InlineData("GhostButton", "TextMutedBrush")]
    [InlineData("PrimaryButton", "OnAccentBrush")]
    [InlineData("SolidDangerButton", "OnAccentBrush")]
    public void ButtonStyles_ColorTheirTextContent(string style, string brush)
    {
        foreach (var light in new[] { false, true })
        {
            WpfTestApp.Run(() =>
            {
                WpfTestApp.UseColors(light);
                try
                {
                    var plain = TextIn(new Button { Content = "Delete", Style = Style(style) }, "Delete");
                    Assert.Equal(WpfTestApp.Resource(brush), WpfTestApp.ColorOf(plain.Foreground));

                    // Text and an icon put in the button by hand follow the button too.
                    var composite = new Button
                    {
                        Style = Style(style),
                        Content = new StackPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "", Style = Style("Icon") },
                                new TextBlock { Text = "Re-run" }
                            }
                        }
                    };
                    WpfTestApp.Layout(new Border { Child = composite });
                    foreach (var text in WpfTestApp.Descendants<TextBlock>(composite))
                        Assert.Equal(WpfTestApp.Resource(brush), WpfTestApp.ColorOf(text.Foreground));
                }
                finally { WpfTestApp.UseColors(light: false); }
            });
        }
    }

    [Fact]
    public void SegmentText_IsMutedUntilChecked()
        => WpfTestApp.Run(() =>
        {
            var segment = new RadioButton { Content = "Activity", Style = Style("Segment") };
            var text = TextIn(segment, "Activity");
            Assert.Equal(WpfTestApp.Resource("TextMutedBrush"), WpfTestApp.ColorOf(text.Foreground));
            segment.IsChecked = true;
            segment.UpdateLayout();
            Assert.Equal(WpfTestApp.Resource("TextBrush"), WpfTestApp.ColorOf(text.Foreground));
        });

    [Fact]
    public void DangerMenuItem_HeaderIsRed()
        => WpfTestApp.Run(() =>
        {
            var item = new MenuItem { Header = "Delete runner…", Style = Style("DangerMenuItem") };
            Assert.Equal(WpfTestApp.Resource("DangerBrush"), WpfTestApp.ColorOf(TextIn(item, "Delete runner…").Foreground));
        });

    [Fact]
    public void DashedRowAndNavItem_ColorTheirText()
        => WpfTestApp.Run(() =>
        {
            Assert.Equal(WpfTestApp.Resource("TextMutedBrush"),
                WpfTestApp.ColorOf(TextIn(new Button { Content = "Add a PC", Style = Style("DashedRow") }, "Add a PC").Foreground));
            var list = new ListBox { ItemContainerStyle = Style("NavItem"), ItemsSource = new[] { "WORKSTATION" } };
            Assert.Equal(WpfTestApp.Resource("TextBrush"), WpfTestApp.ColorOf(TextIn(list, "WORKSTATION").Foreground));
        });

    [Fact]
    public void TextWithItsOwnStyle_KeepsItsColorInsideControls()
        => WpfTestApp.Run(() =>
        {
            // The activity count in the Activity toggle is a caption in a ghost button.
            var ghost = new Button { Style = Style("GhostButton"), Content = new TextBlock { Text = "12", Style = Style("Caption") } };
            Assert.Equal(WpfTestApp.Resource("TextMutedBrush"), WpfTestApp.ColorOf(TextIn(ghost, "12").Foreground));
            var toggle = new CheckBox { Style = Style("ToggleSwitch"), Content = "Notifications" };
            Assert.Equal(WpfTestApp.Resource("TextBrush"), WpfTestApp.ColorOf(TextIn(toggle, "Notifications").Foreground));
            var menu = new MenuItem { Header = "Restart" };
            Assert.Equal(WpfTestApp.Resource("TextBrush"), WpfTestApp.ColorOf(TextIn(menu, "Restart").Foreground));
        });

    /// <summary>Text in other windows that has no color of its own keeps the theme's text color.</summary>
    [Fact]
    public void TextWithoutAColor_KeepsTheThemeTextColor()
        => WpfTestApp.Run(() =>
        {
            var text = WpfTestApp.Resource("TextBrush");
            Assert.Equal(text, WpfTestApp.ColorOf(TextIn(new StackPanel { Children = { new TextBlock { Text = "Plain" } } }, "Plain").Foreground));
            // A button style that only sets a template (as JoinFleetWindow's choice cards do).
            var template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            var card = new Button { Style = new Style(typeof(Button)) { Setters = { new Setter(Control.TemplateProperty, template) } },
                Content = new StackPanel { Children = { new TextBlock { Text = "Join an existing fleet" } } } };
            Assert.Equal(text, WpfTestApp.ColorOf(TextIn(card, "Join an existing fleet").Foreground));
            // A check box made in code (Settings, "Change fleet code").
            Assert.Equal(text, WpfTestApp.ColorOf(TextIn(new CheckBox { Content = "BUILD-01" }, "BUILD-01").Foreground));
        });

    // ----- Bug 6: GitHub sign-in is noticed soon after it ends -----

    [Fact]
    public void SignIn_ShowsThatItIsWaiting()
    {
        var window = NewDashboard();
        // Until gh has been asked, it does not say that GitHub is not connected.
        Assert.Equal("Checking…", WpfTestApp.Run(() => window.GitHubStatusText.Text));
        WpfTestApp.Run(window.WatchGitHubSignIn);
        Assert.Equal("Waiting for sign-in", WpfTestApp.Run(() => window.GitHubStatusText.Text));
    }

    // ----- Bug 7: the window fits its screen and opens where it was -----

    [Fact]
    public void FirstStart_FitsTheWindowOnAScaledScreen()
        => WpfTestApp.Run(() =>
        {
            // 1920 x 1080 at 150 % scale, with the taskbar.
            var window = new Window { Width = 1180, Height = 780 };
            WindowPlacement.Restore(window, null, new Rect(0, 0, 1280, 688));
            Assert.Equal(1180, window.Width);
            Assert.Equal(688, window.Height);
        });

    [Fact]
    public void SavedPlacement_IsRestored()
        => WpfTestApp.Run(() =>
        {
            var window = new Window { Width = 1180, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            WindowPlacement.Restore(window, new SavedWindowPlacement { Left = -1500, Top = 40, Width = 1400, Height = 900, Maximized = true },
                new Rect(0, 0, 1280, 688));
            Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
            Assert.Equal((-1500d, 40d, 1400d, 900d), (window.Left, window.Top, window.Width, window.Height));
            Assert.Equal(WindowState.Maximized, window.WindowState);
        });

    [Fact]
    public void FitFrame_ShrinksAndMovesOnlyWhatDoesNotFit()
    {
        var work = (0, 0, 1920, 1032);
        Assert.Equal((100, 50, 1770, 900), WindowPlacement.FitFrame((100, 50, 1770, 900), work, onScreen: true));
        Assert.Equal((100, 0, 1770, 1032), WindowPlacement.FitFrame((100, -40, 1770, 1170), work, onScreen: true));
        Assert.Equal((150, 0, 1770, 1032), WindowPlacement.FitFrame((400, -40, 1770, 1170), work, onScreen: true));
        Assert.Equal((75, 66, 1770, 900), WindowPlacement.FitFrame((-3000, 50, 1770, 900), work, onScreen: false));
        // A second screen to the left of the main one.
        Assert.Equal((-1280, 0, 1280, 688), WindowPlacement.FitFrame((-1300, -20, 1770, 1170), (-1280, 0, 1280, 688), onScreen: true));
    }

    [Fact]
    public void WindowPlacement_IsSavedWithTheSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rm-settings-{Guid.NewGuid():N}", "settings.json");
        try
        {
            new AppSettings { MainWindowPlacement = new SavedWindowPlacement { Left = 10, Top = 20, Width = 1000, Height = 600, Maximized = true } }.Save(path);
            var loaded = AppSettings.Load(path).MainWindowPlacement;
            Assert.NotNull(loaded);
            Assert.Equal((10d, 20d, 1000d, 600d, true), (loaded.Left, loaded.Top, loaded.Width, loaded.Height, loaded.Maximized));
            Assert.Null(AppSettings.Load(Path.Combine(Path.GetDirectoryName(path)!, "none.json")).MainWindowPlacement);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    // ----- Bug 8: labels of runners on other PCs -----

    [Fact]
    public void RunnerOnAnotherPc_ShowsAndMatchesItsLabels()
    {
        var runner = new MachineRunnerStatus { ServiceName = "actions.runner.app.build-01", DisplayName = "build-01", CustomLabels = new[] { "gpu", "cuda-12" } };
        Assert.Equal("gpu  ·  cuda-12", runner.LabelsText);
        Assert.Equal(Visibility.Visible, runner.LabelsVisibility);
        Assert.True(runner.Matches("CUDA"));
        Assert.False(runner.Matches("arm64"));
    }

    [Fact]
    public void RunnerOnAnotherPc_WithoutReportedLabels_HidesTheLabelsRow()
    {
        var runner = new MachineRunnerStatus { ServiceName = "actions.runner.app.build-01", DisplayName = "build-01" };
        Assert.Equal(Visibility.Collapsed, runner.LabelsVisibility);
    }

    // ----- Bug 13: search -----

    private static MachineInfo PcWith(string name, params MachineRunnerStatus[] runners)
        => MachineInfo.FromPayload(name.ToLowerInvariant(), new MachineStatusPayload
        {
            MachineName = name, AgentProtocolVersion = 14, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true, Runners = runners
        });

    private static MachineRunnerStatus RunnerWith(string name) => new() { ServiceName = $"actions.runner.{name}", DisplayName = name };

    [Fact]
    public void Search_ShowsAMatchInACollapsedCard()
        => WpfTestApp.Run(() =>
        {
            var window = new MainWindow();
            var pc = PcWith("BUILD-02", RunnerWith("gpu-box"));
            MachineInfo.CollapsedPcs.Add("BUILD-02");
            window.Machines.Add(pc);
            Assert.Equal(Visibility.Collapsed, pc.BodyVisibility);
            window.SearchBox.Text = "gpu";
            Assert.Equal(Visibility.Visible, pc.BodyVisibility);
            Assert.True(pc.IsCollapsed);
            window.SearchBox.Text = string.Empty;
            Assert.Equal(Visibility.Collapsed, pc.BodyVisibility);
            MachineInfo.CollapsedPcs.Remove("BUILD-02");
        });

    [Fact]
    public void Search_WithOnePcSelected_SaysWhereTheMatchesAre()
    {
        var window = NewDashboard();
        var first = PcWith("BUILD-01", RunnerWith("api"));
        var second = PcWith("BUILD-02", RunnerWith("gpu-box"));
        WpfTestApp.Run(() =>
        {
            foreach (var pc in new[] { first, second })
            {
                window.Machines.Add(pc);
                window.NavItems.Add(pc);
            }
        });
        // The sidebar's binding takes the new entries from the dispatcher.
        WpfTestApp.Flush();
        WpfTestApp.Run(() =>
        {
            window.MachineNav.SelectedItem = first;
            window.SearchBox.Text = "gpu";
            Assert.Equal(Visibility.Visible, window.NoResultsPanel.Visibility);
            Assert.Equal("Nothing on BUILD-01 matches “gpu”. Other PCs have matches. Select All PCs to see them.", window.NoResultsText.Text);
            window.SearchBox.Text = "arm64";
            Assert.Equal("Nothing on BUILD-01 matches “arm64”.", window.NoResultsText.Text);
            window.MachineNav.SelectedIndex = 0;
            Assert.Equal("No runners or PCs match “arm64”.", window.NoResultsText.Text);
            window.SearchBox.Text = "gpu";
            Assert.Equal(Visibility.Collapsed, window.NoResultsPanel.Visibility);
        });
    }

    // ----- Bug 14: the bottom panel leaves room for the cards -----

    [Fact]
    public void BottomPanel_NeverTakesTheCardsRoom()
    {
        var window = NewDashboard();
        WpfTestApp.Run(() =>
        {
            // A panel dragged to 600 px, in a window made small afterwards.
            window.ActivityRow.Height = new GridLength(600);
            // A window that is not shown does not lay out its content, so it is laid out on its own.
            var content = (UIElement)window.Content;
            window.Content = null;
            var host = new Border { Child = content };
            host.Measure(new Size(1000, 560));
            host.Arrange(new Rect(0, 0, 1000, 560));
            host.UpdateLayout();
            Assert.True(window.CardsRow.ActualHeight >= window.CardsRow.MinHeight, $"Cards row: {window.CardsRow.ActualHeight}");
            var total = window.ContentArea.RowDefinitions.Sum(r => r.ActualHeight);
            Assert.True(total <= 560.5, $"Rows: {total}");
            Assert.True(window.ActivityRow.ActualHeight >= 90, $"Bottom panel: {window.ActivityRow.ActualHeight}");
        });
    }

    // ----- Bug 15: texts -----

    [Fact]
    public void PcVersion_SaysWhatIsKnown()
    {
        Assert.Equal("Not reported", new MachineInfo { MachineName = "BUILD-01" }.VersionText);
        Assert.Equal("fleet agent protocol 7", new MachineInfo { MachineName = "BUILD-01", AgentProtocolVersion = 7 }.VersionText);
        Assert.Equal("v2.8.2", new MachineInfo { MachineName = "BUILD-01", AgentProtocolVersion = 15, AppVersion = "2.8.2" }.VersionText);
    }

    [Fact]
    public void SidebarSubtitle_CountsRunnersInWords()
    {
        Assert.Equal("No runners", PcWith("BUILD-01").SidebarSubtitle);
        Assert.Equal("0/1 runner running", PcWith("BUILD-01", RunnerWith("api")).SidebarSubtitle);
        Assert.Equal("0/2 runners running", PcWith("BUILD-01", RunnerWith("api"), RunnerWith("web")).SidebarSubtitle);
    }

    [Fact]
    public void Activity_KeepsAKindThatIsKnown()
    {
        var window = NewDashboard();
        WpfTestApp.Run(() =>
        {
            // The words alone would make this a warning.
            window.AddActivity("Turn off auto-recovery on BUILD-01 completed.", ActivityKind.Success);
            Assert.Equal(ActivityKind.Success, window.ActivityLog[0].Kind);
            window.AddActivity("Remove runner failed: access denied");
            Assert.Equal(ActivityKind.Error, window.ActivityLog[0].Kind);
        });
    }

    // ----- Item 16: keyboard access -----

    [Fact]
    public void ClickableElements_CanBeUsedFromTheKeyboard()
    {
        var xaml = Xaml("MainWindow.xaml");
        var clickable = Regex.Matches(xaml, @"<\w+\b[^>]*MouseLeftButtonUp=""[^""]+""[^>]*>");
        Assert.NotEmpty(clickable);
        Assert.All(clickable, m => Assert.Contains("KeyDown=", m.Value));
        // Keyboard focus is always visible.
        Assert.DoesNotContain(@"<Setter Property=""FocusVisualStyle"" Value=""{x:Null}""/>", xaml);
    }

    // ----- Item 17: contrast -----

    private static Color ColorIn(ResourceDictionary colors, string key) => WpfTestApp.ColorOf((Brush)colors[key]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaintText_IsReadableOnEveryBackground(bool light)
        => WpfTestApp.Run(() =>
        {
            var colors = WpfTestApp.Load(light ? "Colors.Light.xaml" : "Colors.Dark.xaml");
            var faint = ColorIn(colors, "TextFaintBrush");
            foreach (var background in new[] { "Bg0Brush", "Bg1Brush", "Bg2Brush", "Bg3Brush", "SidebarBrush", "SelectedBrush", "HoverBrush" })
            {
                var contrast = Palette.Contrast(faint, ColorIn(colors, background));
                Assert.True(contrast >= Palette.MinContrast, $"{background}: {contrast:F2}");
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusPills_AreReadableOnCardsAndRows(bool light)
        => WpfTestApp.Run(() =>
        {
            var colors = WpfTestApp.Load(light ? "Colors.Light.xaml" : "Colors.Dark.xaml");
            try
            {
                Palette.Use(light);
                // Palette mirrors the color dictionaries.
                Assert.Equal(new[] { ColorIn(colors, "Bg1Brush"), ColorIn(colors, "Bg2Brush") }, Palette.PillSurfaces);
                Assert.Equal(ColorIn(colors, "TextFaintBrush"), WpfTestApp.ColorOf(Palette.Muted));
                foreach (var status in new[] { Palette.Success, Palette.Danger, Palette.Warning, Palette.Info, Palette.Muted })
                {
                    var tint = WpfTestApp.ColorOf(Palette.Tint(status));
                    var text = WpfTestApp.ColorOf(Palette.OnTint(status));
                    foreach (var surface in Palette.PillSurfaces)
                    {
                        var contrast = Palette.Contrast(text, Palette.Over(tint, tint.A, surface));
                        Assert.True(contrast >= Palette.MinContrast, $"{WpfTestApp.ColorOf(status)} on {surface}: {contrast:F2}");
                    }
                }
            }
            finally { Palette.Use(false); }
        });

    // ----- Item 18: state in words -----

    [Fact]
    public void Sidebar_SaysWhenAPcIsOffline()
    {
        var online = PcWith("BUILD-01", RunnerWith("api"));
        Assert.Equal("0/1 runner running", online.SidebarSubtitle);
        var offline = MachineInfo.FromPayload("build-02", new MachineStatusPayload
        {
            MachineName = "BUILD-02", AgentProtocolVersion = 14, UpdatedUtc = DateTime.UtcNow.AddHours(-1), FleetAgentRunning = true,
            Runners = new[] { RunnerWith("web") }
        });
        Assert.Equal("Offline  ·  0/1 runner running", offline.SidebarSubtitle);
    }

    [Fact]
    public void CollapsedCard_SummarizesItsRunners()
    {
        var stuck = RunnerWith("api");
        stuck.Health = new RunnerHealth(RunnerConcern.Stuck, "Stuck", "No output for 30 minutes.");
        var pc = PcWith("BUILD-03", stuck, RunnerWith("web"));
        Assert.Equal(Visibility.Collapsed, pc.CollapsedSummaryVisibility);
        try
        {
            Assert.True(pc.ToggleCollapsed());
            Assert.Equal(Visibility.Visible, pc.CollapsedSummaryVisibility);
            Assert.Equal(Visibility.Visible, pc.ProblemBadgeVisibility);
            Assert.Equal("1 runner needs attention", pc.ProblemBadgeText);
            Assert.Equal("api: Stuck", pc.ProblemTip);
            // A search that opens the card shows the runners instead.
            pc.SetOpenForSearch(true);
            Assert.Equal(Visibility.Collapsed, pc.CollapsedSummaryVisibility);
        }
        finally { MachineInfo.CollapsedPcs.Remove("BUILD-03"); }
    }

    // ----- Item 19: cut text -----

    private static TextBlock Trimmed(Border host, double width, TextBlock block)
    {
        block.TextTrimming = TextTrimming.CharacterEllipsis;
        TrimmedText.SetToolTip(block, true);
        host.Child = block;
        host.Measure(new Size(width, 100));
        host.Arrange(new Rect(0, 0, width, 100));
        host.UpdateLayout();
        return block;
    }

    [Fact]
    public void CutText_ShowsTheWholeTextAsATooltip()
        => WpfTestApp.Run(() =>
        {
            const string title = "Build and test the Windows installer on every pull request";
            var row = new Border { ToolTip = "Open the job on GitHub" };
            var cut = Trimmed(row, 60, new TextBlock { Text = title });
            // Without a tooltip, none would open.
            Assert.NotNull(cut.ToolTip);
            Assert.Equal(title, TrimmedText.TipFor(cut));
            // When the text fits, the tooltip around it is shown, if there is one.
            Assert.Equal("Open the job on GitHub", TrimmedText.TipFor(Trimmed(row, 2000, new TextBlock { Text = title })));
            Assert.Null(TrimmedText.TipFor(Trimmed(new Border(), 2000, new TextBlock { Text = title })));
        });

    [Fact]
    public void CutText_CountsEveryRun()
        => WpfTestApp.Run(() =>
        {
            var block = new TextBlock();
            block.Inlines.Add(new System.Windows.Documents.Run("msbuild.exe") { FontWeight = FontWeights.SemiBold });
            block.Inlines.Add(new System.Windows.Documents.Run("  /m /p:Configuration=Release RunnerManager.sln"));
            const string whole = "msbuild.exe  /m /p:Configuration=Release RunnerManager.sln";
            Assert.Equal(whole, TrimmedText.FullText(block));
            var host = new Border();
            Assert.Equal(whole, TrimmedText.TipFor(Trimmed(host, 80, block)));
            Assert.Null(TrimmedText.TipFor(Trimmed(host, 2000, block)));
        });

    [Fact]
    public void OfflineReason_HasItsOwnLine()
    {
        const string problem = "Not reachable at 192.168.1.20:47800";
        var pc = MachineInfo.Placeholder("BUILD-04", "BUILD-04", DateTime.UtcNow.AddMinutes(-5), "192.168.1.20", problem);
        Assert.Equal("Not connected", pc.AgentText);
        Assert.Equal(problem, pc.ConnectionProblem);
        Assert.Equal(Visibility.Visible, pc.ProblemLineVisibility);
        Assert.Equal(Visibility.Collapsed, PcWith("BUILD-05").ProblemLineVisibility);
        // The line wraps, so a long reason is read in full.
        var line = Regex.Match(Xaml("MainWindow.xaml"), @"<TextBlock Text=""\{Binding ConnectionProblem\}""[^>]*>");
        Assert.True(line.Success);
        Assert.Contains(@"TextWrapping=""Wrap""", line.Value);
    }

    // ----- Item 20: output after the job -----

    private static MachineRunnerStatus JobRunner(string name, string output, bool busy = true, bool open = true) => new()
    {
        ServiceName = $"actions.runner.{name}", DisplayName = name, MachineKey = "BUILD-06", MachineName = "BUILD-06",
        IsRunning = true, ActivityText = busy ? "Busy" : "Idle", IsExpanded = open,
        LiveJob = busy ? new LiveJobView { Output = output } : null
    };

    [Fact]
    public void JobOutput_StaysUntilTheNextJob()
    {
        var live = new Dictionary<string, MainWindow.OutputCopy>(StringComparer.OrdinalIgnoreCase);
        var finished = new Dictionary<string, MainWindow.OutputCopy>(StringComparer.OrdinalIgnoreCase);
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "error CS1002: ; expected") }, live, finished);
        Assert.Single(live);
        Assert.Empty(finished);

        // The job ends: the runner is idle and has no output to show.
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "", busy: false) }, live, finished);
        Assert.Empty(live);
        var kept = Assert.Single(finished).Value;
        Assert.Equal("error CS1002: ; expected", kept.Text);
        Assert.Equal("api", kept.Title);

        // The next job replaces it.
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "Restoring packages") }, live, finished);
        Assert.Empty(finished);
        Assert.Equal("Restoring packages", Assert.Single(live).Value.Text);

        // Closing a runner that is running a job hides its output, and it is not kept.
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "Restoring packages", open: false) }, live, finished);
        Assert.Empty(live);
        Assert.Empty(finished);
    }

    [Theory]
    [InlineData(true, false, "Job finished")]
    [InlineData(true, true, "Job finished")]
    [InlineData(false, true, "Paused while text is selected")]
    [InlineData(false, false, "")]
    [InlineData(null, true, "")]
    public void OutputNote_SaysWhyTheOutputDoesNotChange(bool? finished, bool held, string note)
        => Assert.Equal(note, MainWindow.OutputNoteText(finished, held));

    // ----- Item 21: loading and empty states -----

    [Fact]
    public void FirstLoad_SaysItIsLoading()
    {
        var window = NewDashboard();
        WpfTestApp.Run(() =>
        {
            Assert.Equal(Visibility.Visible, window.LoadingPanel.Visibility);
            Assert.Equal("Loading…", window.StatusText.Text);
            // Not connected is not known yet, so there is nothing to connect.
            Assert.Equal(Visibility.Collapsed, window.ConnectGitHubButton.Visibility);
        });
    }

    [Fact]
    public void PcWithoutRunners_SaysWhatCanBeDone()
    {
        var local = PcWith(Environment.MachineName);
        Assert.Equal("No runners on this PC.", local.NoRunnersText);
        Assert.Equal(Visibility.Visible, local.AddRunnerVisibility);

        var online = MachineInfo.FromPayload("BUILD-08", new MachineStatusPayload
        {
            MachineName = "BUILD-08", AgentProtocolVersion = 14, UpdatedUtc = DateTime.UtcNow, FleetAgentRunning = true
        });
        Assert.Equal("No runners on BUILD-08.", online.NoRunnersText);
        // Runners are added on this PC.
        Assert.Equal(Visibility.Collapsed, online.AddRunnerVisibility);

        var offline = MachineInfo.Placeholder("BUILD-09", "BUILD-09", DateTime.UtcNow.AddHours(-2), "192.168.1.29", "Not reachable at 192.168.1.29:47800");
        Assert.Equal("BUILD-09 is not connected, so its runners are not known.", offline.NoRunnersText);
    }

    // ----- Item 22: smaller points -----

    [Fact]
    public void Sidebar_WithManyPcs_KeepsAddAPcInView_AndTabsToThePcsFirst()
    {
        var window = NewDashboard();
        WpfTestApp.Run(() =>
        {
            for (var i = 1; i <= 30; i++) window.NavItems.Add(PcWith($"BUILD-{i:D2}"));
        });
        WpfTestApp.Flush();
        WpfTestApp.Run(() =>
        {
            var content = (UIElement)window.Content;
            window.Content = null;
            // The list's items come from the window, which is not around it any more.
            var host = new Border { Child = content, DataContext = window };
            host.Measure(new Size(1000, 560));
            host.Arrange(new Rect(0, 0, 1000, 560));
            host.UpdateLayout();
            var bottom = window.AddPcRow.TranslatePoint(new Point(0, window.AddPcRow.ActualHeight), window.NavArea).Y;
            Assert.True(bottom <= LayoutInformation.GetLayoutSlot(window.NavArea).Height + 0.5, $"Add a PC ends at {bottom}");
            Assert.True(window.MachineNav.ActualHeight > 100, $"PC list: {window.MachineNav.ActualHeight}");
        });
        // The tab order follows the order in the file.
        var xaml = Xaml("MainWindow.xaml");
        Assert.True(xaml.IndexOf(@"x:Name=""MachineNav""", StringComparison.Ordinal) < xaml.IndexOf(@"Click=""OpenSettings_Click""", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerFacts_GiveWayToTheJobInASmallWindow()
    {
        var xaml = Xaml("MainWindow.xaml");
        Assert.DoesNotMatch(@"\sWidth=""340""", xaml);
        Assert.Contains(@"<ColumnDefinition Width=""*"" MinWidth=""220"" MaxWidth=""340""/>", xaml);
    }

    [Fact]
    public void IconButtons_HaveANameForScreenReaders()
    {
        var icons = Regex.Matches(Xaml("MainWindow.xaml"), @"<Button\b[^>]*IconButton[^>]*>");
        Assert.NotEmpty(icons);
        Assert.All(icons, m => Assert.Contains("AutomationProperties.Name=", m.Value));
        // A sidebar entry says which PC it is, not the name of a class.
        Assert.Equal("BUILD-01, No runners", PcWith("BUILD-01").ToString());
        Assert.Equal("All PCs, Loading…", new AllMachinesEntry().ToString());
    }

    [Theory]
    [InlineData("en-US", "2:05:09", "PM")]
    [InlineData("de-DE", "14:05:09", "")]
    public void Times_FollowTheRegionSettings(string culture, string start, string end)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture, useUserOverride: false);
            var time = DateTime.Today.AddHours(14).AddMinutes(5).AddSeconds(9);
            Assert.StartsWith(start, TimeFormat.WithSeconds(time));
            Assert.EndsWith(end, TimeFormat.WithSeconds(time));
            Assert.Equal(TimeFormat.WithSeconds(time), new ActivityEntry { Time = time }.TimeText);
            // Today, the time alone; another day, with the date.
            Assert.DoesNotContain(time.ToString("d", CultureInfo.CurrentCulture), TimeFormat.When(time));
            var earlier = time.AddDays(-3);
            Assert.Contains(earlier.ToString("d", CultureInfo.CurrentCulture), TimeFormat.When(earlier));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    // ----- Bug 10: keyboard shortcuts -----

    [Theory]
    [InlineData(Key.D0, ModifierKeys.Control, 0)]
    [InlineData(Key.D3, ModifierKeys.Control, 3)]
    [InlineData(Key.NumPad2, ModifierKeys.Control, 2)]
    [InlineData(Key.D2, ModifierKeys.Control | ModifierKeys.Alt, null)]
    [InlineData(Key.D2, ModifierKeys.Control | ModifierKeys.Shift, null)]
    [InlineData(Key.D2, ModifierKeys.None, null)]
    [InlineData(Key.A, ModifierKeys.Control, null)]
    public void CtrlAndADigit_SelectsASidebarEntry(Key key, ModifierKeys modifiers, int? index)
        => Assert.Equal(index, MainWindow.NavShortcutIndex(key, modifiers));

    // ================= Round 3 =================

    // ----- 1: the refresh does not wait for gh; checks that run beside it see newer readings -----

    private static MachineRunnerStatus RunnerIn(string state) => new()
    {
        ServiceName = "actions.runner.api", DisplayName = "api",
        IsRunning = state != "stopped", ActivityText = state == "busy" ? "Busy" : "Idle"
    };

    [Theory]
    [InlineData(RunnerConcern.Stuck, "busy", true)]
    [InlineData(RunnerConcern.Stuck, "idle", false)]
    [InlineData(RunnerConcern.RunningLong, "busy", true)]
    [InlineData(RunnerConcern.RunningLong, "stopped", false)]
    [InlineData(RunnerConcern.IdleWithWaitingJobs, "idle", true)]
    [InlineData(RunnerConcern.IdleWithWaitingJobs, "busy", false)]
    [InlineData(RunnerConcern.IdleWithWaitingJobs, "stopped", false)]
    public void JobHealth_FromAnEarlierReading_IsShownOnlyWhileItStillFits(RunnerConcern concern, string state, bool shown)
    {
        var health = new RunnerHealth(concern, "Label", "Detail.");
        var applied = MainWindow.StillApplies(health, RunnerIn(state));
        Assert.Equal(shown ? health : RunnerHealth.Healthy, applied);
    }

    // ----- 3: output of a job that ended -----

    private static (Dictionary<string, MainWindow.OutputCopy> Live, Dictionary<string, MainWindow.OutputCopy> Finished) Outputs()
        => (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void JobOutput_OfARunnerThatIsGone_IsNotKept()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "error CS1002: ; expected") }, live, finished);
        // The runner was deleted, or its PC is no longer listed.
        MainWindow.KeepFinishedOutput(Array.Empty<MachineRunnerStatus>(), live, finished);
        Assert.Empty(live);
        Assert.Empty(finished);
    }

    [Fact]
    public void FinishedOutput_GoesWithItsRunner()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "Build failed") }, live, finished);
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "", busy: false) }, live, finished);
        Assert.Single(finished);
        MainWindow.KeepFinishedOutput(new[] { JobRunner("web", "", busy: false) }, live, finished);
        Assert.Empty(finished);
    }

    [Fact]
    public void JobOutput_IsKeptWhenTheWorkerEndsBeforeTheRunnerIsIdle()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "error MSB3073: exited with code 1") }, live, finished);
        // The job's process has exited, so there is no output, but the runner still shows the job.
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "") }, live, finished);
        Assert.Equal("error MSB3073: exited with code 1", Assert.Single(live).Value.Text);
        Assert.Empty(finished);
        // Then the runner is idle.
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "", busy: false) }, live, finished);
        Assert.Empty(live);
        Assert.Equal("error MSB3073: exited with code 1", Assert.Single(finished).Value.Text);
    }

    [Fact]
    public void JobOutput_IsNotKeptWhenTheRunnerIsClosedAfterItsWorkerEnded()
    {
        var (live, finished) = Outputs();
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "Compiling") }, live, finished);
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "") }, live, finished);
        MainWindow.KeepFinishedOutput(new[] { JobRunner("api", "", open: false) }, live, finished);
        Assert.Empty(live);
        Assert.Empty(finished);
    }

    // ----- 5: JSON null in another PC's status -----

    [Fact]
    public void PcStatus_WithJsonNulls_IsShownWithoutFailing()
    {
        const string json = """
            {
              "MachineName": "BUILD-10", "AgentProtocolVersion": 14, "FleetAgentRunning": true,
              "MachineId": null, "AppVersion": null, "LastCommandId": null, "LastCommandResult": null, "SigningKeyId": null,
              "RecentCommands": [null, { "Id": null, "Result": null }], "PendingCommandIds": null,
              "Disks": [null], "RecentEvents": [null],
              "Runners": [
                null,
                { "ServiceName": null, "InstallPath": "C:\\runners\\api", "DisplayName": null, "RepositoryPath": null,
                  "Scope": null, "AgentName": null, "CustomLabels": null, "ActivityText": null, "StartsAt": null,
                  "RunnerVersion": null, "IsRunning": true },
                { "ServiceName": "actions.runner.web", "CustomLabels": ["gpu", null] }
              ]
            }
            """;
        var payload = System.Text.Json.JsonSerializer.Deserialize<MachineStatusPayload>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var pc = MachineInfo.FromPayload("build-10", payload with { UpdatedUtc = DateTime.UtcNow });

        Assert.Equal(2, pc.Runners.Length);
        Assert.Empty(pc.PendingCommandIds);
        Assert.Empty(pc.Disks);
        Assert.Empty(pc.RecentEvents);
        Assert.False(pc.IsRunningCommand("c1"));
        Assert.Null(pc.CommandResult("c1"));
        Assert.Equal(string.Empty, pc.LastResultText);
        Assert.Equal("fleet agent protocol 14", pc.VersionText);
        Assert.Equal("1/2 runners running", pc.SidebarSubtitle);

        var first = pc.Runners[0];
        Assert.Equal(string.Empty, first.Scope);
        Assert.Equal(string.Empty, first.AgentName);
        Assert.Equal(string.Empty, first.RepositoryPath);
        Assert.Empty(first.CustomLabels);
        Assert.False(first.IsBusy);
        Assert.Equal(RunnerStart.Unknown, first.StartsAt);
        Assert.Equal("C:\\runners\\api", first.CommandKey);
        Assert.Equal("Repository unknown", first.RepositoryText);
        Assert.False(first.Matches("gpu"));
        Assert.Equal(Visibility.Collapsed, first.LabelsVisibility);
        Assert.NotNull(first.StatusText);
        Assert.NotNull(first.RunnerVersionText);

        var second = pc.Runners[1];
        Assert.Equal(new[] { "gpu" }, second.CustomLabels);
        Assert.True(second.Matches("GPU"));
        Assert.All(pc.Runners, r => Assert.Equal("BUILD-10", r.MachineName));
    }

    // ----- 6: no fleet read while this PC joins or leaves the fleet -----

    [Theory]
    [InlineData("Leave fleet", false)]
    [InlineData("Join fleet", false)]
    [InlineData("Add runner", true)]
    [InlineData("Delete api", true)]
    public void FleetIsNotRead_WhileThisPcJoinsOrLeaves(string operation, bool reads)
        => Assert.Equal(reads, MainWindow.ReadsFleetDuring(operation));

    [Fact]
    public void JoiningAndLeaving_WaitForAFleetReadThatStartedBefore()
    {
        var code = File.ReadAllText(Path.Combine(SourceDir, "MainWindow.xaml.cs"));
        foreach (var title in new[] { "RunOperationAsync(LeaveFleetTitle", "RunOperationAsync(JoinFleetTitle" })
        {
            var start = code.IndexOf(title, StringComparison.Ordinal);
            Assert.True(start >= 0, title);
            var wait = code.IndexOf("await WaitForFleetReadAsync();", start, StringComparison.Ordinal);
            Assert.True(wait > start && wait - start < 300, title);
        }
    }
}
