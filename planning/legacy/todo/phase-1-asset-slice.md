# Phase 1 — Asset Pipeline Vertical Slice

**Goal:** load Xonotic's existing maps and models for real. **Retires the #1/#2 risks.**
**Exit demo:** **walk around a real Xonotic map** — correct materials, lightmaps, and brush collision — with a
real player model standing in it (weapon attached to its tag).
**Active tracks:** A (heavy), I, E (collision foundation), U (placeholder controller).
Spec: [`../specs/asset-pipeline.md`](../../specs/asset-pipeline.md).

> Build order is dependency-driven: **VFS → shader compiler → BSP → models**.

---

## Track A — Assets (heavy)

### A.1 pk3 Virtual Filesystem
- ☐ Mount `.pk3` archives; gamedir search order; `override/` precedence; extension-search resolver (matches
  Darkplaces `image.c` lookup). Used by importers and the file builtins.
- ☐ Asset-resolver tests (resolve a name to the same file DP would pick).

### A.2 Q3 `.shader` → Godot material compiler  *(the gating dependency — do first)*
- ☐ Parser for `.shader` stages + directives (`Base/.../scripts/*.shader`, ~121 files).
- ☐ Simple opaque → `StandardMaterial3D` with channel-suffix wiring (`_norm`/`_gloss`/`_glow`/`_shirt`/`_pants`/`_reflect`).
- ☐ Multi-pass → `next_pass` chain (blend-mode mapping); or folded `fragment()`.
- ☐ `tcMod` (scroll/scale/rotate) → `.gdshader` UV transforms; `deformVertexes` → vertex displacement.
- ☐ **`surfaceparm` → side-channel flag table** (collision layers, nodraw, sky/fog, lava/clip). Consumed by A.3/A.4.
- ☐ Manual-override table for hero materials (portals/forcefields/sky).

### A.3 IBSP v46 loader  ↳ depends: A.2
- ☐ Full lump parse → `ArrayMesh` surfaces grouped by material; **tessellate bezier patches**.
- ☐ Lightmaps: load pages as textures; BSP lightmap UVs → UV2; custom modulation shader (**bypass LightmapGI**).
- ☐ Entity lump (text) → Godot nodes (spawn points/items/lights/triggers) — the same lump the QC `spawnfunc_*`
  consume.
- ☐ Brushes/Brushsides → collision geometry handed to `[E]` (with `contentflags`).
- ☐ PVS lump: parse + store (use or drop decision per R9).
- ☐ Importer tests (surface/material/entity invariants).

### A.4 Model importers  ↳ depends: A.2
- ☐ **MD3** (vertex-morph) → morph targets/baked anim + **tags** as sockets.
- ☐ **IQM** (skeletal) → `Skeleton3D` + `AnimationLibrary`.
- ☐ **DPM** (skeletal, big-endian) → `Skeleton3D` + animations.
- ☐ Sidecars: `.framegroups` (anim ranges), `.skin` (material remap + team variants + tag alias), `.sounds`.
- ☐ Offline path → glTF/`.tscn`; runtime C# path over the VFS ([ADR-0006](../../decisions/ADR-0006-asset-pipeline.md)).

### A.5 Native assets glue
- ☐ Texture (.tga/.jpg/.png) import + the channel-suffix → material semantics.
- ☐ Sound (.ogg/.wav) import + loop/`cdtrack` glue. Font (.ttf/.otf) import + Type1 substitution.

## Track E — Engine (collision foundation)
- ☐ Promote the Spike-C `tracebox` to a real **AABB-vs-brush** sweep over a full BSP brush set.
- ☐ Add the **area-grid** broadphase; `setorigin` relink. ↳ unblocks U's controller.
- ☐ Feed A.3 brush data + `surfaceparm`/`contentflags` into the collision world.
- ☐ Grow the **golden-trace corpus** to cover real maps (brush, patch, rotated-bmodel). ↳ [I] harness.

## Track I — Infra
- ☐ Asset-conversion CLI (batch offline conversion + a manifest).
- ☐ Wire importer + golden-trace tests into CI.
- ☐ Begin the **mechanical-assist transpiler** (QC parse → flag/scaffold) — used heavily from Phase 2.

## Track U — Client (placeholder)
- ☐ A kinematic player capsule that moves via `[E]`'s trace service (still placeholder movement math) so the map
  is walkable and collision is visibly correct.
- ☐ Load + display a real player model; attach a weapon to its tag (validates R10 end-to-end).

---

## DoD
Load a real Xonotic `.bsp` with correct materials + lightmaps + brush collision; walk it with collision; a real
IQM/DPM player model renders with a weapon on its tag. R1, R2 → "mitigating/retired" in the risk register; golden
traces green on the map corpus.
