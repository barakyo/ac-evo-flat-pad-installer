namespace FlatPad.Core.Refs;

/// <summary>Result of a closure crawl, partitioned by ownership.</summary>
/// <param name="Owned">Refs under the crawl's own prefix — what a clone must copy.</param>
/// <param name="External">Everything else: shared base-game content, left pointing where it is.</param>
/// <param name="Missing">Owned refs with no file on disk.</param>
public sealed record ClosureResult(
    HashSet<string> Owned,
    HashSet<string> External,
    HashSet<string> Missing);

/// <summary>
/// Walks a track's reference graph instead of listing its files.
/// </summary>
/// <remarks>
/// A track's <c>.scene</c> references materials, which reference textures, which have binary mip
/// companions — hundreds of files, and which ones survive depends on how the scene was stripped.
/// So there is no copy list: BFS the <c>content\…</c> graph from a set of seeds and take exactly
/// what is reachable.
///
/// Port of <c>acevo_modkit/track_clone.py</c>.
/// </remarks>
public static class Closure
{
    /// <summary>Pure binary payloads: they hold no references, and scanning them is slow and wrong.</summary>
    private static readonly HashSet<string> OpaqueExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".texturemips", ".bank", ".dynamictrackpresetcompressed" };

    /// <summary>Guard against walking a 100 MB mesh looking for strings.</summary>
    private const long MaxScanBytes = 8_000_000;

    public static bool IsScannable(string reference) =>
        !OpaqueExtensions.Contains(Path.GetExtension(reference));

    private static HashSet<string> RefsOf(string gameRoot, string reference,
        IReadOnlyDictionary<string, byte[]> overrides)
    {
        if (!IsScannable(reference))
            return [];

        if (!overrides.TryGetValue(RefPath.Canon(reference), out byte[]? data))
        {
            string path = RefPath.RealPath(gameRoot, reference);
            try
            {
                if (new FileInfo(path).Length > MaxScanBytes)
                    return [];
                data = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        var result = new HashSet<string>();
        foreach (string r in ReferenceScanner.ExtractReferences(data))
            result.Add(RefPath.Canon(r));
        return result;
    }

    /// <summary>
    /// Walk the reference graph from <paramref name="seeds"/>, partitioned by
    /// <paramref name="ownPrefix"/>.
    /// </summary>
    /// <param name="overrides">
    /// Maps a ref to bytes to scan INSTEAD of the file on disk — so a derived file that has not
    /// been written yet (a stripped scene, say) still contributes its references.
    /// </param>
    public static ClosureResult Crawl(string gameRoot, IEnumerable<string> seeds, string ownPrefix,
        IReadOnlyDictionary<string, byte[]>? overrides = null)
    {
        Dictionary<string, byte[]> ov = (overrides ?? new Dictionary<string, byte[]>())
            .ToDictionary(kv => RefPath.Canon(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        ownPrefix = RefPath.Canon(ownPrefix);
        if (!ownPrefix.EndsWith('/'))
            ownPrefix += "/";

        var owned = new HashSet<string>(StringComparer.Ordinal);
        var external = new HashSet<string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var queue = new Stack<string>(seeds.Select(RefPath.Canon));
        while (queue.Count > 0)
        {
            string reference = queue.Pop();
            if (!seen.Add(reference.ToLowerInvariant()))
                continue;

            if (!reference.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase))
            {
                external.Add(reference);
                continue;               // borrowed asset: don't copy it, don't crawl into it
            }

            owned.Add(reference);
            if (!ov.ContainsKey(reference) && !File.Exists(RefPath.RealPath(gameRoot, reference)))
            {
                missing.Add(reference);
                continue;
            }

            foreach (string next in RefsOf(gameRoot, reference, ov))
                queue.Push(next);
        }

        return new ClosureResult(owned, external, missing);
    }
}
