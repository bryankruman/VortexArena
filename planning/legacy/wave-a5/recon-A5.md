# Wave A5 — Parity Recon Synthesis

## Overview

Wave A5 bundles nine port-tasks of mixed shape: two heavy combat/rendering ports (T45 warpzone trace/radius recursion + portal rendering, T59 map-object long-tail of 7 rare entities), one security hardening (T47 client-command bus gate), one large feedback/notification port (T61 announcer queue + deathtype registry + MSG_CHOICE replication + status-effect networking + voicetype routing), three HUD client-side ports (T68 shownames overlay, T69 dynamic damage-shake, T29 already-ported-verify of 7 panels), and two pure verify-then-scope tasks (T49 single-player campaign end-to-end, T5 windowed visual QA scoping). The dominant contention surface is the `game/hud/*` cluster shared by T29/T61/T68/T69 — chiefly `HudManager.cs` (touched by T68 + T69, read by T29) and `HudNotifications.cs` (owned by T61, flagged by T29) — plus T45's combat spread across `WeaponFiring.cs`/`WeaponSplash.cs`/`TraceService.cs`/`GameWorld.cs`/`DamageSystem.cs`. T49 and T5 edit nothing on the critical path (T49 is fully verify-only; T5 creates new test/CI files in disjoint dirs), so they are unconditionally parallel-safe. The hud cluster resolves cleanly under owner-edits-HudManager.cs + additive-panels, and T45's spread resolves under one owner because the seams are sequential within single files.

---

## Per-task specs

### T45 — Warpzone combat traversal + portal rendering
- **Verdict:** port — **Complexity:** large
- **Base behavior:** Seamless portals have two separable halves. (1) Transform/teleport (entity crossing a `trigger_warpzone` plane warps to exit with position/velocity/angles transformed by the 180°-flipped relative rotation) — **already ported** (`Warpzone.cs` WarpzoneTransform/Teleport). (2) Trace recursion + radius queries — projectiles, hitscan, splash, and view rendering must cross portals. `TraceBox/TraceLine/TraceToss_ThroughZone` accumulate transforms via a RefSys through up to 16 chained zones; `WarpZone_FindRadius`/`_Recurse` radius-query with the same chaining; client `WarpZone_FixView`/`FixNearClip`/`View_Inside/Outside` rotate the camera and prevent near-clip cutting. `WarpZone_MakeAllSolid` locks zones during a trace. Shipped default: `sv_warpzone_allow_selftarget` 0. Hook sites: weapon-firing traces and radius-damage queries.
- **Current port state:** Transform/teleport DONE. MISSING: TraceService extensions (no `WarpZone_TraceLine`/`TraceBox`/`TraceToss` recursion), `WarpZone_FindRadius` recursion (splash uses dumb `Api.Entities.FindInRadius`, no zone awareness), client portal rendering (no SubViewport wiring; portals render opaque), input-angle rotation post-teleport. Comments flag the gaps (WeaponFiring.cs:34/102/196/316, WeaponSplash.cs:20 "warpzone transforms deferred").
- **filesToCreate:** `src/XonoticGodot.Engine/Collision/TraceServiceWarpzoneExt.cs`, `src/XonoticGodot.Common/Gameplay/Warpzone/WarpzoneRadiusQuery.cs`
- **filesToEdit:** `WeaponFiring.cs`, `WeaponSplash.cs`, `Collision/TraceService.cs`, `Server/GameWorld.cs`
- **sharedFiles:** `MapObjects/Warpzone.cs`, `Collision/TraceService.cs`, `Weapons/WeaponFiring.cs`, `Damage/DamageSystem.cs`, `Weapons/WeaponSplash.cs`, `Server/GameWorld.cs`
- **Seam:** In WeaponFiring FireBullet/FireRailgunBullet/SetupShot replace `Api.Trace.Trace(…)` with new `Api.Trace.TraceLineWarpzone(…)`. In WeaponSplash RadiusDamage replace `FindInRadius` with `WarpzoneRadiusQuery.FindRadiusWarpzone(...)`; wrap the LOS trace in TraceLineWarpzone. One-time Boot wiring in GameWorld: `if (Api.Services is { Trace: TraceService ts }) ts.SetWarpzoneManager(_warpzoneManager);` after InitMapZones.
- **Risks:** 16-zone recursion cap (iterate, don't recurse); perf (each trace runs up to 16 sub-traces, multiplies with FireBullet's 32-iter penetration loop); RefSys accumulator must never be shared across simultaneous traces (single-threaded headless is safe; per-world thread out of scope); trace callback (trail particles) deferred.
- **Test plan:** xUnit `T45_WarpzoneTraceTests.cs` — trace recursion through a 2-zone pair, 20+-zone chain stops at guard (no hang), FindRadius through portal, hitscan through portal. Headless smoke on a stock warpzone map (atelier): rocket crosses zone and explodes on far side, splash hits far-side targets.

### T49 — Single-player campaign wiring
- **Verdict:** partial-wire (verify-then-scope) — **Complexity:** small
- **Base behavior:** QC campaign across 4 files. `CampaignSetup(n)` sets cvars (g_campaign 1, _campaign_name/_index) then `MapInfo_LoadMap` — must run BEFORE world boot. `XonoticCampaignList` (menu) reads `maps/campaign*.txt`, shows frontier+2 rows. `CampaignFile_Load` parses quoted-CSV (8 cols). `CampaignPreInit/PostInit/PreIntermission/PostIntermission` (server) load current+next level (CAMPAIGN_MAX_ENTRIES=2), apply per-level settings, evaluate sole-human-winner win/lose, save progress to `campaign.cfg` ONLY at frontier, advance/replay. Defaults: g_campaign_name "xonoticbeta", g_campaign_skill 0; lost level replays.
- **Current port state:** Implemented but unverified end-to-end. Menu (SingleplayerScreen), config/routing (MatchConfig, Shell, NetGame), server (GameWorld.Boot → Campaign.PreInit/PostInit, OnLevelTransition, intermission → PreIntermission/PostIntermission), and Campaign.cs logic all exist with cited line numbers. Unknown: does the full flow work in a playable session, any missing call-site/param, do cvars flow through Bootstrap/Menu/Server splits, edge cases (empty campaign, last level, replay).
- **filesToCreate / filesToEdit / sharedFiles:** NONE — all wiring exists.
- **Seam:** No new seam. If a gap is found, edit points are the 4 known locations (Shell.OnStartGame routing, NetGame extract+inject, GameWorld cvar bootstrap, intermission wiring).
- **Risks (operational/testing, low technical):** campaign file missing/unparseable → empty list; progress-cvar sync delay on replay; sole-human-winner condition too strict (`won==1 && lost==0`, check Campaign.cs:323); settemp revert between levels; frontier read/write atomicity; out-of-range level stays on current (verify "campaign complete" UX).
- **Test plan:** Expand CampaignCatalog.ParseTest; new CampaignTests (Load, PreInit settings, PreIntermission solo-win-advances/replay-no-advance, PostIntermission last-level-false/next-level-calls-Setup, Setup-sets-version-cvars). Integration: boot headless with `MatchConfig{CampaignId="xonoticbeta",CampaignIndex=0}`, simulate win, verify OnProgressSaved advances, boot index=1. End-to-end checklist on one campaign level.

### T47 — Harden the client command bus (port SV_ParseClientCommand gate)
- **Verdict:** port — **Complexity:** medium
- **Base behavior:** `SV_ParseClientCommand` filters every client command through three gates before dispatch: (1) UTF-8 round-trip validation (reject if differs); (2) `Ban_MaybeEnforceBanOnce`; (3) per-client flood bucket (`cmd_floodtime`) with exponential backoff (`sv_clientcommand_antispam_count`=8, `_time`=1s; exempt: begin/download/mv_getpicture/wpeditor/pause/prespawn/sentcvar/spawn/chat/minigame). After gates, dispatch splits SERVER_COMMANDS (~100+, server-only) vs CLIENT_COMMANDS (22, client-callable). MUTATOR_CALLHOOK at line 1277.
- **Current port state:** `ServerNet.HandleClientCommand` (589-603) calls `Commands.Execute(line, isServerConsole:false, caller:player)` with NO gates — no UTF-8 check, no ban check, no flood control. Commands.cs registers ALL 50+ commands to one table with no source distinction; ban/kick/gotomap/endmatch/settemp/set/seta/toggle all reachable by ANY authenticated client. `Bans.MaybeEnforceBan` exists. No `cmd_floodtime` on PeerState. No antispam cvars. **Exposed vulnerability:** clients can invoke admin verbs + cvar-reflection commands.
- **filesToCreate:** `src/XonoticGodot.Server/ClientCommandRegistry.cs` (optional)
- **filesToEdit:** `game/net/ServerNet.cs` (gates), `src/XonoticGodot.Server/Commands.cs` (source-gate + allowlist + antispam cvars)
- **sharedFiles:** `Commands.cs` (T38/T46/T56/T60 register here), `ServerNet.cs` (T38 minigame, T34 impulse), `Bans.cs` (T60 may expand)
- **Seam:** ServerNet.HandleClientCommand line 589 — insert 3 gates before line 598 Execute call. Commands.Execute (272) — add client-callable flag, gate lookup/invoke (279-282) to reject server-only when caller != null and not in allowlist. Register `sv_clientcommand_antispam_count`(8)/`_time`(1.0).
- **Risks:** low risk (UTF-8 pure filter, ban check standard, flood per-client). Carry-forward: A1-A4 added commands (T38 minigame, T46 chat, T56 rpn/maplist) must be marked client-callable. Edge case: minigame flood exemption (optional fidelity). Test risk: no auto-test catches a missing source-gate → manual coverage critical.
- **Test plan:** xUnit ClientCommandAllowlistTests (22 reachable), ServerCommandBlockTests (~25 rejected), FloodControlTests, UTF8ValidationTests, BanEnforcementTests, MinigameFloodExemption (optional). Headless: `--net-loopback` ban self/connect, say-spam rejection on 9th, `set` from client rejected. Smoke: 2 bots + 1 human, issue client verbs.

### T59 — Map-object long tail (7 rare entities)
- **Verdict:** port — **Complexity:** large
- **Base behavior:** Seven server-side-only entities: (1) func_stardust (EF_STARDUST, think 0.25s); (2) dynlight (static/path/follow/tag-attach, START_OFF/NOSHADOW/FOLLOW flags); (3) trigger_viewlocation + target_viewlocation_start/end (2.5D camera .viewloc); (4) misc_follow (attach via MOVETYPE_FOLLOW at INITPRIO_FINDTARGET); (5) func_fourier (sine-wave-driven mover, controller think 0.1s); (6) func_vectormamamam (driven by up to 4 external entities .wp00-03, projected onto normals); (7) target_voicescript (scripted voice-line sequence on trigger).
- **Current port state:** No port files; zero registrations in MapObjectsRegistry.cs. Infra exists: MapMover (SUB_CalcMove, UseTargets, controller pattern), MapObjectFieldsExtra.Apply, IndexRegister, RunPostSpawn (only Doors today), deferred-init pattern (Laser, LogicGates).
- **filesToCreate:** Stardust.cs, DynamicLight.cs, ViewLocation.cs, Follow.cs, AdvancedMovers.cs, VoiceScript.cs (all under `src/.../Gameplay/MapObjects/`)
- **filesToEdit:** `MapObjectsRegistry.cs`, `MapObjectFieldsExtra.cs`, `Framework/Entity.cs` (partial only via new file)
- **sharedFiles:** `MapObjectsRegistry.cs`, `MapObjectsCommon.cs` (MapMover.IndexRegister, controller pattern)
- **Seam:** RegisterAll() lines 155-162 — add T59 blocks after func_train, before breakables (163). MapObjectFieldsExtra.Apply — new keys (light_lev/color/dtagname, netname). MapObjectsCommon — partial Entity fields. RunPostSpawn — second INITPRIO_FINDTARGET pass for dynlight/follow/fourier/vectormamamam after door-link.
- **Risks:** dynlight CSQC rendering absent (no dlight system, T4 territory — spawns but no visual); viewlocation has no client camera consumer (free-look only); controller sub-entities need deletion hooks (leak); voicescript sound paths must resolve; follow attachment modes need setattachment equivalents; fourier/vectormamamam float-fidelity must match QC.
- **Test plan:** xUnit per entity (Stardust effects flag, DynamicLight static/path/follow/tag, ViewLocation refs, Follow attach/movetype, Fourier sine/blocked-crush, Vectormamamam projection, VoiceScript tokenization). Headless smoke: load a map using these, advance 10s, no crashes.

### T29 — Remaining HUD panels (verify-then-scope)
- **Verdict:** verify-then-scope — **Complexity:** small
- **Base behavior:** 7 panels (CHAT #12, PRESSEDKEYS #11, ENGINEINFO #13, PICKUP #26, QUICKMENU #23, STRAFEHUD #25, SCORE #7) registered via REGISTER_HUD_PANEL, drawn by HUD_Main loop, reading live `hud_panel_<id>_*` cvars via HUD_Panel_LoadCvars.
- **Current port state:** ALL 7 ALREADY FULLY PORTED AND REGISTERED (ChatPanel/PressedKeysPanel/FpsPanel/PickupPanel/QuickMenuPanel/StrafeHudPanel/ScorePanel verified). Shared infra: HudPanel base, HudLayoutDefaults (all 7 in table), HudRegistry (all 7 in Order), HudConfig (per-panel cvar defaults), HudManager (reflection discovery).
- **filesToCreate / filesToEdit:** NONE.
- **sharedFiles:** `HudManager.cs`, `HudPanel.cs`, `HudLayoutDefaults.cs`, `HudNotifications.cs`, `net/NetGame.cs`
- **Seam:** NO SEAMS — panels auto-discovered by HudRegistry.Build() reflection; HudManager._Ready() instantiates + applies StartHiddenIds gating; panels LoadConfig live per frame.
- **Risks:** ZERO implementation risk. Residual (out of scope): full HUD only in `--map` Hud, not NetGame's NetHud; panels net-ready but not fed data on play path → T34 integration task. T29 closes as-is.
- **Test plan:** xUnit panel discovery/identity (28 panels, PanelId↔cvar prefix), cvar default registration (6 per panel match luma), headless boot smoke (HudManager._Ready succeeds, Get<T> non-null, LoadConfig resolves, StartHiddenIds logic).

### T61 — Feedback / notification polish
- **Verdict:** port — **Complexity:** large
- **Base behavior:** Four+ discrete subsystems. (1) Local_Notification_Queue announcer voice queueing (10-deep) + dedup (`cl_announcer_antispam`=2s, tracks prev_soundfile/time) + spacing (queue_time from soundlength). (2) Deathtypes registry + `.message` category (monster/turret/vehicle) — DEATH_ISMONSTER/ISTURRET/ISVEHICLE macros drive obituary phrasing. (3) MSG_CHOICE per-client cvar replication (`msg_choice_choices[20]`, ReplicateVars). (4) StatusEffects_update networking (ENT_CLIENT_STATUSEFFECTS, delta-compressed bitmap, STATUSEFFECT_FLAG_PERSISTENT for burning/frozen overlays). (5) _GlobalSound VOICETYPE routing (PLAYERSOUND/TEAMRADIO/LASTATTACKER/LASTATTACKER_ONLY/AUTOTAUNT/TAUNT, directional attenuation, sv_autotaunt/sv_taunt/sv_gentle gates).
- **Current port state:** HudNotifications.cs has announcer queue (AntiSpamInterval=2f, MaxQueueSize=5 vs base 10; missing soundlength queuetime lookup; dedup at Play not Send time). DeathTypes.cs models strings not a registry — NO `.message` category, NO IsMonster/IsTurret/IsVehicle; obituaries hard-code substring checks (brittle). Notification.cs has MSG_CHOICE fields but NO client choice storage, NO ReplicateVars, NO selection logic. StatusEffects.cs simple list — no networked arrays, no ENT_CLIENT_STATUSEFFECTS, no StatusEffects_update, no PERSISTENT flag. Sounds — NO VOICETYPE routing, NO _GlobalSound, NO voice-message state (zero call sites). ClientNet decodes NotificationDispatch → HudNotifications (wiring present).
- **filesToCreate:** `Sounds/VoiceTypes.cs`, `Sounds/VoiceMessage.cs`, `Notifications/NotificationChoiceState.cs`
- **filesToEdit:** `game/hud/HudNotifications.cs`, `Damage/DeathTypes.cs`, `Notifications/Notification.cs`, `StatusEffects.cs`, `Sounds/SoundSystem.cs`, `Server/GameWorld.cs` or `Server/Scores.cs`
- **sharedFiles:** `StatusEffects.cs` (T11/T43 apply/remove), `Damage/DeathTypes.cs` (T40/T43 read deathtype), `Notifications/Notification.cs` (T34/T40 send), `game/hud/HudNotifications.cs` (T41 announcer sounds), `Sounds/SoundSystem.cs` (T40/T41 emit samples)
- **Seam:** DeathTypes Category enum + IsMonster/Vehicle/Turret predicates replacing string-contains in Scores.DeathMessages. StatusEffects: mark `StatusEffectsChanged=true` on apply/remove, net tick syncs arrays, ENT_CLIENT_STATUSEFFECTS client read. MSG_CHOICE: Entity.ChoiceValues[20] replicated as notification_CHOICE_* cvars. Voice routing: static SoundSystem.PlayVoiceMessage → _GlobalSound dispatcher. Announcer: read Notification.queuetime, per-notification dedup.
- **Risks:** StatusEffects delta-encode bitmap (or accept bandwidth); MSG_CHOICE replicate-on-change not per-frame; VOICETYPE_TEAMRADIO needs team synced first; deathtype registry init order; autotaunt per-client cvar replication timing; Notification.queuetime field may not exist yet; **parallel contention T40+T41** both emit sounds — shared SoundSystem/announcer queue must be touched serially (T40/T41 landed in prior waves; T61 is A5).
- **Test plan:** xUnit DeathTypes registry/category, announcer queue/dedup/spacing/overflow, MSG_CHOICE replication+selection, StatusEffects networking + PERSISTENT, VOICETYPE routing. Headless boot + integration kill-flow (obituary → ANNCE queue → CHOICE → burning sync → voice route).

### T5 — Systematic windowed visual QA (verify-then-scope)
- **Verdict:** verify-then-scope — **Complexity:** medium
- **Base behavior:** QA task verifying four orthogonal sub-areas against Base DP specs (NOT code to port): (1) lightmaps/deluxemaps/materials (Mod_Q3BSP_LoadLightmaps, MODE_LIGHTDIRECTIONMAP directional math); (2) patches (curves.c Q3PatchSubdivide/Tesselation); (3) billboards/flares (Q3FACETYPE_FLARE); (4) model bone poses (MD3/DPM/IQM loaders).
- **Current port state:** Visual infra is read-mostly. ScreenshotHook.cs WIRED + FUNCTIONAL but requires WINDOWED context (headless renders blank — GetViewport image null). Rendering ported (MapLoader, BezierPatch, IqmBuilder/Md3Builder/DpmBuilder, LightmapShader, BspReader DetectAndSplitDeluxemaps). Headless smoke exists (ci/ci.sh: --quit-after 200, --host stormkeep) but checks only log patterns + error count, NO frame capture. NO current visual QA, NO headless-safe assertions, NO map/model sweep.
- **filesToCreate:** `tools/visual-qa.sh` (windowed screenshot driver), `tests/XonoticGodot.Tests/VisualQaTests.cs` (headless-safe assertions)
- **filesToEdit:** `ci/ci.sh` (add "Visual QA (headless assertions only)" section after line 105), `RUNNING.md`
- **sharedFiles:** NONE.
- **Seam:** New ci/ci.sh section after line 105 running VisualQaTests with --filter VisualQa; windowed tools/visual-qa.sh standalone, documented in RUNNING.md.
- **Risks:** HIGH UNCERTAINTY on what is automatable headless. Godot's headless dummy_video renders NOTHING (GetViewport().GetImage() null — confirmed). NO rendered-frame capture in CI; lightmap/deluxemap/shader/patch visual correctness CANNOT be headless-automated (windowed manual eye-check only). Model bone-pose CAN be unit-tested headless (byte-packed structures). Shader compilation assertable headless via log error search.
- **Test plan:** see T5 scoping section below.

---

## CONTENTION MAP

Every file appearing in more than one task's `filesToEdit` OR `sharedFiles`. Path roots normalized (`src/.../` = `src/XonoticGodot.*/`).

| File | Contending tasks | Nature | Notes |
|------|------------------|--------|-------|
| **game/hud/HudManager.cs** | T29 (shared), T68 (shared), T69 (edit+shared) | edit (T69) + shared reads | **T69 OWNS the edit** (adds shake offset in _Process). T68 only reflection-discovers a panel (additive). T29 already-ported, read-only. |
| **game/hud/HudNotifications.cs** | T29 (shared), T61 (edit+shared) | edit (T61) + shared | **T61 OWNS** (enhance announcer queue/dedup). T29 read-only. T41 (prior wave) interplay noted by T61. |
| **game/net/NetGame.cs** | T29 (shared), T68 (edit+shared), T69 (shared) | edit (T68) + shared | **T68 OWNS** the only edit (SetupCameraAndHud seam + _Process feed). T69 has "no seam" in NetGame; T29 read-only. |
| **game/hud/HudPanel.cs** | T29 (shared) | shared | Only T29 in A5; no A5 contention (T29 is verify-only). |
| **game/hud/HudLayoutDefaults.cs** | T29 (shared) | shared | Only T29 in A5; T68/T69 add no panel to this table. |
| **src/.../Collision/TraceService.cs** | T45 (edit+shared) | edit | Single owner T45. |
| **src/.../Weapons/WeaponFiring.cs** | T45 (edit+shared) | edit | Single owner T45. |
| **src/.../Weapons/WeaponSplash.cs** | T45 (edit+shared) | edit | Single owner T45. |
| **src/.../Server/GameWorld.cs** | T45 (edit+shared), T61 (edit, "GameWorld.cs OR Scores.cs") | edit | **Cross-task contention.** T45 edits Boot (warpzone-manager wiring). T61 edits a StatusEffects_update dirty-mark site (or routes via Scores.cs). Different methods; coordinate or T61 prefers Scores.cs. |
| **src/.../Damage/DamageSystem.cs** | T45 (shared) | shared | Only T45 in A5 (T45 lists it shared; no edit cited). No A5 co-editor. |
| **src/.../MapObjects/Warpzone.cs** | T45 (shared) | shared | Single owner T45. |
| **src/.../MapObjects/MapObjectsRegistry.cs** | T59 (edit+shared) | edit | Single owner T59. |
| **src/.../MapObjects/MapObjectsCommon.cs** | T59 (shared) | shared | Single owner T59. |
| **src/.../Damage/DeathTypes.cs** | T61 (edit+shared) | edit | Single owner T61 (T40/T43 are prior waves, not A5). |
| **src/.../StatusEffects.cs** | T61 (edit+shared) | edit | Single owner T61 (T11/T43 prior waves). |
| **src/.../Notifications/Notification.cs** | T29 (—), T61 (edit+shared) | edit | T61 owns; T29 does not list Notification.cs (it lists HudNotifications.cs). No A5 contention. |
| **src/.../Sounds/SoundSystem.cs** | T61 (edit+shared) | edit | Single owner T61. |
| **src/.../Net/NetEntity.cs** | T68 (shared) | shared | Single owner T68. |
| **server/command** ... `ServerNet.cs` / `Commands.cs` / `Bans.cs` | T47 (edit+shared) | edit | Single owner T47 (T38/T46/T56/T60 prior waves). |

**True intra-A5 cross-task contentions (the only ones that need a serialization or ownership decision):**
1. **`game/hud/HudManager.cs`** — T69 (edit) vs T68 (additive panel discovery) vs T29 (read).
2. **`game/net/NetGame.cs`** — T68 (edit) vs T69 (no-seam reader) vs T29 (read).
3. **`src/.../Server/GameWorld.cs`** — T45 (Boot warpzone wiring) vs T61 (StatusEffects_update dirty-mark, or route to Scores.cs).

Every other multi-task file is a same-task edit+shared pairing or a shared-with-prior-wave-only entry (T11/T34/T38/T40/T41/T43/T46/T56/T60 are NOT in A5).

---

## Recommended implementation BATCHING

### Fully disjoint — parallel-safe (no shared editable file with any other A5 task)
- **T47** (ServerNet.cs / Commands.cs / Bans.cs — command bus; no other A5 task touches these).
- **T59** (MapObjects/* — registry, common, new entity files; no other A5 task touches MapObjects).
- **T49** (verify-only, edits nothing).
- **T5** (creates tools/visual-qa.sh + VisualQaTests.cs + edits ci.sh/RUNNING.md — all disjoint dirs).
- **T29** (verify-only, edits nothing; reads hud/* but writes nothing).

These five can run in parallel with no coordination.

### Must serialize or use owner-edits-file / return-snippets

**HUD cluster (T29/T61/T68/T69) resolution:**
- **HudManager.cs owner = T69.** T69 is the only task that *edits* HudManager.cs (it adds the per-frame shake offset in `_Process` after the fade-clamp, applied to the CanvasLayer Position). T68 does NOT edit HudManager.cs — its panel is auto-discovered by HudRegistry reflection (additive, zero-touch), and its layer is a new ShowNamesLayer added in NetGame, not HudManager. T29 is verify-only. **Decision: T69 owns HudManager.cs outright; T68's panel addition is purely additive (new file `ShownamesPanel.cs` + new `ShowNamesLayer.cs`); no serialization needed between T68 and T69 on HudManager.**
- **Panels are additive.** Both new HUD elements (T68 shownames, T69 shake) are additive: T68 adds new files and a NetGame seam; T69 adds a new file (`HudDynamicShake.cs`) and one HudManager edit. They do not touch the same lines. The only shared file with a real write is **NetGame.cs**, where **T68 owns the only edit** (SetupCameraAndHud + _Process feed); T69 explicitly declares "no NetGame seam." **Decision: T68 owns the NetGame.cs edit; T69 needs nothing there.** If both must land in one branch, sequence T68's NetGame edit first, then T69's HudManager edit — they are in different files so order is immaterial.
- **HudNotifications.cs owner = T61.** T29 is read-only here. No contention. T41 (announcer, prior wave) has already landed per T61's own note, so the queue-refactor can proceed.
- **Net effect:** T61, T68, T69 are mutually parallel-safe at the *file* level (T61 → HudNotifications/Sounds/DeathTypes/StatusEffects/Notification; T68 → ShowNames* + NetGame; T69 → HudDynamicShake + HudManager). The single overlap risk is GameWorld.cs (T45 vs T61, below), not within the hud cluster.

**T45 NetGame/DamageSystem/Engine.Collision spread:**
- T45 touches a wide set but **all are single-owner within A5 except GameWorld.cs**: TraceService.cs, WeaponFiring.cs, WeaponSplash.cs, Warpzone.cs, DamageSystem.cs (shared-listed but no edit cited) — none co-edited by any other A5 task. The seams are sequential edits *within* each file (replace `Api.Trace.Trace` call sites, add a setter), so a single T45 owner edits them serially with no cross-task lock.
- **GameWorld.cs is the one cross-task file (T45 vs T61).** T45 edits Boot (the `ts.SetWarpzoneManager(...)` wiring after InitMapZones). T61 edits a StatusEffects_update dirty-mark site *or* routes that through Scores.cs. These are different methods/regions. **Decision: route T61's status-effect dirty-mark through `Scores.cs` (T61's stated alternative) to keep GameWorld.cs single-owner = T45.** If T61 must edit GameWorld.cs, use owner-edits-file: T45 owns GameWorld.cs, T61 returns the exact snippet (the one-line dirty-mark in DamageSystem.ApplyFrozen/ApplyBurning is the cleaner site anyway — those live in DamageSystem.cs, which T45 only shared-reads). **Cleanest split: T61 marks dirty in DamageSystem.ApplyFrozen/ApplyBurning (DamageSystem.cs), not GameWorld.cs — fully removing the GameWorld.cs collision.**

**Recommended wave shape:**
- **Parallel group A (independent):** T47, T59, T29, T49, T5.
- **Parallel group B (hud + feedback, file-disjoint after the GameWorld decision):** T61, T68, T69.
- **T45 solo or with group B**, owning GameWorld.cs; T61 keeps its status-effect dirty-mark in DamageSystem.cs/Scores.cs so GameWorld.cs stays single-owner.

---

## T5 scoping decision (verify-then-scope)

**Hard constraint:** Godot's headless renderer (`dummy_video`) renders nothing — `GetViewport().GetTexture().GetImage()` returns null headless (confirmed in ScreenshotHook.cs:51-53, RUNNING.md:12-13). No rendered-frame capture is possible in CI.

**CAN be asserted headlessly (automatable, CI-safe):**
- **Model bone-pose / skeleton integrity** — IQM/MD3/DPM loaders produce byte-packed data structures that unit-test without rendering: bone count, parent-chain validity, bind-pose matrices non-singular. Safe `[Theory]` per model.
- **Asset load success** — per-map load completes without exception, map-object count > 0, collision-brush count matches expected.
- **Shader compilation success** — if the material/shader loader runs and reports compile errors to the log, assert no `ERROR`/`shader compile failed` in output. (Compile success only — NOT output color/specularity correctness.)
- **BezierPatch tessellation runs** — assert the patch code executes without exceptions (NOT silhouette correctness).
- Tests self-skip without assets/data (existing 18-real-data-test-class pattern).

**NEEDS the user's manual windowed eyes (NOT automatable):**
- **Lightmap/deluxemap visual correctness** — directional MODE_LIGHTDIRECTIONMAP modulation on real walls; only verifiable on-screen.
- **Patch silhouette smoothness** — curves render smooth vs faceted; visual only.
- **Billboards/flares** — appear as textured quads vs invisible/black; visual only.
- **Materials on geometry** — hero textures + deluxemaps + normal/roughness render correctly (no magenta missing-texture); visual only.
- **Model pose correctness on-screen** — model renders un-twisted in-world (load-structure is headless-assertable; the *rendered* pose is not).

**Concrete runnable smoke/checklist proposed:**
- **Headless (CI, new):** `tests/XonoticGodot.Tests/VisualQaTests.cs` — `[Theory]` per official map (enumerate the 31: stormkeep, solarium, afterslime, …) asserting load success + object/brush counts; `[Theory]` per model (erebus/lucifer/xonotic.iqm, …) asserting loader success + skeleton parent-chain + non-singular bind-pose; `[Theory]` per stock shader name asserting no compile error. Wire into `ci/ci.sh` after line 105 as "Visual QA (headless assertions only)": `dotnet test … --filter VisualQa`, fail on `hard_errors != 0`, grep per-map load summary.
- **Windowed (manual, documented in RUNNING.md):** `tools/visual-qa.sh` — for each of 31 maps run `"$GODOT" --path . --map <map> --resolution 1280x720 --screenshot screenshots/<map>.png --screenshot-frames 120`; for each player model `--screenshot screenshots/model_<model>.png`. Manual checklist: stormkeep directional shadows, solarium smooth patches, flare quads visible, model T-pose/idle bones aligned, no magenta materials. Compare to upstream DP baseline screenshots (future task if baselines collected).
- **Smoke (already passing):** `dotnet test --filter VisualQa` 0 failures; ci.sh prints each map's asset-load summary without ERROR; final line "Visual QA (headless): N maps loaded, M models loaded, K shaders compiled, 0 hard errors."
