// Port of common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Dynamic Handicap mutator — port of common/mutators/mutator/dynamic_handicap/sv_dynamic_handicap.qc.
/// Auto-balances the match by giving stronger players (above the mean score) a damage penalty and weaker
/// players a buff, recomputed whenever scores or the roster change. Enabled by <c>g_dynamic_handicap</c>
/// (when handicap isn't otherwise disabled).
///
/// FULLY PORTED: <c>DynamicHandicap_UpdateHandicap</c> — compute the mean SP_SCORE across players, then for
/// each player derive <c>handicap = |(score - mean) * scale| ^ exponent</c>, sign it by whether the player is
/// below the mean, fold it to a multiplier (<c>handicap &gt;= 0 → handicap + 1</c>; else
/// <c>1 / (|handicap| + 1)</c>), clamp to [min, max], and apply it. The application side already exists in the
/// port (DamageSystem reads <see cref="Entity.HandicapTake"/>/<see cref="Entity.HandicapGive"/>); QC's
/// <c>Handicap_SetForcedHandicap(it, h, take?)</c> maps to setting both, so a stronger player both deals less
/// and takes more. Scores are read via <see cref="GameScores"/> (SP_SCORE).
///
/// Recompute triggers: QC fires on ClientDisconnect / PutClientInServer / MakePlayerObserver /
/// AddedPlayerScore(SP_SCORE). The port re-runs on PlayerSpawn (≈ PutClientInServer) and PlayerDies (a kill
/// is a death, so frag scoring is covered). The other three Base triggers have no hook chain in the port yet:
/// ClientDisconnect / MakePlayerObserver (roster shrink) and AddedPlayerScore (every non-kill SP_SCORE delta —
/// caps, objective points, KH/Dom/CTF scoring). Until MutatorHooks gains those chains + live call sites, the
/// mean can stay stale after a leave/observe or a non-death score change until the next spawn/death.
/// </summary>
[Mutator]
public sealed class DynamicHandicapMutator : MutatorBase
{
    public float Scale = 1f;     // g_dynamic_handicap_scale
    public float Exponent = 1f;  // g_dynamic_handicap_exponent
    public float Min;            // g_dynamic_handicap_min
    public float Max;            // g_dynamic_handicap_max

    public DynamicHandicapMutator() => NetName = "dynamic_handicap";

    /// <summary>
    /// QC <c>HANDICAP_DISABLED()</c> (server/handicap.qh:57) == <c>IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)</c>.
    /// The port selects the gametype as a C# object (never stamps a <c>g_cts</c>/<c>g_race</c> cvar), so this
    /// must be wired from the server side to <c>GameType is Cts or Race</c> — the same provider-seam pattern as
    /// <see cref="Items.ItemPickupRules.CtsActiveProvider"/>. Null provider = false (the common non-CTS/RACE case).
    /// </summary>
    public static System.Func<bool>? HandicapDisabledProvider;

    // QC: REGISTER_MUTATOR(dynamic_handicap, autocvar_g_dynamic_handicap && !HANDICAP_DISABLED());
    // HANDICAP_DISABLED() (server/handicap.qh:57) == (IS_GAMETYPE(CTS) || IS_GAMETYPE(RACE)) — in CTS/RACE the
    // whole handicap subsystem (and so this mutator) is hard-disabled.
    public override bool IsEnabled =>
        Api.Services is not null
        && Api.Cvars.GetFloat("g_dynamic_handicap") != 0f
        && !HandicapDisabled;

    // QC HANDICAP_DISABLED(): CTS or RACE gametype. Detected via the server-wired provider (GameType is Cts/Race),
    // NOT the bare g_cts/g_race cvars — the port never sets those, so reading them would leave this guard dead.
    private static bool HandicapDisabled => HandicapDisabledProvider?.Invoke() ?? false;

    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onSpawn;
    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onDies;

    public override void Hook()
    {
        _onSpawn ??= OnPlayerSpawn;
        _onDies ??= OnPlayerDies;
        MutatorHooks.PlayerSpawn.Add(_onSpawn);
        MutatorHooks.PlayerDies.Add(_onDies);

        if (Api.Services is not null)
        {
            Scale = Api.Cvars.GetFloat("g_dynamic_handicap_scale");
            Exponent = Api.Cvars.GetFloat("g_dynamic_handicap_exponent");
            Min = Api.Cvars.GetFloat("g_dynamic_handicap_min");
            Max = Api.Cvars.GetFloat("g_dynamic_handicap_max");
        }
    }

    public override void Unhook()
    {
        if (_onSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onSpawn);
        if (_onDies is not null) MutatorHooks.PlayerDies.Remove(_onDies);
    }

    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args) { UpdateHandicap(); return false; }
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args) { UpdateHandicap(); return false; }

    // void DynamicHandicap_UpdateHandicap()
    public void UpdateHandicap()
    {
        if (Api.Services is null) return;

        float totalScore = 0f;
        int totalPlayers = 0;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            totalScore += GameScores.Get(it, GameScores.Score);
            ++totalPlayers;
        }
        if (totalPlayers == 0) return;

        float meanScore = totalScore / totalPlayers;
        foreach (Entity it in Api.Entities.FindByClass("player"))
        {
            if (it.IsFreed || (it.Flags & EntFlags.Client) == 0) continue;
            float score = GameScores.Get(it, GameScores.Score);
            float handicap = MathF.Abs((score - meanScore) * Scale);
            handicap = MathF.Pow(handicap, Exponent);
            if (score < meanScore) handicap = -handicap;
            if (handicap >= 0f) ++handicap;
            else handicap = 1f / (MathF.Abs(handicap) + 1f);
            handicap = ClampHandicap(handicap);
            // QC Handicap_SetForcedHandicap(it, h, false/true): the give (deal-less) and take (receive-more)
            // multipliers. DamageSystem reads both (HandicapTotal).
            it.HandicapGive = handicap;
            it.HandicapTake = handicap;
        }
    }

    // float DynamicHandicap_ClampHandicap(float handicap)
    private float ClampHandicap(float handicap)
    {
        if (Min >= 0f && handicap < Min) handicap = Min;
        if (Max > 0f && handicap > Max) handicap = Max;
        return handicap;
    }
}
