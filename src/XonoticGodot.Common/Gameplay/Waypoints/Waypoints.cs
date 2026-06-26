using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay.Waypoints;

/// <summary>
/// The waypoint-sprite registry — the C# port of QuakeC's <c>REGISTER_WAYPOINT</c> table
/// (Base/.../qcsrc/common/mutators/mutator/waypoints/all.inc). Each <see cref="WaypointDef"/> describes one
/// kind of objective marker: the on-screen text shown when there's no icon (<see cref="Text"/>), the HUD icon
/// sprite name when there is one (<see cref="Icon"/>, resolved <c>gfx/hud/&lt;skin&gt;/&lt;icon&gt;</c>), the
/// default tint (<see cref="Color"/>), the blink rate (<see cref="Blink"/>), and whether it shows on the radar
/// (<see cref="RadarIcon"/> &gt; 0 → <c>gfx/teamradar_icon_1</c>). The server picks which def a live waypoint
/// currently shows (state machine — e.g. flag base "home" vs "taken") and networks the def name; the client
/// looks the def up here to render the 3D in-world sprite + the radar icon.
/// </summary>
public readonly record struct WaypointDef(
    string Name, string Text, string Icon, Vector3 Color, float Blink, int RadarIcon);

/// <summary>
/// A waypoint as decoded on the CLIENT (the per-peer-filtered slice the server sent) — the data the 3D
/// in-world sprite renderer + the radar icon layer draw from. <see cref="Fade"/> is the server lifetime fade
/// (0..1); <see cref="Health"/> &lt; 0 means "no health bar"; <see cref="Helpme"/> &gt; 0 flags the "needing
/// help" flash. The client resolves the icon/text/blink from <see cref="WaypointRegistry"/> by
/// <see cref="SpriteName"/>.
/// </summary>
public readonly record struct WaypointNet(
    int Id, Vector3 Origin, int Team, string SpriteName, int RadarIcon, Vector3 Color,
    float Health, float Fade, float Helpme, float MaxDistance, bool Hideable);

/// <summary>QC <c>SPRITERULE_*</c> — which audience a waypoint is visible to (the server filters per peer).</summary>
public static class SpriteRule
{
    public const int Default = 0;     // teammates always; enemies per gametype rules
    public const int Teamplay = 1;    // only the owning team
    public const int Spectator = 2;   // only spectators (item respawn timers etc.)
}

/// <summary>QC's three deployed-waypoint owner-fields (<c>.waypointsprite_deployed_personal/_deployed_fixed/_attached</c>):
/// which slot a player-deployed waypoint occupies, so <c>waypoint_clear[_personal]</c> can scope its removal.</summary>
public enum DeployKind
{
    None = 0,     // an objective marker, not player-deployed
    Personal,     // waypointsprite_deployed_personal (only the owner sees it)
    Fixed,        // waypointsprite_deployed_fixed (here/danger pings)
    Attached,     // waypointsprite_attached (HELP ME, follows the owner)
}

/// <summary>The full WP_* registry (the subset the port wires; extend freely — adding a def is one line).</summary>
public static class WaypointRegistry
{
    private static readonly Vector3 Orange = new(1f, 0.5f, 0f);     // WP_REVIVING_COLOR / Assault / Race / Ons / Buff
    private static readonly Vector3 Cyan = new(0f, 1f, 1f);
    private static readonly Vector3 Green = new(0f, 1f, 0f);
    private static readonly Vector3 Tan = new(0.8f, 0.8f, 0f);
    private static readonly Vector3 White = new(1f, 1f, 1f);
    private static readonly Vector3 Red = new(1f, 0f, 0f);
    private static readonly Vector3 Magenta = new(1f, 0f, 1f);      // WP_Item
    private static readonly Vector3 FrozenCol = new(0.25f, 0.9f, 1f);
    private static readonly Vector3 NbBallCol = new(0.91f, 0.85f, 0.62f);
    private static readonly Vector3 SeekerCol = new(0.5f, 1f, 0f);
    private static readonly Vector3 ReturnCol = new(0f, 0.8f, 0.8f);

    private const int IconObjective = 1; // every RADARICON_* except NONE is 1 (the color distinguishes)

    private static Dictionary<string, WaypointDef> Build()
    {
        // Faithful 1:1 port of all.inc REGISTER_WAYPOINT(<name>, _(text), icon, color, blink). The fourth blink
        // arg defaults to 1 in QC and is the per-def base blink (OnsCPDefend 0.5 / OnsCPAttack 2 / Seeker 2);
        // spritelookupblinkvalue still overrides it for superweapon Weapon=2, Item=m_waypointblink, FlagReturn=2.
        var d = new Dictionary<string, WaypointDef>();
        void Add(string name, string text, string icon, Vector3 col, float blink = 1f, int radar = IconObjective)
            => d[name] = new WaypointDef(name, text, icon, col, blink, radar);

        // ---- player pings (no icon → text) ----
        Add("Waypoint", "Waypoint", "", Cyan);
        Add("Helpme", "Help me!", "", Orange);
        Add("Here", "Here", "", Green);
        Add("Danger", "DANGER", "", Orange);

        // ---- FreezeTag ----
        Add("Frozen", "Frozen!", "", FrozenCol);
        Add("Reviving", "Reviving", "", Orange);

        // ---- generic item (itemstime / pickups) ----
        Add("Item", "Item", "", Magenta);

        // ---- Race / CTS ----
        Add("RaceCheckpoint", "Checkpoint", "", Orange);
        Add("RaceFinish", "Finish", "", Orange);
        Add("RaceStart", "Start", "", Orange);
        Add("RaceStartFinish", "Start", "", Orange);

        // ---- Assault ----
        Add("AssaultDefend", "Defend", "as_defend", Orange);
        Add("AssaultDestroy", "Destroy", "as_destroy", Orange);
        Add("AssaultPush", "Push", "", Orange);

        // ---- CTF flags (icons; the home/taken/lost/carrying state machine resolves the def server-side) ----
        Add("FlagCarrier", "Flag carrier", "", Tan);
        Add("FlagBaseNeutral", "White base", "flag_neutral_taken", Tan);
        Add("FlagBaseRed", "Red base", "flag_red_taken", Tan);
        Add("FlagBaseBlue", "Blue base", "flag_blue_taken", Tan);
        Add("FlagBaseYellow", "Yellow base", "flag_yellow_taken", Tan);
        Add("FlagBasePink", "Pink base", "flag_pink_taken", Tan);
        Add("FlagDroppedNeutral", "Dropped flag", "flag_neutral_lost", White);
        Add("FlagDroppedRed", "Dropped flag", "flag_red_lost", White);
        Add("FlagDroppedBlue", "Dropped flag", "flag_blue_lost", White);
        Add("FlagDroppedYellow", "Dropped flag", "flag_yellow_lost", White);
        Add("FlagDroppedPink", "Dropped flag", "flag_pink_lost", White);
        Add("FlagCarrierEnemyNeutral", "Enemy carrier", "flag_neutral_carrying", Tan);
        Add("FlagCarrierEnemyRed", "Enemy carrier", "flag_red_carrying", Tan);
        Add("FlagCarrierEnemyBlue", "Enemy carrier", "flag_blue_carrying", Tan);
        Add("FlagCarrierEnemyYellow", "Enemy carrier", "flag_yellow_carrying", Tan);
        Add("FlagCarrierEnemyPink", "Enemy carrier", "flag_pink_carrying", Tan);
        // FlagReturn's all.inc blink is 1, but spritelookupblinkvalue overrides it to 2 (waypointsprites.qc:215);
        // that override is static (not wp_extra-dependent) so we bake it into the def here.
        Add("FlagReturn", "Return flag here", "", ReturnCol, 2f);

        // ---- Domination ----
        Add("DomNeut", "Control point", "dom_icon_neutral-highlighted", Cyan);
        Add("DomRed", "Control point", "dom_icon_red-highlighted", Cyan);
        Add("DomBlue", "Control point", "dom_icon_blue-highlighted", Cyan);
        Add("DomYellow", "Control point", "dom_icon_yellow-highlighted", Cyan);
        Add("DomPink", "Control point", "dom_icon_pink-highlighted", Cyan);

        // ---- Key Hunt ----
        Add("KeyDropped", "Dropped key", "kh_dropped", Cyan);
        Add("KeyCarrierFriend", "Key carrier", "", Green);
        Add("KeyCarrierFinish", "Run here", "", Cyan);
        Add("KeyCarrierRed", "Key carrier", "kh_red_carrying", Cyan);
        Add("KeyCarrierBlue", "Key carrier", "kh_blue_carrying", Cyan);
        Add("KeyCarrierYellow", "Key carrier", "kh_yellow_carrying", Cyan);
        Add("KeyCarrierPink", "Key carrier", "kh_pink_carrying", Cyan);

        // ---- Keepaway / Team Keepaway ----
        Add("KaBall", "Ball", "notify_ballpickedup", Cyan);
        Add("KaBallCarrier", "Ball carrier", "keepawayball_carrying", Red);
        Add("TkaBallCarrierRed", "Ball carrier", "tka_taken_red", Cyan);
        Add("TkaBallCarrierBlue", "Ball carrier", "tka_taken_blue", Cyan);
        Add("TkaBallCarrierYellow", "Ball carrier", "tka_taken_yellow", Cyan);
        Add("TkaBallCarrierPink", "Ball carrier", "tka_taken_pink", Cyan);

        // ---- Last Man Standing ----
        Add("LmsLeader", "Leader", "", Cyan);

        // ---- Nexball ----
        Add("NbBall", "Ball", "", NbBallCol);
        Add("NbGoal", "Goal", "", Orange);

        // ---- Onslaught (control points + generators; per-def attack/defend blink) ----
        Add("OnsCP", "Control point", "", Orange);
        Add("OnsCPDefend", "Control point", "", Orange, 0.5f);
        Add("OnsCPAttack", "Control point", "", Orange, 2f);
        Add("OnsGen", "Generator", "", Orange);
        Add("OnsGenShielded", "Generator", "", Orange);

        // ---- pickups / monsters / vehicles / buffs ----
        Add("Weapon", "Weapon", "", new Vector3(0f, 0f, 0f));
        Add("Monster", "Monster", "", Red);
        Add("Vehicle", "Vehicle", "", White);
        Add("VehicleIntruder", "Intruder!", "", White);
        Add("Seeker", "Tagged", "", SeekerCol, 2f);
        Add("Buff", "Buff", "", Orange);
        return d;
    }

    private static readonly Dictionary<string, WaypointDef> Defs = Build();

    /// <summary>Look up a def by name (the WP_* netname). Returns a magenta "?" text fallback for unknown names.</summary>
    public static WaypointDef Get(string name)
        => Defs.TryGetValue(name, out WaypointDef def)
            ? def
            : new WaypointDef(name, name, "", new Vector3(1f, 0f, 1f), 1f, 1);
}

/// <summary>
/// One live waypoint sprite on the server — the C# port of the QC <c>WaypointSprite</c> edict
/// (waypointsprites.qc). It tracks a position (fixed, or following an owner entity at an offset), the current
/// def (its sprite/icon/text + radar icon), team + visibility rule, an optional health bar (objectives), a
/// build-progress timer, a helpme flash, a lifetime/fade, and a max view distance. Created + mutated through the
/// <see cref="WaypointSprites"/> API and serialized per-peer by the net layer each tick.
/// </summary>
public sealed class WaypointSprite
{
    public int Id;
    public string SpriteName = "";        // current def name (UpdateSprites swaps it per state)
    public Entity? Owner;                 // follow this entity's origin (carriers / objectives), or null = fixed
    public Vector3 Offset;                // added to Owner.Origin (or the fixed origin)
    public Vector3 FixedOrigin;           // used when Owner is null
    public int Team;                      // Teams.* color code (0 = neutral)
    public Vector3 Color = new(1f, 1f, 1f); // teamradar_color (radar tint; may be custom)
    public int RadarIcon = 1;             // m_radaricon (0 = not on radar)
    public int Rule = SpriteRule.Default;
    public bool Hideable;                 // honors the client's cl_hidewaypoints toggle

    public float Health = -1f;            // -1 = no health bar; 0..1 normalized otherwise
    public float MaxHealth;               // for the bar scale (kept for parity; Health is pre-normalized here)
    public float BuildStarted, BuildFinished, BuildStartHealth; // build-progress timer (QC build_*)

    public float SpawnTime;               // when created (sim time)
    public float Lifetime;                // 0 = forever; else seconds before fade-out
    public float FadeTime;                // fade-out duration (QC fadetime)
    public float MaxDistance;             // 0 = unlimited; else fade/cull beyond this
    public float HelpmeUntil;             // helpme flash active while sim-time < this (0 = off)

    public bool Dead;                     // marked for removal (lifetime expired / killed)
    public float DeadAt;                  // sim time when fade-out completes → drop

    /// <summary>QC <c>WaypointSprite_Ping</c> radar pulse: a ping is "active" (the net layer stamps bit 7 of the
    /// radar-icon byte once) while sim-time &lt; this. The 0.3s anti-spam window matches QC's ping cooldown so a
    /// rapid re-ping doesn't double-stamp the same frame; the client draws an expanding ring from each stamp.</summary>
    public float PingedUntil;

    /// <summary>Sim-time of the most recent <see cref="WaypointSprites.Ping"/> (0 = never). The serializer stamps
    /// bit 7 of the radar-icon byte for the short window after it so every subscribed peer sees the radar pulse.</summary>
    public float PingStartedAt;

    /// <summary>The player who deployed this waypoint (QC <c>.owner</c>), for clear-by-owner; null for objectives.</summary>
    public Player? DeployedBy;
    /// <summary>Which QC owner-field this occupies (personal/fixed/attached), so <c>waypoint_clear</c> can scope it.</summary>
    public DeployKind Kind = DeployKind.None;

    /// <summary>Optional per-waypoint visibility predicate (owner, viewer-player) → visible. Null = rule-based.</summary>
    public System.Func<Player?, bool>? VisibleForPlayer;

    /// <summary>The live world position (owner-relative, or fixed).</summary>
    public Vector3 Origin => Owner is { } o ? o.Origin + Offset : FixedOrigin;
}

/// <summary>
/// The server-side waypoint manager — the C# port of the <c>WaypointSprite_*</c> API + think/expire loop
/// (waypointsprites.qc). Gametypes (in this assembly) spawn + update waypoints through these static helpers;
/// <see cref="GameWorld"/> calls <see cref="Reset"/> on map load and <see cref="Think"/> each tick; the net
/// layer reads <see cref="Active"/> to serialize per peer. A static facade matches how the port's gametypes
/// already reach shared server state (<see cref="GametypeEntities"/> / <c>Api</c>).
/// </summary>
public static class WaypointSprites
{
    private static readonly List<WaypointSprite> _active = new();
    private static int _nextId = 1;

    /// <summary>Every live waypoint (the net layer filters per peer). Read-only view.</summary>
    public static IReadOnlyList<WaypointSprite> Active => _active;

    /// <summary>Drop all waypoints + reset ids (QC map reset / level change).</summary>
    public static void Reset()
    {
        _active.Clear();
        _nextId = 1;
    }

    private static float Now => GametypeEntities.Now;

    // ---- spawn ----------------------------------------------------------------------------------------

    /// <summary>QC <c>WaypointSprite_SpawnFixed</c>: a stationary objective marker at <paramref name="origin"/>
    /// (e.g. a flag base, control point, race checkpoint). <paramref name="ownfield"/> is unused in the port
    /// (kept for call-site parity); ownership cleanup is via <see cref="Kill"/>.</summary>
    public static WaypointSprite SpawnFixed(string spriteName, Vector3 origin, int team, Vector3 color,
        int radarIcon = 1, int rule = SpriteRule.Default, bool hideable = false)
    {
        var wp = new WaypointSprite
        {
            Id = _nextId++,
            SpriteName = spriteName,
            FixedOrigin = origin,
            Team = team,
            Color = color,
            RadarIcon = radarIcon,
            Rule = rule,
            Hideable = hideable,
            SpawnTime = Now,
        };
        _active.Add(wp);
        return wp;
    }

    /// <summary>QC <c>WaypointSprite_Spawn</c>: a marker following <paramref name="reference"/> at an offset, with
    /// an optional lifetime + max view distance (player pings, dropped flags). Pass <paramref name="reference"/>
    /// null for a fixed-position transient (uses <paramref name="origin"/>).</summary>
    public static WaypointSprite Spawn(string spriteName, float lifetime, float maxDistance,
        Entity? reference, Vector3 offset, Vector3 origin, int team, Vector3 color,
        int radarIcon = 1, int rule = SpriteRule.Default, bool hideable = false)
    {
        var wp = new WaypointSprite
        {
            Id = _nextId++,
            SpriteName = spriteName,
            Owner = reference,
            Offset = offset,
            FixedOrigin = origin,
            Team = team,
            Color = color,
            RadarIcon = radarIcon,
            Rule = rule,
            Hideable = hideable,
            SpawnTime = Now,
            Lifetime = lifetime,
            MaxDistance = maxDistance,
        };
        _active.Add(wp);
        return wp;
    }

    /// <summary>QC <c>WaypointSprite_AttachCarrier</c>: a marker that rides a carrier player at the standard
    /// head offset (flag/key/ball carriers). Removed automatically when the carrier dies/leaves
    /// (<see cref="DetachCarrier"/> / <see cref="Kill"/>).</summary>
    public static WaypointSprite AttachCarrier(string spriteName, Entity carrier, int team, Vector3 color,
        int radarIcon = 1, int rule = SpriteRule.Default)
        => Spawn(spriteName, 0f, 0f, carrier, new Vector3(0f, 0f, 64f), default, team, color, radarIcon, rule);

    // ---- player-deployed pings (QC server/impulse.qc → waypointsprites.qc Deploy*) ---------------------

    private static float DeployedLifetime => GametypeEntities.Cvar("sv_waypointsprite_deployed_lifetime", 10f);
    private static float DeployedDeadLifetime => GametypeEntities.Cvar("sv_waypointsprite_deadlifetime", 1f);
    private static float LimitedRange => GametypeEntities.Cvar("sv_waypointsprite_limitedrange", 5120f);

    /// <summary>QC <c>WaypointSprite_DeployFixed</c> (waypointsprites.qc:1105): a stationary HERE/DANGER ping at
    /// <paramref name="origin"/> with the deployed lifetime; team = the player's team in teamplay (the caller
    /// passes <paramref name="team"/>, already resolved), else 0 (everyone). Only one fixed ping per player —
    /// a new one replaces the old (QC owns it via <c>.waypointsprite_deployed_fixed</c>).</summary>
    public static WaypointSprite DeployFixed(string spriteName, bool limitedRange, Player player, Vector3 origin,
        int team, Vector3 color, int radarIcon = 1)
    {
        ClearKind(player, DeployKind.Fixed);
        float maxdist = limitedRange ? LimitedRange : 0f;
        WaypointSprite wp = Spawn(spriteName, DeployedLifetime, maxdist, null, default, origin, team, color, radarIcon);
        wp.FadeTime = DeployedDeadLifetime;
        wp.DeployedBy = player;
        wp.Kind = DeployKind.Fixed;
        return wp;
    }

    /// <summary>QC <c>WaypointSprite_DeployPersonal</c> (waypointsprites.qc:1126): a personal waypoint at
    /// <paramref name="origin"/> visible ONLY to the deploying player (QC <c>showto = own</c>), never fading
    /// (lifetime 0). Replaces the player's previous personal waypoint.</summary>
    public static WaypointSprite DeployPersonal(string spriteName, Player player, Vector3 origin,
        int radarIcon = 1)
    {
        ClearKind(player, DeployKind.Personal);
        WaypointSprite wp = Spawn(spriteName, 0f, 0f, null, default, origin, 0, new Vector3(1f, 1f, 1f), radarIcon);
        wp.DeployedBy = player;
        wp.Kind = DeployKind.Personal;
        // QC showto = own → visible only to the owner.
        wp.VisibleForPlayer = viewer => ReferenceEquals(viewer, player);
        return wp;
    }

    /// <summary>QC <c>WaypointSprite_Attach</c> (waypointsprites.qc:1136): a HELP-ME marker that follows the
    /// player at the head offset for the deployed lifetime; team = the player's team in teamplay. Refused if a
    /// flag/key carrier marker already rides the player (QC the <c>waypointsprite_attachedforcarrier</c> guard),
    /// in which case the existing carrier marker's helpme flash is pinged instead. Returns null when refused.</summary>
    public static WaypointSprite? Attach(string spriteName, Player player, bool limitedRange, int team,
        Vector3 color, int radarIcon = 1)
    {
        // QC: can't attach to a flag/key carrier — find an existing carrier marker on this player and ping it.
        foreach (WaypointSprite e in _active)
            if (ReferenceEquals(e.Owner, player) && e.Kind == DeployKind.None)
            {
                HelpMePing(e, DeployedLifetime);
                return null;
            }
        ClearKind(player, DeployKind.Attached);
        float maxdist = limitedRange ? LimitedRange : 0f;
        WaypointSprite wp = Spawn(spriteName, DeployedLifetime, maxdist, player, new Vector3(0f, 0f, 64f),
            default, team, color, radarIcon);
        wp.FadeTime = DeployedDeadLifetime;
        wp.DeployedBy = player;
        wp.Kind = DeployKind.Attached;
        HelpMePing(wp, DeployedLifetime);
        return wp;
    }

    /// <summary>QC <c>WaypointSprite_ClearPersonal</c>: remove the player's personal waypoint only.</summary>
    public static void ClearPersonal(Player player) => ClearKind(player, DeployKind.Personal);

    /// <summary>QC <c>WaypointSprite_ClearOwned</c>: remove every waypoint the player deployed (personal + here/danger + helpme).</summary>
    public static void ClearOwned(Player player)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_active[i].DeployedBy, player))
                _active.RemoveAt(i);
    }

    private static void ClearKind(Player player, DeployKind kind)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
            if (_active[i].Kind == kind && ReferenceEquals(_active[i].DeployedBy, player))
                _active.RemoveAt(i);
    }

    // ---- update ---------------------------------------------------------------------------------------

    /// <summary>QC <c>WaypointSprite_UpdateSprites</c> (collapsed): swap the current def (state change), e.g. a
    /// flag base going home→taken or an Assault objective defend→destroy.</summary>
    public static void UpdateSprites(WaypointSprite? wp, string spriteName)
    {
        if (wp is not null) wp.SpriteName = spriteName;
    }

    /// <summary>QC <c>WaypointSprite_UpdateTeamRadar</c>: set the radar icon + color.</summary>
    public static void UpdateTeamRadar(WaypointSprite? wp, int radarIcon, Vector3 color)
    {
        if (wp is null) return;
        wp.RadarIcon = radarIcon;
        wp.Color = color;
    }

    /// <summary>QC <c>WaypointSprite_UpdateHealth</c>: set the health-bar fill (already normalized 0..1; &lt;0
    /// hides the bar).</summary>
    public static void UpdateHealth(WaypointSprite? wp, float normalized)
    {
        if (wp is not null) wp.Health = normalized;
    }

    /// <summary>QC <c>WaypointSprite_UpdateMaxHealth</c> — kept for call-site parity (Health is pre-normalized).</summary>
    public static void UpdateMaxHealth(WaypointSprite? wp, float max)
    {
        if (wp is not null) wp.MaxHealth = max;
    }

    /// <summary>QC <c>WaypointSprite_UpdateRule</c>: change the team + visibility rule.</summary>
    public static void UpdateRule(WaypointSprite? wp, int team, int rule)
    {
        if (wp is null) return;
        wp.Team = team;
        wp.Rule = rule;
    }

    /// <summary>QC <c>WaypointSprite_UpdateBuildFinished</c>: drive a build-progress health bar to 1 at
    /// <paramref name="finishTime"/> (item/buff respawn timers, generator build).</summary>
    public static void UpdateBuildFinished(WaypointSprite? wp, float finishTime, float startHealth = 0f)
    {
        if (wp is null) return;
        wp.BuildStarted = Now;
        wp.BuildFinished = finishTime;
        wp.BuildStartHealth = startHealth;
    }

    /// <summary>QC <c>WaypointSprite_Ping</c> (waypointsprites.qc:891): pulse the radar ping ring. Anti-spam — a
    /// re-ping within 0.3s is ignored; otherwise marks the sprite "pinged" for one net frame so the serializer
    /// stamps bit 7 of the radar-icon byte (QC <c>cnt |= BIT(7)</c>), which the client turns into an expanding
    /// gfx/teamradar_ping ring.</summary>
    public static void Ping(WaypointSprite? wp)
    {
        if (wp is null) return;
        float now = Now;
        if (now < wp.PingedUntil) return; // anti-spam (QC waypointsprite_pingtime)
        wp.PingedUntil = now + 0.3f;
        wp.PingStartedAt = now; // opens a short wire window so all subscribed peers see bit 7 for this ping
    }

    /// <summary>True while a ping is actively being broadcast (the short window after <see cref="Ping"/>): the
    /// serializer stamps bit 7 of the radar-icon byte so every subscribed peer sees the pulse. The client
    /// de-dupes by ping start-time so the 0.3s window yields exactly one ring per ping event.</summary>
    public static bool IsPinging(WaypointSprite wp) => wp.PingStartedAt > 0f && Now < wp.PingStartedAt + 0.3f;

    /// <summary>QC <c>WaypointSprite_HelpMePing</c>: flash the "needing help" state for the deployed lifetime.</summary>
    public static void HelpMePing(WaypointSprite? wp, float lifetime)
    {
        if (wp is not null) wp.HelpmeUntil = Now + lifetime;
    }

    // ---- remove ---------------------------------------------------------------------------------------

    /// <summary>QC <c>WaypointSprite_Kill</c>: remove a waypoint immediately.</summary>
    public static void Kill(WaypointSprite? wp)
    {
        if (wp is not null) _active.Remove(wp);
    }

    /// <summary>QC <c>WaypointSprite_Disown</c>: begin a fade-out then remove after <paramref name="fadeTime"/>.</summary>
    public static void Disown(WaypointSprite? wp, float fadeTime)
    {
        if (wp is null) return;
        wp.Dead = true;
        wp.DeadAt = Now + fadeTime;
    }

    /// <summary>Remove every waypoint owned by (following) <paramref name="owner"/> (e.g. a carrier that left).</summary>
    public static void DetachCarrier(Entity owner)
    {
        for (int i = _active.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_active[i].Owner, owner))
                _active.RemoveAt(i);
    }

    // ---- per-tick think (lifetime expiry + build health) ----------------------------------------------

    /// <summary>QC <c>WaypointSprite_Think</c> (aggregated): expire lifetimes, advance build-progress bars, and
    /// drop dead waypoints whose fade-out finished. Called once per server tick by <see cref="GameWorld"/>.</summary>
    public static void Think()
    {
        float now = Now;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            WaypointSprite wp = _active[i];

            // A waypoint following an entity that was freed (its owner item/carrier removed by a path that didn't
            // explicitly kill the sprite — e.g. a dropped powerup landing in a NODROP brush) is dropped here so it
            // can't linger with a stale position. Fixed-origin waypoints (Owner == null) are unaffected.
            if (wp.Owner is { IsFreed: true })
            {
                _active.RemoveAt(i);
                continue;
            }

            // build-progress health (QC build_started..build_finished → 0..1, then -1 to hide the bar).
            if (wp.BuildFinished > 0f)
            {
                if (now < wp.BuildFinished + 0.25f)
                    wp.Health = now < wp.BuildStarted ? wp.BuildStartHealth
                        : now < wp.BuildFinished
                            ? (now - wp.BuildStarted) / System.MathF.Max(0.0001f, wp.BuildFinished - wp.BuildStarted)
                                * (1f - wp.BuildStartHealth) + wp.BuildStartHealth
                            : 1f;
                else
                    wp.Health = -1f;
            }

            // lifetime fade-out → mark dead, then drop after the fade.
            if (!wp.Dead && wp.Lifetime > 0f && now >= wp.SpawnTime + wp.Lifetime)
                Disown(wp, wp.FadeTime > 0f ? wp.FadeTime : 1f);

            if (wp.Dead && now >= wp.DeadAt)
                _active.RemoveAt(i);
        }
    }

    /// <summary>The 0..1 lifetime fade alpha for a waypoint this frame (1 normally; ramps to 0 during fade-out).</summary>
    public static float FadeAlpha(WaypointSprite wp)
    {
        if (!wp.Dead) return 1f;
        float dur = System.MathF.Max(0.0001f, wp.FadeTime > 0f ? wp.FadeTime : 1f);
        return System.Math.Clamp((wp.DeadAt - Now) / dur, 0f, 1f);
    }
}
