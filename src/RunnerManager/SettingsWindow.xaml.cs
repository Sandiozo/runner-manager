using System.Diagnostics;
using System.IO;
using System.Windows;
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
    private readonly Func<string, Func<Action<string>, Task>, Task> _runOperation;
    private bool _loading = true;
    private ReleaseInfo? _release;

    public SettingsWindow(AppSettings settings, Action settingsChanged, Func<Task> fleetChanged,
        Func<string, Func<Action<string>, Task>, Task> runOperation)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        _settings = settings;
        _settingsChanged = settingsChanged;
        _fleetChanged = fleetChanged;
        _runOperation = runOperation;

        (settings.Theme switch { AppTheme.System => ThemeSystem, AppTheme.Light => ThemeLight, _ => ThemeDark }).IsChecked = true;
        (settings.RefreshSeconds switch { >= 30 => Refresh30, >= 10 => Refresh10, _ => Refresh5 }).IsChecked = true;
        NotificationsToggle.IsChecked = settings.Notifications;
        FailedRunsToggle.IsChecked = settings.NotifyOnFailedRuns;
        CloseToTrayToggle.IsChecked = settings.CloseToTray;
        WatchdogToggle.IsChecked = FleetConfig.Current.Watchdog;
        NavVersionText.Text = $"Runner Manager {AppUpdater.CurrentVersion}";
        VersionRow.Title = $"Runner Manager {AppUpdater.CurrentVersion}";
        DataRow.Description = FleetConfig.StateDir;
        ProgramRow.Description = $"{AppUpdater.InstallDir}. To move it, run the setup again and choose another folder.";
        UpdateRdcStatus();
        UpdateRunnerFolder();
        UpdateKeyStatus();
        RefreshPeers();
        BuildAssistantClients();
        UpdateAssistants();
        ShowPage(_lastPage);
        Loaded += async (_, _) =>
        {
            StartupToggle.IsChecked = await ScheduledTasks.ExistsAsync(ScheduledTasks.DashboardTaskName);
            _loading = false;
            await CheckForUpdatesAsync(force: false);
        };
    }

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
        RdcPage.Visibility = page == "Rdc" ? Visibility.Visible : Visibility.Collapsed;
        AssistantsPage.Visibility = page == "Assistants" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Assistants") UpdateAssistants();
        UpdatesPage.Visibility = page == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.FocusedElement is TextBox { Text.Length: > 0 }) return;
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

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Notifications = NotificationsToggle.IsChecked == true;
        _settings.NotifyOnFailedRuns = FailedRunsToggle.IsChecked == true;
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
        var key = FleetConfig.Current.FleetKey;
        KeyRow.Description = key == null
            ? "Not set. Start a new fleet here, or enter the code from a PC that is already in one."
            : $"Set. Fingerprint {CommandSigner.KeyId(key)}, the same on every PC in the fleet.";
        GenerateKeyButton.Visibility = key == null ? Visibility.Visible : Visibility.Collapsed;
        CopyKeyButton.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        ReplaceKeyItem.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        RemoveKeyItem.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        LeaveFleetButton.IsEnabled = key != null;
    }

    private async void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (FleetConfig.Current.FleetKey != null && !DialogWindow.Confirm(this, "Replace the pairing code?",
                "Your other PCs cannot connect to this PC until you enter the new code on them.", "Replace code", danger: true))
            return;
        FleetConfig.SetFleetKey(CommandSigner.NewKey());
        UpdateKeyStatus();
        await _fleetChanged();
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var key = FleetConfig.Current.FleetKey;
        if (key == null) return;
        try
        {
            Clipboard.SetText(CommandSigner.FormatKey(key));
            DialogWindow.Info(this, "Pairing code copied",
                "On another PC, open Settings → Fleet → ⋯ → Enter pairing code, and paste it. Keep the code private, like a password: anyone on your network with it can control your PCs.");
        }
        catch (Exception ex) { DialogWindow.Error(this, "Could not copy the pairing code", ex.Message); }
    }

    private void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        PasteKeyPanel.Visibility = Visibility.Visible;
        PasteKeyBox.Focus();
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
            DialogWindow.Info(this, "Invalid pairing code", "Paste the full code copied from another PC. It starts with rwk_.");
            return;
        }
        FleetConfig.SetFleetKey(key);
        CancelPasteKey_Click(sender, e);
        UpdateKeyStatus();
        await _fleetChanged();
    }

    private async void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Confirm(this, "Remove the pairing code?",
                "This PC and your other PCs cannot connect to each other until you set a pairing code again.", "Remove code", danger: true)) return;
        FleetConfig.SetFleetKey(null);
        UpdateKeyStatus();
        await _fleetChanged();
    }

    private async void WatchdogToggle_Click(object sender, RoutedEventArgs e)
    {
        FleetConfig.Update(c => c.Watchdog = WatchdogToggle.IsChecked == true);
        await _fleetChanged();
    }

    private sealed record PeerRow(string Name, string Detail);

    private void RefreshPeers()
    {
        var config = FleetConfig.Current;
        var peers = config.KnownPeers.OrderBy(p => p.Name).Select(p => new PeerRow(p.Name,
            $"{p.Address}  ·  last seen {(p.LastSeenUtc == DateTime.MinValue ? "never" : p.LastSeenUtc.ToLocalTime().ToString("g"))}")).ToList();
        PeersList.ItemsSource = peers;
        PeersEmptyPanel.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddressList.ItemsSource = config.ManualAddresses.ToList();
        ThisPcAddressText.Text = $"This PC: {Environment.MachineName}  ·  {string.Join(", ", FleetNetwork.LocalAddresses())}  ·  port {FleetNetwork.AgentPort}";
    }

    private async void FindPcs_Click(object sender, RoutedEventArgs e)
    {
        FindPcsButton.IsEnabled = false;
        FindPcsButton.Content = "Searching…";
        try
        {
            await _fleetChanged();
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
        if (address.Length == 0 || address.Contains(' ') || address.Contains('/'))
        {
            DialogWindow.Info(this, "Invalid address", "Enter a PC name, Tailscale name or IP address, with an optional :port.");
            return;
        }
        FleetConfig.Update(c =>
        {
            if (!c.ManualAddresses.Contains(address, StringComparer.OrdinalIgnoreCase)) c.ManualAddresses.Add(address);
        });
        AddressBox.Clear();
        RefreshPeers();
        await _fleetChanged();
        RefreshPeers();
    }

    private async void RemoveAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address }) return;
        FleetConfig.Update(c => c.ManualAddresses.RemoveAll(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)));
        RefreshPeers();
        await _fleetChanged();
    }

    private async void ForgetPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        MachineRelay.ForgetPeer(name);
        RefreshPeers();
        await _fleetChanged();
    }

    private async void LeaveFleet_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not MainWindow main) return;
        await main.LeaveFleetAsync();
        UpdateKeyStatus();
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
        List<MachineInfo> pcs;
        ChangeFleetCodeButton.IsEnabled = false;
        ChangeFleetCodeButton.Content = "Checking PCs…";
        try
        {
            pcs = (await MachineRelay.ReadFleetAsync(forceDiscovery: true))
                .Where(m => !m.IsLocal && !m.MachineKey.StartsWith("ADDR_") && !m.MachineKey.StartsWith("DUPLICATE_")).ToList();
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

        MachineRelay.FleetCodeChange? result = null;
        Exception? failure = null;
        await _runOperation("Change fleet code", async log =>
        {
            try { result = await MachineRelay.ChangeFleetCodeAsync(choice.Keep, choice.LeaveOut, log); }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
        });
        UpdateKeyStatus();
        RefreshPeers();
        await _fleetChanged();
        if (failure != null) DialogWindow.Error(this, "Could not change the fleet code", failure.Message);
        if (result == null) return;

        var lines = new List<string>();
        if (result.Switched.Count > 0) lines.Add($"Use the new code: {string.Join(", ", result.Switched)}.");
        if (result.NotConfirmed.Count > 0) lines.Add($"Got the new code and switch when contacted next: {string.Join(", ", result.NotConfirmed)}.");
        if (result.Failed.Count > 0) lines.Add($"Did not get it, and must be paired again:\n{string.Join("\n", result.Failed)}");
        if (result.LeftOut.Count > 0) lines.Add($"Left out and forgotten: {string.Join(", ", result.LeftOut)}. Pair them again to bring them back.");
        if (lines.Count == 0) lines.Add("No other PCs are in the fleet.");
        DialogWindow.Show(this, result.Failed.Count > 0 ? DialogTone.Warning : DialogTone.Info, "The fleet has a new pairing code",
            string.Join("\n\n", lines), "OK", cancel: null);
    }

    /// <summary>Asks which PCs get the new code. Only online PCs with a current agent can; the others are left out.</summary>
    private (List<MachineInfo> Keep, List<MachineInfo> LeaveOut)? ChooseFleetCodePcs(IReadOnlyList<MachineInfo> pcs)
    {
        var window = new Window
        {
            Title = "Change fleet code", Owner = this, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontSize = 13,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont")
        };
        window.SetResourceReference(BackgroundProperty, "Bg1Brush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        WindowTheme.Apply(window);

        var body = new StackPanel { Margin = new Thickness(28, 26, 28, 20) };
        body.Children.Add(new TextBlock { Text = "Change the fleet code?", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
            Text = "The PCs you keep get a new pairing code, and the old one stops working. PCs you leave out, and PCs that are " +
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
        var change = new Button { Content = "Change code", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Style = (Style)FindResource("SolidDangerButton") };
        change.Click += (_, _) =>
        {
            confirmed = true;
            window.DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, Style = (Style)FindResource("GhostButton") };
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

    // ----- Remote Desktop Commander -----

    private void UpdateRdcStatus()
    {
        var configured = RemoteDesktopManager.IsConfigured();
        RdcRow.Title = configured ? "Set up on this PC" : "Not set up on this PC";
        RdcRow.Description = configured
            ? "Runner Manager monitors it and restarts it when it stops."
            : "Set it up to monitor and restart it from Runner Manager.";
        RdcSetupButton.Content = configured ? "Run setup again" : "Set up";
        RdcSetupButton.Style = (Style)FindResource(configured ? typeof(Button) : "PrimaryButton");
    }

    private async void RdcSetup_Click(object sender, RoutedEventArgs e)
    {
        await _runOperation("Set up Remote Desktop Commander", async log =>
        {
            await RemoteDesktopManager.SetupAsync();
            log(RemoteDesktopManager.IsConfigured()
                ? "Remote Desktop Commander is set up and starting."
                : "Setup window opened. Sign in there. Runner Manager detects the connection automatically.");
        });
        UpdateRdcStatus();
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
        catch (Exception ex) { DialogWindow.Error(this, "Could not copy", ex.Message); }
    }

    private async void AssistantToggle_Click(object sender, RoutedEventArgs e)
    {
        var enable = AssistantToggle.IsChecked == true;
        if (enable && (FleetConfig.Current.FleetKey == null || !await MachineRelay.IsAgentRunningAsync()))
        {
            // Assistants hand their changes to this PC's fleet agent, which needs a pairing code even with only this PC.
            var ready = false;
            await _runOperation("Turn on AI assistants", async log =>
            {
                if (FleetConfig.Current.FleetKey == null) FleetConfig.SetFleetKey(CommandSigner.NewKey());
                await AppUpdater.InstallCurrentBuildAsync(log);
                log("Starting the fleet agent…");
                await MachineRelay.InstallAndStartAgentTaskAsync();
                ready = true;
            });
            UpdateKeyStatus();
            if (!ready)
            {
                AssistantToggle.IsChecked = false;
                return;
            }
        }
        FleetConfig.Update(c => c.Assistant.Enabled = enable);
        AssistantLog.Append(enable ? "Assistant access turned on." : "Assistant access turned off.");
        UpdateAssistants();
        if (enable) await _fleetChanged();
    }

    private void AssistantAskToggle_Click(object sender, RoutedEventArgs e)
    {
        FleetConfig.Update(c => c.Assistant.AskBeforeChanges = AssistantAskToggle.IsChecked == true);
        UpdateAssistants();
    }

    private void AssistantHttpToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = AssistantHttpToggle.IsChecked == true;
        FleetConfig.Update(c =>
        {
            c.Assistant.HttpEnabled = on;
            if (on && c.Assistant.HttpToken.Length == 0) c.Assistant.ProtectedHttpToken = AssistantOptions.ProtectToken(AssistantHttpServer.NewToken());
        });
        UpdateAssistants();
    }

    private void AssistantNewToken_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Confirm(this, "Create a new token?",
                "AI tools that use the current token cannot connect until you give them the new one.", "New token", danger: true)) return;
        FleetConfig.Update(c => c.Assistant.ProtectedHttpToken = AssistantOptions.ProtectToken(AssistantHttpServer.NewToken()));
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
                ? $"Couldn't check for updates: {check.Problem}"
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
        var installed = false;
        var restartAgent = false;
        await _runOperation($"Install Runner Manager {release.Tag}", async log =>
        {
            restartAgent = await AppUpdater.InstallReleaseAsync(release, log);
            installed = true;
        });
        if (!installed) return;
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
