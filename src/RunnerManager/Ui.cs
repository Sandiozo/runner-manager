using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>Status colors used by view models; mirrors Themes/Colors.*.xaml and follows the active theme.</summary>
public static class Palette
{
    public static Brush Success { get; private set; } = Brush("#3FB950");
    public static Brush Danger { get; private set; } = Brush("#F85149");
    public static Brush Warning { get; private set; } = Brush("#D29922");
    public static Brush Info { get; private set; } = Brush("#4493F8");
    public static Brush Muted { get; private set; } = Brush("#8F96A0");
    public static Brush Text { get; private set; } = Brush("#E6EDF3");
    public static Brush TextMuted { get; private set; } = Brush("#9198A1");

    private static readonly Dictionary<Color, Brush> Tints = new();
    private static readonly Dictionary<Color, Brush> OnTints = new();
    private const byte TintAlpha = 0x26;
    /// <summary>WCAG AA for normal text.</summary>
    internal const double MinContrast = 4.5;
    private static bool _light;
    /// <summary>What pills sit on: a card (Bg1Brush) and an expanded or hovered runner row (Bg2Brush).</summary>
    internal static Color[] PillSurfaces { get; private set; } = { ParseColor("#161B22"), ParseColor("#1C2129") };

    public static void Use(bool light)
    {
        Tints.Clear();
        OnTints.Clear();
        _light = light;
        PillSurfaces = light ? new[] { ParseColor("#FFFFFF"), ParseColor("#F3F5F8") } : new[] { ParseColor("#161B22"), ParseColor("#1C2129") };
        Success = Brush(light ? "#1A7F37" : "#3FB950");
        Danger = Brush(light ? "#CF222E" : "#F85149");
        Warning = Brush(light ? "#9A6700" : "#D29922");
        Info = Brush(light ? "#0969DA" : "#4493F8");
        Muted = Brush(light ? "#606976" : "#8F96A0");
        Text = Brush(light ? "#1F2328" : "#E6EDF3");
        TextMuted = Brush(light ? "#59636E" : "#9198A1");
    }

    /// <summary>
    /// A translucent version of a status color, for pill backgrounds. Cached per color, so bindings
    /// get the same frozen brush on every refresh.
    /// </summary>
    public static Brush Tint(Brush brush)
    {
        if (brush is not SolidColorBrush solid) return Brushes.Transparent;
        if (Tints.TryGetValue(solid.Color, out var cached)) return cached;
        var tint = new SolidColorBrush(Color.FromArgb(TintAlpha, solid.Color.R, solid.Color.G, solid.Color.B));
        tint.Freeze();
        Tints[solid.Color] = tint;
        return tint;
    }

    /// <summary>
    /// Text in a status color on that color's <see cref="Tint"/>: the color made lighter (dark theme) or darker (light
    /// theme) only as far as a contrast of 4.5:1 on every pill surface needs. Most status colors on their tint are below it.
    /// </summary>
    public static Brush OnTint(Brush brush)
    {
        if (brush is not SolidColorBrush solid) return brush;
        if (OnTints.TryGetValue(solid.Color, out var cached)) return cached;
        var backgrounds = PillSurfaces.Select(surface => Over(solid.Color, TintAlpha, surface)).ToArray();
        var color = solid.Color;
        var toward = _light ? Colors.Black : Colors.White;
        for (var step = 1; step <= 100 && backgrounds.Any(bg => Contrast(color, bg) < MinContrast); step++)
            color = Over(toward, (byte)Math.Round(step * 2.55), solid.Color);
        var text = new SolidColorBrush(color);
        text.Freeze();
        OnTints[solid.Color] = text;
        return text;
    }

    /// <summary>The WCAG contrast ratio of two opaque colors, from 1 to 21.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var v = value / 255d;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="color"/> at <paramref name="alpha"/> over an opaque background.</summary>
    internal static Color Over(Color color, byte alpha, Color background)
    {
        var a = alpha / 255d;
        byte Mix(byte fg, byte bg) => (byte)Math.Round(fg * a + bg * (1 - a));
        return Color.FromRgb(Mix(color.R, background.R), Mix(color.G, background.G), Mix(color.B, background.B));
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public static class Menus
{
    /// <summary>Opens a button's context menu below it, optionally with the menu's right edge aligned to the button.</summary>
    public static void OpenBelow(Button button, bool alignRight)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 4;
        // The menu's width is only known once it's open.
        if (alignRight) menu.Opened += AlignRight;
        menu.IsOpen = true;

        void AlignRight(object? sender, RoutedEventArgs e)
        {
            menu.Opened -= AlignRight;
            menu.HorizontalOffset = button.ActualWidth - menu.ActualWidth;
        }
    }
}

/// <summary>
/// For a TextBlock with TextTrimming and no tooltip of its own: when the text is cut, its tooltip is the whole text.
/// When the text fits, it shows the tooltip of the element around it, if any.
/// </summary>
public static class TrimmedText
{
    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(bool), typeof(TrimmedText), new PropertyMetadata(false, OnToolTipChanged));

    public static bool GetToolTip(DependencyObject element) => (bool)element.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject element, bool value) => element.SetValue(ToolTipProperty, value);

    private static void OnToolTipChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text) return;
        text.ToolTipOpening -= Opening;
        if (e.NewValue is not true) return;
        // A tooltip only opens when there is one. What it shows is decided when it opens.
        text.ToolTip ??= " ";
        text.ToolTipOpening += Opening;
    }

    private static void Opening(object sender, ToolTipEventArgs e)
    {
        var text = (TextBlock)sender;
        if (TipFor(text) is { } tip) text.ToolTip = tip;
        else e.Handled = true;
    }

    internal static object? TipFor(TextBlock text)
    {
        var whole = FullText(text);
        return IsCut(text, whole) ? whole : ContainerToolTip(text);
    }

    internal static string FullText(TextBlock text)
    {
        var runs = Runs(text);
        return runs.Length > 0 ? string.Concat(runs.Select(r => r.Text)) : text.Text;
    }

    internal static bool IsCut(TextBlock text, string whole)
    {
        if (whole.Length == 0 || text.ActualWidth <= 0) return false;
        var formatted = new FormattedText(whole, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);
        // Runs can have their own font, such as a bold name before its details.
        var start = 0;
        foreach (var run in Runs(text))
        {
            var length = run.Text.Length;
            if (length == 0) continue;
            formatted.SetFontFamily(run.FontFamily, start, length);
            formatted.SetFontSize(run.FontSize, start, length);
            formatted.SetFontWeight(run.FontWeight, start, length);
            formatted.SetFontStyle(run.FontStyle, start, length);
            start += length;
        }
        return formatted.Width > text.ActualWidth - text.Padding.Left - text.Padding.Right + 0.5;
    }

    // Reading Inlines would turn a plain text into a Run; the logical children are only Runs when there are some.
    private static System.Windows.Documents.Run[] Runs(TextBlock text) =>
        LogicalTreeHelper.GetChildren(text).OfType<System.Windows.Documents.Run>().ToArray();

    private static object? ContainerToolTip(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (ToolTipService.GetToolTip(parent) is { } tip)
                // A ToolTip control can only belong to one element.
                return tip is string ? tip : null;
        return null;
    }
}

public enum AppTheme { System, Dark, Light }

/// <summary>Switches the color dictionary (index 0 of the app's merged dictionaries) between dark and light.</summary>
public static class ThemeManager
{
    public static bool IsLight { get; private set; }
    public static event Action? Changed;
    private static AppTheme _mode;
    private static bool _followingSystem;

    public static void Apply(AppTheme mode)
    {
        _mode = mode;
        // "System" follows Windows when the user switches between light and dark while Runner Manager runs.
        if (!_followingSystem && Application.Current != null)
        {
            _followingSystem = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        var light = mode == AppTheme.Light || (mode == AppTheme.System && SystemUsesLightTheme());
        IsLight = light;
        Palette.Use(light);
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is { Count: > 0 })
            dictionaries[0] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/RunnerManager;component/Themes/Colors.{(light ? "Light" : "Dark")}.xaml")
            };
        if (Application.Current != null)
            foreach (Window window in Application.Current.Windows) WindowTheme.Refresh(window);
        Changed?.Invoke();
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // The light/dark switch arrives as a General (ImmersiveColorSet) change.
        if (_mode != AppTheme.System || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_mode == AppTheme.System && SystemUsesLightTheme() != IsLight) Apply(AppTheme.System);
        });
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch { return false; }
    }
}

/// <summary>Keeps windows reachable when the screen they were on is gone.</summary>
public static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>
    /// Fits a window on a screen: one whose title bar is on no screen (for example after a monitor was unplugged while
    /// it was hidden in the notification area) moves to the middle of a screen, and one larger than its screen's work
    /// area (a small or highly scaled screen) shrinks to fit it.
    /// </summary>
    public static void EnsureOnScreen(Window window)
    {
        try
        {
            if (window.WindowState != WindowState.Normal) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            // A point in the title bar, which is what the user needs to reach to move the window.
            var titleBar = new Point { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top + 12 };
            var onScreen = MonitorFromPoint(titleBar, MonitorDefaultToNull) != IntPtr.Zero;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, onScreen ? MonitorDefaultToNearest : MonitorDefaultToPrimary), ref info)) return;
            // The window rectangle includes invisible resize borders; fit the visible frame.
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) != 0) frame = rect;
            var (x, y, width, height) = FitFrame(
                (frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top),
                (info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top), onScreen);
            if (x == frame.Left && y == frame.Top && width == frame.Right - frame.Left && height == frame.Bottom - frame.Top) return;
            SetWindowPos(hwnd, IntPtr.Zero,
                x - (frame.Left - rect.Left), y - (frame.Top - rect.Top),
                width + (rect.Right - rect.Left) - (frame.Right - frame.Left), height + (rect.Bottom - rect.Top) - (frame.Bottom - frame.Top),
                SwpNoZOrder | SwpNoActivate);
        }
        catch { }
    }

    /// <summary>
    /// Where a window's visible frame goes on a work area, in pixels: no larger than the work area, and inside it
    /// when it had to shrink. A frame that is not <paramref name="onScreen"/> moves to the middle.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) FitFrame(
        (int X, int Y, int Width, int Height) frame, (int X, int Y, int Width, int Height) work, bool onScreen)
    {
        var width = Math.Min(frame.Width, work.Width);
        var height = Math.Min(frame.Height, work.Height);
        if (!onScreen) return (work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
        if (width == frame.Width && height == frame.Height) return frame;
        return (Math.Clamp(frame.X, work.X, work.X + work.Width - width), Math.Clamp(frame.Y, work.Y, work.Y + work.Height - height), width, height);
    }

    /// <summary>
    /// Sets a window to its saved place and size before it is first shown. Without one, the window keeps its default
    /// size where that fits <paramref name="workArea"/>: 1920 × 1080 at 150 % scale leaves only about 1280 × 690.
    /// </summary>
    public static void Restore(Window window, SavedWindowPlacement? saved, System.Windows.Rect workArea)
    {
        if (saved is { Width: > 0, Height: > 0 } && double.IsFinite(saved.Left) && double.IsFinite(saved.Top))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = saved.Left;
            window.Top = saved.Top;
            window.Width = saved.Width;
            window.Height = saved.Height;
            if (saved.Maximized) window.WindowState = WindowState.Maximized;
            return;
        }
        if (workArea.Width > 0 && window.Width > workArea.Width) window.Width = workArea.Width;
        if (workArea.Height > 0 && window.Height > workArea.Height) window.Height = workArea.Height;
    }
}

/// <summary>Where the dashboard window was, in WPF units, so it opens there again.</summary>
public sealed class SavedWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Gives standard window chrome a title bar matching the app theme (Windows 10 20H1+/11).</summary>
public static class WindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window) => window.SourceInitialized += (_, _) => Refresh(window);

    public static void Refresh(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var light = ThemeManager.IsLight;
            var dark = light ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
            // COLORREF is 0x00BBGGRR.
            var caption = light ? 0x00FAF8F6 : 0x0017110D;   // #F6F8FA / #0D1117
            var border = light ? 0x00E0D9D1 : 0x003D3630;    // #D1D9E0 / #30363D
            var text = light ? 0x00282321 : 0x00F3EDE6;      // #1F2328 / #E6EDF3
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        }
        catch { }
    }
}

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(FleetConfig.StateDir, "settings.json");

    public bool Notifications { get; set; } = true;
    public bool NotifyOnFailedRuns { get; set; } = true;
    /// <summary>Alerts for stuck and long-running jobs, runners that idle while jobs wait, and jobs no runner takes.</summary>
    public bool NotifyOnJobProblems { get; set; } = true;
    /// <summary>A job that uses no CPU and writes no output for this many minutes counts as stuck.</summary>
    public int QuietMinutes { get; set; } = JobHealth.DefaultQuietMinutes;
    /// <summary>Closing the window hides it to the notification area, so monitoring continues. Exit from the tray icon.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool ActivityPanelOpen { get; set; } = true;
    public double ActivityPanelHeight { get; set; } = 130;
    /// <summary>PCs (by name) whose cards are collapsed to their header.</summary>
    public List<string> CollapsedPcs { get; set; } = new();
    /// <summary>The Queue and Recent runs lists in runners' details are hidden.</summary>
    public bool QueueCollapsed { get; set; }
    public bool RecentRunsCollapsed { get; set; }
    /// <summary>The setup wizard ran (installed copies only).</summary>
    public bool SetupCompleted { get; set; }
    public bool StartMinimizedHint { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public int RefreshSeconds { get; set; } = 5;
    public string DismissedUpdateVersion { get; set; } = string.Empty;
    /// <summary>Where the dashboard window was when it was last closed or hidden; null until then.</summary>
    public SavedWindowPlacement? MainWindowPlacement { get; set; }

    /// <summary>Why settings.json could not be used as it was, or null. The dashboard shows it once.</summary>
    public static string? LoadProblem { get; private set; }

    /// <summary>
    /// The file could not be read at all (for example it was locked), so saving is skipped: writing now would replace
    /// the user's settings with defaults.
    /// </summary>
    private bool _readFailed;

    public static AppSettings Load() => Load(SettingsPath);

    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        string json;
        try { json = ReadWithRetry(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = $"Could not read the settings ({ex.Message}). Defaults are used until Runner Manager restarts, and changes are not saved.";
            return new AppSettings { _readFailed = true };
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Keep what can still be read, and a copy of the damaged file.
            var copy = $"{path}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, copy, overwrite: true); } catch { copy = path; }
            settings = Salvage(json);
            LoadProblem = $"Some settings could not be read and were reset. A copy of the old settings file is in {copy}.";
            MachineRelay.AppendAgentLog($"settings.json could not be read; kept a copy in {copy}.");
        }
        settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 5, 60);
        settings.CollapsedPcs ??= new();
        settings.QuietMinutes = Math.Clamp(settings.QuietMinutes, 5, 240);
        return settings;
    }

    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100 * attempt); }
        }
    }

    /// <summary>Reads each setting on its own, so one bad value (or a cut-off file) does not reset the others.</summary>
    private static AppSettings Salvage(string json)
    {
        var settings = new AppSettings();
        var properties = typeof(AppSettings).GetProperties().Where(p => p.CanWrite).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json),
            new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return settings;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString() ?? string.Empty;
                if (!reader.Read()) break;
                if (properties.TryGetValue(name, out var property))
                {
                    var start = reader;
                    try
                    {
                        property.SetValue(settings, JsonSerializer.Deserialize(ref reader, property.PropertyType));
                        continue;
                    }
                    catch (JsonException) { reader = start; }
                }
                reader.Skip();
            }
        }
        // The file is cut off here: keep what was read before.
        catch (JsonException) { }
        return settings;
    }

    public void Save() => Save(SettingsPath);

    /// <summary>Writes a temporary file and moves it into place, so a crash or full disk never leaves a half-written file.</summary>
    internal void Save(string path)
    {
        if (_readFailed) return;
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5) { Thread.Sleep(100 * attempt); }
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { }
            MachineRelay.AppendAgentLog($"Could not save the settings: {ex.Message}");
        }
    }
}

public enum ActivityKind { Info, Success, Warning, Error }

public sealed class ActivityEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Message { get; init; } = string.Empty;
    public ActivityKind Kind { get; init; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Glyph => Kind switch
    {
        ActivityKind.Success => "",
        ActivityKind.Warning => "",
        ActivityKind.Error => "",
        _ => ""
    };

    public static ActivityKind Classify(string message)
    {
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return ActivityKind.Error;
        if (message.Contains("offline", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stopped", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || message.Contains("auto-recovery", StringComparison.OrdinalIgnoreCase)
            || message.Contains("skipped", StringComparison.OrdinalIgnoreCase)) return ActivityKind.Warning;
        if (message.Contains("completed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("back online", StringComparison.OrdinalIgnoreCase)
            || message.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("verified", StringComparison.OrdinalIgnoreCase)) return ActivityKind.Success;
        return ActivityKind.Info;
    }

    public override string ToString() => $"{TimeText}  {Message}";
}
