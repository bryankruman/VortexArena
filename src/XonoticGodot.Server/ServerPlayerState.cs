using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Server;

/// <summary>
/// Server-only per-player bookkeeping that QuakeC kept as flat fields on the client edict but which has no
/// home on the (sealed, Common-side) <see cref="Player"/> — the regen/rot pause timers, the drown air
/// timer, the contents-damage cadence, and the weapon refire gate. Held in a side table
/// (<see cref="ServerPlayerStates"/>) keyed by <see cref="Player"/> so the server core can read/write it
/// without modifying Common. Mirrors the QC <c>.pauseregen_finished</c> / <c>.pauserothealth_finished</c> /
/// <c>STAT(AIR_FINISHED)</c> / <c>.pain_finished</c> / <c>.contents_damagetime</c> / <c>.watersound_finished</c>
/// fields (server/client.qc, server/main.qc).
/// </summary>
public sealed class ServerPlayerState
{
    // ---- regen / rot pause timers (QC client.qc; damage pauses regen for a few seconds) ----
    public float PauseRegenFinished;     // QC .pauseregen_finished (health+armor regen)
    public float PauseRotHealthFinished; // QC .pauserothealth_finished
    public float PauseRotArmorFinished;  // QC .pauserotarmor_finished
    public float PauseRegenFuelFinished; // QC (shared pauseregen) — fuel regen gate
    public float PauseRotFuelFinished;   // QC .pauserotfuel_finished

    // ---- drowning (QC STAT(AIR_FINISHED) + .pain_finished gate) ----
    public float AirFinished;            // QC STAT(AIR_FINISHED): time the player runs out of air (0 = breathing)
    public float PainFinished;           // QC .pain_finished: next time drown/pain damage may tick

    // ---- contents (water/lava/slime) damage cadence (QC main.qc CreatureFrame) ----
    public float ContentsDamageTime;     // QC .contents_damagetime
    public float WaterSoundFinished;     // QC .watersound_finished
    public bool InWater;                 // QC FL_INWATER tracked per-player here

    // ---- fall-damage bookkeeping (QC .oldvelocity captured each CreatureFrame) ----
    public System.Numerics.Vector3 OldVelocity;

    /// <summary>Reset the transient timers on (re)spawn (QC PutPlayerInServer clears these).</summary>
    public void OnSpawn()
    {
        PauseRegenFinished = 0f;
        PauseRotHealthFinished = 0f;
        PauseRotArmorFinished = 0f;
        PauseRegenFuelFinished = 0f;
        PauseRotFuelFinished = 0f;
        AirFinished = 0f;
        PainFinished = 0f;
        ContentsDamageTime = 0f;
        WaterSoundFinished = 0f;
        InWater = false;
        OldVelocity = System.Numerics.Vector3.Zero;
    }
}

/// <summary>
/// The side table mapping a live <see cref="Player"/> to its <see cref="ServerPlayerState"/>. Owned by
/// <see cref="GameWorld"/>; entries are created on demand and dropped on disconnect.
/// </summary>
public sealed class ServerPlayerStates
{
    private readonly Dictionary<Player, ServerPlayerState> _states = new();

    /// <summary>Get (creating if needed) the server-side state for a player.</summary>
    public ServerPlayerState Of(Player p)
    {
        if (!_states.TryGetValue(p, out var s))
        {
            s = new ServerPlayerState();
            _states[p] = s;
        }
        return s;
    }

    /// <summary>Drop a player's state (on disconnect).</summary>
    public void Remove(Player p) => _states.Remove(p);
}
