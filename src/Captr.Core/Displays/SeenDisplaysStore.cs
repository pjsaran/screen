using System.Text.Json;

using Captr.Core.Common;

namespace Captr.Core.Displays;

/// <summary>
/// Remembers every display stable id this machine has ever shown, so a genuinely NEW
/// display can be told apart from a familiar one coming back (SPEC §5: a newly
/// attached display is recorded by default AND surfaced in the UI). Internal state,
/// not a setting — it lives beside other machine-local state, not in settings.json.
/// Losing this file is harmless: every display simply looks "new" once.
/// </summary>
public sealed class SeenDisplaysStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Production store in local application data.</summary>
    public SeenDisplaysStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captr", "seen-displays.json"))
    {
    }

    /// <summary>Test seam: explicit file path.</summary>
    public SeenDisplaysStore(string path) => _path = path;

    /// <summary>All stable ids ever recorded. Empty on first run or after a
    /// malformed file (which is simply discarded — see class remarks).</summary>
    public IReadOnlyCollection<string> Load()
    {
        string? json = AtomicFile.ReadOrNull(_path);
        if (json is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds any unseen ids from the current topology and persists.</summary>
    public void MarkSeen(IEnumerable<DisplayInfo> attached)
    {
        var all = new SortedSet<string>(Load(), StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (DisplayInfo display in attached)
        {
            changed |= all.Add(display.StableId);
        }

        if (changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.Write(_path, JsonSerializer.Serialize(all, SerializerOptions));
        }
    }
}
