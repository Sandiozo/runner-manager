using System.Windows;
using System.Windows.Threading;

namespace RunnerManager;

/// <summary>"Add a PC": accepts join requests from new PCs for ten minutes.</summary>
public partial class PairingHostWindow : Window
{
    private readonly Pairing.PairingHost _host;
    private readonly Queue<Pairing.JoinRequest> _pending = new();
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromMinutes(10) };
    private Pairing.JoinRequest? _current;
    private readonly List<string> _joined = new();

    /// <summary>Names of the PCs that joined while the window was open.</summary>
    public IReadOnlyList<string> JoinedPcs => _joined;

    public PairingHostWindow(byte[] pairingKey)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        ThisPcRun.Text = Environment.MachineName;
        AddressText.Text = $"On another network, enter one of these on the other PC under “Other ways to join”: {Environment.MachineName}, " +
                           string.Join(", ", FleetNetwork.LocalAddresses());

        _host = new Pairing.PairingHost(pairingKey, Environment.MachineName);
        _host.JoinRequested += request => Dispatcher.Invoke(() => Enqueue(request));
        _host.Joined += name => Dispatcher.Invoke(() =>
        {
            _joined.Add(name);
            JoinedText.Text = $"✓  {string.Join(", ", _joined)} joined. It appears in the sidebar within a few seconds. Add more PCs, or click Done.";
            JoinedText.Visibility = Visibility.Visible;
        });
        _expiry.Tick += (_, _) => Close();
        Loaded += async (_, _) =>
        {
            try
            {
                await FleetNetwork.EnsureFirewallRulesAsync();
                _host.Start();
                _expiry.Start();
            }
            catch (Exception ex)
            {
                DialogWindow.Error(this, "Could not start pairing", $"{ex.Message}\n\nCheck that no other Runner Manager window is already waiting for a PC.");
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _expiry.Stop();
            _current?.Deny();
            while (_pending.TryDequeue(out var request)) request.Deny();
            _host.Dispose();
        };
    }

    private void Enqueue(Pairing.JoinRequest request)
    {
        if (_current != null) { _pending.Enqueue(request); return; }
        ShowRequest(request);
    }

    private void ShowRequest(Pairing.JoinRequest request)
    {
        _current = request;
        RequestTitle.Text = $"{request.Name} wants to join";
        CodeText.Text = request.Code;
        WaitingPanel.Visibility = Visibility.Collapsed;
        RequestPanel.Visibility = Visibility.Visible;
        AllowButton.Visibility = DenyButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = CopyCodeButton.Visibility = Visibility.Collapsed;
        Activate();
        AllowButton.Focus();
    }

    private void Next()
    {
        _current = null;
        if (_pending.TryDequeue(out var next)) { ShowRequest(next); return; }
        WaitingPanel.Visibility = Visibility.Visible;
        RequestPanel.Visibility = Visibility.Collapsed;
        AllowButton.Visibility = DenyButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = CopyCodeButton.Visibility = Visibility.Visible;
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        _current?.Allow();
        Next();
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        _current?.Deny();
        Next();
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (FleetConfig.Current.FleetKey is not { } key) return;
        try
        {
            Clipboard.SetText(CommandSigner.FormatKey(key));
            CopyCodeButton.Content = "Copied";
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
