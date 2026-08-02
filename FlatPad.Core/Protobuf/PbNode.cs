namespace FlatPad.Core.Protobuf;

/// <summary>Wire types the decoder handles. Anything else is malformed as far as we are concerned.</summary>
public static class WireType
{
    public const int Varint = 0;
    public const int I64 = 1;
    public const int Len = 2;
    public const int I32 = 5;
}

public class ProtobufException(string message) : Exception(message);

/// <summary>
/// One field of a lossless protobuf tree.
/// </summary>
/// <remarks>
/// A length-delimited node always keeps its original payload in <see cref="Raw"/>. On encode that
/// payload is emitted verbatim unless <see cref="Dirty"/> is set, which is what makes an untouched
/// file round-trip byte-for-byte and confines re-serialisation to the branches that actually
/// changed. Fixed-width nodes (wire 1 and 5) always emit <see cref="Raw"/>, so writing a float is
/// "assign Raw, then mark the length-delimited ancestors dirty".
/// </remarks>
public sealed class PbNode(long number, int wire)
{
    /// <summary>Field number. Wider than protobuf's own 2^29 limit so that a garbage buffer being
    /// probed by <c>IsMessage</c> is accepted or rejected exactly as the reference implementation's
    /// arbitrary-precision integers would.</summary>
    public long Number { get; } = number;

    public int Wire { get; } = wire;

    /// <summary>Value of a wire-0 field.</summary>
    public ulong Varint { get; set; }

    /// <summary>Wire 1/5 bytes, or the original wire-2 payload.</summary>
    public byte[] Raw { get; set; } = [];

    /// <summary>Set when a wire-2 payload decoded as a string.</summary>
    public string? Text { get; set; }

    /// <summary>Set when a wire-2 payload parsed as a nested message.</summary>
    public List<PbNode>? Message { get; set; }

    /// <summary>True once this node — or a descendant — was modified.</summary>
    public bool Dirty { get; set; }

    /// <summary>Children with the given field number. Empty if this node holds no message.</summary>
    public List<PbNode> Find(int number) =>
        Message is null ? [] : Message.Where(n => n.Number == number).ToList();

    /// <summary>First child with the given field number, or null.</summary>
    public PbNode? First(int number) =>
        Message?.FirstOrDefault(n => n.Number == number);
}
