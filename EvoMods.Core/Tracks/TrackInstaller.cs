using System.Security.Cryptography;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;
using EvoMods.Core.Tables;

namespace EvoMods.Core.Tracks;

public enum CustomTrackState
{
    NotInstalled,
    Installed,

    /// <summary>What a game patch or file verification leaves: files survive, rows revert. Reinstall repairs it.</summary>
    FilesPresentButNotRegistered,

    /// <summary>The menus offer a track whose files are gone. Reinstall or uninstall.</summary>
    RegisteredButFilesMissing,
}

/// <summary>Choices the person installing makes; everything else comes from the package.</summary>
/// <param name="DisplayName">Overrides the package's own name.</param>
/// <param name="LayoutCodes">Layout id to the name the menus show for it.</param>
/// <param name="ReplaceExisting">
/// Take over a registration of this same track folder that another tool made. Never extends to a
/// row for any other folder — a base-game track can never be replaced this way.
/// </param>
public sealed record TrackInstallOptions(
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? LayoutCodes = null,
    bool ReplaceExisting = false);

/// <summary>What installing a package would do, and what stops it.</summary>
public sealed record TrackInstallPlan(
    TrackPackage Package,
    string DisplayName,
    IReadOnlyList<LayoutRegistration> Layouts,
    bool IsReinstall,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> MissingReferences,
    bool OtherToolRegistered = false)
{
    public bool CanInstall => Problems.Count == 0;
}

/// <summary>
/// Installs custom tracks into the unpacked game and registers them, and takes them out again.
/// </summary>
/// <remarks>
/// Files go to <c>&lt;game&gt;\content\tracks\&lt;id&gt;</c>, not <c>Saved Games\ACE\mods\</c>: tracks load
/// only from the unpacked game folder. Everything this writes is recorded in the
/// <see cref="TrackLedger"/>, and removal touches only what the ledger records.
/// </remarks>
public sealed class TrackInstaller(string gameRoot, Action<string> log)
{
    private const string StagingSuffix = ".evomods-installing";
    private const string PreviousSuffix = ".evomods-previous";

    private string Rp(string reference) => RefPath.RealPath(gameRoot, reference);

    // ------------------------------------------------------------------ planning

    public TrackInstallPlan Plan(TrackPackage package, TrackInstallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TrackInstallOptions();
        TrackLedger ledger = TrackLedger.Load(gameRoot);
        LedgerTrack? previous = ledger.Find(package.TrackId);
        string name = string.IsNullOrWhiteSpace(options.DisplayName) ? package.DisplayName : options.DisplayName.Trim();

        var problems = new List<string>();
        var warnings = new List<string>(package.Warnings);

        if (!Directory.Exists(Rp("content/tracks")))
            problems.Add("the game is packed — unpack it first; tracks only load from loose files");

        if (previous is null && Directory.Exists(Rp(package.TrackDir)))
        {
            problems.Add($"content\\tracks\\{package.TrackId} already exists and this tool did not install it "
                         + "— it is a base-game track or another tool's, and will not be overwritten");
        }

        if (ledger.Tracks.FirstOrDefault(t => t.DisplayName == name
                && !string.Equals(t.TrackId, package.TrackId, StringComparison.OrdinalIgnoreCase)) is { } twin)
        {
            problems.Add($"'{name}' is already the name of {twin.TrackId}, installed from {Path.GetFileName(twin.Source)}");
        }

        bool otherTool = false;
        if (File.Exists(Rp(TrackRows.TracksTable)))
        {
            foreach (PbNode row in CatalogRowsNamed(name))
            {
                if (previous?.DisplayName == name && TrackRegistrar.IsCatalogRowFor(row, name, package.TrackId))
                    continue;   // our own previous registration
                if (TrackRegistrar.IsCatalogRowFor(row, name, package.TrackId))
                {
                    otherTool = true;
                    if (!options.ReplaceExisting)
                    {
                        problems.Add($"'{name}' is already registered for this track by another tool; "
                                     + "installing will replace that registration — confirm to go ahead");
                    }
                }
                else
                {
                    problems.Add($"'{name}' is already the name of another track in the game — choose a different name");
                }
            }
        }

        var layouts = new List<LayoutRegistration>();
        foreach (PackageLayout l in package.Layouts)
        {
            string code = options.LayoutCodes?.GetValueOrDefault(l.Id)
                          ?? previous?.Layouts.FirstOrDefault(p => p.Id == l.Id)?.Code
                          ?? DefaultLayoutCode(l);
            layouts.Add(new LayoutRegistration(l, code.Trim(), package.GridSize(l)));
        }

        foreach (IGrouping<string, LayoutRegistration> dup in layouts.GroupBy(l => l.Code).Where(g => g.Count() > 1))
            problems.Add($"two layouts would both be called '{dup.Key}'");
        foreach (LayoutRegistration l in layouts.Where(l => l.Code.Length == 0))
            problems.Add($"layout '{l.Layout.Id}' needs a name");

        List<string> missing = Directory.Exists(Rp("content/tracks"))
            ? package.MissingReferences(gameRoot, cancellationToken)
            : [];
        if (missing.Count > 0)
        {
            warnings.Add($"{missing.Count} file(s) the track refers to are in neither the package nor the game "
                         + "— it was built against different content, and may show gaps or fail to load");
        }

        return new TrackInstallPlan(package, name, layouts, previous is not null, problems, warnings, missing, otherTool);
    }

    /// <summary>A layout's default menu name, from its id: <c>gp</c> → <c>GP</c>. Meant to be edited.</summary>
    private static string DefaultLayoutCode(PackageLayout layout) => TrackPackage.Humanize(layout.Id);

    private List<PbNode> CatalogRowsNamed(string name) =>
        TableEditor.TableEntries(PbTree.ParseTree(File.ReadAllBytes(Rp(TrackRows.TracksTable)))).Entries
            .Where(e => TableEditor.RawTextAt(e, 2, 1) == name).ToList();

    // ------------------------------------------------------------------ install

    /// <summary>Install a planned track: files first, then the ledger, then the registries.</summary>
    /// <remarks>
    /// ⚠️ The order is what makes a failure recoverable. Files are staged beside the real folder and
    /// swapped in whole, so a crash mid-copy leaves the previous install (or none) intact. The
    /// ledger is written BEFORE registering, so if registering fails the folder is still recognised
    /// as ours — reported as "files present, not registered", which reinstalling repairs — rather
    /// than as a stranger's folder that must not be touched.
    /// </remarks>
    public LedgerTrack Install(TrackInstallPlan plan, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.CanInstall)
            throw new InstallException(string.Join("\n", plan.Problems));

        TrackPackage package = plan.Package;
        TrackLedger ledger = TrackLedger.Load(gameRoot);
        LedgerTrack? previous = ledger.Find(package.TrackId);
        log($"Installing '{plan.DisplayName}' ({package.TrackId}) from {Path.GetFileName(package.SourcePath)}:");

        string final = Rp(package.TrackDir);
        string staging = final + StagingSuffix;
        string aside = final + PreviousSuffix;
        foreach (string leftover in (string[])[staging, aside])
        {
            if (Directory.Exists(leftover))
                Directory.Delete(leftover, recursive: true);
        }

        Dictionary<string, string> hashes = package.CopyTo(staging, progress, cancellationToken);
        log($"  copied {hashes.Count} files ({GameArchive.Bytes(package.TotalBytes)})"
            + (package.FullyChecksummed ? ", every one matching the package's checksums" : ""));

        if (Directory.Exists(final))
            Directory.Move(final, aside);
        Directory.Move(staging, final);
        if (Directory.Exists(aside))
            Directory.Delete(aside, recursive: true);

        var record = new LedgerTrack(package.TrackId, plan.DisplayName, package.Version,
            Path.GetFullPath(package.SourcePath), DateTimeOffset.Now,
            previous?.Layouts ?? [], hashes);
        ledger.Put(record);
        ledger.Save(gameRoot);

        List<LedgerLayout> layouts = TrackRegistrar.Register(gameRoot, package.TrackId, plan.DisplayName,
            plan.Layouts, Ownership(previous, plan.DisplayName, package.TrackId, replaceByFolder: true), log);

        record = record with { Layouts = layouts };
        ledger.Put(record);
        ledger.Save(gameRoot);

        log("");
        log($"Done. Launch the game (unpacked) → Practice → '{plan.DisplayName}'.");
        return record;
    }

    /// <summary>
    /// The rows a (re)install or uninstall may remove: this track's previous registration, and —
    /// when the plan was allowed to replace one — another tool's registration of the same folder.
    /// </summary>
    /// <remarks>
    /// A session row is ours by name AND recorded id. A catalog row carries no id, so it is ours by
    /// name AND folder — which also means no row pointing at a different folder, such as a base-game
    /// track that happens to share the name, can ever match.
    /// </remarks>
    private static (Func<PbNode, bool> Catalog, Func<PbNode, bool> Session) Ownership(
        LedgerTrack? previous, string name, string trackId, bool replaceByFolder)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { name };
        if (previous is not null)
            names.Add(previous.DisplayName);
        var ids = previous?.RegistryIds.ToHashSet() ?? [];

        return (
            e => names.Any(n => TrackRegistrar.IsCatalogRowFor(e, n, trackId)),
            e => names.Any(n => TableEditor.RawTextAt(e, 8, 10) == n
                                && ((TableEditor.Child(e, 8, 8) is { } id && ids.Contains(id.Varint))
                                    || (replaceByFolder && TrackRegistrar.IsSessionRowFor(e, n, trackId)))));
    }

    /// <summary>
    /// Register an installed track again from the files already on disk, under the names it had.
    /// </summary>
    /// <remarks>
    /// The fix for <see cref="CustomTrackState.FilesPresentButNotRegistered"/>, which is what a game
    /// patch or a Steam file verification leaves: <c>system\</c> reverts, <c>content\tracks\</c> does not.
    /// </remarks>
    public LedgerTrack Repair(string trackId)
    {
        TrackLedger ledger = TrackLedger.Load(gameRoot);
        LedgerTrack track = ledger.Find(trackId)
                            ?? throw new InstallException($"{trackId} was not installed by this tool");
        log($"Re-registering '{track.DisplayName}' ({track.TrackId}) from the files on disk:");

        using TrackPackage installed = TrackPackage.OpenInstalled(gameRoot, trackId);
        List<LayoutRegistration> layouts = installed.Layouts
            .Select(l => new LayoutRegistration(l,
                track.Layouts.FirstOrDefault(p => p.Id == l.Id)?.Code ?? DefaultLayoutCode(l),
                installed.GridSize(l)))
            .ToList();

        List<LedgerLayout> registered = TrackRegistrar.Register(gameRoot, track.TrackId, track.DisplayName,
            layouts, Ownership(track, track.DisplayName, track.TrackId, replaceByFolder: false), log);

        track = track with { Layouts = registered };
        ledger.Put(track);
        ledger.Save(gameRoot);
        log("");
        log("Done.");
        return track;
    }

    // ------------------------------------------------------------------ uninstall

    public void Uninstall(string trackId)
    {
        TrackLedger ledger = TrackLedger.Load(gameRoot);
        LedgerTrack track = ledger.Find(trackId)
                            ?? throw new InstallException($"{trackId} was not installed by this tool — nothing to remove");
        log($"Removing '{track.DisplayName}' ({track.TrackId}):");

        (int catalog, int sessions) = TrackRegistrar.Unregister(gameRoot,
            Ownership(track, track.DisplayName, track.TrackId, replaceByFolder: false));
        log($"  registries: removed {catalog} catalog + {sessions} session row(s)");

        string folder = Rp($"content/tracks/{track.TrackId}");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
            log($"  removed content\\tracks\\{track.TrackId}");
        }

        ledger.Remove(trackId);
        ledger.Save(gameRoot);
        log("");
        log("Done.");
    }

    // ------------------------------------------------------------------ state

    public List<(LedgerTrack Track, CustomTrackState State)> List() =>
        TrackLedger.Load(gameRoot).Tracks.Select(t => (t, DetectState(t))).ToList();

    public CustomTrackState DetectState(LedgerTrack track)
    {
        bool files = File.Exists(Rp($"content/tracks/{track.TrackId}/{track.TrackId}.scene"));
        bool rows = File.Exists(Rp(TrackRows.TracksTable))
                    && CatalogRowsNamed(track.DisplayName).Any(e => TrackRegistrar.IsCatalogRowFor(e, track.DisplayName, track.TrackId))
                    && track.Layouts.Count > 0;
        return (files, rows) switch
        {
            (true, true) => CustomTrackState.Installed,
            (true, false) => CustomTrackState.FilesPresentButNotRegistered,
            (false, true) => CustomTrackState.RegisteredButFilesMissing,
            _ => CustomTrackState.NotInstalled,
        };
    }

    /// <summary>
    /// Check an installed track against its ledger: rows, numbers, containers and file contents.
    /// Returns the problems found; empty means healthy.
    /// </summary>
    public List<string> Verify(LedgerTrack track, bool hashFiles = true)
    {
        var problems = new List<string>();
        int catalog = CatalogRowsNamed(track.DisplayName).Count(e => TrackRegistrar.IsCatalogRowFor(e, track.DisplayName, track.TrackId));
        if (catalog != 1)
            problems.Add($"tracks.table has {catalog} rows for '{track.DisplayName}', expected 1");

        List<PbNode> sessions = TableEditor.TableEntries(
            PbTree.ParseTree(File.ReadAllBytes(Rp(TrackRows.ContainersTable)))).Entries;
        foreach (LedgerLayout l in track.Layouts)
        {
            List<PbNode> mine = sessions.Where(e => TableEditor.RawTextAt(e, 8, 10) == track.DisplayName
                                                    && TableEditor.Child(e, 8, 8)?.Varint == l.RegistryId).ToList();
            foreach (string s in l.Sessions.Where(s => !mine.Any(e => TableEditor.RawTextAt(e, 8, 1) == s)))
                problems.Add($"session '{s}' is not registered");

            foreach (PbNode other in sessions.Except(mine))
            {
                if (TableEditor.Child(other, 8, 8)?.Varint == l.RegistryId)
                    problems.Add($"id {l.RegistryId} is also used by '{TableEditor.RawTextAt(other, 8, 10)}'");
                if (TableEditor.Child(other, 8, 21)?.Varint == l.MenuIndex)
                    problems.Add($"menu index {l.MenuIndex} is also used by '{TableEditor.RawTextAt(other, 8, 10)}'");
            }

            foreach (string c in mine.SelectMany(e => TableEditor.Child(e, 8)!.Find(11))
                         .Select(TableEditor.RawText).OfType<string>().Distinct())
            {
                if (!File.Exists(Rp(c)))
                    problems.Add($"a session loads {c}, which is not on disk");
            }
        }

        if (hashFiles)
        {
            string root = Rp($"content/tracks/{track.TrackId}");
            int bad = 0;
            foreach ((string rel, string hash) in track.Files)
            {
                string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    bad++;
                else
                {
                    using FileStream fs = File.OpenRead(path);
                    if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(fs)), hash, StringComparison.OrdinalIgnoreCase))
                        bad++;
                }
            }

            if (bad > 0)
                problems.Add($"{bad} of {track.Files.Count} installed file(s) are missing or changed");
        }

        return problems.Distinct().ToList();
    }
}
