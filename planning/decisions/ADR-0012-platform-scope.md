# ADR-0012 — Platform scope: desktop + dedicated server first; web deferred

**Status:** Accepted

## Context

Godot's C# support is production-ready on **desktop** and supports a **headless/dedicated server** export.
However, **C# cannot be exported to the web** in any stable Godot release (only a brittle Mono-WASM prototype),
and mobile C# export is experimental. Xonotic today runs on desktop with dedicated servers.

## Decision

Scope v1 to **Windows / Linux / macOS desktop clients + a headless dedicated server**. **Defer web and mobile.**

## Consequences

- Removes the single biggest platform blocker from the critical path (the web C# gap, R6).
- The dedicated-server export (`--headless`, `dedicated_server` feature tag, resource stripping) is the server
  topology; `XonoticGodot.Common` having no Godot dependency in its logic makes the headless server clean.
- If a browser client becomes a hard requirement later, options are: wait for C#→WASM to stabilize, or build a
  thin GDScript web client subset — both are out of scope for v1.
- Keep `XonoticGodot.Common` portable so a future mobile/web target isn't precluded architecturally.

## Alternatives considered

- **Target web from day one:** rejected — no stable C# web export; would force GDScript or block C#.
- **Mobile v1:** rejected — experimental C# export; not a stated requirement.
