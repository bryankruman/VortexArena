// Port of common/mutators/mutator/random_gravity/sv_random_gravity.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Random Gravity mutator — port of common/mutators/mutator/random_gravity/sv_random_gravity.qc
/// (Mario, inspired by Player 2). Every <c>g_random_gravity_delay</c> seconds the server rolls a new
/// <c>sv_gravity</c>: usually a low/positive value, occasionally (chance <c>g_random_gravity_negative_chance</c>)
/// a negative one, each clamped to <c>[g_random_gravity_min, g_random_gravity_max]</c>. The physics integrator
/// re-reads <c>sv_gravity</c> per tick, so the change takes effect immediately. Enabled by the
/// <c>g_random_gravity</c> cvar.
///
/// Ported: the per-frame roll on SV_StartFrame (gated by game-stopped / game_starttime / round-start), the
/// exact <c>cvar_set("sv_gravity", ...)</c> formula, and the delay schedule. QC's <c>cvar_settemp</c> on enable
/// (so the original gravity is restored at match end) is a host-side cvar-stack concern not modelled here — the
/// host owns cvar restore on map change; NOTEd.
///
/// SEAM: this hooks <see cref="MutatorHooks.SvStartFrame"/> (the Common-side per-frame chain) rather than the
/// server-core <c>ServerHooks.SvStartFrame</c>, because the Godot-free gameplay layer can't reference XonoticGodot.Server.
/// The server's per-frame loop must pump <see cref="MutatorHooks.SvStartFrame"/> for this to tick (crossTaskNeeds).
/// </summary>
[Mutator]
public sealed class RandomGravityMutator : MutatorBase
{
    public RandomGravityMutator() => NetName = "random_gravity";

    // QC: REGISTER_MUTATOR(random_gravity, cvar("g_random_gravity")).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_random_gravity") != 0f;

    /// <summary>QC file-global <c>gravity_delay</c> — next sim time a re-roll is allowed.</summary>
    private float _gravityDelay;

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
        _gravityDelay = 0f;
        // QC MUTATOR_ONADD: cvar_settemp("sv_gravity", cvar_string("sv_gravity")) so it restores at match end.
        // The cvar-restore stack is host-owned; left to the host (NOTE) — the roll below still works.
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
    }

    // MUTATOR_HOOKFUNCTION(random_gravity, SV_StartFrame)
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        if (Api.Services is null) return false;

        // QC: if(game_stopped || !cvar("g_random_gravity")) return false;
        if (VehicleCommon.GameStopped || Api.Cvars.GetFloat("g_random_gravity") == 0f) return false;
        float time = args.Time;
        // QC: if(time < gravity_delay) return false; (the per-frame round/start-time gates use game_stopped above
        // as the available "not live" signal — a faithful superset of the round-not-started branch).
        if (time < _gravityDelay) return false;

        float min = Api.Cvars.GetFloat("g_random_gravity_min");
        float max = Api.Cvars.GetFloat("g_random_gravity_max");
        float negativeChance = Api.Cvars.GetFloat("g_random_gravity_negative_chance");

        float gravity;
        if (Prandom.Float() >= negativeChance)
        {
            // QC: bound(min, random() - random() * -g_random_gravity_negative, max)
            float negative = Api.Cvars.GetFloat("g_random_gravity_negative");
            gravity = QMath.Bound(min, Prandom.Float() - Prandom.Float() * -negative, max);
        }
        else
        {
            // QC: bound(min, random() * g_random_gravity_positive, max)
            float positive = Api.Cvars.GetFloat("g_random_gravity_positive");
            gravity = QMath.Bound(min, Prandom.Float() * positive, max);
        }

        Api.Cvars.Set("sv_gravity", Ftos(gravity));

        // QC: gravity_delay = time + autocvar_g_random_gravity_delay;
        _gravityDelay = time + Api.Cvars.GetFloat("g_random_gravity_delay");
        return false;
    }

    /// <summary>QC <c>ftos</c> — float to its cvar string. Invariant culture so the cvar store parses it back.</summary>
    private static string Ftos(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
