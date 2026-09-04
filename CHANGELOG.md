# Changelog

All notable changes to Sage Havok Editor are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **A clean file was reporting itself in the colour of a problem.** Both badges
  at the top of the validation report are styled to turn green at zero through a
  `DataTrigger`, and neither could ever fire: nothing set the dialog's
  `DataContext`, so `{Binding ErrorCount}` resolved to nothing and the styles
  kept their setter defaults. A file with no errors has been showing a purple
  "0 Errors" since the dialog was written. One line, plus the same green-at-zero
  treatment for the warnings badge, which never had it — an amber "0 Warnings"
  next to a green "0 Errors" reads as a problem you haven't found yet. Caught
  while looking at the badges rather than the list; the harness check for it
  fails on the old code, which is the only reason to trust it.

- **A referenced behaviour file was read once and remembered forever.** The
  reference index cached every lookup for the life of the index, which is
  rebuilt only on load — so the file the comparison dialog showed you was the
  file as it stood when you first looked at it. That is precisely wrong for this
  feature: the referenced graph is usually the one being edited in the other
  window. A cached entry is now dropped when the file's write time moves, and an
  unresolved one is always retried, because authoring the reference before the
  file it names exists is the normal order of doing this. Costs one stat per
  reference per pass.

### Fixed

- **A YAML import was silently losing references, three different ways.** Found
  by chasing the one number `tools/hkx-yaml-import` reports that nothing else
  asks: how many objects the root can't reach, which is exactly what an `.hkx`
  save drops. It was **1459 of `mt_behavior`'s 4102** — 36% of the file, mostly
  state infos with nothing pointing at them. It is now **58 of 4176**.
  `0_master` goes 326 of 1657 → 151 of 1817, `dragonbehavior` 77 of 1229 → 63 of
  1275. The dragon unit now loads with **no structural errors at all**, where it
  had four.

  **Names are not unique, and the importer resolved them first-come.**
  `mt_behavior` has 656 names two files share — `AltarIdle_Enter` is both a state
  and the clip that state plays — so `AltarBehavior`'s `states` list pointed at
  three clips. This is the exact failure the roadmap records from Behavior
  Relay's own history: two same-named nodes collapsing in a name-keyed map, and
  in her case an in-game crash. What decides which one a reference means is the
  slot it sits in: `hkbStateMachine.states` is declared over
  `hkbStateMachineStateInfo` and `hkbStateMachineStateInfo.generator` over
  `hkbGenerator`. So resolution now carries the owning class down and prefers a
  candidate of the declared class, falling back to first-registered — the old
  behaviour — only where the class can't decide. New
  `HavokTypeCatalog.IsKindOf`.

  **A list that ended at a top-level key lost its last item.** The parser only
  closed an open list when it saw a line indented under one, so a list followed
  by another top-level key left its final item pending and then threw it away —
  and a one-item list vanished entirely. That is what every state machine's
  wildcard transitions are: 116 machines in `mt_behavior`, 11 in `0_master`, 17
  in `dragonbehavior`, all of them silently dropped.

  **A name containing a space couldn't survive the name list.** Multi-reference
  fields were joined into one space-separated string and split apart again, but
  141 of `mt_behavior`'s object names contain a space (`Paired
  OffsetBoundStandingCut`), so **110 of its 1234 list entries** came back as two
  tokens pointing at two wrong objects, or none. The names are kept as a list
  until they're resolved; ids never contain a space, so the joined form is safe
  once the names are gone.

  Two checks pin it: every resolved reference must be of the class its slot
  declares (4063 checked in `mt_behavior`, 0 wrong), and every state machine
  that declares wildcard transitions must still have them.

### Added

- **The name-keyed index fields resolve on a YAML import.** The source writes
  the readable half of a pair — `syncVariable: iSyncSprintState` where Havok's
  member is `syncVariableIndex`, `startPlayingEvent: GetUpStart` where it is
  `startPlayingEventId` — and its own writer drops the suffix exactly when it
  has a name to put in place of the number, so the same files carry
  `startMatchingEventId: -1` where there is no event to name. The importer kept
  the readable form, which is a member Havok doesn't have, holding a string
  where an int belongs. 24 sites in vanilla `0_master`, 15 in `dragonbehavior`,
  13 in `mt_behavior` — an `hkbEventDrivenModifier` that never activates, an
  `hkbPoseMatchingGenerator` that never starts matching.

  Rather than a list of the five fields this affects today, the rule is asked of
  the class: a param the class doesn't declare, whose name plus `Id` or `Index`
  *is* a member `HavokTypeCatalog` has marked as an index into the event or
  variable table, is that member written the readable way. Those marks already
  existed for the property editor's name pickers, so nothing new had to be
  decided — and a sixth field appearing in a future source tree needs no code.
  A name with no match resolves to `-1`, Havok's own "no event" sentinel and
  what the source itself writes in that case, rather than the param being
  dropped.

  `HavokTypeCatalog.Lookup(className, paramName)` is new and public for this:
  the importer asks about params that aren't there yet, which is a question
  about the class rather than the instance.


- **Clip triggers survive a YAML import.** A clip's `triggers:` is the list
  itself in the source, where Havok's `hkbClipGenerator.triggers` is a *pointer*
  to an `hkbClipTriggerArray` that holds it — so the import built the right
  nested shape in the wrong slot. It saved as XML and then HKX2's deserializer
  read the element's run-together text as a reference symbol and reported a
  missing `'-0.900000trueJumpFallnull'`. **202 of vanilla `0_master`'s 289 clips
  and 163 of `dragonbehavior`'s 214**, which between them is every clip that
  drives anything off an animation's timeline.

  Two things had to be built as well as moved. An event is a name in the source
  and a positional index at runtime — 554 of them in `0_master`, all now
  resolved against the file's own `eventNames`, the same treatment transitions
  already got. And a payload (`event: HitFrame`, `payload: Left` — the hand a
  hit came from) is a pointer to an `hkbStringEventPayload`, so it has to become
  an object of its own: 24 of them in `0_master`, 2 in `dragonbehavior`. Left as
  text on the trigger, a payload isn't merely ignored — `hkbClipTrigger` has no
  such member, and the object it should have become would be dropped by the
  orphan-pruning save.

  The three booleans (`relativeToEndOfClip`, `acyclic`, `isAnnotation`) are
  written explicitly even though the source omits them when false, so an
  imported trigger is the same shape as one that came out of a real file.

  Measured by `tools/hkx-yaml-import`, which also shows what this doesn't fix:
  the conversion's stopping point moves from `m_triggers` to `m_children` on
  both units, which is the next item.


- **A YAML behaviour folder now imports with the root scaffold an `.hkx` is read
  through.** Nothing in a Behavior Relay source tree describes it, because it
  isn't behaviour: an `.hkx` is one `hkRootLevelContainer` whose `namedVariants`
  name the graph, and every reader starts there. Without it a saved XML declared
  `toplevelobject="#0050"` — an id that happens to exist, being whichever object
  the importer numbered fiftieth — and HKX2's deserializer died on the header
  before reaching any content. Saving to XML worked; saving to `.hkx` was broken
  for every YAML folder, independently of anything else in the import.

  The import now builds the container, points its variant at the
  `hkbBehaviorGraph`, and carries `behavior.yaml`'s `packfile:` header
  (`classversion`, `contentsversion`) through instead of assuming Skyrim SE's.
  It also goes through `HavokManager.BuildGraph` rather than filling `ObjectMap`
  directly — which is what resolves single `#refs` into the `Children` cache the
  way an XML load does, and attaches the declared-type metadata behind the
  property editor's numeric boxes, enum dropdowns and event pickers. An imported
  file had none of that before, so every field in Object Data was a bare
  TextBox.

  New `tools/hkx-yaml-import` measures a real unit end to end. On vanilla
  `0_master` (1177 source files → 1431 objects) the header is now right and the
  conversion gets past it — and then stops on the next thing, which is the point
  of running it: the harness prints the deserializer's first complaint per unit,
  so each remaining fix moves a line that is on the record rather than
  rediscovered. **Three different stopping points across three units**, which
  corrects this roadmap's "nothing else about the import is structurally wrong":
  `m_triggers` on `0_master`, `m_condition` on `mt_behavior` (a
  `condition: isInFurniture == 0` string where an `hkbExpressionCondition`
  object belongs), and `m_children` on `dragonbehavior` (a blender's inline
  child structs in a slot that wants `#id`s).

  Also settled: the "mashed scalar" this roadmap describes for dropped triggers
  (`-0.00899999961truefalsefalseclipEndnull`) is not what the importer builds —
  it builds the right nested shape, in the wrong slot. The mash is HKX2's
  deserializer reading an inline array's run-together element text as a
  reference symbol, which is what it does to any inline array sitting where a
  pointer belongs.


- **＋ Add element now appears on arrays that are empty in the file.** The
  affordance used to need an inline element already present to notice, so an
  array Havok shipped at `numelements="0"` — `hkbBehaviorGraphData`'s
  `characterPropertyInfos` and word min/max value sets, `BSLookAtModifier`'s
  `eyeBones`, `hkbKeyframeBonesModifier`'s `keyframeInfo`, every
  `hkpRigidBody.properties` in a skeleton — could only be filled in by hand-editing
  XML. The missing fact was whether the array holds inline structs or `#id`
  references, which is invisible twice over: reflection sees `IList<T>` for both,
  and an empty array of either is the same bytes on disk.

  It is read out of HKX2's own XML writer. `WriteXml` calls `WriteClassArray` or
  `WriteClassPointerArray` per member, each preceded by a `nameof(m_member)`
  literal, so `HavokArrayKinds` walks the method's IL and keys the answer by that
  literal — the same authority the editor round-trips through, rather than a rule
  inferred about it. Ref arrays are now explicitly refused the button instead of
  merely never qualifying for it.

  The obvious inference was measured and rejected: "pointer arrays hold
  `hkReferencedObject` descendants" is right for 137 of HKX2's 139 serialized
  array members and wrong for `hkpSerializedTrack1nInfo`'s two, which point at
  plain `IHavokObject` elements. Physics classes that never occur in a behaviour
  file — but this codebase has been bitten enough by nearly-right that a rule with
  known exceptions isn't worth carrying when the exact answer costs an IL walk.
  `tools/hkx-array-kinds` cross-checks every member against the class metadata
  Havok's exporter left in the autogen comments (a transcription nothing compiles,
  so a genuine second opinion): zero disagreements, and every inline-struct
  element class can produce a default element, so the button can't appear and then
  dead-end on "no template". `tools/hkx-inline-array-ui` presses it in the real
  property editor on `BSLookAtModifier.eyeBones` — a param with nothing to clone —
  and undoes it again.

  Two things the checks corrected. Three array members are `SERIALIZE_IGNORED`
  (`hkbFootIkModifier.internalLegData` and two `hkp*` ones): they never reach the
  XML, so the IL read finds nothing for them, and expecting otherwise is what
  turned the first run red. And the button's whole point on an empty array is the
  HKX2 default element — the clone-a-sibling fallback has no sibling to clone.


- **A clip's animation can be registered in the character file from the clip
  flow.** The graph names an animation by path; the runtime loads it through the
  character's `animationNames`. Do only the first half and the clip plays
  nothing, in-game, with no error — the graph doctor already reported it, and
  this is the button that fixes it. When a clip's `animationName` isn't in the
  loaded character's list the editor offers to add it, at all three moments a
  path gets chosen: the graph's 🎬 New clip generator, the Clips tab's
  ＋ New Clip Generator, and the Clips tab's Browse. One undoable action, and the
  status line says the character file still needs saving — it is a second file,
  and the editor doesn't write it for you.

  Silent when there is nothing to offer: no character file open, a blank path,
  or a path already registered (compared with `/` and `\` and case treated
  alike, which is how these paths are actually written). The graph raises the
  event and the window answers it, because which character file is open is not
  something a graph view knows.

- **A reminder that first person is a separate project.** The player is two
  behaviour projects: third person under `meshes\actors\character\`, and the arms
  you see holding a sword in a wholly separate one under
  `meshes\actors\character\_1stperson\` — its own `0_master`, its own event
  table, its own animations. Nothing links them, so a patch against the
  third-person graph does not reach first person, and the failure is the quiet
  kind: it plays perfectly in third person, the player switches view, nothing
  happens, and there is no reason to suspect the patch rather than the animation.

  `FirstPersonProject` says so at the two moments the omission becomes real: on
  patch export, appended to the success message, because that is when the patch
  is finished and shippable; and once per loaded file when a behaviour reference
  is authored. Once, because a reminder that fires every time is a dialog people
  dismiss without reading, and this one is only worth anything read.

  It decides from the path — under `\actors\character\` and not under
  `\_1stperson\` — and deliberately **not** by looking for the sibling folder on
  disk. The first-person project normally lives inside a BSA or behind a mod
  manager's virtual file system, so "the folder isn't there" says nothing about
  whether the game has one, and it always does. The cases are pinned in
  `tools/hkx-graph-doctor`: the player's behaviour and character files remind,
  a `_1stperson` path doesn't (it is already there), a dragon doesn't (no
  first-person view), `characterassets` doesn't despite the prefix, and forward
  slashes are the same path as backslashes.

- **A behaviour reference can be followed, and the graph doctor reads what's on
  the other side.** `hkbBehaviorReferenceGenerator` is the one node whose subject
  is a different file — the bridge of every Nemesis/Pandora-style patch — and
  until now its `behaviorName` was a string you had to go and find yourself.

  **Following it.** Double-click the node in the Graph tab, or right-click →
  📂 Open ‹file›. A new `BehaviorReferenceIndex` resolves the path the way the
  runtime does: against the character project's root (the parent of the folder
  holding the character file, the same anchor `behaviorFilename` uses), falling
  back to the folders of whatever else is open, matched case-insensitively
  segment by segment. A `.hkx` path also resolves to a `.xml` beside it, which is
  what a project mid-edit actually looks like. When nothing matches, the message
  lists the folders that were searched — the path is relative to somewhere the
  file never states, so "not found" alone tells you nothing you can act on.

  **One new doctor check.** A `behaviorName` that resolves to nothing (or is
  empty) is reported: at runtime the state is entered and nothing plays, with no
  error anywhere. It is deliberately not structural, so it can never refuse a
  save — whether a file is on *this* disk says nothing about whether the graph
  contradicts itself, and under a mod manager's virtual file system the
  referenced file legitimately isn't there. Validated where it counts: all 13
  references in vanilla SSE `0_master.hkx` resolve, `Behaviors\1HM_Behavior.hkx`
  to `behaviors/1hm_behavior.hkx` and so on, which is the case-insensitive walk
  working on paths somebody else wrote.

  **Event alignment across the reference is a dialog, not a warning — and the
  measurement is why.** The roadmap asked for it as a check: the two graphs link
  by event *name*, each keeping its own table, so an event the referenced graph
  uses that this file has never heard of cannot cross between them. Built as a
  warning, that fires on **10 of vanilla `0_master`'s 13 references** — at 1, 1,
  1, 2, 3, 3, 3, 32, 135 and 418 events — on a file the game runs perfectly. The
  premise is simply wrong: a child behaviour's internal events are its own
  business, so "uses an event the parent hasn't got" is the normal condition
  rather than a defect, and a warning that fires on stock Skyrim content is one
  nobody reads twice.

  The numbers are still worth having when you go looking for them, so they moved
  to **🔗 Compare events with referenced file** on the reference node: both event
  tables side by side, each name marked declared / declared-and-used on either
  side, a filter for the ones that can't cross, and 📋 Copy report. Same framing
  the Guide already uses for unreferenced events — leads, not faults. "Used"
  means the same thing on both sides because both come from the same scan, and
  that scan needed no list of param names: `HavokTypeCatalog`'s
  `HkParamSemantic` already marks every int that is an event index, nested sites
  included.

  The index reads `.hkx` through HKX2 in memory rather than the async conversion
  service — a reference lookup is not a user action, and the deserialize is
  in-memory either way, so there is no temp file to write. Results are cached,
  and dropped when the file's write time moves (see Fixed). `FindFileCaseInsensitive`
  moved out of `HavokWorkspace` into a shared `HkxPathResolver`, since both now
  chase the same kind of path by the same rules and two copies would drift.

  Neither sample file has a behaviour reference, so `tools/hkx-graph-doctor`
  builds one: a node pointing at the file itself, which covers the empty path,
  the missing path and the `.hkx`→`.xml` fallback. It also gained `--project`,
  which anchors the index at a real character folder — that is how the 0_master
  numbers above were obtained, and how "all 13 resolve" is checked against
  content nobody wrote for a test. `tools/hkx-graph-doctor-ui` covers the part
  only the app can get wrong: that the load builds an index, that the window is
  actually listening for the open request, that drilling into a reference asks
  for the file it names — and asks for nothing when there is no path — and that
  the comparison dialog lists both tables and filters to what can't cross. Two
  things bit while writing it: the harness has to unhook the window's own open
  handler first, or it resolves the made-up path, fails, and opens a modal
  MessageBox nothing in the process can dismiss; and on a 2,400-node graph the
  main window's rendering keeps the dispatcher busy enough that a second window
  shown modally never gets laid out, so the dialog check shows it modelessly and
  forces the layout pass itself.

  Two harness bugs fell out of running against real content, both worth naming.
  The independent re-derivation of unreachable states scanned for a stateId
  targeted *anywhere* in the file; that holds on a one-machine file and cries
  wolf on a real one, since 0_master has 9 genuinely unreachable states and every
  one of their ids is targeted in some other machine — it is scoped to the owning
  machine now, which is where stateIds mean anything. And the null-generator
  fault assumed the generator it nulls is exclusively that state's; in the
  vanilla character behaviours generators are shared, so it now picks a state
  whose generator has exactly one inbound reference.

- **An `.hkx` save is refused when the graph contradicts itself.** The evidence
  for the policy is in ROADMAP → Behavior Relay: a case-insensitive `.hky`
  filename collision let MO2's VFS merge two mods into a structurally
  inconsistent graph, the compiler accepted it and emitted a `0_master` 107 KB
  short, and the game hard-faulted with no crash log and no log line — while the
  same input, offline, produced merely wrong-but-complete bytes that every
  offline gate passed. The lesson is that a graph like this is *accepted* at
  every stage that could complain, so the write is the last place the reason can
  still be said.

  `ValidationIssue.IsStructural` marks the findings that mean the graph
  contradicts itself — a `#ref` to an id the file doesn't hold, a null generator,
  an event id or variable index past its table, a `startStateId` or `toStateId`
  matching no state, a duplicate `stateId` within one machine, two
  position-paired arrays disagreeing on length, a `toplevelobject` that isn't
  there. That is Behavior Relay's four checks (dangling node references, root
  generator resolves, `eventInfos`/`eventNames` and the three variable arrays
  agree, `stateId` unique per machine) plus the two the graph doctor added; all
  of them were already implemented, so what is new here is the policy. An `.hkx`
  save carrying one of these is not written, and the report opens with no way to
  overrule it, each row now carrying the likely cause under the description —
  "the destination state was deleted, or its stateId was renumbered" rather than
  only the fact of it. The status bar keeps the one-line version: which graph,
  what failed, likely cause.

  **The refusal is relative to what the file arrived with**, which is the one
  place this diverges from Cassie's design and can't not. Her compiler owns its
  output; an editor opens files it didn't write, and vanilla `dragonbehavior`
  ships 12 structural errors — a duplicate `stateId`, two impossible
  `startStateId`s and nine transitions to states that don't exist — so refusing
  on all of them would make Bethesda's own files unsaveable. Each load takes a
  fingerprint of the structural errors already present (`category|objectId|
  subject`), and only findings outside that set refuse a save. Everything else is
  unchanged: the type-error gate still refuses an HKX save outright, and every
  other finding still opens the advisory Save anyway / Cancel report.

  The fingerprint excludes the description on purpose, and the reason is a trap
  worth naming: an inherited `toStateId` error lists its machine's valid
  stateIds, so adding an unrelated state to that machine rewords an error the
  user did not cause. A description-sensitive fingerprint would then refuse the
  next `.hkx` save over Bethesda's bug. `tools/hkx-graph-doctor` pins exactly
  that — it appends a state to a machine that already has a dangling
  `toStateId`, asserts the wording changed, asserts the fingerprint didn't, and
  asserts nothing became refusable. Erring coarse is the deliberate trade: a
  fingerprint that drifts costs a blocked save, while one too coarse costs a
  second fault on an already-faulty object going to the advisory report, where
  it is still shown and still clickable.

  Both harnesses grew with it. `tools/hkx-graph-doctor` now checks that no
  finding is left uncategorised, that every structural error can name a likely
  cause, that warnings and type errors never enter the structural set, and — for
  each injected fault — whether it counts as newly broken against a load-time
  baseline: the null generator, dangling ref, out-of-range indices and missing
  root do; the unwired state, the dropped objects and the unregistered animation
  deliberately don't, because each is a normal intermediate state of an edit
  rather than a contradiction. `tools/hkx-graph-doctor-ui` checks that loading a
  file actually takes a baseline (a load that forgot to would silently disarm the
  whole thing), that it matches the file's own structural errors so an untouched
  file refuses nothing, that nulling one generator produces exactly one refusable
  finding, and that the refusal dialog offers no way to save anyway.

  Nothing of Cassie's was read or copied — her repo is provisionally
  all-rights-reserved and this editor is GPL-3.0. The four checks and the
  refuse-don't-emit policy as described in our own ROADMAP entry are the whole
  input.

- **A pre-save "graph doctor" pass, and 🔎 Validate now runs it.** This domain's
  failure mode is silence: a wrong id or a state with nothing behind it produces
  no error, no crash and no log line, and the character T-poses in-game — so
  "it saved" and "it converted" both prove nothing. `GraphDoctor` runs
  `HavokValidator`'s file-integrity checks plus five new ones, over the loaded
  graph and (when a character file is open) its animation list:

  - **A generator slot holding `null`** — `hkbStateMachineStateInfo.generator`,
    a blender child's, or `hkbBehaviorGraph.rootGenerator`. That node produces
    no pose.
  - **An event id or variable index past the end of the file's own table.** Both
    are bare positional indices into `eventNames` / `variableNames` and the
    runtime doesn't bounds-check them. The check is generic rather than a list
    of param names: `HavokTypeCatalog` already marks which ints are which — the
    same annotation that drives the property editor's name pickers — so it
    covers transition `eventId`s, the intervals nested inside them, notify
    events, clip triggers and modifier bindings alike.
  - **A clip naming an animation the character project never registered.** The
    graph names animations by path but the runtime loads them through the
    character's `animationNames`, so an unregistered path is a clip that plays
    nothing.
  - **A state nothing can enter** — not its machine's start state, not any
    transition's `toStateId`. A duplicated state that was never wired up looks
    exactly like this. Three blind spots keep it from crying wolf: a machine
    with a `startStateChooser`, or with `randomTransitionEventId` /
    `transitionToNext{Higher,Lower}StateEventId` set (those enter a state
    positionally, so any state is fair game), is skipped whole, and a
    `toNestedStateId` anywhere in the file counts as reaching that stateId in
    any machine rather than only the nested one it names.
  - **The objects an `.hkx` save would drop**, named and counted rather than
    silently pruned. This replaces the old "orphaned object" warning, which
    asked only whether anything referenced an object — two dead objects
    referencing each other passed that test and were dropped anyway. Reachability
    from `toplevelobject` is what actually decides the save, and a
    `toplevelobject` that isn't in the file is now its own error rather than a
    report claiming every object will be pruned.

  Save runs one doctor pass and uses it twice. The type-error gate is unchanged
  (an HKX save is still refused outright, an XML save still offers save-anyway);
  everything else opens the report with **Save anyway** / **Cancel save** when
  there is a structural error or real loss, and never for warnings alone. Nothing
  refuses the save — a graph mid-edit legitimately has states nothing reaches yet
  — but Cancel is the default button, so Enter stops and lets you look, and
  closing the window is a cancel too. The report itself is the existing
  validation dialog: errors sorted first, a headline naming what was found, rows
  that wrap instead of scrolling off to the right, and click-to-navigate wiring
  now shared between both entry points.

  Two harnesses, because the pass has two halves. `tools/hkx-graph-doctor`
  compiles the editor's own validation code and re-introduces each bug on
  purpose — null generator, dangling ref, out-of-range event id and variable
  index, an unwired state appended to a machine, two dead objects referencing
  each other, an unregistered animation, a `toplevelobject` that isn't there —
  checking each is reported on the right object and that removing it restores the
  baseline issue set *exactly*. The findings are also re-derived independently:
  the prune list against a regex walk of the raw XML (which caught a real trap —
  `XElement.Value` glues adjacent text nodes, so `variableBindingSet` `#0053`
  followed by `userData` `0` reads as `#00530`), and every unreachable state
  against a brute-force scan for any transition in the file targeting its
  stateId. `tools/hkx-graph-doctor-ui` drives the real `MainWindow` and the real
  dialog: ✓ Validate's read-out lists what the pass found, clicking a row still
  opens the object, and the gate's two buttons return the DialogResult the save
  path branches on.

  On the dragonbehavior sample (1510 objects) every new check is silent except
  the two that aren't meant to be: 3 objects the `.hkx` save drops, and 15 states
  nothing enters — 7 of which sit in machines the existing `toStateId` /
  `startStateId` checks already flag, the file having a state numbered 32 where
  three transitions target 3. The same checks come back clean on a 1518-object
  modded dragon behaviour but for one state, and silent on character, project,
  skeleton and animation files, which have no graph to walk.

- **Create a blending transition effect from the Add/Edit Transition dialog.** A
  transition's `transition` param points at the `hkbTransitionEffect` that
  decides how it blends, and the dialog never offered any way to choose one:
  `DefaultTransitionEffectRef()` grabbed the first effect in the file and fell
  back to `null` when there wasn't one — which is exactly the state of a file
  scaffolded by ✨ New behavior file, so every transition authored in a fresh
  custom behaviour snapped with zero blend and nothing said so. The dialog now
  has a **Blend** row: `(none — snaps, no blend)`, every transition effect
  already in the file (named, with its id and duration), and **＋ New blending
  effect…** with an editable duration defaulting to 0.2s. Choosing it builds an
  `hkbBlendingTransitionEffect` from HKX2's own default instance — which already
  carries Havok's defaults for everything else (`BLEND_CURVE_SMOOTH`,
  `END_MODE_NONE`, `SELF_TRANSITION_MODE_CONTINUE_IF_CYCLIC_BLEND_IF_ACYCLIC`),
  verified param-for-param against a vanilla effect — names it after its duration
  (`Blend_250ms`), and registers it together with the transition that points at
  it in one undoable action. The picker also works on Edit, which is how an
  already-authored snap transition gets a blend; it always contains the
  transition's current value, rendering an id the file no longer has as
  `‹unknown #N›` rather than quietly rewriting it. A duration that isn't a
  number ≥ 0 is refused before it reaches the file (a comma is read as a decimal
  point, and the value is written back Havok-formatted).

  The dialog only exists inside the WPF app, so the harness for this one drives
  the real `MainWindow`: `tools/hkx-transition-blend` loads a behaviour file,
  opens Add Transition on an actual state machine, picks the new-blend entry,
  confirms, and checks what landed — one new effect with the typed duration and
  Havok's defaults intact, a transition pointing at it on the right state, no id
  shared between the new objects — then undoes it, and repeats for the Edit path.
  Run on vanilla `dragonbehavior` (1510 objects); re-introducing the id bug below
  turns 7 of its checks red.

  One trap surfaced while testing it end-to-end: `GenerateNewObjectId()` scans
  `ObjectMap`, so an object holding an id it hasn't been registered under yet
  hands that same id to the next caller. The new effect and the transition array
  created a few lines later both came out as `#0001`, and registering the effect
  overwrote the array — the state's `transitions` then pointed at a transition
  effect. The effect is registered the moment it's created, and the one bail-out
  after that point takes it back out.

### Fixed

- **Every ComboBox bound to `IdNamePair` showed a class name until you opened
  it.** The dark theme's ComboBox template renders its closed selection box
  through the item's `ToString()`, and `IdNamePair` didn't override it — so the
  Add/Edit Transition dialog's From State, Event and To State pickers all read
  `SageHavokEditor.Models.ViewModels.IdNamePair` while closed, and only the
  drop-down list showed real names. The property editor's event/variable pickers
  were unaffected because `PickerEntry` already overrides `ToString` for exactly
  this reason; `IdNamePair` now does the same. Found while adding the Blend
  picker to that dialog, which inherited the same blank.

- **Duplicate a state with its generator subtree.** Graph tab → right-click a
  state → ⧉ Duplicate state…. Building a family of near-identical states (Aim /
  Throw / Recall / Catch off one clip pattern) was an object-at-a-time job in the
  property editor, with the domain's usual silent failure mode waiting at the
  end: miss one `#ref` and the copy drives the *original's* generator, which
  looks fine until both states animate as one in-game. The copy walks every ref
  the state carries — generator chain, `variableBindingSet`, enter/exit notify
  arrays, the transition array, nested state machines and their states — hands
  each copy a fresh id, and rewrites the copies to point at each other. Two
  boundaries: transition effects are shared rather than copied (one
  `hkbBlendingTransitionEffect` normally serves a whole file and carries no
  per-state data), and file-level singletons are never copied even if a
  hand-edited file points the walk at one. Two options in the dialog, each
  re-counting the objects it would create: share the generator instead of copying
  it, and skip the transitions — skipping means *none*, never the original's
  array, since sharing it would make editing one state's transitions edit the
  other's. The copy gets a fresh `stateId` (unique within its machine, since
  stateIds restart per machine) and is appended to that machine's `states` list
  in the same undoable action — an unwired state is dropped by the orphan-pruning
  `.hkx` save. Names are uniquified, carrying the rename through the subtree
  where the child's name contains the state's own: duplicating `Aim` as `Throw`
  turns `AimClip` into `ThrowClip`.

  The rewiring is the part that had to be proved rather than eyeballed, so it has
  a harness: `tools/hkx-duplicate-state` compiles the editor's own model and
  duplicator and checks, on a real behaviour file, that every copy has a fresh
  id, that no copy references an object that was itself copied, that every ref
  inside the copies resolves, that the transition effects stayed shared, that
  nothing outside the machine's `states` list changed, and that the whole result
  survives a save/reload unchanged. Run on vanilla `dragonbehavior` (1510
  objects; duplicating `ST_Flight` copies 244 of them, its generator being a
  nested machine) and on a 209-state behaviour: all pass, both for the deep copy
  and for the share-generator/no-transitions combination, including the
  one-state-machine case where the `states` ref is cached in `Children` and
  appending to the text alone wouldn't stick.

## [0.6.0] — 2026-08-27

### Fixed

- **Saves wrote a packfile header of empty attributes, and one hard-coded root.**
  Both save paths rebuild the `HkPackfile` from the manager's `ObjectMap` rather
  than keeping the one that was loaded, and neither carried the header across —
  so every saved file said `classversion="" contentsversion=""`.
  `toplevelobject` was worse: empty from `HavokWorkspace.SerializeManager`
  (character and project saves), and hard-coded `"#0050"` from
  `MainWindow.SerializeToFile`. The root object is `#0050` only by convention,
  and `toplevelobject` is how the runtime finds the graph — pointing it at an id
  that isn't the root is the silent kind of wrong this domain specialises in.
  `HavokManager` now keeps `classversion`/`contentsversion`/`toplevelobject` at
  `BuildGraph` and hands them back through a new `NewPackfile()`, which falls
  back to Skyrim's schema (`8` / `hk_2010.2.0-r1`) and to the real root id only
  when a packfile is built from scratch. Found by diffing a save against the
  same file converted by HKX2, after the `numelements` fix made the diff small
  enough to read.

- **Inline objects and the root element carried attributes Havok never writes.**
  The same defaulted-empty-string leak as `numelements`, in three more places.
  `HkObject` had no `ShouldSerialize` guards, so every inline (anonymous)
  element got `name="" class="" signature=""` — 2,798 of them in vanilla
  `dragonbehavior.hkx`, where Havok writes a bare `<hkobject>`. The root element
  picked up `xmlns:xsi` and `xmlns:xsd` from the default XmlSerializer; all
  writes now go through `HkXml.Write`, which passes the one-empty-entry
  `XmlSerializerNamespaces` that suppresses them. And empty array params
  self-closed as `<hkparam … />` where Havok writes `<hkparam …></hkparam>`;
  `ShouldSerializeValue` now emits the text node even when it's empty, which
  keeps the full end tag. That last one follows the converter in 500+ places
  across the sample and differs from it in exactly one (a `quadVariableValues`
  that HKX2 alone self-closes).

  Measured across 9 vanilla files — behaviour, character, project and skeleton,
  71,930 lines of XML — a save now differs from the same file converted by HKX2
  in **65 lines total**: 64 are empty `<hkobject/>` in the two skeletons, and 1
  is that `quadVariableValues`. Four of the six distinct files are exact. Every
  file still round-trips XML → `.hkx` → XML byte-identical to the original
  conversion, so none of this changed what the files mean. The converter's
  14,125 `SERIALIZE_IGNORED` comments are still dropped on load and are excluded
  from those counts — see ROADMAP for why they're separate.

- **XML saves wrote `numelements=""` on every scalar param.** `HkParam.NumElements`
  defaults to `""` and carried no `ShouldSerialize` guard, so the XmlSerializer
  emitted the attribute on every param whether or not it was an array — 18,884
  spurious attributes in a single 1512-object file (vanilla `dragonbehavior.hkx`).
  Harmless to the game, since XML→HKX conversion ignores an empty count, but it
  meant app-saved XML differed from converter output on virtually every line, so
  hand-diffing a save against hkxconv/temp XML — the main way the id and layout
  work gets checked — was buried in noise. `ShouldSerializeNumElements()` now
  suppresses the attribute when blank, which is already what blank means
  everywhere else in the codebase (`HavokTypeCatalog.Annotate` and
  `ResyncNumElements` both read it as "not an array"); a genuinely empty array
  keeps its explicit `"0"`. Verified by round-tripping `dragonbehavior.hkx`
  through the real save path: the attribute set now matches HKX2's own
  serializer exactly — 958 `numelements` attributes on both sides, all 232
  `"0"` counts preserved, zero empty ones — and normalised diff noise against
  converter output dropped from 61,917 differing lines to 20,189 (the remainder
  is the root element's `xmlns:xsi`/`xsd` and long-array line wrapping, both
  separate). The count being authoritative on XML→HKX was the risk worth
  disproving, so the whole loop was run through the real save method: .hkx →
  XML → save → .hkx → XML comes back byte-identical to the original
  conversion, same 369,616-byte binary, nothing truncated. Found 2026-07-29
  while verifying trigger editing end-to-end.

- **The transition detail panel showed almost none of the transition.**
  Everything below the flag badges was read off the `hkbBlendingTransitionEffect`
  the transition points at — but the effect carries only `duration`,
  `blendCurve`, `endMode` and `toGeneratorStartTimeFraction`. Priority, the
  nested-state routing, the condition and both intervals live on the
  `hkbStateMachineTransitionInfo` itself, so those rows silently never
  appeared, and the plain-language "when it fires" sentence dropped its "and
  its condition is true" half. The panel also returned early whenever a
  transition had no effect at all (`null`, normal for a snap transition),
  leaving nothing but the event row. Every row now comes off the transition
  struct: `toNestedStateId` resolved through the destination state's nested
  machine into a clickable state name instead of a bare number — that field is
  how two transitions with the same destination land on different sub-branches
  — plus priority, the condition with variable indices resolved to names, and
  the trigger and initiate intervals with their enter/exit events and times.

- **Validator: no more false "orphaned object" on `hkbBehaviorGraph`, and two
  new checks.** The broken-reference and orphan scans only read top-level param
  values, so refs inside inline structs were invisible — `hkRootLevelContainer`'s
  `variant` ref lives in the inline `namedVariants` struct, which falsely
  flagged the behavior graph as orphaned in every file (and hid genuinely
  broken refs inside transition arrays, now reported with indexed paths like
  `transitions[2].transition`). New check: a `startStateId` that doesn't match
  any state's `stateId` is an error naming the valid ids — the silent
  T-pose-on-activate case; machines using a start-state chooser or a
  non-default `startStateMode` are skipped. The validator's ref parsing also
  moved to the whitespace-safe tokenizer, so line-wrapped ref arrays validate
  correctly. Verified: vanilla magic/horse behaviors and an official-dialect
  file report zero issues; the capitto91 test file reports exactly its two
  real problems and nothing else.

- **The Events tab keeps `eventInfos` paired with `eventNames`.** Adding an
  event only appended the name — the matching `hkbEventInfo` record in
  `hkbBehaviorGraphData.eventInfos` was never created, and the Havok runtime
  pairs the two arrays by index, so every event added through the tab was
  broken in-game (found in a user's real file: 30 names, 26 infos). Add and
  delete now insert/remove the info record at the same index (undo/redo
  included), save reconciles the counts — which also repairs files desynced
  by older versions — and ✓ Validate flags an `eventNames`/`eventInfos` or
  `variableNames`/`variableInfos` count mismatch as an error. Found
  2026-08-02 during the capitto91 weapon-throw walkthrough.

- **Editing a `#ref` in the property editor actually sticks now.** Params whose
  resolved ref is cached in `Children` (single-ref params like `generator`, and
  one-element ref arrays) ignored text edits entirely: the value getter kept
  returning the stale cached id, so the field visually snapped back and save
  wrote the old ref — silently. After an interactive edit the cache is now
  re-resolved from the typed tokens (mutating a reference updates `Children`,
  not just `Value`): if every token resolves the cache is rebuilt, otherwise
  (typo, `null`, ref not created yet) it's cleared and the typed text becomes
  authoritative, with the save-time broken-reference check as the backstop.
  Setting a cached ref to `null` — previously impossible from the text box —
  works too. Seventh fix from the 2026-08-02 external feedback round.

- **Editing a ref-list in the property editor keeps `numelements` in sync.**
  Hand-editing an array param's text (e.g. adding a state ref to `states`)
  updated the value but never the `numelements` attribute — and HKX2 treats
  that attribute as authoritative on XML→HKX conversion, so the array was
  silently truncated to the stale count. The count now recomputes on every
  interactive value change (undo/redo restores included). Deliberately scoped
  to pure text-token arrays: string arrays are counted by their `hkcstring`
  entries and inline arrays by their child objects, so those are left alone —
  as is the load path, where a recount could fire before the elements are
  parsed. Sixth fix from the 2026-08-02 external feedback round.

- **Save now type-checks values first instead of writing garbage or dying with
  a cryptic error.** Previously, save-as-XML wrote values like
  `ararfafasaafaafsafass` into a numeric field verbatim, and save-as-HKX died
  deep in HKX2's parser with a bare "Save failed: …" plus a misleading hint —
  and left the corrupt `.tmp.xml` on disk. Saving now runs the declared-type
  check across every param (nested inline params included): HKX saves are
  blocked with a list naming each bad value (`#0925 events[0].id =
  "POOPOOOPOO" … Expected: a whole number`), XML saves warn and offer
  save-anyway, the temp file is cleaned up on a failed conversion, and the
  ✓ Validate report includes the same check. Validation matches HKX2's real
  parsing rules — pipe-joined flag combos with numeric/hex remainders
  (`FLAG_RAGDOLL|0x4c0`) are legal, verified zero false positives across
  vanilla dragon/magic/horse behaviors. Fifth fix from the 2026-08-02
  external feedback round.

- **Non-behavior Havok files graph from the file's declared root instead of a
  blank canvas.** The graph view only knew two entry points — the
  `hkbStateMachine` dropdown and the ⌂ Root button's `hkbBehaviorGraph`
  lookup — so a ragdoll, skeleton, or arbitrary Havok XML rendered nothing,
  silently. Both now fall back to the file's `toplevelobject`
  (`hkRootLevelContainer`): opening a file with no state machines shows the
  object graph seeded from the root, and ⌂ Root works in any file. The
  generic walk also descends into inline (anonymous) hkobjects — array-of-
  struct params like `namedVariants` and transition arrays carry their refs
  in nested params, not the param value — which the container root needs and
  which also links transition arrays to their transition effects. Truly empty
  views now say so ("No state machines in this file…", "Nothing to display…")
  instead of showing a bare canvas. Fourth fix from the 2026-08-02 external
  feedback round.

- **Shared children keep every inbound edge in the generator drill-down.**
  Havok generator graphs are DAGs — one modifier, clip, or binding set is
  routinely referenced by several parents — but the walk dropped the parent→
  child edge for every parent after the first, so shared nodes looked owned by
  one parent and disconnected from the rest. Revisiting an already-walked
  object now links the new parent to the existing node (without re-recursing),
  so sharing is visible as fan-in. Third fix from the 2026-08-02 external
  feedback round.

- **The generator drill-down and behavior tree now follow every `#ref` instead
  of a param-name whitelist.** The old walk knew six stock hkb param names, so
  every Bethesda class was a dead end — `BSBoneSwitchGenerator`
  (`pDefaultGenerator`/`ChildrenA`), `BSSynchronizedClipGenerator`
  (`pClipGenerator`), `BSiStateTaggingGenerator`, and friends showed as
  childless leaves (in vanilla `magicbehavior` alone, 16 bone-switch subtrees
  were invisible). The walk is now generic — any param token that resolves to
  an object becomes a child, which also surfaces trigger arrays, notify-event
  arrays, and variable binding sets as real graph nodes. Nested state machines
  stay drillable leaves rather than inlining their whole state graph. The
  behavior tree gets the same generic walk plus a proper "referenced as child"
  check, so nested state machines no longer duplicate at top level, and
  jump-to-graph ownership search (`GeneratorChainContains`) finds objects owned
  through Bethesda generator chains. Second fix from the 2026-08-02 external
  feedback round.

- **Graph and tree views no longer drop states/children whose `#ref` sits after
  a line wrap.** Havok's XML writer wraps long ref arrays (`states`, `children`,
  `generators`, `modifiers`) across lines; the graph layer split them on spaces
  only, so line-wrapped refs failed to resolve — states silently missing from
  the graph, transitions to them dropped, generator subtrees dead-ending, and
  wrong "States/Children" counts in node cards. All ref-list parsing in the
  graph view and behavior tree now goes through a shared whitespace-safe
  tokenizer (`HkRefList`). First fix from the 2026-08-02 external feedback
  round (see Roadmap, *Graph view*).

### Added

- **The in-app Guide documents the live debugger properly.** The Debugger tab
  section was a ten-line button list, which left unanswered every question the
  panel actually raises in use: what the two named pipes carry, when the config
  is re-sent, why a state card reads `state 12`, what a recording keeps and
  when it is thrown away. Three new Advanced sections cover it. *Live
  Debugging: Setup & Connection* documents the `SkyrimBehaviorDebugger` /
  `SkyrimBehaviorDebugger_Config` pipe pair and what travels each way, the
  retry-and-reconnect loop (a red status line is a wait, not a failure), every
  point at which the config is rebuilt and re-sent, and the trap that state ids
  and variable indices are resolved positionally against the file open in the
  editor — so the file to open is the Nemesis/Pandora output the game is
  running, not the pre-patch source. *Reading a Live Session* covers the ●
  LIVE badge and pulsing outline, the green edge flash that identifies the
  transition that actually fired, machine auto-follow plus pan-to-active, the
  0.001 change threshold and relevance dimming on the variable list, how the
  actor type (and therefore which variables count as relevant) is detected, and
  the mount group. *Recording & Exporting a Session* states what the panel
  never said out loud: frames arriving while paused are dropped rather than
  buffered, the capture survives ⏹ but not ⏹ Stop Debug — so export before
  stopping — and it documents the exported JSON's shape. The Debugger tab
  section itself now describes the panel top to bottom, including the pop-out
  window, and the Graph tab's live-debugging bullets mention the LIVE badge and
  auto-follow.

- **Event ids and variable indices edit as name pickers.** Every event-id
  param in the property editor was a bare integer — a transition's `eventId`,
  the `enterEventId`/`exitEventId` of a trigger or initiate interval, a state
  machine's `returnToPreviousStateEventId` / `randomTransitionEventId` /
  next-higher / next-lower ids, `hkbEventDrivenModifier`'s activate and
  deactivate ids, and the `id` of an event property (which is where notify
  events, clip triggers and event ranges keep theirs: 436 of them in vanilla
  `dragonbehavior` alone). Setting one meant counting rows in the Events tab.
  They now edit as a dropdown of this file's own event table, shown as
  `name (#index)`, with `(none)` for -1; variable-index params
  (`variableIndex`, `syncVariableIndex`, `assignmentVariableIndex`) get the
  same against the variable table. Params nested inside inline array elements
  are covered too, so interval ids and notify events pick by name as well.

  Havok publishes no metadata saying "this int is an event index", so
  `HavokTypeCatalog` marks them by name — `*EventId`, `assignmentEventIndex`,
  and `id` on any class deriving from `hkbEventBase` — verified against
  vanilla `dragonbehavior`: every int param with an event- or variable-ish
  name is annotated, and nothing else is (`stateId`, `userData` and the enums
  are untouched). An id the table doesn't cover renders as `‹unknown #N›` and
  is left alone: the picker's list is rebuilt to always contain the current
  value, so a selection can never write away a value the editor merely didn't
  recognise. Edits go through the same param setter as typing, so undo/redo
  and save-time validation are unchanged.

- **Event cross-reference — one event, or the whole table at once.** A new
  🔗 Event Xref dialog lists every event in the file with its listen/send
  counts, the references behind them, and an "unreferenced only" filter;
  double-click a reference to jump to that object, or 📋 Copy report to put the
  event's full cross-reference on the clipboard. The Events tab's own usage
  panel now runs the same scan (`EventCrossReference`), which closes the holes
  it had: it knew transitions, wildcards, clip triggers and a short whitelist
  of `*EventId` params, and missed the enter/exit ids inside a transition's
  `triggerInterval`/`initiateInterval` (nested a level below the transition, so
  a top-level param scan never saw them), state `enterNotifyEvents`/
  `exitNotifyEvents`, and every send site (`eventToSend`,
  `eventToSendWhenStateOrTransitionChanges`, annotation triggers). An
  incomplete cross-reference is worse than none — it reads as proof an event is
  unused — so hits are now tagged ◀ listens or ▶ sends and named with their
  owning machine and state. Unreferenced is reported as a lead, never a
  verdict: annotation events (HitFrame, SoundPlay.*, the dragon spell-fire
  events) are emitted from tracks inside the animation `.hkx` files, and
  cross-behaviour events are matched by name in another file's table — neither
  is visible from one behaviour graph.

- **Skyrim LE (32-bit) support, and LE ⇄ SE conversion.** The editor opens
  Skyrim Legendary Edition `.hkx` files directly and can save to either
  edition, plus a new 🔄 LE ⇄ SE toolbar command converts single files or whole
  folders in either direction. Originals are never modified: output goes to a
  folder beside the source, named after it (`Behaviors` → `Behaviors_LE`), and
  files already in the target edition are copied across rather than skipped, so
  the result is a complete drop-in copy even from a mixed folder. Beside rather
  than inside, so a second run over the same folder doesn't walk the previous
  output. Both editions use the same Havok schema
  (`hk_2010.2.0-r1`, class version 8) and differ only in packfile pointer size,
  so conversion is a pure repack that preserves the behaviour graph exactly —
  no external converter, no XML round-trip on disk.

  The bundled HKX2Library had been de-generalised to Skyrim SE: its 588
  generated classes were dumped from the SE runtime, so 908 `Position += N`
  padding constants were baked to an 8-byte pointer. `tools/hkx-layout-gen`
  now derives Havok's layout rules from the metadata each class already
  carries and re-emits that padding as `des.Padding(pad64, pad32)`. It refuses
  to emit anything it can't first reproduce byte-for-byte at 64-bit, so SE
  output is unchanged by construction — verified: every SE sample produces
  identical Havok XML and identical repacked bytes before and after. Two real
  bugs surfaced on the way: `hkUlong` members (`hkbNode.m_userData`, on every
  behaviour node) were read as a fixed 64-bit integer instead of a
  pointer-sized one, and `ALIGN_16` members were not honoured where the 64-bit
  layout happened to be 16-aligned already.

  Validated against all 180 loose vanilla Skyrim LE files (behaviours,
  skeletons and animations): all parse, and all convert to SE and back with
  byte-identical Havok XML. `tools/hkx-roundtrip` is the regression harness.

  Not covered: 19 classes still have 64-bit-only layouts — `hkp*` physics and
  ragdoll classes (multiple inheritance and vtable-only interfaces), Havok
  type-metadata classes that never appear in a serialised file, and
  `hkbGeneratorSyncInfo`, whose SE body has a pre-existing 8-byte over-read.
  None occur in behaviour, character, project, skeleton or animation files;
  writing LE is refused with a message naming them rather than producing a
  silently corrupt file.

- **Add/remove elements of inline-struct arrays in the Object Data panel.**
  Arrays whose elements are nested structs — `hkbStateMachineEventPropertyArray`
  events, transition arrays, enter/exit notify events, variable binding set
  `bindings`, clip triggers, and any other array of inline hkobjects — now get
  a ＋ Add element button and a per-element ✕. New elements are built from the
  HKX2 class defaults (correct param names, vanilla values, type-annotated for
  the red-border validation), falling back to cloning the last element for
  classes outside the type set. `numelements` is maintained, both operations
  are undoable, and the affordance survives deleting the last element. Ref
  arrays (`states`-style) edit as text — now viable end-to-end thanks to the
  whitespace tokenizer, `numelements` sync, and cache re-resolve — and string
  arrays keep their dedicated tabs. Known limitation: an array that is empty
  at load can't offer ＋ (an empty inline array is indistinguishable from an
  empty ref array). Closes the last item from the 2026-08-02 external
  feedback round ("there's no way to add or remove elements from this or any
  other kind of array").

- **Type-aware property editing.** The Object Data panel now knows each param's
  declared Havok type, reflected from the bundled HKX2 class definitions and
  annotated onto every object at load (`HavokTypeCatalog`). Booleans get a real
  checkbox (by declared type, not by sniffing the current value), enums get a
  fixed-choice dropdown (declared-as-int enums are detected via the class's
  serialized defaults), and numeric fields get live validation — a value that
  doesn't parse as the declared type (or falls outside its range, e.g. >127 in
  an int8) shows a red border with an "expected …" tooltip, in nested inline
  params too. Flags fields keep accepting their pipe-joined member names, and
  classes outside the HKX2 type set behave exactly as before. Groundwork for
  pre-save type validation. From the 2026-08-02 external feedback round ("the
  property editor isn't type safe or even type aware").

- **New behavior file** (Load → ✨ New behavior file…) — creates a minimal valid
  behavior from scratch: root container, `hkbBehaviorGraph`, graph data / string
  data / variable value set, and an empty root state machine, with vanilla
  defaults (event ids −1, discard-when-inactive). Written to a chosen path as
  XML or SE HKX and opened through the normal pipeline, ready for Add State.
  Removes the workflow trap of gutting a vanilla file and losing the root
  scaffolding. Documented in a new Guide section, *Creating a New Behavior
  File*.

- **New behavior reference…** on a state's right-click menu in the Graph tab —
  creates an `hkbBehaviorReferenceGenerator` pointing at another behavior file
  and wires it as the state's generator in one undoable step, mirroring the
  clip-generator flow. This is the bridge node for Nemesis/Pandora-style
  patches that link a custom behavior file into a vanilla graph. Documented in
  a new Guide section, *Referencing Another Behavior File*.

- The in-app Guide grew a **Clip Preview** section group covering the 0.5.0
  editing features: the preview window and its pentagon timeline markers,
  annotation editing, clip trigger editing, hkanno import/export, and the ☰
  annotation & trigger list panel. The Clips tab section now points at it.

## [0.5.0] — 2026-07-29

This cycle is about building things instead of just inspecting them — new clips,
states, and wildcard transitions from the graph, and full annotation & trigger
editing in the clip preview.

### Added

**Editing annotations in the clip preview**

- **Add, edit, and delete animation annotations** straight on the preview
  timeline — right-click or double-click the timeline to add at that spot,
  a purple tick to edit or delete it, or use the `＋` button / `A` key to add at
  the playhead. Edits write back to the animation file itself (XML or SE HKX)
  with a one-time `.bak` beside it, and everything is undoable.
- **Drag a purple tick** to move an annotation — frame-snapped while dragging,
  `Alt` for free placement, with a live time + frame readout.
- **hkanno text interchange** — copy or export the clip's annotations in hkanno's
  `<time> <text>` format (with the header `hkanno update` expects), and import or
  paste a set back as one undoable replace. Copy/export also work in read-only
  previews.
- **Annotation list panel** (`☰`) — a table of time / frame / track / text.
  Click a row to seek, edit time and text inline, `Del` to delete, right-click
  for add/edit/delete. The annotation dialog links its time and frame fields and
  offers a track picker when the animation has more than one track.
- The preview window opens bigger (900×660) and remembers the size you resize it
  to. The playhead survives annotation edits instead of resetting to zero.

**Editing clip triggers**

- **The orange ticks are now editable like the purple ones** — right-click the
  timeline to add a trigger, right-click/double-click a tick to edit or delete,
  drag to move. Trigger edits mutate the behavior graph through the normal undo
  stack and land on the next behavior save — no animation file IO.
- **Trigger dialog** with an event picker that also creates new events (typed
  names are added to the behavior's event list as part of the same undo step),
  linked time/frame fields, and an *anchor to the clip's end* checkbox — anchored
  triggers store a negative from-the-end time, so they keep their distance from
  the end if a longer animation is swapped in.
- A clip with no trigger array gets a new `hkbClipTriggerArray` created and wired
  in the same action, so it can't be dropped as an orphan on `.hkx` save. Editing
  an array referenced by several clip generators warns with the list of affected
  clips first.
- **Triggers table** in the `☰` panel under the annotations — click to seek, edit
  time inline, `Del` to delete. Timeline ticks are now little pentagon markers
  (triggers point down, annotations up) so the two are easy to tell apart.

**Building graphs**

- **`🎬 New clip generator…`** — right-click a state in the graph to create a new
  clip and point that state's generator at it in one undoable step, so it can
  never be orphaned. The Clips tab's `+ New Clip Generator` still creates one
  from scratch, and now warns that it will be dropped on `.hkx` save until
  something references it.
- **`➕ Add state`** on a state machine node.
- **Wildcard transitions can be created**, not just seen — `★ WILDCARD (any
  state)` is the first entry in the Add Transition dialog's From-State dropdown.
  The transition is written to the machine's `wildcardTransitions` array with
  `FLAG_IS_LOCAL_WILDCARD` set, creating the array if the machine has none.
- **Live-debug tracking can be wired up in-app** — right-click a state machine to
  give it a sync variable (`syncVariableIndex`) so the live debugger can track
  its active state, and the app warns when a loaded graph has no trackable
  machines at all.

**Behavior tree & modifiers**

- **Right-click menu on behavior tree items** — Jump to in graph (drills to the
  right view and highlights the node), Inspect in Object Data, Copy id / name,
  and a Bookmark toggle.
- The Add-Modifier picker leads with a curated **Common** group above the full
  A–Z list, and new modifiers are named after their target (`GetUpFaceUp` +
  `BSIsActiveModifier` → `GetUpFaceUp_IsActive`) instead of `New_<Class>`.
- The in-app Guide covers the new workflows, including a walkthrough for adding
  a brand-new animation.

### Fixed

- **Graph layout no longer hangs on very large state machines** — the layering
  pass could loop forever (and was quadratic besides) on 200+ state machines.
- Editing a trigger whose preview data had gone stale is caught with a clear
  message instead of editing the wrong trigger.

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

[0.6.0]: https://github.com/lennart99v/SageHavokEditor/releases/tag/v0.6.0
[0.5.0]: https://github.com/lennart99v/SageHavokEditor/releases/tag/v0.5.0
[0.4.0]: https://github.com/lennart99v/SageHavokEditor/releases/tag/v0.4.0
