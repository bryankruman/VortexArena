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
/// ported now: the inverse-variance skill weighting (<c>g_balance_teams_skill</c>: <see cref="MeasureTeam"/>
/// computes each team's weighted mean skill and <see cref="CompareTeams"/> breaks equal-size ties by strength
/// only when the skill gap is statistically significant (z &gt; <c>..._significance_threshold</c> SDs), so a
/// strong player counts as "more" than a weak one but skill never reorders teams of unequal size), the bot
/// autobalance (<see cref="AutoBalanceBots"/>, QC <c>TeamBalance_AutoBalanceBots</c>: move the lowest-scoring
/// bot from the largest to the smallest team when uneven), and <see cref="KillPlayerForTeamChange"/>
/// (QC the forced death + score clear on a mid-match team move). A <see cref="SkillProvider"/> supplies each
/// player's skill (bots feed their <c>skill</c> level) since TrueSkill mu/variance ratings aren't modeled.
///
/// Also ported now: forced teams (<see cref="DetermineForcedTeam"/> / <see cref="GetForcedTeam"/>, QC
/// <c>Player_DetermineForcedTeam</c> / <c>Player_HasRealForcedTeam</c> — <c>g_forced_team_*</c> id/IP-list
/// parsing, <c>g_campaign_forceteam</c>, and the <c>bot_forced_team</c> pin, all honored on the live
/// <see cref="AssignBestTeam"/> join), the <c>bot_vs_human</c> team partition (QC
/// <c>TeamBalance_CheckAllowedTeams</c>, bots banned to one side / humans to the other), the unbalanced-teams
/// size gap (<see cref="SizeDifference"/> / <see cref="TeamsUnbalancedForNag"/>, QC
/// <c>TeamBalance_SizeDifference</c> + <c>sv_teamnagger</c>, for the nag + warmup-hold), and the mid-match
/// switch gate (<see cref="IsTeamAllowedForSwitch"/>, QC <c>g_balance_teams_prevent_imbalance</c> /
/// cmd.qc:746 — a human can't switch to a team that isn't a best team).
///
/// Deferred (need cross-file plumbing): the warmup join queue (<c>g_balance_teams_queue</c>), excess-player
/// removal (<c>g_balance_teams_remove</c>), the network side of the team-nagger center-print, the
/// Player_ChangeTeam/Player_ChangedTeam mutator hooks (no hook chain exists yet), and the
/// <c>teamplay_lockonrestart</c> auto-lock (the restart hook lives in the vote/warmup controller).
/// </summary>
public sealed class Teamplay
{
    /// <summary>QC <c>TEAM_FORCE_SPECTATOR</c>: the forced-team sentinel meaning "force this player to spectate".</summary>
    public const int TeamForceSpectator = -1;

    /// <summary>QC <c>TEAM_FORCE_DEFAULT</c>: no real forced team — fall through to normal balance.</summary>
    public const int TeamForceDefault = 0;

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
    /// QC <c>.team_forced</c>: the per-player forced-team index (1..4 a real team, <see cref="TeamForceDefault"/>,
    /// or <see cref="TeamForceSpectator"/>). Player.cs has no such field, so it is kept here keyed by the player
    /// reference; <see cref="DetermineForcedTeam"/> populates it on connect and <see cref="AssignBestTeam"/>
    /// honors it. Defaults to <see cref="TeamForceDefault"/> for any player not in the table.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object> _forcedTeam = new();

    /// <summary>
    /// QC <c>.bot_forced_team</c>: a bot pinned to a specific team by its roster config (5th arg of the addbot
    /// command). When set (1..4), <see cref="AssignBestTeam"/> early-outs and the bot keeps that team without
    /// auto-balance, exactly like QC's <c>TeamBalance_JoinBestTeam</c> early-out (teamplay.qc:461). Keyed by the
    /// bot's <see cref="Player"/> reference since Player.cs has no field for it.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, object> _botForcedTeam = new();

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

    // ----- forced teams (QC Player_DetermineForcedTeam / Player_HasRealForcedTeam / bot_forced_team) -----

    /// <summary>
    /// QC <c>Team_IndexToTeam</c>: map a 1..4 forced-team index to its color code (other → <see cref="Teams.None"/>).
    /// </summary>
    public static int IndexToTeam(int index) =>
        index >= 1 && index <= Teams.All.Length ? Teams.All[index - 1] : Teams.None;

    /// <summary>
    /// QC <c>Player_DetermineForcedTeam</c>: compute and store <paramref name="player"/>'s forced team from the
    /// server config. In campaign, real clients are pinned to <c>g_campaign_forceteam</c> (1..4); otherwise the
    /// player's crypto id / IP is matched against <c>g_forced_team_{red,blue,yellow,pink}</c> (→ 1..4), falling
    /// back to <c>g_forced_team_otherwise</c> (red|blue|yellow|pink|spectate|default). A non-team game clears any
    /// real forced team. Called once on connect; <see cref="AssignBestTeam"/> later honors the result.
    /// </summary>
    public void DetermineForcedTeam(Player player, bool isCampaign = false)
    {
        int forced = TeamForceDefault;

        if (isCampaign)
        {
            // QC: only real clients (not bots) are campaign-forced.
            if (!player.IsBot)
            {
                int ct = Api.Services is not null ? (int)Api.Cvars.GetFloat("g_campaign_forceteam") : 0;
                forced = (ct >= 1 && ct <= Teams.All.Length) ? ct : TeamForceDefault;
            }
        }
        else if (PlayerInList(player, "g_forced_team_red"))
            forced = 1;
        else if (PlayerInList(player, "g_forced_team_blue"))
            forced = 2;
        else if (PlayerInList(player, "g_forced_team_yellow"))
            forced = 3;
        else if (PlayerInList(player, "g_forced_team_pink"))
            forced = 4;
        else
        {
            string otherwise = Api.Services is not null ? Api.Cvars.GetString("g_forced_team_otherwise") : "default";
            forced = otherwise switch
            {
                "red" => 1,
                "blue" => 2,
                "yellow" => 3,
                "pink" => 4,
                "spectate" or "spectator" => TeamForceSpectator,
                _ => TeamForceDefault,
            };
        }

        // QC: a non-team game clears any real forced team (spectator sentinel survives).
        if (!IsTeamGame && forced > TeamForceDefault)
            forced = TeamForceDefault;

        SetForcedTeam(player, forced);
    }

    /// <summary>QC <c>Player_SetForcedTeamIndex</c>: store a player's forced-team index directly (admin/script override).</summary>
    public void SetForcedTeam(Player player, int index)
    {
        _forcedTeam.Remove(player);
        if (index != TeamForceDefault)
            _forcedTeam.Add(player, index);
    }

    /// <summary>QC <c>Player_GetForcedTeamIndex</c>: the stored forced-team index (default if unset).</summary>
    public int GetForcedTeam(Player player) =>
        _forcedTeam.TryGetValue(player, out object? v) ? (int)v! : TeamForceDefault;

    /// <summary>QC <c>Player_HasRealForcedTeam</c>: true when the player is pinned to an actual team (1..4).</summary>
    public bool HasRealForcedTeam(Player player) => GetForcedTeam(player) > TeamForceDefault;

    /// <summary>
    /// QC <c>.bot_forced_team</c>: pin a bot to a team index (1..4) from its roster config, or clear with 0.
    /// A pinned bot skips auto-balance in <see cref="AssignBestTeam"/>.
    /// </summary>
    public void SetBotForcedTeam(Player bot, int index)
    {
        _botForcedTeam.Remove(bot);
        if (index >= 1 && index <= Teams.All.Length)
            _botForcedTeam.Add(bot, index);
    }

    /// <summary>The bot's forced-team index (1..4), or 0 if unpinned.</summary>
    public int GetBotForcedTeam(Player bot) =>
        _botForcedTeam.TryGetValue(bot, out object? v) ? (int)v! : 0;

    /// <summary>
    /// QC <c>PlayerInList</c> (client.qc:1047): is the player's crypto id (<see cref="Player.PersistentId"/>) or
    /// IP (<see cref="Player.NetAddress"/>) present in the space-separated cvar list <paramref name="cvar"/>?
    /// Empty list → false.
    /// </summary>
    private static bool PlayerInList(Player player, string cvar)
    {
        if (Api.Services is null)
            return false;
        string list = Api.Cvars.GetString(cvar);
        if (string.IsNullOrEmpty(list))
            return false;
        string[] entries = list.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (string e in entries)
        {
            if (!string.IsNullOrEmpty(player.PersistentId) && e == player.PersistentId)
                return true;
            if (!string.IsNullOrEmpty(player.NetAddress) && e == player.NetAddress)
                return true;
        }
        return false;
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

        // QC TeamBalance_JoinBestTeam: a bot pinned by its roster config keeps its team, no auto-balance.
        int botForced = GetBotForcedTeam(joiner);
        if (botForced != 0)
        {
            int forcedColor = IndexToTeam(botForced);
            joiner.Team = forcedColor;
            return forcedColor;
        }

        // QC TeamBalance_CheckAllowedTeams: bot_vs_human bans all-but-one side for bots / the other for humans.
        bool[] allowed = AllowedTeams(joiner);

        // QC: a player with a real forced team joins it iff that team is allowed; otherwise normal balance.
        if (HasRealForcedTeam(joiner))
        {
            int idx = GetForcedTeam(joiner);
            if (idx >= 1 && idx <= TeamCount && allowed[idx - 1])
            {
                int forcedColor = IndexToTeam(idx);
                joiner.Team = forcedColor;
                return forcedColor;
            }
        }

        int bestTeam = Teams.None;
        TeamStrength best = default;

        int ti = 0;
        foreach (int team in Teams.Active(TeamCount))
        {
            if (!allowed[ti++])
                continue; // QC: a banned team is never a candidate (bot_vs_human partition).

            // QC TeamBalance_CompareTeamsInternal: order teams by player count first, then by team strength
            // (skill+score) as a tiebreak — a smaller team wins outright; equal-size teams break to the weaker.
            TeamStrength cand = MeasureTeam(team, roster, joiner);
            if (bestTeam == Teams.None || CompareTeams(cand, best) < 0)
            {
                best = cand;
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
    /// QC the per-team balance entity (teamplay.qc:840-931): the player count plus the inverse-variance weighted
    /// mean skill (<c>m_skill_mu</c>, <c>m_skill_var = 1/mass</c>) and the team score, used by
    /// <see cref="CompareTeams"/> to order candidate teams (size first, then skill+score strength).
    /// </summary>
    private readonly struct TeamStrength
    {
        public readonly int Count;     // QC m_num_players
        public readonly float SkillMu; // QC m_skill_mu (inverse-variance weighted mean skill; 0 = empty team)
        public readonly float SkillVar;// QC m_skill_var (= 1/mass; 0 = no rated players)
        public readonly int Score;     // QC m_team_score

        public TeamStrength(int count, float skillMu, float skillVar, int score)
        {
            Count = count; SkillMu = skillMu; SkillVar = skillVar; Score = score;
        }
    }

    /// <summary>
    /// QC teamplay.qc:840-931 (TeamBalance_GetTeamCounts inner loop): tally a team's player count and its
    /// inverse-variance weighted mean skill from <see cref="SkillProvider"/>, plus its current score. With the
    /// flat per-player variance stand-in, <c>mass</c> is the rated-player count, the weighted mean is the plain
    /// mean skill, and <c>m_skill_var = 1/mass</c>. Excludes <paramref name="ignore"/> (the joining/switching
    /// player is scored against the team it would join, not counted as already on it).
    /// </summary>
    private TeamStrength MeasureTeam(int team, IReadOnlyList<Player> roster, Player? ignore)
    {
        int count = 0;
        float muSum = 0f, mass = 0f;
        for (int i = 0; i < roster.Count; i++)
        {
            Player p = roster[i];
            if (ReferenceEquals(p, ignore)) continue;
            if ((int)p.Team != team) continue;
            count++;
            float mu = SkillProvider(p);
            if (mu != 0f)
            {
                float weight = 1f / SkillVariance; // QC skill_weight = 1 / m_skill_var
                muSum += mu * weight;
                mass += weight;
            }
        }
        float skillMu = mass > 0f ? muSum / mass : 0f;     // QC m_skill_mu /= mass
        float skillVar = mass > 0f ? 1f / mass : 0f;       // QC m_skill_var = 1 / mass
        int score = _scores?.TeamScore(team) ?? 0;
        return new TeamStrength(count, skillMu, skillVar, score);
    }

    /// <summary>
    /// QC <c>TeamBalance_CompareTeamsInternal</c> (teamplay.qc:1299): order two candidate teams for a joiner —
    /// negative if <paramref name="a"/> is the better (preferable, "lesser") team. Player count dominates; on a
    /// tie, team strength (skill mu + score) breaks it, but skill only counts when the gap is statistically
    /// significant (z-score > <c>g_balance_teams_skill_significance_threshold</c> SDs, squared for perf), exactly
    /// like Base. Falls back to the lowest fixed index when fully equal (the foreach keeps the first seen).
    /// </summary>
    private int CompareTeams(in TeamStrength a, in TeamStrength b)
    {
        if (a.Count != b.Count)
            return a.Count < b.Count ? -1 : 1; // QC: fewer players => preferable.

        // QC: equal size — compare skill+score "strength". Skill applies only when significant.
        bool skillEnabled = SkillWeightingEnabled;
        float threshold = (Api.Services is not null)
            ? Api.Cvars.GetFloat("g_balance_teams_skill_significance_threshold")
            : 0f;
        bool useSkill = skillEnabled && a.SkillVar != 0f && b.SkillVar != 0f
            && ((a.SkillMu - b.SkillMu) * (a.SkillMu - b.SkillMu)) / (a.SkillVar + b.SkillVar)
               > threshold * threshold;

        float strengthA = useSkill ? a.SkillMu : 1f;
        float strengthB = useSkill ? b.SkillMu : 1f;

        // QC the score ramp uses time-into-match; without that scalar here we apply the team score directly as a
        // strength bias (a team that is winning is "stronger" so the joiner avoids it), preserving the lower-score
        // tiebreak the port already had.
        strengthA *= 1f + a.Score * 0.0001f;
        strengthB *= 1f + b.Score * 0.0001f;

        if (strengthA < strengthB) return -1; // QC: weaker team => preferable.
        if (strengthA > strengthB) return 1;
        return 0;
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

    /// <summary>
    /// QC <c>TeamBalance_CheckAllowedTeams</c> (the bot_vs_human branch): which of the active teams (in
    /// <see cref="Teams.Active"/> order, indexed 0..TeamCount-1) <paramref name="forWhom"/> may join. Normally all
    /// teams are allowed; with <c>bot_vs_human</c> set on a 2-team match, bots are banned to one side and humans
    /// to the other (positive ratio → bots take the LAST team / humans the first; negative → bots the FIRST team
    /// / humans the last), so the join only ever offers the "correct" side. <paramref name="forWhom"/> null (a
    /// global query) leaves all teams allowed.
    /// </summary>
    private bool[] AllowedTeams(Player? forWhom)
    {
        var allowed = new bool[TeamCount];
        for (int i = 0; i < TeamCount; i++) allowed[i] = true;

        if (forWhom is null || TeamCount != 2 || Api.Services is null)
            return allowed;

        float ratio = Api.Cvars.GetFloat("bot_vs_human");
        if (ratio == 0f)
            return allowed;

        // QC: positive → bots take the LAST available team (index 1), humans the first (index 0).
        //      negative → bots take the FIRST available team (index 0), humans the last (index 1).
        bool botsLast = ratio > 0f;
        int botTeamIdx = botsLast ? 1 : 0;
        int humanTeamIdx = botsLast ? 0 : 1;
        int keep = forWhom.IsBot ? botTeamIdx : humanTeamIdx;
        for (int i = 0; i < TeamCount; i++)
            allowed[i] = (i == keep);
        return allowed;
    }

    /// <summary>
    /// QC <c>TeamBalance_SizeDifference</c>: the gap between the largest and smallest active team's player count
    /// over <paramref name="roster"/> (0 for a non-team game). Used by the unbalanced-teams nag and the warmup
    /// hold (warmup won't end while teams are this uneven).
    /// </summary>
    public int SizeDifference(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame)
            return 0;
        int min = int.MaxValue, max = int.MinValue;
        foreach (int team in Teams.Active(TeamCount))
        {
            int c = TeamBalance.CountTeam(team, roster);
            if (c < min) min = c;
            if (c > max) max = c;
        }
        return max == int.MinValue ? 0 : max - min;
    }

    /// <summary>
    /// QC <c>sv_teamnagger</c> trigger: true when the team-size gap (<see cref="SizeDifference"/>) is at least
    /// <c>sv_teamnagger</c> players (Base default 2), i.e. the unbalanced-teams nag should show and warmup should
    /// be held open. Returns false when <c>sv_teamnagger</c> is 0 (nagger disabled) or for a non-team game.
    /// </summary>
    public bool TeamsUnbalancedForNag(IReadOnlyList<Player> roster)
    {
        if (!IsTeamGame || Api.Services is null)
            return false;
        // QC: (teamplay && total_players && autocvar_sv_teamnagger) ? ... : false — sv_teamnagger 0 disables the
        // nag (and the warmup badteams hold). The cvar is registered (default 2), so an explicit 0 is honored.
        float nagger = Api.Cvars.GetFloat("sv_teamnagger");
        if (nagger <= 0f)
            return false;
        return SizeDifference(roster) >= (int)nagger;
    }

    /// <summary>
    /// QC <c>g_balance_teams_prevent_imbalance</c> mid-match switch gate (cmd.qc:746): may
    /// <paramref name="switcher"/> manually switch to <paramref name="targetTeam"/> (a color code) right now? When
    /// the cvar is set and not in warmup, a human may only switch to a team that is a <em>best</em> team (smallest
    /// active team after the move, by the same count/score rule as <see cref="AssignBestTeam"/>), so they can't
    /// jump to the larger/stronger side. Bots, warmup, and a disabled cvar always allow the switch. The caller
    /// (the team/join command handler) should reject the switch when this returns false.
    /// </summary>
    public bool IsTeamAllowedForSwitch(Player switcher, int targetTeam, IReadOnlyList<Player> roster, bool isWarmup)
    {
        if (!IsTeamGame || switcher.IsBot || isWarmup)
            return true;
        if (Api.Services is null || Api.Cvars.GetFloat("g_balance_teams_prevent_imbalance") == 0f)
            return true;

        bool[] allowed = AllowedTeams(switcher);

        // QC TeamBalance_FindBestTeams: compute the best (by the size→strength ladder) candidate, then accept the
        // target only if it ties that best — i.e. switching there doesn't make teams more unbalanced.
        bool haveBest = false;
        TeamStrength best = default;
        TeamStrength target = default;
        bool haveTarget = false;

        int ti = 0;
        foreach (int team in Teams.Active(TeamCount))
        {
            bool isAllowed = allowed[ti++];
            if (!isAllowed)
            {
                if (team == targetTeam) return false; // banned team (bot_vs_human) is never switchable.
                continue;
            }
            TeamStrength cand = MeasureTeam(team, roster, switcher);
            if (!haveBest || CompareTeams(cand, best) < 0)
            {
                best = cand;
                haveBest = true;
            }
            if (team == targetTeam)
            {
                target = cand;
                haveTarget = true;
            }
        }

        // Target ties the best team (CompareTeams == 0 means equally preferable) → allow.
        return haveTarget && haveBest && CompareTeams(target, best) <= 0;
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
