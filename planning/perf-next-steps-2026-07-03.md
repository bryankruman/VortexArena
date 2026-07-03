# Performance — everything left on the table (2026-07-03)

Compiled after the 2026-07-03 debug-smoothness sessions (bot-strategy fixes, JIT-optimized Debug,
texture pooling — see `planning/perf-diagnosis-improvements-2026-07-02.md` status header and the
`debug-smoothness-2026-07-03` memory). Current clean state on stormkeep debug, 6 bots:
**avg 130 fps, p50 6.9 ms, 1%-low 73, 69 hitches/86 s, gen2 ×7** (was 110/6.9/19/415/34 same morning).

Ranked by recommended order. Effort: S ≤ half day · M ≈ a day · L = multi-day. Facts verified against
this branch on 2026-07-03 (e.g. `EntityNode._Process` still per-node here; `MovementParameters` unmemoized).

## A. Bank what already exists (no new code)

1. **Commit the working tree** (52 files: profiler overhaul, bot fixes, Debug-JIT flip, texture pooling,
   tools, docs). Benefit: everything above survives; the other agent sessions stop diverging. Effort: S.
2. **Merge/rebase `feature/cpuoptimization`** (unmerged since 2026-06-18; verified absent here).
   Carries the measured June wins: R1 central entity-node drive (+dirty-gate; ~30k native↔managed
   callback crossings/s removed), R2 PVS submission gate (culled entities skip transform sync), HUD
   redraw gating (R8 + Ammo R5), dead-IsClient/camera-cache/LOD cheap wins. Benefit: steady-state
   render-submission CPU on BOTH debug and release — the core of the 228→300-fps plan. Risk: 2.5 weeks
   of parity work landed since; needs a careful rebase + perf-smoke + a real playtest. Effort: M (mostly
   verification).
3. **Re-export the release build + regenerate `tools/perf-baselines/`.** The dist exe predates ALL of
   this week's fixes (its census even shows the old EXTERNAL misattribution), so the checked-in baseline
   undersells the game and release playtests test stale code. Effort: S (export + one perf-run).
4. **Let the two queued background tasks land:** casing_shell.mdl format fix (running) and the IQM
   parse-side pooling chip (kills the last ~250 MB join-window alloc storm + its gen2). Effort: already queued.

## B. Named, data-backed next fixes (this week's profiles point straight at them)

5. **Portal render CPU** — an on-screen portal costs ~1.4 ms p50 + ~2× draw calls (debug, measured via
   the spawn-lottery discovery). Options, cheapest first: update the portal viewport every OTHER frame
   (imperceptible at 144 fps, halves the tax), tighten the distance/size render gate, trim the portal
   camera's cull mask (skip particles/tiny entities — a visible deviation, DP renders full). Benefit:
   steady-state on every warpzone map. Effort: S–M. Risk: visual parity judgement calls.
6. **Bot tracewalk tail polish** — the surviving 30–60 ms join-window CPU spikes are `bot.seed`/`bot.path`
   long walk-sims. Add a max-walk-length cap inside `BotTracewalk.CanWalk` for seed/nearest contexts
   (~40 steps), and jitter `bot_ai_strategyinterval` per bot so re-rates don't cluster in adjacent ticks.
   Benefit: worst join frames ~55 → ~25–35 ms. Effort: S. Risk: low (bounded fallbacks already exist).
7. **Scope-coverage burn-down** — every run's summary still prints 6–10 hitches owned by
   `proc:other`/`(unscoped)`. Chase the next 2–3 owners with `Prof.Sample` scopes (the watchdog names the
   neighborhoods). Benefit: future hunts start attributed — this is what made the bot fix a one-session job.
   Effort: S, recurring.
8. **perf-report post-load metrics** — 0.1%-low is pinned at 7 fps by the handful of load/join frames in
   every run; add a "post-load (t≥20 s)" percentile block so steady-state smoothness is measurable without
   load noise (keep the full-session block; label both). Benefit: honest tracking of what the player feels.
   Effort: S.

## C. Still-valid June backlog (verified not on this branch)

9. **R4 — memoize `MovementParameters.FromCvars`** (invalidate on `MoveVarsBlock.Apply`): ~90 cvar-dict
   ops × fps × replay depth ≈ 65–80k lookups/s at high fps, pure waste between snapshots. Benefit:
   steady-state client CPU, scales WITH fps (matters more now that debug runs fast). Effort: S–M. Risk: low.
10. **R6 — BIH for static-world collision** (replace the flat XY-grid `_outside` scan): the one true
    algorithmic gap vs DarkPlaces; the catharsis fix measured ~20–40× on long traces. Benefit: every
    trace consumer at once — bot tracewalks (item 6 shrinks further), particles, weapons, movement.
    Effort: L. Risk: medium (collision correctness — gate behind a cvar + golden-trace tests).
11. **R7 — Entity object pool** (recycle the object, not just the slot; 68 spawn sites): the dominant
    per-combat alloc behind gib/projectile GC spikes. Benefit: combat-burst GC pauses; composes with this
    week's texture/parse pooling to make gen2s rare everywhere. Effort: M. Risk: medium (stale-reference
    discipline).
12. **R19 + gibs/casings epic** — gibs/casings still have parity gaps and per-node physics; the June plan
    is MultiMesh batches (the particle renderer is the proven in-repo pattern). Benefit: combat-peak
    submission + hitch class. Effort: L (it's a parity epic, not just perf).
13. **R30 — engine config trims**: lower `physics_ticks_per_second` / cap `max_physics_steps_per_frame`
    (Godot's 60 Hz physics loop serves only cosmetic nodes). Benefit: small fixed tax + removes a
    hitch amplifier. Effort: S. Risk: low (verify gibs/casings still settle).

## D. Decisions/experiments (Bryan's call — each is a cheap A/B with the harness)

14. **Prefer DDS over TGA in the image resolver** (DP's `r_texture_dds_load` behavior). This week's S3TC
    pass-through only engages on dds-only files because TGA wins resolution today. Benefit: potentially
    1–2 GB less VRAM (3.3 GB today), faster loads, less decode churn; the LOOK matches DP-with-
    texture-compression (slight S3TC banding vs TGA). Do it cvar-gated (`r_texture_dds_load`, default
    off → flip after eyeballing). Effort: S.
15. **.NET GC mode experiment**: try `DOTNET_gcServer=1` (and/or concurrent tweaks) via perf-run env —
    on 24 cores, server GC collects in parallel (shorter pauses, more memory). Gen2s are rare now (×7),
    so expected win is small — but it's a zero-code A/B. Effort: S.
16. **Wire the real graphics quality settings** (per the 2026-06-14 audit most video cvars are dead;
    MSAA 4×/aniso/glow are hardcoded): player-facing perf options for weaker machines, and lower MSAA
    also shrinks the Vulkan pipeline-variant space the warm pass must cover. Effort: M–L (it's the
    settings-wiring epic, perf is a side benefit).

## E. Blocked / accepted-for-now

17. **Timedemo-based deterministic benchmarking** — the real fix for run-to-run variance (bot matches +
    spawn lottery); blocked on the `claude/demo-cinematics` branch merging playback (P10).
18. **`sv_threaded`** — MEASURED WORSE this week (p50 6.9→8.3, 1%-low 73→46): the main thread holds
    `_simGate` across ~95% of `NetGame._Process`, so the worker adds a handoff tax while spikes still
    block through the gate. Reopen only after shrinking the gate span (deep NetGame refactor); potential
    is real for populated servers. Keep default 0.
19. **Join-window PIPELINE-COMPILE pair** (~52–100 ms, 2–3 per match start): the stochastic un-warmed
    Vulkan variant residual — June's warm-pass work closed the rest; full closure needs a Godot-side
    per-pipeline hook (upstream) or accepting the bounded match-start cost. RenderDoc auto-capture is
    wired for whenever it's revisited.
20. **0.1%-low = 7 fps** — the single worst load frame each run; mitigate by staggering the roster warm
    (models across load frames) if it ever matters, but it's behind the load screen; item 8 makes the
    metric honest instead.

21. **Isolate the perf-harness profile** — the cvar-persistence analysis (memory: `cvar-persistence-model`)
    found perf runs write the REAL `~/XonData/config.cfg` (the game honors `XONOTIC_USERDIR` but
    `tools/perf-run.ps1` doesn't set it). Point the harness at a scratch userdir (also isolates the
    session-log retention from real-play logs). Benefit: captures stop mutating the daily profile; runs
    become config-reproducible. Effort: S.

## A/B discipline reminders (learned the hard way this week)

- Check `Get-Process dotnet` is idle before trusting a capture (parallel agent builds contaminate).
- Pin the portal variable (`-Cvar "cl_portal_render 0"`) or watch the report's `draws p50` gate — the
  spawn lottery doubles render load.
- Debug censuses carry the DEBUG-BUILD watermark for a reason; confirm wins on the release export.
