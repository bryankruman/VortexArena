# Handoff: Respawn ghost corpses + corpse gibbing

## Next task (the user's ask)

Implement **respawn ghost corpses** in the XonoticGodot port, and make sure corpses
(including ghosts) **explode into gibs when destroyed**, matching upstream Xonotic.

Upstream behavior (`C:/Users/Bryan/Projects/Xonotic/Base/data/xonotic-data.pk3dir/qcsrc/server/client.qc:1500-1520`):
when `autocvar_g_respawn_ghosts` is on, at the moment a player respawns their corpse turns
into a "ghost": it gets `CSQCMODEL_EF_RESPAWNGHOST`, floats upward
(`velocity = '0 0 1' * g_respawn_ghosts_speed`, random `avelocity * g_respawn_ghosts_speed * 3`),
becomes additive/fullbright, fades out over `g_respawn_ghosts_time` / `_maxtime`, and the server
emits `Send_Effect(EFFECT_RESPAWN_GHOST, this.origin, '0 0 0', 1)` (client.qc:1517). Read the
upstream block before implementing — the cvar names/defaults are in
`qcsrc/server/autocvars.qh` (grep `g_respawn_ghosts`).

Gibbing: corpses gib when health drops below `-sv_gibhealth` — the port already has this
threshold + `GibCorpse` (see anchors); verify the ghost corpse keeps `TakeDamage` so it can
still be destroyed, and that gibbing produces the visible gib burst + blood on the client.

## What already exists in the port (anchors)

Worktree: `C:/Users/Bryan/Projects/Xonotic/XonoticGodot/.claude/worktrees/trusting-borg-0c888d`
Branch: `feature/modern-particles` (pushed to origin; working tree clean as of handoff).

- **Corpse + gib server-side**: `src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs`
  — kill path builds the corpse (~:493, MOVETYPE_TOSS / SOLID_CORPSE), corpse damage path
  (~:297-313) calls `GibCorpse(targ)` when `health <= -sv_gibhealth` (gib = `Alpha=-1` etc.).
  The corpse is a separate clone ("CopyBody"); `Player.IsCorpse` routes damage.
- **Respawn moment** (where the ghost transform belongs): `SpawnSystem.PutPlayerInServer`
  (`src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs:509`) and/or
  `MatchController.Spawn` (`GameTypes/MatchController.cs:85`). NOTE: PutPlayerInServer
  operates on the LIVE player edict — the corpse clone is a different entity; you must find
  where the corpse entity is retained to flag it at respawn (upstream does this in
  PutClientInServer on the old body).
- **Client ghost rendering ALREADY WORKS**: `game/client/ClientWorld.cs:967-989` reads
  `CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST` from the networked `Entity.Effects`
  bits → `ModelTint.ApplyAppearance(..., ghost)` (additive/transparent look). Setting the
  flag server-side on the corpse entity should light this up with no client work.
  Flag constant: `src/XonoticGodot.Engine/Simulation/CsqcModelEffectFlags.cs:49`.
- **Effect**: `RESPAWN_GHOST` → effectinfo `respawn_ghost` is already registered
  (`src/XonoticGodot.Common/Gameplay/Effects/EffectsList.cs:172`). Emit via
  `EffectEmitter.Emit("RESPAWN_GHOST", corpse.Origin)` (namespace
  `XonoticGodot.Common.Gameplay`; see the TELEPORT emission added in
  `MapObjects/Teleporters.cs` this session as a template).
- **Client gib visuals**: `game/client/ModelGibs.cs` (`Splash(origin, velocity, amount, floorZ)`),
  wired as `EffectSystem.Gibs`. Check how server gib events reach it today (grep
  `Violence_GibSplash` / `Gibs.Splash` call sites) — if the server-side gib only sets
  Alpha=-1 without networking a gib effect, that's part of this task.

## Repo state / recent history (don't redo)

This session completed a large faithful-particle effort — all committed and pushed on
`feature/modern-particles` (see `git log --oneline`, commits `8f5d3b2..b685d7f`):
DP-parity renderer, decal splat system (R_DecalSystem port), faithful per-segment
projectile trails, TELEPORT/SPAWN/SPAWNPOINT effect emissions. Design + status docs:
`planning/particles-dual-system.md`, `planning/particles-dual-system-STATUS.md`.
Particle-system gotchas (clock, cvar stores, INVMOD decals, atlas) are in auto-memory:
`~/.claude/projects/C--Users-Bryan-Projects-Xonotic-XonoticGodot/memory/dual-particle-system.md`.

Known intentional gap noted there: RESPAWN_GHOST emission was deliberately NOT added this
session because the ghost corpse feature itself didn't exist — that's exactly this task.

## Build / run / verify recipes

- Build: `dotnet build XonoticGodot.sln` (worktree root). Tests:
  `dotnet test tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj` (1512 green at handoff).
- Run windowed (menu): `"/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64.exe" --path "$PWD" --data "C:/Users/Bryan/Projects/Xonotic/XonoticGodot/assets/data"`
  (the worktree's assets/data is skeletal — always pass `--data` at the MAIN repo's assets).
  Headless renders blank; run windowed for any visual check.
- Listen-server match for testing deaths/respawns: add `--host stormkeep --gametype dm --bots 2`.
- Deterministic effect screenshots: `--map stormkeep --fx-demo <effectinfo-name> --fx-mode 0 --fx-still <ageSeconds> --resolution 1280x720 --screenshot <png>`
  (one burst captured at an exact age; `--fx-interval <s>` for repeat cadence).
- To test ghosts: set `g_respawn_ghosts 1` (register the cvar with upstream default — it
  defaults OFF upstream), kill a bot or self-kill, respawn, observe the floating fading
  ghost; then shoot the ghost until it gibs.

## Suggested skills

- `run` — to launch/drive the game for visual verification (the recipes above came from it).
- `verify` — after implementing, verify the ghost + gib behavior in a real match.
- `code-review` — review the diff before committing (this repo values DP-faithful,
  source-cited comments; follow the existing style citing QC file:line).

## Conventions that matter here

- Server gameplay lives in `XonoticGodot.Common` (Godot-free, ambient `Api` facade);
  client rendering in `game/client/`. Cvars: server reads `Api.Cvars`; CLIENT-side code
  must read `MenuState.Cvars` (see memory: the cvar-store gotcha).
- Comments cite the upstream QC/DP source (`file.qc:line`) for every ported behavior.
- Commit style: `feat(...)/fix(...)` with a thorough body; push to
  `origin/feature/modern-particles`.
