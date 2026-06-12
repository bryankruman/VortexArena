# Dual Particle System — Implementation Status

Status: IMPLEMENTED (2026-06-11), branch `feature/modern-particles`. Companion to
`planning/particles-dual-system.md` (the design). This file records what landed, how it is wired,
what is verified, and the runtime validation still owed (it needs a windowed Godot session).

## What landed

All six milestones (M1–M6) are implemented and the whole solution builds clean
(`dotnet build XonoticGodot.sln`). The full test suite is green: **1512 passed, 0 failed, 0 skipped**,
including the new particle parity + SDF tests.

### Engine (Godot-free, `src/XonoticGodot.Engine/Particles/`)
- `IParticleRng.cs` — RNG seam: `ParticleRandom.Lhrandom`/`VectorRandom` (exact DP `mathlib.h:48,119`,
  RandMax = 2³¹−1), `XorShiftParticleRng` (in-game), `RecordedParticleRng` (tests).
- `ParticleTypes.cs` — `ParticleType/Blend/Orientation` enums (cast-compatible with host `Ei*`),
  `ParticleEmitterInfo` (sim-facing snapshot + shared `ParticleAccumulator`).
- `Particle.cs` — the `particle_t` pool struct.
- `ParticleCvars.cs` — every cvar name + stock default (the §overview table).
- `ParticleSim.cs` — **the faithful CPU simulation**: `CL_NewParticle` /
  `CL_NewParticlesFromEffectinfo` spawn + `R_DrawParticles` update, line-by-line from
  `Base/darkplaces/cl_particles.c`. Collisions/cvars/clock via the ambient `Api` facade; stains/decals
  surfaced through the `OnStain` hook (`StainEvent`) so the core stays pure.
- `PsdfFormat.cs` — `.psdf` cache format (`SdfChunk`/`SdfField`/`SdfGenParams` + `PsdfFile` reader/writer,
  deflate(R16F), SHA-256 bsp hash, params hash).
- `SdfGenerator.cs` — chunked signed-distance generator (per-chunk brush gather + point-to-convex-polytope
  signed distance + thickness dilation + band clamp; `Parallel.For` over chunks).
- `SdfBakeCli.cs` — the `--bake-sdf <map.bsp> [-o out.psdf]` headless baker (reuses the generator verbatim).

### Game (`game/client/particles/`)
- `ParticleStyle.cs` — routing vocabulary (`ParticleStyle`, `EffectStyleEntry`, `ParticleBackendKind`).
- `FaithfulParticleRenderer.cs` — 3 blend-keyed `MultiMesh` batches over the particlefont atlas; inline
  billboard shader; live spark stretch; near-clip + draw-distance cull; back-to-front alpha sort.
- `FaithfulParticleBackend.cs` — owns a `ParticleSim` + renderer; converts `EffectInfoEmitter` →
  `ParticleEmitterInfo`; bridges `OnStain` → Godot `Decals`.
- `SdfCollisionService.cs` — loads/generates the field at map load, builds one
  `GPUParticlesCollisionSDF3D` per chunk, proximity/LRU resident set (7-texture cap), `HasCoverage` gate.
- `ModernParticleBackend.cs` + `ModernParticleShaders.cs` + `ModernPreset.cs` + `ModernPresetLibrary.cs` —
  the modern GPU backend: custom `shader_type particles` process shader (DP spawn+integration + SDF
  collision response) + spatial draw shader (atlas/blend/soft/lit/flipbook/emissive) + named presets.
- `EffectInfoOverlay.cs` + `EffectStyleRegistry.cs` — the `effectinfo_xg.txt` per-effect style overlay
  (never touches upstream `effectinfo.txt`).
- `ParticleTranslation.cs` — modern→original (`ToFaithful`) and original→modern (`DerivePreset`) layers.
- `ParticleRouter.cs` — **the §D.2 routing**: resolves faithful vs modern per
  `cl_particles_modern` × authored style × SDF coverage, with the `cl_particles_modern_nosdf` reroute.

### Integration (shared-file edits)
- `EffectSystem` creates the backends + SDF service + router as children, wires Font/Decals/overlay in
  `EnsureInfoLoaded`, and routes every effectinfo spawn through `Router.Route(...)` before the legacy GPU
  path. New: `EffectSystem.BuildSdfForMap(...)`.
- `ClientSettings.RegisterParticleDefaults` registers `ParticleCvars.Defaults` at boot.
- `DialogSettingsEffects` — "Particles renderer" dropdown (Original/Mixed/Modern → `cl_particles_modern`)
  and "Generate collision fields" checkbox (`cl_particles_sdf_generate`).
- `NetGame` calls `BuildSdfForMap` at map load, **gated on `cl_particles_modern != 0`** (mode 0 pays nothing).
- `Main` handles `--bake-sdf`; `GameDemo` gains `--fx-mode <0|1|2>` for backend A/B screenshots.

### Tests + reference harness
- `tools/particles-ref/particles_ref.c` — independent C reference (mirrors `tools/movement-ref`),
  records its `rand()` sequence; regenerates the goldens via WSL gcc.
- `tests/XonoticGodot.Tests/golden/particles/*.json` — 6 committed golden traces.
- `ParticleParityTests.cs` — replays `ParticleSim` against the C reference with the recorded RNG injected;
  asserts componentwise agreement (incl. bounce/liquid/blood collision scenarios) + distribution/accumulator.
- `SdfGeneratorTests.cs` — box sign, band clamp, `.psdf` round-trip, hash determinism/invalidation.

## Verified vs. unverified

**Verified here** (build + headless tests):
- Whole solution compiles; 1512/1512 tests pass.
- Faithful sim is bit-faithful to the independent C reference across all 6 archetypes (no skips).
- SDF generator + `.psdf` round-trip + cache hashing.

**Verified in a windowed Godot 4.6.3 session** (2026-06-11, stormkeep, `--fx-demo rocket_explode`):
- **Mode 0 (faithful CPU)** renders in-game — `MultiMesh` billboards draw the rocket_explode burst on a real
  map, `[Particles] backend=original`, no script errors. (Screenshot: `screenshots/particles_mode0.png`.)
- **Mode 2 (modern GPU)** renders in-game — the custom `shader_type particles` process shader + spatial draw
  shader compile and produce a vivid emissive explosion with live spark streaks, `[Particles] backend=modern`.
  This required fixing 4 Godot-4.6 shader bugs the spec-authored (never-GPU-compiled) shaders had: `uint >> int`
  (needs `uint` shift operand), `INSTANCE_CUSTOM` read in `fragment()` (vertex-only → pass via a `varying`),
  `return` in `process()` (forbidden → guard the body), `DEPTH_TEXTURE` (removed in 4.x → `hint_depth_texture`
  uniform). (Screenshot: `screenshots/particles_mode2.png`.)

**Still NOT verified:**
- The SDF Texture3D encoding (`§A.4`) — `SdfCollisionService.BuildSdfTexture` is **provisional** (format
  `Rf`, axis permutation `qx=gx, qy=res-1-gz, qz=gy`). Mode 2 above ran *collisionless* (the GameDemo path
  doesn't build the SDF), so the encoding/collision response is unexercised; the editor-bake voxelwise
  comparison spike (§A.4) must run before trusting modern collision response.
- A minor non-fatal shutdown warning ("RID allocations … leaked at exit" / "RenderingServer … is null") —
  the new MultiMesh/Texture3D RIDs aren't freed in `_ExitTree`; cleanup follow-up (no runtime effect).

## Validation still owed (planning §F — windowed)
1. F.2: `--fx-demo` per mode → assert backend node types + log guards (`[Particles] backend=…`, `[ParticleSDF]`).
2. F.3 mode matrix: `cl_particles_modern {0,1,2}` × `sdf_generate {0,1}` × `modern_nosdf {0,1}` × {cache present/absent/stale}.
3. F.4 cache invalidation (touch a bsp byte → regenerate once).
4. F.5 visual A/B vs native Base (rocket/grenade/electro, blood-vs-wall, spark bounce, teleport).
5. F.6 perf (SDF cold-gen ≤5s median / warm ≤50ms; mode 2 ≥ mode 0 fps with 10 explosions).
6. F.7 7-texture-cap corner probe on techassault.

## Known gaps / follow-ups
- SDF Texture3D encoding spike (§A.4) — highest priority before modern collision is trustworthy.
- `ModernParticleBackend` blood-splat sub-emitter (`bounce<0`) needs a `SubEmitter` node attached (§B.2 — left a no-op).
- `SdfCollisionService.UpdateProximity` is not yet called per-frame with live modern-emitter origins (resident-set
  optimization; `HasCoverage` gating already works).
- `FaithfulParticleRenderer` re-packs atlas cells into its own texture (ParticleFont exposes no cell rects) —
  add public accessors to sample the font atlas directly.
- Modern trails route through `Spawn` (no ribbon-trail mesh yet — §B.3 incremental).
- Routing is wired at `EffectSystem.Spawn` (one-shot + trail effect names). The continuous
  **projectile-trail** (`ProjectileRenderer.BuildProjectileTrailEmitters`) and **map-emitter**
  (`MapParticleEmitters`) paths still use the legacy GPU emitters — §D.2 lists them as routing sites;
  wiring them through the router is a follow-up (their own continuous-emitter machinery makes it a larger
  change than the one-shot facade).
