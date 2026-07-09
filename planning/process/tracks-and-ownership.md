# Process — Parallel Tracks & Ownership

Work is organized into **tracks** — parallel workstreams that recur across phases. The phased TODO
([`../legacy/todo/`](../legacy/todo/)) splits each phase into sections by these tracks. Assign an owner per track.

## The tracks

| Track | Code | Scope | Skills | Maps to projects |
|---|---|---|---|---|
| **Infrastructure & Tooling** | **I** | Solution/build/CI, source generators, the mechanical-assist transpiler, golden-trace harness, profiling | C#/Roslyn, build eng | `SourceGen`, `Tests`, build |
| **Assets** | **A** | pk3 VFS, Q3 `.shader` compiler, IBSP loader, MD3/IQM/DPM importers, texture/sound/font glue | graphics, binary formats, Godot rendering | `Assets`, `Engine.Vfs` |
| **Engine Runtime** | **E** | Facade services, 72 Hz sim core, MOVETYPE integrators, collision/trace, skeleton/tag, particles | systems, physics, numerics | `Engine` |
| **Gameplay** | **G** | The framework port + the data-driven content: weapons, items, gametypes, mutators, monsters, turrets, vehicles | gameplay, C# | `Common` |
| **Networking** | **N** | Transport, protocol, prediction/reconciliation, interpolation, lag-comp, dedicated server | netcode, distributed systems | `Net`, `Server` |
| **Client & UI** | **U** | CSQC client, HUD, view/camera, input, and the Menu | UI, gameplay-client, Godot | `Client`, `Menu` |

## Dependency map (who unblocks whom)

```
I (infra, generators, harness) ──┬─▶ A (assets)
                                 ├─▶ E (engine runtime)
                                 └─▶ G (gameplay framework)
A (shader→material, BSP, models) ──▶ E (collision needs brushes) ──▶ U (render a map/player)
E (facade + sim + collision) ──▶ G (gameplay runs on the facade) ──▶ N (server runs gameplay)
G (entity/message/stat defs) ──▶ N (serializers, prediction)
U (Menu) runs largely independently from Phase 1.
```

## Critical path

`I → A(shader+BSP) → E(collision+sim) → G(framework+first weapon) → N(prediction)` is the spine. The big
**Gameplay fan-out** (G) and the **full HUD/Menu** (U) parallelize widely once the spine exists (Phase 4).

## Parallelization guidance

- **Phase 0–1:** I leads; A and E spike in parallel; U builds a placeholder controller. N is dormant (just
  design).
- **Phase 2:** E and G are the heavy tracks (co-developed against the facade); I owns the golden-trace harness;
  U builds a basic HUD; A finishes the first map/model set.
- **Phase 3:** N is the heavy track; E supports (server-side sim); G stabilizes shared state.
- **Phase 4:** G fans out (parallelizes with headcount — this is where the mechanical-assist transpiler pays
  off); U does the full HUD + Menu; A converts the remaining asset set; N handles mapobject replication.
- **Phase 5:** all tracks burn down the long tail (bots, warpzones, effects, server browser, polish).

## Ownership rules

- Each "Very hard" facade/spec item (traces, skeleton/tags, net read/write, particles, PVS, BSP-surface) gets a
  **named owner** and may spawn its own mini-spec.
- Cross-track contracts (the facade interfaces, the net message/serializer formats, the entity base class) are
  **reviewed by both producing and consuming tracks** before they're depended on.
- The fidelity contract (Engine track) and the asset formats (Assets track) are the two areas where "done" means
  **tests green**, not "compiles."
