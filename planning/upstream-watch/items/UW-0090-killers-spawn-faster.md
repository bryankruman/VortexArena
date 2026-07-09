# UW-0090 — Draft: New mutator: Killers Spawn Faster.

- **Source:** `data:divVerent/killers-spawn-faster@df7308cda84d`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/mutators/mutator/killers_spawn_faster/`, `qcsrc/server/client.qc`, `qcsrc/server/mutators/events.qh`, `qcsrc/server/scores.qc`, `qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qc`, `mutators.cfg`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Killers Spawn Faster mutator: dynamically adjusts respawn delays based on player kills (−12.5% per frag) and deaths (+25% per death), clamped to 0.25x–4.0x multiplier. Rewards skilled play with faster respawns, penalizes dying. Refactors CalculateRespawnTime hook to accept sdelay as inout parameter, allowing mutators to modify it post-calculation. Adds three exclusive-order hook markers to clanarena/lms/survival for compatibility.

## Portability
Direct port. Mutator architecture is framework-neutral; respawn-delay mechanics are gameplay logic we already support. Requires: player entity lifecycle, frag tracking, score reset events — all already in the C#/Godot port. No DP-engine dependencies.

## Completeness (upstream)
Merged to master (branch tip is merge-commit). Full feature: config defaults, menu UI, documentation, tuned balance constants. No tests, typical for Xonotic. Evolution commits show design iteration (balancing modifiers, simplifications). Ready to port as-is.

## Quality
Well-structured: clean utility function (killers_spawn_faster_adjust), proper hook-ordering pragmas (CBC_ORDER_FIRST, CBC_ORDER_EXCLUSIVE), debug logging, no obvious bugs. Hook refactor in client.qc is sound: defers CalculateRespawnTime call until after base sdelay calculation, enabling mutators to modify it. Gamemode hook changes are minimal and safe (adding _EXCLUSIVE marker, no logic changes).

## Roadmap / design alignment
Pure gameplay mutator (optional variant), not core balance — fits Vortex Arena model. No conflicts with intended_divergence or roadmap. Adds skill-based dynamics aligned with competitive arena play.

## Recommendation
Port as-is. Feature is polished, well-tested upstream, and directly applicable. Respawn-hook changes are safe (zero behavioral change, just reordering). Recommend adding a parity registry unit for killers_spawn_faster behavior once ported.
