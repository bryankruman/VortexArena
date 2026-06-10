# Player Loop Rules — Damage / Health / Spawning / Respawn / Spectating

Authoritative behavioral spec for the core player loop, derived from the Xonotic QuakeC reference
(`Base/data/xonotic-data.pk3dir/qcsrc/`) and reconciled against the C# port. This is the contract the
port must satisfy. Each rule cites its QC source and notes the port's status as of the 2026-06-09 parity
deep-dive. Items marked **LANDED** are implemented by that pass; **DEFERRED** are tracked follow-ups.

Severity legend: **P0** = visibly broken core behavior; **P1** = wrong/missing but subtle; **P2** = polish/edge.

---

## 1. Damage (`server/damage.qc` `Damage()` + `server/player.qc` `PlayerDamage()`)

1.1 **Single damage gate.** All damage flows through `Damage()` → `event_damage` (`PlayerDamage` for a live
player, `PlayerCorpseDamage` for a corpse). Bail if the target is freed, `takedamage == DAMAGE_NO`, a
spectator, or the game is stopped. The grapple HOOK / `HITTYPE_SOUND` attacks never hit a same-team player.
*Port: faithful & wired (`DamageSystem.Apply`).*

1.2 **Always-lethal deaths.** `DEATH_KILL` / `DEATH_TEAMCHANGE` clear spawn-shield + godmode and force
`damage = 100000`. *Port: faithful.*

1.3 **Armor split.** `save = bound(0, damage·armorblockpercent, armor)`, `take = bound(0, damage−save, damage)`.
`armorblockpercent` default `0.7`; drowning and `HITTYPE_ARMORPIERCE` force it to `0`. *Port: faithful.*

1.4 **Self-damage scales exactly once.** `if (targ == attacker) damage *= g_balance_selfdamagepercent` (0.65),
**inside `Damage()` only** — `RadiusDamageForSource` does NOT scale self-damage. *Port: **LANDED** — was
double-applied (WeaponSplash + DamageSystem → 0.65² = 0.42×); removed the WeaponSplash copy (DMG1).*

1.5 **Teamplay damage.** Stock defaults: `teamplay_mode 4`, `g_friendlyfire 0.5`, `g_mirrordamage 0.7`,
`g_friendlyfire_virtual 1`, `g_mirrordamage_virtual 1`, `g_friendlyfire_virtual_force 1`,
`g_teamdamage_threshold 40`. Mode 4 = friendly fire is graphics-only (virtual) with a mirror-damage
teamkill punishment past the threshold. *Port: **LANDED** — registered stock server defaults so team modes
no longer subtract real teammate HP (DMG2).*

1.6 **Knockback.** `farce = damage_explosion_calcpush(damageforcescale·force, velocity, speedfactor)`; players
seed `damageforcescale = g_player_damageforcescale` (2) on spawn; clears `FL_ONGROUND`; spawn-shielded targets
take no push (unless self). *Port: faithful & wired; delivered to remote players via the velocity snapshot.*

1.7 **Radius falloff distance.** Measured from the nearest point on the inflictor bbox to the nearest point on
the target bbox (minus `damageextraradius`), bounded at 0 — so a point-blank hit takes full core damage.
*Port: **LANDED** — was bbox-center distance (undershot close range); now nearest-point clamp (DMG3).*

1.8 **HITTYPE flags** (`HITTYPE_SPLASH`/`SECONDARY`/`ARMORPIERCE`) ride the deathtype. *Port: DEFERRED (P2) —
no live divergence at stock balance (vortex armorpierce defaults 0); structural follow-up.*

1.9 **Fire/burning.** `Fire_AddDamage` accumulates DPS+duration; `Fire_ApplyDamage` deals
`fire_damagepersec·frametime` per tick as `DEATH_FIRE` credited to `fire_owner`, and transfers fire to
overlapping entities. *Port: DEFERRED (P2) — currently a simplified per-tick stub.*

---

## 2. Health / Armor / Regen / Rot (`PlayerFrameLogic` ← `player_regen`/`RotRegen`)

2.1 **Per-frame regen/rot** runs every server tick for each live, non-stopped player: armor, then health,
then fuel, each moving toward its stable value (exponential `*_regen`/`*_rot` factor + linear term, snap
within 0.25). Clamp to the per-resource limit. *Port: faithful & wired.*

2.2 **Damage pauses regen** for `g_balance_pause_health_regen` (5s): on a health hit,
`pauseregen_finished = max(pauseregen_finished, time + 5)`; the health AND armor regen gates read it.
*Port: **LANDED** — the write (Entity field) and read (server side-table field) hit two different storage
slots, so damage never paused regen; unified onto the Entity field (REGEN1, P0).*

2.3 **Pickup pauses rot.** `GiveResource(HEALTH/ARMOR/FUEL)` bumps the matching
`pauserot*_finished = max(…, time + g_balance_pause_{health,armor,fuel}_rot)` (1/1/2s) so an over-stacked
resource holds before decaying. *Port: **LANDED** — rot timers moved onto the Entity and bumped in
`GiveResource` (REGEN2, P1).*

2.4 **Spawn primes rot-pause.** `PutPlayerInServer` sets `pauserot*_finished = time + g_balance_pause_*_rot_spawn`
(5/5/10s) rather than zeroing, so a player spawned above the stable point (e.g. 200 HP CA/LMS start) holds
before rotting. *Port: **LANDED** (REGEN3, P2).*

2.5 **Rot-to-death.** After regen, if `health < 1` the player dies via a `DEATH_ROT` hit (even if a mutator
disabled regen). *Port: faithful.*

---

## 3. Death / Gibbing / Frag award (`server/player.qc` `PlayerDamage` death block)

3.1 **Death trigger.** `health < 1` inside `PlayerDamage` → the kill path: zero air timer, play death/drown
voice (`sv_gentle < 1`), Obituary→GiveFrags, `PlayerDies` hook, set respawn time, build the corpse, run
corpse-damage with the excess (overkill gibs immediately). *Port: faithful & wired.*

3.2 **Corpse.** `MOVETYPE_TOSS` (or 0-vel if noclip), `SOLID_CORPSE`, upright (`angles_z=0`), `view_ofs.z=-8`,
`deadflag=DEAD_DYING`, route further hits to corpse damage. *Port: faithful.*

3.3 **The live edict is reused on respawn and MUST be reset to a clean live player** — never a "corpse". QC
clones a separate `body` for the lingering corpse and re-uses the client edict via `PutClientInServer`.
*Port: **LANDED** — `IsCorpse` was set on death and never reset, so after one death+respawn the player was
permanently a corpse (un-killable, awarded no frags). `PutPlayerInServer` now resets
`IsCorpse=false`, `Alpha=1`, `BallisticsDensity=0` (DEATH1, P0).*

3.4 **Gibbing.** `health ≤ −sv_gibhealth` (100) → `Violence_GibSplash`, `alpha=−1`, `SOLID_NOT`,
`DAMAGE_NO`. State faithful; **gib gore VFX networking is DEFERRED (P1, DEATH4)**.

3.5 **Frag matrix.** Enemy frag → attacker `SCORE+1`, `KILLS+1`, spree++; suicide/world → victim `SCORE−1`,
`SUICIDES+1`; teamkill → attacker `SCORE−1`, spree reset. Victim always `DEATHS+1`. Credited-attacker window
(`.pusher`/`.pushltime`, `g_maxpushtime` 8s) re-credits an environmental death to the last attacker.
*Port: faithful & wired.*

3.6 **Default weapon drop on death** (`g_weapon_throwable 1`, not your last weapon). *Port: DEFERRED (P2,
DEATH6) — no-op at the Blaster-only stock start loadout.*

3.7 **`/kill`** runs a `g_balance_kill_delay` countdown (announcer) + `g_balance_kill_antispam`. *Port:
DEFERRED (P2, DEATH5) — currently instant.*

---

## 4. Respawn (`server/client.qc` `calculate_player_respawn_time` + the `PlayerThink` dead state machine)

4.1 **Respawn-time computation.** `calculate_player_respawn_time` counts live same-team (teamplay) / all-other
(FFA) players → `pcount`, interpolates the delay between `g_respawn_delay_small`(2) and `_large`(2) by
`pcount` between `_small_count`/`_large_count`(8), quantizes by `g_respawn_waves`, sets
`respawn_time_max = time + g_respawn_delay_max`(5), arms a `respawn_countdown` announcer, and sets
`RESPAWN_FORCE` when `g_forced_respawn`. *Port: **LANDED** — shared `RespawnTiming.Calculate` replacing the
flat 2s; fixed `g_respawn_delay_max` default 2→5 (DEATH3/LOOP5).*

4.2 **Dead state machine** (`PlayerThink` when `IS_DEAD`): `button_pressed = ATCK|JUMP|ATCK2|HOOK|USE`.
`DEAD_DYING → DEAD_DEAD → DEAD_RESPAWNABLE (on press) → DEAD_RESPAWNING (on release) → respawn()`.
At **stock defaults (`g_forced_respawn 0`) a dead player stays on the kill-cam until they press+release fire
after `respawn_time`** — they are NOT auto-yanked back. With `g_forced_respawn 1` they auto-respawn at
`respawn_time_max`. `RESPAWN_DENY` freezes; `RESPAWN_SILENT` hides the timer. *Port: **LANDED** — the
`DeadFlag` machine + button gate now drive respawn; `RespawnDuePlayers` is the forced fallback only
(DEATH2/LOOP1, P1).*

4.3 **Respawn countdown networked.** `STAT(RESPAWN_TIME)` is sent each frame (negated while `DEAD_RESPAWNING`)
so the client shows "Respawning in N…" / "Press fire to respawn". *Port: **LANDED** — added a respawn-time
field to the owner snapshot + the InfoMessages prompt (LOOP4, P1).*

4.4 **`respawn()`** fades/ghosts the corpse, `CopyBody` clones the lingering body, then `PutClientInServer`
re-spawns the live edict. *Port: corpse-clone is DEFERRED (cosmetic); the live respawn is faithful.*

---

## 5. Spawning (`server/client.qc` `PutPlayerInServer` + `server/spawnpoints.qc` `SelectSpawnPoint`)

5.1 **Spawn point scoring.** `Spawn_Score` weights each spot by distance to the nearest live other player,
`+SPAWN_PRIO_GOOD_DISTANCE`(10) when that distance > `mindist`(100); filter wrong-team / inactive /
restricted / startsolid spots; `Spawn_WeightedPoint` picks via a weighted reservoir. With probability
`(1 − g_spawn_furthest)` (default 0.5) use a near-uniform pick; otherwise a strongly-far-biased pick.
*Port: faithful & wired; **LANDED** the `g_spawn_furthest` cvar read (was hardcoded 0.5; SPAWN4, P2).*

5.2 **Start loadout (non-warmup).** `health = g_balance_health_start`(100), `armor = g_balance_armor_start`(0),
the five ammo pools, `start_weapons` (Blaster), `start_items`. Switch to the best owned weapon. *Port:
faithful.*

5.3 **Warmup loadout.** During `warmup_stage`: `warmup_start_health`(100), `warmup_start_armorvalue`(100),
`WARMUP_START_WEAPONS` (all guns when `g_warmup_allguns 1`). *Port: **LANDED** — `PutPlayerInServer` now
branches on warmup (SPAWN3, P1).*

5.4 **Spawn shield.** `StatusEffects_apply(SpawnShield, time + g_spawnshieldtime)` (1s); blocks damage
(`g_spawnshield_blockdamage 1` = full); lost on fire; while shielded the model glows `EF_ADDITIVE|EF_FULLBRIGHT`.
An explicit `g_spawnshieldtime 0` disables it. *Port: server-side block faithful + **LANDED** the `0`-disables
cvar read (SPAWN5, P2). The shield-glow effect bits + HUD indicator networking are DEFERRED (SPAWN1, P1).*

5.5 **Spawn visual/teleport.** `effects |= EF_TELEPORT_BIT | EF_RESTARTANIM_BIT` (cancel interpolation so the
player doesn't slide from its corpse) + a `SpawnEvent` particle/sound burst (`g_spawn_alloweffects` 3).
*Port: DEFERRED (SPAWN2, P1) — the no-interp bit + spawn FX burst are a follow-up.*

5.6 **Placement.** `angles_z=0`, `fixangle`, clear velocities/punch, unduck, standing eye height, bbox
`PL_MIN/MAX`, origin nudge above the marker, `oldorigin=origin`. *Port: faithful.*

5.7 **Pre-match countdown extension.** When spawning before `game_starttime`, extend the shield + rot/regen
pauses by `(game_starttime − time)`. *Port: DEFERRED (P2, SPAWN6).*

5.8 **Spawnpoint relocation / move-out-of-solid** at map link. *Port: DEFERRED (P2, SPAWN7) — borderline spots
filtered at selection instead; emergency fallback can spawn in solid.*

---

## 6. Spectating / Observing (`server/client.qc` `PutObserverInServer` + the Spectate state machine)

6.1 **Observer state.** `PutObserverInServer` makes a free-fly observer: hide the model, `MOVETYPE_FLY_WORLDONLY`
(toggle NOCLIP via `cl_clippedspectating`), `SOLID_NOT`, `DAMAGE_NO`, `FL_CLIENT|FL_NOTARGET`, strip
weapons/items, `RES_HEALTH = FRAGS_SPECTATOR`, `SetSpectatee(NULL)`, `SetSpectatee_status(self)`. Used at
connect, on `/kill`-to-spectate, and on the team-spectator command. *Port: **LANDED** — added
`ClientManager.PutObserverInServer` mirroring this; the connect-as-observer phase already existed
(SPEC4/LOOP2, P0).*

6.2 **Free-fly movement.** A free-fly observer runs `PM_Main` with the spectator-speed ladder
(`PlayerPhysics.SpectatorControl`). *Port: **LANDED** — observers now get a fly movetype so the (already
faithful) `SpectatorControl` actually runs (SPEC3, was dead code).*

6.3 **Follow-a-player.** `+attack` = `SpectateNext`, `SpectatePrev` impulses = previous, `+jump` = Join,
`+attack2` = drop to free-fly. `SetSpectatee(target)` sets `.enemy`; `SpectateNext/Prev` cycle valid
`IS_PLAYER` targets, honoring the `g_<gt>_spectate_enemies` anti-ghost mode (`SpectatorRules.CanSpectate`).
`SpectateCopy` mirrors the spectatee's origin/view/health/armor/weapon onto the spectator each frame.
*Port: **LANDED** — wired `SpectatorRules.CycleSpectatee` (was unwired) + a `SpectateCopy` into
`ObserverOrSpectatorThink` (SPEC1/LOOP3, P1).*

6.4 **`spectatee_status` STAT & networking.** `0` = live player, `etof(self)` → client-side `−1` = observing,
`etof(target) > 0` = spectating that entity. Sent in `ClientData_Send` (BIT(1)). The client uses it as
`player_currententnum`: when `> 0` the camera + HUD render from that entity's eyes; `−1` disables most HUD
panels. *Port: **LANDED** — added a `spectatee` byte to the owner snapshot + client follow-cam retarget
(SPEC2, P0).*

6.5 **`cmd spectate [client]` / `cmd join`.** `spectate <player>` follows that player; `spectate 0` drops to
plain observer; `spectate` (no arg) leaves the match to observe (when `sv_spectate`). `join` enters the match
if `joinAllowed`. *Port: **LANDED** — `CmdSpectate` now runs the real observer transition + honors the
`<client>` arg; `CmdJoin` clears the observer state (SPEC4, P1).*

6.6 **Spectator HUD.** Observing hint ("press jump to play"), the spectated player's name, and the
`sv_showspectators` "Spectators: a, b, c" footer. *Port: **LANDED** the observing hint + spectated name from
the networked `spectatee` status; the spectators-list footer is DEFERRED (P2, needs a list channel) (SPEC5).*

6.7 **One observer representation.** The live loop keys observer behavior off `IsObserver` (physics/damage),
NOT the `FragsStatus` scoreboard sentinel. The two must stay consistent. *Port: **LANDED** — the spectate
commands now flip `IsObserver` via `PutObserverInServer`, not just `FragsStatus`.*

---

## 7. Live loop wiring (`NetGame` → `ServerNet` → `GameWorld`)

The 72 Hz loop (`ServerNet.Tick` → `GameWorld.Frame` → `OnStartFrame` → per-client
`OnClientMove`(PreThink→move→PostThink) → `OnEndFrame`) is structurally faithful. Connect→observer→Join→Spawn,
the damage pipeline, and per-frame regen/drown/contents/fall all run live. The deep-dive's remaining wiring
gaps (respawn input, respawn-time networking, the live player→observer transition, follow-cam) are addressed
by §4 and §6 above. `PlayerFrame` once-per-frame consolidation (LOOP6) is intentional; the missing
spectator-block kick is DEFERRED (P2).
