using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Center-of-screen timed messages — port of Base/.../qcsrc/client/hud/panel/centerprint.qc (HUD panel
/// #16). The QC version kept a small ring of messages (each with an id, start time, duration and an
/// optional countdown), fading them in/out and stacking them vertically centered. It was fed by
/// <c>centerprint_generic</c> from the MSG_CENTER notification path (frag messages, countdowns, the MOTD).
///
/// This port wires that pipeline: the notification layer calls <see cref="Push"/> with a resolved
/// MSG_CENTER <see cref="Notification"/> (or a plain string), an optional message <em>id</em> for
/// replace/kill semantics (QC <c>cpid</c>), and an optional <em>count</em> for the "^COUNT" countdown
/// token. The panel renders Xonotic <c>^N</c> color codes (via <see cref="HudText"/>) and live-substitutes
/// the COUNT token, decrementing it each second so a single "Game starts in ^COUNT" message counts down.
/// </summary>
public partial class CenterPrintPanel : HudPanel
{
    /// <summary>Default on-screen duration when a caller does not specify one (QC hud_panel_centerprint_time).</summary>
    public const float DefaultDuration = 3f;
    private const float FadeIn = 0.2f;
    private const float FadeOut = 0.5f;
    private const int MaxMessages = 8;

    /// <summary>The QC "^COUNT" placeholder substituted with the live countdown number.</summary>
    public const string CountToken = "^COUNT";

    private sealed class Message
    {
        public string Text = "";
        public string Id = "";          // QC cpid group; non-empty ids replace same-id messages
        public double StartTime;
        public double ExpireTime;       // double.PositiveInfinity for a sticky message
        public int Count = -1;          // countdown seconds remaining; < 0 = no COUNT token
        public double CountAnchor;      // time the current Count value was set (for per-second decrement)
    }

    private readonly List<Message> _messages = new();
    private double _now;

    /// <summary>Add a message shown for <see cref="DefaultDuration"/> seconds (QC centerprint_AddStandard).</summary>
    public void Add(string text) => AddTimed(text, DefaultDuration);

    /// <summary>
    /// Push a fully-formed centerprint line — the entry point the notification/net layer calls for a
    /// MSG_CENTER notification (after it has localized + arg-expanded the format string). Supports the
    /// "^COUNT" countdown token (pass <paramref name="count"/> ≥ 0) and an <paramref name="id"/> for QC
    /// replace/kill semantics: a new push with the same non-empty id replaces the existing one.
    /// </summary>
    public void Push(string text, float duration = DefaultDuration, string id = "", int count = -1)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // QC cpid: an id of an existing message replaces it in place (used by countdowns/announcements).
        if (!string.IsNullOrEmpty(id))
            _messages.RemoveAll(m => m.Id == id);

        var msg = new Message
        {
            Text = text.Trim('\n'),
            Id = id,
            StartTime = _now,
            ExpireTime = duration > 0f ? _now + duration : double.PositiveInfinity,
            Count = count,
            CountAnchor = _now,
        };
        _messages.Insert(0, msg); // newest on top (QC prepends at cpm_index)
        if (_messages.Count > MaxMessages)
            _messages.RemoveRange(MaxMessages, _messages.Count - MaxMessages);
        QueueRedraw();
    }

    /// <summary>
    /// Push a MSG_CENTER <see cref="Notification"/>'s message. Uses the notification's
    /// <see cref="Notification.Normal"/> format as the text (the caller is expected to have expanded any
    /// s1/s2/f-args already; remaining "^COUNT" is driven by <paramref name="count"/>) and its
    /// <see cref="Notification.Cpid"/> as the replace/kill id.
    /// </summary>
    public void Push(Notification center, string? expandedText = null,
        float duration = DefaultDuration, int count = -1)
    {
        if (center is null) return;
        string text = !string.IsNullOrEmpty(expandedText) ? expandedText! : center.Normal;
        Push(text, duration, center.Cpid ?? "", count);
    }

    /// <summary>
    /// Add a message shown for <paramref name="duration"/> seconds (QC centerprint_Add). A non-positive
    /// duration makes it sticky until <see cref="ClearAll"/> (QC duration &lt; 0).
    /// </summary>
    public void AddTimed(string text, float duration) => Push(text, duration);

    /// <summary>Remove the message(s) with the given id (QC centerprint_Kill(id)).</summary>
    public void Kill(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (_messages.RemoveAll(m => m.Id == id) > 0) QueueRedraw();
    }

    /// <summary>Remove every message (QC centerprint_KillAll).</summary>
    public void ClearAll()
    {
        _messages.Clear();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _now += delta;

        // Decrement live countdowns once per elapsed second; expire a countdown message when it hits 0.
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            Message m = _messages[i];
            if (m.Count >= 0)
            {
                int steps = (int)(_now - m.CountAnchor);
                if (steps > 0)
                {
                    m.Count -= steps;
                    m.CountAnchor += steps;
                    if (m.Count <= 0) { _messages.RemoveAt(i); continue; }
                }
            }
            if (_now >= m.ExpireTime) _messages.RemoveAt(i);
        }
    }

    protected override void DrawPanel()
    {
        if (_messages.Count == 0) return;

        float y = 0f;
        float w = Size2.X;
        const int baseSize = 22;

        foreach (Message m in _messages)
        {
            float alpha = FadeAlpha(m);
            if (alpha <= 0.01f) continue;

            // Substitute the live countdown, then split on embedded newlines (QC tokenized by '\n').
            string text = m.Count >= 0 ? m.Text.Replace(CountToken, m.Count.ToString()) : m.Text;
            foreach (string line in text.Split('\n'))
            {
                DrawColoredCentered(y, w, line, alpha, baseSize);
                y += baseSize + 4f;
                if (y > Size2.Y - baseSize) return; // clip to panel
            }
            y += 8f; // gap between messages (QC CENTERPRINT_SPACING)
        }
    }

    /// <summary>
    /// Draw a color-coded line centered horizontally. We parse the ^N runs, measure the total to find the
    /// left origin, then lay the runs out left-to-right (Godot can't center multi-color text in one call).
    /// </summary>
    private void DrawColoredCentered(float y, float width, string line, float alpha, int size)
    {
        var baseColor = new Color(1f, 1f, 1f, alpha);
        List<HudText.Run> runs = HudText.Parse(line, baseColor);
        if (runs.Count == 0) return;

        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);

        float cx = (width - total) * 0.5f;
        foreach (HudText.Run r in runs)
        {
            var c = r.Color; c.A = alpha;
            DrawText(new Vector2(cx, y), r.Text, c, size);
            cx += MeasureText(r.Text, size);
        }
    }

    private float FadeAlpha(Message m)
    {
        double age = _now - m.StartTime;
        if (age < FadeIn)
            return (float)(age / FadeIn);
        if (!double.IsPositiveInfinity(m.ExpireTime))
        {
            double remaining = m.ExpireTime - _now;
            if (remaining < FadeOut)
                return Mathf.Clamp((float)(remaining / FadeOut), 0f, 1f);
        }
        return 1f;
    }
}
