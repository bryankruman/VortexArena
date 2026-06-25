using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// What kind of networked entity a <see cref="NetEntityState"/> describes — the successor to the QC
/// <c>entcs</c>/CSQCModel "which read function" dispatch. Lets the client pick the right render representation
/// (a player model + nameplate, a projectile + trail, a pickup, …) from one unified delta-compressed stream.
/// </summary>
public enum NetEntityKind : byte
{
    None = 0,
    Player = 1,      // a remote player (csqcmodel: model + animation + nameplate + radar)
    Projectile = 2,  // a flying projectile (CSQCProjectile: model/sprite + trail)
    Item = 3,        // a world pickup (static unless it bobs/rotates)
    Gib = 4,         // a gib/debris chunk
    Generic = 5,     // any other networked entity (mapobject, turret, monster, …)
    ViewModel = 6,   // a weapon view-entity owned by a player (CL_SpawnWeaponentity / wepent)
    Nameplate = 7,   // ent_cs lightweight: position+team+health only (radar/nameplate, no model)
}

/// <summary>
/// Per-entity boolean flags carried in two bytes when the <see cref="EntityField.Flags"/> bit is delta'd. These
/// mirror the CSQCModel property flags that affect interpolation/rendering rather than a transform value.
/// (Widened from a single byte to a <see cref="ushort"/> when <see cref="UsingJetpack"/> = bit 8 was added — the
/// original 8-bit range was full; <see cref="EntityStateCodec"/> writes/reads the field as a 16-bit value.)
/// </summary>
[Flags]
public enum NetEntityFlags : ushort
{
    None = 0,
    Teleported = 1 << 0, // CSQCMODEL_PROPERTY_TELEPORTED / IFLAG_TELEPORTED — cancel interpolation this update
    OnGround = 1 << 1,   // standing on the ground (affects remote anim / step smoothing)
    Crouched = 1 << 2,   // ducking (smaller hull / crouch anim)
    Dead = 1 << 3,       // a corpse (no nameplate, ragdoll anim)
    ItemExpiring = 1 << 4, // QC ITS_EXPIRING — a loot item in its despawn-fx window (client fades + emits puffs)
    ItemAnimate1 = 1 << 5, // QC ITS_ANIMATE1 — powerup/weapon pickup: client spins yaw +180°/s and bobs 10 + 8·sin(2t)
    ItemAnimate2 = 1 << 6, // QC ITS_ANIMATE2 — health/armor pickup: client spins yaw -90°/s and bobs 8 + 4·sin(3t)
    ItemGhost = 1 << 7,    // QC !ITS_AVAILABLE — a picked-up item awaiting respawn: client renders it faded (cl_ghost_items)
    // QC `ITEMS_STAT(this) & IT_USING_JETPACK` (common/physics/player.qc:878): the player's jetpack is firing this
    // tick. CSQC derives csqcmodel_modelflags |= MF_ROCKET from this networked items bit (player.qc:879), which
    // drives the rocket trail AND the looping jetpack-fly sound (csqcmodel_hooks.qc:611/645). The port networks it
    // here so the client can re-derive MF_ROCKET per player (ComposeForcedAppearance) instead of networking MF_*.
    UsingJetpack = 1 << 8,
}

/// <summary>
/// Which fields of a <see cref="NetEntityState"/> are present in a delta — the C# successor to QC's
/// <c>SendFlags</c>/<c>ReadFlags</c> change mask (csqcmodel <c>ALLPROPERTIES</c>). A 32-bit mask leads each
/// entity's delta; only the set fields follow on the wire, so an idle entity (mask 0) costs just its id.
/// (Widened from 16-bit to 32-bit when <see cref="StatusEffects"/> was added — bit 16 overflowed the old
/// <c>ushort</c>; <see cref="EntityStateCodec.WriteDelta"/>/<see cref="EntityStateCodec.ReadDelta"/> carry the
/// mask as a 32-bit value accordingly.)
/// </summary>
[Flags]
public enum EntityField : uint
{
    None = 0,
    Kind = 1 << 0,
    ModelIndex = 1 << 1,
    Frame = 1 << 2,
    Skin = 1 << 3,
    Origin = 1 << 4,
    Angles = 1 << 5,
    Velocity = 1 << 6,
    Effects = 1 << 7,
    Colormap = 1 << 8,
    Health = 1 << 9,
    Flags = 1 << 10,
    Owner = 1 << 11,  // the player entnum this entity belongs to (view-models, nameplates, projectiles)
    Weapon = 1 << 12, // the active/held weapon registry id (renders a remote player's weapon — QC wepent)
    Model = 1 << 13,  // the model NAME (precache path); networked for players/bots so the client loads the IQM

    // [T41] client-feedback stats networked on the owning player's entity (QC owner-only STATs). These ride the
    // same delta as the rest of the owner's state; they are 0 on every non-owner entity (so they cost nothing on
    // the wire for remote players / projectiles, where the mask bit stays clear).
    Feedback = 1 << 14, // the HitsoundDamageDealtTotal + objective-ring fractions (NadeTimer/Capture/Revive)

    // [T68] the QC entcs ARMOR slice (ent_cs.qc ENTCS_PROP_RESOURCE(ARMOR, …)). Networked alongside Health for the
    // shownames teammate status bar (Draw_ShowNames reads RES_ARMOR off the entcs entity for same-team players).
    // 0 on non-player entities / when armor is unchanged, so it costs nothing on the wire when the bit stays clear.
    Armor = 1 << 15, // a player's armor value (the entcs private ARMOR slice — shownames teammate status bar)

    // [A5 #3/#7] the QC ENT_CLIENT_STATUSEFFECTS channel (status_effects.qh StatusEffects_Write/Send). Carries an
    // entity's full status-effect bitmap (frozen/burning/buffs/powerups — the grouped major/minor + per-effect
    // time+flags blob produced by StatusEffectsCatalog.Write) so the client drives the remote burning/frozen
    // overlays. Bit 16 — this is the field that pushed the mask past 16 bits and widened EntityField to uint. The
    // blob is null/empty on every entity with no networked effects, so the bit stays clear and costs nothing.
    StatusEffects = 1 << 16,

    // [W1-alpha-net] the QC csqcmodel m_alpha / .alpha render-transparency channel (csqcmodel_settings.qh
    // CSQCMODEL_PROPERTY_ALPHA). Carries a per-entity render alpha so the client fades a transparent entity —
    // the Cloaked mutator (default_player_alpha 0.25), Running Guns (invisible player / visible gun), the
    // Invisibility powerup, and any death/spawn fade. Sent as a byte (0..255 = 0..1) only when it changes; the
    // default (fully opaque) never sets the bit, so an opaque entity costs nothing on the wire.
    Alpha = 1 << 17,
}

/// <summary>
/// The networked state of one entity — the unified "property table" the server replicates and the client
/// renders/interpolates. This single value type backs BOTH the snapshot delta-compression (the
/// <see cref="EntityStateCodec"/> writes only changed fields against a baseline) and the CSQC networked-entity
/// model (the field set is csqcmodel <c>ALLPROPERTIES</c> plus the <c>entcs</c> radar slice). Origin uses the
/// 13i coord path, angles the 8i path (matching the property table's WriteVector/WriteAngle quantization).
/// </summary>
public struct NetEntityState
{
    /// <summary>Network entity id (QC <c>entnum</c>) — the dictionary key; written at the snapshot level, not in the delta.</summary>
    public int EntNum;

    public NetEntityKind Kind;
    public int ModelIndex;
    public int Frame;
    public int Skin;
    public Vector3 Origin;
    public Vector3 Angles;
    public Vector3 Velocity;
    public int Effects;          // EF_* render flags bitfield
    public int Colormap;         // player colors (top/bottom) or team tint
    public int Health;           // for nameplates / the owner HUD (0 when not applicable)
    public int Armor;            // [T68] QC entcs RES_ARMOR slice — the shownames teammate status bar (0 when N/A)

    /// <summary>
    /// [W1-alpha-net] QC csqcmodel <c>m_alpha</c> / <c>.alpha</c> render transparency, quantized to a byte
    /// (1..254 = 1/255..254/255; <b>0 = the default "fully opaque"</b>, which is NOT networked — the
    /// <see cref="EntityField.Alpha"/> bit stays clear so an opaque entity costs nothing on the wire and a
    /// never-seen entity (Empty baseline, all zero) reads as opaque). Set by the producer only when an entity's
    /// alpha drops below 1 (Cloaked/Running Guns/Invisibility/death-fade). The client maps 0 → opaque, else
    /// <c>Alpha/255</c>.
    /// </summary>
    public int Alpha;
    public NetEntityFlags Flags;
    public int Owner;            // owning player's entnum (view-models / nameplates / projectiles); 0 = none
    public int Weapon;           // active/held weapon registry id (−1 = none) — renders a remote player's weapon
    public string Model;         // model name / precache path (QC .model) — the client loads the mesh by name

    // [T41] client-feedback stats (QC STAT(HITSOUND_DAMAGE_DEALT_TOTAL) + the HUD_Draw objective rings). Only
    // meaningful on the LOCAL player's own entity; 0 everywhere else. Carried together under EntityField.Feedback.
    /// <summary>QC STAT(HITSOUND_DAMAGE_DEALT_TOTAL): cumulative damage the owner has dealt; the client diffs it
    /// against the previous update to drive the hit-confirmation sound (view.qc UpdateDamage/HitSound).</summary>
    public float HitDamageDealtTotal;
    /// <summary>QC STAT(NADE_TIMER): 0..1 held-nade charge — the top-priority HUD objective ring (view.qc:1006).</summary>
    public float NadeTimer;
    /// <summary>QC STAT(CAPTURE_PROGRESS): 0..1 objective-capture progress — the 2nd-priority ring (view.qc:1012).</summary>
    public float CaptureProgress;
    /// <summary>QC STAT(REVIVE_PROGRESS): 0..1 freeze-tag thaw progress — the 3rd-priority ring (view.qc:1017).</summary>
    public float ReviveProgress;

    /// <summary>
    /// [A5 #3/#7] QC <c>ENT_CLIENT_STATUSEFFECTS</c> blob — the entity's full status-effect bitmap as produced by
    /// <c>StatusEffectsCatalog.Write</c> (the grouped major/minor mask + per-effect time+flags). <c>null</c> (or an
    /// empty array) means "no networked effects": the <see cref="EntityField.StatusEffects"/> bit stays clear so it
    /// costs nothing on the wire. The full bitmap is re-sent only on the tick it changes (the delta compares it by
    /// content); the client decodes it with <c>StatusEffectsCatalog.Read</c> to drive the burning/frozen overlays.
    /// </summary>
    public byte[]? StatusEffects;

    /// <summary>A fresh state carrying just an id (the implicit baseline for a never-seen entity — a "spawn").</summary>
    public static NetEntityState Empty(int entNum) => new() { EntNum = entNum };

    /// <summary>The set of fields that differ between <paramref name="baseline"/> and <paramref name="current"/> (the SendFlags).</summary>
    public static EntityField Diff(in NetEntityState baseline, in NetEntityState current)
    {
        EntityField m = EntityField.None;
        if (baseline.Kind != current.Kind) m |= EntityField.Kind;
        if (baseline.ModelIndex != current.ModelIndex) m |= EntityField.ModelIndex;
        if (baseline.Frame != current.Frame) m |= EntityField.Frame;
        if (baseline.Skin != current.Skin) m |= EntityField.Skin;
        if (baseline.Origin != current.Origin) m |= EntityField.Origin;
        if (baseline.Angles != current.Angles) m |= EntityField.Angles;
        if (baseline.Velocity != current.Velocity) m |= EntityField.Velocity;
        if (baseline.Effects != current.Effects) m |= EntityField.Effects;
        if (baseline.Colormap != current.Colormap) m |= EntityField.Colormap;
        if (baseline.Health != current.Health) m |= EntityField.Health;
        if (baseline.Armor != current.Armor) m |= EntityField.Armor;
        if (baseline.Alpha != current.Alpha) m |= EntityField.Alpha;
        if (baseline.Flags != current.Flags) m |= EntityField.Flags;
        if (baseline.Owner != current.Owner) m |= EntityField.Owner;
        if (baseline.Weapon != current.Weapon) m |= EntityField.Weapon;
        if (baseline.Model != current.Model) m |= EntityField.Model;
        if (baseline.HitDamageDealtTotal != current.HitDamageDealtTotal
            || baseline.NadeTimer != current.NadeTimer
            || baseline.CaptureProgress != current.CaptureProgress
            || baseline.ReviveProgress != current.ReviveProgress) m |= EntityField.Feedback;
        // The status-effect blob is a byte[]; compare by CONTENT (not reference) so the full bitmap is re-sent only
        // when it actually changes. null and empty both mean "no effects" and compare equal.
        if (!StatusBlobEqual(baseline.StatusEffects, current.StatusEffects)) m |= EntityField.StatusEffects;
        return m;
    }

    /// <summary>Content equality for the status-effect blob, treating <c>null</c> and an empty array as equal.</summary>
    internal static bool StatusBlobEqual(byte[]? a, byte[]? b)
    {
        int la = a is null ? 0 : a.Length;
        int lb = b is null ? 0 : b.Length;
        if (la != lb) return false;
        for (int i = 0; i < la; i++)
            if (a![i] != b![i]) return false;
        return true;
    }
}

/// <summary>
/// Serializes a <see cref="NetEntityState"/> as a delta against a baseline — the change-mask codec that
/// powers both snapshot delta-compression and CSQC entity updates (QC <c>SendEntity</c>/<c>ReadEntity</c> with
/// <c>SendFlags</c>). <see cref="WriteDelta"/> emits a 16-bit <see cref="EntityField"/> mask followed by only
/// the changed fields; <see cref="ReadDelta"/> applies them on top of the baseline. A spawn is just a delta
/// against <see cref="NetEntityState.Empty"/>; an idle entity is a single zero mask.
/// </summary>
public static class EntityStateCodec
{
    /// <summary>Write <paramref name="current"/> as the changed-fields delta from <paramref name="baseline"/>. Returns the mask written.</summary>
    public static EntityField WriteDelta(BitWriter w, in NetEntityState baseline, in NetEntityState current)
    {
        EntityField mask = NetEntityState.Diff(baseline, current);
        // 32-bit mask (widened from ushort when EntityField.StatusEffects = bit 16 was added).
        w.WriteULong((uint)mask);

        if ((mask & EntityField.Kind) != 0) w.WriteByte((byte)current.Kind);
        if ((mask & EntityField.ModelIndex) != 0) w.WriteUShort(current.ModelIndex);
        if ((mask & EntityField.Frame) != 0) w.WriteUShort(current.Frame);
        if ((mask & EntityField.Skin) != 0) w.WriteByte(current.Skin & 0xFF);
        if ((mask & EntityField.Origin) != 0) w.WriteVector(current.Origin, NetPrecision.Low);
        if ((mask & EntityField.Angles) != 0) w.WriteAngles(current.Angles, NetPrecision.Low);
        if ((mask & EntityField.Velocity) != 0) w.WriteVector(current.Velocity, NetPrecision.Low);
        if ((mask & EntityField.Effects) != 0) w.WriteLong(current.Effects);
        if ((mask & EntityField.Colormap) != 0) w.WriteByte(current.Colormap & 0xFF);
        if ((mask & EntityField.Health) != 0) w.WriteShort(current.Health);
        if ((mask & EntityField.Armor) != 0) w.WriteShort(current.Armor);
        if ((mask & EntityField.Alpha) != 0) w.WriteByte(current.Alpha & 0xFF); // 0 = opaque (default); 1..254 = alpha/255
        if ((mask & EntityField.Flags) != 0) w.WriteUShort((ushort)current.Flags); // 16-bit since UsingJetpack=bit 8 widened it
        if ((mask & EntityField.Owner) != 0) w.WriteUShort(current.Owner);
        if ((mask & EntityField.Weapon) != 0) w.WriteShort(current.Weapon);
        if ((mask & EntityField.Model) != 0) w.WriteString(current.Model);
        if ((mask & EntityField.Feedback) != 0)
        {
            w.WriteFloat(current.HitDamageDealtTotal);
            w.WriteFloat(current.NadeTimer);
            w.WriteFloat(current.CaptureProgress);
            w.WriteFloat(current.ReviveProgress);
        }
        if ((mask & EntityField.StatusEffects) != 0)
        {
            // Length-prefixed blob (the SessionAuth pattern). A 0 length is a valid value — it means "effects just
            // cleared", so the client reads an empty bitmap and drops the entity's effects.
            byte[]? blob = current.StatusEffects;
            int n = blob is null ? 0 : blob.Length;
            w.WriteUShort(n);
            if (n > 0) w.WriteBytes(blob);
        }
        return mask;
    }

    /// <summary>Read a delta and apply it onto <paramref name="baseline"/>, returning the reconstructed state.</summary>
    public static NetEntityState ReadDelta(ref BitReader r, in NetEntityState baseline)
    {
        NetEntityState s = baseline;
        var mask = (EntityField)r.ReadULong();

        if ((mask & EntityField.Kind) != 0) s.Kind = (NetEntityKind)r.ReadByte();
        if ((mask & EntityField.ModelIndex) != 0) s.ModelIndex = r.ReadUShort();
        if ((mask & EntityField.Frame) != 0) s.Frame = r.ReadUShort();
        if ((mask & EntityField.Skin) != 0) s.Skin = r.ReadByte();
        if ((mask & EntityField.Origin) != 0) s.Origin = r.ReadVector(NetPrecision.Low);
        if ((mask & EntityField.Angles) != 0) s.Angles = r.ReadAngles(NetPrecision.Low);
        if ((mask & EntityField.Velocity) != 0) s.Velocity = r.ReadVector(NetPrecision.Low);
        if ((mask & EntityField.Effects) != 0) s.Effects = r.ReadLong();
        if ((mask & EntityField.Colormap) != 0) s.Colormap = r.ReadByte();
        if ((mask & EntityField.Health) != 0) s.Health = r.ReadShort();
        if ((mask & EntityField.Armor) != 0) s.Armor = r.ReadShort();
        if ((mask & EntityField.Alpha) != 0) s.Alpha = r.ReadByte();
        if ((mask & EntityField.Flags) != 0) s.Flags = (NetEntityFlags)r.ReadUShort();
        if ((mask & EntityField.Owner) != 0) s.Owner = r.ReadUShort();
        if ((mask & EntityField.Weapon) != 0) s.Weapon = r.ReadShort();
        if ((mask & EntityField.Model) != 0) s.Model = r.ReadString();
        if ((mask & EntityField.Feedback) != 0)
        {
            s.HitDamageDealtTotal = r.ReadFloat();
            s.NadeTimer = r.ReadFloat();
            s.CaptureProgress = r.ReadFloat();
            s.ReviveProgress = r.ReadFloat();
        }
        if ((mask & EntityField.StatusEffects) != 0)
        {
            int n = r.ReadUShort();
            // n == 0 (effects cleared) decodes to an empty array so the client distinguishes "field present, now
            // empty" (clear the list) from "field absent" (keep the baseline's blob).
            s.StatusEffects = n > 0 ? r.ReadBytes(n).ToArray() : System.Array.Empty<byte>();
        }
        return s;
    }
}
