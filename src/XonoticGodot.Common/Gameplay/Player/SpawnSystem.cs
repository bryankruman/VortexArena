using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// A resolved spawn location — the QC spawnpoint edict (info_player_deathmatch / info_player_start /
/// info_player_team*) reduced to what placement needs: an <see cref="Origin"/>, facing
/// <see cref="Angles"/>, and the owning <see cref="Team"/> (0 = no team / FFA). Kept as a small struct so
/// selection is allocation-free; the underlying world <see cref="Entity"/> is carried for callers that want
/// to mark it used or read more fields later.
/// </summary>
public readonly struct SpawnPoint
{
    public readonly Vector3 Origin;
    public readonly Vector3 Angles;
    public readonly float Team;
    public readonly Entity? Source;

    public SpawnPoint(Vector3 origin, Vector3 angles, float team = 0f, Entity? source = null)
    {
        Origin = origin;
        Angles = angles;
        Team = team;
        Source = source;
    }

    public bool IsValid => Source is not null || Origin != Vector3.Zero || Angles != Vector3.Zero;
}

/// <summary>
/// The spawn lifecycle — a Godot-free port of <c>SelectSpawnPoint</c> / <c>Spawn_Score</c>
/// (server/spawnpoints.qc) and <c>PutPlayerInServer</c> (server/client.qc). This is the "spawn at a
/// spawnpoint with a loadout" half of the DM match loop.
///
/// Faithfully ported:
///  - spawnpoint discovery across the QC classnames (info_player_deathmatch / info_player_start /
///    info_player_team*), via <see cref="IEntityService.FindByClass"/>;
///  - Spawn_Score's idea: each spot is scored by the distance to the NEAREST live player (a spot far from
///    everyone scores higher), with a priority bonus when that distance exceeds <see cref="SpawnMinDist"/>
///    (QC SPAWN_PRIO_GOOD_DISTANCE / mindist=100), bad spots filtered out (Spawn_FilterOutBadSpots);
///  - Spawn_WeightedPoint's 50/50 split between a near-random pick and a strongly-far-biased pick
///    (the "won't get spawn-fragged twice in a row" rule), via the weighted random in
///    <see cref="WeightedPick"/>;
///  - Spawn_Score's wrong-team / inactive / restriction filters (teamcheck, ACTIVE_ACTIVE, restriction 1/2);
///  - PutPlayerInServer's reset: MOVETYPE_WALK, SOLID, DAMAGE_AIM, FL_CLIENT, DEAD_NO, frags baseline,
///    start health/armor/ammo loadout (SetResource), the player bbox (PL_MIN/MAX_CONST), velocities/angles
///    cleared, placement at the spot (origin nudged +1 like the QC setorigin), the spawn shield
///    (Entity.SpawnShieldExpire = time + g_spawnshieldtime), start_items, and the random-start-weapons set.
///
/// NOTE — genuinely cross-boundary (owned by other systems / agents): the assault target spawn_evalfunc
/// chains and race target requirement (need the assault/race objective graph + race-mode flag), the
/// move-out-of-solid relocation (relocate_spawnpoint), the random-weapon AMMO grant + superweapon timer
/// (need a NetName→ammo/superweapon weapon registry), the countdown shield/pause extension (needs the
/// MatchController start-time clock), weapon view entities (CL_SpawnWeaponentity) + SpawnEvent networking
/// (client/net), and the warmup loadout path. Each is flagged precisely at its call site.
/// </summary>
public static class SpawnSystem
{
    /// <summary>The QC spawnpoint classnames searched, in priority order (info_player_deathmatch is canonical).</summary>
    public static readonly string[] SpawnClassNames =
    {
        "info_player_deathmatch",
        "info_player_start",
        "info_player_team1",
        "info_player_team2",
        "info_player_team3",
        "info_player_team4",
    };

    /// <summary>QC mindist arg to Spawn_FilterOutBadSpots (server/spawnpoints.qc): 100 qu.</summary>
    public const float SpawnMinDist = 100f;

    /// <summary>QC SPAWN_PRIO_GOOD_DISTANCE (server/spawnpoints.qh).</summary>
    public const int PrioGoodDistance = 10;

    /// <summary>QC PL_MIN_CONST (common/constants.qh).</summary>
    public static readonly Vector3 PlayerMins = new(-16f, -16f, -24f);

    /// <summary>QC PL_MAX_CONST (common/constants.qh).</summary>
    public static readonly Vector3 PlayerMaxs = new(16f, 16f, 45f);

    // ----- starting-loadout cvar names + defaults (Xonotic balance-xonotic.cfg; OPEN Q5 names kept) -----
    private const string CvarHealthStart = "g_balance_health_start"; // 100
    private const string CvarArmorStart  = "g_balance_armor_start";  // 0
    private const string CvarAmmoShells  = "g_start_ammo_shells";
    private const string CvarAmmoNails   = "g_start_ammo_nails";
    private const string CvarAmmoRockets = "g_start_ammo_rockets";
    private const string CvarAmmoCells   = "g_start_ammo_cells";
    private const string CvarAmmoFuel    = "g_start_ammo_fuel";

    private const float DefHealthStart = 100f;
    private const float DefArmorStart  = 0f;

    // spawn shield (QC g_spawnshieldtime, xonotic-server.cfg default 1s; the damage pipeline reads the expiry).
    private const string CvarSpawnShieldTime = "g_spawnshieldtime";
    private const float  DefSpawnShieldTime  = 1f;

    // player model (QC FixPlayermodel → setplayermodel; server/client.qc). sv_defaultplayermodel is the universal
    // default; sv_defaultplayerskin the skin. DefaultPlayerModel is the hardcoded fallback (xonotic-server.cfg:105).
    private const string CvarDefaultPlayerModel = "sv_defaultplayermodel";
    private const string CvarDefaultPlayerSkin  = "sv_defaultplayerskin";
    public  const string DefaultPlayerModel     = "models/player/erebus.iqm";

    // random-start-weapons + start_items (QC server/world.qc; defaults make these no-ops in stock DM).
    private const string CvarRandomStartCount    = "g_random_start_weapons_count"; // 0
    private const string CvarRandomStartWeapons  = "g_random_start_weapons";        // weapon NetName list
    private const string CvarStartItems          = "g_start_items";                 // start_items bitfield, 0
    // per-resource random-start ammo (QC random_start_ammo: SetResource(RES_*, g_random_start_*)).
    private const string CvarRandomStartShells   = "g_random_start_shells";
    private const string CvarRandomStartBullets  = "g_random_start_bullets";
    private const string CvarRandomStartRockets  = "g_random_start_rockets";
    private const string CvarRandomStartCells    = "g_random_start_cells";
    // superweapon timer (QC g_balance_superweapons_time): how long a held superweapon lasts before it's removed.
    private const string CvarSuperweaponsTime    = "g_balance_superweapons_time";
    private const float  DefSuperweaponsTime     = 30f;

    /// <summary>
    /// QC <c>start_weapons</c> for the stock DM loadout: just the Blaster (the always-available sidearm).
    /// Other weapons are picked up from the map. Kept as NetNames to match <see cref="Player.OwnedWeapons"/>.
    /// </summary>
    public static readonly string[] DefaultLoadout = { "blaster" };

    /// <summary>Deterministic RNG for spawn selection (QC random()). Seeded so headless sims are reproducible.</summary>
    private static Random _rng = new(0x5EED);

    /// <summary>Reseed the spawn RNG (test/determinism support — the sim sets this from the world seed).</summary>
    public static void Reseed(int seed) => _rng = new Random(seed);

    // ============================================================================================
    //  Team-spawn state — port of server/spawnpoints.qh globals + the spawnpoints.qc link/select logic.
    //  have_team_spawns (tri-state): 0 = no team spawns requested, -1 = requested but none found, 1 =
    //  requested and found. have_team_spawns_forteams: bit X set ⇒ team X has at least one spawnpoint
    //  (team 0 = the "no-team" spots). some_spawn_has_been_used: a team spawn was claimed by a control point.
    // ============================================================================================

    /// <summary>QC <c>have_team_spawns</c> (server/spawnpoints.qh:16): 0 none requested, -1 requested/none found, 1 found.</summary>
    public static int HaveTeamSpawns;

    /// <summary>QC <c>have_team_spawns_forteams</c>: bit X set ⇒ team X has spawns (team 0 = no-team spots).</summary>
    public static int HaveTeamSpawnsForTeams;

    /// <summary>QC <c>some_spawn_has_been_used</c>: a team spawn was claimed (Onslaught control-point capture).</summary>
    public static bool SomeSpawnHasBeenUsed;

    /// <summary>True once <see cref="RequestTeamSpawns"/> was called for this match (host set the request explicitly).</summary>
    private static bool _teamSpawnsRequestExplicit;

    /// <summary>
    /// Port of <c>GameRules_spawning_teams(value)</c> (common/gametypes/sv_rules.qh): the active gametype declares
    /// whether it wants team-only spawnpoints. Sets <see cref="HaveTeamSpawns"/> to -1 (requested) or 0 (not), and
    /// records that the request was made explicitly (so <see cref="SelectSpawnPoint"/> trusts it over the auto
    /// fallback). The host calls this once when a gametype activates, passing
    /// <see cref="GameType.RequestsTeamSpawns"/>. The "found" upgrade to 1 happens during spawnpoint detection.
    /// </summary>
    public static void RequestTeamSpawns(bool value)
    {
        HaveTeamSpawns = value ? -1 : 0;       // QC: have_team_spawns = (value) ? -1 : 0
        _teamSpawnsRequestExplicit = true;
    }

    /// <summary>
    /// Reset the team-spawn globals (QC: cleared per map load). Call when changing maps/gametypes so a stale
    /// "found" latch / request flag from a prior match doesn't leak into the next one. (The forteams mask is
    /// recomputed from the live spots each selection, so it never goes stale on its own.)
    /// </summary>
    public static void ResetTeamSpawns()
    {
        HaveTeamSpawns = 0;
        HaveTeamSpawnsForTeams = 0;
        SomeSpawnHasBeenUsed = false;
        _teamSpawnsRequestExplicit = false;
    }

    /// <summary>
    /// Port of the team assignment in the <c>spawnfunc(info_player_teamN)</c> bodies (server/spawnpoints.qc:201-234):
    /// each <c>info_player_teamN</c> sets <c>this.team = NUM_TEAM_N</c> before chaining to info_player_deathmatch.
    /// In this port spawnpoints have no spawnfunc (they're kept as passive edicts), so the team is carried by the
    /// classname when no explicit <c>team</c> key was on the map entity. Returns <see cref="Teams.None"/> for a
    /// non-team spawn class (info_player_deathmatch / info_player_start).
    /// </summary>
    public static float TeamForSpawnClass(string className) => className switch
    {
        "info_player_team1" => Teams.Red,    // QC NUM_TEAM_1
        "info_player_team2" => Teams.Blue,   // QC NUM_TEAM_2
        "info_player_team3" => Teams.Yellow, // QC NUM_TEAM_3
        "info_player_team4" => Teams.Pink,   // QC NUM_TEAM_4
        _ => Teams.None,
    };

    /// <summary>
    /// Port of the team-spawn detection tail of <c>relocate_spawnpoint</c> (server/spawnpoints.qc:159-165): over the
    /// gathered spots, ensure each carries its team (from the classname when the map left it unset), then fold into
    /// the globals — <c>if (have_team_spawns != 0) if (this.team) have_team_spawns = 1;</c> and
    /// <c>have_team_spawns_forteams |= BIT(this.team)</c>. Recomputed from scratch each selection (idempotent and
    /// O(spots) — selection only, never per-frame) so a map change can't leave a stale forteams mask; it always
    /// reflects the spots currently on the map, matching QC's link-time computation. <see cref="HaveTeamSpawns"/>
    /// stays sticky at -1/0 (the gametype's request) until a team spot upgrades it to 1.
    /// </summary>
    private static void DetectTeamSpawns(List<Entity> spots)
    {
        int forTeams = 0;
        bool anyTeamSpot = false;
        foreach (Entity spot in spots)
        {
            // relocate_spawnpoint runs before the spawnfunc in QC; here apply the classname team when the map
            // entity didn't already provide one (info_player_teamN with no explicit "team" key).
            if (spot.Team == Teams.None)
            {
                float t = TeamForSpawnClass(spot.ClassName);
                if (t != Teams.None)
                    spot.Team = t;
            }

            forTeams |= TeamBit(spot.Team);   // QC: have_team_spawns_forteams |= BIT(this.team)
            if (spot.Team != Teams.None)
                anyTeamSpot = true;
        }
        HaveTeamSpawnsForTeams = forTeams;

        // QC: if (have_team_spawns != 0) if (this.team) have_team_spawns = 1; — team spawns were requested
        // (-1) or already found (1), and at least one spot has a team ⇒ "found" (1).
        if (HaveTeamSpawns != 0 && anyTeamSpot)
            HaveTeamSpawns = 1;
    }

    /// <summary>
    /// QC <c>BIT(this.team)</c> over a spawnpoint's team color. Team 0 (no-team) maps to bit 0; a real team color
    /// (<see cref="Teams.Red"/>=4 … <see cref="Teams.Pink"/>=9) maps to its own bit. Internally consistent with
    /// <see cref="Player.Team"/> (same color encoding), which is all the <c>have_team_spawns_forteams</c> test needs.
    /// </summary>
    private static int TeamBit(float team)
    {
        int t = (int)team;
        if (t < 0 || t > 30) return 0; // guard a bogus team value (BIT shift in range)
        return 1 << t;
    }

    /// <summary>
    /// Port of the <c>teamcheck</c> computation in <c>SelectSpawnPoint</c> (server/spawnpoints.qc:375-396).
    /// Given the requesting player's <see cref="Entity.Team"/> and the team-spawn globals, returns the team a
    /// spot must match (or -1 for "any spot"). Mirrors the QC branch ladder exactly:
    /// <list type="bullet">
    /// <item>useallspawns / anypoint ⇒ -1 (any spot).</item>
    /// <item>team spawns found: if the player's team has spawns ⇒ MUST be that team; else fall back to the
    ///   no-team spots if they exist, else any spot.</item>
    /// <item>no team spawns but no-team spots exist ⇒ MUST be a no-team spot (0).</item>
    /// <item>otherwise ⇒ -1 (require team spawns but have none, or require non-team and have none: use any).</item>
    /// </list>
    /// </summary>
    private static float ComputeTeamCheck(Player forPlayer, bool anyPoint)
    {
        // QC: if (anypoint || autocvar_g_spawn_useallspawns) teamcheck = -1;
        if (anyPoint || CvarBool("g_spawn_useallspawns", false))
            return -1f;

        // If the host never explicitly requested team spawns (no RequestTeamSpawns call), fall back to deriving
        // the request from the player: a team game assigns a valid team, FFA leaves it None. This keeps the team
        // gametypes spawning on team spots out of the box even before the host wires the per-mode request.
        if (!_teamSpawnsRequestExplicit && HaveTeamSpawns == 0
            && IsValidTeam(forPlayer.Team) && (HaveTeamSpawnsForTeams & ~BitZero) != 0)
        {
            HaveTeamSpawns = 1; // a team player + team spots present ⇒ treat as requested-and-found
        }

        int playerBit = TeamBit(forPlayer.Team);

        if (HaveTeamSpawns > 0)
        {
            // QC: if (!(have_team_spawns_forteams & BIT(this.team)))
            if ((HaveTeamSpawnsForTeams & playerBit) == 0)
            {
                // the player's team has no spawns: try the no-team spots, else any spot.
                if ((HaveTeamSpawnsForTeams & BitZero) != 0)
                    return 0f;       // QC: try noteam spawns
                return -1f;          // QC: if not, any spawn has to do
            }
            return forPlayer.Team;   // QC: teamcheck = this.team — MUST be team
        }

        // QC: else if (have_team_spawns == 0 && (have_team_spawns_forteams & BIT(0))) teamcheck = 0; // MUST be noteam
        if (HaveTeamSpawns == 0 && (HaveTeamSpawnsForTeams & BitZero) != 0)
            return 0f;

        // QC: else teamcheck = -1; — require team spawns but have none, or require non-team and have none.
        return -1f;
    }

    /// <summary>QC <c>BIT(0)</c>: the bit for the "no-team" (team 0) spawnpoints.</summary>
    private const int BitZero = 1; // 1 << 0

    /// <summary>Sentinel for the <c>teamCheck</c> parameter meaning "derive teamcheck from the player + globals".</summary>
    private const float AutoTeamCheck = float.NaN;

    /// <summary>QC <c>Team_IsValidTeam</c> (common/teams.qh): one of the four real team colors (not None / spectator).</summary>
    private static bool IsValidTeam(float team)
    {
        int t = (int)team;
        return t == Teams.Red || t == Teams.Blue || t == Teams.Yellow || t == Teams.Pink;
    }

    /// <summary>
    /// Port of <c>SelectSpawnPoint(this, anypoint)</c> (server/spawnpoints.qc): gather every spawnpoint, derive
    /// the team filter, score each by distance to the nearest live player, drop the unusable ones, and
    /// weighted-random-pick among the survivors (50/50 near-random vs. far-biased). Returns null only when no
    /// spawnpoint exists at all.
    /// </summary>
    /// <param name="forPlayer">The player being spawned (excluded from the "nearest other player" check; its
    /// <see cref="Entity.Team"/> drives the team-spawn filter when <paramref name="teamCheck"/> is auto).</param>
    /// <param name="livePlayers">The currently-alive players to keep new spawns away from.</param>
    /// <param name="teamCheck">
    /// QC <c>teamcheck</c>: when &gt;= 0 only spots whose <see cref="Entity.Team"/> equals it are eligible (the
    /// teamplay team-spawn path, <c>have_team_spawns</c>); -1 is the FFA path (any spot). The default
    /// (<see cref="AutoTeamCheck"/>) DERIVES teamcheck from the player's team + the team-spawn globals exactly as
    /// QC's SelectSpawnPoint does — that is the normal path; an explicit value is for tests / forced spots.
    /// </param>
    /// <param name="targetCheck">
    /// QC <c>targetcheck</c> (the 2nd arg to SelectSpawnPoint): enables the ACTIVE_ACTIVE gate and the
    /// assault <c>spawn_evalfunc</c> target chain. Plain DM passes false; assault/onslaught pass true.
    /// </param>
    /// <param name="anyPoint">QC <c>anypoint</c>: ignore team/target filtering and pick from every spot (the
    /// <c>g_spawn_useallspawns</c> / testspawn path). Forces <paramref name="teamCheck"/> to -1.</param>
    public static SpawnPoint? SelectSpawnPoint(Player forPlayer, IReadOnlyList<Player> livePlayers,
        float teamCheck = AutoTeamCheck, bool targetCheck = false, bool anyPoint = false)
    {
        var spots = GatherSpawnPoints();
        if (spots.Count == 0)
            return null;

        // Port of the relocate_spawnpoint team-spawn detection tail (spawnpoints.qc:159-165): assign each spot
        // its classname team + fold into have_team_spawns / have_team_spawns_forteams. QC computes this once at
        // link time; here it's recomputed from the live spots each selection (idempotent, O(spots)).
        DetectTeamSpawns(spots);

        // Port of the teamcheck branch ladder in SelectSpawnPoint (spawnpoints.qc:375-396). When the caller left
        // teamCheck at the auto sentinel (the normal path), derive it from the player's team + the globals; an
        // explicit value (tests / a forced team) is honored as-is.
        float resolvedTeamCheck = float.IsNaN(teamCheck) ? ComputeTeamCheck(forPlayer, anyPoint)
                                : (anyPoint ? -1f : teamCheck);

        // Spawn_ScoreAll + Spawn_FilterOutBadSpots: score each spot, keep prio >= 0.
        // score = (prio, weight): weight is the distance to the nearest live OTHER player.
        var scored = new List<(Entity spot, float prio, float weight)>(spots.Count);
        foreach (var spot in spots)
        {
            (float prio, float weight) = ScoreSpot(forPlayer, spot, livePlayers, resolvedTeamCheck, targetCheck);
            if (prio >= 0f)
                scored.Add((spot, prio, weight));
        }

        // QC: "note this returns the original list if none survived" — fall back to every spot unscored-ish.
        if (scored.Count == 0)
        {
            foreach (var spot in spots)
                scored.Add((spot, 0f, 0f));
        }

        // QC SelectSpawnPoint: with probability (1 - g_spawn_furthest) use a near-uniform pick (1,1,1); otherwise
        // a strongly-far-biased pick (1,5000,5) — `if (random() > g_spawn_furthest)` takes the near branch, so a
        // HIGHER g_spawn_furthest means MORE spawns far from players. Default 0.5 = 50/50 (identical to before).
        Entity chosen = _rng.NextDouble() > Cvar("g_spawn_furthest", 0.5f)
            ? WeightedPick(scored, lower: 1f, upper: 1f, exponent: 1f)
            : WeightedPick(scored, lower: 1f, upper: 5000f, exponent: 5f);

        return ToSpawnPoint(chosen);
    }

    /// <summary>
    /// Port of <c>Spawn_Score</c> (server/spawnpoints.qc), single-FFA path. Returns (prio, weight):
    /// weight = distance to the nearest live other player (a large, world-sized value if none);
    /// prio = <see cref="PrioGoodDistance"/> when that distance exceeds <see cref="SpawnMinDist"/>, else 0.
    /// A negative prio (none produced here yet) would mark the spot unusable.
    /// </summary>
    private static (float prio, float weight) ScoreSpot(Player forPlayer, Entity spot,
        IReadOnlyList<Player> livePlayers, float teamCheck, bool targetCheck)
    {
        // Spawn_Score filters (server/spawnpoints.qc:239), all returning '-1 0 0' (unusable):
        //  - wrong team: teamcheck >= 0 && spot.team != teamcheck
        //  - inactive (only when target-checking): spot.active != ACTIVE_ACTIVE && targetcheck
        //  - restriction: real client rejected by restriction==1; bot rejected by restriction==2
        if (teamCheck >= 0f && spot.Team != teamCheck)
            return (-1f, 0f);
        if (spot.SpawnActive != MapMover.ActiveActive && targetCheck)
            return (-1f, 0f);
        if (!forPlayer.IsBot)
        {
            if (spot.SpawnRestriction == 1) return (-1f, 0f); // bots-only spot
        }
        else
        {
            if (spot.SpawnRestriction == 2) return (-1f, 0f); // real-clients-only spot
        }

        // Spawn_FilterOutBadSpots (server/spawnpoints.qc): discard a spot where the player hull would spawn
        // EMBEDDED IN SOLID. QC traceboxes the spot's player-box and drops it on trace_startsolid; without this
        // an in-solid spawnpoint gets picked and the player is stuck (origin frozen, velocity blows up). Check
        // at the SAME placement the player lands at — spot.origin + the PutPlayerInServer z-nudge.
        if (Api.Services is not null)
        {
            Vector3 place = spot.Origin + new Vector3(0f, 0f, 1f - PlayerMins.Z - 24f);
            TraceResult bad = Api.Trace.Trace(place, PlayerMins, PlayerMaxs, place, MoveFilter.Normal, forPlayer);
            if (bad.StartSolid)
                return (-1f, 0f);
        }

        // NOTE: the race target requirement (race_spawns ⇒ spot must have a target) and the assault
        // spawn_evalfunc target chain (findchain(targetname, spot.target) → targ.spawn_evalfunc) are
        // genuinely cross-boundary: they need the race-mode flag and the target-graph + assault objective
        // entities those gametypes own. Plain DM passes targetCheck=false, so neither is reachable here.

        // QC seeds 'shortest' with vlen(world.maxs - world.mins); without world bounds here we use a large
        // constant so a spot with no nearby players still gets a high (good) weight.
        float shortest = 1_000_000f;
        for (int i = 0; i < livePlayers.Count; i++)
        {
            Player other = livePlayers[i];
            // Skip self, the dead, and observers (a free-fly spectator has DeadState==No but isn't a live
            // player — QC IS_PLAYER excludes spectators, so they must not bias spawn distance scoring).
            if (ReferenceEquals(other, forPlayer) || other.IsDead || other.IsObserver)
                continue;
            float d = Vector3.Distance(other.Origin, spot.Origin);
            if (d < shortest)
                shortest = d;
        }

        float prio = shortest > SpawnMinDist ? PrioGoodDistance : 0f;

        // MUTATOR_CALLHOOK(Spawn_Score, player, spawn_spot, spawn_score) — server/spawnpoints.qc Spawn_Score.
        // Mutators bias a spot's priority here: spawn_unique demotes a repeat spot, spawn_near_teammate
        // promotes spots near a live teammate. QC packs (prio, weight) into the spawn_score vector's (x, y);
        // the port keeps them as the two floats returned below, so the hook exposes both and reads them back.
        var ss = new MutatorHooks.SpawnScoreArgs(forPlayer, spot, prio, shortest);
        MutatorHooks.SpawnScore.Call(ref ss);
        prio = ss.Priority;
        shortest = ss.Weight;

        return (prio, shortest);
    }

    /// <summary>
    /// Port of <c>Spawn_WeightedPoint</c> (server/spawnpoints.qc): weighted random selection where a spot's
    /// weight is <c>bound(lower, distance, upper) ^ exponent</c>, and the priority bonus is folded in as in
    /// QC's RandomSelection (prio contributes a tie-break "preference" so good-distance spots win ties). The
    /// classic Quake reservoir trick (RandomSelection_AddEnt): keep each candidate with probability
    /// weight/runningTotal.
    /// </summary>
    private static Entity WeightedPick(List<(Entity spot, float prio, float weight)> scored, float lower, float upper, float exponent)
    {
        Entity? chosen = null;
        float bestPriority = -1f;
        float total = 0f;

        foreach (var (spot, prio, weight) in scored)
        {
            float w = MathF.Pow(QBound(lower, weight, upper), exponent);
            // QC priority: (weight >= lower) * 0.5 + prio — higher-priority candidates are preferred and
            // a strictly-higher priority resets the reservoir (never undercut by a lower-priority pick).
            float priority = (weight >= lower ? 0.5f : 0f) + prio;

            if (priority > bestPriority)
            {
                // a better priority tier appears: restart the weighted reservoir on it
                bestPriority = priority;
                total = w;
                chosen = spot;
            }
            else if (priority == bestPriority)
            {
                total += w;
                if (w > 0f && _rng.NextDouble() * total <= w)
                    chosen = spot;
            }
        }

        return chosen ?? scored[0].spot;
    }

    /// <summary>Collect all spawnpoint entities across the known classnames (deduped, stable order).</summary>
    private static List<Entity> GatherSpawnPoints()
    {
        var list = new List<Entity>();
        if (Api.Services is null)
            return list;

        foreach (string cls in SpawnClassNames)
        {
            foreach (Entity e in Api.Entities.FindByClass(cls))
            {
                if (e is { IsFreed: false } && !list.Contains(e))
                    list.Add(e);
            }
        }
        return list;
    }

    private static SpawnPoint ToSpawnPoint(Entity spot)
    {
        // QC never spawns tilted even if the spot says to: angles_z = 0.
        Vector3 a = spot.Angles;
        return new SpawnPoint(spot.Origin, new Vector3(a.X, a.Y, 0f), spot.Team, spot);
    }

    /// <summary>
    /// Port of <c>PutPlayerInServer</c> (server/client.qc), Godot-free core: reset the player's physics
    /// state, give the starting loadout, clear the dead state, and place it at <paramref name="sp"/>.
    /// Faithfully mirrors the QC field assignments in order; deferred mechanics flagged inline.
    /// </summary>
    public static void PutPlayerInServer(Player p, SpawnPoint sp, bool warmup = false)
    {
        // --- physics / solidity (QC: set_movetype WALK; solid SLIDEBOX; takedamage AIM; flags FL_CLIENT) ---
        p.MoveType = MoveType.Walk;
        p.Solid = Solid.SlideBox;          // QC SOLID_SLIDEBOX (a moving player hull; not SOLID_BBOX)
        p.TakeDamage = DamageMode.Aim;     // QC DAMAGE_AIM (autoaim-eligible, takes damage)
        p.Flags = EntFlags.Client;         // QC this.flags = FL_CLIENT | FL_PICKUPITEMS (no FL_PICKUPITEMS enum yet)
        p.DeadState = DeadFlag.No;         // QC this.deadflag = DEAD_NO

        // QC: the live edict is REUSED on respawn (the corpse is a separate CopyBody clone), so it must come
        // back as a clean LIVE player. DamageSystem.Killed set IsCorpse=true (routing further hits to the corpse
        // path) and may have gibbed it (Alpha=-1) or set the corpse ballistics density — none of which were ever
        // reset. Without this, after the first death+respawn the player stayed flagged a corpse forever: it could
        // never re-enter PlayerDamage (so it couldn't die again or award a frag) and was bullet-penetrable.
        p.IsCorpse = false;
        p.Alpha = 1f;                      // QC default_player_alpha — fully visible (un-gib)
        p.BallisticsDensity = 0f;          // QC live-player density (corpse density reset)
        p.RespawnFlags = RespawnFlag.None; // QC this.respawn_flags = 0
        p.RespawnTimeMax = 0f;
        p.RespawnCountdown = 0;
        // QC client.qc:676 this.damageforcescale = autocvar_g_player_damageforcescale (default 2). Every knockback
        // consumer (base damage push AND the globalforces mutator) multiplies by the victim's damageforcescale, so
        // a player spawned with 0 here takes NO knockback — must seed it on (re)spawn.
        p.DamageForceScale = Cvar("g_player_damageforcescale", 2f);

        // QC: this.frags = FRAGS_PLAYER on every (re)spawn. The .frags field is the player STATUS sentinel
        // (FRAGS_PLAYER / FRAGS_SPECTATOR / FRAGS_OUT_OF_GAME), NOT the match score — the running score lives in
        // the real score table (Player.ScoreFrags -> GameScores SP_SCORE), so resetting status here no longer
        // wipes score progress. Restores the QC semantics the scores-table port enables.
        p.FragsStatus = Player.FragsPlayer;

        // --- starting loadout (QC PutPlayerInServer: warmup vs non-warmup branch) ---
        if (warmup)
            ApplyWarmupLoadout(p);
        else
            ApplyStartLoadout(p);

        // --- respawn bookkeeping cleared (QC: death_time/respawn_flags/respawn_time = 0) ---
        p.RespawnTime = 0f;

        // --- think cleared: "players have no think function" (QC setthink func_null; nextthink 0) ---
        p.Think = null;
        p.NextThink = 0f;

        // --- orientation + motion reset (QC angles = spot.angles; angles_z = 0; velocities/punch cleared) ---
        p.Angles = sp.Angles;              // already roll-zeroed in ToSpawnPoint
        p.Velocity = Vector3.Zero;
        p.OldOrigin = Vector3.Zero;        // recomputed below after placement
        p.AVelocity = Vector3.Zero;
        p.PunchAngle = Vector3.Zero;
        p.WaterLevel = 0;                  // QC WATERLEVEL_NONE
        p.WaterType = (int)Contents.Empty; // QC CONTENT_EMPTY

        // --- bbox + placement (QC setsize PL_MIN/MAX; setorigin spot.origin + '0 0 1'*(1-mins.z-24)) ---
        if (Api.Services is not null)
            Api.Entities.SetSize(p, PlayerMins, PlayerMaxs);
        else
        {
            p.Mins = PlayerMins; p.Maxs = PlayerMaxs; p.Size = PlayerMaxs - PlayerMins;
        }

        // QC PutPlayerInServer: this.view_ofs = STAT(PL_VIEW_OFS) — seed the STANDING eye height. UpdateCrouch only
        // re-sets ViewOfs on a crouch EDGE, so without this the (re)spawned server player's eye sits at the origin
        // (ViewOfs 0) and W_SetupShot fires ~35u too LOW until the first crouch cycle. Reset the duck state too so
        // it matches the standing hull set above (a respawn while ducked must not carry the crouch eye/hull).
        p.IsDucked = false;
        p.ViewOfs = XonoticGodot.Common.Physics.PlayerPhysics.StandViewOfs;

        // QC nudge: 1 - mins.z - 24 = 1 - (-24) - 24 = 1 qu above the marker, so the hull clears the floor.
        float zNudge = 1f - PlayerMins.Z - 24f;
        Vector3 origin = sp.Origin + new Vector3(0f, 0f, zNudge);
        if (Api.Services is not null)
            Api.Entities.SetOrigin(p, origin);
        else
            p.Origin = origin;

        // QC: "don't reset back to last position even if new position is stuck in solid".
        p.OldOrigin = p.Origin;

        // --- spawn shield (QC PutPlayerInServer ~659/674: StatusEffects_apply(SpawnShield, this, shieldtime)) ---
        // The damage pipeline reads the shield off Entity.SpawnShieldExpire (an absolute sim time), so set it
        // here to now + g_spawnshieldtime. Firing a weapon clears it (handled by the weapon/damage side).
        float now = Api.Services is not null ? Api.Clock.Time : 0f;
        // Honor an explicit g_spawnshieldtime 0 (disable the shield) — CvarOr distinguishes unset from 0 (SPAWN5).
        p.SpawnShieldExpire = now + CvarOr(CvarSpawnShieldTime, DefSpawnShieldTime);

        // QC client.qc:661-664 PRIMES the rot/regen pause timers on spawn (rather than zeroing) so a player
        // spawned above the stable point (e.g. a 200 HP CA/LMS start) holds for a few seconds before rotting.
        // These live on the Entity (DamageEntityState) shared with the regen tick + the pickup path (REGEN3).
        // CvarOr honors an explicit 0 (a host disabling a spawn pause), unlike the 0-fallback Cvar helper.
        p.PauseRegenFinished     = now + CvarOr("g_balance_pause_health_regen_spawn", 0f); // shared (incl. fuel regen)
        p.PauseRotHealthFinished = now + CvarOr("g_balance_pause_health_rot_spawn", 5f);
        p.PauseRotArmorFinished  = now + CvarOr("g_balance_pause_armor_rot_spawn", 5f);
        p.PauseRotFuelFinished   = now + CvarOr("g_balance_pause_fuel_rot_spawn", 10f);

        // --- player model (QC PutPlayerInServer: this.model = ""; FixPlayermodel(this) → setplayermodel;
        //     server/client.qc:720-721 / :241-248) ---
        // sv_defaultplayermodel is the universal default ("models/player/erebus.iqm"). With sv_defaultcharacter 0
        // (stock) the model is really the client's own `playermodel` cvar, which isn't plumbed client→server yet —
        // so every player AND bot resolves to the same default for now (a faithful stand-in; QC also falls back to
        // erebus.iqm for an empty/invalid choice). The model is networked by NAME (NetEntityState.Model) so the
        // client loads the skeletal IQM. The hull set by SetSize above is unchanged (a Player keeps its mins/maxs).
        string playerModel = CvarStr(CvarDefaultPlayerModel);
        if (string.IsNullOrEmpty(playerModel))
            playerModel = DefaultPlayerModel;
        if (Api.Services is not null)
            Api.Entities.SetModel(p, playerModel);
        else
            p.Model = playerModel;
        p.Skin = Cvar(CvarDefaultPlayerSkin, 0f);

        // QC also rolls the damage-rot/regen pause windows forward by g_balance_pause_*_spawn and, while the
        // pre-match countdown is still running (time < game_starttime), extends the shield + pauses by the
        // remaining countdown so spawn protection lasts into live play. Those pause-finished timers and the
        // countdown clock live on the MatchController (warmup/start-time state) which this static spawn helper
        // doesn't own — NOTE: re-apply the (game_starttime - time) shield/pause extension once the spawn path
        // runs with a match-time handle. The base shield above already protects normal (non-countdown) spawns.

        // NOTE (client/networking): weapon view entities (CL_SpawnWeaponentity), SpawnEvent networking, the bot
        // aim reset and fixangle/v_angle are client-render / bot-AI concerns handled by the renderer and bot
        // agents; the server-authoritative owned-weapon set + the player model (above) are applied here.
    }

    /// <summary>
    /// QC <c>SetStartItems()</c> (server/world.qc): build the start-of-life loadout globals (start_health /
    /// start_armorvalue / start_ammo_* / start_weapons / start_items), then fire the SetStartItems mutator
    /// hook so arena mutators (weaponarena_random / new_toys / overkill / nix / instagib) can rewrite them.
    /// QC computes these once at match config; the port computes them per-spawn (handlers are deterministic,
    /// so the result is identical) and feeds the result into <see cref="ApplyStartLoadout"/>. The stock
    /// defaults (Blaster + zero ammo) mirror balance-xonotic.cfg, so with no arena mutator this is a no-op.
    /// </summary>
    public static StartLoadout ComputeStartItems()
    {
        var l = new StartLoadout
        {
            Health = Cvar(CvarHealthStart, DefHealthStart),
            Armor  = Cvar(CvarArmorStart,  DefArmorStart),
            AmmoShells  = Cvar(CvarAmmoShells,  0f),
            AmmoBullets = Cvar(CvarAmmoNails,   0f),
            AmmoRockets = Cvar(CvarAmmoRockets, 0f),
            AmmoCells   = Cvar(CvarAmmoCells,   0f),
            AmmoFuel    = Cvar(CvarAmmoFuel,    0f),
        };
        foreach (string w in DefaultLoadout) l.Weapons.Add(w);

        // MUTATOR_CALLHOOK(SetStartItems) — arena/loadout mutators rewrite the loadout (weaponarena_random
        // randomizes start_weapons, new_toys swaps in the new-toy variants, overkill sets the OK set).
        var args = new MutatorHooks.SetStartItemsArgs(l);
        MutatorHooks.SetStartItems.Call(ref args);
        return args.Loadout;
    }

    /// <summary>
    /// QC starting-resource block of PutPlayerInServer: SetResource for health/armor and the five ammo
    /// pools, plus STAT(WEAPONS) = start_weapons. Sources the values from <see cref="ComputeStartItems"/>
    /// (the SetStartItems seam) so arena mutators take effect; the defaults match balance-xonotic.cfg.
    /// Armor caps at the per-entity limit via <see cref="Resources.SetResource"/>.
    /// </summary>
    private static void ApplyStartLoadout(Player p)
    {
        StartLoadout start = ComputeStartItems();

        float health = start.Health;
        float armor  = start.Armor;

        // QC keeps MaxHealth as the cap; spawn health is start_health (100), max is 100 unless a pickup raised it.
        p.MaxHealth = health;
        // Health/armor are set explicitly (no waste hooks) like the QC SetResource on spawn.
        p.SetResource(ResourceType.Health, health);
        p.SetResource(ResourceType.Armor,  armor);

        p.SetResource(ResourceType.Shells,  start.AmmoShells);
        p.SetResource(ResourceType.Bullets, start.AmmoBullets);
        p.SetResource(ResourceType.Rockets, start.AmmoRockets);
        p.SetResource(ResourceType.Cells,   start.AmmoCells);
        p.SetResource(ResourceType.Fuel,    start.AmmoFuel);

        // QC STAT(WEAPONS, this) = start_weapons — reset the owned-weapon set to the (seam-computed) loadout.
        // Populate BOTH the NetName set (status / superweapon / HasWeapon checks) AND the canonical WepSet
        // bitset (Entity.OwnedWeaponSet — the authority Inventory + the W_WeaponFrame driver read). The spawn
        // path previously filled only the NetName set, leaving OwnedWeaponSet empty, so the player spawned
        // with no equippable weapon and could never fire once the fire driver started gating on the active weapon.
        p.OwnedWeapons.Clear();
        p.OwnedWeaponSet.Clear();
        foreach (string w in start.Weapons)
        {
            p.OwnedWeapons.Add(w);
            if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
        }

        // GiveRandomWeapons (QC server/items.qc:440): in random-start-weapons modes give N extra weapons
        // drawn at random (without replacement) from g_random_start_weapons. Default count is 0, so this is a
        // no-op in stock DM. Deterministic via the spawn RNG (ADR-0010: no nondeterministic random()).
        int randomCount = (int)Cvar(CvarRandomStartCount, 0f);
        if (randomCount > 0)
            GiveRandomWeapons(p, randomCount, CvarStr(CvarRandomStartWeapons));

        // start_items (QC: this.items = start_items) — the starting powerup/key bitfield. Default 0.
        p.Items = (int)Cvar(CvarStartItems, 0f);

        // QC client.qc: if the loadout includes any superweapon (STAT(WEAPONS) & WEPSET_SUPERWEAPONS), arm the
        // Superweapon status effect for g_balance_superweapons_time so the held superweapon expires on schedule
        // (the per-frame countdown in PlayerFrameLogic strips the weapon when it lapses). Uses the central
        // NetName→superweapon registry (Weapons.OwnsAnySuperWeapon).
        if (Weapons.OwnsAnySuperWeapon(p.OwnedWeapons) && StatusEffectsCatalog.Superweapon is { } sw)
            StatusEffectsCatalog.Apply(p, sw, Cvar(CvarSuperweaponsTime, DefSuperweaponsTime));

        // QC client.qc PutPlayerInServer slot loop (~line 830): equip slot 0's best owned weapon
        // (w_getbestweapon) on every (re)spawn so the W_WeaponFrame driver has an ACTIVE weapon to fire.
        // Without this the player spawns with ActiveWeaponId = -1 and the fire path no-ops — the
        // "press fire and nothing happens" bug on the networked / listen-server path.
        Inventory.SwitchToBest(p);
    }

    /// <summary>
    /// QC <c>GiveWarmupResources</c> (server/client.qc:568) + the <c>warmup_stage</c> branch of PutPlayerInServer:
    /// the gear-up loadout used during warmup — <c>g_warmup_start_health</c> (100) / <c>g_warmup_start_armor</c>
    /// (100) / the warmup ammo pools, and <c>WARMUP_START_WEAPONS</c> = all normal weapons when
    /// <c>g_warmup_allguns</c> (else the normal start loadout). Lets players practice with the full arsenal
    /// before the match goes live (SPAWN3). No-op in normal play (g_warmup ships 0).
    /// </summary>
    private static void ApplyWarmupLoadout(Player p)
    {
        float health = Cvar("g_warmup_start_health", 100f);
        float armor  = Cvar("g_warmup_start_armor", 100f);
        p.MaxHealth = MathF.Max(health, 100f);
        p.SetResource(ResourceType.Health, health);
        p.SetResource(ResourceType.Armor,  armor);
        p.SetResource(ResourceType.Shells,  Cvar("g_warmup_start_ammo_shells", 30f));
        p.SetResource(ResourceType.Bullets, Cvar("g_warmup_start_ammo_nails", 160f));
        p.SetResource(ResourceType.Rockets, Cvar("g_warmup_start_ammo_rockets", 80f));
        p.SetResource(ResourceType.Cells,   Cvar("g_warmup_start_ammo_cells", 30f));
        p.SetResource(ResourceType.Fuel,    Cvar("g_warmup_start_ammo_fuel", 0f));

        p.OwnedWeapons.Clear();
        p.OwnedWeaponSet.Clear();
        if (CvarBool("g_warmup_allguns", true))
        {
            // QC WARMUP_START_WEAPONS = weapons_all() with allguns (server/world.qc:1953): want_weapon filters
            // ONLY on WEP_FLAG_HIDDEN (+ mutator-blocked) — so superweapons ARE included (they're not hidden)
            // and the hidden Tuba is excluded. Matching that exactly avoids dropping superweapons / leaking Tuba.
            foreach (Weapon w in Weapons.All)
            {
                if ((w.SpawnFlags & WeaponFlags.Hidden) != 0) continue;
                if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) continue;
                p.OwnedWeapons.Add(w.NetName);
                p.OwnedWeaponSet.Add(w);
            }
        }
        else
        {
            foreach (string w in DefaultLoadout)
            {
                p.OwnedWeapons.Add(w);
                if (Weapons.ByName(w) is { } wep) p.OwnedWeaponSet.Add(wep);
            }
        }

        p.Items = (int)Cvar(CvarStartItems, 0f);
        Inventory.SwitchToBest(p);
    }

    /// <summary>
    /// Port of <c>GiveRandomWeapons</c> (server/items.qc:440), weapon-set half: pick
    /// <paramref name="numWeapons"/> distinct weapons at random (without replacement) from the space-
    /// separated <paramref name="weaponNames"/> list, skipping any the player already owns, and add their
    /// NetNames to <see cref="Player.OwnedWeapons"/>. Each draw uses RandomSelection's uniform pick over the
    /// still-eligible candidates (here all weights are 1, so a plain uniform index), via the deterministic
    /// spawn RNG. Stops early if no eligible weapon remains (QC: RandomSelection_chosen_ent == NULL).
    /// </summary>
    private static void GiveRandomWeapons(Player receiver, int numWeapons, string weaponNames)
    {
        if (numWeapons <= 0 || string.IsNullOrWhiteSpace(weaponNames))
            return;
        string[] potential = weaponNames.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (potential.Length == 0)
            return;

        for (int attempt = 0; attempt < numWeapons; attempt++)
        {
            // Gather the candidates the receiver doesn't already own (RandomSelection_Init + the FOREACH).
            _eligibleScratch.Clear();
            foreach (string w in potential)
                if (!receiver.OwnedWeapons.Contains(w) && !_eligibleScratch.Contains(w))
                    _eligibleScratch.Add(w);
            if (_eligibleScratch.Count == 0)
                return; // QC: chosen_ent == NULL → stop
            string chosen = _eligibleScratch[_rng.Next(_eligibleScratch.Count)];
            receiver.OwnedWeapons.Add(chosen);
            if (Weapons.ByName(chosen) is { } chosenWep) receiver.OwnedWeaponSet.Add(chosenWep);

            // QC: give the chosen weapon's ammo from random_start_ammo — but only when the player has none of
            // that resource yet (don't override ammo a previous random weapon already provided).
            ResourceType ammo = Weapons.AmmoTypeOf(chosen);
            if (ammo != ResourceType.None && receiver.GetResource(ammo) == 0f)
            {
                float amount = RandomStartAmmoFor(ammo);
                if (amount > 0f) receiver.SetResource(ammo, amount);
            }
        }
    }

    /// <summary>QC <c>GetResource(random_start_ammo, res)</c>: the configured random-start ammo for a resource.</summary>
    private static float RandomStartAmmoFor(ResourceType res) => res switch
    {
        ResourceType.Shells  => Cvar(CvarRandomStartShells, 0f),
        ResourceType.Bullets => Cvar(CvarRandomStartBullets, 0f),
        ResourceType.Rockets => Cvar(CvarRandomStartRockets, 0f),
        ResourceType.Cells   => Cvar(CvarRandomStartCells, 0f),
        _ => 0f,
    };

    /// <summary>Scratch list for the GiveRandomWeapons candidate set (single-threaded spawn path).</summary>
    private static readonly List<string> _eligibleScratch = new(16);

    /// <summary>QC <c>bound(lo, v, hi)</c>.</summary>
    private static float QBound(float lo, float v, float hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>Read a float cvar through the facade, falling back to the documented balance default.</summary>
    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>
    /// Read a float cvar, falling back to <paramref name="fallback"/> ONLY when the cvar is unset (empty string)
    /// — an explicit <c>0</c> is honored. Use for cvars where 0 is a meaningful value a host may set (e.g.
    /// <c>g_spawnshieldtime 0</c> to disable the spawn shield), which the plain <see cref="Cvar"/> 0-fallback
    /// would silently override back to the default.
    /// </summary>
    private static float CvarOr(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>Read a string cvar through the facade (empty when unset / no services).</summary>
    private static string CvarStr(string name)
        => Api.Services is null ? "" : Api.Cvars.GetString(name);

    /// <summary>
    /// Read a bool cvar (<c>cvar != 0</c>), falling back to <paramref name="fallback"/> when unset (empty string)
    /// or when no services are wired. Distinguishes "unset" from an explicit "0" like the weapon balance reads,
    /// so a default-on team-spawn cvar (e.g. g_ca_team_spawns 1) isn't read as off before configs load.
    /// </summary>
    private static bool CvarBool(string name, bool fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name) != 0f;
    }
}
