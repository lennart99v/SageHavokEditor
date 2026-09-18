# Releasing

How a version of Sage Havok Editor gets from `master` to a Nexus upload. Written
down after cutting 0.6.0; every command here is one that was actually run.

Saying **"ready the release"** (or "ready the 0.7 zip") means: do everything in
*Cut the release* below, stop before *Publish*, and hand back the zip plus the
version-bump commit. Publishing steps are separate and each needs to be asked
for — they are public and hard to walk back.

## Before starting

- Every feature for the version is on `master`; the tree is clean and up to date.
- `CHANGELOG.md` has an `## [Unreleased]` section holding this version's entries.
  Expect its `### Added` / `### Fixed` headings to be repeated rather than merged;
  step 2 collapses them. This survives the move off PRs: each session still
  prepends its own block.
- `SageHavokEditor/Update-Info.md` has its paragraphs. Its heading is normally
  still `Unreleased:` and needs changing to `<version> Features:` — the form the
  in-app update dialog groups on. 0.7.0 found it unchanged.
- Decide the version number. The changelog's newest entries and `Update-Info.md`'s
  paragraphs tell you what is in it.

## Cut the release

**On `master`, directly.** This document was written when releases went through
`chore/release-<version>` and a PR; that flow was dropped on 2026-09-16 (see
`CLAUDE.md`), and the ruleset on `master` was relaxed to match. The bump is an
ordinary commit now. Nothing else about the sequence changes — the ordering
constraint in step 3 is about the build seeing the bump, not about how it landed.

**1. Bump the version.** It lives in exactly one place —
`SageHavokEditor/SageHavokEditor.csproj`, three properties that move together:

```xml
<Version>0.6.0</Version>
<FileVersion>0.6.0.0</FileVersion>
<AssemblyVersion>0.6.0.0</AssemblyVersion>
```

Nothing else hard-codes a version. The single-file build takes its
`ProductVersion`/`FileVersion` from here, so a stale value ships a binary that
misreports itself in Explorer and in any version comparison. It was left at
`0.5.0` for the whole 0.6 cycle — check it, don't assume.

**2. Collapse the changelog sections, then stamp it.** A version assembled over
more than a handful of sessions arrives with its `### Added` / `### Fixed`
headings repeated: each one prepends its own block rather than merging into the
ones already there. 0.7.0 reached five — `Added`, `Fixed`, `Fixed`, `Added`,
`Fixed` — across 22 PRs, with one feature filed under `Fixed`. 0.8.0 reached four
(`Added`, `Fixed`, `Added`, `Fixed`) over 22 entries, none misfiled.

Group the section into one `### Added` and one `### Fixed`, keeping every entry's
text exactly and moving only entries under the wrong heading. Check it by sorting
the section's non-heading, non-blank lines before and after and diffing — the two
sets should be identical, which proves only the grouping moved.

Do this **before** stamping, and before the tag. The release notes are generated
from this section, so it is what a reader meets on the release page. Fixing it
afterwards is expensive rather than merely late: 0.7.0 had already been tagged and
built, so the correction meant moving the tag, rebuilding and re-zipping, which
invalidated the sha256 that had already been recorded and handed over.

Then `## [Unreleased]` becomes `## [<version>] — <date>`
(em dash, ISO date), and a link line goes at the top of the block at the bottom:

```
[0.6.0]: https://github.com/lennart99v/SageHavokEditor/releases/tag/v0.6.0
```

**3. Commit and push the bump.** Do this *before* building: the build stamps the
current commit into `ProductVersion` as `0.6.0+<sha>`, and that sha should be the
commit the tag will point at. Build first and the artifact's provenance points at
the commit *before* the bump.

**4. Tag that commit**, annotated (`-a`), message
`Sage Havok Editor v<version>` plus a short summary of the release:

```sh
git tag -a v0.6.0 -m "Sage Havok Editor v0.6.0

<one paragraph listing the headline features>"
git push origin v0.6.0
```

**5. Publish the build** from the tagged commit:

```sh
rm -rf SageHavokEditor/bin/Release
dotnet publish SageHavokEditor/SageHavokEditor.csproj -c Release -p:PublishProfile=FolderProfile
```

Output lands in `SageHavokEditor/bin/Release/net8.0-windows/win-x64/publish/win-x64/`.
Warnings are expected (the pre-existing nullable ones); errors are not.

**5b. Build the SKSE plugin.** The live debugger's game half ships in the same
zip, and its source is **not in this repository** — it lives at
`C:\SkyrimBehaviorDebugger\` and is not yet in version control (see *Live
debugger* in `ROADMAP.md`). Build it from a machine that has it:

```pwsh
cmake --build C:\SkyrimBehaviorDebugger\build --config Release
```

Output: `C:\SkyrimBehaviorDebugger\build\Release\SkyrimBehaviorDebugger.dll`.
Run its one test first — it exercises the connect / disconnect / reconnect cycle
the editor depends on, which is otherwise only reachable by launching Skyrim:

```pwsh
# from a VS x64 developer prompt, in C:\SkyrimBehaviorDebugger
cl /std:c++20 /EHsc /nologo /Fe:build\pipe_reconnect_test.exe /Fo:build\ `
   test\pipe_reconnect_test.cpp /I src
.\build\pipe_reconnect_test.exe      # exit code 0 = pass
```

**6. Build the zip** as `SageHavokEditor/SageHavokEditor_v<version>.zip` —
gitignored by `SageHavokEditor_v*.zip`, so it never gets committed:

```
SageHavokEditor.exe                            the published single-file exe, ~156 MB
LICENSE                                        repo root, GPL-3.0
SKSE Plugin/INSTALL.txt                        docs/SKSE-Plugin-INSTALL.txt, renamed
SKSE Plugin/SKSE/Plugins/SkyrimBehaviorDebugger.dll   from step 5b
```

The plugin is optional — it is only needed for the Debugger tab, and the editor
runs without it — so it goes in a subfolder rather than loose at the root, where
it would look like something the editor needs. The nested `SKSE/Plugins/` path
inside that folder is the layout a user drops into `Data`, and it is also what a
mod manager expects if they zip the folder and install it as a mod. `INSTALL.txt`
explains both.

The publish directory also contains a stray `HKX2.pdb` — the vendored library
doesn't set `DebugType None` the way the main project does. **Leave it out.**

v0.4.0 and v0.5.0 shipped the exe alone; `LICENSE` was added in 0.6.0 because
GPL-3.0 §4 asks for the licence text to travel with the binary. The SKSE plugin
was added in 0.8.0 — before that the Debugger tab was documented but the plugin
it needs was never distributed, so the feature worked on one machine only.

## Verify before handing it over

All of these, not a subset:

- `(Get-Item $exe).VersionInfo` — `ProductVersion` is `<version>+<sha of the tag>`,
  `FileVersion` is `<version>.0`, `ProductName` is `Sage Havok Editor`.
- The published exe launches and the window responds.
- **Extract the zip to a clean directory and launch that exe too** — this is the
  copy people download, and it is the one worth trusting.
- The extracted `SKSE Plugin/SKSE/Plugins/SkyrimBehaviorDebugger.dll` is the one
  step 5b built, not a stale copy — check its timestamp against the build.
- Record `Get-FileHash -Algorithm SHA256` of the zip; the release notes quote it.

## Publish — each step asked for separately

**GitHub release.** The repo is public, so this puts the binary in front of
everyone:

```sh
gh release create v0.6.0 "SageHavokEditor/SageHavokEditor_v0.6.0.zip" \
  --title "Sage Havok Editor v0.6.0" --notes-file <notes> --verify-tag
```

Notes are generated from the changelog's section for this version rather than
retyped, with a short header above it: what to download, that it is
self-contained so no .NET install is needed, that SmartScreen warns because the
build is unsigned, the sha256, and the licence. Footer links the changelog at
the tag and the `v<previous>...v<this>` compare.

**Nexus upload.** Always done by hand, by the maintainer. Never automated here.

## Notes

- The build is self-contained `win-x64`, single-file — no .NET runtime needed on
  the user's machine, which is why the zip is ~63 MB compressed.
- It is unsigned, so SmartScreen warns on first run. Worth saying in the notes.
- `Data/Skeletons/` next to the exe is optional at runtime and empty in the repo;
  nothing to ship there.
