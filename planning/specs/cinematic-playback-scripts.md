# Spec — Cinematic Playback Scripts (keyframed camera + perspective timeline)

Companion to [`demo-replay-and-spectator.md`](demo-replay-and-spectator.md) and
[`video-capture.md`](video-capture.md). This is the **author-driven** counterpart to that spec's
*auto-generated* Director cam (§8): a human lays out a camera path and perspective cuts on a demo's
timeline, saves it, plays it back, and exports it to video. Reference (DP/QC): Source-engine demo
camera-path tools and `qcsrc/client/view.qc` (the chase/spectate cam this rides on top of).

> **Status:** ☐ not started — design approved 2026-06-13. Decisions: **full stack** (build T62–T65 in
> order, then this); **hybrid editor** (in-replay WYSIWYG pose capture + a menu dialog for fine-grained
> timeline editing); **smooth interpolation** (Catmull-Rom position spline, slerp orientation, per-keyframe
> ease in/out, look-at-a-tracked-player, cut-or-blend between shots). Depends on T63 (a playable replay
> with FreeFly + Follow + scrub) and T65 (the capture relaunch path).

---

## 1. Goal

Let someone turn a recorded match into a **cinematic**: define a camera that follows a hand-authored path
and cuts between perspectives over the demo's timeline, **save** that as a reusable *playback script*, play
it back, and **export it to video**. This is the feature the user asked for on top of the replay system:

> "record a path for the camera to follow … save those paths for later viewing and/or export their video
> with that path recorded."

### The three playback view modes (how this fits the replay system)

The replay system ([`demo-replay-and-spectator.md`](demo-replay-and-spectator.md)) exposes three ways to
watch a demo. The first two are that spec's existing camera modes under two usage rules; the third is new
and is what this spec adds:

| Mode | What it is | Recording? |
|---|---|---|
| **Fixed** | One perspective locked for the whole session — `Follow:<netId>` first-person or chase, no switching. | The default for video recording. |
| **Free** | Start from a chosen perspective, then switch freely while watching (FreeFly / Follow / cycle targets / Director). | Allowed but not the recording default (interactive). |
| **Scripted** *(new)* | A **playback script** drives the camera pose and perspective cuts over the timeline. | Records to a flawless cinematic via the capture path. |

A playback script *is* the Scripted mode's data. "Fixed" is the degenerate one-shot Follow script; "Free" is
no script at all (live viewer control).

### Non-goals (explicit)

- **No live-play scripting.** Scripts target *recorded demos*, not live matches (a script's keyframes are
  demo-time stamps; there is no demo time during live play).
- **No editing the demo.** A script never mutates the `.xgd`; it is a separate, side-car file that *references*
  one demo. Many scripts can target the same demo.
- **No new netcode.** Like the other camera modes (`demo-replay-and-spectator.md` §8), the Scripted camera is
  **purely client-side** view selection over the same networked entity stream.

---

## 2. The core idea: a script is a client-side camera track over the demo playhead

The replay already gives us everything load-bearing: a `DemoPlayback` playhead (`t_demo`, the demo-time
clock, `demo-replay-and-spectator.md` §3), interpolated recorded entities each frame, a `SpectatorCamera`
that produces the camera pose, and `FirstPersonView` that places the `Camera3D` from a `ViewState`
(origin/angles/fov, Quake space — [`FirstPersonView.cs`](../../game/client/FirstPersonView.cs)). A script
adds **one new `SpectatorMode`** that, each frame, evaluates the script at `t_demo` → a pose → feeds the
same `FirstPersonView`. Nothing downstream changes.

```
DemoPlayback.t_demo ──► CinematicScript.EvaluateAt(t_demo)
                              │  (spline pos + slerp angles + fov + look-at + shot blend)
                              ▼
                        ViewState {OriginQuake, ViewAnglesQuake, Fov}
                              │
                              ▼
              SpectatorCamera (mode = Scripted) ──► FirstPersonView.UpdateView(Camera3D, …)
```

Because the evaluator is a pure function of `t_demo`, it is **deterministic** and behaves identically under
interactive playback and under fixed-FPS capture (`video-capture.md` §2) — the same property that makes the
capture path "perfect" makes scripted capture perfect too.

---

## 3. Data model

A **playback script** is bound to one demo and is an ordered list of **shots** covering a span of the
timeline. Pure data, Godot-free (lives in `XonoticGodot.Net`/`.Engine` so it is unit-testable):

```
CinematicScript
  schemaVersion
  demoFile        (name of the .xgd this targets)
  buildParity     (must match the demo's header — playback rejects a mismatch, like the demo format)
  demoDurationTicks  (sanity-check the script lines up with the demo)
  name, notes
  shots: Shot[]   (ordered, non-overlapping, ascending startTime)

Shot
  startTime, endTime           (DEMO time, seconds — same clock as DemoPlayback.t_demo)
  transitionIn: Cut | Blend(durationMs)   (how we arrive from the previous shot)
  source: one of —
    Cinematic { poses: PoseKey[] }         (a free camera path — ≥1 keyframe)
    Follow    { netId, view: FirstPerson | Chase }   (a perspective selection — a player's eyes/back)
    FreeStatic{ pose: PoseKey }            (a fixed pose; degenerate Cinematic)
    Director                               (hand this shot to the auto-cam, demo-replay §8)

PoseKey
  time            (DEMO time, seconds)
  positionQuake   (camera origin, Quake space — matches ViewState.OriginQuake)
  orientation     (anglesQuake pitch/yaw/roll deg, OR lookAtNetId for auto-aim at a tracked player)
  fov             (degrees; the FirstPersonView base fov for this key)
  ease            (in/out easing applied to the segment parameter: Linear | EaseIn | EaseOut | EaseInOut)
```

Notes:
- **All times are demo time** (`t_demo`), independent of playback speed and capture FPS.
- **Quake-space poses.** `positionQuake`/`anglesQuake` are stored in the same Quake convention as
  `FirstPersonView.ViewState` so a captured FreeFly pose round-trips exactly and renders via the proven
  `Coords.ToGodot` path (no handedness surprises — [`FirstPersonView.cs`](../../game/client/FirstPersonView.cs)).
- **`lookAtNetId`** lets a *fixed-path* camera *auto-aim* at a moving player (orientation derived each frame
  from camera→target eye), the bread-and-butter cinematic move; falls back to stored angles if the target is
  absent at `t`.
- **Perspective availability** mirrors `demo-replay-and-spectator.md` §8: scripts work over **either** demo. A
  **server demo** is lossless (every entity present + recorded per-player view angles → faithful first-person and
  follow of anyone). A **client demo** is **PVS-limited** but **not restricted** — Cinematic/Follow/Director scripts
  all author and play, the data is just incomplete (subjects the server culled from the recording player are absent
  where they were hidden). The editor shows a non-blocking "data may be incomplete" warning rather than disabling
  any mode.

---

## 4. Evaluation & interpolation (the "smooth" target)

`CinematicScript.EvaluateAt(t_demo, IEntitySampler entities)` → `ViewState` pose. Pure; the only external
input is a sampler for `lookAtNetId` target origins (the same interpolated set the renderer already has).

1. **Locate the shot** whose `[startTime, endTime]` contains `t`. Before the first / after the last shot, the
   Scripted camera holds the first/last pose (capture quits at `endTime`, §6).
2. **Evaluate the shot's source:**
   - **Follow:** pose = target's interpolated origin + eye height, oriented by the target's recorded **view
     angles** (first-person) or pulled back via the existing chase cam
     ([`FirstPersonView`](../../game/client/FirstPersonView.cs) chase) (Chase).
   - **FreeStatic / Director:** the single pose / the auto-cam pose.
   - **Cinematic:** find the bracketing pose keyframes `k_i ≤ t < k_{i+1}`; segment parameter
     `u = (t − k_i.time) / (k_{i+1}.time − k_i.time)`; apply `ease` to `u`.
     - **Position:** Catmull-Rom spline through `{k_{i-1}, k_i, k_{i+1}, k_{i+2}}` (endpoints duplicated, or
       the adjacent shot's boundary pose when blended) → smooth, tangent-continuous path.
     - **Orientation:** if `lookAtNetId` set on either bracketing key, aim at the interpolated target eye;
       else convert both keys' angles → look quaternions and **slerp** by `u` (slerp, not Euler-lerp, to
       avoid yaw-wrap/gimbal artifacts).
     - **FOV:** lerp by `u`.
3. **Shot transition (`transitionIn = Blend`):** within the first `durationMs` of a shot, evaluate **both** the
   previous shot (held at its `endTime`) and this shot, then cross-fade: lerp position + slerp orientation +
   lerp fov by the blend parameter. `Cut` = no blend (instant).

All math is in `XonoticGodot.Net` (or `.Engine`) with no Godot types — `System.Numerics.Vector3`/`Quaternion`
and the existing `QMath`/`Coords` for the angle↔vector conversions — so interpolation is **headless-testable**.

---

## 5. Camera mode wiring

Add `Scripted` to the replay camera modes and delegate to the evaluator:

```csharp
enum SpectatorMode { FreeFly, Follow, Director, Scripted }   // demo-replay §8 + this
```

[`SpectatorCamera`](../../game/client/SpectatorCamera.cs) (from T63), when `mode == Scripted`, calls
`script.EvaluateAt(playback.DemoTime, entities)` and returns that pose; `NetGame._Process` feeds it to
`FirstPersonView` exactly as for the other modes. The viewmodel/gun is hidden in Scripted (it's a cinematic
camera, not a player eye) unless the active shot is a `Follow:FirstPerson` of a player.

---

## 6. Capture integration

The script plugs into the existing relaunch-to-capture path (`video-capture.md` §3a) as a new capture view:

```
--playdemo <demo.xgd> --capture-script <script.xgcs> --capture-video <out.mp4> [--capture-fps N] [--capture-size WxH]
```

`--capture-script <path>` is sugar for `--capture-view script:<path>`. [`VideoCaptureHook`](../../game/VideoCaptureHook.cs)
(from T65) loads the script, sets `SpectatorMode.Scripted`, **seeks `t_demo` to the script's first shot
`startTime`**, plays at `speed = 1`, and quits the tree when `t_demo` reaches the last shot's `endTime`
(finalizing the movie). Because the playhead steps by the fixed `1/fps` under Movie Maker mode and the
evaluator is deterministic, the exported camera move is bit-reproducible and audio-synced — the same guarantee
as a fixed-perspective capture.

---

## 7. The editor (hybrid: in-replay WYSIWYG + menu dialog)

### 7a. In-replay overlay — `CinematicEditor` (`game/hud`)

The WYSIWYG half, layered on the replay HUD next to [`ReplayControlBar`](../../game/hud/ReplayControlBar.cs)
(from T63). The author is *inside* the replay:

- **Fly & capture.** In FreeFly, fly the camera where you want; **Set Keyframe** captures the current
  origin/angles/fov at the current `t_demo` into the active Cinematic shot (or starts one).
- **Cut to a perspective.** **Add Cut** inserts a `Follow`/`Director` shot at the playhead; pick the player
  from the demo header roster; toggle first-person/chase.
- **Timeline.** Keyframe/shot markers on the scrub bar; drag to retime; click to select; **Preview** plays the
  script (camera mode flips to Scripted) and **Stop** returns to FreeFly editing.
- **Look-at.** Toggle a selected keyframe to look-at a chosen player (auto-aim) vs. its baked angles.
- **Save / Save As / Load.** Writes the `.xgcs` (§8).

### 7b. Menu dialog — fine-grained editing & management

The management/precision half, reached from `DialogMediaDemo` (T64). Lists `.xgcs` scripts (filtered, like
demos); **New** (pick a target demo) / **Edit** (numeric keyframe table: per-key time, position, angles, fov,
ease, look-at; per-shot transition) / **Play** (launch replay in Scripted mode) / **Export to video** (the §6
relaunch with an FPS/resolution/format sub-dialog, reusing T64's "Record to video" flow). The two halves edit
the same on-disk `.xgcs`; the in-replay overlay is for blocking shots by feel, the dialog for exact numbers.

---

## 8. Persistence

- **Format:** JSON (`.xgcs`, "Xonotic Godot Cinematic Script") — small, human-readable, hand-editable, version
  tolerant; *unlike* the binary `.xgd` demo, a script is tiny and edited, so text wins. Serialized via the
  project's existing JSON path; the model itself is Godot-free so round-trips are unit-tested.
- **Location:** `user://demos/scripts/` (sibling to the demos the VFS enumerates).
- **Binding & validation:** the header's `demoFile` + `buildParity` + `demoDurationTicks` are checked on load;
  a mismatch is reported honestly (no silent misalignment), consistent with the demo format's parity gate.

---

## 9. File-by-file change list (delta beyond T62–T65)

**New:**
- `src/XonoticGodot.Net/CinematicScript.cs` — the data model + `EvaluateAt` (spline/slerp/ease/blend/look-at),
  pure and headless-testable. (Or `XonoticGodot.Engine/Cinematic/` if it needs engine-only helpers.)
- `src/XonoticGodot.Net/CinematicScriptIo.cs` — JSON read/write + parity/duration validation.
- `game/hud/CinematicEditor.cs` — the in-replay WYSIWYG overlay (§7a).

**Touched:**
- `game/client/SpectatorCamera.cs` (T63) — add the `Scripted` mode delegating to the evaluator (§5).
- `game/hud/ReplayControlBar.cs` (T63) — keyframe/shot markers + the editor's timeline affordances.
- `game/menu/dialogs/DialogMediaDemo.cs` (T64) — script list + New/Edit/Play/Export (§7b).
- `game/VideoCaptureHook.cs` + `Main.cs` (T65) — `--capture-script` / `script:<path>` capture view (§6).
- `RUNNING.md` — document `--capture-script` next to `--capture-video`.

---

## 10. Testing

- **Evaluator (headless, `XonoticGodot.Tests`):** a synthetic script → `EvaluateAt` at sampled times yields
  the expected poses: pass-through at keyframes, monotone/ease-shaped parameter between them, Catmull-Rom
  continuity (no position discontinuity at keyframe boundaries), slerp orientation (no yaw-wrap blow-up across
  ±180°), correct shot selection at boundaries, and Cut vs. Blend cross-fade.
- **Look-at:** with a moving synthetic target, the derived orientation aims at the interpolated target eye each
  tick; absent target falls back to baked angles.
- **IO round-trip:** model → JSON → model is identical; parity/duration mismatch is rejected with a clear error.
- **Manual / capture:** author a short script over a recorded `--host` bot match, Preview it, then
  `--playdemo X --capture-script s.xgcs --capture-video out.mp4` → a smooth, deterministic cinematic; spot-check
  frames via the existing `--screenshot` path.

---

## 11. Phasing (after T62–T65 land, per the full-stack decision)

- **Phase C0 — Model + evaluator + IO.** `CinematicScript` (data + `EvaluateAt`, spline/slerp/ease/blend/look-at)
  + `CinematicScriptIo` + the headless tests (§10). No UI. *Deliverable:* a script evaluated to poses in tests.
- **Phase C1 — Scripted camera mode.** `SpectatorMode.Scripted` in `SpectatorCamera`; a hand-written `.xgcs`
  plays back over a demo. *Deliverable:* a saved script drives the replay camera.
- **Phase C2 — Scripted capture.** `--capture-script` in `VideoCaptureHook`/`Main`; seek-to-start + quit-at-end.
  *Deliverable:* export a hand-written script to a flawless video.
- **Phase C3 — In-replay editor.** `CinematicEditor` overlay: fly-and-set-keyframe, add-cut, timeline drag,
  Preview, Save/Load (§7a). *Deliverable:* author a cinematic without touching JSON.
- **Phase C4 — Menu integration & polish.** `DialogMediaDemo` script list + numeric editor + Play/Export
  (§7b); look-at toggle UI, blend controls, event-marker snapping. *Deliverable:* the full authoring loop.

---

## 12. Risks & open questions

- **Spline endpoints across shots.** Catmull-Rom needs phantom endpoints; duplicating boundary keys is simple
  but flattens tangents at cuts. Use adjacent-shot boundary poses when blended; accept clamped ends at hard
  cuts (an author rarely wants continuity across a cut anyway).
- **Editor while scrubbing.** The author sets keyframes against `t_demo`; pausing (speed 0) lets them line up a
  pose precisely while the scene holds (the two-clock model already supports this, `demo-replay §3`).
- **Client-demo completeness.** A cinematic over a PVS-limited client demo may have missing subjects/areas where the
  recording player couldn't see them (anti-cheat culling). This is allowed (not disabled): the editor and menu show a
  non-blocking "data may be incomplete" warning, and a shot that frames a culled subject simply renders what's present.
  A server demo avoids the caveat entirely.
- **Schema evolution.** `schemaVersion` + tolerant JSON; old scripts load or are rejected with a clear message.
- **Promote to an ADR?** Likely folds under the demo+capture ADR (`demo-replay §14`, `video-capture §10`) rather
  than its own — it adds no new load-bearing architecture, just a client-side track over the playhead.
