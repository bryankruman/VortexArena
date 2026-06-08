namespace XonoticGodot.Common.Gameplay.Damage;

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
}
