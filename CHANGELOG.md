# Changelog

All notable changes to Sage Havok Editor are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] — 2026-07-12

First release under the **Sage Havok Editor** name. This one is about seeing what
a behavior file actually does — tracing events, following transitions to the right
place, and editing modifiers directly in the graph.

### Added

**Editing modifiers in the graph**

- **Add modifier** — right-click a modifier list or generator node and pick
  `➕ Add modifier…` to open a searchable list of all ~58 modifier classes.
  Depending on what you right-clicked, the new modifier is appended to an
  `hkbModifierList`, dropped into an empty `hkbModifierGenerator` slot, or the
  generator is wrapped in a new `hkbModifierGenerator` with every existing
  reference repointed at the wrapper. It all lands as a single undo step.
- New modifiers are created with a correct signature and a complete set of default
  params, the same shape as objects loaded from a real `.hkx`.
- **`⌂ Root` button** in the Graph tab jumps to the behavior graph root — the
  root generator and root modifier list that sit above the top state machine.
  Those root-level modifiers were previously unreachable from any view.
- `hkbModifierList` contents now appear as nodes in the graph instead of being
  skipped.

**Tracing events and transitions**

- **Wildcard transitions are visible at last.** Every state machine with wildcard
  transitions (the fire-from-any-state ones — special attacks, death states) now
  shows a `★ ANY` node with dashed edges to each target. Previously they had no
  source state and so were invisible in the graph.
- **Go to event** — right-click any graph edge, transition, or SM Inspector row
  and choose `🔎 Go to event` to jump to the Events tab with that event selected
  and all of its usages listed: transitions, wildcards, clip triggers, properties.
- **Enable/disable a transition** without deleting it, from a graph edge or the SM
  Inspector. Disabled edges draw dimmed and dashed with a `⊘` marker. Undoable.
- **Plain-language transition summaries.** Selecting a transition now leads with a
  sentence describing when it fires — *From Idle → "Attack" when the event
  "attackStart" is received AND the condition (Speed > 0.5) is true* — instead of a
  dump of raw fields. Flags are shown as labelled badges explaining what each one
  does rather than a pipe-separated string.
- **Clicking a usage reveals the exact edge in the graph**, drawn in gold and
  centered, switching to the owning state machine if needed.
- **Show animation & tags** — right-click a state node to walk its generator chain
  to the underlying clip and open the clip preview with its triggers and tags.

**Quality of life**

- Boolean params now edit as a **checkbox** instead of a text box you had to type
  `true` into.
- A visible **`↗` jump-to-reference button** next to any `#ref` param value.
  Following a reference previously required a Ctrl+Click you had to know about.
- Double-clicking a leaf node (modifier, clip, blender) in the graph now shows its
  params in Object Data.
- Unresolved event ids display as `‹unnamed #495›` rather than `Event 495`, so a
  bare number can't be mistaken for a real event name.
- Global search is easier to find: the toolbar button is now `🔭 Search All`, and
  every per-tab filter box advertises `Ctrl+G: search everything`. The Guide
  documents the `event:` `state:` `clip:` `var:` `obj:` scoping prefixes.
- Long param names and values wrap instead of being cut off.
- Expanded in-app Guide: tracing and editing triggers, wildcard transitions, and
  the graph right-click menus.

### Fixed

- **Transitions could point at a state in the wrong state machine.** Havok state
  ids are only unique within a machine, but destinations were resolved with a
  global "first state with this id wins" scan. A Troll transition targeting its own
  state 0 could be drawn — and navigated — to an unrelated state of the same number
  in another machine. Destinations now resolve within their own machine only, which
  also fixes event usages being labelled with the wrong destination state.
- **Drilling into a nested state machine did nothing.** Double-clicking a nested
  machine now opens its graph, at any depth. Nested machine nodes also show a drill
  affordance so they look clickable.
- **Breadcrumbs jumped to the wrong view**, popping one level too many. Clicking any
  crumb now goes straight to that ancestor.
- **Re-layout was broken in a drilled view** — it rebuilt the wrong graph. It now
  re-renders whatever view you are actually looking at.
- Right-clicking a row in the Transitions list didn't select it first, so "Go to
  event" acted on whatever happened to be selected before.
- The in-app Guide's section links didn't scroll anywhere if the Guide tab hadn't
  been opened yet.
- Where a transition genuinely isn't visible in the current view, the app now says
  so instead of silently revealing an unrelated one.

### Changed

- Rebranded from Skyrim Havok Editor to **Sage Havok Editor**.
- The release build is a single-file, self-contained `win-x64` executable — no .NET
  runtime install needed.
- The executable now carries proper version metadata (it previously reported itself
  as 1.0.0 regardless of the build).

[0.4.0]: https://github.com/lennart99v/SageHavokEditor/releases/tag/v0.4.0
