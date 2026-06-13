using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the BSP Potentially-Visible-Set: parsing the Nodes/Leafs/Vis lumps, the point→leaf tree descent, the
/// cluster visibility bitset, and the <c>checkpvs</c> facade. PVS is a conservative superset of true visibility
/// (never a false negative), so it is safe as a traceline pre-filter; an unvised map reads as all-visible.
/// </summary>
public class BspPvsTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    /// <summary>
    /// A minimal vised BSP: one splitting plane at X=0 with two leaves/clusters, where each cluster sees ONLY
    /// itself. Front (x≥0) = leaf 0 / cluster 0; back (x&lt;0) = leaf 1 / cluster 1.
    /// </summary>
    private static BspData TwoClusterBsp() => new()
    {
        Planes = new[] { new BspPlane(new Vector3(1, 0, 0), 0f) },
        Nodes = new[] { new BspNode(0, -1, -2) },           // child0 → leaf0 (-1), child1 → leaf1 (-2)
        Leafs = new[]
        {
            new BspLeaf(0, 0, 0, 0, 0, 0),                  // leaf 0 → cluster 0
            new BspLeaf(1, 0, 0, 0, 0, 0),                  // leaf 1 → cluster 1
        },
        Vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 }), // 0 sees 0; 1 sees 1
    };

    private static readonly Vector3 Front = new(10, 0, 0);   // cluster 0
    private static readonly Vector3 Back = new(-10, 0, 0);   // cluster 1

    [Fact]
    public void FindLeaf_Descends_To_The_Correct_Side()
    {
        var pvs = new BspPvs(TwoClusterBsp());
        Assert.True(pvs.HasVis);
        Assert.Equal(0, pvs.FindLeaf(Front));
        Assert.Equal(1, pvs.FindLeaf(Back));
        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(Front)));
        Assert.Equal(1, pvs.LeafCluster(pvs.FindLeaf(Back)));
    }

    /// <summary>
    /// Regression for the §12.8 world-cell PVS culler: <see cref="BspPvs.FindLeaf"/> descends the tree in
    /// QUAKE space (Z-up), so a point expressed in Godot axes (Y-up) must be converted back first. The map
    /// loader recorded each cell's clusters from its mesh centroids — which live in Godot space — and was
    /// feeding them to FindLeaf RAW, so the Y/Z swap mislabeled every cell and the culler hid geometry that
    /// was actually visible (world surfaces vanishing as the camera crossed clusters). This locks the
    /// contract: the Godot-axis form lands in the WRONG leaf; converting to Quake first lands in the right one.
    /// </summary>
    [Fact]
    public void FindLeaf_Needs_Quake_Axes_Not_Godot_Axes()
    {
        // A Y-split tree so the Quake↔Godot Y/Z swap actually flips the side: front (Quake y≥0) = cluster 0.
        var bsp = new BspData
        {
            Planes = new[] { new BspPlane(new Vector3(0, 1, 0), 0f) },
            Nodes = new[] { new BspNode(0, -1, -2) },
            Leafs = new[] { new BspLeaf(0, 0, 0, 0, 0, 0), new BspLeaf(1, 0, 0, 0, 0, 0) },
            Vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 }),
        };
        var pvs = new BspPvs(bsp);

        // A surface centroid in QUAKE space, genuinely in front (y=10 ≥ 0 → cluster 0).
        var quake = new Vector3(0, 10, -5);
        // Coords.ToGodot mirror (game-side, Godot-coupled, so replicated here): godot = (q.X, q.Z, -q.Y).
        var godot = new Vector3(quake.X, quake.Z, -quake.Y);
        // Coords.ToQuake mirror: quake = (g.X, -g.Z, g.Y) — the exact inverse, what the fix applies.
        var roundTripped = new Vector3(godot.X, -godot.Z, godot.Y);

        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(quake)));            // truth: cluster 0
        Assert.Equal(1, pvs.LeafCluster(pvs.FindLeaf(godot)));           // BUG: raw Godot axes → wrong cluster
        Assert.Equal(0, pvs.LeafCluster(pvs.FindLeaf(roundTripped)));    // FIX: convert back to Quake → correct
    }

    [Fact]
    public void IsInPvs_Honors_The_Cluster_Bitset_Both_Directions()
    {
        var pvs = new BspPvs(TwoClusterBsp());
        Assert.True(pvs.IsInPvs(Front, Front));   // cluster 0 sees itself
        Assert.True(pvs.IsInPvs(Back, Back));     // cluster 1 sees itself
        Assert.False(pvs.IsInPvs(Front, Back));   // cluster 0 does NOT see cluster 1
        Assert.False(pvs.IsInPvs(Back, Front));   // and the reverse
    }

    [Fact]
    public void Unvised_Map_Is_Conservatively_All_Visible()
    {
        BspData src = TwoClusterBsp();
        var bsp = new BspData { Planes = src.Planes, Nodes = src.Nodes, Leafs = src.Leafs }; // no Vis
        var pvs = new BspPvs(bsp);
        Assert.False(pvs.HasVis);
        Assert.True(pvs.IsInPvs(Front, Back)); // no PVS → never cull
    }

    [Fact]
    public void Cluster_Bitset_Out_Of_Range_Reads_Visible()
    {
        var vis = new BspVis(2, 1, new byte[] { 0b0000_0001, 0b0000_0010 });
        Assert.True(vis.ClusterVisible(0, 0));
        Assert.False(vis.ClusterVisible(0, 1));
        Assert.True(vis.ClusterVisible(-1, 0));   // solid-leaf cluster → conservatively visible
        Assert.True(vis.ClusterVisible(0, 99));   // out of range → conservatively visible
    }

    [Fact]
    public void CheckPvs_Facade_Delegates_To_The_Wired_Pvs()
    {
        var world = new CollisionWorld();
        world.BuildGrid();
        var services = new EngineServices(world);

        // No PVS wired → conservatively visible (a non-BSP/test world).
        Assert.True(services.Trace.CheckPvs(Front, Back));

        services.Pvs = new BspPvs(TwoClusterBsp());
        Assert.True(services.Trace.CheckPvs(Front, Front));
        Assert.False(services.Trace.CheckPvs(Front, Back));
    }

    [Fact]
    public void Real_Bsp_Vis_Lumps_Parse_And_SelfVisibility_Holds()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        string? bspPath = vfs.Find("maps/", "bsp").FirstOrDefault();
        if (bspPath is null) return;

        BspData bsp = BspReader.Read(vfs.ReadBytes(bspPath));
        // _init.bsp ships a real BSP tree (nodes + leaves) but is unvised (empty PVS lump), so this exercises
        // the real tree descent while the cluster-bitset culling is covered by the synthetic tests above.
        Assert.True(bsp.Nodes.Length >= 1, "a compiled BSP has a node tree");
        Assert.True(bsp.Leafs.Length >= 1, "a compiled BSP has leaves");

        var pvs = new BspPvs(bsp);
        // Descent on real data must land in a valid leaf, and self-visibility always holds.
        int leaf = pvs.FindLeaf(new Vector3(0, 0, 0));
        Assert.InRange(leaf, 0, bsp.Leafs.Length - 1);
        Assert.True(pvs.IsInPvs(Vector3.Zero, Vector3.Zero));
    }
}
