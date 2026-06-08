using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Active-powerups strip — port of Base/.../qcsrc/client/hud/panel/powerups.qc (HUD panel #2). The QC
/// version built a list of active powerups (strength, shield, invisibility, speed, the buffs, …) via the
/// <c>HUD_Powerups_add</c> hook, each with a name, color, current time and lifetime, then laid them out and
/// drew a countdown bar + number per item.
///
/// Data source: the panel reads the local <see cref="Player"/>'s live powerup timers each frame —
/// the classic powerup finish-times on the entity (QC <c>.strength_finished</c>/<c>.invincible_finished</c>
/// etc.: <see cref="Entity.StrengthFinished"/> …) and the buff/effect timers in
/// <see cref="Entity.StatusEffects"/> (via <see cref="StatusEffectsCatalog"/>). The current time comes from
/// <see cref="Now"/> if set, else the sim clock (<see cref="Api.Clock"/>). The net/demo layer may still
/// override the whole set via <see cref="Set"/> when it wants to drive the display directly.
/// </summary>
public partial class PowerupsPanel : HudPanel
{
    /// <summary>One active powerup row (QC powerupItems entry: message/colormod/count/lifetime).</summary>
    public readonly struct PowerupEntry
    {
        /// <summary>Human-readable name / short label (QC <c>.message</c>).</summary>
        public readonly string Name;
        /// <summary>Seconds remaining (QC <c>.count</c>).</summary>
        public readonly float TimeLeft;
        /// <summary>Full duration, for the bar fraction (QC <c>.lifetime</c>).</summary>
        public readonly float Lifetime;
        /// <summary>Bar/text tint (QC <c>.colormod</c>).</summary>
        public readonly Color Color;

        public PowerupEntry(string name, float timeLeft, float lifetime, Color color)
        {
            Name = name;
            TimeLeft = timeLeft;
            Lifetime = lifetime;
            Color = color;
        }
    }

    /// <summary>The local player (set by <see cref="Hud"/>); powerup timers are read from it each frame.</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Current time used to compute remaining durations. If &lt; 0 (default) the panel uses the sim clock
    /// (<see cref="Api.Clock"/>) when available, else its own per-frame wall clock. The net/demo layer can
    /// slave it to the match clock.
    /// </summary>
    public double Now { get; set; } = -1.0;

    /// <summary>When true, ignore the player and only show the owner-pushed <see cref="Set"/> entries.</summary>
    public bool ManualOnly { get; set; }

    // Default lifetimes used to scale the bar when only a finish-time is known (QC autocvar fallbacks).
    private const float StrengthLifetime = 30f;
    private const float ShieldLifetime = 30f;
    private const float OtherLifetime = 30f;

    private readonly List<PowerupEntry> _manual = new();
    private readonly List<PowerupEntry> _scratch = new(); // rebuilt each draw from the player

    private double _localClock;

    /// <summary>Replace the owner-pushed powerups (QC resetPowerupItems + addPowerupItem). Forces redraw.</summary>
    public void Set(IEnumerable<PowerupEntry> items)
    {
        _manual.Clear();
        if (items != null) _manual.AddRange(items);
        QueueRedraw();
    }

    /// <summary>Clear the owner-pushed powerups (QC resetPowerupItems).</summary>
    public void Clear()
    {
        _manual.Clear();
        QueueRedraw();
    }

    public override void _Process(double delta) => _localClock += delta;

    private float CurrentTime()
    {
        if (Now >= 0.0) return (float)Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)_localClock;
    }

    /// <summary>Gather the rows to draw this frame: owner-pushed plus the player's live powerup timers.</summary>
    private List<PowerupEntry> BuildRows()
    {
        _scratch.Clear();
        _scratch.AddRange(_manual);

        if (!ManualOnly && Player is not null)
            AppendPlayerPowerups(_scratch);

        return _scratch;
    }

    private void AppendPlayerPowerups(List<PowerupEntry> rows)
    {
        float now = CurrentTime();
        Entity e = Player!;

        // Classic powerups: stored as absolute finish times on the entity (QC *_finished fields).
        AddTimer(rows, "Strength",     e.StrengthFinished,     now, StrengthLifetime, new Color(0.7f, 0.3f, 1f, 0.85f));
        AddTimer(rows, "Shield",       e.InvincibleFinished,   now, ShieldLifetime,   new Color(1f, 0.85f, 0.2f, 0.85f));
        AddTimer(rows, "Speed",        e.SpeedFinished,        now, OtherLifetime,    new Color(0.3f, 0.9f, 1f, 0.85f));
        AddTimer(rows, "Invisibility", e.InvisibilityFinished, now, OtherLifetime,    new Color(0.6f, 0.6f, 0.7f, 0.85f));
        AddTimer(rows, "Superweapons", e.SuperweaponsFinished, now, OtherLifetime,    new Color(1f, 0.5f, 0.1f, 0.85f));

        // Buffs / status effects: each carries its own expiry (QC StatusEffects). Permanent ones (expire<=0)
        // and the debuffs (frozen/burning) are skipped — the powerups panel only shows timed buffs.
        foreach (ActiveStatusEffect s in e.StatusEffects)
        {
            if (s.ExpireTime <= 0f) continue; // permanent / no countdown
            if (s.DefId < 0 || s.DefId >= StatusEffectsCatalog.Count) continue;
            StatusEffectDef def = StatusEffectsCatalog.All[s.DefId];
            if (!def.IsBuff) continue;
            float left = s.ExpireTime - now;
            if (left <= 0f) continue;
            rows.Add(new PowerupEntry(BuffLabel(def.Name), left, OtherLifetime, new Color(0.4f, 1f, 0.6f, 0.85f)));
        }
    }

    private static void AddTimer(List<PowerupEntry> rows, string name, float finished, float now,
        float lifetime, Color color)
    {
        float left = finished - now;
        if (finished > 0f && left > 0f)
            rows.Add(new PowerupEntry(name, left, lifetime, color));
    }

    /// <summary>"buff_speed" -> "Speed".</summary>
    private static string BuffLabel(string defName)
    {
        string s = defName.StartsWith("buff_") ? defName.Substring(5) : defName;
        return s.Length == 0 ? defName : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    protected override void DrawPanel()
    {
        List<PowerupEntry> items = BuildRows();
        if (items.Count == 0) return; // QC: panel draws nothing with no active powerups

        DrawBackground();

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float rowH = (Size2.Y - pad * 2f) / items.Count;
        int size = (int)Mathf.Clamp(rowH * 0.7f, 11f, 20f);

        for (int i = 0; i < items.Count; i++)
        {
            PowerupEntry it = items[i];
            float top = pad + i * rowH;
            var bar = new Rect2(pad, top, innerW, rowH - 2f);

            float frac = it.Lifetime > 0f ? it.TimeLeft / it.Lifetime : 1f;
            DrawBar(bar, frac, it.Color);

            float rowTopY = top + (rowH - size) * 0.5f;
            DrawText(new Vector2(pad + 4f, rowTopY), it.Name, new Color(1f, 1f, 1f, 0.85f), Mathf.Max(10, size - 4));
            DrawTextRight(pad + innerW - 4f, rowTopY, innerW, Mathf.CeilToInt(it.TimeLeft).ToString(),
                new Color(1f, 1f, 1f, 0.95f), size);
        }
    }
}
