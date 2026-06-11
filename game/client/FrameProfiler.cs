using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Client;

/// <summary>
/// An always-available, low-overhead frame-time + GC hitch monitor — the instrument the per-second
/// <see cref="Hud.FpsPanel"/> can't be. <c>FpsPanel</c> averages over a 1-second window, which HIDES the very
/// thing that matters for smoothness: a single long frame. This node samples the real per-frame delta, the
/// managed-heap GC activity (gen-0/1/2 collection deltas + bytes allocated <em>this frame</em>), and a handful
/// of coarse subsystem timers, then flags any frame that spikes above the rolling baseline as a "hitch" and
/// logs what dominated it. That turns "it stuttered" into "it stuttered because gen-0 collected after 0.4 MB
/// of allocation during server.tick" — the attribution a profiler needs.
///
/// <para><b>How it measures.</b> A single <see cref="_Process"/> per frame diffs <see cref="GC.CollectionCount"/>
/// and <see cref="GC.GetTotalAllocatedBytes"/> against the previous frame, so the window covers exactly one
/// frame's worth of allocation regardless of node order. Subsystem timing uses the allocation-free
/// <see cref="Scope"/> struct: <c>using (FrameProfiler.Scope("server.tick")) ...</c> accumulates elapsed
/// <see cref="Stopwatch"/> ticks into a static bucket that this node snapshots + clears each frame. When the
/// profiler is off the scope is a no-op (it never even reads the clock), so the instrumentation costs nothing
/// in a release build with the cvar at 0.</para>
///
/// <para><b>Cvars</b> (both archived):
/// <list type="bullet">
///   <item><c>cl_frameprofiler</c> — 0 off, 1 on-screen graph + stats + hitch log, 2 also emits a per-frame
///         trace line at <c>developer&gt;0</c>. Like <see cref="Hud.FpsPanel"/> it defaults <b>on (1) in debug
///         builds</b> unless the player has set it, so a dev always sees the graph; a release stays clean.</item>
///   <item><c>cl_frameprofiler_hitchms</c> — absolute floor (ms) below which a frame is never called a hitch,
///         so high-refresh jitter near the baseline doesn't spam (default 12). A hitch also has to exceed
///         <see cref="HitchFactor"/>× the rolling median, so the threshold tracks the actual framerate.</item>
/// </list></para>
///
/// <para>Instantiated once from <c>Main._Ready</c> so it spans the whole session (menu + match), above every
/// other layer. The graph sits unobtrusively in the top-left.</para>
/// </summary>
public partial class FrameProfiler : CanvasLayer
{
    public static FrameProfiler? Instance { get; private set; }

    /// <summary>A frame is only a "hitch" if it exceeds this multiple of the rolling median (and the ms floor).</summary>
    private const double HitchFactor = 1.8;

    /// <summary>Frame-time ring length — a few seconds of history for the graph + the rolling median/p99.</summary>
    private const int RingSize = 240;

    // ---- subsystem scope timing -----------------------------------------------------------------------------
    // The accumulator lives in XonoticGodot.Common.Diagnostics.Prof so EVERY layer (the sim loop, netcode,
    // physics — none of which can see this Godot-side node) can contribute scopes. This node is the collector:
    // it drives Prof.Enabled from the cvar and drains Prof each frame into _lastScopes/_lastCounters (below).
    private static readonly Dictionary<string, double> _lastScopes = new();
    private static readonly Dictionary<string, double> _lastCounters = new();

    // Whole-node _Process scopes (distinct nodes ⇒ non-overlapping); summed to compute "proc:other". Nested
    // sub-scopes (server.tick, sim.*, net.*, cw.*) are intentionally NOT here so they don't double-count.
    private static readonly string[] TopLevelNodeScopes =
        { "ng.process", "cw.process", "md3.morph", "entitynode", "hud.mgr", "proj", "viewmodel", "nethud" };

    /// <summary>
    /// Open a named timing scope: <c>using (FrameProfiler.Scope("name")) { ... }</c> (or as a one-statement
    /// prefix). Forwards to <see cref="Prof.Sample"/> — a no-op <c>default</c> token when the profiler is off, so
    /// wrapping a hot path is free when disabled. Engine/Server code calls <see cref="Prof.Sample"/> directly.
    /// </summary>
    public static Prof.ScopeToken Scope(string name) => Prof.Sample(name);

    // ---- frame-time ring + GC tracking -----------------------------------------------------------------------
    private readonly double[] _ring = new double[RingSize];
    private readonly double[] _sortScratch = new double[RingSize];
    private int _ringHead;
    private int _ringCount;

    private int _lastGc0, _lastGc1, _lastGc2;
    private long _lastAllocBytes;
    private int _dGc0, _dGc1, _dGc2;   // collections that happened THIS frame
    private long _dAllocBytes;          // bytes allocated THIS frame
    private double _median, _p99;
    private double _summaryClock;   // accumulates ms for the once-per-second mode-2 breakdown
    private bool _defaultsRegistered;

    // Engine-level frame breakdown (the part the C# scopes can't see): total time in ALL _Process callbacks,
    // physics, and — usually the dominant slice on a hitch — the renderer (render-thread CPU submit + GPU).
    private double _procMs, _physMs, _renderCpuMs, _renderGpuMs;
    private Rid _viewportRid;
    private bool _measureArmed;

    private FrameProfilerGraph _view = null!;

    public override void _Ready()
    {
        Instance = this;
        Layer = 10;                    // a dev overlay above the HUD
        ProcessPriority = 1_000_000;   // run late so most scopes have closed before we snapshot the bucket

        TryRegisterDefaults();

        _lastGc0 = GC.CollectionCount(0);
        _lastGc1 = GC.CollectionCount(1);
        _lastGc2 = GC.CollectionCount(2);
        _lastAllocBytes = GC.GetTotalAllocatedBytes(false);

        _view = new FrameProfilerGraph { Name = "Graph", Visible = false };
        _view.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _view.Position = new Vector2(8f, 8f);
        _view.CustomMinimumSize = new Vector2(GraphWidth, GraphHeight + 50f);
        _view.Size = _view.CustomMinimumSize;
        _view.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_view);

        // A fence that runs FIRST each frame (lowest process priority) to stamp the start of the process step;
        // this node runs LAST (priority above), so the span between them is the true per-frame total _Process time.
        AddChild(new FrameProfilerFence { Name = "Fence", ProcessPriority = -1_000_000 });

        // One-shot environment banner: a large `rest` (idle CPU+GPU but a long frame) is usually the run
        // environment, not game code — an attached managed/Mono debugger adds main-thread sync stalls, and the
        // vsync/fps cap paces frame delivery. This prints the facts so you don't have to guess (esp. when running
        // from an IDE "Player" config, which attaches a debugger for hot-reload even on a non-breakpoint run).
        // `debug-build` = OS.IsDebugBuild() = Godot's export/run CONTEXT (always true from the editor, regardless
        // of the C# config). `csharp` = the actual managed assembly optimization (#if DEBUG), which IS what the
        // Rider Debug/Release switch controls — check this line to confirm a Release switch reached the running DLL.
#if DEBUG
        const string csharpConfig = "Debug";
#else
        const string csharpConfig = "Release(optimized)";
#endif
        Log.Info($"[frameprofiler] env: managed-debugger={System.Diagnostics.Debugger.IsAttached} " +
                 $"godot-context={(OS.IsDebugBuild() ? "debug" : "release")} csharp={csharpConfig} " +
                 $"vsync={DisplayServer.WindowGetVsyncMode()} maxfps={Godot.Engine.MaxFps}");
    }

    public override void _Process(double delta)
    {
        TryRegisterDefaults();
        int mode = Mode();
        Prof.Enabled = mode >= 1;

        // GC deltas across exactly one frame (this _Process to the previous one).
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long alloc = GC.GetTotalAllocatedBytes(false);
        _dGc0 = g0 - _lastGc0; _dGc1 = g1 - _lastGc1; _dGc2 = g2 - _lastGc2;
        _dAllocBytes = alloc - _lastAllocBytes;
        _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastAllocBytes = alloc;

        // Drain this frame's subsystem timers + markers (from any layer via Prof) for display + hitch attribution.
        Prof.SnapshotAndReset(_lastScopes, _lastCounters);

        // "Everything else": the per-frame _Process span not attributed to a named WHOLE-NODE scope ⇒ time in
        // nodes we haven't wrapped (each HUD panel, weather/radar/particles, music, gibs, …) plus Godot's
        // per-node dispatch. A big proc:other ⇒ scope more nodes; a small one with a still-large frame ⇒ the time
        // is in `rest` (present/vsync/stall), not _Process. The summed keys are distinct nodes (non-overlapping);
        // nested scopes (server.tick, sim.*, net.*, cw.*) are deliberately excluded so nothing double-counts.
        double accounted = 0.0;
        foreach (string k in TopLevelNodeScopes)
            if (_lastScopes.TryGetValue(k, out double v)) accounted += v;
        _lastScopes["proc:other"] = Math.Max(0.0, _procMs - accounted);

        // Engine-level breakdown: the Prof scopes only cover the C# logic we wrapped — the REST of a frame is the
        // other _Process nodes, physics, and (usually the big one on a hitch) rendering, none of which live in a
        // C# scope. Pull those from Godot so a hitch line accounts for the WHOLE frame: TIME_PROCESS is the sum of
        // every node's _Process; the viewport render timers split the render-thread CPU submit from the GPU (so a
        // first-draw shader/pipeline compile shows up as a GPU or render-CPU spike, not as missing time).
        // Per-frame total _Process span (fence node stamped the start; this node runs last) — a true per-frame
        // number we CAN difference against the scopes, unlike Godot's smoothed TIME_PROCESS monitor.
        _procMs = Prof.FrameProcessMs();
        _physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0; // part of `rest`
        if (!_measureArmed && GetViewport() is { } vp)
        {
            _viewportRid = vp.GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);
            _measureArmed = true;
        }
        if (_measureArmed)
        {
            _renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewportRid);
            _renderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
        }

        double ms = delta * 1000.0;
        _ring[_ringHead] = ms;
        _ringHead = (_ringHead + 1) % RingSize;
        if (_ringCount < RingSize) _ringCount++;
        ComputeStats();

        if (mode >= 1 && _ringCount > 30)
        {
            double floor = HitchFloorMs();
            if (ms > floor && ms > _median * HitchFactor)
                LogHitch(ms);
        }
        // mode 2: a once-per-second breakdown of where frame time goes (the top subsystem scopes + markers), so
        // you can read the steady-state split live without waiting for a hitch. Hitches still log regardless above.
        if (mode >= 2)
        {
            _summaryClock += ms;
            if (_summaryClock >= 1000.0)
            {
                _summaryClock = 0.0;
                // rest = the median frame time not spent in C# _Process or render submit ⇒ present/vsync wait or
                // GPU wait. Big rest + small gpu ⇒ vsync pacing (try Mailbox); big rest + big gpu ⇒ GPU-bound.
                double restMed = Math.Max(0.0, _median - _procMs - _renderCpuMs);
                EmitProfile($"[frameprofile] med {_median:0.0}ms p99 {_p99:0.0}ms | proc {_procMs:0.0} rcpu {_renderCpuMs:0.0} gpu {_renderGpuMs:0.0} rest {restMed:0.0} | {TopScopes(4)}{Markers()}");
            }
        }

        bool show = mode >= 1;
        if (_view.Visible != show)
            _view.Visible = show;
        if (show)
            _view.QueueRedraw();
    }

    private void ComputeStats()
    {
        int n = _ringCount;
        if (n == 0) { _median = _p99 = 0; return; }
        Array.Copy(_ring, _sortScratch, n);
        Array.Sort(_sortScratch, 0, n);
        _median = _sortScratch[n / 2];
        int idx = Math.Min(n - 1, (int)(n * 0.99));
        _p99 = _sortScratch[idx];
    }

    private void LogHitch(double ms)
    {
        string top = TopScopes(3);
        string gc = (_dGc0 + _dGc1 + _dGc2) > 0 ? $" | GC g0+{_dGc0} g1+{_dGc1} g2+{_dGc2}" : "";
        string scopes = top.Length > 0 ? " | top: " + top : "";
        string marks = Markers();
        // rest = wall-clock not spent in C# _Process or render-thread submit ⇒ GPU wait / vsync-present / an
        // external (driver/OS) stall. Big rest + big gpu ⇒ GPU-bound (e.g. a first-draw shader compile);
        // big rest + small gpu ⇒ present/vsync or a stall outside Godot's measured sections.
        double rest = Math.Max(0.0, ms - _procMs - _renderCpuMs);
        EmitProfile($"[hitch] {ms:0.0}ms (med {_median:0.0}) | proc {_procMs:0.0} rcpu {_renderCpuMs:0.0} gpu {_renderGpuMs:0.0} phys {_physMs:0.0} rest {rest:0.0}{gc} | alloc {_dAllocBytes / 1024}KB{scopes}{marks}");
    }

    // Mode-2 file sink: route hitch + per-second profile lines to user://frameprofile.log (in addition to the
    // console) so a windowed or exported run's profile is capturable even when stdout is buffered/detached (the
    // common case for the listen-server play phase). Flushed per line so a force-quit keeps the data.
    private static Godot.FileAccess? _logFile;
    private static bool _logOpenTried;

    private static void EmitProfile(string line)
    {
        Log.Info(line);
        if (Mode() < 2)
            return;
        if (_logFile is null && !_logOpenTried)
        {
            _logOpenTried = true;
            _logFile = Godot.FileAccess.Open("user://frameprofile.log", Godot.FileAccess.ModeFlags.Write);
        }
        if (_logFile is not null)
        {
            _logFile.StoreLine(line);
            _logFile.Flush();
        }
    }

    /// <summary>This frame's numeric markers (e.g. "ticks 3") as a compact suffix; empty when none. Hitch-only.</summary>
    private static string Markers()
    {
        if (_lastCounters.Count == 0) return "";
        string s = "";
        foreach (KeyValuePair<string, double> kv in _lastCounters)
        {
            if (s.Length > 0) s += ", ";
            s += $"{kv.Key} {kv.Value:0.##}";
        }
        return " | " + s;
    }

    /// <summary>The k highest subsystem timers this frame, as a compact string (only built on a hitch).</summary>
    private static string TopScopes(int k)
    {
        if (_lastScopes.Count == 0) return "";
        var used = new HashSet<string>();
        string s = "";
        for (int i = 0; i < k; i++)
        {
            string? best = null; double bestMs = 0;
            foreach (KeyValuePair<string, double> kv in _lastScopes)
                if (kv.Value > bestMs && !used.Contains(kv.Key)) { bestMs = kv.Value; best = kv.Key; }
            if (best is null || bestMs < 0.05) break;
            used.Add(best);
            if (s.Length > 0) s += ", ";
            s += $"{best} {bestMs:0.0}ms";
        }
        return s;
    }

    // ---- drawing data (read by FrameProfilerGraph._Draw) -----------------------------------------------------
    internal const float GraphWidth = 256f;
    internal const float GraphHeight = 56f;

    internal void DrawInto(Control c)
    {
        Font font = c.GetThemeDefaultFont();
        if (font is null)
            return;
        double budget = BudgetMs();
        double scaleMax = Math.Max(budget * 3.0, _p99 * 1.2 + 1.0); // headroom so a 3-frame hitch still fits

        // Backing panel.
        c.DrawRect(new Rect2(0f, 0f, GraphWidth, GraphHeight), new Color(0f, 0f, 0f, 0.45f));

        // Frame-time bars, oldest -> newest left-to-right (show the most recent RingSize samples).
        int n = _ringCount;
        float bw = GraphWidth / RingSize;
        for (int i = 0; i < n; i++)
        {
            // _ringHead points one past the newest; walk back so the rightmost bar is the newest sample.
            int slot = (_ringHead - n + i + RingSize * 2) % RingSize;
            double v = _ring[slot];
            float h = (float)Math.Clamp(v / scaleMax, 0.0, 1.0) * GraphHeight;
            Color col = v >= budget * 2.0 ? new Color(1f, 0.25f, 0.2f)
                : v >= budget * 1.25 ? new Color(1f, 0.8f, 0.2f)
                : new Color(0.3f, 0.9f, 0.4f);
            float x = i * bw;
            c.DrawRect(new Rect2(x, GraphHeight - h, Math.Max(1f, bw), h), col);
        }

        // The vsync/refresh budget line.
        float by = GraphHeight - (float)Math.Clamp(budget / scaleMax, 0.0, 1.0) * GraphHeight;
        c.DrawLine(new Vector2(0f, by), new Vector2(GraphWidth, by), new Color(1f, 1f, 1f, 0.5f), 1f);

        // Three stat lines under the graph: frame summary, the engine breakdown (where the WHOLE frame goes), GC.
        double cur = n > 0 ? _ring[(_ringHead - 1 + RingSize) % RingSize] : 0;
        var white = new Color(1f, 1f, 1f, 0.95f);
        c.DrawString(font, new Vector2(2f, GraphHeight + 13f),
            $"{cur,5:0.0}ms  p99 {_p99:0.0}  med {_median:0.0}  ({budget:0.0} budget)",
            HorizontalAlignment.Left, -1f, 12, white);
        // proc = all C# _Process; gpu = main viewport GPU; the rest of the frame is render-submit + present/wait.
        Color gpuCol = _renderGpuMs > budget ? new Color(1f, 0.55f, 0.3f) : white;
        c.DrawString(font, new Vector2(2f, GraphHeight + 29f),
            $"proc {_procMs:0.0}  rcpu {_renderCpuMs:0.0}  gpu {_renderGpuMs:0.0}",
            HorizontalAlignment.Left, -1f, 12, gpuCol);
        Color allocCol = _dAllocBytes > 256 * 1024 ? new Color(1f, 0.7f, 0.3f) : white;
        c.DrawString(font, new Vector2(2f, GraphHeight + 45f),
            $"GC {_lastGc0}/{_lastGc1}/{_lastGc2}   {_dAllocBytes / 1024}KB/frame",
            HorizontalAlignment.Left, -1f, 12, allocCol);
    }

    private static double BudgetMs()
    {
        double hz = DisplayServer.ScreenGetRefreshRate();
        if (hz <= 1.0) hz = 60.0;
        return 1000.0 / hz;
    }

    // ---- cvars -----------------------------------------------------------------------------------------------
    // cl_frameprofiler* lives in the SHARED CLIENT store (MenuState.Cvars) — where ClientSettings registers it,
    // the console/menu set it, and the --cvar boot override writes it. Reading Api.Cvars was a bug: on a listen
    // server Api.Cvars is the server's PRIVATE store, so `set cl_frameprofiler 2` (or --cvar) never reached the
    // profiler mid-match. (Before MenuState.Boot the store is empty → fall back to the debug default.)
    private static CvarService? ClientCvars()
    {
        CvarService cv = XonoticGodot.Game.Menu.MenuState.Cvars;
        return cv is not null && cv.Has("cl_frameprofiler") ? cv : null;
    }

    private void TryRegisterDefaults()
    {
        if (_defaultsRegistered)
            return;
        CvarService cv = XonoticGodot.Game.Menu.MenuState.Cvars;
        if (cv is null)
            return;
        RegisterDefaults(cv);
        _defaultsRegistered = true;
    }

    /// <summary>Register the <c>cl_frameprofiler*</c> defaults (archived). Idempotent; safe to call from boot.</summary>
    public static void RegisterDefaults(ICvarService cvars)
    {
        if (cvars is null) return;
        cvars.Register("cl_frameprofiler", "0", CvarFlags.Save);
        cvars.Register("cl_frameprofiler_hitchms", "12", CvarFlags.Save);
    }

    /// <summary>Effective mode: 0 off / 1 graph+hitch-log / 2 +per-frame trace. Debug-default-on like FpsPanel.</summary>
    private static int Mode()
    {
        CvarService? cv = ClientCvars();
        if (cv is null)
            return OS.IsDebugBuild() ? 1 : 0;
        int m = (int)cv.GetFloat("cl_frameprofiler");
        if (m != 0)
            return m;
        if (OS.IsDebugBuild() && !cv.IsModified("cl_frameprofiler"))
            return 1;
        return 0;
    }

    private static double HitchFloorMs()
    {
        if (Api.Services is null) return 12.0;
        float v = Api.Cvars.GetFloat("cl_frameprofiler_hitchms");
        return v > 0f ? v : 12.0;
    }
}

/// <summary>The drawing surface for <see cref="FrameProfiler"/>; defers all painting back to the owner.</summary>
public partial class FrameProfilerGraph : Control
{
    public override void _Draw()
    {
        FrameProfiler.Instance?.DrawInto(this);
    }
}

/// <summary>
/// Runs first each frame (lowest <see cref="Node.ProcessPriority"/>) to stamp the start of the process step, so
/// <see cref="FrameProfiler"/> (which runs last) can measure the true per-frame total <c>_Process</c> span.
/// </summary>
public partial class FrameProfilerFence : Node
{
    public override void _Process(double delta) => Prof.MarkFrameStart();
}
