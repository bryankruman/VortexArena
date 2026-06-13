// Port of Base/.../qcsrc/common/mutators/mutator/status_effects/* (status_effects.qh networking:
//      ENT_CLIENT_STATUSEFFECTS, StatusEffects_Write/Send, STATUSEFFECT_FLAG_PERSISTENT/ACTIVE,
//      StatusEffects_update; sv_status_effects.qc m_apply/m_remove/m_tick dirty-marking).
using System.Collections.Generic;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The status-effect networking flags — the C# successor to QC's <c>STATUSEFFECT_FLAG_*</c>
/// (status_effects/all.qh:20-24). Stored per active effect and networked alongside its timer so the client
/// can drive the burning/frozen overlays.
/// </summary>
[System.Flags]
public enum StatusEffectFlags
{
    /// <summary>QC STATUSEFFECT_FLAG_ACTIVE (BIT 0): the effect is currently applied (set on m_apply).</summary>
    Active = 1 << 0,

    /// <summary>
    /// QC STATUSEFFECT_FLAG_PERSISTENT (BIT 1): the effect is being granted passively (it does not time out
    /// while persistent). Burning/frozen overlays read this so they persist on the client. Set by a def's
    /// <c>m_persistent</c> in <c>m_tick</c>; a persistent effect skips removal-on-timeout.
    /// </summary>
    Persistent = 1 << 1,
}

/// <summary>The removal cause passed to a status-effect removal (QC <c>STATUSEFFECT_REMOVE_*</c>, all.qh:26-31).</summary>
public enum StatusEffectRemoval
{
    /// <summary>QC STATUSEFFECT_REMOVE_NORMAL: regular removal (plays the removal sound unless persistent).</summary>
    Normal = 0,
    /// <summary>QC STATUSEFFECT_REMOVE_TIMEOUT: the effect's timer elapsed.</summary>
    Timeout = 1,
    /// <summary>QC STATUSEFFECT_REMOVE_CLEAR: forced removal with no additional mechanics.</summary>
    Clear = 2,
}

/// <summary>
/// A status effect kind (QC common/mutators/mutator/status_effects + buffs/powerups). The full QC
/// <c>StatusEffects</c> registry: the core debuffs (frozen/burning/spawnshield/stunned/superweapon/webbed),
/// the powerups (strength/shield/speed/invisibility — <c>Buff</c> derives from <c>StatusEffect</c>), and the
/// buff set. Registered into <see cref="StatusEffectsCatalog"/> via RegisterAll (self-registering).
/// </summary>
public sealed class StatusEffectDef : IRegistered
{
    public int RegistryId { get; set; }
    public string Name = "";
    public string RegistryName => Name;
    public bool IsBuff;            // a buff (BuffsMutator-offerable) vs a core effect/powerup
    public bool Hidden;            // QC m_hidden — no HUD element (spawnshield/stunned/webbed)
    public float Lifetime;         // QC m_lifetime — default duration cap (0 = none)
    public string? Model;          // buff model / drop model
    public StatusEffectDef(string name, bool isBuff = false) { Name = name; IsBuff = isBuff; }
}

/// <summary>One active effect instance on an entity (with expiry + optional source/strength).</summary>
public struct ActiveStatusEffect
{
    public int DefId;
    public float ExpireTime;       // engine time when it ends (<=0 => permanent)
    public float Strength;         // effect magnitude (e.g. burn dps, buff level)
    public Entity? Source;         // who applied it (for kill credit)
    /// <summary>QC <c>statuseffect_flags[id]</c>: the ACTIVE/PERSISTENT bitmap networked with the timer.</summary>
    public StatusEffectFlags Flags;
}

/// <summary>The status-effect system: apply/remove/query + a per-frame tick (burn damage, expiry, buff timers).</summary>
public static class StatusEffectsCatalog
{
    public static IReadOnlyList<StatusEffectDef> All => Registry<StatusEffectDef>.All;
    public static StatusEffectDef? ByName(string name) => Registry<StatusEffectDef>.ByName(name);
    public static int Count => Registry<StatusEffectDef>.Count;

    // Well-known effects (resolved after RegisterAll).
    public static StatusEffectDef? Frozen => ByName("frozen");
    public static StatusEffectDef? Burning => ByName("burning");
    public static StatusEffectDef? SpawnShield => ByName("spawnshield");
    public static StatusEffectDef? Stunned => ByName("stunned");
    public static StatusEffectDef? Superweapon => ByName("superweapon");
    public static StatusEffectDef? Webbed => ByName("webbed");

    /// <summary>
    /// Mirror of the QC <c>StatusEffects</c> registry (REGISTER_STATUSEFFECT across status_effect/, powerups/,
    /// buffs/, monsters/spider). The buffs carry <see cref="StatusEffectDef.IsBuff"/> so BuffsMutator offers
    /// only those; powerups + core debuffs do not. Names match the XonoticGodot consumers (ItemPickupRules powerup
    /// timers use "shield"/"superweapon"; BuffsMutator + DeathTypes use the "buff_" prefix; MonsterFramework
    /// uses "webbed"/"shield"/"spawnshield").
    /// </summary>
    public static void RegisterAll()
    {
        void R(StatusEffectDef d) => Registry<StatusEffectDef>.Register(d);

        // --- core debuffs / status (status_effect/*.qh) ---
        R(new StatusEffectDef("frozen") { Hidden = true });
        R(new StatusEffectDef("burning") { Hidden = true });
        // STATUSEFFECT_SpawnShield (spawnshield.qh): hidden, 10s cap — post-spawn damage protection.
        R(new StatusEffectDef("spawnshield") { Hidden = true, Lifetime = 10f });
        // STATUSEFFECT_Stunned (stunned.qh): hidden, 10s cap — shockwave/onslaught stun.
        R(new StatusEffectDef("stunned") { Hidden = true, Lifetime = 10f });
        // STATUSEFFECT_Superweapon (superweapons.qh): superweapon ammo window (QC netname "superweapons";
        // XonoticGodot consumers use the singular "superweapon").
        R(new StatusEffectDef("superweapon"));
        // STATUSEFFECT_Webbed (monsters/monster/spider.qh): spider-web movement slow.
        R(new StatusEffectDef("webbed") { Hidden = true });

        // --- powerups (mutators/mutator/powerups/powerup/*.qh — Buff:StatusEffect, but not BuffsMutator buffs) ---
        R(new StatusEffectDef("strength"));
        R(new StatusEffectDef("shield"));      // QC ShieldStatusEffect netname "invincible"; consumers use "shield"
        R(new StatusEffectDef("speed"));
        R(new StatusEffectDef("invisibility"));

        // --- buffs (mutators/mutator/buffs/buff/*.qh): the exact QC set, offered by BuffsMutator ---
        foreach (var b in new[] { "ammo", "bash", "disability", "flight", "inferno", "jump", "luck",
                                  "magnet", "medic", "resistance", "swapper", "vampire", "vengeance" })
            R(new StatusEffectDef("buff_" + b, isBuff: true));

        Registry<StatusEffectDef>.Sort();
    }

    public static bool Has(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects) if (s.DefId == def.RegistryId) return true;
        return false;
    }

    public static void Apply(Entity e, StatusEffectDef def, float duration, float strength = 1f, Entity? source = null)
    {
        float now = Api.Services != null ? Api.Clock.Time : 0f;
        // QC m_apply: automatically enable the ACTIVE flag when applied (sv_status_effects.qc:34).
        // refresh if already present
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId == def.RegistryId)
            {
                var cur = e.StatusEffects[i];
                cur.ExpireTime = duration > 0 ? now + duration : 0f;
                cur.Strength = strength;
                cur.Source = source;
                cur.Flags |= StatusEffectFlags.Active;
                e.StatusEffects[i] = cur;
                Update(e);   // QC StatusEffects_update(actor)
                return;
            }
        }
        e.StatusEffects.Add(new ActiveStatusEffect
        {
            DefId = def.RegistryId,
            ExpireTime = duration > 0 ? now + duration : 0f,
            Strength = strength,
            Source = source,
            Flags = StatusEffectFlags.Active,
        });
        Update(e);   // QC StatusEffects_update(actor)
    }

    public static void Remove(Entity e, StatusEffectDef def)
    {
        int removed = e.StatusEffects.RemoveAll(s => s.DefId == def.RegistryId);
        if (removed > 0)
            Update(e);   // QC m_remove -> StatusEffects_update(actor)
    }

    /// <summary>Per-frame tick: expire effects and apply periodic burn damage. Called by the server loop.</summary>
    public static void Tick(Entity e, float now)
    {
        bool changed = false;
        for (int i = e.StatusEffects.Count - 1; i >= 0; i--)
        {
            var s = e.StatusEffects[i];
            // QC m_tick: a PERSISTENT effect does NOT time out (sv_status_effects.qc:20-22).
            bool persistent = (s.Flags & StatusEffectFlags.Persistent) != 0;
            if (!persistent && s.ExpireTime > 0f && now >= s.ExpireTime) { e.StatusEffects.RemoveAt(i); changed = true; continue; }
            // burning: periodic damage (full burn logic layered on by the status-effects subsystem)
            if (Burning != null && s.DefId == Burning.RegistryId)
            {
                Damage.Combat.Damage(e, null, s.Source, s.Strength * 0.05f, "burning", e.Origin, System.Numerics.Vector3.Zero);
            }
        }
        if (changed)
            Update(e);   // QC m_remove(TIMEOUT) -> StatusEffects_update(actor)
    }

    // ============================================================================================
    //  PERSISTENT flag (QC m_persistent / STATUSEFFECT_FLAG_PERSISTENT)
    // ============================================================================================

    /// <summary>
    /// Set or clear the PERSISTENT flag on an active effect (QC <c>m_tick</c>: <c>BITSET(flg,
    /// STATUSEFFECT_FLAG_PERSISTENT, m_persistent())</c>, sv_status_effects.qc:16). When the flag changes the
    /// entity is marked dirty so the client overlay (burning/frozen) re-syncs. No-op if the effect is absent.
    /// Returns true if the flag changed.
    /// </summary>
    public static bool SetPersistent(Entity e, StatusEffectDef def, bool persistent)
    {
        for (int i = 0; i < e.StatusEffects.Count; i++)
        {
            if (e.StatusEffects[i].DefId != def.RegistryId) continue;
            var s = e.StatusEffects[i];
            StatusEffectFlags before = s.Flags;
            if (persistent) s.Flags |= StatusEffectFlags.Persistent;
            else s.Flags &= ~StatusEffectFlags.Persistent;
            if (s.Flags == before) return false;
            e.StatusEffects[i] = s;
            Update(e);   // QC: if(oldflag != flg) StatusEffects_update(actor)
            return true;
        }
        return false;
    }

    /// <summary>QC <c>statuseffect_flags[id]</c>: the current flag bitmap for an effect, or 0 if not active.</summary>
    public static StatusEffectFlags FlagsOf(Entity e, StatusEffectDef def)
    {
        foreach (var s in e.StatusEffects)
            if (s.DefId == def.RegistryId) return s.Flags;
        return 0;
    }

    // ============================================================================================
    //  Networking — ENT_CLIENT_STATUSEFFECTS (QC status_effects.qh StatusEffects_Write/Send + the
    //  NET_HANDLE read). Server marks an entity dirty on any apply/remove/flag change (QC SendFlags =
    //  0xFFFFFF in StatusEffects_update); the net tick writes a full bitmap snapshot for each dirty
    //  entity and the client reads it back. Delta-encoding against the previous snapshot is OPTIONAL
    //  per recon — this port writes the full per-entity bitmap each changed tick.
    // ============================================================================================

    /// <summary>QC <c>StatusEffects_groups_minor</c> (status_effects.qh:19): bits per minor group (one byte).</summary>
    public const int GroupsMinor = 8;

    /// <summary>QC <c>StatusEffects_groups_major</c> (status_effects.qh:20): number of major (minor-bitmap) groups.</summary>
    public const int GroupsMajor = 4;

    // Server-side dirty set (QC the per-entity SendFlags): entities whose status-effect state changed since
    // the last net flush. Kept here (not on Entity) because Entity's gameplay-state partial is owned by
    // another file this wave; a side-table is the layer-clean equivalent of SendFlags. The full per-entity
    // bitmap is re-sent each flush (delta-encoding against a stored snapshot is optional per recon).
    private static readonly HashSet<Entity> _dirty = new();

    /// <summary>
    /// QC <c>StatusEffects_update(actor)</c> (status_effects.qh:153, <c>SendFlags = 0xFFFFFF</c>): mark this
    /// entity's status-effect state dirty so the next net flush re-sends its full bitmap. Called from every
    /// apply/remove/flag-change site (and by <c>DamageSystem.ApplyFrozen/ApplyBurning</c>).
    /// </summary>
    public static void Update(Entity e)
    {
        if (e is not null)
            _dirty.Add(e);
    }

    /// <summary>True if <paramref name="e"/> has a pending status-effect update to network (QC SendFlags != 0).</summary>
    public static bool IsDirty(Entity e) => _dirty.Contains(e);

    /// <summary>Clear all pending dirty marks (test/teardown support, map reset).</summary>
    public static void ResetNetworkState() => _dirty.Clear();

    /// <summary>
    /// QC <c>StatusEffects_Write</c> (status_effects.qh:74): encode an entity's status-effect state into the
    /// grouped major/minor-bitmap + per-effect (time, flags) wire format. Layout (mirrors the QC Writebits
    /// order exactly): a <see cref="GroupsMajor"/>-bit majorBits mask; then for each set major group, a
    /// <see cref="GroupsMinor"/>-bit minorBits mask; then for each set minor bit, the effect's
    /// float <c>time</c> (4 bytes) and byte <c>flags</c>. This port writes EVERY active effect (a full
    /// snapshot) rather than only the deltas against a store entity (delta-encoding optional per recon).
    /// </summary>
    public static byte[] Write(Entity e)
    {
        // Build per-id (time, flags) from the active list (id is the effect's RegistryId).
        // majorBits / minorBits[major] from which ids are present.
        int majorBits = 0;
        var minorBits = new int[GroupsMajor];
        var ids = new SortedDictionary<int, (float Time, byte Flags)>();
        foreach (var s in e.StatusEffects)
        {
            int id = s.DefId;
            int maj = id / GroupsMinor;
            int min = id % GroupsMinor;
            if (maj < 0 || maj >= GroupsMajor) continue; // out of the networked range (parity safety net)
            majorBits |= 1 << maj;
            minorBits[maj] |= 1 << min;
            ids[id] = (s.ExpireTime, (byte)s.Flags);
        }

        var buf = new List<byte>(2 + GroupsMajor * (1 + GroupsMinor * 5));
        buf.Add((byte)majorBits);
        for (int i = 0; i < GroupsMajor; i++)
        {
            if ((majorBits & (1 << i)) == 0) continue;
            buf.Add((byte)minorBits[i]);
            for (int j = 0; j < GroupsMinor; j++)
            {
                if ((minorBits[i] & (1 << j)) == 0) continue;
                int id = GroupsMinor * i + j;
                var (t, f) = ids.TryGetValue(id, out var v) ? v : (0f, (byte)0);
                buf.AddRange(System.BitConverter.GetBytes(t)); // WriteFloat
                buf.Add(f);                                    // WriteByte flags
            }
        }
        return buf.ToArray();
    }

    /// <summary>
    /// QC the <c>NET_HANDLE(ENT_CLIENT_STATUSEFFECTS)</c> read (status_effects.qh:48): decode a buffer
    /// produced by <see cref="Write"/> back into (effectId -&gt; time, flags) entries. The reverse of the
    /// major/minor-bitmap walk; used by the client to drive the overlays (and by the round-trip test).
    /// </summary>
    public static Dictionary<int, (float Time, StatusEffectFlags Flags)> Read(byte[] data)
    {
        var result = new Dictionary<int, (float, StatusEffectFlags)>();
        if (data is null || data.Length == 0) return result;
        int p = 0;
        int majorBits = data[p++];
        for (int i = 0; i < GroupsMajor; i++)
        {
            if ((majorBits & (1 << i)) == 0) continue;
            if (p >= data.Length) break;
            int minorBits = data[p++];
            for (int j = 0; j < GroupsMinor; j++)
            {
                if ((minorBits & (1 << j)) == 0) continue;
                if (p + 5 > data.Length) break;
                float t = System.BitConverter.ToSingle(data, p); p += 4;
                byte f = data[p++];
                result[GroupsMinor * i + j] = (t, (StatusEffectFlags)f);
            }
        }
        return result;
    }

    /// <summary>
    /// Flush one dirty entity: if it has a pending update, return its freshly-encoded snapshot and clear the
    /// dirty mark (QC the per-frame Net_LinkEntity send). Returns null if the entity isn't dirty. The host
    /// net loop calls this for each entity with a <see cref="IsDirty"/> mark and ships the bytes under
    /// ENT_CLIENT_STATUSEFFECTS.
    /// </summary>
    public static byte[]? Flush(Entity e)
    {
        if (!_dirty.Remove(e)) return null;
        return Write(e);
    }
}
