using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Scene;
using FlatPad.Core.Tables;

using static FlatPad.Core.FlatPad.FlatPadSpec;

namespace FlatPad.Core.FlatPad;

/// <summary>
/// Checks an installed Flat Pad, without launching the game.
/// </summary>
/// <remarks>
/// Every failure mode here cost a game launch to find the first time. A spawn left at the donor's
/// coordinates drops the car into the abyss; no pit zone at the spawn hangs session start on a dead
/// loading screen; a missing session KIND makes the track silently absent from that part of the UI.
/// All of it is decidable from the files on disk.
///
/// Port of <c>verify()</c> in <c>tracks/install_flatpad.py</c>.
/// </remarks>
public sealed class Verifier(string gameRoot)
{
    private string Rp(string reference) => RefPath.RealPath(gameRoot, reference);

    private byte[] Read(string reference) => File.ReadAllBytes(Rp(reference));

    private static string SrcRef(string rel) => $"{SrcDir}/{rel}";

    public VerifyReport Run()
    {
        var report = new VerifyReport();
        report.Line($"Verifying '{DstDisplay}':");

        CheckBaseTracksAreStock(report);
        CheckTrackReferences(report);

        // The geometry checks all measure against the pad's own AABB, so they run only if the pad
        // mesh made it across.
        (Vec3? padLo, Vec3? padHi) = ReadPadBounds();
        if (padLo is not null && padHi is not null)
        {
            CheckSpawns(report, padLo.Value, padHi.Value);
            CheckPitZones(report, padLo.Value, padHi.Value);
            CheckLayoutSplines(report, padLo.Value, padHi.Value);
        }

        CheckRegistration(report);
        CheckUiTiles(report);
        return report;
    }

    // ------------------------------------------------------------------ 1. base tracks untouched

    private void CheckBaseTracksAreStock(VerifyReport report)
    {
        var strays = new List<string>();
        foreach (string track in BaseTracks)
        {
            string root = Rp($"content/tracks/{track}");
            if (!Directory.Exists(root))
                continue;
            strays.AddRange(Directory.EnumerateFiles(root, "*.orig", SearchOption.AllDirectories));
        }

        report.Line($"  base tracks ({string.Join(", ", BaseTracks)}): {strays.Count} leftover .orig snapshot(s)");
        if (strays.Count > 0)
            report.Problem($"{strays.Count} .orig snapshots remain — a base track is still modified");

        string seb = Rp(SrcRef(SrcSceneName));
        double sebMb = File.Exists(seb) ? new FileInfo(seb).Length / 1e6 : 0;
        report.Line($"  {SrcSceneName}: {PyFormat.F1(sebMb)} MB (stock is ~85 MB)");
        if (sebMb < 10)
            report.Problem($"{SrcSceneName} is still stripped");
    }

    // ------------------------------------------------------------------ 2. the track's refs resolve

    private void CheckTrackReferences(VerifyReport report)
    {
        if (!Directory.Exists(Rp(DstDir)))
        {
            report.Problem($"{DstDir} does not exist");
            report.Line($"  {DstDir}: MISSING");
            return;
        }

        var seeds = new List<string> { $"{DstDir}/{Dst}.scene" };
        foreach (string conventionFile in ConventionFiles)
        {
            string rel = conventionFile.Replace(SrcTrackName, $"{Dst}.track");
            if (File.Exists(Rp($"{DstDir}/{rel}")))
                seeds.Add($"{DstDir}/{rel}");
        }

        ClosureResult res = Closure.Crawl(gameRoot, seeds, DstDir);
        List<string> leaks = res.External
            .Where(r => r.StartsWith(SrcDir + "/", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        List<string> unresolved = res.External
            .Where(r => !File.Exists(Rp(r)) && !Directory.Exists(Rp(r)))
            .Order(StringComparer.Ordinal).ToList();

        report.Line($"  {DstDir}: {res.Owned.Count} own refs checked, {res.External.Count} shared refs checked");
        report.Line($"    missing own refs: {res.Missing.Count} | unresolved shared refs: {unresolved.Count} | "
                    + $"refs leaking back into {Src}: {leaks.Count}");

        if (res.Owned.Count < 100)
            report.Problem($"only {res.Owned.Count} own refs found — the track looks incomplete");
        foreach (string r in res.Missing)
            report.Problem($"missing: {r}");
        foreach (string r in unresolved)
            report.Problem($"unresolved shared ref: {r}");
        foreach (string r in leaks.Take(10))
            report.Problem($"leaks back into {Src}: {r}");
    }

    /// <summary>The pad's world-space AABB, from the mesh's file-level <c>[6]</c>/<c>[7]</c> corners.</summary>
    private (Vec3?, Vec3?) ReadPadBounds()
    {
        string pad = Rp($"{DstDir}/static_mesh/{PadMesh}");
        if (!File.Exists(pad))
            return (null, null);
        List<PbNode> t = PbTree.ParseTree(File.ReadAllBytes(pad));
        return (SceneNodes.ReadVec3(TableEditor.Find(t, 6)[0]),
            SceneNodes.ReadVec3(TableEditor.Find(t, 7)[0]));
    }

    // ------------------------------------------------------------------ 3a. spawns land on the pad

    private void CheckSpawns(VerifyReport report, Vec3 lo, Vec3 hi)
    {
        int checked_ = 0;
        int off = 0;
        foreach ((string file, _) in SpawnFiles)
        {
            string path = Rp($"{DstDir}/containers/{file}");
            if (!File.Exists(path))
                continue;
            foreach (PbNode n in PbTree.ParseTree(File.ReadAllBytes(path)))
            {
                if (n.Number != 2 || n.Message is null)
                    continue;
                PbNode? transform = n.First(4);
                PbNode? translation = transform?.Message is null ? null : transform.First(1);
                if (translation?.Message is null || translation.Message.Count < 3)
                    continue;

                Vec3 p = SceneNodes.ReadVec3(translation);
                checked_++;
                if (p.X < lo.X || p.X > hi.X || p.Z < lo.Z || p.Z > hi.Z || Math.Abs(p.Y - PadY) >= 5)
                {
                    off++;
                    if (off <= 3)
                    {
                        report.Problem($"spawn in {file} at ({PyFormat.F0(p.X)},{PyFormat.F0(p.Y)},"
                                       + $"{PyFormat.F0(p.Z)}) is off the pad");
                    }
                }
            }
        }

        report.Line($"  spawns: {checked_} checked against the pad AABB "
                    + $"[{PyFormat.F0(lo.X)}..{PyFormat.F0(hi.X)} x {PyFormat.F0(lo.Z)}..{PyFormat.F0(hi.Z)}], "
                    + $"{off} off the pad");
        if (checked_ == 0)
            report.Problem("no spawn points found to check");
    }

    // ------------------------------------------------------------------ 3b. exactly one pit zone

    private void CheckPitZones(VerifyReport report, Vec3 lo, Vec3 hi)
    {
        string zpath = Rp($"{DstDir}/containers/pitlane_zones_{Layout}.scene");
        var zones = new List<(string Name, float X0, float X1, float Z0, float Z1)>();
        if (File.Exists(zpath))
        {
            foreach (PbNode n in PbTree.ParseTree(File.ReadAllBytes(zpath)))
            {
                if (n.Number != 2 || n.Message is null)
                    continue;
                List<Vec3> pts = SceneNodes.CollectPoints(n);
                if (pts.Any(p => p.X >= lo.X && p.X <= hi.X && p.Z >= lo.Z && p.Z <= hi.Z))
                {
                    zones.Add((SceneNodes.Name(n), pts.Min(p => p.X), pts.Max(p => p.X),
                        pts.Min(p => p.Z), pts.Max(p => p.Z)));
                }
            }
        }

        report.Line($"  pit zones on the pad: {zones.Count} (want 1) {PyFormat.Repr(zones.Select(z => z.Name))}");
        if (zones.Count != 1)
        {
            report.Problem("exactly one pitlane zone must sit on the pad — none and session start hangs "
                           + "waiting for 'entered to pitlane'; more than one ambushes the test area "
                           + $"(got {zones.Count})");
        }

        foreach ((string name, float x0, float x1, float z0, float z1) in zones)
        {
            float span = Math.Max(x1 - x0, z1 - z0);
            bool holds = x0 <= SpawnX && SpawnX <= x1 && z0 <= SpawnZ && SpawnZ <= z1;
            report.Line($"    '{name}' spans {PyFormat.F0(x1 - x0)} x {PyFormat.F0(z1 - z0)} m, "
                        + $"contains the spawn: {PyFormat.Bool(holds)}");
            if (!holds)
            {
                report.Problem($"pit zone '{name}' does not contain the spawn "
                               + $"{PyFormat.ReprTuple(SpawnX, SpawnZ)} — session start will hang on "
                               + "the loading screen");
            }

            if (span > 4 * PitBoxRadiusM)
            {
                report.Problem($"pit zone '{name}' is {PyFormat.F0(span)} m across — big enough to drive "
                               + "into by accident and be teleported to the pits");
            }
        }
    }

    // ------------------------------------------------------------------ 3c. the oval layout

    private void CheckLayoutSplines(VerifyReport report, Vec3 lo, Vec3 hi)
    {
        string lpath = Rp($"{DstDir}/containers/layout_{Layout}.scene");
        if (!ReshapeLayout || !File.Exists(lpath))
            return;

        int checked_ = 0;
        int bad = 0;
        foreach (PbNode n in PbTree.ParseTree(File.ReadAllBytes(lpath)))
        {
            if (n.Number != 2 || n.Message is null)
                continue;
            string name = SceneNodes.Name(n);
            float? want = OvalRadius(name);
            if (want is null)
                continue;

            List<Vec3> pts = SceneNodes.CollectPoints(n);
            checked_++;
            int outside = pts.Count(p => !(p.X >= lo.X && p.X <= hi.X && p.Z >= lo.Z && p.Z <= hi.Z));
            List<double> radii = pts
                .Select(p => Math.Sqrt(((double)p.X - SpawnX) * ((double)p.X - SpawnX)
                                       + ((double)p.Z - SpawnZ) * ((double)p.Z - SpawnZ)))
                .ToList();

            report.Line($"    layout '{name}': {pts.Count} pts, "
                        + $"r={PyFormat.F0(radii.Min())}..{PyFormat.F0(radii.Max())} m "
                        + $"(want {PyFormat.F0(want.Value)}), {outside} off the pad");
            if (outside > 0)
            {
                bad++;
                report.Problem($"layout spline '{name}' has {outside} point(s) off the pad — the "
                               + "drivable loop would run into the abyss");
            }

            if (Math.Abs(radii.Max() - want.Value) > 1.0 || Math.Abs(radii.Min() - want.Value * OvalAspect) > 1.0)
                report.Problem($"layout spline '{name}' is not the requested oval");
        }

        report.Line($"  layout splines: {checked_} reshaped (want {OvalRadii.Length}), {bad} off the pad");
        if (checked_ != OvalRadii.Length)
            report.Problem($"only {checked_} of {OvalRadii.Length} layout splines were reshaped");
    }

    // ------------------------------------------------------------------ 4. registration

    private void CheckRegistration(VerifyReport report)
    {
        List<PbNode> tree = PbTree.ParseTree(Read(TracksTable));
        (_, List<PbNode> catalogEntries) = TableEditor.TableEntries(tree);
        int nCat = catalogEntries.Count(e => TableEditor.TextAt(e, 2, 1) == DstDisplay);

        tree = PbTree.ParseTree(Read(ContainersTable));
        (_, List<PbNode> entries) = TableEditor.TableEntries(tree);
        List<PbNode> ours = entries.Where(e => TableEditor.TextAt(e, 8, 10) == DstDisplay).ToList();
        var ids = ours.Select(e => TableEditor.Child(e, 8, 8)).Where(c => c is not null)
            .Select(c => c!.Varint).ToHashSet();
        var clashes = entries
            .Where(e => TableEditor.TextAt(e, 8, 10) != DstDisplay && TableEditor.Child(e, 8, 8) is not null)
            .Select(e => TableEditor.Child(e, 8, 8)!.Varint)
            .Where(ids.Contains).ToHashSet();

        report.Line($"  tracks.table: {nCat} '{DstDisplay}' catalog entr(y/ies) (want 1)");
        report.Line($"  track_containers.table: {ours.Count} session entr(y/ies) "
                    + $"{PyFormat.Repr(ours.Select(e => TableEditor.TextAt(e, 8, 1)))} (want {Sessions.Length}), "
                    + $"ids {PyFormat.Repr(ids.Order())}");
        if (nCat != 1)
            report.Problem($"tracks.table has {nCat} '{DstDisplay}' entries, expected 1");
        if (ours.Count != Sessions.Length)
            report.Problem($"track_containers.table has {ours.Count} entries, expected {Sessions.Length}");
        if (clashes.Count > 0)
            report.Problem($"track id clash with a base track: {PyFormat.Repr(clashes.Order())}");

        CheckSessionKinds(report, entries, ours);
    }

    /// <summary>
    /// A track missing a session KIND that every base track has is simply invisible in that part of
    /// the UI — with no error anywhere. Practice, for instance, enumerates the "Time Attack" entries.
    /// </summary>
    private static void CheckSessionKinds(VerifyReport report, List<PbNode> entries, List<PbNode> ours)
    {
        var baseKinds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (PbNode e in entries)
        {
            string? track = TableEditor.TextAt(e, 8, 10);
            string? name = TableEditor.TextAt(e, 8, 1);
            if (!string.IsNullOrEmpty(track) && track != DstDisplay && !string.IsNullOrEmpty(name))
            {
                if (!baseKinds.TryGetValue(Kind(name), out HashSet<string>? tracks))
                    baseKinds[Kind(name)] = tracks = new HashSet<string>(StringComparer.Ordinal);
                tracks.Add(track);
            }
        }

        int nBase = entries.Select(e => TableEditor.TextAt(e, 8, 10))
            .Where(t => !string.IsNullOrEmpty(t) && t != DstDisplay)
            .ToHashSet(StringComparer.Ordinal).Count;
        List<string> universal = baseKinds.Where(kv => kv.Value.Count >= nBase * 0.9)
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        var have = ours.Select(e => Kind(TableEditor.TextAt(e, 8, 1) ?? ""))
            .ToHashSet(StringComparer.Ordinal);

        report.Line($"  session kinds: have {PyFormat.Repr(have.Order(StringComparer.Ordinal))} | "
                    + $"required {PyFormat.Repr(RequiredSessionKinds)} | "
                    + $"on >=90% of the {nBase} base tracks {PyFormat.Repr(universal)}");
        foreach (string k in RequiredSessionKinds)
        {
            if (!have.Contains(k))
            {
                report.Problem($"no '{k}' session entry — every base track has one, so the track will "
                               + "not be listed wherever the UI enumerates that kind (Practice reads "
                               + "'Time Attack')");
            }
        }
    }

    /// <summary>
    /// Session names are "&lt;layout code&gt; &lt;kind&gt;" ("GP Time Attack", "Layout 3A Hotstint") — the
    /// kind is a SUFFIX and can contain spaces, so match it, don't split on the last word.
    /// </summary>
    private static string Kind(string name)
    {
        foreach (string k in SessionKinds)
        {
            if (name == k || name.EndsWith(" " + k, StringComparison.Ordinal))
                return k;
        }

        return name;    // Academy events and other one-offs
    }

    // ------------------------------------------------------------------ 5. ui tiles

    private void CheckUiTiles(VerifyReport report)
    {
        List<(string Src, string Dst)> pairs = UiTilePairs();
        int have = pairs.Count(p => File.Exists(Rp(p.Dst)));
        report.Line($"  ui tiles: {have}/{pairs.Count} present for slug '{UiSlug()}'");
    }
}
