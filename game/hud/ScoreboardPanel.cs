using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Scoreboard overlay — port of the core of Base/.../qcsrc/client/hud/panel/scoreboard.qc (HUD panel #25).
/// The QC scoreboard is a CONFIGURABLE column grid driven by the networked <c>scores</c> stat fields: the
/// active <c>scoreboard_columns</c> (else SCOREBOARD_DEFAULT_COLUMNS) selects which SP_* columns show, filtered
/// per-gametype (Cmd_Scoreboard_SetFields), each value formatted by its SFL_* flags (Scoreboard_GetField /
/// ScoreString), rows sorted by the per-mode primary/secondary keys (Scoreboard_ComparePlayerScores), grouped
/// into team sections with per-team totals in teamplay. We port that faithfully (pragmatically): a per-mode
/// column list, value formatting, per-mode sort, team panels, plus the map-stats / respawn / fraglimit header
/// and (stubbed) accuracy + rankings blocks.
///
/// Data source: other players' columns/name/team are a networked thing. The net layer pushes rows via
/// <see cref="SetWireRows"/> (the decoded <see cref="XonoticGodot.Net.ScoreboardWire"/> — full column values per
/// player) which is the column-driven path, or via <see cref="SetRows"/> (a <see cref="ScoreRow"/> per player)
/// for the simpler #/name/score/deaths/ping view. The active per-mode column layout comes from the networked
/// <see cref="GameScores"/> labels/flags (set by the ScoreInfo block, QC ENT_CLIENT_SCORES_INFO).
/// </summary>
public partial class ScoreboardPanel : HudPanel
{
    // The scoreboard only repaints when its data changes (or it's toggled), not every frame.
    public override bool IsDynamic => false;

    /// <summary>
    /// One scoreboard row — the networked per-player score record (QC scores[player] + the entcs name/team
    /// slice). <see cref="Columns"/> holds the full registry-indexed column values (QC <c>pl.(scores(field))</c>)
    /// when fed from the wire; the simple <see cref="SetRows"/>/<see cref="SetPlayers"/> path leaves it null and
    /// only the #/name/score/deaths/ping view renders.
    /// </summary>
    public readonly struct ScoreRow
    {
        /// <summary>Display name, may contain ^N color codes (QC entcs name).</summary>
        public readonly string Name;
        /// <summary>Team color code (QC NUM_TEAM_*; <see cref="Teams.None"/> for FFA). 0 = no team.</summary>
        public readonly int Team;
        /// <summary>Match score / frags (QC SP_SCORE).</summary>
        public readonly int Score;
        /// <summary>Deaths (QC SP_DEATHS); &lt; 0 = unknown/not networked.</summary>
        public readonly int Deaths;
        /// <summary>Ping in ms (QC SP_PING); &lt; 0 = unknown (bot / not networked).</summary>
        public readonly int Ping;
        /// <summary>QC <c>pl.ping_packetloss</c> (SP_PL): the player's packet loss as a 0..1 fraction
        /// (de-quantized from the networked byte). 0 = no loss (or bot / unknown) → the SP_PL cell is blank.</summary>
        public readonly float PacketLoss;
        /// <summary>True if this row is the local player (highlighted). Set by the feeder or matched by name.</summary>
        public readonly bool IsLocal;
        /// <summary>True if this player is eliminated for the round (QC <c>pl.eliminated</c>, the networked
        /// eliminatedPlayers bitfield: CA dead / FT frozen-or-dead / Survival out) — greys the row.</summary>
        public readonly bool Eliminated;
        /// <summary>Full registry-indexed column values (QC scores(field)) when fed from the wire; else null.</summary>
        public readonly int[]? Columns;
        /// <summary>QC <c>.handicap_level</c> (entcs, scoreboard.qc:1003): 0..16; 0 = no handicap. When nonzero the
        /// row draws the <c>player_handicap</c> icon tinted white@1 → red@16 next to the name.</summary>
        public readonly int HandicapLevel;
        /// <summary>QC <c>pl.sv_entnum</c> stand-in: the player's stable net id, used for the playerid '#N' name
        /// prefix (Scoreboard_AddPlayerId) and the final sort tiebreak. 0 = none.</summary>
        public readonly int NetId;

        public ScoreRow(string name, int score, int team = 0, int deaths = -1, int ping = -1,
            bool isLocal = false, int[]? columns = null, bool eliminated = false, float packetLoss = 0f,
            int handicapLevel = 0, int netId = 0)
        {
            Name = name ?? "";
            Score = score;
            Team = team;
            Deaths = deaths;
            Ping = ping;
            PacketLoss = packetLoss;
            IsLocal = isLocal;
            Eliminated = eliminated;
            Columns = columns;
            HandicapLevel = handicapLevel;
            NetId = netId;
        }

        /// <summary>QC <c>pl.(scores(field))</c>: read a column value (0 when not networked / no column data).</summary>
        public int Col(ScoreField f) => (Columns is not null && (uint)f.RegistryId < Columns.Length) ? Columns[f.RegistryId] : 0;
    }

    /// <summary>The local player, whose row is highlighted (set by <see cref="Hud"/>).</summary>
    public Player? LocalPlayer { get; set; }

    /// <summary>The match title shown atop the table (e.g. "Deathmatch"). Settable by the owner.</summary>
    public string Title { get; set; } = "Scoreboard";

    /// <summary>QC <c>MapInfo_Type_ToText(gametype)</c>: the gametype name banner drawn big at the top-right of the
    /// game-info section (e.g. "Deathmatch", "Capture the Flag"). Falls back to <see cref="Title"/> when empty.</summary>
    public string GametypeName { get; set; } = "";

    /// <summary>QC <c>GET_NEXTMAP()</c>: the next map shown ("Next map: …") above the gametype banner; "" = none.</summary>
    public string NextMap { get; set; } = "";

    /// <summary>QC <c>numplayers</c> / <c>srv_maxplayers</c>: the "N/M players" line in the game-info footer
    /// (the map-info line). 0/0 = hidden (e.g. campaign).</summary>
    public int PlayerCount { get; set; }
    public int MaxPlayerCount { get; set; }

    /// <summary>True when the active gametype is teamplay (groups rows into team sections + shows team totals).</summary>
    public bool TeamPlay { get; set; }

    /// <summary>QC <c>gametype.m_hidelimits</c> (mapinfo.qh:50, GAMETYPE_FLAG_HIDELIMITS; scoreboard.qc:2551):
    /// when set the fraglimit + leadlimit terms are suppressed from the game-info limits line (only the timelimit
    /// shows). The only stock gametype that sets it is LMS (lms.qh:11). Fed by the match layer.</summary>
    public bool HideLimits { get; set; }

    /// <summary>QC global <c>campaign</c> (scoreboard.qc:2574): in a single-player campaign the "N/M players" map
    /// line is suppressed (the count is meaningless). Fed by the match layer.</summary>
    public bool Campaign { get; set; }

    // ---- spectator list (QC Scoreboard_Spectators_Draw) ----

    /// <summary>One spectator entry (QC the NUM_SPECTATOR rows: name + ping). Fed via <see cref="SetSpectators"/>.</summary>
    public readonly struct SpectatorRow
    {
        public readonly string Name;   // may carry ^N color codes
        public readonly int Ping;      // ms; &lt; 0 = unknown (bot / not networked)
        public SpectatorRow(string name, int ping = -1) { Name = name ?? ""; Ping = ping; }
    }

    private readonly List<SpectatorRow> _spectators = new();

    // ---- fade in/out (QC scoreboard_fade_alpha + fadeinspeed/fadeoutspeed) ----

    /// <summary>QC <c>scoreboard_active</c>: the owner sets this true while the scoreboard key is held (or on
    /// the death/intermission scoreboard); the panel fades <see cref="_fadeAlpha"/> in/out toward it and hides
    /// itself once fully faded out. Replaces the raw <see cref="Godot.CanvasItem.Visible"/> toggle so the
    /// scoreboard cross-fades like QC instead of popping. The owner may still set Visible directly (legacy).</summary>
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; if (value) Visible = true; QueueRedraw(); } }
    }
    private bool _active;
    private float _fadeAlpha;        // 0..1 current fade (QC scoreboard_fade_alpha)

    /// <summary>The current fade level 0..1 (QC scoreboard_fade_alpha) so the owner can also drive the manager's
    /// non-scoreboard panel cross-fade if it wants. Read-only.</summary>
    public float FadeAlpha => _fadeAlpha;

    // ---- header / footer settable surfaces (QC fraglimit/timelimit + map stats + respawn; networked) ----

    /// <summary>QC the fraglimit / pointlimit header value (Scoreboard_Fraglimit_Draw); 0 = none. Settable by the match layer.</summary>
    public int FragLimit { get; set; }
    /// <summary>QC TIMELIMIT (minutes); 0 = none. Settable by the match layer.</summary>
    public int TimeLimitMinutes { get; set; }
    /// <summary>QC <c>STAT(LEADLIMIT)</c> (scoreboard.qc:2546): the lead limit shown as the "^2+N" header term
    /// (Scoreboard_Fraglimit_Draw is_leadlimit=true). 0 = none. Drawn only when ll &gt; 0 &amp;&amp; (ll &lt; fl || fl &lt;= 0).</summary>
    public int LeadLimit { get; set; }
    /// <summary>QC <c>STAT(LEADLIMIT_AND_FRAGLIMIT)</c> (autocvar_leadlimit_and_fraglimit, scoreboard.qc:2547,2564):
    /// when set (and fraglimit &gt; 0) the lead/frag delimiter is "^7 &amp; " (both required) instead of "^7 / ".</summary>
    public bool LeadAndFragLimit { get; set; }
    /// <summary>QC the map name shown in the footer (Scoreboard footer "<map>"). Settable by the match layer.</summary>
    public string MapName { get; set; } = "";

    // Map stats (QC Scoreboard_MapStats_Draw STAT(MONSTERS_*/SECRETS_*)); -1 totals = no row. Networked → settable.
    public int MonstersKilled { get; set; } = -1;
    public int MonstersTotal { get; set; } = -1;
    public int SecretsFound { get; set; } = -1;
    public int SecretsTotal { get; set; } = -1;

    /// <summary>QC <c>STAT(RESPAWN_TIME)</c> (scoreboard.qc:2764) as networked to the owner
    /// (ClientNet.RespawnTimeStat): 0 = alive (no respawn line); otherwise the absolute respawn time, NEGATED
    /// while a respawn is imminent (DEAD_RESPAWNING). Fed by the match layer each frame; drives the three-state
    /// respawn line. Counted down against <see cref="RespawnServerTime"/> (the networked server time).</summary>
    public float RespawnStat { get; set; }

    /// <summary>The latest networked server time (ClientNet.LatestServerTime) to count <see cref="RespawnStat"/>
    /// down against (QC <c>time</c>). Fed alongside <see cref="RespawnStat"/>.</summary>
    public float RespawnServerTime { get; set; }

    /// <summary>QC <c>getcommandkey(_("jump"), "+jump")</c>: the key bound to +jump, shown in the "press X to
    /// respawn" line. Fed by the match layer (keybind lookup); defaults to "jump".</summary>
    public string RespawnJumpKey { get; set; } = "jump";

    // Accuracy grid (QC Scoreboard_AccuracyStats_Draw weapon_accuracy[]): per-weapon-id hit percentage [0..100],
    // -1 = the weapon was never fired (skipped). Networking the local player's accuracy is a follow-up
    // (HudManager's WeaponsPanel.SetAccuracy seam); until then this is empty and the grid is hidden.
    private readonly Dictionary<int, int> _accuracy = new();

    // Rankings (QC Scoreboard_Rankings_Draw race/CTS grecordtime/grecordholder). Race record networking is its
    // own data source (not present yet) — so this is left empty and the block is gated on race modes + data.
    private readonly List<(int timeEncoded, string holder)> _rankings = new();

    private readonly List<ScoreRow> _rows = new();
    private readonly Dictionary<int, int> _teamScores = new(); // team color code -> team score (QC team scores)

    // The parsed column layout (QC sbt_field[]), rebuilt when the layout generation changes.
    private readonly List<Column> _columns = new();
    private int _columnsForLayoutGen = -1;
    private string _columnsForSpec = "";

    /// <summary>Hidden by default; the owner toggles it (QC: held while the scoreboard key is down).</summary>
    public ScoreboardPanel() => Visible = false;

    /// <summary>
    /// QC <c>autocvar_scoreboard_columns</c>: an explicit column spec ("ping pl name | score …"); empty selects
    /// the built-in SCOREBOARD_DEFAULT_COLUMNS. Settable by the owner (the user's cvar). Triggers a relayout.
    /// </summary>
    public string ColumnSpec
    {
        get => _columnSpec;
        set { _columnSpec = value ?? ""; _columnsForLayoutGen = -1; QueueRedraw(); }
    }
    private string _columnSpec = "";

    // =====================================================================================
    //  Feed paths
    // =====================================================================================

    /// <summary>
    /// THE net path: replace the rows from a decoded <see cref="XonoticGodot.Net.ScoreboardWire"/> (full per-player
    /// columns + the entcs name/team slice). Resolves netId→local via <paramref name="localNetId"/>, hydrates
    /// each row's full column array, sets <see cref="TeamPlay"/> from <see cref="GameScores.Teamplay"/>, and
    /// applies the team totals. This is what makes the networked columns actually render (QC the scores stats →
    /// the scoreboard grid). Maps the wire columns (in <see cref="GameScores.NetworkedFields"/> order) back to a
    /// registry-indexed array so <see cref="ScoreRow.Col"/> reads the right field.
    /// </summary>
    public void SetWireRows(XonoticGodot.Net.ScoreboardWire wire, int localNetId,
        IReadOnlyCollection<int>? eliminatedNetIds = null)
    {
        _rows.Clear();
        _spectators.Clear();
        if (wire is not null)
        {
            IReadOnlyList<ScoreField> netFields = GameScores.NetworkedFields;
            int fieldCount = GameScores.FieldCount;
            ScoreField? scoreF = GameScores.Field("SCORE");
            ScoreField? deathsF = GameScores.Field("DEATHS");
            foreach (XonoticGodot.Net.ScoreRowWire wr in wire.Rows)
            {
                // QC Scoreboard_Spectators_Draw (scoreboard.qc:2369): a spectator/observer is NOT a score-table
                // row — list it in the spectator block instead. The wire carries the flag (the port has no
                // NUM_SPECTATOR team sentinel). Feed the networked per-row ping so spectators_showping renders it.
                if (wr.IsSpectator)
                {
                    _spectators.Add(new SpectatorRow(wr.Name, ping: wr.PingMs));
                    continue;
                }

                // expand the wire's NetworkedFields-ordered columns into a registry-indexed array. fieldCount can
                // be 0 before the score registry is populated, and wr.Columns may be null (the ScoreRowWire ctor
                // doesn't guard it) — both would crash the foreach below, so clamp/null-coalesce here.
                var cols = new int[System.Math.Max(0, fieldCount)];
                int[] wireCols = wr.Columns ?? System.Array.Empty<int>();
                int m = System.Math.Min(wireCols.Length, netFields.Count);
                for (int i = 0; i < m; i++)
                {
                    int rid = netFields[i].RegistryId;
                    if ((uint)rid < cols.Length) cols[rid] = wireCols[i];
                }

                int score = scoreF is not null && (uint)scoreF.RegistryId < cols.Length ? cols[scoreF.RegistryId] : 0;
                int deaths = deathsF is not null && (uint)deathsF.RegistryId < cols.Length ? cols[deathsF.RegistryId] : -1;
                _rows.Add(new ScoreRow(wr.Name, score, wr.Team, deaths, ping: wr.PingMs,
                    isLocal: wr.NetId == localNetId, columns: cols,
                    // QC pl.ping_packetloss: de-quantize the networked 0..255 loss byte to a 0..1 fraction.
                    packetLoss: wr.PacketLossByte / 255f,
                    // QC pl.eliminated (NET_HANDLE ENT_CLIENT_ELIMINATEDPLAYERS, client/main.qc:819): flag the
                    // rows the round-status block marked eliminated so DrawRow greys them.
                    eliminated: eliminatedNetIds is not null && eliminatedNetIds.Contains(wr.NetId),
                    // QC entcs handicap_level (scoreboard.qc:1003): the player_handicap icon level (0 = none).
                    handicapLevel: wr.HandicapLevel,
                    // QC pl.sv_entnum: the stable id for the playerid '#N' prefix + the final sort tiebreak.
                    netId: wr.NetId));
            }

            _teamScores.Clear();
            foreach ((int team, int sc) in wire.Teams)
                if (team != Teams.None) _teamScores[team] = sc;
        }
        TeamPlay = GameScores.Teamplay || _teamScores.Count > 0;
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Replace the scoreboard rows from the simple networked score records. The list is copied and sorted for
    /// display. Kept for callers that only have the #/name/score/deaths/ping slice.
    /// </summary>
    public void SetRows(IEnumerable<ScoreRow> rows)
    {
        _rows.Clear();
        if (rows is not null) _rows.AddRange(rows);
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Convenience: build rows from local <see cref="Player"/> actors. Name/score/team come from the entity
    /// (QC .netname/.frags/.team); deaths/ping are left unknown (only the server knows them) until the net
    /// layer feeds full rows via <see cref="SetRows"/>. The row matching <see cref="LocalPlayer"/> is flagged.
    /// </summary>
    public void SetPlayers(IEnumerable<Player> players)
    {
        _rows.Clear();
        if (players is not null)
            foreach (Player p in players)
            {
                if (p is null) continue;
                string name = string.IsNullOrEmpty(p.NetName) ? p.ClassName : p.NetName;
                // deaths/ping are server-only; leave unknown (-1) until full rows arrive via SetRows.
                _rows.Add(new ScoreRow(name, p.ScoreFrags, (int)p.Team,
                    deaths: -1, ping: -1, isLocal: ReferenceEquals(p, LocalPlayer)));
            }
        SortRows();
        QueueRedraw();
    }

    /// <summary>
    /// Set per-team scores for the team-panel totals (QC team scores), keyed by team color code
    /// (<see cref="Teams.Red"/> etc.). Implies <see cref="TeamPlay"/> when non-empty.
    /// </summary>
    public void SetTeamScores(IReadOnlyDictionary<int, int> teamScores)
    {
        _teamScores.Clear();
        if (teamScores is not null)
            foreach (var kv in teamScores) _teamScores[kv.Key] = kv.Value;
        TeamPlay = _teamScores.Count > 0;
        QueueRedraw();
    }

    /// <summary>QC <c>weapon_accuracy[]</c>: set the local player's per-weapon hit % (0..100; -1 = never fired)
    /// for the accuracy grid. Keyed by weapon registry id. The match layer feeds it when accuracy is networked.</summary>
    public void SetAccuracy(IReadOnlyDictionary<int, int> accuracy)
    {
        _accuracy.Clear();
        if (accuracy is not null) foreach (var kv in accuracy) _accuracy[kv.Key] = kv.Value;
        QueueRedraw();
    }

    /// <summary>QC <c>Scoreboard_Spectators_Draw</c> source: replace the spectator list (NUM_SPECTATOR players —
    /// the entcs slice with no/forfeit scores). Fed by the net layer; empty hides the section.</summary>
    public void SetSpectators(IEnumerable<SpectatorRow> spectators)
    {
        _spectators.Clear();
        if (spectators is not null) _spectators.AddRange(spectators);
        QueueRedraw();
    }

    /// <summary>Convenience overload: feed spectator names only (ping unknown). Kept for simple callers.</summary>
    public void SetSpectators(IEnumerable<string> names)
    {
        _spectators.Clear();
        if (names is not null) foreach (string n in names) _spectators.Add(new SpectatorRow(n));
        QueueRedraw();
    }

    /// <summary>QC the race/CTS rankings (Scoreboard_Rankings_Draw): an ordered best-time list (encoded
    /// hundredths + holder name). Networking records is a follow-up; until then this is empty.</summary>
    public void SetRankings(IEnumerable<(int timeEncoded, string holder)> rankings)
    {
        _rankings.Clear();
        if (rankings is not null) _rankings.AddRange(rankings);
        QueueRedraw();
    }

    // ---- race/CTS speed award (QC scoreboard.qc:2731 race_speedaward / _alltimebest) ----
    private int _speedAward;
    private string _speedAwardHolder = "";
    private int _speedAwardBest;
    private string _speedAwardBestHolder = "";

    /// <summary>QC the race/CTS speed award (Scoreboard_MainPanel scoreboard.qc:2731): the round-best (qu/s, rounded)
    /// + holder and the persisted all-time best + holder, shown as a line above the rankings in race/CTS modes.
    /// All zero/empty hides the line (QC <c>if (race_speedaward_alltimebest)</c>).</summary>
    public void SetSpeedAward(int speed, string holder, int best, string bestHolder)
    {
        _speedAward = speed;
        _speedAwardHolder = holder ?? "";
        _speedAwardBest = best;
        _speedAwardBestHolder = bestHolder ?? "";
        QueueRedraw();
    }

    // =====================================================================================
    //  Sorting (QC Scoreboard_ComparePlayerScores)
    // =====================================================================================

    private void SortRows()
    {
        // QC Scoreboard_ComparePlayerScores: by team (in team modes), then the per-mode primary, secondary, then
        // the remaining registry-order columns; spectators last. We sort by the networked primary/secondary keys
        // when the rows carry full columns; else fall back to score-desc then fewer-deaths.
        ScoreField? primary = GameScores.Primary;
        ScoreField? secondary = GameScores.Secondary;
        bool haveColumns = _rows.Count > 0 && _rows[0].Columns is not null;

        _rows.Sort((a, b) =>
        {
            if (!haveColumns)
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                int ad = a.Deaths < 0 ? int.MaxValue : a.Deaths;
                int bd = b.Deaths < 0 ? int.MaxValue : b.Deaths;
                return ad.CompareTo(bd);
            }
            // ComparePlayers>0 means the first arg ranks ahead; we want the better row FIRST (negative).
            int cmp = -CompareRows(a, b, primary, secondary);
            if (cmp != 0) return cmp;
            // QC Scoreboard_ComparePlayerScores final tiebreak (scoreboard.qc:1300): equal scores fall to
            // sv_entnum so the order is stable frame-to-frame (List.Sort is not a stable sort in .NET).
            return a.NetId.CompareTo(b.NetId);
        });
    }

    /// <summary>QC <c>Scoreboard_ComparePlayerScores</c> core (sans the team split, which the team grouping
    /// handles): primary, then secondary, then registry-order columns. Positive => <paramref name="a"/> ahead.</summary>
    private static int CompareRows(in ScoreRow a, in ScoreRow b, ScoreField? primary, ScoreField? secondary)
    {
        if (primary is not null)
        {
            int r = GameScores.CompareValues(a.Col(primary), b.Col(primary), primary.Flags);
            if (r != 0) return r;
        }
        if (secondary is not null && !ReferenceEquals(secondary, primary))
        {
            int r = GameScores.CompareValues(a.Col(secondary), b.Col(secondary), secondary.Flags);
            if (r != 0) return r;
        }
        foreach (ScoreField f in GameScores.Fields)
        {
            if (f.ClientOnly || f.Label.Length == 0) continue;
            if ((f.Flags & ScoreFlags.NotSortable) != 0) continue;
            if (ReferenceEquals(f, primary) || ReferenceEquals(f, secondary)) continue;
            int r = GameScores.CompareValues(a.Col(f), b.Col(f), f.Flags);
            if (r != 0) return r;
        }
        return 0;
    }

    // =====================================================================================
    //  Column layout (QC Cmd_Scoreboard_SetFields + SCOREBOARD_DEFAULT_COLUMNS)
    // =====================================================================================

    /// <summary>One scoreboard column (QC sbt_field[i] + sbt_field_title[i]).</summary>
    private readonly struct Column
    {
        public readonly ColumnKind Kind;     // the special-field kind (or Label for a SP_* field)
        public readonly ScoreField? Field;   // the backing SP_* field for Kind==Label / sort keys
        public readonly string Title;        // the header label
        public Column(ColumnKind kind, ScoreField? field, string title) { Kind = kind; Field = field; Title = title; }
    }

    private enum ColumnKind { Label, Name, Separator, Ping, Pl, Kdratio, Sum, Frags }

    /// <summary>
    /// QC <c>SCOREBOARD_DEFAULT_COLUMNS</c> (scoreboard.qc:748) — carried VERBATIM for fidelity. The token list
    /// is filtered per-gametype by <see cref="IsGametypeInFilter"/>; a token may carry a leading '?' (no warn)
    /// and a "+/-pattern/field" gametype filter.
    /// </summary>
    private const string DefaultColumns =
        "ping pl fps skill name |" +
        " -teams,rc,cts,surv,inv,lms/kills +ft,tdm,tmayhem/kills ?+rc,inv/kills" +
        " -teams,surv,lms/deaths +ft,tdm,tmayhem/deaths" +
        " +tdm/sum" +
        " -teams,lms,rc,cts,surv,inv,ka/suicides +ft,tdm,tmayhem/suicides ?+rc,inv/suicides" +
        " -cts,dm,tdm,surv,ka,ft,mayhem,tmayhem/frags" +
        " +tdm,ft,dom,ons,as,tmayhem/teamkills" +
        " -rc,cts,surv,nb/dmg -rc,cts,surv,nb/dmgtaken" +
        " +surv/survivals +surv/hunts" +
        " +ctf/pickups +ctf/fckills +ctf/returns +ctf/caps +ons/takes +ons/caps" +
        " +lms/lives +lms/rank" +
        " +kh/kckills +kh/losses +kh/caps" +
        " ?+rc/laps ?+rc/time +rc,cts/fastest" +
        " +as/objectives +nb/faults +nb/goals" +
        " +ka,tka/pickups +ka,tka/bckills +ka,tka/bctime +ft/revivals" +
        " +dom/ticks +dom/takes" +
        " -lms,rc,cts,inv,nb/score";

    /// <summary>QC <c>Cmd_Scoreboard_SetFields</c> (scoreboard.qc:767): parse the active column spec into the
    /// concrete column list for the current gametype. Rebuilt only when the layout generation or the spec
    /// changes (cheap "did the layout move?" gate, like NetworkedFields).</summary>
    private void EnsureColumns()
    {
        string gametype = GameScores.Gametype;
        bool teamplay = GameScores.Teamplay;
        if (_columnsForLayoutGen == GameScores.LayoutGeneration && _columnsForSpec == _columnSpec && _columns.Count > 0)
            return;
        _columnsForLayoutGen = GameScores.LayoutGeneration;
        _columnsForSpec = _columnSpec;
        _columns.Clear();

        ScoreField? primary = GameScores.Primary;
        ScoreField? secondary = GameScores.Secondary;

        string spec = string.IsNullOrWhiteSpace(_columnSpec) ? DefaultColumns : _columnSpec;
        if (spec == "default" || spec == "expand_default") spec = DefaultColumns;

        bool haveName = false, haveSeparator = false, havePrimary = false, haveSecondary = false;
        foreach (string rawTok in spec.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string str = rawTok;
            if (str.StartsWith('?')) str = str[1..]; // nocomplain prefix (we never warn, so just strip it)

            int slash = str.IndexOf('/');
            if (slash >= 0)
            {
                string pattern = str[..slash];
                str = str[(slash + 1)..];
                if (!IsGametypeInFilter(gametype, teamplay, pattern))
                    continue;
            }

            str = str.ToLowerInvariant();
            switch (str)
            {
                case "ping": _columns.Add(new Column(ColumnKind.Ping, null, "Ping")); break;
                case "pl":   _columns.Add(new Column(ColumnKind.Pl, null, "Pl")); break;
                case "name":
                case "nick": _columns.Add(new Column(ColumnKind.Name, null, "Name")); haveName = true; break;
                case "|":    _columns.Add(new Column(ColumnKind.Separator, null, "")); haveSeparator = true; break;
                case "kd":
                case "kdr":
                case "kdratio": _columns.Add(new Column(ColumnKind.Kdratio, null, "K/D")); break;
                case "sum":
                case "diff":
                case "k-d":  _columns.Add(new Column(ColumnKind.Sum, null, "+/-")); break;
                case "frags": _columns.Add(new Column(ColumnKind.Frags, null, "Frags")); break;
                default:
                {
                    if (str == "damage") str = "dmg";
                    if (str == "damagetaken") str = "dmgtaken";
                    ScoreField? f = FieldByLabel(str);
                    if (f is null) continue; // unknown / server-disabled (fps/skill) — skip (we don't warn)
                    _columns.Add(new Column(ColumnKind.Label, f, f.Label));
                    if (ReferenceEquals(f, primary)) havePrimary = true;
                    if (ReferenceEquals(f, secondary)) haveSecondary = true;
                    break;
                }
            }
            if (_columns.Count >= MaxColumns) break;
        }

        // QC: auto-insert any missing name / separator / primary / secondary (the have_* fixups).
        if (primary is not null && (primary.Flags & ScoreFlags.AllowHide) != 0) havePrimary = true;
        if (secondary is null || ReferenceEquals(secondary, primary)) haveSecondary = true;
        else if ((secondary.Flags & ScoreFlags.AllowHide) != 0) haveSecondary = true;

        if (!haveName)
        {
            _columns.Insert(0, new Column(ColumnKind.Name, null, "Name"));
            if (!haveSeparator) { _columns.Insert(1, new Column(ColumnKind.Separator, null, "")); haveSeparator = true; }
        }
        else if (!haveSeparator)
        {
            _columns.Add(new Column(ColumnKind.Separator, null, "")); haveSeparator = true;
        }
        if (!haveSecondary && secondary is not null)
            _columns.Add(new Column(ColumnKind.Label, secondary, secondary.Label));
        if (!havePrimary && primary is not null)
            _columns.Add(new Column(ColumnKind.Label, primary, primary.Label));
    }

    private const int MaxColumns = 24; // QC MAX_SBT_FIELDS

    /// <summary>QC <c>FOREACH(Scores, str == strtolower(scores_label(it)))</c>: find a column by its (active) label.</summary>
    private static ScoreField? FieldByLabel(string label)
    {
        foreach (ScoreField f in GameScores.Fields)
            if (f.Label.Length != 0 && f.Label.ToLowerInvariant() == label) return f;
        return null;
    }

    /// <summary>
    /// QC <c>isGametypeInFilter(gt, teamplay, teamspawns=false, pattern)</c> (common/util.qc:1187): does the
    /// active gametype pass a "+/-pattern" include/exclude list? The pattern is a comma list of mode NetNames
    /// plus the pseudo-gametypes "teams"/"noteams" (and "race" for rc/cts). A leading '-' excludes; '+' (or no
    /// prefix) includes. Faithful to the QC comma-delimited substring matching.
    /// </summary>
    private static bool IsGametypeInFilter(string gametype, bool teamplay, string pattern)
    {
        string sub = "," + gametype + ",";
        string sub2 = teamplay ? ",teams," : ",noteams,";
        string sub4 = (gametype == "rc" || gametype == "cts") ? ",race," : null!;

        if (pattern.StartsWith('-'))
        {
            string p = "," + pattern[1..] + ",";
            if (p.Contains(sub)) return false;
            if (p.Contains(sub2)) return false;
            if (sub4 is not null && p.Contains(sub4)) return false;
            return true;
        }
        else
        {
            string body = pattern.StartsWith('+') ? pattern[1..] : pattern;
            string p = "," + body + ",";
            // QC: pass if the gametype OR teams/noteams (OR race) is present.
            if (p.Contains(sub)) return true;
            if (p.Contains(sub2)) return true;
            if (sub4 is not null && p.Contains(sub4)) return true;
            return false;
        }
    }

    // =====================================================================================
    //  Per-field value formatting (QC Scoreboard_GetField)
    // =====================================================================================

    /// <summary>The result of formatting one field: the display string + its color (QC sbt_field_rgb).</summary>
    private readonly struct FieldText
    {
        public readonly string Text;
        public readonly Color Color;
        public FieldText(string text, Color color) { Text = text; Color = color; }
    }

    /// <summary>QC <c>Scoreboard_GetField</c> (scoreboard.qc:1029): format a column's value for a row, honoring
    /// SP_PING colorization, SP_FRAGS=kills-suicides, SP_KDRATIO, SP_SUM=kills-deaths, SP_DMG/DMGTAKEN ('N.N k'),
    /// and the default <see cref="GameScores.ScoreString"/> for a labeled column (TIME/RANK/HIDE_ZERO aware).</summary>
    private FieldText GetField(in ScoreRow r, in Column col)
    {
        Color white = new(1f, 1f, 1f, 1f);
        var inv0 = System.Globalization.CultureInfo.InvariantCulture;
        // QC scoreboard.qc:1047-1049: when scores_per_round is on, the count-style columns (frags/kdr/sum/dmg/score)
        // are divided by the player's SP_ROUNDS_PL; rounds_played==0 disables averaging for that row/cell.
        int roundsPlayed = ScoresPerRound() ? ColOf(r, "ROUNDS_PL") : 0;
        switch (col.Kind)
        {
            case ColumnKind.Ping:
                // QC SP_PING (scoreboard.qc:1060): the networked per-row ping, colorized by the ping bands. A
                // negative value (unknown / bot, not networked) shows a neutral dash rather than the QC ">>>"
                // no-scores glyph (which has a specific meaning); 0 = connecting → "N/A".
                if (r.Ping < 0) return new FieldText("-", new Color(1f, 1f, 1f, 0.5f));
                if (r.Ping == 0) return new FieldText("N/A", white);
                return new FieldText(r.Ping.ToString(), PingColor(r.Ping));

            case ColumnKind.Pl:
            {
                // QC SP_PL (scoreboard.qc:1070-1082): blank when there's no loss; else show ceil(pl*100),
                // red-tinted by severity ('1 0.5 0.5' - '0 0.5 0.5' * bound(0, pl/0.2, 1); 20% loss = full red).
                // The port doesn't track movement loss, so only packet loss contributes (QC's tmp == 0 branch).
                float pl = r.PacketLoss;
                if (pl <= 0f) return new FieldText("", white);
                int v = Mathf.CeilToInt(pl * 100f);
                float sev = Mathf.Clamp(pl / 0.2f, 0f, 1f);
                Color c = new(1f, 0.5f - 0.5f * sev, 0.5f - 0.5f * sev, 1f);
                return new FieldText(v.ToString(), c);
            }

            case ColumnKind.Name:
            {
                // QC Scoreboard_AddPlayerId (scoreboard.qc:1216): with hud_panel_scoreboard_playerid set, the
                // name cell is prefixed with the player's id, e.g. "#3 " (playerid_prefix + sv_entnum + suffix).
                // Off (0) by default → just the name. Read live so a console toggle takes effect.
                if (CvarF("playerid", 0f) != 0f && r.NetId > 0)
                {
                    string pre = CvarStr("playerid_prefix"); if (pre.Length == 0) pre = "#";
                    string suf = CvarStr("playerid_suffix"); if (suf.Length == 0) suf = " ";
                    return new FieldText($"^7{pre}{r.NetId}{suf}{r.Name}", white);
                }
                return new FieldText(r.Name, white);
            }

            case ColumnKind.Separator:
                return new FieldText("", white);

            case ColumnKind.Frags:
            {
                // QC SP_FRAGS (scoreboard.qc:1090): kills - suicides; per-round → "%.1f" of f/rounds_played.
                int frags = ColOf(r, "KILLS") - ColOf(r, "SUICIDES");
                if (roundsPlayed != 0)
                    return new FieldText((frags / (float)roundsPlayed).ToString("0.0", inv0), white);
                return new FieldText(frags.ToString(), white);
            }

            case ColumnKind.Kdratio:
            {
                // QC SP_KDRATIO (scoreboard.qc:1096): three branches.
                //  denom==0 → green, raw kills (per-round: "%.1f" of num/rounds_played)
                //  num<=0   → red,   "%.1f" num/denom (per-round: "%.2f" num/(denom*rounds_played))
                //  else     → white, "%.1f" num/denom (per-round: "%.2f" num/(denom*rounds_played))
                int num = ColOf(r, "KILLS"), denom = ColOf(r, "DEATHS");
                if (denom == 0)
                {
                    string s = roundsPlayed != 0
                        ? (num / (float)roundsPlayed).ToString("0.0", inv0)
                        : num.ToString(inv0);
                    return new FieldText(s, new Color(0f, 1f, 0f, 1f));
                }
                bool red = num <= 0;
                string str = roundsPlayed != 0
                    ? (num / (float)(denom * roundsPlayed)).ToString("0.00", inv0)
                    : (num / (float)denom).ToString("0.0", inv0);
                return new FieldText(str, red ? new Color(1f, 0f, 0f, 1f) : white);
            }

            case ColumnKind.Sum:
            {
                // QC SP_SUM (scoreboard.qc:1125): kills - deaths; green>0 / white==0 / red<0; per-round → "%.1f".
                int sum = ColOf(r, "KILLS") - ColOf(r, "DEATHS");
                Color c = sum > 0 ? new Color(0f, 1f, 0f, 1f) : sum == 0 ? white : new Color(1f, 0f, 0f, 1f);
                if (roundsPlayed != 0)
                    return new FieldText((sum / (float)roundsPlayed).ToString("0.0", inv0), c);
                return new FieldText(sum.ToString(), c);
            }

            default: // ColumnKind.Label
            {
                ScoreField? f = col.Field;
                if (f is null) return new FieldText("", white);
                int v = r.Col(f);

                // QC SP_SKILL (scoreboard.qc:1138): -1 → "...", -2 → "N/A", else the int. (Networked only when
                // sv_showskill enables the column; the value rides the row columns once present.)
                if (f.Name == "SKILL")
                    return new FieldText(v == -1 ? "..." : v == -2 ? "N/A" : v.ToString(inv0), white);

                // QC SP_FPS (scoreboard.qc:1147): 0 → "N/A" (0 ping = connecting/bot) or "..." white; else the int
                // colored red≤32 / yellow 64-96 / white≥128 via sbt_field_rgb.{y,z} = bound(0,(fps-32)*0.03125,1)
                // and bound(0,(fps-96)*0.03125,1) — x stays 1.
                if (f.Name == "FPS")
                {
                    if (v == 0)
                        return new FieldText(r.Ping == 0 ? "N/A" : "...", white);
                    float g = Mathf.Clamp((v - 32) * 0.03125f, 0f, 1f);
                    float b = Mathf.Clamp((v - 96) * 0.03125f, 0f, 1f);
                    return new FieldText(v.ToString(inv0), new Color(1f, g, b, 1f));
                }

                // QC SP_DMG/SP_DMGTAKEN (scoreboard.qc:1165): "%.1f k" of v/1000 (per-round: "%.2f k" of
                // v/(1000*rounds_played)).
                if (f.Name == "DMG" || f.Name == "DMGTAKEN")
                {
                    string s = roundsPlayed != 0
                        ? (v / (1000f * roundsPlayed)).ToString("0.00", inv0) + " k"
                        : (v / 1000f).ToString("0.0", inv0) + " k";
                    return new FieldText(s, white);
                }

                Color c = ReferenceEquals(f, GameScores.Primary) ? new Color(1f, 1f, 0f, 1f)
                        : ReferenceEquals(f, GameScores.Secondary) ? new Color(0f, 1f, 1f, 1f) : white;
                // QC default/SP_SCORE: ScoreString honors per-round averaging directly.
                return new FieldText(GameScores.ScoreString(f.Flags, v, roundsPlayed), c);
            }
        }
    }

    private static int ColOf(in ScoreRow r, string fieldName)
    {
        ScoreField? f = GameScores.Field(fieldName);
        return f is null ? 0 : r.Col(f);
    }

    /// <summary>QC SP_PING colorization (scoreboard.qc:1060-1067): green→yellow→red by the ping bands
    /// <c>hud_panel_scoreboard_ping_low=20</c> / <c>ping_medium=80</c> / <c>ping_high=200</c> with the QC default
    /// band colors COLOR_LOW='0 1 0', COLOR_MED='1 1 0', COLOR_HIGH='1 0 0'. Read live from the shared store so
    /// console/menu edits take effect (was previously hardcoded 75/200/500, an unintended value gap).</summary>
    private Color PingColor(int ping)
    {
        int low = (int)CvarF("ping_low", 20f);
        int med = (int)CvarF("ping_medium", 80f);
        int high = (int)CvarF("ping_high", 200f);
        Color cLow = new(0f, 1f, 0f, 1f), cMed = new(1f, 1f, 0f, 1f), cHigh = new(1f, 0f, 0f, 1f);
        if (ping < low) return cLow;
        // QC lerps use the band deltas directly; guard against degenerate (equal) bands.
        if (ping < med) return cLow.Lerp(cMed, med > low ? (ping - low) / (float)(med - low) : 1f);
        if (ping < high) return cMed.Lerp(cHigh, high > med ? (ping - med) / (float)(high - med) : 1f);
        return cHigh;
    }

    // =====================================================================================
    //  Draw
    // =====================================================================================

    /// <summary>QC <c>scoreboard_fade_alpha</c> step: ramp the fade toward <see cref="Active"/> using the
    /// fadein/fadeout speeds (per second), self-driving via <see cref="_Process"/> so the cross-fade animates
    /// even though the panel is not <see cref="IsDynamic"/>. Hides the panel once fully faded out.</summary>
    public override void _Process(double delta)
    {
        // QC: fade in at fadeinspeed when active, out at fadeoutspeed when not (0 speed => instant).
        float target = _active ? 1f : 0f;
        if (!Mathf.IsEqualApprox(_fadeAlpha, target))
        {
            float dt = (float)delta;
            float speed = _active ? GlobalF("hud_panel_scoreboard_fadeinspeed", 10f)
                                  : GlobalF("hud_panel_scoreboard_fadeoutspeed", 5f);
            if (speed <= 0f || dt <= 0f) _fadeAlpha = target;
            else if (_active) _fadeAlpha = Mathf.Min(1f, _fadeAlpha + dt * speed);
            else _fadeAlpha = Mathf.Max(0f, _fadeAlpha - dt * speed);

            if (_fadeAlpha <= 0f && !_active) Visible = false;
            QueueRedraw();
        }
        else if (_active && !Visible)
        {
            Visible = true;
            QueueRedraw();
        }
    }

    /// <summary>The effective panel alpha this frame: the HUD fade × the scoreboard's own fade-in/out. When the
    /// owner never sets <see cref="Active"/> (legacy Visible-toggle callers) <see cref="_fadeAlpha"/> stays 0, so
    /// we treat a visible-but-never-faded panel as fully opaque (1) — i.e. fade is opt-in.</summary>
    private float PanelFade()
    {
        float sb = _everActive ? _fadeAlpha : 1f;
        return Mathf.Clamp(LiveFgAlpha / Mathf.Max(0.0001f, Cfg.FgAlpha), 0f, 1f) * sb;
    }
    private bool _everActive;

    protected override void DrawPanel()
    {
        if (_active) _everActive = true;
        float fade = PanelFade();
        if (fade <= 0f) return; // QC: scoreboard_fade_alpha <= 0 → draw nothing

        EnsureColumns();
        _overflowRows = 0; // QC Scoreboard_DrawOthers: reset the dropped-row counter for this draw

        // QC HUD_Panel_DrawBg: the configured luma skin frame (border_default 9-slice, bg color "0 0.3 0.5"
        // @ 0.7) — drawn via the base helper so the panel honors hud_panel_scoreboard_bg*/the live skin like
        // every other panel (no-op when the panel's bg is "0"). LiveBgAlpha already carries the HUD fade.
        DrawBackground();
        // A faint inner darkening so the table reads over a busy world; at the scoreboard cross-fade alpha so
        // it animates with the rest of the panel (the skin frame itself rides LiveBgAlpha).
        DrawRect(new Rect2(Vector2.Zero, Size2), new Color(0.05f, 0.05f, 0.08f, 0.35f * fade));

        float pad = Padding * 2f;
        float x = pad;
        float w = Size2.X - pad * 2f;
        float y = pad;

        // A too-small panel (resolved size clamps to 8px; padding alone can exceed it) yields a non-positive
        // content width. The bg darkening above already drew, but laying out columns / drawing text against a
        // negative width produces off-panel garbage (and negative-width layout math), so stop here.
        if (w <= 1f) return;

        y = DrawGameInfoHeader(x, w, y, fade);

        // Team totals banner (teamplay only).
        if (TeamPlay && _teamScores.Count > 0)
            y = DrawTeamTotals(x, w, y, fade);

        // Column header + rows.
        var layout = ComputeLayout(x, w);
        DrawHeader(layout, ref y, fade);

        if (_rows.Count == 0)
        {
            DrawTextCentered(new Vector2(x, y + 8f), w, "(no players)", new Color(1f, 1f, 1f, 0.4f * fade), 16);
        }
        else
        {
            const float rowH = 24f;
            if (TeamPlay) DrawGroupedByTeam(layout, ref y, rowH, fade);
            else DrawFlat(layout, _rows, ref y, rowH, startRank: 1, fade);
        }

        // QC Scoreboard_DrawOthers (scoreboard.qc:1571): if the table overflowed the panel, show how many
        // players were hidden rather than silently truncating the list.
        if (_overflowRows > 0 && y <= Size2.Y - 18f)
        {
            DrawTextCentered(new Vector2(x, y + 2f), w, $"... and {_overflowRows} more",
                new Color(1f, 1f, 1f, 0.5f * fade), 14);
            y += 18f;
        }

        // Footer blocks (QC spectators / map stats / respawn / accuracy / rankings / map name).
        y += 8f;
        y = DrawSpectators(x, w, y, fade);
        y = DrawMapStats(x, w, y, fade);
        y = DrawRespawn(x, w, y, fade);
        y = DrawAccuracy(x, w, y, fade);
        y = DrawSpeedAward(x, w, y, fade);
        y = DrawRankings(x, w, y, fade);
        DrawFooter(x, w, fade);
    }

    /// <summary>QC the Game Info Section (scoreboard.qc:2502-2581): "Next map: …", the big gametype banner
    /// (right-aligned bold), then the limits line (right) + the "Map: … N/M players" line (left).</summary>
    private float DrawGameInfoHeader(float x, float w, float y, float fade)
    {
        // Next map (drawn small, top-left, before the banner so a long name doesn't cover the title).
        if (!string.IsNullOrEmpty(NextMap))
        {
            DrawColored(new Vector2(x, y), $"Next map: ^9{NextMap}", new Color(1f, 1f, 1f, 0.9f * fade), 13);
        }

        // Gametype banner (QC sb_gameinfo_type_fontsize = hud_fontsize * 2.5, right-aligned, bold).
        string banner = !string.IsNullOrEmpty(GametypeName) ? GametypeName : Title;
        DrawTextRight(x + w, y, w, HudText.Strip(banner), new Color(1f, 1f, 1f, 0.95f * fade), 24);
        y += 30f;

        // Limits line (QC right-aligned, ^3time / ^5frag-or-point limit).
        string limits = BuildLimitsHeader();
        if (limits.Length != 0)
            DrawColoredRight(x + w, y, w, limits, new Color(0.6f, 0.9f, 1f, 0.9f * fade), 14);

        // Map + player count line (QC left-aligned: "Map: <name>   N/M players").
        string mapLine = "";
        if (!string.IsNullOrEmpty(MapName)) mapLine = $"^7Map: ^2{MapName}";
        // QC scoreboard.qc:2574: if (campaign) str = "" — the player-count is meaningless in single-player.
        if (!Campaign && (PlayerCount > 0 || MaxPlayerCount > 0))
        {
            int max = MaxPlayerCount > 0 ? MaxPlayerCount : PlayerCount;
            mapLine = (mapLine.Length != 0 ? mapLine + "    " : "") + $"^5{PlayerCount}^7/^5{max} ^7players";
        }
        if (mapLine.Length != 0)
            DrawColored(new Vector2(x, y), mapLine, new Color(1f, 1f, 1f, 0.9f * fade), 14);
        if (limits.Length != 0 || mapLine.Length != 0) y += 22f;

        return y + 6f;
    }

    /// <summary>QC the limits line (scoreboard.qc:2542-2572): "^3&lt;minutes&gt;" then "^7 / " then the
    /// <c>Scoreboard_Fraglimit_Draw</c> "^5&lt;limit&gt; &lt;label&gt;" (label = "points"/"" for score/fastest).
    /// Color-coded for the right-aligned game-info line. Empty when no limits.</summary>
    private string BuildLimitsHeader()
    {
        // QC scoreboard.qc:2544-2571: tl / fl / ll / ll_and_fl. m_hidelimits suppresses fl + ll entirely
        // (only the timelimit shows); the only stock gametype that sets it is LMS.
        string str = "";
        if (TimeLimitMinutes > 0) str = $"^3{TimeLimitMinutes}";

        // QC scoreboard.qc:2551: if (!gametype.m_hidelimits) — skip the whole frag/lead block when hidden.
        if (!HideLimits)
        {
            int fl = FragLimit;
            int ll = LeadLimit;
            if (fl > 0)
            {
                if (str.Length != 0) str += "^7 / ";   // QC delimiter
                str += FraglimitDraw(fl, isLeadLimit: false);
            }
            // QC: ll > 0 && (ll < fl || fl <= 0) — don't show a lead limit that can never be reached before fraglimit.
            if (ll > 0 && (ll < fl || fl <= 0))
            {
                if (TimeLimitMinutes > 0 || fl > 0)
                    // QC: "^7 & " when leadlimit_and_fraglimit (both needed) and fl > 0, else "^7 / ".
                    str += (LeadAndFragLimit && fl > 0) ? "^7 & " : "^7 / ";
                str += FraglimitDraw(ll, isLeadLimit: true);
            }
        }
        return str;
    }

    /// <summary>QC <c>Scoreboard_Fraglimit_Draw</c> (scoreboard.qc:2392): format one limit using the primary key's
    /// label/flags. The lead-limit term reads "^2+&lt;N&gt; &lt;label&gt;"; the frag/point limit reads
    /// "^5&lt;N&gt; &lt;label&gt;". The label is "points" for "score", "" for "fastest", else the label itself.</summary>
    private string FraglimitDraw(int limit, bool isLeadLimit)
    {
        ScoreField? primary = GameScores.Primary;
        string label = TeamPlay ? GameScores.TeamLabel(GameScores.TeamPrimarySlot) : (primary?.Label ?? "score");
        ScoreFlags flags = TeamPlay ? GameScores.TeamFlagsPrimary : (primary?.Flags ?? ScoreFlags.None);
        string limitStr = GameScores.ScoreString(flags, limit);
        string unit = label == "score" ? "points" : label == "fastest" ? "" : label;
        string prefix = isLeadLimit ? "^2+" : "^5";
        return $"{prefix}{limitStr} {unit}".TrimEnd();
    }

    /// <summary>
    /// QC <c>TeamScore_Compare</c> over the panel's OWN networked team totals (not the static GameScores team
    /// state — that is only mirrored server-side, so a remote client's would be empty). Honors the primary team
    /// slot's SFL_LOWER_IS_BETTER. Positive => team <paramref name="a"/> ranks ahead. <see cref="_teamScores"/>
    /// holds the primary slot's total (the wire ships the primary team score).
    /// </summary>
    private int CompareTeamTotals(int a, int b)
    {
        int va = _teamScores.TryGetValue(a, out int x) ? x : 0;
        int vb = _teamScores.TryGetValue(b, out int y) ? y : 0;
        int r = GameScores.CompareValues(va, vb, GameScores.TeamFlagsPrimary);
        return r != 0 ? r : a - b; // QC the final team-id tiebreak
    }

    private float DrawTeamTotals(float x, float w, float y, float fade)
    {
        // Sorted by the flag-aware team compare so the leading team shows first (QC TeamScore_Compare).
        var teams = new List<KeyValuePair<int, int>>(_teamScores);
        teams.Sort((a, b) => CompareTeamTotals(b.Key, a.Key));

        // QC autocvar_hud_panel_scoreboard_team_size_position (scoreboard.qc:79,2622-2671): 0 = off, 1 = on the
        // left, 2 = on the right; when on, show "N/M" (this team's size / all teams' total size) beside the score.
        int teamSizePos = (int)CvarF("team_size_position", 0f);
        int teamSizeTotal = 0;
        if (teamSizePos != 0)
            foreach (var kv in teams) teamSizeTotal += TeamRowCount(kv.Key);

        float colW = w / Mathf.Max(1, teams.Count);
        for (int i = 0; i < teams.Count; i++)
        {
            int team = teams[i].Key;
            Color tc = TeamColor(team, 0.85f * fade);
            var box = new Rect2(x + i * colW + 2f, y, colW - 4f, 26f);
            DrawRect(box, TeamColor(team, 0.22f * fade));
            DrawText(new Vector2(box.Position.X + 6f, box.Position.Y + 3f), Teams.Name(team), tc, 16);
            DrawTextRight(box.Position.X + box.Size.X - 6f, box.Position.Y + 3f, colW,
                teams[i].Value.ToString(), tc, 18);
            // QC the team-size "N/M" string (bold), placed on the chosen side of the team box.
            if (teamSizePos != 0)
            {
                int size = TeamRowCount(team);
                string sizeStr = $"{size}/{teamSizeTotal}";
                if (teamSizePos == 1)
                    DrawText(new Vector2(box.Position.X + 6f, box.Position.Y + 14f),
                        sizeStr, new Color(tc.R, tc.G, tc.B, 0.85f * fade), 12);
                else
                    DrawTextRight(box.Position.X + box.Size.X - 6f, box.Position.Y + 14f, colW,
                        sizeStr, new Color(tc.R, tc.G, tc.B, 0.85f * fade), 12);
            }
        }
        return y + 32f;
    }

    /// <summary>QC <c>tm.team_size</c>: the count of (non-spectator) score rows on a team, computed locally from
    /// the fed rows (the wire ships per-team totals but not the per-team head-count separately).</summary>
    private int TeamRowCount(int team)
    {
        int n = 0;
        foreach (ScoreRow r in _rows) if (r.Team == team) n++;
        return n;
    }

    private void DrawGroupedByTeam(Layout layout, ref float y, float rowH, float fade)
    {
        var teamsSeen = new List<int>();
        foreach (ScoreRow r in _rows)
            if (r.Team != Teams.None && !teamsSeen.Contains(r.Team)) teamsSeen.Add(r.Team);
        teamsSeen.Sort((a, b) => CompareTeamTotals(b, a));

        // QC ..._bg_teams_color_team: tint each team section's bg by the team color × factor (else a soft tint).
        float teamBgFactor = TeamBgColorFactor();

        foreach (int team in teamsSeen)
        {
            float sectBgA = (teamBgFactor > 0f ? 0.28f : 0.18f) * fade;
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), TeamColor(team, sectBgA));
            DrawText(new Vector2(layout.X + 4f, y + 3f), Teams.Name(team), TeamColor(team, 0.95f * fade), 16);
            y += rowH + 2f;

            int rank = 1;
            int rowParity = 0;
            foreach (ScoreRow r in _rows)
            {
                if (r.Team != team) continue;
                if (!DrawRow(layout, r, rank++, ref y, rowH, fade, rowParity++, team)) return;
            }
            y += 6f;
        }

        var loose = new List<ScoreRow>();
        foreach (ScoreRow r in _rows) if (r.Team == Teams.None) loose.Add(r);
        if (loose.Count > 0) DrawFlat(layout, loose, ref y, rowH, startRank: 1, fade);
    }

    private void DrawFlat(Layout layout, List<ScoreRow> rows, ref float y, float rowH, int startRank, float fade)
    {
        int rank = startRank;
        int rowParity = 0;
        foreach (ScoreRow r in rows)
            if (!DrawRow(layout, r, rank++, ref y, rowH, fade, rowParity++, Teams.None)) return;
    }

    /// <summary>Draw the column header row (QC the sbt_field_title[] header).</summary>
    private void DrawHeader(Layout layout, ref float y, float fade)
    {
        var headerColor = new Color(0.7f, 0.8f, 1f, 0.9f * fade);
        DrawText(new Vector2(layout.RankX, y), "#", headerColor, 14);
        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Separator) continue;
            if (c.Kind == ColumnKind.Name)
                DrawText(new Vector2(layout.ColX[i], y), c.Title, headerColor, 14);
            else
                DrawTextRight(layout.ColRight[i], y, layout.NumW, c.Title, headerColor, 14);
        }
        y += 22f;
        DrawRect(new Rect2(layout.X, y, layout.W, 1f), new Color(1f, 1f, 1f, 0.25f * fade));
        y += 4f;
    }

    // ---- live behavior cvars (QC autocvar_hud_panel_scoreboard_table_*; shared store via the base CvarF) ----

    /// <summary>QC <c>..._table_highlight_alpha_eliminated</c> (scoreboard.qc:77, luma default 0.6): the
    /// eliminated-row grey-out strength, read live from the shared store with the shipped default.</summary>
    private float EliminatedAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha_eliminated", 0.6f), 0f, 1f);

    /// <summary>QC <c>..._table_highlight</c> (default on): alternate-row striping enabled.</summary>
    private bool TableHighlight() => CvarF("table_highlight", 1f) != 0f;
    /// <summary>QC <c>..._table_highlight_alpha</c> (0.2): alternate-row stripe strength.</summary>
    private float TableHighlightAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha", 0.2f), 0f, 1f);
    /// <summary>QC <c>..._table_highlight_alpha_self</c> (0.4): the local player's row highlight strength.</summary>
    private float SelfHighlightAlpha() => Mathf.Clamp(CvarF("table_highlight_alpha_self", 0.4f), 0f, 1f);
    /// <summary>QC <c>..._table_fg_alpha_self</c> (1): the local player's row text alpha (vs ..._table_fg_alpha 0.9).</summary>
    private float SelfFgAlpha() => Mathf.Clamp(CvarF("table_fg_alpha_self", 1f), 0f, 1f);
    /// <summary>QC <c>..._table_fg_alpha</c> (0.9): the non-self row text alpha.</summary>
    private float RowFgAlpha() => Mathf.Clamp(CvarF("table_fg_alpha", 0.9f), 0f, 1f);
    /// <summary>QC <c>..._bg_teams_color_team</c> (0): tint a team section's bg by the team color × this factor.</summary>
    private float TeamBgColorFactor() => CvarF("bg_teams_color_team", 0f);
    /// <summary>QC <c>..._respawntime_decimals</c> (1): decimals shown in the respawn countdown (0 = whole sec).</summary>
    private int RespawnDecimals() => (int)Mathf.Clamp(CvarF("respawntime_decimals", 1f), 0f, 3f);
    /// <summary>QC <c>..._accuracy</c> (true): show the accuracy stats block.</summary>
    private bool AccuracyEnabled() => CvarF("accuracy", 1f) != 0f;

    /// <summary>QC <c>Scoreboard_AccuracyStats_WouldDraw</c> warmup gate (scoreboard.qc:1864): the accuracy block
    /// is hidden during warmup (the stats aren't meaningful until the match proper). Fed by the match layer.</summary>
    public bool MatchWarmup { get; set; }
    /// <summary>QC <c>..._spectators_showping</c> (true): show ping next to spectator names.</summary>
    private bool SpectatorsShowPing() => CvarF("spectators_showping", 1f) != 0f;
    /// <summary>QC <c>autocvar_hud_panel_scoreboard_scores_per_round</c> (scoreboard.qc:105, default 0): when set,
    /// frags/kdr/sum/dmg/score are shown as per-round averages (divided by SP_ROUNDS_PL). Toggled by Ctrl+R in the
    /// interactive UI (not yet ported) — read live so a console "toggle" still takes effect.</summary>
    private bool ScoresPerRound() => CvarF("scores_per_round", 0f) != 0f;

    /// <summary>QC <c>Scoreboard_DrawOthers</c> (scoreboard.qc:1571): how many rows were dropped because the
    /// panel filled up, so the table can draw the "... and N more" overflow line. Reset each draw.</summary>
    private int _overflowRows;

    /// <summary>Draw one player row across all columns; returns false once the panel is full.</summary>
    private bool DrawRow(Layout layout, in ScoreRow r, int rank, ref float y, float rowH,
        float fade, int rowParity, int team)
    {
        if (y > Size2.Y - rowH) { _overflowRows++; return false; }

        // QC scoreboard.qc:1531: alternate-row striping (sbt_highlight) on even rows, tinted by the team color
        // (the row rgb passed into Scoreboard_DrawItem is the team color, '1 1 1' in FFA).
        if (TableHighlight() && (rowParity % 2) == 0)
        {
            Color stripe = team != Teams.None ? TeamColor(team, TableHighlightAlpha() * fade)
                                              : new Color(1f, 1f, 1f, TableHighlightAlpha() * fade);
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), stripe);
        }

        // QC ..._table_highlight_alpha_self: highlight the local player's row (drawn over the stripe).
        if (r.IsLocal)
        {
            Color selfHl = team != Teams.None ? TeamColor(team, SelfHighlightAlpha() * fade)
                                              : new Color(0.3f, 0.5f, 0.9f, SelfHighlightAlpha() * fade);
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), selfHl);
        }

        // QC scoreboard.qc:1519-1520: grey out an eliminated player's row (the eliminatedPlayers bitfield)
        // with a BLACK fill at hud_panel_scoreboard_table_highlight_alpha_eliminated (shipped luma skin: 0.6).
        if (r.Eliminated)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), new Color(0f, 0f, 0f, EliminatedAlpha() * fade));

        // QC ..._table_fg_alpha / _self: self rows are brighter than the rest.
        float rowAlpha = (r.IsLocal ? SelfFgAlpha() : RowFgAlpha()) * fade;
        Color rowFg = new(1f, 1f, 1f, rowAlpha);
        DrawText(new Vector2(layout.RankX + 2f, y + 3f), rank.ToString(), rowFg, 16);

        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Separator) continue;
            FieldText ft = GetField(r, c);
            if (c.Kind == ColumnKind.Name)
            {
                float nameX = layout.ColX[i];
                // QC scoreboard.qc:1003-1009 — the player_handicap extra icon (a 32x32 square drawn next to the
                // name) when handicap_level != 0, tinted '1 0 0' + '0 1 1' * ((16 - lvl) / 15): white at level 1,
                // red at level 16. Draw the REAL gfx/scoreboard/player_handicap art from the mounted game data
                // (sbt_field_icon_extra[1]) tinted with the EXACT Base formula; fall back to a flat colored
                // square if the texture can't be resolved. Offset the name so it doesn't overlap either way.
                if (r.HandicapLevel != 0)
                {
                    int lvl = r.HandicapLevel;
                    float t = (16f - lvl) / 15f; // 1.0 @ lvl 1 (white) → 0.0 @ lvl 16 (red)
                    Color hc = new(1f, t, t, rowAlpha); // '1 0 0' + '0 1 1' * t
                    float sq = rowH - 6f;
                    var iconRect = new Rect2(nameX, y + 3f, sq, sq);
                    Texture2D? icon = TextureCache.Get("gfx/scoreboard/player_handicap");
                    if (icon is not null)
                        DrawTextureRect(icon, iconRect, false, hc);
                    else
                        DrawRect(iconRect, hc);
                    nameX += sq + 4f;
                }
                DrawColored(new Vector2(nameX, y + 3f), ft.Text, rowFg, 16);
            }
            else
                DrawTextRight(layout.ColRight[i], y + 3f, layout.NumW,
                    ft.Text, new Color(ft.Color.R, ft.Color.G, ft.Color.B, ft.Color.A * rowAlpha), 16);
        }

        y += rowH;
        return true;
    }

    // ---- spectators / map stats / respawn / accuracy / rankings footer (QC the footer draws) ----

    /// <summary>QC <c>Scoreboard_Spectators_Draw</c> (scoreboard.qc:2364): a "Spectators (N)" bold header then
    /// the spectator names (with ping when ..._spectators_showping), wrapped to the panel width.</summary>
    private float DrawSpectators(float x, float w, float y, float fade)
    {
        if (_spectators.Count == 0) return y;
        if (y > Size2.Y - 36f) return y;

        DrawText(new Vector2(x, y), $"Spectators ({_spectators.Count})", new Color(1f, 1f, 1f, 0.95f * fade), 15);
        y += 22f;

        bool showPing = SpectatorsShowPing();
        float cx = x + 6f;
        float rowAlpha = RowFgAlpha() * fade;
        const int sz = 14;
        const float lineH = 18f;
        foreach (SpectatorRow sp in _spectators)
        {
            // ping prefix (QC SP_PING field shown before the name when aligned-off / inline otherwise).
            string pingTxt = (showPing && sp.Ping >= 0) ? (sp.Ping == 0 ? "N/A" : sp.Ping.ToString()) : "";
            float pingW = pingTxt.Length != 0 ? MeasureText(pingTxt, sz) + 6f : 0f;
            float nameW = MeasureText(HudText.Strip(sp.Name), sz);
            float cellW = pingW + nameW + 16f;

            if (cx + cellW > x + w - 6f && cx > x + 6f)
            {
                cx = x + 6f; y += lineH;
                if (y > Size2.Y - lineH) break;
            }
            if (pingTxt.Length != 0)
            {
                DrawText(new Vector2(cx, y), pingTxt,
                    new Color(PingColor(sp.Ping).R, PingColor(sp.Ping).G, PingColor(sp.Ping).B, rowAlpha), sz);
                cx += pingW;
            }
            DrawColored(new Vector2(cx, y), sp.Name, new Color(1f, 1f, 1f, rowAlpha), sz);
            cx += nameW + 16f;
        }
        return y + lineH + 6f;
    }

    private float DrawMapStats(float x, float w, float y, float fade)
    {
        bool hasMonsters = MonstersTotal > 0;
        bool hasSecrets = SecretsTotal > 0;
        if (!hasMonsters && !hasSecrets) return y;
        if (y > Size2.Y - 60f) return y;

        var head = new Color(1f, 1f, 1f, 0.9f * fade);
        var body = new Color(1f, 1f, 1f, RowFgAlpha() * fade);
        DrawText(new Vector2(x, y), "Map stats:", head, 14);
        y += 20f;
        if (hasMonsters)
        {
            DrawText(new Vector2(x + 8f, y), "Monsters killed:", body, 14);
            DrawTextRight(x + w, y, w * 0.3f, $"{MonstersKilled}/{MonstersTotal}", body, 14);
            y += 18f;
        }
        if (hasSecrets)
        {
            DrawText(new Vector2(x + 8f, y), "Secrets found:", body, 14);
            DrawTextRight(x + w, y, w * 0.3f, $"{SecretsFound}/{SecretsTotal}", body, 14);
            y += 18f;
        }
        return y + 6f;
    }

    /// <summary>QC the respawn-status line (scoreboard.qc:2763-2796): "^1Respawning in ^3N^1..." (awaiting),
    /// "You are dead, wait ^3N^7 before respawning" (cooldown), or "press jump to respawn" (ready). The decimals
    /// shown follow ..._respawntime_decimals (QC count_seconds_decs vs count_seconds(ceil)).</summary>
    private float DrawRespawn(float x, float w, float y, float fade)
    {
        // QC scoreboard.qc:2763-2796: float respawn_time = STAT(RESPAWN_TIME); the line shows only when not in
        // intermission and respawn_time != 0. The stat is the absolute respawn time, NEGATED while awaiting respawn.
        float respawnTime = RespawnStat;
        if (respawnTime == 0f) return y;
        if (y > Size2.Y - 24f) return y;
        float now = RespawnServerTime;

        string s;
        if (respawnTime < 0f)
        {
            // QC: a negative number means we are awaiting respawn (time value still the same); un-mark it.
            respawnTime = -respawnTime;
            if (respawnTime < now)
                s = ""; // QC: a few frames while the server is respawning — empty so the height doesn't jump
            else
                s = $"^1Respawning in ^3{FormatRespawnSeconds(respawnTime - now)}^1...";
        }
        else if (now < respawnTime)
        {
            // QC: "You are dead, wait N before respawning" (cooldown before a respawn is even allowed).
            s = $"^7You are dead, wait ^3{FormatRespawnSeconds(respawnTime - now)}^7 before respawning";
        }
        else
        {
            // QC: time >= respawn_time → "You are dead, press JUMP to respawn".
            s = $"^7You are dead, press ^2{RespawnJumpKey}^7 to respawn";
        }

        if (s.Length == 0) return y + 22f; // keep the height stable (QC draws an empty string for one frame)
        DrawTextCentered2(new Vector2(x, y), w, s, new Color(1f, 0.9f, 0.4f, 0.95f * fade), 16);
        return y + 22f;
    }

    /// <summary>QC <c>count_seconds_decs(s, respawntime_decimals)</c> vs <c>count_seconds(ceil(s))</c>: the
    /// respawn countdown number, with the configured decimals (..._respawntime_decimals) or whole-second ceil.</summary>
    private string FormatRespawnSeconds(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int dec = RespawnDecimals();
        return dec > 0
            ? seconds.ToString("0." + new string('0', dec), System.Globalization.CultureInfo.InvariantCulture)
            : Mathf.CeilToInt(seconds).ToString();
    }

    private float DrawAccuracy(float x, float w, float y, float fade)
    {
        if (!AccuracyEnabled()) return y;             // QC ..._accuracy gate
        if (MatchWarmup) return y;                    // QC Scoreboard_AccuracyStats_WouldDraw: hidden in warmup
        // QC cl_invasion.qc MUTATOR_HOOKFUNCTION(cl_inv, DrawScoreboardItemStats) returns ISGAMETYPE(INVASION):
        // the item-stats (weapon accuracy) panel is hidden in Invasion because monsters are not valid accuracy
        // targets (sv_invasion.qc AccuracyTargetValid → MUT_ACCADD_INVALID), so the stats would be meaningless.
        if (GameScores.Gametype == "inv") return y;
        if (_accuracy.Count == 0) return y;           // not networked yet — block hidden (QC gates on data)
        if (y > Size2.Y - 40f) return y;

        // QC: "Accuracy stats (average %d%%)" header. QC scoreboard.qc:1947 computes the average as
        // floor(sum * 100 / weapons_with_stats + 0.5) — round-half-up (the port's per-weapon values are already
        // 0..100, so the *100 is absorbed; sum the 0..100 values and round-half-up the mean).
        int sum = 0, n = 0;
        foreach (var kv in _accuracy) if (kv.Value >= 0) { sum += kv.Value; n++; }
        if (n == 0) return y;
        int avg = Mathf.FloorToInt((float)sum / n + 0.5f);
        DrawText(new Vector2(x, y), $"Accuracy stats (average {avg}%)", new Color(1f, 1f, 1f, 0.9f * fade), 14);
        y += 20f;
        // QC scoreboard.qc:1894-1895: with accuracy_nocolors the cells are drawn flat white (rgb = '1 1 1'),
        // bypassing the per-weapon Accuracy_GetColor ramp.
        bool noColors = CvarF("accuracy_nocolors", 0f) != 0f;
        float cx = x + 8f;
        foreach (var kv in _accuracy)
        {
            if (kv.Value < 0) continue; // weapon never fired (QC skips)
            string cell = $"{kv.Value}%";
            Color ac = noColors ? new Color(1f, 1f, 1f, 1f) : AccuracyColor(kv.Value);
            DrawText(new Vector2(cx, y), cell, new Color(ac.R, ac.G, ac.B, ac.A * fade), 14);
            cx += MeasureText(cell, 14) + 14f;
            if (cx > x + w - 40f) { cx = x + 8f; y += 18f; if (y > Size2.Y - 24f) break; }
        }
        return y + 22f;
    }

    /// <summary>QC accuracy color ramp (red→yellow→green by hit %).</summary>
    private static Color AccuracyColor(int pct)
    {
        float f = Mathf.Clamp(pct / 100f, 0f, 1f);
        return f < 0.5f
            ? new Color(1f, Mathf.Lerp(0f, 1f, f * 2f), 0f, 1f)
            : new Color(Mathf.Lerp(1f, 0f, (f - 0.5f) * 2f), 1f, 0f, 1f);
    }

    /// <summary>
    /// QC the race/CTS speed award (Scoreboard_MainPanel, scoreboard.qc:2731): in race/CTS, draw
    /// "Speed award: N unit (holder) / All-time fastest: N unit (holder)" above the rankings. Only drawn when the
    /// all-time best exists (QC <c>if (race_speedaward_alltimebest)</c>); the round-best half is dropped if 0.
    /// The qu/s values from the wire are converted to the configured <c>hud_speed_unit</c> (QC GetSpeedUnitFactor).
    /// </summary>
    private float DrawSpeedAward(float x, float w, float y, float fade)
    {
        if (GameScores.Gametype != "rc" && GameScores.Gametype != "cts") return y;
        if (_speedAwardBest == 0) return y; // QC: if (race_speedaward_alltimebest)
        if (y > Size2.Y - 20f) return y;

        int unit = (int)GlobalF("hud_speed_unit", 1f);
        float factor = SpeedUnitFactor(unit);
        string lbl = SpeedUnitLabel(unit);
        var body = new Color(1f, 1f, 1f, RowFgAlpha() * fade);

        string str = "";
        if (_speedAward != 0) // QC: if (race_speedaward)
        {
            string name = HudText.Strip(_speedAwardHolder);
            str = $"Speed award: {(int)(_speedAward * factor)}{lbl} ({name})";
            str += " / ";
        }
        string bestName = HudText.Strip(_speedAwardBestHolder);
        str += $"All-time fastest: {(int)(_speedAwardBest * factor)}{lbl} ({bestName})";
        DrawText(new Vector2(x, y), str, body, 14);
        return y + 20f;
    }

    /// <summary>QC <c>GetSpeedUnitFactor</c> (client/main.qc): qu/s -> the selected unit's factor.</summary>
    private static float SpeedUnitFactor(int unit) => unit switch
    {
        2 => 0.0254f,
        3 => 0.0254f * 3.6f,
        4 => 0.0254f * 3.6f * 0.6213711922f,
        5 => 0.0254f * 1.943844492f,
        _ => 1.0f,
    };

    /// <summary>QC <c>GetSpeedUnit</c> (client/main.qc): the selected unit's label.</summary>
    private static string SpeedUnitLabel(int unit) => unit switch
    {
        2 => "m/s",
        3 => "km/h",
        4 => "mph",
        5 => "knots",
        _ => "qu/s",
    };

    private float DrawRankings(float x, float w, float y, float fade)
    {
        // QC Scoreboard_Rankings_Draw is race/CTS only; gate on the mode + data (the networked rankings table).
        if (_rankings.Count == 0) return y;
        if (GameScores.Gametype != "rc" && GameScores.Gametype != "cts") return y;
        if (y > Size2.Y - 40f) return y;
        var body = new Color(1f, 1f, 1f, RowFgAlpha() * fade);
        DrawText(new Vector2(x, y), "Rankings:", new Color(1f, 1f, 1f, 0.9f * fade), 14);
        y += 20f;
        string selfName = HudText.Strip(RankingsSelfName);
        for (int i = 0; i < _rankings.Count && y < Size2.Y - 20f; i++)
        {
            (int t, string holder) = _rankings[i];
            // QC: a self-held row gets the brighter self highlight; alternate rows get the stripe.
            bool isSelf = selfName.Length != 0 && HudText.Strip(holder) == selfName;
            if (isSelf)
                DrawRect(new Rect2(x, y - 1f, w, 18f), new Color(1f, 1f, 1f, SelfHighlightAlpha() * fade));
            else if (TableHighlight() && (i % 2) == 0)
                DrawRect(new Rect2(x, y - 1f, w, 18f), new Color(1f, 1f, 1f, TableHighlightAlpha() * fade));

            // QC: gold / silver / bronze rank colors for the top 3, white otherwise.
            Color rankColor = i switch
            {
                0 => new Color(0.933f, 0.733f, 0.200f, body.A),
                1 => new Color(0.667f, 0.667f, 0.667f, body.A),
                2 => new Color(0.800f, 0.467f, 0.267f, body.A),
                _ => body,
            };
            DrawText(new Vector2(x + 8f, y), $"{i + 1}.", rankColor, 14);
            DrawText(new Vector2(x + 36f, y), GameScores.TimeEncodedToString(t, compact: false), body, 14);
            DrawColored(new Vector2(x + 140f, y), holder, body, 14);
            y += 18f;
        }
        return y + 6f;
    }

    /// <summary>QC <c>strdecolorize(entcs_GetName(player_localnum))</c>: the local player's name, used to highlight
    /// the player's own row in the rankings block. Fed by the match layer; "" disables the self-highlight.</summary>
    public string RankingsSelfName { get; set; } = "";

    private void DrawFooter(float x, float w, float fade)
    {
        // QC the map name footer (the game-info line already shows the map at the top; keep a faint bottom
        // echo as a stable anchor). Hidden when no map name.
        if (string.IsNullOrEmpty(MapName)) return;
        DrawTextRight(x + w, Size2.Y - 24f, w, MapName, new Color(1f, 1f, 1f, 0.5f * fade), 14);
    }

    /// <summary>Draw a possibly color-coded string left-to-right starting at <paramref name="pos"/>.</summary>
    private void DrawColored(Vector2 pos, string text, Color baseColor, int size)
    {
        float cx = pos.X;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, pos.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    /// <summary>Draw a color-coded string ending at <paramref name="rightX"/> (right-aligned colored text).</summary>
    private void DrawColoredRight(float rightX, float topY, float width, string text, Color baseColor, int size)
    {
        float total = 0f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor)) total += MeasureText(run.Text, size);
        float cx = rightX - total;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, topY), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    /// <summary>Draw a color-coded string horizontally centered within <paramref name="width"/>.</summary>
    private void DrawTextCentered2(Vector2 pos, float width, string text, Color baseColor, int size)
    {
        float total = 0f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor)) total += MeasureText(run.Text, size);
        float cx = pos.X + (width - total) * 0.5f;
        foreach (HudText.Run run in HudText.Parse(text, baseColor))
        {
            DrawText(new Vector2(cx, pos.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
        }
    }

    private static Color TeamColor(int team, float alpha) => team switch
    {
        Teams.Red    => new Color(1f, 0.35f, 0.35f, alpha),
        Teams.Blue   => new Color(0.4f, 0.55f, 1f, alpha),
        Teams.Yellow => new Color(1f, 0.95f, 0.35f, alpha),
        Teams.Pink   => new Color(1f, 0.45f, 0.85f, alpha),
        _ => new Color(0.8f, 0.8f, 0.8f, alpha),
    };

    // ---- column geometry ----

    /// <summary>Computed pixel geometry for the current column list (QC the sbt_field_size[] widths).</summary>
    private sealed class Layout
    {
        public float X, W, RankX, NumW;
        public float[] ColX = System.Array.Empty<float>();      // left edge of each column (used by Name)
        public float[] ColRight = System.Array.Empty<float>();  // right edge (numeric columns are right-aligned)
    }

    /// <summary>
    /// Lay the columns out left→right: a rank gutter, then the Name column takes the slack while every other
    /// column gets a fixed numeric width (QC's column sizing is content-measured; we approximate with a uniform
    /// numeric width + an elastic name column, which is enough for a readable grid).
    /// </summary>
    private Layout ComputeLayout(float x, float w)
    {
        var l = new Layout { X = x, W = w, RankX = x };
        int n = _columns.Count;
        l.ColX = new float[n];
        l.ColRight = new float[n];

        float rankW = w * 0.05f;
        float numW = Mathf.Clamp(w * 0.10f, 44f, 90f);
        l.NumW = numW;

        // Count the non-name, non-separator numeric columns to size the name column with the remaining slack.
        int numericCount = 0;
        foreach (Column c in _columns)
            if (c.Kind != ColumnKind.Name && c.Kind != ColumnKind.Separator) numericCount++;

        float nameX = x + rankW;
        float numericTotal = numericCount * numW;
        float nameW = Mathf.Max(w * 0.22f, w - rankW - numericTotal);

        // Place name first (left), then numerics filling to the right edge in column order.
        float cursorRight = x + w; // numerics are packed against the right edge, right-to-left in REVERSE order
        // First, assign numerics right→left so the LAST column sits at the far right (QC's right-aligned numerics).
        for (int i = n - 1; i >= 0; i--)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Name || c.Kind == ColumnKind.Separator) continue;
            l.ColRight[i] = cursorRight;
            l.ColX[i] = cursorRight - numW;
            cursorRight -= numW;
        }
        // Name column gets the gutter→first-numeric span.
        for (int i = 0; i < n; i++)
        {
            if (_columns[i].Kind == ColumnKind.Name)
            {
                l.ColX[i] = nameX;
                l.ColRight[i] = nameX + nameW;
            }
        }
        return l;
    }

    // =====================================================================================
    //  Behavior-cvar defaults (QC autocvar_hud_panel_scoreboard_*; HudConfig invokes this by reflection)
    // =====================================================================================

    /// <summary>Register the scoreboard's behavior-cvar defaults into the shared store (QC the
    /// <c>autocvar_hud_panel_scoreboard_*</c> initializers, scoreboard.qc:66-105). Idempotent — keeps any
    /// cfg/user value. Read live by the draw code so console/menu edits take effect immediately.</summary>
    public static void RegisterDefaults(CvarService c)
    {
        // fade in/out (scoreboard.qc:66-67)
        c.Register("hud_panel_scoreboard_fadeinspeed", "10", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_fadeoutspeed", "5", CvarFlags.Save);
        // respawn timer decimals (scoreboard.qc:68)
        c.Register("hud_panel_scoreboard_respawntime_decimals", "1", CvarFlags.Save);
        // table look (scoreboard.qc:69-77)
        c.Register("hud_panel_scoreboard_table_bg_alpha", "0", CvarFlags.Save);
        // QC scoreboard.qc:70 + the Scoreboard_Draw_Export skin-cvar list (scoreboard.qc:29): the table bg
        // texture scale (border_default 9-slice). Registered so it round-trips through the HUD-skin export.
        c.Register("hud_panel_scoreboard_table_bg_scale", "0.25", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_fg_alpha", "0.9", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_fg_alpha_self", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha", "0.2", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha_self", "0.4", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_table_highlight_alpha_eliminated", "0.6", CvarFlags.Save);
        // team bg tint (scoreboard.qc:78)
        c.Register("hud_panel_scoreboard_bg_teams_color_team", "0", CvarFlags.Save);
        // accuracy block (scoreboard.qc:82-84)
        c.Register("hud_panel_scoreboard_accuracy", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_accuracy_doublerows", "0", CvarFlags.Save);
        // QC scoreboard.qc:84 + the Scoreboard_Draw_Export skin-cvar list (scoreboard.qc:38): draw accuracy cells
        // without the per-weapon color ramp. Registered so it round-trips through the HUD-skin export.
        c.Register("hud_panel_scoreboard_accuracy_nocolors", "0", CvarFlags.Save);
        // item-stats block (scoreboard.qc:88-89)
        c.Register("hud_panel_scoreboard_itemstats", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_itemstats_doublerows", "0", CvarFlags.Save);
        // spectator list (scoreboard.qc:80,99)
        c.Register("hud_panel_scoreboard_spectators_position", "1", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_spectators_showping", "1", CvarFlags.Save);
        // per-round score averaging (scoreboard.qc:105)
        c.Register("hud_panel_scoreboard_scores_per_round", "0", CvarFlags.Save);
        // playerid name prefix (scoreboard.qc:91-93): show "#<entnum> " before each name when enabled.
        c.Register("hud_panel_scoreboard_playerid", "0", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_playerid_prefix", "#", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_playerid_suffix", " ", CvarFlags.Save);
        // accuracy show-delay (scoreboard.qc:83) — registered for the HUD-skin round-trip (the warmup gate is wired;
        // the time-since-start show-delay is a documented residual).
        c.Register("hud_panel_scoreboard_accuracy_showdelay", "2", CvarFlags.Save);
        // ping color bands (scoreboard.qc:1017-1019)
        c.Register("hud_panel_scoreboard_ping_low", "20", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_ping_medium", "80", CvarFlags.Save);
        c.Register("hud_panel_scoreboard_ping_high", "200", CvarFlags.Save);
        // team-size side display position (scoreboard.qc:79)
        c.Register("hud_panel_scoreboard_team_size_position", "0", CvarFlags.Save);
    }
}
