using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Walljump mutator — port of common/mutators/mutator/walljump/walljump.qc. Tapping jump while
/// airborne and next to a wall bounces the player off it (away from the wall, with an upward boost).
/// Enabled by the <c>g_walljump</c> cvar.
///
/// Ported: the nearby-wall trace (PlayerTouchWall, skipping NOIMPACT surfaces), the delay gate, the
/// airborne / alive / unfrozen guards, the off-wall velocity impulse with the crouch-held downward slam,
/// and the jump grant via the PlayerJump multijump out-flag plus the jump sound. The smoke-ring particle
/// is emitted at the contact point once the effect/particle service is wired.
/// </summary>
[Mutator]
public sealed class WalljumpMutator : MutatorBase
{
    /// <summary>QC STAT(WALLJUMP_FORCE) — magnitude of the off-wall horizontal push.</summary>
    public float WallForce = 300f;

    /// <summary>QC STAT(WALLJUMP_VELOCITY_XY_FACTOR) — horizontal velocity is divided by this after the push.</summary>
    public float XyFactor = 1.05f;

    /// <summary>QC STAT(WALLJUMP_VELOCITY_Z_FACTOR) — multiplier on jump velocity for the upward boost.</summary>
    public float ZFactor = 1f;

    /// <summary>QC STAT(WALLJUMP_DELAY) — minimum seconds between wall jumps.</summary>
    public float Delay = 0.3f;

    public WalljumpMutator() => NetName = "walljump";

    // QC SVQC: REGISTER_MUTATOR(walljump, autocvar_g_walljump);
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_walljump") != 0f;

    private HookHandler<MutatorHooks.PlayerJumpArgs>? _onJump;

    public override void Hook()
    {
        _onJump ??= OnPlayerJump;
        MutatorHooks.PlayerJump.Add(_onJump);

        if (Api.Services is not null)
        {
            float f = Api.Cvars.GetFloat("g_walljump_force");                  if (f != 0f) WallForce = f;
            float xy = Api.Cvars.GetFloat("g_walljump_velocity_xy_factor");    if (xy != 0f) XyFactor = xy;
            float z = Api.Cvars.GetFloat("g_walljump_velocity_z_factor");      if (z != 0f) ZFactor = z;
            float d = Api.Cvars.GetFloat("g_walljump_delay");                  if (d != 0f) Delay = d;
        }
    }

    public override void Unhook()
    {
        if (_onJump is not null) MutatorHooks.PlayerJump.Remove(_onJump);
    }

    // MUTATOR_HOOKFUNCTION(walljump, PlayerJump)
    private bool OnPlayerJump(ref MutatorHooks.PlayerJumpArgs args)
    {
        Entity player = args.Player;
        if (Api.Services is null) return false;
        float now = Api.Clock.Time;

        // QC guards: delay elapsed, airborne, walk-ish movetype, jump not held, not frozen, alive.
        if (now - player.LastWallJumpTime <= Delay) return false;
        if (player.OnGround) return false;
        if (player.MoveType is MoveType.None or MoveType.Follow or MoveType.Fly or MoveType.Noclip) return false;
        bool jumpHeld = (player.Flags & EntFlags.JumpReleased) == 0;
        if (jumpHeld) return false;
        if (player.DeadState != DeadFlag.No) return false;

        // QC: !STAT(FROZEN, player) && !StatusEffects_active(STATUSEFFECT_Frozen, player).
        var frozen = StatusEffectsCatalog.Frozen;
        if (frozen is not null && StatusEffectsCatalog.Has(player, frozen)) return false;

        Vector3 planeNormal = PlayerTouchWall(player, out Vector3 hitPos);
        if (planeNormal == Vector3.Zero)
            return false;

        // QC off-wall impulse.
        float jumpVel = JumpVelocity();
        Vector3 v = player.Velocity;
        v.X += planeNormal.X * WallForce; v.X /= XyFactor;
        v.Y += planeNormal.Y * WallForce; v.Y /= XyFactor;
        v.Z = jumpVel * ZFactor;
        // QC: if crouch held, invert the z (slam down off the wall).
        if (player.ButtonCrouch) v.Z *= -1f;
        player.Velocity = v;

        player.LastWallJumpTime = now;
        player.OldOrigin = player.Origin; // QC also stashes oldvelocity; origin keeps the anti-stick reference

        // QC presentation: a smoke ring at the contact point, the jump voice, and the jump animation.
        Api.Sound.Play(player, SoundChannel.Body, "player/jump.wav");
        _ = hitPos; // smoke-ring effect spawns here once the effect/particle service lands

        args.Multijump = true; // QC: M_ARGV(2, bool) = true
        return false;
    }

    /// <summary>
    /// QC PlayerTouchWall(this): tracebox forward/back/left/right a short distance; return the plane normal
    /// of the first steep (near-vertical) non-NOIMPACT surface within range (and its contact point), else zero.
    /// </summary>
    private static Vector3 PlayerTouchWall(Entity player, out Vector3 hitPos)
    {
        const float dist = 10f;       // QC dist
        const float maxNormal = 0.2f; // QC max_normal — surface counts as "wall" if normal.z below this
        const float scaler = 100f;    // QC scaler — how far to cast

        Vector3 start = player.Origin;
        QMath.AngleVectors(player.Angles, out Vector3 forward, out Vector3 right, out _);

        Vector3 found = Vector3.Zero, foundPos = Vector3.Zero;

        bool Try(Vector3 target)
        {
            TraceResult tr = Api.Trace.Trace(start, player.Mins, player.Maxs, target, MoveFilter.Normal, player);
            if (tr.Fraction < 1f
                && (player.Origin - tr.EndPos).Length() < dist
                && tr.PlaneNormal.Z < maxNormal
                && (tr.DpHitQ3SurfaceFlags & MutatorConstants.Q3SurfaceFlagNoImpact) == 0)
            {
                found = tr.PlaneNormal;
                foundPos = tr.EndPos;
                return true;
            }
            return false;
        }

        _ = Try(start + forward * scaler)
            || Try(start - forward * scaler)
            || Try(start + right * scaler)
            || Try(start - right * scaler);

        hitPos = foundPos;
        return found;
    }

    private static float JumpVelocity()
    {
        if (Api.Services is null) return 260f;
        float v = Api.Cvars.GetFloat("sv_jumpvelocity");
        return v != 0f ? v : 260f;
    }
}
