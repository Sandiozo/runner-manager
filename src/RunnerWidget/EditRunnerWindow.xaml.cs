using System.Windows;

namespace RunnerWidget;

public partial class EditRunnerWindow : Window
{
    private readonly string[] _currentLabels;
    public RunnerEditRequest? Edit { get; private set; }

    public EditRunnerWindow(MachineRunnerStatus runner)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        RunnerNameBox.Text = runner.AgentName;
        SubtitleText.Text = $"{runner.RepositoryText}  ·  {runner.MachineName}";

        // Labels are only known for runners on this PC (read from GitHub); remote runners keep theirs when left blank.
        _currentLabels = runner.LocalRunner?.CustomLabels ?? Array.Empty<string>();
        LabelsBox.Text = string.Join(", ", _currentLabels);
        LabelsHint.Text = runner.LocalRunner != null
            ? "The labels self-hosted, Windows and X64 are always added. Leave blank to keep the current labels."
            : "Leave blank to keep the current labels.";
        Loaded += (_, _) => { RunnerNameBox.Focus(); RunnerNameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = RunnerNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DialogWindow.Info(this, "Missing information", "Runner name is required.");
            return;
        }
        Edit = new RunnerEditRequest(
            name,
            LabelsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed record RunnerEditRequest(string RunnerName, string[] Labels);
