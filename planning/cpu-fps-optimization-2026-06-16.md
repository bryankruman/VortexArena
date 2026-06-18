# CPU / FPS Optimization Investigation — XonoticGodot vs DarkPlaces

**Date:** 2026-06-16 · **HW:** RTX 3080 (GPU idle — CPU-bound) · **Baseline:** catharsis 68→228 fps (release, post-fix); DarkPlaces 300+ on the same map → residual ~30% gap.

**Method:** a multi-agent audit across 12 CPU dimensions (per-frame client hotpath, C#↔Godot interop, server tick, collision/spatial, render submission, GC/alloc, engine config, threading, HUD, particles, net/prediction, hot algorithms) + 3 DarkPlaces source-comparison tracks, each finding adversarially verified against the code; merged into the ranked plan below. (Two earlier runs were partly lost to transient server rate-limiting/500s and recovered.)

**Independently re-verified anchors (read directly, not just agent-reported):**
- `EngineServices.Spawn()` (`src/XonoticGodot.Engine/Simulation/EngineServices.cs:90`) recycles the *slot* but does `new Entity { Index = idx }` every time — Entity is a partial class across **29 files** (fat object). → Entity-pool finding is real.
- `EntityNode._Process` (`game/EntityNode.cs:54`) sets `Position` + `Basis` per entity per frame; `ClientWorld` creates **one EntityNode per networked entity** (`ClientWorld.cs:429`) and `_Process` makes several `foreach` passes over `_entityNodes` (`:985,:1001,:1034`). → per-node submission tax is real (the rcpu gap).
- The **entity area-grid (D1) is already implemented** — `EntityAreaGrid.cs` + `TraceService.EntitiesInBox` (the old "missing areagrid" premise is stale). The **world-brush** broadphase is a 2D uniform XY grid + `_outside` overflow scanned every query (`CollisionWorld.Query`, `Brush.cs:489`) — BIH is an *incremental* algorithmic win, not a from-scratch areagrid.
- `sv_threaded` worker + `_simGate` are **built and verified-clean** (0 desync/~3 min per the in-code comment) but **default 0** (`NetGame.cs:558,616,783`).
- `PlayerModel.PushBones` is already heavily optimized (off-screen pose-cull, distance half-rate, unit-scale `SetBonePoseScale` skip, `stackalloc`) — residual interop is the 2 calls/bone with no Godot bulk API.
- `project.godot` has **no `[physics]` section** → Godot's 60 Hz PhysicsServer3D runs purely to integrate ≤128 cosmetic gib/casing nodes (no `CollisionShape3D`; collision is the pure-C# TraceService).

**Bottom line:** there is no second 3× hiding here — the big steady-state pathologies were already fixed (catharsis 3.4×). The remaining 228→300 gap is **render-submission / interop CPU** (Godot's per-node scene-tree tax that DP's flat batched renderer avoids), plus the **72 Hz server tick sharing the render thread** on the listen server, with **BIH** the algorithmic tail and **per-frame cvar/replay churn** the high-fps multiplier. The path to "match/beat DP" is the combination R1–R6 below, not any one lever.

---

# XonoticGodot Steady-State FPS Plan — Closing the 228→300 Gap to DarkPlaces

## 1. Executive Summary

**Where the gap lives.** Catharsis already went 68→228 fps (release); DP holds 300+. The residual ~30% is **CPU-bound with the GPU idle** — `proc` (interop marshalling) is only ~0.9ms/frame at 228fps, so the deficit is **render-submission CPU (rcpu)**: the per-frame cost of pushing the scene-tree world into Godot's RenderingServer. Concretely it is (a) **one Godot `_Process` callback per entity node** crossing the managed↔native boundary, (b) **ungated transform/morph writes** for entities that didn't move or are PVS-culled, (c) **per-frame HUD canvas re-records** that fire even when nothing changed, (d) **per-tick cvar-dictionary churn** in the movement/prediction replay loop (multiplied by fps × replay depth), and (e) **per-particle point-contents** that is structurally slower than DP's BSP leaf descent. DP wins because it is a **flat batched immediate renderer with no scene tree** — its per-object cost is an indirect call + a transform, not a node-graph traversal + a marshalled callback. We *beat* DP on gameplay (compiled C# vs the QC PRVM interpreter), so the lever is render/interop, not simulation logic.

**The 4–6 biggest levers (honest magnitudes — estimates unless marked measured):**

1. **Collapse per-node `_Process` into one central loop + change-gate transform writes** (R1). `EntityNode._Process` and `ModelAnimator._Process` each fire a native→managed callback per node per frame with **no change-gate** (verified: `EntityNode.cs:54`, no `SetProcess(false)` anywhere, no `lastOrigin`/`lastYaw` compare). At ~150 entities × 228fps that is ~30k callback crossings/sec, most for resting items/gibs/props. `ClientWorld._Process` *already* iterates `_entityNodes` (verified `ClientWorld.cs:1001`), so the work folds into a loop that already runs. This is the single largest rcpu lever. *Estimate: meaningful fraction of the gap; the dispatch-overhead half is tens-of-µs-class per entity, the dirty-mark-skip half scales with resting-entity count.*

2. **Gate PVS-culled entities out of submission entirely** (R2). The PVS cull today only sets `Visible` — Godot does **not** gate `_Process` on `Visible`, so culled entities still pay full `SyncFromEntity` + (for animated MD3) morph work behind walls (verified `ClientWorld.cs:978-1018`). Folding a `_pvsVisible` skip into the R1 loop converts the cull from a GPU-only win into a CPU-submission win. *Note: with `cl_gpu_morph` now default-on, the heavy ClearSurfaces+AddSurfaceFromArrays re-upload is narrower than first claimed — the broad win is the transform-sync skip across all culled entities.*

3. **`sv_threaded`: move the 72Hz server tick off the render thread** (R3). **Built and verified-clean, default-off.** On the listen server the entire authoritative sim runs in the render-frame budget today. Combat ticks are **15–25ms** (verified) — they directly steal from frame time. This is the top *listen-server* lever and the only one that removes a whole workload class from the render thread rather than shrinking it. It is a **hitch/throughput lever, not the steady-state idle floor** (be honest: at idle the tick is ~2ms, the gate-span overlap is poor, and one prior co-finding — the "ClientWorld race" — was **disproven**, so the "two blockers" framing was overstated). Still: flipping it on for the listen server is the highest-leverage single config change for populated combat.

4. **Memoize the resolved `MovementParameters` struct** (R4). `MovementParameters.FromCvars` does **~45 `Cvar(N(...))` calls + the `g_movement_highspeed` GetString/GetFloat probe** per call (verified `MovementParameters.cs:143-207`) — ~90 dictionary ops. In default `cl_movement_perframe=1`, prediction replays the full unacked window **every render frame**, so this runs fps × depth ≈ 700–900 times/sec → ~65–80k redundant dict lookups/sec, all returning the same struct between snapshots. Memoize the base struct, invalidate on `MoveVarsBlock.Apply`. Also fixes the server leg and folds in the redundant `sv_gameplayfix_nudgeoutofsolid` read at line 207.

5. **HUD: add `NeedsRedraw` change-gates to the four always-on combat panels + the crosshair** (R5). HealthArmor/Ammo/Weapons/Powerups and CrosshairPanel re-record their full canvas item **228×/sec** even when the displayed values are static, because none override `NeedsRedraw()` (defaults true) so `HudManager.cs:249` force-redraws them. The crosshair even computes its own correct `dirty` flag and then **throws it away**. This is the biggest aggregate HUD canvas-re-record + string-alloc saving because these panels are on the entire match.

6. **BIH for static-world collision** (R6, the one true *algorithmic* gap vs DP). The flat XY grid scans the whole `_outside` brush list on long rays; BIH makes traces flat vs brush count (~20–40×, *measured* on the catharsis fix: ~2.06ms→~0.05–0.1ms long traces). This also fixes the per-particle point-contents slowness (R7) at the root.

**Realistic upside:** No single item closes 30% alone. R1+R2 together attack the dominant rcpu term and are the best bet to recover the bulk of it in firefights; R3 removes the 72Hz tick from the frame on the listen server; R4 lifts the per-frame replay tax that scales worst at high fps. Expect to **close most of the gap with R1–R6 combined**, with the firefight floor improving more than the idle ceiling. **Measure before/after each** — several magnitude claims are estimates.

---

## 2. Ranked Optimization Table

Ranked by (CPU impact × confidence ÷ effort), render-frame-path items above server-tick items. Merged/de-duped across PRIOR + RECOVERED. Items that share a fix are noted.

| # | Optimization | Mechanism | Files (file:line) | Est. impact + scaling | Effort | Risk | Theme |
|---|---|---|---|---|---|---|---|
| 1 | **Collapse per-node `_Process` → one ClientWorld loop + change-gate transform writes** | `EntityNode._Process`/`ModelAnimator._Process` fire one native→managed callback per node/frame; transform written unconditionally, no dirty-gate, no `SetProcess(false)`. Fold sync into the existing `_entityNodes` loop; gate on origin/yaw/ItemAnimate unchanged. | `game/EntityNode.cs:54,61,81,101`; `game/client/ModelAnimator.cs:255`; `game/client/ClientWorld.cs:429,1001,1034` | Largest rcpu lever. ~150 ents × 228fps ≈ 30k crossings/s removed; dirty-skip scales with resting-entity count × fps | M | med | render-submission / interop |
| 2 | **Gate PVS-culled entities out of `_Process` submission** | PVS cull only sets `Visible`; Godot doesn't gate `_Process` on it, so culled ents still pay full sync (+morph). AND the existing `_pvsVisible` flag into the R1 loop. | `game/client/ClientWorld.cs:978-1018`; `game/EntityNode.cs:54`; `game/client/ModelAnimator.cs:255,515` | Converts cull from GPU-only to CPU-submission win; scales with culled-frac × ent-count × fps | M | med | render-submission |
| 3 | **`sv_threaded` listen-server default-on (move 72Hz tick off render thread)** | Built + verified-clean, default-off. Combat ticks 15–25ms run in the render budget today. Fix gate-span overlap, then default on for listen server. | `src/XonoticGodot.Server` (ServerThread/`_simGate`); `NetGame._Process` | Removes a whole 72Hz workload from the render thread; hitch/throughput lever, not idle floor | M–L | med | threading |
| 4 | **Memoize `MovementParameters.FromCvars` struct (invalidate on MoveVarsBlock)** | ~90 dict ops/call × fps × replay depth (~700–900 calls/s). Memoize base struct; fold in redundant `nudgeoutofsolid` read (line 207). | `src/XonoticGodot.Common/Physics/MovementParameters.cs:143-207`; `PlayerPhysics.cs:132,152`; `game/net/ClientNet.cs:807` | ~65–80k redundant lookups/s removed; scales fps × depth (worst at 200+fps) | M | med | net/prediction (cvar) |
| 5 | **HUD: `NeedsRedraw` gate on HealthArmor/Ammo/Weapons/Powerups + Crosshair** | All default `NeedsRedraw()=>true` → force-redrawn 228×/s; crosshair computes a `dirty` flag then discards it. Override with snapshot gate (TimerPanel pattern); hoist crosshair `dirty` to a field. | `game/hud/HudManager.cs:249`; `CrosshairPanel.cs:421,500,544`; `HealthArmorPanel.cs:95`; `AmmoPanel.cs`; `WeaponsPanel.cs:133`; `PowerupsPanel.cs:162` | Biggest HUD canvas-re-record + alloc saving; ~150+ redraws/s eliminated in steady aim, scales with fps | S–M | med | hud/canvas |
| 6 | **BIH for static-world collision (replace flat XY-grid `_outside` scan)** | Long rays scan whole `_outside` list; BIH → flat vs brush count. | `src/XonoticGodot.Engine/Collision/Brush.cs:489-546` | **Measured** ~2.06ms→~0.05–0.1ms long traces (~20–40×); roots out R7 | M | med | spatial/collision |
| 7 | **Entity object pool (recycle the object, not just the slot — DP edict pool)** | Slot recycled but object re-`new`'d; 68 spawn sites. Pool the Entity object. | `src/XonoticGodot.Engine/Simulation/EngineServices.cs:90` | Removes dominant per-combat-frame alloc; shrinks 50–140ms gib/projectile GC spikes (hitch, not steady) | M | med | gc/alloc |
| 8 | **HUD: skip LoadConfig+QueueRedraw when scoreboard up (panelFade≈0)** | Non-WithScoreboard bar/number panels redraw fully-transparent canvas while scoreboard held. Skip when effective fade≈0 — **exclude CrosshairPanel** (draws with own `crosshair_alpha`). | `game/hud/HudManager.cs:240,243,249`; `HudPanel.cs:234` | Eliminates whole dynamic-HUD redraw during a common state (Tab/dead/intermission); scales with fps × panels | S | low | hud/canvas |
| 9 | **Avoid second full replay per snapshot frame (Reconcile then SendInput→Predict)** | Poll→Reconcile replays the window, then SendInput→Predict replays it again same frame. Seed Predict from Reconcile's Predicted; apply only the new command when ackedSeq didn't advance. | `game/net/ClientNet.cs:447,915`; `PredictionBuffer.cs:258,340` | Halves replay cost on ~1/3 of frames; **needs carrier hidden-state (crouch/water/jump-debounce) cached** | M | med | net/prediction |
| 10 | **Per-particle PointContents: BSP leaf descent / accel structure (fix at R6)** | Each blood/bubble/rain/snow particle calls `CollisionWorld.Query` which scans full `_outside` per frame vs DP's leaf descent. | `ParticleSim.cs:601,715,721,727,740`; `TraceService.cs:189-233`; `Brush.cs:489-546` | O(live × |_outside|) per frame; combat blood spray = hundreds of queries/frame × fps; **subsumed by R6** | M | med | particles / collision |
| 11 | **`SimulationLoop.IsClient` → inline flag test (drop O(E×C) scan)** | Flag check exists (line 256) but falls through to a full `Clients` scan for every non-player entity (lines 259-260) — provably dead (flag set with list insert at `ClientManager.cs:164`). | `src/XonoticGodot.Engine/Simulation/SimulationLoop.cs:242,254-262`; `ClientManager.cs:164` | E×C → E flag-tests in hottest server loop; bounded by C (small); pure-waste, zero risk | S | low | server tick |
| 12 | **MovementParameters per-tick caller → use mp.NudgeOutOfSolid (fold into R4)** | `PlayerPhysics.Move:152` re-reads `sv_gameplayfix_nudgeoutofsolid` via GetString though `mp` already holds it (line 207). | `PlayerPhysics.cs:152`; `MovementParameters.cs:101,207` | One dict+strcmp per replayed tick removed; folds into R4 | S | low | net/prediction |
| 13 | **Combine Position+Rotation/Basis into one Transform3D write** | EntityNode/vehicle/camera set Position then Rotation as two marshalled property sets each re-dirtying the transform. One `Transform3D=new(basis,origin)`. | `game/EntityNode.cs:81-109`; `ClientWorld.cs:1311-1312`; `FirstPersonView.cs:240-250` | Halves transform-write crossings/entity/frame; composes with R1 | S | low | interop |
| 14 | **Parallelize per-player bone-pose synthesis (FromFrames), keep interop serial** | `cw.players` serial foreach; `PlayerSkeleton.FromFrames` is pure-C# bone math (no Godot calls) → `Parallel.For`; `PushBones` interop stays on main. | `game/client/ClientWorld.cs:929-939`; `PlayerModel.cs:155-185,200-234`; `PlayerSkeleton.cs:114-171` | Cuts synthesis ~by core count in 8–16p matches; **bounded by synthesis-vs-interop ratio — measure split first** | M | med | threading |
| 15 | **HUD CvarF double-lookup + cached per-panel cvar struct** | `CvarF` interpolates the key string twice + 2 dict probes/call; cache via `Changed` event (EnsureCvarCache pattern). | `game/hud/HudPanel.cs:284,287-291`; `CrosshairPanel.cs:386-407` | ~thousands of transient key allocs/s → gen0 churn; **largely subsumed by R5/R8** on idle frames; residual = LoadConfig + hot-redraw frames | M | med | gc/alloc / hud |
| 16 | **Physics/StrafeHud `NeedsRedraw` (quantized speed/accel/angle)** | Both `QueueRedraw()` unconditionally despite a 64Hz `update_interval`; default Race/CTS show-mode 3 (this port's target gametype). | `PhysicsPanel.cs:164,219,434`; `StrafeHudPanel.cs:110` | ~halves redraws of two large panels in the strafe gametype; StrafeHud lower-confidence (mouse-tracked bar) | M | med | hud/canvas |
| 17 | **ClientWorld.CvarF single-lookup + cache cl_force* family** | `CvarF` does GetString+GetFloat (2 probes); `ResolveForcedColormap` calls it 4× per player-model/frame. | `game/client/ClientWorld.cs:1253,1286-1291` | ~8 probes/player-model/frame removed; scales player count × fps; modest | S | low | interop / cvar |
| 18 | **Cache `GetViewport().GetCamera3D()` once per ClientWorld._Process** | Camera re-resolved 4× in one `_Process` (ListenerPos ×2, ViewOrigin ×2). | `ClientWorld.cs:659-665,928,957,993,1032` | ~6 crossings/frame removed; **cross-node refetches (particle/weather) are out of reach of this cache** | S | low | interop |
| 19 | **Gibs/casings → MultiMesh batches (one MultimeshSetBuffer each)** | ~128 Node3D+MeshInstance3D, each with `_PhysicsProcess` + ApplyAlpha tree-walk → 2 MultiMesh draws. | `ModelGibs.cs:84,201`; `ShellCasings.cs:57,193`; `FaithfulParticleRenderer.cs:660` | Combat-peak/hitch lever (transient ents), not idle ceiling; **verify loaded-MD3 material-sharing + per-instance fade first** | L | med | render-submission |
| 20 | **HUD DrawText shadow-collapse (font outline instead of 2nd DrawString)** | Every string emits shadow+main = 2 DrawString crossings. Use FontVariation outline. | `game/hud/HudPanel.cs:399-423` | Halves DrawString crossings on redrawing frames; **mostly subsumed by R5/R8 gating** | M | med | hud / interop |
| 21 | **Particle render-sync: SoA / compact live-index (kills dead-slot scan + AoS cache pulls)** | ~128B AoS struct streamed twice/frame; cull scans dead slots to HighWater reading ~7 hot fields. | `Particle.cs:13-53`; `ParticleSim.cs:580-755,756-759`; `FaithfulParticleRenderer.cs:477-505,561-653` | Memory-bandwidth on render thread; **verify bandwidth-bound before investing**; merges with the dead-slot-scan item | L | med | particles |
| 22 | **Parallelize particle bounce-trace loop (per-thread RNG + deferred decals)** | Flat per-particle loop vs read-only static tracer; but shared `_freeParticle`/`live`/`Rng`/`OnStain` need per-thread RNG + decal deferral to stay faithful. | `ParticleSim.cs:560-637,580-602`; `FaithfulParticleBackend.cs` | ~1–2ms steady, spikes on volleys; faithfulness cost is real — lower priority than R6 fix | L | med | threading / particles |
| 23 | **MultiMesh upload: marshal only n×stride, not full capacity** | After a burst the capacity-sized float[] (e.g. 160KB) re-marshals every frame for ~10s even at a handful live. | `FaithfulParticleRenderer.cs:531-559,655,660` | Steady ~capacity×80B/batch/frame for the 10s decay tail; respect the buffer-length==InstanceCount×stride contract | M | med | particles |
| 24 | **Particle pack-loop: `Dictionary<int,int>` slot → flat int[96] table** | `_slotOf.TryGetValue(p.TexNum)` per particle/frame; keys 0..95, TexNum is a byte → array index. | `FaithfulParticleRenderer.cs:86,198-242,566` | A few-thousand hashed lookups/frame → branchless index; modest pure win | S | low | particles |
| 25 | **FaithfulParticleBackend: guard camera fetch on HighWater>0** | Camera fetch + 2 Quake conversions run every frame even with zero particles. | `FaithfulParticleBackend.cs:335-372`; `FaithfulParticleRenderer.cs:448-456` | Tiny idle-path saving; cheapest/lowest-risk of the particle set | S | low | particles |
| 26 | **ServerNet.RelevantEntitiesFor: int-key list instead of ~130B struct copy** | foreach copies ~130B `NetEntityState` by value per recipient per tick (+ baseline-retain loop, unconditional). | `game/net/ServerNet.cs:1555,1787`; `SnapshotDelta.cs:104`; `NetEntity.cs:92` | 72Hz server-tick, off the render critical path — in-process headroom only, not the fps ceiling | M | med | server tick |
| 27 | **PlayerFrameLogic g_balance_* cvar caching per tick** | ~20 string cvar lookups per player every 72Hz tick. | `src/XonoticGodot.Engine/ (PlayerFrameLogic)` | O(players); server-tick | S | low | server tick / cvar |
| 28 | **Precompute ServerNet.Classify class tag at spawn** | Lower-cased string alloc + StartsWith/Contains per non-player entity per broadcast. | `game/net/ServerNet.cs (Classify)` | O(entities); server-tick alloc removal | S | low | server tick |
| 29 | **Migrate per-tick FindByClass callers to alloc-free List overload (exists)** | Iterator allocs + O(n) scans on StartFrame mutator path. | `EngineServices.cs:192-204` | Low but free | S | low | server tick |
| 30 | **Engine config: gibs/casings off Godot physics; lower physics_ticks; cap max_physics_steps_per_frame** | 60Hz Godot physics loop serves only ≤128 cosmetic nodes; hitch-amplification footgun. | `project.godot`; `ModelGibs.cs:180`; `ShellCasings.cs:181` | Small fixed tax + removes hitch amplifier | S | low | engine config |

---

## 3. Grouped Detail by Theme

### A. Render-submission / scene-tree (the rcpu core — items 1, 2, 13, 19)
The dominant gap. DP draws dynamic entities with one flat `for i in scene.numentities` loop and an indirect `Draw(ent)` call; we run **one Godot Node3D with its own `_Process` per entity**.

- **R1 — central drive loop + dirty-gate.** `EntityNode._Process` (`EntityNode.cs:54`) calls `SyncFromEntity` which unconditionally writes `Position` (line 81) and `Rotation`/`Basis` (101/95). There is **no** `lastOrigin`/`lastYaw` compare and **no** `SetProcess(false)` anywhere (verified). Fix: in `ClientWorld._Process`, drive `SyncFromEntity` from the **already-existing** `_entityNodes` loop (`ClientWorld.cs:1001`), call `SetProcess(false)` on each EntityNode, and skip the write when `Origin==lastOrigin && yaw==lastYaw && ItemAnimate==0` and no pitch/roll. Keep `ItemAnimate!=0` (bobbing pickups) and pitched/rolled props writing every frame. Do the same for `ModelAnimator` — note the existing `_animators` loop (909-917) does **not** call `Advance()` today, so the collapse must relocate the `Advance()` call there.
- **R2 — submission-gate the PVS cull.** `ApplyEntityPvsCull` (`ClientWorld.cs:978-1018`, verified) only sets `SetPvsVisible`. AND the `_pvsVisible` flag into the R1 loop so culled entities skip sync entirely. The morph-skip half is narrower than first stated (`cl_gpu_morph` default-on), but the transform-sync skip applies to **all** culled entities.
- **R13** — collapse the two property sets into one `Transform3D` write; do it inside the R1 loop.
- **R19** — gibs/casings to MultiMesh: real but transient-combat-only; verify the **loaded-MD3 single-surface/material-sharing** assumption and the per-instance alpha fade before promising 128→2 draws.

### B. Interop tax (items 13, 17, 18)
Each managed↔native property set / `GetCamera3D()` is a marshalled crossing. **R18**: cache the camera once at the top of `ClientWorld._Process` (resolved 4× today: `ClientWorld.cs:928,957,993,1032`) — but note FaithfulParticleBackend/Weather/SpawnPoint are **separate nodes** and out of reach of this cache. **R17**: `CvarF` (`ClientWorld.cs:1286`) does GetString+GetFloat; `ResolveForcedColormap` calls it 4× per player-model/frame though forced-colors state never changes — single-lookup it and cache the `cl_force*` family off the `Changed` event.

### C. Threading / `sv_threaded` (items 3, 14, 22)
**R3 is the headline listen-server lever.** `sv_threaded` is **built and verified-clean, default-off**. On the listen server the 72Hz sim runs in the render budget; combat ticks are **15–25ms** (verified). Move it off the render thread. Honest caveats from the recovered audit: the main thread holds `_simGate` across ~95% of `NetGame._Process` (poor overlap — scoreboard/info-messages/appearance/mutator-drains also read server state inside the span, so the "only prediction needs the gate" claim is partly wrong); the `ServerThread` fixed-sleep pacing doesn't subtract tick cost (bounded to MaxTicksPerFrame=3, not unbounded); and the co-claimed "ClientWorld race" blocker was **disproven** (ClientWorld reads network-snapshot proxy entities, never server-world entities). So: it is a **hitch/dropped-frame lever for populated combat, not the idle steady-state floor** — but it is the single highest-leverage config change for the listen server. **R14**: per-player `FromFrames` is verified pure-C# and parallelizable (interop `PushBones` stays serial) — **measure the synthesis-vs-interop split first**; the win is bounded by that ratio.

### D. Spatial / collision — BIH (items 6, 10)
**R6** is the one true algorithmic gap DP has and we don't. `Brush.cs:489` (`CollisionWorld.Query`) scans the entire `_outside` list on long rays. BIH makes traces flat vs brush count — **measured** ~2.06ms→~0.05–0.1ms (~20–40×) on the catharsis fix. **R10** (per-particle point-contents, `ParticleSim.cs:715-740`) is **the same root cause** — DP does a BSP leaf descent (`CL_PointSuperContents`), we scan `_outside` per particle per frame. Fix R6 and R10 mostly resolves.

### E. Server tick (items 11, 26, 27, 28, 29, 30)
72Hz, in-process on the listen server (so it *is* in the render budget, but it's the simulation half). **R11** is the cleanest: `IsClient` (`SimulationLoop.cs:254-262`) checks the flag (line 256) then falls through to a full `Clients` scan (259-260) for every non-player entity — **provably dead** since `ClientManager.RegisterEntity` sets the flag with the list insert (`ClientManager.cs:164`). Replace the call at line 242 with `(e.Flags & EntFlags.Client) != 0`. Zero risk, byte-equivalent. **R26** (RelevantEntitiesFor ~130B struct copies) is real but **off the render critical path** — in-process headroom, not the fps ceiling — rank it below the interop items.

### F. GC / alloc + Entity pool (items 7, 15)
**R7** (Entity object pool, `EngineServices.cs:90`): the slot is recycled but the object re-`new`'d at 68 spawn sites — this is the dominant per-combat-frame alloc and the source of 50–140ms gib/projectile GC spikes. It's a **hitch lever** (spikes), complementary to the steady-state items. **R15** (HUD CvarF transient key allocs) is **largely subsumed** by the redraw gates (R5/R8) — its standalone residual is the per-frame `LoadConfig` path and hot-redraw frames; keep as cleanup.

### G. HUD / canvas (items 5, 8, 15, 16, 20)
All four always-on combat panels + crosshair force-redraw at full fps. **R5**: override `NeedsRedraw()` with an alloc-free snapshot (the `TimerPanel.cs:215` pattern). For the crosshair, hoist the already-computed `dirty` local to a field and return it. **Nuance**: HealthArmor/Powerups/Weapons have genuine time-animated effects (low-health pulse, ghost-bar fade, countdowns, selection slide) — the gate must include "is an effect window active", so the idle-frame win is largest for Ammo/Weapons, partial for HealthArmor/Powerups. **R8**: skip non-WithScoreboard bar/number panels when scoreboard up (`panelFade≈0`) — **must exclude CrosshairPanel** (it draws with its own `crosshair_alpha` and stays visible). **R16**: Physics/StrafeHud gate on quantized speed/accel/angle (StrafeHud lower-confidence — mouse-tracked bar).

### H. Particles (items 10, 21, 22, 23, 24, 25)
Mostly steady taxes on combat-heavy frames. **R10/R6** is the big one. The rest (SoA split R21, parallelize R22, n×stride upload R23, flat-table slot R24, idle camera guard R25) are smaller; **R24 and R25 are cheap pure wins**, R21/R22/R23 are larger and should be **profiler-gated** (confirm bandwidth-bound / volley-frequency before investing). R21 and the dead-slot-scan item share the same compaction fix — don't double-count.

### I. Net / prediction (items 4, 9, 12)
**R4** is the strongest net item (see Executive Summary). **R9** (avoid the second replay per snapshot frame) is real but **needs the carrier's hidden cross-tick state** (crouch hull, water level, jump-debounce, FixAngle) cached/restored — non-trivial; overlaps R4's incremental-replay insight. **R12** folds into R4.

### J. Engine config (item 30)
Move gibs/casings off Godot's 60Hz physics, lower `physics_ticks_per_second`, cap `max_physics_steps_per_frame`. Small fixed tax + removes a hitch-amplification footgun. (Note: Godot strips `project.godot` comments on re-serialise — per memory.)

---

## 4. DarkPlaces Structural Gap

**What DP does that we structurally do NOT:**
- **Flat batched immediate renderer, no scene tree.** DP draws dynamic entities via one `for i in scene.numentities` loop + indirect `Draw(ent)` (`gl_rmain.c:4078`); animated meshes share a per-frame `R_AnimCache`. We pay one Godot Node3D + `_Process` marshalled callback **per entity** (EntityNode/GibBody/CasingBody each override `_Process` individually — 21 `_Process` overrides under `game/client`). **This is the 228→300 rcpu gap** and is exactly what R1/R2/R19 attack. Portability: moderate.
- **BIH static-world collision** vs our flat XY grid (R6). The top algorithmic gap.
- **Edict object pool** — DP recycles the edict object; we recycle the slot but re-`new` the object (R7). The top GC gap.

**What we already adopted (do NOT re-flag):**
- Entity area-grid broadphase (`EntityAreaGrid.cs`), flat dense entity array + indexed iteration (`SimulationLoop.cs:233`), think/nextthink scheduling (`SimulationLoop.RunThink`).
- **The particle renderer is already a near-exact port of DP's `R_DrawParticles`** — cull→sort→pack one buffer→one `MultimeshSetBuffer` per (blend) batch, two MultiMeshInstance3D for all particles. This is the **proven pattern to copy** for gibs/casings/bolts (R19), not something to redo.
- World draw-call count is already near-minimal: `MapLoader.BuildMap` groups faces per (texture,lightmap), merges lightmaps onto one atlas, shares one Material per cell — the structural equivalent of DP's load-time `Mod_MakeSortedSurfaces`.
- Render-state caching and dynamic-buffer ring are **owned by Godot's Vulkan RenderingDevice** — not portable and not needed.

**Where we BEAT DP (do not regress):** native compiled-C# gameplay vs DP's QC PRVM interpreter — DP's single biggest per-tick cost. Our `sim.integrate` is already faster per-tick; the lever is render/interop, not sim logic.

**Which gaps explain the ~30% delta:** primarily the **per-node scene-tree submission tax** (R1/R2) and the **server tick sharing the render thread** (R3 on the listen server), with **BIH** (R6) the algorithmic tail and the **per-frame cvar/replay churn** (R4) the high-fps multiplier.

---

## 5. Quick Wins vs Deep Rewrites (ordered)

**Quick wins (S, low risk — do first):**
1. **R11** — `IsClient` inline flag test (drop O(E×C) scan). Byte-equivalent, zero risk.
2. **R12** — caller reads `mp.NudgeOutOfSolid` (folds into R4).
3. **R8** — skip dynamic panels when scoreboard up (exclude crosshair).
4. **R18** — cache camera once per `ClientWorld._Process`.
5. **R24/R25** — particle flat-table slot lookup + idle camera guard.
6. **R13** — one `Transform3D` write (best landed *with* R1).
7. **R30** — engine config (gibs/casings off Godot physics).

**Medium:**
8. **R5** — HUD NeedsRedraw gates (crosshair `dirty` hoist + four panels with effect-window awareness).
9. **R4** — memoize MovementParameters (invalidate on `MoveVarsBlock.Apply`).
10. **R1+R2** — central `_Process` loop + dirty-gate + PVS submission gate (do together).
11. **R17** — CvarF single-lookup + cl_force* cache.

**Deep rewrites (L / measure-gated):**
12. **R6** — BIH (also fixes R10).
13. **R3** — `sv_threaded` default-on for listen server (fix gate-span overlap first).
14. **R7** — Entity object pool (68 spawn sites).
15. **R19** — gibs/casings MultiMesh (verify material-sharing/fade).
16. **R9** — incremental replay (needs carrier hidden-state).
17. **R14/R21/R22/R23** — parallelize bone-pose / SoA particles / parallelize bounce-trace / n×stride upload — **profile before investing**.

---

## 6. What NOT to Do / Already Done

- **Don't re-flag** EntityAreaGrid, dense entity array, think-scheduling, the particle renderer's batched upload, the world lightmap-atlas/material grouping, or Godot's render-state cache — all present/owned by Godot.
- **Don't re-implement a GL state cache** on top of RenderingServer — redundant (Godot's Vulkan backend dedupes binds and sorts opaque draws internally).
- **Don't gate CrosshairPanel** in the scoreboard-up skip (R8) — it draws with its own `crosshair_alpha`, would blank incorrectly.
- **Don't chase the disproven items:** the "ClientWorld reads server entities" race (it reads network-snapshot proxies — disproven), and `SnapshotInterpolation.Sample` 4× recompute (the `f=1` endpoint short-circuits the trig — no consumer samples at an interpolated time).
- **Don't treat as steady-state:** ModIconsPanel hidden-redraw (no-op draw-wise), RadarPanel per-frame LINQ alloc (one-time GC-pause class, fold into a broader radar cleanup), the `SendInput` idle-redraw gate (covered automatically by R9's incremental replay).
- **Don't promise "by core count"** on R14/R22 without measuring the synthesis-vs-interop and trace-vs-RNG split first.
- **Don't expect R26/R27/R28** (server-tick) to move the render-bound fps ceiling — they buy in-process headroom only.
- **Don't double-count** R21 and the dead-slot-scan item (same compaction fix), or R10 and R6 (same BIH root).

---

## 7. Recommended Verification

**Split the cost first — confirm rcpu vs proc:** the whole plan rests on "CPU-bound, GPU idle, rcpu-dominated." Use the FrameProfiler:
- `cl_frameprofiler 1` — confirm the per-section breakdown; watch the `entitynode` scope (`EntityNode.cs:56`) and `particles.cpu` (`FaithfulParticleBackend.cs:339-355`). The `entitynode` aggregate scope is itself per-node overhead the R1 consolidation removes — its total is a direct proxy for the R1/R2 win.
- Confirm `proc` ≈ 0.9ms (interop) vs `rcpu` (submission) at 228fps before/after each render-frame change.
- `--gpu-profile` — verify the GPU stays idle (the premise); if a change makes the GPU the new bottleneck, stop.

**Per-item verification:**
- **R1/R2**: A/B the `entitynode` scope total and frame time on catharsis with ~150 entities, camera looking at a wall (max PVS cull). The dirty-gate win shows as `entitynode` dropping when standing still.
- **R3**: `sv_threaded 1` on a populated listen server; watch combat-tick frames (15–25ms) move off the render thread — measure 1%-low fps, not just average.
- **R4**: `dotnet-trace` (sampled) on the predict/replay path; count `FromCvars`/`GetFloat` call frequency before/after. Should scale visibly with `cl_maxfps`.
- **R5/R8/R16**: count HUD `_Draw` invocations/sec (instrument `QueueRedraw` or read Godot's `RenderingServer` 2D draw counters) standing still vs in a firefight vs scoreboard-up.
- **R6**: micro-benchmark long traces on catharsis (the prior fix measured ~2.06ms→~0.05–0.1ms — reproduce); confirm flat vs brush count.
- **R21/R22/R23**: `dotnet-trace` to confirm particle cost is bandwidth/CPU-bound and that HighWater stays pinned high post-burst **before** committing to the SoA/parallel rewrites.

**General discipline:** release build only, fixed `cl_maxfps` (uncapped = pathological present pacing per memory), same catharsis spawn + bot count, compare 1%-low and average. Several magnitudes above are **estimates** — only R6 and the original 68→228 catharsis numbers are measured. Measure before/after each landed item.