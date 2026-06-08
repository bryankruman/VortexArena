// Notification descriptors — the C# successor to QuakeC's REGISTER_NOTIFICATION table
// (common/notifications/all.qh macros + all.inc data). Covers the four message families the server
// emits: MSG_INFO (kill feed / console), MSG_CENTER (centerprint), MSG_ANNCE (announcer sounds),
// and MSG_MULTI (a bundle that fans out to an annce + info + center).
//
// A Notification carries its stable NAME, message type, the format string(s) it sprintf()s with the
// supplied args, and how many string/float args it expects (StringCount/FloatCount) so the send path
// can validate call sites exactly like Send_Notification does. The actual rendering (centerprint
// placement, HUD kill-notify icons, announcer queue) is client-side and out of scope here.
//
// Source of truth: Base/.../qcsrc/common/notifications/all.inc. A large slice of the ~727-entry table is
// registered in NotificationsList.cs (announcers, the full self/murder/weapon obituary families, CTF +
// MULTITEAM team expansions, item/connect/score lines, the death/CTF MULTI bundles, and the FRAG/CTF
// MSG_CHOICE notifications). MSG_CHOICE (verbose/terse option selection), gentle-mode selection, and the
// per-arg token expansion (s2loc/spree_inf/item_wepname/frag_stats/death_team/…) are implemented in
// NotificationSystem. The wire protocol (ENT_CLIENT_NOTIFICATION) remains a client-wiring concern.

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>The notification message families (QC <c>ENUMCLASS(MSG)</c> in notifications/all.qh).</summary>
public enum MsgType
{
    /// <summary>Announcer sound (and optional personal cue). QC MSG_ANNCE.</summary>
    Annce = 0,
    /// <summary>"Global" information message — the kill feed / console line. QC MSG_INFO.</summary>
    Info = 1,
    /// <summary>"Personal" centerprint message. QC MSG_CENTER.</summary>
    Center = 2,
    /// <summary>Bundle that subcalls an annce and/or info and/or center notification. QC MSG_MULTI.</summary>
    Multi = 3,
    /// <summary>Picks between two sub-notifications by a per-client choice value (e.g. verbose vs terse). QC MSG_CHOICE.</summary>
    Choice = 4,
}

/// <summary>
/// Whether a server permits a client's non-default MSG_CHOICE selection (QC <c>challow</c> A_* levels).
/// </summary>
public enum NotifAllowed
{
    /// <summary>A_NEVER — choice ignored; always optiona. QC challow 0.</summary>
    Never = 0,
    /// <summary>A_WARMUP — choice honoured only during warmup. QC challow 1.</summary>
    Warmup = 1,
    /// <summary>A_ALWAYS — choice always honoured. QC challow 2.</summary>
    Always = 2,
}

/// <summary>
/// One notification, enrolled into <see cref="Notifications"/>. Mirrors the QC notification entity:
/// a stable name, a <see cref="Type"/>, the expected arg counts, and type-specific payload.
/// <see cref="IRegistered.RegistryName"/> is the QC-style typed name (e.g. "INFO_DEATH_SELF_FALL"),
/// matching how QC keys the registry by <c>Get_Notif_Name</c>.
/// </summary>
public sealed class Notification : IRegistered
{
    public int RegistryId { get; set; }

    /// <summary>Bare name without the type prefix, e.g. "DEATH_SELF_FALL" (QC name field).</summary>
    public string Name = "";

    public MsgType Type;

    /// <summary>Whether this notification is enabled by default (QC nent_default != 0 / nent_enabled).</summary>
    public bool Enabled = true;

    /// <summary>Number of string args this notification consumes (QC nent_stringcount). Max 4.</summary>
    public int StringCount;

    /// <summary>Number of float args this notification consumes (QC nent_floatcount). Max 4.</summary>
    public int FloatCount;

    // --- MSG_INFO / MSG_CENTER payload ---
    /// <summary>The sprintf format for the normal (non-gentle) message (QC <c>normal</c>). Empty for pure annce/multi.</summary>
    public string Normal = "";
    /// <summary>The sprintf format for gentle mode (QC <c>gentle</c>); empty falls back to <see cref="Normal"/>.</summary>
    public string Gentle = "";
    /// <summary>Ordered arg tokens for the message (QC <c>args</c>, e.g. "s1 s2loc spree_lost"). Informational here.</summary>
    public string Args = "";
    /// <summary>HUD kill-notify icon name for MSG_INFO (QC <c>icon</c>); empty if none. Client-side.</summary>
    public string Icon = "";
    /// <summary>Centerprint id group for MSG_CENTER (QC CPID_*); empty = CPID_Null. Client-side grouping.</summary>
    public string Cpid = "";

    // --- MSG_ANNCE payload ---
    /// <summary>Announcer sound filename without extension (QC <c>snd</c>), e.g. "headshot".</summary>
    public string Sound = "";
    /// <summary>Sound channel (QC <c>channel</c>; CH_INFO == 0).</summary>
    public int Channel;
    /// <summary>Sound volume (QC <c>vol</c>; VOL_BASEVOICE == 1.0).</summary>
    public float Volume = 1f;
    /// <summary>Attenuation / positioning (QC <c>position</c>; ATTEN_NONE == 0).</summary>
    public float Attenuation;

    // --- MSG_MULTI references (resolved lazily by name to avoid declaration-order coupling) ---
    /// <summary>Name of the announcer sub-notification (QC anncename), or null.</summary>
    public string? MultiAnnce;
    /// <summary>Name of the info sub-notification (QC infoname), or null.</summary>
    public string? MultiInfo;
    /// <summary>Name of the center sub-notification (QC centername), or null.</summary>
    public string? MultiCenter;

    // --- MSG_CHOICE payload (resolved lazily by name) ---
    /// <summary>The message type the two choice options belong to (QC <c>chtype</c>), e.g. Center.</summary>
    public MsgType ChoiceType;
    /// <summary>Whether a non-default selection is permitted (QC <c>challow</c>).</summary>
    public NotifAllowed ChoiceAllowed;
    /// <summary>Bare name of option A — the default (QC <c>nent_optiona</c>).</summary>
    public string? ChoiceOptionA;
    /// <summary>Bare name of option B — chosen when the client's choice value is 2 (QC <c>nent_optionb</c>).</summary>
    public string? ChoiceOptionB;

    /// <summary>Total args expected, for the Send-time count check (QC stringcount + floatcount).</summary>
    public int ArgCount => StringCount + FloatCount;

    /// <summary>The QC-style typed registry name, e.g. "INFO_DEATH_SELF_FALL".</summary>
    public string RegistryName => $"{TypePrefix(Type)}_{Name}";

    public Notification() { }

    public static string TypePrefix(MsgType t) => t switch
    {
        MsgType.Annce => "ANNCE",
        MsgType.Info => "INFO",
        MsgType.Center => "CENTER",
        MsgType.Multi => "MULTI",
        MsgType.Choice => "CHOICE",
        _ => "MSG",
    };

    public override string ToString() => $"{RegistryName}({StringCount}s,{FloatCount}f)";
}

/// <summary>
/// The notification catalog — the C# successor to QC's <c>REGISTRY(Notifications)</c>. Self-registering:
/// definitions are added by <see cref="NotificationsList.RegisterAll"/>, which the lead calls once from
/// GameInit. Lookups are by the typed name (e.g. "INFO_DEATH_SELF_FALL") or by type + bare name.
/// </summary>
public static class Notifications
{
    public static IReadOnlyList<Notification> All => Registry<Notification>.All;
    public static int Count => Registry<Notification>.Count;

    /// <summary>Look up by the QC-style typed name, e.g. "CENTER_COUNTDOWN_BEGIN".</summary>
    public static Notification? ByName(string registryName) => Registry<Notification>.ByName(registryName);

    /// <summary>Look up by message type + bare name, e.g. (Info, "DEATH_SELF_FALL").</summary>
    public static Notification? ByName(MsgType type, string bareName)
        => Registry<Notification>.ByName($"{Notification.TypePrefix(type)}_{bareName}");

    public static Notification ById(int id) => Registry<Notification>.ById(id);

    /// <summary>Content hash over typed names in order — client and server must agree (QC registry handshake).</summary>
    public static uint Hash => Registry<Notification>.ContentHash();

    public static Notification Register(Notification n)
    {
        Registry<Notification>.Register(n);
        return n;
    }

    /// <summary>
    /// Populate the catalog with every implemented notification (delegates to
    /// <see cref="NotificationsList.RegisterAll"/>). The lead calls this once from GameInit. Idempotent.
    /// </summary>
    public static void RegisterAll() => NotificationsList.RegisterAll();

    public static void Sort() => Registry<Notification>.Sort();
    public static void Clear() => Registry<Notification>.Clear();

    // ---- builder helpers mirroring the QC MSG_*_NOTIF macros ----

    /// <summary>QC <c>MSG_INFO_NOTIF(name, default, strnum, flnum, args, hudargs, icon, normal, gentle)</c>.</summary>
    public static Notification Info(string name, int strCount, int floatCount, string args, string icon,
        string normal, string gentle = "", bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Info, Enabled = enabled,
            StringCount = strCount, FloatCount = floatCount,
            Args = args, Icon = icon, Normal = normal, Gentle = gentle,
        });

    /// <summary>QC <c>MSG_CENTER_NOTIF(name, default, strnum, flnum, args, cpid, durcnt, normal, gentle)</c>.</summary>
    public static Notification Center(string name, int strCount, int floatCount, string args, string cpid,
        string normal, string gentle = "", bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Center, Enabled = enabled,
            StringCount = strCount, FloatCount = floatCount,
            Args = args, Cpid = cpid, Normal = normal, Gentle = gentle,
        });

    /// <summary>QC <c>MSG_ANNCE_NOTIF(name, default, sound, channel, volume, position, queuetime)</c>.</summary>
    public static Notification Annce(string name, string sound, int channel = 0, float volume = 1f,
        float attenuation = 0f, bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Annce, Enabled = enabled,
            Sound = sound, Channel = channel, Volume = volume, Attenuation = attenuation,
        });

    /// <summary>QC <c>MSG_MULTI_NOTIF(name, default, anncename, infoname, centername)</c> (sub-notifs referenced by bare name).</summary>
    public static Notification Multi(string name, string? annce, string? info, string? center, bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Multi, Enabled = enabled,
            MultiAnnce = annce, MultiInfo = info, MultiCenter = center,
        });

    /// <summary>
    /// QC <c>MSG_CHOICE_NOTIF(name, default, challow, chtype, optiona, optionb)</c>. Both options are bare
    /// names of <paramref name="optionType"/> notifications; <see cref="NotificationSystem"/> resolves and
    /// dispatches one based on the per-client choice value (subject to <paramref name="allowed"/>).
    /// </summary>
    public static Notification Choice(string name, NotifAllowed allowed, MsgType optionType,
        string optionA, string optionB, bool enabled = true)
        => Register(new Notification
        {
            Name = name, Type = MsgType.Choice, Enabled = enabled,
            ChoiceAllowed = allowed, ChoiceType = optionType,
            ChoiceOptionA = optionA, ChoiceOptionB = optionB,
        });
}
