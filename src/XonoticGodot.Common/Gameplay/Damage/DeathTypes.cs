// Port of Base/.../qcsrc/common/deathtypes/all.{qh,inc} (REGISTER_DEATHTYPE registry + .message category
//      + DEATH_ISMONSTER/ISTURRET/ISVEHICLE macros).
using System.Collections.Generic;

namespace XonoticGodot.Common.Gameplay.Damage;

/// <summary>
/// The category an "extra" deathtype message carries (QC <c>.message</c> on a registered deathtype,
/// deathtypes/all.qh:12 + all.inc). Drives obituary phrasing: a monster/turret/vehicle kill uses the
/// generic DEATH_MURDER_MONSTER / DEATH_SELF_TURRET / … lines instead of a weapon line. The QC macros
/// <c>DEATH_ISMONSTER(t)</c>/<c>ISTURRET(t)</c>/<c>ISVEHICLE(t)</c> are string compares on this field.
/// </summary>
public enum DeathCategory
{
    /// <summary>No extra category — a plain environment/weapon death (QC <c>message == ""</c>).</summary>
    None = 0,
    /// <summary>QC <c>message == "monster"</c> — DEATH_ISMONSTER (MONSTER_* deathtypes).</summary>
    Monster = 1,
    /// <summary>QC <c>message == "turret"</c> — DEATH_ISTURRET (TURRET_* deathtypes).</summary>
    Turret = 2,
    /// <summary>QC <c>message == "vehicle"</c> — DEATH_ISVEHICLE (VH_* deathtypes).</summary>
    Vehicle = 3,
}

/// <summary>
/// One registered "special" (non-weapon) deathtype — the C# successor to a QC <c>REGISTER_DEATHTYPE</c>
/// entry (deathtypes/all.qh:18 + all.inc). Carries the bare name (QC <c>m_name</c>, e.g. "MONSTER_SPIDER")
/// and its <see cref="Category"/> (QC <c>.message</c>). The self/murder message NAMES are resolved by
/// <c>DeathMessages</c> from the name + category, so they're not duplicated here.
/// </summary>
public sealed class DeathTypeDef
{
    /// <summary>QC <c>m_name</c> — the registered deathtype id, e.g. "MONSTER_SPIDER", "TURRET_MLRS", "VH_WAKI_GUN".</summary>
    public string Name { get; }

    /// <summary>QC <c>.message</c> category (monster/turret/vehicle), or <see cref="DeathCategory.None"/>.</summary>
    public DeathCategory Category { get; }

    /// <summary>QC <c>death_msgself</c> (all.inc col 2): the bare DEATH_SELF_* notification name for a SUICIDE/
    /// self-death by this deathtype, or null when the row registers no self message (QC NULL → generic fallback).</summary>
    public string? SelfMessage { get; }

    /// <summary>QC <c>death_msgmurder</c> (all.inc col 3): the bare DEATH_MURDER_* notification name for a MURDER
    /// by this deathtype, or null when the row registers no murder message (QC NULL → generic fallback).</summary>
    public string? MurderMessage { get; }

    public DeathTypeDef(string name, DeathCategory category, string? selfMessage = null, string? murderMessage = null)
    {
        Name = name;
        Category = category;
        SelfMessage = selfMessage;
        MurderMessage = murderMessage;
    }
}

/// <summary>
/// Lightweight stand-in for the QuakeC deathtype registry
/// (common/deathtypes/all.{qh,inc}, REGISTER_DEATHTYPE). In QC a deathtype is a packed integer:
/// the low 8 bits are a weapon id (DEATH_WEAPONMASK), HITTYPE_* live in bits 8-13, and "special"
/// (non-weapon) deaths start at DT_FIRST (BIT(14)) and index the Deathtypes registry.
///
/// For this phase the pipeline carries the deathtype as a plain string tag (see
/// <see cref="DamageInfo.DeathType"/>): the attacking weapon's NetName for weapon kills, or one of the
/// named environment/special constants below. The HITTYPE_* bitflags (which can be OR'd onto either a
/// weapon or a special death — e.g. a SPLASH rocket hit, an ARMORPIERCE shot, a SOUND attack) are
/// encoded as <c>"|flag"</c> suffixes appended to the base tag (see <see cref="WithHitType"/> /
/// <see cref="HasHitType"/>). This keeps obituary/hook routing readable while modelling the bit math
/// the damage pipeline needs (armorpierce, sound, splash, spam) faithfully.
/// </summary>
public static class DeathTypes
{
    // --- environment / "accident-trap" deaths (DEATH_* specials in all.inc) ---

    /// <summary>QC DEATH_FALL — fatal fall / impact damage.</summary>
    public const string Fall = "fall";

    /// <summary>QC DEATH_DROWN — out of air. (Armor never helps here: see healtharmor_applydamage.)</summary>
    public const string Drown = "drown";

    /// <summary>QC DEATH_HURTTRIGGER used as DEATH_VOID — fell out of the world / into a kill volume.</summary>
    public const string Void = "void";

    /// <summary>QC DEATH_LAVA.</summary>
    public const string Lava = "lava";

    /// <summary>QC DEATH_SLIME.</summary>
    public const string Slime = "slime";

    /// <summary>QC DEATH_SWAMP.</summary>
    public const string Swamp = "swamp";

    /// <summary>QC DEATH_TELEFRAG — telefrag (ALWAYS lethal in QC, ignores teamplay nullify).</summary>
    public const string Telefrag = "telefrag";

    /// <summary>QC DEATH_KILL — explicit suicide (the /kill command). ALWAYS lethal.</summary>
    public const string Kill = "kill";

    /// <summary>QC DEATH_TEAMCHANGE — forced death on team change. ALWAYS lethal (sets health 0.9).</summary>
    public const string TeamChange = "teamchange";

    /// <summary>QC DEATH_AUTOTEAMCHANGE — auto team-balance death. ALWAYS lethal, does not negate frags.</summary>
    public const string AutoTeamChange = "autoteamchange";

    /// <summary>QC DEATH_GENERIC — unattributed damage.</summary>
    public const string Generic = "generic";

    /// <summary>QC DEATH_MIRRORDAMAGE — teamdamage reflected back at the attacker.</summary>
    public const string MirrorDamage = "mirrordamage";

    /// <summary>QC DEATH_NOAMMO — drowning-style "no ammo" death (no damage processing in Damage()).</summary>
    public const string NoAmmo = "noammo";

    /// <summary>QC DEATH_FIRE — burning damage tick (no armor-impact pain sound; special pain handling).</summary>
    public const string Fire = "fire";

    /// <summary>QC DEATH_BUFF_INFERNO — inferno-buff fire (excluded from the hit-sound credit, like fire).</summary>
    public const string BuffInferno = "buff_inferno";

    /// <summary>QC DEATH_BUFF_VENGEANCE — vengeance-buff reflected damage (excluded from hit-sound credit).</summary>
    public const string BuffVengeance = "buff_vengeance";

    /// <summary>Prefix marking a deathtype string that names a weapon (vs. a special death).</summary>
    public const string WeaponPrefix = "weapon/";

    // --- HITTYPE flag suffixes (QC HITTYPE_* bits, all.qh) -----------------------------------------
    // These OR onto a base tag via "|flag". The string scheme can't pack them into bits, so we keep them
    // as ordered suffix tokens; HasHitType scans for the token. The set mirrors common/deathtypes/all.qh.

    /// <summary>QC HITTYPE_SECONDARY (BIT 8) — alt-fire of the weapon.</summary>
    public const string Secondary = "secondary";
    /// <summary>QC HITTYPE_SPLASH (BIT 9) — set automatically by RadiusDamage for indirect (blast) hits.</summary>
    public const string Splash = "splash";
    /// <summary>QC HITTYPE_BOUNCE (BIT 10) — set after a projectile has bounced.</summary>
    public const string Bounce = "bounce";
    /// <summary>QC HITTYPE_ARMORPIERCE (BIT 11) — ignore armor in the health/armor split.</summary>
    public const string ArmorPierce = "armorpierce";
    /// <summary>QC HITTYPE_SOUND (BIT 12) — sound-based attack (causes ear bleeding; never hits teammates).</summary>
    public const string Sound = "sound";
    /// <summary>QC HITTYPE_SPAM (BIT 13) — set after the first RadiusDamage to stop effect spam.</summary>
    public const string Spam = "spam";

    private const char HitTypeSeparator = '|';

    /// <summary>
    /// Build a weapon deathtype tag from a weapon's NetName (QC DEATH_WEAPONOF — the weapon id packed into
    /// the deathtype). Falls back to <see cref="Generic"/> when the NetName is empty.
    /// </summary>
    public static string FromWeapon(string weaponNetName)
        => string.IsNullOrEmpty(weaponNetName) ? Generic : WeaponPrefix + weaponNetName;

    /// <summary>The portion of a deathtype before any HITTYPE suffixes (the weapon/special base tag).</summary>
    public static string BaseOf(string? deathType)
    {
        if (string.IsNullOrEmpty(deathType))
            return Generic;
        int bar = deathType.IndexOf(HitTypeSeparator);
        return bar < 0 ? deathType : deathType[..bar];
    }

    /// <summary>True if <paramref name="deathType"/> was produced by <see cref="FromWeapon"/> (QC DEATH_WEAPONOF != WEP_Null).</summary>
    public static bool IsWeapon(string? deathType)
        => BaseOf(deathType).StartsWith(WeaponPrefix, System.StringComparison.Ordinal);

    /// <summary>The weapon NetName carried by a weapon deathtype, or "" for special/empty deaths.</summary>
    public static string WeaponNetNameOf(string? deathType)
    {
        string b = BaseOf(deathType);
        return b.StartsWith(WeaponPrefix, System.StringComparison.Ordinal) ? b[WeaponPrefix.Length..] : "";
    }

    /// <summary>
    /// QC DEATH_ISSPECIAL(t): a "special" (non-weapon) deathtype — anything that is NOT a weapon hit.
    /// The global weapon damage/force factors only apply when this is false.
    /// </summary>
    public static bool IsSpecial(string? deathType) => !IsWeapon(deathType);

    /// <summary>OR a HITTYPE flag onto a deathtype tag (QC <c>deathtype | HITTYPE_*</c>). Idempotent.</summary>
    public static string WithHitType(string deathType, string hitType)
        => HasHitType(deathType, hitType) ? deathType : deathType + HitTypeSeparator + hitType;

    /// <summary>True if the deathtype carries the given HITTYPE flag (QC <c>deathtype &amp; HITTYPE_*</c>).</summary>
    public static bool HasHitType(string? deathType, string hitType)
    {
        if (string.IsNullOrEmpty(deathType))
            return false;
        int idx = 0;
        while ((idx = deathType.IndexOf(HitTypeSeparator, idx)) >= 0)
        {
            idx++;
            int end = deathType.IndexOf(HitTypeSeparator, idx);
            if (end < 0) end = deathType.Length;
            if (string.CompareOrdinal(deathType, idx, hitType, 0, System.Math.Max(end - idx, hitType.Length)) == 0
                && (end - idx) == hitType.Length)
                return true;
            idx = end;
        }
        return false;
    }

    /// <summary>
    /// QC DEATH_IS(deathtype, DEATH_DROWN) || (deathtype &amp; HITTYPE_ARMORPIERCE): drowning and any
    /// armor-piercing hit bypass armor entirely in healtharmor_applydamage (armorblock forced to 0).
    /// </summary>
    public static bool BypassesArmor(string? deathType)
        => BaseOf(deathType) == Drown || HasHitType(deathType, ArmorPierce);

    /// <summary>
    /// QC <c>deathtype == DEATH_FIRE || DEATH_BUFF_INFERNO || DEATH_BUFF_VENGEANCE</c>: these burn/buff
    /// deaths are excluded from the normal hit-sound / damage-credit accounting in Damage().
    /// </summary>
    public static bool IsFireOrBuff(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == Fire || b == BuffInferno || b == BuffVengeance;
    }

    /// <summary>QC: DEATH_KILL / DEATH_TEAMCHANGE / DEATH_AUTOTEAMCHANGE — the unconditionally-lethal deaths.</summary>
    public static bool IsAlwaysLethal(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == Kill || b == TeamChange || b == AutoTeamChange;
    }

    /// <summary>QC: DEATH_TEAMCHANGE / DEATH_AUTOTEAMCHANGE — the team-change family (armor/health pre-zeroed).</summary>
    public static bool IsTeamChange(string? deathType)
    {
        string b = BaseOf(deathType);
        return b == TeamChange || b == AutoTeamChange;
    }

    // ============================================================================================
    //  Special deathtype registry + .message category (QC deathtypes/all.qh DEATH_ISMONSTER/
    //  ISTURRET/ISVEHICLE + all.inc). Replaces brittle substring obituary checks with a registry
    //  keyed by the special deathtype's base name.
    // ============================================================================================

    // Base-name tags for the categorized special deaths (QC m_name; lower-cased to match the BaseOf scheme).
    // These mirror the REGISTER_DEATHTYPE rows in all.inc that carry "monster"/"turret"/"vehicle".

    /// <summary>QC DEATH_MONSTER_MAGE — Mage monster kill ("monster").</summary>
    public const string MonsterMage = "monster_mage";
    /// <summary>QC DEATH_MONSTER_GOLEM_CLAW — Golem claw ("monster").</summary>
    public const string MonsterGolemClaw = "monster_golem_claw";
    /// <summary>QC DEATH_MONSTER_GOLEM_SMASH — Golem smash ("monster").</summary>
    public const string MonsterGolemSmash = "monster_golem_smash";
    /// <summary>QC DEATH_MONSTER_GOLEM_ZAP — Golem zap ("monster").</summary>
    public const string MonsterGolemZap = "monster_golem_zap";
    /// <summary>QC DEATH_MONSTER_SPIDER — Spider monster ("monster").</summary>
    public const string MonsterSpider = "monster_spider";
    /// <summary>QC DEATH_MONSTER_WYVERN — Wyvern monster ("monster").</summary>
    public const string MonsterWyvern = "monster_wyvern";
    /// <summary>QC DEATH_MONSTER_ZOMBIE_JUMP — Zombie jump ("monster").</summary>
    public const string MonsterZombieJump = "monster_zombie_jump";
    /// <summary>QC DEATH_MONSTER_ZOMBIE_MELEE — Zombie melee ("monster").</summary>
    public const string MonsterZombieMelee = "monster_zombie_melee";

    /// <summary>QC DEATH_TURRET — generic turret ("turret").</summary>
    public const string Turret = "turret";

    /// <summary>QC DEATH_VH_* — generic vehicle death ("vehicle"). Use the registry for the full per-vehicle set.</summary>
    public const string Vehicle = "vehicle";

    /// <summary>
    /// The categorized special-deathtype registry — the C# successor to the QC <c>Deathtypes</c> registry
    /// rows that carry a non-empty <c>.message</c> (deathtypes/all.inc). Keyed by the base-name tag (lower
    /// case, matching <see cref="BaseOf"/>). The monster set is enumerated explicitly (each MONSTER_* row);
    /// the turret/vehicle families are enumerated and ALSO matched by their "turret"/"vh_" base-name prefix
    /// so a per-turret/vehicle tag (e.g. "turret_mlrs", "vh_waki_gun") is recognized without listing all 30+
    /// QC rows. This is what <see cref="IsMonster"/>/<see cref="IsTurret"/>/<see cref="IsVehicle"/> consult,
    /// replacing the brittle substring scans the obituary code used before.
    /// </summary>
    private static readonly Dictionary<string, DeathTypeDef> _registry = BuildRegistry();

    private static Dictionary<string, DeathTypeDef> BuildRegistry()
    {
        var d = new Dictionary<string, DeathTypeDef>(System.StringComparer.Ordinal);
        // QC REGISTER_DEATHTYPE(id, death_msgself, death_msgmurder, …, extra) — the all.inc cols 2/3 carry the
        // bare DEATH_SELF_*/DEATH_MURDER_* notification each row sends (NULL → generic fallback). We store those
        // names verbatim so SelectSpecial reproduces Obituary_SpecialDeath's registered selection exactly,
        // rather than guessing from the tag.
        void Reg(string name, DeathCategory cat, string? self, string? murder)
            => d[name] = new DeathTypeDef(name, cat, self, murder);

        // QC all.inc monster rows (message == "monster"): each monster has its OWN DEATH_SELF_MON_* self line;
        // ALL monsters share DEATH_MURDER_MONSTER for the murder line.
        Reg(MonsterMage,       DeathCategory.Monster, "DEATH_SELF_MON_MAGE",         "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemClaw,  DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_CLAW",   "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemSmash, DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_SMASH",  "DEATH_MURDER_MONSTER");
        Reg(MonsterGolemZap,   DeathCategory.Monster, "DEATH_SELF_MON_GOLEM_ZAP",    "DEATH_MURDER_MONSTER");
        Reg(MonsterSpider,     DeathCategory.Monster, "DEATH_SELF_MON_SPIDER",       "DEATH_MURDER_MONSTER");
        Reg(MonsterWyvern,     DeathCategory.Monster, "DEATH_SELF_MON_WYVERN",       "DEATH_MURDER_MONSTER");
        Reg(MonsterZombieJump, DeathCategory.Monster, "DEATH_SELF_MON_ZOMBIE_JUMP",  "DEATH_MURDER_MONSTER");
        Reg(MonsterZombieMelee,DeathCategory.Monster, "DEATH_SELF_MON_ZOMBIE_MELEE", "DEATH_MURDER_MONSTER");

        // QC all.inc turret rows (message == "turret"): each turret has its OWN DEATH_SELF_TURRET_* self line;
        // ALL turrets share DEATH_MURDER_CHEAT for the murder line. The bare "turret" tag uses DEATH_SELF_TURRET;
        // the per-turret variants below carry their own self line (resolved through the explicit rows here, with
        // the "turret" base-name prefix in Lookup catching any unlisted variant → bare DEATH_SELF_TURRET).
        Reg(Turret,                  DeathCategory.Turret, "DEATH_SELF_TURRET",               "DEATH_MURDER_CHEAT");
        Reg("turret_ewheel",         DeathCategory.Turret, "DEATH_SELF_TURRET_EWHEEL",        "DEATH_MURDER_CHEAT");
        Reg("turret_flac",           DeathCategory.Turret, "DEATH_SELF_TURRET_FLAC",          "DEATH_MURDER_CHEAT");
        Reg("turret_hellion",        DeathCategory.Turret, "DEATH_SELF_TURRET_HELLION",       "DEATH_MURDER_CHEAT");
        Reg("turret_hk",             DeathCategory.Turret, "DEATH_SELF_TURRET_HK",            "DEATH_MURDER_CHEAT");
        Reg("turret_machinegun",     DeathCategory.Turret, "DEATH_SELF_TURRET_MACHINEGUN",    "DEATH_MURDER_CHEAT");
        Reg("turret_mlrs",           DeathCategory.Turret, "DEATH_SELF_TURRET_MLRS",          "DEATH_MURDER_CHEAT");
        Reg("turret_phaser",         DeathCategory.Turret, "DEATH_SELF_TURRET_PHASER",        "DEATH_MURDER_CHEAT");
        Reg("turret_plasma",         DeathCategory.Turret, "DEATH_SELF_TURRET_PLASMA",        "DEATH_MURDER_CHEAT");
        Reg("turret_tesla",          DeathCategory.Turret, "DEATH_SELF_TURRET_TESLA",         "DEATH_MURDER_CHEAT");
        Reg("turret_walk_gun",       DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_GUN",      "DEATH_MURDER_CHEAT");
        Reg("turret_walk_melee",     DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_MELEE",    "DEATH_MURDER_CHEAT");
        Reg("turret_walk_rocket",    DeathCategory.Turret, "DEATH_SELF_TURRET_WALK_ROCKET",   "DEATH_MURDER_CHEAT");

        // QC all.inc vehicle rows (message == "vehicle"): each VH_* row carries its OWN self AND murder line
        // (some are NULL — e.g. a vehicle GUN has murder-only). The generic "vehicle" tag falls back to GENERIC
        // (no QC row registers a bare "vehicle"); the per-vehicle rows below are the real all.inc entries, and
        // the "vh_" base-name prefix in Lookup catches any unlisted variant → generic.
        Reg(Vehicle,           DeathCategory.Vehicle, null, null);
        Reg("vh_bumb_death",   DeathCategory.Vehicle, "DEATH_SELF_VH_BUMB_DEATH",  "DEATH_MURDER_VH_BUMB_DEATH");
        Reg("vh_bumb_gun",     DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_BUMB_GUN");
        Reg("vh_crush",        DeathCategory.Vehicle, "DEATH_SELF_VH_CRUSH",       "DEATH_MURDER_VH_CRUSH");
        Reg("vh_rapt_bomb",    DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_BOMB",   "DEATH_MURDER_VH_RAPT_BOMB");
        Reg("vh_rapt_cannon",  DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_RAPT_CANNON");
        Reg("vh_rapt_death",   DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_DEATH",  "DEATH_MURDER_VH_RAPT_DEATH");
        Reg("vh_rapt_fragment",DeathCategory.Vehicle, "DEATH_SELF_VH_RAPT_BOMB",   "DEATH_MURDER_VH_RAPT_BOMB");
        Reg("vh_spid_death",   DeathCategory.Vehicle, "DEATH_SELF_VH_SPID_DEATH",  "DEATH_MURDER_VH_SPID_DEATH");
        Reg("vh_spid_minigun", DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_SPID_MINIGUN");
        Reg("vh_spid_rocket",  DeathCategory.Vehicle, "DEATH_SELF_VH_SPID_ROCKET", "DEATH_MURDER_VH_SPID_ROCKET");
        Reg("vh_waki_death",   DeathCategory.Vehicle, "DEATH_SELF_VH_WAKI_DEATH",  "DEATH_MURDER_VH_WAKI_DEATH");
        Reg("vh_waki_gun",     DeathCategory.Vehicle, null,                        "DEATH_MURDER_VH_WAKI_GUN");
        Reg("vh_waki_rocket",  DeathCategory.Vehicle, "DEATH_SELF_VH_WAKI_ROCKET", "DEATH_MURDER_VH_WAKI_ROCKET");

        return d;
    }

    /// <summary>Prefix marking the per-turret deathtype variants (QC TURRET_* rows, message "turret").</summary>
    private const string TurretPrefix = "turret";

    /// <summary>Prefix marking the per-vehicle deathtype variants (QC VH_* rows, message "vehicle").</summary>
    private const string VehiclePrefix = "vh_";

    /// <summary>
    /// QC the registered deathtype entity for a tag (DEATH_ENT(t)): the <see cref="DeathTypeDef"/> if the
    /// base name is a registered special death, else null. The per-turret/vehicle variants resolve to the
    /// generic turret/vehicle def via the name-prefix match.
    /// </summary>
    public static DeathTypeDef? Lookup(string? deathType)
    {
        string b = BaseOf(deathType);
        if (_registry.TryGetValue(b, out var def))
            return def;
        if (b.StartsWith(TurretPrefix, System.StringComparison.Ordinal))
            return _registry[Turret];
        if (b.StartsWith(VehiclePrefix, System.StringComparison.Ordinal))
            return _registry[Vehicle];
        return null;
    }

    /// <summary>QC the <c>.message</c> category for a deathtype, or <see cref="DeathCategory.None"/>.</summary>
    public static DeathCategory CategoryOf(string? deathType) => Lookup(deathType)?.Category ?? DeathCategory.None;

    /// <summary>
    /// QC <c>DEATH_ISMONSTER(t)</c> (deathtypes/all.qh:46): the deathtype's <c>.message == "monster"</c>.
    /// Replaces the old substring scan for monster kills in the obituary phrasing.
    /// </summary>
    public static bool IsMonster(string? deathType) => CategoryOf(deathType) == DeathCategory.Monster;

    /// <summary>
    /// QC <c>DEATH_ISTURRET(t)</c> (deathtypes/all.qh:45): the deathtype's <c>.message == "turret"</c>.
    /// Recognizes the bare "turret" tag and every "turret_*" variant.
    /// </summary>
    public static bool IsTurret(string? deathType) => CategoryOf(deathType) == DeathCategory.Turret;

    /// <summary>
    /// QC <c>DEATH_ISVEHICLE(t)</c> (deathtypes/all.qh:44): the deathtype's <c>.message == "vehicle"</c>.
    /// Recognizes the bare "vehicle" tag and every "vh_*" variant.
    /// </summary>
    public static bool IsVehicle(string? deathType) => CategoryOf(deathType) == DeathCategory.Vehicle;
}
