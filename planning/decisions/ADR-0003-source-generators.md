# ADR-0003 — Replace QuakeC macro-metaprogramming with Roslyn source generators

**Status:** Accepted

## Context

Xonotic's `lib/` fakes classes, generics, reflection-like enumeration, and an event bus entirely with the C
preprocessor and gmqcc's `[[accumulate]]`. The load-bearing patterns are:

- **Registries** (`REGISTER_WEAPON`, `REGISTER_ITEM`, `REGISTER_STAT`, `REGISTER_GAMETYPE`,
  `REGISTER_MUTATOR`, …) — declaring a thing anywhere auto-enrolls it in a global, hash-checked catalog
  (`registry.qh` + `[[accumulate]]`).
- **The mutator/hook event bus** (`MUTATOR_HOOKFUNCTION`/`MUTATOR_CALLHOOK`) — ordered, typed callback chains.
- **Net serializers** — X-macro property tables (`ALLPROPERTIES`, `ENTCS_PROP`, `WEPENT_NETPROPS`).

These run at *compile time* with zero runtime cost and produce a deterministic, hash-verified ordering shared by
client and server.

## Decision

Replace the macro layer with **C# attributes + Roslyn incremental source generators** (project
`XonoticGodot.SourceGen`):

- `[Weapon]`/`[Item]`/`[Stat]`/`[GameType]`/`[Mutator]` attributes → generators emit the registry tables,
  index assignment, iteration (`Weapons.All`), and the client/server content hash.
- `[Hook]`/event attributes (or plain C# `event`/delegates) → generated subscription/dispatch with explicit
  ordering (`First`/`Last`/`Any`).
- `[NetProperty]` field tables → generated `Serialize`/`Deserialize` with per-field quantization.

Where compile-time generation is overkill, plain startup reflection or explicit `event +=` is acceptable.

## Consequences

- Preserves the compile-time, zero-runtime-cost, hash-verified character of the original — *better*, with
  diagnostics and no runtime reflection.
- The generators are core infrastructure and must exist before the gameplay fan-out (Phase 2). They are the
  cleanest mechanical answer to `[[accumulate]]`.
- Generated code is debuggable and ships in the assembly; no special runtime support needed.
- Risk: source-generator authoring has a learning curve; budget for it in Phase 0/2.

## Alternatives considered

- **Runtime reflection only:** works but costs startup time and loses compile-time validation; acceptable
  fallback for non-hot registries.
- **Hand-written registration lists:** rejected — re-introduces the central-list maintenance burden the
  `[[accumulate]]` design exists to avoid.
