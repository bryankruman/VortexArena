using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T62 round-trip proof for the <c>.xgd</c> demo format (demo-replay-and-spectator.md §5,12): a synthetic
/// entity-set sequence → <see cref="DemoWriter"/>/<see cref="DemoRecorder"/> → <see cref="DemoReader"/> reproduces
/// every frame's set exactly (keyframe boundaries + deltas + add/remove), and the header round-trips. Values are
/// chosen on the wire quantization grid (integer coords = multiples of 1/8; angles 0/90) so equality is exact and
/// meaningful — the test proves the codec, not float drift.
/// </summary>
public class DemoFormatTests
{
    private static NetEntityState Player(int id, float x, float y, float z, int health, string model)
        => new()
        {
            EntNum = id,
            Kind = NetEntityKind.Player,
            Origin = new Vector3(x, y, z),
            Angles = new Vector3(0, 90, 0),
            Health = health,
            Model = model,
            ModelIndex = 3,
            Weapon = 1,
            Colormap = 17,
        };

    private static NetEntityState Item(int id, float x, float y, float z)
        => new()
        {
            EntNum = id,
            Kind = NetEntityKind.Item,
            Origin = new Vector3(x, y, z),
            Model = "models/item.iqm",
            ModelIndex = 7,
        };

    /// <summary>Build a 10-frame scenario: a moving player (1), a static item (2, never changes), and a player (3)
    /// that spawns at frame 3 and is removed at frame 7 — exercising keyframes, deltas, unchanged-skip, add, remove.</summary>
    private static List<Dictionary<int, NetEntityState>> BuildScenario()
    {
        var frames = new List<Dictionary<int, NetEntityState>>();
        for (int f = 0; f < 10; f++)
        {
            var set = new Dictionary<int, NetEntityState>
            {
                [1] = Player(1, f, 16, 24, 100 - f, "models/player/erebus.iqm"),
                [2] = Item(2, 64, 0, 0),
            };
            if (f >= 3 && f < 7)
                set[3] = Player(3, 100, f, 50, 50, "models/player/megaerebus.iqm");
            frames.Add(set);
        }
        return frames;
    }

    private static DemoHeader Header() => new()
    {
        BuildParity = 0xDEADBEEF,
        TickRate = 72f,
        MapName = "dm/bloodprison",
        GameType = "dm",
        StartWallclockUnix = 1_700_000_000,
        KeyframeInterval = 4,
        Roster = new List<DemoRosterEntry>
        {
            new(1, "Player", 5, "models/player/erebus.iqm", 17, false),
            new(3, "[BOT]Rancho", 14, "models/player/megaerebus.iqm", 0, true),
        },
    };

    [Fact]
    public void RoundTrip_ReproducesEveryFrameExactly()
    {
        List<Dictionary<int, NetEntityState>> scenario = BuildScenario();

        var ms = new MemoryStream();
        var writer = new DemoWriter(ms, Header());
        for (int f = 0; f < scenario.Count; f++)
            writer.WriteFrame((uint)f, f / 72f, scenario[f]);
        writer.Finish();

        ms.Position = 0;
        DemoReader reader = DemoReader.Open(ms);

        Assert.Equal(scenario.Count, reader.Frames.Count);
        Assert.Equal(scenario.Count, reader.Header.DurationFrames);

        for (int f = 0; f < scenario.Count; f++)
        {
            DemoFrame frame = reader.Frames[f];
            Assert.Equal((uint)f, frame.Tick);
            Assert.Equal(f / 72f, frame.ServerTime);
            Assert.Equal(f % 4 == 0, frame.IsKeyframe); // cadence 4 → keyframes at 0,4,8

            Dictionary<int, NetEntityState> expected = scenario[f];
            Assert.Equal(expected.Count, frame.Entities.Count);
            foreach (KeyValuePair<int, NetEntityState> kv in expected)
            {
                Assert.True(frame.Entities.ContainsKey(kv.Key), $"frame {f} missing entity {kv.Key}");
                Assert.Equal(kv.Value, frame.Entities[kv.Key]);
            }
        }
    }

    [Fact]
    public void Header_And_KeyframeIndex_RoundTrip()
    {
        var ms = new MemoryStream();
        var writer = new DemoWriter(ms, Header());
        List<Dictionary<int, NetEntityState>> scenario = BuildScenario();
        for (int f = 0; f < scenario.Count; f++)
            writer.WriteFrame((uint)f, f / 72f, scenario[f]);
        writer.Finish();

        ms.Position = 0;
        DemoHeader h = DemoReader.Open(ms).Header;

        Assert.Equal(DemoFormat.CurrentFormatVersion, h.FormatVersion);
        Assert.Equal(0xDEADBEEFu, h.BuildParity);
        Assert.Equal(72f, h.TickRate);
        Assert.Equal("dm/bloodprison", h.MapName);
        Assert.Equal("dm", h.GameType);
        Assert.Equal(1_700_000_000L, h.StartWallclockUnix);
        Assert.Equal(4, h.KeyframeInterval);

        Assert.Equal(2, h.Roster.Count);
        Assert.Equal("Player", h.Roster[0].Name);
        Assert.Equal(5, h.Roster[0].Team);
        Assert.False(h.Roster[0].IsBot);
        Assert.Equal("[BOT]Rancho", h.Roster[1].Name);
        Assert.True(h.Roster[1].IsBot);

        // keyframes at frames 0, 4, 8 → 3 index entries with ascending byte offsets.
        Assert.Equal(3, h.KeyframeIndex.Count);
        Assert.Equal(0, h.KeyframeIndex[0].FrameIndex);
        Assert.Equal(4, h.KeyframeIndex[1].FrameIndex);
        Assert.Equal(8, h.KeyframeIndex[2].FrameIndex);
        Assert.True(h.KeyframeIndex[0].ByteOffset < h.KeyframeIndex[1].ByteOffset);
        Assert.True(h.KeyframeIndex[1].ByteOffset < h.KeyframeIndex[2].ByteOffset);
    }

    [Fact]
    public void Recorder_WritesAndFinalizes_ToStream()
    {
        // The IDemoSink path the host uses: DemoRecorder owns the stream + tick counter + finalize.
        var ms = new NonClosingMemoryStream();
        var recorder = new DemoRecorder(ms, Header());
        List<Dictionary<int, NetEntityState>> scenario = BuildScenario();
        foreach (Dictionary<int, NetEntityState> set in scenario)
            recorder.RecordTick(0.5f, set);
        Assert.Equal(scenario.Count, recorder.FrameCount);
        recorder.Finish();        // writes trailer, flushes, disposes the stream (NonClosing keeps the buffer)

        DemoReader reader = DemoReader.Open(new MemoryStream(ms.Captured));
        Assert.Equal(scenario.Count, reader.Frames.Count);
        // last frame: entity 3 was removed at frame 7, so frames 7..9 hold only {1,2}.
        Assert.Equal(2, reader.Frames[^1].Entities.Count);
        Assert.True(reader.Frames[^1].Entities.ContainsKey(1));
        Assert.True(reader.Frames[^1].Entities.ContainsKey(2));
        Assert.False(reader.Frames[^1].Entities.ContainsKey(3));
    }

    [Fact]
    public void Playback_Playhead_AdvanceSpeedSeekClamp()
    {
        // 100 frames @ 50 Hz → 1.98 s duration; exercise the two-clock playhead (T63·P2).
        var hdr = new DemoHeader { TickRate = 50f, MapName = "m", GameType = "dm" };
        var ms = new MemoryStream();
        var writer = new DemoWriter(ms, hdr);
        for (int f = 0; f < 100; f++)
            writer.WriteFrame((uint)f, f / 50f, new Dictionary<int, NetEntityState> { [1] = Player(1, f, 0, 0, 100, "m.iqm") });
        writer.Finish();
        ms.Position = 0;
        var pb = DemoPlayback.Open(ms);

        Assert.Equal(0f, pb.DemoTime);
        Assert.Equal(1f, pb.Speed);

        // normal play: advances by realDelta × speed
        pb.Advance(0.5f);
        Assert.Equal(0.5f, pb.DemoTime, 3);

        // pause: frozen
        pb.Speed = 0f;
        pb.Advance(1.0f);
        Assert.Equal(0.5f, pb.DemoTime, 3);

        // 2× fast-forward
        pb.Speed = 2f;
        pb.Advance(0.5f); // +1.0s → 1.5
        Assert.Equal(1.5f, pb.DemoTime, 3);

        // clamp at duration
        pb.Advance(10f);
        Assert.Equal(pb.Duration, pb.DemoTime, 3);

        // smooth rewind, clamps at 0
        pb.Speed = -1f;
        pb.Advance(100f);
        Assert.Equal(0f, pb.DemoTime, 3);

        // smooth motion sets NO seek flag
        Assert.False(pb.ConsumeSeekFlag());

        // seek sets the flag (consumed once) and jumps the playhead
        pb.SeekTo(1.0f);
        Assert.Equal(1.0f, pb.DemoTime, 3);
        Assert.True(pb.ConsumeSeekFlag());
        Assert.False(pb.ConsumeSeekFlag());

        // SampleCurrent tracks the playhead (frame 50 @ 50 Hz = x:50)
        Assert.Equal(50f, pb.SampleCurrent()[1].Origin.X, 3);
    }

    [Fact]
    public void Open_RejectsBadMagic()
        => Assert.Throws<InvalidDataException>(() => DemoReader.Open(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 })));

    [Fact]
    public void Playback_SampleAt_ReturnsFrameForTime_AndClamps()
    {
        // 72 Hz header → frame f is at demo time f/72; DemoPlayback (T63·P1) samples + clamps.
        List<Dictionary<int, NetEntityState>> scenario = BuildScenario();
        var ms = new MemoryStream();
        var writer = new DemoWriter(ms, Header());
        for (int f = 0; f < scenario.Count; f++)
            writer.WriteFrame((uint)f, f / 72f, scenario[f]);
        writer.Finish();

        ms.Position = 0;
        var pb = DemoPlayback.Open(ms);

        Assert.Equal(72f, pb.TickRate);
        Assert.Equal(scenario.Count, pb.FrameCount);
        Assert.Equal((scenario.Count - 1) / 72f, pb.Duration, 3);

        for (int f = 0; f < scenario.Count; f++)
        {
            IReadOnlyDictionary<int, NetEntityState> set = pb.SampleAt(f / 72f);
            Assert.Equal(scenario[f].Count, set.Count);
            foreach (KeyValuePair<int, NetEntityState> kv in scenario[f])
                Assert.Equal(kv.Value, set[kv.Key]);
        }

        // Clamp below 0 → first frame; above duration → last frame.
        Assert.Equal(scenario[0][1], pb.SampleAt(-5f)[1]);
        Assert.Equal(2, pb.SampleAt(9999f).Count); // last frame holds only {1,2} (entity 3 removed at frame 7)
    }

    /// <summary>A MemoryStream that keeps its buffer accessible after Dispose (DemoRecorder.Finish disposes the
    /// stream it owns; the test still needs the bytes).</summary>
    private sealed class NonClosingMemoryStream : MemoryStream
    {
        public byte[] Captured { get; private set; } = System.Array.Empty<byte>();
        protected override void Dispose(bool disposing)
        {
            if (Captured.Length == 0) Captured = ToArray();
            base.Dispose(disposing);
        }
    }
}
