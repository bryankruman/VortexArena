// Port of the CSQC half of qcsrc/common/mapobjects/misc/laser.qc (Draw_Laser, laser.qc:291-340):
// the persistent client renderer for misc_laser beams.
//
// Per visible laser it keeps ONE long-lived cross-ribbon mesh (two perpendicular textured quads — the
// cheap stand-in for QC Draw_CylindricLine, same technique as BeamRenderer but UPDATED IN PLACE instead
// of spawning/freeing a node per frame) plus ONE persistent OmniLight3D for the QC adddynamiclight at the
// hit point. End-point particles (__pointparticles(cnt, endpos, normal, drawframetime*1000)) are
// throttled to ~10 Hz per laser through EffectSystem.Spawn so the transient-node budget stays sane while
// the per-second particle budget matches DP's.
//
// Like TriggerTouch.Predict*Ambient / MusicPlayer, this scans the AMBIENT entity facade
// (Api.Entities.FindByClass("misc_laser")) — so it lights up on the listen-server and demo paths, where
// Api.Services is the live server world. A pure --connect client has no facade (and no BSP) yet, so it
// stays idle there: the established seam, not a silent gap.
//
// Known approximations vs Draw_Laser: the beam texture's sliding texcoord (time*3) is not animated, and
// the client-side trace runs at the SERVER tick origin (no InterpolateOrigin — lasers are static on all
// shipped maps).

using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>Persistent misc_laser beam renderer (the Draw_Laser successor). Hosted by <see cref="ClientWorld"/>.</summary>
public partial class LaserRenderer : Node3D
{
    /// <summary>End-point particle effects route through the shared effect system (throttled).</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Host texture loader (AssetSystem.LoadTexture) for particles/laserbeam.tga. Late-bindable.</summary>
    public Func<string, Texture2D?>? TextureLoader { get; set; }

    private const float RescanInterval = 2f;     // facade re-scan cadence (lasers can spawn via target_spawn)
    private const float EndFxInterval = 0.1f;    // ~10 Hz end-effect bursts per laser
    private const int Q3SurfaceFlagSky = 0x4;    // Q3SURFACEFLAG_SKY

    private sealed class BeamNode
    {
        public Entity Entity = null!;
        public Node3D Root = null!;              // positioned at the laser origin, +X aimed at the endpoint
        public MeshInstance3D RibbonA = null!;   // the two crossed quads
        public MeshInstance3D RibbonB = null!;
        public StandardMaterial3D Material = null!;
        public OmniLight3D Light = null!;
        public float NextFxTime;
    }

    private readonly Dictionary<Entity, BeamNode> _beams = new();
    private float _rescanIn;
    private Texture2D? _beamTex;
    private bool _beamTexTried;

    public override void _Process(double delta)
    {
        if (Api.Services is null)
            return;

        _rescanIn -= (float)delta;
        if (_rescanIn <= 0f)
        {
            Rescan();
            _rescanIn = RescanInterval;
        }

        float now = Api.Clock.Time;
        foreach (BeamNode b in _beams.Values)
            UpdateBeam(b, now);
    }

    // =================================================================================================
    //  Facade scan / node lifecycle
    // =================================================================================================

    private void Rescan()
    {
        foreach (Entity e in Api.Entities.FindByClass("misc_laser"))
        {
            if (e.IsFreed || _beams.ContainsKey(e))
                continue;
            _beams[e] = BuildBeam(e);
        }

        // Drop freed lasers (killtarget can remove them).
        List<Entity>? dead = null;
        foreach (var kv in _beams)
            if (kv.Key.IsFreed)
                (dead ??= new List<Entity>()).Add(kv.Key);
        if (dead is not null)
        {
            foreach (Entity e in dead)
            {
                if (GodotObject.IsInstanceValid(_beams[e].Root))
                    _beams[e].Root.QueueFree();
                _beams.Remove(e);
            }
        }
    }

    private BeamNode BuildBeam(Entity e)
    {
        var root = new Node3D { Name = $"laser#{e.Index}" };

        // Cross-ribbon: two unit quads spanning local +X (0..1), crossed 90° about X, scaled per frame to
        // (length, width). QuadMesh lies in the XY plane; orient it so its long edge runs along +X.
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,   // QC default: DRAWFLAG_ADDITIVE at 0.5
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableReceiveShadows = true,
        };
        ApplyBeamTexture(mat);

        var quad = new QuadMesh { Size = new Vector2(1f, 1f), Material = mat };
        // A unit quad is centered at the origin; park it at +0.5 X so the root sits at the beam START.
        var ribbonA = new MeshInstance3D { Mesh = quad, Position = new Vector3(0.5f, 0f, 0f) };
        var ribbonB = new MeshInstance3D { Mesh = quad, Position = new Vector3(0.5f, 0f, 0f) };
        ribbonB.RotationDegrees = new Vector3(90f, 0f, 0f); // cross plane
        root.AddChild(ribbonA);
        root.AddChild(ribbonB);

        // The QC dlight at the hit point (adddynamiclight(endpos+normal, modelscale-based radius, color*5)).
        var light = new OmniLight3D
        {
            Name = "endlight",
            Visible = false,
            OmniRange = 50f,
            ShadowEnabled = false,
        };
        root.AddChild(light);

        AddChild(root);
        return new BeamNode { Entity = e, Root = root, RibbonA = ribbonA, RibbonB = ribbonB, Material = mat, Light = light };
    }

    private void ApplyBeamTexture(StandardMaterial3D mat)
    {
        if (!_beamTexTried)
        {
            _beamTexTried = true;
            _beamTex = TextureLoader?.Invoke("particles/laserbeam")
                       ?? TextureLoader?.Invoke("particles/laserbeam.tga");
        }
        if (_beamTex is not null)
        {
            mat.AlbedoTexture = _beamTex;
            mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        }
    }

    // =================================================================================================
    //  Per-frame beam update (Draw_Laser)
    // =================================================================================================

    private void UpdateBeam(BeamNode b, float now)
    {
        Entity e = b.Entity;
        if (e.IsFreed || !GodotObject.IsInstanceValid(b.Root))
            return;

        // skip if ACTIVE_NOT (laser.qc:293)
        if (e.Active != MapMover.ActiveActive)
        {
            b.Root.Visible = false;
            return;
        }

        bool finite = (e.SpawnFlags & Laser.Finite) != 0;
        bool noTrace = (e.SpawnFlags & Laser.NoTrace) != 0;

        // --- endpoint (mirrors Draw_Laser:296-321, reading the shared server entity) ---
        NVec3 start = e.Origin;
        NVec3 target;
        if (e.Enemy is { } enemy && !enemy.IsFreed)
        {
            target = finite
                ? enemy.Origin
                : start + QMath.Normalize(enemy.Origin - start) * Laser.BeamMaxLength;
        }
        else if (finite)
        {
            b.Root.Visible = false; // FINITE with no resolved endpoint: nothing to draw yet
            return;
        }
        else
        {
            target = start + QMath.Forward(e.MAngle) * Laser.BeamMaxLength;
        }

        NVec3 end = target;
        NVec3 hitNormal = default;
        bool hitSurface = false;
        if (!noTrace)
        {
            TraceResult tr = Api.Trace.Trace(start, NVec3.Zero, NVec3.Zero, target, MoveFilter.Normal, e);
            end = tr.EndPos;
            hitNormal = tr.PlaneNormal;
            bool sky = (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlagSky) != 0;
            if (!finite && sky)
                end = start + QMath.Normalize(target - start) * Laser.BeamMaxWorldSize; // beyond the sky
            hitSurface = tr.Fraction < 1f && !sky;
        }

        // --- ribbon transform ---
        // QC wire decode: client beam width = 2 * server .scale; scale 0 draws no beam (laser.qc:322,369).
        float width = 2f * e.ScaleFactor;
        if (width <= 0f)
        {
            b.Root.Visible = false;
            return;
        }

        Vector3 gStart = Coords.ToGodot(start);
        Vector3 gEnd = Coords.ToGodot(end);
        Vector3 seg = gEnd - gStart;
        float len = seg.Length();
        if (len < 1f)
        {
            b.Root.Visible = false;
            return;
        }

        b.Root.Visible = true;
        b.Root.Position = gStart;
        // +X along the beam (same stable-basis trick as ProjectileRenderer.OrientToVelocity).
        Vector3 x = seg / len;
        Vector3 upRef = Mathf.Abs(x.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        Vector3 z = x.Cross(upRef).Normalized();
        Vector3 y = z.Cross(x).Normalized();
        b.Root.Basis = new Basis(x, y, z);
        b.RibbonA.Scale = new Vector3(len, width, 1f);
        b.RibbonB.Scale = new Vector3(len, width, 1f);

        // --- color/blend (laser.qc:324-331): alpha key set => normal blend at .alpha, else additive 0.5 ---
        var color = new Color(e.BeamColor.X, e.BeamColor.Y, e.BeamColor.Z);
        if (e.AlphaKey > 0f)
        {
            b.Material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
            b.Material.AlbedoColor = new Color(color, Mathf.Clamp(e.AlphaKey, 0f, 1f));
        }
        else
        {
            b.Material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
            b.Material.AlbedoColor = new Color(color, 0.5f);
        }

        // --- end effects on a real surface hit (laser.qc:333-339) ---
        if (hitSurface)
        {
            // dlight: radius 50*modelscale (the QC wire decode base), color beam_color*5 (clamped to a hue+energy).
            if (e.BeamColor != NVec3.Zero && e.ModelScale > 0f)
            {
                b.Light.Visible = true;
                b.Light.Position = b.Root.ToLocal(Coords.ToGodot(end + hitNormal * 1f));
                b.Light.OmniRange = Mathf.Clamp(50f * e.ModelScale, 1f, 512f);
                float maxc = MathF.Max(1f, MathF.Max(color.R, MathF.Max(color.G, color.B)) * 5f);
                b.Light.LightColor = new Color(color.R * 5f / maxc, color.G * 5f / maxc, color.B * 5f / maxc);
                b.Light.LightEnergy = MathF.Min(4f, maxc);
            }
            else
            {
                b.Light.Visible = false;
            }

            // particles: DP spawns cnt-effect bursts EVERY frame at count=drawframetime*1000; one burst per
            // 0.1 s at count=100 lands the same ~1000 count-units/sec without per-frame node churn.
            if (!string.IsNullOrEmpty(e.LaserEndEffect) && Effects is not null && now >= b.NextFxTime)
            {
                b.NextFxTime = now + EndFxInterval;
                Effects.Spawn(e.LaserEndEffect, end, hitNormal, (int)(EndFxInterval * 1000f));
            }
        }
        else
        {
            b.Light.Visible = false;
        }
    }
}
