using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Verifies the lag-compensation hook is actually wired into the shared hitscan path (the antilag bracket the
/// server uses to rewind other players to the shooter's view-time), and the entity-state Weapon field round-trips.
/// Touches the ambient <see cref="Api"/>/<see cref="LagComp"/> globals, so it runs in the serialized collection.
/// </summary>
[Collection("GlobalState")]
public class AntilagHookTests
{
    private sealed class RecordingLagComp : ILagCompensation
    {
        public int Begins, Ends;
        public Entity? LastShooter;
        public void Begin(Entity shooter) { Begins++; LastShooter = shooter; }
        public void End() => Ends++;
    }

    [Fact]
    public void FireBullet_Brackets_Its_Trace_With_LagComp()
    {
        Api.Services = new EngineServices(new CollisionWorld()); // empty world → the bullet trace misses at once
        var rec = new RecordingLagComp();
        LagComp.Provider = rec;
        try
        {
            Entity actor = Api.Services.Entities.Spawn();
            WeaponFiring.FireBullet(actor, new Vector3(0, 0, 0), new Vector3(1, 0, 0), 1000f, 10f, deathType: 0,
                spread: 0f, solidPenetration: 0f);

            Assert.Equal(1, rec.Begins);          // antilag_takeback ran before the trace
            Assert.Equal(1, rec.Ends);            // antilag_restore ran after (even on a clean miss)
            Assert.Same(actor, rec.LastShooter);  // rewound to THIS shooter's view
        }
        finally
        {
            LagComp.Provider = null;
        }
    }

    [Fact]
    public void FireRailgunBullet_Brackets_Its_Trace_With_LagComp()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        var rec = new RecordingLagComp();
        LagComp.Provider = rec;
        try
        {
            Entity actor = Api.Services.Entities.Spawn();
            WeaponFiring.FireRailgunBullet(actor, new Vector3(0, 0, 0), new Vector3(1000, 0, 0), 80f, deathType: 0);
            Assert.Equal(1, rec.Begins);
            Assert.Equal(1, rec.Ends);
        }
        finally
        {
            LagComp.Provider = null;
        }
    }

    [Fact]
    public void SetupShot_Brackets_Its_Trueaim_And_Muzzle_Traces_With_LagComp()
    {
        // QC tracing.qc:46/85/97 — at g_antilag==2 W_SetupShot's trueaim + muzzle-nudge traces run through the
        // _antilag variants, so w_shotend/w_shotorg are computed against rewound enemy positions. Verify the port
        // brackets that whole trace section with exactly ONE balanced Begin/End around THIS shooter.
        Api.Services = new EngineServices(new CollisionWorld()); // empty world → trueaim/nudge traces miss at once
        var rec = new RecordingLagComp();
        LagComp.Provider = rec;
        try
        {
            Entity actor = Api.Services.Entities.Spawn();
            WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0), WeaponFiring.MaxShotDistance);

            Assert.Equal(1, rec.Begins);          // antilag_takeback ran before the SetupShot traces
            Assert.Equal(1, rec.Ends);            // antilag_restore ran after (balanced, even on a clean miss)
            Assert.Same(actor, rec.LastShooter);  // rewound to THIS shooter's view-time
        }
        finally
        {
            LagComp.Provider = null;
        }
    }

    [Fact]
    public void Entity_Weapon_Field_RoundTrips_In_The_Delta_Codec()
    {
        var baseline = new NetEntityState { EntNum = 5, Kind = NetEntityKind.Player, Weapon = 3 };
        var switched = baseline; switched.Weapon = 7; // the player switched weapons

        var w = new BitWriter();
        EntityField mask = EntityStateCodec.WriteDelta(w, baseline, switched);
        Assert.Equal(EntityField.Weapon, mask); // only the weapon field on the wire

        var r = new BitReader(w.WrittenSpan);
        NetEntityState got = EntityStateCodec.ReadDelta(ref r, baseline);
        Assert.Equal(7, got.Weapon);
        Assert.Equal(NetEntityKind.Player, got.Kind); // carried from baseline
    }
}
