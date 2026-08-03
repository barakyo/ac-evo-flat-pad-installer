using System.Globalization;
using System.Text.RegularExpressions;

using ACEvo.Package;

namespace FlatPad.Core.Game;

/// <summary>How the game is currently set up to read its content.</summary>
public enum ArchiveMode
{
    /// <summary>A live <c>content.kspkg</c> is present: the game reads the archive, and loose
    /// track folders are ignored. Tracks cannot be modded in this state.</summary>
    Packed,

    /// <summary>No live archive, and loose content on disk: the game reads the folders.</summary>
    Unpacked,

    /// <summary>Neither — not a game folder, or a half-finished operation.</summary>
    Unknown,
}

/// <param name="Mode">What the game will do on next launch.</param>
/// <param name="LivePackage">The active <c>content.kspkg</c>, if any.</param>
/// <param name="DisabledPackages">Renamed-aside archives, newest first. More than one is a trap.</param>
/// <param name="ArchiveBytes">Size of the archive this install would unpack (or did).</param>
/// <param name="FreeBytes">Free space on the game's drive.</param>
public sealed record GameArchiveState(
    ArchiveMode Mode,
    string? LivePackage,
    IReadOnlyList<string> DisabledPackages,
    bool HasLooseContent,
    long ArchiveBytes,
    long FreeBytes)
{
    /// <summary>
    /// Space unpacking needs, derived from the archive rather than hardcoded.
    /// </summary>
    /// <remarks>
    /// Unpacking roughly DOUBLES the install: the archive is kept (renamed aside), and its contents
    /// are written out alongside. Measured on a 73.6 GB archive, the unpack wrote ~67 GB into
    /// <c>content/</c> plus a few GB of <c>uiresources/</c>, <c>system/</c>, <c>editor/</c>,
    /// <c>cfg/</c> and <c>scrapyard/</c>. Computing it from the archive size means this survives the
    /// archive changing size with a game update.
    /// </remarks>
    public long RequiredBytes => ArchiveBytes;

    public bool HasEnoughSpace => FreeBytes >= RequiredBytes;
}

/// <param name="Path">Path inside the archive, e.g. <c>content\tracks\sebring\sebring.scene</c>.</param>
public sealed record ArchiveEntry(string Path, long Offset, long Size);

public sealed record UnpackProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal,
    string CurrentPath)
{
    public double Fraction => BytesTotal == 0 ? 0 : (double)BytesDone / BytesTotal;
}

public sealed class GameArchiveException(string message) : Exception(message);

/// <summary>
/// Detects and switches the game between packed and unpacked content.
/// </summary>
/// <remarks>
/// Tracks — unlike cars — are not loaded from <c>Saved Games\ACE\mods\</c>. They only work when the
/// game runs unpacked, which is why an installer for one has to be able to do this at all.
///
/// Wraps Nenkai's MIT <c>ACEvo.Package</c> through its public API only. Its <c>ExtractAll</c> has
/// no progress or cancellation hook and its file table is private, so extraction is driven here one
/// entry at a time — which also buys resumability. Not forking the submodule keeps
/// <c>git submodule update</c> a safe way to pick up format fixes, and the pack format HAS changed
/// across game versions.
/// </remarks>
public static partial class GameArchive
{
    public const string PackageName = "content.kspkg";

    /// <summary>The suffix this tool renames an archive to. Deterministic, so revert is unambiguous.</summary>
    public const string DisabledSuffix = ".bak";

    [GeneratedRegex(@"^(?<path>.*) - offset:(?<offset>[0-9A-Fa-f]+), size:(?<size>[0-9A-Fa-f]+)")]
    private static partial Regex ListingLine { get; }

    public static GameArchiveState Detect(string gameRoot)
    {
        string live = Path.Combine(gameRoot, PackageName);
        bool hasLive = File.Exists(live);

        List<string> disabled = Directory.Exists(gameRoot)
            ? Directory.EnumerateFiles(gameRoot, PackageName + ".*")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .ToList()
            : [];

        bool loose = Directory.Exists(Path.Combine(gameRoot, "content", "tracks"));

        ArchiveMode mode = hasLive ? ArchiveMode.Packed
            : loose && disabled.Count > 0 ? ArchiveMode.Unpacked
            : ArchiveMode.Unknown;

        string? sizeSource = hasLive ? live : disabled.FirstOrDefault();
        long archiveBytes = sizeSource is not null ? new FileInfo(sizeSource).Length : 0;

        long free = 0;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(gameRoot))!).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Unmapped or exotic drive — report 0 free rather than refusing to describe the install.
        }

        return new GameArchiveState(mode, hasLive ? live : null, disabled, loose, archiveBytes, free);
    }

    /// <summary>Every file in an archive, in physical order.</summary>
    /// <remarks>
    /// Reading the listing back out of the library is the only way to enumerate the file table
    /// through its public API. It is also how a packaged mod gets audited before release.
    /// </remarks>
    public static List<ArchiveEntry> ListEntries(string packagePath)
    {
        string listing = Path.Combine(Path.GetTempPath(), $"flatpad-listing-{Guid.NewGuid():N}.txt");
        try
        {
            using (PackFile pack = PackFile.Open(packagePath))
                pack.ListFiles(listing);

            var entries = new List<ArchiveEntry>();
            foreach (string line in File.ReadLines(listing))
            {
                Match m = ListingLine.Match(line);
                if (!m.Success)
                    continue;
                entries.Add(new ArchiveEntry(
                    m.Groups["path"].Value,
                    long.Parse(m.Groups["offset"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    long.Parse(m.Groups["size"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            entries.Sort((a, b) => a.Offset.CompareTo(b.Offset));   // sequential reads, be kind to HDDs
            return entries;
        }
        finally
        {
            if (File.Exists(listing))
                File.Delete(listing);
        }
    }

    /// <summary>
    /// Unpack the game's content archive so loose folders — and therefore modded tracks — load.
    /// </summary>
    /// <remarks>
    /// ⚠️ The archive is renamed aside only AFTER every file is out. Cancel or crash halfway and the
    /// live <c>content.kspkg</c> is still there, so the game still launches and the operation can
    /// simply be re-run. The reverse order would brick the install at the first interruption.
    /// </remarks>
    /// <returns>How many files were written.</returns>
    public static int Unpack(string gameRoot, IProgress<UnpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GameArchiveState state = Detect(gameRoot);
        if (state.Mode != ArchiveMode.Packed || state.LivePackage is null)
        {
            throw new GameArchiveException(
                $"nothing to unpack: no live {PackageName} in {gameRoot} (the game is already unpacked)");
        }

        if (!state.HasEnoughSpace)
        {
            throw new GameArchiveException(
                $"not enough free space: unpacking needs about {Bytes(state.RequiredBytes)} and "
                + $"{Bytes(state.FreeBytes)} is free. The archive is kept, so budget roughly twice its size.");
        }

        List<ArchiveEntry> entries = ListEntries(state.LivePackage);
        long totalBytes = entries.Sum(e => e.Size);
        long doneBytes = 0;
        int done = 0;
        var failed = new List<string>();

        using (PackFile pack = PackFile.Open(state.LivePackage))
        {
            foreach (ArchiveEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!pack.ExtractFile(entry.Path, gameRoot))
                    failed.Add(entry.Path);
                done++;
                doneBytes += entry.Size;
                progress?.Report(new UnpackProgress(done, entries.Count, doneBytes, totalBytes, entry.Path));
            }
        }

        if (failed.Count > 0)
        {
            // Silently short files would look like a successful unpack and fail much later, in-game.
            throw new GameArchiveException(
                $"{failed.Count} of {entries.Count} files could not be extracted, "
                + $"starting with '{failed[0]}'. The archive has been left in place.");
        }

        File.Move(state.LivePackage, state.LivePackage + DisabledSuffix);
        return done;
    }

    /// <summary>Put the archive back, so the game reads packed content again.</summary>
    /// <remarks>
    /// ⚠️ Refuses when several renamed-aside archives exist. A game update downloads a FRESH
    /// <c>content.kspkg</c>, so an install can accumulate archives from different versions — and
    /// restoring the wrong one puts stale content under a newer build. Which one is right is the
    /// user's call, not a guess this tool should make.
    ///
    /// The unpacked files are left where they are: deleting tens of gigabytes is not something to do
    /// as a side effect of a rename. They are inert while the archive is live.
    /// </remarks>
    public static void RevertToPacked(string gameRoot)
    {
        GameArchiveState state = Detect(gameRoot);
        if (state.LivePackage is not null)
            throw new GameArchiveException($"{PackageName} is already in place — the game reads packed content");
        if (state.DisabledPackages.Count == 0)
            throw new GameArchiveException($"no renamed-aside archive found in {gameRoot} to restore");
        if (state.DisabledPackages.Count > 1)
        {
            throw new GameArchiveException(
                $"{state.DisabledPackages.Count} renamed-aside archives are present, so which one belongs "
                + "to this game version is ambiguous — a game update downloads a fresh archive, and "
                + "restoring an older one would put stale content under a newer build. Rename the one you "
                + $"want to '{PackageName}' by hand:\n  "
                + string.Join("\n  ", state.DisabledPackages.Select(
                    p => $"{Path.GetFileName(p)}  ({Bytes(new FileInfo(p).Length)}, "
                         + $"{new FileInfo(p).LastWriteTime:yyyy-MM-dd})")));
        }

        File.Move(state.DisabledPackages[0], Path.Combine(gameRoot, PackageName));
    }

    /// <summary>What a sampled comparison of the unpacked files against the archive found.</summary>
    public sealed record UnpackCheck(int Checked, int Total, List<string> Missing, List<string> Different)
    {
        public bool Ok => Missing.Count == 0 && Different.Count == 0;
    }

    /// <summary>
    /// Compare a sample of the loose files against the archive they came out of.
    /// </summary>
    /// <remarks>
    /// Answers the question a user actually has when a modded track does not appear: is this install
    /// really unpacked, and did the unpack finish? A cancelled or disk-full unpack leaves a tree that
    /// looks plausible and is missing files, and nothing in the game says so.
    ///
    /// The sample is a fixed stride rather than random, so two runs compare the same files.
    /// </remarks>
    public static UnpackCheck CheckUnpack(string gameRoot, string archivePath, int sampleSize = 200,
        IProgress<UnpackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        List<ArchiveEntry> entries = ListEntries(archivePath);
        List<ArchiveEntry> files = entries.Where(e => e.Size > 0).ToList();
        if (files.Count == 0)
            throw new GameArchiveException($"{Path.GetFileName(archivePath)} lists no files with content");

        int stride = Math.Max(1, files.Count / Math.Max(1, sampleSize));
        var sample = new List<ArchiveEntry>();
        for (int i = 0; i < files.Count; i += stride)
            sample.Add(files[i]);

        var missing = new List<string>();
        var different = new List<string>();
        string scratch = Path.Combine(Path.GetTempPath(), $"flatpad-check-{Guid.NewGuid():N}");
        try
        {
            using PackFile pack = PackFile.Open(archivePath);
            int done = 0;
            foreach (ArchiveEntry entry in sample)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string loose = Path.Combine(gameRoot, entry.Path);
                if (!File.Exists(loose))
                {
                    missing.Add(entry.Path);
                }
                else if (!pack.ExtractFile(entry.Path, scratch))
                {
                    different.Add(entry.Path);      // in the listing but not extractable: a bad archive
                }
                else
                {
                    string extracted = Path.Combine(scratch, entry.Path);
                    if (!File.ReadAllBytes(loose).AsSpan().SequenceEqual(File.ReadAllBytes(extracted)))
                        different.Add(entry.Path);
                }

                done++;
                progress?.Report(new UnpackProgress(done, sample.Count, done, sample.Count, entry.Path));
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }

        return new UnpackCheck(sample.Count, files.Count, missing, different);
    }

    public static string Bytes(long value) => value switch
    {
        >= 1L << 40 => $"{value / (double)(1L << 40):F1} TB",
        >= 1L << 30 => $"{value / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{value / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{value / (double)(1L << 10):F1} KB",
        _ => $"{value} bytes",
    };
}
