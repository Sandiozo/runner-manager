using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunnerManager;

public enum AssistantToolKind
{
    /// <summary>Only reads; always allowed while assistant access is on.</summary>
    Read,
    /// <summary>Creates, starts, stops or restarts; asks first when "Ask before changes" is on.</summary>
    Change,
    /// <summary>Moves or removes; always asks first.</summary>
    Destructive
}

/// <summary>An error the assistant should see as the tool's answer, such as a missing argument or a declined approval.</summary>
public sealed class AssistantException(string message) : Exception(message);

public sealed record AssistantTool(string Name, string Description, AssistantToolKind Kind, JsonObject InputSchema,
    Func<AssistantCall, Task<object>> Run);

/// <summary>One tool call: its arguments, plus how to ask the user and where progress goes.</summary>
public sealed class AssistantCall
{
    private readonly JsonElement _arguments;
    private readonly Func<string, string, Task<bool>> _approve;

    public AssistantCall(JsonElement arguments, Func<string, string, Task<bool>> approve, Action<string> log)
    {
        _arguments = arguments;
        _approve = approve;
        Log = log;
    }

    public Action<string> Log { get; }

    public string? Text(string name)
        => _arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
            : null;

    public string RequiredText(string name) => Text(name) ?? throw new AssistantException($"The argument \"{name}\" is required.");

    public bool Flag(string name)
        => _arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public int Number(string name, int fallback, int min, int max)
        => _arguments.ValueKind == JsonValueKind.Object && _arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? Math.Clamp(number, min, max) : fallback;

    public string[] TextList(string name)
    {
        if (_arguments.ValueKind != JsonValueKind.Object || !_arguments.TryGetProperty(name, out var value)) return Array.Empty<string>();
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!.Trim()).Where(v => v.Length > 0).ToArray(),
            JsonValueKind.String => value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>()
        };
    }

    /// <summary>Asks the user when the rules in Settings → AI assistants require it. Throws when the user declines.</summary>
    public async Task ConfirmAsync(AssistantToolKind kind, string title, string message)
    {
        var ask = kind == AssistantToolKind.Destructive || (kind == AssistantToolKind.Change && FleetConfig.Current.Assistant.AskBeforeChanges);
        if (!ask) return;
        if (!await _approve(title, message))
            throw new AssistantException("The user declined this action in Runner Manager.");
    }
}

/// <summary>
/// What AI assistants can do through Runner Manager. The same tools are offered over MCP (stdio and HTTP) and on the
/// command line. Changes go to the fleet agents, which have administrator rights, through the encrypted fleet
/// connection; this process needs none.
/// </summary>
public static class AssistantTools
{
    private static readonly FleetActions Actions = new(viaLocalAgent: true);
    private static readonly string[] DefaultLabels = { "self-hosted", "Windows", "X64" };
    private const string RunnerArgument = "The runner's name, as list_runners shows it.";
    private const string PcArgument = "The PC's name, as list_pcs shows it.";

    public static IReadOnlyList<AssistantTool> All { get; } = new[]
    {
        new AssistantTool("list_pcs",
            "Lists the PCs in the Runner Manager fleet: online state, version, CPU, memory, free disk space and runner counts.",
            AssistantToolKind.Read, Schema(), ListPcsAsync),
        new AssistantTool("list_runners",
            "Lists the GitHub Actions self-hosted runners on every PC (or one PC): state (busy, idle, stopped), repository or organization, and version.",
            AssistantToolKind.Read, Schema(("pc", "string", "Only this PC. Optional.", false)), ListRunnersAsync),
        new AssistantTool("find_runner_for_repo",
            "Checks whether a GitHub repository already has a self-hosted runner in the fleet (its own, or an organization runner). Call this before create_runner. Also returns the runs-on labels to use in workflows.",
            AssistantToolKind.Read, Schema(("repository", "string", "owner/name or a https://github.com/owner/name URL.", true)), FindRunnerAsync),
        new AssistantTool("create_runner",
            "Creates a self-hosted runner for a GitHub repository or organization on a PC in the fleet: downloads the official runner, verifies it, and installs it as a Windows service. Picks the least busy PC when pc is omitted. Refuses when the repository already has a runner unless even_if_exists is true.",
            AssistantToolKind.Change, Schema(
                ("repository", "string", "owner/name or a https://github.com/owner/name URL. Use an organization URL for an organization runner.", true),
                ("pc", "string", "The PC to install it on. Optional; the least busy online PC is used.", false),
                ("name", "string", "Runner name. Optional; letters, digits, '-', '_' and '.'.", false),
                ("labels", "array", "Extra labels besides self-hosted, Windows and X64. Optional.", false),
                ("even_if_exists", "boolean", "Create another runner even if the repository already has one.", false)),
            CreateRunnerAsync),
        new AssistantTool("control_runner",
            "Starts, stops or restarts a runner's Windows service.",
            AssistantToolKind.Change, Schema(
                ("pc", "string", PcArgument, true),
                ("runner", "string", RunnerArgument, true),
                ("action", "string", "start, stop or restart.", true)),
            ControlRunnerAsync),
        new AssistantTool("move_runner",
            "Moves a runner to another PC in the fleet, keeping its name and labels. The user is always asked first.",
            AssistantToolKind.Destructive, Schema(
                ("pc", "string", "The PC the runner is on now.", true),
                ("runner", "string", RunnerArgument, true),
                ("to_pc", "string", "The PC to move it to.", true),
                ("delete_old_files", "boolean", "Also delete the runner folder on the old PC. Default false.", false)),
            MoveRunnerAsync),
        new AssistantTool("remove_runner",
            "Unregisters a runner from GitHub and removes its Windows service. The user is always asked first.",
            AssistantToolKind.Destructive, Schema(
                ("pc", "string", PcArgument, true),
                ("runner", "string", RunnerArgument, true),
                ("delete_files", "boolean", "Also delete the runner folder. Default false.", false)),
            RemoveRunnerAsync),
        new AssistantTool("job_status",
            "Shows what a runner is doing: the running job and its steps, the latest output lines, and the job's processes with CPU and memory.",
            AssistantToolKind.Read, Schema(("pc", "string", PcArgument, true), ("runner", "string", RunnerArgument, true)), JobStatusAsync),
        new AssistantTool("recent_runs",
            "Lists the latest GitHub Actions workflow runs of a repository, with their result and links.",
            AssistantToolKind.Read, Schema(
                ("repository", "string", "owner/name or a https://github.com/owner/name URL.", true),
                ("count", "integer", "How many runs, 1 to 20. Default 10.", false)),
            RecentRunsAsync),
        new AssistantTool("runner_log",
            "Reads the end of a runner's diagnostic log, useful when a runner does not start or does not pick up jobs.",
            AssistantToolKind.Read, Schema(
                ("pc", "string", PcArgument, true),
                ("runner", "string", RunnerArgument, true),
                ("lines", "integer", "How many lines, 20 to 400. Default 150.", false)),
            RunnerLogAsync)
    };

    /// <summary>Runs a tool: checks that assistant access is on, then calls it and records it in the assistant log.</summary>
    public static async Task<object> CallAsync(string name, JsonElement arguments, Func<string, string, Task<bool>> approve, Action<string>? log = null)
    {
        if (!FleetConfig.Current.Assistant.Enabled)
            throw new AssistantException("AI assistant access is turned off. Turn it on in Runner Manager → Settings → AI assistants.");
        var tool = All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new AssistantException($"Unknown tool: {name}. Available tools: {string.Join(", ", All.Select(t => t.Name))}.");
        var steps = new List<string>();
        var call = new AssistantCall(arguments, approve, message => { steps.Add(message); log?.Invoke(message); });
        try
        {
            var result = await tool.Run(call);
            if (tool.Kind != AssistantToolKind.Read) AssistantLog.Append($"{tool.Name}: done. {Summarize(arguments)}");
            return result;
        }
        catch (Exception ex)
        {
            if (tool.Kind != AssistantToolKind.Read) AssistantLog.Append($"{tool.Name}: {ex.Message} {Summarize(arguments)}");
            throw;
        }
    }

    // ----- Read tools -----

    private static async Task<object> ListPcsAsync(AssistantCall call)
    {
        var machines = await MachineRelay.ReadFleetAsync();
        return machines.Select(m => new
        {
            name = m.MachineName,
            this_pc = m.IsLocal,
            online = m.IsReachable,
            problem = m.ConnectionProblem.Length > 0 ? m.ConnectionProblem : null,
            version = m.AppVersion,
            cpu_percent = m.CpuPercent,
            memory_percent = m.MemoryPercent,
            memory_gb = Math.Round(m.MemoryTotalGb, 1),
            disks = m.Disks.Select(d => new { drive = d.Name, free_gb = Math.Round(d.FreeGb), total_gb = Math.Round(d.TotalGb) }),
            runners = m.Runners.Length,
            runners_running = m.Runners.Count(r => r.IsRunning),
            runners_busy = m.Runners.Count(r => r.IsBusy),
            can_manage = CanManage(m)
        }).ToList();
    }

    private static async Task<object> ListRunnersAsync(AssistantCall call)
    {
        var machines = await MachineRelay.ReadFleetAsync();
        if (call.Text("pc") is { } pc) machines = new List<MachineInfo> { FindPc(machines, pc) };
        return machines.SelectMany(m => m.Runners.Select(r => DescribeRunner(m, r))).ToList();
    }

    private static async Task<object> FindRunnerAsync(AssistantCall call)
    {
        var target = await ResolveTargetAsync(call.RequiredText("repository"));
        var machines = await MachineRelay.ReadFleetAsync();
        var matches = RunnersFor(machines, target).ToList();
        return new
        {
            target = target.Display,
            has_runner = matches.Count > 0,
            runners = matches.Select(x => DescribeRunner(x.Machine, x.Runner)),
            runs_on = DefaultLabels,
            next_step = matches.Count > 0
                ? "Use runs-on: [self-hosted, Windows, X64] (plus any custom labels the runner has) in the workflow."
                : "No runner yet. Call create_runner to add one."
        };
    }

    private static async Task<object> JobStatusAsync(AssistantCall call)
    {
        var (machine, runner) = await FindRunnerAsync(call.RequiredText("pc"), call.RequiredText("runner"));
        if (!runner.IsBusy) return new { pc = machine.MachineName, runner = runner.TitleText, busy = false, state = StateOf(runner) };

        object? job = null;
        if (!string.IsNullOrEmpty(runner.RepositoryPath) && runner.AgentId != 0)
        {
            var (read, current) = await GitHubApi.GetCurrentJobAsync(runner.RepositoryPath, runner.AgentId);
            if (read && current != null)
                job = new
                {
                    workflow = current.WorkflowName,
                    name = current.Name,
                    run_number = current.RunNumber,
                    url = current.HtmlUrl,
                    started_utc = current.StartedUtc,
                    steps = current.Steps.Select(s =>
                    {
                        var view = LiveStepView.From(s, DateTime.UtcNow);
                        return new { name = s.Name, state = view.State, duration = view.DurationText };
                    })
                };
        }

        RunnerJobActivity? activity = null;
        string? note = null;
        if (machine.IsLocal || machine.AgentProtocolVersion >= 11)
        {
            try { activity = await MachineRelay.RequestJobActivityAsync(machine.MachineKey, runner.CommandKey); }
            catch (Exception ex) { note = $"Could not read the output: {ex.Message}"; }
        }
        else note = $"{machine.MachineName} runs an older Runner Manager, so output and processes are not available.";

        return new
        {
            pc = machine.MachineName,
            runner = runner.TitleText,
            busy = true,
            job,
            output = activity?.Output ?? Array.Empty<string>(),
            processes = activity?.Processes.Select(p => new
            {
                name = p.Name,
                arguments = p.Arguments,
                cpu_percent = p.CpuPercent,
                memory_mb = p.MemoryBytes / (1024 * 1024)
            }),
            note
        };
    }

    private static async Task<object> RecentRunsAsync(AssistantCall call)
    {
        var target = await ResolveTargetAsync(call.RequiredText("repository"));
        if (target.IsOrganization) throw new AssistantException("recent_runs needs a repository (owner/name), not an organization.");
        var runs = await GitHubApi.GetRecentRunsAsync(target.Path)
                   ?? throw new AssistantException($"Could not read the workflow runs of {target.Path} from GitHub.");
        return runs.Take(call.Number("count", 10, 1, 20)).Select(r => new
        {
            workflow = r.WorkflowName,
            title = r.Title,
            branch = r.Branch,
            run_number = r.RunNumber,
            status = r.Status,
            conclusion = r.Conclusion,
            updated_utc = r.UpdatedUtc,
            url = r.HtmlUrl
        }).ToList();
    }

    private static async Task<object> RunnerLogAsync(AssistantCall call)
    {
        var (machine, runner) = await FindRunnerAsync(call.RequiredText("pc"), call.RequiredText("runner"));
        RequireAgent(machine);
        var log = await MachineRelay.RequestRemoteLogAsync(machine.MachineKey, $"runner:{runner.CommandKey}", CancellationToken.None);
        var lines = log.Text.Split('\n');
        return new { pc = machine.MachineName, runner = runner.TitleText, log = log.Title, text = string.Join('\n', lines.TakeLast(call.Number("lines", 150, 20, 400))) };
    }

    // ----- Change tools -----

    private static async Task<object> CreateRunnerAsync(AssistantCall call)
    {
        var target = await ResolveTargetAsync(call.RequiredText("repository"));
        var machines = await MachineRelay.ReadFleetAsync();
        var existing = RunnersFor(machines, target).ToList();
        if (existing.Count > 0 && !call.Flag("even_if_exists"))
            throw new AssistantException($"{target.Display} already has a runner: " +
                                         string.Join(", ", existing.Select(x => $"{x.Runner.TitleText} on {x.Machine.MachineName}")) +
                                         ". Use it with runs-on: [self-hosted, Windows, X64], or pass even_if_exists=true to add another.");

        var machine = call.Text("pc") is { } pc ? FindPc(machines, pc) : PickPc(machines);
        RequireAgent(machine);
        var name = call.Text("name") ?? DefaultRunnerName(target, machine);
        var labels = call.TextList("labels");
        if (RunnerService.RunnerNameProblem(name) is { } nameProblem) throw new AssistantException(nameProblem);
        if (RunnerService.LabelsProblem(labels) is { } labelProblem) throw new AssistantException(labelProblem);

        await call.ConfirmAsync(AssistantToolKind.Change, "Create a runner?",
            $"An AI assistant wants to create the runner “{name}” for {target.Display} on {machine.MachineName}" +
            (labels.Length > 0 ? $", with the labels {string.Join(", ", labels)}." : "."));
        call.Log($"Creating {name} for {target.Display} on {machine.MachineName}. The download takes about 1–3 minutes…");
        await Actions.CreateRunnerAsync(machine, target.Url, name, labels, call.Log);
        return new
        {
            created = true,
            runner = name,
            pc = machine.MachineName,
            target = target.Display,
            runs_on = DefaultLabels.Concat(labels).ToArray(),
            next_step = $"Use runs-on: [{string.Join(", ", DefaultLabels.Concat(labels))}] in the repository's workflows."
        };
    }

    private static async Task<object> ControlRunnerAsync(AssistantCall call)
    {
        var action = call.RequiredText("action").ToLowerInvariant() switch
        {
            "start" => "Start",
            "stop" => "Stop",
            "restart" => "Restart",
            var other => throw new AssistantException($"Unknown action \"{other}\". Use start, stop or restart.")
        };
        var (machine, runner) = await FindRunnerAsync(call.RequiredText("pc"), call.RequiredText("runner"));
        RequireAgent(machine);
        if (!runner.ServiceInstalled) throw new AssistantException($"{runner.TitleText} has no Windows service, so it cannot be controlled.");
        await call.ConfirmAsync(AssistantToolKind.Change, $"{action} a runner?",
            $"An AI assistant wants to {action.ToLowerInvariant()} the runner “{runner.TitleText}” on {machine.MachineName}.");
        await Actions.ControlRunnerAsync(machine, runner, action, call.Log);
        return new { done = true, pc = machine.MachineName, runner = runner.TitleText, action = action.ToLowerInvariant() };
    }

    private static async Task<object> MoveRunnerAsync(AssistantCall call)
    {
        var machines = await MachineRelay.ReadFleetAsync();
        var (source, runner) = FindRunner(machines, call.RequiredText("pc"), call.RequiredText("runner"));
        var target = FindPc(machines, call.RequiredText("to_pc"));
        if (source.MachineKey == target.MachineKey) throw new AssistantException("The runner is already on that PC.");
        RequireAgent(source);
        RequireAgent(target);
        if (runner.IsBusy) throw new AssistantException($"{runner.TitleText} is running a job. Try again when it is idle.");
        var deleteOld = call.Flag("delete_old_files");
        await call.ConfirmAsync(AssistantToolKind.Destructive, "Move a runner?",
            $"An AI assistant wants to move the runner “{runner.TitleText}” from {source.MachineName} to {target.MachineName}" +
            (deleteOld ? ", and delete its folder on the old PC." : "."));
        await Actions.MoveRunnerAsync(source, runner, target, deleteOld, null, call.Log);
        return new { moved = true, runner = runner.TitleText, from = source.MachineName, to = target.MachineName };
    }

    private static async Task<object> RemoveRunnerAsync(AssistantCall call)
    {
        var (machine, runner) = await FindRunnerAsync(call.RequiredText("pc"), call.RequiredText("runner"));
        RequireAgent(machine);
        if (runner.IsBusy) throw new AssistantException($"{runner.TitleText} is running a job. Try again when it is idle.");
        var deleteFiles = call.Flag("delete_files");
        await call.ConfirmAsync(AssistantToolKind.Destructive, "Delete a runner?",
            $"An AI assistant wants to delete the runner “{runner.TitleText}” on {machine.MachineName}: unregister it from GitHub and remove its Windows service" +
            (deleteFiles ? ", and delete its folder." : ". Its folder is kept."));
        await Actions.RemoveRunnerAsync(machine, runner, deleteFiles, call.Log);
        return new { removed = true, pc = machine.MachineName, runner = runner.TitleText };
    }

    // ----- Helpers -----

    /// <summary>A repository or organization the assistant named, under its current name on GitHub.</summary>
    internal sealed record Target(string Path, bool IsOrganization)
    {
        public string Url => $"https://github.com/{Path}";
        public string Display => IsOrganization ? $"the organization {Path}" : Path;
        public string Owner => Path.Split('/')[0];
    }

    /// <summary>"owner/name", "owner" or a GitHub URL → the repository or organization under its current name.</summary>
    internal static Target ParseTarget(string text)
    {
        var value = text.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "github.com/", "https://www.github.com/" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { value = value[prefix.Length..]; break; }
        if (value.StartsWith("orgs/", StringComparison.OrdinalIgnoreCase)) value = value[5..];
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 2 || parts.Any(p => !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new AssistantException($"\"{text}\" is not a GitHub repository (owner/name) or organization.");
        return new Target(string.Join('/', parts), parts.Length == 1);
    }

    private static async Task<Target> ResolveTargetAsync(string text)
    {
        var target = ParseTarget(text);
        if (target.IsOrganization) return target;
        // The current name, so a renamed repository matches its runners and registers correctly.
        var result = await GitHubCli.RunAsync($"api repos/{target.Path} --jq .full_name");
        if (result.ExitCode != 0)
            throw new AssistantException($"Could not find {target.Path} on GitHub. Check the name, and that GitHub is connected in Runner Manager with access to it.");
        return target with { Path = result.Output.Trim() };
    }

    private static IEnumerable<(MachineInfo Machine, MachineRunnerStatus Runner)> RunnersFor(IEnumerable<MachineInfo> machines, Target target)
        => machines.SelectMany(m => m.Runners.Select(r => (Machine: m, Runner: r)))
            .Where(x => target.IsOrganization
                ? x.Runner.Scope.Equals($"orgs/{target.Path}", StringComparison.OrdinalIgnoreCase)
                : x.Runner.RepositoryPath.Equals(target.Path, StringComparison.OrdinalIgnoreCase)
                  || x.Runner.Scope.Equals($"orgs/{target.Owner}", StringComparison.OrdinalIgnoreCase));

    private static object DescribeRunner(MachineInfo machine, MachineRunnerStatus runner) => new
    {
        pc = machine.MachineName,
        name = runner.TitleText,
        target = runner.IsOrgRunner ? $"organization {runner.Scope[5..]}" : runner.RepositoryPath,
        state = StateOf(runner),
        console = runner.IsInteractive,
        runner_version = runner.RunnerVersion
    };

    private static string StateOf(MachineRunnerStatus runner)
        => !runner.ServiceInstalled && !runner.IsInteractive ? "no service" : runner.IsBusy ? "busy" : runner.IsRunning ? "idle" : "stopped";

    private static MachineInfo FindPc(IEnumerable<MachineInfo> machines, string name)
    {
        var list = machines.ToList();
        return list.FirstOrDefault(m => m.MachineName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                        || MachineRelay.Sanitize(m.MachineName) == MachineRelay.Sanitize(name))
               ?? throw new AssistantException($"There is no PC named \"{name}\". PCs: {string.Join(", ", list.Select(m => m.MachineName))}.");
    }

    private static async Task<(MachineInfo Machine, MachineRunnerStatus Runner)> FindRunnerAsync(string pc, string runner)
        => FindRunner(await MachineRelay.ReadFleetAsync(), pc, runner);

    private static (MachineInfo Machine, MachineRunnerStatus Runner) FindRunner(List<MachineInfo> machines, string pc, string name)
    {
        var machine = FindPc(machines, pc);
        var runner = machine.Runners.FirstOrDefault(r => r.TitleText.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                         || r.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return runner == null
            ? throw new AssistantException($"{machine.MachineName} has no runner named \"{name}\". Runners: " +
                                           (machine.Runners.Length == 0 ? "none" : string.Join(", ", machine.Runners.Select(r => r.TitleText))) + ".")
            : (machine, runner);
    }

    /// <summary>
    /// Whether changes can be sent to the PC: this PC needs its fleet agent (an older one also takes commands, for
    /// example right after an update), other PCs need to be online.
    /// </summary>
    private static bool CanManage(MachineInfo machine)
        => machine.IsLocal
            ? FleetConfig.Current.FleetKey != null && (!machine.IsLocalOnly || machine.LocalAgentOutdated)
            : machine.IsOnline && machine.AgentProtocolVersion >= 9;

    private static void RequireAgent(MachineInfo machine)
    {
        if (CanManage(machine)) return;
        throw new AssistantException(machine.IsLocal
            ? "This PC's fleet agent is not running, so Runner Manager cannot make changes for an assistant. Open Runner Manager, which starts it again."
            : $"{machine.MachineName} cannot be reached or runs an older Runner Manager. {machine.ConnectionProblem}".Trim());
    }

    /// <summary>The PC for a new runner when the assistant does not name one: online, then fewest runners, then most free space.</summary>
    internal static MachineInfo PickPc(IEnumerable<MachineInfo> machines)
        => machines.Where(CanManage)
               .OrderBy(m => m.Runners.Length)
               .ThenByDescending(m => m.Disks.FirstOrDefault(d => d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase))?.FreeGb ?? 0)
               .FirstOrDefault()
           ?? throw new AssistantException("No PC can take a new runner right now. Check that the fleet agents are running.");

    internal static string DefaultRunnerName(Target target, MachineInfo machine)
    {
        var raw = $"{target.Path.Split('/').Last()}-{machine.MachineName}".ToLowerInvariant();
        var name = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        return name.Length > 64 ? name[..64].TrimEnd('-') : name;
    }

    private static string Summarize(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : string.Empty;

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, type, description, _) in properties)
        {
            var property = new JsonObject { ["type"] = type, ["description"] = description };
            if (type == "array") property["items"] = new JsonObject { ["type"] = "string" };
            props[name] = property;
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }
}

/// <summary>A record of what assistants changed, shown in Settings → AI assistants.</summary>
public static class AssistantLog
{
    private static readonly string LogPath = Path.Combine(FleetConfig.StateDir, "assistant.log");
    private static readonly object Gate = new();

    public static void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(FleetConfig.StateDir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static IReadOnlyList<string> Recent(int count)
    {
        try { return File.Exists(LogPath) ? File.ReadLines(LogPath).TakeLast(count).Reverse().ToList() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
