using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RunnerManager;

public partial class SettingsWindow : Window
{
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
        UpdateRdcStatus();
        NotificationsToggle.IsChecked = settings.Notifications;
        FailedRunsToggle.IsChecked = settings.NotifyOnFailedRuns;
        CloseToTrayToggle.IsChecked = settings.CloseToTray;
        RelayBox.Text = FleetConfig.Current.RelayRepository;
        WatchdogToggle.IsChecked = FleetConfig.Current.Watchdog;
        VersionText.Text = $"Runner Manager v{AppUpdater.CurrentVersion}" + (AppUpdater.IsRunningInstalledCopy ? "  ·  installed copy" : "  ·  running from a build folder");
        FooterText.Text = $"Data and logs: {FleetConfig.StateDir}";
        UpdateKeyStatus();
        RefreshPeers();
        Loaded += async (_, _) =>
        {
            StartupToggle.IsChecked = await ScheduledTasks.ExistsAsync(ScheduledTasks.DashboardTaskName);
            _loading = false;
            await CheckForUpdatesAsync(force: false);
        };
    }

    // ----- Appearance & dashboard -----

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Theme = ModeFor(sender);
        ThemeManager.Apply(_settings.Theme);
        Save();
    }

    private AppTheme ModeFor(object sender) => sender == ThemeSystem ? AppTheme.System : sender == ThemeLight ? AppTheme.Light : AppTheme.Dark;

    private void Refresh_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var seconds))
        {
            _settings.RefreshSeconds = seconds;
            Save();
        }
    }

    private void UpdateRdcStatus()
    {
        var configured = RemoteDesktopManager.IsConfigured();
        RdcStatusText.Text = configured ? "Set up on this PC" : "Not set up on this PC";
        RdcStatusText.Foreground = configured ? Palette.Success : Palette.TextMuted;
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
            {
                var exe = File.Exists(AppUpdater.InstalledExe) ? AppUpdater.InstalledExe : Environment.ProcessPath!;
                await ScheduledTasks.RegisterLogonTaskAsync(ScheduledTasks.DashboardTaskName, exe, "--minimized",
                    "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);
            }
            else
            {
                await ScheduledTasks.DeleteTaskAsync(ScheduledTasks.DashboardTaskName);
            }
        }
        catch (Exception ex)
        {
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
            DialogWindow.Error(this, "Could not change the Start with Windows setting", ex.Message);
        }
    }

    private void Save()
    {
        _settings.Save();
        _settingsChanged();
    }

    // ----- Fleet -----

    private async void RelaySave_Click(object sender, RoutedEventArgs e)
    {
        var repository = RelayBox.Text.Trim();
        RelayHint.Visibility = Visibility.Visible;
        if (!FleetConfig.IsValidRepository(repository))
        {
            RelayHint.Text = "Enter the repository as owner/name.";
            RelayHint.Foreground = Palette.Danger;
            return;
        }
        FleetConfig.Update(c => c.RelayRepository = repository);
        RelayHint.Text = "Saved. Updates now come from the releases in this repository.";
        RelayHint.Foreground = Palette.Success;
        await CheckForUpdatesAsync(force: true);
    }

    private void UpdateKeyStatus()
    {
        var key = FleetConfig.Current.FleetKey;
        KeyStatusText.Text = key == null
            ? "Not set. Start a new fleet on your first PC, or enter the pairing code from a PC that is already in the fleet."
            : $"Set (fingerprint {CommandSigner.KeyId(key)}). All PCs in the fleet must use this code and show the same fingerprint.";
        KeyStatusText.Foreground = key == null ? Palette.TextMuted : Palette.Success;
        CopyKeyButton.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        RemoveKeyButton.Visibility = key == null ? Visibility.Collapsed : Visibility.Visible;
        GenerateKeyButton.Content = key == null ? "Start a new fleet" : "Replace pairing code…";
    }

    private async void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        if (FleetConfig.Current.FleetKey != null && !DialogWindow.Confirm(this, "Replace the pairing code?",
                "Your other PCs cannot connect to this PC until you enter the new code on them.", "Replace code", danger: true))
            return;
        var key = CommandSigner.NewKey();
        FleetConfig.SetFleetKey(key);
        UpdateKeyStatus();
        CopyKey_Click(sender, e);
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
                "On each of your other PCs, go to Settings → Fleet → “Enter pairing code” and paste it. Keep the code private, like a password. Anyone on your network with this code can control your PCs.");
        }
        catch (Exception ex) { DialogWindow.Error(this, "Could not copy the pairing code", ex.Message); }
    }

    private void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        PasteKeyPanel.Visibility = Visibility.Visible;
        PasteKeyBox.Focus();
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
        PasteKeyBox.Clear();
        PasteKeyPanel.Visibility = Visibility.Collapsed;
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

    // ----- Updates -----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(force: true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub Releases…";
        try
        {
            _release = await AppUpdater.GetLatestReleaseAsync(force);
            var current = AppUpdater.CurrentVersion;
            if (_release == null)
            {
                UpdateStatusText.Text = $"{AppUpdater.ReleaseRepository} has no release with {AppUpdater.AssetName}. To build one, push a tag such as v{current} (see README).";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
            }
            else if (AppUpdater.IsNewer(_release.Version, current))
            {
                UpdateStatusText.Text = $"Version {_release.Version} is available.";
                InstallUpdateButton.Content = $"Install v{_release.Version}";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = AppUpdater.IsRunningInstalledCopy
                    ? $"Runner Manager is up to date (latest release: v{_release.Version})."
                    : $"The latest release is v{_release.Version}. Install it to run Runner Manager from a fixed location with a Start menu shortcut.";
                InstallUpdateButton.Content = "Install release";
                InstallUpdateButton.Visibility = AppUpdater.IsRunningInstalledCopy ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        var release = _release;
        var installed = false;
        await _runOperation($"Install Runner Manager {release.Tag}", async log =>
        {
            var exe = await AppUpdater.DownloadAndInstallAsync(release, log);
            await AppUpdater.CreateStartMenuShortcutAsync();
            if (await MachineRelay.IsAgentInstalledAsync())
            {
                log("Switching the fleet agent to the installed copy...");
                await MachineRelay.RegisterAgentTaskAsync(exe);
                await MachineRelay.RestartLocalAgentAsync();
            }
            if (await ScheduledTasks.ExistsAsync(ScheduledTasks.DashboardTaskName))
                await ScheduledTasks.RegisterLogonTaskAsync(ScheduledTasks.DashboardTaskName, exe, "--minimized",
                    "Opens the Runner Manager dashboard in the notification area at sign-in.", restartOnFailure: false);
            installed = true;
        });
        if (!installed) return;
        if (DialogWindow.Show(this, DialogTone.Question, $"Runner Manager {release.Tag} is installed",
                "Restart Runner Manager now to use the new version? It is also available in the Start menu.", "Restart now", cancel: "Later") == "Restart now")
        {
            Process.Start(new ProcessStartInfo(AppUpdater.InstalledExe) { UseShellExecute = true });
            ((App)Application.Current).ExitApplication();
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var exe = File.Exists(AppUpdater.InstalledExe) ? AppUpdater.InstalledExe : Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, ArgumentList = { "--uninstall" } });
        ((App)Application.Current).ExitApplication();
    }

    private void Releases_Click(object sender, RoutedEventArgs e)
    {
        var url = _release?.HtmlUrl is { Length: > 0 } html ? html : $"https://github.com/{AppUpdater.ReleaseRepository}/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ----- Peers -----

    private sealed record PeerRow(string Name, string Address, string LastSeenText);

    private void RefreshPeers()
    {
        var config = FleetConfig.Current;
        var peers = config.KnownPeers.OrderBy(p => p.Name).Select(p => new PeerRow(p.Name, p.Address,
            p.LastSeenUtc == DateTime.MinValue ? "never" : p.LastSeenUtc.ToLocalTime().ToString("g"))).ToList();
        PeersList.ItemsSource = peers;
        PeersEmptyText.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async void DeleteRelay_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogWindow.Confirm(this, "Delete the old relay variables?",
                $"This deletes all RW_STATUS_, RW_COMMAND_ and RW_LOG_ Actions variables in {AppUpdater.ReleaseRepository}. " +
                "PCs that run an older version create them again until they are updated.", "Delete", danger: true)) return;
        DeleteRelayButton.IsEnabled = false;
        try
        {
            var count = await MachineRelay.DeleteLegacyRelayDataAsync(AppUpdater.ReleaseRepository);
            DialogWindow.Info(this, "Old relay data deleted", count == 0 ? "There were no variables to delete." : $"Deleted {count} variable{(count == 1 ? "" : "s")}.");
        }
        catch (Exception ex) { DialogWindow.Error(this, "Could not delete the variables", ex.Message); }
        finally { DeleteRelayButton.IsEnabled = true; }
    }

    private async void LeaveFleet_Click(object sender, RoutedEventArgs e)
    {
        if (Owner is not MainWindow main) return;
        await main.LeaveFleetAsync();
        UpdateKeyStatus();
        RefreshPeers();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
