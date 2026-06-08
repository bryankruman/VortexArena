using System.Numerics;

namespace XonoticGodot.Formats.Bsp;

/// <summary>
/// Potentially-Visible-Set queries over a parsed <see cref="BspData"/> — the C# successor to DP's
/// <c>Mod_Q3BSP_PointSuperContents</c>/<c>CM_PointLeafnum</c> + cluster-PVS path, backing the QC
/// <c>checkpvs(vector, entity)</c> builtin. A point is mapped to a BSP leaf (and thus a visibility cluster) by
/// descending the node tree; two points are "potentially visible" when their clusters' PVS bit is set.
///
/// <para>PVS is a conservative <em>superset</em> of true visibility: it never hides something that is actually
/// visible, so it is safe to use as a cheap pre-filter before an exact <c>traceline</c> line-of-sight test
/// (the QC pattern), or to cull entity networking / sound to a client's visible set. On an unvised map (no
/// <see cref="BspData.Vis"/> data) every query returns visible — exactly DP's degradation.</para>
///
/// Godot-free and immutable after construction, so it is safe to share across the sim and build in tests.
/// </summary>
public sealed class BspPvs
{
    private readonly BspNode[] _nodes;
    private readonly BspLeaf[] _leafs;
    private readonly BspPlane[] _planes;
    private readonly BspVis _vis;

    public BspPvs(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        _nodes = bsp.Nodes;
        _leafs = bsp.Leafs;
        _planes = bsp.Planes;
        _vis = bsp.Vis;
    }

    /// <summary>True when the map carries real visibility data (otherwise every <see cref="IsInPvs"/> is visible).</summary>
    public bool HasVis => _vis.ClusterCount > 0 && _vis.Data is { Length: > 0 } && _leafs.Length > 0;

    /// <summary>
    /// Descend the BSP node tree to the leaf containing <paramref name="point"/> (DP <c>CM_PointLeafnum</c>):
    /// at each node take the front child when the point is on the positive side of the splitting plane, else the
    /// back child. Returns a leaf index, or −1 when there is no node tree to descend.
    /// </summary>
    public int FindLeaf(Vector3 point)
    {
        if (_nodes.Length == 0)
            return _leafs.Length > 0 ? 0 : -1; // single-leaf model (no splits)

        int num = 0;
        int guard = 0;
        while (num >= 0)
        {
            if (num >= _nodes.Length || ++guard > _nodes.Length + 1)
                return -1; // malformed tree — bail to "no leaf" (caller treats as visible)
            BspNode node = _nodes[num];
            if (node.PlaneIndex < 0 || node.PlaneIndex >= _planes.Length)
                return -1;
            BspPlane plane = _planes[node.PlaneIndex];
            float d = Vector3.Dot(plane.Normal, point) - plane.Distance;
            num = d >= 0f ? node.Child0 : node.Child1;
        }
        return -num - 1; // leaf index = -(child)-1
    }

    /// <summary>The visibility cluster of a leaf (−1 if the index is invalid or the leaf has no cluster).</summary>
    public int LeafCluster(int leafIndex)
        => leafIndex >= 0 && leafIndex < _leafs.Length ? _leafs[leafIndex].Cluster : -1;

    /// <summary>True if cluster <paramref name="to"/> is potentially visible from cluster <paramref name="from"/>.</summary>
    public bool ClustersVisible(int from, int to) => _vis.ClusterVisible(from, to);

    /// <summary>
    /// QC <c>checkpvs(from, to)</c>: is <paramref name="to"/> potentially visible from <paramref name="from"/>?
    /// Returns true (conservatively visible) on an unvised map or when either point falls outside the leaf/cluster
    /// data — never a false negative, so it is safe as a traceline pre-filter.
    /// </summary>
    public bool IsInPvs(Vector3 from, Vector3 to)
    {
        if (!HasVis)
            return true;
        int clusterFrom = LeafCluster(FindLeaf(from));
        int clusterTo = LeafCluster(FindLeaf(to));
        if (clusterFrom < 0 || clusterTo < 0)
            return true; // a point in solid / no cluster info → conservatively visible
        return _vis.ClusterVisible(clusterFrom, clusterTo);
    }
}
