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
            if (_anchors.TryGetValue(key, out var block))
                block.BringIntoView();
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
                "1. Open a file — use Load or drag a .hkx/.xml onto the window.\n" +
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
                "Live debugging\n" +
                "• Active states glow with an animated green outline.\n" +
                "• When a transition fires, its edge pulses green so you can trace the flow as it happens.\n\n" +
                "Right-click context menus are available on nodes, edges, and empty canvas space " +
                "for additional actions including Add State, Add State Machine, and Re-layout.");

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
                "• The usages panel at the bottom shows every transition, trigger, and property referencing the selected event.\n" +
                "• + Add Event / Delete work the same as the Variables equivalents.");

            AddSection("tab_transitions", "Transitions Tab",
                "A flat list of every hkbStateMachineTransitionInfoArray entry in the file.\n\n" +
                "• Columns: From state, To state, Event, Blend duration.\n" +
                "• Click a row to see full transition details including conditions and trigger intervals.\n" +
                "• Filter box narrows the list by state or event name.");

            AddSection("tab_clips", "Clips Tab",
                "Lists every hkbClipGenerator in the file.\n\n" +
                "• Shows the clip name and the animation file it references.\n" +
                "• Inline editing lets you change the animation path directly or browse with the folder button.\n" +
                "• The trigger panel at the bottom shows all timed events attached to the selected clip.");

            AddSection("tab_sm_inspector", "SM Inspector Tab",
                "A full transition editor for a single hkbStateMachine.\n\n" +
                "• Select a state machine from the dropdown to load all its transitions.\n" +
                "• + Add Transition — opens a dialog to pick source state, target state, event, and flags.\n" +
                "• Edit and Delete buttons act on the selected row.\n" +
                "• Wildcard transitions (★) are shown at the bottom of the list.");

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
                "The Live Debugger connects to a running Skyrim process via a named pipe " +
                "(requires the SkyrimBehaviorDebugger SKSE plugin).\n\n" +
                "• Click Live Debug in the toolbar to start listening.\n" +
                "• Once connected the panel shows Active States, Transition History, and live variable values.\n" +
                "• ⏸ Pause — freeze the UI without disconnecting.\n" +
                "• ⏺ Record — capture all snapshots to memory.\n" +
                "• 💾 Export — save the recorded session to JSON.\n" +
                "• 🎯 Pan-to-active — keep the graph viewport centred on the current state.\n" +
                "• ⧉ Pop Out — detach the debugger panel into a floating window.\n" +
                "• The graph highlights the active state with an animated green glow.");

            AddSection("tab_bookmarks", "Bookmarks Tab",
                "Stores named references to Havok objects for quick navigation.\n\n" +
                "• Click the 🔖 bookmark icon in the Object Data header to bookmark the current object.\n" +
                "• Click a bookmark row to jump straight to that object and open it in Object Data.\n" +
                "• ✕ removes a bookmark. Bookmarks persist between sessions via AppData.");

            AddNavHeader("Advanced");

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

            AddSection("theme", "Themes",
                "Toggle between Dark and Light themes using the ☀ Light / 🌙 Dark button in the toolbar.\n\n" +
                "All UI elements use DynamicResource brushes so the switch is instant. " +
                "Graph node colours are fixed and do not change with the theme.");

            AddSection("global_search", "Global Search",
                "Press Ctrl+G or click 🔭 Search to open the Global Search dialog.\n\n" +
                "• Search across all objects, variables, events, and clips simultaneously.\n" +
                "• Click a result to jump to that object, variable, or event in its respective tab.\n" +
                "• The search is case-insensitive and matches partial names.");

            AddSection("compare", "Compare Files",
                "Click ⇄ Compare to open two behavior files side-by-side.\n\n" +
                "• File A is the currently loaded file.\n" +
                "• Browse to File B in the dialog.\n" +
                "• Differences are highlighted: added objects in green, removed in red, changed in amber.\n" +
                "• Click any diffed object to inspect it in the Object Data panel.");

            AddSection("validate", "Validation",
                "Click 🔎 Validate to run the built-in validator.\n\n" +
                "• Checks for broken object references, missing required parameters, and common modding mistakes.\n" +
                "• Each issue shows the severity (Error / Warning / Info), the affected object, and a description.\n" +
                "• Click an issue row to jump to the offending object.");
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
