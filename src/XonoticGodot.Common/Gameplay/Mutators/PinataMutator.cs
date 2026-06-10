// Port of common/mutators/mutator/pinata/sv_pinata.qc.

using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Piñata mutator — port of <c>sv_pinata.qc</c>. When a player dies they scatter every OTHER weapon
/// they were carrying as real loot pickups (the HELD weapon drops via the normal death path,
/// <c>SpawnThrownWeapon</c> in the kill pipeline), each launched with the QC impulse
/// <c>randomvec()*175 + '0 0 325'</c> from <c>CENTER_OR_VIEWOFS</c>. Enabled by <c>g_pinata</c>
/// (mutators.cfg:557 default 0) and inert under instagib/overkill (QC
/// <c>!MUTATOR_IS_ENABLED(mutator_instagib) &amp;&amp; !MUTATOR_IS_ENABLED(ok)</c>).
/// </summary>
[Mutator]
public sealed class PinataMutator : MutatorBase
{
    /// <summary>QC <c>autocvar_g_pinata_offhand</c> (mutators.cfg:558 default 0): also run the throw loop for
    /// off-hand slots &gt; 0. The port drives a single weapon slot, so this is read but has no effect (QC's
    /// <c>if (slot &gt; 0 &amp;&amp; !offhand) break</c> never trips with one slot) — kept for cvar parity.</summary>
    public bool Offhand;

    public PinataMutator() => NetName = "pinata";

    // QC: REGISTER_MUTATOR(pinata, expr_evaluate(g_pinata) && !MUTATOR_IS_ENABLED(mutator_instagib) && !MUTATOR_IS_ENABLED(ok)).
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_pinata") != 0f
        && !OtherEnabled("instagib")
        && !OtherEnabled("overkill");

    // QC MUTATOR_IS_ENABLED reads the other mutator's enable predicate (not its added state, so activation
    // order between the three can't race).
    private static bool OtherEnabled(string netName)
        => Mutators.ByName(netName) is { } m && m.IsEnabled;

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

    // MUTATOR_HOOKFUNCTION(pinata, PlayerDies) — sv_pinata.qc:7-30.
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (Api.Services is null) return false;
        Entity target = args.Target;
        var slot = new WeaponSlot(0);

        Weapon? held = Inventory.CurrentWeapon(target);
        // QC CENTER_OR_VIEWOFS(frag_target): a player isn't IS_DEAD yet at the PlayerDies hook, so this is
        // origin + view_ofs (the eye), not the bbox center.
        Vector3 org = target.Origin + target.ViewOfs;

        // FOREACH(Weapons, owned && != held && throwable): the held one drops via the normal death path.
        foreach (Weapon it in target.OwnedWeaponSet.Weapons())
        {
            if (held is not null && ReferenceEquals(it, held))
                continue;
            if (!WeaponThrowing.IsWeaponThrowable(target, it))
                continue;
            WeaponThrowing.ThrowNewWeapon(target, it, doreduce: false, org,
                Prandom.Vec() * 175f + new Vector3(0f, 0f, 325f), slot);
        }
        return true; // QC hookfunction returns true (does not stop the chain)
    }
}
