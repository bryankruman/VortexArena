# Engine-Level Optimization — XonoticGodot (Godot 4.6.3, Vulkan/Forward+)

**Date:** 2026-06-15 · **HW:** RTX 3080 · **Method:** a Godot-internals research workflow (official docs +
godotengine/godot source/PRs) grounded in our build + live profiler measurement.
**Goal:** close the residual mid-match shader/pipeline-compile stutters via Godot-native mechanisms, and survey
engine-level perf levers + profiling tools. Builds on the catharsis (FPS) and hitch-resolution reports.

---

## 0. What landed (committed `4c11f3c`)

| # | Change | Effect | Status |
|---|---|---|---|
| 1 | **Split the 5 pipeline-compile counters** in FrameProfiler (Canvas/Mesh/Surface/Draw/Specialization) instead of one "uber" bucket | Made the residual *diagnosable* — confirmed the worst hitches are **SURFACE** compiles | ✅ |
| 2 | **Warm pipelines in the live `World3D`** (not an isolated `OwnWorld3D`) | Eliminated the **105–137ms SURFACE clusters** → worst compile hitch now **~36ms** | ✅ |

## 0.1 What landed (2026-06-16 — the config wins + RenderDoc diagnosis)

| # | Change | Effect | Status |
|---|---|---|---|
| A | **Shader Baker in the windows-client export preset** (`export_presets.cfg` `shader_baker/enabled=true`) | Ships baked `SceneForwardClusteredShaderRD/…*.vulkan.cache` in the PCK → removes first-run / post-driver-bump cold SPIR-V cost on a fresh install. **VERIFIED**: full export completed exit 0, **no `godot#112794` hang** (reached 100%, `project.binary` stored). | ✅ |
| D | **Debanding** (`project.godot` `anti_aliasing/quality/use_debanding=true`) | Kills sky/gradient banding; ~0 CPU, sub-ms GPU (we're GPU-idle). | ✅ |
| B | **Vulkan RD driver pin** — *intentionally not added.* | Empirically the build already selects **`Vulkan 1.4.341 - Forward+`** by default (no D3D12); the report's "fresh checkout could flip to D3D12" premise does **not** hold for 4.6.3. Godot strips a `rendering_device/driver.windows="vulkan"` key as redundant-vs-default, so there's nothing persistable. The preset also sets `application/export_d3d12=0`. | ☑ moot |
| — | **RenderDoc in-app auto-capture** (`RenderDocCapture.cs` + FrameProfiler hook): self-triggers a capture the instant a SURFACE compile fires, gated to mid-match (`SessionSeconds>28`). Self-guarding — no-op unless launched under RenderDoc, free to ship. | The only reliable way to capture a *stochastic, render-thread* PSO stall (the GUI "capture frame" button can't — a backgrounded window stops presenting). Reusable for any future GPU-stutter naming. | ✅ tool |

**RenderDoc finding — the residual SURFACE compile is class-identified (Open Q #1 from §4, answered).** Captured the mid-match `surface+1` frame (frame2905, t=77.6). Godot's **release build sets no Vulkan debug object names**, so a headless string-grep of the (1.8 GB) capture names nothing — the resources are anonymous. But the capture + the frame's forensic + the warm-coverage code together pin the **class**: the compile fires on a frame whose watchdog is in **`cev.process`** (the remote-entity render feed) with **`md3.morph`** active, mid-combat, 169 entities. The warm pass covers player models (IQM), weapon `v_` models, effects, **and projectile bodies+trails** — but **NOT gibs, shell casings, or map-item MD3 models**, which render through the entity feed. So the residual SURFACE compiles are **un-warmed MD3 entity models (gibs / casings / items) first-instancing in combat** — a *different* one each occurrence (hence "stochastic", ~14–49 ms, a couple per match). The **exact** per-instance material would need a GUI before/after pipeline-set diff on the capture (disproportionate for the payoff; worst case already fixed). **Candidate fix (not yet done):** extend the warm pass to render representative gib/casing/item MD3 models at load — the same warm-by-render pattern as the roster/weapon warm.

**Measured (catharsis + 6 bots, release):** the catastrophic 105–137ms SURFACE clusters are gone; residual = 2–4
small compiles (a stray `surface+1` at 14–36ms + the upstream `draw+N` misses). 2755 tests green, 0 errors, clean
spawn render (no flash).

---

## 1. The diagnosis (why the residual existed)

Godot exposes **five** pipeline-compilation counters, with very different meaning (docs):

- **SURFACE** — first-instancing of a `(mesh, material, render-feature)` variant: a **synchronous render-thread
  stall**. *The hitch culprit.*
- **DRAW** — a precompile MISS at draw time: "should never happen with the ubershader system" → a Godot bug if nonzero.
- **MESH / SPECIALIZATION** — load-time / async (benign). **CANVAS** — 2D first-draw.

Our profiler lumped Canvas+Mesh+Surface into "uber", so we were blind. Splitting them showed the 105–137ms
hitches were **SURFACE** compiles.

**Root cause:** the Vulkan pipeline key includes the **enabled render-feature set** (glow, Sky ambient) **and the
directional shadow/PSSM mode**. Our warm pass ran in an isolated `OwnWorld3D` with *none* of those, so it compiled
the wrong variant and the live first-draw (glow + Sun + PSSM) recompiled mid-match. The earlier MSAA-match fix was
necessary (MSAA is also in the key) but incomplete; a hand-built generic light *regressed* it (wrong PSSM split
variant). **Sharing the live `World3D` makes the warmed variant byte-identical by construction** — using the Sun's
exact config — which is the durable fix.

**Caches (confirmed):** the SPIR-V cache persists at `user://shader_cache` (~18MB); a persistent **VkPipelineCache**
at `user://vulkan/pipelines.forward_plus.nvidia_geforce_rtx_3080.cache` (~24MB) amortizes PSO *creation* across
runs. (A cold-vs-warm experiment that deleted only `shader_cache` showed no delta — because the VkPipelineCache,
untouched, still served the PSOs. The residual SURFACE compiles are *new variants* the warm pass never created,
not cache misses.)

---

## 2. Remaining engine plan (ranked, not yet done)

| Pri | Change | How (exact) | Effect | Risk |
|---|---|---|---|---|
| A | **Shader Baker in the export preset** | `export_presets.cfg` windows-client: `shader_baker/enabled=true` (Export ▸ Windows ▸ Shader Baker). Bake on Windows/Vulkan. | Ships ubershader SPIR-V in the PCK → removes first-run / post-driver-bump cold-compile cost on a fresh install. | M — watch godot#112794 (bake can hang ~99%); larger PCK |
| B | **Pin the Vulkan RD driver** | project.godot `[rendering]` `rendering_device/driver.windows="vulkan"` | Insurance: a fresh checkout could flip to the 4.6 D3D12 default and cold-invalidate both caches. | low |
| C | **Pre-trigger global render features at load** | During the loading screen, instance for ≥1 frame any global feature that first appears mid-match (positional shadow cubemap, reflection probe) so `_update_dirty_geometry_pipelines` doesn't storm at join. There is **no** public "precompile this pipeline" RD API in 4.6 — instancing-at-load *is* the supported mechanism. | Moves any residual all-surface recompile cluster into the (invisible) load screen. | M |
| D | **Debanding** | project.godot `anti_aliasing/quality/use_debanding=true` (set at boot — it's itself a pipeline-key change). | Removes sky/gradient banding; ~0 CPU, sub-ms GPU. Pure quality (we're GPU-idle). | low |
| E | **A/B software occlusion culling** | Toggle `r_occlusion_cull` on a big map; compare `proc` ms + draw calls. It rasterizes the occluder buffer **on the CPU** — may be net-negative now that the world is frustum-cell split. | Possible main-thread win, or confirm-leave-off. | low |
| — | **Do NOT** switch to the Mobile renderer or chase mesh-LOD / runtime VRAM compression for perf | — | Mobile caps lights (8 omni/spot) + drops clustered lighting/glow → parity regression for **zero** CPU benefit (we're CPU-bound). Mesh LOD only helps GPU-bound. | — |

The remaining `draw+N` misses are a Godot **ubershader-path gap** (a `DRAW` compile "should never happen") — the
only closure is an upstream `godotengine/godot` bug report with a clustered-light + Sun-PSSM + WorldEnvironment
minimal repro, or accepting them (they're ~15ms, a couple per match at join).

---

## 3. Profiling tools to go deeper (the user's "Godot-provided tools" + external)

Ordered by value for our case (CPU-bound main thread + render-thread compile stalls):

1. **`--gpu-profile`** (free, built-in): `XonoticGodot.exe --gpu-profile` (or `debug/settings/stdout/print_gpu_profile=true`). On a compile-cluster frame the "GPU PROFILE (total Xms)" line stays *small* — proving the cost is `vkCreateGraphicsPipelines` on the render thread, not GPU.
2. **Editor remote debugger + Visual Profiler + Monitors** (free): editor *Debug ▸ Keep Debug Server Open*; run a debug export with `--remote-debug tcp://127.0.0.1:6007`; watch the RenderingServer pipeline-compile monitors spike at bot-join. (Its "GPU" graph is render-task *CPU* time, not true GPU execution.)
3. **RenderDoc** (names *which* pipeline — the gap Godot's count-only API can't fill): Launch Application = the exported exe, enable Collect Callstacks + API Validation; F12-capture a frame inside a compile cluster + a clean frame; diff the pipeline set → the delta's callstack names the exact material/variant to ensure the warm covers it.
4. **NVIDIA Nsight Graphics** (Ampere/RTX 3080, GPU Trace + Real-Time Shader Profiler): watch the "Background Compiles" HUD + PSO-bind row at join to separate Godot synchronous PSO-create from NV-driver background recompile.
5. **.NET profiler for the CPU-bound C# main thread** (the engine profiler can't see C# methods): `dotnet-trace collect --process-id <pid>` (→ Speedscope) or Rider/dotTrace attached to the **release** process; pair with `dotnet-counters monitor --process-id <pid> System.Runtime`.
6. **Validation sanity** (once, not for timing): `--gpu-validation --gpu-abort --verbose` to rule out API misuse forcing recompiles + confirm the RTX 3080 is selected. Don't pass `--rendering-driver` alone (godot#115539 silently forces forward+).

`--verbose` also reveals whether the persistent PSO cache loads clean each launch ("Startup PSO cache (N MiB)") vs
an "Invalid Vulkan pipelines cache header" (a driver bump rejected it → every run cold).

---

## 4. Open questions

- ~~The stray `surface+1` (14–36ms) — which material/mesh variant?~~ **ANSWERED 2026-06-16 (see §0.1):** the
  *class* is un-warmed **MD3 entity models (gibs / casings / items)** rendered via the entity feed; a different one
  each occurrence. Exact per-instance naming needs a GUI before/after diff (Godot ships no Vulkan debug names);
  the actionable fix is to warm those MD3 classes by render at load. *(Not yet implemented.)*
- The `draw+N` misses — file an upstream Godot bug (ubershader should prevent `DRAW` compiles)? *(still open)*
- ~~Does the Shader Baker complete on this 683MB project (godot#112794)?~~ **ANSWERED 2026-06-16:** yes — the
  windows-client export completed exit 0, no hang, baked `SceneForwardClusteredShaderRD` caches stored in the PCK.
