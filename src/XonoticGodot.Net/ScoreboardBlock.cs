using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;          // Teams (entcs team slice cref)
using XonoticGodot.Common.Gameplay.Scoring;

namespace XonoticGodot.Net;

/// <summary>One player's networked score row on the wire: the owner's stable net id, the QC <c>entcs</c> display
/// slice (name + team color code) so the client can label/group the row without a separate entcs stream, and the
/// column values in <see cref="GameScores.NetworkedFields"/> order.
///
/// The <see cref="Name"/>/<see cref="Team"/> pair is the C# stand-in for QC <c>entcs_GetName(sv_entnum)</c> /
/// <c>entcs_GetScoreTeam(sv_entnum)</c> (client/hud/panel/scoreboard.qc): the port networks players by name and
/// has no entcs name stream, so the scoreboard would otherwise have only an opaque net id with no way to resolve
/// it to a display name. Carrying the slice here is the minimal faithful entcs equivalent. Both default to
/// ""/<see cref="Teams.None"/> so a caller that only has columns (the score-codec round-trip test) still works.</summary>
public readonly struct ScoreRowWire
{
    public readonly int NetId;
    public readonly string Name;
    public readonly int Team;
    public readonly int[] Columns;
    /// <summary>QC <c>pl.team == NUM_SPECTATOR</c> (scoreboard.qc:2369): this row is a spectator/observer, so the
    /// client lists it in the <c>Scoreboard_Spectators_Draw</c> block instead of the score table. The port has no
    /// NUM_SPECTATOR team sentinel (observers keep their last team color), so we carry the flag explicitly.</summary>
    public readonly bool IsSpectator;
    public ScoreRowWire(int netId, int[] columns, string name = "", int team = 0, bool isSpectator = false)
    { NetId = netId; Columns = columns; Name = name ?? ""; Team = team; IsSpectator = isSpectator; }
}

/// <summary>The parsed scoreboard (client side after <see cref="ScoreboardBlock.Deserialize"/>).</summary>
public sealed class ScoreboardWire
{
    public readonly List<ScoreRowWire> Rows = new();
    public readonly List<(int team, int score)> Teams = new();
}

/// <summary>
/// Server→client replication of the score table — the C# successor to QuakeC's <c>PlayerScore_SendEntity</c> +
/// <c>TeamScore_SendEntity</c> (server/scores.qc). Carries every player's networked score columns (the
/// non-hidden SP_* fields from <see cref="GameScores.NetworkedFields"/>, positionally — both sides derive the
/// same ordered list from the registry, so no names on the wire) plus the per-team totals. Sent on the snapshot
/// channel, gated by <see cref="GameScores.Version"/> so the steady-state cost is a single bool when nothing
/// scored (matching the movevars block's change-gating).
///
/// Faithful to QC's int24 score quantization (<c>WriteInt24_t</c>); the column order is content-hash-agreed via
/// the <see cref="ScoreField"/> registry so a mod that adds a column can't desync the layout.
/// </summary>
public static class ScoreboardBlock
{
    /// <summary>FNV-1a content hash over the networked field names — the layout agreement check (CL must match SV).</summary>
    public static uint LayoutHash()
    {
        uint h = 2166136261u;
        foreach (var f in GameScores.NetworkedFields)
            foreach (char c in f.Name) { h ^= c; h *= 16777619u; }
        return h;
    }

    /// <summary>Gather the wire rows for a set of players (server side), keyed by a caller-supplied net id. The
    /// row also captures the player's display name (<see cref="Entity.NetName"/>) + team for the client's entcs
    /// slice (see <see cref="ScoreRowWire"/>).</summary>
    public static List<ScoreRowWire> CaptureRows(IEnumerable<(int netId, Entity player)> players)
    {
        var rows = new List<ScoreRowWire>();
        foreach (var (netId, p) in players)
            rows.Add(new ScoreRowWire(netId, GameScores.CaptureColumns(p), p.NetName, (int)p.Team,
                isSpectator: p is Player { IsObserver: true }));
        return rows;
    }

    /// <summary>Serialize the full scoreboard block: field-count + layout hash (sanity), the player rows, then the team totals.</summary>
    public static void Serialize(BitWriter w, IReadOnlyList<ScoreRowWire> rows, IReadOnlyList<(int team, int score)> teamScores)
    {
        int n = GameScores.NetworkedFields.Count;
        w.WriteByte(n);
        w.WriteULong(LayoutHash());

        w.WriteByte(rows.Count > 255 ? 255 : rows.Count);
        int rc = System.Math.Min(rows.Count, 255);
        for (int i = 0; i < rc; i++)
        {
            ScoreRowWire row = rows[i];
            w.WriteUShort(row.NetId);
            w.WriteString(row.Name);    // entcs name slice (QC entcs_GetName)
            w.WriteByte(row.Team & 0xFF); // entcs team slice (QC entcs_GetScoreTeam)
            w.WriteBool(row.IsSpectator); // QC pl.team == NUM_SPECTATOR (drives Scoreboard_Spectators_Draw)
            int m = System.Math.Min(row.Columns.Length, n);
            for (int j = 0; j < n; j++)
                w.WriteInt24(j < m ? row.Columns[j] : 0);
        }

        w.WriteByte(teamScores.Count > 255 ? 255 : teamScores.Count);
        int tc = System.Math.Min(teamScores.Count, 255);
        for (int i = 0; i < tc; i++)
        {
            w.WriteByte(teamScores[i].team & 0xFF);
            w.WriteInt24(teamScores[i].score);
        }
    }

    /// <summary>Read a scoreboard block. Returns null if the layout hash disagrees (the client mod set differs).</summary>
    public static ScoreboardWire? Deserialize(ref BitReader r)
    {
        int n = r.ReadByte();
        uint hash = r.ReadULong();
        if (n < 0 || n > 64) return null;
        bool layoutOk = hash == LayoutHash();

        var sb = new ScoreboardWire();
        int rows = r.ReadByte();
        for (int i = 0; i < rows; i++)
        {
            int netId = r.ReadUShort();
            string name = r.ReadString();    // entcs name slice
            int team = r.ReadByte();          // entcs team slice
            bool isSpec = r.ReadBool();        // QC pl.team == NUM_SPECTATOR
            var cols = new int[n];
            for (int j = 0; j < n; j++) cols[j] = r.ReadInt24();
            if (!r.BadRead) sb.Rows.Add(new ScoreRowWire(netId, cols, name, team, isSpec));
        }
        int teams = r.ReadByte();
        for (int i = 0; i < teams; i++)
        {
            int team = r.ReadByte();
            int score = r.ReadInt24();
            if (!r.BadRead) sb.Teams.Add((team, score));
        }
        if (r.BadRead || !layoutOk) return null; // unreadable, or the client's column layout disagrees
        return sb;
    }
}
