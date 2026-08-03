using System.Buffers.Binary;

using FlatPad.Core.Protobuf;

namespace FlatPad.Core.Scene;

/// <summary>Moving and reshaping the geometry a track scene is made of.</summary>
/// <remarks>
/// Every write goes through <see cref="SetVec3"/> or <see cref="SetXzBounds"/> so the dirty flags
/// that drive re-encoding are never forgotten. Arithmetic is in double throughout, narrowing to
/// float only at the point of packing — see the note on <c>FlatPadSpec</c>'s constants.
/// </remarks>
public static class SceneEdit
{
    private static void Pack(PbNode n, double value)
    {
        byte[] raw = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(raw, (float)value);
        n.Raw = raw;
    }

    /// <summary>Write a <c>{[1]=x, [2]=y, [3]=z}</c> submessage and mark it for re-encoding.</summary>
    public static void SetVec3(PbNode node, double x, double y, double z)
    {
        Pack(node.First(1)!, x);
        Pack(node.First(2)!, y);
        Pack(node.First(3)!, z);
        foreach (int k in (int[])[1, 2, 3])
            node.First(k)!.Dirty = true;
        node.Dirty = true;
    }

    /// <summary>
    /// Set a node's <c>[2.13]</c> XZ cull bounds = (minX,minZ),(maxX,maxZ).
    /// </summary>
    /// <remarks>
    /// ⚠️ These must track the geometry. Leave them where the donor had them after moving a mesh
    /// and the node is culled — which, for the pad, means the car falls straight through it.
    /// </remarks>
    public static void SetXzBounds(PbNode node, double minX, double minZ, double maxX, double maxZ)
    {
        PbNode t13 = node.First(13)!;
        PbNode lo = t13.First(1)!;
        PbNode hi = t13.First(2)!;
        // Positional, not a vec3: each corner is a 2-element (x, z) pair.
        Pack(lo.Message![0], minX);
        Pack(lo.Message![1], minZ);
        Pack(hi.Message![0], maxX);
        Pack(hi.Message![1], maxZ);
        foreach (PbNode sub in (PbNode[])[lo, hi])
        {
            foreach (PbNode c in sub.Message!)
                c.Dirty = true;
            sub.Dirty = true;
        }

        t13.Dirty = true;
        node.Dirty = true;
    }

    private static List<PbNode> PointNodes(PbNode node)
    {
        var outNodes = new List<PbNode>();
        SceneNodes.WalkControlPoints(node, outNodes.Add);
        return outNodes;
    }

    /// <summary>Translate a zone/spline in XZ: its control points AND its cull bounds.</summary>
    /// <returns>How many points moved — count it and check the number.</returns>
    public static int OffsetXz(PbNode node, double dx, double dz)
    {
        int moved = 0;
        bool any = SceneNodes.WalkControlPoints(node, p =>
        {
            Vec3 v = SceneNodes.ReadVec3(p);
            SetVec3(p, v.X + dx, v.Y, v.Z + dz);
            moved++;
        });
        if (any)
            node.Dirty = true;

        foreach (PbNode sub in node.Message ?? [])
        {
            if (sub.Number == 13 && sub.Message is not null)
            {
                PbNode lo = sub.First(1)!;
                PbNode hi = sub.First(2)!;
                double[] v = lo.Message!.Concat(hi.Message!).Select(c => (double)SceneNodes.ReadFloat(c))
                    .ToArray();
                SetXzBounds(node, v[0] + dx, v[1] + dz, v[2] + dx, v[3] + dz);
            }
        }

        return moved;
    }

    /// <summary>
    /// Rewrite a zone's polygon as a regular N-gon of <paramref name="radius"/> around (cx, cz).
    /// </summary>
    /// <remarks>
    /// The point COUNT is preserved — the donor's main zone has 4, giving a diamond ~2·radius
    /// across — so nothing downstream sees a structural change. Only coordinates move.
    /// </remarks>
    public static int ReshapeToBox(PbNode node, double cx, double cz, double radius, double padY)
    {
        List<PbNode> pts = PointNodes(node);
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            SetVec3(pts[i], cx + radius * Math.Cos(a), padY, cz + radius * Math.Sin(a));
        }

        if (n > 0)
            node.Dirty = true;
        SetXzBounds(node, cx - radius, cz - radius, cx + radius, cz + radius);
        return n;
    }

    /// <summary>Rewrite a spline as an oval of <paramref name="radius"/> around (cx, cz).</summary>
    /// <remarks>
    /// Point count and ordering are preserved, so the direction of travel and every downstream
    /// structure stay exactly as the donor had them — only the coordinates change.
    /// </remarks>
    public static int ReshapeToOval(PbNode node, double cx, double cz, double radius, double aspect,
        double padY)
    {
        List<PbNode> pts = PointNodes(node);
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            SetVec3(pts[i], cx + radius * aspect * Math.Cos(a), padY, cz + radius * Math.Sin(a));
        }

        if (n > 0)
        {
            node.Dirty = true;
            SetXzBounds(node, cx - radius * aspect, cz - radius, cx + radius * aspect, cz + radius);
        }

        return n;
    }

    /// <summary>
    /// Does the donor put <c>track_limits_left</c> on the OUTSIDE of its loop?
    /// </summary>
    /// <remarks>
    /// Measured from the donor rather than assumed — which limit is the outer one depends on the
    /// loop's winding. Take the centre spline's first two points for the direction of travel, then
    /// see which side the left limit lies on (2-D cross product in XZ). For a loop generated with
    /// increasing angle the tangent is (-sin, cos) and its left-hand side is the outward radial, so
    /// a negative cross here means left == outer.
    /// </remarks>
    public static bool LeftIsOutward(List<PbNode> nodes, string centreName, string leftName)
    {
        var byName = new Dictionary<string, PbNode>(StringComparer.Ordinal);
        foreach (PbNode n in nodes)
        {
            if (n.Number == 2 && n.Message is not null)
                byName[SceneNodes.Name(n)] = n;
        }

        if (!byName.TryGetValue(centreName, out PbNode? centre) ||
            !byName.TryGetValue(leftName, out PbNode? left))
        {
            return true;
        }

        List<Vec3> cp = SceneNodes.CollectPoints(centre);
        List<Vec3> lp = SceneNodes.CollectPoints(left);
        if (cp.Count < 2 || lp.Count == 0)
            return true;

        double tx = (double)cp[1].X - cp[0].X;
        double tz = (double)cp[1].Z - cp[0].Z;
        Vec3 near = lp.MinBy(p => ((double)p.X - cp[0].X) * ((double)p.X - cp[0].X)
                                  + ((double)p.Z - cp[0].Z) * ((double)p.Z - cp[0].Z));
        double vx = (double)near.X - cp[0].X;
        double vz = (double)near.Z - cp[0].Z;
        return tx * vz - tz * vx < 0;
    }
}
