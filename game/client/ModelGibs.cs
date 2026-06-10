using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Game;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Model gibs — the real limb/chunk meshes a player throws when gibbed, instead of a generic particle
/// burst. The C# successor to CSQC's gibs.qc (the <c>net_gibsplash</c> handler + TossGib / Gib_Draw):
/// a gib splash of "type 1" tosses an eye, a bloody skull, then per-amount a spray of arms, chests, legs and
/// fast-flying chunks, each a bouncing MOVETYPE_BOUNCE body that fades out after cl_gibs_lifetime.
///
/// We load the real MD3 gib models from the mounted content (models/gibs/*.md3) via the host model loader;
/// the Quake1 <c>chunk.mdl</c> isn't handled by the IQM/DPM/MD3 loader, so those fast chunks fall back to a
/// small generated mesh. Physics (gravity + ground bounce + tumble) are integrated client-side per tick,
/// exactly as the QC gib is a pure client drawable advanced by Movetype_Physics_MatchTicrate.
/// </summary>
public sealed partial class ModelGibs : Node3D
{
    /// <summary>Gib lifetime seconds (DP cl_gibs_lifetime default 14, trimmed so they don't pile up).</summary>
    [Export] public float GibLifetime { get; set; } = 8f;

    /// <summary>Hard cap on live gibs (DP cl_gibs_maxcount).</summary>
    [Export] public int MaxGibs { get; set; } = 64;

    private int _liveCount;

    /// <summary>Host model loader (e.g. <c>AssetLoader.LoadModel</c>); null =&gt; generated placeholder chunks.</summary>
    public Func<string, Node3D?>? ModelLoader { get; set; }

    // The MD3 limb models a normal (type 0x01) gib splash tosses (gibs.qc). chunk.mdl is Quake1 (placeholder).
    private static readonly string[] LimbModels =
    {
        "models/gibs/arm.md3",
        "models/gibs/arm.md3",
        "models/gibs/chest.md3",
        "models/gibs/smallchest.md3",
        "models/gibs/leg1.md3",
        "models/gibs/leg2.md3",
    };

    /// <summary>
    /// Spawn a full gib splash at <paramref name="origin"/> (Quake space) with base <paramref name="velocity"/>,
    /// scaled by <paramref name="amount"/> (the QC gibbage multiplier, ~1..15). Bounces off the ground plane at
    /// <paramref name="floorZ"/>. This is the type 0x01 ("full") splash; lesser types fall out as a few chunks.
    /// </summary>
    public void Splash(NVec3 origin, NVec3 velocity, float amount = 4f, float floorZ = float.NegativeInfinity)
    {
        amount = Math.Clamp(amount, 1f, 16f);

        // Always toss an eye and a bloody skull (QC tosses these unconditionally with prandom gates).
        Toss("models/gibs/eye.md3", origin, velocity, RandomVec() * 150f, floorZ, destroyOnTouch: false);
        Toss("models/gibs/bloodyskull.md3", origin + RandomVec() * 16f, velocity, RandomVec() * 100f, floorZ, false);

        // Per the QC loop: for c in 0..amount, gate each limb on (amount-c) so early iterations spawn more.
        for (int c = 0; c < amount; c++)
        {
            float randomValue = amount - c;
            foreach (string mdl in LimbModels)
            {
                if (GD.Randf() < randomValue)
                {
                    NVec3 jitter = RandomVec() * 16f + new NVec3(0f, 0f, 4f);
                    Toss(mdl, origin + jitter, velocity, RandomVec() * (GD.Randf() * 120f + 85f), floorZ, false);
                }
            }
            // Fast chunks that splat on impact (chunk.mdl -> placeholder mesh).
            for (int k = 0; k < 4; k++)
                if (GD.Randf() < randomValue)
                    Toss("models/gibs/chunk.mdl", origin + RandomVec() * 16f, velocity, RandomVec() * 450f, floorZ, destroyOnTouch: true);
        }
    }

    /// <summary>Toss one gib of the given model. Public so callers can drop a single gib (e.g. a chunk).</summary>
    public Node3D Toss(string modelPath, NVec3 origin, NVec3 baseVel, NVec3 randVel, float floorZ, bool destroyOnTouch)
    {
        // QC: velocity = vconst*velocity_scale + vrand*velocity_random + up. We fold the cvars into sane
        // constants (scale 1, random 1, up 100) so it reads like the default config.
        NVec3 vel = baseVel + randVel + new NVec3(0f, 0f, 100f);

        Node3D mesh = BuildMesh(modelPath);
        var gib = new GibBody
        {
            Name = "gib",
            Position = Coords.ToGodot(origin),
            VelocityQuake = vel,
            Lifetime = GibLifetime * (1f + GD.Randf() * 0.15f),
            FloorZ = floorZ,
            DestroyOnTouch = destroyOnTouch,
            AngularVel = RandomVecG() * (vel.Length() * 0.02f),
        };
        gib.AddChild(mesh);

        AddChild(gib);
        gib.OnFreed = () => _liveCount = Math.Max(0, _liveCount - 1);
        _liveCount++;
        CullIfNeeded();
        return gib;
    }

    // ------------------------------------------------------------------------------------------------

    private Node3D BuildMesh(string modelPath)
    {
        if (ModelLoader is not null && !modelPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Node3D? loaded = ModelLoader(modelPath);
                if (loaded is not null)
                    return loaded;
            }
            catch { /* fall through */ }
        }
        return GeneratedChunk();
    }

    // The fallback chunk mesh+material is identical for every generated gib, so build it once and share the Mesh
    // resource across all instances (only the lightweight MeshInstance3D node is per-gib). Avoids constructing a
    // BoxMesh + StandardMaterial3D — and compiling that material's shader on first draw — on the gib's frame.
    private static BoxMesh? _chunkMesh;

    /// <summary>A small reddish chunk used for chunk.mdl and any model that fails to load.</summary>
    private static MeshInstance3D GeneratedChunk()
    {
        _chunkMesh ??= new BoxMesh
        {
            Size = new Vector3(4f, 4f, 4f),
            Material = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.06f, 0.05f),
                Roughness = 0.9f,
            },
        };
        return new MeshInstance3D { Mesh = _chunkMesh };
    }

    private void CullIfNeeded()
    {
        if (_liveCount <= MaxGibs)
            return;
        foreach (Node child in GetChildren())
        {
            if (child is GibBody gb && GodotObject.IsInstanceValid(gb))
            {
                gb.QueueFree();
                _liveCount = Math.Max(0, _liveCount - 1);
                if (_liveCount <= MaxGibs)
                    break;
            }
        }
    }

    private static NVec3 RandomVec()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    private static Vector3 RandomVecG()
        => new((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));

    // ================================================================================================
    //  Per-gib ballistic body (the client MOVETYPE_BOUNCE analogue from Gib_Draw)
    // ================================================================================================

    public sealed partial class GibBody : Node3D
    {
        public NVec3 VelocityQuake;
        public Vector3 AngularVel;
        public float Lifetime = 8f;
        public float FloorZ = float.NegativeInfinity;
        public bool DestroyOnTouch;
        public Action? OnFreed;

        private const float Gravity = 800f;       // sv_gravity; gibs use gravity 1 (full)
        private const float BounceFactor = 0.4f;  // gib bouncefactor-ish
        private float _age;
        private bool _resting;

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

            if (!_resting)
            {
                VelocityQuake.Z -= Gravity * dt;
                NVec3 posQ = Coords.ToQuake(Position) + VelocityQuake * dt;

                if (!float.IsNegativeInfinity(FloorZ) && posQ.Z <= FloorZ && VelocityQuake.Z < 0f)
                {
                    if (DestroyOnTouch)
                    {
                        // chunk.mdl-style: splat on first ground contact.
                        OnFreed?.Invoke();
                        QueueFree();
                        return;
                    }
                    posQ.Z = FloorZ;
                    VelocityQuake.Z = -VelocityQuake.Z * BounceFactor;
                    VelocityQuake.X *= 0.6f;
                    VelocityQuake.Y *= 0.6f;
                    AngularVel *= 0.5f;
                    if (VelocityQuake.Length() < 25f)
                    {
                        _resting = true;
                        VelocityQuake = default;
                    }
                }
                Position = Coords.ToGodot(posQ);
                RotateObjectLocal(Vector3.Up, AngularVel.Z * dt);
                RotateObjectLocal(Vector3.Right, AngularVel.X * dt);
            }

            // Fade over the final second (QC sets alpha = bound(0, nextthink-time, 1)).
            float remaining = Lifetime - _age;
            if (remaining < 1f)
                ApplyAlpha(this, Math.Clamp(remaining, 0f, 1f));
        }

        private static void ApplyAlpha(Node node, float a)
        {
            if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
            {
                int surfaces = mesh.GetSurfaceCount();
                for (int s = 0; s < surfaces; s++)
                {
                    if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D mat)
                    {
                        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                        Color c = mat.AlbedoColor;
                        mat.AlbedoColor = new Color(c.R, c.G, c.B, a);
                    }
                }
            }
            foreach (Node child in node.GetChildren())
                ApplyAlpha(child, a);
        }
    }
}
