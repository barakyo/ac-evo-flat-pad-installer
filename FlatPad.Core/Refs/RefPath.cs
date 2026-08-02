namespace FlatPad.Core.Refs;

/// <summary>
/// Reference paths in canonical form: <c>content/tracks/sebring/sebring.scene</c> — forward
/// slashes, relative to the game root. On disk the game's own files store backslashes, so anything
/// that rewrites a reference has to handle both.
/// </summary>
public static class RefPath
{
    /// <summary>Canonical form: forward slashes, no leading separator.</summary>
    public static string Canon(string reference) =>
        reference.Replace('\\', '/').TrimStart('/');

    /// <summary>Absolute path of a reference inside an unpacked game install.</summary>
    public static string RealPath(string gameRoot, string reference) =>
        Path.Combine(gameRoot, Canon(reference).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Swap a reference's owning prefix (case-insensitive, canonical form in and out).</summary>
    public static string Repath(string reference, string srcPrefix, string dstPrefix)
    {
        reference = Canon(reference);
        srcPrefix = Canon(srcPrefix);
        dstPrefix = Canon(dstPrefix);
        return reference.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase)
            ? dstPrefix + reference[srcPrefix.Length..]
            : reference;
    }

    /// <summary>
    /// String replacements that repath a reference in either separator style. Game files write
    /// <c>content\tracks\sebring\…</c>; our own canonical form uses forward slashes. Rewriting both
    /// means a file authored either way is handled.
    /// </summary>
    public static List<(string Old, string New)> ReplacementPairs(string srcPrefix, string dstPrefix)
    {
        string src = Canon(srcPrefix);
        string dst = Canon(dstPrefix);
        return
        [
            (src.Replace('/', '\\'), dst.Replace('/', '\\')),
            (src, dst),
        ];
    }
}
