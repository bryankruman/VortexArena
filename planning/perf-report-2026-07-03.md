# Performance report — the 2026-07-03 debug-smoothness campaign

One day, five rounds (two of them executed by parallel background sessions), one goal: **make the DEBUG
build — the daily playtest build — smooth**. Everything below is measured with the new
`tools/perf-run.ps1` → `tools/perf-report.py` harness on the same scenario (stormkeep, 6 bots, 75-90 s,
debug build) and committed on `claude/parity-port-waves` (mostly in `d7db8c9`). Companion docs:
**PERF-DEBUGGING.md** (the playbook), `planning/perf-diagnosis-improvements-2026-07-02.md` (the tooling
overhaul), `planning/perf-next-steps-2026-07-03.md` (the ranked backlog).

## Headline

| stormkeep debug, 6 bots      | morning baseline | end of day (clean runs) |
|------------------------------|------------------|--------------------------|
| median frame                 | 6.9 ms           | 6.9 ms (unchanged — the fight was the tails) |
| **1%-low fps**               | **19**           | **73–90**                |
| p99 frame                    | 53.8 ms          | ~13–20 ms                |
| hitches / ~86 s              | 415 (253 primaries) | ~55–70 (≤60 primaries) |
| worst frame                  | 138 ms           | ~55–82 ms (join-window pipeline compile) |
| gen2 GC collections / run    | 34               | **7**                    |
| managed alloc / run          | 3.1 GB           | **~1.0–1.1 GB**          |

The 0.1%-low stays pinned at 7 fps by ONE deterministic ~230 MB load-screen frame (chip open — §7).

---

## 1. Foundation: the profiler had a lie in it (2026-07-02→03 tooling overhaul)

None of the day's fixes would have been findable at this speed without first fixing attribution:

- **The frame-pairing skew (the big one):** Godot's `_Process(delta)` measures the PREVIOUS main-loop
  iteration, but the profiler paired that time with the CURRENT frame's scopes — so every isolated CPU
  spike logged one frame late as "EXTERNAL — OS/driver" with quiet evidence (and a `proc>ms` clamp erased
  the real number). Proven from CSV rows, fixed by finalizing each record one collector pass later.
  **EXTERNAL verdicts on the same scenario: 46 → 2.** Every later diagnosis leaned on this.
- Watchdog histograms retired per-frame (no more stale suffixes) + a veto on false EXTERNAL verdicts +
  a `(post-process)` phase marker + a measured `late` (deferred+present) column.
- Census de-noising: VSYNC/PRESENT hitches within 1 s of a primary are counted as **recovery tails**
  ("131 primaries + 86 tails" instead of an undifferentiated 217).
- DEBUG-BUILD watermark on hitch lines + summaries; GC hitches name the top-allocating scope; a
  "scope coverage debt" summary line; `cl_frameprofiler_alert` opt-in on-screen flash; session-log
  retention (newest 50 pairs); `late_ms` CSV column.
- **Tools:** `perf-run.ps1/.sh` (one-command capture → auto-report → baseline json),
  `perf-report.py` (percentiles/lows, primaries-vs-tails census, clusters, top offenders, alloc storms,
  `--diff` with noise gates; auto-un-skews pre-fix CSVs), `perf-smoke.ps1` + budget-asserting
  `ServerTickPerfBench` (XG_PERF_ASSERT=0 opt-out), `tools/perf-baselines/`.

## 2. Round 1 — bot strategy spikes (the sustained mid-match stutter)

The trees showed one bot's strategy re-rate costing **~97–145 ms in a single tick**. Anatomy and fixes:

- The tracewalk-heavy entry-seed search ran TWICE per pass (rating flood + route build, same origin) →
  **seed set now computed once, captured by the brain, and the route build DEFERRED to the next think**
  (splits the tick cost; behaviorally invisible at a seconds-scale strategy cadence).
- `NearestSeeds`: nearest-first ring ordering, reachable-seed cap 8, walk budget 24→12, traced ring
  growth clamped to 2250 qu. `Nearest`: walk budget 8 (was UNBOUNDED — a mid-air enemy player being
  goal-rated walked candidate after candidate).
- Roam-waypoint rating stopped tracewalk-Nearest-ing its way back to the node it was already holding
  (`RateWaypoint` reads the flood slot directly); `SetGoal`'s goal-side lookup now rides the QC
  `.nearestwaypoint` cache (a parity fix too); the item→waypoint cache is prewarmed 3 items/tick after
  graph load (first pass no longer pays the whole fill).
- New `bot.seed` / `bot.rate` sub-scopes make any future spike self-explanatory.
- **Result: CPU-LOGIC primaries 192 → ~40-60; no more 100 ms+ bot ticks; 1%-low 19 → ~50.**

## 3. Round 2 — Debug builds now run JIT-optimized (the single biggest lever)

- `Directory.Build.props` sets `Optimize=true` for Debug: DEBUG defines, asserts and PDBs untouched;
  step-debugging fidelity is the tradeoff (opt out per-build with `-p:XgDebugUnoptimized=true`). The
  profiler banner reports `csharp=Debug(jit-opt)` vs `Debug(unopt)` so censuses stay comparable.
- Rationale: the daily playtest build carried a 2–5× interpreter-class tax on exactly the hot loops
  (traces, 72 Hz sim, bot tracewalks, entity drive) — release semantics, unoptimized codegen.
- **Result: 1%-low 41 → 79, p99 24 → 12.7 ms, hitches 325 → 55 on the same scenario. Full test suite
  green under optimized codegen (movement goldens included).**

## 4. Rounds 3–5 — the allocation storms (load/join gen2 stutters)

The recurring 130–433 MB single-frame allocation storms (each → gen2, all-thread pauses):

- **Round 3 (this session):** per-texture FILE reads pooled — `VirtualFileSystem.ReadBytesInto`
  (grow-only caller buffer, both mounts) + `[ThreadStatic]` scratch in AssetSystem for tga/dds;
  `RgbaDecodeBuffer` made size-keyed; **DDS S3TC pass-through** (full-chain DXT1/3/5 → Godot verbatim:
  no CPU block decode, shipped mips kept, S3TC on the GPU — narrow on stormkeep because the resolver
  prefers TGA; flipping that is a listed decision). `stream.predecode` worker scope added → the biggest
  previously-unattributed allocator now has a name. Alloc 3.1 → 1.6 GB, gen2 34 → 7.
- **Round 4 (background session):** model-file reads pooled in AssetLoader (roster reads 22.8 → 2.15 MB),
  `IqmData` blend arrays FLATTENED (was one byte[4] object per vertex, ~8.5 k tiny objects/model),
  per-model anim scratch. **Parse 5× faster (roster 361 → 71 ms).** Plus `IqmParsePerfBench`.
- **Round 5 (background session, the root cause of the residual):** the `[ThreadStatic]` decode pools
  were being MULTIPLIED by `Task.Run` fan-out — a decode wave touched a dozen+ pool threads, each growing
  its own buffer set. Fixed with a process-wide rent/return decode pool + a bounded 4-thread streamer
  lane (priority-ordered) replacing `Task.Run`. **Join storms 210/201 MB → ≤48 MB; total alloc
  → ~1.0–1.1 GB; 1%-low 90 on the clean confirmation run.**

## 5. Measured verdicts (questions closed with data)

- **`sv_threaded 1`: WORSE on this workload** (portal-pinned A/B: p50 6.9→8.3, 1%-low 73→46, worst bot
  spike still blocks through the sim gate). The June audit's poor-gate-overlap warning is real; stays
  default-off; shrinking the `_simGate` span is the prerequisite to reopening this.
- **An on-screen warpzone portal costs ~1.4 ms p50 + ~2× draw calls (debug)** — discovered via the
  spawn lottery (below); portal-render CPU is now a named backlog item.
- **The stochastic join-window PIPELINE-COMPILE pair (~52–100 ms ×2-3)** remains the accepted Vulkan
  residual (needs a Godot-side per-pipeline hook; RenderDoc auto-capture stays wired).

## 6. A/B discipline — three confounds caught by the harness itself

1. **Parallel agent/dotnet sessions contaminate runs** (a capture with 13 dotnet processes alive shifted
   p50 itself). Check `Get-Process dotnet` first.
2. **The spawn lottery:** perf-run's idle camera can spawn facing a portal → objs-drawn 1874→3215,
   draws 367→779, +1.4 ms p50 — one "regression" was entirely this. `perf-report` now prints `draws p50`
   and its diff REFUSES to compare runs whose render loads differ; pin with `-Cvar "cl_portal_render 0"`.
3. **Never trust one run's fps deltas** — bot matches are stochastic; count-based metrics (alloc, gen2,
   census primaries) converge much faster than frame-time percentiles. Re-confirmed three times.

## 7. What's left (ranked details in `planning/perf-next-steps-2026-07-03.md`)

- **Open chip:** the deterministic ~230 MB MAIN-THREAD load-screen roster-warm frame
  (`PrecacheCombatSoundsAndModelsAsync`) — the last gen2 cluster and the thing pinning 0.1%-low at 7 fps.
- **Bank existing work:** merge/rebase `feature/cpuoptimization` (June's R1/R2 entity-drive + HUD gating,
  verified NOT on this branch); re-export the release build + regenerate `tools/perf-baselines/`.
- **Next hands-on fixes:** portal render CPU (half-rate updates), bot tracewalk length caps + strategy
  interval jitter, scope-coverage burn-down, perf-report post-load metrics block.
- **June backlog still valid:** memoize `MovementParameters.FromCvars`, BIH world collision (the one
  algorithmic gap vs DP, ~20–40× long traces), Entity object pool, gibs/casings MultiMesh epic.
- **Decisions:** prefer-DDS resolver flip (VRAM), `DOTNET_gcServer` experiment, graphics-settings wiring,
  isolate the perf-harness profile via `XONOTIC_USERDIR` (perf runs currently write the real config.cfg).
- **Blocked:** timedemo benchmarking (demo-cinematics merge); sv_threaded (gate-span refactor first).
