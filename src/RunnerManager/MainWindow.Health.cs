using System.Windows;

namespace RunnerManager;

/// <summary>
/// Stuck and long-running jobs, runners that idle while jobs wait for them, and queued jobs that no runner takes:
/// shown on the runners and reported once each as an alert.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(20);
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private bool _healthChecking;
    /// <summary>When each running runner was first seen idle, by <see cref="JobHealth.Key"/>.</summary>
    private readonly Dictionary<string, DateTime> _idleSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RunnerHealth> _runnerHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<QueueEntry>> _runnerQueues = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What was already reported, so each stuck job, long job or waiting job is reported once.</summary>
    private readonly HashSet<string> _reportedProblems = new(StringComparer.OrdinalIgnoreCase);

    private async Task CheckRunnerHealthAsync()
    {
        var now = DateTime.UtcNow;
        var runners = Machines.SelectMany(m => m.Runners).ToList();
        foreach (var runner in runners)
        {
            var key = JobHealth.Key(runner);
            if (runner.IsRunning && !runner.IsBusy) _idleSince.TryAdd(key, now);
            else _idleSince.Remove(key);
        }
        foreach (var gone in _idleSince.Keys.Except(runners.Select(JobHealth.Key), StringComparer.OrdinalIgnoreCase).ToList())
            _idleSince.Remove(gone);

        if (_healthChecking || now - _lastHealthCheck < HealthCheckInterval) return;
        _healthChecking = true;
        _lastHealthCheck = now;
        try
        {
            var health = await JobHealth.EvaluateAsync(Machines.ToList(), TimeSpan.FromMinutes(_settings.QuietMinutes),
                new Dictionary<string, DateTime>(_idleSince), _githubAuthenticated, DateTime.UtcNow);
            _runnerHealth.Clear();
            foreach (var (key, value) in health.Runners) _runnerHealth[key] = value;
            _runnerQueues.Clear();
            foreach (var (key, value) in health.Queues) _runnerQueues[key] = value;
            // Cards may have been rebuilt meanwhile; the results are kept by runner key.
            foreach (var runner in Machines.SelectMany(m => m.Runners)) ApplyHealth(runner);

            if (!_settings.NotifyOnJobProblems) return;
            foreach (var machine in Machines)
            {
                foreach (var runner in machine.Runners)
                {
                    if (!_runnerHealth.TryGetValue(JobHealth.Key(runner), out var problem) || !problem.IsProblem) continue;
                    if (_reportedProblems.Add($"{JobHealth.Key(runner)}|{problem.Subject}"))
                        RaiseAlert($"{machine.MachineName}: {runner.TitleText} — {problem.Label}. {problem.Detail}", warning: true);
                }
            }
            foreach (var job in health.Unserved)
                if (_reportedProblems.Add($"unserved|{job.JobId}")) RaiseAlert(job.Text, warning: true);
            if (_reportedProblems.Count > 2000) _reportedProblems.Clear();
        }
        catch (Exception ex) { AddActivity($"Could not check the runners' jobs: {ex.Message}"); }
        finally { _healthChecking = false; }
    }

    private void ApplyHealth(MachineRunnerStatus runner)
    {
        var key = JobHealth.Key(runner);
        runner.Health = _runnerHealth.GetValueOrDefault(key) ?? RunnerHealth.Healthy;
        runner.Queue = _runnerQueues.GetValueOrDefault(key) ?? (IReadOnlyList<QueueEntry>)Array.Empty<QueueEntry>();
    }

    private async void RunnerStopJob_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom<MachineRunnerStatus>(sender) is not { CanStopJob: true } runner) return;
        var machine = Machines.FirstOrDefault(m => m.MachineKey == runner.MachineKey);
        if (machine == null) return;
        // The processes as they are now, so the dialog says what will be stopped.
        var processes = string.Empty;
        try
        {
            var activity = machine.IsLocal
                ? await RunnerActivity.ReadAsync(runner.CommandKey)
                : await MachineRelay.RequestJobActivityAsync(machine.MachineKey, runner.CommandKey);
            var names = activity.Processes.Where(p => !p.Name.Equals("Runner.Worker", StringComparison.OrdinalIgnoreCase)
                                                      && !p.Name.Equals("Runner.Worker.exe", StringComparison.OrdinalIgnoreCase))
                .Select(p => System.IO.Path.GetFileNameWithoutExtension(p.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count > 0) processes = $"\n\nRunning now: {string.Join(", ", names)}.";
        }
        catch { }
        if (!DialogWindow.Confirm(this, $"Stop the job's processes on {runner.TitleText}?",
                "Everything the running job started is stopped, such as a build tool or a Unity compiler that no longer does anything. " +
                "The runner keeps running: the current step fails, and the job goes on to its next step or ends." + processes,
                "Stop processes", danger: true)) return;
        await RunRunnerActionAsync(runner, "StopJob", $"Stop the job's processes on {runner.TitleText}", async log =>
        {
            var stopped = await RunnerActivity.StopJobProcessesAsync(runner.CommandKey);
            log(stopped.Count == 0 ? "The job had no processes to stop." : $"Stopped: {string.Join(", ", stopped)}.");
        });
    }
}
