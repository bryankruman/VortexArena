# Running & Testing XonoticGodot

Operational reference for building, running, and smoke-testing the port. A scratchpad for tricks — **add to the
"Tricks & techniques" section as we learn more** (visual tests, profiling, dedicated server, etc.).

---

## Toolchain locations (verified 2026-06)

| Tool | Path | Notes |
|---|---|---|
| **Godot 4.6.3 (GUI/editor)** | `C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64.exe` | The **mono/.NET** build — required (the plain build can't run C#). |
| **Godot 4.6.3 (console)** | `C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe` | Same engine, but **writes to stdout** — use this for headless/CLI runs so you capture `GD.Print` + errors. |
| Godot bundled C# packages | `C:\Program Files\Godot\GodotSharp\Tools\nupkgs` | Holds `Godot.NET.Sdk 4.6.3` etc. `XonoticGodot/nuget.config` adds this folder as a package source (exact editor parity + offline builds). The 4.6.3 packages **are** also on public NuGet (verified 2026-06) — CI removes this source and restores from nuget.org (see `.github/workflows/ci.yml`). |
| .NET SDK | `dotnet --version` → 9.0.308 (builds the `net8.0` targets) | net8.0 ref pack auto-restores. |
| Project root | `C:\Users\Bryan\Projects\Xonotic\XonoticGodot` | `project.godot` + `XonoticGodot.csproj` (the Godot host) live here. |
| Xonotic asset data | `assets/data/` (in-tree, gitignored) | Downloaded by `download-assets.sh` from the upstream Xonotic GitLab repos + official release. The VFS mounts this at runtime (see `GameDemo.DataPath`, default `res://assets/data`). **`Base/` is only the historical port source — the game no longer reads it.** |

**Tip — set an env var once per shell** so commands/tests are short:
```bash
# bash (git-bash / WSL-style)
export GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
```
```cmd
:: cmd / PowerShell
set GODOT="C:\Program Files\Godot\Godot_v4.6.3-stable_mono_win64_console.exe"
```

---

## Build

The Godot-free libraries + tests build with the plain .NET SDK; the Godot host needs the Godot SDK (restores from
the bundled source via `nuget.config`).

```bash
cd C:/Users/Bryan/Projects/Xonotic/XonoticGodot

# libraries + tests (Common, Engine, Net, Assets, Server) — fast, no Godot needed
dotnet build tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj -c Debug
dotnet test  tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj   # ~1160 tests, incl. real-data ones (skip w/o assets)

# the Godot host (game client/server). Outputs into .godot/mono/temp/bin so the editor/engine picks it up.
dotnet build XonoticGodot.csproj -c Debug
```

The SourceGen analyzer: `dotnet build src/XonoticGodot.SourceGen/XonoticGodot.SourceGen.csproj`.

---

## CI (GitHub Actions + the local mirror)

`.github/workflows/ci.yml` runs on every push/PR to `main` (see `planning/decisions/ADR-0014`):

- **test** — the full xUnit suite on ubuntu-latest. **No assets in CI**: the ~18 real-data test
  classes self-skip, so a green badge proves *less* than a local run.
- **build-host** — `dotnet build XonoticGodot.csproj` from a clean clone, restoring `Godot.NET.Sdk`
  purely from nuget.org (CI first runs `dotnet nuget remove source godot-editor` because the
  Windows-only local source in `nuget.config` would hard-fail on a Linux runner).

Packaged **releases** live in a separate workflow, `.github/workflows/release.yml` (push a `v*` tag →
fat per-platform zips published to GitHub Releases). See **[RELEASING.md](RELEASING.md)**.

**The authoritative pre-push gate is the local mirror** (assets present → real-data tests + the
headless boot smoke actually run):

```bash
ci/ci.sh              # libs+tests build, full suite, host build, headless smoke
ci/ci.sh --export     # + both export presets (needs the 4.6.3 mono export templates installed)
ci\ci.ps1             # PowerShell wrapper around the same script
```

---

## Dedicated server (v1 = headless listen server)

There is no separate server binary yet — `--headless --host <map>` runs the full host with a dummy
renderer (the same `NetGame` listen server `--host` uses; a true client-less host like DP's
`ca_dedicated` is a deferred Shell/NetGame seam — ADR-0014). From the repo:

```bash
"$GODOT" --headless --path . --host stormkeep --gametype dm --bots 2
# a second, windowed instance joins it:
"$GODOT" --path . --connect 127.0.0.1
```

A healthy boot prints `[MapLoader] '<map>' surfaces: …`, `[bots] waypoints for '<map>': nodes=N` (once the
bot fill kicks in at sim time 2.5 s), and `handshake accepted`. For scripted/CI runs add
`--quit-after-seconds <s>` so the host exits on its own — Windows `timeout` does NOT kill the Godot child,
and an orphaned host keeps UDP 26000 bound (the next run then fails with "Couldn't create an ENet host";
clean up strays with `powershell "Get-Process Godot* | Stop-Process -Force"`).

For a packaged install, `tools/run-dedicated.sh` (shipped beside the exported `linux-dedicated`
binary by `tools/package.sh`) `cd`s to its own directory first, matching upstream's
`xonotic-linux-dedicated.sh`. The exported build resolves `assets/data` relative to the **executable**
(`GameDemo.ResolveDataPath` — exe-dir, plus the macOS `../Resources` bundle path), so the data just has
to sit beside the binary; the launcher is a convenience, not a requirement (`--data <path>` overrides).

---

## Run headless (smoke test — what CI / an agent should use)

Runs `Main.tscn` for N frames then quits, printing everything to stdout. This is the **non-visual "does it run
without errors" check.**

```bash
"$GODOT" --headless --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot" --quit-after 200
```

- `--headless` — no window (dummy display/renderer; logic + asset loading still execute).
- `--quit-after 200` — auto-quit after 200 frames so it doesn't run forever (`_Process` loops otherwise).
- First run also imports assets + may build the C# solution (slower); subsequent runs are quick.

**One-liner smoke test** (build host, run, assert clean) — copy/paste:
```bash
cd C:/Users/Bryan/Projects/Xonotic/XonoticGodot && \
dotnet build XonoticGodot.csproj -c Debug --nologo -v q | grep -E "Build succeeded|error" && \
timeout 180 "$GODOT" --headless --path "$PWD" --quit-after 200 > /tmp/run.log 2>&1 ; \
echo "hard errors: $(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' /tmp/run.log) | warnings: $(grep -c 'WARNING:' /tmp/run.log)" ; \
grep -iE "XonoticGodot boot|GameDemo\]|loaded .* shaders|collision brushes|spawned" /tmp/run.log
```

**Expected clean output** (hard errors: 0, warnings: 0):
```
=== XonoticGodot boot ===
[AssetSystem] loaded 2439 shaders from 185 scripts.
[GameDemo] mounted '.../assets/data' (9 search paths, 2439 shaders).
[GameDemo] config: 4593 cvars from 16 cfg files (105 aliases, 0 missing).
[GameDemo] spawned sample model 'models/player/erebus.iqm'.
[GameDemo] map 'maps/stormkeep.bsp' loaded: 5234 collision brushes.
```
(`Main.cs` now defaults the demo to the real `maps/stormkeep.bsp`; set it back to `maps/_init/_init.bsp`
for the lightest possible smoke test.)
Error patterns to grep for: `^ERROR:`, `SCRIPT ERROR`, `Unhandled exception`, `WARNING:`, `at XonoticGodot.` (managed
stack frames). Godot prints managed exceptions with a `WARNING:`/`ERROR:` banner + a C# stack trace.

---

## Run visually (the editor — to actually *see* it)

Headless doesn't render. To walk around the scene:

1. Launch `Godot_v4.6.3-stable_mono_win64.exe` → **Import** → pick `XonoticGodot/project.godot`.
2. Top-right **Build** (🔨) to compile the C# solution, then **Play** (F5) → runs `Main.tscn`.
3. Controls: **WASD** move, **mouse** look, **Space** jump, attack key fires the blaster. (See `game/PlayerController.cs`.)
4. Or from CLI, windowed: `"$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot"` (omit `--headless`).
5. For an **automated frame an agent/CI can inspect**, add `--screenshot <path>` (writes a PNG then quits) —
   see Tricks → *Visual capture* below.

---

## Visual QA (T5 — Wave A5)

Verifying the renderer is **split in two**, because the headless renderer (`dummy_video`) renders *nothing* —
`GetViewport().GetTexture().GetImage()` is null headless (`game/ScreenshotHook.cs`), so no rendered-frame or
pixel check can run in CI.

| Half | What it checks | How | Where |
|---|---|---|---|
| **Headless (automated)** | every stock map *loads* + has renderable/collidable geometry; every model *loads* + has a valid bone parent-chain (IQM additionally: non-singular bind pose; DPM/MD3 skip the determinant/unit-scale check per shipped DP model baselines); every `.shader` *compiles* (parses, no hard failure) | `VisualQaTests.cs` (pure xUnit over the parsed asset structures — no GPU, self-skips without `assets/data`) | `ci/ci.sh` step 5; `dotnet test … --filter VisualQa` |
| **Windowed (manual eye-check)** | actual on-screen *correctness*: lightmap/deluxemap direction, patch smoothness, flare quads, material color, bone pose | `tools/visual-qa.sh` captures a real frame per map + per model into `screenshots/`; then a human (or an agent via the Read tool) eyeballs each PNG against the checklist below | `tools/visual-qa.sh` + the checklist below |

**The headless half is NOT a substitute for the eye-check.** A map can load with all counts in range and still
render wrong (magenta walls, flat lighting, faceted curves). The structural assertions only catch *load* and
*structure* regressions; visual correctness is **only** decidable on-screen.

### Capture the frames

```bash
export GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
tools/visual-qa.sh                 # every stock map + every hero model → screenshots/
tools/visual-qa.sh --map stormkeep # just one map
tools/visual-qa.sh --models        # just the player models
tools/visual-qa.sh --frames 240    # let shadows/streaming settle longer before each shot
```

Each capture opens a window for ~1.5 s and self-quits (windowed only — `--headless` writes a blank PNG). The
PNGs land in `screenshots/` (git-ignored, `.gdignore`'d). **`Read` each PNG to view it.**

### Windowed checklist (run per captured PNG)

Compare to an upstream Darkplaces baseline screenshot of the same map/model where one has been collected
(collecting baselines is a future task). Until then, judge against the Base look:

- [ ] **Materials / textures** — no **magenta** missing-texture walls; hero textures, `_norm`/`_gloss`
  variants and DDS-compressed textures all resolve (the first windowed capture caught stormkeep's DDS walls
  rendering magenta while the headless smoke still said `0 errors` — see Tricks → *Visual capture*).
- [ ] **Lightmaps / deluxemaps** — baked lighting reads as directional, not flat/fullbright; deluxemapped maps
  (the `IsDeluxemapped` ones) show light *direction* modulation on walls, not a uniform wash.
- [ ] **Patches (bezier curves)** — curved surfaces (arches, pipes, domes) render **smooth**, not faceted /
  collapsed; no gaps at patch seams.
- [ ] **Billboards / flares** — `Q3FACETYPE_FLARE` light flares appear as textured quads facing the camera, not
  invisible and not opaque black squares.
- [ ] **Model bone pose** — the player model stands **un-twisted** (no bones collapsed to the origin or folded
  inside-out), feet on the floor, the idle/bind pose matching Base. A twisted model points at a skeleton or
  bind-pose decode bug the headless parent-chain/non-singular assertions did *not* catch on-screen.

If something looks wrong, the headless `VisualQaTests` won't have flagged it — file it against the relevant
loader/builder (`MapLoader`/`BspReader`, `BezierPatch`, `IqmBuilder`/`Md3Builder`/`DpmBuilder`,
`LightmapShader`, the material pipeline).

---

## Menu / front-end

`Main.cs` now boots the **`Shell`** (the app coordinator) which shows the **main menu** front-end and owns the
menu↔match lifecycle. The menu is a faithful C#/Godot port of Xonotic's QuakeC menu (`Base/.../qcsrc/menu/`):
a Nexposee of Singleplayer / Multiplayer / Media / Settings / Credits / Quit plus ~50 supporting dialogs
(full 7-tab Settings tree, Multiplayer profile/mutators/server-info, Media, the 22-panel HUD editor, first-run/
ToS/welcome/team-select, tools, confirms). Architecture:

- **Shared cvar store.** Every menu widget binds an engine cvar via the toolkit in `game/menu/framework/`
  (`Widgets.CheckBox/Slider/TextSlider/RadioButton/InputBox/CommandButton`, `Dependent.Bind`/`BindNot` =
  QC `setDependent`). `MenuState` (boot) mounts the VFS once, loads `xonotic-client.cfg`+`xonotic-server.cfg`
  into one process-wide `CvarService`, layers `user://config.cfg` on top, and hands that store + VFS to each
  match (so a setting changed in the menu is live in-game and persists). Apply/restart buttons route through
  `MenuCommand`.
- **Dialogs** live in `game/menu/dialogs/` (one C# file per QC `dialog_*.qc`); `DialogSettingsAudio.cs` is the
  reference pattern. Settings persist to `user://config.cfg` on Back/Apply.
- **In-game:** Escape opens the pause menu (`Shell` pauses the tree; Disconnect returns to the main menu).

**Boot / capture flags** (on the windowed run):
- *(default)* → main menu.
- `--map <vpath>` → boot straight into a match on that map (the smoke test; bypasses the menu).
  `--gametype <short>` selects the boot gametype (dm/ctf/…).
- `--menu-screen <id>` → open one dialog for a screenshot. ids: `settings` (or `settings:Audio` to pick a tab),
  `media` (or `media:Demos`), `multiplayer`, `singleplayer`, `create`, `credits`, `pause`, `profile`,
  `mutators`, `serverinfo`, `teamselect`, `firstrun`, `tos`, `welcome`, `hudpanels`, `hudweapons`, `cvarlist`,
  `sandbox`. e.g.:
  ```bash
  "$GODOT" --path . --menu-screen "settings:Audio" --screenshot "$PWD/screenshots/audio.png"
  ```
  Then `Read` the PNG to inspect the dialog. (Headless renders blank — run windowed.)

---

## Configuration knobs

- **`Main.cs`** constructs `GameDemo` with `MapPath` + `SampleModelPath` (currently `maps/stormkeep.bsp` +
  `models/player/erebus.iqm`). Change these to load a different map/model — any of the 31 official maps in
  `xonotic-20230620-maps.pk3` (e.g. `maps/solarium.bsp`, `maps/afterslime.bsp`) now resolves.
- **`GameDemo` `[Export]`s**: `DataPath` (the content mount; default `res://assets/data`, resolved
  project-relative — a `res://`/`user://` or absolute OS path also works), `MapPath` (VFS vpath; empty → flat
  test floor), `SampleModelPath`. Exposed in the inspector once `GameDemo` is a scene node (today it's created in code).
- **`nuget.config`** adds the editor's bundled package source — needed for `dotnet` to restore `Godot.NET.Sdk
  4.6.3`. If you upgrade Godot, bump the SDK version in `XonoticGodot.csproj` **and** the path/version here.

---

## Gotchas

- **SDK version must match the editor.** `XonoticGodot.csproj`'s `Sdk="Godot.NET.Sdk/4.6.3"` must equal the installed
  Godot version, or GodotSharp API mismatches at load. Bump both on a Godot upgrade.
- **Stale `obj/` + `.godot/` after an SDK change** → duplicate `AssemblyInfo`/`TargetFramework` errors. Fix:
  `rm -rf obj bin .godot/mono` then rebuild.
- **The host project globs `**/*.cs`** from its root; `XonoticGodot.csproj` `<Compile Remove>`s `src/`, `tests/`,
  `planning/`, `.godot/`, `obj/`, `bin/` so it doesn't double-compile the libraries. Don't drop those removes.
- **`dotnet build XonoticGodot.csproj` outputs to `.godot/mono/temp/bin`** (the Godot SDK redirects it), not `bin/` —
  that's where the engine looks for the assembly.
- **Maps:** the **31 official compiled maps** ship in `assets/data/xonotic-20230620-maps.pk3`
  (downloaded from the `xonotic-0.8.6.zip` release; `xonotic-20230620-nexcompat.pk3` adds the Nexuiz-compat set).
  `maps/_init/_init.bsp` is still present (inside the maps pk3) as the lightweight placeholder. To add more,
  drop another `*.pk3` into `assets/data/` — `MountGameDir` picks it up automatically.
- **Sounds** load from the mounted content (`sound/*.ogg|wav`) via `AssetLoader.LoadSound` (wired into
  `ClientWorld.AudioLoader`); the old `res://sound/<sample>.ogg` convention remains as a fallback. The same
  loader feeds **announcer voices** (`HudNotifications.AudioLoader` → `sound/announcer/<voice>/<snd>.ogg`).
- **HUD art** (weapon icons, numbered crosshairs, kill-notify icons) resolves from the mounted content via
  `TextureCache.VfsResolver` → `AssetLoader.LoadTexture` (skin-aware `gfx/hud/<skin>/…`), with `res://art/hud`
  + colored-box/vector fallbacks. So both the visuals and audio of the HUD now come from the mounted content.

---

## Tricks & techniques (grow this)

- **Headless smoke test in an agent/CI:** use the one-liner above; assert `hard errors: 0`. This is the cheapest
  "did my change break runtime startup / asset loading" check without a GPU.
- **Frame budget:** `--quit-after <frames>` (frames, not seconds). ~200 frames is plenty to hit `_Ready` + a few
  `_Process` ticks. Bump it to exercise more of the per-frame sim.
- **Pick what to load:** point `Main.cs`/`GameDemo` at any VFS vpath — e.g. a `*.iqm`/`*.dpm`/`*.md3` to stress a
  model builder, or a map to stress BSP. Watch the log for `[GameDemo]`/`[AssetSystem]` prints + warnings.
- **Config load signal:** the boot log line `[GameDemo] config: <N> cvars from <M> cfg files (… aliases, <K> missing)`
  reports the `ConfigInterpreter` run (`ConfigLoader.LoadServerConfig` execs `xonotic-server.cfg`'s whole chain into
  the live cvar store). Healthy = `~4593 cvars, 16 files, 0 missing`. A non-zero `missing` count or a much smaller
  cvar total means the VFS didn't mount the data dir (check `DataPath`). The gameplay layer's ~461 live `GetFloat`
  reads (movement physics, regen, gametype limits, mutator/monster balance) depend on this — a low number = stale
  defaults. Unit-test the parser headlessly via `dotnet test` (`ConfigTests.cs`, incl. 4 real-data assertions).
- **Managed exceptions** surface as a `WARNING:`/`ERROR:` banner followed by a `at XonoticGodot.…` C# stack trace in
  the console-exe stdout — grep `at XonoticGodot\.` to find the failing method:line.
- **Visual capture (verified 2026-06):** an agent/CI can capture a real frame and *look at it*. `Main` accepts
  `--screenshot <path> [--screenshot-frames N]` (see `game/ScreenshotHook.cs`): it lets the scene settle N idle
  frames (default 90), waits for `RenderingServer.FramePostDraw`, writes the root viewport to a PNG, and quits.
  **Run WINDOWED — `--headless` uses the dummy renderer and the PNG comes out blank.** Then `Read` the PNG (the
  Read tool renders images, so the agent literally sees the frame).
  ```bash
  GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
  "$GODOT" --path "C:/Users/Bryan/Projects/Xonotic/XonoticGodot" \
           --resolution 1280x720 --screenshot "$PWD/screenshots/stormkeep.png"
  # success → stdout has: [Screenshot] wrote 1280x720 -> .../screenshots/stormkeep.png
  ```
  The window opens for ~1.5 s and self-quits. Point `Main.cs`/`GameDemo` at a different map/model to capture
  other scenes; bump `--screenshot-frames` if assets/shadows need longer to settle. Write captures into
  `screenshots/` (or `_scratch/` for general throwaway test files) — **not** the project root: both dirs carry a
  `.gdignore` so the Godot editor skips them and never spams the tree with `*.import` sidecars, and both are
  git-ignored. A root-level capture (`_*.png`) is git-ignored too but Godot will still generate a stray
  `_*.png.import` next to it, so prefer the folders.
  (First proof of this caught stormkeep's **walls rendering as missing-texture magenta** — unsupported DDS
  textures — while the headless smoke test still reported `0 errors`; now fixed by `DdsDecoder` (S3TC/BC1-3 +
  uncompressed). The last couple of `_norm`/`_gloss` maps were pk3 **symlink** stubs from build-time dedup,
  now followed by the VFS (`Pk3Mount`). Visual capture sees what the log can't.) Godot's Movie Maker
  `--write-movie <file>` still works for rendering animation *sequences* to frames (also needs a non-headless context).
- **Perf benches (T33):** three measurement-first benches live in `tests/XonoticGodot.Tests/Perf/`
  (`NetSnapshotPerfBench` — snapshot delta encode/decode; `TracePerfBench` — TraceService sweeps + map-load
  time on real atelier collision; `ServerTickPerfBench` — a booted `GameWorld`'s ms/tick + B/tick with 0 and
  4 players), plus the older `BotPerfBench` (bot nav). Run them with
  `dotnet test tests/XonoticGodot.Tests --filter PerfBench -l "console;verbosity=detailed"` — each prints a
  ms + B/op table; measured baselines are recorded as comments atop each file (update them when numbers move
  materially). They skip without assets; point `XG_DATA_DIR` at a content dir to override the default path.
- **Live-process GC profiling:** the headless benches can't reach client-side per-frame paths
  (`EffectSystem._Process`, HUD rebuilds). Attach `dotnet-counters` to the running game instead:
  `dotnet tool install -g dotnet-counters`, launch the game windowed, then
  `dotnet-counters monitor --process-id <godot PID> --counters System.Runtime` and watch
  *Allocation Rate* / *% Time in GC* / gen0 counts while playing. **Do not** flip GC modes
  (`ServerGarbageCollection` etc.) in `XonoticGodot.csproj` without counter evidence — client frame-pauses
  trade against dedicated throughput.
- **Hitch forensics (FrameProfiler, 2026-06-12):** `cl_frameprofiler 1` = overlay graph + hitch log,
  `2` = also a per-second breakdown + the file sink `user://frameprofile.log`. Every frame is recorded into a
  240-frame forensic ring (full per-scope ms+alloc table, GC counts + **pause ms**, draw calls, **pipeline-compile
  deltas** — a nonzero `pipe +N` mid-play is a first-use shader compile slipping past the warm pass), and a hitch
  emits a multi-line dump: `[hitch] scopes:` (top 12 with per-scope KB), `gpu:`, `gc:`, `events:`, and `prev:`
  (the 8-frame run-up). **Events** are one-shot forensic markers any layer can raise via
  `Prof.Event("...")` — wired today: streamer main-thread builds (with ms), GPU warm-pass completion, sim
  backlog drops, server input-queue trims, faithful-particle capacity changes. Read a hitch as: events name the
  culprit; `scopes` attribute the C# time; `pipe/gpu` attribute the GPU; `gc pause` the GC.
  `set cl_frameprofiler_dump 1` (console, mid-play, after a stutter) writes the whole ring to
  `user://frameprofile_ring.csv` (Excel/pandas-ready) and re-arms. Add new `Prof.Event` call sites freely —
  they're free when the profiler is off, bounded when on, thread-safe everywhere.
- **Dedicated/headless server:** v1 is the headless listen server (`--headless --host`, see the section
  above + `tools/run-dedicated.sh`); the `linux-dedicated` export preset uses Godot's "export as dedicated
  server" mode (`OS.HasFeature("dedicated_server")` is the feature-tag branch point). The
  `XonoticGodot.Server` lib is Godot-free so a plain console host remains possible later.
