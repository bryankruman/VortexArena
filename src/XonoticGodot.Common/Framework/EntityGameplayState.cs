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

    // =====================================================================================
    //  [W14a] csqcmodel render-only mirror fields (decoded onto the CLIENT proxy entity)
    // =====================================================================================
    // These mirror the networked anim-action + wepent block (NetEntityState) onto the proxy so the renderer
    // (PlayerModel / ViewEntityRenderer) can drive the remote torso overlay + weapon switch/transparency. They are
    // RENDER-ONLY (the client fills them from ClientEntityView each frame); the server sim never reads them. The
    // SERVER-side animdecide producer fields (AnimUpperAction/AnimActionStart) land with LI1 in a later wave.

    /// <summary>QC csqcmodel upper-body action id (0 = idle; SHOOT/MELEE/PAIN/DRAW/TAUNT/DEAD). Decoded from
    /// <c>NetEntityState.UpperAction</c>; the client plays it as a torso overlay (LI3). RESERVED — no producer yet.</summary>
    public byte UpperAction;
    /// <summary>QC the action's start time (server clock); the client derives the play phase as <c>now − this</c>.
    /// Decoded from <c>NetEntityState.AnimActionTime</c>. RESERVED — no producer yet.</summary>
    public float AnimActionTime;

    /// <summary>QC <c>.m_switchweapon</c> — the weapon RegistryId the remote player is switching TO (-1 = none).
    /// Decoded from <c>NetEntityState.SwitchWeapon</c> for the remote weapon switch render (QW5).</summary>
    public int SwitchWeapon = -1;
    /// <summary>QC <c>.m_switchingweapon</c> — the in-transition weapon being switched-to mid raise/drop (-1 = none).</summary>
    public int SwitchingWeapon = -1;
    /// <summary>QC the exterior weapon's render phase: 0 = ready, 1 = WS_RAISE, 2 = WS_DROP (drives the raise/lower tween).</summary>
    public byte WepPhase;
    /// <summary>QC the exterior weapon's render alpha (1 = opaque default; 0..1 fade; -1 = hidden). The exterior gun's
    /// transparency, networked independently of the body so Running Guns can hide the body but keep the gun visible.</summary>
    public float WepAlpha = 1f;
    /// <summary>QC the exterior weapon's <c>.skin</c> applied to the built held model.</summary>
    public byte ViewmodelSkin;
    /// <summary>QC the gun-align side (which hand/side the exterior weapon sits on).</summary>
    public byte GunAlign;
}
