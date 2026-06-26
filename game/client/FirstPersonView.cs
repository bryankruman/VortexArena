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

        /// <summary>True while the local player is Freeze-Tag frozen (QC STAT(FROZEN)). With cl_eventchase_frozen set
        /// this engages the event-chase third-person camera (QC MUTATOR_HOOKFUNCTION(cl_ft, WantEventchase)).</summary>
        public bool IsFrozen;

        /// <summary>The view-origin recoil kick in Quake space (QC <c>view_punchvector</c>, decayed 30u/s). Added to
        /// the rendered eye in FIRST PERSON ONLY (<c>vieworg += view_punchvector</c>, cl_player.qc:570 — inside the
        /// non-chase branch), so it kicks the camera without skewing the aim. Suppressed while chase is active.</summary>
        public NVec3 PunchOriginQuake;

        /// <summary>True while the player is standing on the ground (QC <c>IS_ONGROUND</c>). Drives the horizontal
        /// view-bob smooth ramp (<c>cl_bob2</c>) and the fall-bob swing trigger (<c>cl_bobfall</c>).</summary>
        public bool OnGround;

        /// <summary>True while the jump button is held this frame (QC <c>input_buttons &amp; BIT(1)</c>). Blocks the
        /// horizontal-bob smooth ramp so bunny-hopping doesn't twitch the view (cl_player.qc:380).</summary>
        public bool JumpHeld;
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

    /// <summary>True while the local client is spectating/following another player (QC <c>spectatee_status &gt; 0</c>).
    /// Gates the <c>chase_front</c> frontal selfie cam, which Base allows ONLY while spectating
    /// (<c>CSQCPlayer_ApplyChase</c>: <c>if (autocvar_chase_front &amp;&amp; spectatee_status)</c>). The host sets it each
    /// frame from the follow state so the spectator third-person camera can flip to the frontal view.</summary>
    public bool Spectating { get; set; }

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

    // --- view-effect clock (QC `time`): accumulated from dt so the sin-cycle bob / idle-wave advance in real time
    // even though FirstPersonView is a plain helper with no engine clock of its own. ---
    private float _viewTime;

    // --- bob / fall-bob / death-tilt state (cl_player.qc CSQCPlayer_ApplyBobbing / ApplyDeathTilt) ---
    private float _bob2Smooth;    // QC bob2_smooth: 1 while grounded, decays to 0 when airborne (smooths cl_bob2 out)
    private float _bobfallSwing;  // QC bobfall_swing
    private float _bobfallSpeed;  // QC bobfall_speed
    private float _deathTime = -1f; // QC death_time analogue: _viewTime when death began (-1 = alive)
    private bool _wasDead;          // previous-frame dead state, to detect the death edge for _deathTime

    // --- velocity-zoom averaged speed (QC GetCurrentFov:487 `avgspeed`) ---
    private float _avgSpeed;

    // --- zoom-scroll (QC view.qc ZoomScroll): the mousewheel-driven zoom factor while zoomed ---
    private float _zoomScrollFactor;        // QC zoomscroll_factor (the eased current factor)
    private float _zoomScrollFactorTarget;  // QC zoomscroll_factor_target (the wheel target)

    // --- zoomscript auto-zoom (QC View_CheckButtonStatus: autocvar_fov <= 59.5 -> +button9) ---
    private bool _zoomScriptCaught;
    /// <summary>True while the low-fov auto-zoom (QC <c>zoomscript_caught</c>, set when <c>fov &lt;= 59.5</c>) is
    /// engaged — the reticle uses the full-alpha (1.0) branch then, not the fade (crosshair.qc:704).</summary>
    public bool ZoomScriptCaught => _zoomScriptCaught;

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
        _viewTime += dt;
        UpdateZoom(dt);
        UpdateCamera(camera, st);
    }

    /// <summary>
    /// QC <c>View_InputEvent</c> / <c>ZoomScroll</c> (view.qc:450-485): the host calls this on a mousewheel
    /// up/down event. Only takes effect while <c>cl_zoomscroll</c> is on and the player is actively zooming
    /// (held +zoom / weapon zoom). Scrolls the zoom factor target by <c>1 + min(|cl_zoomscroll_scale|, 1)</c>,
    /// clamped to <c>[1, MAX_ZOOMFACTOR]</c>; the zoom then eases toward it in <see cref="UpdateZoom"/>.
    /// <paramref name="wheelUp"/> = K_MWHEELUP (zoom in, before the scale-sign flip).
    /// </summary>
    public void NotifyZoomScroll(bool wheelUp)
    {
        float scale = Cvar("cl_zoomscroll_scale", 0.2f);
        if (Cvar("cl_zoomscroll", 1f) == 0f || scale == 0f)
            return;            // zoom scroll disabled
        if (!ZoomHeld)
            return;            // QC IsZooming(true): only while actively zooming

        bool zoomin = wheelUp;
        if (scale < 0f) zoomin = !zoomin;
        // biggest change allowed is +100%
        float zoomscrollScale = 1f + Mathf.Min(Mathf.Abs(scale), 1f);
        if (_zoomScrollFactorTarget <= 0f) // seed before the first frame initialises it
            _zoomScrollFactorTarget = ResolveZoomFactor();
        if (zoomin)
            _zoomScrollFactorTarget = Mathf.Min(MaxZoomFactor, _zoomScrollFactorTarget * zoomscrollScale);
        else
            _zoomScrollFactorTarget = Mathf.Max(1f, _zoomScrollFactorTarget / zoomscrollScale);
    }

    // QC GetCurrentFov: cl_zoomfactor clamped 1..MAX_ZOOMFACTOR else 2.5.
    private static float ResolveZoomFactor()
    {
        float zoomfactor = Cvar("cl_zoomfactor", 2.5f);
        if (zoomfactor < 1f || zoomfactor > MaxZoomFactor) zoomfactor = 2.5f;
        return zoomfactor;
    }

    /// <summary>
    /// Step the zoom toward its target each frame — the port of view.qc <c>GetCurrentFov</c>'s
    /// <c>current_viewzoom</c> integration. Holding the zoom button (<see cref="ZoomHeld"/>) lerps the zoom IN at
    /// <c>cl_zoomspeed</c> toward <c>1/cl_zoomfactor</c>; releasing lerps back OUT to 1. Negative
    /// <c>cl_zoomspeed</c> snaps instantly. The rendered fov + look sensitivity follow in <see cref="UpdateCamera"/>.
    /// </summary>
    private void UpdateZoom(float dt)
    {
        // Zoomscript auto-zoom (QC View_CheckButtonStatus, view.qc:1492-1507): a very low fov (autocvar_fov <= 59.5)
        // auto-engages +button9 (zoomscript_caught), which folds into the zoom exactly like holding +zoom. Latch the
        // caught flag (for the reticle's full-alpha branch) and treat it as held-zoom for the integration below.
        float fovForScript = Cvar("fov", BaseFov);
        _zoomScriptCaught = fovForScript > 0f && fovForScript <= 59.5f;

        bool zoomdir = ZoomHeld || _zoomScriptCaught;

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
        else
        {
            // Zoom-scroll (QC GetCurrentFov:523-544): while cl_zoomscroll is on, the mousewheel-driven
            // zoomscroll_factor_target eases zoomscroll_factor toward it (frametime-independent averaging), and
            // that eased value REPLACES zoomfactor for this frame. Reset to the base factor when fully zoomed out.
            float zoomscrollScale = Cvar("cl_zoomscroll_scale", 0.2f);
            if (Cvar("cl_zoomscroll", 1f) != 0f && zoomscrollScale != 0f)
            {
                // initialise on the first frame / reset to the base factor when fully zoomed out.
                if (_zoomScrollFactor == 0f || (_currentViewZoom == 1f && !zoomdir))
                {
                    _zoomScrollFactorTarget = zoomfactor;
                    _zoomScrollFactor = zoomfactor;
                }
                if (_zoomScrollFactor != _zoomScrollFactorTarget)
                {
                    float zoomscrollSpeed = Cvar("cl_zoomscroll_speed", 16f);
                    if (Mathf.Abs(_zoomScrollFactor - _zoomScrollFactorTarget) < 0.001f || zoomscrollSpeed < 0f)
                        _zoomScrollFactor = _zoomScrollFactorTarget;
                    else if (zoomscrollSpeed != 0f)
                    {
                        float avgTime = 1f / zoomscrollSpeed;
                        float frac = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, avgTime));
                        _zoomScrollFactor = frac * _zoomScrollFactorTarget + (1f - frac) * _zoomScrollFactor;
                    }
                }
                zoomfactor = _zoomScrollFactor;
            }

            if (zoomspeed < 0f) // instant zoom
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
        float eyeZ = st.EyeHeightZ > 0f ? st.EyeHeightZ : EyeHeight;
        NVec3 eyeQuake = st.OriginQuake + new NVec3(0f, 0f, eyeZ);

        // QC cl_player.qc CalcRefdef: `if(autocvar_chase_active) vieworg = CSQCPlayer_ApplyChase(...)` runs the
        // classic user third-person cam (chase_back/up/front/overhead/pitchangle). It takes precedence over the
        // first-person + punch/bob branch, BUT the death/frozen event-chase cam (engaged via st.IsDead/IsFrozen)
        // owns those special cases — so only run the classic chase when the host requested it AND no event-chase.
        bool eventChaseWanted = WantEventChase(st);
        bool classicChase = CameraMode == ChaseMode.Chase && !eventChaseWanted;

        // The classic chase may rewrite the view angles (chase_overhead forces pitch = chase_pitchangle;
        // chase_front flips the view to look back at the player). Resolve the final angles first, then build the
        // camera basis from them so the orientation matches the chosen perspective.
        NVec3 viewAngles = st.ViewAnglesQuake;
        NVec3 camQuake;
        if (classicChase)
        {
            ChaseActive = true;
            _eventChaseRunning = false;
            _eventChaseDistance = 0f;
            camQuake = ApplyClassicChase(eyeQuake, ref viewAngles);
        }
        else
        {
            // QC cl_player.qc CalcRefdef non-chase branch (lines 564-571), in order:
            //   CSQCPlayer_ApplyDeathTilt  -> view_angles.z += deathtilt roll while dead
            //   view_angles += view_punchangle (already folded into st.ViewAnglesQuake by the host)
            //   view_angles.z += CSQCPlayer_CalcRoll -> bank when strafing (cl_rollangle)
            //   vieworg += view_punchvector (origin recoil kick, first-person only)
            //   vieworg = CSQCPlayer_ApplyBobbing -> vertical/horizontal/fall view-bob
            // All off by default in stock Xonotic (cl_bob 0 / cl_bob2 0 / cl_rollangle 0 / v_deathtilt 0), so a
            // stock match shows none of them — opt-in only. ApplyDeathTilt/CalcRoll mutate the rendered angles
            // BEFORE the event-chase forward is taken so the pull-back direction matches the rendered view.
            ApplyDeathTilt(st, ref viewAngles);
            viewAngles.Z += CalcRoll(st);

            // Eye position in Quake space; the event-chase camera pulls back from the RAW player origin (lifted by
            // cl_eventchase_viewoffset, not the eye) along -forward on death (or when the host forces third person).
            // Returns the eye in first-person. Uses the (unmodified) view forward for the pull-back direction.
            QMath.AngleVectors(viewAngles, out NVec3 efq, out _, out _);
            camQuake = ApplyEventChase(st, eyeQuake, efq);

            // QC cl_player.qc:570 `vieworg += view_punchvector` — the origin recoil kick is applied in the
            // NON-chase (first-person) branch only, so a pulled-back third-person/death cam is never jolted.
            // The view-bob likewise only applies in first person (it's inside the same non-chase branch, and
            // CSQCPlayer_ApplyBobbing early-outs when dead/in-vehicle).
            if (!ChaseActive)
            {
                camQuake += st.PunchOriginQuake;
                camQuake = ApplyBobbing(st, camQuake, viewAngles);
            }
        }

        // QC CSQCPlayer_ApplyIdleScaling runs AFTER the chase/non-chase split (cl_player.qc:573) — the idle
        // sin-wave of pitch/yaw/roll applies in both first-person and chase. Off by default (v_idlescale 0).
        ApplyIdleScaling(ref viewAngles);

        // Orientation: derive the camera basis from the SAME Quake view vectors the sim and aiming use
        // (QMath.AngleVectors), converted axis-by-axis into Godot space. This guarantees the camera looks
        // exactly where movement goes and where AimForwardQuake shoots. Building it from a separate negated-yaw
        // Euler silently disagreed by a handedness flip (camera yaw and the wishdir yaw counter-rotated), so
        // WASD/aim drifted from the view — aligned only near a 45° heading. (Godot Basis(x,y,z) takes the
        // basis COLUMNS; the camera looks down its local -Z, so Z column = -forward.)
        QMath.AngleVectors(viewAngles, out NVec3 fq, out NVec3 rq, out NVec3 uq);
        GVec3 right = Coords.ToGodot(rq);
        GVec3 up = Coords.ToGodot(uq);
        GVec3 fwd = Coords.ToGodot(fq);
        camera.GlobalBasis = new Basis(right, up, -fwd);

        camera.GlobalPosition = Coords.ToGodot(camQuake);

        // QC HUD_Contents samples pointcontents(view_origin) at the FINAL render origin (view.qc:1176) — the
        // pulled-back cam during a chase, the eye otherwise — so the liquid tint reflects where the camera is.
        EyeContents = Api.Services is not null ? Api.Trace.PointContents(camQuake) : 0;

        // Apply the live zoom to the rendered fov (QC GetCurrentFov). See ComputeVerticalFov for the *0.75 note.
        // The velocity-zoom frustum multiplier (off by default, cl_velocityzoom_enabled 0) folds into the zoom.
        float baseFov = Cvar("fov", BaseFov);
        if (baseFov < 1f) baseFov = BaseFov;
        float velocityZoom = ComputeVelocityZoom(st, dtFor: _lastDt);
        camera.Fov = ComputeVerticalFov(baseFov, _currentViewZoom * velocityZoom);
    }

    /// <summary>
    /// Velocity-based FOV zoom — port of <c>GetCurrentFov</c>'s velocityzoom block (view.qc:574-601). When
    /// <c>cl_velocityzoom_enabled</c> and <c>cl_velocityzoom_type</c> are set, the frustum is multiplied by an
    /// exponential of the low-passed player speed so the view widens (or narrows) as you accelerate. Returns 1
    /// (no effect) when disabled — the shipped default (<c>cl_velocityzoom_enabled 0</c>), so a stock match is
    /// unchanged. The averaged speed (<see cref="_avgSpeed"/>) keeps integrating even while disabled is avoided by
    /// only touching it inside the enabled branch, matching QC (it sets velocityzoom = 1 in the else).
    /// </summary>
    private float ComputeVelocityZoom(in ViewState st, float dtFor)
    {
        // cl_velocityzoom_type 0 disables velocity zoom too (QC view.qc:574 comment).
        if (Cvar("cl_velocityzoom_enabled", 0f) == 0f || Cvar("cl_velocityzoom_type", 3f) == 0f)
            return 1f;

        // QC: curspeed from the velocity projected on the view forward (type 3 = max(0, fwd·v); type 2 = fwd·v;
        // type 1/default = |v|). The intermission / spectator-2 zero branch is not on the net first-person path.
        QMath.AngleVectors(st.ViewAnglesQuake, out NVec3 forward, out _, out _);
        NVec3 v = st.VelocityQuake;
        int type = (int)Cvar("cl_velocityzoom_type", 3f);
        float curspeed = type switch
        {
            3 => Mathf.Max(0f, QMath.Dot(forward, v)),
            2 => QMath.Dot(forward, v),
            _ => v.Length(),
        };

        float vzSpeed = Cvar("cl_velocityzoom_speed", 1000f);
        float vzFactor = Cvar("cl_velocityzoom_factor", 0f);
        float vzTime = Cvar("cl_velocityzoom_time", 0.2f);

        float adapt = Mathf.Clamp(dtFor / Mathf.Max(0.000000001f, vzTime), 0f, 1f);
        _avgSpeed = _avgSpeed * (1f - adapt) + (curspeed / vzSpeed) * adapt;
        // QC float2range11(f) = f / (|f| + 1); velocityzoom = exp(float2range11(avgspeed * -factor)).
        float f = _avgSpeed * -vzFactor;
        float range11 = f / (Mathf.Abs(f) + 1f);
        return Mathf.Exp(range11);
    }

    /// <summary>
    /// Death tilt — port of <c>CSQCPlayer_ApplyDeathTilt</c> (cl_player.qc:278-287). Rolls the view over the first
    /// second of death up to <c>v_deathtiltangle</c>. Off by default (<c>v_deathtilt 0</c>) and QC-skipped under
    /// <c>cl_eventchase_death 2</c> (the shipped default) since the death-cam pulls back instead — so it only
    /// shows when a player opts into v_deathtilt AND turns the death-cam off.
    /// </summary>
    private void ApplyDeathTilt(in ViewState st, ref NVec3 viewAngles)
    {
        // Track the death edge so (time - death_time) measures real seconds since death.
        if (st.IsDead && !_wasDead)
            _deathTime = _viewTime;
        else if (!st.IsDead)
            _deathTime = -1f;
        _wasDead = st.IsDead;

        if (!st.IsDead || Cvar("v_deathtilt", 0f) == 0f)
            return;
        // QC: incompatible with cl_eventchase_death 2 (tilt is applied while the corpse is airborne then snapped
        // off on landing), so it bails when the default death-cam is active.
        if (Cvar("cl_eventchase_death", 2f) == 2f)
            return;
        float since = _deathTime >= 0f ? _viewTime - _deathTime : 0f;
        viewAngles.Z += Mathf.Min(since * 8f, 1f) * Cvar("v_deathtiltangle", 80f);
    }

    /// <summary>
    /// Idle view-wave — port of <c>CSQCPlayer_ApplyIdleScaling</c> (cl_player.qc:296-303). Sin-waves pitch/yaw/roll
    /// by <c>v_idlescale</c>. Off by default (<c>v_idlescale 0</c>).
    /// </summary>
    private void ApplyIdleScaling(ref NVec3 viewAngles)
    {
        float idlescale = Cvar("v_idlescale", 0f);
        if (idlescale == 0f)
            return;
        float t = _viewTime;
        viewAngles.X += idlescale * Mathf.Sin(t * Cvar("v_ipitch_cycle", 1f)) * Cvar("v_ipitch_level", 0.3f);
        viewAngles.Y += idlescale * Mathf.Sin(t * Cvar("v_iyaw_cycle", 2f)) * Cvar("v_iyaw_level", 0.3f);
        viewAngles.Z += idlescale * Mathf.Sin(t * Cvar("v_iroll_cycle", 0.5f)) * Cvar("v_iroll_level", 0.1f);
    }

    /// <summary>
    /// View roll when strafing — port of <c>CSQCPlayer_CalcRoll</c> (cl_player.qc:432-447). Banks the view toward
    /// the side velocity, ramping to <c>cl_rollangle</c> past <c>cl_rollspeed</c>. Off by default
    /// (<c>cl_rollangle 0</c>). Returns the roll degrees to add to view_angles.z.
    /// </summary>
    private float CalcRoll(in ViewState st)
    {
        float rollangle = Cvar("cl_rollangle", 0f);
        if (rollangle == 0f)
            return 0f;
        float rollspeed = Cvar("cl_rollspeed", 200f);
        QMath.AngleVectors(st.ViewAnglesQuake, out _, out NVec3 right, out _);
        float side = QMath.Dot(st.VelocityQuake, right);
        float sign = side < 0f ? -1f : 1f;
        side = Mathf.Abs(side);
        if (side < rollspeed)
            side *= rollangle / rollspeed;
        else
            side = rollangle;
        return side * sign;
    }

    /// <summary>
    /// View bobbing — port of <c>CSQCPlayer_ApplyBobbing</c> (cl_player.qc:321-428): vertical bob (<c>cl_bob</c>),
    /// horizontal bob (<c>cl_bob2</c>) and fall-bob (<c>cl_bobfall</c>). All off by default (<c>cl_bob 0</c>,
    /// <c>cl_bob2 0</c>) so a stock match shows no view-bob. Adds the bob offset to the eye/render origin
    /// <paramref name="v"/> in Quake space. The <c>cl_bob_limit_heightcheck</c> trace path (default 0) is omitted.
    /// </summary>
    private NVec3 ApplyBobbing(in ViewState st, NVec3 v, in NVec3 viewAngles)
    {
        // QC bails when dead / in a vehicle / not a player model — the net first-person path here is always a
        // live local player (the dead case is handled by the caller's !ChaseActive gate + event-chase pull-back).
        if (st.IsDead)
            return v;

        const float M_PI = Mathf.Pi;
        float velLimit = Cvar("cl_bob_velocity_limit", 400f);

        // vertical view bobbing
        float clBob = Cvar("cl_bob", 0f);
        float bobcycle = Cvar("cl_bobcycle", 0.5f);
        if (clBob != 0f && bobcycle != 0f)
        {
            float bobLimit = Cvar("cl_bob_limit", 7f);
            float bobup = Cvar("cl_bobup", 0.5f);
            // LordHavoc's bobup: the time at which the sin is at 180deg (lets the peak/valley be stretched).
            float cycle = _viewTime / bobcycle;
            cycle -= Mathf.Round(cycle);
            if (cycle < bobup)
                cycle = Mathf.Sin(M_PI * cycle / bobup);
            else
                cycle = Mathf.Sin(M_PI + M_PI * (cycle - bobup) / (1f - bobup));
            // bob proportional to xy-plane speed (ignore Z so jumping doesn't pump it).
            float xyspeed = Mathf.Clamp(
                Mathf.Sqrt(st.VelocityQuake.X * st.VelocityQuake.X + st.VelocityQuake.Y * st.VelocityQuake.Y),
                0f, velLimit);
            float bob = xyspeed * clBob;
            bob = Mathf.Clamp(bob, 0f, bobLimit);
            bob = bob * 0.3f + bob * 0.7f * cycle;
            v.Z += bob;
        }

        // horizontal view bobbing
        float clBob2 = Cvar("cl_bob2", 0f);
        float bob2cycle = Cvar("cl_bob2cycle", 1f);
        if (clBob2 != 0f && bob2cycle != 0f)
        {
            float cycle = _viewTime / bob2cycle;
            cycle -= Mathf.Round(cycle);
            if (cycle < 0.5f)
                cycle = Mathf.Cos(M_PI * cycle / 0.5f);
            else
                cycle = Mathf.Cos(M_PI + M_PI * (cycle - 0.5f) / 0.5f);
            float bob = clBob2 * cycle;

            // bob2_smooth eases 1->0 when we stop touching ground (also blocked while jumping, to avoid bhop twitches).
            if (st.OnGround && !st.JumpHeld)
                _bob2Smooth = 1f;
            else if (_bob2Smooth > 0f)
                _bob2Smooth -= Mathf.Clamp(Cvar("cl_bob2smooth", 0.05f), 0f, 1f);
            else
                _bob2Smooth = 0f;

            QMath.AngleVectors(viewAngles, out NVec3 fwd, out NVec3 right, out _);
            float side = Mathf.Clamp(QMath.Dot(st.VelocityQuake, right) * _bob2Smooth, -velLimit, velLimit);
            float front = Mathf.Clamp(QMath.Dot(st.VelocityQuake, fwd) * _bob2Smooth, -velLimit, velLimit);
            fwd *= bob;
            right *= bob;
            // side with forward and front with right, so the bob goes sideways when walking forward.
            v.X += side * fwd.X + front * right.X;
            v.Y += side * fwd.Y + front * right.Y;
        }

        // fall bobbing: swing the view down and back up on landing.
        float clBobfall = Cvar("cl_bobfall", 0f);
        float bobfallcycle = Cvar("cl_bobfallcycle", 3f);
        if (clBobfall != 0f && bobfallcycle != 0f)
        {
            if (!st.OnGround)
            {
                _bobfallSpeed = Mathf.Clamp(st.VelocityQuake.Z, -400f, 0f) * Mathf.Clamp(clBobfall, 0f, 0.1f);
                _bobfallSwing = st.VelocityQuake.Z < -Cvar("cl_bobfallminspeed", 200f) ? 1f : 0f;
            }
            else
            {
                _bobfallSwing = Mathf.Max(0f, _bobfallSwing - bobfallcycle * (_lastDt > 0f ? _lastDt : 0.0166667f));
                v.Z += Mathf.Sin(M_PI * _bobfallSwing) * _bobfallSpeed;
            }
        }

        return v;
    }

    /// <summary>
    /// The death / event-chase camera — port of view.qc <c>View_EventChase</c> + <c>WantEventchase</c>. When the
    /// local player is dead (QC <c>cl_eventchase_death</c>), or the host requests third person
    /// (<see cref="CameraMode"/>), pull the camera back along <c>-forward</c> from a pivot at the RAW player
    /// origin lifted by <c>cl_eventchase_viewoffset</c> (NOT the eye — view.qc:807,823-828), growing the distance
    /// smoothly (QC <c>eventchase_current_distance</c>) and box-tracing against the world so the camera stops
    /// short of geometry rather than clipping through it. Returns the eye unchanged in normal first-person.
    /// </summary>
    /// <summary>QC <c>WantEventchase</c> (view.qc): true when the death-cam (<c>cl_eventchase_death</c>) or the
    /// Freeze-Tag frozen chase (<c>cl_eventchase_frozen</c>) should engage the pull-back camera. Separated from the
    /// classic user <c>chase_active</c> cam so the two third-person geometries don't both run on the same frame.</summary>
    private bool WantEventChase(in ViewState st)
    {
        // cl_eventchase_death == 1 engages immediately on death; == 2 (shipped default) waits until the corpse stops
        // sliding (velocity == '0 0 0'), then LATCHES until respawn; == 0 disables the death chase.
        float chaseDeath = Cvar("cl_eventchase_death", 2f);
        bool deathChase = st.IsDead && (
            chaseDeath == 1f
            || (chaseDeath == 2f && (st.VelocityQuake == NVec3.Zero || _eventChaseRunning)));
        // QC MUTATOR_HOOKFUNCTION(cl_ft, WantEventchase): while frozen in Freeze Tag, cl_eventchase_frozen pulls the
        // camera to third person so the encased player can see around themselves.
        bool frozenChase = st.IsFrozen && Cvar("cl_eventchase_frozen", 0f) != 0f;
        return deathChase || frozenChase;
    }

    private NVec3 ApplyEventChase(in ViewState st, NVec3 eyeQuake, NVec3 forwardQuake)
    {
        bool wantChase = WantEventChase(st);
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

    /// <summary>
    /// The classic user third-person camera — port of <c>CSQCPlayer_ApplyChase</c> (cl_player.qc:453), engaged when
    /// <c>chase_active</c> is set and the host requested <see cref="ChaseMode.Chase"/>. Pulls the camera back along
    /// the view by <c>chase_back</c> and up by <c>chase_up</c> (the default branch), or — when
    /// <c>chase_overhead</c> is set — flattens the pitch and drops an overhead cam sampling the lowest of a 5×5
    /// trace grid. <c>chase_front</c> flips it to a frontal selfie view (only while spectating). Traces the world so
    /// the camera stops short of geometry. Mutates <paramref name="viewAngles"/> for the overhead/front variants so
    /// the caller's basis matches. Operates on the eye position <paramref name="v"/> (= QC vieworg post-smoothing).
    /// </summary>
    private NVec3 ApplyClassicChase(NVec3 v, ref NVec3 viewAngles)
    {
        // DarkPlaces engine defaults: chase_back 48, chase_up 24, chase_front 0, chase_overhead 0, chase_pitchangle 0.
        float chaseBack = Cvar("chase_back", 48f);
        float chaseUp = Cvar("chase_up", 24f);
        bool chaseFront = Cvar("chase_front", 0f) != 0f;
        bool chaseOverhead = Cvar("chase_overhead", 0f) != 0f;
        // Spectating-only test (QC CSQCPlayer_ApplyChase: `if (autocvar_chase_front && spectatee_status)`): chase_front
        // is honored ONLY while following a player. For one's own chase cam Spectating is false, so chase_front does
        // nothing — matching the QC guard. While spectating with chase_active set it engages the frontal selfie view.
        bool spectating = Spectating;

        if (chaseOverhead)
        {
            // QC: flatten pitch, sample a 5×5 grid of overhead trace destinations and keep the LOWEST ceiling hit.
            viewAngles.X = 0f;
            QMath.AngleVectors(viewAngles, out NVec3 forward, out _, out NVec3 up);

            NVec3 BackUp(NVec3 ofs) => new(
                v.X - forward.X * chaseBack + up.X * chaseUp + ofs.X,
                v.Y - forward.Y * chaseBack + up.Y * chaseUp + ofs.Y,
                v.Z - forward.Z * chaseBack + up.Z * chaseUp + ofs.Z);

            NVec3 best = TraceEnd(v, BackUp(NVec3.Zero));
            for (float ox = -16f; ox <= 16f; ox += 8f)
                for (float oy = -16f; oy <= 16f; oy += 8f)
                {
                    NVec3 end = TraceEnd(v, BackUp(new NVec3(ox, oy, 0f)));
                    if (best.Z > end.Z) best.Z = end.Z;
                }
            best.Z -= 8f;
            viewAngles.X = Cvar("chase_pitchangle", 0f);
            return best;
        }

        // Default branch: pull back along forward (negated, flipped for chase_front selfie) + lift by chase_up.
        QMath.AngleVectors(viewAngles, out NVec3 fwd, out _, out _);
        if (chaseFront && spectating)
            fwd = -QMath.Normalize(fwd);

        float cdist = -chaseBack - 8f; // QC trace "a little further" so it hits a surface consistently
        NVec3 chaseDest = new(
            v.X + fwd.X * cdist,
            v.Y + fwd.Y * cdist,
            v.Z + fwd.Z * cdist + chaseUp);

        // QC traceline(v, chase_dest, MOVE_NOMONSTERS, NULL); then back off 8 along forward + 4 along the plane normal.
        NVec3 endPos = chaseDest;
        NVec3 planeNormal = NVec3.Zero;
        if (Api.Services is not null)
        {
            TraceResult tr = Api.Trace.Trace(v, NVec3.Zero, NVec3.Zero, chaseDest, MoveFilter.WorldOnly, null);
            endPos = tr.EndPos;
            if (tr.Fraction < 1f) planeNormal = tr.PlaneNormal;
        }
        NVec3 result = new(
            endPos.X + 8f * fwd.X + 4f * planeNormal.X,
            endPos.Y + 8f * fwd.Y + 4f * planeNormal.Y,
            endPos.Z + 8f * fwd.Z + 4f * planeNormal.Z);

        if (chaseFront && spectating)
        {
            // QC: flip the view so the player looks at themselves — inverse pitch, yaw toward the (flipped) forward.
            NVec3 newAng = QMath.VecToAngles(fwd);
            viewAngles.X = -viewAngles.X;
            viewAngles.Y = newAng.Y;
        }
        return result;
    }

    /// <summary>QC <c>traceline(start, end, MOVE_NOMONSTERS, NULL)</c> → trace_endpos; world-only line trace.</summary>
    private static NVec3 TraceEnd(NVec3 start, NVec3 end)
    {
        if (Api.Services is null) return end;
        return Api.Trace.Trace(start, NVec3.Zero, NVec3.Zero, end, MoveFilter.WorldOnly, null).EndPos;
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
