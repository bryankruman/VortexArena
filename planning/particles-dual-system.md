# Dual Particle Systems: Faithful CPU + Modern GPU, with Chunked SDF Collision

Status: IMPLEMENTED (2026-06-11) on branch `feature/modern-particles` — see
`planning/particles-dual-system-STATUS.md` for what landed, what is verified, and the windowed
validation still owed. Companion to `planning/weapon-effects-parity.md`.
Decisions locked 2026-06-11: `cl_particles_modern_nosdf` defaults to 1;
netradiant fork branch DEFERRED — the C# CLI baker is the sole compiler-side
deliverable (revisit only if mapper workflow demands native q3map2).
Findings basis: line-by-line review of `Base/darkplaces/cl_particles.c` vs
`game/client/EffectSystem.cs` (see "Parity findings" summary in §C.0).

## Requirement → section map

| # | Requirement | Section |
|---|---|---|
| 1 | Chunked SDF generator (map-load + map-compiler), hashed user-data cache | §A |
| 2 | Modern GPU backend ≈ original visuals + modern features | §B |
| 3 | CPU faithful simulation, perfect parity with Base | §C |
| 4 | Per-effect choice of modern/original when defining an effect | §D.1 |
| 5 | `cl_particles_modern` 0/1/2 + bidirectional translation layers | §D.2 |
| 6 | Cvar: SDF generation-on-load enable | §A.6 |
| 7 | Cvar: behavior when no SDF present | §A.6 |

## Architecture overview

```
EffectSystem (facade — public API unchanged: Spawn/Trails/MapEmitters/…)
├── ParticleRouter            ← NEW: style resolution + mode routing (§D)
├── shared: EffectInfo catalog, dlights, Decals, ShellCasings, ModelGibs,
│           BeamRenderer, ParticleFont atlas
├── FaithfulBackend (CPU)     ← NEW (§C): particle_t pool + TraceService
│                                collisions + MultiMesh batches
└── ModernBackend (GPU)       ← existing GpuParticles3D path, rebuilt on a
                                 custom particles shader + SDF collision (§B)
MapLoader.BuildMap ──→ SdfCollisionService (§A): cache load / async generate
                       → GPUParticlesCollisionSDF3D chunk nodes
```

Both backends consume the same parsed `EffectInfoEmitter` blocks. Casings,
gibs, decals, beams, and dlights remain shared subsystems (already CPU-side).

Cvar summary (all registered via `ClientSettings` defaults, archived):

| cvar | default | meaning |
|---|---|---|
| `cl_particles_modern` | 0 | 0=original only (+modern→original translation); 1=both coexist, per-effect style honored; 2=all modern (+original→modern translation) |
| `cl_particles_sdf_generate` | 1 | allow SDF generation at map load when no cache/shipped file exists |
| `cl_particles_modern_nosdf` | 1 | when SDF unavailable: 1=run modern collisionless; 0=force original backend (modern effects translated) |
| `cl_particles_sdf_chunk` | 1024 | chunk edge length (qu) |
| `cl_particles_sdf_voxel` | 8 | target voxel size (qu) → per-chunk resolution = chunk/voxel |
| `cl_particles_sdf_debug` | 0 | draw chunk AABBs + generation stats overlay |
| DP mirrors (faithful backend, §C.6) | stock | `cl_particles`, `_quality`, `_size`, `_collisions`, `_blood`, `_sparks`, `_smoke`, `_bubbles`, `_rain`, `_snow`, `cl_decals`, `cl_decals_newsystem_immediatebloodstain`, `r_drawparticles_drawdistance`, `r_drawparticles_nearclip_min` |

---

# A. Chunked SDF pipeline

## A.1 Measured scale (drives all constants)

World bounds of the 29 shipped maps (worldspawn model 0, includes skybox
shell): median max-extent 3904qu; 7 maps span 7.7k–12.4k (techassault 12416,
courtfun 11520, geoplanetary 9400, space-elevator 9344, vorix 9152,
catharsis 8554, xoylent 7712). Fixed-voxel chunking removes map size as a
variable: **1024qu chunks @ 128³ = 8qu voxels, 4.2 MB/chunk (R16F)**.

## A.2 Cache file format (`.psdf`)

One file per map in `user://sdfcache/<mapname>-<bspHash16>.psdf`:

```
header:  magic 'XSDF' | u32 version | 32B sha256(bsp bytes as loaded via VFS)
         | u32 paramsHash (voxel,chunk,skirt,thickness,generatorVersion)
         | f32 voxelSize | f32 chunkSize | f32[3] gridMins | u16[3] gridDims
         | u32 occupiedChunkCount
chunk[]: u16[3] cell | u8 flags | u16 res | u32 compressedLen
         | deflate(R16F res³ signed distances, world units, chunk-local)
```

- Hash the **bsp bytes** (not path) so pk3 vs pk3dir vs edited map all
  invalidate correctly; `paramsHash` invalidates on generator/format changes.
- Loader search order: (1) `maps/<map>.psdf` via VFS (shipped/compiler-baked),
  (2) user cache, (3) generate if `cl_particles_sdf_generate 1`.

## A.3 Generator algorithm

Inputs already exist: brush set + plane lists and tessellated patch triangles
from `BspCollisionBuilder` (`src/XonoticGodot.Engine/Collision/`), and
`TraceService.PointContents`.

Per occupied chunk (cell AABB + 128qu geometry skirt):
1. Gather local brushes and collision triangles by AABB overlap. Empty set →
   chunk unoccupied, no record, no collider node.
2. **Unsigned distance**: per voxel center, min point-triangle distance via a
   chunk-local uniform spatial hash over triangles (cell ≈ 32qu), searched in
   expanding rings, clamped to band `±64qu` (distances beyond band are clamped
   — Godot only needs accuracy near surfaces).
3. **Sign**: voxel center inside any local convex brush (all-planes test) →
   negative. Patches are open shells and contribute surface distance only —
   with thickness dilation below this makes grates *solid* to particles
   (accepted tradeoff, per decision).
4. **Thickness dilation** (replicates Godot bake `thickness`): subtract
   `0.5 * voxelSize` from signed distance, and clamp interior of any hit
   surface to ≤ −voxelSize, so sub-voxel walls/grates read ≥1 voxel thick and
   fast particles don't tunnel.
5. Write R16F slab; deflate.

Scheduling: run on the worker pool **after** map mesh build returns
(`MapLoader.BuildMap` is the single chokepoint — callers `NetGame.cs:763`,
`GameDemo.cs:183`, `AssetLoader.cs:406` need no changes). Chunks complete
incrementally; modern particles run collisionless until their chunk is ready.
Budget target: cold full-map generation ≤5s on a median map (parallel chunks,
~2M voxel queries/chunk); warm load = file read only. Log line for headless
regression guards: `[ParticleSDF] <map>: N chunks (G generated, C cached, T ms)`.

## A.4 Godot encoding validation (de-risk first)

Godot's collider upload/sampling lives in
`servers/rendering/renderer_rd/storage_rd/particles_storage.cpp` +
`particles.glsl`. Task: replicate the editor baker's exact texture encoding
(format, normalization, local-space convention). Validation: bake a unit test
scene (one box) with the editor's `GPUParticlesCollisionSDF3D` bake, dump both
textures, assert voxelwise agreement within tolerance. Do this **before**
writing the full generator — it pins the contract.

## A.5 Runtime integration & the 7-texture cap

- One `GPUParticlesCollisionSDF3D` node per occupied chunk; box = chunk +
  2×skirt (overlapping boxes are intentional); texture from cache;
  `cull_mask` shared with modern emitters.
- Engine caps (verified in `particles_storage.h`): 32 colliders,
  **MAX_3D_TEXTURES = 7** SDF textures per particle system per frame. An
  emitter AABB can straddle up to 8 chunks at a corner. Mitigations, in order:
  (1) skirted bakes mean a dropped 8th volume still has its geometry
  represented in the surviving neighbors' overlap region; (2) keep modern
  burst `VisibilityAabb` ≤ ±384qu so spans stay ≤2 cells/axis; (3) the
  service enables only chunk nodes overlapping live emitters (proximity set,
  LRU ≈ 16 resident textures ≈ 67 MB VRAM worst case; small maps may simply
  keep all chunks resident after empty-cell culling).

## A.6 Cvars (#6, #7)

- `cl_particles_sdf_generate` (default 1): 0 skips generation; cache/shipped
  files still load.
- `cl_particles_modern_nosdf` (default 1): when an effect routes modern but
  no SDF chunk covers it (not generated yet, generation disabled, or cap-
  dropped): 1 = spawn modern without collision response; 0 = reroute that
  spawn to the faithful backend via the modern→original translation (§D.2).
  Checked per spawn, so late-arriving chunks upgrade behavior mid-map.

## A.7 Map-compiler integration (netradiant/q3map2)

Confirmed: Xonotic compiles maps with **q3map2 from the xonotic netradiant
fork** (`https://gitlab.com/xonotic/netradiant.git`; source already checked
out at `Base/netradiant`, q3map2 under `tools/quake3/q3map2/`).

**Canonical (decided): C# CLI baker.** `XonoticGodot --bake-sdf <map.bsp>
[-o out.psdf]` reusing the §A.3 generator verbatim (zero drift between
compiler-time and load-time output). Mappers add it as a post-q3map2 build
step; output ships inside the pk3 as `maps/<map>.psdf` (search order §A.2
picks it up).

**Deferred (decided 2026-06-11): native `q3map2 -sdf` stage.** Would be a
GitHub fork of xonotic/netradiant as submodule `tools/netradiant`, branch
`xonoticgodot/sdf-bake`, with a new `sdf.c` (~600 lines) in the q3map2
dispatch porting §A.3 to C against q3map2's brush/patch structures. Deferred
because it's a second implementation to keep in sync and the CLI covers the
workflow; the §A.2 format spec remains the contract if it's ever revived.

---

# B. Modern GPU backend (visual-parity-plus)

Goal: keep GPU scalability and modern features, but replace the
`ParticleProcessMaterial` approximations with a **custom
`shader_type particles` process shader** implementing DP's actual math.
The existing draw-pass machinery (atlas sprites, blend modes, growth shader,
spark quads, mesh caches) is retained.

## B.1 Custom process shader (one template, per-block parameters)

Uniforms = the `EffectInfoEmitter` fields (sizes/alphas/times/colors/jitters/
offsets/gravity/frictions/bounce/stretch). Cache compiled shaders by
parameter *shape* (which features are nonzero), instantiate cheap
`ShaderMaterial`s per block.

`start()` — DP spawn semantics (`cl_particles.c:1754-1781`, `:668-849`):
- Uniform-ball random `rvec` (use `r = cbrt(rand()) * unitdir` — exact
  uniform-ball equivalent of DP's rejection loop, GPU-friendly), **one sample
  shared by origin and velocity jitter** (the correlated radial expansion).
- `org = lhrandom(originmins..maxs per axis) + originoffset + originjitter⊙rvec`
- `vel = lhrandom(velmins..maxs per axis)·velocitymultiplier + velocityoffset
  + velocityjitter⊙rvec + rotated relativevelocityoffset`
- Per-particle: `size=lhrandom(s0,s1)`, `alpha=lhrandom(a0,a1)`,
  `life = time>0 ? lhrandom(t0,t1) : alpha/min(1,alphafade)`, random
  color-lerp factor, `angle/spin = lhrandom(rotate ranges)`. Stash alpha &
  life in `CUSTOM`, color via `COLOR`.

`process()` — DP integration order (`cl_particles.c:2958-3062`):
```glsl
VELOCITY.y -= gravity_mult * 800.0 * DELTA;          // Godot Y-up, 1qu = 1u
if (airfriction != 0.0)
    VELOCITY *= 1.0 - min(airfriction * DELTA, 1.0); // friction AFTER gravity
                                                     // → terminal velocity
// integrate position ourselves to pin semi-implicit order
TRANSFORM[3].xyz += VELOCITY * DELTA;
COLOR.a = max(0.0, (alpha0 - alphafade * AGE) / 256.0);
if (COLOR.a <= 0.0 || AGE >= life) ACTIVE = false;
if (dot(VELOCITY,VELOCITY) < 0.03) {                  // DP slow-kill
    if (is_spark) ACTIVE = false; else VELOCITY = vec3(0.0);
}
```
- **Spark stretch, live** (`:2812-2820`): each frame write the spark's
  TRANSFORM basis aligned to current velocity with half-length
  `max(stretch*0.04*|vel|, size*0.5)`, width `size` — streaks shorten as they
  decelerate, exactly like DP. (Replaces the baked `SparkAspect`.)
- Snow flutter (`:3091-3098`) via time-hashed rand, **including DP's
  `vel.y` -from-`vel.x` bug** (it's the reference look).
- `sizeincrease` via per-particle scale over AGE (replaces the
  godot#75748 workaround span-bake for modern mode).

## B.2 SDF collision response

Custom particles shaders receive `COLLIDED` / `COLLISION_NORMAL` /
`COLLISION_DEPTH`. Implement DP's reaction (`:2982-3054`):
- push out by `COLLISION_DEPTH`;
- `bounce > 0`: `VELOCITY += n * dot(VELOCITY,n) * (-bounce)` (DP semantics:
  1 = kill normal component, 2 = mirror — do **not** map to Godot
  restitution);
- `bounce < 0` (blood): `ACTIVE = false` + `EMIT_PARTICLE` a sub-emitter splat
  oriented to the normal (approximates the impact decal; the persistent decal
  systems remain CPU/faithful-side);
- no surface flags on GPU: NOIMPACT/NOMARKS/NODROP unmodeled (accepted).

## B.3 Modern features (post-parity, incremental)

Soft particles (depth-fade in the draw shader) — the single biggest
modernization; lit/shadowed smoke (optional per-preset); curl-noise
turbulence; ribbon/tube trail meshes for projectiles; flipbook atlases;
sub-emitter secondary debris; high-count ember/debris showers; HDR emissive
tuned for the existing bloom. Each is a `ModernPreset` knob (§D.1), not a
global change, so gameplay readability is preserved per-effect.

---

# C. Faithful CPU backend (perfect parity)

## C.0 Parity findings being fixed (from the 2026-06-11 review)

GPU path divergences this backend eliminates: (1) air friction ≈ 0 for
jitter-driven bursts (linear Damping derived from baseSpeed=0; e.g.
rocket_explode smoke jitter 912/k=19 → DP max travel ≈48qu vs ~340qu in
port); (2) lost origin↔velocity jitter correlation + anisotropy + ball
distribution; (3) no world collision/bounce/blood-at-impact; (4) per-emitter
midpoint lifetime instead of per-particle; (5) static spark stretch; (6) no
liquid behavior post-spawn; (7) no slow-kill/snow-flutter.

## C.1 Module layout & data

- `src/XonoticGodot.Engine/Particles/ParticleSim.cs` — pure simulation
  (no Godot scene deps → unit-testable, mirrors `lib/` conventions).
- `game/client/particles/FaithfulParticleRenderer.cs` — MultiMesh upload +
  draw shaders.
- Pool: struct array mirroring `particle_t` (org, vel, size, sizeincrease,
  alpha, alphafade, gravity, bounce, airfriction, liquidfriction, die,
  delayedspawn, typeindex, blendmode, orientation, color rgb, texnum,
  staintex, staincolor, stainalpha, stainsize, stretch, angle, spin, time2).
  Free-slot scan with `free_particle` low-water mark; pool grows ×2 up to
  MAX_PARTICLES when full (`cl_particles.c:3174-3177`).

## C.2 Spawn — exact algorithm (CL_NewParticlesFromEffectinfo `:1569-1788`)

Per same-named block in file order (DP layers all blocks):
1. Water gate: spawn-center `PointContents & (WATER|SLIME)` vs
   `underwater`/`notunderwater` flags (already correct in port — keep).
2. Dlight spawn independent of particles (keep existing SpawnInfoLight).
3. `pt_decal` → existing `Decals.SpawnProjected` path (keep; verified faithful).
4. `PARTICLE_HBEAM` → beam particle org=originmins(+rotated reloffset),
   endpoint=originmaxs, lifetime/stretch per block; texcoord scroll
   `dot(end,dir)/64·stretch` (`:2837-2846`).
5. Else: `cnt = countabsolute + pcount·countmultiplier·quality
   (+ traillen/trailspacing·quality if trail) ·fade`; **shared-per-block**
   accumulator `bound(0,acc+cnt,16384)`, drain whole particles
   (`:1710-1754`). Trail: `trailpos` starts at originmins, step
   `traillen/cnt` along traildir per particle (`:1731-1780`); point: re-roll
   `trailpos = lhrandom(originmins..maxs)` per particle.
6. Basis: `AnglesFromVectors(avg velocity | traildir, flippitch=false)` →
   port's `FixedVecToAngles`+`AngleVectors` (already correct).
   `relativeoriginoffset/relativevelocityoffset` rotate through it.
7. Per particle: re-roll tex in [tex0,tex1); **one `VectorRandom` ball sample
   for both jitters**; call NewParticle with per-axis
   `lhrandom(velmins,velmaxs)·velocitymultiplier + velocityoffset +
   velocityjitter⊙rvec + relvel`.
8. `immediatebloodstain` (`:1726-1729`, cvar ≥1 blood / ≥2 staintex, point
   effects only): immediate stain+decal at first particle (replaces the
   current SpawnBloodSplat approximation — same subsystem, now condition-
   exact).

`CL_NewParticle` internals to mirror (`:668-849`): color = random byte-lerp
`l2=(int)lhrandom(0.5,256.5)` (`:726-730`); tint multiplies color (except
INVMOD) and alpha/alphafade/stainalpha (`:771-782`); stain color product
`/0x8000` form (`:740-768`); `lifetime==0 → alpha/min(1,alphafade)` (`:707`);
`die = time + lifetime` **and** alpha-fade kill, whichever first; rain
converts to traced spark + raindecal + delayed splash sub-sparks
(`:804-832`); `delayedspawn` honored. Baseline defaults table = `:233-275`
(port's `EffectInfoEmitter` already matches — re-verify `velocitymultiplier=0`,
`stretch=1`, `stainsize={2,2}`).

RNG: `lhrandom(min,max) = ((rand+0.5)/(RAND_MAX+1))·(max−min)+min`
(`mathlib.h:48`), `VectorRandom` = rejection-sampled unit ball
(`mathlib.h:119`). Implement behind an injectable `IParticleRng` (xorshift in
game; recorded sequence in tests).

## C.3 Per-frame update — exact order (R_DrawParticles `:2907-3105`)

```
frametime = clamp(cl.time − updatetime, 0, 1); updatetime advances clamped
gravity = frametime · movevars_gravity (sv_gravity, 800)
per particle (skip if delayedspawn > time):
  size  += sizeincrease · dt
  alpha −= alphafade · dt;  kill if alpha ≤ 0 or die ≤ time
  if not beam:
    if liquidfriction && cl_particles_collisions && PointContents∈LIQUIDS:
        blood: size += dt·8   else: vel.z −= gravity_mult·gravity
        vel *= 1 − min(liquidfriction·dt, 1)
    else:
        vel.z −= gravity_mult·gravity
        if airfriction: vel *= 1 − min(airfriction·dt, 1)
    org += vel·dt                       // velocity first, then position
    if bounce && cl_particles_collisions && |vel|>0:
        trace old→new, mask SOLID (+LIQUIDS for rain/snow)   (:2984)
        startsolid-SOLID | NODROP | hit NOIMPACT → kill       (:2988)
        hit: org=endpos
             staintex≥0 → stain+decal (Decals; bloodsmears dir = vel-dir
               if cvar else plane normal)                      (:2996-3018)
             pt_blood → NOMARKS? kill : default stain+decal, kill (:3020-3042)
             bounce<0 → kill                                   (:3043-3046)
             else vel += n·dot(vel,n)·(−bounce)                (:3051-3052)
    if |vel|² < 0.03: spark → kill; else vel = 0               (:3057-3062)
  type post-rules (:3065-3105): entityparticle one-frame; blood killed in
  SOLID|LAVA|NODROP; bubble killed outside WATER|SLIME; rain killed in
  SOLID|LIQUIDS; snow: every (rand&3)·0.1s re-wander
  vel.x = vel.x·0.9 + lhrandom(−32,32); vel.y = vel.x·0.9 + lhrandom(−32,32)
  (replicate the x-into-y bug), killed in SOLID|LIQUIDS
```

Trace-result requirement: expose hit `Q3SURFACEFLAG_*` and start/hit
supercontents on `TraceService` results (BspData already stores surfaceflags;
small plumbing task if not yet surfaced on the trace struct).

Intentional divergences (document in-code): `R_Stain` lightmap stainmaps →
routed to `Decals` sprites (no stainmap system); sRGB particle-color path
(`vid.sRGB3D`) follows the project's existing color-space handling; legacy
`cl_particles_quake`/`CL_ParticleEffect_Fallback`/pt_explode ramps deferred
(unreachable for effectinfo-defined Xonotic content; revisit only if a port
call site emits legacy TE codes).

## C.4 Rendering (MultiMesh)

- 3 batches by blend (alpha / add / invmod→sub) over the single particlefont
  atlas; per-instance: transform, COLOR (rgb+alpha), CUSTOM (atlas cell rect
  index, angle+spin phase). Billboarding in the draw vertex shader (same math
  as the existing growth shader); sparks/oriented particles upload their
  CPU-computed basis (spark length from **current** velocity, `:2812-2825`).
- Per-frame upload via one packed `RenderingServer.multimesh_set_buffer` per
  batch (~20 floats/instance; 5k particles ≈ 400 KB/frame — negligible).
- Draw-time fidelity: `size · cl_particles_size` (`:2732`); near-clip skip
  when `dot(org, view.forward) < dot(view.org, view.forward) +
  r_drawparticles_nearclip_min` (`:2932`); `r_drawparticles_drawdistance`²
  cull (`:2935`).
- Sorting: alpha-blend batch CPU-sorted back-to-front per frame (≤ a few k —
  cheap); add/invmod are order-independent. Known approximation: no
  interleaved sorting against other transparent world surfaces (DP submits
  per-particle into a global transparent queue); accept, revisit only if
  glass-vs-smoke artifacts show up.

## C.5 Testing — golden traces (mirrors `tools/movement-ref`)

- `tools/particles-ref/`: minimal C harness extracted from
  `cl_particles.c` (spawn + update loop + the math macros), deterministic
  seeded `rand()`, scripted scenarios (each effectinfo archetype: jittered
  burst, gravity spark w/ bounce plane, liquid pool, trail sweep, snow,
  blood-vs-wall) → JSON traces of (org, vel, size, alpha, alive) per step.
- C# `ParticleParityTests` replays `ParticleSim` with the recorded RNG
  sequence injected; assert componentwise ≤1e-4 over 120 steps at mixed dt
  (16.6ms, 7ms, 41ms — variable frametime is part of the contract).
- Distribution tests: ball-jitter radial histogram, color-lerp byte ranges,
  accumulator drain over 1000 calls at count 0.025 (expect 25±ε).
- Perf gate in tests: 32k-particle update ≤2 ms without traces, ≤4 ms with
  500 bounce traces (stormkeep collision set), single thread.

## C.6 DP cvar mirrors

Register the table from §overview with stock defaults; quality multiplies
spawn counts exactly where DP does (`:1711,:1717` — never countabsolute);
type gates (`cl_particles_blood` etc.) skip blocks at spawn (`:1699-1708`).

---

# D. Routing, authoring, translation

## D.1 Per-effect style (#4)

- `EffectStyleRegistry`: effect name → `{ style: original|modern|auto,
  modernPreset?: id }`.
- Authoring: port-side overlay `effectinfo_xg.txt` (parsed by our
  `EffectInfo` only — never touches the shared upstream effectinfo.txt, so
  DP/Base compatibility is preserved). Syntax: `effect <name>` +
  `xg_style modern` + `xg_preset <id>` + preset parameter overrides.
  Modern-only effects (new names) are defined entirely in the overlay with a
  `ModernPreset` body; every modern-only effect **must** declare or derive
  fallback blocks (D.2) so mode 0 renders something faithful-shaped.
- `ModernPresetLibrary` (C#): named recipes (soft-smoke, ember-shower,
  shockwave, ribbon-trail, lit-explosion) = custom-shader parameter sets +
  draw-pass options.

## D.2 `cl_particles_modern` modes & translation layers (#5)

Routing at `EffectSystem.Spawn` (and `BuildProjectileTrailEmitters` /
map-emitter equivalents):

- **0 (default)** — everything → FaithfulBackend. Modern-authored effects pass
  through **modern→original translation**: synthesized `EffectInfoEmitter`
  blocks (auto-derivation: turbulence→velocityjitter boost, ribbon→spark
  trail blocks at equivalent spacing, soft-smoke→alphastatic, sub-emitters→
  pre-spawned counts; presets may override the auto-derived blocks by hand).
- **1** — both backends live; per-effect style decides (`auto` = original for
  effectinfo-defined, modern for modern-authored). SDF service active.
- **2** — everything → ModernBackend. Original effectinfo blocks render
  through the §B custom shader, which *is* the original→modern translation
  (faithful math on GPU + modern draw features), gated by
  `cl_particles_modern_nosdf` for collision availability.

Live-switch semantics: new spawns route per current mode; in-flight effects
finish on their backend (no drain/teleport).

## D.3 Menu

Settings → Effects: "Particles" dropdown (Original / Mixed / Modern) bound to
`cl_particles_modern` via the existing cvar-bound widget toolkit; "Generate
collision fields" checkbox → `cl_particles_sdf_generate`.

---

# E. Sequencing

| M | Deliverable | Notes |
|---|---|---|
| M1 | §C CPU backend + particles-ref goldens + DP cvars | The default mode-0 system and universal fallback; biggest single chunk; immediately replaces the worst parity gaps |
| M2 | §D router + 3 cvars + menu, ModernBackend = current GPU path as-is | Makes modes 0/1/2 exercisable early; mode 2 temporarily = today's visuals |
| M3 | §A.4 encoding spike → §A.2-A.6 SDF service + cache + cvars | Independent of M4; do A.4 first as de-risk |
| M4 | §B modern backend rebuild (custom shader, SDF response, soft particles, presets) | Mode 2/1 reach target quality |
| M5 | §D.1 authoring overlay + translation polish + §A.7 CLI baker | netradiant branch deferred |
| M6 | §F validation matrix + perf + docs | |

# F. Validation (explicit, end-of-project)

1. Golden parity suite green (C.5), including variable-dt and bounce traces.
2. `--fx-demo` extended: spawns every effectinfo archetype + each ModernPreset,
   asserts expected backend node types per mode, zero errors, log-line guards
   (`[ParticleSDF]`, `[Particles] backend=…`).
3. **Mode matrix** (each entry headless + windowed eyeball):
   `cl_particles_modern ∈ {0,1,2}` × `cl_particles_sdf_generate ∈ {0,1}` ×
   `cl_particles_modern_nosdf ∈ {0,1}` × {cache present, cache absent, cache
   stale-hash}. Verify: mode 0 never instantiates GPU emitters; mode 2 with
   nosdf=0 and no SDF routes to faithful; stale hash regenerates exactly once.
4. Cache invalidation: touch a byte in a test bsp → hash mismatch →
   regenerate; params bump → regenerate; warm load generates nothing.
5. Visual A/B vs native Base (run-xonotic.sh + screenshot tooling): rocket/
   grenade/electro explosions, blood vs wall, spark bounces on stormkeep
   floor, teleport, item despawn — side-by-side at fixed camera; mode 0
   should be indistinguishable in motion.
6. Perf: C.5 gates; SDF cold-gen ≤5s median map / warm ≤50ms; mode 2 with 10
   simultaneous explosions ≥ mode-0 fps.
7. 7-texture-cap probe: scripted burst at a 4-chunk corner on techassault;
   verify no visible collision hole (skirt coverage).

# G. Risks / open questions

- **Godot SDF texture encoding** undocumented → A.4 spike is scheduled first.
- 7-texture cap corner cases on dense effect scenes → skirts + proximity set;
  validated by F.7.
- Custom particles shaders + `EMIT_PARTICLE` sub-emitters interaction with
  our draw passes needs a prototype before committing B.2's blood splat.
- Alpha-sort approximation (C.4) vs DP's global transparent queue — accepted;
  revisit on artifact reports.
- ~~Netradiant fork maintenance liability~~ RESOLVED 2026-06-11: deferred;
  CLI baker is the sole compiler-side deliverable.
- `particleaccumulator` is per-block-global in DP (shared across all
  simultaneous trails of the same effect) — counterintuitive but faithful;
  port already mirrors this; keep it.
