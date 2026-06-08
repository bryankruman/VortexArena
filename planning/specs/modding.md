# Spec — Modding & Untrusted Client Code (WebAssembly)

Implements [ADR-0013](../decisions/ADR-0013-modding-untrusted-client-code.md) and evolves
[ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md). Reference: Darkplaces `sv_curl` + the native
download protocol (the original `.pk3` push), `qcsrc/client/` (CSQC — the client presentation this replaces), and
the XonoticGodot pieces it builds on: `game/net/NetProtocol.cs` (handshake + `BuildParity`),
`game/net/{ClientNet,ServerNet,NetTransport}.cs` (the link), `src/XonoticGodot.Formats/Vfs/VirtualFileSystem.cs`
(`Mount`), `game/hud/`, and `game/console/`.

## 1. Goal & scope

Restore **"connect to a modded server → the client auto-downloads and runs the mod"** for the **client
presentation layer**, sandboxed via Wasmtime .NET. **Client code only is wasm; the server is compiled** (a mod
ships a custom server binary + a downloadable `client.wasm` + asset packs).

**In scope:** mod-manifest negotiation; download + verify + mount of `client.wasm` and asset packs; the Wasmtime
sandbox host; the curated builtin API; mod lifecycle; player consent; the guest SDK.

**Out of scope:** `server.wasm` (server logic stays compiled C#); wasm in the predict/reconcile loop; carrying
*physics/predicted* gameplay changes via download (those need a matching compiled client — documented limitation).

## 2. The presentation / simulation boundary

The single most important rule: **the guest READS simulation state and WRITES to the screen / audio / UI. It
never mutates authoritative state.** Anything that affects the gameplay *outcome* stays server-authoritative and
is networked to the client as state the mod merely renders.

| Concern | Owner | Notes |
|---|---|---|
| HUD, scoreboard, minimap, crosshair | **wasm guest** | immediate-mode draw builtins |
| Centerprint, notifications, killfeed | **wasm guest** | reads networked events, renders them |
| Damage/hit indicators, screen flashes | **wasm guest** | via the draw builtins |
| Cosmetic particles / effects | **wasm guest** | client-only, non-authoritative |
| Viewmodel cosmetics (bob/sway choice) | **wasm guest** | not the authoritative weapon |
| Announcer / sound-cue selection | **wasm guest** | `play_sound(assetId,…)` |
| Mod cvars + console commands | **wasm guest** | mod-scoped, registered via builtins |
| Mod UI panels / menus | **wasm guest** | contributed into the HUD/menu |
| Movement physics + **prediction/reconciliation** | **compiled C#** | stays bit-compatible server↔client |
| Collision / trace | **compiled C#** | engine service |
| Weapon firing, damage, items, rules, scoring, spawns | **compiled C#** | server-authoritative; networked |
| Entity simulation / think / triggers | **compiled C#** | server |
| Asset decode (BSP/MD3/IQM/shader), the VFS, FS, sockets, OS | **compiled C#** | guest sees assets only by handle |

**Drop-in matrix (player perspective):**

- Presentation mod (HUD/effects/UI) → **drop-in**: stock client downloads wasm + assets, runs.
- Server-rules mod (mutators, gametypes, balance, scoring — *not predicted*) → **drop-in**: server is
  authoritative and networks state; the stock client renders it (optionally with mod presentation wasm).
- Movement-physics mod (*predicted*) → **not drop-in**: needs a matching compiled client. Out of scope here.

## 3. Architecture & components

```
                 ┌──────────────────────── modded SERVER (compiled C#) ───────────────────────┐
                 │  custom gameplay  +  ModManifest{ client.wasm, assetPacks[], consent, … }   │
                 └───────────────▲───────────────────────────────────────────┬─────────────────┘
   handshake (reject→reconcile)  │ ManifestOffer                              │ HTTP / in-band content
                 ┌───────────────┴───────────────────────────────────────────▼─────────────────┐
                 │ CLIENT                                                                         │
                 │  ClientNet ──▶ ContentDownloader ──▶ sha256 verify ──▶ ModCache (by hash)      │
                 │                         │                                   │                  │
                 │                         ├── asset packs ──▶ VirtualFileSystem.Mount()          │
                 │                         └── client.wasm ──▶ WasmtimeModSandbox (locked down)    │
                 │                                                   │ builtins (read state/draw/  │
                 │                                                   ▼ sound/cvar/comms)           │
                 │                                  HUD / Renderer / Sound / Console (compiled C#) │
                 └────────────────────────────────────────────────────────────────────────────────┘
```

Components to build:

1. **Mod manifest + handshake negotiation** (reject→reconcile) — `game/net`.
2. **Content download pipeline** — HTTP-first + in-band fallback, hash-pinned, content-addressed cache.
3. **VFS mount** of asset packs — reuse `VirtualFileSystem.Mount`.
4. **Wasmtime sandbox host** — instantiate, limits, lifecycle, watchdog.
5. **Builtin host-import API** — the curated capability surface (the CSQC-builtin analogue).
6. **Client↔server-mod comms channel** — a reserved net message both ends understand.
7. **Consent UI + download UI + cache management** — `game/modding`.

## 4. Project / file layout

Pure, testable logic lives in a Godot-free library; the Wasmtime impl and Godot bridges live under `game/`
(mirrors the engine-services facade pattern, and respects the "tests can't see `game/`" rule — see
`memory`/console note).

```
src/XonoticGodot.Modding/                 # PURE C# (no Godot, no Wasmtime): contracts + orchestration → unit-tested
  ModManifest.cs  Artifact.cs        #   manifest + artifact schema (serialize/deserialize)
  ModCache.cs                        #   content-addressed cache (keyed by sha256), LRU + size budget
  ContentDiff.cs                     #   "what's missing?" given a manifest + cache
  IContentDownloader.cs              #   HTTP / in-band abstraction (impl in game/)
  IModSandbox.cs  ModBuiltins.cs     #   sandbox + builtin-API CONTRACTS (impl in game/)
  ModLimits.cs                       #   fuel/epoch/memory budgets + clamps
game/modding/                        # GODOT-FACING: Wasmtime impl + bridges + UI
  WasmtimeModSandbox.cs              #   IModSandbox via Wasmtime .NET
  HudDrawBridge.cs SoundBridge.cs    #   builtins → Godot renderer/audio
  StateBridge.cs                     #   builtins → read networked sim state
  HttpContentDownloader.cs           #   IContentDownloader (HttpClient) + in-band
  ModConsentDialog.cs ModDownloadScreen.cs
game/net/NetProtocol.cs              # + ManifestOffer/Need/ContentChunk; split BuildParity→BaseProtocolHash
game/net/{ClientNet,ServerNet}.cs    # the reconcile flow
modding-sdk/                         # GUEST side: ABI + language bindings + templates
  ABI.md                             #   host-import signatures + guest-export contract (the wire of the sandbox)
  rust/rebirth-mod/                  #   ergonomic Rust crate over the raw imports
  assemblyscript/                    #   AssemblyScript package
  templates/hello-hud/               #   minimal working mod
tests/XonoticGodot.Tests/Modding/         # manifest/verify/cache/diff + a malicious-wasm corpus
```

## 5. Mod manifest & the reject→reconcile handshake

Today `NetProtocol.BuildParity()` mixes protocol version + content registry hashes into one value the server
*rejects* on mismatch. **Split it:**

- `BaseProtocolHash()` — protocol version + engine wire framing + the `NetMessageId` enum. **Hard gate**: a
  mismatch means the two binaries can't talk → `HandshakeReject` (unchanged behavior).
- Gameplay content (the `Effects`/`Notifications` registries, etc.) is **described by the mod manifest**. A
  vanilla server advertises a canonical "no-mod" manifest; a modded server advertises its content.

**Manifest schema** (logical; serialized with the existing `BitWriter`/`BitReader`, or JSON for the
out-of-band copy):

```
ModManifest {
  modId:          string         // "overkill", "instagib-deluxe"
  modVersion:     string         // semver or content tag
  baseProtocol:   uint           // MUST equal client BaseProtocolHash (hard gate)
  clientModule:   Artifact?      // the client.wasm (null ⇒ assets-only mod)
  assetPacks:     Artifact[]     // .pk3s to Mount()
  apiVersion:     uint           // builtin-API version the wasm was built against
  limits:         { maxMemoryBytes, fuelPerFrame, epochMs }   // server HINTS; client clamps to its own max
  consent:        { title, description, author, url }          // shown to the player before download
}
Artifact { name:string, sizeBytes:long, sha256:byte[32], url:string?, inbandId:uint? }
```

**New `NetControl` values** (extend the enum in `NetProtocol.cs`):

```
ManifestOffer  = 20,  // server → client (reliable): the ModManifest (or vanilla sentinel)
ManifestNeed   = 21,  // client → server (reliable): list of inbandId artifacts to stream (if no HTTP)
ContentChunk   = 22,  // server → client (reliable): chunked artifact bytes (in-band fallback)
ManifestReady  = 23,  // client → server (reliable): provisioned + consented → admit me to the match
ManifestDecline= 24,  // client → server (reliable): user declined / verify failed / cap exceeded → drop
```

**Flow:**

1. Client connects → `HandshakeRequest` with **`BaseProtocolHash`** + name.
2. Server: base mismatch → `HandshakeReject`. Else → `HandshakeAccept` (netId, tickrate) **+ `ManifestOffer`**.
3. Client computes the missing/mismatched set (`ContentDiff` vs `ModCache` by sha256). If empty **and** the mod
   is already consented/cached → send `ManifestReady`, enter match. Else show the **consent dialog** (author,
   description, total download size).
4. On consent: download each missing artifact — **HTTP from `Artifact.url`** (CDN/mirror friendly, like
   `sv_curl_defaulturl`), else request `ManifestNeed` and receive `ContentChunk`s in-band. Verify **sha256**;
   store in `ModCache` keyed by hash.
5. Mount asset packs (`vfs.Mount`); instantiate `client.wasm` (§6); call `mod_init`. Send `ManifestReady`.
6. Decline / verify failure / size-cap / timeout → `ManifestDecline` + graceful disconnect with a reason.

**Trust note:** the manifest (with its hashes) arrives over the authenticated game connection; HTTP bodies are
**verified by hash**, so a compromised mirror cannot substitute content. Prefer HTTPS for the mirror anyway.

## 6. Sandbox host (Wasmtime .NET) — concrete lockdown

API names per the deep-research pass; treat as a sketch (cross-check the **source on `main`**, not the published
HTML docs, which are stale — they omit `Store.SetLimits` and still show `AddFuel`/`ConsumeFuel`).

```csharp
// ── engine: deny-by-default + DoS guards. Determinism NOT needed (presentation-only). ──
var config = new Config()
    .WithFuelConsumption(true)        // deterministic CPU bound (trap on exhaustion)
    .WithEpochInterruption(true)      // wall-clock watchdog (cannot be evaded by the guest)
    .WithSIMD(false)                  // keep the surface minimal
    .WithWasmThreads(false);          // single-threaded guest
using var engine = new Engine(config);

// ── module is validated on load; reject anything that imports outside our "env" namespace ──
using var module = Module.FromBytes(engine, manifest.modId, wasmBytes);

using var store = new Store(engine);
store.SetLimits(                      // memory-class caps (pair with fuel/epoch for CPU)
    memorySize:    limits.MaxMemoryBytes,   // e.g. 64 MiB
    tableElements: 100_000,
    instances: 1, tables: 4, memories: 1);

// ── NO WASI. The ONLY capabilities are our curated builtins (see §7). ──
var linker = new Linker(engine);
linker.Define("env", "hud_draw_text",
    Function.FromCallback(store, (Caller c, int ptr, int len, float x, float y, int rgba) =>
        HudDrawBridge.DrawText(ReadGuestUtf8(c, ptr, len), x, y, rgba)));
// … the rest of the builtin table …

var instance = linker.Instantiate(store, module);
var modInit  = instance.GetAction("mod_init");
var modFrame = instance.GetAction<float>("mod_frame");
```

Per-frame call, fuel reset + watchdog + graceful-degradation:

```csharp
store.Fuel = limits.FuelPerFrame;          // a store starts at 0 fuel — MUST set each call
store.SetEpochDeadline(limits.EpochTicks); // a background thread calls engine.IncrementEpoch() every epochMs
try {
    modFrame.Invoke(dt);
} catch (TrapException) {                   // fuel/epoch/OOB/guest-abort
    DisableModForSession("client.wasm exceeded its budget or trapped");  // never crash the client
}
```

Guest-memory access from a builtin **must be bounds-checked** before any read/write:

```csharp
static string ReadGuestUtf8(Caller c, int ptr, int len) {
    var mem = c.GetMemory("memory") ?? throw new TrapException("no memory");
    if (ptr < 0 || len < 0 || (long)ptr + len > mem.GetLength()) throw new TrapException("oob");
    return mem.ReadString(ptr, len, Encoding.UTF8);
}
```

**Deployment gotchas (encode in build/CI):**

- **Target x64/arm64 explicitly** in Godot export presets — an "Any CPU" config won't copy the RID-specific
  `libwasmtime` native.
- **macOS: code-sign + notarize** the bundled `libwasmtime.dylib`.
- Validate that a **real exported build** (not just `dotnet run`) loads the native on all five RIDs.
- The **epoch watchdog won't interrupt a guest blocked inside a host call** → keep every builtin **non-blocking
  and fast**; never do I/O on the builtin thread.

## 7. The builtin host-import API (the capability surface = the security boundary)

Principles:

- **Read-only** access to simulation state; **write** only to the frame's screen/audio/UI scratch.
- **Every `(ptr,len)` from the guest is validated** against the guest's own linear memory before use; never
  forward a guest pointer to a host API.
- **No ambient authority.** Assets are referenced by **pre-registered integer id**, never by path from the
  guest — at load the host enumerates the mod's mounted pack and hands out ids (`asset_id("gfx/hud/panel") → i32`).
- The guest gets **no** filesystem, socket, env, clock-beyond-game-time, or process builtins. None exist.

**v1 builtin table** (host imports, namespace `env`):

| Category | Builtins (illustrative) |
|---|---|
| **State (read)** | `get_local_player(out*)`, `entity_count()`, `get_entity(i, out*)` → {origin,angles,modelId,team,frame,flags}, `get_match_state(out*)` → {gametype,timelimit,scores}, `get_cvar(nameptr,len,out*)` (whitelist) |
| **Draw (immediate)** | `hud_draw_text`, `hud_draw_pic(assetId,x,y,w,h,rgba)`, `hud_draw_rect`, `hud_measure_text`, `screen_size(out*)`, `set_clip` |
| **Audio** | `play_sound(assetId,channel,vol)`, `announcer(cueId)` |
| **Input/UI** | `get_cursor(out*)`, `register_panel(id,…)`, `ui_key_down(uiKey)` (UI scope only — **not** movement) |
| **Config** | `register_cvar(name,default,flags)`, `get_mod_cvar`/`set_mod_cvar`, `register_command(name)`, `console_print` |
| **Comms** | `send_to_server_mod(ptr,len)` (reserved `clc_stringcmd`-style channel) |
| **Assets** | `asset_id(pathptr,len)` → i32 (resolved against the mod's VFS scope) |
| **Time** | `client_time()`, `frame_time()` |

**Guest exports** the host calls (the other half of the ABI):

```
mod_init()                              // once, after instantiation
mod_frame(dt: f32)                      // per render frame — draw HUD/effects here
mod_event(eventId: i32, ptr: i32, len: i32)   // server→client mod messages, notifications, command dispatch
mod_shutdown()                          // on unload / disconnect / map change (best-effort, budgeted)
memory                                  // the guest's linear memory (exported)
mod_alloc(size: i32) -> i32 ; mod_free(ptr: i32, size: i32)   // so the host can hand the guest buffers
```

**ABI choice:** **core-wasm** for v1 — scalars + `(ptr,len)` into guest linear memory, UTF-8 strings, flat
structs defined in `modding-sdk/ABI.md`. This is the most portable across Rust/AssemblyScript/C. The **Component
Model / WIT** (richer typed interfaces) is a future upgrade — its C# tooling is still preview.

## 8. Mod lifecycle

- **Provision** → create engine/store/instance, register builtins + asset ids, call `mod_init`.
- **Per frame** → reset fuel, set epoch deadline, call `mod_frame(dt)`; builtins draw into the current frame.
- **Server→client message** → `mod_event(eventId, ptr, len)`.
- **Mod console command** → dispatch to the guest (via `mod_event` or a registered callback).
- **Teardown** (disconnect / map change / error) → `mod_shutdown` (budgeted, best-effort) → dispose store/
  instance. **Recreate per match** to prevent state bleed (Wasmtime stores are cheap).
- **Misbehavior** (trap / budget exceed / OOB) → **disable the mod for the session**, log, optionally notify the
  player; the game continues with **vanilla presentation**. The client must **never crash** because of a mod.

## 9. Guest authoring & SDK

- **Languages:** Rust (most mature wasm story), AssemblyScript (TypeScript-like, friendliest for modders),
  C/C++ (`clang --target=wasm32`). All have **cross-platform** toolchains (build on Win/Linux/macOS).
- **C#→Wasm** is allowed for **Windows/Linux/CI** authors (`componentize-dotnet` / NativeAOT-LLVM, preview) but
  **cannot build on a macOS host** today — so it is **not** the default guest language. Promote it when a macOS
  compiler host lands.
- **Ship `modding-sdk/`** with: `ABI.md` (the import/export contract), a Rust crate (`rebirth-mod`) and an
  AssemblyScript package wrapping the raw imports ergonomically, a `hello-hud` template, and a build script that
  runs `wasm-opt`/strip and checks the artifact against the size/fuel budget.

## 10. Security model & threat table

| Threat | Mitigation |
|---|---|
| Guest tries filesystem / network / env / OS / process | **No ambient authority** — no WASI, zero default imports. Confirmed deny-by-default. |
| Guest infinite loop / CPU DoS | **Fuel per frame** + **epoch watchdog** (the latter cannot be evaded). |
| Guest memory bomb | `Store.SetLimits(memorySize…)` + `ResourceLimiter` gating growth. |
| Guest OOB via crafted `(ptr,len)` to a builtin | Host **bounds-checks every guest pointer/length** vs. linear memory → trap on violation. **Fuzz this.** |
| Guest feeds evil data to a builtin with authority | Builtins wield **no** ambient authority; assets by pre-registered id; no path/socket builtins exist. |
| Huge / zip-bomb asset download | Per-artifact + total **size caps**, streaming, **sha256 pin**, decompression limits in the VFS mount. |
| MITM / compromised mirror | **sha256 from the manifest** (delivered over the authenticated connection); verify body; prefer HTTPS. |
| State bleed between matches/mods | **Fresh store/instance per match.** |
| Privacy / non-consensual mods | **Explicit join consent**; sandbox blocks disk/net/env so there is no exfiltration path. |
| Re-entrancy (builtin re-enters the guest) | Builtins are **synchronous, non-reentrant**; none re-enter the instance. |

**Pre-ship checklist** (do not expose builtins to untrusted modules until all are ✔):

- [ ] Review **Wasmtime security advisories** + Spectre/Cranelift mitigation posture (open item from research).
- [ ] **Fuzz** the `(ptr,len)` marshaling on every builtin.
- [ ] Verify a **real exported build** packages + loads `libwasmtime` on all five RIDs.
- [ ] macOS **code-sign / notarize** the dylib.
- [ ] Confirm **fuel set every frame** + **epoch watchdog thread** running.
- [ ] Enforce download **size/time caps**; downloads cancellable.
- [ ] Test **graceful mod-disable** on trap / OOM / timeout — client never crashes.
- [ ] Reject modules importing anything **outside the `env` builtin namespace** at load.

## 11. Determinism note

Presentation wasm is **not** in the predict/reconcile loop, so wasm determinism is **not required** for netcode
correctness — a deliberate simplification (see [ADR-0013](../decisions/ADR-0013-modding-untrusted-client-code.md)).
If a future feature ever runs *predicted* logic in wasm (out of scope), enable
`WithCraneliftNaNCanonicalization(true)` and keep SIMD/threads disabled per the research. For demo/replay
reproducibility, feed the mod the same recorded state rather than relying on guest determinism.

## 12. Phased plan

Each milestone ships behind a cvar (`cl_allow_mods 0` until M5).

| M | Deliverable | Proves |
|---|---|---|
| **M0** | Manifest + reconcile handshake, **assets only** (no wasm). Split `BuildParity`→`BaseProtocolHash`; advertise/diff/download/verify/mount asset packs; consent + cache + caps + download UI. | the content pipeline end-to-end (the "Level 1" asset download) |
| **M1** | Sandbox host MVP: load a trivial **zero-import** wasm with fuel/epoch/memory limits + watchdog; call `mod_init`/`mod_frame`; cross-platform packaging validated on all 5 RIDs. | the sandbox + native deployment |
| **M2** | **Builtin API v1** (read-state + draw + sound). Port one real CSQC-style element (e.g. a damage indicator or scoreboard panel) authored in Rust/AssemblyScript as the proving mod. | the capability surface + ergonomics |
| **M3** | Client↔server-mod **comms channel** + mod cvars/commands + console integration; graceful-degradation paths. | mod ↔ server interaction |
| **M4** | **Guest SDK** (Rust + AssemblyScript bindings, templates, docs, build scripts). | authorability |
| **M5** | **Hardening**: builtin security review, marshaling fuzzing, Wasmtime advisory review, size/time-cap soak, malicious-mod test suite, demo/replay compat. | safe to enable by default |

## 13. Testing

- **Pure-logic unit tests** (`tests/XonoticGodot.Tests/Modding/`, no Godot): manifest serialize/parse, sha256 verify,
  `ContentDiff`, cache LRU/size budget, `BaseProtocolHash` split, size-cap logic.
- **Malicious-wasm corpus**: fuel bomb, memory bomb, OOB pointer, deliberate trap, missing/bad exports, import
  outside `env` — each MUST be safely contained (mod disabled), never crash the host.
- **Marshaling fuzz**: random `(ptr,len)` into each builtin.
- **Cross-platform CI matrix**: load + run a sample `.wasm` on win/linux/macOS × x64/arm64.
- **Golden HUD render test**: the proving mod renders a known frame → compare.

## 14. Open questions / risks

- Wasmtime **CVE / Spectre posture** — review before exposing builtins (from research).
- **Mirror infrastructure** — who hosts mod content (server operator? a master? Steam Workshop?) — ties to
  [OPEN-QUESTIONS](../OPEN-QUESTIONS.md) Q9.
- **macOS C#→Wasm authoring** timeline — affects whether C# becomes a first-class mod language.
- **Native-lib deployment under Godot export** per OS (the "Any CPU" + notarization pitfalls).
- **Builtin-API versioning** policy (`apiVersion`): a mod built against API vN running on engine vM.
- Core-wasm ABI now vs. **Component Model / WIT** later (typed interfaces; tooling maturity).
