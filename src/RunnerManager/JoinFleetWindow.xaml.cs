using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace RunnerManager;

/// <summary>"Set up a fleet": start a new fleet, or find a PC that is hosting pairing and join it.</summary>
public partial class JoinFleetWindow : Window
{
    /// <summary>How long the search finds nothing before the window says what to check: about three searches.</summary>
    private static readonly TimeSpan NothingFoundAfter = TimeSpan.FromSeconds(9);

    private readonly DispatcherTimer _search = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly FoundHosts _hosts = new();
    private readonly Stopwatch _nothingFound = new();
    private CancellationTokenSource? _joinCts;
    private TaskCompletionSource<bool>? _codesMatch;
    private bool _searching;
    private bool _finishing;

    public bool StartNewFleet { get; private set; }
    public byte[]? PairingKey { get; private set; }
    public string HostName { get; private set; } = string.Empty;
    public string HostAddress { get; private set; } = string.Empty;
    /// <summary>The host allowed this PC one way: <see cref="PairingKey"/> is its own code, not the fleet's.</summary>
    public bool Managed { get; private set; }

    public JoinFleetWindow(bool openJoinPage = false)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        HostList.ItemsSource = _hosts.Items;
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
        _hosts.Clear();
        _nothingFound.Restart();
        ShowSearchState();
        _search.Start();
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_searching || FindPanel.Visibility != Visibility.Visible) return;
        _searching = true;
        try
        {
            var found = await Pairing.FindHostsAsync(TimeSpan.FromSeconds(1.5));
            // The list takes in what differs and keeps the rest, so a row keeps its focus and a click on it is not lost.
            if (FindPanel.Visibility == Visibility.Visible) _hosts.Update(found);
        }
        catch { }
        finally { _searching = false; }
        if (FindPanel.Visibility == Visibility.Visible) ShowSearchState();
    }

    private void ShowSearchState()
    {
        var none = _hosts.Items.Count == 0;
        if (!none) _nothingFound.Restart();
        SearchingPanel.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NothingFoundPanel.Visibility = none && _nothingFound.Elapsed >= NothingFoundAfter ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Pairing.Host host }) return;
        if (host.IsOutdated)
        {
            DialogWindow.Info(this, "Update Runner Manager first",
                $"{host.Name} runs an older Runner Manager that cannot pair with this one. Update Runner Manager on {host.Name}, then try again.");
            return;
        }
        await JoinAsync(host.Address, host.Name);
    }

    private async void ConnectAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) return;
        await JoinAsync(address, address);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ConnectAddress_Click(sender, e);
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        PasteCode_Click(sender, e);
    }

    private void PasteCode_Click(object sender, RoutedEventArgs e)
    {
        var key = CommandSigner.ParseKey(CodeBox.Text);
        if (key == null)
        {
            DialogWindow.Info(this, "Invalid fleet code", "Paste the full fleet code copied from another PC. It starts with rwk_.");
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
        JoinProgress.Visibility = WaitText.Visibility = Visibility.Visible;
        MismatchText.Visibility = Visibility.Collapsed;
        // Hidden keeps its place: Back and Cancel must not move under the pointer when the button appears.
        ConfirmButton.Visibility = Visibility.Hidden;
        SetFinishing(false);
        var cts = _joinCts = new CancellationTokenSource();
        try
        {
            Pairing.JoinResult result;
            try
            {
                // Not on this thread: the first use writes the ID to the settings file, which can be busy for a while.
                var machineId = await Task.Run(LocalMachineId);
                result = await Pairing.JoinAsync(address, Environment.MachineName, code => Dispatcher.Invoke(() =>
                {
                    CodeText.Text = code;
                    ConfirmText.Text = $"Check that {label} shows the same 6-digit code. Then click Allow on {label}.";
                    WaitText.Text = $"Waiting for {label}…";
                }), offer => Dispatcher.Invoke(() => AskCodesMatch(label, offer, cts)), cts.Token, machineId);
            }
            finally { SetFinishing(false); }
            // Closed or left in the moment the answer arrived: the code is not used.
            if (cts.IsCancellationRequested)
            {
                CryptographicOperations.ZeroMemory(result.PairingKey);
                return;
            }
            PairingKey = result.PairingKey;
            HostName = result.HostName;
            HostAddress = result.HostAddress;
            Managed = result.Managed;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (!IsLoaded) { }
        // Back was clicked: the page has already changed, so there is nothing to report.
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
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

    /// <summary>
    /// This PC's machine ID for the other PC, which tells this PC from another one with the same name by it. Without
    /// one the join goes on as from an older PC, so a settings file that cannot be read does not stop it here.
    /// </summary>
    private static string? LocalMachineId()
    {
        try { return FleetConfig.LocalMachineId; }
        catch { return null; }
    }

    /// <summary>
    /// The other PC has allowed this PC and sent a code. Whoever answered under that PC's name could do that by itself,
    /// so the code is used only once the user says here that both PCs showed the same number.
    /// </summary>
    private Task<bool> AskCodesMatch(string label, Pairing.JoinOffer offer, CancellationTokenSource cts)
    {
        // Back was clicked in the moment the answer arrived: the page has already changed.
        if (cts.IsCancellationRequested) return Task.FromResult(false);
        var answer = _codesMatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HeadingText.Text = "Do the codes match?";
        var allowed = offer.Managed
            ? $"{label} allowed this PC and will manage it one way. The fleet can view and control this PC. This PC cannot see the PCs of the fleet."
            : $"{label} allowed this PC to join the fleet.";
        ConfirmText.Text = $"{allowed} Click “The codes match” only if {label} showed the same code.";
        MismatchText.Text = $"If {label} showed another code, or did not ask about this PC, click Cancel. Another device may have answered in its place.";
        JoinProgress.Visibility = WaitText.Visibility = Visibility.Collapsed;
        MismatchText.Visibility = Visibility.Visible;
        ConfirmButton.Visibility = Visibility.Visible;
        return answer.Task;
    }

    private void CodesMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_codesMatch is not { } answer) return;
        // The other PC is told now, and adds this PC before it answers. Until that answer there is no way back:
        // leaving now could leave the other PC with a PC that has no code.
        SetFinishing(true);
        answer.TrySetResult(true);
    }

    /// <summary>
    /// While the other PC finishes, Back and Cancel are off and the window stays open (see <see cref="OnClosing"/>).
    /// It ends by itself: with the answer, or after half a minute without one.
    /// </summary>
    private void SetFinishing(bool finishing)
    {
        _finishing = finishing;
        ConfirmButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = !finishing;
        ConfirmButton.Content = finishing ? "Finishing…" : "The codes match";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_finishing) e.Cancel = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Also discards a code that waits for "The codes match".
        _joinCts?.Cancel();
        _codesMatch = null;
        _search.Stop();
        HeadingText.Text = "Set up a fleet";
        ChoicePanel.Visibility = Visibility.Visible;
        FindPanel.Visibility = ConfirmPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = ConfirmButton.Visibility = Visibility.Collapsed;
    }

    private void OtherWays_Click(object sender, RoutedEventArgs e)
    {
        var show = OtherWaysPanel.Visibility != Visibility.Visible;
        OtherWaysPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OtherWaysButton.Content = show ? "Other ways to join ▴" : "Other ways to join ▾";
    }
}
