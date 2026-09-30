using System.Text.Json;
using System.Text.Json.Serialization;

namespace EvoMods.Core.Tracks;

/// <summary>One registered layout of an installed track.</summary>
/// <param name="Id">The file suffix, <c>layout_&lt;Id&gt;.scene</c>.</param>
/// <param name="Code">The <c>[8.14]</c> layout name the menus show, and the prefix of every session name.</param>
public sealed record LedgerLayout(string Id, string Code, ulong RegistryId, ulong MenuIndex, List<string> Sessions);

/// <summary>What was installed for one track, and exactly which registry rows are ours.</summary>
public sealed record LedgerTrack(
    string TrackId,
    string DisplayName,
    string? Version,
    string Source,
    DateTimeOffset InstalledAt,
    List<LedgerLayout> Layouts,
    Dictionary<string, string> Files)
{
    public IEnumerable<ulong> RegistryIds => Layouts.Select(l => l.RegistryId);
}

/// <summary>
/// The record of which custom tracks this tool installed, kept beside the registries it describes.
/// </summary>
/// <remarks>
/// ⚠️ This is what makes removal safe. Flat Pad recognises its rows by display name, which is fine
/// for a name only this tool uses and wrong for a third-party track: another tool (EvoForge) may
/// already have registered the very same name, and deleting "every row called HDC Drift Park"
/// would delete its registration too. A row is ours only when its name AND its <c>[8.8]</c> id
/// are both recorded here.
///
/// Lives in <c>&lt;game&gt;\evomods\tracks.json</c> rather than under Saved Games because it describes
/// the game folder's own tables and files: reinstalling the game discards both together, and a
/// game folder copied elsewhere carries its record with it.
/// </remarks>
public sealed class TrackLedger
{
    public const string RelativePath = "evomods/tracks.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<LedgerTrack> Tracks { get; init; } = [];

    public static string PathFor(string gameRoot) =>
        Path.Combine(gameRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static TrackLedger Load(string gameRoot)
    {
        string path = PathFor(gameRoot);
        if (!File.Exists(path))
            return new TrackLedger();

        // ⚠️ An unreadable ledger is not an empty one. Treating it as empty would let an install go
        // ahead, find "foreign" rows under our own names, and refuse or — worse, with --replace —
        // strip them by name. Stop and say so instead.
        try
        {
            return JsonSerializer.Deserialize<TrackLedger>(File.ReadAllText(path), Json) ?? new TrackLedger();
        }
        catch (JsonException e)
        {
            throw new FlatPad.InstallException($"{path} cannot be read ({e.Message}) — fix or remove it by hand");
        }
    }

    public void Save(string gameRoot)
    {
        string path = PathFor(gameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public LedgerTrack? Find(string trackId) =>
        Tracks.FirstOrDefault(t => string.Equals(t.TrackId, trackId, StringComparison.OrdinalIgnoreCase));

    public void Put(LedgerTrack track)
    {
        Tracks.RemoveAll(t => string.Equals(t.TrackId, track.TrackId, StringComparison.OrdinalIgnoreCase));
        Tracks.Add(track);
    }

    public bool Remove(string trackId) =>
        Tracks.RemoveAll(t => string.Equals(t.TrackId, trackId, StringComparison.OrdinalIgnoreCase)) > 0;
}
