using System.Windows;

namespace RunnerManager;

public partial class EditRunnerWindow : Window
{
    private readonly string _currentName;
    private readonly string[] _currentLabels;
    private readonly bool _canClearLabels;
    private readonly string _alwaysAdded;
    public RunnerEditRequest? Edit { get; private set; }

    /// <param name="canClearLabels">
    /// Whether the runner's PC can remove every custom label. Without that, an empty list keeps the current labels, so
    /// emptying the box is refused instead of silently doing nothing.
    /// </param>
    public EditRunnerWindow(MachineRunnerStatus runner, bool canClearLabels = false)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _currentName = runner.AgentName;
        _canClearLabels = canClearLabels;
        // Runners on other PCs report their labels in their status; for runners on this PC they are read from GitHub.
        // A PC can send null for the list.
        _currentLabels = runner.CustomLabels is { Length: > 0 } reported ? reported : runner.LocalRunner?.CustomLabels ?? Array.Empty<string>();
        _alwaysAdded = runner.IsLocalMachine
            ? $"The labels self-hosted, Windows and {RunnerService.ArchitectureLabel} are always added."
            : "The labels self-hosted, Windows and X64 or ARM64 are always added.";
        RunnerNameBox.Text = runner.AgentName;
        LabelsBox.Text = string.Join(", ", _currentLabels);
        SubtitleText.Text = $"{runner.RepositoryText}  ·  {runner.MachineName}";
        RunnerNameBox.TextChanged += (_, _) => Check();
        LabelsBox.TextChanged += (_, _) => Check();
        Check();
        Loaded += (_, _) => { RunnerNameBox.Focus(); RunnerNameBox.SelectAll(); };
    }

    private EditRunnerForm.Result Check()
    {
        var result = EditRunnerForm.Check(_currentName, _currentLabels, RunnerNameBox.Text, LabelsBox.Text, _canClearLabels);
        NameHint.Text = result.NameProblem ?? string.Empty;
        NameHint.Foreground = Palette.Warning;
        NameHint.Visibility = result.NameProblem != null ? Visibility.Visible : Visibility.Collapsed;
        LabelsHint.Text = result.LabelsProblem
                          ?? (result.Request is { ClearLabels: true } ? $"Every custom label is removed. {_alwaysAdded}"
                              : _currentLabels.Length == 0 ? $"{_alwaysAdded} Leave blank to keep the current labels." : _alwaysAdded);
        LabelsHint.Foreground = result.LabelsProblem != null ? Palette.Warning : Palette.TextMuted;
        SaveButton.IsEnabled = result.CanSave;
        return result;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Check().Request is not { } request) return;
        Edit = request;
        DialogResult = true;
    }
}

/// <param name="ClearLabels">Remove every custom label: an empty <paramref name="Labels"/> alone keeps the current ones.</param>
public sealed record RunnerEditRequest(string RunnerName, string[] Labels, bool ClearLabels = false);
