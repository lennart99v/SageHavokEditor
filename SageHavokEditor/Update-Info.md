0.5.0 Features:

- added a warning when a loaded graph yields 0 syncable SMs, something like "No state machines in this graph have syncVariableIndex set — active-state tracking is unavailable. Live variables will still update."
- added editor action that wires a sync variable onto a selected state machine (create the int variable, set syncVariableIndex) so users can make their own graphs debuggable

