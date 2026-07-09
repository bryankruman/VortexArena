# UW-0092 — Draft: Medals

- **Source:** `data:z411/medals@74239c6b1dc2`
- **Kind:** qc-gameplay
- **Base symbols touched:** `centerprint_Medal()`, `Scoreboard_MedalStats_Draw()`, `GIVE_MEDAL macro`, `MSG_MEDAL notification type`, `SP_MEDAL_* score fields`, `ca_LastPlayer()`, `freezetag_LastPlayer()`, `CTF_IS_NEAR macro`, `Obituary_WeaponDeath(attacker param)`, `MSG_MEDAL_NOTIF macro`, `Create_Notification_Entity_Medal()`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Comprehensive medal system for player achievements. Implements 18 medal types (airshot, assist, capture, damage, defense, electrobitch, excellent, firstblood, headshot, humiliation, impressive, perfect, telefrag, yoda, and kill streaks 3/5/10/15) with server-side scoring via new SP_MEDAL_* columns, client centerprint/scoreboard display, and gamemode/weapon hooks for CTF assists/captures, Clan Arena perfect rounds, Freezetag solo/assist wins, headshots, airshots, and kill streaks. Adds MSG_MEDAL notification class, 18 TGA medal icons, and GIVE_MEDAL macro.

## Portability
High. Qc-gameplay with minimal engine dependencies. Server logic is declarative (kill-condition checks + score increments) porting directly to C# (Damage.cs, Scores.cs, gamemode logic). Client UI rendering (centerprint + scoreboard) uses Godot equivalents of draw_getimagesize/drawpic/drawstring and vector math. MSG_MEDAL notification class is orthogonal to existing netcode — easily added to Notifications enum/registry as a new message type carrying icon path and count. Assets (18 TGA files) are format-compatible. Only port-specific friction: C# lacks QC's macro system (GIVE_MEDAL becomes a static helper method), and centerprint/scoreboard panels need C# HUD equivalents.

## Completeness (upstream)
Upstream WIP/Draft (PR open, title says "Draft: Medals"). The feature is feature-complete with all major medal types implemented and showing; no critical TODOs blocking gameplay. Minor TODOs exist in code (commented out or incomplete): CA_CheckWinner has disabled Defense medal logic (lines 302-309 commented), CA has disabled Accuracy medal (line 334 TODO), Vaporizer has disabled Impressive medals (lines 874-880 commented). These are deliberate upstream decisions, not incomplete work. No test coverage visible in diff. Branch is actively in review (MR exists).

## Quality
Solid QC idiom — clean integration into existing subsystems. GIVE_MEDAL macro is DRY (no repeated Send_Notification calls). Centerprint display is mature (handles stacking up to 5, overflow to count, proper alpha fade). Scoreboard display is organized (separates frag/team medals vs utility medals). Notification infrastructure extension (MSG_MEDAL, Create_Notification_Entity_Medal) follows Base conventions. Only style note: some intentionally commented logic suggests the feature was stabilized but some medal conditions were dialed back for balance. No obvious bugs.

## Roadmap / design alignment
Serves Vortex Arena core gameplay — medals are quality-of-life/engagement feature that enhance competitive play and match atmosphere without altering core mechanics. Aligns with existing upstream direction (Base openly develops medals). No conflict with intended_divergence (Vortex Arena maintains parity on core gameplay; visual feedback like medals is a natural port candidate).

## Recommendation
Port as-is (or with adapt phase for C#/Godot idioms if needed). The feature is near-production-ready upstream; the upstream TODOs (commented Accuracy/Defense logic) are deliberate balance choices, not incomplete work. Vortex Arena should adopt all 18 medal types for parity. C# migration is straightforward: GIVE_MEDAL becomes a static helper, notification dispatch uses existing Notifications.Send(MSG_MEDAL, ...) pattern, and HUD drawing maps to Godot canvas/shader equivalents. The asset pack (18 TGAs) is ready to import. Priority: medium-high (gameplay feature, not blocking, good ROI on effort).
