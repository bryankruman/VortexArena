# UW-0073 — 

- **Source:** `data:terencehill/ammo_panel_hide_unavailable@4298b12ef8e9`
- **Kind:** qc-gameplay
- **Base symbols touched:** `Resource.m_hidden`, `qcsrc/client/hud/panel/ammo.qc::DrawAmmoItem`, `qcsrc/client/hud/panel/ammo.qc::HUD_Ammo`, `qcsrc/client/view.qc::CSQC_UpdateView`, `qcsrc/server/client.qc::ClientConnect`, `qcsrc/common/mutators/mutator/instagib/cl_instagib.qc::REGISTER_MUTATOR`, `qcsrc/common/mutators/mutator/melee_only/cl_melee_only.qc::REGISTER_MUTATOR`, `_cl_weaponarena_weapons`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Hides unavailable ammo type cells in the HUD ammo panel based on gamemode restrictions (Weapon Arena, Melee Only, Instagib). Introduces Resource.m_hidden flag, refactors ammo panel to dynamically count visible ammo types, and transmits weapon arena availability from server to client. Four commits spanning client HUD rendering (qcsrc/client/hud/panel/ammo.qc), weapon arena tracking (qcsrc/client/view.qc), server transmission (qcsrc/server/client.qc), and mutator-specific client-side hiding (qcsrc/common/mutators/mutator/{instagib,melee_only}/cl_*.qc).

## Portability
high — pure QuakeC client HUD logic and mutator registration patterns. Resource visibility flag mechanism maps cleanly to C#. Weapon arena cvar transmission is standard netcode.

## Completeness (upstream)
merged — four sequential commits on master, all complete and consistent. No draft/WIP signals.

## Quality
good — clean refactor removing hardcoded AMMO_COUNT, introduces resource-level visibility abstraction, consistent pattern application across mutators, follows existing Base style.

## Roadmap / design alignment
Vortex Arena — improves UX in core restricted-arsenal modes (Weapon Arena, Melee Only, Instagib). No divergence conflicts; mutator pattern integration is standard.

## Recommendation
Port candidate. Real gameplay/UX improvement for restricted-arsenal modes. Moderate port effort (HUD refactor + mutator client integrations + weapon arena sync). No technical blockers identified. Bryan's call on scheduling priority.
