using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Loaders.Models;
using SN = System.Numerics;

namespace XonoticGodot.Game.Client;

/// <summary>
/// A networked player's rendered model: the IQM <see cref="Skeleton3D"/> scene (built by
/// <see cref="IqmBuilder"/>) driven each frame by the CPU skeletal poser <see cref="PlayerSkeleton"/> — the
/// upper/lower-body split (the torso plays an aim pose while the legs run/strafe) plus the view-pitch AIM
/// bones, exactly as <c>player_skeleton.qc</c> does. Replaces the morph/placeholder path for players.
///
/// Each frame we synthesize the four-pose <see cref="SkeletonAnim"/> from the entity's movement state
/// (<see cref="LocomotionBlend"/>), run <see cref="PlayerSkeleton.FromFrames"/>, then push every bone's posed
/// model-space transform onto the <see cref="Skeleton3D"/> — conjugated from Quake space into Godot space via
/// <see cref="IqmBuilder.ConjugateQuakeWorldToGodot"/> and localized against its parent, so the posed mesh
/// stays consistent with the world-conjugated rests + skin. The built-in <see cref="AnimationPlayer"/> is
/// stopped so it can't fight these per-frame writes.
/// </summary>
public partial class PlayerModel : Node3D
{
    private Skeleton3D _skeleton = null!;
    private SkeletonManager _mgr = null!;
    private PlayerSkeleton _player = null!;
    private int _skel;

    // legs locomotion clips by intent, plus a stable torso clip; resolved once from the model's framegroups.
    private readonly FrameGroup[] _legClips = new FrameGroup[6];
    private FrameGroup _torsoClip;

    private float _legsTime;     // seconds the legs clip has been playing (advances with the active locomotion)
    private LocomotionBlend.Locomotion _lastLoco = (LocomotionBlend.Locomotion)(-1);

    /// <summary>True once the skeleton + poser are wired (a non-skeletal IQM stays a static prop).</summary>
    public bool Active { get; private set; }

    /// <summary>The networked entity this model renders (set when attached; <c>ClientWorld._Process</c> poses it).</summary>
    public Entity? Bound { get; set; }

    // --- streaming placeholder (perf §9.4 Wave 1) ---------------------------------------------------------
    // While the skeletal model parses on the thread pool, the player renders as this shared gray box (the
    // same one ClientWorld drops for an unresolvable model). Shared mesh+material: a per-shell
    // StandardMaterial3D would compile a fresh pipeline per player. The box also matters for correctness:
    // CsqcModelEffects caches the node's mesh list and re-collects when a cached mesh is FREED — clearing
    // the placeholder is what invalidates that cache once the real meshes arrive.
    private static readonly BoxMesh PlaceholderMesh = new() { Size = new Vector3(24f, 56f, 24f) };
    private static readonly StandardMaterial3D PlaceholderMaterial = new() { AlbedoColor = new Color(0.6f, 0.6f, 0.65f) };
    private MeshInstance3D? _placeholder;

    /// <summary>Show the streaming placeholder box (no-op if already shown). Cleared by <see cref="Setup"/>.</summary>
    public void ShowPlaceholder()
    {
        if (_placeholder is not null)
            return;
        _placeholder = new MeshInstance3D
        {
            Name = "Placeholder",
            Mesh = PlaceholderMesh,
            Position = new Vector3(0f, 28f, 0f),
            MaterialOverride = PlaceholderMaterial,
        };
        AddChild(_placeholder);
    }

    /// <summary>Drop the streaming placeholder (the freed mesh is what re-triggers the csqc mesh-list collect).</summary>
    public void ClearPlaceholder()
    {
        if (_placeholder is null)
            return;
        if (GodotObject.IsInstanceValid(_placeholder))
            _placeholder.QueueFree();
        _placeholder = null;
    }

    /// <summary>
    /// Build the poser over an already-built IQM scene (<paramref name="iqmRoot"/>, the <see cref="IqmBuilder"/>
    /// output) parsed from <paramref name="iqm"/>, using its animation clips and skeletal parameters.
    /// </summary>
    public void Setup(IqmData iqm, Node3D iqmRoot, IReadOnlyList<FrameGroup>? groups, ModelInfo? info)
    {
        ClearPlaceholder(); // the real model replaces the streaming box (even the non-skeletal static prop)

        // The CPU poser owns the bones — silence the AnimationPlayer so it can't overwrite our per-frame
        // poses. This must happen BEFORE the scene enters the tree: autoplay fires on tree entry, and setting
        // Autoplay afterwards is a no-op (Godot warns). On the streamed path Setup runs on a shell that is
        // ALREADY in the tree, so the AddChild below is the tree entry.
        foreach (Node child in iqmRoot.GetChildren())
            if (child is AnimationPlayer ap) { ap.Stop(); ap.Autoplay = ""; }

        AddChild(iqmRoot);
        Skeleton3D? skel = IqmBuilder.FindSkeleton(iqmRoot);
        if (skel is null || iqm.Joints.Length == 0)
            return; // not skeletal — render it as the static prop it is

        _skeleton = skel;
        var model = new IqmSkeletalModel(iqm);
        _mgr = new SkeletonManager();
        _skel = _mgr.Create(model);
        if (_skel == 0)
            return;

        PlayerSkeletonConfig cfg = info is not null
            ? PlayerSkeletonConfig.FromModelInfo(info, _mgr, _skel)
            : new PlayerSkeletonConfig();
        _player = new PlayerSkeleton(_mgr, model, cfg);

        BuildClipTable(iqm, groups);

        Active = true;
    }

    /// <summary>
    /// Pose the skeleton for this frame from the entity's movement + view pitch. <paramref name="dt"/> advances
    /// the legs clip. Call once per rendered frame from <c>ClientWorld._Process</c>.
    /// </summary>
    public void Pose(Entity e, float dt)
    {
        if (!Active) return;

        float speed2d = MathF.Sqrt(e.Velocity.X * e.Velocity.X + e.Velocity.Y * e.Velocity.Y);
        bool dead = e.DeadState is DeadFlag.Dying or DeadFlag.Dead || (e.Health <= 0f && e.MaxHealth > 0f);
        LocomotionBlend.Locomotion loco = LocomotionBlend.SelectLegs(speed2d, e.OnGround, e.IsDucked, dead);

        if (loco != _lastLoco) { _legsTime = 0f; _lastLoco = loco; }
        else _legsTime += dt;

        FrameGroup legs = _legClips[(int)loco];
        SkeletonAnim anim = LocomotionBlend.Split(legs, _legsTime, _torsoClip, 0f);
        _player.FromFrames(anim, e.Angles.X, dead);
        PushBones();
    }

    /// <summary>Release the CPU skeleton handle (QC <c>skel_delete</c>). Call before freeing the node.</summary>
    public void ReleaseSkeleton()
    {
        if (Active) { _player.Free(); Active = false; }
    }

    // 3.3: per-bone flag — true when this bone currently has a NON-unit pose scale set. Player skeletons are
    // rigid (every bone is unit-scale), so SetBonePoseScale is a pure-overhead interop call ~50-60×/player/frame;
    // we skip it while the scale stays unit. The flag makes the skip safe for a model that DOES animate a bone's
    // scale: when such a bone returns to unit we set it back to one (instead of leaving the stale scale).
    private bool[]? _boneScaleNonUnit;

    /// <summary>Convert each CPU bone's posed model-space transform → Godot bone-local pose, parents first.</summary>
    private void PushBones()
    {
        int n = _skeleton.GetBoneCount();
        if (_boneScaleNonUnit is null || _boneScaleNonUnit.Length != n)
            _boneScaleNonUnit = new bool[n];
        Span<Transform3D> worldGodot = n <= 256 ? stackalloc Transform3D[n] : new Transform3D[n];
        for (int i = 0; i < n; i++)
        {
            BoneMatrix abs = _player.GetBoneAbs(i + 1); // PlayerSkeleton bones are 1-based; Skeleton3D 0-based, same order
            Transform3D quakeWorld = ToTransform(abs);
            Transform3D wg = IqmBuilder.ConjugateQuakeWorldToGodot(quakeWorld);
            worldGodot[i] = wg;

            int parent = _skeleton.GetBoneParent(i);
            Transform3D local = (parent >= 0 && parent < i) ? worldGodot[parent].AffineInverse() * wg : wg;

            _skeleton.SetBonePosePosition(i, local.Origin);
            _skeleton.SetBonePoseRotation(i, local.Basis.GetRotationQuaternion());

            // 3.3: skip the SetBonePoseScale interop while the scale is unit (the rigid-player common case);
            // only touch it when this bone's scale is non-unit, or to reset a bone that just returned to unit.
            Vector3 scale = local.Basis.Scale;
            bool unit = IsUnitScale(scale);
            if (!unit)
            {
                _skeleton.SetBonePoseScale(i, scale);
                _boneScaleNonUnit[i] = true;
            }
            else if (_boneScaleNonUnit[i])
            {
                _skeleton.SetBonePoseScale(i, Vector3.One);
                _boneScaleNonUnit[i] = false;
            }
        }
    }

    /// <summary>True when a bone scale is unit within a small epsilon (so the scale interop can be skipped).</summary>
    private static bool IsUnitScale(Vector3 s)
    {
        const float eps = 0.001f;
        return MathF.Abs(s.X - 1f) < eps && MathF.Abs(s.Y - 1f) < eps && MathF.Abs(s.Z - 1f) < eps;
    }

    // A Quake-space BoneMatrix (Fwd/Left/Up columns + Origin) as a Godot Transform3D, still in Quake coords
    // (the conjugation to Godot space happens afterward). Godot's Basis(x,y,z) takes the COLUMN vectors.
    private static Transform3D ToTransform(in BoneMatrix m)
        => new(new Basis(GV(m.Fwd), GV(m.Left), GV(m.Up)), GV(m.Origin));

    private static Vector3 GV(SN.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Resolve the legs' per-intent clips + a torso clip from the model's framegroups (by a name keyword, else
    /// a sensible fallback). Without framegroups, the whole pose range becomes one looping clip.
    /// </summary>
    private void BuildClipTable(IqmData iqm, IReadOnlyList<FrameGroup>? groups)
    {
        int total = Math.Max(1, iqm.Frames.Length);
        var whole = new FrameGroup(0, total, 20f, true);

        FrameGroup Pick(params string[] keys)
        {
            if (groups is not null)
                foreach (FrameGroup g in groups)
                    foreach (string k in keys)
                        if (!string.IsNullOrEmpty(g.Name) && g.Name.Contains(k, StringComparison.OrdinalIgnoreCase))
                            return g;
            // fall back to the first framegroup, else the whole range.
            return groups is { Count: > 0 } ? groups[0] : whole;
        }

        _legClips[(int)LocomotionBlend.Locomotion.Idle] = Pick("idle", "stand");
        _legClips[(int)LocomotionBlend.Locomotion.Walk] = Pick("walk", "forward");
        _legClips[(int)LocomotionBlend.Locomotion.Run] = Pick("run", "forward", "walk");
        _legClips[(int)LocomotionBlend.Locomotion.Jump] = Pick("jump");
        _legClips[(int)LocomotionBlend.Locomotion.Crouch] = Pick("crouch", "duck");
        _legClips[(int)LocomotionBlend.Locomotion.Dead] = Pick("death", "dead", "die");
        _torsoClip = Pick("idle", "stand", "aim");
    }
}
