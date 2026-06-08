// Port of common/mutators/mutator/vampirehook/sv_vampirehook.qc

using System.Numerics;
using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Vampire Hook mutator — port of common/mutators/mutator/vampirehook/sv_vampirehook.qc. While a player is
/// hooked to an ENEMY with the grappling hook, the hook drains the victim's health every
/// <c>g_vampirehook_damagerate</c> seconds and heals the hook's owner. (With <c>g_vampirehook_teamheal</c> a
/// teammate can be hooked to heal them, draining the owner instead.) Enabled by the <c>g_vampirehook</c> cvar.
///
/// Ported: the GrappleHookThink hook, the damage-rate gate (per-hook <c>last_dmg</c>), the team/enemy + frozen +
/// alive guards, the <c>Damage(..WEP_HOOK..)</c> drain on the chosen damage entity, and the
/// <c>Heal(...)</c> on the chosen heal target (mapped to GiveResourceWithLimit capped at the small-health max).
///
/// BLOCKER (documented partial): QC operates on <c>thehook.aiment</c> — a hooked PLAYER (the "tarzan"/reel-to-
/// victim variant). The port's grapple (Hook.cs) latches onto GEOMETRY and never sets a hooked-player aiment, so
/// in practice <see cref="Entity.Aiment"/> is null here and the drain is inert (the guards fail). The handler is
/// the faithful body for the day the hooked-player mechanic lands; modelling that reel-to-victim mechanic is a
/// Weapons-side change recorded in crossTaskNeeds. <c>hitsound_damage_dealt</c> is a HUD/stats accumulator the
/// port doesn't carry, so it's omitted (cosmetic).
/// </summary>
[Mutator]
public sealed class VampireHookMutator : MutatorBase
{
    /// <summary>QC autocvar_g_vampirehook_teamheal — hooking a teammate heals them (drains the owner).</summary>
    public bool TeamHeal;

    /// <summary>QC autocvar_g_vampirehook_damage — health drained per tick.</summary>
    public float Damage;

    /// <summary>QC autocvar_g_vampirehook_damagerate — seconds between drain ticks.</summary>
    public float DamageRate = 0.1f;

    /// <summary>QC autocvar_g_vampirehook_health_steal — health the owner gains per tick.</summary>
    public float HealthSteal;

    public VampireHookMutator() => NetName = "vampirehook";

    // QC: REGISTER_MUTATOR(vh, expr_evaluate(autocvar_g_vampirehook)) — g_vampirehook is a string.
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_vampirehook"));

    // Per-hook last_dmg debounce (QC .float last_dmg on the hook edict). Keyed by the hook entity (GC-safe).
    private static readonly ConditionalWeakTable<Entity, float[]> _lastDmg = new();

    private HookHandler<MutatorHooks.GrappleHookThinkArgs>? _onGrappleThink;

    public override void Hook()
    {
        _onGrappleThink ??= OnGrappleHookThink;
        MutatorHooks.GrappleHookThink.Add(_onGrappleThink);

        if (Api.Services is not null)
        {
            TeamHeal = Api.Cvars.GetFloat("g_vampirehook_teamheal") != 0f;
            Damage = Api.Cvars.GetFloat("g_vampirehook_damage");
            float dr = Api.Cvars.GetFloat("g_vampirehook_damagerate");
            if (dr != 0f) DamageRate = dr;
            HealthSteal = Api.Cvars.GetFloat("g_vampirehook_health_steal");
        }
    }

    public override void Unhook()
    {
        if (_onGrappleThink is not null) MutatorHooks.GrappleHookThink.Remove(_onGrappleThink);
    }

    // MUTATOR_HOOKFUNCTION(vh, GrappleHookThink)
    private bool OnGrappleHookThink(ref MutatorHooks.GrappleHookThinkArgs args)
    {
        if (Api.Services is null) return false;
        Entity thehook = args.Hook;
        float time = Api.Clock.Time;

        float[] slot = _lastDmg.GetValue(thehook, static _ => new float[1]);

        // QC: if (!autocvar_g_vampirehook_damage || thehook.last_dmg > time || time < game_starttime) return;
        if (Damage == 0f || slot[0] > time || VehicleCommon.GameStopped) return false;

        Entity? owner = thehook.RealOwner;
        Entity? aiment = thehook.Aiment; // NOTE: null with the port's geometry-latching grapple (documented partial).
        if (owner is null || aiment is null) return false;

        // QC: if (hook_owner != hook_aiment && IS_PLAYER(hook_aiment) && !STAT(FROZEN, hook_aiment)
        //         && (DIFF_TEAM(hook_owner, hook_aiment) || teamheal) && GetResource(hook_aiment, RES_HEALTH) > 0)
        bool aimentIsPlayer = (aiment.Flags & EntFlags.Client) != 0;
        bool aimentFrozen = StatusEffectsCatalog.Frozen is { } fz && StatusEffectsCatalog.Has(aiment, fz);
        bool diffTeam = !Teams.SameTeam(owner, aiment);
        if (ReferenceEquals(owner, aiment) || !aimentIsPlayer || aimentFrozen
            || !(diffTeam || TeamHeal) || aiment.GetResource(ResourceType.Health) <= 0f)
            return false;

        // QC: thehook.last_dmg = time + damagerate;
        slot[0] = time + DamageRate;

        // QC: dmgent = (SAME_TEAM(owner, aiment) && teamheal) ? hook_owner : hook_aiment;
        bool sameTeam = Teams.SameTeam(owner, aiment);
        Entity dmgent = (sameTeam && TeamHeal) ? owner : aiment;

        // QC: Damage(dmgent, thehook, hook_owner, damage, WEP_HOOK.m_id, DMG_NOWEP, thehook.origin, '0 0 0');
        Combat.Damage(dmgent, thehook, owner, Damage, DeathTypes.FromWeapon("hook"), thehook.Origin, Vector3.Zero);

        // QC: targ = SAME_TEAM(owner, aiment) ? hook_aiment : hook_owner; Heal(targ, owner, health_steal, healthsmall_max);
        Entity targ = sameTeam ? aiment : owner;
        float healthSmallMax = Cvar("g_pickup_healthsmall_max", 5f);
        targ.GiveResourceWithLimit(ResourceType.Health, HealthSteal, healthSmallMax);

        // QC: if(dmgent == hook_owner) TakeResource(dmgent, RES_HEALTH, damage); // FIXME: friendly fire?!
        if (ReferenceEquals(dmgent, owner))
            owner.TakeResource(ResourceType.Health, Damage);
        return false;
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        float v = Api.Cvars.GetFloat(name);
        return v != 0f ? v : fallback;
    }

    /// <summary>QC <c>expr_evaluate(s)</c> for a cvar string: false for "" / "0" / "false", true otherwise.</summary>
    private static bool ExprEvaluate(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s == "0" || string.Equals(s, "false", System.StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
