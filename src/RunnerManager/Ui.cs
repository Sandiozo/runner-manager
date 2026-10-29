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
    public static Brush Muted { get; private set; } = Brush("#6E7681");
    public static Brush Text { get; private set; } = Brush("#E6EDF3");
    public static Brush TextMuted { get; private set; } = Brush("#9198A1");

    private static readonly Dictionary<Color, Brush> Tints = new();

    public static void Use(bool light)
    {
        Tints.Clear();
        Success = Brush(light ? "#1A7F37" : "#3FB950");
        Danger = Brush(light ? "#CF222E" : "#F85149");
        Warning = Brush(light ? "#9A6700" : "#D29922");
        Info = Brush(light ? "#0969DA" : "#4493F8");
        Muted = Brush(light ? "#818B98" : "#6E7681");
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
        var tint = new SolidColorBrush(Color.FromArgb(0x26, solid.Color.R, solid.Color.G, solid.Color.B));
        tint.Freeze();
        Tints[solid.Color] = tint;
        return tint;
    }

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    private const uint MonitorDefaultToNull = 0;

    /// <summary>
    /// Moves a window whose title bar is on no screen (for example after a monitor was unplugged while it was hidden in
    /// the notification area) to the middle of the main screen.
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
            if (MonitorFromPoint(titleBar, MonitorDefaultToNull) != IntPtr.Zero) return;
            var area = SystemParameters.WorkArea;
            var width = Math.Min(window.ActualWidth > 0 ? window.ActualWidth : window.Width, area.Width);
            var height = Math.Min(window.ActualHeight > 0 ? window.ActualHeight : window.Height, area.Height);
            window.Left = area.Left + (area.Width - width) / 2;
            window.Top = area.Top + (area.Height - height) / 2;
        }
        catch { }
    }
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
    /// <summary>Closing the window hides it to the notification area, so monitoring continues. Exit from the tray icon.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool ActivityPanelOpen { get; set; } = true;
    public double ActivityPanelHeight { get; set; } = 130;
    /// <summary>PCs (by name) whose cards are collapsed to their header.</summary>
    public List<string> CollapsedPcs { get; set; } = new();
    /// <summary>The setup wizard ran (installed copies only).</summary>
    public bool SetupCompleted { get; set; }
    public bool StartMinimizedHint { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public int RefreshSeconds { get; set; } = 5;
    public string DismissedUpdateVersion { get; set; } = string.Empty;

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
