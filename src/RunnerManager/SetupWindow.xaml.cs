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

    public SetupWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        VersionText.Text = $"Setup · version {AppUpdater.CurrentVersion}";
        ShowInstallFolder();
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

    private void ShowPage(int page)
    {
        _page = page;
        var pages = new FrameworkElement[] { PageWelcome, PagePcs, PageInstall };
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
            dots[i].Background = (System.Windows.Media.Brush)FindResource(i <= page ? "AccentBrush" : "BorderBrush");
        BackButton.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = page switch { 1 => "Install", 2 => "Finish", _ => "Next" };
        CancelButton.Visibility = page == 2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PcsOption_Checked(object sender, RoutedEventArgs e)
    {
        if (FleetOptions != null) FleetOptions.Visibility = SeveralPcsOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < 1) { ShowPage(_page + 1); return; }
        if (_page == 1) { await InstallAsync(); return; }
        Finish();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_page == 1) ShowPage(0);
    }

    private SetupChoices Choices => new(
        DesktopShortcutToggle.IsChecked == true,
        StartupToggle.IsChecked == true,
        SeveralPcsOption.IsChecked != true ? FleetChoice.SinglePc : JoinFleetOption.IsChecked == true ? FleetChoice.JoinFleet : FleetChoice.NewFleet);

    private async Task InstallAsync()
    {
        var choices = Choices;
        ShowPage(2);
        NextButton.IsEnabled = false;
        _installing = true;
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
            _page = 2;
        }
        finally
        {
            _installing = false;
            InstallProgress.Visibility = Visibility.Collapsed;
            NextButton.IsEnabled = _installed;
        }
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_page == 2 && !_installed && BackButton.Visibility == Visibility.Visible) { ShowPage(1); return; }
        Close();
    }
}
