using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Midair mutator — port of common/mutators/mutator/midair/sv_midair.qc. Players can only be hurt
/// while airborne: when on the ground they gain a brief damage shield, and airborne hits are scaled by a
/// damage multiplier. Enabled by the <c>g_midair</c> cvar.
///
/// Ported: the on-ground shield window (PlayerPowerups, gated on the match being live) with the
/// fullbright/additive ground glow, and the airborne damage/force scaling (Damage_Calculate). QC also
/// zeroes a bot's bot_moveskill on spawn to stop it bunnyhopping (so it stays huntable in the air); that
/// is a bot-AI tuning field owned by the bot layer, not a gameplay-state field, so it is applied there.
/// </summary>
[Mutator]
public sealed class MidairMutator : MutatorBase
{
    /// <summary>QC autocvar_g_midair_shieldtime — seconds of post-landing damage immunity.</summary>
    public float ShieldTime = 0.5f;

    /// <summary>QC autocvar_g_midair_damagemultiplier — multiplier for airborne hits (0 = unchanged in QC default cfg).</summary>
    public float DamageMultiplier = 1f;

    /// <summary>QC autocvar_g_midair_damageforcescale — optional knockback multiplier (0 = leave force alone).</summary>
    public float DamageForceScale;

    public MidairMutator() => NetName = "midair";

    // QC: expr_evaluate(autocvar_g_midair).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_midair") != 0f;

    private HookHandler<MutatorHooks.DamageCalculateArgs>? _onDamageCalc;
    private HookHandler<MutatorHooks.PlayerPowerupsArgs>? _onPlayerPowerups;

    public override void Hook()
    {
        _onDamageCalc ??= OnDamageCalculate;
        _onPlayerPowerups ??= OnPlayerPowerups;

        MutatorHooks.DamageCalculate.Add(_onDamageCalc);
        MutatorHooks.PlayerPowerups.Add(_onPlayerPowerups);

        if (Api.Services is not null)
        {
            float st = Api.Cvars.GetFloat("g_midair_shieldtime");
            if (st != 0f) ShieldTime = st;
            float dm = Api.Cvars.GetFloat("g_midair_damagemultiplier");
            if (dm != 0f) DamageMultiplier = dm;
            DamageForceScale = Api.Cvars.GetFloat("g_midair_damageforcescale");
        }
    }

    public override void Unhook()
    {
        if (_onDamageCalc is not null) MutatorHooks.DamageCalculate.Remove(_onDamageCalc);
        if (_onPlayerPowerups is not null) MutatorHooks.PlayerPowerups.Remove(_onPlayerPowerups);
    }

    private static bool IsPlayer(Entity? e) => e is not null && (e.Flags & EntFlags.Client) != 0;

    // MUTATOR_HOOKFUNCTION(midair, Damage_Calculate)
    private bool OnDamageCalculate(ref MutatorHooks.DamageCalculateArgs args)
    {
        if (IsPlayer(args.Attacker) && IsPlayer(args.Target))
        {
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            if (now < args.Target.MidairShieldTime)
                args.Damage = 0f;            // grounded recently → shielded
            else
                args.Damage *= DamageMultiplier;

            if (DamageForceScale != 0f)
                args.Force *= DamageForceScale;
        }
        return false;
    }

    // MUTATOR_HOOKFUNCTION(midair, PlayerPowerups) — refresh the shield while on the ground.
    private bool OnPlayerPowerups(ref MutatorHooks.PlayerPowerupsArgs args)
    {
        Entity player = args.Player;
        // QC gate: if (time >= game_starttime) — the on-ground shield only applies once the match clock has
        // started (so warmup landings don't bank immunity). GameStopped is the available "not live" signal.
        if (!VehicleCommon.GameStopped && player.OnGround)
        {
            // QC: player.effects |= (EF_ADDITIVE | EF_FULLBRIGHT);
            player.Effects |= EffectFlags.Additive | EffectFlags.FullBright;
            float now = Api.Services is not null ? Api.Clock.Time : 0f;
            float target = now + ShieldTime;
            if (target > player.MidairShieldTime)
                player.MidairShieldTime = target;
        }
        return false;
    }
}
