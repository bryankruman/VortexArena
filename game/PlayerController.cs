using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;   // Inventory (local weapon-select on the demo path)
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Console;    // BindTable (sampled axes/buttons)
using XonoticGodot.Game.Console;      // BindInput (event → bind), ConsoleState (focus gate)
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game;

/// <summary>
/// Drives the ported Xonotic player movement (<see cref="Movement"/> -> <see cref="PlayerPhysics"/>)
/// from live Godot input, and renders the result through a child <see cref="Camera3D"/>.
///
/// The flow each physics tick:
///   1. Read WASD into a Quake-space wish-move (forward X, side Y) and Space into the jump button.
///   2. Feed the accumulated mouse-look <c>ViewAngles</c> (pitch X, yaw Y, in degrees) + wish-move into a
///      <see cref="MovementInput"/> with <c>FrameTime = delta</c>.
///   3. Call <see cref="Movement.Move"/> — the SAME deterministic sim the server runs — which integrates
///      gravity, friction/accel and the slide-and-step collision against the <see cref="CollisionWorld"/>
///      via the trace service. The player <see cref="Entity"/> is the authoritative state.
///   4. Convert the entity's Quake origin to Godot (Y-up) and place the camera at eye height, oriented
///      from the view angles.
///
/// Coordinates: the sim stays in Quake space end-to-end; we convert only when positioning the camera
/// (<see cref="Coords.ToGodot"/>) and when mapping the view yaw/pitch to Godot rotations.
/// </summary>
public partial class PlayerController : Node3D
{
    /// <summary>The authoritative movement entity (spawned in the engine entity table).</summary>
    public Entity? Player { get; private set; }

    /// <summary>The player's view camera (exposed so the client layer can parent a weapon view-model to it).</summary>
    public Camera3D Camera => _camera;

    /// <summary>
    /// Optional per-tick "touch the triggers I now overlap" pass, run right after the movement sim (QC
    /// SV_TouchTriggers). The host (e.g. <see cref="GameDemo"/>) installs the engine's
    /// <c>TriggerTouch.RunAmbient</c> here so a player walking onto a jump-pad / teleporter / hurt volume
    /// fires it — those SOLID_TRIGGER volumes are invisible to the slide-move's own collision touch. Null
    /// (the default) leaves movement standalone (the bare physics, no map triggers).
    /// </summary>
    public System.Action<Entity>? PostMoveTouch { get; set; }

    /// <summary>
    /// Live view state the first-person <see cref="Client.ViewModel"/> reads each frame to drive weapon sway
    /// (follow/lean/bob). Mirrors the inputs Xonotic's CSQC <c>viewmodel_animate</c> uses: the player's Quake
    /// velocity, the view angles (deg, Quake pitch/yaw/roll), and whether the player is on the ground.
    /// </summary>
    public Client.ViewModel.ViewState GetViewState() => new()
    {
        VelocityQuake = Player?.Velocity ?? NVec3.Zero,
        ViewAnglesQuake = _viewAngles,
        OnGround = Player?.OnGround ?? false,
    };

    /// <summary>Raised on the rising edge of the attack button (client fire hook for the view-model / effects).</summary>
    public event System.Action? Attacked;

    /// <summary>Raised when the demo weapon-switch keys (1..9) are pressed; argument is the 0-based slot.</summary>
    public event System.Action<int>? WeaponSwitched;

    /// <summary>
    /// Raised when the local player takes damage (a drop in <see cref="Player"/>'s <see cref="Entity.Health"/>
    /// detected each tick); the argument is the amount of health lost. The host wires this to the
    /// <see cref="Client.ViewEffects"/> red-flash (QC <c>.dmg_take</c> → <c>HUD_Damage</c>). Suppressed on the
    /// respawn edge (health jumping back up) so a fresh spawn doesn't read as healing.
    /// </summary>
    public event System.Action<float>? Damaged;

    /// <summary>
    /// The SUPERCONTENTS bitmask sampled at the eye this tick (QC <c>pointcontents(view_origin)</c>). The host
    /// feeds it to the liquid screen-tint (<see cref="Client.ViewEffects"/> / QC <c>HUD_Contents</c>). 0 in air.
    /// </summary>
    public int EyeContents => _view.EyeContents;

    /// <summary>True while the local player is dead (health &lt;= 0 with a real max), so the host can drive the
    /// death fade and the third-person event-chase camera (QC <c>STAT(HEALTH) &lt;= 0</c>).</summary>
    public bool IsDead => Player is { } p && p.Health <= 0f && p.MaxHealth > 0f;

    /// <summary>
    /// The live zoom fraction, 0 = no zoom (full fov) .. 1 = fully zoomed (QC <c>current_zoomfraction</c>).
    /// Exposed so the client layer can scale crosshair / sensitivity feedback if it wants.
    /// </summary>
    public float ZoomFraction => _view.ZoomFraction;

    /// <summary>
    /// The host-requested chase mode (QC user <c>chase_active</c> / spectator cam). Default first-person eye;
    /// set to <see cref="Client.FirstPersonView.ChaseMode.Chase"/> or <c>SpectatorFollow</c> for a pull-back
    /// camera. On local death the event-chase engages automatically regardless of this
    /// (QC <c>cl_eventchase_death</c>). Forwards to the shared <see cref="Client.FirstPersonView"/>.
    /// </summary>
    public Client.FirstPersonView.ChaseMode CameraMode
    {
        get => _view.CameraMode;
        set => _view.CameraMode = value;
    }

    private Camera3D _camera = null!;
    private bool _attackHeld;

    // The SHARED first-person view subsystem (zoom + FOV + chase/death cam + eye-contents), ported once from
    // qcsrc/client/view.qc and used by BOTH this (the GameDemo path) and NetGame (the menu play path). Fed an
    // Entity-backed ViewState each tick; the host reads back ZoomFraction / EyeContents / SensitivityScale.
    private readonly Client.FirstPersonView _view = new();

    // Demo cosmetic weapon slot (index into GameDemo.DemoWeapons), advanced by weapon-select binds and reported
    // via WeaponSwitched. The bind layer issues weapon commands (weapnext/weapon_group_N/…); this tracks the
    // slot for the view-model swap since the demo has no real Inventory ownership. EquipDemoWeapon wraps it.
    private int _demoSlot;

    // Tracks the active→inactive edge (console open) so held buttons are released once when input is suppressed.
    private bool _inputActive = true;

    // Accumulated view angles in DEGREES, Quake convention: X = pitch (down positive), Y = yaw, Z = roll.
    private NVec3 _viewAngles;

    // Zoom / FOV / death-chase / eye-contents now live in the shared FirstPersonView (_view). The look-
    // sensitivity multiplier while zoomed (QC setsensitivityscale) is read back from _view.SensitivityScale.

    // --- pain detection (drop in Player.Health between ticks → Damaged) ---
    private float _lastHealth = float.NaN;

    // Mouse sensitivity (degrees of view rotation per pixel of motion).
    [Export] public float MouseSensitivity { get; set; } = 0.15f;

    // Eye height above the entity origin, in Quake units (Xonotic PL_VIEW_OFS ~ '0 0 35').
    [Export] public float EyeHeight { get; set; } = 35f;

    // Unzoomed vertical field of view in degrees (Xonotic `fov 100`, xonotic-client.cfg). The rendered fov is
    // this scaled by the live zoom (GetCurrentFov); the `fov` cvar overrides it when set.
    [Export] public float BaseFov { get; set; } = 100f;

    // Standing player hull (Xonotic sv_player_mins/maxs), Quake units.
    private static readonly NVec3 HullMins = new(-16f, -16f, -24f);
    private static readonly NVec3 HullMaxs = new(16f, 16f, 45f);

    public override void _Ready()
    {
        // Spawn the movement entity in the engine entity table (created by GameInit.Boot/EngineServices).
        Entity e = Api.Entities.Spawn();
        e.ClassName = "player";
        e.MoveType = MoveType.Walk;
        e.Solid = Solid.SlideBox;
        e.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        e.Mins = HullMins;
        e.Maxs = HullMaxs;
        e.ViewOfs = new NVec3(0f, 0f, EyeHeight);
        // Spawn at full health (Xonotic start_health/start_armorvalue 100) so the view subsystem has a valid
        // health model: the damage red-flash keys off a drop from this, and the death/event-chase cam triggers
        // when it reaches 0 (without this the entity's health is 0 and HUD_Damage would read a permanent death).
        e.Health = 100f;
        e.MaxHealth = 100f;
        // Damageable, so trigger_hurt / lava / future projectiles actually subtract health (and thus drive the
        // damage red-flash). QC players are takedamage DAMAGE_AIM; DAMAGE_YES is sufficient for the touch path.
        e.TakeDamage = DamageMode.Yes;
        // Start where the controller node is placed (convert Godot -> Quake), unless overridden by Teleport.
        e.Origin = Coords.ToQuake(GlobalPosition);
        e.OldOrigin = e.Origin;
        Player = e;
        _lastHealth = e.Health;

        // Ensure a camera child exists. Match Xonotic's default field of view (fov 100, xonotic-client.cfg)
        // and a near plane in Quake units so the first-person weapon (~25u long, authored at the eye) renders
        // at the right apparent size without clipping into the near plane.
        _camera = GetNodeOrNull<Camera3D>("Camera3D") ?? new Camera3D { Name = "Camera3D" };
        _camera.Fov = BaseFov;
        _camera.Near = 1f;
        if (_camera.GetParent() == null)
            AddChild(_camera);

        // Configure the shared view (eye height + base fov from this controller's Exports).
        _view.EyeHeight = EyeHeight;
        _view.BaseFov = BaseFov;

        // Seed the zoom cvars so the zoom math reads authentic values even standalone (the real match loads the
        // full Xonotic cfg tree, which sets these; Register is idempotent so it never clobbers a loaded value).
        if (Api.Services is not null)
        {
            // xonotic-client.cfg:59 cl_zoomfactor 5 (the +zoom magnitude). Seeding 2.5 here made zoom
            // half-strength standalone; the in-code clamp-default below stays 2.5 (mirrors view.qc:503).
            Api.Cvars.Register("cl_zoomfactor", "5");
            Api.Cvars.Register("cl_zoomspeed", "8"); // xonotic-client.cfg:60 (clamp-default 3.5 stays at UpdateZoom, view.qc:498)
            Api.Cvars.Register("cl_zoomsensitivity", "0");
            // Spawn-zoom (default ON): xonotic-client.cfg:56-58.
            Api.Cvars.Register("cl_spawnzoom", "1");
            Api.Cvars.Register("cl_spawnzoom_speed", "1");
            Api.Cvars.Register("cl_spawnzoom_factor", "2");
            Api.Cvars.Register("fov", "100");
            Api.Cvars.Register("cl_eventchase_death", "2");
            Api.Cvars.Register("cl_eventchase_distance", "140");
            Api.Cvars.Register("cl_eventchase_speed", "1.3");
        }

        // Zoom (QC button_zoom) is now driven by the `+zoom` bind from binds-xonotic.cfg via BindTable.ZoomHeld
        // (read in _PhysicsProcess) — no dedicated InputMap action / hardcoded key needed.

        // FPS mouse-look: capture the cursor.
        Input.MouseMode = Input.MouseModeEnum.Captured;

        UpdateCamera();
    }

    /// <summary>Place the player at a Quake-space origin and yaw (used to drop the player on a spawn point).</summary>
    public void Teleport(NVec3 quakeOrigin, float yawDegrees)
    {
        if (Player is null)
            return;
        Player.Origin = quakeOrigin;
        Player.OldOrigin = quakeOrigin;
        Player.Velocity = NVec3.Zero;
        _viewAngles = new NVec3(0f, yawDegrees, 0f);

        // Local-spawn zoom (QC spawnpoints.qc:82-86): snap the zoom in and latch the spawn-zoom effect so the
        // view eases back out to full fov over the first moments after (re)spawning. Default ON (cl_spawnzoom 1).
        _view.TriggerSpawnZoom();

        UpdateCamera();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Mouse right -> turn right (yaw decreases in Quake's CCW yaw); mouse down -> look down
            // (pitch is down-positive, so it increases). The sensitivity is scaled down while zoomed so aiming
            // through the zoom is finer (QC setsensitivityscale, applied to the look input here). The base feel
            // is the live `sensitivity` cvar (the input-settings dialog binds it); m_pitch's SIGN inverts Y.
            float sens = LookSensitivity() * _view.SensitivityScale;
            _viewAngles.Y -= motion.Relative.X * sens;
            _viewAngles.X += motion.Relative.Y * sens * PitchSign();
            _viewAngles.X = Mathf.Clamp(_viewAngles.X, -89f, 89f);
            return;
        }

        // Bind-driven gameplay input (DP key bindings): a keyboard/mouse-button press/release drives the +/-
        // held-button state read in _PhysicsProcess and runs one-shot bound commands (weapon-select/reload/…)
        // through the local runner below. Frozen while the console is open (Escape + the in-game menu are owned
        // by the Shell). The console-open gate matches the polled BindTable read in _PhysicsProcess.
        if (!ConsoleState.IsOpen && @event is InputEventKey or InputEventMouseButton)
            BindInput.HandleEvent(@event, RunBoundCommand);
    }

    /// <summary>
    /// Run a one-shot console command issued by a bind on the LOCAL demo path. There is no client→server
    /// command channel here (the player <see cref="Entity"/> is local), so weapon-select binds drive the
    /// <see cref="Inventory"/> selection API directly (selection.qc weapnext/weapprev/weapon_group_N/…) — a
    /// no-op when the demo player owns no real weapons — and raise <see cref="WeaponSwitched"/> so the demo's
    /// cosmetic view-model swap follows. Unknown commands are ignored (the net path routes them to the server).
    /// </summary>
    private void RunBoundCommand(string command)
    {
        if (Player is null || string.IsNullOrEmpty(command))
            return;

        // weapon_group_N (the 1..9 keys via binds-xonotic.cfg) → demo slot N-1 + the impulse-group cycle.
        if (command.StartsWith("weapon_group_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(command.AsSpan("weapon_group_".Length), out int group))
        {
            Inventory.NextWeaponOnImpulse(Player, group);
            // The demo's DemoWeapons array is 0-based; map group 1..9 → slot 0..8 (group 0 → slot 9), matching
            // the previous hardcoded `Key.Key1..Key9 → slot keycode-Key1` behaviour.
            _demoSlot = group == 0 ? 9 : group - 1;
            WeaponSwitched?.Invoke(_demoSlot);
            return;
        }

        switch (command.ToLowerInvariant())
        {
            case "weapnext":
                Inventory.NextWeapon(Player, 0);
                WeaponSwitched?.Invoke(++_demoSlot);
                break;
            case "weapprev":
                Inventory.PreviousWeapon(Player, 0);
                WeaponSwitched?.Invoke(--_demoSlot);
                break;
            case "weapon_last":
                Inventory.LastWeapon(Player);
                WeaponSwitched?.Invoke(--_demoSlot); // step back one for the cosmetic swap
                break;
            case "weapon_best":
                if (Inventory.GetBestWeapon(Player) is { } best)
                    Inventory.SwitchWeaponWithComplain(Player, best);
                break;
            case "weapon_reload":
                Inventory.CurrentWeapon(Player)?.WrReload(Player, new WeaponSlot(0));
                break;
            // weapon_drop / messagemode / kill / quickmenu / toggleconsole have no local-demo effect; ignore.
        }
    }

    /// <summary>The live look sensitivity (DP `sensitivity` cvar) — the value the input-settings dialog writes,
    /// scaled into the deg/pixel feel the prior <see cref="MouseSensitivity"/> default gave so existing aim is
    /// unchanged at the default; falls back to <see cref="MouseSensitivity"/> when the cvar is unset.</summary>
    private float LookSensitivity()
    {
        float s = Api.Services is not null ? Api.Cvars.GetFloat("sensitivity") : 0f;
        return s > 0f ? s * 0.025f : MouseSensitivity;
    }

    /// <summary>Invert-Y sign from `m_pitch` (DP: negative pitch inverts the Y axis). +1 normal, −1 inverted.</summary>
    private static float PitchSign()
        => Api.Services is not null && Api.Cvars.GetFloat("m_pitch") < 0f ? -1f : 1f;

    public override void _PhysicsProcess(double delta)
    {
        if (Player is null)
            return;

        // Gameplay input is inert while the console is open (it owns focus); drop the held buttons on the edge
        // into that state (DP in_releaseall) so a key held at open-time doesn't stay down. The view angles hold.
        bool active = !ConsoleState.IsOpen;
        if (active != _inputActive)
        {
            _inputActive = active;
            if (!active) BindTable.ReleaseAll();
        }

        // Re-capture on click if the user released the mouse (never while the console is open — it owns the cursor).
        if (active && Input.MouseMode != Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Left))
            Input.MouseMode = Input.MouseModeEnum.Captured;

        // QC button_zoom: held while the +zoom bind's key is down (BindTable, fed from binds-xonotic.cfg —
        // MOUSE3 togglezoom / JOY7 +zoom; the user can rebind). Suppressed when dead so the view zooms out on
        // death (QC cl_unpress_zoom_on_death) and while the console is open. Fed to the shared view.
        _view.ZoomHeld = active && !IsDead && BindTable.ZoomHeld;

        // --- gather wish-move from the bind table (cl_input.c kbutton model): Quake forward = X, side = Y ---
        // BindTable.Forward = +forward − +back; Side = +moveright − +moveleft; Up = +jump − +crouch. All ±1 here;
        // scaled to sv_maxspeed below (the movement code clamps wishspeed anyway).
        float wishSpeed = MovementParameters.Defaults.MaxSpeed;
        var moveValues = new NVec3(BindTable.Forward * wishSpeed, BindTable.Side * wishSpeed, 0f);

        var input = new MovementInput
        {
            ViewAngles = _viewAngles,
            MoveValues = moveValues,
            FrameTime = (float)delta,
            ButtonJump = BindTable.JumpHeld,
            ButtonCrouch = BindTable.CrouchHeld,
            ButtonAttack1 = BindTable.AttackHeld,
            ButtonAttack2 = BindTable.Attack2Held,
        };

        // --- run the ported, deterministic movement sim (gravity + friction/accel + slide/step) ---
        Movement.Move(Player, input);

        // --- SV_TouchTriggers: the movement sim's slide-move only touches the SOLIDs it collides with, so the
        //     non-solid SOLID_TRIGGER volumes (jump-pads / teleporters / trigger_hurt / …) need a separate
        //     overlap pass — the same one the server runs each tick. Relink the player to its post-move origin
        //     first so the overlap test uses current bounds, then fire any host-installed pass. ---
        if (PostMoveTouch is not null)
        {
            Api.Entities.SetOrigin(Player, Player.Origin); // recompute AbsMin/AbsMax (SV_LinkEdict)
            PostMoveTouch(Player);
        }

        // Fire on the rising edge of attack1 (the client fire hook drives muzzle flash + projectile).
        bool attack = input.ButtonAttack1;
        if (attack && !_attackHeld)
            Attacked?.Invoke();
        _attackHeld = attack;

        // --- view subsystem (qcsrc/client/view.qc): pain signal, then the shared view (zoom lerp + camera) ---
        UpdatePain();             // detect a health drop → Damaged (HUD_Damage's dmg_take)

        // UpdateCamera (via FirstPersonView) computes the final rendered origin (eventchase pullback), lerps the
        // zoom, applies the *0.75 fov, and samples EyeContents there — QC HUD_Contents reads
        // pointcontents(view_origin), the FINAL render origin (view.qc:1176).
        UpdateCamera((float)delta);
    }

    /// <summary>
    /// Detect damage taken by the local player and raise <see cref="Damaged"/> with the amount lost (the
    /// client analogue of QC's <c>.dmg_take</c> reaching <c>HUD_Damage</c>). A health <em>increase</em> (regen,
    /// respawn, pickup) raises nothing. Single-process demo: the server damages the same <see cref="Entity"/>
    /// the controller owns, so a plain delta between ticks catches the pain.
    /// </summary>
    private void UpdatePain()
    {
        float h = Player!.Health;
        if (!float.IsNaN(_lastHealth) && h < _lastHealth && _lastHealth > 0f)
            Damaged?.Invoke(_lastHealth - h);
        _lastHealth = h;
    }

    /// <summary>
    /// The view's forward direction in Quake space (unit), derived from the current pitch/yaw. Used by the
    /// client layer to aim muzzle effects and demo projectiles along where the player is looking.
    /// </summary>
    public NVec3 AimForwardQuake()
    {
        float pitch = Mathf.DegToRad(_viewAngles.X); // down-positive
        float yaw = Mathf.DegToRad(_viewAngles.Y);
        float cp = Mathf.Cos(pitch);
        // Quake: forward = (cos(yaw)cos(pitch_up), sin(yaw)cos(pitch_up), -sin(pitch_down)).
        return new NVec3(Mathf.Cos(yaw) * cp, Mathf.Sin(yaw) * cp, -Mathf.Sin(pitch));
    }

    /// <summary>
    /// Drive the shared <see cref="Client.FirstPersonView"/> for this tick: build an Entity-backed
    /// <see cref="Client.FirstPersonView.ViewState"/> from the player entity, then place + orient the camera
    /// (eventchase pullback on death / third-person), lerp the zoom, apply the *0.75 fov, and sample the eye
    /// contents at the FINAL render origin. <paramref name="dt"/> 0 = a placement-only call (Teleport/_Ready),
    /// which still re-orients the camera without advancing the zoom integration.
    /// </summary>
    private void UpdateCamera(float dt = 0f)
    {
        if (Player is null)
            return;

        var st = new Client.FirstPersonView.ViewState
        {
            OriginQuake = Player.Origin,
            VelocityQuake = Player.Velocity,
            ViewAnglesQuake = _viewAngles,
            IsDead = IsDead, // demo: Health<=0 && MaxHealth>0 (no fresh-spawn-at-0 false death)
            // Eye drops while crouched (PlayerPhysics.UpdateCrouch sets Player.ViewOfs, QC STAT(PL_*_VIEW_OFS)).
            EyeHeightZ = Player.ViewOfs.Z,
        };
        _view.UpdateView(_camera, st, dt);
    }
}
