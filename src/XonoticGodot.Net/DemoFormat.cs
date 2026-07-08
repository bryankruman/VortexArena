using System;
using System.Collections.Generic;
using System.IO;

namespace XonoticGodot.Net;

/// <summary>
/// The XonoticGodot demo file format (<c>.xgd</c>) — a self-contained, versioned, seekable recording of a
/// match's networked entity stream. Pure C# (no Godot dependency) so the record→write→read round-trip is
/// headless-testable, and it reuses the existing wire codecs (<see cref="BitWriter"/>/<see cref="BitReader"/>,
/// <see cref="EntityStateCodec"/>) so there is one serialization to maintain.
///
/// <para>Layout (demo-replay-and-spectator.md §5):</para>
/// <code>
/// Header   "XGDM" + formatVersion + buildParity + tickRate + map + gametype + startWallclock
///          + keyframeInterval + player roster
/// Frames   [ frameTag(1) + tick + serverTime + isKeyframe + entitySection ]*
///            keyframe → full NetEntityState set (each entity delta'd from Empty)
///            delta    → removed ids + changed entities (EntityStateCodec delta vs the previous frame)
/// Trailer  endTag(0) + durationFrames + keyframe index [frameIndex → byteOffset] + footer(trailerOffset)
/// </code>
///
/// <para>The format is ours (ADR-0011) — no DP <c>.dem</c> compatibility. Playback rejects a
/// <see cref="DemoHeader.BuildParity"/> or <see cref="DemoHeader.FormatVersion"/> mismatch rather than
/// misrendering; bump <see cref="CurrentFormatVersion"/> on any layout change.</para>
/// </summary>
public static class DemoFormat
{
    /// <summary>Current on-disk layout version. Bump on ANY change to the header/frame/trailer layout.</summary>
    public const uint CurrentFormatVersion = 1;

    /// <summary>Default keyframe cadence in frames (~2 s @ 72 Hz) when the header doesn't specify one.</summary>
    public const int DefaultKeyframeInterval = 144;

    /// <summary>Leading byte of a frame record in the frame stream.</summary>
    internal const byte FrameTag = 1;

    /// <summary>Leading byte that marks the end of the frame stream (the trailer follows).</summary>
    internal const byte EndTag = 0;
}

/// <summary>One player's entry in the demo header roster — the spectate target list + scoreboard seed (§5).</summary>
public readonly struct DemoRosterEntry
{
    public readonly int NetId;
    public readonly string Name;
    public readonly int Team;
    public readonly string Model;
    public readonly int Colormap;
    public readonly bool IsBot;

    public DemoRosterEntry(int netId, string name, int team, string model, int colormap, bool isBot)
    {
        NetId = netId;
        Name = name ?? "";
        Team = team;
        Model = model ?? "";
        Colormap = colormap;
        IsBot = isBot;
    }
}

/// <summary>The demo header facts (everything before the frame stream) plus the trailer info the reader recovers.</summary>
public sealed class DemoHeader
{
    public uint FormatVersion = DemoFormat.CurrentFormatVersion;
    public uint BuildParity;
    public float TickRate = 72f;
    public string MapName = "";
    public string GameType = "";
    public long StartWallclockUnix;
    public int KeyframeInterval = DemoFormat.DefaultKeyframeInterval;
    public IReadOnlyList<DemoRosterEntry> Roster = Array.Empty<DemoRosterEntry>();

    /// <summary>Total frames recorded (read from the trailer; 0 on a freshly-built header before writing).</summary>
    public int DurationFrames;

    /// <summary>The keyframe seek index (read from the trailer): frame index → byte offset of that frame's tag.</summary>
    public IReadOnlyList<(int FrameIndex, int ByteOffset)> KeyframeIndex = Array.Empty<(int, int)>();
}

/// <summary>One decoded frame of a demo: its tick/time and the full entity set reconstructed at that tick.</summary>
public sealed class DemoFrame
{
    public uint Tick;
    public float ServerTime;
    public bool IsKeyframe;
    public Dictionary<int, NetEntityState> Entities = new();
}

/// <summary>
/// Streams demo frames to a writable, seekable <see cref="Stream"/>: writes the header on construction, one
/// record per <see cref="WriteFrame"/>, and the trailer/index on <see cref="Finish"/>. Keeps only the previous
/// frame's set + the keyframe index in memory (the frames themselves go straight to the stream), so a long
/// match doesn't sit in RAM. Does NOT own the stream — the caller (e.g. <see cref="XonoticGodot.Server.DemoRecorder"/>)
/// closes it after <see cref="Finish"/>.
/// </summary>
public sealed class DemoWriter
{
    private readonly Stream _stream;
    private readonly BitWriter _w = new(4096);
    private readonly int _keyframeInterval;
    private readonly Dictionary<int, NetEntityState> _prev = new();
    private readonly List<(int FrameIndex, long ByteOffset)> _keyframeIndex = new();
    private readonly List<int> _scratchIds = new();
    private int _frameIndex;
    private bool _forceNext;

    public DemoWriter(Stream stream, DemoHeader header)
    {
        _stream = stream;
        _keyframeInterval = header.KeyframeInterval > 0 ? header.KeyframeInterval : DemoFormat.DefaultKeyframeInterval;

        _w.Reset();
        _w.WriteByte('X'); _w.WriteByte('G'); _w.WriteByte('D'); _w.WriteByte('M');
        _w.WriteUShort((int)DemoFormat.CurrentFormatVersion);
        _w.WriteULong(header.BuildParity);
        _w.WriteFloat(header.TickRate);
        _w.WriteString(header.MapName);
        _w.WriteString(header.GameType);
        ulong unix = unchecked((ulong)header.StartWallclockUnix);
        _w.WriteULong((uint)(unix & 0xFFFFFFFF));
        _w.WriteULong((uint)(unix >> 32));
        _w.WriteLong(_keyframeInterval);

        IReadOnlyList<DemoRosterEntry> roster = header.Roster ?? Array.Empty<DemoRosterEntry>();
        _w.WriteUShort(roster.Count);
        for (int i = 0; i < roster.Count; i++)
        {
            DemoRosterEntry e = roster[i];
            _w.WriteUShort(e.NetId);
            _w.WriteString(e.Name);
            _w.WriteShort(e.Team);
            _w.WriteString(e.Model);
            _w.WriteUShort(e.Colormap);
            _w.WriteBool(e.IsBot);
        }
        _stream.Write(_w.WrittenSpan);
    }

    /// <summary>Frames written so far (the running duration).</summary>
    public int FrameCount => _frameIndex;

    /// <summary>Force the next <see cref="WriteFrame"/> to be a keyframe (a world reset — map/round change).</summary>
    public void ForceKeyframeNext() => _forceNext = true;

    /// <summary>Append one frame. A keyframe (cadence boundary / forced / first frame) writes the full set; a
    /// delta writes only the removed ids and the entities that changed vs the previous frame.</summary>
    public void WriteFrame(uint tick, float serverTime, IReadOnlyDictionary<int, NetEntityState> entities, bool forceKeyframe = false)
    {
        bool isKey = forceKeyframe || _forceNext || _frameIndex == 0
                     || (_keyframeInterval > 0 && _frameIndex % _keyframeInterval == 0);
        _forceNext = false;
        long offset = _stream.Position;

        _w.Reset();
        _w.WriteByte(DemoFormat.FrameTag);
        _w.WriteULong(tick);
        _w.WriteFloat(serverTime);
        _w.WriteBool(isKey);

        if (isKey)
        {
            _keyframeIndex.Add((_frameIndex, offset));
            _w.WriteUShort(entities.Count);
            foreach (KeyValuePair<int, NetEntityState> kv in entities)
            {
                _w.WriteUShort(kv.Key);
                EntityStateCodec.WriteDelta(_w, NetEntityState.Empty(kv.Key), kv.Value);
            }
        }
        else
        {
            // Removed: in the previous frame but gone now.
            _scratchIds.Clear();
            foreach (int prevId in _prev.Keys)
                if (!entities.ContainsKey(prevId)) _scratchIds.Add(prevId);
            _w.WriteUShort(_scratchIds.Count);
            for (int i = 0; i < _scratchIds.Count; i++) _w.WriteUShort(_scratchIds[i]);

            // Changed: new entities, or entities whose state differs from the previous frame.
            _scratchIds.Clear();
            foreach (KeyValuePair<int, NetEntityState> kv in entities)
            {
                if (_prev.TryGetValue(kv.Key, out NetEntityState prev))
                {
                    if (NetEntityState.Diff(prev, kv.Value) != EntityField.None) _scratchIds.Add(kv.Key);
                }
                else _scratchIds.Add(kv.Key);
            }
            _w.WriteUShort(_scratchIds.Count);
            for (int i = 0; i < _scratchIds.Count; i++)
            {
                int id = _scratchIds[i];
                NetEntityState baseline = _prev.TryGetValue(id, out NetEntityState p) ? p : NetEntityState.Empty(id);
                _w.WriteUShort(id);
                EntityStateCodec.WriteDelta(_w, baseline, entities[id]);
            }
        }

        _stream.Write(_w.WrittenSpan);

        _prev.Clear();
        foreach (KeyValuePair<int, NetEntityState> kv in entities) _prev[kv.Key] = kv.Value;
        _frameIndex++;
    }

    /// <summary>Write the end-of-frames marker, the trailer (duration + keyframe index) and the footer (the
    /// trailer offset, for O(1) seek-to-trailer). Flushes the stream. Call once.</summary>
    public void Finish()
    {
        long trailerStart = _stream.Position;
        _w.Reset();
        _w.WriteByte(DemoFormat.EndTag);
        _w.WriteLong(_frameIndex);
        _w.WriteLong(_keyframeIndex.Count);
        for (int i = 0; i < _keyframeIndex.Count; i++)
        {
            _w.WriteLong(_keyframeIndex[i].FrameIndex);
            _w.WriteLong((int)_keyframeIndex[i].ByteOffset);
        }
        _w.WriteLong((int)trailerStart); // footer: where the trailer begins
        _stream.Write(_w.WrittenSpan);
        _stream.Flush();
    }
}

/// <summary>
/// Reads a demo written by <see cref="DemoWriter"/>: parses the header and reconstructs every frame's full
/// entity set by replaying keyframes + deltas forward. v1 loads the whole demo into memory (fine for tests and
/// short clips); the seekable, streaming reconstruction the replay host needs is layered on top of the same
/// on-disk layout (the keyframe index in the trailer).
/// </summary>
public sealed class DemoReader
{
    public DemoHeader Header { get; private init; } = new();
    public IReadOnlyList<DemoFrame> Frames { get; private init; } = Array.Empty<DemoFrame>();

    public static DemoReader Open(Stream stream)
    {
        byte[] data = ReadFully(stream);
        var r = new BitReader(data);
        DemoHeader header = ReadHeader(ref r);

        var frames = new List<DemoFrame>();
        var running = new Dictionary<int, NetEntityState>();
        while (true)
        {
            int tag = r.ReadByte();
            if (r.BadRead) break;
            if (tag == DemoFormat.EndTag)
            {
                header.DurationFrames = r.ReadLong();
                int kfCount = r.ReadLong();
                var idx = new List<(int, int)>(kfCount < 0 ? 0 : kfCount);
                for (int i = 0; i < kfCount && !r.BadRead; i++)
                {
                    int fi = r.ReadLong();
                    int off = r.ReadLong();
                    idx.Add((fi, off));
                }
                header.KeyframeIndex = idx;
                break;
            }

            uint tick = r.ReadULong();
            float st = r.ReadFloat();
            bool isKey = r.ReadBool();
            if (isKey)
            {
                running.Clear();
                int count = r.ReadUShort();
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadUShort();
                    NetEntityState s = EntityStateCodec.ReadDelta(ref r, NetEntityState.Empty(id));
                    s.EntNum = id;
                    running[id] = s;
                }
            }
            else
            {
                int rem = r.ReadUShort();
                for (int i = 0; i < rem; i++) running.Remove(r.ReadUShort());
                int chg = r.ReadUShort();
                for (int i = 0; i < chg; i++)
                {
                    int id = r.ReadUShort();
                    NetEntityState baseline = running.TryGetValue(id, out NetEntityState pv) ? pv : NetEntityState.Empty(id);
                    NetEntityState s = EntityStateCodec.ReadDelta(ref r, baseline);
                    s.EntNum = id;
                    running[id] = s;
                }
            }

            frames.Add(new DemoFrame
            {
                Tick = tick,
                ServerTime = st,
                IsKeyframe = isKey,
                Entities = new Dictionary<int, NetEntityState>(running),
            });
            if (r.BadRead) break;
        }

        return new DemoReader { Header = header, Frames = frames };
    }

    private static DemoHeader ReadHeader(ref BitReader r)
    {
        if (r.ReadByte() != 'X' || r.ReadByte() != 'G' || r.ReadByte() != 'D' || r.ReadByte() != 'M')
            throw new InvalidDataException("not an XGD demo (bad magic)");

        var h = new DemoHeader { FormatVersion = (uint)r.ReadUShort() };
        if (h.FormatVersion != DemoFormat.CurrentFormatVersion)
            throw new InvalidDataException($"unsupported demo format version {h.FormatVersion} (expected {DemoFormat.CurrentFormatVersion})");

        h.BuildParity = r.ReadULong();
        h.TickRate = r.ReadFloat();
        h.MapName = r.ReadString();
        h.GameType = r.ReadString();
        uint lo = r.ReadULong();
        uint hi = r.ReadULong();
        h.StartWallclockUnix = unchecked((long)(((ulong)hi << 32) | lo));
        h.KeyframeInterval = r.ReadLong();

        int rc = r.ReadUShort();
        var roster = new List<DemoRosterEntry>(rc);
        for (int i = 0; i < rc && !r.BadRead; i++)
        {
            int id = r.ReadUShort();
            string name = r.ReadString();
            int team = r.ReadShort();
            string model = r.ReadString();
            int cm = r.ReadUShort();
            bool bot = r.ReadBool();
            roster.Add(new DemoRosterEntry(id, name, team, model, cm, bot));
        }
        h.Roster = roster;
        return h;
    }

    private static byte[] ReadFully(Stream stream)
    {
        if (stream is MemoryStream ms)
            return ms.ToArray();
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.Begin);
        using var tmp = new MemoryStream();
        stream.CopyTo(tmp);
        return tmp.ToArray();
    }
}
