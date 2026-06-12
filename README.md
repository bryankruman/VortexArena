# XonoticGodot

[![Tests](https://github.com/bryankruman/XonoticGodot/actions/workflows/ci.yml/badge.svg)](https://github.com/bryankruman/XonoticGodot/actions/workflows/ci.yml)
[![Release](https://github.com/bryankruman/XonoticGodot/actions/workflows/release.yml/badge.svg)](https://github.com/bryankruman/XonoticGodot/actions/workflows/release.yml)

**XonoticGodot** is a work-in-progress port of [Xonotic](https://xonotic.org) — the free, open-source arena
shooter — from its original QuakeC/Darkplaces engine stack to **C# on Godot 4.6 (.NET)**. The goal is a
faithful recreation of Xonotic's gameplay, physics, and visual style on a modern, maintainable engine.

> This project is under active development. Many features are playable, but expect rough edges,
> missing content, and breaking changes.

## Current state

The game code is **mostly structurally ported** — roughly **143,000 lines of C#** so far, against the
original's **~219,000 lines of QuakeC** — but **many systems are not yet wired into the live game loop,
and many are still only partially implemented.** What actually runs today:

- BSP map loading (Q3-style `.bsp` with lightmaps, patches, Q3 shaders)
- Player movement that matches Darkplaces physics (bunnyhopping, air control, crouch)
- Basic weapon fire and item pickup
- A menu system with the Xonotic look and feel
- An in-game console
- 1000+ automated tests covering physics parity, asset parsing, and core logic

Many other systems (vehicles, bots, monsters, minigames, most game types) exist as ported code but
are not yet connected or testable in a real game session. This is very much a work in progress.

## Project structure

```
XonoticGodot/
├── project.godot            Godot 4.6 (.NET) project
├── XonoticGodot.csproj           Godot host (game client + headless dedicated server)
├── XonoticGodot.sln              Full solution
├── src/
│   ├── XonoticGodot.Common       Gameplay, physics, protocol defs, framework (NO Godot dependency)
│   ├── XonoticGodot.Engine       Deterministic simulation core + collision/trace (NO Godot)
│   ├── XonoticGodot.Net          Wire serialization, prediction, reconciliation (NO Godot)
│   ├── XonoticGodot.Formats      Binary asset parsers — IBSP, MD3, IQM, DPM (NO Godot)
│   ├── XonoticGodot.Server       Dedicated server logic
│   └── XonoticGodot.SourceGen    Roslyn source generators (registries, hooks, net)
├── game/                    Godot-side game code (rendering, UI, input, menus)
├── tests/XonoticGodot.Tests      xUnit test suite
└── planning/                Architecture decision records (ADRs), specs, design docs
```

A core design rule: **`XonoticGodot.Common` has no Godot dependency.** This keeps the gameplay simulation
headless-testable and enables a dedicated server that runs without the Godot renderer.

## Roadmap

Development is organized into waves. The current focus is closing the gap between "structurally ported"
and "fully playable":

| Phase | Focus | Status |
|---|---|---|
| **Foundation** | BSP maps, movement physics, asset pipeline, core framework | In progress |
| **Gameplay core** | Weapons, items, damage, game types, mutator hooks | In progress |
| **Networking** | NetGame host, player sync, prediction/reconciliation | In progress |
| **Menus & UI** | Full menu system, HUD, console, campaign | In progress |
| **Visual parity** | Model rendering, team colors, lighting, effects, transparency | In progress |
| **Wiring pass** | Connect ported-but-dormant systems (vehicles, monsters, minigames) | Planned |
| **Polish & content** | Missing data tables, audio coverage, map compatibility | Planned |
| **Release prep** | Performance, packaging, documentation, public builds | Future |

See [`TODO.md`](TODO.md) for the detailed task tracker with per-item status.

## Getting started

### Prerequisites

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- [Godot 4.6.3 (.NET / mono build)](https://godotengine.org/download) — the standard build won't
  run C# projects; you need the .NET variant

### Assets

Game assets (textures, models, maps, sounds) are downloaded from the upstream Xonotic repositories:

```bash
./download-assets.sh            # full download (data + music + maps)
./download-assets.sh --no-music # skip the ~300 MB music repo
./download-assets.sh --no-maps  # skip the ~750 MB compiled map pk3s
```

This populates `assets/data/` which the game's VFS mounts at runtime. Without it, maps and models won't load.

### Build

```bash
# Build and test the engine/gameplay libraries (no Godot needed)
dotnet build tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj
dotnet test  tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj

# Build the full Godot project
dotnet build XonoticGodot.csproj

# Or run the whole local CI gate (build + tests + host + headless boot smoke)
ci/ci.sh
```

CI (GitHub Actions) runs the test suite and the host build on every push/PR; note that the
asset-dependent tests self-skip there, so `ci/ci.sh` with assets downloaded is the stronger check.

### Run

**In the editor:** Open `project.godot` in the Godot 4.6.3 .NET editor and press Play.

**Headless smoke test** (no window — useful for CI or quick verification):

```bash
# Set GODOT to the console variant of the engine
export GODOT="/path/to/Godot_v4.6.3-stable_mono_win64_console.exe"

"$GODOT" --headless --path . --quit-after 200
```

See [`RUNNING.md`](RUNNING.md) for full details on toolchain paths, visual runs, hosting a match,
and debugging tips.

## Contributing

Contributions are welcome. A few guidelines:

- **Read the original source first.** Every ported feature should match the behavior of the
  original QuakeC/Darkplaces code. The canonical reference lives in `Base/data/xonotic-data.pk3dir/qcsrc/`.
  Port by mirroring the original logic — same constants, defaults, branch order. Intentional deviations
  should be commented.
- **Keep `XonoticGodot.Common` Godot-free.** Gameplay and simulation code must not reference the Godot API.
  This is enforced architecturally (it's a plain .NET class library) and is non-negotiable.
- **Don't commit binary assets.** Textures, models, sounds, and maps are downloaded by
  `download-assets.sh` into `assets/` (gitignored). Don't commit them to this repository.

See [`planning/`](planning/) for architecture decision records and design context.

## License

XonoticGodot is free software. It is a port of the upstream Xonotic **game** source (`qcsrc/`),
which Xonotic licenses under the
[GNU General Public License v3.0 or later](https://www.gnu.org/licenses/gpl-3.0.html) (GPLv3+).
Because this is a derivative of GPLv3+ code, all source code in this repository is released under
**GPLv3 or later** as well. See [`COPYING`](COPYING) and [`GPL-3`](GPL-3).

> Upstream's *engine* (DarkPlaces) is GPLv2+, but this port runs on Godot and does not include or
> redistribute DarkPlaces, so GPLv2 does not govern this repository.

Game assets (downloaded by `download-assets.sh` from the upstream Xonotic repositories) are
distributed under their original licenses as established by the Xonotic project — primarily GPLv2+ for
code-adjacent assets, with various Creative Commons and other free-content licenses for art, music,
and sounds. See the Xonotic project's licensing documentation for specifics.

The [Godot Engine](https://godotengine.org/license/) is licensed under the MIT License, which is
GPL-compatible. Godot is not vendored here; exported builds that bundle the Godot runtime must include
Godot's copyright notice and MIT license text.
