// Port of common/mutators/mutator/bugrigs/bugrigs.qc

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Bug Rigs mutator — port of common/mutators/mutator/bugrigs/bugrigs.qc. A "big rigs" car-racing
/// physics override: while enabled, players drive a car-like rig (steer with strafe, accelerate with
/// forward/back) instead of the normal FPS movement. It REPLACES the player's physics via the PM_Physics
/// hook: <c>RaceCarPhysics</c> integrates a steering/acceleration/friction model and aligns the rig to the
/// surface, returning <c>true</c> so the default movement branch chain is fully skipped. Enabled by the
/// <c>g_bugrigs</c> cvar (mutators.cfg default <b>0</b>).
///
/// SVQC-only in Base (no client prediction — "disabled on the client side until prediction can be fixed"),
/// so this port runs it server-side; the <c>chase_active 1</c> 3rd-person camera + <c>disableclientprediction</c>
/// are client-presentation/prediction concerns noted as follow-ups.
///
/// Ported faithfully: the 15 <c>g_bugrigs_*</c> cvars (bugrigs_SetVars), <c>racecar_angle</c>, the whole
/// <c>RaceCarPhysics</c> drive model (responsiveness factor, reverse speeding/spinning/stopping,
/// floor/brake/air friction, the planar-movement surface-align traceboxes vs. the FLY fallback, the body
/// pitch/roll from local velocity, and the angle smoothing), the PM_Physics replacement (with the
/// bugrigs_prevangles restore), and the PlayerPhysics stash of bugrigs_prevangles. The QMath pitch
/// convention is honoured per call site: the planar surface-align uses <see cref="QMath.FixedVecToAngles"/>;
/// the FINAL body pitch/roll (bugrigs.qc:271-278) and the angle smoothing (bugrigs.qc:303) use the plain
/// <see cref="QMath.VecToAngles2"/> to match the QC originals exactly — that intermediate <c>angles</c> is not
/// fed straight to the renderer but immediately re-vectored through makevectors by the smoothing pass, so the
/// un-negated form is the faithful one (using FixedVecToAngles2 there inverts the car body pitch on slopes).
/// </summary>
[Mutator]
public sealed class BugrigsMutator : MutatorBase
{
    // The 15 g_bugrigs_* cvars (mutators.cfg:503-517 defaults).
    public bool PlanarMovement = true;          // g_bugrigs_planar_movement
    public bool CarJumping = true;              // g_bugrigs_planar_movement_car_jumping
    public bool ReverseSpeeding = true;         // g_bugrigs_reverse_speeding
    public bool ReverseSpinning = true;         // g_bugrigs_reverse_spinning
    public bool ReverseStopping = true;         // g_bugrigs_reverse_stopping
    public bool AirSteering = true;             // g_bugrigs_air_steering
    public float AngleSmoothing = 5f;          // g_bugrigs_angle_smoothing
    public float FrictionFloor = 50f;          // g_bugrigs_friction_floor
    public float FrictionBrake = 950f;         // g_bugrigs_friction_brake
    public float FrictionAir = 0.00001f;       // g_bugrigs_friction_air
    public float Accel = 800f;                 // g_bugrigs_accel
    public float SpeedRef = 400f;              // g_bugrigs_speed_ref
    public float SpeedPow = 2f;                // g_bugrigs_speed_pow
    public float Steer = 1f;                   // g_bugrigs_steer

    public BugrigsMutator() => NetName = "bugrigs";

    // QC SVQC: REGISTER_MUTATOR(bugrigs, cvar("g_bugrigs")) { MUTATOR_ONADD { bugrigs_SetVars(); } }
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_bugrigs") != 0f;

    private HookHandler<MutatorHooks.PMPhysicsArgs>? _onPmPhysics;
    private HookHandler<MutatorHooks.PlayerPhysicsArgs>? _onPlayerPhysics;

    public override void Hook()
    {
        _onPmPhysics ??= OnPmPhysics;
        _onPlayerPhysics ??= OnPlayerPhysics;
        MutatorHooks.PMPhysics.Add(_onPmPhysics);
        MutatorHooks.PlayerPhysics.Add(_onPlayerPhysics);

        SetVars(); // QC MUTATOR_ONADD { bugrigs_SetVars(); }
    }

    public override void Unhook()
    {
        if (_onPmPhysics is not null) MutatorHooks.PMPhysics.Remove(_onPmPhysics);
        if (_onPlayerPhysics is not null) MutatorHooks.PlayerPhysics.Remove(_onPlayerPhysics);
    }

    // void bugrigs_SetVars() — copy the 15 g_bugrigs_* cvars into the working fields.
    private void SetVars()
    {
        if (Api.Services is null) return;
        PlanarMovement = Api.Cvars.GetFloat("g_bugrigs_planar_movement") != 0f;
        CarJumping = Api.Cvars.GetFloat("g_bugrigs_planar_movement_car_jumping") != 0f;
        ReverseSpeeding = Api.Cvars.GetFloat("g_bugrigs_reverse_speeding") != 0f;
        ReverseSpinning = Api.Cvars.GetFloat("g_bugrigs_reverse_spinning") != 0f;
        ReverseStopping = Api.Cvars.GetFloat("g_bugrigs_reverse_stopping") != 0f;
        AirSteering = Api.Cvars.GetFloat("g_bugrigs_air_steering") != 0f;
        AngleSmoothing = Api.Cvars.GetFloat("g_bugrigs_angle_smoothing");
        FrictionFloor = Api.Cvars.GetFloat("g_bugrigs_friction_floor");
        FrictionBrake = Api.Cvars.GetFloat("g_bugrigs_friction_brake");
        FrictionAir = Api.Cvars.GetFloat("g_bugrigs_friction_air");
        Accel = Api.Cvars.GetFloat("g_bugrigs_accel");
        SpeedRef = Api.Cvars.GetFloat("g_bugrigs_speed_ref");
        SpeedPow = Api.Cvars.GetFloat("g_bugrigs_speed_pow");
        Steer = Api.Cvars.GetFloat("g_bugrigs_steer");
    }

    private static bool IsPlayer(Entity e) => (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(bugrigs, PM_Physics)
    private bool OnPmPhysics(ref MutatorHooks.PMPhysicsArgs args)
    {
        Entity player = args.Player;
        float dt = args.TicRate;

        // if (!PHYS_BUGRIGS(player) || !IS_PLAYER(player)) return;
        if (Api.Services is null || Api.Cvars.GetFloat("g_bugrigs") == 0f || !IsPlayer(player))
            return false;

        // #ifdef SVQC player.angles = player.bugrigs_prevangles;
        player.Angles = player.BugrigsPrevAngles;

        RaceCarPhysics(player, dt);
        return true; // fully replace the move (QC return true)
    }

    // MUTATOR_HOOKFUNCTION(bugrigs, PlayerPhysics)
    private bool OnPlayerPhysics(ref MutatorHooks.PlayerPhysicsArgs args)
    {
        if (Api.Services is null || Api.Cvars.GetFloat("g_bugrigs") == 0f) return false;
        Entity player = args.Player;
        // #ifdef SVQC player.bugrigs_prevangles = player.angles; player.disableclientprediction = 2;
        player.BugrigsPrevAngles = player.Angles;
        // disableclientprediction is a net/prediction concern (bugrigs is SVQC-only in Base); noted as a follow-up.
        return false;
    }

    /// <summary>QC PHYS_MAXSPEED(this): the player's movement maxspeed (sv_maxspeed in the headless sim).</summary>
    private static float MaxSpeed()
    {
        if (Api.Services is null) return 360f;
        float v = Api.Cvars.GetFloat("sv_maxspeed");
        return v != 0f ? v : 360f;
    }

    /// <summary>QC PHYS_GRAVITY(this): sv_gravity (* the entity's gravity scale).</summary>
    private static float Gravity(Entity player)
    {
        float g = Api.Services is null ? 800f : Api.Cvars.GetFloat("sv_gravity");
        if (g == 0f) g = 800f;
        return g * (player.Gravity != 0f ? player.Gravity : 1f);
    }

    /// <summary>QC vectoyaw(v) — the yaw (degrees) of a direction vector.</summary>
    private static float VecToYaw(Vector3 v) => QMath.VecToAngles(v).Y;

    // float racecar_angle(float forward, float down)
    private static float RacecarAngle(float forward, float down)
    {
        if (forward < 0f)
        {
            forward = -forward;
            down = -down;
        }
        float ret = VecToYaw(new Vector3(1f, 0f, 0f) * forward + new Vector3(0f, 1f, 0f) * down);
        float angleMult = forward / (800f + forward);
        if (ret > 180f)
            return ret * angleMult + 360f * (1f - angleMult);
        return ret * angleMult;
    }

    // void RaceCarPhysics(entity this, float dt)
    private void RaceCarPhysics(Entity self, float dt)
    {
        Vector3 rigvel;
        Vector3 anglesSave = self.Angles;

        float maxspeed = MaxSpeed();
        float accel = QMath.Bound(-1f, self.MovementForward / maxspeed, 1f);
        float steer = QMath.Bound(-1f, self.MovementRight / maxspeed, 1f);

        if (ReverseSpeeding)
        {
            if (accel < 0f)
            {
                // back accel is DIGITAL to prevent speedhack
                accel = accel < -0.5f ? -1f : 0f;
            }
        }

        // this.angles_x = 0; this.angles_z = 0; makevectors(this.angles);
        Vector3 ang = self.Angles; ang.X = 0f; ang.Z = 0f; self.Angles = ang;
        QMath.AngleVectors(self.Angles, out Vector3 vForward, out Vector3 vRight, out Vector3 vUp);

        bool onground = self.OnGround;
        if (onground || AirSteering)
        {
            float myspeed = QMath.Dot(self.Velocity, vForward);
            float upspeed = QMath.Dot(self.Velocity, vUp);

            // responsiveness factor f = 1 / (1 + (|myspeed| / speed_ref) ^ speed_pow)
            float f = 1f / (1f + MathF.Pow(MathF.Max(-myspeed, myspeed) / SpeedRef, SpeedPow));

            float steerfactor;
            if (myspeed < 0f && ReverseSpinning)
                steerfactor = -myspeed * Steer;
            else
                steerfactor = -myspeed * f * Steer;

            float accelfactor;
            if (myspeed < 0f && ReverseSpeeding)
                accelfactor = Accel;
            else
                accelfactor = f * Accel;

            if (accel < 0f)
            {
                if (myspeed > 0f)
                    myspeed = MathF.Max(0f, myspeed - dt * (FrictionFloor - FrictionBrake * accel));
                else
                {
                    if (!ReverseSpeeding)
                        myspeed = MathF.Min(0f, myspeed + dt * FrictionFloor);
                }
            }
            else
            {
                if (myspeed >= 0f)
                    myspeed = MathF.Max(0f, myspeed - dt * FrictionFloor);
                else
                {
                    if (ReverseStopping)
                        myspeed = 0f;
                    else
                        myspeed = MathF.Min(0f, myspeed + dt * (FrictionFloor + FrictionBrake * accel));
                }
            }

            // this.angles_y += steer * dt * steerfactor; makevectors(this.angles);
            ang = self.Angles; ang.Y += steer * dt * steerfactor; self.Angles = ang;
            QMath.AngleVectors(self.Angles, out vForward, out vRight, out vUp);

            myspeed += accel * accelfactor * dt;

            rigvel = myspeed * vForward + new Vector3(0f, 0f, 1f) * upspeed;
        }
        else
        {
            float myspeed = self.Velocity.Length();

            float f = 1f / (1f + MathF.Pow(MathF.Max(0f, myspeed / SpeedRef), SpeedPow));
            float steerfactor = -myspeed * f;
            ang = self.Angles; ang.Y += steer * dt * steerfactor; self.Angles = ang;

            rigvel = self.Velocity;
            QMath.AngleVectors(self.Angles, out vForward, out vRight, out vUp);
        }

        // rigvel *= max(0, 1 - vlen(rigvel) * friction_air * dt);
        rigvel *= MathF.Max(0f, 1f - rigvel.Length() * FrictionAir * dt);

        if (PlanarMovement)
        {
            float gravity = Gravity(self);
            rigvel.Z -= dt * gravity; // 4x gravity plays better (QC comment)
            Vector3 rigvelXy = new(rigvel.X, rigvel.Y, 0f);

            MoveFilter mt = CarJumping ? MoveFilter.Normal : MoveFilter.NoMonsters;

            // tracebox(origin, mins, maxs, origin + '0 0 1024', mt, this); up = trace_endpos - origin;
            TraceResult tr = Api.Trace.Trace(self.Origin, self.Mins, self.Maxs,
                self.Origin + new Vector3(0f, 0f, 1024f), mt, self);
            Vector3 up = tr.EndPos - self.Origin;

            // can we move? tracebox(trace_endpos, mins, maxs, trace_endpos + rigvel_xy * dt, mt, this);
            tr = Api.Trace.Trace(tr.EndPos, self.Mins, self.Maxs, tr.EndPos + rigvelXy * dt, mt, self);

            // align to surface: tracebox(trace_endpos, mins, maxs, trace_endpos - up + '0 0 1' * rigvel_z * dt, mt, this);
            tr = Api.Trace.Trace(tr.EndPos, self.Mins, self.Maxs,
                tr.EndPos - up + new Vector3(0f, 0f, 1f) * (rigvel.Z * dt), mt, self);

            float fraction = tr.Fraction;
            Vector3 neworigin;
            if (fraction < 0.5f)
            {
                fraction = 1f;
                neworigin = self.Origin;
            }
            else
                neworigin = tr.EndPos;

            if (fraction < 1f)
            {
                // point the car parallel to the surface (QC vectoangles of the surface-projected forward).
                Vector3 pn = tr.PlaneNormal;
                Vector3 aimVec =
                      new Vector3(1f, 0f, 0f) * (vForward.X * pn.Z)
                    + new Vector3(0f, 1f, 0f) * (vForward.Y * pn.Z)
                    + new Vector3(0f, 0f, 1f) * (-(vForward.X * pn.X + vForward.Y * pn.Y));
                self.Angles = QMath.FixedVecToAngles(aimVec);
                self.Flags |= EntFlags.OnGround;     // SET_ONGROUND
            }
            else
            {
                self.Flags &= ~EntFlags.OnGround;    // UNSET_ONGROUND
            }

            self.Velocity = (neworigin - self.Origin) * (1f / dt);
            self.MoveType = MoveType.Noclip;          // set_movetype(this, MOVETYPE_NOCLIP)
            Api.Entities.SetOrigin(self, neworigin);  // the engine doesn't push the rig — apply the move explicitly
        }
        else
        {
            float gravity = Gravity(self);
            rigvel.Z -= dt * gravity;
            self.Velocity = rigvel;
            self.MoveType = MoveType.Fly;             // set_movetype(this, MOVETYPE_FLY)
        }

        // final body pitch/roll: tracebox down 4u.
        TraceResult floor = Api.Trace.Trace(self.Origin, self.Mins, self.Maxs,
            self.Origin - new Vector3(0f, 0f, 4f), MoveFilter.Normal, self);
        if (floor.Fraction != 1f)
        {
            Vector3 pn = floor.PlaneNormal;
            Vector3 aimVec =
                  new Vector3(1f, 0f, 0f) * (vForward.X * pn.Z)
                + new Vector3(0f, 1f, 0f) * (vForward.Y * pn.Z)
                + new Vector3(0f, 0f, 1f) * (-(vForward.X * pn.X + vForward.Y * pn.Y));
            // QC bugrigs.qc:271-278 uses plain vectoangles2 here (the body-pitch is consumed by the angle
            // smoothing that follows, which round-trips through makevectors/vectoangles2 — not fed straight
            // to the renderer), so use the un-negated VecToAngles2. FixedVecToAngles2 would invert the car
            // body pitch on slopes. (The planar block at ~L287 keeps fixedvectoangles per the same convention.)
            self.Angles = QMath.VecToAngles2(aimVec, pn);
        }
        else
        {
            Vector3 velLocal = new(
                QMath.Dot(vForward, self.Velocity),
                QMath.Dot(vRight, self.Velocity),
                QMath.Dot(vUp, self.Velocity));
            ang = self.Angles;
            ang.X = RacecarAngle(velLocal.X, velLocal.Z);
            ang.Z = RacecarAngle(-velLocal.Y, velLocal.Z);
            self.Angles = ang;
        }

        // smooth the angles
        QMath.AngleVectors(self.Angles, out Vector3 vf, out _, out Vector3 vuNew);
        float fsm = QMath.Bound(0f, dt * AngleSmoothing, 1f);
        if (fsm == 0f) fsm = 1f;
        Vector3 vf1 = vf * fsm;
        Vector3 vu1 = vuNew * fsm;
        QMath.AngleVectors(anglesSave, out Vector3 vfSave, out _, out Vector3 vuSave);
        vf1 += (1f - fsm) * vfSave;
        vu1 += (1f - fsm) * vuSave;
        // QC: smoothangles = vectoangles2(vf1, vu1); this.angles_x = -smoothangles.x; this.angles_z = smoothangles.z;
        Vector3 smoothangles = QMath.VecToAngles2(vf1, vu1);
        ang = self.Angles;
        ang.X = -smoothangles.X;
        ang.Z = smoothangles.Z;
        self.Angles = ang;
    }
}
