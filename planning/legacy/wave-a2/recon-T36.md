# Recon T36 — Untracked-mode map-objective spawnfuncs (Assault / Nexball / Invasion / CTS, + KeyHunt note)

**Status of the gap (verified):** The gametype LOGIC for Assault, Nexball, Invasion, CTS is fully ported and unit-tested, but their MAP ENTITIES never spawn. Two seams handle objective spawns today and BOTH only know CTF / Domination / Race / Onslaught:

- `MapObjectsRegistry.RegisterAll()` (src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs:170-186) registers `item_flag_team1..4`, `item_flag_neutral`, `dom_controlpoint`, `team_dom_point`, `trigger_race_checkpoint`, `trigger_race_penalty`, `onslaught_generator/controlpoint/link` — and nothing for assault/nexball/invasion/cts.
- `GameWorld.WireObjectiveSpawns()` (src/XonoticGodot.Server/GameWorld.cs:1032-1080) only switches on `Ctf`, `Domination`, `Race`, `Onslaught`. The `_ => null` default means an Assault/Nexball/Invasion/Cts match clears the sink → every objective placement is silently dropped.

Net effect: an assault/nexball/invasion/cts map has zero objectives → no win path. Grep confirms NO assault/nexball/invasion/cts classname is registered anywhere (the only references are the gametype `.cs` doc-comments + a bot role that *looks for* `func_assault_destructible` by classname but nothing spawns it: src/XonoticGodot.Server/Bot/BotObjectiveRoles.cs:316,325).

---

## How the port's spawn pipeline works (the pattern to mirror)

1. **Registration is two-tiered.** Gametypes auto-register by the `[GameType]` reflection attribute via `GameRegistries.Bootstrap()` (Registry.cs:21 `GameTypeAttribute`; GameInit.cs:35). Assault/Nexball/Invasion/Cts/KeyHunt are ALL already `[GameType]` and discovered — **no Registries.cs edit is needed** (the A1 lesson holds). What's missing is the *spawnfunc → gametype API* routing, which is plain `SpawnFuncs.Register(...)` table edits + a `WireObjectiveSpawns` switch arm.

2. **Map-entity spawn loop** (GameWorld.SpawnMapEntities, GameWorld.cs:1451-1495):
   - For each `EntityDict`, `Api.Entities.Spawn()` → `e.ClassName = cls` → `ApplyDictFields(e, dict)` → `SpawnFuncs.TrySpawn(cls, e)`.
   - `ApplyDictFields` (GameWorld.cs:1526-1570) maps these map keys onto the edict: `angle`/`angles`, `targetname`→`e.Target Name`, `target`→`e.Target`, `killtarget`, `message`→`e.Message`, `model`, `spawnflags`, `team`→`e.Team` (float), `skin`, **`spawnmob`→`e.Spawnmob`**, **`count`→`e.Count` (int.TryParse)**, `monster_moveflags`, `noalign`, `monster_skill`.
   - **NOT mapped (T36 must add):** `cnt` (QC `.cnt`), `dmg` (QC `.dmg`), `health` (QC `.health`). See the FIELD-MAPPING GOTCHAS section.

3. **The "stateless spawnfunc → Sink" bridge** is the established pattern (mirror it exactly):
   - `WarpzoneSpawns` (Warpzone.cs:415-422): `public static Action<Entity>? Sink;` + `void TriggerWarpzoneSetup(Entity e){ e.ClassName="trigger_warpzone"; Sink?.Invoke(e); }`. Wired in `GameWorld.Boot` step 1: `WarpzoneSpawns.Sink = Warpzones.OnMapEntity;` (GameWorld.cs:304).
   - `GametypeObjectiveSpawns` (MapObjectsRegistry.cs:209-239): `public static Action<Entity>? Sink;` + per-classname `Emit(e, className, team)` helpers. Wired to the active gametype in `GameWorld.WireObjectiveSpawns` (GameWorld.cs:1036).
   - The lump spawns a placeholder edict, the sink reads `e.Origin/e.Angles/e.Team/e.Count/...` and calls the gametype's own `SpawnXxx` API to make the real objective, then `RetirePlaceholder(e)` removes the placeholder (GameWorld.cs:1086-1090).

**Decision for T36: extend `GametypeObjectiveSpawns` (add the new classnames there) + add the four new gametype arms in `WireObjectiveSpawns`.** This keeps every objective spawnfunc in one place and reuses `RetirePlaceholder`. (Alternative: a parallel `Nexball/Assault/...Spawns` class each — rejected; `GametypeObjectiveSpawns` already is the shared home and the orchestrator only has to hand out one hot-file snippet.)

---

## BASE SPEC (read in full) + the port API each spawnfunc must call

### ASSAULT — Base/.../assault/sv_assault.qc (+ sv_assault.qh)
Constants: `ASSAULT_VALUE_INACTIVE = 1000` (qh:7) — **NOTE the port uses `Assault.ObjectiveInactive = 1000000f`**, a deliberate scaling diff; the port's `Active`/`Destroyed` predicates use it consistently, so do NOT feed QC's 1000 in.

Spawnfuncs (all gated `if(!g_assault){delete;return;}`):
- `spawnfunc(target_objective)` (qc:303): `IL_PUSH(g_assault_objectives)`, `use=assault_objective_use`, `reset=assault_objective_reset` (sets health=INACTIVE), `spawn_evalfunc`. Reads `.targetname`, `.target` (next objective). → port `Assault.AddObjective(name=e.TargetName, target=e.Target)`.
- `spawnfunc(target_objective_decrease)` (qc:314): `IL_PUSH(g_assault_objectivedecreasers)`, **`if(!this.dmg) this.dmg=101;`** (DEFAULT 101, not 100), `use=assault_objective_decrease_use`, health=INACTIVE, then `InitializeEntity(target_objective_decrease_findtarget, INITPRIO_FINDTARGET)` → `assault_setenemytoobjective` resolves `.target`→`.enemy` (the objective) **deferred, after all spawn**. → port `Assault.AddDecreaser(objectiveName=e.Target, dmg = e.Dmg>0?e.Dmg:101)`.
- `spawnfunc(func_assault_destructible)` (qc:348): `spawnflags=3`, `event_heal=destructible_heal`, `IL_PUSH(g_assault_destructibles)`, team = the DEFENDER team (opposite attacker), `func_breakable_setup`. Reads `.health` (via breakable setup; default 100), `.target` (= the *decreaser* targetname it triggers). → port `Assault.AddDestructible(decreaser, health, active:false)` — **but the port API takes a `Decreaser` object, not a name**, so the destructible→decreaser link must be resolved by name in a deferred pass (see RISKS).
- `spawnfunc(func_assault_wall)` (qc:364): a wall whose model toggles with its `.enemy` objective health (think `assault_wall_think`). `InitializeEntity(assault_setenemytoobjective)`. **Port has NO API for func_assault_wall** (it's cosmetic collision-toggle; the visual/solid toggle is deferred presentation). Recommend: register the spawnfunc so the edict is consumed (no "unhandled" spam) but treat as a no-op objective (or a thin "wall toggles with objective" if time permits — LOW priority, it never gates the win path).
- `spawnfunc(target_assault_roundend)` (qc:376): `winning=0`, `use=target_assault_roundend_use` (sets winning=1), `cnt=0`, `reset`. → port `Assault.AddRoundEnd()` (sets `HasRoundEnd=true`).
- `spawnfunc(target_assault_roundstart)` (qc:386): sets `assault_attacker_team=NUM_TEAM_1`, `use=assault_roundstart_use`, `InitializeEntity(assault_roundstart_use_this)`. Re-spawns/teamswaps turrets. **Port: `Assault.State.AttackerTeam` already defaults to `Teams.Red` in `Activate()`**; the turret teamswap is cross-boundary-deferred. Register the spawnfunc to consume the edict; the only must-do is ensure attacker=Red (already done). Optionally call a future `Assault.ArmRound()`/`ResetObjectives()` so the first objective is active at match start.
- `spawnfunc(info_player_attacker)` (qc:287) / `info_player_defender` (qc:295): a deathmatch spawn with `.team = NUM_TEAM_1` (attacker) / `NUM_TEAM_2` (defender). These are SPAWN POINTS, not objectives. **The port's spawn system already handles `info_player_*` generically** (BuildGametypeContext reads `info_player_*` team, GameWorld.cs:1509). Best handling: register thin spawnfuncs that set `e.Team` to Red/Blue and re-dispatch to the deathmatch spawn classname — OR (simpler/parity-safe) leave them to the generic spawn handler and just confirm they're picked up. VERIFY against `SpawnSystem`/the info_player_deathmatch spawnfunc (T36 should check it doesn't need explicit registration; if it does, register them mapping to the team + deathmatch-spawn path).

Drive path once spawned: `Combat.Damage` of a `func_assault_destructible` → `Assault.DamageDestructible(wall, byTeam, amount, actor)` → on destroy `DecreaseObjective` → activates next objective or `DestroyFinalObjective` → round win. **The damage→destructible wiring is the live hook** — confirm the breakable/damage path calls `DamageDestructible` (this is the boundary with the breakable system; `func_breakable` is already registered at MapObjectsRegistry.cs:125, and Breakable.cs notes "func_breakable = func_assault_destructible for general use"). T36 scope is SPAWNING; the damage routing may already exist via the breakable death hook or may need a thin bridge — VERIFY and note for the integrator.

### NEXBALL — Base/.../nexball/sv_nexball.qc (+ sv_nexball.qh)
Goal-type sentinels (qh:33-34): **QC `GOAL_FAULT = -1`, `GOAL_OUT = -2`**. **The port uses DIFFERENT sentinels: `Nexball.GoalFault = -2`, `Nexball.GoalOut = -3`** (Nexball.cs:74-76). The spawnfunc must translate to the PORT's constants, never QC's raw numbers.

Goal spawnfuncs → all funnel to `SpawnGoal(this)` (qc:623) which `EXACTTRIGGER_INIT`, sets `classname="nexball_goal"`, `settouch(GoalTouch)`:
- `nexball_redgoal` (qc:643): `team=NUM_TEAM_1` → port `Nexball.SpawnGoal(Teams.Red, e.Origin)`.
- `nexball_bluegoal` (qc:648): `team=NUM_TEAM_2` → `SpawnGoal(Teams.Blue, ...)`.
- `nexball_yellowgoal` (qc:653): `team=NUM_TEAM_3` → `SpawnGoal(Teams.Yellow, ...)`.
- `nexball_pinkgoal` (qc:658): `team=NUM_TEAM_4` → `SpawnGoal(Teams.Pink, ...)`.
- `nexball_fault` (qc:664): `team=GOAL_FAULT` → `SpawnGoal(Nexball.GoalFault, ...)`.
- `nexball_out` (qc:672): `team=GOAL_OUT` → `SpawnGoal(Nexball.GoalOut, ...)`.
- Compat aliases (qc:684-712): `ball`→football, `ball_football`→football, `ball_basketball`→basketball, **`ball_redgoal`→bluegoal, `ball_bluegoal`→redgoal (INTENTIONALLY SWAPPED — "I blame Revenant"), `ball_fault`→fault, `ball_bound`→out**. Mirror the swap exactly.

Ball spawnfuncs → `SpawnBall(this)` (qc:525) relocates + sets bounce/think:
- `nexball_basketball` (qc:580): `solid=SOLID_TRIGGER`, `balls|=BALL_BASKET`, bounce/pushable. → port `Nexball.SpawnBall(e.Origin)` (port spawns a "nexball_basketball" by default; sets `BallHome`). **Set `nb.BallHome = e.Origin` after SpawnBall** so ResetBall returns it home (the port's SpawnBall does NOT set BallHome — verify; Nexball.cs:108-119 sets BallEntity but BallHome is a separate settable prop at :122).
- `nexball_football` (qc:603): same but `balls|=BALL_FOOT`. Port's `SpawnBall` hardcodes classname "nexball_basketball"; football vs basketball touch differs in QC (football can't be carried). For P0 parity the port models a single ball with pickup-on-touch (basketball semantics). NOTE this as a known simplification; the win path (goal scoring) is identical.

Goal team mapping summary: red goal = NUM_TEAM_1 (defended by blue; a ball in red goal scores for whoever's ball.team is). Port's `GoalTouch`/`ScoreGoal` already implement own-goal/fault/enemy-goal scoring (Nexball.cs:204-321) — the spawnfunc just has to place the goals with the right team/sentinel.

### INVASION — Base/.../invasion/sv_invasion.qc (+ sv_invasion.qh)
Spawnfuncs (all gated `if(!g_invasion){delete;return;}`):
- `spawnfunc(invasion_spawnpoint)` (qc:65): `IL_PUSH(g_invasion_spawns)`. → port `Invasion.AddSpawnPoint(e.Origin)`.
- `spawnfunc(invasion_wave)` (qc:58): `IL_PUSH(g_invasion_waves)`. Reads QC **`.cnt` (wave number)** + **`.spawnmob` (monster list)** (used in `invasion_GetWaveEntity` qc:156 `it.cnt==wavenum`, and `invasion_SpawnChosenMonster` qc:181 `wave_ent.spawnmob`). → port `Invasion.AddWave(waveNumber=e.Cnt, spawnmob=e.Spawnmob)`. **`.cnt` is NOT mapped by ApplyDictFields — see GOTCHAS.** `.spawnmob` IS mapped.
- `spawnfunc(target_invasion_roundend)` (qc:45): `victent_present=true`, **`if(!this.count) this.count=0.7;`** (70% threshold), `use=target_invasion_roundend_use`, `IL_PUSH(g_invasion_roundends)`. → port `Invasion.AddRoundEnd()` (the port hardcodes the 0.7 threshold internally, so `.count` need not be plumbed — `AddRoundEnd()` is parameterless; `Invasion.cs:119`). The STAGE win fires via `Invasion.TriggerRoundEnd()` from a touch — but the port's `AddRoundEnd()` only increments a counter; the actual `target_invasion_roundend_use` touch→`TriggerRoundEnd` wiring is the live hook. T36 should give the roundend placeholder a touch that calls `inv.TriggerRoundEnd()` (only meaningful in STAGE), OR note that the trigger-touch is a follow-up (STAGE is a minority type; ROUND is the default and needs only spawnpoints+waves).

### CTS — Base/.../cts/sv_cts.qc + Base/.../server/race.qc
CTS shares race.qc's timer triggers. The `target_startTimer`/`target_stopTimer` are spawned by `target_checkpoint_setup` (race.qc:1142) gated `if(!g_race && !g_cts){delete;return;}`:
- `spawnfunc(target_startTimer)` (race.qc:1200) → `target_checkpoint_setup`: sets `race_checkpoint=0` (start), `defrag_ents=1`, touch=`checkpoint_touch`. → port `Cts.SpawnStartTimer(e.Origin)`.
- `spawnfunc(target_stopTimer)` (race.qc:1201) → `target_checkpoint_setup`: `race_checkpoint=-2` (finish, resolved later), touch. → port `Cts.SpawnStopTimer(e.Origin)`.
- `spawnfunc(target_checkpoint)` (race.qc:1193) → defrag checkpoint (intermediate). **Port CTS models only start/stop** (no intermediate checkpoints — `Cts` is a single start→stop course, Cts.cs doc:13). For T36, register `target_checkpoint` for CTS as a no-op consume (or skip; defrag intermediate CPs are a Race feature). The win/timing path uses only start+stop.
- `spawnfunc(trigger_race_checkpoint)` (race.qc:1086) gated `if(!g_race && !g_cts)` — already routed to `Race.SpawnCheckpoint` via `GametypeObjectiveSpawns.RaceCheckpoint` (MapObjectsRegistry.cs:180). **In a CTS match the active gametype is `Cts`, not `Race`, so that arm's `Race race =>` won't match.** If a CTS map uses `trigger_race_checkpoint`, it's currently dropped. Low priority (CTS maps use start/stop timers); note it.

The port's `Cts.OnFinishRetract` is already wired in GameWorld.ActivateGameType (GameWorld.cs:1013-1016). The start/stop touch handlers (`StartTimer`/`FinishStage`) are baked into `SpawnStartTimer`/`SpawnStopTimer` (Cts.cs:114,132) — so once the entity spawns, the touch path is live. Good.

### KEYHUNT (lower impact — NOTE only) — Base/.../keyhunt/sv_keyhunt.qc
`item_kh_key` is **NOT a map spawnfunc** — there is no `spawnfunc(item_kh_key)` in QC. Keys are created at round start by `kh_Key_Spawn` (qc:731) onto a random teammate (`kh_StartRound` qc:907). The port already does this: `KeyHunt.SpawnKey` (KeyHunt.cs:463 spawns classname "item_kh_key") is called from `KeyHunt.StartRound`, and GameWorld wires `kh.SetRoster(...)` + `EnableRounds()` (GameWorld.cs:975-979). **So KeyHunt needs NO new spawnfunc** — its objective layer already spawns at round start. Confirm: KeyHunt is the one mode in this task that is already complete on the spawn axis. (Do NOT add an `item_kh_key` map spawnfunc; that would double-spawn.)

---

## FIELD-MAPPING GOTCHAS (must fix in ApplyDictFields — this is a hot-file edit)

`GameWorld.ApplyDictFields` (GameWorld.cs:1526-1570) is the only place map keys become edict fields. For T36:
1. **`cnt`** (QC `.cnt`, invasion_wave wave number) — currently UNMAPPED. `Entity.Cnt` exists (int, MapObjectsCommon.cs:54). Add: `if (f.TryGetValue("cnt", out var c) && int.TryParse(c, out int ci)) e.Cnt = ci;`. Without this, `invasion_wave` placements all read wave 0.
2. **`dmg`** (QC `.dmg`, target_objective_decrease damage) — UNMAPPED. There is NO `Entity.Dmg` field on the base or a shared partial usable here (MapObjectsCommon's `Dmg` is on a *MapObjectState* side-struct, not `Entity`; Assault.Decreaser.Dmg is the gametype struct). Cleanest: the Assault sink reads `dmg` straight from `dict.Fields` is NOT possible (the sink only gets the `Entity`, not the dict). Two options: (a) add `e.Dmg` mapping (needs an `Entity.Dmg` field — promote one in a partial, but `Dmg` collides conceptually with mover dmg) OR (b) reuse `e.Count`/`e.Cnt` is wrong. RECOMMEND: route `dmg` into an existing edict field the sink can read — simplest is to map `dmg`→a small promoted `Entity.GtObjDmg`-style field, OR just default to 101 in the spawnfunc (QC's default is 101; most assault maps rely on the default). **Pragmatic P0: default decreaser dmg to 101 and skip plumbing `.dmg` unless a target map needs a custom value.** Note the deviation.
3. **`health`** (QC `.health` on target_objective/func_assault_destructible) — `e.Health` IS on the base Entity (Entity.cs:52) but is NOT set from the `health` map key in ApplyDictFields. Add: `if (f.TryGetValue("health", out var hp) && float.TryParse(hp, ..., out float hpf)) e.Health = hpf;`. func_assault_destructible default health is 100 (breakable setup). Map it so a map's custom objective/wall health is honored.

These three are SHARED edits to `ApplyDictFields` (GameWorld.cs) — list in hotFileNeeds. Minimal, additive, no behavior change for existing classes.

---

## IMPLEMENTATION PLAN (mirrors Base)

1. **ApplyDictFields (GameWorld.cs):** add `cnt`→`e.Cnt`, `health`→`e.Health` mappings (and optionally `dmg` if a field is provided). (hot file — see hotFileNeeds.)

2. **GametypeObjectiveSpawns (MapObjectsRegistry.cs):** add the new classname spawnfuncs as thin `Emit`-style bridges that tag classname + team and invoke `Sink`:
   - Assault: `TargetObjective`, `TargetObjectiveDecrease`, `FuncAssaultDestructible`, `FuncAssaultWall`, `TargetAssaultRoundend`, `TargetAssaultRoundstart`, `InfoPlayerAttacker`, `InfoPlayerDefender`.
   - Nexball: `NexballRedgoal/Bluegoal/Yellowgoal/Pinkgoal/Fault/Out`, `NexballBasketball`, `NexballFootball`, + the `ball*` compat aliases (with the red/blue swap).
   - Invasion: `InvasionSpawnpoint`, `InvasionWave`, `TargetInvasionRoundend`.
   - CTS: `TargetStartTimer`, `TargetStopTimer` (+ `target_checkpoint` no-op consume).
   (These set team where QC does — e.g. nexball goals set NUM_TEAM_n; assault info_player_attacker=Red.)

3. **MapObjectsRegistry.RegisterAll (MapObjectsRegistry.cs ~186):** register every classname above → its `GametypeObjectiveSpawns.Xxx`. (hot file — but T36-owned per the task split; the orchestrator gave T36 MapObjectsRegistry.cs.)

4. **GameWorld.WireObjectiveSpawns (GameWorld.cs:1032-1080):** add four switch arms:
   - `Assault aslt =>` route by classname: target_objective→`AddObjective(e.TargetName, e.Target)`; target_objective_decrease→`AddDecreaser(e.Target, dmg:101 or e-derived)`; func_assault_destructible→record (name=e.Target) for deferred link; target_assault_roundend→`AddRoundEnd()`; roundstart→`ResetObjectives()`/no-op; info_player_attacker/defender→spawn-point handling. Then `RetirePlaceholder(e)`.
   - `Nexball nb =>` route by classname → `SpawnGoal(team-or-sentinel, e.Origin)` for goals; `SpawnBall(e.Origin)` + `nb.BallHome = e.Origin` for balls. `RetirePlaceholder`.
   - `Invasion inv =>` invasion_spawnpoint→`AddSpawnPoint(e.Origin)`; invasion_wave→`AddWave(e.Cnt, e.Spawnmob)`; target_invasion_roundend→`AddRoundEnd()` (+ give the placeholder/edict a touch→`TriggerRoundEnd` for STAGE, or note as follow-up). `RetirePlaceholder` for the wave/roundend control ents; spawnpoints can be retired too (origin already captured).
   - `Cts cts =>` target_startTimer→`SpawnStartTimer(e.Origin)`; target_stopTimer→`SpawnStopTimer(e.Origin)`; target_checkpoint→no-op. `RetirePlaceholder`.

5. **Assault deferred target resolution:** because spawn order is arbitrary and the port's `AddDestructible` takes a `Decreaser` *object* (not a name), the Assault arm must DEFER the destructible→decreaser→objective linking until after the whole lump spawns. Options:
   - (a) Add `MapObjectsRegistry.RunPostSpawn()` call to `GameWorld.Boot` after `SpawnMapEntities()` (currently MISSING from GameWorld — only GameDemo.cs:713 calls it!) and have the Assault sink stash raw (classname, name, target, health) tuples, resolving them in a new `Assault.ResolveObjectiveGraph()` invoked from a post-spawn hook. This ALSO fixes the latent door-deferred-link gap in the server path (Doors.RunDeferredLinks never runs in GameWorld today).
   - (b) Keep resolution inside `WireObjectiveSpawns`/the sink by collecting placeholders into a list and resolving at the END of `SpawnMapEntities` (a dedicated `Assault.FinishSpawn()` call right after the loop).
   RECOMMEND (b) scoped tightly, OR (a) if the orchestrator wants the door-link gap closed too (flag it). Either way the resolution must run AFTER all assault edicts are seen.

6. **Tests:** add `AssaultSpawnTests`, `NexballSpawnTests`, `InvasionSpawnTests`, `CtsSpawnTests` that build a `GameWorld` (or call `GametypeObjectiveSpawns.Sink` directly like the warpzone test) with the right gametype + a handful of `EntityDict`s and assert the gametype's collections populate (Objectives/Decreasers/Destructibles, Goals, SpawnPoints/_waves, Timers) and a minimal win path fires (e.g. damage the destructible → DestroyFinalObjective; ball into enemy goal → +1; spawn a monster from a spawnpoint; cross start then stop → fastest time set).

---

## CONFLICTS / HOT FILES

- **`src/XonoticGodot.Server/GameWorld.cs`** — owned by T36 for `WireObjectiveSpawns` + `ApplyDictFields`. **Conflict risk:** the task split lists GameWorld.cs under T36 (mode-objective spawnfuncs). Other A2 tasks touching GameWorld: T37 (vehicle seam — ServerNet/NetGame/Simulation, may touch GameWorld), T43 (DamageSystem — unlikely GameWorld). The `WireObjectiveSpawns` switch + `ApplyDictFields` are isolated regions; give the orchestrator the exact edits so one owner integrates. ApplyDictFields additions (`cnt`/`health`) are pure additive lines.
- **`src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs`** — T36-owned per the split. Additive `SpawnFuncs.Register(...)` lines in `RegisterAll` + new methods on `GametypeObjectiveSpawns`. No other A2 task is listed against it.
- **Gametype files (Assault.cs / Nexball.cs / Invasion.cs / Cts.cs)** — T36-owned. May need a small additive method (`Assault.ResolveObjectiveGraph`/`FinishSpawn`, set `Nexball.BallHome`). T51 owns *Mutators/Weapons*, not these gametype files — no overlap.
- **No Registries.cs edit** (auto-registration confirmed). **No DamageSystem.cs / MutatorHooks.cs / ServerNet.cs / NetGame.cs edits** for spawning. The Assault destructible→damage routing MAY touch the breakable/damage path (DamageSystem is T43's) — but `func_breakable` already exists and the destructible damage bridge can live in the Assault gametype/Breakable side; if a DamageSystem hook is truly needed, hand T43 a one-line snippet rather than editing it here.

---

## RISKS / PARITY TRAPS

1. **Goal sentinel mismatch (Nexball):** QC GOAL_FAULT=-1/GOAL_OUT=-2 vs port GoalFault=-2/GoalOut=-3. Use `Nexball.GoalFault`/`Nexball.GoalOut` constants, never the raw numbers. A wrong sentinel turns a fault into a real team goal (=-2 collides).
2. **`ball_redgoal`/`ball_bluegoal` are swapped on purpose** (qc:697-704). Preserve the swap.
3. **`ObjectiveInactive` scaling:** port uses 1e6, QC uses 1000. Don't pass QC's 1000 anywhere; rely on the port's constant + its `Active`/`Destroyed` predicates.
4. **Decreaser default dmg = 101** (not 100) in QC. The port's `Decreaser.Dmg` defaults to 100f; a destructible chain whose objective has 100 health needs dmg>health to destroy — with 100 vs 100 the QC `hlth - dmg > 0.5` branch (qc:64) means 100-100=0 ≤0.5 → destroyed, so 100 also works, but use 101 to match.
5. **Spawn-order / deferred resolution (Assault):** `AddDecreaser` resolves the objective by name AT CALL TIME (`Objectives.Find`, Assault.cs:144) and `AddDestructible` needs a `Decreaser` object. A destructible/decreaser spawned before its objective/decreaser will fail to link. MUST resolve after the full lump (QC does this via INITPRIO_FINDTARGET). The destructible→decreaser name link has NO port API yet (AddDestructible takes the object) — add a name-keyed deferred resolver.
6. **`RunPostSpawn` is not called by GameWorld.Boot** (only GameDemo.cs). If T36 uses the door-style deferred-link mechanism for Assault, it must also add the `RunPostSpawn` call to Boot — which incidentally fixes the latent door double/quad-link gap in the headless server path. Flag this to the orchestrator (it's a real pre-existing bug, but adding the call changes door behavior in GameWorld for the first time — verify no double-link).
7. **`.cnt` vs `.count`:** invasion_wave uses `.cnt` (UNMAPPED), target_invasion_roundend uses `.count` (mapped to `e.Count` but unused by the port). Don't confuse them — `e.Count` (from `count`) and `e.Cnt` (from `cnt`, must be added) are different fields.
8. **info_player_attacker/defender are spawn points, not objectives.** Routing them through the objective sink would be wrong (the gametype has no AddSpawnPoint for players). Verify the generic spawn handler already accepts them (they fall through `SpawnFuncs.TrySpawn` → kept as findable edicts; SpawnSystem reads `info_player_*`). If they need team-tagging, a thin spawnfunc that sets team + re-dispatches to the deathmatch-spawn classname is the faithful port (qc:287 calls `spawnfunc_info_player_deathmatch`). Check how the port registers `info_player_deathmatch`/team spawns before adding.
9. **Nexball `SpawnBall` doesn't set `BallHome`** (Nexball.cs:108-119 leaves BallHome at default; ResetBall returns to BallHome). The spawnfunc MUST set `nb.BallHome = e.Origin` or the ball resets to the world origin after the first goal.
10. **STAGE/HUNT invasion variants** need the `target_invasion_roundend` touch→`TriggerRoundEnd` wiring; the default ROUND type only needs spawnpoints + waves. Scope P0 to ROUND (spawnpoints+waves), note STAGE roundend-touch as a thin follow-up.
11. **Damage→destructible routing (Assault):** spawning the destructible is necessary but not sufficient for the win path; something must call `Assault.DamageDestructible` when a `func_assault_destructible` takes damage. Confirm whether the breakable death hook already reaches it or a bridge is needed (cross-boundary with the breakable/damage system).

## CONFIDENCE
Medium-high on the spawn-routing core (the pattern is well-established and the gametype APIs exist + are tested). Medium on the Assault target-graph deferred resolution + the damage→destructible live-hook (those touch ordering + the breakable boundary). KeyHunt confirmed out-of-scope on the spawn axis (already round-spawns).
