using System.Windows;

namespace RunnerWidget;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => a.Equals("--install-agent", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await MachineRelay.InstallAndStartAgentTaskAsync(); }
            finally { Shutdown(); }
            return;
        }

        if (e.Args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await MachineRelay.RunAgentAsync(CancellationToken.None); }
            finally { Shutdown(); }
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
