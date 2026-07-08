# Hitch Resolution — XonoticGodot

**Date:** 2026-06-14
**Hardware:** NVIDIA RTX 3080, 24-core CPU, vsync mailbox, `cl_maxfps 0`
**Scope:** frame **hitches** (stutters), distinct from the sustained-FPS problem fixed in
`catharsis-perf-investigation-2026-06-14.md`. Same methodology: measure a classified census → workflow
root-cause each class → fix + re-measure.
**Method:** the FrameProfiler already classifies every hitch (GC-PAUSE / PIPELINE-COMPILE / ASSET-BUILD /
CPU-LOGIC / GPU-BOUND / VSYNC-PRESENT / EXTERNAL) with forensic scope trees. Census = catharsis + 6 bots DM,
100s, release build, `~/XonData/logs/session-*.log`.

---

## 0. Executive summary

The baseline census showed **985 hitches** in 100s. They are **not 985 independent stutters** — a handful of
primary events each cascade into ~10–15 counted hitches, plus a large, machine-load-sensitive tail of benign
present jitter. Two things drive the *felt* stutters and one cheap lever drives most of the *count*:

| Lever | Effect | Status |
|---|---|---|
| **`cl_maxfps` auto-cap** (the DP default 256 / "Unlimited" never *engaged* on a fast GPU) | Collapses the VSYNC/PRESENT class (the bulk of the count) | **Landed** (ClientSettings) |
| **IQM AnimationLibrary cache** | Eliminates the bot-spawn model-build storm — the catastrophic 88–140ms stutters | **Landed + verified** |
| **Catch-up cap 4→3** | Trims the WeaponThink/bot.think catch-up multiplier on recovery frames | **Landed** |
| Profiler classifier fix | Stops mislabeling GC-pause tails as "EXTERNAL/OS" | **Landed** |

**Combined result — all fixes, shipped DEFAULTS, no config changes (catharsis + 6 bots, release):**

| | Baseline | All fixes |
|---|---|---|
| **total hitches** | **985** | **52** |
| Worst frames | 139 / 106 / 88 / 71 ms | 67 / 56 / 54 / 50 ms |
| ASSET-BUILD | 22 | **1** |
| GC-PAUSE | 11 | **0–3** |
| median frame | 12.9 ms | 6.9 ms (smooth 144) |
| 1%-low fps | 23–51 | **72** |

**~95% fewer hitches, out of the box.** The two remaining smaller items (#3 warm-pass coverage, #2 buffer
pooling) were evaluated and are low-value/blocked — see §5.

---

## 1. The cascade (why 985 ≠ 985 stutters)

The dominant felt hitch is a **bot-spawn/respawn storm** (recurs whenever several bots spawn together):

1. Each bot wearing a given model re-ran `IqmBuilder.BuildAnimationLibrary` — **100–360ms of track-key work +
   a 46–97MB single-frame managed alloc** — because there was **no per-model animation cache** (only MD3 had
   one; skeletal IQM rebuilt unconditionally, `AssetLoader.ParseSkeletalModel`).
2. That alloc burst promotes to gen2 → an **all-thread GC freeze**.
3. The sim falls behind → the next render frames run **catch-up ticks** (`MaxTicksPerFrame=4`).
4. On a 4-tick catch-up frame, two QC-faithful per-tick multipliers fire 4×: **WeaponThink** (7 players × 4 =
   28 calls, `mp.weapon` 2.9ms) and the **bot strategy-token** goal-rating (4 bots/frame, `bot.think` spikes).
5. The overrun backs up the present queue → the following frames register as **VSYNC/PRESENT "recovery"** hitches.

So one spawn storm = 1 ASSET-BUILD + 1–2 GC-PAUSE + 3–6 CPU-LOGIC/MIXED + a VSYNC tail ≈ **10–15 counted
hitches from one root event.** Killing the primary (the animation build) collapses the whole tail — which the
measurements confirm (VSYNC/PRESENT dropped 825→370 uncapped when only the anim cache landed).

**The worst frame was also misclassified:** the 139ms "EXTERNAL/OS-compositor" frame was actually a gen2 GC +
`mp.weapon` catch-up tail — the once-per-frame `GC.GetTotalPauseDuration` delta charged the pause to the prior
frame, so this frame saw `G2==0` and fell through to EXTERNAL. Fixed (§4).

---

## 2. The `cl_maxfps` finding (biggest count lever)

**Controlled, interleaved A/B** (catharsis + 6 bots, runs alternated to cancel machine-load drift):

| `cl_maxfps` | run 1 | run 2 | 1%-low |
|---|---|---|---|
| **0 (uncapped)** | 277 hitches (229 VSYNC) | 164 (116 VSYNC) | 60 / 65 |
| **144 (engages)** | **40 (24 VSYNC)** | **55 (34 VSYNC)** | **84 / 75** |

A cap robustly cuts hitches **~4–5×** and raises 1%-low across both reps. Uncapped, the engine renders as fast as
possible and the CPU outruns the swapchain under mailbox vsync → irregular present pacing → constant >12ms blips.

**The critical nuance** (which corrected an earlier non-interleaved overclaim): a cap only helps when it
**engages** — i.e. sits *below* the framerate the machine can produce. The **DP default is `cl_maxfps 256`**
(`xonotic-client.cfg:785`), and a 3080 sustains ~180–240fps on catharsis, so **256 (and a 250 cap) rarely engage
→ behave like uncapped → still juddery.** 144 wins because it's below what the machine can do, so the CPU paces
itself. (The raw VSYNC *count* is also machine-load-noisy; the interleaved design is what makes the cap effect
robustly visible.)

**Landed fix** (`ClientSettings.ApplyVideo`): the "auto" cases — the DP default 256 and the menu's "Unlimited"
(0) — now apply an *engaging* ceiling `max(144, display-refresh)` instead of running effectively uncapped. The
display refresh would be the ideal ceiling, but Godot under-reports it in borderless mode (reads 60Hz on this
3080), hence the 144 floor. Any explicit menu choice (128 / 512 / 1024 / 2048) is honored verbatim. Verified: with
no config changes the game now caps to 144 (median 6.9ms) and the census drops to **52 hitches** (§0).

---

## 3. Landed fix — IQM AnimationLibrary + parse cache (the storm killer)

`AssetLoader.ParseSkeletalModel` now caches `(IqmData, FrameGroups, AnimationLibrary, defaultClip)` keyed by
normalized model vpath (none depend on skin — the skin only selects materials, loaded per-call). On a cache hit
the 100–360ms / 46–97MB build is skipped and the shared resources are reused.

**Safety (verified):** the `AnimationLibrary` is attached verbatim (`IqmBuilder.Build → AddAnimationLibrary`,
no per-instance mutation); bone tracks are `"Skeleton3D:bone"` paths identical across instances of the same
model; `IqmData` is immutable post-parse. The cache is locked (touched from streamer worker threads); a
double-build race is harmless (last-writer-wins, all readers get a valid shared library). Build happens outside
the lock so a slow build never blocks other models' parses.

**Verified:** 13–20 `anim cache HIT` events per match, **0 runtime errors**, models render posed/tinted
correctly, **2755/2755 tests pass**. ASSET-BUILD hitches 22→1–2, GC-PAUSE 11→1–3, worst frames 88–140ms→50–67ms.
(This implements PERFORMANCE_REPORT §13.3 backlog #1 + the bot-join memory's open seam.)

---

## 4. Landed fix — profiler classifier (census truth)

`FrameProfiler.Classify` now re-attributes a rest-dominated "EXTERNAL"-looking frame to **GC-PAUSE** when one of
the last few frames took a gen2 collection or a long GC pause (the tail of an all-thread freeze the once-per-frame
pause delta charged to the prior frame). This doesn't remove hitches, but it stops the census blaming the OS for
our GC and redirects future work correctly. Pure profiler change; no runtime/gameplay effect.

---

## 5. The rest of the plan — landed + evaluated

**Landed — catch-up cap 4→3** (`NetGame.cs:569`): a recovery frame after a hitch runs `MaxTicksPerFrame`
catch-up ticks, and every per-tick × per-player multiplier (WeaponThink, the bot strategy-token goal-rating)
fires that many times — the measured `mp.weapon ×28` / `bot.think` spikes. 3 trims that ~25%; the backlog drains
over one extra frame (brief, invisible slow-motion) and tick semantics are bit-identical (parity-safe). Secondary
to #1, which removes the primary backlog source.

**Landed (follow-up pass, 2026-06-15):**

- **#3 — warm-pass MSAA fix (the real find) + roster model warm-by-render.** Instrument-first showed the residual
  PIPELINE-COMPILE hitches clustered at the **bot-join window** (t=22–27), rest-dominated, "ubershader" — pointing
  at player-model pipelines. Two causes found:
  1. **A latent bug in `GpuWarmPass`:** a Vulkan graphics pipeline is keyed by its **multisample state**, but the
     warm `SubViewport` defaulted to MSAA-disabled while the main viewport runs 4× MSAA (`project.godot`). So the
     warm pass compiled the **wrong (1×) pipeline variant** and the main viewport recompiled the 4× variant on
     first draw — the *whole* warm pass (effects **and** models) was only partially effective. Fixed by matching
     the warm viewport's MSAA/AA/TAA/debanding/scaling to the main viewport.
  2. **The load-time roster warm built each player model then `QueueFree`'d it *unrendered*** (`NetGame.cs:1554`) —
     warming only the texture/material caches, never compiling the pipelines (the exact bug §12.6-2 fixed for the
     *idle* warmer, missed here). Now the 10 roster models render via `GpuWarmPass.WarmNodes` at load.
  - **Measured: mid-match PIPELINE-COMPILE 8 → 2** (the 2 residual at t≈21–23 are the last uncovered families —
    likely weapon view-models). The ~25MB model-texture build also moves to load-time.
- **#2 — IQM mesh-array reuse (safe version).** Exact-size `[ThreadStatic]` scratch reuse for the per-surface
  arrays in `IqmBuilder.BuildMesh` (a **separate pool per slot** — all six are alive until `AddSurfaceFromArrays`;
  **exact length only**, since an oversized buffer would marshal garbage-tail verts = corrupted mesh; the
  `Godot.Collections.Array` marshal copies synchronously, so reuse is safe). Reuse hits across instances of the
  same model. **Marginal** post the anim cache (the build alloc is texture-dominated and #3 moves it to load),
  but harmless and verified pixel-clean (erebus model render). Note: `ArrayPool` is *not* usable here — its
  oversized arrays violate the exact-length constraint (same reason `DecodeBuffer` can't use it).

**Then mopped up the weapon-model warm (2026-06-15):** `PrecacheWeaponModelsAsync` had the same bug — it built each
weapon v_ model then `QueueFree`'d it *unrendered*, so weapon pipelines compiled on first draw (1st-person view
model on switch / another player's 3rd-person carried weapon). Now warmed-by-render too. Correct fix for the
weapon-first-draw class.

**Still open — a deeper, stochastic residual (NOT player or weapon models).** After warming **both** rosters,
catharsis+6-bots still shows **2–6 PIPELINE-COMPILE hitches at the join window (t≈20–33)**, run-to-run variable,
occasionally a 6-compile / 67–150ms cluster. Since both player- and weapon-model pipelines are now warmed-by-render,
this residual is a different class — materials / render-states the 64×64 warm `SubViewport` doesn't replicate
(clustered lighting, the Sun's PSSM shadows, the WorldEnvironment). Replicating those is **fragile**: matching a
generic shadow-casting light *regressed* it (a different shadow variant compiled than the Sun's, wasted, and the
main scene recompiled anyway), so it was reverted. And **Godot exposes only the compile *count*, not which pipeline**,
so warm-viewport targeting is blind. Proper closure needs a Godot-side per-pipeline hook or accepting it as a
bounded, stochastic match-start cost (it is match-start, not sustained). The **MSAA-match fix is the real warm-pass
win** and stands. Recommend stopping the warm-viewport chase here.

**Deeper structural option (unchanged from PERFORMANCE_REPORT §5 S5):** moving the server sim to a worker thread
would take the whole `server.tick` (and its catch-up multiplier) off the render frame — but it's High-risk
(the `Api.Services` ambient global, the shared↔server cvar bridge) and gated on a real-match profile showing
server tick > ~2ms after the areagrid. Not warranted by this census.

**Instrumentation already added this pass:** the steady-state `ms/frame:` dump, `scene:`/`census:` counters,
new scopes (`mp.fx`/`mp.weapon`/`cev.process`/etc.), the `anim cache HIT/MISS` events, and the GC-tail
reclassifier. The next pass should add: a `sim.backlog` event (ticks ran + active soft-cap) and a per-renderer
first-draw-compile log behind `cl_debug_warm_materials` to make #1's ROI measurable.

---

## 6. Files changed (this pass)

- `game/loaders/AssetLoader.cs` — IQM AnimationLibrary + parse cache (§3, the bot-spawn-storm killer).
- `game/menu/framework/ClientSettings.cs` — `cl_maxfps` auto-cap to an engaging ceiling (§2, the biggest count win).
- `game/net/NetGame.cs` — interactive catch-up cap 4→3 (§5) + roster model **and weapon model** warm-by-render (§5/#3).
- `game/client/FrameProfiler.cs` — GC-tail reclassifier (§4) + the `refresh=NHz` env-banner field.
- `game/client/GpuWarmPass.cs` — warm `SubViewport` now matches the main viewport's MSAA/AA/scaling (§5/#3, the latent-bug fix).
- `game/loaders/models/IqmBuilder.cs` — exact-size `[ThreadStatic]` mesh-array reuse (§5/#2).
