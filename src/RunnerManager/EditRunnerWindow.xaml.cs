using System.Windows;

namespace RunnerManager;

public partial class EditRunnerWindow : Window
{
    public RunnerEditRequest? Edit { get; private set; }

    public EditRunnerWindow(MachineRunnerStatus runner)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        RunnerNameBox.Text = runner.AgentName;
        SubtitleText.Text = $"{runner.RepositoryText}  ·  {runner.MachineName}";

        // Labels are only known for runners on this PC (read from GitHub); remote runners keep theirs when left blank.
        var currentLabels = runner.LocalRunner?.CustomLabels ?? Array.Empty<string>();
        LabelsBox.Text = string.Join(", ", currentLabels);
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
        if (RunnerService.RunnerNameProblem(name) is { } nameProblem)
        {
            DialogWindow.Info(this, "Invalid runner name", nameProblem);
            RunnerNameBox.Focus();
            return;
        }
        var labels = LabelsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (RunnerService.LabelsProblem(labels) is { } labelProblem)
        {
            DialogWindow.Info(this, "Invalid labels", labelProblem);
            LabelsBox.Focus();
            return;
        }
        Edit = new RunnerEditRequest(name, labels);
        DialogResult = true;
    }
}

public sealed record RunnerEditRequest(string RunnerName, string[] Labels);
