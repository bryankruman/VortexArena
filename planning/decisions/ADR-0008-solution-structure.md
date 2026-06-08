# ADR-0008 — Solution structure: Common / Engine / Server / Client / Menu

**Status:** Accepted

## Context

Xonotic compiles three programs (`progs.dat` server, `csprogs.dat` client, `menu.dat` menu) with a large shared
`common/` tree compiled into *both* client and server, selected by `#ifdef SVQC/CSQC/MENUQC/GAMEQC`. The server
is authoritative; the client runs the same gameplay code for prediction.

## Decision

Mirror this as a C# solution (see [`../ARCHITECTURE.md`](../ARCHITECTURE.md) §2):

- **`XonoticGodot.Common`** — shared gameplay + framework + physics + protocol definitions (≈ `common/` + `lib/`).
  **No Godot dependency** in its logic, so it runs headless on the server and is unit-testable.
- **`XonoticGodot.Engine`** — the Darkplaces-compat runtime (facade, sim core, collision, VFS). References Godot.
- **`XonoticGodot.Formats`** — importers. **`XonoticGodot.Net`** — transport + netcode. **`XonoticGodot.SourceGen`** — generators.
- **`XonoticGodot.Server`** (≈ `progs.dat`) — headless host. **`XonoticGodot.Client`** (≈ `csprogs.dat`) — the Godot game.
  **`XonoticGodot.Menu`** (≈ `menu.dat`) — UI, largely independent (0 net calls today).
- Replace the `#ifdef SVQC/CSQC` split with **build configuration / partial classes / interfaces**: shared logic
  in `Common`, side-specific behavior injected (the QC's `PHYS_*` macro layer that maps the same physics onto
  client vs server input becomes an `IMovementInputSource` abstraction).

## Consequences

- Clean separation of "gameplay" (testable, portable) from "engine/presentation" (Godot-coupled) — this is the
  key to headless servers and to testing movement without a renderer.
- The `common/`-into-both pattern is preserved without preprocessor `#ifdef`s.
- `Menu` can be developed on an independent track from Phase 1.

## Alternatives considered

- **One monolithic Godot project:** rejected — couples gameplay to Godot, blocks headless server and unit tests,
  and reproduces QC's lack of separation.
- **Keep `#ifdef`-style conditional compilation in C#:** rejected — C# interfaces/DI/partial classes express the
  client/server split more cleanly than preprocessor symbols.
