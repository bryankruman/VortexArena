# Recon T56 — Generic + client + reply command parity (+ engine `defer`)

READ-ONLY recon. Faithful port spec mapping the ORIGINAL QuakeC/Darkplaces command families onto the
current XonoticGodot port, with exact seams, line refs, cvar defaults, a step plan, a test plan, and conflicts.

Scope recap:
- `common/command/generic.qc` + `rpn.qc` — the GENERIC_COMMAND family (rpn / maplist / addtolist /
  removefromlist / qc_curl / dumpcommands / restartnotifs / nextframe / runtest / settemp / settemp_restore)
  and the RPN calculator VM.
- `server/command/cmd.qc` — ClientCommand_voice / suggestmap / autoswitch / physics / clientversion.
- `server/command/{common.qc,getreplies.qc}` — CommonCommand records/rankings/lsmaps/printmaplist/ladder/
  cvar_purechanges / editmob(+spawn/kill); the getreplies precompute.
- engine `defer <s> <cmd>` / `defer clear` — the sim-clock command queue a passed `restart` vote needs.

---

## 0. ARCHITECTURE OF THE PORT'S COMMAND BUS (how verbs register/dispatch)

There are **two** command surfaces in the port, NOT the QC three (menu/client/server). Confirmed:

1. **`XonoticGodot.Server/Commands.cs`** (`Commands` class) — the SERVER command bus.
   - `Dictionary<string, ConsoleCommand>` (`_commands`, case-insensitive), populated once in
     `RegisterBuiltins()` (Commands.cs:200-307). Registration is a **manual table** via
     `Register(name, help, Func<CommandContext,bool>)` — NOT reflection/attributes. (Contrast the
     A1 lesson: gametypes/mutators auto-register by `[GameType]`/`[Mutator]`; commands do **not**.)
   - `Execute(commandLine, isServerConsole, caller)` (Commands.cs:143-160) tokenizes and dispatches.
   - This single table holds BOTH QC "SERVER_COMMAND" verbs (restart/kick/map…) AND the QC
     "CLIENT_COMMAND" verbs (ready/join/spectate/selectteam/kill/minigame…) AND a few generic ones
     (set/seta/cvar/toggle/settemp/settemp_restore). A verb tells client vs server apart at runtime by
     inspecting `ctx.Caller` (null = server console/rcon; non-null = a player typed `cmd …`).
   - `CommandContext` (Commands.cs:13-57): `Arg(i)`, `ArgCount`, `ArgFloat(i)`, `ArgTail(first)`, `Argv`,
     `Print(line)`, `Output`, `IsServerConsole`, `Caller`. This is the QC `argv()`/`argc`/`sprint`/`print`
     successor.

   **Live call sites (the real play path):**
   - `game/net/ServerNet.cs:409` `HandleClientCommand` — DP `clc_stringcmd`: a connected peer's console
     line → `_world.Commands.Execute(line, isServerConsole:false, caller:st.Player)`; output returned as
     `NetControl.ServerPrint`.
   - `game/net/ServerNet.cs:655` — a C2S impulse → `Execute("impulse N", isServerConsole:false, caller:p)`.
   - `game/Shell.cs:495` `LocalRouteCommand` — the in-game console's gameplay-command router (listen
     server): `world.Commands.Execute(line, isServerConsole:false, caller: LocalServerPlayer)`.
   - `src/XonoticGodot.Server/GameWorld.cs:462` — `Voting.VotePassed = cmd => Commands.Execute(cmd, isServerConsole:true)`
     (a passed vote runs its parsed command back through the bus — **this is where `defer 1 restart` lands**).

2. **`XonoticGodot.Engine/Console/ConsoleCommands.cs`** (`ConsoleCommands` class) — the SHARED console/cvar
   surface, layered onto `XonoticGodot.Common/Config/ConfigInterpreter.cs`.
   - Registers via `_interp.RegisterCommand(name, Action<IReadOnlyList<string>>)` onto the interpreter's
     `_commands` table (ConfigInterpreter.cs:139). Also a **manual table**.
   - Holds the DP engine-console builtins: echo/clear/toggle/cycle/inc/dec/cvar/cvarlist/cmdlist/apropos/
     help/bind/unbind/unbindall/bindlist/name/developer (ConsoleCommands.cs:67-95).
   - `ConfigInterpreter` itself owns set/seta/set_temp/seta_temp/setp/alias/unalias/exec/unset/cvar_reset
     (ConfigInterpreter.cs:244-268) plus `$`-expansion and alias arg substitution.
   - **`UnknownCommandHandler`** (ConfigInterpreter.cs:119, wired ConsoleCommands.cs:93 → `RouteUnknown`):
     any line that is not a console/cvar/alias/bare-cvar command is routed to the live game — the
     `localRouter` (→ `world.Commands.Execute`) or, on a pure client, `remoteSender` (clc_stringcmd).
   - This is Godot-free, in `src`, unit-testable (tests in `tests/XonoticGodot.Tests/ConsoleTests.cs`).

**Where each T56 family belongs (the placement decision):**
- The QC `GENERIC_COMMAND`s are present in MENU **and** CLIENT **and** SERVER programs (generic.qc:556-565;
  aliased via `qc_cmd_svcl`/`qc_cmd_svmenu`). In stock cfgs/quickmenu, `rpn`/`addtolist`/`removefromlist`/
  `maplist` are issued from the **console/cfg** layer (e.g. quickmenu uses `rpn`; `g_maplist` editing uses
  `maplist`/`addtolist`). They are pure cvar/string ops with NO `GameWorld` dependency.
  → **Place rpn / maplist / addtolist / removefromlist / nextframe / dumpcommands / restartnotifs / runtest
    on the SHARED `ConsoleCommands` surface** (`_interp.RegisterCommand`), reachable on client, server, and
    headless tests, exactly mirroring QC's "all programs" availability. `settemp`/`settemp_restore` already
    exist server-side (Commands.cs:296-297, → `SettempCvars`); add the generic console aliases too for parity
    but they can delegate to `SettempCvars` (it's static).
- The QC server `ClientCommand_*` (voice/suggestmap/autoswitch/physics/clientversion) are caller-gated
  per-player commands. → **Place on `Commands.cs`** (the bus that already has caller + GameWorld).
- The QC `CommonCommand_*` (records/rankings/lsmaps/printmaplist/ladder/cvar_purechanges/editmob) are shared
  sv_cmd+cmd commands. → **Place on `Commands.cs`** (some already exist: who/teamstatus/time/info/cvar_changes).
- `defer` is an ENGINE command (DP cmd.c, `CF_SHARED`). It is consumed server-side by the vote path. →
  **Place a `defer` handler on `Commands.cs`** backed by a NEW sim-clock queue pumped from
  `GameWorld.OnStartFrame`. (Optionally also mirror a console-side `defer` later; the load-bearing consumer
  is the server vote path, so server-side is the required scope.)

---

## 1. ENGINE `defer` — full Base spec (Darkplaces cmd.c)

### Base behavior (`Base/darkplaces/cmd.c`)
- Registered: `cmd.c:1563` `Cmd_AddCommand(CF_SHARED, "defer", Cmd_Defer_f, "execute a command in the future")`.
- `Cmd_Defer_f` (cmd.c:79-118):
  - `argc == 1` → list pending: "No commands are pending." or, per entry, `"-> In %9.2f: %s"`.
  - `argc == 2 && argv(1)=="clear"` (case-insensitive) → drop ALL deferred entries.
  - `argc == 3 && strlen(argv(2))>0` → enqueue: link `argv(2)` as a deferred command with
    `delay = atof(argv(1))` (cmd.c:107-108). NOTE: the command is **one token** (`argv(2)`), so callers
    quote multi-word commands: `defer 1 "restart"` or `defer 1 restart` (single word). The vote path uses
    `defer 1 restart` (single word) — fine.
  - else → usage: `"usage: defer <seconds> <command>\n       defer clear\n"`.
- `Cbuf_Execute_Deferred` (cmd.c:320-343) — the pump, called each engine frame:
  - Computes elapsed `eat = realtime - deferred_oldtime` (with a clamp: if elapsed `<0` or `>1800`, reset
    the clock; cmd.c:325-326). **Quantization gate:** `if (eat < 1.0/128.0) return;` (cmd.c:328) — it only
    advances the timers in ~7.8 ms buckets. Then `deferred_oldtime = realtime`.
  - For each deferred entry: `current->delay -= eat; if (delay <= 0) { Cbuf_AddText(text); … move to free; }`
    (cmd.c:332-342). So a `defer 1 X` fires once `delay` crosses 0 (≈1 s of real time later), then the
    command is parsed+executed via the normal command buffer.
- Semantics that matter for parity:
  - Time base is **realtime** (host.realtime), pumped every frame regardless of pause. In the port the
    closest faithful base is the **sim clock** (`GameWorld.Time` / `Api.Clock.Time`), which advances 1/72 s
    per tick — see Risk R1 (timeout pause). For the vote `restart` use case (1 s defer) the sim clock is
    correct and simpler; using sim time also makes it deterministic/testable.
  - The 1/128 s quantization is a micro-optimization, not observable for a 1 s defer; the port can decrement
    by the tick delta each tick (`Time - lastPumpTime`) without the bucket gate and be within one tick of DP.

### Port: current state (the bug)
- **No `defer` command exists** anywhere in the port (grep: `"defer"` appears only as a string in
  `ConfigInterpreter.NonCvarCommands` denylist (ConfigInterpreter.cs:69) and as the vote's parsed command
  text). **No sim-clock command queue exists** (`SimulationLoop` has only per-entity `nextthink`, not a
  command queue; SimulationLoop.cs).
- `VoteController.Parse` builds `parsedCommand = "defer 1 restart"` (VoteController.cs:564) with the comment
  "QC defers so the announcer/result shows first". On pass, `VotePassed?.Invoke(parsedCmd)` fires
  (VoteController.cs:199 and :425) → `GameWorld.cs:462` → `Commands.Execute("defer 1 restart", server)` →
  no `defer` verb → `ctx.Print("Unknown command \"defer\"")` (Commands.cs:157). **The restart silently
  no-ops** — exactly the task's described symptom.

### Port: the fix (NEW file + 2 small seams)
- NEW `src/XonoticGodot.Server/DeferredCommands.cs` — a sim-clock command queue:
  - State: `List<(float fireTime, string command)>` (or a small struct). API:
    `Defer(float delaySeconds, string command)`, `Clear()`, `IReadOnlyList<…> Pending` (for the
    `argc==1` list), and `Pump(float now, Action<string> run)` which fires + removes every entry whose
    `fireTime <= now`, calling `run(command)` for each (DP `Cbuf_AddText`).
  - Faithful detail: `fireTime = now + max(0, delay)`. Fire order is insertion order (DP walks the list).
  - This is a pure, Godot-free, testable type. Mirror DP's `Cbuf_Execute_Deferred`.
- Register the `defer` command in `Commands.RegisterBuiltins()` (Commands.cs ~ alongside settemp at :296):
  `Register("defer", "defer <seconds> <command> | defer clear — run a command after a delay", CmdDefer);`
  - `CmdDefer`: argc==1 → list pending (`"No commands are pending."` / `"-> In %9.2f: %s"`); argc==2 &&
    arg(1)=="clear" → `queue.Clear()`; argc>=3 → `queue.Defer(ArgFloat(1), ArgTail(2))` (use `ArgTail(2)`
    so an unquoted multi-word `defer 1 say hi` also works — a superset of DP's single-token, harmless);
    else usage.
  - The `Commands` class needs a `DeferredCommands` instance + a "run a deferred command" callback. Cleanest:
    `Commands` owns a `public DeferredCommands Deferred { get; } = new();` and the queue runs commands back
    through `this.Execute(cmd, isServerConsole:true)`.
- Pump from the per-tick server loop: in `GameWorld.OnStartFrame()` (GameWorld.cs:664-698), next to
  `Voting.Think()` (:690), add `Commands.Deferred.Pump(Time, cmd => Commands.Execute(cmd, isServerConsole:true));`.
  `Time` is the sim clock (GameWorld exposes `Time`; OnStartFrame already reads it at :686). This is the
  HOT-FILE edit — `GameWorld.cs` is T36-owned (see §7).

---

## 2. RPN calculator — full Base spec (`common/command/rpn.qc`)

A complete stack-based RPN VM, registered `GENERIC_COMMAND(rpn, "RPN calculator", true)` (generic.qc:562).
Stock cfgs/quickmenu rely on it. The body is `GenericCommand_rpn(request, argc, command)` (rpn.qc:63-623).

### Stack + helpers (rpn.qc:11-55)
- `rpn_stack[MAX_RPN_STACK]` of strings, `rpn_sp` pointer, `rpn_error` flag.
- `rpn_pop()` (underflow → "rpn: stack underflow", error=true, ""), `rpn_push(s)` (overflow →
  "rpn: stack overflow"), `rpn_get()` (peek; empty → "rpn: empty stack"), `rpn_set(s)` (poke top).
- Float views: `rpn_getf`/`rpn_popf` = `stof`; `rpn_pushf`/`rpn_setf` = `sprintf("%.9g", f)`. **The %.9g
  formatting is parity-critical** (string round-trip through the stack and into cvars).
- A persistent DB (`rpn_db`, a `db_create()` hashtable) for the dbpush/dbpop/… family, with a cursor at key
  `stack.pos` and a count at `stack.pointer`. Lazily created on first `rpn` (rpn.qc:73-78) and on `SHUTDOWN`
  closed (rpn.qc:57-61).

### Token loop (rpn.qc:80-571) — `rpn_sp=0; rpn_error=false;` then for each `argv(rpnpos)`:
**Literal pushes (rpn.qc:88-98):**
- empty token → skip; first char is a digit `>0`, or `'0'` → push the token literally; `f>=2 && first=='+'`
  or `'-'` → push literally (signed number); `f>=2 && first=='/'` → push `substring(1)` (a quoted string
  literal `/abc` → `abc`).

**Stack ops:** `clear` (sp=0); `def`/`=` (pop value, pop name, `registercvar(name,"")` then
`cvar_set(name,value)` unless error; empty name → error); `defs`/`@` (pop count `i`; pop that many values
joined as `/v /v …`; pop name; same registercvar+set); `load` (peek name → replace with `cvar_string`);
`exch` (swap top two); `dup`; `pop`.

**Arithmetic/bitwise/logic (rpn.qc:162-265):** `add|+`, `sub|-`, `mul|*`, `div|/`, `mod|%`
(`f2 - f*floor(f2/f)`), `pow|**`, `bitand|&`, `bitor|\|`, `bitxor|^`, `and|&&`, `or|\|\|`,
`xor|^^` (`!a != !b`), `bitnot` (`~`), `not`, `abs`, `sgn` (-1/0/1), `neg|~` (negate), `floor|f`,
`ceil|c`, `exp`, `log`, `sin`, `cos`, `max`, `min`, `bound` (`bound(lo,mid,hi)` — pops hi,lo, peeks mid),
`when` (pop cond, pop a, peek b → b if cond else a), comparisons `>|gt`, `<|lt`, `==|eq`, `>=|ge`, `<=|le`,
`!=|ne`, `rand` (`ceil(random()*top)-1`), `crc16` (`crc16(false, top)`).

**DB ops (rpn.qc:270-435):** `put` (key,value → db_put), `get` (key → db_get pushed), `dbpush`/`dbpop`/
`dbget`/`dblen`/`dbclr`/`dbsave`/`dbload`/`dbins`/`dbext`/`dbread`/`dbat`/`dbmov`/`dbgoto` — a full
indexed-stack-in-a-hashtable with a cursor. (These are advanced; see Risk R3 — most cfg/quickmenu rpn use
is arithmetic + def/load + set ops, not the DB family.)

**Set/string ops (rpn.qc:436-559):**
- `union` / `intersection` / `difference` (set ops over two space-separated word lists via tokenize),
- `shuffle` (randomly arrange the words of the top list),
- `fexists_assert` (pop name; error if file missing), `fexists` (peek → 1/0),
- `localtime`/`gmtime` (`strftime(true/false, top)`), `time` (push the VM `time`),
- `digest` (pop algo, peek data → `digest_hex`), `sprintf1s` (pop fmt, peek arg → `sprintf(fmt, arg)`),
- `eval` (pop a string, splice it into the remaining command tail and re-tokenize — a meta-op).
- **Default (rpn.qc:560-562): an unknown token pushes `cvar_string(token)`.** This is the "read a cvar by
  bare name" fallback — important for cfg usage.
- On `rpn_error` the loop breaks (rpn.qc:563-564). After the loop, any leftover stack entries are printed:
  `"rpn: still on stack: <s>"` (rpn.qc:566-570).

### Port: current state — **entirely missing.** No `rpn`, no stack VM (grep confirms).

### Port: the plan
- NEW `src/XonoticGodot.Engine/Console/Rpn.cs` — a pure RPN evaluator. Inputs: the token list (argv[1..]),
  a cvar facade (read `GetString`, write `Set`/`Register` for `def`/`defs`/`load`), and a `print` sink for
  the error/leftover messages. Output: mutates cvars + prints. Keep `%.9g`-equivalent formatting
  (`f.ToString("R")` is NOT the same; use a `G9`-style format: `f.ToString("0.#########", InvariantCulture)`
  carefully, or replicate `%.9g` via `f.ToString("G9", InvariantCulture)` — VERIFY round-trip parity in tests).
- Register on the SHARED `ConsoleCommands` surface (so client+server+cfg can use it):
  `_interp.RegisterCommand("rpn", a => Rpn.Run(a, _cvars, _print));`. It uses the injected `CvarService`
  (which has Set/GetString and Register-equivalent). `def`/`defs` map to `_cvars.Set` (+ archive? — QC uses
  plain `registercvar`+`cvar_set`, NOT seta, so do NOT archive).
- **Phasing (recommended):** implement the arithmetic/stack/logic/compare/min-max-bound/when core +
  def/defs/load/dup/exch/pop/clear + the set ops (union/intersection/difference/shuffle) + sprintf1s +
  the bare-cvar default + the leftover/underflow prints FIRST (covers all stock cfg/quickmenu usage). Defer
  the DB family (dbpush…dbgoto) and time/digest/fexists/eval to a follow-up unless a stock cfg needs them
  (audit: quickmenu uses arithmetic + cvar read/write; the DB family is essentially unused in shipped cfgs).
  Note any omission explicitly as a deliberate deviation.

---

## 3. GENERIC commands — full Base spec (`common/command/generic.qc`)

Each is a `GENERIC_COMMAND(id, desc, menubased)` (generic.qc:556-565) dispatching on `request`
(CMD_REQUEST_COMMAND vs CMD_REQUEST_USAGE).

### `addtolist <cvar> <value>` (generic.qc:60-98)
- If `cvar_string(cvar) == ""` → `cvar_set(cvar, value)`. Else `FOREACH_WORD(list, it==value, return)` (skip
  if already present), then `cvar_set(cvar, cons(list, value))` (append at END with a space). Usage otherwise.
- **cons(a,b)** = `a` if `b==""`, `b` if `a==""`, else `strcat(a, " ", b)` (Xonotic util). FOREACH_WORD
  iterates space-separated words.

### `removefromlist <cvar> <value>` (generic.qc:356-389)
- `argc==3`: rebuild `cvar` keeping only words `!= value` (FOREACH_WORD + cons), then `cvar_set`.

### `maplist <action> [<map>]` (generic.qc:232-334)
- `add <map>` (argc==3): if `maps/<map>.bsp` missing → "maplist: ERROR: <map> does not exist!"; else if
  `g_maplist==""` set to `<map>` else **PREPEND** `"<map> <existing>"` (note: prepend, unlike addtolist).
- `cleanup`: `MapInfo_Enumerate` + filter; keep only words that pass `MapInfo_CheckMap(it)`; `cvar_set`.
- `remove <map>` (argc==3): tokenize `g_maplist`, rebuild keeping `!= map`; `cvar_set`.
- `shuffle`: `cvar_set(g_maplist, maplist_shuffle(g_maplist))` — Fisher-Yates over the words (rpn.qc has its
  own; `maplist_shuffle` at generic.qc:232-249 uses a buf + random insert).
- Usage lists: add, cleanup, remove, shuffle.

### `nextframe <command>` (generic.qc:336-354) — `queue_to_execute_next_frame(substring(...))`. Runs the
  given command on the NEXT VM frame. (A degenerate `defer 0`-ish; in the port = enqueue on a "next tick"
  list, or just `Deferred.Defer(0, cmd)` — fires next pump.)

### `qc_curl [--key n] [--cvar] [--exec] <url> [postargs]` (generic.qc:100-170 + Curl_URI_Get_Callback
  at :31-53) — async HTTP via `crypto_uri_postbuf` + a callback that optionally execs the body / sets a cvar /
  logs it. **Port: stub/omit** (no HTTP layer; not load-bearing for gameplay). Note as a deliberate deviation
  — register a `qc_curl` that prints "not supported" so cfgs that call it don't error, OR omit entirely.

### `dumpcommands` (generic.qc:172-230) — writes all command tables to `<prefix>_dump.txt`. Port: optional;
  can dump `Commands.All` + `ConsoleCommands.AllCommandNames()` to a file, or print to console. Low priority.

### `restartnotifs` (generic.qc:391-446) — counts + re-registers all notifications. Port: the notification
  system is `NotificationSystem` (registry-based); a faithful port re-runs registration. Low priority / can
  be a no-op-with-log unless a cfg/menu path needs it.

### `runtest [<fn>]` (generic.qc:506-530) — `TEST_Run`/`RUN_ALL_TESTS` (the QC unit-test harness). Port:
  the test harness is xUnit; map to a no-op-with-message OR omit. Deliberate deviation (the QC TEST_* macros
  have no runtime analog).

### `settemp` / `settemp_restore` (generic.qc:448-504) — already ported server-side (Commands.cs:296-297 →
  `SettempCvars`). For the generic/console surface, register console aliases delegating to `SettempCvars`
  (static, Godot-free). `settemp` captures the original once and sets; `settemp_restore` writes all back.
  **NOTE the QC settemp also has a "settemp -1 …" / settemp_list flavor in some versions — current generic.qc
  is just settemp/settemp_restore; match that.**

### Port: current state — **all missing on the console surface.** (settemp/settemp_restore exist on the
  server `Commands` table only.)

### Port: the plan — register addtolist / removefromlist / maplist / nextframe on `ConsoleCommands`
  (shared). Implement `cons`/word-list helpers as a small static (NEW `src/XonoticGodot.Engine/Console/WordList.cs`
  or inline). `maplist add`'s bsp-existence check and `cleanup` need a map catalog — the port has
  `MapRotation`/MapInfo-equivalent; `maplist add/remove/shuffle` are pure `g_maplist` string ops (do those
  faithfully), and `cleanup` can fall back to "keep words as-is" if no map catalog is reachable from the
  console layer (note the deviation). `maplist_shuffle` = Fisher-Yates over words (mirror generic.qc:232-249;
  use a seeded RNG for determinism in tests).

---

## 4. SERVER client commands — Base spec (`server/command/cmd.qc`)

These are caller-gated per-player commands; place on `Commands.cs` (has `ctx.Caller` + `GameWorld`).

### `voice <voicetype> [message]` (cmd.qc:1033-1079) — taunts.
- `GetVoiceMessage(argv(1))` → the voice entity; invalid → "Invalid voice. Use one of: %s" (`allvoicesamples`).
- If `IS_DEAD(caller)` → silently return (dead can't taunt; still see invalid warnings). If `IS_SPEC/OBSERVER`
  → silently return (no body to play from). Else `msg = ArgTail(2)`; `VoiceMessage(caller, e, msg)`.
- Port: needs the voice/taunt sound system. Check if a voice system exists (grep `VoiceMessage`/
  `allvoicesamples` — likely missing). If missing, implement a minimal `voice` that validates the type
  against a known list and emits a sound via `Api.Sound`/`SoundService` (the port HAS a sound bus, see
  MEMORY: SoundService.Broadcast). The dead/spec gates are pure `ctx.Caller` checks. **Verify the taunt
  sample registry exists before promising full parity; otherwise scope to validation + the gates + a sound
  emit by name.**

### `suggestmap <map>` (cmd.qc:907-929) — `sprint(MapVote_Suggest(caller, argv(1)))`.
- Port has `MapVoting` (`GameWorld.MapVote`, GameWorld.cs:135). Map to `_world.MapVote.Suggest(caller, map)`
  if such an API exists; else add a Suggest method (MapVoting is NOT T56-owned — but it's not in another A2
  task's owned set either; confirm). The QC `MapVote_Suggest` returns a status string to sprint.

### `autoswitch <selection>` (cmd.qc:201-224) — set per-client `cvar_cl_autoswitch = InterpretBoolean(argv1)`,
  sprint the new state. Port: per-player cvar state — the port replicates client cvars onto the Player
  (`GetCvars`/REPLICATE). Set the player's autoswitch flag (find the field on `Player`/`ServerPlayerState`)
  and sprint. If no such field, add one (a per-player bool; ServerPlayerState is server-side, not another
  A2 task's file).

### `physics <set>` (cmd.qc:567-605) — client physics selection.
- Gated on `autocvar_g_physics_clientselect` (else "Client physics selection is currently disabled."). `list`/
  `help` → print `autocvar_g_physics_clientselect_options + " default"`. If `Physics_Valid(cmd)` or
  `"default"` → `stuffcmd(caller, "seta cl_physics <set>")` + "^2Physics set successfully changed…". Default
  branch prints "Current physics set: <cvar_cl_physics>".
- Port: `g_physics_clientselect` ships **0** (disabled) by default → the faithful port is "print disabled"
  unless an admin enables it. Implement the gate + list + the stuffcmd-equivalent (the port has a stuffcmd
  channel — `selfstuff` analog). Low gameplay impact; the disabled path is the common one.

### `clientversion <version>` (cmd.qc:273-314) — INTERNAL (client sends on connect). Sets `CS(caller).version`
  (`$gameversion` → 1 else stof); if out of `[gameversion_min, gameversion_max]` → version_mismatch +
  observe; else teamplay/spectate seat logic. Port: the handshake already carries build parity
  (ServerNet.cs:505-516 `NetProtocol.BuildParity`), so `clientversion` is largely vestigial — implement as a
  no-op-that-records-the-version OR wire it to the existing version gate. Note the handshake supersedes it.

### Port: current state — none of voice/suggestmap/autoswitch/physics/clientversion exist (Commands.cs has
  no such Register lines; grep confirms).

---

## 5. COMMON commands + getreplies — Base spec (`server/command/{common.qc,getreplies.qc}`)

Place on `Commands.cs`. Several already exist (who/teamstatus/time/info/cvar_changes — Commands.cs:301-306).

### The reply-string cache (getreplies.qc) — the precompute model
- The CommonCommands `records`/`rankings`/`lsmaps`/`printmaplist`/`ladder`/`cvar_purechanges` and editmob's
  `spawn list` are NOT computed on demand — they print a **precomputed strzoned string** built once at world
  init. The cache vars (declared getreplies.qh-adjacent): `records_reply[10]`, `rankings_reply`,
  `lsmaps_reply`, `maplist_reply`, `ladder_reply`, `monsterlist_reply`, plus `cvar_changes`/`cvar_purechanges`.
- Precompute call site: `server/world.qc:1022-1038` (in the world init tail):
  `maplist_reply = strzone(getmaplist()); lsmaps_reply = strzone(getlsmaps());
   monsterlist_reply = strzone(getmonsterlist()); for i in 0..9 records_reply[i] = strzone(getrecords(i));
   ladder_reply = strzone(getladder()); rankings_reply = strzone(getrankings());`
- The generators (getreplies.qc):
  - `getrecords(page)` (35-44): `MUTATOR_CALLHOOK(GetRecords, page, s)` — the per-gametype hook fills the
    page string (CTF cap records, race times, etc.); then `MapInfo_ClearTemps()`.
  - `getrankings()` (46-69): race/CTS top-N times for the current map (`race_readTime`/`race_readName`,
    `count_ordinal`, `TIME_ENCODED_TOSTRING`); "No records…" if empty.
  - `getladder()` (71-231): a heavy cross-map race ladder (per-UID points across all maps from
    `ServerProgsDB`). Very race-specific.
  - `getmaplist()` (233-249): `g_maplist` words that pass `MapInfo_CheckMap`, colorized, "Maps in list (N): …".
  - `getlsmaps()` (252-294): all maps (up to LSMAPS_MAX=250) not forbidden, colorized, new-map asterisks for
    unrecorded race/CTF maps; "Maps available (N): …".
  - `getmonsterlist()` (296-307): `FOREACH(Monsters, !hidden)` netnames, colorized; "Monsters available: …".
- The CommonCommand bodies (common.qc) are then trivial: each just `print_to(caller, <cached>)`:
  - `records` (564-591): `records_reply[num-1]` for a page arg, else all 10 pages.
  - `rankings` (544-562) → `rankings_reply`; `lsmaps` (504-522) → `lsmaps_reply`; `printmaplist` (524-542) →
    `maplist_reply`; `ladder` (484-502) → `ladder_reply`.
  - `cvar_changes` (272-291) → `cvar_changes`; `cvar_purechanges` (293-312) → `cvar_purechanges`.
    (`cvar_changes`/`cvar_purechanges` are built in world.qc as cvars diverge from defaults — the running
    log of non-default cvars; pure vs all.)

### `editmob <cmd> [args]` (common.qc:314-458) — monster editor (also exposed as standalone spawnmob/killmob
  in some cfgs via aliases). Subcommands:
- `name <newname>` — rename the monster you look at (gated g_monsters_edit, ownership, must look at it).
- `spawn <type> [moveflag]` — trace-spawn a monster (gated g_monsters, max counts, alive, not in vehicle,
  not dead, AllowMobSpawning hook); `spawn list` → `monsterlist_reply`; type `random`/`anyrandom` allowed;
  `spawnmonster(spawn(), type, MON_Null, caller, caller, trace_endpos, false, false, moveflag)`.
- `kill` — `Damage(mon, …, health+max_health+200, DEATH_KILL, …)` the looked-at monster.
- `skin <n>` / `movetarget <flags>` — edit the looked-at monster.
- `butcher` — SERVER-ONLY (caller==null): remove ALL monsters; `monsters_total=monsters_killed=totalspawned=0`.
- Usage: "butcher spawn skin movetarget kill name".

### Port: current state
- The reply caches DO NOT exist on `Commands` (no records/rankings/lsmaps/printmaplist/ladder commands;
  grep confirms). `cvar_changes` exists but is a stub printing the loaded-config count (Commands.cs:1074-1080),
  NOT the QC non-default-cvar log; `cvar_purechanges` is absent.
- `editmob`/spawnmob/killmob DO NOT exist on `Commands`.
- The MONSTER spawn/kill machinery DOES exist (T43's `MonsterAI`): `MonsterAI.SpawnMonster(e, name,
  monsterId, spawnedBy, follow, origin, respawn, removeIfInvalid, moveFlags)` (MonsterAI.cs:386),
  `MonsterAI.SpawnFromMap`, `MonsterAI.MasterSwitchEnabled("g_monsters")`, and `Monsters.ByName(name)`
  (used by MonsterSpawnFuncs.cs:24). Killing = `Combat.Damage(...)` (already used by `CmdKill`,
  Commands.cs:715). **These are PUBLIC methods on T43-owned types** — calling them from `Commands.cs` does
  NOT edit `Monsters/*`, so editmob can be implemented WITHOUT touching T43's files (see §7).

### Port: the plan
- Add a NEW `src/XonoticGodot.Server/CommandReplies.cs` (or fold into a `GameWorld` partial) computing the reply
  strings at world init: `getlsmaps`/`getmaplist` from the map catalog + `g_maplist`; `getmonsterlist` from
  the `Monsters` registry (FOREACH non-hidden); `getrankings`/`getrecords`/`getladder` from the race/score
  records store (the port has a records/race subsystem — verify the API; if race records aren't wired,
  rankings/records/ladder fall back to "No records available" exactly like the QC empty case, which is the
  honest current state for non-race modes). Precompute at the same point QC does (world init tail) — the
  port's equivalent is `GameWorld.Boot`/post-map-load.
- Register `records`/`rankings`/`lsmaps`/`printmaplist`/`ladder` on `Commands` (each prints the cached
  string). Fix `cvar_changes` to print the non-default cvar log and add `cvar_purechanges` (the port can
  derive "changed from default" by diffing the cvar store against registered defaults — `CvarService` tracks
  defaults; `ConsoleCommands.PrintCvar` already compares to `GetDefault`).
- Register `editmob` on `Commands`, delegating spawn→`MonsterAI.SpawnMonster`, kill→`Combat.Damage`,
  list→the monster reply, butcher→remove all monsters (server-only). Add `spawnmob`/`killmob` console
  aliases mapping to `editmob spawn`/`editmob kill` for cfg parity (QC ships those aliases in commands.cfg).
  Reuse the existing look-at trace (`Api.Trace`) + the per-player gates (ctx.Caller).

---

## 6. CVARS + SHIPPED DEFAULTS this task reads (all already in `Cvars.Defaults` unless noted)

| cvar | shipped default | where read | in port? |
|---|---|---|---|
| `g_maplist` | "" | maplist/addtolist/getmaplist | YES (Cvars.cs:216) |
| `g_physics_clientselect` | 0 | physics cmd | NO — add default 0 |
| `g_physics_clientselect_options` | "" | physics cmd | NO — add default "" |
| `g_monsters` | (unset→on) | editmob spawn/butcher | via MonsterAI.MasterSwitchEnabled |
| `g_monsters_edit` | 0 | editmob name/skin/movetarget | NO — add (0 = editing off) |
| `g_monsters_max` / `_max_perplayer` | (server cfg) | editmob spawn caps | check Cvars; add if absent |
| `g_campaign` | 0 | editmob (disabled in SP) | YES (Cvars.cs:275) |
| `gameversion_min` / `gameversion_max` | (build) | clientversion | NO — vestigial; handshake supersedes |
| `utf8_enable` | 1 | (tell; not in scope) | n/a |

NOTE: `defer` reads no cvars. `rpn` reads/writes arbitrary cvars by name (`def`/`load`/bare-token default).
DP `defer` uses `host.realtime`; the port substitutes the sim clock (deliberate, documented in §1/R1).

---

## 7. HOT FILES (shared files this task must edit that ALSO appear in another A2 task's owned set)

Per the task brief, **T56 owns `Commands.cs` + `ConsoleCommands.cs`** within A2 (no other A2 task edits
them). So most of T56 is NEW files + edits to those two. The ONE cross-task edit:

### `src/XonoticGodot.Server/GameWorld.cs` — **owned by T36** (mode-objective spawnfuncs).
- **Exact minimal edit T56 needs:** in `OnStartFrame()` (currently GameWorld.cs:664-698), add ONE line next
  to `Voting.Think();` (line 690):
  ```csharp
  // DP Cbuf_Execute_Deferred: fire any `defer`-queued commands whose delay has elapsed (the passed
  // `restart` vote enqueues `defer 1 restart`). Pumped on the sim clock.
  Commands.Deferred.Pump(Time, cmd => Commands.Execute(cmd, isServerConsole: true));
  ```
  Plus (already T56-owned in Commands.cs) the `Commands.Deferred` property exists. This single insertion is
  the only GameWorld touch. Hand T36 this snippet so they own the line; T56 owns the `DeferredCommands` type
  + the `defer` command + the `Commands.Deferred` property (all in T56 files).

### NOT a conflict (verified):
- **Monsters/* (T43-owned):** `editmob`/spawnmob/killmob call `MonsterAI.SpawnMonster` / `Combat.Damage` /
  `Monsters.ByName` — all PUBLIC methods on T43 types. Calling them from `Commands.cs` is a normal cross-assembly
  call, NOT an edit to `Monsters/*`. **No T43 file edit required.** (Confirms the task's "double-check" ask.)
- **DamageSystem.cs (T43):** `Combat.Damage` is the public damage entry already used by `CmdKill`
  (Commands.cs:715) — reused, not edited.
- **MapObjectsRegistry.cs / Registries.cs:** NOT touched — commands register manually in Commands.cs /
  ConsoleCommands.cs, not via attribute/registry tables.
- **VoteController.cs:** already builds `defer 1 restart` (VoteController.cs:564) — NO edit needed; the fix is
  making `defer` exist + pumped. (VoteController is T56-adjacent / server-owned, not another A2 task's file.)
- **MapVoting / MapRotation:** `suggestmap` may need a `MapVoting.Suggest` method and `maplist cleanup` a map
  catalog read — neither file is in another A2 task's owned set, so they're free to edit if needed (prefer a
  NEW method over restructuring). `MapRotation` already parses `g_maplist`/shuffles (MapRotation.cs) — reuse
  its word-split/shuffle rather than duplicating.

---

## 8. STEP PLAN (faithful, mirrors Base order)

1. **`defer` (the load-bearing fix).** NEW `DeferredCommands.cs` (sim-clock queue, mirror Cbuf_Execute_Deferred).
   Add `Commands.Deferred` property + the `defer` command (list/clear/enqueue/usage) in Commands.cs. Hand T36
   the ONE-LINE pump for `GameWorld.OnStartFrame`. → Restart votes now actually restart.
2. **RPN core.** NEW `Rpn.cs` (arithmetic/stack/logic/compare/min/max/bound/when + def/defs/load/dup/exch/
   pop/clear + set ops + sprintf1s + bare-cvar default + underflow/leftover prints, `%.9g` formatting). Register
   `rpn` on ConsoleCommands. Defer DB/time/digest/fexists/eval (note deviation).
3. **Generic list commands.** `addtolist`/`removefromlist`/`maplist`(add/remove/shuffle; cleanup best-effort)/
   `nextframe` on ConsoleCommands (+ `cons`/word-list helper). `settemp`/`settemp_restore` console aliases →
   SettempCvars.
4. **Server client commands.** `autoswitch`/`physics`(disabled-path faithful)/`clientversion`(version record)/
   `suggestmap`/`voice`(validation+gates+sound) on Commands.cs.
5. **Common reply commands.** NEW reply-cache precompute (`getlsmaps`/`getmaplist`/`getmonsterlist`; rankings/
   records/ladder = race store or honest empty). Register `records`/`rankings`/`lsmaps`/`printmaplist`/`ladder`
   on Commands; fix `cvar_changes`, add `cvar_purechanges`.
6. **editmob.** `editmob` (+ `spawnmob`/`killmob` aliases) on Commands, delegating to MonsterAI/Combat.
7. **Stubs/deviations:** `qc_curl`/`dumpcommands`/`restartnotifs`/`runtest` — register no-op-with-message or
   omit, each documented as a deliberate deviation.

---

## 9. TEST PLAN (xUnit; pattern from `tests/XonoticGodot.Tests/ConsoleTests.cs`)

- **DeferredCommands (pure):** enqueue `defer 1 X`; `Pump(now=0.5)` does NOT fire; `Pump(now=1.0)` fires once,
  removes it; insertion-order firing for two entries; `Clear()` empties; the `argc==1` list output format.
- **defer ↔ vote integration:** drive a `GameWorld` (or a `Commands` with a stub world), pass a `restart`
  vote → `VotePassed("defer 1 restart")` → after pumping `Time` past 1 s, `RestartMatch` ran (assert the
  match restarted). This is the regression guard for the silent-no-op bug.
- **RPN (pure, ConsoleTests-style Make()):** `rpn /x 2 3 add def` → cvar `x`=="5"; `rpn /pi 3.14159 def` round-
  trips via `%.9g`; `rpn 7 2 mod` leftover-print == "5"-equivalent; underflow → "rpn: stack underflow";
  `rpn "a b c" "b d" union` → "a b c d"; `min`/`max`/`bound`/`when`/comparisons; bare-cvar default reads a set
  cvar. Round-trip parity test for the `%.9g` formatter against known QC outputs.
- **Generic lists:** `addtolist g_maplist foo` on empty → "foo"; again → unchanged (dedup); add "bar" → "foo bar";
  `removefromlist g_maplist foo` → "bar"; `maplist add baz` PREPENDS → "baz bar"; `maplist remove bar`;
  `maplist shuffle` (seeded RNG) is a permutation.
- **Server client cmds:** `autoswitch 1`/`0` toggles the per-player flag + sprints; `physics` with
  clientselect=0 → "disabled"; with clientselect=1 + `list` → options; `clientversion 806` records the version
  (no crash); `voice` dead/spec → silent, invalid → "Invalid voice…".
- **Common replies:** with `g_maplist`="map1 map2", `printmaplist` prints the cached list; `lsmaps` lists the
  catalog; `getmonsterlist` lists registry monsters; rankings/records on a non-race mode → "No records…".
- **editmob:** server-console `editmob butcher` removes all monsters + zeroes counts; `editmob spawn zombie`
  (with a caller + g_monsters on) spawns via MonsterAI (assert a new monster entity); `editmob spawn list` →
  monster reply; gates (campaign off, not dead, caps) reject correctly.
- **Caller gating:** caller-gated cmds (voice/autoswitch/physics/editmob spawn) reject a null caller where QC
  does; `defer`/`rpn`/list cmds work from the console (no caller).

---

## 10. RISKS / PARITY TRAPS

- **R1 — `defer` time base.** DP uses `host.realtime` (advances even when paused/intermission). The port's
  sim clock (`GameWorld.Time`) FREEZES during a timeout pause (Timeout.IsPaused makes GameStopped true, but
  OnStartFrame still runs `Voting.Think`/the pump — VERIFY the tick still advances `Time` during a pause;
  SimulationLoop.Tick always does `Time += FrameTime`, and GameWorld.Frame always Advances, so Time keeps
  ticking even when GameStopped — GOOD, matches DP closely enough). For the 1 s restart-vote defer this is
  correct. Document the sim-clock substitution. Do NOT gate the pump on GameStopped (DP doesn't).
- **R2 — `%.9g` formatting.** QC `sprintf("%.9g", f)` is the exact stack/cvar string format. C# `G9` differs
  in edge cases (trailing zeros, exponent form). Add a dedicated formatter + a round-trip parity test;
  getting this wrong makes rpn-computed cvars subtly diverge.
- **R3 — RPN scope.** The DB family (dbpush…dbgoto) + time/digest/fexists/eval are large and essentially
  unused by shipped cfgs/quickmenu (which use arithmetic + cvar read/write + set ops). Implementing the core
  first is the right call; explicitly NOTE any omitted op rather than silently dropping it (a cfg that calls a
  missing op should print a clear "rpn: unknown op" rather than mis-pushing a cvar value — but DP's actual
  fallback IS "push cvar_string(token)", so an unimplemented op would read a (likely empty) cvar; preserve
  that fallback to stay faithful, and log when developer>0).
- **R4 — maplist `cleanup`/`add` map existence.** QC checks `maps/<m>.bsp` + `MapInfo_CheckMap`. The console
  layer may not have a map catalog; `add`/`remove`/`shuffle` are pure string ops (do faithfully), but
  `cleanup`'s filter needs the catalog — fall back to identity (keep all words) if unavailable and note it.
- **R5 — `cons`/FOREACH_WORD semantics.** `cons("", x)`=="x" (no leading space), `cons(a,"")`=="a", words are
  space-separated and de-duplicated by exact match (addtolist). A naive `string.Join(" ")` that leaves a
  leading space will diverge from QC (which `substring`s it off). Mirror `cons` exactly.
- **R6 — editmob look-at trace.** QC uses `WarpZone_TraceLine(origin+view_ofs, +v_forward*100)`. The port's
  trace is `Api.Trace`; use the player's view origin + forward (the port has makevectors-equivalent). The
  ownership/visibility gates depend on `trace_ent` being the monster — verify the trace returns the monster
  entity.
- **R7 — voice/taunt assets.** Full voice parity needs the taunt sample registry (`allvoicesamples`/
  GetVoiceMessage). If that registry isn't ported, scope `voice` to validation + dead/spec gates + a
  by-name sound emit; do NOT claim full parity. VERIFY the asset/sound side before sizing.
- **R8 — rankings/records/ladder need the race records DB.** These are race/CTS/CTF-specific and read
  `ServerProgsDB`. If the port's persistent records store isn't wired for the active mode, the honest faithful
  output is the QC empty case ("No records available…"); don't fabricate. Confirm the records API before
  promising populated output.
- **R9 — placement (client vs console).** Putting rpn/maplist/addtolist on the SHARED ConsoleCommands (not the
  server Commands) is the faithful choice (QC: all programs). But the in-game console's `localRouter`
  (Shell.cs:495) routes UNKNOWN commands to `world.Commands` — so a console `rpn …` will be caught by the
  ConsoleCommands registered handler FIRST (interpreter consults registered commands before the unknown router,
  ConfigInterpreter.cs:271), which is correct. Just ensure rpn/maplist are registered on the interpreter so they
  don't fall through to the server bus (which won't have them). Server-side `cmd rpn` (a client typing it) would
  go to `world.Commands` and miss — but QC routes generic commands through `qc_cmd_svcl` on BOTH; if full server
  `cmd rpn` parity is wanted, ALSO register the generic family on Commands.cs (cheap: same handler). Decide and
  note; the common path (console/cfg) is covered by ConsoleCommands.
