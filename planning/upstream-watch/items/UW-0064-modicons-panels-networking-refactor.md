# UW-0064 — Draft: Improve modicons panels and associated networking

- **Source:** `data:k9er/modicons-and-networking@dbbc6e74bab8`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/stats.qh (removes OBJECTIVE_STATUS, REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE, DOM_PPS_*, TKA_BALLSTATUS)`, `qcsrc/common/net_linked.qh (adds SF_ELIMPLAYERS_* flags)`, `qcsrc/client/hud/panel/modicons.qh (adds autocvar_hud_panel_modicons_animations, HUD_Mod_TableWithAR_Draw, HUD_Mod_EliminatedPlayers_DrawItem, HUD_Mod_SmoothlyUpdateCachedValue)`, `qcsrc/server/teamplay.qc/qh (removes team getter/setter helpers)`, `Per-gametype: ctf/cl_ctf.qc (NET_HANDLE ENT_CLIENT_CTF_FLAGSTATUSES), domination, keepaway, keyhunt, lms, nexball, tka, clanarena/freezetag, survival, onslaught (new modicons)`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Refactors modicon HUD panel animations and networking across 9 game modes (CTF, Domination, Keepaway, Keyhunt, LMS, Nexball, TKA, CA/FT, Survival) plus new Onslaught modicons. Replaces per-stat networking (OBJECTIVE_STATUS, REDALIVE/BLUEALIVE, DOM_PPS_*, TKA_BALLSTATUS) with gametype-specific NET_HANDLE entities (e.g., ENT_CLIENT_CTF_FLAGSTATUSES). Adds animation toggle hud_panel_modicons_animations cvar with blink/expand effects. Consolidates drawing into HUD_Mod_TableWithAR_Draw() and HUD_Mod_EliminatedPlayers_DrawItem() helpers. Removes unused server/teamplay.qc getters/setters. 15 commits, 1991 insertions, 1407 deletions.

## Portability
qc-gameplay, medium effort. Networking changes are protocol-level: must mirror gametype-specific NET_HANDLE signatures and bitfield layouts (ENT_CLIENT_CTF_FLAGSTATUSES, etc.) in C#/Godot netcode. Client-side HUD rendering logic (animations, drawing helpers) is mechanical to port; calls blink() and drawpic_aspect_skin() which have Godot equivalents. Animation cvar toggle integrates with existing Godot config system. No asset-format changes.

## Completeness (upstream)
Draft/open MR. 15 commits represent complete, coherent refactoring (not half-baked). All 9 game modes updated consistently. Code follows Base style (includes, function naming). No obvious bugs or hacks seen in spot-check of CTF changes. No tests visible (typical for gameplay refactor in Xonotic).

## Quality
Clean refactoring. Removes dead stats (OBJECTIVE_STATUS, TKA_BALLSTATUS) that hindered modular game-state handling. Consolidates duplicate drawing logic (formerly per-gametype) into reusable helpers. Networking protocol is rational: per-gametype entities avoid cross-mode pollution. Animations conditional on cvar (good UX). Minor: some variable renames (hud_panel_modicons_freezetag_layout -> hud_panel_modicons_ft_layout) are bikeshedding but don't hurt.

## Roadmap / design alignment
Serves Vortex Arena: modicons HUD is core to gameplay feedback (flag status, team counts). Aligns with our C#/Godot design: per-gametype entities fit our architecture better than global stats. Animation toggle respects player preference. No conflicts with intended divergences (checked planning/). Not upstream churn—improves Base maintainability by removing stat bloat.

## Recommendation
Prioritize for porting after core gameplay modes (CTF, Domination) are netcode-ready. Assign to netcode owner for NET_HANDLE mapping audit. Refer to parity registry for any existing modicons/stat tracking units that may need updates post-port. The stat removal (OBJECTIVE_STATUS, etc.) is a breaking change for any third-party gametype code—flag in TODO.md if ported.
