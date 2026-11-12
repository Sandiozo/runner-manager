using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RunnerManager;

public partial class AddRunnerWindow : Window
{
    private const string DefaultFolderName = "NewRunner";
    private const string FolderRule = "The folder must be empty or new.";
    private readonly List<(string Scope, string Name)> _existing;
    private readonly Func<MachineInfo, bool> _canChooseFolder;
    private string _alwaysAdded = string.Empty;
    private AddRunnerForm.RepositoryCheck _repository = AddRunnerForm.CheckRepository(string.Empty);
    /// <summary>The PC to add the runner on; null is this PC, when no PCs were passed.</summary>
    private MachineInfo? _target;
    /// <summary>Why a runner cannot be added on this PC right now, or null.</summary>
    private readonly string? _thisPcProblem;
    /// <summary>What to say about the dashboard's PC when it cannot be chosen, or null.</summary>
    private readonly string? _missingPcHint;
    private bool _pathEdited;
    private bool _nameEdited;
    /// <summary>The folder last typed or chosen, kept while a PC without a folder box is selected.</summary>
    private string _typedPath = string.Empty;
    public RunnerSetup? Setup { get; private set; }

    /// <summary>
    /// The PC to add the runner on, from the PCs passed in. Null or this PC: create it here with
    /// <see cref="RunnerService.CreateRunnerAsync"/>. Another PC: send <see cref="Setup"/> to its fleet agent.
    /// </summary>
    public MachineInfo? TargetPc { get; private set; }

    /// <param name="existingRunners">
    /// Runners already registered, on any PC, so a name that is taken for the repository is caught here instead of
    /// after the download. Null: the runners of <paramref name="pcs"/>.
    /// </param>
    /// <param name="pcs">
    /// The fleet's PCs. Those a runner can be added on are offered (this PC, and online PCs with a fleet agent that can
    /// create runners). With none or one, the window works on this PC only.
    /// </param>
    /// <param name="selectedPc">The PC to select first, such as the one selected on the dashboard. Default: this PC.</param>
    /// <param name="canChooseFolder">
    /// Whether a folder typed for another PC reaches that PC. Null: no PC's does, so for another PC the folder box stays
    /// empty and the runner goes in that PC's folder for new runners.
    /// </param>
    /// <param name="thisPcProblem">
    /// Why a runner cannot be added on this PC right now (it is not connected to GitHub), or null. Shown while this PC
    /// is chosen, and Create stays off, so the form is not filled in for nothing; the other PCs can still be chosen.
    /// </param>
    public AddRunnerWindow(IEnumerable<MachineRunnerStatus>? existingRunners = null, IEnumerable<MachineInfo>? pcs = null,
        MachineInfo? selectedPc = null, Func<MachineInfo, bool>? canChooseFolder = null, string? thisPcProblem = null)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _thisPcProblem = thisPcProblem;
        var fleet = pcs?.OfType<MachineInfo>().ToList() ?? new List<MachineInfo>();
        // Runners that other PCs report can have a null scope or name, which AddRunnerForm.NameProblem allows for.
        _existing = (existingRunners ?? fleet.SelectMany(m => m.Runners ?? Array.Empty<MachineRunnerStatus>()))
            .OfType<MachineRunnerStatus>().Select(r => (r.Scope, r.AgentName)).ToList();
        _canChooseFolder = canChooseFolder ?? (_ => false);

        var targets = AddRunnerForm.TargetPcs(fleet);
        _target = targets.FirstOrDefault(m => selectedPc != null && m.MachineKey == selectedPc.MachineKey)
                  ?? targets.FirstOrDefault(m => m.IsLocal) ?? targets.FirstOrDefault();
        if (targets.Count > 1) ShowPcChoices(targets);
        else PcFieldLabel.Visibility = PcChoicesHost.Visibility = Visibility.Collapsed;
        // The PC selected on the dashboard is missing when it is offline or its fleet agent cannot create runners.
        _missingPcHint = AddRunnerForm.MissingPcHint(selectedPc, targets);
        if (_missingPcHint != null || _thisPcProblem != null) PcPanel.Visibility = Visibility.Visible;

        // Typing in a box keeps what was typed; until then, the name and folder follow the repository and the PC.
        RunnerNameBox.TextChanged += (_, _) =>
        {
            if (RunnerNameBox.IsKeyboardFocused) _nameEdited = true;
            Validate();
        };
        InstallPathBox.TextChanged += (_, _) =>
        {
            if (InstallPathBox.IsKeyboardFocused) _pathEdited = true;
            if (_pathEdited && InstallPathBox.IsEnabled) _typedPath = InstallPathBox.Text;
            Validate();
        };
        LabelsBox.TextChanged += (_, _) => Validate();
        Loaded += (_, _) => RepositoryUrlBox.Focus();
        ShowTarget();
    }

    private bool IsRemote => _target is { IsLocal: false };
    private string TargetName => IsRemote ? _target!.MachineName : Environment.MachineName;
    /// <summary>Whether the folder box is used: always on this PC, on another PC only when the folder reaches it.</summary>
    private bool UsesFolderBox => !IsRemote || _canChooseFolder(_target!);

    private void ShowPcChoices(List<MachineInfo> targets)
    {
        foreach (var pc in targets)
        {
            var (label, accessibleName) = AddRunnerForm.PcLabel(pc);
            var choice = new RadioButton { Content = label, GroupName = "Pc", Style = (Style)FindResource("Segment"), IsChecked = pc == _target };
            AutomationProperties.SetName(choice, accessibleName);
            choice.Checked += (_, _) =>
            {
                if (_target == pc) return;
                _target = pc;
                ShowTarget();
            };
            PcChoices.Children.Add(choice);
        }
        PcPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Names the chosen PC in the title and texts, and sets the default name and folder for it.</summary>
    private void ShowTarget()
    {
        var remote = IsRemote;
        Title = remote ? $"Add runner on {TargetName}" : "Add runner";
        HeadingText.Text = remote ? $"Add a self-hosted runner on {TargetName}" : "Add a self-hosted runner";
        DescriptionText.Text = "Downloads the latest GitHub Actions runner, verifies its checksum, registers it with GitHub and installs it " +
                               $"as a Windows service on {(remote ? TargetName : "this PC")}.";
        // Another PC may have another processor.
        _alwaysAdded = remote
            ? "The labels self-hosted, Windows and X64 or ARM64 are always added."
            : $"The labels self-hosted, Windows and {RunnerService.ArchitectureLabel} are always added.";
        BrowseButton.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;

        // Disabled first, so emptying the box for a PC without one does not replace the typed folder.
        InstallPathBox.IsEnabled = UsesFolderBox;
        InstallPathBox.Text = !UsesFolderBox ? string.Empty : _pathEdited ? _typedPath : remote ? string.Empty : DefaultLocalFolder();
        if (!_nameEdited) RunnerNameBox.Text = AddRunnerForm.DefaultRunnerName(TargetName, _repository, IsTaken);
        Validate();
    }

    private string DefaultLocalFolder() => _repository.IsValid ? SuggestFolder(_repository.Url) : FreeFolder(DefaultFolderName);

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

        if (!_nameEdited) RunnerNameBox.Text = AddRunnerForm.DefaultRunnerName(TargetName, _repository, IsTaken);
        // Another PC picks a free folder in its own folder for new runners.
        if (!_pathEdited && !IsRemote && _repository.IsValid) InstallPathBox.Text = SuggestFolder(_repository.Url);
        Validate();
    }

    private bool IsTaken(string name) => AddRunnerForm.NameProblem(name, _repository.Scope, _existing) != null && AddRunnerForm.NameRuleProblem(name) == null;

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
        // GitHub names are unique for a repository or organization, so a name used on any PC is taken.
        var nameProblem = AddRunnerForm.NameProblem(RunnerNameBox.Text, _repository.Scope, _existing);
        NameHint.Text = nameProblem ?? string.Empty;
        NameHint.Foreground = Palette.Warning;
        NameHint.Visibility = nameProblem != null ? Visibility.Visible : Visibility.Collapsed;

        var labelsProblem = RunnerService.LabelsProblem(AddRunnerForm.ParseLabels(LabelsBox.Text));
        LabelsHint.Text = labelsProblem ?? _alwaysAdded;
        LabelsHint.Foreground = labelsProblem != null ? Palette.Warning : Palette.TextMuted;

        // Another PC's disk cannot be read from here: only the path itself is checked, and that PC checks the rest.
        var folderProblem = !IsRemote ? AddRunnerForm.FolderProblem(InstallPathBox.Text)
            : UsesFolderBox ? AddRunnerForm.RemoteFolderProblem(InstallPathBox.Text, TargetName)
            : null;
        InstallHint.Text = folderProblem
                           ?? (!IsRemote ? FolderRule
                               : UsesFolderBox ? $"Leave empty to use {TargetName}'s folder for new runners. {FolderRule}"
                               : $"The runner is installed in {TargetName}'s folder for new runners.");
        InstallHint.Foreground = folderProblem != null ? Palette.Warning : Palette.TextMuted;

        // This PC's problem counts only while this PC is chosen; otherwise the hint about the dashboard's PC stays.
        var pcProblem = IsRemote ? null : _thisPcProblem;
        var pcText = pcProblem ?? _missingPcHint;
        PcHint.Text = pcText ?? string.Empty;
        PcHint.Foreground = pcProblem != null ? Palette.Warning : Palette.TextMuted;
        PcHint.Visibility = pcText != null ? Visibility.Visible : Visibility.Collapsed;

        var valid = _repository.IsValid && nameProblem == null && labelsProblem == null && folderProblem == null && pcProblem == null;
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
            // Empty for another PC: its folder for new runners.
            InstallPath = UsesFolderBox ? InstallPathBox.Text.Trim() : string.Empty,
            Labels = AddRunnerForm.ParseLabels(LabelsBox.Text)
        };
        TargetPc = _target;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
