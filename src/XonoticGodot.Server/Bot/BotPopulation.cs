// Port of server/bot/default/bot.qc bot_serverframe (:689-833) + bot_fixcount (:623-687)
// + bot_spawn (:45-60) + the name/model slice of bot_setnameandstuff (:163-349)
// + the live per-tick input seam of ecs/systems/sv_physics.qc sys_phys_ai (:41-46).
using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// The live bot population manager — the C# port of QC <c>bot_serverframe</c>: it keeps the roster converged
/// on the configured fill (<c>bot_number</c> / <c>minplayers</c> / <c>bot_vs_human</c>, one add per frame,
/// remove-newest above target), loads the map's waypoint graph once when the first bot appears, rotates the
/// strategy token (exactly one bot per frame may run goal rating), resyncs the live <c>skill</c> cvar onto
/// every brain, auto-readies bots during warmup, and — the input seam — hands GameWorld each bot's per-tick
/// <see cref="MovementInput"/> from its <see cref="BotBrain.ThinkProduce"/>, cached per sim tick so the
/// movement step and the weapon driver consume the SAME command (QC sys_phys_ai runs bot_think inside the
/// client physics step; both readers see the one usercmd).
///
/// Owned by <see cref="GameWorld"/> (created at Boot). Bot client lifecycle still flows through
/// <see cref="ClientManager"/> — its <c>OnBotConnected</c> hook routes EVERY bot connect (fixcount fill,
/// console bot_add, a host's direct ClientConnect) into <see cref="RegisterBot"/> so no bot is brainless.
/// </summary>
public sealed class BotPopulation
{
    private readonly GameWorld _world;

    /// <summary>Engine <c>maxclients</c> (the spawnclient capacity cap in bot_fixcount). Host-set; default 16.</summary>
    public int MaxClients = 16;

    /// <summary>The shared map waypoint graph (QC g_waypoints), loaded once on the first frame with bots.</summary>
    public WaypointNetwork? Network { get; private set; }

    /// <summary>Fired when a bot leaves (fixcount removal, console remove, intermission teardown) so the net
    /// host can clean its per-player maps (ServerNet.ForgetPlayer). Fired from the disconnect chain — every
    /// removal path funnels through it.</summary>
    public event Action<Player>? BotRemoved;

    // brains in connect order (QC bot_list; the order drives strategy-token rotation + remove-newest).
    private readonly List<BotBrain> _brains = new();
    private readonly Dictionary<Player, BotBrain> _byPlayer = new();

    /// <summary>QC <c>currentbots</c>; -1 is the "recount next frame" sentinel armed while time &lt; 2.5.</summary>
    private int _currentBots;

    private float _nextThink;            // QC botframe_nextthink (fixcount backoff on spawn failure)
    private bool _waypointsLoaded;       // QC botframe_spawnedwaypoints (load-once latch)
    private float _lastSkillCvar = float.NaN; // QC the `skill` global resync (bot.qc:725-736)
    private int _tokenIndex;             // QC bot_strategytoken (index into _brains)
    private bool _tokenTaken = true;     // QC bot_strategytoken_taken (true → rotate next frame)
    private int _seedCounter = 1;

    // per-tick input cache (the both-readers-one-command seam; mirrors ServerNet's TickInputTime cache).
    private sealed class TickInput { public float Time = -1f; public MovementInput Input; }
    private readonly Dictionary<Player, TickInput> _tickInputs = new();
    private static readonly MovementInput ZeroInput = new() { FrameTime = Engine.Simulation.SimulationLoop.TicRate };

    /// <summary>The live brains, in connect order (QC bot_list). Exposed for tests/diagnostics.</summary>
    public IReadOnlyList<BotBrain> Brains => _brains;

    /// <summary>The brain driving <paramref name="p"/>, or null if it isn't a managed bot.</summary>
    public BotBrain? BrainOf(Player p) => _byPlayer.TryGetValue(p, out BotBrain? b) ? b : null;

    public BotPopulation(GameWorld world)
    {
        _world = world;
    }

    // =============================================================================================
    // the per-frame loop (QC bot_serverframe)
    // =============================================================================================

    /// <summary>
    /// QC <c>bot_serverframe()</c> — called once per tick from GameWorld.OnStartFrame, at main.qc:372's
    /// position (after the warmup check, before the SV_StartFrame hooks).
    /// </summary>
    public void ServerFrame()
    {
        float time = _world.Time;

        // (a) intermission (bot.qc:691-702): after the match ends bots STAY unless every human left — then all
        // bots are dropped (so an abandoned server empties out).
        if (_world.Intermission.Running && _currentBots > 0)
        {
            if (_world.Clients.HumanCount == 0)
            {
                for (int i = _brains.Count - 1; i >= 0; i--)
                    _world.Clients.ClientDisconnect(_brains[i].Bot); // disconnect chain drops the brain + event
                _currentBots = 0;
            }
            return;
        }

        // (b) game stopped → no population churn (bot.qc:704).
        if (_world.GameStopped)
            return;

        // (c) early-map sentinel (bot.qc:712-723): don't add/remove until time 2.5, then recount what's there
        // (bots may persist across a map change on a long-lived host).
        if (time < 2.5f)
        {
            _currentBots = -1;
            return;
        }
        if (_currentBots == -1)
        {
            _currentBots = _world.Clients.BotCount;
        }

        // (d) live `skill` cvar resync (bot.qc:725-736): a console `skill N` retunes every bot. (QC also
        // re-costs waypoint links across the bunnyhop threshold — the port's link costs aren't skill-scaled,
        // so that branch has no analogue.) Per-bot BotSkill is the authoritative knob; the cvar change writes
        // it for all bots, and the per-frame brain sync below picks up any direct BotSkill write too.
        float skillCvar = Cvars.Skill;
        if (skillCvar != _lastSkillCvar)
        {
            bool first = float.IsNaN(_lastSkillCvar);
            _lastSkillCvar = skillCvar;
            if (!first)
                foreach (BotBrain b in _brains)
                    b.Bot.BotSkill = skillCvar;
        }
        foreach (BotBrain b in _brains)
        {
            b.Skill = b.Bot.BotSkill;
            b.Nav.Skill = b.Bot.BotSkill;
        }

        // (e) autoskill (bot.qc:741-747) — ships skill_auto 0; unported (see residuals).

        // (f) population fill/trim (bot.qc:749-753): one add per frame; a failed spawn backs off 10s.
        if (time > _nextThink)
        {
            if (!FixCount(time))
                _nextThink = time + 10f;
        }

        // (g) waypoint load-once (bot.qc:761-784): the first frame with bots present loads the map graph
        // (.waypoints + .cache + .hardwired via the world's ConfigReader, else entity auto-generation).
        if (!_waypointsLoaded && _currentBots > 0)
        {
            _waypointsLoaded = true;
            Network = _world.LoadWaypointNetwork();
            foreach (BotBrain b in _brains)
                b.Network = Network;
        }

        // (h) strategy token rotation (bot.qc:786-813): when the token was consumed, pass it to the next bot
        // WITHOUT a current goal (skipping dead bots); if none qualifies, simply the next one.
        RotateStrategyToken();

        // (i) warmup auto-ready (QC bot_think:153-157): a spawned bot readies up once per warmup stage.
        if (_world.Warmup.WarmupStage)
        {
            foreach (BotBrain b in _brains)
                if (!b.AutoReadied && !b.Bot.IsObserver)
                {
                    b.AutoReadied = true;
                    _world.Warmup.ToggleReady(b.Bot);
                }
        }
        else
        {
            foreach (BotBrain b in _brains)
                b.AutoReadied = false; // re-arm for the next warmup (ready set is cleared on restart)
        }

        // (j) botframe_updatedangerousobjects (bot.qc:815-822) — per-waypoint danger costs; unported (the
        // port's live danger check is the per-bot havocbot_checkdanger probe in BotBrain). See residuals.
    }

    /// <summary>
    /// The per-tick bot input source (the sys_phys_ai seam): produce this tick's command from the bot's brain,
    /// cached on the sim clock so GameWorld's movement step and weapon driver — which both pull input — consume
    /// the SAME command (mirrors ServerNet's per-tick cache for humans). A bot without a brain (not yet
    /// registered) stands still.
    /// </summary>
    public IMovementInput InputFor(Player p, float dt)
    {
        if (!_byPlayer.TryGetValue(p, out BotBrain? brain))
            return ZeroInput;
        if (!_tickInputs.TryGetValue(p, out TickInput? cache))
            _tickInputs[p] = cache = new TickInput();
        float now = _world.Time;
        if (cache.Time != now)
        {
            cache.Time = now;
            cache.Input = brain.ThinkProduce(p, dt);
        }
        return cache.Input;
    }

    // =============================================================================================
    // fixcount (QC bot_fixcount) — the bot_number/minplayers/bot_vs_human convergence
    // =============================================================================================

    /// <summary>QC <c>bot_fixcount(false)</c>: converge the roster on the target (ONE add per frame, trims all
    /// excess). Returns false on a spawn failure (server full), arming the 10s backoff.</summary>
    private bool FixCount(float time)
    {
        int activeRealPlayers = 0, realPlayers = 0;
        foreach (ClientManager.ClientInfo c in _world.Clients.Clients)
        {
            if (c.IsBot) continue;
            realPlayers++;
            if (!c.Player.IsObserver) activeRealPlayers++; // QC IS_PLAYER (joined, not observing)
        }

        int target = TargetBotCount(
            botVsHuman: Cvars.Float("bot_vs_human"),
            availableTeams: _world.Teamplay.IsTeamGame ? _world.Teamplay.TeamCount : 0,
            activeRealPlayers: activeRealPlayers,
            realPlayers: realPlayers,
            teamplay: _world.Teamplay.IsTeamGame,
            minPlayers: Cvars.Float("minplayers"),
            minPlayersPerTeam: Cvars.Float("minplayers_per_team"),
            botNumber: Cvars.Float("bot_number"),
            playerLimit: Cvars.Int("g_maxplayers"),
            maxClients: MaxClients,
            currentBots: _currentBots,
            time: time,
            botJoinEmpty: Cvars.Bool("bot_join_empty"));

        // only add ONE bot per frame to avoid utter chaos (QC bot.qc:667-681, multiple_per_frame=false).
        if (_currentBots < target)
        {
            if (SpawnBot(null, null) is null)
                return false;
        }
        while (_currentBots > target && target >= 0)
            RemoveNewest();
        return true;
    }

    /// <summary>
    /// The pure fixcount target math (QC bot_fixcount:623-664), exposed for tests: how many bots should be on
    /// the server right now. <paramref name="availableTeams"/> is QC AVAILABLE_TEAMS (0 outside teamplay);
    /// <paramref name="playerLimit"/> is GetPlayerLimit() (g_maxplayers; 0 = unlimited).
    /// </summary>
    public static int TargetBotCount(float botVsHuman, int availableTeams, int activeRealPlayers, int realPlayers,
        bool teamplay, float minPlayers, float minPlayersPerTeam, float botNumber, int playerLimit, int maxClients,
        int currentBots, float time, bool botJoinEmpty)
    {
        // bot_vs_human: an all-bot team sized as |ratio| × active humans (QC bot.qc:642-643).
        if (botVsHuman != 0f && availableTeams == 2)
            return System.Math.Min((int)MathF.Ceiling(MathF.Abs(botVsHuman) * activeRealPlayers),
                maxClients - realPlayers);

        // QC bot.qc:644-660: fill while humans are around (or bot_join_empty, or the 5s level-change grace
        // that keeps last map's bots from being torn down before the humans rejoin).
        if (realPlayers > 0 || botJoinEmpty || (currentBots > 0 && time < 5f))
        {
            int min = teamplay
                ? System.Math.Max(0, (int)MathF.Floor(minPlayersPerTeam) * availableTeams)
                : System.Math.Max(0, (int)MathF.Floor(minPlayers));
            int minBots = System.Math.Max(0, (int)MathF.Floor(botNumber));

            int bots = System.Math.Max(minBots, min - activeRealPlayers);
            if (playerLimit > 0)
                bots = System.Math.Min(bots, System.Math.Max(playerLimit - activeRealPlayers, 0));
            return System.Math.Min(bots, maxClients - realPlayers);
        }

        return 0; // no players → remove bots (QC bot.qc:661-664)
    }

    // =============================================================================================
    // add / remove (QC bot_spawn / bot_removenewest) + the console handlers
    // =============================================================================================

    /// <summary>
    /// Console <c>bot_add</c> (and the setbots top-up): spawn one bot immediately AND raise <c>bot_number</c>
    /// so the faithful fixcount doesn't tear it down at the next convergence (QC's plain bot_spawn adds would
    /// be culled at the recount — the port keeps the console verbs useful by moving the floor with them).
    /// </summary>
    public BotBrain? AddBot(string? name = null, float? skill = null)
    {
        BotBrain? brain = SpawnBot(name, skill);
        if (brain is not null)
            Cvars.Set("bot_number", MathF.Max(Cvars.Float("bot_number") + 1f, _currentBots));
        return brain;
    }

    /// <summary>Console <c>bot_remove</c>: drop the newest bot (optionally matching <paramref name="name"/>)
    /// and lower <c>bot_number</c> with it so fixcount doesn't immediately re-add one.</summary>
    public bool RemoveBot(string? name = null)
    {
        Player? bot = null;
        foreach (BotBrain b in _brains)
            if (string.IsNullOrWhiteSpace(name)
                || b.Bot.NetName.Contains(name!, StringComparison.OrdinalIgnoreCase))
                bot = b.Bot; // last match → newest
        if (bot is null)
            return false;
        Cvars.Set("bot_number", MathF.Max(0f, Cvars.Float("bot_number") - 1f));
        return _world.Clients.ClientDisconnect(bot); // the disconnect chain drops the brain + fires BotRemoved
    }

    /// <summary>QC <c>bot_spawn</c>: connect a bot client (name/model from bots.txt) and bump currentbots.
    /// The brain is created by the ClientManager OnBotConnected hook → <see cref="RegisterBot"/>.</summary>
    private BotBrain? SpawnBot(string? name, float? skill)
    {
        (string botName, string model) = string.IsNullOrWhiteSpace(name)
            ? PickNameAndModel()
            : (name!, "");

        ClientManager.ClientInfo info = _world.Clients.ClientConnect(isBot: true, netName: botName);
        Player p = info.Player;
        p.BotSkill = skill ?? Cvars.Skill;                  // QC the global `skill` cvar seeds new bots
        if (!string.IsNullOrEmpty(model))
        {
            _preferredModel[p] = model;
            ApplyPreferredModel(p);                          // the connect auto-Join already spawned it
        }
        // the OnBotConnected hook registered the brain; sync its skill now that BotSkill is final.
        if (_byPlayer.TryGetValue(p, out BotBrain? brain))
        {
            brain.Skill = p.BotSkill;
            brain.Nav.Skill = p.BotSkill;
        }
        return brain;
    }

    /// <summary>QC <c>bot_removenewest</c> (bot.qc:486-539): drop the most recently added bot — in a team game,
    /// the newest bot on the LARGEST team (so trims also rebalance).</summary>
    private void RemoveNewest()
    {
        if (_brains.Count == 0)
        {
            _currentBots = _world.Clients.BotCount; // defensive resync (count says bots exist but no brains)
            return;
        }

        Player? victim = null;
        if (_world.Teamplay.IsTeamGame)
        {
            // largest team by live player count (QC TeamBalance largest-team pick).
            int bestTeam = 0, bestCount = -1;
            foreach (int team in Teams.Active(_world.Teamplay.TeamCount))
            {
                int n = _world.Teamplay.CountTeam(team, _world.Clients.Players);
                if (n > bestCount) { bestCount = n; bestTeam = team; }
            }
            for (int i = _brains.Count - 1; i >= 0; i--)
                if ((int)_brains[i].Bot.Team == bestTeam) { victim = _brains[i].Bot; break; }
        }
        victim ??= _brains[^1].Bot;
        _world.Clients.ClientDisconnect(victim);
    }

    // =============================================================================================
    // brain lifecycle (wired from ClientManager / GameWorld)
    // =============================================================================================

    /// <summary>
    /// ClientManager <c>OnBotConnected</c> sink (QC bot_clientconnect + havocbot_setupbot): create the brain
    /// for a newly connected bot — role from the gametype, the live roster as its player set, the shared map
    /// graph. Counts it into <c>currentbots</c> (unless the pre-2.5s sentinel is armed; the recount adopts it).
    /// </summary>
    public void RegisterBot(Player p)
    {
        if (_byPlayer.ContainsKey(p))
            return;
        var brain = new BotBrain(p, Network, p.BotSkill, seed: _seedCounter++)
        {
            Role = BotRoles.ChooseRole(_world.GameType?.NetName),
            PlayerProvider = () => _world.Clients.Players,
            GameTypeNetName = _world.GameType?.NetName,
            GameType = _world.GameType,
            StrategyTokenHeld = false,                  // the population hands the token around
            MovementHold = MovementHeld,
        };
        brain.OnStrategyTokenUsed = () => _tokenTaken = true;
        _brains.Add(brain);
        _byPlayer[p] = brain;
        if (_currentBots >= 0)
            _currentBots++;
    }

    /// <summary>GameWorld's disconnect hook (every bot removal path): drop the brain + caches, fire
    /// <see cref="BotRemoved"/> so the net host forgets the player.</summary>
    public void OnBotDisconnected(Player p)
    {
        if (_byPlayer.Remove(p, out BotBrain? brain))
        {
            int idx = _brains.IndexOf(brain);
            if (idx >= 0)
            {
                _brains.RemoveAt(idx);
                if (_tokenIndex > idx || _tokenIndex >= _brains.Count)
                    _tokenIndex = System.Math.Max(0, _tokenIndex - 1);
            }
            if (_currentBots > 0)
                _currentBots--;
        }
        _tickInputs.Remove(p);
        _preferredModel.Remove(p);
        BotRemoved?.Invoke(p);
    }

    /// <summary>GameWorld's spawn hook for bots: re-apply the bots.txt model after every (re)spawn —
    /// PutPlayerInServer stamps sv_defaultplayermodel each time (QC FixPlayermodel honors the client's
    /// playermodel cvar, which for a QC bot carries the bots.txt pick).</summary>
    public void OnBotSpawned(Player p) => ApplyPreferredModel(p);

    /// <summary>QC bot_think's movement holds: the pre-match countdown + the campaign "wait for the player".</summary>
    private bool MovementHeld()
        => _world.Time < _world.GameStartTime && !_world.Warmup.WarmupStage
           || (Cvars.Bool("g_campaign") && !_world.Campaign.BotsMayStart);

    private readonly Dictionary<Player, string> _preferredModel = new();

    private void ApplyPreferredModel(Player p)
    {
        if (!_preferredModel.TryGetValue(p, out string? model) || string.IsNullOrEmpty(model))
            return;
        if (Common.Services.Api.Services is not null)
            Common.Services.Api.Entities.SetModel(p, model);
        else
            p.Model = model;
    }

    // =============================================================================================
    // strategy token (QC bot.qc:786-813)
    // =============================================================================================

    private void RotateStrategyToken()
    {
        if (_brains.Count == 0)
            return;
        if (_tokenTaken)
        {
            _tokenTaken = false;
            // QC bot_relinkplayerlist (bot.qc:437) builds bot_list EXCLUDING IS_OBSERVER || bot_ispaused, and the
            // token (bot.qc:786-813) only ever walks bot_list — so an observer can never hold the token. Mirror
            // that: the candidate set is non-observer bots only. Pass 1 picks the first non-observer WITHOUT a
            // goal (skipping dead, per the QC break condition); pass 2 ("simply the next one") falls to the next
            // non-observer. If every bot is an observer (bot_list == NULL), the token can't rotate — leave it put.
            int n = _brains.Count;
            int next = -1;
            for (int step = 0; step < n; step++)
            {
                int idx = (_tokenIndex + 1 + step) % n;
                BotBrain b = _brains[idx];
                if (b.Bot.IsObserver) continue;               // not in bot_list
                if (next < 0) next = idx;                      // pass 2 fallback: first non-observer
                if (!b.Bot.IsDead && !b.Nav.HasGoal)          // pass 1: first goal-less (skip dead)
                {
                    next = idx;
                    break;
                }
            }
            if (next >= 0)
                _tokenIndex = next;
        }
        for (int i = 0; i < _brains.Count; i++)
            _brains[i].StrategyTokenHeld = i == _tokenIndex;
    }

    // =============================================================================================
    // bots.txt (QC bot_setnameandstuff — the name/model slice)
    // =============================================================================================

    private List<(string Name, string Model)>? _botRows; // parsed once per world; null until first use

    /// <summary>
    /// Pick a name + model from the bot config file (QC bot_setnameandstuff over bot_config_file=bots.txt):
    /// tab-separated rows (name, model, skin, shirt, pants, team, 12 skill modifiers); rows whose name is not
    /// in use are preferred (QC the prio+1 weighting); the name is wrapped in bot_prefix/bot_suffix and
    /// de-duplicated with a "(N)" suffix; the model becomes models/player/&lt;model&gt;.iqm. Falls back to a
    /// small built-in name table when the file isn't mounted. The skin/shirt/pants columns and the 12 skill
    /// modifiers are unported (single-knob skill; see residuals).
    /// </summary>
    private (string name, string model) PickNameAndModel()
    {
        _botRows ??= ParseBotFile();

        // names already in use (QC the cleanname conflict scan).
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Player q in _world.Clients.Players)
            used.Add(q.NetName);

        string prefix = Cvars.String("bot_prefix");
        string suffix = Cvars.String("bot_suffix");

        // prefer rows whose decorated name is unused (QC prio weighting), random among the preferred set.
        var candidates = new List<(string Name, string Model)>();
        foreach ((string Name, string Model) row in _botRows)
            if (!used.Contains(prefix + row.Name + suffix))
                candidates.Add(row);
        if (candidates.Count == 0)
            candidates = _botRows;

        string baseName = "Bot", model = "";
        if (candidates.Count > 0)
        {
            (string Name, string Model) row = candidates[_nameRng.Next(candidates.Count)];
            baseName = row.Name;
            model = row.Model;
        }

        string name = prefix + baseName + suffix;
        // duplicates get "(N)" (QC appends a counter when the name is taken).
        if (used.Contains(name))
        {
            int i = 2;
            while (used.Contains($"{name}({i})")) i++;
            name = $"{name}({i})";
        }

        string modelPath = string.IsNullOrEmpty(model) ? "" : $"models/player/{model}.iqm"; // QC appends .iqm
        return (name, modelPath);
    }

    private readonly Random _nameRng = new(12345);

    /// <summary>Parse bot_config_file (bots.txt) rows via the world's ConfigReader; comments (// #) skipped.</summary>
    private List<(string Name, string Model)> ParseBotFile()
    {
        var rows = new List<(string, string)>();
        string file = Cvars.String("bot_config_file");
        if (string.IsNullOrEmpty(file)) file = "bots.txt";
        string? text = _world.ConfigReader?.Invoke(file);
        if (text is not null)
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                string[] f = line.Split('\t');
                if (f.Length == 0 || string.IsNullOrWhiteSpace(f[0]))
                    continue;
                rows.Add((f[0].Trim(), f.Length > 1 ? f[1].Trim() : ""));
            }
        }
        if (rows.Count == 0)
        {
            // no bots.txt mounted: the old built-in table (kept so a bare test floor still names its bots).
            foreach (string n in new[] { "Hellfire", "Toxic", "Scorcher", "Discbot", "Nexus", "Eureka", "Sensible", "Mystery" })
                rows.Add((n, ""));
        }
        return rows;
    }
}
