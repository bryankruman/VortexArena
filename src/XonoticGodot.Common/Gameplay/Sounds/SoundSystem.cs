using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

// Playback helpers for the sound registry — the C# successor to QuakeC's sound() / GlobalSound() /
// play2all() call sites (common/sounds/sound.qh, common/effects/qc/globalsound.qc). These route a
// registered GameSound (or a bare sample path) through the engine facade Api.Sound.Play.
//
// QC distinguishes:
//   sound(e, chan, samp, vol, atten)        — a positional sound emitted *on* entity e        => PlayOn
//   _GlobalSound / play2all(samp)           — a non-positional sound heard by everyone         => PlayGlobal
//   PlayerSound(this, def, …)               — a voice/body sound on a player at VOL_BASEVOICE  => PlayOn (Voice/Body)
//
// Everything here ultimately calls ISoundService.Play(entity, channel, sample, volume, attenuation).

/// <summary>
/// The random-variant sound groups — the C# successor to QC's <c>SND_*_RANDOM()</c> picker functions
/// (common/sounds/all.inc) and the GlobalSound "&lt;base&gt; &lt;count&gt;" pairs. Two kinds:
///  * <b>registered groups</b> (RIC1..3, GIB_SPLAT01..04, GRENADE_BOUNCE1..6, NEXWHOOSH1..3, FLACEXP1..3):
///    N consecutive registered <see cref="GameSound"/>s; the picker returns one of them — QC indexes by
///    <c>SND_X1.m_id + floor(prandom()*N)</c>, here by name "&lt;PREFIX&gt;&lt;k&gt;".
///  * <b>numbered-file groups</b> (STEP, STEP_METAL, FALL, FALL_METAL): one registered base sound plus a
///    count of on-disk numbered files; the played sample is "&lt;base&gt;&lt;k&gt;.wav" (QC GlobalSound_sample).
/// All selection uses <see cref="Prandom"/> so it stays deterministic / network-reproducible (ADR-0010).
/// </summary>
public static class SoundVariantGroups
{
    /// <summary>QC GlobalSound "&lt;base&gt; &lt;count&gt;" counts (REGISTER_GLOBALSOUND in globalsound.qh).</summary>
    private static readonly Dictionary<string, int> GlobalCounts = new(StringComparer.Ordinal)
    {
        ["STEP"] = 6,
        ["STEP_METAL"] = 6,
        ["FALL"] = 4,
        ["FALL_METAL"] = 4,
    };

    /// <summary>The registered-variant group prefixes and their member counts (SND_*_RANDOM groups).</summary>
    private static readonly (string Prefix, int Count, int Start)[] RegisteredGroups =
    {
        ("RIC", 3, 1),             // ric1..ric3
        ("NEXWHOOSH", 3, 1),       // nexwhoosh1..3
        ("GRENADE_BOUNCE", 6, 1),  // grenade_bounce1..6
        ("FLACEXP", 3, 1),         // hagexp1..3 registered as FLACEXP1..3
        ("GIB_SPLAT0", 4, 1),      // gib_splat01..04 registered as GIB_SPLAT01..04
    };

    /// <summary>
    /// Pick a member of a registered random group by its base name (e.g. "RIC", "GIB_SPLAT0"). Returns the
    /// chosen <see cref="GameSound"/>, or null if the group/member isn't registered. QC SND_X_RANDOM().
    /// </summary>
    public static GameSound? PickRegistered(string groupPrefix)
    {
        foreach (var (prefix, count, start) in RegisteredGroups)
        {
            if (!string.Equals(prefix, groupPrefix, StringComparison.Ordinal)) continue;
            int k = start + Prandom.RangeInt(0, count);            // QC: m_id + floor(prandom()*count)
            return Sounds.ByName($"{prefix}{k}");
        }
        return null;
    }

    // convenience accessors mirroring the QC picker names
    public static GameSound? Ric() => PickRegistered("RIC");
    public static GameSound? NexWhoosh() => PickRegistered("NEXWHOOSH");
    public static GameSound? GrenadeBounce() => PickRegistered("GRENADE_BOUNCE");
    public static GameSound? FlacExp() => PickRegistered("FLACEXP");
    public static GameSound? GibSplat() => PickRegistered("GIB_SPLAT0");

    /// <summary>The numbered-variant count for a GlobalSound base name, or 0 if it isn't a counted group.</summary>
    public static int GlobalCount(string name) => GlobalCounts.TryGetValue(name, out int n) ? n : 0;

    /// <summary>
    /// Resolve the actual sample file for a counted GlobalSound (QC GlobalSound_sample): when the sound has
    /// a variant count, "&lt;base&gt;&lt;floor(prandom()*count)+1&gt;.wav"; otherwise "&lt;base&gt;.wav".
    /// </summary>
    public static string ResolveGlobalSample(GameSound sound)
    {
        int count = GlobalCount(sound.NetName);
        if (count > 0)
            return $"{sound.Sample}{Prandom.RangeInt(0, count) + 1}.wav";
        return $"{sound.Sample}.wav";
    }
}

/// <summary>
/// Routes registered sounds to <see cref="Api.Sound"/>. Stateless apart from a lazily-spawned shared
/// emitter used by <see cref="PlayGlobal(GameSound)"/> when no explicit emitter is supplied (the analogue
/// of QC playing a global sound on the world entity).
/// </summary>
public static class SoundSystem
{
    // Shared emitter for non-positional/global sounds (QC plays these on `world`). Spawned on first use so
    // the system stays inert until something actually plays a global sound. ATTEN_NONE => heard everywhere.
    private static Entity? _globalEmitter;

    private static Entity GlobalEmitter
    {
        get
        {
            if (_globalEmitter is null || _globalEmitter.IsFreed)
            {
                _globalEmitter = Api.Entities.Spawn();
                _globalEmitter.ClassName = "sound_global_emitter";
            }
            return _globalEmitter;
        }
    }

    // ---- play on a specific entity (QC sound(e, …)) ----

    /// <summary>
    /// Play a registered sound emitted on <paramref name="emitter"/>, using the sound's default channel,
    /// volume and attenuation. Successor to QC <c>sound(e, def.chan, SND(def), def.vol, def.atten)</c>.
    /// No-op if the sound is null.
    /// </summary>
    public static void PlayOn(Entity emitter, GameSound? sound)
    {
        if (sound is null) return;
        Api.Sound.Play(emitter, sound.EngineChannel, sound.Sample, sound.Volume, sound.Attenuation);
    }

    /// <summary>Play a registered sound on <paramref name="emitter"/> with explicit volume/attenuation overrides.</summary>
    public static void PlayOn(Entity emitter, GameSound? sound, float volume, float attenuation)
    {
        if (sound is null) return;
        Api.Sound.Play(emitter, sound.EngineChannel, sound.Sample, volume, attenuation);
    }

    /// <summary>Play a registered sound on <paramref name="emitter"/> overriding the channel as well.</summary>
    public static void PlayOn(Entity emitter, GameSound? sound, SoundChannel channel, float volume, float attenuation)
    {
        if (sound is null) return;
        Api.Sound.Play(emitter, channel, sound.Sample, volume, attenuation);
    }

    /// <summary>Look the sound up by name (QC <c>SND_&lt;name&gt;</c>) then play it on <paramref name="emitter"/>.</summary>
    public static void PlayOn(Entity emitter, string soundName) => PlayOn(emitter, Sounds.ByName(soundName));

    /// <summary>Play a raw sample path on <paramref name="emitter"/> (QC <c>sound()</c> with a string literal).</summary>
    public static void PlayRaw(
        Entity emitter,
        string sample,
        SoundChannel channel = SoundChannel.Auto,
        float volume = SoundLevels.VolBase,
        float attenuation = SoundLevels.AttenNorm)
        => Api.Sound.Play(emitter, channel, sample, volume, attenuation);

    // ---- global / non-positional (QC play2all / _GlobalSound on world) ----

    /// <summary>
    /// Play a registered sound globally (heard by everyone, no spatialization) on the shared emitter.
    /// Forces ATTEN_NONE so distance doesn't attenuate it, matching QC announcer/global playback.
    /// Successor to QC <c>play2all(SND(def))</c> / a GlobalSound with ATTEN_NONE.
    /// </summary>
    public static void PlayGlobal(GameSound? sound)
    {
        if (sound is null) return;
        Api.Sound.Play(GlobalEmitter, sound.EngineChannel, sound.Sample, sound.Volume, SoundLevels.AttenNone);
    }

    /// <summary>Play a registered sound globally with an explicit volume.</summary>
    public static void PlayGlobal(GameSound? sound, float volume)
    {
        if (sound is null) return;
        Api.Sound.Play(GlobalEmitter, sound.EngineChannel, sound.Sample, volume, SoundLevels.AttenNone);
    }

    /// <summary>Look the sound up by name then play it globally.</summary>
    public static void PlayGlobal(string soundName) => PlayGlobal(Sounds.ByName(soundName));

    /// <summary>Play a raw sample path globally (QC <c>play2all(samp)</c>).</summary>
    public static void PlayGlobalRaw(string sample, float volume = SoundLevels.VolBase)
        => Api.Sound.Play(GlobalEmitter, SoundChannel.Auto, sample, volume, SoundLevels.AttenNone);

    /// <summary>
    /// Play an announcer cue globally by its short name (e.g. "begin", "impressive") — looks up
    /// <c>ANNCE_&lt;NAME&gt;</c> in the catalog. Convenience over <see cref="PlayGlobal(string)"/> for the
    /// announcer family (notifications/all.inc MSG_ANNCE_NOTIF).
    /// </summary>
    public static void PlayAnnouncer(string shortName)
        => PlayGlobal(Sounds.ByName($"ANNCE_{shortName.ToUpperInvariant()}"));

    // ---- random-variant groups (QC SND_*_RANDOM) ----

    /// <summary>
    /// Play a random member of a registered variant group on <paramref name="emitter"/> (QC
    /// <c>sound(e, ch, SND_X_RANDOM(), …)</c>). <paramref name="groupPrefix"/> is e.g. "RIC" or "GIB_SPLAT0".
    /// </summary>
    public static void PlayRandom(Entity emitter, string groupPrefix)
        => PlayOn(emitter, SoundVariantGroups.PickRegistered(groupPrefix));

    /// <summary>Play a random ricochet (QC SND_RIC_RANDOM).</summary>
    public static void PlayRic(Entity emitter) => PlayOn(emitter, SoundVariantGroups.Ric());

    /// <summary>Play a random gib splat (QC SND_GIB_SPLAT_RANDOM).</summary>
    public static void PlayGibSplat(Entity emitter) => PlayOn(emitter, SoundVariantGroups.GibSplat());

    /// <summary>Play a random grenade bounce (QC SND_GRENADE_BOUNCE_RANDOM).</summary>
    public static void PlayGrenadeBounce(Entity emitter) => PlayOn(emitter, SoundVariantGroups.GrenadeBounce());

    // ---- GlobalSound counted variants (QC GlobalSound_sample / _GlobalSound) ----

    /// <summary>
    /// Play a counted GlobalSound (STEP/FALL/…) on <paramref name="emitter"/>, picking a numbered variant
    /// file via <see cref="SoundVariantGroups.ResolveGlobalSample"/>. Successor to QC <c>GlobalSound(e, def, …)</c>.
    /// </summary>
    public static void PlayGlobalVariant(Entity emitter, GameSound? sound)
    {
        if (sound is null) return;
        Api.Sound.Play(emitter, sound.EngineChannel,
            SoundVariantGroups.ResolveGlobalSample(sound), sound.Volume, sound.Attenuation);
    }

    /// <summary>Look up a counted GlobalSound by name (QC SND_&lt;name&gt;) then play a variant on the emitter.</summary>
    public static void PlayGlobalVariant(Entity emitter, string soundName)
        => PlayGlobalVariant(emitter, Sounds.ByName(soundName));

    // ---- per-model player / voice sounds (QC PlayerSound / VoiceMessage) ----

    /// <summary>
    /// Play a player body sound (QC <c>PlayerSound(this, def, …)</c>) for <paramref name="emitter"/>,
    /// resolving the sample under the emitter's model sound directory. <paramref name="id"/> is a
    /// <see cref="Sounds.PlayerSoundIds"/> entry (e.g. "pain50", "jump"); <paramref name="modelSoundDir"/>
    /// is the model's sound dir (null/empty => the default pack). Channel defaults to CH_VOICE-ish Voice,
    /// volume to VOL_BASEVOICE, matching QC.
    /// </summary>
    public static void PlayPlayerSound(Entity emitter, string id, string? modelSoundDir = null,
        float volume = SoundLevels.VolBaseVoice, float attenuation = SoundLevels.AttenNorm)
    {
        string sample = Sounds.PlayerSoundSample(modelSoundDir, id);
        // QC PlayerSound plays pain/voice on the AUTO voice channel (CH_VOICE = -2) so overlapping cues stack
        // rather than cut each other off; the rate of these is bounded upstream (pain by the PainFinished
        // debounce, voice/death once per event), so they don't pile up.
        Api.Sound.Play(emitter, SoundChannel.VoiceAuto, sample, volume, attenuation);
    }

    /// <summary>
    /// Play a voice message (QC VoiceMessage — team radio / taunt) for <paramref name="emitter"/>. Same
    /// per-model resolution as <see cref="PlayPlayerSound"/>; <paramref name="id"/> is a
    /// <see cref="Sounds.VoiceMessageIds"/> entry (e.g. "attack", "taunt"). This is the simple "play on the
    /// emitter" path; for the full VOICETYPE recipient/gate routing use <see cref="GlobalSound"/>.
    /// </summary>
    public static void PlayVoiceMessage(Entity emitter, string id, string? modelSoundDir = null)
        => PlayPlayerSound(emitter, id, modelSoundDir, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm);

    // ---- VOICETYPE routing (QC _GlobalSound, globalsound.qc:341-465) -------------------------------

    /// <summary>
    /// Route a voice message by its <see cref="VoiceMessages"/> id through the full VOICETYPE dispatch
    /// (QC <c>VoiceMessage(this, def, msg)</c> -&gt; <c>_GlobalSound</c>). Resolves the message's
    /// <see cref="VoiceType"/> and applies the matching recipient set + gates. <paramref name="recipients"/>
    /// is the live real-client roster the host supplies (QC FOREACH_CLIENT) — required for TEAMRADIO and
    /// TAUNT/AUTOTAUNT which broadcast to a subset; null/empty falls back to playing on the emitter only.
    /// </summary>
    public static void GlobalSound(Entity emitter, string voiceMessageId,
        IReadOnlyList<Entity>? recipients = null, string? modelSoundDir = null, bool fake = false)
    {
        VoiceType vt = VoiceMessages.VoiceTypeOf(voiceMessageId);
        _GlobalSound(emitter, voiceMessageId, vt, recipients, modelSoundDir, fake);
    }

    /// <summary>
    /// The C# successor to QC <c>_GlobalSound</c> (globalsound.qc:341): dispatch a voice/player sound to the
    /// recipients selected by <paramref name="voiceType"/>, applying the directional attenuation and the
    /// sv_autotaunt / sv_taunt / sv_gentle gates. The recipient sets mirror QC exactly:
    /// <list type="bullet">
    ///   <item><b>LASTATTACKER / LASTATTACKER_ONLY</b>: heard by the emitter's <see cref="Entity.Pusher"/>
    ///         (the last attacker), and — for LASTATTACKER (not _ONLY) — also by the emitter.</item>
    ///   <item><b>TEAMRADIO</b>: heard by every recipient on the emitter's team (QC SAME_TEAM).</item>
    ///   <item><b>AUTOTAUNT</b>: gated by <c>sv_autotaunt</c>; <b>TAUNT</b> gated by <c>sv_taunt</c> and
    ///         suppressed by <c>sv_gentle</c>; both broadcast to all recipients.</item>
    ///   <item><b>PLAYERSOUND</b>: broadcast to everyone at ATTEN_NORM.</item>
    /// </list>
    /// </summary>
    /// <param name="fake">QC <c>fake</c> (globalsound.qc:341): when true the sound is heard ONLY by the emitter
    /// (every voicetype branch collapses to <c>msg_entity = this; …</c> at MSG_ONE). Driven by the VoiceMessage
    /// macro from the chat flood/spectator gate (fake = IS_SPEC || IS_OBSERVER || Say-flood &lt; 0): a spectator,
    /// observer, or flood-faked speaker hears their own voice taunt but no one else does.</param>
    public static void _GlobalSound(Entity emitter, string voiceMessageId, VoiceType voiceType,
        IReadOnlyList<Entity>? recipients, string? modelSoundDir = null, bool fake = false)
    {
        // QC: if(this.classname == "body") return; — corpses don't speak.
        if (emitter is null || emitter.IsCorpse) return;

        // DEVIATION: QC picks the directional attenuation per-recipient from CS_CVAR(it).cvar_cl_voice_directional
        // (ATTEN_MIN when 1, else ATTEN_NONE). The Godot-free layer has no per-client cvar table, so the
        // directional cases use the directional default (ATTEN_MIN); the per-recipient cvar read is deferred.
        string sample = Sounds.PlayerSoundSample(modelSoundDir, voiceMessageId);

        switch (voiceType)
        {
            case VoiceType.LastAttackerOnly:
            case VoiceType.LastAttacker:
            {
                // QC: if(!fake) { if(!this.pusher) break; … } — a faked speaker skips the attacker emit.
                if (!fake && emitter.Pusher is { } attacker)
                    Emit(attacker, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenMin);
                // QC: LASTATTACKER_ONLY stops here; LASTATTACKER also plays back to the speaker at ATTEN_NONE.
                if (voiceType == VoiceType.LastAttackerOnly) break;
                Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNone);
                break;
            }

            case VoiceType.TeamRadio:
            {
                // QC: if(fake) { msg_entity = this; X(); } — a faked speaker hears it alone.
                if (fake) { Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenMin); break; }
                // QC: FOREACH_CLIENT(IS_REAL_CLIENT(it) && SAME_TEAM(it, this), …) at the directional atten.
                if (recipients is null) { Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenMin); break; }
                foreach (Entity it in recipients)
                    if (it is not null && Teams.SameTeam(it, emitter))
                        Emit(it, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenMin);
                break;
            }

            case VoiceType.AutoTaunt:
            case VoiceType.Taunt:
            {
                // QC: gate the autotaunt on sv_autotaunt; the manual taunt on sv_taunt and !sv_gentle.
                if (voiceType == VoiceType.AutoTaunt)
                {
                    if (!CvarBool(VoiceCvars.SvAutotaunt)) break;
                }
                if (!CvarBool(VoiceCvars.SvTaunt)) break;
                if (CvarBool(VoiceCvars.SvGentle)) break;
                // QC: if(fake) { msg_entity = this; X(); } — a faked speaker hears it alone.
                if (fake) { Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm); break; }
                // QC broadcasts to all real clients (FOREACH_CLIENT(IS_REAL_CLIENT(it))) at the taunt atten.
                if (recipients is null) { Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm); break; }
                foreach (Entity it in recipients)
                    if (it is not null)
                        Emit(it, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm);
                break;
            }

            case VoiceType.PlayerSound:
            default:
            {
                // QC: globalsound(MSG_ALL, …, ATTEN_NORM) — heard by everyone at the emitter's position.
                Emit(emitter, sample, SoundLevels.VolBaseVoice, SoundLevels.AttenNorm);
                break;
            }
        }
    }

    /// <summary>Emit a resolved voice sample on the CH_VOICE channel (QC sound7/soundto from _GlobalSound).</summary>
    private static void Emit(Entity e, string sample, float volume, float attenuation)
        => Api.Sound.Play(e, SoundChannel.VoiceAuto, sample, volume, attenuation);

    /// <summary>Read a boolean cvar via the facade (a non-zero float is true; unset/no-services is false).</summary>
    private static bool CvarBool(string name)
        => Api.Services is not null && Api.Cvars.GetFloat(name) != 0f;

    /// <summary>Reset the shared emitter (test support; also drops a freed emitter reference).</summary>
    public static void Reset() => _globalEmitter = null;
}
