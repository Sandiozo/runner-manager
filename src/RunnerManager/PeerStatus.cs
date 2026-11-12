using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RunnerManager;

/// <summary>
/// Another PC's status, checked before it is used. The other PC may run an older or an odd Runner Manager, or be
/// hostile: its runners' repositories and organizations become links and GitHub requests here, so a value that is not
/// a GitHub name is blanked, and only so many runners and repositories are taken from one PC. What was wrong is
/// logged once per PC, and the rest of the status still shows.
/// </summary>
internal static class PeerStatus
{
    /// <summary>The most runners taken from one PC's status. Far above real use.</summary>
    internal const int MaxRunners = 200;

    /// <summary>
    /// The most repositories and organizations taken from one PC's status. Far above real use; each one can mean
    /// requests to GitHub (the queue, the runs, the stuck-job checks).
    /// </summary>
    internal const int MaxRepositories = 100;

    /// <summary>The most PCs remembered as logged already. When full, the one logged longest ago is forgotten.</summary>
    internal const int MaxLogged = 200;

    /// <summary>How much of the name a PC sends about itself goes into the log line.</summary>
    private const int MaxLoggedName = 80;

    // The PCs whose problems were logged, by where their status came from (see Checked), and the same oldest first.
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> LoggedOrder = new();

    /// <summary>The status as it may be used. <paramref name="log"/> gets what was wrong, the first time for each PC.</summary>
    /// <param name="from">
    /// Where the status came from, in terms the other PC cannot choose: the address it answered at, or the key ID of
    /// a managed PC's record. Not the name it sends, which it can change at each poll or take from another PC.
    /// </param>
    internal static MachineStatusPayload Checked(MachineStatusPayload payload, string from, Action<string> log)
    {
        var runners = new List<MachineRunnerStatus>();
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int blanked = 0, overRunners = 0, overRepositories = 0;
        foreach (var runner in payload.Runners)
        {
            if (runners.Count >= MaxRunners)
            {
                overRunners++;
                continue;
            }
            var path = runner.RepositoryPath.Length == 0 || GitHubNames.IsRepositoryPath(runner.RepositoryPath) ? runner.RepositoryPath : string.Empty;
            var scope = runner.Scope.Length == 0 || GitHubNames.IsScope(runner.Scope) ? runner.Scope : string.Empty;
            var used = runner;
            if (path != runner.RepositoryPath || scope != runner.Scope)
            {
                blanked++;
                used = WithNames(runner, path, scope);
            }
            var repository = path.Length > 0 ? path : scope;
            if (repository.Length > 0 && !repositories.Contains(repository))
            {
                if (repositories.Count >= MaxRepositories)
                {
                    overRepositories++;
                    continue;
                }
                repositories.Add(repository);
            }
            runners.Add(used);
        }

        var notes = new List<string>();
        if (payload.UnreadableRunners > 0) notes.Add($"{Count(payload.UnreadableRunners, "runner")} could not be read");
        if (blanked > 0) notes.Add($"{Count(blanked, "runner")} had a repository or organization that is not a GitHub name");
        // Entries that could not be read count too: RunnerListConverter stops after MaxRunners + 1 of any kind.
        if (overRunners > 0 || payload.Runners.Length + payload.UnreadableRunners > MaxRunners) notes.Add($"it sent more than {MaxRunners} runners");
        if (overRepositories > 0) notes.Add($"it sent runners for more than {MaxRepositories} repositories and organizations");
        // The name is the other PC's own text: on one line, so it cannot write lines of its own into the log.
        if (notes.Count > 0 && FirstTime(from))
            log($"The status of {AssistantLog.OneLine(payload.MachineName, MaxLoggedName)} was not used in full: {string.Join("; ", notes)}. The rest of it is shown.");
        return blanked == 0 && overRunners == 0 && overRepositories == 0 ? payload : payload with { Runners = runners.ToArray() };
    }

    /// <summary>Whether nothing was logged yet for <paramref name="from"/>; it then counts as logged.</summary>
    private static bool FirstTime(string from)
    {
        lock (Logged)
        {
            if (!Logged.Add(from)) return false;
            LoggedOrder.Enqueue(from);
            if (LoggedOrder.Count > MaxLogged) Logged.Remove(LoggedOrder.Dequeue());
            return true;
        }
    }

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";

    /// <summary>A runner with another repository and scope. Its properties are init-only, so it is copied the way it arrived, as JSON.</summary>
    private static MachineRunnerStatus WithNames(MachineRunnerStatus runner, string path, string scope)
    {
        var copy = JsonSerializer.SerializeToNode(runner)!.AsObject();
        copy[nameof(MachineRunnerStatus.RepositoryPath)] = path;
        copy[nameof(MachineRunnerStatus.Scope)] = scope;
        return copy.Deserialize<MachineRunnerStatus>() ?? new MachineRunnerStatus();
    }

    /// <summary>For tests: logs again for each PC.</summary>
    internal static void ForgetLogged()
    {
        lock (Logged)
        {
            Logged.Clear();
            LoggedOrder.Clear();
        }
    }
}

/// <summary>
/// Reads the runners of a PC's status one by one. An entry that cannot be read is left out (and counted, see
/// <see cref="MachineStatusPayload.UnreadableRunners"/>), so one odd entry from an older or unusual agent does not lose
/// the whole status. A null where a text or a list belongs reads as its default. Writes them as before.
/// Only the first <see cref="PeerStatus.MaxRunners"/> + 1 entries are read: one more than is used, so that
/// <see cref="PeerStatus.Checked"/> sees that there were more. The rest are skipped without being read, so a PC that
/// sends a huge list costs little more than one that sends a short one.
/// </summary>
internal sealed class RunnerListConverter : JsonConverter<MachineRunnerStatus[]>
{
    /// <summary>Stands for an entry that could not be read; <see cref="MachineStatusPayload.Runners"/> leaves it out.</summary>
    internal static readonly MachineRunnerStatus Unreadable = new();

    private static readonly MachineRunnerStatus Defaults = new();

    /// <summary>The runner's texts and lists as they arrive in JSON, by name.</summary>
    private static readonly Dictionary<string, PropertyInfo> TextsAndLists = typeof(MachineRunnerStatus)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod is { IsPublic: true } && p.GetCustomAttribute<JsonIgnoreAttribute>() == null
                    && (p.PropertyType == typeof(string) || p.PropertyType == typeof(string[])))
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public override MachineRunnerStatus[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [Unreadable];
        }
        var runners = new List<MachineRunnerStatus>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (runners.Count > PeerStatus.MaxRunners)
            {
                reader.Skip();
                continue;
            }
            using var item = JsonDocument.ParseValue(ref reader);
            runners.Add(ReadOne(item.RootElement, options) ?? Unreadable);
        }
        return runners.ToArray();
    }

    private static MachineRunnerStatus? ReadOne(JsonElement item, JsonSerializerOptions options)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var runner = JsonNode.Parse(item.GetRawText())!.AsObject();
            foreach (var (name, value) in runner.ToList())
            {
                if (!TextsAndLists.TryGetValue(name, out var property)) continue;
                if (property.PropertyType == typeof(string))
                {
                    if (value == null) runner[name] = (string?)property.GetValue(Defaults) ?? string.Empty;
                }
                else if (value is not JsonArray list)
                    runner[name] = new JsonArray();
                else if (list.Any(v => v?.GetValueKind() != JsonValueKind.String))
                    runner[name] = new JsonArray(list.Where(v => v?.GetValueKind() == JsonValueKind.String)
                        .Select(v => (JsonNode?)JsonValue.Create(v!.GetValue<string>())).ToArray());
            }
            return runner.Deserialize<MachineRunnerStatus>(options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, MachineRunnerStatus[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var runner in value) JsonSerializer.Serialize(writer, runner, options);
        writer.WriteEndArray();
    }
}
