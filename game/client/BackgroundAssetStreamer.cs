using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Background asset streaming pipeline (PERFORMANCE_REPORT.md §5 S1). Turns a synchronous cold asset load (a
/// 30–300 ms main-thread stall to read + parse + decode + GPU-upload) into a two-phase async job: the pure-C#
/// OFF-THREAD phase (VFS read + format parse + image decode — none of it touches Godot's RenderingServer) runs
/// on the thread pool, and the MAIN-THREAD phase (turning the parsed data into a <c>Mesh</c>/<c>Material</c>/
/// <c>Texture</c> + attaching the node) is drained from a priority queue in <see cref="_Process"/> under a small
/// millisecond budget — so even a burst of requests never spikes a frame.
///
/// <para>Requests carry a <see cref="Priority"/> (a viewmodel swap the player is waiting on = High, a distant
/// player's model = Low); the main-thread drain always builds the highest-priority ready job first. The off-
/// thread phase produces a value of type <c>T</c>; <c>onMain</c> consumes it on the main thread. A null off-
/// thread result (a missing/failed asset) is dropped silently.</para>
///
/// <para>This is the general mechanism; the felt cold-load cases are already covered eagerly (A3 precaches all
/// weapons/combat-sounds/default models, S3 idle-warms the rest), so today it is wired for the idle player-model
/// warm — moving the IQM parse off the main thread. Live cold-load callers (a renderer swapping a placeholder
/// for the real model 1–3 frames later) can adopt it incrementally.</para>
/// </summary>
public partial class BackgroundAssetStreamer : Node
{
    public enum Priority { High = 0, Low = 1 }

    /// <summary>Main-thread build budget per frame (ms). One ready job always runs even if it overshoots, so a
    /// single heavy build can't deadlock the queue; the loop then stops until next frame.</summary>
    [Export] public double BudgetMs { get; set; } = 2.0;

    private readonly object _lock = new();
    private readonly List<(Priority Prio, long Seq, Action Build)> _ready = new(); // off-thread done, awaiting main build
    private long _seq;
    private int _inFlight; // off-thread phases still running (diagnostics)

    /// <summary>
    /// Queue an asset for streaming: run <paramref name="offThread"/> on the thread pool, then hand its result to
    /// <paramref name="onMain"/> on the main thread within the per-frame budget. <paramref name="offThread"/> must
    /// be pure C# (no Godot RenderingServer/scene-tree calls); a null result is dropped.
    /// </summary>
    public void Request<T>(Func<T?> offThread, Action<T> onMain, Priority priority = Priority.Low) where T : class
    {
        Interlocked.Increment(ref _inFlight);
        long seq = Interlocked.Increment(ref _seq);
        Task.Run(() =>
        {
            T? result = null;
            try { result = offThread(); }
            catch (Exception ex) { GD.PrintErr($"[Streamer] off-thread phase failed: {ex.Message}"); }

            if (result is not null)
            {
                lock (_lock)
                    _ready.Add((priority, seq, () => onMain(result)));
            }
            Interlocked.Decrement(ref _inFlight);
        });
    }

    public override void _Process(double delta)
    {
        // Fast path: nothing ready and nothing in flight → idle.
        lock (_lock)
        {
            if (_ready.Count == 0)
                return;
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            Action? build = null;
            lock (_lock)
            {
                if (_ready.Count > 0)
                {
                    // Highest priority (lowest enum), then FIFO by sequence. The ready list is short, so a linear
                    // scan is cheaper than maintaining a heap.
                    int best = 0;
                    for (int i = 1; i < _ready.Count; i++)
                    {
                        var a = _ready[i];
                        var b = _ready[best];
                        if (a.Prio < b.Prio || (a.Prio == b.Prio && a.Seq < b.Seq))
                            best = i;
                    }
                    build = _ready[best].Build;
                    _ready.RemoveAt(best);
                }
            }
            if (build is null)
                break;
            try { build(); }
            catch (Exception ex) { GD.PrintErr($"[Streamer] main-thread build failed: {ex.Message}"); }
            if (sw.Elapsed.TotalMilliseconds >= BudgetMs)
                break; // budget spent — finish the rest next frame
        }
    }
}
