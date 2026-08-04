using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;

namespace FlatPad.Core.Tables;

/// <summary>
/// Reads and edits the <c>system\*.table</c> registries — the catalogs the game menus enumerate.
/// </summary>
/// <remarks>
/// A table is one <c>[2]</c> container holding repeated <c>[2.3]</c> entries. Registering a track
/// means deep-cloning an existing entry, rewriting its strings, and overriding its numeric ids —
/// never hand-building one, because an unfilled field is a crash.
/// </remarks>
public static class TableEditor
{
    /// <summary>Children of a node list with the given field number.</summary>
    public static List<PbNode> Find(IEnumerable<PbNode> nodes, long number) =>
        nodes.Where(n => n.Number == number).ToList();

    /// <summary>Descend a node by field numbers. Null if any hop is missing.</summary>
    public static PbNode? Child(PbNode? node, params long[] path)
    {
        PbNode? cur = node;
        foreach (long number in path)
        {
            if (cur?.Message is null)
                return null;
            PbNode? hit = cur.Message.FirstOrDefault(n => n.Number == number);
            if (hit is null)
                return null;
            cur = hit;
        }

        return cur;
    }

    public static string? TextAt(PbNode? node, params long[] path) => Child(node, path)?.Text;

    /// <summary>Deep-copy a subtree — round-tripped through the encoder, so lossless by construction.</summary>
    public static PbNode CloneNode(PbNode node) =>
        PbTree.ParseTree(PbTree.EncodeTree([node]))[0];

    /// <summary>Set a nested varint, marking the ancestor chain dirty so it re-encodes.</summary>
    public static void SetVarint(PbNode entry, long[] path, ulong value)
    {
        var chain = new List<PbNode> { entry };
        PbNode cur = entry;
        foreach (long number in path)
        {
            PbNode? hit = cur.Message?.FirstOrDefault(n => n.Number == number);
            if (hit is null)
            {
                throw new KeyNotFoundException(
                    $"no field {number} under [{string.Join(", ", chain.Select(n => n.Number))}]");
            }

            cur = hit;
            chain.Add(cur);
        }

        cur.Varint = value;
        foreach (PbNode n in chain[..^1])
            n.Dirty = true;
    }

    /// <summary>The <c>[2]</c> container of a *.table file and its repeated <c>[2.3]</c> entries.</summary>
    public static (PbNode Root, List<PbNode> Entries) TableEntries(List<PbNode> tree)
    {
        List<PbNode> root = Find(tree, 2);
        if (root.Count == 0)
            throw new InvalidDataException("not a *.table file: no [2] container");
        return (root[0], Find(root[0].Message ?? [], 3));
    }

    /// <summary>Append a clone of <paramref name="template"/> (a <c>[2.3]</c> entry) to a table.</summary>
    /// <param name="replacements">Rewrites applied to the clone's string leaves.</param>
    /// <param name="varints">Field paths RELATIVE TO THE ENTRY NODE, mapped to new values.</param>
    public static PbNode AppendTableEntry(List<PbNode> tree, PbNode template,
        List<(string Old, string New)> replacements,
        IEnumerable<(long[] Path, ulong Value)>? varints = null)
    {
        (PbNode root, _) = TableEntries(tree);
        if (root.Message is null)
            throw new InvalidDataException("the [2] container holds no entries to append to");

        PbNode entry = CloneNode(template);
        PbTree.TransformText([entry], s => CloneTree.Replace(s, replacements));
        foreach ((long[] path, ulong value) in varints ?? [])
            SetVarint(entry, path, value);

        root.Message.Add(entry);
        root.Dirty = true;
        entry.Dirty = true;
        return entry;
    }

    /// <summary>Append an entry node verbatim, keeping every field it already carries.</summary>
    /// <remarks>
    /// Used to put back a stock entry a broken tool removed. Unlike
    /// <see cref="AppendTableEntry"/> nothing is rewritten — the whole point is that the restored
    /// entry keeps the game's own id and dense menu index, which is what makes it the entry the
    /// update shipped rather than a lookalike.
    /// </remarks>
    public static PbNode AppendEntry(List<PbNode> tree, PbNode entry)
    {
        (PbNode root, _) = TableEntries(tree);
        if (root.Message is null)
            throw new InvalidDataException("the [2] container holds no entries to append to");

        PbNode clone = CloneNode(entry);
        root.Message.Add(clone);
        root.Dirty = true;
        return clone;
    }

    /// <summary>Drop every <c>[2.3]</c> entry matching <paramref name="predicate"/>. Returns the count.</summary>
    public static int RemoveTableEntries(List<PbNode> tree, Func<PbNode, bool> predicate)
    {
        (PbNode root, _) = TableEntries(tree);
        List<PbNode> children = root.Message ?? [];
        List<PbNode> keep = children.Where(n => !(n.Number == 3 && predicate(n))).ToList();
        int removed = children.Count - keep.Count;
        if (removed > 0)
        {
            children.Clear();
            children.AddRange(keep);
            root.Dirty = true;
        }

        return removed;
    }
}
