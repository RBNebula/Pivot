# Pivot

Pivot now registers its screwdriver through ToolIntegrator (`com.main.toolintegrator`).

## Asset source files used by this project

These files are compiled as embedded resources:

- `models/screwdriver-world.glb`
- `models/screwdriver-view.glb`
- `screwdriver-icon.png`

## Build

- Build `Pivot.csproj` (`net472`).
- Output DLL: `Pivot/bin/Release/net472/Pivot.dll`.

## Install

1. Copy `Pivot.dll` into `BepInEx/plugins/Pivot/`.
2. Copy `ToolIntegrator.dll` into plugins (or install the ToolIntegrator mod).
3. Launch game.

## Runtime behavior

- Pivot registers tool id `pivotmod.screwdriver` via ToolIntegrator.
- Pivot sets `CacheNamespace` (defaults to `pivotmod`) so cached GLB/PNG files are isolated under `.tool-import-cache/pivotmod`.
- Pivot currently uses default arm handling (no custom hand grip override).
- Pivot actions are active only while holding that imported tool.
- Input mapping:
- Left click: primary visual action
- `R` (Rotate key): rotate looked building +90
- Right click or `Q`: alternate/Q action logic
