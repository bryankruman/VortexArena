# Unportable Analysis

_Authored 2026-06-28. A clear-eyed pass over the **357 gaps the parity waves flagged `unportable`** (see
[DRIFT-2026-06-27-waves16-17.md](DRIFT-2026-06-27-waves16-17.md), [REMAINING-WAVES.md](REMAINING-WAVES.md)).
Goal: decide which are *genuinely* unportable, which are a *decision we have not yet made*, and which are
just *difficult / a dedicated feature build* — and give each cluster options + a recommendation._

## The headline

**Almost nothing here is truly unportable.** The port is C# + Godot reimplementing a QuakeC + DarkPlaces
game. Anything DarkPlaces/CSQC does at runtime — a SubViewport mirror, a networked stat, a rope polyline, a
world model with frame animation, a bot VM — Godot and C# can do too. "Unportable" was a workflow label
meaning *"a code agent doing surgical one-file edits cannot honestly close this in a single pass"*. It was
never a claim of impossibility, and it should not be read as one.

When you sort the 357 by *why* they're open, they fall into four buckets:

| Verdict | Meaning | ~count | What it needs |
|---|---|---:|---|
| **Truly unportable** | No faithful realisation possible on this engine | **~0** | (see "The only real candidates" below) |
| **A decision** | Portable, but we should consciously choose to do/skip it | **~70** | A scope call from you, then maybe code |
| **Difficult (feature build)** | Portable, just bigger than a surgical edit | **~210** | A dedicated task with a spec + plumbing |
| **Verify-only / false positive** | Already faithful, or only needs a live run | **~75** | Run the app, or nothing |

The two numbers that matter: **truly unportable ≈ 0**, and roughly **a fifth of the "unportable" list is not
actually a gap** (it is faithful-but-dormant code, or it just needs an in-game eyeball). The rest is real work,
but it is *ordinary* work — render subsystems, networking wire, art-asset wiring, bot AI — none of it blocked
by the engine.

## The only real candidates for "truly unportable"

There is one honest entry, and even it is "different by design," not impossible:

- **`physics-movetypes.matchticrate.interp`** — DarkPlaces' `Movetype_Physics_MatchTicrate` does sub-tick
  interpolation of CSQC entities (gibs/casings/projectiles/trains) at render rate using `tic_*` snapshot
  fields. The port advances entities on the fixed server tick and smooths on the **render side** instead
  (`PredictionBuffer`/`ClientNet`). This is an *architectural* divergence, not a missing feature: the port
  reaches the same visual end (smooth motion between ticks) by a different mechanism. → **Mark
  `intended_divergence: true`** with a rationale rather than "porting" the QC mechanism verbatim, *if* a live
  check confirms render-side interpolation already covers gibs/casings/projectiles. (It is in the
  verify-only bucket precisely because that confirmation is a live run.)

Everything else below is portable. The question is only *whether* and *how*.

---

# Cluster A — View / camera render subsystems (CSQC view-space)

**What they are.** Client view-space rendering that DarkPlaces draws in CSQC: see-through warpzone portals,
the hook rope-line, the porto trajectory preview, the chase/third-person camera, the crosshair chase trace,
demo free-camera, the 2.5D viewloc camera, and `func_camera` security-camera surfaces.

**Items (≈14):** `warpzones.client.fixview`, `warpzones.client.teleported_view`, `warpzones.client.render`
(✅ landed Wave-18, listen-host only), `warpzones.combat.traceextras`, `warpzones.client.prediction`,
`warpzones.camera`, `warpzones.porto`, `weapon-hook.presentation.rope_line`,
`weapon-porto.presentation.trajectory_preview`, `cl-crosshair.core.origin_chase`,
`cl-view.chase.classic_thirdperson`, `cl-view.demo.free_camera_lockview_ortho`,
`mutator-bugrigs.racecar.drive_model` (chase-cam dependent).

**Portable?** Yes — entirely. Godot has SubViewports (warpzone mirror already proves it), `ImmediateMesh`
line/cylinder drawing (rope, trajectory, beams), and free camera control. None of this is blocked.

**Options**
1. **One "client view subsystem" feature task** that builds the shared primitives once — a networked
   poly-line renderer (`Draw_CylindricLine` analog), a chase-camera mode, and a trajectory predictor — then
   wires each consumer (hook rope, porto preview, third-person). _Recommended._ These share 80% of their
   plumbing; building them together avoids three half-overlapping one-offs.
2. Port each consumer independently as it becomes a priority. Cheaper to start, but you rebuild the
   line/cylinder + chase-cam primitive three times.
3. Defer the niche members (demo free-cam, viewloc 2.5D, `func_camera`) and ship only hook-rope +
   porto-preview + chase-cam, which are the ones a normal match actually sees.

**Recommendation:** Option 1 for the gameplay-visible members (hook rope, porto preview, third-person/chase,
crosshair chase), with Option 3's deferral applied to demo-camera / viewloc / `func_camera` (dev/niche).
Warpzone see-through already landed; finish it by promoting it off listen-host-only (see Cluster B's
networking note) and doing the in-game eyeball.

---

# Cluster B — Networking-infrastructure gaps (wire a value the client already wants)

**What they are.** A client HUD/render consumer exists and is correct, but the **server never networks the
value it needs**. The recurring root cause behind a surprising number of "missing visual" gaps is one of two
holes: (a) no per-weapon entity-state (`wepent`) channel, and (b) no turret/objective entity stream.

**Items (≈12):** `weapon-vortex.fx.charge_glow`, `weapon-vortex.hud.charge_ring`, `weapon-vortex.fx.beam`,
`weapon-arc.beam.visual` (tint), `nexball.hud.modicon` (`NB_METERSTART`), `nexball.weapon.power_meter`,
`cl-viewmodel.anim.networked_frames` (the `wframe` temp-entity), `weapon-electro.orb.csqc_netlink_draw`,
`turret-framework.net.sync` (`TNSF_*`), `items-pickups.itemgroup.networking`,
`mutator-multijump.counter.networking`, `sv-world-rules.entity.pingplreport` (✅ mostly done — ping now wired).

**Portable?** Yes. This is the most mechanical cluster — it's "add a field to a wire struct, set it
server-side, read it client-side." The reason these are flagged is that the *consumers* are scattered across
many units, so no single surgical edit closes them; they need one coherent channel.

**Options**
1. **Build the `wepent` (per-weapon entity-state) channel once** and re-point every charge-ring / charge-glow
   / beam-tint / clip / networked-viewmodel-frame consumer at it. _Recommended for the weapon half._ This
   single piece of plumbing closes ~6 weapon "presentation" gaps that are currently blamed on missing
   visuals but are really missing *data* (the visuals already render off `LocalServerPlayer` on a listen
   host — they just have no value on a pure remote client).
2. **Build the turret/objective entity stream** (`TNSF_*` analog) to feed turret world models + Onslaught
   markers. Pairs naturally with Cluster E (world models) and Cluster C (radar).
3. Quantize-and-fold individual values into existing per-tick wires ad hoc (as was done for ping/PL). Works
   for one-offs, but you accrete N bespoke encodings instead of one entity channel.

**Recommendation:** Option 1 + Option 2 as two scoped networking tasks. They are prerequisites that unblock
large parts of Clusters A, C, and E, so they have the highest leverage of anything in this document. Do these
early.

---

# Cluster C — Waypoint-sprite / radar HUD-marker subsystem

**What they are.** The screen-edge / radar objective markers: CTF flag carriers, ONS/Assault generators &
control points, buff items, monster health bars, item-respawn countdowns, and the maximized/clickable radar.

**Items (≈12):** `mutator-waypoints.registry.defs`, `…server.manager_api`, `…client.draw`, `…client.fades`,
`…ctf.flag_markers`, `…monster.healthbar`, `…itemstime.respawn_countdown`, `ctf.hud.waypoints`,
`buffs.waypoint`, `cl-teamradar.maximized.map`, `cl-teamradar.maximized.click`, `cl-teamradar.mutator.hook`,
`cl-teamradar.links.onslaught` (ONS link lines ✅ partially landed).

**Portable?** Yes. It is a self-contained subsystem (WaypointSprite spawn/network/draw) that Base implements
once and reuses everywhere. The port has the radar panel and ONS links already.

**Options**
1. **Build the WaypointSprite subsystem as one feature task** (registry of sprite defs → server manager API
   → networked sprite entities → client edge/radar draw with fades), then attach the consumers (CTF flag,
   buffs, monster healthbars, ONS). _Recommended._ This is the canonical "build the framework, consumers are
   trivial" case.
2. Add the maximized/clickable radar (`hud radar 1`, ONS spawn-click) as a **separate** follow-on — it's a
   distinct interaction surface, lower priority than the markers themselves.

**Recommendation:** Option 1 now (it's a visible, frequently-seen feature and unblocks several gametypes),
Option 2 deferred (tactical-overview radar is a power-user nicety).

---

# Cluster D — Bot AI deep systems

**What they are.** The parts of the bot AI beyond basic combat: the `bot_cmd` scripting VM, the in-game
waypoint editor + runtime auto-waypointing, per-gametype bot roles (CTS course-running, Freezetag
free/offense, Keepaway, Onslaught), and movement/aim refinements (steerlib, bunnyhop, jetpack rocket-jump,
ballistic `tracetoss` lead aim, dodging).

**Items (≈20):** `bot-ai.scripting.commands`, `bot-waypoints.editor`, `…steer.steerlib`, `…auto.*`,
`…graph.autolink`, `…path.routetogoal`, `…steer.movetogoal`, `…steer.bunnyhop`, `…file.save`,
`cts.bot.role`, `freezetag.bots`, `keepaway.bot.role`, `onslaught.bot.roles`,
`bot-ai.special.jetpack_rocketjump`, `bot-ai.aim.lead`, `bot-ai.combat.dodge`, `bot-ai.target.chooseenemy`,
`bot-ai.roles.goalrating`, `bot-ai.move.steer`, `bot-ai.think.throttle`.

**Portable?** Yes, but this is the largest *logic* build in the set. None of it is engine-blocked; it's
faithful re-implementation of a big QC AI codebase.

**Options**
1. **Tiered, in priority order:** (a) per-gametype bot **roles** first (so bots actually play CTS/FT/KA/ONS
   — highest gameplay impact), (b) movement/aim refinements (steerlib, bunnyhop, lead aim), (c) the
   **waypoint editor** and (d) the **`bot_cmd` scripting VM** last (dev/mapper tooling, near-zero
   match impact). _Recommended._
2. Treat the whole thing as one epic. Honest but unwieldy; the four sub-areas have very different value.
3. **Decision to skip the tail:** explicitly mark `bot_cmd` scripting VM and the in-game waypoint editor as
   *deferred / out-of-scope-for-now* (they serve mappers and cutscenes, not normal play) and record that as
   an intended divergence with a "revisit if we ship a map-editor story" note.

**Recommendation:** Option 1's ordering, with Option 3 applied to the tail (`bot_cmd` VM + waypoint editor).
Bots-don't-play-the-objective is a visible quality gap; a missing bot scripting VM is invisible in a normal
match.

---

# Cluster E — World models + animations (art-asset wiring)

**What they are.** Entities that should render a real `.md3`/`.iqm`/`.dpm` model with frame animation but
currently show a placeholder, a tint, or nothing: all the **vehicles** (raptor/racer/bumblebee/spiderbot +
framework), all the **turrets** (world models + head spin/recoil anim), **monster** animations (golem/mage/
zombie/spider — now driven by `DpmFrameDriver`, pending eyeball), the Freezetag **ice block** + frozen pose,
the **buff** carrier glow model, the **portal disc** model, **nade** projectile models, and weapon
**muzzle-flash** entities.

**Items (≈90 — the single biggest cluster, dominated by `vehicle-*` and `turret-*` `presentation:partial`
rows).**

**Portable?** Yes. Xonotic ships every one of these models; the work is *loading + animating + attaching*
them via the entity feed, plus (for turrets/vehicles) the networking from Cluster B. The animation infra
landed in Wave-17 (`DpmFrameDriver`, `ModelAnimator.FollowEntityFrame`).

**Options**
1. **Decide the vehicle/turret scope first.** Vehicles and turrets are ~70 of these items. They are only
   meaningful if Onslaught/vehicle maps are in scope. If yes → one "vehicle/turret presentation" task riding
   the Cluster B entity stream. If they're not a near-term priority → mark the whole `vehicle-*`/`turret-*`
   presentation tail as **deferred** (one decision retires ~70 "unportable" items honestly). _Recommended:
   make this call explicitly — it's the highest-count decision in the document._
2. **Do the always-visible models regardless:** ice block, frozen pose, buff glow, portal disc, nade models,
   muzzle flash — these appear in mainstream gametypes and are cheap now that the anim infra exists. _Do
   these independent of the vehicle/turret decision._
3. Per-entity as each gametype gets attention.

**Recommendation:** Option 2 unconditionally (mainstream cosmetic models, low cost, high visibility), and
Option 1 as a deliberate scope decision for vehicles/turrets — most likely **defer** them unless ONS/vehicle
maps are on the roadmap, and record that as an intended divergence so they stop counting as silent gaps.

---

# Cluster F — Map-support / auto-gametype heuristics

**What they are.** Base auto-offers certain gametypes on maps that lack an explicit mapinfo tag, using a BSP
**world-bounds + spawnpoint-count** probe (`m_isAlwaysSupported` for CA/big-arena, `m_isForcedSupported` for
`g_tdm_on_dm_maps`), plus per-map team declarations (`spawnfunc_tdm_team`, `teams=` mapinfo override).

**Items (≈4):** `tdm.rules.map_support`, `clanarena.mapinfo.always_supported`, `duel.mapsupport.gating`,
`tdm.rules.map_team_config`.

**Portable?** Yes — the port already loads BSP; computing world diameter + counting spawnpoints is
straightforward. The blocker is that the menu's mapinfo layer is currently *file-only* (no BSP probe).

**Options**
1. Add a BSP world-bounds + spawn-count probe to the mapinfo layer and wire the three heuristics.
2. **Decision to skip:** declare auto-map-support out of scope — maps must tag their gametypes explicitly.
   Retires the cluster as an intended divergence. _Lowest effort._
3. Hybrid: implement the cheap `m_isForcedSupported`/`tdm_team` half (no BSP probe needed for the cvar-driven
   forced-support), defer the BSP-bounds `m_isAlwaysSupported` half.

**Recommendation:** Option 3. The forced-support + per-map team config are cheap and occasionally matter;
the BSP-bounds auto-support is a niche convenience — defer it explicitly. This is a **decision**, not a
difficulty.

---

# Cluster G — Active-mutators bitmask surface

**What it is.** Base's `active_mutators()` / `mut_set_active(MUT_*)` builds a bitmask + translated-name list
consumed by the server-browser "mutators" field and the in-HUD active-mutator icon strip. The port has a
*different* mechanism (`BuildMutatorsString`/`BuildMutatorsPrettyString` for the serverinfo/eventlog), so the
**HUD icon strip + server-browser bitmask** never light up. This is **systemic** — it shows up under several
mutators but is one missing surface.

**Items (≈4 named, systemic):** `mutator-invincibleproj.client.active_mut_flag`,
`mutator-bloodloss.presentation.active_mutator_signalling`, `touchexplode.list.activemutators`, and the
implicit `MUT_*` flag for every other mutator.

**Portable?** Yes — it's a bitmask + a HUD icon row.

**Options**
1. Implement the `MUT_*` registry + `mut_set_active` + a HUD active-mutator icon strip once; every mutator
   gets it for free. _Recommended if the active-mutator HUD strip is wanted._
2. **Decision to skip the icon strip** and rely on the existing textual "Active modifications:" string (which
   is already live). Retires the cluster as intended divergence; the *information* is already conveyed, just
   not as icons.

**Recommendation:** Option 2 unless you specifically want the iconified strip — the textual mutator list
already tells the player what's active. This is a **decision**; mark it and move on.

---

# Cluster H — Interactive scoreboard + scoreboard blocks

**What they are.** The port scoreboard is a passive overlay. Base's is interactive (TAB cycling, arrow
selection, ENTER/SPACE join, Ctrl+C/R/T/K layout/tell/votekick, team-selection screen) and has extra blocks
(per-item pickup-count grid, accuracy, map-stats, player-color swatch, ready-check).

**Items (≈6):** `cl-scoreboard.ui.interactive`, `cl-scoreboard.block.itemstats`, `cl-scoreboard.block.accuracy`,
`cl-scoreboard.block.mapstats`, `cl-scoreboard.field.name_icons` (partial), `cl-scoreboard.draw.export`.

**Portable?** Yes. The interactive UI is input-routing + state; the extra blocks need their data networked
(itemstats needs inventory networked — ties to Cluster B).

**Options**
1. Build the interactive scoreboard (input modes + team-selection screen) as one task.
2. **Decision to keep it passive** — the port already has working join/spectate flows via other UI; the
   interactive scoreboard duplicates that. Defer; add only the cheap **blocks** (accuracy, map-stats) that
   need no interaction.
3. Full parity (interactive + all blocks + inventory networking).

**Recommendation:** Option 2. The interactive scoreboard is a large UI build whose core functions
(join/spectate) already exist elsewhere; the high-value pieces are the cheap *blocks*. Mark the interactive
half a deliberate deferral.

---

# Cluster I — Dev / cheat / editor / sandbox tooling

**What they are.** `sv_cheats` toys and mapper/dev tooling: `CopyBody` clone, the Drag entity-grab system,
`particles_make`, in-map race-checkpoint editing, debug verbs (`radarmap`, `gettaginfo`, `bbox`, `trace`,
`animbench`, `make_mapinfo`), the Sandbox object editor, and the floating kill-countdown digit model.

**Items (≈10):** `sv-cheats.impulse.clone`, `sv-cheats.frame.drag`, `sv-cheats.cmd.particles_make`,
`sv-cheats.mapent.*`, `mutator-sandbox.*` (drag/grab, object spawn/edit/scale/duplicate),
`sv-commands-votes.debug_tools.missing`, `sv-clientkill.indicator.entity`.

**Portable?** Yes, all of it — but it serves developers, mappers, and cheat-enabled servers, not normal play.

**Options**
1. Port on demand when a specific tool is needed (e.g. `radarmap` if you want minimap generation).
2. **Decision to defer the lot** as low-priority dev/cheat tooling; record as intended divergence. The
   Sandbox mutator is itself a niche server mode. _Recommended._

**Recommendation:** Option 2. This cluster is almost pure **decision-to-defer**: high item count, near-zero
mainstream-gameplay impact. Retiring it honestly removes ~10 items from the "unportable" worry list.

---

# Cluster J — Persistent records / stats database

**What they are.** Cross-session persistence: the CTF fastest-capture leaderboard, the race/CTS record store
(`ServerProgsDB`), qualifying records, and xonstat player-stats latency accumulation.

**Items (≈5):** `ctf.leaderboard`, `cts.records.getrecords`, `race.qualifying.mode` (records half),
`race.hud.mod_icon_rankings`, the latency half of `sv-world-rules.entity.pingplreport`.

**Portable?** Yes — it needs a persistence layer (file/db) the port hasn't built.

**Options**
1. Build a small record-store persistence layer once; CTF/CTS/race records + rankings all consume it.
   _Recommended if Race/CTS/CTF records matter to you (they're a core part of the race scene)._
2. Defer; in-match (non-persistent) records already work for most of these.

**Recommendation:** Option 1 if Race/CTS is a target audience (the record store is the *point* of those
modes); otherwise Option 2. A **decision** gated on whether the race community is in scope.

---

# Cluster K — Niche gametype rules & dead-code wiring bugs

**What they are.** Per-gametype edge cases — some genuinely missing logic, but several are **dead-code wiring
bugs** (the logic was ported but has no live caller), which are closer to *surgical fixes* than feature
builds and were over-flagged.

**Items (≈25):** `cts.checkpoints.intermediate`, `race.spawn.grid_respawn`, `race.qualifying.mode`,
`tdm.rules.map_team_config`, `overkill-weapons.mode.weapons_activation`, `freezetag.frags.center_message`,
`freezetag.damage.softkill_void`, `ctf.abort_speedrun`, `ctf.drop_special_items`, `ctf.followfc_superspec`,
and the ClanArena dead-code set (`clanarena.spectate.force_spectate_cmd`,
`clanarena.join.late_join_observer`, `clanarena.lifecycle.reset_map`,
`clanarena.matchend.restore_status`), plus assorted nexball/keepaway/tka/oneflag details.

**Portable?** Yes — and a meaningful fraction are **mis-classified**: the ClanArena items and
`freezetag.frags.center_message` are *producers that were written but never subscribed/called* (e.g.
`GametypeSpectateCommand` has zero callers; `ResetMap`'s switch has no `case ClanArena`). Those are
one-to-few-line wiring fixes, not feature builds.

**Options**
1. **Triage this cluster into two piles:** (a) the dead-code wiring bugs → a quick "wire-check" surgical
   pass (the same discipline that caught A4's four dead seams — see the wave-orchestration memory); (b) the
   genuinely-missing logic (CTS intermediate checkpoints, race start-grid, `tdm_team`) → small per-feature
   tasks. _Recommended._
2. Leave them in the general feature-build pile (over-counts effort).

**Recommendation:** Option 1 — and do the wiring-bug pile *first*; it's cheap and it closes real,
live-path-visible bugs (CA force-spectate, FreezeTag "You froze X" centerprint) that are currently dead.

---

# Cluster L — Misc presentation / audio polish

**What they are.** Cosmetic fidelity that works but isn't pixel/cue-faithful: `bgmscript` ADSR prop
animation, `func_clientwall` distance-fade, `func_*` mover audio cues (bobbing/pendulum/fourier),
`target_music`/`target_speaker` details, flag-wave animation, various particle/sound-cue partials, and the
many `presentation:partial`/`audio:partial` long-tail rows.

**Items (≈40, scattered).**

**Portable?** Yes, all minor.

**Options**
1. Sweep them opportunistically — fold each into whatever wave touches its unit next.
2. One "presentation polish" cleanup pass after the structural clusters (A–E) land, since many depend on
   those (e.g. fade renderers, mover audio).

**Recommendation:** Option 2 — defer to a polish pass; none are individually worth a dedicated task, and
several unblock only after the render/networking subsystems exist.

---

# Cluster M — False positives: faithful-but-dormant (NO action)

**What they are.** Flagged `liveness:dead`, but the code is **present and faithful** — it's simply not reached
at *stock* cvars, **exactly as in Base**. These are not gaps; the registry already notes "matches Base."

**Items (≈7):** `weapon-machinegun.fire.single_mode0`, `weapon-machinegun.reload.checkammo`,
`weapon-electro.primary.midaircombo`, `weapon-crylink.reload`, `weapon-porto.fire.aim_hold`,
`keyhunt.capture.instant_path_dead`, `overkill-weapons.oknex.zoom`.

**Recommendation:** **No action.** Confirm they carry `intended_divergence`/"matches Base" notes so they stop
appearing as worry items, and optionally drop them from the "unportable" tally entirely — they inflate the
count without representing real work.

---

# Cluster N — Verify-only (run the app, don't write code)

**What they are.** The **34 [NEEDS-INGAME-CHECK.md](NEEDS-INGAME-CHECK.md) items**: presentation/timing/
liveness that code-reading marked `unknown` per the skeptical "unverified visible fidelity = unknown" rule.
Includes HUD panel timing (weapons/powerups/physics/itemstime/scoreboard), the monster DPM animation
(golem/mage/zombie/spider, now code-complete via `DpmFrameDriver`), bugrigs drive feel, warpzone render
fidelity, and the **water/ladder/jetpack** physics legs (all code-upgraded to faithful in Wave-13, pending a
live run).

**Recommendation:** **Run `/verify`** on a listen server across a CTF/ONS/race/monster map and tick these off.
Most will resolve to `faithful` (the code already landed); the value is converting `unknown` → confirmed, not
writing new code. This is the single cheapest way to shrink the "open gap" count.

---

# What to do next (recommended sequence)

1. **Reclassify the registry** so the count reflects reality: move Cluster M (≈7) to `intended_divergence`/
   matches-Base, and stamp the deliberate deferrals from Clusters F/G/H/I (and likely E's vehicles/turrets)
   as `intended_divergence` with rationales. This alone honestly retires **~80–90** of the 357 without a line
   of gameplay code — they become *decisions on record*, not open gaps.
2. **Run `/verify`** to clear Cluster N (≈34 → mostly confirmed faithful).
3. **Quick wire-check pass** over Cluster K's dead-code bugs (CA seams, FreezeTag centerprint) — cheap, real,
   live-path fixes.
4. **Two networking tasks** (Cluster B): the `wepent` channel and the turret/objective entity stream — they
   unblock the most downstream visuals.
5. **Two framework tasks:** WaypointSprite subsystem (Cluster C) and the client-view primitives (Cluster A:
   hook rope + porto preview + chase cam).
6. **Bot roles** (Cluster D part a) for objective-playing bots.
7. **Mainstream cosmetic models** (Cluster E part 2: ice block, buff glow, portal disc, nades, muzzle flash).
8. Decide vehicles/turrets (Cluster E part 1), persistent records (Cluster J), and the polish sweep
   (Cluster L) as roadmap calls.

**Bottom line:** the "unportable" list is misnamed. One item is a defensible *architectural divergence*; the
rest is ordinary porting work plus a set of scope decisions we simply haven't made yet. Making those decisions
explicit — and verifying what already landed — converts most of the list from "unportable" to "done,
deferred-on-purpose, or scheduled."
