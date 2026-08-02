# Third-party components

## ACEvo.Package — Nenkai

<https://github.com/Nenkai/ACEvo.Package> · MIT License · Copyright (c) 2025 Nenkai

Reads and extracts Assetto Corsa EVO `.kspkg` archives. This installer references the
`ACEvo.Package` **library** (not its CLI) to detect and unpack the game archive, because a track can
only be loaded from an unpacked install.

The full licence text ships alongside the library in `external/ACEvo.Package/LICENSE.txt`.

> Not yet wired up: the unpack step lands with the GUI. Until then the installer only reads and
> verifies an already-unpacked install.

---

# What this tool does *not* redistribute

The Flat Pad track is **derived on your machine from your own copy of the game**. Its geometry,
textures and irradiance volumes originate from Assetto Corsa EVO (© Kunos Simulazioni) and are
never shipped with this tool. Nothing in this repository contains game assets.
