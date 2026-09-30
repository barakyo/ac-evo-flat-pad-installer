using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using EvoMods.Core.FlatPad;
using EvoMods.Core.Game;
using EvoMods.Core.Protobuf;
using EvoMods.Core.Refs;
using EvoMods.Core.Scene;

namespace EvoMods.Core.Tracks;

/// <summary>One file of a track, relative to the track's own folder (<c>containers/timelines.scene</c>).</summary>
/// <param name="Sha256">The hash the package's own checksum list promises, if it has one.</param>
public sealed record PackageFile(string Rel, long Length, string EntryPath, string? Sha256);

/// <summary>A layout the package ships, and the container file each session kind will be handed.</summary>
/// <param name="Id">The file suffix: <c>layout_&lt;Id&gt;.scene</c>.</param>
/// <param name="Containers">Container kind (<c>spawnpoints_pitlane</c>, …) to the file that provides it.</param>
public sealed record PackageLayout(string Id, IReadOnlyDictionary<string, string> Containers);

/// <summary>
/// A custom track as it arrives: a zip, or the folder it was extracted to.
/// </summary>
/// <remarks>
/// The shape accepted is what EvoForge writes — <c>content\tracks\&lt;id&gt;\&lt;id&gt;.scene</c>
/// plus <c>containers\layout_&lt;layout&gt;.scene</c> — at any depth, because an archive with an extra
/// wrapper folder is the commonest way a download differs from its instructions. Nothing outside
/// the track folder is installed: a README or a manifest describes the track, it is not part of it.
///
/// Opening reads the directory and the few small files that describe the track. It never extracts
/// the meshes; <see cref="CopyTo"/> is the only thing that reads every byte.
/// </remarks>
public sealed partial class TrackPackage : IDisposable
{
    /// <summary>
    /// Container kinds a session can be handed, in the order a base-game row lists them.
    /// </summary>
    /// <remarks>
    /// Each resolves to <c>&lt;kind&gt;_&lt;layout&gt;.scene</c> when the package has one, else to an
    /// unsuffixed <c>&lt;kind&gt;.scene</c> shared by every layout. Kunos suffixes everything
    /// (<c>timelines_gp.scene</c>); EvoForge suffixes only the layout itself (<c>timelines.scene</c>).
    /// </remarks>
    public static readonly string[] ContainerKinds =
    [
        "timelines", "spawnpoints_pitlane", "spawnpoints_hotlap", "spawnpoints_grid",
        "pitlane_zones", "marshalls", "starting_positions", "tv1_cameras", "tv2_cameras",
    ];

    private readonly IEntrySource _source;

    private TrackPackage(IEntrySource source) => _source = source;

    /// <summary>The zip or folder this was opened from.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The track folder name, and the stem of its <c>.scene</c> and <c>.track</c>.</summary>
    public required string TrackId { get; init; }

    /// <summary>What the menus will call it: the manifest's name, or one derived from the id.</summary>
    public required string DisplayName { get; init; }

    public string? Version { get; init; }

    public required IReadOnlyList<PackageLayout> Layouts { get; init; }

    public required IReadOnlyList<PackageFile> Files { get; init; }

    /// <summary>Things worth saying before installing, none of which stop it.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Package entries that are not part of the track and will not be installed.</summary>
    public required IReadOnlyList<string> Ignored { get; init; }

    /// <summary>True when the package carries a checksum for every file it installs.</summary>
    public bool FullyChecksummed => Files.All(f => f.Sha256 is not null);

    public long TotalBytes => Files.Sum(f => f.Length);

    /// <summary><c>content/tracks/&lt;id&gt;</c> — where the files go, in canonical reference form.</summary>
    public string TrackDir => $"content/tracks/{TrackId}";

    public void Dispose() => _source.Dispose();

    /// <summary>Open a <c>.zip</c> or a folder, and work out which track it holds.</summary>
    /// <exception cref="InstallException">It is not a track this can install, and why.</exception>
    public static TrackPackage Open(string path)
    {
        IEntrySource source = Directory.Exists(path) ? new FolderSource(path)
            : File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? new ZipSource(path)
            : throw new InstallException($"not a .zip or a folder: {path}");

        try
        {
            return Read(source, path);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Read a track already installed in the game, as if it were the package it came from.</summary>
    /// <remarks>
    /// What repairing needs: a game patch reverts the registries but leaves the files, and the
    /// registrar only needs to know which containers are there — not the zip, which may be long gone.
    /// </remarks>
    public static TrackPackage OpenInstalled(string gameRoot, string trackId)
    {
        string folder = RefPath.RealPath(gameRoot, $"content/tracks/{trackId}");
        if (!Directory.Exists(folder))
            throw new InstallException($"content\\tracks\\{trackId} is not on disk");

        var source = new FolderSource(folder, $"content/tracks/{trackId}/");
        try
        {
            return Read(source, folder);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static TrackPackage Read(IEntrySource source, string path)
    {
        List<(string Path, long Length)> entries = source.List();

        // ⚠️ Refused before anything else looks at them. An entry that climbs out of its folder is
        // either a broken archive or a hostile one, and neither should be half-installed.
        List<string> unsafeEntries = entries.Select(e => e.Path)
            .Where(p => GameArchive.SafeOutputPath("x", p) is null).ToList();
        if (unsafeEntries.Count > 0)
        {
            throw new InstallException(
                $"refusing a package with {unsafeEntries.Count} path(s) that leave its own folder, "
                + $"e.g. '{unsafeEntries[0]}'");
        }

        var roots = entries
            .Select(e => SceneAtTrackRoot().Match(e.Path))
            .Where(m => m.Success && string.Equals(m.Groups["id"].Value, m.Groups["stem"].Value,
                StringComparison.OrdinalIgnoreCase))
            .Select(m => (Prefix: m.Groups["prefix"].Value, Id: m.Groups["id"].Value))
            .Distinct()
            .ToList();
        if (roots.Count == 0)
        {
            throw new InstallException(
                "no track found: expected content\\tracks\\<id>\\<id>.scene somewhere inside "
                + Path.GetFileName(path));
        }

        if (roots.Count > 1)
        {
            throw new InstallException(
                $"{roots.Count} tracks in one package ({string.Join(", ", roots.Select(r => r.Id))}) "
                + "— install them one at a time");
        }

        (string prefix, string id) = roots[0];
        if (!TrackIdPattern().IsMatch(id))
            throw new InstallException($"'{id}' is not a folder name this can register (letters, digits, _ and - only)");

        string trackPrefix = $"{prefix}content/tracks/{id}/";
        Dictionary<string, string> sums = ReadChecksums(source, entries, prefix);
        var files = new List<PackageFile>();
        var ignored = new List<string>();
        foreach ((string entry, long length) in entries)
        {
            if (entry.StartsWith(trackPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rel = entry[trackPrefix.Length..];
                files.Add(new PackageFile(rel, length, entry,
                    sums.GetValueOrDefault(entry[prefix.Length..])));
            }
            else
            {
                ignored.Add(entry);
            }
        }

        Manifest? manifest = ReadManifest(source, entries, prefix);
        var warnings = new List<string>();
        var layouts = new List<PackageLayout>();
        var names = files.Select(f => f.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (PackageFile f in files)
        {
            Match m = LayoutScene().Match(f.Rel);
            if (!m.Success)
                continue;

            string layout = m.Groups["layout"].Value;
            var containers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layout"] = f.Rel,
            };
            foreach (string kind in ContainerKinds)
            {
                string? hit = new[] { $"containers/{kind}_{layout}.scene", $"containers/{kind}.scene" }
                    .FirstOrDefault(names.Contains);
                if (hit is not null)
                    containers[kind] = files.First(x => string.Equals(x.Rel, hit, StringComparison.OrdinalIgnoreCase)).Rel;
            }

            layouts.Add(new PackageLayout(layout, containers));
        }

        if (layouts.Count == 0)
            throw new InstallException($"{id} has no containers\\layout_<name>.scene — a track needs at least one layout");

        if (layouts.FirstOrDefault(l => !l.Containers.ContainsKey("spawnpoints_pitlane")) is { } unspawnable)
        {
            throw new InstallException(
                $"layout '{unspawnable.Id}' has no spawnpoints_pitlane scene — Practice spawns from it, so "
                + "the track could be selected but never driven");
        }

        if (!names.Contains($"{id}.track"))
            throw new InstallException($"{id} has no {id}.track — the catalog entry points at it");

        if (manifest?.LayoutId is { } wanted
            && !layouts.Any(l => string.Equals(l.Id, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"the manifest names layout '{wanted}', which the package does not contain");
        }

        foreach (PackageLayout l in layouts.Where(l => !l.Containers.ContainsKey("spawnpoints_hotlap")))
            warnings.Add($"layout '{l.Id}' has no hotlap spawn, so it is registered without Hotstint");
        if (!names.Any(n => n.StartsWith("layouts/", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("no AI line or track control points: AI opponents and lap timing may not work");
        if (sums.Count > 0 && !files.All(f => f.Sha256 is not null))
        {
            warnings.Add($"{files.Count(f => f.Sha256 is null)} file(s) are missing from the package's "
                         + "own checksum list and cannot be checked");
        }

        return new TrackPackage(source)
        {
            SourcePath = path,
            TrackId = id,
            DisplayName = string.IsNullOrWhiteSpace(manifest?.Name) ? Humanize(id) : manifest.Name.Trim(),
            Version = manifest?.Version,
            Layouts = layouts.OrderBy(l => l.Id, StringComparer.Ordinal).ToList(),
            Files = files,
            Warnings = warnings,
            Ignored = ignored,
        };
    }

    /// <summary>
    /// <c>hdc_drift_park</c> → <c>HDC Drift Park</c>, <c>gp</c> → <c>GP</c>. A default, meant to be edited.
    /// </summary>
    /// <remarks>Words of three letters or fewer are taken as initials, the way the game writes "GP".</remarks>
    public static string Humanize(string id) =>
        string.Join(' ', id.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 3 ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>A file's bytes, straight from the package.</summary>
    public byte[] Read(PackageFile file)
    {
        using Stream s = _source.Open(file.EntryPath);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>A track file by its path inside the track folder, or null.</summary>
    public PackageFile? FileAt(string rel) =>
        Files.FirstOrDefault(f => string.Equals(f.Rel, rel, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How many cars a layout's grid holds: its grid spawns, else its pit spawns.
    /// </summary>
    /// <remarks>
    /// This is the <c>[8.9]</c> a session row carries. It matches the spawn files on the base game —
    /// Sebring registers 45 and its grid holds 45 — and a count larger than the spawns would offer
    /// a session with more cars than there are places to put them.
    /// </remarks>
    public int GridSize(PackageLayout layout)
    {
        foreach (string kind in (string[])["spawnpoints_grid", "spawnpoints_pitlane"])
        {
            if (!layout.Containers.TryGetValue(kind, out string? rel) || FileAt(rel) is not { } file)
                continue;
            List<PbNode> scene = PbTree.ParseTree(Read(file));
            int count = scene.Count(n => n.Number == 2 && SceneNodes.Type(n) == "Start Pos"
                                         && !SceneNodes.Name(n).StartsWith("box", StringComparison.OrdinalIgnoreCase));
            if (count > 0)
                return count;
        }

        return 1;
    }

    /// <summary>
    /// Every <c>content\…</c> reference the track makes that resolves neither in the package nor on
    /// disk under <paramref name="gameRoot"/>.
    /// </summary>
    /// <remarks>
    /// A converted track leans on base-game scenery, materials and textures, so this is the check
    /// that says the install underneath it is the one it was built against. Files too large to be
    /// worth scanning are skipped, by the same rule as the Flat Pad closure crawl — meshes, which
    /// reference nothing.
    /// </remarks>
    public List<string> MissingReferences(string gameRoot, CancellationToken cancellationToken = default)
    {
        var own = Files.Select(f => RefPath.Canon($"{TrackDir}/{f.Rel}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageFile f in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Closure.IsScannable(f.Rel) || f.Length > MaxScanBytes)
                continue;
            foreach (string r in ReferenceScanner.ExtractReferences(Read(f)))
            {
                string c = RefPath.Canon(r);
                if (!seen.Add(c))
                    continue;
                if (!own.Contains(c) && !File.Exists(RefPath.RealPath(gameRoot, c)))
                    missing.Add(c);
            }
        }

        return missing.ToList();
    }

    private const long MaxScanBytes = 8_000_000;

    /// <summary>
    /// Write every track file into <paramref name="destDir"/>, checking each against the package's
    /// checksum as it goes. Returns each file's SHA-256, for the ledger.
    /// </summary>
    /// <exception cref="InstallException">A file does not match the checksum the package promises.</exception>
    public Dictionary<string, string> CopyTo(string destDir, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        int done = 0;
        foreach (PackageFile f in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dst = GameArchive.SafeOutputPath(destDir, f.Rel)
                         ?? throw new InstallException($"refusing '{f.Rel}': it leaves the track folder");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            using (Stream src = _source.Open(f.EntryPath))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (FileStream outFile = File.Create(dst))
            {
                byte[] buffer = new byte[1 << 16];
                int n;
                while ((n = src.Read(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    outFile.Write(buffer, 0, n);
                }

                string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (f.Sha256 is not null && !string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallException(
                        $"{f.Rel} does not match the package's own checksum — the download is damaged");
                }

                hashes[f.Rel] = hash;
            }

            progress?.Report((++done, Files.Count));
        }

        return hashes;
    }

    // ------------------------------------------------------------------ describing files

    private sealed record Manifest(string? Name, string? Version, string? LayoutId);

    private static Manifest? ReadManifest(IEntrySource source, List<(string Path, long Length)> entries, string prefix)
    {
        (string Path, long Length) hit = entries.FirstOrDefault(e =>
            string.Equals(e.Path, $"{prefix}manifest.json", StringComparison.OrdinalIgnoreCase));
        if (hit.Path is null || hit.Length > 4_000_000)
            return null;

        try
        {
            using Stream s = source.Open(hit.Path);
            using JsonDocument doc = JsonDocument.Parse(s);
            JsonElement root = doc.RootElement;
            string? Str(string key) =>
                root.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Manifest(Str("name"), Str("version"), Str("layoutId"));
        }
        catch (JsonException)
        {
            return null;   // a manifest is a courtesy; a broken one just means deriving the name
        }
    }

    /// <summary><c>SHA256SUMS.txt</c> — <c>&lt;hex&gt;  &lt;path&gt;</c> per line, paths relative to the package root.</summary>
    private static Dictionary<string, string> ReadChecksums(IEntrySource source,
        List<(string Path, long Length)> entries, string prefix)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (string Path, long Length) hit = entries.FirstOrDefault(e =>
            string.Equals(e.Path, $"{prefix}SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (hit.Path is null)
            return sums;

        using var reader = new StreamReader(source.Open(hit.Path));
        while (reader.ReadLine() is { } line)
        {
            Match m = ChecksumLine().Match(line);
            if (m.Success)
                sums[m.Groups["path"].Value.Replace('\\', '/').TrimStart('.', '/')] = m.Groups["hash"].Value;
        }

        return sums;
    }

    [GeneratedRegex(@"^(?<prefix>(?:[^/]+/)*?)content/tracks/(?<id>[^/]+)/(?<stem>[^/]+)\.scene$", RegexOptions.IgnoreCase)]
    private static partial Regex SceneAtTrackRoot();

    [GeneratedRegex(@"^containers/layout_(?<layout>[^/]+)\.scene$", RegexOptions.IgnoreCase)]
    private static partial Regex LayoutScene();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex TrackIdPattern();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<path>.+?)\s*$")]
    private static partial Regex ChecksumLine();

    // ------------------------------------------------------------------ zip or folder

    /// <summary>Entries with forward-slash paths relative to the package root.</summary>
    private interface IEntrySource : IDisposable
    {
        List<(string Path, long Length)> List();

        Stream Open(string path);
    }

    /// <param name="mountedAt">A prefix every entry appears under, as if the folder sat that deep.</param>
    private sealed class FolderSource(string root, string mountedAt = "") : IEntrySource
    {
        public List<(string Path, long Length)> List() =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => (mountedAt + Path.GetRelativePath(root, f).Replace('\\', '/'), new FileInfo(f).Length))
                .ToList();

        public Stream Open(string path) => File.OpenRead(Path.Combine(root, path[mountedAt.Length..]));

        public void Dispose()
        {
        }
    }

    private sealed class ZipSource(string path) : IEntrySource
    {
        private readonly ZipArchive _zip = ZipFile.OpenRead(path);

        public List<(string Path, long Length)> List() =>
            _zip.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))   // folders
                .Select(e => (e.FullName.Replace('\\', '/'), e.Length))
                .ToList();

        public Stream Open(string entryPath) =>
            (_zip.GetEntry(entryPath) ?? _zip.GetEntry(entryPath.Replace('/', '\\'))
                ?? throw new FileNotFoundException(entryPath)).Open();

        public void Dispose() => _zip.Dispose();
    }
}
