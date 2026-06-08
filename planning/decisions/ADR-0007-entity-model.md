# ADR-0007 — Entity model: typed C# classes wrapping Godot nodes

**Status:** Accepted

## Context

QuakeC entities are "edicts": a flat field array where every `.type field` is **global across all entity types**
(one namespace). An entity "is a" weapon/item only by convention (a marker bool + which vtable was copied onto
it). Gameplay code freely stashes data in generic fields (`.enemy`, `.owner`, `.think`), and engine fields
(`.origin`, `.velocity`, `.solid`, `.nextthink`) are mixed with gameplay fields. This flat namespace is the
single biggest Axis-A design obstacle.

## Decision

Model an entity as a **C# object that owns (or wraps) a Godot `Node3D`** for presentation and carries gameplay
state as typed members:

- A base `Entity` class holds the **engine fields** the simulation needs (origin, velocity, angles, mins/maxs,
  solid, movetype, flags, groundentity, nextthink, the think/touch delegates, owner, classname…).
- Gameplay state lives on **subclasses** (Player, Projectile, Item, Door…) or **components**, not on a universal
  field bag.
- Cross-type field reuse from the QC (e.g. a weapon stashing data in `.enemy`) is **audited and resolved
  case-by-case** during the port — promote to a typed member, a component, or a small dictionary where the QC
  genuinely used a field generically.
- The `Entity`↔`Node3D` link is explicit; the simulation operates on `Entity` (no Godot dependency in
  `XonoticGodot.Common`'s logic), and presentation reads from it.
- `self`/`other` globals → explicit parameters / `this` (the QC has already mostly migrated to `entity this`).

## Consequences

- A real OO design replaces the flat bag; this is *design work*, not mechanical translation, concentrated in the
  Player/weapon/item subsystems.
- `XonoticGodot.Common` gameplay stays free of Godot types (testable headless; runs on the dedicated server) — the
  `Entity`↔node binding lives in the client/engine layer.
- Intrusive lists and field-pointer idioms (`il_nextfld`, `SELFWRAP`) are reimplemented as generic containers
  and delegates (the callers mostly use `FOREACH`/`IL_PUSH`, so call sites stay similar).
- Runtime `TRANSMUTE` (class change) and `copyentity` cloning are handled per-case (re-instantiate / compose).

## Alternatives considered

- **One universal `Entity` with a `Dictionary<FieldId, Variant>`:** rejected — preserves the flat-namespace
  smell, kills type safety and performance.
- **Pure ECS (archetype/data-oriented):** deferred — Xonotic's logic is written against entity objects with
  methods; a full ECS is a bigger redesign than the port needs. A light component layer (mirroring `qcsrc/ecs/`)
  is enough.
