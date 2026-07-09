# UW-0063 — Draft: HUD hit indicator

- **Source:** `data:terencehill/hit_indicator_v2@36feda8d4fb5`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/client/view.qc:HitIndicatorUpdate`, `qcsrc/client/view.qc:HitIndicatorShow`, `qcsrc/client/view.qh:autocvar_cl_hit_indicator*`, `qcsrc/common/mutators/mutator/damagetext/sv_damagetext.qc:write_damagetext`, `qcsrc/common/mutators/mutator/waypoints/waypointsprites.qc:drawspritearrow`, `xonotic-client.cfg:cl_hit_indicator*`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
2D directional hit indicator HUD element (arrow pointing to attacker when player takes damage). Renders up to 10 concurrent indicators around screen center, fading over 1.5s, sized by damage amount. Fully configurable: fade time, alpha, radius, color, optional damage text overlay. Integrates with damage-text mutator (extends wire to include attacker entity, adjusts server visibility tier). Refactors drawspritearrow() rendering to support base_alpha distinction, scale parameters, inversion, and border toggle — backwards-compatible, benefits waypoint system. Base symbols: HitIndicatorUpdate/Show, drawspritearrow (signature), write_damagetext (wire), xonotic-client.cfg cvars.

## Portability
High — client-side QuakeC HUD feedback layer. No server-side game logic, netcode, or physics impact. Rendering and entity tracking patterns already established in DamageTextLayer. drawspritearrow refactor is a utility improvement with backward-compatible call-site updates.

## Completeness (upstream)
Active draft: open MR with 11 commits of iterative refinement (2025). Not yet merged to master. Covers edge cases: attacker death/disconnect cleanup, max-queue handling, parameter bounds checking. Looks finished but Draft status suggests awaiting contributor feedback.

## Quality
Clean implementation: proper cvar bounds (fade_time 0.1–2s, alpha 0–1, radius 0.1–1), attacker visibility check mirrors waypoints logic, per-attacker damage accumulation with slot reuse, dead/disconnected attacker removal, damage-scaled sizing. drawspritearrow refactor is backward-compatible. No hacks or test gaps noted.

## Roadmap / design alignment
Strong alignment with Vortex Arena: HUD feedback is part of modern competitive experience. Feature is optional (toggle default on, fully configurable). No conflicts with existing divergences — complements already-ported damage-text mutator. Refactored arrow-rendering benefits waypoint system too.

## Recommendation
Strong candidate for porting. Quality is solid, alignment is clear, effort is moderate. Adds real user-facing feedback value. If porting: follow DamageTextLayer HUD pattern, map drawspritearrow params to Godot polygon drawing, integrate entcs tracking with damage-text pipeline, add to parity registry under cl-view or new hud-feedback unit. If deferring: note in WISHLIST for future HUD infrastructure phase.
