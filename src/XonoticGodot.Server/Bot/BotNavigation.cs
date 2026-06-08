using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Per-bot navigation: the goal stack and path follower — the C# port of the navigation half of
/// server/bot/default/navigation.qc (clearroute/pushroute/poproute, routetogoal) and the steering core
/// of havocbot_movetogoal in havocbot.qc.
///
/// Model: <see cref="SetGoal"/> plans a path (waypoint A* + final goal) and pushes it onto the goal
/// stack (QC <c>goalcurrent</c>/<c>goalstack01..31</c>, here a plain <see cref="List{T}"/> used as a
/// stack with the front = current goal). Each frame <see cref="Steer"/> pops goals the bot has reached,
/// produces a forward/side <c>MoveValues</c> vector toward the current goal, and decides jump/crouch by
/// probing ahead with <see cref="Services.Api"/>.Trace (QC tracebox obstacle/step/fall checks).
///
/// One instance per bot, owned by <see cref="BotBrain"/>.
/// </summary>
public sealed class BotNavigation
{
    private const int MaxGoals = 32;        // QC goalstack depth (goalcurrent + goalstack01..31)
    private const float GoalReachedXY = 24f; // horizontal "touched the waypoint" radius
    private const float GoalReachedZ = 48f;  // vertical tolerance
    private const float StepHeight = 34f;    // QC stepheightvec.z — walkable step
    private const float JumpStepHeight = 48f; // QC jumpstepheightvec.z — reachable with a jump

    /// <summary>One entry on the goal stack: a world point plus the waypoint flags that govern how to
    /// traverse it (jump/crouch/teleport/ladder) and the source waypoint (for box-volume reach tests).</summary>
    private readonly struct Goal
    {
        public readonly Vector3 Pos;
        public readonly WaypointFlags Flags;
        public readonly Waypoint? Wp;
        public Goal(Vector3 pos, WaypointFlags flags = WaypointFlags.None, Waypoint? wp = null)
        {
            Pos = pos; Flags = flags; Wp = wp;
        }
    }

    /// <summary>The goal stack, front (index 0) = current goal (QC goalcurrent).</summary>
    private readonly List<Goal> _goals = new(MaxGoals);

    /// <summary>The final target entity, if the goal is an item/enemy (QC <c>.goalentity</c>). May be null.</summary>
    public Entity? GoalEntity;

    /// <summary>Sim time the bot last used a teleporter/jumppad goal (QC <c>.lastteleporttime</c>), to avoid re-triggering.</summary>
    public float LastTeleportTime;

    /// <summary>Player bounding box (QC PL_MIN/PL_MAX) used for trace boxes. Set by the brain on spawn.</summary>
    public Vector3 Mins = new(-16f, -16f, -24f);
    public Vector3 Maxs = new(16f, 16f, 45f);

    /// <summary>QC autocvar_sv_maxspeed — magnitude of emitted wish-move.</summary>
    public float MaxSpeed = 320f;

    /// <summary>Set true while steering when an obstacle/up-step needs a jump (QC PHYS_INPUT_BUTTON_JUMP).</summary>
    public bool WantJump { get; private set; }

    /// <summary>Set true while steering when traversing a crouch waypoint (QC PHYS_INPUT_BUTTON_CROUCH).</summary>
    public bool WantCrouch { get; private set; }

    /// <summary>The current goal point, or null if the stack is empty (QC <c>.goalcurrent</c>).</summary>
    public Vector3? Current => _goals.Count > 0 ? _goals[0].Pos : null;

    public bool HasGoal => _goals.Count > 0;

    /// <summary>Clear the route (QC navigation_clearroute).</summary>
    public void ClearRoute()
    {
        _goals.Clear();
        GoalEntity = null;
        LastTeleportTime = 0f;
    }

    /// <summary>Push a goal point to the front of the stack (QC navigation_pushroute).</summary>
    public void PushRoute(Vector3 goal) => PushRoute(new Goal(goal));

    private void PushRoute(Goal goal)
    {
        if (_goals.Count >= MaxGoals)
            _goals.RemoveAt(_goals.Count - 1); // drop the farthest; bot will re-plan after the first 31 steps
        _goals.Insert(0, goal);
    }

    /// <summary>Pop the current goal (QC navigation_poproute), e.g. when a waypoint is reached.</summary>
    public void PopRoute()
    {
        if (_goals.Count > 0)
            _goals.RemoveAt(0);
    }

    /// <summary>
    /// Plan a route from <paramref name="origin"/> to <paramref name="goalPos"/> over the waypoint network
    /// and load it onto the goal stack (QC navigation_routetogoal). If origin and goal are directly
    /// reachable (or there's no network), pushes just the goal. The final <paramref name="goalEntity"/>
    /// (item/enemy) is remembered as <see cref="GoalEntity"/>.
    /// </summary>
    public void SetGoal(Vector3 origin, Vector3 goalPos, WaypointNetwork? net, Entity? goalEntity = null)
    {
        ClearRoute();
        GoalEntity = goalEntity;

        // Always end at the real goal position.
        PushRoute(goalPos);

        // If we can walk straight there, no waypoints needed (QC routetogoal early-out via tracewalk).
        if (CanWalkStraight(origin, goalPos))
            return;

        if (net is null || net.Count == 0)
            return; // no graph: just head toward the goal and rely on obstacle avoidance

        var startWp = net.Nearest(origin);
        var goalWp = net.Nearest(goalPos);
        if (startWp is null || goalWp is null)
            return;

        var path = net.FindPath(startWp, goalWp);
        if (path is null || path.Count == 0)
            return;

        // Push intermediate waypoints in reverse so the FIRST waypoint ends up at the front of the stack,
        // ahead of the final goal point (which is already at the front). QC pushes goal first, then walks
        // the back-pointer chain pushing each waypoint, achieving the same front-to-back ordering. Each
        // waypoint carries its flags so Steer can drive jump/crouch/teleport/ladder traversal.
        for (int i = path.Count - 1; i >= 0; i--)
        {
            Waypoint wp = path[i];
            PushRoute(new Goal(wp.Center, wp.Flags, wp));
        }
    }

    /// <summary>
    /// Advance the route follower one frame and produce a wish-move toward the current goal
    /// (QC havocbot_movetogoal core). Pops reached goals, sets <see cref="WantJump"/>/<see cref="WantCrouch"/>,
    /// and returns forward/side/up move values in the bot's local frame (X forward, Y side, Z up), scaled to
    /// <see cref="MaxSpeed"/>. <paramref name="viewYaw"/> is the bot's current yaw (degrees) used to project
    /// the world move direction into the local frame. Returns zero when there's no goal.
    /// </summary>
    public Vector3 Steer(Entity bot, float viewYaw, bool onGround)
    {
        WantJump = false;
        WantCrouch = false;

        // Pop any goals we've effectively reached (QC navigation_poptouchedgoals). A teleport/jumppad goal is
        // "reached" once we've entered its trigger volume — the trigger then moves us, so we note the time and
        // pop so the bot doesn't try to stand on the destination (QC the lastteleporttime handling).
        while (_goals.Count > 0 && ReachedGoal(bot, _goals[0]))
        {
            if ((_goals[0].Flags & WaypointFlags.Teleport) != 0)
                LastTeleportTime = Now;
            PopRoute();
        }

        if (_goals.Count == 0)
            return Vector3.Zero;

        Goal goal = _goals[0];
        Vector3 destorg = goal.Pos;
        Vector3 diff = destorg - bot.Origin;
        Vector3 dir = QMath.Normalize(diff);
        var flat = new Vector3(diff.X, diff.Y, 0f);
        Vector3 flatdir = flat.LengthSquared() > 0f ? QMath.Normalize(flat) : dir;

        // ---- crouch waypoint: hold crouch while traversing (QC WAYPOINTFLAG_CROUCH) ----
        if ((goal.Flags & WaypointFlags.Crouch) != 0)
            WantCrouch = true;

        // ---- jump waypoint: the outgoing link requires a jump (QC WAYPOINTFLAG_JUMP) ----
        // Jump as we approach the jump-waypoint so the leap carries us along the link.
        if ((goal.Flags & WaypointFlags.Jump) != 0 && onGround && flat.Length() < 100f)
            WantJump = true;

        // ---- ladder waypoint: climb (QC WAYPOINTFLAG_LADDER) — bias the move upward and don't brake on the
        //      vertical gap, since a ladder lets us ascend without a jump.
        bool onLadder = (goal.Flags & WaypointFlags.Ladder) != 0;

        // ---- obstacle / step-up detection -> jump (QC tracebox jumpobstacle_check block) ----
        // Probe a little ahead at current and stepped-up heights; if a wall blocks at foot level but the
        // path opens up when raised by a jump's worth of height, jump.
        float speed = new Vector2(bot.Velocity.X, bot.Velocity.Y).Length();
        float reach = MathF.Max(32f, speed * 0.3f);
        Vector3 ahead = bot.Origin + flatdir * reach;

        var trFlat = Trace(bot, bot.Origin, ahead);
        if (trFlat.Fraction < 1f && trFlat.PlaneNormal.Z < 0.7f)
        {
            // Wall ahead. Can we walk up it as a step?
            var step = new Vector3(0f, 0f, StepHeight);
            var trStep = Trace(bot, bot.Origin + step, ahead + step);
            if (trStep.Fraction < trFlat.Fraction + 0.01f && trStep.PlaneNormal.Z < 0.7f)
            {
                // Still blocked at step height: try a full jump's height.
                var jh = new Vector3(0f, 0f, JumpStepHeight);
                var trJump = Trace(bot, bot.Origin + jh, ahead + jh);
                if (trJump.Fraction > trStep.Fraction && onGround)
                    WantJump = true;
            }
        }

        // ---- goal above us -> jump up onto it (unless on a ladder, where we just climb) ----
        if (!onLadder && onGround && diff.Z > StepHeight && flat.Length() < Maxs.X * 2f)
            WantJump = true;

        // ---- dangerous edge / fall ahead -> brake (QC do_break, simplified) ----
        // Skipped for jumppad/teleport/jump goals (we WANT to commit) and ladders (controlled descent).
        Vector3 brake = Vector3.Zero;
        bool committing = (goal.Flags & (WaypointFlags.Teleport | WaypointFlags.Jump)) != 0 || onLadder;
        if (!committing && onGround && diff.Z < -120f && flat.Length() < 250f)
        {
            // The goal is far below and not far ahead horizontally: there may be a ledge. Probe straight
            // down ahead of us; if the drop is large, slow down so we don't overrun a deadly edge.
            var downStart = bot.Origin + flatdir * 16f;
            var trDown = Trace(bot, downStart, downStart - new Vector3(0f, 0f, 400f));
            if (downStart.Z - trDown.EndPos.Z > 200f)
                brake = QMath.Normalize(bot.Velocity) * -1f;
        }

        Vector3 worldMove = QMath.Normalize(dir + brake);
        if (worldMove == Vector3.Zero)
            worldMove = dir;
        // On a ladder, bias the move strongly upward so the climb works (QC pushes +z on ladders).
        if (onLadder && diff.Z > 0f)
            worldMove = QMath.Normalize(worldMove + new Vector3(0f, 0f, 1f));

        // ---- project world direction into the bot's local move frame ----
        // Use yaw-only basis (QC makevectors(v_angle.y * '0 1 0')) so forward/side don't tilt with pitch.
        QMath.AngleVectors(new Vector3(0f, viewYaw, 0f), out var forward, out var right, out var up);
        float fwd = QMath.Dot(worldMove, forward);
        float side = QMath.Dot(worldMove, right);
        float vert = QMath.Dot(worldMove, up);

        // ---- bunnyhop tuning (QC havocbot_bunnyhop): keep jumping to maintain speed toward a far goal ----
        if (Bunnyhop(bot, dir, onGround, goal, attacking: false))
            WantJump = true;

        return new Vector3(fwd, side, vert) * MaxSpeed;
    }

    /// <summary>Bot skill (QC <c>skill</c>), set by the brain — gates whether the bot bunnyhops at all.</summary>
    public float Skill = 5f;

    /// <summary>
    /// QC <c>havocbot_bunnyhop</c>: decide whether to jump this frame to bunnyhop toward the goal. The bot
    /// bunnyhops only at/above the skill offset, when not attacking, already at/above run speed, on the
    /// ground, not crouched, out of deep water, and heading at the goal within the direction-deviation cone —
    /// and only when the remaining distance to the goal exceeds the jump distance (so it doesn't overshoot a
    /// near waypoint). Faithful to the QC gating including the jump-distance-vs-remaining check.
    /// </summary>
    private bool Bunnyhop(Entity bot, Vector3 dir, bool onGround, Goal goal, bool attacking)
    {
        // skill gate (QC: bunnyhopping bots are skill >= bot_ai_bunnyhop_skilloffset).
        float skillOffset = Cvars.FloatOr("bot_ai_bunnyhop_skilloffset", 6f);
        if (Skill < skillOffset)
            return false;
        if (attacking || !onGround)
            return false;
        if (WantCrouch || bot.WaterLevel > 1) // WATERLEVEL_WETFEET
            return false;
        // don't bunnyhop straight into a jump/teleport goal (we handle those explicitly).
        if ((goal.Flags & (WaypointFlags.Jump | WaypointFlags.Teleport)) != 0)
            return false;

        var vel2 = new Vector2(bot.Velocity.X, bot.Velocity.Y);
        float vel = vel2.Length();
        if (vel < MaxSpeed) // QC: must already be at/above run speed
            return false;

        // direction deviation cone (QC: angle between velocity and desired dir within the max).
        Vector3 velAngles = QMath.VecToAngles(new Vector3(bot.Velocity.X, bot.Velocity.Y, 0f));
        Vector3 dirAngles = QMath.VecToAngles(new Vector3(dir.X, dir.Y, 0f));
        float devY = WrapDeg(velAngles.Y - dirAngles.Y);
        float maxDev = Cvars.FloatOr("bot_ai_bunnyhop_dir_deviation_max", 10f);
        if (MathF.Abs(devY) >= maxDev)
            return false;

        // jump distance grows ~linearly with speed (QC formula); only hop if the goal is farther than that.
        Vector3 gco = goal.Pos;
        float jumpDistance = 52.661f + 0.606f * vel + (bot.Origin.Z - gco.Z);
        float remaining = new Vector2(gco.X - bot.Origin.X, gco.Y - bot.Origin.Y).Length();
        return remaining > MathF.Max(0f, jumpDistance);
    }

    private static float WrapDeg(float a)
    {
        while (a < -180f) a += 360f;
        while (a > 180f) a -= 360f;
        return a;
    }

    private static float Now => Api.Clock.Time;

    /// <summary>
    /// Have we reached this goal? (QC navigation_poptouchedgoals). A box waypoint (e.g. a teleporter trigger
    /// volume) counts as reached once the bot is inside the box footprint; a point waypoint uses the
    /// proximity radius. A teleport/jumppad goal also counts the moment we're inside its trigger so we let it
    /// fling us rather than overshooting.
    /// </summary>
    private bool ReachedGoal(Entity bot, Goal goal)
    {
        if (goal.Wp is { IsBox: true } wp)
        {
            Vector3 lo = wp.AbsMin, hi = wp.AbsMax;
            Vector3 o = bot.Origin;
            bool inside = o.X >= lo.X && o.X <= hi.X && o.Y >= lo.Y && o.Y <= hi.Y
                          && o.Z >= lo.Z - GoalReachedZ && o.Z <= hi.Z + GoalReachedZ;
            if (inside) return true;
        }
        return Reached(bot.Origin, goal.Pos);
    }

    /// <summary>Have we reached <paramref name="goal"/> from <paramref name="origin"/>? (QC poptouchedgoals proximity).</summary>
    private static bool Reached(Vector3 origin, Vector3 goal)
    {
        var d = goal - origin;
        float xy = new Vector2(d.X, d.Y).Length();
        return xy < GoalReachedXY && MathF.Abs(d.Z) < GoalReachedZ;
    }

    /// <summary>tracebox between two points using the bot's hull, ignoring the bot (QC tracebox MOVE_NOMONSTERS).</summary>
    private TraceResult Trace(Entity bot, Vector3 start, Vector3 end)
        => Api.Trace.Trace(start, Mins, Maxs, end, MoveFilter.NoMonsters, bot);

    /// <summary>
    /// Can the bot walk in a straight line from a to b? (QC tracewalk early-out in navigation_routetogoal).
    /// Uses the full <see cref="BotTracewalk"/> reachability test — stepping the hull along the path and
    /// handling stairs, ledges and water — rather than a single straight hull sweep, so a clear staircase or
    /// shallow ford counts as directly reachable (no waypoints needed).
    /// </summary>
    private bool CanWalkStraight(Vector3 a, Vector3 b)
        => BotTracewalk.CanWalk(a, b, Mins, Maxs);
}
