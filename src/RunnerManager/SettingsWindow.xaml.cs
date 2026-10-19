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

    // ----- Updates -----

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(force: true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        CheckUpdatesButton.IsEnabled = false;
        VersionRow.Description = "Checking for updates…";
        try
        {
            _release = await AppUpdater.GetLatestReleaseAsync(force);
            var current = AppUpdater.CurrentVersion;
            var available = _release != null && AppUpdater.IsNewer(_release.Version, current);
            var installable = available || _release != null && !AppUpdater.IsRunningInstalledCopy;
            VersionRow.Description = _release == null
                ? $"No release found in {AppUpdater.ReleaseRepository}."
                : available
                    ? $"Version {_release.Version} is available."
                    : AppUpdater.IsRunningInstalledCopy
                        ? "Up to date."
                        : $"Running from a build folder. Install v{_release.Version} to run from a fixed location.";
            InstallUpdateButton.Content = available ? $"Install {_release!.Version}" : "Install";
            InstallUpdateButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            UpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
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
                await Installer.RegisterStartupTaskAsync(exe);
            installed = true;
        });
        if (!installed) return;
        if (DialogWindow.Show(this, DialogTone.Question, $"Runner Manager {release.Tag} is installed",
                "Restart Runner Manager now to use the new version?", "Restart now", cancel: "Later") == "Restart now")
        {
            Process.Start(new ProcessStartInfo(AppUpdater.InstalledExe) { UseShellExecute = true });
            ((App)Application.Current).ExitApplication();
        }
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
        var exe = AppUpdater.PreferredExe;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, ArgumentList = { "--uninstall" } });
        ((App)Application.Current).ExitApplication();
    }
}
