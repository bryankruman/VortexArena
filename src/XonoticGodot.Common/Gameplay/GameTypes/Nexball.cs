using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Nexball gametype — port of <c>CLASS(NexBall, Gametype)</c>
/// (common/gametypes/gametype/nexball/nexball.qh + sv_nexball.qc).
///
/// A team sport: players carry/shoot a ball and try to put it through the enemy team's goal. Putting the ball
/// in the <em>enemy</em> goal scores a point for the scorer's team (QC <c>GoalTouch</c> → pscore=+1,
/// TeamScore_AddToTeam(ST_NEXBALL_GOALS)); an own-goal or a "fault" goal costs a point (pscore=−1, credited
/// to the other team in a two-team game). The first team to the goal limit (cvar <c>g_nexball_goallimit</c>,
/// default 5) wins (QC GameRules_limit_score). It is a weapon-arena gametype (only the ball-launcher weapon).
///
/// Faithfully ported (the goal-scoring rule):
///  - per-team goal tally (<see cref="GoalsFor"/>, routed through the unified GameScores team store);
///  - goal resolution via <see cref="ScoreGoal"/> (own-goal/fault → −1 to other team; enemy goal → +1);
///  - goal-limit win check (<see cref="GoalLimit"/>, QC g_nexball_goallimit) and winning team.
///
/// Faithfully ported (objective layer):
///  - the ball entity (basketball/football) with carry/drop/reset (<see cref="SpawnBall"/>/
///    <see cref="GiveBall"/>/<see cref="DropBall"/>, QC GiveBall / DropBall / ResetBall);
///  - the goal trigger entities (team goals + GOAL_FAULT + GOAL_OUT) and the GoalTouch dispatch that derives
///    the goal kind and scores it (<see cref="SpawnGoal"/>/<see cref="GoalTouch"/>, QC GoalTouch pscore).
///
/// Deferred (NOTE — cross-boundary): the ball physics tuning (bounce factors, basketball meter), the ball-launcher arena
/// weapon, the ball/goal models + waypoint sprites (CSQC), and score networking — client or weapon concerns.
/// </summary>
[GameType]
public sealed class Nexball : GameType
{
    // ----- goal limit cvars + default (gametype default pointlimit=5) -----
    private const string CvarGoalLimit    = "g_nexball_goallimit";
    private const string CvarFragLimit    = "fraglimit"; // GameRules_limit_score writes the goal limit here
    private const int    DefaultGoalLimit = 5;           // gametype_init "pointlimit=5"

    // ----- ball-lifecycle / physics cvars + their gametypes-server.cfg defaults (QC autocvar_g_nexball_*) -----
    private const float DelayStart   = 3f;    // g_nexball_delay_start    — ball stands on spawn before release
    private const float DelayIdle    = 10f;   // g_nexball_delay_idle     — max idle time before a reset
    private const float DelayGoal    = 3f;    // g_nexball_delay_goal     — delay between a goal and the ball reset
    private const float DelayCollect = 0.5f;  // g_nexball_delay_collect  — relaunch self-recapture cooldown
    private const float DelayHold        = 20f; // g_nexball_basketball_delay_hold         — anti-ballcamp DropOwner
    private const float DelayHoldForTeam = 60f; // g_nexball_basketball_delay_hold_forteam — team-hold auto-return
    private const float FootballBoostForward = 100f; // g_nexball_football_boost_forward
    private const float FootballBoostUp      = 200f; // g_nexball_football_boost_up
    private const float FootballPhysics      = 2f;   // g_nexball_football_physics (2 = view-independent, the default)
    private const int   EffectsDefault       = 8;    // g_nexball_basketball_effects_default (EF_DIMLIGHT)

    /// <summary>How a goal was scored, mirroring QC's GoalTouch pscore branches.</summary>
    public enum GoalKind
    {
        /// <summary>Ball entered the enemy goal: +1 to the scorer's team (QC pscore=+1).</summary>
        Score,
        /// <summary>Ball entered the scorer's own goal: −1 (QC own-goal / GOAL_FAULT, pscore=−1).</summary>
        OwnGoal,
    }

    // Per-team goal counts (QC ST_NEXBALL_GOALS, team slot 1 — the TEAM primary) now live in the unified
    // GameScores two-slot team store — the source of truth (common/scores.qh). Read/written via GoalsFor /
    // GameScores.AddToTeam (slot 1).

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a kill (no nexball-specific scoring on kills).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a team reaches the goal limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The team color code (see <see cref="Teams"/>) that reached the goal limit, or 0 if none yet.</summary>
    public int WinningTeam { get; private set; }

    public Nexball()
    {
        NetName = "nb";
        DisplayName = "Nexball";
        TeamGame = true;
    }

    // ----- goal-kind sentinels stored in a goal entity's GtHomeTeam (QC GOAL_FAULT / GOAL_OUT) -----
    /// <summary>QC GOAL_FAULT: a goal volume that docks the ball team a point (or credits the other team).</summary>
    public const int GoalFault = -2;
    /// <summary>QC GOAL_OUT: an out-of-bounds volume that just returns the ball (no scoring).</summary>
    public const int GoalOut = -3;

    /// <summary>The ball entity (QC nexball_basketball / nexball_football), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    /// <summary>The team that currently "owns" the ball (QC ball.team) — set on pickup, used by GoalTouch.</summary>
    public int BallTeam { get; private set; } = Teams.None;

    /// <summary>The player carrying / who last touched the ball (QC ball.pusher), credited on goals.</summary>
    public Player? BallPusher { get; private set; }

    /// <summary>The goal trigger entities on the map (QC nexball_*goal / fault / out).</summary>
    public readonly List<Entity> Goals = new();

    // ----- per-ball QC state (kept on the gametype since there is one ball; QC stored these on the ball edict) -----
    /// <summary>QC ball.cnt: the ResetBall glide step (0/1 = active, 1 = goal-reset pending, 2..4 = glide-home steps).</summary>
    private int _ballCnt;
    /// <summary>QC ball.lifetime: absolute time a team-held ball auto-returns (delay_hold_forteam); 0 = none.</summary>
    private float _ballLifetime;
    /// <summary>QC ball.lastground: last world-bounce time, throttling the bounce sound (football_touch 0.1 s).</summary>
    private float _ballLastGround;
    /// <summary>QC ball.nb_dropper / ball.nb_droptime: the player who last launched the ball + when (delay_collect).</summary>
    private Player? _ballDropper;
    private float _ballDropTime;
    /// <summary>True iff the live ball is a basketball (NBM_BASKETBALL — carryable). False = football (kickable only).</summary>
    private bool _isBasketball = true;
    /// <summary>QC: the ball's think is InitBall (release) rather than ResetBall (glide) — set by SpawnBall and by
    /// ResetBall step 4, cleared once InitBall runs. Keeps ball.cnt faithful to QC (0 during the pre-release hold).</summary>
    private bool _releasePending;

    public override void OnInit()
    {
        // QC: the ball + goals are spawned from the map's nexball_* entities (see SpawnBall / SpawnGoal).
        // gametype_init flags (TEAMPLAY|USEPOINTS|WEAPONARENA) are the engine's job; OnInit clears state.
        BallEntity = null;
        BallTeam = Teams.None;
        BallPusher = null;
        Goals.Clear();
        _ballCnt = 0;
        _ballLifetime = 0f;
        _ballLastGround = 0f;
        _ballDropper = null;
        _ballDropTime = 0f;
        _isBasketball = true;
        _releasePending = false;
    }

    // ============================================================================================
    //  Ball ENTITY layer (QC SpawnBall / GiveBall / DropBall / ResetBall)
    // ============================================================================================

    /// <summary>
    /// QC SpawnBall (+ spawnfunc nexball_basketball/football): create the world ball entity at
    /// <paramref name="origin"/> through the shared <see cref="global::XonoticGodot.Common.Gameplay.BallEntity"/>
    /// framework so it gets the bbox / movetype / glow defaults, then install Nexball's own touch + the
    /// <see cref="BallThink"/> state machine (delay_start release → idle reset → 4-step glide-home). The ball
    /// stands on its spawn origin for <see cref="DelayStart"/> before InitBall releases it (QC nextthink =
    /// game_starttime + delay_start). <paramref name="basketball"/> selects the carryable basketball vs the
    /// kick-only football. Returns the entity (null when no facade is wired).
    /// </summary>
    public Entity? SpawnBall(Vector3 origin, bool basketball = true)
    {
        _isBasketball = basketball;
        var cfg = new BallConfig
        {
            Touch = basketball ? BasketballTouch : FootballTouch, // QC settouch(basketball_touch/football_touch)
            Think = BallThink,                                    // QC InitBall/ResetBall state machine (not the loose default)
            RespawnTime = DelayIdle,                              // QC g_nexball_delay_idle
            Effects = basketball ? EffectsDefault : 0,            // QC basketball_effects_default (EF_DIMLIGHT); football none
        };
        Entity? e = global::XonoticGodot.Common.Gameplay.BallEntity.SpawnForGametype(
            basketball ? BallKind.NexballBasketball : BallKind.NexballFootball, origin, cfg);

        BallEntity = e;
        BallTeam = Teams.None;
        BallPusher = null;
        _ballCnt = 0;            // QC ball.cnt default 0 (a ball can be caught during the pre-release hold)
        _releasePending = true;  // the FIRST BallThink runs InitBall (QC setthink(this, InitBall) in SpawnBall)
        _ballLifetime = 0f;
        BallHome = origin; // QC SpawnBall: this.spawnorigin = this.origin — ResetBall returns the ball here
        if (e is not null)
        {
            e.MoveType = MoveType.Fly; // QC SpawnBall: set_movetype(this, MOVETYPE_FLY) until release
            // QC SpawnBall: setthink(this, InitBall); nextthink = game_starttime + delay_start.
            e.Think = BallThink;
            e.NextThink = global::System.Math.Max(GametypeEntities.Now, GameStartTime) + DelayStart;
        }
        return e;
    }

    /// <summary>The ball's home/reset position (QC spawnorigin), defaults to the spawn origin.</summary>
    public Vector3 BallHome { get; set; }

    /// <summary>QC game_starttime: the absolute time the match goes live (the ball releases delay_start after it).
    /// The host sets this before SpawnBall; 0 = release immediately (the headless/test default).</summary>
    public float GameStartTime { get; set; }

    /// <summary>
    /// QC GiveBall: <paramref name="player"/> picks up the ball, becoming its carrier + team owner. Sets the
    /// team-hold auto-return lifetime when the catcher is on a DIFFERENT team than the last owner
    /// (delay_hold_forteam), arms the anti-ballcamp DropOwner timer (delay_hold), and stops/attaches the ball.
    /// The weapon-arena swap to WEP_NEXBALL + carrier light/waypoint are deferred (separate weapon/CSQC seams).
    /// </summary>
    public void GiveBall(Player player)
    {
        if (MatchEnded || player.IsDead)
            return;

        // QC GiveBall: if(ball.team != plyr.team) ball.lifetime = time + delay_hold_forteam.
        if (BallTeam != (int)player.Team)
            _ballLifetime = GametypeEntities.Now + DelayHoldForTeam;

        BallTeam = (int)player.Team; // QC ball.team = plyr.team
        BallPusher = player;          // QC ball.owner = ball.pusher = plyr (owner = carrier, pusher = last toucher)
        _ballCnt = 0;                 // QC ball gets caught — cnt stays clear (no reset pending)
        _ballDropper = player;        // QC ball.nb_dropper = plyr (delay_collect cooldown is keyed off the catcher)
        _ballDropTime = GametypeEntities.Now;

        if (BallEntity is Entity e)
        {
            // QC GiveBall: setorigin to plyr.origin + view_ofs, MOVETYPE_NONE, settouch func_null, scale down.
            global::XonoticGodot.Common.Gameplay.BallEntity.AttachToCarrier(e, player, player.ViewOfs);
            e.Team = BallTeam;
            e.Touch = null;          // QC settouch(ball, func_null) — held ball isn't picked up by touch
            e.Velocity = Vector3.Zero;
            e.Effects &= ~EffectsDefault; // QC ball.effects &= ~effects_default (the glow moves to the carrier, client)

            // QC GiveBall: if(delay_hold) setthink(ball, DropOwner); nextthink = time + delay_hold.
            if (DelayHold > 0f)
            {
                e.Think = DropOwnerThink;
                e.NextThink = GametypeEntities.Now + DelayHold;
            }
        }
        // QC GiveBall weapon-arena swap (STAT(WEAPONS)=WEPSET(NEXBALL), W_SwitchWeapon(WEP_NEXBALL)) — deferred:
        // the BallStealer weapon does not exist in the port yet (see nexball.weapon.ballstealer todo).
    }

    /// <summary>
    /// QC DropBall: the carrier loses the ball; it drops where they stood inheriting <paramref name="vel"/>,
    /// keeping the last-toucher team. Restores bounce + the pickup touch, arms the idle-reset think
    /// (delay_idle, capped by the team-hold lifetime), and records the dropper for the delay_collect cooldown.
    /// </summary>
    public void DropBall(Vector3 org, Vector3 vel)
    {
        Player? carrier = BallEntity?.GtCarrier as Player ?? BallPusher;
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;     // QC set_movetype(ball, MOVETYPE_BOUNCE)
            e.Flags &= ~EntFlags.OnGround;     // QC UNSET_ONGROUND(ball)
            GametypeEntities.SetOrigin(e, org);
            e.Velocity = vel;                  // QC ball.velocity = vel (carrier velocity inherited)
            e.Effects |= EffectsDefault;       // QC ball.effects |= effects_default
            e.Touch = _isBasketball ? BasketballTouch : FootballTouch; // QC settouch(ball, basketball_touch)
            e.Think = BallThink;               // QC setthink(ball, ResetBall)
            // QC nextthink = min(time + delay_idle, ball.lifetime).
            e.NextThink = MinIdle(GametypeEntities.Now + DelayIdle);
        }
        _ballDropTime = GametypeEntities.Now;  // QC ball.nb_droptime = time
        if (carrier is not null)
            _ballDropper = carrier;            // QC ball.nb_dropper stays the launching player (delay_collect)
        // QC keeps ball.team + ball.pusher after a drop (the last toucher still "owns" it for goal scoring).
    }

    /// <summary>QC nb_DropBall(player) / the PlayerDies+ClientDisconnect+MakePlayerObserver hooks: the carrier's
    /// ball drops where they stand with their velocity. No-op if the player isn't carrying this ball.</summary>
    public void DropBall(Player carrier)
    {
        if (BallEntity is null || !ReferenceEquals(carrier.GtCarried, BallEntity))
            return;
        DropBall(carrier.Origin, carrier.Velocity);
    }

    /// <summary>QC DropOwner (the anti-ballcamp think): force the held ball loose and shove the camping carrier
    /// up-and-back so a basketball can't be hoarded past delay_hold.</summary>
    public void DropOwner()
    {
        Player? ownr = BallEntity?.GtCarrier as Player;
        if (ownr is null)
            return;
        DropBall(ownr.Origin, ownr.Velocity); // QC DropBall(this, ownr.origin, ownr.velocity)
        // QC: makevectors(ownr.v_angle.y * '0 1 0'); ownr.velocity += ('0 0 0.75' - v_forward) * 1000.
        Vector3 fwd = QMath.Forward(new Vector3(0f, ownr.VAngle.Y, 0f));
        ownr.Velocity += (new Vector3(0f, 0f, 0.75f) - fwd) * 1000f;
        ownr.Flags &= ~EntFlags.OnGround;     // QC UNSET_ONGROUND(ownr)
    }

    private void DropOwnerThink(Entity self) => DropOwner();

    /// <summary>
    /// QC ResetBall (the 4-step think state machine): return the ball home and clear team ownership. cnt&lt;2 stops
    /// it and noclips (step 1); cnt 2/3 glides it toward the spawn origin (step 2/3, 0.5 s each); cnt 4 snaps it
    /// home, re-arms InitBall after delay_start, and clears ownership (step 4). The instantaneous form is the
    /// cnt&gt;=4 leg; the host's BallThink drives the intermediate steps each think tick.
    /// </summary>
    public void ResetBall()
    {
        if (BallEntity is not Entity e)
        {
            BallTeam = Teams.None;
            BallPusher = null;
            return;
        }

        if (_ballCnt < 2) // QC step 1: stop, go noclip, schedule the glide
        {
            e.Touch = null;                    // QC settouch(this, func_null)
            e.MoveType = MoveType.Noclip;      // QC set_movetype(this, MOVETYPE_NOCLIP)
            e.Velocity = Vector3.Zero;
            _ballCnt = 2;
            e.NextThink = GametypeEntities.Now; // QC nextthink = time (run step 2 next tick)
        }
        else if (_ballCnt < 4) // QC steps 2 & 3: velocity-glide back toward spawnorigin over 0.5 s
        {
            // QC velocity = (spawnorigin - origin) * (cnt - 1) — 1.0 then 0.5 second movement.
            e.Velocity = (BallHome - e.Origin) * (_ballCnt - 1);
            e.NextThink = GametypeEntities.Now + 0.5f;
            _ballCnt += 1;
        }
        else // QC step 4: snap home, re-arm InitBall after delay_start, clear ownership
        {
            e.Velocity = Vector3.Zero;
            GametypeEntities.SetOrigin(e, BallHome); // QC setorigin(this, spawnorigin)
            e.MoveType = MoveType.None;              // QC set_movetype(this, MOVETYPE_NONE)
            e.Think = BallThink;                     // QC setthink(this, InitBall)
            e.NextThink = global::System.Math.Max(GametypeEntities.Now, GameStartTime) + DelayStart;
            _ballCnt = 0;                            // glide complete; the next BallThink runs InitBall (release)
            _releasePending = true;
            BallTeam = Teams.None;
            BallPusher = null;
            _ballLifetime = 0f;
        }
    }

    /// <summary>
    /// QC InitBall: release the ball into play after the delay_start hold — restore bounce + the mode touch, clear
    /// the reset step, arm the idle-reset think (delay_idle), play the drop sound (noise1), and clear ownership.
    /// </summary>
    public void InitBall()
    {
        if (BallEntity is not Entity e)
            return;
        _releasePending = false;                // released — subsequent thinks run ResetBall (the glide)
        e.Flags &= ~EntFlags.OnGround;          // QC UNSET_ONGROUND(this)
        e.MoveType = MoveType.Bounce;           // QC set_movetype(this, MOVETYPE_BOUNCE)
        e.Touch = _isBasketball ? BasketballTouch : FootballTouch;
        _ballCnt = 0;                           // QC this.cnt = 0
        e.Think = BallThink;                    // QC setthink(this, ResetBall)
        e.NextThink = GametypeEntities.Now + DelayIdle + 3f; // QC nextthink = time + delay_idle + 3
        _ballLifetime = 0f;                     // QC this.lifetime = 0
        BallPusher = null;                      // QC this.pusher = NULL
        BallTeam = Teams.None;                  // QC this.team = false
        SoundSystem.PlayOn(e, Sounds.ByName("NB_DROP"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm); // QC _sound(this, CH_TRIGGER, this.noise1, ...)
    }

    /// <summary>
    /// The ball's single Think trampoline (QC the InitBall/ResetBall function pointers). A carried ball with the
    /// DropOwner timer armed is handled separately (DropOwnerThink). Dispatches InitBall when the reset state
    /// machine has completed (cnt == -1, i.e. "release pending"), else advances ResetBall.
    /// </summary>
    private void BallThink(Entity self)
    {
        if (!ReferenceEquals(self, BallEntity))
            return;
        if (_releasePending) // QC setthink(InitBall) — release the ball into play after the delay_start hold
            InitBall();
        else
            ResetBall();
    }

    /// <summary>QC min(time + delay_idle, ball.lifetime): the idle-reset deadline, capped by the team-hold lifetime
    /// when one is armed (lifetime 0 = uncapped).</summary>
    private float MinIdle(float idleDeadline)
        => _ballLifetime > 0f ? global::System.Math.Min(idleDeadline, _ballLifetime) : idleDeadline;

    /// <summary>
    /// QC basketball_touch: a carryable ball. A toucher who already carries a ball falls through to
    /// <see cref="FootballTouch"/> (bump-as-soccer); otherwise a live player who isn't on the delay_collect
    /// cooldown and the reset isn't pending (cnt==0) catches it; a world touch plays the bounce sfx + re-arms idle.
    /// </summary>
    private void BasketballTouch(Entity self, Entity other)
    {
        // QC: if(toucher.ballcarried) { football_touch(this, toucher); return; } — a toucher who already carries a
        // ball doesn't catch this one, it bumps it like a football (bump-as-soccer).
        if (other.GtCarried is not null)
        {
            FootballTouch(self, other);
            return;
        }

        // QC: if(!this.cnt && IS_PLAYER && !IS_DEAD && (toucher != nb_dropper || time > nb_droptime + delay_collect))
        if (_ballCnt == 0 && other is Player p && !p.IsDead
            && (!ReferenceEquals(p, _ballDropper) || GametypeEntities.Now > _ballDropTime + DelayCollect))
        {
            GiveBall(p); // QC GiveBall(toucher, this)
            return;
        }

        // QC: else if(toucher.solid == SOLID_BSP) — a world bounce: sfx + re-arm idle.
        if (other.Solid == Solid.Bsp)
        {
            SoundSystem.PlayOn(self, Sounds.ByName("NB_BOUNCE"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
            if (self.Velocity != Vector3.Zero && _ballCnt == 0)
                self.NextThink = MinIdle(GametypeEntities.Now + DelayIdle);
        }
    }

    /// <summary>
    /// QC football_touch: a kickable (soccer) ball. A world bounce plays the bounce sfx (throttled 0.1 s) + re-arms
    /// idle; a live player/vehicle touch becomes the new pusher and is given a velocity boost (boost_forward/up via
    /// the view-independent physics mode 2) plus a backspin avelocity, so the ball can be driven toward a goal.
    /// </summary>
    private void FootballTouch(Entity self, Entity other)
    {
        if (other.Solid == Solid.Bsp)
        {
            // QC: world bounce — throttle the bounce sound to once per 0.1 s, re-arm idle.
            if (GametypeEntities.Now > _ballLastGround + 0.1f)
            {
                SoundSystem.PlayOn(self, Sounds.ByName("NB_BOUNCE"), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNorm);
                _ballLastGround = GametypeEntities.Now;
            }
            if (self.Velocity != Vector3.Zero && _ballCnt == 0)
                self.NextThink = GametypeEntities.Now + DelayIdle;
            return;
        }

        if (other is not Player kicker || kicker.IsDead)
            return; // QC: !IS_PLAYER && !IS_VEHICLE, or RES_HEALTH < 1

        if (_ballCnt == 0)
            self.NextThink = GametypeEntities.Now + DelayIdle;

        BallPusher = kicker;            // QC this.pusher = toucher
        BallTeam = (int)kicker.Team;    // QC this.team = toucher.team

        // QC physics mode 2 (the default, fully view-independent): use only the kicker's YAW.
        // makevectors(toucher.v_angle.y * '0 1 0'); velocity = toucher.velocity + v_forward*boost_forward + v_up*boost_up.
        Vector3 angles = FootballPhysics == 2f ? new Vector3(0f, kicker.VAngle.Y, 0f) : kicker.VAngle;
        QMath.AngleVectors(angles, out Vector3 fwd, out _, out Vector3 up);
        if (FootballPhysics == -1f)
        {
            // QC mode -1: velocity = toucher.velocity * 1.5 + '0 0 1'*boost_up (only when the kicker is moving).
            if (kicker.Velocity != Vector3.Zero)
                self.Velocity = kicker.Velocity * 1.5f + new Vector3(0f, 0f, 1f) * FootballBoostUp;
        }
        else if (FootballPhysics == 2f)
        {
            self.Velocity = kicker.Velocity + fwd * FootballBoostForward + up * FootballBoostUp;
        }
        else // QC modes 0 (Revenant) and 1: full view angles, v_forward + '0 0 1'/v_up boost.
        {
            self.Velocity = kicker.Velocity + fwd * FootballBoostForward + up * FootballBoostUp;
        }
        self.AVelocity = fwd * -250f; // QC this.avelocity = -250 * v_forward (backspin)
    }

    // ============================================================================================
    //  Goal ENTITIES (QC nexball_*goal / nexball_fault / nexball_out + GoalTouch)
    // ============================================================================================

    /// <summary>
    /// QC SpawnGoal (nexball_redgoal/.../fault/out): create a goal trigger volume owned by
    /// <paramref name="goalTeam"/> (a team color, or <see cref="GoalFault"/>/<see cref="GoalOut"/>). Touch
    /// dispatches scoring via <see cref="GoalTouch"/>.
    /// </summary>
    public Entity? SpawnGoal(int goalTeam, Vector3 origin, Vector3 mins = default, Vector3 maxs = default)
    {
        if (maxs == default) { mins = new Vector3(-64f, -64f, -64f); maxs = new Vector3(64f, 64f, 64f); }
        Entity? e = GametypeEntities.SpawnObjective("nexball_goal", origin, Teams.None, mins, maxs,
            touch: GoalTouchEntity);
        if (e is not null)
        {
            e.Flags = EntFlags.None;
            e.GtHomeTeam = goalTeam; // the goal's "team"/kind sentinel
            Goals.Add(e);
        }
        return e;
    }

    /// <summary>
    /// QC GoalTouch: the ball entered goal <paramref name="goalEnt"/>. Derive the goal kind from the goal's
    /// team vs the ball's team — own-goal/fault → −1 (credited to the other team in 2-team play), enemy goal
    /// → +1, out → no score — apply it via <see cref="ScoreGoal"/>, play the goal sound, then arm the delayed
    /// ball reset (delay_goal; 0 for OUT). A scored basketball drops its carrier and is converted to football
    /// control (cnt=1 + football_touch) for the duration of the reset delay so the ball physically falls through.
    /// </summary>
    public void GoalTouch(Entity goalEnt)
    {
        if (MatchEnded)
            return; // QC: game_stopped guard
        int goalTeam = goalEnt.GtHomeTeam;

        // QC: if((!ball.pusher && this.team != GOAL_OUT) || ball.cnt) return; — no scorer (except OUT) or a ball
        // already mid-reset can't score.
        if ((BallTeam == Teams.None && goalTeam != GoalOut) || _ballCnt != 0)
            return;

        int otherTeam = TeamCountIsTwo() && BallTeam != Teams.None ? OtherTeam(BallTeam) : Teams.None;

        if (goalTeam == GoalOut)
        {
            // QC GOAL_OUT: pscore = 0 — no score, the ball is just returned (delay 0).
        }
        else if (goalTeam == GoalFault || goalTeam == BallTeam)
        {
            // QC fault or own-goal: pscore = -1.
            ScoreGoal(BallTeam, GoalKind.OwnGoal, otherTeam);
        }
        else
        {
            // QC enemy goal: pscore = +1 for the ball's team.
            ScoreGoal(BallTeam, GoalKind.Score, otherTeam);
        }

        if (BallEntity is Entity ball)
        {
            // QC: _sound(ball, CH_TRIGGER, this.noise, VOL_BASE, ATTEN_NONE) — the goal's noise (ctf/respawn for a
            // score, TYPEHIT for fault/out). The goal entity carries no per-goal noise field in the port, so pick
            // the faithful default by goal kind.
            string goalSound = goalTeam is GoalFault or GoalOut ? "TYPEHIT" : "KH_CAPTURE"; // ctf/respawn ≈ capture cue
            SoundSystem.PlayOn(ball, Sounds.ByName(goalSound), SoundChannel.TriggerAuto, SoundLevels.VolBase, SoundLevels.AttenNone);

            // QC GOAL_TOUCHPLAYER (ball.owner): a carrier scored directly — drop the ball where they stand.
            if (ball.GtCarrier is Player owner)
                DropBall(owner.Origin, owner.Velocity);

            // QC: ball.cnt = 1; setthink(ResetBall); for a basketball settouch(football_touch) (football control
            // until reset); nextthink = time + delay_goal * (this.team != GOAL_OUT) — OUT resets next tick.
            _ballCnt = 1;
            ball.Think = BallThink;
            if (_isBasketball)
                ball.Touch = FootballTouch;
            ball.NextThink = GametypeEntities.Now + DelayGoal * (goalTeam != GoalOut ? 1f : 0f);
        }

        // The goal is scored and the reset is armed; ownership is logically cleared now (the ball.cnt guard above
        // prevents a second score before the glide-home think actually returns the ball). This keeps BallTeam/
        // BallPusher consistent with "no live ball in play" immediately, matching the headless reset contract.
        BallTeam = Teams.None;
        BallPusher = null;
        _ballLifetime = 0f;
    }

    private void GoalTouchEntity(Entity self, Entity other)
    {
        // The ball (or a ball-carrier) hit the goal.
        if (ReferenceEquals(other, BallEntity) || (other is Player p && ReferenceEquals(p.GtCarried, BallEntity)))
            GoalTouch(self);
    }

    /// <summary>QC OtherTeam (two-team only): the opposing team color.</summary>
    private static int OtherTeam(int team) => team == Teams.Red ? Teams.Blue : Teams.Red;

    private static bool TeamCountIsTwo() => true; // Nexball is two-team by default (AVAILABLE_TEAMS == 2)

    /// <summary>The goal limit in force (g_nexball_goallimit, else fraglimit, else 5). 0 means no limit.</summary>
    public int GoalLimit
    {
        get
        {
            if (TryCvar(CvarGoalLimit, out float gl)) return (int)gl;
            if (TryCvar(CvarFragLimit, out float fl)) return (int)fl;
            return DefaultGoalLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        WinningTeam = 0;
        GS.ResetTeams(); // QC Score_ClearAll at match start: zero both team slots before declaring

        // QC nb_ScoreRules (sv_nexball.qc) GameRules_scoring(teams, 0, 0, { field_team(ST_NEXBALL_GOALS, "goals",
        // PRIMARY); field(SP_NEXBALL_GOALS, "goals", PRIMARY); field(SP_NEXBALL_FAULTS, "faults", SECONDARY |
        // LOWER_IS_BETTER); }): BOTH SP_SCORE (spprio=0) and ST_SCORE (stprio=0) are non-primary; the player
        // primary is SP_NEXBALL_GOALS (secondary FAULTS, fewer is better); the TEAM primary is ST_NEXBALL_GOALS
        // (slot 1, goal totals).
        GS.ScoreRulesBasics(teams: true);
        GS.TeamRulesBasics(scorePrio: Scoring.ScoreFlags.None); // ST_SCORE (slot 0) stprio = 0 (non-primary)
        GS.SetTeamLabel(GS.TeamSlotSecondary, "goals", Scoring.ScoreFlags.SortPrioPrimary); // ST_NEXBALL_GOALS (slot 1)
        GS.DeclareColumn("NEXBALL_GOALS", Scoring.ScoreFlags.None, "goals");
        GS.DeclareColumn("NEXBALL_FAULTS", Scoring.ScoreFlags.LowerIsBetter, "faults"); // QC: SECONDARY | LOWER_IS_BETTER
        GS.SetSortKeys(GS.Field("NEXBALL_GOALS")!, GS.Field("NEXBALL_FAULTS"));
        GS.SeedTeams(2); // Nexball is two-team (red vs blue; AVAILABLE_TEAMS == 2)

        // QC nb_Initialize: round the basketball meter period to 1/32 s (sent as a byte ×32), min 2 if <= 0. The
        // power-meter weapon doesn't exist in the port yet, but we compute the value so the future weapon/HUD seam
        // reads the bit-faithful period (see nexball.weapon.power_meter / nexball.hud.modicon todos).
        float meter = GametypeEntities.Cvar("g_nexball_meter_period", 1f);
        if (meter <= 0f) meter = 2f;
        MeterPeriod = global::System.MathF.Round(meter * 32f) / 32f;

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>QC g_nexball_meter_period (nb_Initialize): the rounded basketball power-meter cycle in seconds
    /// (rounded to 1/32 s, min 2 when the cvar is &lt;= 0). Read by the (deferred) power-meter weapon + HUD bar.</summary>
    public float MeterPeriod { get; private set; } = 1f;

    public override void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>The current goal count for a team color code (QC teamscores(team, ST_NEXBALL_GOALS); 0 if none).</summary>
    public int GoalsFor(int team) => GS.TeamScore(team, GS.TeamSlotSecondary);

    /// <summary>QC team equality (server/scores.qc:500): the top two teams are tied on goals
    /// (ST_NEXBALL_GOALS, Nexball's ranking primary), so a tied timed Nexball enters overtime instead of
    /// drawing (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster)
        => TeamTie.TopTwoTied(GS.LeaderTeam(), GS.SecondTeam(), GoalsFor);

    /// <summary>
    /// Resolve a ball-into-goal event (QC GoalTouch). For a normal <see cref="GoalKind.Score"/> the scorer's
    /// team gains a point; for an <see cref="GoalKind.OwnGoal"/> a point is taken away — in a two-team game by
    /// crediting the <paramref name="otherTeam"/> instead (QC: pscore&lt;0 ⇒ TeamScore_AddToTeam(otherteam)),
    /// otherwise by docking the scorer's own team. Then re-check the goal limit. The ball/goal geometry that
    /// decides which branch applies is deferred; the host calls this when it detects a goal.
    /// </summary>
    /// <param name="scorerTeam">The team color code of the player who last touched the ball (QC ball.team).</param>
    /// <param name="otherTeam">The opposing team in a two-team game, or 0 if not applicable.</param>
    public void ScoreGoal(int scorerTeam, GoalKind kind, int otherTeam = 0)
    {
        if (MatchEnded || scorerTeam == Teams.None)
            return;

        if (kind == GoalKind.Score)
        {
            AddTeamGoal(scorerTeam, +1);
            // QC GoalTouch: pscore > 0 → GameRules_scoring_add(ball.pusher, NEXBALL_GOALS, pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_GOALS", 1);
        }
        else // OwnGoal / fault: −1
        {
            if (otherTeam != Teams.None)
                AddTeamGoal(otherTeam, +1);   // QC two-team: the point goes to the other team
            else
                AddTeamGoal(scorerTeam, -1);  // otherwise dock the scorer's team
            // QC GoalTouch: pscore < 0 → GameRules_scoring_add(ball.pusher, NEXBALL_FAULTS, -pscore).
            if (BallPusher is not null) AddCol(BallPusher, "NEXBALL_FAULTS", 1);
        }

        CheckGoalLimit();
    }

    private void AddTeamGoal(int team, int delta)
        => GS.AddToTeam(team, GS.TeamSlotSecondary, delta); // QC TeamScore_AddToTeam(team, ST_NEXBALL_GOALS, delta)

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Nexball player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    /// <summary>QC GameRules_limit_lead(g_nexball_goalleadlimit): the goal lead at which the match ends early.
    /// The cvar default is -1 = "use the mapinfo leadlimit" (0 = disabled); &lt;= 0 disables it.</summary>
    public int GoalLeadLimit => TryCvar("g_nexball_goalleadlimit", out float ll) && ll > 0f ? (int)ll : 0;

    /// <summary>QC GameRules_limit_score + GameRules_limit_lead: latch the match once a team reaches the goal limit,
    /// or once the leader is ahead of the runner-up by at least the lead limit.</summary>
    public void CheckGoalLimit()
    {
        if (MatchEnded)
            return;

        // QC GameRules_limit_score: the leading team (the flag-aware ST_NEXBALL_GOALS leader) reaching the limit wins.
        int leader = GS.LeaderTeam();
        if (leader == Teams.None)
            return;

        int limit = GoalLimit;
        if (limit > 0 && GoalsFor(leader) >= limit)
        {
            MatchEnded = true;
            WinningTeam = leader;
            return;
        }

        // QC GameRules_limit_lead: end when the leader's margin over the runner-up reaches the lead limit.
        int lead = GoalLeadLimit;
        if (lead > 0)
        {
            int second = GS.SecondTeam();
            int margin = GoalsFor(leader) - (second != Teams.None ? GoalsFor(second) : 0);
            if (margin >= lead)
            {
                MatchEnded = true;
                WinningTeam = leader;
            }
        }
    }

    /// <summary>Kills don't score in Nexball (the ball does); a carrier who dies drops the ball.</summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        // QC nb (PlayerDies / DropSpecialItems hooks → nb_DropBall): a carrier who dies drops the ball where they
        // fell, inheriting their velocity.
        DropBall(victim);
        Events?.OnFrag(ev.Attacker as Player, victim, ev.DeathType);
        return false;
    }

    private static bool TryCvar(string name, out float value)
    {
        value = 0f;
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString(name);
        if (string.IsNullOrEmpty(s))
            return false;
        value = Api.Cvars.GetFloat(name);
        return true;
    }
}
