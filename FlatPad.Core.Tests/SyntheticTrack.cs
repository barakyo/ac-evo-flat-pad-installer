using FlatPad.Core.Refs;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// Builds the smallest game install the verifier will accept, so its checks can be exercised
/// without a 140 GB unpacked game on disk. Each <c>With…</c> method breaks exactly one thing.
/// </summary>
public sealed class SyntheticTrack : IDisposable
{
    private const float PadY = 18.6f;
    private const float SpawnX = -219.0f;
    private const float SpawnZ = 164.0f;
    private const float PadHalf = 750.0f;

    /// <summary>Enough owned refs to clear the verifier's "the track looks incomplete" floor of 100.</summary>
    private const int OwnedMaterials = 120;

    public string Root { get; } = Directory.CreateTempSubdirectory("flatpad-game-").FullName;

    public void Dispose() => Directory.Delete(Root, recursive: true);

    public SyntheticTrack()
    {
        WriteStockDonor();
        WriteReferenceClosure();
        WritePadMesh(PadHalf);
        WriteSpawns(SpawnX, SpawnZ);
        WritePitZone(SpawnX, SpawnZ, radius: 30.0f);
        WriteLayoutSplines();
        WriteTables();
        WriteUiTiles(5);
    }

    // ------------------------------------------------------------------ breakage

    public SyntheticTrack WithSpawnOffThePad()
    {
        WriteSpawns(SpawnX + 5000, SpawnZ);
        return this;
    }

    public SyntheticTrack WithEveryPitZoneExiled()
    {
        WritePitZone(SpawnX + 100000, SpawnZ + 100000, radius: 30.0f);
        return this;
    }

    public SyntheticTrack WithAnOversizedPitZone()
    {
        WritePitZone(SpawnX, SpawnZ, radius: 400.0f);
        return this;
    }

    public SyntheticTrack WithADonorShapedSpline()
    {
        // The donor's real circuit, not the oval: right radius nowhere near what was asked for.
        Write($"content/tracks/flatpad/containers/layout_gp.scene", Cat(
            Spline("gp_track_limits_left", 650.0f),
            Spline("gp_center_spline", 430.0f),
            Spline("gp_ideal_line", 430.0f),
            Spline("gp_track_limits_right", 155.0f)));
        return this;
    }

    public SyntheticTrack WithoutTheTimeAttackSession()
    {
        WriteTables(sessions: ["GP Hotstint", "No Game Mode"]);
        return this;
    }

    public SyntheticTrack WithAStrayOrigSnapshot()
    {
        Write("content/tracks/sebring/containers/layout_gp.scene.orig", "snapshot"u8.ToArray());
        return this;
    }

    // ------------------------------------------------------------------ construction

    /// <summary>The donor, at stock size — anything under 10 MB reads as "still stripped".</summary>
    private void WriteStockDonor()
    {
        string path = RefPath.RealPath(Root, "content/tracks/sebring/sebring.scene");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream fs = File.Create(path);
        fs.SetLength(85_200_000);
    }

    private void WriteReferenceClosure()
    {
        var sceneRefs = new List<byte[]>();
        for (int i = 0; i < OwnedMaterials; i++)
        {
            sceneRefs.Add(StringField(1, $"content\\tracks\\flatpad\\materials\\m{i}.material"));
            Write($"content/tracks/flatpad/materials/m{i}.material", StringField(1, "leaf"));
        }

        // One borrowed ref, left pointing at shared base-game content exactly as the real track does.
        sceneRefs.Add(StringField(2, "content\\tracks\\common_assets\\shared.texture"));
        Write("content/tracks/common_assets/shared.texture", StringField(1, "shared"));
        Write("content/tracks/flatpad/flatpad.scene", Cat([.. sceneRefs]));
    }

    private void WritePadMesh(float half)
    {
        Write("content/tracks/flatpad/static_mesh/concrete_in007.mesh", Cat(
            Vec3Field(6, SpawnX - half, PadY, SpawnZ - half),
            Vec3Field(7, SpawnX + half, PadY, SpawnZ + half)));
    }

    private void WriteSpawns(float x, float z)
    {
        // node [2] -> transform [4] -> translation [1] = vec3
        byte[] spawn = MessageField(2, Cat(
            StringField(1, "spawn_0"),
            MessageField(4, Vec3Field(1, x, PadY + 0.3f, z))));
        Write("content/tracks/flatpad/containers/spawnpoints_pitlane_gp.scene", spawn);
        Write("content/tracks/flatpad/containers/spawnpoints_hotlap_gp.scene", spawn);
        Write("content/tracks/flatpad/containers/spawnpoints_grid_gp.scene", spawn);
    }

    private void WritePitZone(float cx, float cz, float radius)
    {
        Write("content/tracks/flatpad/containers/pitlane_zones_gp.scene",
            Polygon("pitlane_zone_main_gp", cx, cz, radius, points: 4));
    }

    private void WriteLayoutSplines()
    {
        Write("content/tracks/flatpad/containers/layout_gp.scene", Cat(
            Spline("gp_track_limits_left", 650.0f),
            Spline("gp_center_spline", 430.0f),
            Spline("gp_ideal_line", 430.0f),
            Spline("gp_track_limits_right", 150.0f)));
    }

    private static byte[] Spline(string name, float radius) =>
        Polygon(name, SpawnX, SpawnZ, radius, points: 16);

    /// <summary>A named node whose <c>[50]</c> payload holds a ring of control points.</summary>
    private static byte[] Polygon(string name, float cx, float cz, float radius, int points)
    {
        var pts = new List<byte[]>();
        for (int i = 0; i < points; i++)
        {
            double a = 2 * Math.PI * i / points;
            pts.Add(ControlPoint(
                cx + radius * (float)Math.Cos(a), PadY, cz + radius * (float)Math.Sin(a)));
        }

        return MessageField(2, Cat(
            StringField(1, name),
            StringField(3, "Zone"),
            MessageField(50, Cat([.. pts]))));
    }

    /// <summary>
    /// <c>{[1] = position, [2] = empty, [3] = scale}</c>. The scale reads (1,1,1) on every stock
    /// point and must survive any pass that moves the position.
    /// </summary>
    private static byte[] ControlPoint(float x, float y, float z) =>
        MessageField(2, Cat(
            Vec3Field(1, x, y, z),
            MessageField(2, []),
            Vec3Field(3, 1.0f, 1.0f, 1.0f)));

    private static byte[] Vec3Field(int field, float x, float y, float z) =>
        MessageField(field, Cat(Fixed32Field(1, x), Fixed32Field(2, y), Fixed32Field(3, z)));

    private void WriteTables(string[]? sessions = null)
    {
        sessions ??= ["GP Time Attack", "GP Hotstint", "No Game Mode"];

        // tracks.table: entry [3] -> [2] -> {[1] display name}
        Write("system/tracks.table", MessageField(2, Cat(
            MessageField(3, MessageField(2, StringField(1, "Sebring International Raceway"))),
            MessageField(3, MessageField(2, StringField(1, "Flat Pad"))))));

        // track_containers.table: entry [3] -> [8] -> {[1] session, [8] id, [10] track, [21] index}
        var entries = new List<byte[]>();
        foreach (string s in new[] { "GP Time Attack", "GP Hotstint", "GP Race", "No Game Mode" })
            entries.Add(ContainerEntry(s, "Sebring International Raceway", 5954, 36));
        foreach (string s in sessions)
            entries.Add(ContainerEntry(s, "Flat Pad", 26001, 37));
        Write("system/track_containers.table", MessageField(2, Cat([.. entries])));
    }

    private static byte[] ContainerEntry(string session, string track, ulong id, ulong index) =>
        MessageField(3, MessageField(8, Cat(
            StringField(1, session),
            VarintField(8, id),
            StringField(10, track),
            VarintField(21, index))));

    private void WriteUiTiles(int count)
    {
        string[] all =
        [
            "uiresources/images/tracks/flat_pad-gp.texture",
            "uiresources/images/tracks/flat_pad-gp.texturemips",
            "uiresources/images/trackmaps/flat_pad-gp.svg",
            "uiresources/images/trackmaps/flat_pad-gp.texture",
            "uiresources/images/trackmaps/flat_pad-gp.texturemips",
        ];
        foreach (string reference in all.Take(count))
            Write(reference, "tile"u8.ToArray());
    }

    private void Write(string reference, byte[] data)
    {
        string path = RefPath.RealPath(Root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }
}
