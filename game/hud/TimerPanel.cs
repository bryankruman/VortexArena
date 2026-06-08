using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Match timer — port of Base/.../qcsrc/client/hud/panel/timer.qc (HUD panel #5). The QC version read
/// <c>STAT(GAMESTARTTIME)</c>, <c>STAT(TIMELIMIT)</c> and the current time to show either elapsed time
/// (count up) or time remaining (count down), coloring it yellow then red as the limit approached, plus a
/// "Warmup"/"Overtime" subtext and a round subtimer.
///
/// Match state is fed by the net/match layer, not carried on a player: it sets <see cref="MatchStartTime"/>
/// and <see cref="TimeLimitSeconds"/> (QC GAMESTARTTIME / TIMELIMIT*60), the warmup/overtime flags, and an
/// optional round subtimer via <see cref="SetRound"/>. The panel renders the main timer (count up/down with
/// the warning colors), the subtext line ("Warmup"/"Overtime"), and the round timer when active. The clock
/// comes from <see cref="Now"/> if set, else the sim clock (<see cref="Api.Clock"/>), else its own ticker.
/// </summary>
public partial class TimerPanel : HudPanel
{
    /// <summary>Absolute time (seconds, same clock as <see cref="Now"/>) the match started. QC GAMESTARTTIME.</summary>
    public double MatchStartTime { get; set; }

    /// <summary>Match time limit in seconds; &lt;= 0 means no limit (count up). QC TIMELIMIT * 60.</summary>
    public float TimeLimitSeconds { get; set; }

    /// <summary>Force count-up even when a limit is set (QC hud_panel_timer_increment).</summary>
    public bool CountUp { get; set; }

    /// <summary>True during warmup (QC warmup_stage): shows "Warmup" and counts up regardless of limit.</summary>
    public bool WarmupStage { get; set; }

    /// <summary>
    /// True once the match has gone into overtime (QC overtime): shows "Overtime" and the main timer counts
    /// up past the limit. The match layer sets this when it grants overtime.
    /// </summary>
    public bool Overtime { get; set; }

    /// <summary>Optional explicit subtext under the timer (QC subtext). Overrides the warmup/overtime label.</summary>
    public string Subtext { get; set; } = "";

    /// <summary>
    /// The current time on the same clock as <see cref="MatchStartTime"/>. If &lt; 0 (default), the panel
    /// uses the sim clock when available, else its own wall clock. Set it to slave to the match clock.
    /// </summary>
    public double Now { get; set; } = -1.0;

    // --- round subtimer (QC the second smaller timer for round-based gametypes) ---
    private bool _roundActive;
    private double _roundEndTime;     // absolute time the round limit expires
    private string _roundLabel = "";

    private double _localClock;

    private const float WarnYellow = 60f; // seconds remaining -> yellow (QC hud_panel_timer_warning_yellow)
    private const float WarnRed = 30f;     // seconds remaining -> red   (QC hud_panel_timer_warning_red)

    /// <summary>
    /// Set the round subtimer (QC round_starttime/round_limit). <paramref name="endTime"/> is the absolute
    /// time (same clock as <see cref="Now"/>) the round ends; pass a label like "Round". Call
    /// <see cref="ClearRound"/> to hide it.
    /// </summary>
    public void SetRound(double endTime, string label = "Round")
    {
        _roundActive = true;
        _roundEndTime = endTime;
        _roundLabel = label ?? "";
    }

    /// <summary>Hide the round subtimer (QC: round not running).</summary>
    public void ClearRound() => _roundActive = false;

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

    protected override void DrawPanel()
    {
        DrawBackground();

        double current = CurrentTime();
        double elapsed = current - MatchStartTime;
        if (elapsed < 0d) elapsed = 0d;

        bool hasLimit = TimeLimitSeconds > 0f;
        // Warmup and overtime always count up; otherwise count down against the limit (unless forced up).
        bool countDown = hasLimit && !CountUp && !WarmupStage && !Overtime;

        string text;
        Color color = FgColor;
        if (countDown)
        {
            float remaining = TimeLimitSeconds - (float)elapsed;
            if (remaining < 0f) remaining = 0f;
            text = SecondsToString(remaining);
            // QC HUD_Timer_Color: white -> yellow -> red as the limit approaches.
            if (remaining <= WarnRed) color = new Color(1f, 0.25f, 0.25f, FgColor.A);
            else if (remaining <= WarnYellow) color = new Color(1f, 1f, 0.25f, FgColor.A);
        }
        else
        {
            text = SecondsToString((float)elapsed);
            if (Overtime) color = new Color(1f, 0.5f, 0.2f, FgColor.A);
        }

        // Subtext: explicit override, else the match-state label.
        string sub = !string.IsNullOrEmpty(Subtext) ? Subtext
                   : WarmupStage ? "Warmup"
                   : Overtime ? "Overtime"
                   : "";
        bool hasSub = !string.IsNullOrEmpty(sub);
        bool hasRound = _roundActive;

        // Vertical budget: optional subtext row, optional round row, main timer fills the rest.
        float extraH = (hasSub ? Size2.Y * 0.26f : 0f) + (hasRound ? Size2.Y * 0.26f : 0f);
        float timerH = Size2.Y - extraH;
        int timerSize = (int)Mathf.Clamp(timerH * 0.7f, 16f, 40f);

        DrawTextCentered(new Vector2(0f, (timerH - timerSize) * 0.5f), Size2.X, text, color, timerSize);

        float y = timerH;
        if (hasRound)
        {
            float left = (float)(_roundEndTime - current);
            if (left < 0f) left = 0f;
            float rowH = Size2.Y * 0.26f;
            int rs = (int)Mathf.Clamp(rowH * 0.75f, 11f, 22f);
            string rtext = string.IsNullOrEmpty(_roundLabel)
                ? SecondsToString(left)
                : $"{_roundLabel} {SecondsToString(left)}";
            DrawTextCentered(new Vector2(0f, y + (rowH - rs) * 0.5f), Size2.X, rtext,
                new Color(0.85f, 0.9f, 1f, FgColor.A), rs);
            y += rowH;
        }

        if (hasSub)
        {
            float rowH = Size2.Y * 0.26f;
            int subSize = (int)Mathf.Clamp(rowH * 0.7f, 11f, 22f);
            Color subColor = Overtime && !WarmupStage
                ? new Color(1f, 0.5f, 0.2f, FgColor.A)
                : new Color(0.4f, 1f, 0.4f, FgColor.A);
            DrawTextCentered(new Vector2(0f, y + (rowH - subSize) * 0.5f), Size2.X, sub, subColor, subSize);
        }
    }
}
