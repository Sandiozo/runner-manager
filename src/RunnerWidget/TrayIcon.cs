using System.Windows;
using Forms = System.Windows.Forms;

namespace RunnerWidget;

/// <summary>Notification-area icon: quick access to the window plus desktop notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _notificationsItem;
    private readonly Forms.ToolStripMenuItem _closeToTrayItem;

    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;
    public event Action<bool>? NotificationsToggled;
    public event Action<bool>? CloseToTrayToggled;

    public TrayIcon(AppSettings settings)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "Runner Manager",
            Icon = LoadIcon(),
            Visible = true
        };
        _notificationsItem = new Forms.ToolStripMenuItem("Desktop notifications") { CheckOnClick = true, Checked = settings.Notifications };
        _notificationsItem.CheckedChanged += (_, _) => NotificationsToggled?.Invoke(_notificationsItem.Checked);
        _closeToTrayItem = new Forms.ToolStripMenuItem("Keep running when closed") { CheckOnClick = true, Checked = settings.CloseToTray };
        _closeToTrayItem.CheckedChanged += (_, _) => CloseToTrayToggled?.Invoke(_closeToTrayItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open Runner Manager", null, (_, _) => OpenRequested?.Invoke());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripMenuItem("Refresh now", null, (_, _) => RefreshRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(_closeToTrayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SyncSettings(AppSettings settings)
    {
        _notificationsItem.Checked = settings.Notifications;
        _closeToTrayItem.Checked = settings.CloseToTray;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string message, bool warning = false)
        => _icon.ShowBalloonTip(5000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/assets/RunnerManager.ico"));
            if (resource != null)
                return new System.Drawing.Icon(resource.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
