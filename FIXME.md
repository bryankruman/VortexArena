# FIXME — Outstanding gameplay bugs

Tracker for reported gameplay defects. Each item carries an `Fnn` label. Update
status as work lands. See also [TODO.md](TODO.md) and
[REBIRTH_FEATURE_COMPLETENESS.md](../REBIRTH_FEATURE_COMPLETENESS.md).

Status legend: **OPEN** · **INVESTIGATING** · **FIX LANDED (unverified)** · **VERIFIED** · **WONTFIX**

Regression coverage for F02/F04 lives in
[tests/XonoticGodot.Tests/FixmeRegressionTests.cs](tests/XonoticGodot.Tests/FixmeRegressionTests.cs).
All 1456 tests pass; the build is clean.

---

## F01 — Enemy / 3rd-person player models do not animate · FIX LANDED (needs in-game feel-test)

Enemy/remote player models rendered but were **frozen in a single pose** while the
bot moved around the map.

**Root cause (two wire bugs in the remote-player animation feed):**
1. The server's player snapshot ([game/net/ServerNet.cs](game/net/ServerNet.cs)
   `BuildEntitySet`) **never networked `Velocity`** for players, so every remote
   read velocity 0.
2. The client render proxy ([game/net/ClientEntityView.cs](game/net/ClientEntityView.cs)
   `DriveEntity`) **never copied the networked `OnGround` flag** onto the proxy
   (`Entity.OnGround` is derived from `EntFlags.OnGround`).

`PlayerModel.Pose` → `LocomotionBlend.SelectLegs(speed2d, onGround, …)` returns the
**Jump** clip whenever `!onGround` — so with `onGround` always false (and speed 0)
every remote was stuck in the jump pose, looking static.

**Fix:**
- Server now networks `Velocity = p.Velocity` in the player `NetEntityState`.
- Client proxy now mirrors the `OnGround` flag into `e.Flags` **and** sets
  `e.DeadState` from the networked `Dead` flag (so corpses pose dead).

Players use the skeletal IQM `models/player/erebus.iqm` path
(`ResolvePlayerModel` → `PlayerModel.Pose`), which is what these inputs drive, so
idle/walk/run/jump/crouch/death now select correctly. **Still wants a windowed
feel-test** (animation can't be seen in a still screenshot).

---

## F02 — Dead player can still move / slides sideways · FIX LANDED (test-verified)

After death the corpse slid and **WASD still drove movement**.

**Root cause:** the authoritative server already gates dead movement
(`GameWorld.OnClientMove` runs `DeadPlayerThink` and returns), but the
**client-prediction carrier** ([game/net/EntityMovementStep.cs](game/net/EntityMovementStep.cs))
and the single-process demo path called `Movement.Move` directly with no dead
gate — so the local predicted body kept sliding under input.

**Fix:**
- Added the QC `IS_DEAD` bail to the shared chokepoint
  [PlayerPhysics.Move](src/XonoticGodot.Common/Physics/PlayerPhysics.cs) —
  `if (player.DeadState != DeadFlag.No) return;` (covers server, prediction, demo).
- The prediction carrier is a distinct entity that never learns it died, so
  [NetGame](game/net/NetGame.cs) now mirrors the authoritative dead state onto the
  carrier each frame (listen server: host `Player`; pure client: networked health
  after first spawn) so the gate actually fires.

Covered by `DeadPlayer_DoesNotMoveUnderForwardInput` /
`AlivePlayer_MovesUnderForwardInput`.

> Known limitation (pre-existing, out of scope): the corpse doesn't fall/ragdoll —
> `DeadPlayerThink` runs no corpse physics, so the body freezes at the death spot
> (client now matches server). A faithful MOVETYPE_TOSS corpse is a follow-up.

---

## F03 — Respawn does not reset camera/view angle · FIX LANDED (listen-server; pure-client follow-up)

On respawn the view wasn't snapped to the spawn point's facing.

**Root cause:** the server set `p.Angles = sp.Angles` on spawn, but (a) it
overwrites `p.Angles` with the client's input view angles on the next live tick,
and (b) the client owns its view angles (prediction) so the spawn angle never
reached `_viewAngles`.

**Fix (QC `fixangle`):**
- [SpawnSystem.PutPlayerInServer](src/XonoticGodot.Common/Gameplay/Player/SpawnSystem.cs)
  now latches the spawn facing in the QC `.fixangle` channel
  (`p.FixAngle = true; p.FixAngleAngles = sp.Angles;`) — the same field
  teleporters use.
- [NetGame](game/net/NetGame.cs) reads `LocalServerPlayer.FixAngle` each frame
  (before sampling input), snaps `_viewAngles` to the latched facing, and clears
  it (one-shot). This also makes server-side (multi-destination) teleports snap
  the view.

Listen-server only (in-process read). A **pure remote client** needs the fixangle
networked in the owner snapshot — noted as a follow-up.

---

## F04 — CTF flags invisible + no flag FX / sounds / announcer · FIX LANDED (needs in-game feel-test)

The CTF state machine worked (scoreboard updated) but the flag entity was a
headless objective with **no model, no audio, no announcer**, and a carried flag
didn't move with its carrier.

**Root cause:** [Ctf.cs](src/XonoticGodot.Common/Gameplay/GameTypes/Ctf.cs) spawned
the flag via `GametypeEntities.SpawnObjective` which **never set `Entity.Model`**
(→ networked invisible), and never called the (already-wired, already-registered)
sound/notification systems.

**Fix:**
- **Model + skin + glow:** `SpawnFlag` now sets the flag's model/skin from the
  `g_ctf_flag_<team>_model` / `_skin` cvars (default `models/ctf/flags.md3`, skin
  0–4 = red/blue/yellow/pink/neutral) + `EF_FULLBRIGHT`. Flag classifies as an
  `Item` → networked + rendered by the existing client item path.
- **Carry-follow:** `Tick` repositions a carried flag behind+above its carrier
  (yaw-rotated `FLAG_CARRY_OFFSET`) each tick so it rides the player.
- **Sounds (global flag voices, QC `_sound(... ATTEN_NONE)`):** `Pickup` →
  `CTF_TAKEN`, `Capture` → `CTF_CAPTURE`, `DropFlag` → `CTF_DROPPED`,
  `ReturnFlag`/`AutoReturnFlag` → `CTF_RETURNED` (all keyed by the flag's home
  team; registered in `SoundsList`).
- **Announcer / centerprint / kill-feed:** the same events now send
  `INFO`/`CENTER` notifications (`CTF_PICKUP_*`, `CTF_CAPTURE_*`, `CTF_LOST_*`,
  `CTF_FLAGRETURN_TIMEOUT_NEUTRAL`) via `NotificationSystem`.

Covered by `SpawnFlag_GivesTheFlagAModel_SoItRenders` /
`CarriedFlag_RidesTheCarrier_OnTick`. Boots cleanly on `courtfun` CTF with no
flag-asset load errors. **Wants a windowed playtest** to confirm the flag visuals
+ audio land in-match.

> Follow-ups (lower priority): colored glow-trail (client effect), the flag-damage
> return path, dropped-flag water float, MD3 per-team skin-color verification.
