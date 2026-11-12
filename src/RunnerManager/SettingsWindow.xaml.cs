using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace RunnerManager;

public partial class SettingsWindow : Window
{
    /// <summary>The page shown last, so Settings reopens where it was left during this session.</summary>
    private static string _lastPage = "General";

    private readonly AppSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Func<Task> _fleetChanged;
    /// <summary>The dashboard's RunOperationAsync: false when the operation failed (it shows the error) or was skipped.</summary>
    private readonly Func<string, Func<Action<string>, Task>, Task<bool>> _runOperation;
    /// <summary>Set while the constructor sets the controls, so their events do not save what was just read.</summary>
    private bool _loading = true;
    private ReleaseInfo? _release;
    /// <summary>
    /// The dashboard's join flow, which installs and starts this PC's fleet agent once a code is set. It is the only
    /// way Settings joins: without it, a code entered here is only stored, and the dashboard joins.
    /// </summary>
    private readonly Func<Task>? _finishJoining;
    /// <summary>Whether this PC's fleet agent task exists; null until schtasks has answered.</summary>
    private bool? _agentInstalled;

    public SettingsWindow(AppSettings settings, Action settingsChanged, Func<Task> fleetChanged,
        Func<string, Func<Action<string>, Task>, Task<bool>> runOperation, Func<Task>? finishJoining = null)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _settings = settings;
        _settingsChanged = settingsChanged;
        _fleetChanged = fleetChanged;
        _runOperation = runOperation;
        _finishJoining = finishJoining;

        (settings.Theme switch { AppTheme.System => ThemeSystem, AppTheme.Light => ThemeLight, _ => ThemeDark }).IsChecked = true;
        (settings.RefreshSeconds switch { >= 30 => Refresh30, >= 10 => Refresh10, _ => Refresh5 }).IsChecked = true;
        NotificationsToggle.IsChecked = settings.Notifications;
        FailedRunsToggle.IsChecked = settings.NotifyOnFailedRuns;
        JobProblemsToggle.IsChecked = settings.NotifyOnJobProblems;
        (settings.QuietMinutes switch { >= 60 => Quiet60, >= 30 => Quiet30, >= 15 => Quiet15, _ => Quiet10 }).IsChecked = true;
        CloseToTrayToggle.IsChecked = settings.CloseToTray;
        WatchdogToggle.IsChecked = FleetConfig.Current.Watchdog;
        NavVersionText.Text = $"Runner Manager {AppUpdater.CurrentVersion}";
        VersionRow.Title = $"Runner Manager {AppUpdater.CurrentVersion}";
        DataRow.Description = FleetConfig.StateDir;
        ProgramRow.Description = $"{AppUpdater.InstallDir}. To move it, run the setup again and choose another folder.";
        UpdateRunnerFolder();
        UpdateKeyStatus();
        RefreshPeers();
        BuildAssistantClients();
        UpdateAssistants();
        ShowPage(_lastPage);
        // Only the controls set above are read; Start with Windows waits for schtasks below, so the others work meanwhile.
        _loading = false;
        StartupToggle.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(ReadStartupTaskAsync(), ReadAgentStateAsync(), CheckAddressesAsync(again: false));
            await CheckForUpdatesAsync(force: false);
        };
    }

    /// <summary>A fleet code alone does not join a fleet: the fleet agent is what other PCs reach, and it runs auto-recovery.</summary>
    private async Task ReadAgentStateAsync()
    {
        try { _agentInstalled = await MachineRelay.IsAgentInstalledAsync(); }
        catch { _agentInstalled = null; }
        UpdateKeyStatus();
        UpdateWatchdog();
    }

    private void UpdateWatchdog()
    {
        var noAgent = _agentInstalled == false;
        WatchdogToggle.IsEnabled = !noAgent;
        WatchdogRow.Description = noAgent
            ? "Needs the fleet agent, which is not installed on this PC. Start a fleet or join one first."
            : WatchdogDescription;
    }

    private const string WatchdogDescription = "Restarts runners that stop unexpectedly, up to 3 times an hour.";

    private async Task ReadStartupTaskAsync()
    {
        try
        {
            StartupToggle.IsChecked = await ScheduledTasks.ExistsAsync(ScheduledTasks.DashboardTaskName);
            StartupRow.Description = StartupDescription;
        }
        catch (Exception ex)
        {
            StartupRow.Description = $"Could not read the startup task: {ex.Message}";
        }
        finally { StartupToggle.IsEnabled = true; }
    }

    private const string StartupDescription = "Opens in the notification area when you sign in.";

    /// <summary>
    /// Runs an operation through the dashboard, one at a time on this PC. True only when it ran without an error. The
    /// dashboard shows both other cases itself: the error of a failed operation, and that another operation is running.
    /// </summary>
    private Task<bool> RunOperationAsync(string title, Func<Action<string>, Task> operation) => _runOperation(title, operation);

    // ----- Navigation -----

    private void ShowPage(string page)
    {
        var item = Nav.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == page) ?? (ListBoxItem)Nav.Items[0];
        Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        _lastPage = page;
        GeneralPage.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        FleetPage.Visibility = page == "Fleet" ? Visibility.Visible : Visibility.Collapsed;
        AssistantsPage.Visibility = page == "Assistants" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Assistants") UpdateAssistants();
        UpdatesPage.Visibility = page == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc in a box with typed text is left to the box; a read-only box (the assistant setup) does not hold it.
        if (e.Key != Key.Escape || Keyboard.FocusedElement is TextBox { IsReadOnly: false, Text.Length: > 0 }) return;
        e.Handled = true;
        Close();
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) Menus.OpenBelow(button, alignRight: false);
    }

    // ----- General -----

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Theme = sender == ThemeSystem ? AppTheme.System : sender == ThemeLight ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.Apply(_settings.Theme);
        Save();
    }

    private void Refresh_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var seconds))
        {
            _settings.RefreshSeconds = seconds;
            Save();
        }
    }

    private void Quiet_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var minutes))
        {
            _settings.QuietMinutes = minutes;
            Save();
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Notifications = NotificationsToggle.IsChecked == true;
        _settings.NotifyOnFailedRuns = FailedRunsToggle.IsChecked == true;
        _settings.NotifyOnJobProblems = JobProblemsToggle.IsChecked == true;
        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        Save();
    }

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StartupToggle.IsChecked == true)
                await Installer.RegisterStartupTaskAsync(AppUpdater.PreferredExe);
            else
                await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);
        }
        catch (Exception ex)
        {
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
            DialogWindow.Error(this, "Could not change the Start with Windows setting", ex.Message);
        }
    }

    private void UpdateRunnerFolder()
    {
        var custom = FleetConfig.Current.RunnerFolder.Length > 0;
        RunnerFolderRow.Description = custom
            ? $"{RunnerService.RunnersRoot}. Runners moved to this PC are installed here too."
            : $"{RunnerService.RunnersRoot} (default). Runners moved to this PC are installed here too.";
        RunnerFolderResetButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RunnerFolderChange_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder for new runners",
            InitialDirectory = Directory.Exists(RunnerService.RunnersRoot) ? RunnerService.RunnersRoot : null
        };
        if (dialog.ShowDialog(this) != true) return;
        if (RunnerService.RunnerFolderProblem(dialog.FolderName) is { } problem)
        {
            DialogWindow.Info(this, "Choose another folder", problem);
            return;
        }
        FleetConfig.Update(c => c.RunnerFolder = dialog.FolderName);
        UpdateRunnerFolder();
    }

    private void RunnerFolderReset_Click(object sender, RoutedEventArgs e)
    {
        FleetConfig.Update(c => c.RunnerFolder = string.Empty);
        UpdateRunnerFolder();
    }

    private void Save()
    {
        _settings.Save();
        _settingsChanged();
    }

    // ----- Fleet -----

    private void UpdateKeyStatus()
    {
        var config = FleetConfig.Current;
        var key = config.FleetKey;
        var managedBy = key == null ? string.Empty : config.ManagedBy;
        ManagedGroup.Visibility = managedBy.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ManagedRow.Description = $"By {managedBy}'s fleet. Its PCs and AI assistants can view and control this PC. This PC cannot see or control theirs.";
        // Set, but the fleet agent was never installed (the code was entered here, or installing it failed).
        var notJoined = key != null && _agentInstalled == false;
        // A PC managed one way has its access code here, which is not the fleet code.
        KeyRow.Title = CodeRowText.Title(managedBy);
        AutomationProperties.SetName(CopyKeyButton, CodeRowText.CopyName(managedBy));
        RemoveKeyItem.Header = CodeRowText.RemoveItem(managedBy);
        KeyRow.Description = key == null
            ? "Not set. Start a new fleet here, or enter the code from a PC that is already in one."
            : notJoined
                ? $"Set (fingerprint {CommandSigner.KeyId(key)}), but this PC has not joined yet: its fleet agent is not installed, so other PCs cannot reach it."
                  + (_finishJoining == null ? " To join, click “Connect another PC” in the sidebar of the dashboard." : string.Empty)
            : managedBy.Length > 0
                ? CodeRowText.ManagedStatus(managedBy, CommandSigner.KeyId(key))
                : $"Set. Fingerprint {CommandSigner.KeyId(key)}, the same on every PC in the fleet.";
        GenerateKeyButton.Visibility = key == null ? Visibility.Visible : Visibility.Collapsed;
        FinishJoiningButton.Visibility = notJoined && _finishJoining != null ? Visibility.Visible : Visibility.Collapsed;
        CopyKeyButton.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        ReplaceKeyItem.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        RemoveKeyItem.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        // Leaving removes the agent and tells the other PCs; without an agent there is nothing to leave.
        LeaveFleetButton.IsEnabled = key != null && !notJoined;
    }

    private async void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        var config = FleetConfig.Current;
        if (config.FleetKey != null && !DialogWindow.Confirm(this, "Give only this PC a new fleet code?",
                CodeRowText.NewCodeMessage(config.ManagedBy), "New code", danger: true))
            return;
        FleetConfig.SetFleetKey(CommandSigner.NewKey());
        await JoinWithCodeAsync();
    }

    /// <summary>After a code was set here: joins through the dashboard if the fleet agent is missing, so this PC is in the fleet.</summary>
    private async Task JoinWithCodeAsync()
    {
        UpdateKeyStatus();
        await ReadAgentStateAsync();
        if (_agentInstalled == false && _finishJoining != null) await FinishJoiningAsync();
        else await _fleetChanged();
        RefreshPeers();
    }

    private async void FinishJoining_Click(object sender, RoutedEventArgs e)
    {
        await FinishJoiningAsync();
        RefreshPeers();
    }

    private async Task FinishJoiningAsync()
    {
        if (FleetConfig.Current.FleetKey == null || _finishJoining == null) return;
        FinishJoiningButton.IsEnabled = false;
        try
        {
            await _finishJoining();
        }
        finally
        {
            FinishJoiningButton.IsEnabled = true;
            await ReadAgentStateAsync();
        }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var config = FleetConfig.Current;
        if (config.FleetKey is not { } key) return;
        var managedBy = config.ManagedBy;
        try
        {
            Clipboard.SetText(CommandSigner.FormatKey(key));
            DialogWindow.Info(this, CodeRowText.CopiedHeading(managedBy), CodeRowText.CopiedMessage(managedBy));
        }
        catch (Exception ex) { DialogWindow.Error(this, CodeRowText.CopyFailed(managedBy), ex.Message); }
    }

    private void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        PasteKeyPanel.Visibility = Visibility.Visible;
        PasteKeyBox.Focus();
    }

    private void PasteKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveKey_Click(sender, e);
    }

    private void CancelPasteKey_Click(object sender, RoutedEventArgs e)
    {
        PasteKeyBox.Clear();
        PasteKeyPanel.Visibility = Visibility.Collapsed;
    }

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = CommandSigner.ParseKey(PasteKeyBox.Text);
        if (key == null)
        {
            DialogWindow.Info(this, "Invalid fleet code", "Paste the full code copied from another PC. It starts with rwk_.");
            return;
        }
        FleetConfig.SetFleetKey(key);
        CancelPasteKey_Click(sender, e);
        await JoinWithCodeAsync();
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        var managedBy = FleetConfig.Current.ManagedBy;
        if (!DialogWindow.Confirm(this, CodeRowText.RemoveHeading(managedBy), CodeRowText.RemoveMessage(managedBy), "Remove code", danger: true)) return;
        FleetConfig.SetFleetKey(null);
        UpdateKeyStatus();
        await _fleetChanged();
    }

    private async void StopBeingManaged_Click(object sender, RoutedEventArgs e)
    {
        var managedBy = FleetConfig.Current.ManagedBy;
        if (!DialogWindow.Confirm(this, "Disconnect from the fleet?",
                $"{managedBy}'s fleet can no longer view or control this PC. Its runners keep running. To connect it again, pair it again.",
                "Disconnect", danger: true)) return;
        FleetConfig.StopBeingManaged();
        UpdateKeyStatus();
        RefreshPeers();
        await _fleetChanged();
        await ReadAgentStateAsync();
    }

    private async void WatchdogToggle_Click(object sender, RoutedEventArgs e)
    {
        FleetConfig.Update(c => c.Watchdog = WatchdogToggle.IsChecked == true);
        await _fleetChanged();
    }

    private sealed record PeerRow(string Name, string Address, string Detail, bool Managed)
    {
        public string Action => Managed ? "Stop managing" : "Forget";
        public string ActionName => $"{Action} {Name}";
    }

    /// <summary>An address added in Settings, with what the last fleet refresh found there.</summary>
    private sealed record AddressRow(string Address, string Status)
    {
        public string RemoveName => $"Remove {Address}";
    }

    private static string LastSeen(DateTime utc) => utc == DateTime.MinValue ? "never" : utc.ToLocalTime().ToString("g");

    /// <summary>What each address answered when this window asked it (see <see cref="CheckAddressesAsync"/>).</summary>
    private readonly Dictionary<string, MachineInfo> _addressAnswers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _addressesChecking = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshPeers()
    {
        var config = FleetConfig.Current;
        var peers = config.KnownPeers.Select(p => new PeerRow(p.Name, p.Address, $"{p.Address}  ·  last seen {LastSeen(p.LastSeenUtc)}", false))
            .Concat(config.ManagedPcs.Select(p => new PeerRow(p.Name, p.Address, $"Managed one way  ·  {p.Address}  ·  last seen {LastSeen(p.LastSeenUtc)}", true)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        PeersList.ItemsSource = peers;
        PeersEmptyPanel.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The dashboard lists an address where no PC answered as a placeholder, with the reason.
        var machines = (Owner as MainWindow)?.Machines.ToList() ?? new List<MachineInfo>();
        var localAddresses = FleetNetwork.LocalAddresses().ToList();
        AddressList.ItemsSource = config.ManualAddresses
            .Select(a => new AddressRow(a, FleetAddressRules.Problem(a, Environment.MachineName, localAddresses)
                ?? (_addressesChecking.Contains(a) ? "Checking…"
                    : FleetAddressRules.Status(a, config.KnownPeers, config.ManagedPcs, machines, _addressAnswers.GetValueOrDefault(a), LastSeen))))
            .ToList();
        ThisPcAddressText.Text = $"This PC: {Environment.MachineName}  ·  {string.Join(", ", FleetNetwork.LocalAddresses())}  ·  port {FleetNetwork.AgentPort}";
    }

    /// <summary>
    /// Asks the addresses that no known PC has, with the status request the fleet refresh sends there. The refresh
    /// lists a PC that answers at such an address under the address it already had, so this is what names it.
    /// </summary>
    private async Task CheckAddressesAsync(bool again)
    {
        var config = FleetConfig.Current;
        if (config.FleetKey == null) return;
        var localAddresses = FleetNetwork.LocalAddresses().ToList();
        var addresses = config.ManualAddresses
            .Where(a => FleetAddressRules.Problem(a, Environment.MachineName, localAddresses) == null
                        && !config.KnownPeers.Any(p => FleetAddressRules.SameHost(p.Address, a))
                        && !config.ManagedPcs.Any(p => FleetAddressRules.SameHost(p.Address, a))
                        && (again || !_addressAnswers.ContainsKey(a)))
            .Where(_addressesChecking.Add)
            .ToList();
        if (addresses.Count == 0) return;
        RefreshPeers();
        try
        {
            var answers = await Task.WhenAll(addresses.Select(a => MachineRelay.FetchPeerAsync(null, null, a, null)));
            for (var i = 0; i < addresses.Count; i++)
                if (answers[i] is { } answer) _addressAnswers[addresses[i]] = answer;
        }
        catch (Exception)
        {
            // The status stays as the last fleet refresh found it.
        }
        finally
        {
            foreach (var address in addresses) _addressesChecking.Remove(address);
        }
        RefreshPeers();
    }

    private async void FindPcs_Click(object sender, RoutedEventArgs e)
    {
        FindPcsButton.IsEnabled = false;
        FindPcsButton.Content = "Searching…";
        try
        {
            await Task.WhenAll(_fleetChanged(), CheckAddressesAsync(again: true));
            RefreshPeers();
        }
        finally
        {
            FindPcsButton.Content = "Find PCs";
            FindPcsButton.IsEnabled = true;
        }
    }

    private async void AddAddress_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (FleetAddressRules.Problem(address, Environment.MachineName, FleetNetwork.LocalAddresses()) is { } problem)
        {
            DialogWindow.Info(this, "Check the address", problem);
            AddressBox.Focus();
            return;
        }
        if (FleetConfig.Current.ManualAddresses.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            DialogWindow.Info(this, "Address already added", $"{address} is already in the list below.");
            return;
        }
        FleetConfig.Update(c => c.ManualAddresses.Add(address));
        AddressBox.Clear();
        RefreshPeers();
        await Task.WhenAll(_fleetChanged(), CheckAddressesAsync(again: false));
        RefreshPeers();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddAddress_Click(sender, e);
    }

    private async void RemoveAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AddressRow { Address: var address } }) return;
        FleetConfig.Update(c => c.ManualAddresses.RemoveAll(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)));
        RefreshPeers();
        await _fleetChanged();
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PeerRow peer }) return;
        if (peer.Managed)
        {
            if (Owner is MainWindow main) await main.StopManagingAsync(peer.Name);
        }
        else
        {
            // As on the dashboard: ask first, and drop the address the PC was added by, or the next refresh finds it again.
            var machine = (Owner as MainWindow)?.Machines.FirstOrDefault(m => !m.IsLocal && m.MachineKey == MachineRelay.Sanitize(peer.Name));
            if (!DialogWindow.Confirm(this, ForgetPc.Heading(peer.Name), ForgetPc.Message(peer.Name, machine?.IsOnline == true), "Forget")) return;
            ForgetPc.Forget(peer.Name, peer.Address, machine?.Address ?? string.Empty);
            RefreshPeers();
            await _fleetChanged();
        }
        RefreshPeers();
    }

    private async void LeaveFleet_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not MainWindow main) return;
        await main.LeaveFleetAsync();
        await ReadAgentStateAsync();
        RefreshPeers();
    }

    // ----- Change fleet code -----

    private async void ChangeFleetCode_Click(object sender, RoutedEventArgs e)
    {
        if (FleetConfig.Current.FleetKey == null)
        {
            DialogWindow.Info(this, "No fleet yet", "Start a fleet or join one first.");
            return;
        }
        if (FleetConfig.Current.ManagedBy.Length > 0)
        {
            DialogWindow.Info(this, "This PC is managed one way", CodeRowText.ManagedCannotChange(FleetConfig.Current.ManagedBy));
            return;
        }
        List<MachineInfo> pcs;
        ChangeFleetCodeButton.IsEnabled = false;
        ChangeFleetCodeButton.Content = "Checking PCs…";
        try
        {
            pcs = (await MachineRelay.ReadFleetAsync(forceDiscovery: true))
                .Where(m => !m.IsLocal && !m.IsManaged && !m.MachineKey.StartsWith("ADDR_") && !m.MachineKey.StartsWith("DUPLICATE_")).ToList();
        }
        catch (Exception ex)
        {
            DialogWindow.Error(this, "Could not check the other PCs", ex.Message);
            return;
        }
        finally
        {
            ChangeFleetCodeButton.Content = "Change code…";
            ChangeFleetCodeButton.IsEnabled = true;
        }
        if (ChooseFleetCodePcs(pcs) is not { } choice) return;

        // A failure is shown by the dashboard, with the operation's title.
        MachineRelay.FleetCodeChange? result = null;
        await RunOperationAsync("Change the fleet code on every PC", async log => result = await MachineRelay.ChangeFleetCodeAsync(choice.Keep, choice.LeaveOut, log));
        UpdateKeyStatus();
        RefreshPeers();
        await _fleetChanged();
        if (result == null) return;

        var lines = new List<string>();
        if (result.Switched.Count > 0) lines.Add($"Use the new code: {string.Join(", ", result.Switched)}.");
        if (result.NotConfirmed.Count > 0) lines.Add($"Got the new code and switch when contacted next: {string.Join(", ", result.NotConfirmed)}.");
        if (result.Failed.Count > 0) lines.Add($"Did not get it, and must be paired again:\n{string.Join("\n", result.Failed)}");
        if (result.LeftOut.Count > 0) lines.Add($"Left out and forgotten: {string.Join(", ", result.LeftOut)}. Pair them again to bring them back.");
        if (result.ManagedSwitched.Count > 0) lines.Add($"Managed PCs with a new access code: {string.Join(", ", result.ManagedSwitched)}.");
        if (result.ManagedFailed.Count > 0) lines.Add($"Managed PCs that kept their old access code:\n{string.Join("\n", result.ManagedFailed)}\nA PC that was left out may still reach them. Stop managing them and pair them again.");
        if (lines.Count == 0) lines.Add("No other PCs are in the fleet.");
        DialogWindow.Show(this, result.Failed.Count + result.ManagedFailed.Count > 0 ? DialogTone.Warning : DialogTone.Info, "The fleet code was changed",
            string.Join("\n\n", lines), "OK", cancel: null);
    }

    /// <summary>Asks which PCs get the new code. Only online PCs with a current agent can; the others are left out.</summary>
    private (List<MachineInfo> Keep, List<MachineInfo> LeaveOut)? ChooseFleetCodePcs(IReadOnlyList<MachineInfo> pcs)
    {
        var window = new Window
        {
            Title = "Change the fleet code on every PC", Owner = this, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontSize = 13,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont")
        };
        window.SetResourceReference(BackgroundProperty, "Bg1Brush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        WindowTheme.Apply(window);

        var body = new StackPanel { Margin = new Thickness(28, 26, 28, 20) };
        body.Children.Add(new TextBlock { Text = "Change the fleet code on every PC?", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
            Text = "The PCs you keep get a new fleet code, and the old one stops working. PCs you leave out, and PCs that are " +
                   "offline or too old, keep the old code: they can no longer reach the fleet and must be paired again."
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        body.Children.Add(intro);

        var boxes = new List<(CheckBox Box, MachineInfo Pc)>();
        foreach (var pc in pcs.OrderBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase))
        {
            var ready = pc.IsOnline && pc.AgentProtocolVersion >= MachineRelay.RekeyProtocolVersion;
            var note = ready ? string.Empty : pc.IsOnline ? "  ·  update Runner Manager on it first" : "  ·  offline";
            var box = new CheckBox { Content = pc.MachineName + note, IsChecked = ready, IsEnabled = ready, Margin = new Thickness(0, 4, 0, 4) };
            boxes.Add((box, pc));
            body.Children.Add(box);
        }
        if (boxes.Count == 0) body.Children.Add(new TextBlock { Text = "No other PCs are in the fleet. Only this PC gets the new code.", TextWrapping = TextWrapping.Wrap });

        var confirmed = false;
        var change = new Button { Content = "Change code", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("SolidDangerButton") };
        change.Click += (_, _) =>
        {
            confirmed = true;
            window.DialogResult = true;
        };
        // As in the other danger dialogs, Enter cancels: changing the code needs a click or a deliberate key press.
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, IsDefault = true, Style = (Style)FindResource("GhostButton") };
        window.Loaded += (_, _) => cancel.Focus();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, change } };
        var footer = new Border { Padding = new Thickness(28, 14, 28, 14), BorderThickness = new Thickness(0, 1, 0, 0), Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, "Bg0Brush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        window.Content = new StackPanel { Children = { body, footer } };
        window.ShowDialog();
        if (!confirmed) return null;
        return (boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Pc).ToList(),
                boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Pc).ToList());
    }

    // ----- AI assistants -----

    /// <summary>The AI tool chosen last, so the page reopens on it during this session.</summary>
    private static string _lastAssistantClient = AssistantSetup.Clients[0].Name;

    private void BuildAssistantClients()
    {
        foreach (var client in AssistantSetup.Clients)
        {
            var option = new RadioButton
            {
                Content = client.Name,
                GroupName = "AssistantClient",
                Style = (Style)FindResource("Segment"),
                Padding = new Thickness(10, 5, 10, 5),
                IsChecked = client.Name == _lastAssistantClient
            };
            option.Checked += (_, _) =>
            {
                _lastAssistantClient = client.Name;
                ShowAssistantSnippet();
            };
            AssistantClientPanel.Children.Add(option);
        }
    }

    private static AssistantClient SelectedAssistantClient
        => AssistantSetup.Clients.FirstOrDefault(c => c.Name == _lastAssistantClient) ?? AssistantSetup.Clients[0];

    private void UpdateAssistants()
    {
        var options = FleetConfig.Current.Assistant;
        AssistantToggle.IsChecked = options.Enabled;
        AssistantAskToggle.IsChecked = options.AskBeforeChanges;
        AssistantHttpToggle.IsChecked = options.HttpEnabled;
        AssistantAskToggle.IsEnabled = options.Enabled;
        AssistantHttpToggle.IsEnabled = options.Enabled;
        AssistantNewTokenButton.Visibility = options.HttpEnabled ? Visibility.Visible : Visibility.Collapsed;
        AssistantHttpRow.Description = options.HttpEnabled
            ? $"{AssistantHttpServer.Url}. Only programs on this PC can connect, and they need the token."
            : "For AI tools that connect to a URL instead of starting a program.";
        ShowAssistantSnippet();

        var log = AssistantLog.Recent(8);
        AssistantLogList.ItemsSource = log;
        AssistantLogEmpty.Visibility = log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAssistantSnippet()
    {
        var client = SelectedAssistantClient;
        var options = FleetConfig.Current.Assistant;
        var token = options.HttpToken;
        // The token stays hidden on screen; Copy puts the real one on the clipboard.
        var shown = token.Length > 0 ? token[..Math.Min(9, token.Length)] + "…" : "<turn on Connect by URL>";
        AssistantWhereText.Text = client.Where;
        AssistantSnippetBox.Text = client.Snippet(AppUpdater.InstalledExe, shown);
        AssistantHttpHint.Visibility = client.NeedsHttp && !options.HttpEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AssistantCopy_Click(object sender, RoutedEventArgs e)
    {
        var options = FleetConfig.Current.Assistant;
        var client = SelectedAssistantClient;
        if (client.NeedsHttp && (!options.HttpEnabled || options.HttpToken.Length == 0))
        {
            DialogWindow.Info(this, "Turn on Connect by URL first", "This setup uses the URL endpoint and its token.");
            return;
        }
        try { Clipboard.SetText(client.Snippet(AppUpdater.InstalledExe, options.HttpToken)); }
        catch (Exception ex)
        {
            DialogWindow.Error(this, "Could not copy", ex.Message);
            return;
        }
        ShowCopied();
    }

    private int _copiedShown;

    /// <summary>"Copied" on the button for two seconds, so the click is seen to have worked.</summary>
    private async void ShowCopied()
    {
        var shown = ++_copiedShown;
        AssistantCopyButton.Content = "Copied";
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (shown == _copiedShown) AssistantCopyButton.Content = "Copy";
    }

    private async void AssistantToggle_Click(object sender, RoutedEventArgs e)
    {
        var enable = AssistantToggle.IsChecked == true;
        // Before the question and before anything below changes this PC: a switch that cannot be saved changes nothing.
        if (AssistantSwitchForm.Problem() is { } problem)
        {
            DialogWindow.Error(this, AssistantSwitchForm.ErrorHeading, problem);
            UpdateAssistants();
            return;
        }
        var setUp = false;
        if (enable && (FleetConfig.Current.FleetKey == null || !await MachineRelay.IsAgentRunningAsync()))
        {
            // Assistants hand their changes to this PC's fleet agent, which needs a fleet code even with only this PC.
            // That changes this PC (a code, a scheduled task, firewall rules), so ask first.
            var installed = _agentInstalled ?? await MachineRelay.IsAgentInstalledAsync();
            var what = FleetConfig.Current.FleetKey == null
                ? "Runner Manager starts a fleet with only this PC: it creates a fleet code, installs the fleet agent and opens its ports in Windows Firewall for the local network and Tailscale. Other PCs can join later with the code."
                : installed
                    ? "The fleet agent on this PC is not running. Runner Manager installs it again and starts it."
                    : "Runner Manager installs the fleet agent and opens its ports in Windows Firewall for the local network and Tailscale.";
            if (!DialogWindow.Confirm(this, "Turn on AI assistants?",
                    $"Assistants work through this PC's fleet agent. {what}", "Turn on"))
            {
                AssistantToggle.IsChecked = false;
                return;
            }
            var ready = await RunOperationAsync("Turn on AI assistants", async log =>
            {
                if (FleetConfig.Current.FleetKey == null) FleetConfig.SetFleetKey(CommandSigner.NewKey());
                await AppUpdater.InstallCurrentBuildAsync(log);
                log("Starting the fleet agent…");
                await MachineRelay.InstallAndStartAgentTaskAsync();
            });
            await ReadAgentStateAsync();
            if (!ready)
            {
                AssistantToggle.IsChecked = false;
                return;
            }
            setUp = true;
        }
        if (!ChangeSwitches(enabled: enable))
        {
            // The fleet code or the fleet agent may be new all the same: the dashboard shows them.
            if (setUp) await _fleetChanged();
            return;
        }
        AssistantLog.Append(enable ? "Assistant access turned on." : "Assistant access turned off.");
        UpdateAssistants();
        if (enable) await _fleetChanged();
    }

    private void AssistantAskToggle_Click(object sender, RoutedEventArgs e)
    {
        var ask = AssistantAskToggle.IsChecked == true;
        if (ChangeSwitches(askBeforeChanges: ask)) UpdateAssistants();
    }

    /// <summary>
    /// Changes "Allow AI assistants" or "Ask before changes", which need administrator rights, so a click alone does
    /// not change them. When that fails, shows why, and sets the switches back to what is stored.
    /// </summary>
    private bool ChangeSwitches(bool? enabled = null, bool? askBeforeChanges = null)
    {
        if (AssistantSwitchForm.Set(enabled, askBeforeChanges) is not { } problem) return true;
        DialogWindow.Error(this, AssistantSwitchForm.ErrorHeading, problem);
        UpdateAssistants();
        return false;
    }

    /// <summary>
    /// Saves a change to the AI assistant settings in fleet.json (Connect by URL and its token). When that fails, shows
    /// why, and sets the controls back to what is stored.
    /// </summary>
    private bool ChangeAssistants(Action<FleetConfig> change)
    {
        try
        {
            FleetConfig.Update(change);
            return true;
        }
        catch (Exception ex)
        {
            DialogWindow.Error(this, AssistantSwitchForm.ErrorHeading, ex.Message);
            UpdateAssistants();
            return false;
        }
    }

    private void AssistantHttpToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = AssistantHttpToggle.IsChecked == true;
        if (ChangeAssistants(c =>
            {
                c.Assistant.HttpEnabled = on;
                if (on && c.Assistant.HttpToken.Length == 0) c.Assistant.ProtectedHttpToken = AssistantOptions.ProtectToken(AssistantHttpServer.NewToken());
            }))
            UpdateAssistants();
    }

    private void AssistantNewToken_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Confirm(this, "Create a new token?",
                "AI tools that use the current token cannot connect until you give them the new one.", "New token", danger: true)) return;
        if (ChangeAssistants(c => c.Assistant.ProtectedHttpToken = AssistantOptions.ProtectToken(AssistantHttpServer.NewToken())))
            UpdateAssistants();
    }

    // ----- Updates -----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(force: true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        CheckUpdatesButton.IsEnabled = false;
        VersionRow.Description = "Checking for updates…";
        try
        {
            var check = await AppUpdater.CheckForUpdateAsync(force);
            _release = check.Release;
            var current = AppUpdater.CurrentVersion;
            var available = _release != null && AppUpdater.IsNewer(_release.Version, current);
            // Never offer to replace a newer installed copy (for example from a build folder of an older version).
            var installed = AppUpdater.InstalledVersion;
            var replacesNewer = _release != null && installed != null && AppUpdater.IsOlder(_release.Version, installed);
            var installable = !replacesNewer && (available || _release != null && !AppUpdater.IsRunningInstalledCopy);
            VersionRow.Description = _release == null
                ? $"Could not check for updates: {check.Problem}"
                : replacesNewer
                    ? $"The newest release is v{_release.Version}. v{installed} is installed, which is newer."
                : available
                    ? $"Version {_release.Version} is available."
                    : AppUpdater.IsRunningInstalledCopy
                        ? "Up to date."
                        : $"Running from a build folder. Install v{_release.Version} to run from a fixed location.";
            InstallUpdateButton.Content = available ? $"Install {_release!.Version}" : "Install";
            InstallUpdateButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            UpdateDot.Visibility = available && !replacesNewer ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        var release = _release;
        if (Owner is MainWindow { IsAgentWorkBusy: true })
        {
            DialogWindow.Info(this, "The fleet agent is being updated", "Try again when it is done. Progress is shown in the activity list.");
            return;
        }
        var restartAgent = false;
        if (!await RunOperationAsync($"Install Runner Manager {release.Tag}", async log => restartAgent = await AppUpdater.InstallReleaseAsync(release, log)))
            return;
        if (restartAgent)
        {
            if (Owner is MainWindow main) main.RestartLocalAgentInBackground();
            else await MachineRelay.RestartLocalAgentAsync();
        }
        if (DialogWindow.Show(this, DialogTone.Question, $"Runner Manager {release.Tag} is installed",
                "Restart Runner Manager now to use the new version?", "Restart now", cancel: "Later") == "Restart now")
            ((App)Application.Current).RestartDashboard();
    }

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        var url = _release?.HtmlUrl is { Length: > 0 } html ? html : AppUpdater.ReleasesUrl;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenProgramFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(AppUpdater.InstallDir)) Process.Start(new ProcessStartInfo(AppUpdater.InstallDir) { UseShellExecute = true });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(FleetConfig.StateDir);
        Process.Start(new ProcessStartInfo(FleetConfig.StateDir) { UseShellExecute = true });
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        // Asked here, so cancelling keeps this dashboard running.
        if (App.AskUninstall(this) is not { } removeData) return;
        if (Owner is MainWindow main && !main.ConfirmExitDuringOperation()) return;
        var start = new ProcessStartInfo(AppUpdater.PreferredExe) { UseShellExecute = true, ArgumentList = { "--uninstall", App.ConfirmedArgument } };
        if (removeData) start.ArgumentList.Add(App.RemoveDataArgument);
        Process.Start(start);
        ((App)Application.Current).ExitApplication();
    }
}
