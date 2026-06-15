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
| **`cl_maxfps` cap** (don't run uncapped) | Collapses the VSYNC/PRESENT class (the bulk of the count) | **Recommendation** (settings) |
| **IQM AnimationLibrary cache** | Eliminates the bot-spawn model-build storm — the catastrophic 88–140ms stutters | **Landed + verified** |
| Profiler classifier fix | Stops mislabeling GC-pause tails as "EXTERNAL/OS" | **Landed** |

**Result of the landed code fix (catharsis + 6 bots, release):**

| | Baseline | + anim cache |
|---|---|---|
| Worst frames | 139 / 106 / 88 / 71 ms | **67 / 56 / 54 / 50 ms** |
| ASSET-BUILD hitches | 22 | **1–2** |
| GC-PAUSE hitches | 11 | **1–3** |
| total (uncapped) | 985 | **476** |

The remaining felt stutters (mid-match PIPELINE-COMPILE ~67ms, the spawn-time catch-up residual) have a
ranked, well-specified fix plan in §5.

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

Controlled A/B (catharsis + 6 bots), changing only `cl_maxfps`:

| `cl_maxfps` | total hitches | VSYNC/PRESENT | 1%-low fps |
|---|---|---|---|
| **0 (uncapped)** | 985 | 825 | 51 |
| 144 | 179 | 130 | 63 |
| 250 (above sustained ~178) | 164 | 99 | 96 |

**Any cap — even 250, which barely limits the framerate — dramatically reduces the present-class hitches and
improves 1%-low.** Uncapped (`Engine.MaxFps=0`) is pathological: the engine renders as fast as possible and the
present/swapchain pacing with mailbox vsync becomes irregular, producing constant >12ms blips. This matches
PERFORMANCE_REPORT §2.1/§9 ("a cap slightly under the worst-case sustainable rate is smoother than uncapped").

> **Caveat (measured honestly):** the VSYNC/PRESENT count is also sensitive to **background machine load** — a
> later capped run during heavy concurrent build/agent activity showed 442 VSYNC frames. So the *exact*
> reduction varies, but the direction is robust across the clean runs and the prior analysis. Treat the cap as
> the top smoothness lever, not a precise number.

**Recommendation:** ship a default `cl_maxfps` cap (e.g. 250, or the display refresh) instead of 0, or at least
set it in-config. Bryan currently runs the default 0. This is the single cheapest, highest-count win — but it is
a settings/policy change (DP's default is unlimited), so it's left as a recommendation, not forced.

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

## 5. Ranked remaining plan (next pass — from the analysis workflow)

| Pri | Fix | Hitches addressed | Files | Effort / Risk |
|---|---|---|---|---|
| **1** | **Extend GpuWarmPass to all runtime-constructed material families** (effect bursts, beam, laser, MD3-morph) — cache per (class, color) and render one of each at warm. Godot compiles a pipeline on first *draw*, so warming must render it (`GpuWarmPass.WarmNodes` pattern). | PIPELINE-COMPILE (the remaining ~67ms mid-match stutters) | `EffectSystem.cs` (ConfigureBurst/BuildInfoBurst/BuildWarmupInstances), `BeamRenderer.cs:195`, `LaserRenderer.cs:114`, `ModelAnimator.cs:445`, `GpuWarmPass.cs` | M / low |
| **2** | **Pool IQM parse + decode buffers** — rent the per-surface `Vector3[]/Vector2[]/int[]` from `ArrayPool` (AddSurfaceFromArrays copies synchronously) and size-band the decode buffer; cuts the residual alloc the anim cache doesn't cover. | GC-PAUSE residual; lowers the 888ms total GC pause + 4.1GB total alloc | `IqmBuilder.cs:258-318`, `DecodeBuffer.cs` | M / medium (ArrayPool aliasing — verify no retained array) |
| **3** | **Lower the interactive catch-up cap 4→2–3** + short post-hitch ramp — halves the WeaponThink/bot.think catch-up multiplier on recovery frames (the spawn-time 50–56ms CPU-LOGIC residual). Soft-cap is faithful DP behavior; tick semantics unchanged. | CPU-LOGIC/MIXED catch-up frames | `NetGame.cs:569`, `SimulationLoop.cs` | S / low (gate on a feel-test — the prior report deliberately chose 4) |
| **4** | **Cross-spawn in-flight texture dedup + streamer sub-budget** during a multi-bot spawn — gate on whether #1's animation cache already flattens the storm (likely redundant). | residual `stream.build` MIXED frames | `NetGame.cs:3671-3711`, `BackgroundAssetStreamer.cs:35` | M / low |

**Recommended order:** #1 (the biggest remaining felt stutter), then #2, then #3 (with a feel-test), then #4
only if a re-profile still shows storm-window stream frames.

**Instrumentation already added this pass:** the steady-state `ms/frame:` dump, `scene:`/`census:` counters,
new scopes (`mp.fx`/`mp.weapon`/`cev.process`/etc.), the `anim cache HIT/MISS` events, and the GC-tail
reclassifier. The next pass should add: a `sim.backlog` event (ticks ran + active soft-cap) and a per-renderer
first-draw-compile log behind `cl_debug_warm_materials` to make #1's ROI measurable.

---

## 6. Files changed (this pass)

- `game/loaders/AssetLoader.cs` — IQM AnimationLibrary + parse cache (§3, the storm killer).
- `game/client/FrameProfiler.cs` — GC-tail reclassifier (§4).
