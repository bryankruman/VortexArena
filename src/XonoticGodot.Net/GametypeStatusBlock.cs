// Port of qcsrc/common/gametypes/gametype/clanarena/sv_clanarena.qc (CA_count_alive_players → STAT(REDALIVE..PINKALIVE))
//       + qcsrc/common/gametypes/gametype/freezetag/sv_freezetag.qc (freezetag_count_alive_players)
//       + qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qc (kh_update_state → STAT(OBJECTIVE_STATUS))
//       + qcsrc/common/gametypes/gametype/survival/sv_survival.qc (SurvivalStatuses_SendEntity)
//       + qcsrc/server/elimination.qc (EliminatedPlayers_SendEntity)
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Net;

/// <summary>
/// Server→client replication of the per-mode round/objective HUD status (T53) — the C# successor to four QC
/// channels at once: the <c>STAT(REDALIVE..PINKALIVE)</c> alive counts (CA/FreezeTag mod icons), the
/// <c>STAT(OBJECTIVE_STATUS)</c> KeyHunt key pack, the <c>eliminatedPlayers</c> linked-entity bitfield (the
/// scoreboard row grey-out, server/elimination.qc) and the <c>survivalStatuses</c> linked entity (Survival's
/// own-role tag + the hunter-identity disclosure, sv_survival.qc).
///
/// <para><b>Personalized per recipient.</b> Two payloads differ per destination, exactly as in QC: the KeyHunt
/// pack ORs the slot of a key the RECIPIENT carries to 31 (kh_update_state's self override), and the Survival
/// hunter set is only written to a hunter destination — or to everyone once the round is over
/// (SurvivalStatuses_SendEntity's <c>STATUS_SEND_HUNTERS</c> rule). Hunter ids must NEVER ride the wire to a
/// prey/observer mid-round (the anti-cheat invariant), which is why the server serializes this block per peer
/// and gates on a per-peer <see cref="Hash"/> of the recipient's OWN bytes rather than sharing one block.</para>
///
/// <para><b>Snapshot framing.</b> The block sits between the scoreboard block and the delta-compressed entity
/// section, behind a bool gate written EVERY snapshot (steady state and all untracked modes = one false bool).
/// Eliminated players + hunters are id LISTS keyed on the snapshot's stable player net ids (the port networks
/// players by net id, not the QC maxclients bit slots). Value-change gating via <see cref="Hash"/> is
/// observationally identical to QC's stat delta-compression + SendFlags re-sends — DP stats are themselves only
/// transmitted when the value changes (the brief's round-handler cadence note).</para>
/// </summary>
public static class GametypeStatusBlock
{
    /// <summary>Which mode's payload the block carries (the QC <c>gametype.m_modicons</c> dispatch analogue).
    /// <see cref="None"/> is never written — untracked modes simply never send the block.</summary>
    public enum Kind : byte
    {
        None = 0,
        ClanArena = 1,
        FreezeTag = 2,
        KeyHunt = 3,
        Survival = 4,
    }

    /// <summary>
    /// Team color code (<see cref="Teams.Red"/>/Blue/Yellow/Pink = 4/13/12/9) → 1-based team INDEX
    /// (1 red .. 4 pink; 0 = none/observer). The wire deliberately carries indices, not color codes: the
    /// dormant ModIconsPanel KH decode (and QC's <c>myteam</c>) are index-shaped, and one convention on the
    /// wire avoids the SVQC-vs-CSQC code split QC bridges with its <c>−1</c>.
    /// </summary>
    public static int TeamIndexOf(int teamCode) => teamCode switch
    {
        Teams.Red => 1, Teams.Blue => 2, Teams.Yellow => 3, Teams.Pink => 4, _ => 0,
    };

    /// <summary>
    /// Serialize the personalized status block for <paramref name="viewer"/> (server side). Returns false —
    /// writing NOTHING — for any gametype this block doesn't track, so the caller sends only its false bool
    /// gate. <paramref name="netIdOf"/> supplies the stable per-player wire id (ServerNet.NetIdFor);
    /// <paramref name="roundStarted"/> is the world round handler's phase — part of the splice contract,
    /// reserved here: the per-mode gates come from each mode's OWN state (<see cref="KeyHunt.Phase"/> /
    /// <see cref="Survival.Handler"/>), which are the drivers that actually spawn keys / assign roles.
    /// </summary>
    public static bool Capture(BitWriter w, object? gametype, Player viewer,
        System.Collections.Generic.IReadOnlyList<Player> players,
        System.Func<Player, int> netIdOf, bool roundStarted)
    {
        _ = roundStarted;
        switch (gametype)
        {
            case ClanArena ca:
                WriteHeader(w, Kind.ClanArena, viewer, ca.TeamCount);
                WriteAliveCounts(w, ca.AliveCount);
                WriteIdList(w, players, netIdOf, ca.IsEliminatedPlayer); // QC ca_isEliminated → eliminatedPlayers
                return true;

            case FreezeTag ft:
                WriteHeader(w, Kind.FreezeTag, viewer, ft.TeamCount);
                WriteAliveCounts(w, ft.AliveCount);
                WriteIdList(w, players, netIdOf, ft.IsEliminated); // QC freezetag_isEliminated (frozen || dead)
                return true;

            case KeyHunt kh:
                WriteHeader(w, Kind.KeyHunt, viewer, kh.TeamCount);
                w.WriteULong(kh.PackKeyState(viewer)); // QC kh_update_state, incl. the per-recipient 31 slot
                return true;

            case Survival surv:
                WriteHeader(w, Kind.Survival, viewer, 0); // roleplay-FFA: no visible teams (QC USEPOINTS only)
                // Own role: 0 until the roles are live (the client hides the panel on 0 — QC's
                // GAMESTARTTIME/ROUNDSTARTTIME gate runs server-side here), else 1 prey / 2 hunter.
                w.WriteByte(surv.RoleAssigned ? (int)surv.StatusOf(viewer) : 0);
                if (surv.DisclosesHuntersTo(viewer))
                    WriteIdList(w, players, netIdOf, surv.IsHunter); // hunters know hunters; everyone at round end
                else
                    w.WriteByte(0); // mid-round prey/observer: NEVER leak the hunter ids (anti-cheat)
                WriteIdList(w, players, netIdOf, surv.IsEliminatedPlayer); // QC surv_isEliminated
                return true;

            default:
                return false; // untracked mode: nothing written
        }
    }

    /// <summary>The shared header: mode byte, the recipient's team INDEX (0 none/observer, 1..4 — QC
    /// <c>myteam</c>), and the active team count (2..4 for the team modes; 0 for Survival).</summary>
    private static void WriteHeader(BitWriter w, Kind kind, Player viewer, int teamCount)
    {
        w.WriteByte((byte)kind);
        w.WriteByte(viewer.IsObserver ? 0 : TeamIndexOf((int)viewer.Team));
        w.WriteByte(teamCount);
    }

    /// <summary>QC STAT(REDALIVE/BLUEALIVE/YELLOWALIVE/PINKALIVE): the four alive counts in FIXED
    /// red,blue,yellow,pink order (inactive teams read 0), each clamped to a byte.</summary>
    private static void WriteAliveCounts(BitWriter w, System.Func<int, int> aliveOf)
    {
        for (int i = 0; i < Teams.All.Length; i++)
            w.WriteByte(System.Math.Clamp(aliveOf(Teams.All[i]), 0, 255));
    }

    /// <summary>
    /// QC EliminatedPlayers_SendEntity / the SurvivalStatuses hunter bitfield, re-shaped as an id list (count
    /// byte + that many ushort net ids — the port has stable net ids, not maxclients bit slots). Observers are
    /// skipped (QC's INGAME / IS_PLAYER gates). Two passes over the roster keep the write allocation-free and
    /// the wire order roster-stable (deterministic bytes → stable <see cref="Hash"/>).
    /// </summary>
    private static void WriteIdList(BitWriter w, System.Collections.Generic.IReadOnlyList<Player> players,
        System.Func<Player, int> netIdOf, System.Func<Player, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < players.Count; i++)
            if (!players[i].IsObserver && predicate(players[i]))
                count++;
        if (count > 255) count = 255;
        w.WriteByte(count);

        int written = 0;
        for (int i = 0; i < players.Count && written < count; i++)
        {
            Player p = players[i];
            if (p.IsObserver || !predicate(p))
                continue;
            w.WriteUShort(netIdOf(p));
            written++;
        }
    }

    /// <summary>The decoded per-mode status (client side after <see cref="Deserialize"/>). Consumers:
    /// NetGame.UpdateModIcons (ModIconsPanel feed) + UpdateScoreboard (eliminated grey-out).</summary>
    public sealed class Decoded
    {
        public Kind Mode;
        /// <summary>The recipient's team as a 1-based INDEX (0 = none/observer) — feeds ModIconsPanel.MyTeam.</summary>
        public int MyTeamIndex;
        /// <summary>Active team count (2..4 for CA/FT/KH); 0 for Survival (roleplay-FFA).</summary>
        public int TeamCount;
        /// <summary>Alive counts in red,blue,yellow,pink order (QC STAT(REDALIVE..PINKALIVE)); CA/FT only.</summary>
        public int[] Alive = new int[4];
        /// <summary>Net ids the scoreboard greys out (QC the eliminatedPlayers bitfield); CA/FT/Survival.</summary>
        public System.Collections.Generic.HashSet<int> EliminatedNetIds = new();
        /// <summary>The KH OBJECTIVE_STATUS pack (4 × 5-bit slots) — feeds ModIconsPanel.ObjectiveStatus.</summary>
        public uint KeyState;
        /// <summary>The recipient's own Survival role: 0 none (pre-round/spectating), 1 prey, 2 hunter.</summary>
        public int MyStatus;
        /// <summary>Disclosed hunter net ids (own side mid-round; everyone once the round is over).</summary>
        public System.Collections.Generic.HashSet<int> HunterNetIds = new();
    }

    /// <summary>Read a status block (the inverse of <see cref="Capture"/>'s writes). Returns null on a bad
    /// read or an unknown mode byte — the snapshot stream is then corrupt and the caller bails on BadRead.</summary>
    public static Decoded? Deserialize(ref BitReader r)
    {
        int mode = r.ReadByte();
        if (mode < (int)Kind.ClanArena || mode > (int)Kind.Survival)
            return null; // unknown mode: build-parity should make this unreachable
        var d = new Decoded
        {
            Mode = (Kind)mode,
            MyTeamIndex = r.ReadByte(),
            TeamCount = r.ReadByte(),
        };
        switch (d.Mode)
        {
            case Kind.ClanArena:
            case Kind.FreezeTag:
                for (int i = 0; i < d.Alive.Length; i++)
                    d.Alive[i] = r.ReadByte();
                ReadIdList(ref r, d.EliminatedNetIds);
                break;
            case Kind.KeyHunt:
                d.KeyState = r.ReadULong();
                break;
            case Kind.Survival:
                d.MyStatus = r.ReadByte();
                ReadIdList(ref r, d.HunterNetIds);
                ReadIdList(ref r, d.EliminatedNetIds);
                break;
        }
        return r.BadRead ? null : d;
    }

    private static void ReadIdList(ref BitReader r, System.Collections.Generic.HashSet<int> into)
    {
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            int id = r.ReadUShort();
            if (r.BadRead)
                return;
            into.Add(id);
        }
    }

    /// <summary>
    /// FNV-1a over the serialized block bytes, folded to NONZERO: 0 is the per-peer "never sent" sentinel
    /// (PeerState.LastModeStatusHash), so the first snapshot of a live tracked mode always sends and a
    /// hash that happens to compute 0 can't alias it.
    /// </summary>
    public static uint Hash(System.ReadOnlySpan<byte> bytes)
    {
        uint h = 2166136261u;
        for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 16777619u; }
        return h == 0u ? 1u : h;
    }
}
