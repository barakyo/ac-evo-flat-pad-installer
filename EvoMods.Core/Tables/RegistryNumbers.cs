using EvoMods.Core.Protobuf;

namespace EvoMods.Core.Tables;

/// <summary>
/// The two numbers a <c>track_containers.table</c> session row needs to be told apart: its
/// <c>[8.8]</c> id and its <c>[8.21]</c> menu index. Both are per (track, layout) — every session row
/// of one layout shares them.
/// </summary>
public static class RegistryNumbers
{
    /// <summary>
    /// Lowest id a track this tool registers will take.
    /// </summary>
    /// <remarks>
    /// The base game's ids topped out at 25947, so starting above 26000 keeps ours clear of that
    /// block. It is only a floor: the actual id is allocated above whatever the live table holds,
    /// so an update growing into our range cannot collide.
    /// </remarks>
    public const ulong NewTrackIdFloor = 26001;

    /// <summary>
    /// Pick registry numbers that are free in THIS install's table, for <paramref name="count"/>
    /// consecutive layouts.
    /// </summary>
    /// <param name="entries">The live table's rows, with the caller's own already stripped.</param>
    /// <param name="previous">
    /// The (id, index) this track held before its rows were stripped, if any. Reused when nothing
    /// else has taken it, so a reinstall leaves the track where it was instead of moving it past
    /// every track installed since — which would also leave a hole in a dense index.
    /// </param>
    /// <remarks>
    /// ⚠️ These were once hardcoded (id 26001, index 37) from the table as it stood in one game
    /// version. <c>[8.21]</c> is a dense global index over (track, layout) pairs, so the next game
    /// update to add a track takes the next number — and v0.8.1 did exactly that, putting Kyalami
    /// on 37 and making Flat Pad displace it in the menus. Allocate above whatever is actually there.
    /// </remarks>
    public static (ulong Id, ulong Index) Allocate(List<PbNode> entries, int count = 1,
        (ulong Id, ulong Index)? previous = null)
    {
        ulong maxId = 0;
        ulong maxIndex = 0;
        var ids = new HashSet<ulong>();
        var indices = new HashSet<ulong>();
        foreach (PbNode e in entries)
        {
            if (TableEditor.Child(e, 8, 8) is { } id)
            {
                maxId = Math.Max(maxId, id.Varint);
                ids.Add(id.Varint);
            }

            if (TableEditor.Child(e, 8, 21) is { } index)
            {
                maxIndex = Math.Max(maxIndex, index.Varint);
                indices.Add(index.Varint);
            }
        }

        if (previous is { } p && p.Id >= NewTrackIdFloor
            && Enumerable.Range(0, count).All(i => !ids.Contains(p.Id + (ulong)i) && !indices.Contains(p.Index + (ulong)i)))
        {
            return p;
        }

        // Leave the ids well clear of the base game's block so an update growing into ours is
        // obvious rather than silent; the index has to be exactly the next one, it is dense.
        return (Math.Max(maxId + 1, NewTrackIdFloor), maxIndex + 1);
    }

    /// <summary>The (id, index) a set of session rows carried, from the first that has both.</summary>
    public static (ulong Id, ulong Index)? Of(IEnumerable<PbNode> rows)
    {
        foreach (PbNode e in rows)
        {
            if (TableEditor.Child(e, 8, 8) is { } id && TableEditor.Child(e, 8, 21) is { } index)
                return (id.Varint, index.Varint);
        }

        return null;
    }
}
