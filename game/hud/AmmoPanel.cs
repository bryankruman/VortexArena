using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Ammunition readout — port of Base/.../qcsrc/client/hud/panel/ammo.qc (HUD panel #1). The QC version,
/// when <c>hud_panel_ammo_onlycurrent</c> was set, drew just the current weapon's ammo type
/// (<c>wep.ammo_type</c>, value from <c>getstati(GetAmmoStat(...))</c>); otherwise it iterated the
/// ammo resources and drew each with an icon + count, coloring the current one and dimming the rest.
///
/// The active weapon now comes from the inventory — <see cref="Inventory.CurrentWeapon"/> reads the
/// player's <see cref="Entity.ActiveWeaponId"/> (QC STAT(ACTIVEWEAPON)) — and the *current* pool is that
/// weapon's own ammo type via <see cref="WeaponHud.AmmoType"/> (the concrete weapon's <c>AmmoType</c> field,
/// QC <c>wep.ammo_type</c>). When <see cref="OnlyCurrent"/> is set we draw the single active pool large;
/// otherwise the "all pools, highlight current" list. Each amount is read live from the
/// <see cref="Player"/> via <see cref="Resources.GetResource"/>.
/// </summary>
public partial class AmmoPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    /// <summary>
    /// Optional override for the active weapon by NetName. Normally left null so the panel reads the
    /// player's equipped weapon from the inventory; the net/demo layer may set it to force a specific
    /// weapon's pool to highlight (QC switchweapon preview).
    /// </summary>
    public string? CurrentWeapon { get; set; }

    /// <summary>QC <c>hud_panel_ammo_onlycurrent</c>: show only the active weapon's pool, drawn large.</summary>
    public bool OnlyCurrent { get; set; }

    // The ammo pools shown, in display order (QC default_order_resources, ammo subset).
    private static readonly (ResourceType Res, string Label)[] Pools =
    {
        (ResourceType.Shells,  "shells"),
        (ResourceType.Bullets, "bullets"),
        (ResourceType.Rockets, "rockets"),
        (ResourceType.Cells,   "cells"),
        (ResourceType.Fuel,    "fuel"),
    };

    /// <summary>
    /// Resolve the active weapon's ammo type. Prefers the live inventory weapon
    /// (<see cref="Inventory.CurrentWeapon"/> → its <c>AmmoType</c>); if a <see cref="CurrentWeapon"/>
    /// NetName override is set, that weapon's ammo type wins. <see cref="ResourceType.None"/> when the
    /// active weapon uses no standard pool (e.g. Blaster).
    /// </summary>
    private ResourceType ActiveAmmoType()
    {
        if (Player is null) return ResourceType.None;

        // Override by NetName (net/demo-driven switch preview).
        if (!string.IsNullOrEmpty(CurrentWeapon))
        {
            Weapon? byName = Weapons.ByName(CurrentWeapon);
            if (byName is not null) return WeaponHud.AmmoType(byName);
        }

        Weapon? active = Inventory.CurrentWeapon(Player);
        return active is not null ? WeaponHud.AmmoType(active) : ResourceType.None;
    }

    protected override void DrawPanel()
    {
        if (Player is null) return;
        if (Player.GetResource(ResourceType.Health) <= 0f) return; // QC hide_ondeath

        DrawBackground();

        ResourceType current = ActiveAmmoType();

        if (OnlyCurrent)
        {
            DrawOnlyCurrent(current);
            return;
        }

        float pad = Padding;
        float rowH = (Size2.Y - pad * 2f) / Pools.Length;
        int size = (int)Mathf.Clamp(rowH * 0.75f, 11f, 22f);
        float x = pad;
        float w = Size2.X - pad * 2f;

        for (int i = 0; i < Pools.Length; i++)
        {
            (ResourceType res, string label) = Pools[i];
            float amount = Player.GetResource(res);
            bool isCurrent = res == current && current != ResourceType.None;

            // QC: dim non-current pools; the current one is full alpha and gets a backing highlight.
            float top = pad + i * rowH;
            if (isCurrent)
                DrawRect(new Rect2(x, top, w, rowH - 1f), new Color(1f, 1f, 1f, 0.12f));

            DrawText(new Vector2(x + 2f, top + (rowH - size) * 0.5f), label,
                new Color(1f, 1f, 1f, isCurrent ? 0.6f : 0.4f), Mathf.Max(9, size - 5));
            DrawTextRight(x + w - 2f, top + (rowH - size) * 0.5f, w,
                Mathf.RoundToInt(amount).ToString(), PoolColor(amount, isCurrent), size);
        }
    }

    /// <summary>
    /// QC <c>hud_panel_ammo_onlycurrent</c> layout: the active weapon's pool only, big number centered with
    /// a small label, colored by how low it is. Weapons with no pool (Blaster) show "∞".
    /// </summary>
    private void DrawOnlyCurrent(ResourceType current)
    {
        if (current == ResourceType.None)
        {
            int s = (int)Mathf.Clamp(Size2.Y * 0.5f, 16f, 48f);
            DrawTextCentered(new Vector2(0f, (Size2.Y - s) * 0.5f), Size2.X, "∞",
                new Color(1f, 1f, 1f, 0.7f), s);
            return;
        }

        float amount = Player!.GetResource(current);
        float max = Resources.GetResourceLimit(Player, current);
        string label = LabelFor(current);

        int numSize = (int)Mathf.Clamp(Size2.Y * 0.55f, 18f, 56f);
        Color color = max > 0f ? NumColor(amount, max) : PoolColor(amount, true);

        DrawTextCentered(new Vector2(0f, Size2.Y * 0.10f), Size2.X,
            Mathf.RoundToInt(amount).ToString(), color, numSize);
        int labelSize = (int)Mathf.Clamp(Size2.Y * 0.22f, 10f, 18f);
        DrawTextCentered(new Vector2(0f, Size2.Y - labelSize - Padding), Size2.X, label,
            new Color(1f, 1f, 1f, 0.55f), labelSize);
    }

    private static Color PoolColor(float amount, bool isCurrent)
    {
        if (amount <= 0f && !isCurrent) return new Color(0.5f, 0.5f, 0.5f, 0.4f);
        if (isCurrent)                  return new Color(0.2f, 0.95f, 0.2f, 0.95f);
        if (amount < 10f)               return new Color(0.85f, 0.15f, 0.1f, 0.9f);
        return FgColor;
    }

    private static string LabelFor(ResourceType res) => res switch
    {
        ResourceType.Shells  => "shells",
        ResourceType.Bullets => "bullets",
        ResourceType.Rockets => "rockets",
        ResourceType.Cells   => "cells",
        ResourceType.Fuel    => "fuel",
        _ => "",
    };
}
