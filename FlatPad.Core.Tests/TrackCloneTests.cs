using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Tables;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>Port of <c>acevo-modkit/tests/test_track_clone.py</c> (11 tests).</summary>
public class TrackCloneTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string reference, byte[] data)
    {
        string path = RefPath.RealPath(_root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private byte[] Read(string reference) => File.ReadAllBytes(RefPath.RealPath(_root, reference));

    // ------------------------------------------------------------------ closure

    [Fact]
    public void Closure_follows_refs_and_splits_owned_from_borrowed()
    {
        // scene -> material (owned) + a shared common_assets texture (borrowed)
        Write("content/tracks/src/src.scene", Cat(
            StringField(1, "content\\tracks\\src\\materials\\a.material"),
            StringField(2, "content\\tracks\\common_assets\\textures\\shared.texture")));
        // the owned material pulls in one more owned texture
        Write("content/tracks/src/materials/a.material",
            StringField(1, "content\\tracks\\src\\textures\\t.texture"));
        Write("content/tracks/src/textures/t.texture", StringField(1, "x"));
        Write("content/tracks/common_assets/textures/shared.texture", StringField(1, "x"));

        ClosureResult res = Closure.Crawl(_root, ["content/tracks/src/src.scene"], "content/tracks/src");

        Assert.Equal([
            "content/tracks/src/materials/a.material",
            "content/tracks/src/src.scene",
            "content/tracks/src/textures/t.texture",
        ], res.Owned.Order(StringComparer.Ordinal));
        Assert.Equal(["content/tracks/common_assets/textures/shared.texture"], res.External);
        Assert.Empty(res.Missing);
    }

    [Fact]
    public void Closure_does_not_crawl_into_borrowed_content()
    {
        // A shared asset is recorded but not walked — its own deps are the base game's problem.
        Write("content/tracks/src/src.scene", StringField(1, "content\\tracks\\common_assets\\a.material"));
        Write("content/tracks/common_assets/a.material",
            StringField(1, "content\\tracks\\common_assets\\deep.texture"));

        ClosureResult res = Closure.Crawl(_root, ["content/tracks/src/src.scene"], "content/tracks/src");

        Assert.Equal(["content/tracks/common_assets/a.material"], res.External);
    }

    [Fact]
    public void Closure_reports_missing_owned_refs()
    {
        Write("content/tracks/src/src.scene", StringField(1, "content\\tracks\\src\\gone.material"));

        ClosureResult res = Closure.Crawl(_root, ["content/tracks/src/src.scene"], "content/tracks/src");

        Assert.Equal(["content/tracks/src/gone.material"], res.Missing);
    }

    [Fact]
    public void Closure_overrides_supply_bytes_for_files_not_on_disk()
    {
        // A derived (stripped) scene contributes its refs before it is ever written.
        Write("content/tracks/src/src.scene", StringField(1, "content\\tracks\\src\\old.material"));
        Write("content/tracks/src/new.material", StringField(1, "x"));

        ClosureResult res = Closure.Crawl(_root, ["content/tracks/src/src.scene"], "content/tracks/src",
            new Dictionary<string, byte[]>
            {
                ["content/tracks/src/src.scene"] = StringField(1, "content\\tracks\\src\\new.material"),
            });

        Assert.Contains("content/tracks/src/new.material", res.Owned);
        Assert.DoesNotContain("content/tracks/src/old.material", res.Owned);
    }

    [Fact]
    public void Closure_skips_opaque_payloads()
    {
        // .texturemips are raw pixels; a byte sequence in them must not be mistaken for a ref.
        Write("content/tracks/src/src.scene", StringField(1, "content\\tracks\\src\\a.texturemips"));
        Write("content/tracks/src/a.texturemips", StringField(1, "content\\tracks\\src\\phantom.material"));

        ClosureResult res = Closure.Crawl(_root, ["content/tracks/src/src.scene"], "content/tracks/src");

        Assert.DoesNotContain("content/tracks/src/phantom.material", res.Owned);
    }

    // ------------------------------------------------------------------ repath / clone

    [Fact]
    public void Repath_swaps_prefix_case_insensitively()
    {
        Assert.Equal("content/tracks/flatpad/a.mesh",
            RefPath.Repath("content/tracks/Sebring/a.mesh", "content/tracks/sebring", "content/tracks/flatpad"));
        Assert.Equal("content/tracks/other/a.mesh",
            RefPath.Repath("content/tracks/other/a.mesh", "content/tracks/sebring", "content/tracks/flatpad"));
    }

    [Fact]
    public void Clone_copies_repaths_and_renames()
    {
        Write("content/tracks/src/src.scene", Cat(
            StringField(1, "content\\tracks\\src\\materials\\a.material"),
            StringField(2, "content\\tracks\\common_assets\\shared.texture")));
        Write("content/tracks/src/materials/a.material", StringField(1, "hello"));

        CloneStats stats = CloneTree.Clone(_root,
            ["content/tracks/src/src.scene", "content/tracks/src/materials/a.material"],
            "content/tracks/src", "content/tracks/dst",
            rename: new Dictionary<string, string>
            {
                ["content/tracks/src/src.scene"] = "content/tracks/dst/dst.scene",
            });

        Assert.Empty(stats.Missing);
        string[] strings = PbTree.WalkStrings(Read("content/tracks/dst/dst.scene"))
            .Select(h => h.Text).ToArray();
        // owned ref repathed; borrowed ref left exactly as it was
        Assert.Contains("content\\tracks\\dst\\materials\\a.material", strings);
        Assert.Contains("content\\tracks\\common_assets\\shared.texture", strings);
    }

    [Fact]
    public void Clone_drags_the_texturemips_companion()
    {
        // The .texture header carries no path — pairing is by basename only.
        Write("content/tracks/src/t.texture", StringField(1, "hdr"));
        Write("content/tracks/src/t.texturemips", [0x00, 0x01, 0x02, .. "pixels"u8, 0xFF]);

        CloneStats stats = CloneTree.Clone(_root, ["content/tracks/src/t.texture"],
            "content/tracks/src", "content/tracks/dst");

        Assert.Equal(1, stats.Mips);
        Assert.Equal([0x00, 0x01, 0x02, .. "pixels"u8, 0xFF], Read("content/tracks/dst/t.texturemips"));
    }

    [Fact]
    public void Clone_copies_non_protobuf_verbatim()
    {
        byte[] payload = [0xFF, 0xFE, .. " not protobuf at all "u8, 0x00];
        Write("content/tracks/src/blob.mesh", payload);

        CloneStats stats = CloneTree.Clone(_root, ["content/tracks/src/blob.mesh"],
            "content/tracks/src", "content/tracks/dst");

        Assert.Equal(1, stats.Copied);
        Assert.Equal(payload, Read("content/tracks/dst/blob.mesh"));
    }

    [Fact]
    public void Clone_uses_override_bytes()
    {
        Write("content/tracks/src/src.scene", StringField(1, "on disk"));

        CloneTree.Clone(_root, ["content/tracks/src/src.scene"], "content/tracks/src", "content/tracks/dst",
            overrides: new Dictionary<string, byte[]>
            {
                ["content/tracks/src/src.scene"] = StringField(1, "derived"),
            });

        Assert.Equal(["derived"],
            PbTree.WalkStrings(Read("content/tracks/dst/src.scene")).Select(h => h.Text));
    }

    // ------------------------------------------------------------------ *.table registries

    /// <summary>A *.table file: one [2] container holding repeated [2.3] entries.</summary>
    private static byte[] Table(params byte[][] entries) => MessageField(2, Cat(entries));

    private static byte[] TrackEntry(string name, string folder, ulong ident = 5954, ulong index = 36) =>
        MessageField(3, MessageField(8, Cat(
            StringField(1, name),
            StringField(3, $"content\\tracks\\{folder}"),
            VarintField(8, ident),
            VarintField(21, index))));

    [Fact]
    public void AppendTableEntry_clones_with_new_strings_and_ids()
    {
        byte[] data = Table(TrackEntry("Sebring International Raceway", "sebring"));
        List<PbNode> tree = PbTree.ParseTree(data);
        (_, List<PbNode> entries) = TableEditor.TableEntries(tree);

        TableEditor.AppendTableEntry(tree, entries[0],
            [("Sebring International Raceway", "Flat Pad"),
             ("content\\tracks\\sebring", "content\\tracks\\flatpad")],
            [([8, 8], 26001), ([8, 21], 37)]);

        List<PbNode> outTree = PbTree.ParseTree(PbTree.EncodeTree(tree));
        (_, List<PbNode> outEntries) = TableEditor.TableEntries(outTree);
        Assert.Equal(2, outEntries.Count);
        // original untouched
        Assert.Equal("Sebring International Raceway", TableEditor.TextAt(outEntries[0], 8, 1));
        Assert.Equal(5954ul, TableEditor.Child(outEntries[0], 8, 8)!.Varint);
        // clone rewritten
        Assert.Equal("Flat Pad", TableEditor.TextAt(outEntries[1], 8, 1));
        Assert.Equal("content\\tracks\\flatpad", TableEditor.TextAt(outEntries[1], 8, 3));
        Assert.Equal(26001ul, TableEditor.Child(outEntries[1], 8, 8)!.Varint);
        Assert.Equal(37ul, TableEditor.Child(outEntries[1], 8, 21)!.Varint);
    }

    [Fact]
    public void Append_is_repeatable_after_removal_so_install_is_idempotent()
    {
        byte[] data = Table(TrackEntry("Sebring International Raceway", "sebring"));
        List<PbNode> tree = PbTree.ParseTree(data);
        (_, List<PbNode> entries) = TableEditor.TableEntries(tree);
        List<(string, string)> repl = [("Sebring International Raceway", "Flat Pad")];

        TableEditor.AppendTableEntry(tree, entries[0], repl);
        int removed = TableEditor.RemoveTableEntries(tree, e => TableEditor.TextAt(e, 8, 1) == "Flat Pad");
        Assert.Equal(1, removed);
        TableEditor.AppendTableEntry(tree, entries[0], repl);

        List<PbNode> outTree = PbTree.ParseTree(PbTree.EncodeTree(tree));
        (_, List<PbNode> outEntries) = TableEditor.TableEntries(outTree);
        Assert.Equal(["Sebring International Raceway", "Flat Pad"],
            outEntries.Select(e => TableEditor.TextAt(e, 8, 1)));
    }

    [Fact]
    public void Untouched_table_round_trips_byte_identical()
    {
        byte[] data = Table(TrackEntry("A", "a"), TrackEntry("B", "b"));
        List<PbNode> tree = PbTree.ParseTree(data);

        Assert.Equal(data, PbTree.EncodeTree(tree));
    }

    [Fact]
    public void SetVarint_raises_on_a_missing_field()
    {
        List<PbNode> tree = PbTree.ParseTree(Table(TrackEntry("A", "a")));
        (_, List<PbNode> entries) = TableEditor.TableEntries(tree);

        Assert.Throws<KeyNotFoundException>(() => TableEditor.SetVarint(entries[0], [8, 99], 1));
    }
}
