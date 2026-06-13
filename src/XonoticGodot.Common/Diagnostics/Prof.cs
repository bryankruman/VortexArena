using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace XonoticGodot.Common.Diagnostics;

/// <summary>
/// A tiny, allocation-free subsystem timing primitive shared by every layer (Common/Engine/Server AND the Godot
/// game host). It exists so code that can't see the Godot-side <c>FrameProfiler</c> — the simulation loop, the
/// netcode, the physics — can still contribute named timing scopes to the per-frame profile. <c>FrameProfiler</c>
/// is the <em>collector</em>: each frame it flips <see cref="Enabled"/> from the <c>cl_frameprofiler</c> cvar and
/// drains the accumulators via <see cref="SnapshotAndReset"/> for display + hitch attribution.
///
/// <para>Usage at a call site: <c>using (Prof.Sample("sim.move")) { ... }</c>. When <see cref="Enabled"/> is false
/// (a real dedicated server, or the profiler turned off) <see cref="Sample"/> returns a no-op <c>default</c> token
/// that never reads the clock — so the instrumentation is free in production. When <see cref="Enabled"/> is false
/// nothing ever touches the shared dictionaries (and never takes the gate), so the production path is byte-for-byte
/// the original no-op. When profiling IS on, the accumulators are guarded by <see cref="_gate"/>: normally scopes
/// run on a single frame/tick thread (the listen server simulates on the Godot main thread), but the S5
/// <c>sv_threaded</c> path runs the sim scopes (sim.move, bot.think, …) on the dedicated worker thread WHILE the
/// main thread records its own scopes — measuring S5 needs the profiler on while threaded, so the drain/accumulate
/// must be thread-safe. The lock is uncontended (and negligible) in the common single-threaded profiling case.</para>
/// </summary>
public static class Prof
{
    /// <summary>Set by the collector (FrameProfiler) each frame from <c>cl_frameprofiler</c>. False ⇒ scopes are free no-ops.</summary>
    public static bool Enabled;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly object _gate = new();                         // guards the three accumulators (only taken when Enabled)
    private static readonly Dictionary<string, double> _bucket = new();   // accumulated ms per scope, this frame
    private static readonly Dictionary<string, double> _alloc = new();     // accumulated bytes allocated per scope, this frame
    private static readonly Dictionary<string, double> _counters = new(); // per-frame numeric markers (e.g. tick count)
    private static long _frameStart;                                      // stamp at the first _Process of the frame

    /// <summary>
    /// Stamp the start of the process step. Called by a fence node that runs FIRST each frame (lowest
    /// process priority); paired with <see cref="FrameProcessMs"/> read by the collector that runs LAST, it
    /// measures the wall-clock span across ALL nodes' <c>_Process</c> — a true per-frame total to compare
    /// against the sum of scopes (Godot's TIME_PROCESS monitor is smoothed, so it can't be differenced per frame).
    /// </summary>
    public static void MarkFrameStart()
    {
        if (Enabled)
            _frameStart = Stopwatch.GetTimestamp();
    }

    /// <summary>Per-frame total <c>_Process</c> span in ms (first node's _Process → this call). 0 when disabled.</summary>
    public static double FrameProcessMs() => Enabled ? (Stopwatch.GetTimestamp() - _frameStart) * MsPerTick : 0.0;

    /// <summary>A timing scope; accumulates its elapsed time AND bytes-allocated into the named buckets on
    /// Dispose. <c>default</c> = no-op. The alloc delta uses the cheap thread-local
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> counter, so a scope reports both "how long" and "how
    /// much garbage" — the attribution a GC-stutter hunt needs.</summary>
    public readonly struct ScopeToken : System.IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        private readonly long _startAlloc;
        internal ScopeToken(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
            _startAlloc = GC.GetAllocatedBytesForCurrentThread();
        }
        public void Dispose()
        {
            if (_name is null)
                return;
            // Compute outside the lock so the lock cost is NOT charged to this scope's measured time.
            double ms = (Stopwatch.GetTimestamp() - _start) * MsPerTick;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - _startAlloc;
            lock (_gate)
            {
                _bucket.TryGetValue(_name, out double cur);
                _bucket[_name] = cur + ms;
                _alloc.TryGetValue(_name, out double a);
                _alloc[_name] = a + bytes;
            }
        }
    }

    /// <summary>Open a named timing scope (no-op + zero cost when <see cref="Enabled"/> is false).</summary>
    public static ScopeToken Sample(string name) => Enabled ? new ScopeToken(name) : default;

    /// <summary>
    /// Record a per-frame numeric marker shown in the hitch log (latest value wins, reset each frame) — e.g. the
    /// number of catch-up sim ticks that ran, so a <c>server.tick</c> spike can be read as "ran 3 ticks" vs
    /// "one expensive tick". No-op when disabled.
    /// </summary>
    public static void Mark(string name, double value)
    {
        if (Enabled)
            lock (_gate)
                _counters[name] = value;
    }

    // ---- one-shot events (hitch forensics) --------------------------------------------------------------------
    // Markers (above) are per-frame NUMBERS; events are timestamped one-shot STRINGS for the things that
    // correlate with hitches but happen between frames or rarely: "player model built", "input backlog trimmed",
    // "sim backlog dropped", "MultiMesh capacity grew". The collector drains them each frame and stamps them with
    // the frame id, so a hitch dump can say "this 40 ms frame is the one where the model build landed". Bounded
    // ring (drops oldest on overflow), thread-safe (the sim worker + streamer pool threads raise events too).

    private const int MaxPendingEvents = 64;
    private static readonly Queue<string> _events = new();

    /// <summary>Raise a one-shot forensic event (e.g. <c>"stream: built player model X"</c>). Cheap no-op when
    /// the profiler is off; bounded, so an event storm can't grow memory. Callable from any thread.</summary>
    public static void Event(string label)
    {
        if (!Enabled)
            return;
        lock (_gate)
        {
            if (_events.Count >= MaxPendingEvents)
                _events.Dequeue();
            _events.Enqueue(label);
        }
    }

    /// <summary>Collector hook: drain the pending one-shot events into <paramref name="into"/> (appended).</summary>
    public static void DrainEvents(List<string> into)
    {
        if (_events.Count == 0)
            return; // racy read is fine: a just-raised event is picked up next frame
        lock (_gate)
        {
            while (_events.Count > 0)
                into.Add(_events.Dequeue());
        }
    }

    /// <summary>
    /// Collector hook: copy this frame's accumulated scopes + markers into the supplied dictionaries and clear the
    /// internal accumulators for the next frame. Called once per frame by FrameProfiler. Capacity is retained, so
    /// the steady state allocates nothing.
    /// </summary>
    public static void SnapshotAndReset(Dictionary<string, double> scopesInto, Dictionary<string, double> countersInto,
        Dictionary<string, double>? allocInto = null)
    {
        // Drain atomically vs. the scope accumulators: under S5 sv_threaded the worker thread may be writing a
        // sim scope while the main-thread collector drains here. Uncontended (negligible) single-threaded.
        lock (_gate)
        {
            scopesInto.Clear();
            foreach (KeyValuePair<string, double> kv in _bucket)
                scopesInto[kv.Key] = kv.Value;
            _bucket.Clear();

            countersInto.Clear();
            foreach (KeyValuePair<string, double> kv in _counters)
                countersInto[kv.Key] = kv.Value;
            _counters.Clear();

            // Per-scope allocation (bytes), for the GC-stutter attribution. Always drained so it can't accumulate
            // across frames; copied out only when the caller asked for it (FrameProfiler's 2-arg call ignores it).
            if (allocInto is not null)
            {
                allocInto.Clear();
                foreach (KeyValuePair<string, double> kv in _alloc)
                    allocInto[kv.Key] = kv.Value;
            }
            _alloc.Clear();
        }
    }
}
