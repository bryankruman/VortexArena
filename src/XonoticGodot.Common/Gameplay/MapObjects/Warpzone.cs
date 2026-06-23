using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The seamless-portal transform — the deterministic core of <c>lib/warpzone</c> (common.qc
/// <c>WarpZone_SetUp</c> / <c>WarpZone_TransformOrigin</c> / <c>_TransformVelocity</c> / <c>_TransformAngles</c>).
/// A warpzone glues two surfaces (an IN plane and an OUT plane) so an entity crossing the IN surface emerges
/// from the OUT surface with its position, velocity and view rotated by the relative plane transform — a 180°
/// turn (you exit the OTHER way) composed with the rotation between the two planes. This preserves momentum
/// so movement is continuous (strafe-jumps carry through).
///
/// The transform is <c>R·v</c> for directions and <c>outOrigin + R·(v − inOrigin)</c> for positions, where R
/// maps the IN basis to the 180°-flipped OUT basis (inFwd→−outFwd, inRight→−outRight, inUp→outUp). This is
/// the QC <c>warpzone_transform</c>/<c>warpzone_shift</c> pair expressed with explicit forward/right/up bases
/// (no AnglesTransform vector encoding). The seamless VIEW rendering through the portal is a client concern.
/// </summary>
public readonly struct WarpzoneTransform
{
    private readonly Vector3 _inFwd, _inRight, _inUp;
    private readonly Vector3 _outFwd, _outRight, _outUp;
    private readonly Vector3 _inOrigin, _outOrigin;
    public readonly bool Valid;

    public Vector3 InOrigin => _inOrigin;
    public Vector3 OutOrigin => _outOrigin;
    public Vector3 InForward => _inFwd;
    public Vector3 OutForward => _outFwd;

    /// <summary>
    /// QC <c>WarpZone_SetUp(e, my_org, my_ang, other_org, other_ang)</c>: build the transform from the IN plane
    /// (origin + Euler angles, forward = surface normal toward the approaching entity) to the OUT plane.
    /// </summary>
    public WarpzoneTransform(Vector3 inOrigin, Vector3 inAngles, Vector3 outOrigin, Vector3 outAngles)
    {
        QMath.AngleVectors(inAngles, out _inFwd, out _inRight, out _inUp);
        QMath.AngleVectors(outAngles, out _outFwd, out _outRight, out _outUp);
        _inOrigin = inOrigin;
        _outOrigin = outOrigin;
        Valid = true;
    }

    /// <summary>R·v — rotate a direction/velocity through the portal (QC WarpZone_TransformVelocity).</summary>
    public Vector3 Rotate(Vector3 v)
    {
        float a = Vector3.Dot(_inFwd, v), b = Vector3.Dot(_inRight, v), c = Vector3.Dot(_inUp, v);
        // map the IN basis to the 180°-flipped OUT basis: inFwd→−outFwd, inRight→−outRight, inUp→outUp.
        return -_outFwd * a - _outRight * b + _outUp * c;
    }

    /// <summary>QC WarpZone_TransformOrigin: outOrigin + R·(v − inOrigin).</summary>
    public Vector3 TransformOrigin(Vector3 v) => _outOrigin + Rotate(v - _inOrigin);

    /// <summary>QC WarpZone_TransformVelocity: R·v (no translation).</summary>
    public Vector3 TransformVelocity(Vector3 v) => Rotate(v);

    /// <summary>QC WarpZone_TransformAngles: rotate the view forward vector through the portal and re-derive angles.</summary>
    public Vector3 TransformAngles(Vector3 angles)
    {
        QMath.AngleVectors(angles, out Vector3 fwd, out _, out _);
        return QMath.FixedVecToAngles(Rotate(fwd));
    }

    /// <summary>The inverse transform (the OUT→IN direction), for tracing/queries the other way.</summary>
    public WarpzoneTransform Inverse() => new(_outOrigin, AnglesOf(_outFwd, _outUp), _inOrigin, AnglesOf(_inFwd, _inUp));

    private static Vector3 AnglesOf(Vector3 fwd, Vector3 up) => QMath.FixedVecToAngles(fwd); // roll from up not modeled

    /// <summary>
    /// [T45] Decompose this transform into its affine form <c>p → R·p + shift</c> for the trace/radius
    /// recursion accumulator (<see cref="WarpzoneTransformChain"/>). The rotation rows are emitted so
    /// that <c>(R·v)[k] == Dot(rowK, v)</c>; <paramref name="shift"/> is <c>outOrigin − R·inOrigin</c>. This is
    /// the explicit-basis equivalent of QC's <c>warpzone_transform</c>/<c>warpzone_shift</c> pair
    /// (lib/warpzone/common.qc), composed by the chain exactly as <c>WarpZone_Accumulator_AddTransform</c> does.
    /// </summary>
    public void GetAffine(out Vector3 row0, out Vector3 row1, out Vector3 row2, out Vector3 shift)
    {
        // Rotate(v) = -outFwd*(inFwd·v) - outRight*(inRight·v) + outUp*(inUp·v) (see Rotate above); so the k-th
        // output component is Dot(rowK, v) with rowK = -outFwd[k]*inFwd - outRight[k]*inRight + outUp[k]*inUp.
        row0 = -_outFwd.X * _inFwd - _outRight.X * _inRight + _outUp.X * _inUp;
        row1 = -_outFwd.Y * _inFwd - _outRight.Y * _inRight + _outUp.Y * _inUp;
        row2 = -_outFwd.Z * _inFwd - _outRight.Z * _inRight + _outUp.Z * _inUp;
        // shift = outOrigin - R·inOrigin (so TransformPoint(inOrigin) == outOrigin), matching TransformOrigin.
        Vector3 rotIn = new(
            Vector3.Dot(row0, _inOrigin),
            Vector3.Dot(row1, _inOrigin),
            Vector3.Dot(row2, _inOrigin));
        shift = _outOrigin - rotIn;
    }
}

/// <summary>
/// A single seamless portal in the world — the C# successor to the QuakeC <c>trigger_warpzone</c> edict
/// (lib/warpzone/server.qc). It pairs an IN trigger volume with the transform to its linked OUT plane. When an
/// entity moving INTO the IN surface touches it, <see cref="Warpzones.Teleport"/> warps it to the exit with its
/// momentum preserved. Linked by <see cref="Entity.Target"/>/<see cref="Entity.TargetName"/> (the other zone),
/// like the QC warpzone target wiring; <see cref="WarpzoneManager"/> resolves the pairs.
/// </summary>
public sealed class Warpzone
{
    /// <summary>The trigger entity (the IN surface volume), or null in a headless test that uses the POJO transform.</summary>
    public Entity? Trigger;

    /// <summary>The transform from this zone's IN plane to its linked exit (set when the pair is linked).</summary>
    public WarpzoneTransform Transform;

    /// <summary>QC warpzone_origin/_angles — the IN plane this zone presents.</summary>
    public Vector3 InOrigin, InAngles;

    /// <summary>The linked exit plane (the partner zone's IN plane), resolved by <see cref="WarpzoneManager.Link"/>.</summary>
    public Vector3 OutOrigin, OutAngles;

    /// <summary>The target name linking this zone to its partner (QC .target / .targetname).</summary>
    public string TargetName = "", Target = "";

    /// <summary>QC <c>.enemy</c> — the linked partner zone (the OUT side). Set when the pair is linked; used to fire
    /// the partner's targets on a crossing (QC <c>SUB_UseTargets_SkipTargets(this.enemy, ...)</c>).</summary>
    public Warpzone? Partner;

    public bool Linked => Transform.Valid;
}

/// <summary>
/// The warpzone manager — holds the world's warpzones, links each to its partner (by targetname), and performs
/// the seamless teleport when an entity crosses a zone. The C# successor to lib/warpzone's server-side
/// WarpZone_Teleport + the trigger touch. Godot-free: operates on <see cref="Entity"/> through the facade.
/// </summary>
public sealed class WarpzoneManager
{
    private readonly List<Warpzone> _zones = new();
    public IReadOnlyList<Warpzone> Zones => _zones;

    /// <summary>
    /// QC <c>.warpzone_teleport_finishtime</c> — the per-entity same-frame re-teleport guard (server.qc:186). After a
    /// crossing the entity emerges potentially inside/touching the PARTNER zone (or re-touches this one as the touch
    /// pass re-fires); a teleport is suppressed while <c>time &lt;= finishtime</c> so a single cross can't ping-pong
    /// between the two zones in one frame. Keyed by entity (QC stores it on the edict); pruned lazily when freed.
    /// </summary>
    private readonly Dictionary<Entity, float> _teleportFinishTime = new();

    /// <summary>Register a warpzone (QC trigger_warpzone spawnfunc → IL_PUSH(g_warpzones)).</summary>
    public Warpzone Add(Warpzone wz) { _zones.Add(wz); return wz; }

    /// <summary>
    /// QC WarpZone_SetUp wiring: link each zone to its <see cref="Warpzone.Target"/> partner, computing the
    /// IN→partner-IN transform. A zone whose partner is missing stays unlinked (inert). Call after all zones spawn.
    /// </summary>
    public void Link()
    {
        // [T45] QC sv_warpzone_allow_selftarget (lib/warpzone/server.qc WarpZone_InitStep_FindTarget, default 0):
        // a zone never selects ITSELF as its partner unless the cvar is set. Read once per link pass.
        bool allowSelfTarget = Api.Services is not null && Api.Cvars.GetFloat("sv_warpzone_allow_selftarget") != 0f;

        // Forward pass: each zone carrying a .target links to that partner (the symmetric common case where
        // both zones name each other).
        foreach (Warpzone wz in _zones)
        {
            Warpzone? partner = FindByTargetName(wz.Target, allowSelfTarget ? null : wz);
            if (partner is not null) LinkOneWay(wz, partner);
        }
        // Reverse pass — QC's two-way `this.enemy = e2; e2.enemy = this`: a zone that carries no .target but IS
        // targeted by another still links, pointing back at whoever targets it (asymmetric maps).
        foreach (Warpzone wz in _zones)
        {
            if (wz.Linked || string.IsNullOrEmpty(wz.TargetName)) continue;
            foreach (Warpzone other in _zones)
                if (!ReferenceEquals(other, wz) && other.Target == wz.TargetName) { LinkOneWay(wz, other); break; }
        }
    }

    private static void LinkOneWay(Warpzone wz, Warpzone partner)
    {
        wz.OutOrigin = partner.InOrigin;
        wz.OutAngles = partner.InAngles;
        wz.Transform = new WarpzoneTransform(wz.InOrigin, wz.InAngles, partner.InOrigin, partner.InAngles);
        wz.Partner = partner; // QC this.enemy = partner — for the partner-zone target relay on a crossing
    }

    private Warpzone? FindByTargetName(string name, Warpzone? excludeSelf = null)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (Warpzone wz in _zones)
        {
            if (ReferenceEquals(wz, excludeSelf)) continue; // QC !sv_warpzone_allow_selftarget: never self-link
            if (wz.TargetName == name) return wz;
        }
        return null;
    }

    /// <summary>
    /// QC <c>WarpZone_Touch</c> + <c>WarpZone_Teleport</c>: warp <paramref name="e"/> through <paramref name="wz"/> —
    /// transform its origin, velocity, angles and view, momentum preserved. The crossing is gated by the QC
    /// plane-side test <c>WarpZone_PlaneDist(this, origin + view_ofs) &gt;= 0 → skip</c> (server.qc:193): an entity is
    /// only teleported once it is on the FAR (negative) side of the IN plane — i.e. it has actually crossed the
    /// surface — regardless of its velocity sign, exactly like Base (an entity loitering just past the plane with
    /// outward/zero velocity is still teleported). The QC same-frame re-teleport guard
    /// (<c>warpzone_teleport_finishtime</c>) prevents a ping-pong between the paired zones in one frame. Returns true
    /// if the teleport happened.
    /// </summary>
    public bool Teleport(Entity e, Warpzone wz)
    {
        if (!wz.Linked) return false;

        // QC server.qc:186 — already teleported this frame (the entity emerges in/near the partner zone and would
        // immediately re-touch). Suppress until the guard expires.
        float now = MapMover.Now();
        if (_teleportFinishTime.TryGetValue(e, out float finish))
        {
            if (e.IsFreed) _teleportFinishTime.Remove(e);   // don't pin a freed edict (QC stores it on the entity)
            else if (now <= finish) return false;
        }

        // QC server.qc:193 — WarpZone_PlaneDist(this, toucher.origin + toucher.view_ofs) >= 0 → wrong side, don't
        // teleport yet. PlaneDist = (point - warpzone_origin)·warpzone_forward; the entity must be PAST the plane
        // (negative side) before it warps. This is the plane-side test, NOT a velocity-direction gate.
        Vector3 point = e.Origin + e.ViewOfs;
        if (Vector3.Dot(point - wz.Transform.InOrigin, wz.Transform.InForward) >= 0f)
            return false; // not yet across the seam

        Vector3 newOrigin = wz.Transform.TransformOrigin(e.Origin);
        e.Velocity = wz.Transform.TransformVelocity(e.Velocity);
        e.Angles = wz.Transform.TransformAngles(e.Angles);
        e.AVelocity = wz.Transform.TransformVelocity(e.AVelocity);
        if (Api.Services is not null) Api.Entities.SetOrigin(e, newOrigin);
        else e.Origin = newOrigin;
        e.OldOrigin = newOrigin; // QC: cancel interpolation across the seam (a teleport, not a slide)

        // QC server.qc:355 — stamp the same-frame re-teleport guard so the partner-zone touch (which the entity now
        // overlaps after emerging) doesn't immediately warp it straight back. Base uses
        // time + PHYS_INPUT_FRAMETIME - dt as a back-teleport guard; with no per-move dt here we hold for one frame.
        _teleportFinishTime[e] = now + MapMover.FrameTime();

        // QC WarpZone_Touch fires the targets of BOTH this zone and its partner (this.enemy) on a crossing, with the
        // QC skip masks: this → SkipTargets BIT(1)|BIT(3); this.enemy → BIT(1)|BIT(2) (server.qc:218-219). A map can
        // chain a relay/sound/door off either side of a warpzone. No-op when neither trigger carries a target.
        if (wz.Trigger is { } trig)
            MapMover.UseTargetsEx(trig, e, trig, preventReuse: false,
                skipTargets: MapMover.SkipTarget1 | MapMover.SkipTarget3);
        if (wz.Partner?.Trigger is { } partnerTrig)
            MapMover.UseTargetsEx(partnerTrig, e, e, preventReuse: false,
                skipTargets: MapMover.SkipTarget1 | MapMover.SkipTarget2);
        return true;
    }

    /// <summary>
    /// QC trigger_warpzone spawnfunc: create + register a warpzone from explicit IN/OUT plane parameters and a
    /// touch volume. The touch warps any crossing player/projectile. <paramref name="target"/>/<paramref name="targetName"/>
    /// link it to its partner (call <see cref="Link"/> after all zones spawn). Returns the zone.
    /// </summary>
    public Warpzone Spawn(Vector3 inOrigin, Vector3 inAngles, string targetName, string target,
        Vector3 mins, Vector3 maxs)
    {
        var wz = new Warpzone { InOrigin = inOrigin, InAngles = inAngles, TargetName = targetName, Target = target };
        SpawnTriggerFor(wz, mins, maxs);
        return Add(wz);
    }

    /// <summary>
    /// QC <c>WarpZone_InitStep_UpdateTransform</c>: derive a warpzone's IN plane (origin + angles) from the
    /// geometry of its brush model via the <c>getsurface*</c> builtins — area-weighted average of every
    /// (non-trigger) triangle's normal and centroid. This is the auto-orientation a mapper gets by just
    /// drawing a planar <c>trigger_warpzone</c> brush (no explicit angle). When <paramref name="aiment"/> (a
    /// <c>target_position</c>) is given, its origin/angles seed the result and the brush plane only corrects
    /// the orientation. Returns ok=false when the brush is non-planar / has no usable surface (the QC errors).
    /// </summary>
    public (Vector3 origin, Vector3 angles, bool ok) DerivePlaneFromBrush(Entity brush, Entity? aiment = null)
    {
        if (Api.Services is null) return (Vector3.Zero, Vector3.Zero, false);
        ISurfaceService surf = Api.Surfaces;

        Vector3 org = brush.Origin;
        if (org == Vector3.Zero) org = 0.5f * (brush.Mins + brush.Maxs);

        Vector3 norm = Vector3.Zero, point = Vector3.Zero;
        float area = 0f;
        for (int i_s = 0; ; i_s++)
        {
            string tex = surf.GetSurfaceTexture(brush, i_s);
            if (string.IsNullOrEmpty(tex)) break;                 // past the last surface (QC `if (!tex) break`)
            if (tex == "textures/common/trigger" || tex == "trigger") continue;
            int nt = surf.GetSurfaceNumTriangles(brush, i_s);
            for (int i_t = 0; i_t < nt; i_t++)
            {
                Vector3 tri = surf.GetSurfaceTriangle(brush, i_s, i_t);
                Vector3 a = surf.GetSurfacePoint(brush, i_s, (int)tri.X);
                Vector3 b = surf.GetSurfacePoint(brush, i_s, (int)tri.Y);
                Vector3 c = surf.GetSurfacePoint(brush, i_s, (int)tri.Z);
                Vector3 n = Vector3.Cross(c - a, b - a);
                float w = n.Length();
                area += w;
                norm += n;
                point += w * (a + b + c);
            }
        }

        if (area > 0f)
        {
            norm *= 1f / area;
            point *= 1f / (3f * area);
            if (norm.Length() < 0.99f) area = 0f;                  // surfaces don't agree on a plane → non-planar
            norm = QMath.Normalize(norm);
        }

        Vector3 ang = Vector3.Zero;
        if (aiment is not null)
        {
            org = aiment.Origin;
            ang = aiment.Angles;
            if (area > 0f)
            {
                org -= Vector3.Dot(org - point, norm) * norm;       // project the target onto the plane
                QMath.AngleVectors(ang, out Vector3 fwd, out _, out Vector3 up);
                if (Vector3.Dot(norm, fwd) < 0f) norm = -norm;       // face out of the trigger
                ang = QMath.FixedVecToAngles2(norm, up);             // keep roll, aim exactly against the plane
            }
        }
        else if (area > 0f)
        {
            org = point;
            ang = QMath.FixedVecToAngles(norm);
        }
        else
        {
            return (Vector3.Zero, Vector3.Zero, false);              // QC: error("cannot infer origin/angles")
        }

        return (org, ang, true);
    }

    /// <summary>
    /// Spawn a warpzone whose IN plane is auto-derived from <paramref name="brush"/>'s geometry (a planar
    /// <c>trigger_warpzone</c> brush model) via <see cref="DerivePlaneFromBrush"/>. The brush itself becomes the
    /// touch volume. <paramref name="target"/>/<paramref name="targetName"/> link it to its partner — call
    /// <see cref="Link"/> after all zones spawn. Returns null when the plane can't be inferred.
    /// </summary>
    public Warpzone? SpawnFromBrush(Entity brush, string targetName, string target, Entity? aiment = null)
    {
        (Vector3 org, Vector3 ang, bool ok) = DerivePlaneFromBrush(brush, aiment);
        if (!ok) return null;
        var wz = new Warpzone { InOrigin = org, InAngles = ang, TargetName = targetName, Target = target, Trigger = brush };
        brush.ClassName = "trigger_warpzone";
        brush.Solid = Solid.Trigger;
        brush.Touch = (self, other) => { if (!other.IsFreed && other.Solid != Solid.Not) Teleport(other, wz); };
        return Add(wz);
    }

    /// <summary>Create the touch-volume entity for a zone (QC the warpzone brush trigger), if a facade is wired.</summary>
    private void SpawnTriggerFor(Warpzone wz, Vector3 mins, Vector3 maxs)
    {
        if (Api.Services is null) return;
        Entity t = Api.Entities.Spawn();
        t.ClassName = "trigger_warpzone";
        t.Solid = Solid.Trigger;
        t.MoveType = MoveType.None;
        Api.Entities.SetSize(t, mins, maxs);
        Api.Entities.SetOrigin(t, wz.InOrigin);
        t.Touch = (self, other) => { if (!other.IsFreed && other.Solid != Solid.Not) Teleport(other, wz); };
        wz.Trigger = t;
    }

    // Porto portals awaiting their partner (keyed by owner + the shot's portal id).
    private readonly Dictionary<(Entity?, int), Warpzone> _pendingPorto = new();
    private static readonly Vector3 PortoMins = new(-48, -48, -48);
    private static readonly Vector3 PortoMaxs = new(48, 48, 48);

    /// <summary>
    /// QC Portal_SpawnIn/OutPortalAtTrace: realise a Porto-weapon portal as a warpzone. The plane forward is the
    /// wall's surface normal (so an entity entering emerges out of the partner). The in-portal is held pending
    /// until its matching out-portal lands; then the pair is linked two-way (you can walk through either side).
    /// Wire <c>Porto.PortalSpawner</c> to this on the host.
    /// </summary>
    public void PlacePortoPortal(Vector3 origin, Vector3 surfaceNormal, bool isInPortal, int portalId, Entity? owner)
    {
        Vector3 angles = QMath.FixedVecToAngles(surfaceNormal); // forward = the wall normal
        var wz = new Warpzone { InOrigin = origin, InAngles = angles, TargetName = $"porto_{portalId}_{(isInPortal ? "in" : "out")}" };
        SpawnTriggerFor(wz, PortoMins, PortoMaxs);
        Add(wz);

        var key = (owner, portalId);
        if (isInPortal)
        {
            _pendingPorto[key] = wz;
            return;
        }
        if (_pendingPorto.TryGetValue(key, out Warpzone? inZone))
        {
            LinkPair(inZone, wz); // two-way Porto portal
            _pendingPorto.Remove(key);
        }
    }

    /// <summary>Link two warpzones into a two-way portal (each transforms toward the other's IN plane).</summary>
    public void LinkPair(Warpzone a, Warpzone b)
    {
        a.OutOrigin = b.InOrigin; a.OutAngles = b.InAngles;
        a.Transform = new WarpzoneTransform(a.InOrigin, a.InAngles, b.InOrigin, b.InAngles);
        a.Partner = b;
        b.OutOrigin = a.InOrigin; b.OutAngles = a.InAngles;
        b.Transform = new WarpzoneTransform(b.InOrigin, b.InAngles, a.InOrigin, a.InAngles);
        b.Partner = a;
    }

    // =============================================================================================
    //  Map-entity warpzones — QC spawnfunc(trigger_warpzone) / spawnfunc(trigger_warpzone_position)
    //  registers each, then the deferred WarpZone_StartFrame init pass derives planes + links pairs.
    // =============================================================================================
    private readonly List<Entity> _pendingZones = new();
    private readonly List<Entity> _pendingPositions = new();

    /// <summary>
    /// The static→instance bridge target: <see cref="WarpzoneSpawns.Sink"/> (wired by the host's Boot, mirroring
    /// <c>Porto.PortalSpawner</c>) routes each spawned <c>trigger_warpzone</c>/<c>_position</c> edict here, where
    /// it is dispatched by classname. Registration only — the plane/link are deferred to <see cref="InitMapZones"/>.
    /// </summary>
    public void OnMapEntity(Entity e)
    {
        if (e.ClassName == "trigger_warpzone_position") AddMapPosition(e);
        else AddMapZone(e);
    }

    /// <summary>QC <c>spawnfunc(trigger_warpzone)</c>: register the brush (resolve its inline model for the
    /// trigger volume + surface queries). The plane/link are finalized in <see cref="InitMapZones"/>.</summary>
    public void AddMapZone(Entity brush)
    {
        if (Api.Services is not null && !string.IsNullOrEmpty(brush.Model))
            Api.Entities.SetModel(brush, brush.Model); // real "*N" bounds (touch volume) + findable surfaces
        _pendingZones.Add(brush);
    }

    /// <summary>QC <c>spawnfunc(trigger_warpzone_position)</c>: the explicit-orientation helper; indexed so a
    /// zone can find it by targetname, stashed so <see cref="InitMapZones"/> can attach it as a zone's aiment.</summary>
    public void AddMapPosition(Entity pos)
    {
        MapMover.IndexRegister(pos);
        _pendingPositions.Add(pos);
    }

    /// <summary>
    /// QC <c>WarpZone_StartFrame</c> init (deferred to after every entity spawned): for each registered zone,
    /// resolve its explicit orientation (.aiment) — via <c>killtarget</c> (the zone names a target_position) or a
    /// <c>trigger_warpzone_position</c> whose <c>.target</c> names the zone — derive the IN plane from the brush
    /// geometry (<see cref="DerivePlaneFromBrush"/> / <c>getsurface*</c>), build the touch volume, then link the
    /// pairs (<see cref="Link"/>). Run once after <c>SpawnMapEntities</c>; clears the pending sets.
    /// </summary>
    public void InitMapZones()
    {
        foreach (Entity zone in _pendingZones)
        {
            Entity? aiment = ResolveAiment(zone);
            SpawnFromBrush(zone, zone.TargetName, zone.Target, aiment);
        }
        Link();
        _pendingZones.Clear();
        _pendingPositions.Clear();
    }

    /// <summary>The explicit-orientation entity for a zone (QC WarpZone_InitStep_FindOriginTarget + the
    /// trigger_warpzone_position reverse-attach), or null to orient purely from the brush plane.</summary>
    private Entity? ResolveAiment(Entity zone)
    {
        // mechanism 1 — killtarget: the zone names a target_position direction arrow (QC clears killtarget after).
        if (!string.IsNullOrEmpty(zone.KillTarget))
        {
            Entity? found = MapMover.FindFirstByTargetName(zone.KillTarget);
            zone.KillTarget = "";
            if (found is not null) return found;
        }
        // mechanism 2 — a trigger_warpzone_position whose .target names this zone.
        if (!string.IsNullOrEmpty(zone.TargetName))
            foreach (Entity pos in _pendingPositions)
                if (pos.Target == zone.TargetName) return pos;
        return null;
    }
}

/// <summary>
/// The map-entity spawnfuncs for warpzones (QC <c>spawnfunc(trigger_warpzone)</c> /
/// <c>spawnfunc(trigger_warpzone_position)</c>). Stateless like every map-object spawnfunc, so they reach the
/// match's instance <see cref="WarpzoneManager"/> through the static <see cref="Sink"/> — wired by the host's
/// Boot to <see cref="WarpzoneManager.OnMapEntity"/>, exactly as <c>Porto.PortalSpawner</c> bridges the Porto
/// weapon to the same manager.
/// </summary>
public static class WarpzoneSpawns
{
    /// <summary>Route a spawned warpzone edict to this match's manager. Null = no manager (e.g. unit tests).</summary>
    public static System.Action<Entity>? Sink;

    public static void TriggerWarpzoneSetup(Entity e) { e.ClassName = "trigger_warpzone"; Sink?.Invoke(e); }
    public static void TriggerWarpzonePositionSetup(Entity e) { e.ClassName = "trigger_warpzone_position"; Sink?.Invoke(e); }
}
