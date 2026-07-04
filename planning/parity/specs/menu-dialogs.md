# Menu dialogs (settings + multiplayer) — parity spec

**Base refs:** `menu/xonotic/dialog_settings*.qc`, `menu/xonotic/dialog_multiplayer*.qc`
**Port refs:** `game/menu/dialogs/DialogSettings*.cs`, `game/menu/dialogs/DialogMutators.cs`,
`game/menu/dialogs/DialogServerInfo.cs`, `game/menu/dialogs/DialogCreateGameMapInfo.cs`,
`game/menu/dialogs/DialogMultiplayerProfile.cs`, `game/menu/MultiplayerScreen.cs`,
`game/menu/CreateGameScreen.cs`, `game/menu/framework/ClientSettings.cs`, `game/menu/framework/MenuCommand.cs`
**Reference rev:** `v0.8.6-1779-g863cd3e84`  ·  **Last audited:** 2026-07-02

## Overview

This unit audits the two big dialog trees at **behavior level**: the Settings dialog (Video / Effects /
Audio / Game{Crosshair,HUD,Messages,Models,View,Weapons,+DamageText} / Input / User / Misc, plus the
sub-dialogs userbind-edit, bindings-reset, hudconfirm, cvars-editor, factory-reset, language-warning) and
the Multiplayer dialog (Servers / Create{mapinfo, mutators} / Profile, plus server-info{status tab, ToS
tab}). For each dialog the questions are: (1) does the surface exist, (2) are the Base settings present,
(3) **does changing each setting take effect** — the port's known-weak axis. Widget-toolkit internals,
the one-off dialogs (quit/welcome/teamselect/…), and the serverlist/maplist/keybinder/skinlist widgets
are `menu-core`'s unit; effect-side fidelity of gameplay cvars belongs to the owning gameplay units
(cl-crosshair, cl-hud, fx-sounds, physics-player, weapon/mutator units) and is only spot-cited here.

## Base algorithm (authoritative)

### Settings container (`dialog_settings.qc:XonoticSettingsDialog_fill`)
- Two tab rows over a tab controller: Video/Effects/Audio (2 columns each), then
  Game/Input/User/Misc (1.5 each). No bottom button row; per-tab Apply buttons live inside tabs.

### Video tab (`dialog_settings_video.qc`)
- **Apply button** stages `_menu_vid_width/_menu_vid_height/_menu_vid_pixelheight/_menu_vid_desktopfullscreen`
  into the real `vid_*` cvars, runs `menu_cmd update_conwidths_before_vid_restart; vid_restart; menu_cmd sync`.
- Resolution comes from `slider_resolution.qc` enumerating **real display modes** (`getresolution()`).
- Controls: `vid_fullscreen`, `vid_borderless` (dep !fullscreen), `menu_vid_scale` (−1..1 named 9 steps),
  `r_stereo_redcyan`, `r_viewfbo` (Ex bit 2), `vid_samples` (Disabled/2x/4x), `gl_texture_anisotropy`
  (1/2/4/8/16), `r_depthfirst` (0/1/2), `gl_finish`, `vid_gl20`; color column `v_brightness` 0–0.5/0.02,
  `v_contrast` 1–3/0.05, `v_gamma` 0.5–2/0.05, `v_contrastboost` 1–5/0.1, `r_glsl_saturation` 0.5–2/0.05
  (all four gated on `vid_gl20`), `r_ambient` 0–20/0.25, `r_hdr_scenebrightness` 0.5–2/0.05; framerate
  `cl_maxfps` (128/256/512/1024/2048/Unlimited=0), `cl_minfps`, `cl_maxidlefps`, `vid_vsync` (checkbox),
  `showfps`.

### Effects tab (`dialog_settings_effects.qc`)
- **Preset row:** five command buttons `exec effects-{low,med,normal,high,ultra}.cfg`.
- Left column: `r_subdivisions_tolerance` (16/8/4/3/2/1), `cl_playerdetailreduction` (4..0),
  `gl_picmip` via the picmip slider (1337/2/1/0/−1/−2), `gl_texturecompression` (1/2/0), `r_sky`,
  `mod_q3bsp_nolightmaps`, `r_glsl_deluxemapping`, `r_shadow_gloss`, `r_glsl_offsetmapping`(+relief),
  `r_water` + `r_water_resolutionmultiplier` (0.25/0.5/1), `cl_decals` + `cl_decals_models` +
  `r_drawdecals_drawdistance` 200–500/20 + `cl_decals_fadetime` 1–20/1, `cl_damageeffect` (0/1/2).
- Right column: `r_shadow_realtime_dlight`(+shadows, +`makeMulti !gl_flashblend`),
  `r_shadow_realtime_world`(+shadows), `r_shadow_usenormalmap`, `r_shadow_shadowmapping`
  (enabled via the `someShadowCvarIsEnabled` predicate), `r_coronas` 0–1.5/0.1 +
  `r_coronas_occlusionquery`, `r_bloom`, `hud_postprocessing_maxbluralpha` (Ex 0.5 + multi `hud_powerup`),
  `r_motionblur` slider-checkbox (off 0 / saved 0.4), `cl_particles` + `cl_spawn_point_particles`
  (+multi `cl_spawn_event_particles`) + `cl_particles_quality` 0–3/0.25 +
  `r_drawparticles_drawdistance` 200–3000/200. Apply button = `vid_restart`.

### Audio tab (`dialog_settings_audio.qc`)
- Ten **decibel sliders** (−40..0 dB, `slider_decibels.qc`) on `mastervolume`, `bgmvolume`(+ch8 multi),
  `snd_staticvolume`(+ch9), `snd_channel{0,3,6,7,4,2,1}volume` (Info/Items/Pain/Player/Shots/Voice/
  Weapons, Weapons multi ch5 tuba); all non-master rows dep `mastervolume != 0`.
- `menu_snd_attenuation_method` (apply runs `snd_restart; snd_attenuation_method_${...}` — a cfg alias
  that sets `snd_soundradius/snd_attenuation_exponent/snd_attenuation_decibel`), `snd_mutewhenidle`,
  `snd_speed` (8 rates), `snd_channels` (Mono..7.1), `snd_swapstereo`, `snd_spatialization_control`,
  `cl_hitsound` checkbox + mode radios 1/2/3 (sendCvars), `con_chatsound`, `menu_sounds` (1 click /
  2 +hover), `cl_announcer_maptime` (0/1/5/3=Both), `cl_autotaunt` (0/0.35/0.65/1, sendCvars).

### Game tab host (`dialog_settings_game.qc` + `menu/gamesettings.qh`)
- A topic **listbox over the Settings registry** (`REGISTER_SETTINGS`) swapping the right panel;
  registrants: crosshair, hud, messages, model, view, weapons + the damagetext mutator's settings tab.

### Game sub-tabs (crosshair/hud/messages/model/view/weapons — one .qc each)
- Constants captured in the YAML rows. Notable mechanics:
  - **hud:** `HUDSetup_Check_Gamestatus` — in a match, `togglemenu 0; _hud_configure 1`; otherwise pop
    the **hudconfirm** dialog whose Yes runs `map _hudsetup` then `_hud_configure 1`.
  - **messages:** nearly every checkbox `makeMulti`-writes sibling `notification_*` cvars
    (powerups row = 14 cvars, weapons rows = 6 each, ANNCE rows = 7–10 each).
  - **view:** `cl_eventchase_death` is a checkboxEx with **on-value 2**; zoom family
    (`cl_zoomfactor` 2–10/0.5 + 11–30/1, `cl_zoomspeed` 1–8 + Instant −1, `cl_zoomsensitivity`,
    `cl_zoomscroll{,_scale,_speed}`, `cl_velocityzoom_*`, `cl_reticle`, `cl_unpress_zoom_*`).
  - **weapons:** the weaponslist priority editor + Up/Down; apply = `sendcvar cl_weaponpriority`;
    `cl_gunalign` radios 4/1/3; `cl_followmodel`(+`cl_leanmodel` multi)/`cl_bobmodel`/`cl_viewmodel_alpha`
    dep `r_drawviewmodel`; `cl_tracers_teamcolor` 0/1/2.

### Input tab (`dialog_settings_input.qc` + userbind + bindings_reset)
- Keybinder listbox + Change/Edit/Clear buttons + "Reset all" (opens the **bindings-reset** confirm →
  `KeyBinder_Bind_Reset_All` = `unbindall; exec binds-xonotic.cfg`). "Edit..." opens the **userbind**
  dialog (name / command-on-press / command-on-release → `editUserbind`).
- Mouse: `sensitivity` 0.1–9.9 slider **linked** to an input box (`linkSensitivities`), `m_filter`,
  `m_pitch` invert checkbox (on-value 1.022, sign semantics), `m_accelerate` (0/1/1.2–4.0.2) +
  min/maxspeed sliders, `vid_dgamouse`|`apple_mouse_noaccel` (engine-dependent), `menu_mouse_absolute`
  (+`hud_cursormode` multi, redisplays the menu). Other: `con_closeontoggleconsole`,
  `cl_movement_track_canjump` (sendCvars), `cl_jetpack_jump` (0/1/2, sendCvars), `joy_enable`|`joystick`
  dep `joy_detected`.

### User tab (`dialog_settings_user.qc` + languagewarning)
- Skin list + "Set skin"; language list + "Set language" (`prvm_language "$_menu_prvm_language";
  menu_restart; menu_cmd languageselect`); while connected the change is routed through the
  **language-warning** dialog (Disconnect now / Switch language). `cl_gentle` master +
  `cl_gentle_gibs`(+`cl_gentle_damage` multi) + `cl_gentle_messages` (deps `cl_gentle == 0`).

### Misc tab (`dialog_settings_misc.qc` + misc_cvars + misc_reset)
- Network: `shownetgraph`, `cl_netrepeatinput`, `cl_movement_errorcompensation` (dep `cl_movement`),
  `crypto_aeslevel` (Ex bit, engine-gated). HTTP: `cl_curl_maxdownloads` 1–5, `cl_curl_maxspeed`
  (64 KiB/s..8 MiB/s, Unlimited 0). Other: `menu_tooltips` 0/1/2, `menu_animations` (0, 0.05–0.5/0.05),
  `r_textshadow`, `showtime`(+`showdate` multi). Buttons: **Advanced settings...** → the cvars dialog
  (filter box + modified-only + description-search checkboxes, list, Setting/Type/Value editor,
  Reset-to-default, Description pane); **Factory reset** → confirm dialog whose Yes runs
  `saveconfig backup.cfg; exec default.cfg`. Apply = `menu_restart`.

### Multiplayer dialog (`dialog_multiplayer.qc`) and tabs
- Tabs Servers / Create / Profile.
- **Servers** (`dialog_multiplayer_join.qc`): Categories checkbox, filter box, Empty/Full/Laggy,
  Refresh (ASK mode), `net_slist_pause`, 5 sort buttons, the serverlist, Address box (Enter = connect),
  favorite button, Info... (opens server-info), Leave-match | Join!.
- **Server info** (`_join_serverinfo.qc` + `_serverinfotab.qc` + `_termsofservice.qc`): tokenizes
  `SLIST_FIELD_QCSTATUS` on ":" — gametype, version, then `P` pure-violations / `S` freeslots / `F`
  flags / `T` ToS-url / `M` modname; status tab fields (hostname/address/gametype/map/mod/version/
  settings/players/bots/free slots/encryption/ID/key/stats) + the playerlist; ToS tab url_fopen-loads
  the advertised ToS.
- **Create** (`_create.qc`): gametype list, `menu_create_show_all`, `timelimit_override` mixed slider,
  per-gametype frag slider (re-configured by `gt.m_configuremenu` → `GameType_ConfigureSliders`:
  label + cvar + range + teams cvar), Teams (Default/2/3/4), `menu_maxplayers` 1–32, `bot_number` 0–9
  (deps !g_campaign, !bot_vs_human), `skill` 0–10, `bot_vs_human` (predicate: teams < 2 && !campaign);
  maplist column + filter + Add/Remove shown/all + Mutators... + Start (`MapList_LoadMap`).
- **Map info** (`_create_mapinfo.qc`): preview (maps/ → levelshots/ → nopreview_map), title/author/
  description, per-gametype supported tags (dimmed by `MapInfo_Map_supportedGametypes`), Close/Play.
- **Mutators** (`_create_mutators.qc`): gameplay mutators (dodging, touchexplode, cloaked, buffs
  off=−1, midair, vampire, bloodloss slider-checkbox 10–50 off 0, low-gravity slider-checkbox
  `sv_gravity` 80–400 off 800 saved 200 shown ×0.125 %), weapon/item mutators (hook, jetpack,
  invincible projectiles, new toys + autoreplace 0/1/2, rocket flying, piñata, weapons stay), arena
  radios (Regular / custom `g_weaponarena`=`menu_weaponarena` with a per-weapon checkbox grid /
  most / all), special arenas (`g_instagib`, `g_nix` + `g_nix_with_blaster`), No-start-weapons radio
  (17 × `g_balance_*_weaponstartoverride` multi, off −1). `checkCompatibility_*` predicates grey
  incompatible combos; closing refilters the maplist.
- **Profile** (`_profile.qc`): name box (`_cl_name`, forbidden `\r\n\\"$`, −127 byte cap) + colorpicker
  + charmap; model selector + glowing/detail 15-color palettes (`_cl_color` nibbles); stats opt-ins
  `cl_allow_uidtracking` → `cl_allow_uid2name`/`cl_allow_uidranking` (sendCvars) + statslist;
  "Select language..."; Apply = `color -1 -1; name "$_cl_name"; playermodel $_cl_playermodel;
  playerskin $_cl_playerskin`. The Name header blinks while `_cl_name == "Player"`.

## Port mapping

| Base | Port | State |
|---|---|---|
| dialog_settings.qc | `DialogSettings.cs` | faithful 7-tab grid, live |
| dialog_settings_video.qc | `DialogSettingsVideo.cs` + `ClientSettings.ApplyVideo` | dialog faithful; only window/vsync/fps/priority apply |
| dialog_settings_effects.qc | `DialogSettingsEffects.cs` | dialog faithful; presets + ~90 % of cvars dead |
| dialog_settings_audio.qc | `DialogSettingsAudio.cs` + `ClientSettings.ApplyAudio` | 6 of 9 volume routes live; output section cosmetic |
| dialog_settings_game.qc | `DialogSettingsGame.cs` (nested TabContainer) | intended divergence (registry → fixed tabs) |
| …game_crosshair.qc | `DialogSettingsGameCrosshair` (+CrosshairPicker/Preview) | live (renderer = cl-crosshair) |
| …game_hud.qc / hudconfirm.qc | `DialogSettingsGameHud` / `DialogHudConfirm` (dead) | shownames+waypoints live; editor entry diverges |
| …game_messages.qc | `DialogSettingsGameMessages` | primary cvars only; makeMulti dropped |
| …game_model.qc | `DialogSettingsGameModel` | ghost/force/deathglow live; simple-items/nogibs dead |
| …game_view.qc | `DialogSettingsGameView` + `FirstPersonView`/`ChaseCamera` | mostly live; eventchase value bug; unpress dead |
| …game_weapons.qc | `DialogSettingsGameWeapons` + `WeaponPriorityList`/`ViewModel` | list+visuals live; sendcvar/apply inert |
| dialog_settings_input.qc / userbind / bindings_reset | `DialogSettingsInput` (+`DialogBindingsReset` dead) | sensitivity/invert live; mouse-accel/filter dead; userbind inert |
| dialog_settings_user.qc / languagewarning | `DialogSettingsUser` (+`DialogLanguageWarning` dead) | skin+language live; gentle partial |
| dialog_settings_misc.qc / misc_cvars / misc_reset | `DialogSettingsMisc` / `DialogCvarList` (dev-only) / — | both escalation buttons broken/divergent |
| dialog_multiplayer.qc | `MultiplayerScreen.cs` | faithful |
| dialog_multiplayer_join.qc | `MultiplayerScreen.BuildServersTab` | chrome faithful (list = menu-core) |
| …join_serverinfo(+tab, +ToS).qc | `DialogServerInfo.cs` | status subset; ToS tab + playerlist missing |
| dialog_multiplayer_create.qc | `CreateGameScreen.cs` | live end-to-end; hand-copied mode table |
| …create_mapinfo.qc | `DialogCreateGameMapInfo.cs` | live; tag row unverified |
| …create_mutators.qc | `DialogMutators.cs` | live; compat predicates dropped; 17-cvar multi kept |
| dialog_multiplayer_profile.qc | `DialogMultiplayerProfile.cs` | widgets live; Apply inert |

## Parity assessment

- **The dialogs themselves are strong** — every scoped Base dialog except four sub-dialogs
  (userbind-edit, bindings-reset confirm, hudconfirm, factory-reset confirm — all replaced by direct
  action or inert logs) and the server-info ToS tab has a ported surface, usually with QC-order layout
  and verbatim cvar bindings. The port even adds honest extras (showping/showposition, particle
  renderer mode, Damage Text tab).
- **Wiring is the failure axis, concentrated in Video/Effects:** `exec` is not a MenuCommand verb so
  the five quality presets do nothing, and the graphics cvars the tabs bind are mostly readerless
  (MSAA/aniso/glow/ambient hardcoded — see `planning/graphics-settings-audit-2026-06-14.md`, Table 7
  for the wiring plan). Since that audit, a lot of GAME-tab wiring has landed: shownames, waypoint
  sprites, view bob/idle, chase camera, gun align/sway/bob, ghost items, force models/colors,
  deathglow, tracers teamcolor, weapon priority — the Game group is now the healthiest.
- **Liveness of command verbs:** handled = vid_restart/snd_restart/menu_restart/prvm_language/
  saveconfig/cvar_resettodefaults_*/map/connect/disconnect/quit/set/seta/toggle/inc/dec/directmenu/
  nav verbs/cmd/sandbox/join/spec/ready. NOT handled (inert): `exec`, `sendcvar`, `color`, `name`,
  `playermodel`, `playerskin`, `menu_showcvarsdialog`, `saveconfig <file>` args.
- **Systemic makeMulti loss:** the port toolkit has no multi-cvar widget; except DialogMutators'
  start-weapons radio, every QC `makeMulti` sibling write is dropped (worst on the Messages tab).
- **Intended divergences:** Game "tab of tabs" architecture; vid_vsync 0–3 domain + cl_maxfps auto
  ceiling (perf work); linear-percent volume sliders (readout only — same cvars).

## Verification

- YAML `verification` entries cite the concrete reader `file:line` for every LIVE claim (grep-verified
  2026-07-02) and the missing-handler/zero-caller evidence for every DEAD claim.
- Effect-level fidelity spot checks deferred to owning units: cl-crosshair (picker/ring/dot),
  cl-hud (scoreboard/dynamic), fx-sounds (hitsound sample bug), physics-player (track_canjump).
- Not behavior-tested in-game: force-color mode semantics, mutator dialog → server efficacy per
  mutator, server-info field derivation, profile name byte-cap.

## Open questions

- Does the notification dispatcher consult per-notification cvars by name at dispatch (which would
  make the Messages-tab primary cvars effective despite the dropped multis)? Needs a notifs-unit read.
- Is `chase_up` consumed anywhere (only `chase_back` found)?
- Should `cl_eventchase_death`'s FlagCheckBox be fixed to write 2 (Base on-value) — currently ticking
  the box *disables* the default behavior (FirstPersonView special-cases ==2)?
- menu-core.misc.cvarlist marks the cvar editor `live`; with `menu_showcvarsdialog` unhandled the only
  entry is the dev flag — that row's liveness should be revisited on the next menu-core drift pass.
