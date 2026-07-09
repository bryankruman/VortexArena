# UW-0048 — Remake Mutators dialog

- **Source:** `data:k9er/new-mutator-dialog@2accdf057be4`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qc`, `qcsrc/menu/xonotic/dialog_multiplayer_create_mutators.qh`, `qcsrc/menu/xonotic/mutatortab.qc`, `qcsrc/menu/xonotic/mutatortab.qh`, `qcsrc/common/mutators/mutator/*/ui_*.qc`, `qcsrc/common/mutators/mutator/*/ui_*.qh`, `qcsrc/server/mutators/events.qh`, `qcsrc/server/world.qc`, `gfx/menu/*/skinvalues.txt`, `xonotic-client.cfg`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Refactored mutators dialog from flat hardcoded layout to modular registry-driven architecture. Four commits: (1) Clean up Settings Game code; (2) Remake dialog with XonoticMutatorTab base class, MenuMutatorSource data source, per-mutator ui_*.qc/.qh files (bloodloss, buffs, cloaked, instagib, invincibleproj, jetpack, nades, nix, overkill, pinata, powerups, rocketflying, touchexplode, vampire); refactor dialog_multiplayer_create_mutators to use tabs and registry; remove weaponarenacheckbox.qc; add mutatortab.qc/qh; add skin theme colors; (3) Improve mutator enable conditions, add SetWeaponArena/SetWeaponStay/SetWeaponOverride hooks, prevent incompatible mutator pairs; (4) Add Buffs replace powerups string for clarity when both are enabled.

## Portability
Mostly qc-gameplay (MenuQC) with light SVQC; MenuQC is portable to C#/Godot UI but requires reimplementation of registry-driven mutator UI pattern. The modular architecture (per-mutator ui_*.qc files with describe/canEnable methods) is sound and likely aligns with how Vortex Arena organizes UI for mutators. SVQC changes are minimal (mutator hook registration conditions to prevent conflicts) and portable.

## Completeness (upstream)
Merged to branch, 4 commits, production-ready. All 14 mutator tabs follow consistent pattern. No automated menu tests in upstream qcsrc/. Diff is internally consistent across schema, implementation, and skin data.

## Quality
Clean refactoring. Consistent pattern across all mutator tabs (XonoticMutatorTab subclass with fill, describe, canEnable methods). SVQC changes well-scoped (mutator hook functions to improve enable conditions). Skin data added for all four menu themes. No obvious hacks or incomplete patterns.

## Roadmap / design alignment
Serves Vortex Arena goals. Dialog UI in Godot is independent of Base (we reimplemented it), so this is not upstream-specific churn; the *infrastructure* (per-mutator tabs, describe/canEnable pattern) is a design improvement we would likely adopt. Mutator gameplay logic is unchanged. No conflicts with known Vortex Arena divergences (we have no documented menu-specific intended_divergences).

## Recommendation
This is a substantive UI/infrastructure improvement that decouples mutator behavior from menu layout. The registry-driven pattern and per-mutator describe/canEnable hooks could inform how we organize our C# menu mutator panel. Port decision is Bryan's; SVQC changes are non-controversial and low-risk. Consider a deep-dive to map the per-mutator UI patterns to Godot equivalents before committing.
