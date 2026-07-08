# Client crosshair — parity spec

**Base refs:** `client/hud/crosshair.qc` (+ `crosshair.qh`, `crosshairs.cfg`), `client/view.qc:UpdateDamage`/`HUD_Draw`
**Port refs:** `game/hud/CrosshairPanel.cs` · `game/client/ReticleOverlay.cs` · `game/net/NetGame.cs` (feeders) · `game/hud/HudManager.cs` (Player wire)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The crosshair is the client-only (CSQC / presentation) reticle drawn dead-centre every frame while the local
player is alive and the normal HUD is up. Beyond the static cross pic it carries a lot of live feedback: it is
tinted per-weapon / by health / random; it bumps in scale on item pickup and on a confirmed hit (with a colour
flash); it runs a forward "true-aim" world trace and recolours/blurs/shrinks itself when the shot would hit a
teammate or be obstructed; it draws weapon-state **rings** around the centre (Vortex/Overkill charge + inner
chargepool ring, Hagar burst load, Mine Layer count, Arc overheat, and the reload/ammo "clip" ring); it cross-fades
between the old and new crosshair image on a weapon switch; and it draws an optional centre dot. A separate set of
**objective rings** (held-nade fuse timer, capture progress, revive/thaw progress) is drawn around the same centre
from `view.qc:HUD_Draw`, in strict priority. The zoom **reticle** (`DrawReticle`) also lives in `crosshair.qc` but is
a full-screen scope overlay, ported separately as `ReticleOverlay.cs` (noted here, scored as its own feature row).

Entry: `view.qc:HUD_Draw` calls `UpdateDamage()` → `HUD_Crosshair(this)` → `HitSound()` every frame (crosshair is
"drawn VERY LAST"). All crosshair state is local/CSQC-computed except the inputs it reads from STATs (HEALTH, ARMOR,
charge/clip via `wepent`, NADE_TIMER/CAPTURE_PROGRESS/REVIVE_PROGRESS, HIT_TIME/HITSOUND_DAMAGE_DEALT_TOTAL).

## Base algorithm (authoritative)

### Master gating & enable (`crosshair.qc:HUD_Crosshair` 213-256)
Skips entirely (and resets all `wcross_*_prev` smoothing state) when: scoreboard active, camera active,
intermission==2, game stopped, lockview, spectatee_status==-1, HEALTH<=0, a mutator `DrawCrosshair` hook fires,
inside a viewloc that isn't FREEAIM, or the minigame menu is open. Then per-cvar early-outs: `crosshair_enabled 0`
(**default 1**), dead-spectator camera mode 2, vehicle HUD (→`HUD_Crosshair_Vehicle`), `crosshair "0"` /
`crosshair_size 0` / `crosshair_alpha 0`. **Constants:** `crosshair_enabled 1`, `crosshair "16"`, `crosshair_size 0.4`,
`crosshair_alpha 0.8`.

### Crosshair origin / chase (`crosshair.qc` 259-301)
Origin is normally centre-screen via `project_3d_to_2d(view_origin + max_shot_distance*view_forward)`
(`max_shot_distance = 32768`). In a FREEAIM viewloc it uses the 2D mouse pos. In third-person
(`chase_active>0 && crosshair_chase`, **default 1**) it traces from the player org along view_forward (MOVE_WORLDONLY)
and projects the hit; it also fades the player model's alpha toward `crosshair_chase_playeralpha` (**0.25**) while the
body obstructs the view, restoring it when chase turns off.

### True-aim hit test (`crosshair.qc:TrueAimCheck`/`EnemyHitCheck` 50-154, used 306-322)
When `crosshair_hittest` (**default 1**): classify what the shot would hit. `WEP_FLAG_NOTRUEAIM` weapons (Mortar,
Hook, Porto, Tuba) skip → HITWORLD. Per-weapon move filter: Vortex/Overkill-Nex/Vaporizer use MOVE_NORMAL (trace
players); Rifle uses a body/corpse-only trace ent and (if zoomed) a single straight `tracebox`; everything else
MOVE_NOMONSTERS. Projectile-size weapons add a trace box: Devastator `±3`, Fireball `±16`, Seeker `±2`, Electro
`(0,0,-3)`. Algorithm: traceline eye→`view_forward*max_shot_distance` to get `trueaimpoint`, nudge `+view_forward`,
clamp to at least `g_trueaim_minrange` (**44**, networked from server) ahead; compute the real shot origin from the
decompressed `SHOTORG` offset; tracebox from shot org forward then to the aim point; `EnemyHitCheck` classifies the
hit ent: `n<1`/`n>maxclients` → HITWORLD; `teamplay && team==myteam` → **HITTEAM**; spectator team → HITWORLD; else
**HITENEMY**. Back in `HUD_Crosshair`, if HITWORLD but the projected impact drifted >0.01 of screen from centre →
**HITOBSTRUCTION** (screen-space test). The `#if 0` 3D-distance obstruction test is dead in Base (misfires on RL).
`crosshair_hittest_showimpact` (**0**, debug) moves the crosshair to the impact point.

### Colour (`crosshair.qc:crosshair_getcolor` 165-204)
`crosshair_color_special` (**default 1**): 1 = active weapon's `m_color` (only HUD_NORMAL, else normal); 2 =
health+armor via `healtharmor_maxdamage(...)` → `HUD_Get_Num_Color(hp,200,false)` 5-stop ramp; 3 = rainbow —
`randomvec()*crosshair_color_special_rainbow_brightness` (**20**) re-rolled every `..._rainbow_delay` (**0.1**) s;
default/0 = `stov(crosshair_color)` (**"0.6 0.8 1"**).

### Pickup pulse (`crosshair.qc` 362-380)
When `STAT(LAST_PICKUP)` advances (and <5 s old), `pickup_crosshair_size=1`; it decays by
`crosshair_pickup_speed*frametime` (**4/s**); `wcross_scale += sin(pickup_crosshair_size) * crosshair_pickup`
(**0.25**).

### Hit indication (`crosshair.qc` 382-399, fed by `view.qc:UpdateDamage` 890-912)
`UpdateDamage` accumulates `unaccounted_damage` from `STAT(HITSOUND_DAMAGE_DEALT_TOTAL)` deltas whenever
`STAT(HIT_TIME)` advances (zeroed on spectatee change). When `crosshair_hitindication` (**0.5**) and
`unaccounted_damage`, `hitindication_crosshair_size=1`, decays by `crosshair_hitindication_speed*frametime`
(**5/s**); adds `sin(size)*hitindication` to scale and `sin(size)*col` per RGB channel, where col is
`crosshair_hitindication_color` (**"10 -10 -10"**, i.e. push red / pull green+blue) or
`crosshair_hitindication_per_weapon_color` (**"10 10 10"**, brighten) in weapon-colour mode.

### Smooth transitions (`crosshair.qc` 408-449, 559-615)
`crosshair_effect_time` (**0.4**). Goal-based scale/alpha/colour ease toward target over that window
(`f = frametime/(changedonetime-time+frametime)`). `crosshair_effect_scalefade` (**1**) folds the resolution into the
scale so the whole thing scale-fades. On a `wcross_name`/resolution change (weapon switch) it records the previous
pic and cross-fades alpha: the outgoing pic draws under the incoming over `effect_time`.

### Teammate/obstruction signalling (`crosshair.qc` 405-435)
HITTEAM: `wcross_scale /= crosshair_hittest` (shrinks). Blur: HITTEAM & `crosshair_hittest_blur_teammate` (**0**),
or HITOBSTRUCTION & `crosshair_hittest_blur_wall` (**1**, not in chase) → `wcross_blur=1`, `wcross_alpha*=0.75`; blur
draws a 5×5 spread of 0.04-alpha copies.

### Weapon-stat rings (`crosshair.qc` 458-583)
Drawn under the pic when `crosshair_ring` (**1**) or `crosshair_ring_reload` (**1**), radius
`wcross_size.x * resolution * ring_scale`. Priority chain reading `viewmodels[0]` (`wepent`):
1. **Vortex / Overkill-Nex charge** (`crosshair_ring_vortex` **1**): outer `crosshair_ring_nexgun` at `charge`,
   alpha `..._vortex_alpha` (**0.15**), colour=wcross_color; inner ring = chargepool (latched via
   `use_vortex_chargepool`) or `bound(0, ..._currentcharge_scale (30) * (charge - movingavg), 1)` with movingavg rate
   **0.05**, alpha `..._vortex_inner_alpha` (**0.15**), colour `(0.8,0,0)`. Inner only if `crosshair_ring_inner`
   (**0**).
2. **Mine Layer** (`crosshair_ring_minelayer` **1**): `mines/limit`, alpha **0.15**.
3. **Hagar** (`crosshair_ring_hagar` **1**): `hagar_load / load_max`, alpha **0.15**.
4. **Reload/ammo** (`crosshair_ring_reload` **1**, needs `clip_size`): `clip_load/clip_size`, `ring_reload_size`
   (**2.5**), alpha **0.2**; Rifle (clip 80) uses `crosshair_ring_rifle` art.
5. **Arc heat** (`crosshair_ring_arc` **1**): `arc_heat`, alpha lerps cold **0.2** → hot **0.5**, colour lerps
   wcross→hot `(1,0,0)`.
Ring also cross-fades on weapon switch (`wcross_ring_prev`). `crosshair_ring_size` **2**, `crosshair_ring_alpha` **0.2**
(the generic ring alpha cvar; the specific paths use their own alphas above).

### Dot (`crosshair.qc` 617-627)
`crosshair_dot` (**0**): draw `gfx/crosshairdot` at `resolution*crosshair_dot_size` (**0.6**), alpha
`crosshair_dot_alpha` (**1**); if `crosshair_dot_color_custom` (**1**) and `crosshair_dot_color`!="0" use that colour
(**"1 0 0"**) else the crosshair colour.

### Objective rings (`view.qc:HUD_Draw` 1003-1022) — NOT in HUD_Crosshair
Around `0.6*conheight` (not the crosshair centre), strict priority, only one draws: `STAT(NADE_TIMER)` (gated by
`cl_nade_timer`; colour `'0.25 0.90 1' + (t,-t,-t)`) > `STAT(CAPTURE_PROGRESS)` > `STAT(REVIVE_PROGRESS)` (both flat
cyan), via `DrawCircleClippedPic(..., "gfx/crosshair_ring", value, col, hud_colorflash_alpha, ADDITIVE)`.

### Per-weapon crosshair / 2D / vehicle
`crosshair_per_weapon` (**1**): use the weapon's `w_crosshair` pic and `w_crosshair_size` multiplier. `crosshair_2d`
(**"54"**): the side-scroller crosshair pic. Vehicle HUD routes to `info.vr_crosshair`.

### Reticle (`crosshair.qc:DrawReticle` 648-717) — separate full-screen scope
`cl_reticle` (**1**). Type 2 = weapon scope (`w_reticle`, e.g. Vortex) when zoomed and `cl_reticle_weapon` (**1**);
type 1 = generic `gfx/reticle_normal` while `+zoom`/zoomscript; suppressed dead/spectating/chase (unless
`cl_reticle_chase` **0**). Alpha scales with `current_zoomfraction` (min 0.25). `cl_reticle_stretch` (**0**),
`cl_reticle_normal_alpha`/`_weapon_alpha` (**1**).

## Port mapping
`CrosshairPanel.cs` is a single Godot `HudPanel` that ports nearly the whole `HUD_Crosshair` body in `DrawPanel()` +
`_Process()`. `RegisterDefaults` registers all 40+ `crosshair_*` cvars with the stock crosshairs.cfg values.
The reticle is `ReticleOverlay.cs` (`UpdateReticle`, a faithful `DrawReticle` port).

| Base feature | Port symbol | Live? |
|---|---|---|
| Master gating / enable / size / alpha | `DrawPanel` 553-559, `_cvEnabled` | live (panel `Visible` toggled by NetGame 2640) |
| Origin / chase camera + player-alpha fade | NOT IMPLEMENTED (always panel centre) | — |
| True-aim trace + HITTEAM/ENEMY/WORLD/OBSTRUCTION | `ComputeShotType`/`EnemyHitCheck`/`ClassifyObstruction` | live (Player-reconstruction path) |
| Colour: weapon / health / fixed | `BaseColor`/`HealthArmorColor`/`NumRampColor` | live |
| Colour: rainbow (special 3) | NOT IMPLEMENTED (only 0/1/2) | — |
| Pickup pulse | `_pickupSize` + `PulsePickup()` | **dead** (no caller of `PulsePickup`) |
| Hit indication (scale + channel add) | `_hitIndSize` block + `HitFlash` | live host-only (`HitFlash=1` NetGame 2086) |
| Smooth scale/alpha ease | `SmoothGoal` | live |
| Weapon-switch image cross-fade | `_crossPrev`/`_changeStartTime`/fadeIn | live (texture-switch driven) |
| Teammate shrink / blur (wall/teammate) | `DrawPanel` 595-605 + `DrawCrosshairTexture` blur | live |
| Vortex/Overkill charge ring + inner pool | `DrawStatRing` 866-895 | **dead** (no `ChargeFraction`/`ChargePool` feeder) |
| Mine Layer ring | `DrawStatRing` 896-904 | **dead** (no `MineCount` feeder) |
| Hagar ring | `DrawStatRing` 905-913 | **dead** (no `HagarLoad` feeder) |
| Reload/ammo ring (+ Rifle art) | `DrawStatRing` 914-924 | **dead** (no `ClipLoad`/`ClipSize` feeder) |
| Arc heat ring | `DrawStatRing` 925-938 | **dead** (no `ArcHeat` feeder) |
| Ring switch cross-fade | `SwitchRingFade` | dead (rings never fed) |
| Centre dot | `DrawDot` | live |
| Objective ring: NADE_TIMER | `DrawObjectiveRings` + NetGame 2100 feed | live host-only |
| Objective ring: CAPTURE / REVIVE | `DrawObjectiveRings` (props) | **dead** (no live feeder) |
| Per-weapon crosshair pic/number | `ResolveCrosshair` + `PerWeaponNumber` | partial (dict never populated → single number) |
| `crosshair_2d` side-scroller | NOT IMPLEMENTED | — |
| Vehicle crosshair (`vr_crosshair`) | NOT IMPLEMENTED | — |
| Reticle (`DrawReticle`) | `ReticleOverlay.UpdateReticle` | live (NetGame 2420) |

## Parity assessment
**Liveness is the dominant story.** The panel itself is live: `HudManager.SetPlayer` wires `Crosshair.Player`,
NetGame toggles `Crosshair.Visible` with a local player, and `_Process`/`DrawPanel` run every frame — so the static
crosshair, per-weapon/health/fixed colour, the dot, the true-aim recolour/shrink/blur (via the Player-reconstruction
aim ray), the weapon-switch image cross-fade, and the smooth ease are all genuinely on the live path. But a whole
class of dynamic feedback is **present-but-dead** because no live caller ever feeds the public stat setters:

- **All five weapon-stat rings are dead.** Grep over `src/`+`game/` finds **zero** assignments to
  `CrosshairPanel.ChargeFraction`, `.ChargePool`, `.ClipLoad`, `.ClipSize`, `.ArcHeat`, `.HagarLoad`, `.MineCount`
  (the only hits are unrelated weapon/player *state* objects, never the panel). `HasAnyRing()` is therefore always
  false and `DrawStatRing` returns early. In Base these rings are core feedback (Vortex charge, clip/ammo on every
  reloadable weapon). **A player sees no charge/clip/heat/mine/load ring at all.**
- **Pickup pulse is dead** — nothing calls `PulsePickup()`; the crosshair never bumps on item pickup.
- **Firing ring is dead** — `FiringRing` is never set (the legacy `NetHud` fallback uses its own `_fireRing`, but
  the skinned panel's is unfed).
- **Capture / revive objective rings are dead** — only `NadeTimer` is fed (NetGame 2100). FreezeTag writes
  `ReviveProgress` to the *player state*, and `NetEntity` serialises CAPTURE/REVIVE, but nothing pushes either to the
  panel. So capture-the-flag / freeze-tag progress around the crosshair does not show.
- **Hit indication is host-only.** `HitFlash=1` is set only inside `if (_server is not null)` (the host/listen path,
  via the damagetext mutator events). A pure remote client gets no hit-indication scale bump or colour flash. It is
  also coupled to the damagetext drain, not a dedicated hit stat. The decay/colour math itself is faithful.
- **NadeTimer objective ring is host-only** for the same reason.

**Logic/value gaps on the live parts:**
- **No third-person chase crosshair** (`crosshair_chase`/`_chase_playeralpha`): the panel always draws at its own
  geometric centre and never traces from the player org or fades the body alpha. Minor (chase cam is uncommon).
  Note: `crosshair_chase`/`_chase_playeralpha` are **not even registered** in `RegisterDefaults` (no reader, no cvar).
- **No `crosshair_color_special 3` (rainbow).** `ColorModeCvar` maps only 0/1/2; mode 3 silently falls to fixed.
- **`EnemyHitCheck` team test is not gated on teamplay** and does not special-case the spectator team the way Base
  does (`teamplay && t==myteam`; `t==NUM_SPECTATOR`→world). In a non-team game two players on team 0 can't collide
  here (0!=0 guard), so the practical effect is small, but it diverges from the Base branch structure.
- **Obstruction test is 3D-distance, not Base's screen-space test**, and is deliberately restricted to zero-box
  (hitscan) weapons. The port author documents this (CrosshairPanel 1098-1114): Base's real test reprojects the
  box-trace endpoint to 2D and compares frame-to-frame screen drift; the port lacks the projected-endpoint history
  the view layer would need to feed. So HITOBSTRUCTION blur is only approximate and only for hitscan.
- **Per-weapon crosshair pics:** `ResolveCrosshair` supports a `PerWeaponNumber` dict but it is never populated, so
  every weapon shows the same `crosshair` number. Base maps each weapon to its own `w_crosshair`. Partial.
- **`crosshair_2d` and the vehicle crosshair are absent.** Side-scroller mode and in-vehicle aiming have no port.
  `crosshair_2d` is not registered either.
- **Color is not eased** in the smooth-transition block: Base eases scale, alpha AND color over `effect_time`
  (`wcross_color = f*c + (1-f)*c_prev`); the port's `SmoothGoal` eases only scale+alpha and sets color directly.
- **Teammate-shrink divisor diverges at default cvars.** Base `wcross_scale /= crosshair_hittest` divides by 1
  (no shrink at the default), but the port divides by a separate `HitTestTeammateShrink` (default 1.25 = shrinks).
- **True-aim throttle is an intended divergence.** `TrueAimCanReuse` + `cl_crosshair_trueaim_rate` (default 30 Hz,
  view-unchanged cache always on) caps the two world traces; Base traces every frame. This is a documented perf
  change (the spawn-area low-FPS fix); cosmetic-only, so it is an intended divergence, not a gap.

**Values that match:** the registered cvar defaults are bit-faithful to crosshairs.cfg (incl. the cfg-over-qh
overrides: rainbow_brightness 20, effect_time 0.4). `max_shot_distance` 32768 and `g_trueaim_minrange` 44 match. The
projectile trace boxes (Devastator/Fireball/Seeker/Electro) match exactly. The hit-indication colour/speed, pickup
speed, ring alphas, vortex movingavg rate/scale, health ramp stops, and reload Rifle-art branch all match.

**Reticle:** `ReticleOverlay` is a faithful, live port of `DrawReticle` (type 1 generic / type 2 weapon scope, the
zoomfraction alpha ramp, the dead/spectate/chase suppression, `cl_reticle*` cvars). Scored as its own row.

## Verification
- Base: read `crosshair.qc` (full), `crosshair.qh`, `crosshairs.cfg` (defaults), `view.qc` UpdateDamage/HitSound/
  HUD_Draw. Constants taken from crosshairs.cfg (the live defaults) over the .qh inline values.
- Port: read `CrosshairPanel.cs` in full + `ReticleOverlay.cs`. Liveness by code-trace:
  - `Crosshair.Player` set live: `HudManager.cs:322`; `Visible` toggled: `NetGame.cs:2640`.
  - `Crosshair.HitFlash = 1f`: only `NetGame.cs:2086`, inside `if (_server is not null)` (host path).
  - `Crosshair.NadeTimer`: only `NetGame.cs:2100`, host path.
  - Grep `\.ChargeFraction =|\.ChargePool =|\.ClipLoad =|\.ClipSize =|\.ArcHeat =|\.HagarLoad =|\.MineCount =|`
    `\.FiringRing =|\.CaptureProgress =|\.ReviveProgress =|\.AimForward|\.AimOrigin|\.PulsePickup\(` over `game/`+
    `src/` (excl. tests/bin): **no assignment targets the panel** (matches land on weapon/player state objects).
  - Reticle live: `_reticle.UpdateReticle(...)` `NetGame.cs:2420`.
- Not run in-game this pass (static audit). Status of the live-but-unverified-visually rows kept at confidence
  medium; the dead rings/pulse are high-confidence dead by grep.

## Open questions
- Are the weapon-stat ring feeders intended to be wired from the predicted local weapon state (clip_load/charge are
  available client-side via the inventory/weapon state), or only from a networked `wepent`-equivalent? The setters
  exist and are documented "fed by the net layer" but no net-layer code calls them.
- Should hit-indication and the NADE_TIMER ring be fed on the **remote-client** path (currently host-only)? Needs a
  client-side hit/objective stat rather than the host damagetext drain.
- Is the chase-camera crosshair worth porting given third-person is rarely used in the port?
