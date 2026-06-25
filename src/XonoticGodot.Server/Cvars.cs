using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// A console variable's static metadata — the C# successor to one QuakeC <c>autocvar_*</c> declaration
/// plus its <c>seta</c>/<c>set</c> registration in the default configs (xonotic-server.cfg /
/// _balance/*.cfg / bot cvars). QuakeC autocvars are compile-time bound globals seeded from the engine
/// cvar store; this port keeps the same idea as data — a <see cref="Name"/> → <see cref="DefaultValue"/>
/// mapping with <see cref="CvarFlags"/> — so <see cref="Cvars.RegisterDefaults"/> can stamp them into the
/// ambient <see cref="ICvarService"/> at boot, and gameplay reads them through the normal cvar facade.
/// </summary>
public readonly struct CvarDef
{
    /// <summary>The cvar name (QC the autocvar suffix, e.g. <c>sv_gravity</c> for <c>autocvar_sv_gravity</c>).</summary>
    public readonly string Name;

    /// <summary>The default string value (QC the value in the <c>set</c> command). Parsed to a float on read.</summary>
    public readonly string DefaultValue;

    /// <summary>Engine cvar flags (QC the <c>set</c> vs <c>seta</c> archive/notify distinction).</summary>
    public readonly CvarFlags Flags;

    /// <summary>One-line description (QC the trailing config comment); diagnostics only.</summary>
    public readonly string Description;

    public CvarDef(string name, string defaultValue, CvarFlags flags = CvarFlags.None, string description = "")
    {
        Name = name;
        DefaultValue = defaultValue;
        Flags = flags;
        Description = description;
    }

    /// <summary>Convenience overload for a non-archived cvar with just a description (flags default to None).</summary>
    public CvarDef(string name, string defaultValue, string description)
        : this(name, defaultValue, CvarFlags.None, description) { }
}

/// <summary>
/// The server cvar framework — the C# successor to the Xonotic autocvar system + the default server
/// configuration (xonotic-server.cfg and the balance/bot cfgs it execs). It is the data backing for the
/// many <c>Api.Cvars.GetFloat(...)</c> reads scattered across the gameplay layer: at boot
/// <see cref="RegisterDefaults"/> stamps a baseline of well-known server cvars (with their stock Xonotic
/// defaults and flags) into the ambient <see cref="ICvarService"/> so those reads return sane values
/// instead of 0, and a host can override any of them before/after boot via the normal cvar facade or the
/// <see cref="Commands"/> <c>set</c>/<c>cvar</c> commands.
///
/// This deliberately holds no state of its own — the engine <see cref="ICvarService"/> remains the single
/// source of truth (QC's cvar store). This class is the registry of <em>defaults</em> + a set of typed,
/// clamped convenience accessors mirroring QC's <c>autocvar_*</c> globals and helper reads (bound/notify).
/// </summary>
public static class Cvars
{
    // =============================================================================================
    // the default table (QC the stock server/balance/bot cfg values)
    // =============================================================================================

    private const CvarFlags Save = CvarFlags.Save;
    private const CvarFlags Notify = CvarFlags.Notify;

    /// <summary>
    /// The baseline server cvar defaults — a curated slice of xonotic-server.cfg + balanceXonotic.cfg +
    /// the bot cvars, covering the cvars the ported server core / bot AI actually read. Not exhaustive
    /// (QC has thousands); a host execs its own cfg over the top. Values match stock Xonotic.
    /// </summary>
    public static readonly CvarDef[] Defaults =
    {
        // ---- world / physics ----
        // Only sv_gravity is registered here: it is a genuine world cvar that GameWorld writes per-map
        // (Cvars.Set("sv_gravity")) and many subsystems read (Cvars.Gravity, MonsterAI, vehicles, jumppads,
        // moving brushes). The per-tick *movement tunables* — sv_maxspeed / sv_maxairspeed / sv_jumpvelocity
        // / sv_accelerate / sv_airaccelerate / sv_friction / sv_stopspeed / sv_stepheight /
        // sv_gameplayfix_stepdown — are deliberately NOT pre-registered: their single source of truth is
        // MovementParameters.FromCvars(), which already substitutes the stock Xonotic values (physicsX.cfg)
        // when a cvar is unset. Registering them here only duplicated that table — and the hand-typed copies
        // had drifted wrong (e.g. sv_maxairspeed 30 vs stock 360), so on a host that does NOT load the real
        // cfg tree they SHADOWED the correct fallbacks and produced wrong physics. Leaving them out lets the
        // authoritative defaults win; a loaded physicsX.cfg still sets the live cvars for the few callers that
        // read them directly (vehicles/mutators, which each carry their own correct self-default too).
        new("sv_gravity", "800", Notify, "world gravity (u/s^2)"),

        // Step-up vertical-velocity limiter (PORT EXTENSION — see PlayerPhysics.ApplyStepUpSpeedClamp). Unlike the
        // magnitude movement tunables above (deliberately UNregistered so FromCvars owns their defaults), these two
        // ARE registered: (1) so the in-game console/menu can SEE + complete them (RegisterDefaults stamps the shared
        // MenuState.Cvars store at MenuState.Boot), and (2) so the listen-server cvar bridge — which only forwards a
        // console `set` to the server tick when the server store already Has(name) (NetGame._sharedCvarBridge) —
        // actually applies a change. SAFE from the shadowing trap that comment warns about: the registered value is
        // the NO-OP identity, byte-equal to MovementParameters.Defaults (scale 1 / max -1 = disabled), so FromCvars'
        // EXISTS-gated CvarRaw reads back exactly the same default it would have fallen back to. KEEP these two values
        // in lockstep with MovementParameters.Defaults.StepUpSpeedScale / StepUpSpeedMax.
        new("sv_step_upspeed_scale", "1", "step-up upward-velocity multiplier (1 = vanilla launch, 0 = step up without launching)"),
        new("sv_step_upspeed_max", "-1", "step-up upward-velocity hard cap in u/s (-1 = disabled/uncapped)"),

        // Global time scale (Darkplaces host_timescale / Xonotic slowmo): the real->sim time mapping for the WHOLE
        // simulation. <1 = slow motion, >1 = fast, 1 = real time, 0 = paused. ServerNet.StepWorld feeds it to
        // SimulationLoop.TimeScale and NetGame scales the client's input cadence + render clock by the same value,
        // so prediction stays in lockstep (this is the same time-mapping that makes movement frame-rate-independent).
        // Registered (like the step-up knobs) so the console/menu can set it and the listen-server cvar bridge applies it.
        new("slowmo", "1", Notify, "global time scale: <1 slow-motion, >1 fast, 1 = real time, 0 = paused"),

        // [T45] warpzone self-targeting (lib/warpzone/server.qc WarpZone_InitStep_FindTarget). Behaviour is already
        // correct without this entry (Warpzone.cs reads it via Api.Cvars.GetFloat, which returns 0 when unset), but
        // registering it makes it visible to cvarlist/the menu and lets the listen-server `set` bridge apply changes.
        new("sv_warpzone_allow_selftarget", "0", Save, "1 = a warpzone may target itself (default 0: never self-link)"),

        // ---- match flow / limits (mapinfo + xonotic-server.cfg) ----
        new("timelimit", "20", Notify, "match time limit in minutes (0 = none)"),
        new("fraglimit", "0", Notify, "score limit (0 = none)"),
        new("leadlimit", "0", Notify, "lead margin to win (0 = none)"),
        // overtime / sudden death (xonotic-server.cfg:273-275; gametypes-server.cfg leadlimit_and_fraglimit).
        // Read by OverTimeManager (QC world.qc InitiateSuddenDeath / InitiateOvertime / WinningCondition_Scores):
        // a tied timed match adds normal overtimes (up to timelimit_overtimes) then enters suddendeath.
        new("timelimit_overtime", "2", Save, "duration in minutes of one added overtime, added to the timelimit"),
        new("timelimit_overtimes", "0", Save, "how many overtimes to add at max"),
        new("timelimit_suddendeath", "5", Save, "minutes suddendeath lasts after all overtimes are added and still no winner"),
        new("leadlimit_and_fraglimit", "0", Save, "both leadlimit AND fraglimit must be reached"),
        new("g_maxplayers", "0", Save, "0 = unlimited player slots"),
        new("minplayers", "0", Save, "fill FFA up to this many with bots"),
        new("minplayers_per_team", "0", Save, "fill each team up to this many with bots"),

        // ---- warmup / ready-restart (xonotic-server.cfg) ----
        new("g_warmup", "0", Save, "enable warmup stage"),
        new("g_warmup_limit", "180", Save, "warmup time limit in seconds (-1 = until ready, 0 = use timelimit)"),
        new("g_warmup_allow_timeout", "0", Save),
        new("g_warmup_majority_factor", "0.8", Save, "fraction of players that must ready up"),
        new("sv_ready_restart", "0", Save),
        new("sv_ready_restart_after_countdown", "0", Save, "reset players and map items after the countdown ended, instead of at the beginning of the countdown"),
        new("sv_ready_restart_repeatable", "0", Save),
        // QC map_minplayers (world.qc:697): per-map worldspawn key, the g_warmup<0 minimum-player lower bound (0 = none).
        new("map_minplayers", "0", Save, "minimum players for the map (g_warmup -1 lower bound)"),
        // QC g_start_delay (xonotic-common.cfg:185-186): the pre-match join window (0 listen / 15 dedicated). Listen
        // server default; a dedicated server raises it to 15 in its config.
        new("g_start_delay", "0", Save, "delay before the game starts, so everyone can join (15 recommended on a public server)"),

        // ---- intermission / map change (xonotic-server.cfg) ----
        new("sv_mapchange_delay", "5", Save, "scoreboard hold before map change"),
        new("g_maplist_votable", "6", Save, "candidates on the end-of-match map vote"),
        new("g_maplist_votable_timeout", "30", Save, "map vote duration"),
        new("g_maplist_votable_abstain", "0", Save),

        // ---- teamplay / balance (xonotic-server.cfg) ----
        new("teamplay", "0", Notify),
        new("g_balance_teams", "1", Save, "automatic team balance"),
        new("g_balance_teams_prevent_imbalance", "1", Save),
        new("g_balance_teams_skill", "1", Save, "weigh balance by player skill, not just count"),
        // QC xonotic-server.cfg:297-298: skill-weighting tuning. The significance threshold is the z-score (in
        // standard deviations) a team-vs-player skill gap must exceed before it is treated as real; the unranked
        // factor scales the assumed skill of clients with no rating relative to the server average.
        new("g_balance_teams_skill_significance_threshold", "1.645", Save,
            "team/player skill differences count as significant past this many standard deviations"),
        new("g_balance_teams_skill_unranked_factor", "0.666", Save,
            "assumed skill of unranked clients as a fraction of the server-average skill"),
        new("g_changeteam_bantime", "30", Save),
        // QC xonotic-server.cfg:446 g_balance_kill_delay: the countdown (seconds) between a `kill` command / team
        // change and the player actually dying — the kill-indicator/announcer countdown reads this.
        new("g_balance_kill_delay", "2", Save, "delay (seconds) before a `kill` command actually kills you"),
        // QC sv_teamnagger (xonotic-server.cfg:300): team-size gap threshold for the unbalanced-teams nag; g_warmup
        // won't end while it's tripped. The warmup badteams gate (ReadyCount) reads this via Teamplay.
        new("sv_teamnagger", "2", Save, "team size difference threshold for the unbalanced-teams nag (0 = off)"),
        // QC teamplay_lockonrestart (xonotic-server.cfg:29): lock teams once the match restarts (ReadyRestart_force).
        new("teamplay_lockonrestart", "0", Save, "lock teams once all players readied up and the game restarted"),

        // ---- health / armor regen + rot (balance-xonotic.cfg; values match stock so regen works without
        //      a loaded balance cfg — the port's old 0 defaults left health regen OFF out of the box) ----
        new("g_balance_health_regen", "0.08", Notify),
        new("g_balance_health_regenlinear", "0.5", Notify),
        new("g_balance_health_regenstable", "100", Notify),
        new("g_balance_health_rot", "0.02", Notify),
        new("g_balance_health_rotlinear", "1", Notify),
        new("g_balance_health_rotstable", "100", Notify),
        new("g_balance_health_limit", "200", Notify),
        new("g_balance_armor_regen", "0", Notify),
        new("g_balance_armor_regenlinear", "0", Notify),
        new("g_balance_armor_regenstable", "100", Notify),
        new("g_balance_armor_rot", "0.02", Notify),
        new("g_balance_armor_rotlinear", "1", Notify),
        new("g_balance_armor_rotstable", "100", Notify),
        new("g_balance_armor_limit", "200", Notify),
        new("g_balance_armor_blockpercent", "0.7", Notify),
        new("g_balance_health_start", "100", Notify),
        new("g_balance_armor_start", "0", Notify),
        new("g_balance_fuel_regen", "0.1", Notify),
        new("g_balance_fuel_regenlinear", "0", Notify),
        new("g_balance_fuel_regenstable", "50", Notify),
        new("g_balance_fuel_rot", "0.05", Notify),
        new("g_balance_fuel_rotlinear", "0", Notify),
        new("g_balance_fuel_rotstable", "100", Notify),
        new("g_balance_fuel_limit", "100", Notify),
        // damage-pause windows (after a hit / after a pickup / freshly spawned). The *_spawn timers PRIME the
        // rot-pause at spawn so an above-stable spawn (e.g. 200 HP CA/LMS) holds before decaying.
        new("g_balance_pause_health_regen", "5", Notify, "no health/armor regen for N s after damage"),
        new("g_balance_pause_health_regen_spawn", "0", Notify),
        new("g_balance_pause_health_rot", "1", Notify, "no health rot for N s after a pickup"),
        new("g_balance_pause_health_rot_spawn", "5", Notify),
        new("g_balance_pause_armor_rot", "1", Notify),
        new("g_balance_pause_armor_rot_spawn", "5", Notify),
        new("g_balance_pause_fuel_regen", "2", Notify),
        new("g_balance_pause_fuel_rot", "5", Notify),
        new("g_balance_pause_fuel_rot_spawn", "10", Notify),

        // ---- combat damage (xonotic-server.cfg + balance-xonotic.cfg). The DamageSystem reads these; without
        //      them team modes did real friendly fire (mode-2 fallback) instead of stock mode-4 graphics-only. ----
        new("teamplay_mode", "4", Notify, "1=no FF, 2=FF+self, 3=no FF+self, 4=obey g_friendlyfire*/g_mirrordamage*"),
        new("g_friendlyfire", "0.5", Notify, "teamplay_mode 4 friendly-fire factor"),
        new("g_friendlyfire_virtual", "1", Notify, "show FF as graphics only (no HP lost)"),
        new("g_friendlyfire_virtual_force", "1", Notify, "still apply knockback for virtual FF"),
        new("g_mirrordamage", "0.7", Notify, "teamplay_mode 4 mirror-damage factor"),
        new("g_mirrordamage_virtual", "1", Notify, "show mirror damage as graphics only"),
        new("g_mirrordamage_onlyweapons", "0", Notify),
        new("g_teamdamage_threshold", "40", Notify, "team damage before mirror punishment kicks in"),
        new("g_teamdamage_resetspeed", "20", Notify),
        new("g_maxpushtime", "8", Notify, "credited-attacker window for environmental deaths"),
        // teleporter telefrag rules (xonotic-server.cfg:59-61; common/mapobjects/teleporters.qh autocvar_g_telefrags*).
        // Registered so a server override (set g_telefrags 0, etc.) actually applies — the port's Teleporters.cs
        // previously used hardcoded fallbacks that matched these defaults but were inert. Stock defaults: all 1.
        new("g_telefrags", "1", Notify, "enable telefragging (kill anyone standing on a teleport/portal exit)"),
        new("g_telefrags_teamplay", "1", Notify, "1 = never telefrag teammates"),
        new("g_telefrags_avoid", "1", Notify, "random-destination teleporters avoid exits where a telefrag would occur"),
        new("g_weapondamagefactor", "1", Notify, "global weapon damage multiplier"),
        new("g_weaponforcefactor", "1", Notify, "global weapon force multiplier"),
        new("g_balance_selfdamagepercent", "0.65", Notify, "self-damage (rocket/blaster-jump) scale"),
        new("g_balance_damagepush_speedfactor", "2.5", Notify),
        new("g_player_damageforcescale", "2", Notify, "knockback multiplier vs players"),
        new("g_spawnshieldtime", "1", Save, "seconds of spawn invulnerability (0 = none; lost on fire)"),
        new("g_spawnshield_blockdamage", "1", Notify, "1 = spawn shield fully blocks damage"),
        new("g_spawn_alloweffects", "3", Save, "spawn FX: 1=particles, 2=sound, 3=both"),
        new("g_spawn_furthest", "0.5", Save, "fraction of spawns biased far from players"),
        new("g_spawn_useallspawns", "0", Save),
        new("sv_gibhealth", "100", Notify, "health below -N gibs the corpse"),
        new("sv_gentle", "0", Notify, "1 = suppress gore + pain/death voices"),
        new("g_forced_respawn", "0", Save, "1 = auto-respawn at g_respawn_delay_max even without pressing fire"),

        // ---- warmup loadout (balance-xonotic.cfg g_warmup_start_*; only used while g_warmup is on) ----
        new("g_warmup_allguns", "1", Save, "warmup gives all weapons"),
        new("g_warmup_start_health", "100", Notify),
        new("g_warmup_start_armor", "100", Notify),
        new("g_warmup_start_ammo_shells", "30", Notify),
        new("g_warmup_start_ammo_nails", "160", Notify),
        new("g_warmup_start_ammo_rockets", "80", Notify),
        new("g_warmup_start_ammo_cells", "30", Notify),
        new("g_warmup_start_ammo_fuel", "0", Notify),

        // ---- contents (water/lava/slime/fall) damage (balance-contents) ----
        new("g_balance_contents_damagerate", "0.2", Notify, "tick interval for liquid/contents damage"),
        new("g_balance_contents_playerdamage_drowning", "30", Notify, "drown dps"),
        new("g_balance_contents_drowndelay", "10", Notify, "air time before drowning"),
        new("g_balance_contents_playerdamage_lava", "50", Notify),
        new("g_balance_contents_playerdamage_lava_burn", "0", Notify),
        new("g_balance_contents_playerdamage_lava_burn_time", "5", Notify),
        new("g_balance_contents_playerdamage_slime", "40", Notify),
        new("g_balance_contents_projectiledamage", "10000", Notify),
        new("g_balance_falldamage_deadminspeed", "250", Notify),
        new("g_balance_falldamage_factor", "0.15", Notify),
        new("g_balance_falldamage_minspeed", "900", Notify),
        new("g_balance_falldamage_maxdamage", "40", Notify),
        new("g_balance_falldamage_onlyvertical", "0", Notify),
        new("g_maxspeed", "0", Notify, "shooting-star kill speed (0 = off)"),

        // ---- respawn timing (xonotic-server.cfg) ----
        new("g_respawn_delay_small", "2", Save),
        new("g_respawn_delay_small_count", "0", Save, "players-per-team at which the small delay applies (<=0 = min)"),
        new("g_respawn_delay_large", "2", Save),
        new("g_respawn_delay_large_count", "8", Save, "players-per-team at which the large delay applies"),
        new("g_respawn_delay_max", "5", Save, "max wait before forced respawn (needs g_forced_respawn 1)"),
        new("g_respawn_delay_forced", "0", Save),
        new("g_respawn_waves", "0", Save),
        new("g_respawn_ghosts", "1", Save),

        // ---- bot management (bot cvars; defaults = SHIPPED xonotic-server.cfg:120-195 values — these matter
        //      when the real cfg tree isn't mounted, so they must not drift from stock) ----
        new("bot_number", "0", Save, "how many bots to keep on the server"),
        new("bot_vs_human", "0", Save, "force bots onto one team vs humans"),
        new("bot_join_empty", "0", Save),
        new("bot_prefix", "[BOT]", Save),
        new("bot_suffix", "", Save),
        new("bot_usemodelnames", "0", Save),
        new("bot_config_file", "bots.txt", Save, "name and path of the bot configuration file"),
        new("skill", "8", Save, "bot skill 0..10 (>100 = superbot); xonotic-server.cfg ships 8"),
        new("skill_auto", "0", Save),
        new("bot_god", "0", Save),
        new("bot_nofire", "0", Save),
        new("bot_ignore_bots", "0", Save),
        new("bot_typefrag", "0", Save),
        new("bot_wander_enable", "1", Save),

        // ---- bot AI tuning (bot cvars; defaults from xonotic-server.cfg:135-183) ----
        new("bot_ai_thinkinterval", "0.05", Save),
        new("bot_ai_strategyinterval", "7", Save),
        new("bot_ai_strategyinterval_movingtarget", "5.5", Save),
        new("bot_ai_enemydetectioninterval", "2", Save),
        new("bot_ai_enemydetectioninterval_stickingtoenemy", "4", Save),
        new("bot_ai_enemydetectionradius", "10000", Save),
        new("bot_ai_chooseweaponinterval", "0.5", Save),
        new("bot_ai_dangerdetectioninterval", "0.25", Save),
        new("bot_ai_dangerdetectionupdates", "64", Save),
        new("bot_ai_friends_aware_pickup_radius", "500", Save),
        new("bot_ai_ignoregoal_timeout", "3", Save),
        new("bot_ai_keyboard_distance", "250", Save),
        new("bot_ai_keyboard_threshold", "0.57", Save),
        new("bot_ai_timeitems", "1", Save, "let bots time item respawns"),
        new("bot_ai_timeitems_minrespawndelay", "25", Save),
        new("bot_ai_weapon_combo", "1", Save),
        new("bot_ai_weapon_combo_threshold", "0.4", Save),
        new("bot_ai_aimskill_offset", "1.8", Save),
        new("bot_ai_aimskill_think", "1", Save),
        new("bot_ai_aimskill_mouse", "1", Save),
        new("bot_ai_aimskill_fixedrate", "15", Save),
        new("bot_ai_aimskill_blendrate", "2", Save),
        new("bot_ai_aimskill_firetolerance", "1", Save),
        new("bot_ai_bunnyhop_skilloffset", "7", Save, "skill at/above which bots bunnyhop"),
        new("bot_ai_bunnyhop_dir_deviation_max", "20", Save),
        new("bot_ai_bunnyhop_downward_pitch_max", "30", Save),
        new("bot_ai_bunnyhop_turn_angle_min", "4", Save),
        new("bot_ai_bunnyhop_turn_angle_max", "80", Save),
        new("bot_ai_bunnyhop_turn_angle_reduction", "40", Save),
        new("bot_ai_custom_weapon_priority_distances", "300 850", Save),
        new("bot_ai_custom_weapon_priority_far", "", Save),
        new("bot_ai_custom_weapon_priority_mid", "", Save),
        new("bot_ai_custom_weapon_priority_close", "", Save),
        new("bot_navigation_ignoreplayers", "0", Save),

        // ---- server identity / admin (xonotic-server.cfg) ----
        new("sv_dedicated", "0", CvarFlags.ReadOnly),
        new("sv_spectate", "1", Save),
        new("hostname", "Xonotic XonoticGodot Server", Save),
        new("g_maplist", "", Save, "the map rotation"),

        // ---- voting (server/command/vote.qc) ----
        new("sv_vote_call", "1", Save, "allow players to call votes"),
        new("sv_vote_change", "0", Save, "allow changing a vote after casting"),
        new("sv_vote_commands", "restart fraglimit chmap gotomap nextmap endmatch reducematchtime extendmatchtime allready kick kickban cointoss shuffleteams", Save, "votable command whitelist"),
        new("sv_vote_master", "0", Save, "allow vote-master subsystem"),
        new("sv_vote_master_callable", "0", Save, "allow calling a vote to become master"),
        new("sv_vote_master_commands", "movetoauto movetored movetoblue movetoyellow movetopink movetospec", Save, "extra commands the master may run"),
        new("sv_vote_master_password", "", Save, "password to log in as vote master ('' = disabled)"),
        new("sv_vote_master_playerlimit", "2", Save, "min players for a master vote to pass"),
        new("sv_vote_singlecount", "0", Save, "don't recount on each vote (only at timeout)"),
        new("sv_vote_timeout", "30", Save, "seconds a called vote stays open"),
        new("sv_vote_wait", "120", Save, "cooldown after calling a vote"),
        new("sv_vote_stop", "5", Save, "cooldown after stopping your own vote"),
        new("sv_vote_majority_factor", "0.5", Save, "overall yes-fraction to pass"),
        new("sv_vote_majority_factor_of_voted", "0.5", Save, "yes-fraction of those who voted (at timeout)"),
        new("sv_vote_no_stops_vote", "0", Save, "caller voting no cancels their own vote"),
        new("sv_vote_nospectators", "0", Save, "0=spectators vote, 1=except warmup/intermission, 2=never"),
        new("sv_vote_gamestart", "0", Save, "allow vote calling before the match starts"),
        new("sv_vote_limit", "80", Save, "max votable command length (0 = no limit)"),
        new("sv_vote_debug", "0", Save, "include bots as voters; print debug banner"),
        new("sv_vote_override_mostrecent", "0", Save, "allow voting a recently-played map"),

        // ---- cheats (server/cheats.qc) ----
        new("sv_cheats", "0", Notify, "0=off, 1=cheats, 2=cheats for non-players + wider teleport"),
        new("g_grab_range", "200", Save, "non-cheat object drag range"),
        new("g_max_info_autoscreenshot", "60", Save),
        new("sv_clones", "0", Save, "max clones a player may spawn (cheat)"),
        new("g_allow_checkpoints", "0", Save, "speedrun checkpoint teleport is not a cheat"),

        // ---- bans (server/ipban.qc + command/banning.qc) ----
        new("g_banned_list", "", Save, "serialized local ban list"),
        new("g_banned_list_idmode", "1", Save, "IP bans only apply to clients without a crypto id"),
        new("g_ban_telluser", "1", Save, "tell a kicked client they are banned"),
        new("g_ban_default_bantime", "5400", Save, "default ban duration (s)"),
        new("g_ban_default_masksize", "3", Save, "default IP mask size (1..4)"),
        new("g_chatban_list", "", Save, "muted players (IP/id prefixes)"),
        new("g_playban_list", "", Save, "forced-spectate players (IP/id prefixes)"),
        new("g_voteban_list", "", Save, "vote-banned players (IP/id prefixes)"),
        new("g_playban_minigames", "1", Save, "also remove play-banned players from minigames"),

        // ---- event log + player stats (server/gamelog.qc + common/playerstats.qc) ----
        new("sv_eventlog", "0", Save, "enable the event log"),
        new("sv_eventlog_console", "0", Save, "echo the event log to the console"),
        new("sv_eventlog_files", "0", Save, "write the event log to files"),
        new("sv_eventlog_files_counter", "0", Save, "match counter for log filenames"),
        new("sv_eventlog_files_nameprefix", "ServerLog-", Save),
        new("sv_eventlog_files_namesuffix", ".log", Save),
        new("sv_eventlog_files_timestamps", "1", Save),
        new("sv_eventlog_ipv6_delimiter", "0", Save, "replace ':' with '_' in logged IPs"),
        new("g_playerstats_gamereport_uri", "", Save, "stats upload endpoint ('' = disabled)"),

        // ---- diagnostics / logging (engine cvar; gates lib/log.qh → XonoticGodot.Common.Diagnostics.Log) ----
        // QC autocvar_developer: 0 = off, 1 = LOG_TRACE + LOG_INFO headers, 2 = + LOG_DEBUG and full source
        // locations. Read live by Log.Emit on every call, so `set developer 1` / `toggle developer` are instant.
        new("developer", "0", "developer/debug log verbosity (0=off, 1=trace+info, 2=+debug)"),

        // ---- client physics selection (server/command/cmd.qc ClientCommand_physics). Ships 0 → the `physics`
        //      command prints "disabled" unless an admin opts in (physics.cfg). Options string is physics.cfg's. ----
        new("g_physics_clientselect", "0", Save, "allow clients to select their physics set"),
        new("g_physics_clientselect_options", "xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf", Save, "the selectable physics sets"),

        // ---- monster editor (server/command/common.qc editmob). monsters.cfg ships g_monsters_edit 0,
        //      g_monsters_max 20, g_monsters_max_perplayer 0 — so editmob spawn is OFF by default (per-player 0). ----
        new("g_monsters_edit", "0", Save, "0=off, 1=edit own monsters, 2=edit any (admin)"),
        new("g_monsters_max", "20", Save, "max monsters alive server-wide"),
        new("g_monsters_max_perplayer", "0", Save, "max monsters a single player may have (0 = none)"),

        // ---- campaign (server/campaign.qc) ----
        new("g_campaign", "0", "in-campaign master switch"),
        new("g_campaign_skill", "0", Save, "global bot-skill offset for the campaign"),
        new("g_campaign_forceteam", "0"),
        new("_campaign_index", "0", "current campaign map index"),
        new("_campaign_name", "", "current campaign id"),
        new("_campaign_testrun", "0", "campaign test run (instant win)"),

        // ---- timeout / timein (server/command/common.qc) ----
        new("sv_timeout", "0", Save, "allow players to pause via timeout"),
        new("sv_timeout_leadtime", "4", Save, "countdown before a timeout begins"),
        new("sv_timeout_length", "120", Save, "max timeout duration"),
        new("sv_timeout_number", "2", Save, "timeouts allowed per player"),
        new("sv_timeout_resumetime", "3", Save, "countdown when resuming"),

        // ---- map rotation / map vote (server/intermission.qc + mapvoting.qc) ----
        new("g_maplist_index", "0", Save, "rotation cursor into g_maplist"),
        new("g_maplist_mostrecent", "", Save, "recently played maps (excluded from rotation)"),
        new("g_maplist_mostrecent_count", "3", Save, "how many recent maps to remember"),
        new("g_maplist_shuffle", "1", Save, "shuffle the rotation on load"),
        new("g_maplist_selectrandom", "0", Save, "pick the next map at random"),
        new("g_maplist_votable_reduce_time", "15", Save, "reduce the number of options shown after this amount of time during the vote; 0 = disable"),
        new("g_maplist_votable_reduce_count", "2", Save, "options kept after reduce; if < 2, keep all maps that got any votes (if at least 2 did)"),
        new("g_maplist_votable_show_suggester", "1", Save, "show which player suggested a map"),
        new("g_maplist_votable_suggestions", "2", Save, "player-suggested map slots on the ballot"),
        new("g_maplist_votable_suggestions_override_mostrecent", "0", Save),
        new("sv_vote_gametype", "0", Save, "run a gametype vote before the map vote"),
        new("sv_vote_gametype_timeout", "20", Save),
        new("sv_vote_gametype_options", "dm tdm ctf", Save),
        new("samelevel", "0", "restart the same map instead of rotating"),
        new("lastlevel", "0"),
        new("timelimit_increment", "5", Save, "minutes added by extendmatchtime"),
        new("timelimit_decrement", "5", Save, "minutes removed by reducematchtime"),
        new("timelimit_min", "5", Save),
        new("timelimit_max", "60", Save),

        // ---- demo recording (engine sv_autodemo; host-driven) ----
        new("sv_autodemo", "0", Save, "record a server demo of the whole match"),
        new("sv_autodemo_perclient", "0", Save, "record a per-client demo (0=off,1=players,2=all)"),
        new("sv_autodemo_perclient_discardable", "0", Save, "per-client demos may be auto-deleted"),

        // ---- world-item pickups (T35: common/items/* + server/items/items.qc; values from balance-xonotic.cfg
        //      + _all.cfg). NOTE: the SHIPPED g_pickup_items / g_powerups default is -1 ("use gametype default"),
        //      which needs a per-gametype computation the headless port doesn't do — so we seed 1 (force-spawn)
        //      here so stock maps spawn their items + powerups out of the box. A host can set 0 / -1 over the top.
        new("g_pickup_items", "1", "force item spawning (-1 = gametype default; seeded 1 so items spawn)"),
        new("g_weapon_stay", "0", Save, "1 = ghost weapons (no ammo), 2 = ghost weapons refill ammo"),
        new("g_items_dropped_lifetime", "20", "seconds a dropped loot item survives before despawning"),
        new("g_pickup_weapons_anyway", "0", "always pick up a weapon even if already owned"),

        // powerups: master + per-type toggles (seeded on, like _all.cfg's per-type 1s) + the balance durations.
        new("g_powerups", "1", "force powerup spawning (-1 = gametype default; seeded 1)"),
        new("g_powerups_stack", "0", "stack powerup timers on re-pickup instead of refreshing"),
        new("g_powerups_strength", "1", "allow Strength powerups to spawn"),
        new("g_powerups_shield", "1", "allow Shield powerups to spawn"),
        new("g_powerups_speed", "1", "allow Speed powerups to spawn"),
        new("g_powerups_invisibility", "1", "allow Invisibility powerups to spawn"),
        new("g_powerups_jetpack", "1", "allow Jetpacks to spawn"),
        new("g_powerups_fuelregen", "1", "allow Fuel Regenerators to spawn"),
        new("g_balance_powerup_strength_time", "30", Notify, "Strength powerup duration (s)"),
        new("g_balance_powerup_invincible_time", "30", Notify, "Shield powerup duration (s)"),
        new("g_balance_powerup_speed_time", "30", Notify, "Speed powerup duration (s)"),
        new("g_balance_powerup_invisibility_time", "30", Notify, "Invisibility powerup duration (s)"),
        new("g_balance_superweapons_time", "30", Notify, "default superweapon ammo duration (s)"),

        // health pickups (amount + per-pickup cap). balance-xonotic.cfg.
        new("g_pickup_healthsmall", "5", Notify), new("g_pickup_healthsmall_max", "200", Notify),
        new("g_pickup_healthmedium", "25", Notify), new("g_pickup_healthmedium_max", "200", Notify),
        new("g_pickup_healthbig", "50", Notify), new("g_pickup_healthbig_max", "200", Notify),
        new("g_pickup_healthmega", "100", Notify), new("g_pickup_healthmega_max", "200", Notify),

        // armor pickups (amount + per-pickup cap). balance-xonotic.cfg.
        new("g_pickup_armorsmall", "5", Notify), new("g_pickup_armorsmall_max", "200", Notify),
        new("g_pickup_armormedium", "25", Notify), new("g_pickup_armormedium_max", "200", Notify),
        new("g_pickup_armorbig", "50", Notify), new("g_pickup_armorbig_max", "200", Notify),
        new("g_pickup_armormega", "100", Notify), new("g_pickup_armormega_max", "200", Notify),

        // ammo pickups (amount + per-resource cap). balance-xonotic.cfg. (bullets cvar is g_pickup_nails.)
        new("g_pickup_shells", "15", Notify), new("g_pickup_shells_max", "60", Notify),
        new("g_pickup_nails", "80", Notify), new("g_pickup_nails_max", "320", Notify),
        new("g_pickup_rockets", "40", Notify), new("g_pickup_rockets_max", "160", Notify),
        new("g_pickup_cells", "30", Notify), new("g_pickup_cells_max", "180", Notify),
        new("g_pickup_fuel", "50", Notify), new("g_pickup_fuel_max", "100", Notify),
        new("g_pickup_fuel_jetpack", "100", Notify, "fuel a Jetpack pickup grants"),

        // respawn times (xonotic-server.cfg / balance). Per-class for health/armor; shared for ammo; long for powerups.
        new("g_pickup_respawntime_health_small", "15", Save), new("g_pickup_respawntime_health_medium", "15", Save),
        new("g_pickup_respawntime_health_big", "20", Save), new("g_pickup_respawntime_health_mega", "30", Save),
        new("g_pickup_respawntime_armor_small", "15", Save), new("g_pickup_respawntime_armor_medium", "20", Save),
        new("g_pickup_respawntime_armor_big", "30", Save), new("g_pickup_respawntime_armor_mega", "30", Save),
        new("g_pickup_respawntime_ammo", "10", Save),
        new("g_pickup_respawntime_powerup", "120", Save),
        new("g_pickup_respawntime_weapon", "10", Save),
        new("g_pickup_respawntime_superweapon", "120", Save),

        // (§12.8) DP-faithful networking entity culling (DP sv_send.c SV_MarkWriteEntityStateToClient): the
        // server stops SENDING a client the entities whose bounds fall outside that client's PVS — the bandwidth
        // + draw-call win, and the half of DP's entity culling we were missing (we only used PVS for bot LOS).
        // Box-tested per recipient (conservative: a model spanning a cluster boundary stays sent; a viewer in
        // solid / unvised map / dead-or-spectating recipient sends everything). Default 1 = DP's default.
        // See ServerNet.BuildEntitySet/RelevantEntitiesFor.
        new("sv_cullentities_pvs", "1", Save, "PVS-cull networked entities per client (DP SV_MarkWriteEntityStateToClient)"),
    };

    /// <summary>
    /// Register every entry of <see cref="Defaults"/> into the ambient <see cref="ICvarService"/> (QC the
    /// autocvar registration done as the configs are exec'd at startup). Idempotent per-name: an already-set
    /// cvar keeps its value (so a host that set overrides before boot wins), matching
    /// <see cref="ICvarService.Register"/>'s "registration is idempotent" contract. Safe to call from
    /// <see cref="GameWorld.Boot"/> after the facade is published.
    /// </summary>
    public static void RegisterDefaults()
    {
        if (Api.Services is null)
            return;
        foreach (CvarDef d in Defaults)
            Api.Cvars.Register(d.Name, d.DefaultValue, d.Flags);
    }

    /// <summary>Register one extra cvar default (a host/mutator adding its own autocvar at boot).</summary>
    public static void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None)
    {
        if (Api.Services is not null)
            Api.Cvars.Register(name, defaultValue, flags);
    }

    // =============================================================================================
    // typed accessors (QC the autocvar_* globals + cvar()/cvar_string()/bound reads)
    // =============================================================================================

    /// <summary>QC <c>autocvar_NAME</c> as a float (0 if unset). Equivalent to <c>cvar(name)</c>.</summary>
    public static float Float(string name) => Api.Services is not null ? Api.Cvars.GetFloat(name) : 0f;

    /// <summary>QC <c>autocvar_NAME</c> as a string ("" if unset). Equivalent to <c>cvar_string(name)</c>.</summary>
    public static string String(string name) => Api.Services is not null ? Api.Cvars.GetString(name) : "";

    /// <summary>QC <c>cvar(name) != 0</c> — the common bool-cvar idiom.</summary>
    public static bool Bool(string name) => Float(name) != 0f;

    /// <summary>QC <c>(int)cvar(name)</c>.</summary>
    public static int Int(string name) => (int)Float(name);

    /// <summary>
    /// Read a float cvar but fall back to <paramref name="fallback"/> when the cvar is unset/empty (QC the
    /// frequent <c>x = cvar(name); if (!x) x = default;</c> pattern). Distinguishes "unset" from "set to 0".
    /// </summary>
    public static float FloatOr(string name, float fallback)
    {
        if (Api.Services is null)
            return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>QC <c>bound(lo, cvar(name), hi)</c>: a clamped float-cvar read.</summary>
    public static float Bound(string name, float lo, float hi)
    {
        float v = Float(name);
        return v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Set a cvar through the facade (QC <c>cvar_set</c>); honors the ReadOnly flag.</summary>
    public static void Set(string name, string value)
    {
        if (Api.Services is not null)
            Api.Cvars.Set(name, value);
    }

    /// <summary>Set a numeric cvar (QC <c>cvar_set(name, ftos(value))</c>).</summary>
    public static void Set(string name, float value)
        => Set(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // =============================================================================================
    // a few hot, named convenience reads (QC autocvar_* the server core touches every frame)
    // =============================================================================================

    public static float Gravity => FloatOr("sv_gravity", 800f);
    public static float MaxSpeed => FloatOr("sv_maxspeed", 360f);      // stock Xonotic (physicsX.cfg)
    public static float JumpVelocity => FloatOr("sv_jumpvelocity", 260f); // stock Xonotic (physicsX.cfg)
    public static float TimeLimitMinutes => Float("timelimit");
    public static float FragLimit => Float("fraglimit");
    public static bool Teamplay => Bool("teamplay");
    public static bool WarmupStage => Bool("g_warmup");
    public static float WarmupLimit => FloatOr("g_warmup_limit", -1f);
    public static float MapChangeDelay => FloatOr("sv_mapchange_delay", 5f);
    public static int BotNumber => Int("bot_number");
    public static float Skill => FloatOr("skill", 8f); // xonotic-server.cfg:129 ships skill 8
}
