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

### 2.1 Vsync beat (CONFIRMED root cause of the investigated hitches)

With `vsync=Enabled`, `maxfps=0` on a ~160 Hz display, total frame work (~6 ms) sits right at the vblank budget (~6.25 ms). Any jitter (IDE overhead, a Gen0 GC, an unwarm pipeline) pushes the frame past the vblank → it waits for the *next* one → frame doubles/triples (the observed 12.5/16.7/20.4 ms frames with `rest` dominating).

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
