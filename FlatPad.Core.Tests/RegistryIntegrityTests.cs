using FlatPad.Core.FlatPad;
using FlatPad.Core.Game;
using FlatPad.Core.Refs;

using static FlatPad.Core.Tests.RegistryFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// Damage to the registry the game owns — the failure mode nothing reports.
/// </summary>
/// <remarks>
/// A v0.8.1 install had Kyalami's catalog entry deleted by this tool and its menu index taken by a
/// hardcoded number. There was no error, no log line, and <c>verify</c> passed throughout; the track
/// was simply absent from every list. Both causes are fixed, but the invisibility is not fixed by
/// fixing a cause — the only way to notice damage from ANY cause is to hold the live registry up
/// against the one in the game's own archive, which is what these tests drive.
/// </remarks>
public class RegistryIntegrityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flatpad-integrity-").FullName;
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Installer Installer => new(_root, _log.Add);

    /// <summary>Stock tables served from memory, so none of this needs a 70 GB archive.</summary>
    private sealed class FakeStock(byte[] catalog, byte[] sessions) : IStockRegistry
    {
        public bool Available => true;

        public string Describe => "content.kspkg.bak";

        public byte[]? Read(string reference) => reference switch
        {
            "system/tracks.table" => catalog,
            "system/track_containers.table" => sessions,
            _ => null,
        };
    }

    /// <summary>Lay down a stock registry, and optionally the track folders that go with it.</summary>
    private FakeStock Stock(bool withUpdateTrack, bool updateTrackOnDisk = true)
    {
        (byte[] catalog, byte[] sessions) = StockTables(withUpdateTrack);
        Directory.CreateDirectory(RefPath.RealPath(_root, $"content/tracks/{DonorSlug}"));
        if (withUpdateTrack && updateTrackOnDisk)
            Directory.CreateDirectory(RefPath.RealPath(_root, $"content/tracks/{UpdateSlug}"));
        return new FakeStock(catalog, sessions);
    }

    /// <summary>Make the live registry a copy of stock, as a healthy install's would be.</summary>
    private void WriteLiveFromStock(bool withUpdateTrack)
    {
        (byte[] catalog, byte[] sessions) = StockTables(withUpdateTrack);
        Write(_root, "system/tracks.table", catalog);
        Write(_root, "system/track_containers.table", sessions);
    }

    private RegistryDiff Compare(IStockRegistry stock) => RegistryIntegrity.Compare(_root, stock);

    // ------------------------------------------------------------------ detection

    [Fact]
    public void A_registry_that_still_holds_everything_stock_holds_reports_counts_and_no_damage()
    {
        FakeStock stock = Stock(withUpdateTrack: true);
        WriteLiveFromStock(withUpdateTrack: true);
        Installer.Register();               // our own entries are not part of the comparison

        RegistryDiff diff = Compare(stock);

        Assert.True(diff.Available);
        // A check that finds nothing must still say what it looked at, or it is indistinguishable
        // from a check that ran against an empty table.
        Assert.Equal(2, diff.StockCatalog);
        Assert.Equal(5, diff.StockSessions);
        Assert.Empty(diff.Damaged);
        Assert.Empty(diff.Absent);
    }

    [Fact]
    public void A_base_track_whose_catalog_entry_was_deleted_is_reported_as_damage()
    {
        // Exactly what happened: a stale .orig snapshot written back over a registry the update had
        // grown, removing Kyalami's catalog entry. The files stayed on disk the whole time.
        FakeStock stock = Stock(withUpdateTrack: true);
        WriteLiveFromStock(withUpdateTrack: false);

        RegistryDiff diff = Compare(stock);

        Assert.Equal([UpdateTrack], diff.DamagedTracks);
        Assert.Contains(diff.Damaged, d => d.Table == "system/tracks.table" && d.Session is null);
        Assert.Contains(diff.Damaged, d => d.Session == "GP Time Attack");
        Assert.Empty(diff.Absent);
    }

    [Fact]
    public void A_catalog_entry_is_still_attributable_when_only_its_session_entries_survive()
    {
        // ⚠️ The real tracks.table gives a catalog entry a DISPLAY NAME and nothing else — no
        // content\tracks\… path anywhere in it. Only session entries carry one, in the repeated
        // [8.11] container list. So the owning folder has to be resolved per TRACK across both
        // tables; resolving it per entry leaves every catalog entry unattributable, and quietly
        // demotes a deleted catalog entry — the exact damage that happened — to "not our content".
        FakeStock stock = Stock(withUpdateTrack: true);
        Write(_root, "system/tracks.table", CatalogTable([CatalogEntry(Donor)]));
        Write(_root, "system/track_containers.table",
            SessionTable([.. DonorSessions(), .. UpdateSessions()]));

        RegistryDiff diff = Compare(stock);

        StockEntry missing = Assert.Single(diff.Damaged);
        Assert.Equal("system/tracks.table", missing.Table);
        Assert.Null(missing.Session);
        Assert.Equal($"content/tracks/{UpdateSlug}", missing.TrackFolder);
        Assert.Empty(diff.Absent);
    }

    [Fact]
    public void A_stock_track_this_install_does_not_have_is_not_damage()
    {
        // The guard that makes a mismatched archive harmless. An archive NEWER than the unpacked
        // content lists tracks the install never had — reporting those as damage would fail every
        // machine whose archive and loose content came from different game versions, which is an
        // ordinary state here.
        FakeStock stock = Stock(withUpdateTrack: true, updateTrackOnDisk: false);
        WriteLiveFromStock(withUpdateTrack: false);

        RegistryDiff diff = Compare(stock);

        Assert.Empty(diff.Damaged);
        Assert.Equal(3, diff.Absent.Count);          // 1 catalog + 2 sessions
        Assert.All(diff.Absent, a => Assert.Equal(UpdateTrack, a.Track));
    }

    [Fact]
    public void A_session_entry_missing_under_an_intact_catalog_entry_is_reported()
    {
        // The finer-grained half: the track still lists, but is absent from whichever part of the UI
        // enumerates that session kind — and nothing anywhere says which.
        FakeStock stock = Stock(withUpdateTrack: true);
        Write(_root, "system/tracks.table", StockTables(withUpdateTrack: true).Catalog);
        Write(_root, "system/track_containers.table", SessionTable(
            // Kyalami's "GP Race" entry is gone; everything else is intact.
            [.. DonorSessions(), SessionEntry("GP Time Attack", UpdateTrack, 6001, 37)]));

        RegistryDiff diff = Compare(stock);

        StockEntry missing = Assert.Single(diff.Damaged);
        Assert.Equal("GP Race", missing.Session);
        Assert.Equal("system/track_containers.table", missing.Table);
    }

    [Fact]
    public void Our_own_registration_is_excluded_from_the_comparison_on_both_sides()
    {
        // A registered track is in the live table by design, so the tool must never see its own work
        // as drift. Symmetric on purpose: the stock side is skipped too, so that if a future build
        // ever shipped an entry under our name the check would not start demanding we restore it
        // over the one we just wrote.
        Directory.CreateDirectory(RefPath.RealPath(_root, $"content/tracks/{DonorSlug}"));
        var stock = new FakeStock(
            CatalogTable([CatalogEntry(Donor), CatalogEntry(FlatPadSpec.DstDisplay)]),
            SessionTable([.. DonorSessions(),
                SessionEntry("GP Time Attack", FlatPadSpec.DstDisplay, 26001, 40)]));
        WriteLiveFromStock(withUpdateTrack: false);

        RegistryDiff diff = Compare(stock);

        Assert.Equal(1, diff.StockCatalog);          // the donor only — not our name
        Assert.Equal(3, diff.StockSessions);
        Assert.Empty(diff.Damaged);
        Assert.Empty(diff.Absent);
    }

    // ------------------------------------------------------------------ when to decline

    [Fact]
    public void With_no_archive_the_check_is_skipped_rather_than_failed()
    {
        // A user may have deleted theirs to reclaim 70 GB. That is not a fault, and it is not
        // something to report as one.
        WriteLiveFromStock(withUpdateTrack: true);

        RegistryDiff diff = Compare(ArchiveStockRegistry.ForGame(_root));

        Assert.False(diff.Available);
        Assert.Contains("no renamed-aside archive", diff.Source, StringComparison.Ordinal);
        Assert.Empty(diff.Damaged);
    }

    [Fact]
    public void With_several_archives_the_check_declines_to_guess_which_one_is_stock()
    {
        // A game update downloads a fresh archive, so an install accumulates them. The wrong one is
        // a different game version's content — `revert` already refuses to choose, and so does this.
        File.WriteAllBytes(Path.Combine(_root, "content.kspkg.bak"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "content.kspkg.jul22update-disabled"), [1, 2, 3]);
        WriteLiveFromStock(withUpdateTrack: true);

        RegistryDiff diff = Compare(ArchiveStockRegistry.ForGame(_root));

        Assert.False(diff.Available);
        Assert.Contains("2 archives present", diff.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_that_holds_no_registry_is_skipped_rather_than_throwing()
    {
        // Pointing the tool at some other .kspkg — a packaged car, say — must degrade to "nothing to
        // compare against", not an error the user cannot act on.
        WriteLiveFromStock(withUpdateTrack: true);

        RegistryDiff diff = Compare(new EmptyStock());

        Assert.False(diff.Available);
        Assert.Contains("holds no system/tracks.table", diff.Source, StringComparison.Ordinal);
    }

    private sealed class EmptyStock : IStockRegistry
    {
        public bool Available => true;

        public string Describe => "some_other.kspkg";

        public byte[]? Read(string reference) => null;
    }

    // ------------------------------------------------------------------ repair

    [Fact]
    public void Repair_restores_the_missing_entry_with_the_id_and_index_the_game_gave_it()
    {
        FakeStock stock = Stock(withUpdateTrack: true);
        WriteLiveFromStock(withUpdateTrack: false);

        Installer.RepairRegistry(Compare(stock));

        Assert.Contains(UpdateTrack, CatalogNames(_root));
        var restored = Sessions(_root).Where(s => s.Track == UpdateTrack).ToList();
        Assert.Equal(2, restored.Count);
        // Verbatim: a restored entry has to BE the one the update shipped, not a lookalike carrying
        // freshly allocated numbers.
        Assert.All(restored, s => Assert.Equal(6001ul, s.Id));
        Assert.All(restored, s => Assert.Equal(37ul, s.Index));
    }

    [Fact]
    public void Repair_moves_our_own_index_off_the_slot_the_restored_track_owns()
    {
        // The full shape of the original bug: Flat Pad was hardcoded to index 37, which is Kyalami's.
        // Restoring Kyalami at 37 without re-allocating ours would rebuild the collision — and
        // [8.21] is DENSE, so sharing one hides the other track completely.
        FakeStock stock = Stock(withUpdateTrack: true);
        WriteLiveFromStock(withUpdateTrack: false);
        Directory.CreateDirectory(RefPath.RealPath(_root, FlatPadSpec.DstDir));
        Installer.Register();               // with Kyalami absent, we take 37

        Assert.Contains(Sessions(_root), s => s.Track == FlatPadSpec.DstDisplay && s.Index == 37);

        Installer.RepairRegistry(Compare(stock));

        var kyalami = Sessions(_root).Where(s => s.Track == UpdateTrack).Select(s => s.Index).ToHashSet();
        var ours = Sessions(_root).Where(s => s.Track == FlatPadSpec.DstDisplay).Select(s => s.Index).ToHashSet();
        Assert.Equal([37ul], kyalami);
        Assert.Equal([38ul], ours);
    }

    [Fact]
    public void Repair_leaves_another_mods_registration_alone()
    {
        // ⚠️ The trap a wholesale table restore would fall into: putting the archive's tables back
        // wholesale would delete every other mod's entries. Only what is MISSING gets appended.
        FakeStock stock = Stock(withUpdateTrack: true);
        Write(_root, "system/tracks.table", CatalogTable(
            [CatalogEntry(Donor), CatalogEntry("Some Other Mod Track")]));
        Write(_root, "system/track_containers.table", SessionTable(
            [.. DonorSessions(), SessionEntry("GP Time Attack", "Some Other Mod Track", 27000, 40)]));

        Installer.RepairRegistry(Compare(stock));

        Assert.Contains("Some Other Mod Track", CatalogNames(_root));
        Assert.Contains(Sessions(_root), s => s.Track == "Some Other Mod Track" && s.Index == 40);
        Assert.Contains(UpdateTrack, CatalogNames(_root));
    }

    [Fact]
    public void Repair_with_nothing_missing_writes_nothing()
    {
        FakeStock stock = Stock(withUpdateTrack: true);
        WriteLiveFromStock(withUpdateTrack: true);
        byte[][] before =
        [
            File.ReadAllBytes(RefPath.RealPath(_root, "system/tracks.table")),
            File.ReadAllBytes(RefPath.RealPath(_root, "system/track_containers.table")),
        ];

        int restored = Installer.RepairRegistry(Compare(stock));

        Assert.Equal(0, restored);
        Assert.Equal(before[0], File.ReadAllBytes(RefPath.RealPath(_root, "system/tracks.table")));
        Assert.Equal(before[1], File.ReadAllBytes(RefPath.RealPath(_root, "system/track_containers.table")));
    }

    [Fact]
    public void Repair_refuses_when_there_is_nothing_trustworthy_to_compare_against()
    {
        WriteLiveFromStock(withUpdateTrack: false);

        Assert.Throws<InstallException>(() =>
            Installer.RepairRegistry(Compare(ArchiveStockRegistry.ForGame(_root))));
    }

    // ------------------------------------------------------------------ the verifier's one line

    [Fact]
    public void Verify_reports_the_comparison_in_a_single_line_when_the_registry_is_intact()
    {
        // Deliberately one line. This check has no counterpart in the Python reference — it cannot
        // read a .kspkg — so everything it prints is console divergence, and the per-entry detail is
        // worth that only when there is damage to describe.
        using var game = new SyntheticTrack();
        (byte[] catalog, byte[] sessions) = StockTables(withUpdateTrack: false);

        VerifyReport report = new Verifier(game.Root, new FakeStock(catalog, sessions)).Run();

        string line = Assert.Single(report.Lines, l => l.Contains("registry vs stock", StringComparison.Ordinal));
        Assert.Contains("content.kspkg.bak", line, StringComparison.Ordinal);
        Assert.Contains("1 catalog + 3 session entries in stock", line, StringComparison.Ordinal);
        Assert.Contains("0 missing live", line, StringComparison.Ordinal);
        Assert.True(report.Passed, string.Join("\n", report.Problems));
    }

    [Fact]
    public void Verify_fails_on_damage_and_hands_the_caller_what_it_needs_to_repair()
    {
        using var game = new SyntheticTrack();
        (byte[] catalog, byte[] sessions) = StockTables(withUpdateTrack: true);
        Directory.CreateDirectory(Path.Combine(game.Root, "content", "tracks", UpdateSlug));

        VerifyReport report = new Verifier(game.Root, new FakeStock(catalog, sessions)).Run();

        Assert.False(report.Passed);
        Assert.Contains(report.Problems, p => p.Contains(UpdateTrack, StringComparison.Ordinal));
        // Structured, not scraped: the GUI offers a repair straight from this without reopening the
        // archive and getting a second, possibly different, answer.
        Assert.Equal([UpdateTrack], report.Registry!.DamagedTracks);
    }
}
