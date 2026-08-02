using FlatPad.Core.Protobuf;

namespace FlatPad.Core.Refs;

public sealed class CloneStats
{
    public int Transformed { get; set; }
    public int Copied { get; set; }
    public int Mips { get; set; }
    public List<string> Missing { get; } = [];
    public List<string> Written { get; } = [];
}

/// <summary>Copy a set of references to a new prefix, repathing every string on the way through.</summary>
public static class CloneTree
{
    /// <summary>
    /// Copy <paramref name="src"/> to <paramref name="dst"/>, rewriting every protobuf string leaf
    /// through <paramref name="replacements"/>.
    /// </summary>
    /// <param name="data">Supply the source bytes directly, for content built in memory.</param>
    /// <returns>"transformed" for protobuf files, "copied" for anything else (passed through
    /// byte-for-byte).</returns>
    public static string ApplyToFile(string src, string dst, List<(string Old, string New)> replacements,
        byte[]? data = null)
    {
        string? dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        data ??= File.ReadAllBytes(src);

        List<PbNode> tree;
        try
        {
            tree = PbTree.ParseTree(data);
        }
        catch (ProtobufException)
        {
            File.WriteAllBytes(dst, data);      // non-protobuf asset (texture / mesh / …)
            return "copied";
        }

        PbTree.TransformText(tree, s => Replace(s, replacements));
        File.WriteAllBytes(dst, PbTree.EncodeTree(tree));
        return "transformed";
    }

    public static string Replace(string s, List<(string Old, string New)> replacements)
    {
        foreach ((string old, string @new) in replacements)
            s = s.Replace(old, @new, StringComparison.Ordinal);
        return s;
    }

    /// <summary>
    /// Copy every ref in <paramref name="refs"/> from <paramref name="srcPrefix"/> to
    /// <paramref name="dstPrefix"/>, repathing as it goes.
    /// </summary>
    /// <param name="rename">
    /// Maps a canonical source ref to a canonical destination ref, for the handful of files whose
    /// NAME changes too (<c>sebring.scene</c> -> <c>flatpad.scene</c>).
    /// </param>
    /// <param name="extraReplacements">
    /// Applied BEFORE the prefix rule, so a whole-path override (the renamed scene) wins over the
    /// general prefix swap.
    /// </param>
    public static CloneStats Clone(string gameRoot, IEnumerable<string> refs, string srcPrefix,
        string dstPrefix,
        IReadOnlyDictionary<string, byte[]>? overrides = null,
        List<(string Old, string New)>? extraReplacements = null,
        IReadOnlyDictionary<string, string>? rename = null)
    {
        Dictionary<string, byte[]> ov = (overrides ?? new Dictionary<string, byte[]>())
            .ToDictionary(kv => RefPath.Canon(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        Dictionary<string, string> ren = (rename ?? new Dictionary<string, string>())
            .ToDictionary(kv => RefPath.Canon(kv.Key), kv => RefPath.Canon(kv.Value), StringComparer.Ordinal);

        var replacements = new List<(string, string)>(extraReplacements ?? []);
        replacements.AddRange(RefPath.ReplacementPairs(srcPrefix, dstPrefix));

        var stats = new CloneStats();

        foreach (string reference in refs.Select(RefPath.Canon).Order(StringComparer.Ordinal))
        {
            string dstRef = ren.TryGetValue(reference, out string? renamed)
                ? renamed
                : RefPath.Repath(reference, srcPrefix, dstPrefix);
            string srcAbs = RefPath.RealPath(gameRoot, reference);
            string dstAbs = RefPath.RealPath(gameRoot, dstRef);
            ov.TryGetValue(reference, out byte[]? data);

            if (data is null && !File.Exists(srcAbs))
            {
                stats.Missing.Add(reference);
                continue;
            }

            if (data is null && !Closure.IsScannable(reference))
            {
                // Opaque payload (mips, audio bank, compressed preset): never parse it as protobuf.
                // A multi-MB blob that happens to decode would be re-encoded for no reason.
                CopyFile(srcAbs, dstAbs);
                stats.Copied++;
            }
            else if (ApplyToFile(srcAbs, dstAbs, replacements, data) == "transformed")
            {
                stats.Transformed++;
            }
            else
            {
                stats.Copied++;
            }

            stats.Written.Add(dstRef);

            // A .texture is only a header; its pixels live in a same-basename .texturemips that
            // nothing references by path. Copy it or the clone renders a blank tile.
            if (reference.EndsWith(".texture", StringComparison.OrdinalIgnoreCase))
            {
                string mipsSrc = srcAbs + "mips";
                if (File.Exists(mipsSrc))
                {
                    CopyFile(mipsSrc, dstAbs + "mips");
                    stats.Mips++;
                    stats.Written.Add(dstRef + "mips");
                }
            }
        }

        return stats;
    }

    private static void CopyFile(string src, string dst)
    {
        string? dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(src, dst, overwrite: true);
    }
}
