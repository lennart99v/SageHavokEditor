# Releasing

How a version of Sage Havok Editor gets from `master` to a Nexus upload. Written
down after cutting 0.6.0; every command here is one that was actually run.

Saying **"ready the release"** (or "ready the 0.7 zip") means: do everything in
*Cut the release* below, stop before *Publish*, and hand back the zip plus the
version-bump PR. Publishing steps are separate and each needs to be asked for —
they are public and hard to walk back.

## Before starting

- Every feature PR for the version is merged; `master` is clean and up to date.
- `CHANGELOG.md` has an `## [Unreleased]` section holding this version's entries.
  Expect its `### Added` / `### Fixed` headings to be repeated rather than merged;
  step 2 collapses them.
- `SageHavokEditor/Update-Info.md` has its paragraphs. Its heading is normally
  still `Unreleased:` and needs changing to `<version> Features:` — the form the
  in-app update dialog groups on. 0.7.0 found it unchanged.
- Decide the version number. The changelog's newest entries and `Update-Info.md`'s
  paragraphs tell you what is in it.

## Cut the release

Branch: `chore/release-<version>`.

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

**2. Collapse the changelog sections, then stamp it.** A version assembled from
more than a handful of squash merges arrives with its `### Added` / `### Fixed`
headings repeated: each merge prepends its own block rather than merging into the
ones already there. 0.7.0 reached five — `Added`, `Fixed`, `Fixed`, `Added`,
`Fixed` — across 22 PRs, with one feature filed under `Fixed`.

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

**3. Commit and open the PR**, then **merge it**. Do this *before* building:
the build stamps the current commit into `ProductVersion` as
`0.6.0+<sha>`, and that sha should be the commit the tag will point at. Build
first and the artifact's provenance points at the commit *before* the bump.

**4. Tag the merge commit**, annotated (`-a`), message
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

**6. Build the zip** as `SageHavokEditor/SageHavokEditor_v<version>.zip` —
gitignored by `SageHavokEditor_v*.zip`, so it never gets committed. Two entries,
flat, no folder:

```
SageHavokEditor.exe    the published single-file exe, ~156 MB
LICENSE                repo root, GPL-3.0
```

The publish directory also contains a stray `HKX2.pdb` — the vendored library
doesn't set `DebugType None` the way the main project does. **Leave it out.**

v0.4.0 and v0.5.0 shipped the exe alone; `LICENSE` was added in 0.6.0 because
GPL-3.0 §4 asks for the licence text to travel with the binary.

## Verify before handing it over

All of these, not a subset:

- `(Get-Item $exe).VersionInfo` — `ProductVersion` is `<version>+<sha of the tag>`,
  `FileVersion` is `<version>.0`, `ProductName` is `Sage Havok Editor`.
- The published exe launches and the window responds.
- **Extract the zip to a clean directory and launch that exe too** — this is the
  copy people download, and it is the one worth trusting.
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
