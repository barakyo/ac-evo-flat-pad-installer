using System.IO.Compression;
using System.Security.Cryptography;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Tables;
using EvoMods.Core.Tracks;

using static EvoMods.Core.Tests.PbFixture;

namespace EvoMods.Core.Tests;

/// <summary>
/// Custom track install: reading an EvoForge-shaped package, registering it from the files it
/// actually ships, and removing only what this tool added.
/// </summary>
public sealed class CustomTrackTests : IDisposable
{
    private const string Id = "test_drift";
    private const string Name = "Test Drift Park";
    private const string Common = "content/tracks/common_assets/materials/grass.material";

    private readonly string _game = Directory.CreateTempSubdirectory("evomods-game-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("evomods-pkg-").FullName;
    private readonly List<string> _log = [];

    public CustomTrackTests()
    {
        // Sebring's rows as the real table has them: its own containers under content\tracks\sebring,
        // plus the shared camera sequence No Game Mode pulls in from common_assets.
        RegistryFixture.Write(_game, TrackRows.TracksTable,
            RegistryFixture.CatalogTable([Catalog(RegistryFixture.Donor, "sebring"), Catalog("Kyalami", "kyalami")]));
        RegistryFixture.Write(_game, TrackRows.ContainersTable, RegistryFixture.SessionTable(
        [
            Session("GP Time Attack", RegistryFixture.Donor, 5954, 36, ["sebring\\containers\\layout_gp.scene", "sebring\\containers\\spawnpoints_pitlane_gp.scene"], cars: 45),
            Session("GP Hotstint", RegistryFixture.Donor, 5954, 36, ["sebring\\containers\\layout_gp.scene", "sebring\\containers\\spawnpoints_hotlap_gp.scene"]),
            Session("No Game Mode", RegistryFixture.Donor, 5954, 36, ["sebring\\containers\\tv1_cameras_gp.scene", "common_assets\\containers\\cam_sequence.scene"]),
            Session("GP Time Attack", "Kyalami", 4522, 37, ["kyalami\\containers\\layout_gp.scene"]),
        ]));
        Directory.CreateDirectory(Path.Combine(_game, "content", "tracks", "sebring"));
        WriteFile(_game, Common, [1, 2, 3]);
        WriteFile(_game, "content/tracks/common_assets/containers/cam_sequence.scene", [4]);
    }

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        Directory.Delete(_work, recursive: true);
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] Catalog(string name, string folder) =>
        MessageField(3, MessageField(2, Cat(
            StringField(1, name),
            StringField(3, $"content\\tracks\\{folder}"),
            StringField(4, $"content\\tracks\\{folder}\\{folder}.scene"),
            StringField(5, "USA"),
            StringField(8, $"content\\tracks\\{folder}\\{folder}.track"))));

    private static byte[] Session(string session, string track, ulong id, ulong index, string[] containers, ulong? cars = null) =>
        MessageField(3, MessageField(8, Cat(
            StringField(1, session),
            VarintField(8, id),
            cars is { } c ? VarintField(9, c) : [],
            StringField(10, track),
            Cat([.. containers.Select(x => StringField(11, $"content\\tracks\\{x}"))]),
            StringField(14, "GP"),
            VarintField(21, index))));

    private static byte[] StartPos(string name) =>
        MessageField(2, Cat(StringField(1, name), StringField(3, "Start Pos")));

    private static void WriteFile(string root, string rel, byte[] data)
    {
        string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    /// <summary>
    /// An EvoForge-shaped package in a wrapper folder: unsuffixed containers, a manifest, and a
    /// checksum list — the shape HDC Drift Park actually arrived in.
    /// </summary>
    private string Package(bool zip = true, string id = Id, bool withHotlap = true, string? reference = Common,
        Action<string>? tamper = null)
    {
        string root = Path.Combine(_work, $"pkg-{Guid.NewGuid():N}");
        string wrapper = Path.Combine(root, "My_Track_v1");
        string track = $"content/tracks/{id}";
        var files = new Dictionary<string, byte[]>
        {
            [$"{track}/{id}.scene"] = Cat(StringField(1, "SMesh"), MessageField(2, StringField(5, (reference ?? $"{track}/{id}.track").Replace('/', '\\')))),
            [$"{track}/{id}.track"] = [0x0A, 0x01, 0x10],
            [$"{track}/containers/layout_park.scene"] = StringField(1, "Spline"),
            [$"{track}/containers/timelines.scene"] = StringField(1, "Zone"),
            [$"{track}/containers/spawnpoints_pitlane.scene"] = Cat(StringField(1, "Start Pos"),
                StartPos("pit_spawn_0"), StartPos("pit_spawn_1"), StartPos("box_spawn_0"), StartPos("box_spawn_1")),
            [$"{track}/containers/spawnpoints_grid.scene"] = Cat(StringField(1, "Start Pos"),
                StartPos("starting_grid_0"), StartPos("starting_grid_1"), StartPos("starting_grid_2")),
        };
        if (withHotlap)
            files[$"{track}/containers/spawnpoints_hotlap.scene"] = Cat(StringField(1, "Start Pos"), StartPos("starting_position_hotlap"));

        foreach ((string rel, byte[] data) in files)
            WriteFile(wrapper, rel, data);
        File.WriteAllText(Path.Combine(wrapper, "manifest.json"),
            $$"""{ "name": "{{Name}}", "version": "0.1", "trackId": "{{id}}", "layoutId": "park" }""");
        File.WriteAllLines(Path.Combine(wrapper, "SHA256SUMS.txt"),
            files.Select(f => $"{Convert.ToHexStringLower(SHA256.HashData(f.Value))}  {f.Key}"));
        tamper?.Invoke(wrapper);

        if (!zip)
            return root;
        string zipPath = root + ".zip";
        ZipFile.CreateFromDirectory(root, zipPath);
        return zipPath;
    }

    private TrackInstaller Installer() => new(_game, _log.Add);

    private LedgerTrack Install(string package, TrackInstallOptions? options = null)
    {
        using TrackPackage p = TrackPackage.Open(package);
        TrackInstallPlan plan = Installer().Plan(p, options);
        Assert.True(plan.CanInstall, string.Join("; ", plan.Problems));
        return Installer().Install(plan);
    }

    private List<PbNode> Rows(string table) =>
        TableEditor.TableEntries(RegistryFixture.ReadTable(_game, table)).Entries;

    private List<PbNode> OurSessions(string name = Name) =>
        Rows(TrackRows.ContainersTable).Where(e => TableEditor.RawTextAt(e, 8, 10) == name).ToList();

    private static List<string> Containers(PbNode row) =>
        TableEditor.Child(row, 8)!.Find(11).Select(TableEditor.RawText).OfType<string>().ToList();

    private byte[] TableBytes(string table) => File.ReadAllBytes(Path.Combine(_game, table));

    // ------------------------------------------------------------------ reading a package

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opens_a_package_through_a_wrapper_folder(bool zip)
    {
        using TrackPackage p = TrackPackage.Open(Package(zip));

        Assert.Equal(Id, p.TrackId);
        Assert.Equal(Name, p.DisplayName);
        Assert.Equal("0.1", p.Version);
        Assert.True(p.FullyChecksummed);
        Assert.Equal(7, p.Files.Count);
        Assert.Equal(2, p.Ignored.Count);   // the manifest and the checksum list describe it; they are not it

        PackageLayout layout = Assert.Single(p.Layouts);
        Assert.Equal("park", layout.Id);
        Assert.Equal("containers/timelines.scene", layout.Containers["timelines"]);
        Assert.Equal("containers/spawnpoints_pitlane.scene", layout.Containers["spawnpoints_pitlane"]);
        Assert.False(layout.Containers.ContainsKey("pitlane_zones"));
    }

    [Fact]
    public void Grid_size_counts_grid_spawns_not_pit_boxes()
    {
        using TrackPackage p = TrackPackage.Open(Package());
        Assert.Equal(3, p.GridSize(p.Layouts[0]));
    }

    [Fact]
    public void Refuses_a_zip_entry_that_climbs_out_of_its_folder()
    {
        string zipPath = Package();
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            zip.CreateEntry("My_Track_v1/content/tracks/test_drift/../../../../../evil.txt");

        InstallException e = Assert.Throws<InstallException>(() => TrackPackage.Open(zipPath));
        Assert.Contains("leave its own folder", e.Message);
    }

    [Fact]
    public void Refuses_a_package_without_pit_spawns()
    {
        string folder = Package(zip: false, tamper: w =>
            File.Delete(Path.Combine(w, "content", "tracks", Id, "containers", "spawnpoints_pitlane.scene")));

        InstallException e = Assert.Throws<InstallException>(() => TrackPackage.Open(folder));
        Assert.Contains("spawnpoints_pitlane", e.Message);
    }

    [Fact]
    public void Reports_references_that_resolve_nowhere()
    {
        using TrackPackage p = TrackPackage.Open(Package(reference: "content/tracks/nowhere/gone.material"));
        TrackInstallPlan plan = Installer().Plan(p);

        Assert.Equal(["content/tracks/nowhere/gone.material"], plan.MissingReferences);
        Assert.Contains(plan.Warnings, w => w.Contains("neither the package nor the game"));
    }

    // ------------------------------------------------------------------ registering

    [Fact]
    public void Registers_rows_built_from_the_files_the_package_ships()
    {
        LedgerTrack t = Install(Package(), new TrackInstallOptions(LayoutCodes: new Dictionary<string, string> { ["park"] = "Park" }));

        PbNode catalog = Assert.Single(Rows(TrackRows.TracksTable), e => TableEditor.RawTextAt(e, 2, 1) == Name);
        Assert.Equal($"content\\tracks\\{Id}", TableEditor.RawTextAt(catalog, 2, 3));
        Assert.Equal($"content\\tracks\\{Id}\\{Id}.scene", TableEditor.RawTextAt(catalog, 2, 4));
        Assert.Equal($"content\\tracks\\{Id}\\{Id}.track", TableEditor.RawTextAt(catalog, 2, 8));

        List<PbNode> sessions = OurSessions();
        Assert.Equal(["Park Time Attack", "Park Hotstint", "No Game Mode"],
            sessions.Select(e => TableEditor.RawTextAt(e, 8, 1)));
        Assert.All(sessions, e => Assert.Equal("Park", TableEditor.RawTextAt(e, 8, 14)));

        string c = $"content\\tracks\\{Id}\\containers\\";
        Assert.Equal([c + "layout_park.scene", c + "timelines.scene", c + "spawnpoints_pitlane.scene"], Containers(sessions[0]));
        Assert.Equal([c + "layout_park.scene", c + "timelines.scene", c + "spawnpoints_hotlap.scene"], Containers(sessions[1]));
        // No TV cameras in the package, so only the shared camera sequence survives from the donor.
        Assert.Equal(["content\\tracks\\common_assets\\containers\\cam_sequence.scene"], Containers(sessions[2]));

        Assert.Equal(3UL, TableEditor.Child(sessions[0], 8, 9)!.Varint);
        Assert.All(sessions, e => Assert.Equal(RegistryNumbers.NewTrackIdFloor, TableEditor.Child(e, 8, 8)!.Varint));
        Assert.All(sessions, e => Assert.Equal(38UL, TableEditor.Child(e, 8, 21)!.Varint));

        Assert.Equal(CustomTrackState.Installed, Installer().DetectState(t));
        Assert.True(File.Exists(Path.Combine(_game, "content", "tracks", Id, "containers", "timelines.scene")));
    }

    [Fact]
    public void A_package_without_a_hotlap_spawn_gets_no_hotstint()
    {
        Install(Package(withHotlap: false));
        Assert.DoesNotContain(OurSessions(), e => TableEditor.RawTextAt(e, 8, 1)!.EndsWith("Hotstint"));
    }

    [Fact]
    public void Verify_passes_on_a_fresh_install_and_catches_a_changed_file()
    {
        LedgerTrack t = Install(Package());
        Assert.Empty(Installer().Verify(t));

        File.WriteAllBytes(Path.Combine(_game, "content", "tracks", Id, "containers", "timelines.scene"), [9]);
        Assert.Contains(Installer().Verify(t), p => p.Contains("missing or changed"));
    }

    [Fact]
    public void Reinstalling_keeps_the_same_numbers_and_stacks_nothing()
    {
        LedgerTrack first = Install(Package());
        LedgerTrack second = Install(Package());

        Assert.Equal(3, OurSessions().Count);
        Assert.Single(Rows(TrackRows.TracksTable), e => TableEditor.RawTextAt(e, 2, 1) == Name);
        Assert.Equal(first.Layouts[0].RegistryId, second.Layouts[0].RegistryId);
        Assert.Equal(first.Layouts[0].MenuIndex, second.Layouts[0].MenuIndex);
    }

    [Fact]
    public void Uninstall_leaves_both_registries_byte_identical()
    {
        byte[] tracks = TableBytes("system/tracks.table");
        byte[] containers = TableBytes("system/track_containers.table");

        Install(Package());
        Installer().Uninstall(Id);

        Assert.Equal(tracks, TableBytes("system/tracks.table"));
        Assert.Equal(containers, TableBytes("system/track_containers.table"));
        Assert.False(Directory.Exists(Path.Combine(_game, "content", "tracks", Id)));
        Assert.Empty(TrackLedger.Load(_game).Tracks);
    }

    [Fact]
    public void Uninstall_spares_a_same_named_row_this_tool_did_not_write()
    {
        Install(Package());
        byte[] foreign = Session("Other Time Attack", Name, 777, 5, ["elsewhere\\layout.scene"]);
        List<PbNode> tree = RegistryFixture.ReadTable(_game, TrackRows.ContainersTable);
        TableEditor.AppendEntry(tree, PbTree.ParseTree(foreign)[0]);
        File.WriteAllBytes(Path.Combine(_game, "system", "track_containers.table"), PbTree.EncodeTree(tree));

        Installer().Uninstall(Id);

        Assert.Equal(777UL, TableEditor.Child(Assert.Single(OurSessions()), 8, 8)!.Varint);
    }

    [Fact]
    public void Two_custom_tracks_never_share_numbers()
    {
        LedgerTrack a = Install(Package());
        LedgerTrack b = Install(Package(id: "second_track"), new TrackInstallOptions("Second Track"));

        Assert.NotEqual(a.Layouts[0].RegistryId, b.Layouts[0].RegistryId);
        Assert.Equal(a.Layouts[0].MenuIndex + 1, b.Layouts[0].MenuIndex);
    }

    // ------------------------------------------------------------------ refusing

    [Fact]
    public void Refuses_a_folder_this_tool_did_not_install()
    {
        using TrackPackage p = TrackPackage.Open(Package(id: "sebring"));
        TrackInstallPlan plan = Installer().Plan(p, new TrackInstallOptions("Anything"));
        Assert.Contains(plan.Problems, x => x.Contains("did not install"));
    }

    [Fact]
    public void Refuses_the_name_of_another_track_even_when_told_to_replace()
    {
        using TrackPackage p = TrackPackage.Open(Package());
        TrackInstallPlan plan = Installer().Plan(p, new TrackInstallOptions("Kyalami", ReplaceExisting: true));
        Assert.Contains(plan.Problems, x => x.Contains("another track"));
    }

    [Fact]
    public void Another_tools_registration_of_the_same_folder_is_replaced_only_when_asked()
    {
        List<PbNode> tree = RegistryFixture.ReadTable(_game, TrackRows.TracksTable);
        TableEditor.AppendEntry(tree, PbTree.ParseTree(Catalog(Name, Id))[0]);
        File.WriteAllBytes(Path.Combine(_game, "system", "tracks.table"), PbTree.EncodeTree(tree));

        using (TrackPackage p = TrackPackage.Open(Package()))
        {
            TrackInstallPlan refused = Installer().Plan(p);
            Assert.True(refused.OtherToolRegistered);
            Assert.False(refused.CanInstall);
        }

        Install(Package(), new TrackInstallOptions(ReplaceExisting: true));
        Assert.Single(Rows(TrackRows.TracksTable), e => TableEditor.RawTextAt(e, 2, 1) == Name);
    }

    [Fact]
    public void A_damaged_download_installs_nothing()
    {
        string folder = Package(zip: false, tamper: w =>
            File.WriteAllBytes(Path.Combine(w, "content", "tracks", Id, "containers", "timelines.scene"), [0xFF]));
        byte[] tracks = TableBytes("system/tracks.table");

        using TrackPackage p = TrackPackage.Open(folder);
        TrackInstallPlan plan = Installer().Plan(p);
        Assert.Throws<InstallException>(() => Installer().Install(plan));

        Assert.False(Directory.Exists(Path.Combine(_game, "content", "tracks", Id)));
        Assert.Equal(tracks, TableBytes("system/tracks.table"));
        Assert.Empty(TrackLedger.Load(_game).Tracks);
    }

    // ------------------------------------------------------------------ after a game patch

    [Fact]
    public void Repair_re_registers_from_the_files_on_disk_after_the_registries_revert()
    {
        byte[] tracks = TableBytes("system/tracks.table");
        byte[] containers = TableBytes("system/track_containers.table");
        LedgerTrack installed = Install(Package());

        // What a game patch does: system\ comes back stock, content\tracks\ is left alone.
        File.WriteAllBytes(Path.Combine(_game, "system", "tracks.table"), tracks);
        File.WriteAllBytes(Path.Combine(_game, "system", "track_containers.table"), containers);
        Assert.Equal(CustomTrackState.FilesPresentButNotRegistered, Installer().DetectState(installed));

        LedgerTrack repaired = Installer().Repair(Id);

        Assert.Equal(CustomTrackState.Installed, Installer().DetectState(repaired));
        Assert.Equal(installed.Layouts[0].Code, repaired.Layouts[0].Code);
        Assert.Equal(installed.Layouts[0].RegistryId, repaired.Layouts[0].RegistryId);
        Assert.Empty(Installer().Verify(repaired));
    }

    // ------------------------------------------------------------------ allocation

    [Fact]
    public void Allocation_reuses_a_previous_slot_only_while_it_is_free()
    {
        List<PbNode> entries = TableEditor.TableEntries(PbTree.ParseTree(RegistryFixture.SessionTable(
            [RegistryFixture.SessionEntry("GP Time Attack", "A", 26001, 38)]))).Entries;

        Assert.Equal((26005UL, 40UL), RegistryNumbers.Allocate(entries, previous: (26005, 40)));
        Assert.Equal((26002UL, 39UL), RegistryNumbers.Allocate(entries, previous: (26001, 38)));
        Assert.Equal((26002UL, 39UL), RegistryNumbers.Allocate(entries));
    }
}
