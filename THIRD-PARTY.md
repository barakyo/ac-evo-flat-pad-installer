# Third-party components

## ACEvo.Package — Nenkai

<https://github.com/Nenkai/ACEvo.Package> · MIT License · Copyright (c) 2025 Nenkai

Reads and extracts Assetto Corsa EVO `.kspkg` archives. This installer references the
`ACEvo.Package` **library** (not its CLI) to detect and unpack the game archive, because a track can
only be loaded from an unpacked install.

The full licence text ships alongside the library in `external/ACEvo.Package/LICENSE.txt`.

Referenced as a git submodule and used through its **public API only** — no fork. Its `ExtractAll`
has no progress or cancellation hook and its file table is private, so extraction is driven one
entry at a time from our side instead. Keeping the submodule unmodified means
`git submodule update` stays a safe way to pick up format fixes, and the pack format *has* changed
across game versions.

---

# What this tool does *not* redistribute

The Flat Pad track is **derived on your machine from your own copy of the game**. Its geometry,
textures and irradiance volumes originate from Assetto Corsa EVO (© Kunos Simulazioni) and are
never shipped with this tool. Nothing in this repository contains game assets.
