using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
}
