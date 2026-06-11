# Weapon effects parity — diagnosis & implementation plan

> **STATUS (2026-06-10): T1–T7 IMPLEMENTED, builds + 1477 tests pass. NOT yet feel/screenshot-verified
> (the headless capture booted to the menu; user will verify windowed manually).** What landed:
> - **T1** trail particles on the line: `EffectSystem.BuildInfoBurst` trail path now emits from a baked
>   emission-POINT texture stepped along the segment (`TrailGeometry.PointsAlongSegment` in
>   `src/XonoticGodot.Engine/Effects`, 4 unit tests), `Explosiveness=1`. Heuristic `BuildTrail` too. **This is
>   the vortex-beam fix.**
> - **T2** single tint: de-compounded in the info burst, the new trail emitter, the heuristic burst/trail, and
>   the legacy `ProjectileRenderer.BuildTrail` (tint now in ONE place: `mat.Color`/`ColorInitialRamp`; ramp +
>   AlbedoColor are White). Map emitters (`BuildEmitterMesh`) deliberately untouched (separate pipeline).
> - **T3** velocity-aligned sparks: `GpuParticles3D.TransformAlign = ZBillboardYToVelocity` + BillboardMode off
>   for spark blocks; `SparkAspect()` length from DP `stretch*0.04*speed`.
> - **T4** impact velocity args: Blaster/Arc-bolt now pass `-normalize(velocity)*1000` (the w_backoff fallback);
>   Machinegun/Rifle/Shotgun use the real `impTr.PlaneNormal`; Vortex impact now `'0 0 0'` (matches QC
>   boxparticles); Crylink secondary now emits `CRYLINK_IMPACT2` (threaded `secondary` through OnTouch/LinkExplode).
> - **T5** effectinfo-driven projectile trails: `EffectSystem.BuildProjectileTrailEmitters` builds one
>   continuous emitter per trail block (Amount = speed·life/trailspacing); `ProjectileRenderer` now holds
>   `Visual.Trails` (list), falls back to the legacy single emitter for names absent from effectinfo.
> - **T6** temporal sizeincrease: custom billboard `ShaderMaterial` (`GrowthShader`) scaling by
>   `INSTANCE_CUSTOM.z` (lifetime phase via anim_speed=1) — used only for non-spark billboards with a sprite +
>   nonzero sizeincrease; everything else keeps the proven StandardMaterial3D baked-span path. **HIGHEST
>   regression risk — verify the growing particles (explosion fire/smoke, muzzle flash, nex_impact ring) still
>   render; if broken, make `ComputeGrowthRatio` always return null to fall back to the baked span.**
> - **T7** Arc beam: `ARC_BEAM`/`ARC_BEAM_HEAL` registered `isTrail:true` (they were dropped as count-0 point
>   effects → the arc beam never drew). T7.1 textured-HBEAM for `type beam` (vaporizer/old-vortex) left as
>   optional polish (the CrossRibbon beam is acceptable).
> Verify (windowed): `Godot_..._mono_win64.exe --path . -- --map maps/atelier.bsp --fx-demo nex_beam
> --screenshot <abs>.png --screenshot-frames 150` (also `--fx-demo rocket_explode` for T6 growth, `--proj-demo`
> for T5 trails). Per-particle random atlas cell (RC6) intentionally NOT done — kept single-cell-per-emitter.



Goal: make weapon particles, sprites, projectile trails, and the vortex beam match stock Xonotic
(Darkplaces). This doc lists verified root causes and precise tasks. Each task is self-contained;
do them in the listed order. Reference engine source: `Base/darkplaces/cl_particles.c` (spawn:
`CL_NewParticlesFromEffectinfo` ~1569–1790, sim/render: `R_DrawParticles` ~2907–3169). Effect data:
`Base/data/xonotic-data.pk3dir/effectinfo.txt`.

Port files involved:
- `game/client/EffectSystem.cs` — effectinfo-driven bursts/trails (`BuildFromInfo`, `BuildInfoBurst`,
  `ApplyInfoColorRamp`, `BuildInfoMesh`), heuristic fallback (`BuildBurst`, `BuildTrail`).
- `game/client/EffectInfo.cs`, `EffectInfoParticle.cs` — parser/data (correct; mostly untouched).
- `game/client/ProjectileRenderer.cs` + `ProjectileCatalog.cs` — hand-rolled projectile trails.
- `game/client/BeamRenderer.cs` — CrossRibbon beams (TE_TEI_G3 `type beam` block routes here).
- `game/client/ParticleFont.cs` — atlas (correct).
- `src/XonoticGodot.Common/Gameplay/Weapons/*.cs` — impact-effect call sites.
- `src/XonoticGodot.Common/Gameplay/Effects/EffectsList.cs` — registry name → effectinfo name.

## Verified root causes (ranked by visual impact)

**RC1 — Trail particles spawn in the segment's axis-aligned bounding box, dribbled out over time.**
`EffectSystem.BuildInfoBurst` trail path: `EmissionShape = Box` with
`EmissionBoxExtents = |seg|*0.5 per axis + jitter` (EffectSystem.cs:936-940) and
`Explosiveness = 0` for trails (EffectSystem.cs:910). DP instead spawns ALL `cnt` particles in the
same frame, stepped exactly along the line: `trailstep = traillen/cnt; trailpos += trailstep*traildir`
per particle (cl_particles.c:1780), with per-axis `originjitter` around each step point. For a long
diagonal shot the port fills a giant box volume with particles that pop in over ~0.2–1.5 s instead of
drawing a crisp line. **This is the vortex beam bug**: stock default beam = `nex_beam` (effectinfo.txt:1447-1480,
4 trail blocks: bright core line `alpha 256 256 1280` spacing 12; ring tex 32 spacing 64; smoke tex 0-8
spacing 12; drifting dots tex 63 spacing 16) — pure trail particles, `cl_particles_oldvortexbeam` defaults 0.
Port maps `VORTEX_BEAM → nex_beam, isTrail:true` (EffectsList.cs:82) so it gets the worst of this.

**RC2 — Projectile trails are hand-rolled, not effectinfo-driven.**
`ProjectileRenderer.BuildTrail` (ProjectileRenderer.cs:379-449) builds ONE continuous point-emitter per
projectile with invented params (Direction=Up, speed 2–10, one color, one sprite, 25° spread), where DP
runs EVERY same-named effectinfo block distance-stepped via trailspacing. E.g. `TR_ROCKET` is 4 blocks
(effectinfo.txt: smoke tex 0-8 rotating/growing spacing 10; fire core `static` tex 48-55 spacing 4 with
`velocitymultiplier -1.5` (streams backward); underwater bubbles; bright sparks tex 41 spacing 12 with
`velocityoffset 0 0 256`). Port shows a single generic puff stream: wrong sprites, no fire core, no
sparks, density independent of projectile speed.

**RC3 — `sizeincrease` has no temporal effect.** Baked into the spawn scale span instead
(EffectSystem.cs:1038-1041, workaround for godot#75748: scale curve + Box emission + particle billboard
stops drawing). Muzzle flashes (`sizeincrease -100`) don't pop-and-shrink; smoke doesn't billow; the
vortex impact shockwave ring (`nex_impact` block `sizeincrease 900`, effectinfo.txt:1501-1508) renders as
random-size static blobs instead of an expanding ring.

**RC4 — Sparks aren't velocity-aligned.** `BuildInfoMesh` uses a fixed 0.15×2.0 quad with
`BillboardMode.Particles` (EffectSystem.cs:1137-1138, 1152) → elongation direction is arbitrary
screen-space. DP `PARTICLE_SPARK` aligns the quad to velocity with length
`max(stretch*0.04*speed, size*0.5)` (cl_particles.c:2812-2825). Affects every impact spray
(laser_impact has `count 128` sparks), muzzle flashes, damage effects.

**RC5 — The tint is multiplied in up to three places** → colors darker/muddier than stock.
Final color = `ProcessMaterial.Color` × `ColorRamp` RGB (set to baseColor, EffectSystem.cs:1122-1125)
× draw-pass `AlbedoColor` (set to baseColor again, EffectSystem.cs:1159 with VertexColorUseAsAlbedo)
[× `ColorInitialRamp` when Color0≠Color1]. Godot multiplies all of these
(COLOR = color_value × initial_ramp × color_ramp; albedo = AlbedoColor × vertex COLOR × texture).
A 0x808080 smoke renders at ~0.125 instead of 0.5. Same triple-apply in
`ProjectileRenderer.BuildTrail` (mat.Color + ramp colors + AlbedoColor all = c.Color) and the heuristic
`BuildBurst`/`ApplyColorRamp` path.

**RC6 — Lost per-particle randomization** (each individually moderate, together = "uniform/flat" look):
- one atlas cell per EMITTER, not per particle (EffectSystem.cs:1069-1076; DP: `tex N M` random per particle);
- initial alpha = midpoint, not `lhrandom(alphaMin, alphaMax)` (ApplyInfoColorRamp, EffectInfoParticle.MidAlpha01);
- per-axis `velocityjitter` collapsed to an isotropic cone + speed band (EffectSystem.cs:966-980; DP adds
  independent per-axis uniform random — vertical-biased jitters like `100 100 300` lose their character);
- trail particles get ZERO emit velocity (`emitVel = isTrail ? default : velocity`, EffectSystem.cs:715) —
  the trail-spawn API has no velocity param at all, so `velocitymultiplier` blocks are static.

**RC7 — minor:** several impact call sites omit the `w_backoff * 1000` velocity arg QC passes
(Blaster.cs ~193 "BLASTER_IMPACT", Electro.cs "ELECTRO_IMPACT", Crylink impacts — also Crylink uses
"CRYLINK_IMPACT" for both primary and secondary where QC uses EFFECT_CRYLINK_IMPACT2 for secondary);
`type beam` blocks (TE_TEI_G3 = vaporizer beam / old vortex beam) route to BeamRenderer's CrossRibbon
with an invented glow gradient instead of DP's HBEAM: a flat quad textured with atlas beam strip
(tex 200), width 2×size=8u, `alpha 128 128 256` (≈0.5 s fade); InvMod≈Sub blend is a known approximation.

DP semantics quick-reference (for all tasks): size = half-size, world units (quad edge = 2×size);
alpha 0–256, alphafade = units/sec, die when alpha≤0 or time elapsed; gravity = multiplier × 800;
airfriction exponential `v *= 1-min(k*dt,1)`; count = countabsolute + pcount*countmultiplier*quality
(+ traillen/trailspacing for trails; trailspacing sets countmultiplier=1/trailspacing at parse —
the port's formula at EffectSystem.cs:875-878 already matches, don't touch).

---

## Tasks (do in this order)

### T2 first — single tint application (cheap, global win)
In `EffectSystem.BuildInfoMesh`: set `AlbedoColor = Colors.White` (keep VertexColorUseAsAlbedo).
In `ApplyInfoColorRamp`: ramp colors RGB = white, only alpha animates
(`ramp.SetColor(0, new Color(1,1,1,a0))`, end `new Color(1,1,1, …)`).
Tint must live in exactly ONE place: if `Color0 != Color1` set `mat.Color = Colors.White` and let
`ColorInitialRamp` carry the per-particle color (it already exists, EffectSystem.cs:1048-1056);
else `mat.Color = baseColor`.
Mirror in `ProjectileRenderer.BuildTrail` (ramp white-RGB + AlbedoColor white, keep mat.Color) and in
the heuristic path: `BuildBurst`/`BuildTrail` set `mat.Color = White`; `ApplyColorRamp`'s gradient
already encodes the tint (warm→base→dark) so also set `BuildParticleMesh` AlbedoColor = White.
Decal color (`InfoDecalColor`) is a separate path — do NOT touch.
NOTE on color space: after de-compounding, colors may now read BRIGHTER than stock (Godot renders
linear; effectinfo colors are display-ish values). Screenshot A/B vs stock first; if washed out, apply
`Color.SrgbToLinear()` where the hex unpacks (`EffectInfoParticle.Unpack`/MidColor consumers) — one
switch, decide by eye against the reference.

### T1 — trail particles on the line (fixes the vortex beam)
In `BuildInfoBurst`, when `isTrail && traillen > 0`:
1. `Explosiveness = 1.0f` (all particles this frame, like DP).
2. Replace the Box emission with point emission along the segment:
   - Compute n as today (accumulator). `step = seg / n` (Quake coords originMin→originMax).
   - Build points CPU-side: `p_i = originMin + step*(i+0.5) + perAxisJitter` where
     `perAxisJitter = (jx*rand(-1,1), jy*rand(-1,1), jz*rand(-1,1))` from `info.OriginJitter`
     (+ `info.OriginOffset` + the relative-offset term already computed as `relOrigin`).
     Convert each to Godot local space relative to the parent (parent sits at originMin):
     `Coords.ToGodot(p_i - originMin)`.
   - Upload: `Image.CreateFromData(n, 1, false, Image.Format.Rgbf, bytes)` →
     `ImageTexture.CreateFromImage` → `mat.EmissionShape = Points`,
     `mat.EmissionPointTexture = tex`, `mat.EmissionPointCount = n`.
   - `particles.Position = Vector3.Zero` for this branch (points are already absolute-relative;
     the burst branch keeps its `centerOffsetG`).
3. Extract point generation into a pure static helper
   `internal static NVec3[] TrailPoints(NVec3 start, NVec3 end, int n, NVec3 jitter, Random rng)`
   so it's unit-testable: every point within `jitter` (per axis) of the segment; consecutive
   parameter t monotonically increasing; n points exactly. Add tests in the EffectSystem test file
   neighborhood (pure helper → no Godot dependency; if EffectSystem is in game/ and untestable, put
   the helper in `src/` e.g. next to EffectInfoParticle, or as a static in EffectInfo.cs which already
   has tests).
4. Keep the burst (non-trail) branch byte-identical.
5. Optional cap: n ≤ 512 points per call (vortex across a big map: len ~4000 / spacing 12 ≈ 333 — fine).
Also apply the same treatment to the heuristic `BuildTrail` (EffectSystem.cs:565-615) — lower priority,
only used when effectinfo is missing.
Verify: `--fx-demo` equivalent for trails — add a dev flag (mirror the existing `--fx-demo` plumbing in
GameDemo) that calls the trail spawn between two fixed diagonal points every 0.5 s, then
`--map <map> --screenshot`. The beam must read as a straight line of particles, visible in the same
frame it was fired, with the bright core + rings + wisps layering of nex_beam.

### T4 — impact velocity args + Crylink secondary effect (one-liners)
QC passes `pointparticles(EFFECT_*_IMPACT, org2, w_backoff * 1000, 1)` where `w_backoff` is the impact
surface plane normal. In the port pass the impact trace's plane normal × 1000 as the velocity arg
(NOT -shot.Dir): Blaster impact, Electro impact, Crylink impact, Shotgun impact, Arc bolt impact —
grep `_IMPACT"` in `src/XonoticGodot.Common/Gameplay/Weapons/` and compare each against the weapon's
`wr_impacteffect` in `Base/data/xonotic-data.pk3dir/qcsrc/common/weapons/weapon/<w>.qc`.
Crylink: secondary/bounce impact should emit `CRYLINK_IMPACT2` (register in EffectsList mapping to
effectinfo `crylink_impact2` — check exact name in effectinfo.txt) instead of reusing CRYLINK_IMPACT.
This matters because the velocity feeds the relative-offset basis and `velocitymultiplier` blocks
(e.g. laser_impact smoke `velocitymultiplier 0.01` drifts off the wall).

### T3 — velocity-aligned sparks
For emitter blocks with `Orientation == EiOrientation.Spark`:
- On the `GpuParticles3D` node: `TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity`.
- In `BuildInfoMesh` spark branch: `BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled`
  (TransformAlign now owns orientation; quad +Y = velocity direction). Keep the elongated quad.
- Length approximation (DP: `len = max(stretch*0.04*speed, size*0.5)`, width = size): per emitter
  compute `expectedSpeed = baseSpeed + jitterSpeed` (both already computed in BuildInfoBurst);
  `aspect = clamp((info.StretchFactor>0?info.StretchFactor:1) * 0.04f * expectedSpeed / MathF.Max(0.5f, (info.SizeMin+info.SizeMax)), 1f, 30f)`;
  quad `Size = new Vector2(0.5f, aspect)` (scale carries the 2×size edge; 0.5 ⇒ world width ≈ size).
  Pass aspect into BuildInfoMesh (new param).
Verify with laser/machinegun impact in `--fx-demo`: sparks must streak OUTWARD along their flight
direction and fall with gravity, not shimmer as fixed vertical slivers.

### T5 — effectinfo-driven projectile trails
Replace the invented params in `ProjectileRenderer.BuildTrail` with per-block emitters derived from
effectinfo (keep the continuous-emitter architecture — do NOT spawn nodes per frame):
1. Look up blocks: `Effects.InfoBlocks(desc.TrailEffect)` (add a small accessor on EffectSystem that
   returns the parsed `IReadOnlyList<EffectInfoEmitter>?`; reuse LookupInfo).
2. For each block except `underwater`-gated ones (skip `Underwater` blocks; keep `NotUnderwater`):
   build one continuous `GpuParticles3D` child:
   - `Lifetime = block.Lifetime()`, `OneShot=false`, `LocalCoords=false`, `Explosiveness=0`,
     keep the pinned VisibilityAabb.
   - Required emission rate = projectileSpeed / trailspacing (particles/sec). Godot rate = Amount/Lifetime
     ⇒ `Amount = clamp(ceil(speed * block.Lifetime() / block.TrailSpacing), 2, 256)`. Use a nominal
     per-type speed from ProjectileCatalog (add `NominalSpeed` to Desc: rocket ≈ 1000, grenade ≈ 900,
     crylink ≈ 3000, electro ≈ 2500 — read g_balance defaults; exactness not critical) or measure the
     entity's first observed velocity. Blocks with `TrailSpacing <= 0` (pure count blocks): skip.
   - Process material: extract the block→ParticleProcessMaterial construction out of `BuildInfoBurst`
     into `BuildInfoProcessMaterial(EffectInfoEmitter info, NVec3 emitVel, …)` and reuse it here with
     point emission (EmissionShape=Point) + originjitter as small Box extents. This brings gravity,
     airfriction, rotate, color ramps (with T2 fix), bounce for free.
   - Velocity inheritance: emitVel = projectileVelocity × block.VelocityMultiplier (+VelocityOffset).
     Since direction changes in flight, the process material must be PER-INSTANCE for blocks with
     |VelocityMultiplier| > 0 or VelocityOffset != 0: clone the material and update
     `Direction/InitialVelocityMin/Max` in the renderer's per-frame projectile update (live projectile
     count is small; for blocks with velocitymultiplier==0 keep a shared cached material per
     (type, blockIndex)).
   - Draw pass via `BuildInfoMesh(block, …)` (gets atlas cell, blend, spark handling from T3).
3. Dynamic light: if any block has `LightRadius > 0` attach an OmniLight3D to the projectile node
   (range/color via the same normalization as `SpawnInfoLight`) — replaces/unifies the catalog
   `HasLight` flag for these types.
4. Keep ProjectileCatalog's Body/model/glow rendering unchanged; only the Trail params become
   data-driven. Remove the now-dead hand-tuned trail color/amount fields once all types resolve
   (types whose TrailEffect has no effectinfo entry keep the legacy path as fallback).
5. On removal keep the existing detach-and-linger behavior.
Verify: rocket = grey smoke + orange fire core + occasional bright sparks; crylink = tight purple
double-layer plasma; electro = blue plasma; via `--host` fire-run + screenshots; check trail density
roughly constant per-distance when projectile speed differs (compare mortar vs crylink).

### T6 — temporal sizeincrease + per-particle atlas cell (custom draw-pass shader)
Riskiest task; do last, keep behind small diffs and screenshot every step. Replace the draw-pass
`StandardMaterial3D` in `BuildInfoMesh` with a hand-written spatial `ShaderMaterial` (one template,
3 blend variants via `render_mode blend_add|blend_mix|blend_sub`) that reproduces what we use today:
unshaded, billboard with keep-scale (use the standard Godot billboard snippet with
`MODEL_MATRIX` scale extraction), `ALBEDO/ALPHA = texture(albedo, UV) * COLOR` (COLOR = per-particle
vertex color from the process material — ramps keep working).
Then add what StandardMaterial3D can't do:
- **Growth**: uniform `float grow_ratio` (= endEdge/startEdge from `info.SizeIncrease*2*life` math,
  can be < 1 for shrink); in vertex: `VERTEX.xy *= mix(1.0, grow_ratio, INSTANCE_CUSTOM.y)`
  (INSTANCE_CUSTOM.y = lifetime phase from ParticleProcessMaterial). Then revert ScaleMin/Max to the
  SPAWN size only (`sizeMin..sizeMax`, drop the baked span at EffectSystem.cs:1038-1041). This sidesteps
  godot#75748 because no scale curve is involved.
- **Per-particle random cell**: instead of cropping one cell, pass the FULL atlas + a
  `uniform vec4 cell_rects[8]` (the block's [tex0,tex1) range UVs from ParticleFont's table, cap 8) and
  pick `int idx = int(float(N) * fract(INSTANCE_CUSTOM.x * 12.9898))` (CUSTOM.x = per-particle angle…
  if angle is in use, instead enable `ParticlesAnimHFrames = N, AnimOffsetMin=0, AnimOffsetMax=1,
  ParticlesAnimLoop=false, speed 0` so INSTANCE_CUSTOM.z carries a stable per-particle random and use
  that). Verify each blend mode renders (the previous sprite-sheet attempt went invisible under
  StandardMaterial3D — with our own shader we control the path, but PROVE it with --fx-demo
  screenshots for: explosion fire, smoke, sparks, blood, decal-less bursts).
If any variant misbehaves, fall back per-feature (growth without cell variety is still a big win —
nex_impact ring, muzzle flash pop).

### T7 — optional polish
- TE_TEI_G3 `type beam` (vaporizer, cl_particles_oldvortexbeam): render as DP HBEAM — a single quad
  strip start→end textured with atlas beam cell 200 (`ParticleFont.Cell(200)`), world width = 2×size
  (8u), additive, alpha 0.5 fading per `alpha 128 128 256` (≈0.5 s), texcoord u = distance/64.
  Today's CrossRibbon+glow-gradient is recognizable but not stock. Touch `BeamRenderer` or route via
  the same MeshInstance path it uses.
- Arc beam: confirm the continuous EFFECT_ARC_BEAM trail is emitted every frame while firing
  (reference draws trailparticles per frame, qcsrc arc.qc W_Arc_Beam_Think) — with T1 the per-frame
  trail call becomes correct; check the port's Arc emits it per tick, not once.
- Heuristic `velocityjitter` per-axis fidelity (EffectSystem.cs:966-980): acceptable approximation;
  only revisit if vertical-biased sprays (damage_vortex `100 100 300`) still read wrong after T1–T6.

## Validation checklist (after each task)
1. `dotnet build XonoticGodot.csproj` + `dotnet test tests/XonoticGodot.Tests` (from Windows dotnet,
   per RUNNING.md/memory).
2. `--fx-demo` screenshots: explosion + impact + (new) trail demo; boot log still prints
   `[EffectSystem] effectinfo: 301 effects`.
3. A/B against stock: run native Xonotic (RUNNING-xonotic.md) same map/spot, compare: vortex shot
   across the hall, rocket trail arc, laser impact spray, machinegun wall sparks.
4. Perf: firefight smoke test — no per-frame Resource allocation regressions (trail point textures are
   per-CALL for one-shot trails: acceptable; projectile-trail materials cloned once per projectile).
