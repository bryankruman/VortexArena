using XonoticGodot.Common.Gameplay;
using XonoticGodot.Net;

namespace XonoticGodot.Game.Net;

/// <summary>
/// The top-level packet framing for the XonoticGodot client/server link, plus the build-parity gate.
///
/// Two layers of "message id" exist:
/// <list type="bullet">
///   <item><b>This file</b> — the <see cref="NetControl"/> byte that prefixes every datagram and says what
///         KIND of packet it is (handshake, a server snapshot frame, a client input frame, a bundle of
///         server events). It is the analogue of the DP top-level <c>svc_*</c>/<c>clc_*</c> framing.</item>
///   <item><see cref="NetMessageId"/> (XonoticGodot.Net) — the per-message id INSIDE a frame, dispatched by
///         <see cref="NetDispatcher"/> (Linked entity updates, Temp events, C2S input). The QC
///         <c>LinkedEntities</c>/<c>TempEntities</c>/<c>C2S_Protocol</c> registries.</item>
/// </list>
///
/// The transport (<see cref="NetTransport"/>) frames raw bytes on two ENet channels:
/// <see cref="ReliableChannel"/> for things that must arrive in order (the handshake, entity spawns/removes,
/// notifications, scores) and <see cref="UnreliableChannel"/> for the high-rate, loss-tolerant streams
/// (per-tick snapshots, input frames, fire-and-forget effects) — exactly the reliable/unreliable split the
/// networking spec calls for.
/// </summary>
public static class NetProtocol
{
    /// <summary>
    /// The wire protocol version. Bump on ANY incompatible change to the framing/serialization that the
    /// registry content hash would not catch (e.g. a change to <see cref="InputCommand.Serialize"/> or the
    /// snapshot layout). Mixed with the registry hashes into <see cref="BuildParity"/>.
    ///
    /// v2: the SoundBundle record gained a source entity net-id + loop/stop flags (<see cref="XonoticGodot.Net.SoundWire"/>)
    /// for DP's entity+channel looping-sound model (Arc beam loop, vehicle engines).
    /// v3: added the console string-command channel (<see cref="NetControl.ClientCommand"/> /
    /// <see cref="NetControl.ServerPrint"/>) — the DP <c>clc_stringcmd</c> / <c>svc_print</c> pair that lets the
    /// in-game console run gameplay commands against a remote server and print the reply.
    /// v4: the snapshot gained the <see cref="XonoticGodot.Net.ScoreInfoBlock"/> (QC ENT_CLIENT_SCORES_INFO) — the
    /// active per-mode score label/flag set + gametype/teamplay, sent before the per-player
    /// <see cref="XonoticGodot.Net.ScoreboardBlock"/> so a remote client's networked-column layout matches the server
    /// (without it the scoreboard block's layout hash disagreed and was silently dropped). The ScoreboardBlock
    /// row also gained the entcs name+team slice.
    /// v5: the C2S <see cref="InputCommand"/> gained a one-shot <see cref="InputCommand.Impulse"/> byte (QC
    /// usercmd.impulse) so a human on the net path can switch weapons / reload — the client samples weapon binds
    /// into it and the server dispatches it through the gated <c>impulse</c> command path (<c>WeaponImpulses</c>).
    /// v6: added the minigame session snapshot channel (<see cref="NetControl.MinigameState"/>) — the S2C push
    /// of a live <c>MinigameSession</c> (board/turn/winner/players + bespoke Pong state) to its participating
    /// peer(s), the C# stand-in for QC's per-entity minigame networking (ENT_CLIENT_MINIGAME). The <c>cmd
    /// minigame …</c> command itself rides the existing <see cref="NetControl.ClientCommand"/> channel.
    /// v7 (the SINGLE wave-A3 bump — T53/T57 additions ride it too):
    /// <list type="number">
    ///   <item>the snapshot's <see cref="XonoticGodot.Net.MoveVarsBlock"/> grew from 40 to 46 entries (the full
    ///         stats.qh MOVEVARS breadth: g_movement_highspeed(+_q3_compat), sv_gameplayfix_nudgeoutofsolid,
    ///         sv_wallclip, sv_nostep, sv_slick_applygravity — appended, prefix-stable);</item>
    ///   <item>a per-peer preset-RESOLVED physics block follows the global movevars block: one bool, then —
    ///         when true — a MoveVarsBlock vector resolved through g_physics_clientselect/cl_physics
    ///         (Physics_ClientOption, player.qc:18-42). A count-0 block clears the client's override. Hash-gated
    ///         per peer; always the bare false bool when client physics selection is off (the stock default);</item>
    ///   <item>RESERVED: T53's mode-stats block — a bool(+block) slot between the scores block and the entity
    ///         section of the snapshot (see the [A3 reserved] markers in ServerNet.BroadcastSnapshots /
    ///         ClientNet.HandleSnapshot). Absent until T53's snippets land (no wire bytes — doc-only);</item>
    ///   <item>RESERVED: T57's accuracy fields — appended at the END of the owner state (after spectateeStatus,
    ///         per the append-at-END lockstep contract; see the markers in ServerNet.WriteOwnerState and the
    ///         ClientNet owner read). Absent until T57's snippets land.</item>
    /// </list>
    /// Any new top-level frame kind starts at <see cref="NetControl"/> id 20 (Waypoints=19 is the last used).
    ///
    /// v8: added the match-clock channel (<see cref="NetControl.MatchState"/>) — a global S2C push of
    /// GAMESTARTTIME / TIMELIMIT / warmup so the TIMER panel works on the play path. Additive (old clients drop
    /// the unknown frame), but the version bump keeps the parity hash honest.
    ///
    /// v9: added the waypoint-sprite channel (<see cref="NetControl.Waypoints"/>) — per-peer team/rule-filtered
    /// waypoint sprites (CTF flags + player pings + objective markers) driving the 3D in-world sprite layer + the
    /// radar icons (the C# port of QC's ENT_CLIENT_WAYPOINT entities).
    /// </summary>
    public const uint ProtocolVersion = 9;

    /// <summary>Ordered, reliable ENet channel — handshake, spawns/removes, notifications, scores.</summary>
    public const int ReliableChannel = 0;

    /// <summary>Unreliable ENet channel — snapshots, input frames, fire-and-forget effects/temp-entities.</summary>
    public const int UnreliableChannel = 1;

    /// <summary>Number of ENet channels the host/client allocate.</summary>
    public const int ChannelCount = 2;

    /// <summary>
    /// The largest payload (bytes) we send on the <see cref="UnreliableChannel"/>. Godot's ENet host reports an
    /// MTU of 1392; an UNRELIABLE packet above it is sent as a lossy unreliable-fragment datagram (Godot logs a
    /// "Sending N bytes unreliably which is above the MTU" warning + a higher drop rate — a lost fragment loses
    /// the WHOLE packet). A snapshot only exceeds this on its initial full-baseline frame (every static map item
    /// at once, before the client's first ack lets the server delta-compress it) or after packet loss reverts the
    /// stream to full baselines — both must arrive, so <see cref="ServerNet.BroadcastSnapshots"/> promotes an
    /// oversized snapshot to the <see cref="ReliableChannel"/> (ENet fragments reliable packets losslessly).
    /// Kept a touch under the reported MTU to leave headroom for ENet's per-packet overhead.
    /// </summary>
    public const int MaxUnreliablePayload = 1350;

    /// <summary>
    /// Build-parity gate: a single hash mixing the protocol version with the content hashes of every
    /// registry whose ORDER the client and server must agree on (effects, notifications) — the analogue of
    /// QC's registry-hash handshake (registry_net.qh) that replaced the DP <c>csprogs.dat</c> push. A client
    /// and server with mismatched gameplay content (different effect/notification tables, or protocol) hash
    /// differently and are rejected at <see cref="NetControl.HandshakeRequest"/> time.
    ///
    /// FNV-1a-style fold of the constituent hashes so adding a registry later is a one-line change.
    /// </summary>
    public static uint BuildParity()
    {
        uint h = 2166136261u;
        h = Mix(h, ProtocolVersion);
        h = Mix(h, Effects.Hash);
        h = Mix(h, Notifications.Hash);
        // NetMessageId is a hand-authored enum today (not a registry); fold its highest assigned id so a
        // renumber is caught too. When it becomes source-generated this becomes Registry<NetMsg>.ContentHash.
        h = Mix(h, (uint)NetMessageId.Temp_Explosion);
        return h;
    }

    private static uint Mix(uint h, uint value)
    {
        // fold each byte of value through FNV-1a.
        for (int i = 0; i < 4; i++)
        {
            h ^= value & 0xFF;
            h *= 16777619u;
            value >>= 8;
        }
        return h;
    }
}

/// <summary>
/// The leading byte of every datagram — the kind of packet. Distinct from the per-message
/// <see cref="NetMessageId"/> (which identifies messages WITHIN a snapshot/event frame).
/// </summary>
public enum NetControl : byte
{
    None = 0,

    // ---- handshake (reliable channel) ----
    /// <summary>Client → server on connect: protocol/build-parity hash + the client's name + its public identity key.</summary>
    HandshakeRequest = 1,
    /// <summary>Server → client: accepted; carries the assigned entity id and the server tick rate.</summary>
    HandshakeAccept = 2,
    /// <summary>Server → client: rejected (build-parity mismatch / server full / failed auth); carries a reason string.</summary>
    HandshakeReject = 3,
    /// <summary>Server → client: build-parity passed; here is a random challenge — sign it with your identity key.</summary>
    HandshakeChallenge = 4,
    /// <summary>Client → server: the signature over the challenge (proves ownership of the public key). SessionAuth.</summary>
    HandshakeAuth = 5,

    // ---- gameplay frames ----
    /// <summary>Client → server (unreliable): a redundant tail of recent <see cref="InputCommand"/>s.</summary>
    InputFrame = 10,
    /// <summary>Server → client (unreliable): a world snapshot — acked input seq, server time, entity states.</summary>
    Snapshot = 11,
    /// <summary>Server → client (unreliable): a bundle of fire-and-forget events (effects, temp-entities).</summary>
    EventBundle = 12,
    /// <summary>Server → client (reliable): a bundle of must-arrive events (notifications, spawns, scores).</summary>
    ReliableBundle = 13,

    /// <summary>Server → client (unreliable): a bundle of fire-and-forget positional sounds (DP SV_StartSound) —
    /// see <c>ServerNet.FlushSounds</c> / <c>ClientNet.HandleSoundBundle</c>.</summary>
    SoundBundle = 14,

    // ---- console string commands (DP clc_stringcmd / svc_print) ----
    /// <summary>Client → server (reliable): a console command line to run on the server on the sender's behalf
    /// (the in-game console's gameplay commands — kill/say/team/…). DP <c>clc_stringcmd</c>.</summary>
    ClientCommand = 15,
    /// <summary>Server → client (reliable): a line of console output (a command reply / server notice) to print
    /// in the client console. DP <c>svc_print</c>.</summary>
    ServerPrint = 16,

    // ---- minigames (the C# stand-in for QC's per-entity ENT_CLIENT_MINIGAME networking) ----
    /// <summary>Server → client (reliable): a full minigame-session snapshot (board/turn/winner/players +
    /// bespoke Pong state, via <see cref="MinigameNetState.Encode"/>), sent to the session's participating peers
    /// when it changes. The client decodes it and drives the minigame board overlay + menu.</summary>
    MinigameState = 17,

    // ---- match clock (the C# stand-in for QC STAT(GAMESTARTTIME)/TIMELIMIT/WARMUP) ----
    /// <summary>Server → client (reliable): the global match-clock state — game start time, time limit, warmup
    /// stage + warmup limit, intermission/overtime — so the TIMER panel can count up/down on the play path.
    /// Broadcast to every accepted peer on change and ~1×/s (covers late joiners). Decoded into
    /// <see cref="ClientNet"/>'s match fields; unknown to old clients (dispatch falls through harmlessly).</summary>
    MatchState = 18,

    // ---- waypoint sprites (the C# port of QC's networked ENT_CLIENT_WAYPOINT entities) ----
    /// <summary>Server → client: the live waypoint sprites — gametype objectives (CTF flags, DOM points, KH
    /// keys…) + player pings (HERE/DANGER/HELPME) + deployed markers. Drives BOTH the 3D in-world sprite layer
    /// (icon/text + edge-arrow + health bar) and the radar icons. Sent per-peer (team/rule filtered) each network
    /// tick on the unreliable channel (positions move with carriers). Each entry = id + origin + team + sprite
    /// name + radar icon + color + health + fade + helpme + maxdist + hideable. Unknown to old clients.</summary>
    Waypoints = 19,
}
