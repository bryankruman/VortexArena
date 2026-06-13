using System;
using System.Collections.Generic;
using System.Net;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Net;
using XonoticGodot.Server;
using NVec3 = System.Numerics.Vector3;
using BitWriter = XonoticGodot.Net.BitWriter;
using BitReader = XonoticGodot.Net.BitReader;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The authoritative server's network driver — the glue that turns a headless <see cref="GameWorld"/> into a
/// networked match. It owns a <see cref="NetTransport.Server"/>, maps each connected ENet peer to a
/// <see cref="Player"/> on the world's <see cref="ClientManager"/>, feeds received <see cref="InputCommand"/>s
/// into the world's per-player movement, advances the simulation, and broadcasts a per-tick snapshot of the
/// relevant entity state back to every client. It also installs the <see cref="EffectEmitter"/> /
/// <see cref="NotificationSystem"/> NET sinks so server-emitted effects and notifications are serialized and
/// broadcast (resolving the "wire to netcode" TODOs in those systems).
///
/// Drive it by calling <see cref="Tick"/> once per host frame with the real elapsed time:
/// <list type="number">
///   <item>poll the transport (dispatches handshakes + queued input packets);</item>
///   <item>advance the <see cref="GameWorld"/> by the elapsed time (the world pulls each client's queued
///         input through <see cref="GameWorld.InputProvider"/>, runs authoritative movement, fires
///         effect/notification emissions which our sinks capture);</item>
///   <item>broadcast a snapshot to each client (owner-replicated full-precision state for prediction + the
///         quantized state of every other entity for interpolation), then flush the captured event bundles.</item>
/// </list>
///
/// Lag-compensation rewind (antilag.qc) is wired: each frame this records the position history of every player,
/// monster and nade (<see cref="BuildEntitySet"/> / <see cref="RecordAntilagEntities"/>), and at hitscan/projectile
/// fire it rewinds them all to the shooter's MEASURED view-time and restores them after the trace
/// (<see cref="BeginLagComp"/>/<see cref="EndLagComp"/>). Delta-compression of the snapshot is also in place
/// (<see cref="ServerSnapshotHistory"/>).
/// </summary>
public sealed class ServerNet : IDisposable
{
    private readonly NetTransport.Server _transport;
    private readonly GameWorld _world;
    private readonly string _serverName;

    // S5 (sv_threaded, default OFF): the cross-thread serialisation gate for the listen/host worker-thread path.
    // NULL by default (single-threaded) — when null Tick runs with NO lock, byte-for-byte as today. The host
    // installs a shared lock object here AND takes the SAME object around the main thread's prediction span in
    // NetGame._Process; the ServerThread worker's Tick then locks it, so the sim and the main-thread prediction
    // never touch the shared world concurrently. Set ONCE at StartListenServer when sv_threaded is on.
    private object? _simGate;

    /// <summary>S5: install (or clear) the cross-thread serialisation gate. Set by the host when sv_threaded is on,
    /// BEFORE the ServerThread starts; the SAME object is taken by the main thread around its prediction span.</summary>
    public object? SimGate { get => _simGate; set => _simGate = value; }

    // S5: main→server one-way command handoff. When threaded, console/menu sinks (chat / bot_add / bot_remove /
    // changelevel) fire on the MAIN thread but must run on the worker that owns the world — they're enqueued here
    // and drained at the top of Tick (under the gate). When NOT threaded (_simGate == null) Enqueue runs the
    // action inline immediately, so the single-threaded path keeps its exact synchronous behavior.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _inbound = new();

    /// <summary>S5: run <paramref name="action"/> on the sim thread. Threaded: enqueue for the worker to drain at the
    /// next tick top (under the gate). Single-threaded: invoke inline now (unchanged synchronous behavior).</summary>
    public void RunOnSimThread(Action action)
    {
        if (_simGate is null) action();
        else _inbound.Enqueue(action);
    }

    /// <summary>The transport's connection cap — the browser's slots fallback when g_maxplayers is unset.</summary>
    private int _maxClients = 32;

    // Reused writers (the networking spec's allocation discipline: one writer per send path, reset+refilled).
    private readonly BitWriter _snapshotWriter = new(2048);
    private readonly BitWriter _eventWriter = new(1024);
    private readonly BitWriter _reliableWriter = new(1024);
    private readonly BitWriter _scratchWriter = new(512);

    /// <summary>Reusable scratch for the per-peer personalized GametypeStatusBlock (T53) — serialized first so
    /// it can be hash-gated before deciding to copy it into the snapshot.</summary>
    private readonly BitWriter _modeStatusScratch = new(256);

    private readonly Dictionary<int, PeerState> _peers = new();      // by Godot peer id
    private readonly Dictionary<Player, PeerState> _byPlayer = new(ReferenceEqualityComparer.Instance);

    // --- snapshot delta-compression + movevar replication + teleport detection ---
    private ushort _snapshotSeq;                                     // global snapshot sequence (clients ack it)
    private readonly Dictionary<int, NetEntityState> _entityScratch = new(); // reused per-tick entity set
    // Memoized "<classname> <netname>" catalog keys for model-less projectiles so BuildEntitySet doesn't
    // allocate a fresh interpolated string per projectile per tick-frame (3.2-4). Keyed by (classname, netname)
    // — both stable per projectile — so the set of distinct entries is tiny (one per projectile type).
    private readonly Dictionary<(string, string), string> _projKeyCache = new();

    // Small stable per-player net ids (humans AND bots), decoupled from the large/random ENet peer id and kept
    // below EntityNetBase so the player and non-player id spaces never collide.
    private readonly Dictionary<Player, int> _playerNetIds = new(ReferenceEqualityComparer.Instance);
    private int _nextPlayerNetId = 1;
    private float[] _moveVars = System.Array.Empty<float>();         // this tick's movement cvars
    private uint _moveVarsHash;

    // this tick's scoreboard snapshot (built once, broadcast to all; per-client gated by _scoreVersion).
    private readonly List<XonoticGodot.Net.ScoreRowWire> _scoreRows = new();
    private readonly List<(int team, int score)> _scoreTeams = new();
    private int _scoreVersion;
    // this tick's score-LAYOUT generation (the active label/flag set + gametype/teamplay): the ScoreInfo block
    // (QC ENT_CLIENT_SCORES_INFO) is sent per-client only when this changed since they last got it (a mode
    // switch). Distinct from _scoreVersion, which gates the per-VALUE scoreboard block.
    private int _scoreInfoGen;
    private uint _scoreInfoHash;
    private readonly Dictionary<Player, NVec3> _lastSnapOrigin = new(ReferenceEqualityComparer.Instance);

    // --- lag compensation: a per-player position history rewound at hitscan/projectile fire (antilag.qc) ---
    private readonly Dictionary<Player, AntilagBuffer> _antilag = new(ReferenceEqualityComparer.Instance);

    // The antilag breadth beyond players (antilag.qc antilag_takeback_all/restore_all also rewind IL_EACH
    // g_monsters PLUS IL_EACH g_projectiles where classname=="nade"): a parallel per-Entity history for every
    // monster + nade. Keyed by the entity itself (the QC stores the ring on the entity — antilag_record(it, it,
    // altime)). Pruned each frame as entities free; cleared on (re)spawn via SetOrigin teleport detection.
    private readonly Dictionary<Entity, AntilagBuffer> _antilagEntities = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entity> _antilagPruneScratch = new();   // reused: freed/gone entities to drop

    // antilag enable + record nudge, refreshed once per frame from the cvar store (xonotic-server.cfg:
    // g_antilag default 2 = "server side hit scan in the past"; g_antilag_nudge default 0). Only mode 2 takes
    // the shooter back on the server (antilag.qc traceline_antilag: autocvar_g_antilag != 2 → lag = 0); mode 1 is
    // client-verified hitscan and mode 0 is off — both apply zero server-side takeback.
    private int _antilagMode = 2;
    private float _antilagNudge;

    // --- master-server registration (server browser): periodic heartbeat + answer getinfo probes ---
    private MasterServerLink? _master;
    private readonly List<IPEndPoint> _masters = new();
    private float _heartbeatAccum;
    private const float HeartbeatInterval = 180f; // DP re-registers every ~3 minutes

    /// <summary>An origin jump beyond this in a single tick is a teleport/respawn, not movement → set the teleport bit
    /// so the client snaps instead of lerping (normal play moves &lt;~20u/tick at 72 Hz).</summary>
    private const float TeleportTickDistance = 150f;

    /// <summary>Low-pass factor for the measured RTT (per input frame): smaller = smoother but slower to track a
    /// latency change. ~0.25 reaches ~95% of a step in ~10 input frames, damping the per-packet jitter that would
    /// otherwise make the rewind depth (and thus hit registration) flicker.</summary>
    private const float RttSmoothing = 0.25f;

    /// <summary>Per-frame captured fire-and-forget effects (broadcast unreliable in the event bundle).</summary>
    private readonly List<CapturedEffect> _effectQueue = new();

    /// <summary>Per-frame captured notifications (broadcast reliable in the reliable bundle).</summary>
    private readonly List<CapturedNotification> _notifyQueue = new();

    /// <summary>Per-frame captured positional sounds (broadcast unreliable in the sound bundle, DP SV_StartSound).</summary>
    private readonly List<CapturedSound> _soundQueue = new();

    /// <summary>One connected client: its peer id, its player, the input ring + ack cursor.</summary>
    private sealed class PeerState
    {
        public int PeerId;
        public int NetId;               // the small stable entity id sent on the wire (not the raw ENet peer id)
        public Player? Player;          // null until the handshake completes
        public bool Accepted;
        public uint LastProcessedSeq;   // servercommandframe: the highest input seq applied
        public uint HighestQueuedSeq;   // highest seq currently queued (dedup of the redundant tail)
        public readonly Queue<InputCommand> Pending = new();
        public InputCommand Last;       // last applied input (repeated if the queue starves — held keys)
        public bool HasLast;
        // Per-frame (variable-dt) input mode for this client (set from the input-frame header): when true,
        // ProvideInput drains ALL pending commands this tick and OnClientMove runs movement per command (each with
        // its own DeltaTime) — DP's process-queued-client-moves; false = the legacy one-command-per-tick path.
        public bool PerFrameInput;

        // The input resolved for the current sim tick + the sim time it was resolved at. GameWorld pulls
        // InputProvider TWICE per tick (movement + the W_WeaponFrame driver); we dequeue/ack only on the first
        // call of a tick and return this cached command on the second, so the queue isn't drained 2x and
        // LastProcessedSeq doesn't over-ack an input whose movement never ran.
        public IMovementInput? TickInput;
        public float TickInputTime = float.NaN;
        // Per-frame mode: the per-command movement inputs drained for THIS tick (one per client render frame),
        // iterated by OnClientMove so each integrates with its own dt. Reused (cleared) each tick; keyed live by
        // TickInputTime == world time. Empty/stale → OnClientMove falls back to the single merged TickInput.
        public readonly List<IMovementInput> TickBatch = new();

        /// <summary>QC PlayerUseKey is edge-driven (DP fires it once per +use press, not per tick): the
        /// RELEASED-&gt;PRESSED rising edge of the +use button (BIT(5)) triggers vehicle board/exit. Track the
        /// previous tick's +use state per player so a HELD key doesn't board-&gt;exit-&gt;board flicker.</summary>
        public bool UsePrevDown;

        /// <summary>Per-client delta-compression baseline ring (the entity stream this client interpolates).</summary>
        public readonly ServerSnapshotHistory SnapHistory = new();

        /// <summary>The movevars hash last sent to this client (re-send the block only when the physics changes).</summary>
        public uint LastMoveVarsHash;

        /// <summary>The hash of the preset-RESOLVED movevar vector last sent to this client (v7, T54 —
        /// g_physics_clientselect). 0 = no per-client override outstanding; when a peer's resolved physics stops
        /// deviating from the global block, a count-0 "clear" block is sent once and this resets to 0.</summary>
        public uint LastResolvedVarsHash;

        /// <summary>The score-table version last sent (re-send the scoreboard block only when a score changed). -1 = never.</summary>
        public int LastScoreVersion = -1;

        /// <summary>The score-LAYOUT hash last sent (re-send the ScoreInfo label/flag block only on a mode switch).
        /// 0 = never sent (so the first snapshot always carries the layout, even if it equals the default hash).</summary>
        public uint LastScoreInfoHash;
        public bool SentScoreInfo;

        /// <summary>FNV hash of the last GametypeStatusBlock sent to this client (0 = never) — the per-mode
        /// round/objective HUD stats (T53) are personalized (the KH "31 = self" slot, the SURV hunter
        /// visibility), so each peer gates on the hash of ITS OWN serialized block, resending only when the
        /// bytes change.</summary>
        public uint LastModeStatusHash;

        /// <summary>The accuracy-change generation last sent to this client (T57, QC ENT_CLIENT_ACCURACY's
        /// per-weapon SendFlags change detection): the owner's per-weapon accuracy byte array is re-sent only
        /// when <see cref="Scores.AccuracyGeneration"/> moves. -1 = never sent (so the first snapshot to a client
        /// always carries the array, even when it's all zeros).</summary>
        public int LastAccuracyGen = -1;

        /// <summary>Measured round-trip latency in seconds (DP <c>host_client-&gt;ping = cmd.receivetime - cmd.time</c>,
        /// sv_user.c:847) — the server-receive time minus the server-time the client echoed from the snapshot it was
        /// responding to. Exponentially smoothed to damp per-packet jitter; this is the value <c>ANTILAG_LATENCY</c>
        /// (antilag.qh: <c>CS(e).ping * 0.001</c>) consumes to rewind. 0 on a LAN/listen/bot client (fully caught up).</summary>
        public float MeasuredRtt;
        public bool HasRtt;

        /// <summary>[T47] QC <c>CS(this).cmd_floodtime</c> (server/command/cmd.qc): the per-client command-flood
        /// bucket cursor — the absolute sim time the client's command budget is "used up to". A new command is
        /// rejected when <c>frameStart + count*time &lt; CmdFloodTime</c>; otherwise the cursor advances by
        /// <c>antispam_time</c> from <c>max(frameStart, CmdFloodTime)</c> (exponential backoff under sustained
        /// spam). Initialized to 0; the count*time headroom means a fresh client never trips on its first burst.</summary>
        public float CmdFloodTime;

        /// <summary>[T47] QC <c>.ban_checked</c> (server/ipban.qc <c>Ban_MaybeEnforceBanOnce</c>): the ban gate runs
        /// at most once per client. Once set, the per-command ban check short-circuits to "not banned" (the QC
        /// once-guard) — a banned client is dropped on its first command, so re-checking is pointless.</summary>
        public bool BanChecked;

        /// <summary>The session challenge issued to this client and whether its identity is verified (SessionAuth).</summary>
        public byte[]? AuthChallenge;
        public byte[]? PendingPublicKey;   // the client's identity key, held until it signs the challenge
        public string PendingName = "";    // the requested name, applied on successful auth
        public string IdentityFingerprint = "";
    }

    public ServerNet(NetTransport.Server transport, GameWorld world, string serverName = "XonoticGodot Server")
    {
        _transport = transport;
        _world = world;
        _serverName = serverName;

        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _transport.PacketReceived += OnPacket;

        // The world asks us for each client's movement input every tick; serve it from that client's queue.
        _world.InputProvider = ProvideInput;
        _world.TickMovementBatch = TickMovementBatch;

        // Wire the NET sinks so server-emitted effects/notifications are serialized + broadcast (the
        // EffectEmitter/NotificationSystem "wire to netcode" TODOs). They capture into our per-frame queues;
        // Tick() flushes them after the snapshot.
        EffectEmitter.Sink = new EffectNetSink(this);
        NotificationSystem.Sink = new NotificationNetSink(this);

        // Positional sounds (DP SV_StartSound): the sim emits via Api.Sound.Play → SoundService.Broadcast. On a
        // listen server _world.Services.SoundImpl is the SAME instance Api.Sound resolves to, so capturing here
        // grabs exactly what the sim emits; flushed each frame in FlushSounds() like the effect bundle. A NAMED
        // method (not a lambda) so Dispose() can unsubscribe and a re-host on a reused world won't double-subscribe.
        _world.Services.SoundImpl.Broadcast += OnSoundEmitted;

        // Install the lag-compensation hook so hitscan weapon traces rewind other players to the shooter's view
        // time (antilag.qc). Weapons call LagComp.Begin/End ambiently; this routes it to our rewind/restore.
        LagComp.Provider = new LagCompProvider(this);

        // Per-player physics resolution (T54, QC Physics_UpdateStats → Physics_ClientOption): the shared sim
        // asks this for each player's preset-resolved movevar vector. Returns null when g_physics_clientselect
        // is off (the stock default) — the sim then reads the global cvars exactly as before.
        _presetProvider = ResolvePresetVector;
        MovementParameters.PresetProvider = _presetProvider;
    }

    // --- per-player preset physics (T54): the provider + a per-sim-tick resolve cache ---
    private readonly System.Func<Entity, float[]?> _presetProvider;
    private readonly Dictionary<Player, float[]?> _presetCache = new(ReferenceEqualityComparer.Instance);
    private float _presetCacheTime = float.NaN;
    private float[]? _presetCacheGlobals;

    /// <summary>
    /// QC <c>Physics_ClientOption</c> over the whole movevar vector for one player. Null when client physics
    /// selection is off, or when the player's resolution doesn't deviate from the globals (so the sim takes the
    /// plain FromCvars path). Cached per sim tick (keyed on <c>_world.Time</c>, which is constant within a tick)
    /// because the sim asks once per move and the snapshot writer asks again at broadcast.
    /// A bot/unknown player resolves with cl_physics "" — chain 2 skipped, but the g_physics_clientselect_default
    /// chain still applies, faithful to player.qc:34-40 running for ANY entity.
    /// </summary>
    private float[]? ResolvePresetVector(Entity e)
    {
        if (e is not Player p)
            return null;
        var cvars = _world.Services.Cvars;
        if (cvars.GetFloat("g_physics_clientselect") == 0f)
            return null;

        float now = _world.Time;
        if (_presetCacheTime != now)
        {
            _presetCache.Clear();
            _presetCacheGlobals = MoveVarsBlock.Capture(cvars);
            _presetCacheTime = now;
        }
        if (_presetCache.TryGetValue(p, out float[]? cached))
            return cached;

        string clPhysics = _byPlayer.ContainsKey(p) ? _world.Commands.GetClientCvar(p, "cl_physics", "") : "";
        float[] globals = _presetCacheGlobals!;
        float[] resolved = MoveVarsBlock.CaptureResolved(cvars, clPhysics, globals, _world.Services.CvarsImpl.Has);
        // No deviation from the global vector → null (the cheap shared path; also what keys the wire "clear").
        float[]? result = MoveVarsBlock.Hash(resolved) == MoveVarsBlock.Hash(globals) ? null : resolved;
        _presetCache[p] = result;
        return result;
    }

    /// <summary>Convenience: start a server on <paramref name="port"/> and drive <paramref name="world"/>.</summary>
    public static ServerNet? Start(GameWorld world, int port, int maxClients = 32, string serverName = "XonoticGodot Server")
    {
        NetTransport.Server? t = NetTransport.Server.Start(port, maxClients);
        return t is null ? null : new ServerNet(t, world, serverName) { _maxClients = maxClients };
    }

    // =====================================================================================
    //  Per-frame drive
    // =====================================================================================

    /// <summary>
    /// Advance one host frame: pump the transport, step the authoritative world by <paramref name="realDelta"/>
    /// seconds, then broadcast a snapshot to each client and flush the captured effect/notification bundles.
    /// </summary>
    public void Tick(float realDelta)
    {
        // S5: when threaded, the WHOLE tick runs under the host's shared gate so it can't race the main thread's
        // prediction span (NetGame._Process takes the same gate). When _simGate is null (the default) there is NO
        // lock and the inbound queue is always empty (RunOnSimThread ran inline) — byte-for-byte the old path.
        if (_simGate is null)
        {
            TickInternal(realDelta);
            return;
        }
        lock (_simGate)
        {
            // Drain main→server commands (chat / bot_add/remove / changelevel) FIRST so Commands.Execute runs on
            // the thread that owns the world, before this tick reads/mutates it.
            while (_inbound.TryDequeue(out Action? a)) a();
            TickInternal(realDelta);
        }
    }

    private void TickInternal(float realDelta)
    {
        // 1) receive: handshakes + input frames (fills each peer's input queue).
        using (Prof.Sample("net.poll")) _transport.Poll();

        // 2) simulate: the world runs its fixed ticks, pulling each client's queued input via ProvideInput
        //    and firing effect/notification emissions into our sinks. ticksRan == 0 when the host renders faster
        //    than the 72 Hz sim — those frames leave the world byte-identical to the last broadcast.
        int ticksRan = _world.Frame(realDelta);
        Prof.Mark("ticks", ticksRan); // >1 ⇒ a server.tick spike is catch-up amortizing a prior stall, not new work

        // 2b) observer/join lifecycle: a human connects as an OBSERVER (ClientConnect → TRANSMUTE(Observer)) and
        //     only enters the match via Join — on +jump/+attack or the delayed autojoin (server/client.qc
        //     ObserverOrSpectatorThink / PlayerPreThink). The headless world doesn't drive this on the real-client
        //     path, so we run it here per accepted peer from its last input. (Bots autojoined at connect.)
        DriveObserverJoins();

        // 3) send: one snapshot per client + the shared event bundles — but ONLY on a frame where the world
        //    actually advanced. The sim is a fixed 72 Hz accumulator, so when the render rate outruns it many
        //    frames run 0 ticks and the world (and _world.Time) is identical to the last broadcast: rebuilding the
        //    entity set + scoreboard and re-encoding a duplicate snapshot per peer is pure waste, and it's the
        //    dominant per-frame server cost at high refresh. Gating on ticksRan sends at the sim rate (DP networks
        //    at sys_ticrate, not the render rate) and lets a 0-tick frame after a hitch recover a frame sooner.
        //    Events/minigame state are only produced inside a tick, so on a 0-tick frame they're empty too.
        using (Prof.Sample("net.send"))
        if (ticksRan > 0)
        {
            BroadcastSnapshots();
            SendMinigameState(); // [T38] push per-peer minigame session snapshots (reliable channel)
            SendMatchState();    // global match clock (GAMESTARTTIME/TIMELIMIT/warmup) → TIMER panel
            SendWaypoints(); // per-peer waypoint sprites (CTF flags + player pings…) → 3D markers + radar icons
            FlushEventBundles();
        }

        // 4) master-server registration: answer browser probes + re-heartbeat periodically.
        PumpMasterServer(realDelta);

        // 5) flush: push the snapshots/bundles queued above onto the wire NOW (send-only) instead of letting them
        //    wait for the next tick's Poll(). On the in-process listen loop the client's Poll() later this SAME
        //    frame then receives the snapshot, so the fire/knockback the server just simulated reconciles a render-
        //    frame sooner — the return leg of the input→fire→feedback latency (pairs with the client send flush).
        using (Prof.Sample("net.flush")) _transport.Flush();
    }

    /// <summary>
    /// Drive the observer→join lifecycle for each accepted human peer (server/client.qc ObserverOrSpectatorThink
    /// + the PlayerPreThink delayed autojoin). Reads the peer's last applied input for the +jump/+attack join
    /// edge and runs <see cref="ClientManager.ObserverOrSpectatorThink"/> (which also handles the ~1s delayed
    /// autojoin). A bot never reaches here (it joins at connect).
    ///
    /// T44 (spectator free-flight): an observer is no longer frozen here. In QC the per-client move tick
    /// (<c>sys_phys_update</c>) runs for EVERY IS_CLIENT — players AND spectators — and a spectator has
    /// MOVETYPE_NOCLIP/FLY so it FLIES (the fly branch, scaled by STAT(SPECTATORSPEED)). Zeroing the velocity
    /// each frame would defeat that. The observer reaches <c>Movement.Move</c> via the OnClientMove observer
    /// gate (see T44's gameWorldWiring seam) and its speed is driven by the spectator-speed ladder in
    /// <c>PlayerPhysics.SpectatorControl</c>.
    /// </summary>
    private void DriveObserverJoins()
    {
        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is not { IsObserver: true } observer)
                continue;

            bool jump = false, attack = false, attack2 = false;
            if (st.HasLast)
            {
                InputButtons b = st.Last.TypedButtons;
                jump = (b & InputButtons.Jump) != 0;
                attack = (b & InputButtons.Attack) != 0;
                attack2 = (b & InputButtons.Attack2) != 0;
            }
            _world.Clients.ObserverOrSpectatorThink(observer, jump, attack, attack2);
        }
    }

    // =====================================================================================
    //  Master server (server browser): heartbeat to the master + answer getinfo probes
    // =====================================================================================

    /// <summary>
    /// Register this server with the given master(s) (DP <c>sv_masterextra*</c>) so it appears in the internet
    /// server browser. Resolves each <c>host:port</c> once, sends an immediate heartbeat, and answers clients'
    /// <c>getinfo</c> probes with this server's infostring. Call after <see cref="Start"/>; silently skips an
    /// address that can't be resolved.
    /// </summary>
    /// <summary>
    /// Make this server discoverable on the LAN: bind the out-of-band <c>getinfo</c> answerer to a well-known
    /// port NEXT TO the game port so browser broadcasts reach it. DP answers OOB probes on the game socket
    /// itself, but our game socket is ENet (it drops raw OOB datagrams), so the discovery socket lives on the
    /// first free port in <c>[gamePort+1 .. gamePort+8]</c> — the browser sweeps that small range and the
    /// <c>infoResponse</c> carries the real game <c>port</c> to connect to (<see cref="BuildServerInfo"/>).
    /// Safe to call when every candidate port is taken (discovery is then simply off, logged).
    /// </summary>
    public void EnableLanDiscovery(int gamePort)
    {
        if (_master is not null)
            return; // already discoverable (EnableMasterServer bound a link)
        for (int candidate = gamePort + 1; candidate <= gamePort + 8; candidate++)
        {
            try
            {
                _master = new MasterServerLink(candidate);
                _master.GetInfoRequested += (from, _) => _master!.SendInfoResponse(from, BuildServerInfo(gamePort));
                GD.Print($"[ServerNet] LAN discovery: answering getinfo on UDP {candidate} (game port {gamePort}).");
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // port taken (another local server's discovery socket) — try the next one
            }
        }
        GD.Print($"[ServerNet] LAN discovery unavailable: UDP {gamePort + 1}..{gamePort + 8} all in use.");
    }

    public void EnableMasterServer(IEnumerable<string> masters, int port)
    {
        _master ??= new MasterServerLink();
        _master.GetInfoRequested += (from, _) => _master!.SendInfoResponse(from, BuildServerInfo(port));
        foreach (string addr in masters)
        {
            if (TryResolve(addr, out IPEndPoint? ep))
                _masters.Add(ep!);
        }
        _heartbeatAccum = HeartbeatInterval; // heartbeat on the next Tick
        GD.Print($"[ServerNet] master-server registration enabled for {_masters.Count} master(s).");
    }

    private void PumpMasterServer(float realDelta)
    {
        if (_master is null)
            return;
        _master.Poll(); // answer any getinfo probes (the event handler replies)

        _heartbeatAccum += realDelta;
        if (_heartbeatAccum >= HeartbeatInterval)
        {
            _heartbeatAccum = 0f;
            foreach (IPEndPoint m in _masters)
                _master.SendHeartbeat(m);
        }
    }

    /// <summary>The DP infostring a browser sees: hostname, map, gametype, player counts, protocol parity.</summary>
    private Dictionary<string, string> BuildServerInfo(int port) => new()
    {
        ["hostname"] = _serverName,
        ["mapname"] = _world.Services.Cvars.GetString("mapname"),
        ["gametype"] = _world.GameType?.RegistryName ?? "dm",
        ["clients"] = _byPlayer.Count.ToString(),
        // g_maxplayers 0/unset means "no gameplay cap" — report the transport's connection cap so the
        // browser's players column shows real slots instead of "/0".
        ["sv_maxclients"] = _world.Services.Cvars.GetFloat("g_maxplayers") > 0
            ? _world.Services.Cvars.GetString("g_maxplayers")
            : _maxClients.ToString(),
        ["protocol"] = NetProtocol.BuildParity().ToString(),
        ["port"] = port.ToString(),
        ["gamename"] = "Xonotic",
    };

    private static bool TryResolve(string hostPort, out IPEndPoint? ep)
    {
        ep = null;
        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out int port))
            return false;
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(hostPort[..colon]);
            if (addrs.Length == 0) return false;
            ep = new IPEndPoint(addrs[0], port);
            return true;
        }
        catch (Exception ex) { GD.PrintErr($"[ServerNet] master resolve '{hostPort}' failed: {ex.Message}"); return false; }
    }

    // =====================================================================================
    //  Connection lifecycle
    // =====================================================================================

    private void OnPeerConnected(int peerId)
    {
        // ENet-level connect only — we wait for the client's HandshakeRequest (build-parity) before admitting
        // it as a player. Track the slot so we can attach the player on accept.
        _peers[peerId] = new PeerState { PeerId = peerId };
        GD.Print($"[ServerNet] peer {peerId} connected (awaiting handshake).");
    }

    private void OnPeerDisconnected(int peerId)
    {
        if (_peers.Remove(peerId, out PeerState? st) && st.Player is not null)
        {
            _byPlayer.Remove(st.Player);
            _antilag.Remove(st.Player);
            _lastSnapOrigin.Remove(st.Player);
            _playerNetIds.Remove(st.Player);
            _world.Commands.ForgetPlayer(st.Player); // drop the per-client replicated-cvar/autoswitch tables
            _world.Clients.ClientDisconnect(st.Player);
            GD.Print($"[ServerNet] peer {peerId} disconnected ({st.Player.NetName}).");
        }
    }

    private void OnPacket(int from, int channel, byte[] data)
    {
        var r = new BitReader(data);
        var control = (NetControl)r.ReadByte();
        switch (control)
        {
            case NetControl.HandshakeRequest:
                HandleHandshake(from, ref r);
                break;
            case NetControl.HandshakeAuth:
                HandleAuth(from, ref r);
                break;
            case NetControl.InputFrame:
                HandleInputFrame(from, ref r);
                break;
            case NetControl.ClientCommand:
                HandleClientCommand(from, ref r);
                break;
            default:
                // unknown / not-server-bound control byte — ignore (a malformed or out-of-phase packet).
                break;
        }
    }

    /// <summary>The <see cref="Player"/> assigned the small stable net id <paramref name="netId"/> (humans and
    /// bots), or null if none — the reverse of the wire id (lets the host map <c>ClientNet.LocalNetId</c> back to
    /// the local player for console client-commands).</summary>
    public Player? PlayerByNetId(int netId)
    {
        foreach (var kv in _playerNetIds)
            if (kv.Value == netId)
                return kv.Key;
        return null;
    }

    /// <summary>
    /// DP <c>clc_stringcmd</c>: run a console command line on behalf of the sending peer's player. Dispatched as
    /// a CLIENT command (<c>isServerConsole: false</c>, <c>caller</c> = the peer's player) so caller-gated
    /// commands (kill/say/team/…) act on the right player; the command's output is returned to that peer as a
    /// <see cref="NetControl.ServerPrint"/>. Ignored from an unauthed/observer peer.
    /// </summary>
    private void HandleClientCommand(int peerId, ref BitReader r)
    {
        string line = r.ReadString();
        if (r.BadRead)
            return;
        if (!_peers.TryGetValue(peerId, out PeerState? st) || !st.Accepted || st.Player is null
            || string.IsNullOrWhiteSpace(line))
            return;

        // [T47] SV_ParseClientCommand's three pre-dispatch gates (server/command/cmd.qc:1170-1251), run in QC
        // order BEFORE any command executes. Any gate that returns drops the command silently (QC `return`):
        //
        // GATE 1 — UTF-8 round-trip validation (cmd.qc:1172-1178): QC re-encodes the command char-by-char through
        // chr2str(str2chr(...)) and drops it if it doesn't round-trip. Malformed input never reaches the parser.
        if (!ClientCommandRegistry.IsValidUtf8Command(line))
            return;

        // GATE 2 — Ban_MaybeEnforceBanOnce (cmd.qc:1180-1181 → ipban.qc): if the client is banned, tell+drop them
        // and don't parse the command. Runs at most once per client (the QC .ban_checked once-guard).
        if (!st.BanChecked)
        {
            st.BanChecked = true;
            if (_world.Bans.MaybeEnforceBan(st.Player))
                return; // banned → dropped by the wired Bans.DropClient pipeline; command discarded
        }

        // GATE 3 — per-client command flood bucket (cmd.qc:1191-1251): exponential-backoff anti-spam on the
        // common command stream, exempting the engine/server/chat-handled verbs (begin/download/prespawn/spawn/
        // sentcvar/pause/say/say_team/tell/minigame-non-common/…). A flooded command is rejected with a notice.
        // The exemption test + budget math live in ClientCommandRegistry (the testable Server assembly); this
        // method supplies the per-client cursor, the sim frame-start time, and the antispam cvar reads.
        string verb = FirstToken(line);
        if (!ClientCommandRegistry.IsCommandFloodExempt(verb, SecondToken(line)))
        {
            float antispamTime = Cvars.FloatOr("sv_clientcommand_antispam_time", 1f);
            float antispamCount = Cvars.FloatOr("sv_clientcommand_antispam_count", 8f);
            // QC uses gettime(GETTIME_FRAMESTART); _world.Time (the sim clock, constant within a tick) is the
            // faithful equivalent of that frame-start time. Capture the pre-update cursor for the wait notice.
            float floodBefore = st.CmdFloodTime;
            if (!ClientCommandRegistry.TryPassFlood(ref st.CmdFloodTime, _world.Time, antispamCount, antispamTime))
            {
                float wait = floodBefore - (_world.Time + antispamCount * antispamTime); // QC: cmd_floodtime - mod_time
                SendPrint(peerId, $"^3CMD FLOOD CONTROL: wait ^1{wait:0.######}^3 seconds, command was: {line}");
                return;
            }
        }

        CommandContext ctx = _world.Commands.Execute(line, isServerConsole: false, caller: st.Player);
        string output = ctx.Output;
        if (string.IsNullOrEmpty(output))
            return;
        SendPrint(peerId, output.TrimEnd('\n', '\r'));
    }

    /// <summary>The lowercased first whitespace-delimited token of <paramref name="line"/> (QC <c>strtolower(argv(0))</c>).</summary>
    private static string FirstToken(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return line.Substring(start, i - start).ToLowerInvariant();
    }

    /// <summary>The second whitespace-delimited token of <paramref name="line"/> (QC <c>argv(1)</c>), "" if absent.</summary>
    private static string SecondToken(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;          // skip argv(0)
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        int start = i;
        while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        return start < line.Length ? line.Substring(start, i - start) : "";
    }

    /// <summary>Send one console line to a single peer (DP <c>svc_print</c>).</summary>
    private void SendPrint(int peerId, string text)
    {
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.ServerPrint);
        _scratchWriter.WriteString(text);
        _transport.Send(peerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    /// <summary>
    /// Broadcast one console line to EVERY accepted client (DP <c>bprint</c> over <c>svc_print</c>) — the host
    /// wires <see cref="Commands.ChatBroadcast"/> (vote/ban/team notices) and <see cref="Commands.ChatHandler"/>
    /// (player chat) to this so they reach all clients' consoles, the listen-server host included.
    /// </summary>
    public void BroadcastPrint(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.ServerPrint);
        _scratchWriter.WriteString(text);
        foreach (PeerState st in _peers.Values)
            if (st.Accepted)
                _transport.Send(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    // =============================================================================================
    // [T46] chat delivery — per-player sprint + team/private routing with ignore filtering. The chat engine
    // (Chat.Say) does the routing/ignore/flood logic over ClientManager.Players; these are the net send paths.
    // A host wires Commands.ChatToPlayer to SendChatToPlayer so each routed recipient gets a reliable svc_print.
    // =============================================================================================

    /// <summary>QC <c>sprint(client, text)</c>: send one chat line to a single player's console (DP svc_print over
    /// the reliable channel). No-op if the player has no accepted peer (a bot, or a not-yet-handshaked client).</summary>
    public void SendChatToPlayer(Player player, string text)
    {
        if (player is null || string.IsNullOrEmpty(text))
            return;
        if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
            return;
        SendPrint(st.PeerId, text);
    }

    /// <summary>
    /// QC the say_team FOREACH_CLIENT branch (chat.qc:328): deliver <paramref name="text"/> to every real client
    /// on <paramref name="team"/> who has not ignored <paramref name="sender"/>. Append-only convenience for a
    /// host that prefers ServerNet to own team routing; <see cref="Chat.Say"/> already routes per-player through
    /// <see cref="SendChatToPlayer"/>, so this is an alternative entry, not on the default path.
    /// </summary>
    public void SendTeamChat(Player? sender, int team, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Player p in _world.Clients.Players)
        {
            if (p.IsBot || p.IsObserver || (int)p.Team != team)
                continue;
            if (ReferenceEquals(p, sender))
                continue;
            if (sender is not null && IgnorePlayerInList(p, sender))
                continue;
            SendChatToPlayer(p, text);
        }
    }

    /// <summary>
    /// QC the private (tell) delivery (chat.qc:295-307): deliver <paramref name="text"/> to <paramref name="target"/>
    /// only, suppressed if the target has ignored <paramref name="sender"/>. Append-only convenience entry.
    /// </summary>
    public void SendPrivateChat(Player? sender, Player target, string text)
    {
        if (target is null || string.IsNullOrEmpty(text))
            return;
        if (sender is not null && IgnorePlayerInList(target, sender))
            return; // sender is ignored by target — drop it
        SendChatToPlayer(target, text);
    }

    /// <summary>QC <c>ignore_playerinlist(this, other)</c>: is <paramref name="other"/> on <paramref name="self"/>'s
    /// ignore list? Delegates to the chat engine's PersistentId-keyed check.</summary>
    public static bool IgnorePlayerInList(Player self, Player other) => Chat.IgnorePlayerInList(self, other);

    /// <summary>[T38] Push each changed minigame session's snapshot to its participating peers (QC
    /// <c>minigame_resend</c> + <c>minigame_CheckSend</c>), each carrying that peer's own team; and an empty
    /// envelope to anyone who just left / whose session ended (QC the per-entity removal → CSQC
    /// <c>deactivate_minigame</c>). Reliable channel — the board state must arrive.</summary>
    private void SendMinigameState()
    {
        MinigameSessionManager mg = _world.Minigames;

        // Changed sessions → per-participant envelope (carries that participant's own team).
        foreach (MinigameSession s in mg.DrainDirty())
        {
            foreach ((Player player, int team) in mg.Participants(s))
            {
                if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
                    continue;
                _scratchWriter.Reset();
                _scratchWriter.WriteByte((byte)NetControl.MinigameState);
                MinigameNetState.EncodeEnvelope(_scratchWriter, s, team);
                _transport.Send(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
            }
        }

        // Departed players → an empty envelope so the client hides the board.
        foreach (Player player in mg.DrainDeparted())
        {
            if (!_byPlayer.TryGetValue(player, out PeerState? st) || !st.Accepted)
                continue;
            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.MinigameState);
            MinigameNetState.EncodeEnvelope(_scratchWriter, null, 0);
            _transport.Send(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
        }
    }

    private float _nextMatchStateSend;
    private string _lastMatchStateKey = "";

    /// <summary>Broadcast the GLOBAL match-clock state (QC STAT(GAMESTARTTIME)/TIMELIMIT/WARMUP) to every
    /// accepted peer so the TIMER panel can count up/down on the play path. Sent immediately when a value
    /// changes (warmup→live, overtime, timelimit edit) and otherwise ~1×/s so a late joiner gets it within a
    /// second. Reliable + tiny (~18 bytes). Same field order as <see cref="ClientNet.HandleMatchState"/>.</summary>
    private void SendMatchState()
    {
        float gameStart = _world.GameStartTime;
        float timeLimitSec = _world.Services.Cvars.GetFloat("timelimit") * 60f;
        bool warmup = _world.Warmup.WarmupStage;
        float warmupLimit = _world.Warmup.WarmupLimit;
        bool intermission = _world.Intermission.Running;

        string key = $"{gameStart:F2}|{timeLimitSec:F1}|{(warmup ? 1 : 0)}|{warmupLimit:F1}|{(intermission ? 1 : 0)}";
        if (key == _lastMatchStateKey && _world.Time < _nextMatchStateSend)
            return;
        _lastMatchStateKey = key;
        _nextMatchStateSend = _world.Time + 1f;

        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.MatchState);
        _scratchWriter.WriteFloat(gameStart);
        _scratchWriter.WriteFloat(timeLimitSec);
        _scratchWriter.WriteBool(warmup);
        _scratchWriter.WriteFloat(warmupLimit);
        _scratchWriter.WriteBool(intermission);

        foreach (PeerState st in _peers.Values)   // real network peers (matches BroadcastSnapshots; excludes bots)
            if (st.Accepted && st.Player is not null)
                _transport.Send(st.PeerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    private readonly System.Collections.Generic.List<XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite> _wpScratch = new();
    private readonly System.Collections.Generic.List<XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite> _wpVisible = new();
    private bool _wpWasNonEmpty;

    /// <summary>Push the live waypoint sprites (QC the networked ENT_CLIENT_WAYPOINT entities) to each peer,
    /// filtered to what that peer may see (team/rule/predicate). The set is the persistent
    /// <see cref="XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites"/> manager (player pings, deployed
    /// markers) merged with the gametype's per-tick objective rebuild (flags/points/keys via
    /// <c>GameType.CollectWaypoints</c>). Unreliable + small (positions move with carriers). Skipped for
    /// waypoint-less modes except the single clearing message when a mode that HAD waypoints drops to none.</summary>
    private void SendWaypoints()
    {
        var mgr = XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.Active;
        _wpScratch.Clear();
        _world.GameType?.CollectWaypoints(_wpScratch);

        int total = mgr.Count + _wpScratch.Count;
        if (total == 0 && !_wpWasNonEmpty)
            return; // waypoint-less mode: never spam empty messages
        _wpWasNonEmpty = total > 0;

        float now = _world.Time;
        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null)
                continue;
            Player peer = st.Player;

            // Gather the peer-visible subset (manager persistents + gametype objectives), capped to a byte count.
            _wpVisible.Clear();
            foreach (var wp in mgr) if (WaypointVisible(wp, peer) && _wpVisible.Count < 255) _wpVisible.Add(wp);
            foreach (var wp in _wpScratch) if (WaypointVisible(wp, peer) && _wpVisible.Count < 255) _wpVisible.Add(wp);

            _scratchWriter.Reset();
            _scratchWriter.WriteByte((byte)NetControl.Waypoints);
            _scratchWriter.WriteByte((byte)_wpVisible.Count);
            foreach (var wp in _wpVisible)
            {
                System.Numerics.Vector3 o = wp.Origin;
                _scratchWriter.WriteLong(wp.Id);
                _scratchWriter.WriteFloat(o.X);
                _scratchWriter.WriteFloat(o.Y);
                _scratchWriter.WriteFloat(o.Z);
                _scratchWriter.WriteByte((byte)(wp.Team & 0xFF));
                _scratchWriter.WriteString(wp.SpriteName);
                _scratchWriter.WriteByte((byte)(wp.RadarIcon & 0xFF));
                _scratchWriter.WriteByte(Clamp255(wp.Color.X));
                _scratchWriter.WriteByte(Clamp255(wp.Color.Y));
                _scratchWriter.WriteByte(Clamp255(wp.Color.Z));
                _scratchWriter.WriteFloat(wp.Health);
                _scratchWriter.WriteFloat(XonoticGodot.Common.Gameplay.Waypoints.WaypointSprites.FadeAlpha(wp));
                _scratchWriter.WriteByte((byte)(wp.HelpmeUntil > now ? 1 : 0));
                _scratchWriter.WriteFloat(wp.MaxDistance);
                _scratchWriter.WriteBool(wp.Hideable);
            }
            _transport.Send(st.PeerId, _scratchWriter.WrittenSpan, reliable: false);
        }
    }

    private static byte Clamp255(float c) => (byte)System.Math.Clamp((int)(c * 255f), 0, 255);

    /// <summary>QC WaypointSprite_visible_for_player: a waypoint's team/rule/predicate gate for one viewer.</summary>
    private static bool WaypointVisible(XonoticGodot.Common.Gameplay.Waypoints.WaypointSprite wp, Player peer)
    {
        if (wp.VisibleForPlayer is not null) return wp.VisibleForPlayer(peer);
        return wp.Rule switch
        {
            XonoticGodot.Common.Gameplay.Waypoints.SpriteRule.Teamplay => wp.Team == 0 || wp.Team == (int)peer.Team,
            XonoticGodot.Common.Gameplay.Waypoints.SpriteRule.Spectator => peer.IsObserver,
            _ => true, // SPRITERULE_DEFAULT: visible to everyone
        };
    }

    /// <summary>
    /// Drop a player from the server's per-player networking state — the bot-removal counterpart to
    /// <see cref="OnPeerDisconnected"/> (which only fires for real ENet peers). A removed bot leaves
    /// <see cref="GameWorld"/>'s client list (so the next snapshot delta despawns it on clients); this clears the
    /// id/antilag/baseline maps so they don't retain the dead player. Safe to call for any player (no-op if absent).
    /// </summary>
    public void ForgetPlayer(Player p)
    {
        _playerNetIds.Remove(p);
        _byPlayer.Remove(p);
        _antilag.Remove(p);
        _lastSnapOrigin.Remove(p);
        _world.Commands.ForgetPlayer(p); // per-client replicated-cvar/autoswitch tables
    }

    /// <summary>Send a reject reason and drop the peer (build mismatch / failed auth / full).</summary>
    private void Reject(int peerId, string reason)
    {
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeReject);
        _scratchWriter.WriteString(reason);
        _transport.Send(peerId, _scratchWriter.WrittenSpan, reliable: true);
        _transport.Disconnect(peerId);
    }

    private void HandleHandshake(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || st.Accepted)
            return;

        uint clientParity = r.ReadULong();
        st.PendingName = r.ReadString();
        var publicKey = r.ReadBytes(r.ReadUShort()).ToArray(); // the client's identity public key (SPKI)
        uint serverParity = NetProtocol.BuildParity();

        if (clientParity != serverParity)
        {
            // build-parity gate: mismatched content/protocol — reject with a reason and drop.
            Reject(peerId, $"build mismatch (server 0x{serverParity:X8}, client 0x{clientParity:X8})");
            GD.Print($"[ServerNet] peer {peerId} REJECTED: parity {clientParity:X8} != {serverParity:X8}.");
            return;
        }

        // build OK — issue a session-auth challenge (SessionAuth). We admit the player only once it proves
        // ownership of its public identity key by signing this challenge (replaces d0_blind_id, ADR-0011).
        st.PendingPublicKey = publicKey;
        st.AuthChallenge = ServerChallenge.NewChallenge();
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeChallenge);
        _scratchWriter.WriteUShort(st.AuthChallenge.Length);
        _scratchWriter.WriteBytes(st.AuthChallenge);
        _transport.Send(peerId, _scratchWriter.WrittenSpan, reliable: true);
    }

    private void HandleAuth(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || st.Accepted || st.AuthChallenge is null || st.PendingPublicKey is null)
            return;

        var signature = r.ReadBytes(r.ReadUShort()).ToArray();
        if (r.BadRead || !ServerChallenge.Verify(st.PendingPublicKey, st.AuthChallenge, signature))
        {
            Reject(peerId, "identity verification failed");
            GD.Print($"[ServerNet] peer {peerId} REJECTED: bad auth signature.");
            return;
        }
        st.IdentityFingerprint = PlayerIdentity.ComputeFingerprint(st.PendingPublicKey);
        st.AuthChallenge = null;

        // admit: create the player on the world's client roster (spawns it via PutClientInServer).
        ClientManager.ClientInfo info = _world.Clients.ClientConnect(isBot: false, netName: string.IsNullOrEmpty(st.PendingName) ? null : st.PendingName);
        st.Player = info.Player;
        // QC .crypto_idfp: the stable identity used to key race/CTS records (anonymous bots keep "").
        info.Player.PersistentId = st.IdentityFingerprint;
        st.NetId = NetIdFor(info.Player);
        st.Accepted = true;
        _byPlayer[info.Player] = st;

        // the client's stable network entity id is a small allocated id (NOT the large ENet peer id). The owner
        // uses it to pick + exclude its own state from the snapshot.
        _scratchWriter.Reset();
        _scratchWriter.WriteByte((byte)NetControl.HandshakeAccept);
        _scratchWriter.WriteUShort(st.NetId);                     // your net entity id
        _scratchWriter.WriteFloat(1f / SimulationLoopTicRate);     // server tick RATE in Hz (1/dt) for client timing
        _scratchWriter.WriteString(_serverName);                   // server display name (for the client UI)
        _transport.Send(peerId, _scratchWriter.WrittenSpan, reliable: true);
        GD.Print($"[ServerNet] peer {peerId} accepted as '{info.Player.NetName}' (netId {peerId}, id {st.IdentityFingerprint[..8]}).");
    }

    private void HandleInputFrame(int peerId, ref BitReader r)
    {
        if (!_peers.TryGetValue(peerId, out PeerState? st) || !st.Accepted || st.Player is null)
            return;

        // The client leads its input frame with the newest snapshot it decoded; that closes the delta loop so
        // the server deltas the next snapshot against a baseline this client provably holds.
        st.SnapHistory.Ack((ushort)r.ReadUShort());

        // ...then the server-time stamp of the snapshot the client is responding to (its LatestServerTime).
        // Measured RTT = receive-time − that stamp — the faithful port of DP's
        // host_client->ping = cmd.receivetime - cmd.time (sv_user.c:847), in the SERVER clock domain on both
        // ends (the snapshot left the server at echoedTime and this input came back now), so there is no
        // client/server clock-offset error. Exponentially smoothed (DP networks a similarly-filtered ping).
        float echoedServerTime = r.ReadFloat();
        if (!r.BadRead && echoedServerTime > 0f)
        {
            float rtt = _world.Time - echoedServerTime;
            if (rtt < 0f) rtt = 0f;                 // clock/order skew → no rewind (antilag_getlag lag<0.001 → 0)
            if (!st.HasRtt) { st.MeasuredRtt = rtt; st.HasRtt = true; }
            else st.MeasuredRtt += (rtt - st.MeasuredRtt) * RttSmoothing; // 1st-order low-pass
        }

        // Per-frame (variable-dt) input mode for this client (see PeerState.PerFrameInput). Client-authoritative
        // for its own input cadence; the server still clamps each command's dt + caps the per-tick budget when it
        // drains. Position on the wire matches ClientNet.SendInput: snapAck, echoedServerTime, THIS flag, count.
        st.PerFrameInput = r.ReadByte() != 0;

        int count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            InputCommand cmd = InputCommand.Deserialize(ref r);
            if (r.BadRead)
                break;
            // dedup by seq: the client sends a redundant tail; only enqueue commands we haven't processed and
            // haven't already queued (keep the queue monotonic in seq).
            if (cmd.Seq > st.LastProcessedSeq && cmd.Seq > st.HighestQueuedSeq)
            {
                st.Pending.Enqueue(cmd);
                st.HighestQueuedSeq = cmd.Seq;
            }
        }
    }

    // =====================================================================================
    //  Input feed (GameWorld.InputProvider)
    // =====================================================================================

    /// <summary>
    /// Supply the next authoritative movement input for <paramref name="p"/> this tick (QC the received move
    /// command). Pops the oldest un-applied <see cref="InputCommand"/> from the client's queue and converts it
    /// to a <see cref="MovementInput"/>; if the queue has starved (packet loss / client hitch) it repeats the
    /// last input so held movement keys keep the player moving rather than stuttering to a stop.
    /// </summary>
    private IMovementInput ProvideInput(Player p)
    {
        if (!_byPlayer.TryGetValue(p, out PeerState? st))
            return ZeroInput;

        // QC applies exactly ONE usercmd per player per server frame; movement (SV_PlayerPhysics) and the weapon
        // driver (W_WeaponFrame) both read that same command. GameWorld calls InputProvider twice per tick — once
        // for movement, once for the weapon frame — so resolve/dequeue only on the FIRST call of a sim tick and
        // return the cached command on the second. (_world.Time is constant within a sim tick and advances each
        // tick, so it keys the per-tick cache.) Otherwise the queue drains at 2x and LastProcessedSeq over-acks
        // inputs whose movement never ran, corrupting predict/reconcile + antilag under input-queue jitter.
        float now = _world.Time;
        if (st.TickInput is not null && st.TickInputTime == now)
            return st.TickInput;

        // Per-frame (variable-dt) client: drain ALL pending commands this tick into the per-command movement batch
        // + a merged once-per-tick input, instead of the legacy one-command-per-tick below.
        if (st.PerFrameInput)
            return ProvideInputPerFrame(st, p, now);

        // LEGACY queue bound (DP sv_clmovement_inputtimeout). Producer and consumer both pace at exactly
        // 72 Hz, so the queue length never shrinks on its own — a client hitch's burst (up to 18 commands
        // per long frame, while the sim's B3 soft-cap + spiral guard run/drop FEWER catch-up ticks) would
        // otherwise become PERMANENT input latency (fire/jump executing queue-length ticks late for the
        // rest of the match, compounding with every further hitch — the felt "projectile leaves 0.5 s after
        // the click"). Past the abnormal-depth trigger, discard the OLDEST commands down to a small jitter
        // floor: dropped seqs still ack (client prediction stops replaying them — both ends agree the
        // movement never happened, the same brief reconcile warp DP shows), and their one-shot impulses
        // still dispatch so a hitch can't eat a weapon switch.
        if (st.Pending.Count > InputQueuePolicy.MaxLegacyQueuedCommands)
        {
            _droppedImpulseScratch.Clear();
            int before = st.Pending.Count;
            uint highestDropped = InputQueuePolicy.Trim(st.Pending,
                InputQueuePolicy.MaxLegacyQueuedCommands, InputQueuePolicy.LegacyTrimResidual,
                _droppedImpulseScratch);
            if (highestDropped > st.LastProcessedSeq)
                st.LastProcessedSeq = highestDropped;
            if (!p.IsObserver)
                foreach (int imp in _droppedImpulseScratch)
                    _world.Commands.Execute($"impulse {imp}", isServerConsole: false, caller: p);
            if (Log.WillTrace)
                Log.Trace($"[ServerNet] input backlog trimmed for '{p.NetName}': {before} -> {st.Pending.Count} " +
                          "(client hitch burst; surplus consumed unsimulated)");
            Prof.Event($"net: input backlog trimmed {before} -> {st.Pending.Count} ({p.NetName})");
        }

        InputCommand cmd;
        if (st.Pending.Count > 0)
        {
            cmd = st.Pending.Dequeue();
            st.Last = cmd;
            st.HasLast = true;
            st.LastProcessedSeq = cmd.Seq;     // ack cursor: highest input we've applied

            // C2S impulse (QC usercmd.impulse → ImpulseCommands): a fresh command may carry a one-shot weapon
            // switch/reload number. Dispatch it ONCE, here on dequeue (this is the FIRST ProvideInput call of the
            // tick — the second call returns the cached TickInput above, so it never re-runs), through the GATED
            // impulse command path (Commands.CmdImpulse → DispatchImpulse keeps the game_stopped / timeout /
            // round-not-started guards faithful) → WeaponImpulses.Handle. Then ZERO it on st.Last so the
            // starve-repeat below (which reuses st.Last when the queue drains) doesn't re-fire it every starved
            // tick — exactly QC's CS(this).impulse = 0 (impulse.qc:377). The Seq-dedup already drops the
            // redundant input tail, so this processes the impulse exactly once.
            //
            // T44: for an OBSERVER (spectator free-flight) the impulse is NOT a weapon command — the speed-step
            // impulses (1-19/200-209/220-229) drive the spectator-speed ladder in PlayerPhysics.SpectatorControl,
            // which reads it off the MovementInput. So skip the weapon dispatch for observers and let the impulse
            // ride into ToMovementInput(cmd) below (cmd keeps it; only st.Last is cleared). In QC the spectator
            // branch handles the impulse BEFORE weapon impulses for non-players (physics.qc:58-62), and weapon
            // impulses don't apply to a spectator anyway. We still ZERO st.Last so the starve-repeat doesn't
            // re-step the ladder every starved tick (QC's CS(this).impulse = 0 after spectator_control consumes it).
            if (cmd.Impulse != 0)
            {
                if (!p.IsObserver)
                    _world.Commands.Execute($"impulse {cmd.Impulse}", isServerConsole: false, caller: p);
                InputCommand last = st.Last;
                last.Impulse = 0;
                st.Last = last;
            }
        }
        else if (st.HasLast)
        {
            cmd = st.Last;                      // starved: repeat the last input (held keys keep moving)
        }
        else
        {
            st.TickInput = ZeroInput;
            st.TickInputTime = now;
            return ZeroInput;
        }

        IMovementInput resolved = ToMovementInput(cmd);

        // +use rising edge (QC PlayerUseKey, client.qc): board/exit a vehicle on the RELEASED->PRESSED edge of
        // the +use button. PlayerUseKey is edge-driven in DP (fired once per press), so detecting the edge here
        // — once per sim tick, since this is the FIRST ProvideInput call of the tick (the cached-return above
        // guards the W_WeaponFrame second call) — keeps a HELD +use from boarding then immediately exiting then
        // re-boarding. Routed to the server-authoritative VehicleBoarding.UseKey (Common-side, headless).
        bool useDown = resolved.ButtonUse;
        if (useDown && !st.UsePrevDown)
            VehicleBoarding.UseKey(p);
        st.UsePrevDown = useDown;

        st.TickInput = resolved;
        st.TickInputTime = now;
        return resolved;
    }

    // Per-frame (variable-dt) guardrails. The client already clamps each command's dt to the SAME bounds before
    // stamping it (NetGame.MinInputDt/MaxInputDt) so re-clamping here is a no-op for an honest client and keeps
    // client prediction in lockstep; it only tames a buggy/cheating client. The per-tick budget caps how much
    // simulated time one server tick may drain (drain the rest next tick) so a flooded queue can't fast-forward.
    private const float PerFrameMinCommandDt = 0.0005f;
    private const float PerFrameMaxCommandDt = 0.05f;
    private const float PerFrameTickBudget = 0.25f;

    /// <summary>
    /// Per-frame (variable-dt) input drain — DP's process-queued-client-moves. Dequeues ALL pending commands for
    /// this tick (up to <see cref="PerFrameTickBudget"/> simulated seconds), building <see cref="PeerState.TickBatch"/>
    /// (one movement input per command, integrated with its own clamped dt by OnClientMove) AND the MERGED
    /// once-per-tick input the single-call readers use: the LATEST command's angles/wish-move with the OR of every
    /// drained command's buttons (so a fire pressed in ANY sub-frame reaches this tick's weapon driver). Impulses
    /// dispatch per command (rapid weapon switches); the +use edge + starve-repeat mirror the legacy path.
    /// </summary>
    private IMovementInput ProvideInputPerFrame(PeerState st, Player p, float now)
    {
        st.TickBatch.Clear();
        InputButtons mergedButtons = InputButtons.None;
        InputCommand last = default;
        bool any = false;
        float consumed = 0f;

        while (st.Pending.Count > 0 && consumed < PerFrameTickBudget)
        {
            InputCommand cmd = st.Pending.Dequeue();
            // Clamp this command's dt server-side (untrusted client); the client clamps to the same bounds, so an
            // honest command is unchanged → prediction stays in lockstep.
            float dt = cmd.DeltaTime;
            dt = dt < PerFrameMinCommandDt ? PerFrameMinCommandDt : (dt > PerFrameMaxCommandDt ? PerFrameMaxCommandDt : dt);
            cmd.DeltaTime = dt;
            consumed += dt;

            st.Last = cmd;
            st.HasLast = true;
            st.LastProcessedSeq = cmd.Seq;

            // Impulse: dispatch once per command (the enqueue Seq-dedup already drops the redundant tail), then zero
            // it on st.Last so the starve-repeat can't re-fire it — exactly the legacy path, per command. Observers
            // keep the impulse on the dequeued cmd (it rides the batch entry into SpectatorControl's speed ladder).
            if (cmd.Impulse != 0)
            {
                if (!p.IsObserver)
                    _world.Commands.Execute($"impulse {cmd.Impulse}", isServerConsole: false, caller: p);
                InputCommand z = st.Last; z.Impulse = 0; st.Last = z;
            }

            st.TickBatch.Add(ToMovementInput(cmd));
            mergedButtons |= cmd.TypedButtons;
            last = cmd;
            any = true;
        }

        if (!any)
        {
            // Starved (packet loss / client hitch): repeat the last command so held keys keep the player moving —
            // a single batch entry worth one tick of dt.
            if (st.HasLast)
            {
                InputCommand rep = st.Last;
                rep.DeltaTime = SimulationLoopTicRate;
                st.TickBatch.Add(ToMovementInput(rep));
                mergedButtons = rep.TypedButtons;
                last = rep;
            }
            else
            {
                st.TickInput = ZeroInput;
                st.TickInputTime = now;
                return ZeroInput; // empty batch → OnClientMove uses the single ZeroInput
            }
        }

        // MERGED once-per-tick input: the latest command's angles/wish-move (freshest aim + intent) carrying the OR
        // of every drained command's buttons, at the tick frametime (the weapon driver/regen run once per tick).
        // Drives p.Angles, the dead-respawn buttons, the vehicle input and W_WeaponFrame.
        InputCommand mergedCmd = last;
        mergedCmd.Buttons = (int)mergedButtons;
        mergedCmd.Impulse = 0;
        mergedCmd.DeltaTime = SimulationLoopTicRate;
        IMovementInput merged = ToMovementInput(mergedCmd);

        // +use rising edge from the merged state (once per tick, like the legacy path).
        bool useDown = merged.ButtonUse;
        if (useDown && !st.UsePrevDown)
            VehicleBoarding.UseKey(p);
        st.UsePrevDown = useDown;

        st.TickInput = merged;
        st.TickInputTime = now;
        return merged;
    }

    /// <summary>
    /// The per-command movement batch drained for <paramref name="p"/> THIS tick (per-frame mode) — one entry per
    /// client render frame, each integrated with its own dt by OnClientMove. Null in legacy mode or when not
    /// drained this tick (→ OnClientMove runs the single InputProvider command). Built by
    /// <see cref="ProvideInputPerFrame"/>, which OnClientMove triggers via its first InputProvider call.
    /// </summary>
    private IReadOnlyList<IMovementInput>? TickMovementBatch(Player p)
    {
        if (!_byPlayer.TryGetValue(p, out PeerState? st))
            return null;
        if (!st.PerFrameInput || st.TickInputTime != _world.Time)
            return null;
        return st.TickBatch.Count > 0 ? st.TickBatch : null;
    }

    // Boxed once: typed as the interface so the hot ProvideInput paths return/store the same object instead of
    // re-boxing the struct on every assignment to an IMovementInput-typed field/return.
    private static readonly IMovementInput ZeroInput = new MovementInput { FrameTime = SimulationLoopTicRate };

    // Scratch for the legacy input-queue trim (one-shot impulses carried by dropped commands); ProvideInput
    // runs on the sim thread only, so one shared buffer is safe.
    private readonly List<int> _droppedImpulseScratch = new(4);

    private static MovementInput ToMovementInput(in InputCommand c)
    {
        InputButtons b = c.TypedButtons;
        return new MovementInput
        {
            ViewAngles = c.ViewAngles,
            // Rescale the normalized (±1) wish-move to wish-velocity units via the Darkplaces client input speeds
            // (cl_forwardspeed=400 / cl_sidespeed=350 / cl_upspeed=400, cl_input.c) — NOT the live sv_maxspeed.
            // MUST use the SAME scaling the client predictor (EntityMovementStep) uses or prediction and authority
            // disagree; PlayerPhysics clamps wishspeed to live MaxSpeed downstream, so maxspeed>360 isn't capped.
            MoveValues = WishMoveScaling.Scale(c.Forward, c.Side, c.Up),
            FrameTime = c.DeltaTime > 0f ? c.DeltaTime : SimulationLoopTicRate,
            ButtonJump = (b & InputButtons.Jump) != 0,
            ButtonCrouch = (b & InputButtons.Crouch) != 0,
            ButtonAttack1 = (b & InputButtons.Attack) != 0,
            ButtonAttack2 = (b & InputButtons.Attack2) != 0,
            ButtonUse = (b & InputButtons.Use) != 0,
            // Carry the one-shot impulse (QC CS(this).impulse) so the spectator free-flight speed ladder
            // (PlayerPhysics.SpectatorControl) sees it. For a live PLAYER the impulse was already dispatched as
            // a weapon command in ProvideInput; PlayerPhysics' spectator branch is gated on IsObserver, so this
            // value is simply ignored by the player movement path (no double-processing). For an OBSERVER
            // ProvideInput skips that weapon dispatch, so the speed-step impulse (1-19/200-229) survives here.
            Impulse = c.Impulse,
        };
    }

    // =====================================================================================
    //  Snapshot broadcast
    // =====================================================================================

    /// <summary>
    /// Send one snapshot per accepted client. Each snapshot carries: the server time, the recipient's acked
    /// input seq, their own full-precision authoritative state (the reconcile seed) + the movevars block when the
    /// physics changed, then a <b>delta-compressed</b> entity section (only spawned/changed/removed entities
    /// since this client's last-acked snapshot — <see cref="ServerSnapshotHistory"/>). The owner's own entity is
    /// excluded (the client predicts it). The entity set is built once per tick + the teleport bit detected.
    /// </summary>
    private void BroadcastSnapshots()
    {
        float now = _world.Time;

        // antilag config for this frame (antilag.qc / world.qc EndFrame). g_antilag default 2 (xonotic-server.cfg)
        // — when the cvar is unset GetString is empty, so we keep the stock-enabled default; an explicit 0
        // disables server-side rewind (antilag_getlag). g_antilag_nudge (default 0) tunes the record timestamp.
        string antilag = _world.Services.Cvars.GetString("g_antilag");
        _antilagMode = string.IsNullOrEmpty(antilag) ? 2 : (int)_world.Services.Cvars.GetFloat("g_antilag");
        _antilagNudge = _world.Services.Cvars.GetFloat("g_antilag_nudge");

        BuildEntitySet(now);

        // movevars (the prediction-relevant sv_* set): recomputed once; sent per client only when its hash changes.
        _moveVars = MoveVarsBlock.Capture(_world.Services.Cvars);
        _moveVarsHash = MoveVarsBlock.Hash(_moveVars);

        // scoreboard: mirror the authoritative team totals into the score table so its Version reflects team
        // changes too, then snapshot every player's networked columns + the team totals once (same to all).
        BuildScoreboard();

        _snapshotSeq++;
        if (_snapshotSeq == 0) _snapshotSeq = 1; // 0 is the "no baseline" sentinel

        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null)
                continue;

            Player owner = st.Player;
            _snapshotWriter.Reset();
            _snapshotWriter.WriteByte((byte)NetControl.Snapshot);
            _snapshotWriter.WriteFloat(now);
            _snapshotWriter.WriteULong(st.LastProcessedSeq);       // ack: the last input we ran for this client

            // owner-replicated state (full precision — aim/position fidelity matters for prediction).
            _snapshotWriter.WriteUShort(st.NetId);                 // owner net id (echo, lets the client confirm)
            WriteOwnerState(_snapshotWriter, owner, st);

            // movevars: send the block only when this client's physics is stale (steady-state = one bool).
            bool sendMoveVars = st.LastMoveVarsHash != _moveVarsHash;
            _snapshotWriter.WriteBool(sendMoveVars);
            if (sendMoveVars)
            {
                MoveVarsBlock.Serialize(_snapshotWriter, _moveVars);
                st.LastMoveVarsHash = _moveVarsHash;
            }

            // [v7] per-peer preset-RESOLVED physics (T54 — QC Physics_ClientOption, player.qc:18-42): when this
            // client's g_physics_clientselect resolution deviates from the global block, send the resolved
            // vector (hash-gated like the movevars block); when the deviation ENDS (preset back to default /
            // clientselect turned off) send ONE count-0 block so the client clears its PredictionOverride.
            // Steady state either way is a single false bool. The same vector drives the server sim via
            // MovementParameters.PresetProvider (ResolvePresetVector), so authority and prediction agree.
            {
                float[]? resolved = ResolvePresetVector(owner);
                uint resolvedHash = resolved is null ? 0u : MoveVarsBlock.Hash(resolved);
                bool sendResolved = st.LastResolvedVarsHash != resolvedHash;
                _snapshotWriter.WriteBool(sendResolved);
                if (sendResolved)
                {
                    MoveVarsBlock.Serialize(_snapshotWriter, resolved ?? System.Array.Empty<float>());
                    st.LastResolvedVarsHash = resolvedHash;
                    if (_world.Services.Cvars.GetFloat("developer") != 0f)
                        GD.Print($"[ServerNet] resolved physics block → {owner.NetName}: "
                                 + (resolved is null ? "cleared (back to global)" : $"hash 0x{resolvedHash:X8}"));
                }
            }

            // ScoreInfo (the per-mode label/flag layout + gametype/teamplay): send the block only when the layout
            // changed since this client last got it — a gametype/mode switch (else one bool). MUST precede the
            // scoreboard block so the client applies the labels BEFORE deserializing the per-player columns, or the
            // first post-switch scoreboard frame would be dropped on a layout-hash mismatch (QC sends ScoreInfo on
            // its own linked entity; here both ride the snapshot, so order is the guarantee). Always sent on the
            // first snapshot to a client (SentScoreInfo=false) so a remote --connect client gets the layout even if
            // it equals the default hash.
            bool sendScoreInfo = !st.SentScoreInfo || st.LastScoreInfoHash != _scoreInfoHash;
            _snapshotWriter.WriteBool(sendScoreInfo);
            if (sendScoreInfo)
            {
                XonoticGodot.Net.ScoreInfoBlock.Serialize(_snapshotWriter);
                st.LastScoreInfoHash = _scoreInfoHash;
                st.SentScoreInfo = true;
            }

            // scoreboard: send the block only when a score changed since this client last got it (one bool otherwise).
            bool sendScores = st.LastScoreVersion != _scoreVersion;
            _snapshotWriter.WriteBool(sendScores);
            if (sendScores)
            {
                XonoticGodot.Net.ScoreboardBlock.Serialize(_snapshotWriter, _scoreRows, _scoreTeams);
                st.LastScoreVersion = _scoreVersion;
            }

            // Gametype status (T53): the per-mode round/objective HUD stats — QC STAT(REDALIVE..PINKALIVE)
            // (CA/FT), STAT(OBJECTIVE_STATUS) (the KH key pack), eliminatedPlayers (scoreboard grey-out) and
            // survivalStatuses (role + hunter disclosure). The block is PERSONALIZED per recipient (the KH
            // "31 = self" slot, the SURV hunter visibility), so serialize per peer into a scratch and hash-gate
            // (steady-state cost = one bool; QC's equivalent is stat delta-compression + linked-entity SendFlags).
            _modeStatusScratch.Reset();
            bool haveModeStatus = XonoticGodot.Net.GametypeStatusBlock.Capture(
                _modeStatusScratch, _world.GameType, owner, _world.Clients.Players, NetIdFor,
                roundStarted: _world.Rounds is { IsRoundStarted: true });
            uint modeStatusHash = haveModeStatus ? XonoticGodot.Net.GametypeStatusBlock.Hash(_modeStatusScratch.WrittenSpan) : 0u;
            bool sendModeStatus = haveModeStatus && st.LastModeStatusHash != modeStatusHash;
            _snapshotWriter.WriteBool(sendModeStatus);
            if (sendModeStatus)
            {
                _snapshotWriter.WriteBytes(_modeStatusScratch.WrittenSpan);
                st.LastModeStatusHash = modeStatusHash;
            }

            // [T57 accuracy] — the owner's per-weapon accuracy byte array (QC ENT_CLIENT_ACCURACY, owner-only)
            // is a PER-OWNER payload, so it is written/read at the END of the owner block (WriteOwnerState's
            // append-at-END slot), NOT here in the broadcast entity-shared region.

            // delta-compressed entity section (everyone but the recipient's own entity).
            st.SnapHistory.EncodeSnapshot(_snapshotWriter, _entityScratch, _snapshotSeq, excludeEntNum: st.NetId);

            // Snapshots are normally unreliable (latest-wins, loss-tolerant), but the FIRST frame to a client is a
            // full baseline of every networked entity (all of the map's static items at once) and exceeds the
            // unreliable MTU until the client's first ack lets the server delta-compress against it. An oversized
            // unreliable packet is lossy (fragmented, any lost fragment drops the whole frame) — so a dropped
            // baseline would leave the client missing static items until they next change (rare). Promote any
            // over-MTU snapshot to the reliable channel (ENet fragments it losslessly); the client dispatches by
            // the leading control byte regardless of channel, so this is transparent. Steady-state deltas stay
            // unreliable, so this adds no head-of-line blocking once the first ack shrinks the frame.
            ReadOnlySpan<byte> snapshot = _snapshotWriter.WrittenSpan;
            _transport.Send(st.PeerId, snapshot, reliable: snapshot.Length > NetProtocol.MaxUnreliablePayload);
        }
    }

    /// <summary>The memoized "&lt;classname&gt; &lt;netname&gt;" catalog key a model-less projectile networks in its
    /// (otherwise unused) model field so the client's <c>ProjectileCatalog</c> picks the right type. Cached per
    /// (classname, netname) so the per-tick entity build allocates no interpolated string (3.2-4).</summary>
    private string ProjectileCatalogKey(string className, string netName)
    {
        if (string.IsNullOrEmpty(netName))
            return className;
        var key = (className, netName);
        if (!_projKeyCache.TryGetValue(key, out string? combined))
        {
            combined = $"{className} {netName}";
            _projKeyCache[key] = combined;
        }
        return combined;
    }

    /// <summary>
    /// Build this tick's networked entity set (keyed by net id) from the player roster, stamping the teleport
    /// bit on any entity whose one-tick origin jump exceeds <see cref="TeleportTickDistance"/> (a teleport or
    /// respawn, which the client must snap to rather than lerp across). Reused dictionary — no per-tick alloc.
    /// </summary>
    private void BuildEntitySet(float now)
    {
        _entityScratch.Clear();

        // QC world.qc:EndFrame records antilag history at `altime` (time + frametime*(1+g_antilag_nudge)), NOT at
        // the bare current time — so the ring aligns with the time the client will see this frame. Compute it once.
        float altime = LagCompensation.RecordTime(now, SimulationLoopTicRate, _antilagNudge);

        IReadOnlyList<Player> players = _world.Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];

            // An OBSERVER (connected but not yet joined) has no live body — it's never spawned, so it would
            // otherwise be networked as a ghost player at the world origin. QC only puts live players in the
            // CSQCModel player stream; skip observers here so no client sees a phantom at (0,0,0).
            if (p.IsObserver)
                continue;

            int netId = NetIdFor(p); // humans AND bots get a stable id (bots aren't in _byPlayer)

            // Detect a teleport/respawn (a one-tick origin jump past the threshold) ONCE, up front — it both flags
            // the snapshot (so the client snaps instead of lerping) and, faithful to PutPlayerInServer's
            // antilag_clear(this, CS(this)) (client.qc:858), wipes this player's lag-comp ring so the next shot
            // can't rewind a freshly-spawned/teleported player back to their corpse for ~0.4s.
            bool teleported = _lastSnapOrigin.TryGetValue(p, out NVec3 prev)
                              && (p.Origin - prev).Length() > TeleportTickDistance;

            // record the position history for lag compensation (antilag_record(it, CS(it), altime)).
            if (!_antilag.TryGetValue(p, out AntilagBuffer? hist))
            {
                hist = new AntilagBuffer();
                _antilag[p] = hist;
            }
            if (teleported)
            {
                hist.Clear();                 // antilag_clear: drop pre-teleport history before recording the new pos
                _lastSnapOrigin.Remove(p);    // forget the pre-jump origin so the post-jump baseline is this frame's
            }
            hist.Store(altime, p.Origin);

            var s = new NetEntityState
            {
                EntNum = netId,
                Kind = NetEntityKind.Player,
                ModelIndex = p.ModelIndex,
                Frame = (int)p.Frame,
                Skin = (int)p.Skin,
                Origin = p.Origin,
                Angles = p.Angles,
                // The remote player's velocity drives the client-side locomotion pick (LocomotionBlend.SelectLegs
                // → run/walk/idle) for the skeletal PlayerModel — exactly as CSQC's animdecide derives walk/run
                // from the networked velocity. Without it every remote velocity reads 0 and (combined with the
                // OnGround flag below) the bot is frozen in a single pose. (QC csqcmodel networks .velocity.)
                Velocity = p.Velocity,
                Health = (int)p.Health,
                Armor = (int)p.ArmorValue, // [T68] networked entcs armor → teammate shownames armor sub-bar
                Colormap = (int)p.Team,
                Weapon = p.ActiveWeaponId, // renders the remote player's held weapon (QC wepent)
                Model = p.Model,           // QC .model (playermodel) — the client loads the skeletal IQM by name
                Flags = (p.OnGround ? NetEntityFlags.OnGround : 0)
                      | (p.IsDead ? NetEntityFlags.Dead : 0)
                      | (p.IsDucked ? NetEntityFlags.Crouched : 0), // QC FL_DUCKED → remote crouch anim/hull
                // [T41/T46] the Feedback stats the client diffs/draws (view.qc HitSound + objective rings):
                //   - HitDamageDealtTotal: cumulative damage dealt — the client diffs it to fire the hit sound.
                //   - NadeTimer: 0..1 held-nade charge — drives the nade objective ring.
                //   - ReviveProgress: 0..1 Freeze-Tag thaw — drives the thaw ring (mirrored from FrozenState).
                // CaptureProgress is intentionally left 0: the stat is networked + rendered but no gametype sets
                // it (matches Base QC — an unfinished CTF/Assault ring producer; see NetEntity.cs).
                HitDamageDealtTotal = p.HitsoundDamageDealtTotal,
                NadeTimer = p.NadeTimer,
                ReviveProgress = p.ReviveProgress,
            };
            if (teleported)
                s.Flags |= NetEntityFlags.Teleported;
            _lastSnapOrigin[p] = p.Origin;

            _entityScratch[netId] = s;
        }

        // non-player networked entities: projectiles, items, gibs, monsters/turrets/vehicles — everything with a
        // model the client must render (CSQCProjectile + the general CSQC entity stream). Keyed in a high net-id
        // range so they never collide with the small ENet peer ids; inline brush models ("*N") are skipped (the
        // client renders those from the map). Delta-compression makes a static item nearly free after its spawn.
        IReadOnlyList<Entity> all = _world.Services.EntityTable.All;
        for (int i = 0; i < all.Count; i++)
        {
            Entity e = all[i];
            if (e is null || e.IsFreed || e is Player)
                continue;
            if (!string.IsNullOrEmpty(e.Model) && e.Model.StartsWith('*'))
                continue;                          // inline brush model — drawn by the map, not networked
            NetEntityKind kind = Classify(e);
            if (kind == NetEntityKind.None)
                continue;
            // Projectiles carry NO server-side .model (QC sets the model client-side via CSQCProjectile); the
            // client renders them procedurally (ProjectileRenderer + ProjectileCatalog). So a projectile networks
            // even with an empty model — only NON-projectiles are dropped for having nothing to draw.
            if (kind != NetEntityKind.Projectile && e.ModelIndex <= 0 && string.IsNullOrEmpty(e.Model))
                continue;                          // nothing to render

            // Give a model-less projectile a catalog key so the client picks the right type (rocket/grenade/
            // electro/…) instead of the Generic fallback: send its classname + netname in the (otherwise unused)
            // model field. ProjectileRenderer ignores the model geometry, so this only feeds ProjectileCatalog.
            string netModel = e.Model;
            if (kind == NetEntityKind.Projectile && string.IsNullOrEmpty(netModel))
                netModel = ProjectileCatalogKey(e.ClassName, e.NetName);

            int netId = EntityNetBase + e.Index;
            if (netId > ushort.MaxValue)
                continue;
            _entityScratch[netId] = new NetEntityState
            {
                EntNum = netId,
                Kind = kind,
                ModelIndex = e.ModelIndex,
                Model = netModel,                  // QC .model (or, for a projectile, its catalog key) — resolved by name
                Frame = (int)e.Frame,
                Skin = (int)e.Skin,
                Origin = e.Origin,
                Angles = e.Angles,
                Velocity = e.Velocity,             // static entities keep 0 → the delta never sends it
                Effects = e.Effects,
                Colormap = (int)e.Team,
                Health = (int)e.Health,
                Owner = (e.Owner is Player op && _byPlayer.TryGetValue(op, out PeerState? ops)) ? ops.PeerId : 0,
                // QC ItemStatus bits (items.qc). ITS_EXPIRING: a loot item in its despawn-fx window — set the
                // frame it flips so the client starts the despawn animation. ITS_ANIMATE1/2: the item's static
                // bob+spin class (set once at spawn from the def) so the client renders the float/rotation and
                // lifts the model clear of the floor. Other non-player kinds leave these clear → Flags stays None.
                // ITS_AVAILABLE: a picked-up item awaiting respawn keeps its model (Item_Show(e,0) only clears
                // availability + solidity, it does NOT null the model) — networked as ItemGhost so the client
                // renders it faded instead of opaque. Item-only: non-items never clear ItemAvailable (default true).
                Flags = (e.ItemExpiringFx ? NetEntityFlags.ItemExpiring : NetEntityFlags.None)
                        | (e.ItemAnimate == 1 ? NetEntityFlags.ItemAnimate1 : NetEntityFlags.None)
                        | (e.ItemAnimate == 2 ? NetEntityFlags.ItemAnimate2 : NetEntityFlags.None)
                        | (kind == NetEntityKind.Item && !e.ItemAvailable ? NetEntityFlags.ItemGhost : NetEntityFlags.None),
            };
        }

        // Antilag breadth (world.qc:EndFrame): besides players, record the position history of every MONSTER and
        // every NADE so they can also be rewound at fire time (antilag_takeback_all/restore_all walk g_monsters +
        // g_projectiles[classname=="nade"] too). Done as its own pass over `all` because antilag-eligibility is
        // independent of render-eligibility — a monster/nade is recorded whether or not it made the snapshot set.
        RecordAntilagEntities(all, altime);
    }

    /// <summary>
    /// Port of the monster + nade half of the <c>world.qc:EndFrame</c> antilag record loop
    /// (<c>IL_EACH(g_monsters, …) { antilag_record(it, it, altime); }</c> and the <c>g_projectiles</c> pass
    /// gated on <c>it.classname == "nade"</c>). Stores each live monster/nade's origin at <paramref name="altime"/>
    /// into its own per-entity <see cref="AntilagBuffer"/>, allocating one on first sight; entities that have since
    /// freed are pruned so their rings don't leak. We have no intrusive lists (IL_EACH) yet, so we filter the dense
    /// entity table: a monster carries <see cref="EntFlags.Monster"/> (set by <c>MonsterAI.Setup</c>) — the same
    /// test Invasion uses — and a nade is <c>ClassName == "nade"</c> (the thrown-grenade projectile; the nades
    /// subsystem that spawns it is T11, so this is currently a no-op until those entities exist, but the rewind is
    /// in place for when they do).
    /// </summary>
    private void RecordAntilagEntities(IReadOnlyList<Entity> all, float altime)
    {
        for (int i = 0; i < all.Count; i++)
        {
            Entity e = all[i];
            if (e is null || e.IsFreed || e is Player)
                continue;
            if (!IsAntilagged(e))
                continue;

            if (!_antilagEntities.TryGetValue(e, out AntilagBuffer? hist))
            {
                hist = new AntilagBuffer();
                _antilagEntities[e] = hist;
            }
            // A monster/nade that teleported or (re)spawned in place gets its ring wiped first, mirroring the
            // player-side antilag_clear, so a shot can't rewind it to a pre-jump position. altime is strictly
            // newer than every stored stamp, so SampleAt(altime) returns the head (last recorded) origin to
            // diff against without a new API.
            if (hist.HasData && (e.Origin - hist.SampleAt(altime)).Length() > TeleportTickDistance)
                hist.Clear();
            hist.Store(altime, e.Origin);
        }

        // prune rings for monsters/nades that died/expired since last frame (freed, or no longer antilagged) so the
        // dictionary tracks only live entities — the QC equivalent is the entity simply leaving its intrusive list
        // on remove. Cheap over a small dict, so done every frame rather than gated on a (churn-blind) count check.
        if (_antilagEntities.Count > 0)
        {
            _antilagPruneScratch.Clear();
            foreach (Entity tracked in _antilagEntities.Keys)
                if (tracked.IsFreed || !IsAntilagged(tracked))
                    _antilagPruneScratch.Add(tracked);
            for (int i = 0; i < _antilagPruneScratch.Count; i++)
                _antilagEntities.Remove(_antilagPruneScratch[i]);
        }
    }

    /// <summary>Is this non-player entity one the antilag engine rewinds — a monster (FL_MONSTER, the same flag test
    /// <c>Invasion</c> uses) or a thrown nade (<c>classname == "nade"</c>)? (antilag.qc antilag_takeback_all set.)</summary>
    private static bool IsAntilagged(Entity e)
        => (e.Flags & EntFlags.Monster) != 0 || e.ClassName == "nade";

    /// <summary>
    /// Snapshot the score table for this tick (QC PlayerScore_SendEntity / TeamScore_SendEntity). Mirrors the
    /// authoritative per-team totals (which the gametype owns) into the shared score table so its
    /// <see cref="XonoticGodot.Common.Gameplay.Scoring.GameScores.Version"/> reflects team changes, then captures
    /// every player's networked columns + the active team totals once. The version stamp gates the per-client send.
    /// </summary>
    private void BuildScoreboard()
    {
        // mirror the active gametype/teamplay into the score table so the ScoreInfo block (QC
        // ScoreInfo_SendEntity's WriteRegistered(Gametypes) + teamplay) carries the live mode for the client's
        // column filter. Idempotent; writing the same NetName doesn't bump the layout generation (only label/flag
        // changes do), so it doesn't spuriously force a ScoreInfo resend.
        XonoticGodot.Common.Gameplay.Scoring.GameScores.Gametype = _world.GameType?.RegistryName ?? "dm";
        XonoticGodot.Common.Gameplay.Scoring.GameScores.Teamplay = _world.Teamplay is { IsTeamGame: true };

        // mirror gametype-owned team totals into the score table (bumps Version on a team-score change).
        if (_world.Teamplay is { IsTeamGame: true })
            foreach (int t in XonoticGodot.Common.Gameplay.Teams.Active(_world.Teamplay.TeamCount))
                XonoticGodot.Common.Gameplay.Scoring.GameScores.SetTeamScore(t, _world.Scores.TeamScore(t));

        _scoreVersion = XonoticGodot.Common.Gameplay.Scoring.GameScores.Version;
        _scoreInfoGen = XonoticGodot.Common.Gameplay.Scoring.GameScores.LayoutGeneration;
        _scoreInfoHash = XonoticGodot.Net.ScoreInfoBlock.Hash();

        _scoreRows.Clear();
        IReadOnlyList<Player> players = _world.Clients.Players;
        for (int i = 0; i < players.Count; i++)
        {
            Player p = players[i];
            // carry the entcs name/team slice so the client can label/group the row without an entcs stream
            // (the port has no entcs name source; the scoreboard would otherwise have an opaque net id).
            _scoreRows.Add(new XonoticGodot.Net.ScoreRowWire(NetIdFor(p),
                XonoticGodot.Common.Gameplay.Scoring.GameScores.CaptureColumns(p), p.NetName, (int)p.Team));
        }

        _scoreTeams.Clear();
        if (_world.Teamplay is { IsTeamGame: true })
            foreach (int t in XonoticGodot.Common.Gameplay.Teams.Active(_world.Teamplay.TeamCount))
                _scoreTeams.Add((t, _world.Scores.TeamScore(t)));
    }

    /// <summary>Net-id base for non-player entities, above the small ENet peer ids (so the two id spaces can't collide).</summary>
    private const int EntityNetBase = 16384;

    private static bool IsProjectileMoveType(MoveType mt)
        => mt is MoveType.Fly or MoveType.Toss or MoveType.FlyMissile or MoveType.Bounce or MoveType.BounceMissile;

    /// <summary>Classify a networked non-player entity into its CSQC render kind (None = not networkable).</summary>
    private static NetEntityKind Classify(Entity e)
    {
        string cn = e.ClassName.ToLowerInvariant();

        // Pickups (map items, weapon/ammo boxes, dropped loot) rest with MOVETYPE_TOSS but are NOT projectiles —
        // they must render their item MODEL via an EntityNode, not the procedural projectile visual. Match them by
        // their item classnames FIRST, because the projectile test below also keys on the shared FL_ITEM marker
        // (the port reuses EntFlags.Item as the FL_PROJECTILE stand-in) and would otherwise steal every Toss item.
        if (cn == "item" || cn.StartsWith("item_") || cn.StartsWith("weapon_") || cn.StartsWith("ammo_"))
            return NetEntityKind.Item;
        if (cn.Contains("gib") || cn.Contains("debris"))
            return NetEntityKind.Gib;

        // A live projectile: the port marks every projectile with EntFlags.Item (its FL_PROJECTILE stand-in — see
        // every weapon's spawn), so a projectile is that marker PLUS an owner PLUS a projectile movetype
        // (rocket/grenade/bolt/nade/mine/…). Same test as TurretAI.IsMissile; excludes ownerless map geometry.
        if ((e.Flags & EntFlags.Item) != 0 && e.Owner is not null && IsProjectileMoveType(e.MoveType))
            return NetEntityKind.Projectile;

        if (cn.Contains("_item") || e.Solid == Solid.Trigger)
            return NetEntityKind.Item;
        return NetEntityKind.Generic; // monsters, turrets, vehicles, mapobjects with alias models
    }

    /// <summary>Get (or allocate) a player's small stable net id — works for bots (which have no ENet peer).</summary>
    private int NetIdFor(Player p)
    {
        if (!_playerNetIds.TryGetValue(p, out int id))
        {
            id = _nextPlayerNetId++;
            _playerNetIds[p] = id;
        }
        return id;
    }

    /// <summary>Owner-replicated authoritative state: origin + velocity (full precision) + onground + HUD stats +
    /// the active weapon id (the local first-person viewmodel selector — QC wepent m_weapon, which the owner is
    /// excluded from the entity stream for). Fixed-layout: keep these in lockstep with
    /// <c>ClientNet.HandleSnapshot</c>'s owner read, appending new fields at the END.</summary>
    private void WriteOwnerState(BitWriter w, Player p, PeerState st)
    {
        w.WriteVector(p.Origin, XonoticGodot.Net.NetPrecision.Float);
        w.WriteVector(p.Velocity, XonoticGodot.Net.NetPrecision.Float);
        w.WriteBool(p.OnGround);
        w.WriteShort((int)p.Health);
        w.WriteShort((int)p.ArmorValue);
        w.WriteShort(p.ActiveWeaponId); // QC wepent m_weapon — drives the local first-person viewmodel

        // QC STAT(RESPAWN_TIME) (server/client.qc:2419-2436): the dead-player respawn countdown / "press fire"
        // prompt. Absolute sim time, negated while DEAD_RESPAWNING, 0 while alive/silent. Float (a sim time can
        // exceed a short over a long match).
        w.WriteFloat(p.RespawnTimeStat);

        // QC spectatee_status (server/client.qc:1904, networked in ClientData BIT(1)): 0 = this owner is a live
        // player; its own net id = observing (free-fly; the client maps it to -1); another player's net id = it
        // is spectating that player (the client renders from their eyes). Net ids are assigned here, so the
        // Player-ref spectatee link is resolved to a wire id at send time.
        int spec;
        if (p.Spectatee is { } tgt && !tgt.IsDead && !tgt.IsObserver)
            spec = NetIdFor(tgt);
        else if (p.IsObserver)
            spec = NetIdFor(p);          // observing (not following anyone)
        else
            spec = 0;                    // a live player
        p.SpectateeStatus = spec;
        w.WriteShort(spec);

        // QC view punch (PM_check_punch): the weapon-recoil view kick, decayed server-side and applied to the
        // owner's rendered view angles client-side. Owner-only (only the local player sees their own kick), so
        // it rides the owner block, not the entity stream. Small + cheap; sent unconditionally for lockstep.
        w.WriteVector(p.PunchAngle, XonoticGodot.Net.NetPrecision.Float);

        // [T57 accuracy] — the owner's own per-weapon accuracy bytes (QC ENT_CLIENT_ACCURACY, owner-only:
        // a.drawonlytoclient = e, accuracy.qc:54). One QC accuracy_byte per Registry<Weapon> id (0 = never fired,
        // 1..101 = pct+1, 255 = >100%). Sent at the END of the owner block, in lockstep with ClientNet's matching
        // read. Change-gated like QC's accuracy_send SendFlags (accuracy.qc:38: "zero sendflags can never be
        // sent... so we can use that to say that we send no accuracy"): write the change generation, then a bool
        // "changed since last sent to THIS peer" (tracked in PeerState.LastAccuracyGen, the T53 hash pattern);
        // only when changed do the length + bytes ride along. Steady state = one int + one false bool.
        int accGen = _world.Scores.AccuracyGeneration(p);
        w.WriteLong(accGen);
        bool sendAccuracy = st.LastAccuracyGen != accGen;
        w.WriteBool(sendAccuracy);
        if (sendAccuracy)
        {
            byte[] bytes = _world.Scores.AccuracyBytes(p);
            int n = bytes.Length > 255 ? 255 : bytes.Length; // length byte — the per-weapon array is small
            w.WriteByte(n);
            w.WriteBytes(bytes.AsSpan(0, n));
            st.LastAccuracyGen = accGen;
        }
    }

    // =====================================================================================
    //  Lag compensation (antilag.qc) — rewind other players to the shooter's view time at fire
    // =====================================================================================

    private readonly Dictionary<Player, NVec3> _antilagRestore = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, NVec3> _antilagEntityRestore = new(ReferenceEqualityComparer.Instance);
    private bool _lagCompActive;

    /// <summary>
    /// <paramref name="shooter"/>'s MEASURED round-trip latency, in seconds — the value
    /// <c>ANTILAG_LATENCY(e) = min(0.4, CS(e).ping * 0.001)</c> (antilag.qh) consumes to pick the rewind depth.
    /// This is the smoothed <c>cmd.receivetime − cmd.time</c> the server computed from the snapshot timestamp the
    /// client echoes in each input frame (DP sv_user.c:847), NOT an assumption: 0 for a LAN/listen/bot client
    /// (fully caught up) and the genuine round trip otherwise. Replaces the earlier seq-gap heuristic.
    /// </summary>
    public float EstimatedPing(Player shooter)
    {
        if (!_byPlayer.TryGetValue(shooter, out PeerState? st) || !st.HasRtt)
            return 0f;
        return st.MeasuredRtt;
    }

    /// <summary>
    /// <see cref="ILagCompensation.Begin"/>: rewind every OTHER antilagged entity — players PLUS monsters PLUS
    /// nades — to where <paramref name="shooter"/> saw them at fire time (<c>antilag_takeback_all</c>), sampled
    /// from each recorded <see cref="AntilagBuffer"/> at the shooter's measured view-time
    /// (<c>time - ANTILAG_LATENCY</c>, <see cref="LagCompensation.ComputeTakebackTime"/>). No-op unless server-side
    /// rewind is active (<c>autocvar_g_antilag == 2</c>; mode 1 is client-verified hitscan with no takeback), for a
    /// non-client shooter (bot/monster: no remote latency to compensate), or when already active (a multi-pellet
    /// shot won't nest). The shooter itself is the <c>ignore</c> entity.
    /// </summary>
    public void BeginLagComp(Entity shooter)
    {
        // QC's hitscan path only rewinds at g_antilag == 2 (traceline_antilag: autocvar_g_antilag != 2 → lag = 0);
        // g_antilag == 1 is client-verified hitscan with NO server takeback. The cvar-unset fallback is 2 (stock).
        if (_lagCompActive || _antilagMode != 2 || shooter is not Player sp || !_byPlayer.ContainsKey(sp))
            return;
        // Per-shooter opt-out (T54): QC forces lag = 0 for a shooter whose replicated cl_noantilag is set
        // (antilag.qc:154,205-230 — antilag_takeback_all's `if (noantilag(ignore)) lag = 0`; same gate in
        // tracing.qc:115 for fireBullet). Zero lag == no rewind, so skipping Begin entirely is equivalent
        // (EndLagComp no-ops via _lagCompActive). The restore path is untouched.
        if (_world.Commands.GetClientCvarBool(sp, "cl_noantilag"))
            return;
        _lagCompActive = true;
        // Rewind depth is the shooter's measured latency alone: ANTILAG_LATENCY(e) = min(0.4, ping) (antilag.qh),
        // with NO added ticrate (the "add one ticrate?" there is a commented-out musing). The single frame-of-lag
        // compensation QC applies is on the RECORD side (altime = time + frametime*(1+nudge), see RecordTime).
        float t = LagCompensation.ComputeTakebackTime(_world.Time, EstimatedPing(sp), 0f);

        // players (FOREACH_CLIENT IS_PLAYER && it != ignore → antilag_takeback(it, CS(it), time - lag)).
        _antilagRestore.Clear();
        foreach (KeyValuePair<Player, AntilagBuffer> kv in _antilag)
        {
            if (ReferenceEquals(kv.Key, sp) || !kv.Value.HasData)
                continue;
            _antilagRestore[kv.Key] = kv.Key.Origin;
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value.SampleAt(t));
        }

        // monsters + nades (IL_EACH g_monsters / IL_EACH g_projectiles[classname=="nade"] → antilag_takeback(it, it, ...)).
        _antilagEntityRestore.Clear();
        foreach (KeyValuePair<Entity, AntilagBuffer> kv in _antilagEntities)
        {
            if (ReferenceEquals(kv.Key, shooter) || kv.Key.IsFreed || !kv.Value.HasData)
                continue;
            _antilagEntityRestore[kv.Key] = kv.Key.Origin;
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value.SampleAt(t));
        }
    }

    /// <summary><see cref="ILagCompensation.End"/>: restore every rewound entity — players, monsters, nades — to
    /// its authoritative present position (<c>antilag_restore_all</c>).</summary>
    public void EndLagComp()
    {
        if (!_lagCompActive)
            return;
        foreach (KeyValuePair<Player, NVec3> kv in _antilagRestore)
            _world.Services.Entities.SetOrigin(kv.Key, kv.Value);
        _antilagRestore.Clear();
        foreach (KeyValuePair<Entity, NVec3> kv in _antilagEntityRestore)
            if (!kv.Key.IsFreed)
                _world.Services.Entities.SetOrigin(kv.Key, kv.Value);
        _antilagEntityRestore.Clear();
        _lagCompActive = false;
    }

    /// <summary>The ambient <see cref="LagComp"/> provider weapon code calls; routes to <see cref="BeginLagComp"/>/<see cref="EndLagComp"/>.</summary>
    private sealed class LagCompProvider : ILagCompensation
    {
        private readonly ServerNet _net;
        public LagCompProvider(ServerNet net) => _net = net;
        public void Begin(Entity shooter) => _net.BeginLagComp(shooter);
        public void End() => _net.EndLagComp();
    }

    // Snapshot entity count is patched after the loop (we don't know it up front without a pre-scan).
    private static int ReserveCount(BitWriter w) { int at = w.Length; w.WriteUShort(0); return at; }

    private static void PatchCount(BitWriter w, int at, int count)
        => w.PatchUShortAt(at, count);

    // =====================================================================================
    //  Effect + notification NET sinks (resolves the EffectEmitter / NotificationSystem TODOs)
    // =====================================================================================

    private readonly struct CapturedEffect
    {
        public readonly EffectRequest Request;
        public readonly Entity? Except;
        public CapturedEffect(in EffectRequest req) { Request = req; Except = req.Except; }
    }

    private readonly struct CapturedNotification
    {
        public readonly NotificationDispatch Dispatch;
        public CapturedNotification(in NotificationDispatch d) { Dispatch = d; }
    }

    private readonly struct CapturedSound
    {
        public readonly XonoticGodot.Engine.Simulation.SoundEvent Event;
        public CapturedSound(in XonoticGodot.Engine.Simulation.SoundEvent e) { Event = e; }
    }

    internal void CaptureEffect(in EffectRequest request) => _effectQueue.Add(new CapturedEffect(request));
    internal void CaptureNotification(in NotificationDispatch d) => _notifyQueue.Add(new CapturedNotification(d));

    /// <summary>SoundService.Broadcast handler — queue a server-emitted sound for this frame's sound bundle.</summary>
    private void OnSoundEmitted(XonoticGodot.Engine.Simulation.SoundEvent e) => _soundQueue.Add(new CapturedSound(e));

    /// <summary>
    /// Flush the per-frame captured effects + notifications to clients. Effects go in an unreliable
    /// <see cref="NetControl.EventBundle"/> (fire-and-forget, like the QC temp-entities), notifications in a
    /// reliable <see cref="NetControl.ReliableBundle"/> (kill-feed / scores must arrive). Both honor the
    /// per-request exclusion/target so a shooter doesn't get told about their own muzzleflash twice, etc.
    /// </summary>
    private void FlushEventBundles()
    {
        FlushEffects();
        FlushSounds();
        FlushNotifications();
    }

    private void FlushEffects()
    {
        if (_effectQueue.Count == 0)
            return;

        // Build one bundle per recipient only when an exclusion is in play; otherwise broadcast a shared one.
        // Simple + correct: broadcast the bundle to all, but encode each effect once; the rare "except" case
        // is handled by sending that effect only to the non-excluded peers (a per-peer pass).
        _eventWriter.Reset();
        _eventWriter.WriteByte((byte)NetControl.EventBundle);
        int countPos = ReserveCount(_eventWriter);
        int n = 0;
        bool anyExcept = false;
        for (int i = 0; i < _effectQueue.Count; i++)
        {
            CapturedEffect ce = _effectQueue[i];
            if (ce.Except is not null) { anyExcept = true; continue; } // handled per-peer below
            if (WriteEffect(_eventWriter, ce.Request)) n++;
        }
        PatchCount(_eventWriter, countPos, n);
        if (n > 0)
            _transport.Broadcast(_eventWriter.WrittenSpan, reliable: false);

        // per-peer pass for excluded effects (each goes to everyone but the excluded player).
        if (anyExcept)
        {
            foreach (PeerState st in _peers.Values)
            {
                if (!st.Accepted || st.Player is null) continue;
                _eventWriter.Reset();
                _eventWriter.WriteByte((byte)NetControl.EventBundle);
                int cpos = ReserveCount(_eventWriter);
                int m = 0;
                for (int i = 0; i < _effectQueue.Count; i++)
                {
                    CapturedEffect ce = _effectQueue[i];
                    if (ce.Except is null) continue;
                    if (ReferenceEquals(ce.Except, st.Player)) continue; // excluded recipient
                    if (WriteEffect(_eventWriter, ce.Request)) m++;
                }
                PatchCount(_eventWriter, cpos, m);
                if (m > 0)
                    _transport.Send(st.PeerId, _eventWriter.WrittenSpan, reliable: false);
            }
        }

        _effectQueue.Clear();
    }

    /// <summary>
    /// Encode one effect into the bundle: the registered effect id (ushort) then the EFF_NET_* body from
    /// <see cref="EffectNetProtocol.Encode"/> (the faithful QC Net_Write_Effect layout). Returns false for a
    /// non-networkable request (no registered effect / count-0 point effect) so the count stays accurate.
    /// </summary>
    private bool WriteEffect(BitWriter w, in EffectRequest r)
    {
        byte[]? body = EffectNetProtocol.Encode(r);
        if (body is null || r.Effect is null)
            return false;
        w.WriteUShort(r.Effect.RegistryId);
        w.WriteUShort(body.Length);
        w.WriteBytes(body);
        return true;
    }

    /// <summary>
    /// Flush this frame's captured positional sounds to all clients in one unreliable
    /// <see cref="NetControl.SoundBundle"/>. DP <c>sound()</c> is a pure broadcast — no per-recipient exclusion
    /// (unlike effects) — so this is a single shared bundle. One record per emission; repeats are kept
    /// (shotgun pellets, footsteps). PHS/occlusion culling is a follow-up.
    /// </summary>
    private void FlushSounds()
    {
        if (_soundQueue.Count == 0)
            return;

        _eventWriter.Reset();
        _eventWriter.WriteByte((byte)NetControl.SoundBundle);
        int countPos = ReserveCount(_eventWriter);
        int n = 0;
        for (int i = 0; i < _soundQueue.Count; i++)
            if (WriteSound(_eventWriter, _soundQueue[i].Event)) n++;
        PatchCount(_eventWriter, countPos, n);
        if (n > 0)
            _transport.Broadcast(_eventWriter.WrittenSpan, reliable: false);

        _soundQueue.Clear();
    }

    /// <summary>
    /// Encode one sound record via the shared <see cref="XonoticGodot.Net.SoundWire"/> codec: sample, origin (raw
    /// floats, matching <see cref="WriteEffect"/>'s origin — NOT the quantized WriteVector path), volume,
    /// attenuation, channel, the SOURCE entity's net id, and the loop/stop flags. The net id + flags let the
    /// client key a looping AudioStreamPlayer3D by (entity, channel) and replace/stop it as the emitter moves
    /// (Arc beam owner, vehicles). Returns false for a non-stop record with an empty sample (nothing to play) so
    /// the bundle count stays accurate; a STOP record is always written though its sample is empty. Inverse of
    /// <c>ClientNet.HandleSoundBundle</c>.
    /// </summary>
    private bool WriteSound(BitWriter w, in XonoticGodot.Engine.Simulation.SoundEvent e)
    {
        if (!e.Stop && string.IsNullOrEmpty(e.Sample))
            return false;
        var rec = new XonoticGodot.Net.SoundWire
        {
            Sample = e.Sample,
            Origin = e.Origin,
            Volume = e.Volume,
            Attenuation = e.Attenuation,
            Channel = (int)e.Channel,
            SourceNetId = NetIdForEntity(e.Source),
            Loop = e.Loop,
            Stop = e.Stop,
            Pitch = e.Pitch,
        };
        rec.Write(w);
        return true;
    }

    /// <summary>
    /// The network id of a sound's SOURCE entity — the SAME id space the snapshot uses (<see cref="BuildEntitySet"/>),
    /// so the client can match a looping sound to the player/vehicle entity it follows. Players get their small
    /// stable id (<see cref="NetIdFor"/>); any other entity gets the <see cref="EntityNetBase"/>+index id. 0 when
    /// the entity is null or the id would overflow the ushort wire field — the loop then simply doesn't follow
    /// (it still plays at its emit origin).
    /// </summary>
    private int NetIdForEntity(Entity? e)
    {
        if (e is null) return 0;
        if (e is Player p) return NetIdFor(p);
        int id = EntityNetBase + e.Index;
        return id <= ushort.MaxValue ? id : 0;
    }

    private void FlushNotifications()
    {
        if (_notifyQueue.Count == 0)
            return;

        foreach (PeerState st in _peers.Values)
        {
            if (!st.Accepted || st.Player is null) continue;

            _reliableWriter.Reset();
            _reliableWriter.WriteByte((byte)NetControl.ReliableBundle);
            int cpos = ReserveCount(_reliableWriter);
            int n = 0;
            for (int i = 0; i < _notifyQueue.Count; i++)
            {
                NotificationDispatch d = _notifyQueue[i].Dispatch;
                if (!NotificationReaches(d, st.Player)) continue;
                WriteNotification(_reliableWriter, d);
                n++;
            }
            PatchCount(_reliableWriter, cpos, n);
            if (n > 0)
                _transport.Send(st.PeerId, _reliableWriter.WrittenSpan, reliable: true);
        }

        _notifyQueue.Clear();
    }

    /// <summary>Does a notification's broadcast/target reach this player? (the QC NOTIF_ONE/ALL/EXCEPT routing).</summary>
    private static bool NotificationReaches(in NotificationDispatch d, Player p)
    {
        switch (d.Broadcast)
        {
            case NotifBroadcast.All:
                return true;
            case NotifBroadcast.AllExcept:
                return !ReferenceEquals(d.Target, p);
            case NotifBroadcast.One:
            case NotifBroadcast.OneOnly:
                return ReferenceEquals(d.Target, p);
            case NotifBroadcast.Team:
                return d.Target is Player t && t.Team == p.Team;
            case NotifBroadcast.TeamExcept:
                return d.Target is Player te && te.Team == p.Team && !ReferenceEquals(d.Target, p);
            default:
                return true;
        }
    }

    /// <summary>
    /// Encode a notification: its registry id (ushort), the resolved text, and the raw string/float args (so
    /// the client can re-localize/re-format if it wants). Compact + reliable.
    /// </summary>
    private void WriteNotification(BitWriter w, in NotificationDispatch d)
    {
        w.WriteUShort(d.Notification.RegistryId);
        w.WriteByte((byte)d.Notification.Type);
        w.WriteString(d.Text);
        w.WriteByte(d.StringArgs.Length);
        for (int i = 0; i < d.StringArgs.Length; i++) w.WriteString(d.StringArgs[i]);
        w.WriteByte(d.FloatArgs.Length);
        for (int i = 0; i < d.FloatArgs.Length; i++) w.WriteFloat(d.FloatArgs[i]);
    }

    // The world's fixed tick dt (SimulationLoop.TicRate is the per-tick seconds, 1/72). Mirrored from the
    // engine constant so the input dt fallback and the advertised tick rate stay in lockstep with the sim.
    private const float SimulationLoopTicRate = XonoticGodot.Engine.Simulation.SimulationLoop.TicRate;

    private sealed class EffectNetSink : IEffectSink
    {
        private readonly ServerNet _net;
        public EffectNetSink(ServerNet net) => _net = net;
        public void Emit(in EffectRequest request) => _net.CaptureEffect(request);
    }

    private sealed class NotificationNetSink : INotificationSink
    {
        private readonly ServerNet _net;
        public NotificationNetSink(ServerNet net) => _net = net;
        public void Dispatch(in NotificationDispatch dispatch) => _net.CaptureNotification(dispatch);
    }

    public void Dispose()
    {
        // restore the default recording sinks so a torn-down server doesn't leave dangling net sinks.
        if (EffectEmitter.Sink is EffectNetSink) EffectEmitter.Sink = EffectEmitter.Recorder;
        if (NotificationSystem.Sink is NotificationNetSink) NotificationSystem.Sink = NotificationSystem.Recorder;
        _world.Services.SoundImpl.Broadcast -= OnSoundEmitted; // drop the sound capture (avoid a re-host double-subscribe)
        if (LagComp.Provider is LagCompProvider) LagComp.Provider = null;
        // drop the per-player physics resolver only if it is still OURS (a re-host may have installed a new one).
        if (ReferenceEquals(MovementParameters.PresetProvider, _presetProvider))
            MovementParameters.PresetProvider = null;
        _master?.Dispose();
        _transport.Dispose();
    }
}
