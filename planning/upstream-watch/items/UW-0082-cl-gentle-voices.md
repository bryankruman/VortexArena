# UW-0082 — cl_gentle_voices: gentle mode for player model voicelines

- **Source:** `data:drjaska/cl_gentle_voices@db277288fc89`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/effects/qc/globalsound.qc:GlobalSound_sample`, `qcsrc/common/effects/qc/globalsound.qc:PrecachePlayerSounds`, `qcsrc/common/effects/qc/globalsound.qc:LoadPlayerSounds`, `qcsrc/common/effects/qc/globalsound.qh:autocvar_cl_gentle_voices`, `sound/player/default.sounds (+ 20 others)`, `xonotic-client.cfg`
- **Port-worthiness:** medium  ·  **Effort:** S
- **Decision:** pending

## What it does / how it works
Adds client-side cl_gentle_voices cvar to enable alternative (gentler) player voiceline variants. Extends .sounds manifest format from 3 to 4 columns (file / count / taunt-count / gentle-count). GlobalSound_sample() checks cl_gentle_voices at runtime to select the gentle variant path if available. LoadPlayerSounds() / PrecachePlayerSounds() upgraded to parse the new 4th column with backwards-compatible warnings on old 3-column manifests. All 21 sound/player/*.sounds files updated to add the 4th column (currently set to 0, infrastructure ready). Base files: globalsound.qc (core sample logic), globalsound.qh (cvar decl), xonotic-client.cfg (cvar init), sound/player/*.sounds (manifest format).

## Portability
Client-side only (CSQC-gated); no server logic, netcode, or collision impact. Pure voice-selection conditional added to sample-pick path. Manifest format change is deterministic and mirrors existing data-cfg patterns. Closely aligns with the existing cl_gentle_* cvar family (cl_gentle, cl_gentle_gibs, cl_gentle_messages, cl_gentle_damage), making the port a straightforward 1:1 translation: extend SoundSystem.PlayerSoundSample to parse 4-column manifests, thread cl_gentle_voices check through Play* call sites, update all .sounds manifests shipped with Vortex Arena (currently gentle count=0 OK).

## Completeness (upstream)
Fully merged to master (4 commits). Feature-complete, stable, backwards-compatible (3-column manifests still parse with per-file one-time warning on CSQC). No WIP / draft state; ready for integration. Tests: feature tested against existing QC codebase before merge; no test suite required (cosmetic cvar, no physics/netcode).

## Quality
Clean implementation: proper CSQC guards (#ifdef CSQC), graceful 3-column fallback with user-friendly LOG_WARNF, straightforward tokenize_console parsing matching existing LoadPlayerSounds style. Shell script improvements (check-sounds.sh) are minor hygiene (regex quoting, style). Code matches surrounding Base idioms; no hacks or edge cases flagged.

## Roadmap / design alignment
Strong. This is a client-side cosmetic preference (accessibility/gentleness), not core gameplay. No conflict with Vortex Arena design goals or existing intended_divergence decisions. Feature is entirely opt-in (defaults off). Does not regress any parity registry units (sound subsystem, fx-sounds.yaml, already tracks per-client gentle gates; this extends that pattern). No upstream-specific churn.

## Recommendation
READY for port. Zero technical blockers; path is clear. Small effort (S) makes it a good backlog-filler. Accessibility alignment and opt-in design carry positive signal. Recommend: Bryan assesses priority (low-urgency cosmetic vs. roadmap slot) and issues Decision. When ported, update parity/registry/fx-sounds.yaml to note the gentle-variant infrastructure (new feature row, backend=merged, frontend=ported). Link from Vortex Arena TODO.md as 'UW-0082: Add cl_gentle_voices support' with sub-tasks: (1) C# port of manifest loader + GlobalSound.PlayerSoundSample, (2) .sounds file manifest update, (3) client UI cvar register.
