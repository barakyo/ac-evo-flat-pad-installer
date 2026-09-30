# EvoMods Manager

A Windows app for modding _Assetto Corsa EVO_: unpack the game, install the Flat Pad test track,
unlock the post-processing filters the game hides, install more, and tune the chase camera for video
capture.

| | |
| --- | --- |
| **Game content** | Unpack the game so mods load at all, revert it, or take any `.kspkg` apart. |
| **Flat Pad** | A 1.5 km dead-flat, wall-free test track, derived on your machine from your own game files. |
| **Tracks** | Install a custom track from a `.zip` or folder — drop it on the page — and register it so it appears in the menus. |
| **Filters** | Show the nine filters EVO ships hidden; install five built for video capture. |
| **Camera** | Five chase-camera presets, the eight values behind them, and the settle time shown next to every value. |

It updates itself from GitHub Releases. Download the latest `EvoMods.Manager-win-Setup.exe` from
[Releases](https://github.com/barakyo/evomods-manager/releases); after that it offers new versions on
its own.

## Game content

EVO ships a `content.kspkg`. When that archive is present the game reads everything from it and
**ignores loose folders entirely**, so nothing can be modded until it is unpacked. Tracks in
particular are not loaded from `Saved Games\ACE\mods\`, which is why this is the first screen.

Unpacking builds on [Nenkai's ACEvo.Package](https://github.com/Nenkai/ACEvo.Package) and roughly
**doubles the install**: the archive is kept, renamed `content.kspkg.bak`, and its ~68 GB of contents
written out alongside. That figure is computed from your archive rather than hardcoded, and shown
before anything is touched. **Nothing is renamed until every file is out**, so cancelling or crashing
halfway leaves the game exactly as playable as it was, and re-running continues.

⚠️ While unpacked, do **not** use Steam's _Verify integrity of game files_ — it re-downloads the whole
archive. A game update also restores packed mode; unpacking again fixes it.

Reverting puts a renamed archive back. When several are present it asks which rather than guessing: a
game update downloads a fresh one, so installs accumulate them, and restoring an older build's
archive under a newer game is the failure worth a dialog.

**Unpack a `.kspkg`** is separate from all of that. It points at any package — a car mod, or the
game's own archive — and writes its contents into a folder beside it. Nothing about the install
changes: no archive is renamed, no registry touched. Before writing it reports the file count, the
total size, and the folder every file shares — `content\cars\nissan_skyline_r34_gtr\`, so you can see
which car you have. Cancelling is checked between files, never during one, so everything on disk when
you stop is complete rather than half-written. It is the one action that needs no game folder at all.

## Flat Pad

A clone of Sebring with everything stripped away but the track surface, built for physics testing —
where driving 800 m across a real circuit before every run gets old fast.

The track is **derived on your machine from your own copy of the game**; nothing is downloaded. Adding
files is not enough on its own: a track only appears in the menus once it is registered in two
`system\*.table` registries.

Install stays available in every state, because it is also the repair action. A game update re-packs
the game and restores stock content, and re-running install is the documented fix — so the label
changes rather than the button greying out.

## Custom tracks

Drop a `.zip` or a folder on the Tracks page, or choose one. It accepts the layout EvoForge produces —
`content\tracks\<id>\<id>.scene` plus `containers\layout_<name>.scene` — at any depth, so an archive
with an extra wrapper folder still works. Before anything is written it shows the file count and size,
the name and layout name it will register (both editable), the sessions each layout gets, and anything
worth knowing, such as a missing AI line.

The files go to the **game folder**, `content\tracks\<id>`, not `Saved Games\ACE\mods\` — tracks do not
load from there, whatever a download's README says. Like Flat Pad, it needs the game unpacked.

A converted track ships **no registry entries**, so they are built here. Rows are cloned from Sebring's,
as every row this tool writes is, but every field that names the track is then set outright, and each
session's container list is rebuilt from the files the package actually contains. Kunos suffixes every
container with its layout (`timelines_gp.scene`); EvoForge suffixes only the layout itself
(`timelines.scene`), and a row naming a file the track lacks is a path to nothing. Each layout gets
**Time Attack** (which is what Practice reads), **Hotstint** if it ships a hotlap spawn, and **No Game
Mode**. Race is left out, as it is for Flat Pad. The car count is the track's own grid.

Three safeguards:

- **Only its own rows are ever removed.** Every install is recorded in `<game>\evomods\tracks.json`,
  and a row counts as this tool's only when its name *and* its recorded id match. Another tool that
  registered the same name keeps its rows — unless you tick the box to replace a registration of the
  same track folder, which can never extend to a different track.
- **A damaged download installs nothing.** When the package carries a `SHA256SUMS.txt`, every file is
  checked as it is copied, into a staging folder that is only swapped in once all of them pass.
- **A game patch is repairable without the zip.** Patches revert `system\` but leave `content\tracks\`
  alone; the page reports that as *files present, not registered*, and **Repair** re-registers from
  the files on disk, under the same names and numbers.

Confirmed in game with [HDC Drift Park](https://www.reddit.com/r/assettocorsaevo/comments/1wpy6ub/introducing_hinas_drift_club_three_drift_cars/)
on `0.9.1`: loads in Practice and drives. Uninstall returns both registries **byte-identical** to
before.

## Post-processing filters

Two things, both reversible.

**Unlock the filters the game hides.** EVO registers nine filters without the flag that offers them
in the video options — `TV 1`, `TV 3_1`, `TV 3_low`, `TV 4`, `TV 5`, `Natural 5`, `Natural 6`,
`Natural 8` and `Washed`. They load fine and simply cannot be chosen. Showing one adds two bytes to
its row; hiding it again removes them, and the file comes back byte-identical.

**Install five filters built for video capture** — `Video_Hero`, `Video_Hero_Soft`, `Video_Punch`,
`Video_Cine` and `Video_Clean`. They ship inside the app rather than being downloaded, so installing
needs no network and nothing to drop. See [`THIRD-PARTY.md`](THIRD-PARTY.md) for what they derive
from.

Three things about this format are worth knowing, because all three fail **silently** — the filter
registers, appears in the list, is selectable, and the previously chosen one just keeps rendering:

- **A filter name is a localization key**, not a display string. A new name containing a space never
  loads. The stock names have spaces only because `en.loc` defines them.
- **A filter is not always one file.** Each references seven curves; six resolve against the game's
  own `natural1` folder, and `Video_Hero` and `Video_Hero_Soft` also point into `pure_gamma_full`.
  The installer works out what to copy by reading what each filter actually references, rather than
  from a list someone maintains, so installing one filter on its own still brings what it needs.
- **Registration does not survive a game patch or a Steam file verification.**
  `system/post_processing.table` lives in the game directory, so the files under `content/` survive
  while the rows revert. The screen reports that state as _files present, not registered_; installing
  again puts the rows back.

## Chase camera

Tuning for video capture. The settings are read at startup, so how the car drives and how the camera
looks are fully separate: drive a clean lap on normal settings, exit, set a cinematic camera, relaunch
and record the replay. An undriveable camera is an acceptable one.

Two files, one screen. **Framing** — where the camera sits — is
`Saved Games\ACE\CarCameras.carcamerausersettings`. **Behaviour** — lag and stabilisation — is
`camerasettings.camerasettings`. Which file a setting lives in is not something anyone tuning a
camera should have to hold, so Apply writes whichever of the two changed.

### Framing

Five presets, from the convention the game ships to the most extreme:

| Preset | near Y / D / P | far Y / D / P | FOV n/f | roof vs horizon | car width |
| --- | --- | --- | --- | --- | --- |
| `Stock` | 1.80 / 5.19 / −5.0 | 2.50 / 6.19 / −5.0 | 80 / 65 | −5.1 | 17.4 % |
| `Cinematic` | 1.35 / 4.40 / −4.0 | 1.85 / 5.60 / −3.5 | 85 / 70 | +0.1 | 19.6 % |
| `Aggressive` | 1.15 / 3.90 / −3.0 | 1.55 / 5.10 / −3.0 | 95 / 78 | +2.5 | 20.5 % |
| `Wide` | 1.15 / 4.20 / −3.0 | 1.55 / 5.40 / −3.0 | 105 / 88 | +1.9 | 18.0 % |
| `Hero` | 0.95 / 4.60 / −0.5 | 1.35 / 5.80 / −1.5 | 90 / 75 | +4.5 | 18.1 % |

Y is height in metres, D distance behind, P pitch in degrees. **Roof vs horizon** is the number that
decides the look: how far the car's roofline sits above or below the skyline, in percent of frame
height. Negative reads observational; around zero the roofline rides the horizon; positive cuts above
it. It is shown beside every preset for the same reason the settle time is shown beside chase lag —
`1.15 / 4.20 / −3.0` means nothing on its own.

The counter-intuitive part, and the finding the table was built on: **height does not move the
horizon at all.** The horizon's position is a function of pitch and lens only, so a high horizon needs
steep down-pitch, and steep down-pitch from a low camera aims at tarmac — you cannot have both out of
a fixed-pitch chase cam. What lowering buys is the *aim* landing lower on the car, so the car breaks
the skyline instead of sitting under it.

`Custom` reveals the eight values behind the presets — height, distance, pitch and field of view for
each of the near and far chase cameras — and moving any of them flips the dropdown to `Custom` rather
than letting it keep claiming a preset it no longer matches. Every preset is reachable by hand, so
`Custom` is a true superset rather than a different mode.

Three things about this file are worth knowing:

- **Field of view is global.** One packed array indexed by camera, shared by every car in the file;
  the drivable path has no per-car field to narrow it to. It is written as part of the preset because
  it is most of what makes `Wide` feel wide. The four indices belonging to the views you actually
  drive from are hard-refused.
- **Geometry is per-car, and every car is written.** There is no car picker, because a preset that
  moved the lens on one car and the field of view on all of them would be the confusing half-measure
  rather than the safe one. When the cars disagree the screen reports the framing **most of them are
  on** — it still names the preset rather than falling back to `Custom` — and warns which cars are
  not on it and what they are on instead.
- **Side offset is set to zero rather than preserved.** A non-zero `x` is what dragging the in-game
  gizmo leaves behind; the R34 shipped with 0.25, which is 25 cm toward the passenger side on a
  right-hand-drive car and pushed the subject off frame centre.

⚠️ `Stock` is the shipped **family convention** — every Kunos car is 1.80 / 2.50 — not a byte-exact
revert of your own file, because several cars carry values that were drift rather than design. A
backup is what reverses a specific edit; each write leaves one in `carcameras_backups\`.

### New cars arrive on the shipped framing, silently

The single most surprising thing about this file, and the reason the drift warning exists. It is
**not** a global setting with per-car overrides: the game **appends an entry at the car's own
`.actor` default the first time you drive that car**. Applying a preset only ever covers the cars
that existed *when you applied it*, so every content update quietly adds cars on the shipped framing
while the ones you tuned stay tuned. Nothing in the game says so, and the only symptom is one car
looking different in a replay — noticed after the recording exists.

Confirmed on `0.9.0+release.48`: the file went 13 → 16 cars, and the three new entries —
`ks_audi_r8_lms_gt3_evo_2`, `ks_bmw_m2_coupe`, `ks_porsche_718_cayman_gt4_rs` — were exactly the
three left on Stock `1.80 / 5.19 / −5.0` against thirteen on `Wide`.

So the Camera page names them: how many cars are not on the framing shown, which ones, and what they
are on instead. Apply brings them into line — and **asks first when the file disagrees with itself**,
because "the framing most cars are on" is not the same as "the framing you meant". A file holding two
hand-tuned cars and fourteen the game appended reads as the fourteen, and one unconfirmed click would
flatten the two.

⚠️ Apply covers the cars **in the file**, and a car you have not driven yet is not in it. Reapplying
after an update is therefore not enough on its own — drive the new cars once, then apply. Better
still, check this screen before a capture session rather than after an update.

### Behaviour

⚠️ There are **two** `camerasettings.camerasettings` files and only one is read. The copy under the
game's `system\` has no effect while a user file exists — measured, by cranking a value to 200 in the
game file and seeing nothing change, then making the same edit in `%USERPROFILE%\Saved Games\ACE\`
and seeing it honoured. `%USERPROFILE%\Documents\ACE\` also exists, looks plausible, and is stale.

Two settings were measured as dramatic, and both are chase-camera only, so they can go to any extreme
without affecting how the car drives:

- **Chase lag** (stock 4.5) — first-order lag toward the camera's target orientation; lower is
  laggier. Because it is first-order the settle time is just `1 / value`, which is what the slider
  shows beside the number: `1.5` means nothing on its own, `~0.7 s` is a duration you can picture
  against a corner. It agrees with every settle time measured in game:

  | Chase lag | Settle | Feel |
  | --- | --- | --- |
  | 0.05 | ~20 s | never catches up |
  | 0.5 | ~2 s | still very loose |
  | 1.5 | ~0.7 s | lags visibly, recovers within a corner |
  | 3.0 | ~0.3 s | subtle lag |
  | 4.5 | ~0.2 s | stock |

- **Horizon lock** (stock 0.4) — at 0 the camera sits low and level behind the car, at 1 it sits high
  and looks down. Dramatic on pitched or banked terrain, and **invisible on flat ground**, which is
  why testing it on the flat pad reads as broken.

The rest change the view you actually drive from and are grouped separately. `enableShake`,
`g_forces_shake`, `gForceLag` and `worldAligned` are **not** offered: they were tested to absurd
values on build 0.8.1 and did nothing at all, and a control that does nothing is worse than none.

⚠️ Do not open the in-game camera settings screen after saving — the game rewrites the file and
discards the values.

Both files are edited in place, so fields this tool does not know about survive. That is a deliberate
difference from the reference PowerShell script, which rebuilds `camerasettings.camerasettings` from
the six settings it understands and silently drops the rest. `CarCameras.carcamerausersettings` holds
every car you have driven and all the tuning on them, so it gets more than that: a preflight that
refuses to touch a file which does not already round-trip byte-identical, and a read-back afterwards
checking every slot it meant to write, every slot it did not, the onboard camera section and the
trailers.

## How it avoids breaking your game

Every feature here writes into files the game owns and other mods share — two `system\*.table` track
registries, `post_processing.table`, the camera settings. Get that wrong and there is no error and no
log line: the affected thing is simply absent, or silently inert. This tool got it wrong twice, and
neither time was visible.

**Nothing is ever restored from a snapshot.** Registering reads the **live** file, strips out this
tool's own entries, and adds them back — so re-running cannot stack duplicates, and another tool's
rows survive untouched. A snapshot taken on a previous run would silently revert whatever a game
update added in the meantime, which once cost a base-game track its registration outright. Rows are
only ever added by cloning an existing one and swapping the strings that need to change, because a
field left unfilled is a crash and several of these schemas were never fully recovered.

**Verify reads the registries as the game ships them**, straight out of your `content.kspkg`, and
compares:

```
  registry vs stock (content.kspkg.bak): 20 catalog + 179 session entries in stock, 0 missing live
```

Anything present in stock, missing live, and whose track files are still on disk is a **failure**,
whatever caused it. **Repair** puts those entries back — cloned verbatim from the archive, so each
keeps the id and menu index the game gave it. It is offered after a Verify that found damage, never
as a standing button, because knowing means opening a ~68 GB archive.

Two things it will not do. It declines when several renamed-aside archives are present, because which
one matches your build is a guess. And an entry whose track folder is _not_ on disk is reported as
"not installed here" rather than damage — that is what an archive from a different game version looks
like, and there is nothing to fix.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), and the submodule:

```
git submodule update --init
dotnet build EvoMods.slnx
dotnet test  EvoMods.slnx
```

### Releasing

```
.\build\pack.ps1    -Version 0.4.1             # build an installer, locally
.\build\release.ps1 -Version 0.4.1             # ... and upload it as a DRAFT
.\build\release.ps1 -Version 0.4.1 -Publish    # ... and publish it
```

Packing and publishing are separate commands on purpose. Packing is safe and repeatable; publishing
is neither, because every installed copy watches that feed. Uploads are drafts unless `-Publish` is
passed.

`release.ps1` needs `build\secrets.ps1` — gitignored, copied from `build\secrets.example.ps1` —
holding a fine-grained token scoped to this repo with **Contents: read and write**.

⚠️ **Keep `releases/` between versions.** Velopack builds a delta by diffing against the previous
package sitting in that folder; with an empty folder it can only produce a full ~91 MB package, and
users download all of it.

Updates need no infrastructure beyond GitHub Releases. `vpk` writes a `releases.win.json` manifest
carrying a SHA1, SHA256 and size per package; the client compares versions itself and refuses to apply
anything whose hash does not match. ⚠️ That is integrity, not authenticity — the manifest sits beside
the packages, so whoever can replace one can replace the other. Signing is what would change that;
until then the GitHub account's 2FA is what holds the update channel shut.

### The dev CLI

Not shipped. It exists so the logic can be driven, and diffed against the reference implementation,
without a UI.

```
dotnet run --project FlatPad.Cli -- status
dotnet run --project FlatPad.Cli -- unpack
dotnet run --project FlatPad.Cli -- install
dotnet run --project FlatPad.Cli -- verify
dotnet run --project FlatPad.Cli -- repair
dotnet run --project FlatPad.Cli -- uninstall
dotnet run --project FlatPad.Cli -- revert
dotnet run --project FlatPad.Cli -- check-unpack

# custom tracks
dotnet run --project FlatPad.Cli -- inspect-track   --input "<file.zip | folder>"
dotnet run --project FlatPad.Cli -- install-track   --input "<file.zip | folder>" [--name N] [--layout-name N] [--replace]
dotnet run --project FlatPad.Cli -- uninstall-track --id <track id>
dotnet run --project FlatPad.Cli -- list-tracks
dotnet run --project FlatPad.Cli -- dump-track-rows [--name "<display name>"]

# standalone packages — these need no game folder at all
dotnet run --project FlatPad.Cli -- inspect-package --input "<file.kspkg>"
dotnet run --project FlatPad.Cli -- unpack-package  --input "<file.kspkg>" --out "<dir>"
```

`--game <path>` is auto-detected from Steam if omitted. Every command is idempotent, and Ctrl+C
cancels cleanly. `verify` is read-only, and reports a **count** for every check — a validator that
finds nothing to check would otherwise print a cheerful `PASS`. `check-unpack` samples the loose files
against the archive they came from, which is how you tell a finished unpack from one that quietly ran
out of disk.

It covers Flat Pad, custom tracks and the archive. Filters and camera are exercised by the test suite instead.

`dump-track-rows` prints a track's rows in both registries — the quickest way to see what another
tool (EvoForge) registered, or what a working base-game entry looks like.

### The retired WinForms installer

Up to **v1.1.0** this shipped as `FlatPadInstaller.exe`, a single ~49 MB WinForms build that did
nothing but unpack the game and install Flat Pad. EvoMods Manager does all of that and more, so the
project was removed once it reached parity. The old releases stay published — they still work, and
nothing is served by breaking a download link somebody has. `git log -- FlatPad.App` finds the
history.

## Verification

The Python and PowerShell references stay authoritative until each port is confirmed in-game.

| | |
| --- | --- |
| Track build | **Byte-identical** to the Python across all 1530 files, from a warm install and a cold start. Uninstall matches too. |
| Verify | Console output byte-identical, on a passing install _and_ on a deliberately broken one. |
| Archive reading | 201 files sampled from the real 67.5 GB archive extract byte-identical to disk, across its 115,896-file table. |
| Unpack round trip | End-to-end against a real 599 MB `.kspkg`: detect → free-space check → extract all 650 files → rename aside → detect unpacked → revert → detect packed. Output **byte-identical to Nenkai's own CLI**. |
| Package unpack | 607 files / 532.1 MB out of 650 entries (43 are folders), SHA-256 **identical to Nenkai's own CLI** on every file and no extras. |
| Package cancellation | Cancelled mid-unpack at 30, 570 and 607 of 607 files. Every file on disk byte-identical to a full extract; **none truncated**. |
| Unpack at full scale | Run once for real: **119,443 files / 68.5 GB**, then reinstall and verify. The round trip left all 1530 installed files **byte-identical** to the baseline. |
| Registry repair | Rehearsed against a real registry: a scratch copy with a real base track's 5 entries deleted, repaired from the real 72.5 GB archive. All 5 came back **byte-identical**, and no other entry changed. |
| Filter tables | The stock 1560-byte and a modified 3411-byte `post_processing.table` both round-trip byte-identical, and the nine rows stock ships hidden are exactly the nine the code names. |
| Self-update | 0.3.0 installed by hand, then updated to 0.4.0 over the network from GitHub Releases and relaunched. |
| Chase camera framing | `Wide` applied from the app to the real 12-car file. The reference script's own diff reports exactly the 22 near/far slots changed, **no driven view touched**, and no entry added or removed; its frame-layout figures agree to 0.01 pt. Live file restored byte-identical afterwards. |
| Drift detection | Read against the real 16-car v0.9 file: reports `Wide`, no drift, agreeing with the reference script's own `-Audit` car for car. Against a copy with three cars put back to Stock — the R34 among them — it names those three and falls back to the majority rather than letting the reference car misreport the file. A real 12-car backup from before the app existed reports all 12 framings. |
| Custom track | HDC Drift Park installed from its zip, loaded and driven in Practice. Uninstalled through the app: both registries back **byte-identical** to their pre-install hashes; reinstalled through the picker onto the same id and index. |
| Unit tests | **315**, covering the protobuf layer, the closure crawl, the geometry edits, the archive state machine, progress throttling, registry integrity, custom track packages and registration, the filter table and install plan, and both camera settings files. |

**Console divergence from the Python.** The reference implementation is behind: the five bugs a
real-world v0.8.1 run exposed were fixed here and never back-ported, and it cannot read a `.kspkg` at
all, so it has no registry-integrity check. `diff py.txt cs.txt` on a healthy install shows four
hunks, and the verdicts differ — the Python still reports the _base game's_ own dangling reference as
a failure. Re-measure rather than trusting a remembered number. The file-level comparison is the check
that matters, and it is unaffected: all 1530 bytes still agree.

## Layout

| | |
| --- | --- |
| `EvoMods.Core/Protobuf` | Lossless raw-protobuf tree. Re-emits a node's original bytes unless it was modified, so an untouched file round-trips byte-identical. |
| `EvoMods.Core/Refs` | Reference extraction, the `content\…` closure crawl, and copy-with-repath. |
| `EvoMods.Core/Scene` | Reading and reshaping the geometry a track scene is made of. |
| `EvoMods.Core/Tables` | The `system\*.table` registry editor. |
| `EvoMods.Core/Game` | Finding the install, switching it between packed and unpacked, reading stock files back out of the archive, and unpacking a standalone `.kspkg`. |
| `EvoMods.Core/FlatPad` | The Flat Pad recipe: build, install, uninstall, verify, repair. |
| `EvoMods.Core/Tracks` | Custom tracks: reading a zip or folder, building registry rows from what it ships, the ownership ledger. |
| `EvoMods.Core/Filters` | Post-processing filters: reading `post_processing.table`, showing the ones the game hides, and installing the filters carried in `Filters/Assets`. |
| `EvoMods.Core/Camera` | The camera settings the game actually honours, edited in place so unknown fields survive — behaviour in `camerasettings.camerasettings`, framing and the presets in `CarCameras.carcamerausersettings`. |
| `EvoMods.App` | The WinUI 3 GUI — a shell and a page per feature, no logic of its own. |
| `FlatPad.Cli` | Dev entry point. Not shipped. |

## Licence

MIT — see [`LICENSE`](LICENSE). Third-party components and the asset-redistribution position are in
[`THIRD-PARTY.md`](THIRD-PARTY.md).
