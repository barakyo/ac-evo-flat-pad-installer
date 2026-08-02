# Flat Pad Installer

A one-click installer for **Flat Pad** — a 1.5 km dead-flat, wall-free, scenery-free test pad for
*Assetto Corsa EVO* that spawns your car ready to drive. Built for physics testing, where driving
800 m across a real circuit before every run gets old fast.

> **Status: in progress.** The reference implementation is a Python script; this is the C# port that
> turns it into a distributable Windows app. Today the port covers the file-format layer and the
> `verify` command. Building, registering and unpacking land next, then the GUI.

## How it works, and why it isn't just a zip

Flat Pad is **derived from your own installed copy of the game**, on your machine, every time. It is
Sebring's own concrete surface mesh flattened and scaled up — nothing else reliably collides — plus
the reference closure that mesh drags in (~780 files). None of that is redistributable, so the tool
ships the *recipe*, never the output.

A zipped track folder would not work anyway. A track only appears in the menus once it is registered
in two `system\*.table` registries, and the game has to be running **unpacked** — tracks, unlike
cars, are not loaded from `Saved Games\ACE\mods\`. The installer handles all of it, and reverts it.

Base-game content is left byte-identical to what Kunos ships. The two registries are always rebuilt
from a `.orig` snapshot rather than appended to, so re-running can never stack duplicate entries.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build FlatPadInstaller.slnx
dotnet test  FlatPadInstaller.slnx
```

## Using the dev CLI

```
dotnet run --project FlatPad.Cli -- verify [--game "<path to Assetto Corsa EVO>"]
```

`--game` is optional once auto-detection lands; for now pass it explicitly. `verify` is read-only —
it reports a **count** for every check, because a validator that finds nothing to check would
otherwise print a cheerful `PASS`.

## Checking against the reference implementation

The Python script in the parent project stays authoritative until the port is confirmed in-game. The
two must agree exactly:

```
uv run python ../tracks/install_flatpad.py --verify > py.txt
dotnet run --project FlatPad.Cli -- verify --game "<game>"  > cs.txt
diff py.txt cs.txt
```

## Layout

| | |
|---|---|
| `FlatPad.Core/Protobuf` | Lossless raw-protobuf tree. Re-emits a node's original bytes unless it was modified, so an untouched file round-trips byte-identical. |
| `FlatPad.Core/Refs` | Reference extraction, the `content\…` closure crawl, and copy-with-repath. |
| `FlatPad.Core/Tables` | The `system\*.table` registry editor. |
| `FlatPad.Core/FlatPad` | The Flat Pad recipe itself, and `verify`. |
| `FlatPad.Cli` | Dev entry point. Not shipped. |

## Licence

MIT — see [`LICENSE`](LICENSE). Third-party components and the asset-redistribution position are in
[`THIRD-PARTY.md`](THIRD-PARTY.md).
