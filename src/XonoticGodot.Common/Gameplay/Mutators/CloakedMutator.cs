using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Cloaked mutator — port of common/mutators/mutator/cloaked/sv_cloaked.qc. Lowers the default alpha
/// of players and their weapons so everyone is semi-transparent. Enabled by the <c>g_cloaked</c> cvar.
///
/// Core behavior ported: the SetDefaultAlpha override that sets the player/weapon default alpha from
/// <c>g_balance_cloaked_alpha</c>. (The mutators-string reporting is cosmetic and skipped.)
/// </summary>
[Mutator]
public sealed class CloakedMutator : MutatorBase
{
    /// <summary>QC autocvar_g_balance_cloaked_alpha — the default alpha applied to players/weapons.</summary>
    public float Alpha = 0.25f;

    public CloakedMutator() => NetName = "cloaked";

    // QC: expr_evaluate(cvar_string("g_cloaked")).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_cloaked") != 0f;

    private HookHandler<MutatorHooks.SetDefaultAlphaArgs>? _onSetDefaultAlpha;

    public override void Hook()
    {
        _onSetDefaultAlpha ??= OnSetDefaultAlpha;
        MutatorHooks.SetDefaultAlpha.Add(_onSetDefaultAlpha);

        if (Api.Services is not null)
        {
            float a = Api.Cvars.GetFloat("g_balance_cloaked_alpha");
            if (a != 0f) Alpha = a;
        }
    }

    public override void Unhook()
    {
        if (_onSetDefaultAlpha is not null) MutatorHooks.SetDefaultAlpha.Remove(_onSetDefaultAlpha);
    }

    // MUTATOR_HOOKFUNCTION(cloaked, SetDefaultAlpha)
    private bool OnSetDefaultAlpha(ref MutatorHooks.SetDefaultAlphaArgs args)
    {
        // QC: default_player_alpha = autocvar_g_balance_cloaked_alpha;
        //     default_weapon_alpha = default_player_alpha; return true;
        args.PlayerAlpha = Alpha;
        args.WeaponAlpha = Alpha;
        return true; // QC returns true (handled).
    }
}
