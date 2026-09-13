# Interleaved animation regression tests

Run from the repository root with the .NET 8 SDK:

```powershell
dotnet run --project tools/hkx-interleaved-tests -c Release
```

The harness compiles the editor's actual parser and annotation saver, and the
vendored HKX2 library. It generates its own fixtures in a temporary directory
and deletes them afterwards; no game files are needed or modified. Failure
returns a nonzero exit code.

Coverage includes frame-major translation/rotation/scale samples, binding
selection, reference-pose fallback, sample timing, single-frame and float-only
clips, malformed arrays, Turkish numeric culture, and spline regressions.
Annotation add/edit/delete/replace/undo is checked through XML, LE and SE files.
Binary checks compare the decoded transform and float values exactly, including
values below the legacy reader's six-decimal precision, and check root motion,
bindings, untouched annotations, backups, and edition preservation.

The precise-reader test also exercises both byte orders and verifies that
default callers retain the legacy reader behavior. It does not claim support
for additional console packfile formats.

## Manual Windows verification still required

Build the editor:

```powershell
dotnet build SageHavokEditor/SageHavokEditor.csproj -c Debug
```

1. Open a behavior with an interleaved animation and its matching skeleton.
2. Preview the clip; play, scrub, and check the final pose at the timeline end.
3. Add, move, edit and delete an annotation, then undo the changes.
4. Close and reopen the preview to verify the saved file loads again.
5. Repeat with a known spline-compressed clip.

Automated tests use synthetic fixtures. They do not substitute for a Windows
launch test or testing the particular animation that produced the reported
error.

## Scope

This change adds preview and annotation editing for
`hkaInterleavedUncompressedAnimation`. It does not implement animation
recompression, keyframe authoring, delta/wavelet/quantized decoding, or rendering
float tracks. The existing FBX exporter still exports translation and rotation
only; scale animation export is outside this change. Binary annotation edits
refuse a container with multiple supported clips because the preview has no
clip selector.
