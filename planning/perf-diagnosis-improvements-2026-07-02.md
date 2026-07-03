# Performance-Diagnosis Capability Audit — 2026-07-02

**Question:** hitching keeps recurring; how do we make diagnosing and resolving perf issues as fast as possible?
**Method:** three parallel code/doc sweeps (instrumentation internals, docs + offline tooling, in-game surface +
repro/benchmark), grounded against the live evidence in `~/XonData/logs/session-20260702-*.{log,csv}` and the
repo-root stdout logs from tonight's portal playtests. Contradictory agent claims were re-verified against code.

**Verdict:** capture is excellent — consumption, attribution-trust, reproducibility, and prevention are the gaps.
The FrameProfiler stack is a better-than-industry-typical flight recorder (8-class hitch taxonomy, per-scope
alloc, watchdog sampling, PSO counters, RenderDoc auto-capture, per-frame CSV). But almost nothing *consumes* the
data it writes (258 files / 90MB of unread CSVs since 06-14), one classifier bucket is demonstrably lying
(EXTERNAL vs watchdog disagreement), every A/B is hand-run and hand-eyeballed, feature playtests happen on Debug
builds whose censuses don't represent release, and the newest per-frame-heavy system (portals) shipped with zero
scopes. Each of those independently costs "multi-session saga" time — the pattern behind the ENet-throttle and
warpzone hunts.

---

## 1. What exists (inventory, verified)

### Capture — strong
- **FrameProfiler** (`game/client/FrameProfiler.cs`, ~1340 lines): hitch = frame > `cl_frameprofiler_hitchms`
  (12) AND > median×1.8. Classes: GC-PAUSE / PIPELINE-COMPILE / ASSET-BUILD / CPU-LOGIC / GPU-BOUND /
  VSYNC-PRESENT / EXTERNAL / MIXED, with same-class runs collapsed to `×N` lines. Each hitch one-liner carries:
  dominant scope + typical-baseline multiplier, proc/rcpu/gpu/rest split, alloc, ticks, remote.ents, watchdog
  suffix, PSO sync/async counters, prev-8-frames, correlated events (`[-1f] anim cache HIT …`), and a full
  box-drawing scope tree (ms · %fr · ×n · max · **alloc** · typ) in the session log.
- **Prof** (`src/XonoticGodot.Common/Diagnostics/Prof.cs`): per-thread lock-free accumulators, parent/self-time,
  call count + max, per-scope allocated bytes; `MainPhase` volatile is **main-thread-only** (verified
  `Prof.cs:183-205`); zero cost disabled. ~200 scopes, dense in sim (`sim.*/move.*/bot.*/net.*`) and client
  (`cw.*`, `stream.build`, `particles.cpu`, effect nodes).
- **PhaseWatchdog** (`FrameProfiler.cs:1271-1339`): background 1ms sampler of `Prof.MainPhase` once a frame
  exceeds 0.6×floor; histogram **resets per frame** via `Prof.FrameSeq` (verified line 1313).
- **GPU/present**: `ViewportGetMeasuredRenderTime` CPU+GPU, 5-way pipeline-compile counters with sync
  (surface/draw) vs async split, draw calls, VRAM; **RenderDocCapture** auto-captures on sync surface compiles
  after t>28s (≤6/session, no-op outside RenderDoc).
- **GC**: gen0/1/2 deltas, pause-time delta, per-frame alloc, per-scope alloc, and the gen2-tail reclassifier.
- **SessionProfileLog**: background writer thread → `~/XonData/logs/session-<stamp>.log` + `.csv`
  (`frame,time_s,ms,proc,rcpu,gpu,phys,rest,alloc_kb,gc0..2,gc_pause_ms,draw_calls,pipe_compiles,pipe_uber,top1,top1_ms`);
  CSV rows drop first under backpressure, text never. End-of-session summary: avg/1%low/0.1%low fps, census by
  class, worst-5 with timestamps, GC totals, alloc peak/rate/heap.
- **Release builds**: everything ships (no `#if DEBUG` strip); `cl_frameprofiler` defaults 1 debug / 0 release;
  console works in release. Zero cost when off.

### Repro bones — present but uncomposed
- `--map` / `--host`, repeatable `--cvar`, `--headless`, and `--quit-after-seconds` (`Main.cs:161`, wall-clock
  self-quit for scripted runs).
- **`_scratch/perf-run.sh`** (uncommitted): the only A/B harness — kills stale processes, launches the RELEASE
  export on catharsis with profiler forced, quits after N s, detects the new session log, greps out env/steady
  snapshots/summary/a representative tree. This drove the June investigations. It is not in `tools/`, not
  documented, bash-only.
- Headless perf benches exist (`tests/.../Perf/ServerTickPerfBench.cs` — real atelier map, ms/tick vs 72Hz
  budget; `BotPerfBench`, `TracePerfBench`, `NetSnapshotPerfBench`) — **informational only, no assertions**.
- Demo/timedemo: `DemoControl.cs` stub only on this branch; format/playback live on `claude/demo-cinematics`.

### Docs — rich fragments, no front door
Five verified postmortems (`catharsis-perf-investigation`, `hitch-resolution`, `cpu-fps-optimization`,
`engine-optimization`, `graphics-settings-audit`) + `PERFORMANCE_REPORT.md` (2026-06-10 mega-doc, stale on
status) + `NET-DEBUGGING.md` (the model: failure signatures + worked example) + `TROUBLESHOOTING.md` + `CVARS.md`.
**No profiling playbook**: nothing explains enable→capture→read-the-CSV→classify→compare. No project CLAUDE.md.
Fresh-contributor discoverability ≈ 3/10.

---

## 2. What tonight's own logs demonstrate

`_play_session7.log` (Debug build, stormkeep, 90s): **179 hitches — 102 VSYNC/PRESENT, 46 EXTERNAL, 14
CPU-LOGIC, 10 MIXED, 4 PIPELINE-COMPILE, 2 ASSET-BUILD, 1 GC-PAUSE; avg 131fps, 1%low 47, 0.1%low 7.**

1. **The EXTERNAL bucket is lying.** Three of the worst five (119.7 / 98.3 / 83.3 ms) are EXTERNAL
   ("game-side quiet — OS/compositor/driver") yet carry watchdog majorities in **named game scopes** with quiet
   prev-frames: `watchdog: 21/36 in 'bot.path'`, `20/23 in 'bot.strategy'`, `34/54 in 'bot.path'`. The watchdog
   resets per frame and MainPhase is main-thread-only, so either the frame record's proc/rest accounting
   misattributes across a frame boundary, or classification ignores watchdog evidence it should trust. Either
   way an EXTERNAL verdict currently misdirects investigations toward the OS.
2. **Debug-build censuses don't represent the game.** Tonight: `iqm.anims 255.8ms` (release has the cache),
   433.9MB allocated in one frame, 3.1GB total/90s. The env banner records `csharp=Debug` but no summary line
   says "this census is not representative"; feature playtests (portals) all ran Debug.
3. **VSYNC/PRESENT floods the census** — 102/179, mostly 12–20ms pacing blips and post-hitch recovery tails,
   burying the ~30 primary events.
4. **Unscoped code keeps winning hitches** — several `watchdog: n/n in '(unscoped)'` and `proc:other`-dominated
   CPU-LOGIC hitches; and **`PortalRenderer.cs` contains zero `Prof.Sample`** while doing per-portal camera
   re-renders, plus unthrottled per-frame `[portal]` prints (console I/O + string alloc on the frame path — a
   hitch vector by itself).
5. **The GC one-liner names the pause, not the allocator** — the 189.9MB-alloc GC hitch required reading the
   correlated events to see it was a first-time `ignismasked.iqm` build; per-scope alloc exists in the tree but
   isn't surfaced in the one-liner.

---

## 3. Findings, ranked

| # | Finding | Evidence |
|---|---|---|
| F1 | **Capture ≫ consumption**: no tool parses/summarizes/diffs the session CSVs; every A/B was hand-interleaved and eyeballed | 258 files / 89.7MB unread; `perf-run.sh` greps text only |
| F2 | **EXTERNAL classification untrustworthy** (classifier never consults the watchdog; possible frame-boundary misalignment between scope drain, frame record, and watchdog window) | §2.1 |
| F3 | **No reproducible benchmark or regression gate**: bones exist (`--quit-after-seconds`, benches) but nothing composed, asserted, or baselined — regressions are discovered by feel | agents 2+3; benches informational |
| F4 | **Debug/release confound** on playtests; no watermark, no one-command release loop | §2.2 |
| F5 | **New per-frame systems ship unscoped** (portals now; pattern also behind past `(unscoped)` verdicts) | §2.4 |
| F6 | **VSYNC/PRESENT noise** drowns census signal; recovery tails conflated with isolated pacing | §2.3 |
| F7 | **No playbook / discoverability**: knowledge scattered across 5 postmortems; NET-DEBUGGING.md pattern proven but no perf equivalent; no CLAUDE.md | agent 2 |
| F8 | **Hygiene**: 28 ad-hoc stdout logs in repo root (untracked), no XonData/logs retention, stale "dormant" comment on `net_input_trace` in ClientSettings (it IS implemented — NetGame/ServerNet/ClientNet) which fooled an auditor | repo root; `ClientSettings.cs:197` |
| F9 | **Moment-of-hitch surfacing minimal**: console line only; no overlay pin/toast; FpsPanel has no hitch counter | agent 3 |
| F10 | **GC hitches don't headline the allocator** (data exists per-scope, unsurfaced) | §2.5 |

---

## 4. Improvement plan

### Tier 1 — biggest time-to-diagnosis wins (each ≈ a day)

**P1. `tools/perf-report.py` — make the CSVs answer questions.** (F1)
Input: a session `.csv`+`.log` (default: newest). Output: percentiles (p50/p95/p99/1%low/0.1%low), hitch census
split **primaries vs recovery-tails**, dropped-frame-weighted "felt severity", top-offender histogram (`top1`
column), alloc storms (frames > N MB with their `top1`), time-cluster detection ("11 hitches cluster t=20–33 —
join window"), GC/pipe totals. `--diff baseline` compares two sessions with noise awareness (VSYNC class is
machine-load-sensitive — report deltas with spread, not single numbers). `--json` for baselines (P5/P9).

**P2. Fix the EXTERNAL/watchdog contradiction.** (F2)
(a) When the watchdog majority (≥50% samples) is a named scope, never emit EXTERNAL — emit CPU-LOGIC with a
`watchdog-attributed` marker. (b) Audit the frame-boundary alignment between `MarkFrameStart`, the accumulator
drain, the frame record, and the watchdog seq — tonight's "proc 3.8 / rest 37.2 yet 20/23 samples in
bot.strategy" is only possible if one of them is booking time to the wrong frame. (c) Distinguish
`(present-wait)` (MainPhase==null after scopes closed) from `(unscoped)` so rest-dominated frames read cleanly.

**P3. Promote the A/B harness to a first-class tool.** (F3, F4)
Port `_scratch/perf-run.sh` → committed `tools/perf-run.ps1` (+ keep the sh twin): label, map, bots, seconds,
extra cvars; defaults to the **release export**; auto-runs P1's report (and `--diff` vs a named baseline) on
completion. One command from "I feel hitching" to a classified, compared census.

**P4. Scope the portal renderer + debug-print hygiene.** (F5)
Add `portal.render` (+ per-portal children) to `PortalRenderer.cs`; register it in `TopLevelNodeScopes`. Throttle
or gate the per-frame `[portal]` prints behind debug-level ≥2. Adopt the rule (record in P6's playbook): **any
new per-frame system ships with a Prof scope in the same PR.**

### Tier 2 — keep the truth visible (1–3 days total)

**P5. Debug-build honesty + release baselines.** (F4) Watermark hitch lines and the session summary with
`DEBUG BUILD — census not release-representative` when `csharp=Debug`; check in per-map release baseline JSONs
(from P1 `--json`) so any run can answer "is this worse than known-good?".

**P6. `PERF-DEBUGGING.md` playbook** (F7), NET-DEBUGGING.md-style: symptom → class decision tree; per-class
known-causes table linking the five postmortems; CSV column reference; the P3 workflow; log locations; debug vs
release caveats; cvar reference (`cl_frameprofiler*`, `showfps`, `r_pvs_cull`, …). Cross-link from
TROUBLESHOOTING.md/README; add a minimal CLAUDE.md pointing at it so every future session starts oriented.

**P7. Moment-of-hitch surfacing.** (F9) Pin "last hitch: CLASS top-scope ms (age)" on the compact profiler
overlay; add a session hitch counter to FpsPanel; optional `cl_frameprofiler_alert` for a brief on-screen flash.

**P8. Census de-noising.** (F6) Tag hitches within ~1s after a primary as `recovery`; summary reports
primaries/tails separately (tonight's 179 becomes ~30 primaries + tails — the actionable number).

### Tier 3 — prevention (structural)

**P9. Budget-asserting benches.** Turn `ServerTickPerfBench` (+ bot/trace benches) into loose-threshold
assertions (catch 2× regressions, not 5% noise) against checked-in baselines; a `tools/perf-smoke` script for
pre-merge runs of the P3 harness on 1–2 maps.

**P10. Timedemo as the benchmark substrate** — once `claude/demo-cinematics` merges playback, a recorded demo
replayed with `--quit-after-seconds` gives deterministic A/B (removes bot stochasticity); wire into P3/P9.

**P11. Hygiene sweep.** (F8, F10) Retention in SessionProfileLog (keep newest ~50 pairs); `.gitignore` `_*.log`
and adopt scratch-dir stdout logs; fix the stale `net_input_trace` comment; surface top-alloc scope in the
GC-PAUSE one-liner; add a `(unscoped)`/`proc:other` burn-down line to the session summary so coverage debt is
visible.

---

## 5. Appendix — reading of tonight's session (current hitching, Debug build)

- Median 6.9ms is rock-solid; the *felt* problems are (a) the **join-window cluster** at t≈21–33 (bot.path
  initial pathfinding 110.7ms CPU-LOGIC + 123.8ms PIPELINE-COMPILE + the 50ms GC from a first-time player-model
  build — the known, partially-accepted match-start cost), (b) the **EXTERNAL-labeled bot.* suspects** (F2 —
  can't be trusted until the classifier fix), and (c) a 41.5ms **canvas pipeline compile** during portal render.
- 102 VSYNC/PRESENT events are mostly 12–20ms pacing blips (mailbox vsync, cap 144, Godot-reported 60Hz
  refresh) — after P8 these stop dominating the census.
- Practical now: feel-test on the release export via the P3 harness; treat Debug censuses as functional-only.
