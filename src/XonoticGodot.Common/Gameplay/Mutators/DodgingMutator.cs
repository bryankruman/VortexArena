using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Dodging mutator — port of common/mutators/mutator/dodging/sv_dodging.qc. A double-tap of a strafe
/// key (or the dedicated +dodge button) launches the player in that direction with a quick upward hop, the
/// horizontal force ramping in over a short time. Enabled by the <c>g_dodging</c> cvar.
///
/// Ported: the double-tap detection (<c>PM_dodging_checkpressedkeys</c>) driven off the per-frame movement
/// intent + pressed-key bits the input layer writes onto the entity (<see cref="Entity.MovementForward"/> /
/// <see cref="Entity.MovementRight"/> / <see cref="Entity.PressedKeys"/>), including the per-direction
/// key-press history, the speed→force mapping, and the ground/wall/air gating; plus the per-frame dodge
/// ramp/up-impulse state machine (<c>PM_dodging</c>) and the spawn/observer reset. A +dodge bind can also
/// call <see cref="TryStartDodge"/> directly.
/// </summary>
[Mutator]
public sealed class DodgingMutator : MutatorBase
{
    /// <summary>QC autocvar_sv_dodging_ramp_time — seconds over which the horizontal force is added.</summary>
    public float RampTime = 0.1f;

    /// <summary>QC autocvar_sv_dodging_up_speed — the one-shot vertical impulse.</summary>
    public float UpSpeed = 200f;

    /// <summary>QC autocvar_sv_dodging_delay — minimum seconds between dodges.</summary>
    public float Delay = 0.5f;

    /// <summary>QC autocvar_sv_dodging_horiz_force_slowest — horizontal force at the low end of the speed range.</summary>
    public float HorizForceSlowest = 200f;

    /// <summary>QC autocvar_sv_dodging_horiz_force_fastest — horizontal force at the high end of the speed range.</summary>
    public float HorizForce = 400f;

    /// <summary>QC autocvar_sv_dodging_horiz_speed_min — speed mapped to the slowest force.</summary>
    public float HorizSpeedMin = 400f;

    /// <summary>QC autocvar_sv_dodging_horiz_speed_max — speed mapped to the fastest force.</summary>
    public float HorizSpeedMax = 1000f;

    /// <summary>QC autocvar_sv_dodging_height_threshold — how close to the ground counts as "grounded" for a dodge.</summary>
    public float HeightThreshold = 10f;

    /// <summary>QC autocvar_sv_dodging_wall_distance_threshold — how close to a wall counts for a wall dodge.</summary>
    public float WallDistanceThreshold = 10f;

    /// <summary>QC autocvar_sv_dodging_wall_dodging — allow dodging off walls in midair.</summary>
    public bool WallDodging;

    /// <summary>QC autocvar_sv_dodging_air_dodging — allow dodging freely in midair.</summary>
    public bool AirDodging;

    /// <summary>QC autocvar_sv_dodging_maxspeed — ground-dodge disallowed above this speed (0 = no cap).</summary>
    public float MaxSpeed = 450f;

    /// <summary>QC autocvar_sv_dodging_air_maxspeed — air-dodge disallowed above this speed (0 = no cap).</summary>
    public float AirMaxSpeed;

    /// <summary>QC cl_dodging_timeout — max seconds between the two taps of a double-tap.</summary>
    public float Timeout = 0.2f;

    public DodgingMutator() => NetName = "dodging";

    // QC SVQC: REGISTER_MUTATOR(dodging, cvar("g_dodging")).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_dodging") != 0f;

    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPhysics;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;

    public override void Hook()
    {
        _onPhysics ??= OnPlayerPhysics;
        _onSpawn ??= OnPlayerSpawn;

        MutatorHooks.PlayerPhysics.Add(_onPhysics);
        MutatorHooks.PlayerSpawn.Add(_onSpawn);

        if (Api.Services is not null)
        {
            float r = Api.Cvars.GetFloat("sv_dodging_ramp_time");            if (r != 0f) RampTime = r;
            float u = Api.Cvars.GetFloat("sv_dodging_up_speed");            if (u != 0f) UpSpeed = u;
            float d = Api.Cvars.GetFloat("sv_dodging_delay");              if (d != 0f) Delay = d;
            float f = Api.Cvars.GetFloat("sv_dodging_horiz_force_fastest"); if (f != 0f) HorizForce = f;
            float fs = Api.Cvars.GetFloat("sv_dodging_horiz_force_slowest"); if (fs != 0f) HorizForceSlowest = fs;
            float sn = Api.Cvars.GetFloat("sv_dodging_horiz_speed_min");     if (sn != 0f) HorizSpeedMin = sn;
            float sx = Api.Cvars.GetFloat("sv_dodging_horiz_speed_max");     if (sx != 0f) HorizSpeedMax = sx;
            float ht = Api.Cvars.GetFloat("sv_dodging_height_threshold");    if (ht != 0f) HeightThreshold = ht;
            float wd = Api.Cvars.GetFloat("sv_dodging_wall_distance_threshold"); if (wd != 0f) WallDistanceThreshold = wd;
            WallDodging = Api.Cvars.GetFloat("sv_dodging_wall_dodging") != 0f;
            AirDodging = Api.Cvars.GetFloat("sv_dodging_air_dodging") != 0f;
            MaxSpeed = Api.Cvars.GetFloat("sv_dodging_maxspeed");
            AirMaxSpeed = Api.Cvars.GetFloat("sv_dodging_air_maxspeed");
            float to = Api.Cvars.GetFloat("cl_dodging_timeout");            if (to != 0f) Timeout = to;
        }
    }

    public override void Unhook()
    {
        if (_onPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPhysics);
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
    }

    // MUTATOR_HOOKFUNCTION(dodging, PlayerPhysics) — detect the double-tap, then drive the dodge ramp.
    // (QC runs PM_dodging_checkpressedkeys from the GetPressedKeys hook and PM_dodging from PlayerPhysics;
    // both are folded here, in QC order, since the headless sim drives input once per physics frame.)
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        CheckPressedKeys(args.Player);
        PMDodging(args.Player, args.TicRate);
        return false;
    }

    /// <summary>
    /// QC PM_dodging_checkpressedkeys: turn a double-tap of a strafe direction (or the +dodge button) into a
    /// dodge. Reads the per-frame movement intent + the previous frame's pressed-key bits the input layer
    /// wrote onto the entity, then starts a dodge if the tap is fresh, the delay has elapsed, and the player
    /// is in a state that allows it (near ground, near a wall, or freely in the air per the cvars).
    /// </summary>
    public bool CheckPressedKeys(Entity player)
    {
        if (Api.Services is null) return false;
        float now = Api.Clock.Time;

        float mvF = player.MovementForward;  // movement.x  (forward + / back -)
        float mvR = player.MovementRight;    // movement.y  (right + / left -)
        var pressed = (PressedKeyBits)player.PressedKeys;
        bool dodgeButton = (pressed & PressedKeyBits.Crouch) == 0 && DodgeButtonHeld(player);

        float tapX = 0f, tapY = 0f;
        bool detected = false;

        // QC X(COND,BTN,RESULT): a fresh press in a movement direction is a double-tap if it landed within
        // the timeout of the previous press in the same direction (or if +dodge is held). Each direction is
        // checked inline (rather than via a closure) to keep the ref-into-entity-field semantics explicit.
        if (mvF < 0f && (pressed & PressedKeyBits.Backward) == 0)
        {
            tapX -= 1f;
            if ((now - player.DodgeLastBackwardTime) < Timeout || dodgeButton) detected = true;
            player.DodgeLastBackwardTime = now;
        }
        if (mvF > 0f && (pressed & PressedKeyBits.Forward) == 0)
        {
            tapX += 1f;
            if ((now - player.DodgeLastForwardTime) < Timeout || dodgeButton) detected = true;
            player.DodgeLastForwardTime = now;
        }
        if (mvR < 0f && (pressed & PressedKeyBits.Left) == 0)
        {
            tapY -= 1f;
            if ((now - player.DodgeLastLeftTime) < Timeout || dodgeButton) detected = true;
            player.DodgeLastLeftTime = now;
        }
        if (mvR > 0f && (pressed & PressedKeyBits.Right) == 0)
        {
            tapY += 1f;
            if ((now - player.DodgeLastRightTime) < Timeout || dodgeButton) detected = true;
            player.DodgeLastRightTime = now;
        }

        bool started = false;
        if (detected)
        {
            // QC: the delay gate is checked after the keys so the *first* tap may precede the delay.
            if ((now - player.LastDodgingTime) >= Delay)
            {
                QMath.AngleVectors(player.Angles, out Vector3 forward, out Vector3 right, out _);
                float horiz = Horiz2D(player.Velocity);

                bool canGround = IsCloseToGround(player) && (MaxSpeed == 0f || player.Velocity.Length() < MaxSpeed);
                bool canWall = WallDodging && IsCloseToWall(player, forward, right);
                bool canAir = AirDodging && (AirMaxSpeed == 0f || player.Velocity.Length() < AirMaxSpeed);

                if (canGround || canWall || canAir)
                    started = BeginDodge(player, tapX, tapY, DetermineForce(horiz));
            }
        }

        // Refresh the pressed-key bits for next frame's state-change detection (QC PM_dodging_GetPressedKeys).
        var keys = PressedKeyBits.None;
        if (mvF > 0f) keys |= PressedKeyBits.Forward;
        if (mvF < 0f) keys |= PressedKeyBits.Backward;
        if (mvR > 0f) keys |= PressedKeyBits.Right;
        if (mvR < 0f) keys |= PressedKeyBits.Left;
        if ((player.Flags & EntFlags.JumpReleased) == 0) keys |= PressedKeyBits.Jump;
        if (player.ButtonCrouch) keys |= PressedKeyBits.Crouch;
        player.PressedKeys = (int)keys;

        return started;
    }

    private static bool DodgeButtonHeld(Entity player)
        // No dedicated +dodge button bit in the headless input yet; double-tap is the trigger. (When a
        // +dodge bind lands it can set a flag the input layer exposes here.)
        => false;

    // float determine_force(player) — map current horizontal speed to a force in [slowest..fastest].
    private float DetermineForce(float horizSpeed)
        => MapBoundRanges(horizSpeed, HorizSpeedMin, HorizSpeedMax, HorizForceSlowest, HorizForce);

    // QC map_bound_ranges(x, from_min, from_max, to_min, to_max) — clamp x to [from_min,from_max] then lerp.
    private static float MapBoundRanges(float x, float fromMin, float fromMax, float toMin, float toMax)
    {
        if (fromMax <= fromMin) return toMin;
        float t = (QMath.Clamp(x, fromMin, fromMax) - fromMin) / (fromMax - fromMin);
        return toMin + (toMax - toMin) * t;
    }

    private static float Horiz2D(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // QC v_angle — the exact aim angles; falls back to the body yaw when the input layer hasn't set them.
    private static Vector3 ViewAnglesOf(Entity e) => e.ViewAngles != Vector3.Zero ? e.ViewAngles : e.Angles;

    // bool is_close_to_ground(this, threshold, up)
    private bool IsCloseToGround(Entity player)
    {
        if (player.OnGround) return true;
        return TraceHitsNear(player, new Vector3(0f, 0f, -1f), HeightThreshold);
    }

    // bool is_close_to_wall(this, threshold, forward, right)
    private bool IsCloseToWall(Entity player, Vector3 forward, Vector3 right)
        => TraceHitsNear(player, right, WallDistanceThreshold)
        || TraceHitsNear(player, -right, WallDistanceThreshold)
        || TraceHitsNear(player, forward, WallDistanceThreshold)
        || TraceHitsNear(player, -forward, WallDistanceThreshold);

    // QC X(dir): tracebox a short distance; true if it hits a non-sky surface within range.
    private static bool TraceHitsNear(Entity player, Vector3 dir, float threshold)
    {
        Vector3 target = player.Origin + dir * threshold;
        TraceResult tr = Api.Trace.Trace(player.Origin, player.Mins, player.Maxs, target, MoveFilter.Normal, player);
        return tr.Fraction < 1f && (tr.DpHitQ3SurfaceFlags & MutatorConstants.Q3SurfaceFlagSky) == 0;
    }

    // MUTATOR_HOOKFUNCTION(dodging, PlayerSpawn) + MakePlayerObserver share dodging_ResetPlayer.
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        ResetPlayer(args.Player);
        return false;
    }

    /// <summary>
    /// Begin a dodge in the given horizontal direction (the result of the QC double-tap check).
    /// Returns false when on cooldown. Exposed so an input layer / +dodge bind can trigger it directly;
    /// the force is computed from the player's current horizontal speed (QC determine_force).
    /// </summary>
    public bool TryStartDodge(Entity player, Vector2 direction)
    {
        if (Api.Services is null) return false;
        if ((Api.Clock.Time - player.LastDodgingTime) < Delay) return false;
        return BeginDodge(player, direction.X, direction.Y, DetermineForce(Horiz2D(player.Velocity)));
    }

    // Set up the dodge state (caller has already validated the delay + state gates). QC sets dodging_action,
    // the one-shot up-impulse flag, the ramped force, and the normalized tap direction.
    private bool BeginDodge(Entity player, float dirX, float dirY, float force)
    {
        float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (len <= 0f) return false;
        dirX /= len; dirY /= len;

        player.LastDodgingTime = Api.Clock.Time;
        player.DodgingAction = 1f;
        player.DodgingSingleAction = 1f;
        player.DodgingForceTotal = force;
        player.DodgingForceRemaining = force;
        player.DodgingDirection = new Vector3(dirX, dirY, 0f);
        return true;
    }

    // void PM_dodging(entity this)
    private void PMDodging(Entity player, float ticRate)
    {
        if (player.DodgingAction == 0f) return;

        // QC: no dodging while swimming or dead.
        if (player.WaterLevel >= 2 || player.DeadState != DeadFlag.No)
        {
            player.DodgingAction = 0f;
            player.DodgingDirection = Vector3.Zero;
            return;
        }

        // QC uses v_angle when air-dodging (so the dodge follows the exact aim), else the body yaw.
        Vector3 angles = AirDodging ? ViewAnglesOf(player) : player.Angles;
        QMath.AngleVectors(angles, out Vector3 forward, out Vector3 right, out Vector3 up);

        // QC ramp: add force_total * (frametime / ramp_time) each frame, capped by what remains.
        float frameTime = ticRate > 0f ? ticRate : 1f / 60f;
        float commonFactor = RampTime > 0f ? frameTime / RampTime : 1f;
        float increase = MathF.Min(commonFactor * player.DodgingForceTotal, player.DodgingForceRemaining);
        player.DodgingForceRemaining -= increase;

        Vector3 dir = player.DodgingDirection;
        player.Velocity += dir.X * increase * forward + dir.Y * increase * right;

        // The up part is a one-shot.
        if (player.DodgingSingleAction == 1f)
        {
            player.Flags &= ~EntFlags.OnGround; // QC UNSET_ONGROUND
            player.Velocity += UpSpeed * up;
            // QC: if autocvar_sv_dodging_sound, PlayerSound(playersound_jump) + animdecide ANIMACTION_JUMP.
            if (Api.Services is not null && Api.Cvars.GetFloat("sv_dodging_sound") != 0f)
                Api.Sound.Play(player, SoundChannel.Body, "player/jump.wav");
            player.DodgingSingleAction = 0f;
        }

        if (player.DodgingForceRemaining <= 0f)
        {
            player.DodgingAction = 0f;
            player.DodgingDirection = Vector3.Zero;
        }
    }

    // void dodging_ResetPlayer(entity this)
    private static void ResetPlayer(Entity player)
    {
        player.LastDodgingTime = 0f;
        player.DodgingAction = 0f;
        player.DodgingSingleAction = 0f;
        player.DodgingForceTotal = 0f;
        player.DodgingForceRemaining = 0f;
        player.DodgingDirection = Vector3.Zero;
    }
}
