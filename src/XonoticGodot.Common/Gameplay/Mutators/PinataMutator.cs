using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Piñata mutator — port of common/mutators/mutator/pinata/sv_pinata.qc. When a player dies they
/// scatter every weapon they were carrying (except the one in hand) as throwable pickups. Enabled by the
/// <c>g_pinata</c> cvar (and only when not instagib/overkill).
///
/// The PlayerDies hook walks the owned-weapon set (<see cref="Inventory"/>/<see cref="WepSet"/>) and, for
/// every throwable weapon the victim owned that wasn't the one in hand, spawns a weapon pickup launched with
/// the QC impulse <c>randomvec()*175 + '0 0 325'</c>. The offhand toggle (<c>g_pinata_offhand</c>) mirrors
/// QC's "only throw from non-primary slots when set"; with a single active-weapon model that reduces to
/// "also throw the in-hand weapon".
/// </summary>
[Mutator]
public sealed class PinataMutator : MutatorBase
{
    /// <summary>QC autocvar_g_pinata_offhand — also throw weapons from the non-primary slots.</summary>
    public bool Offhand;

    public PinataMutator() => NetName = "pinata";

    // QC: expr_evaluate(autocvar_g_pinata) && !instagib && !ok.
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_pinata") != 0f;

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);

        if (Api.Services is not null)
            Offhand = Api.Cvars.GetFloat("g_pinata_offhand") != 0f;
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    // MUTATOR_HOOKFUNCTION(pinata, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (Api.Services is null) return false;
        Entity target = args.Target;

        // QC: slot 0 always throws; slots > 0 only when g_pinata_offhand. With one active weapon, the
        // "in-hand" weapon (the held one) is dropped only when Offhand is set, every other owned weapon
        // always drops — matching QC's "throw every owned weapon except the one held in the active slot".
        Weapon? held = Inventory.CurrentWeapon(target);

        foreach (Weapon w in Inventory.GetWeapons(target).Weapons())
        {
            bool isHeld = held is not null && w == held;
            if (isHeld && !Offhand)
                continue; // the in-hand weapon stays unless offhand throwing is enabled
            if (!IsThrowable(w))
                continue;
            ThrowWeapon(target, w);
        }
        return false;
    }

    /// <summary>
    /// QC W_IsWeaponThrowable: a weapon can be dropped if it isn't a hidden/special non-droppable. The base
    /// blaster (impulse 1) is the always-present fallback weapon and is never thrown; everything else with a
    /// world/item model is throwable.
    /// </summary>
    private static bool IsThrowable(Weapon w)
    {
        if (w.NetName == "blaster") return false;             // the base weapon is never dropped
        if ((w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0) return false;
        return true;
    }

    // W_ThrowNewWeapon(target, weapon, ..., randomvec()*175 + '0 0 325', ...) — spawn a weapon pickup.
    private static void ThrowWeapon(Entity owner, Weapon w)
    {
        Entity drop = Api.Entities.Spawn();
        drop.ClassName = "weapon_" + w.NetName;
        drop.NetName = w.NetName;
        drop.Owner = owner;

        // QC origin is CENTER_OR_VIEWOFS(target): the bbox center of the corpse.
        Vector3 org = owner.Origin + (owner.Mins + owner.Maxs) * 0.5f;
        Api.Entities.SetOrigin(drop, org);
        if (w.ItemModel is not null) Api.Entities.SetModel(drop, w.ItemModel);

        // QC launch impulse: randomvec() * 175 + '0 0 325'.
        drop.Velocity = Prandom.Vec() * 175f + new Vector3(0f, 0f, 325f);
        drop.MoveType = MoveType.Toss;
        drop.Solid = Solid.Trigger;
        drop.Flags |= EntFlags.Item;
    }
}
