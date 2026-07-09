# UW-0099 — Draft: Welcome dialog rehaul proposal

- **Source:** `data:z411/welcome_dialog@dbb2ada2da10`
- **Kind:** qc-gameplay
- **Base symbols touched:** `dialog_welcome.qc`, `dialog_teamselect.qc`, `util.qc`, `makeTeamButton`, `makeTeamButton_T`, `_teams_available`, `showNotify`, `XonoticWelcomeDialog_fill`
- **Port-worthiness:** medium  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Welcome dialog redesign that transforms the join experience: moves team-selection UI into the welcome dialog itself (auto-join + four team buttons + spectate, gated by _teams_available bitmask), shows server details (game type, player count, point/time limits, mods) alongside the MOTD, refactors makeTeamButton helpers into a shared util.qh for code reuse with dialog_teamselect.qc. Touched files: qcsrc/menu/xonotic/dialog_welcome.{qc,qh}, qcsrc/menu/xonotic/dialog_teamselect.qc, qcsrc/menu/xonotic/util.{qc,qh}.

## Portability
qc-gameplay — menu UI layer. Directly translatable to C# DialogWelcome.cs, which already exists as a faithful port but is currently simplified (Join/Spectate only, no team selection). The new branch integrates team-selection logic formerly isolated in dialog_teamselect.qc into the welcome flow, plus server info (game type, player count, limits, mods) layout. No gameplay logic, no netcode, no determinism impact.

## Completeness (upstream)
Draft/WIP. The MR title says "Draft: Welcome dialog rehaul proposal". Three commits span May 16, 2022 (initial update + colors) + a recent "Fix compilation units" (dbb2ada2d). No merge to master yet; no tests visible in diff. The initial commit and color tweaks suggest active work, but the draft status and age (4 years old) indicate unfinished or abandoned upstream proposal.

## Quality
Moderate. The refactor (extracting makeTeamButton to util.qh) is clean and reduces duplication. Layout logic is straightforward table-building (me.TR/me.TD cell arithmetic). Commented-out code (//case K_KP_ENTER) and //me.gotoRC(...OK button...) at the end suggest incomplete transition or experimental state. No obvious bugs, but the draft status and stale age (2022) without upstream merge suggest it was either superseded or remains contentious upstream.

## Roadmap / design alignment
Partial alignment. Vortex Arena's DialogWelcome.cs is already a faithful C# port of the *simpler* QC (Join + Spectate only, no team UI, placeholder MOTD). Adopting this upstream branch would require extending DialogWelcome to include team-selection buttons and server-info display. DialogTeamSelect.cs already has similar logic (team buttons, availability gating). The merge is non-conflicting in intent (both are UI), but represents a *design choice*: does the welcome dialog own team selection, or does a separate dialog? The upstream proposal consolidates them; our current port separates them. This is an intended_divergence candidate worth reviewing with Bryan.

## Recommendation
This is a UI/UX redesign proposal (menu gameplay layer) with real end-user impact: it consolidates the initial-join flow into a single dialog, reducing friction. The upstream branch is old (2022) and marked Draft, suggesting it was either deferred or abandoned upstream — check gitlab.com/xonotic/xonotic-data.pk3dir for the MR status. If the MR is closed/abandoned upstream, it may represent a rejected design choice. If still open/active, it's a feature candidate. Vortex Arena's port is intentionally simplified (minimal backend); adopting this requires commitment to the consolidated-welcome UX. Recommend: (1) verify upstream MR status; (2) if active/merged upstream in later commits, decide: *adapt* the C# DialogWelcome to include team UI and server info (extends existing port), or *defer* pending UX stability upstream. (3) If abandoned, *reject* with note that upstream did not converge on this design.
