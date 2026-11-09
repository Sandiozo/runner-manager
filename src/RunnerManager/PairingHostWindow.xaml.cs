using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace RunnerManager;

/// <summary>"Add a PC": accepts join requests from new PCs for ten minutes.</summary>
public partial class PairingHostWindow : Window
{
    /// <summary>How long the window waits for a request before it closes by itself.</summary>
    private static readonly TimeSpan OpenFor = TimeSpan.FromMinutes(10);

    private readonly Pairing.PairingHost _host;
    private readonly Queue<Pairing.JoinRequest> _pending = new();
    private readonly DispatcherTimer _expiry = new() { Interval = OpenFor };
    private Pairing.JoinRequest? _current;
    private bool _listening;
    private bool _closed;
    // Allowed requests whose code is on its way; a PC allowed one way has its own code here until it is saved.
    private readonly Dictionary<Pairing.JoinRequest, byte[]?> _sending = new();
    private readonly List<string> _joined = new();
    private readonly List<string> _managed = new();
    private readonly List<string> _allowed = new();
    private readonly List<string> _notAdded = new();

    /// <summary>Names of the PCs that joined while the window was open.</summary>
    public IReadOnlyList<string> JoinedPcs => _joined;
    /// <summary>Names of the PCs that were paired one way while the window was open.</summary>
    public IReadOnlyList<string> ManagedPcs => _managed;

    public PairingHostWindow(byte[] pairingKey)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        ThisPcRun.Text = Environment.MachineName;
        AddressText.Text = $"On another network, enter one of these on the other PC under “Other ways to join”: {Environment.MachineName}, " +
                           string.Join(", ", FleetNetwork.LocalAddresses());
        ExpiryText.Text = $"This window closes by itself after {OpenFor.TotalMinutes:0} minutes without a request.";

        _host = new Pairing.PairingHost(pairingKey, Environment.MachineName);
        _host.JoinRequested += request => Dispatcher.Invoke(() => Enqueue(request));
        _host.Joined += request => Dispatcher.Invoke(() => OnJoined(request));
        _expiry.Tick += (_, _) => Close();
        Loaded += async (_, _) =>
        {
            try
            {
                await FleetNetwork.EnsureFirewallRulesAsync();
                // Closed while the firewall rules were checked: the host is stopped, and must not take the ports now.
                if (_closed) return;
                _host.Start();
                _listening = true;
                _expiry.Start();
            }
            catch (Exception ex)
            {
                if (_closed) return;
                DialogWindow.Error(this, "Could not start pairing", $"{ex.Message}\n\nCheck that no other Runner Manager window is already waiting for a PC.");
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _expiry.Stop();
            // Denies the open requests and lets the other PCs hear it before the connections close.
            _ = _host.StopAsync();
        };
    }

    private void Enqueue(Pairing.JoinRequest request)
    {
        if (request.Ended.IsCompleted) return;
        // The host stops waiting after a few minutes, or when the other PC gives up: take the request off the screen then.
        request.Ended.ContinueWith(_ => Dispatcher.BeginInvoke(() => Remove(request)), TaskScheduler.Default);
        if (_current != null) { _pending.Enqueue(request); return; }
        ShowRequest(request);
    }

    private void Remove(Pairing.JoinRequest request)
    {
        // Allowed, but it ended before its code was sent: the other PC left, or the connection broke.
        if (_sending.Remove(request))
        {
            _notAdded.Add($"{request.Name} left before it got its code. It was not added.");
            ShowResults();
        }
        if (_current == request)
        {
            Next();
            return;
        }
        var others = _pending.Where(r => r != request).ToList();
        _pending.Clear();
        foreach (var other in others) _pending.Enqueue(other);
    }

    private void ShowRequest(Pairing.JoinRequest request)
    {
        _current = request;
        // A request on screen keeps the window open; the time starts again when the last one is answered.
        _expiry.Stop();
        RequestTitle.Text = $"{request.Name} wants to join";
        CodeText.Text = request.Code;
        // Nothing is chosen for the user, for any PC: with a choice already made, a stray key or click on Allow would
        // hand over the fleet's code.
        FullAccessOption.IsChecked = ManagedAccessOption.IsChecked = false;
        ManagedAccessOption.IsEnabled = request.CanBeManaged;
        ManagedUnavailableText.Visibility = request.CanBeManaged ? Visibility.Collapsed : Visibility.Visible;
        AccessOption_Checked(this, new RoutedEventArgs());
        WaitingPanel.Visibility = Visibility.Collapsed;
        RequestPanel.Visibility = Visibility.Visible;
        AllowButton.Visibility = DenyButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = CopyCodeButton.Visibility = Visibility.Collapsed;
        Activate();
        // No option and no button has the keyboard focus, for the same reason: what the user was typing as the request
        // appeared must not answer it.
        FocusManager.SetFocusedElement(this, null);
        if (IsKeyboardFocusWithin) Keyboard.ClearFocus();
    }

    private void Next()
    {
        _current = null;
        if (_pending.TryDequeue(out var next)) { ShowRequest(next); return; }
        WaitingPanel.Visibility = Visibility.Visible;
        RequestPanel.Visibility = Visibility.Collapsed;
        AllowButton.Visibility = DenyButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = CopyCodeButton.Visibility = Visibility.Visible;
        _expiry.Stop();
        if (_listening && !_closed) _expiry.Start();
    }

    private void AccessOption_Checked(object sender, RoutedEventArgs e)
    {
        if (AllowButton == null) return;
        var chosen = FullAccessOption.IsChecked == true || ManagedAccessOption.IsChecked == true;
        AllowButton.Content = ManagedAccessOption.IsChecked == true ? "Allow one way" : "Allow";
        AllowButton.IsEnabled = chosen;
        AllowNoteText.Text = chosen ? "Allow only if the codes match." : "First choose what it can do. Allow only if the codes match.";
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } request || AllowButton.IsEnabled != true) return;
        if (ManagedAccessOption.IsChecked == true)
        {
            if (!request.CanBeManaged) return;
            // The PC is saved once it has its code (see OnJoined). Saving replaces a pairing of the same name across
            // the fleet, so saving first would lose a working pairing whenever the new code does not arrive. Check
            // now that the settings can be read, so that the PC does not get a code that cannot be saved.
            try { FleetConfig.UpdateIf(_ => false); }
            catch (Exception ex)
            {
                DialogWindow.Error(this, "Could not save the PC", ex.Message);
                return;
            }
            var key = CommandSigner.NewKey();
            _sending[request] = key;
            request.AllowManaged(key);
        }
        else if (FullAccessOption.IsChecked == true)
        {
            _sending[request] = null;
            request.Allow();
        }
        else return;
        Next();
    }

    /// <summary>The PC has its code.</summary>
    private void OnJoined(Pairing.JoinRequest request)
    {
        if (_sending.Remove(request, out var ownCode) && ownCode != null)
        {
            try { FleetConfig.AddManagedPc(request.Name, FleetNetwork.WithPort(request.Address), ownCode); }
            catch (Exception ex)
            {
                DialogWindow.Error(this, "Could not save the PC",
                    $"{request.Name} has its code, but this PC could not save it, so the fleet cannot manage {request.Name}.\n\n{ex.Message}\n\n" +
                    $"On {request.Name}, open Settings → Fleet and click Disconnect. Then add it again.");
                return;
            }
            _managed.Add(request.Name);
            _allowed.Add($"✓  {request.Name} was allowed, managed one way. Its code was {request.Code}.");
        }
        else
        {
            _joined.Add(request.Name);
            // Discovery finds it only on the local network, so remember where it joined from (over Tailscale, for example).
            try { MachineRelay.AddKnownPeer(request.Name, request.Address); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _allowed.Add($"✓  {request.Name} was allowed to join. Its code was {request.Code}.");
        }
        ShowResults();
    }

    private void ShowResults()
    {
        // The codes stay on screen: the other PC asks its user whether the codes match before it accepts its code.
        JoinedText.Text = string.Join("\n", _allowed);
        JoinedText.Visibility = JoinedNoteText.Visibility = _allowed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotAddedText.Text = string.Join("\n", _notAdded);
        NotAddedText.Visibility = _notAdded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        _current?.Deny();
        Next();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Done, the cancel button, is hidden while a request is shown. Esc then answers no, the safe answer.
        if (e.Key != Key.Escape || _current == null) return;
        e.Handled = true;
        Deny_Click(this, new RoutedEventArgs());
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FleetConfig.Current.FleetKey is not { } key) throw new InvalidOperationException("This PC has no pairing code.");
            Clipboard.SetText(CommandSigner.FormatKey(key));
            CopyCodeButton.Content = "Copied";
            CopyNoteText.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { DialogWindow.Error(this, "Could not copy the pairing code", ex.Message); }
    }
}
