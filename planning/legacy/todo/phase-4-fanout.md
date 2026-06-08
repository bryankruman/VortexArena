# Phase 4 — Fan-out (the data-driven bulk)

**Goal:** with the spine proven, port the large, mostly-pluggable content in parallel. This is the
highest-volume, lowest-risk phase — it scales with headcount and leans on the mechanical-assist transpiler.
**Exit demo:** most weapons/gametypes/mutators playable online; full HUD; working Menu.
**Active tracks:** G (wide fan-out), U (full HUD + Menu), A (remaining assets), N (mapobject replication).

> Define the v1 content subset first (OPEN Q7) so "done" is bounded; the rest is incremental.

---

## Track G — Gameplay (parallel fan-out — each item is largely independent)

### G.A Weapons (19 total; 3 done in Phase 2)
- ☐ Port remaining weapons (arc, crylink, devastator, electro, fireball, hagar, hlac, hook, minelayer, mortar,
  porto, rifle, seeker, shotgun, tuba, vaporizer) — registry + fire logic + projectiles. ↳ data-driven; balance
  cvars reused (OPEN Q5).

### G.B Gametypes (20 total; DM done)
- ☐ CTF, Clan Arena, Freeze Tag, Duel, TDM, Domination, Key Hunt, Keepaway, Race/CTS, Onslaught, Assault, LMS,
  Nexball, Invasion, Survival, Mayhem/tMayhem, TKA. ↳ each a `REGISTER_GAMETYPE` module; depends on
  scores/teamplay (port from `server/`).

### G.C Mutators (44 total)
- ☐ Port the high-value set first (instagib, nades, overkill, buffs, dodging, hook, NIX, midair, walljump,
  multijump) then the long tail. ↳ each subscribes to the hook bus; mostly self-contained plugins.

### G.D Server-core systems
- ☐ Scores/score-rules, teamplay, mapvoting, intermission, round handler, chat, vote/command framework,
  anticheat, handicap. ↳ port from `server/` (the integration hub).

### G.E Items & resources (finish)
- ☐ Powerups, all ammo/armor variants, item-spawn timing, random items.

## Track U — Client & UI

### U.A Full HUD (port `client/hud`, ~14.8k LOC)
- ☐ All HUD panels (scoreboard, centerprint, minimap/radar, weapon/ammo/powerup panels, racetimer, vote dialog),
  via `REGISTER_HUD_PANEL`.
- ☐ Shownames, damage text, hit indication, kill feed (notifications client side).

### U.B Menu (independent track — can start in Phase 1)
- ☐ Port the menu widget toolkit (`menu/item/` containers) → C#/Godot UI ([ADR-0008]; 0 net calls).
- ☐ Main menu, server browser shell, settings dialogs, player/profile, gametype/map select.
- ☐ Wire settings → cvars; keybindings.

## Track A — Assets (remaining set)
- ☐ Convert the full map/model/sound/texture catalog; resolve long-tail importer issues.
- ☐ Effects/particle assets staged for Phase 5 effect parity.

## Track N — Networking (mapobjects & events)
- ☐ Replicate `common/mapobjects/` entity state (doors/triggers/plats — **highest net coupling**, 405 calls in QC).
- ☐ Temp-entity events (effects/sounds) over the event channel.
- ☐ Notifications networking (kill feed, announcer).

---

## DoD
The v1 content subset (OPEN Q7) is playable online: most weapons + gametypes + mutators, full HUD, working Menu,
mapobjects replicating correctly. Remaining items tracked into Phase 5.
