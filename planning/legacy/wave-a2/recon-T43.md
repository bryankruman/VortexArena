# Recon T43 — Monster damage → pain/death/reset seam + global counters

**Status:** READ-ONLY recon. Scope: wire the already-written `MonsterAI.MarkPain`/`MarkDead`/`DropItem`/`Respawn` (dead code, ZERO callers) into the live damage pipeline so a monster victim runs the monster pain/death path instead of being treated as a player; add `Monster_Reset` (T14 deferred it), the corpse dead-think respawn/fade, the `monsters_total`/`monsters_killed` global counters, and feed the scoreboard map-stats row.

---

## 1. BASE SPEC (the authority — `Base/data/xonotic-data.pk3dir/qcsrc/common/monsters/sv_monsters.qc`)

### 1.1 Installation point (the seam in Base)
`Monster_Spawn` (sv_monsters.qc:1388) line **1457**:
```c
this.event_damage = Monster_Damage;
this.event_heal   = Monster_Heal;   // line 1458
this.reset        = Monster_Reset;  // line 1469
```
So in QC the monster carries its OWN `.event_damage` (=`Monster_Damage`), its own `.event_heal` (=`Monster_Heal`), and its own `.reset` (=`Monster_Reset`). The generic `Damage()` dispatcher (server/damage.qc) calls `this.event_damage(...)` — it never special-cases "player" in the QC engine; PlayerDamage is just the player's installed `event_damage`. **This is the exact model the port's `Entity.GtEventDamage` already implements** (see §3.2).

### 1.2 `Monster_Damage` (sv_monsters.qc:1083) — the pain/death event handler
Inputs: `(this, inflictor, attacker, damage, deathtype, .weaponentity, hitloc, force)`.

Order of operations (MIRROR EXACTLY):
1. **Invulnerability gate** (1085-1089): return (no damage) if ANY of —
   - `(spawnflags & MONSTERFLAG_INVINCIBLE) && deathtype != DEATH_KILL && !ITEM_DAMAGE_NEEDKILL(deathtype)`
   - `StatusEffects_active(STATUSEFFECT_SpawnShield, this) && deathtype != DEATH_KILL`
   - `deathtype == DEATH_FALL && this.draggedby != NULL` (a ridden monster ignores fall damage)
2. **Armor split** (1091): `v = healtharmor_applydamage(100, GetResource(this, RES_ARMOR) / 100, deathtype, damage); take = v.x;`
   See §1.7 for the exact `healtharmor_applydamage` body + the monster-armor quirk.
3. **`mr_pain` hook** (1095-1096): `take = mon.mr_pain(mon, this, take, attacker, deathtype);` — per-monster override may modify (usually returns `take` unchanged; some adjust).
4. **Apply take** (1098-1102): if `take` truthy → `TakeResource(this, RES_HEALTH, take)` + `Monster_Sound(monstersound_pain, 1.2, delaytoo=true, CH_PAIN)`.
5. Waypoint health update (1104-1105) — client/sprite (deferred).
6. `this.dmg_time = time;` (1107).
7. **Body-impact spamsound** (1109-1110): if `deathtype != DEATH_DROWN && != DEATH_FIRE && sound_allowed(...)` → `spamsound(this, CH_PAIN, SND_BODYIMPACT1, VOL_BASE, ATTEN_NORM)`.
8. **Self-knockback** (1112): `this.velocity += force * this.damageforcescale;` (NOTE: NO `damage_explosion_calcpush`, NO spawn-shield gate, NO `g_player_damageforcescale` — monsters use the raw `.damageforcescale` * force).
9. **Gib splashes** (1114-1121): if `deathtype != DEATH_DROWN && take` → `Violence_GibSplash_At(hitloc, force, 2, bound(0,take,200)/16,…)`; if `take>50` extra splash type 3; if `take>100` another. (Client VFX → deferred named no-op.)
10. **Death check** (1123-1145): if `GetResource(RES_HEALTH) <= 0`:
    - if `deathtype == DEATH_KILL` → `this.candrop = false` (killed by `mobkill` cmd: no loot).
    - `SUB_UseTargets(this, attacker, this.enemy);` (fires the monster's map targets on death).
    - `this.target2 = this.oldtarget2;` (restore original patrol target for respawn).
    - `Monster_Dead(this, attacker, gibbed)` where **`gibbed = (GetResource(RES_HEALTH) <= -100 || deathtype == DEATH_KILL)`**.
    - `WaypointSprite_Kill(this.sprite);`
    - **`MUTATOR_CALLHOOK(MonsterDies, this, attacker, deathtype);`** ← the monster-dies mutator hook (port: `NadeBonus.OnMonsterDies`, see §3.4).
    - if `(RES_HEALTH <= -100 || deathtype == DEATH_KILL)` (already gibbed) → `Violence_GibSplash(this,1,0.5,attacker)` + `setthink(SUB_Remove); nextthink = time + 0.1`.

**The gib threshold is hardcoded `-100`, NOT `sv_gibhealth`.** (sv_gibhealth=100 is the PLAYER corpse gib threshold; monsters use `-100` literal.) Parity trap.

### 1.3 `Monster_Dead` (sv_monsters.qc:1036) — become a corpse
`(this, attacker, gibbed)`:
1. `setthink(Monster_Dead_Think); nextthink = time; this.monster_lifetime = time + 5;` (corpse lives 5s before fade/respawn).
2. `monster_dropitem(this, attacker);` (loot drop — §1.5).
3. `Monster_Sound(monstersound_death, 0, false, CH_VOICE);`
4. **Kill counter** (1046-1047): `if (!(spawnflags & (MONSTERFLAG_SPAWNED | MONSTERFLAG_RESPAWNED))) ++monsters_killed;` — only NATURAL (map-placed, first-life) monsters count toward the killed total.
5. **Score** (1049-1051): `if (IS_PLAYER(attacker) && (autocvar_g_monsters_score_spawned || !(spawnflags & (SPAWNED|RESPAWNED)))) GameRules_scoring_add(attacker, SCORE, +autocvar_g_monsters_score_kill);` (shipped `g_monsters_score_kill=0`, `g_monsters_score_spawned=0` → no points by default).
6. `if (gibbed) --totalspawned;` (mob-command bookkeeping).
7. `if (!gibbed && this.mdl_dead) _setmodel(this, this.mdl_dead);` (swap to corpse model).
8. **Field changes** (1059-1075):
   - `event_damage = gibbed ? func_null : Monster_Dead_Damage;`
   - `event_heal = func_null;`
   - `solid = SOLID_CORPSE; takedamage = DAMAGE_AIM; deadflag = DEAD_DEAD; enemy = NULL;`
   - `set_movetype(MOVETYPE_TOSS); moveto = origin; settouch(Monster_Touch);`
   - **`this.reset = func_null;`** (a dead monster no longer round-resets — it'll respawn or fade).
   - `state = 0; attack_finished_single[0] = 0; effects = 0;`
   - `dphitcontentsmask &= ~DPCONTENTS_BODY;`
   - `if (!(flags & (FL_FLY|FL_SWIM))) velocity = '0 0 0';`
9. `CSQCModel_UnlinkEntity(this);` (client).
10. `mon.mr_death(mon, this);` (per-monster death override — e.g. set the death anim).

### 1.4 `Monster_Dead_Damage` (sv_monsters.qc:1018) — corpse damage (the ungibbed-corpse path)
`TakeResource(RES_HEALTH, damage); Violence_GibSplash_At(...);` and if `RES_HEALTH <= -50` → second splash, `--totalspawned`, `setthink(SUB_Remove); nextthink = time+0.1; event_damage = func_null`. (Corpse gibs at **-50** more health, distinct from the live -100.)

### 1.5 `Monster_Dead_Think` (sv_monsters.qc:965) + `Monster_Dead_Fade` (567)
- `Monster_Dead_Think`: `nextthink = time; mon.mr_deadthink(mon, this);` then `if (monster_lifetime != 0 && time >= monster_lifetime) Monster_Dead_Fade(this);`.
- `Monster_Dead_Fade`: if `Monster_Respawn_Check(this)` → set `MONSTERFLAG_RESPAWNED`, `setthink(Monster_Respawn); nextthink = time + respawntime; monster_lifetime = 0; deadflag = DEAD_RESPAWNING; event_damage=event_heal=func_null; takedamage=DAMAGE_NO;` restore `pos1/pos2` (DEATHPOINT writes them first), `SetResourceExplicit(RES_HEALTH, max_health); setmodel(MDL_Null)` (invisible while waiting). Else (`-- totalspawned; SUB_SetFade(time+3, 1)`).
- `Monster_Respawn_Check` (547): `if (deadflag==DEAD_DEAD && MUTATOR_CALLHOOK(MonsterRespawn)) return true; if (!autocvar_g_monsters_respawn || (spawnflags & MONSTERFLAG_NORESPAWN)) return false; return true;`
- `Monster_Respawn` (559): `Monster_Spawn(this, true, this.monsterdef);` (full re-spawn).

### 1.6 `monster_dropitem` (sv_monsters.qc:40) — loot on death
`if (!candrop || autocvar_g_monsters_drop_time <= 0) return;` `itemlist = this.monster_loot;` `if (spawnflags & MONSTERFLAG_MINIBOSS) itemlist = autocvar_g_monsters_miniboss_loot;` `MUTATOR_CALLHOOK(MonsterDropItem, this, itemlist, attacker); itemlist = M_ARGV(1,string);` `loot = Item_RandomFromList(itemlist);` if null return; spawn an item, `ITEM_SET_LOOT`, `colormap`, `setorigin(CENTER_OR_VIEWOFS)`, `velocity = randomvec()*175 + '0 0 325'`, `lifetime = drop_time`, `Item_Initialise`. (Port already has `MonsterFramework.DropItem`, §3.3.)

### 1.7 `Monster_Heal` (sv_monsters.qc:1148) — the heal event (Mage)
`(targ, inflictor, amount, limit)`: `true_limit = (limit != RES_LIMIT_NONE) ? limit : targ.max_health;` `if (GetResource(RES_HEALTH)<=0 || GetResource(RES_HEALTH)>=true_limit) return false;` `GiveResourceWithLimit(targ, RES_HEALTH, amount, true_limit);` sprite update; `return true;`. Installed as `this.event_heal` (1458). The Mage's radius heal calls the target's `event_heal`.

### 1.8 `Monster_Reset` (sv_monsters.qc:999) — round restart (T14 deferred)
`if (spawnflags & MONSTERFLAG_SPAWNED) { Monster_Remove(this); return; }` (command/wave-spawned monsters are deleted on reset, not restored). Else: `setorigin(pos1); angles = pos2; SetResourceExplicit(RES_HEALTH, max_health); velocity='0 0 0'; enemy=NULL; goalentity=NULL; attack_finished_single[0]=0; moveto=origin;`. Assigned to `this.reset` at spawn (1469); the round handler calls every entity's `.reset` on restart.

### 1.9 `monsters_setstatus` (sv_monsters.qc:34) + the counters
```c
void monsters_setstatus(entity this) {
    STAT(MONSTERS_TOTAL, this)  = monsters_total;
    STAT(MONSTERS_KILLED, this) = monsters_killed;
}
```
`monsters_total` / `monsters_killed` are server **globals** (defined sv_monsters.qh). Incremented:
- `monsters_total`: `Monster_Spawn` (sv_monsters.qc:1427) `if (!(spawnflags & SPAWNED) && !(spawnflags & RESPAWNED)) ++monsters_total;` (natural first-life spawns only).
- `monsters_killed`: `Monster_Dead` (1047) as in §1.3.4.
`monsters_setstatus` is a per-client status function (registered via a Client_SetStatus path) — it pushes the two globals into each player's STAT block every frame, so the client scoreboard's `Scoreboard_MapStats_Draw` reads `STAT(MONSTERS_TOTAL/KILLED)`.

### 1.10 `healtharmor_applydamage` (common/util.qc:1413) — the armor split (verbatim)
```c
vector healtharmor_applydamage(float a, float armorblock, int deathtype, float damage) {
    if (DEATH_IS(deathtype, DEATH_DROWN)) armorblock = 0;
    if (deathtype & HITTYPE_ARMORPIERCE)  armorblock = 0;
    v.y = bound(0, damage * armorblock, a);   // save
    v.x = bound(0, damage - v.y, damage);     // take
    v.z = 0;  return v;
}
```
**Monster-armor quirk (parity trap):** `Monster_Damage` calls `healtharmor_applydamage(100, RES_ARMOR/100, …)`. Monster `RES_ARMOR` is `bound(0.2, 0.5*skillmod, 0.9)` (sv_monsters.qc:1328) — a small FRACTION ~0.2..0.9. So `armorblock = RES_ARMOR/100 = 0.002..0.009` → monster armor blocks **almost nothing** (e.g. 100 dmg → save ≤ 0.9, take ≈ 99.1). The Zombie "block" (`RES_ARMOR = 0.9` → armorblock 0.009) is likewise near-cosmetic numerically. **Do NOT "fix" this; replicate `ArmorValue / 100` exactly.** The port stores `Entity.ArmorValue` as the same fraction (Zombie sets `e.ArmorValue = 0.9f`; default block `g_monsters_armor_blockpercent=0.6`).

---

## 2. CVARS + SHIPPED DEFAULTS (from `Base/.../monsters.cfg` + balance)
| cvar | shipped default | port usage / discrepancy |
|---|---|---|
| `g_monsters` | 1 | master switch (MasterSwitchEnabled — OK) |
| `g_monsters_skill` | 1 | ResolveSkill fallback OK |
| `g_monsters_respawn` | 1 | RespawnCheck `Cvar(...,1)` OK |
| `g_monsters_respawn_delay` | **20** | **MonsterAI.cs:263 + MonsterState.RespawnTime use 10 — WRONG, should be 20** |
| `g_monsters_spawnshieldtime` | **2** | **Setup/Respawn use `Cvar(...,1f)` — WRONG fallback, should be 2** |
| `g_monsters_drop_time` | 10 | DropItem `Cvar(...,10)` OK |
| `g_monsters_score_kill` | 0 | NOT applied in MarkDead (missing GameRules_scoring_add SCORE) — default 0 hides it but non-default servers lose it |
| `g_monsters_score_spawned` | 0 | not modeled (controls whether spawned monsters score) |
| `g_monsters_miniboss_chance` | 5 | Setup `Cvar(...,0)` fallback (cvar present → 5; only differs headless) |
| `g_monsters_miniboss_healthboost` | 100 | Setup `Cvar(...,100)` OK |
| `g_monsters_miniboss_loot` | "vortex" | DropItem `CvarString(...,"vortex")` OK |
| `g_monsters_target_range` | 2000 | OK |
| `g_monsters_attack_range` | 120 | OK |
| `g_monsters_damageforcescale` | 0.8 | OK (per-monster overrides in each Spawn) |
| `g_monsters_armor_blockpercent` | 0.6 | used by Zombie block restore |
| `g_monsters_healthbars` | 0 | sprite (deferred) |
| `sv_gibhealth` | 100 | PLAYER gib only — monsters use literal -100 (don't apply sv_gibhealth to monsters) |

**Two real default bugs to fix in this task: `RespawnTime` 10→20 and `spawnshieldtime` fallback 1→2.**

---

## 3. PORT SIDE — what exists, what's dead, the exact seams

### 3.1 `MonsterAI.cs` (`src/XonoticGodot.Common/Gameplay/Monsters/MonsterAI.cs`, 1477 lines)
- **`MarkPain` (lines 1238-1274)** — port of `Monster_Damage` steps 3-10. **ZERO callers (confirmed by grep).** Receives `take` ALREADY split (no `healtharmor_applydamage` inside). Has the invincible/spawnshield gate (1241-1247), a Mage-shield multiplier (1250-1254), apply-take + pain sound (1256-1263), `Velocity += force * DamageForceScale` (1266), death branch → `MarkDead` with `gibbed = Health<=-100 || isKill` (1268-1272).
  - **GAP vs Base:** does the armor split *outside* (no caller does it); missing `dmg_time`, missing body-impact `spamsound` (step 7), missing `SUB_UseTargets`/`target2` restore (step 10b/c), missing gib-splash thresholds (step 9), missing `WaypointSprite` (client, OK to skip).
- **`MarkDead` (lines 1281-1319)** — port of `Monster_Dead`. **ZERO callers.** Sets `Lifetime=time+5`, `candrop=false` on KILL, `DropItem`, death sound, **fires `Combat.Death.Call` (1296)** (this is the obituary/score bus), corpse fields (DeadFlag.Dead/Solid.Corpse/TakeDamage.Aim/MoveType.Toss/Touch reset), gibbed→remove at +0.1s.
  - **GAP vs Base:** does NOT increment `monsters_killed` (no global counter exists); does NOT call the `MonsterDies` mutator hook (NadeBonus.OnMonsterDies); does NOT call `SUB_UseTargets`; does NOT `--totalspawned`; does NOT swap to `mdl_dead`; does NOT call a per-monster `mr_death` (no such descriptor hook — see §3.5).
- **`DeadThink` (lines 1008-1023)** — port of `Monster_Dead_Think`/`Monster_Dead_Fade`. Already called from `RunThink`'s dead branch (line 908). Respawn (`Respawn`, 1334-1375) and fade are present.
- **`Respawn` (1334-1375)** — port of `Monster_Respawn`/fade respawn branch. Uses `RespawnAtDeathPoint`, re-stamps live fields after `RespawnTime`.
- **`RunThink` (896-947)**: the dead branch (906-910) calls `DeadThink`. The lifetime-expiry suicide (899-903) uses `DeathTypes.Kill`. **This is where a monster's corpse think runs — it IS wired** (SimulationLoop.RunThink fires `e.Think`, which Zombie/etc. route to `MonsterAI.RunThink`).
- **`Setup` (198-282)**: stamps engine fields, creates `MonsterState`. **DOES NOT install any `event_damage`/`GtEventDamage`/`Reset` delegate.** This is the missing wiring.
- **NO `Reset` method exists** (grep: no `MonsterReset`). T14 deferred it (see the XML doc on `SpawnFromMap` lines 295-301: "The QC `this.reset = Monster_Reset` round-restart hook is omitted").
- **NO `monsters_total`/`monsters_killed` counters** (grep in src = none).

### 3.2 `DamageSystem.cs` — the routing seam (`src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs`)
**`EventDamage` (lines 276-293)** — the branch the brief asks about:
```csharp
if (!IsPlayer(targ) && targ.GtEventDamage is not null) { targ.GtEventDamage(...); return; }   // 283-287
if (targ.IsCorpse || IsDead(targ)) PlayerCorpseDamage(...);                                     // 289-290
else PlayerDamage(...);                                                                         // 291-292
```
`IsPlayer` (688-689) = `Client flag && !IsCorpse`. **A monster has `EntFlags.Monster` (not Client), `GtEventDamage == null`, `IsCorpse == false`, `DeadState == No` → it falls to `PlayerDamage` (line 292) — the BUG.** It's processed with player handicap/spawn-shield, then `Killed()` (line 441) sets it `Solid.Corpse + IsCorpse=true` and fires `Combat.Death` once. So Invasion's kill-count works today *by accident via the player path*, but with NO monster pain/anim/sound/knockback-via-damageforcescale, NO loot, NO -100 gib, NO MonsterDies hook, NO Monster_Dead_Think respawn, NO monsters_killed counter, and the monster gets player-corpse semantics.

**TWO fix options (recommend A):**
- **Option A (PREFERRED — minimal/zero DamageSystem edit):** install a `GtEventDamage` shim on the monster entity in `MonsterAI.Setup` (and re-install in `Respawn` since `MarkDead` clears it / corpse path). The shim signature is EXACTLY `Action<Entity, Entity?, Entity?, string, float, Vector3, Vector3>` = `(self, inflictor, attacker, deathtype, damage, hitloc, force)`. Because `EventDamage` already routes a non-player-with-`GtEventDamage` to it and `return`s, the monster never reaches `PlayerDamage`. **This reuses the proven Onslaught/Breakable/secret-trigger seam (OnslaughtControlPoint installs `GtEventDamage` identically). No DamageSystem.cs edit required.**
  - **Caveat:** after `MarkDead` the monster is `DeadFlag.Dead` + `Solid.Corpse` but `IsCorpse` stays false and `GtEventDamage` stays installed → a corpse hit re-enters the SAME shim (still non-player + GtEventDamage). The shim must internally branch on `DeadState`: live → split + `MarkPain`; dead → `Monster_Dead_Damage` (corpse -50 gib). This is faithful to QC (which swaps `event_damage` to `Monster_Dead_Damage`); in the port the single shim dispatches by `DeadState` instead of swapping the delegate. Acceptable + cleaner.
- **Option B (explicit branch in EventDamage):** add `if (!IsPlayer(targ) && (targ.Flags & EntFlags.Monster) != 0) { MonsterAI.MonsterEventDamage(...); return; }` BEFORE the corpse/PlayerDamage branch (so it catches the dead monster too, before `IsDead(targ)` sends it to PlayerCorpseDamage). This is a 3-line edit to DamageSystem.cs (owned by no other A2 task per the brief — but see §5). Use only if Option A's corpse re-entry is deemed too subtle.

**Recommendation: Option A.** It needs no shared-file edit and matches how every other non-player damageable already works in this port.

### 3.3 `MonsterFramework.DropItem` (lines 210-237) — already a faithful `monster_dropitem`. Reused by `MarkDead`. OK (the `MonsterDropItem` mutator hook is omitted; acceptable, no port hook exists).

### 3.4 `NadeBonus.OnMonsterDies` (`src/XonoticGodot.Common/Gameplay/Nades/NadeBonus.cs:108`) — the port's `MUTATOR_CALLHOOK(MonsterDies)` equivalent (direct method, NOT a hook chain). `MarkDead` should call `NadeBonus.OnMonsterDies(attacker, self, monsterWasSpawned: st.Spawned || st.Respawned)`. (There is NO `MutatorHooks.MonsterDies` chain — confirmed by grep; the only MonsterDies is this method + Invasion's inline handling via `Combat.Death`.)

### 3.5 The `Monster` descriptor (`src/XonoticGodot.Common/Gameplay/EntityClasses.cs:6-23`) — has `Spawn`/`Think`/`Attack` virtuals **but NO `Pain`/`Death`/`DeadThink` hook.** QC's `mr_pain`/`mr_death`/`mr_deadthink` per-monster overrides are therefore NOT represented. For T43 parity this is mostly fine (the 5 ported monsters' `mr_pain` return `take` unchanged and `mr_death` only sets the death anim, which is client-render). **Decision:** the generic `MarkPain`/`MarkDead` already cover the shared behavior; do NOT add descriptor Pain/Death hooks unless a specific monster needs it (none of the 5 do meaningfully on the server). Document as a deliberate deviation.

### 3.6 `Entity` fields (all PRESENT — no new partial needed for the core):
- `MaxHealth`, `Health`, `DeadState`, `Skin`, `Effects`, `Team`, `SpawnFlags`, `Velocity`, `MoveType`, `Solid`, `TakeDamage`, `Touch`, `Think`, `Enemy`, `GoalEntity` (Entity.cs).
- `ArmorValue`, `MaxArmorValue` (Items/EntityResources.cs).
- `GtEventDamage` (GameTypes/EntityGametypeState.cs:82) ← the seam delegate.
- `GtWaveMonster` (EntityGametypeState.cs:95), `IsCorpse`/`DamageForceScale`/`PauseRegenFinished` (Damage/DamageEntityState.cs).
- `MonsterSkill`/`Spawnmob`/`NoAlign`/`MonsterMoveFlags` (Monsters/MonsterSpawnFuncs.cs:113-126).
- **`GetResource(ResourceType.Armor)` / `TakeResource`** exist (EntityResources.cs) — but monster armor uses `Entity.ArmorValue` directly (a fraction), so the shim's split reads `e.ArmorValue / 100f` to mirror QC `RES_ARMOR/100`. (Confirm whether monsters set `ArmorValue` or `RES_ARMOR`; Zombie sets `e.ArmorValue`. Use `ArmorValue`.)

### 3.7 `ScoreboardPanel.cs` (`game/hud/ScoreboardPanel.cs`)
- `MonstersKilled` / `MonstersTotal` settable surfaces ALREADY EXIST (lines 88-89, default -1). `DrawMapStats` (729-751) already renders the `Monsters killed: K/T` row gated on `MonstersTotal > 0`. **NO panel change needed** — the gap is the FEED: nothing sets these (only `FragLimit` is set in NetGame.cs:1080). See §4 networking.

### 3.8 Live-loop confirmation
- Monsters ARE spawned + ticked: `Invasion.SpawnMonsterDef` → `MonsterAI.SpawnMonster` → `SpawnFromMap` wires `e.Think` → `SimulationLoop.RunThink` (sv_phys.c:1015 port, lines 213-240) fires it. `MonsterSpawnFuncs` wires map-placed `monster_*` via `MapObjectsRegistry`.
- `Combat.Death` is consumed by Invasion (`OnDeath` 408-437, acts on monster victims) and turret handlers; **every player-gametype handler guards `if (ev.Victim is not Player) return false`** (verified Deathmatch, Scores) → firing Death once from `MarkDead` for a monster victim is safe and won't corrupt player scores.

---

## 4. STAT / NETWORKING — `monsters_total`/`monsters_killed` to the HUD (the hotFile question)
QC nets these via per-client `STAT(MONSTERS_TOTAL/KILLED)` (`monsters_setstatus`). The port has **no STAT block for map-stats** and the scoreboard surfaces are unfed. Two server-side pieces are needed:
1. **Counters (NEW, in MonsterAI):** static `MonstersTotal`/`MonstersKilled` ints. `MonstersTotal++` in `Setup`/`SpawnFromMap` for natural first-life spawns (`!Spawned && !Respawned`); `MonstersKilled++` in `MarkDead` (same gate). Add a `ResetCounters()` for map/round reset. **No shared-file edit** (lives in MonsterAI.cs, this task's file).
2. **Feed to HUD (SHARED — hotFile):** the listen-server path (`game/net/NetGame.cs`, owned by **T37**) sets the other scoreboard header fields (FragLimit at NetGame.cs:1080, MapName/TimeLimit ~1077-1081 inside the scoreboard-header setter). The minimal edit there: after the existing `_scoreboard.FragLimit = …` setter, add
   ```csharp
   _scoreboard.MonstersTotal  = MonsterAI.MonstersTotal  > 0 ? MonsterAI.MonstersTotal  : -1;
   _scoreboard.MonstersKilled = MonsterAI.MonstersKilled;
   ```
   (Server-authoritative on a listen server; a pure remote client would need a STAT in `ServerNet.cs`/`ClientNet`, also T37-owned — defer the pure-client net path as a follow-up; the listen-server feed covers `--host`/Create-match, the real play path per memory.)
   **Flagged in hotFileNeeds: `game/net/NetGame.cs` (T37).** Do NOT edit a stat-block in `ServerNet.cs` for v1 — keep the feed to the listen-server scoreboard setter only; pure-client map-stats networking is out of scope (note as a deferred seam).

---

## 5. CONFLICT / OWNERSHIP NOTES
- **DamageSystem.cs** (NOT in any A2 owner set per the brief; T43 may edit it) — but with **Option A we DON'T edit it at all**. If Option B is chosen, the edit is the 3-line monster branch in `EventDamage` (276-293). Recommend Option A to keep zero contention.
- **NadeBonus.cs** — call its existing `OnMonsterDies` (no edit; it's a public static method in a T11-area file, but calling it is not an edit).
- **`MutatorHooks.cs` / `Registries.cs` / `GameWorld.cs` / `MapObjectsRegistry.cs`** — **NOT touched.** Monsters auto-register via `[Monster]` attribute (confirmed: `[Monster] public sealed class Zombie`); the damage seam is `GtEventDamage` (per-entity delegate), not a registry table. The A1 lesson holds: this is new/local code, not shared-table edits.
- **`game/net/NetGame.cs` (T37)** — the ONE shared file needing an edit (the 2-line scoreboard feed in §4). Hand T37 the snippet.
- **`game/hud/ScoreboardPanel.cs` (listed in T43's brief)** — **no edit needed**; the surfaces + draw already exist. (Confirm with T43; brief said "monsters total/killed display" but it's already built.)
- Registration of monster spawnfuncs is in `MapObjectsRegistry` (T36-owned) but already done for monsters; no new registration for T43.

---

## 6. IMPLEMENTATION PLAN (mirrors Base)
1. **`MonsterAI.cs` — new `MonsterEventDamage` shim** (the `Monster_Damage` front: armor split + dead/live dispatch):
   ```
   public static void MonsterEventDamage(Entity self, Entity? inflictor, Entity? attacker,
       string deathType, float damage, Vector3 hitLoc, Vector3 force) {
     var st = StateOf(self); if (st is null) return;
     if (self.DeadState != DeadFlag.No) { MonsterDeadDamage(self, st, attacker, damage, hitLoc, force); return; }
     // QC Monster_Damage:1091 — armorblock = ArmorValue/100 (the monster quirk), drown/armorpierce zero it.
     float armorBlock = (DeathTypes.BypassesArmor(deathType)) ? 0f : self.ArmorValue / 100f;
     float save = Clamp(damage*armorBlock, 0f, 100f);
     float take = Clamp(damage - save, 0f, damage);
     MarkPain(self, st, take, attacker, deathType, force);   // existing
   }
   ```
   - Add the missing `Monster_Damage` bits INSIDE MarkPain (or the shim): `dmg_time`/PauseRegen (optional), `SUB_UseTargets` on death (call existing target-fire util with self/attacker/enemy), `target2 = oldtarget2`. Body-impact spamsound + gib splashes = named no-op (client).
2. **`MonsterAI.cs` — new `MonsterDeadDamage`** (port `Monster_Dead_Damage`:1018): `self.Health -= damage;` gib at `<= -50` → remove at +0.1s. (Splashes = no-op.)
3. **Install the shim** in `Setup` (after creating state) AND in `Respawn`'s revive lambda: `e.GtEventDamage = MonsterEventDamage;`. (MarkDead leaves it installed so corpse hits re-enter the shim's dead branch — faithful.)
4. **`MarkDead` additions** (mirror `Monster_Dead`:1046-1080):
   - `if (!(st.Spawned || st.Respawned)) MonstersKilled++;` (the killed counter).
   - `NadeBonus.OnMonsterDies(attacker, self, st.Spawned || st.Respawned);` (the MonsterDies hook).
   - keep the existing `Combat.Death.Call` (it's the obituary/score bus — fires once, safe).
   - (optional) `mdl_dead` model swap is client; SUB_UseTargets on death already in step 1.
5. **`MonsterAI.cs` — `MonstersTotal`/`MonstersKilled` static counters + `ResetCounters()`.** `MonstersTotal++` in `SpawnFromMap` for natural spawns (`!st.Spawned && !st.Respawned`), placed where QC does (sv_monsters.qc:1427, after the team gate, before model set). Call `ResetCounters()` from the map/gametype reset path (wire-up: the world boot / gametype OnInit — minimal, in MonsterAI or via Invasion.OnInit calling it).
6. **`MonsterAI.cs` — new `Reset(Entity)`** (port `Monster_Reset`:999): SPAWNED → `Remove`; else restore pos1/pos2/health/clear enemy/goal/attack. Install on the entity. **Entity has no `Reset` delegate** (grep confirmed) → either (a) add a `public Action<Entity>? Reset;` to a NEW Entity partial in the Monsters folder (ADR-0007, like MonsterSpawnFuncs added `MonsterSkill`), and have the round-restart path call it, OR (b) register the monster with the existing round-reset mechanism the gametypes use (`Round.Reset()` is gametype-level; map entities reset via…). **Recommend (a): add `Entity.Reset` delegate in a new Monsters partial + have the reset caller invoke it.** Confirm who calls per-entity reset on round restart (search `.Reset` callers — currently only gametype `Round.Reset()`; map-object resets go through `sel.Reset()` on their own state objects, not an Entity delegate). For T43, install `e.Reset = Reset` and document that the round-restart wiring (calling it) is the same deferred seam T14 left — provide the delegate + a `MonsterAI.ResetAll()` helper the round handler can call over `FindByClass("monster")`.
7. **Fix the two default bugs:** `MonsterState.RespawnTime = 20f` and `Cvar("g_monsters_respawn_delay", 20f)` (line 263); `Cvar("g_monsters_spawnshieldtime", 2f)` (lines 277, 1368).
8. **`game/net/NetGame.cs` (T37 hotFile):** feed `_scoreboard.MonstersTotal/Killed` from `MonsterAI.MonstersTotal/Killed` in the scoreboard-header setter (snippet in §4).

---

## 7. TEST PLAN (xUnit, `[Collection("GlobalState")]`, harness like `InvasionMonsterSpawnTests`)
1. **Routing:** spawn a monster (Zombie) on a floor via `MonsterAI.SpawnMonster`/`SpawnFromMap`; `Combat.Damage(monster, atk, atk, 50, FromWeapon("blaster"), …)`. Assert: health dropped by ~50 (armor quirk ≈ no block), `Velocity` got `force*DamageForceScale` (NOT player force scale), and the monster did NOT acquire `IsCorpse=true`/player corpse semantics. (Proves the shim ran, not PlayerDamage.)
2. **Death → corpse:** damage past 0 (but > -100). Assert `DeadState==Dead`, `Solid==Corpse`, `MoveType==Toss`, `Lifetime≈time+5`, `MonstersKilled` incremented by 1 (natural spawn). Assert `Combat.Death` fired once (subscribe a counter).
3. **Gib threshold:** one hit taking health below -100 (or `DeathTypes.Kill`). Assert `gibbed` path → entity removed shortly (NextThink set, removed after +0.1 tick). Distinct from the corpse -50 path.
4. **MonsterDies hook:** spawn a NON-spawned monster (map-placed: `Spawned=false`), kill with a player attacker; assert `NadeBonus` bonus applied (or the hook's observable effect). Spawned/wave monster (Invasion) → no MonsterDies bonus (QC: spawned monsters award nothing).
5. **Counters:** spawn N natural monsters → `MonstersTotal == N`; kill M → `MonstersKilled == M`; a RESPAWNED monster's death does NOT increment either (gate on Spawned/Respawned). `ResetCounters()` zeroes both.
6. **Heal (Mage path):** a damaged monster's `event_heal`/`Monster_Heal` (if installed) raises health up to max_health, not above; dead monster can't be healed.
7. **Reset:** a non-SPAWNED monster after `Reset` returns to pos1/pos2 at max_health, enemy cleared; a SPAWNED monster is removed.
8. **Corpse damage:** hit a dead (corpse) monster → routes to `MonsterDeadDamage` (health drops, no second Death event), gibs at -50.
9. **Defaults:** assert `MonsterState.RespawnTime == 20` and spawnshield fallback 2 (regression on the two cfg defaults).
10. **End-to-end (existing harness):** an Invasion wave monster killed by player damage advances `Wave.Killed` AND increments `MonsterAI.MonstersKilled` (the two were previously coupled only by the accidental player path).

---

## 8. RISKS / PARITY TRAPS
- **Gib thresholds:** live monster gibs at **-100** (literal, NOT sv_gibhealth); corpse gibs at **-50**. Don't unify with the player's `sv_gibhealth=100`.
- **Monster armor is `RES_ARMOR/100`** → near-zero blocking by design. Use `ArmorValue / 100f`; don't treat ArmorValue as a normal 0..1 blockpercent.
- **Self-knockback is raw** `force * DamageForceScale` — NO `damage_explosion_calcpush`, NO spawn-shield gate, NO player force scale (unlike `ApplyKnockback`). The general `ApplyKnockback` in `Damage.Apply` runs BEFORE `EventDamage` and uses player rules; for a monster `DamageForceScale` is set (e.g. 0.55) so `ApplyKnockback` ALSO pushes it (with calcpush). **Potential double-push:** `Damage.Apply` calls `ApplyKnockback` (line 234) for ALL targets, THEN `EventDamage`→shim→`MarkPain` adds `force*DamageForceScale` again (line 1266). QC's `Monster_Damage` does the velocity add itself AND the generic `Damage()` "apply push" block (damage.qc:671) ALSO runs for the monster (it's pushable). So QC ALSO double-applies? — re-check: QC damage.qc apply-push runs for `targ.damageforcescale && force`, which a monster satisfies → monster gets BOTH the generic calcpush AND the `Monster_Damage` raw add. **So the port's double-push is actually FAITHFUL.** Verify with a knockback test but do not "fix" by removing one.
- **Death fires once:** ensure the monster reaches the shim (Option A) so it never also hits the player `Killed()` path → `Combat.Death` fires exactly once. With Option A this is guaranteed (EventDamage returns after GtEventDamage).
- **Corpse re-entry (Option A):** after MarkDead, `GtEventDamage` stays installed + `IsCorpse` false → the shim's `DeadState` branch must catch the corpse hit (route to `MonsterDeadDamage`), else it'd re-run MarkPain on a corpse. Tested in test #8.
- **`SUB_UseTargets` on death:** find the port's target-fire util (TargetUtilities) and pass `(self, attacker, enemy)`; missing it means map triggers tied to a monster's death won't fire (low impact on stock maps but part of parity).
- **`Reset` wiring:** the round-restart caller for per-entity `.reset` is the same seam T14 deferred — providing `e.Reset` + a `ResetAll()` helper is in-scope; the round-handler invoking it may remain a documented follow-up if no round-reset path exists for map entities yet.
- **No descriptor `mr_pain`/`mr_death`:** deliberate — the 5 ported monsters don't need server-side per-type pain/death beyond the generic path. If a future monster does, add the hook then.
- **`monster_skill` vs `Skill`:** Invasion stamps `st.Skill` post-spawn (MonsterSkill scaling affects damage/speed, not spawn health) — counters/death don't touch skill, no interaction.
