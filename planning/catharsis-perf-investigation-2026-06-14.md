# Catharsis Performance Investigation — XonoticGodot

**Date:** 2026-06-14
**Hardware:** NVIDIA RTX 3080, 24-core CPU, native res, vsync mailbox, `cl_maxfps 0`
**Symptom (reported):** catharsis ran at 30–60 fps (vs 300+ in Darkplaces on the same map/HW), *worst while
standing at the central spawn*; "not sure if shaders, map size, or something else."
**Method:** extended the in-tree FrameProfiler, then ran a measurement-first A/B campaign (release export +
Debug attribution) plus two parallel code-analysis workflows. Every claim below is backed by a measured
profiler capture (`~/XonData/logs/session-*.log`) or a `file:line` read.

---

## 0. Executive summary

**The map was never GPU-bound and the slowness had almost nothing to do with shaders, MSAA, lighting, the
map's size, or draw calls.** On the RTX 3080 the GPU sat at **1–6 ms/frame with ~600 draws** — nearly idle.
The entire ceiling was **~6 ms/frame of CPU** in one place:

> **`CrosshairPanel` fired two full-length world traces *every frame* for the cosmetic "true-aim" crosshair
> tint, and the collision broadphase brute-forced every brush in the map for those long rays.** On catharsis
> (a brush-dense map) each trace was ~3 ms → ~6 ms/frame of pure CPU, capping a 144 Hz-capable machine at
> ~80 fps. Small maps (stormkeep) hid it because their traces are cheap (~0.9 ms). It got *worse the more
> geometry the forward ray crossed* — which is exactly why it felt worst at the open central spawn.

Two fixes landed, both verified (2755/2755 tests pass, release re-export measured):

| Catharsis, RTX 3080, **release** | avg fps | median frame | `proc` (CPU/frame) | 1%-low fps |
|---|---|---|---|---|
| **Before** (bots on, default) | **68** | 12.9 ms | 8.6 ms | 23 |
| **After both fixes** (bots on) | **228** | 3.9 ms | ~0.9 ms | 157 |

**A 3.4× FPS improvement**, now in Darkplaces' ballpark. The GPU was never the problem.

---

## 1. The decisive finding: CPU-bound, not GPU-bound

The first catharsis capture settled the whole question:

```
proc 8.6 ms   gpu 1.7 ms   rcpu ~1 ms   draws ~595   vram 3.5 GB
```

`proc` (all C# `_Process` CPU) dominated; `gpu` (RTX 3080 viewport time) was idle. A fast scene (the main
menu) showed `proc 0.1 ms`. So the catharsis cost lived **on the CPU main thread and scaled with the map**.
Within `proc`, a single bucket dominated and was **unscoped** — `proc:other ≈ 5–6 ms every frame** — invisible
to the existing profiler (the sampling watchdog only reported `(unscoped)`).

**Implication that reframed the whole investigation:** because the GPU was idle, any "optimization" that trades
CPU to reduce draw calls (occlusion culling, finer PVS, more cells) is *net-negative* here. Confirmed below.

---

## 2. How `proc:other` was attributed (the elimination)

I extended the FrameProfiler (see §6) to emit a steady-state per-scope breakdown, then ran controlled A/Bs
(Debug, bots off unless noted, standing at the spawn). Each ruled a suspect **out**:

| Experiment | `proc:other` | Verdict |
|---|---|---|
| Baseline catharsis | **6.3 ms** | the floor |
| All culling off (`r_pvs_cull 0 r_pvs_cull_entities 0 r_occlusion_cull 0`) | ~6.2 ms | **not culling** (and culling is net-neutral here — GPU idle) |
| `cl_particles 0` (+ map emitters) | 6.2 ms | **not particles** |
| Bots off (170 → 30 entities) | 6.3 ms | **not entities/bots** — constant regardless of entity count |
| `cl_idle_warmup 0` | 6.2 ms | **not the idle warmer** |
| One big world mesh (`r_world_cell_size 131072` → 8 cells, draws 584→439) | 6.2 ms | **not the cell split / scene-node count / draw count** |
| stormkeep (same engine, smaller map) | **1.8 ms** | scales with **map geometry**, not nodes (both ~2800 nodes) |
| Main menu, no map loaded | **0.1 ms** | the cost appears **only in-match** |
| **`crosshair_enabled 0`** | **0.5 ms** | **★ the entire floor is the crosshair** |

The node-type census also disproved tempting theories: the scene is ~2000 *hidden menu UI* nodes (Label 861,
HBox 210, Button 110, …) that **don't `_Process`** and cost nothing; no AnimationPlayer / AudioStreamPlayer3D /
GpuParticles3D type even reached the top-10. So it was neither the scene-graph size, animation, nor audio.

`crosshair_enabled 0` collapsed `proc:other` 6.2 → 0.5 ms and took the map from ~90 fps to ~244 fps (Debug) in
one toggle. **Root cause located.**

---

## 3. Root cause (two layers)

### 3a. `CrosshairPanel` traces the world twice per frame (`game/hud/CrosshairPanel.cs`)

`CrosshairPanel._Process` → `ComputeShotType()` runs the QC `TrueAimCheck` **every frame** to color the
crosshair (hit-world / hit-enemy / obstruction). It issues two `Api.Trace.Trace` calls:

1. **A full `max_shot_distance` ray**: `origin → origin + forward * 32768` (`CrosshairPanel.cs:1028`,
   `WeaponFiring.MaxShotDistance = 32768`).
2. A box trace to the resulting aim point.

QC does this per frame too — but DP's world raycast is ~microseconds. Ours was ~3 ms each on catharsis.

### 3b. Why one world raycast was ~3 ms — the collision broadphase brute-forces long rays (`src/XonoticGodot.Engine/Collision/Brush.cs`)

The world collision has a **128×128 XY broadphase grid sized to the world bounds** (`Brush.cs`
`CollisionWorld`). But `Query` had a trap: when a query AABB **spills** the grid extent, it fell back to a
**full brute-force scan over every brush in the map** (`Brush.cs:519-530`, the `outside` branch). The
crosshair's 32768-qu ray produces a swept AABB far larger than any map's extent, so it **always** tripped the
full scan → the trace was **O(total brushes)**. catharsis simply has many more brushes than stormkeep — hence
~3 ms vs ~0.9 ms, and "slower the more you can see." This penalty also hit **weapon hitscan, AI
line-of-sight, splash/`FindInRadius`, and particle bounces** — any long ray.

(Allocation is *not* a factor — the trace box is cached and the crosshair ray is a zero-box point trace; the
~3 ms is pure CPU. The areagrid landed earlier accelerates *entity* clipping, not the world brush sweep.)

---

## 4. Fixes landed (both verified, 2755/2755 tests pass)

### Fix 1 — broadphase: scan the clamped grid range, not the whole map (`Brush.cs`) — **the real cure**

When a query spills the grid, scan the **clamped** grid cells plus the always-scanned `_outside` list instead
of brute-forcing every brush. This is **candidate-identical** (provably the same brush set, so bit-identical
trace results): every brush is either in `_outside` (it spilled the grid at link time) or linked to *all*
in-grid cells it overlaps, so a brush overlapping the query is always found by the clamped scan. `GridRange`
now always returns the clamped range. Verified by the `BspCollision` + differential trace suites (115
collision/trace tests + full 2755 green).

- **Effect:** a full-length world trace dropped from ~3 ms to negligible. Measured: with this fix and the
  crosshair tracing **every frame** (no throttle), `proc:other` = **0.1 ms**, **227 fps**.
- **Benefit is global:** every long ray (hitscan, AI LOS, splash, particle bounces) is now grid-accelerated,
  not just the crosshair.

### Fix 2 — crosshair true-aim cache (`CrosshairPanel.cs`) — cheap, always-correct guard

Skip the two traces when the aim ray is effectively unchanged since the last trace (a static aim ray cannot
change the classification) — so a *standing* player (the reported worst case) does **zero** true-aim traces.
An optional `cl_crosshair_trueaim_rate` cvar can additionally cap the re-trace rate while turning on
pathologically dense future content; it **defaults to 0 (off)** because Fix 1 already made per-frame tracing
cheap, preserving the faithful QC per-frame cadence. Also added a permanent `hud.trueaim` profiler scope so
this can never hide in `proc:other` again.

**Combined result (release, catharsis, bots on):** `proc:other` 0.1 ms, **228 fps avg, 157 fps 1%-low,
3.9 ms median** (was 68 / 23 / 12.9).

> Note: Fix 1 alone achieves the full win and is the root-cause fix. Fix 2 is defense-in-depth + the
> standing-still optimization; both are low-risk and faithful.

---

## 5. Secondary findings (not the FPS ceiling, but real)

These are *separate* from the steady-state floor and affect the *played* (bots + combat) experience:

1. **Bot/combat hitches.** With bots, `server.tick → sim.move → move.post` spikes to 15–25 ms intermittently
   (weapon-fire `WeaponThink`, status-effect ticks), and ~140 bot-related entities stream models continuously
   (`iqm.anims` churn, ASSET-BUILD hitches, the 3.5 GB VRAM and gen2 GC pauses during the first ~20 s). These
   are the *hitch* class the earlier PERFORMANCE_REPORT waves targeted; the **steady FPS floor fixed here is
   independent of them.** The `move.post` spike now has `mp.fx`/`mp.weapon` sub-scopes for follow-up.
2. **Culling is net-neutral-to-negative on this hardware.** Disabling all culling barely changed frame time
   (GPU idle). The per-cell world split and PVS culling cost CPU to save draws the 3080 doesn't need saved —
   keep them for weaker GPUs, but they are not a win here. Raising `r_world_cell_size` (fewer cells) is a free
   knob if scene-node overhead ever matters.
3. **VRAM ~3.5 GB on catharsis** (vs 156 MB at menu); heap climbs to ~1.9 GB with bots. Caches never evict
   across the session (PERFORMANCE_REPORT backlog #9). Not the FPS cause, but a long-session pressure risk.
4. **The full settings-menu tree (~2000 Control nodes) stays instantiated during the match.** Harmless to
   `proc` (they don't `_Process`), but it inflates the node count and is worth freeing on match start.

---

## 6. Profiler extensions added (reusable instrumentation)

To make this and future investigations measurable (the old profiler dumped a scope tree only on a *hitch*,
so a steady-state floor was invisible):

- **Steady-state per-scope dump** — `EmitSnapshot` now prints `ms/frame:` (top scopes as per-frame averages)
  every 5 s, so any two experiments are directly comparable without provoking a hitch.
- **Scene-complexity + node-type census** — `scene: nodes/objs-drawn/draws/vram` and a `census:` of node types
  with processing counts (this is what disproved the scene-graph/animation/audio theories).
- **New permanent scopes** so `proc:other` is attributable: `cev.process` (ClientEntityView), `world.pvscull`,
  `emitters`, `clientmisc` (WorldOcclusion/SpawnPoint/Laser/Vignette), `hud.trueaim`, and `mp.fx`/`mp.weapon`
  sub-scopes in `GameWorld.OnPlayerPostThink`. A `remote.ents` marker surfaces the entity count.

A reusable capture harness lives at `_scratch/perf-run.sh`.

---

## 7. Recommended further changes (ranked)

| Pri | Change | Why | Effort / Risk |
|---|---|---|---|
| **P1 (done)** | Broadphase clamped-scan (Fix 1) + crosshair cache (Fix 2) | the FPS ceiling | landed, verified |
| **P2** | **Port DP's BSP-tree world line-trace** (`Mod_Q3BSP_TraceLine`) into `CollisionWorld` | Fix 1 makes long rays grid-accelerated; a true BSP-tree descent would make them ~20–50× (visit only the leaves the ray crosses), helping hitscan/AI/particles further. `BspPvs` already parses the tree; keep `TraceBrushVsBrush` byte-identical — only candidate gathering changes. Verify with a grid-vs-tree differential trace test. | Med / Med (parity-gated) |
| **P2** | **`AnimationLibrary` cache per (model, skin)** + pooled parse/decode buffers | kills the bot-join `iqm.anims` churn, the ASSET-BUILD hitches, and most of the 3.5 GB VRAM / gen2 pauses (PERFORMANCE_REPORT backlog #1/#2) | Med / Low |
| **P3** | Free the settings-menu node tree on match start (rebuild on return) | removes ~2000 idle nodes from the in-match tree | Low / Low |
| **P3** | Map-scoped cache eviction (textures/materials/anims) | bounds long-session VRAM growth (backlog #9) | Med / Low |
| **P3** | Consider lowering default culling on strong GPUs (or expose a quality tier) | culling is net-neutral here; it's pure CPU spent for no GPU benefit on a 3080 | Low / Low |

The earlier "graphics settings" levers (MSAA/glow/shadows/render-scale, the dead quality cvars in
`graphics-settings-audit-2026-06-14.md`) are **not** relevant to catharsis FPS — the GPU is idle. They matter
only for GPU-bound scenarios / weaker hardware.

---

## 8. Files changed

- `src/XonoticGodot.Engine/Collision/Brush.cs` — **Fix 1** (broadphase clamped grid scan; `GridRange` always clamps).
- `game/hud/CrosshairPanel.cs` — **Fix 2** (true-aim cache + `cl_crosshair_trueaim_rate` + `hud.trueaim` scope).
- `game/client/FrameProfiler.cs` — instrumentation (ms/frame dump, scene/census counters, scope registrations).
- `game/net/ClientEntityView.cs`, `game/WorldPvsCuller.cs`, `game/client/MapParticleEmitters.cs`,
  `game/WorldOcclusion.cs`, `game/client/SpawnPointParticles.cs`, `game/client/LaserRenderer.cs`,
  `game/client/VignetteOverlay.cs` — added Prof scopes (instrumentation).
- `src/XonoticGodot.Server/GameWorld.cs` — `mp.fx`/`mp.weapon` sub-scopes (instrumentation).
- `_scratch/perf-run.sh` — reusable capture harness.
