using System.Collections.Generic;

namespace XonoticGodot.Net;

/// <summary>
/// The per-tick recording sink the server (or the client recorder) feeds the already-assembled entity set into,
/// once per advanced tick. <see cref="XonoticGodot.Game.Net.ServerNet"/> taps this right after
/// <c>BuildEntitySet</c> (the omniscient full-state set the snapshot encoder reads); the implementation
/// (<see cref="XonoticGodot.Server.DemoRecorder"/>) writes the <c>.xgd</c> demo file.
///
/// <para>v1 records the <b>entity stream</b> — the load-bearing content the replay host injects to reproduce the
/// match (demo-replay-and-spectator.md §4–5). The captured event streams (effects/sounds/notifications) and the
/// score/movevars blocks are an additive per-frame extension consumed by the replay's time-control phase
/// (§7); they ride the format's reserved per-frame section and do not change this surface.</para>
/// </summary>
public interface IDemoSink
{
    /// <summary>Record one advanced tick: the server time and the full networked entity set at that tick. The set
    /// is borrowed (reused by the caller next tick) — the sink must serialize, not retain, it.</summary>
    void RecordTick(float serverTime, IReadOnlyDictionary<int, NetEntityState> entities);

    /// <summary>Finalize the demo (write the trailer/index and flush/close). Idempotent; safe to call at a match
    /// boundary or on shutdown.</summary>
    void Finish();
}
