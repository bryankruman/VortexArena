using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Items-time panel — port of Base/.../qcsrc/common/mutators/mutator/itemstime/itemstime.qc, the CSQC
/// <c>HUD_ItemsTime</c> draw (HUD panel #22). The QC version shows respawn countdowns for the "timed" pickups
/// — Mega/Big Health, Mega/Big Armor, the powerups (Strength/Shield/...), and a Superweapons slot — laying
/// out one icon + remaining-seconds per item in a grid. Each item's absolute respawn time arrives over the
/// <c>itemstime</c> net message into <c>ItemsTime_time[]</c>; a negative encoded value means "another copy is
/// available right now". The number is colored red &lt;5s, yellow &lt;10s, white otherwise (QC
/// <c>DrawItemsTimeItem</c>), and a blink/checkmark marks spawned items.
///
/// The respawn times are server-driven, so the net layer pushes them here via <see cref="SetItemTime"/> /
/// <see cref="SetItemTimes"/>, keyed by item name (the analogue of the QC item registry id). The panel reads
/// the sim clock (<see cref="Api.Clock"/>) for the countdown and draws the icon swatch + seconds per entry,
/// hiding already-spawned items when <see cref="HideSpawned"/> is set (QC
/// <c>hud_panel_itemstime_hidespawned</c>).
/// </summary>
public partial class ItemsTimePanel : HudPanel
{
    /// <summary>One timed item (QC the per-id <c>ItemsTime_time</c> entry plus its icon/color).</summary>
    private readonly struct Item
    {
        public readonly string Name;   // display label, e.g. "MEGA"
        public readonly Color Color;   // swatch tint (stands in for the QC skin icon)
        public Item(string name, Color color) { Name = name; Color = color; }
    }

    // The timed items shown, in display order (QC Item_ItemsTime_Allow set: mega/big health+armor + powerups).
    private static readonly Dictionary<string, Item> Catalog = new()
    {
        ["health_mega"]  = new Item("MH", new Color(0.3f, 0.4f, 1f, 1f)),
        ["health_big"]   = new Item("H+", new Color(0.3f, 0.6f, 1f, 1f)),
        ["armor_mega"]   = new Item("MA", new Color(1f, 0.3f, 0.3f, 1f)),
        ["armor_big"]    = new Item("A+", new Color(1f, 0.5f, 0.2f, 1f)),
        ["strength"]     = new Item("STR", new Color(0.7f, 0.3f, 1f, 1f)),
        ["shield"]       = new Item("SHD", new Color(1f, 0.85f, 0.2f, 1f)),
        ["superweapons"] = new Item("SW", new Color(1f, 0.5f, 0.1f, 1f)),
    };

    // QC ItemsTime_time[]: absolute respawn time per item (seconds, sim clock). A value < -1 (the QC negative
    // encoding) means another copy is available now; -1 means "not on this map" (hidden).
    private readonly Dictionary<string, float> _times = new();

    /// <summary>QC <c>hud_panel_itemstime_hidespawned</c>: don't list items that are currently spawned.</summary>
    public bool HideSpawned { get; set; }

    /// <summary>Slave the countdown to this clock; &lt; 0 uses the sim clock (else its own ticker).</summary>
    public double Now { get; set; } = -1.0;

    private double _localClock;

    public override void _Process(double delta)
    {
        _localClock += delta;
        QueueRedraw();
    }

    private float CurrentTime()
    {
        if (Now >= 0.0) return (float)Now;
        if (Api.Services is not null) return Api.Clock.Time;
        return (float)_localClock;
    }

    /// <summary>
    /// Push one item's absolute respawn time (QC <c>ItemsTime_time[id] = f</c>), keyed by item name. A value
    /// &lt; -1 marks "available now" (another copy up); -1 hides the item.
    /// </summary>
    public void SetItemTime(string itemName, float absoluteTime)
    {
        if (string.IsNullOrEmpty(itemName)) return;
        _times[itemName] = absoluteTime;
        QueueRedraw();
    }

    /// <summary>Replace all item respawn times at once (QC: a fresh ItemsTime sync).</summary>
    public void SetItemTimes(IEnumerable<KeyValuePair<string, float>>? times)
    {
        _times.Clear();
        if (times is not null)
            foreach (var kv in times) _times[kv.Key] = kv.Value;
        QueueRedraw();
    }

    /// <summary>Clear all item times (e.g. on map reset).</summary>
    public void Clear()
    {
        _times.Clear();
        QueueRedraw();
    }

    protected override void DrawPanel()
    {
        float now = CurrentTime();

        // Gather the items to draw this frame (QC counts then lays out a grid). We keep insertion order from
        // the catalog so the layout is stable.
        var rows = new List<(Item item, float remaining, bool available)>();
        foreach (var kv in Catalog)
        {
            if (!_times.TryGetValue(kv.Key, out float t)) continue;
            if (t == -1f) continue; // not on this map (QC: time == -1)

            bool available;
            float respawnAt = t;
            if (t < -1f) { available = true; respawnAt = -t; } // QC negative encoding: available now
            else available = t <= now;

            float remaining = respawnAt - now;
            if (HideSpawned && (available || remaining <= 0f)) continue;

            rows.Add((kv.Value, remaining, available));
        }

        if (rows.Count == 0) return;

        DrawBackground();

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float rowH = (Size2.Y - pad * 2f) / rows.Count;
        int size = (int)Mathf.Clamp(rowH * 0.7f, 11f, 22f);
        float iconW = rowH; // square icon swatch on the left (QC drew the skin icon at mySize_y square)

        for (int i = 0; i < rows.Count; i++)
        {
            var (item, remaining, available) = rows[i];
            float top = pad + i * rowH;

            // Icon swatch (stands in for the QC skinned item icon). Blink it while spawned (QC blink(0.85,0.15,5)).
            float iconAlpha = available ? 0.5f + 0.35f * Mathf.Abs(Mathf.Sin(now * Mathf.Pi * 5f)) : 1f;
            DrawRect(new Rect2(pad, top + rowH * 0.1f, iconW * 0.85f, rowH * 0.8f),
                new Color(item.Color.R, item.Color.G, item.Color.B, item.Color.A * iconAlpha));
            DrawTextCentered(new Vector2(pad, top + (rowH - size * 0.7f) * 0.5f), iconW * 0.85f,
                item.Name, new Color(1f, 1f, 1f, 0.9f), Mathf.Max(8, size - 6));

            // Countdown number (QC: floor(item_time - time + 0.999), colored by how close it is).
            if (available || remaining <= 0f)
            {
                // Spawned: a checkmark stand-in.
                DrawTextRight(pad + innerW, top + (rowH - size) * 0.5f, innerW - iconW,
                    "ready", new Color(0.4f, 1f, 0.4f, 0.9f), size);
            }
            else
            {
                int secs = Mathf.FloorToInt(remaining + 0.999f);
                Color c = secs < 5 ? new Color(0.7f, 0f, 0f, 1f)
                        : secs < 10 ? new Color(0.7f, 0.7f, 0f, 1f)
                        : FgColor;
                DrawTextRight(pad + innerW, top + (rowH - size) * 0.5f, innerW - iconW,
                    secs.ToString(), c, size);
            }
        }
    }
}
