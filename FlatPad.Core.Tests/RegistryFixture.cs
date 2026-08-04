using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Tables;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// Builds the two <c>system\*.table</c> registries the way the game ships them.
/// </summary>
/// <remarks>
/// Only the fields this tool reads are modelled — the display name, the owning track folder, the
/// per-(track, layout) id <c>[8.8]</c> and the DENSE global menu index <c>[8.21]</c>. That is enough
/// for every registry bug that has actually happened, and all of them were about those numbers.
/// </remarks>
public static class RegistryFixture
{
    public const string Donor = "Sebring International Raceway";
    public const string DonorSlug = "sebring";
    public const string UpdateTrack = "Kyalami";
    public const string UpdateSlug = "kyalami";

    public static string Slug(string display) => display.ToLowerInvariant().Replace(' ', '_');

    /// <summary>
    /// A catalog entry: a display name and NOTHING ELSE that identifies the track.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately carries no <c>content\tracks\…</c> path, because the real
    /// <c>tracks.table</c> carries none — checked against the live file, after a fixture that
    /// invented one hid a bug in the integrity check. Which track a catalog entry belongs to can
    /// only be learned from that track's SESSION entries.
    /// </remarks>
    public static byte[] CatalogEntry(string name) =>
        MessageField(3, MessageField(2, StringField(1, name)));

    /// <summary>A session entry, with the repeated <c>[8.11]</c> container paths the real one has.</summary>
    public static byte[] SessionEntry(string session, string track, ulong id, ulong index) =>
        MessageField(3, MessageField(8, Cat(
            StringField(1, session),
            VarintField(8, id),
            StringField(10, track),
            StringField(11, $"content\\tracks\\{Slug(track)}\\containers\\layout_gp.scene"),
            StringField(11, $"content\\tracks\\{Slug(track)}\\containers\\pitlane_zones_gp.scene"),
            StringField(14, "GP"),
            VarintField(21, index))));

    public static byte[] CatalogTable(IEnumerable<byte[]> entries) => MessageField(2, Cat([.. entries]));

    public static byte[] SessionTable(IEnumerable<byte[]> entries) => MessageField(2, Cat([.. entries]));

    /// <summary>The donor's three session entries, all sharing the one per-(track, layout) id.</summary>
    public static List<byte[]> DonorSessions() =>
    [
        SessionEntry("GP Time Attack", Donor, 5954, 36),
        SessionEntry("GP Hotstint", Donor, 5954, 36),
        SessionEntry("No Game Mode", Donor, 5954, 36),
    ];

    /// <summary>What a game update added, at the next free dense index.</summary>
    public static List<byte[]> UpdateSessions() =>
    [
        SessionEntry("GP Time Attack", UpdateTrack, 6001, 37),
        SessionEntry("GP Race", UpdateTrack, 6001, 37),
    ];

    /// <summary>The donor at index 36, plus — optionally — the track a game update added at 37.</summary>
    public static (byte[] Catalog, byte[] Sessions) StockTables(bool withUpdateTrack)
    {
        var catalog = new List<byte[]> { CatalogEntry(Donor) };
        List<byte[]> sessions = DonorSessions();
        if (withUpdateTrack)
        {
            catalog.Add(CatalogEntry(UpdateTrack));
            sessions.AddRange(UpdateSessions());
        }

        return (CatalogTable(catalog), SessionTable(sessions));
    }

    // ------------------------------------------------------------------ reading it back

    public static List<PbNode> ReadTable(string root, string reference) =>
        PbTree.ParseTree(File.ReadAllBytes(RefPath.RealPath(root, reference)));

    public static void Write(string root, string reference, byte[] data)
    {
        string path = RefPath.RealPath(root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    public static List<string?> CatalogNames(string root)
    {
        (_, List<PbNode> e) = TableEditor.TableEntries(ReadTable(root, "system/tracks.table"));
        return e.Select(x => TableEditor.TextAt(x, 2, 1)).ToList();
    }

    /// <summary>Every session entry as (track, session, id, dense menu index).</summary>
    public static List<(string? Track, string? Session, ulong Id, ulong Index)> Sessions(string root)
    {
        (_, List<PbNode> e) = TableEditor.TableEntries(ReadTable(root, "system/track_containers.table"));
        return e.Select(x => (TableEditor.TextAt(x, 8, 10), TableEditor.TextAt(x, 8, 1),
            TableEditor.Child(x, 8, 8)?.Varint ?? 0, TableEditor.Child(x, 8, 21)?.Varint ?? 0)).ToList();
    }
}
