using Godot;
using XonoticGodot.Common.Gameplay;     // Teams.Red/Blue/Yellow/Pink (NUM_TEAM_* palette values)
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Net;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Top-down radar / minimap — the C# successor to QuakeC's <c>hud_panel_radar</c> +
/// <c>client/teamradar.qc</c> (Base/.../qcsrc/client/hud/panel/radar.qc). In QC the radar walked the
/// <c>entcs</c> entities — the lightweight position+team+health slice the server replicates for <i>every</i>
/// player so they show on the map even when out of view (<c>draw_teamradar_player</c>) — and stamped a
/// team-colored, facing-oriented arrow per player onto a rotated, player-centered minimap, framed by the skin
/// border and the optional minimap foreground image (<c>draw_teamradar_background</c>).
///
/// Here that same data arrives through <see cref="ClientNet"/>: the predicted local origin
/// (<see cref="ClientNet.PredictedOrigin"/>) is the radar center, and every other networked entity is sampled
/// by id (<see cref="ClientNet.RemoteIds"/> → <see cref="ClientNet.SampleRemote"/> for the interpolated world
/// pose, <see cref="ClientNet.TryGetRemoteState"/> for its kind/team/flags). We render the contract faithfully:
/// the skin border frame, the QC <b>rotation</b> modes (<c>_rotation</c> 0 = player-aligned facing-up, nonzero =
/// fixed cardinal lock), the <b>zoom/scale</b> (<c>_scale</c> / <c>_zoommode</c>) that the QC blends between a
/// "fit whole arena" big size and a fixed pixels-per-unit normal size, the team-colored player arrows, and the
/// foreground alpha (<c>_foreground_alpha</c>). The full minimap texture (<c>minimapname</c>) is heavy and not
/// loaded here, so we render the framed disc + blips + the same rotation/zoom/team-color behaviour, which is the
/// parity target the contract sets for phase 1 (HUD_PARITY_CONTRACT §6 radar).
///
/// <para>As of the HUD parity refactor this is a real <see cref="HudPanel"/> so it is auto-discovered into the
/// full HUD (laid out at the luma top-left corner with the <c>border_corner_northwest</c> frame) AND still works
/// STANDALONE the way <see cref="XonoticGodot.Game.Net.NetGame"/> uses it — set directly (<see cref="Net"/>,
/// <see cref="Control.Position"/>/<see cref="Control.Size"/>, <see cref="LocalYawDegrees"/> each frame) without
/// a manager driving <c>LoadConfig</c>. The draw code keys off the live <see cref="Control.Size"/> so it honours
/// whichever path placed the panel. The standalone NetGame radar keeps its manual 200×200 at (24,24).</para>
///
/// <para><b>Coordinate mapping.</b> Quake's ground plane is X (forward/east) and Y (left/north); a blip's world
/// delta <c>(d = entity.XY − PredictedOrigin.XY)</c> is rotated by the radar angle into screen space, then mapped
/// to pixels at the current pixels-per-world scale, with screen-Y negated because Godot screen-Y grows downward
/// while Quake +Y is north. The radar angle is the QC <c>HUD_Radar_GetAngle</c>: player-aligned (<c>_rotation</c>
/// 0) counter-rotates the field by the player's yaw so their facing points up; a fixed <c>_rotation</c> locks the
/// map to a cardinal.</para>
/// </summary>
public partial class RadarPanel : HudPanel
{
    /// <summary>
    /// The network source. The radar reads the local player's predicted position and every remote
    /// entity's interpolated pose/state from this. <c>null</c> until the host wires up a live session;
    /// the panel draws nothing while it is.
    /// </summary>
    public ClientNet? Net { get; set; }

    /// <summary>
    /// World radius (Quake units) the radar circle covers from its center to its edge when no live scale is
    /// resolvable (the fallback for the standalone path / unset cvars). Entities farther than this are clamped
    /// onto the rim (so off-radar contacts still show a bearing). The live <c>hud_panel_radar_scale</c> /
    /// <c>_zoommode</c> override this when set. 2500 is a reasonable arena-scale default.
    /// </summary>
    [Export] public float RangeUnits { get; set; } = 2500f;

    /// <summary>
    /// The local player's view yaw (Quake yaw, degrees) — set by the host each frame so the radar rotates
    /// with the player and the direction they face points "up" in the player-aligned rotation mode
    /// (<c>hud_panel_radar_rotation</c> == 0). Ignored when a fixed rotation is selected.
    /// </summary>
    [Export] public float LocalYawDegrees { get; set; }

    /// <summary>
    /// The local player's world origin override (Quake units). When set (non-null) it is used as the radar
    /// center instead of <see cref="ClientNet.PredictedOrigin"/> — handy for the standalone path or when
    /// spectating. Left null the radar centers on the net predicted origin.
    /// </summary>
    public NVec3? CenterOrigin { get; set; }

    /// <summary>The local player's facing arrow color (QC draws the own player white). Public so the integration
    /// layer can tint it (e.g. to the local team color) if desired.</summary>
    public Color LocalColorTint { get; set; } = new(0.95f, 0.95f, 0.95f, 1f);

    // ---- the real minimap image (QC minimapname + mi_min/mi_max) ----

    /// <summary>The short map name (e.g. "stormkeep"). Used to resolve the minimap image
    /// <c>gfx/&lt;map&gt;_radar</c> → <c>gfx/&lt;map&gt;_mini</c> (QC <c>minimapname</c>) through the VFS — the
    /// real Xonotic minimaps ship inside each map's pk3 as <c>gfx/&lt;map&gt;_mini.jpg</c>.</summary>
    public string? MapName { get; set; }

    /// <summary>Map world XY bounds (QC <c>mi_min</c>/<c>mi_max</c> = the BSP worldspawn model mins/maxs). The
    /// minimap image covers exactly this rect; the image and the blips share the same world→screen transform so
    /// they stay aligned as the radar rotates/zooms.</summary>
    public Vector2 MapMinXY { get; set; }
    public Vector2 MapMaxXY { get; set; }

    private bool HasMapBounds => MapMaxXY.X > MapMinXY.X && MapMaxXY.Y > MapMinXY.Y;

    public override void _Ready()
        => ClipContents = true; // QC drawsetcliparea(panel rect): clip the minimap + blips to the radar rectangle

    // ---- visual constants (the modernized stand-ins for the QC hud_panel_radar_* skin cvars) ----

    private static readonly Color BackgroundColor = new(0.05f, 0.06f, 0.08f, 0.45f);
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.18f);

    // QC HUD_Radar_GetZoomFactor / scale constants. hud_panel_radar_scale is pixels-per (mi_scale) world unit in
    // the QC; without the loaded minimap bounds we treat _scale as "how many world units fill the radar width"
    // via a reference divisor so the cvar still meaningfully zooms. Larger _scale = wider world coverage.
    private const float ScaleReferenceDivisor = 4f; // tuned so the luma default 8192 ≈ 2048u radius

    /// <summary>The radar reads live net state, so it must repaint every frame.</summary>
    public override void _Process(double delta) => QueueRedraw();

    // =================================================================================================
    //  Identity / layout (luma defaults: radar sits in the top-left corner with a corner frame)
    // =================================================================================================

    /// <summary>QC <c>panel.panel_name</c> = "radar".</summary>
    public override string PanelId => "radar";

    // The luma table already carries radar's pos/size/bg (0 0 / 0.2 0.25 / border_corner_northwest), so we do
    // NOT override DefaultLayout/DefaultBg — the base virtuals look them up. The standalone NetGame path sets
    // Position/Size manually and never calls LoadConfig, so those defaults only apply to the discovered panel.

    // -------------------------------------------------------------------------------------------------
    //  Behaviour-cvar defaults (HudConfig invokes this by reflection). Mirrors radar.qh + teamradar defaults.
    // -------------------------------------------------------------------------------------------------
    public static void RegisterDefaults(CvarService c)
    {
        const CvarFlags S = CvarFlags.Save;
        const string P = "hud_panel_radar_";

        c.Register(P + "foreground_alpha", "1", S);   // teamradar default-fills to 0.8 when 0; we use 1
        c.Register(P + "rotation", "0", S);            // 0 = player-aligned, 1..3 = fixed 90°·rotation
        c.Register(P + "zoommode", "0", S);            // 0 default zoom fraction, 1 inverse-zoom, 2 normal, 3 big
        c.Register(P + "scale", "8192", S);            // pixels-per-world-unit-ish; bigger = wider coverage
        c.Register(P + "maximized_scale", "5120", S);
        c.Register(P + "maximized_size", "0.5 0.5", S);
        c.Register(P + "maximized_rotation", "0", S);
        c.Register(P + "maximized_zoommode", "0", S);
        c.Register(P + "dynamichud", "1", S);
    }

    protected override void DrawPanel()
    {
        ClientNet? net = Net;
        if (net is null)
            return; // self-blank: no data → draw nothing

        // Draw in the live control size so this works both as a discovered panel (Size == Cfg.SizePx, set by
        // LoadConfig) and standalone (Size set manually by NetGame). panel-local space: origin = top-left.
        Vector2 size = Size;
        float radius = Mathf.Min(size.X, size.Y) * 0.5f;
        if (radius <= 0f)
            return;
        Vector2 center = size * 0.5f;

        // ---- skin border frame (QC HUD_Panel_DrawBg → draw_BorderPicture). No-op when the panel's bg is "0". ----
        DrawBackground();

        // Foreground alpha (QC hud_panel_radar_foreground_alpha * panel_fg_alpha). When discovered it folds the
        // HUD/scoreboard fade in via LiveFgAlpha; standalone LiveFgAlpha defaults to ~0.9, which is fine.
        // QC teamradar_loadcvars: a foreground_alpha of 0 (or unset) defaults to 0.8 — match that faithfully so a
        // skin that zeroes it still shows the radar rather than blanking. NaN/Inf cvars collapse to the default.
        float fgCvar = CvarF("foreground_alpha", 1f);
        if (!float.IsFinite(fgCvar) || fgCvar <= 0f)
            fgCvar = 0.8f;
        float fgAlpha = Mathf.Clamp(fgCvar, 0f, 1f) * Mathf.Clamp(LiveFgAlpha, 0f, 1f);
        if (fgAlpha <= 0.001f)
            return;

        // Background playfield (rectangular, like the QC radar panel). The minimap image draws over it; areas
        // outside the map (or before the image loads) stay this dark fill.
        DrawRect(new Rect2(Vector2.Zero, size), WithAlpha(BackgroundColor, BackgroundColor.A * fgAlpha / 0.9f));

        // ---- rotation (QC HUD_Radar_GetAngle) ----
        // _rotation 0  → player-aligned: counter-rotate by the player's yaw so facing points up. QC returns
        //                (view_angles.y - 90)·DEG2RAD — the -90 makes a player facing north (Quake yaw 90)
        //                leave the map un-rotated, so their facing ends up "up" after the screen-Y flip below.
        // _rotation !0 → fixed: lock the map to a cardinal (90°·rotation), independent of the player's facing.
        // The view yaw is host-supplied each frame and could be uninitialised / NaN before the first net update;
        // a NaN yaw would poison cos/sin and turn every blip into NaN. Sanitise it to a finite angle.
        float localYaw = float.IsFinite(LocalYawDegrees) ? LocalYawDegrees : 0f;
        float rotationCvar = CvarF("rotation", 0f);
        int rotation = float.IsFinite(rotationCvar) ? (int)rotationCvar : 0;
        float radarYawDeg = rotation != 0 ? 90f * rotation : localYaw - 90f;
        float yawRad = Mathf.DegToRad(radarYawDeg);
        float cos = Mathf.Cos(-yawRad);
        float sin = Mathf.Sin(-yawRad);

        // ---- zoom / scale (QC HUD_Radar_GetZoomFactor + teamradar_size blend) ----
        // QC blends teamradar_size between a "big" size (fit whole arena) and a "normal" size (fixed scale) by a
        // zoom factor. We have no loaded map bounds, so we model: normalRange = world units that fill the radius
        // at the cvar scale; bigRange = RangeUnits (the arena-scale fit fallback). zoomFactor 1 → big, 0 → normal.
        float scale = CvarF("scale", 8192f);
        if (!float.IsFinite(scale) || scale <= 0f) scale = 4096f; // QC teamradar_loadcvars default
        float normalRange = scale / ScaleReferenceDivisor; // world units edge-to-center at this scale
        float bigRange = Mathf.Max(RangeUnits, normalRange);
        float zoomCvar = CvarF("zoommode", 0f);
        int zoommode = float.IsFinite(zoomCvar) ? (int)zoomCvar : 0;
        float zoomFactor = Mathf.Clamp(GetZoomFactor(zoommode), 0f, 1f);
        float rangeUnits = Mathf.Lerp(normalRange, bigRange, zoomFactor);
        // Guard against a non-finite or non-positive range (bad cvars / RangeUnits) before it becomes a divisor.
        if (!float.IsFinite(rangeUnits))
            rangeUnits = bigRange;
        rangeUnits = Mathf.Max(rangeUnits, 1f);

        float worldToPixels = radius / rangeUnits;
        NVec3 selfOrigin = CenterOrigin ?? net.PredictedOrigin;
        // A non-finite radar center would turn every blip delta into NaN; if it is bad, skip the contacts entirely
        // and only draw the (origin-independent) center arrow below.
        bool selfFinite = float.IsFinite(selfOrigin.X) && float.IsFinite(selfOrigin.Y);
        float now = net.LatestServerTime;
        if (!float.IsFinite(now))
            now = 0f;
        int localId = net.LocalNetId;

        // World ground-plane XY → radar screen — the shared transform (QC teamradar_texcoord_to_2dcoord, but in
        // world units): delta from the radar center, Rotate by the radar angle, screen-Y flip, scale, recenter.
        // The minimap image AND every blip go through this, so they always stay aligned.
        Vector2 WorldToScreen(float wx, float wy)
        {
            float ddx = wx - selfOrigin.X, ddy = wy - selfOrigin.Y;
            float rrx = ddx * cos - ddy * sin, rry = ddx * sin + ddy * cos;
            return center + new Vector2(rrx, -rry) * worldToPixels;
        }

        // ---- the real minimap image (QC draw_teamradar_background): a rotated, player-centered textured quad
        // covering the map's world bounds [mi_min, mi_max]. The corners map through WorldToScreen; the UVs match
        // QC's yinvert (image Y is top-down, world Y bottom-up). Clipped to the panel rect by ClipContents. ----
        if (selfFinite && HasMapBounds && !string.IsNullOrEmpty(MapName))
        {
            Texture2D? mini = TextureCache.GetFirst($"gfx/{MapName}_radar", $"gfx/{MapName}_mini");
            if (mini is not null)
            {
                float x0 = MapMinXY.X, y0 = MapMinXY.Y, x1 = MapMaxXY.X, y1 = MapMaxXY.Y;
                var pts = new[] { WorldToScreen(x0, y0), WorldToScreen(x1, y0), WorldToScreen(x1, y1), WorldToScreen(x0, y1) };
                if (AllFinite(pts))
                {
                    var uvs = new[] { new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 0f) };
                    Color mc = new(1f, 1f, 1f, fgAlpha);
                    DrawPolygon(pts, new[] { mc, mc, mc, mc }, uvs, mini);
                }
            }
        }

        // Snapshot the remote-id set before iterating: RemoteIds is a live view over the net layer's dictionary,
        // and a snapshot/forget arriving mid-frame (or the public ForgetRemote) would otherwise throw
        // "collection was modified" out of this every-frame draw path. ToArray copies the keys defensively.
        if (selfFinite)
        {
            int[] ids = System.Linq.Enumerable.ToArray(net.RemoteIds);
            foreach (int netId in ids)
            {
                if (netId == localId)
                    continue;
                if (!net.SampleRemote(netId, now, out NVec3 origin, out NVec3 angles))
                    continue;
                if (!net.TryGetRemoteState(netId, out NetEntityState state))
                    continue;
                // QC: the radar draws ONLY players (draw_teamradar_player) here + gametype-objective icons
                // (draw_teamradar_icon over g_radaricons — CTF flags / DOM points / KH keys) drawn separately
                // below from the networked RadarObjectives. It NEVER shows regular items, projectiles or gibs.
                if (state.Kind != NetEntityKind.Player && state.Kind != NetEntityKind.Nameplate)
                    continue;
                if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y))
                    continue; // bad interpolated pose — skip rather than draw a NaN blip

                // Same transform as the minimap image (QC: blips clip to the panel rect, no rim-clamp).
                Vector2 pos = WorldToScreen(origin.X, origin.Y);
                if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
                    continue; // never hand a NaN coordinate to the drawing primitives
                DrawBlip(pos, state, angles, radarYawDeg, fgAlpha);
            }

            // ---- waypoint objective icons (QC draw_teamradar_icon over g_radaricons): CTF flags / DOM points /
            // KH keys / player pings — networked as waypoint sprites + team/rule-filtered server-side. A small
            // teamradar sprite at each waypoint's world position, tinted by its networked radar color, drawn UNDER
            // the local arrow so the player's own marker stays on top. ----
            float iconPx = Mathf.Clamp(radius * 0.07f, 7f, 12f);
            foreach (XonoticGodot.Common.Gameplay.Waypoints.WaypointNet wp in net.Waypoints)
            {
                if (wp.RadarIcon <= 0)
                    continue; // not a radar waypoint (some pings are 3D-only)
                if (!float.IsFinite(wp.Origin.X) || !float.IsFinite(wp.Origin.Y))
                    continue;
                Vector2 op = WorldToScreen(wp.Origin.X, wp.Origin.Y);
                if (!float.IsFinite(op.X) || !float.IsFinite(op.Y))
                    continue;
                Color oc = new(wp.Color.X, wp.Color.Y, wp.Color.Z, fgAlpha * Mathf.Clamp(wp.Fade, 0f, 1f));
                Texture2D? icon = TextureCache.Get(wp.RadarIcon == 2 ? "gfx/teamradar_icon_2" : "gfx/teamradar_icon_1");
                var orect = new Rect2(op - new Vector2(iconPx, iconPx) * 0.5f, new Vector2(iconPx, iconPx));
                if (icon is not null)
                    DrawTextureRect(icon, orect, false, oc);
                else
                    DrawRect(orect, oc); // fallback marker if the teamradar sprite is missing
            }
        }

        // Local player last, on top, at the exact center: a facing arrow (QC draws own player white at view_angles).
        DrawPlayerArrow(center, localYaw, radarYawDeg, LocalColorTint, fgAlpha, isLocal: true);

        // Rectangular frame edge (subtle), drawn last inside the clip so it reads over the map image.
        DrawRect(new Rect2(Vector2.One, size - new Vector2(2f, 2f)),
            WithAlpha(BorderColor, BorderColor.A * fgAlpha / 0.9f), filled: false, width: 1.5f);
    }

    /// <summary>True when every point is finite (guards the textured-quad / polygon draw against a NaN vertex).</summary>
    private static bool AllFinite(Vector2[] pts)
    {
        foreach (Vector2 p in pts)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                return false;
        return true;
    }

    // QC HUD_Radar_GetZoomFactor (radar.qc). current_zoomfraction (the live zoom-in lerp) isn't exposed to the
    // HUD layer here, so the "follow the player's zoom" modes (0 / 1) settle to the static endpoints.
    private static float GetZoomFactor(int zoommode) => zoommode switch
    {
        1 => 1f,  // QC 1 - current_zoomfraction → fully zoomed-out (big) when not zooming in
        2 => 0f,  // always normal (fixed scale)
        3 => 1f,  // always big (fit arena)
        _ => 0f,  // default: current_zoomfraction → normal at rest
    };

    /// <summary>Draw one remote player contact as a team-colored facing arrow (QC <c>draw_teamradar_player</c>),
    /// dimmed when dead. Only players reach here (the loop filters everything else, like QC).</summary>
    private void DrawBlip(Vector2 pos, in NetEntityState state, NVec3 angles, float radarYawDeg, float fgAlpha)
    {
        bool dead = (state.Flags & NetEntityFlags.Dead) != 0;
        Color color = TeamColor(state.Colormap);
        float a = fgAlpha * (dead ? 0.35f : 1f);
        DrawPlayerArrow(pos, angles.Y, radarYawDeg, color, a, isLocal: false);
    }

    /// <summary>
    /// Draw a player marker as a facing-oriented arrow — the C# port of QC <c>draw_teamradar_player</c>. The QC
    /// draws two stacked quads: a slightly larger contrast quad (black behind a colored player, white behind the
    /// white own-player) and the colored arrow on top, both rotated by the entity's yaw relative to the radar
    /// angle. <paramref name="yawDeg"/> is the entity's Quake yaw; <paramref name="radarYawDeg"/> is the radar's
    /// rotation so the arrow points the right way on a rotated map.
    /// </summary>
    private void DrawPlayerArrow(Vector2 pos, float yawDeg, float radarYawDeg, Color color, float alpha, bool isLocal)
    {
        if (alpha <= 0.001f)
            return;
        // Defensive: the marker origin and the entity yaw both originate from interpolated net state and could be
        // non-finite; either would feed NaN into the polygon vertices below and corrupt the draw call.
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y))
            return;
        if (!float.IsFinite(yawDeg)) yawDeg = 0f;
        if (!float.IsFinite(radarYawDeg)) radarYawDeg = 0f;

        // QC draw_teamradar_player: MAKE_VECTORS(pangles - radar_angle), then forward.y *= -1 (screen-Y flip)
        // and right = (-forward.y, forward.x). With Quake yaw 0 = +X (east) and pitch 0, AngleVectors gives
        // forward = (cos(eyaw), sin(eyaw)); after the y-flip → (cos(eyaw), -sin(eyaw)) and right = (sin(eyaw),
        // cos(eyaw)). eyaw = entity yaw minus the radar angle. The local player (yaw - radarYaw == 90) thus
        // points straight up, exactly as QC draws own-player at view_angles on a (yaw-90)-rotated map.
        float screenYaw = Mathf.DegToRad(yawDeg - radarYawDeg);
        float c = Mathf.Cos(screenYaw), sn = Mathf.Sin(screenYaw);
        var forward = new Vector2(c, -sn);
        var right = new Vector2(sn, c);

        // QC draws every player (incl. the local one) at the SAME fixed pixel size — no per-player scaling.
        // Contrast backing quad (QC rgb2: black behind a colored arrow, white behind the white own-player), then
        // the colored arrow on top. Exact QC vertex offsets: backing (3, 4, 2.5, 2), colored (2, 3, 2, 1).
        Color contrast = IsNearWhite(color)
            ? new Color(0f, 0f, 0f, alpha)
            : new Color(1f, 1f, 1f, alpha);
        DrawArrowQuad(pos, forward, right, 3f, 4f, 2.5f, 2f, contrast);
        DrawArrowQuad(pos, forward, right, 2f, 3f, 2f, 1f, WithAlpha(color, alpha));
    }

    /// <summary>The four-vertex kite the QC builds for a radar player: tip ahead, two wings to the sides-back, a
    /// tail behind — a chevron/arrowhead pointing along <paramref name="forward"/>.</summary>
    private void DrawArrowQuad(Vector2 c, Vector2 forward, Vector2 right,
        float tip, float wing, float wingBack, float tail, Color color)
    {
        // Mirrors draw_teamradar_player's 4 R_PolygonVertex calls:
        //   coord + forward*tip            (front tip)
        //   coord + right*wing - forward*wingBack   (right wing, set back)
        //   coord - forward*tail           (tail)
        //   coord - right*wing - forward*wingBack   (left wing, set back)
        var pts = new Vector2[]
        {
            c + forward * tip,
            c + right * wing - forward * wingBack,
            c - forward * tail,
            c - right * wing - forward * wingBack,
        };
        DrawColoredPolygon(pts, color);
    }

    /// <summary>
    /// Map a player's <see cref="NetEntityState.Colormap"/> to its radar tint — the C# port of QC
    /// <c>Team_ColorRGB(entcs_GetTeam(...))</c> (teamradar.qc <c>draw_teamradar_player</c>). The server writes
    /// <c>Colormap = (int)p.Team</c> (ServerNet), so the low byte carries the real NUM_TEAM_* palette code
    /// (<see cref="Teams.Red"/>=4, <see cref="Teams.Blue"/>=13, <see cref="Teams.Yellow"/>=12,
    /// <see cref="Teams.Pink"/>=9) — NOT a 1..4 index — so we switch on those constants and return the faithful
    /// <c>Team_ColorRGB</c> RGBs (common/teams.qh:76). Anything else (FFA / unknown) is white.
    /// </summary>
    private static Color TeamColor(int colormap)
    {
        int team = colormap & 0xFF;
        return team switch
        {
            Teams.Red    => new Color(1f, 0.0625f, 0.0625f, 1f),  // Team_ColorRGB NUM_TEAM_1 (0xFF0F0F)
            Teams.Blue   => new Color(0.0625f, 0.0625f, 1f, 1f),  // NUM_TEAM_2 (0x0F0FFF)
            Teams.Yellow => new Color(1f, 1f, 0.0625f, 1f),       // NUM_TEAM_3 (0xFFFF0F)
            Teams.Pink   => new Color(1f, 0.0625f, 1f, 1f),       // NUM_TEAM_4 (0xFF0FFF)
            _            => new Color(1f, 1f, 1f, 1f),            // white (no team / FFA)
        };
    }

    private static bool IsNearWhite(Color c) => c.R > 0.85f && c.G > 0.85f && c.B > 0.85f;

    private static Color WithAlpha(Color c, float a) => new(c.R, c.G, c.B, Mathf.Clamp(a, 0f, 1f));
}
