# Flat Pad Installer

A one-click installer for **Flat Pad** — a 1.5 km dead-flat, wall-free, scenery-free test pad for
*Assetto Corsa EVO* that spawns your car ready to drive. Built for physics testing, where driving
800 m across a real circuit before every run gets old fast.

> **Status: in progress.** The reference implementation is a Python script; this is the C# port that
> turns it into a distributable Windows app. Install, uninstall and verify all work today — the port
> produces a **byte-identical** track to the Python's, all 1530 files. Still to come: detecting and
> unpacking the game archive, and the GUI.

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
dotnet run --project FlatPad.Cli -- install   --game "<path to Assetto Corsa EVO>"
dotnet run --project FlatPad.Cli -- uninstall --game "<path>"
dotnet run --project FlatPad.Cli -- verify    --game "<path>"
```

`--game` becomes optional once auto-detection lands; for now pass it explicitly. All three are
idempotent. `verify` is read-only, and reports a **count** for every check — a validator that finds
nothing to check would otherwise print a cheerful `PASS`.

`--pr-runoff-spawn` from the Python is deliberately not ported: it moves a spawn on a *base-game*
track, which is the one thing this tool exists to avoid. `uninstall` still reverts it if an older
run left it behind.

## Checking against the reference implementation

The Python script in the parent project stays authoritative until the port is confirmed in-game.
The two agree byte-for-byte, on the console output *and* on the files produced:

```
uv run python ../tracks/install_flatpad.py --verify           > py.txt
dotnet run --project FlatPad.Cli -- verify --game "<game>"    > cs.txt
diff py.txt cs.txt

# stronger: install with each, and hash everything they wrote
( cd "<game>" && find content/tracks/flatpad -type f | sort | xargs sha256sum ) > tree.sha256
```

## Layout

| | |
|---|---|
| `FlatPad.Core/Protobuf` | Lossless raw-protobuf tree. Re-emits a node's original bytes unless it was modified, so an untouched file round-trips byte-identical. |
| `FlatPad.Core/Refs` | Reference extraction, the `content\…` closure crawl, and copy-with-repath. |
| `FlatPad.Core/Scene` | Reading and reshaping the geometry a track scene is made of. |
| `FlatPad.Core/Tables` | The `system\*.table` registry editor. |
| `FlatPad.Core/FlatPad` | The Flat Pad recipe itself: build, install, uninstall, verify. |
| `FlatPad.Cli` | Dev entry point. Not shipped. |

## Licence

MIT — see [`LICENSE`](LICENSE). Third-party components and the asset-redistribution position are in
[`THIRD-PARTY.md`](THIRD-PARTY.md).
