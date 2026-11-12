using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
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
    /// <summary>
    /// Whether the request on screen can be allowed: not for a PC with this PC's name or machine ID, or with characters
    /// in its name that cannot be seen.
    /// </summary>
    private bool _canAllow;
    /// <summary>This PC's machine ID, read before the window takes requests; empty when it is not known.</summary>
    private string _thisPcId = string.Empty;
    private bool _listening;
    private bool _closed;
    // Allowed requests that are not added yet: the code is on its way, or the other PC's user has not confirmed it.
    // A PC allowed one way has its access code here until it is saved.
    private readonly Dictionary<Pairing.JoinRequest, byte[]?> _sending = new();
    // Those of them that have their code and ask their user whether the codes match.
    private readonly List<Pairing.JoinRequest> _confirming = new();
    private readonly List<string> _joined = new();
    private readonly List<string> _managed = new();
    private readonly List<string> _added = new();
    private readonly List<string> _notAdded = new();
    // PCs that joined with the fleet code but could not be saved here: a next request with the name or the machine ID
    // of one of them is still warned about, as if it was allowed a moment ago.
    private readonly List<(string Name, string? MachineId)> _notSaved = new();

    /// <summary>Names of the PCs that joined while the window was open.</summary>
    public IReadOnlyList<string> JoinedPcs => _joined;
    /// <summary>Names of the PCs that were paired one way while the window was open.</summary>
    public IReadOnlyList<string> ManagedPcs => _managed;

    /// <summary>Shows an error over the window, with its heading and message; tests replace it.</summary>
    internal Action<string, string> ShowError { get; set; }

    public PairingHostWindow(byte[] pairingKey)
    {
        ShowError = (heading, message) => DialogWindow.Error(this, heading, message);
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
                _thisPcId = await Task.Run(LocalMachineId);
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

    /// <summary>
    /// This PC's machine ID, or empty. As in "Join a fleet", a settings file that cannot be read does not stop pairing:
    /// without the ID, only the check for a PC that sends this PC's ID is left out.
    /// </summary>
    private static string LocalMachineId()
    {
        try { return FleetConfig.LocalMachineId; }
        catch { return string.Empty; }
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
                // A PC allowed one way gets an access code of its own, which is not the fleet code.
                : ownCode != null ? $"{request.Name} left before it got its access code. It was not added."
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
        CodeText.Text = request.Code;
        // Nothing is chosen for the user, for any PC: with a choice already made, a stray key or click on Allow would
        // hand over the fleet's code.
        FullAccessOption.IsChecked = ManagedAccessOption.IsChecked = false;
        ShowCheck(request);
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

    /// <summary>The check of the request on screen, as it is shown.</summary>
    private RequestCheck? _shownCheck;

    /// <summary>The request checked against this PC, the PCs saved here and the PCs allowed but not added yet.</summary>
    private RequestCheck Check(Pairing.JoinRequest request)
    {
        var allowed = _sending.Keys.Where(r => r != request).Select(r => (r.Name, r.MachineId)).Concat(_notSaved).ToList();
        return CheckRequest(FleetConfig.Current, request.Name, request.MachineId, allowed, Environment.MachineName, _thisPcId);
    }

    /// <summary>
    /// Shows what the window knows against the request, and turns Allow off for a PC that cannot be added. Shown again
    /// when the PCs allowed but not added yet change, as they are part of it, and when Allow finds that it changed.
    /// </summary>
    /// <param name="check">The check of <paramref name="request"/>; made here when null.</param>
    private void ShowCheck(Pairing.JoinRequest request, RequestCheck? check = null)
    {
        check ??= Check(request);
        _shownCheck = check;
        SameNameText.Text = check.Warning ?? string.Empty;
        SameNamePanel.Visibility = check.Warning != null ? Visibility.Visible : Visibility.Collapsed;
        _canAllow = check.CanAllow;
        if (!_canAllow) FullAccessOption.IsChecked = ManagedAccessOption.IsChecked = false;
        FullAccessOption.IsEnabled = _canAllow;
        ManagedAccessOption.IsEnabled = _canAllow && request.CanBeManaged;
        ManagedUnavailableText.Visibility = _canAllow && !request.CanBeManaged ? Visibility.Visible : Visibility.Collapsed;
        AccessOption_Checked(this, new RoutedEventArgs());
    }

    /// <summary>What the window says about a request before Allow, and whether Allow can be clicked at all.</summary>
    internal sealed record RequestCheck(string? Warning, bool CanAllow);

    /// <summary>
    /// Checks a request against this PC and the PCs saved here. A PC with this PC's name or machine ID, or with characters
    /// in its name that cannot be seen, cannot be added. One whose name or machine ID belongs to a saved PC, or to a PC
    /// allowed a moment ago that is not added yet, takes that PC's place once it is added, so the warning names that PC.
    /// </summary>
    /// <param name="allowed">The PCs allowed in this window that are not added yet, with their machine IDs.</param>
    /// <param name="thisPcId">This PC's machine ID; empty when it is not known.</param>
    internal static RequestCheck CheckRequest(FleetConfig config, string name, string? machineId,
        IEnumerable<(string Name, string? MachineId)> allowed, string thisPc, string thisPcId)
    {
        var pending = allowed.ToList();
        if (MachineRelay.Sanitize(name) == MachineRelay.Sanitize(thisPc))
            return new RequestCheck(SameNameWarning(config, name, machineId, thisPc), CanAllow: false);
        // Such a name looks like another one, of a saved PC or of this PC, and the fleet keeps them apart.
        if (name.EnumerateRunes().Any(IsInvisible))
        {
            var shown = VisiblePart(name).Trim();
            if (shown.Length == 0) shown = "the PC that asks";
            return new RequestCheck($"The name of {shown} contains characters that cannot be seen, so {shown} cannot be added. " +
                                    "Rename the PC in Windows first.", CanAllow: false);
        }
        // A copied disk keeps the machine ID, and the fleet cannot tell such a PC from this one.
        if (machineId is { Length: > 0 } && thisPcId.Length > 0 && machineId.Equals(thisPcId, StringComparison.OrdinalIgnoreCase))
            return new RequestCheck($"{name} sends the machine ID of this PC, so the fleet cannot tell the two PCs apart, and {name} cannot be added. " +
                                    "This happens when a PC is set up from a copy of the other PC's disk.", CanAllow: false);
        var warnings = new[]
            {
                SameNameWarning(config, name, machineId, thisPc, pending), LookAlikeWarning(config, name, thisPc, pending),
                SameIdWarning(config, name, machineId, pending)
            }
            .OfType<string>().ToList();
        return new RequestCheck(warnings.Count > 0 ? string.Join("\n\n", warnings) : null, CanAllow: true);
    }

    /// <summary>
    /// A character that cannot be seen in a name: a format or control character, a line or paragraph separator, or one
    /// that Unicode marks as not shown (<see cref="McpServer.IsNotShown"/>: the variation selectors, the combining
    /// grapheme joiner, the Hangul fillers). Accents and vowel signs can be seen, and names in many scripts need them
    /// (Thai, Devanagari, a decomposed Ā), so a combining mark as such is allowed; <see cref="SameNameForWarning"/>
    /// compares such a name in its composed form.
    /// </summary>
    internal static bool IsInvisible(Rune rune)
        => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.LineSeparator
               or UnicodeCategory.ParagraphSeparator
           || McpServer.IsNotShown(rune.Value);

    /// <summary>The name without the characters that cannot be seen, for messages.</summary>
    internal static string VisiblePart(string name)
        => string.Concat(name.EnumerateRunes().Where(r => !IsInvisible(r)).Select(r => r.ToString()));

    /// <summary>
    /// Whether two names count as one for the warnings: as Runner Manager keys them (<see cref="MachineRelay.Sanitize"/>),
    /// or in Unicode normalization form KC, so that a decomposed and a composed form of one name (A with a combining
    /// macron, and Ā), or full-width letters, count as one name. Saving and the block on this PC's name use the key only.
    /// Letters of other alphabets that look alike (a Cyrillic О for a Latin O) still count as other names; such a name
    /// gets its own warning (<see cref="LookAlikeWarning"/>).
    /// </summary>
    internal static bool SameNameForWarning(string a, string b)
        => MachineRelay.Sanitize(a) == MachineRelay.Sanitize(b) || MachineRelay.Sanitize(FormKC(a)) == MachineRelay.Sanitize(FormKC(b));

    private static string FormKC(string name)
    {
        try { return name.Normalize(NormalizationForm.FormKC); }
        // Not valid Unicode (a lone surrogate, for example): compared as it is.
        catch (ArgumentException) { return name; }
    }

    /// <summary>
    /// What to tell the user when the name of a joining PC only looks like another name: like the name of this PC (in
    /// full-width letters, for example), or of a PC saved here or allowed a moment ago, once look-alike letters of the
    /// Latin, Greek and Cyrillic alphabets are read as one (<see cref="LookAlikes.LookAlike"/>). A name that is the same
    /// for <see cref="SameNameForWarning"/> is left to <see cref="SameNameWarning"/>. Otherwise, a name that mixes
    /// alphabets with letters like Latin ones (Latin, Greek, Cyrillic, and as one more the other scripts that have such
    /// letters, such as Armenian or Cherokee; see <see cref="LookAlikes.AlphabetOf"/>) is warned about, as it can look like
    /// a name that is not saved here. A name written in one alphabet alone is not, nor are the letters of a script without
    /// such letters (Chinese or Thai) next to Latin ones. Null when there is nothing to say.
    /// </summary>
    internal static string? LookAlikeWarning(FleetConfig config, string name, string thisPc, IEnumerable<(string Name, string? MachineId)>? allowed = null)
    {
        const string allowIf = "Allow only if you expect a PC with this name.";
        bool LooksLike(string other) => LookAlikes.LookAlike(other, name) && !SameNameForWarning(other, name);
        if (MachineRelay.Sanitize(name) != MachineRelay.Sanitize(thisPc) && LookAlikes.LookAlike(thisPc, name))
            return $"{name} looks like {thisPc}, the name of this PC, but some of its letters only look alike, so it is another PC. {allowIf}";
        var like = config.KnownPeers.Where(p => LooksLike(p.Name)).Select(p => (p.Name, What: "a PC in this fleet", Short: "in this fleet"))
            .Concat(config.ManagedPcs.Where(p => LooksLike(p.Name))
                .Select(p => (p.Name, What: "a PC that this fleet manages one way", Short: "managed one way")))
            .Concat((allowed ?? []).Where(p => LooksLike(p.Name))
                .Select(p => (p.Name, What: "which was allowed a moment ago", Short: "allowed a moment ago")))
            .DistinctBy(p => (p.Name, p.Short))
            .ToList();
        if (like.Count == 1)
            return $"{name} looks like {like[0].Name}, {like[0].What}, but some of its letters only look alike, so it is not the same name. {allowIf}";
        if (like.Count > 1)
        {
            var list = like.Select(p => $"{p.Name} ({p.Short})").ToArray();
            return $"{name} looks like the names of these PCs, but some of its letters only look alike: {string.Join(", ", list[..^1])} and {list[^1]}. {allowIf}";
        }
        var alphabets = LookAlikes.AlphabetsIn(name);
        if (alphabets.Count < 2) return null;
        var names = alphabets.Where(a => a != LookAlikes.Alphabet.Other).Select(a => a.ToString()).ToArray();
        var named = names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
        var mix = alphabets.Contains(LookAlikes.Alphabet.Other) ? $"{named} letters with letters of another alphabet" : $"{named} letters";
        return $"The name {name} mixes {mix}, so it can look like the name of another PC. {allowIf}";
    }

    /// <summary>
    /// What to tell the user when a joining PC sends the machine ID of PCs saved here under another name. Once added,
    /// it takes the place of each of them, as a PC that was renamed. The ID is what the PC says about itself, and other
    /// devices on the network can see it, so the warning names every PC that would be replaced. Null otherwise.
    /// </summary>
    internal static string? SameIdWarning(FleetConfig config, string name, string? machineId, IEnumerable<(string Name, string? MachineId)>? allowed = null)
    {
        if (machineId is not { Length: > 0 }) return null;
        bool UnderAnotherName(string otherName, string? otherId)
            => otherId is { Length: > 0 } && otherId.Equals(machineId, StringComparison.OrdinalIgnoreCase) && !SameNameForWarning(otherName, name);
        var replaced = config.KnownPeers.Where(p => UnderAnotherName(p.Name, p.MachineId)).Select(p => (p.Name, What: "a PC in this fleet", Short: "in this fleet"))
            .Concat(config.ManagedPcs.Where(p => UnderAnotherName(p.Name, p.MachineId))
                .Select(p => (p.Name, What: "a PC that this fleet manages one way", Short: "managed one way")))
            .Concat((allowed ?? []).Where(p => UnderAnotherName(p.Name, p.MachineId))
                .Select(p => (p.Name, What: "which was allowed a moment ago", Short: "allowed a moment ago")))
            .ToList();
        if (replaced.Count == 0) return null;
        if (replaced.Count == 1)
        {
            var other = replaced[0].Name;
            return $"{name} says it is {other}, {replaced[0].What}, with a new name. If you allow it, it takes the place of {other}. " +
                   $"Allow only if {other} was renamed to {name}.";
        }
        var list = replaced.Select(p => $"{p.Name} ({p.Short})").ToArray();
        return $"{name} sends the machine ID that these PCs have: {string.Join(", ", list[..^1])} and {list[^1]}. " +
               $"If you allow it, it takes the place of each of them. Allow only if one of them was renamed to {name}.";
    }

    /// <summary>
    /// What to tell the user when the name of a joining PC is taken here already. The joining PC chooses its name, and
    /// a PC that is saved takes the place of the one saved under that name. A machine ID proves nothing: it travels in
    /// clear, so any device on the network can send the ID of a saved PC. So a taken name is always warned about. When
    /// the PC sends the ID of the PC with the name, the warning says that it says it is that PC, and what allowing it
    /// replaces. Null when the name is free.
    /// </summary>
    /// <param name="machineId">The joining PC's machine ID, or null from a PC that sends none.</param>
    /// <param name="thisPc">The name of this PC; tests give one.</param>
    /// <param name="allowed">
    /// The PCs allowed in this window that are not added yet: of two PCs with one name, the one added last replaces the other.
    /// </param>
    internal static string? SameNameWarning(FleetConfig config, string name, string? machineId, string? thisPc = null,
        IEnumerable<(string Name, string? MachineId)>? allowed = null)
    {
        // Never the same PC, whatever ID it sends.
        if (MachineRelay.Sanitize(name) == MachineRelay.Sanitize(thisPc ?? Environment.MachineName))
            return $"This PC is also named {name}. Two PCs in one fleet cannot have the same name. Rename one of them in Windows first.";
        var id = machineId is { Length: > 0 } ? machineId : null;
        // The PCs that have the name, and the one of them that has the ID the PC sends. A saved PC without an ID (it
        // has an older agent, or was not reached yet) never has it, and neither does an older PC that sends none.
        (string? Named, string? WithId) Taken(IEnumerable<(string Name, string? MachineId)> pcs)
        {
            var named = pcs.Where(p => SameNameForWarning(p.Name, name)).ToList();
            var withId = id == null ? null : named.FirstOrDefault(p => id.Equals(p.MachineId, StringComparison.OrdinalIgnoreCase)).Name;
            return (named.Count > 0 ? named[0].Name : null, withId);
        }
        const string saysItIs = "and the PC that asks says it is that PC";
        var warnings = new List<string>();
        var member = Taken(config.KnownPeers.Select(p => (p.Name, (string?)p.MachineId)));
        if (member.WithId is { } samePeer)
            // "Manage it one way" forgets a fleet member with the ID (FleetConfig.AddManagedPc).
            warnings.Add($"A PC named {name} is already in this fleet, {saysItIs}. If you allow it, it replaces the saved {samePeer}. " +
                         "Allowed one way, it stops being a fleet member here. Allow only if this is the same PC.");
        else if (member.Named != null)
            warnings.Add($"A PC named {name} is already in this fleet. Allow only if this is the same PC.");
        var managed = Taken(config.ManagedPcs.Select(p => (p.Name, (string?)p.MachineId)));
        if (managed.WithId is { } sameManaged)
            warnings.Add($"This fleet already manages a PC named {name}, {saysItIs}. If you allow it, it replaces the saved {sameManaged}. " +
                         "Allow only if this is the same PC.");
        else if (managed.Named != null)
            warnings.Add($"This fleet already manages a PC named {name}. Allow only if this is the same PC.");
        var pending = Taken(allowed ?? []);
        if (pending.WithId != null)
            warnings.Add($"A PC named {name} was allowed a moment ago and is not added yet, {saysItIs}. " +
                         "If you allow it too, the one that is added last replaces the other. Allow only if this is the same PC.");
        else if (pending.Named != null)
            warnings.Add($"A PC named {name} was allowed a moment ago and is not added yet. Allow only if this is the same PC.");
        return warnings.Count > 0 ? string.Join("\n\n", warnings) : null;
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
        AllowButton.IsEnabled = chosen && _canAllow;
        AllowNoteText.Text = !_canAllow ? "This PC cannot be added. Click Deny."
            : chosen ? "Allow only if the codes match." : "First choose what it can do. Allow only if the codes match.";
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } request || AllowButton.IsEnabled != true || !_canAllow) return;
        // The saved PCs can change while the request is on screen (the fleet agent syncs them, or another window saves
        // one), and the user allows what the window shows. When the check differs now, the new one is shown first.
        var check = Check(request);
        if (check != _shownCheck)
        {
            ShowCheck(request, check);
            if (check.CanAllow)
                AllowNoteText.Text = check.Warning != null
                    ? "The warning above changed. Read it, then click Allow again."
                    : "The warning no longer applies. Click Allow again to allow.";
            return;
        }
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
            // A fleet member with its machine ID is forgotten, as the warning before Allow said, so the PC is not known both ways.
            try { FleetConfig.AddManagedPc(request.Name, FleetNetwork.WithPort(request.Address), ownCode, request.MachineId); }
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
                // An older PC has kept its access code already.
                DialogWindow.Error(this, "Could not save the PC",
                    $"{request.Name} has its access code, but this PC could not save it, so the fleet cannot manage {request.Name}.\n\n{ex.Message}\n\n" +
                    $"On {request.Name}, open Settings → Fleet and click Disconnect. Then add it again.");
                ShowResults();
                return;
            }
            _managed.Add(request.Name);
            _added.Add($"✓  {request.Name} is managed one way.");
        }
        else
        {
            // Discovery finds it only on the local network, so remember where it joined from (over Tailscale, for example).
            // With its machine ID, a one-way pairing of the same PC ends here.
            try { MachineRelay.AddKnownPeer(request.Name, request.Address, request.MachineId); }
            catch (Exception ex)
            {
                var failure = JoinNotSaved(request.Name, request.Confirms, ex.Message);
                if (failure.TellThePc)
                {
                    // The PC is told, and drops the fleet code: it is neither in the fleet nor saved here. The dialog comes
                    // after that, so that the PC does not wait for it.
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!_closed) DialogWindow.Error(this, "Could not save the PC", failure.Message);
                    });
                    _notAdded.Add(failure.Result);
                    ShowResults();
                    throw;
                }
                // An older PC has kept the fleet code already, so it is in the fleet.
                _joined.Add(request.Name);
                _notSaved.Add((request.Name, request.MachineId));
                _added.Add(failure.Result);
                ShowResults();
                ShowError("Could not save the PC", failure.Message);
                return;
            }
            _joined.Add(request.Name);
            _added.Add($"✓  {request.Name} joined the fleet.");
        }
        ShowResults();
    }

    /// <summary>What the window does when a PC joined with the fleet code and saving it here failed.</summary>
    /// <param name="TellThePc">Throw, so that the PC (one that confirms) is told that it was not added and drops the code.</param>
    /// <param name="Result">The line in the window's results.</param>
    /// <param name="Message">The error shown.</param>
    internal sealed record JoinFailure(bool TellThePc, string Result, string Message);

    /// <param name="confirms">The PC waits for this PC's answer (<see cref="Pairing.JoinRequest.Confirms"/>); an older one has kept the code.</param>
    internal static JoinFailure JoinNotSaved(string name, bool confirms, string error) => confirms
        ? new JoinFailure(true, $"{name} was not added. This PC could not save it.",
            $"This PC could not save {name}, so it was not added.\n\n{error}\n\nTry again.")
        : new JoinFailure(false, $"✓  {name} joined the fleet, but this PC could not save it.",
            $"{name} has the fleet code and is in the fleet, but this PC could not save it.\n\n{error}\n\n" +
            $"On the local network, the fleet finds {name} by itself. On another network, for example over Tailscale, add its address " +
            "in Settings → Fleet → “Look for a paired PC at an address”.");

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
        // A PC allowed a moment ago was added or left: what the request on screen is checked against changed.
        if (_current != null) ShowCheck(_current);
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
