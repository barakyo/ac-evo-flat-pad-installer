using FlatPad.Core.FlatPad;
using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Scene;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>The derived files, built against a synthetic donor.</summary>
public class TrackBuilderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-donor-").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private TrackBuilder Builder => new(_root, _log.Add);

    private void WriteDonor(string rel, byte[] data)
    {
        string path = RefPath.RealPath(_root, $"content/tracks/sebring/{rel}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static byte[] Vec3(int field, double x, double y, double z) =>
        MessageField(field, Cat(Fixed32Field(1, (float)x), Fixed32Field(2, (float)y), Fixed32Field(3, (float)z)));

    /// <summary>A spawn: node [2] -> transform [4] -> translation [1] = vec3.</summary>
    private static byte[] Spawn(int i) =>
        MessageField(2, Cat(StringField(1, $"grid_{i}"), MessageField(4, Vec3(1, 1000, 5, 2000))));

    private static List<Vec3> SpawnPositions(byte[] scene) =>
        PbTree.ParseTree(scene)
            .Where(n => n.Number == 2 && n.Message is not null)
            .Select(n => n.First(4)?.First(1))
            .OfType<PbNode>()
            .Select(SceneNodes.ReadVec3)
            .ToList();

    [Fact]
    public void Spawns_with_no_spread_all_land_on_the_pad_centre()
    {
        // Right for the solo Practice/Hotstint sessions: one car, put it in the middle.
        WriteDonor("containers/spawnpoints_pitlane_gp.scene", Cat(Spawn(0), Spawn(1), Spawn(2)));

        byte[] outBytes = Builder.BuildSpawns("containers/spawnpoints_pitlane_gp.scene", 0.0);

        Assert.All(SpawnPositions(outBytes), p =>
        {
            Assert.Equal(-219.0f, p.X);
            Assert.Equal(164.0f, p.Z);
            Assert.Equal(18.9f, p.Y, 5);        // PAD_Y + 0.3, computed in double
        });
        Assert.Contains("3 spawn(s) -> pad centre (-219.0, 164.0)", _log[0]);
    }

    [Fact]
    public void A_race_grid_is_staggered_into_two_columns_so_the_field_does_not_spawn_inside_itself()
    {
        WriteDonor("containers/spawnpoints_grid_gp.scene",
            Cat([.. Enumerable.Range(0, 5).Select(Spawn)]));

        byte[] outBytes = Builder.BuildSpawns("containers/spawnpoints_grid_gp.scene", 8.0);

        List<Vec3> p = SpawnPositions(outBytes);
        Assert.Equal([-223.0f, -215.0f, -223.0f, -215.0f, -223.0f], p.Select(v => v.X));
        Assert.Equal([164.0f, 164.0f, 152.0f, 152.0f, 140.0f], p.Select(v => v.Z));
        Assert.Contains("5 spawn(s) -> a 8 m grid at (-219.0, 164.0)", _log[0]);
    }

    [Fact]
    public void A_node_without_a_transform_is_left_alone_rather_than_crashing()
    {
        WriteDonor("containers/spawnpoints_hotlap_gp.scene",
            Cat(MessageField(2, StringField(1, "not_a_spawn")), Spawn(0)));

        byte[] outBytes = Builder.BuildSpawns("containers/spawnpoints_hotlap_gp.scene", 0.0);

        Assert.Contains("1 spawn(s)", _log[0]);
        Assert.Single(SpawnPositions(outBytes));
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Building_pit_zones_fails_loudly_when_the_donor_has_no_zone_to_keep()
    {
        // With no zone at the spawn, session start never fires "entered to pitlane" and the track
        // loads to a dead screen. Better to refuse than to ship that.
        WriteDonor("containers/pitlane_zones_gp.scene", Zone("sc_2", 0, 0));

        var ex = Assert.Throws<InstallException>(() => Builder.BuildPitZones());

        Assert.Contains("session start would hang", ex.Message);
    }

    [Fact]
    public void Pit_zones_keep_exactly_one_and_exile_the_rest()
    {
        WriteDonor("containers/pitlane_zones_gp.scene", Cat(
            Zone("pitlane_zone_main_gp", -318, 155),
            Zone("pitlane_zone_entry_gp", -472, 155),
            Zone("pitlane_zone_exit_gp", 215, 133),
            Zone("sc_2", 318, 110)));

        byte[] outBytes = Builder.BuildPitZones();

        var onPad = PbTree.ParseTree(outBytes)
            .Where(n => n.Number == 2 && n.Message is not null)
            .Where(n => SceneNodes.CollectPoints(n).Any(p => Math.Abs(p.X) < 1000 && Math.Abs(p.Z) < 1000))
            .Select(SceneNodes.Name)
            .ToList();
        Assert.Equal(["pitlane_zone_main_gp"], onPad);
        Assert.Contains("1 kept at the spawn, 3 moved off the pad", _log[^1]);
    }

    [Fact]
    public void Building_the_layout_fails_loudly_when_a_spline_is_missing_from_the_donor()
    {
        WriteDonor("containers/layout_gp.scene", Cat(
            Zone("gp_track_limits_left", 0, 0),
            Zone("gp_center_spline", 0, 0),
            Zone("gp_ideal_line", 0, 0)));

        var ex = Assert.Throws<InstallException>(() => Builder.BuildLayout());

        Assert.Contains("gp_track_limits_right", ex.Message);
    }

    [Fact]
    public void The_layout_becomes_concentric_ovals_centred_on_the_spawn()
    {
        // The geometry is the mitigation for wrong-way penalties: every radial line out of the
        // spawn is perpendicular to the loop, so driving away reads as neither forward nor reverse.
        WriteDonor("containers/layout_gp.scene", Cat(
            Zone("gp_track_limits_left", 0, 0),
            Zone("gp_center_spline", 0, 0),
            Zone("gp_ideal_line", 0, 0),
            Zone("gp_track_limits_right", 0, 0),
            Zone("poly_direction_1", 0, 0)));

        byte[] outBytes = Builder.BuildLayout();

        var byName = PbTree.ParseTree(outBytes)
            .Where(n => n.Number == 2 && n.Message is not null)
            .ToDictionary(SceneNodes.Name, n => SceneNodes.CollectPoints(n));
        Assert.DoesNotContain("poly_direction_1", byName.Keys);   // direction visuals dropped
        foreach ((string name, double want) in
                 (( string, double)[])[("gp_track_limits_left", 650), ("gp_center_spline", 430),
                     ("gp_ideal_line", 430), ("gp_track_limits_right", 150)])
        {
            Assert.All(byName[name], p => Assert.Equal(want,
                Math.Sqrt(Math.Pow(p.X + 219.0, 2) + Math.Pow(p.Z - 164.0, 2)), 2));
        }
    }

    /// <summary>A named zone node with a 4-point polygon and <c>[13]</c> bounds.</summary>
    private static byte[] Zone(string name, double cx, double cz)
    {
        var pts = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            double a = 2 * Math.PI * i / 4;
            pts.Add(MessageField(2, Cat(
                Vec3(1, cx + 50 * Math.Cos(a), 0, cz + 50 * Math.Sin(a)),
                MessageField(2, []),
                Vec3(3, 1, 1, 1))));
        }

        return MessageField(2, Cat(
            StringField(1, name),
            StringField(3, "Zone"),
            MessageField(13, Cat(
                MessageField(1, Cat(Fixed32Field(1, (float)(cx - 50)), Fixed32Field(2, (float)(cz - 50)))),
                MessageField(2, Cat(Fixed32Field(1, (float)(cx + 50)), Fixed32Field(2, (float)(cz + 50)))))),
            MessageField(50, Cat([.. pts]))));
    }
}
