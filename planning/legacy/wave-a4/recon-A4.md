# Wave A4 Recon Synthesis

## Overview

Wave A4 covers nine independent parity-port tasks spanning the server feedback/rules layer (T41 client feedback drivers, T42 overtime/sudden-death, T46 server chat engine, T66 FilterItem hook), gameplay/weapon parity (T52 Q3/QL/CPMA/Q1/WoP/Q3DF compat remaps, T67 HLAC crouch-spread), menu data-list backends (T23), the HUD configure-mode editor (T27), and a verify-then-cut audit of the legacy model importers (T24). Seven tasks are full ports/partial-wires, one (T24) is a recommended formal cut, and the remainder are small isolated fixes. The dominant scheduling risk is a cluster of hot shared files — `GameWorld.cs`, `Cvars.cs`, `GameType.cs`, `NetGame.cs`, `ServerNet.cs`, `Commands.cs`, the `game/hud/*` panel files, and the two MapObjects/Items registries — each touched by two or more tasks, which forces serialization or an owner-edits/return-snippets model on those seams.

---

## T41 — Client feedback drivers (hitsound / footsteps / announcer countdown / objective rings)

- **Verdict:** partial-wire
- **Complexity:** medium
- **Base behavior:** HitSound (`client/view.qc:890–978`) accumulates `STAT(HITSOUND_DAMAGE_DEALT_TOTAL)` on stat-update edges guarded by `STAT(HIT_TIME)`; fires `sound7()` when `unaccounted_damage > 0` and antispam passes, with pitch modes 1=fixed / 2=decreasing (line 942) / 3=increasing (line 948); also fires TYPEHIT/KILL on their stat edges. Announcer (`client/announcer.qc`) spawns an `announcer_countdown` entity on GAMESTARTTIME change; its think loop fires ANNCE_NUMBER (3/2/1/prepare) once per second, and `Announcer_Time()` fires remaining-min announcements with hysteresis. Footsteps/hitground (`common/physics/player.qc:664–708`, both `#ifdef SVQC`): `PM_check_hitground` fires once per landing via a WasFlying latch (`GS_FALL/_METAL`); `PM_Footsteps` is periodic at 0.3s, speed-gated at 60% maxspeed. Objective rings (`client/view.qc:1006–1022`): HUD_Draw priority NADE_TIMER > CAPTURE_PROGRESS > REVIVE_PROGRESS, drawn via `DrawCircleClippedPic` as a [0,1] fill fraction.
- **Base sources:** `qcsrc/client/view.qc` (UpdateDamage 890–912, HitSound 914–978, HUD_Draw 1006–1022); `qcsrc/client/announcer.qc` (Announcer_Gamestart 127–174, Announcer_Countdown 58–117, Announcer_Time 188–226); `qcsrc/common/physics/player.qc` (PM_check_hitground 664–687, PM_Footsteps 689–708).
- **Port current state:** HitSound written in `game/client/HitSound.cs` (137 lines, pitch modes 1/2/3 correct) but UNWIRED — `DamageEntityState.FireHitSound` never called and the damage stat is not networked (blocker). Announcer MISSING entirely (no .cs; the stats exist but no loop runs). Footsteps DONE & WIRED in `PlayerPhysics.cs` (1580–1685, call-site live). Objective rings MISSING — stats undefined in NetEntity; `CrosshairPanel.cs` renders weapon-stat rings only.
- **Files to create:** `src/XonoticGodot.Server/AnnouncerController.cs`
- **Files to edit:** `src/XonoticGodot.Net/NetEntity.cs`, `src/XonoticGodot.Server/GameWorld.cs`, `src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs`, `game/net/NetGame.cs`, `game/hud/CrosshairPanel.cs`
- **Seam:** HitSound — (1) `DamageSystem.OnEventDamage()` increments the damage stat by (health+armor) when attacker≠target; (2) `NetGame.cs` calls `_hitSound?.OnHit(damage)` on the local-player stat/damage edge. Announcer — `AnnouncerController.Update(serverTime)` ports Gamestart/Countdown/Time and fires into NotificationSystem; instantiated + ticked from `GameWorld.OnStartFrame()`. Objective rings — add `NadeTimer/CaptureProgress/ReviveProgress` networked floats to `NetEntity.cs`; objective-spawn code sets them; `CrosshairPanel._Draw()` renders rings in NADE>CAPTURE>REVIVE priority.
- **Risks:** HitSound path fails silently if the damage stat is undefined/unsent. Announcer notifications must exist in NotificationSystem or they no-op. Objective stats unset → rings invisible (feature-dead, not crash). NetGame must route ANNCE_* to the client.
- **Test plan:** Unit — countdown fires 3/2/1/prepare on a 1s schedule; time-remaining fires at thresholds with hysteresis; DamageSystem increments stat per event; HitSound pitch varies by mode; objective stats set/cleared over lifecycle. Headless — countdown audible, hitsounds fire, footsteps play, grenade ring renders. Local feel — countdown timing, pitch variation, muffled-crouched footsteps, live objective rings.

---

## T42 — Global overtime / sudden-death win layer (CheckRules_World)

- **Verdict:** port
- **Complexity:** medium
- **Base behavior:** `CheckRules_World` (`world.qc:1725–1861`): timelimit-based overtime initiation (1781–1810) calls `InitiateSuddenDeath()` when timelimit elapses with no latched winner — if `timelimit_overtime>0` and overtimes remain it returns 1 (→ `InitiateOvertime()` later); otherwise it arms `checkrules_suddendeathend` and sets `overtimes=OVERTIME_SUDDENDEATH`. `WinningCondition_Scores` (1560–1639) returns YES/NO/STARTSUDDENDEATHOVERTIME(3); ties yield STARTSUDDENDEATHOVERTIME. Equality is set per score-mode (`WinningConditionHelper_equality`); leadlimit logic (`top-second >= leadlimit`) interacts with fraglimit (both must satisfy if both set, else either). `GetWinningCode` (1510–1536) maps equality×limit to STARTSUDDENDEATHOVERTIME / WINNING_NEVER / YES / NO. Flow (1827–1860) drives sudden-death entry/exit and final-victory latching. Cvars: `timelimit_overtime=2`, `timelimit_overtimes=0`, `timelimit_suddendeath=5`, `leadlimit=0`, `leadlimit_and_fraglimit`. State: `checkrules_overtimesadded/_suddendeathend/_suddendeathwarning`, `overtimes` (OVERTIME_SUDDENDEATH=0x01000000).
- **Base sources:** `qcsrc/server/world.qc` — CheckRules_World (1725–1861), InitiateSuddenDeath (1467–1497), InitiateOvertime (1499–1508), WinningCondition_Scores (1560–1639), GetWinningCode (1510–1536); `qcsrc/common/stats.qh` (OVERTIME_SUDDENDEATH, overtimes); `qcsrc/server/world.qh` (WINNING_STARTSUDDENDEATHOVERTIME=3); `xonotic-server.cfg` (shipped defaults).
- **Port current state:** `GameWorld.CheckRulesAndIntermission()` (1648–1677) only polls the gametype's `MatchEnded` latch — the entire timelimit→overtime→sudden-death cascade is absent; a tied timed match ends instantly. `Cvars.cs` registers timelimit/fraglimit/leadlimit (line 98) but MISSING the three overtime cvars. `Deathmatch.cs` latches on FragLimit but has no equality detection. `Tdm.cs` documents tie detection but it's not wired to CheckRules. All gametypes have a `MatchEnded` bool but no unified equality-report hook. `Intermission` class is a correct sink; upstream data structure missing.
- **Files to create:** `src/XonoticGodot.Server/OverTimeManager.cs`
- **Files to edit:** `Cvars.cs`, `GameWorld.cs`, `GameType.cs`, plus all 14 gametypes — Deathmatch, Tdm, Ctf, Domination, KeyHunt, FreezeTag, ClanArena, TeamMayhem, Mayhem, Keepaway, Onslaught, Nexball (all under `src/XonoticGodot.Common/Gameplay/GameTypes/`).
- **Seam:** Rewrite `GameWorld.CheckRulesAndIntermission()` (1648–1677) to add the cascade before the `MatchHasEnded()` poll: compute timelimit (warmup/campaign/start-aware); if expired and no winner, call `OverTimeManager.InitiateSuddenDeath()`; if another overtime queued, `InitiateOvertime()`; if sudden-death timer expired, latch victory; then poll MatchHasEnded as normal. Template is `world.qc:1741–1860`.
- **Risks:** Per-mode equality must be faithful to QC (Tdm=equal team points; Ctf=equal captures; DM=no tie). `InitiateOvertime()` must `Cvars.SetFloat("timelimit", new)` so clients/commands see the extension. Campaign must be gated out via g_campaign. `STAT(OVERTIMES)` networking deferred to T54/T23 (no T42 risk). Leadlimit itself stays in team-modes — T42 only routes their equality flag (verify Tdm wired). Round-based modes (ClanArena/FreezeTag) are out of scope.
- **Test plan:** Unit (xUnit) — OverTimeManager Reset/InitiateSuddenDeath true/false/InitiateOvertime increment; Tdm/Ctf report tie on equal scores; Deathmatch never reports tie. Integration (headless) — boot DM 2 bots with `--timelimit 1 --fraglimit 0 --timelimit_overtime 2 --timelimit_overtimes 1`, force equal frags, assert match continues into sudden-death, timelimit extended, then concludes cleanly.

---

## T46 — Server chat engine (team / private / ignore / flood)

- **Verdict:** port
- **Complexity:** large
- **Base behavior:** `Say(source, teamsay, privatesay, msgin, floodcontrol)` gates on 4 `g_chat_*` cvars (all shipped 1), runs `formatmessage()` for macro expansion, applies per-say-type flood control (spl 3/1/1, burst 2/2/2, lmax 2/2/2 for broadcast/team/tell), routes to FOREACH_CLIENT recipient sets (broadcast/team/spectator/private) with mutual `ignore_playerinlist(receiver,sender)` blocking, logs via EventLog if enabled, and returns 1/0/-1 (accept/reject/fake-accept). Commands say_team/tell/ignore/unignore/clear_ignores invoke Say or ignore-list CRUD keyed by crypto_idfp (PersistentId in port). Muted players fake-accept (sender sees msg, others don't). `formatmessage` expands %h/%a/%l/%d/%o/%O/%w/%W/%x/%s/%S/%t/%T via helpers (PlayerHealth/NearestLocation/WeaponNameFromWeaponentity/AmmoNameFromWeaponentity).
- **Base sources:** `qcsrc/server/chat.qc` — Say (27–372), formatmessage (498–591); `qcsrc/server/command/cmd.qc` — say_team/tell/ignore/unignore/clear_ignores + ignore-list CRUD (661–680, 931–986, 452–505, 988–1031, 226–251, 47–195); `xonotic-server.cfg` flood/allowed defaults.
- **Port current state:** `Chat.cs` does NOT exist. `Commands.cs:563–580` has a stub `CmdSay()` calling `ChatHandler(caller, msg, teamOnly=false)`; no say_team/tell/ignore commands. No ignore-list, mute flag, or per-say flood state on `Player.cs` (PersistentId exists at line 100). `ServerNet.cs:619–629` `BroadcastPrint()` sends to all accepted clients — no team routing, private paths, or ignore filtering. `NetGame.cs` sets `ChatHandler` hardcoded to broadcast, no real team routing.
- **Files to create:** `src/XonoticGodot.Server/Chat.cs`
- **Files to edit:** `src/XonoticGodot.Server/Commands.cs`, `src/XonoticGodot.Common/Gameplay/Player/Player.cs`, `game/net/ServerNet.cs`
- **Seam:** Port `Say()` fully into `Chat.cs` (gate, formatmessage, per-type flood, routing, ignore, mute fake-accept, event log, mutator hooks). In `Commands.cs` add Player fields (floodcontrol_chat/chatteam/chattell, ignore_list, muted), register CmdSayTeam/CmdTell/CmdIgnore/CmdUnignore/CmdClearIgnores, and wire `ChatHandler` (broadcast → `Say(caller,false,null,msg,true)`). In `ServerNet.cs` add `SendTeamChat(team,msg)` and `SendPrivateChat(sender,target,msg)` that iterate ClientManager.Players filtering by team/IS_PLAYER/ignore_list and send per-peer, plus an `ignore_playerinlist(this,other)` helper.
- **Risks:** Per-player flood timestamps must persist across ticks (else spam). Ignore-list tempstring semantics must port to C# string/Set cleanly. Ignore checks run on every send branch — O(N·M), tolerable <32 players. Mute fake-accept return-code fork is subtle (lines 289–294). `formatmessage` crosshair/location tracing needs the headless tracer or %y/%x crash.
- **Test plan:** Unit — gate checks (allowed=0 → 0; team/spectator gates); flood throttling with persistent timestamps; recipient routing (teamsay 1/−1, privatesay unicast); ignore add/remove/tell-blocked → −1; mute fake-accept; formatmessage macros. Integration (headless `--host atelier`): say / say_team / tell / ignore+tell(blocked) / unignore+tell / clear_ignores, verifying routing + ignore enforcement in console log.

---

## T52 — Compat entity remaps (Q3 / QL / CPMA / Q1 / WoP / Q3DF) — the ADD side

- **Verdict:** port
- **Complexity:** large
- **Base behavior:** T52 adds weapon/item classname remaps for imported maps. `SPAWNFUNC_Q3/_Q3WEAPON/_Q3AMMO` (quake3.qh:18–38) spawn a weapon/ammo pair and scale `.count` by an optional multiplier, then set ammo via `SetResource(this, ammo_type, rint(count * GetAmmoConsumption(weapon)))`. Weapon remaps (quake3.qc:55–100): railgun→VORTEX, lightning→ELECTRO (0.125× ammo), rocketlauncher→DEVASTATOR, shotgun↔machinegun (arena swap, 8× SG→MG), grenadelauncher→MORTAR, nailgun→CRYLINK (ELECTRO on Q1), plasmagun→HLAC (HAGAR on XDF), bfg→FIREBALL (CRYLINK on XDF), chaingun/hmg→HAGAR, prox_launcher→MINE_LAYER, gauntlet→TUBA, grapplinghook→HOOK. Item remaps (102–116): quad→Strength, enviro→Shield, haste→Speed, invis→Invisibility, armor body/combat/shard/green→ArmorMega/Big/Small/Medium. Q1 (quake.qc:14–20), Q2 (quake2.qc:9–12), WoP (wop.qc:18–42) add further pairs. Q3DF target_* entities (quake3.qc:119–295): target_init (reset per spawnflags), target_score / target_fragsFilter (CTS-only), target_print/smallprint (centerprints, Q3 vs Q3DF spawnflag modes). Critical: SG↔MG swap ONLY on `q3compat==Q3COMPAT_ARENA`; ammo scales hardcoded per pair; target_score/fragsFilter auto-delete outside g_cts; REMOVAL filter already ported, only ADD side missing.
- **Base sources:** `qcsrc/server/compat/quake3.qc` (51–295), `quake3.qh` (18–38), `quake.qc` (14–20), `quake2.qc` (9–12), `wop.qc` (18–42); `qcsrc/common/resources/sv_resources.qc` GetAmmoConsumption (231–243); `weapon.qh` SPAWNFUNC_WEAPON (191–195); `item.qh` SPAWNFUNC_ITEM/_BODY (97–110).
- **Port current state:** Removal filter ported (`MapEntityFilter.DoesQ3CompatRemove`). ADD side completely absent. `ItemSpawnFuncs.Register()` (35–93) registers only canonical names + generic aliases — no compat aliases, no `.count` scaling. `MapObjectsRegistry.RegisterAll()` (19–262) has no Q3/Q1/WoP block; target_* unregistered. Net effect: Q3/DeFRaG/WoP maps load geometry but `weapon_railgun`/`item_quad` resolve to null → no pickups.
- **Files to create:** `src/XonoticGodot.Common/Gameplay/MapObjects/CompatRemaps.cs`
- **Files to edit:** `src/XonoticGodot.Common/Gameplay/Items/ItemSpawnFuncs.cs`, `src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs`
- **Seam:** `ItemSpawnFuncs.Register()` — add compat `AliasItem()` blocks after canonical aliases (after line 82). `MapObjectsRegistry.RegisterAll()` — register target_* after `ItemSpawnFuncs.Register()` (line 25). Ammo scaling — in `ItemSpawnFuncs.WeaponSpawn` (127–140) via a compat dict keyed by weapon NetName, applied to `e.ItemAmmoCount` before `StartItem.Spawn`.
- **Risks:** Ammo `.count` scaling must apply BEFORE `WeaponPickup.ItemInit` reads it. No global `q3compat` flag in port — either add one or always apply SG↔MG. target_score/fragsFilter CTS gate must wire delete-if-not-cts. target_print spawnflag bit meanings flip Q3DF vs Q3. Verify a C# GetAmmoConsumption equivalent exists or add a table/abstract method.
- **Test plan:** xUnit — alias resolution (railgun→vortex, quad→strength, supernailgun→hagar, punchy→arc); ammo scaling (lightning .count=100 → ≈50; shotgun arena → machinegun); Q3DF targets (target_score deletes outside CTS / adds inside; fragsFilter thresholds + RESET; target_print centerprints). Headless — load a Q3 map, no errors, pickups spawn, item_quad grants Strength, target_init resets, exit 0.

---

## T23 — Menu data-list backends (screenshots / music+playlist / skins / player-stats / server-info / create-game map-info)

- **Verdict:** partial-wire
- **Complexity:** medium
- **Base behavior:** Six self-contained list backends over a shared `DataSource` abstraction (datasource.qc/.qh: getEntry/reload/indexOf/destroy, StringSource/CvarStringSource, DataSource_true/false sentinels). (1) Screenshots — enumerate `screenshots/{,*/}*.{jpg,tga,png}`, strip prefix/ext, decolorize, buf_sort; viewer + slideshow; binds `cl_autoscreenshot`. (2) Music — enumerate `sound/cdtracks/*.ogg`, mark [C]/[D], add to playlist (`music_playlist_list0`), set/reset `menu_cdtrack`. (3) Playlist — right pane from `music_playlist_list0` tokenized each draw; transport via index/sampleposition with wait+defer; drag-reorder via swapInPriorityList. (4) Skins — enumerate `gfx/menu/*/skinvalues.txt`, parse title/author, store NAME/TITLE/AUTHOR/PREVIEW; binds `menu_skin`, applies via menu_restart. (5) Player stats — `PS_D_IN_DB` + db_get keys, date conversion, sorted overall→ranked→unranked; async fetch via PlayerStats_PlayerDetail_CheckUpdate. (6) Server-info — host-cache reads + QCSTATUS parse, Status/ToS tabs. (7) Create-game map-info — `MapInfo_Get_ByID`, preview fallback chain, gametype checklist. Shipped cvars: cl_autoscreenshot=0, cl_autodemo=0, music_playlist_list0="", music_playlist_index=−1, menu_skin="default", etc.
- **Base sources:** `qcsrc/menu/xonotic/` — datasource.qh/.qc, screenshotlist.qc, soundlist.qc, playlist.qc, skinlist.qc, statslist.qc, dialog_multiplayer_join_serverinfo.qc, dialog_multiplayer_create_mapinfo.qc, serverlist.qc, maplist.qc; `qcsrc/common/playerstats.qc`; `qcsrc/common/mapinfo.qc`.
- **Port current state:** Honest stubs exist for screenshots, music, demo, server-info, profile-stats — all with inert buttons and empty lists; live cvar checkboxes only. `game/menu/MapList.cs` (88 lines) is a VFS data catalog, NOT a maplist UI; map-info dialog missing. NO skin-list UI ported. NO DataSource/StringSource/CvarSource abstraction. Missing entirely: skins dialog, create-game map-info dialog, DataSource abstraction, MapInfo .mapinfo parser/cache.
- **Files to create:** `game/menu/dialogs/DialogMediaSkinList.cs`, `game/menu/dialogs/DialogCreateGameMapInfo.cs`, `src/XonoticGodot.Common/Menu/DataSource.cs`, `src/XonoticGodot.Common/Menu/MapInfoBackend.cs`
- **Files to edit:** `game/menu/dialogs/DialogMediaMusicPlayer.cs`, `DialogMediaScreenshot.cs`, `DialogMediaDemo.cs`, `DialogMultiplayerProfile.cs`, `DialogServerInfo.cs`, `game/menu/MapList.cs`, `game/menu/ServerBrowser.cs`
- **Seam:** New `DataSource` (StringSource/CvarStringSource) in a menu-utilities namespace; each list backend instantiates its source on _Ready. Per-dialog enumerator → ItemList.AddItem loop; reload on filter/refresh; cvar changes re-read each draw (playlist tokenizes every frame). SkinList parses skinvalues.txt → bind menu_skin → menu_restart. Stats list async via callback. ServerInfo populates detail rows from `ServerBrowser` selected entry + player list. MapInfo static class parses .mapinfo per BSP and caches; map-info dialog reads it, draws preview fallback chain, populates gametype checkboxes.
- **Risks:** Async stats backend needs callback/error handling + loading placeholder. Playlist cvars read each draw or UI goes stale. .mapinfo parse order must mirror base or dialog blanks. Decolorization must replicate strdecolorize. search_begin → Godot VFS (`MenuState.Vfs.Find`)/DirectoryAccess; empty if VFS unmounted at boot. Host-cache only after a refresh; sequence Refresh→replies→info dialog. **Conflict with T50 (menu_cmd dispatch):** T23 buttons route through MenuCommand and stay inert until T50 defines handlers (music_add/remove, menu_cdtrack, etc.).
- **Test plan:** Unit — DataSource contract; mocked-VFS enumerators (screenshot/sound/demo/skin); cvar binding read/write; filter+sort; MapInfo.GetByIndex + preview fallback; ServerInfo detail population from a fake ServerEntry. Headless — Media→Music populates (or honest "pending"); Create Game→map double-click pops map-info; Profile→stats list; server browser→select→Server Info; all inert buttons GD.Print-logged. Success: all six lists render non-empty, cvar bindings correct, no NREs/missing-asset errors (use fallbacks).

---

## T24 — MDL/MD2/ZYM/PSK importers — verify shipped content needs them first; else formally cut

- **Verdict:** verify-then-maybe-cut → **recommend FORMAL CUT**
- **Complexity:** small
- **Base behavior:** Darkplaces dispatches model loading by magic byte (`model_shared.c:45–65`): IDPO (MDL), IDP2 (MD2), ZYMOTICMODEL (ZYM), ACTRHEAD (PSK), plus IDP3 (MD3) and IQM. Loaders in `model_alias.c` (Mod_IDP0_Load 972, Mod_IDP2_Load 1324, Mod_ZYMOTICMODEL_Load 1768, Mod_PSKMODEL_Load 2540); structs in `model_alias.h`. Ship inventory: Base has MDL=19 (projectiles/gibs/casings/traces), ZYM=2 (pomp.zym, train.zym — both map-exclusive, in xonotic-maps.pk3dir), MD2=0, PSK=0. QC `common/models/all.inc` registers 21 MDL entries via MODEL(); no ZYM references.
- **Base sources:** `darkplaces/model_shared.c` (loader table 45–65), `model_alias.c` (loaders), `model_alias.h` (structs), `qcsrc/common/models/all.inc` (MODEL() registrations).
- **Port current state:** Port ships ONLY MDL=19 (identical Base list), ZYM=0, MD2=0, PSK=0. `AssetLoader.BuildModelFactory()` (258–303) dispatches by magic (MagicIqm/MagicDpm/MagicMd3) and falls through to null + "is not a known model" log (line 301). Two client systems already skip MDL with fallbacks: `ShellCasings.cs:84` (casing_shell.mdl → GeneratedCasing) and `ModelGibs.cs:107` (chunk.mdl → GeneratedChunk). No other MDL/MD2/ZYM/PSK references.
- **Files to create / edit:** none.
- **Seam:** `AssetLoader.BuildModelFactory()` 271–303 magic-dispatch chain; an MDL importer would add an `if (magic.StartsWith(MagicMdl))` clause (MagicMdl="IDPO") before the final error log.
- **Risks / finding:** The two MDL files actually in use already have deliberate skip+fallback rendering; the other 17 are dead asset weight. The 2 ZYM files are map-exclusive addons absent from the shipped base game; MD2/PSK have zero shipped content. Cost to implement ~2000 LOC (≈500/format ×4) for nil benefit. **Recommendation: FORMALLY CUT all four formats.** Exception risk: future custom ZYM/PSK maps silently fall back to placeholder geometry — acceptable (matches QC's gib-splash fallback precedent).
- **Test plan (verification, no code):** Confirm ShellCasings/ModelGibs fallbacks render when the loader returns null (`LoadModel("models/casing_shell.mdl")` expects null + fallback). Verify no error spam from 19 MDL shipments (boot a map, only gibs/casing messages appear). Document the cut with a comment in `BuildModelFactory()` explaining why MDL/MD2/ZYM/PSK are not implemented. Feel check: kill a player (gibs) and fire a shotgun (casings) — both use generated meshes, no errors.

---

## T27 — HUD Configure-Mode Editor: Drag/Resize/Undo + Skin-List Backend

- **Verdict:** port
- **Complexity:** large
- **Base behavior:** `hud_config.qc` is the CSQC configure-mode overlay over `hud_panel_*_pos/_size` cvars. Input pipeline (`HUD_Panel_InputEvent`, from main.qc:504): on `_hud_configure=1` intercepts input — mouse updates global mousepos + triggers Highlight; keyboard ESC/Ctrl+Backspace/Ctrl+Tab(cycle)/Ctrl+Space(toggle)/Ctrl+C/V(copy size)/Ctrl+Z(undo)/Ctrl+S(save)/arrows(move/resize, grid-snapped). Mouse drag/resize (`HUD_Panel_Mouse`, every frame): Highlight detects panel+edge → SetPos/SetPosSize recompute cvars with collision snapping (CheckMove/CheckResize) and grid snapping (grid applied BEFORE collision). Frame wiring (`HUD_Configure_Frame`): init on entry, sync _menu_alpha, draw grid, cleanup on exit. All edits write cvars directly (normalized 0..1). Undo is one-level (panel_pos/size_backup, Ctrl+Z restores). Export (`HUD_Panel_ExportCfg`): Ctrl+S / "hud save" writes `data/data/hud_<skin>_<cfg>.cfg`. PostDraw renders highlight border, center-line guide, tab highlight, grid. Shipped cvars: _hud_configure=0, hud_configure_checkcollisions=1, hud_configure_grid=0, grid_xsize=0.01, grid_ysize=0.011, grid_alpha=0.15, vertical_lines=0.5, teamcolorforced=0.
- **Base sources:** `qcsrc/client/hud/hud_config.qc` (InputEvent 532–805, Mouse 953–1057, Frame 1087–1120, PostDraw 1160–1178, SetPos/SetPosSize/CheckMove/CheckResize, ExportCfg 10–84, Arrow_Action 411–521, Highlight 872–945, FirstInDrawQ 842–870), `hud_config.qh` (cvars + globals + HUD_Write macros), `hud.qc` (HUD_Main 682, calls Frame 696 + PostDraw 821), `hud.qh` (UpdatePosSize macro, panel flags, REGISTER_HUD_PANEL), `main.qc:504`.
- **Port current state:** Partially wired. `HudConfig.cs` registers most cvar defaults but MISSING `_hud_configure` and `hud_configure_checkcollisions`. `DialogHudSetupExit.cs` (menu-side exit/settings dialog) is DONE with real cvar bindings, but the HUD skin LIST is a placeholder note (no file-scan backend); Refresh/Set/Save route inert. `HudManager.cs` discovers/creates panels but has NO ConfigEditor field/call/input hook. `HudPanel.cs` has LoadConfig (cvar→pixels) but no interactive drag/resize. `NetGame._UnhandledInput` (2528) does not call any HUD editor. `HudLayoutDefaults.cs` holds luma defaults; `HudSkin.cs` resolves border images but has no skin enumeration/save. Missing entirely: HudConfigEditor state machine + input handler, cvar write-back, collision/grid snapping, undo, export, input hook, frame wiring, skin-list backend.
- **Files to create:** `game/hud/HudConfigEditor.cs`
- **Files to edit:** `game/hud/HudConfig.cs`, `game/hud/HudManager.cs`, `game/net/NetGame.cs`, `game/menu/framework/HudPanelCommon.cs`, `game/hud/HudPanel.cs`
- **Seam:** Add missing cvar defaults to `HudConfig.RegisterDefaults` (after line 33). In `HudManager._Ready` (after SyncSkin, line 125) instantiate `ConfigEditor = new HudConfigEditor()` + AddChild; in `_Process` (after 145) call `ConfigEditor.Update(vp, fade)` before the panel loop. In `NetGame._UnhandledInput` (2528) route input to `hud.ConfigEditor?.HandleInput(@event)`. In `HudPanelCommon.BuildCommon` add a skin-list backend hook (TODO defer). In `HudPanel.cs` add `IsHighlighted`/`IsTabSelected` properties for the editor's _Draw.
- **Risks:** Input ordering — ConfigEditor must be findable in the tree. Cvar atomicity — batch per-frame writes / defer LoadConfig throttle during configure to avoid stutter. Snapping order — grid BEFORE collision (matches QC 364–368). Panel registry — T10/T29 panels read the same _pos/_size cvars and must see changes immediately. Menu-alpha lerp (UpdatePosSize_ForMenu) must replicate. **Shared HudManager/HudPanel/HudConfig with T41** — coordinate edits. Undo stays one-level.
- **Test plan:** xUnit — SetPanelPos/Size cvar write+normalization; grid snapping (before collision); CheckPanelCollision four corners; OnArrowKey step sizing (grid vs pixel-accel, Ctrl+Shift reverse); OnCtrlZ undo restore + re-backup; OnCtrlTab band-ordered cycling + Shift reverse; OnCtrlSpace enable/disable toggle; OnCtrlS export command. Headless feel — `_hud_configure 1` shows grid+borders; mouse highlight; drag moves (cvar/frame); edge + collision snap; grid quantize; keyboard move/resize; Ctrl+Tab cycle; Ctrl+Space disable; Ctrl+Z restore; Ctrl+S logs save; `_hud_configure 0` keeps positions; reload defaults jumps back.

---

## T66 — Fire the FilterItem item-spawn hook

- **Verdict:** partial-wire
- **Complexity:** small
- **Base behavior:** In `StartItem` (`qcsrc/server/items/items.qc:1007`), after ItemInit and the IT_*/weapon/FL_ITEM seeding (1018–1026), the FilterItem hook fires (line 1031): `if(MUTATOR_CALLHOOK(FilterItem, this)) { delete(this); return; }` — BEFORE the have-pickup-item gate (1037+). Handlers inspect instanceOfHealth/Armor/Powerup and return true to forbid the spawn; on true the item is deleted, the function returns early, and `startitem_failed=true`.
- **Base sources:** `qcsrc/server/items/items.qc` StartItem (1007–1035) — FilterItem at 1031, after ItemInit (1018–1019), before have_pickup_item (1037).
- **Port current state:** `StartItem.SpawnInternal` (`src/XonoticGodot.Common/Gameplay/Items/StartItem.cs:61–124`) has the right structure but line 78 is a DEAD comment ("no FilterItem hook chain in the port"). ClassName set at 66, ItemInit at 70, flags at 73–76; hook never called. Six live subscribers registered: NixMutator (107), MeleeOnlyMutator (~67), HookMutator (~151), Mayhem (561), TeamMayhem (316), and Duel.FilterItem (Duel.cs:79, defined but NEVER registered). All read `args.Definition.ClassName/NetName`. `MutatorHooks.FilterItemDefinition` chain exists (MutatorHooks.cs:357) with Call() but is never invoked.
- **Files to create:** none. **Files to edit:** `src/XonoticGodot.Common/Gameplay/Items/StartItem.cs`
- **Seam:** In `StartItem.SpawnInternal`, after line 76 (`item.Flags |= EntFlags.Item`) and before line 101 (isLoot branch): (1) build `FilterItemDefinitionArgs(item)`; (2) `MutatorHooks.FilterItemDefinition.Call(ref args)`; (3) if true → `ItemPickupRules.RemoveItem(item)` and `return null` (matches QC delete+return). Must run BEFORE `SetupPermanent` (line 108) so the item dies before the have-pickup gate.
- **Risks:** Subscribers read `args.Definition.NetName`, but NetName is assigned at line 114 — after the hook fires. MeleeOnly and HookMutator read both ClassName and NetName, so NetName would be null/empty at hook time. **Mitigation:** assign `item.NetName = def.NetName` BEFORE the hook (move line 114 earlier or add before line 78). Duel.FilterItem is unregistered, so it won't fire (documentation note, separate follow-up).
- **Test plan:** xUnit — NIX active → item_health deleted; `g_nix_with_healtharmor=1` → item exists; MeleeOnly → item_health_small deleted, item_health_big exists; assert Duel.FilterItem NOT called. Headless — load stock map with NIX (no health/armor unless cvar-enabled) and Melee (small items stripped); run full GameWorld.Boot → ItemSpawnFuncs.Spawn → StartItem.Spawn and confirm filters enforced.

---

## T67 — HLAC crouch-spread modifier

- **Verdict:** port
- **Complexity:** small
- **Base behavior:** `W_HLAC_Attack` (primary) computes `spread = min(spread_min + spread_add*misc_bulletcounter, spread_max)` (31–32), then `if (IS_DUCKED(actor) && IS_ONGROUND(actor)) spread *= WEP_CVAR_PRI(WEP_HLAC, spread_crouchmod)` (33–34). `W_HLAC_Attack2` (secondary) reads `spread = WEP_CVAR_SEC(...spread)` (77) then the same gate (79–80). Both gate the multiplier on actor being BOTH ducked AND on ground, applied AFTER all other spread calc, immediately before projectile velocity setup.
- **Base sources:** `qcsrc/common/weapons/weapon/hlac.qc` — W_HLAC_Attack (27–73, gate 33–34), W_HLAC_Attack2 (75–126, gate 79–80).
- **Port current state:** `Hlac.cs` seeds SpreadCrouchmod cvars correctly (Primary 0.25 / Secondary 0.5 at 92/107). Primary `Attack()` (143–160) computes spread at 148 but NEVER applies the modifier (line 149 comment flags the gap). Secondary `Attack2()` (163–182) reads Secondary.Spread at 177 and passes it directly to the bolt loop with no gate. No CrouchSpreadMod() helper. Verified: lines 148 (primary) and 177 (secondary) pass unmodified spread.
- **Files to create:** none. **Files to edit:** `src/XonoticGodot.Common/Gameplay/Weapons/Hlac.cs`
- **Seam:** (1) Primary `Attack()`: after line 148 apply the crouch modifier before SpawnBolt (line 156). (2) Secondary `Attack2()`: after line 177 apply the modifier to spread in the loop. Follow `Machinegun.cs:CrouchSpreadMod()` (261–262): `(actor.IsDucked && actor.OnGround) ? Cvars.SpreadCrouchmod : 1f;`. Add a CrouchSpreadMod(Entity actor) helper to Hlac.cs using `Primary.SpreadCrouchmod` / `Secondary.SpreadCrouchmod` per call site, called inline at each spread assignment.
- **Risks:** MINIMAL — purely multiplicative, isolated, mirrors shipped Machinegun pattern. No shared registries/state/hooks. `WeaponFireGate.cs` only uses CheckAmmo (unaffected). IsDucked/OnGround already maintained by physics; cvars seeded at Configure().
- **Test plan:** xUnit — HlacCrouchSpreadTest for primary+secondary, asserting ×SpreadCrouchmod ONLY when IsDucked && OnGround (0.25 primary, 0.5 secondary), unmodified otherwise. Headless — equip HLAC, stand (X), crouch-grounded (X×0.25 / X×0.5), jump-crouch (X, no gate), confirm projectile spread visually + network diffs.

---

## CONTENTION MAP

Files appearing in more than one task's `filesToEdit` or `sharedFiles`. Paths normalized; tasks listed are those contending.

| File | Contending tasks | Notes |
|------|------------------|-------|
| `src/XonoticGodot.Server/GameWorld.cs` | **T41, T42** | T41 ticks AnnouncerController in OnStartFrame; T42 rewrites CheckRulesAndIntermission. Distinct methods — owner-edits viable, but both in the same hot server file. |
| `src/XonoticGodot.Server/Cvars.cs` | **T42** (filesToEdit + sharedFiles) | Single-task within A4, but it is the project-wide cvar registry — high cross-wave contention; treat as a shared seam. |
| `src/XonoticGodot.Common/Gameplay/GameTypes/GameType.cs` | **T42** (filesToEdit + sharedFiles) | Single A4 task; the abstract equality hook lands here. Base class touched by all 14 gametypes in T42. |
| `game/net/NetGame.cs` | **T41, T27** | **Known overlap.** T41 wires HitSound.OnHit; T27 inserts a HUD-editor input route in `_UnhandledInput` (line 2528). Different regions but the same large client file. |
| `game/hud/CrosshairPanel.cs` | **T41** | Single A4 task (objective rings). Listed for completeness; not contended within A4. |
| `src/XonoticGodot.Common/Gameplay/Damage/DamageSystem.cs` | **T41** (filesToEdit + sharedFiles) | Single A4 task; flagged shared (other waves' damage work). |
| `src/XonoticGodot.Server/Commands.cs` | **T46** (+ noted external T38/T47/T56/T60/T70) | Single A4 task here, but a known multi-wave command-registration hot file. |
| `game/net/ServerNet.cs` | **T46** (+ noted external T34/T54) | Single A4 task here; shared network file across waves. |
| `src/XonoticGodot.Common/Gameplay/MapObjects/MapObjectsRegistry.cs` | **T52** (filesToEdit + sharedFiles) | Single A4 task; registry seam. |
| `src/XonoticGodot.Common/Gameplay/Items/ItemSpawnFuncs.cs` | **T52** (filesToEdit + sharedFiles) | Single A4 task; registry seam. |
| `game/menu/ServerBrowser.cs` | **T23** (+ noted external T50) | Single A4 task here; selected-row read shared with menu-command wave. |
| `game/hud/HudManager.cs` | **T27** (sharedFiles, + noted T41) | T27 owns; T41 *may* touch HUD panels — coordinate. |
| `game/hud/HudPanel.cs` | **T27** (sharedFiles, + noted T41) | Same coordination note as HudManager. |
| `game/hud/HudConfig.cs` | **T27** (sharedFiles, + noted T41) | Same. |
| `game/hud/HudLayoutDefaults.cs` | **T27** (sharedFiles) | Single A4 task. |
| `src/XonoticGodot.Common/Gameplay/Weapons/WeaponFireGate.cs` | **T67** (sharedFiles) | Read-only by T67; not contended. |

**True intra-A4 multi-task contention** (two A4 tasks editing the same file): **`game/net/NetGame.cs` (T41 ↔ T27)** and **`src/XonoticGodot.Server/GameWorld.cs` (T41 ↔ T42)**. Everything else is single-A4-task; the remaining shared flags warn of cross-wave (non-A4) contention.

---

## Recommended Implementation Batching

### Fully disjoint — parallel-safe (no shared file with any other A4 task)

- **T46** (chat) — Chat.cs / Commands.cs / Player.cs / ServerNet.cs. No A4 overlap (Commands/ServerNet shared only with non-A4 waves).
- **T52** (compat remaps) — CompatRemaps.cs / ItemSpawnFuncs.cs / MapObjectsRegistry.cs. Registry-local, no A4 overlap.
- **T66** (FilterItem hook) — StartItem.cs only.
- **T67** (HLAC crouch-spread) — Hlac.cs only.
- **T23** (menu data-lists) — menu dialogs + DataSource/MapInfo + ServerBrowser. No A4 overlap (ServerBrowser shared only with non-A4 T50).
- **T24** (importer cut) — no code; verification + a one-line comment. Effectively zero contention.

These six can run concurrently. Caveat: T52 and T66 both live in `.../Items/` but edit **different files** (ItemSpawnFuncs.cs/MapObjectsRegistry.cs vs StartItem.cs) — safe in parallel; just avoid a shared refactor of the item-spawn pipeline.

### Must serialize or use owner-edits-file / return-snippets

- **T41 ↔ T42 share `GameWorld.cs`.** Run one as the file **owner** (recommend **T42**, since it rewrites CheckRulesAndIntermission — the larger change), and have **T41** return its AnnouncerController tick snippet (a single `OnStartFrame` line) for the owner to splice. Their other files (T41: NetEntity/DamageSystem/NetGame/CrosshairPanel; T42: Cvars/GameType/14 gametypes/OverTimeManager) are otherwise disjoint.
- **T41 ↔ T27 share `game/net/NetGame.cs` (the known overlap).** Regions differ — T41 adds a HitSound call, T27 inserts a HUD-editor route in `_UnhandledInput` (line 2528). Designate a single NetGame owner (recommend **T27**, whose input-hook edit is structurally load-bearing) and have **T41** return its HitSound.OnHit snippet for splicing. Because T41 touches BOTH contended files, **T41 should be the snippet-returner** and run last, or be the single integration point that merges T42's GameWorld and T27's NetGame edits.
- **T27** additionally flags `HudManager/HudPanel/HudConfig` as possibly co-touched by T41. T41's actual A4 file list does NOT include the HUD panel files (only CrosshairPanel.cs), so this is a soft warning — **no hard serialization needed**, but T27 owns all `game/hud/*` files and T41 must stay out of them.

### Cross-wave shared seams to flag to the orchestrator (not A4-internal blockers)

- **`Cvars.cs`** (T42) and **`GameType.cs`** (T42) — project-wide; if other waves register cvars/gametype hooks concurrently, route through an owner.
- **`Commands.cs`** (T46, with T38/T47/T56/T60/T70) and **`ServerNet.cs`** (T46, with T34/T54) — multi-wave command/network registration; use additive append-only edits.
- **`ServerBrowser.cs`** (T23, with T50) — T50 (menu_cmd) must define handlers before T23's buttons go live; T23 ships honest-inert until then.

### Suggested wave order

1. **Batch 1 (parallel):** T46, T52, T66, T67, T23, T24, plus T42 and T27 (each owns its hot file).
2. **Batch 2 (integration, serial):** T41 last — it splices its GameWorld snippet into T42's rewrite and its NetGame snippet into T27's input hook, then finishes its disjoint files (NetEntity/DamageSystem/CrosshairPanel).

### verify-then-maybe-cut tasks

- **T24** — verdict **verify-then-maybe-cut**; **recommendation: FORMALLY CUT** MDL/MD2/ZYM/PSK. The two MDL files in active use (casing_shell.mdl, chunk.mdl) already have deliberate skip+fallback rendering; the other 17 MDL files are dead weight; the 2 ZYM files are map-exclusive addons not in the shipped base game; MD2/PSK have zero shipped content. ~2000 LOC for nil benefit. Action: document the cut with a comment in `AssetLoader.BuildModelFactory()` and verify the two fallbacks render cleanly — no importer implementation.
