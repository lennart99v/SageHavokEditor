0.6.0 Features:

- Graph tab → right-click a state → "🔗 New behavior reference…". Creates an hkbBehaviorReferenceGenerator — the bridge node for Nemesis/Pandora-style patches that link a custom behavior file into a vanilla graph — and points that state's generator at it in one undoable action, same flow as the clip generator: replace-confirmation if the state already has one, wired through Children/InnerObject at creation so it survives the orphan-pruning .hkx save. Node defaults come from HKX2 via ModifierCatalog.CreateDefault (correct signature, no hand-maintained param table). The confirm + wiring halves of the clip flow were extracted into shared helpers (ConfirmReplaceGenerator / WireGeneratorIntoState) so both entries stay in lockstep. The path prompt is deliberately plain text, no file browser — the referenced file usually doesn't exist yet when the bridge is authored. New Guide section "Referencing Another Behavior File" covers the events-link-by-name rule and the sharp edges (typo'd path = silent T-pose in-game, not an error; the referenced file needs its own valid root scaffolding).

0.5.0 Features:

- added a warning when a loaded graph yields 0 syncable SMs, something like "No state machines in this graph have syncVariableIndex set — active-state tracking is unavailable. Live variables will still update."
- added editor action that wires a sync variable onto a selected state machine (create the int variable, set syncVariableIndex) so users can make their own graphs debuggable
- Graph tab → right-click a state → "🎬 New clip generator…" (the safe path). Creates the clip and points that state's generator at it in one action, so it can never be orphaned. If the state already has a generator it asks first, and warns that the old one may itself be dropped if nothing else references it. Fully undoable.
- Clips tab → "+ New Clip Generator" (what he literally asked for). Still there, but it now warns explicitly that the clip is unreferenced and will be dropped on .hkx save until wired, and points at the graph action.
★ WILDCARD (any state) is now the first entry in the Add Transition dialog's From-State dropdown. Picking it branches the write-back from the from-state's own transitions array to the state machine's wildcardTransitions array, and forces FLAG_IS_LOCAL_WILDCARD into the flags (Havok won't treat it as a wildcard without it). Undo/redo already closed over the array generically, so it works unchanged.

Roadmap batch (all four open items):

- Add-Modifier picker now shows a curated **Common** group (hkbModifierList, BSDirectAt/BSLookAt/BSIsActive/BSTimerModifier) above the full A–Z list; filtering searches the full list flat. hkbModifierGenerator was on the roadmap's common list but is deliberately excluded — the picker's only call site wires the chosen class into a *modifier* slot, and a generator there would corrupt the graph (the wrap path already creates the ModGen wrapper itself).
- New modifiers are named from their target instead of `New_<Class>`: `GetUpFaceUp` + BSIsActiveModifier → `GetUpFaceUp_IsActive` (vendor prefix and "Modifier" suffix stripped, `_2`/`_3` uniquifier when taken).
- Behavior tree right-click menu: **Jump to in graph** (SM → switches machine, state → highlights in its machine, anything else → drills into the owning state's generator view via the RevealClipNode path), Inspect in Object Data, Copy id / Copy name, Bookmark toggle (kept in sync with the Object Data ★ button). Right-click also selects the item under the cursor first.
- Clip preview annotations are editable: right-click the timeline to add an annotation at that time, right-click a purple tick to edit/delete. Edits write back to the animation file itself (XML or SE HKX, same HkPackfile→XML→HKX pipeline as behavior save) with a one-time `<file>.bak` beside it before the first overwrite. New tracks/annotations are created inline through Children (never Value) and wired into the track in the same action, numelements maintained, array kept time-sorted, track 0 by hkanno convention. Undoable — undo/redo re-applies the inverse/original edit to the file and refreshes the preview. Preview cache is invalidated per-file after each edit.

Annotation quick-win pass (usability on top of the editing feature):

- Single **AnnotationDialog** replaces the two sequential input prompts: text + linked time/frame fields (edit either, the other follows; frame grid = duration/numFrames, matching FrameAt). OK clamps time into [0, duration] and refuses empty text.
- **Double-click** the timeline to add at that spot, double-click a tick to edit it (Preview-tunneling on the scrub area, routed by the hit element's DataContext). Right-click menus still work.
- **＋ button** next to play and the **A** key add an annotation at the playhead (key hooked on the host window, ignored while typing in a text box).
- Add flows pre-fill the **nearest frame boundary**; editing an existing annotation keeps its exact time unless changed.
- Ticks got a 12px transparent **hit twin** (tooltip, cursor, clicks) over the 2px visible line — no more pixel hunting; HighlightTicks still recolors via the visible line's Tag.
- **Playhead survives annotation edits**: a same-duration reload keeps the scrub position and paused pose instead of resetting to 0 and re-triggering autoplay; loading a different clip now explicitly resets to 0 (previously the slider kept a stale position).

Annotation power pass (interchange + direct manipulation):

- **hkanno text interchange** on the timeline right-click menu: copy all to clipboard, export to .txt (with hkanno's `# numOriginalFrames` / `# duration` header so the file round-trips through `hkanno update` unchanged), import & replace from .txt, paste & replace from clipboard. Import clamps out-of-range times (warns with a count), lands everything on track 0, and the whole swap is ONE undoable step — new `ReplaceAll` edit kind whose inverse swaps the old/new snapshots, so undo restores annotations to their original tracks (tracks are kept, only their annotations arrays are rewritten).
- Copy/export also work in **read-only** previews — the timeline menu now opens without OnAnnotationEdit and just hides the mutating entries.
- **Drag a purple tick** to move an annotation: 4px dead zone before the press becomes a drag, frame-snapped while dragging (**Alt** = free placement), the time label live-updates with time + frame, release commits a normal Edit (undoable). Capture loss (Alt+Tab etc.) snaps the tick back with nothing committed. Ctrl+click-to-seek is unchanged and trigger ticks still fall through to the slider.
- Export's default filename comes from the previewed animation (`<anim>.anno.txt`) via a new AnimationPath handoff from MainWindow.

Clip trigger editing (orange ticks join the purple ones):

- Timeline right-click → "⚡ Add trigger", right-click/double-click an orange tick → edit/delete, drag to move (same frame-snap + Alt behavior as annotations). All behavior-side: edits mutate the in-memory graph through the normal undo stack and land on the next behavior save — no animation file IO.
- **ClipTriggerDialog**: editable event combo (pick an existing event or type a new name — the event is created as part of the same undo action, id = end of EventList), linked time/frame fields, and a "relativeToEndOfClip" checkbox. Time is always entered as absolute clip time; the negative from-the-end localTime is computed on save, so anchored triggers keep their distance from the end when a longer animation is swapped in.
- Sharp edges handled: a clip with `triggers = null` gets a new hkbClipTriggerArray created **with an #id and wired through Children/InnerObject in the same action** (orphan-prune safe); editing an array referenced by several clip generators warns with the list of affected clips first; edits replace the trigger object instead of mutating it (keeps undo's before/after list snapshots honest) while unchanged params — including payload objects — are carried over by instance; stale preview indexes are guarded by re-checking the event id at the recorded position.
- Trigger changes refresh the Triggers tab (if that clip is selected) and reload the preview in place (playhead kept).

Annotation list panel + multi-track pass:

- **☰ toggles an annotation list panel** (right side of the preview, state persisted via AppSettings.PreviewAnnotationList): a themed DataGrid of time / frame / track / text. Click a row → seek there (guarded so programmatic rebuilds don't seek). **Time and Text edit inline** — cell commit goes through the same Edit pipeline as the dialog (undoable, file write-back, reload); invalid input (unparseable time, empty text) just refreshes the rows back, deferred past the DataGrid edit transaction. **Del** deletes the selected row (ignored while typing in a cell). Read-only previews get a read-only grid. The Trk column hides itself for single-track files.
- **Panel polish**: play button's ⏸ no longer clips (forced Segoe UI Symbol so the glyph renders as monochrome text, not oversized emoji); Fr/Trk column headers grew tooltips; the panel header has a ＋ add-at-playhead button; and rows have a right-click menu — add at playhead, edit via dialog, delete (right-click selects the row first; empty space below the rows still offers add).
- **Bigger preview window**: pops up at 900×660 instead of 520×480, and since the window is recreated on each preview, it now remembers the size you resize it to (AppSettings PreviewWindowWidth/Height, clamped to the screen work area; maximized bounds aren't saved).
- **Multi-track aware add dialog**: AnimationClip now carries AnnotationTrackNames (parsed per track), and AnnotationDialog grows an optional track row — only visible when the file has >1 track. Add flows default to track 0; the edit dialog shows the annotation's track locked (cross-track moves aren't supported — that would be a delete+add).

Three sharp edges handled:
- A state machine with no wildcards may not carry the wildcardTransitions param at all, so it's added rather than assumed.
- Writing the array reference goes through Children/InnerObject, not Value — your stored HkParam note again.
- Editing an existing wildcard row now preselects ★ WILDCARD in the combo (a wildcard row's OwnerState == null, and that null is the marker).
