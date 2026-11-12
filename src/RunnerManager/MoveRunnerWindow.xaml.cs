using System.Windows;

namespace RunnerManager;

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
        System.Windows.Automation.AutomationProperties.SetName(DeleteOldToggle, DeleteOldText.Text);
        TargetList.ItemsSource = candidates;
        if (candidates.Count == 1) TargetList.SelectedIndex = 0;
    }

    private void TargetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        MoveButton.IsEnabled = TargetList.SelectedItem != null;
        // In a TextBlock: string content would drop an underscore in the PC's name, read as an access key.
        if (TargetList.SelectedItem is MachineInfo machine) MoveButton.Content = new System.Windows.Controls.TextBlock { Text = $"Move to {machine.MachineName}" };
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        Target = TargetList.SelectedItem as MachineInfo;
        if (Target == null) return;
        DeleteOldFiles = DeleteOldToggle.IsChecked == true;
        DialogResult = true;
    }
}
