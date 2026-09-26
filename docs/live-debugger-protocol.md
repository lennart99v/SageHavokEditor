# Live debugger — the SKSE plugin and its wire protocol

The Debugger tab talks to an SKSE plugin called **SkyrimBehaviorDebugger** over a
pair of local named pipes. This document describes both halves as built.

## Status

The plugin **ships in the release zip from 0.8.0**, under
`SKSE Plugin/SKSE/Plugins/`. It is still **not in this repository and not in
version control anywhere** — the release process reaches for a path on one
machine, which is the remaining soft spot and is tracked in `ROADMAP.md`. Nothing
in this repo builds or tests it, so a change to it lands here as documentation
only.

- Source: `C:\SkyrimBehaviorDebugger\` — CMake + vcpkg + CommonLibSSE-NG,
  one translation unit (`src/Plugin.cpp`, ~500 lines) plus a PCH and
  `src/PipeServer.h`. Version 1.1.0; the version matters, because the editor
  asks it for things older builds cannot do.
- Built artefact: `SkyrimBehaviorDebugger.dll`, installed to
  `Data/SKSE/Plugins/`. Writes `SkyrimBehaviorDebugger.log` to the usual SKSE log
  directory.
- Dependencies are `commonlibsse-ng` and `spdlog` (header-only), both via vcpkg;
  C++23, static MSVC runtime. **The generator in `CMakePresets.json` has to name
  the same MSVC toolset vcpkg builds CommonLibSSE with** — both link the static
  CRT, and the STL's `__std_*` helpers are not stable across versions, so a
  mismatch is an unresolved symbol across dozens of CommonLibSSE objects rather
  than anything that reads as a version problem. `RELEASING.md` step 5b has the
  re-point recipe.

Getting it published is tracked in `ROADMAP.md` under *Live debugger*.

## Shape

Two local named pipes. **The plugin is the server on both**; the editor connects
as a client and retries until the game appears. No network transport — both halves
must be on the same machine.

| Pipe | Direction | Purpose |
| --- | --- | --- |
| `\\.\pipe\SkyrimBehaviorDebugger` | game → editor | one JSON snapshot per line, every 500 ms |
| `\\.\pipe\SkyrimBehaviorDebugger_Config` | editor → game | one JSON watch-list, on demand |

The editor never writes to the first and never reads from the second.

Three detached threads do the work: a pipe server, a config listener, and a
snapshot sender that starts at `kDataLoaded`.

## `SkyrimBehaviorDebugger` — snapshots

`PIPE_ACCESS_OUTBOUND`, byte mode, `PIPE_NOWAIT`, one instance, 64 KB out buffer.
The sender builds a snapshot and writes it plus `\n` every **500 ms** — a
deliberate 2 Hz, not a per-frame firehose. The editor imposes no rate limit and
handles whatever arrives; it skips blank lines and silently discards any line it
cannot parse, so a malformed snapshot costs one frame, not the session.

A real line, formatted here for reading — the plugin emits it on one line with no
spaces:

```json
{
  "formId": "00000014",
  "actorName": "Player",
  "source": "player",
  "behaviorFile": "0_master",
  "activeStates": [
    { "smName": "MTState", "stateId": 12, "stateName": "" }
  ],
  "variables": [
    { "name": "SpeedSampled", "value": 0.000000 },
    { "name": "bIsRiding",    "value": 0 }
  ]
}
```

- **`formId`** — eight uppercase hex digits, **no `0x` prefix** (`00000014` for
  the player). Carried by the editor but not currently displayed.
- **`actorName`** — the subject's `GetDisplayFullName()`, so a real name: the
  character's own name for the player, `Wolf` for a wolf. `(unnamed)` when the
  form has no name, and `(no target)` for the empty snapshot described under
  *Which actor* below. It was the hardcoded literal `"Player"` before 1.1.0.
- **`source`** — how the subject was resolved: `player`, `crosshair`, `console`,
  `held`, or `none`. **Absent entirely from a plugin older than 1.1.0**, and the
  editor leans on that: a missing `source` while it is asking for a target is how
  it knows the game side is too old to honour the request.
- **`behaviorFile`** — read from `graphs[0]->behaviorGraph->name`, so it is the
  **graph's name**, not a path or a filename with an extension. Empty string if
  the actor has no animation graph yet.
- **`activeStates`** — one entry per tracked state machine. `stateName` is
  **always sent empty**; the editor resolves the readable name itself, positionally,
  against the file the user has open, and shows `state 12` when it cannot. A
  machine whose state variable is missing or reads negative is **omitted
  entirely** rather than reported as unknown.
- **`variables`** — the configured watch list. A variable the running graph does
  not have is **skipped**, not sent as zero. Floats are printed with six decimals
  (`0.000000`); ints are printed bare (`12`). Never a JSON `true`/`false` — the
  editor reads every value as a float and flashes a variable whose value moved by
  more than `0.001`.
- **`bIsRiding`** — appended to `variables` unconditionally, whether or not the
  editor asked for it, so the mount group can appear.
- **`dragon`** — present only while riding, and the key is **absent** rather than
  `null` otherwise. Same `formId` / `behaviorFile` / `activeStates` / `variables`
  shape, read off `GetMount()` with the same watch list. The name is historical;
  the panel labels it the mount group and a horse belongs there too.

## `SkyrimBehaviorDebugger_Config` — the watch list

`PIPE_ACCESS_INBOUND`, byte mode, `PIPE_WAIT`, 64 KB in buffer. The listener
loops on accept, reads to EOF, parses, and goes back to waiting — so every config
send is a fresh connection, which is exactly what the editor does: connect, write
**one UTF-8 JSON object with no trailing newline**, flush, disconnect. It does not
wait for a reply.

```json
{
  "variables": [
    { "name": "SpeedSampled", "type": "float" },
    { "name": "iState_MT",    "type": "int"   }
  ],
  "stateMachines": [
    { "variableName": "iState_MT", "smName": "MTState" }
  ],
  "actorSource": "target"
}
```

- **`variables`** — everything the editor wants back, built from the variable
  table of the file it has open. `type` is `"float"` for `REAL`, `VECTOR` and
  `QUATERNION` variables and `"int"` for everything else; it selects
  `GetGraphVariableFloat` or `GetGraphVariableInt`.
- **`stateMachines`** — each machine's name plus the behaviour variable to read
  its state from. See below.
- **`actorSource`** — `"player"` (the default, and what is assumed when the key is
  absent) or `"target"`. A flat scalar on purpose: the array scanning below stops
  at the first `]` after its own key, so a scalar beside the arrays cannot disturb
  it, and an older plugin ignores it and carries on reporting the player.

The editor re-sends the whole config — never a delta — on start, on every
reconnect, whenever a different file is loaded while debugging, and immediately
after the user enables tracking for a machine.

**The parser is hand-rolled substring scanning, not a JSON parser**, and that
constrains the format more than the format admits:

- It takes the first `[` after a key and the **first `]` after that**, so an array
  containing a nested array or any bracketed string would truncate. The config
  must stay flat.
- Within an entry, `"type"` must follow `"name"`, and `"smName"` must follow
  `"variableName"`. Key order is load-bearing.
- Anything it cannot find is silently absent rather than an error.

This matches what the editor emits today. It is worth knowing before either side
changes shape.

## Which actor a snapshot is about

Before 1.1.0 the answer was always the player. `BuildSnapshot` began with
`RE::PlayerCharacter::GetSingleton()` and read every value through that pointer;
nothing in the plugin enumerated the cell, the crosshair or the follower list, so
no way of spawning an actor could put one in the panel. The only non-player it
could report was the player's own mount.

It now resolves a **subject** first, in this order:

1. `CrosshairPickData::targetActor`, then its `target` when that is an actor —
   pointing at something is the obvious gesture, and the pick data clears the
   moment you look away.
2. `Console::GetSelectedRef()`, for what you cannot put a crosshair on: a
   reference clicked with the console open stays selected while you walk around
   it.
3. The last actor resolved by either, **held**. Without this, turning your head
   or alt-tabbing to read the editor would drop the subject on the frame you
   wanted to read it. The hold is an `ActorHandle`, re-resolved each snapshot and
   released once it no longer resolves to a loaded actor.

Each candidate must be an `Actor` with `Is3DLoaded()` — an actor with no 3D has
no animation graph, and asking one for a graph variable off the sender thread is
not how you want to find that out. The resolved actor is held as a
`NiPointer<Actor>` for the duration of the snapshot so it cannot be freed
mid-read.

With `actorSource` of `target` and nothing resolved, the plugin sends a snapshot
with `"actorName": "(no target)"`, `"source": "none"`, an empty `behaviorFile`
and empty arrays — deliberately, rather than falling back to the player. The
editor highlights the graph of whatever it is sent, so a silent substitution
would light up the wrong file.

The mount group still rides on the subject: `bIsRiding` and `GetMount()` are read
from the resolved actor, not from the player, so a mounted NPC reports its horse.

## How active states are read, and why it is the weak point

A `hkbStateMachine` does not publish its current state. It can mirror it into a
behaviour variable, named by the machine's `syncVariableIndex`, and that mirror is
what the protocol is built on: the editor says *"machine `MTState` reports through
variable `iState_MT`"*, and the plugin answers with `GetGraphVariableInt`.

That needs nothing but the ordinary variable getter, which is the appeal. The cost
is that **almost nothing in Skyrim is synced** — 11 of 112 state machines in
vanilla `0_master.hkx`, none at all in `WeapEquip.hkx`. The editor can add an int
variable and point a machine's `syncVariableIndex` at it, but that edits the graph,
which means re-running Nemesis or Pandora before the game sees it. It is the
feature's worst wart and it is a property of this design, not of the game.

**If the plugin can reach the live graph instead, it should.** It already holds
`BSAnimationGraphManagerPtr` in `GetBehaviorFileName`; reading a state machine's
active state from there would report every machine with no `syncVariableIndex`, no
graph edit and no patch run. The editor needs no change to accept it — it keys
active states on `smName` and resolves the id against the open file either way,
so `stateMachines` in the config would simply become advisory.

**Fallback when no config has arrived.** With an empty machine list the plugin
reads a variable literally named `iState` and reports it as a machine called
`BehaviorMode`. That is a development convenience from before the config pipe
existed; it fires whenever the editor has not sent a config yet, and it will
produce a machine name that exists in no real graph.

## Known limitations

Worth fixing before the plugin is published, and worth knowing meanwhile.

1. **Fixed 2 Hz.** Fine for variables, coarse for catching a state held briefly.
   The graph's live pulse redraws about 30 times a second, so there is headroom;
   sending on change would be better than simply raising the rate.
2. **The snapshot thread reads game state off the main thread.** It always has —
   `GetGraphVariableInt` on a detached thread predates target following — and
   resolving handles and reading `GetDisplayFullName()` there is more of the same.
   Holding a `NiPointer` keeps the actor alive across a snapshot, which is the
   part that matters, but a task queued onto the main thread would be the correct
   shape.

Fixed since this document was first written: the pipe served one editor session
per game launch (the accept loop parked and never saw the disconnect, because
`Send` discarded the `WriteFile` result — both addressed, with
`test/pipe_reconnect_test.cpp` covering the cycle), and the plugin reported the
player and nothing else (see *Which actor a snapshot is about*).
