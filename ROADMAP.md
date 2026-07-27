# Roadmap

Planned improvements and polish, grouped by area. Items here are candidates — not commitments.

## Modifiers

- [x] **"Common modifiers" section in the Add-Modifier picker.** The picker currently lists all 58 modifier classes alphabetically. Add a short curated group at the top (e.g. `hkbModifierList`, `BSDirectAtModifier`, `BSLookAtModifier`, `BSIsActiveModifier`, `BSTimerModifier`, `hkbModifierGenerator`) with the full list below.
- [x] **Better default names for new modifiers.** New modifiers are named `New_<Class>`. Either prompt for a name on creation, or derive one from the target node (e.g. wrapping `GetUpFaceUp` with a `BSIsActiveModifier` → `GetUpFaceUp_IsActive`).

## Behavior tree (left panel)

- [x] **Right-click context menu on tree items.** Add a context menu when right-clicking nodes in the behavior tree, with more options than are available today. First option: **"Jump to in graph"** — select/reveal that object in the graph view (drill to the right level and highlight the node). Other candidates to consider: copy id/name, inspect in Object Data, bookmark.

## Animation / clip preview

- [x] **Add/edit/delete clip triggers on the timeline.** The orange ticks are editable like the purple ones: right-click/double-click to edit (event picker with create-new-event, time/frame, relativeToEndOfClip anchor), drag to move, timeline right-click to add. Creates and wires the hkbClipTriggerArray in the same undoable action when the clip has none; warns before editing an array shared by multiple clips.
- [x] **Annotation list panel in the clip preview.** Toggleable table (☰) of all annotations — time, frame, track, text — click a row to seek, edit time/text inline, Del to delete.
- [x] **Track picker for multi-track animations.** The Add-Annotation dialog shows a track dropdown when the file has more than one annotation track (single-track files keep the track-0 default silently).
- [x] **hkanno-format annotation import/export.** Copy/export the clip's annotations as hkanno's `<time> <text>` text format (the interchange format Precision/AMR modders already use), and import/paste a set back, replacing the file's annotations in one undoable step.
- [x] **Drag annotation ticks on the timeline.** Move an annotation by dragging its purple tick — frame-snapped, Alt for free placement.
- [x] **Add/edit annotations on the clip timeline.** The preview already reads an animation's `annotationTracks` and draws each annotation as a timed, labeled tick (purple), but they're read-only. Let users add a new annotation, edit an existing one's time/text, and delete one — useful for mods that drive behavior off annotations (e.g. Precision, Animation Motion Revolution). Requires a write-back into the animation's `annotationTracks` array: create the annotation through Children/InnerObject (not `Value`), and wire it into the track in the same action so it isn't pruned on `.hkx` save. Should be undoable like other object edits. Requested by a user.
