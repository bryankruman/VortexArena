using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Owned-weapons strip — port of Base/.../qcsrc/client/hud/panel/weapons.qc (HUD panel #0). The QC version
/// built a weapon-priority-ordered grid of icons (<c>it.model2</c>), drawing owned weapons fully and
/// unowned ones as dim "ghost" icons, with a highlight behind the currently selected weapon and an
/// optional per-weapon accuracy tint.
///
/// Ownership now comes from the real bitset — <see cref="Entity.OwnedWeaponSet"/> (QC STAT(WEAPONS) WEPSET)
/// — and the selected weapon from <see cref="Inventory.CurrentWeapon"/> (QC STAT(ACTIVEWEAPON)). Each cell
/// draws the weapon's HUD icon via <see cref="WeaponHud.Icon"/> (QC <c>model2</c>), falling back to a
/// color-tinted box + label when the art is missing. When <see cref="SetAccuracy"/> has been fed per-weapon
/// hit fractions (a networked stat in QC), owned cells are tinted by accuracy (red→green) like
/// <c>hud_panel_weapons_accuracy</c>.
/// </summary>
public partial class WeaponsPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Optional override for the selected weapon by NetName. Normally null so the panel reads the player's
    /// equipped weapon from the inventory; the net/demo layer may set it (QC switchweapon preview).
    /// </summary>
    public string? CurrentWeapon { get; set; }

    /// <summary>When true (QC <c>hud_panel_weapons_onlyowned</c>), draw only owned weapons; else ghost the rest.</summary>
    public bool OnlyOwned { get; set; } = true;

    /// <summary>QC <c>hud_panel_weapons_accuracy</c>: tint owned cells by per-weapon accuracy when fed.</summary>
    public bool ShowAccuracy { get; set; } = true;

    // Per-weapon accuracy in [0,1], keyed by NetName (QC weapon_accuracy[] from the networked stats).
    private readonly Dictionary<string, float> _accuracy = new();

    /// <summary>
    /// Feed per-weapon accuracy fractions (0..1 by weapon NetName) from the networked accuracy stats
    /// (QC <c>weapon_accuracy</c>). Replaces the whole table; pass an empty/null map to clear.
    /// </summary>
    public void SetAccuracy(IReadOnlyDictionary<string, float>? accuracyByNetName)
    {
        _accuracy.Clear();
        if (accuracyByNetName is not null)
            foreach (var kv in accuracyByNetName) _accuracy[kv.Key] = kv.Value;
        QueueRedraw();
    }

    /// <summary>The currently selected weapon (inventory unless <see cref="CurrentWeapon"/> overrides).</summary>
    private string? SelectedNetName()
    {
        if (!string.IsNullOrEmpty(CurrentWeapon)) return CurrentWeapon;
        Weapon? active = Player is not null ? Inventory.CurrentWeapon(Player) : null;
        return active?.NetName;
    }

    protected override void DrawPanel()
    {
        if (Player is null) return;

        DrawBackground();

        var weapons = Weapons.All;
        if (weapons.Count == 0) return;

        WepSet owned = Player.OwnedWeaponSet;
        string? selected = SelectedNetName();

        // First pass: count how many cells we'll draw to size them. Skip placeholder weapons (impulse < 0).
        int count = 0;
        foreach (Weapon w in weapons)
        {
            if (w.Impulse < 0) continue;
            if (OnlyOwned && !owned.Has(w)) continue;
            count++;
        }
        if (count == 0) return;

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float innerH = Size2.Y - pad * 2f;
        float cellW = innerW / count;
        int size = (int)Mathf.Clamp(innerH * 0.45f, 10f, 20f);

        int col = 0;
        foreach (Weapon w in weapons)
        {
            if (w.Impulse < 0) continue;
            bool isOwned = owned.Has(w);
            if (OnlyOwned && !isOwned) continue;

            float cx = pad + col * cellW;
            var cell = new Rect2(cx + 1f, pad, cellW - 2f, innerH);
            bool isCurrent = w.NetName == selected;

            DrawCell(w, cell, isOwned, isCurrent, size);
            col++;
        }
    }

    private void DrawCell(Weapon w, Rect2 cell, bool owned, bool isCurrent, int size)
    {
        // Cell background: weapon color, brighter if owned, brightest if current; accuracy overrides the
        // owned tint when we have a hit fraction for this weapon (QC accuracy coloring).
        float bgAlpha = !owned ? 0.10f : isCurrent ? 0.55f : 0.28f;
        Color bg = WeaponHud.ColorOf(w, bgAlpha);
        if (owned && ShowAccuracy && _accuracy.TryGetValue(w.NetName, out float acc))
            bg = AccuracyColor(acc, isCurrent ? 0.6f : 0.4f);
        DrawRect(cell, bg);

        if (isCurrent)
            DrawRect(cell, new Color(1f, 1f, 1f, 0.9f), filled: false, width: 2f);

        // Icon (QC model2). Fits the icon into the cell preserving aspect; falls back to a label cell.
        Texture2D? icon = WeaponHud.Icon(w);
        float textAlpha = owned ? 0.95f : 0.4f;

        if (icon is not null)
        {
            Vector2 ts = icon.GetSize();
            float scale = Mathf.Min((cell.Size.X - 4f) / ts.X, (cell.Size.Y - size - 4f) / ts.Y);
            if (scale <= 0f) scale = Mathf.Min((cell.Size.X - 4f) / ts.X, (cell.Size.Y - 4f) / ts.Y);
            var draw = new Vector2(ts.X * scale, ts.Y * scale);
            var at = new Vector2(cell.Position.X + (cell.Size.X - draw.X) * 0.5f, cell.Position.Y + 2f);
            // Ghost unowned icons (dim, like the QC unavailable-weapon alpha).
            DrawTextureRect(icon, new Rect2(at, draw), tile: false,
                new Color(1f, 1f, 1f, owned ? 1f : 0.35f));
        }

        // Impulse (weapon slot number) top-left, always — the QC numeric label.
        DrawText(new Vector2(cell.Position.X + 3f, cell.Position.Y + 1f), w.Impulse.ToString(),
            new Color(1f, 1f, 1f, textAlpha), Mathf.Max(9, size - 4));

        // Short name centered at the bottom of the cell (also the whole label when there's no icon).
        string name = ShortName(w.DisplayName, w.NetName);
        DrawTextCentered(new Vector2(cell.Position.X, cell.Position.Y + cell.Size.Y - size - 1f),
            cell.Size.X, name, new Color(1f, 1f, 1f, textAlpha), size);
    }

    /// <summary>Accuracy tint (QC: red at 0%, yellow mid, green at 100%).</summary>
    private static Color AccuracyColor(float acc, float alpha)
    {
        acc = Mathf.Clamp(acc, 0f, 1f);
        Color c = acc >= 0.5f
            ? new Color(Mathf.Lerp(1f, 0.2f, (acc - 0.5f) * 2f), 1f, 0.2f)   // yellow -> green
            : new Color(1f, Mathf.Lerp(0.2f, 1f, acc * 2f), 0.2f);            // red -> yellow
        c.A = alpha;
        return c;
    }

    /// <summary>Pick a compact label for a weapon cell (display name if short, else the NetName).</summary>
    private static string ShortName(string display, string netName)
    {
        string s = string.IsNullOrEmpty(display) ? netName : display;
        return s.Length <= 10 ? s : s.Substring(0, 10);
    }
}
