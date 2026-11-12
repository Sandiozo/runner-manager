using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace RunnerManager;

/// <summary>Shows the tail of a log fetched locally or uploaded by another PC's fleet agent.</summary>
public partial class LogViewerWindow : Window
{
    private readonly Func<CancellationToken, Task<LogPayload>> _load;
    private readonly string? _folder;
    private readonly string _waitingText;
    private CancellationTokenSource? _cts;
    /// <summary>The log shown, or empty while it loads, after a failed refresh and when the log is empty.</summary>
    private string _fullText = string.Empty;

    public LogViewerWindow(string title, string waitingText, Func<CancellationToken, Task<LogPayload>> load, string? folder = null)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        Title = title;
        TitleText.Text = title;
        _waitingText = waitingText;
        _load = load;
        _folder = folder;
        FolderButton.Visibility = folder != null ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        RefreshButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        ShowState("", _waitingText);
        try
        {
            var log = await _load(token);
            if (token.IsCancellationRequested) return;
            TitleText.Text = log.Title;
            SubtitleText.Text = $"Last {CountLines(log.Text)} lines  ·  captured {log.CreatedUtc.ToLocalTime():HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(log.Text))
            {
                _fullText = log.Text;
                StatePanel.Visibility = Visibility.Collapsed;
                CopyButton.IsEnabled = SaveButton.IsEnabled = true;
                ApplyFilter();
            }
            else ShowState("", "The log is empty.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowState("", ex.Message);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>Shows a message instead of the log, and forgets the log, so the filter, Copy and Save do not use an old one.</summary>
    private void ShowState(string glyph, string text)
    {
        StateGlyph.Text = glyph;
        StateText.Text = text;
        StatePanel.Visibility = Visibility.Visible;
        _fullText = string.Empty;
        LogText.Text = string.Empty;
        CopyButton.IsEnabled = SaveButton.IsEnabled = false;
    }

    private void ApplyFilter()
    {
        if (_fullText.Length == 0) return;
        var filter = FilterBox.Text.Trim();
        LogText.Text = filter.Length == 0
            ? _fullText
            : string.Join('\n', _fullText.Split('\n').Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        LogText.ScrollToEnd();
    }

    private static int CountLines(string text) => string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + 1;

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc clears the filter first, then closes.
            if (FilterBox.IsKeyboardFocused && FilterBox.Text.Length > 0) FilterBox.Clear();
            else Close();
            e.Handled = true;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.SelectionLength > 0 ? LogText.SelectedText : LogText.Text); } catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{string.Concat(TitleText.Text.Split(Path.GetInvalidFileNameChars()))}.log".Replace(' ', '-'),
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _fullText); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DialogWindow.Error(this, "Could not save the log", ex.Message);
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_folder != null && Directory.Exists(_folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
    }
}
