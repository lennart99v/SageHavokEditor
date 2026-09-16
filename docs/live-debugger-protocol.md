# Live debugger — wire protocol

Sage Havok Editor ships the **client** half of a live behaviour debugger: a panel
that shows which states a running actor's graph is in, which transition just
fired, and what every behaviour variable is worth, lined up against the file open
in the editor. The **game** half — an SKSE plugin — does not exist. This document
is the contract it would have to satisfy, written out of the shipping client
(`SageHavokEditor/Core/BehaviorDebuggerClient.cs`) so that anyone who wants to
write that plugin does not have to read C# to find out what the editor expects.

Nothing here is fixed except by the accident that only one side is implemented.
If you are writing the plugin and a decision below is wrong for the game, say so —
the client is 200 lines and changing it is easier than working around it.

## Shape

Two local named pipes. **The plugin is the server on both**; the editor connects
as a client and retries until the game appears. There is no network transport and
both halves must be on the same machine.

| Pipe | Direction | Purpose |
| --- | --- | --- |
| `\\.\pipe\SkyrimBehaviorDebugger` | game → editor | one JSON snapshot per line, continuously |
| `\\.\pipe\SkyrimBehaviorDebugger_Config` | editor → game | one JSON watch-list, on demand |

The editor never writes to the first and never reads from the second.

## `SkyrimBehaviorDebugger` — snapshots

A byte stream of UTF-8 **newline-delimited JSON**: one complete object per line,
`\n`-terminated. The editor reads with a line reader, skips blank lines, and
silently discards any line it cannot parse — so a malformed snapshot costs one
frame, not the session.

```json
{
  "formId": "0x00000014",
  "actorName": "Player",
  "behaviorFile": "0_master.hkx",
  "activeStates": [
    { "smName": "MTState", "stateId": 12, "stateName": "" }
  ],
  "variables": [
    { "name": "SpeedSampled", "value": 0.0 },
    { "name": "bIsSynced",    "value": 1.0 }
  ],
  "dragon": null
}
```

Field by field:

- **`formId`**, **`actorName`** — identify the actor. `actorName` is displayed in
  the panel header and written into exported recordings; `formId` is carried
  through but not currently displayed.
- **`behaviorFile`** — the graph the game is running, displayed so the user can
  tell they have the wrong file open. The bare filename is enough.
- **`activeStates`** — one entry per *watched* state machine (see the config
  below), not per machine in the graph. `stateId` is the number; **`stateName`
  may be sent empty** — the editor resolves the readable name itself from the
  file the user has open, and shows `state 12` when it cannot. Sending a name is
  harmless but it is not used.
- **`variables`** — the watched variables and their current values. **`value` is
  always a JSON number read as a float**, including for integer and boolean
  variables: send `1.0` for a true bool, not `true`. The editor flashes a
  variable whose value moved by more than `0.001`.
- **`dragon`** — optional, `null` when absent. The mount's graph while riding,
  with the same `formId` / `behaviorFile` / `activeStates` / `variables` shape.
  The name is historical and the panel labels it the mount group; it is not
  dragon-specific and a horse belongs here too.

Omitted fields deserialize to empty, so a minimal plugin can send `actorName`,
`behaviorFile` and `variables` and get a working variable view with no active
states.

**Cadence is unspecified and is the plugin's call.** The editor imposes no rate
limit and handles whatever arrives; the graph's live-state pulse redraws about 30
times a second, so anything at or below that is smooth and anything much above it
is wasted. A snapshot per behaviour-graph update is more than is needed. Sending
only on change is fine — the editor holds the last value.

## `SkyrimBehaviorDebugger_Config` — the watch list

The editor connects, writes **one UTF-8 JSON object with no trailing newline**,
flushes, and **disconnects**. It does not wait for a reply. Every config send is a
fresh connection, so the plugin must loop on accept rather than handle one client
and stop.

```json
{
  "variables": [
    { "name": "SpeedSampled", "type": "float" },
    { "name": "iState_MT",    "type": "int"   }
  ],
  "stateMachines": [
    { "variableName": "iState_MT", "smName": "MTState" }
  ]
}
```

- **`variables`** — everything the editor wants back in each snapshot, built from
  the variable table of the file it has open. `type` is `"float"` for `REAL`,
  `VECTOR` and `QUATERNION` variables and `"int"` for everything else; it tells
  the plugin which getter to call, and the answer still comes back as a number.
- **`stateMachines`** — the machines that can report a state, each as its name
  plus **the behaviour variable to read that state from**. See below.

The editor re-sends the whole config — it is never a delta — on start, on every
reconnect, whenever the user loads a different file while debugging, and
immediately after the user enables tracking for a machine.

## How active states are read today, and why you might not want to

A `hkbStateMachine` does not publish its current state to the game. It can mirror
it into a behaviour variable, named by the machine's `syncVariableIndex`
parameter, and that mirror is what the protocol above is built around: the editor
tells the plugin *"machine `MTState` reports through variable `iState_MT`"*, and
the plugin answers by calling the ordinary int-variable getter.

That works with nothing but `GetGraphVariableInt`, which is the appeal. The cost
is that **almost nothing in Skyrim is synced**: 11 of 112 state machines in
vanilla `0_master.hkx`, and none at all in `WeapEquip.hkx`. The editor has a
command that adds an int variable and points a machine's `syncVariableIndex` at
it, but that edits the graph, which means re-running Nemesis or Pandora before
the game sees it. It is the single biggest wart in the feature.

**If the plugin can reach the live graph instead, it should.** A plugin that
already holds the behaviour graph can read a state machine's active state
directly and report every machine in it, with no `syncVariableIndex`, no graph
edit and no patch run. In that case `stateMachines` in the config becomes
advisory — a filter, or ignorable — and `activeStates` simply carries everything.
The editor needs no change for this: it keys active states on `smName` and
resolves the id against the open file either way.

Treat the sync-variable path as the floor, not the design.

## Notes for an implementer

- The client connects with a 2 s timeout on the snapshot pipe and 3 s on the
  config pipe, and retries about once a second forever. Starting the game first
  or the editor first both work.
- Both pipes should be created once at plugin load and torn down at shutdown. The
  editor treats a dropped pipe as a reconnect, not an error.
- Variable and state-machine names are the graph's own names, matched as written.
  The editor resolves state ids **by position against the file the user has
  open**, which is why it tells users to open the Nemesis/Pandora output rather
  than their pre-patch source.
- `GetGraphVariableFloat` / `GetGraphVariableInt` on the actor are enough for the
  `variables` array and for the sync-variable readback.

## Open questions

Worth settling with whoever writes the plugin rather than guessing:

1. **Which actor?** The client assumes one — in practice the player — plus an
   optional mount. Reporting an arbitrary targeted actor would be more useful and
   costs the protocol nothing, but the editor's panel would need a picker.
2. **Cadence and throttling** — per graph update, fixed Hz, or on-change only.
3. **Direct state access** — the section above; the answer decides whether
   `syncVariableIndex` stays in the feature at all.
4. **Whether `dragon` should be a general `mounts` / `others` array** rather than
   one optional slot with a misleading name. Changing it is a client change, and
   the client is the easy half to change.
