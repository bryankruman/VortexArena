# Performance campaign — status + ranked plan (2026-07-06)

Supersedes `planning/perf-next-steps-2026-07-03.md` as the active perf plan (that doc's ledger is
folded in below with per-item status verified against today's main). Goal, per Bryan: **maximum
smoothness first — remove every removable hitch without compromising fidelity/quality; fps
optimizations second, and thoroughly reviewed.** The plan starts with a measurement phase because
every number we have predates the last three weeks of merges.

---

## 1. Where we are

### Measured wins already banked (main @ b49f235)

| Campaign | Result |
|---|---|
| Hitch-class census + cascade fixes (06-14) | 985 → 52 hitches/session |
| Catharsis sustained fps (06-14) | 68 → 228 fps (crosshair-trace broadphase) |
| Pipeline warm pass (06-15) | join PSO compiles down to a 2–3× ~52–100 ms residual pair |
| Bot strategy melt (07-03 r1) | 97–145 ms bot ticks gone; CPU-LOGIC primaries 192 → ~40–60 |
| Debug JIT-opt flip (07-03 r2) | debug 1%-low 41 → 79, p99 24 → 12.7 ms |
| Texture/IQM/decode pooling + bounded streamer lane (07-03 r3–5) | alloc/run 3.1 → ~1.0 GB, gen2 34 → 7, join storms 210 → ≤48 MB, 1%-low 90 (stormkeep debug) |
| Profiler attribution overhaul (07-02→03) | frame-pairing skew fixed — false EXTERNAL verdicts 46 → 2; census/diff/baseline tooling (`tools/perf-run.ps1`, `perf-report.py`, `perf-smoke.ps1`) |
| **R1/R2 entity-drive + HUD gating (merged 07-06, PR #3)** | central `DriveEntityNodes` + dirty-gate (~30k native↔managed crossings/s removed), PVS-culled entities skip transform sync, HUD faded-panel skip + Ammo redraw gate, dead-IsClient/camera-cache cheap wins — **the June "render-submission CPU" levers are now on main, but their realized win on release has never been measured** |

### What changed since the last capture (all unprofiled)

- **PR #3 merged** — R1/R2 plus the rounds-5–15 playtest marathon (#29–#43): lightgrid model
  lighting (per-frame trilinear sample + `grid_lit` shader branch), animMap/rgbGen-wave animated
  shaders, unshaded additive stages, decal path rework, FFA colors. New per-frame work **and new
  shader variants** — `grid_lit` appears nowhere in the warm pass (verified today), so first-fire /
  first-sight PSO compiles are a live regression risk.
- **Release export refreshed today** (dist 07-06 15:30) but `tools/perf-baselines/` still holds one
  catharsis json from 07-03 (pre-merge) — the checked-in baseline no longer describes the game.
- Unmerged perf-relevant branches queued behind this: `feature/client-map-load` (4 ahead),
  `feature/anim-smoothness-ragdolls` (crossfade two-pass, opt-in ragdolls),
  `claude/view-bobfall-idle-sway`, `claude/movement-fixes`, `fix/warpzone-view-smoothing`.

### The 07-03 backlog ledger, re-verified against today's code

| # | Item | Status (verified 07-06) |
|---|---|---|
| A.1 | Commit working tree | ✅ d7db8c9 |
| A.2 | Merge `feature/cpuoptimization` | ✅ PR #3 → b49f235 |
| A.3 | Re-export release + regen baselines | ◐ export fresh (today); **baselines stale, catharsis-only** |
| A.4 | Pooling chips | ✅ rounds 4–5 |
| B.5 | Portal render CPU (~1.4 ms p50 + 2× draws) | ❌ open — `PortalRenderer.cs:275,423` still `UpdateMode.Always` |
| B.6 | Bot tracewalk tail caps + strategy-interval jitter | ❌ open — `BotBrain.cs:358` un-jittered; 30–60 ms tails remain |
| B.7 | Scope-coverage burn-down | ♻ recurring (registry is well-kept: portal.render/nadeorbs/dynlights added) |
| B.8 | perf-report post-load metrics block | ❌ open — no post-load split in `perf-report.py` |
| C.9 | R4 memoize `MovementParameters.FromCvars` | ❌ open — still per-player-move dict reads |
| C.10 | R6 BIH static collision | ❌ open (the one algorithmic gap vs DP; ~20–40× long traces) |
| C.11 | R7 Entity object pool | ❌ open (dominant combat-burst allocator) |
| C.12 | Gibs/casings epic | ❌ open — they **never spawn at all** today (fidelity gap first, perf design built in) |
| C.13 | R30 physics-tick config trim | ❌ open |
| D.14 | Prefer-DDS resolver (`r_texture_dds_load`) | ❌ open — cvar doesn't exist |
| D.15 | `DOTNET_gcServer` A/B | ❌ open — csproj still workstation+concurrent (deliberate; A/B never run) |
| D.16 | Graphics-settings wiring | ❌ open (separate epic; most video cvars still dead stubs) |
| E.17 | Timedemo benchmarking | ⛔ blocked on demo-cinematics playback (recorder T62 exists, uncommitted) |
| E.18 | `sv_threaded` | ⛔ parked — measured WORSE (p50 6.9→8.3); gate-span refactor is prerequisite |
| E.19 | Join-window PIPELINE-COMPILE pair | ⚠ was "accepted residual" — **re-opened**: new shader variants may have grown it |
| E.20 | ~230 MB load-screen roster-warm frame | ❌ open — `NetGame.cs:2297` unchanged, pins 0.1%-low at 7 fps |
| E.21 | Isolate perf-harness userdir | ❌ open — `perf-run.ps1` still writes the real `~/XonData` |

### Known open hitch inventory, by when the player feels it

1. **Mid-combat (worst):** gen2 GC bursts from entity/projectile spawns (gen2 ×7/run remain; R7);
   *suspected* new first-fire/first-sight PSO compiles from the weapon-render marathon (unmeasured).
2. **Join window (first ~20 s):** the stochastic PIPELINE-COMPILE pair (~52–100 ms ×2–3); residual
   30–60 ms `bot.seed`/`bot.path` tracewalk tails; strategy re-rates clustering on the same tick.
3. **Steady drains (fps, not hitches):** portal viewport ~1.4 ms p50 + 2× draws when facing one;
   `FromCvars` ~65–80k dict ops/s; 4 always-on HUD panels force-redrawing every frame; brute-force
   long traces (BIH gap) taxing bots/particles/weapons.
4. **Load screen (cosmetic but deterministic):** the ~230 MB single-frame roster warm (55 ms GC
   pause, 0.1%-low pinned).

---

## 2. The plan — phases in execution order, items sorted biggest-win-first

**Phase 1 is the point:** nothing measured describes today's build. Every later phase's ranking is
provisional until the census lands; expect Phase 1 to re-rank Phases 2–3 (that's its job).

### Phase 0 — sharpen the instruments (½ day, do before any capture)

| # | Item | Why first | Effort |
|---|---|---|---|
| 0.1 | **perf-report post-load block** — add a `t ≥ 20 s` percentile/census section alongside the full-session one | separates "what the player feels mid-match" from load noise; makes 0.1%-low honest | S |
| 0.2 | **Isolate harness userdir** — `perf-run.ps1` sets `XONOTIC_USERDIR` to a scratch profile | captures stop mutating the daily config.cfg; runs become config-reproducible | S |
| 0.3 | **Fixed-cvar capture profile** — pin `cl_portal_render`, `cl_autopause 0`, `cl_maxfps` in the harness scratch config so every census is comparable by construction | kills the two known A/B confounds permanently | S |

### Phase 1 — the census (Bryan's explicit ask; ~1 day of captures, quiet machine)

All on the fresh release export, two runs per cell (count-based metrics converge; fps percentiles
need the two-run rule), `Get-Process dotnet` idle, portals pinned except where they're the variable.

**2026-07-06 revision (Bryan):** the idle stand-at-spawn camera is not representative — it never
traverses the map, never sees gunplay, and exercises none of the weapon-render variants. Captures now
default to perf-run's **demo scenario**: the host spectates a living bot first-person
(`cl_bench_spectate`, with view-entity hiding + `cl_spectate_smoothangles`), every bot carries the 8
core weapons (`g_weaponarena`) and rotates through them one-by-one (`bot_ai_weapon_rotate 8`), forced
respawn keeps everyone fighting. This replaces the artificial "weapon sweep" and "mid-match join"
cells — a spectated demo run IS a continuous weapon sweep with real traversal. The debug shakedown
already validated the approach: 6 PIPELINE-COMPILE primaries (worst 114 ms, one at t=62 s mid-match)
where the idle camera saw 2 small ones.

| Capture (demo scenario unless noted) | Question it answers |
|---|---|
| **catharsis, 6 bots, 90 s ×2** | Steady-state census on the busiest map + the R1/R2 anchor. Idle-scenario reads from today: capped avg 134/125, uncapped p50 5.0 ms (~200 fps ceiling). |
| **stormkeep, 6 bots, 90 s ×2** | Census + new baseline. Idle read: post-load nearly clean (4–5 primaries/80 s, 0.1%-low 90+) — demo shows what that misses. |
| **solarium + xoylent, 6 bots, 90 s** | Two more DM maps (all stock maps ship waypoints) — map-varied hitch classes, streaming, different material sets. |
| **catharsis uncapped (`-Cvar "cl_maxfps 1024"`) ×2** | Throughput headroom vs the 144 auto-cap (cl_maxfps 0 ⇒ `max(144, refresh)` — the deliberate 06-14 present-jitter fix; "uncapped" is a diagnostic mode only). |
| **catharsis + stormkeep, `-Scenario idle`** | The old floor camera, for continuity with the 07-03 numbers and as the render-load control. |

Deliverables: regenerated `tools/perf-baselines/` (demo-scenario catharsis + stormkeep), a hitch census
table with named owners per class, the scope-coverage debt list, and a **re-ranked Phase 2/3**.
Also read the census for surprises from the three weeks of unprofiled merges — new scopes, new
allocators, anything the ledger above doesn't predict.

### Phase 1 RESULTS — the 2026-07-06 census (release export @ this branch, demo scenario, 90 s, 6 bots)

Post-load (t ≥ 20 s) numbers; two runs where shown. `idle ctl` = the old stand-at-spawn camera on the
same export. Older idle cells ran on the morning export (pre-bench code — inert difference).

| cell | pl avg fps | pl p99 | pl 1%low | pl 0.1%low | pl hitch ms | pl primaries | pipe (total/sync) |
|---|---|---|---|---|---|---|---|
| catharsis demo A/B | 128.2 / 127.4 | 16.7 / 20.8 | 60 / 48 | 11 / 14 | **6037 / 6494** | **245 / 250** | 128/65 · 134/68 |
| catharsis idle ctl | 127.3 | 13.9 | 72 | 32 | 2306 | 73 | 116/62 |
| xoylent demo | 136.4 | 15.9 | 63 | 29 | 2371 | 170 | 124/62 |
| solarium demo | 137.2 | 12.7 | 78 | 18 | 2376 | 105 | 138/71 |
| stormkeep demo A/B | 143.0 / 143.4 | 8.3 / 8.3 | 120 / 120 | 60 / 60 | **331 / 213** | **23 / 12** | 133/67 · 129/65 |
| catharsis demo uncapped ×2 | 150.0 / 149.4 | 16.7 / 16.8 | 60 / 60 | 14 / 23 | 5855 / 6424 | (census not comparable uncapped) | 136/69 · 134/68 |

**Headline findings (each reproduced across runs):**

1. **The idle camera was hiding most of the problem.** Real gameplay (spectated bot, all weapons,
   forced respawn) shows 245–250 post-load primaries / ~6.2 s over-budget on catharsis where the
   idle control shows 73 / 2.3 s. Smoothness work must be measured with the demo scenario from now on.
2. **`ng.process` — the server tick's bot AI under sustained combat — is the #1 steady-state hitch
   owner on every busy map**: 3.2–3.8 s over-budget/90 s on catharsis, 2.3 s on solarium AND xoylent
   (the watchdog names `bot.seed`/`bot.rate`). Constant kills + forced respawns re-rate strategies far
   more often than the idle match did. The 07-03 round-1 fixes hold (no 100 ms+ single ticks), but the
   30–60 ms tail class is now the dominant mid-match stutter, and it clusters (un-jittered
   `bot_ai_strategyinterval` — `BotBrain.cs:358`).
3. **`hud.trueaim` re-emerges on open maps — 1.3–2.4 s over-budget on catharsis** (≈0 on
   stormkeep/xoylent, 118 ms solarium). The panel traces a 32768 qu ray + a box trace per frame; its
   06-14 static-aim cache never misses on an idle camera (why nobody saw it since) but a real/spectated
   player re-aims every frame. Cheap fix exists in-tree: `cl_crosshair_trueaim_rate` (default 0);
   structural fix is R6 BIH. Also explains part of catharsis-vs-stormkeep gap.
4. **The weapon-variant PSO warm gap is real and map-independent**: ~62–71 SYNC pipeline compiles in
   every join window (any map) + 3–6 PIPELINE-COMPILE primaries per demo match — worst measured
   114.5 ms (debug) / 74.5 ms mid-match. The warm pass has no coverage for the r12–r15 shader variants
   (`grid_lit`, animMap cycling, unshaded additive — verified absent).
5. **Stormkeep steady state is genuinely near-clean** (0.1%-low 60, p99 8.3 under full combat) — the
   07-03 campaign's wins were real; catharsis/xoylent-class open maps are where the remaining work is.
6. **The load-phase roster warm grew**: 404 MB single-frame alloc on catharsis (230 MB stormkeep),
   plus a second unattributed 277 MB `proc:other` storm at t≈5 s (catharsis only) and 104 MB at t≈1 s
   — three gen2s before the match starts, 0.1%-low pinned at 7–8 everywhere (full-session).
7. **`cl_maxfps 0` is NOT uncapped**: auto-cap = `max(144, refresh)` (deliberate 06-14 present-jitter
   fix, ClientSettings.cs — capped 144 measured 5× fewer hitches than 256). Uncapped demo throughput:
   p50 6.1–6.2 ms (~163 fps) vs idle-uncapped 5.0 ms (~200) — real gameplay costs ~1.1 ms/frame of
   CPU headroom. The June "228 fps" anchor predates the cap and the r9+ render features; treat 200
   (idle) / 163 (combat) as today's uncapped catharsis reality.
8. Constants: gen2 ×6 per run everywhere (combat alloc 1.9–2.0 GB/90 s — R7 entity pool still the
   named fix); `proc:other` scope debt 350–420 ms per run (burn-down list); `stream.predecode`
   200–370 ms in-play streaming; `wz_portal_lookat` appears inert (portal cell showed no draw
   increase — investigate when portal work starts; the demo camera crosses warpzones organically).

**Re-ranked Phase 2 (hitches) per this census:**
2.1 bot-AI combat cost (`ng.process`/`bot.rate` — jitter + walk caps + a fresh profile of what melts) →
2.2 `hud.trueaim` rate-cap + catharsis long-trace investigation →
2.3 weapon-variant warm coverage (kills the mid-match PIPE class) →
2.4 roster-warm frame staggering (+ name the 277 MB `proc:other` storm) →
2.5 R7 entity pool (gen2 ×6) → 2.6 R30 physics trim.
Phase 3 (fps) ranking unchanged except: BIH (R6) rises — it now underwrites BOTH 2.1 (tracewalks)
and 2.2 (trueaim rays); portal half-rate stays top of the pure-fps list.

### Phase 2 — kill the known hitches (smoothness; biggest player-felt win first)

| # | Item | Expected win | Effort / risk | Verification |
|---|---|---|---|---|
| 2.1 | **Warm the new shader variants** (if Phase 1 confirms): extend GpuWarmPass/material warm list to `grid_lit`, animMap-cycling, unshaded-additive combos | removes *mid-combat* first-fire freezes — the worst possible hitch timing | S–M / low | weapon-sweep capture goes PSO-clean; RenderDoc auto-capture idle |
| 2.2 | **R7 Entity object pool** (recycle on the 68 spawn sites) | the dominant combat-burst allocator → gen2 ×7 → ~0 mid-match; composes with the 07-03 pooling | M / medium (stale-reference discipline) | perf-smoke alloc counters; census gen2 row; full suite (2959) |
| 2.3 | **Bot tracewalk tail caps + strategy jitter** — max-walk-length in seed/nearest contexts (~40 steps), jitter `bot_ai_strategyinterval` per bot (`BotBrain.cs:358`) | worst join frames ~55 → ~25–35 ms; de-clusters re-rates all match | S / low (bounded fallbacks exist) | join-capture census; bot behavior spot-check (they still find items) |
| 2.4 | **Stagger the load-screen roster warm** — spread `LoadSkeletalModel` parses across frames or route through the bounded streamer lane with the loading bar as backpressure (`NetGame.cs:2297`) | the deterministic ~230 MB / 55 ms-GC frame; unpins 0.1%-low | S–M / low | dedicated load-phase capture (load frames get no hitch trees — needs `cl_frameprofiler_dump`) |
| 2.5 | **R30 engine config trim** — lower `physics_ticks_per_second` / cap `max_physics_steps_per_frame` (Godot's 60 Hz loop serves only cosmetic nodes) | removes a hitch *amplifier* (catch-up multipliers) | S / low | verify casings/gibs-class cosmetic physics still settle once they exist |

### Phase 3 — steady-state fps (each item ships with an A/B + review; fidelity-neutral by default)

| # | Item | Expected win | Effort / risk | Review gate |
|---|---|---|---|---|
| 3.1 | **Portal render CPU** — half-rate portal viewport updates (cvar, e.g. `cl_portal_update_interval`, default keeps full rate; flip after eyeballing) + tighten distance/size gate | ~1.4 ms p50 + ~2× draws whenever a portal is visible — the largest single measured steady drain | S–M / visual-parity judgment (Bryan A/Bs the half-rate look at high fps) | warpzone on/off capture pair; screenshot compare at crossing speed |
| 3.2 | **HUD R5 completion** — decouple HealthArmor/Weapons/Powerups/Crosshair animation state (damage-ghost, selection-slide, low-health pulse) from `DrawPanel` into `_Process`, then `NeedsRedraw`-gate them | 4 always-on panels × full redraw × fps today | M / medium (the animation-state coupling is why it was deferred) | pixel-compare HUD goldens if available; playtest the pulse/slide anims |
| 3.3 | **R4 memoize `FromCvars`** — version-stamp the cvar store, rebuild the struct only on `MoveVarsBlock.Apply`/cvar write | ~65–80k dict lookups/s at high fps, pure waste between snapshots; scales WITH the fps we're adding | S–M / low now that sv_threaded is parked (the June staleness concern) — key per store anyway | movement goldens (byte-identical contract); `net_input_trace` spot-check |
| 3.4 | **R6 BIH for static world collision** (cvar-gated, golden-trace tests) | the one algorithmic gap vs DP — measured ~20–40× on long traces; pays out to bots, particles, weapons, movement at once | **L / medium** (collision correctness) | golden-trace suite BEFORE the swap; A/B cvar in captures; bot-match smoke |
| 3.5 | **Prefer-DDS resolver flip** (`r_texture_dds_load`, default off → Bryan eyeballs) | 1–2 GB VRAM (of 3.3), faster loads, less decode churn; slight S3TC banding = deliberate fidelity call | S / fidelity decision | side-by-side screenshots; Bryan decides the default |
| 3.6 | **`DOTNET_gcServer` A/B** via perf-run env | probably small (gen2 already rare) but zero code | S / none | one capture pair |

Ordering rationale: 3.1 is the largest *measured* number; 3.2/3.3 are medium-certainty June
estimates that R1/R2's merge didn't touch; 3.4 is the biggest total win but longest lead time —
start it once the quick levers are banked, or immediately if Phase 1 shows traces dominating.
Per Bryan's instruction every one of these gets a thorough review: correctness gates listed above,
plus `tools/perf-smoke.ps1` (house rule) and a before/after census attached to the PR.

### Phase 4 — fidelity work that must land perf-clean (not perf items; listed so they don't regress the campaign)

- **Gibs/casings epic** (tasks #6–8): today they never spawn — pure fidelity gap. Build them
  MultiMesh/RID-batched with world-trace bounces + instance-color fades from day one (the
  FaithfulParticleRenderer pattern), never per-node `Node3D+_PhysicsProcess`. Perf-smoke before merge.
- **Merging the queued branches** (anim-crossfade+ragdolls, view-bobfall, client-map-load,
  movement-fixes, warpzone-view-smoothing): each adds per-frame work → house rule applies
  (`Prof.Sample` scope + `TopLevelNodeScopes` registration in the same change), plus a
  before/after capture for the anim branch (two-pass crossfade touches every animated entity).
- **Graphics-settings wiring** (the dead-cvar audit): player-facing scaling for weaker machines;
  lower MSAA also shrinks the PSO variant space the warm pass must cover.

### Phase 5 — the long game

- **Timedemo determinism** (blocked on demo-cinematics playback): the real fix for run-to-run
  variance; would turn every A/B above from two-run-rule statistics into exact replays.
- **`sv_threaded` gate-span refactor**: only if populated-server throughput becomes a goal;
  measured worse as-is — do not flip.
- **Scope-coverage burn-down** as recurring discipline: chase the `proc:other`/`(unscoped)`
  owners the census names each round.

---

## 3. Standing discipline (all learned the hard way; do not relearn)

- Release export for verdicts; debug censuses are watermarked for a reason.
- Two-run rule: never trust one run's fps delta; alloc/gen2/census counts converge first.
- `Get-Process dotnet` idle before any capture (parallel agent builds contaminate).
- Pin the portal variable or watch `draws p50` — the spawn lottery doubles render load.
- Engaged `cl_maxfps` cap; `cl_maxfps 0` is pathological present pacing, not a benchmark.
- Don't re-chase: TGA/DDS decoders (fully pooled), `Godot *TrackInsertKey` (zero-alloc ptrcalls),
  sv_threaded (parked), the pre-07-03 EXTERNAL verdicts (they were a profiler bug, since fixed).
