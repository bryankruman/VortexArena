# Recon T37 — Vehicle runtime seam: board / input / impulse / return

**Scope:** wire the already-ported (and ORPHANED) Racer/Raptor/Spiderbot/Bumblebee so a hand-placed
vehicle can be **boarded**, **driven** (the seated player's input reaches `*_frame`), have its **impulses**
routed to mode-switch/chase, and **return** (already mostly handled by the descriptor `Death()` → respawn).

**TL;DR of the gap.** The entire deep per-vehicle behaviour is faithfully ported and compiles against real
helpers. What is missing is *runtime plumbing*, four seams:
1. **board** — nothing calls `VehicleCommon.EnterVehicle` / `Bumblebee.GunnerEnter`. There is no
   `PlayerUseKey` in the port, no radius auto-enter, and the player slide-move does NOT dual-dispatch
   `.Touch` onto solids, so even the bumblebee body-touch path can't fire.
2. **input** — `Entity.VehInput` is **never written**. Every `*_frame` reads `vehicle.VehInput` (zeroed),
   so a boarded vehicle would sit dead-still / never fire even if you could board it.
3. **drive gate** — `GameWorld.OnClientMove` runs `Movement.Move(p,input)` (PM_Main) unconditionally for a
   live player; QC instead runs the player's `PlayerPhysplug` (= the vehicle frame) when seated, and skips
   PM_Main. The seated player must NOT run PM_Main.
4. **impulse** — `Commands.DispatchImpulse` goes straight to `WeaponImpulses.Handle`; when the caller is
   seated it must instead route to the vehicle's impulse handler (Raptor/Spiderbot `CycleMode`/`SetMode`,
   and the `weapon_drop` chase toggle), exactly like QC `vehicle_impulse` runs before weapon impulses.

The vehicle `Think()` ALREADY ticks automatically: `vehicle_initialize`/`vr_spawn` is reproduced in the
descriptor `Spawn()` which sets `vehicle.Think` + `NextThink`, and `SimulationLoop.Tick → MoveTypePhysics.
RunEntity → RunThink` fires it for every non-client entity. So regen, idle hover, death/blowup/respawn,
projectile guidance all already run once a vehicle is spawned. The spawnfuncs are even registered
(`MapObjectsRegistry.cs:159-162`). The vehicle is *spawned and self-thinking but unboardable and inert to a
pilot* — the textbook "dead code between spawned and playable".

---

## 1. BASE SPEC (read in full)

### 1.1 sv_vehicles.qc — shared enter/exit/touch/impulse/return/init

**`vehicles_touch(this, toucher)` (~874).**
```
if (MUTATOR_CALLHOOK(VehicleTouch, this, toucher)) return;   // hook can suppress
if (this.owner) {                                            // vehicle in use
    if (toucher && this.origin.z+this.maxs.z > toucher.origin.z
        && vehicles_crushable(toucher) && !weaponLocked(this.owner)) {
        if (vdist(this.velocity, >=, g_vehicles_crush_minspeed))   // default 100
            Damage(toucher, this, this.owner, g_vehicles_crush_dmg/*70*/, DEATH_VH_CRUSH, ...,
                   normalize(toucher.origin-this.origin)*g_vehicles_crush_force/*50*/);
        return;                                              // no self-damage on soft targets
    }
    if (this.play_time < time) info.vr_impact(info, this);   // ground-impact pain
    return;
}
if (!autocvar_g_vehicles_enter) vehicles_enter(toucher, this); // TOUCH-mode board only
```
Key: **touch-mode board only happens when `g_vehicles_enter == 0`.** Default is `1` (use-key mode), where
`vehicles_touch` does NOT enter — boarding is via `PlayerUseKey`. `vehicles_crushable(e)` = `IS_PLAYER(e) &&
time >= e.vehicle_enter_delay`, or `IS_MONSTER(e)`.

**`vehicles_enter(pl, veh)` (~931).** Guards then attaches:
```
if (IS_BOT_CLIENT(pl) && !g_vehicles_allow_bots) return;
if (!IS_PLAYER(pl) || veh.phase>=time || pl.vehicle_enter_delay>=time
    || FROZEN || IS_DEAD(pl) || pl.vehicle) return;
// MULTISLOT + already-owned + same-team → gunner enter (bumblebee):
if (g_vehicles_enter && (veh.vehicle_flags & VHF_MULTISLOT) && veh.owner && SAME_TEAM(pl,veh))
    info.vr_gunner_enter(info, veh, pl);
if (veh.owner) return;                       // got seated as gunner (or full) → done
if (teamplay && veh.team) if (DIFF_TEAM) { if (g_vehicles_steal) {steal waypoints…} else return; }
RemoveGrapplingHooks(pl);
veh.vehicle_ammo1=ammo2=reload1=reload2=energy=0;
veh.owner = pl;  pl.vehicle = veh;           // *** the link ***
veh.vehicle_hudmodel.viewmodelforclient = pl;
UNSET_DUCKED(pl); pl.view_ofs = PL_VIEW_OFS; setsize(pl, PL_MIN, PL_MAX);
veh.event_damage=vehicles_damage; veh.event_heal=vehicles_heal; veh.nextthink=0;  // *** veh stops self-think ***
pl.items &= ~IT_USING_JETPACK; pl.angles=veh.angles;
pl.takedamage=DAMAGE_NO; pl.solid=SOLID_NOT; pl.disableclientprediction=1;
set_movetype(pl, MOVETYPE_NOCLIP); pl.teleportable=false; pl.alpha=-1;
pl.event_damage=func_null; pl.view_ofs='0 0 0';
veh.colormap = pl.colormap; (+tur_head)
for slot: veh.(weaponentity)=new(temp_wepent); copy m_switchweapon
STAT(HUD,pl)=veh.vehicleid;  pl.PlayerPhysplug = veh.PlayerPhysplug;   // *** the drive gate ***
pl.vehicle_ammo1/ammo2/reload1/reload2/energy = veh.(same)
UNSET_ONGROUND(pl); UNSET_ONGROUND(veh);
veh.team = pl.team; veh.flags -= FL_NOTARGET;
vehicles_reset_colors(veh, pl);
…SVC_SETVIEWPORT/SETVIEWANGLES networking… CSQCVehicleSetup(pl, veh.vehicleid);
MUTATOR_CALLHOOK(VehicleEnter, pl, veh);     // *** hook ***
CSQCModel_UnlinkEntity(veh); info.vr_enter(info, veh);  // per-vehicle enter (movetype etc.)
antilag_clear(pl, CS(pl));
```
The PORT's `VehicleCommon.EnterVehicle(vehic, player)` already does the server-authoritative core
(owner/vehicle link, NextThink=0, takedamage/solid/movetype NONE, velocity 0, view_ofs 0, team, ammo-reset),
and each descriptor `Enter()` calls it then applies the per-vehicle movetype + HUD seed. **It does NOT call
the guards, the gunner-enter branch, RRemoveGrapplingHooks, or the VehicleEnter hook** (all noted as
cross-boundary TODOs in VehicleCommon.cs:210). The wiring task should add the *guards* + the gunner-enter
branch + the hook at the call-site (a new `Vehicles` boarding helper), not edit the descriptors.

**`vehicles_exit(vehic, eject)` (~775).** The reverse: restore player to WALK/SLIDEBOX/DAMAGE_AIM, drop
link, set `vehicle_enter_delay = time+2`, `last_vehiclecheck = time+3`, restore view_ofs/size, MULTISLOT
slot path calls `vehic.vehicle_exit` and returns, `MUTATOR_CALLHOOK(VehicleExit)`, `vehicles_setreturn`,
`vehicles_reset_colors`, `owner=NULL`. The port's `VehicleCommon.ExitVehicle` does the core; each
descriptor `Exit()` computes eject vector + origin then calls it. **`vehicles_exit_running` re-entrancy
guard** exists in QC; the port has none (a death-eject calling Exit which the descriptor might re-enter — low
risk because the port's Death path doesn't re-call Exit, but note it).

**`vehicle_impulse(this, imp)` (~912).** Runs BEFORE weapon impulses for a seated player:
```
if (!this.vehicle) return false;
if (IS_DEAD(this.vehicle)) return false;
f = this.vehicle.vehicles_impulse;  if (f && f(this, imp)) return true;   // per-vehicle (raptor/spider)
switch (imp) {
  case IMP_weapon_drop.impulse:                                           // shared: chase toggle
    stuffcmd(this, "\ntoggle cl_eventchase_vehicle\nset _vehicles_shownchasemessage 1\n");
    return true;
}
return false;
```
Called from `sys_phys_spectator_control`/`ImpulseCommands` ordering: physics.qc:58-62 runs the vehicle/
spectator impulse path before weapon impulses. So in the PORT, `DispatchImpulse` must try the vehicle path
first when `caller.Vehicle != null`.

**`vehicles_setreturn` / `vehicles_return` / `vehicles_showwp` (~426-520).** Spawn a `vehicle_return`
helper that, after `respawntime`, teleports + re-thinks the vehicle's `wp00` (= the vehicle) via
`vehicles_spawn`, and shows a waypoint. The PORT does NOT have this helper-entity machinery; instead each
descriptor `Death()`/`Blowup()` directly does `vehicle.NextThink = Time + RespawnTime; vehicle.Think =
self => Spawn(self)` — a faithful *functional* respawn (the waypoint sprite is the only loss, a client/HUD
concern). **`vehicles_setreturn` is also called on EXIT** (a living unpiloted vehicle is scheduled to
return). The port's `ExitVehicle` does NOT schedule a return for a living abandoned vehicle — but the
descriptor `Exit()` re-arms the idle think (`racer_think`/`Land`), and a living vehicle just idles forever
(QC would return it after respawntime). **Minor parity gap; flag but out of scope** unless the task wants
faithful "abandoned vehicles vanish after respawntime".

**`vehicle_initialize(this, info, nodrop)` (~1168).** The map-spawn init: gate on `autocvar_g_vehicles`;
precache; resolve `targetname` → `vehicle_controller` (sets `.use=vehicle_use`, ACTIVE_NOT until a control
point captures it in teamplay); set model/flags/hitbox; create tur_head/hud/viewport; `settouch(vehicles_
touch); setthink(vehicles_spawn); nextthink=time; effects=EF_NODRAW;` drop to floor (unless nodrop);
`pos1=origin; pos2=angles;` schedule: ACTIVE_NOT → nextthink=0; `g_vehicles_delayspawn` →
nextthink=time+respawntime+random*jitter; else nextthink=time+game_starttime. `MUTATOR_CALLHOOK(VehicleInit,
this)`. The PORT folds all of this into `VehicleSpawnFuncs.Spawn` + descriptor `Spawn` → `VehicleCommon.
SpawnVehicle`. **The `targetname`/controller/`vehicle_use`/ACTIVE_NOT/delayspawn pieces are NOT ported** —
the port spawns every vehicle ACTIVE immediately. For stock DM/CTF maps that hand-place vehicles with no
controller, ACTIVE-immediate is correct; the controller path matters for Assault/Onslaught objective-gated
vehicles (out of scope here; flag for T36).

**`vehicle_use(this, actor, trigger)` (~522).** The control-point use handler — sets tur_head.team,
ACTIVE_ACTIVE/NOT, respawns the vehicle for the capturing team. Not ported. Out of scope (controller path).

**`vehicles_spawn` (~1107).** Reproduced by `VehicleCommon.SpawnVehicle` + descriptor `Spawn`: owner=NULL,
movetype STEP, solid SLIDEBOX, takedamage AIM, DEAD_NO, FL_NOTARGET, avelocity/velocity 0, angles=pos2,
origin=pos1, lock reset, `vr_spawn`. The port wires `settouch(vehicles_touch)` only on the Bumblebee (for
its gunner-routing touch); Racer/Raptor/Spiderbot set `Touch=null` in Spawn (because touch-mode board is
off by default). **If touch-mode board is to be supported, all four need `Touch=vehicles_touch` and the
crush/impact logic ported.**

**Hooks:** `MUTATOR_CALLHOOK(VehicleInit ~1283)`, `VehicleEnter ~1072`, `VehicleExit ~848`,
`VehicleTouch ~876`. `VehicleTouch` is the only one with a return value used (suppress touch). `VehicleEnter`/
`VehicleExit` are notify-style (no return used here). `VehicleInit` return true ABORTS init.

### 1.2 Per-vehicle `*_frame` + `*_impulse` (board/drive/impulse-relevant slices)

All four `*_frame(this, dt)` are the player's `PlayerPhysplug`: `this` = the seated player, `this.vehicle` =
the body. They read `PHYS_INPUT_BUTTON_ATCK/ATCK2/JUMP/CROUCH(this)`, `CS(this).movement`, `this.v_angle`,
then write the body velocity/angles and **glue the player**: `setorigin(this, vehic.origin + offset);
this.oldorigin=this.origin; this.velocity=vehic.velocity;`. They END by clearing the attack buttons
(`PHYS_INPUT_BUTTON_ATCK(this)=ATCK2=false`). All ported faithfully into each descriptor `Frame(vehicle,
player, input, dt)`, driven from `Think()` via `vehicle.VehInput`.

- **racer_frame** (racer.qc:154): afterburn on `PHYS_INPUT_BUTTON_JUMP`, primary on ATCK (energy laser),
  secondary on ATCK2 (rocket pair, lock via `crosshair_trace`+`vehicles_locktarget`). Ported in Racer.Frame.
- **raptor_frame** (raptor.qc:~155-433): avelocity flight, jump/crouch climb, twin cannon, bomb/flare by
  `STAT(VEHICLESTAT_W2MODE)`. **raptor_takeoff** runs first (vertical rise to frame 25) then hands to
  raptor_frame. Ported in Raptor.Takeoff/Frame.
- **spiderbot_frame** (spiderbot.qc:49-321): head turret aim, 4-point leg align, directional jump
  (`PHYS_INPUT_BUTTON_JUMP` + `.button2` latch + `.jump_delay`), walk/strafe, minigun (ATCK), rocket modes
  (ATCK2). Ported in Spiderbot.Frame/RocketDo. Note QC `spiderbot_frame` forces `PHYS_INPUT_BUTTON_ZOOM/
  CROUCH=false` + zeroes `m_switchweapon` — port doesn't need the switchweapon clear (no temp_wepent).
- **bumblebee_pilot_frame** (bumblebee.qc:427+) + **bumblebee_gunner_frame** + **bumblebee_touch**
  (bumblebee.qc:384) + **bumblebee_gunner_enter/exit**. Multi-seat. `bumblebee_touch` ONLY enters a gunner
  when `g_vehicles_enter==0` (touch mode); in use-key mode the gunner boards via `vehicles_enter`'s
  MULTISLOT branch → `vr_gunner_enter`. Ported in Bumblebee.Touch/GunnerEnter/GunnerExit/GunnerFrame/Frame.

**Impulse mode-mapping (the values the wiring must dispatch):**
- **spiderbot_impulse** (spiderbot.qc:480): `IMP_weapon_group_1→SBRM_VOLLY`, `_2→SBRM_GUIDE`,
  `_3→SBRM_ARTILLERY`; `weapon_next_*` → ++mode wrap to FIRST; `weapon_prev_*/weapon_last` → --mode wrap to
  LAST. Port: `Spiderbot.SetMode(vehicle, SpiderbotRocketMode)` + `Spiderbot.CycleMode(vehicle, +1/-1)`.
- **raptor_impulse** (raptor.qc:544): `IMP_weapon_group_1→RSM_BOMB`, `_2→RSM_FLARE`; next/prev cycle.
  Port: `Raptor.SetMode` + `Raptor.CycleMode`.
- racer/bumblebee: no `vehicles_impulse` (only the shared `weapon_drop` chase toggle applies).

**Impulse numbers (common/impulses/all.qh — same table the port's `WeaponCommandToImpulse` already uses):**
`IMP_weapon_group_1..3` = impulses **1,2,3**; `weapon_next_bygroup`=10? — port maps `weapnext→10`,
`weapprev→12`, `weapon_last→11`, `weapon_drop→17`. The vehicle impulse cases switch on
`weapon_group_N`/`weapon_next_*`/`weapon_prev_*`/`weapon_last`, i.e. impulses **1,2,3** (set mode) and
**10/12 (next/prev)**, **11 (last→prev)**. So the wiring's vehicle-impulse switch keys on those numbers.

### 1.3 last_vehiclecheck auto-enter / use-key (server/client.qc)

- **`PlayerUseKey(this)` (client.qc:2620):** if `this.vehicle` → `vehicles_exit(this.vehicle, VHEF_NORMAL)`.
  Else if `g_vehicles_enter` → find nearest `IS_VEHICLE && !DEAD && takedamage!=NO` within
  `g_vehicles_enter_radius` (250) that is `!owner` OR `(MULTISLOT && SAME_TEAM(owner))` → `vehicles_enter`.
  This is the **primary board path** in the default config.
- **`last_vehiclecheck` (client.qc:2958, in PlayerPostThink):** when `g_vehicles_enter` and
  `time>last_vehiclecheck` and not seated, FOREACH_ENTITY_RADIUS shows a `CENTER_VEHICLE_ENTER` centerprint
  ("press use to enter"); sets `last_vehiclecheck=time+1`. **Display-only; no boarding.** (Client/HUD; the
  port can skip or stub it — the board still works via the use key.)

The PORT has **no `PlayerUseKey`** and **no `ButtonUse` consumer for vehicles**. `IMovementInput.ButtonUse`
exists and arrives over the wire (`InputButtons.Use`), but nothing acts on it for vehicles. The "+use"
bind/key must be wired (BindTable/NetGame) → an edge-triggered server action → board/exit.

---

## 2. PORT STATE (what exists / what's missing) with line refs

### 2.1 What exists (faithful, compiles, ORPHANED)
- `src/XonoticGodot.Common/Gameplay/Vehicles/VehicleCommon.cs` — `EnterVehicle`(181), `ExitVehicle`(230),
  `FindGoodExit`(270), `SpawnVehicle`(309), `Regen`/`RegenResource`, `DamageVehicle`(383),
  `VehicleDamageRate`, `SpawnProjectile`(493), `FreezeIfGameStopped`(552), `GameStopped` flag(165),
  `VehicleFlags` enum, `VehicleExitFlag` enum, `VehicleAttribute`. **The 4 hooks are documented as
  not-yet-existing (VehicleCommon.cs:210/241).**
- `src/XonoticGodot.Common/Gameplay/Vehicles/EntityVehicleStateExtra.cs` — `Entity.VehInput`(29, type
  `MovementInput`), all the `Veh*` lock/weapon/jump/multi-seat/guidance fields.
- `Racer.cs` / `Raptor.cs` / `Spiderbot.cs` / `Bumblebee.cs` — each `[Vehicle]`, full
  Spawn/Enter/Exit/Think/Frame/Death/weapons. Raptor.SetMode/CycleMode(591-598), Spiderbot.SetMode/
  CycleMode(642-649). Bumblebee.GunnerEnter(417)/GunnerExit(451)/GunnerFrame(489)/Touch(151).
- `VehiclePhysicsHelpers.cs` — `VehiclePhysics` (ForceFromTag, AimTurret, LockTarget, CrosshairTrace,
  GroundAlign4Point, GuideRocket, angle helpers). All compile against `Api.*`.
- `VehicleSpawnFuncs.cs` — Racer/Raptor/Spiderbot/Bumblebee map spawnfuncs; gate on `g_vehicle_<name>`.
- `MapObjectsRegistry.cs:159-162` — spawnfuncs REGISTERED (`SpawnFuncs.Register("vehicle_racer",…)` etc.),
  so a BSP entity-lump `vehicle_racer` actually instantiates + self-thinks.

### 2.2 The Think auto-tick path (already works)
`Engine/Simulation/SimulationLoop.cs:176-182` (Tick step 3) iterates non-client entities →
`MoveTypePhysics.RunEntity(ctx, e, RunThink)` (MoveTypePhysics.cs:27) → the movetype's `runThink(ent)`
fires `ent.Think` when `NextThink` is due (`SimulationLoop.RunThink`, :213). So every spawned vehicle's
`Think()` (regen, idle hover, death/respawn, projectile guidance) **already runs**. No work needed for the
think tick itself.

### 2.3 The missing seams (precise insertion points)

**SEAM A — write `VehInput` for the seated pilot/gunner each tick (input).**
- File: `src/XonoticGodot.Server/GameWorld.cs`, method `OnClientMove(Entity e)` (726-775).
- Today: `IMovementInput input = InputProvider(p); if (canMove) Movement.Move(p, input);`.
- Insert: BEFORE the `Movement.Move` call, when `p.Vehicle != null`, copy the resolved input into the
  vehicle (and, for a gunner, into the slot's body) and **skip PM_Main**:
  ```
  if (p.Vehicle is not null) {
      p.Vehicle.VehInput = ToMovementInputStruct(input);   // see parity trap §5
      // the gunner case: p.Vehicle is the gun-slot; the body Think drives GunnerFrame off gun's owner.
      // PM_Main is NOT run; the vehicle Think (already ticking) consumes VehInput next/this tick.
      OnPlayerPostThink(p);   // QC still runs PlayerPostThink for a seated player (drown is gated by NOCLIP)
      return;
  }
  ```
  Caveat: in QC the seated player's physics IS `PlayerPhysplug = veh.PlayerPhysplug` and it runs in the
  player's movement slot, i.e. the vehicle frame executes during the player's OnClientMove, NOT during the
  vehicle's own Think. The PORT instead drives `Frame()` from `vehicle.Think()`. **Two valid designs:**
  (a) keep the port's "Frame from Think" and have OnClientMove only *stash* VehInput + skip PM_Main (the
  vehicle's own Think, which runs later this same tick in step 3, consumes it — a 0-tick latency since both
  run in the same Tick, Think after clients); OR (b) call the descriptor `Frame()` directly from
  OnClientMove (true QC ordering) and have `Think()` only do regen/idle when `Owner==null`. **Design (a) is
  the smaller change and already matches how the descriptors are written (Think calls Frame when Owner!=
  null).** Recommend (a). Either way the gate "seated player skips Movement.Move" is the load-bearing edit.

**SEAM B — board / exit on the +use key (board).**
- The faithful path is `PlayerUseKey`. Add a server-side `Vehicles.UseKey(Player)` (NEW file,
  `Gameplay/Vehicles/VehicleBoarding.cs`) reproducing client.qc:2620: seated → exit; else if
  `g_vehicles_enter` → nearest vehicle in radius 250 → `EnterVehicle` (+ the guards from `vehicles_enter`
  and the MULTISLOT gunner branch).
- Trigger: an edge-triggered "+use pressed" signal must reach the server. Today `ButtonUse` rides every
  `InputCommand` (`ServerNet.ToMovementInput` sets `ButtonUse`). Cleanest faithful wiring: detect the
  RELEASED→PRESSED edge of `ButtonUse` server-side per player (a `bool _usePrev` in `ServerNet.PeerState`
  or `ServerPlayerState`) and call `Vehicles.UseKey(p)` on the rising edge — QC `PlayerUseKey` is itself
  edge-driven (DP fires it once per +use press, not per tick). **Where:** `GameWorld.OnClientMove` (after
  resolving `input`, before/independent of the vehicle gate) OR `ServerNet.ProvideInput` (it already has the
  per-player state + the dequeued command). Putting the edge-detect in `GameWorld.OnClientMove` keeps it net-
  layer-independent and testable; but it needs the per-player "use was down last tick" memory —
  `ServerPlayerState` (`PlayerStates.Of(p)`) is the natural home.
- NOTE touch-mode board (`g_vehicles_enter==0`) is a SEPARATE path and is **not reachable** today because
  PlayerPhysics does not dual-dispatch `.Touch` onto solids it hits (confirmed: no `.Touch` invoke in
  `Physics/PlayerPhysics.cs`). Supporting touch-mode would require either (1) porting the crush/impact/enter
  `vehicles_touch` + adding a player-vs-solid touch dispatch, or (2) a radius check each tick. **Default
  config is use-key, so SEAM B via UseKey is sufficient for parity in the shipped default.** Flag
  touch-mode as a documented partial.

**SEAM C — route impulses to the seated vehicle (impulse).**
- File: `src/XonoticGodot.Server/Commands.cs`, `DispatchImpulse(ctx, caller, imp)` (893-905).
- Today: `if (!WeaponImpulses.Handle(caller, imp)) ctx.Print(...)`.
- Insert FIRST (QC `vehicle_impulse` runs before weapon impulses):
  ```
  if (caller.Vehicle is not null && !VehicleCommon.IsDead(caller.Vehicle)) {
      if (Vehicles.Impulse(caller, imp)) return true;     // NEW dispatcher (per-vehicle CycleMode/SetMode + chase)
  }
  ```
- `Vehicles.Impulse(caller, imp)` (NEW, in VehicleBoarding.cs): switch on the vehicle type's def:
  - Raptor: imp 1→SetMode(Bomb), 2→SetMode(Flare), 10→CycleMode(+1), 11/12→CycleMode(-1).
  - Spiderbot: imp 1→Volley, 2→Guide, 3→Artillery, 10→CycleMode(+1), 11/12→CycleMode(-1).
  - shared `weapon_drop` (imp 17): chase toggle — server-side this is a stuffcmd in QC
    (`toggle cl_eventchase_vehicle`); in the port this is a client cvar, so either no-op server-side or
    send a stringcmd back. **Lowest-risk: handle 1/2/3/10/11/12 (mode) faithfully, treat 17 as a no-op
    (the chase cam is a client concern, T-client).**
- The existing C2S impulse path already reaches here: `NetGame` bind → `_pendingImpulse` →
  `InputCommand.Impulse` → `ServerNet.ProvideInput` → `_world.Commands.Execute($"impulse {N}")` →
  `CmdImpulse` → `DispatchImpulse`. So routing the impulse to the vehicle at `DispatchImpulse` makes the
  EXISTING weapon-group keys (1,2,3 / weapnext / weapprev) switch vehicle modes when seated — zero net work.
  But `ServerNet.ProvideInput` SKIPS the impulse dispatch for observers; a seated player is NOT an observer,
  so the dispatch runs — good. (Confirm a seated player is not flagged IsObserver; EnterVehicle does not set
  it, so fine.)

**SEAM D — VehicleEnter/Exit/Touch/Init hooks (MutatorHooks.cs).** See §4 (hotFileNeeds).

**SEAM E — feed `VehicleCommon.GameStopped` from the match loop.** `FreezeIfGameStopped` reads
`GameStopped || cvar g_game_stopped`. `GameWorld` already has a `GameStopped`/`MatchEnded`/`Intermission`
concept (used in OnClientMove/OnPlayerPostThink). Set `VehicleCommon.GameStopped = <world game-stopped>`
once per frame (e.g. in `GameWorld.OnEndFrame` or StartFrame). Tiny edit; without it vehicles keep flying
during intermission (cosmetic). Optional but faithful.

---

## 3. CONFIG / CVARS + SHIPPED DEFAULTS (board/drive/impulse-relevant)

From `sv_vehicles.qh` and the per-vehicle `.qc` (defaults inlined in the port already; listing the *gating*
ones the wiring reads):
- `g_vehicles` = (master) — vehicle_initialize aborts if 0. Port: `VehicleSpawnFuncs` gates per-vehicle, no
  global gate yet — **add the `g_vehicles` master gate** to the boarding path (QC `vehicle_impulse`/enter
  implicitly require vehicles to exist).
- `g_vehicles_enter` = **1** (sv_vehicles.qh) — 1 = USE-KEY board (PlayerUseKey), 0 = TOUCH board.
- `g_vehicles_enter_radius` = **250** — PlayerUseKey/last_vehiclecheck search radius.
- `g_vehicles_allow_bots` = 0 — bots can't board (guard in vehicles_enter + Bumblebee.AllowBots already).
- `g_vehicles_steal` / `g_vehicles_steal_show_waypoint` — enemy boarding of a team vehicle (waypoints are
  client; the steal *gameplay* (shield=0, flags backup) is in vehicles_enter — port's EnterVehicle does NOT
  do steal; flag as partial).
- `g_vehicles_teams` = 1, `g_vehicles_delayspawn` / `_jitter` — init scheduling (not ported; ACTIVE-now).
- `g_vehicles_crush_dmg` = 70, `_crush_force` = 50, `_crush_minspeed` = 100 — `vehicles_touch` crush (only
  matters if touch dispatch on a piloted vehicle is wired).
- `g_vehicles_exit_attempts` = 25 — `FindGoodExit` (already read in VehicleCommon.cs:287).
- `g_vehicles_thinkrate` = 0.1 — `VehicleCommon.DefaultThinkRate` (already).
- Per-vehicle master `g_vehicle_racer`/`_raptor`/`_spiderbot`/`_bumblebee` = 1 (each per-vehicle .qc top).
  `VehicleSpawnFuncs.MasterSwitchEnabled` already honours these (absent→on, explicit 0→off).
- Damage-rate table `g_vehicles_*_damagerate` (vortex 0.75 / machinegun 0.75 / rifle 0.75 / vaporizer 0.5 /
  tag 5 / weapon 2) — already in `VehicleCommon.VehicleDamageRate`.

**Where defaults live in the port:** there is no `vehicles.cfg` loader; the per-vehicle balance is inlined
as C# field initializers in each descriptor (Racer.cs:31+ etc.) and the shared cvars are read by name with a
fallback. The wiring should follow suit: read `g_vehicles_enter`/`_enter_radius`/`g_vehicles_allow_bots`
via `Api.Cvars.GetFloat(name)` with the shipped fallback (1 / 250 / 0).

---

## 4. hotFileNeeds (shared files this task must edit that other A2 tasks also own)

### `MutatorHooks.cs` (also needed by **T51**) — add 4 vehicle hook chains.
`MutatorHooks.cs` is the additive hook-chain registry; `HookChain<TArgs>` (Framework/Hooks.cs) is the bus.
Existing pattern: a `struct XArgs { readonly Entity …; ctor }` + `public static readonly HookChain<XArgs> X
= new();` (+ optional `FireX` helper like `FireStartFrame`). The EXACT minimal addition (append to the class,
after `SetDefaultAlpha`):
```csharp
// ---- Vehicles (common/vehicles/sv_vehicles.qc MUTATOR_CALLHOOK) ----

/// <summary>EV_VehicleInit — fired at end of vehicle_initialize; a handler returning true ABORTS init
/// (QC: MUTATOR_CALLHOOK(VehicleInit,this) → return false). Slot0 the vehicle entity.</summary>
public struct VehicleInitArgs { public readonly Entity Vehicle; public VehicleInitArgs(Entity v){Vehicle=v;} }
public static readonly HookChain<VehicleInitArgs> VehicleInit = new();

/// <summary>EV_VehicleEnter — a player boarded a vehicle. Slot0 player, slot1 vehicle. (Notify; return unused.)</summary>
public struct VehicleEnterArgs { public readonly Entity Player; public readonly Entity Vehicle;
    public VehicleEnterArgs(Entity p, Entity v){Player=p;Vehicle=v;} }
public static readonly HookChain<VehicleEnterArgs> VehicleEnter = new();

/// <summary>EV_VehicleExit — a player left a vehicle. Slot0 player (may be null), slot1 vehicle.</summary>
public struct VehicleExitArgs { public readonly Entity? Player; public readonly Entity Vehicle;
    public VehicleExitArgs(Entity? p, Entity v){Player=p;Vehicle=v;} }
public static readonly HookChain<VehicleExitArgs> VehicleExit = new();

/// <summary>EV_VehicleTouch — vehicle touched an entity; a handler returning true SUPPRESSES the touch
/// (QC: if (MUTATOR_CALLHOOK(VehicleTouch,this,toucher)) return). Slot0 vehicle, slot1 toucher.</summary>
public struct VehicleTouchArgs { public readonly Entity Vehicle; public readonly Entity Toucher;
    public VehicleTouchArgs(Entity v, Entity t){Vehicle=v;Toucher=t;} }
public static readonly HookChain<VehicleTouchArgs> VehicleTouch = new();
```
**Orchestrator note:** assign ONE owner of MutatorHooks.cs across T37+T51 and hand the other this exact
snippet. No T51 mutator currently subscribes to these (no stock mutator hooks VehicleEnter/Exit/Init/Touch
in the batch), so the addition is purely additive and order-independent. T37 needs them only so the boarding
helper can `MutatorHooks.VehicleEnter.Call(...)`/`VehicleTouch.Call(...)` at the call-site (the descriptors
already compile without them).

### `GameWorld.cs` (also owned by **T36**) — SEAM A (the seated-player gate) + SEAM E.
T36 (mode-objective spawnfuncs) also edits GameWorld.cs. T37's edits are localized to `OnClientMove`
(726-775: the "seated player stashes VehInput + skips Movement.Move + still PostThinks" branch) and one line
in a per-frame method for `VehicleCommon.GameStopped`. **Exact minimal edit** — inside `OnClientMove`, right
after `IMovementInput input = InputProvider(p);` (762) and before `if (canMove)`:
```csharp
// Seated in a vehicle: the vehicle frame (driven from its Think) consumes this input; the player does NOT
// run PM_Main (QC: PlayerPhysplug = veh.PlayerPhysplug replaces SV_PlayerPhysics). Mirror QC by stashing the
// input on the body and skipping the walk move; PlayerPostThink still runs (drown is NOCLIP-gated).
if (p.Vehicle is not null && canMove) {
    Entity body = p.Vehicle.VehSlotOwner ?? p.Vehicle;       // gunner slot → body; pilot → the body itself
    p.Vehicle.VehInput = VehicleBoarding.ToInput(input);     // both gun-slot & body carry it (GunnerFrame reads gunner.VehInput)
    OnPlayerPostThink(p);
    return;
}
```
and one line wherever the world publishes its frozen state (e.g. top of `OnEndFrame`):
`VehicleCommon.GameStopped = GameStopped || MatchEnded || Intermission.Running;`
**Orchestrator note:** these are small, contiguous, and don't touch T36's spawnfunc table region; safe to
hand T36 the snippet OR let T37 own these specific hunks.

### `Commands.cs` (owned by **T56**) — SEAM C (impulse routing).
T56 owns `XonoticGodot.Server/Commands.cs`. T37 needs ONE insertion in `DispatchImpulse` (893):
```csharp
// QC ImpulseCommands: the vehicle impulse path runs BEFORE weapon impulses for a seated player
// (vehicle_impulse → per-vehicle vehicles_impulse). Route mode-switch/cycle to the vehicle first.
if (caller.Vehicle is not null && !VehicleCommon.IsDead(caller.Vehicle)
    && VehicleBoarding.Impulse(caller, imp)) return true;
```
(placed after the intermission/timeout guards, before `WeaponImpulses.Handle`). **Orchestrator note:**
single line + a using; hand T56 this exact snippet, or have T37 own the `DispatchImpulse` hunk.

### NOT a registry edit.
Registration is by reflection attribute: `[Vehicle]` → `VehicleAttribute : GameRegistryAttribute` →
`GameRegistries.Bootstrap` enrols each into `Vehicles` (VehicleCommon.cs:104-111). Spawnfuncs are already in
`MapObjectsRegistry.cs:159-162`. **No `Registries.cs` edit needed.** The boarding/impulse helpers live in a
NEW file (`Gameplay/Vehicles/VehicleBoarding.cs`), and the hooks are additive to MutatorHooks.cs. So the bulk
of T37 is NEW code + 3 tiny shared-file insertions (GameWorld OnClientMove gate, Commands DispatchImpulse
line, MutatorHooks 4 chains).

---

## 5. RISKS / PARITY TRAPS

1. **`Entity.VehInput` is `MovementInput` (struct) but `InputProvider` returns `IMovementInput`
   (interface).** Copying requires materializing the struct. `ServerNet.ToMovementInput` already builds a
   `MovementInput` from the `InputCommand`, but `GameWorld.InputProvider` hands back the boxed
   `IMovementInput`. Add a `VehicleBoarding.ToInput(IMovementInput)` that copies every field into a
   `MovementInput` (ViewAngles, MoveValues, FrameTime, all buttons, Impulse). **Do NOT just cast** — the
   concrete type may be the cached `MovementInput` from ServerNet, but a host with no net layer uses
   `ZeroInput`; copy defensively.
2. **MoveValues is already wish-VELOCITY scaled (±400) by the time it reaches the seam.**
   `ServerNet.ToMovementInput` runs `WishMoveScaling.Scale(c.Forward,c.Side,c.Up)` (cl_forwardspeed 400 /
   sidespeed 350 / upspeed 400). The per-vehicle `*_frame` in QC reads `CS(this).movement` which is ALSO the
   scaled wish-move (DP fills CS(this).movement from the usercmd*speed). The port's vehicle Frame uses
   `move.X > 0 ? +speed : -speed` (sign-only), so the magnitude doesn't matter — but the spiderbot jump
   `movefix = sign(move.X/Y)` and racer/raptor `move.X != 0` are sign tests too. **So the scaling is benign
   for vehicles** (they only read the SIGN of MoveValues). Verify no vehicle reads the magnitude (grep
   confirms: all four use `move.X/Y > 0 ?` or `!= 0` or `Sign(...)`). Safe.
3. **"Frame from Think" vs "Frame from player move" ordering.** The port drives `Frame()` from the
   vehicle's `Think()` (step 3 of the Tick), while the player's input is stashed in step 2 (OnClientMove).
   Since both run in the SAME Tick (clients before non-clients), the latency is 0 ticks. BUT: the player-glue
   (`setorigin(player, vehicle.origin+offset); player.velocity=vehicle.velocity`) happens in `Think()`
   AFTER the player already moved (or was skipped) in step 2 — correct, because we SKIP `Movement.Move` for
   the seated player, so the player isn't double-moved. **Confirm the seated player is excluded from the
   slide-move** (SEAM A's `return` after stashing). If you forget the skip, the player runs PM_Main AND gets
   glued — fighting origins, jitter.
4. **The carrier/prediction split (listen server + client).** On a listen server the AUTHORITATIVE
   `Player` boards the vehicle server-side. The CLIENT predicts a separate `_carrier` entity (NetGame.cs:470
   `SpawnCarrier`, SOLID_NOT) via `EntityMovementStep` which runs PM_Main, NOT the vehicle frame. So a
   boarded local player's CLIENT prediction will keep walking/falling while the SERVER drives the vehicle —
   the reconcile will snap. **Faithful client-side vehicle prediction is out of scope** (QC sets
   `disableclientprediction=1` on board — the client stops predicting and follows the networked vehicle
   pose). For T37's server-authoritative scope, document that the local pilot's view may be rough until
   client-side "stop predicting while seated" is added (a follow-up: when the networked owner state shows
   `vehicle != 0`, NetGame should freeze prediction and place the camera at the networked vehicle eye). The
   SERVER behaviour (board/drive/fire/exit/return) is fully testable headlessly without this.
5. **Touch-mode board is unreachable** (PlayerPhysics doesn't dual-dispatch `.Touch` onto solids). Only
   matters if a server sets `g_vehicles_enter 0`. Default is use-key → fine. Document the partial.
6. **`vehicles_setreturn` on a living abandoned vehicle is not ported** — an exited (not destroyed) vehicle
   idles forever instead of vanishing after `respawntime`. Faithful only for the destroyed-vehicle respawn
   (which the descriptor Death does). Flag; low gameplay impact for DM/CTF.
7. **`targetname`/controller/ACTIVE_NOT/delayspawn not ported** — every vehicle spawns ACTIVE immediately.
   Correct for hand-placed DM/CTF vehicles; the controller path is an Assault/Onslaught objective concern
   (coordinate with T36). Don't add it here unless asked.
8. **Re-entrancy:** QC's `vehicles_exit_running` guard is absent. The death path (`DamageVehicle` →
   descriptor `Exit` → `ExitVehicle`, then `Death`) does not re-enter Exit in the port, so it's currently
   safe, but a future `VehicleExit` hook handler that calls exit again could recurse. Low risk; mention.
9. **`+use` edge detection** must be once-per-press (QC PlayerUseKey is edge-driven). Don't call UseKey
   every tick `ButtonUse` is held (that would board→exit→board flicker). Store `usePrevDown` per player.
10. **Observer/seated interplay:** `ServerNet.ProvideInput` only dispatches the impulse-as-weapon-command
    when `!p.IsObserver`. A seated player is not an observer, so the impulse reaches `DispatchImpulse` → the
    new vehicle route. Good. But confirm `EnterVehicle` doesn't set any observer/independent flag.

---

## 6. TEST PLAN (headless, deterministic — no Godot)

Add `tests/XonoticGodot.Tests/VehicleRuntimeTests.cs` (xUnit, the suite disables parallelism already). Use the
ambient services pattern other gameplay tests use (boot a `SimulationLoop`/minimal world, `InstallAsAmbient`,
spawn entities). Cover the SEAMS, not the (already-tested-by-construction) deep math:

1. **Spawn + self-think:** instantiate `vehicle_racer` via `VehicleSpawnFuncs.Racer(e)` (with
   `g_vehicle_racer` unset → default on); assert `e.VehicleDef is Racer`, `e.Think != null`, `e.NextThink>0`,
   health == 200, `(e.VehicleFlags & IsVehicle)!=0`. Tick a few times → it idles (origin near spawn, no NaN).
2. **Board (use-key path):** spawn a Racer at origin, a `Player` within 250u; call
   `VehicleBoarding.UseKey(player)` (with `g_vehicles_enter` default 1). Assert `player.Vehicle == racer`,
   `racer.Owner == player`, `player.MoveType == None`, `player.Solid == Not`, `player.TakeDamage == No`,
   `racer.NextThink` re-armed. Guard tests: bot without `g_vehicles_allow_bots` → not boarded; dead player →
   not boarded; `vehicle_enter_delay` in future → not boarded; out-of-radius → not boarded.
3. **Drive (input reaches Frame):** board; set `racer.VehInput = {ButtonJump=true}` (afterburn) and tick;
   assert `racer.VehicleEnergy` dropped by ~AfterburnCost*dt. Set `MoveValues=(+1,0,0)` and tick; assert the
   racer accelerated forward (velocity dot forward > 0). Set `ButtonAttack1` + full energy; assert a
   `vehicles_projectile` was spawned (FindByClass count increased) and energy dropped by CannonCost.
   **This is the test that proves the seam: without VehInput wiring the energy/velocity never change.**
4. **GameWorld seated gate:** drive a `Player` through `GameWorld.OnClientMove` (or the SimulationLoop
   ClientMove) while seated with `MoveValues!=0`; assert PM_Main did NOT move the player hull (player origin
   tracks the vehicle origin+offset, not a walk-move), i.e. the skip works. (May require a GameWorld test
   harness; if too heavy, assert the gate at the helper level: `VehicleBoarding.ToInput` round-trips every
   field, and assert OnClientMove returns early — via a spy on Movement.Move call count if available.)
5. **Impulse routing:** board a Spiderbot; default mode SBRM_GUIDE (=2). Call `VehicleBoarding.Impulse(p,1)`
   → `VehW2Mode == SBRM_VOLLY`. `Impulse(p,3)` → ARTILLERY. `Impulse(p,10)` (next) wraps VOLLY. Raptor:
   `Impulse(p,2)` → RSM_FLARE. Non-seated caller → `Impulse` returns false (falls through to weapons).
   Optionally end-to-end via `Commands.Execute("impulse 1", caller:p)` to prove the DispatchImpulse hook.
6. **Exit (use-key while seated):** board, then `UseKey(player)` again → `player.Vehicle == null`,
   `player.MoveType == Walk`, `player.Solid == SlideBox`, `player.TakeDamage == Aim`,
   `player.VehicleEnterDelay > time`, `racer.Owner == null`, player relocated to a FindGoodExit spot.
7. **Death → respawn (already works; regression guard):** force `racer` health to 0 via
   `VehicleCommon.DamageVehicle`; assert pilot ejected (if seated), `DeadState==Dying`, and after ticking
   `RespawnTime` seconds the vehicle re-spawns (DeadState back to No, health restored, at SpawnPos).
8. **Bumblebee multi-seat:** board a pilot; a 2nd same-team player calls `Vehicles.UseKey` (or
   `GunnerEnter`) → seated in gun1/gun2 (`VehGunner1/2` set, `player.Vehicle == the gun slot`). Set the
   gunner's `VehInput.ButtonAttack1` + tick → a `vehicles_projectile` spawned from the gun; gun ammo dropped.
9. **Hooks fire:** subscribe a handler to `MutatorHooks.VehicleEnter`/`VehicleExit`/`VehicleTouch`; assert
   it's called on board/exit (and that a `VehicleTouch` handler returning true suppresses touch, if touch is
   wired). At minimum assert the chains exist + Enter/Exit fire.

Determinism: vehicle code uses `Prandom` (the seeded shared PRNG) for spread/exit-attempts/death timing —
seed it per test (`Prandom.Seed(...)` if exposed) so projectile spreads + death delays are reproducible.

---

## 7. RECOMMENDED FILE PLAN (for the implementer)

- **NEW** `src/XonoticGodot.Common/Gameplay/Vehicles/VehicleBoarding.cs` — `Vehicles.UseKey(Player)`,
  `Vehicles.Enter(player, vehicle)` (the guards + steal + gunner branch + `MutatorHooks.VehicleEnter` +
  RemoveGrapplingHooks), `Vehicles.Exit(player)`, `Vehicles.Impulse(player, imp)`, `ToInput(IMovementInput)
  → MovementInput`, and the radius search (read `g_vehicles_enter`/`_enter_radius`/`g_vehicles_allow_bots`).
  (Namespace `XonoticGodot.Common.Gameplay`, like the other vehicle files.)
- **EDIT** `MutatorHooks.cs` — +4 hook chains (§4 snippet). *hot (T51)*.
- **EDIT** `GameWorld.cs` `OnClientMove` — seated gate (§4 snippet) + `VehicleCommon.GameStopped` feed.
  *hot (T36)*.
- **EDIT** `Commands.cs` `DispatchImpulse` — vehicle-impulse-first line (§4 snippet). *hot (T56)*.
- **NEW** `tests/XonoticGodot.Tests/VehicleRuntimeTests.cs` — §6.
- Optionally **EDIT** the descriptor `Spawn()`s to set `Touch = vehicles_touch` + port `vehicles_touch`
  crush/impact IF touch-mode board is in scope (NOT recommended for the default-config parity goal).

Confidence: high on the spec + seams (read in full, cross-checked against the live call graph); medium on
the exact GameWorld OnClientMove harnessing for test #4 (may need a lighter assertion if a Movement.Move spy
isn't readily available).
