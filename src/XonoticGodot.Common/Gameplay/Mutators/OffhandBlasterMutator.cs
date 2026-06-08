using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Offhand Blaster mutator — port of common/mutators/mutator/offhand_blaster/sv_offhand_blaster.qc.
/// Gives every player a blaster they can fire "offhand" (without switching away from their current
/// weapon). Enabled by the <c>g_offhand_blaster</c> cvar.
///
/// Ported: on spawn the player's offhand slot is set to the blaster (PlayerSpawn), and the offhand-fire
/// path runs each frame (PlayerPreThink) — when the offhand fire button is held it fires a Blaster primary
/// shot from the player without switching weapons, gated by the blaster's refire. The input layer flags the
/// press via <see cref="Entity.OffhandFirePressed"/> (or calls <see cref="FireOffhand"/> directly from a bind).
/// </summary>
[Mutator]
public sealed class OffhandBlasterMutator : MutatorBase
{
    public OffhandBlasterMutator() => NetName = "offhand_blaster";

    // QC: expr_evaluate(autocvar_g_offhand_blaster) (string cvar defaulting to "0").
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_offhand_blaster") != 0f;

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerPreThinkArgs>? _onPreThink;

    public override void Hook()
    {
        _onSpawn ??= OnPlayerSpawn;
        _onPreThink ??= OnPlayerPreThink;
        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerPreThink.Add(_onPreThink);
    }

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onPreThink is not null) MutatorHooks.PlayerPreThink.Remove(_onPreThink);
    }

    // MUTATOR_HOOKFUNCTION(offhand_blaster, PlayerSpawn) — player.offhand = OFFHAND_BLASTER;
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        args.Player.OffhandWeapon = "blaster";
        return false;
    }

    // OFFHAND_BLASTER.offhand_think: when the offhand button is held, fire the blaster independently of the
    // currently-selected weapon. Driven each frame from PlayerPreThink (the headless analogue of the offhand
    // think the offhand framework runs).
    private bool OnPlayerPreThink(ref MutatorHooks.PlayerPreThinkArgs args)
    {
        Entity player = args.Player;
        if ((player.Flags & EntFlags.Client) == 0 || player.DeadState != DeadFlag.No) return false;
        if (player.OffhandWeapon != "blaster") return false;
        if (player.OffhandFirePressed)
            FireOffhand(player);
        return false;
    }

    /// <summary>
    /// Fire the offhand blaster once (respecting its refire gate) without switching the player's active
    /// weapon — the C# successor to W_Blaster's offhand fire mode. Exposed so a +offhand bind can call it.
    /// </summary>
    public void FireOffhand(Entity player)
    {
        if (Api.Services is null) return;
        float now = Api.Clock.Time;
        if (now < player.OffhandNextThink) return;

        if (Weapons.ByName("blaster") is not Blaster blaster) return;

        // Fire a Blaster primary shot from the player; the offhand uses a dedicated slot so it never
        // disturbs the held weapon's state. (QC W_Blaster_Attack with the offhand laser parameters.)
        blaster.WrThink(player, new WeaponSlot(MutatorConstants.MaxWeaponSlots), FireMode.Primary);

        float refire = blaster.Primary.Refire > 0f ? blaster.Primary.Refire : 0.7f;
        player.OffhandNextThink = now + refire;
    }
}
