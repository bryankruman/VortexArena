# Recon T30 — Close the golden-trace loop + populate `trace_t.DpHitTextureName`

READ-ONLY recon. Two independent deliverables; (2) is the high-value, fully-achievable
one; (1) is feasible thanks to a working native engine but is larger and has a faithful
incremental form. T30 owns: `tools/movement-ref/*`, `tests/XonoticGodot.Tests/golden/*.json` +
`MovementParityTests.cs` + `MovementAnalyticTrace.cs`, `XonoticGodot.Engine/Collision/TraceService.cs`,
`XonoticGodot.Common/Services/Services.cs` (and, additively, `XonoticGodot.Engine/Collision/Brush.cs`
+ `XonoticGodot.Engine/Collision/BspCollisionBuilder.cs` — not owned by any other A2 task).

---

## PART A — DpHitTextureName (deliverable 2): the faithful spec + port gap

### A.1 Base behavior (the SPEC)

DP's trace result carries `const texture_t *hittexture` (the impacted surface's texture/shader).
The QC builtin exposes it as the global string `trace_dphittexturename`:

`Base/darkplaces/prvm_cmds.c:5242`
```c
PRVM_gameglobalstring(trace_dphittexturename) =
    trace->hittexture ? PRVM_SetTempString(prog, trace->hittexture->name, strlen(...)) : 0;
```
(reset to 0 in the no-collision setter, prvm_cmds.c:5260).

`trace->hittexture` is set in three collision paths:

1. **`Collision_TraceBrushBrushFloat`** (collision.c:559) — the SAT brush-vs-brush sweep, the
   exact algorithm the port's `TraceBrushVsBrush` mirrors. On an accepted enter event
   (collision.c:728-736):
   ```c
   trace->hittexture = hittexture;   // hittexture chosen at 676-694:
   ```
   - if the separating axis is one of `other`'s planes (`nplane < numplanes1`) →
     `other_start->planes[nplane2].texture` (collision.c:681)
   - if it's one of the moving brush's planes (`nplane < numplanes2`) →
     `trace_start->planes[nplane2].texture` (collision.c:688) — for a box trace this is
     a generated box brush with NO per-plane texture (NULL)
   - else (edge-cross axis) → `other_start->texture` (collision.c:693), the brush-wide texture.
   On a **startsolid** (started inside) it instead stores `starttexture` (collision.c:753), the
   brush-wide `other_start->texture` of the deepest-penetrating plane (638-643).
   `Collision_TraceLineBrushFloat`/`TracePointBrushFloat` (the ~681/688/693/731 lines the task
   cites) are the line/point degenerate forms of the same code.

2. **`Mod_Q1BSP_TraceLineAgainstSurfaces`** (model_brush.c:1408) — Q1BSP surfaces:
   `t->trace->hittexture = surface->texture->currentframe;`. Q1BSP leaf-content traces
   (no surface) use the static singletons `mod_q1bsp_texture_{solid,sky,lava,slime,water}`
   (model_brush.c:814-822). **Xonotic maps are Q3BSP — they do NOT take this path.**

3. **`Mod_CollisionBIH_TraceLineShared` / `_TraceBrushShared` / `_TraceBox`**
   (model_brush.c:6861 / 7332) — **THE path Xonotic Q3BSP maps go through** (mod->TraceBox =
   `Mod_CollisionBIH_TraceBox`, model_brush.c:4207/4891/7648/8352/8468). For each BIH leaf it
   dispatches by `leaf->type` (model_brush.c:7058-7076):
   - `BIH_BRUSH` → `Collision_TraceLineBrushFloat(trace,…,brush,brush)` — the brush carries its
     own per-plane + brush-wide `texture` (set at load), so hittexture flows from path (1).
   - `BIH_COLLISIONTRIANGLE` / `BIH_RENDERTRIANGLE` → `Collision_TraceLineTriangleFloat(…,
     texture->supercontents, texture->surfaceflags, texture)` with
     `texture = model->data_textures + leaf->textureindex` (model_brush.c:7068/7073). The
     triangle trace stamps `trace->hittexture = texture` on a hit.

   **The texture object** is `model->data_textures[idx]`, and `texture_t.name` is the shader
   name as it appears in the BSP texture lump (e.g. `"common/caulk"`, `"common/clip"`,
   `"exx/base/floor01"`). For Q3BSP this name == the BSP texture-lump `ShaderName`.

#### The real gameplay consumer (why this matters — not cosmetic)

`Base/.../qcsrc/server/world.qc:1158-1170` — `MoveToRandomLocationWithinBounds` /
`WantsToExtendMapForFindRandomLocation` (the random-location finder used by item respawn and
the "find a spot" spawn fallback) rejects a candidate point whose horizontal/vertical
sightlines hit nothing **or hit `common/caulk`** (a point outside the playable hull):
```c
traceline(mstart, mstart + '1 0 0' * delta.x, MOVE_NORMAL, e);
if (trace_fraction >= 1 || trace_dphittexturename == "common/caulk") continue;  // ×6 axes
```
Other engine consumers:
- `lib/warpzone/server.qc:378,393` — save/restore the global across a nested trace (QC pointer
  workaround; **moot for the port** — the port returns `TraceResult` by value, see A.4).
- `common/physics/movetypes/movetypes.qc:416,430,446 / 467,494,512` — `_Movetype_PushEntity`
  touch dispatch save/restore (same QC-globals workaround; **moot for the port**).

The two physics.qc references (physics.qc:532, :556) are inside `sys_phys_simulate_simple`'s
touch-area-grid synth, where it CLEARS the globals before a touch callback; the port's
`SimulationLoop` touch path is separate and doesn't read texture names.

So the ONE behavior that depends on a populated value is the `common/caulk` spawn/location
rejection. **The port's `SpawnSystem.cs` (server/Player/SpawnSystem.cs) ports
`Spawn_FilterOutBadSpots` but ONLY the `trace_startsolid` rule** (SpawnSystem.cs:394-397) — it
has no caulk/outside-the-hull check because `DpHitTextureName` was never populated. Populating it
unblocks a genuinely-unportable QC check (a T36 follow-up could then wire it; see Conflicts).

### A.2 Port state (what exists / what's the gap)

- `TraceResult.DpHitTextureName` **field already exists** but is DEAD: never written by any
  producer, read by no consumer. (`Services.cs:27`; the only hit in src is the declaration.)
- `TraceService.TraceBrushVsBrush` (TraceService.cs:281-429) already threads **SurfaceFlags**
  (`hitSurfaceFlags`, axisFromOther logic at 318/325/341/378) and **Contents** through the
  identical SAT enter-event path. The texture name has NO analog because `Brush`/`BrushPlane`
  carry no name.
- `Brush` (Brush.cs:157) has `Contents`, `SurfaceFlags`, `Sides[]`(`BrushPlane` with per-plane
  `Contents`+`SurfaceFlags`), `Points`, `EdgeDirs` — **no texture name**.
- `BspCollisionBuilder.BuildBrush` (BspCollisionBuilder.cs:141-189) reads
  `bsp.Textures[brush.TextureIndex]` for `ContentFlags`/`SurfaceFlags` but discards
  `BspTexture.ShaderName`. `AppendPatchBrushes` (BspCollisionBuilder.cs:322-362) likewise reads
  `bsp.Textures[face.TextureIndex]` and discards the name.
- `BspTexture` (BspData.cs:182) = `record struct (string ShaderName, int SurfaceFlags,
  int ContentFlags)` — **the name is already parsed and on hand at build time.** The whole
  deliverable is: thread that string `ShaderName` → `Brush` → SAT sweep → `TraceResult`.

### A.3 Implementation plan (mirror SurfaceFlags exactly — it's the proven template)

1. **`Brush.cs`** (T30 may edit; not owned by another A2 task):
   - Add `public string? Texture;` to `BrushPlane` (parallels its `SurfaceFlags`/`Contents`).
   - Add `public string? Texture;` to `Brush` (brush-wide; parallels `SurfaceFlags`).
   - Add an optional `string? texture = null` param to both `BrushPlane` ctor and the `Brush`
     ctor, AND to `Brush.FromBox(... , string? texture = null)` (so box-brush callers compile;
     the moving box passes null → matches DP's box brush having no per-plane texture). Propagate
     `Texture` through `Brush.Transform` (copy `Sides[i].Texture`; copy brush-wide `Texture`).
2. **`BspCollisionBuilder.cs`** (T30 may edit; not owned by another A2 task):
   - In `BuildBrush`: capture `string? texName = (idx valid) ? bsp.Textures[idx].ShaderName : null;`
     pass it to each `BrushPlane` and to the final `new Brush(...)`. (Per-side: there is a
     `side.TextureIndex` for IG/v48 maps — prefer `bsp.Textures[side.TextureIndex].ShaderName`
     when `side.TextureIndex` is valid, else fall back to the brush-wide `texName`, mirroring the
     existing `sideSurfaceFlags` fallback at BspCollisionBuilder.cs:165-169.)
   - In `AppendPatchBrushes`/`TryBuildTriangleSlab`/`AddEdgePlane`: thread `surfaceName` the same
     way `surfaceFlags` already flows, so a curve hit (DP's BIH_COLLISIONTRIANGLE) reports the
     patch shader name.
3. **`TraceService.cs`** — mirror the SurfaceFlags machinery exactly:
   - In `TraceBrushVsBrush`, add `string? hitTexture = null;` next to `hitSurfaceFlags`.
   - In the enter-event accept block (TraceService.cs:367-379), set
     `hitTexture = axisFromOther ? other.Sides[nplane].Texture : other.Texture;`
     — EXACTLY paralleling `hitSurfaceFlags = axisFromOther ? axisSurfaceFlags : other.SurfaceFlags`
     (line 378). Capture `other.Sides[nplane].Texture` in the `nplane < n1` branch (alongside
     `axisSurfaceFlags` at 318); box/edge axes use the brush-wide `other.Texture` (DP collision.c
     688/693: a box-plane axis has NULL texture, so an edge/box axis correctly falls back to
     `other.texture`). NOTE: when the chosen axis is a *box* plane (`!axisFromOther`, `nplane<n2`),
     DP would use `trace_start->planes[].texture` = NULL → so the brush-wide `other.Texture`
     fallback is the faithful choice (the box has no real shader). Document this one deliberate
     simplification (DP NULL → port falls back to brush-wide name; functionally identical for the
     `common/caulk` consumer because caulk faces are `other`'s planes).
   - In the startsolid branch (TraceService.cs:421-428), set `trace.HitTexture = <brush-wide
     other.Texture>` to mirror DP's `starttexture` (collision.c:753).
   - Add `public string? HitTexture;` to `SweepState` (next to `HitSurfaceFlags`, line 528).
   - In `BuildResult` (TraceService.cs:497-515), inside `if (s.Hit)` add
     `r.DpHitTextureName = s.HitTexture;` (next to `r.DpHitQ3SurfaceFlags` at 510).
4. **`MovementAnalyticTrace.cs`** (T30-owned test stub): the analytic `Brush` is a separate type
   with only `Planes`+`Contents`; it needs NO change (it never sets DpHitTextureName, and
   `MovementParityTests` never reads it). Leave it; just confirm it still compiles after the
   `Services.cs` field is already present (it is — no change there).
5. **`Services.cs`**: the field already exists; **no edit needed** unless adding an XML-doc note.
   (List it under hotFileNeeds defensively since T30 "owns" it, but the minimal edit is zero.)

### A.4 Parity traps / deliberate deviations

- **QC globals save/restore is a non-issue.** movetypes.qc/warpzone save+restore
  `trace_dphittexturename` only because QC trace_* are globals clobbered by nested traces. The
  port's `Api.Trace.Trace(...)` returns a value `TraceResult`, so there is nothing to save/restore
  — do NOT add a save/restore dance. (Confirmed: movetypes.qc:416/446, warpzone server.qc:378/393.)
- **Box-plane axis → NULL in DP.** When the impact separating axis belongs to the *moving box*,
  DP's hittexture is NULL (box brushes have no texture). The faithful port choice is to fall back
  to the brush-wide `other.Texture` (still the correct shader of the surface you hit). This is the
  one intentional deviation; harmless for every consumer (caulk faces are world brush planes).
- **Q3 vs Q1 texture-name forms.** Xonotic Q3BSP names are the raw shader path
  (`common/caulk`, `common/clip`, `mapname/texture`). Do NOT prefix/normalize — `world.qc`
  compares `== "common/caulk"` literally. Verify the BspReader keeps the lump string verbatim
  (BspData.BspTexture.ShaderName) — it does (BspReader.ReadTextures).
- **Patch/triangle brushes** carry the patch shader name (e.g. `common/clip`,
  `mapname/grate`). DP's BIH_COLLISIONTRIANGLE stamps `texture->name`; the port's tessellated
  slab should carry the same.
- **Null/missing texture** → leave `DpHitTextureName = null` (DP sets `trace_dphittexturename = 0`
  / string_null). `world.qc`'s `== "common/caulk"` is false for null — correct.

### A.5 Test plan (deliverable 2)

New tests in `tests/XonoticGodot.Tests/BspCollisionTests.cs` (T30 may add; the harness is already
there — `AddBox`/`TwoBoxBsp` at BspCollisionTests.cs:27-67 builds a BspData with a named
`BspTexture`):
1. `Trace_Reports_HitTextureName_OfWorldBrush`: build a one-box world from a
   `BspTexture("common/caulk", 0, Solid)`, trace a box INTO the +X face, assert
   `tr.DpHitTextureName == "common/caulk"` and `tr.Fraction < 1`.
2. `Trace_Reports_HitTextureName_Differs_PerBrush`: two boxes with distinct shader names
   (`"a/floor"`, `"b/wall"`); trace into each, assert the correct name on each.
3. `Trace_Miss_LeavesTextureName_Null`: trace that misses → `tr.DpHitTextureName is null`.
4. `Trace_StartSolid_ReportsBrushTexture`: start inside the box → `StartSolid` true and
   `DpHitTextureName == "<that box's shader>"` (mirrors DP starttexture).
5. (regression) Re-run `MovementParityTests` + the existing `BspCollisionTests` /
   `BspPatchCollisionTests` to prove the SurfaceFlags/Contents/fraction paths are byte-identical
   (the new field must not perturb the SAT math — it's set in the same accept block, no control-flow
   change).
6. (optional, faithful) A patch test: build a Patch face with a named shader, trace onto it,
   assert the slab reports that name (proves the BIH_COLLISIONTRIANGLE analog).

Build/run: `dotnet build XonoticGodot.csproj`; `dotnet test tests/XonoticGodot.Tests --filter BspCollision`
(Windows `dotnet`, per MEMORY: Godot SDK from editor nupkgs).

---

## PART B — Close the golden-trace loop (deliverable 1)

### B.1 What "today" is

`tools/movement-ref/movement_ref.c` (974 lines) is an INDEPENDENT analytic C reference,
transcribed line-for-line from the *preprocessed* QC (`.tmp/server.txt`). It hand-builds 4 convex
brush worlds (flat / stairs / 30° ramp / water) with its OWN brush-vs-box trace
(`clip_box_to_brush` / `world_trace`, movement_ref.c:157-224), runs 10 scenarios, and emits
`tests/XonoticGodot.Tests/golden/*.json`. `MovementParityTests` replays each fixture through the
ported `PlayerPhysics` against a C# twin of that same analytic trace
(`MovementAnalyticTrace.cs` — `AnalyticWorld`/`AnalyticTraceService`), tolerances PosTol 0.20 qu /
VelTol 0.40 qu/s (MovementParityTests.cs:32-33).

**Fidelity confirmed during recon** (so the corpus is already trustworthy as a transcription):
- `movement_ref.c`'s `stock()` (lines 95-108) == `physicsX.cfg` verbatim: gravity 800,
  maxspeed 360, stopspeed 100, accelerate 15, airaccelerate 2, friction 6, jumpvelocity 260,
  airaccel_qw -0.8 (verified against `Base/.../physicsX.cfg:4-33`).
- `movement_ref.c`'s `PM_Accelerate` (268-308) == `player.qc:280-341` line-for-line (speedclamp
  ternary, AdjustAirAccelQW, fwd/back/straight bounds, sidefric branch, speedclamp).
- `sys_phys_simulate` branch order (movement_ref.c:566-666) == `physics.qc:198-476`.
- `physics.qc`'s `sys_phys_update` (the live driver) selects branches off the PERSISTENT
  FL_ONGROUND (physics.qc:88, 148) — the port mirrors this (PlayerPhysics.DetermineOnGround,
  PlayerPhysics.cs:971-980) and the README notes 2 bugs the corpus already caught.

### B.2 "Capture from a running Darkplaces" — FEASIBILITY: YES, a built engine exists

**A working native Windows engine is present and boots the real Xonotic gamedir:**
`Base/darkplaces/darkplaces-sdl.exe` (4.6 MB, PE32+ x86-64, built 2026-06-05, "Windows64
dedicated d93f9c42"). Verified in recon: launched with `-game data` it execs the FULL real config
chain — `quake.rc → default.cfg → xonotic-common.cfg → xonotic-server.cfg → physicsX.cfg →
gametypes-server.cfg → …` and reaches `SpawnServer: _init/_init` + Q3 shader parsing. The
compiled progs are present (`xonotic-data.pk3dir/{progs,csprogs,menu}.dat`), maps live in
`xonotic-maps.pk3dir`, and `run-xonotic.sh` / `RUNNING-xonotic.md` document the canonical launch
(`./all run sdl …`, native Windows, isolated userdir). So a REAL-engine capture is genuinely
achievable — this is NOT a "needs a build we don't have" situation.

### B.3 The honest problem: a deterministic, scriptable capture is the hard part

Recording *real engine traces* faithfully needs three things the bare engine doesn't hand you:
1. **Deterministic per-tick input injection** — the same input sequence the corpus encodes
   (`ang/move/jump/crouch/dt`). DP movement is driven by the client's `usercmd` (`PHYS_CS(this).
   movement`, `v_angle`, buttons); there is no stock console command to push a scripted usercmd
   stream into the server physics tick.
2. **Fixed dt** — set `sys_ticrate` / `cl_netfps` so each physics frame is exactly 1/32 s
   (the corpus dt). DP supports `host_framerate 0.03125` (forces a fixed frame dt) +
   `sv_fixedframeratesingleplayer 1`; a `timedemo`/`-benchmark` run is frame-locked.
3. **State dump** — emit `origin/velocity/onground/waterlevel` (and the inputs) each tick to a
   file in the JSON schema the test already parses (MovementParityTests.cs:126-162).

Two viable faithful mechanisms, in increasing effort:

**(B.3a) Server-side QC dumper via a tiny extra .qc + `prvm_edictset`/console (RECOMMENDED
faithful increment).** Because the real `progs.dat` runs, you can drive physics directly:
- Spawn a bot or a dummy player entity on a flat test map (or build a tiny `.bsp` of the same
  flat/step/ramp/water worlds — but a stock map like `dm_-` works for flat/air).
- Each `frametime`, set `self.movement`, `self.v_angle`, the jump/crouch buttons from a baked
  table, call the normal `PlayerPreThink`/physics, then `print`/`fputs` the post-physics
  `origin/velocity/IS_ONGROUND/waterlevel`. This is the MOST faithful: it's the engine's own
  collision + physics, not a transcription.
- Practicality: this requires either (i) a small QC patch + a gmqcc rebuild of `progs.dat`
  (the QC toolchain build is documented in MEMORY xonotic-build-setup, but gmqcc isn't confirmed
  built natively), or (ii) a `csqc`/menu QC hook, or (iii) a demo replay (below). HONEST: a QC
  rebuild is the biggest lift and the least "drop-in".

**(B.3b) Demo capture + parse (NO rebuild; most drop-in real-engine path).** Record a demo of a
scripted movement (`cl_demo`, or `-benchmark` a canned demo), then parse the demo's player
entity-state stream (origin/velocity are networked). DP demos are `.dem` protocol streams; the
port already has a net layer (`XonoticGodot.Net`) that could decode entity snapshots. This captures
REAL engine state but at NETWORK precision/rate (entity origins are quantized; velocity may be
client-derived) — lower fidelity than B.3a, and the input-side (exact per-tick move) must be
reconstructed from the recorded usercmds in the demo. Faithful for trajectory shape, weaker for
ULP-exact parity.

**What is NOT achievable headlessly right now:** a turnkey "press a button, get byte-exact
golden JSON from DP" — neither a stock console command nor an existing tool does it; both faithful
paths (B.3a QC dumper, B.3b demo parse) need new tooling, and B.3a additionally needs a
`progs.dat` rebuild to add the dumper QC.

### B.4 Concrete, faithful, testable increment (what I recommend T30 actually do)

Given the cost asymmetry (deliverable 2 is small + unblocks a real consumer; deliverable 1's
ideal form needs a QC rebuild), the faithful increment for (1) WITHOUT over-reaching:

1. **Validate the existing analytic corpus against the real engine's CONFIG + math, and document
   the gap honestly** — i.e. promote `movement_ref.c` from "transcription we hope is faithful" to
   "transcription cross-checked against the live engine," by:
   - Adding a `tools/movement-ref/verify-against-dp.md` (NOT a report .md — a tool doc) that
     records the exact `darkplaces-sdl.exe -game data` launch + the cvar dump
     (`physicsX.cfg` values it execs) so the `stock()` table is provably the engine's.
   - This is achievable TODAY with the existing binary (no rebuild): launch dedicated, `cvarlist
     sv_*` / `prvm_globalset`, diff against `stock()`.
2. **Add ONE real-engine "smoke" scenario** captured via the lowest-effort faithful path
   feasible in this environment (assess at impl time): if a `progs.dat` rebuild is available
   (gmqcc), add the B.3a QC dumper for a single `free_fall` + `ground_accel_forward` capture and
   commit it as `golden/dp_*.json` alongside the analytic ones, with `MovementParityTests`
   asserting the port matches the DP-captured trace at a looser network-aware tolerance. If a
   rebuild is NOT available, STOP at step 1 + a written, file-specific plan in the tool doc for
   the B.3a path (exact QC snippet + build command + JSON schema) so a follow-up can finish it.
3. **Keep the analytic corpus as the primary guard** — it's ULP-tight and already catches port
   bugs; the DP capture is an additive cross-check, not a replacement (the analytic trace is the
   ONLY way to get ULP-exact collision parity since DP's collision is double-internally in places).

Be explicit in deliverables that (1) lands as: corpus-fidelity validation + a captured smoke
scenario IF a QC rebuild is in reach, ELSE a precise written capture plan; (2) lands as working
code + tests. Do not claim a full DP-capture pipeline unless the rebuild succeeds.

### B.5 Risks (deliverable 1)

- **No drop-in capture.** Both faithful paths need new tooling; the QC-dumper path needs a
  `progs.dat` rebuild (gmqcc) whose native availability is unconfirmed. Mitigation: gate step 2
  on the rebuild; deliver the plan if not.
- **DP collision is not bit-identical to the analytic trace.** DP uses `collision_impactnudge`
  (0.03125) and double-precision in spots; the analytic ref uses `TRACE_DIST_EPSILON 1/32`. A
  real-engine capture will differ from the analytic golden by small amounts even when the port is
  correct — so a DP-captured fixture MUST use a looser tolerance than PosTol 0.20 (document why).
- **Network-precision loss (B.3b).** Demo-derived origins are quantized; not suitable for tight
  parity — only trajectory-shape validation.
- **Scope creep.** The temptation is to build a full input-injection harness. Keep it to 1-2 smoke
  scenarios; the analytic corpus remains the workhorse.

---

## Conflict awareness (A2 cross-task)

T30's file set is almost entirely T30-private:
- **`TraceService.cs`, `Services.cs`** — only T30 (and T30 is the named owner). No other A2 task
  edits the trace service or the services facade. **No conflict.**
- **`Brush.cs`, `BspCollisionBuilder.cs`** — engine collision; not in ANY other A2 task's owned
  set (T36 touches GameWorld/MapObjectsRegistry/GameTypes; T37 Simulation/* but not Collision/*;
  T43 Monsters/Damage; etc.). **No conflict.**
- **`MovementParityTests.cs`, `MovementAnalyticTrace.cs`, `tools/movement-ref/*`, `golden/*`** —
  T30-private. **No conflict.**

The ONE cross-task *interaction* (not a file conflict): populating `DpHitTextureName` unblocks the
`common/caulk` spawn/location rejection in **`SpawnSystem.cs`** (server/Player/SpawnSystem.cs) and
the random-location finder (`world.qc` analog). **`SpawnSystem.cs` is NOT in T30's owned set and
NOT listed for any A2 task** — so T30 should NOT edit it; T30 only makes the field truthfully
populated. Flag for the orchestrator: a *follow-up* (or T36, which owns spawn-adjacent
mode-objective spawnfuncs + GameWorld) could add the caulk check now that the field works. T30's
job ends at "field is correctly populated + tested."

**Registration model (confirmed):** the A1 lesson holds — gametypes/mutators/items/monsters
auto-register via `[GameType]`/`[Mutator]`/etc. reflection attributes (the `XonoticGodot.SourceGen`
`RegistryGenerator` emits `GeneratedRegistrations`; GeneratorHelpers.cs:76-101 lists the attribute
names). T30 touches NONE of that — no `Registries.cs`/`GameInit.cs` edit. The collision-builder
texture threading is plain field plumbing, no registry involvement.

## hotFileNeeds summary

T30 has **no true hot-file conflict** with another A2 task. `Services.cs` and `TraceService.cs`
are T30-owned and untouched by others. The only file another task *might* want that T30's work
*enables* is `SpawnSystem.cs` (caulk check) — but T30 does not and should not edit it. Listing
`Services.cs` defensively (T30 owns it; minimal/zero edit).
