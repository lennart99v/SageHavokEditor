# SageHavokEditor

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)]()

A WPF editor for Skyrim Special Edition Havok behavior, animation and
skeleton files (`.hkx`), with a graph view of state machines, an animation
clip preview, and a Nemesis-style patch system for distributing changes
without shipping modified behavior files.

> Screenshots — TBD.

## Features

- Load and edit Havok packfile (`.hkx`) and XML behavior projects.
- State-machine graph view: pan/zoom canvas, lasso select, inline transition
  and state editing.
- Animation clip preview with skeleton-aware playback (front/side/top).
- Global search across variables, events, clips, transitions and bindings.
- Validation pass over the loaded object graph with click-to-jump issues.
- Nemesis-style patch system: take a snapshot of a vanilla file, edit, then
  export `#XXXX.txt` patch files that can be applied on top of any matching
  vanilla install.
- YAML behavior import/export for diff-friendly source control of behaviors.
- NIF skeleton import for bone-name resolution.
- Undo / redo on object edits.
- Live behavior debugger client (named-pipe protocol).

## Requirements

- Windows 10 / 11 (x64).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
  from source. The published build is self-contained — no runtime install
  needed for end users.

## Build

```pwsh
dotnet build SageHavokEditor/SageHavokEditor.csproj -c Release
```

To produce a single-file, self-contained `win-x64` release bundle in
`SageHavokEditor/SageHavokEditor_Dist/`:

```pwsh
dotnet publish SageHavokEditor/SageHavokEditor.csproj -c Release
cd SageHavokEditor
.\PrepareRelease.bat
```

## Project layout

```
SageHavokEditor/
  Core/                Havok parsing, patching, services, validation
    Animation/         skeleton parser, spline decoder, clip sampler
    Patching/          snapshot, patch generator/applier, Nemesis reader
    Services/          undo/redo, bookmarks, HKX↔XML conversion
    Skeletons/         NIF skeleton reader, bone registry
    Validation/        graph validator
  Models/              data models + view-models
  UI/                  MainWindow, dialogs, graph view, clip preview
libs/
  HKX2Library/         vendored copy of ret2end's HKX2 (MIT, see below)
```

## Licensing

This project is licensed under the **GNU General Public License v3.0** —
see [`LICENSE`](LICENSE).

It bundles [`HKX2Library`](https://github.com/ret2end/HKX2Library) by
ret2end (a fork of Katalash's HKX2), which is licensed under the **MIT
License**. See [`libs/HKX2Library/LICENSE`](libs/HKX2Library/LICENSE).
MIT is GPL-compatible, so the combined work can be redistributed under
GPL-3.0.

## Credits

- [ret2end](https://github.com/ret2end) — `HKX2Library` (Skyrim SE fork).
- [Katalash](https://github.com/katalash) — original `HKX2` library
  (in `DSMapStudio`).
- [figment / hkxcmd](https://github.com/figment/hkxcmd) — XML ↔ HKX
  conversion tool, used at runtime if available.
