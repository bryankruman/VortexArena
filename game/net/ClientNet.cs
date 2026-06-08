using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Net;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The client's network driver — connects to a <see cref="ServerNet"/>, streams the local player's
/// <see cref="InputCommand"/>s up, and applies the authoritative snapshots that come back: the local player
/// is <b>predicted + reconciled</b> (via <see cref="PredictionBuffer"/> + <see cref="Reconciler"/>) and every
/// remote entity is <b>interpolated</b> (via a per-entity <see cref="InterpolationBuffer"/>). Effect,
/// temp-entity and notification messages are decoded and surfaced as C# events for the renderer to consume.
///
/// Per host frame the host calls, in order:
/// <list type="number">
///   <item><see cref="Poll"/> — pump the transport (handshake + snapshots + event bundles arrive here);</item>
///   <item><see cref="SendInput"/> — sample the local input, push it to the ring buffer, predict forward, and
///         send the redundant input tail to the server.</item>
/// </list>
/// then reads <see cref="PredictedOrigin"/> (+ <see cref="PredictedStairOffset"/>) for the local camera and
/// <see cref="SampleRemote"/> for each remote entity's render pose.
///
/// The movement maths is injected as an <see cref="IMovementStep"/> (the host wraps the shared deterministic
/// <see cref="Movement"/> sim over a scratch entity — see <see cref="EntityMovementStep"/>) so this driver
/// stays movement-agnostic and the SAME sim runs client-side prediction and server-side authority.
///
/// The client also echoes the latest snapshot's server-time in each input frame so the server can MEASURE its
/// round-trip latency for lag compensation (antilag.qc), and refreshes its predictor movement-vars from the
/// server's replicated <see cref="MoveVarsBlock"/> so prediction reads authoritative physics, not constants.
/// TODO(net): a client-side interpolation delay / jitter buffer tuned to RTT (we interpolate at the latest
/// received server time, which is correct but minimally buffered). The predict/reconcile/interpolate core is complete.
/// </summary>
public sealed class ClientNet : IDisposable
{
    private NetTransport.Client _transport;
    private readonly PredictionBuffer _inputBuffer = new();
    private readonly Reconciler _reconciler;
    private readonly Func<InputCommand> _sampleInput;

    // reconnect: remembered target + loss detection so a dropped link can be re-established.
    private string _host = "";
    private int _port;
    private bool _wasConnected;

    // rate tuning: minimum seconds between input-frame transmits (0 = every tick). The redundant tail makes a
    // lower send rate loss-tolerant; raising this trades a little input latency for bandwidth (DP cl_netrate).
    private float _lastInputSend;

    private readonly BitWriter _writer = new(512);

    // delta-decompression baseline ring (reconstructs each snapshot from the server-named baseline).
    private readonly ClientSnapshotHistory _snapHistory = new();
    private readonly List<int> _staleScratch = new();

    // remote entities, keyed by their server net id.
    private readonly Dictionary<int, RemoteEntity> _remotes = new();

    private sealed class RemoteEntity
    {
        public readonly InterpolationBuffer Interp = new();
        public float LastServerTime;     // serverprevtime for the next Note()
        public NetEntityState State;     // latest decoded properties (model/frame/health/team) for the renderer
    }

    /// <summary>The client's own network entity id (assigned by the server at handshake). 0 until accepted.</summary>
    public int LocalNetId { get; private set; }

    /// <summary>True once the server has accepted the build-parity handshake.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The server tick rate (Hz) advertised at handshake (drives interpolation/decay timing).</summary>
    public float ServerTickRate { get; private set; } = 72f;

    /// <summary>The latest server time carried by a snapshot (the clock we interpolate remote entities at).</summary>
    public float LatestServerTime { get; private set; }

    /// <summary>The current predicted local origin (Quake space) — what the camera follows.</summary>
    public NVec3 PredictedOrigin => _reconciler.Predicted.Origin;

    /// <summary>The current predicted local velocity (Quake space).</summary>
    public NVec3 PredictedVelocity => _reconciler.Predicted.Velocity;

    /// <summary>Owner HUD stats from the last snapshot (health/armor), for the client HUD.</summary>
    public int Health { get; private set; }
    public int Armor { get; private set; }

    // Spawn/respawn settle-snap state: a freshly spawned player teleports to the spawn point and falls to the
    // floor; the client predicts the whole fall instantly while the server lands tick-by-tick, so the reconcile
    // would fold that prediction LEAD into the view smoother. We snap (clear smoothing) until the server lands.
    private bool _wasAliveLastSnap;
    private bool _settlingAfterSpawn;

    /// <summary>The local player's active weapon id (registry id; -1 = none/holstered), from the owner block —
    /// the first-person viewmodel selector (QC wepent m_weapon → viewmodels[slot].activeweapon).</summary>
    public int ActiveWeaponId { get; private set; } = -1;

    /// <summary>The last networked scoreboard (per-player columns + team totals), for the client scoreboard HUD. Null until first received.</summary>
    public XonoticGodot.Net.ScoreboardWire? LatestScoreboard { get; private set; }

    /// <summary>The last received score LAYOUT (QC ENT_CLIENT_SCORES_INFO): the active gametype/teamplay + the
    /// applied per-mode label/flag set, for the client scoreboard HUD's per-mode columns. Null until first received.
    /// Already applied to the shared <see cref="XonoticGodot.Common.Gameplay.Scoring.GameScores"/> when set.</summary>
    public XonoticGodot.Net.ScoreInfoBlock.Decoded? LatestScoreInfo { get; private set; }

    /// <summary>The stair-smoothing Z offset to subtract from <see cref="PredictedOrigin"/> Z when placing the
    /// camera (so it glides over steps). Advanced by the REAL frame delta <paramref name="frameDt"/> (clamped),
    /// NOT the server-synced render clock — the rebasing clock's quantized jumps made the view jitter up/down.</summary>
    public float PredictedStairOffset(float frameDt) => _reconciler.GetStairSmoothOffset(frameDt);

    /// <summary>The prediction-error smoothing offset to add to the rendered origin (decays to zero).</summary>
    public NVec3 PredictionErrorOffset(float now) => _reconciler.GetPredictionErrorOrigin(now);

    // ---- renderer-facing events ----

    /// <summary>Raised for each received effect/temp-entity (the client renderer spawns the particle/sound).</summary>
    public event Action<EffectEvent>? EffectReceived;

    /// <summary>Raised for each received positional sound (the client renderer plays it spatially). DP SV_StartSound.</summary>
    public event Action<SoundEvent>? SoundReceived;

    /// <summary>Raised for each received notification (the client HUD shows the kill-feed / centerprint / announce).</summary>
    public event Action<NotificationEvent>? NotificationReceived;

    /// <summary>A decoded effect event (the client-side counterpart of <see cref="EffectRequest"/>).</summary>
    public readonly struct EffectEvent
    {
        public readonly Effect? Effect;       // resolved from the registry id; null if unknown
        public readonly NVec3 Origin;
        public readonly NVec3 Velocity;
        public readonly int Count;
        public readonly NVec3 ColorMin;
        public readonly NVec3 ColorMax;
        public EffectEvent(Effect? effect, NVec3 origin, NVec3 velocity, int count, NVec3 colorMin, NVec3 colorMax)
        { Effect = effect; Origin = origin; Velocity = velocity; Count = count; ColorMin = colorMin; ColorMax = colorMax; }
    }

    /// <summary>A decoded positional sound (the client-side counterpart of the engine's <c>SoundEvent</c>). Kept
    /// distinct from <c>XonoticGodot.Engine.Simulation.SoundEvent</c> so the client net layer doesn't depend on the
    /// engine record (mirrors <see cref="EffectEvent"/> vs <c>EffectRequest</c>). <see cref="SourceNetId"/> +
    /// <see cref="Loop"/>/<see cref="Stop"/> carry DP's entity+channel looping-sound model.</summary>
    public readonly struct SoundEvent
    {
        public readonly string Sample;        // bare sample path or a registered GameSound name
        public readonly NVec3 Origin;
        public readonly float Volume;
        public readonly float Attenuation;
        public readonly int Channel;          // SoundChannel, carried as a byte on the wire
        public readonly int SourceNetId;      // emitter entity net id — keys a looping sound by (entity, channel)
        public readonly bool Loop;            // a persistent looping sound (QC loopsound)
        public readonly bool Stop;            // stop the (entity, channel) sound (DP sound(e, ch, SND_Null))
        public readonly float Pitch;          // pitch scale (1.0 = normal, DP percentage encoding)
        public SoundEvent(string sample, NVec3 origin, float volume, float attenuation, int channel,
            int sourceNetId, bool loop, bool stop, float pitch = 1f)
        {
            Sample = sample; Origin = origin; Volume = volume; Attenuation = attenuation; Channel = channel;
            SourceNetId = sourceNetId; Loop = loop; Stop = stop; Pitch = pitch;
        }
    }

    /// <summary>A decoded notification event for the HUD.</summary>
    public readonly struct NotificationEvent
    {
        public readonly Notification? Notification;  // resolved from the registry id; null if unknown
        public readonly MsgType Type;
        public readonly string Text;                 // pre-formatted message / announcer sound name
        public readonly string[] StringArgs;
        public readonly float[] FloatArgs;
        public NotificationEvent(Notification? n, MsgType type, string text, string[] s, float[] f)
        { Notification = n; Type = type; Text = text; StringArgs = s; FloatArgs = f; }
    }

    public ClientNet(NetTransport.Client transport, IMovementStep movement, Func<InputCommand> sampleInput)
    {
        _transport = transport;
        _sampleInput = sampleInput;
        _reconciler = new Reconciler(_inputBuffer, movement)
        {
            // smoothing on by default so corrections/steps are visually gradual (cl_movement_errorcompensation).
            ErrorCompensation = 1f,
            TickRate = 72f,
        };

        _transport.PacketReceived += OnPacket;
    }

    /// <summary>Convenience: connect to <paramref name="host"/>:<paramref name="port"/> and build the driver.</summary>
    public static ClientNet? Connect(string host, int port, IMovementStep movement, Func<InputCommand> sampleInput)
    {
        NetTransport.Client? t = NetTransport.Client.Connect(host, port);
        if (t is null) return null;
        var cn = new ClientNet(t, movement, sampleInput) { _host = host, _port = port };
        return cn;
    }

    /// <summary>Minimum seconds between input-frame transmits (0 = every tick). DP <c>cl_netrate</c>-style pacing.</summary>
    public float InputSendInterval { get; set; }

    /// <summary>True when the link was established and has since dropped — the host can prompt/auto <see cref="Reconnect"/>.</summary>
    public bool ConnectionLost => _wasConnected && !_transport.IsActive;

    /// <summary>
    /// Re-establish the link to the original server after a drop: rebuild the ENet transport and reset the
    /// handshake + delta baseline + remote-entity state so the fresh session starts clean. Returns false if the
    /// socket couldn't be created.
    /// </summary>
    public bool Reconnect()
    {
        NetTransport.Client? t = NetTransport.Client.Connect(_host, _port);
        if (t is null)
            return false;

        _transport.PacketReceived -= OnPacket;
        _transport.Dispose();
        _transport = t;
        _transport.PacketReceived += OnPacket;

        _handshakeSent = false;
        _wasConnected = false;
        Accepted = false;
        _remotes.Clear();
        // Reset the echoed snapshot clock so the first post-reconnect input frame doesn't carry a stale large
        // server-time the server would read as a near-zero/negative RTT (it self-heals via the rtt<0 clamp, but
        // this avoids the brief under-report). 0 = "no snapshot yet", which the server treats as "measure nothing".
        LatestServerTime = 0f;
        return true;
    }

    // =====================================================================================
    //  Per-frame drive
    // =====================================================================================

    /// <summary>
    /// Pump the transport: completes the ENet connection, sends the build-parity handshake once connected,
    /// and dispatches any received snapshots / event bundles. Call once per host frame before reading state.
    /// </summary>
    public void Poll()
    {
        _transport.Poll();

        if (_transport.IsConnected)
            _wasConnected = true; // arm loss detection (ConnectionLost / Reconnect)

        // Once the ENet link is up but we haven't sent our handshake, send it (the server replies accept/reject).
        if (_transport.IsConnected && !_handshakeSent)
        {
            SendHandshake();
            _handshakeSent = true;
        }
    }

    private bool _handshakeSent;

    /// <summary>This client's cryptographic identity (its public-key fingerprint is the stable player id). Defaults
    /// to a fresh ephemeral key; the host may assign a persisted <see cref="PlayerIdentity"/> before connecting.</summary>
    public PlayerIdentity Identity { get; set; } = PlayerIdentity.Generate();

    private void SendHandshake()
    {
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.HandshakeRequest);
        _writer.WriteULong(NetProtocol.BuildParity());
        _writer.WriteString(LocalPlayerName);
        byte[] pub = Identity.PublicKey;
        _writer.WriteUShort(pub.Length);
        _writer.WriteBytes(pub);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    /// <summary>Sign the server's session challenge and return the proof (SessionAuth). Admits us once verified.</summary>
    private void HandleChallenge(ref BitReader r)
    {
        ReadOnlySpan<byte> challenge = r.ReadBytes(r.ReadUShort());
        if (r.BadRead) return;
        byte[] sig = Identity.Sign(challenge);
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.HandshakeAuth);
        _writer.WriteUShort(sig.Length);
        _writer.WriteBytes(sig);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    /// <summary>The name advertised to the server at handshake. Host may set this before connecting.</summary>
    public string LocalPlayerName { get; set; } = "player";

    /// <summary>
    /// Sample the local input this tick, push it to the input ring buffer, predict forward (replay from the
    /// last acked authoritative state), and send the redundant input tail to the server. Returns the seq
    /// stamped on this tick's command. No-op until the handshake is accepted.
    ///
    /// <para>The sampled <see cref="InputCommand"/> may carry a one-shot <see cref="InputCommand.Impulse"/> (QC
    /// usercmd.impulse — a weapon switch/reload, stamped by the host's sampler). It rides the command transparently
    /// through <see cref="InputCommand.Serialize"/>; because the impulse is part of THIS command's Seq, the
    /// redundant tail re-sends the same numbered command, and the server's Seq-dedup processes it exactly once
    /// (then zeroes it on its cached command so a starved tick can't re-fire it). No separate channel is needed —
    /// this matches QC, where the impulse is part of the move command.</para>
    /// </summary>
    public uint SendInput(float now)
    {
        if (!Accepted)
            return 0;

        InputCommand cmd = _sampleInput();
        uint seq = _inputBuffer.Push(cmd);

        // Re-predict from the last authoritative state with the freshly-extended input history so the local
        // view reflects this tick immediately (client-side prediction). We reconcile against the last server
        // state we have; if none yet, the reconciler simply replays from the current predicted state.
        Predict(now);

        // Send the redundant tail (unreliable): a single dropped datagram won't strand an input. Lead with the
        // newest snapshot we decoded so the server can delta the next snapshot against a baseline we hold. The
        // transmit is rate-gated (InputSendInterval); the redundancy widens to still cover every batched tick.
        if (InputSendInterval <= 0f || now - _lastInputSend >= InputSendInterval)
        {
            int redundancy = InputSendInterval > 0f ? (int)(InputSendInterval * ServerTickRate) + 3 : 3;
            _writer.Reset();
            _writer.WriteByte((byte)NetControl.InputFrame);
            _writer.WriteUShort(_snapHistory.LastDecodedSeq);
            // Echo the server-time of the latest snapshot we hold so the server can MEASURE our round-trip latency
            // (receive-time − this echoed time = RTT), the faithful port of DP's cmd.receivetime − cmd.time
            // (sv_user.c) that feeds ANTILAG_LATENCY. 0 before the first snapshot (the server then measures nothing).
            _writer.WriteFloat(LatestServerTime);
            _inputBuffer.WriteRedundant(_writer, redundancy);
            _transport.SendToServer(_writer.WrittenSpan, reliable: false);
            // Flush this input onto the wire NOW (send-only) instead of letting it sit until the next Poll(): on
            // the in-process listen loop the server's next Tick then consumes it THIS frame rather than a render-
            // frame later, cutting the input→fire latency that makes firing (and Blaster trick-jump timing) feel
            // behind the keypress. Harmless on a remote client (the datagram just leaves a hair sooner).
            _transport.Flush();
            _lastInputSend = now;
        }
        return seq;
    }

    // The last authoritative state + acked seq we reconcile from (updated on each snapshot).
    private PredictedState _serverState;
    private uint _serverAckedSeq;
    private PlayerState _vars = DefaultVars();
    private PredictedState _previousPredictionAtAck;

    /// <summary>Replay unacked inputs on top of the last server state to refresh <see cref="PredictedOrigin"/>
    /// (replay-only — the prediction error is measured/armed in <see cref="HandleSnapshot"/> when a new ack
    /// lands, not on every input tick, so smoothing decays cleanly between snapshots).</summary>
    private void Predict(float now)
        => _reconciler.Predict(_serverState, _serverAckedSeq, _vars, now);

    // =====================================================================================
    //  Receive
    // =====================================================================================

    private void OnPacket(int from, int channel, byte[] data)
    {
        var r = new BitReader(data);
        var control = (NetControl)r.ReadByte();
        switch (control)
        {
            case NetControl.HandshakeChallenge: HandleChallenge(ref r); break;
            case NetControl.HandshakeAccept: HandleAccept(ref r); break;
            case NetControl.HandshakeReject: HandleReject(ref r); break;
            case NetControl.Snapshot: HandleSnapshot(ref r); break;
            case NetControl.EventBundle: HandleEventBundle(ref r); break;
            case NetControl.SoundBundle: HandleSoundBundle(ref r); break;
            case NetControl.ReliableBundle: HandleReliableBundle(ref r); break;
            case NetControl.ServerPrint: HandleServerPrint(ref r); break;
            case NetControl.MinigameState: HandleMinigameState(ref r); break;
            default: break;
        }
    }

    /// <summary>Raised with a decoded minigame-session envelope (the S2C <see cref="NetControl.MinigameState"/>
    /// push) — the host drives the minigame board overlay + menu from it. A null
    /// <see cref="XonoticGodot.Game.Net.MinigameNetState.Envelope.Session"/> means the local player left / has no
    /// active minigame. The C# stand-in for CSQC <c>activate_minigame</c>/<c>deactivate_minigame</c>.</summary>
    public event Action<XonoticGodot.Game.Net.MinigameNetState.Envelope>? MinigameStateReceived;

    private void HandleMinigameState(ref BitReader r)
    {
        XonoticGodot.Game.Net.MinigameNetState.Envelope env = XonoticGodot.Game.Net.MinigameNetState.DecodeEnvelope(ref r);
        if (r.BadRead)
            return;
        MinigameStateReceived?.Invoke(env);
    }

    /// <summary>Raised with a line of server console output (a reply to <see cref="SendStringCommand"/> or a
    /// server notice) — the host appends it to the in-game console. DP <c>svc_print</c>.</summary>
    public event Action<string>? PrintReceived;

    /// <summary>
    /// Send a console command line to run on the server on our behalf (DP <c>clc_stringcmd</c>) — how the
    /// in-game console reaches a REMOTE server's gameplay commands (kill/say/team/…). Reliable; the reply (if
    /// any) arrives as a <see cref="NetControl.ServerPrint"/> raised on <see cref="PrintReceived"/>. No-op until
    /// the handshake is accepted (the server only honours commands from an authed peer).
    /// </summary>
    public void SendStringCommand(string line)
    {
        if (!Accepted || string.IsNullOrWhiteSpace(line))
            return;
        _writer.Reset();
        _writer.WriteByte((byte)NetControl.ClientCommand);
        _writer.WriteString(line);
        _transport.SendToServer(_writer.WrittenSpan, reliable: true);
    }

    private void HandleServerPrint(ref BitReader r)
    {
        string text = r.ReadString();
        if (r.BadRead)
            return;
        PrintReceived?.Invoke(text);
    }

    private void HandleAccept(ref BitReader r)
    {
        LocalNetId = r.ReadUShort();
        ServerTickRate = r.ReadFloat();
        if (ServerTickRate <= 0f) ServerTickRate = 72f;
        ServerName = r.ReadString();
        _reconciler.TickRate = ServerTickRate;
        Accepted = true;
        GD.Print($"[ClientNet] handshake accepted by '{ServerName}': netId {LocalNetId}, tickrate {ServerTickRate} Hz.");
    }

    /// <summary>The server's display name, learned at handshake (for the client UI / server browser).</summary>
    public string ServerName { get; private set; } = "";

    private void HandleReject(ref BitReader r)
    {
        string reason = r.ReadString();
        GD.PrintErr($"[ClientNet] handshake REJECTED: {reason}");
        Accepted = false;
    }

    private void HandleSnapshot(ref BitReader r)
    {
        float serverTime = r.ReadFloat();
        uint ackedSeq = r.ReadULong();

        int ownerNetId = r.ReadUShort();
        // owner authoritative state (full precision) — the reconcile seed.
        NVec3 origin = r.ReadVector(NetPrecision.Float);
        NVec3 velocity = r.ReadVector(NetPrecision.Float);
        bool onGround = r.ReadBool();
        Health = r.ReadShort();
        Armor = r.ReadShort();
        ActiveWeaponId = r.ReadShort(); // owner block — same order as ServerNet.WriteOwnerState (viewmodel selector)

        // movevars: when the server's physics changed, stamp the replicated values into our cvar store so the
        // predictor's MovementParameters.FromCvars() matches authority (mid-match physics/mutator changes), then
        // refresh the PlayerState the reconciler carries from those same live cvars — so prediction reads the
        // REPLICATED movevars (the server's sv_maxspeed/accel/gravity/…) rather than the hardcoded Defaults the
        // session seeded with. (PlayerPhysics.Move reads FromCvars() directly each tick, so the cvar Apply is the
        // load-bearing half; refreshing _vars keeps the carried stat set honest for any consumer of it too.)
        if (r.ReadBool())
        {
            float[] mv = MoveVarsBlock.Deserialize(ref r);
            if (!r.BadRead && Api.Services is not null)
            {
                MoveVarsBlock.Apply(Api.Cvars, mv);
                _vars = VarsFromCvars();
            }
        }

        // ScoreInfo (QC ENT_CLIENT_SCORES_INFO): when the per-mode label/flag layout changed, parse + APPLY it
        // BEFORE the scoreboard block below. This is THE fix for the dropped-scoreboard bug: a remote --connect
        // client only ran GameScores.RegisterAll (default labels), so its networked-column set differed from the
        // server's mode-specific set and ScoreboardBlock.Deserialize dropped the whole block on a layout-hash
        // mismatch. Applying the received labels (GameScores.SetLabel per field) makes the client's NetworkedFields
        // — and thus the layout hash — match the server, so the scoreboard read below now succeeds. Apply is
        // idempotent: on a listen server the co-located server already set these on the SHARED GameScores, so this
        // re-writes the same values (a no-op) and never desyncs the in-process path.
        if (r.ReadBool())
        {
            // ALWAYS deserialize to consume the bytes (keeps the stream aligned for the scoreboard + entity
            // sections below) even when there's no engine to apply into — mirrors the movevars block above.
            XonoticGodot.Net.ScoreInfoBlock.Decoded? si = XonoticGodot.Net.ScoreInfoBlock.Deserialize(ref r);
            if (si is not null && Api.Services is not null)
            {
                XonoticGodot.Net.ScoreInfoBlock.Apply(si);
                LatestScoreInfo = si;
            }
        }

        // scoreboard: when a score changed, parse the networked per-player columns + team totals (the HUD reads
        // LatestScoreboard). Sent after the ScoreInfo block (so the layout already matches) and before the entity
        // section, so it must be read here to stay frame-aligned.
        if (r.ReadBool())
        {
            XonoticGodot.Net.ScoreboardWire? sb = XonoticGodot.Net.ScoreboardBlock.Deserialize(ref r);
            if (sb is not null)
                LatestScoreboard = sb;
        }

        if (r.BadRead)
            return;

        LatestServerTime = serverTime;

        // Stash the previous prediction at the (newly) acked frame for the error measurement, then update the
        // authoritative state the reconciler replays from.
        _previousPredictionAtAck = _reconciler.Predicted;
        _serverState = new PredictedState { Origin = origin, Velocity = velocity, OnGround = onGround };
        _serverAckedSeq = ackedSeq;

        // Reconcile immediately so prediction error is measured + smoothing armed the moment the ack lands.
        _reconciler.Reconcile(_serverState, _serverAckedSeq, _vars, serverTime, _previousPredictionAtAck);

        // Spawn/respawn settle-snap: a freshly spawned player teleports to its spawn point and FALLS to the floor.
        // The client predicts that whole fall in one replay (it lands instantly) while the server descends one tick
        // per frame — so the reconcile measures a big positive prediction LEAD every fall snapshot and folds it
        // into the view smoother, floating the camera ~150u up for seconds. Treat the spawn+settle as a teleport
        // (QC csqcmodel_teleported: a respawn snaps the view, never smooths): keep the smoother cleared until the
        // server reports the player has landed (on-ground). After that the player is stable (oErr≈0) so normal
        // smoothing resumes cleanly. The mid-game teleporter/respawn-in-place cases are still caught by the
        // teleport-sized origin reset in Reconciler.SetPredictionError.
        if (Health > 0 && !_wasAliveLastSnap)
            _settlingAfterSpawn = true;
        _wasAliveLastSnap = Health > 0;
        if (_settlingAfterSpawn)
        {
            _reconciler.ResetError();
            if (_serverState.OnGround)
                _settlingAfterSpawn = false; // landed: prediction has converged, resume smoothing
        }

        // delta-decompress the entity section against the baseline the server named.
        IReadOnlyDictionary<int, NetEntityState>? entities = _snapHistory.DecodeSnapshot(ref r);
        if (entities is null)
            return; // bad read or missing baseline — wait for a full snapshot

        foreach (KeyValuePair<int, NetEntityState> kv in entities)
        {
            int netId = kv.Key;
            if (netId == LocalNetId)
                continue; // never interpolate our own entity (we predict it)

            NetEntityState s = kv.Value;
            RemoteEntity re = GetOrCreateRemote(netId);
            re.State = s;
            bool teleported = (s.Flags & NetEntityFlags.Teleported) != 0;
            var snap = new Snapshot { Time = serverTime, Origin = s.Origin, Angles = s.Angles };
            re.Interp.Note(snap, teleported, re.LastServerTime);
            re.LastServerTime = serverTime;
        }

        // drop remotes the server removed this frame (no longer in the reconstructed set).
        _staleScratch.Clear();
        foreach (int id in _remotes.Keys)
            if (id != LocalNetId && !entities.ContainsKey(id))
                _staleScratch.Add(id);
        for (int i = 0; i < _staleScratch.Count; i++)
            _remotes.Remove(_staleScratch[i]);
    }

    private RemoteEntity GetOrCreateRemote(int netId)
    {
        if (!_remotes.TryGetValue(netId, out RemoteEntity? re))
        {
            re = new RemoteEntity();
            _remotes[netId] = re;
        }
        return re;
    }

    /// <summary>The net ids of all known remote entities (the renderer iterates these to place nodes).</summary>
    public IReadOnlyCollection<int> RemoteIds => _remotes.Keys;

    /// <summary>
    /// The interpolated render pose of remote entity <paramref name="netId"/> at client time
    /// <paramref name="now"/> (origin + seam-safe blended angles). Returns false if the entity is unknown.
    /// Interpolate slightly behind the latest server time for smoothness; here <paramref name="now"/> is
    /// typically <see cref="LatestServerTime"/> (a one-snapshot delay falls out of the two-snapshot lerp).
    /// </summary>
    public bool SampleRemote(int netId, float now, out NVec3 origin, out NVec3 angles)
    {
        if (_remotes.TryGetValue(netId, out RemoteEntity? re) && re.Interp.HasData)
        {
            Snapshot s = re.Interp.Sample(now);
            origin = s.Origin;
            angles = s.Angles;
            return true;
        }
        origin = default;
        angles = default;
        return false;
    }

    /// <summary>The latest decoded properties (kind/model/frame/health/team/flags) of a remote entity, for the
    /// renderer to pick the right model + nameplate. Returns false if the entity is unknown.</summary>
    public bool TryGetRemoteState(int netId, out NetEntityState state)
    {
        if (_remotes.TryGetValue(netId, out RemoteEntity? re)) { state = re.State; return true; }
        state = default;
        return false;
    }

    /// <summary>Drop a remote entity (the renderer calls this when the server stops sending it / it's removed).</summary>
    public void ForgetRemote(int netId) => _remotes.Remove(netId);

    // ---- event bundles ----

    private void HandleEventBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            int effectId = r.ReadUShort();
            int bodyLen = r.ReadUShort();
            ReadOnlySpan<byte> body = r.ReadBytes(bodyLen);
            if (r.BadRead)
                return;
            DecodeEffect(effectId, body);
        }
    }

    /// <summary>
    /// Decode one EFF_NET_* effect body (the inverse of <see cref="EffectNetProtocol.Encode"/>) and raise
    /// <see cref="EffectReceived"/>. The body is: origin vector (3 floats), an extraflags byte, then — gated
    /// by the flags — velocity (3 floats), color-min triple, color-max triple, and (point effects only) a
    /// count byte. Trail effects carry no count.
    /// </summary>
    private void DecodeEffect(int effectId, ReadOnlySpan<byte> body)
    {
        Effect? effect = (effectId >= 0 && effectId < Effects.Count) ? Effects.ById(effectId) : null;
        var br = new BitReader(body);

        NVec3 origin = new(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        int flags = br.ReadByte();

        NVec3 velocity = default, colorMin = default, colorMax = default;
        if ((flags & EffectNetProtocol.NetVelocity) != 0)
            velocity = new NVec3(br.ReadFloat(), br.ReadFloat(), br.ReadFloat());
        if ((flags & EffectNetProtocol.NetColorMin) != 0)
            colorMin = ReadColor(ref br);
        // EFF_NET_COLOR_SAME means min == max; only color-max is sent when distinct.
        if ((flags & EffectNetProtocol.NetColorSame) != 0)
            colorMax = colorMin;
        else if ((flags & EffectNetProtocol.NetColorMax) != 0)
            colorMax = ReadColor(ref br);

        bool trail = effect is { IsTrail: true };
        int count = trail ? 0 : br.ReadByte();
        if (br.BadRead)
            return;

        EffectReceived?.Invoke(new EffectEvent(effect, origin, velocity, count, colorMin, colorMax));
    }

    // EFF_NET color components are quantized as rint(bound(0,16*c,255)); decode back to the [0,16) float range.
    private static NVec3 ReadColor(ref BitReader r)
    {
        float x = r.ReadByte() / 16f;
        float y = r.ReadByte() / 16f;
        float z = r.ReadByte() / 16f;
        return new NVec3(x, y, z);
    }

    /// <summary>
    /// Decode a <see cref="NetControl.SoundBundle"/> via the shared <see cref="XonoticGodot.Net.SoundWire"/> codec
    /// (the inverse of <c>ServerNet.WriteSound</c>) and raise <see cref="SoundReceived"/> per record — sample,
    /// origin, volume, attenuation, channel, the source entity net id, and the loop/stop flags.
    /// <see cref="BitReader.BadRead"/> guards a truncated/oversized record — bail like <see cref="DecodeEffect"/>.
    /// </summary>
    private void HandleSoundBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            XonoticGodot.Net.SoundWire rec = XonoticGodot.Net.SoundWire.Read(ref r);
            if (r.BadRead)
                return;
            SoundReceived?.Invoke(new SoundEvent(rec.Sample, rec.Origin, rec.Volume, rec.Attenuation,
                rec.Channel, rec.SourceNetId, rec.Loop, rec.Stop, rec.Pitch));
        }
    }

    private void HandleReliableBundle(ref BitReader r)
    {
        int count = r.ReadUShort();
        for (int i = 0; i < count; i++)
        {
            int notifId = r.ReadUShort();
            var type = (MsgType)r.ReadByte();
            string text = r.ReadString();
            int strN = r.ReadByte();
            var strs = strN > 0 ? new string[strN] : Array.Empty<string>();
            for (int s = 0; s < strN; s++) strs[s] = r.ReadString();
            int fltN = r.ReadByte();
            var flts = fltN > 0 ? new float[fltN] : Array.Empty<float>();
            for (int f = 0; f < fltN; f++) flts[f] = r.ReadFloat();
            if (r.BadRead)
                return;

            Notification? notif = (notifId >= 0 && notifId < Notifications.Count) ? Notifications.ById(notifId) : null;
            NotificationReceived?.Invoke(new NotificationEvent(notif, type, text, strs, flts));
        }
    }

    // =====================================================================================
    //  Helpers
    // =====================================================================================

    private static PlayerState DefaultVars()
    {
        // The movement-var seed the predictor carries before the first movevar block arrives. Built from the
        // shared deterministic defaults (NOT a lone hardcoded constant) so it already agrees with the server's
        // stock physics; once a MoveVarsBlock lands, HandleSnapshot refreshes this from the REPLICATED cvars via
        // VarsFromCvars() so a mid-match physics/mutator change is reflected in prediction.
        return ToPlayerVars(MovementParameters.Defaults);
    }

    /// <summary>
    /// Rebuild the predictor's <see cref="PlayerState"/> movement-var set from the live cvar store after the
    /// server's replicated <see cref="MoveVarsBlock"/> was applied — i.e. read the SERVER's
    /// sv_maxspeed/accelerate/airaccelerate/friction/stopspeed/jumpvelocity/gravity/stepheight rather than the
    /// hardcoded defaults. <c>MovementParameters.FromCvars()</c> consults <c>Api.Cvars</c>, which the
    /// just-run <c>MoveVarsBlock.Apply</c> stamped, so this picks up the authoritative physics.
    /// </summary>
    private static PlayerState VarsFromCvars() => ToPlayerVars(MovementParameters.FromCvars());

    /// <summary>Project the movement-var subset of a <see cref="MovementParameters"/> onto the owner-replicated
    /// <see cref="PlayerState"/> the reconciler carries (the MOVEVARS_* the predictor depends on). Local mapping so
    /// this stays in the net layer without touching the shared physics/stat types.</summary>
    private static PlayerState ToPlayerVars(in MovementParameters mp) => new()
    {
        MaxSpeed = mp.MaxSpeed,
        Accelerate = mp.Accelerate,
        AirAccelerate = mp.AirAccelerate,
        Friction = mp.Friction,
        StopSpeed = mp.StopSpeed,
        JumpVelocity = mp.JumpVelocity,
        Gravity = mp.Gravity,
        StepHeight = mp.StepHeight,
    };

    public void Dispose() => _transport.Dispose();
}
