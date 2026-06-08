using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server-side team manager — the C# successor to the team-assignment slice of
/// server/teamplay.qc (<c>TeamBalance_JoinBestTeam</c> → <c>TeamBalance_FindBestTeam</c>: place a joining
/// player on the active team with the fewest players; ties → the lowest-index such team). It wraps the
/// Godot-free core already ported in <see cref="TeamBalance"/> (XonoticGodot.Common) and adds the server-roster
/// concerns: the configured team count, balance over the live roster, and a notion of "this gametype is a
/// team game".
///
/// QC <c>TeamBalance_FindBestTeam</c> weighs the smallest team first and, on a tie, the team with the lower
/// total score (so a team that is both small and losing is preferred) — both rules are ported here. Also
/// ported now: the inverse-variance skill weighting (<c>g_balance_teams_skill</c>:
/// <see cref="TeamBalance_GetWeightedTeamCount"/> sums 1/variance-weighted skill so a strong player counts
/// as "more" than a weak one, and the significance threshold gates whether skill is used at all), the bot
/// autobalance (<see cref="AutoBalanceBots"/>, QC <c>TeamBalance_AutoBalanceBots</c>: move the lowest-scoring
/// bot from the largest to the smallest team when uneven), and <see cref="KillPlayerForTeamChange"/>
/// (QC the forced death + score clear on a mid-match team move). A <see cref="SkillProvider"/> supplies each
/// player's skill (bots feed their <c>skill</c> level) since TrueSkill mu/variance ratings aren't modeled.
///
/// Deferred: g_forced_team_*, the warmup join queue, and the network team-nagger.
/// </summary>
public sealed class Teamplay
{
    /// <summary>Whether the active gametype is a team game (QC <c>teamplay</c>). FFA leaves teams at <see cref="Teams.None"/>.</summary>
    public bool IsTeamGame { get; }

    /// <summary>Number of teams in play (2..4). 0 for a non-team game.</summary>
    public int TeamCount { get; private set; }

    /// <summary>The running per-team score, mirrored from <see cref="Scores"/> so balance can read it without a back-reference.</summary>
    private readonly Scores? _scores;

    /// <summary>
    /// Supplies a player's skill rating (QC <c>m_skill_mu</c>): bots feed their <c>skill</c> level (0..10),
    /// humans default to a mid rating. Used for the skill-weighted team balance when
    /// <c>g_balance_teams_skill</c> is enabled. Defaults to a flat 5 for every player (no skill weighting
    /// effect) until a host wires in real skills (e.g. each bot's <see cref="Bot.BotBrain.Skill"/>).
    /// </summary>
    public Func<Player, float> SkillProvider { get; set; } = static _ => 5f;

    /// <summary>QC the fixed skill variance stand-in (no TrueSkill ratings here): the inverse weight per player.</summary>
    private const float SkillVariance = 1f;

    /// <summary>
    /// Construct the team manager. <paramref name="teamCount"/> is clamped to 2..4 for a team game (QC
    /// gametype team count, e.g. g_tdm_teams). <paramref name="scores"/> (optional) is consulted for the
    /// lowest-score tiebreak; without it, ties fall to the lowest-index team like the base helper.
    /// </summary>
    public Teamplay(bool isTeamGame, int teamCount = 2, Scores? scores = null)
    {
        IsTeamGame = isTeamGame;
        TeamCount = isTeamGame ? System.Math.Clamp(teamCount, 2, 4) : 0;
        _scores = scores;
        if (isTeamGame)
            _scores?.SeedTeams(TeamCount);
    }

    /// <summary>Reconfigure the team count at runtime (QC teams_override cvar change). Clamped to 2..4.</summary>
    public void SetTeamCount(int teamCount)
    {
        if (!IsTeamGame)
            return;
        TeamCount = System.Math.Clamp(teamCount, 2, 4);
        _scores?.SeedTeams(TeamCount);
    }

    /// <summary>
    /// Port of <c>TeamBalance_JoinBestTeam</c> → <c>TeamBalance_FindBestTeam</c>: assign <paramref name="joiner"/>
    /// to the smallest active team over <paramref name="roster"/> (ignoring the joiner), breaking ties by the
    /// lower team score, then by the lower team index. Writes <see cref="Common.Framework.Entity.Team"/> and
    /// returns the chosen color code. A no-op returning <see cref="Teams.None"/> for a non-team game.
    /// </summary>
    public int AssignBestTeam(Player joiner, IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
        {
            joiner.Team = Teams.None;
            return Teams.None;
        }

        bool useSkill = SkillWeightingEnabled;

        int bestTeam = Teams.None;
        float bestCount = float.MaxValue;
        int bestScore = int.MaxValue;

        foreach (int team in Teams.Active(TeamCount))
        {
            // QC TeamBalance_FindBestTeams: weigh by the (optionally skill-weighted) player count, then
            // break ties by the lower team score (a small AND losing team is preferred), then lowest index.
            float count = useSkill
                ? TeamBalance_GetWeightedTeamCount(team, roster, joiner)
                : TeamBalance.CountTeam(team, roster, joiner);
            int score = _scores?.TeamScore(team) ?? 0;

            if (count < bestCount || (count == bestCount && score < bestScore))
            {
                bestCount = count;
                bestScore = score;
                bestTeam = team;
            }
        }

        if (bestTeam == Teams.None)
            bestTeam = Teams.Red; // degenerate guard (matches TeamBalance.JoinSmallestTeam)

        joiner.Team = bestTeam;
        return bestTeam;
    }

    /// <summary>
    /// QC <c>g_balance_teams_skill</c> gate: whether to weigh team sizes by player skill rather than raw
    /// count. True only when the cvar is set; without TrueSkill ratings the significance threshold is always
    /// considered met (every player contributes its <see cref="SkillProvider"/> rating).
    /// </summary>
    private bool SkillWeightingEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_balance_teams_skill") != 0f;

    /// <summary>
    /// QC <c>TeamBalanceTeam_GetWeightedNumberOfPlayers</c> (the inverse-variance skill weighting): instead of
    /// counting each player as 1, count each as its <c>1/variance</c> skill weight scaled by their skill mu,
    /// so a strong player counts as "more team strength" than a weak one. With a flat variance here this
    /// reduces to "sum of skills / a reference skill", giving the smallest <em>total skill</em> team. Excludes
    /// <paramref name="ignore"/> (the joining player, counted via the caller's branch).
    /// </summary>
    private float TeamBalance_GetWeightedTeamCount(int team, IReadOnlyList<Player> roster, Player? ignore)
    {
        const float referenceSkill = 5f; // QC server_skill_average stand-in (mid skill = weight 1.0)
        float total = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, ignore)) continue;
            if ((int)p.Team != team) continue;
            float weight = 1f / SkillVariance;                 // QC skill_weight = 1 / m_skill_var
            float mu = System.Math.Max(0.1f, SkillProvider(p)); // QC m_skill_mu
            total += weight * (mu / referenceSkill);
        }
        return total;
    }

    /// <summary>QC <c>TeamBalance_GetNumberOfPlayers</c>: count the roster on a given team.</summary>
    public int CountTeam(int team, IReadOnlyList<Player> roster) => TeamBalance.CountTeam(team, roster);

    /// <summary>
    /// QC <c>TeamBalance_IsTeamAllowed</c> / the "are teams uneven?" check used by autobalance: true when the
    /// largest active team has at least two more players than the smallest (the QC threshold for moving a
    /// player). Returns false for a non-team game or fewer than two players.
    /// </summary>
    public bool TeamsAreUneven(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return false;
        int min = int.MaxValue, max = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < min) min = c;
            if (c > max) max = c;
        }
        return max != int.MinValue && (max - min) >= 2;
    }

    /// <summary>
    /// QC <c>TeamBalance_AutoBalanceBots</c>: if the teams are uneven by ≥2 players, move the
    /// <em>lowest-scoring</em> bot from the largest team to the smallest (QC
    /// <c>TeamBalance_GetPlayerForTeamSwitch</c> picks the lowest SP_SCORE). Returns the moved player, or null
    /// if nothing needed moving / no movable bot exists. Faithful to QC: only bots are moved (never yank a
    /// human across teams mid-match), and the lowest scorer goes so the strongest players stay put. The caller
    /// should follow up with <see cref="KillPlayerForTeamChange"/> + a re-spawn for the moved bot.
    /// </summary>
    public Player? AutoBalanceBots(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return null;

        // QC: find the smallest team, then the largest; bail if they're the same or the gap is < 2.
        int smallTeam = SmallestTeam(roster, out int smallCount);
        if (smallTeam == Teams.None)
            return null;

        // walk teams from largest down until we find one with a movable bot and a ≥2 gap (QC the while loop).
        int remainingTeams = TeamCount;
        var consideredLargest = new HashSet<int>();
        while (consideredLargest.Count < remainingTeams)
        {
            int largeTeam = LargestTeamExcluding(roster, consideredLargest, out int largeCount);
            if (largeTeam == Teams.None || largeTeam == smallTeam)
                return null;
            if (largeCount - smallCount < 2)
                return null; // QC: stop once the largest remaining team is within 1 of the smallest

            Player? bot = LowestScoringBotOnTeam(roster, largeTeam);
            if (bot is not null)
            {
                bot.Team = smallTeam;
                return bot;
            }
            consideredLargest.Add(largeTeam); // no movable bot here; try the next-largest team
        }
        return null;
    }

    /// <summary>
    /// QC <c>KillPlayerForTeamChange</c>: when a player is moved across teams mid-match, kill them
    /// (DEATH_AUTOTEAMCHANGE — always lethal, does not negate frags) and clear their auxiliary score columns
    /// so the move doesn't unfairly carry kills/deaths to the new team. Returns true if the player was alive
    /// (and thus killed). The caller re-spawns the player afterwards via the normal respawn path.
    /// </summary>
    public bool KillPlayerForTeamChange(Player p)
    {
        // QC PlayerScore_Clear on a team change: reset this player's aux score columns.
        _scores?.Row(p).ClearForTeamChange();

        if (p.IsDead)
            return false;

        // QC Damage(... DEATH_AUTOTEAMCHANGE ...): force-kill through the damage pipeline so the obituary
        // bus + respawn timer fire exactly like any other death.
        Combat.Damage(p, null, null, 100000f, DeathTypes.AutoTeamChange, p.Origin, System.Numerics.Vector3.Zero);
        return true;
    }

    // ----- balance scan helpers (QC TeamBalance_GetLargestTeamIndex / GetPlayerForTeamSwitch) -----

    private int SmallestTeam(IReadOnlyList<Player> roster, out int count)
    {
        int best = Teams.None, bestCount = int.MaxValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < bestCount) { bestCount = c; best = team; }
        }
        count = bestCount == int.MaxValue ? 0 : bestCount;
        return best;
    }

    private int LargestTeamExcluding(IReadOnlyList<Player> roster, HashSet<int> exclude, out int count)
    {
        int best = Teams.None, bestCount = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            if (exclude.Contains(team)) continue;
            int c = TeamBalance.CountTeam(team, roster);
            if (c > bestCount) { bestCount = c; best = team; }
        }
        count = bestCount == int.MinValue ? 0 : bestCount;
        return best;
    }

    private Player? LowestScoringBotOnTeam(IReadOnlyList<Player> roster, int team)
    {
        Player? lowest = null;
        int lowestScore = int.MaxValue;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (!p.IsBot || (int)p.Team != team) continue;
            int score = p.ScoreFrags;
            if (score < lowestScore) { lowestScore = score; lowest = p; }
        }
        return lowest;
    }
}
