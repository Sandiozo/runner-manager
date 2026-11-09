using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RunnerManager;

public partial class AddRunnerWindow : Window
{
    private const string DefaultFolderName = "NewRunner";
    private const string FolderRule = "The folder must be empty or new.";
    private readonly List<(string Scope, string Name)> _existing;
    private readonly string _alwaysAdded = $"The labels self-hosted, Windows and {RunnerService.ArchitectureLabel} are always added.";
    private AddRunnerForm.RepositoryCheck _repository = AddRunnerForm.CheckRepository(string.Empty);
    private bool _pathEdited;
    private bool _nameEdited;
    public RunnerSetup? Setup { get; private set; }

    /// <param name="existingRunners">
    /// Runners already registered, on any PC, so a name that is taken for the repository is caught here instead of
    /// after the download.
    /// </param>
    public AddRunnerWindow(IEnumerable<MachineRunnerStatus>? existingRunners = null)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _existing = (existingRunners ?? Enumerable.Empty<MachineRunnerStatus>()).Select(r => (r.Scope, r.AgentName)).ToList();
        LabelsHint.Text = _alwaysAdded;
        RunnerNameBox.Text = Environment.MachineName;
        InstallPathBox.Text = FreeFolder(DefaultFolderName);
        // Typing in a box keeps what was typed; until then, the name and folder follow the repository.
        RunnerNameBox.TextChanged += (_, _) =>
        {
            if (RunnerNameBox.IsKeyboardFocused) _nameEdited = true;
            Validate();
        };
        InstallPathBox.TextChanged += (_, _) =>
        {
            if (InstallPathBox.IsKeyboardFocused) _pathEdited = true;
            Validate();
        };
        LabelsBox.TextChanged += (_, _) => Validate();
        Loaded += (_, _) => RepositoryUrlBox.Focus();
        Validate();
    }

    private void RepositoryUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _repository = AddRunnerForm.CheckRepository(RepositoryUrlBox.Text);
        var typed = RepositoryUrlBox.Text.Trim().Length > 0;
        RepositoryHint.Text = !typed ? AddRunnerForm.RepositoryHint
            : !_repository.IsValid ? _repository.Problem
            : _repository.RepositoryPath.Length > 0 ? $"✓  Repository runner for {_repository.RepositoryPath}"
            : $"✓  Organization runner for {_repository.Organization} (requires organization admin rights)";
        RepositoryHint.Foreground = !typed ? Palette.TextMuted : _repository.IsValid ? Palette.Success : Palette.Warning;

        var repositoryRunner = _repository.IsValid && _repository.RepositoryPath.Length > 0;
        CheckWorkflowsToggle.IsEnabled = repositoryRunner;
        if (!repositoryRunner) CheckWorkflowsToggle.IsChecked = false;

        if (!_nameEdited) RunnerNameBox.Text = AddRunnerForm.DefaultRunnerName(Environment.MachineName, _repository, IsTaken);
        if (!_pathEdited && _repository.IsValid) InstallPathBox.Text = SuggestFolder(_repository.Url);
        Validate();
    }

    private bool IsTaken(string name) => AddRunnerForm.NameProblem(name, _repository.Scope, _existing) != null && RunnerService.RunnerNameProblem(name) == null;

    /// <summary>A free folder for the repository, so a second runner for it does not start with a folder that has files.</summary>
    private static string SuggestFolder(string registrationUrl)
    {
        try { return RunnerService.SuggestInstallPath(registrationUrl); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(RunnerService.RunnersRoot, RunnerService.SafeFolderName(RunnerService.RepositoryPath(registrationUrl).Split('/').Last()));
        }
    }

    private static string FreeFolder(string name)
    {
        try { return AddRunnerForm.FreeFolder(RunnerService.RunnersRoot, name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Path.Combine(RunnerService.RunnersRoot, name); }
    }

    /// <summary>Shows the problem of each field next to it, and enables Create only when there is none.</summary>
    private bool Validate()
    {
        var nameProblem = AddRunnerForm.NameProblem(RunnerNameBox.Text, _repository.Scope, _existing);
        NameHint.Text = nameProblem ?? string.Empty;
        NameHint.Foreground = Palette.Warning;
        NameHint.Visibility = nameProblem != null ? Visibility.Visible : Visibility.Collapsed;

        var labelsProblem = RunnerService.LabelsProblem(AddRunnerForm.ParseLabels(LabelsBox.Text));
        LabelsHint.Text = labelsProblem ?? _alwaysAdded;
        LabelsHint.Foreground = labelsProblem != null ? Palette.Warning : Palette.TextMuted;

        var folderProblem = AddRunnerForm.FolderProblem(InstallPathBox.Text);
        InstallHint.Text = folderProblem ?? FolderRule;
        InstallHint.Foreground = folderProblem != null ? Palette.Warning : Palette.TextMuted;

        var valid = _repository.IsValid && nameProblem == null && labelsProblem == null && folderProblem == null;
        CreateButton.IsEnabled = valid;
        return valid;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the runner install folder",
            InitialDirectory = Directory.Exists(RunnerService.RunnersRoot) ? RunnerService.RunnersRoot : null
        };
        if (dialog.ShowDialog(this) != true) return;
        _pathEdited = true;
        InstallPathBox.Text = dialog.FolderName;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        // Checked again: the folder may have been filled since it was typed.
        if (!Validate()) return;
        Setup = new RunnerSetup
        {
            RepositoryUrl = _repository.Url,
            CheckWorkflows = CheckWorkflowsToggle.IsChecked == true,
            RunnerName = RunnerNameBox.Text.Trim(),
            InstallPath = InstallPathBox.Text.Trim(),
            Labels = AddRunnerForm.ParseLabels(LabelsBox.Text)
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
