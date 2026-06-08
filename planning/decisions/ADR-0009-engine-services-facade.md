# ADR-0009 — The `dpdefs` builtins become a C# engine-services facade

**Status:** Accepted

## Context

The QuakeC reaches the engine through ~890 builtin declarations in `qcsrc/dpdefs/` (≈420 distinct functions
across the server/client/menu VMs). The game already wraps the raw builtins in higher-level helpers
(`lib/net.qh`'s `WriteHeader`/`WriteVector`/`ReadCSQCEntity`, the `MAKE_VECTORS`/`GET_TAG_INFO`/`SKEL_*`
deglobalization macros). This binding layer is the only coupling point between gameplay and engine.

## Decision

Reimplement the builtins as a **C# engine-services facade** in `XonoticGodot.Engine.Services`, and port the gameplay
code to call it. The facade is the single integration seam.

- Group services by concern behind interfaces (e.g. `IEntityService`, `ITraceService`, `ICvarService`,
  `ISoundService`, `IDrawService`, `INetWriter`/`INetReader`, `IModelService`, `IFileService`). Logic in
  `XonoticGodot.Common` depends on these interfaces, not on Godot.
- Prioritize implementation by **hot-call counts** (the report's facade table): strings, entity mgmt, cvars,
  traces, sound, 2D draw, net write/read first.
- Replace globals-mutating builtins (`makevectors` → `v_forward/right/up`) with **return values/structs**
  (the QC already hides these behind macros, easing the move).
- **Skip the confirmed-unused builtins** (ODE physics, CSQC particle-theme spawner, `addentity`/`renderscene`,
  `getlight`, `altstr_*`).

See [`../specs/engine-services-facade.md`](../specs/engine-services-facade.md) for the full catalog.

## Consequences

- The bulk of gameplay ports against a stable, mockable interface — and can be **unit-tested without Godot**.
- The hardest facade items (traces, tags/skeleton, net read/write, particles) are isolated behind their
  interfaces and can be hardened independently (they carry the fidelity risk).
- Menu-only services (host-cache/server-browser) are a separate, deferrable surface.

## Alternatives considered

- **Scatter engine calls throughout the ported code (no facade):** rejected — destroys testability and couples
  `Common` to Godot, and there'd be no clean place to enforce the fidelity contract.
- **Auto-generate the facade from `dpdefs`:** the *signatures* can be scaffolded mechanically, but the bodies are
  hand-written; treat dpdefs as the spec, not the implementation.
