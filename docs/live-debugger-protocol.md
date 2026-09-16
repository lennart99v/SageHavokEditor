# Live debugger — the SKSE plugin and its wire protocol

The Debugger tab talks to an SKSE plugin called **SkyrimBehaviorDebugger** over a
pair of local named pipes. This document describes both halves as built.

## Status

The plugin **exists and works**. It is not in this repository, not in version
control anywhere, and has never been released — so anybody who reads the Guide,
goes looking for it on Nexus and finds nothing is not doing anything wrong. Until
it is published, the Debugger tab is effectively author-only.

- Source: `C:\SkyrimBehaviorDebugger\` — CMake + vcpkg + CommonLibSSE-NG,
  one translation unit (`src/Plugin.cpp`, ~470 lines) plus a PCH and a one-symbol
  `RegexStub.cpp` that satisfies a CommonLibSSE built against an older MSVC STL.
- Built artefact: `SkyrimBehaviorDebugger.dll`, installed to
  `Data/SKSE/Plugins/`. Writes `SkyrimBehaviorDebugger.log` to the usual SKSE log
  directory.
- Dependencies are `commonlibsse-ng` and `spdlog` (header-only), both via vcpkg;
  C++23, static MSVC runtime.

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
- **`actorName`** — hardcoded `"Player"`. The plugin only ever reports the player
  (plus a mount, below); it does not look at targets or followers.
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
  ]
}
```

- **`variables`** — everything the editor wants back, built from the variable
  table of the file it has open. `type` is `"float"` for `REAL`, `VECTOR` and
  `QUATERNION` variables and `"int"` for everything else; it selects
  `GetGraphVariableFloat` or `GetGraphVariableInt`.
- **`stateMachines`** — each machine's name plus the behaviour variable to read
  its state from. See below.

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

1. **One editor session per game launch.** After a client connects, the pipe
   thread parks in `while (_running) Sleep(100)` and never notices the
   disconnect, so the pipe is never torn down and recreated. Stop Debug and start
   again and the editor will wait forever against a game that thinks it still has
   a client. The editor's own reconnect loop is fine; the plugin is the half that
   cannot. Restarting Skyrim is the current workaround.
2. **Write failures are ignored.** `Send` discards the `WriteFile` result, so a
   broken pipe is indistinguishable from a successful send and the plugin keeps
   building snapshots for a reader that has gone. Checking it is also how the
   thread would learn to recreate the pipe and fix (1).
3. **Player only.** Reporting a targeted actor would be more useful for debugging
   a creature or a follower, and costs the protocol nothing — but the editor's
   panel would need an actor picker.
4. **Fixed 2 Hz.** Fine for variables, coarse for catching a state held briefly.
   The graph's live pulse redraws about 30 times a second, so there is headroom;
   sending on change would be better than simply raising the rate.
