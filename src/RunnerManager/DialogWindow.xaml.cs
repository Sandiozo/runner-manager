using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RunnerManager;

public enum DialogTone { Info, Question, Warning, Danger, Error }

/// <summary>Themed replacement for MessageBox: Cancel, then the secondary choice, then the primary (default) button on the right.</summary>
public partial class DialogWindow : Window
{
    private string? _result;

    private DialogWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            DialogResult = false;
        };
    }

    /// <summary>Shows the dialog and returns the label of the chosen button, or null when closed.</summary>
    public static string? Show(Window? owner, DialogTone tone, string heading, string message,
        string primary, string? secondary = null, string? cancel = "Cancel", string? detail = null)
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
            DialogTone.Question => ("", Palette.Info),
            DialogTone.Warning => ("", Palette.Warning),
            DialogTone.Danger => ("", Palette.Danger),
            DialogTone.Error => ("", Palette.Danger),
            _ => ("", Palette.Info)
        };
        dialog.IconGlyph.Text = glyph;
        dialog.IconGlyph.Foreground = brush;
        dialog.IconBadge.Background = Palette.Tint(brush);

        if (cancel != null) dialog.AddButton(cancel, "GhostButton", isCancel: true);
        if (secondary != null) dialog.AddButton(secondary, null);
        dialog.AddButton(primary, tone is DialogTone.Danger ? "SolidDangerButton" : tone is DialogTone.Error or DialogTone.Info ? null : "PrimaryButton",
            isDefault: true);

        dialog.ShowDialog();
        return dialog._result;
    }

    public static void Error(Window? owner, string heading, string message)
        => Show(owner, DialogTone.Error, heading, message, "OK", cancel: null);

    public static void Info(Window? owner, string heading, string message)
        => Show(owner, DialogTone.Info, heading, message, "OK", cancel: null);

    public static bool Confirm(Window? owner, string heading, string message, string confirm, bool danger = false)
        => Show(owner, danger ? DialogTone.Danger : DialogTone.Question, heading, message, confirm) == confirm;

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
        button.Click += (_, _) =>
        {
            _result = isCancel ? null : label;
            DialogResult = !isCancel;
        };
        ButtonPanel.Children.Add(button);
        if (isDefault) Loaded += (_, _) => button.Focus();
    }
}
