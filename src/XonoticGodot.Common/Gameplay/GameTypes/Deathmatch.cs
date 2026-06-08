using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Deathmatch (FFA) gametype — port of <c>CLASS(Deathmatch, Gametype)</c>
/// (common/gametypes/gametype/deathmatch/deathmatch.qh + sv_deathmatch.qc) fused with the DM-relevant
/// slice of the obituary/scoring path (server/damage.qc <c>Obituary</c> / <c>GiveFrags</c>).
///
/// Everyone frags everyone; reaching the frag limit ends the match. On <see cref="OnInit"/> the registry
/// bootstrap constructs this; the host calls <see cref="Activate"/> to subscribe the kill handler and
/// <see cref="Deactivate"/> to unsubscribe.
///
/// Faithfully ported (GiveFrags / Obituary scoring matrix, server/damage.qc):
///  - enemy frag  → attacker +1 (Obituary "MURDER" non-teammate → GiveFrags(attacker, targ, +1));
///  - suicide     → victim   −1 (Obituary "SUICIDE" → GiveFrags(attacker==targ, targ, −1));
///  - world/accident death (no player attacker) → victim −1 (Obituary "ACCIDENT/TRAP" → GiveFrags(targ, targ, −1));
///  - teamkill    → attacker −1 (GiveFrags ... −1) — present for completeness though DM is non-team;
///  - frag-limit end-of-match check (QC GameRules pointlimit / checkrules) and the respawn-time schedule
///    (server/client.qc <c>calculate_respawntime</c>: respawn_time = time + delay).
///
/// Deferred (NOTE — cross-boundary): score networking/HUD, notifications/announcers, spree tracking, leadlimit/
/// timelimit, warmup, team scoring, GiveFragsForKill mutator hook, and the dying-animation interim.
/// </summary>
[GameType]
public sealed class Deathmatch : GameType
{
    // ----- frag-limit cvars + default (gametype default pointlimit=30; m_legacydefaults "30 20 0") -----
    private const string CvarFragLimitDm  = "g_dmlimit";   // DM-specific override
    private const string CvarFragLimit    = "fraglimit";   // generic engine frag limit
    private const float  DefaultFragLimit = 30f;

    // ----- respawn delay cvars + default (xonotic-server.cfg: g_respawn_delay_small/large = 2) -----
    private const string CvarRespawnDelaySmall = "g_respawn_delay_small";
    private const string CvarRespawnDelayLarge = "g_respawn_delay_large";
    private const float  DefaultRespawnDelay   = 2f;

    /// <summary>Cached delegate so <see cref="Deactivate"/> can <see cref="HookChain{T}.Remove"/> the exact instance.</summary>
    private HookHandler<DeathEvent>? _deathHandler;

    /// <summary>
    /// Optional sink the gametype notifies when a kill is scored, so the <see cref="MatchController"/> (or a
    /// host) can react (e.g. schedule the victim's respawn) without the gametype owning the player list. Set
    /// by <see cref="MatchController"/> in its <c>Activate</c>.
    /// </summary>
    public IMatchEvents? Events;

    /// <summary>QC checkrules end-of-match latch: true once a player reaches the frag limit.</summary>
    public bool MatchEnded { get; private set; }

    /// <summary>The current frag leader (highest <see cref="Player.ScoreFrags"/>), or null before any frag.</summary>
    public Player? Leader { get; private set; }

    public Deathmatch()
    {
        NetName = "dm";
        DisplayName = "Deathmatch";
        TeamGame = false;
    }

    /// <summary>QC INIT(Deathmatch) gametype_init: identity is set in the ctor; kept for parity / future cvar seeding.</summary>
    public override void OnInit()
    {
        // QC INIT(Deathmatch): the gametype identity (NetName/DisplayName/TeamGame=false) is set in the ctor
        // and the frag limit is read on demand (FragLimit). The gametype_init flags (USEPOINTS|PREFERRED) and
        // the mapinfo pointlimit are engine-side metadata — there is no per-match objective state to seed here.
    }

    /// <summary>
    /// The frag limit currently in force (g_dmlimit, else fraglimit, else default 30). A value of 0 means
    /// "no limit" (QC pointlimit 0 == unlimited) — distinguished from an unset cvar via the string read, so
    /// an explicit <c>g_dmlimit 0</c> is honored as unlimited rather than falling back to the default.
    /// </summary>
    public float FragLimit
    {
        get
        {
            if (TryCvar(CvarFragLimitDm, out float dm)) return dm;
            if (TryCvar(CvarFragLimit, out float fl)) return fl;
            return DefaultFragLimit;
        }
    }

    /// <summary>Subscribe the kill handler to the obituary bus (QC: gametype hooks installed on activation).</summary>
    public void Activate()
    {
        if (_deathHandler is not null)
            return; // idempotent
        MatchEnded = false;
        Leader = null;
        // QC ScoreRules_basics (deathmatch.qh has no extra columns): SP_SCORE is the primary sort/fraglimit key
        // (sprio default SFL_SORT_PRIO_PRIMARY); no per-mode columns. Re-pin it so switching from a mode that set
        // CTF_CAPS/RACE_LAPS/… as primary reverts the scoreboard to sorting by score.
        Scoring.GameScores.ScoreRulesBasics(teams: false);
        Scoring.GameScores.SetSortKeys(Scoring.GameScores.Score);
        _deathHandler = OnDeath;
        Combat.Death.Add(_deathHandler);
    }

    /// <summary>Unsubscribe the kill handler (QC: gametype teardown on map end / gametype switch).</summary>
    public void Deactivate()
    {
        if (_deathHandler is null)
            return;
        Combat.Death.Remove(_deathHandler);
        _deathHandler = null;
    }

    /// <summary>
    /// The obituary/scoring handler — the Godot-free essence of <c>Obituary</c> + <c>GiveFrags</c>
    /// (server/damage.qc). Awards frags per the DM matrix, schedules the victim's respawn, and checks the
    /// frag limit. Returns false (does not consume the event) so other subscribers still run.
    /// </summary>
    private bool OnDeath(ref DeathEvent ev)
    {
        // QC Obituary sanity check: only score player victims.
        if (ev.Victim is not Player victim)
            return false;

        // QC GiveFrags bails when game_stopped; once the match has ended we stop scoring.
        if (MatchEnded)
            return false;

        Player? attacker = ev.Attacker as Player;

        if (attacker is null || ReferenceEquals(attacker, victim))
        {
            // SUICIDE (targ == attacker) or ACCIDENT/TRAP (no player attacker): victim loses a frag.
            // QC: GiveFrags(attacker, targ, -1) with attacker==targ, and GiveFrags(targ, targ, -1).
            victim.ScoreFrags -= 1;
            victim.GtSuicides += 1; // QC SP_SUICIDES
        }
        else if (SameTeam(attacker, victim))
        {
            // TEAMKILL: attacker loses a frag (QC GiveFrags(attacker, targ, -1)). DM is FFA so this is
            // effectively unreachable (Team 0 == Team 0 would match) — guarded below so FFA never teamkills.
            attacker.ScoreFrags -= 1;
            attacker.GtKillCount = 0; // a teamkill breaks the attacker's spree
        }
        else
        {
            // ENEMY FRAG: attacker gains a frag (QC GiveFrags(attacker, targ, +1)).
            attacker.ScoreFrags += 1;
            attacker.GtKillCount += 1; // QC .killcount — the attacker's frag spree
        }

        // QC obituary bookkeeping: the victim's DEATHS counter ticks and their frag spree resets.
        victim.GtDeaths += 1;   // QC SP_DEATHS
        victim.GtKillCount = 0; // dying ends a spree

        // Schedule the victim's respawn (QC calculate_respawntime: respawn_time = time + delay).
        ScheduleRespawn(victim);

        // Recompute the leader and check the frag limit (QC checkrules / Scores_CountFragsRemaining).
        UpdateLeaderAndCheckLimit(attacker, victim);

        // Let the host/controller react to the kill (e.g. enqueue the respawn).
        Events?.OnFrag(attacker, victim, ev.DeathType);

        return false; // not "handled" — allow other Death subscribers (stats, mutators) to run
    }

    /// <summary>
    /// QC <c>calculate_respawntime</c> (server/client.qc) reduced to FFA: <c>respawn_time = time + delay</c>.
    /// The full version scales the delay between g_respawn_delay_small/large by player count; here we use the
    /// small delay (both default to 2s in xonotic-server.cfg, so the simplification is exact at defaults).
    /// </summary>
    public void ScheduleRespawn(Player victim)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        float delay = RespawnDelay;
        victim.RespawnTime = now + delay;
        // QC also sets deadflag/respawn_flags; the damage system already set DeadState=Dead before this fires.
    }

    /// <summary>
    /// The respawn delay in seconds (g_respawn_delay_small, falling back to large, then 2s). An explicit 0
    /// (instant respawn) is honored — the string read distinguishes it from an unset cvar.
    /// </summary>
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
        // Track the leader among the two players whose score just changed (cheap, no full scan); the
        // controller can call RecomputeLeader for an authoritative pass if it holds the whole roster.
        Player candidate = attacker ?? victim;
        if (Leader is null || candidate.ScoreFrags > Leader.ScoreFrags)
            Leader = candidate;

        float limit = FragLimit;
        if (limit > 0f && Leader is not null && Leader.ScoreFrags >= limit)
            MatchEnded = true;
    }

    /// <summary>
    /// Authoritative leader + frag-limit pass over the full roster (QC checkrules over all clients). The host
    /// may call this each tick; the incremental path in <see cref="OnDeath"/> keeps it correct between calls.
    /// </summary>
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

    /// <summary>
    /// QC SAME_TEAM. In FFA (TeamGame=false) players are never teammates, so two players only "share a team"
    /// when this is a team game AND both have the same nonzero team — this keeps the FFA frag path from ever
    /// being treated as a teamkill.
    /// </summary>
    private bool SameTeam(Player a, Player b)
        => TeamGame && a.Team != 0f && a.Team == b.Team;

    /// <summary>
    /// Read a float cvar IF it is actually set (non-empty string), so an explicit 0 is distinguishable from
    /// "unset". The <see cref="ICvarService"/> returns "" for an unregistered/unset cvar (vs. "0" when set
    /// to zero). Returns false (and <paramref name="value"/> = 0) when unset or no services are wired.
    /// </summary>
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
