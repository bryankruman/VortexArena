using Godot;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Loaders;
using XonoticGodot.Game.Client;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game;

/// <summary>
/// The walkable demo scene. Boots the gameplay stack on an engine <see cref="CollisionWorld"/>, lights
/// the scene, and drops a <see cref="PlayerController"/> that drives the ported movement sim.
///
/// With no <see cref="MapPath"/> set it builds a single large floor brush (so the player still has
/// something to stand on); with a <c>.bsp</c> path it loads the map's collision + render geometry +
/// spawn points via <see cref="MapLoader"/>.
///
/// Boot order matters: <see cref="GameInit.Boot"/> installs the engine-services facade (entity table,
/// trace service, clock) and the movement system BEFORE the <see cref="PlayerController"/> is added —
/// the controller spawns its entity through that facade in its own <c>_Ready</c>.
/// </summary>
public partial class GameDemo : Node3D
{
    /// <summary>
    /// Optional path to an IBSP <c>.bsp</c> map. Resolved first as a VFS virtual path inside the mounted
    /// <see cref="DataPath"/> (e.g. <c>"maps/stormkeep.bsp"</c> or just <c>"stormkeep"</c>), then as a raw
    /// filesystem path (back-compat). When empty the demo uses a flat test floor.
    /// </summary>
    [Export] public string MapPath { get; set; } = "";

    /// <summary>
    /// The Xonotic game data directory to mount as the asset VFS root (its <c>.pk3</c>/<c>.pk3dir</c> packs
    /// plus loose files). The whole asset pipeline — maps, models, sounds, sprites, materials, fonts — reads
    /// from here. Defaults to the in-tree <c>assets/data</c> directory (populated by
    /// <c>download-assets.sh</c>); a <c>res://</c>/<c>user://</c> or absolute OS path
    /// also works (see <see cref="ResolveDataPath"/>). When the path doesn't exist the demo still runs on the
    /// test floor (assets just won't resolve).
    /// </summary>
    [Export] public string DataPath { get; set; } = "res://assets/data";

    /// <summary>
    /// Optional model to spawn as a smoke-test of the model pipeline (dispatched by magic: IQM/DPM/MD3).
    /// A VFS vpath, e.g. <c>"models/ctf/flag_red.md3"</c>. Empty disables the sample spawn.
    /// </summary>
    [Export] public string SampleModelPath { get; set; } = "";

    /// <summary>
    /// The gametype this match runs, as its short code (QC <c>Gametype.mdl</c> / <c>GameType.NetName</c> —
    /// e.g. <c>"dm"</c>, <c>"ctf"</c>, <c>"rc"</c>). Drives the per-gametype map-entity filter
    /// (<see cref="XonoticGodot.Engine.Collision.MapEntityFilter"/>): brush entities tagged for a different gametype
    /// — a Race-only barrier (<c>func_wall "gametypefilter" "+rc"</c>), a non-Race barrier (<c>"-rc"</c>), … —
    /// are dropped from BOTH render and collision, so changing this changes which conditional walls appear.
    /// Defaults to Deathmatch, the universal fallback gametype every map supports.
    /// </summary>
    [Export] public string Gametype { get; set; } = "dm";

    /// <summary>
    /// An already-mounted asset VFS to reuse instead of mounting <see cref="DataPath"/> again. The menu shell
    /// mounts the gamedir once at boot (<see cref="Menu.MenuState"/>) and hands it to each match, so the mount
    /// isn't paid twice and menu + game read the same packs. When set, the demo does NOT dispose it on teardown
    /// (the shell owns its lifetime). Null → the demo mounts its own from <see cref="DataPath"/> (standalone use).
    /// </summary>
    public VirtualFileSystem? SharedVfs { get; set; }

    /// <summary>
    /// The process-wide cvar store to run on instead of a private one. The menu loads the stock config tree +
    /// the user's saved preferences into this store before the match, so the match inherits authentic (possibly
    /// user-overridden) values and a setting changed in the in-game menu takes effect at once. When set, the demo
    /// skips its own config load (the store is already populated). Null → a private store + a fresh cfg load.
    /// </summary>
    public XonoticGodot.Engine.Simulation.CvarService? SharedCvars { get; set; }

    private CollisionWorld _world = null!;
    private PlayerController _player = null!;

    // --- asset pipeline (VFS + unified loader) ---
    private VirtualFileSystem? _vfs;
    private AssetLoader? _assets;

    // --- client-side rendering layer ---
    private ClientWorld _client = null!;
    private ViewModel _viewModel = null!;

    // --- on-screen HUD + notification router ---
    private Hud.Hud _hud = null!;
    private Hud.HudNotifications _notifications = null!;

    // --- full-screen view post-effects (damage red-flash + liquid tint), QC view.qc HUD_Damage/HUD_Contents ---
    private Client.ViewEffects _viewEffects = null!;

    /// <summary>
    /// The weapons cycled through by the demo switcher (1..4 keys): muzzle effect, demo projectile class, and
    /// the first-person view-model MD3 (legacy <c>v_*</c> names — laser=blaster, nex=vortex, rl=devastator,
    /// gl=mortar). The model loads through the asset VFS; a miss falls back to the placeholder bar.
    /// </summary>
    private static readonly (string weapon, string muzzle, string projectile, string model)[] DemoWeapons =
    {
        ("blaster",   "BLASTER_MUZZLEFLASH",   "blaster_bolt", "models/weapons/v_laser.md3"),
        ("vortex",    "VORTEX_MUZZLEFLASH",    "vortex_beam",  "models/weapons/v_nex.md3"),
        ("rocket",    "ROCKET_MUZZLEFLASH",    "rocket",       "models/weapons/v_rl.md3"),
        ("mortar",    "GRENADE_MUZZLEFLASH",   "grenade",      "models/weapons/v_gl.md3"),
    };
    private int _demoWeapon;

    // Monotonic source of unique negative pseudo-indices for client-only demo projectiles (never collides
    // with the engine entity table, which assigns non-negative slots).
    private int _demoProjSeq;

    public override void _Ready()
    {
        // --- mount the asset VFS (gamedir + packs) and build the unified loader ---
        MountAssets();

        BspData? bsp = TryLoadMap();

        // The shared material/texture resolver the map + model builders use. Always non-null once the VFS
        // mounted; null only if mounting failed entirely (then we run on the bare test floor).
        AssetSystem? assetSystem = _assets?.Assets;

        // --- per-gametype map-entity filter (QC SV_OnEntityPreSpawnFunction): resolve which inline "*N" brush
        //     entities (gametype-conditional func_wall/clientwall/…) this gametype drops, so neither their
        //     collision brushes nor their render faces are built. Empty for a map with no filtered entities. ---
        var droppedSubmodels = bsp is not null ? ComputeDroppedSubmodels(bsp) : null;

        // --- collision world (Quake coords) + inline "*N" brush models for SOLID_BSP entities ---
        BspCollisionBuilder.Result? collision = null;
        if (bsp is not null)
        {
            collision = BspCollisionBuilder.Build(bsp, droppedSubmodels);
            _world = collision.World;
        }
        else
        {
            _world = BuildTestFloorWorld();
        }

        // --- boot the gameplay stack on this world (facade + registries + systems). Reuse the menu's shared
        //     cvar store when provided so menu-set preferences are live in the match (else a private store). ---
        var services = new EngineServices(_world, SharedCvars);
        GameInit.Boot(services);

        // --- register the map's inline brush models so func_door/plat/etc. clip with their real, moving
        //     brushes (and resolve correct bounds via setmodel) instead of falling back to an AABB. ---
        if (collision is not null)
            BspCollisionBuilder.RegisterSubmodels(collision.Submodels, services.ModelsImpl);

        // --- attach each inline model's render surfaces so the getsurface* builtins work (warpzone auto-plane
        //     from a brush, decal/surface queries). No-op when the BSP has no models lump. ---
        if (bsp is not null)
            XonoticGodot.Engine.Collision.BspSurfaceBuilder.BuildAndAttach(bsp, services.ModelsImpl);

        // --- wire the map's compiled PVS so checkpvs() culls correctly (no-op on an unvised map). ---
        if (bsp is not null)
            services.Pvs = new XonoticGodot.Formats.Bsp.BspPvs(bsp);

        // --- load the REAL Xonotic cvar tree from the mounted assets (balance/physics/gametypes/...) so the
        //     many live GetFloat() reads (movement physics, regen, gametype limits, mutator tuning, ...) get
        //     authentic values instead of hardcoded baselines. No-op when assets didn't mount. ---
        LoadGameConfig();

        // --- spawn the map's gameplay entities (QC the BSP entity lump → spawnfunc_CLASSNAME): jump-pads,
        //     teleporters, hurt/gravity volumes, doors, etc. Runs AFTER RegisterSubmodels (so a trigger's
        //     inline "*N" brush model resolves its real bbox) and AFTER the config load (so sv_gravity is set).
        //     Without this the walkable demo had collision + render geometry but NO trigger volumes, so e.g.
        //     a jump-pad never launched the player. ---
        if (bsp is not null)
            SpawnGameplayEntities(bsp);

        // --- lighting + environment ---
        AddLighting();

        // --- render geometry (skip faces of gametype-filtered "*N" brush entities) ---
        if (bsp is not null && assetSystem is not null)
        {
            // Thread the map name so external lightmaps resolve. Xonotic ships ALL of its lighting as
            // external lm_NNNN.jpg pages (BSP lump 14 is empty for every stock map), and LoadLightmap only
            // probes maps/<name>/lm_NNNN when it has a name — an empty name means no baked lighting at all.
            AddChild(MapLoader.BuildMap(bsp, assetSystem, MapPath, droppedSubmodels));
        }
        else
        {
            AddChild(BuildTestFloorMesh());
        }

        // --- player at a spawn point (Quake coords) ---
        (NVec3 spawnOrigin, float spawnYaw) = ChooseSpawn(bsp);
        _player = new PlayerController { Name = "Player" };
        // Place the node so PlayerController._Ready seeds the entity origin from it.
        _player.Position = Coords.ToGodot(spawnOrigin);
        AddChild(_player);
        // Re-apply precisely (also sets the view yaw) now that the entity exists.
        _player.Teleport(spawnOrigin, spawnYaw);
        // Fire the per-tick touch-triggers pass after the player moves (QC SV_TouchTriggers), so walking onto a
        // jump-pad / teleporter / trigger_hurt actually fires it — those SOLID_TRIGGER volumes are non-solid to
        // the movement sweep. Uses the ambient engine table the spawnfuncs populated.
        _player.PostMoveTouch = XonoticGodot.Engine.Simulation.TriggerTouch.RunAmbient;

        // --- client rendering layer (effects / projectiles / animation / view-model) ---
        SetupClient();

        // --- map background music (cdtrack from mapinfo / worldspawn) ---
        SetupMusic(bsp);

        // --- on-screen HUD + the server→client notification channel (centerprint / kill feed / announcer) ---
        SetupHud();

        // --- full-screen view post-effects: damage red-flash + underwater tint (QC view.qc) ---
        SetupView();

        // --- prove the model pipeline: drop a sample model at the spawn, if one is configured ---
        SpawnSampleModel(spawnOrigin);

        // --- dev smoke: exercise the skeletal player path on a real player IQM (--skeleton-smoke) ---
        if (System.Array.IndexOf(OS.GetCmdlineArgs(), "--skeleton-smoke") >= 0)
            RunSkeletonSmoke();

        // --- dev visual: `--fx-demo [effectname]` repeatedly bursts an explosion/impact on the floor in front
        //     of the spawn so a --screenshot captures the real particlefont sprites + scorch decal (parity check).
        {
            string[] cl = OS.GetCmdlineArgs();
            int fi = System.Array.IndexOf(cl, "--fx-demo");
            if (fi >= 0)
                _fxDemoEffect = (fi + 1 < cl.Length && !cl[fi + 1].StartsWith("--")) ? cl[fi + 1] : "rocket_explode";
            // `--proj-demo` repeatedly fires the equipped demo weapon's projectile straight ahead so a windowed
            // --screenshot captures a flying rocket/grenade with its model + trail (orientation/trail parity check).
            _projDemo = System.Array.IndexOf(cl, "--proj-demo") >= 0;
        }

        GD.Print(bsp is not null
            ? $"[GameDemo] map '{MapPath}' loaded ({Gametype}): {_world.Brushes.Count} collision brushes, " +
              $"{droppedSubmodels?.Count ?? 0} gametype-filtered brush entities dropped."
            : "[GameDemo] no map set — using flat test floor.");
    }

    /// <summary>
    /// Dev smoke (<c>--skeleton-smoke</c>): load a real player IQM, build a <see cref="PlayerModel"/>, and pose
    /// it idle vs running-while-looking-down — proving the whole skeletal path (IQM → PlayerSkeleton CPU split +
    /// aim → conjugation → <see cref="Skeleton3D"/>) runs on real data and actually moves the bones. Headless-safe.
    /// </summary>
    private void RunSkeletonSmoke()
    {
        if (_assets is null) { GD.PrintErr("[skeleton-smoke] no assets mounted"); return; }
        const string vpath = "models/player/erebus.iqm";
        AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(vpath, 0);
        if (parts is null) { GD.PrintErr($"[skeleton-smoke] could not load {vpath}"); return; }

        var pm = new PlayerModel { Name = "SkeletonSmoke" };
        pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
        Skeleton3D? skel = XonoticGodot.Game.Loaders.Models.IqmBuilder.FindSkeleton(parts.Root);
        GD.Print($"[skeleton-smoke] {vpath}: active={pm.Active}, bones={skel?.GetBoneCount() ?? 0}, " +
                 $"upperbody='{parts.Info?.BoneUpperBody ?? "-"}', aimbones={parts.Info?.AimBones.Count ?? 0}, fixbone={parts.Info?.FixBone}");
        if (!pm.Active || skel is null) { pm.ReleaseSkeleton(); pm.QueueFree(); return; }

        var idle = new Entity { Velocity = NVec3.Zero, Angles = NVec3.Zero, Flags = EntFlags.OnGround, Health = 100f, MaxHealth = 100f };
        var running = new Entity { Velocity = new NVec3(320f, 0f, 0f), Angles = new NVec3(45f, 0f, 0f), Flags = EntFlags.OnGround, Health = 100f, MaxHealth = 100f };

        pm.Pose(idle, 0.1f);
        (Vector3 pos, Quaternion rot)[] a = SnapshotPoses(skel);
        for (int i = 0; i < 6; i++) pm.Pose(running, 0.1f); // advance the run cycle + hold a 45° down aim
        (Vector3 pos, Quaternion rot)[] b = SnapshotPoses(skel);

        int moved = 0; float maxDelta = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float d = (a[i].pos - b[i].pos).Length() + a[i].rot.Normalized().AngleTo(b[i].rot.Normalized());
            if (d > 1e-3f) moved++;
            if (d > maxDelta) maxDelta = d;
        }
        GD.Print($"[skeleton-smoke] idle->run+aim: {moved}/{a.Length} bones changed (max delta {maxDelta:F3}) — " +
                 (moved > 0 ? "OK: skeletal posing drives the Skeleton3D" : "FAIL: no bone moved"));

        pm.ReleaseSkeleton();
        pm.QueueFree();
    }

    private static (Vector3 pos, Quaternion rot)[] SnapshotPoses(Skeleton3D skel)
    {
        int n = skel.GetBoneCount();
        var t = new (Vector3, Quaternion)[n];
        for (int i = 0; i < n; i++)
            t[i] = (skel.GetBonePosePosition(i), skel.GetBonePoseRotation(i));
        return t;
    }

    /// <summary>
    /// Resolve which inline <c>"*N"</c> brush entities the active <see cref="Gametype"/> filters out (QC
    /// <c>SV_OnEntityPreSpawnFunction</c>), so their collision + render geometry is skipped. Builds the gametype
    /// context the filter needs — teamplay + have-team-spawns are derived from the active gametype registry and
    /// the map's spawn points (QC <c>teamplay</c> / <c>have_team_spawns</c>). Logs each dropped class for
    /// diagnostics. Returns null when nothing is filtered (a map with no <c>gametypefilter</c> entities).
    /// </summary>
    private System.Collections.Generic.IReadOnlySet<int>? ComputeDroppedSubmodels(BspData bsp)
    {
        string shortName = string.IsNullOrWhiteSpace(Gametype) ? "dm" : Gametype.Trim();

        // The gametype-conditional inline-brush filter (QC SV_OnEntityPreSpawnFunction) is single-sourced in
        // GameMapView so the demo's render + collision AND the networked listen server all drop the SAME "*N"
        // brush entities for a given gametype.
        System.Collections.Generic.IReadOnlySet<int>? dropped = GameMapView.ComputeDroppedSubmodels(bsp, Gametype);
        if (dropped is null)
            return null;

        // Diagnostic: list the dropped brush-entity classes + filter keys so the headless log proves the filter.
        foreach (var ent in bsp.Entities)
        {
            if (!ent.TryGetValue("model", out string? model) || model is null || !model.StartsWith("*"))
                continue;
            if (!int.TryParse(model.AsSpan(1), out int n) || !dropped.Contains(n))
                continue;
            ent.TryGetValue("classname", out string? cls);
            ent.TryGetValue("gametypefilter", out string? gf);
            GD.Print($"[GameDemo]   filtered {model} {cls ?? "?"} (gametypefilter \"{gf}\") — not in '{shortName}'.");
        }
        return dropped;
    }

    /// <summary>
    /// Build the asset <see cref="VirtualFileSystem"/> by mounting <see cref="DataPath"/> as a gamedir
    /// (every <c>.pk3</c>/<c>.pk3dir</c> inside it plus loose files), then construct the unified
    /// <see cref="AssetLoader"/> (which also builds the shared <see cref="AssetSystem"/> material resolver and
    /// the font cache). Tolerant: a missing/empty data dir just leaves the loader without content, and the
    /// demo falls back to the test floor.
    /// </summary>
    private void MountAssets()
    {
        // Reuse the menu shell's already-mounted VFS when one was injected (no second mount; shared packs).
        if (SharedVfs is not null)
        {
            _vfs = SharedVfs;
            _assets = new AssetLoader(_vfs);
            GD.Print($"[GameDemo] reusing shared VFS ({_vfs.MountedPaths.Count} search paths, " +
                     $"{_assets.Assets.ShaderCount} shaders).");
            return;
        }

        _vfs = new VirtualFileSystem();
        string dataDir = ResolveDataPath(DataPath);
        bool mounted = false;
        try
        {
            mounted = _vfs.MountGameDir(dataDir);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[GameDemo] failed to mount data dir '{dataDir}': {ex.Message}");
        }

        if (!mounted)
        {
            GD.PrintErr($"[GameDemo] data dir '{dataDir}' not found — running without mounted assets.");
            return;
        }

        _assets = new AssetLoader(_vfs);
        GD.Print($"[GameDemo] mounted '{dataDir}' ({_vfs.MountedPaths.Count} search paths, " +
                 $"{_assets.Assets.ShaderCount} shaders).");
    }

    /// <summary>
    /// Resolve the configured <see cref="DataPath"/> to an absolute OS directory. A <c>res://</c> path is
    /// rooted at the project directory (so <c>res://assets/data</c> finds the in-tree content
    /// repo regardless of where the checkout lives), <c>user://</c> via Godot, and an absolute path is used
    /// verbatim. Any <c>..</c> segments are collapsed. Falls back to the raw string if globalization fails.
    /// </summary>
    public static string ResolveDataPath(string configured)
    {
        string p = string.IsNullOrWhiteSpace(configured) ? "res://assets/data" : configured.Trim();
        try
        {
            if (p.StartsWith("res://", System.StringComparison.OrdinalIgnoreCase))
            {
                string projectDir = ProjectSettings.GlobalizePath("res://");
                p = System.IO.Path.Combine(projectDir, p["res://".Length..]);
            }
            else if (p.StartsWith("user://", System.StringComparison.OrdinalIgnoreCase))
            {
                p = ProjectSettings.GlobalizePath(p);
            }
            return System.IO.Path.GetFullPath(p);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[GameDemo] could not resolve DataPath '{configured}': {ex.Message}");
            return p;
        }
    }

    /// <summary>
    /// Load the stock Xonotic server config tree (<c>xonotic-server.cfg</c> → balance/physics/gametypes/…)
    /// out of the mounted VFS into the live cvar store, so gameplay reads authentic values. The interpreter
    /// resolves each <c>exec</c> path through the VFS (case-insensitive, gamedir search order). No-op when no
    /// assets mounted (the demo then runs on <see cref="MovementParameters"/> defaults).
    /// </summary>
    private void LoadGameConfig()
    {
        // The menu shell preloads the shared store with the full client+server config tree + user prefs, so when
        // it's injected we skip reloading (which would also clobber the user's menu-set overrides with stock).
        if (SharedCvars is not null)
        {
            XonoticGodot.Common.Gameplay.Weapons.ConfigureAll();
            return;
        }
        if (_vfs is null || Api.Services is null)
            return;
        var interp = XonoticGodot.Common.Config.ConfigLoader.LoadServerConfig(
            Api.Cvars, path => _vfs.Exists(path) ? _vfs.ReadText(path) : null);
        // Re-seed weapon balance from the now-loaded g_balance_* cvars (the registration pass used stock fallbacks).
        XonoticGodot.Common.Gameplay.Weapons.ConfigureAll();
        GD.Print($"[GameDemo] config: {interp.CvarsAssigned} cvars from {interp.FilesExecuted} cfg files " +
                 $"({interp.AliasesDefined} aliases, {interp.FilesMissing} missing).");
    }

    /// <summary>
    /// Spawn the configured <see cref="SampleModelPath"/> model through the unified loader (magic-dispatched
    /// IQM/DPM/MD3 with sidecars), parked just in front of the spawn point so the asset pipeline is visibly
    /// exercised end-to-end. No-op when no sample is set or the model fails to load.
    /// </summary>
    private void SpawnSampleModel(NVec3 spawnOriginQuake)
    {
        if (_assets is null || string.IsNullOrWhiteSpace(SampleModelPath))
            return;

        Node3D? model = _assets.LoadModel(SampleModelPath);
        if (model is null)
        {
            GD.PrintErr($"[GameDemo] sample model '{SampleModelPath}' did not load.");
            return;
        }

        // Place it ~96 units in front of the spawn (Quake +X is forward), on the floor.
        NVec3 place = spawnOriginQuake + new NVec3(96f, 0f, 0f);
        model.Position = Coords.ToGodot(place);
        AddChild(model);
        GD.Print($"[GameDemo] spawned sample model '{SampleModelPath}'.");
    }

    /// <summary>Release the VFS (it keeps the mounted <c>.pk3</c> zip streams open) when the scene tears down.</summary>
    public override void _ExitTree()
    {
        _notifications?.UninstallLocalSink();
        // Only dispose a VFS we mounted ourselves; a shared one is owned by the menu shell and outlives the match.
        if (!ReferenceEquals(_vfs, SharedVfs))
            _vfs?.Dispose();
        _vfs = null;
        _assets = null;
    }

    /// <summary>
    /// Stand up the <see cref="ClientWorld"/> and attach a first-person <see cref="ViewModel"/> to the
    /// player camera. This is the host-side wiring that turns the per-item <c>TODO(port,client)</c> markers
    /// (muzzle flashes, projectile visuals, named effects) into something visible: the ClientWorld installs
    /// an effect sink so any in-process <c>EffectEmitter.Emit</c> renders, and the demo fires a muzzle flash
    /// + spawns a visual projectile on attack so the systems are exercised end-to-end.
    /// </summary>
    private void SetupClient()
    {
        _client = new ClientWorld { Name = "ClientWorld" };
        // Resolve positional/loop sounds straight from the mounted content VFS (sound/*.ogg|wav) instead of
        // res://; the renderers keep the res:// convention as a fallback when no loader/sample is present.
        if (_assets is not null)
        {
            _client.AudioLoader = _assets.LoadSound;

            // Players render as skeletal IQM models with the CPU upper/lower-body split + view-pitch aim
            // (PlayerSkeleton). The resolver fires only for player models (models/player/*.iqm); everything
            // else falls through to the MD3 morph / placeholder path. Built per entity on first sight.
            _client.PlayerModelResolver = e =>
            {
                if (string.IsNullOrEmpty(e.Model) || e.Model.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) < 0)
                    return null;
                AssetLoader.SkeletalModelParts? parts = _assets.LoadSkeletalModel(e.Model, (int)e.Skin);
                if (parts is null)
                    return null;
                var pm = new PlayerModel { Name = $"player#{e.Index}" };
                pm.Setup(parts.Iqm, parts.Root, parts.Groups, parts.Info);
                if (!pm.Active) { pm.QueueFree(); return null; } // non-skeletal IQM -> let the normal path handle it
                return pm;
            };

            // World non-player entities (items/gibs/monsters) resolve their MD3 by name and render textured via
            // the (material-resolving) ModelLoader.BuildModel / ModelAnimator path.
            _client.Assets = _assets.Assets;
            _client.ModelResolver = e =>
                (string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*')) ? null : _assets.LoadMd3(e.Model);
            // Format-agnostic fallback (IQM/DPM/MD3) so non-MD3 world entities also render their real model.
            _client.EntityModelFactory = e =>
                (string.IsNullOrEmpty(e.Model) || e.Model.StartsWith('*')) ? null : _assets.LoadModel(e.Model);
        }
        AddChild(_client);

        // Projectiles draw their REAL model (rocket.md3 with its additive RocketThrust flame cone,
        // grenademodel.md3) via the same VFS loader the world/weapon models use — set after AddChild since
        // ClientWorld._Ready (which built _client.Projectiles) ran synchronously on it.
        if (_assets is not null)
            _client.Projectiles.ModelFactory = m => _assets.LoadModel(m);

        // Pre-warm the effect catalog + particlefont atlas at setup so the first shot doesn't hitch parsing
        // effectinfo.txt + decoding the atlas on its frame (mirrors NetGame.SetupRender; DP precaches at init).
        // _client.Assets was set above (wires the loaders) and _client.Effects is live after AddChild.
        _client.Effects.Warmup();

        // Hang the first-person weapon view-model off the player camera (so it inherits view orientation).
        // Feed it live view state (velocity / angles / onground) so the gun sways (follow/lean/bob).
        _viewModel = new ViewModel { Name = "ViewModel", Effects = _client.Effects };
        _viewModel.ViewStateProvider = _player.GetViewState;
        _player.Camera.AddChild(_viewModel);
        _client.ViewModel = _viewModel;
        // The demo starts on the blaster; `--weapon N` (1..4) picks a different starting weapon (dev/capture).
        int startWeapon = 0;
        string[] cmd = OS.GetCmdlineArgs();
        int wi = System.Array.IndexOf(cmd, "--weapon");
        if (wi >= 0 && wi + 1 < cmd.Length && int.TryParse(cmd[wi + 1], out int wn))
            startWeapon = wn - 1;
        EquipDemoWeapon(startWeapon);

        // Fire on the player's attack edge.
        _player.Attacked += OnPlayerAttacked;
        _player.WeaponSwitched += EquipDemoWeapon;
    }

    /// <summary>
    /// Set up the background music player. Reads the cdtrack from the map's .mapinfo file (or worldspawn
    /// music/noise field) and starts it looping on the Music audio bus.
    /// </summary>
    private void SetupMusic(BspData? bsp)
    {
        // Resolve the cdtrack from mapinfo.
        string cdTrack = "";
        if (_vfs is not null && !string.IsNullOrEmpty(MapPath))
        {
            // Normalize the map name (strip path + extension to get the bare name).
            string mapName = MapPath;
            int lastSlash = mapName.LastIndexOfAny(new[] { '/', '\\' });
            if (lastSlash >= 0) mapName = mapName.Substring(lastSlash + 1);
            if (mapName.EndsWith(".bsp", System.StringComparison.OrdinalIgnoreCase))
                mapName = mapName.Substring(0, mapName.Length - 4);

            string mapinfoPath = $"maps/{mapName}.mapinfo";
            if (_vfs.Exists(mapinfoPath))
            {
                try
                {
                    string text = _vfs.ReadText(mapinfoPath);
                    cdTrack = ParseMapinfoCdTrack(text);
                }
                catch { /* mapinfo read failed */ }
            }
        }

        // Fallback: worldspawn "music" or "noise" field (read from the raw BSP entity lump).
        if (string.IsNullOrEmpty(cdTrack) && bsp is not null)
        {
            foreach (var dict in bsp.Entities)
            {
                if (dict.TryGetValue("classname", out string? cls) && cls == "worldspawn")
                {
                    if (dict.TryGetValue("music", out string? wsMusic) && !string.IsNullOrEmpty(wsMusic))
                        cdTrack = wsMusic;
                    else if (dict.TryGetValue("noise", out string? wsNoise) && !string.IsNullOrEmpty(wsNoise))
                        cdTrack = wsNoise;
                    break;
                }
            }
        }

        string resolvedTrack = Client.MusicPlayer.ResolveMusicPath(cdTrack);

        float bgmVol = 0.7f;
        if (Api.Services is not null)
        {
            float cv = Api.Cvars.GetFloat("bgmvolume");
            if (cv > 0f) bgmVol = cv;
        }

        var music = new Client.MusicPlayer
        {
            Name = "MusicPlayer",
            CdTrack = resolvedTrack,
            AudioLoader = _assets is not null ? _assets.LoadSound : null,
            BgmVolume = bgmVol,
        };

        // On the GameDemo path, entities live in the ambient Api.Services table.
        if (Api.Services is not null)
            music.EntityList = ((XonoticGodot.Engine.Simulation.EngineServices)Api.Services).EntityTable.All;

        AddChild(music);

        if (!string.IsNullOrEmpty(resolvedTrack))
            GD.Print($"[GameDemo] map music: '{resolvedTrack}'");
    }

    /// <summary>Parse the cdtrack line from a .mapinfo file's text content.</summary>
    private static string ParseMapinfoCdTrack(string mapinfoText)
    {
        if (string.IsNullOrEmpty(mapinfoText))
            return "";
        foreach (string rawLine in mapinfoText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("cdtrack ", System.StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("cdtrack\t", System.StringComparison.OrdinalIgnoreCase))
            {
                string value = line.Substring(8).Trim();
                if (value.Length > 1 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                return value;
            }
        }
        return "";
    }

    /// <summary>
    /// Stand up the on-screen <see cref="Hud.Hud"/> and the <see cref="Hud.HudNotifications"/> router. The HUD
    /// reflects the local player's live state (health/armor/ammo/weapons); the router connects the notification
    /// channel end-to-end — for this single-process demo it installs a local <see cref="INotificationSink"/> so
    /// any server-side <c>Send_Notification</c> in this process renders as a centerprint / kill-feed line /
    /// announcer voice. A networked client instead feeds <c>HudNotifications.OnNotification</c> from
    /// <c>ClientNet.NotificationReceived</c>.
    /// </summary>
    private void SetupHud()
    {
        // Bridge the HUD's texture cache to the mounted game data so weapon icons / crosshairs / kill-notify
        // icons draw the REAL Xonotic art (resolved via the asset pipeline) instead of colored-box fallbacks.
        if (_assets is not null)
            Hud.TextureCache.VfsResolver = _assets.LoadTexture;

        _hud = new Hud.Hud { Name = "Hud" };
        AddChild(_hud);
        _hud.SetPlayer(_player.Player as XonoticGodot.Common.Gameplay.Player);

        _notifications = new Hud.HudNotifications(_hud);
        // Announcer voices play straight from the mounted content (sound/announcer/default/<snd>.ogg) via the
        // VFS loader; the res:// resolver stays as the fallback when no assets are mounted.
        if (_assets is not null)
            _notifications.AudioLoader = _assets.LoadSound;
        _notifications.AnnouncerResolver = ResolveSoundResource;
        _notifications.InstallLocalSink();
    }

    /// <summary>
    /// Stand up the full-screen view post-effects overlay (<see cref="Client.ViewEffects"/>) — the C# successor
    /// to view.qc's <c>HUD_Damage</c> (damage red-flash) and <c>HUD_Contents</c> (underwater/lava/slime tint).
    /// It's a sibling <see cref="CanvasLayer"/> that composites a full-screen coloured quad under the HUD. We
    /// drive its damage flash from the player's pain edge (<see cref="PlayerController.Damaged"/>) and feed it
    /// the live health + eye contents each frame in <see cref="_Process"/>.
    /// </summary>
    private void SetupView()
    {
        _viewEffects = new Client.ViewEffects { Name = "ViewEffects" };
        AddChild(_viewEffects);

        // Pain edge → red flash (QC .dmg_take feeding HUD_Damage). The controller detects a health drop on the
        // player entity it owns and raises Damaged with the amount lost.
        _player.Damaged += amount => _viewEffects.ReportDamage(amount);
    }

    /// <summary>
    /// Per-frame drive of the screen post-effects (view.qc runs HUD_Damage/HUD_Contents every CSQC_UpdateView).
    /// Feeds the overlay the local player's current health + the SUPERCONTENTS the controller sampled at the eye
    /// this tick, so the red-flash decays and the liquid tint fades in/out as the eye enters/leaves water.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_viewEffects is null || _player?.Player is null)
            return;
        // observing:false — the demo player is always a spawned, playing actor (never an observer / pre-spawn), so
        // the death fade is driven by the internal health<1 ramp when it dies, not suppressed.
        _viewEffects.UpdateEffects((float)delta, _player.Player.Health, _player.EyeContents, observing: false);

        // Advance the announcer queue (play the next queued voice if the current one finished).
        _notifications?.ProcessAnnouncerQueue();

        if (_fxDemoEffect is not null)
            DriveFxDemo((float)delta);

        if (_projDemo)
        {
            _projDemoTimer -= (float)delta;
            if (_projDemoTimer <= 0f)
            {
                _projDemoTimer = 0.25f; // a fresh projectile 4×/sec; each flies ~2s, so several are airborne at varied range
                OnPlayerAttacked();
            }
        }
    }

    // --- dev visual: `--proj-demo` auto-fires the equipped projectile for --screenshot capture ---
    private bool _projDemo;
    private float _projDemoTimer;

    // --- dev visual fx preview (--fx-demo) ---------------------------------------------------------------
    private string? _fxDemoEffect;
    private float _fxDemoTimer;

    /// <summary>
    /// Repeatedly fire the configured effect on the floor a short way in front of the spawn so a windowed
    /// <c>--screenshot</c> always lands on a live burst, and so its <c>type decal</c> block accumulates a
    /// visible scorch on the surface. Dev-only; never active in a normal run (no <c>--fx-demo</c> flag).
    /// </summary>
    private void DriveFxDemo(float delta)
    {
        _fxDemoTimer -= delta;
        if (_fxDemoTimer > 0f)
            return;
        _fxDemoTimer = 0.12f; // re-burst cadence — frequent enough that a fresh burst is always on screen

        if (_player.Player is null)
            return;
        NVec3 eye = _player.Player.Origin + new NVec3(0f, 0f, _player.EyeHeight);
        NVec3 fwd = _player.AimForwardQuake();

        // Aim a point ~120u ahead, then drop to the floor under it so the burst rises into view and the scorch
        // lands on a real surface. Fall back to a forward-wall hit if there's no floor (open drop).
        NVec3 ahead = eye + fwd * 120f;
        var down = XonoticGodot.Common.Services.Api.Trace.Trace(
            ahead, NVec3.Zero, NVec3.Zero, ahead - new NVec3(0f, 0f, 400f),
            XonoticGodot.Common.Framework.MoveFilter.WorldOnly, null);
        NVec3 floor = down.Fraction < 1f ? down.EndPos + new NVec3(0f, 0f, 6f) : ahead;

        // Floor burst (scorch decal lands here) + an eye-level burst centered in view (fireball always visible
        // regardless of spawn orientation) + a bullet-impact burst (impact sprite/decal path).
        _client.Effects.Spawn(_fxDemoEffect!, floor);
        _client.Effects.Spawn(_fxDemoEffect!, eye + fwd * 110f + new NVec3(0f, 0f, 45f));
        _client.Effects.Spawn("machinegun_impact", floor + fwd * 30f, -fwd);
    }

    /// <summary>
    /// Resolve a bare announcer/sound name (QC <c>snd</c>) to a loadable Godot audio path. Probes the common
    /// Xonotic announcer layout under <c>res://sound/announcer/default/</c>; returns the bare-name path so the
    /// loader's existence check decides (a miss simply silences the cue while the audio import is pending).
    /// </summary>
    private static string? ResolveSoundResource(string soundName)
        => string.IsNullOrWhiteSpace(soundName) ? null : $"res://sound/announcer/default/{soundName}.ogg";

    private void EquipDemoWeapon(int index)
    {
        _demoWeapon = ((index % DemoWeapons.Length) + DemoWeapons.Length) % DemoWeapons.Length;
        (string weapon, string muzzle, string _, string model) = DemoWeapons[_demoWeapon];

        // Base-faithful first-person model selection (CL_WeaponEntity_SetModel): full-model DPM rigs (rl/gl/
        // crylink/…) render the h_ HAND RIG itself; invisible-hand IQM rigs render the v_ visual model attached
        // to the rig's "weapon" bone. A miss installs the placeholder bar. The h_ rig's own tag_shot bone is the
        // muzzle, resolved by ViewModel.SetWeaponModel (falls back to the skeleton bone rest for the rig path).
        ViewModelEquip eq = BuildViewModelEquip(_assets, model);
        _viewModel.SetWeaponModel(eq.Model, muzzle, "tag_shot", eq.Attach);

        GD.Print($"[GameDemo] weapon -> {weapon} (muzzle {muzzle}, " +
                 $"{(eq.Model is null ? "placeholder" : eq.IsHandRig ? $"h_ rig {model}" : $"v_ model {model}")})");
    }

    /// <summary>The model + attach offset to hand the <see cref="ViewModel"/> for one weapon, and whether the
    /// rendered node is the h_ HAND RIG itself (full-model) rather than the v_ visual model (invisible-hand).</summary>
    internal readonly struct ViewModelEquip
    {
        /// <summary>The built node to render as the first-person weapon (either the v_ model or the h_ rig).</summary>
        public Node3D? Model { get; init; }
        /// <summary>The local attach transform applied to <see cref="Model"/> under the ViewBasis.</summary>
        public Transform3D Attach { get; init; }
        /// <summary>True when <see cref="Model"/> is the h_ rig itself (full-model DPM weapons); false = v_ model.</summary>
        public bool IsHandRig { get; init; }

        /// <summary>The placeholder/missing equip (no model, identity attach, v_ path).</summary>
        public static ViewModelEquip None => new() { Model = null, Attach = Transform3D.Identity, IsHandRig = false };
    }

    /// <summary>
    /// Build the first-person weapon node for a <c>v_*</c> model path, faithful to Base
    /// <c>CL_WeaponEntity_SetModel</c> (all.qc:367-424). The branch is keyed off the sibling <c>h_*</c> HAND
    /// RIG's bones:
    /// <list type="number">
    ///   <item><b>INVISIBLE-HAND</b> (the h_ rig exposes a <c>weapon</c>/<c>tag_weapon</c> bone — the IQM rigs
    ///   h_arc/h_nex/h_shotgun/…): render the <c>v_</c> VISUAL model attached to that bone's rest (position-only,
    ///   the legacy path), exactly as Base attaches the v_ <c>weaponchild</c> to the <c>weapon</c> bone.</item>
    ///   <item><b>FULL-MODEL</b> (the h_ rig has NO such bone — the DPM rigs h_rl/h_crylink/h_electro/h_gl/
    ///   h_hagar/ok_*): render the <b>h_ RIG ITSELF</b> (its own gun+hand mesh) at <c>attach = identity</c>, and
    ///   IGNORE the v_ model — Base leaves the weaponchild NULL and the rig IS the viewmodel. The rig is authored
    ///   in the same Quake→Godot model space as the v_ models, so its drawn gun and its own <c>tag_shot</c> shot
    ///   origin coincide (no rotation strip, no <c>tag_handle</c> offset — that hack is gone for these).</item>
    /// </list>
    /// When the h_ rig is missing/unparsable it degrades to the invisible-hand v_ path (legacy behaviour).
    /// </summary>
    internal static ViewModelEquip BuildViewModelEquip(AssetLoader? assets, string vModelPath)
    {
        if (assets is null || string.IsNullOrEmpty(vModelPath))
            return ViewModelEquip.None;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm by name; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");

        // Classify the rig WITHOUT building a Godot node (Godot-free parsed-data probe). Missing/unparsable rig
        // or a path with no h_ sibling -> treat as invisible-hand so we keep the legacy v_ render.
        bool? invisibleHand = hPath == vModelPath ? null : assets.WeaponRigIsInvisibleHand(hPath);

        // FULL-MODEL (DPM rigs, no weapon bone): the h_ rig itself is the viewmodel. attach = identity.
        if (invisibleHand == false)
        {
            Node3D? hand = assets.LoadModel(hPath);
            if (hand is not null)
                return new ViewModelEquip { Model = hand, Attach = Transform3D.Identity, IsHandRig = true };
            // Rig classified full-model but failed to build — fall through to the v_ path as a last resort.
        }

        // INVISIBLE-HAND (IQM rigs with a weapon bone) OR a missing/unbuildable rig: render the v_ model,
        // attached to the rig's weapon-attach bone (position-only — see WeaponAttachTransform).
        Node3D? built = assets.LoadModel(vModelPath);
        Transform3D attach = WeaponAttachTransform(assets, vModelPath);
        return new ViewModelEquip { Model = built, Attach = attach, IsHandRig = false };
    }

    /// <summary>
    /// The held-gun attach offset for a <c>v_*</c> weapon path: load the sibling <c>h_*</c> hand model and read
    /// its <c>weapon</c> / <c>tag_weapon</c> bone rest (in built Godot space). This mirrors Xonotic's
    /// <c>setattachment(weaponchild, this, "weapon")</c> — the hand model is the rig that positions the
    /// <c>v_</c> visual model in the lower-right hand. Used only on the INVISIBLE-HAND path now (full-model DPM
    /// weapons render the h_ rig itself, so they never reach here); the <c>tag_handle</c> socket of the DPM rigs
    /// is therefore no longer consulted. Returns identity (gun at the eye) if no hand model / bone resolves.
    /// </summary>
    internal static Transform3D WeaponAttachTransform(AssetLoader? assets, string vModelPath)
    {
        if (assets is null)
            return Transform3D.Identity;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");
        if (hPath == vModelPath || !assets.Vfs.Exists(hPath))
            return Transform3D.Identity;

        Node3D? hand = assets.LoadModel(hPath);
        if (hand is null)
            return Transform3D.Identity;

        // The attachment socket the v_ model rides on, in order of the QC's preference. Only invisible-hand
        // (IQM) rigs reach this helper now, and they all expose a `weapon` bone (identity rotation), so the
        // legacy `tag_handle` DPM fallback is removed — a full-model DPM rig renders the h_ rig itself and never
        // gets here, so attaching the (wrong) v_ model to its `tag_handle` socket (the "twisted crylink" hack)
        // is no longer reachable.
        Transform3D attach = Transform3D.Identity;
        foreach (string bone in new[] { "weapon", "tag_weapon" })
        {
            Transform3D rest = XonoticGodot.Game.Loaders.Models.IqmBuilder.GetBoneGlobalRest(hand, bone);
            if (rest != Transform3D.Identity)
            {
                attach = rest;
                break;
            }
        }
        hand.QueueFree();

        // POSITION the gun only — never re-orient it. The v_ model geometry is already authored pointing
        // "forward" (the same IQM the world pickup builds correctly via Coords.ToGodot), and ViewModel's
        // ViewBasis re-aims that built-in forward into the camera frame. The socket's bind ROTATION must not be
        // layered on top (the IQM `weapon` bones carry identity rotation, so this is a no-op for them); keep the
        // socket's translation (the held-hand offset) and any uniform scale, drop the rotation/shear.
        return new Transform3D(Basis.Identity.Scaled(attach.Basis.Scale), attach.Origin);
    }

    /// <summary>
    /// The local player pressed attack: pop the view-model muzzle flash and launch a demo projectile from
    /// the muzzle so the <see cref="ProjectileRenderer"/> and trail effects are visible. The projectile is a
    /// throwaway client-only <see cref="Entity"/> that flies straight and self-explodes after a short life —
    /// it stands in for what the networked entity stream will feed once the server projectile is wired.
    /// </summary>
    private void OnPlayerAttacked()
    {
        (string _, string muzzle, string projectile, string _) = DemoWeapons[_demoWeapon];

        // View-model flash (also emits the muzzle EFFECT_* at the weapon tag, in world space).
        _client.OnMuzzleFlash(muzzle);

        if (_player.Player is null)
            return;

        // Launch point: the player's eye, aimed along the view forward (Quake space).
        NVec3 eye = _player.Player.Origin + new NVec3(0f, 0f, _player.EyeHeight);
        NVec3 dir = _player.AimForwardQuake();
        LaunchDemoProjectile(projectile, eye + dir * 40f, dir * 1600f);
    }

    /// <summary>Spawn a transient client-only projectile entity, render it, and self-explode after a beat.</summary>
    private void LaunchDemoProjectile(string className, NVec3 origin, NVec3 velocity)
    {
        var proj = new Entity
        {
            ClassName = className,
            Origin = origin,
            OldOrigin = origin,
            Velocity = velocity,
            // Negative pseudo-index (monotonic) so it never collides with the engine entity table.
            Index = -(1 + _demoProjSeq++),
        };

        _client.OnEntityUpdate(proj);

        // Integrate the position client-side (no collision) and explode on a timer.
        var driver = new DemoProjectileDriver
        {
            Name = $"projdrv{proj.Index}",
            Projectile = proj,
            Client = _client,
            LifeSeconds = 2.0f,
            ImpactEffect = "EXPLOSION_SMALL",
            ImpactSound = "weapons/rocket_impact",
        };
        AddChild(driver);
    }

    // -------------------------------------------------------------------------------------------------
    //  Gameplay map entities (QC the BSP entity lump → spawnfunc_CLASSNAME)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spawn the map's gameplay entities from the BSP entity lump (the demo analogue of the server's
    /// <c>GameWorld.SpawnMapEntities</c>): for each entity dict, mint an engine edict, copy the fields the
    /// map-object spawnfuncs read (classname/origin/angles/target/targetname/model/spawnflags/…), then dispatch
    /// to the registered spawnfunc (<see cref="XonoticGodot.Common.Gameplay.SpawnFuncs"/> — registered by
    /// <see cref="GameInit.Boot"/>). A class with no spawnfunc is left as a plain findable edict (spawn points,
    /// markers), exactly like the server. Then runs the post-spawn link pass (double/quad doors).
    ///
    /// This is what gives the walkable demo its jump-pads / teleporters / hurt volumes. NOTE the demo does not
    /// step a full <see cref="XonoticGodot.Engine.Simulation.SimulationLoop"/>, so entity THINKs don't tick — movers
    /// (doors/plats) won't animate here — but trigger volumes whose effect is synchronous on touch (jump-pads,
    /// teleporters, trigger_hurt) work, because the player runs the touch-triggers pass each physics frame.
    /// </summary>
    private void SpawnGameplayEntities(BspData bsp)
    {
        if (Api.Services is null)
            return;

        int spawned = 0, pads = 0;
        foreach (var dict in bsp.Entities)
        {
            if (!dict.TryGetValue("classname", out string? cls) || string.IsNullOrEmpty(cls) || cls == "worldspawn")
                continue;

            Entity e = Api.Entities.Spawn();
            e.ClassName = cls;
            ApplyMapFields(e, dict);

            if (XonoticGodot.Common.Gameplay.SpawnFuncs.TrySpawn(cls, e))
            {
                spawned++;
                if (cls is "trigger_push" or "trigger_push_velocity")
                    pads++;
            }
            // else: no spawnfunc — keep the bare edict (findable by classname/targetname), like the server.
        }

        // QC INITPRIO_LINKDOORS: link double/quad doors now every door's size is known.
        XonoticGodot.Common.Gameplay.MapObjectsRegistry.RunPostSpawn();

        GD.Print($"[GameDemo] spawned {spawned} map entities ({pads} jump-pads) from the entity lump.");
    }

    /// <summary>
    /// Copy the recognized map-dict keys onto the edict (the demo twin of <c>GameWorld.ApplyDictFields</c>):
    /// origin/angles plus the common keys the ported map objects read, then setorigin to link the bounds. A
    /// trigger's inline <c>"*N"</c> brush model bbox is resolved by its spawnfunc (InitTrigger → setmodel).
    /// </summary>
    private static void ApplyMapFields(Entity e, System.Collections.Generic.IReadOnlyDictionary<string, string> f)
    {
        e.Origin = ParseVec(f, "origin");
        e.OldOrigin = e.Origin;
        e.Angles = ParseVec(f, "angles");

        if (f.TryGetValue("targetname", out var tn)) e.TargetName = tn;
        if (f.TryGetValue("target", out var tg)) e.Target = tg;
        if (f.TryGetValue("killtarget", out var kt)) e.KillTarget = kt;
        if (f.TryGetValue("message", out var msg)) e.Message = msg;
        if (f.TryGetValue("model", out var mdl)) e.Model = mdl;
        if (f.TryGetValue("noise", out var noise)) e.Noise = noise;
        if (f.TryGetValue("speed", out var sp) && TryFloat(sp, out float spf)) e.Speed = spf;
        if (f.TryGetValue("height", out var ht) && TryFloat(ht, out float htf)) e.Height = htf;
        if (f.TryGetValue("spawnflags", out var sf) && int.TryParse(sf, out int sfi)) e.SpawnFlags = sfi;
        if (f.TryGetValue("team", out var tm) && TryFloat(tm, out float tmf)) e.Team = tmf;

        Api.Entities.SetOrigin(e, e.Origin); // relink AbsMin/AbsMax to the final placement
    }

    private static NVec3 ParseVec(System.Collections.Generic.IReadOnlyDictionary<string, string> f, string key)
    {
        if (!f.TryGetValue(key, out string? s) || string.IsNullOrWhiteSpace(s))
            return NVec3.Zero;
        string[] p = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return NVec3.Zero;
        return new NVec3(
            TryFloat(p[0], out float x) ? x : 0f,
            TryFloat(p[1], out float y) ? y : 0f,
            TryFloat(p[2], out float z) ? z : 0f);
    }

    private static bool TryFloat(string s, out float v)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v);

    // -------------------------------------------------------------------------------------------------
    //  Map loading
    // -------------------------------------------------------------------------------------------------

    private BspData? TryLoadMap()
    {
        if (string.IsNullOrWhiteSpace(MapPath))
            return null;

        // 1) Preferred: resolve through the asset VFS. Accept a bare map name, a "maps/foo" path, or a
        //    path with/without the .bsp extension.
        if (_assets is not null)
        {
            foreach (string vpath in MapVPathCandidates(MapPath))
            {
                if (!_assets.Vfs.Exists(vpath))
                    continue;
                BspData? bsp = _assets.ReadBsp(vpath);
                if (bsp is not null)
                    return bsp;
            }
        }

        // 2) Fallback: a raw filesystem path (back-compat with the original demo behaviour).
        try
        {
            if (System.IO.File.Exists(MapPath))
            {
                byte[] bytes = System.IO.File.ReadAllBytes(MapPath);
                return BspReader.Read(bytes);
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[GameDemo] failed to load BSP '{MapPath}': {ex.Message}");
            return null;
        }

        GD.PrintErr($"[GameDemo] map '{MapPath}' not found in the VFS or on disk.");
        return null;
    }

    /// <summary>The VFS vpaths a configured <see cref="MapPath"/> is probed at (bare name → maps/, +.bsp).</summary>
    private static System.Collections.Generic.IEnumerable<string> MapVPathCandidates(string mapPath)
    {
        string p = mapPath.Replace('\\', '/').Trim();
        bool hasBsp = p.EndsWith(".bsp", System.StringComparison.OrdinalIgnoreCase);
        bool underMaps = p.StartsWith("maps/", System.StringComparison.OrdinalIgnoreCase);

        // verbatim, then +.bsp
        yield return p;
        if (!hasBsp) yield return p + ".bsp";

        // under maps/ if it wasn't already
        if (!underMaps)
        {
            yield return "maps/" + p;
            if (!hasBsp) yield return "maps/" + p + ".bsp";
        }
    }

    private (NVec3 origin, float yaw) ChooseSpawn(BspData? bsp)
    {
        if (bsp is not null)
        {
            foreach ((string _, NVec3 origin, float angle) in MapLoader.SpawnPoints(bsp))
            {
                // Lift slightly so the player settles onto the floor rather than starting embedded.
                return (origin + new NVec3(0f, 0f, 16f), angle);
            }
        }
        // Default: just above the test floor (origin.Z 40 -> hull bottom at 16, falls to rest at 24).
        return (new NVec3(0f, 0f, 40f), 0f);
    }

    // -------------------------------------------------------------------------------------------------
    //  Fallback test floor (no map)
    // -------------------------------------------------------------------------------------------------

    private static CollisionWorld BuildTestFloorWorld()
    {
        var world = new CollisionWorld();
        // A big, thick floor slab in Quake space: [-4096,-4096,-64] .. [4096,4096,0].
        var floor = Brush.FromBox(
            new NVec3(-4096f, -4096f, -64f),
            new NVec3(4096f, 4096f, 0f),
            SuperContents.Solid);
        world.AddBrush(floor);
        world.BuildGrid();
        return world;
    }

    private static Node3D BuildTestFloorMesh()
    {
        var root = new Node3D { Name = "TestFloor" };
        // The collision floor top sits at Quake Z=0 -> Godot Y=0. Lay a plane there.
        var plane = new PlaneMesh { Size = new Vector2(8192f, 8192f) };
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.45f, 0.5f) };
        var mi = new MeshInstance3D { Name = "FloorMesh", Mesh = plane, MaterialOverride = mat };
        root.AddChild(mi);
        return root;
    }

    // -------------------------------------------------------------------------------------------------
    //  Lighting
    // -------------------------------------------------------------------------------------------------

    private void AddLighting()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            // Angle the light down and to the side.
            RotationDegrees = new Vector3(-55f, -35f, 0f),
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,
        };
        var worldEnv = new WorldEnvironment { Name = "WorldEnvironment", Environment = env };
        AddChild(worldEnv);
    }
}
