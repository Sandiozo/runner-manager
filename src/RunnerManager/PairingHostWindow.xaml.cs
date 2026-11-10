using System.ComponentModel;
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
    // Allowed requests that are not added yet: the code is on its way, or the other PC's user has not confirmed it.
    // A PC allowed one way has its own code here until it is saved.
    private readonly Dictionary<Pairing.JoinRequest, byte[]?> _sending = new();
    // Those of them that have their code and ask their user whether the codes match.
    private readonly List<Pairing.JoinRequest> _confirming = new();
    private readonly List<string> _joined = new();
    private readonly List<string> _managed = new();
    private readonly List<string> _added = new();
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
        _host.Confirming += request => Dispatcher.Invoke(() => OnConfirming(request));
        // Not posted: a PC that confirms waits until it is saved here, and is told when that fails.
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
        _confirming.Remove(request);
        // Allowed, but it ended before it was added: the other PC left or the connection broke, its user did not
        // confirm the codes there, or its answer did not pass the check (its name was changed on the way, for example).
        if (_sending.Remove(request, out var ownCode))
        {
            _notAdded.Add(request.CheckFailed ? $"{request.Name} was not added. The pairing check failed. Try again."
                : request.CodeSent ? $"{request.Name} did not confirm the codes. It was not added."
                // A PC allowed one way gets a code of its own, which is not the fleet code.
                : ownCode != null ? $"{request.Name} left before it got its code. It was not added."
                : $"{request.Name} left before it got the fleet code. It was not added.");
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
        var sameName = SameNameWarning(FleetConfig.Current, request.Name, request.MachineId);
        SameNameText.Text = sameName ?? string.Empty;
        SameNamePanel.Visibility = sameName != null ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// What to tell the user when the name of a joining PC is taken here already. The joining PC chooses its name, and
    /// a PC that is saved takes the place of the one saved under that name. Null when the name is free, or when the PC
    /// sent the machine ID of the PC that has the name: then it is that PC, joining again.
    /// </summary>
    /// <param name="machineId">The joining PC's machine ID, or null from a PC that sends none.</param>
    /// <param name="thisPc">The name of this PC; tests give one.</param>
    internal static string? SameNameWarning(FleetConfig config, string name, string? machineId, string? thisPc = null)
    {
        var key = MachineRelay.Sanitize(name);
        // A saved PC without an ID (it has an older agent, or was not reached yet) cannot be told to be the same PC.
        bool IsAnother(string knownName, string knownId)
            => MachineRelay.Sanitize(knownName) == key
               && (machineId == null || !knownId.Equals(machineId, StringComparison.OrdinalIgnoreCase));
        // Never the same PC, whatever ID it sends.
        if (key == MachineRelay.Sanitize(thisPc ?? Environment.MachineName))
            return $"This PC is also named {name}. Two PCs in one fleet cannot have the same name. Rename one of them in Windows first.";
        if (config.KnownPeers.Any(p => IsAnother(p.Name, p.MachineId)))
            return $"A PC named {name} is already in this fleet. Allow only if this is the same PC.";
        if (config.ManagedPcs.Any(p => IsAnother(p.Name, p.MachineId)))
            return $"This fleet already manages a PC named {name}. Allow only if this is the same PC.";
        return null;
    }

    private void Next()
    {
        _current = null;
        if (_pending.TryDequeue(out var next)) { ShowRequest(next); return; }
        WaitingPanel.Visibility = Visibility.Visible;
        RequestPanel.Visibility = Visibility.Collapsed;
        AllowButton.Visibility = DenyButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = CopyCodeButton.Visibility = Visibility.Visible;
        RestartExpiry();
    }

    /// <summary>
    /// The window closes by itself only while nothing goes on. A request on screen keeps it open, and so does a PC
    /// whose user has not confirmed the codes yet. The time starts again when the last of them is done.
    /// </summary>
    private void RestartExpiry()
    {
        _expiry.Stop();
        if (_listening && !_closed && _current == null && _confirming.Count == 0) _expiry.Start();
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
            // The PC is saved once it has joined (see OnJoined). Saving replaces a pairing of the same name across
            // the fleet, so saving first would lose a working pairing whenever the new code does not arrive or is not
            // confirmed. Check now that the settings can be read, so that the PC does not get a code that cannot be saved.
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

    /// <summary>The PC has its code, and asks its user whether the codes match.</summary>
    private void OnConfirming(Pairing.JoinRequest request)
    {
        if (_closed || !_sending.ContainsKey(request)) return;
        _confirming.Add(request);
        ShowResults();
    }

    /// <summary>The PC has its code, and its user has confirmed the codes when the PC can say so.</summary>
    private void OnJoined(Pairing.JoinRequest request)
    {
        // The window closed with the warning that this PC is not added. The PC waits for the answer, and gets a no.
        if (_closed && request.Confirms) throw new InvalidOperationException("“Add a PC” is closed.");
        _confirming.Remove(request);
        if (_sending.Remove(request, out var ownCode) && ownCode != null)
        {
            try { FleetConfig.AddManagedPc(request.Name, FleetNetwork.WithPort(request.Address), ownCode); }
            catch (Exception ex) when (request.Confirms)
            {
                // The PC is told, and drops its code. The dialog comes after that, so that the PC does not wait for it.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closed) DialogWindow.Error(this, "Could not save the PC", $"This PC could not save {request.Name}, so it was not added.\n\n{ex.Message}\n\nTry again.");
                });
                _notAdded.Add($"{request.Name} was not added. This PC could not save it.");
                ShowResults();
                throw;
            }
            catch (Exception ex)
            {
                // An older PC has kept its code already.
                DialogWindow.Error(this, "Could not save the PC",
                    $"{request.Name} has its code, but this PC could not save it, so the fleet cannot manage {request.Name}.\n\n{ex.Message}\n\n" +
                    $"On {request.Name}, open Settings → Fleet and click Disconnect. Then add it again.");
                ShowResults();
                return;
            }
            _managed.Add(request.Name);
            _added.Add($"✓  {request.Name} is managed one way.");
        }
        else
        {
            _joined.Add(request.Name);
            // Discovery finds it only on the local network, so remember where it joined from (over Tailscale, for example).
            try { MachineRelay.AddKnownPeer(request.Name, request.Address); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _added.Add($"✓  {request.Name} joined the fleet.");
        }
        ShowResults();
    }

    private void ShowResults()
    {
        // The code stays on screen while the other PC asks its user whether the codes match.
        ConfirmingText.Text = string.Join("\n", _confirming.Select(r =>
            $"Waiting for {r.Name} to confirm. On {r.Name}, check that it shows the code {r.Code}, then click “The codes match”. Keep this window open until then."));
        ConfirmingText.Visibility = _confirming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        JoinedText.Text = string.Join("\n", _added);
        JoinedText.Visibility = JoinedNoteText.Visibility = _added.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotAddedText.Text = string.Join("\n", _notAdded);
        NotAddedText.Visibility = _notAdded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestartExpiry();
    }

    /// <summary>Closing ends the wait for a PC whose user has not confirmed the codes yet, so ask first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _confirming.Count == 0) return;
        var names = string.Join(", ", _confirming.Select(r => r.Name));
        var message = _confirming.Count == 1
            ? $"{names} has not confirmed the codes yet. If this window closes now, {names} is not added."
            : $"These PCs have not confirmed the codes yet: {names}. If this window closes now, they are not added.";
        // A danger: "Close anyway" must not be what a stray Enter chooses.
        if (!DialogWindow.Confirm(this, "Close “Add a PC” now?", message, "Close anyway", danger: true)) e.Cancel = true;
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
            if (FleetConfig.Current.FleetKey is not { } key) throw new InvalidOperationException("This PC has no fleet code.");
            Clipboard.SetText(CommandSigner.FormatKey(key));
            CopyCodeButton.Content = "Copied";
            CopyNoteText.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { DialogWindow.Error(this, "Could not copy the fleet code", ex.Message); }
    }
}
