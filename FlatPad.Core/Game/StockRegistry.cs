using ACEvo.Package;

namespace FlatPad.Core.Game;

/// <summary>
/// Reads files as the game SHIPS them, out of its own content archive.
/// </summary>
/// <remarks>
/// The point is to have something to compare the live install against. A mod that writes into a
/// shared registry can damage it, and that damage is invisible — no error, no log line, the track
/// simply stops appearing in the menus. The archive is the only stock reference available offline.
///
/// Deliberately narrow: a handful of small files by exact path, never a crawl.
/// </remarks>
public interface IStockRegistry
{
    /// <summary>False when there is nothing trustworthy to compare against; <see cref="Describe"/> says why.</summary>
    bool Available { get; }

    /// <summary>The archive being read, or the reason there is none.</summary>
    string Describe { get; }

    /// <summary>Stock bytes of a reference such as <c>system/tracks.table</c>, or null if absent.</summary>
    byte[]? Read(string reference);
}

/// <summary>No stock reference available, and why.</summary>
public sealed class UnavailableStockRegistry(string reason) : IStockRegistry
{
    public bool Available => false;

    public string Describe => reason;

    public byte[]? Read(string reference) => null;
}

/// <summary>
/// Stock files pulled straight out of the game's renamed-aside <c>content.kspkg</c>.
/// </summary>
/// <remarks>
/// ⚠️ Uses the archive's hash lookup (<c>PackFile.ExtractFile</c>), NOT the file-table listing.
/// Enumerating a 119,000-entry archive takes 20–30 s; extracting two named files is a dictionary
/// lookup plus a seek. A check that runs inside <c>verify</c> has to be the second thing.
/// </remarks>
public sealed class ArchiveStockRegistry(string archivePath) : IStockRegistry, IDisposable
{
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private PackFile? _pack;
    private bool _opened;
    private string? _openError;

    public bool Available => Open() is not null;

    public string Describe => _openError ?? Path.GetFileName(archivePath);

    /// <summary>
    /// Pick the archive that describes this install's stock content — or decline to guess.
    /// </summary>
    /// <remarks>
    /// ⚠️ An install can hold archives from several game versions, because an update downloads a
    /// fresh one. <c>revert</c> already refuses to choose between them, and so does this: an archive
    /// NEWER than the loose content holds tracks the install never had, which would read as damage.
    /// </remarks>
    public static IStockRegistry ForGame(string gameRoot)
    {
        GameArchiveState state = GameArchive.Detect(gameRoot);

        // A live archive means the game is packed, so there is no loose registry to compare with —
        // but reading stock from it is still perfectly valid, and costs nothing to allow.
        if (state.LivePackage is not null)
            return new ArchiveStockRegistry(state.LivePackage);

        return state.DisabledPackages.Count switch
        {
            0 => new UnavailableStockRegistry("no renamed-aside archive to compare against"),
            1 => new ArchiveStockRegistry(state.DisabledPackages[0]),
            _ => new UnavailableStockRegistry(
                $"{state.DisabledPackages.Count} archives present — cannot tell which matches this build"),
        };
    }

    public byte[]? Read(string reference)
    {
        if (_cache.TryGetValue(reference, out byte[]? cached))
            return cached;

        byte[]? data = Extract(reference);
        _cache[reference] = data;
        return data;
    }

    private byte[]? Extract(string reference)
    {
        PackFile? pack = Open();
        if (pack is null)
            return null;

        string scratch = Path.Combine(Path.GetTempPath(), $"flatpad-stock-{Guid.NewGuid():N}");
        try
        {
            // ExtractFile hashes the path itself, lowercasing and normalising '/' to '\', so a
            // canonical reference goes in as-is. It returns false rather than throwing when the
            // archive holds no such file — which is also how a package that is not the game's
            // content archive at all degrades to "nothing to compare against".
            if (!pack.ExtractFile(reference, scratch))
                return null;
            string written = Path.Combine(scratch, reference.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(written) ? File.ReadAllBytes(written) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Open once, lazily — the cost is the 64 MB file table, and a skipped check pays none of it.</summary>
    private PackFile? Open()
    {
        if (_opened)
            return _pack;
        _opened = true;
        try
        {
            _pack = PackFile.Open(archivePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or InvalidDataException or ArgumentException)
        {
            _openError = $"{Path.GetFileName(archivePath)} could not be read ({e.Message})";
        }

        return _pack;
    }

    public void Dispose()
    {
        _pack?.Dispose();
        _pack = null;
    }
}
