// Port of common/mutators/mutator/itemstime/itemstime.qc (the SVQC it_times producer + sync feeds).

using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Items-time mutator (server backend) — port of the SVQC half of
/// common/mutators/mutator/itemstime/itemstime.qc. Tracks the absolute respawn time of the "timed" pickups
/// (the powerups Strength + Shield, Mega/Big Health, Mega/Big Armor, and an aggregate Superweapons slot) and
/// publishes them so the client HUD <c>ItemsTimePanel</c> can draw the respawn countdowns. Enabled by
/// <c>sv_itemstime</c> (xonotic-server.cfg default <b>1</b>; 2 also sends to alive players — a HUD-visibility
/// tier handled client-side, so the server-side producer is the same).
///
/// QC kept <c>float it_times[REGISTRY_MAX(Items) + 1]</c> (the +1 is the superweapons slot), updated as items
/// are picked up / scheduled to respawn and synced to clients via the <c>itemstime</c> net message. The port
/// computes the equivalent table from the live world items' <see cref="Entity.ScheduledRespawnTime"/> (set by
/// the item-respawn scheduler in ItemPickupRules) and exposes it as <see cref="CurrentTimes"/> keyed by the
/// HUD panel's item names — the host/net layer pushes it into <c>ItemsTimePanel.SetItemTimes</c> each frame.
///
/// Ported faithfully: the timed-item set (Item_ItemsTime_Allow: powerups + mega/big health+armor + the
/// superweapons aggregate), the absolute-respawn-time table, and the NEGATIVE "available now" encoding from
/// <c>Item_ItemsTime_UpdateTime</c> (when one copy of an item is already up, the time is sent as <c>-t</c> so
/// the panel shows "ready" while another copy is still on cooldown). The per-player visibility tiers
/// (sv_itemstime 1 vs 2: spectators-only vs also-alive-players) reduce to the client panel's own
/// <c>hud_panel_itemstime</c> gate in this single-view port — documented as a simplification.
/// </summary>
[Mutator]
public sealed class ItemstimeMutator : MutatorBase
{
    public ItemstimeMutator() => NetName = "itemstime";

    // QC: REGISTER_MUTATOR(itemstime, true) — always registered; sv_itemstime gates the producer. Modeling
    // IsEnabled on the cvar is equivalent (the only behavior is publishing the times, which sv_itemstime gates).
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("sv_itemstime") != 0f;

    /// <summary>
    /// The published respawn-time table (QC it_times), keyed by the HUD panel's item names
    /// (health_mega / health_big / armor_mega / armor_big / strength / shield / superweapons). A value &gt; time
    /// is the absolute respawn time; a value &lt; -1 is the "another copy available now" encoding; an item not on
    /// the map is absent (the panel hides it). The host/net layer feeds this to <c>ItemsTimePanel.SetItemTimes</c>.
    /// </summary>
    public IReadOnlyDictionary<string, float> CurrentTimes => _times;
    private readonly Dictionary<string, float> _times = new(System.StringComparer.Ordinal);

    private HookHandler<MutatorHooks.SvStartFrameArgs>? _onStartFrame;

    public override void Hook()
    {
        _onStartFrame ??= OnStartFrame;
        MutatorHooks.SvStartFrame.Add(_onStartFrame);
        Recompute(); // seed immediately on activation
    }

    public override void Unhook()
    {
        if (_onStartFrame is not null) MutatorHooks.SvStartFrame.Remove(_onStartFrame);
        _times.Clear();
    }

    // QC the producer re-syncs on reset_map_global / player spawn / connect; here we recompute each server
    // frame (cheap — a small scan) so CurrentTimes always reflects the live world for the per-frame HUD push.
    private bool OnStartFrame(ref MutatorHooks.SvStartFrameArgs args)
    {
        Recompute();
        return false;
    }

    /// <summary>
    /// QC the classname → HUD-panel-key map for the timed items (Item_ItemsTime_Allow's set). The world item
    /// entities carry these classnames (ItemSpawnFuncs); the superweapons aggregate is computed separately.
    /// </summary>
    private static readonly Dictionary<string, string> TimedItemClasses = new(System.StringComparer.Ordinal)
    {
        ["item_health_mega"] = "health_mega",
        ["item_health_big"]  = "health_big",
        ["item_armor_mega"]  = "armor_mega",
        ["item_armor_large"] = "armor_mega",   // alias
        ["item_armor25"]     = "armor_mega",   // alias
        ["item_armor_big"]   = "armor_big",
        ["item_strength"]    = "strength",
        ["item_shield"]      = "shield",
        ["item_invincible"]  = "shield",       // alias
    };

    /// <summary>
    /// Recompute <see cref="CurrentTimes"/> from the live world items. Mirrors QC's
    /// <c>Item_ItemsTime_SetTime</c> + <c>Item_ItemsTime_UpdateTime</c>: for each timed item key, take the
    /// minimum scheduled respawn time across all copies; if any copy is already up
    /// (<c>scheduledrespawntime &lt;= time</c>) encode the result as NEGATIVE ("another copy available now").
    /// Superweapon weapon-pickups feed the single aggregate "superweapons" slot.
    /// </summary>
    public void Recompute()
    {
        if (Api.Services is null) return;
        if (Api.Cvars.GetFloat("sv_itemstime") == 0f) { _times.Clear(); return; }

        float now = Api.Clock.Time;

        // Accumulator per key: (anyAvailable, minScheduled, hasMin, seen). hasMin distinguishes "no cooldown
        // copy recorded yet" from "min == 0" so the min isn't pinned at 0 by an available copy.
        var acc = new Dictionary<string, (bool avail, float minT, bool hasMin, bool seen)>(System.StringComparer.Ordinal);

        void Note(string key, Entity item)
        {
            if (item.IsFreed || (item.Flags & EntFlags.Item) == 0) return;
            float t = item.ScheduledRespawnTime;
            bool avail = t <= now; // a copy that is up / not on cooldown
            if (!acc.TryGetValue(key, out var a))
                a = (false, 0f, false, false);
            if (avail) a.avail = true;
            else if (!a.hasMin || t < a.minT) { a.minT = t; a.hasMin = true; }
            a.seen = true;
            acc[key] = a;
        }

        // Timed non-weapon items, scanned per known classname (exact-match FindByClass).
        foreach (var kv in TimedItemClasses)
            foreach (Entity e in Api.Entities.FindByClass(kv.Key))
                Note(kv.Value, e);

        // Superweapon weapon-pickups → the single aggregate slot (QC WEPSET_SUPERWEAPONS). The pickup
        // classname is "weapon_<netname>" (ItemSpawnFuncs SPAWNFUNC_WEAPON convention).
        foreach (Weapon w in Registry<Weapon>.All)
        {
            if (!w.IsSuperWeapon) continue;
            foreach (Entity e in Api.Entities.FindByClass("weapon_" + w.NetName))
                Note("superweapons", e);
        }

        _times.Clear();
        foreach (var kv in acc)
        {
            (bool avail, float minT, bool hasMin, bool seen) = kv.Value;
            if (!seen) continue;
            // QC Item_ItemsTime_UpdateTime: when a copy is available, send the min cooldown time NEGATED ("another
            // copy is up now"); else send the (positive) min cooldown time. If a copy is available and no other is
            // on cooldown, the time is 0 (everything is up).
            float t = hasMin ? minT : 0f;
            _times[kv.Key] = avail ? -t : t;
        }
    }
}
