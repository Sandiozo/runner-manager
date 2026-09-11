using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RunnerWidget;

public partial class AddRunnerWindow : Window
{
    private bool _pathEdited;
    public RunnerSetup? Setup { get; private set; }

    public AddRunnerWindow()
    {
        InitializeComponent();
        RunnerNameBox.Text = Environment.MachineName;
        InstallPathBox.Text = Path.Combine(RunnerManager.DefaultRoot, "NewRunner");
        InstallPathBox.TextChanged += (_, _) => _pathEdited = InstallPathBox.IsKeyboardFocused;
    }

    private void RepositoryUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pathEdited) return;
        var repoPath = RunnerManager.RepositoryPath(RepositoryUrlBox.Text.Trim());
        var name = repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(name))
            InstallPathBox.Text = Path.Combine(RunnerManager.DefaultRoot, Sanitize(name));
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepositoryUrlBox.Text.Trim();
        var runnerName = RunnerNameBox.Text.Trim();
        var install = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(RunnerManager.RepositoryPath(repo)))
        {
            MessageBox.Show(this, "Enter a valid https://github.com/owner/repository URL.", "Invalid repository");
            return;
        }
        if (string.IsNullOrWhiteSpace(runnerName) || string.IsNullOrWhiteSpace(install))
        {
            MessageBox.Show(this, "Runner name and install folder are required.", "Missing information");
            return;
        }
        Setup = new RunnerSetup
        {
            RepositoryUrl = repo,
            RunnerName = runnerName,
            InstallPath = install,
            Labels = LabelsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
