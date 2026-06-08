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
//   ACTIVATOR  (8) — play only to the triggering player (deferred: plays to all for now)
//
// Behavior modes:
//   1. LOOPED_ON without targetname: ambient sound — plays immediately and forever (ambientsound equivalent)
//   2. LOOPED_ON with targetname: starts playing, can be toggled off/on via .Use triggers
//   3. LOOPED_OFF: starts silent, toggled on/off via .Use triggers
//   4. Neither flag (one-shot): each .Use trigger plays the sound once (not looping)
//   5. GLOBAL: sets ATTEN_NONE so the sound is heard at full volume everywhere
//   6. ACTIVATOR: per-client sound (plays to all in this port — MSG_ONE not yet supported)

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

    // The channel target_speaker uses for its sounds. QC uses CH_TRIGGER_SINGLE which is a single-channel
    // replacement key — the same entity+channel pair replaces any previous sound on that key. TriggerAuto
    // (-3) is the auto-channel variant; but for looping toggle behavior we need a SINGLE channel so Stop()
    // can target the exact (entity, channel) pair. We use Body (4) as it is unlikely to collide with other
    // map object sounds on the same entity, and it gives us single-channel replacement semantics.
    private const SoundChannel SpeakerChannel = SoundChannel.Body;

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

        // --- targetname present: triggerable speaker ---
        if (!string.IsNullOrEmpty(this_.TargetName))
        {
            if (loopedOn)
            {
                // Starts playing immediately; can be toggled OFF when triggered.
                StartLoop(this_);
                this_.Use = SpeakerUseOff;
            }
            else if (loopedOff)
            {
                // Starts silent; first trigger starts the loop.
                this_.Use = SpeakerUseOn;
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

        if (Api.Services is null || string.IsNullOrEmpty(self.Noise))
            return;

        Api.Sound.Play(self, SpeakerChannel, self.Noise, self.Volume, self.Atten, loop: false);
    }

    // ====================================================================
    //  Loop start/stop helpers
    // ====================================================================

    /// <summary>Start (or confirm) the looping sound on this speaker entity.</summary>
    private static void StartLoop(Entity self)
    {
        if (Api.Services is null || string.IsNullOrEmpty(self.Noise))
            return;

        Api.Sound.Play(self, SpeakerChannel, self.Noise, self.Volume, self.Atten, loop: true);
    }

    /// <summary>Stop the looping sound on this speaker entity.</summary>
    private static void StopLoop(Entity self)
    {
        if (Api.Services is null)
            return;

        Api.Sound.Stop(self, SpeakerChannel);
    }
}
