using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The server cheat system — the Godot-free essence of server/cheats.qc (<c>CheatInit</c> /
/// <c>CheatsAllowed</c> / <c>CheatCommand</c> + the cheat accounting). It gates cheats behind
/// <c>sv_cheats</c> (snapshotted at init, like QC's <c>gamestart_sv_cheats</c>, so a mid-match cvar change
/// doesn't take effect until restart) and implements the simple, pure-logic cheats: <c>god</c>, <c>notarget</c>,
/// <c>noclip</c>, <c>fly</c>, <c>give</c>, <c>usetarget</c>/<c>killtarget</c>, <c>teleporttotarget</c>, the
/// <c>R00T</c> radius-nuke impulse, and the <c>GIVE_ALL</c> impulse. Each successful cheat bumps a
/// per-player and a global cheat counter (QC <c>cheatcount</c> / <c>cheatcount_total</c>), which the campaign
/// reads to refuse saving progress when cheats were used.
///
/// Faithful to QC: <see cref="Allowed"/> requires BOTH the snapshot AND the live <c>sv_cheats</c> (except the
/// <see cref="Player"/>-level <c>maycheat</c> override); dead players can't cheat unless <paramref name="ignoreDead"/>;
/// at <c>sv_cheats &gt;= 2</c> non-players (observers) may cheat too. A refused cheat command broadcasts the QC
/// "Player ... tried to use cheat '...'" notice (QC <c>CheatsAllowed</c>'s <c>logattempt</c> branch). The
/// remaining trace/particle/file-I/O cheats (the emergency teleport-to-random-location/info_autoscreenshot,
/// the clone/copybody, the speedrun personal-waypoint, the drag object-carry subsystem, and the
/// particle/<c>make</c>/<c>penalty</c> + race map-editor tooling) depend on engine services outside this
/// Godot-free core and are not part of it; the gameplay-state cheats above are complete.
/// </summary>
public sealed class Cheats
{
    /// <summary>QC <c>cheatcount_total</c>: cumulative successful cheats this map (campaign progress gate).</summary>
    public int CheatCountTotal { get; private set; }

    /// <summary>QC <c>gamestart_sv_cheats</c>: the <c>sv_cheats</c> value snapshotted at <see cref="Init"/>.</summary>
    public int GameStartCheats { get; private set; }

    /// <summary>Per-player successful-cheat count (QC the edict's <c>.cheatcount</c>).</summary>
    private readonly Dictionary<Player, int> _perPlayer = new();

    /// <summary>Diagnostics sink (QC bprint/sprint). Defaults to swallowing; a host/test can capture.</summary>
    public Action<string>? Log { get; set; }

    private void Echo(string s) => Log?.Invoke(s);

    /// <summary>QC <c>CheatInit</c>: snapshot <c>sv_cheats</c>; reset the counters for a fresh map.</summary>
    public void Init()
    {
        GameStartCheats = Cvars.Int("sv_cheats");
        CheatCountTotal = 0;
        _perPlayer.Clear();
    }

    /// <summary>How many successful cheats this player has used this map (QC <c>this.cheatcount</c>).</summary>
    public int CheatCountOf(Player p) => _perPlayer.TryGetValue(p, out int n) ? n : 0;

    private void AddCheats(Player p, int n)
    {
        if (n <= 0) return;
        CheatCountTotal += n;
        _perPlayer[p] = CheatCountOf(p) + n;
    }

    /// <summary>
    /// QC <c>CheatsAllowed</c>: is <paramref name="p"/> allowed to cheat right now? Requires both the snapshot
    /// and the live <c>sv_cheats</c> (or the player's <c>MayCheat</c> override). Dead players are refused unless
    /// <paramref name="ignoreDead"/>; observers (non-players) only at <c>sv_cheats &gt;= 2</c>.
    /// When refused at the final <c>sv_cheats==0</c> fall-through and <paramref name="logAttempt"/> is set,
    /// broadcasts the QC "Player ... tried to use cheat '<paramref name="cheatName"/>'" notice (QC's
    /// <c>bprintf</c> branch). The dead-player and observer guards refuse SILENTLY, as in Base.
    /// </summary>
    public bool Allowed(Player p, bool ignoreDead = false, bool logAttempt = false, string? cheatName = null,
        bool cloneImpulse = false)
    {
        // QC CheatsAllowed returns 0 SILENTLY on the dead-player and observer guards; only the
        // final sv_cheats==0 fall-through logs the attempt. Match that placement.
        if (!ignoreDead && p.IsDead) return false;
        if (GameStartCheats < 2 && (p.Flags & EntFlags.Client) == 0) return false; // observer guard
        // QC cheats.qc:66-68 — the CLONE_MOVING/CLONE_STANDING impulses are permitted INDEPENDENTLY of sv_cheats
        // while the player is still under their per-life clone budget (this.lip < sv_clones). lip counts the clones
        // already spawned this life.
        if (cloneImpulse && p.Lip < Cvars.Int("sv_clones")) return true;
        if (p.MayCheat) return true;
        if (GameStartCheats != 0 && Cvars.Int("sv_cheats") != 0) return true;
        return Refuse(p, logAttempt, cheatName);
    }

    /// <summary>
    /// QC <c>CheatsAllowed</c>'s refusal tail: when a player is not allowed to cheat and logging was requested,
    /// broadcast the attempt (QC <c>bprintf</c>); always returns false so callers can <c>return Refuse(...)</c>.
    /// </summary>
    private bool Refuse(Player p, bool logAttempt, string? cheatName)
    {
        if (logAttempt)
        {
            if (!string.IsNullOrEmpty(cheatName))
                Echo($"Player {p.NetName}^7 tried to use cheat '{cheatName}'");
            else
                Echo($"Player {p.NetName}^7 tried to use an unknown cheat");
        }
        return false;
    }

    /// <summary>
    /// QC <c>CheatCommand</c>: dispatch a <c>cheat</c>-class command (<paramref name="argv"/>[0] = name). Returns
    /// true if a cheat was attempted (allowed and handled), matching QC's "consumed" return. The caller routes a
    /// client's <c>cmd</c> here before the normal client-command table.
    /// </summary>
    public bool Command(Player p, string[] argv)
    {
        if (argv.Length == 0) return false;
        string verb = argv[0].ToLowerInvariant();
        switch (verb)
        {
            case "god":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                p.Flags ^= EntFlags.GodMode;
                Echo((p.Flags & EntFlags.GodMode) != 0 ? "godmode ON" : "godmode OFF");
                if ((p.Flags & EntFlags.GodMode) != 0) AddCheats(p, 1);
                return true;

            case "notarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                p.Flags ^= EntFlags.NoTarget;
                Echo((p.Flags & EntFlags.NoTarget) != 0 ? "notarget ON" : "notarget OFF");
                if ((p.Flags & EntFlags.NoTarget) != 0) AddCheats(p, 1);
                return true;

            case "noclip":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (p.MoveType != MoveType.Noclip) { p.MoveType = MoveType.Noclip; Echo("noclip ON"); AddCheats(p, 1); }
                else { p.MoveType = MoveType.Walk; Echo("noclip OFF"); }
                return true;

            case "fly":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (p.MoveType != MoveType.Fly) { p.MoveType = MoveType.Fly; Echo("flymode ON"); AddCheats(p, 1); }
                else { p.MoveType = MoveType.Walk; Echo("flymode OFF"); }
                return true;

            case "give":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                if (GiveItems(p, argv)) AddCheats(p, 1);
                return true;

            case "usetarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                FireNamedTarget(p, argv.Length > 1 ? argv[1] : "", killTarget: false);
                AddCheats(p, 1);
                return true;

            case "killtarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                FireNamedTarget(p, argv.Length > 1 ? argv[1] : "", killTarget: true);
                AddCheats(p, 1);
                return true;

            case "teleporttotarget":
                if (!Allowed(p, logAttempt: true, cheatName: verb)) return false;
                // QC: DID_CHEAT only when teleport_findtarget found a destination and the teleport ran.
                if (TeleportToTarget(p, argv.Length > 1 ? argv[1] : "")) AddCheats(p, 1);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// QC the <c>GIVE_ALL</c> impulse (impulse 99): equivalent to <c>give all</c>. Returns true when a cheat
    /// was applied (so the caller bumps the cheat count).
    /// </summary>
    public bool GiveAll(Player p)
    {
        if (!Allowed(p, logAttempt: true, cheatName: "impulse 99")) return false;
        bool ok = GiveItems(p, new[] { "give", "all" });
        if (ok) AddCheats(p, 1);
        return ok;
    }

    /// <summary>
    /// QC the <c>R00T</c> impulse (impulse 148, <c>CheatImpulse</c> CHIMPULSE_R00T): nuke a random living enemy.
    /// Picks a uniformly-random live enemy player from <paramref name="roster"/> (QC RandomSelection over
    /// <c>IS_PLAYER(it) &amp;&amp; !IS_DEAD(it) &amp;&amp; DIFF_TEAM(it, this)</c>), falling back to the caller
    /// when there is none, then spawns the rocket-explode effect + impact sound at the victim and applies
    /// <c>RadiusDamage(1000 dmg, 0 edge, 128 radius, 500 force, DEATH_CHEAT)</c>. Logs "404 Sportsmanship not
    /// found." and counts a cheat. <paramref name="roster"/> is the connected-client list (observers and dead
    /// players are filtered out here).
    /// </summary>
    public bool R00t(Player p, IReadOnlyList<Player> roster)
    {
        if (!Allowed(p, logAttempt: true, cheatName: "impulse 148")) return false;

        // QC: RandomSelection over every IS_PLAYER && !IS_DEAD && DIFF_TEAM enemy (reservoir-sample one uniformly).
        Entity victim = p;
        int seen = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (it.IsObserver || it.IsDead) continue; // QC IS_PLAYER(it) && !IS_DEAD(it)
            if (it.Team == p.Team) continue; // QC DIFF_TEAM(it, this): a.team != b.team (FFA: all 0 -> self-nuke)
            seen++;
            if (XonoticGodot.Common.Math.Prandom.RangeInt(0, seen) == 0)
                victim = it;
        }

        // QC: Send_Effect(EFFECT_ROCKET_EXPLODE) + sound(CH_SHOTS, SND_ROCKET_IMPACT) at the victim.
        EffectEmitter.Emit("ROCKET_EXPLODE", victim.Origin);
        WeaponSplash.ImpactSound(victim, "weapons/rocket_impact.wav");

        // QC: RadiusDamage(spawn at victim, this, 1000, 0, 128, NULL, NULL, 500, DEATH_CHEAT, DMG_NOWEP, victim).
        WeaponSplash.RadiusDamage(
            inflictor: victim, center: victim.Origin, damage: 1000f, edgeDamage: 0f, radius: 128f,
            attacker: p, deathType: 0, force: 500f, directHit: victim,
            deathTag: XonoticGodot.Common.Gameplay.Damage.DeathTypes.Cheat);

        Echo("404 Sportsmanship not found.");
        AddCheats(p, 1);
        return true;
    }

    /// <summary>
    /// QC the <c>teleporttotarget</c> cheat command (<c>CheatCommand</c> case): teleport the caller to a named
    /// teleport-destination target. QC spawns a transient <c>cheattriggerteleport</c>, sets <c>.target</c>,
    /// runs <c>teleport_findtarget</c>, and (when a destination was found) <c>Simple_TeleportPlayer</c>.
    /// </summary>
    private static bool TeleportToTarget(Player p, string targetName)
        => Teleporters.TeleportToTarget(p, targetName);

    // =============================================================================================
    // give (QC GiveItems — the cheat-side give parser, reduced to the gameplay-state slice)
    // =============================================================================================

    /// <summary>
    /// QC <c>GiveItems</c> (the cheat <c>give</c> command): now routes through the shared op-aware
    /// <see cref="XonoticGodot.Common.Gameplay.GiveItems"/> (T35) — <c>GiveItems(actor, 0, tokenize)</c> with
    /// <c>argv[0]=="give"</c> dropped — so <c>give all</c> / <c>give allweapons</c> / <c>give max 50 health</c> /
    /// <c>give &lt;weapon&gt;</c> all use the faithful grammar (the FALLTHROUGH aggregates, the operator prefixes)
    /// and populate BOTH weapon-ownership reps. Returns true if anything changed (QC <c>got</c> &gt; 0).
    /// </summary>
    private bool GiveItems(Player p, string[] argv)
        => XonoticGodot.Common.Gameplay.GiveItems.Apply(p, argv, 1, argv.Length) != 0;

    /// <summary>
    /// QC the <c>usetarget</c>/<c>killtarget</c> cheats: spawn a transient entity carrying the named target
    /// (in the <c>.target</c> or <c>.killtarget</c> slot), fire <see cref="MapMover.UseTargets"/> (which uses
    /// or removes the matched entities), then drop the transient.
    /// </summary>
    private static void FireNamedTarget(Player p, string targetName, bool killTarget)
    {
        if (string.IsNullOrEmpty(targetName) || Api.Services is null)
            return;
        Entity trigger = Api.Entities.Spawn();
        if (killTarget) trigger.KillTarget = targetName;
        else trigger.Target = targetName;
        MapMover.UseTargets(trigger, p, null);
        Api.Entities.Remove(trigger);
    }
}
