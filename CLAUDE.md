# SageHavokEditor — working notes

WPF editor (.NET 8, Windows-only) for Skyrim Havok behaviour files.
`libs/HKX2Library/` is a vendored MIT subtree — read its own README before
changing anything in it. `tools/` holds research harnesses, not shipped code.

## Build

```pwsh
dotnet build SageHavokEditor/SageHavokEditor.csproj -c Debug   # must be 0 errors
```

Warnings are tolerated (pre-existing nullable ones in the big UI files); don't
add new categories. There is no automated UI test suite, so running the app and
exercising the change is part of finishing it — and say which files you opened.

**If you touched anything under `SageHavokEditor/Core/`, build the harnesses too:**

```pwsh
dotnet build tools/Harnesses.slnx -c Debug                     # must be 0 errors
```

Most harnesses reach into the app with `<Compile Include>` on individual files
rather than referencing it, which is what lets a console harness exercise
WPF-adjacent code — and means a new dependency in a shared `Core` file breaks
every csproj that lists that file and not its new neighbour. The app still
builds, so nothing tells you. `hkx-graph-doctor`, `hkx-hky-export` and
`hkx-yaml-import` sat unbuildable from 0.8.0 (when `GraphDoctor` picked up the
`AnimData` reader) until 2026-09-21 for exactly that reason. `hkx-layout-gen` is
Python and isn't in the solution.

## Git flow — commit straight to master

**Changed 2026-09-16 by the maintainer; this section said the opposite before.**
Work on `master`. No feature branches, no PRs.

1. `git pull --ff-only origin master` before starting, and again before pushing —
   more than one session works in this tree at a time.
2. Commit per logical change, in the existing style: imperative subject, and a
   body that explains *why* and the mechanism rather than restating the diff.
   Each commit lands on `master` as itself, so the subject is what shows up in
   `git log` and in a bisect — there is no squash to tidy it up any more.
3. `git push origin master`.

The ruleset on `master` was relaxed to match: the pull-request requirement is
gone, and **deletion, force-push and non-linear history are still blocked**. So a
push that is rejected is a real problem — normally that you are behind and need
to pull — rather than the protection it used to be. Don't reach for `--force`;
it is refused, and it is refused for a good reason.

If you genuinely need a branch for something (a spike you may throw away), that
is fine — just don't make one purely out of habit.

## Documentation duty

A user-visible change isn't done until the docs move with it:

- `CHANGELOG.md` — under `## [Unreleased]`, newest first, `### Added` /
  `### Fixed`. Entries explain the mechanism and how the bug was found.
- `ROADMAP.md` — check the item off, or add it as `[x]` with what was learned.
- `SageHavokEditor/Update-Info.md` — one prose paragraph per feature, in the
  in-app release-notes voice (this is what users read in the update dialog).
- `SageHavokEditor/UI/Dialogs/DocumentationView.xaml.cs` — the in-app Guide,
  whenever the change adds or alters something the user clicks.

## Releasing

`RELEASING.md` has the whole process. **"Ready the release"** / "ready the 0.7
zip" means: bump the version, stamp the changelog, merge that PR, tag, publish
the build, zip it, verify it, and hand it back — then stop. Publishing the
result is separate: a GitHub release and the Nexus upload each get asked for on
their own, and the Nexus one is always done by hand.

Two things that bite: the version lives only in `SageHavokEditor.csproj` (three
properties) and gets forgotten, and the build must happen *after* the bump
commit lands or the binary's embedded sha points at the wrong commit.

## Domain traps worth remembering

- Saving `.hkx` silently prunes objects unreachable from the root — a new object
  must be wired into its parent in the same action or it disappears.
- Mutating a `#ref` means updating the resolved `Children` cache too, not just
  `HkParam.Value`, or the edit doesn't stick.
- `numelements` is authoritative on XML→HKX conversion; a stale count truncates
  the array.
- The domain's failure mode is silent: a wrong id or a desynced parallel array
  T-poses in-game with no error. Prefer a check in `HavokValidator` over trust.
