using System.Windows;

namespace RunnerWidget;

public partial class EditRunnerWindow : Window
{
    public RunnerEditRequest? Edit { get; private set; }

    public EditRunnerWindow(MachineRunnerStatus runner)
    {
        InitializeComponent();
        RunnerNameBox.Text = runner.AgentName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = RunnerNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Runner name is required.", "Missing information");
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
