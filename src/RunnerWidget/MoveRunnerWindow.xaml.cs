using System.Windows;

namespace RunnerWidget;

public partial class MoveRunnerWindow : Window
{
    public MachineInfo? Target { get; private set; }
    public bool DeleteOldFiles { get; private set; }

    public MoveRunnerWindow(MachineRunnerStatus runner, IReadOnlyList<MachineInfo> candidates)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        HeadingText.Text = $"Move {runner.TitleText}";
        SubtitleText.Text = $"{runner.RepositoryText}  ·  currently on {runner.MachineName}";
        DeleteOldText.Text = $"Delete the runner files on {runner.MachineName}";
        TargetList.ItemsSource = candidates;
        if (candidates.Count == 1) TargetList.SelectedIndex = 0;
    }

    private void TargetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        MoveButton.IsEnabled = TargetList.SelectedItem != null;
        if (TargetList.SelectedItem is MachineInfo machine) MoveButton.Content = $"Move to {machine.MachineName}";
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        Target = TargetList.SelectedItem as MachineInfo;
        if (Target == null) return;
        DeleteOldFiles = DeleteOldToggle.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
