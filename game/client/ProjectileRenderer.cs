using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using NVec3 = System.Numerics.Vector3;
using PType = XonoticGodot.Game.Client.ProjectileCatalog.ProjectileType;
using BodyFamily = XonoticGodot.Game.Client.ProjectileCatalog.BodyFamily;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Renders networked projectile entities — the Godot successor to CSQC's <c>CSQCProjectile</c> /
/// <c>Projectile_Draw</c> path (qcsrc/client/weapons/projectile.qc). The server spawns rockets, grenades,
/// plasma, blaster/electro/crylink bolts etc. as plain <see cref="Entity"/> objects with a model, origin
/// and velocity; this turns each into a per-entity visual (a <see cref="MeshInstance3D"/> body or a glowing
/// <see cref="Sprite3D"/> for bolts) that follows the entity, oriented to its velocity.
///
/// The per-projectile <b>trail, model scale, spin and looping fly sound</b> come from
/// <see cref="ProjectileCatalog"/> — the faithful port of the <c>ENT_CLIENT_PROJECTILE</c> HANDLE table, so a
/// rocket smokes and rolls (EFFECT_TR_ROCKET + z-spin 720 + <c>rocket_fly</c> loop), electro draws a blue
/// plasma trail, crylink purple shards, the fireball is particle-only fire, the hagar rocket is scaled to
/// 0.75, etc., rather than one generic exhaust for everything.
///
/// It owns nothing about gameplay: <see cref="OnSpawn"/>/<see cref="OnUpdate"/>/<see cref="OnRemove"/> are
/// driven by <see cref="ClientWorld"/> from the entity stream, and <see cref="Process"/> interpolates each
/// live visual toward its entity's current origin every frame. Origins/velocities are Quake-space and
/// converted at the boundary with <see cref="Coords"/>.
/// </summary>
public partial class ProjectileRenderer : Node3D
{
    /// <summary>The Godot visual bound to one projectile entity.</summary>
    public sealed class Visual
    {
        public required Entity Entity;
        public required Node3D Root;          // follows the entity each frame
        public Node3D? Body;                  // the mesh/sprite that spins/tumbles (child of Root)
        public GpuParticles3D? Trail;         // continuous exhaust/glow (null for trailless kinds)
        public OmniLight3D? Light;            // dynamic point light (rockets/plasma/fireball)
        public PType Type;
        public Vector3 SpinDegPerSec;         // local tumble/roll rate (QC avelocity / Projectile_Draw rot)
        public Vector3 LastPos;               // for velocity-from-motion when Entity.Velocity is unset
    }

    private readonly Dictionary<int, Visual> _visuals = new();

    /// <summary>Reused per-frame iteration buffer so <see cref="Process"/> never allocates a snapshot list.</summary>
    private readonly List<Visual> _iterBuffer = new();

    /// <summary>Soft round glow sprite texture — identical for every energy bolt, so built once and shared.</summary>
    private static ImageTexture? _glowTexture;

    /// <summary>Dependency: the effect system, used when a projectile is removed/explodes (optional).</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>When true, projectiles cast a small dynamic light. Off saves a lot of light updates.</summary>
    [Export] public bool DynamicLights { get; set; } = true;

    /// <summary>When true, attach a looping fly sound to projectiles that have one (rocket/electro/fireball/…).</summary>
    [Export] public bool LoopSounds { get; set; } = true;

    /// <summary>
    /// Resolves a QC sound sample path (e.g. "weapons/rocket_fly") to a Godot audio resource path. Host-set
    /// (VFS-backed); defaults to the <c>res://sound/&lt;sample&gt;.ogg</c> convention. A null/blank result
    /// silences the loop (graceful miss while the audio import is pending).
    /// </summary>
    public Func<string, string?>? SoundResolver { get; set; }

    /// <summary>Loads a sample straight to an <see cref="AudioStream"/> from the mounted VFS (host-set to
    /// <c>AssetLoader.LoadSound</c>). Tried before the <see cref="SoundResolver"/> <c>res://</c> fallback.</summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    // =================================================================================================
    //  Lifecycle hooks (driven by ClientWorld)
    // =================================================================================================

    /// <summary>Begin rendering a projectile entity. Idempotent: re-spawning the same index updates in place.</summary>
    public Visual? OnSpawn(Entity entity)
    {
        if (entity is null)
            return null;
        if (_visuals.TryGetValue(entity.Index, out Visual? existing))
        {
            existing.Entity = entity;
            return existing;
        }

        PType type = ProjectileCatalog.Classify(entity);
        ProjectileCatalog.Desc desc = ProjectileCatalog.DescOf(type);
        var root = new Node3D { Name = $"proj#{entity.Index}_{type}" };

        Node3D body = BuildBody(desc);
        if (desc.ModelScale is > 0f and not 1f)
            body.Scale = Vector3.One * desc.ModelScale;
        root.AddChild(body);

        GpuParticles3D? trail = BuildTrail(desc);
        if (trail is not null) root.AddChild(trail);

        OmniLight3D? light = (DynamicLights && desc.HasLight) ? BuildLight(desc) : null;
        if (light is not null) root.AddChild(light);

        Vector3 startPos = Coords.ToGodot(entity.Origin);
        root.Position = startPos;

        AddChild(root);
        // Orient after entering the tree (LookAt works on the global transform).
        OrientToVelocity(root, entity);

        // Looping fly sound (QC loopsound): rocket/electro/fireball/seeker/vehicle rockets.
        if (LoopSounds && !string.IsNullOrEmpty(desc.LoopSound))
            AttachLoopSound(root, desc.LoopSound!);

        var visual = new Visual
        {
            Entity = entity,
            Root = root,
            Body = body,
            Trail = trail,
            Light = light,
            Type = type,
            SpinDegPerSec = desc.SpinDegPerSec,
            LastPos = startPos,
        };
        _visuals[entity.Index] = visual;

        // Bind as the entity's presence link so the sim can find its node (mirrors EntityNode.Bind).
        entity.Presence ??= new ProjectilePresence(visual.Root);
        return visual;
    }

    /// <summary>Pull one projectile's current origin/velocity onto its visual (called per network update).</summary>
    public void OnUpdate(Entity entity)
    {
        if (entity is not null && _visuals.TryGetValue(entity.Index, out Visual? v))
            v.Entity = entity; // _Process does the actual transform follow (smooth interpolation)
    }

    /// <summary>Stop rendering a projectile (it hit something / expired). Optionally play an impact effect.</summary>
    public void OnRemove(Entity entity, string? impactEffect = null)
    {
        if (entity is null) return;
        OnRemove(entity.Index, entity.Origin, impactEffect);
    }

    /// <summary>Stop rendering by index; spawns <paramref name="impactEffect"/> at <paramref name="origin"/> if given.</summary>
    public void OnRemove(int index, NVec3 origin, string? impactEffect = null)
    {
        if (!_visuals.Remove(index, out Visual? v))
            return;

        // Impact effect (the CSQC wr_impacteffect): the explicit one if the caller passed it (demo path), else
        // the projectile type's default boom. No PVS culling runs on the entity stream, so the live net path only
        // removes a projectile when the server FREED it (it detonated / expired) — so drawing the explosion here
        // is correct, not a false positive from going out of view.
        string? fx = !string.IsNullOrEmpty(impactEffect) ? impactEffect : ImpactEffectFor(v.Type);
        if (!string.IsNullOrEmpty(fx))
            Effects?.Spawn(fx!, origin);

        // Let an active trail finish emitting its tail before the node disappears (detach + linger).
        if (v.Trail is not null && GodotObject.IsInstanceValid(v.Trail))
        {
            v.Trail.Emitting = false;
            v.Trail.Reparent(this, keepGlobalTransform: true);
            float linger = (float)v.Trail.Lifetime + 0.2f;
            SceneTreeTimer t = GetTree().CreateTimer(linger);
            GpuParticles3D trailRef = v.Trail;
            t.Timeout += () => { if (GodotObject.IsInstanceValid(trailRef)) trailRef.QueueFree(); };
        }

        if (GodotObject.IsInstanceValid(v.Root))
            v.Root.QueueFree();
    }

    /// <summary>True if a projectile entity is currently rendered.</summary>
    public bool IsTracking(int index) => _visuals.ContainsKey(index);

    /// <summary>Number of live projectile visuals (diagnostics).</summary>
    public int LiveCount => _visuals.Count;

    // =================================================================================================
    //  Per-frame follow / interpolation
    // =================================================================================================

    public override void _Process(double delta)
    {
        Process((float)delta);
    }

    /// <summary>Advance every visual toward its entity's current Quake origin (call once per frame).</summary>
    public void Process(float delta)
    {
        if (_visuals.Count == 0)
            return;

        // Iterate over a snapshot since a removal could mutate the dictionary mid-loop in edge cases.
        // Reuse a persistent buffer rather than allocating a fresh List every frame.
        _iterBuffer.Clear();
        foreach (Visual v in _visuals.Values)
            _iterBuffer.Add(v);
        foreach (Visual v in _iterBuffer)
        {
            if (v.Entity.IsFreed)
            {
                OnRemove(v.Entity, null);
                continue;
            }
            if (!GodotObject.IsInstanceValid(v.Root))
                continue;

            Vector3 target = Coords.ToGodot(v.Entity.Origin);
            // Smooth a touch to hide network jitter; snap if the jump is large (teleport/respawn).
            float dist2 = v.Root.Position.DistanceSquaredTo(target);
            if (dist2 > 256f * 256f)
                v.Root.Position = target;
            else
                v.Root.Position = v.Root.Position.Lerp(target, Mathf.Clamp(delta * 30f, 0f, 1f));

            OrientToVelocity(v.Root, v.Entity, v.Root.Position - v.LastPos);
            v.LastPos = v.Root.Position;

            // Spin/tumble the body locally on top of the velocity-aligned root (QC Projectile_Draw rot /
            // avelocity): rocket rolls (z), bouncing grenade tumbles sideways (y), hookbomb pitches (x), …
            if (v.Body is not null && v.SpinDegPerSec != Vector3.Zero && GodotObject.IsInstanceValid(v.Body))
                ApplySpin(v.Body, v.SpinDegPerSec, delta);
        }
    }

    private static void ApplySpin(Node3D body, Vector3 spinDegPerSec, float delta)
    {
        if (spinDegPerSec.X != 0f) body.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(spinDegPerSec.X * delta));
        if (spinDegPerSec.Y != 0f) body.RotateObjectLocal(Vector3.Up, Mathf.DegToRad(spinDegPerSec.Y * delta));
        if (spinDegPerSec.Z != 0f) body.RotateObjectLocal(Vector3.Back, Mathf.DegToRad(spinDegPerSec.Z * delta));
    }

    // =================================================================================================
    //  Node construction (driven by the catalog descriptor)
    // =================================================================================================

    private static Node3D BuildBody(ProjectileCatalog.Desc desc)
    {
        Color color = desc.GlowColor;

        switch (desc.Body)
        {
            case BodyFamily.RocketMesh:
            case BodyFamily.GrenadeMesh:
            {
                // A small solid body. Rocket = elongated capsule along forward (Godot -Z); grenade = sphere.
                bool rocket = desc.Body == BodyFamily.RocketMesh;
                Mesh mesh = rocket
                    ? new CapsuleMesh { Radius = 2.0f, Height = 12f }
                    : new SphereMesh { Radius = 3f, Height = 6f };
                var mat = new StandardMaterial3D { AlbedoColor = color, Metallic = 0.4f, Roughness = 0.6f };
                var mi = new MeshInstance3D { Name = "Body", Mesh = mesh, MaterialOverride = mat };
                // Capsule's long axis is +Y in Godot; lay it along forward (-Z) so it points where it flies.
                if (rocket)
                    mi.RotationDegrees = new Vector3(90f, 0f, 0f);
                return mi;
            }
            default: // GlowSprite / FireSprite — a bright additive billboard (no real model needed).
            {
                var sprite = new Sprite3D
                {
                    Name = "Glow",
                    Texture = _glowTexture ??= MakeGlowTexture(),
                    Modulate = color,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    Shaded = false,
                    DoubleSided = true,
                    PixelSize = desc.Body == BodyFamily.FireSprite ? 0.4f : 0.25f,
                    NoDepthTest = false,
                    AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                };
                return sprite;
            }
        }
    }

    private GpuParticles3D? BuildTrail(ProjectileCatalog.Desc desc)
    {
        if (desc.Trail is not { } c)
            return null;

        var p = new GpuParticles3D
        {
            Name = "Trail",
            Amount = Math.Max(1, c.Amount),
            Lifetime = c.Life,
            OneShot = false,
            Emitting = true,
            LocalCoords = false, // emit in world space so the trail stays behind the moving projectile
            Explosiveness = 0f,
        };
        var mat = new ParticleProcessMaterial
        {
            Direction = Vector3.Zero,
            Spread = 8f,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 8f,
            Gravity = new Vector3(0f, c.Gravity, 0f),
            ScaleMin = c.Scale * 0.5f,
            ScaleMax = c.Scale,
            Color = c.Color,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
        };
        var ramp = new Gradient();
        ramp.SetColor(0, c.Color);
        ramp.SetColor(1, new Color(c.Color.R, c.Color.G, c.Color.B, 0f));
        mat.ColorRamp = new GradientTexture1D { Gradient = ramp };
        p.ProcessMaterial = mat;

        // Apply the real particlefont atlas sprite when the effectinfo catalog is mounted.
        // The sprite defines the particle SHAPE (smoke wisps for TR_ROCKET, sparkle dots for TR_NEXUIZPLASMA,
        // fire blobs for fireball trails, etc.) while c.Color continues to tint it — matching the
        // StandardMaterial3D formula: albedo_texture × albedo_color × vertex_color_from_ramp.
        Texture2D? sprite = !string.IsNullOrEmpty(desc.TrailEffect)
            ? Effects?.QueryTrailSprite(desc.TrailEffect)
            : null;

        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        var meshMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = c.Additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = c.Color,
        };
        if (sprite is not null)
        {
            meshMat.AlbedoTexture = sprite;
            meshMat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        }
        quad.Material = meshMat;
        p.DrawPass1 = quad;
        return p;
    }

    private static OmniLight3D BuildLight(ProjectileCatalog.Desc desc)
        => new()
        {
            Name = "Light",
            LightColor = desc.GlowColor,
            LightEnergy = desc.Type is PType.Rocket or PType.Rpc ? 2.0f : 1.4f,
            OmniRange = desc.Body == BodyFamily.FireSprite ? 220f : 160f,
            ShadowEnabled = false,
        };

    /// <summary>Attach a looping spatial fly sound to the projectile root (QC <c>loopsound</c>). Graceful miss.</summary>
    private void AttachLoopSound(Node3D root, string sample)
    {
        AudioStream? stream = LoadLoopStream(sample);
        if (stream is null)
            return;

        var player = new AudioStreamPlayer3D
        {
            Name = "FlySound",
            Stream = stream,
            MaxDistance = 2048f,
            Autoplay = true,
        };
        // Restart on finish so short samples keep looping for the projectile's life (cheap, no glitch for
        // seamless loops; imported .ogg loop points are honored when present).
        player.Finished += () => { if (GodotObject.IsInstanceValid(player)) player.Play(); };
        root.AddChild(player);
    }

    /// <summary>Resolve a loop sample to a stream: the VFS <see cref="AudioLoader"/> first, then the
    /// <see cref="SoundResolver"/> <c>res://</c> fallback. Null = silent (graceful miss).</summary>
    private AudioStream? LoadLoopStream(string sample)
    {
        AudioStream? stream = AudioLoader?.Invoke(sample);
        if (stream is not null)
            return stream;

        string? resPath = SoundResolver is not null ? SoundResolver(sample) : $"res://sound/{sample}.ogg";
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    /// <summary>Build a soft round glow texture for energy-bolt sprites (radial alpha falloff).</summary>
    private static ImageTexture MakeGlowTexture()
    {
        const int size = 32;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp(1f - d, 0f, 1f);
            a *= a; // sharpen the core
            img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// The explosion/impact effect a projectile type leaves when it's removed (detonates) — the CSQC
    /// <c>wr_impacteffect</c> mapping. Splash weapons leave a real explosion; energy bolts a small burst; pure
    /// gibs/none return null (no effect). Names resolve through <see cref="EffectSystem.Spawn(string,NVec3,NVec3,int,Color?)"/>.
    /// </summary>
    private static string? ImpactEffectFor(PType t) => t switch
    {
        PType.Rocket or PType.Rpc or PType.SpiderRocket or PType.WakiRocket => "ROCKET_EXPLODE",
        PType.Fireball => "FIREBALL_EXPLODE",
        PType.Grenade or PType.GrenadeBouncing or PType.Mine => "GRENADE_EXPLODE",
        PType.Firemine => "GRENADE_EXPLODE",
        PType.Hagar or PType.HagarBouncing or PType.Seeker or PType.Flac or PType.Tag => "HAGAR_EXPLODE",
        PType.Electro or PType.ElectroBeam => "ELECTRO_BALLEXPLODE",
        PType.Crylink or PType.CrylinkBouncing => "CRYLINK_IMPACT",
        PType.Blaster or PType.RocketMinstaLaser => "BLASTER_IMPACT",
        PType.Hlac => "GREEN_HLAC_IMPACT",
        PType.GolemLightning or PType.MageSpike or PType.ArcBolt or PType.Plasma => "ELECTRO_IMPACT",
        _ => null,
    };

    /// <summary>Point the root along the entity's velocity (or its frame motion when velocity is zero).</summary>
    private static void OrientToVelocity(Node3D root, Entity e, Vector3? motionFallback = null)
    {
        Vector3 vel = Coords.ToGodot(e.Velocity);
        if (vel.LengthSquared() < 1f && motionFallback is { } m && m.LengthSquared() > 1e-4f)
            vel = m;
        if (vel.LengthSquared() < 1e-4f)
            return;
        Vector3 fwd = vel.Normalized();
        Vector3 up = Mathf.Abs(fwd.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
        // Godot LookAt makes -Z point at the target; aim a unit ahead along velocity.
        root.LookAt(root.Position + fwd, up);
    }

    /// <summary>Presence link for a projectile entity (so the sim can reach its node, like EntityNode).</summary>
    private sealed class ProjectilePresence : IEntityPresence
    {
        public Node3D Node { get; }
        public ProjectilePresence(Node3D node) => Node = node;
    }
}
