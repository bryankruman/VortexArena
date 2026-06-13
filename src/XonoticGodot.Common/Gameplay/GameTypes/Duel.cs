using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Duel (1v1) gametype — port of <c>CLASS(Duel, Gametype)</c>
/// (common/gametypes/gametype/duel/duel.qh + sv_duel.qc).
///
/// Duel is Deathmatch constrained to exactly two players: it reuses the DM frag matrix wholesale, the only
/// gametype-specific rules being the hard player limit of 2 (QC MUTATOR_HOOKFUNCTION(duel, GetPlayerLimit)
/// → 2) and the powerup filter (FilterItemDefinition → block powerups unless g_duel_with_powerups). The win
/// condition is the same frag/time limit as DM, but the gametype default is <c>pointlimit=0</c> (no frag
/// limit — duels are decided by the time limit by default), so it relies on the engine <c>fraglimit</c>
/// only when an admin sets one.
///
/// Faithfully ported (the duel-relevant slice):
///  - GetPlayerLimit → 2 (<see cref="PlayerLimit"/>): duel is always 1v1;
///  - the DM obituary/scoring matrix (enemy frag +1, suicide/world −1) reused verbatim from Deathmatch;
///  - frag-limit end-of-match latch (g_duel default pointlimit=0 ⇒ unlimited unless fraglimit set).
///
/// Faithfully ported (the duel-relevant slice, cont.):
///  - the powerup FilterItemDefinition hook (<see cref="FilterItem"/>, QC: block powerups unless
///    g_duel_with_powerups).
///
/// Deferred (NOTE — cross-boundary): map-size support gating (m_isAlwaysSupported diameter &lt; 3250 /
/// m_isForcedSupported on DM maps) and the spree/announcer + score networking/HUD shared with DM — map-pool
/// and client concerns.
/// </summary>
[GameType]
public sealed class Duel : GameType
{
    // ----- frag/time limit cvars (gametype default pointlimit=0 ⇒ no frag limit; timelimit=10) -----
    private const string CvarFragLimitDuel = "g_duellimit"; // duel-specific override, if an admin sets one
    private const string CvarFragLimit     = "fraglimit";   // generic engine frag limit
    private const float  DefaultFragLimit  = 0f;            // QC m_legacydefaults / gametype_init: pointlimit=0

    // ----- respawn delay cvars (shared with DM; xonotic-server.cfg g_respawn_delay_small/large = 2) -----
    private const string CvarRespawnDelaySmall = "g_respawn_delay_small";
    private const string CvarRespawnDelayLarge = "g_respawn_delay_large";
    private const float  DefaultRespawnDelay   = 2f;

    /// <summary>QC GetPlayerLimit → 2: duel is always exactly 1v1.</summary>
    public const int PlayerLimit = 2;

    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>Optional sink for the host/controller to react to a frag (e.g. schedule the respawn).</summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a player reaches the frag limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current frag leader (highest <see cref="Player.ScoreFrags"/>), or null before any frag.</summary>
    public Player? Leader { get; private set; }

    public Duel()
    {
        NetName = "duel";
        DisplayName = "Duel";
        TeamGame = false;
    }

    public override void OnInit()
    {
        // QC INIT(Duel): identity is set in the ctor; the player limit is the const PlayerLimit (2) and the
        // powerup filter is FilterItem. gametype_init flags (USEPOINTS|1V1) and map-size gating are
        // engine/map-pool concerns.
    }

    /// <summary>QC FFA equality (server/scores.qc:537): the two duelists are tied on the primary score. Duel
    /// is timed (pointlimit=0), so a tie at the time limit is the canonical overtime trigger (server/world.qc).</summary>
    public override bool ReportsTie(IReadOnlyList<Player> roster) => FfaTie.TopTwoTied(roster);

    /// <summary>QC autocvar_g_duel_with_powerups: when set, powerups are allowed in duel (off by default).</summary>
    public bool WithPowerups => Api.Services is not null && Api.Cvars.GetFloat("g_duel_with_powerups") != 0f;

    /// <summary>
    /// QC MUTATOR_HOOKFUNCTION(duel, FilterItemDefinition): block powerup items from spawning unless
    /// g_duel_with_powerups is set. Returns true if the item should be FILTERED OUT (not spawned).
    /// </summary>
    public bool FilterItem(GameItemDef definition)
        => definition.IsPowerup && !WithPowerups;

    /// <summary>
    /// The frag limit currently in force (g_duellimit, else fraglimit, else default 0 = unlimited). 0 means
    /// "no frag limit" — duels are decided by the time limit unless an admin sets a fraglimit.
    /// </summary>
    public float FragLimit
    {
        get
        {
            if (TryCvar(CvarFragLimitDuel, out float dl)) return dl;
            if (TryCvar(CvarFragLimit, out float fl)) return fl;
            return DefaultFragLimit;
        }
    }

    public void Activate()
    {
        if (_deathHandler is not null)
            return;
        MatchEnded = false;
        Leader = null;
        // QC ScoreRules_basics (Duel is 1v1 deathmatch, no extra columns): SP_SCORE is the primary sort/fraglimit
        // key. Re-pin it so switching from a column-mode reverts the scoreboard to sorting by score.
        Scoring.GameScores.ScoreRulesBasics(teams: false);
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// The DM obituary/scoring handler restricted to duel — identical matrix to Deathmatch (enemy frag +1,
    /// suicide/world death −1), since duel only differs from DM by the player limit and item filter.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        if (ev.Victim is not Player victim)
            return false;
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        if (attacker is null || ReferenceEquals(attacker, victim))
            victim.ScoreFrags -= 1;       // SUICIDE / ACCIDENT: victim loses a frag
        else
            attacker.ScoreFrags += 1;     // ENEMY FRAG: attacker gains a frag (no teams in duel)

        ScheduleRespawn(victim);
        UpdateLeaderAndCheckLimit(attacker, victim);
        Events?.OnFrag(attacker, victim, ev.DeathType);
        return false;
    }

    /// <summary>QC calculate_respawntime reduced to 1v1: respawn_time = time + delay (small delay).</summary>
    public void ScheduleRespawn(Player victim)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        victim.RespawnTime = now + RespawnDelay;
    }

    public float RespawnDelay
    {
        get
        {
            if (TryCvar(CvarRespawnDelaySmall, out float small)) return small;
            if (TryCvar(CvarRespawnDelayLarge, out float large)) return large;
            return DefaultRespawnDelay;
        }
    }

    private void UpdateLeaderAndCheckLimit(Player? attacker, Player victim)
    {
        Player candidate = attacker ?? victim;
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        float limit = FragLimit;
        if (limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>Authoritative leader + frag-limit pass over the (two-player) roster.</summary>
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

        float limit = FragLimit;
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
