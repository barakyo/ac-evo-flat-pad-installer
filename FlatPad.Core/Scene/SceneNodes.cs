using System.Buffers.Binary;

using FlatPad.Core.Protobuf;

namespace FlatPad.Core.Scene;

/// <summary>A position in AC EVO's world axes: X lateral, Y up, Z forward.</summary>
public readonly record struct Vec3(float X, float Y, float Z);

/// <summary>
/// The handful of shapes a track <c>.scene</c> is made of: named nodes, vec3s, and the control
/// points that make up zone polygons and layout splines.
/// </summary>
public static class SceneNodes
{
    /// <summary>A scene node's name (<c>[2.1]</c>), or empty.</summary>
    public static string Name(PbNode n)
    {
        if (n.Message is null)
            return "";
        PbNode? nm = n.First(1);
        return string.IsNullOrEmpty(nm?.Text) ? "" : nm.Text;
    }

    /// <summary>A scene node's type (<c>[2.3]</c>) — "SMesh", "Zone", "Marshal", … — or null.</summary>
    public static string? Type(PbNode n)
    {
        PbNode? t = n.Message is null ? null : n.First(3);
        return string.IsNullOrEmpty(t?.Text) ? null : t.Text;
    }

    /// <summary>The first <c>.mesh</c> reference anywhere inside a node.</summary>
    public static string? MeshRef(PbNode n)
    {
        foreach ((_, string t) in PbTree.WalkStrings(n.Raw))
        {
            if (t.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                return t;
        }

        return null;
    }

    public static float ReadFloat(PbNode n) => BinaryPrimitives.ReadSingleLittleEndian(n.Raw);

    /// <summary>Read a <c>{[1]=x, [2]=y, [3]=z}</c> submessage.</summary>
    public static Vec3 ReadVec3(PbNode n) => new(
        ReadFloat(n.First(1)!),
        ReadFloat(n.First(2)!),
        ReadFloat(n.First(3)!));

    /// <summary>Exactly fields [1][2][3], all fixed32 — the shape of every coordinate triple.</summary>
    public static bool IsVec3(PbNode n)
    {
        if (n.Message is null || n.Message.Count != 3)
            return false;
        return n.Message[0].Number == 1 && n.Message[1].Number == 2 && n.Message[2].Number == 3
               && n.Message.All(k => k.Wire == WireType.I32);
    }

    /// <summary>
    /// A zone/spline control point: <c>{[1] = position vec3, [2] = empty, [3] = scale vec3}</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only <c>[1]</c> is a POSITION. <c>[3]</c> reads (1,1,1) on every stock point — it is a
    /// SCALE, so a naive "walk every vec3" pass silently rewrites it to (100001, 1, 100001). Match
    /// the control-point SHAPE and touch only <c>[1]</c>. The tell, the first time this went wrong,
    /// was a point count of 45 across four polygons that have 22 vertices between them: count what
    /// you touched and check the number.
    /// </remarks>
    public static bool IsControlPoint(PbNode n)
    {
        if (n.Message is null)
            return false;
        PbNode? one = null;
        PbNode? three = null;
        foreach (PbNode k in n.Message)
        {
            if (k.Number == 1)
                one = k;
            else if (k.Number == 3)
                three = k;
        }

        return one is not null && three is not null && IsVec3(one) && IsVec3(three);
    }

    /// <summary>
    /// Call <paramref name="fn"/> with the POSITION node of every control point under
    /// <paramref name="node"/>'s <c>[2.50]</c> payload. Returns whether any were found.
    /// </summary>
    public static bool WalkControlPoints(PbNode node, Action<PbNode> fn)
    {
        bool hit = false;
        foreach (PbNode sub in node.Message ?? [])
        {
            if (sub.Number == 50 && Walk(sub, fn))
                hit = true;
        }

        return hit;
    }

    private static bool Walk(PbNode n, Action<PbNode> fn)
    {
        if (n.Message is null)
            return false;
        if (IsControlPoint(n))
        {
            fn(n.First(1)!);
            return true;
        }

        bool found = false;
        foreach (PbNode k in n.Message)
        {
            if (Walk(k, fn))
                found = true;
        }

        return found;
    }

    /// <summary>Every control-point position under a node, in order.</summary>
    public static List<Vec3> CollectPoints(PbNode node)
    {
        var outPoints = new List<Vec3>();
        WalkControlPoints(node, p => outPoints.Add(ReadVec3(p)));
        return outPoints;
    }
}
