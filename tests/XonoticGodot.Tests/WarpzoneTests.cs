using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.13: the seamless warpzone transform (lib/warpzone WarpZone_SetUp / TransformOrigin / Velocity)
/// — plane centers map to each other, speed is preserved, momentum carries through, and an entity moving out of
/// a zone isn't warped.
/// </summary>
public class WarpzoneTests
{
    // IN plane at origin facing +X (yaw 0); OUT plane 100 units away facing −X (yaw 180).
    private static WarpzoneTransform MakeT() =>
        new(new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 180, 0));

    [Fact]
    public void Transform_MapsPlaneCenters()
    {
        var t = MakeT();
        Vector3 mapped = t.TransformOrigin(new Vector3(0, 0, 0)); // the IN center
        Assert.True((mapped - new Vector3(100, 0, 0)).Length() < 0.01f); // → the OUT center
    }

    [Fact]
    public void Transform_PreservesSpeed()
    {
        var t = MakeT();
        var v = new Vector3(120, -260, 35);
        Vector3 tv = t.TransformVelocity(v);
        Assert.True(System.MathF.Abs(tv.Length() - v.Length()) < 0.01f); // rotation → length preserved
    }

    [Fact]
    public void Transform_MovingIntoIn_EmergesOutOfOut()
    {
        var t = MakeT();
        // moving into the IN surface (−X, against inFwd=+X) should emerge moving out of OUT (along outFwd=−X).
        Vector3 tv = t.TransformVelocity(new Vector3(-200, 0, 0));
        Assert.True(Vector3.Dot(Vector3.Normalize(tv), t.OutForward) > 0.99f); // along +outForward
    }

    [Fact]
    public void Teleport_WarpsEntityAndPreservesMomentum()
    {
        var mgr = new WarpzoneManager();
        var a = mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        var b = mgr.Spawn(new Vector3(100, 0, 0), new Vector3(0, 180, 0), "wzB", "wzA", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();
        Assert.True(a.Linked && b.Linked);

        // The entity has crossed the IN plane: its center+view_ofs is on the FAR (negative) side, which is the
        // Base gate (server.qc:193 WarpZone_PlaneDist(this, origin + view_ofs) >= 0 → don't teleport yet). ViewOfs
        // pushes the plane-side probe past the seam while the actual warp still transforms Origin (0,0,0)→(100,0,0).
        var e = new Entity { Origin = new Vector3(0, 0, 0), ViewOfs = new Vector3(-1, 0, 0), Velocity = new Vector3(-200, 0, 0) };
        float speedBefore = e.Velocity.Length();
        bool warped = mgr.Teleport(e, a);

        Assert.True(warped);
        Assert.True((e.Origin - new Vector3(100, 0, 0)).Length() < 0.01f);     // emerged at the OUT plane
        Assert.True(System.MathF.Abs(e.Velocity.Length() - speedBefore) < 0.01f); // momentum preserved
    }

    [Fact]
    public void Teleport_Client_StampsAuthoritativeFixAngleToExitFacing()
    {
        // Stormkeep-style PERPENDICULAR pair: IN faces +X (yaw 0), OUT faces +Y (yaw 90) — 90° apart. A client
        // crossing IN while looking WEST (−X, into the seam, yaw 180) must emerge looking NORTH (+outForward, yaw
        // 90) AND have the SERVER-AUTHORITATIVE fixangle stamped (QC WarpZone_TeleportPlayer `player.fixangle =
        // true`) so the listen host snaps the local view. Regression for the "comes out 90° sideways" report: the
        // body/velocity warped but the view never re-oriented because the snap was never stamped server-side.
        var mgr = new WarpzoneManager();
        var a = mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Spawn(new Vector3(100, 0, 0), new Vector3(0, 90, 0), "wzB", "wzA", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();

        var e = new Entity
        {
            Flags = EntFlags.Client,
            Origin = new Vector3(0, 0, 0),
            ViewOfs = new Vector3(-1, 0, 0),    // probe past the seam (Base gate uses origin + view_ofs)
            Velocity = new Vector3(-200, 0, 0), // moving into the IN surface
            Angles = new Vector3(0, 180, 0),    // looking WEST, into the seam
        };

        Assert.True(mgr.Teleport(e, a));
        Assert.True(e.FixAngle);                       // authoritative view-snap stamped for the host to consume
        Assert.Equal(e.Angles, e.FixAngleAngles);      // host applies the already-transformed exit facing verbatim
        // Exit faces +outForward (North): yaw ≈ 90, NOT the entry yaw 180 — the view IS re-oriented to the new plane.
        Assert.True(System.MathF.Abs(e.FixAngleAngles.Y - 90f) < 0.5f);
    }

    [Fact]
    public void Teleport_NonClient_DoesNotStampFixAngle()
    {
        // A projectile (no Client flag) warps origin/velocity/angles but must NOT stamp the view-snap fixangle.
        var mgr = new WarpzoneManager();
        var a = mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Spawn(new Vector3(100, 0, 0), new Vector3(0, 180, 0), "wzB", "wzA", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();

        var e = new Entity { Origin = new Vector3(0, 0, 0), ViewOfs = new Vector3(-1, 0, 0), Velocity = new Vector3(-200, 0, 0) };
        Assert.True(mgr.Teleport(e, a));
        Assert.False(e.FixAngle); // no spurious view-snap on a non-client
    }

    [Fact]
    public void Teleport_SkipsWhenMovingOutOfZone()
    {
        var mgr = new WarpzoneManager();
        var a = mgr.Spawn(new Vector3(0, 0, 0), new Vector3(0, 0, 0), "wzA", "wzB", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Spawn(new Vector3(100, 0, 0), new Vector3(0, 180, 0), "wzB", "wzA", new Vector3(-8, -64, -64), new Vector3(8, 64, 64));
        mgr.Link();

        // moving +X (out of the IN surface, along inFwd) → not a crossing, no warp.
        var e = new Entity { Origin = new Vector3(0, 0, 0), Velocity = new Vector3(200, 0, 0) };
        Assert.False(mgr.Teleport(e, a));
        Assert.Equal(new Vector3(0, 0, 0), e.Origin); // unchanged
    }
}
