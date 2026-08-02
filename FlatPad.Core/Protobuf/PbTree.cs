using System.Text;

namespace FlatPad.Core.Protobuf;

/// <summary>
/// Schema-less Protocol Buffers reader/writer for AC EVO editor data files.
/// </summary>
/// <remarks>
/// The game serialises <c>.scene</c>, <c>.mesh</c>, <c>.table</c> and friends as binary protobuf,
/// and Kunos has not published the <c>.proto</c> schemas. So the wire format is decoded generically
/// (like <c>protoc --decode_raw</c>): enough to walk the structure, pull out the embedded file
/// references, and write edits back.
///
/// Heuristic for a length-delimited field: try to parse its bytes as a nested message; if that
/// succeeds cleanly (valid wire types, every byte consumed, at least one field), treat it as a
/// message and recurse. Otherwise, if it is mostly printable, treat it as a string.
///
/// Port of <c>acevo_modkit/parsers/protobuf_raw.py</c>. The behaviour is deliberately identical
/// down to the heuristic thresholds — the reference implementation's file-reference counts are the
/// acceptance test, and they fall out of exactly these decisions.
/// </remarks>
public static class PbTree
{
    // ------------------------------------------------------------------ primitives

    /// <summary>
    /// Read a base-128 varint. Values wider than 64 bits saturate to <see cref="ulong.MaxValue"/>
    /// rather than wrapping, so an absurd length is still rejected by the bounds check that
    /// follows it (Python's arbitrary-precision ints reject it the same way).
    /// </summary>
    private static ulong ReadVarint(ReadOnlySpan<byte> buf, ref int i, int end)
    {
        int shift = 0;
        ulong result = 0;
        bool overflowed = false;
        while (true)
        {
            if (i >= end)
                throw new ProtobufException("varint overruns buffer");
            byte b = buf[i];
            i++;
            ulong part = (ulong)(b & 0x7F);
            if (shift < 64)
            {
                if (shift > 0 && (part >> (64 - shift)) != 0)
                    overflowed = true;
                result |= part << shift;
            }
            else if (part != 0)
            {
                overflowed = true;
            }

            if ((b & 0x80) == 0)
                return overflowed ? ulong.MaxValue : result;

            shift += 7;
            if (shift > 70)
                throw new ProtobufException("varint too long");
        }
    }

    public static byte[] EncodeVarint(ulong n)
    {
        Span<byte> tmp = stackalloc byte[10];
        int len = 0;
        while (true)
        {
            byte b = (byte)(n & 0x7F);
            n >>= 7;
            if (n != 0)
            {
                tmp[len++] = (byte)(b | 0x80);
            }
            else
            {
                tmp[len++] = b;
                return tmp[..len].ToArray();
            }
        }
    }

    private static void WriteVarint(Stream s, ulong n)
    {
        while (true)
        {
            byte b = (byte)(n & 0x7F);
            n >>= 7;
            if (n != 0)
            {
                s.WriteByte((byte)(b | 0x80));
            }
            else
            {
                s.WriteByte(b);
                return;
            }
        }
    }

    /// <summary>Skip over a buffer's fields, counting them. Throws if it is not valid protobuf.</summary>
    private static int CountFields(ReadOnlySpan<byte> buf)
    {
        int count = 0;
        int i = 0;
        int end = buf.Length;
        while (i < end)
        {
            ulong tag = ReadVarint(buf, ref i, end);
            long number = (long)(tag >> 3);
            int wire = (int)(tag & 7);
            if (number == 0)
                throw new ProtobufException("field number 0");
            switch (wire)
            {
                case WireType.Varint:
                    ReadVarint(buf, ref i, end);
                    break;
                case WireType.I64:
                    if (i + 8 > end)
                        throw new ProtobufException("i64 overruns buffer");
                    i += 8;
                    break;
                case WireType.Len:
                    ulong length = ReadVarint(buf, ref i, end);
                    if (length > (ulong)(end - i))
                        throw new ProtobufException("length-delimited overruns buffer");
                    i += (int)length;
                    break;
                case WireType.I32:
                    if (i + 4 > end)
                        throw new ProtobufException("i32 overruns buffer");
                    i += 4;
                    break;
                default:
                    throw new ProtobufException($"unsupported wire type {wire}");
            }

            count++;
        }

        return count;
    }

    /// <summary>Would these bytes parse cleanly as a nested message with at least one field?</summary>
    public static bool IsMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return false;
        try
        {
            return CountFields(data) >= 1;
        }
        catch (ProtobufException)
        {
            return false;
        }
    }

    /// <summary>Is this payload mostly printable ASCII? (95 % of bytes, tabs and newlines allowed.)</summary>
    public static bool LooksText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return false;
        int printable = 0;
        foreach (byte c in data)
        {
            if ((c >= 0x20 && c <= 0x7E) || c == 9 || c == 10 || c == 13)
                printable++;
        }

        return (double)printable / data.Length >= 0.95;
    }

    // ------------------------------------------------------------------ the lossless tree

    public static List<PbNode> ParseTree(byte[] data) => ParseTree(data, 0, data.Length);

    public static List<PbNode> ParseTree(byte[] data, int start, int end)
    {
        var nodes = new List<PbNode>();
        var span = data.AsSpan();
        int i = start;
        while (i < end)
        {
            ulong tag = ReadVarint(span, ref i, end);
            long number = (long)(tag >> 3);
            int wire = (int)(tag & 7);
            if (number == 0)
                throw new ProtobufException("field number 0");

            switch (wire)
            {
                case WireType.Varint:
                    nodes.Add(new PbNode(number, wire) { Varint = ReadVarint(span, ref i, end) });
                    break;

                case WireType.I64:
                    if (i + 8 > end)
                        throw new ProtobufException("i64 overruns buffer");
                    nodes.Add(new PbNode(number, wire) { Raw = span.Slice(i, 8).ToArray() });
                    i += 8;
                    break;

                case WireType.I32:
                    if (i + 4 > end)
                        throw new ProtobufException("i32 overruns buffer");
                    nodes.Add(new PbNode(number, wire) { Raw = span.Slice(i, 4).ToArray() });
                    i += 4;
                    break;

                case WireType.Len:
                {
                    ulong length = ReadVarint(span, ref i, end);
                    if (length > (ulong)(end - i))
                        throw new ProtobufException("length-delimited overruns buffer");
                    byte[] sub = span.Slice(i, (int)length).ToArray();
                    i += (int)length;
                    var node = new PbNode(number, wire) { Raw = sub };
                    if (IsMessage(sub))
                        node.Message = ParseTree(sub, 0, sub.Length);
                    else if (LooksText(sub))
                        node.Text = Encoding.UTF8.GetString(sub);
                    nodes.Add(node);
                    break;
                }

                default:
                    throw new ProtobufException($"unsupported wire type {wire}");
            }
        }

        return nodes;
    }

    /// <summary>
    /// Serialise a tree back to protobuf bytes. A length-delimited node emits its ORIGINAL payload
    /// unless it is dirty — that is what makes an untouched file round-trip byte-identical.
    /// </summary>
    public static byte[] EncodeTree(List<PbNode> nodes)
    {
        var ms = new MemoryStream();
        EncodeTree(nodes, ms);
        return ms.ToArray();
    }

    private static void EncodeTree(List<PbNode> nodes, Stream outStream)
    {
        foreach (PbNode n in nodes)
        {
            WriteVarint(outStream, ((ulong)n.Number << 3) | (uint)n.Wire);
            switch (n.Wire)
            {
                case WireType.Varint:
                    WriteVarint(outStream, n.Varint);
                    break;

                case WireType.I64:
                case WireType.I32:
                    outStream.Write(n.Raw, 0, n.Raw.Length);
                    break;

                case WireType.Len:
                {
                    byte[] body;
                    if (n.Dirty && n.Message is not null)
                        body = EncodeTree(n.Message);
                    else if (n.Dirty && n.Text is not null)
                        body = Encoding.UTF8.GetBytes(n.Text);
                    else
                        body = n.Raw;
                    WriteVarint(outStream, (ulong)body.Length);
                    outStream.Write(body, 0, body.Length);
                    break;
                }

                default:
                    throw new ProtobufException($"unsupported wire type {n.Wire}");
            }
        }
    }

    /// <summary>
    /// Apply <paramref name="fn"/> to every string leaf, marking changed nodes and their ancestors
    /// dirty. Returns true if anything changed, so callers know whether to re-encode.
    /// </summary>
    public static bool TransformText(List<PbNode> nodes, Func<string, string> fn)
    {
        bool changed = false;
        foreach (PbNode n in nodes)
        {
            if (n.Wire != WireType.Len)
                continue;
            if (n.Message is not null)
            {
                if (TransformText(n.Message, fn))
                {
                    n.Dirty = true;
                    changed = true;
                }
            }
            else if (n.Text is not null)
            {
                string updated = fn(n.Text);
                if (updated != n.Text)
                {
                    n.Text = updated;
                    n.Dirty = true;
                    changed = true;
                }
            }
        }

        return changed;
    }

    // ------------------------------------------------------------------ inspection

    /// <summary>
    /// Every string-like leaf in a buffer, as (dotted field path, text). Never throws: bytes that
    /// are not protobuf simply yield nothing.
    /// </summary>
    /// <remarks>
    /// Recurses whenever a payload parses cleanly as a message EVEN IF it is mostly printable. A
    /// real leaf string (<c>content\…</c>) will not parse as a valid message, so this splits
    /// path-packed submessages apart instead of returning them as one mangled string.
    /// </remarks>
    public static IEnumerable<(string Path, string Text)> WalkStrings(byte[] data, string path = "")
    {
        List<PbNode> fields;
        try
        {
            fields = ParseShallow(data);
        }
        catch (ProtobufException)
        {
            yield break;
        }

        foreach (PbNode f in fields)
        {
            if (f.Wire != WireType.Len)
                continue;
            string fp = path.Length == 0 ? f.Number.ToString() : $"{path}.{f.Number}";
            if (IsMessage(f.Raw))
            {
                foreach (var hit in WalkStrings(f.Raw, fp))
                    yield return hit;
            }
            else if (LooksText(f.Raw))
            {
                yield return (fp, Encoding.UTF8.GetString(f.Raw));
            }
        }
    }

    /// <summary>Decode one level of fields without recursing into nested messages.</summary>
    private static List<PbNode> ParseShallow(byte[] data)
    {
        var nodes = new List<PbNode>();
        var span = data.AsSpan();
        int i = 0;
        int end = data.Length;
        while (i < end)
        {
            ulong tag = ReadVarint(span, ref i, end);
            long number = (long)(tag >> 3);
            int wire = (int)(tag & 7);
            if (number == 0)
                throw new ProtobufException("field number 0");
            switch (wire)
            {
                case WireType.Varint:
                    nodes.Add(new PbNode(number, wire) { Varint = ReadVarint(span, ref i, end) });
                    break;
                case WireType.I64:
                    if (i + 8 > end)
                        throw new ProtobufException("i64 overruns buffer");
                    nodes.Add(new PbNode(number, wire) { Raw = span.Slice(i, 8).ToArray() });
                    i += 8;
                    break;
                case WireType.Len:
                {
                    ulong length = ReadVarint(span, ref i, end);
                    if (length > (ulong)(end - i))
                        throw new ProtobufException("length-delimited overruns buffer");
                    nodes.Add(new PbNode(number, wire) { Raw = span.Slice(i, (int)length).ToArray() });
                    i += (int)length;
                    break;
                }
                case WireType.I32:
                    if (i + 4 > end)
                        throw new ProtobufException("i32 overruns buffer");
                    nodes.Add(new PbNode(number, wire) { Raw = span.Slice(i, 4).ToArray() });
                    i += 4;
                    break;
                default:
                    throw new ProtobufException($"unsupported wire type {wire}");
            }
        }

        return nodes;
    }

    /// <summary>Human-readable structure dump, for debugging a file by eye.</summary>
    public static List<string> Dump(byte[] data, string path = "", int depth = 0)
    {
        var lines = new List<string>();
        List<PbNode> fields;
        try
        {
            fields = ParseShallow(data);
        }
        catch (ProtobufException)
        {
            lines.Add($"{new string(' ', depth * 2)}<not protobuf: {data.Length} bytes>");
            return lines;
        }

        foreach (PbNode f in fields)
        {
            string fp = path.Length == 0 ? f.Number.ToString() : $"{path}.{f.Number}";
            string ind = new(' ', depth * 2);
            switch (f.Wire)
            {
                case WireType.Varint:
                    lines.Add($"{ind}[{fp}] varint = {f.Varint}");
                    break;
                case WireType.I32:
                case WireType.I64:
                    lines.Add($"{ind}[{fp}] fixed{(f.Wire == WireType.I32 ? "32" : "64")}");
                    break;
                case WireType.Len:
                    if (IsMessage(f.Raw))
                    {
                        lines.Add($"{ind}[{fp}] msg ({f.Raw.Length}B)");
                        lines.AddRange(Dump(f.Raw, fp, depth + 1));
                    }
                    else if (LooksText(f.Raw))
                    {
                        lines.Add($"{ind}[{fp}] str = '{Encoding.UTF8.GetString(f.Raw)}'");
                    }
                    else
                    {
                        lines.Add($"{ind}[{fp}] bytes ({f.Raw.Length}B)");
                    }

                    break;
            }
        }

        return lines;
    }
}
