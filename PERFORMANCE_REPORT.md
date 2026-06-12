# XonoticGodot Performance Report — Hitching, Stutter, and Low-FPS Causes

**Date:** 2026-06-10 · **Engine:** Godot 4.6.3 (Forward+) · **Runtime:** .NET 8 (default GC/JIT config)
**Scope:** every mechanism found that can cause frame hitches, micro-stutter, or sustained low FPS, with concrete optimization plans, including parallelization strategies.
**Method:** four parallel code audits (GC/alloc, Godot rendering, loading/threading, sim-loop/pacing) over `game/` + `src/` (~46.5k LoC), cross-checked against the live `FrameProfiler` measurements taken earlier today. Every file:line below was verified by reading the code, not just pattern-matched.

---

## 0. Executive summary

There are **four distinct problem classes**, and they need different fixes:

| # | Class | Felt as | Status | Biggest lever |
|---|-------|---------|--------|---------------|
| 1 | **First-use stalls** — lazy asset load + runtime-created materials compiling shaders/pipelines on first draw | one big hitch on first shot / first pickup / first effect of a kind | partially mitigated | cache runtime materials + GPU warm pass at map load; precache **all** assets |
| 2 | **Frame pacing / vsync beat** | continuous judder or "drops to 60 from 160" while CPU+GPU are mostly idle | **root-caused empirically** (`rest` dominates hitch frames) | Mailbox vsync option, fps cap guidance, keep per-frame cost ≪ refresh budget |
| 3 | **Steady per-frame CPU & GC churn** | lowered fps ceiling, occasional Gen0 spikes in firefights | several fixes landed; effect-spawn churn remains | pool/cache `GpuParticles3D` + materials; HUD string/redraw caching |
| 4 | **Sim-loop scaling** | fps degrades with players/projectiles/entity count | not yet hit in 1-player tests; will bite in real matches | DP-style areagrid (spatial partition) for traces/triggers/radius queries |

**Key empirical facts already established** (via the in-tree `FrameProfiler`, `cl_frameprofiler 1/2`):
- In the investigated runs, hitch frames showed `proc ≈ 3 ms`, `rcpu+gpu ≈ 3 ms`, **`rest` 8–17 ms** → the stall was **vsync/present wait**, not game code, not GC.
- Measured allocation rate is modest (**40–87 KB/frame, near-zero collections**) → GC is currently *not* the dominant hitch source. The goal of the GC section is to keep it that way under combat load.
- `server.tick` spikes were a **symptom**: the 72 Hz accumulator runs catch-up ticks *after* a long frame (`ticks ≥ 2` in the hitch log means the cause is elsewhere).

**Already landed** (don't re-do): FrameProfiler + `Prof` scopes (`proc/rcpu/gpu/rest`, hitch logger, env banner); MD3 morph skip-guard for static frames; `CsqcModelEffects` light-list scratch reuse; gib/casing fallback mesh+material caching; projectile **trail** resource cache + `WarmupTrails()`; snapshot broadcast gated on `ticksRan > 0`; sub-tic eye extrapolation; stair-smoothing feedforward.

---

## 1. Class 1 — One-time hitches (first-use stalls)

These are the "game freezes for 30–300 ms the first time X happens" events. Two mechanisms stack: **(a)** synchronous disk-read + parse + GPU upload on the main thread, and **(b)** Godot compiling the shader/pipeline for a never-drawn material on first draw.

> Godot 4.6 note: the 4.4+ ubershader/pipeline-precompile system only helps materials that exist when their mesh is **instantiated/loaded**. Materials constructed at runtime (`new StandardMaterial3D`, `new ParticleProcessMaterial`) still pay shader + pipeline compile when they first enter the tree/draw. The cure is the same as for class 3: **stop constructing them at runtime — build once, cache, pre-instantiate during load.**

### 1.1 Runtime-constructed materials & particle resources (VERIFIED, HIGH)

Every call below builds fresh Godot resources **per spawn** — first use compiles a pipeline; every use churns native handles (see §3.2):

| Site | What's created per spawn |
|------|--------------------------|
| `game/client/EffectSystem.cs:533-562` (`BuildBurst`) | `GpuParticles3D` + `ParticleProcessMaterial` + draw-pass mesh |
| `game/client/EffectSystem.cs:584-615` (`BuildTrail`) | same |
| `game/client/EffectSystem.cs:634-660` (`BuildParticleMesh`) | `QuadMesh` + `StandardMaterial3D` |
| `game/client/EffectSystem.cs:903/918` (effectinfo burst) | `GpuParticles3D` + `ParticleProcessMaterial` |
| `game/client/EffectSystem.cs:1138/1147` (info mesh) | `QuadMesh` + `StandardMaterial3D` |
| `game/client/ProjectileRenderer.cs:339` (body) | `StandardMaterial3D` per projectile spawn (trail path is cached ✅) |
| `game/client/BeamRenderer.cs:195`, `LaserRenderer.cs:112-122` | material/quad on first beam/laser |
| `game/client/WeatherSystem.cs:107/134/206/213` | per-map weather emitters (load-time — OK, but include in warm pass) |
| `game/client/Decals.cs:171-192` | `ImageTexture` baked per unique decal color (has `_texCache` ✅ — first hit per color still stalls) |

**Plan A1 — resource caching (also fixes §3.2):**
- Key burst resources by `(EffectClass, tint-bucket)` — quantize tint (e.g. 4 bits/channel) exactly like `Decals.SolidTexture` does. All other parameters are pure functions of `EffectClass`, so the `ParticleProcessMaterial`, draw-pass mesh, and material are **shareable across emitters** (same reasoning that made `_trailResCache` safe).
- Same for projectile bodies: key by `BodyFamily` + color; share `CapsuleMesh`/`SphereMesh` instances.
- Pre-build the whole cache in `EffectSystem.Warmup()` / `ProjectileRenderer.WarmupTrails()` (both already run at map load, in the right order).

**Plan A2 — GPU warm pass at map load (the deferred item; do it offscreen to avoid the visible-flash problem):**
1. Create a 64×64 `SubViewport` (`UpdateMode.Always`, own `Camera3D`) during the loading screen.
2. Parent one hidden-cost instance of **every cached runtime material family** into it: each effect-class burst (1-particle, `Explosiveness=1`), each projectile body+trail, beam, laser, gib, casing, decal quad, viewmodel placeholder.
3. `await RenderingServer.FramePostDraw` twice (one frame to compile, one to flush), then free the viewport.
4. Result: first rocket/explosion/gib in real play hits a warm pipeline. ~50 lines; no visible flash because nothing touches the main viewport.
- Cheap verification: `cl_frameprofiler 1`, fire each weapon once — the `[hitch] … gpu` spikes on first-fire disappear.

### 1.2 Synchronous cold asset loads mid-game (VERIFIED, HIGH)

All asset loads are synchronous on the main thread; anything not precached stalls play on first use:

| Asset | Trigger | Cost | Path |
|-------|---------|------|------|
| Weapon model (v_/h_) | switch/pickup of a weapon not in `ComputeExpectedWeapons()` | 30–300 ms | `ViewEntityRenderer.Update → AssetSystem.LoadModel` (VFS read → IQM/DPM/MD3 parse → textures → ArrayMesh → materials) |
| Player model | first sight of a new player/model | 20–150 ms | `ClientWorld.BuildRenderNode → LoadSkeletalModel` (`ClientWorld.cs:~1298`) |
| Sound sample | first play of a sample | 5–50 ms (OGG decode) | `ClientWorld.OnSound → AssetLoader.LoadSound` (`AssetLoader.cs:483-510`) |
| Texture | first material referencing it | 20–100 ms | `AssetSystem.LoadTexture` (decode + `ImageTexture.CreateFromImage`) |
| effectinfo/particlefont | warmed at map load ✅ (`EffectSystem.Warmup`) | — | — |

Existing mitigation: `NetGame.PrecacheWeaponModelsAsync` (~`NetGame.cs:1081-1150`) warms only the *expected* weapon set; everything else can arrive cold.

**Plan A3 — precache everything, then stream the stragglers:**
1. **Warm all ~25 weapons** at load, not just expected (remove the `expected.Contains()` filter; ~50–100 ms more load time, hidden by the loading screen). Trivial, zero risk, big felt win.
2. **Precache combat sounds** (per-weapon fire/impact lists are already in the weapon defs + `SoundsList`) during a loading stage.
3. **Precache player models** for connected clients at join time (and default models at load).
4. **Background streaming for the rest** — see §5 strategy S1: `Task.Run` the VFS read + parse (pure C#), then build Godot objects on the main thread via `CallDeferred`, budgeted.

### 1.3 VFS resolve overhead (VERIFIED, MEDIUM)

`VirtualFileSystem.Exists` linearly probes every mount, and image/sound resolution probes **4 extensions × mounts** per name with **no negative-result cache** (`src/XonoticGodot.Formats/Vfs/VirtualFileSystem.cs:162-171`). Misses repeat the full scan every time (and many lookups are misses by design — e.g. `_norm/_gloss` companion probes).

**Plan A4:** add a `HashSet<string>` negative cache (cleared on mount changes) + a resolved-path cache keyed by base name. ~20 lines; compounds with every loader.

### 1.4 JIT warmup (LOW, release builds)

`XonoticGodot.csproj` / `Directory.Build.props` set no JIT/GC knobs → tiered JIT compiles novel call paths at tier-0 on first use mid-game (small but real hitches).
**Plan A5:** for exported release builds, publish with **ReadyToRun** (`<PublishReadyToRun>true</PublishReadyToRun>` per-RID) so game-code methods are pre-compiled. Don't disable tiered compilation globally (hurts startup + steady-state code quality).

---

## 2. Class 2 — Frame pacing (the empirically-confirmed one)

This is the judder you measured: **CPU and GPU mostly idle, but frames take 2–3× the refresh interval.**

### 2.1 Vsync beat (hypothesised from early runs — SUPERSEDED for the bot-load case; see §9)

With `vsync=Enabled`, `maxfps=0` on a ~160 Hz display, total frame work (~6 ms) sits right at the vblank budget (~6.25 ms). Any jitter (IDE overhead, a Gen0 GC, an unwarm pipeline) pushes the frame past the vblank → it waits for the *next* one → frame doubles/triples (the observed 12.5/16.7/20.4 ms frames with `rest` dominating).

> **⚠ Wave-0 correction (2026-06-11, §9).** Measured empirically with bots: `rest` is **NOT** the vsync beat here. Turning vsync fully OFF (`vid_vsync 0`, confirmed applied) does **not** remove `rest`, and in an exported **release** build the steady-state `rest` is just normal GPU + present (~3.5 ms). The large `rest` in the original (Debug/editor, windowed) runs was Debug-assembly `proc` spillover + windowed-DWM pacing, not a fixable vsync beat. **Mailbox/B1 is therefore not the lever** for the felt bot-load hitching — keep it as a user option, but the real causes are the Debug build (§9) and a GC stall (§9), not pacing.

**Plan B1:**
1. **Expose and try `DisplayServer.VSyncMode.Mailbox`** (`vid_vsync 2`?) — on Vulkan this renders uncapped and presents the latest complete frame at vblank: no tearing, no beat-doubling. This is the single best pacing fix for high-refresh displays. Wire it in `ClientSettings.ApplyVideoSettings` (`game/menu/framework/ClientSettings.cs:134-142`).
2. **`cl_maxfps` guidance**: when vsync is off, cap to a rate the machine holds *consistently* (or to the display rate). A cap slightly *under* the worst-case sustainable rate is smoother than uncapped. **Second reason to cap (per godot#105750, see §3.4):** per-frame allocation cost scales with framerate — uncapped at 200–600 fps multiplies any per-frame Godot-interop or managed alloc into a much higher *bytes/second* GC rate. Our measured 40–87 KB/*frame* is benign at 100 fps but becomes a Gen0 treadmill uncapped. A cap protects pacing *and* GC headroom at once.
3. **Test in an exported release build** — editor/IDE runs always load the Debug C# assembly (verified: Rider's Release switch does NOT change the launched game) and add scheduler noise. `run-release.sh` exists; export templates must be installed once via the editor.
4. Everything in classes 1/3/4 that lowers per-frame cost adds headroom that prevents budget overruns in the first place.

### 2.2 Input quantization + local view stepping (PARTIALLY FIXED)

- Legacy input mode emits one command per 1/72 s regardless of display rate (`NetGame.cs:1516-1537`) → up to ~14 ms input latency and stepwise prediction. A **per-frame input mode already exists** (`cl_movement_perframe`, `NetGame.cs:1502-1515`, DP-style variable-dt commands).
- The 72 Hz view stepping is already smoothed: **sub-tic eye extrapolation is implemented** (`NetGame.cs:2312-2323`, `predicted += viewVelocity * _inputAccum`) plus stair smoothing (`NetGame.cs:2325-2336`).

**Plan B2:** feel-test `cl_movement_perframe 1` and consider making it the default; it removes both the input quantization and the residual tick-stepping (each rendered frame gets its own predicted move). Keep the legacy path for parity testing.

### 2.3 Catch-up amplification (VERIFIED, MEDIUM)

`SimulationLoop.Advance` (`src/XonoticGodot.Engine/Simulation/SimulationLoop.cs:114-137`) runs up to **`MaxTicksPerAdvance = 16`** catch-up ticks in one render frame after a stall — i.e. a hitch is *followed* by a frame doing 16× sim work (then drops backlog). With sim ~1 ms/tick today that's ~16 ms — itself a missed vblank.

**Plan B3:** lower the per-frame catch-up for the interactive client-hosted path — e.g. clamp to `min(4, backlog)` ticks per render frame and let the remainder drain over the next frames (brief, invisible slow-motion instead of a second hitch). Keep 16 for headless dedicated servers. One constant + one parameter; verify with the `ticks N` marker in hitch logs.

### 2.4 Remote-entity snapshot pacing (LOW)

`SnapshotInterpolation` lerps prev→cur with snap-on-stale (`src/XonoticGodot.Net/SnapshotInterpolation.cs`). Fine on LAN; on lossy/high-ping connections a late snapshot can pop. **Optional:** one-snapshot de-jitter buffer (adds one snapshot interval of latency) behind a cvar. Not a local-play issue — lowest priority.

---

## 3. Class 3 — Steady per-frame CPU & GC churn

### 3.1 Current GC posture

.NET 8 defaults: workstation concurrent GC, tiered JIT — reasonable. Measured 40–87 KB/frame with almost no collections. **The discipline in the net/sim core is good** (struct snapshots, reused `BitWriter`s, scratch dicts, for-loops). Do **not** chase `new Vector3/Basis/Transform3D/NetEntityState/...` — they are structs, zero GC (a naive allocation scan over-flags them).

**Plan C1 — runtime settings (cheap insurance):**
- At boot: `GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` (defers blocking Gen2 collections; right mode for a game).
- Add explicit `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>` + `<ServerGarbageCollection>false</ServerGarbageCollection>` to the csproj so the intent is pinned, not default-dependent.

### 3.2 Real churn sites (VERIFIED, ordered by impact)

1. **Effect spawns** — every burst/trail allocates 3–5 Godot objects (§1.1 table). Godot C# objects are not just managed allocs: each is a **native handle + finalizer-queue pressure**, and `QueueFree` churn (`EffectSystem.ScheduleFree → QueueFree`, `EffectSystem.cs:1260-1275`) adds tree add/remove cost. Dominant churn in firefights.
   **Fix:** Plan A1 caching removes the *resource* churn (the expensive part). Then optionally pool the `GpuParticles3D` **nodes** per class (reset via `Restart()`/`Emitting=true`, hide on finish instead of free). Pooling is Tier-2: do caching first, measure, pool if node churn still shows.
2. **`CsqcModelEffects` mesh walks** — `ResetRenderFlags` runs 4 recursive `Meshes()` traversals per *effect-bearing* entity per frame; each node visited calls `GetChildren()` (allocates a `Godot.Collections.Array` + marshals) plus nested iterator allocs (`CsqcModelEffects.cs:181-216, 273-280`). Correctly **gated** by `effectsActive` (`ClientWorld.cs:937-944`), so it only costs when EF_* bits are live (powerup glows, flame, ghosts) — but those are exactly the busy moments.
   **Fix:** cache the `List<MeshInstance3D>` on `CsqcModelEffects.State` when the model node is (re)built; invalidate on model swap. Removes all per-frame `GetChildren()` marshaling.
3. **HUD redraw + string churn** — `HudManager.cs:168-169` calls `QueueRedraw()` every frame for every `IsDynamic` panel (default **true**, `HudPanel.cs:89`); each redraw re-records the canvas item and re-formats strings: `FpsPanel.cs:109-112`, `PingPanel.cs:68`, `TimerPanel.cs:138`, `RaceTimerPanel.cs:84-121`. (Scoreboard is correctly change-gated, `IsDynamic => false`.)
   **Fix:** cache the formatted string + redraw only when the *displayed* value changes (fps text changes ~1×/s; timer 1×/s; ping on update). Make `IsDynamic` panels store a small "last drawn state" struct. Cuts both allocs and canvas re-record cost.
4. **`ServerNet.BuildEntitySet` projectile key** — `$"{e.ClassName} {e.NetName}"` per model-less projectile per tick-frame (`game/net/ServerNet.cs:1218-1220`).
   **Fix:** compute once at projectile spawn and stash on the entity (or memoize per `(ClassName, NetName)` in a small dict).
5. **`BuildScoreboard`** rebuilds `_scoreRows` + per-player columns every tick-frame (known). Gating on a score-version risks missing name/team edits — if touched, version-stamp *all* inputs. Modest cost; fine to defer.
6. **`FindInRadius`** allocates an iterator per call and linearly scans all entities (`src/XonoticGodot.Engine/Simulation/EngineServices.cs:186-202`); called per splash-damage event. Folds into the §4 areagrid work (add a list-filling overload to also kill the enumerator alloc).

### 3.3 Per-frame interop cost (not GC, still CPU)

- **Skeletal pose push:** 3 interop calls per bone (`SetBonePosePosition/Rotation/Scale`) × ~50–60 bones × players × frame (`game/client/PlayerModel.cs:107-123`). No bulk API in Godot 4.6.
  **Mitigations:** skip `SetBonePoseScale` when scale is unit (saves 1/3); skip pose push entirely when the model is off-screen (`VisibleOnScreenNotifier3D`) or refresh distant players at half rate. Engine-side, a batched pose API would be the real fix.
- **MD3 morph re-upload:** animated morphs still `ClearSurfaces + AddSurfaceFromArrays` (full vertex+normal upload) per surface per frame (`game/client/ModelAnimator.cs:378-427`); the landed skip-guard removes the static-model cost.
  **Optional big win (Tier-3):** GPU morphing — bake MD3 frames into a float texture (or two mesh streams + a custom shader lerping by uniform), so per-frame work becomes "set 2 uniforms". Eliminates the re-upload entirely; medium effort, isolated to ModelAnimator.

---

### 3.4 Godot interop allocations — the hidden GC class (godot#105750)

[godotengine/godot#105750](https://github.com/godotengine/godot/issues/105750) documents a class of GC stutter a `new List<>`/`$"..."` grep **cannot** find: **Godot's C# marshaling allocates on the managed heap for many ordinary API calls**, independent of your own `new`s. The reported offenders (Godot 4.3–4.4, Forward+/Vulkan):
- `Input.IsActionPressed("jump")` / any string-keyed input API → mints a `StringName` **every call**.
- `GetTree().GetNodesInGroup(name)` / `CallGroup` → returns a heap `Godot.Collections.Array`.
- `GetNode("path")` / string `NodePath` args → `NodePath` alloc per call.
- Any `Set*ShaderParameter("name", …)` / `GlobalShaderParameterSet("name", …)` → implicit `string`→`StringName` alloc per call (the API parameter is `StringName`, so even a `const string` converts).
- Godot `PhysicsServer` raycasts marshal per call.
- The reporter's key insight: **the cost scales with framerate** — a single alloc/frame is nothing at 60 fps and a GC treadmill at 600+ fps (hence the §2.1 fps-cap cross-link). The brute-force workaround in the thread (`GC.Collect()` every frame) is a smell; the principled version is `GCLatencyMode.SustainedLowLatency` (Plan C1) + just not allocating.

**I audited this codebase against every offender above — it is, somewhat remarkably, already clean in the hot paths, by construction rather than by StringName-caching:**
- **No string-based input.** Gameplay input reads the port's own `BindTable`, not Godot's string `InputMap`. The only hot `Godot.Input` call is `HudManager.cs:233` `Input.IsKeyPressed(Key.Up)` — takes a **`Key` enum, no `StringName`**.
- **No `GetNode(string)` / `GetNodesInGroup` / `CallGroup` anywhere in `game/`** (verified — zero matches). Entities are held in typed dictionaries, not the scene-group system.
- **Animated world textures are GPU-driven.** `ShaderCompiler` bakes `TIME` into the generated GLSL (`ShaderCompiler.cs:477/488/515`) for scroll/rotate/turb, so animated stages cost **zero per-frame interop** — the GPU's built-in `TIME` drives them.
- **All `Set*ShaderParameter` calls are build-time or change-gated**, not per-frame (verified: the only `_Process`-bearing file with shader-param calls, `VignetteOverlay.cs:97-101`, gates `Apply` behind a `Sig.Equals(_last)` check; `WorldTint.cs:227-237` guards each `GlobalShaderParameterSet` on value-change; `ModelTint` runs on appearance change, not per frame).
- **Traces don't use Godot physics.** `TraceService` is pure C# (§5), so the per-raycast marshaling alloc the issue cites simply doesn't exist here — an underrated advantage of the custom collision system.

**Residual exposure (LOW — on-event, not per-frame):** the shader-uniform name constants are `const string` (`PlayerSkinShader.ColormodUniform = "colormod"`, `WorldTint.MapTintUniform = "map_tint"`, etc.) passed to `StringName` parameters → one `StringName` alloc per call, but only at material build / appearance-change frequency.

**Plan C2 — harden the convention before someone adds a per-frame call (trivial, zero-risk):**
- Convert the uniform-name constants from `const string` → `static readonly StringName` (in `PlayerSkinShader`, `LightmapShader`, `WorldTint`, `HeroMaterials`, `ModelTint`, `VignetteOverlay`). Eliminates even the on-event allocs and means a future per-frame `SetShaderParameter` is automatically alloc-free.
- Standing rule (worth a comment in the shader-uniform files): **never pass a string literal to a Godot API typed `StringName`/`NodePath` from a per-frame path; cache it as `static readonly StringName`.** Same for avoiding `GetNodesInGroup`/`GetNode(string)`/string input APIs in `_Process`.
- This class is invisible to source greps — the only reliable detector is `dotnet-counters monitor … System.Runtime` alloc-rate while playing (already the §7 recommendation; godot#105750 confirms it's the right tool). Re-measure after any new rendering/HUD feature.

**Plan C3 — enforce the convention so new code can't regress it (compile-time, not a test):**

*Why not a unit test:* the test project (`tests/XonoticGodot.Tests`) references the five `src/` libraries but **not the Godot host project**, so a test literally cannot see `game/` — where 100% of the Godot interop lives — and can't reflection-load the host DLL without the full Godot runtime. A test is a dead end for this rule.

*The right tool is a Roslyn `DiagnosticAnalyzer`*, which runs during the **host's own compile**: it sees `game/` natively, surfaces as a build warning/error **and** an IDE squiggle at the exact call site, and is suppressible per-line when genuinely intended. Home it in the existing `src/XonoticGodot.SourceGen` analyzer assembly (netstandard2.0 + Roslyn) — it's already consumed by the host build, so no new wiring — and unit-test it in-memory the way `tests/.../SourceGenTests.cs` tests the generator (the test project already references `Microsoft.CodeAnalysis.CSharp 4.8.0`). Split the rule into two tiers by false-positive profile:

- **Tier 1 — blanket ban on the string-keyed APIs with zero legitimate uses here** (verified: **zero current matches** in `game/`, so a hard ban has zero false positives and zero churn): `Input.IsActionPressed`/`IsActionJustPressed`/`IsActionJustReleased`/`GetActionStrength`/`GetActionRawStrength` (string input — the port uses its own `BindTable`), `GetNodesInGroup`/`CallGroup`/`AddToGroup` (string group system), `GetNode(string)`/`GetNodeOrNull(string)` (typed dicts instead). Pure regression protection that locks in a property currently true only by habit. **~1 hour incl. unit test.**
- **Tier 2 — flag implicit `string`-literal→`StringName`/`NodePath` conversions (and string-keyed Godot calls) ONLY inside `_Process`/`_PhysicsProcess`/`_Draw` method bodies.** That syntactic scope encodes "not *per-frame*" precisely and has near-zero false positives, because build-time material setup (the 40+ legit `SetShaderParameter("literal", …)` sites) is never in those methods. Cheap to write — no interprocedural reachability, just "is this node inside a method declared `_Process`/…". **Land the C2 `StringName` conversion alongside it** so the rule is satisfiable without friction (a uniform name needed in a hot path is already a cached `StringName`, nothing to suppress). **~half a day incl. tests.**

*Honest limitation:* neither tier catches a bad call buried in a helper *invoked from* `_Process` (static reachability isn't worth it). That gap is covered by the runtime backstop already in §7 (`dotnet-counters` alloc-rate + the FrameProfiler GC segment), which surfaces the alloc regardless of source pattern. Treat the analyzer as **defense-in-depth that fails the build on the obvious mistakes**, not a proof of zero per-frame allocation.

*Low-tech alternative (if not authoring an analyzer):* an MSBuild pre-build `<Target>` that greps `game/**/*.cs` and fails on the Tier-1 API names — ~15 minutes, but worse UX (no IDE squiggle, no per-line suppression). Adequate for Tier 1; can't reasonably express Tier 2's scoping.

---

## 4. Class 4 — Simulation scaling (will bite with players/bots/projectile spam)

The sim core is faithful and clean but has **no spatial acceleration structure** — DarkPlaces has the areagrid; the port currently scans linearly:

| Site | Pattern | Cost shape |
|------|---------|-----------|
| `src/XonoticGodot.Engine/Collision/TraceService.cs:160-165` (`ClipToEntities`) | every trace tests **every** solid entity (`SolidEntities` cached list ✅, but O(n) per trace) | players × (5–8 traces/tick) × solids + projectiles × solids |
| `src/XonoticGodot.Engine/Simulation/TriggerTouch.cs:37-50` | after each client move, scan **all** entities for trigger overlap | clients × entities / tick |
| `EngineServices.FindInRadius:186-202` | linear scan per damage event | events × entities |
| `SimulationLoop.Tick:178-190` (`sim.integrate`) | every non-client entity integrates each tick; movers trace (O(solids) each) | projectiles × solids |

At today's test scale (1 player, few entities) this measures ~1 ms/tick total — invisible. At 8–16 players + bots + rocket spam it's the main fps-floor risk: order of 10⁴–10⁵ entity tests per tick.

**Plan D1 — port the DP areagrid (the one algorithmic fix that pays everywhere):**
- A uniform XY grid (DP uses 32×32 cells over the world bounds) holding entity links, updated in `LinkEdict` (the single choke-point already exists: `EntityService.LinkEdict`).
- `ClipToEntities`, `TriggerTouch.Run`, and `FindInRadius` query cells overlapping the swept AABB instead of the global list.
- This is *faithful* (DP does exactly this — `SV_AreaGrid` semantics), so no gameplay-parity risk, and it's the precondition that makes 16-player matches viable. Medium effort (~1–2 days incl. tests); fully testable headless (golden-trace suite + a new grid-vs-linear differential test: same results, fewer candidates).

**Plan D2 — bot AI budget (currently moot, future-proof):** bot A*/goal-rating runs unbudgeted inside `sim.start`. Note: per the nav-perf investigation, **`BotBrain.Think` has no caller in the live `--host` loop yet** — so this costs nothing today. When bots get wired: rotate one "strategy token" so only one bot replans per tick (the QC games do exactly this), and reuse the pooled A* (already landed).

**Plan D3 — do NOT time-slice projectile integration.** It was considered and rejected: staggering projectile movement diverges from DP tick semantics (parity risk for a port whose value is faithfulness). The areagrid removes the need.

---

## 5. Parallelization & mitigation strategies

### What is safe to move off the main thread (verified boundaries)

- **Pure C#, no Godot:** all of `src/` — BSP/MD3/IQM/DPM parsers, TGA/DDS decoders, collision/brush building, bezier patch tessellation, movement traces (`TraceService` is pure math — *no* Godot PhysicsServer involved), VFS reads/zip decompression, effectinfo/text parsing. All `Task.Run`/`Parallel.For`-able.
- **Thread-tolerant Godot:** creating/manipulating `Image` instances (decode into an `Image` off-thread is what Godot's own threaded loader does).
- **Main thread (or `CallDeferred`):** scene-tree ops (`AddChild`/`QueueFree`), and conservatively all RenderingServer-backed resource finalization (`ImageTexture.CreateFromImage`, `ArrayMesh.AddSurfaceFromArrays`, material creation). Godot's servers are technically thread-safe via command queues, but keeping GPU-resource creation on the main thread, **frame-budgeted**, is the robust pattern.

### S1 — Background streaming pipeline (kills remaining cold-load hitches)
```
request(name) ──► Task.Run: VFS read + parse (+ Image decode)   [worker]
              ──► CallDeferred: build mesh/material/texture      [main, ≤2ms/frame budget]
              ──► attach node / swap placeholder                 [main]
```
- One shared `AssetStreamer` with a priority queue (viewmodel swap = high, far player model = low) and a per-frame millisecond budget (Stopwatch-guarded drain in `_Process`).
- First-use callers get a cheap placeholder for 1–3 frames instead of a 100 ms stall (weapon viewmodel already has a placeholder path: `ViewEntityRenderer.cs:176`).

### S2 — Parallel map load (shrinks load time; frees budget for more precaching)
Inside `MapLoader.BuildMap` + `NetGame` startup, fan out the pure-C# stages with `Parallel.For`/`Task.WhenAll`:
lightmap `lm_NNNN.jpg` decode (Image per worker), texture decode, bezier patch tessellation, `BspCollisionBuilder` brush builds — then do the Godot mesh/material/texture creation on the main thread in batches between loading-screen pumps (the `await YieldForLoadingFrame()` scaffolding already exists in `NetGame._Ready`). Typical 4–8× on the decode-heavy stages.

### S3 — Idle-time full warmup
After the loading screen drops, keep a low-priority budget (~1–2 ms/frame) warming *everything not yet touched* (remaining weapon skins, announcer lines, gib variants). Within the first minute of play the entire asset set is hot regardless of precache lists.

### S4 — Areagrid (Plan D1) — algorithmic, not threaded, but it's the scaling fix that makes everything else irrelevant at high entity counts.

### S5 — Server sim on a worker thread (listen server) — **Phase 3, design carefully**
Today `NetGame._Process` runs client prediction, rendering feeds, *and* `ServerNet.Tick` (sim + broadcast) on the main thread. Moving the server side (`SimulationLoop` + `GameWorld` + `ServerNet`) to a dedicated thread would make server cost invisible to render. **Blockers found:** the ambient `Api.Services` global is shared by client-side prediction (`TriggerTouch.RunAmbient`, predicted jumppads/teleports) and the server sim — they'd race; the shared↔server cvar bridge and `SoundService.Broadcast` cross the boundary too. Prerequisites: split the ambient facade per-world, make the loopback transport the *only* contact surface, hand snapshots over via a lock-free queue. High effort/high risk — only worth it if profiling shows server tick > ~2 ms in real matches *after* the areagrid. The transport already treats the local client as a normal peer, which is the right foundation.

### S6 — Parallel trace batches inside a tick (experimental, off by default)
Traces are pure C# and read-only over a stable entity set; projectile sweeps could run `Parallel.For` in a gather phase with touch *responses* applied serially afterward in entity order. Faithfulness caveat: DP resolves serially, so ordering effects (two projectiles hitting the same mover) could differ subtly — gate behind a cvar, default off, and only revisit if areagrid + real-match profiling still shows `sim.integrate` dominating.

### S7 — Render-thread experiment
`project.godot` sets no `rendering/driver/threads/thread_model`. Try `thread_model=2` (separate render thread) in a test build: moves RenderingServer submission off the main thread (helps `proc`-bound frames slightly). Historically occasional issues with viewport-heavy code — verify menus/HUD/SubViewport warm pass under it.

### S8 — Pipeline/shader cache hygiene
Godot caches compiled shaders/pipelines on disk per driver. Don't disable it; note that a driver update invalidates it (one cold run). The §1.1 warm pass makes even that first run clean.

---

## 6. Prioritized action plan

| Pri | Item | Section | Effort | Felt impact | Risk |
|-----|------|---------|--------|-------------|------|
| **P0** | Cache effect/projectile runtime materials + meshes | A1/3.2-1 | S–M | High (firefight hitches + churn) | Low (proven pattern: `_trailResCache`) |
| **P0** | Offscreen GPU warm pass at map load | A2 | S | High (first-use hitches) | Low (offscreen; verify no flash) |
| **P0** | Precache all weapons + combat sounds + player models | A3 | S | High | Low |
| **P0** | Mailbox vsync option + maxfps guidance + release-build test | B1 | S | High (the measured judder) | Low |
| **P1** | VFS negative-lookup cache | A4 | S | Med | Low |
| **P1** | HUD string/redraw change-gating | 3.2-3 | S | Med | Low |
| **P1** | `CsqcModelEffects` mesh-list cache | 3.2-2 | S | Med | Low |
| **P1** | `ServerNet` projectile-key string cache | 3.2-4 | S | Low-Med | Low |
| **P1** | GC: SustainedLowLatency + explicit csproj GC config | C1 | S | Insurance | Low |
| **P3** | Uniform names `const string`→`static readonly StringName` (godot#105750 hardening) | C2 | S | Insurance | Low |
| **P2** | Tier-1 analyzer: ban never-used string APIs (`Input.IsAction*` / `GetNodesInGroup` / `CallGroup` / `GetNode(string)`) | C3 | S | Regression guard (0 false pos) | Low |
| **P3** | Tier-2 analyzer: string-literal→`StringName`/`NodePath` inside `_Process`/`_PhysicsProcess`/`_Draw` (+ land C2) | C3 | M | Regression guard | Low |
| **P1** | Catch-up clamp (4 ticks/frame on client-hosted path) | B3 | S | Med (post-hitch recovery) | Low-Med (verify headless) |
| **P2** | **Areagrid** for traces/triggers/radius | D1 | M | High at scale | Med (differential-test it) |
| **P2** | Background asset streamer (Task.Run parse + budgeted build) | S1 | M | High (long tail of cold loads) | Med |
| **P2** | Parallel map load | S2 | M | Med (load time) | Low-Med |
| **P2** | Idle-time full warmup queue | S3 | S | Med | Low |
| **P2** | `cl_movement_perframe` default-on (after feel test) | B2 | S | Med (input feel @144Hz+) | Med (parity feel) |
| **P3** | GpuParticles3D node pooling | 3.2-1 | M | Med | Med (reset semantics) |
| **P3** | GPU vertex-shader MD3 morphing | 3.3 | M–L | Med (many animated models) | Med |
| **P3** | ReadyToRun release publish | A5 | S | Small | Low |
| **P3** | Render thread_model=2 experiment | S7 | S | Small-Med | Med |
| **P3** | Server-sim worker thread | S5 | L | High at scale | High |

**Suggested batches:** ship the four P0s together (they attack the two *felt* problems: first-use hitches and pacing), measure a windowed run, then do P1 as one cleanup pass, then start the areagrid.

---

## 7. Verification protocol (use what's already built)

1. `cl_frameprofiler 1` (graph + hitch log) / `2` (per-second breakdown). Read hitch lines as: big `gpu` → pipeline compile or GPU load; big `rest` → pacing/present (class 2); big `proc` with small scopes → unscoped `_Process` work; `GC gN` segment → allocation spike; `ticks ≥ 2` → the frame is *amortizing a prior stall*, look earlier.
2. `dotnet-counters monitor --process-id <pid> System.Runtime` — alloc-rate + pause-time ground truth while playing.
3. A/B each fix in a windowed run with a fixed scenario (same map, scripted fire of every weapon, `--host`): compare median frame, P99 frame, hitch count from the log.
4. For the areagrid: extend the golden-trace movement suite with a grid-vs-linear differential test (identical trace results on recorded scenarios).
5. Always confirm the final feel in an **exported release build** — editor/IDE runs misrepresent both pacing and C# perf (Debug assembly always loads there; verified).

---

## 8. Implementation status (2026-06-10)

All 23 prioritized items were worked through in priority order. Build green; **1495 tests pass** (incl. new
areagrid differential + analyzer suites); stormkeep windowed screenshots verify rendering parity at each phase.

**Landed + verified:**
- **P0** — A1 effect/projectile material+mesh caches (`EffectSystem._heurMeshCache`/`_infoMeshCache`,
  `ProjectileRenderer._bodyResCache`); A2 offscreen GPU warm pass (`game/client/GpuWarmPass.cs`); A3 precache-all
  (`cl_precache_all_weapons` default 1 → 24 weapons + 36 combat sounds + local/default player models);
  B1 mailbox vsync (`vid_vsync` 0/1/**2**/3 in `ClientSettings.ApplyVideo` + the video dialog slider).
- **P1** — A4 VFS `Exists`/`ResolveImage` pos+neg caches; 3.2-2 `CsqcModelEffects` cached mesh list; 3.2-3 HUD
  `NeedsRedraw()` change-gating (Fps/Ping/Timer/RaceTimer); 3.2-4 `ServerNet._projKeyCache`; C1 `GCLatencyMode`
  + csproj GC pin; B3 `SimulationLoop.MaxTicksPerFrame` soft-cap (interactive=4, drains backlog).
- **P2** — **D1 entity area-grid** (`EntityAreaGrid.cs` → traces/PointContents/FindInRadius/TouchAreaGrid;
  differential test + 1495-test parity); S1 `BackgroundAssetStreamer` + AssetLoader parse/build split (idle
  player-model parse off-thread); S2 parallel bezier tessellation in `MapLoader.BuildMap`; S3 `IdleWarmer`
  (long-tail sound/model warm); C3-T1 `StringApiAnalyzer` (**XG0001**, build error, 9 tests).
- **P3** — C2 uniform names `const string`→`static readonly StringName` (LightmapShader/PlayerSkinShader/WorldTint
  + VignetteOverlay) + standing-rule comment; C3-T2 `HotPathStringNameAnalyzer` (**XG0002**, 6 tests — caught +
  fixed a real per-frame `SetMeta("cmd",…)` alloc in PauseMenu); A5 `PublishReadyToRun` (gated to ExportRelease);
  3.3 skip unit-scale `SetBonePoseScale` interop in `PlayerModel.PushBones`.

**Evaluated → no change warranted (the report's own gating / engine reality):**
- **B2** `cl_movement_perframe` default-on — kept default 0: that's the parity-faithful default (stock DP = fixed
  72 Hz input); flipping changes movement *feel* and needs the windowed feel-test the report gates it on (a user
  step). Feature already cvar-exposed.
- **S7** render `thread_model=2` — **not applicable to Godot 4.6**: the Godot-3-era `rendering/driver/threads/
  thread_model` was removed in Godot 4, which already runs the RenderingServer on a separate thread via its
  command queue. Setting the key is inert (ProjectSettings stores it unread); tested → reverted.

**Deferred (conditional/high-risk per the report — gated on a manual measurement this automated pass can't run):**
- **3.2-1** GpuParticles3D node pooling — §3.2-1 explicitly says "do A1 caching first, **measure**, pool *if node
  churn still shows*". A1 removed the expensive resource churn; the residual *node* pooling carries Med
  reset-semantics risk and its gate is a `dotnet-counters` per-frame alloc-rate read during a firefight (a manual
  windowed step). Pool the heuristic single-node bursts first if that measurement justifies it.
- **3.3 Tier-3** GPU vertex-shader MD3 morphing + off-screen pose-push skip — the §3.3 "optional big win (Tier-3)"
  shader rewrite of `ModelAnimator` (Med risk) and a `VisibleOnScreenNotifier3D`-gated pose skip. The low-risk
  skip-unit-scale win landed; the rewrite awaits a profile showing morph re-upload dominating.
- **S5** server-sim worker thread — §5 S5 itself says "only worth it **if profiling shows server tick > ~2 ms in
  real matches *after* the areagrid**" (now landed), and lists concrete race blockers (the ambient `Api.Services`
  global, the shared↔server cvar bridge, `SoundService.Broadcast`). High effort / High risk with no profiling
  justification yet — revisit only if a real-match server-tick profile demands it.

**Manual steps left to the user (the report's §7 protocol):** the windowed FrameProfiler A/B (scripted weapon
fire, median/P99/hitch deltas) and the `dotnet-counters` alloc-rate read in an **exported release build** — these
are the gates for the three deferred items above and the final feel-confirmation for B1/B2.

---

## 9. Bot-load hitch investigation + Wave 0 ground truth (2026-06-11)

After P0–P3 landed, the user still reported hitching **"especially with bots in the map,"** and observed that
**GameDemo does not hitch the way NetGame does**. This section documents the dedicated investigation, what it
disproved (including §2.1's vsync hypothesis), and what it found to be the real cause.

### 9.1 The harness (reusable — this is the deliverable for iterating on bot-load perf)
- **`tests/XonoticGodot.Tests/Perf/BotTickPerfBench.cs`** — boots a real `GameWorld` on a map, fills *N* bots
  (it sets `bot_join_empty 1` + `bot_number N` and runs frames past the 2.5 s sentinel — calling `Bots.AddBot`
  directly gets **trimmed** on a human-less server), lets them settle, then times `GameWorld.Frame` over many
  ticks. It reports **median / P99 / MAX** tick (a hitch *is* the worst tick, not the mean), **per-scope time and
  per-scope allocation**, and GC gen0/1/2 counts. Env-parameterised: `XG_BOTS` (default 6), `XG_MAP` (stormkeep),
  `XG_TICKS`. Run: `XG_BOTS=12 dotnet test … --filter BotTickPerfBench -l "console;verbosity=detailed"`.
- **`Prof` per-scope allocation tracking** — `ScopeToken` now diffs `GC.GetAllocatedBytesForCurrentThread()`, and
  `SnapshotAndReset(scopes, counters, allocInto = null)` takes an optional 3rd arg to drain the byte buckets. Every
  `Prof.Sample("x")` now reports **both ms and bytes** (FrameProfiler's existing 2-arg call is unaffected, and the
  scopes are free when the profiler is off). New `bot.think` / `bot.path` scopes added in `BotBrain`.
- **Diagnostics tooling added** (all reusable): repeatable **`--cvar NAME VALUE`** boot flag
  (`Shell.ApplyCvarOverrides`, applied before `ClientSettings.ApplyAll`); a FrameProfiler **file sink** at
  `user://frameprofile.log` plus `rest` in the mode-2 line (windowed/exported runs detach stdout — read the file);
  a **`[video]` requested-vs-actual vsync log** in `ApplyVideo`; and a `cl_idle_warmup 0` A/B gate on the idle
  warm. **Bug fixed along the way:** `FrameProfiler.Mode()`/`HitchFloorMs()` read `Api.Cvars` (the listen-server's
  *private* store), but `cl_frameprofiler` is registered/set in `MenuState.Cvars` (the *shared* client store) — so
  `set cl_frameprofiler 2` never took effect mid-`--host` match. Now reads `MenuState.Cvars`.

### 9.2 Diagnosis — why NetGame and not GameDemo
The **server tick** is the cost NetGame pays and GameDemo never does (GameDemo = one local player; no server, no
net, no bots). In the bench, `sim.move` (per-client movement physics) dominates the worst tick and scales linearly
with bot count: 6 bots → MAX ~5.9 ms, 12 bots → MAX ~11 ms — of which only ~0.9 ms is `bot.think` AI and ~0.3 ms
`bot.path` A*; **the rest is movement physics × bots.** (Note: bot AI **is** wired into the live loop now via
`BotController.Tick → BotBrain.Think` / `BotPopulation.ServerFrame` — correcting the older "BotBrain.Think has no
caller" note.) The worst tick still fits the 72 Hz **sim** budget (13.9 ms), but a single ~6 ms server tick landing
in the same frame as rendering 6 players overruns a 144/160 Hz **render-frame** budget (6.25–6.9 ms) → the hitch.

**Allocation fixes landed** (the per-scope alloc readout pinned `Brush.FromBox` — a `Brush` + `Vector3[8]` +
`BrushPlane[6]` ≈ 360 B allocated **per trace** — as the dominant `sim.move` churn):
- `TraceService._boxCache` (`Dictionary<(mins,maxs),Brush>`): the moving trace box is the mover's hull, constant
  across a slide-move's many traces → cache per hull. **−30 %** alloc (92.5 → 64 KB/tick @ 12 bots; gen0 16 → 11).
- `Brush.RefillBox` (in-place, zero-alloc) + a pooled `TraceService._entBrush`: the per-candidate entity-AABB box
  in `ClipToEntities` is refilled into one pooled brush (used immediately, never retained). Helps the clustered
  worst tick most. **Cumulative 92.5 → 61.7 KB/tick (−33 %); gen0 16 → 10.** 1496 tests pass; headless `--host` clean.

### 9.3 Wave 0 — release baseline + vsync A/B (DECISIVE)
Run in an **exported release build** (`run-release.sh` → `dist/windows-client/XonoticGodot.exe`). Three findings,
in order of impact:

1. **Most of the felt hitching is the Debug build, not a bug.** Debug (editor/console) steady median degrades to
   ~11.7 ms (85 fps), `proc` ~6 ms. **Release holds ~7.6 ms (130 fps), `proc` ~3 ms — halved** — with 6 bots. The
   headless *sim* cost is Debug ≈ Release, but the **client** (prediction/render) code roughly halves in Release.
   → **The single biggest win for felt smoothness is to play in a release build.**
2. **`rest` is NOT the vsync beat** (this corrects §2.1). With `vid_vsync 0` (confirmed actually applied via the
   new `[video]` log), `rest` does **not** go away; in Release steady-state `rest` ~3.5 ms is just normal GPU +
   present. The large `rest` in the original Debug/windowed runs was Debug-`proc` spillover + windowed-DWM pacing.
   **Mailbox/B1 is not the lever** — keep it as a user option, but stop attributing the bot hitch to pacing.
3. **The real release hitch is a GC stall: a ~154 MB allocation burst right after self-connect → a 100–154 ms
   all-thread gen2 freeze (a few times early in a match), which PERSISTS in release and is NetGame-only.** Root
   cause: the client builds **all 6 bot player models at once on the first snapshot** (~25 MB per skeletal model —
   IQM + skin/normal/gloss — × 6 ≈ 154 MB) → a full gen2 collection that suspends every thread. It is **not** the
   idle warm (it persists with `cl_idle_warmup 0`). GameDemo builds **one** model, so it never triggers this — which
   is exactly the user's "GameDemo doesn't hitch" observation.

### 9.4 Reprioritized wave plan (supersedes the vsync-centric framing of §2)
- **Wave 1 — ✅ LANDED (2026-06-11, see §10 for the full write-up):** **stagger live player-model builds** +
  **finish the `sim.move`/`sim.integrate` allocation hunt.** The player-model path now returns a placeholder
  shell and streams the build one-per-frame through the S1 `BackgroundAssetStreamer` (kills the 154 MB gen2
  burst); the alloc hunt landed seven fixes for **60.9 → 2.9 KB/tick at 12 bots** (the report's named suspects
  — `IMovementInput` boxing, per-tick concats — were real and are fixed). 1496 tests pass; verified live.
- **Wave 2+ (structural, only if a real-match profile demands it):** **S5 — move the server sim onto a worker
  thread**, so the per-tick movement-physics cost stops stealing the render-frame budget. This is the real
  architectural answer to "GameDemo vs NetGame," but it is High effort / High risk (the ambient `Api.Services`
  global, the shared↔server cvar bridge, and `SoundService.Broadcast` are the concrete race blockers named in §5),
  and §5's own gate — server tick > ~2 ms in real matches *after* the areagrid — must be confirmed first.
- **Out:** the vsync/`rest` track (§2.1) as a fix for the bot hitch — Wave 0 disproved it.

**Bottom line for the user:** play in a **release** build (halves the felt cost and is free); the remaining true
hitch is the 154 MB model-build stall, which Wave 1 targets directly. Vsync/mailbox is a comfort option, not the cure.

---

## 10. Wave 1 — implemented (2026-06-11)

Both §9.4 Wave-1 items landed. Build green; **1496 tests pass**; verified live in a windowed `--host stormkeep
--bots 6` run (screenshots + the `[stream]` log).

### 10.1 Staggered live player-model builds (the 154 MB gen2 stall)

`NetGame.ResolvePlayerModel` no longer parses+builds synchronously on first sight. It returns a
`PlayerModel` **shell with a shared placeholder box** immediately and routes the load through the
(`SetupRender`-owned, unconditional) `BackgroundAssetStreamer`: the IQM+sidecar parse runs on the thread pool
(`AssetLoader.ParseSkeletalModel`), the Godot build (`BuildSkeletalModel` + `PlayerModel.Setup`) lands on the
main thread under the streamer's 2 ms budget at **High** priority — so N bots arriving in one snapshot build
**one model per frame** instead of all in that frame. Verified: 6 bots → 6 `[stream] player model … built …
placeholder swapped` lines (a `developer 1` Log.Trace), real posed/tinted models in the capture.

Pieces (all in place for reuse):
- `PlayerModel.ShowPlaceholder()/ClearPlaceholder()` — shared mesh+material (no per-shell pipeline compile);
  `Setup()` clears it. The placeholder is also **load-bearing for cache invalidation**: freeing it is what
  makes `CsqcModelEffects`' cached mesh list stale so the late-added real meshes get collected.
  `Setup()` also silences the `AnimationPlayer` BEFORE tree entry now (autoplay-after-entry is a no-op + warning).
- `ClientWorld.RebuildEntityModel(entity)` — tears down the model children (nameplate survives), releases the
  csqc effect state like `OnEntityRemove`, re-runs `TryAttachModel`. Used when an async resolve settles
  "not skeletal" (memoized in `NetGame._nonSkeletalPlayerModels` → the resolver returns null → the old MD3/
  static fall-through), or the entity's model changed mid-flight.
- `ClientWorld.SeedAppearance(entity)` — immediate colormap tint at delivery (the per-frame pass keeps it fresh).
- Delivery validates staleness (`pm` freed/queued, entity freed, model/skin changed) before touching the tree.
- A failed parse is BOXED (`SkeletalParseBox`) because the streamer drops null off-thread results silently —
  a miss must still deliver to trigger the fall-back attach.

Residual (accepted): `BuildForcedPlayerModel` (cl_forceplayermodels) still loads synchronously — explicit user
opt-in, and the forced model is typically the precached local model. `ModelTint.ApplyAppearance` still walks
meshes per frame per player (client-side; pre-existing).

### 10.2 sim.move / sim.integrate allocation hunt — **60.9 → 2.9 KB/tick (−95%)** @ 12 bots

Method: `Prof` per-scope alloc attribution via `BotTickPerfBench` (`XG_BOTS=12`), with new permanent sub-scopes
`move.pre/in/pm/post/link/touch` (GameWorld/SimulationLoop) and `start.warmup/bots/vote/defer/hooks`. Fixes, in
found order (each confirmed by a bench re-run):

| Fix | Scope | B/tick before → after |
|-----|-------|----------------------|
| `SimulationLoop`: cached `_runThink` delegate (the `RunThink` method-group arg allocated a `Func` **per entity per tick**) | sim.integrate | 19,475 → 185 |
| `MovementParameters.FromCvars`: per-(prefix,name) cvar-name cache — ~45 `prefix + "name"` concats ran **per player move** (server ticks × clients + every prediction replay) | move.pm | 31,366 → 35 |
| `ItemstimeMutator.Recompute` (runs every server frame): persistent accumulator + static `Note` (no closure) + prebuilt classname→key map (incl. `weapon_<superweapon>` names) + **single pass over `IEntityService.All`** (new default-interface member; `EntityService` already had it, `ServerEntityService` delegates; FindByClass fallback for fakes) — was a fresh Dictionary + closure + ~10 `FindByClass` iterators + concats per tick | start.hooks | 3,000 → ~0 |
| `BuffsMutator.Buff()`: memoized `"buff_" + shortName` (g_buffs **-1** ⇒ enabled in stock; its PreThink calls `Active()` ×5 per player per tick) | move.pre | 2,500 → ~0 |
| `BotPopulation.TickInput` now **implements IMovementInput** (returning the struct as the interface boxed per read ×2 readers × bots × ticks); `ZeroInput` boxed once (also in ServerNet) | move.in | −370 |
| `BotBrain`: per-weapon balance-cvar name cache (3 `$"g_balance_{w.NetName}_primary_*"` interps per think) | bot.think | 2,412 → 2,041 |
| `GameWorld.OnStartFrame`: cached `_deferredExec` delegate (the `Commands.Deferred.Pump` lambda captured `this` per tick) | start.defer | small |

Result @ 12 bots / 2160 ticks: **alloc 60,926 → 2,907 B/tick** (125.5 → 6.0 MB per 30 s), **gen0 16 → 1**,
gen2 0. Tick timing unchanged (med ~0.6 ms, MAX ~10 ms — the MAX is `move.post` weapon-fire bursts, real
gameplay events). Remaining buckets: `bot.think` ~2.0 KB/tick (token-gated `FindInRadius`/`FindByClass`
iterator allocs in goal rating — rare-path, diminishing returns), `move.post` ~0.6 KB (combat events).

### 10.3 Verification

- Bench: `XG_BOTS=12 dotnet test tests/XonoticGodot.Tests --filter BotTickPerfBench -l "console;verbosity=detailed"`.
- Live: windowed `--host stormkeep --gametype dm --bots 6 --screenshot …` → models posed/tinted, no streamer
  errors, no autoplay warnings; `--cvar developer 1` shows the six staggered `[stream]` builds.
- **Headless `--host` works** (verified this session: `--headless --host stormkeep --bots 2
  --quit-after-seconds 20` → exit 0, `[MapLoader]` loads stormkeep, waypoints load). An earlier draft of this
  section wrongly called it broken — that was a self-inflicted test artifact: Windows `timeout` does NOT kill
  the Godot child, so an orphaned host kept UDP 26000 and the *next* run failed with "Couldn't create an ENet
  host" / a fatal CLR error. **Use `--quit-after-seconds <s>` (not `timeout`) and kill orphaned Godot processes
  between scripted runs.** The FramePostDraw hang that genuinely broke this path was already fixed in a prior
  session: `Shell.WaitForFramePainted` awaited `frame_post_draw`, which never fires headless (the main loop
  never calls `draw()` with no window), so it now falls back to `process_frame` ticks under the headless
  DisplayServer and `GpuWarmPass` skips its SubViewport there. Guarded by the `ci/ci.sh` headless host smoke.

### 10.4 Remaining / todos

- **Wave 1 acceptance gate (USER step, not automatable here):** confirm the early-match ~154 MB gen2 freeze is
  actually gone *as felt* in an **exported release build** with bots (`run-release.sh`, then `--host … --bots`).
  The bench proves the per-tick allocation collapse and the staggered build is verified in a windowed run, but
  the original symptom was measured in §9.3 on a release build — close the loop there. (§7 protocol.)
- **Residual server-tick alloc (~2 KB/tick, LOW):** `bot.think` token-gated `FindInRadius`/`FindByClass`
  iterator allocs in goal rating, and `move.post` combat-event allocs. Diminishing returns vs. the −95% already
  banked; revisit only if a real-match profile shows GC pressure climbing with more bots.
- **Residual sync model loads (LOW):** `BuildForcedPlayerModel` (`cl_forceplayermodels`) still loads
  synchronously (explicit opt-in, usually the precached local model); `ModelTint.ApplyAppearance` still walks a
  player's meshes each frame (client-side, pre-existing). Stream/cache these only if a forced-model match or a
  high player count shows them in a profile.
- **Wave 2+ (structural, gated):** **S5 — server sim on a worker thread** (§9.4). The real architectural answer
  to "GameDemo vs NetGame," but High effort / High risk (the `Api.Services` ambient global, the shared↔server
  cvar bridge, and `SoundService.Broadcast` are the named race blockers) and gated on a real-match profile
  showing server tick > ~2 ms *after* the areagrid. Do **not** start it without that profile.
- **Still deferred from §8** (unchanged, gated on a manual `dotnet-counters` measurement): GpuParticles3D node
  pooling (3.2-1), GPU vertex-shader MD3 morphing (3.3 Tier-3). See §8 "Deferred."
