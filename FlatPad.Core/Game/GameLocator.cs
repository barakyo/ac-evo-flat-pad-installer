using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Microsoft.Win32;

namespace FlatPad.Core.Game;

/// <summary>
/// Finds the Assetto Corsa EVO install, so most users never have to type a path.
/// </summary>
/// <remarks>
/// Steam records its own location in the registry and its extra library folders in
/// <c>steamapps/libraryfolders.vdf</c> — the game is very often not on C:. Everything here is
/// best-effort: a failed lookup just means the Browse button gets used.
/// </remarks>
public static partial class GameLocator
{
    public const string GameFolderName = "Assetto Corsa EVO";
    private const string GameExe = "AssettoCorsaEVO.exe";

    /// <summary>Is this folder actually an AC EVO install?</summary>
    public static bool LooksLikeGame(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, GameExe));

    /// <summary>Every AC EVO install found, best candidate first. Empty if none.</summary>
    public static List<string> FindAll()
    {
        var found = new List<string>();
        foreach (string library in SteamLibraries())
        {
            string candidate = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (LooksLikeGame(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                found.Add(TrueCase(candidate));
        }

        return found;
    }

    /// <summary>
    /// Re-spell a path the way the filesystem actually spells it.
    /// </summary>
    /// <remarks>
    /// Steam's registry value is all lower case, and a tool that is about to write into someone's
    /// game folder should show them a path they recognise rather than one that merely works.
    /// </remarks>
    public static string TrueCase(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        if (!dir.Exists)
            return dir.FullName;

        var parts = new Stack<string>();
        DirectoryInfo cursor = dir;
        while (cursor.Parent is not null)
        {
            parts.Push(cursor.Parent.GetDirectories(cursor.Name).FirstOrDefault()?.Name ?? cursor.Name);
            cursor = cursor.Parent;
        }

        return Path.Combine(cursor.Name.ToUpperInvariant(), Path.Combine([.. parts]));
    }

    public static string? Find() => FindAll().FirstOrDefault();

    private static List<string> SteamLibraries()
    {
        var libraries = new List<string>();
        string? steam = SteamPath();
        if (steam is null)
            return libraries;

        libraries.Add(steam);

        // Extra libraries live in libraryfolders.vdf as `"path"  "D:\\SteamLibrary"` lines. Parsing
        // just that one key is enough and survives the surrounding schema being reshuffled, which
        // Valve has done more than once.
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            return libraries;

        try
        {
            foreach (Match m in LibraryPath.Matches(File.ReadAllText(vdf)))
            {
                string path = m.Groups["path"].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path))
                    libraries.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Steam mid-write, or locked down: fall back to the base library alone.
        }

        return libraries;
    }

    [GeneratedRegex(@"""path""\s*""(?<path>[^""]+)""")]
    private static partial Regex LibraryPath { get; }

    [SupportedOSPlatform("windows")]
    private static string? SteamPathFromRegistry()
    {
        foreach ((RegistryKey root, string key) in
                 (( RegistryKey, string)[])
                 [(Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam")])
        {
            using RegistryKey? handle = root.OpenSubKey(key);
            string? value = handle?.GetValue("SteamPath") as string
                            ?? handle?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
                return Path.GetFullPath(value);
        }

        return null;
    }

    private static string? SteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string? fromRegistry = SteamPathFromRegistry();
                if (fromRegistry is not null)
                    return fromRegistry;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // No registry access: fall through to the conventional locations.
            }
        }

        foreach (string guess in (string[])
                 [@"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam"])
        {
            if (Directory.Exists(guess))
                return guess;
        }

        return null;
    }
}
