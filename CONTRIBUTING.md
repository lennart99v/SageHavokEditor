# Contributing to SageHavokEditor

Thanks for taking the time to contribute. This document captures the bare
minimum you need to know to send a useful patch.

## Before you start

- For anything more than a small bug fix, **open an issue first** to check
  it fits the project's direction. Saves you re-work and saves me having
  to decline a finished PR.
- Search existing issues / PRs first so you don't duplicate effort.

## Setting up

Requirements: Windows 10/11 (x64) and the .NET 8 SDK. See the [README](README.md)
for the full build commands.

```pwsh
git clone https://github.com/lennart99v/SageHavokEditor.git
cd SageHavokEditor
dotnet build SageHavokEditor/SageHavokEditor.csproj -c Debug
```

The `libs/HKX2Library/` subtree is vendored — you don't need to clone
anything separately.

## Making changes

1. Create a feature branch off `master`:
   ```pwsh
   git checkout -b fix/short-description
   ```
2. Make your change. The repo has an `.editorconfig` — please don't fight it.
   If you use Visual Studio or Rider, it'll be picked up automatically.
3. Build clean: `dotnet build -c Debug` should produce 0 errors. Warnings
   are tolerated (there are still some pre-existing nullable ones in the
   big UI files) but please don't add new categories.
4. **Run the app and exercise the change manually.** There is no automated
   UI test suite — manual verification is part of the deal.
5. Commit with a clear message. One logical change per commit. If you
   end up with `WIP` / `fixup` commits, please squash them before opening
   the PR.

## Opening a pull request

- Push your branch to your fork.
- Open a PR against `lennart99v/SageHavokEditor:master`.
- In the description, explain **why** (not just what) and any manual
  testing you did.
- If the PR closes an issue, include `Closes #123` in the description.

The merge method is **squash** — your branch's commits will be collapsed
into a single commit on `master`. Don't worry about cleaning history past
the point of being readable.

## Scope of contributions welcomed

- 🟢 Bug fixes
- 🟢 Build / dependency / packaging improvements
- 🟢 Documentation, README, screenshots
- 🟢 Test cases (especially for `Core/` parsers and patching logic)
- 🟢 New file format support (other Havok versions, other games using
  the same packfiles)
- 🟡 New features — please open an issue first to align on scope
- 🟡 Large refactors — same: open an issue first
- 🔴 Reformatting passes over files you're not otherwise touching

## Licensing

SageHavokEditor is licensed under **GPL-3.0** (see [LICENSE](LICENSE)).

By submitting a contribution you agree it's released under the same
license. GitHub's terms call this "inbound = outbound" and it's the
default unless you state otherwise — but flagging it here so there's no
ambiguity.

The bundled `libs/HKX2Library/` is third-party MIT code by ret2end; do
not modify it as part of an unrelated PR. If you need to fix something
inside HKX2Library, contribute upstream at
[ret2end/HKX2Library](https://github.com/ret2end/HKX2Library) and we'll
pull the change in.

## A few things to leave out

- Don't commit hardcoded local paths (`C:\Users\you\...`) or personal
  info in code, comments, or screenshots.
- Don't commit binary release artifacts — `*_Dist/` and `*_Dist.zip`
  are gitignored already.
- Don't change the `AppData` folder name (`SageHavokEditor/`) without
  noting it — existing users' settings live there.
