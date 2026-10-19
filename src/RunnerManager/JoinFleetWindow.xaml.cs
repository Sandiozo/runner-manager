using System.Windows;
using System.Windows.Threading;

namespace RunnerManager;

/// <summary>"Set up a fleet": start a new fleet, or find a PC that is hosting pairing and join it.</summary>
public partial class JoinFleetWindow : Window
{
    private readonly DispatcherTimer _search = new() { Interval = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _joinCts;
    private bool _searching;

    public bool StartNewFleet { get; private set; }
    public byte[]? PairingKey { get; private set; }
    public string HostName { get; private set; } = string.Empty;
    public string HostAddress { get; private set; } = string.Empty;

    public JoinFleetWindow(bool openJoinPage = false)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        if (openJoinPage) Loaded += (_, _) => ChooseJoin_Click(this, new RoutedEventArgs());
        _search.Tick += async (_, _) => await SearchAsync();
        Closed += (_, _) =>
        {
            _search.Stop();
            _joinCts?.Cancel();
        };
    }

    private void ChooseNew_Click(object sender, RoutedEventArgs e)
    {
        StartNewFleet = true;
        DialogResult = true;
    }

    private async void ChooseJoin_Click(object sender, RoutedEventArgs e)
    {
        HeadingText.Text = "Join an existing fleet";
        ChoicePanel.Visibility = Visibility.Collapsed;
        FindPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        _search.Start();
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_searching || FindPanel.Visibility != Visibility.Visible) return;
        _searching = true;
        try
        {
            var hosts = await Pairing.FindHostsAsync(TimeSpan.FromSeconds(1.5));
            HostList.ItemsSource = hosts;
            SearchingPanel.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
        finally { _searching = false; }
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Pairing.Host host }) await JoinAsync(host.Address, host.Name);
    }

    private async void ConnectAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) return;
        await JoinAsync(address, address);
    }

    private void PasteCode_Click(object sender, RoutedEventArgs e)
    {
        var key = CommandSigner.ParseKey(CodeBox.Text);
        if (key == null)
        {
            DialogWindow.Info(this, "Invalid pairing code", "Paste the full pairing code copied from another PC. It starts with rwk_.");
            return;
        }
        PairingKey = key;
        DialogResult = true;
    }

    private async Task JoinAsync(string address, string label)
    {
        _search.Stop();
        FindPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        HeadingText.Text = $"Joining {label}";
        ConfirmText.Text = $"Connecting to {label}…";
        CodeText.Text = "··· ···";
        WaitText.Text = string.Empty;
        _joinCts = new CancellationTokenSource();
        try
        {
            var result = await Pairing.JoinAsync(address, Environment.MachineName, code => Dispatcher.Invoke(() =>
            {
                CodeText.Text = code;
                ConfirmText.Text = $"Check that {label} shows the same 6-digit code. Then click Allow on {label}.";
                WaitText.Text = $"Waiting for {label}…";
            }), _joinCts.Token);
            PairingKey = result.PairingKey;
            HostName = result.HostName;
            HostAddress = result.HostAddress;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (!IsLoaded) { }
        catch (Exception ex)
        {
            if (!IsLoaded) return;
            DialogWindow.Error(this, "Could not join the fleet", ex is System.Net.Sockets.SocketException or OperationCanceledException
                ? $"Could not connect to {label}. On {label}, click “Add a PC” in the sidebar, then try again."
                : ex.Message);
            Back_Click(this, new RoutedEventArgs());
            ChooseJoin_Click(this, new RoutedEventArgs());
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _joinCts?.Cancel();
        _search.Stop();
        HeadingText.Text = "Set up a fleet";
        ChoicePanel.Visibility = Visibility.Visible;
        FindPanel.Visibility = ConfirmPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
    }

    private void OtherWays_Click(object sender, RoutedEventArgs e)
    {
        var show = OtherWaysPanel.Visibility != Visibility.Visible;
        OtherWaysPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OtherWaysButton.Content = show ? "Other ways to join ▴" : "Other ways to join ▾";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
