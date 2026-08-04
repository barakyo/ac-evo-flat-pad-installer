using FlatPad.Core.FlatPad;
using FlatPad.Core.Game;

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
        default:
            Console.Error.WriteLine($"unrecognised argument: {args[i]}");
            Console.Error.Write(Usage);
            return 2;
    }
}

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

// Ctrl+C stops long work cleanly rather than killing it mid-file.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\ncancelling…");
    cancel.Cancel();
};

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
