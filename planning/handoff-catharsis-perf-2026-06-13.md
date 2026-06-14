# Handoff — Path A: catharsis performance (get to a stable ≥72 fps)

**Date:** 2026-06-13
**Next session focus (user's words):** *"doing A, we'll do that in a separate worktree (reference this worktree in case we need any of the testing apparatus, but I think we have other profiling functions which can figure out issues with catharsis otherwise)."*

Start a **fresh worktree off `main`** for this work. The worktree below is only a *reference* for the testing apparatus + the diagnosis that motivates this task; do **not** build on its branch.

---

## Why this task exists (the one-paragraph reason)

The user reports inconsistent bunnyhop / "rubberbanding on a local server" on the **catharsis** CTF map. A deterministic test harness (built in the reference worktree) **proved** the movement inconsistency is a pure function of frame rate vs the 72 Hz sim rate:

| fps | per-frame reconcile "rubberband" |
|---|---|
| **≥72** | **0.00u — exact** |
| 60 | ~1.7u mean |
| 43 (catharsis today) | **~6u mean on the majority of frames**, even with no hitches |

So **getting catharsis to a stable ≥72 fps eliminates the movement problem entirely.** That is Path A. (Path B — re-architecting the listen-server prediction to be correct at any fps — was the alternative; the user chose A.)

The full diagnosis (root cause = two independent integer-tick accumulators diverging below the sim rate; what was ruled out; the architectural alternative) is recorded — **do not re-derive it**:
- Memory: `C:\Users\Bryan\.claude\projects\C--Users-Bryan-Projects-Xonotic-XonoticGodot\memory\camera-drift-render-smoothing.md` (read the "BHOP/MOVEMENT INCONSISTENCY" + "CONFIRMED DETERMINISTICALLY" sections).

---

## The performance reference doc — READ THIS FIRST

`PERFORMANCE_REPORT.md` (repo root) is a thorough, file:line-verified perf audit done specifically against live `FrameProfiler` measurements on this project. It is the authoritative starting point for Path A. Key sections:
- **"Already landed (don't re-do)"** (~line 25) — B2/B3, MD3 morph skip-guard, effect/trail caches, snapshot-gating, sub-tic extrapolation, etc. Don't repeat these.
- **§2.1 / §3.4** — `cl_maxfps` guidance + **godot#105750 Godot C# interop marshaling allocs** (the hidden GC class a `new`/`$"..."` grep can't find: `Set*ShaderParameter`/`GlobalShaderParameterSet` → implicit `string→StringName` alloc per call, `GetNode` NodePath allocs; **cost scales with framerate**).
- Effect spawns (§1.1), `CsqcModelEffects` recursive `Meshes()`/`GetChildren()` walks (~line 145), HUD canvas redraw (~line 148), `FindInRadius` enumerator allocs (~line 152), MD3 morph re-upload (~line 158).

Note: the report's measured baseline was "40–87 KB/frame, near-zero collections" — i.e. GC was *not* dominant in that measurement. But the live catharsis launch (below) showed a **170 ms Gen2 GC pause** early in the match, so re-measure on catharsis specifically.

---

## Live evidence already captured (from a Debug run of catharsis)

A windowed Debug launch of catharsis (2 bots, CTF) produced these `FrameProfiler` hitch-log numbers:
- median frame **~22.7 ms (~43 fps)**; frequent hitches **45–53 ms** ("ticks 3–4"/frame); one **170 ms Gen2 GC pause** (~282 MB alloc) early in the match; vram **~3574 MB**; **1087 draws**.
- Hitch scopes, dominant first: **`proc:other ~22 ms`** (the un-scoped remainder of `_Process` — the biggest unknown), then `ng.process`, `md3.morph ~1.2 ms`, `particles.cpu ~1 ms`, `server.tick`, `sim.move`, `move.pm`.

**IMPORTANT CAVEAT:** that run was a **Debug C# assembly launched via the Godot binary on the worktree** — Debug + editor-context inflates everything (esp. `proc:other` and GC). **Re-measure on a true Release build** before drawing conclusions (`run-release.sh` does the Release export; see RUNNING.md / `run-release.sh`). The *shape* (sub-72 fps, GC pause, proc:other dominant) is likely real; the magnitudes are not.

**First concrete step:** `proc:other ~22 ms` is the un-attributed `_Process` remainder. Add `Prof.Sample("...")` scopes around the major un-scoped chunks of `NetGame._Process` / the client render/HUD/entity update path so the next hitch log attributes where that 22 ms actually goes. `Prof.Sample`/`Prof.Event` + the `FrameProfiler` are the existing instrumentation (gated by `cl_frameprofiler`, debug-default-on; `cl_showfps` for the on-screen counter).

---

## How to launch + profile (in the new worktree)

- Godot binary: `C:/Program Files/Godot/Godot_v4.6.3-stable_mono_win64.exe` (windowed) or `..._console.exe` (logs to stdout).
- Build the C# first: `dotnet build XonoticGodot.csproj -c Debug` (or use `run-release.sh` for Release).
- A worktree needs its `assets/data`: create a **PowerShell junction** to the main repo's data (verified to work with the VFS — fixes menu + maps; `--data` does NOT fully work for the menu):
  `New-Item -ItemType Junction -Path '<worktree>\assets\data' -Target 'C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data'`
- Launch to the menu (the user starts their own catharsis CTF server): `"$GODOT" --path "<worktree>"`.
- Profiling: `cl_frameprofiler` (hitch log + scope tree), `cl_showfps 1`. Hitch lines show `proc/rcpu/gpu/rest`, per-scope ms, `ticks N`, GC markers, and `EXTERNAL?` (OS/compositor) tagging.
- The user notes they have **other profiling functions** to dig into catharsis — prefer those + the `FrameProfiler` scopes over the movement apparatus.

---

## Reference worktree (apparatus only — likely NOT needed for Path A)

- Path: `C:\Users\Bryan\Projects\Xonotic\XonoticGodot\.claude\worktrees\vigilant-chatelet-cbfc41`  branch `claude/vigilant-chatelet-cbfc41`.
- **All work there is UNCOMMITTED** (camera-drift fix + bhop fixed-timestep input fix + the diagnosis apparatus). Don't depend on it landing on main.
- The movement/camera test apparatus, if you ever want to *re-verify* that improved fps removed the rubberband:
  - `tests/XonoticGodot.Tests/Camera/ListenServerHarness.cs` + `tests/XonoticGodot.Tests/ListenServerDiagnosisTests.cs` — the deterministic fps-vs-rubberband proof (the table above).
  - `tests/XonoticGodot.Tests/MovementTimingTests.cs` — fixed vs variable dt hop-timing.
  - `tests/XonoticGodot.Tests/Camera/` (CameraReferenceQc, PlayerPhysicsStep, CameraPipeline) + `CameraDriftTests.cs`; `tools/camera-ref/` (scenarios + `analyze.py`/`compare.py` + README); the `--camera-trace <scenario> <out>` in-engine capture boot flag.
- These are for *movement* verification; for raw catharsis fps you almost certainly only need the `FrameProfiler` + Release build.

---

## Definition of done (Path A)

catharsis holds a **stable ≥72 fps** (Release, representative hardware/match) with no recurring multi-tick GC/hitch stalls — at which point the movement rubberband is gone by construction (proven by the harness). Verify the fps with the `FrameProfiler` hitch log (no sustained `ticks ≥2` frames, median frametime ≤ ~13.9 ms) and a quick catharsis playtest of bunnyhopping.

---

## Suggested skills

- **`run`** — launch/drive the app to profile catharsis and confirm fps before/after each change (project's run path; falls back to the Godot-binary launch above).
- **`verify`** — confirm a perf change actually raised the sustained fps / removed the hitch (run the app, read the FrameProfiler hitch log, don't trust micro-benchmarks alone).
- **`code-review`** (medium/high) — after perf edits, to catch correctness regressions in the hot path you touched.
- Read `PERFORMANCE_REPORT.md` and the memory file above **before** profiling; don't re-do "Already landed" items.
