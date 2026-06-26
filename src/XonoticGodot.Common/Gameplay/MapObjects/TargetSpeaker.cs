// Port of qcsrc/common/mapobjects/target/speaker.qc (target_speaker).
//
// target_speaker is the map entity that provides ALL ambient/environmental audio in Xonotic maps: wind,
// machinery hum, lava bubbling, event sounds triggered by buttons/doors/logic, etc. It uses the existing
// looping sound infrastructure (ISoundService.Play with loop:true) for persistent ambient loops, and
// one-shot plays for triggered event sounds.
//
// Spawnflags (from speaker.qh):
//   LOOPED_ON  (1) — starts playing on spawn, loops continuously
//   LOOPED_OFF (2) — starts silent, first trigger starts the loop (toggleable)
//   GLOBAL     (4) — sets attenuation to ATTEN_NONE (heard everywhere, no distance falloff)
//   ACTIVATOR  (8) — play only to the triggering REAL client (gated; plays to all for now — no MSG_ONE)
//
// Behavior modes:
//   1. LOOPED_ON without targetname: ambient sound — plays immediately and forever (ambientsound equivalent)
//   2. LOOPED_ON with targetname: starts playing, can be toggled off/on via .Use triggers
//   3. LOOPED_OFF: starts silent, toggled on/off via .Use triggers
//   4. Neither flag (one-shot): each .Use trigger plays the sound once (not looping)
//   5. GLOBAL: sets ATTEN_NONE so the sound is heard at full volume everywhere
//   6. ACTIVATOR: per-client sound (plays to all in this port — MSG_ONE not yet supported)
//
// *-prefixed noise: a per-player voice-message sample. QC resolves via GetVoiceMessageSampleField +
// argv(1) random count against the activator's player-sounds manifest. The port resolves via
// Sounds.PlayerSoundSample(Sounds.ModelSoundsFile(activator.Model, skin), voiceId), which routes through
// the host-installed ModelSoundResolver (PlayerSoundResolver) and its built-in variant randomization.
// When there is no activator (ambient loop, null entity), the *-name resolves to silence (QC SND(Null)
// fallback), exactly as before.

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// <c>target_speaker</c> — ambient/environmental and triggered sound emitter. Each setup method is a
/// spawnfunc registered by <see cref="MapObjectsRegistry"/>.
/// </summary>
public static class TargetSpeaker
{
    // ---- spawnflag bits (speaker.qh) ----
    private const int SpeakerLoopedOn = 1 << 0;   // BIT(0) — start looping on spawn
    private const int SpeakerLoopedOff = 1 << 1;  // BIT(1) — start silent, togglable loop
    private const int SpeakerGlobal = 1 << 2;     // BIT(2) — no distance falloff (ATTEN_NONE)
    private const int SpeakerActivator = 1 << 3;  // BIT(3) — per-client sound (MSG_ONE; deferred)

    // QC channel mapping (common/sounds/sound.qh):
    //   CH_TRIGGER_SINGLE = 3  -> SoundChannel.Item  — single-channel replacement key; the looped/toggle and
    //                                                  the non-activator one-shot use this so Stop() can target
    //                                                  the exact (entity, channel) pair and a re-play replaces.
    //   CH_TRIGGER        = -3 -> SoundChannel.TriggerAuto — the STACKING auto channel; QC's
    //                                                  target_speaker_use_activator (soundto MSG_ONE) emits on it.
    private const SoundChannel SpeakerChannel = SoundChannel.Item;        // CH_TRIGGER_SINGLE
    private const SoundChannel SpeakerActivatorChannel = SoundChannel.TriggerAuto; // CH_TRIGGER

    /// <summary><c>spawnfunc(target_speaker)</c> — ambient and triggered sound emitter.</summary>
    public static void SpeakerSetup(Entity this_)
    {
        this_.ClassName = "target_speaker";

        // --- resolve attenuation (QC logic from speaker.qc) ---
        float atten = this_.Atten;
        bool loopedOn = (this_.SpawnFlags & SpeakerLoopedOn) != 0;
        bool loopedOff = (this_.SpawnFlags & SpeakerLoopedOff) != 0;
        bool isGlobal = (this_.SpawnFlags & SpeakerGlobal) != 0;

        // Q3 compat: GLOBAL flag with atten==0 and neither looped flag -> set atten to -1 (will become ATTEN_NONE)
        if (atten == 0f && isGlobal && !loopedOn && !loopedOff)
            atten = -1f;

        // Default atten when unset (0): if it has a targetname use ATTEN_NORM, otherwise ATTEN_STATIC (tight ambient)
        if (atten == 0f)
            atten = !string.IsNullOrEmpty(this_.TargetName) ? SoundLevels.AttenNorm : SoundLevels.AttenStatic;

        // Negative atten -> ATTEN_NONE (play everywhere, no distance falloff)
        if (atten < 0f)
            atten = SoundLevels.AttenNone;

        this_.Atten = atten;

        // --- resolve volume (default to 1.0 if not set) ---
        if (this_.Volume == 0f)
            this_.Volume = 1f;

        // --- validate noise ---
        if (string.IsNullOrEmpty(this_.Noise))
        {
            // No sound sample specified — this speaker is useless. QC would objerror; we just skip.
            return;
        }

        bool isActivator = (this_.SpawnFlags & SpeakerActivator) != 0;

        // --- targetname present: triggerable speaker ---
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            if (isActivator)
            {
                // ACTIVATOR (BIT3): each trigger plays the sound ONLY to the triggering real client.
                // QC checks this flag FIRST (before the looped flags), so an ACTIVATOR speaker is always
                // a per-activator one-shot regardless of any looped flag also being set.
                this_.Use = SpeakerUseActivator;
            }
            else if (loopedOn)
            {
                // Starts playing immediately; can be toggled OFF when triggered.
                // QC: target_speaker_use_on(this, NULL, NULL) + this.reset = target_speaker_reset
                StartLoop(this_);
                this_.Use = SpeakerUseOff;
                // QC speaker.qc:111 — LOOPED_ON installs a reset hook to re-arm on round restart
                this_.Reset = SpeakerReset;
            }
            else if (loopedOff)
            {
                // Starts silent; first trigger starts the loop.
                // QC: this.use = target_speaker_use_on + this.reset = target_speaker_reset
                this_.Use = SpeakerUseOn;
                // QC speaker.qc:116 — LOOPED_OFF installs a reset hook to re-arm on round restart
                this_.Reset = SpeakerReset;
            }
            else
            {
                // One-shot: each trigger plays the sound once (no looping).
                this_.Use = SpeakerUseOneShot;
            }

            this_.Active = MapMover.ActiveActive;
            MapMover.IndexRegister(this_);
            return;
        }

        // --- no targetname: untriggerable ambient ---
        if (loopedOn || (!loopedOn && !loopedOff))
        {
            // Play the looping ambient immediately. QC would use ambientsound() then delete the entity;
            // we keep the entity alive as the sound emitter so the looping-sound infrastructure (which is
            // keyed by entity+channel) has a stable source to attach to.
            StartLoop(this_);
            MapMover.IndexRegister(this_);
        }
        else if (loopedOff)
        {
            // LOOPED_OFF without a targetname: can never be activated. QC logs an error.
            // Nothing to do — the entity is inert.
        }
    }

    // ====================================================================
    //  Use handlers (toggle on/off / one-shot)
    // ====================================================================

    /// <summary>Toggle ON: start the loop, then swap .Use to the OFF handler.</summary>
    private static void SpeakerUseOn(Entity self, Entity activator)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        StartLoop(self);
        self.Use = SpeakerUseOff;
    }

    /// <summary>Toggle OFF: stop the loop, then swap .Use to the ON handler.</summary>
    private static void SpeakerUseOff(Entity self, Entity activator)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        StopLoop(self);
        self.Use = SpeakerUseOn;
    }

    /// <summary>One-shot: play the sound once each time triggered (not looping).</summary>
    private static void SpeakerUseOneShot(Entity self, Entity activator)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        if (Api.Services is null)
            return;

        string snd = ResolveNoise(self.Noise);
        if (string.IsNullOrEmpty(snd))
            return;

        Api.Sound.Play(self, SpeakerChannel, snd, self.Volume, self.Atten, loop: false);
    }

    /// <summary>
    /// ACTIVATOR (BIT3): play the sound on the stacking CH_TRIGGER channel for the triggering player.
    /// QC <c>target_speaker_use_activator</c> gates on <c>IS_REAL_CLIENT(actor)</c> and emits via
    /// <c>soundto(MSG_ONE, ...)</c> so only that one human hears it. The port has no MSG_ONE per-client
    /// sound facade, so we emit to all (documented divergence) but still honour the real-client gate so a
    /// bot/world activator produces nothing — matching Base's early-out.
    /// </summary>
    private static void SpeakerUseActivator(Entity self, Entity activator)
    {
        if (self.Active != MapMover.ActiveActive)
            return;

        // QC: if (!IS_REAL_CLIENT(actor)) return;
        if (!IsRealClient(activator))
            return;

        if (Api.Services is null)
            return;

        // QC: *-noise resolves against the activating player's voice-sample manifest (GetVoiceMessageSampleField)
        string snd = ResolveNoise(self.Noise, activator);
        if (string.IsNullOrEmpty(snd))
            return;

        // TODO(cross-file): no MSG_ONE / per-client sound facade — this plays to ALL clients, not just the
        // triggering activator. Needs an ISoundService.PlayTo(client, ...) seam to be byte-faithful.
        Api.Sound.Play(self, SpeakerActivatorChannel, snd, self.Volume, self.Atten, loop: false);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>
    /// QC <c>IS_REAL_CLIENT(actor)</c>: a connected, non-bot (human) client. World/projectile/bot activators
    /// fail this and produce no per-activator sound.
    /// </summary>
    private static bool IsRealClient(Entity actor)
        => actor is not null
           && (actor.Flags & EntFlags.Client) != 0
           && !(actor is Player p && p.IsBot);

    /// <summary>
    /// Resolve the <c>.noise</c> sample, optionally against an <paramref name="activator"/> entity.
    /// <para>
    /// A leading <c>*</c> means this is a per-player voice-message sample: QC maps the id after the <c>*</c>
    /// to the activating player's voice-sound manifest field (<c>GetVoiceMessageSampleField</c>) and picks a
    /// random variant via <c>argv(1)</c>. The port equivalent is <see cref="Sounds.PlayerSoundSample"/> routed
    /// through the host-installed <see cref="Sounds.ModelSoundResolver"/> (PlayerSoundResolver), which already
    /// handles variant-count randomization. Requires a non-null activator with a model; if no activator is
    /// available (ambient loop invocation, null entity), the <c>*</c>-name resolves to silence — matching QC's
    /// <c>SND(Null)</c> fallback when the player's sample field is empty or GetPlayerSoundSampleField returns
    /// not-found.
    /// </para>
    /// </summary>
    /// <param name="noise">The raw <c>.noise</c> value on the speaker entity.</param>
    /// <param name="activator">Optional triggering entity (for <c>*</c>-prefix voice-sample resolution).</param>
    private static string ResolveNoise(string noise, Entity? activator = null)
    {
        if (string.IsNullOrEmpty(noise))
            return string.Empty;

        if (noise[0] == '*')
        {
            // *-prefixed: a per-player voice-message sample.
            // QC: GetVoiceMessageSampleField(noise[1..]) -> .string field on actor -> argv(0)/argv(1) count.
            // Port: resolve via the host-installed ModelSoundResolver using the activator's model.
            if (activator is null || string.IsNullOrEmpty(activator.Model))
                return string.Empty; // no activator / no model: SND(Null) fallback (silent), matching QC not-found

            string voiceId = noise.Substring(1); // strip the leading '*'
            // Sounds.PlayerSoundSample routes through the host ModelSoundResolver which handles random variants
            // (count > 0 => picks randomly among {path}1..{path}N, per QC argv(1) / ftos(floor(random()*n+1))).
            string? modelSoundsFile = Sounds.ModelSoundsFile(activator.Model, (int)activator.Skin);
            string resolved = Sounds.PlayerSoundSample(modelSoundsFile, voiceId);
            // PlayerSoundSampleRaw falls back to a manifest-path concat that won't resolve on disk; treat as silence.
            if (string.IsNullOrEmpty(resolved) || resolved.EndsWith(".sounds/" + voiceId, System.StringComparison.Ordinal))
                return string.Empty;
            return resolved;
        }

        return noise;
    }

    // ====================================================================
    //  Round-restart reset (QC target_speaker_reset, speaker.qc:63-75)
    // ====================================================================

    /// <summary>
    /// Port of QC <c>target_speaker_reset</c> (speaker.qc:63): fired on every map entity when a round
    /// restarts (<see cref="GameWorld.ResetMapObjects"/>). Re-arms the loop state to match the spawnflag:
    /// <list type="bullet">
    ///   <item><b>LOOPED_ON</b>: if the loop was toggled OFF (Use == SpeakerUseOn), restart it now.</item>
    ///   <item><b>LOOPED_OFF</b>: if the loop was toggled ON (Use == SpeakerUseOff), silence it now.</item>
    /// </list>
    /// QC checks <c>this.use == target_speaker_use_on</c> / <c>target_speaker_use_off</c> — the port mirrors
    /// that with the C# static-method delegate comparisons.
    /// </summary>
    private static void SpeakerReset(Entity self)
    {
        if ((self.SpawnFlags & SpeakerLoopedOn) != 0)
        {
            // LOOPED_ON reset: if currently OFF (Use was swapped to the ON handler) -> restart the loop.
            // QC: if(this.use == target_speaker_use_on) target_speaker_use_on(this, NULL, NULL);
            if (self.Use == SpeakerUseOn)
            {
                StartLoop(self);
                self.Use = SpeakerUseOff;
            }
        }
        else if ((self.SpawnFlags & SpeakerLoopedOff) != 0)
        {
            // LOOPED_OFF reset: if currently ON (Use was swapped to the OFF handler) -> silence it.
            // QC: if(this.use == target_speaker_use_off) target_speaker_use_off(this, NULL, NULL);
            if (self.Use == SpeakerUseOff)
            {
                StopLoop(self);
                self.Use = SpeakerUseOn;
            }
        }
    }

    // ====================================================================
    //  Loop start/stop helpers
    // ====================================================================

    /// <summary>Start (or confirm) the looping sound on this speaker entity.</summary>
    private static void StartLoop(Entity self)
    {
        if (Api.Services is null)
            return;

        string snd = ResolveNoise(self.Noise);
        if (string.IsNullOrEmpty(snd))
            return;

        Api.Sound.Play(self, SpeakerChannel, snd, self.Volume, self.Atten, loop: true);
    }

    /// <summary>Stop the looping sound on this speaker entity.</summary>
    private static void StopLoop(Entity self)
    {
        if (Api.Services is null)
            return;

        Api.Sound.Stop(self, SpeakerChannel);
    }
}
