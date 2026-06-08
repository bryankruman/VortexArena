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

    /// <summary>
    /// Build the poser over an already-built IQM scene (<paramref name="iqmRoot"/>, the <see cref="IqmBuilder"/>
    /// output) parsed from <paramref name="iqm"/>, using its animation clips and skeletal parameters.
    /// </summary>
    public void Setup(IqmData iqm, Node3D iqmRoot, IReadOnlyList<FrameGroup>? groups, ModelInfo? info)
    {
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

        // The CPU poser owns the bones now — silence the AnimationPlayer so it doesn't overwrite our poses.
        foreach (Node child in iqmRoot.GetChildren())
            if (child is AnimationPlayer ap) { ap.Stop(); ap.Autoplay = ""; }

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

    /// <summary>Convert each CPU bone's posed model-space transform → Godot bone-local pose, parents first.</summary>
    private void PushBones()
    {
        int n = _skeleton.GetBoneCount();
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
            _skeleton.SetBonePoseScale(i, local.Basis.Scale);
        }
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
