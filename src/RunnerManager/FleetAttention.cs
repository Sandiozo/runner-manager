namespace RunnerManager;

/// <summary>
/// What the "Needs attention" tile counts, over the whole fleet: PCs that are offline (each PC once, not its runners),
/// and on the PCs that are online, runners that are stopped and runners with a concern (<see cref="RunnerHealth"/>):
/// a job that looks stuck or runs long, or a runner that idles while jobs wait for it. Its filter shows only those.
/// </summary>
internal sealed record FleetAttention(int OfflinePcs, int StoppedRunners, int StuckJobs, int LongJobs, int IdleRunners)
{
    public int Total => OfflinePcs + StoppedRunners + StuckJobs + LongJobs + IdleRunners;

    /// <summary>
    /// A runner that is not running, unless it was stopped on purpose with Stop. A console runner that is stopped because
    /// nobody is signed in on its PC counts: nothing runs its jobs.
    /// </summary>
    public static bool IsUnexpectedlyStopped(MachineRunnerStatus runner) => !runner.IsRunning && !runner.ManuallyStopped;

    public static FleetAttention Count(IEnumerable<MachineInfo> machines)
    {
        int offline = 0, stopped = 0, stuck = 0, longJobs = 0, idle = 0;
        foreach (var machine in machines)
        {
            if (!machine.IsReachable)
            {
                offline++;
                continue;
            }
            stopped += machine.Runners.Count(IsUnexpectedlyStopped);
            // A stopped runner is counted as stopped only.
            foreach (var runner in machine.ProblemRunners.Where(r => r.IsRunning))
            {
                switch (runner.Health.Concern)
                {
                    case RunnerConcern.Stuck: stuck++; break;
                    case RunnerConcern.RunningLong: longJobs++; break;
                    case RunnerConcern.IdleWithWaitingJobs: idle++; break;
                }
            }
        }
        return new FleetAttention(offline, stopped, stuck, longJobs, idle);
    }

    /// <summary>
    /// A runner on a PC that is online needs attention when it is stopped (not on purpose) or has a concern, as
    /// <see cref="Count"/> counts it.
    /// </summary>
    public static bool RunnerNeedsAttention(MachineRunnerStatus runner) => IsUnexpectedlyStopped(runner) || runner.Health.IsProblem;

    /// <summary>A PC is shown by the filter when it is offline or has a runner that needs attention.</summary>
    public static bool PcNeedsAttention(MachineInfo machine) => !machine.IsReachable || machine.Runners.Any(RunnerNeedsAttention);

    /// <summary>The runners the filter shows: all of an offline PC's, otherwise those that need attention.</summary>
    public static bool ShowsRunner(MachineInfo machine, MachineRunnerStatus runner) => !machine.IsReachable || RunnerNeedsAttention(runner);

    /// <summary>The tile's tooltip, such as "1 PC is offline, 2 runners are stopped, 1 job looks stuck."</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (OfflinePcs > 0) parts.Add(OfflinePcs == 1 ? "1 PC is offline" : $"{OfflinePcs} PCs are offline");
            if (StoppedRunners > 0) parts.Add(StoppedRunners == 1 ? "1 runner is stopped" : $"{StoppedRunners} runners are stopped");
            if (StuckJobs > 0) parts.Add(StuckJobs == 1 ? "1 job looks stuck" : $"{StuckJobs} jobs look stuck");
            if (LongJobs > 0) parts.Add(LongJobs == 1 ? "1 job is running long" : $"{LongJobs} jobs are running long");
            if (IdleRunners > 0)
                parts.Add(IdleRunners == 1 ? "1 runner is idle while jobs wait for it" : $"{IdleRunners} runners are idle while jobs wait for them");
            return parts.Count == 0 ? "Nothing needs attention." : string.Join(", ", parts) + ".";
        }
    }

    /// <summary>The tile's second line, short: "1 offline · 2 stopped · 1 stuck".</summary>
    public string SubText
    {
        get
        {
            var parts = new List<string>();
            if (OfflinePcs > 0) parts.Add($"{OfflinePcs} offline");
            if (StoppedRunners > 0) parts.Add($"{StoppedRunners} stopped");
            if (StuckJobs > 0) parts.Add($"{StuckJobs} stuck");
            if (LongJobs > 0) parts.Add($"{LongJobs} running long");
            if (IdleRunners > 0) parts.Add($"{IdleRunners} idle, jobs waiting");
            return string.Join(" · ", parts);
        }
    }
}
