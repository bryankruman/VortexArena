# ADR-0002 — Target platform: Godot 4 + C#/.NET

**Status:** Accepted

## Context

We need a host engine to replace Darkplaces' renderer, audio, input, windowing, and asset decoding, and a
language for the gameplay rewrite. The requirement names Godot and C#.

## Decision

Target **Godot 4.x (.NET / "Godot Mono" build)** with **C# / .NET 8 (LTS)** for all gameplay, engine-services,
asset, and netcode code. Pin the exact Godot + .NET versions in Phase 0 (see [OPEN-QUESTIONS](../OPEN-QUESTIONS.md) Q2).

## Consequences

- Godot supplies the renderer (Forward+), scene graph, audio mixing, input, font rendering, and texture/sound/font
  decoding for free — the largest single chunk of Darkplaces (~40k LOC of renderer) is *not* ported.
- C# is production-ready on desktop, is the right fit for a large structured codebase, and supports Roslyn
  **source generators** (the basis of [ADR-0003](ADR-0003-source-generators.md)).
- We inherit Godot/C# constraints: **GC discipline required** (see RISK R5), **scene tree is single-threaded**
  (offload heavy parsing/sim to worker threads), and **no stable C# web export** (see
  [ADR-0012](ADR-0012-platform-scope.md)).
- Custom kinematic movement via `CharacterBody3D` + `PhysicsDirectSpaceState3D` queries is a first-class Godot
  pattern — but for gameplay we use our *own* collision (see [ADR-0004](ADR-0004-deterministic-simulation.md)).
- Headless/dedicated server export is supported.

## Alternatives considered

- **Other engines (Unity, Unreal, bespoke):** out of scope — the requirement specifies Godot. Godot is also a
  proven FPS host (e.g. Cruelty Squad) and is open-source, which suits a GPL game.
- **GDScript instead of C#:** rejected — C# is required, scales better for 200k LOC, and enables source
  generators; GDScript may still be used for thin editor tooling.
