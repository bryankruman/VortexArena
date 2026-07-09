# UW-0058 — 

- **Source:** `data:terencehill/misc_stuff@3ee28d7e6aa8`
- **Kind:** qc-gameplay
- **Base symbols touched:** `WriteSignedByte`, `ReadSignedByte`, `WriteInt24_t`, `ReadInt24_t`, `WriteShort`, `ReadShort`, `WriteChar (deprecated)`, `ReadChar (deprecated)`
- **Port-worthiness:** high  ·  **Effort:** S
- **Decision:** adapt

## What it does / how it works
Refactors network serialization to use explicit WriteSignedByte/ReadSignedByte macros (replacing ambiguous WriteChar/ReadChar), fixes WriteShort/ReadShort to use symmetric signed arithmetic, adds WriteInt24_t/ReadInt24_t, and converts 20 callsites across scores, votes, mapvoting, animations, sound, and tuba. Prevents future serialization bugs by hiding old builtins behind DO_NOT_USE guards. Touches qcsrc/lib/net.qh (core infra), qcsrc/client/main.qc, qcsrc/client/mapvoting.qc, qcsrc/common/{csqcmodel_settings.qh, effects/qc/globalsound.qc, ent_cs.qc}, qcsrc/common/weapons/weapon/tuba.qc, qcsrc/server/{command/vote.qc, mapvoting.qc, scores.qc}, qcsrc/dpdefs/{csprogsdefs,menudefs,post,progsdefs}.qh.

## Portability
Direct. Pure QuakeC network layer with no engine-specific deps beyond standard protocol I/O. Semantic clarification (signed vs unsigned, range validation) is exactly what a C#/Godot port needs to replicate in its netcode layer.

## Completeness (upstream)
Merged to master (4-commit series, all landed). Systematic: infrastructure, callsite conversions, regression prevention. No TODOs, no stale branches. Implies CI+tests passed.

## Quality
High. Clean abstractions (WriteSignedByte/ReadSignedByte), fixes genuine bugs (WriteShort/ReadShort asymmetry, missing WriteInt24_t), documents value ranges, uses DO_NOT_USE guards for future safety. Consistent style.

## Roadmap / design alignment
Core to Vortex Arena. Networking is critical for multiplayer. Ensures server/client serialization symmetry and correct range handling. Not upstream churn; foundational correctness. No design conflicts.

## Recommendation
The Godot C# netcode layer will have its own serialization API, but must replicate the sign/range semantics and value table from this branch. This defines the network format spec (WriteShort/ReadShort symmetry, signed byte ranges, 24-bit support). Port as a protocol spec to a parity registry row; implement the C# equivalent idiomatically.
