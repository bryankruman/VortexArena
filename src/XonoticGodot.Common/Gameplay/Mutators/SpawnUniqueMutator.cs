// Port of common/mutators/mutator/spawn_unique/sv_spawn_unique.qc

using System.Runtime.CompilerServices;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Unique Spawn mutator — port of common/mutators/mutator/spawn_unique/sv_spawn_unique.qc. Stops a player
/// from respawning on the exact spawnpoint they last used: while scoring spawn spots, the player's previous
/// spawnpoint gets an extremely low (but still selectable) priority, so they only land on it again when nothing
/// else is available. Enabled by the <c>g_spawn_unique</c> cvar.
///
/// Ported faithfully: the Spawn_Score hook (demote the repeat spot to priority 0.1) and the PlayerSpawn hook
/// (record the spot the player just spawned on). QC kept <c>.su_last_point</c> on the player edict; adding an
/// Entity field is out of this task's edit scope, so the per-player last-spot is held in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the player entity (GC-safe; the entry drops when the
/// player is collected) — the same per-entity mutator-state idea the stale_move_negation / vampirehook ports use.
/// </summary>
[Mutator]
public sealed class SpawnUniqueMutator : MutatorBase
{
    public SpawnUniqueMutator() => NetName = "spawn_unique";

    // QC: REGISTER_MUTATOR(spawn_unique, expr_evaluate(autocvar_g_spawn_unique)).
    public override bool IsEnabled =>
        Api.Services is not null && ExprEvaluate(Api.Cvars.GetString("g_spawn_unique"));

    // Per-player last spawnpoint (QC .entity su_last_point). Box the entity so it can be a CWT value.
    private static readonly ConditionalWeakTable<Entity, StrongRef> _lastPoint = new();

    private sealed class StrongRef { public Entity? Value; }

    private HookHandler<MutatorHooks.SpawnScoreArgs>? _onSpawnScore;
    private HookHandler<MutatorHooks.PlayerSpawnArgs>? _onPlayerSpawn;

    public override void Hook()
    {
        _onSpawnScore ??= OnSpawnScore;
        _onPlayerSpawn ??= OnPlayerSpawn;
        MutatorHooks.SpawnScore.Add(_onSpawnScore);
        MutatorHooks.PlayerSpawn.Add(_onPlayerSpawn);
    }

    public override void Unhook()
    {
        if (_onSpawnScore is not null) MutatorHooks.SpawnScore.Remove(_onSpawnScore);
        if (_onPlayerSpawn is not null) MutatorHooks.PlayerSpawn.Remove(_onPlayerSpawn);
    }

    // MUTATOR_HOOKFUNCTION(spawn_unique, Spawn_Score)
    private bool OnSpawnScore(ref MutatorHooks.SpawnScoreArgs args)
    {
        // QC: if(spawn_spot == player.su_last_point) spawn_score.x = 0.1;
        if (_lastPoint.TryGetValue(args.Player, out StrongRef? r) && ReferenceEquals(r.Value, args.Spot))
            args.Priority = 0.1f; // extremely low priority but still selectable
        return false;
    }

    // MUTATOR_HOOKFUNCTION(spawn_unique, PlayerSpawn)
    private bool OnPlayerSpawn(ref MutatorHooks.PlayerSpawnArgs args)
    {
        // QC: player.su_last_point = spawn_spot;
        StrongRef r = _lastPoint.GetValue(args.Player, static _ => new StrongRef());
        r.Value = args.Spot;
        return false;
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
