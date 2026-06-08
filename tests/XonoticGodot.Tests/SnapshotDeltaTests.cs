using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the snapshot delta-compression spine: the per-entity change-mask codec (<see cref="EntityStateCodec"/>)
/// and the server/client baseline-ack history (<see cref="ServerSnapshotHistory"/> /
/// <see cref="ClientSnapshotHistory"/>) that only sends spawned/changed/removed entities, plus movevar replication.
/// </summary>
public class SnapshotDeltaTests
{
    private static NetEntityState Player(int id, Vector3 origin, int health) => new()
    {
        EntNum = id, Kind = NetEntityKind.Player, ModelIndex = 7, Origin = origin, Health = health,
    };

    [Fact]
    public void EntityCodec_RoundTrips_A_Spawn_From_Empty()
    {
        var w = new BitWriter();
        NetEntityState cur = Player(10, new Vector3(64, 0, 16), 100);
        EntityStateCodec.WriteDelta(w, NetEntityState.Empty(10), cur);

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, NetEntityState.Empty(10));
        Assert.False(r.BadRead);
        Assert.Equal(NetEntityKind.Player, got.Kind);
        Assert.Equal(7, got.ModelIndex);
        Assert.Equal(100, got.Health);
        Assert.True((got.Origin - cur.Origin).Length() < 0.5f, "origin round-trips within quantization");
    }

    [Fact]
    public void EntityCodec_Only_Encodes_Changed_Fields()
    {
        var baseline = Player(10, new Vector3(64, 0, 16), 100);
        var moved = baseline; moved.Health = 75; // only health changed

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, moved);
        Assert.Equal(EntityField.Health, mask); // exactly one field on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(75, got.Health);
        Assert.Equal(baseline.ModelIndex, got.ModelIndex); // carried from baseline
    }

    [Fact]
    public void Snapshot_RoundTrip_Carries_Unchanged_And_Applies_Changes()
    {
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();

        // frame 1: two entities, full snapshot (no baseline yet).
        var f1 = new Dictionary<int, NetEntityState>
        {
            [10] = Player(10, new Vector3(0, 0, 0), 100),
            [20] = Player(20, new Vector3(128, 0, 0), 50),
        };
        w.Reset(); server.EncodeSnapshot(w, f1, 1);
        var r = new BitReader(w.WrittenSpan);
        var d1 = client.DecodeSnapshot(ref r);
        Assert.NotNull(d1);
        Assert.Equal(2, d1!.Count);
        Assert.Equal(1, (int)client.LastDecodedSeq);

        // client acks → server can now delta against frame 1.
        server.Ack(client.LastDecodedSeq);

        // frame 2: entity 10 moves, 20 unchanged. Only 10 should be on the wire.
        var f2 = new Dictionary<int, NetEntityState>
        {
            [10] = Player(10, new Vector3(72, 0, 0), 100),
            [20] = f1[20], // identical
        };
        w.Reset(); server.EncodeSnapshot(w, f2, 2);
        int deltaBytes = w.Length;

        var r2 = new BitReader(w.WrittenSpan);
        var d2 = client.DecodeSnapshot(ref r2);
        Assert.NotNull(d2);
        Assert.Equal(2, d2!.Count);                                   // 20 carried from the baseline
        Assert.True((d2[10].Origin - f2[10].Origin).Length() < 0.5f); // 10's new origin applied
        Assert.Equal(50, d2[20].Health);                             // 20 unchanged, carried correctly

        // A full re-send of both entities would be much larger; the delta is small (header + one entity).
        Assert.True(deltaBytes < 40, $"delta should be compact, was {deltaBytes} bytes");
    }

    [Fact]
    public void Snapshot_Removes_Entities_Gone_Since_Baseline()
    {
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();

        var f1 = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100), [20] = Player(20, Vector3.One, 50) };
        w.Reset(); server.EncodeSnapshot(w, f1, 1);
        var r1 = new BitReader(w.WrittenSpan); client.DecodeSnapshot(ref r1);
        server.Ack(client.LastDecodedSeq);

        // frame 2: entity 20 removed.
        var f2 = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100) };
        w.Reset(); server.EncodeSnapshot(w, f2, 2);
        var r2 = new BitReader(w.WrittenSpan);
        var d2 = client.DecodeSnapshot(ref r2);
        Assert.NotNull(d2);
        Assert.True(d2!.ContainsKey(10));
        Assert.False(d2.ContainsKey(20)); // removed
    }

    [Fact]
    public void Snapshot_Without_Ack_Stays_A_Full_Resend()
    {
        // If the client never acks (total packet loss on the C2S path), the server keeps sending full snapshots
        // (baselineSeq 0) so a freshly-connected/desynced client can always decode without prior state.
        var server = new ServerSnapshotHistory();
        var client = new ClientSnapshotHistory();
        var w = new BitWriter();
        var f = new Dictionary<int, NetEntityState> { [10] = Player(10, Vector3.Zero, 100) };

        w.Reset(); server.EncodeSnapshot(w, f, 1);
        // no Ack(): a second client connecting cold can still decode this from scratch.
        var fresh = new ClientSnapshotHistory();
        var r = new BitReader(w.WrittenSpan);
        var d = fresh.DecodeSnapshot(ref r);
        Assert.NotNull(d);
        Assert.True(d!.ContainsKey(10));
    }

    [Fact]
    public void MoveVars_RoundTrip_And_Apply_To_Client_Cvars()
    {
        var serverCvars = new CvarService();
        serverCvars.Set("sv_maxspeed", "400");      // an XPM/overkill-style override
        serverCvars.Set("sv_gravity", "800");
        serverCvars.Set("sv_jumpvelocity", "270");

        float[] vals = MoveVarsBlock.Capture(serverCvars);
        var w = new BitWriter();
        MoveVarsBlock.Serialize(w, vals);

        var r = new BitReader(w.WrittenSpan);
        float[] got = MoveVarsBlock.Deserialize(ref r);
        Assert.Equal(vals.Length, got.Length);
        Assert.Equal(MoveVarsBlock.Hash(vals), MoveVarsBlock.Hash(got));

        var clientCvars = new CvarService();
        MoveVarsBlock.Apply(clientCvars, got);
        Assert.Equal(400f, clientCvars.GetFloat("sv_maxspeed")); // the server's physics now drives client prediction
        Assert.Equal(270f, clientCvars.GetFloat("sv_jumpvelocity"));
    }

    [Fact]
    public void SequenceWraparound_Comparison_Is_Correct()
    {
        Assert.True(ServerSnapshotHistory.IsNewer(2, 1));
        Assert.True(ServerSnapshotHistory.IsNewer(1, 65535));   // wrapped: 1 is newer than 65535
        Assert.False(ServerSnapshotHistory.IsNewer(65535, 1));
    }
}
