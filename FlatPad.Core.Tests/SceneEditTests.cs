using FlatPad.Core.FlatPad;
using FlatPad.Core.Protobuf;
using FlatPad.Core.Scene;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// The control-point trap, guarded permanently.
/// </summary>
/// <remarks>
/// A point is <c>{[1] = position, [2] = empty, [3] = scale}</c> and <c>[3]</c> reads (1,1,1) on
/// every stock point. A naive "offset every 3-fixed32 submessage" walk rewrites the SCALE to
/// (100001, 1, 100001). The first cut of the Python did exactly that, and it showed up only as an
/// off-by-2x point count — 45 "points" moved across four polygons with 22 vertices between them.
/// So these tests assert the count AND that the scale is untouched.
/// </remarks>
public class SceneEditTests
{
    private const double PadY = 18.6;

    /// <summary>A named node with a ring of control points and <c>[13]</c> XZ cull bounds.</summary>
    private static PbNode Polygon(string name, double cx, double cz, double radius, int points)
    {
        var pts = new List<byte[]>();
        for (int i = 0; i < points; i++)
        {
            double a = 2 * Math.PI * i / points;
            pts.Add(MessageField(2, Cat(
                Vec3(1, cx + radius * Math.Cos(a), 0.0, cz + radius * Math.Sin(a)),
                MessageField(2, []),
                Vec3(3, 1.0, 1.0, 1.0))));
        }

        byte[] node = MessageField(2, Cat(
            StringField(1, name),
            StringField(3, "Zone"),
            MessageField(13, Cat(
                MessageField(1, Cat(Fixed32Field(1, (float)(cx - radius)), Fixed32Field(2, (float)(cz - radius)))),
                MessageField(2, Cat(Fixed32Field(1, (float)(cx + radius)), Fixed32Field(2, (float)(cz + radius)))))),
            MessageField(50, Cat([.. pts]))));
        return PbTree.ParseTree(node)[0];
    }

    private static byte[] Vec3(int field, double x, double y, double z) =>
        MessageField(field, Cat(Fixed32Field(1, (float)x), Fixed32Field(2, (float)y), Fixed32Field(3, (float)z)));

    /// <summary>Every control point's SCALE, which nothing here may ever write.</summary>
    private static List<Vec3> Scales(PbNode node)
    {
        var outScales = new List<Vec3>();
        Collect(node);
        return outScales;

        void Collect(PbNode n)
        {
            if (n.Message is null)
                return;
            if (SceneNodes.IsControlPoint(n))
            {
                outScales.Add(SceneNodes.ReadVec3(n.First(3)!));
                return;
            }

            foreach (PbNode k in n.Message)
                Collect(k);
        }
    }

    private static (double MinX, double MinZ, double MaxX, double MaxZ) Bounds(PbNode node)
    {
        PbNode t13 = node.First(13)!;
        return (SceneNodes.ReadFloat(t13.First(1)!.Message![0]),
            SceneNodes.ReadFloat(t13.First(1)!.Message![1]),
            SceneNodes.ReadFloat(t13.First(2)!.Message![0]),
            SceneNodes.ReadFloat(t13.First(2)!.Message![1]));
    }

    [Fact]
    public void ReshapeToOval_moves_every_position_and_no_scale()
    {
        PbNode node = Polygon("gp_center_spline", 0, 0, 100, points: 22);

        int moved = SceneEdit.ReshapeToOval(node, -219.0, 164.0, 430.0, 1.0, PadY);

        Assert.Equal(22, moved);                       // count what you touched
        Assert.All(Scales(node), s => Assert.Equal(new Vec3(1, 1, 1), s));
        foreach (Vec3 p in SceneNodes.CollectPoints(node))
        {
            Assert.Equal(430.0, Math.Sqrt(Math.Pow(p.X + 219.0, 2) + Math.Pow(p.Z - 164.0, 2)), 3);
            Assert.Equal((float)PadY, p.Y);
        }
    }

    [Fact]
    public void ReshapeToOval_refits_the_cull_bounds_to_the_new_geometry()
    {
        // Leave [2.13] where the donor had it and the node is culled — for the pad, that means the
        // car falls straight through it.
        PbNode node = Polygon("gp_center_spline", 0, 0, 100, points: 8);

        SceneEdit.ReshapeToOval(node, -219.0, 164.0, 430.0, 1.0, PadY);

        (double minX, double minZ, double maxX, double maxZ) = Bounds(node);
        Assert.Equal(-649.0, minX, 3);
        Assert.Equal(-266.0, minZ, 3);
        Assert.Equal(211.0, maxX, 3);
        Assert.Equal(594.0, maxZ, 3);
    }

    [Fact]
    public void ReshapeToBox_preserves_the_point_count_so_nothing_downstream_sees_a_change()
    {
        // The donor's main pit zone has 4 points, giving a diamond ~2*radius across.
        PbNode node = Polygon("pitlane_zone_main_gp", -318, 155, 267, points: 4);

        int moved = SceneEdit.ReshapeToBox(node, -219.0, 164.0, 30.0, PadY);

        Assert.Equal(4, moved);
        Assert.Equal(4, SceneNodes.CollectPoints(node).Count);
        Assert.All(Scales(node), s => Assert.Equal(new Vec3(1, 1, 1), s));
        (double minX, double minZ, double maxX, double maxZ) = Bounds(node);
        Assert.Equal(60.0, maxX - minX, 3);
        Assert.Equal(60.0, maxZ - minZ, 3);
    }

    [Fact]
    public void OffsetXz_exiles_a_zone_without_touching_its_scale()
    {
        PbNode node = Polygon("pitlane_zone_entry_gp", -400, 170, 80, points: 6);
        List<Vec3> before = SceneNodes.CollectPoints(node);

        int moved = SceneEdit.OffsetXz(node, 100000.0, 100000.0);

        Assert.Equal(6, moved);
        List<Vec3> after = SceneNodes.CollectPoints(node);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].X + 100000.0, after[i].X, 1);
            Assert.Equal(before[i].Y, after[i].Y);
            Assert.Equal(before[i].Z + 100000.0, after[i].Z, 1);
        }

        // The exact failure mode this guards: (1,1,1) becoming (100001, 1, 100001).
        Assert.All(Scales(node), s => Assert.Equal(new Vec3(1, 1, 1), s));
    }

    [Fact]
    public void An_edited_node_survives_a_round_trip_through_the_encoder()
    {
        PbNode node = Polygon("gp_track_limits_left", 0, 0, 100, points: 12);
        SceneEdit.ReshapeToOval(node, -219.0, 164.0, 650.0, 1.0, PadY);

        PbNode reparsed = PbTree.ParseTree(PbTree.EncodeTree([node]))[0];

        Assert.Equal(12, SceneNodes.CollectPoints(reparsed).Count);
        Assert.All(Scales(reparsed), s => Assert.Equal(new Vec3(1, 1, 1), s));
        Assert.Equal(650.0, SceneNodes.CollectPoints(reparsed)
            .Max(p => Math.Sqrt(Math.Pow(p.X + 219.0, 2) + Math.Pow(p.Z - 164.0, 2))), 3);
    }

    [Fact]
    public void LeftIsOutward_is_measured_from_the_donor_not_assumed()
    {
        // Which limit is the outer one depends on the loop's winding, so it is recomputed per donor.
        PbNode centre = Polygon("gp_center_spline", 0, 0, 100, points: 32);
        PbNode outerLeft = Polygon("gp_track_limits_left", 0, 0, 120, points: 32);
        PbNode innerLeft = Polygon("gp_track_limits_left", 0, 0, 80, points: 32);

        Assert.True(SceneEdit.LeftIsOutward([centre, outerLeft], "gp_center_spline", "gp_track_limits_left"));
        Assert.False(SceneEdit.LeftIsOutward([centre, innerLeft], "gp_center_spline", "gp_track_limits_left"));
    }

    [Fact]
    public void LeftIsOutward_defaults_to_left_when_the_donor_has_no_such_splines()
    {
        Assert.True(SceneEdit.LeftIsOutward([], "gp_center_spline", "gp_track_limits_left"));
    }
}
