using FlatPad.Core.FlatPad;

namespace FlatPad.Core.Tests;

/// <summary>
/// Each of these failures cost a game launch to discover the first time round. They are all
/// decidable from the files on disk, and the point of the verifier is that they never cost one again.
/// </summary>
public class VerifierTests
{
    private static VerifyReport Verify(SyntheticTrack game) => new Verifier(game.Root).Run();

    private static bool HasProblem(VerifyReport r, string fragment) =>
        r.Problems.Any(p => p.Contains(fragment, StringComparison.Ordinal));

    private static string Line(VerifyReport r, string fragment) =>
        r.Lines.Single(l => l.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void A_well_formed_install_passes_and_reports_a_count_for_every_check()
    {
        using var game = new SyntheticTrack();

        VerifyReport report = Verify(game);

        Assert.True(report.Passed, string.Join("\n", report.Problems));
        Assert.Equal(0, report.ExitCode);
        // A validator that finds nothing to check must not read as a pass, so each line carries its
        // own count.
        Assert.Contains("121 own refs checked, 1 shared refs checked", Line(report, "content/tracks/flatpad"));
        Assert.Contains("3 checked against the pad AABB", Line(report, "  spawns:"));
        Assert.Contains("1 (want 1)", Line(report, "pit zones on the pad"));
        Assert.Contains("4 reshaped (want 4)", Line(report, "layout splines:"));
        Assert.Contains("5/5 present", Line(report, "ui tiles:"));
    }

    [Fact]
    public void A_spawn_left_at_the_donors_coordinates_is_reported()
    {
        // Every session type reads a different spawn container. Miss one and that mode spawns at
        // the donor circuit and the car drops off the pad edge.
        using var game = new SyntheticTrack().WithSpawnOffThePad();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, "is off the pad"));
        Assert.Contains("3 off the pad", Line(report, "  spawns:"));
    }

    [Fact]
    public void No_pit_zone_at_the_spawn_is_reported()
    {
        // Session start teleports the car to its box, which must fire "entered to pitlane" for the
        // pitlane page to open. With every zone exiled the load hangs on a dead screen.
        using var game = new SyntheticTrack().WithEveryPitZoneExiled();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, "exactly one pitlane zone must sit on the pad"));
        Assert.Contains("0 (want 1)", Line(report, "pit zones on the pad"));
    }

    [Fact]
    public void A_pit_zone_big_enough_to_drive_into_is_reported()
    {
        // Crossing a zone mid-test teleports the car to the pits and opens the setup screen.
        using var game = new SyntheticTrack().WithAnOversizedPitZone();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, "big enough to drive into by accident"));
    }

    [Fact]
    public void A_spline_that_is_not_the_requested_oval_is_reported()
    {
        using var game = new SyntheticTrack().WithADonorShapedSpline();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, "'gp_track_limits_right' is not the requested oval"));
    }

    [Fact]
    public void A_missing_session_kind_is_reported_even_though_the_game_would_not_complain()
    {
        // Practice enumerates the "Time Attack" entries. Registering Hotstint alone leaves the
        // track absent from the Practice list, with nothing logged anywhere.
        using var game = new SyntheticTrack().WithoutTheTimeAttackSession();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, "no 'Time Attack' session entry"));
    }

    [Fact]
    public void A_leftover_orig_snapshot_means_the_donor_is_still_modified()
    {
        // The track is derived from the donor, so deriving from a modified one silently bakes the
        // modification in. Only an earlier, destructive builder leaves these — this tool reads the
        // donor and never writes to it.
        using var game = new SyntheticTrack().WithAStrayOrigSnapshot();

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.True(HasProblem(report, ".orig snapshots remain"));
        Assert.Contains("1 leftover .orig snapshot(s)", Line(report, "  donor ("));
    }

    [Fact]
    public void A_missing_track_is_reported_rather_than_silently_skipped()
    {
        using var game = new SyntheticTrack();
        Directory.Delete(Path.Combine(game.Root, "content", "tracks", "flatpad"), recursive: true);

        VerifyReport report = Verify(game);

        Assert.False(report.Passed);
        Assert.Equal(1, report.ExitCode);
        Assert.True(HasProblem(report, "content/tracks/flatpad does not exist"));
    }
}
