using System.Windows;
using System.Windows.Automation;

namespace RunnerManager;

/// <summary>
/// The "Needs attention" tile: how many PCs are offline, and runners stopped or with a concern, over the whole fleet.
/// Pressed, the dashboard shows only those PCs and runners, together with the search and the PC selected in the sidebar.
/// </summary>
public partial class MainWindow
{
    /// <summary>The tile is pressed: only what it counts is shown.</summary>
    private bool _attentionOnly;

    internal bool AttentionOnly => _attentionOnly;

    /// <summary>Counts again. At zero the tile is hidden, and a pressed tile lets go, so nothing stays filtered.</summary>
    private void UpdateAttentionTile(IReadOnlyCollection<MachineInfo> machines)
    {
        var attention = FleetAttention.Count(machines);
        if (attention.Total == 0 && _attentionOnly)
        {
            ShowAttentionOnly(false);
            return;
        }
        AttentionTile.Visibility = attention.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        TileAttention.Text = attention.Total.ToString();
        TileAttentionSub.Text = attention.SubText;
        AttentionTile.ToolTip = attention.Summary + (_attentionOnly ? " Click to show everything again." : " Click to show only these.");
        AutomationProperties.SetName(AttentionTile, $"Needs attention, {attention.Total}");
        AutomationProperties.SetHelpText(AttentionTile, attention.Summary);
    }

    private void AttentionTile_Click(object sender, RoutedEventArgs e) => ShowAttentionOnly(AttentionTile.IsChecked == true);

    /// <summary>Shows only what needs attention, or everything again.</summary>
    internal void ShowAttentionOnly(bool on)
    {
        _attentionOnly = on;
        AttentionTile.IsChecked = on;
        ApplyFilter();
        UpdateAttentionTile(Machines.ToList());
    }
}
