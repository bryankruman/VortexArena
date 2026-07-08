# XonoticGodot — agent notes

Godot 4 (.NET) port of Xonotic. Game host under `game/`, engine/gameplay libraries under `src/`,
tests under `tests/`, design docs + postmortems under `planning/`.

## Build & test

```bash
dotnet build XonoticGodot.csproj -c Debug                         # the Godot host
dotnet test tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj    # full suite (real-data tests self-skip without assets/)
ci/ci.sh                                                          # the authoritative local gate
```

Toolchain paths, launch flags (`--host <map> --bots N`, `--cvar`, `--quit-after-seconds`), headless
smoke: **RUNNING.md**.

## Where to look first

- **Performance / hitching** → **PERF-DEBUGGING.md** (profiler, hitch classes, `tools/perf-run.ps1`,
  `tools/perf-report.py`). Measure before theorizing; capture on the release export, not Debug.
- **Movement / netcode** → **TROUBLESHOOTING.md** + **NET-DEBUGGING.md** (`net_input_trace`).
- **Cvars** → **CVARS.md** (regen: `python tools/find-cvars.py`). Prefix = authority, not reader.
- Past investigations → `planning/*.md` postmortems (verified, dated).

## House rules

- Any new per-frame system ships with a `Prof.Sample` scope (registered in
  `FrameProfiler.TopLevelNodeScopes`) in the same change.
- Redirected-stdout debug logs go to `_scratch/` (gitignored), not the repo root.
- Perf-relevant changes: run `tools/perf-smoke.ps1` before merging.
