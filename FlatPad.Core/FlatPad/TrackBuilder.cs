using System.Buffers.Binary;

using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Scene;
using FlatPad.Core.Tables;

using static FlatPad.Core.FlatPad.FlatPadSpec;

namespace FlatPad.Core.FlatPad;

/// <summary>Raised when the donor is not in a state the recipe can build from.</summary>
public sealed class InstallException(string message) : Exception(message);

/// <summary>The pad's world-space XZ extent, as derived from the flattened mesh.</summary>
public readonly record struct PadBounds(double MinX, double MinZ, double MaxX, double MaxZ);

/// <summary>
/// Derives the Flat Pad's files from the donor track's stock ones, in memory.
/// </summary>
/// <remarks>
/// Nothing here writes to disk: each method returns bytes that become an <c>overrides</c> entry for
/// the closure crawl, so a derived file contributes its references before it exists. The donor is
/// only ever read.
///
/// Port of the <c>build_*</c> functions in <c>tracks/install_flatpad.py</c>.
/// </remarks>
public sealed class TrackBuilder(string gameRoot, Action<string> log)
{
    private byte[] ReadSrc(string rel) => File.ReadAllBytes(RefPath.RealPath(gameRoot, SrcRef(rel)));

    internal static string SrcRef(string rel) => $"{SrcDir}/{rel}";

    /// <summary>
    /// Flatten and enlarge the donor's surface mesh into the pad.
    /// </summary>
    /// <remarks>
    /// AC EVO's physics builder rejects a FOREIGN flat mesh dropped into a track — the car falls
    /// through it even with a correct surface tag, cull bounds and physics flag. The only thing
    /// that reliably collides is one of the track's OWN surface meshes, reshaped. Node transforms
    /// are ignored for static meshes, so the world coordinates have to be baked into the vertex
    /// stream itself.
    /// </remarks>
    public (byte[] Bytes, PadBounds Bounds) BuildPadMesh()
    {
        List<PbNode> t = PbTree.ParseTree(ReadSrc($"static_mesh/{PadMesh}"));
        PbNode mesh = TableEditor.Find(t, 5)[0];
        List<PbNode> m = mesh.Message!;

        PbNode stream = TableEditor.Find(m, 5)[0];      // [5.5] = vec3 position stream
        byte[] buf = (byte[])stream.Raw.Clone();
        int nv = buf.Length / 12;

        Vec3 aMin = SceneNodes.ReadVec3(TableEditor.Find(m, 17)[0]);
        Vec3 aMax = SceneNodes.ReadVec3(TableEditor.Find(m, 18)[0]);
        double cx = ((double)aMin.X + aMax.X) / 2;
        double cz = ((double)aMin.Z + aMax.Z) / 2;
        double sx = PadSizeM / ((double)aMax.X - aMin.X);
        double sz = PadSizeM / ((double)aMax.Z - aMin.Z);

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
        for (int i = 0; i < nv; i++)
        {
            var vertex = buf.AsSpan(i * 12, 12);
            double x = BinaryPrimitives.ReadSingleLittleEndian(vertex);
            double z = BinaryPrimitives.ReadSingleLittleEndian(vertex[8..]);
            double nx = cx + sx * (x - cx);
            double nz = cz + sz * (z - cz);
            BinaryPrimitives.WriteSingleLittleEndian(vertex, (float)nx);
            BinaryPrimitives.WriteSingleLittleEndian(vertex[4..], (float)PadY);
            BinaryPrimitives.WriteSingleLittleEndian(vertex[8..], (float)nz);
            minX = Math.Min(minX, nx);
            maxX = Math.Max(maxX, nx);
            minZ = Math.Min(minZ, nz);
            maxZ = Math.Max(maxZ, nz);
        }

        stream.Raw = buf;
        stream.Dirty = true;

        SceneEdit.SetVec3(TableEditor.Find(m, 17)[0], minX, PadY, minZ);
        SceneEdit.SetVec3(TableEditor.Find(m, 18)[0], maxX, PadY, maxZ);
        SceneEdit.SetVec3(TableEditor.Find(t, 6)[0], minX, PadY, minZ);   // file-level AABB
        SceneEdit.SetVec3(TableEditor.Find(t, 7)[0], maxX, PadY, maxZ);
        mesh.Dirty = true;

        log($"  pad mesh: {PyFormat.F0(maxX - minX)}x{PyFormat.F0(maxZ - minZ)} m at Y={PadY}");
        return (PbTree.EncodeTree(t), new PadBounds(minX, minZ, maxX, maxZ));
    }

    /// <summary>
    /// Keep the pad surface and the structural actors; drop everything decorative.
    /// </summary>
    /// <remarks>
    /// A too-minimal scene CRASHES: the gamemode needs Start Pos, Surface Def, the track-limit /
    /// centre / pitlane splines, cameras, lighting probes and Track Info. So strip in place, never
    /// gut.
    /// </remarks>
    public byte[] BuildScene(PadBounds pad)
    {
        var kept = new List<PbNode>();
        foreach (PbNode n in PbTree.ParseTree(ReadSrc(SrcSceneName)))
        {
            if (n.Number == 2)
            {
                string? ty = SceneNodes.Type(n);
                string nm = SceneNodes.Name(n);
                if (ty is "SMesh" or "ISMesh")
                {
                    string? mr = SceneNodes.MeshRef(n);
                    if (mr is not null && mr.EndsWith(PadMesh, StringComparison.Ordinal))
                    {
                        // the pad — keep it, and refit its cull bounds to where the geometry now is
                        SceneEdit.SetXzBounds(n, pad.MinX, pad.MinZ, pad.MaxX, pad.MaxZ);
                        kept.Add(n);
                    }

                    continue;                                   // drop all other meshes
                }

                if (ty is "Decal" or "Marshal")
                    continue;
                if (ty == "Container" && (nm.StartsWith("barb_1", StringComparison.Ordinal)
                                          || nm.StartsWith("big_screens", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (ty is null && DecorPrefix.Any(p => nm.StartsWith(p, StringComparison.Ordinal)))
                    continue;                                   // campers / cars / spectators / pit shed
            }

            kept.Add(n);
        }

        log("  scene: kept pad + structure, dropped surfaces/visuals/walls/decoration");
        return PbTree.EncodeTree(kept);
    }

    /// <summary>Main-scene decoration to remove (untyped nodes carrying these name prefixes).</summary>
    private static readonly string[] DecorPrefix =
    [
        "pits_interior", "trucks", "camper", "gazebo", "motorhome", "scenic_cars",
        "scissor_lift", "star_tent", "seated_", "walkers",
    ];

    /// <summary>
    /// Relocate every spawn in a container onto the pad.
    /// </summary>
    /// <remarks>
    /// A <paramref name="spread"/> of 0 clusters every spawn on the pad centre (right for the solo
    /// Practice/Hotstint sessions); a non-zero spread lays them out in a staggered two-column grid
    /// so a full race field does not spawn inside itself. Headings are left alone — the pad is
    /// featureless and 750 m deep in every direction, so any facing works.
    /// </remarks>
    public byte[] BuildSpawns(string rel, double spread)
    {
        List<PbNode> nodes = PbTree.ParseTree(ReadSrc(rel));
        int moved = 0;
        foreach (PbNode n in nodes)
        {
            if (n.Number != 2 || n.Message is null)
                continue;
            PbNode? transform = n.First(4);
            if (transform?.Message is null)
                continue;
            PbNode? translation = transform.First(1);
            if (translation?.Message is null || translation.Message.Count < 3)
                continue;

            int row = Math.DivRem(moved, 2, out int col);
            double x = SpawnX + (col - 0.5) * spread;
            double z = SpawnZ - row * spread * 1.5;
            WriteFloat(translation.First(1)!, x);
            WriteFloat(translation.First(2)!, PadY + 0.3);
            WriteFloat(translation.First(3)!, z);
            n.Dirty = true;
            transform.Dirty = true;
            translation.Dirty = true;
            moved++;
        }

        string where = spread == 0
            ? $"pad centre {PyFormat.ReprTuple(SpawnX, SpawnZ)}"
            : $"a {PyFormat.G(spread)} m grid at {PyFormat.ReprTuple(SpawnX, SpawnZ)}";
        log($"  {Path.GetFileName(rel)}: {moved} spawn(s) -> {where}");
        return PbTree.EncodeTree(nodes);
    }

    private static void WriteFloat(PbNode n, double value)
    {
        byte[] raw = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(raw, (float)value);
        n.Raw = raw;
    }

    /// <summary>
    /// The layout container: keep the splines (mandatory), drop the poly_direction visuals, and
    /// reshape the splines into an oval centred on the spawn.
    /// </summary>
    public byte[] BuildLayout()
    {
        List<PbNode> nodes = PbTree.ParseTree(ReadSrc($"containers/layout_{Layout}.scene"))
            .Where(n => !(n.Number == 2
                          && SceneNodes.Name(n).StartsWith("poly_direction", StringComparison.Ordinal)))
            .ToList();
        if (!ReshapeLayout)
            return PbTree.EncodeTree(nodes);

        string outward = SceneEdit.LeftIsOutward(nodes, $"{Layout}_center_spline",
            $"{Layout}_track_limits_left") ? "left" : "right";

        var done = new List<string>();
        foreach (PbNode n in nodes)
        {
            if (n.Number != 2 || n.Message is null)
                continue;
            string name = SceneNodes.Name(n);
            double? r = OvalRadius(name);
            if (r is null)
                continue;
            int points = SceneEdit.ReshapeToOval(n, SpawnX, SpawnZ, r.Value, OvalAspect, PadY);
            done.Add($"{name}@{PyFormat.F0(r.Value)}m x{points}");
        }

        log($"  layout: reshaped to an oval centred on the spawn ({outward} limit is the outer edge)");
        foreach (string d in done)
            log($"    {d}");
        if (done.Count != OvalRadii.Length)
        {
            IEnumerable<string> missing = OvalRadii.Select(o => o.Name)
                .Except(done.Select(d => d.Split('@')[0])).Order(StringComparer.Ordinal);
            throw new InstallException($"layout splines not found in the donor: {PyFormat.Repr(missing)}");
        }

        return PbTree.EncodeTree(nodes);
    }

    /// <summary>Shrink the pit zone that holds the spawn; move the rest off the pad.</summary>
    /// <remarks>
    /// ⚠️ Exactly one zone must remain. None, and session start never fires "entered to pitlane",
    /// so the pitlane page never opens and the load hangs on a dead screen. Several or large, and
    /// driving across the pad teleports you to the pits mid-test.
    /// </remarks>
    public byte[] BuildPitZones()
    {
        List<PbNode> nodes = PbTree.ParseTree(ReadSrc($"containers/pitlane_zones_{Layout}.scene"));
        int kept = 0;
        int exiled = 0;
        foreach (PbNode n in nodes)
        {
            if (n.Number != 2 || n.Message is null)
                continue;
            if (SceneNodes.Name(n) == PitZoneKeep)
            {
                int k = SceneEdit.ReshapeToBox(n, SpawnX, SpawnZ, PitBoxRadiusM, PadY);
                log($"  pit zone '{PitZoneKeep}': reshaped to a {k}-point box of radius "
                    + $"{PyFormat.G(PitBoxRadiusM)} m at the spawn (session start needs one here)");
                kept++;
            }
            else
            {
                SceneEdit.OffsetXz(n, PitZoneExileX, PitZoneExileZ);
                exiled++;
            }
        }

        if (kept == 0)
        {
            throw new InstallException(
                $"no '{PitZoneKeep}' zone in the donor's pitlane_zones — session start would hang");
        }

        log($"  pit zones: {kept} kept at the spawn, {exiled} moved off the pad");
        return PbTree.EncodeTree(nodes);
    }

    /// <summary>Every derived container scene, keyed by its DONOR ref.</summary>
    public Dictionary<string, byte[]> BuildContainers()
    {
        var outFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // additional.scene -> header only (was 43 Decal/decoration nodes: crowd, RVs)
        string additional = "containers/additional.scene";
        outFiles[SrcRef(additional)] = PbTree.EncodeTree(
            PbTree.ParseTree(ReadSrc(additional)).Where(n => n.Number == 1).ToList());

        outFiles[SrcRef($"containers/layout_{Layout}.scene")] = BuildLayout();

        // marshals -> drop the Marshal nodes, keep the lights
        string marshals = $"containers/marshalls_{Layout}.scene";
        outFiles[SrcRef(marshals)] = PbTree.EncodeTree(PbTree.ParseTree(ReadSrc(marshals))
            .Where(n => !(n.Number == 2 && SceneNodes.Type(n) == "Marshal")).ToList());

        log("  containers: additional emptied, layout splines kept, marshals removed");
        outFiles[SrcRef($"containers/pitlane_zones_{Layout}.scene")] = BuildPitZones();
        return outFiles;
    }
}
