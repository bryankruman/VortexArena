# Recon T51 — Remaining mutators (wave 2) + Overkill weapons + doublejump fix

Read-only recon. Faithful spec from Base QuakeC + exact port seams. Author: recon agent. Date 2026-06-07.

cwd = `C:/Users/Bryan/Projects/Xonotic`. Base = `Base/data/xonotic-data.pk3dir/qcsrc`. Port = `XonoticGodot/src`, `XonoticGodot/game`.

---

## 0. Registration model (CONFIRMED — the A1 lesson holds)

Reflection auto-registration, NO manual tables to edit:
- `XonoticGodot/src/XonoticGodot.Common/Framework/Registry.cs:18-22` defines `[Weapon] [Mutator] [GameType] [Monster] [Item]` (all subclass `GameRegistryAttribute`).
- `XonoticGodot/src/XonoticGodot.Common/Gameplay/Registries.cs:79-130` `GameRegistries.Bootstrap()` scans all loaded assemblies for `GameRegistryAttribute`, `Activator.CreateInstance`, dispatches by base type into `Registry<T>`, then `Sort()` + `Weapons.ConfigureAll()`.
- **A new `[Mutator]` class file or `[Weapon]` class file is auto-discovered. NO edit to Registries.cs / GameInit.cs needed for registration.** `GameInit.cs` only needs editing for non-registry *system installs* (it currently installs Movement/Damage/MapObjects/etc.). T51 needs NO GameInit edit for the mutators/weapons themselves.
- Mutator activation: `XonoticGodot/.../Mutators/MutatorActivation.cs` — `Apply()` walks `Mutators.All`, calls `Add()` (→ `mut.Hook()`) when `IsEnabled`, `Remove()` (→ `mut.Unhook()`) otherwise. Idempotent via `mut.Added`.
- Mutator pattern to mirror: `XonoticGodot/.../Mutators/VampireHookMutator.cs` (canonical: `[Mutator]`, ctor sets `NetName`, `IsEnabled => Api.Cvars...`, `Hook()` adds handlers + reads cvars, `Unhook()` removes; handlers are `bool OnX(ref MutatorHooks.XArgs args)` returning `false` normally / `true` for "forbid/handled").
- Weapon pattern to mirror: `XonoticGodot/.../Weapons/Machinegun.cs` (`[Weapon]`, ctor sets identity/`NetName`/`AmmoType`/`SpawnFlags`/`Color`/models, `Configure()` reads `g_balance_*` via `Bal()/BalInt()/BalBool()`, `WrThink(actor,slot,fire)`, `RefireFor/AnimtimeFor`). Balance read fallbacks must be the SHIPPED `bal-wep-xonotic.cfg` numbers.

Per-entity mutator fields live in `XonoticGodot/.../Mutators/EntityMutatorState.cs` (a `partial class Entity` NEW file — already T51-owned, modified today). Add new `.float`/`.bool` fields there (e.g. campcheck timers, oknex charge). `MutatorConstants.MaxWeaponSlots = 2`.

---

## 1. doublejump FIX (P1 — the headline correctness bug)

### Base spec — `common/mutators/mutator/doublejump/doublejump.qc` (full file, 36 lines)
```
#ifdef SVQC  REGISTER_MUTATOR(doublejump, autocvar_sv_doublejump);  #elif CSQC REGISTER_MUTATOR(doublejump, true);
#define PHYS_DOUBLEJUMP(s)  STAT(DOUBLEJUMP, s)
MUTATOR_HOOKFUNCTION(doublejump, PlayerJump) {
    entity player = M_ARGV(0, entity);
    if (PHYS_DOUBLEJUMP(player)) {
        tracebox(player.origin + '0 0 0.01', player.mins, player.maxs, player.origin - '0 0 0.01', MOVE_NORMAL, player);
        if (trace_fraction < 1 && trace_plane_normal_z > 0.7) {
            M_ARGV(2, bool) = true;   // grant the air-jump (the `doublejump` out flag)
            // we MUST clip velocity here!
            float f = player.velocity * trace_plane_normal;   // dot
            if (f < 0)  player.velocity -= f * trace_plane_normal;
        }
    }
}
```
Behavior: doublejump is **conditional**. Only when a tracebox down 0.01u below the player's feet hits a surface (`trace_fraction<1`) whose normal is steep-enough-to-stand-on (`trace_plane_normal_z > 0.7`, i.e. a floor/walkable ramp, not a wall) does it grant the extra jump AND clip the velocity into the plane (kill into-plane component). So a Q2-doublejump only triggers when you're essentially on/just-above the ground — it is NOT a free midair re-jump. Cvar `sv_doublejump` default **0** (`xonotic-server.cfg:91`); some physics presets set it 1 (Q2/Q2a/Fruit/Warsow/XDF).

### Current port BUG — `XonoticGodot/src/XonoticGodot.Common/Physics/PlayerPhysics.cs`
- `MovementParameters.cs:61` `public bool DoubleJump;` ; `:128` reads `prefix+"doublejump"`; `:189` default `false`.
- `PlayerPhysics.cs:754` `bool doublejump = mp.DoubleJump;` → fed straight into the gate at **`:781`** `if (!doublejump && (player.Flags & EntFlags.OnGround) == 0) return ...;`. So with `sv_doublejump 1`, `doublejump` is **unconditionally true** every frame → player can re-jump in midair with NO surface trace and NO velocity clip. This is the bug: the port treats `sv_doublejump` as an unconditional air-jump.

### Fix plan (faithful)
Two acceptable shapes; **prefer (A)** (a real DoublejumpMutator, matching every other mutator + how Base structures it):
- **(A) New `XonoticGodot/.../Mutators/DoublejumpMutator.cs` `[Mutator]`** that subscribes `MutatorHooks.PlayerJump` and reproduces the QC body: tracebox `origin+(0,0,0.01)` with `player.Mins/Maxs` down to `origin-(0,0,0.01)`, `MoveFilter.Normal`, ignore self; if `tr.Fraction<1 && tr.PlaneNormal.Z>0.7` → `args.Multijump = true;` and clip `f = Dot(player.Velocity, tr.PlaneNormal); if (f<0) player.Velocity -= f*tr.PlaneNormal;`. `IsEnabled => Api.Cvars.GetFloat("sv_doublejump") != 0f`. The PlayerJump hook chain is ALREADY pumped at `PlayerPhysics.cs:760` (`MutatorHooks.PlayerJump.Call(ref pj)`), and `args.Multijump` flows back into `doublejump` at `:763`.
  - **AND remove the unconditional grant**: change `PlayerPhysics.cs:754` `bool doublejump = mp.DoubleJump;` → `bool doublejump = false;` so `sv_doublejump` no longer pre-grants the air-jump (the mutator now governs it). `MovementParameters.DoubleJump` becomes informational/unused for the gate (can keep the field; the *consumer* at 754 is the change). **This is a 1-line edit to a T30-sensitive file (PlayerPhysics.cs) — see hotFileNeeds.**
- **(B)** Inline the tracebox+clip at `PlayerPhysics.cs:754` guarded by `mp.DoubleJump` (no new mutator). Less faithful to Base's mutator structure; only choose if the orchestrator wants zero new mutator files. Still requires the same `:754` edit.
- **Parity note**: with default `sv_doublejump 0`, both QC and the fixed port grant nothing — the golden movement traces (T30) must be UNCHANGED. The fix only alters behavior when `sv_doublejump != 0`, which the golden traces do not exercise. Verify the golden traces still pass (they should be byte-identical because `:754` now starts `false` exactly as `mp.DoubleJump==false` did).

---

## 2. hook mutator — `g_grappling_hook` (P1)

### Base spec — `common/mutators/mutator/hook/sv_hook.qc` (full)
- `AUTOCVAR(g_grappling_hook_useammo, bool, false)`. `g_grappling_hook` is a STRING cvar read via `expr_evaluate(cvar_string("g_grappling_hook"))` (default "0", `mutators.cfg:418`; ruleset-instahook sets 1).
- `REGISTER_MUTATOR(hook, ...)` with `MUTATOR_ONADD { g_grappling_hook = true; if(!useammo) WEP_HOOK.ammo_factor = 0; }` and `MUTATOR_ONROLLBACK_OR_REMOVE { g_grappling_hook = false; if(!useammo) WEP_HOOK.ammo_factor = 1; }` — i.e. when useammo is off, the hook costs no fuel (ammo_factor 0).
- `MUTATOR_HOOKFUNCTION(hook, SetStartItems)`: if `g_grappling_hook_useammo` → `start_items |= ITEM_FuelRegen.m_itemid; start_ammo_fuel = max(start_ammo_fuel, cvar("g_balance_fuel_rotstable")); warmup_start_ammo_fuel = max(..)`.
- `MUTATOR_HOOKFUNCTION(hook, PlayerSpawn)`: `player.offhand = OFFHAND_HOOK;`
- `MUTATOR_HOOKFUNCTION(hook, FilterItem)`: `return item.weapon == WEP_HOOK.m_id;` (don't spawn WEP_HOOK as a world pickup when the offhand-hook mutator is on).
- (Also BuildMutatorsString ":grappling_hook" / pretty ", Hook" — cosmetic, skip.)
- The mutator does NOT itself implement reeling — it makes WEP_HOOK the player's **offhand** weapon; the offhand framework's `offhand_think` (server/weapons/weaponsystem.qc) calls the hook's grapple while the offhand button is held, so the player reels while keeping their primary weapon out. (`hook.qc`/`cl_hook.qc` are MENUQC describe + CSQC vis only.)

### Port mapping
- Existing model: `OffhandBlasterMutator.cs` (the exact analogue — sets `args.Player.OffhandWeapon = "blaster"` in PlayerSpawn, drives an offhand-fire each PlayerPreThink via `FireOffhand`).
- `Entity.OffhandWeapon` / `OffhandFirePressed` / `OffhandNextThink` already exist (`EntityMutatorState.cs:58-64`).
- The grapple weapon `Hook.cs` already implements the full reel lifecycle in `WrThink(actor, slot, Primary)` driven by `st.ButtonAttack` (`Weapons/Hook.cs:111-200`). So the hook mutator's offhand-think = call `hook.WrThink(player, offhandSlot, Primary)` each PlayerPreThink while `OffhandFirePressed`, with `st.ButtonAttack` mirrored from the offhand button (so the press/release reel logic works). Mirror OffhandBlasterMutator.FireOffhand but for the continuous hook (no refire gate — the hook is continuous; drive it every think while held, drop when released).
- `SetStartItems` consumer exists: `XonoticGodot/.../Player/SpawnSystem.cs:605 ComputeStartItems()` fires `MutatorHooks.SetStartItems.Call` at `:621-622`; `StartLoadout` has `AmmoFuel` + `ItemFlags` (a `HashSet<string>`). The hook handler adds fuel: `l.AmmoFuel = MathF.Max(l.AmmoFuel, cvar("g_balance_fuel_rotstable"))` and `l.ItemFlags.Add("FUEL_REGEN")` — ONLY when `g_grappling_hook_useammo`.

### Plan — new `XonoticGodot/.../Mutators/HookMutator.cs` `[Mutator]`
- `NetName = "grappling_hook"` (registry name; the enable cvar is `g_grappling_hook`).
- `IsEnabled => Api.Services!=null && ExprEvaluate(Api.Cvars.GetString("g_grappling_hook"))` (reuse the `ExprEvaluate` helper pattern from VampireHookMutator.cs:128).
- `Hook()`: read `UseAmmo = Cvars.GetFloat("g_grappling_hook_useammo")!=0`. Subscribe PlayerSpawn, PlayerPreThink (offhand think), SetStartItems, and **FilterItem** (a new hook chain — see §9). On enable set the Hook weapon's ammo cost to 0 when `!UseAmmo` (port equivalent of `WEP_HOOK.ammo_factor=0`: the Hook weapon reads `Primary.Ammo`; gate the fuel decrement in the offhand path / set a flag the offhand-think honors. Simplest faithful: when `!UseAmmo`, the offhand-think calls the reel without `TakeResource`).
- PlayerSpawn handler: `args.Player.OffhandWeapon = "hook"`.
- PlayerPreThink handler: if player alive + `OffhandWeapon=="hook"`, mirror `OffhandFirePressed`→`st.ButtonAttack` on the offhand slot and call `hook.WrThink(player, offhandSlot, Primary)`; on release call once more so the reel-drop branch runs.
- SetStartItems handler: the fuel grant above.
- FilterItem handler: return true if the candidate item is the hook weapon pickup (block WEP_HOOK world pickup while offhand-hook is on).
- **Note**: this overlaps OffhandBlasterMutator — QC says offhand_blaster overrides hook (it registers later / both set `.offhand`). Not a conflict for the port (last PlayerSpawn handler wins on `OffhandWeapon`); document the order is registry-sorted by NetName ("grappling_hook" < "offhand_blaster" so blaster runs later and wins — matches QC).

---

## 3. damagetext — floating damage numbers (P2; server feed + client draw)

### Base spec
**`sv_damagetext.qc`** (server): `AUTOCVAR(sv_damagetext, int, 2)` (shipped 2, `xonotic-server.cfg:692`). `REGISTER_MUTATOR(damagetext, true)` — always on; gated by the cvar. Wire fields on a temp net entity `net_damagetext`: `dent_net_flags`(int), `dent_net_deathtype`(int24), `dent_net_health/armor/potential`(float). `.int dent_attackers[DENT_ATTACKERS_SIZE]` per-victim bitset (first-hit-after-respawn detection).
- `MUTATOR_HOOKFUNCTION(damagetext, PlayerDamaged)` — fires on every damage tick (see emission point §below). Inputs: attacker, hit(=target), health(dh), armor(da), deathtype(int), potential_damage. Skips: `sv_damagetext<=0`; `hit==attacker`; `potential_damage==0`; instagib + DEATH_WEAPONOF==VAPORIZER (one-shot kill text suppressed). Accumulation: if the SAME attacker+hit+deathtype hit the same victim THIS frame (`net_damagetext_prev_time==time` and prev not freed), the new damage is ADDED onto the previous net_text (shotgun pellets / multi-hit accumulate into one number). Flags computed: `DTFLAG_SAMETEAM` (SAME_TEAM hit,attacker), `DTFLAG_BIG_HEALTH/ARMOR/POTENTIAL` (>= DAMAGETEXT_SHORT_LIMIT=256), `DTFLAG_NO_ARMOR` (armor==0), `DTFLAG_NO_POTENTIAL` (almost_equals_eps(armor+health, potential, 5)), `DTFLAG_STOP_ACCUMULATION` (first hit after respawn from this attacker, via the dent_attackers bitset). Creates a `net_pure` entity, sets fields, Net_LinkEntity with `write_damagetext`; nextthink = (time>10)?time+0.5:10 then SUB_Remove.
- `write_damagetext(this, client, sf)`: visibility per cvar: ALL (3) / PLAYERS (>=2 && client==attacker) / SPECTATORS (>=1 && spec following attacker) / OBSERVERS (>=1). Writes: byte etof(hit), int24 deathtype, byte flags, then health (int24 if BIG else short) `* DAMAGETEXT_PRECISION_MULTIPLIER(128)`, armor (if !NO_ARMOR), potential (if !NO_POTENTIAL).
- `ClientDisconnect` / `PlayerSpawn` clear the dent_attackers bitset.
- **Emission point** — `server/player.qc:430`: `bool forbid_logging_damage = MUTATOR_CALLHOOK(PlayerDamaged, attacker, this, dh, da, hitloc, deathtype, damage);` where `dh = initial_health - max(GetResource(this,HEALTH),0)` and `da = initial_armor - max(armor,0)` (the ACTUAL health/armor removed, computed AFTER the subtract; `damage` is the pre-split potential). EV def `server/mutators/events.qh:478` `EV_PlayerDamaged(attacker, target, health(dh), armor(da), location, deathtype:int, potential_damage)`.

**`damagetext.qh`** constants: `DAMAGETEXT_PRECISION_MULTIPLIER 128`, `DAMAGETEXT_SHORT_LIMIT 256`, `DTFLAG_SAMETEAM=1, BIG_HEALTH=2, BIG_ARMOR=4, BIG_POTENTIAL=8, NO_ARMOR=16, NO_POTENTIAL=32, STOP_ACCUMULATION=64`.

**`cl_damagetext.qc`** (client draw, CLASS DamageText): NET_HANDLE reads the wire, applies friendlyfire filters (`cl_damagetext_friendlyfire` 0/1/2), groups by server entity index, accumulates onto an existing DamageText if young enough (`cl_damagetext_accumulate_lifetime`, `_accumulate_alpha_rel`), chooses 3D-world vs 2D-screen placement (`cl_damagetext_2d` + `_2d_close_range` + `_2d_out_of_view`). `DamageText_update` builds the label from `cl_damagetext_format` (`{health}{armor}{total}{potential}{potential_health}` token replacement, verbose + hide_redundant variants), size from `map_bound_ranges(potential, size_min_damage, size_max_damage, size_min, size_max)`. `DamageText_draw2d` fades (alpha_lifetime / 2d_alpha_lifetime), shrinks (2d_size_lifetime), moves (velocity_world / 2d_velocity), projects 3d→2d, color = `cl_damagetext_color` (or friendlyfire_color, or per-weapon color if `_color_per_weapon`).

**`ui_damagetext.qc`** = the MENUQC settings dialog (XonoticDamageTextSettings) — a menu tab; **out of scope for T51 gameplay** (the menu port is a separate stream, T28). Note it as a follow-up.

**Shipped cvar defaults** (`xonotic-client.cfg:562-592`): `cl_damagetext 1`, `_format "-{total}"`, `_format_verbose 0`, `_format_hide_redundant 0`, `_color "1 1 0"`, `_color_per_weapon 0`, `_size_min 10`, `_size_min_damage 25`, `_size_max 16`, `_size_max_damage 140`, `_alpha_start 1`, `_alpha_lifetime 3`, `_lifetime -1`, `_velocity_screen "0 0 0"`, `_velocity_world "0 0 20"`, `_offset_screen "0 -45 0"`, `_offset_world "0 25 0"`, `_accumulate_alpha_rel 0.65`, `_accumulate_lifetime -1`, `_friendlyfire 1`, `_friendlyfire_color "1 0 0"`, `_2d 1`, `_2d_pos "0.47 0.53 0"`, `_2d_alpha_start 1`, `_2d_alpha_lifetime 1.3`, `_2d_size_lifetime 3`, `_2d_velocity "-25 0 0"`, `_2d_overlap_offset "0 -15 0"`, `_2d_close_range 125`, `_2d_out_of_view 1`. (`sv_damagetext` default **2**.)

### Port mapping / gap
- **No `PlayerDamaged` hook chain exists** (confirmed: `MutatorHooks` has `PlayerDamageArgs`/`PlayerDamageSplitHealthArmor` but NOT the QC `PlayerDamaged` event). MUST ADD a `PlayerDamaged` hook chain to `MutatorHooks.cs` (attacker, target, dh, da, hitLoc, deathType, potentialDamage) and fire it from `DamageSystem.cs` at the dh/da computation point.
- **DamageSystem emission seam**: `DamageSystem.cs` `PlayerDamage(...)` computes `take/save` and subtracts; the QC `dh/da` = actual removed = `baseHealth - currentHealth` etc. The existing `Apply()` already computes `dh/da` at `:268-270` (`baseHealth - max(health,0)` etc.) but that's at the OUTER `Apply` level AFTER EventDamage. **The faithful PlayerDamaged fire site** is inside `PlayerDamage` right after the subtract block (`:417-437`), computing dh = `initialHealth - max(health,0)`, da = `initialArmor - max(armor,0)`, with `damage` = the pre-split potential. Fire `MutatorHooks.PlayerDamaged.Call(...)` there. This is a **DamageSystem.cs edit** (the chain Call site) — DamageSystem.cs is T43-owned → hotFileNeeds.
- **No client damagetext draw exists** (`find game -iname '*damage*'` = empty). MUST CREATE `XonoticGodot/game/client/DamageTextLayer.cs` (or similar) — a Godot draw node mirroring DamageText (3D-world + 2D-screen, fade/shrink/move, format tokens). The server→HUD seam: the damagetext server feed (the computed flags+health+armor+potential per hit) must reach the client. Mirror the existing combat-feedback/notification seam (`NetControl.SoundBundle` + the hidden Hud announcer host, per the NetGame-play-path memo). Cleanest: a `DamageTextBundle`/event the server raises and the client `DamageTextLayer` consumes (analogous to how sounds were mirrored). The server-side mutator (a `[Mutator] DamagetextMutator`) computes flags from the PlayerDamaged hook and raises the event; the client layer draws.

### Plan
- **Server**: `XonoticGodot/.../Mutators/DamagetextMutator.cs` `[Mutator]` (`NetName="damagetext"`, `IsEnabled => sv_damagetext>0` — QC is always-registered but the cvar gates emission; modeling `IsEnabled` on the cvar is equivalent since the only behavior is the PlayerDamaged handler). Subscribe `PlayerDamaged` + `PlayerSpawn`(clear bitset). Compute flags+accumulation exactly per QC; raise a damagetext event/bundle toward the client carrying (targetNetId, deathType, flags, health, armor, potential). Add `Entity.DentAttackers` (a small int[] or HashSet<int>) to `EntityMutatorState.cs` for the first-hit bitset, and `Entity.DentPrev*` static-equivalent accumulation state (keep the "prev this frame" as static fields on the mutator like QC's `static entity net_text_prev`).
- **Constants**: a `DamageTextWire` static (mirror `damagetext.qh`: PRECISION 128, SHORT_LIMIT 256, DTFLAG_*). Put in the mutator file or a small new file under Mutators/.
- **Client**: `XonoticGodot/game/client/DamageTextLayer.cs` reading ~30 `cl_damagetext_*` cvars (defaults above), implementing DamageText_update (format tokens, size mapping) + draw2d (world/screen, fade/shrink/move). Visibility filter (`sv_damagetext` tiers) is applied server-side in QC; for a single-player/host port the simplest faithful subset is "show to the local attacker" (tier 2 default).
- **Scope trim allowed**: the accumulation-into-existing-DamageText and 2D-vs-3D placement heuristics are cosmetic refinements; a faithful MVP is per-hit world-space numbers with fade. Document the trimmed parts.

---

## 4. itemstime backend — server it_times[] feed (P2; the HUD panel renders empty today)

### Base spec — `itemstime.qc`
- `REGISTER_MUTATOR(itemstime, true)`; `REGISTER_NET_TEMP(itemstime)`. `sv_itemstime` default **1** (`xonotic-server.cfg:421`); 2 = also send to alive players.
- SVQC: `float it_times[REGISTRY_MAX(Items)+1]` (+1 slot = superweapons time). `IT_Write(e,i,f)`: to a real client, `WriteHeader itemstime; WriteByte i; WriteFloat f`.
- `Item_ItemsTime_Allow(it)` = `it.instanceOfPowerup || Item_ItemsTime_SpectatorOnly(it)`; `SpectatorOnly` = ArmorMega || (ArmorBig && !hidebig) || HealthMega || (HealthBig && !hidebig). I.e. the timed set = Strength, Shield (powerups) + Mega/Big Health + Mega/Big Armor + the superweapons slot.
- `Item_ItemsTime_SetTime(e,t)`: if `!sv_itemstime` return; for a non-weapon GameItem set `it_times[item.m_id]=t`; for a weapon pickup with a superweapon set `it_times[SUPERWEAPONS_SLOT]=t`.
- `Item_ItemsTime_UpdateTime(e,t)`: scan all g_items of the same itemdef (or same superweapon set); if any is already up (`scheduledrespawntime<=time`) mark available; else take the min scheduledrespawntime. If available, return `-t` (negative encoding = "another copy available now").
- Feeds: `reset_map_global` resets all times + re-sets per item + sends to all; `MakePlayerObserver`/`ClientConnect`(LAST)/`PlayerSpawn` send/reset per player gated on warmup_stage || sv_itemstime==2. `SetTimesForAllPlayers` sends to real clients gated likewise.
- CSQC side (`HUD_ItemsTime`, `DrawItemsTimeItem`) — already ported (see below).

### Port mapping / gap
- **Client panel already exists and renders**: `XonoticGodot/game/hud/ItemsTimePanel.cs` — `SetItemTime(name, abs)` / `SetItemTimes(...)` keyed by item NAME, draws icon+countdown, color red<5/yellow<10/white, negative encoding handled (`:110`). Catalog keys: `health_mega/health_big/armor_mega/armor_big/strength/shield/superweapons` (`:33-42`). `HudManager.cs:73` exposes `ItemsTime`.
- **GAP = no server feed**: nothing calls `ItemsTimePanel.SetItemTimes`. The panel is empty because the item-respawn times never reach it. Need: (a) a server-side `it_times` producer that, when an item is picked up / scheduled to respawn, records the absolute respawn time keyed by the panel's item-name; (b) the net→HUD seam to push them (mirror the NetGame notifications/sound seam).
- **Where item respawn happens in the port**: search `XonoticGodot/.../Gameplay/Items/` for the pickup→respawn scheduler (the analogue of `Item_ScheduleRespawn` / `scheduledrespawntime`). The itemstime producer hooks the same point (item taken → compute respawn-at → SetTime). The mutator's `reset_map_global` / per-player sync map to the port's match-reset + client-join paths.

### Plan
- `XonoticGodot/.../Mutators/ItemstimeMutator.cs` `[Mutator]` (`NetName="itemstime"`, `IsEnabled => sv_itemstime != 0`). It maintains a `Dictionary<string,float>` of item-name→absolute-respawn-time (the it_times port), updated when timed items (powerups + mega/big health/armor + superweapon) are picked up / respawn-scheduled, and pushes them to the client `ItemsTimePanel` via the NetGame seam (or directly in host mode). The negative "available now" encoding + the superweapons aggregate slot are part of parity.
- Confirm the item-pickup/respawn call-site to hook (likely `XonoticGodot/.../Gameplay/Items/*` Item respawn scheduler). If no respawn-time field exists on the world-item entity, add it (`Entity.ScheduledRespawnTime` in EntityMutatorState.cs).
- The client panel needs no changes — just feed `SetItemTimes`. **Wiring the NetGame→panel push is a NetGame.cs touch** (T37-owned) → hotFileNeeds (or do it host-side without NetGame if the host already holds the panel).

---

## 5. bugrigs — RaceCarPhysics via PM_Physics override (P2; PM_Physics seam exists)

### Base spec — `bugrigs.qc` (full, 360 lines; SVQC-only, "disabled on client until prediction fixed")
- `REGISTER_MUTATOR(bugrigs, cvar("g_bugrigs"))` with `MUTATOR_ONADD { bugrigs_SetVars(); }` (copies the 15 `g_bugrigs_*` cvars into globals). `g_bugrigs` default **0** (`mutators.cfg:503`).
- 15 cvars + defaults (`mutators.cfg:503-517`): `planar_movement 1`, `planar_movement_car_jumping 1`, `reverse_speeding 1`, `reverse_spinning 1`, `reverse_stopping 1`, `air_steering 1`, `angle_smoothing 5`, `friction_floor 50`, `friction_brake 950`, `friction_air 0.00001`, `accel 800`, `speed_ref 400`, `speed_pow 2`, `steer 1`. (No `_friction_floor`-distinct; note `car_jumping` cvar name is `g_bugrigs_planar_movement_car_jumping`.)
- `racecar_angle(forward, down)` — helper producing the body pitch/roll from local velocity.
- `RaceCarPhysics(this, dt)` — the whole drive model:
  - `accel = bound(-1, movement.x/maxspeed, 1)`, `steer = bound(-1, movement.y/maxspeed, 1)`. reverse_speeding makes back-accel digital.
  - zero pitch/roll, makevectors(angles). On-ground (or air_steering): `myspeed = velocity·v_forward`, `upspeed = velocity·v_up`; responsiveness `f = 1/(1+(|myspeed|/speed_ref)^speed_pow)`; steerfactor (reverse_spinning variant), accelfactor; friction_floor/brake logic for accel<0 / >=0; `angles_y += steer*dt*steerfactor`; `myspeed += accel*accelfactor*dt`; `rigvel = myspeed*v_forward + (0,0,1)*upspeed`. Else (airborne, no air_steering): `myspeed = vlen(velocity)`, steer only, `rigvel = velocity`.
  - air friction `rigvel *= max(0, 1 - vlen(rigvel)*friction_air*dt)`.
  - planar_movement branch: `rigvel_z -= dt*gravity`; tracebox up 1024 (MOVE_NORMAL if car_jumping else MOVE_NOMONSTERS) to find surface, tracebox forward by rigvel_xy*dt, tracebox down to align to surface; sets origin, aligns `angles` to the surface normal via vectoangles, SET/UNSET_ONGROUND, `velocity = (neworigin-origin)/dt`, `set_movetype(NOCLIP)`. Else: `velocity = rigvel; set_movetype(FLY)`.
  - final tracebox down 4u to set body pitch/roll (racecar_angle) + angle smoothing (`angle_smoothing`).
- `MUTATOR_HOOKFUNCTION(bugrigs, PM_Physics)`: `if(!g_bugrigs || !IS_PLAYER) return;` `#ifdef SVQC player.angles = player.bugrigs_prevangles;` `RaceCarPhysics(player, dt); return true;` (the `return true` means PM_Physics fully replaced the move).
- `MUTATOR_HOOKFUNCTION(bugrigs, PlayerPhysics)`: `if(!g_bugrigs) return; player.bugrigs_prevangles = player.angles; player.disableclientprediction = 2;`
- `ClientConnect`: stuffcmd chase_active 1 (3rd-person). BuildMutatorsString cosmetic.

### Port mapping / gap
- **PM_Physics seam is LIVE**: `PlayerPhysics.cs:226` `else if (MutatorHooks.PMPhysics.Count > 0 && CallPmPhysics(player, maxspeedMod, dt))` — a `true` return fully replaces the move (the whole branch chain below is skipped). This is exactly the bugrigs hook. `PMPhysicsArgs(player, maxspeedMod, ticRate)` (`MutatorHooks.cs:186-196`). `T44` provided this (PM_Physics now exists).
- The port's input: `Entity.MovementForward`/`MovementRight` (the QC `PHYS_CS().movement.x/.y`) exist (`EntityMutatorState.cs:20-22`). `MaxSpeed`, gravity, tracebox via `Api.Trace`, `set_movetype` via `Entity.MoveType`, angles via `Entity.Angles`/`AVelocity` all exist.
- **GAP**: no bugrigs implementation exists in the port.

### Plan — new `XonoticGodot/.../Mutators/BugrigsMutator.cs` `[Mutator]`
- `NetName="bugrigs"`, `IsEnabled => g_bugrigs != 0`. `Hook()` reads the 15 cvars (defaults above) + subscribes `PMPhysics` (HookOrder doesn't matter; only handler) and `PlayerPhysics` (to stash `bugrigs_prevangles`).
- PM_Physics handler: `if (g_bugrigs==0 || !IsPlayer(player)) return false;` `player.Angles = player.BugrigsPrevAngles;` `RaceCarPhysics(player, dt);` `return true;` (replaces the move).
- PlayerPhysics handler: stash `player.BugrigsPrevAngles = player.Angles`. (`disableclientprediction` is a net/prediction concern — note as cosmetic for the headless sim.)
- Port `RaceCarPhysics` + `racecar_angle` faithfully using `Api.Trace.Trace(...)` for the traceboxes, `QMath.AngleVectors`/`VecToAngles`/`VecToYaw` for the angle math. **Watch the QMath pitch convention** (the VecToAngles/AngleVectors pitch sign is intentionally inverse per the qmath-pitch memo — use `FixedVecToAngles` where Base uses `vectoangles`/`vectoangles2`).
- Add `Entity.BugrigsPrevAngles` (Vector3) to `EntityMutatorState.cs`.
- **chase_active 1 / 3rd-person + client prediction**: bugrigs is SVQC-only in Base (no client prediction). For the port, run it server-side; the camera/3rd-person is a client-presentation follow-up (note it). T44 prereq is satisfied.
- **Where bugrigs is used**: race/CTS-style "bumblebee racing" — the Race/Cts gametypes exist (`GameTypes/Race.cs`, `Cts.cs`). No gametype coupling needed (pure physics override).

---

## 6. Overkill weapons — REGISTER_WEAPON(OVERKILL_*) (P2; OverkillMutator references them by name, NULL today)

### The gap
`OverkillMutator.cs` (already ported) gives the loadout `okmachinegun/oknex/okshotgun` (+ `okrpc`/`okhmg` conditionally) via `Weapons.ByName(n)` (lines 46, 182-188, 232-235). **These names resolve to NULL today** because no `[Weapon]` classes exist for them → `g_overkill` grants nothing. Confirmed: `Weapons/` has Machinegun/Shotgun/Vortex/Devastator/... but no Ok* classes. T51 creates the 5 weapon classes; OverkillMutator then resolves them.

### Identity (from the .qh files) + balance (from `bal-wep-xonotic.cfg:792-922`)
All 5: secondary fire = a Blaster shot (no damage/force — the OK signature; the OverkillMutator's Damage_Calculate already nullifies blaster dmg/force). `refire_type 1` for all secondaries = the secondary uses its own `jump_interval` timer (continuous blaster-jump) rather than the weapon's refire. All have `WEP_FLAG_HIDDEN | WEP_FLAG_MUTATORBLOCKED` (never normal world pickups; only granted by g_overkill).

| Weapon (NetName) | impulse | ammo | flags | color | mirror template |
|---|---|---|---|---|---|
| **okmachinegun** | 3 | RES_BULLETS | HIDDEN,RELOADABLE,HITSCAN,PENETRATEWALLS,MUTATORBLOCKED | 0.678 0.886 0.267 | `Machinegun.cs` (auto-fire bullets) |
| **okshotgun** | 2 | RES_SHELLS | HIDDEN,RELOADABLE,HITSCAN,MUTATORBLOCKED | 0.518 0.608 0.659 | `Shotgun.cs` (pellets) |
| **oknex** | 7 | RES_CELLS | HIDDEN,RELOADABLE,HITSCAN,MUTATORBLOCKED | 0.459 0.765 0.835 | `Vortex.cs` (railgun, charge OFF by default) |
| **okhmg** | 3 | RES_BULLETS | MUTATORBLOCKED,HIDDEN,RELOADABLE,HITSCAN,SUPERWEAPON,PENETRATEWALLS | 0.992 0.471 0.396 | `Machinegun.cs` (super, deathtype legacy "hmg") |
| **okrpc** | 9 | RES_ROCKETS | MUTATORBLOCKED,HIDDEN,CANCLIMB,RELOADABLE,SPLASH,SUPERWEAPON | 0.914 0.745 0.341 | `Devastator.cs`/`Mortar.cs` (rocket, deathtype legacy "rpc") |

**Balance defaults** (all `g_balance_<wep>_primary_*` unless noted):
- **okmachinegun**: ammo 1, damage 25, force 5, refire 0.1, solidpenetration 100, spread_add 0.012, spread_max 0.05, spread_min 0; reload_ammo 30, reload_time 1.5; secondary_refire_type 1; damagefalloff_* 0; weaponthrowable 1.
- **okshotgun**: ammo 3, animtime 0.65, bot_range 512, bullets 10, damage 17, force 80, refire 0.75, solidpenetration 3.8, spread 0.07, spread_pattern 0/scale 0/bias 0; reload_ammo 24, reload_time 2; secondary_refire_type 1; damagefalloff_* 0.
- **oknex**: ammo 10, animtime 0.65, damage 100, force 500, refire 1; reload_ammo 50, reload_time 2; secondary 2 (=blaster), secondary_refire_type 1; **charge 0** (the velocity-charge model is OFF by default → mirror Vortex without charge), charge_* present but inert; damagefalloff_* 0.
- **okhmg**: ammo 1, damage 30, force 10, refire 0.05, solidpenetration 127, spread_add 0.005, spread_max 0.06, spread_min 0.01; reload_ammo 120, reload_time 1; secondary_refire_type 1. (Superweapon: needs the Superweapon status-effect to fire — see okhmg.qc:22-23. Also a `Nade_Damage` hook scales nade self-damage to 10% — niche, see §9.)
- **okrpc**: ammo 10, animtime 1, damage 150 (explosion core), **damage2 500** (the chainsaw pass-through per-hit), damageforcescale 2, edgedamage 50, force 400, health 25 (the missile is shootable), lifetime 30, radius 300, refire 1, speed 2500, speedaccel 5000; reload_ammo 10, reload_time 1; secondary_refire_type 1.

### Behavior notes per weapon (wr_think/checkammo/reload/aim)
- **Common secondary**: `if (refire_type==1 && (fire&2) && time>=actor.jump_interval) { jump_interval = time + WEP_CVAR_PRI(BLASTER, refire)*ratefactor; W_Blaster_Attack(actor, weaponentity); ... }` — i.e. hold ATCK2 → blaster-jump on the blaster's own refire. Port: reuse `Blaster.FirePrimaryDirect(actor, slot)` (public, `Weapons/Blaster.cs:124`). Use `Entity.JumpInterval` (add to EntityMutatorState if absent).
- **okmachinegun / okhmg**: forced reload when `reload_ammo && clip_load < primary_ammo`; primary = auto-fire one bullet per refire, accumulating spread `bound(spread_min, spread_min + spread_add*misc_bulletcounter, spread_max)`, fireBullet with solidpenetration+force. Mirror `Machinegun.AttackAuto` + `FireOne` (use `WeaponFiring.FireBullet`). okhmg adds the superweapon gate (`StatusEffects_active(Superweapon)` or IT_UNLIMITED_SUPERWEAPONS).
- **okshotgun**: primary = `W_Shotgun_Attack` (10 pellets, spread 0.07). Mirror `Shotgun.Attack` (the existing helper is private → re-mirror via `WeaponFiring.FireBullet` in a loop with `CalculateSpread`/`CalculateSpreadPattern`). animtime 0.65, refire 0.75.
- **oknex**: primary = `W_OverkillNex_Attack` → `FireRailgunBullet` (damage 100, force 500). With charge OFF (default), `charge=1` so no charge math. Mirror `Vortex.Attack` (FireRailgunBullet). The `GetPressedKeys` velocity-charge + `wr_glow` are charge-only (default off) → document as deferred.
- **okrpc**: primary = `W_OverkillRocketPropelledChainsaw_Attack` — spawn a `MOVETYPE_FLY` missile, shootable (health 25, damageforcescale 2), `Think` accelerates it (speed += speedaccel*frametime) and on each frame traces forward; if it passes through a PLAYER it deals `damage2`(500) per pass (the "chainsaw"); on touch/timeout it `RadiusDamage`s (damage 150, edge 50, radius 300, force 400). Lifetime 30. Mirror the `Devastator`/`Mortar` projectile-spawn pattern + a per-tick forward-trace damage. This is the most complex — the chainsaw pass-through is the distinctive part.

### Plan
- 5 new files `XonoticGodot/.../Weapons/Ok{Machinegun,Shotgun,Nex,Hmg,Rpc}.cs`, each `[Weapon]`, mirroring the cited existing weapon + the OK secondary-blaster. Auto-register; `OverkillMutator` then resolves them. Balance reads with the defaults above.
- **No Registries.cs / GameInit.cs edit** (reflection). The OverkillMutator already maps HMG→MG, RPC→Nex on respawn (`OverkillMutator.cs:167`) — works once the weapons exist.
- Add `Entity.JumpInterval` (float) to `EntityMutatorState.cs` if not present (the OK-secondary blaster-jump timer + the existing Machinegun uses no such field — confirm).
- okrpc projectile fields (m_chainsaw_damage, cnt/lifetime) live on the missile entity — use the existing projectile-entity fields or add minimal ones.

---

## 7. Remaining registered admin/niche mutators (P2/P3)

### breakablehook (P3 — needs hook-as-entity) — `sv_breakablehook.qc` (31 lines)
- `REGISTER_MUTATOR(breakablehook, cvar("g_breakablehook"))`. `g_breakablehook` + `g_breakablehook_owner` bools.
- `Damage_Calculate`: if `frag_target.classname=="grapplinghook"`: zero the damage if (`!g_breakablehook`) or (`!g_breakablehook_owner && attacker==hook.realowner`); if `DIFF_TEAM(attacker, hook.realowner)` → `Damage(hook.realowner, attacker, attacker, 5, WEP_HOOK|HITTYPE_SPLASH, ...)` + `RemoveHook(hook)`.
- Port gap: requires the grapple hook to be a **damageable entity** with `classname=="grapplinghook"` and a `RemoveHook`. The port's `Hook.cs` grapple latches geometry; whether the in-flight hook is a shootable entity is uncertain. **P3 — depends on hook-as-shootable-entity; faithful handler is small but the substrate (shootable hook + RemoveHook) may not exist.** Document; implement the Damage_Calculate handler against the entity if it exists, else stub with a recorded blocker (like VampireHookMutator's documented partial).

### campcheck (P2) — `sv_campcheck.qc` (103 lines)
- `REGISTER_MUTATOR(campcheck, expr_evaluate(autocvar_g_campcheck))`. cvars: `g_campcheck`(string), `_damage`, `_distance`, `_interval`, `_typecheck`.
- Per-player `.float campcheck_nextcheck/_traveled_distance`, `.vector campcheck_prevorigin`.
- `PlayerPreThink`: if interval set & match live & player alive & real client & weapon unlocked: accumulate 2D distance traveled; every `interval` seconds, if distance < `_distance` → centerprint CENTER_CAMPCHECK + Damage(player, ..., bound(0, _damage, hp+armor*blockpercent+5), DEATH_CAMP, ...). Reset distance/timer.
- `PlayerDies` → centerprint CPID_CAMPCHECK; `Damage_Calculate` (combatants reset their camp distance); `CopyBody`/`PlayerSpawn` init.
- Port: PlayerPreThink + PlayerDies + Damage_Calculate hooks all exist. Centerprint via the Notifications system (`Gameplay/Notifications/`). Damage via `Combat.Damage(...)`. Add `Entity.CampcheckNextCheck/_TraveledDistance/_PrevOrigin` to EntityMutatorState. **P2 — fully portable.** Note: bots skipped (real-client only), so headless `--bots` won't trigger it (mirror the bot-nav memo caveat).

### kick_teamkiller (P3 — admin/scoring) — `sv_kick_teamkiller.qc` (76 lines)
- `REGISTER_MUTATOR(kick_teamkiller, (autocvar_g_kick_teamkiller_rate > 0))`. cvars rate/lower_limit/severity/bantime.
- `PlayerDies`: teamplay+!warmup; if attacker is real client and teamkills (PlayerScore SP_TEAMKILLS) exceed rate*playtime/60 and >= lower_limit → severity 1 (kick), 2 (ban), default (play-ban / observer).
- Port gap: needs SP_TEAMKILLS scoring, dropclient/ban infra, observer transmute — server-admin plumbing largely absent in the headless sim. **P3 — niche admin; document, likely stub.**

### dynamic_handicap (P3 — scoring/handicap) — `sv_dynamic_handicap.qc` (121 lines)
- `REGISTER_MUTATOR(dynamic_handicap, autocvar_g_dynamic_handicap && !HANDICAP_DISABLED())`. cvars scale/exponent/min/max.
- Recomputes per-player forced handicap from `(score - mean_score)*scale)^exponent` on ClientDisconnect/PutClientInServer/MakePlayerObserver/AddedPlayerScore(SP_SCORE).
- Port: `DamageSystem` already reads `Entity.HandicapTake/HandicapGive` (`HandicapTotal`, `DamageSystem.cs:676`). So the handicap *application* exists; this mutator just SETS those per-player from scores. Needs the score hooks (AddedPlayerScore) + `Handicap_SetForcedHandicap`. **P3 — depends on the scoring hook surface; portable if AddedPlayerScore/score reads exist.** Document.

### superspec (P3 — admin/spectator chat tooling) — `sv_superspec.qc` (442 lines)
- `REGISTER_MUTATOR(superspec, expr_evaluate(autocvar_g_superspectate))`. A spectator power-tool: chat-command parsing (`sv_cmd`/client cmds), per-client options saved to `superspec-*.options` files (fopen/fputs), auto-spectate flags (ASF_*), follow-killer, item-grab notifications, item filters. Heavy dependence on Spectate(), chat commands, file I/O, FOREACH_CLIENT, observer mechanics.
- **P3 — deep admin tool, out of scope for gameplay parity.** Document as deferred/skip; if attempted, it's a large standalone effort (chat-command + spectator framework + file persistence), none of which the headless sim has.

### sandbox (P3 — server object editor) — `sv_sandbox.qc` (833 lines)
- `REGISTER_MUTATOR(sandbox, expr_evaluate(autocvar_g_sandbox))` with ONADD creating `g_sandbox_objects` IntrusiveList + optional DB load. A full server-side object editor: spawn/edit/attach/remove props, materials, persistent storage (`g_sandbox_storage_*`), `sv_cmd sandbox` chat commands, MAX_STORAGE_ATTACHMENTS, flood control.
- **P3 — large standalone editor feature, out of scope.** Document as deferred/skip.

**Summary of niche set**: campcheck = P2 (portable). breakablehook = P3 (needs shootable-hook entity). kick_teamkiller / dynamic_handicap = P3 (need admin/scoring plumbing). superspec / sandbox = P3 large standalone (skip / explicit deferral). Faithful-port priority: campcheck first; the rest documented with their substrate blockers.

---

## 8. Shared infra needed (CONFIRMED via port reads)

- `WeaponFiring` (`Weapons/WeaponFiring.cs`): `MaxShotDistance`, `SetupShot`, `FireBullet(actor,start,dir,range,damage,deathTypeId,spread,solidPenetration,force)`, `FireRailgunBullet(...,force,...)`, `CalculateSpread`, `CalculateSpreadPattern`, `ProjectileVelocity`, `ExponentialFalloff`. (deathtype passed as the weapon's `RegistryId` int — see Machinegun.cs:277.)
- Shared weapon helpers (`Weapons/WeaponFireGate.cs`, the `Weapon` partial): `PrepareAttack`, `WeaponRateFactor`, `UnlimitedAmmo`. (`Machinegun.cs` uses `PrepareAttack(actor,slot,fire[,attackTime])`, `WeaponRateFactor()`.)
- `Blaster.FirePrimaryDirect(actor, slot)` (`Weapons/Blaster.cs:124`) = the reusable OK-secondary blaster shot.
- `Inventory` (`Gameplay/Inventory.cs`): ClearWeapons/GiveWeapon/SwitchWeapon/HasWeapon/CurrentWeapon/SwitchToBest (used by OverkillMutator.cs:160-176 — already working).
- `Combat.Damage(...)` (`Damage/DamageSystem.cs` via `Combat`), `DeathTypes.FromWeapon("name")`, `Teams.SameTeam`, `StatusEffectsCatalog`.
- Notifications: `Gameplay/Notifications/NotificationsList.cs` + `Notification.cs` (for campcheck centerprint).
- Per-entity fields: `EntityMutatorState.cs` (T51-owned partial Entity NEW file).
- Test harness: `tests/XonoticGodot.Tests/MutatorBatchT19Tests.cs` — `[Collection("GlobalState")]`, `Boot((cvar,val)...)` (sets cvars → GameRegistries.Reset → Bootstrap → DamageSystem → MutatorActivation.Apply), `Dispose()=>DeactivateAll()`, `NewPlayer`/`SpawnPlayer`/`Configure`. Mirror this for T51 tests.

---

## 9. Hook chains to ADD to MutatorHooks.cs (NEW events)

Current chains (confirmed): PlayerSpawn, SpawnScore, PlayerPreThink, SvStartFrame, PlayerPowerups, PlayerRegen, PlayerPhysics, WeaponRateFactor, PlayerJump, PlayerCanCrouch, PMPhysics, IsFlying, DamageCalculate, PlayerDies, GiveFragsForKill, SetStartItems, SetWeaponArena, ForbidRandomStartWeapons, ForbidThrowCurrentWeapon, FilterItemDefinition, EditProjectile, GrappleHookThink, SetDefaultAlpha; + GameHooks.PlayerDamageSplitHealthArmor.

**MUST ADD:**
1. **`PlayerDamaged`** (damagetext) — `EV_PlayerDamaged(attacker, target, health(dh), armor(da), location, deathtype:int/string, potential_damage)`. Fired from `DamageSystem.cs` `PlayerDamage` after the subtract (computing dh/da = actual removed; damage = pre-split potential). The QC return ("forbid logging") can be ignored by the port (logging is stats). **This is the damagetext emission seam.**
2. **`FilterItem`** (hook mutator) — `EV_FilterItem(item)` returns true to suppress a world-item spawn (the hook mutator blocks WEP_HOOK pickups). NOTE: a `FilterItemDefinition` chain already exists (`MutatorHooks.cs:352`, keyed on ClassName/NetName). **Reuse `FilterItemDefinition` if the item-definition entity exposes the weapon id**, else add a parallel `FilterItem`. Prefer reusing FilterItemDefinition (return true when `item.NetName/ClassName` is the hook weapon).

**ADD ONLY IF implementing the charge/superweapon-niche paths (default-off, can defer):**
3. **`GetPressedKeys`** (oknex velocity-charge) — only needed if `g_balance_oknex_charge` is enabled (default 0). Defer.
4. **`Nade_Damage`** (okhmg nade self-damage scaling to 10%) — niche; only if nade↔okhmg interaction is in scope. Defer (the nade-throw input is a carried major per the completeness memo).

All other behaviors map onto EXISTING chains (PlayerSpawn, PlayerPreThink, SetStartItems, PMPhysics, PlayerPhysics, PlayerJump, Damage_Calculate, PlayerDies). **MutatorHooks.cs edit is additive (new chains/args) — no reorder/removal.**

---

## 10. Cross-task conflict map (hotFileNeeds)

Per the brief, the 7 A2 tasks share one tree. T51 must touch these files that OTHER tasks own:

- **`MutatorHooks.cs`** — *not* in another task's listed owned set, but it is the shared hook bus many A2 tasks extend (T37/T43/T36 may add chains). T51 needs to ADD `PlayerDamaged` (+ maybe `FilterItem` reuse). **Additive only** (append new `struct XArgs` + `HookChain<>`); the orchestrator should make ONE owner of MutatorHooks.cs and hand others their additive snippet (chokepoint-wave pattern). T51's snippet: the `PlayerDamaged` chain.
- **`DamageSystem.cs`** (T43-owned) — T51 needs ONE added Call site: fire `MutatorHooks.PlayerDamaged.Call(attacker, targ, dh, da, hitLoc, deathType, potentialDamage)` inside `PlayerDamage(...)` right after the subtract block (`~:417-437`), with `dh = initialHealth - max(health,0)`, `da = initialArmor - max(armor,0)`, `potentialDamage = damage`. (T43 also edits DamageSystem for monster-death; non-overlapping lines.) **Snippet for the T43 owner.**
- **`PlayerPhysics.cs`** (T30-sensitive — golden traces) — T51 needs ONE line: `PlayerPhysics.cs:754` change `bool doublejump = mp.DoubleJump;` → `bool doublejump = false;` (the doublejump fix; the PlayerJump hook chain already handles the grant). Golden traces unaffected (default `sv_doublejump 0` → identical). **Coordinate with T30** (the golden-trace owner) so it re-baselines/confirms. T30 owns `Collision/TraceService.cs` + `Services/Services.cs` + `tools/movement-ref`, not PlayerPhysics.cs directly — but the trace owner must sign off the 1-liner.
- **`NetGame.cs`** (T37-owned) — T51 needs the net→HUD push for damagetext (DamageTextLayer feed) and itemstime (`ItemsTimePanel.SetItemTimes`). Mirror the existing `NetControl.SoundBundle`/hidden-Hud seam. **Snippet for the T37 owner** (or do host-side without NetGame if the host already holds the HUD). Two small additions: a damagetext event hookup + an itemstime push.
- **`GameInit.cs`** — **NO edit needed** (mutators/weapons auto-register via reflection; no new *system* install required for T51). (If itemstime needs an InstallGameplaySystems entry for its producer, that's a 1-line additive — flag only if the producer can't be a pure `[Mutator]`.)
- **`Registries.cs`** — **NO edit** (reflection).
- **`MapObjectsRegistry.cs` / `ServerNet.cs` / `Commands.cs` / `Services.cs`** — **NO edit needed by T51.**

EntityMutatorState.cs is T51's own (already modified today) — all new per-entity fields (BugrigsPrevAngles, Campcheck*, JumpInterval, DentAttackers, ScheduledRespawnTime, etc.) go there, no conflict.

---

## 11. Risks / parity traps

- **doublejump golden-trace regression**: the `:754` edit must leave default behavior byte-identical (default 0 → `false`, same as before). Verify MovementParityTests pass. The trap is "fixing" it in a way that changes the default path.
- **doublejump velocity clip sign**: `f = velocity·normal; if (f<0) velocity -= f*normal`. Only clips the INTO-surface component (negative dot). Don't clip when rising.
- **bugrigs QMath pitch convention**: Base uses `vectoangles`/`vectoangles2`; the port's `VecToAngles` has an intentionally-inverse pitch sign (qmath-pitch memo) — use `FixedVecToAngles`. Also the makevectors→dot order and the surface-align traceboxes must match exactly or the car flips/clips.
- **bugrigs PM_Physics `return true`**: must return true so the move is fully replaced (the `Count>0` gate at PlayerPhysics.cs:226 keeps the default path allocation-free — fine).
- **OK weapons spread/deathtype**: pass the weapon's `RegistryId` as the deathtype int (Machinegun.cs:277 pattern), NOT a raw constant. okhmg/okrpc carry legacy netnames ("hmg"/"rpc") for deathtype text — the `m_deprecated_netname` (kill-message routing) — keep `NetName="okhmg"/"okrpc"` for the registry but be aware of the legacy alias if death-message parity is checked.
- **OK secondary = blaster jump on `jump_interval`** (not the weapon refire). Using the weapon's own refire would make the blaster-jump too slow. Use a dedicated `JumpInterval` timer + `Blaster.FirePrimaryDirect`.
- **oknex charge default OFF** — don't port the GetPressedKeys velocity-charge / wr_glow unless `g_balance_oknex_charge` is set (it's 0); a faithful default-balance oknex is just Vortex-without-charge.
- **okrpc chainsaw** = the pass-through `damage2`(500) per-player-per-frame is the distinctive mechanic; the explosion is `damage`(150). Easy to conflate. The missile is shootable (health 25).
- **damagetext accumulation + visibility tiers**: the per-frame same-attacker accumulation and the dent_attackers first-hit-after-respawn bitset are fiddly; an MVP can skip accumulation. The visibility tiers (`sv_damagetext` 1/2/3 spectator/attacker/all) reduce to "local attacker" in a host port.
- **itemstime negative encoding**: `-t` means "available now / another copy up"; the panel already handles `< -1f` (`ItemsTimePanel.cs:110`) — feed it the same encoding. `-1` exactly = "not on this map" (hide). The superweapons aggregate is a single extra slot (REGISTRY_MAX(Items)).
- **hook mutator vs offhand_blaster precedence**: both set `Entity.OffhandWeapon` in PlayerSpawn; registry NetName sort makes "offhand_blaster" run after "grappling_hook" → blaster wins (matches QC "overridden by offhand_blaster"). Don't reorder.
- **breakablehook/superspec/sandbox/kick_teamkiller/dynamic_handicap** depend on substrate (shootable-hook entity / chat-commands / file I/O / ban infra / scoring hooks) that is largely absent headless — implementing them "fully" risks inventing infra. Keep faithful handler bodies; gate behind the substrate and document the blocker (the VampireHookMutator "documented partial" precedent).
- **MutatorHooks/DamageSystem/PlayerPhysics/NetGame are multi-task hot files** — DO NOT edit them in T51 directly; emit the exact additive snippet for the chokepoint owner (the completeness memo's "hold the hot file to 1 owner + others report seam snippets" pattern).

---

## 12. Suggested implementation order (within T51)
1. **doublejump fix** (highest value, smallest surface; new DoublejumpMutator + the PlayerPhysics.cs:754 1-liner via T30/owner).
2. **Overkill weapons** (5 weapon classes; unblocks the already-ported OverkillMutator → `g_overkill` becomes playable). No hot-file edits.
3. **bugrigs** (new mutator; PMPhysics seam live; no hot-file edits beyond EntityMutatorState).
4. **hook mutator** (new mutator; reuses existing Hook.cs grapple + offhand pattern; SetStartItems consumer exists; needs FilterItem(Definition) reuse).
5. **campcheck** (new mutator; all hooks + Notifications + Combat.Damage exist).
6. **damagetext** (new server mutator + the PlayerDamaged hook/Call seam [hot files] + a new client DamageTextLayer + NetGame feed).
7. **itemstime backend** (new server producer/mutator + NetGame push to the existing panel).
8. **breakablehook / kick_teamkiller / dynamic_handicap** (P3, substrate-gated handlers + documented blockers).
9. **superspec / sandbox** (P3 large standalone — explicit deferral notes).
