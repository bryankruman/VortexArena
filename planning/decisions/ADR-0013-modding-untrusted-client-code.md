# ADR-0013 — Sandboxed WebAssembly for server-pushed client mods (client code only)

**Status:** Proposed

## Context

Original Xonotic auto-downloaded **client code** (`csprogs.dat`) **and assets** (`.pk3`) on connect, so a modded
server could update the client automatically. That client code was QuakeC *bytecode* run in the Darkplaces VM —
a sandbox with a fixed builtin table — which is precisely why executing a stranger's code was safe.

[ADR-0011](ADR-0011-protocol-ecosystem-boundary.md) dropped the `csprogs.dat` push and replaced it with a
build-parity **reject** gate. That deliberately precludes server-driven client mods. We want to **restore** the
capability — server-pushed, auto-downloaded client mods — *without* the security disaster of running untrusted
**native** code. XonoticGodot compiles gameplay to C# ([ADR-0002](ADR-0002-target-platform.md)); `Assembly.Load`,
GDExtension, and runtime-compiled C# all run at **full trust** (remote code execution), because reflection
defeats any reference-restriction. The sandbox property must come from the guest **never being host-native code**.

A cited deep-research pass (2026-06) confirmed **WebAssembly via the official Wasmtime .NET embedding** is:

- **Cross-platform** — the `Wasmtime` NuGet bundles prebuilt natives for win-x64/arm64, linux-x64/arm64, and
  **osx-x64 + osx-arm64 (Apple Silicon)** — every desktop target in [ADR-0012](ADR-0012-platform-scope.md), no
  manual build, no Apple-Silicon gap (verified by unzipping the package).
- **Lockable-down** — deny-by-default capabilities (no filesystem/network/env/clock without explicit host
  imports) plus fuel, epoch, and memory caps.
- **Non-intrusive** — a managed NuGet that P/Invokes a bundled native lib, so it does **not** cross the
  C#↔GDExtension boundary the asset pipeline avoids.

**Project-owner scope constraint:** *client code only as wasm; the server stays compiled C#.* A mod ships as a
**recompiled/custom server binary** plus a downloadable **client `.wasm` + assets**. There is no `server.wasm`.

## Decision

Reintroduce server-driven client modding via **sandboxed WebAssembly modules for the client presentation layer
only**, hosted by **Wasmtime .NET**.

1. **Scope boundary.** `client.wasm` owns *presentation and client-only behavior* (HUD, scoreboard,
   notifications/centerprint rendering, view/camera & screen effects, cosmetic particles, viewmodel cosmetics,
   UI panels, sound-cue/announcer selection, client-mod cvars/commands). The **authoritative simulation and
   client-side prediction stay compiled C#** in the engine and server. Mods change gameplay by **recompiling the
   server**; changes to *predicted* gameplay (movement physics) additionally require a matching **client build**
   — an explicit, documented limitation (see Consequences). *(Scope widened by the 2026-07-08 addendum below:
   movement physics and the game entity extension schema join the module.)*

2. **Client-only wasm; no `server.wasm`.** The server runs trusted, operator-compiled code. Only the client
   instantiates untrusted wasm. Server-side mod logic is plain C# in the mod's server build.

3. **Reject → reconcile handshake.** Evolve the [ADR-0011](ADR-0011-protocol-ecosystem-boundary.md) parity gate.
   Split it into a **`BaseProtocolHash`** (engine + wire framing — stays a hard gate; mismatch ⇒ disconnect) and
   a **gameplay-content identity** that becomes a **mod manifest** the server advertises (the `client.wasm` +
   asset packs, each by name/size/sha256/url). The client diffs against its cache, downloads what's missing,
   **verifies by hash**, mounts assets into the VFS, and instantiates the wasm in a locked-down store. A
   content mismatch becomes "provision," not "disconnect."

4. **Lockdown.** Deny-by-default: instantiate with **zero ambient authority** (no WASI). The only capability
   surface is a **curated host-import "builtin" API** (the CSQC-builtin analogue). Every guest call runs under
   **fuel + epoch + memory limits**; downloads are **hash-pinned with size/time caps**; joining a modded server
   requires **explicit player consent**; a misbehaving mod is disabled, never allowed to crash the client.

5. **Guest language stays open.** Lead with portable toolchains (Rust, AssemblyScript) because C#→Wasm authoring
   cannot run on a **macOS host** today; allow C#→Wasm for Windows/Linux/CI authors. The host runs any valid
   `.wasm` regardless of source language.

Wire format, builtin API, security model, and phasing live in [`specs/modding.md`](../specs/modding.md).

## Consequences

**Positive**

- Restores Xonotic's headline capability — join a modded server, the client auto-provisions — **safely and
  cross-platform**.
- The bulk of real mods (server-side mutators / gametypes / balance + CSQC-style presentation) are **fully
  drop-in for the player**: a stock client downloads the wasm + assets and runs them.
- Players are protected: a hostile mod cannot touch disk/network/OS/process and cannot DoS (resource-capped).
- No GDExtension interop; Wasmtime is an ordinary managed dependency.
- **Determinism is *not* required of the wasm.** Because it is presentation-only and never in the
  predict/reconcile loop ([specs/networking.md](../specs/networking.md)), we avoid making wasm bit-identical to
  C#. A significant simplification versus the rejected "gameplay in wasm" path. *(Superseded for movement
  physics by the 2026-07-08 addendum — and the objection dissolves rather than being overridden: both sides run
  the SAME module, so no cross-language determinism is ever needed. See the addendum.)*

**Negative / limitations**

- ~~**Physics/predicted gameplay mods are not carried by the wasm.**~~ *(Superseded by the 2026-07-08
  addendum: movement physics becomes a module function executed by both server sim and client prediction, so
  physics mods ARE drop-in — the full DP/CSQC property.)*
- **Server mods require a recompiled server binary** (no stock-server-hosts-a-mod drop-in) — a direct
  consequence of the client-only-wasm constraint. Revisitable later via a `server.wasm` (out of scope now).
- New subsystem to build and maintain: download pipeline, sandbox host, and a **security-critical builtin API**
  (the host-import boundary is now the real attack surface).
- macOS mod authors are limited to portable guest languages or CI until C#→Wasm gains a macOS compiler host.

**Neutral**

- The base build-parity gate remains for engine/protocol; only the gameplay-content dimension becomes negotiable.

## Alternatives considered

- **Also sandbox `server.wasm` (stock server hosts mods, no recompile).** Rejected per the client-only-wasm
  constraint; it is the most "moddable" end state but a larger surface. Reconsider if drop-in *server* mods
  become a goal.
- **Gameplay/prediction in wasm.** Originally rejected: "forces cross-language determinism (wasm ≡ C#) and
  enlarges the predicted/trusted surface." *(Partially reversed by the 2026-07-08 addendum for MOVEMENT
  PHYSICS specifically — the determinism objection dissolves when both sides run the same module, and the
  security surface is analyzed there. The broader "all gameplay in wasm" path stays rejected; the
  authoritative sim beyond player movement remains compiled C#.)*
- **Runtime-compiled C# / load a downloaded assembly.** Rejected: no sandbox, full-trust RCE, AOT-hostile. Legit
  only for *trusted/first-party* hot-reload via a collectible `AssemblyLoadContext` — a different (trust-gated)
  use case.
- **Other VMs** — Lua (MoonSharp), JS (Jint), libriscv (`godot-sandbox`), or a re-embedded QuakeC VM. Rejected
  versus Wasm for the *combination* of: prebuilt cross-platform native coverage incl. Apple Silicon, a mature
  capability sandbox, language-agnostic guests, and no GDExtension interop (libriscv) / no niche-language VM to
  maintain (QC VM). MoonSharp/Jint remain pure-managed fallbacks if per-RID native-lib deployment proves painful.

## Addendum (2026-07-08) — scope widened to full DP/CSQC parity

Confirmed direction (Bryan, 2026-07-08; drove `specs/demo-replay-and-spectator.md` §16): the goal is the full
DarkPlaces property — **a client joining a newer/modded server downloads ONLY the module + assets, never an
engine build; demos embed the module and replay under any engine of the same protocol era.** In DP terms the
engine protocol is frozen-ish and `csprogs.dat` owns game meaning *including movement physics* (Xonotic's
physics is QC shared code — CSQC predicts with the same source the server simulates). Two scope changes to
the Decision above follow:

1. **Game entity extension payload joins the module boundary (schema-driven).** The engine keeps a fixed
   **core entity block** (id, kind, origin, angles, velocity, model name, interpolation flags — what native
   interp/render placement needs). Every other per-entity field (health/armor/weapon/anim/gameplay bits — the
   things that actually churn between game versions) is defined by a **module-published schema** (field name /
   type / quantization / interp hint) and encoded by a **generic native codec** in the engine: the hot
   encode/delta path stays compiled, compression stays field-level, and the schema is embedded in demo headers
   so any tool can inspect old demos without instantiating the module. New gameplay field ⇒ new schema from
   the module ⇒ **no `BaseProtocolHash` bump**. (Rejected alternative: fully opaque CSQC-style per-entity
   blobs decoded in wasm — maximal freedom but per-entity guest calls per snapshot and only byte-level deltas;
   the schema's type vocabulary can grow a reserved opaque-blob field type later if exotic data ever needs it.)

2. **Movement physics becomes a module function run by BOTH sides — superseding "prediction stays compiled".**
   The player-movement step ships in the module; the **server executes it for its authoritative player sim and
   the client executes the same function inside prediction/reconcile** — one source of truth, so
   client/server physics divergence is impossible by construction (the QC-shared-code property). The
   **trust boundary is unchanged**: "no `server.wasm`" always meant the server never runs *untrusted* code —
   here the server runs the module **it itself ships**, which is exactly as trusted as its own binary. Engine
   updates are then needed only for true engine work (renderer, transport framing, core block).

### Why this is efficient enough (the numbers behind "can we do 2 efficiently?")

- A Wasmtime typed host→guest call is ~10–50 ns; a physics tick is 1 guest call + ~3–10 trace **host imports**
  (collision stays native — traces call back into the engine's `CollisionWorld`).
- Client prediction replays ~1–5 ticks/frame on loopback and ~7–15 at ~100 ms RTT ⇒ worst case ~150 boundary
  crossings/frame ≈ **single-digit microseconds** of call overhead.
- Guest numeric code runs ~1.1–2× native under Cranelift; the movement math itself is tens of microseconds per
  frame today, so the ceiling is a sub-0.1 ms add in the worst case — invisible next to a 4–7 ms frame.
- Server side: players × 72 Hz guest calls (e.g. 16 × 72 ≈ 1.2 k/s) — trivial.

**Two engineering conditions make those numbers real (bake into the implementation):**

1. **Epoch interruption, NOT per-instruction fuel, on the hot paths** (physics + per-snapshot decode).
   Fuel metering instruments every guest instruction (~significant slowdown); epoch-based interruption is a
   near-free periodic check and still guarantees a hung module can't freeze the client. Keep fuel for cold,
   rarely-called guest entry points (UI/panel code) if wanted; the watchdog property must come from epochs.
2. **Shared linear-memory structs, not per-call marshaling**: player state, input command, movevars, and trace
   results live at fixed layouts in guest memory; host and guest read/write in place. No serialization in the
   loop, and the reconcile replay reuses the same buffers.

### Consequence for the parity gates

`BaseProtocolHash` shrinks to: transport/channel framing + snapshot container/ack machinery + the core entity
block + the schema-codec's own format + the host-import ABI (now including the trace/physics imports). All
gameplay identity — registries, entity schema, physics, presentation — moves into the manifest/module, i.e.
into content that travels with a connection or a demo. This is the durable-demos boundary
(`demo-replay-and-spectator.md` §16) and the join-newer-server boundary, and they are the same boundary by
construction.

### Security under the widened scope (prediction in the sandbox — analyzed 2026-07-08)

Moving movement physics into the module does **not** weaken the sandbox — the capability model is unchanged —
but it adds two concrete requirements and deserves the explicit argument:

**Why the capability story is unchanged.** Sandboxing is per-instance and capability-based, not per-content:
the module can only do what its host imports allow, regardless of what code runs inside. Physics adds imports
that are all **pure queries or in-place state math**: trace-line/trace-box against the native collision world,
movevars reads, player state in shared memory. None grants I/O, none reaches data the client doesn't already
possess (a trace result is derived from the map the client has loaded). There is still nothing to exfiltrate
*through* and nowhere to exfiltrate *to*.

**Why a hostile physics module can't cheat.** The server stays authoritative: client prediction is cosmetic
(reconcile overwrites it), and on the server the module executing player movement is the module **the operator
themselves shipped** — trusted exactly like their own server binary ("no untrusted server wasm" holds; the
constraint was always about trust, not about which machine runs wasm). A malicious module on the CLIENT can
mispredict (self-inflicted rubber-banding on that server only) — and anything it could "gain" by lying about
movement is already available to a modified client today, because client input is untrusted by design
(`specs/networking.md`). Prediction-in-wasm adds **zero** cheat capability beyond the existing threat model.

**Requirement 1 — tight epoch deadline + quarantine, because physics is in the per-frame hot loop.** A module
that spins in a presentation call ruins a frame; one that spins in the prediction loop would freeze *gameplay*
every frame. The epoch deadline on hot-path calls must be milliseconds-scale, and tripping it repeatedly
**quarantines the module**: disable it, surface an honest error (disconnect from the modded server / abort the
demo with a message), never a frozen client limping at 3 fps.

**Requirement 2 — guest outputs are untrusted data; validate at the boundary.** Shared linear-memory structs
mean the host reads state the guest wrote. Every value read back crosses a trust boundary: **clamp/reject
NaN/Inf origins, velocities, and angles, and out-of-range magnitudes** before they reach interpolation, the
renderer, or the input encoder — NaN-poisoning must die at the boundary, not propagate into native math. (This
rule applies to ALL module outputs — presentation intents included — but physics outputs feed the most native
consumers, so it is load-bearing here.)

**Determinism bonus.** Wasm float semantics are deterministic IEEE-754 across platforms; with both ends running
the same module, server/client movement agrees bit-exactly even across OS/CPU differences — strictly better
than today's "same C# on both ends, probably identical JIT output" position.
