using FlatPad.Core.Protobuf;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// The property everything else rests on: a tree re-emits a node's original bytes unless that node
/// was modified, so parse-then-encode is a no-op on an untouched file.
/// </summary>
public class PbTreeTests
{
    private static byte[] Table(params byte[][] entries) => MessageField(2, Cat(entries));

    private static byte[] TrackEntry(string name, string folder, ulong ident = 5954, ulong index = 36) =>
        MessageField(3, MessageField(8, Cat(
            StringField(1, name),
            StringField(3, $"content\\tracks\\{folder}"),
            VarintField(8, ident),
            VarintField(21, index))));

    [Fact]
    public void Untouched_table_round_trips_byte_identical()
    {
        byte[] data = Table(TrackEntry("A", "a"), TrackEntry("B", "b"));

        List<PbNode> tree = PbTree.ParseTree(data);

        Assert.Equal(data, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void Round_trip_survives_every_wire_type()
    {
        byte[] data = Cat(
            VarintField(1, 300),
            StringField(2, "content\\tracks\\sebring\\sebring.scene"),
            Fixed32Field(3, -219.0f),
            MessageField(4, Cat(VarintField(1, 0), Fixed32Field(2, 18.6f))),
            Cat(Tag(5, 1), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        Assert.Equal(data, PbTree.EncodeTree(PbTree.ParseTree(data)));
    }

    /// <summary>A nested field whose length prefix is padded to four bytes instead of one. Emitting
    /// the payload verbatim keeps it; re-serialising the node normalises it away.</summary>
    private static readonly byte[] NonCanonicalPayload =
        Cat(Tag(1, 2), [0x84, 0x80, 0x80, 0x00], "keep"u8.ToArray());

    [Fact]
    public void A_clean_node_emits_its_original_bytes_while_a_dirty_sibling_is_rewritten()
    {
        byte[] untouched = MessageField(1, NonCanonicalPayload);
        byte[] data = Cat(untouched, MessageField(2, StringField(1, "old")));

        List<PbNode> tree = PbTree.ParseTree(data);
        PbTree.TransformText(tree.Where(n => n.Number == 2).ToList(), s => s.Replace("old", "new"));
        byte[] outBytes = PbTree.EncodeTree(tree);

        Assert.Equal(untouched, outBytes[..untouched.Length]);
        Assert.Equal(MessageField(2, StringField(1, "new")), outBytes[untouched.Length..]);
    }

    [Fact]
    public void Dirtying_a_node_re_serialises_it_from_its_children()
    {
        List<PbNode> tree = PbTree.ParseTree(MessageField(1, NonCanonicalPayload));
        tree[0].Dirty = true;

        // Same field, same value — but written canonically, proving the payload really was rebuilt.
        Assert.Equal(MessageField(1, StringField(1, "keep")), PbTree.EncodeTree(tree));
    }

    [Fact]
    public void TransformText_marks_the_whole_ancestor_chain_dirty()
    {
        byte[] data = MessageField(1, MessageField(2, MessageField(3, StringField(4, "sebring"))));

        List<PbNode> tree = PbTree.ParseTree(data);
        bool changed = PbTree.TransformText(tree, s => s.Replace("sebring", "flatpad"));

        Assert.True(changed);
        Assert.Equal(MessageField(1, MessageField(2, MessageField(3, StringField(4, "flatpad")))),
            PbTree.EncodeTree(tree));
    }

    [Fact]
    public void TransformText_reports_no_change_when_nothing_matches()
    {
        List<PbNode> tree = PbTree.ParseTree(MessageField(1, StringField(2, "sebring")));

        Assert.False(PbTree.TransformText(tree, s => s.Replace("imola", "flatpad")));
    }

    [Fact]
    public void A_payload_that_parses_as_a_message_is_recursed_not_returned_as_a_string()
    {
        // Two packed path strings. Returned whole they would be one mangled ref; the message
        // heuristic has to split them.
        byte[] data = MessageField(1, Cat(
            StringField(1, "content\\tracks\\sebring\\a.material"),
            StringField(2, "content\\tracks\\sebring\\b.material")));

        string[] found = PbTree.WalkStrings(data).Select(h => h.Text).ToArray();

        Assert.Equal(["content\\tracks\\sebring\\a.material", "content\\tracks\\sebring\\b.material"],
            found);
    }

    [Fact]
    public void WalkStrings_reports_the_dotted_field_path()
    {
        byte[] data = MessageField(103, MessageField(12, StringField(6, "hello")));

        Assert.Equal([("103.12.6", "hello")], PbTree.WalkStrings(data).ToArray());
    }

    [Fact]
    public void WalkStrings_yields_nothing_for_bytes_that_are_not_protobuf()
    {
        Assert.Empty(PbTree.WalkStrings([0xFF, 0xFE, 0xFD, 0xFC]));
    }

    [Fact]
    public void Binary_payloads_are_neither_message_nor_text()
    {
        byte[] pixels = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x80, 0x81, 0x00];
        List<PbNode> tree = PbTree.ParseTree(MessageField(1, pixels));

        Assert.Null(tree[0].Message);
        Assert.Null(tree[0].Text);
        Assert.Equal(pixels, tree[0].Raw);
    }

    [Theory]
    [InlineData(new byte[] { 0x08 }, "varint overruns buffer")]            // tag with no value
    [InlineData(new byte[] { 0x0A, 0x05, 0x41 }, "length-delimited overruns buffer")]
    [InlineData(new byte[] { 0x0C, 0x01 }, "unsupported wire type 4")]
    [InlineData(new byte[] { 0x00, 0x01 }, "field number 0")]  // checked before the wire type
    public void Malformed_input_is_rejected(byte[] data, string message)
    {
        var ex = Assert.Throws<ProtobufException>(() => PbTree.ParseTree(data));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void An_over_long_varint_is_rejected_rather_than_wrapping()
    {
        // 12 continuation bytes: Python raises "varint too long" at shift 77, and a naive ulong
        // shift would silently wrap instead.
        byte[] data = Cat(Tag(1, 0), Enumerable.Repeat((byte)0xFF, 12).ToArray());

        Assert.Throws<ProtobufException>(() => PbTree.ParseTree(data));
    }
}
