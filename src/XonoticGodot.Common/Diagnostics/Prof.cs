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
/// that never reads the clock — so the instrumentation is free in production. Single-threaded by contract: scopes
/// run on the frame/tick thread (the listen server simulates on the Godot main thread); the dedicated-server path
/// leaves <see cref="Enabled"/> false so nothing touches the shared dictionaries.</para>
/// </summary>
public static class Prof
{
    /// <summary>Set by the collector (FrameProfiler) each frame from <c>cl_frameprofiler</c>. False ⇒ scopes are free no-ops.</summary>
    public static bool Enabled;

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly Dictionary<string, double> _bucket = new();   // accumulated ms per scope, this frame
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

    /// <summary>A timing scope; accumulates its elapsed time into the named bucket on Dispose. <c>default</c> = no-op.</summary>
    public readonly struct ScopeToken : System.IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        internal ScopeToken(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_name is null)
                return;
            double ms = (Stopwatch.GetTimestamp() - _start) * MsPerTick;
            _bucket.TryGetValue(_name, out double cur);
            _bucket[_name] = cur + ms;
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
            _counters[name] = value;
    }

    /// <summary>
    /// Collector hook: copy this frame's accumulated scopes + markers into the supplied dictionaries and clear the
    /// internal accumulators for the next frame. Called once per frame by FrameProfiler. Capacity is retained, so
    /// the steady state allocates nothing.
    /// </summary>
    public static void SnapshotAndReset(Dictionary<string, double> scopesInto, Dictionary<string, double> countersInto)
    {
        scopesInto.Clear();
        foreach (KeyValuePair<string, double> kv in _bucket)
            scopesInto[kv.Key] = kv.Value;
        _bucket.Clear();

        countersInto.Clear();
        foreach (KeyValuePair<string, double> kv in _counters)
            countersInto[kv.Key] = kv.Value;
        _counters.Clear();
    }
}
