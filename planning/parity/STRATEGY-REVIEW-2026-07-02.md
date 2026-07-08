# Parity strategy review — 2026-07-02

_Why the game still has unregistered gaps after 11+ waves, ~918 gap-closures, and 0 net
regressions — and what changed about the process in response. Read this before changing the
parity **process**; read [EXECUTION-STRATEGY.md](EXECUTION-STRATEGY.md) before running a
porting **wave**._

## The verdict

The per-unit audit machinery is sound: 6-dimension scoring with liveness, adversarial
verification (which caught 125 omitted features and 330 over-claims in the original drafts),
tiered-model waves with opus review, and honest `unknown` discipline took the registry from
1,363 to ~445 open gap-features with zero net regressions. But the machinery only measures
what is inside its frame. Four structural blind spots explain "I found a difference in-game
that the registry doesn't know about":

## Blind spot 1 — registry coverage of Base was unmeasured (and ~30% unmapped)

Measured 2026-07-02 (`tools/parity-coverage.py`, first run): the registry cited **588 of
1,757** Base source files. Whole subsystems had never been looked at: `menu/` (~25k lines,
2% cited), `common/mapinfo.qc` (1,678 lines, 0 citations — while ≥3 top-25 ranked gaps were
*symptoms* of it recorded in per-gametype units), the 7 minigames, the console-command
surface, `damageeffects.qc`, playerstats/xonstat, fine-grained strafehud. The tell: **every
drift pass found "newly-unmapped Base features"** (13, 12, ~100, 15, 7, 5, 4 across passes) —
enumeration was converging asymptotically with no completeness metric.

**Response (built 2026-07-02):**
- [`tools/parity-coverage.py`](../../tools/parity-coverage.py) → [COVERAGE.md](COVERAGE.md):
  every Base file is now `cited` / `excluded` (rationale required,
  [coverage-scope.yaml](coverage-scope.yaml)) / `deferred` (audit scheduled) / **UNMAPPED**.
  The actionable number is UNMAPPED; drive it to 0 and keep it there. Also lints **stale
  citations** (12 found on first run — rows citing upstream-era paths like
  `common/gamemodes/gamemode/...` that don't exist at the pin).
- The 2026-07-02 **unmapped-area audit wave** (`parity-unmapped-audit` workflow): 8 new units
  (`mapinfo`, `minigames`, `menu-core`, `menu-dialogs`, `cl-damageeffects`,
  `console-commands`, `playerstats-xonstat`, `cl-strafehud` — each audited + adversarially
  verified), plus sweep agents classifying every remaining uncited file into
  exclude/defer/fold, plus fold agents adding the missing rows to existing units.
- New `menu` registry category (behavior-level: dialogs/settings and whether their cvars take
  effect; the QC widget toolkit is an intended divergence).

**Standing rule:** run `parity-coverage.py` alongside `parity-assemble.py` after any registry
change. New UNMAPPED files (upstream additions, new citations lost in edits) are a process
failure, not background noise.

## Blind spot 2 — the spec was "qcsrc only"; the data/engine layer was out of contract

Original-Xonotic behavior = QC **+** ~170 shipped cfg files **+** effectinfo/assets **+** the
DarkPlaces engine. The shipped bugs that hurt came disproportionately from outside qcsrc: the
ENet throttle spawn-rubberband (engine networking), the hitsound loading non-existent
`misc/hitconfirm` (asset ref), `hud_panel_centerprint_fade_in` 0.15-vs-0 (code default vs
shipped cfg), the dead graphics cvars, `buff.md3`/`casing_shell.mdl` load failures in the boot
log. Each was found by luck or one-at-a-time audit.

**Response (built 2026-07-02):**
- [`tools/parity-cvar-diff.py`](../../tools/parity-cvar-diff.py) → [CVAR-DIFF.md](CVAR-DIFF.md):
  simulates the port's real cfg boot chain (`xonotic-client.cfg` + `xonotic-server.cfg` +
  `notifications.cfg`, ConfigInterpreter-faithful grammar) over **both** trees and diffs:
  chain divergence, effective values, code-literal defaults vs Base effective, and
  Base-effective cvars never referenced in port source. First run: the cfg trees are clean
  (only the intended `physicsBryan` preset differs — now recorded in
  [cvar-diff-known.yaml](cvar-diff-known.yaml)), but **56 code-default mismatches** (the
  fade_in class, wholesale: `g_balance_armor_regenstable` 50-vs-100,
  `g_ctf_allow_vehicle_carry` 0-vs-1, `cl_zoomfactor` 2.5-vs-5, …) and **1,556 never-read
  Base cvars** (dead-setting leads, prefix-grouped) await triage.
  - Notable correction: Base's *shipped cfg* sets `cl_movement_errorcompensation 1` (the QC
    autocvar default 0 is overridden) — the port's "intended divergence to 1" recorded in the
    registry/memory has the direction inverted; the port's *code register* of 0 is the outlier.
- [`tools/parity-asset-check.py`](../../tools/parity-asset-check.py) → [ASSET-CHECK.md](ASSET-CHECK.md):
  every literal asset path in port code resolved against the real VFS mounts with DP fallback
  rules + model-magic sniffing. First run: **6 missing + 2 unsupported-format**, all genuine —
  including `models/sphere.md3` (the just-built nade-orb renderer; real path
  `models/sphere/sphere.md3`), stale `axh-*` vehicle-HUD texture names, `gfx/*/default/`
  skin paths that don't exist, and the two IDPO `.mdl` refs (casings, gib chunks).
- Both tools have `*-known.yaml` suppression files with the same discipline as
  `intended_divergence`: suppress only what is confirmed deliberate.

**Standing rule:** rerun the differs after touching cfgs/defaults/asset paths; triage findings
into fixes, registry rows (in the owning unit), or known-file entries. Engine-behavior
divergences keep being recorded where consumed, with `intended_divergence` where deliberate.

## Blind spot 3 — verification is static code-reading; the runtime dimension is an IOU

51 NEEDS-INGAME-CHECK items; Phase 6 (in-game verify) never run; 57 features hold an `unknown`
dimension by honest discipline — much of it *freshly built* render work (portal render, nade
presentation, anim overlay, hook rope, charge rings). Meanwhile the repo already owns proven
A/B machinery the parity loop never absorbed: `tools/camera-ref` (the live-DP `movement_dump`
instrumentation — it settled the camera-drift investigation), `tools/movement-ref`,
`tools/visual-qa.sh` (designed to diff against a DP screenshot baseline that was **never
collected**), and `sv_eventlog` on both games.

**Response:** not built this pass (tooling-first was the call). The plan, in order of leverage:
1. **Run Phase 6** — structured `/verify` sessions consuming NEEDS-INGAME-CHECK.md.
2. **Collect the DP screenshot baseline** so visual-qa becomes comparative.
3. **Eventlog A/B harness** — same map/seed scripted bot match on Base (WSL) and port, diff
   normalized event streams. The machine-checkable behavioral regression net.
4. **Verification provenance on rows** (`verification: {kind: test|runtime, ref}`) so
   re-verify passes skip re-deriving what's already guarded.

## Blind spot 4 — cross-cutting contract classes escape per-unit audits

The proven case: Wave 16 wrote 9 floats in `ServerNet.WriteOwnerState` with no `ClientNet`
read — build green, tests green, per-unit re-verify green, real remote clients desync. Found
by accident. Same shape: cvars registered-but-never-read (the graphics stubs),
`BuildMutatorsPrettyString` with zero consumers across ~25 mutators (found late),
mutator×gametype exclusion gates, prediction-vs-server sync.

**Response:** partially covered by CVAR-DIFF's never-read section (2026-07-02). Still to build:
- Net codec **round-trip tests** per message family + a write/read symmetry lint
  (only `ObjStreamCodecTests`/`SnapshotDeltaTests`/`OwnerWeaponRingsTests` exist).
- A mutator×gametype×weapon **interaction checklist** unit for the gates Base makes explicit.

## Process/efficiency amendments (standing)

| Amendment | Status |
|---|---|
| Coverage ledger runs with every assemble; UNMAPPED target 0 | **active** (2026-07-02) |
| Data-layer differs rerun after cfg/default/asset changes | **active** (2026-07-02) |
| Audit prompts: search `src/` AND `game/`; live-path defaults; pin verbatim; honest unknown | **codified** in the audit-wave workflow prompts |
| Git-scoped re-verify (`changed-since:<sha>` — map changed files→units instead of 154-unit sweeps) | todo |
| Per-batch DRIFT filenames (`DRIFT-<date>-<scope>.md` — stop overwriting) | todo |
| Liveness `dormant` (dead-in-Base-too) split, to unpollute PARITY-GAPS ranking | todo |
| Decide the ~70 UNPORTABLE-ANALYSIS "decision" items (→ `intended_divergence` or scheduled) | todo (needs Bryan) |
| Playtest intake (one command: observed difference → triage queue → registry row) | todo |
| Phase 6 `/verify` + screenshot baseline + eventlog A/B | todo (next verification pass) |
