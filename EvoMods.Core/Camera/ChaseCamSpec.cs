namespace EvoMods.Core.Camera;

/// <summary>The four numbers that decide how a chase camera frames the car.</summary>
public enum ChaseCamAxis
{
    /// <summary>Metres above the ground.</summary>
    Height,

    /// <summary>Metres BEHIND the car, positive. Stored as a negative z.</summary>
    Distance,

    /// <summary>Degrees. Negative looks down.</summary>
    Pitch,

    /// <summary>Degrees. ⚠️ One value shared by every car — see <see cref="ChaseCamSpec"/>.</summary>
    Fov,
}

/// <param name="Min">Slider bounds, not the game's. Wide enough to be useless, narrow enough to be safe.</param>
public sealed record ChaseCamKnob(
    ChaseCamAxis Axis,
    string Label,
    string Unit,
    float Min,
    float Max,
    float Step,
    string Note)
{
    /// <summary>The value with its unit attached the way that unit is normally written.</summary>
    public string Format(float value) =>
        Unit == "°" ? $"{value:0.##}°" : $"{value:0.##} {Unit}";
}

/// <summary>
/// Where one chase camera sits, and how wide its lens is.
/// </summary>
/// <remarks>
/// Side offset and yaw are not carried because every preset sets them to 0 and 180, and both were
/// established as drift rather than design: the R34 shipped with x = 0.25, which is 25 cm toward the
/// passenger side on a right-hand-drive car and pushed it off frame centre. The writer sets them
/// explicitly rather than preserving whatever is there, so applying anything here also fixes that.
/// </remarks>
public sealed record ChaseCamView(float Height, float Distance, float Pitch, float Fov)
{
    public float this[ChaseCamAxis axis] => axis switch
    {
        ChaseCamAxis.Height => Height,
        ChaseCamAxis.Distance => Distance,
        ChaseCamAxis.Pitch => Pitch,
        ChaseCamAxis.Fov => Fov,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public ChaseCamView With(ChaseCamAxis axis, float value) => axis switch
    {
        ChaseCamAxis.Height => this with { Height = value },
        ChaseCamAxis.Distance => this with { Distance = value },
        ChaseCamAxis.Pitch => this with { Pitch = value },
        ChaseCamAxis.Fov => this with { Fov = value },
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public bool Matches(ChaseCamView other, float tolerance = 1e-3f) =>
        Enum.GetValues<ChaseCamAxis>().All(a => Math.Abs(this[a] - other[a]) <= tolerance);

    /// <summary>Do these two sit in the same place? Field of view deliberately not consulted.</summary>
    /// <remarks>
    /// For comparing one car in the file against another, where <see cref="Matches"/> would be right
    /// only by accident: field of view is one packed array shared by every car, so two cars can never
    /// differ on it, and including it in the comparison quietly documents the opposite.
    /// <para>
    /// The tolerance matches <see cref="Matches"/>, and has to. <see cref="ChaseCamSpec.Match"/> is
    /// hard-wired to that default, so anything tighter here would separate cars the preset table calls
    /// identical — a screen reporting cars out of line with a preset it claims in the same breath to
    /// be on.
    /// </para>
    /// </remarks>
    public bool SameGeometry(ChaseCamView other, float tolerance = 1e-3f) =>
        Math.Abs(Height - other.Height) <= tolerance
        && Math.Abs(Distance - other.Distance) <= tolerance
        && Math.Abs(Pitch - other.Pitch) <= tolerance;
}

/// <param name="Blurb">What this one is FOR — the reason to pick it over its neighbour.</param>
public sealed record ChaseCamPreset(string Name, string Blurb, ChaseCamView Near, ChaseCamView Far);

/// <summary>
/// The chase camera's GEOMETRY — where it sits, where it points, and how wide the lens is.
/// </summary>
/// <remarks>
/// A third settings file, and not the one <see cref="CameraSpec"/> describes. That one covers
/// BEHAVIOUR — lag and stabilisation — and has no geometry in it at all: <c>horizon_lock</c> is a
/// 0..1 stabilisation blend and cannot move the camera, which is the dead end that led here.
/// <para>
/// ⚠️ FOV IS GLOBAL. One packed array indexed by camera, shared by every car in the file; the
/// drivable path has no per-car FOV field. Geometry is per-car, FOV is all of them or none.
/// </para>
/// <para>
/// ⚠️ Opening the in-game camera settings screen makes the game rewrite this file and discard
/// everything here, exactly as it does for <see cref="CameraSpec.UserFile"/>. Deleting a car's entry
/// makes the game regenerate it from that car's <c>.actor</c> default.
/// </para>
/// <para>
/// Established empirically against build 0.8.1+release.25, cross-checked against the car
/// <c>.actor</c> camera definitions. Re-verify after a game update.
/// </para>
/// </remarks>
public static class ChaseCamSpec
{
    /// <summary>The file the game actually reads. Beside the one <see cref="CameraSpec"/> edits.</summary>
    public static string UserFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games", "ACE", "CarCameras.carcamerausersettings");

    /// <summary>Timestamped copies, kept because the game itself can overwrite this file.</summary>
    /// <remarks>
    /// Beside the file being written rather than beside the real user file — a backup that lands
    /// somewhere other than next to its original is worse than none.
    /// </remarks>
    public static string BackupDirFor(string settingsFile) =>
        Path.Combine(Path.GetDirectoryName(settingsFile)!, "carcameras_backups");

    /// <summary>Camera slots per car, in file order. The index IS the position in the record list.</summary>
    public static readonly string[] CameraNames =
        ["Cockpit", "Dash", "Bonnet", "Bumper", "Near chase", "Far chase"];

    public const int NearChase = 4;
    public const int FarChase = 5;

    /// <summary>How many camera slots each car carries. A car with any other count is not understood.</summary>
    public const int DrivableCameras = 6;

    /// <summary>
    /// The lowest FOV index this code will write.
    /// </summary>
    /// <remarks>
    /// 0..3 are Cockpit, Dash, Bonnet and Bumper — the views you actually drive from. Nothing here
    /// is about them, and widening the cockpit lens by accident is not a mistake you would notice
    /// until you were already in a session.
    /// </remarks>
    public const int FirstWritableFov = 4;

    /// <summary>
    /// The car every preset was measured against, preferred when reporting what a file is set to.
    /// </summary>
    /// <remarks>
    /// A preference, not a rule. When the file's cars disagree it is the framing MOST of them are on
    /// that gets reported, and this car only decides which of that group to quote — because it is not
    /// immune to the drift itself. Opening the in-game camera screen regenerates entries, and a rule
    /// that always spoke for this one car would let a single regenerated R34 misreport the whole file.
    /// </remarks>
    public const string ReferenceCar = "nissan_skyline_r34_gtr";

    // ---- the presets

    /// <summary>
    /// Named framing, from the shipped convention out to the most extreme.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Stock</c> is the shipped FAMILY convention — every Kunos car is 1.80 / 2.50 — and NOT a
    /// byte-exact revert of whatever is in your file. The R34's shipped values were drift, not
    /// design: y = 1.96 / 2.67 inherited from the <c>ks_modded_car</c> donor slot, plus z = -5.190157
    /// and x = 0.25 from dragging the in-game gizmo. For a byte-exact revert, use a backup.
    /// </remarks>
    public static readonly ChaseCamPreset[] Presets =
    [
        new("Stock",
            "The convention every Kunos car ships with. The lens sits well above the roofline, so the "
            + "car sits under the skyline rather than breaking it.",
            new ChaseCamView(1.80f, 5.19f, -5.0f, 80f),
            new ChaseCamView(2.50f, 6.19f, -5.0f, 65f)),

        new("Cinematic",
            "Lowered until the roofline rides the horizon exactly. The safe one: dramatic enough to "
            + "look deliberate, tame enough to drive from.",
            new ChaseCamView(1.35f, 4.40f, -4.0f, 85f),
            new ChaseCamView(1.85f, 5.60f, -3.5f, 70f)),

        new("Aggressive",
            "The closest in and the widest lens, so the car fills the most frame and the world "
            + "distorts past it.",
            new ChaseCamView(1.15f, 3.90f, -3.0f, 95f),
            new ChaseCamView(1.55f, 5.10f, -3.0f, 78f)),

        new("Wide",
            "Aggressive's stance with the widest lens and about 30 cm more room, for when you want "
            + "more of the world in shot.",
            new ChaseCamView(1.15f, 4.20f, -3.0f, 105f),
            new ChaseCamView(1.55f, 5.40f, -3.0f, 88f)),

        new("Hero",
            "The lowest lens of any preset and very nearly level, so the car cuts hard above the "
            + "skyline.",
            new ChaseCamView(0.95f, 4.60f, -0.5f, 90f),
            new ChaseCamView(1.35f, 5.80f, -1.5f, 75f)),
    ];

    public static ChaseCamPreset Stock => Presets[0];

    /// <summary>The preset these two cameras are exactly set to, or null for anything else.</summary>
    public static ChaseCamPreset? Match(ChaseCamView near, ChaseCamView far) =>
        Presets.FirstOrDefault(p => p.Near.Matches(near) && p.Far.Matches(far));

    // ---- the knobs

    public const float MinFov = 20f;
    public const float MaxFov = 130f;

    /// <summary>
    /// What a hand-tuned camera can be moved along, and how far.
    /// </summary>
    /// <remarks>
    /// These bounds are the UI's, not the game's. The FOV pair is the one exception: 20..130 is a
    /// hard refusal in the writer too, because a packed float shared by every car in the file is not
    /// somewhere to discover that 400 was accepted.
    /// </remarks>
    public static readonly ChaseCamKnob[] Knobs =
    [
        new(ChaseCamAxis.Height, "Height", "m", 0.5f, 3.0f, 0.05f,
            "Metres above the ground. Lowering does NOT move the horizon — what it buys is the aim "
            + "landing lower on the car, so the car breaks the skyline instead of sitting under it."),

        new(ChaseCamAxis.Distance, "Distance", "m", 2.0f, 10.0f, 0.05f,
            "Metres behind the car. This, not the lens, is what actually changes how big the car is."),

        new(ChaseCamAxis.Pitch, "Pitch", "°", -15f, 5f, 0.5f,
            "Negative looks down. Pitch and lens are the only two things that move the horizon, and "
            + "steep down-pitch from a low camera aims at tarmac — you cannot have both."),

        new(ChaseCamAxis.Fov, "Field of view", "°", MinFov, MaxFov, 1f,
            "⚠️ Shared by every car in the file. There is no per-car field of view to narrow it to."),
    ];

    // ---- what it will look like

    /// <summary>Roofline of the car the layout figures are quoted against, in metres.</summary>
    /// <remarks>The R34's, which is what every preset was tuned against.</remarks>
    public const float RoofHeight = 1.36f;

    /// <summary>Width of that same car, for the apparent-size figure.</summary>
    public const float CarWidth = 1.785f;

    public const float Aspect = 16f / 9f;

    private const double Rad = Math.PI / 180;

    /// <summary>Where the camera actually points, in metres above the ground.</summary>
    public static float Aim(ChaseCamView v) =>
        (float)(v.Height + v.Distance * Math.Tan(v.Pitch * Rad));

    /// <summary>
    /// How far up the frame a point at this elevation angle lands, as a fraction from the bottom.
    /// </summary>
    /// <remarks>
    /// ⚠️ Reads the stored FOV as VERTICAL. Which axis the game means is still unconfirmed — the
    /// reference prints both readings side by side — but the vertical reading is the one every
    /// published preset figure was quoted against, so it is the one used here. The RATIO between two
    /// presets is nearly identical either way, which is what these numbers are for.
    /// </remarks>
    public static double FrameFraction(double thetaDeg, double pitchDeg, double fovDeg) =>
        0.5 + 0.5 * Math.Tan((thetaDeg - pitchDeg) * Rad) / Math.Tan(fovDeg * Rad / 2);

    /// <summary>
    /// Where the roofline sits relative to the skyline, in percent of frame height.
    /// </summary>
    /// <remarks>
    /// This is the number that decides the look, and the reason this readout exists at all.
    /// "1.15 / 4.20 / -3.0" means nothing on its own; "the roofline rides the horizon" is something a
    /// person can picture. Negative reads observational, around zero the roofline rides the skyline,
    /// positive cuts above it.
    /// <para>
    /// Note it does NOT depend on height: the horizon's own position is a function of pitch and lens
    /// alone. What lowering the camera changes is where the ROOFLINE lands, which is the other half
    /// of this subtraction.
    /// </para>
    /// </remarks>
    public static float RoofVsHorizon(ChaseCamView v)
    {
        double roofTheta = Math.Atan2(RoofHeight - v.Height, v.Distance) / Rad;
        return (float)((FrameFraction(roofTheta, v.Pitch, v.Fov)
                        - FrameFraction(0, v.Pitch, v.Fov)) * 100);
    }

    /// <summary>The car's apparent width as a percentage of frame width — the "zoom" number.</summary>
    public static float CarWidthPercent(ChaseCamView v)
    {
        double hfov = 2 * Math.Atan(Math.Tan(v.Fov * Rad / 2) * Aspect);
        return (float)(100 * (2 * Math.Atan((CarWidth / 2) / v.Distance)) / hfov);
    }

    /// <summary>What that number means, in the four bands the reference measured.</summary>
    public static string Verdict(float roofVsHorizon) => roofVsHorizon switch
    {
        < -4f => "The car sits well below the skyline — observational, looking down onto the roof.",
        < -1f => "The car sits just below the skyline.",
        <= 4f => "The roofline rides the horizon — the car breaks the skyline.",
        _ => "The car cuts above the skyline — very strong, watch for wing occlusion.",
    };

    /// <summary>What this framing actually looks like, in numbers somebody can picture.</summary>
    public static string Feel(ChaseCamView v)
    {
        // The roofline-against-horizon figure is what DECIDES this, and it is still what Verdict is
        // computed from — but "+4.5 points against the horizon" is a unit nobody outside this repo
        // has a feel for, whereas how much of the frame the car fills is one anybody can picture.
        string text = $"{Verdict(RoofVsHorizon(v))} The car fills {CarWidthPercent(v):0.0}% of the frame.";

        // Both of these are the difference between a shot and a mistake, and neither is visible from
        // the numbers on the sliders.
        if (v.Height > RoofHeight + 0.40f)
            text += " ⚠️ The lens is well above the roof — you will see the roof panel.";
        if (Aim(v) < RoofHeight - 0.55f)
            text += " ⚠️ The aim is below the boot lid — the centre of frame is tarmac.";

        return text;
    }
}
