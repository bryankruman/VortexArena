# Base coverage ledger

_Generated 2026-07-02 by `tools/parity-coverage.py` from 162 registry shards against `C:\Users\Bryan\Projects\Xonotic\Base\data\xonotic-data.pk3dir\qcsrc`._

A Base file is **cited** when any registry row's `base_refs` names it, **excluded** when
[coverage-scope.yaml](coverage-scope.yaml) declares it out of parity scope (with rationale),
**deferred** when an audit is scheduled but not yet landed, and **UNMAPPED** when nobody has
ever looked — the actionable number. Citation is a *claim* that the owning unit audited the
file; it does not by itself prove row-level completeness (the adversarial verify passes do that).

## Summary

| bucket | files | lines | % of lines |
|---|---:|---:|---:|
| cited | 811 | 207192 | 85.9% |
| excluded | 11 | 4220 | 1.7% |
| deferred | 177 | 4463 | 1.9% |
| UNMAPPED | 758 | 25350 | 10.5% |
| **total** | 1757 | 241225 | 100% |

## Per-directory

| dir | total files | cited | excluded | deferred | UNMAPPED files | UNMAPPED lines |
|---|---:|---:|---:|---:|---:|---:|
| `client/` | 109 | 57 | 0 | 0 | 52 | 1674 |
| `common/` | 1000 | 500 | 0 | 0 | 500 | 11466 |
| `dpdefs/` | 12 | 1 | 11 | 0 | 0 | 0 |
| `ecs/` | 21 | 2 | 0 | 0 | 19 | 196 |
| `lib/` | 101 | 21 | 0 | 0 | 80 | 7116 |
| `menu/` | 340 | 163 | 0 | 177 | 0 | 0 |
| `server/` | 174 | 67 | 0 | 0 | 107 | 4898 |

## Largest UNMAPPED files (top 0)

| file | lines |
|---|---:|

## Deferred (audit scheduled)

| file | lines | note |
|---|---:|---|
| `menu/skin-customizables.inc` | 282 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/serverlist.qh` | 166 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/guide.qh` | 133 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/_mod.inc` | 132 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/_mod.qh` | 132 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/listbox.qh` | 87 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/dialog.qh` | 74 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/util.qh` | 70 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/menu.qh` | 59 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/maplist.qh` | 57 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/slider.qh` | 51 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/container.qh` | 50 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/modalcontroller.qh` | 50 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/cvarlist.qh` | 50 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/campaign.qh` | 49 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game.qh` | 49 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/nexposee.qh` | 46 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/inputbox.qh` | 45 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/screenshotlist.qh` | 45 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/draw.qh` | 44 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/button.qh` | 42 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/pages.qh` | 40 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/hudskinlist.qh` | 39 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/keybinder.qh` | 39 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/playlist.qh` | 39 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/label.qh` | 38 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/entries.qh` | 37 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/inputbox.qh` | 37 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/languagelist.qh` | 37 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog.qh` | 35 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/soundlist.qh` | 35 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/datasource.qh` | 34 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_join_serverinfo.qh` | 34 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/demolist.qh` | 33 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/skinlist.qh` | 33 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item.qh` | 32 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/picker.qh` | 31 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/mainwindow.qh` | 30 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/slider.qh` | 30 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/checkbox.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_join_serverinfotab.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/topics.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/mixedslider.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/playermodel.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/textslider.qh` | 29 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/image.qh` | 28 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/mixedslider.qh` | 28 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/skin.qh` | 28 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/animation.qh` | 27 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_create_mapinfo.qh` | 27 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/tab.qh` | 27 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/gametypelist.qh` | 26 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/radiobutton.qh` | 26 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/textbox.qh` | 26 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/borderimage.qh` | 25 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/tab.qh` | 25 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/checkbox_string.qh` | 25 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_welcome.qh` | 25 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/weaponslist.qh` | 25 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/checkbox_slider_invalid.qh` | 24 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/scrollpanel.qh` | 24 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/statslist.qh` | 24 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/colorpicker_string.qh` | 23 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/credits.qh` | 23 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudsetup_exit.qh` | 23 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_termsofservice.qh` | 23 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/description.qh` | 23 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_screenshot_viewer.qh` | 22 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/playerlist.qh` | 22 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/button.qh` | 20 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/charmap.qh` | 20 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/colorpicker.qh` | 20 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/animhost.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/textslider.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/colorbutton.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_guide.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_create.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/listbox.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/screenshotimage.qh` | 19 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/_mod.inc` | 18 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/_mod.qh` | 18 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/commandbutton.qh` | 18 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_teamselect.qh` | 18 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_firstrun.qh` | 17 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_input_userbind.qh` | 17 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/keyframe.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/checkbox.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/inputcontainer.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_gamemenu.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_join_termsofservice.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/rootdialog.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/slider_resolution.qh` | 16 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_screenshot.qh` | 15 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/easing.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/crosshairpicker.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_credits.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_create_mutators.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_profile.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/weaponarenacheckbox.qh` | 14 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/progs.inc` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_ammo.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_centerprint.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_chat.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_checkpoints.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_engineinfo.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_healtharmor.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_infomessages.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_modicons.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_notification.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_physics.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_pickup.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_powerups.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_pressedkeys.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_racetimer.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_radar.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_score.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_strafehud.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_timer.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_vote.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_weapons.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_quit.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_sandboxtools.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_hud.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_messages.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_model.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_weapons.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_singleplayer.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_uid2name.qh` | 13 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/crosshairpreview.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_itemstime.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_hudpanel_quickmenu.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_demo.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_monstertools.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_multiplayer_join.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_crosshair.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_view.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_misc_cvars.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_singleplayer_winner.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/tabcontroller.qh` | 12 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/_mod.inc` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/_mod.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/item/radiobutton.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_demo_startconfirm.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_demo_timeconfirm.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_media_musicplayer.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_audio.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_bindings_reset.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_game_hudconfirm.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_input.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_misc_reset.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_user_languagewarning.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_video.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/textlabel.qh` | 11 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_effects.qh` | 10 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_misc.qh` | 10 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/dialog_settings_user.qh` | 10 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/slider_decibels.qh` | 10 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/slider_picmip.qh` | 10 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/bigcommandbutton.qh` | 9 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/leavematchbutton.qh` | 9 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/slider_sbfadetime.qh` | 9 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/bigbutton.qh` | 8 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/image.qh` | 7 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/nexposee.qh` | 7 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/_mod.inc` | 6 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/xonotic/guide/_mod.qh` | 6 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/_mod.inc` | 5 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/anim/_mod.qh` | 5 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/command/menu_cmd.qh` | 3 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/command/_mod.inc` | 2 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/command/_mod.qh` | 2 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/mutators/_mod.inc` | 2 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/mutators/_mod.qh` | 2 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |
| `menu/matrix.qh` | 1 | menu-core + menu-dialogs units — 2026-07-02 unmapped-area audit wave |

## Exclusions (deliberately out of scope)

| pattern | files | lines | rationale |
|---|---:|---:|---|
| `dpdefs/**` | 11 | 4220 | DarkPlaces/menu engine API declaration mirrors (builtin prototypes, extension defs, keycodes) — declarations only, no behavior. The engine behavior behind these APIs is audited where it is consumed (each unit's rows) and by the data/engine-layer checks (CVAR-DIFF.md, ASSET-CHECK.md). |

## Defer entries now satisfied (remove from coverage-scope.yaml)

- `client/command/_mod.inc` is now cited
- `client/command/_mod.qh` is now cited
- `client/command/cl_cmd.qc` is now cited
- `client/command/cl_cmd.qh` is now cited
- `client/hud/panel/strafehud/_mod.inc` is now cited
- `client/hud/panel/strafehud/_mod.qh` is now cited
- `client/hud/panel/strafehud/draw.qc` is now cited
- `client/hud/panel/strafehud/draw.qh` is now cited
- `client/hud/panel/strafehud/draw_core.qc` is now cited
- `client/hud/panel/strafehud/draw_core.qh` is now cited
- `client/hud/panel/strafehud/extra.qc` is now cited
- `client/hud/panel/strafehud/extra.qh` is now cited
- `client/hud/panel/strafehud/util.qc` is now cited
- `client/hud/panel/strafehud/util.qh` is now cited
- `common/command/_mod.inc` is now cited
- `common/command/_mod.qh` is now cited
- `common/command/command.qh` is now cited
- `common/command/generic.qc` is now cited
- `common/command/generic.qh` is now cited
- `common/command/markup.qc` is now cited
- `common/command/markup.qh` is now cited
- `common/command/reg.qc` is now cited
- `common/command/reg.qh` is now cited
- `common/command/rpn.qc` is now cited
- `common/command/rpn.qh` is now cited
- `common/mapinfo.qc` is now cited
- `common/mapinfo.qh` is now cited
- `common/minigames/_mod.inc` is now cited
- `common/minigames/_mod.qh` is now cited
- `common/minigames/cl_minigames.qc` is now cited
- `common/minigames/cl_minigames.qh` is now cited
- `common/minigames/cl_minigames_hud.qc` is now cited
- `common/minigames/cl_minigames_hud.qh` is now cited
- `common/minigames/minigame/_mod.inc` is now cited
- `common/minigames/minigame/_mod.qh` is now cited
- `common/minigames/minigame/all.qh` is now cited
- `common/minigames/minigame/bd.qc` is now cited
- `common/minigames/minigame/bd.qh` is now cited
- `common/minigames/minigame/c4.qc` is now cited
- `common/minigames/minigame/c4.qh` is now cited
- `common/minigames/minigame/nmm.qc` is now cited
- `common/minigames/minigame/nmm.qh` is now cited
- `common/minigames/minigame/pong.qc` is now cited
- `common/minigames/minigame/pong.qh` is now cited
- `common/minigames/minigame/pp.qc` is now cited
- `common/minigames/minigame/pp.qh` is now cited
- `common/minigames/minigame/ps.qc` is now cited
- `common/minigames/minigame/ps.qh` is now cited
- `common/minigames/minigame/ttt.qc` is now cited
- `common/minigames/minigame/ttt.qh` is now cited
- `common/minigames/minigames.qc` is now cited
- `common/minigames/minigames.qh` is now cited
- `common/minigames/sv_minigames.qc` is now cited
- `common/minigames/sv_minigames.qh` is now cited
- `lib/json.qc` is now cited
- `lib/urllib.qc` is now cited
- `menu/anim/animation.qc` is now cited
- `menu/anim/animhost.qc` is now cited
- `menu/anim/easing.qc` is now cited
- `menu/anim/keyframe.qc` is now cited
- `menu/command/menu_cmd.qc` is now cited
- `menu/draw.qc` is now cited
- `menu/gamesettings.qh` is now cited
- `menu/item.qc` is now cited
- `menu/item/borderimage.qc` is now cited
- `menu/item/button.qc` is now cited
- `menu/item/checkbox.qc` is now cited
- `menu/item/container.qc` is now cited
- `menu/item/dialog.qc` is now cited
- `menu/item/image.qc` is now cited
- `menu/item/inputbox.qc` is now cited
- `menu/item/inputcontainer.qc` is now cited
- `menu/item/label.qc` is now cited
- `menu/item/listbox.qc` is now cited
- `menu/item/mixedslider.qc` is now cited
- `menu/item/modalcontroller.qc` is now cited
- `menu/item/nexposee.qc` is now cited
- `menu/item/radiobutton.qc` is now cited
- `menu/item/slider.qc` is now cited
- `menu/item/tab.qc` is now cited
- `menu/item/textslider.qc` is now cited
- `menu/matrix.qc` is now cited
- `menu/menu.qc` is now cited
- `menu/mutators/events.qc` is now cited
- `menu/mutators/events.qh` is now cited
- `menu/xonotic/bigbutton.qc` is now cited
- `menu/xonotic/bigcommandbutton.qc` is now cited
- `menu/xonotic/button.qc` is now cited
- `menu/xonotic/campaign.qc` is now cited
- `menu/xonotic/charmap.qc` is now cited
- `menu/xonotic/checkbox.qc` is now cited
- `menu/xonotic/checkbox_slider_invalid.qc` is now cited
- `menu/xonotic/checkbox_string.qc` is now cited
- `menu/xonotic/colorbutton.qc` is now cited
- `menu/xonotic/colorpicker.qc` is now cited
- `menu/xonotic/colorpicker_string.qc` is now cited
- `menu/xonotic/commandbutton.qc` is now cited
- `menu/xonotic/credits.qc` is now cited
- `menu/xonotic/crosshairpicker.qc` is now cited
- `menu/xonotic/crosshairpreview.qc` is now cited
- `menu/xonotic/cvarlist.qc` is now cited
- `menu/xonotic/datasource.qc` is now cited
- `menu/xonotic/demolist.qc` is now cited
- `menu/xonotic/dialog.qc` is now cited
- `menu/xonotic/dialog_credits.qc` is now cited
- `menu/xonotic/dialog_firstrun.qc` is now cited
- `menu/xonotic/dialog_gamemenu.qc` is now cited
- `menu/xonotic/dialog_hudpanel_ammo.qc` is now cited
- `menu/xonotic/dialog_hudpanel_centerprint.qc` is now cited
- `menu/xonotic/dialog_hudpanel_chat.qc` is now cited
- `menu/xonotic/dialog_hudpanel_checkpoints.qc` is now cited
- `menu/xonotic/dialog_hudpanel_engineinfo.qc` is now cited
- `menu/xonotic/dialog_hudpanel_healtharmor.qc` is now cited
- `menu/xonotic/dialog_hudpanel_infomessages.qc` is now cited
- `menu/xonotic/dialog_hudpanel_itemstime.qc` is now cited
- `menu/xonotic/dialog_hudpanel_modicons.qc` is now cited
- `menu/xonotic/dialog_hudpanel_notification.qc` is now cited
- `menu/xonotic/dialog_hudpanel_physics.qc` is now cited
- `menu/xonotic/dialog_hudpanel_pickup.qc` is now cited
- `menu/xonotic/dialog_hudpanel_powerups.qc` is now cited
- `menu/xonotic/dialog_hudpanel_pressedkeys.qc` is now cited
- `menu/xonotic/dialog_hudpanel_quickmenu.qc` is now cited
- `menu/xonotic/dialog_hudpanel_racetimer.qc` is now cited
- `menu/xonotic/dialog_hudpanel_radar.qc` is now cited
- `menu/xonotic/dialog_hudpanel_score.qc` is now cited
- `menu/xonotic/dialog_hudpanel_strafehud.qc` is now cited
- `menu/xonotic/dialog_hudpanel_timer.qc` is now cited
- `menu/xonotic/dialog_hudpanel_vote.qc` is now cited
- `menu/xonotic/dialog_hudpanel_weapons.qc` is now cited
- `menu/xonotic/dialog_hudsetup_exit.qc` is now cited
- `menu/xonotic/dialog_media.qc` is now cited
- `menu/xonotic/dialog_media_demo.qc` is now cited
- `menu/xonotic/dialog_media_demo_startconfirm.qc` is now cited
- `menu/xonotic/dialog_media_demo_timeconfirm.qc` is now cited
- `menu/xonotic/dialog_media_guide.qc` is now cited
- `menu/xonotic/dialog_media_musicplayer.qc` is now cited
- `menu/xonotic/dialog_media_screenshot.qc` is now cited
- `menu/xonotic/dialog_media_screenshot_viewer.qc` is now cited
- `menu/xonotic/dialog_monstertools.qc` is now cited
- `menu/xonotic/dialog_multiplayer.qc` is now cited
- `menu/xonotic/dialog_multiplayer_create.qc` is now cited
- `menu/xonotic/dialog_multiplayer_create_mapinfo.qc` is now cited
- `menu/xonotic/dialog_multiplayer_create_mutators.qc` is now cited
- `menu/xonotic/dialog_multiplayer_join.qc` is now cited
- `menu/xonotic/dialog_multiplayer_join_serverinfo.qc` is now cited
- `menu/xonotic/dialog_multiplayer_join_serverinfotab.qc` is now cited
- `menu/xonotic/dialog_multiplayer_join_termsofservice.qc` is now cited
- `menu/xonotic/dialog_multiplayer_profile.qc` is now cited
- `menu/xonotic/dialog_quit.qc` is now cited
- `menu/xonotic/dialog_sandboxtools.qc` is now cited
- `menu/xonotic/dialog_settings.qc` is now cited
- `menu/xonotic/dialog_settings_audio.qc` is now cited
- `menu/xonotic/dialog_settings_bindings_reset.qc` is now cited
- `menu/xonotic/dialog_settings_effects.qc` is now cited
- `menu/xonotic/dialog_settings_game.qc` is now cited
- `menu/xonotic/dialog_settings_game_crosshair.qc` is now cited
- `menu/xonotic/dialog_settings_game_hud.qc` is now cited
- `menu/xonotic/dialog_settings_game_hudconfirm.qc` is now cited
- `menu/xonotic/dialog_settings_game_messages.qc` is now cited
- `menu/xonotic/dialog_settings_game_model.qc` is now cited
- `menu/xonotic/dialog_settings_game_view.qc` is now cited
- `menu/xonotic/dialog_settings_game_weapons.qc` is now cited
- `menu/xonotic/dialog_settings_input.qc` is now cited
- `menu/xonotic/dialog_settings_input_userbind.qc` is now cited
- `menu/xonotic/dialog_settings_misc.qc` is now cited
- `menu/xonotic/dialog_settings_misc_cvars.qc` is now cited
- `menu/xonotic/dialog_settings_misc_reset.qc` is now cited
- `menu/xonotic/dialog_settings_user.qc` is now cited
- `menu/xonotic/dialog_settings_user_languagewarning.qc` is now cited
- `menu/xonotic/dialog_settings_video.qc` is now cited
- `menu/xonotic/dialog_singleplayer.qc` is now cited
- `menu/xonotic/dialog_singleplayer_winner.qc` is now cited
- `menu/xonotic/dialog_teamselect.qc` is now cited
- `menu/xonotic/dialog_termsofservice.qc` is now cited
- `menu/xonotic/dialog_uid2name.qc` is now cited
- `menu/xonotic/dialog_welcome.qc` is now cited
- `menu/xonotic/gametypelist.qc` is now cited
- `menu/xonotic/guide/description.qc` is now cited
- `menu/xonotic/guide/entries.qc` is now cited
- `menu/xonotic/guide/guide.qc` is now cited
- `menu/xonotic/guide/pages.qc` is now cited
- `menu/xonotic/guide/topics.qc` is now cited
- `menu/xonotic/hudskinlist.qc` is now cited
- `menu/xonotic/image.qc` is now cited
- `menu/xonotic/inputbox.qc` is now cited
- `menu/xonotic/keybinder.qc` is now cited
- `menu/xonotic/languagelist.qc` is now cited
- `menu/xonotic/leavematchbutton.qc` is now cited
- `menu/xonotic/listbox.qc` is now cited
- `menu/xonotic/mainwindow.qc` is now cited
- `menu/xonotic/maplist.qc` is now cited
- `menu/xonotic/mixedslider.qc` is now cited
- `menu/xonotic/nexposee.qc` is now cited
- `menu/xonotic/picker.qc` is now cited
- `menu/xonotic/playerlist.qc` is now cited
- `menu/xonotic/playermodel.qc` is now cited
- `menu/xonotic/playlist.qc` is now cited
- `menu/xonotic/radiobutton.qc` is now cited
- `menu/xonotic/rootdialog.qc` is now cited
- `menu/xonotic/screenshotimage.qc` is now cited
- `menu/xonotic/screenshotlist.qc` is now cited
- `menu/xonotic/scrollpanel.qc` is now cited
- `menu/xonotic/serverlist.qc` is now cited
- `menu/xonotic/skinlist.qc` is now cited
- `menu/xonotic/slider.qc` is now cited
- `menu/xonotic/slider_decibels.qc` is now cited
- `menu/xonotic/slider_picmip.qc` is now cited
- `menu/xonotic/slider_resolution.qc` is now cited
- `menu/xonotic/slider_sbfadetime.qc` is now cited
- `menu/xonotic/soundlist.qc` is now cited
- `menu/xonotic/statslist.qc` is now cited
- `menu/xonotic/tab.qc` is now cited
- `menu/xonotic/tabcontroller.qc` is now cited
- `menu/xonotic/textbox.qc` is now cited
- `menu/xonotic/textlabel.qc` is now cited
- `menu/xonotic/textslider.qc` is now cited
- `menu/xonotic/util.qc` is now cited
- `menu/xonotic/weaponarenacheckbox.qc` is now cited
- `menu/xonotic/weaponslist.qc` is now cited
- `server/command/getreplies.qc` is now cited

## STALE citations (base_refs path matches no Base file — fix the row)

- `client/hud/panel/crosshair.qc`
- `client/hud/panel/itemstime.qc`
- `common/gamemodes/gamemode/ctf/sv_ctf.qc`
- `common/gamemodes/gamemode/freezetag/sv_freezetag.qc`
- `common/gamemodes/gamemode/keyhunt/sv_keyhunt.qc`
- `common/gamemodes/gamemode/lms/sv_lms.qc`
- `common/mutators/mutator/itemstime/sv_itemstime.qc`
- `common/t_items.qc`
- `lib/util.qc`
- `server/mutators/mutator/gamemode_ctf.qc`
- `server/mutators/mutator/gamemode_onslaught.qc`
