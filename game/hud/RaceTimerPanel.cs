using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Race timer panel — port of Base/.../qcsrc/client/hud/panel/racetimer.qc (HUD panel #8). The QC version is
/// the on-screen race split timer: while you run a Race/CTS lap it shows the running lap time (big, bold),
/// and on every checkpoint it flashes a "Checkpoint N (+/-delta vs the record)" split (built by
/// <c>MakeRaceString</c>) for ~2s plus any penalty text. It reads a pile of networked race stats
/// (<c>race_checkpoint</c>, <c>race_time</c>, <c>race_laptime</c>, <c>race_checkpointtime</c>, the
/// previous-best times/names, the penalty fields) that arrive via the <c>TE_CSQC_RACE</c> message.
///
/// Those race stats are a server/net concern, so the net layer drives them through the settable members here
/// (the <c>Race*</c> properties), the same injection model as <see cref="TimerPanel"/>/<see cref="VotePanel"/>.
/// The panel renders: the running lap time (count up from <see cref="RaceLapTime"/>), the most-recent
/// checkpoint split (faded over 2s via <see cref="RaceCheckpointTime"/>), and the penalty line. The faithful
/// <see cref="MakeRaceString"/> split formatter is ported in full (start/finish/checkpoint naming, +/-
/// coloring, lap deltas, opponent name). Times are kept in plain seconds here rather than the QC encoded
/// tenths, so the encode/decode helpers collapse away.
/// </summary>
public partial class RaceTimerPanel : HudPanel
{
    // ---- networked race stats (QC racetimer.qh globals, set from TE_CSQC_RACE) ----

    /// <summary>QC <c>race_checkpoint</c>: the checkpoint just reached (254 = start, 255/0 = finish).</summary>
    public int RaceCheckpoint { get; set; }

    /// <summary>QC <c>race_laptime</c>: absolute time the current lap started; 0 if not lapping.</summary>
    public double RaceLapTime { get; set; }

    /// <summary>QC <c>race_checkpointtime</c>: absolute time the last checkpoint was hit (drives the split fade).</summary>
    public double RaceCheckpointTime { get; set; }

    /// <summary>QC <c>race_time</c>: your split time (seconds) at the last checkpoint.</summary>
    public float RaceTime { get; set; }

    /// <summary>QC <c>race_previousbesttime</c>: the record split (seconds) at that checkpoint.</summary>
    public float RacePreviousBestTime { get; set; }

    /// <summary>QC <c>race_mypreviousbesttime</c>: your personal-best split (seconds) at that checkpoint.</summary>
    public float RaceMyPreviousBestTime { get; set; }

    /// <summary>QC <c>race_previousbestname</c>: the record holder's name (may carry <c>^N</c>).</summary>
    public string RacePreviousBestName { get; set; } = "";

    /// <summary>QC <c>race_penaltytime</c>: penalty magnitude in tenths of a second; 0 = none.</summary>
    public float RacePenaltyTime { get; set; }

    /// <summary>QC <c>race_penaltyeventtime</c>: absolute time the penalty was applied (drives its fade).</summary>
    public double RacePenaltyEventTime { get; set; }

    /// <summary>QC <c>race_penaltyreason</c>: human-readable penalty reason.</summary>
    public string RacePenaltyReason { get; set; } = "";

    /// <summary>QC <c>spectatee_status == -1</c> guard: while observing, the race timer hides.</summary>
    public bool Observing { get; set; }

    /// <summary>Force count-up display even with no live lap (so the panel can still show a frozen time).</summary>
    public double Now { get; set; } = -1.0;

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
    /// Port of racetimer.qc <c>MakeRaceString</c>: build the colored split string for a checkpoint.
    /// <paramref name="cp"/> is the checkpoint number (254=start, 255=finish), <paramref name="mytime"/> the
    /// delta vs the record (negative = ahead), <paramref name="theirtime"/> &lt; 0 means "no comparison yet"
    /// and &gt; 0 means the record is still being anticipated, <paramref name="lapdelta"/> the lap difference,
    /// and <paramref name="theirname"/> the record holder. Times are seconds.
    /// </summary>
    public static string MakeRaceString(int cp, float mytime, float theirtime, float othertime,
        int lapdelta, string theirname)
    {
        string lapstr = "", timestr = "", col = "^7", lapcol = "^7";
        if (string.IsNullOrEmpty(theirname))
            othertime = 0; // don't count personal time when there's no comparison name

        if (theirtime == 0f) // goal hit: show the +/- delta colored by sign
        {
            if (mytime > 0f) { timestr = "+" + mytime.ToString("0.000"); col = "^1"; }
            else if (mytime == 0f) { timestr = "+0.000"; col = "^3"; }
            else { timestr = "-" + (-mytime).ToString("0.000"); col = "^2"; }

            if (lapdelta > 0) { lapstr = $" (-{lapdelta}L)"; lapcol = "^2"; }
            else if (lapdelta < 0) { lapstr = $" (+{-lapdelta}L)"; lapcol = "^1"; }
        }
        else if (theirtime > 0f) // anticipation: either you're already behind, or the record still ticks
        {
            timestr = mytime >= theirtime
                ? "+" + (mytime - theirtime).ToString("0.000")
                : theirtime.ToString("0.000");
            col = "^3";
        }

        string cpname = cp switch
        {
            254 => "Start line",
            255 => "Finish line",
            0 => "Finish line",
            _ => $"Checkpoint {cp}",
        };

        if (theirtime < 0f)
            return col + cpname;
        if (string.IsNullOrEmpty(theirname))
            return $"{col}{cpname} ({timestr})";
        return $"{col}{cpname} ({timestr} {theirname}{lapcol}{lapstr})";
    }

    protected override void DrawPanel()
    {
        if (Observing) return; // QC spectatee_status == -1 guard

        DrawBackground();

        double now = CurrentTime();
        float pad = Padding;
        float w = Size2.X - pad * 2f;

        // QC enforces a 4:1 aspect inside the panel; we just use the padded rect and lay text in three rows:
        // a recent-checkpoint split (top), the penalty line, and the big running lap time (filling the rest).

        // --- checkpoint split, faded over 2s (QC: a = bound(0, 2 - (time - race_checkpointtime), 1)) ---
        if (RaceCheckpointTime > 0.0)
        {
            float a = Mathf.Clamp(2f - (float)(now - RaceCheckpointTime), 0f, 1f);
            if (a > 0f && RaceCheckpoint != 254)
            {
                string split = (RaceTime != 0f && RacePreviousBestTime != 0f)
                    ? MakeRaceString(RaceCheckpoint, RaceTime - RacePreviousBestTime, 0f,
                        RaceMyPreviousBestTime != 0f ? RaceTime - RaceMyPreviousBestTime : 0f, 0, RacePreviousBestName)
                    : MakeRaceString(RaceCheckpoint, 0f, -1f, 0f, 0, RacePreviousBestName);

                int sz = (int)Mathf.Clamp(Size2.Y * 0.2f, 10f, 22f);
                DrawColoredCentered(new Vector2(pad, Size2.Y * 0.04f), w, split,
                    new Color(1f, 1f, 1f, FgColor.A * a), sz);
            }
        }

        // --- penalty line, faded over 1s (QC: a = bound(0, 2 - (time - race_penaltyeventtime), 1)) ---
        if (RacePenaltyTime != 0f)
        {
            float a = Mathf.Clamp(2f - (float)(now - RacePenaltyEventTime), 0f, 1f);
            if (a > 0f)
            {
                string s = $"^1PENALTY: {(RacePenaltyTime * 0.1f):0.0} ({RacePenaltyReason})";
                int sz = (int)Mathf.Clamp(Size2.Y * 0.2f, 10f, 22f);
                DrawColoredCentered(new Vector2(pad, Size2.Y * 0.30f), w, s,
                    new Color(1f, 1f, 1f, FgColor.A * a), sz);
            }
        }

        // --- running lap time, big and bold (QC: TIME from time - race_laptime) ---
        if (RaceLapTime > 0.0 && RaceCheckpoint != 255)
        {
            float lap = (float)(now - RaceLapTime);
            if (lap < 0f) lap = 0f;
            int big = (int)Mathf.Clamp(Size2.Y * 0.5f, 18f, 48f);
            DrawTextCentered(new Vector2(0f, Size2.Y * 0.45f), Size2.X, FormatRaceTime(lap),
                FgColor, big);
        }
    }

    /// <summary>QC <c>TIME_ENCODED_TOSTRING</c> form for a running time: M:SS.mmm.</summary>
    private static string FormatRaceTime(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        int m = total / 60;
        int s = total % 60;
        int ms = (int)((seconds - total) * 1000f);
        return m > 0 ? $"{m}:{s:D2}.{ms:D3}" : $"{s}.{ms:D3}";
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
