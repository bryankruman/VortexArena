using System;
using System.Collections.Generic;
using System.IO;

namespace XonoticGodot.Net;

/// <summary>
/// The <see cref="IDemoSink"/> backend that <see cref="XonoticGodot.Server.DemoControl"/>'s
/// <c>StartRecording</c>/<c>StopRecording</c> hooks wire to (via <see cref="XonoticGodot.Game.Net.ServerNet"/>) —
/// the "host wires to the engine recorder" TODO that file documents. Owns the open <see cref="Stream"/> and a
/// <see cref="DemoWriter"/>, forwards each tick's entity set, and finalizes the <c>.xgd</c> (trailer + index) on
/// <see cref="Finish"/>. Pure .NET (file I/O only, no Godot) so it is unit-testable; the host resolves the
/// <c>user://demos/…</c> path and hands it an open <see cref="FileStream"/>.
///
/// <para>Lives in <c>XonoticGodot.Net</c> alongside <see cref="DemoFormat"/> because the server library doesn't
/// reference the net library — the demo file machinery is one self-contained unit here.</para>
/// </summary>
public sealed class DemoRecorder : IDemoSink, IDisposable
{
    private readonly Stream _stream;
    private readonly DemoWriter _writer;
    private int _tick;
    private bool _finished;

    public DemoRecorder(Stream stream, DemoHeader header)
    {
        _stream = stream;
        _writer = new DemoWriter(stream, header);
    }

    /// <summary>The on-disk path this recorder is writing to (informational; set by the host that opened the stream).</summary>
    public string? Path { get; init; }

    /// <summary>Frames recorded so far.</summary>
    public int FrameCount => _tick;

    /// <inheritdoc/>
    public void RecordTick(float serverTime, IReadOnlyDictionary<int, NetEntityState> entities)
    {
        if (_finished) return;
        _writer.WriteFrame((uint)_tick, serverTime, entities);
        _tick++;
    }

    /// <summary>Force the next recorded tick to be a keyframe (a world reset — map/round change).</summary>
    public void MarkWorldReset() => _writer.ForceKeyframeNext();

    /// <inheritdoc/>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _writer.Finish();
        _stream.Flush();
        _stream.Dispose();
    }

    public void Dispose() => Finish();
}
