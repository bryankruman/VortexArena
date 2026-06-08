# Spec — Networking & Multiplayer

Implements [ADR-0005](../decisions/ADR-0005-custom-netcode.md) and
[ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md). Reference:
`qcsrc/lib/net.qh`, `qcsrc/lib/csqcmodel/`, `qcsrc/lib/stats.qh`, `qcsrc/common/net_linked.qh`,
`qcsrc/server/antilag.qc`, and Darkplaces `sv_ents5.c` / `sv_ents_csqc.c` / `com_msg.c`.

## What's reusable (design) vs. rebuilt (plumbing)

**Reusable — port QC→C#, mostly mechanical:**
- The **typed message registry**: `LinkedEntities` (stateful, delta'd), `TempEntities` (one-shot events),
  `C2S_Protocol` (client→server). A tagged-union: byte/int24 type tag → handler. → C# `enum` +
  source-generated dispatch ([ADR-0003](../decisions/ADR-0003-source-generators.md)).
- **Per-property serializers** (`ALLPROPERTIES`, `ENTCS_PROP`, `WEPENT_NETPROPS`) with explicit quantization
  (health→1 byte /10, angles→1 byte 360/64) → `[NetProperty]` tables.
- **Stat definitions** (health/ammo/movevars) → an owner-replicated `PlayerState`. **Drop** the engine's fixed
  256-slot array + `MAGIC_STATS` reserved indices (no such cap in XonoticGodot).
- **The predict-and-reconcile algorithm** (`cl_player.qc`) and **snapshot interpolation** (`interpolate.qc`) —
  the most valuable reuse; Godot gives nothing equivalent.
- **RNG-seed sync** (`ENT_CLIENT_RANDOMSEED`).

**Rebuilt — engine-owned, no QC source:** delta compressor, PVS/scope culling, coordinate/angle quantization
*encoding* (we redefine it), input-frame ring buffer, reliability/channels, fragmentation. The DP `csprogs.dat`
push is gone — replaced by the build-parity gate.

## Architecture

Transport: **`ENetMultiplayerPeer`** (raw) + `rpc` for discrete events. On top, a custom layer:

```
Client tick (72 Hz):
  sample input → InputCommand{seq, buttons, move, angles, dt}
  push to input ring buffer; send to server (unreliable, with redundancy)
  predict: from last server-acked state, replay all unacked InputCommands
  render from predicted local state + interpolated remote snapshots

Server tick (72 Hz):
  for each client: dequeue InputCommands up to now; run authoritative movement
  ack last-processed input seq per client
  build snapshot (changed entity state, quantized); send (unreliable)
  reliable channel for events that must arrive (spawns, scores, chat)

Client on snapshot:
  store server state + acked seq; measure prediction error; smooth it out
  (large error → treat as teleport/jumppad, ignore — per cl_movement_errorcompensation)
```

### Components to build (`XonoticGodot.Net`)

1. **Message dispatcher** — ported from `LinkedEntities`/`TempEntities`/`C2S_Protocol`; source-gen'd type-tag →
   handler. Linked = stateful synchronized entities; Temp = fire-and-forget events.
2. **Serializers** — per-entity `[NetProperty]` tables; numeric helpers (`Int24`, approx-past-time byte, bit-width
   selection) ported verbatim from `net.qh`.
3. **Input ring buffer** — sequence-numbered `InputCommand`s, redundant send, server-side dedup/apply.
4. **Predict-reconcile loop** — unpredict to acked state, replay unacked inputs, error-decay smoothing, teleport
   ignore (port `cl_player.qc:157-208,573-665`).
5. **Snapshot interpolation** — two-snapshot lerp, teleport-snap (>1000u), derive-velocity-from-origins (port
   `interpolate.qc`).
6. **Lag compensation** — server rewinds positions to the shooter's view at fire time (port `antilag.qc`).
7. **Build-parity gate** — protocol version + content hash on connect (analogue of the registry-hash handshake).

`MultiplayerSynchronizer` may handle **cold** entities (pickups, scores, `ent_cs` nameplate data) opportunistically.

## Why not Godot high-level MP alone

No prediction, no reconciliation, no lag-comp, no input buffering; `MultiplayerSynchronizer` sends full property
updates (no true delta) and degrades past ~16 players; physics is non-deterministic. It is insufficient for
competitive arena play — hence the custom layer. A mature framework (MonkeNet — C#; Netfox) may bootstrap items
3–5.

## Server topology

Headless dedicated server via Godot's dedicated-server export (`--headless`, `dedicated_server` feature tag,
resource stripping). `XonoticGodot.Common` has no Godot dependency in its logic, so the authoritative simulation runs
clean headless. Listen-server (client-hosted) is the same server code in-process.

## Determinism dependency

Prediction requires the movement sim to be re-runnable and low-divergence — see
[`determinism-and-physics.md`](determinism-and-physics.md) and
[ADR-0010](../decisions/ADR-0010-determinism-and-numerics.md). The existing error compensation is the safety net.
