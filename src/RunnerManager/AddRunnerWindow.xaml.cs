using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RunnerManager;

public partial class AddRunnerWindow : Window
{
    private bool _pathEdited;
    public RunnerSetup? Setup { get; private set; }

    public AddRunnerWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        RunnerNameBox.Text = Environment.MachineName;
        InstallPathBox.Text = Path.Combine(RunnerService.DefaultRoot, "NewRunner");
        InstallPathBox.TextChanged += (_, _) =>
        {
            if (InstallPathBox.IsKeyboardFocused) _pathEdited = true;
            ValidateInstallPath();
        };
        Loaded += (_, _) => RepositoryUrlBox.Focus();
        ValidateInstallPath();
    }

    private string RepositoryUrl
    {
        get
        {
            var text = RepositoryUrlBox.Text.Trim();
            // Accept the short owner/repo form too.
            if (!text.Contains("://") && text.Count(c => c == '/') == 1 && !text.Contains(' '))
                return $"https://github.com/{text.TrimEnd('/')}";
            return text;
        }
    }

    private void RepositoryUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var repoPath = RunnerService.RepositoryPath(RepositoryUrl);
        var org = RunnerService.OrganizationName(RepositoryUrl);
        if (RepositoryUrlBox.Text.Trim().Length == 0)
        {
            RepositoryHint.Text = "Enter a repository URL or owner/repo. For an organization runner, enter the organization URL.";
            RepositoryHint.Foreground = Palette.TextMuted;
        }
        else if (!string.IsNullOrEmpty(repoPath))
        {
            RepositoryHint.Text = $"✓  Repository runner for {repoPath}";
            RepositoryHint.Foreground = Palette.Success;
        }
        else if (!string.IsNullOrEmpty(org))
        {
            RepositoryHint.Text = $"✓  Organization runner for {org} (requires organization admin rights)";
            RepositoryHint.Foreground = Palette.Success;
        }
        else
        {
            RepositoryHint.Text = "This is not a GitHub repository or organization URL.";
            RepositoryHint.Foreground = Palette.Warning;
        }
        CheckWorkflowsToggle.IsEnabled = !string.IsNullOrEmpty(repoPath);
        if (string.IsNullOrEmpty(repoPath)) CheckWorkflowsToggle.IsChecked = false;

        if (_pathEdited) return;
        var name = (string.IsNullOrEmpty(repoPath) ? org : repoPath).Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(name))
            InstallPathBox.Text = Path.Combine(RunnerService.DefaultRoot, Sanitize(name));
    }

    private void ValidateInstallPath()
    {
        var path = InstallPathBox.Text.Trim();
        var problem = string.Empty;
        try
        {
            if (path.Length > 0 && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                problem = "This folder already contains files. Choose an empty or new folder.";
        }
        catch { }
        InstallHint.Text = problem.Length > 0 ? problem : "The folder must be empty or new.";
        InstallHint.Foreground = problem.Length > 0 ? Palette.Warning : Palette.TextMuted;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the runner install folder",
            InitialDirectory = Directory.Exists(RunnerService.DefaultRoot) ? RunnerService.DefaultRoot : null
        };
        if (dialog.ShowDialog(this) != true) return;
        _pathEdited = true;
        InstallPathBox.Text = dialog.FolderName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepositoryUrl;
        var runnerName = RunnerNameBox.Text.Trim();
        var install = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(RunnerService.ApiScope(repo)))
        {
            DialogWindow.Info(this, "Invalid repository", "Enter a GitHub repository URL such as https://github.com/owner/repository, or an organization URL.");
            RepositoryUrlBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(runnerName) || string.IsNullOrWhiteSpace(install))
        {
            DialogWindow.Info(this, "Missing information", "Runner name and install folder are required.");
            return;
        }
        if (Directory.Exists(install) && Directory.EnumerateFileSystemEntries(install).Any())
        {
            DialogWindow.Info(this, "Folder is not empty", "Choose an empty or new folder for the runner.");
            return;
        }
        Setup = new RunnerSetup
        {
            RepositoryUrl = RunnerService.NormalizeRegistrationUrl(repo),
            CheckWorkflows = CheckWorkflowsToggle.IsChecked == true,
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
