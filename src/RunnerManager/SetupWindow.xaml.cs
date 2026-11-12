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
    /// <summary>The folder setup offers: this user's install where it is, the install that is on this PC, or Program Files.</summary>
    private readonly string _proposedFolder;
    /// <summary>The offered folder may be this user's install, but setup could not confirm it (see <see cref="SetupOffer"/>).</summary>
    private readonly bool _proposedUnconfirmed;
    private string _installFolder;
    private readonly Task _presetDone;

    public SetupWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        VersionText.Text = $"Setup · version {AppUpdater.CurrentVersion}";
        (_proposedFolder, _proposedUnconfirmed) = MachineInstall.SetupProposal();
        _installFolder = _proposedFolder;
        // Also says what setup does with a copy that is already in the folder it offers.
        ShowInstallFolder();
        _presetDone = PresetFromInstallAsync();
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
            NextStepsText.Text = WhatComesNext(choices.Fleet, await AlreadyInFleetAsync());
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

    /// <summary>
    /// What the last page says comes next. A PC that is already in a fleet stays in it whatever was chosen (the dashboard
    /// starts or joins a fleet only on a PC that is in none), so the page says that and not what to do on another PC.
    /// </summary>
    internal static string WhatComesNext(FleetChoice fleet, bool alreadyInFleet) => (fleet, alreadyInFleet) switch
    {
        (FleetChoice.JoinFleet, true) => "This PC is already in a fleet and stays in it.",
        (_, true) => "This PC is already in a fleet and stays in it. To leave the fleet, open Settings → Fleet → “Leave fleet”.",
        (FleetChoice.NewFleet, false) => "Next: Runner Manager starts a new fleet. Then install Runner Manager on your other PCs and choose “Join an existing fleet”.",
        (FleetChoice.JoinFleet, false) => "Next: on a PC that is already in the fleet, click “Add a PC” in the sidebar. This PC then connects to it.",
        _ => string.Empty
    };

    /// <summary>The same rule as the dashboard: the fleet agent is installed and this PC has the fleet code.</summary>
    private static async Task<bool> AlreadyInFleetAsync()
    {
        try { return FleetConfig.Current.FleetKey != null && await MachineRelay.IsAgentInstalledAsync(); }
        catch { return false; }
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

    /// <summary>
    /// Says on the first page what setup does with a copy that is already in the chosen folder, decided as
    /// <see cref="Installer.InstallAsync"/> decides it: this user's install is the recorded one, and other users are those
    /// that the records and the scheduled tasks show. Shown again for each folder the user chooses.
    /// </summary>
    private async Task ShowExistingInstallAsync()
    {
        var folder = _installFolder;
        var installedVersion = AppUpdater.InstalledVersion;
        var ownInstall = Installer.OwnInstall(MachineInstall.ReadOwnRecord()) is { } own && AppUpdater.SameFolder(own, folder);
        var sharedWithOthers = false;
        if (installedVersion != null)
        {
            try { sharedWithOthers = (await MachineInstall.OtherUsersOfAsync(folder)).Names.Count > 0; }
            catch (Exception ex) { MachineRelay.AppendAgentLog($"Setup could not read which other Windows users use {folder}: {ex.Message}"); }
        }
        // Another folder was chosen meanwhile; its own call shows the text.
        if (!AppUpdater.SameFolder(folder, _installFolder)) return;
        var text = ExistingInstallText(ownInstall, installedVersion, AppUpdater.CurrentVersion, sharedWithOthers);
        ExistingText.Text = text ?? string.Empty;
        ExistingText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// What setup's first page says about a copy that is already in the chosen folder, or null when there is none.
    /// <paramref name="ownInstall"/>: it is this user's recorded install. <paramref name="sharedWithOthers"/>: other Windows
    /// users of this PC use it, so setup never puts an older version in its place (see <see cref="Installer.KeepsInstalledCopy"/>).
    /// Any other copy is replaced by this setup's version.
    /// </summary>
    internal static string? ExistingInstallText(bool ownInstall, string? installedVersion, string setupVersion, bool sharedWithOthers)
    {
        if (installedVersion == null) return null;
        var older = AppUpdater.IsOlder(setupVersion, installedVersion);
        var keeps = Installer.KeepsInstalledCopy(sharedWithOthers, installedVersion, setupVersion);
        if (ownInstall)
            return !older ? "Runner Manager is already installed. Continue to update it. Your settings are kept. To move it, choose another folder."
                : keeps
                    ? $"Runner Manager v{installedVersion} is installed, which is newer than this setup (v{setupVersion}). Other Windows users on this PC use it, so setup keeps v{installedVersion}. Your settings are kept."
                    : $"Runner Manager v{installedVersion} is installed, which is newer than this setup (v{setupVersion}). Continue only to go back to the older version. Your settings are kept.";
        if (sharedWithOthers)
            return keeps
                ? $"Runner Manager v{installedVersion} is already in this folder, which is newer than this setup (v{setupVersion}). Other Windows users on this PC use it, so setup keeps v{installedVersion} and sets it up for your Windows user too."
                : AppUpdater.IsNewer(setupVersion, installedVersion)
                    ? $"Runner Manager v{installedVersion} is already in this folder, and other Windows users on this PC use it. Continue to update it to v{setupVersion} and to use it for your Windows user too."
                    : $"Runner Manager v{installedVersion} is already in this folder, and other Windows users on this PC use it. Continue to use it for your Windows user too.";
        return older
            ? $"Runner Manager v{installedVersion} is already in this folder, which is newer than this setup (v{setupVersion}). Continue only to go back to the older version."
            : $"Runner Manager v{installedVersion} is already in this folder. Continue to replace it with v{setupVersion}.";
    }

    /// <summary>
    /// The button beside the folder: shown once the folder differs from the one setup offered, to go back to it. It says
    /// "Default" when that is Program Files, and "Current folder" when it is where Runner Manager is installed now.
    /// </summary>
    internal static (bool Visible, string Label) FolderResetButton(string folder, string proposed, string programFilesDir)
        => (!AppUpdater.SameFolder(folder, proposed), AppUpdater.SameFolder(proposed, programFilesDir) ? "Default" : "Current folder");

    /// <summary>
    /// A note under the folder when it is outside Program Files, which Windows does not protect by itself: a folder the
    /// user chose, or the offered folder when setup could not confirm the install in it (<paramref name="proposedUnconfirmed"/>).
    /// </summary>
    internal static string? FolderNote(string folder, string proposed, string programFilesDir, bool proposedUnconfirmed)
    {
        if (AppUpdater.SameFolder(folder, programFilesDir)) return null;
        var offered = AppUpdater.SameFolder(folder, proposed);
        if (offered && !proposedUnconfirmed) return null;
        return (offered ? "Setup could not confirm that Runner Manager is installed in this folder. " : string.Empty) +
               "Runner Manager runs with administrator rights. Outside Program Files, check that only administrators can change the files in this folder.";
    }

    private void ShowInstallFolder()
    {
        // What setup says about a copy in the folder is about this folder.
        AppUpdater.UseInstallDir(_installFolder);
        _ = ShowExistingInstallAsync();
        InstallPathText.Text = _installFolder;
        var (visible, label) = FolderResetButton(_installFolder, _proposedFolder, MachineInstall.ProgramFilesDir);
        DefaultInstallFolderButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DefaultInstallFolderButton.Content = label;
        DefaultInstallFolderButton.ToolTip = _proposedFolder;
        var note = FolderNote(_installFolder, _proposedFolder, MachineInstall.ProgramFilesDir, _proposedUnconfirmed);
        FolderNoteText.Text = note ?? string.Empty;
        FolderNoteText.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
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
        _installFolder = _proposedFolder;
        ShowInstallFolder();
    }

    // Also Esc. During the install the window stays open (see the constructor).
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
