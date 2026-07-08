# ADR-0011 — XonoticGodot is its own network ecosystem (no Darkplaces wire interop)

**Status:** Accepted

## Context

Darkplaces owns a specific wire format: coordinate/angle quantization, entity-frame delta compression, the CSQC
entity channel, temp-entity framing, and the `d0_blind_id` crypto handshake. Reproducing it bit-exactly would let
a XonoticGodot client talk to existing DP servers (and vice-versa) — but it is an enormous, fidelity-brittle effort
and would constrain every netcode decision.

## Decision

Treat **XonoticGodot as its own ecosystem** with its own (cleaner) protocol:

- Design our own wire format in `XonoticGodot.Net` (we control both ends), reusing the *design* and field
  quantizations from Xonotic's message registry but not the DP byte layout.
- **Enforce client/server build parity on connect** via a protocol-version + content-hash gate (the analogue of
  the existing registry-hash handshake). *(Evolves under [ADR-0013](ADR-0013-modding-untrusted-client-code.md)
  + its 2026-07-08 addendum: the single gate splits into a hard `BaseProtocolHash` — framing + core entity
  block + codec format + host ABI — and a provisionable mod manifest carrying the game-content identity; demos
  pin the same identities, [`specs/demo-replay-and-spectator.md`](../specs/demo-replay-and-spectator.md) §16.)*
- **Drop `d0_blind_id`**; adopt a modern auth scheme or platform identity (see
  [OPEN-QUESTIONS](../OPEN-QUESTIONS.md) Q8) — it was only needed for DP interop.

## Consequences

- Frees the netcode from bit-exact DP reproduction; simplifies [ADR-0005](ADR-0005-custom-netcode.md).
- **No interop** with the existing Xonotic/Darkplaces server population — XonoticGodot servers and clients form a
  separate network. (Single-player, bots, and new servers are unaffected.)
- We may still *quantize coordinates/angles* similarly where it benefits bandwidth/feel — that's our choice, not
  a compatibility constraint.

## Alternatives considered

- **Full DP protocol compatibility:** rejected — disproportionate cost, brittle, and would dictate the whole
  netcode design for a benefit (joining old servers) that conflicts with also replacing the gameplay.
- **Dual-stack (speak both):** rejected for v1 — doubles the netcode surface.
