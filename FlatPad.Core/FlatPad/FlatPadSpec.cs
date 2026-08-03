namespace FlatPad.Core.FlatPad;

/// <summary>
/// The Flat Pad recipe: a 1.5 km dead-flat, wall-free, scenery-free pad derived from Sebring's
/// stock files, registered as its own track so no base-game content is modified.
/// </summary>
/// <remarks>
/// Port of the constants block of <c>tracks/install_flatpad.py</c>. Every value here is
/// load-bearing and was arrived at the hard way — see <c>tracks/FLAT_TEST_TRACK.md</c> §10.
/// </remarks>
public static class FlatPadSpec
{
    // ----------------------------------------------------------------- identity
    /// <summary>Donor track: the flattest base circuit (a former airfield).</summary>
    public const string Src = "sebring";

    public const string SrcSceneName = "sebring.scene";
    public const string SrcTrackName = "sebring.track";
    public const string SrcDisplay = "Sebring International Raceway";

    /// <summary>Donor container-file suffix (<c>layout_gp.scene</c>, …).</summary>
    public const string Layout = "gp";

    public const string Dst = "flatpad";
    public const string DstDisplay = "Flat Pad";

    /// <summary><c>[2.3.8.14]</c> — with <see cref="DstDisplay"/> this derives the UI tile slug.</summary>
    public const string LayoutCode = "GP";

    /// <summary>
    /// Lowest id we will take in <c>track_containers.table</c> <c>[8.8]</c>.
    /// </summary>
    /// <remarks>
    /// <c>.8</c> is per-(track, layout) — all of Sebring's session entries share one. The base
    /// game's ids topped out at 25947, so starting above 26000 keeps ours clear of that block. The
    /// actual id is still allocated above whatever the live table holds; this is only a floor, so
    /// an update growing into our range cannot collide.
    ///
    /// ⚠️ Nothing here may be a fixed number. <c>[8.21]</c> is a DENSE global index over
    /// (track, layout) pairs, and a hardcoded 37 — correct when Sebring held the maximum of 36 —
    /// silently displaced Kyalami in the menus the moment v0.8.1 added it at 37.
    /// </remarks>
    public const ulong NewTrackIdFloor = 26001;

    /// <summary>
    /// Session entries to register, by their donor name in <c>track_containers.table</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ "GP Time Attack" is what PRACTICE reads — confirmed twice: every one of the 38 base
    /// track/layouts has a Time Attack entry, and <c>GameModeType_PRACTICE.gamemodesave</c> stores
    /// the chosen session as <c>[3.5.6] = 'GP Time Attack'</c>. Registering Hotstint alone leaves
    /// the track invisible in the Practice track list, silently. "No Game Mode" is the
    /// TV-camera/replay entry rather than a playable session, kept because the physics harness
    /// reads replays. "GP Race" is left out to keep the registration minimal — the spawns are
    /// already laid out for it if it is ever wanted.
    /// </remarks>
    public static readonly string[] Sessions = ["GP Time Attack", "GP Hotstint", "No Game Mode"];

    /// <summary>
    /// The session kinds a name can end in. A kind is a SUFFIX after the layout code
    /// ("GP Time Attack", "Layout 3A Hotstint", "Nordschleife Race").
    /// </summary>
    public static readonly string[] SessionKinds = ["Time Attack", "Hotstint", "Race", "No Game Mode"];

    /// <summary>
    /// Kinds verification treats as mandatory. All four above are on essentially every base track,
    /// but "Race" is a deliberate omission here — the rest are load-bearing for the UI.
    /// </summary>
    public static readonly string[] RequiredSessionKinds = ["Time Attack", "No Game Mode"];

    // ----------------------------------------------------------------- pad geometry (Sebring coords)
    /// <summary>The surface the car spawns on. It is one of the donor's own meshes, so it collides.</summary>
    public const string PadMesh = "concrete_in007.mesh";

    // ⚠️ Every geometry constant here is a DOUBLE, and all derived arithmetic stays in double until
    // the moment a value is packed as a 32-bit float. 18.6 is not representable in binary, so
    // `(float)18.6 + 0.3f` and `(float)(18.6 + 0.3)` land on different bits — and the reference
    // implementation computes in Python floats, which are doubles. Anything else silently produces
    // an install that is *almost* byte-identical.

    /// <summary>Flat plane height, just above the pit surface.</summary>
    public const double PadY = 18.6;

    /// <summary>Target X and Z extent of the pad, in metres.</summary>
    public const double PadSizeM = 1500.0;

    /// <summary>Pit-cluster centre; spawns land here, on the pad.</summary>
    public const double SpawnX = -219.0;

    public const double SpawnZ = 164.0;

    /// <summary>
    /// Spawn containers to relocate onto the pad, as (filename, grid spread in metres). Every
    /// session type reads a DIFFERENT one — Hotstint/Time Attack the hotlap or pitlane file, Race
    /// the grid — so all of them must move, or that mode spawns at the donor circuit and drops off
    /// the pad edge.
    /// </summary>
    public static readonly (string File, double Spread)[] SpawnFiles =
    [
        ($"spawnpoints_pitlane_{Layout}.scene", 0.0),
        ($"spawnpoints_hotlap_{Layout}.scene", 0.0),
        ($"spawnpoints_grid_{Layout}.scene", 8.0),
    ];

    // ----------------------------------------------------------------- pitlane zones
    /// <summary>
    /// The one pit zone reshaped around the spawn. Session start REQUIRES a zone there: start
    /// teleports the car to its box, which fires "entered to pitlane", which opens the pitlane page.
    /// With no zone at the spawn none of that fires and the session hangs on a dead loading screen.
    /// The donor's other three lie right across the pad, so they are exiled — crossing one
    /// teleports the car to the pits mid-test.
    /// </summary>
    public const string PitZoneKeep = $"pitlane_zone_main_{Layout}";

    /// <summary>Its half-extent: big enough to hold the car reliably, small enough to avoid.</summary>
    public const double PitBoxRadiusM = 30.0;

    /// <summary>XZ offset for the rest — well beyond the 1.5 km pad.</summary>
    public const double PitZoneExileX = 100000.0;

    public const double PitZoneExileZ = 100000.0;

    // ----------------------------------------------------------------- layout splines
    /// <summary>
    /// False keeps the donor's circuit splines verbatim.
    /// </summary>
    /// <remarks>
    /// The game derives "which way is forward" from the layout splines, and the donor's still trace
    /// its real circuit — so across the pad that direction field points every which way and ordinary
    /// driving reads as reverse, which escalates to a disqualification. Penalties are configured
    /// globally per game mode, so a track cannot switch them off. What a track CAN do is choose a
    /// direction field that is hard to oppose: one big oval CENTRED ON THE SPAWN makes every radial
    /// line out of the spawn perpendicular to the loop, so driving away in any direction reads as
    /// neither forward nor reverse.
    /// </remarks>
    // Deliberately not `const`: it is an escape hatch worth keeping compilable, and folding it away
    // turns the other branch into an "unreachable code" error.
    public static readonly bool ReshapeLayout = true;

    /// <summary>1.0 = circle (the pad is square); &gt;1 stretches X.</summary>
    public const double OvalAspect = 1.0;

    /// <summary>Spline radii from the spawn, in metres. Left/right are resolved from the donor.</summary>
    public static readonly (string Name, double Radius)[] OvalRadii =
    [
        ($"{Layout}_track_limits_left", 650.0),   // OUTER edge
        ($"{Layout}_center_spline", 430.0),       // the racing line
        ($"{Layout}_ideal_line", 430.0),
        ($"{Layout}_track_limits_right", 150.0),  // INNER edge; the pit box sits inside it
    ];

    public static double? OvalRadius(string name)
    {
        foreach ((string n, double r) in OvalRadii)
        {
            if (n == name)
                return r;
        }

        return null;
    }

    // ----------------------------------------------------------------- files & paths
    /// <summary>Loaded by folder convention rather than by reference — seed the closure with them.</summary>
    public static readonly string[] ConventionFiles =
    [
        SrcTrackName,
        $"layouts/layout_{Layout}.trackcontrolpoints",
        $"layouts/layout_{Layout}.ideal_line.aisplinedata",
        $"layouts/layout_{Layout}.pitlane.aisplinedata",
        $"dynamic_track/{Layout}.dynamictrackpresetcompressed",
    ];

    /// <summary>
    /// Tracks swept for leftover <c>.orig</c> snapshots before deriving anything: the donor only.
    /// </summary>
    /// <remarks>
    /// This tool never writes to a base-game track — it reads the donor and writes to
    /// <c>content\tracks\flatpad</c>. The sweep exists because an EARLIER, destructive builder
    /// modified the donor in place and left <c>.orig</c> snapshots beside the files it replaced.
    /// Restoring those first is what guarantees the track is always derived from stock.
    ///
    /// The Python reference also lists <c>paul_ricard</c>, because its <c>--pr-runoff-spawn</c>
    /// flag moved a spawn on that track. That flag is deliberately not ported — editing a base-game
    /// track is the one thing this tool exists to avoid — so carrying the name here would describe
    /// one machine's history rather than anything this tool does.
    /// </remarks>
    public static readonly string[] SweptForSnapshots = [Src];

    public const string SrcDir = $"content/tracks/{Src}";
    public const string DstDir = $"content/tracks/{Dst}";
    public const string TracksTable = "system/tracks.table";
    public const string ContainersTable = "system/track_containers.table";

    // ----------------------------------------------------------------- UI tiles
    /// <summary>
    /// The UI tile id the game derives from the track name + layout. From
    /// <c>uiresources/js/components.js</c>:
    /// <c>[name.replace("-","_"), layout].join("-").replaceAll(" ","_").toLowerCase()</c>.
    /// The filename is the ENTIRE binding — nothing registers these.
    /// </summary>
    public static string UiSlug() => Slug(DstDisplay);

    public static string SrcUiSlug() => Slug(SrcDisplay);

    private static string Slug(string display) =>
        $"{display.Replace("-", "_")}-{LayoutCode}".Replace(" ", "_").ToLowerInvariant();

    /// <summary>(source, destination) ref pairs for the menu thumbnail + track map.</summary>
    public static List<(string Src, string Dst)> UiTilePairs()
    {
        var outPairs = new List<(string, string)>();
        (string Folder, string[] Exts)[] groups =
        [
            ("tracks", [".texture", ".texturemips"]),
            ("trackmaps", [".svg", ".texture", ".texturemips"]),
        ];
        foreach ((string folder, string[] exts) in groups)
        {
            foreach (string ext in exts)
            {
                outPairs.Add(($"uiresources/images/{folder}/{SrcUiSlug()}{ext}",
                    $"uiresources/images/{folder}/{UiSlug()}{ext}"));
            }
        }

        return outPairs;
    }
}
