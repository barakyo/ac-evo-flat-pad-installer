using EvoMods.Core.FlatPad;
using EvoMods.Core.Protobuf;

namespace EvoMods.Core.Camera;

/// <summary>One framing found in the file, and every car sitting on it.</summary>
/// <remarks>
/// A file normally holds exactly one of these. More than one means the cars disagree — see
/// <see cref="ChaseCamReading.Uniform"/> for how that happens without anyone doing anything wrong.
/// </remarks>
public sealed record ChaseCamFraming(
    ChaseCamView Near, ChaseCamView Far, IReadOnlyList<string> Cars)
{
    /// <summary>The preset this framing is set to, or null for anything else.</summary>
    public ChaseCamPreset? Preset => ChaseCamSpec.Match(Near, Far);
}

/// <param name="Cars">Every car key in the file, in file order.</param>
/// <param name="Representative">
/// The car <paramref name="Near"/> and <paramref name="Far"/> were read from, or null when the file
/// is absent.
/// </param>
/// <param name="Drifted">
/// The cars that are NOT on the framing reported here, in file order. Empty for a file that agrees
/// with itself.
/// </param>
/// <param name="Framings">
/// Every distinct framing in the file, the one with the best claim to being the file's first. A file
/// that agrees with itself has exactly one.
/// </param>
/// <param name="Exists">False when there is no user file yet.</param>
public sealed record ChaseCamReading(
    IReadOnlyList<string> Cars,
    ChaseCamView Near,
    ChaseCamView Far,
    string? Representative,
    IReadOnlyList<string> Drifted,
    IReadOnlyList<ChaseCamFraming> Framings,
    bool Exists)
{
    /// <summary>False when the cars do not all agree.</summary>
    /// <remarks>
    /// ⚠️ Not a sign that anything went wrong, and NOT only possible if something other than this app
    /// wrote the file — every write here covers all of them, so this app cannot cause it. THE GAME
    /// causes it: it appends an entry at the car's own <c>.actor</c> default the first time you drive
    /// that car. Every content update therefore adds cars on the shipped framing while the ones
    /// already tuned stay tuned, nothing surfaces it, and the only symptom is one car looking
    /// different in a replay — after the recording exists.
    /// <para>
    /// Confirmed on <c>0.9.0+release.48</c>: the file went 13 to 16 cars, and the three new entries
    /// were exactly the three left on Stock.
    /// </para>
    /// </remarks>
    public bool Uniform => Drifted.Count == 0;

    /// <summary>The preset the framing reported here is set to, or null for anything else.</summary>
    /// <remarks>
    /// Named even when the cars disagree, because refusing to name it is worse: a file with thirteen
    /// of sixteen cars on Wide would read "Custom" while showing Wide's exact numbers. Which cars are
    /// not on it, and what they are on instead, is <see cref="Drifted"/> and <see cref="Framings"/>.
    /// </remarks>
    public ChaseCamPreset? Preset => ChaseCamSpec.Match(Near, Far);

    /// <summary>What a screen shows before it has read anything.</summary>
    public static ChaseCamReading Absent { get; } = new(
        [], ChaseCamSpec.Stock.Near, ChaseCamSpec.Stock.Far, null, [], [], Exists: false);
}

/// <summary>
/// Reads and writes the chase camera's geometry.
/// </summary>
/// <remarks>
/// The file is protobuf, and unlike <see cref="CameraSettings"/>'s flat list of top-level floats the
/// interesting values are four levels down. <see cref="PbTree"/> still carries it: a length-delimited
/// node re-emits its original bytes unless it is dirty, so a vector is written by rebuilding that one
/// payload and marking its ANCESTORS dirty. Everything else in the file — the onboard camera section,
/// the trailers, the four views you drive from — comes back byte-identical.
/// <para>
/// ⚠️ The vector node itself must never be marked dirty. Its <see cref="PbNode.Message"/> is the
/// parse from BEFORE the edit, and re-encoding from it would silently drop the write. Same for the
/// packed FOV array, whose bytes happen to parse as a plausible submessage.
/// </para>
/// <para>
/// This file holds every car you have driven, and all the hand-tuned work on them, which is why it
/// gets a preflight, a structural assert, a backup and a four-part read-back rather than the single
/// verify pass <see cref="CameraSettings"/> uses on six floats.
/// </para>
/// </remarks>
public static class CarCameras
{
    /// <summary>What the file currently says. Never throws for an absent file.</summary>
    public static ChaseCamReading Read(string? path = null)
    {
        path ??= ChaseCamSpec.UserFile;
        if (!File.Exists(path))
            return ChaseCamReading.Absent;

        List<PbNode> top = PbTree.ParseTree(File.ReadAllBytes(path));
        PbNode drivable = Drivable(top);
        float[] fov = PackedFloats(FovArray(drivable));
        List<CarEntry> cars = Cars(drivable);

        // Once per car. Everything below compares every car against every other, and reading a slot
        // walks the protobuf.
        List<(string Key, ChaseCamView Near, ChaseCamView Far)> read = cars
            .Select(c => (
                c.Key,
                Near: ViewOf(c, ChaseCamSpec.NearChase, fov),
                Far: ViewOf(c, ChaseCamSpec.FarChase, fov)))
            .ToList();

        List<List<int>> groups = GroupByFraming(read);

        // Inside a group the reference car speaks for it when it is there, so the numbers on screen
        // stay the ones every preset was measured against; otherwise the earliest car on that framing.
        int Speaker(List<int> group) =>
            group.FirstOrDefault(i => read[i].Key == ChaseCamSpec.ReferenceCar, group[0]);

        var framings = new List<ChaseCamFraming>();
        foreach (List<int> group in groups)
        {
            (_, ChaseCamView groupNear, ChaseCamView groupFar) = read[Speaker(group)];
            framings.Add(
                new ChaseCamFraming(groupNear, groupFar, group.Select(i => read[i].Key).ToList()));
        }

        // By index rather than by key: nothing stops the file carrying the same key twice, and asking
        // whether a NAME is in the settled group would then clear the drift on both of them.
        var settled = new bool[read.Count];
        foreach (int i in groups[0])
            settled[i] = true;

        return new ChaseCamReading(
            read.Select(c => c.Key).ToList(),
            framings[0].Near,
            framings[0].Far,
            read[Speaker(groups[0])].Key,
            read.Where((_, i) => !settled[i]).Select(c => c.Key).ToList(),
            framings,
            Exists: true);
    }

    /// <summary>
    /// The cars gathered by where their chase cameras actually sit, best claim to the file first.
    /// </summary>
    /// <remarks>
    /// The majority wins, because a car the game has just appended arrives at its own default while
    /// the cars that were set deliberately are already there. That is a preference and not a proof —
    /// a file with two hand-tuned cars and fourteen the game added has a majority that is exactly
    /// backwards — which is why the caller is expected to REPORT the disagreement rather than quietly
    /// act on it.
    /// <para>
    /// The tie-breaks, in order after size: a framing matching a known preset beats one at arbitrary
    /// values, which is the reference script's "most common among cars that match a preset" and what
    /// stops one car at garbage values ever speaking for the file; then the group holding
    /// <see cref="ChaseCamSpec.ReferenceCar"/>; then the earliest in the file, which is the oldest,
    /// because entries are APPENDED and so drift arrives at the tail.
    /// </para>
    /// </remarks>
    private static List<List<int>> GroupByFraming(
        List<(string Key, ChaseCamView Near, ChaseCamView Far)> cars)
    {
        var groups = new List<List<int>>();
        for (int i = 0; i < cars.Count; i++)
        {
            // Geometry only. Field of view is one array shared by the whole file, so it is identical
            // on every car by construction and can never tell two of them apart.
            List<int>? hit = groups.FirstOrDefault(g =>
                cars[g[0]].Near.SameGeometry(cars[i].Near)
                && cars[g[0]].Far.SameGeometry(cars[i].Far));

            if (hit is null)
                groups.Add([i]);
            else
                hit.Add(i);
        }

        return groups
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => ChaseCamSpec.Match(cars[g[0]].Near, cars[g[0]].Far) is not null)
            .ThenByDescending(g => g.Any(i => cars[i].Key == ChaseCamSpec.ReferenceCar))
            .ThenBy(g => g[0])
            .ToList();
    }

    /// <summary>
    /// Point every car's near and far chase camera at the same framing. Returns how many changed.
    /// </summary>
    /// <remarks>
    /// All cars, always. The field of view has no per-car field to narrow it to — one packed array
    /// shared by the lot — so narrowing the geometry while the lens moved under every other car would
    /// be the confusing half-measure rather than the safe one.
    /// </remarks>
    /// <param name="guardGameRunning">
    /// Refuse to write while the game is up. A parameter rather than an unconditional check so the
    /// tests can exercise writing without depending on whether the machine running them happens to
    /// have the game open.
    /// </param>
    public static int Write(
        ChaseCamView near, ChaseCamView far, Action<string> log, string? path = null,
        bool guardGameRunning = true)
    {
        path ??= ChaseCamSpec.UserFile;

        if (!File.Exists(path))
        {
            throw new InstallException(
                $"No chase camera file at {path}. Launch the game once so it writes one — this "
                + "edits the file the game already has rather than inventing one.");
        }

        if (guardGameRunning && CameraSettings.RunningGame() is { } game)
        {
            throw new InstallException(
                $"Assetto Corsa EVO is running (PID {game.Id}). It reads this file at startup and "
                + "rewrites it on exit, so close it first.");
        }

        byte[] raw = File.ReadAllBytes(path);
        List<PbNode> top = PbTree.ParseTree(raw);

        // Preflight. If this file cannot survive a parse and rebuild untouched, nothing below can be
        // trusted either, and the right move is to not have opened it.
        if (!PbTree.EncodeTree(top).SequenceEqual(raw))
        {
            throw new InstallException(
                $"{Path.GetFileName(path)} does not round-trip through the protobuf reader "
                + "unchanged, so it is not the format this understands. Nothing was written.");
        }

        PbNode drivable = Drivable(top);
        PbNode fovArray = FovArray(drivable);
        List<CarEntry> cars = Cars(drivable);
        float[] fov = PackedFloats(fovArray);

        Dictionary<string, string> before = Snapshot(cars);
        byte[] onboardBefore = Section(top, 2);
        byte[] trailer3Before = Section(top, 3);
        byte[] trailer4Before = Section(top, 4);

        int changed = 0;
        foreach (CarEntry car in cars)
        {
            changed += Plan(car, ChaseCamSpec.NearChase, near);
            changed += Plan(car, ChaseCamSpec.FarChase, far);
        }

        var fovPlan = new Dictionary<int, float>
        {
            [ChaseCamSpec.NearChase] = near.Fov,
            [ChaseCamSpec.FarChase] = far.Fov,
        };
        int fovChanged = fovPlan.Count(f => Math.Abs(fov[f.Key] - f.Value) > 1e-4f);

        if (changed == 0 && fovChanged == 0)
        {
            log("  nothing to change — the file already says that");
            return 0;
        }

        string backup = Backup(path);

        // Inner to outer: the vectors first, then the FOV array, then mark the spine dirty so every
        // rebuilt payload is picked up on the way out.
        foreach (CarEntry car in cars)
        {
            SetView(car, ChaseCamSpec.NearChase, near);
            SetView(car, ChaseCamSpec.FarChase, far);
        }

        foreach ((int index, float value) in fovPlan)
            SetFov(fovArray, index, value);

        // The last link in the chain, and the one whose absence is silent: an ancestor that is not
        // dirty re-emits the payload it was PARSED from, so leaving this clean writes the file back
        // out unchanged and every check below still passes against the old bytes.
        drivable.Dirty = true;

        File.WriteAllBytes(path, PbTree.EncodeTree(top));

        Verify(path, near, far, before, onboardBefore, trailer3Before, trailer4Before, cars.Count, backup);

        int total = changed + fovChanged;
        log($"  {changed} camera(s) across {cars.Count} car(s) written and verified");
        if (fovChanged > 0)
            log($"  field of view set for all {cars.Count} car(s) — there is no per-car value");
        log($"  backup: {backup}");
        return total;
    }

    /// <summary>Put every car back to the framing the game's own cars ship with.</summary>
    /// <remarks>
    /// The shipped family convention, NOT a byte-exact revert of this file — several cars carry
    /// values that were drift rather than design. A backup is what reverses a specific edit.
    /// </remarks>
    public static int Restore(Action<string> log, string? path = null, bool guardGameRunning = true) =>
        Write(ChaseCamSpec.Stock.Near, ChaseCamSpec.Stock.Far, log, path, guardGameRunning);

    // ---- navigating the file

    private sealed record CarEntry(string Key, PbNode Entry, PbNode Settings);

    /// <summary>The drivable camera collection at top.f1 — the one with the chase cameras in it.</summary>
    private static PbNode Drivable(List<PbNode> top) =>
        top.FirstOrDefault(n => n.Number == 1 && n.Wire == WireType.Len && n.Message is not null)
        ?? throw new InstallException(
            "No drivable camera collection in this file (expected field 1). Nothing was written.");

    /// <summary>The packed float array of one field of view per camera index, shared by all cars.</summary>
    private static PbNode FovArray(PbNode drivable) =>
        drivable.First(1) is { Wire: WireType.Len } node && node.Raw.Length % 4 == 0
            ? node
            : throw new InstallException(
                "No global field-of-view array in this file (expected field 1.1). Nothing was written.");

    private static float[] PackedFloats(PbNode node)
    {
        var values = new float[node.Raw.Length / 4];
        for (int i = 0; i < values.Length; i++)
            values[i] = BitConverter.ToSingle(node.Raw, i * 4);
        return values;
    }

    private static List<CarEntry> Cars(PbNode drivable)
    {
        var cars = new List<CarEntry>();
        foreach (PbNode entry in drivable.Find(2))
        {
            if (entry.Message is null)
                continue;
            PbNode? key = entry.First(1);
            PbNode? settings = entry.First(2);
            if (key is null || settings?.Message is null)
                continue;

            string name = key.Text ?? System.Text.Encoding.UTF8.GetString(key.Raw);

            // Same reasoning as the top-level preflight, one level down: a car whose settings do not
            // rebuild unchanged is one whose neighbours would be rewritten from a bad parse.
            if (!PbTree.EncodeTree(settings.Message).SequenceEqual(settings.Raw))
            {
                throw new InstallException(
                    $"The camera settings for '{name}' do not round-trip unchanged. Nothing was written.");
            }

            cars.Add(new CarEntry(name, entry, settings));
        }

        if (cars.Count == 0)
            throw new InstallException("No cars in this file. Nothing was written.");

        return cars;
    }

    /// <summary>The position and angle records for one camera slot of one car.</summary>
    private static (PbNode Position, PbNode Angles) Slot(CarEntry car, int index)
    {
        List<PbNode> positions = car.Settings.Find(1);
        List<PbNode> angles = car.Settings.Find(2);
        if (positions.Count != ChaseCamSpec.DrivableCameras || angles.Count != ChaseCamSpec.DrivableCameras)
        {
            throw new InstallException(
                $"'{car.Key}' has {positions.Count} camera position(s) and {angles.Count} angle(s), "
                + $"expected {ChaseCamSpec.DrivableCameras} of each. Nothing was written.");
        }

        return (positions[index], angles[index]);
    }

    /// <summary>
    /// One camera's geometry, with the file's own conventions undone.
    /// </summary>
    /// <remarks>z is negative behind the car, so the distance a person tunes is its negation.</remarks>
    private static ChaseCamView ViewOf(CarEntry car, int index, float[] fov)
    {
        (PbNode position, PbNode angles) = Slot(car, index);
        float[] xyz = Vector(position, 3);
        float[] pitchYaw = Vector(angles, 2);
        return new ChaseCamView(xyz[1], -xyz[2], pitchYaw[0], index < fov.Length ? fov[index] : 0f);
    }

    /// <summary>Fixed32 floats at fields 1..n. Absent components are zero — the game omits them.</summary>
    private static float[] Vector(PbNode node, int components)
    {
        var values = new float[components];
        foreach (PbNode child in node.Message ?? [])
        {
            if (child.Wire != WireType.I32 || child.Number < 1 || child.Number > components)
                throw new InstallException($"Unexpected field {child.Number} in a camera vector.");
            values[child.Number - 1] = BitConverter.ToSingle(child.Raw, 0);
        }

        return values;
    }

    // ---- writing

    /// <summary>Would this slot actually move? Counted before anything is touched.</summary>
    private static int Plan(CarEntry car, int index, ChaseCamView view)
    {
        (PbNode position, PbNode angles) = Slot(car, index);
        float[] xyz = Vector(position, 3);
        float[] pitchYaw = Vector(angles, 2);

        bool same = Math.Abs(xyz[0] - 0f) <= 1e-5f
            && Math.Abs(xyz[1] - view.Height) <= 1e-5f
            && Math.Abs(xyz[2] - -view.Distance) <= 1e-5f
            && Math.Abs(pitchYaw[0] - view.Pitch) <= 1e-5f
            && Math.Abs(pitchYaw[1] - Yaw) <= 1e-5f;

        return same ? 0 : 1;
    }

    /// <summary>Facing the car. Anything else is a camera pointed at the scenery.</summary>
    private const float Yaw = 180f;

    private static void SetView(CarEntry car, int index, ChaseCamView view)
    {
        (PbNode position, PbNode angles) = Slot(car, index);

        // x is 0 deliberately, not preserved: a non-zero side offset is what the in-game gizmo leaves
        // behind, and on a right-hand-drive car it pushes the subject off frame centre.
        position.Raw = EncodeVector(0f, view.Height, -view.Distance);
        angles.Raw = EncodeVector(view.Pitch, Yaw);

        // The payloads above are now the truth; the spine has to be re-encoded to carry them, and the
        // vectors themselves left clean so they emit exactly what was just built.
        car.Settings.Dirty = true;
        car.Entry.Dirty = true;
    }

    /// <summary>
    /// Fixed32 floats at fields 1..n, with exact zeros omitted.
    /// </summary>
    /// <remarks>
    /// Mirrors the game's own encoder, which is why a chase position record is 15 bytes when x is
    /// set and 10 when it is not. Writing the zeros instead would still be valid protobuf and would
    /// still read back correctly — it would just no longer be byte-identical to a file the game
    /// wrote, which is the only thing making a diff of this file meaningful.
    /// </remarks>
    private static byte[] EncodeVector(params float[] components)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i] == 0f)
                continue;
            bytes.AddRange(PbTree.EncodeVarint(((ulong)(i + 1) << 3) | WireType.I32));
            bytes.AddRange(BitConverter.GetBytes(components[i]));
        }

        return bytes.ToArray();
    }

    private static void SetFov(PbNode array, int index, float value)
    {
        if (index < ChaseCamSpec.FirstWritableFov)
        {
            throw new InstallException(
                $"Refusing to write field of view {index} — 0 to 3 are "
                + $"{string.Join(", ", ChaseCamSpec.CameraNames[..ChaseCamSpec.FirstWritableFov])}, "
                + "the views you actually drive from.");
        }

        if (index * 4 >= array.Raw.Length)
        {
            throw new InstallException(
                $"Field of view {index} is past the end of an array with "
                + $"{array.Raw.Length / 4} entr(ies). Nothing was written.");
        }

        if (value < ChaseCamSpec.MinFov || value > ChaseCamSpec.MaxFov)
        {
            throw new InstallException(
                $"Field of view {value:0.##} is outside {ChaseCamSpec.MinFov:0}..{ChaseCamSpec.MaxFov:0}.");
        }

        byte[] updated = (byte[])array.Raw.Clone();
        BitConverter.GetBytes(value).CopyTo(updated, index * 4);

        // Assign Raw and leave this node clean: these bytes are a packed float array that happens to
        // parse as a plausible submessage, so re-encoding from Message would emit that misreading.
        array.Raw = updated;
    }

    // ---- checking the work

    /// <summary>Every car's every camera slot, as text, for the nothing-else-moved assertion.</summary>
    private static Dictionary<string, string> Snapshot(List<CarEntry> cars)
    {
        var slots = new Dictionary<string, string>();
        foreach (CarEntry car in cars)
        {
            for (int i = 0; i < ChaseCamSpec.DrivableCameras; i++)
            {
                (PbNode position, PbNode angles) = Slot(car, i);
                float[] xyz = Vector(position, 3);
                float[] pitchYaw = Vector(angles, 2);
                slots[$"{car.Key}|{i}"] = string.Join(
                    ",", xyz.Concat(pitchYaw).Select(v => v.ToString("R")));
            }
        }

        return slots;
    }

    private static byte[] Section(List<PbNode> top, int number)
    {
        PbNode? node = top.FirstOrDefault(n => n.Number == number);
        return node is null ? [] : node.Wire == WireType.Len ? node.Raw : BitConverter.GetBytes(node.Varint);
    }

    /// <summary>
    /// Read the file back and prove all four things: the edit landed, and nothing else did.
    /// </summary>
    /// <remarks>
    /// A camera that is silently wrong is not noticed until a replay has already been recorded, and
    /// the untouched-region checks are the ones that matter most — the four views you drive from and
    /// the onboard section share this file and nothing here is about them.
    /// </remarks>
    private static void Verify(
        string path, ChaseCamView near, ChaseCamView far, Dictionary<string, string> before,
        byte[] onboard, byte[] trailer3, byte[] trailer4, int carCount, string backup)
    {
        List<PbNode> top = PbTree.ParseTree(File.ReadAllBytes(path));
        PbNode drivable = Drivable(top);
        float[] fov = PackedFloats(FovArray(drivable));
        List<CarEntry> cars = Cars(drivable);

        void Fail(string what) =>
            throw new InstallException($"Verify failed: {what}. Restore from {backup}.");

        if (cars.Count != carCount)
            Fail($"the file went from {carCount} car(s) to {cars.Count}");

        foreach (CarEntry car in cars)
        {
            foreach ((int index, ChaseCamView want) in
                     new[] { (ChaseCamSpec.NearChase, near), (ChaseCamSpec.FarChase, far) })
            {
                ChaseCamView got = ViewOf(car, index, fov);
                if (!got.Matches(want, 1e-4f))
                {
                    Fail($"'{car.Key}' {ChaseCamSpec.CameraNames[index].ToLowerInvariant()} reads "
                        + $"{got.Height:0.###}/{got.Distance:0.###}/{got.Pitch:0.##} at {got.Fov:0.#}°, "
                        + $"wrote {want.Height:0.###}/{want.Distance:0.###}/{want.Pitch:0.##} "
                        + $"at {want.Fov:0.#}°");
                }
            }
        }

        Dictionary<string, string> after = Snapshot(cars);
        foreach ((string slot, string was) in before)
        {
            int index = int.Parse(slot[(slot.LastIndexOf('|') + 1)..]);
            if (index == ChaseCamSpec.NearChase || index == ChaseCamSpec.FarChase)
                continue;
            if (!after.TryGetValue(slot, out string? now))
                Fail($"camera slot {slot} disappeared");
            else if (now != was)
                Fail($"camera slot {slot} changed, and nothing here is about it (was {was}, now {now})");
        }

        if (!Section(top, 2).SequenceEqual(onboard))
            Fail("the onboard camera section changed");
        if (!Section(top, 3).SequenceEqual(trailer3) || !Section(top, 4).SequenceEqual(trailer4))
            Fail("a trailing field changed");
    }

    /// <summary>Copy the file aside, named for when it was last written.</summary>
    /// <remarks>
    /// Keyed on the file's own timestamp rather than now, so re-running without the game having
    /// touched the file in between does not pile up identical copies.
    /// </remarks>
    private static string Backup(string path)
    {
        string dir = ChaseCamSpec.BackupDirFor(path);
        Directory.CreateDirectory(dir);
        string name = $"CarCameras.{File.GetLastWriteTime(path):yyyyMMdd-HHmmss}.bak";
        string backup = Path.Combine(dir, name);
        if (!File.Exists(backup))
            File.Copy(path, backup);
        return backup;
    }
}
