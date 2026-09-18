# Sage Havok Editor — Guide

This is the same Guide as the in-app **📖 Guide** tab, from version 0.7.0.
It is generated from the app, so it cannot drift from what the app shows.

## Contents


**Overview**

- [Overview](#overview)
- [What Are Behavior Files?](#what-are-behavior-files)

**Getting Started**

- [Getting Started](#getting-started)
- [The Behavior Tree (left panel)](#the-behavior-tree-left-panel)

**Tabs**

- [Graph Tab](#graph-tab)
- [The Object Data Panel](#the-object-data-panel)
- [Variables Tab](#variables-tab)
- [Events Tab](#events-tab)
- [Transitions Tab](#transitions-tab)
- [Clips Tab](#clips-tab)
- [SM Inspector Tab](#sm-inspector-tab)
- [Bindings Tab](#bindings-tab)
- [Project Tab](#project-tab)
- [Character Tab](#character-tab)
- [Debugger Tab](#debugger-tab)
- [Bookmarks Tab](#bookmarks-tab)

**Clip Preview**

- [The Clip Preview](#the-clip-preview)
- [Editing Annotations](#editing-annotations)
- [Editing Clip Triggers](#editing-clip-triggers)
- [hkanno Import & Export](#hkanno-import--export)
- [Export FBX](#export-fbx)
- [The Annotation & Trigger List (☰)](#the-annotation--trigger-list-)

**Advanced**

- [Creating a New Behavior File](#creating-a-new-behavior-file)
- [Adding a New Animation](#adding-a-new-animation)
- [Duplicating a State](#duplicating-a-state)
- [Referencing Another Behavior File](#referencing-another-behavior-file)
- [Working With Very Large State Machines](#working-with-very-large-state-machines)
- [Blending vs Snapping Transitions](#blending-vs-snapping-transitions)
- [Creating a Wildcard Transition](#creating-a-wildcard-transition)
- [Live Debugging: Setup & Connection](#live-debugging-setup--connection)
- [Reading a Live Session](#reading-a-live-session)
- [Recording & Exporting a Session](#recording--exporting-a-session)
- [Why Active States Are Empty](#why-active-states-are-empty)
- [Tracing & Editing Triggers](#tracing--editing-triggers)
- [Exporting Patches](#exporting-patches)
- [Applying Patches](#applying-patches)
- [Global Search](#global-search)
- [Event Cross-Reference](#event-cross-reference)
- [Compare Files](#compare-files)
- [Validation — the graph doctor](#validation--the-graph-doctor)
- [Skyrim LE ⇄ SE Conversion](#skyrim-le--se-conversion)


# Overview

## Overview

Sage Havok Editor is a WPF-based desktop editor for Skyrim Havok behaviour files (.hkx / .xml). It lets you view, edit, and export behaviour graphs without hand-editing XML. The editor parses the Havok object graph into a typed data model and lets you navigate every object, edit parameters, manage variables and events, and visualise state-machine transitions as an interactive node graph.



**Reading this Guide**

- Text size — the − and + buttons above, or Ctrl+scroll anywhere in the text. The size is remembered between sessions. Reset puts it back to 100%.
- ⭳ Save as Markdown — writes this whole Guide to a .md file with its formatting intact, for reading outside the app or at your own size. There is no separate source document to ask for: the Guide is generated from the app, and so is that file. A copy of it is also published in the repository as docs/GUIDE.md.


## What Are Behavior Files?

Havok Behavior files (.hkx) are the animation logic layer that sits between Skyrim's animation clips and the game engine. They tell the engine which animation to play, when to switch between animations, and how to blend between them — all driven by in-game conditions like speed, weapon type, or combat state.



**Structure**

- hkbStateMachine — the core building block. A state machine contains a set of states and a table of transitions between them. When a triggering event fires, the SM switches to the target state and plays its animation.
- hkbStateMachineStateInfo — a single state inside a state machine. Each state points to a generator (the actual animation source) and optionally holds its own transition table.
- hkbClipGenerator — a leaf node that references a specific .hkx animation clip by path. This is what ultimately plays on the skeleton.
- hkbBlenderGenerator / hkbManualSelectorGenerator — blend or switch between multiple child generators based on variable values, creating smooth pose mixing.
- hkbModifierGenerator — wraps a generator and applies modifiers on top, such as foot IK, look-at constraints, or procedural bone adjustments.
- hkbVariableBindingSet — binds a Havok variable to a specific parameter on an object, so that changing the variable at runtime automatically drives that property.


**Variables and Events**

- Variables are named float, int, or bool values that the game writes every frame (e.g. Speed, Direction, IsSneaking). State machines read these to choose transitions.
- Events are one-shot signals fired by game code or animation notifies (e.g. AttackStart, FootDown). Transitions use events as their trigger condition.

File layout A typical character has three files: a project file (.hkx) that ties everything together, a character file that references the skeleton and lists all available animations, and one or more behavior files that contain the actual state machine logic. Sage Havok Editor can open any of the three and will automatically follow the references to load the full chain.



# Getting Started

## Getting Started

1. Open a file — use Load or drag a .hkx/.xml onto the window. (Starting a mod from scratch? Load → ✨ New behavior file… scaffolds a valid empty behavior; see Creating a New Behavior File.) 2. The editor loads all Havok objects and populates every tab. 3. Navigate to the Graph tab first for a visual overview of the state machines. 4. Edit any value directly in the Variables, Events, or Transitions tabs. 5. Save with the Save button (or Ctrl+S). The file is serialised back to Havok XML. 6. Use the Patch button to produce a Nemesis or Pandora-compatible patch folder.



## The Behavior Tree (left panel)

The left panel shows the behavior as a tree: state machines, the states under them, each state's generator chain, and its transitions. Click any node to load that object into Object Data on the right. The filter box at the top searches node names and opens the path down to every match.


A behavior is not really a tree. The same clip generator, modifier or nested machine is used from many places at once, so drawing it out in full under every one of them would repeat most of the file over and over — on vanilla 0\_master, one clip is reachable 480,290 different ways.



**↗ means "shown somewhere else too"**

- Each object is therefore drawn in full the first time it appears. Later appearances are a single line with a ↗ in front of the name, and no expander.
- A ↗ line is still the object. Click it and Object Data shows exactly what the first appearance shows, and editing it there edits it everywhere it is used — because it is one object, not a copy. That is the point worth taking from the marker: if you change a generator that carries a ↗ anywhere in the tree, every state using it changes.
- To see its contents expanded, use the filter box to find the name, or 🔍 Search All.


**How much opens at load**

- The tree opens breadth-first under a budget of about 500 visible nodes, so a small behavior comes up fully expanded and a large one opens as far as it can afford; the rest expands on click.
- Filtering always opens the path to every match, however deep it is.


# Tabs

## Graph Tab

An interactive node-graph canvas showing every state and transition in the currently selected state machine.



**Mouse controls**

- Scroll wheel — zoom in/out toward the cursor.
- Middle-mouse drag — pan the canvas.
- Left-click a node — select it and open its data in the Object Data panel.
- Left-click drag on empty space — lasso-select multiple nodes.
- Hover a node or transition — a tooltip card shows its key details (state ID, generator, animation path, blend duration). Hovering a transition also enlarges its event label so it stays readable when zoomed out.
- Drag a node — moving it shows pink alignment guides and snaps to other nodes' edges and centres. Hold Alt to disable snapping.
- Double-click a state node — drill down into its generator hierarchy.
- Drag from the right port of a node to another state — create a transition. Valid targets are ringed in green and invalid ones dimmed while you drag.
- Drag the arrowhead end of a transition onto a different state — re-target the transition's destination without recreating it.


**Toolbar**

- Machine selector — choose which hkbStateMachine to display.
- ← Back — return from a drill-down level.
- Search box — type a node name to highlight and jump to it.
- Layout — re-run the automatic layout algorithm.
- Fit — zoom and pan so all nodes are visible.
- Pan-to-active — when the live debugger is running, keep the viewport centred on the currently active state.
- Export PNG — render the current graph to a PNG file.


**Keyboard shortcuts (while the graph has focus)**

- F — fit to view.
- Delete / Backspace — delete the selected node. A state is removed from every state machine that lists it, not just the one you are looking at: Havok lets one state belong to several machines, and leaving it in the others would point them at an object that is no longer in the file. The confirmation says so when there is more than one, and Undo puts all of them back.
- F2 — rename the selected node inline.
- C — wrap selected nodes in a comment box.
- Q — align selected nodes in a horizontal row.
- W — align selected nodes in a vertical column.
- E — distribute selected nodes evenly.
- Ctrl+1-9 — save a viewport bookmark.
- 1-9 — jump to a saved bookmark.
- Escape — clear selection and search highlight.


**Wildcard transitions**

- Wildcard (high-priority) transitions fire from ANY state in a machine, so they aren't anchored to a single node. They are drawn from a dedicated amber ★ ANY source node with dashed amber edges to each target state. This makes the otherwise invisible "random/high-priority" triggers (e.g. a creature's special-attack or death state) easy to find.
- Clicking the ★ ANY node opens its state machine in the Object Data panel.
- Right-click the ★ ANY node → ➕ Add Wildcard Transition to create a new one; see Creating a Wildcard Transition.


**Edge right-click menu**

- Go to event — jump straight to the triggering event's definition and its full usage list (works on normal and wildcard edges).
- Disable / Enable transition — toggles the Havok FLAG\_DISABLED flag. A disabled transition is drawn dimmed and dashed with a ⊘ marker on its label, and never fires in-game until re-enabled. Fully undoable.
- Delete Transition — removes the transition.


**Live debugging**

- Active states glow with an animated green outline and carry a ● LIVE badge.
- When a transition fires, its edge pulses green so you can trace the flow as it happens.
- The machine dropdown auto-follows the actor — entering a state that belongs to a different machine switches the graph to it — and 🎯 pan-to-active keeps the active node centred.
- See Reading a Live Session for the whole picture.


**Node right-click menu**

- 🎬 New clip generator… — on a state: creates a new hkbClipGenerator and points that state's generator at it in one step. See Adding a New Animation.
- 🔗 New behavior reference… — on a state: creates a new hkbBehaviorReferenceGenerator pointing at another behavior file and wires it as the state's generator. See Referencing Another Behavior File.
- ⧉ Duplicate state… — on a state: copies the state and everything hanging off it (generator chain, transitions, notify events) with fresh ids, and adds the copy to the same machine. See Duplicating a State.
- 🐞 Enable live-debug tracking — on a state machine (or empty canvas with a machine selected): makes that machine report its active state to the debugger. Only machines with syncVariableIndex set can be tracked; see Why Active States Are Empty.

Right-click context menus are available on nodes, edges, and empty canvas space for additional actions including Add State, Add State Machine, Add modifier, and Re-layout.



## The Object Data Panel

The right-hand panel shows every parameter of the selected object, and it knows each parameter's declared Havok type (from the bundled HKX2 class definitions).


- Booleans edit as a checkbox, enums as a fixed-choice dropdown.
- Event ids and variable indices edit as name pickers rather than numbers — a transition's eventId, a trigger or initiate interval's enterEventId/exitEventId, a machine's returnToPreviousStateEventId and friends, the id of an event property (notify events, clip triggers), variableIndex and syncVariableIndex. The list is this file's own event or variable table, shown as name (#index), with (none) for -1. An id the table doesn't cover shows as ‹unknown #N› and is left exactly as it was — nothing is silently renumbered.
- Numeric fields validate live: a value that doesn't parse as the declared type — or falls outside its range, like 200 in an int8 — gets a red border and an "expected …" tooltip. Nested params inside array elements are validated too. Saving as HKX is blocked while such values exist (the conversion would reject them anyway); saving as XML warns first.
- References (#0123, shown blue) jump with ↗ or Ctrl+Click. Editing a reference by typing re-resolves it properly — including setting it to null.
- Ref arrays (like a machine's states) edit as space-separated #ids; the numelements count is maintained automatically, and they never offer ＋ Add element. The states param also has the ✏ Edit States dialog.
- Arrays of nested elements (event property arrays, transition arrays, notify events, binding sets…) have a ＋ Add element button and a per-element ✕. New elements are created with vanilla defaults. Both are undoable, and the numelements count moves with them.
- An array that is empty when the file loads offers ＋ too. Which kind of array it is comes from Havok's class definitions rather than from the file, so an empty characterPropertyInfos, eyeBones or rigid-body properties list can be filled in here instead of by hand in XML.
- Every edit lands on the normal undo stack (Ctrl+Z).


## Variables Tab

Lists every behaviour variable (hkbBehaviorGraphData / hkbBehaviorGraphStringData).


- Type badge — coloured chip showing BOOL, INT, FLOAT, PTR, etc.
- Value editor — inline TextBox for numeric/string values; ComboBox for booleans.
- + Add Variable — creates a new variable and wires it into all three backing objects.
- − Delete Variable — removes the variable after checking for usages. You are warned if usages are found.
- Search box — filters the list in real time.

What the value actually is, because it matters here. Every variable's value is stored in one 32-bit integer slot, whatever the variable's type. A FLOAT keeps its value bit-cast into that slot — 1.0 is stored as 1065353216, -2.06 as -1073490166 — while bools, ints and pointer indices are stored as the number they look like. Nothing in the number says which it is; only the variable's declared type does, which is what the type badge shows. The editor shows you the value (1, -2.06) and converts using that type, so type the value you want and not the bit pattern.

- Only variables you actually edit here are written back on save. A value you haven't touched is left exactly as the file had it, and a value you edited through Object Data instead is not overwritten from this list.


## Events Tab

Lists every behaviour event (hkbBehaviorGraphStringData.eventNames).


- Each row shows the event index and an editable name.
- The usages panel at the bottom shows every place in the file that references the selected event, tagged ◀ listens (something reacts to it) or ▶ sends (something emits it): state and wildcard transitions, the enter/exit ids of a transition's trigger and initiate intervals, a machine's returnToPrevious / random / next-higher / next-lower state ids, event-driven modifiers, state enter/exit notify events, clip annotation triggers, and eventToSend fields. Click a usage to jump straight to it.
- 🔗 Event Xref in the toolbar runs the same cross-reference for every event at once — see Event Cross-Reference.
- Everywhere else in the editor, an event is shown by its resolved name rather than a raw numeric id. If a referenced id has no name it appears as ‹unnamed #N› so you can still trace it. Right-click an event in the graph, the Transitions list, or the SM Inspector and choose Go to event to land here on the matching row with its usages.
- + Add Event / Delete work the same as the Variables equivalents. Both keep the per-event info records (hkbBehaviorGraphData.eventInfos) paired with the names — the game matches the two arrays by position, so a mismatch breaks events in-game. Saving also reconciles the counts, which repairs files desynced by older editor versions, and 🔎 Validate flags any remaining mismatch.


## Transitions Tab

A flat list of every hkbStateMachineTransitionInfoArray entry in the file.


- Columns: From state, To state, Event, Blend duration.
- Click a row for the full detail panel: a plain-language "when it fires" sentence, the triggering event, decoded flag badges, routing (priority, and the nested state the transition lands on resolved to its name), the blend effect's duration / curve / start fraction / end mode, the condition, and the trigger and initiate intervals with their enter/exit events. Everything except the blend fields lives on the transition itself, so it shows even when the transition has no effect object — see Blending vs Snapping Transitions for what that means and how to give it one.
- Right-click a row → Go to event to jump to the triggering event's definition and usages.
- Filter box narrows the list by state or event name.


## Clips Tab

Lists every hkbClipGenerator in the file.


- Shows the clip name and the animation file it references.
- Inline editing lets you change the animation path directly or browse with the folder button.
- The trigger panel at the bottom shows all timed events attached to the selected clip.
- ▶ on a row opens the animation in the Clip Preview window, where annotations and triggers can be edited on the timeline — see The Clip Preview.
- + New Clip Generator — creates a new hkbClipGenerator from scratch. It is created unattached, so nothing references it yet; see Adding a New Animation for why that matters and how to wire it up.


## SM Inspector Tab

A full transition editor for a single hkbStateMachine.


- Select a state machine from the dropdown to load all its transitions.
- + Add Transition — opens a dialog to pick source state, target state, event, flags, and the blend the transition uses; see Blending vs Snapping Transitions. Choose ★ WILDCARD (any state) as the source to create a wildcard; see Creating a Wildcard Transition.
- + Add State — adds a new state to the selected machine without building the graph. Useful for very large machines, and the fastest route generally. The new state starts with no generator, so give it one (see Adding a New Animation) before using it.
- Edit and Delete buttons act on the selected row.
- Wildcard transitions (★ WILDCARD) are shown at the bottom of the list — these are the from-any-state, high-priority triggers also drawn from the ★ ANY node in the Graph tab.
- Right-click a row for: Go to event (jump to the event definition + usages) and Enable / Disable transition (toggles FLAG\_DISABLED, marked with ⊘; undoable).


## Bindings Tab

Lists every hkbVariableBindingSet entry found in the file.


- Each row shows the owner object, the member path being bound, and the variable it is bound to.
- Click a row to open the owner object in the Object Data panel.
- Filter box narrows by owner name, variable name, or member path.


## Project Tab

Shows file-level metadata from hkbProjectData and hkbProjectStringData.


- Open Project / Save Project / New Project toolbar buttons.
- World Up and Default Event Mode fields are editable directly.
- The Characters list shows every character file referenced by the project. Click Open to load a character file, or + Add to reference a new one.


## Character Tab

Displays and edits hkbCharacterData and hkbCharacterStringData.


- Identity — character name.
- Physics Capsule — height and radius used for collision.
- File Paths — skeleton, ragdoll, and linked behavior paths with browse buttons.
- Open → jumps straight to the linked behavior file.
- Animation Names — the list of animation files registered to this character.


## Debugger Tab

The Live Debugger shows what a running Skyrim actor's behaviour graph is actually doing — which states are active, which transition just fired, and what every behaviour variable is worth — lined up against the file you have open in the editor.


It needs the SkyrimBehaviorDebugger SKSE plugin, which ships in the same download as this editor — look for the SKSE Plugin folder next to SageHavokEditor.exe, and read the INSTALL.txt in it. The plugin is optional and the editor runs without it; the Debugger tab is the only thing that needs it. Before 0.8.0 it was not distributed at all, so if you went looking for it on Nexus once and found nothing, that is why. See Live Debugging: Setup & Connection.


The same panel lives in two places. Docked it is the 🎮 Debugger tab; ⧉ Pop Out detaches it into a small always-on-top window you can park on a second monitor while Skyrim runs full-screen. Both are bound to the same data, so nothing resets when you detach or re-dock — the tab header reads 🎮 Debugger ⧉ while it is floating, the button becomes ↩ Dock, and closing the floating window docks it again.



**What the panel shows, top to bottom**

- Header — a status dot (grey before you start, green while connected, dark red when the pipe drops and the client is retrying), the detected actor's icon and name, and the panel buttons.
- ACTIVE STATES — one card per tracked state machine: the machine name in blue above its current state's name in green. An empty list here is the usual first-run surprise and almost never a broken connection; see Why Active States Are Empty.
- TRANSITION HISTORY — a timestamped log, newest first, of every state entry as it happens, written as machine → state. It keeps the last 50 entries; a state that is merely still active is not repeated, so every line is a real entry into that state.
- VARIABLES — the actor's live variable values, with a second collapsible 🐉 group underneath for the mount whenever the actor is riding. Both group headers collapse, which is worth doing on 0\_master's ~120 variables.


**Buttons**

- ⏸ / ▶ Pause — freezes the panel without dropping the connection. Snapshots that arrive while paused are discarded rather than queued, so resuming shows the live present instead of replaying a backlog — and they are not recorded either.
- ⏺ Record — captures every snapshot to memory. The icon turns into a bright ⏹ while recording, and stopping reports the frame count in the status bar.
- 💾 Export — writes the captured session to JSON; see Recording & Exporting a Session.
- 🎯 Pan-to-active — keeps the graph viewport centred on the active state. Same toggle as the 🎯 button on the graph toolbar; the icon sits at full opacity while it is on.
- ? in the tab header opens this page.

Starting and stopping happen from the toolbar's 🎮 Live Debug button, not from this tab.



## Bookmarks Tab

Stores named references to Havok objects for quick navigation.


- Click the 🔖 bookmark icon in the Object Data header to bookmark the current object.
- Click a bookmark row to jump straight to that object and open it in Object Data.
- ✕ removes a bookmark. Bookmarks persist between sessions via AppData.


# Clip Preview

## The Clip Preview

A skeleton-aware animation player in its own window. Open it with the ▶ button on a Clips tab row, the ▶ next to an animation name on the Character tab, ▶ Preview in the Object Data panel, or by right-clicking a state in the graph → Show animation & tags.



**Playback**

- Play/pause, a scrubbable timeline, and front / side / top camera views.
- Ctrl+click a timeline tick to seek straight to it.
- The window remembers the size you resize it to.
- Export FBX writes the clip out for Blender/Max/Maya — see Export FBX.


**Timeline markers**

- Purple pentagons pointing up are annotations — timed text markers stored inside the animation file itself (the hkanno kind).
- Orange pentagons pointing down are clip triggers — timed behaviour events stored on the clip's hkbClipGenerator in the behaviour graph.
- Both are editable right on the timeline (see the next two sections), but their edits land in different places: annotation edits write to the animation file, trigger edits are behaviour edits saved with the behaviour file.

Which animations it can play Both of the ways Havok stores motion: spline-compressed, which is what the game ships, and interleaved (uncompressed), which is what many tools produce when they create or convert an animation. The difference is only how the motion is stored — compressed as curves that have to be unpacked, or written out bone by bone for every frame — and the preview reads both.


If a file will not open here at all, that stops the preview only. Editing annotations works regardless, because the markers are read straight out of the file without unpacking any motion.



## Editing Annotations

Annotations are the timed text markers inside an animation file — the same data hkanno edits from the command line. The preview edits them in place.



**Add / edit / delete / move**

- Right-click or double-click the timeline to add an annotation at that spot.
- The ＋ button next to play — or the A key — adds one at the playhead.
- Right-click or double-click a purple tick to edit or delete it.
- Drag a tick to move it — frame-snapped while dragging, hold Alt for free placement, with a live time + frame readout.


**The annotation dialog**

- Time and frame fields are linked — edit either and the other follows.
- Add flows pre-fill the nearest frame boundary; editing keeps the exact time unless you change it.
- If the animation has more than one annotation track, a track picker appears (new annotations default to track 0, the hkanno convention).


**Where the edits go**

- Edits write back to the animation file itself (XML or SE HKX). The first write makes a one-time .bak copy beside the file.
- Everything is undoable — undo rewrites the file and refreshes the preview — and the playhead stays where it was instead of resetting to zero.


## Editing Clip Triggers

Clip triggers fire a behaviour event at a set time while a clip plays (footsteps, hit frames, weapon swings). Unlike annotations they live in the behaviour graph — on the clip's hkbClipTriggerArray — so editing them is a behaviour edit, not an animation file edit.



**Add / edit / delete / move**

- Right-click the timeline → ⚡ Add trigger.
- Right-click or double-click an orange tick to edit or delete it; drag to move (same frame-snap and Alt behaviour as annotations).


**The trigger dialog**

- The event picker lists every existing event — or type a new name and the event is added to the behaviour's event list as part of the same undo step.
- Time and frame fields are linked; time is always entered as absolute clip time.
- Anchor to the clip's end stores the time as a negative offset from the end, so the trigger keeps its distance from the end if a longer animation is swapped in later.


**Safety**

- Trigger edits go through the normal undo stack and land on the next behaviour save — no animation file IO.
- A clip with no trigger array gets a new hkbClipTriggerArray created and wired in the same action, so it cannot be dropped as an orphan on .hkx save.
- If the trigger array is shared by several clip generators, the editor warns you with the list of affected clips before the edit.


## hkanno Import & Export

The preview speaks hkanno's text format, so annotations round-trip with existing tooling and can be shared as plain text.


- Right-click the timeline to copy all annotations to the clipboard, or export them to a .txt file — complete with the header hkanno update expects, so the file round-trips unchanged. Export defaults to ‹animation›.anno.txt.
- Import from .txt or paste from clipboard replaces the clip's annotations as one undoable step; undo restores the originals to their original tracks.
- Out-of-range times are clamped (you are told how many); imported annotations land on track 0.
- Copy and export also work in read-only previews.


## Export FBX

The Export FBX button writes the clip you are looking at as a binary FBX — the skeleton as a bone hierarchy, plus the animation. It is the way out of the editor for anyone who wants a Skyrim animation in Blender, Max or Maya rather than in this preview.



**What lands in the file**

- One bone per bone in the project’s animation skeleton, with the file’s own names and parenting, and the reference pose as the rest pose.
- Every frame of every bone written as a key, rather than curves fitted to the motion. The editor already evaluates Havok’s splines frame by frame, so baking keeps the export a copy of what it plays rather than a second approximation of it.
- The animation’s own frame rate, taken from its frameDuration.
- No mesh, no materials, no skin weights. This is an animation carrier: bring your own body mesh and attach it to the imported armature.


**Scale**

The box beside the button multiplies every translation, and remembers what you pick. Nothing inside a .hkx says what a Havok unit is worth, so this is a judgement the file cannot make for you. Blender reads the export as centimetres and divides by 100 on import, so the number lands as havok × scale ÷ 100 Blender units:

- 1 — Havok units unchanged. A 161-unit-tall troll imports 1.61 Blender units tall.
- 100 — one Havok unit becomes one Blender unit. The same troll is 161.12.
- 1.428 — roughly real-world metres, on the usual estimate of about 1.43 cm per unit. The troll comes out 2.30 m, which is about right for a troll.
The list is editable, so type any positive number if none of those suit. A value that is not a positive number is refused rather than quietly treated as 1.



**Things to know**

- Rotations are stored as Euler angles, because that is what FBX animates. The conversion picks, per frame, whichever of the two equivalent Euler spellings sits closest to the previous frame, which is what stops a twist bone appearing to snap a full turn between two frames.
- Bone scaling is not exported (Skyrim animations do not use it).
- It is a binary FBX, not the ASCII flavour — Blender refuses ASCII FBX outright, so the binary form is the only one worth writing.


## The Annotation & Trigger List (☰)

The ☰ button toggles a side panel listing every annotation and trigger in the clip — the fastest way to work through a long timeline. Whether it is open is remembered between sessions.



**Annotations table**

- Columns: time / frame / track / text. Click a row to seek there.
- Time and text edit inline, through the same undoable pipeline as the dialog.
- Del deletes the selected row; right-click a row for add / edit / delete.
- The Trk column hides itself when the file has a single track.


**Triggers table**

- Sits below the annotations: click to seek, edit time inline, Del to delete.

Read-only previews show the same tables without editing.



# Advanced

## Creating a New Behavior File

Load → ✨ New behavior file… creates a fresh, minimal behavior from scratch: the root container, an hkbBehaviorGraph, its graph data / string data / variable value set, and an empty root state machine wired in as the root generator. Pick a location and name; the file is written there (XML or SE HKX) and opened immediately, ready for Add State and New clip generator.


Why start here A behavior file is not an empty canvas — the root scaffolding above is mandatory, and every object must stay reachable from it to survive a save to .hkx (see the orphan-pruning note under Adding a New Animation). Opening a vanilla file and deleting everything destroys that scaffolding and leaves nothing valid to build on. The template gives you the correct skeleton with vanilla defaults (event ids -1, discard-when-inactive variable mode) for free.


Typical use This is step one of a custom-behavior mod: build your states and clips inside the new file, then patch a vanilla graph with a behavior reference pointing at it — see Referencing Another Behavior File.



## Adding a New Animation

Playing a new animation means adding a new hkbClipGenerator — the leaf node that points at an .hkx animation file — and attaching it to a state.



**The quick way (recommended)**

- Open the Graph tab, right-click the state that should play the animation, and choose 🎬 New clip generator….
- Enter a name and the animation path (e.g. Animations\\MyAttack.hkx).
- The editor creates the clip and points that state's generator at it in one step. If the state already had a generator you are asked to confirm the replacement.
- The whole action is undoable.


**Unreferenced clips are dropped on save**

This is the trap to know about. Saving to .hkx writes the object graph starting from the root and following references, so any object that nothing points at is silently discarded — no error, no warning. A clip created on its own and left unattached will simply be gone the next time you open the file.

- So: always attach the clip to a state's generator before saving as .hkx.
- + New Clip Generator on the Clips tab creates an unattached clip on purpose (useful if you intend to wire it by hand in the Object Data panel), and warns you that it is not referenced yet.
- Saving as .xml keeps unreferenced objects, so it is a safe intermediate format if you want to park work in progress.

Sensible defaults New clips are created with playbackSpeed 1.0 and animationBindingIndex -1, matching vanilla clips. A playbackSpeed of 0 never advances the animation, so it would look frozen in-game.


Don't forget the animation itself The clip only references an animation path. The .hkx animation file still has to exist under the actor's folder, and be registered in the character file's Animation Names list (Character tab) — otherwise the clip has nothing to play. If you are shipping a Nemesis/Pandora patch, the animation is registered through the patch as usual.


Registering it, from here When a character file is open and the path you typed isn't in its list, the editor offers to add it there and then — from 🎬 New clip generator, from ＋ New Clip Generator, and from Browse on an existing clip. Say yes and it goes in as one undoable step. Note the character file is a separate file: registering the animation does not save it, and the status bar says so. Nothing is offered if the animation is already registered, if the path is blank, or if no character file is open.


First person is a different project If you are working on the player, remember that the arms you see in first person come from a separate project under \_1stperson\\, with its own behavior files, event table and animations. Nothing you do in the third-person project reaches it. The editor reminds you when you export a patch or author a behavior reference, but the rule is worth knowing in advance: it works in third person, you switch view, and nothing happens — with no error anywhere to suggest the patch rather than the animation.



## Duplicating a State

Custom behavior work is usually a family of near-identical states — Aim, Throw, Recall, Catch off one clip pattern. Building the second one by hand means creating the state, its clip, its modifiers and its transition array object by object, and one missed reference leaves the copy quietly driving the original's generator.



**Making a copy**

- Graph tab → right-click the state → ⧉ Duplicate state….
- Name the copy. The dialog says how many objects it will create and re-counts as you change the two options.
- Duplicate the generator subtree — on by default. Off, the copy points at the same generator as the original, so editing that generator changes both states.
- Copy the outgoing transitions — on by default. Off, the copy starts with no outgoing transitions (it never shares the original's transition array, because editing one state's transitions would then edit the other's).
- The whole thing is one undoable action, and the copy is added to the same state machine's states list — which is what keeps it out of the orphan-pruning .hkx save.


**What is copied and what is shared**

- Copied: the state, its generator chain (clips, modifiers, blend/select nodes, nested state machines and their states), its variableBindingSet, its enter/exit notify-event arrays, and its transition array.
- Shared on purpose: hkbBlendingTransitionEffect and other transition effects. One effect normally serves the whole file, it carries no per-state data, and a copy per duplicated transition would be pure bloat.
- A state whose generator is a nested state machine copies that whole machine, which is why the object count is worth reading before you confirm.

Nothing transitions to the copy yet Transitions route by stateId, and the copy is given a fresh stateId — unique within its machine. So the copy exists, is wired into the machine and will be saved, but nothing reaches it in-game until you add an incoming transition (right-click the source state → ➕ Add Transition from this state).


Names Copies are renamed so the file has no new name collisions. Where a child's name contains the original state's name the rename carries through — duplicating Aim as Throw turns AimClip into ThrowClip — otherwise the copy gets a \_2 suffix. Havok itself doesn't care about names, but every list, picker and graph label here does.


Animations are not copied A copied clip generator keeps the original's animationName, so both states play the same animation until you point the copy at a different one (Object Data, or the Clips tab). The new animation still has to be registered in the character file — see Adding a New Animation.



## Referencing Another Behavior File

An hkbBehaviorReferenceGenerator embeds a whole other behavior graph where a state's generator would normally sit. Vanilla uses it to split the graph across files (0\_master pulls in 1hm\_behavior, magicbehavior, and so on), and it is the standard bridge for mods: patch a vanilla graph with one new state whose generator is a behavior reference pointing at your own, self-contained behavior file.



**Creating one**

- Open the Graph tab, right-click the state, and choose 🔗 New behavior reference….
- Enter a node name and the referenced file's path. The path is relative to the character project's folder — e.g. Behaviors\\MyMod.hkx for a file next to the vanilla behaviors.
- The editor creates the node and points the state's generator at it in one undoable step. If the state already had a generator you are asked to confirm the replacement.

Opening the file it points at Double-click the reference node in the Graph tab, or right-click it and choose 📂 Open <file>. The path is resolved against the character project's root folder the same way the runtime resolves it, case-insensitively, and a project mid-edit that holds the referenced graph as Havok XML rather than .hkx is found too. If nothing matches, the editor lists the folders it searched — the path is relative to somewhere the file never states, so that list is usually the answer.


How the two graphs talk The link is by name: an event (or variable) with the identical name in both files' string data is the same event at runtime. So the events that drive transitions inside the referenced file must also exist in the referencing graph's eventNames — add them on the Events tab of both files as part of the same patch.


Right-click the reference and choose 🔗 Compare events with referenced file to see both tables at once: every name either side declares, which side actually uses it, a filter for the ones that can't cross, and 📋 Copy report.


Read that list as leads rather than faults, and expect it to be long. Vanilla 0\_master references thirteen files and ten of them use events it has never heard of — MT\_Behavior alone accounts for 418 — on a graph the game runs perfectly, because a child behavior's internal events are simply its own business. This is why the comparison is something you ask for and not a warning you are given. What it is good for is the opposite mistake: expecting an event to cross when it can't, which in-game looks exactly like everything working until the animation doesn't play.



**Things that bite**

- A path that resolves to nothing is a silent T-pose in-game, not an error there. 🔎 Validate warns about it, but only about files it can see: if the referenced file lives inside a mod manager's virtual file system, the warning is about your disk rather than about the graph, and a save is never refused over it.
- The orphan-pruning rule from Adding a New Animation applies: the reference is wired to the state immediately precisely so it survives the .hkx save.
- The referenced file must be a valid SSE 64-bit behavior with its own root (hkbBehaviorGraph, string data, variable value set) — Load → ✨ New behavior file… scaffolds exactly that; see Creating a New Behavior File.


## Working With Very Large State Machines

Some machines are huge — a few hundred states with thousands of transitions. They are fully editable, but you do not have to render the graph to work on them.



**Add a state without the graph**

- SM Inspector tab → select the machine → + Add State.
- You are asked for a name; the editor picks the next free stateId within that machine and links the state into the machine's states list.
- The new state starts with no generator. Give it one — the quickest way is the Graph tab's 🎬 New clip generator… on that state (see Adding a New Animation). A state whose generator is null has nothing to play if it is ever entered.
- Add transitions to it from the same tab with + Add Transition. So a state can be created, wired, and connected entirely from the SM Inspector.


**Graph layout on large machines**

The graph uses Graphviz for layout when it is installed (C:\\Program Files\\Graphviz\\bin\\dot.exe) and falls back to a built-in layout otherwise. Both handle large cyclic machines; installing Graphviz simply gives nicer results on dense graphs.

- Use the machine selector to view one machine at a time rather than -- All Machines --.
- Fit (F) and the minimap help you find your way around once it is drawn.


## Blending vs Snapping Transitions

Whether a transition blends into the next animation or cuts to it instantly is not a field on the transition. It lives on a separate object the transition points at — its transition effect, normally an hkbBlendingTransitionEffect — and that object carries the blend duration, the blend curve, the end mode and the start fraction.


A transition whose effect is null snaps. That is legal Havok, and it is what a brand-new behaviour file has: ✨ New behavior file scaffolds no effect, so until you make one, every transition you add to that file cuts instantly — with no setting anywhere on the transition to explain why.



**The Blend row in Add / Edit Transition**

- (none — snaps, no blend) — writes null. Correct for an instant cut.
- Any transition effect already in the file, listed by name with its id and its duration. Vanilla files share one effect across hundreds of transitions, which is normal — the object holds no per-transition data.
- ＋ New blending effect… — creates a new hkbBlendingTransitionEffect with the duration you type (0.2 seconds is a reasonable default for locomotion; attacks usually want less) and points this transition at it. Everything else on it takes Havok's own defaults, which are the values vanilla uses: a smooth curve, no end mode, and self-transition set to continue-if-cyclic.

The list always contains whatever the transition currently points at, so confirming the dialog can never silently change its effect. An id the file no longer has shows as ‹unknown #N› and is preserved.



**Notes**

- The new effect and the reference to it are created in one undoable action, so it cannot be left unreferenced for the orphan-pruning .hkx save to drop.
- It is named after its duration (Blend\_200ms) so it is recognisable in the picker later; rename it in Object Data if you prefer something else.
- Editing a transition is how an already-authored snap transition gets a blend — select the row, Edit, and pick or create an effect.
- Changing an effect's duration in Object Data changes it for every transition that shares it. Create a second effect if you want one transition to differ.


## Creating a Wildcard Transition

A wildcard fires from ANY state in a machine, rather than from one specific state. Vanilla uses them for things that must be able to interrupt whatever is playing — entering a death or stagger state, a creature's special attack, and so on.


Where they actually live A normal transition is stored on its source state (hkbStateMachineStateInfo.transitions). A wildcard has no source state, so it is stored on the state machine itself, in hkbStateMachine.wildcardTransitions. That is why it is drawn from the amber ★ ANY node in the Graph tab instead of from a state.



**How to create one**

- SM Inspector tab → + Add Transition, then pick ★ WILDCARD (any state) at the top of the From State dropdown.
- Or Graph tab → right-click the amber ★ ANY node → ➕ Add Wildcard Transition, which opens the same dialog with ★ WILDCARD already selected.
- Pick the triggering event and the target state as usual, then confirm.


**What the editor does for you**

- The transition is written to the machine's wildcardTransitions array, not to a state. If the machine has no wildcard array yet, one is created and linked.
- FLAG\_IS\_LOCAL\_WILDCARD is added to the flags automatically — Havok does not treat a transition as a wildcard without it, so a wildcard missing this flag simply never fires.
- The action is undoable, and the new wildcard appears immediately as a ★ WILDCARD row in the SM Inspector and as a dashed amber edge from ★ ANY in the graph.

Editing and removing Wildcard rows behave like any other transition: right-click for Go to event, Enable / Disable transition (FLAG\_DISABLED), or Delete.



## Live Debugging: Setup & Connection

Live debugging pairs the editor with a running game. The game side is the SkyrimBehaviorDebugger SKSE plugin, which reads the behaviour state of the actor you are controlling; the editor side is a client that renders it against the graph you have open. Both have to run on the same PC — the link is a pair of local named pipes, not a network socket.



**Installing the plugin**

- It is in the SKSE Plugin folder of this editor's download, with an INSTALL.txt beside it. Either install that folder as a mod through your mod manager, or copy its SKSE folder into your Skyrim Special Edition Data folder so you end up with Data\\SKSE\\Plugins\\SkyrimBehaviorDebugger.dll.
- It needs SKSE64 and Skyrim Special Edition. The editor runs fine without it — the Debugger tab is the only thing that needs it.
- To check it loaded, look for SkyrimBehaviorDebugger.log under Documents\\My Games\\Skyrim Special Edition\\SKSE\\. A line reading SkyrimBehaviorDebugger loaded! means the game side is fine; no log file at all usually means the .dll is in the wrong folder.
- If you are sitting on 🔴 Live debugger disconnected — retrying… with Skyrim plainly running, a plugin that did not load is the first thing to check — the client retries forever by design, so that looks the same as a game you have not started.
- This was not distributed at all before 0.8.0.


**What flows over which pipe**

- SkyrimBehaviorDebugger — game → editor. One JSON snapshot per line: the actor's name and behaviour file, its active states as machine name plus numeric state id, every watched variable's value, and the same again for the mount when riding.
- SkyrimBehaviorDebugger\_Config — editor → game. Tells the plugin what to watch: the loaded file's variables with each one's type (float for REAL, VECTOR and QUATERNION variables, int for everything else), plus one entry per state machine that can report its state, giving the machine's name and the variable to read it from.


**Starting a session**

- Click 🎮 Live Debug in the toolbar. The button becomes ⏹ Stop Debug and the status bar reads ⏳ Live debugger started — launch Skyrim with SKSE.
- Order does not matter. The client retries about once a second until the game appears and re-connects by itself if the game exits or reloads, so 🔴 Live debugger disconnected — retrying… is a wait, not a failure — unless the plugin is not installed, in which case it is the only thing you will ever see.
- ⏹ Stop Debug and then 🎮 Live Debug again reconnects to the same running game. Before 0.8.0 the plugin served one session per game launch and the second connection hung; if you remember having to restart Skyrim, you no longer do.
- On connect the status bar reads 🟢 Live debugger connected and the config is re-sent automatically.
- ⏹ Stop Debug clears the panel, drops the graph highlight and discards the client — including anything you recorded, so export first.


**When the config is sent**

- On start, on every re-connect, and whenever you load a file while the debugger is running — so switching files mid-session re-points the plugin at the new graph.
- Immediately after 🐞 Enable live-debug tracking, so a machine you have just made trackable starts reporting without restarting the session.
- The status bar confirms what went out, e.g. Config: 17 vars, 2 SMs — with a warning form of the same line when no machine is trackable.

Open the file the game is running The config, the variable names and the state-id lookup are all built from the file open in the editor. If the game is running a Nemesis- or Pandora-generated output, open that output rather than your pre-patch source: the generated graph can carry different state ids and a longer variable table, and both are resolved by position. A mismatch shows up as active states named state 12 and variables that never flash.



## Reading a Live Session

Once snapshots are arriving, the graph and the panel move together. None of this needs a click — it is all driven by what the game sends.



**On the graph**

- An active state's node gets a pulsing green outline, a faint green tint across its body, and a ● LIVE badge in its bottom-right corner. The pulse redraws about 30 times a second, so a state held only briefly still registers.
- When one state is left and another entered in the same snapshot, the edge between them flashes green and fades over roughly a second — that is the transition that actually fired, which is the quickest way to tell which of several candidate edges the game took.
- Auto-follow: if a newly entered state belongs to a machine in the machine dropdown other than the one on screen, the graph switches to that machine by itself. With 🎯 pan-to-active also on, the viewport then animates to centre the active node, so the view chases the actor through the graph hands-free.


**Active-state names**

- The plugin reports a machine name and a numeric state id; the readable name is resolved from the graph you have loaded.
- A card reading state 12 with no name means that id is not in the open graph — normally the wrong file, or a copy from before the last patch run.


**The variables list**

- Variables appear as the game reports them and stay for the session. A value that moves by more than 0.001 flashes green for about 400 ms, which is how you find out which variable a key press or an animation event really drives.
- Names the editor considers relevant to the detected actor type are drawn bright and the rest dim grey. The dim ones still update, they just do not flash, so the dozen variables that matter are not lost among a hundred that don't.
- Values are shown to two decimals, and ints and bools arrive as numbers (1.00 / 0.00) because Havok keeps them all in one variable table.


**Actor detection**

- The icon and accent colour come from the snapshot's behaviour file name: dragon and horse files map to 🐉 and 🐴, and 0\_master, defaultmale, defaultfemale or mt\_behavior to 👤 player.
- Anything else is matched against the file you have open, then split into 🧍 humanoid NPC or 🐺 creature by whether the graph carries humanoid variables such as iRightHandType, iCombatStance or IsSneaking.
- Getting this wrong is cosmetic: it only changes the icon, the accent colour and which variable names count as relevant. Everything is still reported.


**Riding a mount**

- While the actor is riding, the snapshot carries a second set of states and variables for the mount, shown in the 🐉 group below the actor's variables with its own accent colour and the mount's behaviour file as the label.
- The group disappears the moment mount data stops arriving, which is itself a useful signal when debugging mounting and dismounting.


## Recording & Exporting a Session

The live panel only ever shows the present. Recording captures the snapshot stream so you can read it back frame by frame — which is how you catch a state that flickers past too fast to see, or compare what the game sets against what your graph expects.



**Capturing**

- ⏺ starts a recording and clears whatever was captured before, so every take is clean.
- Each snapshot is appended to memory as it arrives. Frames that arrive while ⏸ Pause is on are dropped rather than buffered, so pausing during a recording is a cut, not a gap you can scrub back into.
- ⏹ stops the capture and reports the count in the status bar, e.g. ⏹ 412 frames captured. The frames stay in memory until the next ⏺, so you can stop first and export at leisure.
- Recordings live in memory only, and only for the life of the debugger client: ⏹ Stop Debug throws them away. Export before you stop.


**Exporting**

- 💾 asks for a path, pre-filled as session\_yyyyMMdd\_HHmmss.json, and writes indented JSON.
- The file is an array with one object per snapshot: timestamp (HH:mm:ss.fff), actorName, behaviorFile, activeStates — each with smName, stateId and the resolved stateName — and variables as name/value pairs.
- Export writes whatever is in the buffer, so it also works mid-recording without interrupting the capture.


**What it is good for**

- Diffing the variable table the game actually drives against the one your behaviour file declares — a variable that never changes is usually one nothing writes.
- Establishing the order of state entries around a bug, with timestamps you can line up against a video capture.
- Attaching evidence to a bug report: the JSON is plain text and names the behaviour file, so it says which graph was running.


## Why Active States Are Empty

Connecting successfully and still seeing an empty Active States list is the most common live-debug question, and it is usually not a broken setup.


How active states are read A state machine does not expose its current state to the game directly. It can only mirror it into a behaviour variable — the one named by the machine's syncVariableIndex parameter. The editor therefore asks the plugin to watch only those machines whose syncVariableIndex is set (0 or higher), and the plugin reports the state by reading that variable back. A machine with syncVariableIndex = -1 has no readable state, so it can never light up.


Most state machines are not synced This is normal, and it is true of vanilla files too. In vanilla 0\_master.hkx only 11 of 112 state machines are synced (via iSyncSprintState and currentDefaultState). Vanilla WeapEquip.hkx has none at all. So a custom behaviour graph with no synced machines shows no active states — exactly like the vanilla file it replaces.



**How to tell**

- The status bar reports the config sent to the plugin, e.g. Config: 17 vars, 2 SMs.
- If it reads 0 of N state machines tracked — none have syncVariableIndex set, that is the whole diagnosis. Live variables will still update normally; only state highlighting is unavailable.


**Enable tracking for a machine**

- In the Graph tab, right-click the state machine node — or right-click empty canvas with the machine selected in the machine dropdown — and choose 🐞 Enable live-debug tracking.
- The editor adds an int variable named i‹MachineName›\_State and points that machine's syncVariableIndex at it. The machine will now write its current state ID into the variable, which is what the debugger reads.
- The change is undoable and is written back on Save. Re-run your Nemesis/Pandora patch so the edited graph reaches the game.

Nested graphs If the graph you edited is pulled in by an hkbBehaviorReferenceGenerator (a nested behaviour graph, e.g. a custom WeapEquip replacement referenced from 0\_master), the sync variable most likely also has to exist under the same name in the root graph — Havok links a nested graph's variables to the root graph by name. Add a variable with the identical name to 0\_master as part of your patch, then test in-game.



## Tracing & Editing Triggers

Behaviour files reference events by a numeric id (e.g. #495), which makes a raw state-machine trigger hard to follow. The editor resolves these for you and gives you a direct path from any trigger to where it is defined and used.



**Find what a trigger is**

- Events are shown by name everywhere — graph edge labels, the Transitions list, and the SM Inspector. An id with no name appears as ‹unnamed #N›, never as a bare number.
- Go to event — right-click a transition (in the graph, the Transitions list, or the SM Inspector) and choose Go to event. You land on the Events tab with that event selected and its full usage list shown: every transition, wildcard, clip trigger, and property that references it.


**Find a high-priority / random trigger**

- "Random" or high-priority behaviours (a creature breathing fire, entering a death state, etc.) are usually wildcard transitions that fire from any state. Open the Graph tab and look for the amber ★ ANY node — its dashed edges are exactly those triggers. You can also read them at the bottom of the SM Inspector list (★ WILDCARD).


**Add a high-priority / random trigger**

- Pick ★ WILDCARD (any state) as the From State in + Add Transition, or right-click the ★ ANY node in the graph. See Creating a Wildcard Transition.


**Turn a trigger off**

- Right-click the transition (graph edge or SM Inspector row) → Disable transition. This sets the Havok FLAG\_DISABLED flag so it never fires, without deleting it — a dimmed/⊘ marker shows it is off, and Enable transition restores it. Every toggle is undoable and is written back on save.


## Exporting Patches

Generate a Nemesis or Pandora compatible patch from your edits.


1. Make your edits to the loaded behavior file. 2. Click the 📦 Patch button in the toolbar. 3. The Patch Preview dialog shows every changed object. 4. Click Export Nemesis or Export Pandora and choose an output folder. 5. The exporter writes one #XXXX.txt per changed object with ORIGINAL/NEW markers.


The snapshot used for diffing is taken when the file is first loaded. Reloading the file resets the snapshot baseline.


How a patch says which object it means The file you apply a patch to has different object ids from the file it was written against, so a patch names its target by content instead — the object's name and its class together, as name:hkbClipGenerator:MT\_Jump. The class is not decoration: Havok names are only unique among objects of the same kind, and a state and the clip it plays almost always share one — MT\_Jump in 0\_master is both, and 659 names in MT\_Behavior are. Patches written before the class was recorded still apply, and if one of them is genuinely ambiguous the apply report says so rather than picking for you.



## Applying Patches

Apply a Nemesis/Pandora patch folder or a native .behaviorpatch file.


- Click 🔧 Apply Patch in the toolbar.
- Browse to a .behaviorpatch file or navigate into a Nemesis/Pandora mod folder.
- The preview shows every operation with checkboxes — uncheck any you want to skip.
- Click Apply to commit. The UI refreshes automatically.

Read the warnings in the result. An operation whose target could not be found is skipped and says so, and one whose target was ambiguous names every object it could have meant — that happens with older patches recording a name without its class, and it is the one case where a patch can quietly do the wrong thing.



## Global Search

Press Ctrl+G or click 🔭 Search All to open the Global Search dialog. This is the fastest way to find anything in a file — use it instead of scrolling a tab by hand. The per-tab filter boxes also hint at it (Ctrl+G: search everything).


- Searches across all objects, states, variables, events, and clips at once.
- Type a prefix to scope the search: event:  state:  clip:  var:  trans:  obj: (e.g. event:attack finds only events matching "attack"). Filter chips do the same.
- Click or press ↵ on a result to jump to it in its tab; double-click to navigate.
- Case and Regex toggles refine matching; the ± Replace panel can edit matched values.
- The search is case-insensitive by default and matches partial names.


## Event Cross-Reference

Click 🔗 Event Xref in the toolbar for a whole-file view of the event table: every event, how many places listen to it, how many send it, and what those places are. The Events tab answers the same question one event at a time; this answers it for all of them at once.


- The left list is every event in hkbBehaviorGraphStringData.eventNames, with its id and a listen/send count. The filter box narrows by name or id.
- Selecting an event lists its references on the right, each tagged ◀ listens or ▶ sends: state and wildcard transitions, the enter/exit ids inside a transition's trigger and initiate intervals, a state machine's returnToPrevious / random / next-higher / next-lower state ids, event-driven modifiers, state enter/exit notify events, clip annotation triggers, and eventToSend fields.
- Double-click a reference to select that object in Object Data and the behaviour tree.
- 📋 Copy report puts the selected event's full cross-reference on the clipboard as plain text — handy for a bug report or a patch write-up.

Unreferenced only The checkbox filters to events that nothing in this file references — the dead entries a hand-edited or tool-extended event table accumulates. Treat those as leads, not as a verdict: annotation events (HitFrame, SoundPlay.\*, the spell-fire events dragons use) are emitted from annotation tracks inside the animation .hkx files, and cross-behaviour events are matched by name in another file's table. Neither is visible from here, so an unreferenced event is never safe to delete on that basis alone.



## Compare Files

Click ⇄ Compare to open two behavior files side-by-side.


- File A is the currently loaded file.
- Browse to File B in the dialog.
- Differences are highlighted: added objects in green, removed in red, changed in amber.
- Click any diffed object to inspect it in the Object Data panel.


## Validation — the graph doctor

Click 🔎 Validate to run the graph doctor over the loaded file. The same pass runs automatically just before every save, because this domain fails silently: a wrong id or a state with nothing behind it produces no error, no crash and no log line — the character simply T-poses in-game. "It saved" and "it converted" prove nothing.


What it checks:


- Broken references — every #id in every param, including refs nested inside array elements (a transition's blend effect, the root container's variants).
- Generator slots left null — a state, a blender child or the graph's own rootGenerator with nothing behind it. That node produces no pose.
- Event ids and variable indices past the end of this file's own tables. Both are bare positional indices into eventNames / variableNames and the runtime does not bounds-check them.
- Transition destinations that no longer exist — including the two kinds the older toStateId check never looked at: a machine's wildcardTransitions, which is how most Skyrim machines are actually entered, and a transition's toNestedStateId, the state it starts the nested machine in. These are the references saving cannot protect you from. Deleting an object removes it from the file cleanly, because nothing points at it any more — but a transition names its destination by number, not by pointer, so the number survives and now means nothing. A nested destination is followed through the destination state's generator to the machine it starts; when that leads into another file through a behavior reference, it is left alone rather than guessed at.
- A clip's animationBindingIndex past the end of the character's registered animations, when a character file is open. -1 is the normal value and means the clip binds by animationName instead.
- Clips whose animationName isn't in the character's animationNames list, when a character file is open. The graph names the animation but the runtime loads it through the character — the usual outcome of adding an animation and forgetting the character file.
- Clips the animation cache disagrees with, when an animationdatasinglefile.txt is found above the file you opened. That is the third side of the same trap: the graph names the animation, the character file has to list it, and the cache stores the position in that list which the runtime actually follows. A clip can pass both of the other checks and still be sent to a different animation, which the stats bar and this report both call out. Reported are a clip with no cache record at all (Nemesis/Pandora hasn't been re-run since it was added, so it has no root motion and none of its cache triggers), a cached index past the end of the roster, an index resolving to an animation the graph doesn't name, and two clips whose names differ only in case — which is not hypothetical: vanilla has CrossBow\_IdleHeld and Crossbow\_IdleHeld pointing at different animations. All of these are warnings and never block a save, because the cache is regenerated after you edit the graph, so it is stale by design in between. The stats bar at the bottom names the cache projects being checked against; nothing there means no cache was found, which is not the same as nothing being wrong.
- States nothing can enter — not the machine's start state, and no transition's toStateId. A duplicated state that was never wired up looks exactly like this. Machines that pick a state some other way (a start-state chooser, a random or next-higher/next-lower transition event) are skipped rather than guessed at.
- Behavior references whose behaviorName isn't on disk under the project. Comparing the two graphs' event tables is a dialog you ask for rather than a check — Referencing Another Behavior File explains why.
- Objects an .hkx save would drop — everything the walk from the file root can't reach. An XML save keeps them; a .hkx save does not, and says nothing. This replaces the old "orphaned object" check, which only asked whether anything referenced an object: two dead objects referencing each other passed it and were dropped anyway.
- Values that don't match their declared Havok type (the red-bordered fields in Object Data). These also block saving as HKX.
- startStateId that doesn't match any state's stateId, duplicate stateIds in a machine, transitions whose toStateId doesn't exist, machines with no states, clips with no animation path, and eventNames/eventInfos, variableNames/variableInfos and variable name/value count mismatches — the game pairs those arrays by position.

Each issue shows the severity, the affected object, a description and — where the check has one to offer — the likely cause underneath it, in italics. Errors are listed first. Click an issue row to jump to the offending object.


Before a save The report opens by itself when there is either a structural error or an object the .hkx save would drop, with Save anyway and Cancel save instead of Close. Cancel is the default button, so pressing Enter stops and lets you look, and warnings alone never interrupt a save.


When a save is refused outright One case is not a decision: an .hkx save is refused when the graph contradicts itself in a way it did not when you opened the file. A reference to an object that isn't there, a null generator, an event id or variable index past the end of the table, a startStateId or transition target matching no state — wildcard and nested destinations included — two states in one machine sharing a stateId, paired arrays of different lengths: any of these and the file is not written, with no Save anyway to click.


A fault inside an object the save was going to drop anyway never refuses it. The refusal is about the file that gets written, and an object nothing reaches is not in that file — it is still listed in the report, as something about to be lost.


The reason it isn't a warning is that nothing downstream will object either. The conversion succeeds, the game loads the file, and then an actor T-poses or the process falls over with nothing written to any log. This is the last moment at which the reason is still knowable.


Only what your session introduced counts. Vanilla files are not clean by this standard — dragonbehavior.hkx ships two start states that don't exist and twenty transitions to missing states, nine of them at the wildcard and nested sites, plus six transitions asking for a nested state the machine hasn't got. Mostly these are states that were renumbered with the transitions into them left behind. So the editor records what was already wrong when it opened the file and refuses only over the rest. Opening a file with pre-existing defects, editing it and saving works exactly as before; those defects stay in the report as errors. Saving to XML is never refused this way either: XML is the working format, not what the game loads.



## Skyrim LE ⇄ SE Conversion

The editor reads and writes both Skyrim editions' .hkx binaries. LE (Legendary Edition / Oldrim) and SE use the same Havok schema and differ only in pointer size — 32-bit against 64-bit — so converting between them is a pure repack: the behaviour graph is preserved exactly, with no XML round-trip on disk and no external converter.


Which edition am I looking at? The status bar shows the edition the loaded file came from, next to the file name (blank for Havok XML, which has no pointer size). Save offers both editions in its file-type dropdown with the source's edition listed first, so accepting the default writes back the edition you opened.



**Converting files**

- Click 🔄 LE ⇄ SE and choose a single .hkx (multi-select works) or a whole folder, which is searched recursively.
- The prompt reports how many LE and SE files were found and asks which edition to convert to, defaulting to the opposite of what the selection mostly contains.
- Originals are never modified. Results are written to a folder beside the source, named after it — Behaviors becomes Behaviors\_LE — keeping the sub-folder structure. Beside rather than inside, so converting the same folder again doesn't walk the previous output.
- Files already in the target edition are copied across untouched rather than skipped, so the output folder is a complete, drop-in copy of the source even when the source is a mix of both editions. Only .hkx files are picked up — loose .txt, .xml or mesh files sitting in the folder are not copied.

Limits Nineteen Havok classes still have no 32-bit layout — hkp\* physics and ragdoll classes, plus a few type-metadata ones that never appear in a serialised file. None of them occur in behaviour, character, project, skeleton or animation files. If a file does contain one, writing it as LE is refused with the class names listed, rather than producing a silently corrupt file.


