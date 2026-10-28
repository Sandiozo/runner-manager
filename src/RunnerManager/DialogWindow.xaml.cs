using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace RunnerManager;

public enum DialogTone { Info, Question, Warning, Danger, Error }

/// <summary>Themed replacement for MessageBox: Cancel, then the secondary choice, then the primary (default) button on the right.</summary>
public partial class DialogWindow : Window
{
    private string? _result;
    /// <summary>Shown with Show() instead of ShowDialog(), so it closes itself instead of setting DialogResult.</summary>
    private bool _modeless;

    private DialogWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Finish(null);
        };
    }

    /// <summary>Shows the dialog and returns the label of the chosen button, or null when closed.</summary>
    public static string? Show(Window? owner, DialogTone tone, string heading, string message,
        string primary, string? secondary = null, string? cancel = "Cancel", string? detail = null, bool topmost = false)
    {
        var dialog = Create(owner, tone, heading, message, detail);
        if (topmost)
        {
            // Asked from a process with no window of its own, so keep it in front.
            dialog.Topmost = true;
            dialog.ShowInTaskbar = true;
            dialog.Loaded += (_, _) => dialog.Activate();
        }
        dialog.AddButtons(tone, primary, secondary, cancel, primaryIsDefault: true);
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>
    /// An approval for an AI assistant: in front of other windows, but without taking the keyboard from what the user
    /// is typing (it flashes in the taskbar instead), and with no default button, so a stray Enter approves nothing.
    /// Returns the label of the chosen button, or null when declined, closed or cancelled.
    /// </summary>
    public static Task<string?> ShowApprovalAsync(DialogTone tone, string heading, string message, string primary, string cancel,
        CancellationToken cancellationToken)
    {
        var dialog = Create(null, tone, heading, message, null);
        dialog._modeless = true;
        dialog.Topmost = true;
        dialog.ShowInTaskbar = true;
        dialog.ShowActivated = false;
        dialog.AddButtons(tone, primary, null, cancel, primaryIsDefault: false);
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => dialog.Dispatcher.InvokeAsync(() => { if (dialog.IsVisible) dialog.Close(); }));
        dialog.Closed += (_, _) =>
        {
            registration.Dispose();
            closed.TrySetResult(cancellationToken.IsCancellationRequested ? null : dialog._result);
        };
        dialog.SourceInitialized += (_, _) => Flash(dialog);
        if (cancellationToken.IsCancellationRequested) return Task.FromResult<string?>(null);
        dialog.Show();
        return closed.Task;
    }

    public static void Error(Window? owner, string heading, string message)
        => Show(owner, DialogTone.Error, heading, message, "OK", cancel: null);

    public static void Info(Window? owner, string heading, string message)
        => Show(owner, DialogTone.Info, heading, message, "OK", cancel: null);

    public static bool Confirm(Window? owner, string heading, string message, string confirm, bool danger = false)
        => Show(owner, danger ? DialogTone.Danger : DialogTone.Question, heading, message, confirm) == confirm;

    private static DialogWindow Create(Window? owner, DialogTone tone, string heading, string message, string? detail)
    {
        var dialog = new DialogWindow { Title = heading };
        if (owner is { IsLoaded: true, IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            dialog.DetailText.Text = detail;
            dialog.DetailBox.Visibility = Visibility.Visible;
        }

        var (glyph, brush) = tone switch
        {
            DialogTone.Question => ("", Palette.Info),
            DialogTone.Warning => ("", Palette.Warning),
            DialogTone.Danger => ("", Palette.Danger),
            DialogTone.Error => ("", Palette.Danger),
            _ => ("", Palette.Info)
        };
        dialog.IconGlyph.Text = glyph;
        dialog.IconGlyph.Foreground = brush;
        dialog.IconBadge.Background = Palette.Tint(brush);
        return dialog;
    }

    private void AddButtons(DialogTone tone, string primary, string? secondary, string? cancel, bool primaryIsDefault)
    {
        if (cancel != null) AddButton(cancel, "GhostButton", isCancel: true);
        if (secondary != null) AddButton(secondary, null);
        AddButton(primary, tone is DialogTone.Danger ? "SolidDangerButton" : tone is DialogTone.Error or DialogTone.Info ? null : "PrimaryButton",
            isDefault: primaryIsDefault);
    }

    private void AddButton(string label, string? style, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        if (style != null) button.Style = (Style)FindResource(style);
        button.Click += (_, _) => Finish(isCancel ? null : label);
        ButtonPanel.Children.Add(button);
        if (isDefault) Loaded += (_, _) => button.Focus();
    }

    private void Finish(string? result)
    {
        _result = result;
        if (_modeless) Close();
        else DialogResult = result != null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Flashes the taskbar button until the user switches to the window.</summary>
    private static void Flash(Window window)
    {
        const uint all = 3, untilForeground = 12;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(window).Handle,
            Flags = all | untilForeground
        };
        FlashWindowEx(ref info);
    }
}
