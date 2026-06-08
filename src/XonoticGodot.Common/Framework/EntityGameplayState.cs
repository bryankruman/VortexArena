using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Common.Framework;

/// <summary>
/// Gameplay-state fields on the base entity that cross the Framework/Gameplay boundary: the weapon
/// inventory (<see cref="WepSet"/>) and active status effects. Kept here (Framework) because they are
/// <c>partial Entity</c> members; the types they use live in Gameplay.
/// </summary>
public partial class Entity
{
    // weapon inventory (QC .weapons / .switchweapon)
    public WepSet OwnedWeaponSet;
    public int ActiveWeaponId = -1;     // RegistryId of the equipped weapon, -1 = none
    public int SwitchWeaponId = -1;     // pending switch target
    public float WeaponNextAttack;      // refire gate (server time)

    // status effects (frozen/burning/buffs)
    public readonly List<ActiveStatusEffect> StatusEffects = new();
}
