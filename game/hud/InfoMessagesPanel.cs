using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Status / info messages — port of Base/.../qcsrc/client/hud/panel/infomessages.qc (HUD panel #14). The
/// QC version drew transient state hints in the top-left: "Observing" / "Spectating: name", "Press X to
/// join", warmup notices, the "Game starts in N seconds" countdown, team-unbalanced nags, and the
/// spectator list.
///
/// This port renders that full set from a mix of local + networked state. The dead/respawn line is computed
/// from the local <see cref="Player"/> (<see cref="Player.IsDead"/> + <see cref="Player.RespawnTime"/>).
/// The match/spectator state is networked, so it is fed via settable members the net layer drives:
/// <see cref="IsSpectating"/>/<see cref="SpectatingName"/>, <see cref="WarmupStage"/>,
/// <see cref="CountdownSeconds"/>, the team-balance nag (<see cref="TeamBalanceWarning"/>), the spectator
/// list (<see cref="SetSpectators"/>), and the join/keybind hint (<see cref="JoinHint"/>). Names render with
/// their <c>^N</c> color codes.
/// </summary>
public partial class InfoMessagesPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>); drives the dead/respawn line.</summary>
    public Player? Player { get; set; }

    /// <summary>True while observing/spectating (QC spectatee_status). Shows the observing hint.</summary>
    public bool IsSpectating { get; set; }

    /// <summary>Name of the player being spectated, if any (QC entcs_GetName(current_player)). May carry ^N.</summary>
    public string SpectatingName { get; set; } = "";

    /// <summary>True during the warmup stage (QC warmup_stage). Shows the warmup notice.</summary>
    public bool WarmupStage { get; set; }

    /// <summary>
    /// Seconds until the match starts; &gt; 0 shows the "Game starts in N" countdown (QC GAMESTARTTIME -
    /// time). Set to 0 once the match is live. The owner updates this from the match clock.
    /// </summary>
    public float CountdownSeconds { get; set; }

    /// <summary>
    /// Team-balance nag text (QC the "teams are unbalanced" / "press to switch teams" line), or empty for
    /// none. The team logic on the net side fills this when teams need rebalancing.
    /// </summary>
    public string TeamBalanceWarning { get; set; } = "";

    /// <summary>
    /// The join / key-bind hint shown while spectating or dead (QC the keybind-substituted "press jump to
    /// join" line). Settable so the keybind layer can substitute the real bound key. Empty falls back to a
    /// generic "Press Fire to join/respawn".
    /// </summary>
    public string JoinHint { get; set; } = "";

    /// <summary>
    /// The current sim/match time, used to compute the respawn countdown against
    /// <see cref="Player.RespawnTime"/>. If left &lt; 0 the respawn line shows without a countdown.
    /// </summary>
    public double Now { get; set; } = -1.0;

    /// <summary>
    /// QC <c>STAT(RESPAWN_TIME)</c> as networked to the owner (ClientNet.RespawnTimeStat): 0 = alive; otherwise
    /// the absolute respawn time, NEGATED while a respawn is imminent. Drives the dead/respawn line WITHOUT a
    /// local <see cref="Player"/> actor — so the countdown shows on a pure remote client too. Paired with
    /// <see cref="NetServerTime"/> for the remaining seconds.
    /// </summary>
    public float RespawnStat { get; set; }

    /// <summary>The latest networked server time (ClientNet.LatestServerTime), to count <see cref="RespawnStat"/> down against.</summary>
    public float NetServerTime { get; set; }

    private readonly List<string> _extra = new();
    private readonly List<string> _spectators = new();

    /// <summary>Push an extra info line shown below the computed ones (QC InfoMessage). Replaces nothing.</summary>
    public void AddLine(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) _extra.Add(text);
        QueueRedraw();
    }

    /// <summary>Clear the owner-pushed extra lines.</summary>
    public void ClearLines()
    {
        _extra.Clear();
        QueueRedraw();
    }

    /// <summary>
    /// Set the spectator list (QC the "Spectators: a, b, c" footer), by display name (names may carry ^N).
    /// Pass null/empty to hide it.
    /// </summary>
    public void SetSpectators(IEnumerable<string>? names)
    {
        _spectators.Clear();
        if (names is not null)
            foreach (string n in names)
                if (!string.IsNullOrWhiteSpace(n)) _spectators.Add(n);
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        // Gather the (possibly color-coded) lines to draw this frame.
        var lines = new List<string>();

        if (IsSpectating)
        {
            lines.Add(string.IsNullOrEmpty(SpectatingName) ? "Observing" : $"Spectating: {SpectatingName}");
            lines.Add(string.IsNullOrEmpty(JoinHint) ? "Press Fire to join" : JoinHint);
        }
        else if (RespawnStat != 0f)
        {
            // Networked respawn timer (QC STAT(RESPAWN_TIME)) — works even with no local Player actor (pure
            // client). |stat| is the absolute respawn time; a negative stat means a respawn is imminent.
            float remaining = Mathf.Abs(RespawnStat) - NetServerTime;
            if (remaining > 0.05f)
                lines.Add($"Respawning in {Mathf.CeilToInt(remaining)}...");
            else
                lines.Add(string.IsNullOrEmpty(JoinHint) ? "Press Fire to respawn" : JoinHint);
        }
        else if (Player is not null && Player.IsDead)
        {
            if (Now >= 0.0 && Player.RespawnTime > 0f)
            {
                float secs = Mathf.Max(0f, (float)(Player.RespawnTime - Now));
                lines.Add($"Respawning in {Mathf.CeilToInt(secs)}...");
            }
            else
            {
                lines.Add(string.IsNullOrEmpty(JoinHint) ? "Press Fire to respawn" : JoinHint);
            }
        }

        if (WarmupStage)
            lines.Add("Warmup");

        if (CountdownSeconds > 0f)
            lines.Add($"Game starts in {Mathf.CeilToInt(CountdownSeconds)} seconds");

        if (!string.IsNullOrEmpty(TeamBalanceWarning))
            lines.Add(TeamBalanceWarning);

        lines.AddRange(_extra);

        if (_spectators.Count > 0)
            lines.Add("Spectators: " + string.Join(", ", _spectators));

        if (lines.Count == 0) return;

        DrawBackground();

        float pad = Padding;
        float y = pad;
        const int size = 16;
        float w = Size2.X - pad * 2f;
        foreach (string line in lines)
        {
            DrawColored(new Vector2(pad, y), line, FgColor, size, w);
            y += size + 6f;
            if (y > Size2.Y - size) break; // clip to panel
        }
    }

    /// <summary>Draw a (possibly ^N color-coded) line left-to-right, clipped to <paramref name="maxW"/>.</summary>
    private void DrawColored(Vector2 pos, string line, Color baseColor, int size, float maxW)
    {
        float cx = pos.X;
        float limit = pos.X + maxW;
        foreach (HudText.Run run in HudText.Parse(line, baseColor))
        {
            DrawText(new Vector2(cx, pos.Y), run.Text, run.Color, size);
            cx += MeasureText(run.Text, size);
            if (cx >= limit) break;
        }
    }
}
