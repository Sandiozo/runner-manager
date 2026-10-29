using System.Windows;
using System.Windows.Controls;

namespace RunnerManager;

/// <summary>
/// The bottom panel: Activity, and while a runner that is running a job is open, a tab with that runner's live output.
/// </summary>
public partial class MainWindow
{
    /// <summary>The runner whose output tab is selected (its <see cref="RunnerKey"/>), or null for Activity.</summary>
    private string? _outputTabKey;
    /// <summary>A runner the user just opened: its output tab is selected once its output arrives.</summary>
    private string? _selectOutputFor;
    private bool _syncingTabs;

    private MachineRunnerStatus? OutputRunner => _outputTabKey == null ? null
        : Machines.SelectMany(m => m.Runners).FirstOrDefault(r => RunnerKey(r) == _outputTabKey && r.OutputText.Length > 0);

    /// <summary>Adds and removes output tabs for the open busy runners, and shows the selected tab's newest output.</summary>
    private void UpdateOutputTabs()
    {
        var runners = Machines.SelectMany(m => m.Runners).Where(r => r.OutputText.Length > 0).ToList();
        var keys = runners.Select(RunnerKey).ToList();
        var tabs = BottomTabsPanel.Children.OfType<RadioButton>().Where(t => t != ActivityTab).ToList();
        if (!tabs.Select(t => (string)t.Tag).SequenceEqual(keys))
        {
            foreach (var tab in tabs) BottomTabsPanel.Children.Remove(tab);
            foreach (var runner in runners)
            {
                var tab = new RadioButton
                {
                    Tag = RunnerKey(runner),
                    GroupName = "BottomTab",
                    Style = (Style)FindResource("Segment"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Content = new TextBlock { FontSize = 12 },
                    ToolTip = $"Live output of {runner.TitleText} on {runner.MachineName}"
                };
                tab.Checked += BottomTab_Checked;
                BottomTabsPanel.Children.Add(tab);
            }
        }
        // A runner's name can change while its tab stays.
        foreach (var (tab, runner) in BottomTabsPanel.Children.OfType<RadioButton>().Where(t => t != ActivityTab).Zip(runners))
            ((TextBlock)tab.Content).Text = $"Output · {runner.TitleText}";

        if (_selectOutputFor != null && keys.Contains(_selectOutputFor))
        {
            _outputTabKey = _selectOutputFor;
            _selectOutputFor = null;
        }
        if (_outputTabKey != null && !keys.Contains(_outputTabKey)) _outputTabKey = null;

        _syncingTabs = true;
        foreach (var tab in BottomTabsPanel.Children.OfType<RadioButton>())
            tab.IsChecked = (tab.Tag as string) == _outputTabKey;
        _syncingTabs = false;

        var hasTabs = keys.Count > 0;
        BottomTabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        ActivityTitle.Visibility = ActivityCount.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        ApplyBottomPanel();
    }

    private void BottomTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingTabs || sender is not RadioButton tab) return;
        _outputTabKey = tab.Tag as string;
        _selectOutputFor = null;
        if (!_settings.ActivityPanelOpen)
        {
            _settings.ActivityPanelOpen = true;
            ApplyActivityPanelState();
            _settings.Save();
        }
        ApplyBottomPanel();
    }

    /// <summary>Shows Activity or the selected runner's output, while the panel is open.</summary>
    private void ApplyBottomPanel()
    {
        var output = OutputRunner;
        var open = _settings.ActivityPanelOpen;
        ActivityList.Visibility = open && output == null ? Visibility.Visible : Visibility.Collapsed;
        OutputBox.Visibility = open && output != null ? Visibility.Visible : Visibility.Collapsed;
        OpenOutputButton.Visibility = output != null ? Visibility.Visible : Visibility.Collapsed;
        ClearActivityButton.Visibility = output == null ? Visibility.Visible : Visibility.Collapsed;
        CopyBottomButton.ToolTip = output == null ? "Copy activity" : "Copy output";
        if (output != null) ShowOutput(output.OutputText);
    }

    private void ShowOutput(string text)
    {
        // Replacing the text would drop what the user is selecting; it updates again once the selection is cleared.
        if (OutputBox.Text == text || OutputBox.SelectionLength > 0) return;
        var atEnd = OutputBox.Text.Length == 0 || OutputBox.VerticalOffset + OutputBox.ViewportHeight >= OutputBox.ExtentHeight - 4;
        OutputBox.Text = text;
        if (atEnd) OutputBox.ScrollToEnd();
    }

    private void OutputBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (OutputBox.SelectionLength == 0 && OutputRunner is { } runner) ShowOutput(runner.OutputText);
    }

    private void BottomOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (OutputRunner is { } runner) OpenLiveOutput(runner);
    }
}
