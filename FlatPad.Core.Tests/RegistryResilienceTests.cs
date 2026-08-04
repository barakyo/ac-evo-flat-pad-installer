using FlatPad.Core.FlatPad;
using FlatPad.Core.Game;
using FlatPad.Core.Protobuf;
using FlatPad.Core.Refs;
using FlatPad.Core.Tables;

namespace FlatPad.Core.Tests;

/// <summary>
/// What a game update does to a registered track.
/// </summary>
/// <remarks>
/// Every test here is a bug that shipped. A v0.8.1 install lost Kyalami twice over: once because a
/// stale <c>.orig</c> snapshot reverted the update's registry, and again because a hardcoded index
/// claimed the slot the update had given it. Nothing errored either time — the track was simply
/// absent from the menus.
/// </remarks>
public class RegistryResilienceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-registry-").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Installer Installer => new(_root, _log.Add);

    private void Write(string reference, byte[] data) => RegistryFixture.Write(_root, reference, data);

    private List<PbNode> ReadTable(string reference) => RegistryFixture.ReadTable(_root, reference);

    private List<string?> CatalogNames() => RegistryFixture.CatalogNames(_root);

    private List<(string? Track, ulong Index)> SessionIndices() =>
        RegistryFixture.Sessions(_root).Select(x => (x.Track, x.Index)).ToList();

    /// <summary>A stock-looking registry: the donor at index 36, plus whatever an update added.</summary>
    private void WriteStockRegistry(bool withUpdateTrack)
    {
        (byte[] catalog, byte[] sessions) = RegistryFixture.StockTables(withUpdateTrack);
        Write("system/tracks.table", catalog);
        Write("system/track_containers.table", sessions);
    }

    // ------------------------------------------------------------------ the Kyalami bugs

    [Fact]
    public void A_track_added_by_a_game_update_keeps_its_registry_index()
    {
        // The index is a DENSE global counter. Hardcoding 37 — correct when the donor held the max
        // of 36 — took the slot the update had just given Kyalami, and Kyalami vanished from the
        // menus with nothing logged.
        WriteStockRegistry(withUpdateTrack: true);

        Installer.Register();

        List<(string? Track, ulong Index)> indices = SessionIndices();
        ulong kyalami = indices.First(x => x.Track == "Kyalami").Index;
        var flatPad = indices.Where(x => x.Track == "Flat Pad").Select(x => x.Index).Distinct().ToList();

        Assert.Equal(37ul, kyalami);                 // untouched
        Assert.Single(flatPad);
        Assert.DoesNotContain(kyalami, flatPad);     // and we are not sitting on top of it
        Assert.Equal(38ul, flatPad[0]);              // next free
    }

    [Fact]
    public void Registering_never_discards_entries_added_since_the_last_install()
    {
        // The old code rebuilt from a .orig snapshot taken on first run. After an update that
        // snapshot is a previous version's registry, and writing it back silently reverts the
        // update — which is how Kyalami's registration disappeared entirely.
        WriteStockRegistry(withUpdateTrack: false);
        Installer.Register();               // first install, pre-update

        WriteStockRegistry(withUpdateTrack: true);  // the game updates, adding a track
        Installer.Register();               // and we install again

        Assert.Contains("Kyalami", CatalogNames());
        Assert.Contains(SessionIndices(), x => x.Track == "Kyalami");
    }

    [Fact]
    public void Re_registering_replaces_our_entries_instead_of_stacking_them()
    {
        WriteStockRegistry(withUpdateTrack: true);

        Installer.Register();
        Installer.Register();
        Installer.Register();

        Assert.Single(CatalogNames(), n => n == "Flat Pad");
        Assert.Equal(FlatPadSpec.Sessions.Length, SessionIndices().Count(x => x.Track == "Flat Pad"));
    }

    [Fact]
    public void Ids_stay_clear_of_the_base_games_block_even_as_it_grows()
    {
        WriteStockRegistry(withUpdateTrack: true);

        Installer.Register();

        (_, List<PbNode> e) = TableEditor.TableEntries(ReadTable("system/track_containers.table"));
        ulong ours = e.Where(x => TableEditor.TextAt(x, 8, 10) == "Flat Pad")
            .Select(x => TableEditor.Child(x, 8, 8)!.Varint).Distinct().Single();
        var baseIds = e.Where(x => TableEditor.TextAt(x, 8, 10) != "Flat Pad")
            .Select(x => TableEditor.Child(x, 8, 8)!.Varint).ToHashSet();

        Assert.DoesNotContain(ours, baseIds);
        Assert.True(ours >= FlatPadSpec.NewTrackIdFloor);
    }

    [Fact]
    public void A_stale_orig_snapshot_from_an_older_build_is_deleted_not_used()
    {
        WriteStockRegistry(withUpdateTrack: true);
        // An older version of this tool left one behind, holding a pre-update registry.
        Write("system/tracks.table.orig",
            RegistryFixture.CatalogTable([RegistryFixture.CatalogEntry(RegistryFixture.Donor)]));

        Installer.Register();

        Assert.False(File.Exists(RefPath.RealPath(_root, "system/tracks.table.orig")));
        Assert.Contains("Kyalami", CatalogNames());
    }

    // ------------------------------------------------------------------ archive rename

    [Fact]
    public void Unpack_does_not_pick_an_archive_name_that_is_already_taken()
    {
        // File.Move will not overwrite. Colliding here throws AFTER a full extraction, leaving every
        // file on disk and the install still reporting itself as packed.
        File.WriteAllBytes(Path.Combine(_root, "content.kspkg.bak"), [1, 2, 3]);

        string chosen = GameArchive.FreeDisabledName(Path.Combine(_root, "content.kspkg"));

        Assert.False(File.Exists(chosen));
        Assert.NotEqual(Path.Combine(_root, "content.kspkg.bak"), chosen);
    }

    [Fact]
    public void The_first_disabled_name_is_the_plain_one()
    {
        string chosen = GameArchive.FreeDisabledName(Path.Combine(_root, "content.kspkg"));

        Assert.Equal(Path.Combine(_root, "content.kspkg.bak"), chosen);
    }
}
