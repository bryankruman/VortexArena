using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Game.Client;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The networked entity → renderer bridge — the Godot successor to CSQC's <c>CSQC_Ent_Update</c> /
/// <c>CSQCModel_Hook_PreUpdate</c> dispatch loop (lib/csqcmodel). <see cref="ClientNet"/> decodes the
/// delta-compressed snapshot into a per-entity <see cref="NetEntityState"/> table and interpolates each
/// remote entity's pose; this node, once per frame, reads that table and drives the actual render
/// representation in <see cref="ClientWorld"/>:
///
/// <list type="bullet">
///   <item><b>Player / Generic / Gib</b> → an <c>EntityNode</c> + <see cref="ModelAnimator"/> whose animation
///         is <b>frame-driven</b> by the networked <see cref="NetEntityState.Frame"/> (CSQCMODEL_AUTOUPDATE),
///         which is the gap this closes — remote players/monsters now play their server-computed frame instead
///         of the local movement heuristic.</item>
///   <item><b>Item</b> → a static/bobbing model node (no frame playback).</item>
///   <item><b>Projectile</b> → the <see cref="ProjectileRenderer"/> (trail/scale/spin from the catalog).</item>
///   <item><b>ViewModel</b> → the held-weapon entity, attached to its owner (see <see cref="ViewEntityRenderer"/>).</item>
///   <item><b>Nameplate</b> → a lightweight pose-only entity (radar/nameplate; no world model).</item>
/// </list>
///
/// It keeps one reusable proxy <see cref="Entity"/> per net id (the sim is Godot-free, so the renderer wants
/// an <see cref="Entity"/> to bind to). The proxy is mutated each frame from the decoded state + the
/// interpolated pose, then handed to the renderer — exactly how the QC client filled a CSQCModel entity from
/// the read functions and let the draw path consume it.
///
/// Model identity: the wire carries a numeric <see cref="NetEntityState.ModelIndex"/> (a precache slot), not a
/// classname. <see cref="ResolveModelIndex"/> (host-provided, backed by the precache table) maps it to a
/// classname/model string so projectiles classify correctly and <c>ClientWorld.ModelResolver</c> can load the
/// mesh; without it, entities fall back to kind-derived names (still renders, just less specific).
/// </summary>
public sealed partial class ClientEntityView : Node
{
    private readonly ClientNet _net;
    private readonly ClientWorld _render;
    private readonly ViewEntityRenderer _viewEntities;

    // One reusable proxy entity per net id (mutated in place each frame, like a CSQCModel slot).
    private readonly Dictionary<int, Entity> _proxies = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _stale = new();

    /// <summary>
    /// Resolve a networked model index (precache slot) to a classname + model name, so the renderer can pick
    /// the right projectile type / load the right mesh. Host-provided (precache table). When null, entities
    /// use a generic kind-derived classname.
    /// </summary>
    public Func<int, (string ClassName, string Model)>? ResolveModelIndex { get; set; }

    /// <summary>
    /// Builds the world model for a third-person carried weapon (a weapon registry id → a fresh scene node),
    /// host-wired to the asset pipeline. Forwarded to the <see cref="ViewEntityRenderer"/>; when null the
    /// renderer falls back to a placeholder box.
    /// </summary>
    public Func<int, Node3D?>? WeaponModelFactory
    {
        get => _viewEntities.WeaponModelFactory;
        set => _viewEntities.WeaponModelFactory = value;
    }

    public ClientEntityView(ClientNet net, ClientWorld render)
    {
        _net = net ?? throw new ArgumentNullException(nameof(net));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _viewEntities = new ViewEntityRenderer(render);
    }

    public override void _Process(double delta)
    {
        if (!_net.Accepted)
            return;

        // Interpolate remote entities at the latest server time (a one-snapshot delay falls out of the lerp).
        float now = _net.LatestServerTime;
        _seen.Clear();

        foreach (int id in _net.RemoteIds)
        {
            if (id == _net.LocalNetId)
                continue;
            if (!_net.TryGetRemoteState(id, out NetEntityState s))
                continue;

            // Interpolated pose (origin + blended angles); fall back to the raw state if no interp yet.
            if (!_net.SampleRemote(id, now, out NVec3 origin, out NVec3 angles))
            {
                origin = s.Origin;
                angles = s.Angles;
            }

            _seen.Add(id);
            DriveEntity(id, s, origin, angles);
        }

        CullDeparted();
        _viewEntities.Process();
    }

    // =====================================================================================
    //  Per-entity drive
    // =====================================================================================

    private void DriveEntity(int id, in NetEntityState s, NVec3 origin, NVec3 angles)
    {
        Entity e = GetOrCreateProxy(id);

        // Fill the proxy from the decoded state + interpolated pose (the CSQCModel property copy).
        e.Origin = origin;
        e.OldOrigin = origin;
        e.Angles = angles;
        e.Velocity = s.Velocity;
        e.Frame = s.Frame;
        e.Skin = s.Skin;
        e.ModelIndex = s.ModelIndex;
        e.Effects = s.Effects;
        e.Health = s.Health;
        e.Team = s.Colormap;
        e.ActiveWeaponId = s.Weapon;
        // QC ITS_EXPIRING (item snapshot flag): drives ClientWorld's loot despawn animation. Refreshed every
        // frame so it tracks the networked status (only ever set by the server on an expiring loot item).
        e.ItemExpiringFx = (s.Flags & NetEntityFlags.ItemExpiring) != 0;
        // QC !ITS_AVAILABLE (item snapshot flag): a picked-up item awaiting respawn → ClientWorld renders it as
        // the cl_ghost_items fade. Only ever set by the server on an unavailable item, so non-items stay available.
        e.ItemAvailable = (s.Flags & NetEntityFlags.ItemGhost) == 0;
        // QC ITS_ANIMATE1/2 (item bob+spin class): refreshed each frame from the snapshot. ANIMATE1 takes
        // priority (matches the client's if/else if). EntityNode reads this to drive the float + rotation.
        e.ItemAnimate = (s.Flags & NetEntityFlags.ItemAnimate1) != 0 ? (byte)1
                      : (s.Flags & NetEntityFlags.ItemAnimate2) != 0 ? (byte)2 : (byte)0;
        // QC FL_DUCKED: a remote player's crouch drives LocomotionBlend (duck legs) + the lowered hull/nameplate.
        e.IsDucked = (s.Flags & NetEntityFlags.Crouched) != 0;
        ApplyIdentity(e, s);

        switch (s.Kind)
        {
            case NetEntityKind.Projectile:
                // ClientWorld.IsProjectile routes this to the ProjectileRenderer (catalog-classified).
                _render.OnEntityUpdate(e);
                break;

            case NetEntityKind.ViewModel:
                // The local player's own held weapon is the first-person ViewModel, not a world entity — only
                // render OTHER players' carried weapons (third-person attachment).
                if (s.Owner != _net.LocalNetId)
                    _viewEntities.Update(e, s);
                break;

            case NetEntityKind.Nameplate:
                // Pose-only (radar/nameplate). No world model; the HUD/radar reads the state separately.
                break;

            case NetEntityKind.Player:
                // Animated networked actor + its held-weapon view-entity (wepent) attached third-person.
                _render.OnEntityUpdate(e, frameDriven: true);
                _viewEntities.Update(e, s);
                break;

            case NetEntityKind.Generic:
            case NetEntityKind.Gib:
                // Animated networked actors: drive the model frame straight from the network (autoupdate).
                _render.OnEntityUpdate(e, frameDriven: true);
                break;

            case NetEntityKind.Item:
            default:
                // Static/bobbing pickups: render the model, no frame playback.
                _render.OnEntityUpdate(e, frameDriven: false);
                break;
        }
    }

    /// <summary>Set the proxy's classname/model from the precache resolver, or a kind-derived default.</summary>
    private void ApplyIdentity(Entity e, in NetEntityState s)
    {
        // Prefer the networked model NAME (QC .model): players/bots carry their playermodel path so the client
        // loads the skeletal IQM directly, without a shared precache-index table (which wouldn't survive a real
        // client/server split). ModelIndex stays available as the fallback below.
        if (!string.IsNullOrEmpty(s.Model))
            e.Model = s.Model;

        if (ResolveModelIndex is not null && s.ModelIndex > 0)
        {
            (string className, string model) = ResolveModelIndex(s.ModelIndex);
            if (!string.IsNullOrEmpty(className)) e.ClassName = className;
            if (!string.IsNullOrEmpty(model)) e.Model = model;
            if (!string.IsNullOrEmpty(e.ClassName)) return;
        }

        // Kind-derived fallback (enough for ClientWorld.IsProjectile routing + a placeholder model).
        e.ClassName = s.Kind switch
        {
            NetEntityKind.Projectile => "projectile",
            NetEntityKind.Player => "player",
            NetEntityKind.Item => "item",
            NetEntityKind.Gib => "gib",
            NetEntityKind.ViewModel => "weaponentity",
            NetEntityKind.Nameplate => "nameplate",
            _ => e.ClassName,
        };
    }

    private Entity GetOrCreateProxy(int id)
    {
        if (!_proxies.TryGetValue(id, out Entity? e))
        {
            e = new Entity { Index = id };
            _proxies[id] = e;
        }
        return e;
    }

    /// <summary>Remove proxies (and their render nodes) for ids the server stopped sending this frame.</summary>
    private void CullDeparted()
    {
        _stale.Clear();
        foreach (KeyValuePair<int, Entity> kv in _proxies)
            if (!_seen.Contains(kv.Key))
                _stale.Add(kv.Key);

        for (int i = 0; i < _stale.Count; i++)
        {
            int id = _stale[i];
            Entity e = _proxies[id];
            _viewEntities.Remove(id);
            _render.OnEntityRemove(id, e.Origin);
            _proxies.Remove(id);
        }
    }

    public override void _ExitTree() => _viewEntities.Clear();
}
