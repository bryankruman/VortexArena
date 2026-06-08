using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Map-vote panel — port of Base/.../qcsrc/client/mapvoting.qc <c>MapVote_Draw</c> (HUD panel #MAPVOTE,
/// registered in hud.qh to <c>MapVote_Draw</c>). At the end of a match (intermission) the server offers a set
/// of candidate maps (or gametypes) and the clients draw a grid of cells — one per candidate, each with its
/// map name, current vote count and a thumbnail — plus a big bold title ("Vote for a map") and a green
/// seconds-remaining countdown (<c>mv_timeout - time</c>). Your own vote (<c>mv_ownvote</c>) is highlighted,
/// the currently-selected cell pulses, and when voting ends the winner (<c>mv_winner</c>) is revealed while
/// the rest fade out.
///
/// The whole vote state is server-driven (the <c>mapvote</c> net entity), so the net/match layer pushes it
/// here via <see cref="SetVote"/> and updates the live bits (<see cref="OwnVote"/>, <see cref="Selection"/>,
/// <see cref="Winner"/>) — the same injection model as the other match panels. The panel computes the grid
/// (best item aspect ratio, like QC <c>HUD_GetTableSize_BestItemAR</c>), draws each cell with its name +
/// count + highlight, and renders the countdown. Thumbnails are replaced by a colored placeholder swatch.
/// </summary>
public partial class MapVotePanel : HudPanel
{
    /// <summary>One vote candidate (QC <c>mv_maps[id]</c> / <c>mv_votes[id]</c> entry).</summary>
    public readonly struct Candidate
    {
        /// <summary>Display name (QC <c>mv_maps[id]</c> / the gametype name).</summary>
        public readonly string Name;
        /// <summary>Vote count (QC <c>mv_votes[id]</c>; -1 means "hidden until reveal").</summary>
        public readonly int Votes;
        public Candidate(string name, int votes) { Name = name; Votes = votes; }
    }

    private readonly List<Candidate> _candidates = new();

    /// <summary>QC <c>mv_active</c>: a map vote is in progress (the panel only draws while true).</summary>
    public bool Active { get; set; }

    /// <summary>QC <c>gametypevote</c>: this is a gametype vote (title becomes "Decide the gametype").</summary>
    public bool GametypeVote { get; set; }

    /// <summary>QC <c>mv_timeout</c>: absolute time the vote closes; the countdown is <c>mv_timeout - now</c>.</summary>
    public double Timeout { get; set; }

    /// <summary>QC <c>mv_ownvote</c>: index of the candidate the local player voted for (-1 = none).</summary>
    public int OwnVote { get; set; } = -1;

    /// <summary>QC <c>mv_selection</c>: index of the cell currently under the cursor/keyboard (-1 = none).</summary>
    public int Selection { get; set; } = -1;

    /// <summary>QC <c>mv_winner</c>: 1-based index of the winning candidate once decided (0 = not yet).</summary>
    public int Winner { get; set; }

    /// <summary>QC <c>mv_abstain</c>: an extra "Don't care" option is offered.</summary>
    public bool Abstain { get; set; }

    /// <summary>Slave the countdown to this clock; &lt; 0 uses the sim clock (else its own ticker).</summary>
    public double Now { get; set; } = -1.0;

    public override bool IsDynamic => true;

    private double _localClock;
    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        if (Now >= 0.0) return Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    /// <summary>
    /// Replace the candidate set (QC: the <c>mapvote</c> net entity populating <c>mv_maps</c>/<c>mv_votes</c>).
    /// </summary>
    public void SetVote(IEnumerable<Candidate>? candidates, double timeout, bool gametypeVote = false, bool abstain = false)
    {
        _candidates.Clear();
        if (candidates is not null) _candidates.AddRange(candidates);
        Timeout = timeout;
        GametypeVote = gametypeVote;
        Abstain = abstain;
        Active = _candidates.Count > 0;
        QueueRedraw();
    }

    /// <summary>Update the live vote counts in place (QC: incremental <c>mv_votes</c> updates).</summary>
    public void SetVotes(IReadOnlyList<int> votes)
    {
        if (votes is null) return;
        for (int i = 0; i < _candidates.Count && i < votes.Count; i++)
            _candidates[i] = new Candidate(_candidates[i].Name, votes[i]);
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        if (!Active || _candidates.Count == 0) return;

        DrawBackground();

        double now = CurrentTime();
        float pad = Padding;
        float w = Size2.X - pad * 2f;

        // --- title (QC: bold, hud_fontsize*2, hidden once a winner is shown) ---
        float y = pad;
        if (Winner == 0)
        {
            int titleSize = (int)Mathf.Clamp(Size2.Y * 0.08f, 16f, 32f);
            string title = GametypeVote ? "Decide the gametype" : "Vote for a map";
            DrawTextCentered(new Vector2(0f, y), Size2.X, title, FgColor, titleSize);
            y += titleSize + Size2.Y * 0.02f;
        }

        // --- countdown / winner line (QC: green, hud_fontsize*1.5) ---
        int subSize = (int)Mathf.Clamp(Size2.Y * 0.06f, 14f, 26f);
        string sub;
        if (Winner > 0 && Winner <= _candidates.Count)
            sub = _candidates[Winner - 1].Name;
        else
        {
            int secs = Mathf.CeilToInt(Mathf.Max(1f, (float)(Timeout - now)));
            sub = $"{secs} seconds left";
        }
        DrawTextCentered(new Vector2(0f, y), Size2.X, sub, new Color(0.3f, 1f, 0.3f, FgColor.A), subSize);
        y += subSize + Size2.Y * 0.03f;

        // --- the candidate grid (QC HUD_GetTableSize_BestItemAR: pick the column count that best fits) ---
        int n = _candidates.Count;
        float gridTop = y;
        float gridH = Size2.Y - pad - gridTop;
        if (gridH <= 0f) return;

        const float itemAspect = 5f / 3f; // QC item_aspect for map items
        int columns = BestColumns(n, w, gridH, itemAspect);
        int rows = Mathf.CeilToInt(n / (float)columns);

        float cellW = w / columns;
        float cellH = gridH / rows;
        float winnerFade = Winner > 0
            ? Mathf.Max(0.2f, 1f - Mathf.Sqrt(Mathf.Max(0f, (float)(now - (Timeout))))) // approx mv_winner fade
            : 1f;

        for (int i = 0; i < n; i++)
        {
            int col = i % columns;
            int row = i / columns;
            var cellPos = new Vector2(pad + col * cellW, gridTop + row * cellH);
            DrawCandidate(cellPos, new Vector2(cellW, cellH), i, winnerFade);
        }
    }

    /// <summary>Draw one candidate cell: a thumbnail placeholder, the map name, and its vote count.</summary>
    private void DrawCandidate(Vector2 pos, Vector2 size, int id, float winnerFade)
    {
        Candidate c = _candidates[id];
        Color rgb = CandidateColor(id);

        float margin = 3f;
        var inner = new Rect2(pos.X + margin, pos.Y + margin, size.X - margin * 2f, size.Y - margin * 2f);

        // Highlight: own vote gets a filled tint + border; the selected cell gets a soft fill (QC drawfill).
        float a = winnerFade;
        if (Winner == 0)
        {
            if (id == OwnVote)
            {
                DrawRect(inner, new Color(rgb.R, rgb.G, rgb.B, 0.18f * a));
                DrawRect(inner, new Color(rgb.R, rgb.G, rgb.B, a), filled: false, width: 2f);
            }
            else if (id == Selection)
            {
                DrawRect(inner, new Color(1f, 1f, 1f, 0.12f * a));
            }
        }
        else if (Winner - 1 == id)
        {
            DrawRect(inner, new Color(rgb.R, rgb.G, rgb.B, 0.25f));
            DrawRect(inner, new Color(rgb.R, rgb.G, rgb.B, 1f), filled: false, width: 3f);
        }
        else
        {
            a *= 0.4f; // losers fade back (QC mv_winner_alpha dims the rest)
        }

        // Thumbnail placeholder swatch (QC MapVote_DrawMapPicture; we don't have the level shot here).
        float labelH = Mathf.Clamp(size.Y * 0.22f, 12f, 22f);
        var thumb = new Rect2(inner.Position.X, inner.Position.Y, inner.Size.X, inner.Size.Y - labelH);
        DrawRect(thumb, new Color(rgb.R * 0.4f, rgb.G * 0.4f, rgb.B * 0.4f, 0.6f * a));

        // Label: "name (count)" centered (QC MapVote_FormatMapItem), colored by the candidate color.
        int sz = (int)Mathf.Clamp(labelH * 0.85f, 9f, 18f);
        string countStr = c.Votes >= 0 ? $" ({c.Votes})" : "";
        DrawTextCentered(new Vector2(inner.Position.X, inner.Position.Y + inner.Size.Y - labelH),
            inner.Size.X, c.Name + countStr, new Color(rgb.R, rgb.G, rgb.B, a), sz);
    }

    /// <summary>
    /// Pick the column count that best fills the area at the target item aspect ratio — a compact stand-in for
    /// QC <c>HUD_GetTableSize_BestItemAR</c>: try every column count and keep the one whose resulting cell
    /// aspect is closest to <paramref name="itemAspect"/>.
    /// </summary>
    private static int BestColumns(int count, float areaW, float areaH, float itemAspect)
    {
        int best = 1;
        float bestErr = float.MaxValue;
        for (int cols = 1; cols <= count; cols++)
        {
            int rows = Mathf.CeilToInt(count / (float)cols);
            float cellAr = (areaW / cols) / (areaH / rows);
            float err = Mathf.Abs(cellAr - itemAspect);
            if (err < bestErr) { bestErr = err; best = cols; }
        }
        return best;
    }

    /// <summary>QC <c>MapVote_RGB</c>: a stable per-candidate accent color so cells are distinguishable.</summary>
    private static Color CandidateColor(int id)
    {
        // Spread hues evenly; mirrors how QC tints each item by id.
        float hue = (id * 0.137508f) % 1f; // golden-ratio hue stepping
        return Color.FromHsv(hue, 0.45f, 1f);
    }
}
