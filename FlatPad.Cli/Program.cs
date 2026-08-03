using FlatPad.Core.FlatPad;

// Development entry point. Not shipped — the distributable is the WinForms app; this exists so the
// logic can be exercised, and diffed against the Python reference implementation, without a GUI.

const string Usage = """
    Flat Pad Installer — dev CLI

      install   --game <path>    build + register the track (idempotent)
      uninstall --game <path>    remove it and restore stock content
      verify    --game <path>    check an existing install (read-only)

    <path> is the Assetto Corsa EVO folder, which must be UNPACKED.

    """;

if (args.Length == 0)
{
    Console.Error.Write(Usage);
    return 2;
}

string command = args[0];
string? gameRoot = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--game" && i + 1 < args.Length)
    {
        gameRoot = args[++i];
    }
    else
    {
        Console.Error.WriteLine($"unrecognised argument: {args[i]}");
        Console.Error.Write(Usage);
        return 2;
    }
}

if (gameRoot is null)
{
    Console.Error.WriteLine("--game is required");
    return 2;
}

if (!Directory.Exists(Path.Combine(gameRoot, "content", "tracks")))
{
    Console.Error.WriteLine($"no content/tracks under {gameRoot} — is the game unpacked?");
    return 2;
}

try
{
    switch (command)
    {
        case "verify":
            VerifyReport report = new Verifier(gameRoot).Run();
            Console.Out.Write(report.Render());
            return report.ExitCode;

        case "install":
            new Installer(gameRoot, Console.Out.WriteLine).Install();
            return 0;

        case "uninstall":
            new Installer(gameRoot, Console.Out.WriteLine).Uninstall();
            return 0;

        default:
            Console.Error.WriteLine($"unknown command: {command}");
            Console.Error.Write(Usage);
            return 2;
    }
}
catch (InstallException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
