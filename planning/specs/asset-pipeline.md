# Spec — Asset Pipeline

Implements [ADR-0006](../decisions/ADR-0006-asset-pipeline.md). Loads Xonotic's existing Quake-format assets into
Godot. Format ground truth: `Base/darkplaces/model_*.c`, `model_q3bsp.h`, `bspfile.h`, `model_shared.c:47-63`
(magic-byte dispatch table).

## Format inventory (on disk)

| Class | Formats & counts | Native Godot? | Plan |
|---|---|---|---|
| Models | MD3 ×287, IQM ×135, DPM ×40, MDL/ZYM/sprites ×~64 | No | C# importer → ArrayMesh/Skeleton3D/Animation |
| Model sidecars | .skin ×266, .framegroups ×87, .sounds ×21 | parse | parse into materials/anims/sound tables |
| Maps | **IBSP v46** (per-map pk3s) + 150 `.map` sources | No | C# IBSP loader (primary) |
| Map sidecars | .mapinfo ×30, .waypoints ×32, .rtlights ×6 | parse | gametype/title; bot nav; light defs |
| Materials | **Q3 `.shader` ×121** | No | `.shader`→material compiler (**build first**) |
| Textures | .tga ×3470, .jpg ×256, .png ×73 | **Yes** | native import + channel-suffix semantics |
| Sounds | .ogg ×627, .wav ×200, .mp3 ×2 | **Yes** | native import + loop/cdtrack glue |
| Fonts | .ttf/.otf ×6 (+1 .pfb) | **Yes** | native; substitute the Type1 |

## Build order (dependency-driven)

1. **pk3 VFS** — zip mount, gamedir search order, `override/` precedence, extension-search resolver. Used by
   everything below and by the game's file builtins.
2. **Q3 `.shader` compiler** — the gating dependency (BSP + model skins both need it).
3. **IBSP v46 loader** — needs the shader compiler.
4. **Model importers** (MD3, then IQM/DPM) — need the shader/material + texture resolver.
5. **Texture/sound/font** native import + resolver glue (can proceed in parallel from step 1).

## Q3 `.shader` → Godot material compiler

A `.shader` = named material = global directives + ordered **stages**. Translate:

- Simple opaque → `StandardMaterial3D` (albedo + `_norm`→normal, `_gloss`→roughness, `_glow`→emission,
  `_shirt`/`_pants`→team-color masks via a custom shader, `_reflect`→reflection).
- **Multi-pass stages** → material `next_pass` chain with blend modes: `GL_ONE GL_ONE`→`blend_add`,
  `GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA`→`blend_mix`, `GL_DST_COLOR GL_ZERO`→`blend_mul`. Or fold 2–3 stages into
  one `fragment()`.
- **tcMod** (scroll/scale/rotate/turb) → UV transforms in a `.gdshader` driven by `TIME` (preserve Q3 op order).
- **deformVertexes** (wave/move/bulge) → vertex displacement in `vertex()`; ensure imported brush meshes carry
  enough subdivision (Q3 `tessSize` subdivision is gone).
- **rgbGen/alphaGen** wavefuncs → shader-driven vertex color/alpha.
- **`surfaceparm`** → **side-channel flag table** consumed by the BSP/model importer to set collision layers,
  `nodraw` skips, sky/fog, and content gameplay (lava/slime/clip/playerclip). **These are gameplay — never drop.**

Deliverable: a parser + ~10–15 GDShader templates + a manual-override table for hero materials (portals,
force-fields, sky). Expect manual fix-ups; full auto-translation of the long tail is not a goal.

## IBSP v46 loader

Magic `IBSP`, version **46** (accept 47/48), 17 lumps. Produce:

- **Geometry** (Faces + Vertices + Triangles) → `ArrayMesh` surfaces grouped by material; **tessellate bezier
  patches** (face type 2) to triangles.
- **Lightmaps** (lump 14, 128×128 RGB pages) → load as textures; feed BSP lightmap UVs as **UV2**; modulate in a
  custom shader. **Bypass `LightmapGI`** (it won't ingest precomputed textures). Dynamic relight lost unless
  rebaked (acceptable for parity).
- **Materials** — each texture lump entry is a shader *name* → resolve via the `.shader` compiler.
- **Entities** (lump 0, text) → spawn points, items, lights, triggers → Godot nodes; this is the same entity lump
  the QC `spawnfunc_*` handlers consume (gameplay-critical).
- **Collision** (Brushes + Brushsides) → fed to the collision/trace service (`specs/determinism-and-physics.md`),
  preserving `contentflags`.
- **PVS** (lump 16) — optional; drop in favor of Godot occlusion, or keep to pre-partition (R9).

Study/fork `ballerburg9005/godot-bsp-map-loader` (built for Xonotic/Q3, unfinished GDScript) for reference.

## Model importers (MD3 / IQM / DPM)

- **MD3** (vertex-morph): per-frame xyz (int16) + tags. → morph targets or baked `Animation`; expose **tags** as
  attachment sockets.
- **IQM** (skeletal): joints + poses + named anims (`IQM_LOOP`). → `Skeleton3D` + `AnimationLibrary`; DP packs
  poses to short[7].
- **DPM** (skeletal, big-endian): hierarchical bones, per-frame 3×4 pose matrices. → `Skeleton3D` + animations.
- **Sidecars:** `.framegroups` (firstframe count fps loop → name the animation ranges), `.skin`
  (mesh→material remap + team variants + tag alias), `.sounds` (voice tables).
- **Tags/attachments are load-bearing** (gettaginfo 84, setattachment 87): expose named tags/bones as queryable
  world-transform sockets (`BoneAttachment3D` for skeletal; baked tag table for MD3). The `gettaginfo` facade
  builtin queries these.

Two delivery paths per [ADR-0006](../decisions/ADR-0006-asset-pipeline.md): offline → glTF/`.tscn` (primary),
runtime C# parse (fallback for unmodified assets). **Parse in C#, not GDExtension** (no C#↔GDExtension interop).

## Textures / sounds / fonts

- Textures (.tga/.jpg/.png): native import. Work is the channel-suffix → material wiring and the
  extension-search/`override/` precedence resolver.
- Sounds (.ogg/.wav/.mp3): native import; recreate loop metadata + the `.sounds`/`cdtrack` indirection.
- Fonts (.ttf/.otf): native import; substitute the single Type1 `.pfb`; drop legacy `conchars.tga`.
