using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Bsp;

namespace XonoticGodot.Game;

/// <summary>
/// (§12.8) PVS-driven visibility for the spatially-split world mesh (§12.5 R5a): each frame the camera's
/// position descends the BSP tree to its visibility cluster, and every world cell is shown iff ANY of the
/// clusters its geometry occupies is potentially visible from there — the map compiler's own precomputed
/// occlusion (q3map2 vis), exactly the data DP culls with. This hides whole cells BEHIND WALLS, which
/// frustum culling cannot, at near-zero per-frame cost (one ~15-plane tree descent + a few dozen bitset
/// probes, and cell visibility is only re-applied when the camera CHANGES cluster).
///
/// <para><b>Losslessness:</b> PVS is a conservative superset of true visibility, and a cell's cluster set is
/// the UNION over its triangles — strictly more conservative than DP's per-face culling. A camera outside
/// the tree / in solid (cluster −1), a cell with no recorded clusters, or <c>r_pvs_cull 0</c> all degrade to
/// "everything visible" (today's behavior). Nothing DP would draw is ever hidden.</para>
/// </summary>
public sealed partial class WorldPvsCuller : Node
{
    private readonly BspPvs _pvs;
    private readonly (MeshInstance3D Node, int[] Clusters)[] _cells;
    private int _lastCluster = int.MinValue;   // sentinel: first frame always applies
    private bool _disabled;

    public WorldPvsCuller(BspPvs pvs, List<(MeshInstance3D Node, int[] Clusters)> cells)
    {
        Name = "WorldPvsCuller";
        _pvs = pvs;
        _cells = cells.ToArray();
    }

    public override void _Ready()
    {
        // Escape hatch for A/B + safety (archived like the other r_* engine cvars).
        XonoticGodot.Game.Menu.MenuState.Cvars.Register("r_pvs_cull", "1",
            XonoticGodot.Common.Services.CvarFlags.Save);
    }

    public override void _Process(double delta)
    {
        using var _prof = XonoticGodot.Game.Client.FrameProfiler.Scope("world.pvscull");
        bool enabled = XonoticGodot.Game.Menu.MenuState.Cvars.GetFloat("r_pvs_cull") != 0f;
        if (!enabled)
        {
            if (!_disabled)
            {
                // Turned off: restore everything once, then idle until re-enabled.
                foreach ((MeshInstance3D node, _) in _cells)
                    if (GodotObject.IsInstanceValid(node))
                        node.Visible = true;
                _disabled = true;
                _lastCluster = int.MinValue;
            }
            return;
        }
        _disabled = false;

        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null)
            return;

        System.Numerics.Vector3 quakeEye = Coords.ToQuake(cam.GlobalPosition);
        int cluster = _pvs.LeafCluster(_pvs.FindLeaf(quakeEye));
        if (cluster == _lastCluster)
            return;   // same cluster → same visible set; nothing to re-apply
        _lastCluster = cluster;

        bool showAll = cluster < 0;   // camera in solid / outside the tree → conservative: show everything
        foreach ((MeshInstance3D node, int[] clusters) in _cells)
        {
            bool visible = showAll || clusters.Length == 0;
            if (!visible)
                foreach (int c in clusters)
                    if (_pvs.ClustersVisible(cluster, c)) { visible = true; break; }
            if (GodotObject.IsInstanceValid(node))
                node.Visible = visible;
        }
    }
}
