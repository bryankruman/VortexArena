using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using GS = XonoticGodot.Common.Gameplay.Scoring.GameScores; // T7: alias the static score table for the per-mode ScoreRules

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Keepaway — port of <c>CLASS(Keepaway, Gametype)</c>
/// (common/gametypes/gametype/keepaway/{keepaway.qh,sv_keepaway.qc}). A single ball sits on the map;
/// hold it to score. The carrier earns points over time (g_keepaway_score_timepoints) and a bonus per
/// kill made while carrying (g_keepaway_score_killac); killing the carrier earns the killer a bonus
/// (g_keepaway_score_bckill). First player to the point limit (g_keepaway_point_limit, default 30) wins.
///
/// NOTE on TeamGame: in Xonotic, Keepaway is a FREE-FOR-ALL objective mode — its gametype flags are
/// <c>GAMETYPE_FLAG_USEPOINTS</c> only, with NO <c>GAMETYPE_FLAG_TEAMPLAY</c> (see keepaway.qh
/// gametype_init "timelimit=20 pointlimit=30", no teams= arg). We therefore set <see cref="TeamGame"/> =
/// false to stay faithful to the QC source. (The team variant is a SEPARATE gametype, "tka" /
/// TeamKeepaway, common/gametypes/gametype/tka — out of scope here.) The brief grouped Keepaway with the
/// team modes; the faithful port keeps it FFA, with per-player scoring rather than per-team.
///
/// QC defaults: "timelimit=20 pointlimit=30".
///
/// Faithfully ported (Godot-free essence):
///  - a single ball with a carrier (<see cref="BallState"/>, QC .ballcarried / keepawayball entity);
///  - pickup / drop (<see cref="PickUp"/>/<see cref="Drop"/>, QC ka_TouchEvent / ka_DropEvent);
///  - per-frame time scoring for the carrier (<see cref="Tick"/>, QC ka_BallThink_Carried →
///    g_keepaway_score_timepoints * frametime) and the kill bonuses on the obituary bus
///    (carrier-kill bonus to the killer, kill-while-carrying bonus to the carrier);
///  - point-limit win condition (pointlimit).
///
/// Faithfully ported (objective layer):
///  - a single world ball entity spawned at map start (<see cref="SpawnBall"/>, QC ka_SpawnBalls) with
///    touch = pickup and a respawn timer when loose (<see cref="RespawnBall"/>, QC ka_RespawnBall);
///  - pickup attaches the ball to the carrier, drop detaches and re-arms the respawn (<see cref="PickUp"/>/
///    <see cref="Drop"/>, QC ka_TouchEvent / ka_DropEvent);
///  - the ballcarrier damage/force scaling matrix (<see cref="DamageScale"/>, QC Damage_Calculate
///    ballcarrier/noncarrier damage/force).
///
/// Deferred (NOTE — cross-boundary): the ball model/effects/waypoints (CSQC), multiple-ball chaining
/// (g_keepaway_ballcarrier_maxballs), and the carrier highspeed PlayerPhysics modifier.
/// </summary>
[GameType]
public sealed class Keepaway : GameType
{
    // ----- point-limit cvars + default (g_keepaway_point_limit; gametype_init pointlimit=30) -----
    private const string CvarPointLimitKa = "g_keepaway_point_limit";
    private const string CvarPointLimit   = "fraglimit";
    private const float  DefaultPointLimit = 30f;

    // ----- score cvars + defaults (g_keepaway_score_*) -----
    private const string CvarScoreTimePoints = "g_keepaway_score_timepoints"; // points/sec while carrying
    private const string CvarScoreKillAc     = "g_keepaway_score_killac";     // bonus per kill while carrying
    private const string CvarScoreBcKill     = "g_keepaway_score_bckill";     // bonus for killing the carrier
    private const float  DefaultScoreTimePoints = 0f; // QC default off (time scoring opt-in)
    private const float  DefaultScoreKillAc     = 0f;
    private const float  DefaultScoreBcKill     = 0f;

    /// <summary>The (single) ball (QC keepawayball / g_kaballs[0]).</summary>
    public readonly BallState Ball = new();

    /// <summary>Time-points remainder accumulator (QC float2int_decimal_fld) so fractional points carry over.</summary>
    private float _timePointsRemainder;

    public bool MatchEnded { get; private set; }

    /// <summary>The current points leader (highest <see cref="Player.ScoreFrags"/>), or null.</summary>
    public Player? Leader { get; private set; }

    private HookHandler<DeathEvent>? _deathHandler;

    public Keepaway()
    {
        NetName = "ka";
        DisplayName = "Keepaway";
        TeamGame = false; // QC: Keepaway is FFA (no GAMETYPE_FLAG_TEAMPLAY). See class remarks.
    }

    /// <summary>The single ball's world entity (QC keepawayball edict), or null (headless).</summary>
    public Entity? BallEntity { get; private set; }

    // ----- ball respawn + damage-scaling cvars (g_keepaway*) -----
    private const string CvarRespawnTime = "g_keepawayball_respawntime"; // loose-ball relocate timer

    public override void OnInit()
    {
        // QC ka_SpawnBalls: one ball is spawned at map start (see SpawnBall). The host calls SpawnBall once
        // the map is loaded; OnInit clears the ball reference. GameRules USEPOINTS is the engine's job.
        BallEntity = null;
        Ball.Carrier = null;
    }

    /// <summary>QC g_keepawayball_respawntime: seconds a loose ball waits before relocating itself.</summary>
    public float RespawnTime => TryCvar(CvarRespawnTime, out float v) && v > 0f ? v : 10f;

    /// <summary>Keepaway is free-for-all; there are no teams (a single ball, individual scoring).</summary>
    public int TeamCount => 0;

    /// <summary>Point limit in force (g_keepaway_point_limit, else fraglimit, else 30). 0 == unlimited.</summary>
    public float PointLimit
    {
        get
        {
            if (TryCvar(CvarPointLimitKa, out float pl)) return pl;
            if (TryCvar(CvarPointLimit, out float fl)) return fl;
            return DefaultPointLimit;
        }
    }

    public float ScoreTimePoints => TryCvar(CvarScoreTimePoints, out float v) ? v : DefaultScoreTimePoints;
    public float ScoreKillAc     => TryCvar(CvarScoreKillAc,     out float v) ? v : DefaultScoreKillAc;
    public float ScoreBcKill     => TryCvar(CvarScoreBcKill,     out float v) ? v : DefaultScoreBcKill;

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Leader = null;
        _timePointsRemainder = 0f;
        _bcTimeRemainder = 0f;
        Ball.Carrier = null;

        // QC sv_keepaway.qh GameRules_scoring(0, SFL_SORT_PRIO_PRIMARY, 0, { field(SP_KEEPAWAY_PICKUPS, "pickups",
        // 0); field(SP_KEEPAWAY_CARRIERKILLS, "bckills", 0); field(SP_KEEPAWAY_BCTIME, "bctime", SECONDARY); }):
        // SP_SCORE is the player primary (time-points + kill bonuses), SP_KEEPAWAY_BCTIME the secondary.
        GS.ScoreRulesBasics(teams: false);
        GS.DeclareColumn("KEEPAWAY_PICKUPS", Scoring.ScoreFlags.None, "pickups");
        GS.DeclareColumn("KEEPAWAY_CARRIERKILLS", Scoring.ScoreFlags.None, "bckills");
        GS.DeclareColumn("KEEPAWAY_BCTIME", Scoring.ScoreFlags.None, "bctime");
        GS.SetSortKeys(GS.Score, GS.Field("KEEPAWAY_BCTIME"));

        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>Ball-carry-time remainder accumulator (QC SP_KEEPAWAY_BCTIME += frametime), whole seconds banked.</summary>
    private float _bcTimeRemainder;

    /// <summary>QC <c>GameRules_scoring_add(player, SP_X, n)</c> for a Keepaway player column (no-op if unregistered).</summary>
    private static void AddCol(Player p, string field, int n)
    {
        Scoring.ScoreField? f = Scoring.GameScores.Field(field);
        if (f is not null) Scoring.GameScores.AddToPlayer(p, f, n);
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// QC ka_TouchEvent: <paramref name="player"/> picks up the ball. No-op if already carried or the
    /// player is dead. Sets the carrier. Returns true if the pickup happened.
    /// </summary>
    public bool PickUp(Player player)
    {
        if (MatchEnded || player.IsDead || Ball.Carrier is not null)
            return false;
        Ball.Carrier = player;
        Ball.PickupTime = Api.Services is not null ? Api.Clock.Time : 0f;
        AddCol(player, "KEEPAWAY_PICKUPS", 1); // QC ka_TouchEvent: GameRules_scoring_add(toucher, KEEPAWAY_PICKUPS, 1)
        // Attach the ball entity to the carrier (QC ka_TouchEvent setattachment + SOLID_NOT).
        if (BallEntity is Entity e)
        {
            GametypeEntities.AttachToCarrier(e, player, Vector3.Zero);
            e.NextThink = 0f; // carried ball has no respawn think
        }
        return true;
    }

    /// <summary>QC ka_DropEvent: the carrier loses the ball (on death, use-key, or disconnect).</summary>
    public void Drop()
    {
        Player? carrier = Ball.Carrier;
        Ball.Carrier = null;
        // Detach the ball entity, drop it where the carrier stood, and re-arm the relocate timer (QC ka_DropEvent).
        if (BallEntity is Entity e)
        {
            GametypeEntities.DetachFromCarrier(e);
            e.Solid = Solid.Trigger;
            e.MoveType = MoveType.Bounce;
            e.TakeDamage = DamageMode.Yes;
            if (carrier is not null)
                GametypeEntities.SetOrigin(e, carrier.Origin + new Vector3(0f, 0f, 10f));
            e.Velocity = new Vector3(0f, 0f, 200f);
            e.Think = RespawnBallThink;
            e.NextThink = GametypeEntities.Now + RespawnTime;
        }
    }

    // ============================================================================================
    //  Ball ENTITY layer (QC ka_SpawnBalls / ka_RespawnBall / ka_TouchEvent)
    // ============================================================================================

    /// <summary>
    /// QC ka_SpawnBalls: create the single world ball entity at <paramref name="origin"/> (touch = pickup,
    /// think = relocate when loose). Returns the entity (or null when no facade is wired). The respawn timer
    /// starts armed so an untouched ball relocates after <see cref="RespawnTime"/>.
    /// </summary>
    public Entity? SpawnBall(Vector3 origin)
    {
        Entity? e = GametypeEntities.SpawnObjective("keepawayball", origin, Teams.None,
            new Vector3(-24f, -24f, -24f), new Vector3(24f, 24f, 24f),
            touch: BallTouchEntity, think: RespawnBallThink);
        if (e is not null)
        {
            e.MoveType = MoveType.Bounce;
            e.TakeDamage = DamageMode.Yes;
            e.NextThink = GametypeEntities.Now + RespawnTime;
        }
        BallEntity = e;
        Ball.Carrier = null;
        return e;
    }

    /// <summary>QC ka_RespawnBall (think): relocate a loose ball and re-arm the timer.</summary>
    public void RespawnBall(Entity e)
    {
        if (!ReferenceEquals(e, BallEntity) || Ball.Carrier is not null)
            return;
        // The actual MoveToRandomMapLocation is a host/physics concern; here we just bounce it upward and
        // re-arm so a ball that nobody collects keeps cycling its relocate timer (QC nextthink loop).
        e.Solid = Solid.Trigger;
        e.MoveType = MoveType.Bounce;
        e.Velocity = new Vector3(0f, 0f, 200f);
        e.NextThink = GametypeEntities.Now + RespawnTime;
    }

    /// <summary>Entity touch trampoline: a live player picks the loose ball up (QC ka_TouchEvent).</summary>
    private void BallTouchEntity(Entity self, Entity other)
    {
        if (other is Player p && !p.IsDead && Ball.Carrier is null)
            PickUp(p);
    }

    private void RespawnBallThink(Entity self) => RespawnBall(self);

    // ============================================================================================
    //  Possession damage/force scaling (QC ka_Damage_Calculate)
    // ============================================================================================

    /// <summary>
    /// QC Damage_Calculate (ka): scale player-vs-player damage by whether the attacker and/or target carries
    /// the ball. The x/y/z components of g_keepaway_ballcarrier_damage and g_keepaway_noncarrier_damage select
    /// the self/other-carrier/noncarrier case. Returns the scaled damage. (Force scaling is the same matrix on
    /// the force cvars; <see cref="DamageForceScale"/> exposes that factor.)
    /// </summary>
    public float DamageScale(Player attacker, Player target, float damage)
        => damage * ScaleFactor(attacker, target, "g_keepaway_ballcarrier_damage", "g_keepaway_noncarrier_damage");

    /// <summary>The QC force multiplier for a hit (same matrix as <see cref="DamageScale"/> on the force cvars).</summary>
    public float DamageForceScale(Player attacker, Player target)
        => ScaleFactor(attacker, target, "g_keepaway_ballcarrier_force", "g_keepaway_noncarrier_force");

    private float ScaleFactor(Player attacker, Player target, string carrierCvar, string nonCarrierCvar)
    {
        // QC stores a vector cvar (self.x, otherCarrier.y, noncarrier.z); the facade exposes floats, so we read
        // the three component cvars by suffix. Default 1 (no scaling) when unset.
        bool attackerCarries = ReferenceEquals(Ball.Carrier, attacker);
        bool targetCarries = ReferenceEquals(Ball.Carrier, target);
        string baseName = attackerCarries ? carrierCvar : nonCarrierCvar;
        string suffix = ReferenceEquals(target, attacker) ? "_x" : (targetCarries ? "_y" : "_z");
        return Cvar(baseName + suffix, 1f);
    }

    private static float Cvar(string name, float fallback) => TryCvar(name, out float v) ? v : fallback;

    /// <summary>
    /// Advance Keepaway one step (QC ka_BallThink_Carried): while the ball is carried, accrue
    /// <see cref="ScoreTimePoints"/> per second to the carrier's SCORE, carrying the fractional remainder
    /// like QC's float2int accumulator. <paramref name="dt"/> is the frame delta (QC frametime). Call each
    /// tick; safe after <see cref="MatchEnded"/>.
    /// </summary>
    public void Tick(float dt)
    {
        if (MatchEnded)
            return;
        Player? carrier = Ball.Carrier;
        if (carrier is null || carrier.IsDead)
            return;

        float pps = ScoreTimePoints;
        if (pps > 0f)
        {
            _timePointsRemainder += pps * dt;
            int whole = (int)_timePointsRemainder; // floor toward zero (pps, dt >= 0)
            if (whole != 0)
            {
                _timePointsRemainder -= whole;
                carrier.ScoreFrags += whole;
                UpdateLeaderAndCheckLimit(carrier);
            }
        }

        // QC ka_BallThink_Carried: GameRules_scoring_add(owner, KEEPAWAY_BCTIME, frametime) — accrue ball-carry
        // seconds. The column is an integer, so bank whole seconds and carry the fractional remainder.
        _bcTimeRemainder += dt;
        int wholeSecs = (int)_bcTimeRemainder;
        if (wholeSecs != 0)
        {
            _bcTimeRemainder -= wholeSecs;
            AddCol(carrier, "KEEPAWAY_BCTIME", wholeSecs);
        }
    }

    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;
        bool victimCarried = ReferenceEquals(Ball.Carrier, victim);

        if (attacker is not null && !ReferenceEquals(attacker, victim))
        {
            // QC ka PlayerDies: killing the ballcarrier earns a bonus; a kill made WHILE carrying earns one.
            if (victimCarried)
            {
                attacker.ScoreFrags += (int)ScoreBcKill;
                AddCol(attacker, "KEEPAWAY_CARRIERKILLS", 1); // QC GameRules_scoring_add(attacker, KEEPAWAY_CARRIERKILLS, 1)
            }
            if (ReferenceEquals(Ball.Carrier, attacker))
                attacker.ScoreFrags += (int)ScoreKillAc;

            UpdateLeaderAndCheckLimit(attacker);
        }

        // The victim drops the ball if they had it.
        if (victimCarried)
            Drop();

        // FFA respawn; arm the timer.
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        victim.RespawnTime = now + 2f;
        return false;
    }

    private void UpdateLeaderAndCheckLimit(Player candidate)
    {
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        float limit = PointLimit;
        if (limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>Authoritative leader + limit pass over the roster (QC checkrules). The host may call each tick.</summary>
    public void RecomputeLeader(IReadOnlyList<Player> players)
    {
        Player? best = null;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            if (best is null || p.ScoreFrags > best.ScoreFrags)
                best = p;
        }
        Leader = best;

        float limit = PointLimit;
        if (limit > 0f && best is not null && best.ScoreFrags >= limit)
            MatchEnded = true;
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

/// <summary>
/// The Keepaway ball state — the Godot-free essence of the QC keepawayball edict (.owner carrier, pickup
/// time). Tracks who carries it; the world ball <see cref="Entity"/> lives on <see cref="Keepaway.BallEntity"/>.
/// The ball model/effects, multi-ball chaining, and waypoints remain client concerns.
/// </summary>
public sealed class BallState
{
    /// <summary>The player carrying the ball (QC ball.owner / player.ballcarried), or null when loose.</summary>
    public Player? Carrier;

    /// <summary>Sim time the ball was last picked up (QC pickup time).</summary>
    public float PickupTime;
}
