using XonoticGodot.Formats.Sidecars;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Synthesizes the per-tick <see cref="SkeletonAnim"/> that drives <see cref="PlayerSkeleton.FromFrames"/>,
/// from the single networked <c>Entity.Frame</c> + the player's movement state + the model's animation clips.
///
/// The server networks ONE frame index per entity; the upper/lower-body split needs a four-pose blend (legs +
/// torso). The CSQC client therefore reconstructs it (in Xonotic the <c>animdecide</c> + CSQCMODEL system).
/// This helper covers the core: the LEGS play a locomotion clip with smooth inter-frame interpolation
/// (<c>frame</c>/<c>frame2</c> + <c>lerpfrac</c>), and the TORSO shows its clip's current frame as the upper
/// body (<c>frame3</c> at full split weight) for the aim bones to bend by view pitch. This reproduces the
/// Xonotic look — legs run/strafe while the torso holds an aim pose — and is the seam where the full
/// per-action torso animation (attack/reload overlays, torso inter-frame lerp) can later be layered in.
///
/// Encoding note (see <see cref="PlayerSkeleton.FromFrames"/>): the split doubles the input lerpfracs, so the
/// legs' phase is halved here, and the torso's split weight is 0.5 (→ 1.0 after doubling). <c>lerpfrac4</c> is
/// kept 0 so the torso's frames never bleed into the lower body.
/// </summary>
public static class LocomotionBlend
{
    /// <summary>
    /// Sample a clip at <paramref name="time"/> seconds: the two bracketing absolute frame indices and the
    /// fractional lerp between them. Looping wraps the phase; a non-looping clip clamps at the last frame.
    /// </summary>
    public static (int frameA, int frameB, float lerp) SampleClip(in FrameGroup clip, float time)
    {
        int n = clip.FrameCount;
        if (n <= 1)
            return (clip.FirstFrame, clip.FirstFrame, 0f);

        float phase = time * clip.Fps;
        if (clip.Loop)
        {
            phase %= n;
            if (phase < 0f) phase += n;
        }
        else
        {
            if (phase < 0f) phase = 0f;
            else if (phase > n - 1) phase = n - 1;
        }

        int a = (int)System.MathF.Floor(phase);
        float lerp = phase - a;
        int b = clip.Loop ? (a + 1) % n : System.Math.Min(a + 1, n - 1);
        return (clip.FirstFrame + a, clip.FirstFrame + b, lerp);
    }

    /// <summary>
    /// Build the split <see cref="SkeletonAnim"/>: <paramref name="legs"/> animates smoothly (frame/frame2),
    /// <paramref name="torso"/>'s current frame is the upper body (frame3), to be bent by the aim bones.
    /// </summary>
    public static SkeletonAnim Split(in FrameGroup legs, float legsTime, in FrameGroup torso, float torsoTime)
    {
        (int la, int lb, float ll) = SampleClip(legs, legsTime);
        (int ta, int tb, float _) = SampleClip(torso, torsoTime);
        return new SkeletonAnim
        {
            Frame = la,                 // legs current
            Frame2 = lb,                // legs next (lower-body inter-frame lerp)
            Frame3 = ta,                // torso current (the upper-body pose)
            Frame4 = tb,                // torso next (reserved; not yet weighted into the upper split)
            Lerp = ll * 0.5f,           // doubled by the lower split → full legs phase
            Lerp3 = 0.5f,               // doubled by the upper split → upper body == frame3 (torso)
            Lerp4 = 0f,                 // keep torso out of the lower body
        };
    }

    /// <summary>Coarse locomotion intent from the player's movement state (QC animdecide, simplified).</summary>
    public enum Locomotion { Idle, Walk, Run, Jump, Crouch, Dead }

    /// <summary>
    /// Pick the legs' locomotion clip from movement state (the torso uses a stable idle/aim clip independently).
    /// Mirrors the existing MD3 movement heuristic so skeletal and morph models read the same.
    /// </summary>
    public static Locomotion SelectLegs(float speed2d, bool onGround, bool ducked, bool dead)
    {
        if (dead) return Locomotion.Dead;
        if (!onGround) return Locomotion.Jump;
        if (ducked) return Locomotion.Crouch;
        if (speed2d > 220f) return Locomotion.Run;
        if (speed2d > 20f) return Locomotion.Walk;
        return Locomotion.Idle;
    }
}
