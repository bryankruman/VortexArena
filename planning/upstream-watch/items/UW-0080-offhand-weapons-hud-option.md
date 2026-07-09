# UW-0080 — Draft: Add option to show offhand weapons/nades in the weapon HUD

- **Source:** `data:k9er/offhands-in-wep-hud@8119a280dfd3`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/client/hud/panel/weapons.qc: Weapons_Draw, Weapons_DrawWeaponOrOffhand (new), Weapons_GetOffhands (new), Weapons_Fade`, `qcsrc/client/hud/panel/weapons.qh: function signatures, autocvar_hud_panel_weapons_offhand (new)`, `qcsrc/common/mutators/mutator/nades/cl_nades.qc: nades_GetColor (extracted), DrawAmmoNades`, `qcsrc/common/mutators/mutator/nades/sv_nades.qc: nade_prime, MUTATOR_HOOKFUNCTION PlayerPreThink`, `qcsrc/common/stats.qh: NADE_BONUS_ONLY (new), NADE_OFFHAND_TYPE (new)`, `qcsrc/menu/xonotic/dialog_hudpanel_weapons.qc: UI slider control added`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Adds configurable option to display offhand weapons (grenades, hook, blaster) in the weapon HUD panel with three modes: disabled (0), prepend to list (1), append to list (2, default). Major refactor of HUD drawing system with new Weapons_GetOffhands() and Weapons_DrawWeaponOrOffhand() functions. Introduces stats NADE_BONUS_ONLY and NADE_OFFHAND_TYPE. Offhands rendered at 80% scale for visual distinction. Includes menu UI control and config defaults.

## Portability
qc-gameplay (client HUD + game logic). Core player-facing UI. Offhand concept (grenades, hook, blaster) directly maps to Vortex Arena gameplay. Porting requires: mapping QC draw calls to Godot UI system, integrating with C# HUD framework, validating stat replication, testing responsive layout. No engine-level dependencies.

## Completeness (upstream)
Open MR, Draft status. 16 commits with iterative bug fixes (weapon overflow, layout recalculation, bind string handling). Branch actively maintained (multiple master merges). Code appears complete and functional, not half-baked experimental work.

## Quality
Good. Clean refactoring with helper extraction (Weapons_GetOffhands, nades_GetColor, Weapons_DrawWeaponOrOffhand). Follows existing codebase HUD patterns. Stat migration (REPLICATE to STAT for g_nades_bonus_only) is correct and well-motivated. Minor style improvements (e.g. ++nHidden idiom). No obvious bugs in diff.

## Roadmap / design alignment
High. Pure gameplay/UX feature (weapon HUD display preferences) serving player usability and control. Aligns with Vortex Arena's goal of faithful reimplementation. No conflicts with design goals or intended divergences. Offhand weapon concept is fundamental to Xonotic arsenal and our game.

## Recommendation
Recommend porting. Feature has solid implementation, addresses player UX needs, and fits well within Vortex Arena's scope. The HUD refactoring improves code organization. Requires medium effort to adapt QC drawing calls to Godot UI layer and test layout responsiveness. No blockers identified. Suitable for inclusion in the weapon HUD subsystem port roadmap once the base HUD infrastructure is established in C#/Godot.
