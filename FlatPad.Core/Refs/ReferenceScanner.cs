using System.Text;
using System.Text.RegularExpressions;

using FlatPad.Core.Protobuf;

namespace FlatPad.Core.Refs;

/// <summary>Pulls the <c>content\…</c> file references out of a data file's bytes.</summary>
/// <remarks>Port of <c>acevo_modkit/graph.py:extract_references</c>.</remarks>
public static partial class ReferenceScanner
{
    /// <summary>
    /// A path reference: "content", path characters, then a file extension. Greedy up to the LAST
    /// dot, so a filename with a dot in its stem keeps its real extension
    /// (<c>rim_modded_car_7.5Jx18.rimmesh</c> must not truncate to <c>…7.5Jx18</c>). Path
    /// characters exclude control bytes, so a protobuf tag byte following a cleanly
    /// length-delimited string naturally terminates the match.
    /// </summary>
    [GeneratedRegex(@"content[\\/][\w\-./\\ ]+\.[A-Za-z0-9_]+")]
    private static partial Regex PathPattern { get; }

    /// <summary>
    /// Every <c>content/…</c> reference in a file's bytes, normalised to forward slashes.
    /// </summary>
    /// <remarks>
    /// The protobuf decoder is the primary source: string fields are exactly length-delimited, so a
    /// path ends cleanly at its extension. A raw-byte regex over the whole file would swallow the
    /// next field's tag byte instead — <c>master_bank.bankJ</c> — producing bogus "missing" refs.
    /// Only when a file is not protobuf at all do we fall back to scanning raw bytes.
    /// </remarks>
    public static HashSet<string> ExtractReferences(byte[] data)
    {
        var refs = new HashSet<string>();
        bool gotStrings = false;
        foreach ((_, string s) in PbTree.WalkStrings(data))
        {
            gotStrings = true;
            foreach (Match m in PathPattern.Matches(s))
                refs.Add(m.Value.Replace('\\', '/'));
        }

        if (!gotStrings)
        {
            // Non-protobuf file (html / css / js / …): regex the text directly.
            foreach (Match m in PathPattern.Matches(Encoding.Latin1.GetString(data)))
                refs.Add(m.Value.Replace('\\', '/'));
        }

        return refs;
    }
}
