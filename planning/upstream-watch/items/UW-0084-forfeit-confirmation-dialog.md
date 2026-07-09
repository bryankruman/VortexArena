# UW-0084 — Forfeit confirmation dialog on F3 press; allow F3 to switch from spectator to observer too

- **Source:** `data:terencehill/forfeit_dialog@d2ca2e2675a5`
- **Kind:** qc-gameplay
- **Base symbols touched:** `commands.cfg`, `binds-xonotic.cfg`, `qcsrc/server/command/cmd.qc (ClientCommand_spectate)`, `qcsrc/common/gamemodes/gamemode/clanarena/sv_clanarena.qc`, `qcsrc/common/gamemodes/gamemode/survival/sv_survival.qc`, `qcsrc/menu/xonotic/dialog_forfeit.qc/.qh (new)`, `qcsrc/common/mutators/events.qh (MUT_SPECCMD_RETURN_FORFEIT)`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Adds a **forfeit confirmation dialog** (XonoticForfeitDialog) triggered when a player presses F3 during an active match. The dialog asks "observe and forfeit?" with Yes/No + "Never ask again" checkbox. Refactors the spectate command server-side (ClientCommand_spectate in cmd.qc) to check an argv(2) forfeit_ask flag and show the dialog if the player is in-game (warmth guard). Extends mutator hooks with MUT_SPECCMD_RETURN_FORFEIT so CA/Survival can also enforce forfeit confirmation. Recleans F3 keybinding + observer-switch logic (secondary-fire now toggles spectator↔observer camera mode); HUD messages updated to reflect new bindings. Backward-compatible: auto-migrates old F3→spec binds to F3→spec_forfeit on first load. The dialog UX pattern prevents accidental match forfeiture.

## Portability
qc-gameplay (menu dialog + spectate command refactor). Portable to Commands.cs CmdSpectate path (already in tree) + a C# dialog UI replacement.

## Completeness (upstream)
Merged to master (origin/terencehill/forfeit_dialog@d2ca2e2675a5 is a full branch with 10 commits, resolved from 3f3ba94e8 base through d2ca2e267 tip). Clean commits including alias bugfixes, refactors to avoid networking unnecessary cvars, and a backward-compat gate for old menus. No tests visible in the diff; relies on manual play-testing.

## Quality
Clean. The branch spans 10 commits with clear intent (forfeit dialog → backward compat → alias fix → spec refactor to avoid cvar networking). Code matches Base style in dialog.qc, keybinder updates, and mutator hook extensions. No obvious inefficiencies. The "Never ask again" checkbox persists to spec_forfeit_dont_ask cvar — good UX, standard pattern. The backward-compat gate (_spec_forfeit_bindupdate cvar + replace_bind in keybinder) is sound: old menu versions can't handle the dialog, so the system auto-migrates binds on first load. Minor style nit: overkill.qc trailing-whitespace fix is unrelated (a rebase artifact, not forfeiting logic).

## Roadmap / design alignment
**Vortex Arena alignment: HIGH.** The forfeit dialog and spec refactor serve Vortex Arena's UX goals (preventing accidental match forfeit by requiring confirmation, matching modern game conventions). The observer-switch refactor (F3 to toggle spectator↔observer) is useful spectator QoL. The mutator hook extension (MUT_SPECCMD_RETURN_FORFEIT) is clean gameplay-layer piping with no unintended side-effects. No conflicts with our intended_divergence records: spectate command is already fully mapped in Commands.cs (CmdSpectate lines 1555-1560 for LMS spectate-lockout, line 1594 for CA force-spectate); this branch adds the forfeit *dialog* layer on top. Not upstream churn — this is real feature work addressing a legit UX pain point (accidental forfeit). Parity-wise, this is a **new upstream feature** after our pin (v0.8.6-1779-g863cd3e84); we're free to adopt or adapt.

## Recommendation
**Port — subject to menu UI porting strategy.** This is a high-value QoL feature addressing a real pain point (accidental forfeit). The server-side logic (spectate gate + mutator hook extension) is clean and fully compatible with our existing CmdSpectate impl. The blocking item is porting the menu dialog UI from QC XonoticForfeitDialog class to C#/Godot (MessageBoxes or ConfirmationDialog pattern). Once a C# dialog UI is available, integrate via: (1) extend Commands.cs CmdSpectate to accept a forfeit_ask parameter (similar to Base argv(2)==1 check), (2) call the dialog when forfeit_ask && !warmup_stage && player is in-game, (3) port the MUT_SPECCMD_RETURN_FORFEIT hook enum extension to our mutator event system, (4) wire CA.OnSpectateCommand and Survival to return it when appropriate. No parity registry work needed — this is upstream feature adoption, not a prior-mapped behavior. Estimate: S/M for server logic + variable effort for UI (depends on existing dialog/UI framework in Godot port). Risk: low — isolated to spectate flow, no netcode/physics impact."
