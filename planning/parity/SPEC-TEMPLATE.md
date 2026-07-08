# Spec template — `specs/<unit>.md`

A spec doc is the human-readable companion to `registry/<unit>.yaml`: it explains *how the
feature actually works in Base* (the algorithm), with exact constants, so a reader can fix the
port without re-reading QuakeC. Keep it factual and Base-anchored. Copy the skeleton below.

---

# <Unit name> — parity spec

**Base refs:** `common/.../<files>.qc`  ·  **Port refs:** `src/...` / `game/...`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** YYYY-MM-DD

## Overview
One paragraph: what this subsystem does in gameplay terms, and which modes/conditions activate it.

## Base algorithm (authoritative)
For each meaningful feature/behavior:

### <feature name>  (`base_refs: file:symbol`)
- **Trigger / entry:** what calls it, on which side (sv/cl/shared), under what condition.
- **Algorithm:** step-by-step what Base does. Pseudocode where it clarifies control flow.
- **Constants:** every magic number / cvar with its Base default and units, e.g.
  `g_balance_ctf_flagcarrier_damage = 2`, `respawntime`, `caplimit`, model frame group, etc.
- **State / networking:** entity fields, `.SendFlags`, CSQC sync, what the client is told vs computes.
- **Edge cases:** dropped flag timeout, return-on-touch, carrier death, mid-air, team handling.

## Port mapping
How the above maps onto the port. For each Base feature, the corresponding port symbol (or
"NOT IMPLEMENTED"). Note the layer split (authority `Server`/`Common` vs presentation `game/`).

## Parity assessment
Per-dimension narrative that justifies the `status` block in the YAML. Call out:
- **Gaps** — concrete defects (what a player would observe / measure).
- **Liveness** — is it actually invoked in a match? Name the live caller, or state it's dead.
- **Intended divergences** — deliberate changes, with rationale.

## Verification
How each claim was checked: unit test ref, in-game observation, value diff, or "unverified".

## Open questions
Anything the auditor couldn't resolve from the code and that needs a runtime check or owner input.
