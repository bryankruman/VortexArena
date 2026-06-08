# Spec — Entity & Component Model

Implements [ADR-0007](../decisions/ADR-0007-entity-model.md). How QuakeC's flat-field edicts become typed C#.

## The problem

Every QC `.type field` is **global across all entities** — one namespace. An entity "is a" weapon/item only by
convention (a marker bool + which vtable was `copyentity`'d onto it). Engine fields (`.origin`, `.velocity`,
`.solid`, `.nextthink`, `.classname`) mix with gameplay fields, and code freely reuses generic fields (`.enemy`,
`.owner`, `.think`, `.nextthink`) across "types." Naively, this is one giant struct; that is the smell to remove.

## Target model

```
Entity (base — in XonoticGodot.Common, NO Godot dependency)
 ├─ engine fields: Origin, Velocity, Angles, Mins/Maxs, AbsMin/AbsMax, Solid, MoveType,
 │                 Flags, GroundEntity, WaterLevel, NextThink, Owner, ClassName, ModelIndex…
 ├─ delegates: Action Think; TouchHandler Touch; UseHandler Use; Blocked; SendEntity
 ├─ NodeBinding? (link to a Godot Node3D — set only on the client/presentation side)
 └─ subclasses / components carry gameplay state:
      Player, Projectile, Item, Weapon-entity, Door/Plat (mapobject), Trigger, …
```

- **Logic operates on `Entity`** (and subclasses), never on Godot types — so `XonoticGodot.Common` is headless-testable
  and runs on the dedicated server. The `Entity`↔`Node3D` binding lives in the client/engine layer.
- **Engine fields** live on the base; the simulation/collision/facade read and write them (matching what the QC
  read from engine-maintained fields).
- **Gameplay state** lives on subclasses or **components** (mirroring `qcsrc/ecs/`), not a universal bag.

## Porting rules for the flat namespace

1. **Promote** a `.field` to a typed member on the class that owns it.
2. **Generic-field reuse** (e.g. a weapon stashing data in `.enemy`/`.owner` with a domain meaning): rename to a
   typed member or a component; do **not** preserve the generic field unless the QC genuinely used it
   polymorphically.
3. **Truly generic slots** the engine needs (`think`, `nextthink`, `owner`, `enemy` as AI target) stay on base.
4. **Field-pointers** (`.entity`-typed references passed as values: `il_nextfld`, `SELFWRAP`, stat fields) →
   reimplement the *container/dispatch* (intrusive list → generic `List<T>`/`LinkedList<T>`; the dispatch →
   delegates). Callers mostly use `FOREACH`/`IL_PUSH`, so call sites stay close.
5. **`self`/`other`** → `this` / explicit parameter (the QC mostly already did this).
6. **`TRANSMUTE`** (runtime class change) and **`copyentity`** cloning → re-instantiate or compose per-case.

## The framework port (`lib/` → `XonoticGodot.Common.Framework`)

| QC mechanism | C# replacement |
|---|---|
| `CLASS/METHOD/ATTRIB/NEW/SUPER/ENDCLASS` (`oo.qh`) | native `class`/`virtual`/`override`/`: base()`; ctors |
| `REGISTER_*` + `[[accumulate]]` (`registry.qh`) | `[Attribute]` + source generator (registries, indices, hash) |
| `MUTATOR_HOOKFUNCTION`/`CALLHOOK` (`mutators/base.qh`) | typed `event`/delegates + `ref` args; ordered dispatch |
| `intrusivelist.qh` / field-pointers | generic containers + delegates |
| `self.qh`, `string.qh` strzone, `int/bool/struct/map/unsafe` | **delete** (C# provides natively) |
| `OVERLOAD`/`p99.qh` | native overloads / optional params (**delete**) |
| `STATIC_INIT`/`PRECACHE`/`SHUTDOWN` phases | explicit init pipeline / `[ModuleInitializer]` ordering |

## Example: Blaster weapon (today → target)

QC (`common/weapons/weapon/blaster.qh`): `CLASS(Blaster, Weapon)` + `ATTRIB(...)` defaults + `REGISTER_WEAPON` +
`METHOD(Blaster, wr_think, ...)`.

C#:
```csharp
[Weapon]                       // source-gen enrolls into Weapons.All + assigns id
public sealed class Blaster : Weapon {
    public Blaster() {
        Impulse = 1;
        SpawnFlags = WepFlag.Normal | WepFlag.CanClimb | WepType.Splash;
        Color = new Vector3(0.969f, 0.443f, 0.482f);
        Name = _("Blaster");
    }
    public override void WrThink(Entity actor, WeaponSlot slot, FireMode fire) { /* ... */ }
}
```

The vtable/copyentity/accumulate machinery evaporates; the CLR provides it. `FOREACH(Weapons, …)` →
`foreach (var w in Weapons.All)`.

## Testing

`XonoticGodot.Common` is unit-testable without Godot: construct entities, run think/touch, assert state. This is a
deliberate consequence of keeping logic Godot-free (see [ADR-0008](../decisions/ADR-0008-solution-structure.md)).
