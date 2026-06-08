using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Kill feed — port of Base/.../qcsrc/client/hud/panel/notify.qc (HUD panel #4). The QC version kept a
/// ring of recent obituary lines, each "<c>attacker [icon] victim</c>", pushed by
/// <c>Local_Notification</c> from the MSG_INFO notification path, and faded them after
/// <c>hud_panel_notify_time</c>. This port keeps the ring + fade and renders, per line, the colored
/// attacker name, the weapon/death icon, then the colored victim name.
///
/// Pipeline: the notification/net layer calls <see cref="Push"/> with attacker/victim names (which may
/// carry <c>^N</c> color codes) and an icon name, or <see cref="PushDeath"/> with the resolved MSG_INFO
/// death <see cref="Notification"/> (its <see cref="Notification.Icon"/> is the QC kill-notify icon). The
/// icon is resolved to a texture via <see cref="TextureCache"/> (kill-notify art under
/// <c>art/gfx/hud/luma/&lt;icon&gt;</c>); when it is missing we fall back to a "»" separator glyph.
/// </summary>
public partial class NotifyPanel : HudPanel
{
    /// <summary>How long a line stays fully opaque before fading (QC hud_panel_notify_time).</summary>
    public const float HoldTime = 6f;
    private const float FadeTime = 1f;
    private const int MaxEntries = 10;

    private sealed class Entry
    {
        public string Attacker = "";
        public string Victim = "";
        public string Icon = "";
        public double Time;
    }

    private readonly List<Entry> _entries = new();
    private double _now;

    /// <summary>
    /// Push a kill-feed line (QC HUD_Notify_Push). A null/empty <paramref name="attacker"/> denotes a
    /// non-kill death (suicide / environment); the victim is then shown alone. Names may contain ^N codes.
    /// </summary>
    public void Push(string? attacker, string victim, string icon = "")
    {
        if (string.IsNullOrEmpty(victim)) return;
        _entries.Insert(0, new Entry
        {
            Attacker = attacker ?? "",
            Victim = victim,
            Icon = icon,
            Time = _now,
        });
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        QueueRedraw();
    }

    /// <summary>
    /// Push a death using a MSG_INFO death <see cref="Notification"/> (carries the kill-notify
    /// <see cref="Notification.Icon"/>). The attacker/victim names come from the notification's s1/s2 args,
    /// supplied by the caller (the net layer resolves entcs names; they may carry ^N color codes).
    /// </summary>
    public void PushDeath(Notification death, string? attacker, string victim)
        => Push(attacker, victim, death?.Icon ?? "");

    public override void _Process(double delta)
    {
        _now += delta;
        if (_entries.Count > 0)
            _entries.RemoveAll(e => _now - e.Time >= HoldTime + FadeTime);
    }

    protected override void DrawPanel()
    {
        if (_entries.Count == 0) return;

        const float rowH = 22f;
        const int size = 16;
        float w = Size2.X;
        float y = 0f;

        foreach (Entry e in _entries)
        {
            float age = (float)(_now - e.Time);
            float alpha = age <= HoldTime ? 1f : Mathf.Clamp(1f - (age - HoldTime) / FadeTime, 0f, 1f);
            if (alpha <= 0.01f) continue;

            DrawObituaryLine(e, y, w, rowH, size, alpha);
            y += rowH;
            if (y > Size2.Y - rowH) break; // clip to panel
        }
    }

    /// <summary>
    /// Draw one obituary line right-aligned to the panel edge: [attacker] [icon] [victim]. The icon is the
    /// weapon/death texture (QC model2-style kill-notify pic), or a "»" glyph when the art is missing.
    /// Names render with their ^N color codes.
    /// </summary>
    private void DrawObituaryLine(Entry e, float y, float w, float rowH, int size, float alpha)
    {
        var baseColor = new Color(1f, 1f, 1f, alpha);
        Texture2D? icon = TextureCache.GetFirst(IconPaths(e.Icon));

        List<HudText.Run> attacker = string.IsNullOrEmpty(e.Attacker)
            ? new List<HudText.Run>()
            : HudText.Parse(e.Attacker, baseColor);
        List<HudText.Run> victim = HudText.Parse(e.Victim, baseColor);

        float iconSize = rowH - 6f;
        const string sep = "  »  ";

        // Measure the whole line so we can right-align it against the screen edge.
        float attW = RunsWidth(attacker, size);
        float vicW = RunsWidth(victim, size);
        float midW = attacker.Count == 0 ? 0f : (icon is not null ? iconSize + 8f : MeasureText(sep, size));
        float total = attW + midW + vicW;

        float cx = w - total;
        float baseY = y + (rowH - size) * 0.5f;

        cx = DrawRuns(attacker, cx, baseY, size, alpha);

        if (attacker.Count != 0)
        {
            if (icon is not null)
            {
                var at = new Vector2(cx + 4f, y + (rowH - iconSize) * 0.5f);
                DrawTextureRect(icon, new Rect2(at, new Vector2(iconSize, iconSize)), tile: false,
                    new Color(1f, 1f, 1f, alpha));
                cx += iconSize + 8f;
            }
            else
            {
                DrawText(new Vector2(cx, baseY), sep, baseColor, size);
                cx += MeasureText(sep, size);
            }
        }

        DrawRuns(victim, cx, baseY, size, alpha);
    }

    private float DrawRuns(List<HudText.Run> runs, float x, float y, int size, float alpha)
    {
        foreach (HudText.Run r in runs)
        {
            var c = r.Color; c.A = alpha;
            DrawText(new Vector2(x, y), r.Text, c, size);
            x += MeasureText(r.Text, size);
        }
        return x;
    }

    private static float RunsWidth(List<HudText.Run> runs, int size)
    {
        float total = 0f;
        foreach (HudText.Run r in runs) total += MeasureText(r.Text, size);
        return total;
    }

    /// <summary>The HUD skin whose kill-notify art is preferred (QC <c>hud_skin</c>); default-skin is the fallback.</summary>
    public static string HudSkin { get; set; } = "luma";

    /// <summary>
    /// Candidate texture paths for a kill-notify icon name (QC <c>icon</c>, e.g. "notify_death",
    /// "weaponvortex"), best-first. The bare names resolve the REAL Xonotic kill-notify art from the mounted
    /// game data via <see cref="TextureCache"/>'s VFS resolver (<c>gfx/hud/&lt;skin&gt;/&lt;icon&gt;</c>), with a
    /// <c>res://</c> project override last.
    /// </summary>
    private static string[] IconPaths(string icon)
    {
        if (string.IsNullOrEmpty(icon)) return System.Array.Empty<string>();
        return new[]
        {
            $"gfx/hud/{HudSkin}/{icon}",     // VFS: preferred skin
            $"gfx/hud/default/{icon}",       // VFS: default-skin fallback
            $"res://art/hud/notify/{icon}.png", // project art override
        };
    }
}
