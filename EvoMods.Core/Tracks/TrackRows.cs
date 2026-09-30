using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;
using EvoMods.Core.Tables;

namespace EvoMods.Core.Tracks;

/// <summary>
/// Reads one track's registration back out of the two <c>system\*.table</c> registries.
/// </summary>
/// <remarks>
/// Exists so a registration can be SEEN rather than inferred: dumping a base track shows what a
/// working entry looks like, and dumping a track another tool registered (EvoForge) shows exactly
/// which containers it hands each session — which is the one part of a custom track's rows that
/// cannot be cloned from a donor.
/// </remarks>
public static class TrackRows
{
    public const string TracksTable = "system/tracks.table";
    public const string ContainersTable = "system/track_containers.table";

    /// <summary>A track's catalog row and session rows, matched by display name.</summary>
    public static (List<PbNode> Catalog, List<PbNode> Sessions) Find(string gameRoot, string displayName)
    {
        List<PbNode> Entries(string table) =>
            TableEditor.TableEntries(PbTree.ParseTree(File.ReadAllBytes(RefPath.RealPath(gameRoot, table)))).Entries;

        return (
            Entries(TracksTable).Where(e => TableEditor.RawTextAt(e, 2, 1) == displayName).ToList(),
            Entries(ContainersTable).Where(e => TableEditor.RawTextAt(e, 8, 10) == displayName).ToList());
    }

    /// <summary>Every display name in the catalog, in table order.</summary>
    public static List<string> CatalogNames(string gameRoot) =>
        TableEditor.TableEntries(PbTree.ParseTree(File.ReadAllBytes(RefPath.RealPath(gameRoot, TracksTable))))
            .Entries.Select(e => TableEditor.RawTextAt(e, 2, 1) ?? "?").ToList();

    /// <summary>A readable dump of a track's rows: a summary line per session, then the raw fields.</summary>
    public static List<string> Describe(string gameRoot, string displayName)
    {
        (List<PbNode> catalog, List<PbNode> sessions) = Find(gameRoot, displayName);
        var lines = new List<string>
        {
            $"{TracksTable}: {catalog.Count} row(s) named '{displayName}'",
        };
        foreach (PbNode e in catalog)
            lines.AddRange(PbTree.Dump(PbTree.EncodeTree([e])).Select(l => "  " + l));

        lines.Add("");
        lines.Add($"{ContainersTable}: {sessions.Count} row(s) for '{displayName}'");
        foreach (PbNode e in sessions)
        {
            PbNode? s = TableEditor.Child(e, 8);
            List<string> containers = s?.Find(11).Select(TableEditor.RawText).OfType<string>().ToList() ?? [];
            lines.Add($"  '{TableEditor.RawTextAt(e, 8, 1)}'  id {TableEditor.Child(e, 8, 8)?.Varint}"
                      + $"  index {TableEditor.Child(e, 8, 21)?.Varint}"
                      + $"  layout '{TableEditor.RawTextAt(e, 8, 14)}'");
            foreach (string c in containers)
                lines.Add($"      {c}");
        }

        lines.Add("");
        lines.Add("raw session rows:");
        foreach (PbNode e in sessions)
        {
            lines.AddRange(PbTree.Dump(PbTree.EncodeTree([e])).Select(l => "  " + l));
            lines.Add("");
        }

        return lines;
    }
}
