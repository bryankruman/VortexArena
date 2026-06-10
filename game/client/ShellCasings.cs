using System;
using Godot;
using XonoticGodot.Game;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Ejected shell casings — the small brass/shell meshes a weapon throws on each shot. The C# successor to
/// CSQC's casings.qc (the <c>casings</c> net temp-entity + Casing_Draw): MDL_CASING_BULLET
/// (models/casing_bronze.iqm) for bullet weapons, MDL_CASING_SHELL (models/casing_shell.mdl) for the
/// shotgun. A casing is a bouncing rigid body (MOVETYPE_BOUNCE, gravity 1, an avelocity tumble) that lives
/// for cl_casings_*_time seconds and fades out.
///
/// We integrate the ballistic + floor-bounce ourselves in <see cref="CasingBody._PhysicsProcess"/> (the sim
/// runs server-side; casings are pure client eye-candy in QC too, advanced by Movetype_Physics_MatchTicrate
/// on the CSQC side). World collision beyond a ground plane is out of scope — the lifetime fade keeps them
/// from piling up, exactly like the QC maxcount cull.
/// </summary>
public sealed partial class ShellCasings : Node3D
{
    /// <summary>Casing kind, matching the QC <c>casing.state</c> switch (1 = shotgun shell, else bullet).</summary>
    public enum CasingKind { Bullet = 0, Shell = 1 }

    /// <summary>Bullet-casing lifetime seconds (DP cl_casings_bronze_time default).</summary>
    [Export] public float BulletTime { get; set; } = 1.5f;

    /// <summary>Shell-casing lifetime seconds (DP cl_casings_shell_time default).</summary>
    [Export] public float ShellTime { get; set; } = 2.0f;

    /// <summary>Hard cap on live casings (DP cl_casings_maxcount).</summary>
    [Export] public int MaxCasings { get; set; } = 64;

    private int _liveCount;

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>): given a virtual model path
    /// returns a fresh Godot node, or null on miss. When unset, casings render as a tiny generated cylinder.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    /// <summary>
    /// Eject a casing from <paramref name="origin"/> (Quake space) with initial <paramref name="velocity"/>
    /// (Quake space). The casing tumbles, falls under gravity, bounces off the ground plane at
    /// <paramref name="floorZ"/> (Quake Z), and fades out. Returns the spawned body.
    /// </summary>
    public Node3D Spawn(NVec3 origin, NVec3 velocity, CasingKind kind = CasingKind.Bullet, float floorZ = float.NegativeInfinity)
    {
        // QC adds a little jitter + a tumble (avelocity '0 10 0' + 100*prandomvec) on receipt.
        NVec3 vel = velocity + RandomVec() * 6f;
        float life = kind == CasingKind.Shell ? ShellTime : BulletTime;
        float bounce = kind == CasingKind.Shell ? 0.25f : 0.5f;

        Node3D mesh = BuildMesh(kind);

        var body = new CasingBody
        {
            Name = "casing",
            Position = Coords.ToGodot(origin),
            VelocityQuake = vel,
            BounceFactor = bounce,
            Lifetime = life,
            FloorZ = floorZ,
            AngularVel = new Vector3(
                (float)GD.RandRange(-12.0, 12.0),
                (float)GD.RandRange(-12.0, 12.0),
                (float)GD.RandRange(-12.0, 12.0)),
        };
        body.AddChild(mesh);

        AddChild(body);
        body.OnFreed = () => _liveCount = Math.Max(0, _liveCount - 1);
        _liveCount++;
        CullIfNeeded();
        return body;
    }

    // ------------------------------------------------------------------------------------------------

    private Node3D BuildMesh(CasingKind kind)
    {
        // Prefer the real casing model. The shotgun shell is a Quake1 .mdl (not handled by the IQM/DPM/MD3
        // loader) so it falls through to the generated mesh; the bullet casing is an IQM and loads cleanly.
        string vpath = kind == CasingKind.Shell ? "models/casing_shell.mdl" : "models/casing_bronze.iqm";
        if (ModelLoader is not null)
        {
            try
            {
                Node3D? loaded = ModelLoader(vpath);
                if (loaded is not null)
                    return loaded;
            }
            catch { /* fall through to generated mesh */ }
        }
        return GeneratedCasing(kind);
    }

    // Each casing kind's fallback mesh+material is identical across spawns, so build the two variants once and
    // share the Mesh resource (only the MeshInstance3D node is per-casing). Avoids constructing a CylinderMesh +
    // StandardMaterial3D — and the material's first-draw shader compile — on the casing's frame.
    private static CylinderMesh? _shellMesh;
    private static CylinderMesh? _bulletMesh;

    /// <summary>A tiny brass cylinder used when the real casing model can't be loaded.</summary>
    private static MeshInstance3D GeneratedCasing(CasingKind kind)
    {
        bool shell = kind == CasingKind.Shell;
        CylinderMesh mesh = shell
            ? _shellMesh ??= BuildCasingMesh(true)
            : _bulletMesh ??= BuildCasingMesh(false);
        // Lay the cylinder along its travel-ish axis (Godot cylinder is Y-up by default; rotate to be a pin).
        return new MeshInstance3D
        {
            Mesh = mesh,
            RotationDegrees = new Vector3(90f, 0f, 0f),
        };
    }

    private static CylinderMesh BuildCasingMesh(bool shell)
    {
        Color brass = shell ? new Color(0.7f, 0.15f, 0.12f) : new Color(0.78f, 0.62f, 0.25f);
        return new CylinderMesh
        {
            TopRadius = shell ? 0.9f : 0.5f,
            BottomRadius = shell ? 0.9f : 0.5f,
            Height = shell ? 3.0f : 1.6f,
            RadialSegments = 6,
            Rings = 0,
            Material = new StandardMaterial3D
            {
                AlbedoColor = brass,
                Metallic = 0.8f,
                Roughness = 0.35f,
            },
        };
    }

    private void CullIfNeeded()
    {
        if (_liveCount <= MaxCasings)
            return;
        // Free the oldest casing child past the cap (cheap FIFO over the node list).
        foreach (Node child in GetChildren())
        {
            if (child is CasingBody cb && GodotObject.IsInstanceValid(cb))
            {
                cb.QueueFree();
                _liveCount = Math.Max(0, _liveCount - 1);
                if (_liveCount <= MaxCasings)
                    break;
            }
        }
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    // ================================================================================================
    //  The per-casing ballistic body
    // ================================================================================================

    /// <summary>
    /// One casing: integrates a simple gravity + ground-bounce trajectory each physics tick (the client-side
    /// MOVETYPE_BOUNCE analogue from Casing_Draw), tumbles via an angular velocity, and fades+frees at the
    /// end of its lifetime. Velocity is stored in Quake space and converted for the Godot transform.
    /// </summary>
    public sealed partial class CasingBody : Node3D
    {
        public NVec3 VelocityQuake;
        public Vector3 AngularVel;
        public float BounceFactor = 0.5f;
        public float Lifetime = 1.5f;
        public float FloorZ = float.NegativeInfinity;
        public Action? OnFreed;

        // DP world gravity (sv_gravity default). gib/casing entities use gravity 1 (full).
        private const float Gravity = 800f;
        private float _age;
        private bool _onGround;

        public override void _PhysicsProcess(double delta)
        {
            float dt = (float)delta;
            _age += dt;
            if (_age >= Lifetime)
            {
                OnFreed?.Invoke();
                QueueFree();
                return;
            }

            if (!_onGround)
            {
                // Integrate gravity in Quake space (Z up).
                VelocityQuake.Z -= Gravity * dt;
                NVec3 posQ = Coords.ToQuake(Position) + VelocityQuake * dt;

                // Ground-plane bounce: when crossing FloorZ heading down, reflect Z and damp by bouncefactor.
                if (!float.IsNegativeInfinity(FloorZ) && posQ.Z <= FloorZ && VelocityQuake.Z < 0f)
                {
                    posQ.Z = FloorZ;
                    VelocityQuake.Z = -VelocityQuake.Z * BounceFactor;
                    VelocityQuake.X *= 0.7f;
                    VelocityQuake.Y *= 0.7f;
                    AngularVel *= 0.6f;
                    // Once it's barely moving, let it rest (QC zeroes pitch/roll on ground).
                    if (VelocityQuake.Length() < 20f)
                    {
                        _onGround = true;
                        VelocityQuake = default;
                    }
                }
                Position = Coords.ToGodot(posQ);
            }

            // Tumble while airborne.
            if (!_onGround)
                RotateObjectLocal(Vector3.Right, AngularVel.X * dt);

            // Fade out over the final 0.4s of life.
            float remaining = Lifetime - _age;
            if (remaining < 0.4f)
            {
                float a = Math.Clamp(remaining / 0.4f, 0f, 1f);
                ApplyAlpha(this, a);
            }
        }

        private static void ApplyAlpha(Node node, float a)
        {
            if (node is MeshInstance3D mi && mi.GetSurfaceOverrideMaterialCount() >= 0)
            {
                // Only our generated cylinder carries a StandardMaterial3D we can fade; loaded models keep
                // their own materials (fading those would need per-instance overrides — skipped, they just pop).
                if (mi.Mesh is { } mesh && mesh.SurfaceGetMaterial(0) is StandardMaterial3D mat)
                {
                    mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    Color c = mat.AlbedoColor;
                    mat.AlbedoColor = new Color(c.R, c.G, c.B, a);
                }
            }
            foreach (Node child in node.GetChildren())
                ApplyAlpha(child, a);
        }
    }
}
