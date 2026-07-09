# UW-0072 — Display fuel level on the Ammo panel

- **Source:** `data:terencehill/fuel_on_ammo_panel@87667d0ed179`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/client/hud/panel/ammo.qc`, `qcsrc/client/hud/panel/ammo.qh`, `qcsrc/client/hud/panel/healtharmor.qc`, `qcsrc/client/hud/panel/healtharmor.qh`, `qcsrc/client/hud/panel/weapons.qc`, `qcsrc/server/world.qc`, `qcsrc/common/stats.qh`, `qcsrc/common/resources/all.inc`, `qcsrc/common/mutators/mutator/hook/sv_hook.qc`, `qcsrc/menu/xonotic/dialog_hudpanel_ammo.qc`, `qcsrc/menu/xonotic/dialog_hudpanel_ammo.qh`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Moves fuel display from HealthArmor panel to Ammo panel, making it display alongside ammo/weapons. Four commits: (1) core relocation with unhiding RES_FUEL; (2) conditional fuel display based on hook/jetpack consumption (adds HOOK_FUEL stat); (3) fixes onlycurrent layout; (4) adds optional always-show mode. Touches ammo.qc/qh, healtharmor.qc/qh, weapons.qc, server/world.qc, stats.qh, resources/all.inc, hook/sv_hook.qc, menu dialogs, and 7 HUD config files.

## Portability
qc-gameplay; ammo and healtharmor panels already ported (cl-hud.panel.ammo and cl-hud.panel.healtharmor both in parity registry as faithful). Fuel display logic exists; requires refactoring panel layout logic and server-side hook fuel consumption conditionals. No DarkPlaces engine-internal changes.

## Completeness (upstream)
Merged to master equivalent? No, this is an open branch. However, it is complete and production-quality: four logical commits building coherently, no WIP markers, no incomplete features. All affected subsystems updated consistently (qc code, configs, menu dialogs).

## Quality
Clean implementation. Fixes exposed bugs progressively (onlycurrent layout issue in commit 3). Code follows Base patterns (progress bar helpers, stat conditionals, config cascades). No obvious hacks or incomplete error handling.

## Roadmap / design alignment
Directly serves Vortex Arena: fuel and jetpack are core mechanics we've ported. Moving fuel to the ammo panel consolidates weapon/ammo/fuel HUD into a single logical area—a UX improvement, not a departure. No conflicts with intended_divergences (we already diverge in how we render HUDs via Godot, but the logical behavior is preserved). Aligns with our gameplay parity goals (cl-hud registry shows both ammo and healtharmor panels are already faithful).

## Recommendation
Port as-is. No blockers, no conflicts with our design. Already-ported panels (ammo/healtharmor) provide a clean foundation. The conditional fuel-consumption logic (HOOK_FUEL stat) should be cross-checked against our jetpack implementation to ensure parity on when fuel is consumed, but this is routine porting work, not a risk. Create a parity registry amendment row (or amend cl-hud.panel.ammo and cl-hud.panel.healtharmor to reflect the new consolidated layout) once ported. Assign effort M, owner triage on priority (not a blocker for release, nice-to-have UX).
