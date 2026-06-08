# Process — Coding Standards & QC→C# Conventions

Conventions specific to this port. General C# style (naming, async, nullability) follows standard .NET
guidelines; this doc covers the Xonotic-specific translation rules and the performance discipline.

## QC → C# idiom map

| QuakeC | C# | Note |
|---|---|---|
| `.float health;` (entity field) | `public float Health;` on the owning class | Resolve the flat namespace per [entity-model spec](../specs/entity-model.md). |
| `vector v; v_x/v_y/v_z; '1 0 0'` | `Vector3` (Godot) / `System.Numerics.Vector3` | QC `*` on two vectors = **dot**; `><` = cross — translate operators carefully. |
| `string s; strzone(s); strunzone(s)` | `string s;` | Delete zone/unzone; GC owns lifetime. |
| `self` / `other` | `this` / explicit param | QC mostly migrated already. |
| `entity e = spawn(); ... remove(e);` | `new`/pooled `Entity`; explicit free/return-to-pool | See pooling below. |
| `CLASS/METHOD/ATTRIB/NEW` | `class`/`virtual`/field init/`new` | |
| `REGISTER_WEAPON(BLASTER, NEW(Blaster))` | `[Weapon] class Blaster` | Source generator enrolls it. |
| `FOREACH(Weapons, cond, body)` | `foreach (var it in Weapons.All) if (cond) { }` | |
| `MUTATOR_HOOKFUNCTION/CALLHOOK` + `M_ARGV` | typed `event`/delegate + `ref`/`out` params | Replace global arg-slots with real params. |
| `WriteByte(...)`, `ReadByte()` | `INetWriter`/`INetReader` calls | Through the facade, not raw. |
| `_("text")` (translatable) | `Tr("text")` / localization API | Preserve i18n. |
| `cvar("g_foo")`, autocvars | `Cvars.Get("g_foo")` / `[AutoCvar]` | Keep cvar names (OPEN Q5). |
| `#ifdef SVQC/CSQC` | project boundary / interface / `partial` | No preprocessor symbols for the cl/sv split. |

## Where logic lives (enforced)

- **`XonoticGodot.Common` must not reference Godot.** Gameplay logic is engine-agnostic and depends on the
  facade **interfaces**. This is what makes the headless server and unit tests possible. CI enforces the
  no-Godot-reference rule on `Common`.
- Presentation (nodes, materials, audio playback, input reading) lives in `Client`/`Engine`, behind the facade.

## Performance discipline (GC is the enemy — RISK R5)

The simulation/render hot paths must be **allocation-free**:

- **No per-frame heap allocations** in the tick or render loop. No LINQ, no closures capturing locals, no boxing,
  no `params`, no `Godot.Collections` in hot paths.
- **Cache `StringName`/`NodePath`** as `static readonly` fields; never pass raw `string` to Godot APIs that take
  `StringName`/`NodePath` in hot paths (implicit cast allocates every call).
- **Pool entities and transient objects** (projectiles, effects, net messages). Spawn/remove churn is high in
  Quake gameplay.
- Prefer `struct` for small value types (vectors, trace results, input commands); pass by `in`/`ref` where it
  avoids copies.
- Profile with **external .NET profilers** — Godot's built-in memory view doesn't see C# allocations.
- Don't call `GC.Collect()` manually.

## Determinism discipline (sim code only)

In `XonoticGodot.Common.Physics` and the sim core:
- Fixed timestep; same code path client+server.
- `double` where the QC accumulated in double; audit.
- No FMA-contracted/fast-math, no SIMD reductions that reorder, in the movement/collision hot path.
- Deterministic PRNG only (never `System.Random` for gameplay). See
  [ADR-0010](../decisions/ADR-0010-determinism-and-numerics.md).

## Fidelity discipline

Any change to code under the **fidelity contract** (tick, `SV_Physics` order, collision, `common/physics/`)
must be accompanied by a golden-trace / movement-parity test result. See
[testing-strategy.md](testing-strategy.md).

## Porting hygiene

- Port **module by module** behind the facade; keep the QC file path in a `// port: qcsrc/...` comment for
  traceability during the migration.
- Preserve cvar names, stat semantics, and message field order where they cross the network or affect balance.
- Use the **mechanical-assist transpiler** for the regular cases; hand-review everything it flags
  (cross-type field access, field-pointers, transmute).
- Translatable strings keep their text identical (i18n catalogs are reusable).
