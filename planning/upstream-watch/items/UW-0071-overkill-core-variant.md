# UW-0071 — 

- **Source:** `data:terencehill/overkill-core@99fde21bd464`
- **Kind:** qc-gameplay
- **Base symbols touched:** `net_handle_ServerWelcome`, `REGISTER_MUTATOR(ok)`, `SetModname hook`, `BuildMutatorsString hook`, `BuildMutatorsPrettyString hook`, `checkCompatibility_newtoys`, `build_mutator_list`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** port

## What it does / how it works
Introduces a lightweight "core" Overkill variant (`g_overkill 1`) that can be enabled via the mutators dialog, separate from the full Overkill mod (enabled by config). Core has no custom models/physics/nades/dodging. Adds server→client `modname` protocol field; Overkill mutator activation decoupled from `g_mod_balance` check; conditional mod-name reporting; UI checkbox added to mutators menu.

## Portability
qc-gameplay + protocol adjustment. Gameplay logic is portable to C#/Godot mutator framework; protocol is a spec change (modname field) we must mirror in our netcode. OverkillMutator already ported.

## Completeness (upstream)
Merged to master. Fully formed — no half-baked features or tests missing. Commit message is clear; diff is clean.

## Quality
Clean, focused refactor. Removes the hard gate (`g_mod_balance == "Overkill"`) from mutator enable, allowing Overkill to coexist with any balance set via conditional modname reporting. No regressions visible; maintains backward compat (full Overkill still via cfg).

## Roadmap / design alignment
Serves Vortex Arena. OverkillMutator is already ported and functional (planning/parity/DRIFT-2026-06-22-waves.md confirms parity unit exists + weapons partially ported). This opens the UI path to enable it, fixing the prior limitation that Overkill required full config load (ruleset-overkill.cfg) rather than simple toggle. Directly unblocks a feature we want: in-menu Overkill selection.

## Recommendation
Port this. It unlocks menu-driven Overkill selection for our already-ported OverkillMutator, removing a prior blocker (required full config load). The protocol change (modname field) is minimal and mirrors the upstream spec. Adaptation note: ensure our netcode serializes/deserializes modname alongside version/map on ServerWelcome (trivial string field add). The UI menu work is a future polish; core gameplay ports first.
