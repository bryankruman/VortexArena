# Phase 3 — Networking & Multiplayer

**Goal:** turn the local slice into authoritative client/server with prediction. **Retires the #4 risk.**
**Exit demo:** **2+ player LAN deathmatch** with client-side prediction that feels responsive (no rubber-banding
within budget); a headless dedicated server.
**Active tracks:** N (heavy), E (server support), G (shared state stabilization), U (net HUD).
Spec: [`../specs/networking.md`](../../specs/networking.md). Decisions:
[ADR-0005](../../decisions/ADR-0005-custom-netcode.md), [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md).

---

## Track N — Networking (heavy)

### N.1 Transport & framing
- ☐ `ENetMultiplayerPeer` raw transport; reliable + unreliable channels; connection lifecycle.
- ☐ Build-parity gate on connect (protocol version + content hash — analogue of the registry-hash handshake).

### N.2 Message system  ↳ depends: G message/registry definitions
- ☐ Typed dispatcher ported from `LinkedEntities`/`TempEntities`/`C2S_Protocol` (source-gen'd tag→handler).
- ☐ Per-entity serializers from the `*PROPERTIES` tables + numeric helpers (`Int24`, approx-past-time, bit-width).
- ☐ Linked (stateful, delta'd) vs Temp (fire-and-forget event) channels.

### N.3 Prediction & reconciliation  ↳ depends: E deterministic sim
- ☐ Sequence-numbered `InputCommand` ring buffer; redundant client send; server dedup/apply.
- ☐ Server: authoritative movement; ack last-processed input seq per client.
- ☐ Client: unpredict→replay-unacked-inputs; measure + smooth prediction error; teleport-ignore
  (port `cl_player.qc`).
- ☐ Determinism validated under the harness (low-divergence; [ADR-0010]).

### N.4 Interpolation & lag-comp
- ☐ Remote-entity snapshot interpolation (two-snapshot lerp, teleport-snap, derive-velocity — port `interpolate.qc`).
- ☐ Lag compensation: server rewind to shooter's view at fire time (port `antilag.qc`).
- ☐ Snapshot bandwidth budget for the hot path (player/projectile state).

### N.5 Stats & replication
- ☐ Owner-replicated `PlayerState` (health/ammo/movevars) — drop the 256-slot/`MAGIC_STATS` mechanism.
- ☐ Client→server cvar/settings replication (port `replicate.qh` as typed RPCs).
- ☐ RNG-seed sync for predictable effects.

## Track E — Engine (server support)
- ☐ Run the sim core headless (dedicated-server export; `dedicated_server` feature branch).
- ☐ Ensure the facade has no client-only assumptions on the server path.

## Track G — Gameplay (stabilize shared state)
- ☐ Mark networked entity state (`[NetProperty]`) on players/projectiles/items; resolve any client/server logic
  divergence.
- ☐ Make the DM slice fully authoritative (server owns scoring/damage/spawns).

## Track U — Client (net HUD)
- ☐ Render from predicted local + interpolated remote state; netgraph/ping debug overlay.

## Track I — Infra
- ☐ Simulated latency/loss harness in CI (assert clean reconciliation + smooth interp).

---

## DoD
A LAN deathmatch with 2+ players, client prediction + server reconciliation, lag-compensated hit registration,
running against a headless dedicated server. R4 retired.
