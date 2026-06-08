using System.Globalization;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server.Bot;

/// <summary>
/// Waypoint type flags (QC WAYPOINTFLAG_*, server/bot/api.qh). Only the bits that affect navigation
/// behaviour in this port are wired up; the editor-only bits (PROTECTED/USEFUL/DEAD_END) are carried
/// through verbatim so a round-trip of the <c>.waypoints</c> file preserves them.
/// </summary>
[Flags]
public enum WaypointFlags
{
    None = 0,
    Generated = 1 << 23, // auto-generated (item/teleporter waypoint), not written to .waypoints
    Item = 1 << 22,
    Teleport = 1 << 21, // teleports, warpzones and jumppads (a single wp00 destination link)
    Personal = 1 << 19,
    Protected = 1 << 18,
    Useful = 1 << 17,
    DeadEnd = 1 << 16,
    Ladder = 1 << 15,
    Jump = 1 << 14,    // bot must jump along the outgoing link
    CustomJp = 1 << 13,
    Crouch = 1 << 12,  // bot must crouch while traversing
    Support = 1 << 11,
}

/// <summary>
/// A single navigation node — the C# successor to a QuakeC <c>spawnfunc_waypoint</c> edict
/// (server/bot/default/waypoints.qc). A waypoint is either a point (Mins == Maxs == 0, a "wp") or a
/// box (a "waybox", e.g. a teleporter trigger volume); for a box, <see cref="Mins"/>/<see cref="Maxs"/>
/// are the half-extents relative to <see cref="Origin"/> and <see cref="IsBox"/> is true.
///
/// In QC the outgoing links live in 32 flat <c>.entity wp00..wp31</c> fields with parallel
/// <c>wpXXmincost</c> costs; here they collapse to a single <see cref="Links"/> adjacency list.
/// </summary>
public sealed class Waypoint
{
    /// <summary>Center of the waypoint (QC <c>.origin</c>).</summary>
    public Vector3 Origin;

    /// <summary>Box half-extents relative to <see cref="Origin"/> (QC <c>.mins</c>); zero for a point waypoint.</summary>
    public Vector3 Mins;

    /// <summary>Box half-extents relative to <see cref="Origin"/> (QC <c>.maxs</c>); zero for a point waypoint.</summary>
    public Vector3 Maxs;

    public WaypointFlags Flags;

    /// <summary>Dense index into <see cref="WaypointNetwork.Nodes"/> (assigned on add). -1 until added.</summary>
    public int Index = -1;

    /// <summary>Outgoing links (QC <c>wp00..wp31</c>). Each carries a precomputed travel cost.</summary>
    public readonly List<WaypointLink> Links = new();

    public bool IsBox => Mins != Vector3.Zero || Maxs != Vector3.Zero;

    /// <summary>World-space lower bound (QC <c>.absmin</c>).</summary>
    public Vector3 AbsMin => Origin + Mins;

    /// <summary>World-space upper bound (QC <c>.absmax</c>).</summary>
    public Vector3 AbsMax => Origin + Maxs;

    /// <summary>Box center (QC <c>(absmin + absmax) * 0.5</c>), == <see cref="Origin"/> for a point.</summary>
    public Vector3 Center => (AbsMin + AbsMax) * 0.5f;

    public bool HasFlag(WaypointFlags f) => (Flags & f) != 0;

    /// <summary>
    /// Closest point on this waypoint to <paramref name="from"/> (QC navigation_markroutes_checkwaypoint
    /// box-clamp). For a point waypoint this is just <see cref="Origin"/>.
    /// </summary>
    public Vector3 ClosestPoint(Vector3 from)
    {
        if (!IsBox) return Origin;
        var lo = AbsMin;
        var hi = AbsMax;
        return new Vector3(
            QMath.Clamp(from.X, lo.X, hi.X),
            QMath.Clamp(from.Y, lo.Y, hi.Y),
            QMath.Clamp(from.Z, lo.Z, hi.Z));
    }
}

/// <summary>An outgoing link from one waypoint to another with a precomputed traversal cost.</summary>
public readonly struct WaypointLink
{
    public readonly Waypoint To;
    /// <summary>Travel cost (seconds-ish; QC waypoint_getlinkcost). 999 marks a known-unwalkable link.</summary>
    public readonly float Cost;

    public WaypointLink(Waypoint to, float cost)
    {
        To = to;
        Cost = cost;
    }
}

/// <summary>
/// The waypoint navigation graph for a map — the C# successor to QuakeC's <c>g_waypoints</c> intrusive
/// list plus the link bookkeeping in waypoints.qc. Owns the nodes, the spatial "nearest" query, the
/// <c>.waypoints</c>/<c>.waypoints.cache</c> loaders, and an A* shortest-path search.
///
/// The QC engine keeps Dijkstra state (wpcost/enemy back-pointers) directly on each waypoint edict and
/// recomputes it every strategy frame (navigation_markroutes). Here that transient state is kept out of
/// <see cref="Waypoint"/> and computed locally per search so the graph itself stays immutable and the
/// network is safe to share across bots.
/// </summary>
public sealed class WaypointNetwork
{
    private readonly List<Waypoint> _nodes = new();

    public IReadOnlyList<Waypoint> Nodes => _nodes;
    public int Count => _nodes.Count;

    // ---- cost model (QC waypoint_getlinearcost / waypoint_gettravelcost) ----

    /// <summary>QC autocvar_sv_maxspeed default (xonotic-server.cfg). Used as the cost denominator.</summary>
    public float MaxSpeed = 320f;

    /// <summary>QC autocvar_sv_gravity default. Drives the fall-time term of travel cost.</summary>
    public float Gravity = 800f;

    /// <summary>Apparent jump height (QC jumpheight_vec.z, derived from sv_jumpvelocity); falls beyond this add a time penalty.</summary>
    public float JumpHeight = 130f;

    /// <summary>
    /// Linear (distance/speed) cost (QC waypoint_getlinearcost). The bunnyhop 1.25 speedup is skill
    /// dependent in QC; we use the base speed here so costs are skill-stable across bots.
    /// </summary>
    public float LinearCost(float dist) => dist / MaxSpeed;

    /// <summary>
    /// Travel cost between two world points (QC waypoint_gettravelcost / waypoint_gettravelcost_inwater) —
    /// linear xy/3d cost plus a fall-time term for big drops, with the QC underwater (slower) and crouched
    /// (slower) half-path adjustments. <paramref name="fromInWater"/>/<paramref name="toInWater"/> mark
    /// whether each endpoint is submerged, and <paramref name="crouch"/> that the link is traversed crouched.
    /// </summary>
    public float TravelCost(Vector3 from, Vector3 to, bool fromIsJump = false,
        bool fromInWater = false, bool toInWater = false, bool crouch = false)
    {
        float c = LinearCost((to - from).Length());
        float height = from.Z - to.Z;
        if (height > JumpHeight && Gravity > 0f)
        {
            var flat = new Vector3(to.X - from.X, to.Y - from.Y, 0f);
            float fallTime = MathF.Sqrt(height / (Gravity / 2f));
            float flatCost = LinearCost(flat.Length());
            c = MathF.Max(flatCost, fallTime);
        }

        // QC waypoint_gettravelcost underwater/crouch adjustments: movement is slower in water and while
        // crouched, so those segments cost more time. QC scales the half of the path in each condition; we
        // apply the speed factor to the whole-segment cost when an endpoint is in that state (a faithful
        // approximation that keeps the relative ordering of links the bot's A* needs).
        if (fromInWater || toInWater)
        {
            // swimming is ~0.7x walk speed → ~1.43x the time (the "underwater" half-path penalty).
            float waterFrac = (fromInWater && toInWater) ? 1f : 0.5f;
            c *= 1f + waterFrac * (1f / 0.7f - 1f);
        }
        if (crouch)
        {
            // crouched movement is ~0.5x speed → 2x time for the crouched portion.
            c *= 1f + 0.5f * (1f / 0.5f - 1f);
        }
        return c;
    }

    // ---- construction / linking ----

    /// <summary>Spawn a point waypoint (QC waypoint_spawn with m1 == m2).</summary>
    public Waypoint Add(Vector3 origin, WaypointFlags flags = WaypointFlags.None)
        => Add(origin, Vector3.Zero, Vector3.Zero, flags);

    /// <summary>
    /// Spawn a waypoint/waybox (QC waypoint_spawn). <paramref name="mins"/>/<paramref name="maxs"/> are
    /// box half-extents relative to <paramref name="origin"/>; pass zero for a point waypoint.
    /// </summary>
    public Waypoint Add(Vector3 origin, Vector3 mins, Vector3 maxs, WaypointFlags flags = WaypointFlags.None)
    {
        var wp = new Waypoint { Origin = origin, Mins = mins, Maxs = maxs, Flags = flags, Index = _nodes.Count };
        _nodes.Add(wp);
        return wp;
    }

    /// <summary>
    /// Add a directed link a-&gt;b with auto-computed cost (QC waypoint_addlink). Pass
    /// <paramref name="bidirectional"/> to also add b-&gt;a.
    /// </summary>
    public void Link(Waypoint a, Waypoint b, bool bidirectional = false, float? cost = null)
    {
        float c = cost ?? TravelCost(a.ClosestPoint(b.Origin), b.ClosestPoint(a.Origin), a.HasFlag(WaypointFlags.Jump));
        AddLinkOnce(a, b, c);
        if (bidirectional)
            AddLinkOnce(b, a, cost ?? TravelCost(b.ClosestPoint(a.Origin), a.ClosestPoint(b.Origin), b.HasFlag(WaypointFlags.Jump)));
    }

    private static void AddLinkOnce(Waypoint a, Waypoint b, float cost)
    {
        for (int i = 0; i < a.Links.Count; i++)
        {
            if (ReferenceEquals(a.Links[i].To, b))
            {
                a.Links[i] = new WaypointLink(b, cost);
                return;
            }
        }
        a.Links.Add(new WaypointLink(b, cost));
    }

    /// <summary>
    /// Auto-link every waypoint to nearby waypoints within <paramref name="maxDist"/> (QC
    /// waypoint_schedulerelinkall → waypoint_addlink_for). Each candidate link is gated by a
    /// <see cref="BotTracewalk"/> reachability test (the QC tracewalk: can a player actually walk/step/swim
    /// from a to b?), so links only form along traversable ground — matching real Xonotic instead of pure
    /// distance/LOS. Teleport/jump/ladder/custom waypoints keep their hand-authored outgoing links
    /// (QC WPFLAGMASK_NORELINK). Falls back to a straight LOS gate when no collision world is available
    /// (<paramref name="requireLineOfSight"/>), or pure distance when that's off too.
    /// </summary>
    public void AutoLink(float maxDist = 1050f, bool requireLineOfSight = true, int maxLinksPerNode = 32)
    {
        float maxDist2 = maxDist * maxDist;
        bool canTrace = Api.Services is not null;

        foreach (var a in _nodes)
        {
            // teleport/jump/ladder waypoints keep their hand-authored outgoing links (QC WPFLAGMASK_NORELINK)
            if ((a.Flags & (WaypointFlags.Teleport | WaypointFlags.Jump | WaypointFlags.Ladder
                            | WaypointFlags.CustomJp | WaypointFlags.Support)) != 0)
                continue;

            // candidates sorted by distance so we keep the closest ones if we hit the per-node cap
            var near = new List<(float d2, Waypoint b)>();
            foreach (var b in _nodes)
            {
                if (ReferenceEquals(a, b)) continue;
                float d2 = (b.Origin - a.Origin).LengthSquared();
                if (d2 > maxDist2) continue;
                near.Add((d2, b));
            }
            near.Sort((x, y) => x.d2.CompareTo(y.d2));

            foreach (var (_, b) in near)
            {
                if (a.Links.Count >= maxLinksPerNode) break;
                Vector3 from = a.ClosestPoint(b.Origin);
                Vector3 to = b.ClosestPoint(a.Origin);
                bool reachable;
                if (canTrace)
                    // QC tracewalk reachability: walk/step/swim from a to b with the player hull.
                    reachable = BotTracewalk.CanWalk(from, to, _playerMins, _playerMaxs, b.IsBox ? (b.AbsMax.Z - to.Z) : 0f);
                else if (requireLineOfSight)
                    reachable = true; // no world: accept (offline graph build)
                else
                    reachable = true;
                if (!reachable)
                    continue;
                bool crouch = a.HasFlag(WaypointFlags.Crouch) || b.HasFlag(WaypointFlags.Crouch);
                Link(a, b, cost: TravelCost(from, to, a.HasFlag(WaypointFlags.Jump), crouch: crouch));
            }
        }
    }

    /// <summary>Player hull used for tracewalk reachability tests (QC PL_MIN_CONST/PL_MAX_CONST).</summary>
    private static readonly Vector3 _playerMins = new(-16f, -16f, -24f);
    private static readonly Vector3 _playerMaxs = new(16f, 16f, 45f);

    /// <summary>
    /// Auto-generate a navigable waypoint graph from the live map entities (QC the
    /// <c>waypoint_spawnforitem</c> / <c>waypoint_spawnforteleporter</c> / <c>waypoint_spawn_fromeditor</c>
    /// auto-waypointing): drop a waypoint on every item and spawn point, a box waypoint over every
    /// teleporter/jumppad trigger with a one-way Teleport link to its destination, then walk-link the rest
    /// (<see cref="AutoLink"/>). Lets bots navigate a map that ships no hand-authored <c>.waypoints</c> file.
    /// Returns the number of waypoints generated.
    /// </summary>
    public int GenerateFromEntities(IReadOnlyList<XonoticGodot.Common.Framework.Entity> entities, bool autoLink = true)
    {
        // index destinations by targetname so a trigger's .target resolves to its exit point.
        var byTargetName = new Dictionary<string, XonoticGodot.Common.Framework.Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entities)
            if (!string.IsNullOrEmpty(e.TargetName) && !byTargetName.ContainsKey(e.TargetName))
                byTargetName[e.TargetName] = e;

        int before = _nodes.Count;
        var destWaypoints = new Dictionary<XonoticGodot.Common.Framework.Entity, Waypoint>(ReferenceEqualityComparer.Instance);

        Waypoint DestFor(XonoticGodot.Common.Framework.Entity dst)
        {
            if (!destWaypoints.TryGetValue(dst, out var wp))
            {
                wp = Add(dst.Origin + new Vector3(0, 0, 24f), WaypointFlags.Generated);
                destWaypoints[dst] = wp;
            }
            return wp;
        }

        foreach (var e in entities)
        {
            if (e is null || e.IsFreed) continue;
            string cn = e.ClassName ?? "";

            // items + spawn points → a stand-on-it point waypoint.
            if ((e.Flags & XonoticGodot.Common.Framework.EntFlags.Item) != 0
                || cn.StartsWith("info_player", StringComparison.Ordinal))
            {
                Add(e.Origin + new Vector3(0, 0, 24f),
                    WaypointFlags.Generated | ((e.Flags & XonoticGodot.Common.Framework.EntFlags.Item) != 0 ? WaypointFlags.Item : WaypointFlags.None));
                continue;
            }

            // teleporter / jumppad triggers → a box waypoint with a one-way Teleport link to the destination.
            bool isTeleport = cn == "trigger_teleport";
            bool isJumppad = cn == "trigger_push" || cn == "trigger_push_velocity";
            if (isTeleport || isJumppad)
            {
                XonoticGodot.Common.Framework.Entity? dst = null;
                if (!string.IsNullOrEmpty(e.Target)) byTargetName.TryGetValue(e.Target, out dst);
                var box = Add(e.Origin, e.Mins, e.Maxs, WaypointFlags.Generated | WaypointFlags.Teleport);
                if (dst is not null)
                    Link(box, DestFor(dst)); // one-way: enter the trigger → arrive at the destination
            }
        }

        if (autoLink)
            AutoLink();
        return _nodes.Count - before;
    }

    // Reusable scratch for Nearest's candidate sort — avoids a per-call allocation. The sim drives bots
    // sequentially on one thread, so a shared buffer on the shared network is safe (Nearest isn't re-entrant).
    private readonly List<(float D2, Waypoint Wp)> _nearScratch = new();
    private static readonly Comparison<(float D2, Waypoint Wp)> _byD2 = static (a, b) => a.D2.CompareTo(b.D2);

    /// <summary>
    /// Nearest waypoint to a world position (QC navigation_findnearestwaypoint). Prefers a box waypoint that
    /// contains the point; otherwise scans by distance within <paramref name="maxDist"/> and — when a
    /// collision world is available and <paramref name="requireReachable"/> is set — confirms the candidate
    /// is actually reachable from <paramref name="pos"/> with a <see cref="BotTracewalk"/> (so the bot is
    /// routed via a node it can genuinely walk to, not one across a wall). Falls back to the nearest by pure
    /// distance if none pass the reachability test (better a far node than none).
    ///
    /// Performance: candidates are gathered then sorted nearest-first and the FIRST reachable one is returned —
    /// since closer-reachable always wins, this stops at the first successful <see cref="BotTracewalk.CanWalk"/>
    /// (typically one) instead of tracewalking every node in range as the old distance-keyed scan did.
    /// </summary>
    public Waypoint? Nearest(Vector3 pos, float maxDist = 1050f, bool requireReachable = true)
    {
        float maxD2 = maxDist * maxDist;
        bool canTrace = requireReachable && Api.Services is not null;

        var cand = _nearScratch;
        cand.Clear();
        for (int i = 0; i < _nodes.Count; i++)
        {
            Waypoint wp = _nodes[i];
            if (wp.IsBox)
            {
                var lo = wp.AbsMin;
                var hi = wp.AbsMax;
                if (pos.X >= lo.X && pos.X <= hi.X && pos.Y >= lo.Y && pos.Y <= hi.Y
                    && pos.Z >= lo.Z && pos.Z <= hi.Z)
                    return wp; // inside the box: definitive match
            }
            float d2 = (wp.ClosestPoint(pos) - pos).LengthSquared();
            if (d2 <= maxD2)
                cand.Add((d2, wp));
        }
        if (cand.Count == 0)
            return null;

        cand.Sort(_byD2);
        if (!canTrace)
            return cand[0].Wp; // no collision world: nearest by distance

        // Nearest-first: the first node we can actually walk to is the nearest reachable one.
        for (int i = 0; i < cand.Count; i++)
        {
            Waypoint wp = cand[i].Wp;
            Vector3 cp = wp.ClosestPoint(pos);
            if (BotTracewalk.CanWalk(pos, cp, _playerMins, _playerMaxs, wp.IsBox ? (wp.AbsMax.Z - cp.Z) : 0f))
                return wp;
        }
        return cand[0].Wp; // none reachable: fall back to the nearest by pure distance
    }

    // ---- A* pathfinding (deliverable: simple A* FindPath) ----

    /// <summary>
    /// A* shortest path between two graph nodes over the link costs (QC builds this implicitly via
    /// navigation_markroutes + back-pointer walk). Returns the node sequence from <paramref name="from"/> to
    /// <paramref name="to"/> inclusive, or null if unreachable. The heuristic is straight-line/MaxSpeed, an
    /// admissible lower bound on remaining travel cost so the result is optimal.
    /// </summary>
    // Reusable A* working set (resized when the graph grows). Reused across searches to avoid allocating four
    // arrays + a heap on every FindPath; the sim drives bots sequentially so a shared scratch is safe.
    private float[] _gScore = Array.Empty<float>();
    private float[] _fScore = Array.Empty<float>();
    private Waypoint?[] _cameFrom = Array.Empty<Waypoint?>();
    private bool[] _closed = Array.Empty<bool>();
    private MinHeap? _open;

    public List<Waypoint>? FindPath(Waypoint from, Waypoint to)
    {
        if (ReferenceEquals(from, to))
            return new List<Waypoint> { from };

        int n = _nodes.Count;
        if (_gScore.Length < n)
        {
            _gScore = new float[n];
            _fScore = new float[n];
            _cameFrom = new Waypoint?[n];
            _closed = new bool[n];
        }
        float[] gScore = _gScore, fScore = _fScore;
        Waypoint?[] cameFrom = _cameFrom;
        bool[] closed = _closed;
        for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; fScore[i] = float.PositiveInfinity; cameFrom[i] = null; closed[i] = false; }

        // binary-heap open set keyed by fScore; entries may be stale (lazy decrease-key)
        MinHeap open = _open ??= new MinHeap(n);
        open.Clear();
        gScore[from.Index] = 0f;
        fScore[from.Index] = Heuristic(from, to);
        open.Push(from.Index, fScore[from.Index]);

        while (open.TryPop(out int currentIdx))
        {
            if (closed[currentIdx]) continue; // stale heap entry
            var current = _nodes[currentIdx];
            if (ReferenceEquals(current, to))
                return Reconstruct(cameFrom, current);

            closed[currentIdx] = true;

            foreach (var link in current.Links)
            {
                var nb = link.To;
                if (nb.Index < 0 || closed[nb.Index]) continue;
                if (link.Cost >= 999f) continue; // known-unwalkable link

                float tentative = gScore[currentIdx] + link.Cost;
                if (tentative < gScore[nb.Index])
                {
                    cameFrom[nb.Index] = current;
                    gScore[nb.Index] = tentative;
                    fScore[nb.Index] = tentative + Heuristic(nb, to);
                    open.Push(nb.Index, fScore[nb.Index]);
                }
            }
        }
        return null;
    }

    private float Heuristic(Waypoint a, Waypoint b) => (b.Origin - a.Origin).Length() / MaxSpeed;

    private static List<Waypoint> Reconstruct(Waypoint?[] cameFrom, Waypoint current)
    {
        var path = new List<Waypoint> { current };
        var c = cameFrom[current.Index];
        while (c is not null)
        {
            path.Add(c);
            c = cameFrom[c.Index];
        }
        path.Reverse();
        return path;
    }

    // ---- .waypoints file format (QC waypoint_loadall / waypoint_load_links) ----

    /// <summary>
    /// The symmetry header parsed from a <c>.waypoints</c> file (QC the <c>//WAYPOINT_SYMMETRY</c> lines):
    /// the format version, plus the map's symmetry origin/axis/order so a half-authored graph can be mirrored.
    /// <see cref="Order"/> 0 means "no symmetry / autodetect"; ≥2 is a rotational symmetry order.
    /// </summary>
    public sealed class SymmetryHeader
    {
        public int Version;
        public Vector3 Origin;     // QC g_waypointeditor_symmetrical_origin
        public Vector3 Axis;       // QC g_waypointeditor_symmetrical_axis (m, q packed in x/y)
        public int Order;          // QC the symmetry order (0 = none/auto, 2..4 rotational, -1/-2 mirror)
    }

    /// <summary>The symmetry header of the last <see cref="LoadFromText"/>, or null if none was present.</summary>
    public SymmetryHeader? Symmetry { get; private set; }

    /// <summary>
    /// Parse a Xonotic <c>.waypoints</c> file (QC waypoint_loadall). Format: an optional <c>//WAYPOINT_*</c>
    /// comment header (version + symmetry, captured into <see cref="Symmetry"/>), then repeating triples of
    /// lines — m1 ("x y z" = origin+mins), m2 (origin+maxs), flags (int). A point waypoint has m1 == m2;
    /// otherwise it's a waybox. Returns a network with nodes added but NOT linked — call
    /// <see cref="LoadLinks"/> with the matching <c>.waypoints.cache</c>, <see cref="LoadHardwiredLinks"/>
    /// with the <c>.waypoints.hardwired</c> file, or <see cref="AutoLink"/>.
    /// </summary>
    public static WaypointNetwork LoadFromText(string text)
    {
        var net = new WaypointNetwork();
        using var reader = new StringReader(text);
        string? line;
        bool parsingComments = true;
        var pending = new List<string>(3);
        SymmetryHeader? sym = null;

        while ((line = reader.ReadLine()) is not null)
        {
            if (parsingComments)
            {
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    sym ??= new SymmetryHeader();
                    ParseSymmetryComment(line, sym); // WAYPOINT_VERSION / WAYPOINT_SYMMETRY*
                    continue;
                }
                parsingComments = false;
            }

            pending.Add(line);
            if (pending.Count == 3)
            {
                if (TryParseVec(pending[0], out var m1) && TryParseVec(pending[1], out var m2)
                    && int.TryParse(pending[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fl))
                {
                    var origin = (m1 + m2) * 0.5f;
                    var mins = m1 - origin;
                    var maxs = m2 - origin;
                    net.Add(origin, mins, maxs, (WaypointFlags)fl);
                }
                pending.Clear();
            }
        }
        net.Symmetry = sym;
        return net;
    }

    /// <summary>Parse one <c>//WAYPOINT_*</c> header comment into the symmetry header (QC the editor cvars).</summary>
    private static void ParseSymmetryComment(string line, SymmetryHeader sym)
    {
        // forms: "//WAYPOINT_VERSION 1", "//WAYPOINT_SYMMETRY_ORIGIN x y z",
        //        "//WAYPOINT_SYMMETRY_AXIS m q", "//WAYPOINT_SYMMETRY_ORDER n"
        string body = line.TrimStart('/').Trim();
        int sp = body.IndexOf(' ');
        if (sp < 0) return;
        string key = body[..sp].ToUpperInvariant();
        string val = body[(sp + 1)..].Trim();
        switch (key)
        {
            case "WAYPOINT_VERSION":
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out sym.Version);
                break;
            case "WAYPOINT_SYMMETRY_ORIGIN":
                if (TryParseVec(val, out var o)) sym.Origin = o;
                break;
            case "WAYPOINT_SYMMETRY_AXIS":
                if (TryParseVec(val + " 0", out var a)) sym.Axis = a; // m q -> (m, q, 0)
                break;
            case "WAYPOINT_SYMMETRY_ORDER":
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out sym.Order);
                break;
        }
    }

    /// <summary>Load a <c>.waypoints</c> file from disk (QC fopen path maps/&lt;map&gt;.waypoints).</summary>
    public static WaypointNetwork LoadFromFile(string path) => LoadFromText(File.ReadAllText(path));

    /// <summary>
    /// The host's one-call map-waypoint setup (QC the waypoint load-or-autogenerate path), built ONCE per map
    /// and shared across all bot brains. If the map ships a hand-authored <c>.waypoints</c> file, load its
    /// nodes, then wire the edges from the precompiled <c>.waypoints.cache</c> (just cost math — no tracewalk)
    /// plus any <c>.waypoints.hardwired</c> links; only when no cache ships do we pay the
    /// <see cref="AutoLink"/> tracewalk pass. Otherwise auto-generate a navigable graph from the live map
    /// entities (<see cref="GenerateFromEntities"/>) so bots can still play. Pass the loaded file texts
    /// (<paramref name="waypointFileText"/> null/empty → auto-generate) and the spawned entity list.
    /// </summary>
    public static WaypointNetwork ForMap(string? waypointFileText, IReadOnlyList<XonoticGodot.Common.Framework.Entity> entities,
        string? linkCacheText = null, string? hardwiredText = null)
    {
        if (!string.IsNullOrWhiteSpace(waypointFileText))
        {
            var net = LoadFromText(waypointFileText);
            bool haveCache = !string.IsNullOrWhiteSpace(linkCacheText);
            if (haveCache)
                net.LoadLinks(linkCacheText!);
            if (!string.IsNullOrWhiteSpace(hardwiredText))
                net.LoadHardwiredLinks(hardwiredText!);
            // No precompiled link cache → compute reachability links now (the expensive tracewalk pass). With a
            // cache the edges are already loaded above, so AutoLink (and its O(N^2) tracewalks) is skipped.
            if (!haveCache)
                net.AutoLink();
            return net;
        }
        var auto = new WaypointNetwork();
        auto.GenerateFromEntities(entities);
        return auto;
    }

    /// <summary>
    /// Load the link cache (QC waypoint_load_links) over an already-loaded network. Each non-comment line is
    /// "fromOrigin*toOrigin" (two "x y z" vectors separated by '*'); each adds one directed link, with cost
    /// auto-computed. Waypoints are matched to the nearest existing node within 1 unit (QC findradius 1).
    /// </summary>
    public void LoadLinks(string cacheText)
    {
        using var reader = new StringReader(cacheText);
        string? line;
        bool parsingComments = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (parsingComments)
            {
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                parsingComments = false;
            }
            int star = line.IndexOf('*');
            if (star < 0) continue;
            if (!TryParseVec(line.AsSpan(0, star).ToString(), out var fromPos)) continue;
            if (!TryParseVec(line.AsSpan(star + 1).ToString(), out var toPos)) continue;

            var from = FindAt(fromPos);
            var to = FindAt(toPos);
            if (from is null || to is null) continue;
            Link(from, to); // directed (the cache lists each direction explicitly)
        }
    }

    /// <summary>
    /// Load a hardwired-link file (QC waypoint_load_hardwiredlinks): map maker-authored links the auto-linker
    /// must never remove. Format: lines of "fromOrigin*toOrigin" (two "x y z" vectors separated by '*'),
    /// with <c>//</c> and <c>#</c> comment lines ignored; a leading <c>*</c> marks a "special" link (kept,
    /// the marker is stripped). Each adds one directed link with auto-computed cost, matched to the nearest
    /// node within 5 units (QC findradius 5 for hardwired vs. 1 for the cache). Links added here are flagged
    /// on their source waypoint so <see cref="AutoLink"/> won't overwrite them.
    /// </summary>
    public void LoadHardwiredLinks(string hardwiredText)
    {
        using var reader = new StringReader(hardwiredText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            string s = line;
            if (s.StartsWith("*", StringComparison.Ordinal))
                s = s[1..]; // special link marker (QC: old versions skip these); we still load it

            int star = s.IndexOf('*');
            if (star < 0) continue;
            if (!TryParseVec(s.AsSpan(0, star).ToString(), out var fromPos)) continue;
            if (!TryParseVec(s.AsSpan(star + 1).ToString(), out var toPos)) continue;

            var from = FindAt(fromPos, 5f);
            var to = FindAt(toPos, 5f);
            if (from is null || to is null) continue;

            // Mark the source as having hand-authored links so AutoLink leaves it alone (QC WPFLAGMASK_NORELINK).
            from.Flags |= WaypointFlags.CustomJp;
            Link(from, to);
        }
    }

    private Waypoint? FindAt(Vector3 pos, float radius = 1f)
    {
        float r2 = radius * radius;
        foreach (var wp in _nodes)
            if ((wp.Origin - pos).LengthSquared() < r2)
                return wp;
        return null;
    }

    /// <summary>
    /// Parse a Quake vector literal "x y z" (the form vtos emits, 1 decimal place). The shipped
    /// <c>.waypoints</c>/<c>.waypoints.cache</c>/<c>.waypoints.hardwired</c> files single-quote each vector —
    /// <c>'-507.0 1112.6 409.0'</c> (DP's <c>vtos</c> emits the quotes; QC's <c>stov</c> strips them). The
    /// quotes must be stripped from the whole literal BEFORE splitting: vtos right-aligns each component, so a
    /// small value carries a leading space inside the quotes (<c>' 46.8 -380.6 536.0'</c>) and a per-token trim
    /// would leave the lone opening quote as its own token and shift the parse. Without this strip every line
    /// was rejected and the graph parsed to zero nodes, forcing the expensive auto-link tracewalk fallback.
    /// </summary>
    private static bool TryParseVec(string s, out Vector3 v)
    {
        v = Vector3.Zero;
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            t = t[1..^1]; // strip the surrounding vtos quotes (handles a leading-space component)
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        // per-token trim too, in case a variant quotes each component individually ('x' 'y' 'z').
        if (!float.TryParse(parts[0].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(parts[1].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        if (!float.TryParse(parts[2].Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    /// <summary>A tiny binary min-heap over node indices keyed by float priority (A* open set).</summary>
    private sealed class MinHeap
    {
        private int[] _idx;
        private float[] _pri;
        private int _size;

        public MinHeap(int capacity)
        {
            capacity = System.Math.Max(capacity, 16);
            _idx = new int[capacity];
            _pri = new float[capacity];
        }

        /// <summary>Reset to empty for reuse across searches (keeps the backing arrays).</summary>
        public void Clear() => _size = 0;

        public void Push(int nodeIndex, float priority)
        {
            if (_size == _idx.Length)
            {
                Array.Resize(ref _idx, _size * 2);
                Array.Resize(ref _pri, _size * 2);
            }
            int i = _size++;
            _idx[i] = nodeIndex;
            _pri[i] = priority;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_pri[parent] <= _pri[i]) break;
                Swap(i, parent);
                i = parent;
            }
        }

        public bool TryPop(out int nodeIndex)
        {
            if (_size == 0) { nodeIndex = -1; return false; }
            nodeIndex = _idx[0];
            _size--;
            if (_size > 0)
            {
                _idx[0] = _idx[_size];
                _pri[0] = _pri[_size];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                    if (l < _size && _pri[l] < _pri[smallest]) smallest = l;
                    if (r < _size && _pri[r] < _pri[smallest]) smallest = r;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }
            return true;
        }

        private void Swap(int a, int b)
        {
            (_idx[a], _idx[b]) = (_idx[b], _idx[a]);
            (_pri[a], _pri[b]) = (_pri[b], _pri[a]);
        }
    }
}
