# Spec — Demo Replay & Spectator (rewindable)

Implements / extends [ADR-0005](../decisions/ADR-0005-custom-netcode.md) (custom netcode) and builds on
[`networking.md`](networking.md). Reference (DP/QC): `Base/darkplaces/cl_demo.c` (the engine `.dem`
record/playback), `qcsrc/server/demo` glue, `qcsrc/client/main.qc` + `view.qc` (chase/spectate cam,
`spectatee_status`), `qcsrc/menu/xonotic/dialog_media_demo.qc`.

> **Status:** ☐ not started — design approved, implementation phased below.

---

## 1. Goal

Record matches and play them back as a **rewindable, time-controllable replay** that the full game and menu
stack runs *on top of*. A viewer joins a replay as a **spectator** and can, at will:

- **Free-fly** anywhere through the recorded match (existing spectator free-flight).
- **Follow a player** — snap into that player's first-person view, or a 3rd-person chase cam; cycle targets.
- **Watch a director / action cam** — an auto-generated cinematic view that frames the action.

…with full **time control**: pause, slow-motion, fast-forward, smooth rewind, and instant seek (scrub).

### Non-goals (explicit)

- **No interaction with the recording.** Spectators never collide with, damage, or are blocked by recorded
  entities. This rules out the deterministic full-resim path; recorded entities are *kinematic, observation-only*.
- **No DP `.dem` wire compatibility** (consistent with [ADR-0011](../decisions/ADR-0011-protocol-ecosystem-boundary.md)).
  XonoticGodot demos are their own format.
- **No in-match "rewind time" gameplay mechanic** — this is about replaying recordings, not rewinding live play.

---

## 2. The core idea: a replay is a listen server whose entities come from a demo

The play stack is already structured so this falls out cleanly:

- A **listen server** ([`NetGame`](../../game/net/NetGame.cs)) boots a `GameWorld` + `ServerNet` in-process and
  **self-connects a local `ClientNet`** to `127.0.0.1`. The client, renderer, HUD, scoreboard, and menus are
  **agnostic** to what produced the server's state — they just consume snapshots and predict/interpolate.
- `ServerNet.BroadcastSnapshots` → [`BuildEntitySet`](../../game/net/ServerNet.cs) is **content-blind**: it reads
  whatever entities are in the world and serializes them. It does not care whether a live sim or a demo put them there.
- **Free-flying spectators already exist** — the "T44 spectator free-flight" path
  ([`ServerNet.DriveObserverJoins`](../../game/net/ServerNet.cs), `PlayerPhysics.SpectatorControl`) gives observers
  `MOVETYPE_NOCLIP/FLY`, networked like any client.

So:

> **A replay = a listen server hosted in "replay mode," where the recorded match entities are injected from a
> demo each tick, and every human viewer joins as a free-flying spectator.**

Everything the user asked for — "game and menu code operating over top of a demo," spectators that move around,
follow players, and watch a director cam — is then either free (it's just a normal client) or a small, contained
addition (the camera modes and the time controls).

```
demo file ──► DemoPlayback (playhead, sample/seek)
                    │  inject recorded NetEntityState set + event streams each tick
                    ▼
            GameWorld (replay mode: match rules inert, map loaded)
              └─ viewers = live free-flying OBSERVERS (existing spectator sim)
                    │
                    ▼
            ServerNet.BroadcastSnapshots  ──(recorded entities + nothing else)──►  every viewer
                    │
                    ▼
            normal ClientNet / ClientWorld / HUD / menu   (+ SpectatorCamera + ReplayControlBar)
```

### Record server-side, not in-eye

A DP-style **client** demo only captures one player's PVS — fly behind them in a replay and there is nothing
there. We record on the **server**, which holds the full authoritative entity set every tick
([`ServerNet.BuildEntitySet`](../../game/net/ServerNet.cs) builds exactly the `Dictionary<int, NetEntityState>`
we need). This is omniscient and lossless → free-cam spectating works everywhere. It also matches the existing
[`DemoControl`](../../src/XonoticGodot.Server/DemoControl.cs) scaffold (`sv_autodemo`), which already decides
*when/which* to record and hands the byte-writing to a host hook that this spec finally supplies.

---

## 3. The two-clock time model (the heart of slow-mo / pause / rewind)

Time controls are clean **only if** the recorded match and the live spectator are on *separate clocks*:

| Clock | Advances by | Drives |
|---|---|---|
| **Sim / snapshot clock** (`GameWorld.Time`) | `realDelta` (always real time) | spectator movement, snapshot timestamps, client interpolation |
| **Demo playhead** (`t_demo`) | `realDelta × speed` (scaled), or set directly on seek | which recorded frame the injected entities show |

Because snapshot **timestamps** always advance at real time while the injected entity **content** advances at the
playhead rate, the existing client-side snapshot interpolation renders the result correctly with no special cases:

- **Pause** (`speed = 0`): playhead frozen → recorded entities hold still; timestamps keep flowing so the stream
  stays alive and **spectators keep flying smoothly** through a frozen scene (the Overwatch-replay feel).
- **Slow-mo** (`speed = 0.25/0.5`): recorded positions change in small steps between snapshots → smooth slow motion.
- **Fast-forward** (`speed = 2/4`): playhead crosses several recorded ticks per snapshot → still interpolated smoothly.
- **Smooth rewind** (`speed = -1`): playhead decreases → injected positions move backward by normal per-tick deltas
  → the client interpolates smooth reverse motion. No seek needed.
- **Instant seek / scrub** (playhead jumps): set `t_demo` directly; flag every injected entity `Teleported`
  (existing `NetEntityFlags.Teleported`) so the client snaps instead of lerping across the discontinuity, and reset
  transient client FX (below).

The viewer's own spectator body is **never part of the demo**, so rewinding the match does not rewind the camera —
exactly the desired behavior.

---

## 4. Recording

### Where it hooks

`ServerNet.Tick` already, per frame: `_world.Frame()` → `BuildEntitySet` → `BroadcastSnapshots` →
`FlushEventBundles`. The recorder taps the data that is *already assembled*:

- the per-tick entity set `_entityScratch` (`Dictionary<int, NetEntityState>`),
- the captured event queues `_effectQueue`, `_soundQueue`, `_notifyQueue`,
- the score/score-info blocks and movevars,
- plus header facts (map, gametype, tick rate, build-parity, player roster).

`ServerNet` exposes a single optional sink:

```csharp
public IDemoSink? DemoSink { get; set; }   // null = not recording
// called once per tick, right after BuildEntitySet, before the per-client encode:
DemoSink?.RecordTick(now, _entityScratch, _effectQueue, _soundQueue, _notifyQueue,
                     _scoreRows, _scoreTeams, scoreVersion, _moveVars, moveVarsHash);
```

`DemoRecorder : IDemoSink` (in `XonoticGodot.Server`) owns the file writer and the keyframe cadence.
[`DemoControl`](../../src/XonoticGodot.Server/DemoControl.cs)'s `StartRecording`/`StopRecording` actions get wired
by the host to construct/dispose a `DemoRecorder` and attach it as `ServerNet.DemoSink` (closing the
"host wires to the engine recorder" TODO that file documents).

### What is captured each tick

A frame is either a **keyframe** (full entity set + full block state) or a **delta** (only changed entities, via
the existing [`EntityStateCodec`](../../src/XonoticGodot.Net/NetEntity.cs) / `SnapshotDelta`), plus the event lists
for that tick. Keyframe cadence: every `demo_keyframe_interval` ticks (default ~2 s @ 72 Hz = every 144 ticks) and
always on frame 0 and on any tick where the recorder is told the world reset (map/round change).

---

## 5. File format

Self-contained, versioned, seekable. Reuse the existing wire codecs (`BitWriter`/`BitReader`, `EntityStateCodec`,
`SoundWire`, the score blocks) so there is *one* serialization to maintain.

```
Header
  magic "XGDM" + formatVersion
  buildParity (NetProtocol.BuildParity at record time — playback rejects a mismatch)
  tickRate, mapName, gametype, startWallclock (passed in; scripts can't read the clock)
  player roster: [netId, name, team, modelName, colormap]   (for the spectate target list + scoreboard seed)
  durationTicks, keyframeInterval
Frame stream  (one record per recorded tick)
  tick (uint), serverTime (float), isKeyframe (bool)
  entitySection : keyframe → full NetEntityState set ; delta → SnapshotDelta vs previous frame
  events        : effects[] (EFF_NET bodies) ; sounds[] (SoundWire) ; notifications[]
  blocks        : scoreboard/scoreinfo/movevars — only when changed (same "send bool" pattern as the snapshot)
Trailer / index
  keyframe index: [tick → byte offset]   (O(log n) seek)
  loop-sound index: derived start/stop intervals per (sourceNetId, channel)  (see §7)
```

Notes:
- **ID namespacing.** Recorded entity ids live in their original match's id space. The replay assigns *its own*
  small ids to live spectators (1..N). Offset recorded ids into a high range (mirroring `EntityNetBase`) at
  inject time, or store an explicit remap in the header, so the two spaces never collide.
- **Pure & testable.** `DemoFormat` (read/write) lives in `XonoticGodot.Net` with no Godot dependency, so a
  headless round-trip test (record set → write → read → re-derive set) can assert byte-exactness.
- **Streaming, not all-in-RAM.** Keep the keyframe index in memory; read frames from disk on demand. A 10-minute
  match at 72 Hz is ~43k frames; full-RAM keyframes-every-tick would be hundreds of MB, hence keyframe+delta + the
  on-disk index.

---

## 6. Playback authority

`DemoPlayback` (in `XonoticGodot.Server`, logic-only where possible) owns:

- the loaded `DemoFormat` reader + keyframe index,
- the playhead `t_demo` and `speed`,
- a reconstructed **current entity set** (`Dictionary<int, NetEntityState>`) at the playhead,
- the event window bookkeeping.

It plugs into `ServerNet` through one hook that **replaces** the live entity scan (in replay mode there are no
match players — all humans are observers, and observers are intentionally skipped by `BuildEntitySet`, so the
recorded set is the *only* source of networked entities):

```csharp
public IReplayEntitySource? ReplaySource { get; set; }  // when set, BuildEntitySet uses this instead of the world scan
```

Per tick the replay host:
1. advances `t_demo += realDelta * speed` (clamped to `[0, duration]`), or applies a pending seek;
2. `DemoPlayback.SampleAt(t_demo)` reconstructs the entity set (nearest keyframe ≤ tick, replay deltas forward);
3. flags `Teleported` on all entities **if** this was a seek/backward jump beyond the per-tick threshold;
4. `BuildEntitySet` reads that set; `BroadcastSnapshots` ships it; the recorded **events** for the crossed window
   are re-emitted (see §7).

### GameWorld in replay mode

Boot a normal `GameWorld` (we want the map, services, entity table, and the spectator movement step) but with a
`ReplayMode` flag that makes the match logic inert in `StartFrame`/`EndFrame` (no rounds, voting, rules,
respawn, damage, intermission) while **keeping** the per-client movement step so observers still fly. Recorded
state arrives via injection, not simulation, so nothing fights it. (Alternative considered: a bespoke
`ReplayWorld` implementing `ServerNet`'s surface — rejected as more invasive than a mode flag.)

---

## 7. Events across time control (the genuinely tricky part)

Effects, sounds, and notifications are fire-and-forget — there is no "un-fire." They must be driven off the
**playhead**, not blindly replayed:

- **Forward (any speed > 0):** emit each recorded event whose tick falls in `(t_prev, t_now]`. At fast speed the
  window is wider; that's fine.
- **Backward / paused:** emit nothing (don't re-fire crossed events).
- **On seek (jump):** clear all transient client state — active particles, decals, gibs, shell casings,
  one-shot sounds — then resume forward emission from the new playhead.
- **Looping sounds** (Arc beam, vehicle engines — `SoundWire` loop/stop) need reconstruction after a seek:
  a loop is active at `T` iff its last `start ≤ T` with no `stop` in `(start, T]`. The trailer's **loop-sound
  index** lets `DemoPlayback` compute the active loop set at any `T` and tell the client to (re)start exactly
  those — closing the only event class that isn't naturally stateless.

Client side, `ClientWorld`/`EffectSystem`/sound pools gain a `ClearTransients()` entry the replay host calls on seek.

---

## 8. Spectator camera modes (client-side)

All three modes are **purely client-side** view selection over the same networked entity stream — no server
involvement, so each viewer chooses independently. A `SpectatorCamera` (in `game/client`) holds the mode and
produces the camera pose each frame; `NetGame._Process` uses it instead of the predicted first-person eye when in
replay.

```csharp
enum SpectatorMode { FreeFly, Follow, Director }
```

1. **FreeFly** — the existing observer free-flight. Camera = the predicted observer eye (the viewer's own live
   spectator body). Nothing new beyond confirming the client predicts the `MOVETYPE_FLY` observer path (see §11).
2. **Follow** — pick a recorded player net id; camera tracks that entity's interpolated origin.
   - **First-person:** eye at the target's origin + eye height, oriented by the target's **view angles**.
   - **Chase (3rd-person):** reuse [`FirstPersonView`](../../game/client/FirstPersonView.cs)'s existing chase-cam.
   - Cycle target with attack/jump edges (QC spectate next/prev); a target list comes from the demo header roster.
   - **Fidelity note:** faithful first-person needs the target's *view* pitch, not just body yaw. Record each
     player's view angles explicitly in the demo (a small per-player field) rather than relying on the networked
     body `Angles`.
3. **Director / action cam** — an auto-cam that frames the action:
   - **Subject scoring** from the entity + event streams: recent kill participants, flag/key carriers, clustered
     combatants, high-speed movement. Highest score wins the shot; switch on major events (kills, captures) with a
     minimum dwell time to avoid thrashing.
   - **Shot framing:** orbit / tracking / over-the-shoulder presets with smoothed (critically-damped) moves and
     a short look-ahead, picking angles that keep the subject and the action in frame and roughly collision-aware.
   - Self-contained: consumes the same data the HUD does; no new netcode.

---

## 9. Time-control UI + input

- **`ReplayControlBar`** (in `game/hud`): a scrub bar with a draggable playhead, play/pause, a speed selector
  (−1, 0, 0.25, 0.5, 1, 2, 4), step-frame, jump-to-keyframe, and event markers on the timeline (kills/captures
  from the notification stream). Visible in replay only.
- **Keybinds** (replay-only context): space = pause/play, ←/→ = seek ±5 s, [ / ] = speed down/up, comma/period =
  step frame, F1/F2/F3 = camera mode, mouse-wheel or Tab = cycle follow target.
- Dragging the bar issues a **seek**; the speed selector sets `speed`. For a **local single viewer**, these drive
  `DemoPlayback` directly. For a **shared replay** (multiple viewers, one timeline), time control is a server-side
  admin action over the existing `ClientCommand` channel (`demo_*` commands); camera mode stays per-viewer. Build
  the local case first.

---

## 10. Menu integration & launch path

- **`DialogMediaDemo`** ([dialog](../../game/menu/dialogs/DialogMediaDemo.cs)) already has the Demos tab UI (filter,
  list, Refresh, Play, Timedemo, `cl_autodemo`) wired but inert. Give it the backend: enumerate `demos/*.xgd`
  via the VFS, populate the list, and on **Play** launch a replay.
- **`NetGame.ConfigureReplay(demoPath, vfs, …)`** — a third configuration beside `ConfigureClient` /
  `ConfigureListenServer`: boots a replay-mode `GameWorld` + `DemoPlayback` + `ServerNet` (loopback), self-connects
  a local observer `ClientNet`, reads the **map name from the demo header** so the client renders the right
  worldmodel, and adds the `SpectatorCamera` + `ReplayControlBar`. A `--playdemo <path>` CLI flag mirrors it.
- **`cl_autodemo`** wires `DemoControl` → `DemoRecorder` so real matches record automatically.

---

## 11. File-by-file change list

**New:**
- `src/XonoticGodot.Net/DemoFormat.cs` — header/frame/index records + read/write (pure, testable).
- `src/XonoticGodot.Server/DemoRecorder.cs` — `IDemoSink`; keyframe cadence; file writer.
- `src/XonoticGodot.Server/DemoPlayback.cs` — `IReplayEntitySource`; playhead, `SampleAt`, seek, event windowing,
  loop-sound reconstruction.
- `game/client/SpectatorCamera.cs` — FreeFly / Follow / Director pose generation + target cycling.
- `game/hud/ReplayControlBar.cs` — scrub/time UI + keybind context.

**Touched:**
- `game/net/ServerNet.cs` — add `DemoSink` (record hook) + `ReplaySource` (inject hook in `BuildEntitySet`);
  Teleported-on-seek flagging.
- `game/net/NetGame.cs` — `ConfigureReplay`; replay-mode wiring (camera, control bar, map-from-header); `--playdemo`.
- `src/XonoticGodot.Server/GameWorld.cs` — `ReplayMode` flag (match logic inert, spectator movement kept).
- `src/XonoticGodot.Server/DemoControl.cs` — wire `StartRecording/StopRecording` to `DemoRecorder`.
- `game/client/FirstPersonView.cs` — reuse chase cam for Follow 3rd-person.
- `game/client/ClientWorld.cs` (+ `EffectSystem`, sound pools) — `ClearTransients()` for seek.
- `game/menu/dialogs/DialogMediaDemo.cs` — enumerate + launch.
- (fidelity) the player net-state path — record per-player **view angles** for faithful first-person follow.

---

## 12. Testing

- **Round-trip (headless, `XonoticGodot.Tests`):** build a synthetic entity-set sequence → `DemoRecorder` →
  `DemoFormat` read → `DemoPlayback.SampleAt` per tick reproduces the original sets byte-for-byte (incl. keyframe
  boundaries and deltas).
- **Seek determinism:** `SampleAt(T)` reached by (a) forward play and (b) nearest-keyframe seek yields identical
  state for arbitrary `T`.
- **Loop-sound reconstruction:** after a seek to `T`, the computed active-loop set matches forward play to `T`.
- **Time model:** pause holds entity state while sim time advances; `speed` scales playhead; negative speed
  decreases it; all clamp at `[0, duration]`.
- **Manual / `--playdemo`:** record a `--host` bot match, replay it, exercise all three camera modes + scrub +
  slow-mo + rewind; screenshot via the existing `--screenshot` path.

---

## 13. Phasing

- **Phase 0 — Format + recorder.** `DemoFormat` + `DemoRecorder` + `DemoControl` wiring + round-trip tests. No
  playback yet. *Deliverable:* matches write real `.xgd` files; tests prove the round-trip.
- **Phase 1 — Replay host (forward, free-fly).** `GameWorld.ReplayMode` + `DemoPlayback` + `ServerNet.ReplaySource`
  + `NetGame.ConfigureReplay` + `--playdemo`. *Deliverable:* fly around a recording at 1×.
- **Phase 2 — Time control.** Two-clock model, pause/slow/fast/smooth-rewind/seek, keyframe seek, transient-clear +
  loop-sound reconstruction, `ReplayControlBar`. *Deliverable:* full scrub/slow-mo/rewind.
- **Phase 3 — Camera modes.** Follow (1st-person + chase, target cycle) then the Director auto-cam. *Deliverable:*
  the three modes the user asked for.
- **Phase 4 — Menu + polish.** `DialogMediaDemo` backend, `cl_autodemo`, timeline event markers, shared-replay
  time-control commands (optional).

---

## 14. Risks & open questions

- **Spectator prediction.** FreeFly is responsive only if the client predicts the `MOVETYPE_FLY` observer path.
  Verify the reconciler/`EntityMovementStep` covers it; if not, fall back to server-authoritative + interpolation
  for the camera, or extend prediction. *(Verify in Phase 1.)*
- **Director quality** is iterative — ship a simple scorer first; treat shot polish as tunable, not blocking.
- **Demo size / long matches** — keyframe interval trades file size vs seek latency; expose `demo_keyframe_interval`.
- **Format stability** — gated by `buildParity`; bump `formatVersion` on any layout change. Old demos that don't
  match are rejected with an honest message (no silent misrender).
- **Promote to an ADR?** The two load-bearing decisions — *record server-side full-state* and *replay-as-listen-server
  with direct entity injection* — are arguably ADR-worthy. Capture as an ADR if/when accepted.
```
