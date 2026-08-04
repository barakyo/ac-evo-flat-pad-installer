using System.Text.RegularExpressions;

using FlatPad.Core.Game;
using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Tables;

using static FlatPad.Core.FlatPad.FlatPadSpec;

namespace FlatPad.Core.FlatPad;

/// <summary>One registry entry the game ships, identified well enough to report and to restore.</summary>
/// <param name="Table">Which registry it belongs to, as a reference path.</param>
/// <param name="Track">Display name, e.g. <c>Kyalami</c>.</param>
/// <param name="Session">Session name for a container entry; null for a catalog entry.</param>
/// <param name="TrackFolder">Canonical <c>content/tracks/…</c> path for the owning track, if known.</param>
/// <param name="Entry">The stock node itself, so a repair can clone it verbatim.</param>
public sealed record StockEntry(string Table, string Track, string? Session, string? TrackFolder,
    PbNode Entry)
{
    public string Describe => Session is null ? $"'{Track}'" : $"'{Track}' / '{Session}'";
}

/// <summary>What the game's own archive says should be registered, against what actually is.</summary>
/// <param name="Source">Archive read, or the reason none was.</param>
/// <param name="Damaged">In stock, missing live, and the track's files are still on disk → a real fault.</param>
/// <param name="Absent">In stock, missing live, and the files are not there either → not this install's content.</param>
public sealed record RegistryDiff(
    bool Available,
    string Source,
    int StockCatalog,
    int StockSessions,
    IReadOnlyList<StockEntry> Damaged,
    IReadOnlyList<StockEntry> Absent)
{
    public static RegistryDiff Skipped(string reason) => new(false, reason, 0, 0, [], []);

    /// <summary>Tracks named in <see cref="Damaged"/>, deduplicated and ordered.</summary>
    public IReadOnlyList<string> DamagedTracks =>
        Damaged.Select(d => d.Track).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Compares the live <c>system\*.table</c> registries against the ones the game ships.
/// </summary>
/// <remarks>
/// This exists because registry damage is SILENT. A v0.8.1 install lost Kyalami's catalog entry
/// outright — written away by a stale snapshot this tool had taken — and nothing anywhere said so:
/// no error, no log line, `verify` passing, the track just absent from every menu. The only way to
/// notice is to hold the live table up against a stock one.
///
/// It reports rather than assumes. A missing stock entry is only called damage when that track's
/// files are still on disk; otherwise the archive simply holds content this install does not have,
/// which is what an archive newer than the unpacked content looks like.
/// </remarks>
public static partial class RegistryIntegrity
{
    /// <summary>Matches the owning track folder inside any string an entry carries.</summary>
    [GeneratedRegex(@"content[\\/]tracks[\\/]([^\\/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackFolderRef { get; }

    public static RegistryDiff Compare(string gameRoot, IStockRegistry stock)
    {
        if (!stock.Available)
            return RegistryDiff.Skipped(stock.Describe);

        byte[]? stockCatalogBytes = stock.Read(TracksTable);
        byte[]? stockSessionBytes = stock.Read(ContainersTable);
        if (stockCatalogBytes is null || stockSessionBytes is null)
        {
            return RegistryDiff.Skipped(
                $"{stock.Describe} holds no {(stockCatalogBytes is null ? TracksTable : ContainersTable)}");
        }

        List<StockEntry> stockCatalog;
        List<StockEntry> stockSessions;
        HashSet<string> liveCatalog;
        HashSet<string> liveSessions;
        try
        {
            List<RawEntry> catalogRaw = CatalogEntries(stockCatalogBytes);
            List<RawEntry> sessionRaw = SessionEntries(stockSessionBytes);

            Dictionary<string, string> folders = TrackFolders(catalogRaw.Concat(sessionRaw));
            stockCatalog = Materialise(catalogRaw, TracksTable, folders);
            stockSessions = Materialise(sessionRaw, ContainersTable, folders);

            liveCatalog = Keys(CatalogEntries(File.ReadAllBytes(RefPath.RealPath(gameRoot, TracksTable))));
            liveSessions = Keys(SessionEntries(File.ReadAllBytes(RefPath.RealPath(gameRoot, ContainersTable))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ProtobufException or InvalidDataException)
        {
            return RegistryDiff.Skipped($"the registries could not be compared ({e.Message})");
        }

        var damaged = new List<StockEntry>();
        var absent = new List<StockEntry>();
        foreach (StockEntry entry in stockCatalog.Concat(stockSessions))
        {
            HashSet<string> live = entry.Session is null ? liveCatalog : liveSessions;
            if (live.Contains(Key(entry)))
                continue;

            // ⚠️ The guard that makes a mismatched archive harmless: an archive newer than the
            // unpacked content lists tracks this install genuinely never had, and their folders are
            // not on disk either. Only a registration missing from underneath content that IS there
            // is a fault someone can act on.
            if (entry.TrackFolder is not null
                && Directory.Exists(RefPath.RealPath(gameRoot, entry.TrackFolder)))
            {
                damaged.Add(entry);
            }
            else
            {
                absent.Add(entry);
            }
        }

        return new RegistryDiff(true, stock.Describe, stockCatalog.Count, stockSessions.Count,
            damaged, absent);
    }

    // ------------------------------------------------------------------ reading the two tables

    /// <summary>An entry and the two strings that identify it, before its folder is known.</summary>
    private readonly record struct RawEntry(PbNode Node, string Track, string? Session);

    /// <summary>Catalog entries, keyed by display name <c>[2.1]</c>.</summary>
    private static List<RawEntry> CatalogEntries(byte[] data) =>
        Entries(data, e => (TableEditor.TextAt(e, 2, 1), null));

    /// <summary>Session entries, keyed by track <c>[8.10]</c> and session name <c>[8.1]</c>.</summary>
    private static List<RawEntry> SessionEntries(byte[] data) =>
        Entries(data, e => (TableEditor.TextAt(e, 8, 10), TableEditor.TextAt(e, 8, 1)));

    private static List<RawEntry> Entries(byte[] data,
        Func<PbNode, (string? Track, string? Session)> identify)
    {
        (_, List<PbNode> entries) = TableEditor.TableEntries(PbTree.ParseTree(data));
        var outEntries = new List<RawEntry>();
        foreach (PbNode e in entries)
        {
            (string? track, string? session) = identify(e);

            // Our own registration is not part of the stock comparison in either direction: the
            // archive never has it, and finding it live is the normal, expected state. Nameless
            // rows — the real catalog carries one — identify nothing, so there is nothing to
            // compare them by either.
            if (string.IsNullOrEmpty(track) || track == DstDisplay)
                continue;
            outEntries.Add(new RawEntry(e, track, session));
        }

        return outEntries;
    }

    private static List<StockEntry> Materialise(List<RawEntry> raw, string table,
        Dictionary<string, string> folders) =>
        raw.Select(r => new StockEntry(table, r.Track, r.Session,
            folders.GetValueOrDefault(r.Track), r.Node)).ToList();

    private static string Key(RawEntry e) => e.Session is null ? e.Track : $"{e.Track} {e.Session}";

    private static string Key(StockEntry e) => e.Session is null ? e.Track : $"{e.Track} {e.Session}";

    private static HashSet<string> Keys(IEnumerable<RawEntry> entries) =>
        entries.Select(Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Each track's <c>content/tracks/&lt;slug&gt;</c> folder, from whichever of its entries names one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Resolved per TRACK, pooled across BOTH tables, because only SESSION entries carry a path —
    /// in the repeated <c>[8.11]</c> container list. Real catalog entries carry no path at all, so
    /// looking one up per entry would leave every catalog entry unattributable and quietly demote
    /// its loss to "not this install's content" — and a deleted CATALOG entry is exactly the damage
    /// that happened. A synthetic fixture that gave catalog entries a path hid this until the real
    /// table was read.
    ///
    /// Found by scanning string leaves rather than by reading a fixed field number. The registry
    /// layout is reverse-engineered and drifts with game versions; a wrong field number here would
    /// silently misclassify real damage, which is the kind of miss this whole check exists to end.
    /// </remarks>
    private static Dictionary<string, string> TrackFolders(IEnumerable<RawEntry> entries)
    {
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (RawEntry e in entries)
        {
            if (folders.ContainsKey(e.Track))
                continue;
            foreach ((_, string text) in PbTree.WalkStrings(e.Node.Raw))
            {
                Match m = TrackFolderRef.Match(text);
                if (m.Success)
                {
                    folders[e.Track] = $"content/tracks/{m.Groups[1].Value}";
                    break;
                }
            }
        }

        return folders;
    }
}
