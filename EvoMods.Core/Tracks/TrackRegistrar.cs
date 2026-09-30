using System.Text;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;
using EvoMods.Core.Tables;

namespace EvoMods.Core.Tracks;

/// <summary>A layout to register: what the package ships for it, and what to call it.</summary>
/// <param name="Code">The <c>[8.14]</c> layout name — "GP", "Layout 3A". Prefixes every session name.</param>
/// <param name="GridSize">Cars a session on it can hold: <c>[8.9]</c>.</param>
public sealed record LayoutRegistration(PackageLayout Layout, string Code, int GridSize);

/// <summary>
/// Writes a custom track's rows into the two <c>system\*.table</c> registries.
/// </summary>
/// <remarks>
/// Rows are CLONED from Sebring's, never hand-built — an unfilled field is a crash, and several of
/// these schemas were never fully recovered. What differs from Flat Pad is what happens after the
/// clone. Flat Pad is Sebring with its path swapped, so substring replacement is enough; a custom
/// track shares nothing with Sebring's file names. So every field that names the track is SET
/// outright, and each session's container list <c>[8.11]</c> is rebuilt from the files the package
/// actually ships. A container a row names but the track lacks would be a path to nothing.
/// </remarks>
public static class TrackRegistrar
{
    public const string DonorDisplay = FlatPadSpec.SrcDisplay;

    public const string TimeAttack = "Time Attack";
    public const string Hotstint = "Hotstint";
    public const string NoGameMode = "No Game Mode";

    /// <summary>
    /// Which containers each session kind is handed, in the order Sebring's own rows list them.
    /// </summary>
    /// <remarks>
    /// ⚠️ Every session kind reads a DIFFERENT spawn file — Time Attack (which is what Practice
    /// reads) the pit spawns, Hotstint the hotlap one. Race is left out, as Flat Pad leaves it out:
    /// its row wants <c>starting_positions</c> and a grid, and converted tracks ship neither reliably.
    /// </remarks>
    private static readonly Dictionary<string, string[]> SessionContainers = new()
    {
        [TimeAttack] = ["layout", "timelines", "spawnpoints_pitlane", "pitlane_zones", "marshalls"],
        [Hotstint] = ["layout", "timelines", "spawnpoints_hotlap", "pitlane_zones", "marshalls"],
        [NoGameMode] = ["tv1_cameras", "tv2_cameras"],
    };

    /// <summary>The session names a layout gets, given what its package ships.</summary>
    public static List<string> SessionsFor(LayoutRegistration l) =>
        new[] { TimeAttack, Hotstint, NoGameMode }
            .Where(kind => kind != Hotstint || l.Layout.Containers.ContainsKey("spawnpoints_hotlap"))
            .Select(kind => SessionName(l.Code, kind))
            .ToList();

    public static string SessionName(string code, string kind) => kind == NoGameMode ? NoGameMode : $"{code} {kind}";

    private static string Backslashed(string trackId, string rel) =>
        $@"content\tracks\{trackId}\{rel.Replace('/', '\\')}";

    /// <summary>Is this catalog row a registration of this track folder?</summary>
    public static bool IsCatalogRowFor(PbNode e, string displayName, string trackId) =>
        TableEditor.RawTextAt(e, 2, 1) == displayName
        && string.Equals(RefPath.Canon(TableEditor.RawTextAt(e, 2, 3) ?? ""), $"content/tracks/{trackId}",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Does this session row load containers out of this track folder?</summary>
    public static bool IsSessionRowFor(PbNode e, string displayName, string trackId) =>
        TableEditor.RawTextAt(e, 8, 10) == displayName
        && (TableEditor.Child(e, 8)?.Find(11) ?? []).Any(c => RefPath.Canon(TableEditor.RawText(c) ?? "")
            .StartsWith($"content/tracks/{trackId}/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Register a track, first removing every row <paramref name="isOurs"/> claims.
    /// </summary>
    /// <param name="isOurs">
    /// Which existing rows are this track's previous registration, for the catalog and the session
    /// table respectively. Only those are removed; every other row is left exactly as it was.
    /// </param>
    /// <returns>What was registered, with the numbers allocated, for the ledger.</returns>
    public static List<LedgerLayout> Register(string gameRoot, string trackId, string displayName,
        IReadOnlyList<LayoutRegistration> layouts,
        (Func<PbNode, bool> Catalog, Func<PbNode, bool> Session) isOurs,
        Action<string> log)
    {
        string tracksPath = RefPath.RealPath(gameRoot, TrackRows.TracksTable);
        string containersPath = RefPath.RealPath(gameRoot, TrackRows.ContainersTable);

        // --- tracks.table: one catalog row per track
        List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(tracksPath));
        int removed = TableEditor.RemoveTableEntries(tree, isOurs.Catalog);
        PbNode donor = TableEditor.TableEntries(tree).Entries
                           .FirstOrDefault(e => TableEditor.RawTextAt(e, 2, 1) == DonorDisplay)
                       ?? throw new InstallException($"no '{DonorDisplay}' row in {TrackRows.TracksTable} to clone");

        PbNode catalog = TableEditor.AppendTableEntry(tree, donor, []);
        PbNode fields = TableEditor.Child(catalog, 2)!;
        SetString(fields, 1, displayName);
        SetString(fields, 3, $@"content\tracks\{trackId}");
        SetString(fields, 4, Backslashed(trackId, $"{trackId}.scene"));
        SetString(fields, 8, Backslashed(trackId, $"{trackId}.track"));
        TableEditor.RemoveFields(fields, 2);   // a short code belongs to the donor, not to us
        TableEditor.MarkDirty(catalog, fields);

        byte[] tracksBytes = PbTree.EncodeTree(tree);

        // --- track_containers.table: the rows the menus actually enumerate
        tree = PbTree.ParseTree(File.ReadAllBytes(containersPath));
        List<PbNode> previousRows = TableEditor.TableEntries(tree).Entries.Where(isOurs.Session).ToList();
        int removedSessions = TableEditor.RemoveTableEntries(tree, isOurs.Session);

        var donorSessions = new Dictionary<string, PbNode>(StringComparer.Ordinal);
        foreach (PbNode e in TableEditor.TableEntries(tree).Entries)
        {
            if (TableEditor.RawTextAt(e, 8, 10) == DonorDisplay && TableEditor.RawTextAt(e, 8, 1) is { } name)
                donorSessions[name] = e;
        }

        var result = new List<LedgerLayout>();
        foreach (LayoutRegistration l in layouts)
        {
            // Allocated per layout, AFTER the previous layout's rows went in, so they cannot collide.
            (ulong id, ulong index) = RegistryNumbers.Allocate(TableEditor.TableEntries(tree).Entries,
                previous: RegistryNumbers.Of(previousRows.Where(e => TableEditor.RawTextAt(e, 8, 14) == l.Code)));

            List<string> sessions = SessionsFor(l);
            foreach (string kind in new[] { TimeAttack, Hotstint, NoGameMode })
            {
                string session = SessionName(l.Code, kind);
                if (!sessions.Contains(session))
                    continue;

                string donorName = SessionName(FlatPadSpec.LayoutCode, kind);
                PbNode template = donorSessions.GetValueOrDefault(donorName)
                                  ?? throw new InstallException(
                                      $"{DonorDisplay} has no '{donorName}' row in {TrackRows.ContainersTable} to clone");

                PbNode row = TableEditor.AppendTableEntry(tree, template, [], [([8, 8], id), ([8, 21], index)]);
                PbNode s = TableEditor.Child(row, 8)!;
                SetString(s, 1, session);
                SetString(s, 10, displayName);
                SetString(s, 14, l.Code);
                if (kind == TimeAttack && TableEditor.Child(s, 9) is not null)
                    TableEditor.SetVarint(row, [8, 9], (ulong)l.GridSize);

                // Shared scenery the donor pulls in from outside its own folder (the common camera
                // sequence) is kept; everything from Sebring's own folder is replaced by ours.
                List<string> shared = s.Find(11).Select(TableEditor.RawText).OfType<string>()
                    .Where(c => !RefPath.Canon(c).StartsWith($"content/tracks/{FlatPadSpec.Src}/",
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<string> containers = SessionContainers[kind]
                    .Where(l.Layout.Containers.ContainsKey)
                    .Select(k => Backslashed(trackId, l.Layout.Containers[k]))
                    .Concat(shared)
                    .ToList();
                SetContainers(s, containers);
                TableEditor.MarkDirty(row, s);
            }

            result.Add(new LedgerLayout(l.Layout.Id, l.Code, id, index, sessions));
            log($"  track_containers.table: '{l.Code}' → {PyFormat.Repr(sessions)} (id {id}, index {index})");
        }

        // Both written only once both are built: a failure above leaves the registries untouched.
        File.WriteAllBytes(tracksPath, tracksBytes);
        File.WriteAllBytes(containersPath, PbTree.EncodeTree(tree));
        log($"  tracks.table: registered '{displayName}'"
            + (removed + removedSessions > 0 ? $" (replaced {removed} catalog + {removedSessions} session rows)" : ""));
        return result;
    }

    /// <summary>Remove a track's rows. Returns (catalog, session) counts removed.</summary>
    public static (int Catalog, int Sessions) Unregister(string gameRoot,
        (Func<PbNode, bool> Catalog, Func<PbNode, bool> Session) isOurs)
    {
        int catalog = RemoveRows(gameRoot, TrackRows.TracksTable, isOurs.Catalog);
        int sessions = RemoveRows(gameRoot, TrackRows.ContainersTable, isOurs.Session);
        return (catalog, sessions);
    }

    private static int RemoveRows(string gameRoot, string table, Func<PbNode, bool> isOurs)
    {
        string path = RefPath.RealPath(gameRoot, table);
        List<PbNode> tree = PbTree.ParseTree(File.ReadAllBytes(path));
        int removed = TableEditor.RemoveTableEntries(tree, isOurs);
        if (removed > 0)
            File.WriteAllBytes(path, PbTree.EncodeTree(tree));
        return removed;
    }

    private static void SetString(PbNode message, long number, string value)
    {
        PbNode? node = message.Message?.FirstOrDefault(n => n.Number == number);
        if (node is null)
        {
            TableEditor.InsertField(message, StringNode(number, value));
            return;
        }

        TableEditor.SetText(node, value);
        message.Dirty = true;
    }

    /// <summary>Replace the repeated <c>[8.11]</c> container list, in place of the donor's.</summary>
    private static void SetContainers(PbNode session, List<string> containers)
    {
        TableEditor.RemoveFields(session, 11);
        foreach (string c in containers)
            TableEditor.InsertField(session, StringNode(11, c));
    }

    private static PbNode StringNode(long number, string value) =>
        new(number, WireType.Len) { Text = value, Raw = Encoding.UTF8.GetBytes(value), Dirty = true };
}
