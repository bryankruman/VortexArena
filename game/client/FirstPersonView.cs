using Godot;
using XonoticGodot.Common.Framework;   // MoveFilter
using XonoticGodot.Common.Math;        // QMath, Coords
using XonoticGodot.Common.Services;    // Api, TraceResult, PointContents
using NVec3 = System.Numerics.Vector3;
using GVec3 = Godot.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Port of qcsrc/client/view.qc (CSQC_UpdateView: zoom, eventchase, FOV) — the SINGLE first-person view
/// driver shared by BOTH the local <see cref="XonoticGodot.Game.PlayerController"/> (the GameDemo path) and the
/// networked <see cref="XonoticGodot.Game.Net.NetGame"/> (the menu Start / Create-Game play path).
///
/// Xonotic has ONE first-person path; the port had drifted into two partial re-implementations (the FULL
/// view logic in PlayerController + a STRIPPED copy in NetGame). This factors the view subsystem out so both
/// hosts feed it the same code: the zoom state machine (<c>GetCurrentFov</c>'s <c>current_viewzoom</c> integration
/// + <c>current_zoomfraction</c> + the spawn-zoom latch + <c>setsensitivityscale</c>), the FOV math with the
/// <c>*0.75</c> 4:3 aspect normalization and live zoom, the death/event-chase camera
/// (<c>View_EventChase</c> / <c>WantEventchase</c>), the exact <c>AngleVectors</c> → <c>Coords.ToGodot</c> columns →
/// <c>Basis(right, up, -forward)</c> camera placement (a negated-yaw Euler silently flips handedness, so this is
/// the proven vector path), and the eye-contents sample at the FINAL render origin (<c>HUD_Contents</c>).
///
/// <para>It is a plain helper (not a <see cref="Node"/>): the host owns the <see cref="Camera3D"/> and the
/// player/predicted state, and each frame hands this a small <see cref="ViewState"/> snapshot (origin/velocity/
/// view angles/dead) — NOT an <see cref="Entity"/> — so an Entity-backed source (PlayerController) and a
/// ClientNet-predicted source (NetGame) both plug in.</para>
/// </summary>
public sealed class FirstPersonView
{
    /// <summary>
    /// The per-frame view-state source the shared view reads (the abstraction over an owned
    /// <see cref="Entity"/> vs ClientNet-predicted state). Mirrors the inputs <c>CSQC_UpdateView</c> drives the
    /// view from: the RAW player origin (Quake; the eye/chase pivot is derived from it), the velocity (Quake;
    /// the <c>cl_eventchase_death == 2</c> settle gate reads it), whether the player is dead
    /// (<c>STAT(HEALTH) &lt;= 0</c>), and the view angles (deg, Quake pitch/yaw/roll).
    /// </summary>
    public struct ViewState
    {
        /// <summary>RAW player origin in Quake space (the eye = origin + (0,0,EyeHeight); the chase pivot is
        /// this origin lifted by cl_eventchase_viewoffset — NOT the eye, view.qc:807,823-828).</summary>
        public NVec3 OriginQuake;

        /// <summary>Player velocity in Quake space — the cl_eventchase_death==2 gate waits for it to settle to 0.</summary>
        public NVec3 VelocityQuake;

        /// <summary>View angles in DEGREES, Quake convention (X = pitch down-positive, Y = yaw, Z = roll).</summary>
        public NVec3 ViewAnglesQuake;

        /// <summary>True while the local player is dead (drives the death fade + the event-chase cam, QC
        /// STAT(HEALTH) &lt;= 0). The host decides the predicate: GameDemo uses Health&lt;=0 &amp;&amp; MaxHealth&gt;0
        /// (no fresh-spawn-at-0 false death); the net path has no MaxHealth so it is Health&lt;=0.</summary>
        public bool IsDead;

        /// <summary>Live eye height above the origin, Quake units (QC STAT(PL_VIEW_OFS) / PL_CROUCH_VIEW_OFS — drops
        /// from ~35 to ~20 while crouched, set by PlayerPhysics.UpdateCrouch). 0 = unset → fall back to the static
        /// <see cref="EyeHeight"/> (e.g. a placement call before the carrier/player view offset is known).</summary>
        public float EyeHeightZ;
    }

    /// <summary>
    /// Chase-camera mode the view runs in (QC <c>chase_active</c> / spectator cam). <see cref="ChaseMode.None"/>
    /// is the normal first-person eye; the others pull the camera back from the player along the view, tracing
    /// against the world so it doesn't clip. On local death the event-chase engages automatically regardless of
    /// this (QC <c>cl_eventchase_death</c>).
    /// </summary>
    public enum ChaseMode { None, Chase, SpectatorFollow }

    /// <summary>The host-requested chase mode (QC user <c>chase_active</c> / spectator cam). Default first-person eye.</summary>
    public ChaseMode CameraMode { get; set; } = ChaseMode.None;

    /// <summary>Set each frame by the host from the <c>+zoom</c> bind's held state (QC <c>button_zoom</c>). The
    /// host is responsible for suppressing it when dead / paused / the console is open, so the view zooms out on
    /// death (QC <c>cl_unpress_zoom_on_death</c>).</summary>
    public bool ZoomHeld { get; set; }

    /// <summary>Eye height above the entity origin, in Quake units (Xonotic PL_VIEW_OFS ~ '0 0 35').</summary>
    public float EyeHeight { get; set; } = 35f;

    /// <summary>Unzoomed vertical field of view base in degrees (Xonotic `fov 100`). The rendered fov is this
    /// scaled by the live zoom (GetCurrentFov); the `fov` cvar overrides it when set.</summary>
    public float BaseFov { get; set; } = 100f;

    /// <summary>The live zoom fraction, 0 = no zoom (full fov) .. 1 = fully zoomed (QC <c>current_zoomfraction</c>).</summary>
    public float ZoomFraction { get; private set; }

    /// <summary>The look-sensitivity multiplier the host applies to mouse-look while zoomed (QC
    /// <c>setsensitivityscale</c>: <c>current_viewzoom ** (1 - cl_zoomsensitivity)</c>). 1 when unzoomed.</summary>
    public float SensitivityScale { get; private set; } = 1f;

    /// <summary>The SUPERCONTENTS bitmask sampled at the FINAL render origin this frame (QC
    /// <c>pointcontents(view_origin)</c>, view.qc:1176) — the host feeds it to the liquid screen-tint. 0 in air.</summary>
    public int EyeContents { get; private set; }

    /// <summary>True while the event/death chase camera is engaged this frame (so the host can hide the
    /// first-person viewmodel while chase is active — view.qc viewmodel_draw masks the gun when chase_active).</summary>
    public bool ChaseActive { get; private set; }

    // --- zoom state (view.qc GetCurrentFov: current_viewzoom drives the rendered fov) ---
    // current_viewzoom in (0,1]: 1 = unzoomed, 1/zoomfactor = fully zoomed. Lerped each frame toward the target.
    private float _currentViewZoom = 1f;

    // Spawn-zoom (view.qc:512-520 + spawnpoints.qc:82-86): on local spawn current_viewzoom is snapped to
    // 1/cl_spawnzoom_factor and zoomin_effect latches; GetCurrentFov then eases the zoom back out to 1, until
    // it arrives (clears the latch) or the player manually zooms (which cancels it via IsZooming → zoomin_effect=0).
    private bool _zoomInEffect;

    // QC view.qc MAX_ZOOMFACTOR.
    private const float MaxZoomFactor = 30f;

    // --- death / event-chase camera state (view.qc View_EventChase: eventchase_current_distance) ---
    private float _eventChaseDistance;   // current (smoothed) pull-back distance behind the eye
    private bool _eventChaseRunning;     // latches the chase on once death starts (QC eventchase_running)

    /// <summary>
    /// Arm the spawn-zoom effect (QC spawnpoints.qc:82-86, the local-spawn branch). Snaps
    /// <c>current_viewzoom</c> to <c>1/cl_spawnzoom_factor</c> and latches the spawn-zoom effect; the next
    /// <see cref="UpdateView"/> eases the zoom back out to 1. No-op when <c>cl_spawnzoom</c> is off. Called by the
    /// host on the (re)spawn edge — GameDemo from <c>PlayerController.Teleport</c>, NetGame from a Health 0→&gt;0 edge.
    /// </summary>
    public void TriggerSpawnZoom()
    {
        if (Cvar("cl_spawnzoom", 1f) == 0f)
            return;
        // spawnpoints.qc:85 seeds current_viewzoom = 1/bound(1, cl_spawnzoom_factor, 16) (16, not MAX_ZOOMFACTOR).
        float spawnzoomfactor = Mathf.Clamp(Cvar("cl_spawnzoom_factor", 2f), 1f, 16f);
        _zoomInEffect = true;
        _currentViewZoom = 1f / spawnzoomfactor;
    }

    /// <summary>
    /// The CSQC_UpdateView per-frame view step (view.qc): step the zoom toward its target, place + orient the
    /// camera (with the event-chase pull-back on death / third-person), apply the live zoom to the rendered fov,
    /// and sample the eye contents at the FINAL render origin. The host calls this each physics frame after
    /// setting <see cref="ZoomHeld"/> / <see cref="CameraMode"/>, then reads back <see cref="ZoomFraction"/> /
    /// <see cref="EyeContents"/> / <see cref="SensitivityScale"/> / <see cref="ChaseActive"/>.
    /// </summary>
    public void UpdateView(Camera3D camera, in ViewState st, float dt)
    {
        if (camera is null)
            return;

        _lastDt = dt;
        UpdateZoom(dt);
        UpdateCamera(camera, st);
    }

    /// <summary>
    /// Step the zoom toward its target each frame — the port of view.qc <c>GetCurrentFov</c>'s
    /// <c>current_viewzoom</c> integration. Holding the zoom button (<see cref="ZoomHeld"/>) lerps the zoom IN at
    /// <c>cl_zoomspeed</c> toward <c>1/cl_zoomfactor</c>; releasing lerps back OUT to 1. Negative
    /// <c>cl_zoomspeed</c> snaps instantly. The rendered fov + look sensitivity follow in <see cref="UpdateCamera"/>.
    /// </summary>
    private void UpdateZoom(float dt)
    {
        bool zoomdir = ZoomHeld;

        // QC view.qc:505-506: manually zooming (button_zoom) cancels an in-progress spawn-zoom.
        if (zoomdir)
            _zoomInEffect = false;

        float zoomfactor = Cvar("cl_zoomfactor", 2.5f);
        if (zoomfactor < 1f || zoomfactor > MaxZoomFactor) zoomfactor = 2.5f;
        float zoomspeed = Cvar("cl_zoomspeed", 3.5f);
        if (zoomspeed >= 0f && (zoomspeed < 0.5f || zoomspeed > 16f)) zoomspeed = 3.5f;

        // Spawn-zoom takes precedence over the normal/instant zoom while latched (QC view.qc:512-520: the spawn
        // branch sits ABOVE the normal zoom in GetCurrentFov's else-if chain). Ease current_viewzoom back out
        // toward 1 from the 1/spawnzoom_factor it was snapped to on spawn, then clear the latch when it arrives.
        if (Cvar("cl_spawnzoom", 1f) != 0f && _zoomInEffect)
        {
            float spawnzoomfactor = Mathf.Clamp(Cvar("cl_spawnzoom_factor", 2f), 1f, MaxZoomFactor);
            float spawnzoomspeed = Cvar("cl_spawnzoom_speed", 1f);
            _currentViewZoom += spawnzoomspeed * (spawnzoomfactor - _currentViewZoom) * dt;
            _currentViewZoom = Mathf.Clamp(_currentViewZoom, 1f / spawnzoomfactor, 1f);
            if (_currentViewZoom == 1f)
                _zoomInEffect = false;
        }
        else if (zoomspeed < 0f) // instant zoom
        {
            _currentViewZoom = zoomdir ? 1f / zoomfactor : 1f;
        }
        else if (zoomdir)
        {
            // zoom in: drive 1/current_viewzoom up toward zoomfactor, bounded.
            _currentViewZoom = 1f / Mathf.Clamp(
                1f / _currentViewZoom + dt * zoomspeed * (zoomfactor - 1f), 1f, zoomfactor);
        }
        else
        {
            // zoom out: drive current_viewzoom up toward 1, bounded below by 1/zoomfactor.
            _currentViewZoom = Mathf.Clamp(
                _currentViewZoom + dt * zoomspeed * (1f - 1f / zoomfactor), 1f / zoomfactor, 1f);
        }

        // current_zoomfraction: 0 at full fov, 1 fully zoomed (QC GetCurrentFov tail).
        if (zoomfactor == 1f || _currentViewZoom > 0.999f)
            ZoomFraction = 0f;
        else if (Mathf.Abs(_currentViewZoom - 1f / zoomfactor) < 1e-4f)
            ZoomFraction = 1f;
        else
            ZoomFraction = (_currentViewZoom - 1f) / (1f / zoomfactor - 1f);

        // Scale mouse sensitivity while zoomed (QC setsensitivityscale: current_viewzoom^(1-zoomsensitivity)).
        float zoomsensitivity = Cvar("cl_zoomsensitivity", 0f);
        SensitivityScale = zoomsensitivity < 1f
            ? Mathf.Pow(_currentViewZoom, 1f - zoomsensitivity)
            : 1f;
    }

    /// <summary>
    /// The pure FOV computation (no camera) — exposed for headless testing of the zoom math. Xonotic's `fov` cvar
    /// is HORIZONTAL at a 4:3 reference; Godot's Camera3D.Fov is the VERTICAL fov (KeepHeight). QC: frustumy =
    /// tan(fov/2) * 0.75 * current_viewzoom (the 0.75 = 3/4 aspect normalization), then fovy = atan(frustumy)*2.
    /// Without the 0.75 the scene is ~16° too wide (fov 100 → 100° vertical instead of Xonotic's ~83.6°).
    /// </summary>
    public static float ComputeVerticalFov(float baseFovDegrees, float currentViewZoom)
    {
        float frustumy = Mathf.Tan(Mathf.DegToRad(baseFovDegrees) * 0.5f) * 0.75f * currentViewZoom;
        return Mathf.RadToDeg(Mathf.Atan(frustumy) * 2f);
    }

    /// <summary>The live <c>current_viewzoom</c> (1 = unzoomed, 1/zoomfactor = fully zoomed) — exposed for tests.</summary>
    public float CurrentViewZoom => _currentViewZoom;

    /// <summary>Place + orient the camera from the player's origin (eye height) and the view angles, applying the
    /// event-chase pull-back on death / third-person and the live zoom to the rendered fov.</summary>
    private void UpdateCamera(Camera3D camera, in ViewState st)
    {
        // Orientation: derive the camera basis from the SAME Quake view vectors the sim and aiming use
        // (QMath.AngleVectors), converted axis-by-axis into Godot space. This guarantees the camera looks
        // exactly where movement goes and where AimForwardQuake shoots. Building it from a separate negated-yaw
        // Euler silently disagreed by a handedness flip (camera yaw and the wishdir yaw counter-rotated), so
        // WASD/aim drifted from the view — aligned only near a 45° heading. (Godot Basis(x,y,z) takes the
        // basis COLUMNS; the camera looks down its local -Z, so Z column = -forward.)
        QMath.AngleVectors(st.ViewAnglesQuake, out NVec3 fq, out NVec3 rq, out NVec3 uq);
        GVec3 right = Coords.ToGodot(rq);
        GVec3 up = Coords.ToGodot(uq);
        GVec3 fwd = Coords.ToGodot(fq);
        camera.GlobalBasis = new Basis(right, up, -fwd);

        // Eye position in Quake space; the event-chase camera pulls back from the RAW player origin (lifted by
        // cl_eventchase_viewoffset, not the eye) along -forward on death (or when the host forces third person).
        // View_EventChase traces the box back so the camera doesn't clip walls. Returns the eye in first-person.
        // Eye height follows the live view offset (lowers while crouched, QC STAT(PL_VIEW_OFS)/PL_CROUCH_VIEW_OFS);
        // a 0 (unset) ViewState falls back to the static standing eye height.
        float eyeZ = st.EyeHeightZ > 0f ? st.EyeHeightZ : EyeHeight;
        NVec3 eyeQuake = st.OriginQuake + new NVec3(0f, 0f, eyeZ);
        NVec3 camQuake = ApplyEventChase(st, eyeQuake, fq);
        camera.GlobalPosition = Coords.ToGodot(camQuake);

        // QC HUD_Contents samples pointcontents(view_origin) at the FINAL render origin (view.qc:1176) — the
        // pulled-back cam during a chase, the eye otherwise — so the liquid tint reflects where the camera is.
        EyeContents = Api.Services is not null ? Api.Trace.PointContents(camQuake) : 0;

        // Apply the live zoom to the rendered fov (QC GetCurrentFov). See ComputeVerticalFov for the *0.75 note.
        float baseFov = Cvar("fov", BaseFov);
        if (baseFov < 1f) baseFov = BaseFov;
        camera.Fov = ComputeVerticalFov(baseFov, _currentViewZoom);
    }

    /// <summary>
    /// The death / event-chase camera — port of view.qc <c>View_EventChase</c> + <c>WantEventchase</c>. When the
    /// local player is dead (QC <c>cl_eventchase_death</c>), or the host requests third person
    /// (<see cref="CameraMode"/>), pull the camera back along <c>-forward</c> from a pivot at the RAW player
    /// origin lifted by <c>cl_eventchase_viewoffset</c> (NOT the eye — view.qc:807,823-828), growing the distance
    /// smoothly (QC <c>eventchase_current_distance</c>) and box-tracing against the world so the camera stops
    /// short of geometry rather than clipping through it. Returns the eye unchanged in normal first-person.
    /// </summary>
    private NVec3 ApplyEventChase(in ViewState st, NVec3 eyeQuake, NVec3 forwardQuake)
    {
        // QC WantEventchase (view.qc): cl_eventchase_death == 1 engages the chase IMMEDIATELY on death; == 2 (the
        // SHIPPED default) waits until the corpse stops sliding (velocity == '0 0 0'), then LATCHES until respawn;
        // == 0 disables the death chase. The local player keeps being simulated while dead, so Quake friction
        // settles its velocity to exactly zero on the ground — making the QC velocity gate fire as intended.
        float chaseDeath = Cvar("cl_eventchase_death", 2f);
        bool deathChase = st.IsDead && (
            chaseDeath == 1f
            || (chaseDeath == 2f && (st.VelocityQuake == NVec3.Zero || _eventChaseRunning)));
        bool wantChase = CameraMode != ChaseMode.None || deathChase;
        ChaseActive = wantChase;

        if (!wantChase)
        {
            // reset so the next death starts the pull-back from 0 (QC sets eventchase_current_distance = 0).
            _eventChaseRunning = false;
            _eventChaseDistance = 0f;
            return eyeQuake;
        }
        _eventChaseRunning = true;

        // QC cl_eventchase_mins/maxs "-12 -12 -8"/"12 12 8" (xonotic-client.cfg:217-218). Needed both for the
        // viewoffset ceiling clamp (uses maxs.z) and the pull-back box-trace.
        NVec3 mins = new(-12f, -12f, -8f), maxs = new(12f, 12f, 8f);

        // QC view.qc:807,812,823-828: the pull-back PIVOT is the RAW player origin (csqcplayer.origin /
        // pmove_org), lifted by cl_eventchase_viewoffset "0 0 20" (xonotic-client.cfg:219) — NOT the eye.
        // The lift is ceiling-aware: trace world-up by view_offset + maxs.z; if clear take the full offset,
        // else clamp the rise so the camera box (height maxs.z) stays below the blocking surface.
        NVec3 pivot = st.OriginQuake;
        NVec3 viewOffset = new(0f, 0f, 20f);
        if (viewOffset != NVec3.Zero && Api.Services is not null)
        {
            NVec3 ceilTo = pivot + viewOffset + new NVec3(0f, 0f, maxs.Z);
            TraceResult ct = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, ceilTo, MoveFilter.WorldOnly, null);
            if (ct.Fraction == 1f)
                pivot += viewOffset;
            else
                pivot.Z += Mathf.Max(0f, (ct.EndPos.Z - pivot.Z) - maxs.Z);
        }
        else if (viewOffset != NVec3.Zero)
        {
            pivot += viewOffset;
        }

        float chaseDistance = Cvar("cl_eventchase_distance", 140f);
        float chaseSpeed = Cvar("cl_eventchase_speed", 1.3f);

        // ease the distance out (slow down the further back we get) — QC eventchase_current_distance integration.
        // A frametime-scaled exponential approach, as in QC.
        float frametime = _lastDt > 0f ? _lastDt : 0.0166667f;
        if (chaseSpeed != 0f && _eventChaseDistance < chaseDistance)
            _eventChaseDistance += chaseSpeed * (chaseDistance - _eventChaseDistance) * frametime;
        else if (!Mathf.IsEqualApprox(_eventChaseDistance, chaseDistance))
            _eventChaseDistance = chaseDistance;

        NVec3 target = pivot - forwardQuake * _eventChaseDistance;

        // Box-trace from the pivot to the target against the world only (QC WarpZone_TraceBox MOVE_WORLDONLY): a
        // small box so the camera keeps a little clearance from walls (QC cl_eventchase_mins/maxs).
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(pivot, mins, maxs, target, MoveFilter.WorldOnly, null);
            if (tr.StartSolid)
            {
                // Camera box started in solid (pivot against a wall): fall back to a line trace (QC behaviour) and
                // stop just short, lifted off the surface by the box extent.
                TraceResult lt = Api.Trace.Trace(pivot, NVec3.Zero, NVec3.Zero, target, MoveFilter.WorldOnly, null);
                return lt.EndPos - forwardQuake * mins.Z;
            }
            return tr.EndPos;
        }
        return target;
    }

    // The dt of the latest UpdateView, so ApplyEventChase advances the smoothed distance against the same step.
    private float _lastDt;

    /// <summary>
    /// Read a float cvar live, falling back to <paramref name="fallback"/> only when the cvar is genuinely unset
    /// (its string value is empty). An explicit <c>0</c> is honored — important for toggle cvars like
    /// <c>cl_eventchase_death 0</c> where 0 must not be reinterpreted as "use the default".
    /// </summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
