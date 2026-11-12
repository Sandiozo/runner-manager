using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RunnerManager;

/// <summary>
/// The installer: welcome and options → one PC or several → install. After installing it starts the installed app
/// with the follow-up step (start or join a fleet).
/// </summary>
public partial class SetupWindow : Window
{
    private int _page;
    private bool _installed;
    private bool _installing;
    private string _installedExe = string.Empty;
    private string _installFolder = AppUpdater.InstallDir;
    private readonly Task _presetDone;

    public SetupWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        VersionText.Text = $"Setup · version {AppUpdater.CurrentVersion}";
        ShowInstallFolder();
        _presetDone = PresetFromInstallAsync();
        if (Installer.IsInstalled)
        {
            ExistingText.Text = AppUpdater.InstalledVersion is { } installed && AppUpdater.IsOlder(AppUpdater.CurrentVersion, installed)
                ? $"Runner Manager v{installed} is installed, which is newer than this setup (v{AppUpdater.CurrentVersion}). Continue only to go back to the older version. Your settings are kept."
                : "Runner Manager is already installed. Continue to update it. Your settings are kept. To move it, choose another folder.";
            ExistingText.Visibility = Visibility.Visible;
        }
        if (FleetConfig.Current.FleetKey != null)
        {
            SeveralPcsOption.IsChecked = true;
            JoinFleetOption.IsChecked = true;
        }
        ShowPage(0);
        // Closing during the install could leave a half-installed copy, so the window stays open until it finishes.
        Closing += (_, e) => { if (_installing) e.Cancel = true; };
    }

    /// <summary>
    /// Setup over an existing install keeps its desktop shortcut and "Start with Windows": both are turned on here when
    /// they exist, because setup removes what is turned off.
    /// </summary>
    private async Task PresetFromInstallAsync()
    {
        DesktopShortcutToggle.IsChecked = Installer.HasDesktopShortcut;
        // Not changeable until the scheduled tasks are read, so the answer never overrides the user's choice.
        StartupToggle.IsEnabled = false;
        try
        {
            if (await Installer.StartsWithWindowsAsync() is { } starts)
            {
                StartupToggle.IsChecked = starts;
                _startupKnown = true;
            }
            else MachineRelay.AppendAgentLog("Setup could not read the startup task, so it leaves it as it is unless the choice is changed.");
        }
        catch (Exception ex) { MachineRelay.AppendAgentLog($"Setup could not read the startup task: {ex.Message}"); }
        finally { StartupToggle.IsEnabled = true; }
    }

    /// <summary>Whether the startup choice is known: read from the install, or set by the user. Otherwise setup leaves the task as it is.</summary>
    private bool _startupKnown;

    private void StartupToggle_Click(object sender, RoutedEventArgs e) => _startupKnown = true;

    private void ShowPage(int page)
    {
        _page = page;
        var pages = new FrameworkElement[] { PageWelcome, PagePcs, PageInstall };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
            dots[i].Background = (System.Windows.Media.Brush)FindResource(i <= page ? "AccentBrush" : "BorderBrush");
        StepText.Text = $"Step {page + 1} of {dots.Length}";
        BackButton.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = page switch { 1 => "Install", 2 => "Finish", _ => "Next" };
        NextButton.IsEnabled = page != 2 || _installed;
        CancelButton.Visibility = page == 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PcsOption_Checked(object sender, RoutedEventArgs e)
    {
        if (FleetOptions != null) FleetOptions.Visibility = SeveralPcsOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        // An install may still wait for the startup task to be read, on the options page.
        if (_installing) return;
        if (_page < 1) { ShowPage(_page + 1); return; }
        if (_page == 1) { await InstallAsync(); return; }
        Finish();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        if (_page == 1) ShowPage(0);
        // After a failed install: back to the options, to change them and install again.
        else if (_page == 2 && !_installed && !_installing) ShowPage(1);
    }

    private SetupChoices Choices => new(
        DesktopShortcutToggle.IsChecked == true,
        _startupKnown ? StartupToggle.IsChecked == true : null,
        SeveralPcsOption.IsChecked != true ? FleetChoice.SinglePc : JoinFleetOption.IsChecked == true ? FleetChoice.JoinFleet : FleetChoice.NewFleet);

    private async Task InstallAsync()
    {
        _installing = true;
        NextButton.IsEnabled = false;
        await _presetDone;
        var choices = Choices;
        ResetInstallPage();
        ShowPage(2);
        try
        {
            AppUpdater.UseInstallDir(_installFolder);
            _installedExe = await Installer.InstallAsync(choices, message => Dispatcher.Invoke(() => InstallStatus.Text = message));
            _installed = true;
            InstallTitle.Text = "Runner Manager is installed";
            DoneGlyph.Text = "";
            DoneGlyph.Foreground = Palette.Success;
            DoneBadge.Background = Palette.Tint(Palette.Success);
            InstallStatus.Text = "Runner Manager is in the Start menu. To remove it, go to Settings → Apps.";
            NextStepsText.Text = choices switch
            {
                { Fleet: FleetChoice.NewFleet } => "Next: Runner Manager starts a new fleet. Then install Runner Manager on your other PCs and choose “Join an existing fleet”.",
                { Fleet: FleetChoice.JoinFleet } => "Next: on a PC that is already in the fleet, click “Add a PC” in the sidebar. This PC then connects to it.",
                _ => string.Empty
            };
            NextStepsText.Visibility = NextStepsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            LaunchToggle.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            InstallTitle.Text = "Installation failed";
            DoneGlyph.Text = "";
            DoneGlyph.Foreground = Palette.Danger;
            DoneBadge.Background = Palette.Tint(Palette.Danger);
            InstallStatus.Text = ex.Message;
            CancelButton.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _installing = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            NextButton.IsEnabled = _installed;
        }
    }

    /// <summary>The last page as it is before an install, also after a failed one.</summary>
    private void ResetInstallPage()
    {
        InstallTitle.Text = "Installing…";
        DoneGlyph.Text = "";
        DoneGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        DoneBadge.SetResourceReference(Border.BackgroundProperty, "SelectedBrush");
        InstallStatus.Text = string.Empty;
        InstallProgress.Visibility = Visibility.Visible;
        NextStepsText.Visibility = Visibility.Collapsed;
        LaunchToggle.Visibility = Visibility.Collapsed;
    }

    private void Finish()
    {
        if (_installed && LaunchToggle.IsChecked == true)
        {
            var choices = Choices;
            var args = new List<string>();
            if (choices.Fleet == FleetChoice.NewFleet) args.Add("--fleet=new");
            if (choices.Fleet == FleetChoice.JoinFleet) args.Add("--fleet=join");
            var start = new ProcessStartInfo(_installedExe) { UseShellExecute = true, WorkingDirectory = AppUpdater.InstallDir };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            try { Process.Start(start); } catch { }
        }
        Close();
    }

    private void ShowInstallFolder()
    {
        InstallPathText.Text = _installFolder;
        DefaultInstallFolderButton.Visibility = AppUpdater.IsDefaultInstallDir(_installFolder) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChangeInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var current = Directory.Exists(_installFolder) ? _installFolder : Path.GetDirectoryName(_installFolder);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install Runner Manager",
            InitialDirectory = current != null && Directory.Exists(current) ? current : null
        };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Installer.InstallFolderFor(dialog.FolderName);
        if (Installer.InstallFolderProblem(folder) is { } problem)
        {
            DialogWindow.Info(this, "Choose another folder", problem);
            return;
        }
        _installFolder = folder;
        ShowInstallFolder();
    }

    private void DefaultInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        _installFolder = AppUpdater.DefaultInstallDir;
        ShowInstallFolder();
    }

    // Also Esc. During the install the window stays open (see the constructor).
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
