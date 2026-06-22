# cl-hud — parity spec

**Base refs:** `client/hud/hud.qc`, `client/hud/hud.qh`, `client/hud/hud_config.qc`, `client/hud/panel/*.qc` (weapons, ammo, powerups, healtharmor, notify, timer, radar, score, racetimer, vote, modicons, pressedkeys, chat, engineinfo, infomessages, physics, centerprint, pickup, itemstime, checkpoints, quickmenu, scoreboard, strafehud)
**Port refs:** `game/hud/*.cs` (HudPanel, HudManager, HudLayoutDefaults, HudConfig, HudRegistry, HudSkin, HudDynamicShake, HudNotifications, + one *.cs per panel) wired by `game/net/NetGame.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The CSQC on-screen HUD: a registry of panels (`hud_panels`), each laid out and skinned from `hud_panel_<id>_*`
cvars, drawn each frame by `HUD_Main` in a fixed z-order with a PANEL_SHOW gate (maingame / minigame /
mapvote / with-scoreboard) and a scoreboard cross-fade. The HUD also hosts the dock background, the
damage-keyed whole-screen shake (`Hud_Dynamic_Frame`), the configure-mode editor (`hud_config.qc`), and the
9-slice skin-border drawing (`draw_BorderPicture`). Panels read networked stats (`STAT(HEALTH)` etc.) and
client caches; almost everything here is **presentation** (client-only), with a few **shared** read helpers.

The port is a faithful, well-architected re-implementation: a `HudPanel` base + reflection-discovered
registry (`HudRegistry`), a luma defaults table (`HudLayoutDefaults`), per-frame cvar-driven `LoadConfig`,
the skin 9-slice (`HudSkin`), and one C# panel per QC panel. It is live on the real net path
(`NetGame._fullHud`, CanvasLayer 4) — but a handful of data-driven panels have full code yet **no live feeder**
(VotePanel, MapVotePanel, RaceTimerPanel, CheckpointsPanel), and a few engine systems are unported
(dock, dynamic-follow, full minimap render).

## Base algorithm (authoritative)

### HUD engine / panel framework (`hud.qc:HUD_Main`, `hud.qh:REGISTER_HUD_PANEL`)
- **Registry:** 28 gameplay panels registered with `REGISTER_HUD_PANEL(id, draw, configflags, showflags)`
  (`hud.qh:250-277`, WEAPONS..CHECKPOINTS) plus 4 minigame panels (MINIGAMEBOARD/STATUS/HELP/MENU) = 32 total
  registrations. Each gets
  `panel_name = strtolower(#id)`, the cvar prefix `hud_panel_<name>_`, `PANEL_CONFIG_*` (MAIN, CANBEOFF) and
  `PANEL_SHOW_*` flags (MAINGAME=BIT0, MINIGAME=BIT1, MAPVOTE=BIT2, WITH_SB=BIT3).
- **Per-frame (`HUD_Main`):** `hud_fade_alpha = 1 - autocvar__menu_alpha`; if `myteam` changed, recolor +
  force all panels' `update_time = time`. Early-out if scoreboard fully up AND menu fully up. Draw the dock if
  `hud_dock != "0"`. Build/cache `panel_order` from `_hud_panelorder`. Draw panels **in reverse order** (so
  order[0] ends on top), then `HUD_Vehicle()`, then the maximized panels on top: radar (if maximized), chat
  (if `_con_chat_maximized`), quickmenu (if open), and **always** scoreboard last. Manage cursor mode.
- **`HUD_Panel_Draw(panent)` gate:** precedence — scoreboard-up (WITH_SB) → active-minigame+menu-open
  (MINIGAME) → intermission==2 (MAPVOTE) → MAINGAME. Non-WITH_SB panels get `panel_fade_alpha =
  1 - scoreboard_fade_alpha` (hidden when 0); WITH_SB panels stay at 1 (or fade to `scoreboard_fade_alpha`
  during a mapvote).
- **`HUD_Panel_LoadCvars` (throttled `hud_panel_update_interval`=2s, every frame in configure):** read
  `_pos`/`_size` (normalized 0..1 → ×`vid_conwidth/height`), `_bg` ("" inherit `hud_panel_bg`; "0" off; else
  `<skin>/<name>` with fallback to `border_default`), `_bg_color` ("" inherit; `shirt`/`pants`/team blend;
  else `stov`), `_bg_color_team`, `_bg_alpha`, `_bg_border`, `_bg_padding` (clamped
  `min(min(size)*0.5-5, padding)`), `_fg_alpha`. Multiply bg/fg alpha by `hud_fade_alpha * panel_fade_alpha`.
- **`HUD_Panel_DrawBg` → `draw_BorderPicture`:** 9-slice the resolved bg expanded by `panel_bg_border` on all
  sides, corner slice = `BORDER_MULTIPLIER(4) * border`, tinted `panel_bg_color` at `panel_bg_alpha`.
- **`HUD_Panel_DrawProgressBar`:** horizontal/vertical progress bar with baralign 0/1/2/3 (left/right/center/
  center-bidirectional), using skin `progressbar`/`progressbar_vertical`/`accelbar`/`num_leading`.
- **`HUD_Get_Num_Color(hp, max, blink)`:** green→lightgreen→white→lightyellow→red gradient by percent, with a
  blink at ≥100% and a stronger pulse <25%.
- **`Hud_Dynamic_Frame`:** (a) **shake** — health-loss ≥ `hud_dynamic_shake_damage_min`(10) latches a factor
  `(loss-m)/(max-m)` (`damage_max`=130), plays a fixed 9-keyframe polyline at speed `17+9*factor`, scaled by
  `factor * hud_dynamic_shake_scale`(0.2), bound ±0.1·viewport, strongest hit wins, suppressed at
  intermission and for the frame after a reset. (b) **dynamic-follow** (`hud_dynamic_follow`) — shifts/scales
  the whole HUD by the view's followmodel offset.
- **Configure mode (`hud_config.qc`):** `_hud_configure 1` shows a grid + lets the user drag/resize/keyboard-
  move panels (writes `hud_panel_<id>_pos/_size`), snap to grid (`hud_configure_grid_*`), collision-test
  (`hud_configure_checkcollisions`), Ctrl+Tab cycle, per-panel settings dialog, export to a skin .cfg.

### Panels (each `panel/<name>.qc`)
- **weapons (#0):** grid of owned weapons (or all if `_onlyowned 0`), best-aspect layout
  (`HUD_GetTableSize_BestItemAR`), current-weapon bg (`weapon_current_bg`) with a sliding selection animation
  (`_selection_speed`10/`_selection_radius`), per-weapon ammo clip bar (`_ammo`,`weapon_ammo`), accuracy tint
  overlay (`_accuracy`,`weapon_accuracy`, color levels from `accuracy_color*`), label modes 0..3
  (`_label`2/`_label_scale`0.3), complainbubble (out-of-ammo/don't-have/unavailable, 3 colors, `_time`/
  `_fadetime`), timeout fade/move when idle (`_timeout`1/`_timeout_effect`1, fadebg/fgmin 0.4, speed_in 0.25/
  out 0.75), ghost icons for unowned, noncurrent alpha/scale.
- **ammo (#1):** per-resource icons+counts (shells/bullets/rockets/cells/fuel) or only-current (`_onlycurrent`),
  current bg (`ammo_current_bg`), optional clip bar (`_progressbar`, `_maxammo`40), nade bonus display,
  iconalign, low-ammo red text (<10), infinite (∞) when `IT_UNLIMITED_AMMO`, noncurrent alpha 0.6/scale 0.4.
- **powerups (#2):** mutator-fed linked list of active powerups (strength/shield/etc.), best-aspect grid,
  per-powerup progress bar in its color, countdown DrawNumIcon (∞ for infinite, expanding flash ≤5s),
  iconalign/baralign.
- **healtharmor (#3):** combined ("ideal max damage") or split health/armor bars+numbers, fuel bar, underwater
  oxygen bar (blink out-of-air), damage-flash ghost bar (`_progressbar_gfx_damage`5), smooth lerp
  (`_progressbar_gfx_smooth`2), low-health pulse (`_progressbar_gfx_lowhealth`40 → `blink(0.85,0.15,9)`),
  flip/baralign 3/iconalign 3, maxhealth/maxarmor 200, number tint by `HUD_Get_Num_Color`.
- **notify (#4):** circular buffer (10) of kill-feed lines (attacker/victim/icon), per-line fade
  (`_time`10/`_fadetime`3), flip, color-coded names shortened to width, fade out faster at intermission.
- **timer (#5):** main clock (count-down to timelimit or count-up `_increment`), color warning red/yellow,
  warmup/overtime/sudden-death/timeout subtext, secondary round timer for round modes (`_secondary`).
- **radar (#6):** minimap render of the BSP minimap + g_radaricons + entcs player blips + own player,
  rotation modes, zoommode, scale 8192/maximized_scale 5120/maximized_size, clickable maximized (ons spawn /
  teleport select), foreground alpha.
- **score (#7):** rankings leaderboard (place/score, self highlighted green/yellow/red) OR ffa distribution
  (own score + gap) OR race time + delta OR team scores; `_rankings` 0/1.
- **racetimer (#8):** running lap time (bold, count-up + penalty accum), most-recent checkpoint split
  (`MakeRaceString`, faded 2s), anticipation of next cp vs record, penalty line; gated by `ShowRaceTimer`.
- **vote (#9):** callvote question + yes/no key+count + progress bars (`voteprogress_back/prog/voted`),
  needed count, fade in/out, `_alreadyvoted_alpha`; also the uid2name dialog.
- **modicons (#10):** per-gametype icons via `gametype.m_modicons` (CTF flags, dom points, CA/freezetag alive
  counts, keyhunt keys, assault), fade `mod_alpha`.
- **pressedkeys (#11):** 8-key WASD+jump+crouch+(atck1/2) grid using `key_*`/`key_*_inv` art, `_aspect`1.8,
  `_attack`0, enable 0/1/2 (off/spectating-only/always).
- **chat (#12):** drives the engine chat rect (`con_chatrect`/`con_chatwidth`/`con_chat`/`con_chatsize`8/
  `con_chattime`30), maximized full-height mode with scroll, intermission resize.
- **engineinfo (#13):** FPS readout (moving-average or window), off by default.
- **infomessages (#14):** observing/spectating/press-to-join/warmup/ready-up/game-starts-in/team-unbalanced/
  spectator-list lines, cycling group hints, flip.
- **physics (#15):** speedometer (2d/3d, unit `hud_speed_unit`), topspeed memory (`_topspeed`/`_topspeed_time`4
  + progressbar peak), jumpspeed, acceleration bar in g (`accelbar`, `_acceleration_max`1.5), speed bar,
  flip/baralign, speed-colored text, layout auto/horizontal/vertical; enable 3 (Race/CTS show-mode).
- **centerprint (#16):** circular buffer (10) of priority messages with fade in/out (`_fade_in`0/`_fade_out`
  0.15/`_time`3), id-keyed replace, countdown `^COUNT`, title (+ duel "vs" title), bold/title font scales,
  align 0.5, flip, subsequent-fade, font-size-by-alpha, follows below radar/scoreboard.
- **itemstime (#17):** per-item respawn timers (mega health, large armor, powerups) icons + countdown +
  progressbar; enable 2 (spectating-only/always).
- **quickmenu:** nested quick-chat menu from a quickmenu file, key nav + click, align, translatecommands.
- **scoreboard:** full table — per-player columns (configurable `scoreboard_columns`), team totals, spectator
  list, per-weapon accuracy rows, item-stats rows, game-info footer, self/eliminated highlight, ping/pl,
  fade in/out (`_fadeinspeed`10/`_fadeoutspeed`5), respawn timer.
- **strafehud:** strafe-quality bar (neutral/accel/overturn zones), angle + best-angle indicator, wturn/switch
  indicators, mode/style/range; enable 3.
- **pickup (#26):** recent-pickup line (icon + name + ×count) faded over `_time`3 / `_fade_out`0.15, optional
  item timer, iconsize 1.5.
- **checkpoints (#27):** race checkpoint splits list (flip/align/fontscale), delta coloring.

## Port mapping
| Base | Port | Layer | Live? |
|---|---|---|---|
| `HUD_Main` panel walk + z-order + gate | `HudManager._Process` (per-frame LoadConfig + ResolveVisible + QueueRedraw) | presentation | live (NetGame `_fullHud`) |
| `REGISTER_HUD_PANEL` registry | `HudRegistry` (reflection-discovered) + `HudLayoutDefaults` table | presentation | live |
| `HUD_Panel_LoadCvars` | `HudPanel.LoadConfig`/`Resolve` (throttle-by-dirty + viewport) | presentation | live |
| `draw_BorderPicture` 9-slice | `HudSkin.DrawBorderPicture` + `HudPanel.DrawBackground` | presentation | live |
| `HUD_Panel_DrawProgressBar` | `HudPanel.DrawBar` (simplified flat bar) | presentation | live, partial |
| `HUD_Get_Num_Color` | `HudPanel.NumColor` (simplified gradient, no blink) | presentation | live, partial |
| `Hud_Dynamic_Frame` shake | `HudDynamicShake` (faithful) | presentation | live |
| `Hud_Dynamic_Frame` follow | NOT IMPLEMENTED | presentation | na |
| dock draw | NOT IMPLEMENTED (cvars registered, never drawn) | presentation | dead |
| `hud_config.qc` editor | `HudConfigEditor` | presentation | live (`_hud_configure`) |
| weapons/ammo/powerups/healtharmor/physics | `*Panel.cs` (fed via `Hud.SetPlayer`) | presentation | live |
| notify + centerprint | `NotifyPanel`/`CenterPrintPanel` via `HudNotifications` | presentation | live |
| timer | `TimerPanel` (fed `FeedTimer`) | presentation | live |
| infomessages | `InfoMessagesPanel` (fed `FeedInfoMessages`) | presentation | live |
| score | `ScorePanel` (fed `FeedScorePanel`) | presentation | live |
| modicons | `ModIconsPanel` (fed from match state) | presentation | live |
| itemstime | `ItemsTimePanel` (fed `SetItemTimes`) | presentation | live |
| pickup | `PickupPanel` (fed) | presentation | live |
| chat | `ChatPanel.AddLine` (own scroll render, not engine rect) | presentation | live (intended divergence) |
| quickmenu | `QuickMenuPanel` | presentation | live |
| strafehud | `StrafeHudPanel` (fed) | presentation | live |
| scoreboard | standalone `_scoreboard` (manual `Configure` rect, fed from `ClientNet.LatestScoreboard`); `_fullHud.Scoreboard` unfed | presentation | live (partial layout) |
| radar | standalone `_radar` (`_fullHud` radar hidden); minimap render | presentation | live, partial |
| vote | `VotePanel` (full API) | presentation | **dead — no client feed** |
| mapvote | `MapVotePanel` (full API) | presentation | **dead — no NetGame feed** |
| racetimer | `RaceTimerPanel` (full API) | presentation | **dead — no race-split network feed** |
| checkpoints | `CheckpointsPanel` (full API) | presentation | **dead — no feed** |
| engineinfo | `EngineInfoPanel` (off by default; port also has `FpsPanel`) | presentation | live (off) |
| minigame BOARD/STATUS/HELP/MENU | `MinigameRenderer` (board+status merged) + `MinigameMenu` + `MinigameHelpPanel`, driven by `MinigameClient` | presentation | live |
| `HUD_Vehicle`/`vr_hud` | `VehicleHud` | presentation | partial |

## Parity assessment

### Gaps (concrete, observable)
- **VotePanel never appears in a match.** No vote state is networked to the client (`ClientNet` has no vote
  fields) and `NetGame` never sets `_fullHud.Vote.Active/CalledVote/YesCount/NoCount/Needed`. A real callvote
  shows no on-screen yes/no progress bar — only the server-side `VoteController` exists.
- **MapVotePanel never appears at intermission.** `NetGame` has zero `MapVote`/`MapVotePanel` references; the
  intermission map-grid + vote-counts + countdown are not shown to the player.
- **RaceTimerPanel + CheckpointsPanel are dead.** Their full `Race*`/split APIs are never fed (no race-split
  networking on the client). In Race/CTS the live checkpoint split deltas and running lap timer panel do not
  render (the Score panel's race branch covers the record line only).
- **No HUD dock.** `hud_dock`/`dock_medium` is registered + menu-exposed but never drawn — enabling the dock
  has no visual effect.
- **No dynamic-follow.** `hud_dynamic_follow` (HUD sway with the viewmodel) is unported; the shake half is faithful.
- **Progress bars are flat fills, not the skin `progressbar` 9-slice.** `DrawBar` draws a plain rect, not the
  rounded skin bar with baralign 0/1/2/3 semantics — health/ammo/physics bars look flatter than Base and the
  center-bidirectional accel bar (baralign 3) and vertical-bar path are approximated per panel.
- **`NumColor` lacks the blink** (≥100% sine flash + <25% pulse) and uses a 2-stop approximation of Base's
  5-stop gradient, so number tints differ slightly.
- **Scoreboard does not use the cvar layout.** The live `_scoreboard` is placed by a hardcoded `Configure`
  rect in `NetGame`, not `hud_panel_scoreboard_pos/_size`; `_fullHud`'s own discovered Scoreboard is unfed
  (avoids a double scoreboard). Moving the scoreboard via cvars/editor has no effect on the networked board.
- **Radar minimap fidelity / maximized clickable mode** are partial (placeholder/own-blip vs full BSP minimap
  + ons-spawn click-to-select).
- **Chat is a port re-render, not the engine chat rect.** `ChatPanel` keeps its own line buffer and draws with
  Xolonium; it does not drive `con_chatrect`/`con_chat*` like Base (intended — there's no DP engine chat here).

### Liveness
The HUD is genuinely live on the real net path: `NetGame` builds `_fullHud` (a `Hud`) on Layer 4, wires the
Xolonium font + bold font, feeds `SetPlayer` (health/armor/ammo/weapons/powerups/physics/crosshair), routes
notifications (centerprint + killfeed + announcer) through `HudNotifications`, and feeds timer, infomessages,
score, modicons, itemstime, pickup, chat, quickmenu, strafehud, minigamehelp. The configure editor is live on
`_hud_configure`. Dead-but-present panels: **vote, mapvote, racetimer, checkpoints** (full code, no feeder),
and the **dock** + **dynamic-follow** engine features.

### Intended divergences
- **Hybrid HUD (`NetHud` + `_fullHud`).** A lightweight `NetHud` draws health/armor + crosshair from
  networked stats so a pure `--connect` client (no local `Player`) still has a reticle/health; the skinned
  `_fullHud.HealthArmor`/`Crosshair` are suppressed in that case to avoid a double draw. Rationale: Godot
  panels need a local actor for `STAT(*)`, which a remote client lacks. (port architecture, contract §9)
- **Port-extra panels:** `FpsPanel`, `PingPanel`, `PositionPanel`, `ShownamesPanel` are not stock Xonotic
  panels; they self-manage visibility (DP `cl_showfps`/`cl_showping` style) and are exempt from the gate loop.
- **Chat as a port re-render** (above) — there is no DarkPlaces engine chat console to drive.
- **Scoreboard manual placement** (above) — the networked board owns its rect.

## Verification
- **Live-path wiring:** read `game/net/NetGame.cs` — font (`:1137`), `_fullHud` build (`:1147`), SetPlayer
  (`:2621`), notifications (`:1204`/`:3634`), score/modicons/itemstime/strafehud/chat/quickmenu/minigamehelp
  feeders present. Verified by source, not runtime.
- **Dead panels:** `grep` over `game/` + `src/` shows no caller sets `VotePanel`/`MapVotePanel` state and no
  `RaceTimer.`/`Checkpoints.` setter caller; `ClientNet` has no vote/mapvote/race-split fields. High confidence.
- **Constants:** `HudLayoutDefaults` + each panel's `RegisterDefaults` match the luma/REGISTER_HUD_PANEL values
  (pos/size/bg/showflags, healtharmor maxhealth 200 / gfx_damage 5 / lowhealth 40, etc.) — verified by diff.
- **Shake:** `HudDynamicShake` mirrors `hud.qc:562-655` line-for-line (keyframes, factor, bound, scale); has
  unit tests (`HudDynamicShakeTests`). High confidence faithful.
- Presentation fidelity of individual draws (progress-bar art, num-color blink, weapon selection slide) NOT
  runtime-verified — marked partial/unknown where the code visibly approximates Base.

## Open questions
- Should the dead networked panels (vote/mapvote/racetimer/checkpoints) be fed by extending `ClientNet`, or are
  they deliberately deferred? They compile + render correctly when given data, so this is purely a wire-up gap.
- Is the flat `DrawBar` an accepted simplification or a follow-up? Same for `NumColor` blink.
- Does any path drive `hud_panel_scoreboard_pos/_size`, or is the manual scoreboard rect permanent?
