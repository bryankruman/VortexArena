# Graphics Settings Audit — XonoticGodot

**Date:** 2026-06-14
**Scope:** Every graphics/quality cvar in the Video & Effects menus — Godot wiring status, current
defaults, and recommended Low/Med/High/Ultra preset values, plus future Godot 4.6 features and
recommended improvements.
**Method:** Cvar-by-cvar wire-check; every reader traced to `file:line` and adversarially re-verified.
Reference original: `../Base/data/xonotic-data.pk3dir/effects-*.cfg` (Darkplaces/QC).

---

## The core finding

The **Video** (`game/menu/dialogs/DialogSettingsVideo.cs`) and **Effects**
(`game/menu/dialogs/DialogSettingsEffects.cs`) settings tabs are faithful C# ports of the Darkplaces
QC dialogs — every widget binds the *exact* original cvar. But **binding ≠ wiring**. Only a small set
of cvars are actually *read and applied to Godot's renderer*; the rest are
**registered-for-visibility stubs** or **dialog-only widgets** with no reader. The visual quality the
game actually renders is mostly **hardcoded** in `NetGame.AddLight()` and `project.godot`, not driven
by any preset.

**Legend (Wired? column):**

- ✅ **Wired** — cvar is read and drives a real Godot rendering change
- 🟡 **Partial** — read, but the effect is incomplete or map-driven
- 🔌 **Needs wiring** — Godot *can* do this today; the cvar is dead or the value is hardcoded/static
- 📌 **Static** — fixed in `project.godot`, cvar dead
- ⛔ **Not implemented** — no Godot render path exists yet → see *Future Features*

> **Correction baked in:** `project.godot` `msaa_3d=2` is **MSAA 4×** (Godot enum: 2 = MSAA_4X), and
> `anisotropic_filtering_level=3` is **8×**.

---

## Table 1 — Core graphics settings (features we support today)

### 1a. Display & framerate

| Setting | Cvar(s) | Godot mechanism | Wired? | Default now | Low | Med | High | Ultra |
|---|---|---|---|---|---|---|---|---|
| Resolution | `vid_width/height` | `DisplayServer.WindowSetSize` | ✅ | Window size | Native↓ | Native | Native | Native |
| Window mode | `vid_fullscreen` (0/1/2) | `WindowSetMode` (incl. exclusive) | ✅ | Borderless FS (1) | Excl (2) | Excl (2) | Borderless (1) | Borderless (1) |
| Borderless | `vid_borderless` | `WindowSetFlag(Borderless)` | ✅ | Off | — | — | — | — |
| Vertical sync | `vid_vsync` (0/1/2/3) | `WindowSetVsyncMode` | ✅ | Mailbox (2) | Off (0) | Mailbox (2) | Mailbox (2) | Mailbox (2) |
| Max FPS | `cl_maxfps` | `Engine.MaxFps` | ✅ | Unlimited (0) | 144 | 250 | 500 | 0 (unlim) |
| Target/min FPS | `cl_minfps` | — (no adaptive-quality loop) | ⛔ | Dead | — | — | — | — |
| Idle FPS cap | `cl_maxidlefps` | — (no focus handler) | ⛔ | Dead | 32 | 60 | 60 | 0 |
| Process priority | `sys_priority_boost` | `Process.PriorityClass` | ✅ | AboveNormal (1) | 1 | 1 | 1 | 1 |
| Pixel height / aspect | `vid_pixelheight` | — | ⛔ | Dead (staged only) | — | — | — | — |

### 1b. Anti-aliasing & sharpness

| Setting | Cvar(s) | Godot mechanism | Wired? | Default now | Low | Med | High | Ultra |
|---|---|---|---|---|---|---|---|---|
| MSAA | `vid_samples` | `Viewport.Msaa3D` | 📌 **4× fixed** | 4× (`project.godot:25`) | Off | 2× | 4× | 8× |
| Anisotropic filtering | `gl_texture_anisotropy` | `anisotropic_filtering_level` | 📌 **8× fixed** | 8× (`project.godot:24`) | 2× | 4× | 8× | 16× |
| High-quality framebuffer | `r_viewfbo` | — (Forward+ always HQ) | ⛔ | Dead | — | — | — | — |
| GLSL shaders | `vid_gl20` | — (always GLSL) | ⛔ | Always on | — | — | — | — |

> **All AA is currently non-adjustable** — 4× MSAA is hardwired in `project.godot:25`, anisotropy at
> level 3 (8×) in `:24`. There is **no TAA, FXAA, or FSR** (those are Future Features). The
> `vid_samples`/`gl_texture_anisotropy` sliders are dead.

### 1c. Post-processing

| Setting | Cvar(s) | Godot mechanism | Wired? | Default now | Low | Med | High | Ultra |
|---|---|---|---|---|---|---|---|---|
| Bloom / Glow | `r_bloom` | `Environment.GlowEnabled` | 🔌 **Hardwired ON** | On, intensity 0.8 (`NetGame.cs:3981`) | Off | On (0.6) | On (0.8) | On (1.0) |
| Tonemap | `r_hdr_scenebrightness` | `Environment.TonemapMode` | 🔌 **Hardwired Linear** | Linear (`NetGame.cs:3976`) | Linear | Linear | Linear | Linear / ACES |
| Map fog | worldspawn `fog` key | `Environment.FogEnabled` | 🟡 Map-driven | On iff map declares it | map | map | map | map |
| Motion blur | `r_motionblur` | needs `CompositorEffect` | ⛔ | Dead | — | — | — | — |
| Damage/underwater blur | `hud_postprocessing_maxbluralpha` | needs post-process pass | ⛔ | Dead (not ported) | — | — | — | — |
| Powerup screen FX | `hud_powerup` | — | ⛔ | Dead | — | — | — | — |
| Anaglyph 3D | `r_stereo_redcyan` | needs stereo path | ⛔ | Dead | — | — | — | — |

> Damage flash + liquid screen-tint *do* work (`ViewEffects.cs` → full-screen `ColorRect` overlays,
> `hud_damage*`/`hud_contents_*` ✅), but the **GLSL blur/sharpen** post-process behind
> `hud_postprocessing` was deliberately not ported.

### 1d. Brightness & color

| Setting | Cvar(s) | Godot mechanism | Wired? | Default now | Low | Med | High | Ultra |
|---|---|---|---|---|---|---|---|---|
| Brightness/black | `v_brightness` | `Environment.AdjustmentBrightness` | ⛔ | Dead | — | — | — | — |
| Contrast | `v_contrast` | `AdjustmentContrast` | ⛔ | Dead | — | — | — | — |
| Gamma | `v_gamma` | post gamma | ⛔ | Dead | — | — | — | — |
| Saturation | `r_glsl_saturation` | `AdjustmentSaturation` | ⛔ | Dead | — | — | — | — |
| Ambient light | `r_ambient` | `AmbientLightEnergy` | 🔌 **Hardwired 0.6** | 0.6 (`NetGame.cs:3969`) | — | — | — | — |
| Scene intensity | `r_hdr_scenebrightness` | `tonemap_exposure` | ⛔ | Dead | — | — | — | — |
| Map/world tint | `r_map_tint` `_strength` | global shader param `map_tint` | ✅ | Off (identity) | — | — | — | — |
| Entity/scene tint | `r_scene_tint` `_strength` | global shader param `entity_tint` | ✅ | Off (identity) | — | — | — | — |

### 1e. Lighting & shadows → see **Table 3**

| Setting | Cvar(s) | Wired? | Default now |
|---|---|---|---|
| Sun (directional) shadows | — (no cvar) | ✅ on, **default quality** | One `DirectionalLight3D`, shadows on, no quality tuned |
| Realtime dynamic lights | `r_shadow_realtime_dlight` | ⛔ **always-on, not gated** | Explosions/rockets/projectiles spawn `OmniLight3D` unconditionally |
| Dynamic-light shadows | `r_shadow_realtime_dlight_shadows` | 🔌 | Off (all dynamic lights `ShadowEnabled=false`) |
| Realtime world rtlights | `r_shadow_realtime_world` | ⛔ | Baked lightmaps only (unshaded) |
| Soft shadows | `r_shadow_shadowmapping` | 🔌 | Godot default filter |
| Gloss / specular | `r_shadow_gloss` | ⛔ (data-driven) | On wherever a `_gloss` texture exists |
| Normal maps | `r_shadow_usenormalmap` | ⛔ (data-driven) | On wherever a `_norm` texture exists |
| Deluxe mapping | `r_glsl_deluxemapping` | ⛔ (BSP-driven) | On iff map is deluxemapped |
| Lightmaps | `mod_q3bsp_nolightmaps` | ⛔ | Always on |
| Coronas / flares | `r_coronas`, `gl_flashblend` | ⛔ | Not implemented |
| Depth prepass | `r_depthfirst` | ⛔ | Forward+ does its own |

### 1f. Textures & geometry → see **Table 4**

| Setting | Cvar(s) | Wired? | Default now |
|---|---|---|---|
| Texture resolution (picmip) | `gl_picmip` | 🔌 (import-time) | Full res, no mip-skip |
| Texture compression | `gl_texturecompression*` | ⛔ | Uncompressed runtime upload |
| Geometry/curve detail | `r_subdivisions_tolerance` | 🔌 **fixed 8** | Bézier patches at 8 subdivisions |
| Player model detail (LOD) | `cl_playerdetailreduction` | 🟡 **computed then discarded** | Always lod0 (LOD index computed, never applied) |
| Offset / relief mapping | `r_glsl_offsetmapping*` | ⛔ | Parsed but no shader consumes it |
| Show surfaces (debug) | `r_showsurfaces` | ⛔ | Dead (menu gate only) |

### 1g. Effects detail & culling → see **Table 2** (particles/decals) and **Table 5** (culling)

| Setting | Cvar(s) | Wired? | Default now |
|---|---|---|---|
| Particles master | `cl_particles` | ✅ | On |
| Particle quality | `cl_particles_quality` | ✅ (faithful backend only) | 1.0× |
| Particle renderer | `cl_particles_modern` | ✅ | Faithful (0) |
| Particle draw distance | `r_drawparticles_drawdistance` | ✅ | 2000 qu |
| Decals master | `cl_decals` | ⛔ **dead — can't disable decals** | Always on |
| Decal fade time | `cl_decals_fadetime` | ⛔ **hardcoded 2 s** | 2 s |
| Decal draw distance | `r_drawdecals_drawdistance` | ⛔ (`DistanceFadeEnabled=false`) | No distance cull |
| Damage effects | `cl_damageeffect` | ⛔ | Dead |
| Gibs | `cl_nogibs` | ⛔ | Static cap |
| PVS culling | `r_pvs_cull` | ✅ | On |
| Occlusion culling | `r_occlusion_cull` | ✅ | Off |
| Reflections (water) | `r_water` | ⛔ | No reflection feature exists |
| Show sky | `r_sky` | ⛔ | Sky always shown |

### 1h. View & overlay (wired, mostly working)

| Setting | Cvar(s) | Wired? | Default now |
|---|---|---|---|
| FOV / zoom | `fov`, `cl_zoomfactor/speed/sensitivity` | ✅ | `Camera3D.Fov` |
| Vignette | `cl_vignette*` | ✅ | Shader uniform overlay |
| Zoom reticle | `cl_reticle*` | ✅ | `TextureRect` overlay |
| Crosshair | `crosshair_size/alpha/dot/ring/hittest…` | ✅ | 2D draw |
| Eye-height smoothing | `cl_smoothviewheight` | ✅ | 0.05 |
| Damage flash / liquid tint | `hud_damage*`, `hud_contents_*` | ✅ | `ColorRect` overlay |
| Event-chase (death cam) | `cl_eventchase_death/distance/speed` | 🟡 | Wired; `viewoffset/mins/maxs` hardcoded |
| Third-person chase | `chase_active/back/up` | ⛔ | Unreachable in code |
| View bob | `cl_bob*`, `cl_rollangle` | ⛔ | Not implemented |
| Gun sway / align / offset | `cl_followmodel/leanmodel/gunalign/gunoffset` | ⛔ | Fixed `[Export]` defaults |
| Draw viewmodel | `r_drawviewmodel` | ⛔ | Always drawn (unless dead/chase) |

---

## Table 2 — Particles & decals preset matrix (multi-cvar)

Reference column = Xonotic Base `effects-*.cfg`. "Wired" column = whether the value actually does
anything today.

| Cvar | Wired? | Low | Med | High | Ultra | Base ref (low→ultra) |
|---|---|---|---|---|---|---|
| `cl_particles` | ✅ | 1 | 1 | 1 | 1 | 1 / 1 / 1 / 1 |
| `cl_particles_quality` | ✅ (faithful) | 0.4 | 0.8 | 1.0 | 1.0 | 0.4 / 0.8 / 1.0 / 1.0 |
| `cl_particles_modern` | ✅ | 0 | 0 | 0 | 0 (or 2) | n/a (port-only) |
| `cl_particles_sdf_generate` | ✅ (modern only) | 0 | 0 | 0 | 1 | n/a |
| `r_drawparticles_drawdistance` | ✅ | 500 | 1000 | 1500 | 2000 | 500 / 750 / 1500 / 2000 |
| `cl_spawn_point_particles` | ✅ | 0 | 0 | 1 | 1 | 0 / 0 / 1 / 1 |
| `cl_spawn_event_particles` | ⛔ unconditional | 0 | 0 | 1 | 1 | 0 / 0 / 1 / 1 |
| `cl_damageeffect` | ⛔ | 0 | 0 | 1 | 2 | 0 / 0 / 1 / 2 |
| `cl_decals` | ⛔ **dead** | 1 | 1 | 1 | 1 | 1 / 1 / 1 / 1 |
| `cl_decals_models` | ⛔ | 0 | 0 | 0 | 1 | 0 / 0 / 0 / 1 |
| `cl_decals_fadetime` | ⛔ **hardcoded 2** | 2 | 2 | 4 | 10 | 2 / 2 / 4 / 10 |
| `r_drawdecals_drawdistance` | ⛔ | 200 | 300 | 500 | 500 | 200 / 300 / 500 / 500 |

---

## Table 3 — Lighting & shadows preset matrix (multi-cvar)

Today only the sun casts shadows, at Godot defaults. These are the values to wire once shadow quality
is exposed (repurpose the dead `r_shadow_*` cvars + `project.godot` shadow keys).

| Setting → Godot API | Wired? | Low | Med | High | Ultra |
|---|---|---|---|---|---|
| Directional shadow atlas → `directional_shadow/size` | 🔌 | 1024 | 2048 | 4096 | 8192 |
| Shadow splits → `DirectionalShadowMode` | 🔌 | Orthogonal | 2-split | 4-split (PSSM) | 4-split |
| Shadow max distance (qu) → `shadow_max_distance` | 🔌 | ~2000 | ~4000 | ~8000 | ~16000 |
| Soft shadow filter → `soft_shadow_filter_quality` | 🔌 (was `r_shadow_shadowmapping`) | Hard (0) | Low (1) | High (3) | Ultra (4) |
| Dynamic-light shadows → `OmniLight3D.ShadowEnabled` | 🔌 (was `r_shadow_realtime_dlight_shadows`) | Off | Off | On | On |
| Ambient energy → `AmbientLightEnergy` | 🔌 (hardwired 0.6) | 0.6 | 0.6 | 0.6 | 0.6 |
| Realtime world rtlights | ⛔ build needed | Off | Off | Off | Off |
| Coronas/flares | ⛔ build needed | 0 | 0 | 1 | 1 |

---

## Table 4 — Textures & geometry preset matrix (multi-cvar)

| Setting → Godot API | Wired? | Low | Med | High | Ultra |
|---|---|---|---|---|---|
| Anisotropy → `anisotropic_filtering_level` | 📌 8× fixed | 2× | 4× | 8× | 16× |
| Curve subdivisions → `BezierPatch.Subdivisions` | 🔌 8 fixed | 4 | 6 | 8 | 12 |
| Player LOD → `cl_playerdetailreduction` (+ real mesh swap) | 🟡 discarded | 4 | 3 | 1 | 0 |
| Mesh LOD bias → `Viewport.MeshLodThreshold` | 🔌 (default 1.0) | 8.0 | 4.0 | 1.0 | 0.0 |
| Texture resolution → import mip-skip | 🔌 import-time | −1 mip | full | full | full |

---

## Table 5 — Culling & performance preset matrix (mostly wired ✅)

| Cvar | Wired? | Low | Med | High | Ultra | Notes |
|---|---|---|---|---|---|---|
| `r_pvs_cull` | ✅ | 1 | 1 | 1 | 1 | Shipping cull path; default 1 |
| `r_pvs_cull_entities` | ✅ | 1 | 1 | 1 | 1 | Default 1 |
| `r_pvs_cull_entities_margin` | ✅ | 64 | 64 | 64 | 64 | qu half-extent |
| `r_occlusion_cull` | ✅ | 1 | 1 | 0 | 0 | On at Low/Med for max perf; PVS-only at High/Ultra (default 0) |
| `r_world_cell_adaptive` | ✅ | 1 | 1 | 0 | 0 | Adaptive sizing for big maps |
| `r_world_cell_size` | ✅ | 1024 | 1024 | 1024 | 512 | Smaller = finer cull, more draws |
| `cl_pose_cull` | ✅ | 1 | 1 | 1 | 0 | Skeletal off-screen skip; default 1 |
| `cl_pose_cull_distance` | ✅ | 1000 | 1500 | 2000 | 3000 | qu |
| `sv_cullentities_pvs` | ✅ | 1 | 1 | 1 | 1 | Server-side relevance (networking) |

---

## Table 6 — Future features (Godot 4.6 Forward+ we *could* add)

These need new render-side code (none exist today). Values tuned for a fast competitive arena FPS
(clarity/perf-biased).

| Feature | Godot API | Proposed cvar | Low | Med | High | Ultra |
|---|---|---|---|---|---|---|
| **TAA** | `Viewport.UseTaa` | `r_taa` | 0 | 0 | 1 | 1 |
| **FXAA** | `Viewport.ScreenSpaceAA=Fxaa` | `r_fxaa` | 1 | 0 | 0 | 0 |
| **FSR 2.2 upscale** | `Scaling3DMode=Fsr2` + scale | `r_scaling_mode`+`r_scaling_scale` | FSR2 @ 0.5 | FSR2 @ 0.67 | FSR2 @ 0.77 | Off (1.0) |
| **FSR 1 spatial** | `Scaling3DMode=Fsr` | `r_scaling_mode` | FSR1 @ 0.59 | FSR1 @ 0.77 | Off | Off |
| **Render scale / SSAA** | `Scaling3DMode=Bilinear` + scale | `r_renderscale` | 0.75 | 1.0 | 1.0 | 1.25 (SSAA) |
| **SSAO** | `Environment.SsaoEnabled` | `r_ssao` | 0 | 0 | 1 | 1 (Ultra qual) |
| **SSIL** | `Environment.SsilEnabled` | `r_ssil` | 0 | 0 | 0 | 1 |
| **SSR** | `Environment.SsrEnabled` | `r_ssr` | 0 | 0 | 0 | 1 |
| **SDFGI (GI)** | `Environment.SdfgiEnabled` | `r_gi` | 0 | 0 | 0 | 1 (4 casc.) |
| **Volumetric fog** | `Environment.VolumetricFogEnabled` | `r_volfog` | 0 | 0 | 0 | 1 |
| **Depth of field** | `Environment.DofBlur*` | `r_dof` | 0 | 0 | 0 | 0 (cinematics only) |
| **Auto-exposure** | `CameraAttributesPractical.auto_exposure` | `r_autoexposure` | 0 | 0 | 0 | 0 (opt-in) |
| **Lens flare** | `CompositorEffect` (no native API) | `r_lensflare` | 0 | 0 | 0 | 1 |
| **Debanding** | `Viewport.UseDebanding` | `r_debanding` | 0 | 1 | 1 | 1 |
| **Variable rate shading** | `Viewport.VrsMode` | `r_vrs` | 1 | 0 | 0 | 0 |
| **Motion blur** | `CompositorEffect` | `r_motionblur` (wire) | 0 | 0 | 0 | 0.4 |
| **Water planar reflections** | `ReflectionProbe`/SubViewport | `r_water` (wire) | 0 | 0 | 1 | 1 |

---

## Table 7 — Recommended improvements to existing graphics features

| # | Area | Current state | Recommended improvement | Priority |
|---|---|---|---|---|
| 1 | **Decals can't be turned off** | `cl_decals` master const has **zero usages** — the toggle is dead | Wire `cl_decals` to gate `DecalSplats`/`Decals` spawning; restore `cl_decals_fadetime` (hardcoded 2 s) and `r_drawdecals_drawdistance` (`DistanceFadeEnabled=false` → enable distance fade) | **High** (correctness) |
| 2 | **Bloom is forced on, untunable** | `GlowEnabled=true`, intensity 0.8, `GlowBloom 0.05`, `GlowHdrThreshold 1.0` hardcoded (`NetGame.cs:3981`) | Wire `r_bloom` → `GlowEnabled` so competitive players can disable it; expose intensity. With **Linear** tonemap + threshold 1.0 only true-HDR pixels bloom, so it reads subtle — consider `GlowHdrThreshold ≈ 0.9` and tuning `GlowBloom`/`GlowLevels` for a stronger-but-controllable look | **High** |
| 3 | **MSAA & anisotropy are static** | Fixed 4× / 8× in `project.godot`; sliders dead | Read `vid_samples`→`Viewport.Msaa3D` and an anisotropy cvar in `ClientSettings.ApplyVideo`; lets weak GPUs drop AA and strong ones raise it | **High** |
| 4 | **Player LOD computed then discarded** | `cl_playerdetailreduction` read at `ClientWorld.cs:1199` → index computed → `_ =` thrown away, always lod0 | Either resolve/hot-swap the `_lodN` meshes, or drive Godot `VisibilityRange`/`MeshLodThreshold` from it so the setting actually reduces detail | **Medium** |
| 5 | **No debanding** | Off | Enable `Viewport.UseDebanding` (≈free) — removes visible banding in the fog/sky gradients the Linear pipeline produces | **Medium** (cheap win) |
| 6 | **Add a master render-scale / FSR lever** | Native 1.0 only | The single biggest GPU-bound perf knob; wire `Scaling3DMode`+`scale` (FSR2 at low tiers, SSAA at Ultra). Far more impactful than the dead picmip slider | **Medium** |
| 7 | **Directional shadow quality untuned** | Sun shadows on at Godot defaults (4096 atlas, default filter/distance) | Expose `directional_shadow/size`, `soft_shadow_filter_quality`, `shadow_max_distance` per tier (Table 3); current defaults can flicker on large maps and waste atlas on small ones | **Medium** |
| 8 | **Ambient/brightness/gamma all dead** | `r_ambient`/`v_*`/`r_hdr_scenebrightness` do nothing; ambient hardwired 0.6 | Wire brightness/contrast/saturation → `Environment.Adjustment*` and `r_hdr_scenebrightness` → `tonemap_exposure`/`AmbientLightEnergy`; otherwise hide the dead sliders so the menu isn't misleading | **Medium** |
| 9 | **SSAO for grounding** | None | Add `Environment.SsaoEnabled` at High/Ultra — cheap depth/contact grounding that markedly improves prop/corner readability without hurting competitive clarity | **Low** |
| 10 | **Tonemap locked to Linear** | Hardcoded (`NetGame.cs:3976`, with an inline ACES note) | Expose `r_tonemap` (Linear faithful / ACES / AgX) as an Ultra "modern look" option | **Low** |
| 11 | **Dynamic lights cast no shadows & aren't gated** | Explosion/rocket `OmniLight3D`s always spawn, `ShadowEnabled=false` | Gate count via `r_shadow_realtime_dlight`; optionally enable shadows on the brightest few at High/Ultra | **Low** |
| 12 | **Menu honesty** | Effects/Video tabs imply ~30 working controls; most are inert | After wiring the high-value ones, hide or grey the genuinely unsupported cvars (anaglyph, offset mapping, coronas, world rtlights) so presets reflect reality | **Low** |

---

## Bottom line

**Genuinely wired today:** window/vsync/fps, particles (+quality/distance/modern), PVS & occlusion
culling, world-cell sizing, pose-cull, color tint, vignette/reticle/crosshair/FOV/damage-flash.

**Hardwired (works but untunable):** 4× MSAA, 8× anisotropy, glow/bloom, Linear tonemap, one
shadow-casting sun, map fog, ambient 0.6.

**Dead stubs:** essentially every other Effects/Video cvar (shadows, lights, water, sky, picmip,
motion blur, brightness/gamma, decal toggles, geometry detail).

The fastest path to real quality presets: items **1–6** in Table 7 convert the existing hardwired
behavior into cvar-driven Low/Med/High/Ultra tiers, then layer in the Future Features (TAA/FSR/SSAO)
for the High/Ultra end.

---

## Current Godot rendering baseline (verified)

- **In-match `WorldEnvironment`** — `NetGame.AddLight()` (`game/net/NetGame.cs:3955-3997`):
  `BackgroundMode=Sky`, `AmbientLightSource=Sky`, `AmbientLightEnergy=0.6`, `TonemapMode=Linear`,
  Glow ON (intensity 0.8 / strength 1.0 / bloom 0.05 / HDR threshold 1.0 / Screen blend), fog only
  via `MapLoader.ApplyFog` when the worldspawn declares it, `WorldTint.ApplyWorldspawn` for
  `map_tint`/`entity_tint` global shader params. **OFF:** SSAO, SSIL, SSR, SDFGI, volumetric fog,
  DOF, auto-exposure, adjustments, lens flare.
- **Lighting/shadows** — one `DirectionalLight3D` "Sun" with `ShadowEnabled=true` (`NetGame.cs:3957`);
  shadow size/quality/splits/distance all at Godot defaults. Other lights `ShadowEnabled=false`.
- **`project.godot` [rendering]** (static) — `anisotropic_filtering_level=3` (8×),
  `anti_aliasing/quality/msaa_3d=2` (4× MSAA), `occlusion_culling/use_occlusion_culling=true`
  (buffer allocated, per-viewport opt-in), `vram_compression/import_etc2_astc=true`. **Not present
  (Godot defaults):** `screen_space_aa` (off), `use_taa` (off), `scaling_3d` (bilinear 1.0 — no
  FSR/render-scale), `use_debanding` (off), mesh LOD threshold (1.0px), VRS (off).
- **Apply layer** — `ClientSettings.ApplyVideo` wires *only* window mode/size/borderless, vsync,
  `cl_maxfps`→`Engine.MaxFps`, `sys_priority_boost`, and audio buses. **No graphics-quality setting
  is wired.**
- The only runtime Viewport rendering property touched anywhere is
  `WorldOcclusion.cs:60` (`vp.UseOcclusionCulling = r_occlusion_cull`, default 0/off).
