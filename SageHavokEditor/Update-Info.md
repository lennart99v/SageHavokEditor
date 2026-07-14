0.5.0 Features:

- added a warning when a loaded graph yields 0 syncable SMs, something like "No state machines in this graph have syncVariableIndex set — active-state tracking is unavailable. Live variables will still update."
- added editor action that wires a sync variable onto a selected state machine (create the int variable, set syncVariableIndex) so users can make their own graphs debuggable
- Graph tab → right-click a state → "🎬 New clip generator…" (the safe path). Creates the clip and points that state's generator at it in one action, so it can never be orphaned. If the state already has a generator it asks first, and warns that the old one may itself be dropped if nothing else references it. Fully undoable.
- Clips tab → "+ New Clip Generator" (what he literally asked for). Still there, but it now warns explicitly that the clip is unreferenced and will be dropped on .hkx save until wired, and points at the graph action.

