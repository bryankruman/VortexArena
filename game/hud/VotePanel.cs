using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Vote panel — port of Base/.../qcsrc/client/hud/panel/vote.qc (HUD panel #9). The QC version, while a
/// callvote was active (<c>vote_active</c>), drew the called-vote text ("A vote has been called for: ...")
/// and two progress bars — Yes (green) and No (red) — each filled to <c>count / vote_needed</c>, with the
/// bound keys for <c>vyes</c>/<c>vno</c>, fading the whole panel in/out over 0.5s when the vote starts/ends
/// (QC <c>vote_alpha = bound(0, (time - vote_change) * 2, 1)</c>) and dimming it once the local player has
/// voted (<c>vote_highlighted</c> -> <c>hud_panel_vote_alreadyvoted_alpha</c>).
///
/// The vote state is a server/net concern, so the net layer drives it through the settable members here
/// (<see cref="Active"/>, <see cref="CalledVote"/>, <see cref="YesCount"/>/<see cref="NoCount"/>/
/// <see cref="Needed"/>, <see cref="Highlighted"/>) — the same injection model as
/// <see cref="InfoMessagesPanel"/>. The fade is computed locally from when <see cref="Active"/> last
/// changed. The skinned voteprogress images are replaced by drawn bars.
/// </summary>
public partial class VotePanel : HudPanel
{
    /// <summary>QC <c>vote_active</c>: a callvote is currently running. Drives the fade in/out.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value == _active) return;
            _active = value;
            _voteChange = CurrentTime();
        }
    }
    private bool _active;

    /// <summary>QC <c>vote_called_vote</c>: the description of the called vote (may carry <c>^N</c> colors).</summary>
    public string CalledVote { get; set; } = "";

    /// <summary>QC <c>vote_yescount</c>.</summary>
    public int YesCount { get; set; }

    /// <summary>QC <c>vote_nocount</c>.</summary>
    public int NoCount { get; set; }

    /// <summary>QC <c>vote_needed</c>: votes required to pass (bar denominator).</summary>
    public int Needed { get; set; }

    /// <summary>
    /// QC <c>vote_highlighted</c>: the local player's choice — +1 = voted Yes, -1 = voted No, 0 = not voted.
    /// A non-zero value dims the whole panel (already-voted alpha) and highlights the chosen side.
    /// </summary>
    public int Highlighted { get; set; }

    /// <summary>QC <c>hud_panel_vote_alreadyvoted_alpha</c>: panel alpha multiplier once you've voted.</summary>
    public float AlreadyVotedAlpha { get; set; } = 0.75f;

    /// <summary>The keybind hint for Yes (QC <c>getcommandkey_forcename(_("Yes"), "vyes")</c>).</summary>
    public string YesKey { get; set; } = "F1";

    /// <summary>The keybind hint for No (QC <c>getcommandkey_forcename(_("No"), "vno")</c>).</summary>
    public string NoKey { get; set; } = "F2";

    /// <summary>The panel only draws while live (dynamic), but its fade requires per-frame repaints.</summary>
    public override bool IsDynamic => true;

    private double _voteChange = -1.0;
    private double _localClock;

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private double CurrentTime()
    {
        if (Api.Services is not null) return Api.Clock.Time;
        return _localClock;
    }

    protected override void DrawPanel()
    {
        double now = CurrentTime();

        // QC vote_alpha: fade up while active, down once it ends.
        float fade;
        if (_voteChange < 0.0)
            fade = Active ? 1f : 0f;
        else if (Active)
            fade = Mathf.Clamp((float)(now - _voteChange) * 2f, 0f, 1f);
        else
            fade = Mathf.Clamp(1f - (float)(now - _voteChange) * 2f, 0f, 1f);

        // QC: once you've voted the whole panel dims to alreadyvoted_alpha.
        float a = fade * (Highlighted != 0 ? AlreadyVotedAlpha : 1f);
        if (a <= 0f) return;

        DrawBackground();

        float pad = Padding;
        float x = pad;
        float w = Size2.X - pad * 2f;
        float h = Size2.Y - pad * 2f;

        // Title line (QC: "A vote has been called for:" in the top 2/8).
        int titleSize = (int)Mathf.Clamp(h * 0.16f, 11f, 20f);
        DrawTextCentered(new Vector2(0f, pad), Size2.X, "A vote has been called for:",
            new Color(1f, 1f, 1f, a), titleSize);

        // The called-vote description (color-coded), under the title.
        float descY = pad + h * 0.24f;
        int descSize = (int)Mathf.Clamp(h * 0.16f, 11f, 22f);
        DrawColoredCentered(new Vector2(pad, descY), w, CalledVote, new Color(1f, 1f, 1f, a), descSize);

        // Yes / No key+count headers (QC the "Yes (3)" / "No (2)" line at 4/8).
        float countY = pad + h * 0.50f;
        int countSize = (int)Mathf.Clamp(h * 0.14f, 10f, 18f);
        DrawTextCentered(new Vector2(x, countY), w * 0.5f,
            $"{YesKey} ({YesCount})", new Color(0.3f, 1f, 0.3f, a), countSize);
        DrawTextCentered(new Vector2(x + w * 0.5f, countY), w * 0.5f,
            $"{NoKey} ({NoCount})", new Color(1f, 0.3f, 0.3f, a), countSize);

        // The two progress bars (QC voteprogress_back + voteprogress_prog, filled to count/needed).
        float barsY = pad + h * 0.68f;
        float barsH = h * 0.26f;
        float gap = w * 0.03f;
        float barW = (w - gap) * 0.5f;
        float denom = Needed > 0 ? Needed : 1;

        var yesBar = new Rect2(x, barsY, barW, barsH);
        DrawBar(yesBar, YesCount / denom, new Color(0.25f, 0.9f, 0.25f, a));
        if (Highlighted == 1)
            DrawRect(yesBar, new Color(1f, 1f, 1f, a * 0.5f), filled: false, width: 2f);

        var noBar = new Rect2(x + barW + gap, barsY, barW, barsH);
        DrawBar(noBar, NoCount / denom, new Color(0.9f, 0.25f, 0.2f, a));
        if (Highlighted == -1)
            DrawRect(noBar, new Color(1f, 1f, 1f, a * 0.5f), filled: false, width: 2f);
    }

    /// <summary>Draw a (possibly <c>^N</c> color-coded) line horizontally centered within <paramref name="width"/>.</summary>
    private void DrawColoredCentered(Vector2 pos, float width, string line, Color baseColor, int size)
    {
        if (string.IsNullOrEmpty(line)) return;
        var runs = HudText.Parse(line, baseColor);
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        float cx = pos.X + (width - total) * 0.5f;
        if (cx < pos.X) cx = pos.X;
        foreach (HudText.Run r in runs)
        {
            DrawText(new Vector2(cx, pos.Y), r.Text, r.Color, size);
            cx += MeasureText(r.Text, size);
        }
    }
}
