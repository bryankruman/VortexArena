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

    // --- zoom / scope (CSQC view + reticle, view.qc IsZooming + crosshair.qc DrawReticle) -----------

    /// <summary>
    /// QC <c>w_reticle</c> (the CSQC weapon ATTRIB, e.g. vortex.qh <c>"gfx/reticle_nex"</c>): the weapon-specific
    /// zoom "scope" overlay image drawn full-screen while zoomed with this weapon. <c>null</c> = no scope (most
    /// weapons). Read client-side by the reticle overlay; the server ignores it.
    /// </summary>
    public virtual string? Reticle => null;

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

    // --- behavior hooks (override in concrete weapons) ---
    /// <summary>Main fire/think driver (QC wr_think).</summary>
    public virtual void WrThink(Entity actor, WeaponSlot slot, FireMode fire) { }

    /// <summary>Per-actor setup when the weapon becomes active (QC wr_setup).</summary>
    public virtual void WrSetup(Entity actor, WeaponSlot slot) { }

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

    /// <summary>Rebuild this gametype's OBJECTIVE waypoint sprites (flags / control points / keys …) into
    /// <paramref name="into"/> each server tick — the C# successor to the server's persistent
    /// <c>WaypointSprite_*</c> objectives. (Transient, derived from live gametype state; player pings live in the
    /// persistent <see cref="Waypoints.WaypointSprites"/> manager instead.) The net layer merges both, filters per
    /// peer, and feeds the radar + the 3D in-world sprite layer. Default: none (DM/TDM have no objectives).</summary>
    public virtual void CollectWaypoints(System.Collections.Generic.List<Waypoints.WaypointSprite> into) { }
}
