using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Tracks;

// Development entry point. Not shipped — the distributable is the WinForms app; this exists so the
// logic can be exercised, and diffed against the Python reference implementation, without a GUI.

const string Usage = """
    Flat Pad Installer — dev CLI

      status                     where the game is, and how it reads its content
      install                    build + register the track (idempotent)
      uninstall                  remove it and restore stock content
      verify                     check an existing install (read-only)
      repair                     restore base-game registry entries that have gone missing
      unpack                     unpack the game archive so loose tracks load
      revert [--archive F]       put the archive back (--archive picks one if several exist)
                                 (--archive also names the stock reference for verify/repair)
      check-unpack [--archive F] sample the loose files against the archive they came from
      dump-track-rows [--name N] a track's rows in both registries (no name: list the catalog)

    Custom tracks — a .zip or folder holding content\tracks\<id>\:

      inspect-track --input F    what installing it would do, without doing it
      install-track --input F [--name N] [--layout-name N] [--replace]
                                 install + register (--replace takes over another tool's
                                 registration of the same track folder)
      uninstall-track --id ID    remove one this tool installed
      list-tracks                every track this tool installed, checked

    Standalone packages — a car mod, or any .kspkg. These need no game folder:

      inspect-package --input F  what is inside a .kspkg, without extracting it
      unpack-package  --input F --out DIR
                                 extract a .kspkg into a folder, changing nothing else

    --game <path>   the Assetto Corsa EVO folder. Auto-detected from Steam if omitted.

    """;

if (args.Length == 0)
{
    Console.Error.Write(Usage);
    return 2;
}

string command = args[0];
string? gameRoot = null;
string? archive = null;
string? input = null;
string? outDir = null;
string? name = null;
string? layoutName = null;
string? trackId = null;
bool replace = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--game" when i + 1 < args.Length:
            gameRoot = args[++i];
            break;
        case "--archive" when i + 1 < args.Length:
            archive = args[++i];
            break;
        case "--input" when i + 1 < args.Length:
            input = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outDir = args[++i];
            break;
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--layout-name" when i + 1 < args.Length:
            layoutName = args[++i];
            break;
        case "--id" when i + 1 < args.Length:
            trackId = args[++i];
            break;
        case "--replace":
            replace = true;
            break;
        default:
            Console.Error.WriteLine($"unrecognised argument: {args[i]}");
            Console.Error.Write(Usage);
            return 2;
    }
}

// Ctrl+C stops long work cleanly rather than killing it mid-file.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\ncancelling…");
    cancel.Cancel();
};

// ⚠️ Dispatched before the game-root resolution below, which gives up when no install is found. A
// standalone package has nothing to do with the game, and refusing to read a car mod because Steam
// is missing would be nonsense.
if (command is "inspect-package" or "unpack-package")
    return Package(command, input, outDir, cancel.Token);

if (gameRoot is null)
{
    gameRoot = GameLocator.Find();
    if (gameRoot is null)
    {
        Console.Error.WriteLine("could not find Assetto Corsa EVO — pass --game <path>");
        return 2;
    }

    Console.Error.WriteLine($"auto-detected: {gameRoot}");
}

try
{
    switch (command)
    {
        case "status":
            return Status(gameRoot);

        case "verify":
            RequireUnpackedContent(gameRoot);
            VerifyReport report = new Verifier(gameRoot,
                archive is not null ? new ArchiveStockRegistry(archive) : null).Run();
            Console.Out.Write(report.Render());
            return report.ExitCode;

        case "repair":
        {
            RequireUnpackedContent(gameRoot);
            // Detect and repair in one pass: the diff carries the stock entry nodes, so restoring
            // them needs no second look at the archive and cannot disagree with what was reported.
            RegistryDiff diff = RegistryIntegrity.Compare(gameRoot,
                archive is not null ? new ArchiveStockRegistry(archive) : ArchiveStockRegistry.ForGame(gameRoot));
            new Installer(gameRoot, Console.Out.WriteLine).RepairRegistry(diff);
            return 0;
        }

        case "install":
            RequireUnpackedContent(gameRoot);
            new Installer(gameRoot, Console.Out.WriteLine).Install();
            return 0;

        case "uninstall":
            RequireUnpackedContent(gameRoot);
            new Installer(gameRoot, Console.Out.WriteLine).Uninstall();
            return 0;

        case "dump-track-rows":
            RequireUnpackedContent(gameRoot);
            foreach (string line in name is null ? TrackRows.CatalogNames(gameRoot) : TrackRows.Describe(gameRoot, name))
                Console.WriteLine(line);
            return 0;

        case "inspect-track":
        case "install-track":
        {
            if (input is null)
            {
                Console.Error.WriteLine($"{command} needs --input <file.zip | folder>");
                return 2;
            }

            using TrackPackage package = TrackPackage.Open(input);
            if (layoutName is not null && package.Layouts.Count != 1)
            {
                Console.Error.WriteLine($"--layout-name needs a single-layout track; this one has {package.Layouts.Count}");
                return 2;
            }

            var installer = new TrackInstaller(gameRoot, Console.Out.WriteLine);
            TrackInstallPlan plan = installer.Plan(package, new TrackInstallOptions(name,
                layoutName is null ? null : new Dictionary<string, string> { [package.Layouts[0].Id] = layoutName },
                replace), cancel.Token);
            PrintPlan(plan);
            if (!plan.CanInstall)
                return 1;
            if (command == "inspect-track")
                return 0;

            Console.WriteLine();
            installer.Install(plan, new InlineProgress<(int Done, int Total)>(p =>
                Console.Write($"\rCopying… {p.Done:N0}/{p.Total:N0} files   ")), cancel.Token);
            return 0;
        }

        case "uninstall-track":
            if (trackId is null)
            {
                Console.Error.WriteLine("uninstall-track needs --id <track id>");
                return 2;
            }

            new TrackInstaller(gameRoot, Console.Out.WriteLine).Uninstall(trackId);
            return 0;

        case "list-tracks":
        {
            var installer = new TrackInstaller(gameRoot, Console.Out.WriteLine);
            List<(LedgerTrack Track, CustomTrackState State)> tracks = installer.List();
            if (tracks.Count == 0)
                Console.WriteLine("no custom tracks installed");
            int failures = 0;
            foreach ((LedgerTrack t, CustomTrackState state) in tracks)
            {
                Console.WriteLine($"{t.DisplayName}  ({t.TrackId}{(t.Version is null ? "" : $" {t.Version}")})  {state}");
                foreach (LedgerLayout l in t.Layouts)
                    Console.WriteLine($"  '{l.Code}'  id {l.RegistryId}  index {l.MenuIndex}  {string.Join(", ", l.Sessions)}");
                List<string> problems = state == CustomTrackState.Installed ? installer.Verify(t) : [];
                foreach (string p in problems)
                    Console.WriteLine($"  FAIL {p}");
                if (state == CustomTrackState.Installed && problems.Count == 0)
                    Console.WriteLine($"  PASS rows, numbers, containers and {t.Files.Count} file hashes");
                failures += problems.Count + (state == CustomTrackState.Installed ? 0 : 1);
            }

            return failures == 0 ? 0 : 1;
        }

        case "unpack":
        {
            int files = GameArchive.Unpack(gameRoot, Bar("Unpacking"), cancel.Token);
            Console.WriteLine($"\nUnpacked {files:N0} files. The game now reads loose content.");
            return 0;
        }

        case "revert":
            if (archive is not null)
                GameArchive.RevertToPacked(gameRoot, archive);
            else
                GameArchive.RevertToPacked(gameRoot);
            Console.WriteLine("Archive restored. The game reads packed content again.");
            Console.WriteLine("The unpacked files are still on disk; delete content/ by hand to reclaim the space.");
            return 0;

        case "check-unpack":
        {
            GameArchiveState state = GameArchive.Detect(gameRoot);
            archive ??= state.DisabledPackages.Count == 1 ? state.DisabledPackages[0] : null;
            if (archive is null)
            {
                Console.Error.WriteLine(state.DisabledPackages.Count == 0
                    ? "no renamed-aside archive to compare against — pass --archive <file>"
                    : $"{state.DisabledPackages.Count} archives present, pass --archive to pick one:\n  "
                      + string.Join("\n  ", state.DisabledPackages));
                return 2;
            }

            GameArchive.UnpackCheck check = GameArchive.CheckUnpack(gameRoot, archive,
                progress: Bar("Checking"), cancellationToken: cancel.Token);
            Console.WriteLine($"\nchecked {check.Checked} of {check.Total} files from "
                              + $"{Path.GetFileName(archive)}");
            Console.WriteLine($"  missing on disk: {check.Missing.Count}");
            Console.WriteLine($"  different bytes: {check.Different.Count}");
            foreach (string p in check.Missing.Take(5))
                Console.WriteLine($"    missing:   {p}");
            foreach (string p in check.Different.Take(5))
                Console.WriteLine($"    different: {p}");
            Console.WriteLine(check.Ok ? "\nPASS" : "\nFAIL");
            return check.Ok ? 0 : 1;
        }

        default:
            Console.Error.WriteLine($"unknown command: {command}");
            Console.Error.Write(Usage);
            return 2;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled — nothing was left half-applied.");
    return 130;
}
catch (Exception e) when (e is InstallException or GameArchiveException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

/// <summary>
/// Read or extract a standalone package. Self-contained down to the exit code, because it runs
/// outside the try that wraps every game command.
/// </summary>
static int Package(string command, string? input, string? outDir, CancellationToken cancel)
{
    if (input is null)
    {
        Console.Error.WriteLine($"{command} needs --input <file.kspkg>");
        return 2;
    }

    try
    {
        PackageInfo info = PackageUnpacker.Inspect(input);
        Console.WriteLine($"Package  {Path.GetFileName(info.PackagePath)} "
                          + $"({GameArchive.Bytes(new FileInfo(info.PackagePath).Length)})");
        Console.WriteLine($"Holds    {info.Files.Count:N0} files, "
                          + $"{GameArchive.Bytes(info.TotalBytes)} unpacked"
                          + (info.DirectoryCount > 0 ? $"  ({info.DirectoryCount:N0} folders)" : ""));
        Console.WriteLine($"Under    {(info.CommonRoot.Length > 0 ? info.CommonRoot : "(no shared folder)")}");

        if (command == "inspect-package")
            return 0;

        if (outDir is null)
        {
            Console.Error.WriteLine("unpack-package needs --out <dir>");
            return 2;
        }

        UnpackedPackage result = PackageUnpacker.Unpack(info, outDir, Bar("Unpacking"), cancel);
        Console.WriteLine($"\nWrote {result.Written:N0} files ({GameArchive.Bytes(result.BytesWritten)}) "
                          + $"to {Path.GetFullPath(outDir)}");
        if (result.Ok)
            return 0;

        // Never silent: an entry the archive lists but that was not written is either a corrupt
        // package or a hostile one, and both matter more than the files that did come out.
        Console.Error.WriteLine($"{result.Refused.Count:N0} entries were refused:");
        foreach (string path in result.Refused.Take(10))
            Console.Error.WriteLine($"  {path}");
        return 1;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\ncancelled — every file written is complete, and the package is untouched.");
        return 130;
    }
    catch (Exception e) when (e is GameArchiveException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

static int Status(string gameRoot)
{
    Console.WriteLine($"Game     {gameRoot}");
    List<string> all = GameLocator.FindAll();
    if (all.Count > 1)
    {
        foreach (string other in all.Where(p => !string.Equals(p, gameRoot, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"         (also found: {other})");
    }

    GameArchiveState state = GameArchive.Detect(gameRoot);
    Console.WriteLine($"Content  {state.Mode}"
                      + (state.Mode == ArchiveMode.Packed
                          ? "  — tracks cannot be modded until this is unpacked"
                          : ""));
    if (state.LivePackage is not null)
        Console.WriteLine($"         {Path.GetFileName(state.LivePackage)} ({GameArchive.Bytes(state.ArchiveBytes)})");
    foreach (string p in state.DisabledPackages)
    {
        Console.WriteLine($"         renamed aside: {Path.GetFileName(p)} "
                          + $"({GameArchive.Bytes(new FileInfo(p).Length)}, {new FileInfo(p).LastWriteTime:yyyy-MM-dd})");
    }

    if (state.DisabledPackages.Count > 1)
        Console.WriteLine("         ⚠ more than one archive — 'revert' cannot guess which belongs to this build");

    Console.WriteLine($"Disk     {GameArchive.Bytes(state.FreeBytes)} free"
                      + (state.Mode == ArchiveMode.Packed
                          ? $" — unpacking needs about {GameArchive.Bytes(state.RequiredBytes)}"
                            + (state.HasEnoughSpace ? "" : "  ⚠ NOT ENOUGH")
                          : ""));

    Console.WriteLine($"Flat Pad {Verifier.DetectState(gameRoot) switch
    {
        FlatPadState.Installed => "installed",
        FlatPadState.FilesPresentButNotRegistered =>
            "files present, but NOT registered — run 'install' (unpacking replaces the registries)",
        _ => "not installed",
    }}");
    return 0;
}

static void PrintPlan(TrackInstallPlan plan)
{
    TrackPackage p = plan.Package;
    Console.WriteLine($"Track    {plan.DisplayName}  ({p.TrackId}{(p.Version is null ? "" : $", {p.Version}")})"
                      + (plan.IsReinstall ? "  — reinstall" : ""));
    Console.WriteLine($"Files    {p.Files.Count:N0}, {GameArchive.Bytes(p.TotalBytes)}"
                      + (p.FullyChecksummed ? ", all checksummed" : "")
                      + (p.Ignored.Count > 0 ? $"  ({p.Ignored.Count} package file(s) outside the track, not installed)" : ""));
    foreach (LayoutRegistration l in plan.Layouts)
    {
        Console.WriteLine($"Layout   '{l.Code}' (layout_{l.Layout.Id})  {l.GridSize} car(s)  "
                          + string.Join(", ", TrackRegistrar.SessionsFor(l)));
        foreach ((string kind, string rel) in l.Layout.Containers)
            Console.WriteLine($"           {kind,-20} {rel}");
    }

    foreach (string w in plan.Warnings)
        Console.WriteLine($"  ⚠ {w}");
    foreach (string m in plan.MissingReferences.Take(10))
        Console.WriteLine($"      missing: {m}");
    foreach (string problem in plan.Problems)
        Console.WriteLine($"  ✗ {problem}");
}

static void RequireUnpackedContent(string gameRoot)
{
    if (!Directory.Exists(Path.Combine(gameRoot, "content", "tracks")))
    {
        throw new GameArchiveException(
            $"no content/tracks under {gameRoot} — the game is still packed. Run 'unpack' first.");
    }
}

// Synchronous, not Progress<T>: on the console there is no UI thread to marshal to, and queueing
// the callbacks just lets the bar fall behind the work. Core already rate-limits the reports.
static IProgress<UnpackProgress> Bar(string label) => new InlineProgress(p =>
    Console.Write($"\r{label}… {p.Fraction * 100,5:F1}%  {p.FilesDone:N0}/{p.FilesTotal:N0} files   "));

internal sealed class InlineProgress(Action<UnpackProgress> write) : IProgress<UnpackProgress>
{
    public void Report(UnpackProgress value) => write(value);
}

internal sealed class InlineProgress<T>(Action<T> write) : IProgress<T>
{
    public void Report(T value) => write(value);
}
