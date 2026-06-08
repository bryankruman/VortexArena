using Godot;
using XonoticGodot.Game.Net;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Top-down radar overlay — the C# successor to QuakeC's <c>hud_panel_radar</c> /
/// <c>ent_cs</c> minimap (Base/.../qcsrc/client/hud/panel/radar.qc + the <c>entcs</c> read path). In
/// QC the radar walked the <c>entcs</c> entities — the lightweight position+team+health slice the
/// server replicates for <i>every</i> player so they show on the map even when out of view — and
/// stamped a team-colored blip per player onto a rotated, player-centered minimap.
///
/// Here that same data arrives through <see cref="ClientNet"/>: the predicted local origin
/// (<see cref="ClientNet.PredictedOrigin"/>) is the radar center, and every other networked entity is
/// sampled by id (<see cref="ClientNet.RemoteIds"/> → <see cref="ClientNet.SampleRemote"/> for the
/// interpolated world position, <see cref="ClientNet.TryGetRemoteState"/> for its kind/team/flags). We
/// modernize rather than port 1:1 — the cvar skin, the rotating background texture, the zoom and the
/// teamradar foreground image are dropped; what remains is the contract: a circle centered on the local
/// player, rotated to their facing, with a colored dot per entity placed by its networked position.
///
/// Unlike the other panels this is a plain <see cref="Control"/> (not a <c>HudPanel</c>): it draws from
/// the live net stream rather than the local <c>Player</c> actor, and the host drives its facing each
/// frame via <see cref="LocalYawDegrees"/>.
///
/// <para><b>Coordinate mapping.</b> Quake's ground plane is X (forward/east) and Y (left/north); the radar
/// is player-relative and north-up means "the direction the player faces points up the screen". So we take
/// the world delta <c>(d = entity.XY − PredictedOrigin.XY)</c>, rotate it by <c>−LocalYawDegrees</c> into
/// the player's frame, then map to screen pixels as <c>screen_x = +d.x</c>, <c>screen_y = −d.y</c> — the
/// Y flip because Godot screen-Y grows downward while Quake +Y is north. With yaw 0 the result is
/// north-up; as the player turns, the whole field counter-rotates so their facing stays pointing up.</para>
/// </summary>
public partial class RadarPanel : Control
{
    /// <summary>
    /// The network source. The radar reads the local player's predicted position and every remote
    /// entity's interpolated pose/state from this. <c>null</c> until the host wires up a live session;
    /// the panel draws nothing while it is.
    /// </summary>
    public ClientNet? Net { get; set; }

    /// <summary>
    /// World radius (Quake units) the radar circle covers from its center to its edge. Entities farther
    /// than this are clamped onto the rim (so off-radar contacts still show a bearing). QC default zoom
    /// covered a few thousand units; 2500 is a reasonable arena-scale default.
    /// </summary>
    [Export] public float RangeUnits { get; set; } = 2500f;

    /// <summary>
    /// The local player's view yaw (Quake yaw, degrees) — set by the host each frame so the radar rotates
    /// with the player and the direction they face points "up". Left at its default 0 the radar is
    /// north-up (world +Y at the top) and does not rotate.
    /// </summary>
    [Export] public float LocalYawDegrees { get; set; }

    // ---- visual constants (the modernized stand-ins for the QC hud_panel_radar_* skin cvars) ----

    private static readonly Color BackgroundColor = new(0.05f, 0.06f, 0.08f, 0.45f);
    private static readonly Color BorderColor = new(1f, 1f, 1f, 0.18f);
    private static readonly Color LocalColor = new(0.95f, 0.95f, 0.95f, 0.95f);
    private static readonly Color ProjectileColor = new(1f, 0.6f, 0.1f, 0.9f);
    private static readonly Color GenericColor = new(0.6f, 0.6f, 0.6f, 0.8f);

    private const float PlayerBlipRadius = 4f;
    private const float ProjectileBlipRadius = 2f;
    private const float GenericBlipRadius = 2f;

    /// <summary>The radar reads live net state, so it must repaint every frame.</summary>
    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        ClientNet? net = Net;
        if (net is null)
            return;

        float radius = Mathf.Min(Size.X, Size.Y) * 0.5f;
        if (radius <= 0f)
            return;
        Vector2 center = Size * 0.5f;

        // Background disc + rim (QC radar background + teamradar foreground border).
        DrawCircle(center, radius, BackgroundColor);
        DrawArc(center, radius, 0f, Mathf.Tau, 48, BorderColor, 1.5f, antialiased: true);

        // Rotation that brings world space into the player's frame: rotate the world delta by −yaw so the
        // facing direction lines up with "up". Precompute the cos/sin once for the whole pass.
        float yawRad = Mathf.DegToRad(LocalYawDegrees);
        float cos = Mathf.Cos(-yawRad);
        float sin = Mathf.Sin(-yawRad);

        float worldToPixels = radius / Mathf.Max(RangeUnits, 1f);
        NVec3 selfOrigin = net.PredictedOrigin;
        float now = net.LatestServerTime;
        int localId = net.LocalNetId;

        foreach (int netId in net.RemoteIds)
        {
            if (netId == localId)
                continue;
            if (!net.SampleRemote(netId, now, out NVec3 origin, out _))
                continue;
            if (!net.TryGetRemoteState(netId, out NetEntityState state))
                continue;
            if (state.Kind == NetEntityKind.None || state.Kind == NetEntityKind.ViewModel)
                continue; // not a real-world contact

            // Ground-plane delta in world units (Quake X/Y), then rotated into the player's frame.
            float dx = origin.X - selfOrigin.X;
            float dy = origin.Y - selfOrigin.Y;
            float rx = dx * cos - dy * sin;
            float ry = dx * sin + dy * cos;

            // World → pixels. screen_y is negated so world +Y (north / player-forward) is up on screen.
            Vector2 blip = new(rx * worldToPixels, -ry * worldToPixels);

            // Clamp off-radar contacts onto the rim so they still read as a bearing.
            float lenSq = blip.LengthSquared();
            if (lenSq > radius * radius && lenSq > 0f)
                blip *= radius / Mathf.Sqrt(lenSq);

            Vector2 pos = center + blip;
            DrawBlip(pos, state);
        }

        // Local player last, on top, at the exact center: an arrow pointing up (the player faces up).
        DrawLocalArrow(center);
    }

    /// <summary>Draw one remote contact: players are team-colored ~4px discs (dimmed if dead), projectiles
    /// small orange dots, everything else small grey dots.</summary>
    private void DrawBlip(Vector2 pos, in NetEntityState state)
    {
        switch (state.Kind)
        {
            case NetEntityKind.Player:
            case NetEntityKind.Nameplate: // ent_cs lightweight player slice — same blip
            {
                bool dead = (state.Flags & NetEntityFlags.Dead) != 0;
                Color color = TeamColor(state.Colormap);
                if (dead)
                    color = new Color(color.R, color.G, color.B, color.A * 0.35f);
                DrawCircle(pos, PlayerBlipRadius, color);
                break;
            }
            case NetEntityKind.Projectile:
                DrawCircle(pos, ProjectileBlipRadius, ProjectileColor);
                break;
            default: // Item / Gib / Generic
                DrawCircle(pos, GenericBlipRadius, GenericColor);
                break;
        }
    }

    /// <summary>The local player's marker: a small filled triangle pointing up (toward the player's facing).</summary>
    private void DrawLocalArrow(Vector2 center)
    {
        const float h = 6f; // half-height (tip distance from center)
        const float w = 4f; // half-width at the base
        var pts = new Vector2[]
        {
            center + new Vector2(0f, -h),  // tip (up = facing)
            center + new Vector2(-w, h),   // back-left
            center + new Vector2(w, h),    // back-right
        };
        DrawColoredPolygon(pts, LocalColor);
    }

    /// <summary>
    /// Map a player's <see cref="NetEntityState.Colormap"/> to its radar tint. The <c>ent_cs</c> slice
    /// carries the team in the colormap (QC packs top/bottom colors into one byte; team play uses the low
    /// nibble), so we mask it: 1=red, 2=blue, 3=yellow, 4=pink, anything else white (FFA / unknown).
    /// </summary>
    private static Color TeamColor(int colormap)
    {
        int team = colormap & 0x0F;
        return team switch
        {
            1 => new Color(0.95f, 0.2f, 0.2f, 0.95f),  // red
            2 => new Color(0.2f, 0.45f, 0.95f, 0.95f), // blue
            3 => new Color(0.95f, 0.9f, 0.2f, 0.95f),  // yellow
            4 => new Color(0.95f, 0.3f, 0.8f, 0.95f),  // pink
            _ => new Color(0.9f, 0.9f, 0.9f, 0.95f),   // white (no team)
        };
    }
}
