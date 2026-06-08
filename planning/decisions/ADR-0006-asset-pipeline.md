# ADR-0006 — Asset pipeline: write C# importers, offline-first + runtime fallback

**Status:** Accepted

## Context

Xonotic ships **compiled** Quake-format assets that Godot cannot import natively: **IBSP v46** maps,
**MD3/IQM/DPM** models, and **Q3 `.shader`** materials, plus `.skin`/`.framegroups`/tag sidecars. The adjacent
community tools target the wrong inputs: Func_Godot/Qodot consume `.map` brush *source* (not BSP), and the only
IBSP importer (`ballerburg9005`) is unfinished GDScript. GDExtension (C++/Rust) cannot be authored in or called
from C# cleanly today. Textures/sounds/fonts (.tga/.jpg/.png, .ogg/.wav, .ttf/.otf) import natively.

## Decision

**Write the importers in C#**, and support **both** offline conversion and runtime loading:

- **Offline-first** (`EditorImportPlugin`-style): convert the shipped asset set to optimized Godot resources
  (`.tscn`/`.glb`/`.tres`) for fast load and parity. Primary path for the known asset set.
- **Runtime loader** (`ResourceFormatLoader`-style over the pk3 VFS): parse the binaries in C# at runtime to
  satisfy "load existing models and maps unmodified."
- **Build the Q3 `.shader`→Godot material compiler first** — both the BSP loader and model skins depend on it.
  Split `surfaceparm` gameplay flags into a side-channel table consumed by the collision/import layer.
- Provide a **pk3 (zip) virtual filesystem** with Darkplaces' search order and `override/` precedence, used by
  both importers and the game's file builtins.

## Consequences

- This is the largest concrete line item and top risk (R1, R2); retired by the Phase 1 vertical slice.
- Parsing in C# (not GDExtension) keeps everything in the managed codebase at some perf cost — acceptable, and
  offline conversion removes runtime cost for the shipped set.
- Lightmaps: bypass `LightmapGI` (it won't ingest precomputed textures) — load Q3 lightmaps as textures, feed
  BSP lightmap UVs as UV2, modulate in a custom shader. Dynamic relighting is lost unless rebaked (acceptable).
- Tags/attachments must be exposed as queryable sockets (R10) — they are gameplay, not cosmetics.

## Alternatives considered

- **Convert `.map` sources via Func_Godot/Qodot:** partial — doesn't cover maps shipped only as BSP and loses
  precomputed lightmaps/PVS. Usable for *some* maps; not the primary plan.
- **GDExtension (C++/Rust) parsers consumed by C#:** rejected — no clean C#↔GDExtension interop today.
- **Re-author all assets natively in Godot:** rejected for v1 — defeats "load existing assets"; revisit per
  [OPEN-QUESTIONS](../OPEN-QUESTIONS.md) Q10.
