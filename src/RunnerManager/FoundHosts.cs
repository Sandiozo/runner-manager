using System.Collections.ObjectModel;

namespace RunnerManager;

/// <summary>
/// The PCs that the join window lists. A search runs every few seconds and its answers come by UDP, where one can get
/// lost. So the list changes only where a search differs from it, and a PC stays through a few searches without its
/// answer: a row keeps its keyboard focus, a click on it is not lost, and the list does not flicker.
/// </summary>
public sealed class FoundHosts
{
    /// <summary>Searches in a row that a listed PC may miss before it leaves the list.</summary>
    internal const int MissesAllowed = 2;

    private readonly Dictionary<string, int> _missed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list, in name order. Only the rows that differ are changed.</summary>
    public ObservableCollection<Pairing.Host> Items { get; } = new();

    /// <summary>Takes in the answers of one search.</summary>
    public void Update(IEnumerable<Pairing.Host> found)
    {
        var answers = new Dictionary<string, Pairing.Host>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in found) answers.TryAdd(host.Name, host);

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var listed = Items[i];
            if (answers.Remove(listed.Name, out var answer))
            {
                _missed.Remove(listed.Name);
                // Another address, or its version after an update.
                if (answer != listed) Items[i] = answer;
            }
            else if (_missed.GetValueOrDefault(listed.Name) >= MissesAllowed)
            {
                _missed.Remove(listed.Name);
                Items.RemoveAt(i);
            }
            else _missed[listed.Name] = _missed.GetValueOrDefault(listed.Name) + 1;
        }

        foreach (var added in answers.Values)
        {
            var index = 0;
            while (index < Items.Count && string.Compare(Items[index].Name, added.Name, StringComparison.OrdinalIgnoreCase) < 0) index++;
            Items.Insert(index, added);
        }
    }

    public void Clear()
    {
        _missed.Clear();
        Items.Clear();
    }
}
