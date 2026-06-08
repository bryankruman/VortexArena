using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side rendering hub — the Godot successor to CSQC's <c>CSQC_Ent_Update</c> /
/// <c>CSQC_Event</c> / temp-entity dispatch. It owns the four render subsystems
/// (<see cref="EffectSystem"/>, <see cref="ProjectileRenderer"/>, <see cref="ModelAnimator"/>s, the local
/// <see cref="ViewModel"/>) and exposes a tiny, dependency-free public API that the networking layer
/// (<c>game/net/ClientNet</c>, owned by another agent) calls as packets arrive:
///
///   • <see cref="OnEffect"/>      — a networked particle effect fired (Send_Effect / EFF_NET_* decode).
///   • <see cref="OnEntityUpdate"/>— an entity entered/changed in the client's snapshot (CSQC ent update).
///   • <see cref="OnEntityRemove"/>— an entity left the snapshot / was freed.
///   • <see cref="OnSound"/>       — a positional sound event (QC sound() on an entity/point).
///   • <see cref="OnMuzzleFlash"/> — the local player fired (drives the view-model flash).
///
/// These are plain methods (and matching <c>Action</c> properties for code that prefers delegates), so the
/// net layer can wire them with no shared interface beyond <see cref="Entity"/>/<see cref="Effect"/>, which
/// both sides already reference. The class also installs an <see cref="IEffectSink"/> so that any server-side
/// or local <c>EffectEmitter.Emit</c> in this process is mirrored straight onto the renderer — which is what
/// makes the demo show effects without a real network round-trip.
///
/// Each frame it advances the projectile follow/interp and any model animators it owns; the EffectSystem and
/// per-projectile trails self-advance as Godot particle nodes.
/// </summary>
public partial class ClientWorld : Node3D
{
    /// <summary>Particle effect renderer (EFFECT_* catalog → Godot particles).</summary>
    public EffectSystem Effects { get; private set; } = null!;

    /// <summary>Networked projectile visual renderer.</summary>
    public ProjectileRenderer Projectiles { get; private set; } = null!;

    /// <summary>Lightning-arc / beam renderer (te_csqc_lightningarc: electro combo, Golem zaps, Tesla arcs).</summary>
    public BeamRenderer Beams { get; private set; } = null!;

    /// <summary>The local player's first-person weapon view-model (optional; set by the host).</summary>
    public ViewModel? ViewModel { get; set; }

    /// <summary>Per-entity render nodes for non-projectile networked entities (players/monsters/items).</summary>
    private readonly Dictionary<int, EntityNode> _entityNodes = new();

    /// <summary>Per-entity model animators (players/monsters/vehicles with MD3 anim).</summary>
    private readonly Dictionary<int, ModelAnimator> _animators = new();

    /// <summary>Per-player skeletal models (IQM + CPU upper/lower split + view-pitch aim), driven each frame.</summary>
    private readonly Dictionary<int, PlayerModel> _playerModels = new();

    /// <summary>Per-player floating nameplates (ent_cs): a billboarded health/id label above the head.</summary>
    private readonly Dictionary<int, Label3D> _nameplates = new();

    /// <summary>Per-entity vehicle visual drivers (rotors/barrels/engine sound/gibs/heal-beam) for vehicle entities.</summary>
    private readonly Dictionary<int, VehicleVisuals> _vehicles = new();

    /// <summary>Entity indices whose animation frame is networked (CSQCMODEL_AUTOUPDATE): their
    /// <see cref="ModelAnimator"/> follows <see cref="Entity.Frame"/> directly instead of the local
    /// movement-derived clip heuristic. Populated by <see cref="OnEntityUpdate(Entity,bool)"/>.</summary>
    private readonly HashSet<int> _frameDriven = new();

    /// <summary>
    /// Per-entity CSQC-model render state (the C# successor to the per-edict CSQCModel fields the client kept in
    /// <c>csqcmodel_hooks.qc</c>): the persistent effect-light + jetpack-loop state, the last-seen colormap, and
    /// the client-OBSERVED death timestamp (the wire doesn't carry the server <c>death_time</c>, so the
    /// <c>cl_deathglow</c> fade starts from when this client first saw the model dead). Driven each frame in
    /// <see cref="DriveCsqcModelHooks"/>; torn down in <see cref="OnEntityRemove(int,NVec3,string?)"/>.
    /// </summary>
    private sealed class CsqcState
    {
        public readonly CsqcModelEffects.State Effects = new();
        public bool WasDead;          // last frame's dead test (to capture the death instant)
        public float DeathTime;       // client-observed time the model went dead (cl_deathglow fade origin)
        public bool IsPlayerModel;    // a skeletal/MD3 player model (gets the appearance/deathglow pass)
        public ItemDespawnFx? Despawn; // loot despawn animation (lazily created when ITS_EXPIRING first seen)
        public bool ItemFaded;        // a despawn/ghost transparency is currently applied (reset to opaque on the way back)
    }
    private readonly Dictionary<int, CsqcState> _csqc = new();

    /// <summary>Pool of positional audio players reused for one-shot sound events.</summary>
    private readonly List<AudioStreamPlayer3D> _audioPool = new();

    /// <summary>
    /// Active one-shot players on SINGLE (positive) channels keyed by (emitter net id, channel). A new sound
    /// on the same key stops the previous player before starting — DP's CH_*_SINGLE replacement semantic.
    /// Auto/negative channels never key here (they always stack via the pool).
    /// </summary>
    private readonly Dictionary<(int netId, int channel), AudioStreamPlayer3D> _singleChannelPlayers = new();

    /// <summary>
    /// Active one-shot players that must FOLLOW their emitter for the sound's duration. DP attaches a sound to
    /// the entity+channel so it moves with it; without this a gunshot fired while running is stranded at the
    /// firing point — the listener (camera) moves away mid-playback, so Godot's distance attenuation makes it
    /// go quiet AND its distance low-pass filter makes it muffled/muddy (the "sounds fine standing still, bad
    /// while moving" bug, worst for your own weapon since its emitter should sit on the listener). Populated by
    /// <see cref="OnSound"/> for any record with a valid source net id; driven each frame by
    /// <see cref="DriveAttachedOneShots"/> (mirrors the looping-sound follow), entries pruned when the player
    /// stops or is recycled. Auto/looping sounds are unaffected — loops already follow via _loopingSounds.
    /// </summary>
    private readonly List<(AudioStreamPlayer3D player, int netId)> _attachedOneShots = new();

    /// <summary>
    /// Persistent LOOPING positional sounds keyed by (emitter net id, channel) — DP's entity+channel sound
    /// model (the Arc beam loop, vehicle engines). Distinct from the one-shot <see cref="_audioPool"/>: a loop
    /// follows its emitter each frame (<see cref="EntityOriginResolver"/>), is replaced by a new loop on the
    /// same key, and ends on <see cref="OnStopSound"/> / the emitter leaving the snapshot / a keepalive lapse.
    /// <see cref="OnLoopingSound"/> populates it.
    /// </summary>
    private readonly Dictionary<(int netId, int channel), LoopingSound> _loopingSounds = new();
    private readonly List<(int netId, int channel)> _loopScratch = new(); // reused: keys to drop this frame

    /// <summary>One persistent looping positional source (Arc beam / vehicle engine) keyed in <see cref="_loopingSounds"/>.</summary>
    private sealed class LoopingSound
    {
        public AudioStreamPlayer3D Player = null!;
        public string Sample = "";   // the resolved sample currently looping (idempotency check on re-emit)
        public float SilentTime;     // seconds since the last keepalive refresh (auto-stop past the timeout)
        public bool Alive = true;    // false once stopped, so the Finished re-play handler goes inert
    }

    /// <summary>A looping sound auto-stops if it isn't refreshed (re-emitted) for this long — the safety net for an
    /// emitter that stops without an explicit stop (e.g. the player switches off the Arc mid-fire, so the server
    /// stops re-emitting the loop but never sends a stop). Generous vs the per-tick/per-frame re-emit cadence.</summary>
    private const float LoopKeepaliveTimeout = 0.5f;

    /// <summary>
    /// Resolves an <see cref="Entity.ModelIndex"/> / model name to a parsed <see cref="Md3Data"/> for the
    /// renderer. The host sets this (it knows the asset/precache table); when null, entities render as a
    /// placeholder box. Keeping it a delegate avoids a hard dependency on the asset pipeline here.
    /// </summary>
    public Func<Entity, Md3Data?>? ModelResolver { get; set; }

    /// <summary>
    /// The material facade (host-set to <c>AssetLoader.Assets</c>) used to texture models built from a
    /// <see cref="ModelResolver"/> result — the <see cref="ModelAnimator"/> morph, the single-frame
    /// <see cref="ModelLoader.BuildModel"/> snapshot, and vehicle bodies. Null → those surfaces render untextured.
    /// Also feeds the <see cref="EffectSystem"/> its particlefont atlas + effectinfo.txt loaders (so explosions,
    /// impacts and decals draw the real Xonotic sprites), wired whether this is set before or after _Ready.
    /// </summary>
    public AssetSystem? Assets
    {
        get => _assets;
        set { _assets = value; WireEffectAssets(); }
    }
    private AssetSystem? _assets;

    /// <summary>Push the asset-backed texture/text loaders into the effect system so it can build the
    /// particlefont atlas and read effectinfo.txt from the mounted VFS. No-op until both Effects and Assets
    /// exist (the host sets Assets before AddChild, so this also runs from _Ready once Effects is created).</summary>
    private void WireEffectAssets()
    {
        if (Effects is null || _assets is null)
            return;
        Effects.TextureLoader = _assets.LoadTexture;
        Effects.VfsTextLoader = v =>
        {
            try { return _assets.Vfs.ReadText(v); }
            catch { return null; }
        };
    }

    /// <summary>
    /// Format-agnostic fallback model factory: builds a fully-textured, animated scene node for a non-player
    /// world entity of ANY model format (IQM/DPM/MD3) straight from the asset pipeline
    /// (<c>AssetLoader.LoadModel</c>). Tried after <see cref="ModelResolver"/> (the MD3-only, server-frame-driven
    /// path) returns null, so IQM/DPM props/items/monsters render their real model instead of a placeholder box.
    /// Host-wired; null → those entities fall back to the placeholder.
    /// </summary>
    public Func<Entity, Node3D?>? EntityModelFactory { get; set; }

    /// <summary>
    /// Resolves a player entity to a skeletal <see cref="PlayerModel"/> (an IQM with the CPU upper/lower-body
    /// split + view-pitch aim). Tried before <see cref="ModelResolver"/>: when it returns non-null the entity
    /// renders as a posed skeleton; null falls through to the MD3 morph path / placeholder. The host wires it
    /// to the asset pipeline (<c>AssetLoader.LoadSkeletalModel</c>); null = no skeletal players.
    /// </summary>
    public Func<Entity, PlayerModel?>? PlayerModelResolver { get; set; }

    /// <summary>
    /// Resolves a sound sample path (QC <c>sound_str</c>, e.g. "weapons/rocket_impact") to a loadable Godot
    /// audio resource path (e.g. "res://sound/weapons/rocket_impact.ogg"). Host-provided; defaults to a
    /// res://sound/&lt;sample&gt;.ogg convention. Returning null/blank skips playback.
    /// </summary>
    public Func<string, string?>? SoundResolver { get; set; }

    /// <summary>
    /// Loads a sound sample straight to a decoded <see cref="AudioStream"/> from the mounted asset VFS
    /// (host-set to <c>AssetLoader.LoadSound</c>). This is the primary path — it reads the real Xonotic
    /// <c>sound/</c> tree out of the mounted content packs. When null (or it returns null) the code falls
    /// back to the <see cref="SoundResolver"/> <c>res://</c> path convention. Shared with the projectile
    /// and vehicle renderers so every positional cue resolves the same way.
    /// </summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>
    /// Resolves a networked entity's CURRENT Quake-space origin by its net id, so a LOOPING positional sound
    /// (Arc beam, vehicle engine) follows its emitter each frame (DP channel sounds track the entity). Host-wired
    /// to the net layer (local player → predicted origin; remotes → interpolated pose). Returns null when the id
    /// is unknown — the loop then holds its last position. Only looping sounds use it; one-shots fire at a fixed point.
    /// </summary>
    public Func<int, NVec3?>? EntityOriginResolver { get; set; }

    /// <summary>
    /// The client-side appearance context the CSQC <c>CSQCPlayer_ModelAppearance_Apply</c> FORCEMODEL/FORCECOLORS
    /// branch needs (csqcmodel_hooks.qc:141-327): which net id is the local player, the local player's team +
    /// gametype flags, and the local player number. Host-set (NetGame reads it from <see cref="ClientNet"/>);
    /// when null the force-model/force-color overrides are inert (the entity keeps its own model + networked
    /// colormap, the pre-T58 behavior). The fields mirror the QC globals of the same intent.
    /// </summary>
    public sealed class AppearanceContext
    {
        public int LocalNetId;       // QC: the local player's entnum (islocalplayer = entnum == this)
        public int MyTeam;           // QC myteam (1..4 in team modes; NUM_SPECTATOR=5 spectating; 0 none)
        public bool Teamplay;        // QC teamplay
        public bool Is1v1;           // QC gametype.m_1v1 (Duel)
        public int TeamCount;        // QC team_count (2 for the 2-team force-color gate)
        public int PlayerLocalNum;   // QC player_localnum (the FFA forced colormap = player_localnum+1)
    }

    /// <summary>Host-set provider for the live <see cref="AppearanceContext"/> (read each frame). Null = no force
    /// model/colors (overrides inert). NetGame wires it; a pure demo leaves it null.</summary>
    public Func<AppearanceContext?>? AppearanceProvider { get; set; }

    /// <summary>
    /// Resolve a FORCED player model (QC <c>cl_forcemyplayermodel</c> / the <c>cl_forceplayermodels</c> →
    /// <c>_cl_playermodel</c> swap) to a render node for an entity: given the forced model NAME + skin, build the
    /// skeletal/MD3 node the same way <see cref="PlayerModelResolver"/>/<see cref="ModelResolver"/> would. Host-set
    /// (NetGame); null → no force-model swap (the entity keeps its own networked model). Returns null when the
    /// forced model can't be built (QC <c>fexists</c>-miss → keep own).
    /// </summary>
    public Func<string, int, Node3D?>? ForcedModelResolver { get; set; }

    // ---- delegate-style hooks (for net code that prefers Action wiring) ----
    public Action<string, NVec3, NVec3, int>? EffectHook;
    public Action<Entity>? EntityUpdateHook;
    public Action<Entity>? EntityRemoveHook;
    public Action<string, NVec3>? SoundHook;

    // =================================================================================================
    //  Setup
    // =================================================================================================

    public override void _Ready()
    {
        Effects = new EffectSystem { Name = "Effects" };
        AddChild(Effects);
        // The host set Assets before AddChild (so before this _Ready), when Effects was still null; now that it
        // exists, push the particlefont/effectinfo loaders into it so the real sprites/decals are used.
        WireEffectAssets();

        Projectiles = new ProjectileRenderer { Name = "Projectiles", Effects = Effects };
        // Share the client's sound resolution so projectile fly-loops (rocket/electro/fireball) resolve the
        // same way positional sounds do (QC loopsound on the CSQC projectile).
        Projectiles.SoundResolver = ResolveSound;
        // Share the VFS audio loader so projectile fly-loops resolve from the mounted content packs too
        // (late-bound: the host sets AudioLoader after _Ready; ProjectileRenderer keeps the res:// fallback).
        Projectiles.AudioLoader = s => AudioLoader?.Invoke(s);
        AddChild(Projectiles);

        Beams = new BeamRenderer { Name = "Beams" };
        AddChild(Beams);
        // Let the effect system route beam-class effects (lightning/arc/laser) to the real beam renderer
        // instead of a particle burst (te_csqc_lightningarc).
        Effects.Beams = Beams;

        // Mirror in-process effect emissions (server/local gameplay calling EffectEmitter.Emit) onto the
        // renderer. This is what makes a single-process demo show effects with no network layer present.
        EffectEmitter.Sink = new RenderSink(this, EffectEmitter.Sink);

        // Bind the Action hooks to the methods so either calling convention works for the net layer.
        EffectHook = (name, origin, vel, count) => OnEffect(name, origin, vel, count);
        EntityUpdateHook = OnEntityUpdate;
        EntityRemoveHook = OnEntityRemove;
        SoundHook = (s, o) => OnSound(s, o);
    }

    public override void _ExitTree()
    {
        // Detach our sink so a torn-down client doesn't keep mirroring into freed nodes.
        if (EffectEmitter.Sink is RenderSink rs)
            EffectEmitter.Sink = rs.Inner;
    }

    // =================================================================================================
    //  Public API — called by the networking layer
    // =================================================================================================

    /// <summary>A networked particle effect fired at a point (Send_Effect). Names are EFFECT_* or effectinfo.</summary>
    public void OnEffect(string name, NVec3 origin, NVec3 velocity = default, int count = 1, Color? color = null)
    {
        Effects.Spawn(name, origin, velocity, count, color);
    }

    /// <summary>Effect fired from a decoded <see cref="EffectRequest"/> (the wire form).</summary>
    public void OnEffect(in EffectRequest request) => Effects.Spawn(request);

    /// <summary>
    /// A networked lightning arc between two Quake-space points (the TE_CSQC_ARC temp-entity: electro combo,
    /// Golem zaps, Tesla turret arcs). Draws the jagged crackling bolt; <paramref name="color"/> tints it.
    /// </summary>
    public void OnArc(NVec3 from, NVec3 to, Color? color = null) => Beams.Arc(from, to, color);

    /// <summary>A straight steady beam between two Quake-space points (rail / lightning trunk).</summary>
    public void OnBeam(NVec3 from, NVec3 to, Color? color = null) => Beams.Beam(from, to, color);

    /// <summary>
    /// An entity entered the client snapshot or changed. Routes projectiles to the
    /// <see cref="ProjectileRenderer"/> and everything else to an <see cref="EntityNode"/> (+ animator).
    /// Safe to call every snapshot for the same entity — it creates on first sight, updates thereafter.
    /// </summary>
    public void OnEntityUpdate(Entity entity) => OnEntityUpdate(entity, frameDriven: false);

    /// <summary>
    /// As <see cref="OnEntityUpdate(Entity)"/>, but when <paramref name="frameDriven"/> is true the entity's
    /// animation is driven by its networked <see cref="Entity.Frame"/> (CSQCMODEL_AUTOUPDATE) rather than the
    /// local movement-derived clip heuristic — used for networked remote players/monsters fed by the entity
    /// stream (see <c>ClientEntityView</c>). The flag takes effect when the model/animator is first attached.
    /// </summary>
    public void OnEntityUpdate(Entity entity, bool frameDriven)
    {
        if (entity is null || entity.IsFreed)
            return;

        if (frameDriven) _frameDriven.Add(entity.Index);
        else _frameDriven.Remove(entity.Index);

        if (IsProjectile(entity))
        {
            if (Projectiles.IsTracking(entity.Index)) Projectiles.OnUpdate(entity);
            else Projectiles.OnSpawn(entity);
            return;
        }

        // Vehicles get the dedicated visual driver (rotors/barrels/engine/gibs/heal-beam) instead of a plain
        // model node (Racer/Raptor/Spiderbot/Bumblebee — the bulk of the client vehicle TODOs).
        if (VehicleCatalog.Classify(entity.ClassName + " " + entity.Model) != VehicleCatalog.VehicleKind.None)
        {
            UpdateVehicle(entity);
            return;
        }

        // Non-projectile: ensure an EntityNode exists and is bound; it syncs origin/yaw each frame itself.
        if (!_entityNodes.TryGetValue(entity.Index, out EntityNode? node))
        {
            node = new EntityNode { Name = $"ent#{entity.Index}_{Safe(entity.ClassName)}" };
            AddChild(node);
            node.Bind(entity);
            _entityNodes[entity.Index] = node;

            TryAttachModel(entity, node);
            TryAttachNameplate(entity, node);
        }
        else
        {
            node.Entity = entity;
        }

        // Drive animation from the entity's frame/movement if it has an animator.
        UpdateAnimatorState(entity);
        UpdateNameplate(entity);
    }

    /// <summary>Add a floating health/id nameplate above a remote player (ent_cs). Skipped for non-players.</summary>
    private void TryAttachNameplate(Entity entity, EntityNode node)
    {
        if (!entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase))
            return;
        var plate = new Label3D
        {
            Name = "Nameplate",
            Position = new Vector3(0f, 60f, 0f),  // above the head (Godot up)
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            PixelSize = 0.03f,
            FontSize = 48,
            OutlineSize = 8,
            Modulate = Colors.White,
            Text = "",
        };
        node.AddChild(plate);
        _nameplates[entity.Index] = plate;
    }

    /// <summary>Refresh a player's nameplate text (health) each update; hide it when the player is dead.</summary>
    private void UpdateNameplate(Entity entity)
    {
        if (!_nameplates.TryGetValue(entity.Index, out Label3D? plate) || !GodotObject.IsInstanceValid(plate))
            return;
        bool dead = entity.Health <= 0f && entity.MaxHealth > 0f;
        plate.Visible = !dead;
        if (!dead)
            plate.Text = $"♥ {Mathf.Max(0, (int)entity.Health)}";
    }

    /// <summary>An entity left the snapshot / was freed. Tears down its visual(s).</summary>
    public void OnEntityRemove(Entity entity)
    {
        if (entity is null)
            return;
        OnEntityRemove(entity.Index, entity.Origin);
    }

    /// <summary>Remove by index (the net layer may only have the id by the time it's culled).</summary>
    public void OnEntityRemove(int index, NVec3 lastOrigin = default, string? impactEffect = null)
    {
        _frameDriven.Remove(index);
        _nameplates.Remove(index); // the Label3D is a child of the node — freed with it below
        StopLoopsForEntity(index); // end any looping sounds this entity emitted (Arc beam, vehicle engine)

        // Tear down the CSQC-model effect state (stop the jetpack loop, free the dynamic light).
        if (_csqc.Remove(index, out CsqcState? cs))
        {
            // Build a throwaway entity carrying the id so the sound service can key the (e, channel) stop.
            CsqcModelEffects.Release(new Entity { Index = index }, cs.Effects, Api.Services?.Sound);
        }

        if (Projectiles.IsTracking(index))
            Projectiles.OnRemove(index, lastOrigin, impactEffect);

        if (_animators.Remove(index, out ModelAnimator? anim) && GodotObject.IsInstanceValid(anim))
            anim.QueueFree();

        if (_playerModels.Remove(index, out PlayerModel? pm) && GodotObject.IsInstanceValid(pm))
        {
            pm.ReleaseSkeleton(); // skel_delete on the CPU poser
            pm.QueueFree();
        }

        if (_vehicles.Remove(index, out VehicleVisuals? veh) && GodotObject.IsInstanceValid(veh))
        {
            // Trigger the death gibs/explosion on removal, then let the driver self-free after they settle.
            veh.Apply(new VehicleVisuals.State { Alive = false }, 0f);
        }

        if (_entityNodes.Remove(index, out EntityNode? node) && GodotObject.IsInstanceValid(node))
            node.QueueFree();
    }

    /// <summary>
    /// The Godot node a child should attach to in order to track entity <paramref name="ownerIndex"/> at the
    /// named model tag (QC <c>setattachment(weapon, player, "tag_weapon")</c>) — the owner's
    /// <see cref="ModelAnimator"/> tag <see cref="Marker3D"/> when present (so the attachment follows the
    /// animation), else the owner's animator/entity root, else null when the owner isn't rendered yet. Used by
    /// the weapon view-entity renderer to hang a remote player's held weapon off their hand.
    /// </summary>
    public Node3D? GetAttachmentMarker(int ownerIndex, string tagName)
    {
        if (_animators.TryGetValue(ownerIndex, out ModelAnimator? anim) && GodotObject.IsInstanceValid(anim))
        {
            Marker3D? m = string.IsNullOrEmpty(tagName) ? null : anim.GetTag(tagName);
            return m ?? (Node3D)anim;
        }
        if (_entityNodes.TryGetValue(ownerIndex, out EntityNode? node) && GodotObject.IsInstanceValid(node))
            return node;
        return null;
    }

    /// <summary>Resolve a model for a networked entity via the host's <see cref="ModelResolver"/> (or null).</summary>
    public XonoticGodot.Formats.Md3.Md3Data? ResolveModel(Entity e) => ModelResolver?.Invoke(e);

    /// <summary>The node networked render children (e.g. weapon view-entities) parent under when unattached.</summary>
    public Node3D RenderRoot => this;

    /// <summary>
    /// A positional sound event at a point (QC sound() on an entity, networked to the client). Resolves the
    /// sample to a Godot audio resource and plays it spatially. <paramref name="sample"/> is the bare path
    /// (QC <c>sound_str</c>) or a registered <see cref="GameSound.NetName"/>. When the channel is a positive
    /// SINGLE channel and the emitter has a valid net id, a new play replaces (stops) the previous sound on
    /// that (entity, channel) key — DP's CH_*_SINGLE semantic. Auto/negative channels always stack.
    /// </summary>
    public void OnSound(string sample, NVec3 origin, float volume = 1f, float attenuation = 0.5f,
        int channel = 0, int sourceNetId = 0, float pitch = 1f)
    {
        GameSound? reg = Sounds.ByName(sample);
        string bare = reg?.Sample ?? sample;
        if (reg is not null) { volume = reg.Volume; }

        AudioStream? stream = LoadStream(bare);
        if (stream is null)
            return;

        string bus = BusForChannel(channel);
        bool isSingle = channel > 0 && sourceNetId > 0;

        if (isSingle)
        {
            var key = (sourceNetId, channel);
            if (_singleChannelPlayers.TryGetValue(key, out AudioStreamPlayer3D? prev)
                && GodotObject.IsInstanceValid(prev) && prev.Playing)
                prev.Stop();
            _singleChannelPlayers[key] = StartOneShot(stream, bus, origin, volume, attenuation, pitch, sourceNetId);
        }
        else
        {
            StartOneShot(stream, bus, origin, volume, attenuation, pitch, sourceNetId);
        }
    }

    /// <summary>Rent, configure and play a one-shot positional player; when the record has a valid emitter net id
    /// register it to FOLLOW that emitter for its duration (<see cref="_attachedOneShots"/>) — DP attaches a sound
    /// to the entity so it tracks the (moving) emitter/listener instead of being stranded at the firing point.</summary>
    private AudioStreamPlayer3D StartOneShot(AudioStream stream, string bus, NVec3 origin,
        float volume, float attenuation, float pitch, int sourceNetId)
    {
        AudioStreamPlayer3D player = RentAudioPlayer();
        player.Stream = stream;
        player.Bus = bus;
        player.GlobalPosition = Coords.ToGodot(origin);
        player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volume, 0.001f, 1f));
        player.UnitSize = attenuation <= 0f ? 100000f : Mathf.Clamp(40f / attenuation, 1f, 100000f);
        player.PitchScale = pitch;
        player.Play();

        // A pooled player may have been following a previous emitter — drop that stale follow before (maybe)
        // re-registering it, so one untracked sound can't get dragged around by an old entity's motion.
        for (int i = _attachedOneShots.Count - 1; i >= 0; i--)
            if (_attachedOneShots[i].player == player)
                _attachedOneShots.RemoveAt(i);
        if (sourceNetId > 0)
            _attachedOneShots.Add((player, sourceNetId));
        return player;
    }

    /// <summary>
    /// Map a <see cref="SoundChannel"/> value to the corresponding Godot audio bus name. Provides per-channel
    /// volume control (QC <c>snd_channel&lt;N&gt;volume</c> → dedicated buses created by <c>ClientSettings</c>).
    /// </summary>
    private static string BusForChannel(int channel)
    {
        // SoundChannel enum values:
        //   WeaponSingle=1/WeaponAuto=-1 → Weapon
        //   Voice=2/VoiceAuto=-2          → Voice
        //   Body=4/Player=7/Pain=6        → Player
        //   Item=3/TriggerAuto=-3         → Ambient (SFX fallback)
        //   Auto=0/ShotsAuto=-4           → SFX
        return channel switch
        {
            1 or -1 => "Weapon",                    // WeaponSingle, WeaponAuto
            2 or -2 => "Voice",                     // Voice, VoiceAuto
            4 or 6 or 7 or -6 or -7 => "Player",    // Body, Pain, Player (+ PainAuto, PlayerAuto)
            3 or -3 => "Ambient",                   // Item, TriggerAuto
            _ => "SFX",                             // Auto (0), ShotsAuto (-4), Tuba (5), Bgm (8), etc.
        };
    }

    /// <summary>
    /// Start (or refresh, or replace) a LOOPING positional sound keyed by (<paramref name="netId"/>,
    /// <paramref name="channel"/>) — DP <c>loopsound</c> on an entity+channel (the Arc beam, vehicle engines).
    /// Idempotent on re-emit: if the SAME sample is already looping on that key, this only refreshes its spatial
    /// params + keepalive and does NOT restart it — so a continuous emitter (the Arc) can call it every
    /// weapon-think without audible stacking. A DIFFERENT sample on the key replaces the old loop. The source
    /// then follows its emitter each frame via <see cref="EntityOriginResolver"/>.
    /// </summary>
    public void OnLoopingSound(int netId, int channel, string sample, NVec3 origin, float volume = 1f, float attenuation = 1f)
    {
        if (string.IsNullOrEmpty(sample))
            return;

        // Allow either a registered sound name or a raw sample path (same resolution as the one-shot OnSound).
        GameSound? reg = Sounds.ByName(sample);
        string bare = reg?.Sample ?? sample;
        if (reg is not null) volume = reg.Volume;

        var key = (netId, channel);
        if (_loopingSounds.TryGetValue(key, out LoopingSound? existing) && GodotObject.IsInstanceValid(existing.Player))
        {
            if (existing.Sample == bare)
            {
                // Same loop already playing on this (entity, channel): refresh, don't restart (idempotent re-emit).
                existing.SilentTime = 0f;
                ApplyLoopSpatial(existing.Player, origin, volume, attenuation);
                return;
            }
            DestroyLoop(existing);          // a different sample on the key → replace
            _loopingSounds.Remove(key);
        }

        AudioStream? stream = LoadStream(bare);
        if (stream is null)
            return;

        LoopingSound ls = CreateLoop(netId, channel, stream, bare);
        ApplyLoopSpatial(ls.Player, origin, volume, attenuation);
        _loopingSounds[key] = ls;
    }

    /// <summary>
    /// Stop the looping sound on (<paramref name="netId"/>, <paramref name="channel"/>) — DP
    /// <c>sound(e, ch, SND_Null)</c> (the Arc beam on release/overheat). A no-op if nothing loops on that key.
    /// </summary>
    public void OnStopSound(int netId, int channel)
    {
        if (_loopingSounds.Remove((netId, channel), out LoopingSound? ls))
            DestroyLoop(ls);
    }

    /// <summary>Build a dedicated looping <see cref="AudioStreamPlayer3D"/> for a (netId, channel) loop (NOT pooled —
    /// the one-shot pool would cut it off). Ogg/MP3 loop natively; other types re-trigger on <c>Finished</c>.</summary>
    private LoopingSound CreateLoop(int netId, int channel, AudioStream stream, string sample)
    {
        AudioStream looping = AudioLoop.MakeLooping(stream, out bool nativeLoop);
        var player = new AudioStreamPlayer3D
        {
            Name = $"loop_{netId}_{channel}",
            Stream = looping,
            MaxDistance = 4096f,
        };
        var ls = new LoopingSound { Player = player, Sample = sample };
        AddChild(player);
        if (!nativeLoop)
            // WAV / no native loop flag: re-trigger on end while this entry is still the live loop on its key.
            player.Finished += () => { if (ls.Alive && GodotObject.IsInstanceValid(player)) player.Play(); };
        player.Play();
        return ls;
    }

    private static void DestroyLoop(LoopingSound ls)
    {
        ls.Alive = false; // the Finished re-play handler (if any) goes inert
        if (GodotObject.IsInstanceValid(ls.Player))
        {
            ls.Player.Stop();
            ls.Player.QueueFree();
        }
    }

    /// <summary>Place + tune a looping source (mirrors the one-shot spatialization in <see cref="OnSound"/>).</summary>
    private static void ApplyLoopSpatial(AudioStreamPlayer3D p, NVec3 origin, float volume, float attenuation)
    {
        p.GlobalPosition = Coords.ToGodot(origin);
        p.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(volume, 0.001f, 1f));
        p.UnitSize = attenuation <= 0f ? 100000f : Mathf.Clamp(40f / attenuation, 1f, 100000f);
    }

    /// <summary>Follow each looping source to its emitter's current position and age its keepalive; auto-stop a loop
    /// not refreshed for <see cref="LoopKeepaliveTimeout"/> (its emitter stopped without an explicit stop).</summary>
    private void DriveLoopingSounds(float delta)
    {
        if (_loopingSounds.Count == 0)
            return;

        _loopScratch.Clear();
        foreach (var kv in _loopingSounds)
        {
            LoopingSound ls = kv.Value;
            if (!GodotObject.IsInstanceValid(ls.Player))
            {
                _loopScratch.Add(kv.Key);
                continue;
            }
            ls.SilentTime += delta;
            if (ls.SilentTime > LoopKeepaliveTimeout)
            {
                _loopScratch.Add(kv.Key);
                continue;
            }
            // Follow the emitter (local player → predicted eye; remotes → interpolated pose).
            if (EntityOriginResolver?.Invoke(kv.Key.netId) is { } pos)
                ls.Player.GlobalPosition = Coords.ToGodot(pos);
        }
        for (int i = 0; i < _loopScratch.Count; i++)
            if (_loopingSounds.Remove(_loopScratch[i], out LoopingSound? dead))
                DestroyLoop(dead);
    }

    /// <summary>Move each attached one-shot to its emitter's current position and prune entries whose player has
    /// finished or been recycled. Mirrors <see cref="DriveLoopingSounds"/> for the short-lived pooled players so a
    /// sound (a footstep, a gunshot, the local weapon) tracks the entity that made it for its whole duration
    /// instead of staying where it started — the fix for sounds going quiet/muffled while the listener moves.</summary>
    private void DriveAttachedOneShots()
    {
        for (int i = _attachedOneShots.Count - 1; i >= 0; i--)
        {
            (AudioStreamPlayer3D player, int netId) = _attachedOneShots[i];
            if (!GodotObject.IsInstanceValid(player) || !player.Playing)
            {
                _attachedOneShots.RemoveAt(i);
                continue;
            }
            if (EntityOriginResolver?.Invoke(netId) is { } pos)
                player.GlobalPosition = Coords.ToGodot(pos);
        }
    }

    /// <summary>Stop every looping sound emitted by <paramref name="netId"/> (any channel) — the emitter left the
    /// snapshot / was removed, so its loops (Arc beam, vehicle engine) must end even without an explicit stop.</summary>
    private void StopLoopsForEntity(int netId)
    {
        if (_loopingSounds.Count == 0 && _singleChannelPlayers.Count == 0)
            return;

        _loopScratch.Clear();
        foreach (var kv in _loopingSounds)
            if (kv.Key.netId == netId)
                _loopScratch.Add(kv.Key);
        for (int i = 0; i < _loopScratch.Count; i++)
            if (_loopingSounds.Remove(_loopScratch[i], out LoopingSound? ls))
                DestroyLoop(ls);

        _loopScratch.Clear();
        foreach (var kv in _singleChannelPlayers)
            if (kv.Key.netId == netId)
                _loopScratch.Add(kv.Key);
        for (int i = 0; i < _loopScratch.Count; i++)
        {
            if (_singleChannelPlayers.Remove(_loopScratch[i], out AudioStreamPlayer3D? p)
                && GodotObject.IsInstanceValid(p) && p.Playing)
                p.Stop();
        }
    }

    /// <summary>The local player fired — drive the view-model muzzle flash (CSQC W_MuzzleFlash).</summary>
    public void OnMuzzleFlash(string? effectOverride = null) => ViewModel?.Fire(effectOverride);

    // =================================================================================================
    //  Per-frame drive
    // =================================================================================================

    public override void _Process(double delta)
    {
        // The EffectSystem nodes and projectile trails advance themselves (they're Godot particle nodes);
        // ProjectileRenderer and the ModelAnimators also self-advance via their own _Process. We still poll
        // here for entity-bound animators that need their clip re-selected from movement each frame.
        foreach (var kv in _animators)
        {
            ModelAnimator anim = kv.Value;
            if (anim.FollowEntityFrame) continue; // networked frame drives it (CSQCMODEL_AUTOUPDATE)
            Entity? e = anim.Entity;
            if (e is not null && !e.IsFreed)
                SelectClipFromMovement(e, anim);
        }

        // Skeletal player models: synthesize the four-pose split + aim each frame and push the CPU bones.
        foreach (PlayerModel pm in _playerModels.Values)
        {
            Entity? e = pm.Bound;
            if (e is not null && !e.IsFreed && GodotObject.IsInstanceValid(pm))
                pm.Pose(e, (float)delta);
        }

        DriveCsqcModelHooks((float)delta);
        DriveVehicles((float)delta);
        DriveLoopingSounds((float)delta);
        DriveAttachedOneShots();
    }

    /// <summary>
    /// Port of the per-frame CSQCModel PreDraw hooks (<c>CSQCModel_Hook_PreDraw</c>, csqcmodel_hooks.qc:674) for
    /// every rendered entity: the appearance/glowmod + death-fade pass on player models (<see cref="ModelTint"/>),
    /// the LOD distance pick (<see cref="CsqcModelLod"/>, best-effort — see note), and the EF_*/MF_* effects pass
    /// (<see cref="CsqcModelEffects"/>). Mirrors the QC order: appearance → LOD → effects.
    /// </summary>
    private void DriveCsqcModelHooks(float delta)
    {
        if (_entityNodes.Count == 0 && _playerModels.Count == 0)
            return;

        float frameTime = Mathf.Clamp(delta, 0f, 0.1f); // QC bound(0, frametime, 0.1) used for the particle bursts
        NVec3? viewOrigin = ViewOrigin();

        foreach (var kv in _entityNodes)
        {
            EntityNode node = kv.Value;
            Entity? e = node.Entity;
            if (e is null || e.IsFreed || !GodotObject.IsInstanceValid(node))
                continue;

            CsqcState st = CsqcStateFor(e);

            // (1) APPEARANCE (player models only): the FORCECOLORS reassignment + glowmod from the colormap +
            //     the cl_deathglow death fade. MUST run before LOD in QC; here LOD is a no-op swap so order is
            //     moot, but we keep it first.
            if (st.IsPlayerModel)
            {
                bool dead = IsDeadModel(e);
                // Capture the death instant the first frame we observe it dead (death_time isn't networked).
                if (dead && !st.WasDead)
                    st.DeathTime = Now();
                st.WasDead = dead;
                bool ghost = (e.Effects & CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST) != 0;
                // FORCECOLORS: the cl_forceplayercolors family can reassign this.colormap (else keep e.Team).
                int colormap = ResolveForcedColormap(e);
                ModelTint.ApplyAppearance(node, colormap, dead, st.DeathTime, ghost);
            }

            // (2) LOD: compute the index (faithful math) — see ApplyLod for the swap caveat.
            ApplyLod(e, node, st, viewOrigin);

            // (3) EFFECTS: EF_* lights/particles/render-flags from the networked Effects bitfield. MF_* model
            //     flags aren't networked, so 0 here for remote entities (EF_BRIGHTFIELD trail still applies).
            //     Skip the per-frame material reset for the common no-effects prop UNLESS state is still active
            //     (so the frame effects turn off, we still run once to clear the light/render-flags) — QC sets a
            //     cheap int flag every frame; the port's reset walks meshes, so guard it to avoid churn.
            bool ghostBit = (e.Effects & CsqcModelEffectFlags.CSQCMODEL_EF_RESPAWNGHOST) != 0;
            bool effectsActive = e.Effects != 0 || ghostBit || st.Effects.LoopChannel != 0
                                 || st.Effects.LastEffects != 0; // re-run ONE frame after effects clear so the
                                                                 // render-flags/visibility/light all reset (no stick)
            if (effectsActive)
                // Trail return is unused here: entities in _entityNodes are non-projectiles (players/items/
                // monsters); projectiles own their trail via ProjectileRenderer. EF_BRIGHTFIELD on a player
                // model (rare) is the only dropped case — flagged as a known parity gap.
                _ = CsqcModelEffects.Apply(Effects, node, e, st.Effects, modelFlags: 0, frameTime, Api.Services?.Sound, ghostBit);

            // (4) ITEM VISIBILITY FX (item-only — these flags are never set on players/monsters): QC ItemDraw's
            //     alpha pass (client/items/items.qc:163-210). Priority matches QC (availability, then the expiring
            //     fade layered on top): an unavailable picked-up item is the cl_ghost_items "ghost"; an expiring
            //     loot item fades + emits despawn puffs; an available, non-expiring item resets to opaque once.
            if (e.ItemExpiringFx)
            {
                DriveItemDespawnFx(e, node, st);
                st.ItemFaded = true;
            }
            else if (!e.ItemAvailable)
            {
                DriveItemGhostFx(node);
                st.ItemFaded = true;
            }
            else if (st.ItemFaded)
            {
                // Back to available (respawned) — undo the prior ghost/despawn fade exactly once (no per-frame churn).
                SetTreeTransparency(node, 0f);
                node.Visible = true;
                st.ItemFaded = false;
            }
        }
    }

    /// <summary>
    /// Render a picked-up item awaiting respawn as the faded "ghost" — QC ItemDraw's <c>!ITS_AVAILABLE</c> branch
    /// (client/items/items.qc:182-186): <c>alpha *= cl_ghost_items</c> (default 0.45). <c>cl_ghost_items</c> 0
    /// hides it entirely (QC <c>alpha 0 → drawmask 0</c>). The optional tint (<c>cl_ghost_items_color</c>, default
    /// <c>'-1 -1 -1'</c> = no tint) is left for a follow-up. Reuses the per-instance transparency the despawn fade
    /// uses (never touches the shared/cached item materials); the bob+spin keeps running (driven in EntityNode).
    /// </summary>
    private void DriveItemGhostFx(EntityNode node)
    {
        float ghost = Mathf.Clamp(CvarF("cl_ghost_items", 0.45f), 0f, 1f); // QC autocvar_cl_ghost_items default 0.45
        SetTreeTransparency(node, 1f - ghost);
        node.Visible = ghost > 0.001f;
    }

    /// <summary>
    /// Drive one frame of a loot item's despawn animation (QC <c>ItemDraw</c>'s <c>ITS_EXPIRING</c> branch,
    /// client/items/items.qc:191-210): fade the item node's alpha and emit the accelerating
    /// <c>EFFECT_ITEM_DESPAWN</c> puffs at <c>origin + (0,0,16)</c>. Honors <c>cl_items_animate</c> bit 2 (fade)
    /// and bit 4 (particles); the pure timing/bit logic lives in <see cref="ItemDespawnFx"/> so it's unit-tested.
    /// </summary>
    private void DriveItemDespawnFx(Entity e, EntityNode node, CsqcState st)
    {
        ItemDespawnFx fx = st.Despawn ??= new ItemDespawnFx();
        int animate = (int)CvarF("cl_items_animate", 7f); // xonotic-client.cfg default 7 (bob+fade+particles)
        fx.Tick(Now(), animate, out float alpha, out bool emitPuff);

        // QC: this.alpha *= (wait - time)/IT_DESPAWNFX_TIME — apply as a per-instance transparency on the model
        // (bit 2). Bit 2 clear → Tick returns 1, so this is a no-op (item stays opaque, just particles).
        SetTreeTransparency(node, 1f - alpha);
        node.Visible = alpha > 0.001f; // hidden once fully faded (QC drawmask 0); transparency handles the gradient

        // QC: pointparticles(EFFECT_ITEM_DESPAWN, this.origin + '0 0 16', '0 0 0', 1) — same client path the
        // pickup/respawn bursts take (EffectSystem.Spawn); item_despawn resolves from effectinfo.txt.
        if (emitPuff)
            Effects.Spawn("ITEM_DESPAWN", e.Origin + new NVec3(0f, 0f, 16f));
    }

    /// <summary>
    /// Apply a whole-model transparency (0 = opaque, 1 = invisible) to every <see cref="GeometryInstance3D"/> in a
    /// node's subtree — the render-side analogue of QC's per-entity <c>.alpha</c>. Uses
    /// <see cref="GeometryInstance3D.Transparency"/> (a per-instance property, NOT a material edit) so it never
    /// mutates the shared/cached item materials. Child <c>Visible</c> is left untouched (other systems own it);
    /// the caller hides the root node outright once fully faded.
    /// </summary>
    private static void SetTreeTransparency(Node node, float transparency)
    {
        transparency = Mathf.Clamp(transparency, 0f, 1f);
        if (node is GeometryInstance3D gi)
            gi.Transparency = transparency;
        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
            SetTreeTransparency(node.GetChild(i), transparency);
    }

    /// <summary>
    /// QC <c>CSQCModel_LOD_Apply</c> index pick for an entity. The port renders a single resolved model node
    /// (not an engine modelindex it can swap by integer), and stock Xonotic ships <c>_lodN</c> variants for only
    /// some models; faithfully resolving + hot-swapping the alternate model file mid-render is out of this task's
    /// in-scope wire/render budget. So this computes the faithful LOD INDEX (so the decision is correct and
    /// testable) but, lacking a resolved alternate model, keeps lod0 — mirroring the QC fexists guard that keeps
    /// the base model when no <c>_lodN</c> file exists. Documented as a known parity gap in the T58 report.
    /// </summary>
    private void ApplyLod(Entity e, EntityNode node, CsqcState st, NVec3? viewOrigin)
    {
        if (viewOrigin is not { } vo)
            return;
        int detailReduction = st.IsPlayerModel
            ? (int)CvarF("cl_playerdetailreduction", 4f)
            : (int)CvarF("cl_modeldetailreduction", 1f);
        float dist1 = CvarF("cl_loddistance1", 1024f); // cfg seta 1024 overrides the qh inline 768 at runtime
        float dist2 = CvarF("cl_loddistance2", 3072f); // cfg seta 3072
        float distance = (e.Origin - vo).Length();
        // view_quality has no port equivalent (renderer LOD-quality global) → 1; current_viewzoom → 1 here.
        _ = CsqcModelLod.SelectLodIndex(detailReduction, distance, viewZoom: 1f, viewQuality: 1f, dist1, dist2);
        // No alternate LOD model resolved → keep lod0 (the QC fexists-miss path). See doc comment.
    }

    /// <summary>The active camera's Quake-space origin (CSQC <c>view_origin</c>) for LOD distance, or null.</summary>
    private NVec3? ViewOrigin()
    {
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null || !GodotObject.IsInstanceValid(cam))
            return null;
        return Coords.ToQuake(cam.GlobalPosition);
    }

    private CsqcState CsqcStateFor(Entity e)
    {
        if (!_csqc.TryGetValue(e.Index, out CsqcState? st))
        {
            st = new CsqcState { IsPlayerModel = _playerModels.ContainsKey(e.Index) || IsPlayerMd3(e.Index) };
            _csqc[e.Index] = st;
        }
        return st;
    }

    /// <summary>An MD3 animator bound to a player entity (the non-skeletal player path).</summary>
    private bool IsPlayerMd3(int index)
        => _animators.TryGetValue(index, out ModelAnimator? a) && GodotObject.IsInstanceValid(a)
           && a.Entity is { } e && e.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// QC <c>csqcmodel_isdead</c> for the death-fade. Base derives it from <c>IS_DEAD_FRAME(frame)</c> =
    /// <c>(frame==0||frame==1)</c> (csqcmodel_hooks.qc:374/408) — the MD3 death-anim frame convention. The
    /// XonoticGodot wire does NOT carry that 0/1 death frame for players (the server animates players client-side via
    /// <see cref="PlayerModel"/>/<see cref="SelectClipFromMovement"/>, and <see cref="Entity.Frame"/> is 0 for a
    /// LIVING idle player), so a literal <c>frame==0||frame==1</c> port would false-positive living players as
    /// dead. We instead use the networked DeadState / Health&lt;=0-with-real-MaxHealth, which captures the same
    /// intent faithfully against the port's wire. (Documented T58 parity note — matching Base's frame test here
    /// would reduce fidelity, not raise it.)
    /// </summary>
    private static bool IsDeadModel(Entity e)
        => e.DeadState is DeadFlag.Dying or DeadFlag.Dead || (e.Health <= 0f && e.MaxHealth > 0f);

    /// <summary>
    /// QC FORCECOLORS (csqcmodel_hooks.qc:239-327): the cl_forceplayercolors family can reassign a player's
    /// colormap (force-my-colors / force-enemy-colors / unique-enemy-colors). Returns the colormap to render with
    /// — the forced packed-palette colormap (&gt;=1024) when a force applies, else the entity's own networked team
    /// id (<see cref="Entity.Team"/>). With no <see cref="AppearanceProvider"/> wired (a pure demo) the override is
    /// inert and the entity keeps its own colormap. The pure decision is <see cref="CsqcModelAppearance.ResolveForcedColormap"/>.
    /// </summary>
    private int ResolveForcedColormap(Entity e)
    {
        int own = (int)e.Team;
        AppearanceContext? ctx = AppearanceProvider?.Invoke();
        if (ctx is null)
            return own;

        int fpc = (int)CvarF("cl_forceplayercolors", 0f);
        bool enabled = CsqcModelAppearance.ForcePlayerColorsEnabled(fpc, ctx.Is1v1, ctx.Teamplay, ctx.TeamCount, ctx.MyTeam);
        int forceMyColors = (int)CvarF("cl_forcemyplayercolors", 0f);
        int clColor = (int)CvarF("_cl_color", 0f);
        bool forceUnique = CvarF("cl_forceuniqueplayercolors", 0f) != 0f;
        bool isLocal = e.Index == ctx.LocalNetId;
        // QC works in 1024+17*team colormap units; the port networks a team id, so map both sides to that form so
        // the friend test (cm == 1024+17*myteam) reduces to (e.Team == myteam).
        int cm = 1024 + 17 * own;
        int forced = CsqcModelAppearance.ResolveForcedColormap(
            enabled, forceMyColors, clColor, forceUnique, isLocal, ctx.Is1v1, ctx.Teamplay, ctx.MyTeam,
            cm, ctx.PlayerLocalNum, e.Index);
        return forced != 0 ? forced : own;
    }

    private static float Now() => Api.Services?.Clock?.Time ?? 0f;

    private static float CvarF(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrWhiteSpace(s) ? fallback : Api.Cvars.GetFloat(name);
    }

    /// <summary>
    /// Position each vehicle driver at its entity and feed it the live presentation state derived from the
    /// networked entity (speed from velocity, leg frame, alive from health). Rotors/barrels/engine/heal-beam
    /// follow from there.
    /// </summary>
    private void DriveVehicles(float delta)
    {
        if (_vehicles.Count == 0)
            return;

        const float speedRef = 700f; // a representative top engine load speed (u/s) for the idle↔move crossfade
        foreach (VehicleVisuals vis in _vehicles.Values)
        {
            Entity? e = vis.Bound;
            if (e is null || e.IsFreed || !GodotObject.IsInstanceValid(vis))
                continue;

            // Follow the entity transform (Quake → Godot), like EntityNode.
            vis.Position = Coords.ToGodot(e.Origin);
            vis.Rotation = new Vector3(0f, -Mathf.DegToRad(e.Angles.Y), 0f);

            float speed01 = Mathf.Clamp(
                Mathf.Sqrt(e.Velocity.X * e.Velocity.X + e.Velocity.Y * e.Velocity.Y) / speedRef, 0f, 1f);
            bool alive = !(e.Health <= 0f && e.MaxHealth > 0f);

            // Firing/boost ride the entity's networked Effects bitfield (the server vehicle frame sets them).
            bool firing = (e.Effects & VehicleEffects.Firing) != 0;
            bool boosting = (e.Effects & VehicleEffects.Boosting) != 0;

            vis.Apply(new VehicleVisuals.State
            {
                Speed01 = speed01,
                Frame = (int)e.Frame,
                Alive = alive,
                Firing = firing,
                Boosting = boosting,
                // Heal-target rides the Bumblebee's networked heal state (see VehicleHealBeam wiring).
            }, delta);
        }
    }

    // =================================================================================================
    //  Model + animation attachment
    // =================================================================================================

    private static bool IsProjectile(Entity e)
    {
        string cn = e.ClassName.ToLowerInvariant();
        // A pickup (item/weapon/ammo box, or dropped-loot "item") is NEVER a projectile — even though its MODEL
        // name carries a weapon stem (a_rockets.md3, g_crylink.md3, g_rl.md3, …) that the substring test below
        // would otherwise match, routing the pickup to the ProjectileRenderer instead of showing its model. The
        // server's NetEntityKind already separated them; this keeps the client render routing consistent.
        if (cn == "item" || cn.StartsWith("item_") || cn.StartsWith("weapon_") || cn.StartsWith("ammo_"))
            return false;
        string s = cn + " " + e.Model.ToLowerInvariant();
        // QC projectiles share these classname/model stems (the server also sends a projectile's classname+netname
        // as its "model" so the catalog can type it); movetype FLY/TOSS/BOUNCE w/ owner is the deeper server test.
        if (s.Contains("projectile") || s.Contains("rocket") || s.Contains("grenade") || s.Contains("nade")
            || s.Contains("plasma") || s.Contains("electro") || s.Contains("crylink")
            || s.Contains("hagar") || s.Contains("seeker") || s.Contains("fireball") || s.Contains("blaster")
            || s.Contains("spike") || s.Contains("mine") || s.Contains("hook") || s.Contains("mortar")
            || s.Contains("devastator") || s.Contains("arc") || s.Contains("porto") || s.Contains("vaporizer"))
            return true;
        return false;
    }

    // =================================================================================================
    //  Vehicles (dedicated visual driver)
    // =================================================================================================

    /// <summary>
    /// Create (first sight) or re-bind a <see cref="VehicleVisuals"/> driver for a vehicle entity. The driver
    /// owns the body/rotors/barrels/engine-sound/heal-beam; <see cref="DriveVehicles"/> feeds it the live
    /// state and positions it each frame. Dependencies (effects/beams/model/sound) are wired from this client.
    /// </summary>
    private void UpdateVehicle(Entity entity)
    {
        if (!_vehicles.TryGetValue(entity.Index, out VehicleVisuals? vis))
        {
            vis = new VehicleVisuals
            {
                Name = $"vehicle#{entity.Index}",
                Effects = Effects,
                Beams = Beams,
                // Resolve a vehicle model by name through the shared resolver (wrap the name in a temp entity).
                ModelResolver = name => ModelResolver?.Invoke(new Entity { ClassName = "vehicle", Model = name }),
                SoundResolver = ResolveSound,
                AudioLoader = s => this.AudioLoader?.Invoke(s),
                Bound = entity,
            };
            AddChild(vis);
            vis.Assets = Assets;   // texture the vehicle body/guns built via ModelLoader.BuildModel/ModelAnimator
            vis.Build(entity.ClassName + " " + entity.Model);
            _vehicles[entity.Index] = vis;
        }
        else
        {
            vis.Bound = entity;
        }
    }

    /// <summary>
    /// QC FORCEMODEL (csqcmodel_hooks.qc:204-235): when <c>cl_forcemyplayermodel</c> (for a friend) or
    /// <c>cl_forceplayermodels</c> is set and the forced model resolves, build THAT model+skin for the player
    /// entity and attach it, returning true. Returns false (the caller renders the entity's own model) when no
    /// force applies, the entity isn't a player, no <see cref="AppearanceProvider"/>/<see cref="ForcedModelResolver"/>
    /// is wired, or the forced model can't be built (the QC <c>fexists</c>-miss → keep own). The forced model is a
    /// plain render node (it doesn't get the skeletal pose/animation path — a documented force-model parity note).
    /// </summary>
    private bool TryAttachForcedModel(Entity entity, EntityNode node)
    {
        if (ForcedModelResolver is null)
            return false;
        if (!entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase))
            return false;
        AppearanceContext? ctx = AppearanceProvider?.Invoke();
        if (ctx is null)
            return false;

        string forceMyModel = Api.Services is null ? "" : Api.Cvars.GetString("cl_forcemyplayermodel");
        bool hasForceMy = !string.IsNullOrEmpty(forceMyModel);
        bool forceAll = CvarF("cl_forceplayermodels", 0f) != 0f;
        if (!hasForceMy && !forceAll)
            return false; // no force-model cvar set → keep own (the common case)

        // isfriend = teamplay ? (this entity's team == myteam) : islocalplayer (csqcmodel_hooks.qc:205-210).
        bool isFriend = ctx.Teamplay ? ((int)entity.Team == ctx.MyTeam) : (entity.Index == ctx.LocalNetId);

        // Try cl_forcemyplayermodel first (friend only), then cl_forceplayermodels (everyone) — mirroring the QC
        // cascade. "good" = the forced resolver actually builds a node (the port's fexists analog).
        if (hasForceMy && isFriend)
        {
            int skin = (int)CvarF("cl_forcemyplayerskin", 0f);
            if (ForcedModelResolver(forceMyModel, skin) is { } myNode)
            {
                AttachForcedNode(entity, node, myNode);
                return true;
            }
        }
        if (forceAll)
        {
            string allModel = Api.Services is null ? "" : Api.Cvars.GetString("_cl_playermodel");
            int allSkin = (int)CvarF("_cl_playerskin", 0f);
            if (!string.IsNullOrEmpty(allModel) && ForcedModelResolver(allModel, allSkin) is { } allNode)
            {
                AttachForcedNode(entity, node, allNode);
                return true;
            }
        }
        return false; // forced model(s) didn't resolve → fall through to the entity's own model
    }

    /// <summary>Parent a forced-model render node under the entity node and run the appearance pass on it (so the
    /// FORCECOLORS/glowmod/deathglow tint applies to the forced model too). A forced <see cref="PlayerModel"/> is
    /// registered for the per-frame skeletal pose so it animates like the entity's own model would.</summary>
    private void AttachForcedNode(Entity entity, EntityNode node, Node3D forcedNode)
    {
        node.AddChild(forcedNode);
        if (forcedNode is PlayerModel pm)
        {
            pm.Bound = entity;
            _playerModels[entity.Index] = pm;
        }
        CsqcStateFor(entity).IsPlayerModel = true;
        ModelTint.ApplyAppearance(node, ResolveForcedColormap(entity), isDead: false, deathTime: 0f, isRespawnGhost: false);
    }

    private void TryAttachModel(Entity entity, EntityNode node)
    {
        // FORCEMODEL (csqcmodel_hooks.qc:204-235): cl_forcemyplayermodel (friend) / cl_forceplayermodels swaps the
        // player's model+skin to the forced one before it's built. When a force applies AND the forced model
        // resolves, render THAT instead of the entity's own model.
        if (TryAttachForcedModel(entity, node))
            return;

        // Players: a skeletal IQM with the CPU upper/lower split + view-pitch aim (driven each frame in _Process).
        if (PlayerModelResolver?.Invoke(entity) is { } playerModel)
        {
            playerModel.Bound = entity;
            node.AddChild(playerModel);
            _playerModels[entity.Index] = playerModel;
            // Mark this entity for the per-frame CSQC appearance/deathglow pass (DriveCsqcModelHooks). The
            // networked colormap rides on Entity.Team (ClientEntityView); FFA/unknown stays untinted, native glow.
            CsqcStateFor(entity).IsPlayerModel = true;
            // Seed the tint immediately so the model isn't a frame untinted (the per-frame pass refreshes it).
            ModelTint.ApplyAppearance(node, ResolveForcedColormap(entity), isDead: false, deathTime: 0f, isRespawnGhost: false);
            return;
        }

        Md3Data? md3 = ModelResolver?.Invoke(entity);
        if (md3 is null)
        {
            // Not an MD3 (the MD3 resolver passed): try the format-agnostic factory (IQM/DPM/MD3 via the asset
            // pipeline → textured + animated). This is what covers non-MD3 world props/items/monsters.
            if (EntityModelFactory?.Invoke(entity) is { } built)
            {
                node.AddChild(built);
                return;
            }

            // Nothing resolved at all: drop a small placeholder so the entity is still visible.
            node.AddChild(new MeshInstance3D
            {
                Name = "Placeholder",
                Mesh = new BoxMesh { Size = new Vector3(24f, 56f, 24f) },
                Position = new Vector3(0f, 28f, 0f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.65f) },
            });
            return;
        }

        if (md3.FrameCount > 1)
        {
            var anim = ModelAnimator.Create(md3, $"anim#{entity.Index}", Assets);
            anim.Entity = entity;
            // Networked entities play their server-computed frame directly (CSQCMODEL_AUTOUPDATE); local
            // (demo) entities fall to the movement-derived clip heuristic in SelectClipFromMovement.
            anim.FollowEntityFrame = _frameDriven.Contains(entity.Index);
            // Non-local player MD3 models: route the raw frame through the CSQC fallback-frame remap so a model
            // missing the melee/duckwalk anims falls back to a present frame (csqcmodel_hooks.qc:702).
            bool isPlayer = entity.ClassName.Equals("player", StringComparison.OrdinalIgnoreCase);
            anim.UseFallbackFrame = isPlayer && anim.FollowEntityFrame;
            if (isPlayer)
                CsqcStateFor(entity).IsPlayerModel = true;
            node.AddChild(anim);
            _animators[entity.Index] = anim;
        }
        else
        {
            node.AddChild(ModelLoader.BuildModel(md3, 0, Assets));
        }
    }

    private void UpdateAnimatorState(Entity entity)
    {
        if (_animators.TryGetValue(entity.Index, out ModelAnimator? anim) && !anim.FollowEntityFrame)
            SelectClipFromMovement(entity, anim);
    }

    /// <summary>
    /// Pick a locomotion clip from the entity's state — the client-side animation selection the libs flag
    /// (walk/strafe/idle/jump/death). Dead → death; airborne → jump; moving → run; else idle. If the model
    /// registered no such clips, <see cref="ModelAnimator.PlayIfChanged"/> simply no-ops and the current
    /// (auto-detected) clip keeps playing.
    /// </summary>
    private static void SelectClipFromMovement(Entity e, ModelAnimator anim)
    {
        string clip;
        bool dead = e.DeadState is DeadFlag.Dying or DeadFlag.Dead
                    || (e.Health <= 0f && e.MaxHealth > 0f);
        if (dead)
            clip = "death";
        else if (!e.OnGround && e.MoveType is MoveType.Walk or MoveType.Step or MoveType.Fly)
            clip = "jump";
        else
        {
            float speed2 = e.Velocity.X * e.Velocity.X + e.Velocity.Y * e.Velocity.Y;
            clip = speed2 > 100f ? "run" : "idle";
        }
        anim.PlayIfChanged(clip);
    }

    // =================================================================================================
    //  Helpers
    // =================================================================================================

    private string? ResolveSound(string bareSample)
    {
        if (SoundResolver is not null)
            return SoundResolver(bareSample);
        // Default convention: res://sound/<sample>.ogg (the Xonotic sound/ tree under the project).
        return $"res://sound/{bareSample}.ogg";
    }

    /// <summary>
    /// Resolve a sample to a playable <see cref="AudioStream"/>: the VFS <see cref="AudioLoader"/> first
    /// (the real mounted content packs), then the <see cref="ResolveSound"/> <c>res://</c> fallback.
    /// Returns null to stay silent (missing sample).
    /// </summary>
    private AudioStream? LoadStream(string bareSample)
    {
        AudioStream? stream = AudioLoader?.Invoke(bareSample);
        if (stream is not null)
            return stream;

        string? resPath = ResolveSound(bareSample);
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    private AudioStreamPlayer3D RentAudioPlayer()
    {
        foreach (AudioStreamPlayer3D p in _audioPool)
            if (GodotObject.IsInstanceValid(p) && !p.Playing)
                return p;

        var np = new AudioStreamPlayer3D { Name = $"sfx{_audioPool.Count}", MaxDistance = 4096f };
        AddChild(np);
        _audioPool.Add(np);
        return np;
    }

    private static string Safe(string s) => string.IsNullOrEmpty(s) ? "entity" : s;

    /// <summary>
    /// An <see cref="IEffectSink"/> that mirrors every in-process <c>EffectEmitter.Emit</c> onto the client
    /// renderer (and still forwards to whatever sink was previously installed, e.g. the recorder/network
    /// sink). This bridges server-side gameplay emission to the client visuals within one process, which is
    /// exactly the demo path; in a real client the network sink stays primary and the decoded effect comes
    /// in via <see cref="OnEffect(in EffectRequest)"/> instead.
    /// </summary>
    private sealed class RenderSink : IEffectSink
    {
        private readonly ClientWorld _world;
        public IEffectSink Inner { get; }

        public RenderSink(ClientWorld world, IEffectSink inner)
        {
            _world = world;
            Inner = inner;
        }

        public void Emit(in EffectRequest request)
        {
            Inner.Emit(request);                 // keep recording / networking behaviour intact
            if (!GodotObject.IsInstanceValid(_world))
                return;

            // Render on the client. Defer onto the node's thread — emission can come from a sim tick, and
            // building Godot nodes must happen on the main/scene thread. Capture by value (struct copy).
            string name = request.Effect?.Name ?? request.EffectName;
            NVec3 origin = request.Origin;
            NVec3 velocity = request.Velocity;
            int count = request.Count;
            Color? tint = null;
            if (request.ColorMin != default || request.ColorMax != default)
            {
                NVec3 mid = (request.ColorMin + request.ColorMax) * 0.5f;
                tint = new Color(mid.X, mid.Y, mid.Z);
            }

            ClientWorld world = _world;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(world))
                    world.Effects.Spawn(name, origin, velocity, count, tint);
            }).CallDeferred();
        }
    }
}
