# XonoticGodot — Remaining Work / Gap List

> 📋 **Historical snapshot (superseded for task management).** Kept for reference. **Manage active work in
> [`../../TODO.md`](../../TODO.md)** — the consolidated, prioritized tracker the orchestrator assigns from. Don't add new
> TODOs here.

Complete, audited list of what is **missing, incomplete, or built-but-unwired** as of this checkpoint.

**Context:** the *game logic* is comprehensively ported — 0 plain `TODO(port)` remain, all 7 projects build, 15 tests
pass. What remains is concentrated in (1) the **asset pipeline**, (2) **netcode depth**, (3) **client presentation
specifics**, (4) **gameplay edge-completeness**, (5) **server infrastructure**, (6) **engine-fidelity tooling**, and
(7) **project tooling**. Items are tagged by status and rough priority.

**Status:** ☐ not started · ◑ partial · ⊟ built but not wired/active · ✎ data-incomplete
**Priority (to a playable build):** 🔴 blocker · 🟠 important · 🟡 polish/later

---

## 1. Asset pipeline — ✅ DONE (built this session)

The entire asset pipeline is now implemented and building (Godot-free parsers in `XonoticGodot.Formats`, Godot builders
in `game/assets`). 3 real-data tests pass (VFS mounts the tree; 121 `.shader` files → 500+ materials; a real IQM
parses with joints+meshes). All items below are ☑.

| Item | Status | Notes |
|---|---|---|
| **IQM model importer** (skeletal) | ☑ | `Iqm/IqmReader` (pose/frame decode) → `IqmBuilder` (Skeleton3D + skinned ArrayMesh + AnimationLibrary; world-conjugation bone conversion). |
| **DPM model importer** (skeletal, big-endian) | ☑ | `Dpm/DpmReader` (BE, variable bone-weights) → `DpmBuilder` (frame-0 bind, top-4 weights). |
| **Q3 `.shader` → Godot material compiler** | ☑ | `Materials/Q3ShaderParser` (1542 materials from real data) → `ShaderCompiler` (single/multi-stage `next_pass`, `tcMod`/`deformVertexes`→`.gdshader`, `$lightmap`, cull/alpha). |
| **pk3 (zip) virtual filesystem** | ☑ | `Vfs/VirtualFileSystem` (mount pk3/pk3dir, DP search-order precedence, `ResolveImage` extension search incl. `override/` + dds). |
| **Lightmap loading + UV2 + modulation shader** | ☑ | BSP 128² pages → ImageTexture, `LightmapCoord`→UV2, `LightmapShader.gdshader` modulate (+ external `lm_*` fallback). |
| **Bezier patch tessellation** | ☑ | `BezierPatch` (3×3 biquadratic sub-patches, watertight, N=8) wired into `MapLoader.BuildMap`. |
| **Texture loading + channel semantics** | ☑ | Robust uncompressed+RLE `TgaDecoder` (byte-identical to PIL) + png/jpg; `_norm`/`_gloss`/`_glow` companions wired in `ShaderCompiler`. |
| **Model sidecars** (`.framegroups`, `.skin`, `.sounds`) | ☑ | `Sidecars/` parsers; builders consume them (anim clips, material/team remap, tag aliases). |
| **MD3 tag → attachment wiring** | ☑ | `Md3Builder` exposes tags as `Marker3D` sockets + `GetTag(name, frame)`; IQM exposes `GetBoneGlobalRest`. |
| **Sprites** (`.spr`/`.sp2`/`.spr32`), **fonts** | ☑ | `Sprites/SpriteReader` → `SpriteBuilder` (billboards); `FontLoader` (ttf/otf via VFS). |
| **Offline conversion pipeline** | ☑ | `AssetConverter.ConvertAll` → `.tscn` via PackedScene; `AssetLoader` dispatches model loads by magic; `GameDemo` boots VFS + loads a real map/model. |

**Remaining asset polish (minor):** shader-name→diffuse-texture accessor for lightmapped pure-shader surfaces
(albedo currently white×lightmap on those); 8-bit `.spr` Quake-palette decode; runtime verification in Godot.

---

## 2. Networking — ✅ COMPLETE (incl. breadth + a runnable loopback)

The authoritative client/server loop (input → simulate → snapshot → predict/reconcile/interpolate) and ENet
transport exist (`game/net`). All 8 depth gaps below are done: Godot-free cores in `XonoticGodot.Net` (+ **47 tests**:
delta/movevars/weapon-field, antilag + hitscan-bracket, master protocol+loopback, session auth), wired into the
`game/net` drivers + `WeaponFiring`. **The whole stack now runs in-process** via `NetLoopback`
(`Main --net-loopback`): a real `ServerNet`+bot and `ClientNet` over localhost ENet — the ECDSA auth handshake,
delta snapshots, movevar replication, and the full client render path (entities + held-weapon view-entities +
nameplates + the ent_cs radar) all exercised end-to-end, headless, 0 errors (the client sees the bot as a
networked remote player). Full build + headless run clean.

**Breadth filled this pass:** the server entity set now networks projectiles **+ items/gibs/monsters/generic**
(not just projectiles); a small stable per-player net id (humans AND bots, decoupled from the raw ENet peer id)
so bots are networked and the id space can't collide; `ClientEntityView` (proxy-entity → `ClientWorld`,
frame-driven animation) + `ViewEntityRenderer` (held-weapon wepent attachment) + nameplates (ent_cs) in
`ClientWorld` + `RadarPanel` (ent_cs radar); and `ServerBrowser` now does a real internet master-server query
(`MasterServerLink`/`MasterServerProtocol`, stock Xonotic masters) with the DP-correct 4×0xFF wire format.

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Lag compensation / antilag** (server rewind) | ☑ | 🟠 | `AntilagBuffer` (per-entity position history + lerp `SampleAt`) + `LagCompensation.ComputeTakebackTime` (ping+interp, 0.4s cap). `ServerNet` records each player per tick + installs an ambient `LagComp` provider (`Begin`/`End` = rewind others to the shooter's view-time / restore; RTT from the snapshot-ack gap). **`WeaponFiring.FireBullet`/`FireRailgunBullet` now bracket their trace with it**, so every hitscan weapon is lag-compensated (tested end-to-end). Bots/clients no-op (no remote latency). |
| **Snapshot delta-compression** | ☑ | 🟠 | `NetEntityState` + `EntityStateCodec` (16-bit `EntityField` change-mask = SendFlags) + `Server/ClientSnapshotHistory` (Quake3-style ack/baseline rings, 16-bit wrap-safe seq). `ServerNet` now sends only spawned/changed/removed entities vs the client's last-acked snapshot (was full every tick); client delta-decodes + acks via the input frame. |
| **CSQC networked-entity model** (`lib/csqcmodel`) | ☑ | 🟠 | Unified property table (`NetEntityState`: kind/model/frame/skin/effects/colormap/health/flags/owner/weapon). **Players, projectiles, items, gibs, monsters/generic all networked** (server scans the engine entity table, classifies by move-type/classname/solid, skips inline brush models). Render path: `ClientEntityView` drives proxy entities into `ClientWorld` with **frame-driven animation** (CSQCMODEL_AUTOUPDATE); `ViewEntityRenderer` attaches the **held-weapon view-entity** (wepent); `ClientWorld` draws **nameplates** + `RadarPanel` the **ent_cs radar**. Exercised live by `NetLoopback`. |
| **Per-snapshot movevar replication** | ☑ | 🟡 | `MoveVarsBlock` (the exact `sv_*` set `MovementParameters.FromCvars` reads), sent owner-only **only when the physics hash changes**, stamped into the client cvar store so prediction matches a mid-match physics/mutator change. |
| **Per-entity teleport bit** | ☑ | 🟡 | `NetEntityFlags.Teleported`; server sets it on a >150u/tick origin jump (teleport/respawn), client passes it to the interp buffer (which already cancels the lerp). |
| **Master server / real server browser** | ☑ | 🟡 | `MasterServerProtocol` (DP OOB `heartbeat`/`getservers`/`getserversResponse`/`getinfo`/`infoResponse` + infostrings) + `MasterServerLink` (Godot-free UDP socket, send/receive/dispatch). Loopback-tested. **`ServerNet.EnableMasterServer(masters, port)`** resolves the masters, heartbeats every 180 s in `Tick`, and answers `getinfo` probes with the server's infostring (hostname/map/gametype/clients/…). Remaining: the client browser-list UI consumes the parsed list. |
| **Reconnect / fragmentation / rate tuning** | ☑ | 🟡 | `ClientNet.Reconnect()` (rebuild transport + reset handshake/baseline/remotes) + `ConnectionLost` + `InputSendInterval` rate-gating (redundancy widens to cover batched ticks). Fragmentation is ENet's. |
| **Crypto / player identity** | ☑ | 🟡 | `SessionAuth` (ECDSA P-256 challenge-response; the SPKI public-key fingerprint is the stable anonymous id) replacing `d0_blind_id` (ADR-0011). Wired into a 4-step handshake: request+pubkey → server challenge → client signs → verify+accept. |

---

## 3. Client presentation — ✅ DONE (built this session) 🟡→✅

`EffectSystem` (EFFECT_*→particles), `ProjectileRenderer`, `ModelAnimator`, `ViewModel`, HUD, Menu existed; the
specific bits the generic systems didn't cover (the **27 `TODO(port,client)`** markers — all in the vehicle
code — plus the projectile/anim/notification/beam/minigame gaps) are now filled. All items below are ☑. The
full game host + 7 libraries build; 98 tests pass.

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Vehicle visuals** | ☑ | 🟡 | `VehicleVisuals` (driver) + `VehicleCatalog` (per-type specs ported from `vr_spawn`/`*_frame`/`vr_death`): Raptor counter-rotating rotor spinners (engine_left/right), Spiderbot minigun barrel spin (spool-up while firing) + frame-driven legs, Racer scale-0.5 hack, idle/move/boost engine-sound crossfade, death gib sub-entities (burst+fall+fade) + death sound/explosion, Bumblebee heal-beam, muzzle flash on the firing edge. Wired into `ClientWorld` (vehicle entities → driver, fed each frame from the networked entity; gibs on removal). Sub-models mount at model tags or catalog fallback offsets, so every mechanism shows even without the `.dpm` art. |
| **CSQCVehicleSetup** (vehicle 1st-person viewport/HUD) | ☑ | 🟡 | `VehicleHud` panel (port of `Vehicles_drawHUD`): mirrors `VEHICLESTAT_*` (health/shield/energy/ammo1/reload1/ammo2/reload2) onto the bottom-center vehicle frame — silhouette tinted by health, weapon overlays by ammo, clipped health/shield/ammo bars, blinking low-stat icons. `ConfigureForVehicle`/`Exit` = the `TE_CSQC_VEHICLESETUP` dispatch; `SetAuxiliaryXhair` draws lock-on crosshairs projected from 3D (Racer rocket lock / Bumblebee gunner). `InVehicle` is what the host reads for the `SVC_SETVIEWPORT`/`SETVIEWANGLES` camera override. Wired into `Hud`. |
| **Weapon view-entity networking** | ☑ | 🟠 | The held weapon networked as the `Weapon` field on the player CSQCModel state (`Entity.ActiveWeaponId`); `ViewEntityRenderer` hangs the weapon's world model (`v_*.md3`) off the owner's `tag_weapon` hand tag (third-person wepent) — tracks the animated hand, rebuilds on weapon swap, hides when holstered. The local player still sees their first-person `ViewModel`. Driven by `ClientEntityView`. |
| **`te_csqc_lightningarc` beams** | ☑ | 🟡 | `BeamRenderer` (faithful port of `cl_effects_lightningarc`): the jagged bolt split into ≤16 drifting segments (drift 0.45→0.1, seglength 64), drawn as a self-illuminated additive cross-ribbon that flickers + fades. `ClientWorld.OnArc`/`OnBeam` + `EffectSystem` routes beam-class effects to it. Server emits it end-to-end: `EffectEmitter.TeCsqcLightningArc` wired into the Golem `ChainedZaps` + Tesla `Toast`. |
| **CSQC model animation networking** | ☑ | 🟡 | `ClientEntityView` (the `CSQC_Ent_Update` bridge) drives proxy entities from `ClientNet`'s remote stream into `ClientWorld` with **frame-driven** animation: `ModelAnimator` follows the networked `Entity.Frame` (CSQCMODEL_AUTOUPDATE) for remote players/monsters, and `ClientWorld` skips the local movement-clip heuristic for those. |
| **Per-projectile CSQC trails** | ☑ | 🟡 | `ProjectileCatalog` (the `ENT_CLIENT_PROJECTILE` HANDLE table): per-`PROJECTILE_*` trail effect + color/density/additivity, model scale (rocket ×2, hagar 0.75, porto ×4, golem 2.5…), spin (rocket z-720 roll, grenade-bouncing sideways tumble, hookbomb pitch), looping fly sound, body family + light. `ProjectileRenderer` resolves the type from the entity and builds the tuned trail/scale/spin/sound. |
| **HUD art assets** | ☑ | 🟡 | `TextureCache.VfsResolver` bridges the HUD to the mounted game data (host-wired to `AssetLoader.LoadTexture`), so weapon icons, numbered crosshairs and kill-notify icons draw the REAL Xonotic art from the pk3 tree (skin-aware `gfx/hud/<skin>/…`, NetName→icon-name map for legacy-named weapons: mortar→grenadelauncher, vortex→nex, …), keeping the colored-box/vector fallbacks. `WeaponHud`/`CrosshairPanel`/`NotifyPanel` updated. |
| **Minigame rendering + networking** | ☑ | 🟡 | `MinigameRenderer` (CSQC board draw): the centered board square + per-tile team piece + win-glow + status line, **the Pong court (paddles + balls + per-team scores) and the Snake body + food** drawn from the session's bespoke `Extra` state, VFS art (`minigames/<game>/board`+`piece<team>`) with colored-token fallbacks, click-a-tile → move (the client→server `minigame_cmd`). `MinigameNetState` is the MSLE read/write — encode/decode a `MinigameSession` snapshot (game id, turn/winner, grid cells, player roster, **+ the Pong paddles/balls/scores**) for the server→client sync. Wired into `Hud`. |
| **Notification/centerprint → net wiring** | ☑ | 🟡 | `HudNotifications` routes a decoded notification to the HUD: MSG_CENTER → `CenterPrintPanel` (with cpid + the live `^COUNT`), MSG_INFO → `NotifyPanel` (kill-feed obituary by s1/s2 when an icon is present, else a plain line), MSG_ANNCE → the announcer voice (played from the mounted content `sound/announcer/<voice>/<snd>.ogg` via `HudNotifications.AudioLoader` → `AssetLoader.LoadSound`, with the `res://` convention as fallback). Decoupled from the net type so either the real `ClientNet.NotificationReceived` path or the in-process sink (`InstallLocalSink`, like the effect render-sink) feeds it. Wired into `GameDemo` (HUD + local sink). |

**Presentation polish — also done this pass:** vehicle **Firing/Boost** now ride the entity's networked `Effects` bitfield (`VehicleEffects.Firing/Boosting`, high bits above the engine EF_* range, set by the Spiderbot/Racer server frame, read by `VehicleVisuals` → barrel spin + muzzle + boost sound); the Bumblebee **heal-beam (BRG_*)** is emitted end-to-end (`EffectEmitter.TeHealBeam` from the gun3 ray in `FireRay` → the client draws a straight green beam, with `EffectSystem` now routing electric effects to the jagged arc and laser/rail/heal effects to a straight cylinder); the **aux-crosshair lock-color** API (`VehicleHud.SetAuxiliaryXhairLock`, red→yellow→green by lock strength) is in place. **Runtime-verified in Godot 4.6.3 headless:** the host boots, loads `stormkeep.bsp` (5234 brushes) + 2439 shaders + 4593 cvars, and brings up the HUD / notification / client / effect / projectile / vehicle / beam / minigame systems with **zero exceptions** (the port's first real run — see §7).

**Remaining (genuine net-protocol follow-up, not polish):** the aux-crosshair lock *positions* + the `VehicleVisuals.HealTarget` feed need a vehicle-specific stat channel (vehicles currently network as generic entities); the Snake board isn't networked (single-player). DDS texture decode is a §1 asset-pipeline gap (warnings at load).

---

## 4. Gameplay completeness — ✅ DONE (built this session)

All 14 edge items below are now ☑, building (7 projects) + **185 tests pass** + the host boots Godot-headless with **0 errors/0 warnings**. The notification table is complete (all 918 QC names), the scores table is a real networked per-column registry with the `.frags`-as-status split restored, and every gametype edge (race records, CTF/Invasion variants, Survival/LMS/KeyHunt specifics), the spectator/no-FF rules, warpzones (+ Porto), and bot nav (auto-waypointing + objective roles) are ported.

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Notifications table** | ☑ | 🟠 | **DONE.** The full QC `all.inc` table — **all 918 unique typed names** registered (943 entries; INFO/CENTER/ANNCE/MULTI/CHOICE) incl. every MULTITEAM/CHOICE/death-bundle/race/onslaught/etc. family (`NotificationsList.cs`). |
| **Status effects: `stunned`, `spawnshield`, `inferno`** | ☑ | 🟡 | **DONE.** `StatusEffectsCatalog` now mirrors the whole QC `StatusEffects` registry: core (frozen/burning/spawnshield/stunned/superweapon/webbed), powerups (strength/shield/speed/invisibility — now resolve in ItemPickupRules), and the exact 13 buffs incl. `inferno`. |
| **Real scores table** | ☑ | 🟠 | **DONE.** New shared `GameScores` (SP_* `ScoreField` registry + SFL_* flags + sort/winning-condition) stores every per-player column (kills/deaths/accuracy/spree…) + team totals; `Player.ScoreFrags` re-backs onto SP_SCORE so `Entity.Frags` reverts to the QC **status sentinel reset on respawn**; networked via `ScoreboardBlock` (ServerNet/ClientNet snapshot, change-gated). |
| **Random-start-weapon ammo + superweapon timers** | ☑ | 🟡 | **DONE.** `AmmoType` lifted onto the `Weapon` base (+`IsSuperWeapon`) → central `Weapons.AmmoTypeOf/IsSuperWeapon/Superweapons`. Random-start now grants `g_random_start_*` ammo per weapon; superweapons arm the `Superweapon` status effect for `g_balance_superweapons_time` and are stripped on expiry (`PlayerFrameLogic.SuperweaponTimeout`). |
| **Race/CTS record persistence** | ☑ | 🟡 | **DONE.** `RaceRecords` DB (QC ServerProgsDB: read/write/setTime/pos top-99 ranking, export/import); Race qualifying mode (rank by fastest lap) + Race/CTS **finish kill-delay re-teleport** (`OnFinishRetract` → `Clients.Spawn`); `Player.PersistentId` keys records. |
| **CTF one-flag + 3/4-team variants** | ☑ | 🟡 | **DONE.** One-flag play (neutral flag pickup + capture at own/enemy base under `g_ctf_oneflag_reverse`, team flags non-pickable); 3/4-team verified end-to-end via the team-keyed flag/cap tables. |
| **Invasion STAGE/HUNT variants + per-wave monster lists** | ☑ | 🟡 | **DONE.** `g_invasion_type` ROUND/HUNT/STAGE win conditions, per-wave `spawnmob` monster lists (`AddWave`/`GetWaveMonsters`), `invasion_roundend` triggers, monster-skill scaling; host now drives `inv.Tick()`. |
| **Survival banked scoring / validkills** | ☑ | 🟡 | **DONE.** Banked validkills (kills bank, scored to the side at round end via `UpdateScores` → SP_SCORE + SURV_SURVIVALS/HUNTS) + the AddPlayerScore **role-anonymization** hook (hides kills/deaths so hunters aren't outed). |
| **LMS extra-life pickups + leader waypoints** | ☑ | 🟡 | **DONE.** `GiveExtraLife`/`SpawnExtraLife` (g_lms_extra_lives), `UpdateLeaders` (max-lives leaders gated by lead-diff + minpercent → `IsLeader`, host drives it). |
| **KeyHunt key model/animation/networking** | ☑ | 🟡 | **DONE.** The key entity gets a model + team colormap + glow + per-team netname + spin (`SetKeyVisual`), so it networks/renders; carried keys stop spinning (`AttachToCarrier` clears AVelocity). |
| **Skill-weighted team balance + bot autobalance** | ☑ | 🟡 | **DONE.** The inverse-variance weighting + `AutoBalanceBots` (already in `Teamplay`) are now **wired + active**: `SkillProvider` reads `Player.BotSkill`; `GameWorld.BalanceTeamsTick` moves the lowest-scoring bot off the largest team on a timer. |
| **Spectator rules** (spectate-enemies, no-FF) | ☑ | 🟡 | **DONE.** `SpectatorRules` (teammates-only / anyone / blocked modes + Next/Prev cycling, QC SpectateSet); ClanArena **no-friendly-fire** filter on the `DamageCalculate` hook (zero self/team/fall damage, no mirror). |
| **Warpzones** (`lib/warpzone` seamless portals) | ☑ | 🟠 | **DONE.** `WarpzoneTransform` (the seamless portal rotation+shift, momentum-preserving) + `WarpzoneManager` (link pairs, teleport-on-cross) + **Porto** portal spawn wired (`Porto.PortalSpawner` → `PlacePortoPortal`, two-way pair). **Map-entity spawn now wired too:** the `trigger_warpzone` spawnfunc registers a brush; `InitMapZones` (after `SpawnMapEntities`, the QC `WarpZone_StartFrame`) auto-derives each plane from the brush geometry via `getsurface*` (§6), resolves an optional `trigger_warpzone_position`/`killtarget` orientation, and links the pairs (incl. QC's two-way `enemy` link); teleport fires the zone's targets. Bridged static-spawnfunc→instance-manager via `WarpzoneSpawns.Sink` (mirrors `Porto.PortalSpawner`). 3 spawn tests. |
| **Bot nav refinements** | ☑ | 🟡 | **DONE.** Waypoint **auto-generation** from map entities (items/spawns/teleporters/jumppads → graph + auto-link; `GenerateFromEntities`/`ForMap`); per-gametype objective roles extended with **Nexball + Assault** (CTF/KH/Dom/Ons/KA already present); jump/teleport/ladder/crouch traversal + bunnyhop already in `BotNavigation`. |

---

## 5. Server infrastructure — ✅ DONE (built this session)

All §5 items are now ported (Godot-free, in `XonoticGodot.Server`) and wired into `GameWorld`'s lifecycle.
**36 new tests pass; full solution builds clean** (0 errors). The genuinely engine-side pieces (the HTTP
stats upload, the byte-level demo recorder, the networked vote/mapvote HUD entities) are delegated to the host
through callbacks — exactly the QC split — not left as gaps.

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Config (`.cfg`) parsing** | ☑ | 🟠 | **DONE.** Darkplaces-faithful `ConfigInterpreter` (`XonoticGodot.Common/Config/`): comments, quotes/escapes, `;` separators, `set`/`seta`, bare assignment + command denylist, `exec` recursion + cycle guard, `alias`/`unalias` + invocation, `$cvar`/`$args`/`$$`/`${* asis}` expansion. `ConfigLoader.LoadServerConfig` execs the real `xonotic-server.cfg` chain. Wired into `GameWorld.Boot` (`ConfigReader` hook, after `RegisterDefaults`) + `GameDemo` (VFS reader). **Runtime-verified in Godot: 4593 cvars from 16 cfg files, 0 missing, 0 errors.** 18 tests (incl. 4 on real data). Lights up the ~461 live cvar reads with authentic balance/physics/gametype/mutator/monster values. **Weapons wired too:** all 19 `Weapon.Configure()` read `g_balance_*` via `Bal()`/`BalBool()`/`BalInt()` (stock fallback), invoked at registration + via `Weapons.ConfigureAll()` after the load — fixed a latent bug where `Configure()` was never called (weapons fired zero-balance). |
| **Full command set** | ☑ | 🟡 | **DONE.** `Commands` now registers the admin/gameplay/client console set (QC `sv_cmd`/`cmd`/`common`/`generic`): match flow (`allready`/`resetmatch`/`gametype`/`cointoss`/`nextmap`/`reduce`/`extendmatchtime`), teamplay admin (`lockteams`/`unlockteams`/`shuffleteams`/`moveplayer`/`allspec`/`nospectators`), per-player (`ready`/`join`/`spectate`/`selectteam`/`kill`), `timeout`/`timein` (`TimeoutController`: lead→pause→resume via `IsPaused`, per-player allowance, the guard chain), bots (`setbots`/`removebots`), bans (below), cheats (below), `warp`, `settemp`/`settemp_restore` (`SettempCvars`, captures-once + restore), and introspection (`teamstatus`/`who`/`time`/`info`/`cvar_changes`). |
| **Anticheat / cheats / bans** (`anticheat.qc`, `cheats.qc`, `ipban.qc`) | ☑ | 🟡 | **DONE.** `Bans` (ipban + banning + the chatban/playban/voteban prefix lists): IPv4/IPv6 mask derivation, `Insert`/`IsClientBanned`/`Delete`/`KickBanClient`/`Enforce`, `g_banned_list_idmode` (IP bans only catch anonymous), crypto-id-ban-wins, version-1 cvar persistence; enforced on connect. `Cheats` (`sv_cheats` snapshot gating + per-player/total cheat count): `god`/`notarget`/`noclip`/`fly`/`give`/`usetarget`/`killtarget` + the give-all impulse. `AntiCheat` (faithful `MEAN` power-mean detectors): speedhack ×2, strafebot movement/aim, div0-evade, idle snap-aim + snap-back; per-tick `Physics`, `StartFrame`/`EndFrame`, fix-angle suppression, `:anticheat:` report. |
| **Vote system** | ☑ | 🟡 | **DONE.** `VoteController` rewritten faithful to `vote.qc`: the full sub-command set (`call`/`yes`/`no`/`abstain`/`stop`/`status`/`master`/`help`), the parse/whitelist (`CheckNasty`/`CheckInList` with `map`/`chmap`→`gotomap`, `restart`→`defer 1 restart`, kick-by-name), master `login`/`do` + call-for-master, the overall + of-voted majority math, spectator/voteban gates, spam throttle (`sv_vote_wait`/`_stop`). Wired into `Commands.vote`; a pass runs the command through the bus. |
| **Player stats / accuracy / gamelog upload** | ☑ | 🟡 | **DONE.** `GameLog` (event log): the two sinks (`sv_eventlog_console` / `sv_eventlog_files` with the counter-named, `:logversion:3`-headed file), the `:gamestart:`/`:gameinfo:`/`:connect:`/`:join:`/`:part:`/`:kill:`/`:team:`/`:gameover:` lines, IPv6 delimiter. `PlayerStats` (GameReport): the per-player/per-team event accumulator keyed by the exact XonStat event-id strings, crypto-id/`bot#`/fallback identity, accuracy + anticheat + score feeds, warmup-discard, and the **format-version-9 report serializer**. Wired into the connect/spawn/disconnect + NextLevel lifecycle. The HTTP upload itself is the engine's job (the report string is handed back); `DelayMapVote` clears synchronously so the map vote never hangs. |
| **Campaign** (single-player progression) | ☑ | 🟡 | **DONE.** `Campaign` (faithful to `campaign.qc` + `campaign_file.qc`): the quoted-CSV level parser (comment/blank lines transparent to the index, the 9-column layout, `default`/empty/value limit semantics), `PreInit` (gametype + bot count/skill + mutator settemps + the permanent `sv_public`/`pausable`, baseskill = `max(0, g_campaign_skill + level)`), `PostInit` (frag/time limits), the 5-branch win/lose decision (sole-winner rule), `campaign.cfg` progress persistence (frontier-level + cheat-free gate), and the advance/replay/`warp` transition. Wired into `GameWorld.Boot` (PreInit drives the gametype; PostInit after spawn) + the intermission flow. |
| **Demo recording/playback** | ☑ | 🟡 | **DONE.** `DemoControl` — the server-side control surface for a feature that is engine-side in Xonotic too (no QC `record`/`stop`): match-boundary start/stop gated by `sv_autodemo`, the per-client recording decision (`sv_autodemo_perclient` 0/1/2, never bots), mid-match connect/disconnect handling, and the keep/discard preservation (the only real QC glue). The actual byte recording is delegated to the host via `StartRecording`/`StopRecording`/`Start/StopClientRecording` hooks (a real engine host wires its demo writer). |
| **Map voting UI/flow completeness** | ☑ | 🟡 | **DONE.** `MapRotation` (the maplist machinery): `g_maplist` parse (optional shuffle), the rotation cursor (`g_maplist_index`), `GetNextMap` (random→iterate→repeat), recent-map exclusion (`g_maplist_mostrecent`+count), `Map_Check` passes, ballot build. The **end-of-match flow** is wired in `GameWorld`: `NextLevel` (winners + event log + stats report + campaign decision + demo stop) → `DoNextMapOverride` (campaign / queued-nextmap / samelevel) → a map vote (`MapVoting`, when votable + players) or a silent rotation → `Map_Goto` (mark-recent + the host's changelevel). Exercised end-to-end by the GameWorld rotation test. The networked ballot UI stays in the client/HUD layer. |

---

## 6. Engine fidelity & tooling — ✅ DONE (built this session)

All §6 items are now ported, building (full solution, 0 errors/0 warnings) + **257 tests pass** (serialized,
flake-free). The golden-trace work A/B'd the movement port against an independent C transcription of the
*preprocessed* Xonotic QuakeC the engine runs and **caught + fixed two real movement-fidelity bugs**
(`nogravityonground`, an over-eager on-ground re-detection that broke jumps/bunnyhopping); a bonus fix disabled
xUnit parallelism to kill the documented global-state test flake.

**The two engine-tooling consumers are now also wired end-to-end** (the items flagged "out of scope" when §6
landed): (1) the **`trigger_warpzone` map-entity spawnfunc** — a brush in the entity lump auto-orients from its
geometry via `getsurface*` and the deferred `WarpzoneManager.InitMapZones` derives each plane + links the pairs
(+ `trigger_warpzone_position` orientation override, killtarget aiment, QC two-way link, fire-targets-on-teleport;
3 tests). (2) **`PlayerSkeleton` → Godot `Skeleton3D`** — a model-info sidecar parser + a locomotion-blend
synthesizer (11 tests) feed a new `PlayerModel` component that runs the CPU upper/lower split + view-pitch aim
and pushes the conjugated bone poses onto the skinned `Skeleton3D`; verified headless on the real 60-bone
`erebus.iqm` (split bone `spine2`, 4 aim bones, fixbone — posing idle→run+aim moves 14/60 bones on the live
skeleton). See the §3/§4 notes below.

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Golden-trace corpus from Darkplaces** | ☑ | 🔴 | **DONE.** `tools/movement-ref/movement_ref.c` — an INDEPENDENT C reference transcribed line-for-line from the *preprocessed* QuakeC the engine compiled (`.tmp/server.txt`: `sys_phys_simulate` + `PM_Accelerate`/aircontrol/jump + `_Movetype_FlyMove`/walk), stock `physicsX.cfg` cvars, single precision. Compiled in WSL (gcc-12), emits 10 per-tick golden JSON fixtures (`tests/XonoticGodot.Tests/golden/`) for ground accel/friction, jump arc, strafe-jump, bunnyhop, CPM air-control, free-fall, ramp, stairs, swim. The brush-vs-box trace is shared verbatim with the C# harness so collision is identical and only the physics math is under test. README documents regeneration. |
| **Movement-parity tests** | ☑ | 🟠 | **DONE.** `MovementParityTests` replays each fixture's exact input through the ported `PlayerPhysics` and asserts per-tick origin/velocity match — **all 10 at ~0 error** (most exactly 0, the rest sub-0.001 qu transcendental ULP). Surfaced the two port bugs above (fixed): `sv_gameplayfix_nogravityonground` (grounded velocity.z must stay 0) and `DetermineOnGround` re-tracing the floor after `PlayerJump` cleared the flag (forced the ground branch on the jump tick → broke jumps/bhop). Covers strafe-jump/bunnyhop/ramp/stair/water vs the DP-faithful reference. |
| **PVS / `checkpvs`** (BSP visibility) | ☑ | 🟠 | **DONE.** Parsed Nodes/Leafs/Vis lumps (`BspData.Nodes/Leafs/Vis`); `XonoticGodot.Formats.Bsp.BspPvs` does point→leaf tree descent + cluster-bitset lookup (`IsInPvs`, conservative superset, unvised→all-visible). Added `ITraceService.CheckPvs(from,to)` → `TraceService.Pvs` (set from `new BspPvs(bsp)`), exposed via `EngineServices.Pvs`, wired in `GameDemo` + `GameWorld.Pvs`. Consumer: bot enemy-detection LOS now PVS-pre-filters before the traceline (`BotBrain`). 6 tests (synthetic 2-cluster both-directions + facade + real `_init.bsp` descent — _init is unvised so the bitset path is synthetic-only). |
| **`getsurface*` BSP/model mesh queries** | ☑ | 🟠 | **DONE.** `ISurfaceService` facade (all 9 builtins #434-#439/#486/#628-#629 + `SurfaceAttribute` SPA_* codes) → `SurfaceService` (entity-transformed world-space points/normals/tris/texture, nearpoint, clipped-point, per-vertex attributes); `BspSurfaceBuilder` builds one `ModelSurface` per BSP face grouped by inline `"*N"` model (+ patch grid triangulation), attached to the `ModelService` defs. Added `vectoangles2`/`fixedvectoangles2` to `QMath`. **Consumer wired: the warpzone brush→plane auto-derivation** (`WarpzoneManager.DerivePlaneFromBrush`/`SpawnFromBrush`, port of `WarpZone_InitStep_UpdateTransform` — area-weighted triangle-normal/centroid average), so a planar `trigger_warpzone` orients itself. Wired into `GameDemo` + `GameWorld.MapBsp`. 7 tests incl. real shipped-map surfaces + the warpzone plane. |
| **SOLID_BSP collision real brushes** | ☑ | 🟠 | **DONE.** Parsed the BSP `Models` lump (`BspData.Models`); new Godot-free `XonoticGodot.Engine.Collision.BspCollisionBuilder` splits worldspawn (`Models[0]`) into the static `CollisionWorld` and each `"*N"` inline model into a `Submodel` (real brushes), registered on the `ModelService` so `setmodel("*N")` resolves real bounds and the SOLID_BSP trace clips moving door/plat brushes (no more AABB fallback). Fixed a latent bug: all brushes were dumped into the static world, so doors were baked-in walls that never moved. Brush-building moved from the Godot host (`MapLoader`) to the engine (now also lets the **headless server build collision** — `Engine`→`Assets` ref added). Wired into `GameDemo` + `GameWorld.BrushModels`. 4 tests incl. real `_init.bsp` + synthetic 2-box separation/registration. |
| **Skeletal `skel_*` CPU manipulation** | ☑ | 🟡 | **DONE.** `BoneMatrix` (the DP `matrix4x4_t` equivalent: FromVectors/ToVectors with the `v_right = -left` convention, Concat/Interpolate/Accumulate/Normalize3/FromTRS/orthonormal-inverse); `SkeletonManager` = the full `skel_*` builtin set (create/build/get_numbones/bonename/boneparent/find_bone/get_bonerel/get_boneabs/set_bone/mul_bone(s)/copybones/delete) over an `ISkeletalModel`; `IqmSkeletalModel` adapter (bind hierarchy + per-frame poses). `PlayerSkeleton` ports `player_skeleton.qc`: the upper/lower-body split (torso plays frames 1+3, legs 2+4), `fixbone` re-anchoring, and the view-pitch AIM bones (spine/head bend). 6 tests incl. the split routing, the aim bend, and a real shipped-IQM skeleton build. |
| **Cross-architecture determinism validation** | ☑ | 🟡 | **DONE.** `DeterminismHash` (order-sensitive FNV-1a over exact IEEE-754 bits) + `DeterminismTests` (ADR-0010): same-run bit-reproducibility of a canonical movement+RNG+math trace; pinned x64 trace/PRNG/quake-math checksums as the **cross-arch detector**; a ULP-perturbation→prediction-window-drift **envelope** check (ground 4e-6 qu / air 4e-5 qu vs a 2 qu envelope — proves the smoothing absorbs cross-arch float divergence); and a forbidden-API source guard (no wall-clock/Random/FMA in the sim). New `planning/process/determinism.md` documents the numeric contract + the x64↔ARM validation procedure; ADR-0010 updated. |

---

## 7. Project tooling & process 🟡

| Item | Status | Pri | Notes |
|---|---|---|---|
| **Source generator as active registry path** | ⊟ | 🟡 | `RegistryGenerator` is built but **not referenced/active**; registration uses the runtime reflection bootstrap. Wire it into `Common` to make it compile-time (ADR-0003). |
| **Runtime bring-up in Godot** | ◑ | 🔴 | **It runs.** Booted headless on the installed **Godot 4.6.3-mono** (`--headless --quit-after`): mounts the `XonoticGodot.Assets` VFS, loads `stormkeep.bsp` (5234 collision brushes) + 2439 shaders + 4593 cvars, spawns the player + a real IQM model, and brings up the full client/HUD/effect/projectile/vehicle/beam/minigame/notification stack with **zero exceptions**. Remaining for full bring-up: an on-screen interactive session (windowed), DDS texture decode (§1), and walking/fighting a bot locally. |
| **CI pipeline** | ☐ | 🟡 | No automated build/test on commit. |
| **Mechanical-assist QC→C# transpiler** | ☐ | 🟡 | Plan mentioned it; never built (port was agent-driven). Useful only if more QC remains. |
| **Localization (i18n) catalogs** | ☐ | 🟡 | `_("…")` strings carried as literals; gettext catalogs not wired. |
| **Performance pass** (GC discipline, draw batching, pooling) | ☐ | 🟡 | No profiling done; hot-path alloc audit pending. |
| **Web/mobile export** | ☐ | 🟡 | C#→WASM unsupported (ADR-0012); desktop + dedicated server only. |
| **Packaging / installers / dedicated-server distribution** | ☐ | 🟡 | |

---

## Recommended order to a first *playable* build

1. **pk3 VFS** + **texture/material wiring** + **Q3 `.shader` compiler** → maps render correctly. (§1) 🔴
2. **IQM importer** (+ `.framegroups`/`.skin`) → animated player/weapon models. (§1) 🔴
3. ~~**Golden-trace harness** → lock movement fidelity. (§6)~~ ✅ **DONE** — an independent C transcription of the preprocessed QuakeC generates 10 per-tick golden traces; `MovementParityTests` A/B's the port against them at ~0 error and **caught + fixed two real fidelity bugs** (`nogravityonground`, the on-ground re-detection that broke jumps/bhop). All of §6 (golden traces, movement parity, getsurface*, skel_*, cross-arch determinism) is now done.
4. ~~**Config `.cfg` parsing** → real balance/cvar values instead of hardcoded. (§5)~~ ✅ **DONE** — `ConfigInterpreter` loads the real `xonotic-server.cfg` chain (4593 cvars) at boot; the ~461 live cvar reads now use authentic values. **Weapons too:** all 19 `Weapon.Configure()` now read `g_balance_*` via `Bal()`/`BalBool()`/`BalInt()` (stock value as fallback), called at registration + re-run by `Weapons.ConfigureAll()` after the cfg load — so alternate balance sets (XPM/overkill/instagib) take effect. Also fixed a latent bug: `Configure()` was never called, so weapons fired with zero damage/speed.
5. **Runtime bring-up**: install Godot, load a map, walk + fight a bot locally. (§7) 🔴
6. **Snapshot delta-compression + weapon view-entities + antilag** → competitive online. (§2) 🟠
7. Fan out the §3/§4 long tail (vehicle visuals, notification table, warpzones, gametype edges). ~~§5 admin/vote/stats~~ ✅ **DONE** — the full command set, bans/cheats/anticheat, the vote system, player-stats/gamelog, campaign, demo control, and the end-of-match map vote/rotation are all ported and tested.

> Everything in §1 and §7 (runtime bring-up) gates *seeing the game work*. §6 (engine fidelity & tooling) is now
> complete — the movement port is locked to a Darkplaces-faithful golden corpus, and the determinism contract is
> validated. The gameplay-logic port itself (weapons/damage/gametypes/movement/AI) is done and building; the
> remaining gaps are the asset, presentation, and netcode-depth layers around it.
