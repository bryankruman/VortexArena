using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>Primary vs secondary fire (QC weaponentity fire modes).</summary>
public enum FireMode { Primary = 0, Secondary = 1 }

/// <summary>Identifies which weapon-slot entity on an actor is acting (QC .entity weaponentities[]).</summary>
public readonly struct WeaponSlot
{
    public readonly int Index;
    public WeaponSlot(int index) => Index = index;
}

/// <summary>
/// Per-bot scratch state for a weapon's <see cref="Weapon.BotWantsSecondary"/> (QC <c>wr_aim</c>). QC's wr_aim
/// stamps a couple of fields on the firing actor (e.g. the rifle's <c>bot_secondary_riflemooth</c>) to persist
/// a primary/secondary preference across frames; this carries the equivalent per-bot state (owned by the bot's
/// brain) plus a fresh 0..1 RNG draw so a stateful wr_aim can roll its flip chance. Stateless wr_aims
/// (MachineGun) ignore it.
/// </summary>
public struct BotAimState
{
    /// <summary>QC rifle <c>.bot_secondary_riflemooth</c>: the bot currently prefers the secondary fire.</summary>
    public bool SecondaryToggle;

    /// <summary>A fresh QC <c>random()</c> draw in [0,1) for this decision (the brain re-rolls it each call).</summary>
    public float Random01;

    /// <summary>The deciding bot actor (QC wr_aim's <c>actor</c>), so an ammo-conditional wr_aim (e.g. the
    /// Vaporizer: rail primary while it has cells, else the Blaster-laser secondary) can read its resources.
    /// Set by the bot brain before each <see cref="Weapon.BotWantsSecondary"/> call; null in non-bot contexts.</summary>
    public Entity? Actor;
}

/// <summary>
/// Base weapon descriptor — one singleton instance per weapon type, enrolled into <see cref="Registries.Weapons"/>.
/// The C# successor to QuakeC's <c>CLASS(Weapon)</c> registrable (see common/weapons/weapon/*.qc).
/// Concrete weapons (Blaster, Vortex, …) subclass this; declared <c>partial</c> so the weapons module can
/// add shared firing helpers without editing this file.
/// </summary>
public abstract partial class Weapon : IRegistered
{
    public int RegistryId { get; set; }

    // identity
    public string NetName = "";        // stable ref name, e.g. "blaster"
    public string DisplayName = "";    // localized display name
    public int Impulse;
    public Vector3 Color;
    public int SpawnFlags;

    /// <summary>
    /// QC <c>ammo_type</c> (the weapon's <c>.m_ammo</c> ATTRIB, RES_*): the resource this weapon consumes, or
    /// <see cref="ResourceType.None"/> for ammo-less weapons (Blaster/Porto/Tuba/Fireball). Concrete weapons
    /// set it in their constructor. Lifted onto the base (was a shadowing per-subclass field) so the central
    /// <see cref="Weapons"/> registry can map NetName→ammo-type for random-start ammo + pickup logic.
    /// </summary>
    public ResourceType AmmoType = ResourceType.None;

    // model/asset names (resolved by the model/asset services)
    public string? ViewModel;
    public string? WorldModel;
    public string? ItemModel;

    public string RegistryName => NetName;

    /// <summary>
    /// QC MENUQC <c>describe()</c> (the per-weapon METHOD, e.g. electro.qc:753 — the multi-paragraph weapon-guide
    /// prose shown in the in-menu Guide's Weapons topic: what primary/secondary/combo do, ammo, and a tactical
    /// tip). <c>null</c> = no guide text ported yet for this weapon (the Guide falls back to a generic note).
    /// Plain newline-separated paragraphs (the QC %s name substitutions are pre-filled with the literal names).
    /// </summary>
    public virtual string? GuideDescription => null;

    // --- zoom / scope (CSQC view + reticle, view.qc IsZooming + crosshair.qc DrawReticle) -----------

    /// <summary>
    /// QC <c>w_reticle</c> (the CSQC weapon ATTRIB, e.g. vortex.qh <c>"gfx/reticle_nex"</c>): the weapon-specific
    /// zoom "scope" overlay image drawn full-screen while zoomed with this weapon. <c>null</c> = no scope (most
    /// weapons). Read client-side by the reticle overlay; the server ignores it.
    /// </summary>
    public virtual string? Reticle => null;

    /// <summary>
    /// QC <c>w_crosshair</c> (the per-weapon ATTRIB, e.g. hook.qh <c>"gfx/crosshairhook"</c>): the weapon's own
    /// crosshair image, drawn instead of the numbered <c>gfx/crosshair&lt;N&gt;</c> art when
    /// <c>crosshair_per_weapon</c> is on (QC <c>wcross_name = e.w_crosshair</c>). <c>null</c> = use the numbered
    /// crosshair. Carried on the weapon to mirror Base's ATTRIB; read client-side by the crosshair panel.
    /// </summary>
    public virtual string? Crosshair => null;

    /// <summary>
    /// QC <c>w_crosshair_size</c> (the per-weapon ATTRIB, e.g. hook.qh <c>0.5</c>): the per-weapon crosshair size
    /// multiplier applied on top of <c>crosshair_size</c> when <c>crosshair_per_weapon</c> is on (QC
    /// <c>wcross_resolution *= e.w_crosshair_size</c>). Default <c>1</c>.
    /// </summary>
    public virtual float CrosshairSize => 1f;

    /// <summary>
    /// QC <c>wr_zoomdir</c> / <c>wr_zoom</c> (vortex.qc:353/346 — the <c>button_attack2 &amp;&amp; !secondary</c>
    /// predicate): does holding ATTACK2 zoom the view with this weapon right now? Drives BOTH the FOV zoom
    /// (view.qc <c>IsZooming</c>) and the scope reticle (<c>DrawReticle</c>'s <c>wep_zoomed</c>). Default
    /// <c>false</c> — ATTACK2 is an ordinary secondary fire. The Vortex returns <c>true</c> while
    /// <c>g_balance_vortex_secondary</c> is 0 (the stock default, where secondary == zoom, not a fire mode).
    /// </summary>
    public virtual bool ZoomOnSecondary => false;

    /// <summary>QC <c>WEP_FLAG_SUPERWEAPON</c>: a timed superweapon (Vaporizer/Fireball/…) — held only while the
    /// Superweapon status effect lasts.</summary>
    public bool IsSuperWeapon => (SpawnFlags & WeaponFlags.SuperWeapon) != 0;

    /// <summary>
    /// QC <c>weaponreplace</c> WEP_CVAR (the per-weapon <c>g_balance_&lt;netname&gt;_weaponreplace</c> string,
    /// default empty / NONE): a server-configured replacement token list applied to this weapon's map spawns by
    /// <c>W_Apply_Weaponreplace</c> (server/weapons/spawning.qc:13). Empty = no replacement (spawn as-is); a
    /// single <c>"0"</c> token deletes the spawn. Read live from the cvar store so a server cfg takes effect.
    /// </summary>
    public string WeaponReplace
    {
        get
        {
            if (Api.Services is null) return "";
            return Api.Cvars.GetString($"g_balance_{NetName}_weaponreplace");
        }
    }

    // --- behavior hooks (override in concrete weapons) ---
    /// <summary>Main fire/think driver (QC wr_think).</summary>
    public virtual void WrThink(Entity actor, WeaponSlot slot, FireMode fire) { }

    /// <summary>
    /// QC <c>wr_aim</c> — the per-weapon bot fire-button selection. Given the distance to the bot's enemy and
    /// the bot's skill, decide whether this weapon's bot should press the SECONDARY fire (ATCK2) instead of the
    /// primary (ATCK) for the shot it has already decided to take this frame. Default <c>false</c> (bots fire
    /// primary only). Weapons whose secondary is range-useful (e.g. the MachineGun's long-range no-spread burst)
    /// override this; see machinegun.qc:wr_aim.
    ///
    /// <para><paramref name="ctx"/> carries the per-bot wr_aim state (the QC fields a weapon's wr_aim stamps on
    /// the actor, e.g. the rifle's <c>bot_secondary_riflemooth</c> toggle) plus a 0..1 RNG draw, so a stateful
    /// wr_aim can persist its primary/secondary preference across frames.</para>
    /// </summary>
    public virtual bool BotWantsSecondary(float enemyDistance, float skill, ref BotAimState ctx) => false;

    /// <summary>
    /// QC <c>wr_aim</c> projectile-speed override for the bot's shot lead. QC's wr_aim calls
    /// <c>bot_aim(actor, …, spd, …)</c> with a per-weapon speed; the brain leads the target by that speed. Most
    /// weapons just lead by their projectile's actual launch speed (the default = <paramref name="defaultSpeed"/>,
    /// the brain's read of the weapon's primary-fire speed cvar). The Devastator overrides this to lead as if the
    /// rocket flew much faster, "simulating rocket guide" (devastator.qc:351-355). Return 0 for hitscan/no-lead.
    /// </summary>
    public virtual float BotAimShotSpeed(float defaultSpeed) => defaultSpeed;

    /// <summary>
    /// QC <c>wr_aim</c>'s <c>shot_accurate</c> argument to <c>bot_aim</c> (the fire-deviation cone tightness):
    /// <c>null</c> = use the brain's default (hitscan ⇒ accurate, projectile ⇒ relaxed). The Devastator returns
    /// <c>false</c> when its rockets are guidable (<c>guiderate &lt; 50</c>) — "no need to fire with high accuracy
    /// on large distances if rockets can be guided" (devastator.qc:356-357).
    /// </summary>
    public virtual bool? BotAimAccurate() => null;

    /// <summary>
    /// QC <c>wr_aim</c>'s auto-detonation decision (the skill ≥ 2 block of devastator.qc:360-450): decide whether
    /// the bot should press SECONDARY this frame to remote-detonate its in-flight projectiles, by predicting the
    /// splash damage to self / teammates / enemies (now vs. a short look-ahead). Default <c>false</c> (no weapon
    /// auto-detonates). The Devastator implements the full predicted-damage heuristic + the skill ≥ 7 self-kill
    /// veto. When this returns true the brain also suppresses the primary fire (QC "don't fire a new shot at the
    /// same time"). <paramref name="shouldAttack"/> is the brain's <c>bot_shouldattack</c> filter (actor, target).
    /// </summary>
    public virtual bool BotWantsDetonate(
        Entity actor, WeaponSlot slot, float skill,
        System.Collections.Generic.IEnumerable<Entity> targets,
        System.Func<Entity, Entity, bool> shouldAttack) => false;

    /// <summary>Per-actor setup when the weapon becomes active (QC wr_setup).</summary>
    public virtual void WrSetup(Entity actor, WeaponSlot slot) { }

    /// <summary>Called when the player switches away from this weapon (QC wr_gonethink). A weapon may fire/reset any state held mid-action.</summary>
    public virtual void WrGoneThink(Entity actor, WeaponSlot slot) { }

    /// <summary>Called when the player dies while holding this weapon (QC wr_playerdeath). A weapon may fire/reset any state held mid-action.</summary>
    public virtual void WrPlayerDeath(Entity actor, WeaponSlot slot) { }

    /// <summary>Called when the player is reset (round restart, etc.) (QC wr_resetplayer). A weapon must clear any per-player state held on the slot.</summary>
    public virtual void WrResetPlayer(Entity actor, WeaponSlot slot) { }

    /// <summary>
    /// Seed this weapon's balance block from the <c>g_balance_*</c> cvars (QC W_PROPS / WEP_CVAR). Called once
    /// at registration (stock fallbacks, before any config loads) and again by <see cref="Weapons.ConfigureAll"/>
    /// after the <c>.cfg</c> tree loads, so an alternate balance set (XPM/overkill/instagib/…) takes effect.
    /// Concrete weapons override this and assign their balance struct via <see cref="Bal"/> with the stock value
    /// as the fallback.
    /// </summary>
    public virtual void Configure() { }

    // --- balance-cvar reads (QC WEP_CVAR / autocvar_g_balance_*) ----------------------------------
    // These mirror QC's autocvar reads: the value comes from the live cvar store (seeded by bal-wep-*.cfg via
    // the config interpreter) and falls back to the stock number when the cvar is unset, so the port keeps
    // authentic Xonotic balance even before any config is loaded (bare unit test) and honors balance variants
    // once one is. "Unset" is the empty-string case, kept distinct from a genuine "0".

    /// <summary>Read a float balance cvar, or <paramref name="fallback"/> (the stock value) if it's unset.</summary>
    protected static float Bal(string cvar, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(cvar);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(cvar);
    }

    /// <summary>Read a bool balance cvar (<c>cvar != 0</c>), or <paramref name="fallback"/> if it's unset.</summary>
    protected static bool BalBool(string cvar, bool fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(cvar);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(cvar) != 0f;
    }

    /// <summary>Read an int balance cvar (truncated), or <paramref name="fallback"/> if it's unset.</summary>
    protected static int BalInt(string cvar, int fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(cvar);
        return string.IsNullOrEmpty(s) ? fallback : (int)Api.Cvars.GetFloat(cvar);
    }
}

/// <summary>
/// Base pickup/item descriptor — one singleton per item type, enrolled into <see cref="Registries.Items"/>.
/// Successor to QuakeC's <c>CLASS(Pickup)</c> (common/items/*).
/// </summary>
public abstract partial class Pickup : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public string? Model;
    public string RegistryName => NetName;

    /// <summary>Attempt to give this item to a player touching the world item entity (QC give logic). Returns true if taken.</summary>
    public virtual bool GiveTo(Entity player, Entity worldItem) => false;
}

/// <summary>
/// Base mutator — a gameplay modifier that subscribes to hook chains while enabled.
/// Successor to QuakeC's REGISTER_MUTATOR + MUTATOR_HOOKFUNCTION (common/mutators/*).
/// </summary>
public abstract partial class MutatorBase : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string RegistryName => NetName;

    /// <summary>
    /// QC <c>.bool m_added</c> (common/mutators/base.qh): whether this mutator's hooks are currently
    /// subscribed. Guards <see cref="MutatorActivation.Add"/> / <see cref="MutatorActivation.Remove"/> so a
    /// double-add (e.g. a re-Boot, or a host that re-applies the loadout) is idempotent, exactly as QC's
    /// <c>Mutator_Add</c> short-circuits on <c>mut.m_added</c>.
    /// </summary>
    public bool Added { get; internal set; }

    /// <summary>Whether this mutator is currently active (QC mutator enable-expr <c>mutatorcheck()</c>).</summary>
    public virtual bool IsEnabled => false;

    /// <summary>Subscribe hooks (called when the mutator is enabled — QC the MUTATOR_ADDING branch).</summary>
    public virtual void Hook() { }

    /// <summary>Unsubscribe hooks (called when the mutator is disabled — QC the MUTATOR_REMOVING branch).</summary>
    public virtual void Unhook() { }

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(&lt;mut&gt;, BuildMutatorsString)</c> (the per-mutator hook that does
    /// <c>M_ARGV(0, string) = strcat(M_ARGV(0, string), ":Tag")</c>). The C# successor to the
    /// <c>BuildMutatorsString</c> hook chain: append this mutator's colon-delimited machine token (e.g.
    /// <c>":Vampire"</c>) to the accumulator <paramref name="s"/> and return it. Used to build the
    /// <c>:gameinfo:mutators:LIST</c> event-log line / the server-browser mutators field. Default: no
    /// contribution (a mutator that doesn't hook BuildMutatorsString).
    /// </summary>
    public virtual string BuildMutatorsString(string s) => s;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(&lt;mut&gt;, BuildMutatorsPrettyString)</c>: append this mutator's
    /// human-readable token (e.g. <c>", Vampire"</c>) to <paramref name="s"/> and return it — the leading
    /// <c>", "</c> is stripped once by the caller after the chain runs (QC <c>substring(..., 2, ...)</c>).
    /// Used for the map-info "modifications" line shown to joining clients. Default: no contribution.
    /// </summary>
    public virtual string BuildMutatorsPrettyString(string s) => s;

    /// <summary>
    /// QC <c>MUTATOR_HOOKFUNCTION(&lt;mut&gt;, SetModname)</c> (server/world.qc:1090): override the server's
    /// <c>modname</c> serverinfo key. A mutator that constitutes a full "mod experience" (instagib, overkill, NIX)
    /// returns true and overwrites the modname string. Default: no override (pass through unchanged).
    /// The first mutator to return true wins (matches QC CBC_ORDER_ANY early-exit on return true).
    /// </summary>
    public virtual (string name, bool overridden) SetModname(string name) => (name, false);
}

/// <summary>
/// Base gametype descriptor (DM, CTF, …). Successor to QuakeC's REGISTER_GAMETYPE (common/gametypes/*).
/// </summary>
public abstract partial class GameType : IRegistered
{
    public int RegistryId { get; set; }
    public string NetName = "";
    public string DisplayName = "";
    public bool TeamGame;
    public string RegistryName => NetName;

    public virtual void OnInit() { }

    /// <summary>
    /// Tear down every hook this gametype subscribed to the global chains (<see cref="MutatorHooks"/>,
    /// <c>Combat.Death</c>, etc.) when it activated — the symmetric counterpart to each gametype's
    /// <c>Activate()</c>, mirroring <see cref="MutatorActivation.DeactivateAll"/> for mutators. The base is a
    /// no-op (DM/TDM and the objective modes override it to remove their handlers); every concrete gametype
    /// overrides it and guards on its own "was I subscribed?" flag so it is safe to call even when the
    /// gametype never activated.
    ///
    /// This is what lets a gametype's hooks be dropped on a progs reload / registry reset (so a CTS map's
    /// Shotgun-only PlayerSpawn/PlayerPreThink handlers can't leak onto a subsequently-booted DM world) and
    /// when a live world switches gametypes (a campaign level change, a gametype vote). Without a virtual
    /// here the global hook chains retained handlers bound to a discarded gametype instance.
    /// </summary>
    public virtual void Deactivate() { }

    /// <summary>
    /// QC <c>WinningConditionHelper_equality</c> (server/scores.qc:500/537): are the top two contenders
    /// currently <em>tied</em> on this gametype's primary win key? The overtime/sudden-death cascade
    /// (server/world.qc <c>WinningCondition_Scores</c> → <c>GetWinningCode</c>) routes a tie at the time/score
    /// limit into overtime instead of declaring a draw. The base returns <c>false</c> (objective-latched modes
    /// — Onslaught/Assault/Nexball latch a single winning team, and the round-based modes ClanArena/FreezeTag
    /// are out of T42 scope — never report a tie); the timed score modes override it to compare their top two
    /// players/teams on the primary score:
    ///   • FFA score modes (DM/Mayhem/Duel/Keepaway) — top two players' primary score equal;
    ///   • team score modes (TDM/CTF/Dom/KH/TeamMayhem/TeamKeepaway) — top two teams' primary score equal.
    /// <paramref name="roster"/> is the current player list (the FFA modes scan it; team modes read GameScores).
    /// </summary>
    public virtual bool ReportsTie(System.Collections.Generic.IReadOnlyList<Player> roster) => false;

    /// <summary>Rebuild this gametype's OBJECTIVE waypoint sprites (flags / control points / keys …) into
    /// <paramref name="into"/> each server tick — the C# successor to the server's persistent
    /// <c>WaypointSprite_*</c> objectives. (Transient, derived from live gametype state; player pings live in the
    /// persistent <see cref="Waypoints.WaypointSprites"/> manager instead.) The net layer merges both, filters per
    /// peer, and feeds the radar + the 3D in-world sprite layer. Default: none (DM/TDM have no objectives).</summary>
    public virtual void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into) { }

    /// <summary>
    /// QC the shared body of the <c>MUTATOR_HOOKFUNCTION(&lt;mode&gt;, ClientDisconnect)</c> /
    /// <c>MakePlayerObserver</c> hooks (e.g. <c>ctf_RemovePlayer</c>): a player who leaves play — disconnects or
    /// becomes a spectator — must relinquish any objective they hold (a carried flag/ball/key) and have every
    /// objective entity that still back-references them (pass sender/target, dropper, …) cleared so it can never
    /// dangle pointing at a gone player. Default: none (DM/TDM hold no per-player objective state); objective
    /// modes override it. Dispatched once on disconnect and once when a player is forced to observer.
    ///
    /// NAMING NOTE: this is <c>OnPlayerRemoved</c> (not the QC verb <c>RemovePlayer</c>) to avoid colliding with
    /// the unrelated per-mode <c>RemovePlayer(Player)</c> state-dict cleanups on Cts/Race/Survival/LMS — those are
    /// orphaned helpers with different semantics, not this leave-play objective hook.
    /// </summary>
    public virtual void OnPlayerRemoved(Player player) { }
}
