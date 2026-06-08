using System;
using Godot;
using XonoticGodot.Formats.Md3;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The first-person weapon view-model — the Godot successor to CSQC's <c>viewmodel</c> / <c>weaponentity</c>
/// (qcsrc/client/view.qc), including the muzzle-flash attachment the libs flag with
/// <c>TODO(port,client): EFFECT_*_MUZZLEFLASH</c>. It renders the held weapon model in front of the camera
/// and, on each shot, fires the weapon's muzzle effect (from <see cref="EffectSystem"/>) at the model's
/// muzzle <see cref="Md3Tag"/> ("tag_shot" / "tag_weapon"), with a brief flash light and a small kick/bob.
///
/// <para><b>Placement (faithful to Xonotic+DarkPlaces).</b> Xonotic's weapon models (<c>v_*.md3</c>) are
/// authored directly in <i>view space</i> in Quake units: the geometry already sits forward/right/down of the
/// eye (e.g. <c>v_nex</c> spans X≈-12..46), and DP renders the viewmodel entity at the view origin with
/// <c>cl_viewmodel_scale</c> (=1) — there is NO per-model shrink or hand-tuned offset. So we attach this node
/// to the player <see cref="Camera3D"/> and place the model at the camera origin under a fixed Quake→camera
/// basis (the model is NOT scaled). We add <c>cl_gunoffset</c> (default 0) and the right/center/left
/// <c>cl_gunalign</c> nudge, matching <c>CL_WeaponEntity_SetModel</c> + <c>viewmodel_draw</c>.</para>
///
/// <para><b>Sway (cl_followmodel / cl_leanmodel / cl_bobmodel).</b> Ported from <c>viewmodel_animate</c> in
/// view.qc: the gun lags/pulls when accelerating (follow), tilts when turning (lean), and bobs while running
/// on the ground (bob). These produce a view-space <c>gunorg</c> offset and <c>gunangles</c> rotation applied
/// each frame on top of the rest placement. State is read from the player each frame via
/// <see cref="ViewStateProvider"/>.</para>
///
/// Coordinates: the model + sway math live in Quake view space (X fwd, Y left, Z up) and are mapped once into
/// the camera's local Godot frame (<see cref="QuakeViewToCamera"/>). The muzzle effect is still emitted in
/// WORLD space (the muzzle marker's global transform converted back through Godot).
/// </summary>
public partial class ViewModel : Node3D
{
    /// <summary>The live view state the sway math reads (player velocity, view angles, onground).</summary>
    public readonly struct ViewState
    {
        /// <summary>Player velocity in Quake space (units/s).</summary>
        public NVec3 VelocityQuake { get; init; }
        /// <summary>View angles in degrees, Quake convention (X = pitch down-positive, Y = yaw, Z = roll).</summary>
        public NVec3 ViewAnglesQuake { get; init; }
        /// <summary>Whether the player is standing on the ground (gates the run-bob).</summary>
        public bool OnGround { get; init; }
    }

    /// <summary>
    /// Supplies the per-frame <see cref="ViewState"/> for sway. Injected by the host (the
    /// <see cref="PlayerController"/>); when null the weapon simply holds its rest pose with no sway.
    /// </summary>
    public Func<ViewState>? ViewStateProvider { get; set; }

    /// <summary>The effect system that renders muzzle flashes (injected by the client world).</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>EFFECT_* (or effectinfo) name for this weapon's muzzle flash, e.g. "VORTEX_MUZZLEFLASH".</summary>
    [Export] public string MuzzleEffect { get; set; } = "BLASTER_MUZZLEFLASH";

    /// <summary>Tag name on the weapon model where shots originate (Quake/Xonotic convention).</summary>
    [Export] public string MuzzleTagName { get; set; } = "tag_shot";

    /// <summary>
    /// <c>cl_viewmodel_scale</c> — uniform scale DarkPlaces applies to the viewmodel matrix. Xonotic's default
    /// is 1: the <c>v_*</c> models are authored at the right size in view space, so we do NOT shrink them.
    /// </summary>
    [Export] public float ViewModelScale { get; set; } = 1f;

    /// <summary>
    /// <c>cl_gunoffset</c> — extra view-space offset (Quake: X fwd, Y left, Z up) applied to the gun on top of
    /// the alignment nudge. Xonotic default is "0 0 0". Exposed for tuning / a future cvar binding.
    /// </summary>
    [Export] public Vector3 GunOffset { get; set; } = Vector3.Zero;

    /// <summary>
    /// <c>cl_gunalign</c> — 1/2 = center, 3 = right (Xonotic default), 4 = left. With shoot-from-eye servers
    /// this is purely the visual gun position. We apply the same small nudge <c>shotorg_adjustfromclient</c>
    /// does so the gun reads as held in the right hand by default.
    /// </summary>
    [Export] public int GunAlign { get; set; } = 3;

    /// <summary>How far the weapon kicks back per shot (view-space Quake units), and how fast it recovers.</summary>
    [Export] public float RecoilKick { get; set; } = 2.0f;
    [Export] public float RecoilRecovery { get; set; } = 8f;

    // --- gun sway cvars (Xonotic xonotic-client.cfg + DP defaults; see qcsrc/client/view.qc) ---------------
    [Export] public bool FollowModel { get; set; } = true;
    [Export] public float FollowSpeed { get; set; } = 0.3f;
    [Export] public float FollowLimit { get; set; } = 135f;
    [Export] public float FollowVelocityLowpass { get; set; } = 0.05f;
    [Export] public float FollowHighpass { get; set; } = 0.05f;
    [Export] public float FollowLowpass { get; set; } = 0.03f;

    [Export] public bool LeanModel { get; set; } = true;
    [Export] public float LeanSpeed { get; set; } = 0.3f;
    [Export] public float LeanLimit { get; set; } = 30f;
    [Export] public float LeanHighpass1 { get; set; } = 0.2f;
    [Export] public float LeanHighpass { get; set; } = 0.2f;
    [Export] public float LeanLowpass { get; set; } = 0.05f;

    [Export] public bool BobModel { get; set; } = true;
    [Export] public float BobModelSide { get; set; } = 0.2f;
    [Export] public float BobModelUp { get; set; } = 0.1f;
    [Export] public float BobModelSpeed { get; set; } = 10f;

    // The fixed model → camera-local basis. The model builders (IqmBuilder/Md3Builder/DpmBuilder/ModelLoader)
    // already convert vertices Quake->Godot WORLD via Coords.ToGodot, so a built gun points Godot +X (= Quake
    // forward), with Godot +Y = up and Godot -Z = the gun's left (Quake +Y). This basis re-aims those built-in
    // axes into the camera's local frame (camera: +X right, +Y up, -Z forward):
    //   model fwd  (Godot +X) -> camera -Z (forward)
    //   model up   (Godot +Y) -> camera +Y (up)
    //   model left (Godot -Z) -> camera -X (left)   i.e. Godot +Z -> camera +X
    // (Basis(x,y,z) takes the COLUMN images of the model's local axes.)
    private static readonly Basis ViewBasis = new(
        new Vector3(0f, 0f, -1f),   // col 0: image of model +X (forward) = camera -Z
        new Vector3(0f, 1f, 0f),    // col 1: image of model +Y (up)      = camera +Y
        new Vector3(1f, 0f, 0f));   // col 2: image of model +Z           = camera +X

    private ModelAnimator? _animator;     // optional animated weapon model
    private Node3D _modelRoot = null!;     // holds the weapon mesh (+ its tags), under the Quake->camera basis
    private Marker3D? _muzzleMarker;       // muzzle socket on the model (from tags)
    private OmniLight3D _flashLight = null!;
    private OmniLight3D _fillLight = null!; // soft constant light so the view-model is never a black silhouette
    private float _flashTime;              // remaining flash-light time
    private float _recoil;                 // current recoil displacement (Quake units, decays to 0)

    // Sway running state (the QC static locals: follow/lean low/high-pass accumulators + bob phase).
    private SwayState _sway;

    // =================================================================================================
    //  Construction / weapon swap
    // =================================================================================================

    public override void _Ready()
    {
        _modelRoot = new Node3D { Name = "WeaponModel" };
        AddChild(_modelRoot);

        _flashLight = new OmniLight3D
        {
            Name = "MuzzleLight",
            LightColor = new Color(1f, 0.9f, 0.6f),
            LightEnergy = 0f,
            OmniRange = 120f,
            ShadowEnabled = false,
        };
        AddChild(_flashLight);

        // A fill light that travels with the weapon so the first-person model stays legible regardless of how
        // the map's lighting falls near the camera (view weapons otherwise read as a black silhouette). Placed
        // up/right and slightly behind the camera, aimed at the gun which sits forward (camera -Z). Energy/range
        // are generous (Quake-unit range covering the ~25u gun) so the textured surface reads in any map light.
        _fillLight = new OmniLight3D
        {
            Name = "ViewFill",
            LightColor = new Color(1f, 0.98f, 0.95f),
            LightEnergy = 6.0f,
            OmniRange = 96f,
            OmniAttenuation = 0.5f,
            ShadowEnabled = false,
            Position = new Vector3(8f, 14f, 6f), // camera-local: right, up, behind (+Z) — Quake units
        };
        AddChild(_fillLight);

        ApplyRestPlacement();
    }

    /// <summary>
    /// Place the model root at the resting view-space transform: the Quake→camera basis (no shrink — the model
    /// is authored in view space), with <c>cl_gunoffset</c> + the <c>cl_gunalign</c> nudge as the origin. Sway
    /// and recoil are layered on top of this each frame in <see cref="_Process"/>.
    /// </summary>
    private void ApplyRestPlacement()
    {
        Vector3 alignOfs = GunAlignOffset();                       // Quake view space
        Vector3 restQuake = alignOfs + GunOffset;                  // + cl_gunoffset
        _modelRoot.Transform = new Transform3D(
            ViewBasis.Scaled(new Vector3(ViewModelScale, ViewModelScale, ViewModelScale)),
            QuakeViewToCamera(restQuake));
        // The ViewModel node itself carries no extra transform at rest (sway resets it each frame).
        Transform = Transform3D.Identity;
    }

    /// <summary>
    /// Swap in a weapon model from a parsed MD3, with its muzzle effect/tag. The previous model is freed.
    /// If the MD3 has multiple frames a <see cref="ModelAnimator"/> is created and its "idle" clip played;
    /// otherwise a static <see cref="ModelLoader.BuildModel"/> snapshot is used. The muzzle marker is taken
    /// from the model's tags (falling back to <see cref="MuzzleTagName"/> alternatives).
    /// </summary>
    public void SetWeapon(Md3Data md3, string muzzleEffect, string? muzzleTag = null)
    {
        MuzzleEffect = muzzleEffect;
        if (!string.IsNullOrEmpty(muzzleTag))
            MuzzleTagName = muzzleTag!;

        // Clear old model.
        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;
        _muzzleMarker = null;

        ApplyRestPlacement();

        if (md3.FrameCount > 1)
        {
            _animator = ModelAnimator.Create(md3, "WeaponAnim");
            _modelRoot.AddChild(_animator);
            _muzzleMarker = ResolveMuzzleMarker(_animator.GetTag);
        }
        else
        {
            MeshInstance3D mesh = ModelLoader.BuildModel(md3, 0);
            _modelRoot.AddChild(mesh);
            Node3D tags = ModelLoader.BuildTags(md3, 0);
            _modelRoot.AddChild(tags);
            _muzzleMarker = ResolveMuzzleMarker(name =>
            {
                foreach (Node c in tags.GetChildren())
                    if (c is Marker3D m && m.Name == name) return m;
                return null;
            });
        }
    }

    /// <summary>Set a weapon that has no model yet (uses a placeholder bar) but still flashes correctly.</summary>
    public void SetWeaponPlaceholder(string muzzleEffect, string? muzzleTag = null)
    {
        MuzzleEffect = muzzleEffect;
        if (!string.IsNullOrEmpty(muzzleTag)) MuzzleTagName = muzzleTag!;

        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;

        // The placeholder is authored in Quake view space too (a barrel a few units long forward of the eye),
        // so it shares the same Quake->camera basis as a real model.
        ApplyRestPlacement();

        var body = new MeshInstance3D
        {
            Name = "Placeholder",
            // Quake units: ~24 long down the barrel (+X forward), thin.
            Mesh = new BoxMesh { Size = new Vector3(24f, 4f, 4f) },
            Position = new Vector3(12f, 0f, 0f), // pushed forward so it's in front of the eye
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.15f, 0.17f) },
        };
        _modelRoot.AddChild(body);

        // Muzzle at the front tip of the barrel (+X forward in Quake view space).
        var muzzle = new Marker3D { Name = "tag_shot", Position = new Vector3(24f, 0f, 0f) };
        _modelRoot.AddChild(muzzle);
        _muzzleMarker = muzzle;
    }

    /// <summary>
    /// Equip a weapon from a prebuilt model node — any format the asset pipeline produces (MD3/IQM/DPM,
    /// skinned + animated), e.g. from <see cref="AssetLoader.LoadModel"/>. The node is placed in view space
    /// (the model is authored there; no shrink); the muzzle socket is resolved by searching the node's
    /// descendants for an MD3 tag marker (<c>tag_shot</c>/<c>tag_weapon</c>), falling back to the model front.
    /// A <c>null</c> model installs the placeholder bar.
    /// </summary>
    /// <param name="attach">
    /// The <c>weapon</c>-tag attachment transform (in the model's built Godot space) that offsets the gun to
    /// the held position — Xonotic attaches the <c>v_*</c> model to the <c>h_*</c> hand model's <c>weapon</c>
    /// bone (≈7u right, ≈15-18u down). Pass <see cref="Transform3D.Identity"/> to place the model at the eye.
    /// </param>
    public void SetWeaponModel(Node3D? model, string muzzleEffect, string? muzzleTag = null, Transform3D? attach = null)
    {
        if (model is null)
        {
            SetWeaponPlaceholder(muzzleEffect, muzzleTag);
            return;
        }

        MuzzleEffect = muzzleEffect;
        if (!string.IsNullOrEmpty(muzzleTag))
            MuzzleTagName = muzzleTag!;

        foreach (Node c in _modelRoot.GetChildren())
            c.QueueFree();
        _animator = null;

        ApplyRestPlacement();
        // The attachment transform lives in the model's built (Godot) space, so it is applied AS the model's
        // local transform — _modelRoot's ViewBasis then re-aims the whole assembly into the camera frame.
        model.Transform = attach ?? Transform3D.Identity;
        _modelRoot.AddChild(model);

        _muzzleMarker = ResolveMuzzleMarker(name => FindMarkerByName(model, name));
    }

    private Marker3D? ResolveMuzzleMarker(Func<string, Marker3D?> lookup)
    {
        // Try the configured name, then the common Quake/Xonotic muzzle tag aliases.
        foreach (string candidate in new[] { MuzzleTagName, "tag_shot", "tag_weapon", "shot", "muzzle", "tag_barrel" })
        {
            Marker3D? m = lookup(candidate);
            if (m is not null)
                return m;
        }
        return null;
    }

    /// <summary>Depth-first search for a <see cref="Marker3D"/> descendant whose name matches (case-insensitive).</summary>
    private static Marker3D? FindMarkerByName(Node root, string name)
    {
        foreach (Node c in root.GetChildren())
        {
            if (c is Marker3D m && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                return m;
            if (FindMarkerByName(c, name) is { } found)
                return found;
        }
        return null;
    }

    // =================================================================================================
    //  Firing
    // =================================================================================================

    /// <summary>
    /// Play one shot: spawn the muzzle flash at the muzzle socket (world space), pop the flash light, and
    /// apply recoil. Call this from the client when the local player's weapon fires (the CSQC
    /// <c>W_MuzzleFlash</c> / weapon attack hook). <paramref name="effectOverride"/> lets a specific shot use
    /// a different effect (e.g. secondary fire).
    /// </summary>
    public void Fire(string? effectOverride = null)
    {
        string effect = string.IsNullOrEmpty(effectOverride) ? MuzzleEffect : effectOverride!;

        // World-space muzzle origin + forward, then convert Godot -> Quake for the EffectSystem.
        Transform3D muzzleXf = MuzzleGlobalTransform();
        Vector3 originGodot = muzzleXf.Origin;
        Vector3 forwardGodot = -muzzleXf.Basis.Z; // -Z is "forward" in Godot
        var originQuake = Coords.ToQuake(originGodot);
        var dirQuake = Coords.ToQuake(forwardGodot) * 120f; // give the flash a little forward velocity

        Effects?.MuzzleFlash(effect, originQuake, dirQuake);

        // Flash light + recoil.
        _flashTime = 0.06f;
        _flashLight.LightEnergy = 3.0f;
        _recoil = Mathf.Min(_recoil + RecoilKick, RecoilKick * 2f);

        // If the weapon has a "fire"/"attack" clip, play it once.
        _animator?.Play(FindFireClip());
    }

    private string FindFireClip()
    {
        foreach (string n in new[] { "fire", "shoot", "attack", "fire1" })
            if (_animator!.PlayIfChanged(n)) return n;
        return _animator!.CurrentClip;
    }

    /// <summary>The muzzle socket's global transform (marker if present, else the model front).</summary>
    private Transform3D MuzzleGlobalTransform()
    {
        if (_muzzleMarker is not null && GodotObject.IsInstanceValid(_muzzleMarker))
            return _muzzleMarker.GlobalTransform;
        // Fall back to a point in front of the weapon root (camera -Z forward).
        return new Transform3D(GlobalBasis, ToGlobal(new Vector3(0f, 0f, -16f)));
    }

    // =================================================================================================
    //  Per-frame: decay the flash, recover recoil, drive follow/lean/bob sway
    // =================================================================================================

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_flashTime > 0f)
        {
            _flashTime -= dt;
            // Ease the light out over the flash window.
            _flashLight.LightEnergy = Mathf.Max(0f, _flashLight.LightEnergy - 3.0f * (dt / 0.06f));
            if (_flashTime <= 0f)
                _flashLight.LightEnergy = 0f;
            // Keep the flash light parked at the muzzle.
            if (_muzzleMarker is not null && GodotObject.IsInstanceValid(_muzzleMarker))
                _flashLight.GlobalPosition = _muzzleMarker.GlobalPosition;
        }

        // Recoil recovers along view forward (+X Quake). It and the sway both feed the gunorg below.
        _recoil = Mathf.MoveToward(_recoil, 0f, RecoilRecovery * RecoilKick * dt);

        UpdateSway(dt);
    }

    /// <summary>
    /// Drive the per-frame weapon sway (Xonotic <c>viewmodel_animate</c>): follow (origin lag on accel), lean
    /// (angle tilt on turn), bob (run cycle), plus the firing recoil. Produces a Quake view-space
    /// <c>gunorg</c> / <c>gunangles</c> applied as this node's local transform on top of the rest placement.
    /// </summary>
    private void UpdateSway(float dt)
    {
        // gunorg (view space: X fwd, Y right(+)/left, Z up) and gunangles (deg) accumulate the effects.
        Vector3 gunorg = new(-_recoil, 0f, 0f); // recoil pulls the gun back along -fwd
        Vector3 gunangles = Vector3.Zero;

        ViewState vs = ViewStateProvider is not null ? ViewStateProvider() : default;
        // Guard against a zero/oversized dt (paused frame, first frame); the QC avg_factor needs frametime>0.
        float frametime = Mathf.Clamp(dt, 0f, 0.1f);

        if (frametime > 0f)
        {
            if (FollowModel)
                gunorg += FollowModelOffset(vs, frametime);
            if (LeanModel)
                gunangles += LeanModelOffset(vs, frametime);
            if (BobModel)
                gunorg += BobModelOffset(vs, frametime);
        }

        // Map the view-space gunorg/gunangles into the camera-local Godot frame and apply.
        // Translation: the gunorg components are (fwd, right, up) in Quake view space. Note bob/follow build
        // gunorg.y as a RIGHT-positive side value (matching the QC, which works in a right-handed view), so we
        // negate y to Quake's left-positive before the basis map.
        Vector3 ofsQuake = new(gunorg.X, -gunorg.Y, gunorg.Z);
        Vector3 localOfs = QuakeViewToCamera(ofsQuake);

        // Rotation: gunangles is (pitch, yaw, roll) in degrees, view space. Build the small rotation in the
        // camera frame: pitch about the view right axis, yaw about view up, roll about view forward.
        Basis rot = Basis.Identity;
        if (gunangles != Vector3.Zero)
            rot = LeanRotation(gunangles);

        Transform = new Transform3D(rot, localOfs);
    }

    /// <summary>
    /// Port of <c>calc_followmodel_ofs</c> (view.qc): turns the player's view-relative velocity into a lagging
    /// origin offset via a velocity-lowpass then a highpass→lowpass (velocity→acceleration) chain, so the gun
    /// pulls back when you accelerate and settles when you cruise. Returns a view-space (fwd,right,up) offset.
    /// </summary>
    private Vector3 FollowModelOffset(ViewState vs, float frametime)
    {
        // view-relative velocity: x = along forward, y = along right*-1, z = along up (QC convention).
        AngleVectorsQuake(vs.ViewAnglesQuake, out NVec3 f, out NVec3 r, out NVec3 u);
        Vector3 vel = new(
            Dot(vs.VelocityQuake, f),
            -Dot(vs.VelocityQuake, r),
            Dot(vs.VelocityQuake, u));

        // bound around the running average so a spike can't yank the gun across the screen.
        vel.X = Bound(_sway.FollowVelAvg.X - FollowLimit, vel.X, _sway.FollowVelAvg.X + FollowLimit);
        vel.Y = Bound(_sway.FollowVelAvg.Y - FollowLimit, vel.Y, _sway.FollowVelAvg.Y + FollowLimit);
        vel.Z = Bound(_sway.FollowVelAvg.Z - FollowLimit, vel.Z, _sway.FollowVelAvg.Z + FollowLimit);

        float frac = AvgFactor(FollowVelocityLowpass, frametime);
        Vector3 gunorg = Lowpass3(vel, frac, ref _sway.FollowVelAvg);

        gunorg *= -FollowSpeed * 0.042f;

        // highpass THEN lowpass (the QC trick: lowpass last so the stored vector IS the result).
        frac = AvgFactor(FollowHighpass, frametime);
        gunorg = Highpass3(gunorg, frac, ref _sway.FollowAdjHighpass);
        frac = AvgFactor(FollowLowpass, frametime);
        gunorg = Lowpass3(gunorg, frac, ref _sway.FollowAdjLowpass);

        return gunorg;
    }

    /// <summary>
    /// Port of <c>leanmodel_ofs</c> (view.qc): a highpass on the view angles (the difference between the actual
    /// and a smoothed angle) tilts the gun when you turn, scaled by <c>cl_leanmodel_speed</c> and run through a
    /// further high/low-pass for smoothness. Returns view-space (pitch, yaw, roll) degrees.
    /// </summary>
    private Vector3 LeanModelOffset(ViewState vs, float frametime)
    {
        Vector3 view = new(vs.ViewAnglesQuake.X, vs.ViewAnglesQuake.Y, vs.ViewAnglesQuake.Z);

        // store the DIFFERENCE to the actual view angles, unwrapped across the 360° seam (pitch+yaw).
        _sway.LeanHighpass += _sway.LeanPrev;
        _sway.LeanHighpass.X += 360f * Mathf.Floor((view.X - _sway.LeanHighpass.X) / 360f + 0.5f);
        _sway.LeanHighpass.Y += 360f * Mathf.Floor((view.Y - _sway.LeanHighpass.Y) / 360f + 0.5f);
        float frac = AvgFactor(LeanHighpass1, frametime);
        Vector3 gunangles = Vector3.Zero;
        gunangles.X = Highpass1Limited(view.X, frac, LeanLimit, ref _sway.LeanHighpass.X);
        gunangles.Y = Highpass1Limited(view.Y, frac, LeanLimit, ref _sway.LeanHighpass.Y);
        _sway.LeanPrev = view;
        _sway.LeanHighpass -= _sway.LeanPrev;

        gunangles.X *= -LeanSpeed; // pitch
        gunangles.Y *= -LeanSpeed; // yaw

        // PITCH = x, YAW = y: highpass then lowpass.
        frac = AvgFactor(LeanHighpass, frametime);
        gunangles.X = Highpass1(gunangles.X, frac, ref _sway.LeanAdjHighpass.X);
        gunangles.Y = Highpass1(gunangles.Y, frac, ref _sway.LeanAdjHighpass.Y);
        frac = AvgFactor(LeanLowpass, frametime);
        gunangles.X = Lowpass1(gunangles.X, frac, ref _sway.LeanAdjLowpass.X);
        gunangles.Y = Lowpass1(gunangles.Y, frac, ref _sway.LeanAdjLowpass.Y);

        gunangles.X = -gunangles.X; // pitch was inverted; matches the QC final negate.
        return gunangles;
    }

    /// <summary>
    /// Port of <c>bobmodel_ofs</c> (view.qc): a sinusoidal side/up sway scaled by horizontal speed, ramped in
    /// while running on the ground and out while airborne. Returns a view-space (fwd,right,up) offset (only the
    /// right + up components move).
    /// </summary>
    private Vector3 BobModelOffset(ViewState vs, float frametime)
    {
        bool onground = vs.OnGround;
        if (onground)
        {
            if (!_sway.BobOldOnGround)
                _sway.BobHitGroundTime = _sway.BobTime;
        }
        _sway.BobOldOnGround = onground;
        // advance the bob clock (QC uses global 'time'; we keep our own monotonic accumulator).
        _sway.BobTime += frametime;

        if (onground)
        {
            if (_sway.BobTime - _sway.BobHitGroundTime > 0.05f)
                _sway.BobScale = Mathf.Min(1f, _sway.BobScale + frametime * 5f);
        }
        else
            _sway.BobScale = Mathf.Max(0f, _sway.BobScale - frametime * 5f);

        Vector3 gunorg = Vector3.Zero;
        if (_sway.BobScale > 0f)
        {
            float xyspeed = Bound(0f, Length2(vs.VelocityQuake), 400f);
            const float avgTime = 0.1f;
            float frac = 1f - Mathf.Exp(-frametime / Mathf.Max(0.001f, avgTime));
            _sway.BobAvgXySpeed = frac * xyspeed + (1f - frac) * _sway.BobAvgXySpeed;
            if (_sway.BobAvgXySpeed < 1f)
                _sway.BobTimeOfs = _sway.BobTime;
            else
            {
                if (_sway.BobAvgXySpeed < 400f) // reduce bob frequency when crouch-walking / slow
                    _sway.BobTimeOfs += frametime * MapBoundRanges(_sway.BobAvgXySpeed, 150f, 400f, 0.08f, 0f);

                float bspeed = _sway.BobAvgXySpeed * 0.01f * _sway.BobScale;
                float s = (_sway.BobTime - _sway.BobTimeOfs) * BobModelSpeed;
                gunorg.Y = bspeed * BobModelSide * Mathf.Sin(s);       // right
                gunorg.Z = bspeed * BobModelUp * Mathf.Cos(s * 2f);    // up
            }
        }
        else
            _sway.BobTimeOfs = _sway.BobTime;

        return gunorg;
    }

    /// <summary>Build a small camera-local rotation from view-space (pitch,yaw,roll) degrees: pitch about the
    /// view right axis (camera +X), yaw about the view up (camera +Y), roll about view forward (camera -Z).</summary>
    private static Basis LeanRotation(Vector3 gunanglesDeg)
    {
        float pitch = Mathf.DegToRad(gunanglesDeg.X);
        float yaw = Mathf.DegToRad(gunanglesDeg.Y);
        float roll = Mathf.DegToRad(gunanglesDeg.Z);
        // Quake yaw turns left (+); in the camera frame that's a +Y rotation. Quake pitch is down-positive;
        // tilting the gun down is a negative rotation about camera +X. Roll about forward (camera -Z).
        Basis b = new Basis(Vector3.Up, yaw)
                  * new Basis(Vector3.Right, -pitch)
                  * new Basis(Vector3.Back, -roll);
        return b;
    }

    // =================================================================================================
    //  View-space mapping + alignment
    // =================================================================================================

    /// <summary>Map a Quake view-space point (X fwd, Y left, Z up) into the camera's local Godot frame.</summary>
    private static Vector3 QuakeViewToCamera(Vector3 q) => new(-q.Y, q.Z, -q.X);

    /// <summary>
    /// The <c>cl_gunalign</c> view-space nudge (Quake: X fwd, Y left, Z up). Right alignment (3) is the model's
    /// authored position (no change); center (1/2) pulls it to the middle and drops it 2u; left (4) mirrors the
    /// side. This mirrors <c>shotorg_adjustfromclient</c>, applied to a representative right-hand offset so the
    /// gun reads as held even though the v_ model is authored roughly centered.
    /// </summary>
    private Vector3 GunAlignOffset()
    {
        // A small representative "held in the right hand" side offset (Quake +Y = left, so right is -Y).
        const float side = 0f; // the v_ models already place the gun; alignment only matters for center/left.
        switch (GunAlign)
        {
            default:
            case 3: // right — authored position
                return new Vector3(0f, side, 0f);
            case 4: // left — mirror the side
                return new Vector3(0f, -side, 0f);
            case 1:
            case 2: // center — pull to the middle and drop 2 units
                return new Vector3(0f, 0f, -2f);
        }
    }

    // =================================================================================================
    //  Sway state + math helpers (ports of the view.qc macros)
    // =================================================================================================

    /// <summary>The running accumulators the QC keeps as function-static locals (one set per local player).</summary>
    private struct SwayState
    {
        // follow
        public Vector3 FollowVelAvg;
        public Vector3 FollowAdjHighpass;
        public Vector3 FollowAdjLowpass;
        // lean
        public Vector3 LeanPrev;
        public Vector3 LeanHighpass;
        public Vector3 LeanAdjHighpass;
        public Vector3 LeanAdjLowpass;
        // bob
        public bool BobOldOnGround;
        public float BobHitGroundTime;
        public float BobScale;
        public float BobTimeOfs;
        public float BobAvgXySpeed;
        public float BobTime;
    }

    /// <summary><c>avg_factor</c>: 1 - exp(-frametime / max(0.001, avg_time)).</summary>
    private static float AvgFactor(float avgTime, float frametime)
        => 1f - Mathf.Exp(-frametime / Mathf.Max(0.001f, avgTime));

    // lowpass(value, frac, ref store) { store = store*(1-frac) + value*frac; return store; }
    private static float Lowpass1(float value, float frac, ref float store)
    {
        store = store * (1f - frac) + value * frac;
        return store;
    }

    private static Vector3 Lowpass3(Vector3 v, float frac, ref Vector3 store)
        => new(Lowpass1(v.X, frac, ref store.X), Lowpass1(v.Y, frac, ref store.Y), Lowpass1(v.Z, frac, ref store.Z));

    // highpass(value, frac, ref store) { float f = lowpass(value,frac,store); return value - f; }
    private static float Highpass1(float value, float frac, ref float store)
    {
        float f = Lowpass1(value, frac, ref store);
        return value - f;
    }

    private static Vector3 Highpass3(Vector3 v, float frac, ref Vector3 store)
        => new(Highpass1(v.X, frac, ref store.X), Highpass1(v.Y, frac, ref store.Y), Highpass1(v.Z, frac, ref store.Z));

    // highpass_limited: lowpass into store, clamp store to value±limit, return value - store.
    private static float Highpass1Limited(float value, float frac, float limit, ref float store)
    {
        Lowpass1(value, frac, ref store);
        store = Bound(value - limit, store, value + limit);
        return value - store;
    }

    private static float Bound(float lo, float v, float hi) => Mathf.Max(lo, Mathf.Min(v, hi));

    /// <summary>Length of the horizontal (X,Y) velocity components in Quake space.</summary>
    private static float Length2(NVec3 v) => Mathf.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float Dot(NVec3 a, NVec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>QC <c>map_bound_ranges</c>: remap <paramref name="v"/> from [a0,a1] into [b0,b1], clamped.</summary>
    private static float MapBoundRanges(float v, float a0, float a1, float b0, float b1)
    {
        if (a1 == a0) return b0;
        float t = Mathf.Clamp((v - a0) / (a1 - a0), 0f, 1f);
        return b0 + (b1 - b0) * t;
    }

    /// <summary>
    /// Quake AngleVectors for the follow-model velocity projection (forward/right/up unit vectors from
    /// pitch/yaw/roll degrees). Standard Quake handedness (yaw CCW, pitch down-positive) — matching the sim's
    /// QMath so the projected velocity agrees with the camera the player actually sees.
    /// </summary>
    private static void AngleVectorsQuake(NVec3 anglesDeg, out NVec3 forward, out NVec3 right, out NVec3 up)
    {
        float pitch = Mathf.DegToRad(anglesDeg.X);
        float yaw = Mathf.DegToRad(anglesDeg.Y);
        float roll = Mathf.DegToRad(anglesDeg.Z);
        float sp = Mathf.Sin(pitch), cp = Mathf.Cos(pitch);
        float sy = Mathf.Sin(yaw), cy = Mathf.Cos(yaw);
        float sr = Mathf.Sin(roll), cr = Mathf.Cos(roll);

        forward = new NVec3(cp * cy, cp * sy, -sp);
        right = new NVec3(
            -sr * sp * cy + cr * sy,
            -sr * sp * sy - cr * cy,
            -sr * cp);
        up = new NVec3(
            cr * sp * cy + sr * sy,
            cr * sp * sy - sr * cy,
            cr * cp);
    }
}
