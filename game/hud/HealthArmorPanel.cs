using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Health + armor readout — port of Base/.../qcsrc/client/hud/panel/healtharmor.qc (HUD panel #3). The QC
/// version read <c>STAT(HEALTH)</c>/<c>STAT(ARMOR)</c>/<c>STAT(FUEL)</c> and drew, per the
/// <c>hud_panel_healtharmor_*</c> cvars, either a combined bar or split health/armor progress bars with
/// numbers tinted by <c>HUD_Get_Num_Color</c>. We keep the split layout (two stacked bars + numbers) and
/// drop the damage-flash smoothing, oxygen bar, and combined mode.
///
/// Real data: pulled live from the local <see cref="Player"/> via the resource accessors
/// (<see cref="Resources.GetResource"/>) — exactly the values the sim maintains. Limits come from
/// <see cref="Resources.GetResourceLimit"/> (the QC <c>g_balance_*_limit</c> cvars), so the bars fill
/// against the same maxima the server clamps to.
/// </summary>
public partial class HealthArmorPanel : HudPanel
{
    /// <summary>The local player actor (set by <see cref="Hud.SetPlayer"/>).</summary>
    public Player? Player { get; set; }

    private static readonly Color HealthColor = new(0.6f, 0.95f, 0.55f, 0.85f);
    private static readonly Color ArmorColor = new(0.45f, 0.65f, 1f, 0.85f);
    private static readonly Color FuelColor = new(1f, 0.85f, 0.2f, 0.7f);

    protected override void DrawPanel()
    {
        if (Player is null) return;

        float health = Player.GetResource(ResourceType.Health);
        float armor = Player.GetResource(ResourceType.Armor);
        float fuel = Player.GetResource(ResourceType.Fuel);

        // QC: hide the panel (or zero out) when dead.
        if (health <= 0f) return;

        float maxHealth = Resources.GetResourceLimit(Player, ResourceType.Health);
        float maxArmor = Resources.GetResourceLimit(Player, ResourceType.Armor);
        float maxFuel = Resources.GetResourceLimit(Player, ResourceType.Fuel);
        if (maxHealth <= 0f) maxHealth = 200f;
        if (maxArmor <= 0f) maxArmor = 200f;
        if (maxFuel <= 0f) maxFuel = 100f;

        DrawBackground();

        float pad = Padding;
        float innerW = Size2.X - pad * 2f;
        float innerH = Size2.Y - pad * 2f;
        bool hasFuel = fuel > 0f;

        // Split the inner area: health bar on top, armor bar below; a thin fuel sliver at the bottom.
        float fuelH = hasFuel ? Mathf.Max(4f, innerH * 0.15f) : 0f;
        float barsH = innerH - fuelH;
        float barH = barsH * 0.5f;

        var healthBar = new Rect2(pad, pad, innerW, barH - 2f);
        var armorBar = new Rect2(pad, pad + barH, innerW, barH - 2f);

        DrawBar(healthBar, health / maxHealth, HealthColor);
        DrawBar(armorBar, armor / maxArmor, ArmorColor);

        // Numbers, right-aligned inside each bar (QC DrawNumIcon with the icon on the left).
        int numSize = (int)Mathf.Clamp(barH * 0.8f, 12f, 28f);
        DrawNumberInBar(healthBar, Mathf.RoundToInt(health), NumColor(health, maxHealth), numSize, "health");
        DrawNumberInBar(armorBar, Mathf.RoundToInt(armor), NumColor(armor, maxArmor), numSize, "armor");

        if (hasFuel)
        {
            var fuelBar = new Rect2(pad, pad + barsH + 1f, innerW, fuelH - 2f);
            DrawBar(fuelBar, fuel / maxFuel, FuelColor);
        }
    }

    private void DrawNumberInBar(Rect2 bar, int value, Color color, int size, string label)
    {
        // Vertically center the text within the bar; both helpers add the baseline offset themselves.
        float topY = bar.Position.Y + (bar.Size.Y - size) * 0.5f;
        // value right-aligned, small label left-aligned (stand-in for the QC skin icon).
        DrawTextRight(bar.Position.X + bar.Size.X - 4f, topY, bar.Size.X, value.ToString(), color, size);
        DrawText(new Vector2(bar.Position.X + 4f, topY), label, new Color(1f, 1f, 1f, 0.5f), Mathf.Max(10, size - 6));
    }
}
