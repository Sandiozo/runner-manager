using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RunnerManager;

/// <summary>
/// The bottom panel: Activity, and while a runner that is running a job is open, a tab with that runner's live output.
/// When the job ends, its last output stays, often with the error, until the runner's next job or until it is closed.
/// </summary>
public partial class MainWindow
{
    /// <summary>A job's output, with what its tab says and which job it is from.</summary>
    internal sealed record OutputCopy(string Title, string MachineName, string Text, JobIdentity Job = default);

    /// <summary>
    /// Which job a busy runner runs, as far as it is known: when its worker started on its PC (from protocol 14 agents
    /// and on this PC), and GitHub's page of the job once GitHub reported it.
    /// </summary>
    internal readonly record struct JobIdentity(DateTime? WorkerStartedUtc, string? Url)
    {
        public static JobIdentity Of(MachineRunnerStatus runner) => new(runner.JobStartedUtc, runner.LiveJob?.Url);

        /// <summary>Another job, as far as both readings tell; a reading that does not tell counts as the same job.</summary>
        public bool IsOtherJobThan(JobIdentity other)
            => WorkerStartedUtc is { } started && other.WorkerStartedUtc is { } otherStarted ? started != otherStarted
                : Url is { Length: > 0 } url && other.Url is { Length: > 0 } otherUrl && !url.Equals(otherUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The runner whose output tab is selected (its <see cref="RunnerKey"/>), or null for Activity.</summary>
    private string? _outputTabKey;
    /// <summary>A runner the user just opened: its output tab is selected once its output arrives.</summary>
    private string? _selectOutputFor;
    private bool _syncingTabs;
    /// <summary>The output each live tab shows, kept when the job ends.</summary>
    private readonly Dictionary<string, OutputCopy> _liveOutputs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The last output of jobs that ended.</summary>
    private readonly Dictionary<string, OutputCopy> _finishedOutputs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The tab whose output is in the box.</summary>
    private string? _shownOutputKey;
    /// <summary>Newer output waits because text is selected.</summary>
    private bool _outputHeld;

    /// <summary>The runner of the selected live output tab, while it runs its job.</summary>
    private MachineRunnerStatus? OutputRunner => _outputTabKey == null || !_liveOutputs.ContainsKey(_outputTabKey) ? null
        : Machines.SelectMany(m => m.Runners).FirstOrDefault(r => RunnerKey(r) == _outputTabKey && r.IsBusy);

    /// <summary>The selected tab's output and whether its job has finished, or null for Activity.</summary>
    private (string Text, bool Finished)? SelectedOutput
        => _outputTabKey == null ? null
            : _liveOutputs.TryGetValue(_outputTabKey, out var live) ? (live.Text, false)
            : _finishedOutputs.TryGetValue(_outputTabKey, out var copy) ? (copy.Text, true)
            : null;

    /// <summary>
    /// Keeps the output of a job that ended, until the runner runs a job again. It is kept only while the runner is
    /// listed and idle: output that goes because a busy runner was closed, or because the runner is gone, is not kept.
    /// </summary>
    /// <remarks>
    /// The output ends when the job's worker process exits, often a moment before the runner stops showing the job. Until
    /// then the last output stays in its live tab, so it is not lost. Not once the runner runs another job: when a job
    /// starts right after the last one, the last one's output goes, as when a job starts after the runner was idle.
    /// </remarks>
    internal static void KeepFinishedOutput(IReadOnlyCollection<MachineRunnerStatus> runners,
        Dictionary<string, OutputCopy> live, Dictionary<string, OutputCopy> finished)
    {
        var byKey = runners.GroupBy(RunnerKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, copy) in live.ToList())
        {
            byKey.TryGetValue(key, out var runner);
            if (runner is { OutputText.Length: > 0 }) continue;
            if (runner is { IsBusy: true, IsExpanded: true } && !copy.Job.IsOtherJobThan(JobIdentity.Of(runner))) continue;
            live.Remove(key);
            if (runner is { IsBusy: false }) finished[key] = copy;
        }
        foreach (var gone in finished.Keys.Where(key => !byKey.ContainsKey(key)).ToList()) finished.Remove(gone);
        foreach (var runner in runners)
        {
            var key = RunnerKey(runner);
            if (runner.IsBusy) finished.Remove(key);
            if (runner.OutputText.Length > 0)
                live[key] = new OutputCopy(runner.TitleText, runner.MachineName, runner.OutputText, JobIdentity.Of(runner));
        }
    }

    /// <summary>What the panel says about the output: that its job has finished, or that newer output waits.</summary>
    internal static string OutputNoteText(bool? finished, bool held)
        => finished == true ? "Job finished" : finished == false && held ? "Paused while text is selected" : string.Empty;

    /// <summary>Adds and removes output tabs, and shows the selected tab's newest output.</summary>
    private void UpdateOutputTabs()
    {
        var runners = Machines.SelectMany(m => m.Runners).ToList();
        KeepFinishedOutput(runners, _liveOutputs, _finishedOutputs);
        // Live output in the order of the cards, then the output of jobs that ended.
        var tabs = runners.Select(RunnerKey).Distinct(StringComparer.OrdinalIgnoreCase).Where(_liveOutputs.ContainsKey)
            .Select(key => (Key: key, Copy: _liveOutputs[key], Finished: false))
            .Concat(_finishedOutputs.Select(f => (Key: f.Key, Copy: f.Value, Finished: true)))
            .ToList();
        var keys = tabs.Select(t => t.Key).ToList();
        var buttons = BottomTabsPanel.Children.OfType<RadioButton>().Where(t => t != ActivityTab).ToList();
        if (!buttons.Select(t => (string)t.Tag).SequenceEqual(keys))
        {
            foreach (var button in buttons) BottomTabsPanel.Children.Remove(button);
            foreach (var key in keys)
            {
                var button = new RadioButton
                {
                    Tag = key,
                    GroupName = "BottomTab",
                    Style = (Style)FindResource("Segment"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Content = new TextBlock { FontSize = 12 }
                };
                button.Checked += BottomTab_Checked;
                BottomTabsPanel.Children.Add(button);
            }
        }
        // A runner's name can change while its tab stays, and its job can end.
        foreach (var (button, tab) in BottomTabsPanel.Children.OfType<RadioButton>().Where(t => t != ActivityTab).Zip(tabs))
        {
            ((TextBlock)button.Content).Text = tab.Finished ? $"Output · {tab.Copy.Title} (finished)" : $"Output · {tab.Copy.Title}";
            AutomationProperties.SetName(button, ((TextBlock)button.Content).Text);
            button.ToolTip = tab.Finished
                ? $"The last output of {tab.Copy.Title} on {tab.Copy.MachineName}. The job has finished."
                : $"Live output of {tab.Copy.Title} on {tab.Copy.MachineName}";
        }

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
        var output = SelectedOutput;
        var open = _settings.ActivityPanelOpen;
        ActivityList.Visibility = open && output == null ? Visibility.Visible : Visibility.Collapsed;
        OutputBox.Visibility = open && output != null ? Visibility.Visible : Visibility.Collapsed;
        // The full output is read from the runner, which only has it while the job runs.
        OpenOutputButton.Visibility = output is { Finished: false } ? Visibility.Visible : Visibility.Collapsed;
        CloseOutputButton.Visibility = output is { Finished: true } ? Visibility.Visible : Visibility.Collapsed;
        ClearActivityButton.Visibility = output == null ? Visibility.Visible : Visibility.Collapsed;
        CopyBottomButton.ToolTip = output == null ? "Copy activity" : "Copy output";
        if (_shownOutputKey != _outputTabKey)
        {
            // A selection in one tab's output would hold back the next tab's.
            _shownOutputKey = _outputTabKey;
            OutputBox.Select(0, 0);
        }
        if (output != null) ShowOutput(output.Value.Text);
        else UpdateOutputNote();
    }

    private void ShowOutput(string text)
    {
        // Replacing the text would drop what the user is selecting; it updates again once the selection is cleared.
        _outputHeld = OutputBox.Text != text && OutputBox.SelectionLength > 0;
        if (OutputBox.Text != text && !_outputHeld)
        {
            var atEnd = OutputBox.Text.Length == 0 || OutputBox.VerticalOffset + OutputBox.ViewportHeight >= OutputBox.ExtentHeight - 4;
            OutputBox.Text = text;
            if (atEnd) OutputBox.ScrollToEnd();
        }
        UpdateOutputNote();
    }

    private void UpdateOutputNote()
    {
        OutputNote.Text = OutputNoteText(SelectedOutput?.Finished, _outputHeld);
        OutputNote.Visibility = OutputNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OutputBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (OutputBox.SelectionLength == 0 && SelectedOutput is { } output) ShowOutput(output.Text);
    }

    private void BottomOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (OutputRunner is { } runner) OpenLiveOutput(runner);
    }

    private void CloseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_outputTabKey != null && _finishedOutputs.Remove(_outputTabKey)) UpdateOutputTabs();
    }
}
