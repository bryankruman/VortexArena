# ADR-0005 — Custom authoritative netcode on ENet

**Status:** Accepted

## Context

Xonotic is a fast-paced authoritative-server FPS with client-side prediction, reconciliation, snapshot
interpolation, and lag compensation (`antilag.qc`). Its networking is already a modern CSQC predict-and-reconcile
design; the *design*, field quantizations, stat definitions, and prediction math are reusable. What is
engine-owned (delta compression, PVS culling, coordinate quantization, the input-frame ring buffer, the
reliability layer) has no QuakeC source and must be rebuilt.

Godot's high-level multiplayer (`MultiplayerSynchronizer`, RPCs) has **no** prediction, reconciliation,
lag-compensation, or input buffering, and leans on non-deterministic physics — insufficient for competitive play.

## Decision

Build a **dedicated authoritative netcode layer** (`XonoticGodot.Net`) using Godot's **`ENetMultiplayerPeer` as raw
transport** and `rpc` for discrete events, implementing:

1. a typed message dispatcher ported from the `LinkedEntities`/`TempEntities`/`C2S_Protocol` registries;
2. per-entity property serializers ported from the `*PROPERTIES` tables (with their quantization);
3. a fixed-tick deterministic movement step shared with the server (see [ADR-0004](ADR-0004-deterministic-simulation.md));
4. the predict-and-reconcile loop ported from `lib/csqcmodel/cl_player.qc` (input ring buffer with sequence
   numbers, server ack of last-processed input, unpredict→replay, error-decay smoothing);
5. snapshot interpolation ported from `interpolate.qc` (two-snapshot lerp, teleport-snap, derive-velocity);
6. lag compensation (server rewind) per `antilag.qc`.

`MultiplayerSynchronizer` may be used opportunistically for cold, non-predicted, non-bandwidth-critical entities
(pickups, scores, nameplate `ent_cs` data).

## Consequences

- We get responsive, competitive-grade netcode — but it is a from-scratch build (RISK R4), the highest-value
  rebuild. A mature framework (MonkeNet — C#, or Netfox) may bootstrap the prediction layer.
- Determinism is a hard prerequisite ([ADR-0004](ADR-0004-deterministic-simulation.md),
  [ADR-0010](ADR-0010-determinism-and-numerics.md)).
- We define our own wire format; no DP interop ([ADR-0011](ADR-0011-protocol-ecosystem-boundary.md)).
- The dedicated/headless server export is the server host.

## Alternatives considered

- **Godot high-level MP alone:** rejected — cannot host a predicted FPS.
- **Lockstep/rollback (GGPO-style):** rejected for a 32-player arena shooter — authoritative-server +
  prediction is the right model and matches the existing design.
