# cl-teamradar — parity spec

**Base refs:** `client/teamradar.qc` · `client/teamradar.qh` · `client/hud/panel/radar.qc` · `client/hud/panel/radar.qh` · `client/main.qc` (minimap load) · `common/mutators/mutator/waypoints/waypointsprites.qc` (radar icons/pings)
**Port refs:** `game/hud/RadarPanel.cs` · `game/net/NetGame.cs` (live wiring) · `game/net/NetLoopback.cs` · `game/menu/dialogs/DialogHudPanelRadar.cs` (config UI) · `src/.../Waypoints/Waypoints.cs` + `game/net/ServerNet.cs` (radar-icon networking)
**Reference rev:** `v0.8.6-1779-g863cd3e84` · **Last audited:** 2026-06-22

## Overview
The team radar (HUD panel #6) is the client-only top-down minimap. It draws a rotated, player-centered
view of the arena, framed by the skin border and an optional per-map minimap image
(`gfx/<map>_radar` → `gfx/<map>_mini`), and stamps team-colored facing arrows for every player it
knows about (from the `entcs` slice), objective icons (`g_radaricons`: CTF flags, DOM points, KH keys,
plus expiring ping pulses), and Onslaught control-point links (`g_radarlinks`). It supports two display
states — the small always-on corner panel and a *maximized* fullscreen tactical map (bound to `m`) that
in Onslaught becomes clickable to pick a spawn control-point, and elsewhere is read-only (and can be
clicked to teleport in some modes). Both modes share per-mode `rotation`/`zoommode`/`scale` cvars. In
non-team gametypes the small panel is hidden unless `hud_panel_radar 2`.

## Base algorithm (authoritative)

### Coordinate transforms  (`teamradar.qc: teamradar_*coord_*`)
Four chained transforms map world ↔ minimap-texture ↔ 2D screen:
- `teamradar_3dcoord_to_texcoord`: world XY → normalized [0,1] over the minimap rect `[mi_picmin, mi_picmax]`.
- `teamradar_texcoord_to_2dcoord`: subtract player origin (in texcoord), `Rotate(in, teamradar_angle)`,
  negate Y (screen space is reversed), scale by `teamradar_size`, flip X if `v_flipped`, add `teamradar_origin2d`.
- `teamradar_2dcoord_to_texcoord` / `teamradar_texcoord_to_3dcoord`: the inverses (used by the clickable map).
- `teamradar_angle` is in **radians** (cached as the player yaw to optimize), `teamradar_size` is the 2D scale factor.

### Background / minimap image  (`teamradar.qc: draw_teamradar_background`)
Only drawn when `fg > 0 && minimapname != ""`. Emits one quad with the four minimap-texcoord corners
(`mi_pictexcoord0..3`), reversed winding when `v_flipped`, color `'1 1 1' * fg`, alpha 1, flags
`DRAWFLAG_SCREEN | DRAWFLAG_MIPMAP`. `minimapname`/`mi_min`/`mi_max`/`mi_center`/`mi_scale` are resolved
once at map load (`main.qc:139-154`): try `gfx/<map>_radar`, then `gfx/<map>_mini`, else `""` (no image).

### Player blip  (`teamradar.qc: draw_teamradar_player`)
Two stacked 4-vertex polygons forming a chevron/arrowhead. `MAKE_VECTORS(pangles - radar_angle)`, then
`forward.z=0; normalize; forward.y*=-1; right=(-forward.y, forward.x)`. Contrast backing quad uses rgb2
(black behind a colored arrow, white behind the white own-player); the colored arrow on top. Fixed pixel
vertex offsets: backing `(+f*3, +r*4-f*2.5, -f*2, -r*4-f*2.5)`, colored `(+f*2, +r*3-f*2, -f, -r*3-f*2)`.
Alpha = `panel_fg_alpha`. Every player drawn at the same fixed size (no per-player scale, no rim-clamp).

### Objective icon  (`teamradar.qc: draw_teamradar_icon`)
`drawpic_builtin(coord-'4 4 0', "gfx/teamradar_icon_" + ftos(icon.m_radaricon), '8 8 0', rgb, a, 0)`.
The icon sprite name is built from the per-icon registry id `m_radaricon`. **NOTE (verified):** every
core `REGISTER_RADARICON` (`waypoints/all.qh:41-61`) resolves `m_radaricon` to **0 (none) or 1** — there is
no id 2 in core Xonotic, so the only sprite Base ever actually draws is `gfx/teamradar_icon_1`. Objective
differentiation is by **color** (`spritelookupcolor`), not by sprite name. Size is a fixed `'8 8 0'` px.
**Ping pulses:** if the entity carries `pingdata`, loop `MAX_TEAMRADAR_TIMES = 32` stored times; for each
`dt = time - teamradar_times[i]` in `(0,1)`, draw `gfx/teamradar_ping` as an expanding additive ring of
size `'2 2 0' * teamradar_size * dt`, alpha `(1-dt)*a`, flag `DRAWFLAG_ADDITIVE`. Times are stamped by the
waypointsprite net handler when a ping arrives (`waypointsprites.qc:188-196`).

### Radar links  (`teamradar.qc: draw_teamradar_link` + `NET_HANDLE(ENT_CLIENT_RADARLINK)`)
Onslaught control-point connection lines. Each `g_radarlinks` ent carries origin/velocity(=end)/team,
synced via the `ENT_CLIENT_RADARLINK` CSQC entity (interpolated). Draws a quad between the two endpoints
with per-end colors from `colormapPaletteColor(colors & 0x0F)` / `((colors & 0xF0)/0x10)`.

### Hover glow  (`radar.qc: HUD_Radar`)
When the radar is clickable (`hud_panel_radar_mouse`) and an objective is alive and team-matches, if the
mouse is within 8px of an icon draw `gfx/teamradar_icon_glow` at 1.5× brightened color.

### cvar load  (`teamradar.qc: teamradar_loadcvars`)
Reads the per-mode cvars and applies **code defaults when unset/zero** (these win over the empty-string
`_hud_descriptions.cfg` defaults):
- `hud_panel_radar_scale` → if 0, **4096**. Maximized overrides with `_maximized_scale` (luma 5120/8192) when `>0` and not in config mode.
- `hud_panel_radar_foreground_alpha = cvar * panel_fg_alpha`; if 0, **0.8 * panel_fg_alpha**.
- `hud_panel_radar_size.x` → if 0, **128**; `.y` → if 0, mirror `.x`.
- `rotation`, `zoommode`, `maximized_rotation`, `maximized_zoommode`, `v_flipped` straight through.

### Zoom / scale blend  (`radar.qc: HUD_Radar`, `HUD_Radar_GetZoomFactor`)
`zoom_factor`: mode `0` → `current_zoomfraction` (live +zoom lerp), `1` → `1 - current_zoomfraction`,
`2` → `0` (always normal), `3` → `1` (always big). Clickable maximized forces factor 1.
`bigsize` = fit the whole arena in the radar in any rotation (uses `mi_scale`, `scale2d = vlen_maxnorm2d(mi_picmax-mi_picmin)`,
1.05 margin). `normalsize = vlen_maxnorm2d(teamradar_size2d) * scale2d / hud_panel_radar_scale`, floored to `bigsize`.
`teamradar_size = zoom*bigsize + (1-zoom)*normalsize`. Center = `zoom*mi_center + (1-zoom)*view_origin`.

### Rotation  (`radar.qc: HUD_Radar_GetAngle`)
`rotation != 0` → fixed `90 * rotation * DEG2RAD` (modes 1=west,2=south,3=east,4=north — **five** modes 0..4;
the rotate alias cycles `0 1 2 3 4`). `rotation == 0` → player-aligned `(view_angles.y - 90) * DEG2RAD`.

### Maximized map + clickable spawn/teleport  (`radar.qc: HUD_Radar_Show_Maximized/Mouse/InputEvent`)
`+hud_panel_radar_maximized` (bound `m`) → `cl_cmd hud radar 1`. Maximized uses `_maximized_size`
(bound 0.2..1 of screen), centered, always the default border, its own rotation/zoom cvars. When made
clickable (`hud_panel_radar_mouse`, ONS): forces zoom factor 1, captures the mouse, draws the
"Click to select spawn/teleport location" prompt; a MOUSE1 inside the rect issues
`cmd ons_spawn x y z` at the picked world point; click-outside / MOUSE2 / ESC / dead+showscores all
close or temp-hide it. Mouse position drives an icon hover-glow. Plain (non-clickable) maximized is a
read-only tactical overview.

### entcs player enumeration  (`radar.qc: HUD_Radar`)
Iterates `_entcs` for every private entity except the local one, drawing each at `it.origin`/`it.angles`
in its `entcs_GetTeam` team color (`Team_ColorRGB`). The local player is drawn LAST, at its own origin,
`view_angles`, white `'1 1 1'`. Objective icons from `g_radaricons` are drawn between links and players.

## Port mapping
| Base feature | Port symbol | State |
|---|---|---|
| Coordinate transforms | `RadarPanel.WorldToScreen` (world-space, no texcoord stage) | reimplemented, approximate |
| Minimap image | `RadarPanel.DrawPanel` minimap quad (`gfx/<map>_radar`/`_mini`, `MapMinXY/MaxXY`) | live (fed `MapName` + BSP model[0] bounds in NetGame) |
| Player blip arrow | `RadarPanel.DrawPlayerArrow`/`DrawArrowQuad`/`DrawBlip` (faithful vertex offsets) | live |
| entcs enumeration | `DrawPanel` loop over `net.RemoteIds`+`SampleRemote`/`TryGetRemoteState` (players/nameplates only) | live |
| Local player arrow | `DrawPlayerArrow(center, localYaw, …, white)` | live |
| Objective icons | `DrawPanel` waypoint loop over `net.Waypoints`, `wp.RadarIcon` 1→icon_1 / 2→icon_2 | live but binary icon, no registry |
| Ping pulses (`teamradar_times`/`gfx/teamradar_ping`) | NOT IMPLEMENTED | missing |
| Radar links (ONS, `ENT_CLIENT_RADARLINK`) | NOT IMPLEMENTED | missing |
| Hover glow (`teamradar_icon_glow`) | NOT IMPLEMENTED | missing |
| cvar load + defaults | `RadarPanel.RegisterDefaults` + inline `CvarF` reads | partial (wrong defaults) |
| Zoom/scale blend (mi_scale math) | `GetZoomFactor` + `ScaleReferenceDivisor=4` heuristic | partial (heuristic, not mi_scale) |
| Rotation modes | inline `radarYawDeg` (`90*rotation` / `localYaw-90`) | partial (no v_flipped, comment says 3 not 4 modes) |
| Maximized map + ONS spawn-click / teleport / input / ESC / showscores | DialogHudPanelRadar configures cvars only; NO runtime | missing |
| `hud_panel_radar 2` (show in non-team modes) | NOT IMPLEMENTED (small panel only drawn standalone) | missing |
| Discovered HUD panel | created by HudManager but force-hidden (`StartHiddenIds` "radar") | dead (the discovered instance) |

## Parity assessment

**Liveness.** The radar IS live, but only via the **standalone** `RadarPanel` instance created directly in
`NetGame.SetupHud` (`NetGame.cs:1249`, 256×256 at (24,24)) and in `NetLoopback`. It is fed `Net`, `MapName`,
the BSP world bounds, and `LocalYawDegrees = _viewAngles.Y` each frame (`NetGame.cs:2454`). Separately the
HUD-registry auto-discovers a second `RadarPanel` into the full HUD, but `HudManager.StartHiddenIds`
contains `"radar"` ("no data wiring yet") so that discovered copy never draws — it is dead. So one live
radar exists on the real play path; the others are dead.

**What is faithful.** The player-arrow geometry is a faithful port of `draw_teamradar_player` (exact vertex
offsets, contrast backing, screen-Y flip, white own-player). Team colors match `Team_ColorRGB`. The
player-aligned vs fixed rotation split matches `HUD_Radar_GetAngle`. The minimap image is loaded and shares
the blip transform, and CTF/objective waypoint icons render from networked waypoint data.

**Gaps (observable).**
- **Maximized radar entirely absent at runtime.** No `m`-bind fullscreen map, no clickable ONS spawn-point
  selection (`cmd ons_spawn`), no teleport-click, no input/mouse/ESC/showscores handling, no "Click to
  select…" prompt. The config dialog exposes the cvars but nothing consumes them. Major feature gap,
  especially for Onslaught.
- **Ping pulses missing.** A teammate ping (waypoint ping) draws no expanding `gfx/teamradar_ping` ring on
  the radar (`teamradar_times`/`draw_teamradar_icon` pulse loop unported).
- **Onslaught radar links missing.** Control-point connection lines (`draw_teamradar_link` /
  `ENT_CLIENT_RADARLINK`) are not networked or drawn — the ONS radar shows points but no links.
- **Hover glow missing** (`gfx/teamradar_icon_glow`) — only relevant once the clickable map exists.
- **Objective icon size differs (not the sprite name).** The port draws `gfx/teamradar_icon_1` at a
  radius-relative `clamp(radius*0.07, 7, 12)` px; Base draws it at a fixed `8x8` px. The sprite NAME is
  actually correct (every core `m_radaricon` is 0/1, so Base only ever draws `_1`); the port's `icon_2`
  branch (keyed on `RadarIcon==2`) is unreachable dead code — no port path sets `RadarIcon` to 2 and
  `ServerNet` sends `RadarIcon & 0xFF`. So the real gaps are the SIZE and the client-side `spritelookupcolor`
  tint resolution (the port relies on the server pre-resolving `wp.Color`).
- **Scale/zoom is a heuristic, not the Base math.** Port uses `RangeUnits=2500` + `scale/4` divisor and
  `GetZoomFactor` collapsing the live `current_zoomfraction` modes (0/1) to static endpoints; Base derives
  `bigsize`/`normalsize` from `mi_scale`/`scale2d` and blends with the actual zoom fraction. Coverage and
  zoom-in behavior differ.
- **`hud_panel_radar 2` not honored** — no logic to show the small radar in non-team gametypes vs team-only.
- **`v_flipped` not honored** (mirrored-view layout would not flip the radar).
- **Cardinal rotation modes:** the runtime `90*rotation` handles ALL five modes (0..4) correctly and the
  config dialog (`DialogHudPanelRadar`) exposes Forward/West/South/East/North — so mode 4 (north) actually
  works. Only a stale source comment says "1..3". The remaining rotation gaps are `v_flipped` (unported) and
  `maximized_rotation` default 0 vs luma 1 (only consumed by the missing maximized path).

**Value mismatches (`constants`).** The port `RegisterDefaults` registers `scale=8192` and
`foreground_alpha=1`, which are the **luma skin** values, not the QC *code* defaults that
`teamradar_loadcvars` applies when the cvar is empty (`scale → 4096`, `foreground_alpha → 0.8`).
`maximized_scale` registered 5120 (luma) vs luminos 8192; `maximized_rotation` registered 0 vs luma 1.
`hud_panel_radar_size` default 128 (QC) has no port analogue (port uses the live control size / 2500u range).
The port's own draw code does fall back to 4096/0.8 when the cvar is non-finite or ≤0, partly mitigating the
registered-default mismatch, but a user who never touches the cvar still gets 8192/1.

**Intended divergences.** The port deliberately renders in world units with a `RangeUnits`/`ScaleReferenceDivisor`
heuristic instead of the `mi_picmin/mi_picmax` texcoord pipeline, and the docstring frames this as the
"phase-1 parity target." Treated here as a partial (not-yet-faithful) approximation rather than a sanctioned
divergence, since no rationale pins the specific numbers to a gameplay decision.

## Verification
- Base algorithm + constants: read `client/teamradar.qc/.qh`, `client/hud/panel/radar.qc/.qh`,
  `client/main.qc:139-154` (minimap load), `waypointsprites.qc` (radar icon/ping send + `teamradar_times`).
- cvar defaults: `_hud_common.cfg` (`hud_panel_radar 1`, `_dynamichud 1`), `_hud_descriptions.cfg`
  (all `""` → code defaults), skin configs (luma scale 8192 / max 5120, luminos 4096 / max 8192).
- Port liveness: traced `NetGame.cs:1249-1265` (creation + map wiring) and `:2454` (per-frame yaw feed);
  `HudManager.cs:108-112` (`StartHiddenIds` hides the discovered radar). RadarPanel read in full.
- Networking: `ServerNet.cs:955` writes `wp.RadarIcon`; `Waypoints.cs` carries `RadarIcon`/`Color`/`Fade`.
  No `RADARLINK`/ping-time/`ons_spawn` equivalent found anywhere in the port (grep).
- Not run in-game this pass; visual claims are code-derived.

## Open questions
- Does any port path drive the discovered HUD-registry radar (could it be un-hidden once "data wiring"
  lands), or is the standalone NetGame instance the permanent design? `StartHiddenIds` suggests the latter
  for now.
- Is Onslaught (control points + radar links + clickable spawn) in scope for the port at all? If ONS is not
  shipping, the maximized-clickable-radar gap is lower priority than the always-on ping/link gaps.
- `current_zoomfraction` (the +zoom lerp) is not exposed to the HUD layer; wiring it would make zoommode
  0/1 behave faithfully.
