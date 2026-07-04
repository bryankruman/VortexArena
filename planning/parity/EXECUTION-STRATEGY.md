# Parity wave execution strategy

_The reusable harness + model-tiering discipline for porting parity gaps. Read before authoring any parity
porting wave. Codified 2026-06-27._

## Why a fixed harness

Each parity wave touches dozens of units that share a handful of **hub files** (`GameWorld.cs` ×22,
`NetGame.cs` ×21, `ServerNet.cs` ×13, `Commands.cs` ×12, `DamageSystem.cs` ×7, …). Letting many agents edit
the same file concurrently clobbers writes; serialising every shared-file unit into its own round costs ~22
sequential passes. The **plan → apply** split removes both problems and, as a bonus, is the natural place to
apply the model-tiering rule.

## Model-tiering rule (the standing strategy)

> **Smaller models propose; 4.8 disposes.** Cheap models do the easy, bounded *reading/planning* work.
> **Opus 4.8 always does the integration, review, correction, build-gating, and re-verification of every
> submission** — nothing a smaller model wrote reaches a commit without 4.8 passing over it.

| Tier | Harness model id | Used for |
|---|---|---|
| **4.5 (haiku)** | `haiku` | Trivial units: 1–2 gaps, pure wiring / notification-emit / localised presentation. |
| **4.6 (sonnet)** | `sonnet` | Easy–moderate units: self-contained gameplay/presentation logic, few cross-file APIs. |
| **4.8 (opus)** | `opus` | Hard units (logic-heavy, foundation, cross-cutting) **and every apply / review / gate / verify agent, regardless of unit tier.** |

Notes:
- The harness exposes `haiku | sonnet | opus | fable` as model overrides. "4.7" is not separately selectable;
  **sonnet (4.6) is the small-model tier** and haiku (4.5) the trivial tier.
- A unit's plan-phase tier comes from its `tier` field in `_remaining-plan.json` (the difficulty judgement
  made during scoping). The apply/gate/verify phases ignore that field — they are **always opus**.

## The harness (per wave)

Run as a single `Workflow` with four phases. Script: `_wave-port.workflow.js` (parameterised by `args`).

1. **Plan** — _parallel, tiered, read-only._ One agent per unit. It reads `specs/<unit>.md` (authoritative
   Base algorithm + constants), the port `.cs` files, and `registry/<unit>.yaml` (the open gap dimensions),
   then emits a structured **edit-spec**: a list of `{file, anchor, action, code, crossFileApi}` plus an
   `unportable[]` list for gaps that genuinely need engine/render work or live verification. No writes → no
   conflicts → unlimited parallelism → cheap models are safe here.
2. **Apply** — _opus, one owner per target file (files disjoint → parallel; same file → one owner)._ The
   edit-specs are regrouped by target file (a justified barrier — apply needs every spec bucketed). Each file
   owner applies all queued edits to its file, **reconciles overlaps, corrects spec errors from the cheaper
   plan models, and reviews the result for coherence.** This is the review/correction gate.
3. **Gate** — _opus._ `dotnet build` → fix loop → `dotnet test`. Cross-file inconsistencies surface here and
   are fixed. A wave is never committed unless the build and the full suite are green.
4. **Verify** — _opus, parallel per unit._ Re-audit each touched unit against current Base+port (the
   `parity-diff` discipline), update `registry/<unit>.yaml` dimensions, and emit a DRIFT row. Then
   `python tools/parity-assemble.py` refreshes `PARITY-GAPS.md` + the gap count.

Between waves (main loop, not the workflow): confirm green, `git commit`, `git push`, then launch the next wave.

## Conflict-freedom invariant

Exactly one apply agent owns a file for the duration of a wave. Two units that edit the same file route their
specs to that one owner; two units on disjoint files run fully parallel. Cross-file APIs (a method added in
file A, called from file B) are specified in the plan so the two owners stay consistent; any drift is caught
by the Gate build.

## What does NOT get auto-closed

Gaps a code agent cannot honestly close are recorded in the plan's `unportable[]` and left open with a note,
never faked:
- **Render features needing real Godot plumbing** (warpzone SubViewport, hook rope-line, porto trajectory
  preview, world models for ONS/Assault objectives) — these get a best-effort scaffold + an honest note.
- **The 30 `NEEDS-INGAME-CHECK.md` items** — fidelity/liveness that only a live run can confirm. Cleared in a
  separate verification pass (run the app via `/verify`), not by the porting waves.
