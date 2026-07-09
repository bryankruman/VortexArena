# UW-0054 — 

- **Source:** `data:pending-release@61523510029f`
- **Kind:** qc-gameplay
- **Base symbols touched:** `StrengthItem`, `ShieldItem`, `SpeedItem`, `InvisibilityItem`, `PowerupStatusEffect`, `ctf_Handle_Pickup`, `ctf_Handle_Return`, `ctf_CheckFlagReturn`, `MULTITEAM_ANNCE`, `MODEL`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Three merged features: (1) New powerup models (strength/invincible/invisibility/speed relics via relic_powerup.md3 + skins, replacing legacy buff models); updated powerup item definitions to use shared relic model with skin indices instead of individual models; new powerup HUD icons. (2) Enhanced CTF announcer with team-relative audio + new notifications for pickup/return events (relative team/enemy/you perspective), plus new team-scores announcer. Adds ~30 OGG audio files and modifies ctf_Handle_Pickup/ctf_Handle_Return logic. (3) Minigame busy indicator converted from IQM to sprite format with symlink. Touches: qcsrc/common/mutators/mutator/powerups/**, qcsrc/common/gamemodes/gamemode/ctf/sv_ctf.qc, qcsrc/common/notifications/all.inc, qcsrc/common/models/all.inc, notifications.cfg, shader files, models/relics/**, sound/announcer/**.

Base symbols: Powerup classes (StrengthItem, ShieldItem, SpeedItem, InvisibilityItem, PowerupStatusEffect variants), ctf_Handle_Pickup(), ctf_Handle_Return(), ctf_CheckFlagReturn(), MULTITEAM_ANNCE macro, MODEL() macro.

## Portability
Highly portable. Powerup changes: pure config/attribute updates to QC classes (m_skin indices, color vectors, icon references) that map directly to C#/Godot item system. CTF announcer logic: straightforward Send_Notification() calls (cross-engine abstraction exists); requires porting audio assets + notification type definitions. Minigame sprite: format support depends on Godot loader — likely portable if IQM/sprite loaders already exist, else trivial once format support is added.

## Completeness (upstream)
Merged to pending-release (3 separate MRs consolidated). All assets present (models, skins, audio OGGs). Code appears complete — no TODOs, no draft markers. Quality: consistent with Base codebase style; changes are well-isolated per subsystem (powerups, CTF, minigame); minimal cross-cutting impact.

## Quality
Clean implementation. Powerup refactor removes ~3 old item-model macros and consolidates to a single relic model + skin swapping (simpler, more maintainable). CTF logic additions are straightforward notification sends; no complex new state or edge cases. Notification system changes (MULTITEAM_ANNCE queuetime param) appear cosmetic/non-breaking. Shader updates (new sprite/luma precedent shaders) follow Base pattern. No tests visible, but gameplay features should be integration-tested in their respective MRs.

## Roadmap / design alignment
Strong alignment with Vortex Arena. Powerup visuals + CTF announcer directly enhance multiplayer experience on both code + asset front; both are high-value for competitive Xonotic ports. New item models (relic_powerup) and audio (CTF announcements) improve visual/audio polish. No conflicts with known intended_divergences; powerup item system is already a port target. CTF is a core gamemode for any Xonotic port.

## Recommendation
Recommend as a port candidate. The powerup model consolidation simplifies our item layer and improves visual consistency; the CTF announcer enhances competitiveness. Split into two work items: (1) Powerup model + item attribute updates (S — pure config), (2) CTF announcer + audio (M — requires audio asset pipeline setup and notification routing). Minigame sprite (already in base contrib 1/3) is trivial bonus if sprite loader is ready. Verify audio asset format support before committing; if not yet ported, defer minigame sprite until loader is available, or use legacy IQM as fallback.
