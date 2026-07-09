# Weapon viewmodel render pipeline — Base (DarkPlaces) vs XonoticGodot

Mapped 2026-07-05 (playtest r14, "mortar looks bland") — **facts only, no changes made**. Anchor case:
the MORTAR / grenade launcher (`h_gl.iqm` DPM rig, meshes `grenadelauncher` + `grenadelauncher_sight`,
`scripts/gl.shader`). Both sides traced by dedicated deep-dives with file:line cites; port-side anchors
re-verified by hand. Companion texture set on disk: `grenadelauncher{,_norm,_gloss,_glow,_reflect,_shirt}.tga`
(no `_pants`). **Measured: the diffuse `grenadelauncher.tga` averages ~8/255 — the weapon albedos are
authored nearly BLACK and rely on the engine's light math to read as gunmetal.**

## The two final per-pixel formulas (mortar body)

**DarkPlaces (Xonotic defaults: `vid_sRGB 0`, no realtime world lights, no tonemap):**

```
display ≈ diffuse_tex × Ambient
        + diffuse_tex × Diffuse × max(0, N·L)
        + gloss_tex.rgb × Specular × pow(max(0,N·H), 1 + SpecularPower·gloss_tex.a)
        + glow_tex × r_hdr_glowintensity(1)
        [+ the sight stage, added GL_ONE GL_ONE, animMap frame cycling, rgbGen wave brightness]
```

- `Ambient/Diffuse/Specular` come from **one source: the lightgrid sample at the player origin**
  (`R_CompleteLightPoint` → `Mod_Q3BSP_LightPoint`, r_shadow.c:6014-6142; ambient = grid ambient +
  ~0.5·directed − 0.333·diffuse-projection, diffuse = grid directed, specular = grid directed uncolormodded;
  `L` = the grid's baked light DIRECTION). Values routinely exceed 1.0 (q3map overbright) — the texture is
  **brightened above its authored value** in bright areas.
- **Everything happens in GAMMA space**: textures are NOT sRGB-decoded (`vid_sRGB 0` → `TEXTYPE_BGRA`),
  no tonemap, framebuffer displayed as-is. Light multiplies the gamma-encoded texel directly:
  **display brightness scales LINEARLY with light** (2× light = 2× displayed brightness).
- Viewmodel specifics: depth-range hack (z → [0, 0.0625], gl_rmain.c:6214), lit at the PLAYER's grid cell,
  no shadows received, `r_hdr_scenebrightness 1` post multiplier.

**XonoticGodot (current):**

```
display ≈ sRGB_encode( tonemap_LINEAR(
            PBR( albedo   = sRGB_decode(diffuse_tex) × colormod × entity_tint (+ shirt·shirt_color),
                 normal   = _norm, roughness = 1 − gloss_tex.g, metallic = reflect_mask·0.35 fallback,
                 lights   = Sun(directional −50°/−30°, white, shadows)
                          + SkyAmbient(0.6)
                          + ViewFill Omni(energy 6.0, warm white, camera-local, always on)
                          [+ muzzle light 0.06 s/shot, effect lights] )
            + EMISSION(glow_tex + reflect fallback) ) )
colormod = lightgrid sample at camera: clamp((ambient + 0.5·directed) / mapAverageIntensity, 0.35, 1.7)  (#41)
```

- Material = `PlayerSkinShader` (the `_shirt`/`_reflect` companions route it there; `dpreflectcube`
  deliberately unbound — DP only evaluates it under rtlights, near-invisible at Xonotic defaults).
- **Everything happens in LINEAR space**: `source_color` decodes the albedo (8/255 gamma → ~0.0009 linear!),
  PBR lighting, Linear tonemap, sRGB-encode at output.
- The sight mesh: animated ShaderMaterial, additive + unshaded (#38) — but **only the first `animMap` frame
  is bound (no frame cycling) and `rgbGen wave` is not emitted** (static brightness).

## Ranked divergences (why the mortar reads "bland")

1. **Light-response curve (gamma-space vs linear-space shading).** The sRGB round trip cancels at light≈1.0
   — both engines then show ≈ the authored texel. But light ≠ 1 diverges hard: DP displays `tex × L`
   (linear response, punchy); Godot displays `tex × L^(1/2.2)` perceptually (a 2× light change reads as
   ~1.4×). DP's grid values above 1.0 OVERBRIGHTEN the near-black albedos into visible gunmetal with strong
   spatial contrast; the port's response is compressed around the middle. **This is the structural "punch"
   difference and it affects every model (and arguably the world too).**
2. **What lights the gun.** DP: ONE source — the map's baked grid (colored, directional, position-varying;
   ambient + a directed lobe along the baked light direction + a colored specular from the same sample).
   Port: a fixed WHITE sun (constant direction, can be shadowed indoors), flat sky ambient, and a **6.0-energy
   always-on warm fill light** that dominates up close — uniform illumination regardless of where you stand,
   with the grid reduced to a flat albedo multiplier (#41, capped 1.7 linear ≈ 1.27 perceptual). The gun's
   SHADING STRUCTURE (which faces are lit, what color, how strongly) barely changes across the map.
3. **Specular model.** DP: colored specular = `gloss_tex.rgb × grid_directed × pow(N·H, 1+power·gloss.a)`
   along the BAKED light direction — tight colored glints that track the map lighting. Port: GGX with
   `roughness = 1 − gloss.g` (mortar gloss avg 45/255 → rough ≈ 0.82 = broad dull sheen) from the sun/fill —
   white-ish, soft, directionally unrelated to the map.
4. **The sight overlay.** Port binds only frame 1 of the 3-frame `animMap` and drops `rgbGen wave sawtooth`
   (the blink). Small area, but in Base the sight visibly pulses — motion the port lacks.
5. **Glow term parity is CLOSE** (both add `glow_tex` ≈ ×1) — but under the port's tonemap the emission can
   bloom differently (GlowHdrThreshold 1.0); minor.

## Key default-value table

| Knob | DP (Xonotic default) | Port |
|---|---|---|
| Color space | Gamma (vid_sRGB 0, no decode, no tonemap) | Linear (sRGB decode → Linear tonemap → encode) |
| Model light source | Lightgrid sample at player (RGB ambient+directed+dir) | Sun (white, fixed) + sky ambient 0.6 + ViewFill omni 6.0 + grid colormod (#41) |
| Overbright | Grid values > 1 brighten texels directly | colormod capped 1.7 (linear) on albedo only |
| Specular | gloss.rgb × grid directed, exponent 1+power·gloss.a | GGX, roughness = 1−gloss.g, from sun/fill |
| Glow | + glow × r_hdr_glowintensity(1) | EMISSION = glow (×1) + bloom ≥ 1.0 |
| dpreflectcube | rtlight-permutation only → ~invisible at defaults | unbound by design (matches) |
| Viewmodel depth | depth-range hack [0, 0.0625] | plain child of camera, normal depth |
| Sight animMap/wave | 3-frame cycle + sawtooth blink | first frame only, no wave |

## Candidate experiments (NOT implemented — for discussion)

- **A. Gamma-faithful model shading**: have the skin shader emulate DP's response — e.g. compute the model
  light in gamma space (`ALBEDO = tex × light` with light allowed >1, unshaded-style) instead of PBR-lit
  linear albedo. Biggest single step toward the DP look for models; bypasses divergences 1–3 at once for
  skin-shader surfaces. Cost: models stop reacting to Godot dynamic lights unless folded in manually.
- **B. Real grid lighting instead of the fill/sun stack**: drive per-model ambient+directed uniforms from the
  existing `LightGridData` sample (the #41 sampler already decodes the direction) and retire/attenuate the
  ViewFill + sun influence on the viewmodel — restores position-varying colored shading structure.
- **C. Colored DP-style specular** in the skin shader from the same grid sample (gloss.rgb × directed).
- **D. Sight polish**: animMap frame cycling + rgbGen wave in the animated-stage compiler.
- E. (Rejected earlier, stays rejected: global ACES tonemap — the world shaders are hand-tuned for Linear.)
