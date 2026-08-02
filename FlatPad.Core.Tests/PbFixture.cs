using System.Text;

namespace FlatPad.Core.Tests;

/// <summary>Minimal protobuf ENCODER for tests — mirrors the subset <c>PbTree</c> decodes.</summary>
/// <remarks>Port of <c>acevo-modkit/tests/pb_fixture.py</c>.</remarks>
public static class PbFixture
{
    public static byte[] Varint(ulong n)
    {
        var outBytes = new List<byte>();
        while (true)
        {
            byte b = (byte)(n & 0x7F);
            n >>= 7;
            if (n != 0)
            {
                outBytes.Add((byte)(b | 0x80));
            }
            else
            {
                outBytes.Add(b);
                return outBytes.ToArray();
            }
        }
    }

    public static byte[] Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);

    public static byte[] StringField(int field, string s)
    {
        byte[] data = Encoding.UTF8.GetBytes(s);
        return Cat(Tag(field, 2), Varint((ulong)data.Length), data);
    }

    public static byte[] MessageField(int field, byte[] inner) =>
        Cat(Tag(field, 2), Varint((ulong)inner.Length), inner);

    public static byte[] VarintField(int field, ulong n) => Cat(Tag(field, 0), Varint(n));

    public static byte[] Fixed32Field(int field, float value) =>
        Cat(Tag(field, 5), BitConverter.GetBytes(value));

    public static byte[] Cat(params byte[][] parts)
    {
        var outBytes = new List<byte>();
        foreach (byte[] p in parts)
            outBytes.AddRange(p);
        return outBytes.ToArray();
    }
}
