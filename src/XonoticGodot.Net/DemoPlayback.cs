using System;
using System.Collections.Generic;
using System.IO;

namespace XonoticGodot.Net;

/// <summary>
/// The replay-side entity source: when <see cref="XonoticGodot.Game.Net.ServerNet.ReplaySource"/> is set, the
/// server's <c>BuildEntitySet</c> reads the recorded set from this instead of scanning the live world. So a replay
/// is "a listen server whose entities come from a demo" (demo-replay-and-spectator.md §2,6) — the client, HUD and
/// menus stay agnostic.
/// </summary>
public interface IReplayEntitySource
{
    /// <summary>Total demo length in seconds.</summary>
    float Duration { get; }

    /// <summary>The current playhead position in demo seconds — the two-clock model's <c>t_demo</c>
    /// (demo-replay-and-spectator.md §3), decoupled from the real-time sim/snapshot clock.</summary>
    float DemoTime { get; }

    /// <summary>Playback rate: 0 = paused, 1 = normal, 0.25/0.5 = slow-mo, 2/4 = fast-forward, negative = smooth
    /// rewind. Set by the time-control UI; the host applies it via <see cref="Advance"/>.</summary>
    float Speed { get; set; }

    /// <summary>Advance the playhead by <c>realDelta × Speed</c> (clamped to <c>[0, Duration]</c>). Called once per
    /// host frame with the REAL elapsed time — so pause/slow/fast/rewind all fall out of the rate.</summary>
    void Advance(float realDelta);

    /// <summary>Jump the playhead to <paramref name="demoTime"/> (clamped). Sets the seek flag so the next sampled
    /// frame is flagged as a discontinuity (the client snaps instead of lerping across the jump).</summary>
    void SeekTo(float demoTime);

    /// <summary>Read + clear the "a seek happened" flag. The host ORs <see cref="NetEntityFlags.Teleported"/> into
    /// the injected entities for that one frame so the client snaps across the discontinuity.</summary>
    bool ConsumeSeekFlag();

    /// <summary>The recorded entity set at the current playhead. Borrowed (do not mutate).</summary>
    IReadOnlyDictionary<int, NetEntityState> SampleCurrent();

    /// <summary>The recorded entity set at an explicit demo time (clamped). Borrowed (do not mutate).</summary>
    IReadOnlyDictionary<int, NetEntityState> SampleAt(float demoTime);
}

/// <summary>
/// Loads a <c>.xgd</c> demo and samples the recorded entity set at any demo time — the playback authority
/// (demo-replay-and-spectator.md §6). v1 (T63·P1) reconstructs every frame up front via <see cref="DemoReader"/>
/// and indexes by tick, which makes forward play <i>and</i> instant seek (P2) O(1); the streaming, keyframe-index
/// seek the spec describes for very long matches is a later refinement over the same on-disk layout (the trailer
/// already carries the keyframe index). Pure C# (no Godot) so it is headless-testable.
/// </summary>
public sealed class DemoPlayback : IReplayEntitySource
{
    private static readonly Dictionary<int, NetEntityState> EmptySet = new();

    private readonly DemoReader _reader;
    private readonly float _tickRate;

    public DemoPlayback(DemoReader reader)
    {
        _reader = reader;
        _tickRate = reader.Header.TickRate > 0f ? reader.Header.TickRate : 72f;
        int n = reader.Frames.Count;
        Duration = n > 0 ? (n - 1) / _tickRate : 0f;
    }

    /// <summary>Open a demo from a seekable stream (loads + reconstructs all frames).</summary>
    public static DemoPlayback Open(Stream stream) => new(DemoReader.Open(stream));

    /// <summary>Open a demo file by OS path.</summary>
    public static DemoPlayback OpenFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return new DemoPlayback(DemoReader.Open(fs)); // DemoReader.Open reads the whole stream before returning
    }

    /// <summary>The demo header (map/gametype/tick rate/roster + parity for the gate).</summary>
    public DemoHeader Header => _reader.Header;

    /// <summary>Recorded tick rate (Hz).</summary>
    public float TickRate => _tickRate;

    /// <summary>Total recorded frames.</summary>
    public int FrameCount => _reader.Frames.Count;

    /// <inheritdoc/>
    public float Duration { get; }

    // --- playhead (T63·P2): the two-clock model's t_demo + speed. ---
    private bool _seekFlag;

    /// <inheritdoc/>
    public float DemoTime { get; private set; }

    /// <inheritdoc/>
    public float Speed { get; set; } = 1f;

    /// <inheritdoc/>
    public void Advance(float realDelta)
    {
        if (Speed == 0f) return; // paused: playhead frozen (the sim clock keeps flowing → observers fly smoothly)
        float t = DemoTime + realDelta * Speed;
        DemoTime = t < 0f ? 0f : (t > Duration ? Duration : t);
        // Smooth forward/slow/fast/rewind motion — NO seek flag; the client interpolates per-tick deltas (incl.
        // reverse). Only an explicit SeekTo (a jump) is a discontinuity the client must snap across.
    }

    /// <inheritdoc/>
    public void SeekTo(float demoTime)
    {
        DemoTime = demoTime < 0f ? 0f : (demoTime > Duration ? Duration : demoTime);
        _seekFlag = true;
    }

    /// <inheritdoc/>
    public bool ConsumeSeekFlag()
    {
        bool f = _seekFlag;
        _seekFlag = false;
        return f;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, NetEntityState> SampleCurrent() => SampleAt(DemoTime);

    /// <summary>The frame index nearest <paramref name="demoTime"/> (clamped to the recorded range).</summary>
    public int FrameIndexAt(float demoTime)
    {
        int n = _reader.Frames.Count;
        if (n == 0) return 0;
        int idx = (int)MathF.Round(demoTime * _tickRate);
        if (idx < 0) return 0;
        if (idx >= n) return n - 1;
        return idx;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, NetEntityState> SampleAt(float demoTime)
    {
        int n = _reader.Frames.Count;
        if (n == 0) return EmptySet;
        return _reader.Frames[FrameIndexAt(demoTime)].Entities;
    }
}
