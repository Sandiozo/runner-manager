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

    public static void Apply(AppTheme mode)
    {
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
    public bool CloseToTray { get; set; }
    public bool ActivityPanelOpen { get; set; } = true;
    public double ActivityPanelHeight { get; set; } = 130;
    /// <summary>The setup wizard ran (installed copies only).</summary>
    public bool SetupCompleted { get; set; }
    public bool StartMinimizedHint { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public int RefreshSeconds { get; set; } = 5;
    public string DismissedUpdateVersion { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 5, 60);
                return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
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
