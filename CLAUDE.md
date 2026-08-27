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

## Git flow — branch and PR, never push to master

`master` is protected by a ruleset: changes go through a pull request, squash
merge, linear history. The repository-admin role bypasses it, so a direct push
*appears* to succeed while printing `remote: Bypassed rule violations` — that is
the rule being broken, not permission to break it. Don't push to `master`.

1. Branch off an up-to-date `master`:
   `git switch -c feat/short-description` (also `fix/`, `docs/`, `chore/`).
2. Commit per logical change, in the existing style: imperative subject, and a
   body that explains *why* and the mechanism rather than restating the diff.
3. `git push -u origin <branch>`
4. `gh pr create` — fill in the template, then leave it for review.

One feature per PR. The merge is **squash only**, so a PR carrying four features
lands on `master` as one commit nobody can bisect; the PR title becomes that
commit's subject.

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
