using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SageHavokEditor.UI.Dialogs
{
    public partial class DocumentationView : UserControl
    {

        public DocumentationView()
        {
            InitializeComponent();
            Loaded += (_, __) => Build();
        }

        private readonly Dictionary<string, Block> _anchors = new();
        private RichTextBox _docBox = null!;

        public void ScrollToSection(string key)
        {
            // The doc is built on Loaded; if the Guide tab has never been shown yet,
            // build it now so the anchors exist.
            if (_anchors.Count == 0) Build();
            if (!_anchors.ContainsKey(key)) return;

            // BringIntoView() on a FlowDocument Block does not reliably drive the
            // surrounding ScrollViewer, so translate the heading's position into the
            // outer ScrollViewer's content offset and scroll there explicitly.
            // Deferred to DispatcherPriority.Loaded so the layout pass (and any tab
            // switch) has completed before we measure.
            Dispatcher.InvokeAsync(() =>
            {
                if (_docBox == null || !_anchors.TryGetValue(key, out var block)) return;
                try
                {
                    _docBox.UpdateLayout();
                    var rect = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    if (rect.IsEmpty) return;
                    double y = _docBox.TransformToAncestor(ContentPanel)
                                      .Transform(new Point(0, rect.Top)).Y;
                    ContentScroller.ScrollToVerticalOffset(System.Math.Max(0, y - 8));
                }
                catch
                {
                    // Layout not ready or visual tree changed — fall back to the
                    // best-effort built-in behaviour.
                    block.BringIntoView();
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Build()
        {
            NavPanel.Children.Clear();
            _anchors.Clear();

            _docBox = new RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                IsTabStop = false
            };
            _docBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);          // outer ScrollViewer handles scrolling
            _docBox.Document.Blocks.Clear();
            _docBox.Document.PagePadding = new Thickness(0);

            ContentPanel.Children.Clear();
            ContentPanel.Children.Add(_docBox);

            AddNavHeader("Overview");
            AddSection("overview", "Overview",
                "Sage Havok Editor is a WPF-based desktop editor for Skyrim " +
                "Havok behaviour files (.hkx / .xml). It lets you view, edit, and export behaviour graphs " +
                "without hand-editing XML. The editor parses the Havok object graph into a typed data model " +
                "and lets you navigate every object, edit parameters, manage variables and events, and " +
                "visualise state-machine transitions as an interactive node graph.");

            AddSection("behavior_files", "What Are Behavior Files?",
    "Havok Behavior files (.hkx) are the animation logic layer that sits between Skyrim's " +
    "animation clips and the game engine. They tell the engine which animation to play, " +
    "when to switch between animations, and how to blend between them — all driven by " +
    "in-game conditions like speed, weapon type, or combat state.\n\n" +
    "Structure\n" +
    "• hkbStateMachine — the core building block. A state machine contains a set of states " +
    "and a table of transitions between them. When a triggering event fires, the SM switches " +
    "to the target state and plays its animation.\n" +
    "• hkbStateMachineStateInfo — a single state inside a state machine. Each state points " +
    "to a generator (the actual animation source) and optionally holds its own transition table.\n" +
    "• hkbClipGenerator — a leaf node that references a specific .hkx animation clip by path. " +
    "This is what ultimately plays on the skeleton.\n" +
    "• hkbBlenderGenerator / hkbManualSelectorGenerator — blend or switch between multiple " +
    "child generators based on variable values, creating smooth pose mixing.\n" +
    "• hkbModifierGenerator — wraps a generator and applies modifiers on top, such as " +
    "foot IK, look-at constraints, or procedural bone adjustments.\n" +
    "• hkbVariableBindingSet — binds a Havok variable to a specific parameter on an object, " +
    "so that changing the variable at runtime automatically drives that property.\n\n" +
    "Variables and Events\n" +
    "• Variables are named float, int, or bool values that the game writes every frame " +
    "(e.g. Speed, Direction, IsSneaking). State machines read these to choose transitions.\n" +
    "• Events are one-shot signals fired by game code or animation notifies " +
    "(e.g. AttackStart, FootDown). Transitions use events as their trigger condition.\n\n" +
    "File layout\n" +
    "A typical character has three files: a project file (.hkx) that ties everything together, " +
    "a character file that references the skeleton and lists all available animations, and one " +
    "or more behavior files that contain the actual state machine logic. " +
    "Sage Havok Editor can open any of the three and will automatically follow the references " +
    "to load the full chain.");

            AddNavHeader("Getting Started");
            AddSection("getting_started", "Getting Started",
                "1. Open a file — use Load or drag a .hkx/.xml onto the window. (Starting a mod " +
                "from scratch? Load → ✨ New behavior file… scaffolds a valid empty behavior; " +
                "see Creating a New Behavior File.)\n" +
                "2. The editor loads all Havok objects and populates every tab.\n" +
                "3. Navigate to the Graph tab first for a visual overview of the state machines.\n" +
                "4. Edit any value directly in the Variables, Events, or Transitions tabs.\n" +
                "5. Save with the Save button (or Ctrl+S). The file is serialised back to Havok XML.\n" +
                "6. Use the Patch button to produce a Nemesis or Pandora-compatible patch folder.");

            AddNavHeader("Tabs");

            AddSection("tab_graph", "Graph Tab",
                "An interactive node-graph canvas showing every state and transition in the currently " +
                "selected state machine.\n\n" +
                "Mouse controls\n" +
                "• Scroll wheel — zoom in/out toward the cursor.\n" +
                "• Middle-mouse drag — pan the canvas.\n" +
                "• Left-click a node — select it and open its data in the Object Data panel.\n" +
                "• Left-click drag on empty space — lasso-select multiple nodes.\n" +
                "• Hover a node or transition — a tooltip card shows its key details (state ID, " +
                "generator, animation path, blend duration). Hovering a transition also enlarges its " +
                "event label so it stays readable when zoomed out.\n" +
                "• Drag a node — moving it shows pink alignment guides and snaps to other nodes' edges " +
                "and centres. Hold Alt to disable snapping.\n" +
                "• Double-click a state node — drill down into its generator hierarchy.\n" +
                "• Drag from the right port of a node to another state — create a transition. Valid " +
                "targets are ringed in green and invalid ones dimmed while you drag.\n" +
                "• Drag the arrowhead end of a transition onto a different state — re-target the " +
                "transition's destination without recreating it.\n\n" +
                "Toolbar\n" +
                "• Machine selector — choose which hkbStateMachine to display.\n" +
                "• ← Back — return from a drill-down level.\n" +
                "• Search box — type a node name to highlight and jump to it.\n" +
                "• Layout — re-run the automatic layout algorithm.\n" +
                "• Fit — zoom and pan so all nodes are visible.\n" +
                "• Pan-to-active — when the live debugger is running, keep the viewport centred on the currently active state.\n" +
                "• Export PNG — render the current graph to a PNG file.\n\n" +
                "Keyboard shortcuts (while the graph has focus)\n" +
                "• F — fit to view.\n" +
                "• Delete / Backspace — delete the selected node.\n" +
                "• F2 — rename the selected node inline.\n" +
                "• C — wrap selected nodes in a comment box.\n" +
                "• Q — align selected nodes in a horizontal row.\n" +
                "• W — align selected nodes in a vertical column.\n" +
                "• E — distribute selected nodes evenly.\n" +
                "• Ctrl+1-9 — save a viewport bookmark.\n" +
                "• 1-9 — jump to a saved bookmark.\n" +
                "• Escape — clear selection and search highlight.\n\n" +
                "Wildcard transitions\n" +
                "• Wildcard (high-priority) transitions fire from ANY state in a machine, so they " +
                "aren't anchored to a single node. They are drawn from a dedicated amber ★ ANY " +
                "source node with dashed amber edges to each target state. This makes the otherwise " +
                "invisible \"random/high-priority\" triggers (e.g. a creature's special-attack or " +
                "death state) easy to find.\n" +
                "• Clicking the ★ ANY node opens its state machine in the Object Data panel.\n" +
                "• Right-click the ★ ANY node → ➕ Add Wildcard Transition to create a new one; see " +
                "Creating a Wildcard Transition.\n\n" +
                "Edge right-click menu\n" +
                "• Go to event — jump straight to the triggering event's definition and its full " +
                "usage list (works on normal and wildcard edges).\n" +
                "• Disable / Enable transition — toggles the Havok FLAG_DISABLED flag. A disabled " +
                "transition is drawn dimmed and dashed with a ⊘ marker on its label, and never fires " +
                "in-game until re-enabled. Fully undoable.\n" +
                "• Delete Transition — removes the transition.\n\n" +
                "Live debugging\n" +
                "• Active states glow with an animated green outline and carry a ● LIVE badge.\n" +
                "• When a transition fires, its edge pulses green so you can trace the flow as it happens.\n" +
                "• The machine dropdown auto-follows the actor — entering a state that belongs "
                + "to a different machine switches the graph to it — and 🎯 pan-to-active "
                + "keeps the active node centred.\n" +
                "• See Reading a Live Session for the whole picture.\n\n" +
                "Node right-click menu\n" +
                "• 🎬 New clip generator… — on a state: creates a new hkbClipGenerator and points that " +
                "state's generator at it in one step. See Adding a New Animation.\n" +
                "• 🔗 New behavior reference… — on a state: creates a new hkbBehaviorReferenceGenerator " +
                "pointing at another behavior file and wires it as the state's generator. See " +
                "Referencing Another Behavior File.\n" +
                "• ⧉ Duplicate state… — on a state: copies the state and everything hanging off it " +
                "(generator chain, transitions, notify events) with fresh ids, and adds the copy to " +
                "the same machine. See Duplicating a State.\n" +
                "• 🐞 Enable live-debug tracking — on a state machine (or empty canvas with a machine " +
                "selected): makes that machine report its active state to the debugger. Only machines " +
                "with syncVariableIndex set can be tracked; see Why Active States Are Empty.\n\n" +
                "Right-click context menus are available on nodes, edges, and empty canvas space " +
                "for additional actions including Add State, Add State Machine, Add modifier, and " +
                "Re-layout.");

            AddSection("object_data", "The Object Data Panel",
                "The right-hand panel shows every parameter of the selected object, and it knows " +
                "each parameter's declared Havok type (from the bundled HKX2 class definitions).\n\n" +
                "• Booleans edit as a checkbox, enums as a fixed-choice dropdown.\n" +
                "• Event ids and variable indices edit as name pickers rather than numbers — " +
                "a transition's eventId, a trigger or initiate interval's enterEventId/exitEventId, " +
                "a machine's returnToPreviousStateEventId and friends, the id of an event property " +
                "(notify events, clip triggers), variableIndex and syncVariableIndex. The list is " +
                "this file's own event or variable table, shown as name (#index), with (none) for " +
                "-1. An id the table doesn't cover shows as ‹unknown #N› and is left exactly as it " +
                "was — nothing is silently renumbered.\n" +
                "• Numeric fields validate live: a value that doesn't parse as the declared type — or " +
                "falls outside its range, like 200 in an int8 — gets a red border and an \"expected …\" " +
                "tooltip. Nested params inside array elements are validated too. Saving as HKX is " +
                "blocked while such values exist (the conversion would reject them anyway); saving " +
                "as XML warns first.\n" +
                "• References (#0123, shown blue) jump with ↗ or Ctrl+Click. Editing a reference by " +
                "typing re-resolves it properly — including setting it to null.\n" +
                "• Ref arrays (like a machine's states) edit as space-separated #ids; the numelements " +
                "count is maintained automatically. The states param also has the ✏ Edit States dialog.\n" +
                "• Arrays of nested elements (event property arrays, transition arrays, notify events, " +
                "binding sets…) have a ＋ Add element button and a per-element ✕. New elements are " +
                "created with vanilla defaults. Both are undoable. An array that is empty when the " +
                "file loads can't offer ＋ yet — add the first element by hand in XML, or start from " +
                "a file that has one.\n" +
                "• Every edit lands on the normal undo stack (Ctrl+Z).");

            AddSection("tab_variables", "Variables Tab",
                "Lists every behaviour variable (hkbBehaviorGraphData / hkbBehaviorGraphStringData).\n\n" +
                "• Type badge — coloured chip showing BOOL, INT, FLOAT, PTR, etc.\n" +
                "• Value editor — inline TextBox for numeric/string values; ComboBox for booleans.\n" +
                "• + Add Variable — creates a new variable and wires it into all three backing objects.\n" +
                "• − Delete Variable — removes the variable after checking for usages. You are warned if usages are found.\n" +
                "• Search box — filters the list in real time.");

            AddSection("tab_events", "Events Tab",
                "Lists every behaviour event (hkbBehaviorGraphStringData.eventNames).\n\n" +
                "• Each row shows the event index and an editable name.\n" +
                "• The usages panel at the bottom shows every place in the file that references the " +
                "selected event, tagged ◀ listens (something reacts to it) or ▶ sends (something " +
                "emits it): state and wildcard transitions, the enter/exit ids of a transition's " +
                "trigger and initiate intervals, a machine's returnToPrevious / random / " +
                "next-higher / next-lower state ids, event-driven modifiers, state enter/exit " +
                "notify events, clip annotation triggers, and eventToSend fields. Click a usage " +
                "to jump straight to it.\n" +
                "• 🔗 Event Xref in the toolbar runs the same cross-reference for every event at " +
                "once — see Event Cross-Reference.\n" +
                "• Everywhere else in the editor, an event is shown by its resolved name rather than a " +
                "raw numeric id. If a referenced id has no name it appears as ‹unnamed #N› so you can " +
                "still trace it. Right-click an event in the graph, the Transitions list, or the SM " +
                "Inspector and choose Go to event to land here on the matching row with its usages.\n" +
                "• + Add Event / Delete work the same as the Variables equivalents. Both keep the " +
                "per-event info records (hkbBehaviorGraphData.eventInfos) paired with the names — " +
                "the game matches the two arrays by position, so a mismatch breaks events in-game. " +
                "Saving also reconciles the counts, which repairs files desynced by older editor " +
                "versions, and 🔎 Validate flags any remaining mismatch.");

            AddSection("tab_transitions", "Transitions Tab",
                "A flat list of every hkbStateMachineTransitionInfoArray entry in the file.\n\n" +
                "• Columns: From state, To state, Event, Blend duration.\n" +
                "• Click a row for the full detail panel: a plain-language \"when it fires\" sentence, " +
                "the triggering event, decoded flag badges, routing (priority, and the nested state " +
                "the transition lands on resolved to its name), the blend effect's duration / curve / " +
                "start fraction / end mode, the condition, and the trigger and initiate intervals " +
                "with their enter/exit events. Everything except the blend fields lives on the " +
                "transition itself, so it shows even when the transition has no effect object.\n" +
                "• Right-click a row → Go to event to jump to the triggering event's definition and usages.\n" +
                "• Filter box narrows the list by state or event name.");

            AddSection("tab_clips", "Clips Tab",
                "Lists every hkbClipGenerator in the file.\n\n" +
                "• Shows the clip name and the animation file it references.\n" +
                "• Inline editing lets you change the animation path directly or browse with the folder button.\n" +
                "• The trigger panel at the bottom shows all timed events attached to the selected clip.\n" +
                "• ▶ on a row opens the animation in the Clip Preview window, where annotations and " +
                "triggers can be edited on the timeline — see The Clip Preview.\n" +
                "• + New Clip Generator — creates a new hkbClipGenerator from scratch. It is created " +
                "unattached, so nothing references it yet; see Adding a New Animation for why that " +
                "matters and how to wire it up.");

            AddSection("tab_sm_inspector", "SM Inspector Tab",
                "A full transition editor for a single hkbStateMachine.\n\n" +
                "• Select a state machine from the dropdown to load all its transitions.\n" +
                "• + Add Transition — opens a dialog to pick source state, target state, event, and flags. " +
                "Choose ★ WILDCARD (any state) as the source to create a wildcard; see " +
                "Creating a Wildcard Transition.\n" +
                "• + Add State — adds a new state to the selected machine without building the graph. " +
                "Useful for very large machines, and the fastest route generally. The new state starts " +
                "with no generator, so give it one (see Adding a New Animation) before using it.\n" +
                "• Edit and Delete buttons act on the selected row.\n" +
                "• Wildcard transitions (★ WILDCARD) are shown at the bottom of the list — these are the " +
                "from-any-state, high-priority triggers also drawn from the ★ ANY node in the Graph tab.\n" +
                "• Right-click a row for: Go to event (jump to the event definition + usages) and " +
                "Enable / Disable transition (toggles FLAG_DISABLED, marked with ⊘; undoable).");

            AddSection("tab_bindings", "Bindings Tab",
                "Lists every hkbVariableBindingSet entry found in the file.\n\n" +
                "• Each row shows the owner object, the member path being bound, and the variable it is bound to.\n" +
                "• Click a row to open the owner object in the Object Data panel.\n" +
                "• Filter box narrows by owner name, variable name, or member path.");

            AddSection("tab_project", "Project Tab",
                "Shows file-level metadata from hkbProjectData and hkbProjectStringData.\n\n" +
                "• Open Project / Save Project / New Project toolbar buttons.\n" +
                "• World Up and Default Event Mode fields are editable directly.\n" +
                "• The Characters list shows every character file referenced by the project. " +
                "Click Open to load a character file, or + Add to reference a new one.");

            AddSection("tab_character", "Character Tab",
                "Displays and edits hkbCharacterData and hkbCharacterStringData.\n\n" +
                "• Identity — character name.\n" +
                "• Physics Capsule — height and radius used for collision.\n" +
                "• File Paths — skeleton, ragdoll, and linked behavior paths with browse buttons.\n" +
                "• Open → jumps straight to the linked behavior file.\n" +
                "• Animation Names — the list of animation files registered to this character.");

            AddSection("tab_debugger", "Debugger Tab",
                "The Live Debugger shows what a running Skyrim actor's behaviour graph is actually "
                + "doing — which states are active, which transition just fired, and what every "
                + "behaviour variable is worth — lined up against the file you have open in the "
                + "editor. It needs the SkyrimBehaviorDebugger SKSE plugin installed in the game; "
                + "see Live Debugging: Setup & Connection for how the two halves find each other.\n\n"
                + "The same panel lives in two places. Docked it is the 🎮 Debugger tab; "
                + "⧉ Pop Out detaches it into a small always-on-top window you can park on a second "
                + "monitor while Skyrim runs full-screen. Both are bound to the same data, so nothing "
                + "resets when you detach or re-dock — the tab header reads 🎮 Debugger ⧉ "
                + "while it is floating, the button becomes ↩ Dock, and closing the floating window "
                + "docks it again.\n\n"
                + "What the panel shows, top to bottom\n"
                + "• Header — a status dot (grey before you start, green while connected, dark red "
                + "when the pipe drops and the client is retrying), the detected actor's icon and name, "
                + "and the panel buttons.\n"
                + "• ACTIVE STATES — one card per tracked state machine: the machine name in blue "
                + "above its current state's name in green. An empty list here is the usual first-run "
                + "surprise and almost never a broken connection; see Why Active States Are Empty.\n"
                + "• TRANSITION HISTORY — a timestamped log, newest first, of every state entry as "
                + "it happens, written as machine → state. It keeps the last 50 entries; a state that "
                + "is merely still active is not repeated, so every line is a real entry into that "
                + "state.\n"
                + "• VARIABLES — the actor's live variable values, with a second collapsible "
                + "🐉 group underneath for the mount whenever the actor is riding. Both group "
                + "headers collapse, which is worth doing on 0_master's ~120 variables.\n\n"
                + "Buttons\n"
                + "• ⏸ / ▶ Pause — freezes the panel without dropping the connection. "
                + "Snapshots that arrive while paused are discarded rather than queued, so resuming shows "
                + "the live present instead of replaying a backlog — and they are not recorded "
                + "either.\n"
                + "• ⏺ Record — captures every snapshot to memory. The icon turns into a "
                + "bright ⏹ while recording, and stopping reports the frame count in the status "
                + "bar.\n"
                + "• 💾 Export — writes the captured session to JSON; see Recording & "
                + "Exporting a Session.\n"
                + "• 🎯 Pan-to-active — keeps the graph viewport centred on the active "
                + "state. Same toggle as the 🎯 button on the graph toolbar; the icon sits at full "
                + "opacity while it is on.\n"
                + "• ? in the tab header opens this page.\n\n"
                + "Starting and stopping happen from the toolbar's 🎮 Live Debug button, not from "
                + "this tab.");

            AddSection("tab_bookmarks", "Bookmarks Tab",
                "Stores named references to Havok objects for quick navigation.\n\n" +
                "• Click the 🔖 bookmark icon in the Object Data header to bookmark the current object.\n" +
                "• Click a bookmark row to jump straight to that object and open it in Object Data.\n" +
                "• ✕ removes a bookmark. Bookmarks persist between sessions via AppData.");

            AddNavHeader("Clip Preview");

            AddSection("clip_preview", "The Clip Preview",
                "A skeleton-aware animation player in its own window. Open it with the ▶ button on a " +
                "Clips tab row, the ▶ next to an animation name on the Character tab, ▶ Preview in the " +
                "Object Data panel, or by right-clicking a state in the graph → Show animation & tags.\n\n" +
                "Playback\n" +
                "• Play/pause, a scrubbable timeline, and front / side / top camera views.\n" +
                "• Ctrl+click a timeline tick to seek straight to it.\n" +
                "• The window remembers the size you resize it to.\n\n" +
                "Timeline markers\n" +
                "• Purple pentagons pointing up are annotations — timed text markers stored inside the " +
                "animation file itself (the hkanno kind).\n" +
                "• Orange pentagons pointing down are clip triggers — timed behaviour events stored on " +
                "the clip's hkbClipGenerator in the behaviour graph.\n" +
                "• Both are editable right on the timeline (see the next two sections), but their edits " +
                "land in different places: annotation edits write to the animation file, trigger edits " +
                "are behaviour edits saved with the behaviour file.");

            AddSection("preview_annotations", "Editing Annotations",
                "Annotations are the timed text markers inside an animation file — the same data hkanno " +
                "edits from the command line. The preview edits them in place.\n\n" +
                "Add / edit / delete / move\n" +
                "• Right-click or double-click the timeline to add an annotation at that spot.\n" +
                "• The ＋ button next to play — or the A key — adds one at the playhead.\n" +
                "• Right-click or double-click a purple tick to edit or delete it.\n" +
                "• Drag a tick to move it — frame-snapped while dragging, hold Alt for free placement, " +
                "with a live time + frame readout.\n\n" +
                "The annotation dialog\n" +
                "• Time and frame fields are linked — edit either and the other follows.\n" +
                "• Add flows pre-fill the nearest frame boundary; editing keeps the exact time unless " +
                "you change it.\n" +
                "• If the animation has more than one annotation track, a track picker appears " +
                "(new annotations default to track 0, the hkanno convention).\n\n" +
                "Where the edits go\n" +
                "• Edits write back to the animation file itself (XML or SE HKX). The first write makes " +
                "a one-time .bak copy beside the file.\n" +
                "• Everything is undoable — undo rewrites the file and refreshes the preview — and the " +
                "playhead stays where it was instead of resetting to zero.");

            AddSection("preview_triggers", "Editing Clip Triggers",
                "Clip triggers fire a behaviour event at a set time while a clip plays (footsteps, hit " +
                "frames, weapon swings). Unlike annotations they live in the behaviour graph — on the " +
                "clip's hkbClipTriggerArray — so editing them is a behaviour edit, not an animation " +
                "file edit.\n\n" +
                "Add / edit / delete / move\n" +
                "• Right-click the timeline → ⚡ Add trigger.\n" +
                "• Right-click or double-click an orange tick to edit or delete it; drag to move " +
                "(same frame-snap and Alt behaviour as annotations).\n\n" +
                "The trigger dialog\n" +
                "• The event picker lists every existing event — or type a new name and the event is " +
                "added to the behaviour's event list as part of the same undo step.\n" +
                "• Time and frame fields are linked; time is always entered as absolute clip time.\n" +
                "• Anchor to the clip's end stores the time as a negative offset from the end, so the " +
                "trigger keeps its distance from the end if a longer animation is swapped in later.\n\n" +
                "Safety\n" +
                "• Trigger edits go through the normal undo stack and land on the next behaviour save — " +
                "no animation file IO.\n" +
                "• A clip with no trigger array gets a new hkbClipTriggerArray created and wired in the " +
                "same action, so it cannot be dropped as an orphan on .hkx save.\n" +
                "• If the trigger array is shared by several clip generators, the editor warns you with " +
                "the list of affected clips before the edit.");

            AddSection("preview_hkanno", "hkanno Import & Export",
                "The preview speaks hkanno's text format, so annotations round-trip with existing " +
                "tooling and can be shared as plain text.\n\n" +
                "• Right-click the timeline to copy all annotations to the clipboard, or export them to " +
                "a .txt file — complete with the header hkanno update expects, so the file round-trips " +
                "unchanged. Export defaults to ‹animation›.anno.txt.\n" +
                "• Import from .txt or paste from clipboard replaces the clip's annotations as one " +
                "undoable step; undo restores the originals to their original tracks.\n" +
                "• Out-of-range times are clamped (you are told how many); imported annotations land " +
                "on track 0.\n" +
                "• Copy and export also work in read-only previews.");

            AddSection("preview_list", "The Annotation & Trigger List (☰)",
                "The ☰ button toggles a side panel listing every annotation and trigger in the clip — " +
                "the fastest way to work through a long timeline. Whether it is open is remembered " +
                "between sessions.\n\n" +
                "Annotations table\n" +
                "• Columns: time / frame / track / text. Click a row to seek there.\n" +
                "• Time and text edit inline, through the same undoable pipeline as the dialog.\n" +
                "• Del deletes the selected row; right-click a row for add / edit / delete.\n" +
                "• The Trk column hides itself when the file has a single track.\n\n" +
                "Triggers table\n" +
                "• Sits below the annotations: click to seek, edit time inline, Del to delete.\n\n" +
                "Read-only previews show the same tables without editing.");

            AddNavHeader("Advanced");

            AddSection("new_behavior", "Creating a New Behavior File",
                "Load → ✨ New behavior file… creates a fresh, minimal behavior from scratch: " +
                "the root container, an hkbBehaviorGraph, its graph data / string data / variable " +
                "value set, and an empty root state machine wired in as the root generator. Pick a " +
                "location and name; the file is written there (XML or SE HKX) and opened " +
                "immediately, ready for Add State and New clip generator.\n\n" +
                "Why start here\n" +
                "A behavior file is not an empty canvas — the root scaffolding above is mandatory, " +
                "and every object must stay reachable from it to survive a save to .hkx (see the " +
                "orphan-pruning note under Adding a New Animation). Opening a vanilla file and " +
                "deleting everything destroys that scaffolding and leaves nothing valid to build " +
                "on. The template gives you the correct skeleton with vanilla defaults (event ids " +
                "-1, discard-when-inactive variable mode) for free.\n\n" +
                "Typical use\n" +
                "This is step one of a custom-behavior mod: build your states and clips inside the " +
                "new file, then patch a vanilla graph with a behavior reference pointing at it — " +
                "see Referencing Another Behavior File.");

            AddSection("new_animation", "Adding a New Animation",
                "Playing a new animation means adding a new hkbClipGenerator — the leaf node that " +
                "points at an .hkx animation file — and attaching it to a state.\n\n" +
                "The quick way (recommended)\n" +
                "• Open the Graph tab, right-click the state that should play the animation, and choose " +
                "🎬 New clip generator….\n" +
                "• Enter a name and the animation path (e.g. Animations\\MyAttack.hkx).\n" +
                "• The editor creates the clip and points that state's generator at it in one step. " +
                "If the state already had a generator you are asked to confirm the replacement.\n" +
                "• The whole action is undoable.\n\n" +
                "Unreferenced clips are dropped on save\n" +
                "This is the trap to know about. Saving to .hkx writes the object graph starting from " +
                "the root and following references, so any object that nothing points at is silently " +
                "discarded — no error, no warning. A clip created on its own and left unattached will " +
                "simply be gone the next time you open the file.\n" +
                "• So: always attach the clip to a state's generator before saving as .hkx.\n" +
                "• + New Clip Generator on the Clips tab creates an unattached clip on purpose (useful " +
                "if you intend to wire it by hand in the Object Data panel), and warns you that it is " +
                "not referenced yet.\n" +
                "• Saving as .xml keeps unreferenced objects, so it is a safe intermediate format if " +
                "you want to park work in progress.\n\n" +
                "Sensible defaults\n" +
                "New clips are created with playbackSpeed 1.0 and animationBindingIndex -1, matching " +
                "vanilla clips. A playbackSpeed of 0 never advances the animation, so it would look " +
                "frozen in-game.\n\n" +
                "Don't forget the animation itself\n" +
                "The clip only references an animation path. The .hkx animation file still has to exist " +
                "under the actor's folder, and be registered in the character file's Animation Names " +
                "list (Character tab) — otherwise the clip has nothing to play. If you are shipping a " +
                "Nemesis/Pandora patch, the animation is registered through the patch as usual.");

            AddSection("duplicate_state", "Duplicating a State",
                "Custom behavior work is usually a family of near-identical states — Aim, Throw, " +
                "Recall, Catch off one clip pattern. Building the second one by hand means creating " +
                "the state, its clip, its modifiers and its transition array object by object, and " +
                "one missed reference leaves the copy quietly driving the original's generator.\n\n" +
                "Making a copy\n" +
                "• Graph tab → right-click the state → ⧉ Duplicate state….\n" +
                "• Name the copy. The dialog says how many objects it will create and re-counts as " +
                "you change the two options.\n" +
                "• Duplicate the generator subtree — on by default. Off, the copy points at the " +
                "same generator as the original, so editing that generator changes both states.\n" +
                "• Copy the outgoing transitions — on by default. Off, the copy starts with no " +
                "outgoing transitions (it never shares the original's transition array, because " +
                "editing one state's transitions would then edit the other's).\n" +
                "• The whole thing is one undoable action, and the copy is added to the same state " +
                "machine's states list — which is what keeps it out of the orphan-pruning .hkx save.\n\n" +
                "What is copied and what is shared\n" +
                "• Copied: the state, its generator chain (clips, modifiers, blend/select nodes, " +
                "nested state machines and their states), its variableBindingSet, its enter/exit " +
                "notify-event arrays, and its transition array.\n" +
                "• Shared on purpose: hkbBlendingTransitionEffect and other transition effects. One " +
                "effect normally serves the whole file, it carries no per-state data, and a copy per " +
                "duplicated transition would be pure bloat.\n" +
                "• A state whose generator is a nested state machine copies that whole machine, which " +
                "is why the object count is worth reading before you confirm.\n\n" +
                "Nothing transitions to the copy yet\n" +
                "Transitions route by stateId, and the copy is given a fresh stateId — unique within " +
                "its machine. So the copy exists, is wired into the machine and will be saved, but " +
                "nothing reaches it in-game until you add an incoming transition (right-click the " +
                "source state → ➕ Add Transition from this state).\n\n" +
                "Names\n" +
                "Copies are renamed so the file has no new name collisions. Where a child's name " +
                "contains the original state's name the rename carries through — duplicating Aim as " +
                "Throw turns AimClip into ThrowClip — otherwise the copy gets a _2 suffix. Havok " +
                "itself doesn't care about names, but every list, picker and graph label here does.\n\n" +
                "Animations are not copied\n" +
                "A copied clip generator keeps the original's animationName, so both states play the " +
                "same animation until you point the copy at a different one (Object Data, or the " +
                "Clips tab). The new animation still has to be registered in the character file — " +
                "see Adding a New Animation.");

            AddSection("behavior_reference", "Referencing Another Behavior File",
                "An hkbBehaviorReferenceGenerator embeds a whole other behavior graph where a state's " +
                "generator would normally sit. Vanilla uses it to split the graph across files " +
                "(0_master pulls in 1hm_behavior, magicbehavior, and so on), and it is the standard " +
                "bridge for mods: patch a vanilla graph with one new state whose generator is a " +
                "behavior reference pointing at your own, self-contained behavior file.\n\n" +
                "Creating one\n" +
                "• Open the Graph tab, right-click the state, and choose 🔗 New behavior reference….\n" +
                "• Enter a node name and the referenced file's path. The path is relative to the " +
                "character project's folder — e.g. Behaviors\\MyMod.hkx for a file next to the vanilla " +
                "behaviors.\n" +
                "• The editor creates the node and points the state's generator at it in one undoable " +
                "step. If the state already had a generator you are asked to confirm the replacement.\n\n" +
                "How the two graphs talk\n" +
                "The link is by name: an event (or variable) with the identical name in both files' " +
                "string data is the same event at runtime. So the events that drive transitions inside " +
                "the referenced file must also exist in the referencing graph's eventNames — add them " +
                "on the Events tab of both files as part of the same patch.\n\n" +
                "Things that bite\n" +
                "• The referenced file is not opened or validated — the path is stored as text, and a " +
                "typo becomes a silent T-pose in-game, not an error here.\n" +
                "• The orphan-pruning rule from Adding a New Animation applies: the reference is wired " +
                "to the state immediately precisely so it survives the .hkx save.\n" +
                "• The referenced file must be a valid SSE 64-bit behavior with its own root " +
                "(hkbBehaviorGraph, string data, variable value set) — Load → ✨ New behavior " +
                "file… scaffolds exactly that; see Creating a New Behavior File.");

            AddSection("large_machines", "Working With Very Large State Machines",
                "Some machines are huge — a few hundred states with thousands of transitions. " +
                "They are fully editable, but you do not have to render the graph to work on them.\n\n" +
                "Add a state without the graph\n" +
                "• SM Inspector tab → select the machine → + Add State.\n" +
                "• You are asked for a name; the editor picks the next free stateId within that machine " +
                "and links the state into the machine's states list.\n" +
                "• The new state starts with no generator. Give it one — the quickest way is the Graph " +
                "tab's 🎬 New clip generator… on that state (see Adding a New Animation). A state whose " +
                "generator is null has nothing to play if it is ever entered.\n" +
                "• Add transitions to it from the same tab with + Add Transition. So a state can be " +
                "created, wired, and connected entirely from the SM Inspector.\n\n" +
                "Graph layout on large machines\n" +
                "The graph uses Graphviz for layout when it is installed (C:\\Program Files\\Graphviz\\" +
                "bin\\dot.exe) and falls back to a built-in layout otherwise. Both handle large cyclic " +
                "machines; installing Graphviz simply gives nicer results on dense graphs.\n" +
                "• Use the machine selector to view one machine at a time rather than -- All Machines --.\n" +
                "• Fit (F) and the minimap help you find your way around once it is drawn.");

            AddSection("wildcard_create", "Creating a Wildcard Transition",
                "A wildcard fires from ANY state in a machine, rather than from one specific state. " +
                "Vanilla uses them for things that must be able to interrupt whatever is playing — " +
                "entering a death or stagger state, a creature's special attack, and so on.\n\n" +
                "Where they actually live\n" +
                "A normal transition is stored on its source state (hkbStateMachineStateInfo.transitions). " +
                "A wildcard has no source state, so it is stored on the state machine itself, in " +
                "hkbStateMachine.wildcardTransitions. That is why it is drawn from the amber ★ ANY node " +
                "in the Graph tab instead of from a state.\n\n" +
                "How to create one\n" +
                "• SM Inspector tab → + Add Transition, then pick ★ WILDCARD (any state) at the top of " +
                "the From State dropdown.\n" +
                "• Or Graph tab → right-click the amber ★ ANY node → ➕ Add Wildcard Transition, which " +
                "opens the same dialog with ★ WILDCARD already selected.\n" +
                "• Pick the triggering event and the target state as usual, then confirm.\n\n" +
                "What the editor does for you\n" +
                "• The transition is written to the machine's wildcardTransitions array, not to a state. " +
                "If the machine has no wildcard array yet, one is created and linked.\n" +
                "• FLAG_IS_LOCAL_WILDCARD is added to the flags automatically — Havok does not treat a " +
                "transition as a wildcard without it, so a wildcard missing this flag simply never fires.\n" +
                "• The action is undoable, and the new wildcard appears immediately as a ★ WILDCARD row " +
                "in the SM Inspector and as a dashed amber edge from ★ ANY in the graph.\n\n" +
                "Editing and removing\n" +
                "Wildcard rows behave like any other transition: right-click for Go to event, " +
                "Enable / Disable transition (FLAG_DISABLED), or Delete.");

            AddSection("debug_setup", "Live Debugging: Setup & Connection",
                "Live debugging pairs the editor with a running game. The game side is the "
                + "SkyrimBehaviorDebugger SKSE plugin, which reads the behaviour state of the actor you "
                + "are controlling; the editor side is a client that renders it against the graph you "
                + "have open. Both have to run on the same PC — the link is a pair of local named "
                + "pipes, not a network socket.\n\n"
                + "What flows over which pipe\n"
                + "• SkyrimBehaviorDebugger — game → editor. One JSON snapshot per line: the "
                + "actor's name and behaviour file, its active states as machine name plus numeric state "
                + "id, every watched variable's value, and the same again for the mount when riding.\n"
                + "• SkyrimBehaviorDebugger_Config — editor → game. Tells the plugin what to "
                + "watch: the loaded file's variables with each one's type (float for REAL, VECTOR and "
                + "QUATERNION variables, int for everything else), plus one entry per state machine that "
                + "can report its state, giving the machine's name and the variable to read it from.\n\n"
                + "Starting a session\n"
                + "• Click 🎮 Live Debug in the toolbar. The button becomes ⏹ Stop Debug "
                + "and the status bar reads ⏳ Live debugger started — launch Skyrim with SKSE.\n"
                + "• Order does not matter. The client retries about once a second until the game "
                + "appears and re-connects by itself if the game exits or reloads, so 🔴 Live "
                + "debugger disconnected — retrying… is a wait, not an error.\n"
                + "• On connect the status bar reads 🟢 Live debugger connected and the config "
                + "is re-sent automatically.\n"
                + "• ⏹ Stop Debug clears the panel, drops the graph highlight and discards the "
                + "client — including anything you recorded, so export first.\n\n"
                + "When the config is sent\n"
                + "• On start, on every re-connect, and whenever you load a file while the debugger is "
                + "running — so switching files mid-session re-points the plugin at the new graph.\n"
                + "• Immediately after 🐞 Enable live-debug tracking, so a machine you have just "
                + "made trackable starts reporting without restarting the session.\n"
                + "• The status bar confirms what went out, e.g. Config: 17 vars, 2 SMs — with a "
                + "warning form of the same line when no machine is trackable.\n\n"
                + "Open the file the game is running\n"
                + "The config, the variable names and the state-id lookup are all built from the file open "
                + "in the editor. If the game is running a Nemesis- or Pandora-generated output, open that "
                + "output rather than your pre-patch source: the generated graph can carry different state "
                + "ids and a longer variable table, and both are resolved by position. A mismatch shows up "
                + "as active states named state 12 and variables that never flash.");

            AddSection("debug_reading", "Reading a Live Session",
                "Once snapshots are arriving, the graph and the panel move together. None of this needs "
                + "a click — it is all driven by what the game sends.\n\n"
                + "On the graph\n"
                + "• An active state's node gets a pulsing green outline, a faint green tint across "
                + "its body, and a ● LIVE badge in its bottom-right corner. The pulse redraws about 30 "
                + "times a second, so a state held only briefly still registers.\n"
                + "• When one state is left and another entered in the same snapshot, the edge between "
                + "them flashes green and fades over roughly a second — that is the transition that "
                + "actually fired, which is the quickest way to tell which of several candidate edges the "
                + "game took.\n"
                + "• Auto-follow: if a newly entered state belongs to a machine in the machine "
                + "dropdown other than the one on screen, the graph switches to that machine by itself. "
                + "With 🎯 pan-to-active also on, the viewport then animates to centre the active "
                + "node, so the view chases the actor through the graph hands-free.\n\n"
                + "Active-state names\n"
                + "• The plugin reports a machine name and a numeric state id; the readable name is "
                + "resolved from the graph you have loaded.\n"
                + "• A card reading state 12 with no name means that id is not in the open graph — "
                + "normally the wrong file, or a copy from before the last patch run.\n\n"
                + "The variables list\n"
                + "• Variables appear as the game reports them and stay for the session. A value that "
                + "moves by more than 0.001 flashes green for about 400 ms, which is how you find out "
                + "which variable a key press or an animation event really drives.\n"
                + "• Names the editor considers relevant to the detected actor type are drawn bright "
                + "and the rest dim grey. The dim ones still update, they just do not flash, so the dozen "
                + "variables that matter are not lost among a hundred that don't.\n"
                + "• Values are shown to two decimals, and ints and bools arrive as numbers (1.00 / "
                + "0.00) because Havok keeps them all in one variable table.\n\n"
                + "Actor detection\n"
                + "• The icon and accent colour come from the snapshot's behaviour file name: dragon "
                + "and horse files map to 🐉 and 🐴, and 0_master, defaultmale, "
                + "defaultfemale or mt_behavior to 👤 player.\n"
                + "• Anything else is matched against the file you have open, then split into "
                + "🧍 humanoid NPC or 🐺 creature by whether the graph carries humanoid "
                + "variables such as iRightHandType, iCombatStance or IsSneaking.\n"
                + "• Getting this wrong is cosmetic: it only changes the icon, the accent colour and "
                + "which variable names count as relevant. Everything is still reported.\n\n"
                + "Riding a mount\n"
                + "• While the actor is riding, the snapshot carries a second set of states and "
                + "variables for the mount, shown in the 🐉 group below the actor's variables with "
                + "its own accent colour and the mount's behaviour file as the label.\n"
                + "• The group disappears the moment mount data stops arriving, which is itself a "
                + "useful signal when debugging mounting and dismounting.");

            AddSection("debug_recording", "Recording & Exporting a Session",
                "The live panel only ever shows the present. Recording captures the snapshot stream so "
                + "you can read it back frame by frame — which is how you catch a state that flickers "
                + "past too fast to see, or compare what the game sets against what your graph "
                + "expects.\n\n"
                + "Capturing\n"
                + "• ⏺ starts a recording and clears whatever was captured before, so every take "
                + "is clean.\n"
                + "• Each snapshot is appended to memory as it arrives. Frames that arrive while "
                + "⏸ Pause is on are dropped rather than buffered, so pausing during a recording is a "
                + "cut, not a gap you can scrub back into.\n"
                + "• ⏹ stops the capture and reports the count in the status bar, e.g. ⏹ 412 "
                + "frames captured. The frames stay in memory until the next ⏺, so you can stop first "
                + "and export at leisure.\n"
                + "• Recordings live in memory only, and only for the life of the debugger client: "
                + "⏹ Stop Debug throws them away. Export before you stop.\n\n"
                + "Exporting\n"
                + "• 💾 asks for a path, pre-filled as session_yyyyMMdd_HHmmss.json, and "
                + "writes indented JSON.\n"
                + "• The file is an array with one object per snapshot: timestamp (HH:mm:ss.fff), "
                + "actorName, behaviorFile, activeStates — each with smName, stateId and the resolved "
                + "stateName — and variables as name/value pairs.\n"
                + "• Export writes whatever is in the buffer, so it also works mid-recording without "
                + "interrupting the capture.\n\n"
                + "What it is good for\n"
                + "• Diffing the variable table the game actually drives against the one your "
                + "behaviour file declares — a variable that never changes is usually one nothing "
                + "writes.\n"
                + "• Establishing the order of state entries around a bug, with timestamps you can "
                + "line up against a video capture.\n"
                + "• Attaching evidence to a bug report: the JSON is plain text and names the "
                + "behaviour file, so it says which graph was running.");

            AddSection("debug_tracking", "Why Active States Are Empty",
                "Connecting successfully and still seeing an empty Active States list is the most common " +
                "live-debug question, and it is usually not a broken setup.\n\n" +
                "How active states are read\n" +
                "A state machine does not expose its current state to the game directly. It can only " +
                "mirror it into a behaviour variable — the one named by the machine's syncVariableIndex " +
                "parameter. The editor therefore asks the plugin to watch only those machines whose " +
                "syncVariableIndex is set (0 or higher), and the plugin reports the state by reading " +
                "that variable back. A machine with syncVariableIndex = -1 has no readable state, so it " +
                "can never light up.\n\n" +
                "Most state machines are not synced\n" +
                "This is normal, and it is true of vanilla files too. In vanilla 0_master.hkx only 11 of " +
                "112 state machines are synced (via iSyncSprintState and currentDefaultState). Vanilla " +
                "WeapEquip.hkx has none at all. So a custom behaviour graph with no synced machines shows " +
                "no active states — exactly like the vanilla file it replaces.\n\n" +
                "How to tell\n" +
                "• The status bar reports the config sent to the plugin, e.g. Config: 17 vars, 2 SMs.\n" +
                "• If it reads 0 of N state machines tracked — none have syncVariableIndex set, that is " +
                "the whole diagnosis. Live variables will still update normally; only state highlighting " +
                "is unavailable.\n\n" +
                "Enable tracking for a machine\n" +
                "• In the Graph tab, right-click the state machine node — or right-click empty canvas " +
                "with the machine selected in the machine dropdown — and choose " +
                "🐞 Enable live-debug tracking.\n" +
                "• The editor adds an int variable named i‹MachineName›_State and points that machine's " +
                "syncVariableIndex at it. The machine will now write its current state ID into the " +
                "variable, which is what the debugger reads.\n" +
                "• The change is undoable and is written back on Save. Re-run your Nemesis/Pandora patch " +
                "so the edited graph reaches the game.\n\n" +
                "Nested graphs\n" +
                "If the graph you edited is pulled in by an hkbBehaviorReferenceGenerator (a nested " +
                "behaviour graph, e.g. a custom WeapEquip replacement referenced from 0_master), the " +
                "sync variable most likely also has to exist under the same name in the root graph — " +
                "Havok links a nested graph's variables to the root graph by name. Add a variable with " +
                "the identical name to 0_master as part of your patch, then test in-game.");

            AddSection("tracing_triggers", "Tracing & Editing Triggers",
                "Behaviour files reference events by a numeric id (e.g. #495), which makes a raw " +
                "state-machine trigger hard to follow. The editor resolves these for you and gives you " +
                "a direct path from any trigger to where it is defined and used.\n\n" +
                "Find what a trigger is\n" +
                "• Events are shown by name everywhere — graph edge labels, the Transitions list, and " +
                "the SM Inspector. An id with no name appears as ‹unnamed #N›, never as a bare number.\n" +
                "• Go to event — right-click a transition (in the graph, the Transitions list, or the " +
                "SM Inspector) and choose Go to event. You land on the Events tab with that event " +
                "selected and its full usage list shown: every transition, wildcard, clip trigger, and " +
                "property that references it.\n\n" +
                "Find a high-priority / random trigger\n" +
                "• \"Random\" or high-priority behaviours (a creature breathing fire, entering a death " +
                "state, etc.) are usually wildcard transitions that fire from any state. Open the Graph " +
                "tab and look for the amber ★ ANY node — its dashed edges are exactly those triggers. " +
                "You can also read them at the bottom of the SM Inspector list (★ WILDCARD).\n\n" +
                "Add a high-priority / random trigger\n" +
                "• Pick ★ WILDCARD (any state) as the From State in + Add Transition, or right-click the " +
                "★ ANY node in the graph. See Creating a Wildcard Transition.\n\n" +
                "Turn a trigger off\n" +
                "• Right-click the transition (graph edge or SM Inspector row) → Disable transition. " +
                "This sets the Havok FLAG_DISABLED flag so it never fires, without deleting it — a " +
                "dimmed/⊘ marker shows it is off, and Enable transition restores it. Every toggle is " +
                "undoable and is written back on save.");

            AddSection("patch_export", "Exporting Patches",
                "Generate a Nemesis or Pandora compatible patch from your edits.\n\n" +
                "1. Make your edits to the loaded behavior file.\n" +
                "2. Click the 📦 Patch button in the toolbar.\n" +
                "3. The Patch Preview dialog shows every changed object.\n" +
                "4. Click Export Nemesis or Export Pandora and choose an output folder.\n" +
                "5. The exporter writes one #XXXX.txt per changed object with ORIGINAL/NEW markers.\n\n" +
                "The snapshot used for diffing is taken when the file is first loaded. " +
                "Reloading the file resets the snapshot baseline.");

            AddSection("patch_apply", "Applying Patches",
                "Apply a Nemesis/Pandora patch folder or a native .behaviorpatch file.\n\n" +
                "• Click 🔧 Apply Patch in the toolbar.\n" +
                "• Browse to a .behaviorpatch file or navigate into a Nemesis/Pandora mod folder.\n" +
                "• The preview shows every operation with checkboxes — uncheck any you want to skip.\n" +
                "• Click Apply to commit. The UI refreshes automatically.");

            AddSection("global_search", "Global Search",
                "Press Ctrl+G or click 🔭 Search All to open the Global Search dialog. This is the " +
                "fastest way to find anything in a file — use it instead of scrolling a tab by hand. " +
                "The per-tab filter boxes also hint at it (Ctrl+G: search everything).\n\n" +
                "• Searches across all objects, states, variables, events, and clips at once.\n" +
                "• Type a prefix to scope the search: event:  state:  clip:  var:  trans:  obj: " +
                "(e.g. event:attack finds only events matching \"attack\"). Filter chips do the same.\n" +
                "• Click or press ↵ on a result to jump to it in its tab; double-click to navigate.\n" +
                "• Case and Regex toggles refine matching; the ± Replace panel can edit matched values.\n" +
                "• The search is case-insensitive by default and matches partial names.");

            AddSection("event_xref", "Event Cross-Reference",
                "Click 🔗 Event Xref in the toolbar for a whole-file view of the event table: every " +
                "event, how many places listen to it, how many send it, and what those places are. " +
                "The Events tab answers the same question one event at a time; this answers it for " +
                "all of them at once.\n\n" +
                "• The left list is every event in hkbBehaviorGraphStringData.eventNames, with its id " +
                "and a listen/send count. The filter box narrows by name or id.\n" +
                "• Selecting an event lists its references on the right, each tagged ◀ listens or " +
                "▶ sends: state and wildcard transitions, the enter/exit ids inside a transition's " +
                "trigger and initiate intervals, a state machine's returnToPrevious / random / " +
                "next-higher / next-lower state ids, event-driven modifiers, state enter/exit notify " +
                "events, clip annotation triggers, and eventToSend fields.\n" +
                "• Double-click a reference to select that object in Object Data and the behaviour tree.\n" +
                "• 📋 Copy report puts the selected event's full cross-reference on the clipboard as " +
                "plain text — handy for a bug report or a patch write-up.\n\n" +
                "Unreferenced only\n" +
                "The checkbox filters to events that nothing in this file references — the dead entries " +
                "a hand-edited or tool-extended event table accumulates. Treat those as leads, not as a " +
                "verdict: annotation events (HitFrame, SoundPlay.*, the spell-fire events dragons use) " +
                "are emitted from annotation tracks inside the animation .hkx files, and cross-behaviour " +
                "events are matched by name in another file's table. Neither is visible from here, so " +
                "an unreferenced event is never safe to delete on that basis alone.");

            AddSection("compare", "Compare Files",
                "Click ⇄ Compare to open two behavior files side-by-side.\n\n" +
                "• File A is the currently loaded file.\n" +
                "• Browse to File B in the dialog.\n" +
                "• Differences are highlighted: added objects in green, removed in red, changed in amber.\n" +
                "• Click any diffed object to inspect it in the Object Data panel.");

            AddSection("validate", "Validation",
                "Click 🔎 Validate to run the built-in validator. What it checks:\n\n" +
                "• Broken references — every #id in every param, including refs nested inside " +
                "array elements (a transition's blend effect, the root container's variants).\n" +
                "• Orphaned objects — nothing references them, so the .hkx save will drop them.\n" +
                "• Values that don't match their declared Havok type (the red-bordered fields in " +
                "Object Data). These also block saving as HKX.\n" +
                "• startStateId that doesn't match any state's stateId — the machine has no valid " +
                "start state and silently T-poses when activated.\n" +
                "• eventNames/eventInfos and variableNames/variableInfos count mismatches — the " +
                "game pairs these arrays by position.\n" +
                "• Duplicate stateIds in a machine, transitions whose toStateId doesn't exist, " +
                "machines with no states, clips with no animation path, variable name/value " +
                "count mismatches.\n\n" +
                "Each issue shows the severity, the affected object, and a description. " +
                "Click an issue row to jump to the offending object.");

            AddSection("le_se", "Skyrim LE ⇄ SE Conversion",
                "The editor reads and writes both Skyrim editions' .hkx binaries. LE (Legendary " +
                "Edition / Oldrim) and SE use the same Havok schema and differ only in pointer size — " +
                "32-bit against 64-bit — so converting between them is a pure repack: the behaviour " +
                "graph is preserved exactly, with no XML round-trip on disk and no external converter.\n\n" +
                "Which edition am I looking at?\n" +
                "The status bar shows the edition the loaded file came from, next to the file name " +
                "(blank for Havok XML, which has no pointer size). Save offers both editions in its " +
                "file-type dropdown with the source's edition listed first, so accepting the default " +
                "writes back the edition you opened.\n\n" +
                "Converting files\n" +
                "• Click 🔄 LE ⇄ SE and choose a single .hkx (multi-select works) or a whole folder, " +
                "which is searched recursively.\n" +
                "• The prompt reports how many LE and SE files were found and asks which edition to " +
                "convert to, defaulting to the opposite of what the selection mostly contains.\n" +
                "• Originals are never modified. Results are written to a folder beside the source, " +
                "named after it — Behaviors becomes Behaviors_LE — keeping the sub-folder structure. " +
                "Beside rather than inside, so converting the same folder again doesn't walk the " +
                "previous output.\n" +
                "• Files already in the target edition are copied across untouched rather than " +
                "skipped, so the output folder is a complete, drop-in copy of the source even when " +
                "the source is a mix of both editions. Only .hkx files are picked up — loose .txt, " +
                ".xml or mesh files sitting in the folder are not copied.\n\n" +
                "Limits\n" +
                "Nineteen Havok classes still have no 32-bit layout — hkp* physics and ragdoll classes, " +
                "plus a few type-metadata ones that never appear in a serialised file. None of them " +
                "occur in behaviour, character, project, skeleton or animation files. If a file does " +
                "contain one, writing it as LE is refused with the class names listed, rather than " +
                "producing a silently corrupt file.");
        }

        private void AddNavHeader(string text)
        {
            var hasResource = TryFindResource("AccentBlueBrush") != null;
            NavPanel.Children.Add(new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = hasResource
                    ? (Brush)FindResource("AccentBlueBrush")
                    : new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                Margin = new Thickness(12, 14, 8, 4)
            });
        }

        private void AddSection(string key, string title, string body)
        {
            Brush primaryBrush = TryFindResource("TextPrimaryBrush") is Brush pr
                ? pr : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
            Brush secondaryBrush = TryFindResource("TextSecondaryBrush") is Brush se
                ? se : new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9D));
            Brush borderBrush = TryFindResource("BorderBrush") is Brush bo
                ? bo : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x55));

            // Nav button (sidebar — not part of the selectable document)
            var navBtn = new Button
            {
                Content = title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 11,
                Padding = new Thickness(12, 4, 8, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = primaryBrush,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            navBtn.Click += (_, __) => ScrollToSection(key);
            NavPanel.Children.Add(navBtn);

            // Heading paragraph — doubles as the scroll anchor
            var heading = new Paragraph(new Run(title))
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryBrush,
                Margin = new Thickness(0, 16, 0, 6)
            };
            _anchors[key] = heading;
            _docBox.Document.Blocks.Add(heading);

            // Rule
            _docBox.Document.Blocks.Add(new BlockUIContainer(new Border
            {
                Height = 1,
                Background = borderBrush,
                Margin = new Thickness(0, 0, 0, 8)
            }));

            // Body paragraphs
            foreach (var para in body.Split("\n\n"))
            {
                var pg = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 20, FontSize = 13 };
                bool first = true;
                foreach (var line in para.Split('\n'))
                {
                    if (!first) pg.Inlines.Add(new LineBreak());
                    first = false;
                    bool isBullet = line.StartsWith("•");
                    bool isSub = !isBullet && !char.IsDigit(line.FirstOrDefault()) &&
                                 para.Contains("•") && line.Length > 0;
                    pg.Inlines.Add(new Run(isBullet ? "    " + line : line)
                    {
                        FontWeight = isSub ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = isSub ? primaryBrush : secondaryBrush
                    });
                }
                _docBox.Document.Blocks.Add(pg);
            }
        }
    }
}
