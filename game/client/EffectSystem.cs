using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side renderer for the named particle-effect catalog (<see cref="Effects"/>): the Godot
/// successor to CSQC's effectinfo.txt particle player. The server emits <c>EffectRequest</c>s by EFFECT_*
/// name (see <c>EffectEmitter</c> / <c>Send_Effect</c>); this turns each name into a live Godot particle
/// node placed at the converted (Quake -> Godot) origin.
///
/// This is the GENERAL mechanism that subsumes the per-item <c>TODO(port,client)</c> markers scattered
/// across the libs ("EFFECT_EXPLOSION_SMALL", "EFFECT_*_MUZZLEFLASH", "EFFECT_*_ROCKET_TRAIL", spark/blood
/// bursts, teleport, etc.): rather than hand-coding a node per call-site, each effect name is classified by
/// a small set of prefix/substring rules into a <see cref="EffectClass"/>, and a per-class factory builds a
/// preset <see cref="GpuParticles3D"/> (one-shot burst or swept trail). New effectinfo names automatically
/// land in a reasonable class; named overrides refine the common ones (explosions, muzzleflashes, blood…).
///
/// Coordinate boundary: origins/velocities arrive in Quake space (the sim's convention) and are converted
/// with <see cref="Coords.ToGodot"/> here, exactly like every other render-side node.
///
/// Lifetime: every spawned node self-frees once its particles finish (one-shot <c>Emitting</c> + a
/// <c>SceneTreeTimer</c> sized to lifetime), so the caller fires and forgets.
/// </summary>
public partial class EffectSystem : Node3D
{
    /// <summary>Broad visual families an EFFECT_* name resolves to; each maps to a particle factory.</summary>
    public enum EffectClass
    {
        Explosion,    // explosion_*, *_explode, te_explosion, generator finalexplosion
        Smoke,        // smoke_*, smoking, smoke_ring, booster_smoke, torch
        Blood,        // blood, tr_blood, tr_slightblood
        Spark,        // sparks, *_sparks, te_spark, electricity
        MuzzleFlash,  // *_muzzleflash
        Impact,       // *_impact (bullet/laser/plasma hit puffs)
        Trail,        // tr_*, *_trail, *_thrust, fireball/firemine/seeker trails (IsTrail effects)
        Teleport,     // teleport, spawn, spawn_point, respawn_ghost
        Beam,         // *_beam, arc_lightning, *_pass (networked line effects; thin drawn here)
        Electro,      // electro_*, arc_*, shock — bluish energy bursts
        Fire,         // fire*, ef_flame, firefield, rage
        Pickup,       // item_pickup/respawn/despawn, healing, regen, armorrepair
        Generic,      // anything unclassified — a small neutral puff
    }

    /// <summary>A resolved color tint for an effect (RGB), already converted from the QC 0..1 emission color.</summary>
    private readonly record struct Tint(Color Color, bool HasOverride);

    // Cache the name->class resolution so repeated emissions of the same effect don't re-run the classifier.
    private readonly Dictionary<string, EffectClass> _classCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional beam renderer: beam-class effects (lightning/arc/laser/pass) draw as a real beam
    /// between origin and the velocity end-point instead of a particle burst when this is set.</summary>
    public BeamRenderer? Beams { get; set; }

    /// <summary>Global multiplier on particle counts (lets the host scale effect density for perf).</summary>
    [Export] public float DensityScale { get; set; } = 1f;

    /// <summary>Hard cap on simultaneous one-shot effect nodes; oldest are culled past this (cheap GC guard).</summary>
    [Export] public int MaxLiveEffects { get; set; } = 256;

    private readonly Queue<Node3D> _live = new();

    // =================================================================================================
    //  effectinfo.txt-driven path (T20): the real parsed catalog and the decal/casing sub-systems.
    //  The heuristic EffectClass machinery below is retained as a FALLBACK for names not in the file.
    // =================================================================================================

    /// <summary>
    /// The parsed effectinfo.txt catalog. When an effect name is present here, the burst is built from the
    /// REAL parameters (type/color/size/alpha/count/velocity/jitter/gravity/blend/light) rather than the
    /// name-classified heuristic. Loaded lazily on first use; a miss leaves it empty (heuristic fallback).
    /// Exposed so the host can inject a VFS text loader (<see cref="EffectInfo.TextLoader"/>) before use.
    /// </summary>
    public EffectInfo Info { get; } = new();

    /// <summary>
    /// The DP particle-texture atlas (particles/particlefont.tga + .txt). Built lazily from the host loaders
    /// below; when loaded, every particle billboard and projected decal is textured with the REAL sprite the
    /// effect's <c>tex</c>/<c>staintex</c> index selects (fireball/smoke/spark/blood/scorch) instead of a flat
    /// solid-color quad. Null/not-loaded => the solid-quad fallback (the game still runs with no atlas mounted).
    /// </summary>
    public ParticleFont? Font { get; private set; }

    /// <summary>Host-supplied texture loader (e.g. <c>AssetSystem.LoadTexture</c>) — resolves the particlefont
    /// atlas image. Set by the client before first use; combined with <see cref="VfsTextLoader"/> to build the
    /// <see cref="Font"/> and to source effectinfo.txt straight from the mounted VFS.</summary>
    public Func<string, Texture2D?>? TextureLoader { get; set; }

    /// <summary>Host-supplied text loader (e.g. <c>VirtualFileSystem.ReadText</c>) — reads effectinfo.txt and
    /// the particlefont UV table from the mounted content packs.</summary>
    public Func<string, string?>? VfsTextLoader { get; set; }

    private bool _infoLoadAttempted;

    /// <summary>Projected impact decals (bullet holes / scorch / blood stains). Created on first add to the tree.</summary>
    public Decals Decals { get; private set; } = null!;

    /// <summary>Ejected weapon shell casings. Created on first add to the tree.</summary>
    public ShellCasings Casings { get; private set; } = null!;

    /// <summary>Real model-gib bursts (player death / robot chunks). Created on first add to the tree.</summary>
    public ModelGibs Gibs { get; private set; } = null!;

    /// <summary>
    /// Optional host-supplied model loader (e.g. <c>AssetLoader.LoadModel</c>) shared with the casing and
    /// gib systems so they can render the real brass/limb meshes from the mounted content. When unset they
    /// fall back to generated placeholder meshes. Setting this late (after _Ready) is fine.
    /// </summary>
    public Func<string, Node3D?>? ModelLoader
    {
        get => _modelLoader;
        set
        {
            _modelLoader = value;
            if (Casings is not null) Casings.ModelLoader = value;
            if (Gibs is not null) Gibs.ModelLoader = value;
        }
    }
    private Func<string, Node3D?>? _modelLoader;

    public override void _Ready()
    {
        // Sub-systems live as children so they share this node's tree lifetime and transform.
        Decals = new Decals { Name = "Decals" };
        AddChild(Decals);
        Casings = new ShellCasings { Name = "Casings", ModelLoader = _modelLoader };
        AddChild(Casings);
        Gibs = new ModelGibs { Name = "Gibs", ModelLoader = _modelLoader };
        AddChild(Gibs);
    }

    /// <summary>
    /// Pre-warm the effect catalog + particle atlas off the hot fire path (DP precaches these at client/map
    /// init — cl_particles.c CL_Particles_ParseEffectInfo — not on the first te_particleeffect). Call this once
    /// at map load / spawn, AFTER the host has wired <see cref="TextureLoader"/>/<see cref="VfsTextLoader"/>.
    /// It only builds CPU-side resources (parse effectinfo.txt, decode the atlas image, pre-crop the common
    /// sprite bands) — it spawns NO visible particles. Idempotent: it shares <see cref="EnsureInfoLoaded"/>'s
    /// guard, so a later lazy Spawn is a no-op and the warm-up never runs twice.
    /// </summary>
    public void Warmup()
    {
        EnsureInfoLoaded();
        // Pre-crop the particlefont cells the common bursts use (smoke/dust 0-7, scorch/bullet decals 8-15,
        // blood decals 16-23, blood drops 24-31, ring 32, sparkle 38-46, fire/flame 48-55, white dot 63,
        // electro bolts 70-74). Cell()/DecalCell() cache per index, so the first real explosion/muzzleflash/
        // blood burst no longer pays the GetRegion + ImageTexture.CreateFromImage crop on its frame.
        if (Font?.Loaded == true)
        {
            for (int i = 0; i <= 74; i++)
            {
                Font.Cell(i);
                if (i <= 23) Font.DecalCell(i); // decal-form cells (luminance->alpha) for scorch/blood marks
            }
        }
    }

    /// <summary>A curated set of the most common effectinfo effects to warm the GPU pipeline for (A2): every
    /// weapon's explosion (the growth-shader fireball + smoke), a muzzleflash, a bullet/impact puff, sparks and
    /// blood. Names absent from the mounted effectinfo are skipped silently.</summary>
    private static readonly string[] WarmupEffectNames =
    {
        "rocket_explode", "grenade_explode", "hagar_explode", "electro_combo", "crylink_impact",
        "rocketlauncher_muzzleflash", "machinegun_muzzleflash", "TR_ROCKET", "TR_NEXUIZPLASMA",
        "bullet_impact", "spark_explode", "blood", "TE_EXPLOSION", "electro_impact",
    };

    /// <summary>
    /// Build one representative HIDDEN instance per effect family — a heuristic burst per <see cref="EffectClass"/>
    /// plus a curated set of common effectinfo bursts (<see cref="WarmupEffectNames"/>) — for the offscreen GPU
    /// warm pass (A2, <see cref="GpuWarmPass"/>). Each instance references the SAME cached materials/meshes a real
    /// burst uses (A1's <c>_heurMeshCache</c>/<c>_infoMeshCache</c>), so rendering them once offscreen compiles
    /// every particle draw + process pipeline (incl. the growth ShaderMaterial and the Sub/InvMod blend) — the
    /// first real explosion/muzzleflash/blood in play then hits a warm GPU. The returned nodes are NOT added to
    /// this system's tree or live pool; the warm pass parents, renders, and frees them.
    /// </summary>
    public List<Node3D> BuildWarmupInstances()
    {
        EnsureInfoLoaded();
        var list = new List<Node3D>();

        // One heuristic burst per class (Explosiveness handled inside BuildBurst; a small forward velocity so the
        // directional emitters build their cones). Covers the StandardMaterial3D mix/add draw pipelines.
        foreach (EffectClass kind in System.Enum.GetValues<EffectClass>())
        {
            if (kind == EffectClass.Beam) continue; // beams draw via BeamRenderer, warmed separately
            Tint tint = DefaultTint(kind, string.Empty);
            list.Add(BuildBurst(kind, Vector3.Zero, new Vector3(0f, 50f, 0f), 1, tint));
        }

        // A curated set of real effectinfo bursts (detached) so their specific draw materials — the growth
        // ShaderMaterial, the InvMod/Sub blend, velocity-aligned sparks — and process pipelines compile too.
        foreach (string name in WarmupEffectNames)
        {
            Effect? eff = ResolveEffect(name);
            IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(name, eff);
            if (blocks is null || blocks.Count == 0)
                continue;
            Node3D? built = BuildFromInfo(blocks, NVec3.Zero, NVec3.Zero, 1, null, isTrail: false, attach: false);
            if (built is not null)
                list.Add(built);
        }
        return list;
    }

    /// <summary>Lazily load effectinfo.txt the first time we need it (so a TextLoader set after _Ready still applies).</summary>
    private void EnsureInfoLoaded()
    {
        if (_infoLoadAttempted)
            return;
        _infoLoadAttempted = true;

        // Source effectinfo.txt straight from the mounted VFS when the host wired a text loader (so the
        // override/ precedence applies); otherwise the parser's disk fallback finds the content repo copy.
        if (VfsTextLoader is not null)
            Info.TextLoader = VfsTextLoader;
        try { Info.Load(); }
        catch { /* leave empty; heuristic fallback covers it */ }

        // Build the particle-texture atlas once. A miss leaves Font not-loaded and callers fall back to the
        // solid-quad/disc path, so this never blocks rendering.
        try { Font = ParticleFont.Load(TextureLoader, VfsTextLoader); }
        catch { Font = null; }

        GD.Print($"[EffectSystem] effectinfo: {Info.Count} effects, particlefont atlas: " +
                 (Font?.Loaded == true ? "loaded" : "MISSING (solid-quad fallback)"));
    }

    // =================================================================================================
    //  Public API — the entry point the net/client layer calls
    // =================================================================================================

    /// <summary>
    /// Spawn a one-shot effect by EFFECT_* registry name or effectinfo name (e.g. "EXPLOSION_BIG",
    /// "rocketlauncher_muzzleflash"). <paramref name="origin"/>/<paramref name="velocity"/> are in Quake
    /// space. For point effects <paramref name="count"/> scales the burst; for trail effects (resolved
    /// from the catalog) <paramref name="velocity"/> is treated as the trail END point. A non-null
    /// <paramref name="color"/> tints the particles (QC eent_net_color override).
    /// Returns the created node (already added to the tree), or null if the name is empty/Null.
    /// </summary>
    public Node3D? Spawn(string effectName, NVec3 origin, NVec3 velocity = default, int count = 1, Color? color = null)
    {
        if (string.IsNullOrEmpty(effectName))
            return null;

        Effect? effect = ResolveEffect(effectName);
        // EFFECT_Null and the empty effectinfo string are intentional no-renders.
        if (effect is not null && string.IsNullOrEmpty(effect.NetName))
            return null;

        EffectClass kind = Classify(effectName, effect);
        bool isTrail = effect?.IsTrail ?? IsTrailName(effectName);

        // Beam-class effects (lightning/arc/laser/pass/heal) are a line between two points (origin → velocity
        // end-point, the QC beam convention), drawn as a real beam rather than a particle burst when a beam
        // renderer is available. Electric ones crackle (jagged te_csqc_lightningarc); laser/rail/heal beams are
        // a straight cylinder (Draw_CylindricLine).
        if (kind == EffectClass.Beam && Beams is not null && velocity != default)
        {
            Color? beamTint = color is { } bc ? bc : null;
            bool jagged = HasAny(effectName, "lightning", "arc", "shock", "electro", "tesla");
            return jagged ? Beams.Arc(origin, velocity, beamTint) : Beams.Beam(origin, velocity, beamTint);
        }

        // --- effectinfo.txt-driven path (T20) ------------------------------------------------------------
        // If the file defines this effect, layer one Godot burst per parsed emitter block from the REAL
        // params and return. This subsumes the heuristic for everything in the file; only names absent from
        // effectinfo fall through to the EffectClass machinery below.
        EnsureInfoLoaded();
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(effectName, effect);
        if (blocks is not null && blocks.Count > 0)
        {
            Node3D? built = BuildFromInfo(blocks, origin, velocity, count, color, isTrail);
            if (built is not null)
                return built;
            // BuildFromInfo returns null only if every block was a non-render kind (e.g. all decals at a
            // point with no surface) — that's a legitimate no-op, mirror it rather than re-running heuristics.
            return null;
        }

        Tint tint = color is { } c ? new Tint(c, true) : DefaultTint(kind, effectName);

        Vector3 godotOrigin = Coords.ToGodot(origin);

        Node3D node;
        if (isTrail)
        {
            // Trail: sweep from origin to the velocity-end point (QC trailparticles convention).
            Vector3 godotEnd = Coords.ToGodot(velocity == default ? origin : velocity);
            node = BuildTrail(kind, godotOrigin, godotEnd, tint);
        }
        else
        {
            int n = Math.Max(1, (int)MathF.Round(count * DensityScale));
            node = BuildBurst(kind, godotOrigin, Coords.ToGodot(velocity), n, tint);
        }

        AddChild(node);
        // Now that the node is in the tree, schedule its self-removal once the one-shot burst finishes.
        // GpuParticles3D.Lifetime is the per-particle life; add a margin for the last particles to fade.
        float linger = (float)(node is GpuParticles3D gp ? gp.Lifetime : 1f) + 0.5f;
        ScheduleFree(node, linger);
        Track(node);
        return node;
    }

    /// <summary>Spawn directly from a structured <see cref="EffectRequest"/> (the net layer's decoded form).</summary>
    public Node3D? Spawn(in EffectRequest request)
    {
        Color? tint = null;
        // QC networks a min/max color range; use the midpoint as the representative tint when present.
        if (request.ColorMin != default || request.ColorMax != default)
        {
            NVec3 mid = (request.ColorMin + request.ColorMax) * 0.5f;
            tint = new Color(mid.X, mid.Y, mid.Z);
        }
        string name = request.Effect?.Name ?? request.EffectName;
        return Spawn(name, request.Origin, request.Velocity, request.Count, tint);
    }

    /// <summary>Convenience: explosion at a Quake-space point (te_explosion analogue).</summary>
    public Node3D? Explosion(NVec3 origin) => Spawn("TE_EXPLOSION", origin);

    /// <summary>Convenience: a muzzle flash at a Quake-space point/direction for a given weapon family name.</summary>
    public Node3D? MuzzleFlash(string effectName, NVec3 origin, NVec3 direction)
        => Spawn(effectName, origin, direction, 1);

    /// <summary>
    /// A muzzle flash ATTACHED to a weapon node (the first-person view-model's muzzle socket): the burst is
    /// parented under <paramref name="parent"/> in LOCAL coords, emitting forward out of the barrel (the socket's
    /// local −Z, the same axis the world-space <see cref="MuzzleFlash"/> uses via −muzzleXf.Basis.Z), so the
    /// particles ride the gun's sway/recoil/bob and pop the instant the local player clicks — the snappy
    /// first-person muzzle flash. Remote players still see the networked world-space copy (Send_Effect). Uses the
    /// heuristic muzzle-flash burst rather than the full effectinfo tree so localisation stays trivial; the local
    /// flash needn't be byte-identical to the networked one.
    /// </summary>
    public Node3D? MuzzleFlashAttached(string effectName, Node3D parent, float speed = 120f)
    {
        if (parent is null || !GodotObject.IsInstanceValid(parent))
            return null;
        Tint tint = DefaultTint(EffectClass.MuzzleFlash, effectName);
        Node3D node = BuildBurst(EffectClass.MuzzleFlash, Vector3.Zero, new Vector3(0f, 0f, -1f) * speed, 1, tint);
        if (node is GpuParticles3D gp)
            gp.LocalCoords = true; // emitted particles follow the gun (the attached feel)
        parent.AddChild(node);
        // Self-free after the brief burst; QueueFree-based so it works regardless of parent (and the node is freed
        // anyway when the weapon model is swapped). Not Track()'d — the view-model owns it, not the effect pool.
        ScheduleFree(node, 0.7f);
        return node;
    }

    /// <summary>
    /// Returns the particlefont atlas sprite for a named trail effect (e.g. "TR_ROCKET", "TR_NEXUIZPLASMA"),
    /// for use by the <see cref="ProjectileRenderer"/>'s continuous-emitter trail builder. Loads effectinfo
    /// lazily on first call. Returns null when the atlas isn't mounted, the effect isn't in effectinfo, or
    /// every block is a non-sprite type (decal / bubble / beam / underwater-only).
    /// </summary>
    public Texture2D? QueryTrailSprite(string trailEffectName)
    {
        if (string.IsNullOrEmpty(trailEffectName)) return null;
        EnsureInfoLoaded();
        Effect? effect = ResolveEffect(trailEffectName);
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(trailEffectName, effect);
        if (blocks is null) return null;
        foreach (EffectInfoEmitter block in blocks)
        {
            if (!block.Defined || block.Underwater) continue;
            if (block.Type is EiType.Decal or EiType.Bubble or EiType.Beam) continue;
            Texture2D? sprite = Font?.CellInRange(block.Tex0, block.Tex1);
            if (sprite is not null) return sprite;
        }
        return null;
    }

    /// <summary>
    /// The parsed effectinfo emitter blocks for a name (raw name, then the registry NetName) — the lookup
    /// the PERSISTENT map emitters (func_pointparticles / misc_laser end fx, game/client/MapParticleEmitters
    /// + LaserRenderer) use to configure long-lived <see cref="GpuParticles3D"/> nodes instead of churning a
    /// transient node per frame through <see cref="Spawn(string, NVec3, NVec3, int, Color?)"/>. Loads
    /// effectinfo lazily; null when the file is missing or the name is unknown.
    /// </summary>
    public IReadOnlyList<EffectInfoEmitter>? GetInfoBlocks(string effectName)
    {
        if (string.IsNullOrEmpty(effectName))
            return null;
        EnsureInfoLoaded();
        return LookupInfo(effectName, ResolveEffect(effectName));
    }

    /// <summary>The billboard mesh for one emitter block (sprite from the block's atlas range, blend per the
    /// parsed mode) — public so the persistent map emitters reuse the proven particle-material path.</summary>
    public Mesh BuildEmitterMesh(EffectInfoEmitter info, Color color)
        => BuildInfoMesh(info, color, Font?.CellInRange(info.Tex0, info.Tex1));

    // --- decals / casings / model-gibs entry points (T20) -------------------------------------------
    // These are the public hooks the net/client layer calls for the CSQC temp-entities that aren't plain
    // pointparticles: the `casings` casing-eject TE (casings.qc), the `net_gibsplash` gib splash
    // (gibs.qc), and a directly-requested impact decal. effectinfo `type decal` blocks already drive
    // Decals.Spawn through the normal Spawn() path; this is the explicit decal entry for callers that have a
    // surface normal/impact direction in hand.

    /// <summary>
    /// Eject a shell casing (the <c>casings</c> CSQC temp-entity): <paramref name="shell"/> selects the
    /// shotgun shell vs the default bullet casing. Origin/velocity are Quake space; <paramref name="floorZ"/>
    /// is an optional ground plane (Quake Z) the casing bounces on. No-op if the casing system isn't ready.
    /// </summary>
    public Node3D? SpawnCasing(NVec3 origin, NVec3 velocity, bool shell = false, float floorZ = float.NegativeInfinity)
        => Casings?.Spawn(origin, velocity,
            shell ? ShellCasings.CasingKind.Shell : ShellCasings.CasingKind.Bullet, floorZ);

    /// <summary>
    /// Spawn a full model-gib splash (the type-1 <c>net_gibsplash</c>): tosses the real limb/skull/eye MD3
    /// meshes outward from <paramref name="origin"/> (Quake space) scaled by <paramref name="amount"/>
    /// (the QC gibbage multiplier). Use this for player gib deaths instead of a generic blood burst.
    /// </summary>
    public void SpawnGibs(NVec3 origin, NVec3 velocity, float amount = 4f, float floorZ = float.NegativeInfinity)
        => Gibs?.Splash(origin, velocity, amount, floorZ);

    /// <summary>
    /// Drop a single impact decal at a Quake-space point, projected along <paramref name="impactDir"/> (the
    /// shot/explosion direction). For callers that resolve their own decal; the effectinfo path projects
    /// decals automatically for effects that declare a <c>type decal</c> block.
    /// </summary>
    public Node3D? SpawnDecal(NVec3 origin, NVec3 impactDir, float radius, Color color, float alpha = 1f)
        => Decals?.Spawn(origin, impactDir, radius, color, alpha);

    // =================================================================================================
    //  Name -> class resolution
    // =================================================================================================

    private static Effect? ResolveEffect(string name)
        => Effects.ByName(name) ?? Effects.ByEffectInfoName(name);

    /// <summary>Classify an effect name into a visual family (cached). Uses the catalog flag for trails.</summary>
    public EffectClass Classify(string effectName, Effect? effect = null)
    {
        if (_classCache.TryGetValue(effectName, out EffectClass cached))
            return cached;

        EffectClass kind = ClassifyUncached(effectName, effect ?? ResolveEffect(effectName));
        _classCache[effectName] = kind;
        return kind;
    }

    private static EffectClass ClassifyUncached(string name, Effect? effect)
    {
        // Normalise: match against both the EFFECT_* id and the effectinfo NetName (lower-case), since the
        // libs reference effects by either spelling. Substring tests are ordinal/case-insensitive.
        string id = name;
        string net = effect?.NetName ?? name;

        // Trails first — the catalog flag is authoritative; otherwise fall through to name heuristics.
        if ((effect?.IsTrail ?? false) || HasAny(net, "tr_", "_trail", "_thrust") || HasAny(id, "_TRAIL"))
        {
            // A trail still has a flavour: fire/plasma/blood trails read differently.
            if (HasAny(net, "blood")) return EffectClass.Blood;
            return EffectClass.Trail;
        }

        // Muzzleflashes (every weapon's *_muzzleflash / *_MUZZLEFLASH).
        if (HasAny(id, "MUZZLEFLASH") || HasAny(net, "muzzleflash"))
            return EffectClass.MuzzleFlash;

        // Explosions (explosion_*, *_explode, finalexplosion, te_explosion, ballexplode, joinexplode).
        if (HasAny(id, "EXPLO") || HasAny(net, "explo", "explode", "_ballexplode", "joinexplode", "blowup"))
            return EffectClass.Explosion;

        // Teleport / spawn-in / respawn ghost.
        if (HasAny(id, "TELEPORT", "SPAWN", "RESPAWN") || HasAny(net, "teleport", "spawn_event", "spawn_point", "respawn"))
            return EffectClass.Teleport;

        // Beams & passes (drawn as a thin line/streak between two points).
        if (HasAny(id, "BEAM", "LIGHTNING", "PASS", "LASER_BEAM") || HasAny(net, "beam", "lightning", "_pass", "g3"))
            return EffectClass.Beam;

        // Blood.
        if (HasAny(id, "BLOOD") || HasAny(net, "blood"))
            return EffectClass.Blood;

        // Sparks (sparks, *_sparks, te_spark, electricity_sparks, kaball_sparks).
        if (HasAny(id, "SPARK") || HasAny(net, "spark"))
            return EffectClass.Spark;

        // Electro / arc / shock — bluish energy bursts (impacts and combos).
        if (HasAny(id, "ELECTRO", "ARC", "SHOCK") || HasAny(net, "electro", "arc_", "shock", "lightning"))
            return EffectClass.Electro;

        // Fire family (fireball without trail, flame, firefield, firemine point, rage).
        if (HasAny(id, "FIRE", "FLAME", "RAGE") || HasAny(net, "fire", "flame", "rage", "torch"))
            return EffectClass.Fire;

        // Smoke (smoke_*, smoking, smoke_ring).
        if (HasAny(id, "SMOKE", "SMOKING") || HasAny(net, "smoke", "smoking"))
            return EffectClass.Smoke;

        // Item / pickup / heal / regen.
        if (HasAny(id, "ITEM_", "PICKUP", "HEALING", "REGEN", "ARMOR_REPAIR", "AMMO")
            || HasAny(net, "item_", "pickup", "healing", "regen", "armorrepair", "ammoregen"))
            return EffectClass.Pickup;

        // Impacts (*_impact) — generic hit puff; checked after the more specific families above.
        if (HasAny(id, "IMPACT") || HasAny(net, "impact"))
            return EffectClass.Impact;

        return EffectClass.Generic;
    }

    private static bool IsTrailName(string name)
        => HasAny(name, "_TRAIL", "tr_", "_trail", "_thrust");

    private static bool HasAny(string haystack, params string[] needles)
    {
        foreach (string n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // =================================================================================================
    //  Default tints per class (used when the emission carries no explicit color override)
    // =================================================================================================

    private static Tint DefaultTint(EffectClass kind, string name)
    {
        Color c = kind switch
        {
            EffectClass.Explosion => new Color(1.0f, 0.55f, 0.15f),   // orange fireball
            EffectClass.Smoke => new Color(0.35f, 0.35f, 0.37f),       // grey
            EffectClass.Blood => new Color(0.55f, 0.04f, 0.04f),       // dark red
            EffectClass.Spark => new Color(1.0f, 0.85f, 0.4f),         // yellow-white
            EffectClass.MuzzleFlash => new Color(1.0f, 0.9f, 0.55f),   // bright muzzle yellow
            EffectClass.Impact => new Color(0.75f, 0.7f, 0.6f),        // dusty puff
            EffectClass.Trail => new Color(0.9f, 0.7f, 0.4f),          // warm exhaust
            EffectClass.Teleport => new Color(0.5f, 0.7f, 1.0f),       // cyan-blue
            EffectClass.Beam => new Color(0.7f, 0.85f, 1.0f),          // pale energy
            EffectClass.Electro => new Color(0.35f, 0.55f, 1.0f),      // electro blue
            EffectClass.Fire => new Color(1.0f, 0.45f, 0.1f),          // fire orange
            EffectClass.Pickup => new Color(0.4f, 1.0f, 0.6f),         // green sparkle
            _ => new Color(0.8f, 0.8f, 0.8f),
        };

        // A couple of weapon-family hue refinements keyed off the name (plasma=blue, crylink=purple).
        if (HasAny(name, "plasma", "vortex", "nex", "hagar")) c = c.Lerp(new Color(0.4f, 0.6f, 1.0f), 0.4f);
        else if (HasAny(name, "crylink")) c = c.Lerp(new Color(0.8f, 0.4f, 1.0f), 0.5f);
        else if (HasAny(name, "rocket", "grenade", "mortar")) c = c.Lerp(new Color(1.0f, 0.5f, 0.1f), 0.3f);

        return new Tint(c, false);
    }

    // =================================================================================================
    //  Particle factories — one per class. Each returns a self-freeing node.
    // =================================================================================================

    private Node3D BuildBurst(EffectClass kind, Vector3 pos, Vector3 vel, int count, Tint tint)
    {
        // Per-class burst tuning: (baseCount, lifetime, speedMin, speedMax, scale, gravityZ, spread).
        (int baseCount, float life, float vMin, float vMax, float scale, float grav, float spread) = kind switch
        {
            EffectClass.Explosion => (48, 0.9f, 60f, 320f, 1.6f, -120f, 1f),
            EffectClass.Smoke => (16, 1.6f, 8f, 40f, 2.2f, 30f, 0.6f),
            EffectClass.Blood => (20, 0.7f, 30f, 140f, 0.5f, -400f, 0.7f),
            EffectClass.Spark => (24, 0.6f, 120f, 360f, 0.18f, -500f, 0.9f),
            EffectClass.MuzzleFlash => (12, 0.12f, 40f, 160f, 0.5f, 0f, 0.4f),
            EffectClass.Impact => (12, 0.45f, 20f, 90f, 0.35f, -200f, 0.8f),
            EffectClass.Teleport => (40, 1.1f, 30f, 110f, 0.6f, 60f, 1f),
            EffectClass.Electro => (30, 0.7f, 80f, 260f, 0.4f, 0f, 1f),
            EffectClass.Fire => (24, 0.8f, 20f, 90f, 1.0f, 120f, 0.7f),
            EffectClass.Pickup => (20, 1.0f, 20f, 80f, 0.4f, 80f, 0.9f),
            EffectClass.Beam => (16, 0.35f, 40f, 120f, 0.4f, 0f, 1f),
            EffectClass.Trail => (10, 0.5f, 10f, 50f, 0.5f, 0f, 0.5f),
            _ => (12, 0.6f, 20f, 100f, 0.5f, 0f, 0.8f),
        };

        int n = Math.Clamp(Math.Max(baseCount, count), 1, 1024);

        var particles = new GpuParticles3D
        {
            Name = $"fx_{kind}",
            Amount = n,
            Lifetime = life,
            OneShot = true,
            Explosiveness = kind == EffectClass.MuzzleFlash ? 1f : 0.85f,
            Emitting = true,
            Position = pos,
            // Directional emitters (muzzle/blood/spark) bias along the velocity; otherwise spherical.
        };

        var mat = new ParticleProcessMaterial
        {
            Direction = vel.LengthSquared() > 1f ? vel.Normalized() : Vector3.Up,
            Spread = spread * 180f * 0.5f,
            InitialVelocityMin = vMin,
            InitialVelocityMax = vMax,
            Gravity = new Vector3(0f, grav, 0f),
            ScaleMin = scale * 0.6f,
            ScaleMax = scale,
            // T2: tint lives ONLY in the color ramp (ApplyColorRamp encodes it) + this White base avoids the
            // base × ramp × albedo triple-multiply that darkened the heuristic bursts.
            Color = Colors.White,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = kind == EffectClass.Smoke ? 6f : 2f,
        };
        // Fade alpha over life and cool the color toward dark for fire/explosion.
        ApplyColorRamp(mat, kind, tint.Color);
        particles.ProcessMaterial = mat;
        particles.DrawPass1 = BuildParticleMesh(kind, tint.Color);
        return particles;
    }

    private Node3D BuildTrail(EffectClass kind, Vector3 start, Vector3 end, Tint tint)
    {
        // A trail (the heuristic fallback, used only for names absent from effectinfo) lays its particles
        // stepped ALONG the segment in one frame (T1), like the effectinfo path — not scattered through the
        // segment's bounding box over time. The emitter sits at `start` and the emission-point set places each
        // particle down the line.
        Vector3 seg = end - start;
        float len = seg.Length();

        (float life, float scale, float grav) = kind switch
        {
            EffectClass.Blood => (0.8f, 0.3f, -200f),
            EffectClass.Fire => (0.7f, 0.7f, 60f),
            _ => (0.6f, 0.45f, 0f),
        };

        // Density scales with segment length so a long trail isn't sparse (≈1 particle per 6 units).
        int n = Math.Clamp((int)(len / 6f) + 8, 8, 512);

        var particles = new GpuParticles3D
        {
            Name = $"trail_{kind}",
            Amount = n,
            Lifetime = life,
            OneShot = true,
            Explosiveness = 1.0f, // lay the whole line down this frame (the points distribute it in space)
            Emitting = true,
            Position = start,
            VisibilityAabb = new Aabb(new Vector3(-len - 32f, -len - 32f, -len - 32f), new Vector3(2f * len + 64f, 2f * len + 64f, 2f * len + 64f)),
        };

        var mat = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 12f,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 20f,
            Gravity = new Vector3(0f, grav, 0f),
            ScaleMin = scale * 0.5f,
            ScaleMax = scale,
            Color = Colors.White, // T2: tint via the color ramp only (see BuildBurst)
        };
        // Emission points stepped along the segment (local to `start`), with a tiny jitter so a thick effect
        // doesn't read as a perfectly straight pixel line.
        var pointImg = Image.CreateEmpty(n, 1, false, Image.Format.Rgbf);
        for (int i = 0; i < n; i++)
        {
            float t = (i + 0.5f) / n;
            Vector3 p = seg * t + new Vector3(
                (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0));
            pointImg.SetPixel(i, 0, new Color(p.X, p.Y, p.Z));
        }
        mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Points;
        mat.EmissionPointTexture = ImageTexture.CreateFromImage(pointImg);
        mat.EmissionPointCount = n;
        ApplyColorRamp(mat, kind, tint.Color);
        particles.ProcessMaterial = mat;
        particles.DrawPass1 = BuildParticleMesh(kind, tint.Color);
        return particles;
    }

    /// <summary>Fade particles toward transparent over their life; warm classes also cool toward dark.</summary>
    private static void ApplyColorRamp(ParticleProcessMaterial mat, EffectClass kind, Color baseColor)
    {
        var ramp = new Gradient();
        bool warm = kind is EffectClass.Explosion or EffectClass.Fire or EffectClass.MuzzleFlash;
        ramp.SetColor(0, warm ? new Color(1f, 1f, 0.85f, 1f) : baseColor);
        ramp.AddPoint(0.5f, baseColor);
        // End transparent (and darker for warm classes, like smoke cooling out of a fireball).
        Color end = warm ? new Color(0.2f, 0.18f, 0.18f, 0f) : new Color(baseColor.R, baseColor.G, baseColor.B, 0f);
        ramp.SetColor(ramp.GetPointCount() - 1, end);

        var tex = new GradientTexture1D { Gradient = ramp };
        mat.ColorRamp = tex;
    }

    // Per-(class, sprite) cache of the heuristic draw mesh+material. The mesh+material is a pure function of
    // (kind, sprite) — the albedo is always White (T2: the tint rides the per-particle vertex color, so the
    // `color` arg never reaches the material) — so it's shareable across emitters exactly like _trailResCache.
    // CellInRange still randomises the sprite cell per spawn, so keying by the RESOLVED sprite instance keeps
    // the per-burst wisp/blob variety while sharing the QuadMesh + StandardMaterial3D (and its first-use
    // pipeline compile). Removes 2 Godot Resource allocations per heuristic-burst spawn.
    private readonly Dictionary<(EffectClass, ulong), Mesh> _heurMeshCache = new();

    /// <summary>The billboard mesh each particle draws: a small unshaded quad tinted to the class and (when the
    /// atlas is mounted) textured with a representative particlefont sprite for that family. Cached + shared.</summary>
    private Mesh BuildParticleMesh(EffectClass kind, Color color)
    {
        (int t0, int t1) = HeuristicTex(kind);
        Texture2D? sprite = Font?.CellInRange(t0, t1);
        var key = (kind, sprite is null ? 0UL : sprite.GetInstanceId());
        if (_heurMeshCache.TryGetValue(key, out Mesh? cachedMesh))
            return cachedMesh;

        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        bool additive = kind is EffectClass.Explosion or EffectClass.MuzzleFlash or EffectClass.Spark
            or EffectClass.Electro or EffectClass.Fire or EffectClass.Beam or EffectClass.Teleport
            or EffectClass.Pickup;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            // Keep the per-particle scale through billboarding (godot#74897); without it large puffs collapse
            // to the 1×1 base quad and vanish.
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            // T2: White albedo — the tint is carried by the per-particle vertex COLOR (process material ramp).
            AlbedoColor = Colors.White,
            // Use the particle billboard path; let the shader read per-particle color from the process mat.
            DisableReceiveShadows = true,
        };
        ApplySprite(mat, sprite);
        quad.Material = mat;
        _heurMeshCache[key] = quad;
        return quad;
    }

    /// <summary>Quantise a color to a 5-bit-per-channel cache key (matches <c>Decals.SolidTexture</c>) so a
    /// continuum of tints collapses to a bounded set of shared resources.</summary>
    private static int ColorKey5(Color c)
        => ((int)(c.R * 31) << 10) | ((int)(c.G * 31) << 5) | (int)(c.B * 31);

    /// <summary>A representative particlefont tex range for a heuristic class (the effectinfo-less fallback),
    /// using the same DP atlas bands the file does: smoke 0-7, fire/flame 48-55, blood particle 24-31,
    /// sparkle 38-46, the white dot 63, the electro bolts 70-74, the ring 32.</summary>
    private static (int, int) HeuristicTex(EffectClass kind) => kind switch
    {
        EffectClass.Explosion => (48, 56),   // fire blobs
        EffectClass.Fire => (48, 56),
        EffectClass.MuzzleFlash => (48, 56),
        EffectClass.Smoke => (0, 8),         // smoke wisps
        EffectClass.Impact => (0, 8),        // dust puff
        EffectClass.Blood => (24, 32),       // blood droplets
        EffectClass.Spark => (63, 64),       // bright dot
        EffectClass.Electro => (70, 74),     // lightning sprites
        EffectClass.Teleport => (32, 33),    // ring
        EffectClass.Pickup => (38, 46),      // sparkle
        EffectClass.Beam => (63, 64),
        EffectClass.Trail => (63, 64),
        _ => (63, 64),                       // generic white particle
    };

    // =================================================================================================
    //  effectinfo.txt-driven burst construction (T20) — the faithful CL_NewParticlesFromEffectinfo port
    // =================================================================================================

    // DP world gravity (sv_gravity default); effectinfo `gravity` is a multiplier on this. We negate it for
    // the Godot ParticleProcessMaterial.Gravity Y (Godot Y is up; positive gravity should pull down).
    private const float DpGravity = 800f;

    /// <summary>Resolve the parsed emitter list for a name, trying the raw name then the registry NetName.</summary>
    private IReadOnlyList<EffectInfoEmitter>? LookupInfo(string effectName, Effect? effect)
    {
        if (!Info.Loaded)
            return null;
        IReadOnlyList<EffectInfoEmitter>? blocks = Info.Get(effectName);
        if (blocks is null && effect is not null && !string.IsNullOrEmpty(effect.NetName))
            blocks = Info.Get(effect.NetName);
        return blocks;
    }

    /// <summary>
    /// Build the layered set of Godot nodes for an effectinfo effect — one per emitter block, exactly as DP
    /// runs every same-named block in sequence. Returns a parent <see cref="Node3D"/> holding them, or null
    /// if no block produced a visible node. <paramref name="velocity"/> is the emit velocity for a point
    /// effect, or the trail END point for a trail effect (then the segment is origin..velocity).
    /// </summary>
    private Node3D? BuildFromInfo(IReadOnlyList<EffectInfoEmitter> blocks, NVec3 origin, NVec3 velocity,
        int count, Color? colorOverride, bool isTrail, bool attach = true)
    {
        var parent = new Node3D { Name = "fx_info", Position = Coords.ToGodot(origin) };
        bool any = false;

        // For a trail, originmins..originmaxs span the segment; for a point effect both are the origin and
        // the emit velocity is the supplied velocity (DP passes velocitymins==velocitymaxs for point fx).
        NVec3 originMin = origin;
        NVec3 originMax = isTrail && velocity != default ? velocity : origin;
        NVec3 emitVel = isTrail ? default : velocity;
        float traillen = (originMax - originMin).Length();

        // DP cl_particles.c:1600-1602: compute the spawn-center water state ONCE per call and gate each block
        // by it (underwater/notunderwater). Only WATER|SLIME count as "underwater" (not lava) — matching DP.
        NVec3 center = (originMin + originMax) * 0.5f;
        bool underwater = Api.Services is not null
            && (Api.Trace.PointContents(center) & (SuperContents.Water | SuperContents.Slime)) != 0;

        // The velocity-derived forward/right/up basis the relative origin/velocity offsets rotate into (DP
        // cl_particles.c:1745-1752). DP uses AnglesFromVectors(.., flippitch:false) → the makevectors-consistent
        // (round-trippable) angles, i.e. our FixedVecToAngles; for a trail the basis comes from the trail dir.
        NVec3 basisDir = isTrail ? (originMax - originMin) : emitVel;
        XonoticGodot.Common.Math.QMath.AngleVectors(
            XonoticGodot.Common.Math.QMath.FixedVecToAngles(basisDir),
            out NVec3 fwd, out NVec3 right, out NVec3 up);

        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined)
                continue;

            // Skip underwater-only blocks when dry and notunderwater-only blocks when submerged (DP:1625-1628).
            if (info.Underwater && !underwater)
                continue;
            if (info.NotUnderwater && underwater)
                continue;

            // Beam emitters only draw as a trail; if we have a beam renderer, route to it and skip particles.
            if (info.Orientation == EiOrientation.Beam)
            {
                if (Beams is not null && isTrail && velocity != default)
                {
                    Beams.Beam(origin, velocity, InfoTintColor(info, colorOverride));
                    any = true;
                }
                continue;
            }

            // Spawn the dlight this block requests (explosion flash etc.), independent of particles (DP spawns
            // the dlight before the cnt==0 particle check). Mark `any` so a pure-dlight block (cnt<=0: F3 cases
            // like TE_SMALLFLASH / grapple_muzzleflash / jumppad_activate) keeps the parent that holds the light.
            if (info.LightRadius > 0f)
            {
                SpawnInfoLight(parent, info, originMax, colorOverride);
                any = true;
            }

            switch (info.Type)
            {
                case EiType.Decal:
                {
                    // The offscreen GPU warm pass (attach:false) only needs the PARTICLE pipelines compiled; a
                    // projected decal would raycast into the live map and leave a stray scorch mark, so skip it.
                    if (!attach)
                        break;
                    // pt_decal (DP cl_particles.c:1674-1681): fire 32 rays out to originjitter[0] and project
                    // onto the NEAREST surface using its NORMAL — the emit velocity plays NO role. DP first
                    // shifts the center by relativeoriginoffset rotated into the velocity basis.
                    NVec3 baseAt = isTrail ? originMax : center;
                    NVec3 at = baseAt
                        + info.RelativeOriginOffset.X * fwd
                        + info.RelativeOriginOffset.Y * right
                        + info.RelativeOriginOffset.Z * up;
                    // INVMOD decals store the inverse of the visible color (white -> black scorch); invert so the
                    // Godot Decal's modulate lands the same mark instead of a bright square.
                    Color dcol = InfoDecalColor(info, colorOverride, useStain: false);
                    float radius = (info.SizeMin + info.SizeMax) * 0.5f;
                    // The scorch/bullet-mark sprite the block's tex range selects (8-15 in the atlas), in the
                    // Decal-node form (alpha = mark luminance) so it projects a shaped mark, not an opaque square.
                    ImageTexture? dtex = Font?.CellInRange(info.Tex0, info.Tex1, decal: true);
                    // maxdist = originjitter[0] (DP), the ray search radius for the nearest surface.
                    if (Decals?.SpawnProjected(at, info.OriginJitter.X, radius, dcol, info.MidAlpha01(), dtex) is not null)
                        any = true; // a decal landed on a surface (DP only spawns when bestfrac < 1)
                    break;
                }

                case EiType.RainDecal:
                case EiType.EntityParticle:
                    // RainDecal (a falling-rain splat decal) and EntityParticle (particles parented to a moving
                    // entity) need machinery the burst builder doesn't model; skip them rather than fake it.
                    break;

                // NOTE: pt_rain/pt_snow are NOT weather-only — Xonotic uses `type snow` for plain gameplay
                // particles (item_despawn's orange puff, plasma/wiz/vore trails, goldendust, the minsta lasers,
                // nex_beam). Treat them as a normal burst (the heuristic Pickup/etc. path would have too); the
                // drift/orientation nuance of true snow is a known approximation, not a reason to drop the puff.
                case EiType.Rain:
                case EiType.Snow:
                default:
                {
                    GpuParticles3D? p = BuildInfoBurst(info, originMin, originMax, emitVel, traillen, count,
                        colorOverride, isTrail, fwd, right, up);
                    if (p is not null)
                    {
                        // Position is relative to the parent (already at origin); emit positions are absolute,
                        // so park the emitter at the parent origin and let the process material place particles.
                        parent.AddChild(p);
                        any = true;

                        // DP collides particles vs world when bounce!=0; a pt_blood particle that hits solid leaves
                        // a blood decal+stain (cl_particles.c:3020-3041), and blood is emitted with bounce -1. Full
                        // per-particle world-tracing is architectural, so route the blood-splat to the decal system:
                        // a stain on the nearest surface around the spawn (no surface in range => none, like blood
                        // sprayed into open air). Gated on p!=null so the stain rate tracks the (fractional-count)
                        // particle rate as DP does, not once per call. Non-blood bounce<0 leaves NO decal in DP.
                        // Skipped during the warm pass (attach:false) — a stain would raycast into the live map.
                        if (attach && info.Type == EiType.Blood)
                            SpawnBloodSplat(info, center, colorOverride);
                    }
                    break;
                }
            }
        }

        if (!any)
        {
            parent.QueueFree();
            return null;
        }

        // attach:false — the offscreen GPU warm pass (A2) owns the node itself (parents it under its warm
        // viewport, renders a couple of frames to compile the pipelines, then frees it); skip the live-pool
        // bookkeeping so a warm burst never enters the visible scene or the MaxLiveEffects cull queue.
        if (!attach)
            return parent;

        AddChild(parent);
        ScheduleFree(parent, InfoParentLinger(blocks));
        Track(parent);
        return parent;
    }

    /// <summary>
    /// Drop a blood stain on the nearest surface around a blood emitter's spawn (F6) — the bounded stand-in for
    /// DP's per-particle blood-vs-world collision (cl_particles.c:3020-3041), where a pt_blood particle that hits
    /// solid leaves a <c>tex_blooddecal</c> mark. We don't trace each particle, so we project ONE stain at the
    /// spawn center via the nearest-surface raycast (no surface in range => no stain, like blood into open air).
    /// </summary>
    private void SpawnBloodSplat(EffectInfoEmitter info, NVec3 center, Color? colorOverride)
    {
        if (Decals is null)
            return;
        // Use the declared stain (staintex/staincolor/stainsize/stainalpha) when present — that's the dedicated
        // blood-decal sprite (atlas 16-23) and tint DP leaves where blood hits. The blood particle color is
        // stored INVMOD-inverted (0xA8FFFF cyan -> dark red), so InfoDecalColor inverts it and folds in the
        // neutral staincolor. Falls back to the emitter's own size/color when no stain was declared.
        Color blood = InfoDecalColor(info, colorOverride, useStain: true);
        float radius = info.HasStain
            ? MathF.Max(2f, info.StainMidSize() * 2f)         // stain half-size -> world radius
            : MathF.Max(2f, info.SizeMax * 1.5f);
        float alpha = info.HasStain
            ? MathF.Max(0.4f, info.StainMidAlpha01())
            : MathF.Max(0.4f, info.MidAlpha01());
        ImageTexture? sprite = info.HasStain
            ? Font?.CellInRange(info.StainTex0, info.StainTex1, decal: true)
            : Font?.CellInRange(16, 24, decal: true); // default blood-decal band when a blood block omitted staintex
        // Maxdist: a small surface-search radius so the stain stays local to the hit.
        Decals.SpawnProjected(center, 32f, radius, blood, alpha, sprite);
    }

    /// <summary>Build one GPU-particle emitter from a single parsed emitter block (the per-block spawn loop).</summary>
    private GpuParticles3D? BuildInfoBurst(EffectInfoEmitter info, NVec3 originMin, NVec3 originMax,
        NVec3 emitVel, float traillen, int requestedCount, Color? colorOverride, bool isTrail,
        NVec3 fwd, NVec3 right, NVec3 up)
    {
        // DP cl_particles.c:1710-1721: cnt = countabsolute + (pcount*countmultiplier)*quality, plus the
        // trailspacing term for a trail — quality (our DensityScale) multiplies ONLY the multiplier/trailspacing
        // terms, NEVER countabsolute (F10). Then the persistent per-info accumulator drains integer particles and
        // carries the fraction across calls, so sub-1 counts (e.g. count 0.025) spawn ~1 particle every ~40 calls
        // instead of over-spawning every call (F2).
        float pcount = MathF.Max(1f, requestedCount);
        float cnt = info.CountAbsolute + pcount * info.CountMultiplier * DensityScale;
        if (isTrail && info.TrailSpacing > 0f && traillen > 0f)
            cnt += (traillen / info.TrailSpacing) * DensityScale;

        // DP: if (cnt == 0) continue; — a pure-dlight block (TE_SMALLFLASH, grapple muzzleflash/impact,
        // jumppad_activate) emits only its dlight, no billboard (F3). The dlight was already spawned by the caller.
        if (cnt <= 0f)
            return null;

        // Persistent fractional accumulator (DP particleeffectinfo_t.particleaccumulator), bounded as DP does.
        info.ParticleAccumulator = Math.Clamp(info.ParticleAccumulator + cnt, 0.0, 16384.0);
        int n = (int)info.ParticleAccumulator; // integer particles to spawn this call
        info.ParticleAccumulator -= n;         // keep the remainder for next time
        if (n <= 0)
            return null; // fraction not yet accumulated to a whole particle — nothing to draw this call
        n = Math.Clamp(n, 1, 1024);

        // Colors: DP picks a random lerp between color[0] and color[1] per particle; Godot's process material
        // has a single base color, so use the midpoint and let the (optional) emission override win.
        Color baseColor = InfoTintColor(info, colorOverride);

        // Sizes are world-unit half-sizes; Godot quad scale is the full edge, so 2*size. sizeincrease is
        // units/sec growth — fold it into a scale-over-life curve.
        float sizeMin = MathF.Max(0.01f, info.SizeMin) * 2f;
        float sizeMax = MathF.Max(0.01f, info.SizeMax) * 2f;
        float life = info.Lifetime();

        var particles = new GpuParticles3D
        {
            Name = $"ei_{info.Type}",
            Amount = n,
            Lifetime = life,
            OneShot = true,
            // Both a point burst and a (T1) line trail spawn ALL their particles this frame: the burst explodes
            // outward, the trail lays its particles down the segment at once (the emission-point set below already
            // distributes them in space, so there's nothing to spread over time — a beam must appear instantly).
            Explosiveness = 1.0f,
            Emitting = true,
            // Pin a generous visibility box so a burst whose particles fly out far (velocityjitter up to 900) +
            // grow big isn't frustum-culled as a whole once the emitter point drifts off-screen (Godot's auto
            // AABB tracks only the emitter origin). Defensive — cheap for a short-lived one-shot.
            VisibilityAabb = new Aabb(new Vector3(-384f, -384f, -384f), new Vector3(768f, 768f, 768f)),
        };

        var mat = new ParticleProcessMaterial();

        // --- emission position: origin range + originjitter (an ellipsoid radius per axis) -------------
        // Place the emission box to span originMin..originMax (a point if both equal), centered on the parent
        // origin. Add originjitter as extra box extents (DP adds jitter*VectorRandom per axis).
        // DP cl_particles.c:1751 & 1773: the emission origin gets relativeoriginoffset rotated into the velocity
        // basis (forward/right/up), then a flat originoffset added in world axes (F5). Both are constant per
        // emitter, so fold them into the span center here.
        NVec3 relOrigin =
            info.RelativeOriginOffset.X * fwd
            + info.RelativeOriginOffset.Y * right
            + info.RelativeOriginOffset.Z * up;

        if (isTrail && traillen > 0.001f)
        {
            // T1 — a trail lays its particles stepped ALONG the segment (DP cl_particles.c:1763-1780), each
            // jittered by originjitter per axis, ALL this frame. We encode the per-particle spawn positions into
            // a Godot emission-POINT texture (one RGB pixel = one local position) instead of spraying a box. This
            // is what makes the vortex beam (nex_beam, 4 trail blocks) read as a crisp line of particles in the
            // frame it fired, rather than a cloud fading in across the segment's bounding box over its lifetime.
            NVec3 segStart = originMin + relOrigin + info.OriginOffset;
            NVec3 segEnd = originMax + relOrigin + info.OriginOffset;
            NVec3[] ptsQ = XonoticGodot.Engine.Effects.TrailGeometry.PointsAlongSegment(
                segStart, segEnd, n, info.OriginJitter, () => (float)GD.RandRange(-1.0, 1.0));
            var pointImg = Image.CreateEmpty(n, 1, false, Image.Format.Rgbf);
            for (int i = 0; i < n; i++)
            {
                Vector3 g = Coords.ToGodot(ptsQ[i] - originMin); // local to the parent (which sits at originMin)
                pointImg.SetPixel(i, 0, new Color(g.X, g.Y, g.Z));
            }
            mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Points;
            mat.EmissionPointTexture = ImageTexture.CreateFromImage(pointImg);
            mat.EmissionPointCount = n;
            particles.Position = Vector3.Zero;
        }
        else
        {
            NVec3 spanCenterQ = (originMin + originMax) * 0.5f + relOrigin + info.OriginOffset;
            NVec3 spanHalfQ = (originMax - originMin) * 0.5f;
            // Convert the Quake-space jitter/half-extent magnitudes to Godot axes (abs, since extents are radii).
            Vector3 jitterG = AbsToGodot(info.OriginJitter);
            Vector3 spanHalfG = AbsToGodot(spanHalfQ);
            Vector3 centerOffsetG = Coords.ToGodot(spanCenterQ - originMin); // parent is at originMin
            mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            mat.EmissionBoxExtents = new Vector3(
                MathF.Max(0.01f, spanHalfG.X + jitterG.X),
                MathF.Max(0.01f, spanHalfG.Y + jitterG.Y),
                MathF.Max(0.01f, spanHalfG.Z + jitterG.Z));
            particles.Position = centerOffsetG;
        }

        // --- velocity: emitVel*velmult + velocityoffset + relativevelocityoffset(basis) + velocityjitter*rand
        // DP cl_particles.c:1752 adds relativevelocityoffset rotated into the velocity basis (F5).
        NVec3 relVel =
            info.RelativeVelocityOffset.X * fwd
            + info.RelativeVelocityOffset.Y * right
            + info.RelativeVelocityOffset.Z * up;
        NVec3 baseVelQ = emitVel * info.VelocityMultiplier + info.VelocityOffset + relVel;
        Vector3 baseVelG = Coords.ToGodot(baseVelQ);
        float baseSpeed = baseVelG.Length();
        if (baseSpeed > 0.001f)
        {
            mat.Direction = baseVelG / baseSpeed;
            mat.InitialVelocityMin = baseSpeed;
            mat.InitialVelocityMax = baseSpeed;
            mat.Spread = 0f;
        }
        else
        {
            mat.Direction = Vector3.Up;
            mat.InitialVelocityMin = 0f;
            mat.InitialVelocityMax = 0f;
            mat.Spread = 0f;
        }
        // velocityjitter is an isotropic-ish random add; model it with a spherical velocity-pivot spread plus a
        // randomized speed magnitude scaled to the average jitter radius. This reproduces the "spray" feel.
        Vector3 vjG = AbsToGodot(info.VelocityJitter);
        float jitterSpeed = (vjG.X + vjG.Y + vjG.Z) / 3f;
        if (jitterSpeed > 0.001f)
        {
            // Add the jitter as extra velocity spread: widen the cone and the speed band.
            mat.Spread = baseSpeed > 0.001f ? 60f : 180f;
            mat.InitialVelocityMin = MathF.Max(0f, mat.InitialVelocityMin - jitterSpeed);
            mat.InitialVelocityMax = mat.InitialVelocityMax + jitterSpeed;
            // Keep the originjitter Box emission shape even with no base velocity: DP always spawns within
            // trailpos +/- originjitter[axis] regardless of velocity (cl_particles.c:1773). Switching to a Sphere
            // here left EmissionSphereRadius at Godot's default 1.0 and collapsed the originjitter spawn volume —
            // explosions/impacts (originjitter 8-16) emitted from a near-point instead of the intended cloud.
        }

        // --- gravity: multiplier on world gravity, pulling down (-Z Quake = -Y Godot) ------------------
        mat.Gravity = new Vector3(0f, -DpGravity * info.Gravity, 0f);

        // --- bounce (F6, partial): DP ricochets particles off world when bounce>0 (cl_particles.c:3048-3053).
        // Wire Godot's rigid particle collision so positive-bounce emitters (sparks/casings spray) bounce IF the
        // scene has GpuParticlesCollision* colliders; with none present (the current scene) this is inert, not a
        // regression. bounce<0 (blood removal+stain) is handled by SpawnBloodSplat, not here. DP bounce is the
        // restitution coefficient, which maps directly onto CollisionBounce.
        if (info.Bounce > 0.0001f)
        {
            mat.CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Rigid;
            mat.CollisionBounce = Math.Clamp(info.Bounce, 0f, 1f);
            mat.CollisionFriction = 0f;
        }

        // --- air friction (F4) -------------------------------------------------------------------------
        // DP cl_particles.c:2972-2976 applies velocity-PROPORTIONAL (exponential) decay every frame for ANY
        // nonzero airfriction, including NEGATIVE (which accelerates): v(t) = v0 * exp(-airfriction * t).
        // Godot's process-material Damping is LINEAR (units/sec) and can't accelerate, so:
        //  * positive airfriction -> a linear Damping equal to the exponential's AVERAGE deceleration over the
        //    particle life: total speed lost = v0*(1-exp(-k*life)); / life gives the mean units/sec. This is
        //    dimensionally correct and far closer than the old k*v0*0.5 constant.
        //  * negative airfriction (acceleration) -> Godot can't ramp speed up via Damping, so instead of
        //    DROPPING it (the old '>0f' guard silently dropped 57 emitters) we leave Damping at 0 and raise the
        //    initial speed to the exponential's average speed over life, so the particle covers ~the same
        //    distance the acceleration would have produced.
        float k = info.AirFriction;
        if (k > 0.0001f)
        {
            float decay = (1f - MathF.Exp(-k * life)) / MathF.Max(0.0001f, life); // mean (units/sec) per unit speed
            float d = baseSpeed * decay;
            mat.DampingMin = d;
            mat.DampingMax = d;
        }
        else if (k < -0.0001f && baseSpeed > 0.001f)
        {
            // Mean of v0*exp(-k*t) over [0,life] (k<0 => grows) = v0*(exp(-k*life)-1)/(-k*life). Scale the
            // initial speed by that mean/v0 ratio so the emitted particles read as the faster, spreading spray.
            float klife = -k * life;
            float meanRatio = (MathF.Exp(klife) - 1f) / MathF.Max(0.0001f, klife);
            meanRatio = MathF.Min(meanRatio, 8f); // cap the boost so a long-lived emitter can't explode the speed
            mat.InitialVelocityMin *= meanRatio;
            mat.InitialVelocityMax *= meanRatio;
        }

        // --- size + sizeincrease ----------------------------------------------------------------------
        // DP grows/shrinks each particle by `sizeincrease` (world units/sec) over its life. The Godot-native
        // way is ParticleProcessMaterial.scale_curve (a CurveTexture), but that path is ENGINE-BUGGY: a scale
        // curve combined with a non-Point emission shape (we use Box) + a non-default amount + Particle
        // billboard mode makes GPUParticles3D silently STOP DRAWING (godotengine/godot#75748, #76332 — closed
        // "won't fix"). That is exactly why every explosion fire/smoke and every rocket/grenade smoke-TRAIL
        // emitter — all of which carry `sizeincrease` — rendered INVISIBLE, while the sparks/decals/debris
        // (no sizeincrease, so no scale curve) drew fine. So instead of a scale curve we bake the size SPAN
        // (birth..death) straight into scale_min/scale_max: the burst spawns puffs across that range, reading
        // as an expanding cloud for positive sizeincrease (or holding the big birth size for the negative-
        // sizeincrease fire flash), and ALWAYS renders. We lose per-particle temporal growth; visibility wins.
        // T6: a billboard particle with a nonzero sizeincrease GROWS over its life in a custom draw shader
        // (scaled by INSTANCE_CUSTOM.z = lifetime phase) — the muzzle flash pops then shrinks, the nex_impact
        // shockwave ring expands, smoke billows. Sparks keep the baked span (they're velocity-aligned, not
        // billboarded). When not growing, fall back to baking the size SPAN into scale_min/max (the proven
        // always-renders path that sidesteps godot#75748's invisible-billboard scale-curve bug).
        float? growRatio = ComputeGrowthRatio(info, sizeMin, sizeMax, life);
        if (growRatio is not null)
        {
            mat.ScaleMin = sizeMin;
            mat.ScaleMax = sizeMax;
            EnableLifetimePhase(mat); // drive INSTANCE_CUSTOM.z 0→1 over life for the growth shader
        }
        else
        {
            float grow = info.SizeIncrease * 2f * life; // total edge-size change over the particle's life
            float endMin = sizeMin + grow, endMax = sizeMax + grow;
            mat.ScaleMin = MathF.Max(0.4f, MathF.Min(sizeMin, endMin));
            mat.ScaleMax = MathF.Max(mat.ScaleMin, MathF.Max(sizeMax, endMax));
        }

        // --- color + alpha over life (alphafade) ------------------------------------------------------
        // CRITICAL (T2): the tint must be applied in exactly ONE place or it compounds. Godot multiplies the
        // particle color through base Color × ColorRamp × ColorInitialRamp, then the draw mesh multiplies that
        // vertex COLOR by its AlbedoColor. Setting the tint on ALL of them (the old bug) cubed it — a 0x808080
        // smoke rendered at ~0.125 instead of 0.5. So: the ColorRamp carries ONLY the alpha fade (white RGB);
        // the draw mesh's AlbedoColor is White (see the BuildInfoMesh call below); and the tint lives in EITHER
        // ColorInitialRamp (per-particle random lerp, DP color[0]..color[1]) or the flat base Color.
        bool perParticleColor = colorOverride is null && info.Color0 != info.Color1;
        mat.Color = perParticleColor ? Colors.White : baseColor;
        ApplyInfoColorRamp(mat, info);
        // Per-particle color randomization: DP picks a random lerp between color[0] and color[1] per particle.
        // ColorInitialRamp assigns each particle a random tint sampled from the gradient at spawn time. With the
        // base Color White and the ramp white-RGB, this is the SOLE tint when it's set.
        if (perParticleColor)
        {
            Color c0 = new Color(((info.Color0 >> 16) & 0xFF) / 255f, ((info.Color0 >> 8) & 0xFF) / 255f, (info.Color0 & 0xFF) / 255f);
            Color c1 = new Color(((info.Color1 >> 16) & 0xFF) / 255f, ((info.Color1 >> 8) & 0xFF) / 255f, (info.Color1 & 0xFF) / 255f);
            var initGrad = new Gradient();
            initGrad.SetColor(0, c0);
            initGrad.SetColor(1, c1);
            mat.ColorInitialRamp = new GradientTexture1D { Gradient = initGrad };
        }

        // --- rotation (rotate base/spin) --------------------------------------------------------------
        if (info.RotateSpinMin != 0f || info.RotateSpinMax != 0f || info.RotateBaseMax != info.RotateBaseMin)
        {
            mat.AngleMin = info.RotateBaseMin;
            mat.AngleMax = info.RotateBaseMax;
            float spinMin = info.RotateSpinMin, spinMax = info.RotateSpinMax;
            mat.AngularVelocityMin = spinMin;
            mat.AngularVelocityMax = spinMax;
        }

        // T3 — velocity-aligned sparks. DP draws pt_spark / orientation=spark as a quad STRETCHED along the
        // particle's velocity (cl_particles.c:2812-2825), not a camera-facing billboard. Godot's
        // ZBillboardYToVelocity transform-align orients each particle's local +Y to its velocity while still
        // facing the camera about that axis — exactly the spark streak. The elongated quad (its long axis = +Y)
        // then reads as an outward-flying streak that falls with gravity, instead of a fixed screen-space sliver.
        if (info.Orientation == EiOrientation.Spark)
            particles.TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity;

        particles.ProcessMaterial = mat;
        // Sprite: one representative atlas cell from the block's [tex0,tex1) range. DP randomises the cell PER
        // PARTICLE; we pick one PER EMITTER (CellInRange already randomises within the range, so repeated bursts
        // still vary the wisp/blob). We deliberately do NOT pack the range into a ParticlesAnimHFrames sprite
        // strip + AnimOffset: that GPU sprite-sheet path rendered every multi-cell billboard INVISIBLE (the fire
        // `static`, the `smoke`, the dark `alphastatic` smoke, debris — all tex ranges), while the single-cell
        // emitters (spark tex 40) drew fine. One static cell uses that same proven single-sprite path so the
        // fire and smoke puffs actually show, at the cost of per-particle cell variety (a fair trade).
        Texture2D? sprite = Font?.CellInRange(info.Tex0, info.Tex1);
        // T2: pass White as the mesh albedo — the tint already lives in the process material's color pipeline
        // (mat.Color / ColorInitialRamp), read through as the per-particle vertex COLOR. (BuildEmitterMesh, the
        // persistent-map-emitter caller, still passes its baseColor — its own pipeline is separate.)
        particles.DrawPass1 = BuildInfoMesh(info, Colors.White, sprite,
            sparkAspect: SparkAspect(info, baseSpeed, jitterSpeed), growRatio: growRatio);
        return particles;
    }

    // =================================================================================================
    //  Projectile trails (T5) — effectinfo-driven CONTINUOUS emitters for a flying projectile
    // =================================================================================================

    /// <summary>
    /// Build the per-block CONTINUOUS trail emitters for a named effectinfo trail (TR_ROCKET, TR_CRYLINKPLASMA,
    /// FIREBALL, …) so the <see cref="ProjectileRenderer"/> can give a projectile its REAL layered trail instead
    /// of one invented puff stream. Each non-underwater block that declares a <c>trailspacing</c> becomes a
    /// world-space (LocalCoords=false) emitter whose per-second particle rate tracks the projectile's
    /// speed / trailspacing — so a rocket smokes grey, streams an orange fire core BACKWARD (velocitymultiplier
    /// -1.5) and throws bright sparks, exactly the DP layering. <paramref name="initialVelocityGodot"/> is the
    /// projectile's spawn velocity (Godot space); velocitymultiplier blocks inherit it (baked at spawn —
    /// projectiles fly ~straight, so this stays faithful without per-frame updates). The emitters are NOT yet
    /// parented (the caller adds them as children of the projectile root). Returns null when effectinfo isn't
    /// loaded or the name has no usable trail block — the caller then keeps its legacy single-emitter trail.
    /// </summary>
    public List<GpuParticles3D>? BuildProjectileTrailEmitters(
        string trailEffectName, Vector3 initialVelocityGodot, Color? tint = null)
    {
        if (string.IsNullOrEmpty(trailEffectName))
            return null;
        EnsureInfoLoaded();
        IReadOnlyList<EffectInfoEmitter>? blocks = LookupInfo(trailEffectName, ResolveEffect(trailEffectName));
        if (blocks is null || blocks.Count == 0)
            return null;

        float speed = initialVelocityGodot.Length();
        Vector3 velDir = speed > 0.001f ? initialVelocityGodot / speed : Vector3.Forward;
        if (speed < 1f) speed = 1200f; // velocity not yet networked at spawn — assume a typical projectile speed

        var emitters = new List<GpuParticles3D>();
        foreach (EffectInfoEmitter info in blocks)
        {
            if (!info.Defined || info.Underwater) continue;        // skip placeholder + underwater-only blocks
            if (info.TrailSpacing <= 0f) continue;                 // not a per-distance trail block
            if (info.Type is EiType.Decal or EiType.RainDecal or EiType.EntityParticle) continue;
            if (info.Orientation == EiOrientation.Beam) continue;  // beams aren't projectile trails

            GpuParticles3D? e = BuildOneTrailEmitter(info, speed, velDir, tint);
            if (e is not null) emitters.Add(e);
        }
        return emitters.Count > 0 ? emitters : null;
    }

    /// <summary>Configure one continuous world-space trail emitter from a parsed trail block (T5). Mirrors the
    /// per-block physics/color/size of <see cref="BuildInfoBurst"/>, but emits per-distance at the projectile.</summary>
    private GpuParticles3D? BuildOneTrailEmitter(EffectInfoEmitter info, float speed, Vector3 velDir, Color? tint)
    {
        float life = info.Lifetime();
        // DP spawns one trail particle per `trailspacing` units of travel; the per-second rate is therefore
        // speed/trailspacing. Godot's emit rate is Amount/Lifetime, so Amount = rate*life (× DensityScale).
        float rate = (speed / info.TrailSpacing) * DensityScale;
        int amount = Math.Clamp((int)MathF.Ceiling(rate * life), 2, 256);

        var p = new GpuParticles3D
        {
            Name = $"trail_{info.Type}",
            Amount = amount,
            Lifetime = life,
            OneShot = false,
            Emitting = true,
            LocalCoords = false, // particles stay in world space, left behind the moving projectile
            Explosiveness = 0f,  // continuous stream
            VisibilityAabb = new Aabb(new Vector3(-256f, -256f, -256f), new Vector3(512f, 512f, 512f)),
        };

        var mat = new ParticleProcessMaterial();
        Color baseColor = InfoTintColor(info, tint);
        bool perParticleColor = tint is null && info.Color0 != info.Color1;

        // emission: a small box around the projectile origin; originjitter gives the trail its thickness.
        Vector3 jitterG = AbsToGodot(info.OriginJitter);
        mat.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
        mat.EmissionBoxExtents = new Vector3(
            MathF.Max(0.05f, jitterG.X), MathF.Max(0.05f, jitterG.Y), MathF.Max(0.05f, jitterG.Z));

        // velocity: projectile velocity * velocitymultiplier + velocityoffset (world space). A velmult of -1.5
        // (TR_ROCKET fire core) streams the particles BACKWARD out of the nozzle; the smoke layer's velmult≈0
        // leaves them roughly stationary; velocityjitter sprays the sparks.
        Vector3 baseVelG = velDir * (speed * info.VelocityMultiplier) + Coords.ToGodot(info.VelocityOffset);
        float baseSpeed = baseVelG.Length();
        if (baseSpeed > 0.001f)
        {
            mat.Direction = baseVelG / baseSpeed;
            mat.InitialVelocityMin = baseSpeed;
            mat.InitialVelocityMax = baseSpeed;
        }
        else
        {
            mat.Direction = Vector3.Up;
            mat.InitialVelocityMin = 0f;
            mat.InitialVelocityMax = 0f;
        }
        Vector3 vjG = AbsToGodot(info.VelocityJitter);
        float jitterSpeed = (vjG.X + vjG.Y + vjG.Z) / 3f;
        if (jitterSpeed > 0.001f)
        {
            mat.Spread = baseSpeed > 0.001f ? 45f : 180f;
            mat.InitialVelocityMin = MathF.Max(0f, mat.InitialVelocityMin - jitterSpeed);
            mat.InitialVelocityMax += jitterSpeed;
        }

        mat.Gravity = new Vector3(0f, -DpGravity * info.Gravity, 0f);

        // air friction (same exponential→linear approximation as the burst path)
        float k = info.AirFriction;
        if (k > 0.0001f)
        {
            float decay = (1f - MathF.Exp(-k * life)) / MathF.Max(0.0001f, life);
            float d = baseSpeed * decay;
            mat.DampingMin = d;
            mat.DampingMax = d;
        }
        else if (k < -0.0001f && baseSpeed > 0.001f)
        {
            float klife = -k * life;
            float meanRatio = MathF.Min((MathF.Exp(klife) - 1f) / MathF.Max(0.0001f, klife), 8f);
            mat.InitialVelocityMin *= meanRatio;
            mat.InitialVelocityMax *= meanRatio;
        }

        // size + sizeincrease (T6: temporal growth via the draw shader when nonzero, else the baked span)
        float sizeMin = MathF.Max(0.01f, info.SizeMin) * 2f;
        float sizeMax = MathF.Max(0.01f, info.SizeMax) * 2f;
        float? growRatio = ComputeGrowthRatio(info, sizeMin, sizeMax, life);
        if (growRatio is not null)
        {
            mat.ScaleMin = sizeMin;
            mat.ScaleMax = sizeMax;
            EnableLifetimePhase(mat);
        }
        else
        {
            float grow = info.SizeIncrease * 2f * life;
            mat.ScaleMin = MathF.Max(0.4f, MathF.Min(sizeMin, sizeMin + grow));
            mat.ScaleMax = MathF.Max(mat.ScaleMin, MathF.Max(sizeMax, sizeMax + grow));
        }

        // color + alpha (de-compounded — the tint lives in exactly one place; see BuildInfoBurst/T2)
        mat.Color = perParticleColor ? Colors.White : baseColor;
        ApplyInfoColorRamp(mat, info);
        if (perParticleColor)
        {
            Color c0 = new Color(((info.Color0 >> 16) & 0xFF) / 255f, ((info.Color0 >> 8) & 0xFF) / 255f, (info.Color0 & 0xFF) / 255f);
            Color c1 = new Color(((info.Color1 >> 16) & 0xFF) / 255f, ((info.Color1 >> 8) & 0xFF) / 255f, (info.Color1 & 0xFF) / 255f);
            var initGrad = new Gradient();
            initGrad.SetColor(0, c0);
            initGrad.SetColor(1, c1);
            mat.ColorInitialRamp = new GradientTexture1D { Gradient = initGrad };
        }

        if (info.RotateSpinMin != 0f || info.RotateSpinMax != 0f || info.RotateBaseMax != info.RotateBaseMin)
        {
            mat.AngleMin = info.RotateBaseMin;
            mat.AngleMax = info.RotateBaseMax;
            mat.AngularVelocityMin = info.RotateSpinMin;
            mat.AngularVelocityMax = info.RotateSpinMax;
        }

        if (info.Orientation == EiOrientation.Spark)
            p.TransformAlign = GpuParticles3D.TransformAlignEnum.ZBillboardYToVelocity;

        p.ProcessMaterial = mat;
        Texture2D? sprite = Font?.CellInRange(info.Tex0, info.Tex1);
        p.DrawPass1 = BuildInfoMesh(info, Colors.White, sprite,
            sparkAspect: SparkAspect(info, baseSpeed, jitterSpeed), growRatio: growRatio);
        return p;
    }

    // =================================================================================================
    //  Temporal sizeincrease (T6) — grow/shrink each billboard particle over its life in a draw shader
    // =================================================================================================

    /// <summary>
    /// The end:start size ratio for a billboard particle that grows/shrinks over its life (DP sizeincrease,
    /// world units/sec). Returns null when there's no growth to animate (sizeincrease ~0) or the particle is a
    /// velocity-aligned spark (those keep the baked span). <paramref name="sizeMin"/>/<paramref name="sizeMax"/>
    /// are the spawn edge sizes (2*half-size). The shader lerps the quad scale 1→ratio over the lifetime phase.
    /// </summary>
    private static float? ComputeGrowthRatio(EffectInfoEmitter info, float sizeMin, float sizeMax, float life)
    {
        if (info.Orientation == EiOrientation.Spark || MathF.Abs(info.SizeIncrease) <= 0.0001f)
            return null;
        float avgEdge = MathF.Max(0.01f, (sizeMin + sizeMax) * 0.5f);
        float endEdge = avgEdge + info.SizeIncrease * 2f * life; // sizeincrease is half-size/sec -> *2 edge/sec
        return Math.Clamp(endEdge / avgEdge, 0.02f, 12f);
    }

    /// <summary>Drive <c>INSTANCE_CUSTOM.z</c> from 0 (birth) to 1 (death) over the particle's lifetime so the
    /// growth draw shader can read the lifetime phase — anim_speed 1, no offset, no loop (the value isn't a
    /// sprite frame here, just the phase ramp).</summary>
    private static void EnableLifetimePhase(ParticleProcessMaterial mat)
    {
        mat.AnimSpeedMin = 1f;
        mat.AnimSpeedMax = 1f;
        mat.AnimOffsetMin = 0f;
        mat.AnimOffsetMax = 0f;
    }

    // One compiled growth shader per blend mode (the render_mode is baked into the source), shared across
    // every growing emitter; only the lightweight ShaderMaterial (grow + sprite uniforms) is per-emitter.
    private static readonly Dictionary<BaseMaterial3D.BlendModeEnum, Shader> _growthShaders = new();

    /// <summary>The cached growth shader for a blend mode: a camera-facing billboard (keeping per-particle scale
    /// and rotation) that scales the quad by <c>mix(1, grow, lifetimePhase)</c>, reproducing DP's per-particle
    /// sizeincrease. Built once per blend; the same Godot billboard-particle vertex math the StandardMaterial3D
    /// path uses, plus the growth term — so it renders identically except for the temporal size.</summary>
    private static Shader GrowthShader(BaseMaterial3D.BlendModeEnum blend)
    {
        if (_growthShaders.TryGetValue(blend, out Shader? cached))
            return cached;
        string blendMode = blend switch
        {
            BaseMaterial3D.BlendModeEnum.Add => "blend_add",
            BaseMaterial3D.BlendModeEnum.Sub => "blend_sub",
            _ => "blend_mix",
        };
        var shader = new Shader
        {
            Code =
                "shader_type spatial;\n" +
                // Keep depth TESTING on (walls occlude the particle) but depth WRITE off for transparency
                // (depth_draw_opaque = transparent frags don't write depth). depth_test_disabled was the bug
                // that let the rocket explosion / smoke draw THROUGH walls.
                "render_mode " + blendMode + ", unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
                "uniform sampler2D albedo_tex : source_color, filter_linear;\n" +
                "uniform float grow = 1.0;\n" +
                "void vertex() {\n" +
                "    float angle = INSTANCE_CUSTOM.x;\n" +
                "    float phase = clamp(INSTANCE_CUSTOM.z, 0.0, 1.0);\n" +
                "    float gs = mix(1.0, grow, phase);\n" +
                "    vec3 r = normalize(INV_VIEW_MATRIX[0].xyz) * (length(MODEL_MATRIX[0].xyz) * gs);\n" +
                "    vec3 u = normalize(INV_VIEW_MATRIX[1].xyz) * (length(MODEL_MATRIX[1].xyz) * gs);\n" +
                "    vec3 f = normalize(INV_VIEW_MATRIX[2].xyz);\n" +
                "    mat4 bb = mat4(vec4(r, 0.0), vec4(u, 0.0), vec4(f, 0.0), MODEL_MATRIX[3]);\n" +
                "    mat4 rot = mat4(vec4(cos(angle), -sin(angle), 0.0, 0.0), vec4(sin(angle), cos(angle), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));\n" +
                "    MODELVIEW_MATRIX = VIEW_MATRIX * (bb * rot);\n" +
                "}\n" +
                "void fragment() {\n" +
                "    vec4 t = texture(albedo_tex, UV);\n" +
                "    ALBEDO = t.rgb * COLOR.rgb;\n" +
                "    ALPHA = t.a * COLOR.a;\n" +
                "}\n",
        };
        _growthShaders[blend] = shader;
        return shader;
    }

    /// <summary>
    /// The length:width aspect ratio for a velocity-stretched spark quad (T3). DP stretches a spark to
    /// <c>len = max(stretch*0.04*speed, size*0.5)</c> along velocity with cross-width <c>size</c>
    /// (cl_particles.c:2812-2825). Our quad is <c>(0.5, aspect)</c> scaled by ScaleMin/Max (≈2*size), so the
    /// aspect is <c>stretch*0.04*speed / (size)</c>, clamped to a sane streak range. <paramref name="baseSpeed"/>
    /// and <paramref name="jitterSpeed"/> are the emitter's directed + jitter speeds (the spark's expected speed).
    /// </summary>
    private static float SparkAspect(EffectInfoEmitter info, float baseSpeed, float jitterSpeed)
    {
        if (info.Orientation != EiOrientation.Spark)
            return 8f;
        float stretch = info.StretchFactor > 0f ? info.StretchFactor : 1f;
        float expectedSpeed = baseSpeed + jitterSpeed;
        float size = MathF.Max(0.5f, info.SizeMin + info.SizeMax); // ~2*avg half-size (the quad's scaled edge)
        return Math.Clamp(stretch * 0.04f * expectedSpeed / size, 1f, 30f);
    }

    /// <summary>The representative tint for an emitter: the explicit emission override, else the color midpoint.</summary>
    private static Color InfoTintColor(EffectInfoEmitter info, Color? colorOverride)
    {
        if (colorOverride is { } c)
            return c;
        (float r, float g, float b) = info.MidColor();
        return new Color(r, g, b);
    }

    /// <summary>
    /// The visible tint for a PROJECTED decal (scorch / stain). DP draws decals with PBLEND_INVMOD, where the
    /// stored color is the INVERSE of what the player sees (a scorch stores white -> renders black; blood stores
    /// cyan 0xA8FFFF -> renders dark red). A Godot <see cref="Decal"/> modulates rather than inverse-modulates,
    /// so we invert here to land the same final color. An explicit emission override is taken as-is (already a
    /// final color). When a stain color is supplied (0xRRGGBB, typically 0x808080 neutral) it multiplies the
    /// inverted base, matching DP's particle*staincolor product.
    /// </summary>
    private static Color InfoDecalColor(EffectInfoEmitter info, Color? colorOverride, bool useStain)
    {
        if (colorOverride is { } c)
            return c;
        (float r, float g, float b) = info.MidColor();
        bool invmod = info.Blend == EiBlend.InvMod;
        if (invmod) { r = 1f - r; g = 1f - g; b = 1f - b; } // stored inverse -> visible color
        if (useStain && info.HasStain)
        {
            (float sr, float sg, float sb) = info.StainMidColor();
            r *= sr; g *= sg; b *= sb; // particle color * staincolor (DP stainmap product)
        }
        return new Color(r, g, b);
    }

    /// <summary>Fade alpha from the initial opacity to 0 over the particle life (DP alphafade). The RGB is left
    /// WHITE so this ramp carries ONLY the alpha curve — the tint comes from <c>mat.Color</c> / the initial ramp
    /// (T2 single-application; multiplying a tinted ramp on top of a tinted base/albedo is what darkened bursts).</summary>
    private static void ApplyInfoColorRamp(ParticleProcessMaterial mat, EffectInfoEmitter info)
    {
        float a0 = info.MidAlpha01();
        // alphafade is alpha-units/sec; over `life` seconds it drops a0 by (alphafade/256)*life. Clamp to 0.
        float life = info.Lifetime();
        float a1 = Math.Clamp(a0 - (info.AlphaFade / 256f) * life, 0f, a0);

        var ramp = new Gradient();
        ramp.SetColor(0, new Color(1f, 1f, 1f, a0));
        // For additive/blood, fade fully to transparent at the end so the burst dissipates cleanly.
        ramp.SetColor(1, new Color(1f, 1f, 1f, info.AlphaFade > 0f ? 0f : a1));
        mat.ColorRamp = new GradientTexture1D { Gradient = ramp };
    }

    /// <summary>Composite cache key for <see cref="BuildInfoMesh"/>: everything that determines the mesh+material.</summary>
    private readonly record struct InfoMeshKey(
        ulong Sprite, int Blend, bool Spark, int SparkAspectQ, int GrowRatioQ, int ColorKey, int CellCount);

    // Per-determinant cache of the effectinfo draw mesh+material (the PRIMARY effect path: every burst block and
    // every projectile-trail emitter draws through here). The mesh+material is a pure function of the key below;
    // CellInRange randomises the sprite cell per spawn, so keying by the resolved sprite instance KEEPS the
    // per-emitter sprite variety while sharing the QuadMesh + (StandardMaterial3D | growth ShaderMaterial) and
    // its first-use pipeline compile across emitters. Removes 2–3 Godot Resource allocations per emitter spawn.
    private readonly Dictionary<InfoMeshKey, Mesh> _infoMeshCache = new();

    /// <summary>The billboard mesh for one emitter, blended per the parsed blend mode (add/alpha/invmod) and
    /// textured with the particlefont sprite (<paramref name="sprite"/>, null => a flat tinted quad).
    /// <paramref name="cellCount"/> &gt; 1 enables sprite-sheet particle animation so AnimOffset can randomise
    /// the starting cell per particle (one cell of the horizontal strip per particle). Cached + shared.</summary>
    private Mesh BuildInfoMesh(EffectInfoEmitter info, Color color, Texture2D? sprite, int cellCount = 1,
        float sparkAspect = 8f, float? growRatio = null)
    {
        // Sparks are velocity-stretched in DP (length = max(stretch*0.04*speed, size*0.5), width = size). The
        // GpuParticles node aligns the quad's +Y to velocity (TransformAlign, set in BuildInfoBurst); here we
        // shape the quad so width≈size and length≈aspect*size. The per-particle scale (ScaleMin/Max ≈ 2*size)
        // multiplies both, so a (0.5,aspect) base quad gives world width 0.5*2*size=size and length aspect*size.
        bool isSpark = info.Orientation == EiOrientation.Spark;
        BaseMaterial3D.BlendModeEnum blend = info.Blend switch
        {
            EiBlend.Add => BaseMaterial3D.BlendModeEnum.Add,
            EiBlend.InvMod => BaseMaterial3D.BlendModeEnum.Sub, // inverse-modulate ≈ subtractive darkening
            _ => BaseMaterial3D.BlendModeEnum.Mix,
        };

        var key = new InfoMeshKey(
            sprite is null ? 0UL : sprite.GetInstanceId(),
            (int)blend, isSpark,
            isSpark ? (int)MathF.Round(MathF.Max(1f, sparkAspect)) : 0,   // spark quad length (mesh size)
            growRatio is { } g0 ? (int)MathF.Round(g0 * 16f) : -1,        // growth-shader `grow` uniform (-1 = none)
            ColorKey5(color), cellCount);
        if (_infoMeshCache.TryGetValue(key, out Mesh? cached))
            return cached;

        var quad = new QuadMesh
        {
            Size = isSpark ? new Vector2(0.5f, MathF.Max(1f, sparkAspect)) : new Vector2(1f, 1f),
        };

        // T6: a growing billboard particle (sizeincrease != 0, not a spark) uses the custom growth shader so it
        // expands/shrinks over its life. Only when an atlas sprite is present (the real game) — the no-atlas
        // fallback keeps the flat StandardMaterial3D quad. Everything else uses the proven StandardMaterial3D.
        if (growRatio is { } gr && !isSpark && sprite is not null)
        {
            var grow = new ShaderMaterial { Shader = GrowthShader(blend) };
            grow.SetShaderParameter("grow", gr);
            grow.SetShaderParameter("albedo_tex", sprite);
            quad.Material = grow;
        }
        else
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = blend,
                // Sparks are oriented by the node's TransformAlign (Y→velocity); leave billboarding OFF so the mesh
                // uses that velocity-aligned transform (which carries the per-particle scale). Everything else uses
                // particle billboarding.
                BillboardMode = isSpark ? BaseMaterial3D.BillboardModeEnum.Disabled : BaseMaterial3D.BillboardModeEnum.Particles,
                // CRITICAL: particle billboarding DISCARDS the per-particle scale (scale_min/max from the process
                // material) unless keep-scale is on (godotengine/godot#74897). Without this the big fire/smoke
                // puffs (scale 66-128) collapsed to their 1×1 base quad — a near-invisible dot — which is why only
                // the sparks (a 0.15×2 base quad that reads even at scale 1) showed. Keep the scale so they render.
                BillboardKeepScale = true,
                VertexColorUseAsAlbedo = true,
                AlbedoColor = color,
                DisableReceiveShadows = true,
            };
            // Sprite-sheet particle animation: each particle starts on a random frame (AnimOffset) so the burst
            // shows a mix of smoke wisps / fire blobs / spark dots rather than identical copies.
            if (cellCount > 1)
            {
                mat.ParticlesAnimHFrames = cellCount;
                mat.ParticlesAnimVFrames = 1;
                mat.ParticlesAnimLoop = false;
            }
            ApplySprite(mat, sprite);
            quad.Material = mat;
        }
        _infoMeshCache[key] = quad;
        return quad;
    }

    /// <summary>
    /// Texture a particle billboard with an atlas sprite. The sprite is the alpha SHAPE (smoke wisp, fire
    /// blob, spark dot, blood drop); the per-particle vertex color (from the process material's color ramp)
    /// tints it (StandardMaterial3D multiplies albedo_texture * albedo_color * vertex_color). The sprite's own
    /// alpha channel hides the transparent atlas margins, so no scissor is needed (and for additive blends the
    /// near-black borders add nothing anyway).
    /// </summary>
    private static void ApplySprite(StandardMaterial3D mat, Texture2D? sprite)
    {
        if (sprite is null)
            return;
        mat.AlbedoTexture = sprite;
        mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
    }

    /// <summary>Spawn the OmniLight3D an effectinfo block requests (lightradius/lightcolor/lightradiusfade).</summary>
    private void SpawnInfoLight(Node3D parent, EffectInfoEmitter info, NVec3 atQuake, Color? colorOverride)
    {
        // DP color scales lightcolor by the tint; we use the parsed lightcolor (or the override hue) directly.
        Color lcol = colorOverride is { } c
            ? c
            : new Color(info.LightColor.X, info.LightColor.Y, info.LightColor.Z);
        // lightcolor components routinely exceed 1 (e.g. "8 4 1"); normalise into a unit hue + energy.
        float maxc = MathF.Max(1f, MathF.Max(lcol.R, MathF.Max(lcol.G, lcol.B)));
        var light = new OmniLight3D
        {
            Name = "fx_light",
            Position = Coords.ToGodot(atQuake) - parent.Position,
            OmniRange = Math.Clamp(info.LightRadius, 1f, 2000f),
            LightColor = new Color(lcol.R / maxc, lcol.G / maxc, lcol.B / maxc),
            LightEnergy = MathF.Min(8f, maxc),
        };
        parent.AddChild(light);

        // Fade the flash: lightradiusfade is radius-units/sec, so the flash lasts ~radius/fade seconds.
        float dur = info.LightRadiusFade > 0f
            ? Math.Clamp(info.LightRadius / info.LightRadiusFade, 0.05f, 1.5f)
            : 0.15f;
        SceneTree? tree = IsInsideTree() ? GetTree() : parent.GetTree();
        if (tree is not null)
        {
            // Create the tween from the SceneTree, not the light: the light's parent isn't AddChild'd until after
            // this block loop, so light.CreateTween() would fail ("not inside the SceneTree") and return null,
            // NRE-ing every lightradius>0 effect (all explosions). The callback below guards the light's validity.
            var tween = tree.CreateTween();
            tween.TweenProperty(light, "light_energy", 0f, dur).From(light.LightEnergy);
            tween.TweenCallback(Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(light)) light.QueueFree();
            }));
        }
    }

    /// <summary>The parent node's linger time: the longest particle life across its blocks plus a fade margin.</summary>
    private static float InfoParentLinger(IReadOnlyList<EffectInfoEmitter> blocks)
    {
        float max = 0.5f;
        foreach (EffectInfoEmitter b in blocks)
            if (b.Defined && b.Type != EiType.Decal)
                max = MathF.Max(max, b.Lifetime());
        return max + 0.6f;
    }

    /// <summary>Magnitude of a Quake-space vector mapped to Godot axes (componentwise abs; for radii/extents).</summary>
    private static Vector3 AbsToGodot(NVec3 q)
    {
        Vector3 g = Coords.ToGodot(q);
        return new Vector3(MathF.Abs(g.X), MathF.Abs(g.Y), MathF.Abs(g.Z));
    }

    // =================================================================================================
    //  Lifetime bookkeeping
    // =================================================================================================

    private void Track(Node3D node)
    {
        _live.Enqueue(node);
        while (_live.Count > MaxLiveEffects && _live.TryDequeue(out Node3D? old))
        {
            if (GodotObject.IsInstanceValid(old))
                old.QueueFree();
        }
    }

    /// <summary>Queue the node for removal after <paramref name="seconds"/> (one-shot particles are done by then).</summary>
    private void ScheduleFree(Node node, float seconds)
    {
        // The node has already been AddChild'd to this system; if the system is in the tree the child is too.
        SceneTree? tree = IsInsideTree() ? GetTree() : node.GetTree();
        if (tree is null)
        {
            // Not in the tree yet (effect spawned before the system was added) — free on a fixed safety net.
            node.QueueFree();
            return;
        }
        SceneTreeTimer timer = tree.CreateTimer(seconds);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
        };
    }
}
