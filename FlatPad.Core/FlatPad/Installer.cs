using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Tables;

using static FlatPad.Core.FlatPad.FlatPadSpec;

namespace FlatPad.Core.FlatPad;

/// <summary>
/// Installs and removes Flat Pad, deriving it from the user's own copy of the game.
/// </summary>
/// <remarks>
/// The track is never shipped — everything it writes is derived from Kunos assets, so each user
/// builds it from their own install. Base-game tracks are left byte-identical to what Kunos ships,
/// and the two <c>system\*.table</c> registries are always rebuilt from a <c>.orig</c> snapshot
/// rather than appended to in place, which is what makes re-running unable to stack duplicates.
///
/// Port of <c>install()</c> / <c>uninstall()</c> in <c>tracks/install_flatpad.py</c>.
/// </remarks>
public sealed class Installer(string gameRoot, Action<string> log)
{
    private string Rp(string reference) => RefPath.RealPath(gameRoot, reference);

    private static string SrcRef(string rel) => TrackBuilder.SrcRef(rel);

    // ------------------------------------------------------------------ stock restore

    /// <summary>
    /// Restore any <c>*.orig</c> snapshot an earlier, destructive build left in the donor.
    /// </summary>
    /// <remarks>
    /// Always deriving from stock is the invariant this protects. Nothing in this tool creates
    /// these snapshots — it never writes to a base-game track — so on a clean install this is a
    /// no-op that says so.
    /// </remarks>
    public int RestoreDonorSnapshots(bool verbose = true)
    {
        int restored = 0;
        foreach (string track in SweptForSnapshots)
        {
            string root = Rp($"content/tracks/{track}");
            if (!Directory.Exists(root))
                continue;
            foreach (string snap in Directory.EnumerateFiles(root, "*.orig", SearchOption.AllDirectories)
                         .ToList())
            {
                File.Copy(snap, snap[..^".orig".Length], overwrite: true);
                File.Delete(snap);
                restored++;
            }
        }

        if (verbose)
        {
            log(restored > 0
                ? $"  donor: restored {restored} file(s) to stock"
                : "  donor: already stock");
        }

        return restored;
    }

    /// <summary>Refuse to build from a donor that is still stripped — stock is then unrecoverable.</summary>
    private void AssertStockSource()
    {
        string scene = Rp(SrcRef(SrcSceneName));
        if (!File.Exists(scene))
        {
            throw new InstallException(
                $"{Src} not found at {Rp(SrcDir)} — is the game unpacked?");
        }

        long size = new FileInfo(scene).Length;
        if (size < 10_000_000)
        {
            throw new InstallException(
                $"{SrcSceneName} is only {PyFormat.F1(size / 1e6)} MB — that is a STRIPPED scene, not "
                + "stock (stock is ~85 MB), and no .orig snapshot was found to restore from.\n"
                + "Re-unpack content.kspkg to recover the stock track, then re-run.");
        }
    }

    // ------------------------------------------------------------------ registration

    /// <summary>Path/name rewrites for a registry entry. Whole-filename rules must come first.</summary>
    internal static List<(string Old, string New)> EntryReplacements()
    {
        var pairs = new List<(string, string)>();
        foreach (char sep in (char[])['\\', '/'])
        {
            foreach ((string old, string @new) in (( string, string)[])
                     [(SrcSceneName, $"{Dst}.scene"), (SrcTrackName, $"{Dst}.track")])
            {
                pairs.Add(($"content{sep}tracks{sep}{Src}{sep}{old}",
                    $"content{sep}tracks{sep}{Dst}{sep}{@new}"));
            }

            pairs.Add(($"content{sep}tracks{sep}{Src}", $"content{sep}tracks{sep}{Dst}"));
        }

        pairs.Add((SrcDisplay, DstDisplay));
        return pairs;
    }

    /// <summary>
    /// Parse a registry from its <c>.orig</c> snapshot, creating the snapshot on first run.
    /// Always rebuilding from stock is what makes registration idempotent.
    /// </summary>
    private List<PbNode> LoadTable(string reference)
    {
        string live = Rp(reference);
        string snap = live + ".orig";
        if (!File.Exists(snap))
            File.Copy(live, snap);
        return PbTree.ParseTree(File.ReadAllBytes(snap));
    }

    private void Register()
    {
        List<(string Old, string New)> repl = EntryReplacements();

        // --- tracks.table: the catalog entry
        List<PbNode> tree = LoadTable(TracksTable);
        (_, List<PbNode> entries) = TableEditor.TableEntries(tree);
        PbNode? template = entries.FirstOrDefault(e => TableEditor.TextAt(e, 2, 1) == SrcDisplay);
        if (template is null)
        {
            throw new InstallException(
                $"no '{SrcDisplay}' entry in {TracksTable} — cannot clone a catalog entry");
        }

        TableEditor.AppendTableEntry(tree, template, repl);
        File.WriteAllBytes(Rp(TracksTable), PbTree.EncodeTree(tree));
        log($"  tracks.table: registered '{DstDisplay}'");

        // --- track_containers.table: one entry per session (this is what the menu enumerates)
        tree = LoadTable(ContainersTable);
        (_, entries) = TableEditor.TableEntries(tree);
        var donor = new Dictionary<string, PbNode>(StringComparer.Ordinal);
        foreach (PbNode e in entries)
        {
            if (TableEditor.TextAt(e, 8, 10) == SrcDisplay)
            {
                string? name = TableEditor.TextAt(e, 8, 1);
                if (name is not null)
                    donor[name] = e;
            }
        }

        List<string> missing = Sessions.Where(s => !donor.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            throw new InstallException(
                $"{SrcDisplay} has no session entries named {PyFormat.Repr(missing)} in {ContainersTable}");
        }

        foreach (string session in Sessions)
        {
            TableEditor.AppendTableEntry(tree, donor[session], repl,
                [([8, 8], NewTrackId), ([8, 21], NewTrackIndex)]);
        }

        File.WriteAllBytes(Rp(ContainersTable), PbTree.EncodeTree(tree));
        log($"  track_containers.table: registered {PyFormat.Repr(Sessions)} "
            + $"(id {NewTrackId}, index {NewTrackIndex})");
    }

    private int CopyUiTiles()
    {
        int copied = 0;
        foreach ((string s, string d) in UiTilePairs())
        {
            if (File.Exists(Rp(s)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Rp(d))!);
                File.Copy(Rp(s), Rp(d), overwrite: true);
                copied++;
            }
        }

        log($"  ui: {copied} tile file(s) -> '{UiSlug()}'");
        return copied;
    }

    // ------------------------------------------------------------------ commands

    public void Install()
    {
        log($"Installing '{DstDisplay}' ({Dst}) derived from {Src}:");
        RestoreDonorSnapshots();
        AssertStockSource();

        if (Directory.Exists(Rp(DstDir)))
        {
            Directory.Delete(Rp(DstDir), recursive: true);
            log($"  cleared previous {DstDir}");
        }

        var builder = new TrackBuilder(gameRoot, log);
        (byte[] padBytes, PadBounds bounds) = builder.BuildPadMesh();
        var derived = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SrcRef($"static_mesh/{PadMesh}")] = padBytes,
            [SrcRef(SrcSceneName)] = builder.BuildScene(bounds),
        };
        foreach ((string file, double spread) in SpawnFiles)
        {
            string rel = $"containers/{file}";
            if (File.Exists(Rp(SrcRef(rel))))
                derived[SrcRef(rel)] = builder.BuildSpawns(rel, spread);
        }

        foreach ((string k, byte[] v) in builder.BuildContainers())
            derived[k] = v;

        // Files loaded by folder convention rather than by reference are invisible to the crawl.
        var seeds = new List<string> { SrcRef(SrcSceneName) };
        foreach (string rel in ConventionFiles)
        {
            if (File.Exists(Rp(SrcRef(rel))))
                seeds.Add(SrcRef(rel));
        }

        ClosureResult res = Closure.Crawl(gameRoot, seeds, SrcDir, derived);
        if (res.Missing.Count > 0)
            log($"  ! {res.Missing.Count} referenced file(s) missing in {Src}; skipping them");
        log($"  closure: {res.Owned.Count} owned refs to copy, "
            + $"{res.External.Count} shared refs left in place");

        List<(string Old, string New)> repl = EntryReplacements();
        CloneStats stats = CloneTree.Clone(gameRoot, res.Owned, SrcDir, DstDir,
            overrides: derived,
            extraReplacements: repl[..^1],       // path rules only, not the display name
            rename: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SrcRef(SrcSceneName)] = $"{DstDir}/{Dst}.scene",
                [SrcRef(SrcTrackName)] = $"{DstDir}/{Dst}.track",
            });

        long size = stats.Written.Where(w => File.Exists(Rp(w))).Sum(w => new FileInfo(Rp(w)).Length);
        log($"  copied: {stats.Transformed} repathed + {stats.Copied} verbatim + {stats.Mips} "
            + $"mip payloads = {stats.Written.Count} files, {PyFormat.F1(size / 1e6)} MB");

        Register();
        CopyUiTiles();

        // A separate blank line, not an embedded "\n": the log sink owns the line ending.
        log("");
        log($"Done. Launch the game (unpacked) -> select '{DstDisplay}' -> Practice.");
        log($"{SrcDisplay} is untouched — it was only ever read. Undo with --uninstall.");
    }

    public void Uninstall()
    {
        log($"Removing '{DstDisplay}':");
        RestoreDonorSnapshots();
        if (Directory.Exists(Rp(DstDir)))
        {
            Directory.Delete(Rp(DstDir), recursive: true);
            log($"  removed {DstDir}");
        }

        foreach (string reference in (string[])[TracksTable, ContainersTable])
        {
            string snap = Rp(reference) + ".orig";
            if (File.Exists(snap))
            {
                File.Copy(snap, Rp(reference), overwrite: true);
                File.Delete(snap);
                log($"  restored {reference}");
            }
        }

        int removed = 0;
        foreach ((_, string d) in UiTilePairs())
        {
            if (File.Exists(Rp(d)))
            {
                File.Delete(Rp(d));
                removed++;
            }
        }

        log($"  removed {removed} ui tile file(s)");
        log("");
        log("Done. The game is back to stock content.");
    }
}
