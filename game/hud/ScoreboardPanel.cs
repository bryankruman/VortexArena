using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

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
        /// <summary>True if this row is the local player (highlighted). Set by the feeder or matched by name.</summary>
        public readonly bool IsLocal;
        /// <summary>True if this player is eliminated for the round (QC <c>pl.eliminated</c>, the networked
        /// eliminatedPlayers bitfield: CA dead / FT frozen-or-dead / Survival out) — greys the row.</summary>
        public readonly bool Eliminated;
        /// <summary>Full registry-indexed column values (QC scores(field)) when fed from the wire; else null.</summary>
        public readonly int[]? Columns;

        public ScoreRow(string name, int score, int team = 0, int deaths = -1, int ping = -1,
            bool isLocal = false, int[]? columns = null, bool eliminated = false)
        {
            Name = name ?? "";
            Score = score;
            Team = team;
            Deaths = deaths;
            Ping = ping;
            IsLocal = isLocal;
            Eliminated = eliminated;
            Columns = columns;
        }

        /// <summary>QC <c>pl.(scores(field))</c>: read a column value (0 when not networked / no column data).</summary>
        public int Col(ScoreField f) => (Columns is not null && (uint)f.RegistryId < Columns.Length) ? Columns[f.RegistryId] : 0;
    }

    /// <summary>The local player, whose row is highlighted (set by <see cref="Hud"/>).</summary>
    public Player? LocalPlayer { get; set; }

    /// <summary>The match title shown atop the table (e.g. "Deathmatch"). Settable by the owner.</summary>
    public string Title { get; set; } = "Scoreboard";

    /// <summary>True when the active gametype is teamplay (groups rows into team sections + shows team totals).</summary>
    public bool TeamPlay { get; set; }

    // ---- header / footer settable surfaces (QC fraglimit/timelimit + map stats + respawn; networked) ----

    /// <summary>QC the fraglimit / pointlimit header value (Scoreboard_Fraglimit_Draw); 0 = none. Settable by the match layer.</summary>
    public int FragLimit { get; set; }
    /// <summary>QC TIMELIMIT (minutes); 0 = none. Settable by the match layer.</summary>
    public int TimeLimitMinutes { get; set; }
    /// <summary>QC the map name shown in the footer (Scoreboard footer "<map>"). Settable by the match layer.</summary>
    public string MapName { get; set; } = "";

    // Map stats (QC Scoreboard_MapStats_Draw STAT(MONSTERS_*/SECRETS_*)); -1 totals = no row. Networked → settable.
    public int MonstersKilled { get; set; } = -1;
    public int MonstersTotal { get; set; } = -1;
    public int SecretsFound { get; set; } = -1;
    public int SecretsTotal { get; set; } = -1;

    /// <summary>QC the respawn-status line remaining seconds (RESPAWN_TIME stat); &lt; 0 = alive/no line. Settable by the match layer.</summary>
    public float RespawnRemaining { get; set; } = -1f;

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
        if (wire is not null)
        {
            IReadOnlyList<ScoreField> netFields = GameScores.NetworkedFields;
            int fieldCount = GameScores.FieldCount;
            ScoreField? scoreF = GameScores.Field("SCORE");
            ScoreField? deathsF = GameScores.Field("DEATHS");
            foreach (XonoticGodot.Net.ScoreRowWire wr in wire.Rows)
            {
                // expand the wire's NetworkedFields-ordered columns into a registry-indexed array.
                var cols = new int[fieldCount];
                int m = System.Math.Min(wr.Columns.Length, netFields.Count);
                for (int i = 0; i < m; i++) cols[netFields[i].RegistryId] = wr.Columns[i];

                int score = scoreF is not null ? cols[scoreF.RegistryId] : 0;
                int deaths = deathsF is not null ? cols[deathsF.RegistryId] : -1;
                _rows.Add(new ScoreRow(wr.Name, score, wr.Team, deaths, ping: -1,
                    isLocal: wr.NetId == localNetId, columns: cols,
                    // QC pl.eliminated (NET_HANDLE ENT_CLIENT_ELIMINATEDPLAYERS, client/main.qc:819): flag the
                    // rows the round-status block marked eliminated so DrawRow greys them.
                    eliminated: eliminatedNetIds is not null && eliminatedNetIds.Contains(wr.NetId)));
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

    /// <summary>QC the race/CTS rankings (Scoreboard_Rankings_Draw): an ordered best-time list (encoded
    /// hundredths + holder name). Networking records is a follow-up; until then this is empty.</summary>
    public void SetRankings(IEnumerable<(int timeEncoded, string holder)> rankings)
    {
        _rankings.Clear();
        if (rankings is not null) _rankings.AddRange(rankings);
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
            return -CompareRows(a, b, primary, secondary);
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
        switch (col.Kind)
        {
            case ColumnKind.Ping:
                // Ping isn't networked on the row yet (always -1); show a neutral dash rather than the QC
                // ">>>" no-scores glyph (which has a specific meaning). When ping is networked this colorizes it.
                if (r.Ping < 0) return new FieldText("-", new Color(1f, 1f, 1f, 0.5f));
                if (r.Ping == 0) return new FieldText("N/A", white);
                return new FieldText(r.Ping.ToString(), PingColor(r.Ping));

            case ColumnKind.Pl:
                return new FieldText("", white); // packet-loss not networked yet (QC SP_PL) — blank

            case ColumnKind.Name:
                return new FieldText(r.Name, white);

            case ColumnKind.Separator:
                return new FieldText("", white);

            case ColumnKind.Frags:
            {
                int frags = ColOf(r, "KILLS") - ColOf(r, "SUICIDES");
                return new FieldText(frags.ToString(), white);
            }

            case ColumnKind.Kdratio:
            {
                int num = ColOf(r, "KILLS"), denom = ColOf(r, "DEATHS");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (denom == 0) return new FieldText(num.ToString(), new Color(0f, 1f, 0f, 1f));
                if (num <= 0) return new FieldText((num / (float)denom).ToString("0.0", inv), new Color(1f, 0f, 0f, 1f));
                return new FieldText((num / (float)denom).ToString("0.0", inv), white);
            }

            case ColumnKind.Sum:
            {
                int sum = ColOf(r, "KILLS") - ColOf(r, "DEATHS");
                Color c = sum > 0 ? new Color(0f, 1f, 0f, 1f) : sum == 0 ? white : new Color(1f, 0f, 0f, 1f);
                return new FieldText(sum.ToString(), c);
            }

            default: // ColumnKind.Label
            {
                ScoreField? f = col.Field;
                if (f is null) return new FieldText("", white);
                int v = r.Col(f);
                if (f.Name == "DMG" || f.Name == "DMGTAKEN")
                    return new FieldText((v / 1000f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " k", white);
                Color c = ReferenceEquals(f, GameScores.Primary) ? new Color(1f, 1f, 0f, 1f)
                        : ReferenceEquals(f, GameScores.Secondary) ? new Color(0f, 1f, 1f, 1f) : white;
                return new FieldText(GameScores.ScoreString(f.Flags, v), c);
            }
        }
    }

    private static int ColOf(in ScoreRow r, string fieldName)
    {
        ScoreField? f = GameScores.Field(fieldName);
        return f is null ? 0 : r.Col(f);
    }

    /// <summary>QC SP_PING colorization (scoreboard.qc:1060): green→yellow→red by ping bands (PING_LOW=75/MED=200/HIGH=500).</summary>
    private static Color PingColor(int ping)
    {
        const int low = 75, med = 200, high = 500;
        Color cLow = new(0f, 1f, 0f, 1f), cMed = new(1f, 1f, 0f, 1f), cHigh = new(1f, 0f, 0f, 1f);
        if (ping < low) return cLow;
        if (ping < med) return cLow.Lerp(cMed, (ping - low) / (float)(med - low));
        if (ping < high) return cMed.Lerp(cHigh, (ping - med) / (float)(high - med));
        return cHigh;
    }

    // =====================================================================================
    //  Draw
    // =====================================================================================

    protected override void DrawPanel()
    {
        EnsureColumns();

        // Dim full-panel backdrop (QC scoreboard background).
        DrawRect(new Rect2(Vector2.Zero, Size2), new Color(0.05f, 0.05f, 0.08f, 0.75f));

        float pad = Padding * 2f;
        float x = pad;
        float w = Size2.X - pad * 2f;
        float y = pad;

        // Title.
        DrawTextCentered(new Vector2(x, y), w, Title, new Color(1f, 1f, 1f, 0.95f), 24);
        y += 34f;

        // Limits header (QC Scoreboard_Fraglimit_Draw): "<limit> <label>" / timelimit.
        string limits = BuildLimitsHeader();
        if (limits.Length != 0)
        {
            DrawTextCentered(new Vector2(x, y), w, limits, new Color(0.6f, 0.9f, 1f, 0.9f), 14);
            y += 22f;
        }

        // Team totals banner (teamplay only).
        if (TeamPlay && _teamScores.Count > 0)
            y = DrawTeamTotals(x, w, y);

        // Column header + rows.
        var layout = ComputeLayout(x, w);
        DrawHeader(layout, ref y);

        if (_rows.Count == 0)
        {
            DrawTextCentered(new Vector2(x, y + 8f), w, "(no players)", new Color(1f, 1f, 1f, 0.4f), 16);
        }
        else
        {
            const float rowH = 24f;
            if (TeamPlay) DrawGroupedByTeam(layout, ref y, rowH);
            else DrawFlat(layout, _rows, ref y, rowH, startRank: 1);
        }

        // Footer blocks (QC map stats / respawn / accuracy / rankings / map name).
        y += 8f;
        y = DrawMapStats(x, w, y);
        y = DrawRespawn(x, w, y);
        y = DrawAccuracy(x, w, y);
        y = DrawRankings(x, w, y);
        DrawFooter(x, w);
    }

    /// <summary>QC Scoreboard_Fraglimit_Draw: the "<limit> points" / timelimit header line.</summary>
    private string BuildLimitsHeader()
    {
        var parts = new List<string>();
        if (FragLimit > 0)
        {
            // QC: the primary key's label; "score" → "points", "fastest" → "".
            ScoreField? primary = GameScores.Primary;
            string label = TeamPlay ? GameScores.TeamLabel(GameScores.TeamPrimarySlot) : (primary?.Label ?? "score");
            ScoreFlags flags = TeamPlay ? GameScores.TeamFlagsPrimary : (primary?.Flags ?? ScoreFlags.None);
            string limitStr = GameScores.ScoreString(flags, FragLimit);
            string unit = label == "score" ? "points" : label == "fastest" ? "" : label;
            parts.Add($"{limitStr} {unit}".Trim());
        }
        if (TimeLimitMinutes > 0) parts.Add($"{TimeLimitMinutes} min");
        return string.Join("   ", parts);
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

    private float DrawTeamTotals(float x, float w, float y)
    {
        // Sorted by the flag-aware team compare so the leading team shows first (QC TeamScore_Compare).
        var teams = new List<KeyValuePair<int, int>>(_teamScores);
        teams.Sort((a, b) => CompareTeamTotals(b.Key, a.Key));

        float colW = w / Mathf.Max(1, teams.Count);
        for (int i = 0; i < teams.Count; i++)
        {
            int team = teams[i].Key;
            Color tc = TeamColor(team, 0.85f);
            var box = new Rect2(x + i * colW + 2f, y, colW - 4f, 26f);
            DrawRect(box, TeamColor(team, 0.22f));
            DrawText(new Vector2(box.Position.X + 6f, box.Position.Y + 3f), Teams.Name(team), tc, 16);
            DrawTextRight(box.Position.X + box.Size.X - 6f, box.Position.Y + 3f, colW,
                teams[i].Value.ToString(), tc, 18);
        }
        return y + 32f;
    }

    private void DrawGroupedByTeam(Layout layout, ref float y, float rowH)
    {
        var teamsSeen = new List<int>();
        foreach (ScoreRow r in _rows)
            if (r.Team != Teams.None && !teamsSeen.Contains(r.Team)) teamsSeen.Add(r.Team);
        teamsSeen.Sort((a, b) => CompareTeamTotals(b, a));

        foreach (int team in teamsSeen)
        {
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), TeamColor(team, 0.18f));
            DrawText(new Vector2(layout.X + 4f, y + 3f), Teams.Name(team), TeamColor(team, 0.95f), 16);
            y += rowH + 2f;

            int rank = 1;
            foreach (ScoreRow r in _rows)
            {
                if (r.Team != team) continue;
                if (!DrawRow(layout, r, rank++, ref y, rowH)) return;
            }
            y += 6f;
        }

        var loose = new List<ScoreRow>();
        foreach (ScoreRow r in _rows) if (r.Team == Teams.None) loose.Add(r);
        if (loose.Count > 0) DrawFlat(layout, loose, ref y, rowH, startRank: 1);
    }

    private void DrawFlat(Layout layout, List<ScoreRow> rows, ref float y, float rowH, int startRank)
    {
        int rank = startRank;
        foreach (ScoreRow r in rows)
            if (!DrawRow(layout, r, rank++, ref y, rowH)) return;
    }

    /// <summary>Draw the column header row (QC the sbt_field_title[] header).</summary>
    private void DrawHeader(Layout layout, ref float y)
    {
        var headerColor = new Color(0.7f, 0.8f, 1f, 0.9f);
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
        DrawRect(new Rect2(layout.X, y, layout.W, 1f), new Color(1f, 1f, 1f, 0.25f));
        y += 4f;
    }

    /// <summary>QC <c>autocvar_hud_panel_scoreboard_table_highlight_alpha_eliminated</c> (scoreboard.qc:77,
    /// default 0.6 — the luma skin also ships 0.6): the eliminated-row grey-out strength, cvar-read live with
    /// the shipped default as fallback.</summary>
    private static float EliminatedAlpha()
    {
        if (Api.Services is null) return 0.6f;
        string s = Api.Cvars.GetString("hud_panel_scoreboard_table_highlight_alpha_eliminated");
        if (string.IsNullOrEmpty(s)) return 0.6f;
        return Mathf.Clamp(Api.Cvars.GetFloat("hud_panel_scoreboard_table_highlight_alpha_eliminated"), 0f, 1f);
    }

    /// <summary>Draw one player row across all columns; returns false once the panel is full.</summary>
    private bool DrawRow(Layout layout, in ScoreRow r, int rank, ref float y, float rowH)
    {
        if (y > Size2.Y - rowH) return false;

        if (r.IsLocal)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), new Color(0.3f, 0.5f, 0.9f, 0.30f));

        // QC scoreboard.qc:1519-1520: grey out an eliminated player's row (the eliminatedPlayers bitfield)
        // with a BLACK fill at hud_panel_scoreboard_table_highlight_alpha_eliminated (shipped luma skin: 0.6).
        if (r.Eliminated)
            DrawRect(new Rect2(layout.X, y, layout.W, rowH), new Color(0f, 0f, 0f, EliminatedAlpha()));

        DrawText(new Vector2(layout.RankX + 2f, y + 3f), rank.ToString(), r.IsLocal ? new Color(1f, 1f, 1f, 1f) : FgColor, 16);

        for (int i = 0; i < _columns.Count; i++)
        {
            Column c = _columns[i];
            if (c.Kind == ColumnKind.Separator) continue;
            FieldText ft = GetField(r, c);
            if (c.Kind == ColumnKind.Name)
                DrawColored(new Vector2(layout.ColX[i], y + 3f), ft.Text, r.IsLocal ? new Color(1f, 1f, 1f, 1f) : FgColor, 16);
            else
                DrawTextRight(layout.ColRight[i], y + 3f, layout.NumW, ft.Text, ft.Color, 16);
        }

        y += rowH;
        return true;
    }

    // ---- map stats / respawn / accuracy / rankings footer (QC the footer draws) ----

    private float DrawMapStats(float x, float w, float y)
    {
        bool hasMonsters = MonstersTotal > 0;
        bool hasSecrets = SecretsTotal > 0;
        if (!hasMonsters && !hasSecrets) return y;
        if (y > Size2.Y - 60f) return y;

        DrawText(new Vector2(x, y), "Map stats:", new Color(1f, 1f, 1f, 0.9f), 14);
        y += 20f;
        if (hasMonsters)
        {
            DrawText(new Vector2(x + 8f, y), "Monsters killed:", FgColor, 14);
            DrawTextRight(x + w, y, w * 0.3f, $"{MonstersKilled}/{MonstersTotal}", FgColor, 14);
            y += 18f;
        }
        if (hasSecrets)
        {
            DrawText(new Vector2(x + 8f, y), "Secrets found:", FgColor, 14);
            DrawTextRight(x + w, y, w * 0.3f, $"{SecretsFound}/{SecretsTotal}", FgColor, 14);
            y += 18f;
        }
        return y + 6f;
    }

    private float DrawRespawn(float x, float w, float y)
    {
        if (RespawnRemaining < 0f) return y;
        if (y > Size2.Y - 24f) return y;
        string s = RespawnRemaining <= 0f ? "Respawning..." : $"Respawning in {RespawnRemaining:0.0}s";
        DrawTextCentered(new Vector2(x, y), w, s, new Color(1f, 0.9f, 0.4f, 0.95f), 16);
        return y + 22f;
    }

    private float DrawAccuracy(float x, float w, float y)
    {
        if (_accuracy.Count == 0) return y; // not networked yet — block hidden (QC gates on weapon_accuracy data)
        if (y > Size2.Y - 40f) return y;
        DrawText(new Vector2(x, y), "Accuracy:", new Color(1f, 1f, 1f, 0.9f), 14);
        y += 20f;
        float cx = x + 8f;
        foreach (var kv in _accuracy)
        {
            if (kv.Value < 0) continue; // weapon never fired (QC skips)
            string cell = $"{kv.Value}%";
            DrawText(new Vector2(cx, y), cell, AccuracyColor(kv.Value), 14);
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

    private float DrawRankings(float x, float w, float y)
    {
        // QC Scoreboard_Rankings_Draw is race/CTS only; gate on the mode + data (records aren't networked yet).
        if (_rankings.Count == 0) return y;
        if (GameScores.Gametype != "rc" && GameScores.Gametype != "cts") return y;
        if (y > Size2.Y - 40f) return y;
        DrawText(new Vector2(x, y), "Rankings:", new Color(1f, 1f, 1f, 0.9f), 14);
        y += 20f;
        for (int i = 0; i < _rankings.Count && y < Size2.Y - 20f; i++)
        {
            (int t, string holder) = _rankings[i];
            DrawText(new Vector2(x + 8f, y), $"{i + 1}.", FgColor, 14);
            DrawText(new Vector2(x + 36f, y), GameScores.TimeEncodedToString(t, compact: false), FgColor, 14);
            DrawColored(new Vector2(x + 140f, y), holder, FgColor, 14);
            y += 18f;
        }
        return y + 6f;
    }

    private void DrawFooter(float x, float w)
    {
        if (string.IsNullOrEmpty(MapName)) return;
        DrawTextRight(x + w, Size2.Y - 24f, w, MapName, new Color(1f, 1f, 1f, 0.5f), 14);
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
}
