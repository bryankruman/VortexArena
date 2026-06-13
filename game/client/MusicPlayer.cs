// Client-side music player — the Godot node that actually plays background music for maps.
//
// Implements the Xonotic/DP priority system (from QC TargetMusic_Advance):
//   Priority (highest wins): trigger_music > target_music > cdtrack (default)
//
// Each frame, the player selects the highest-priority active music source, crossfades to its track
// (fading out the old, fading in the new), and loops it. The music is NON-POSITIONAL (heard equally
// everywhere) and routes to the "Music" Godot audio bus (controlled by the bgmvolume cvar).
//
// For the listen-server path, target_music/trigger_music entities live in the same process, so we read
// their state directly (no networking needed). The server's GameWorld exposes the entities via the
// standard Api.Entities scan.

using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Non-positional music playback node (the cdtrack / target_music / trigger_music renderer).
/// Added as a child of <see cref="ClientWorld"/> or the net host node. Each frame it evaluates
/// the priority stack and crossfades between tracks.
/// </summary>
public sealed partial class MusicPlayer : Node
{
    // ---- configuration (set by the host before/after AddChild) ----

    /// <summary>The cdtrack for the current map (resolved from the mapinfo file). Empty = no default music.</summary>
    public string CdTrack { get; set; } = "";

    /// <summary>
    /// Loads a sound sample path to a decoded <see cref="AudioStream"/> from the VFS (same delegate as
    /// <c>ClientWorld.AudioLoader</c>). Used to load the music .ogg files from the content packs.
    /// </summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>Default fade-in/fade-out time in seconds when switching tracks.</summary>
    public float DefaultFadeTime { get; set; } = 2.0f;

    /// <summary>Volume multiplier from bgmvolume cvar (0-1). Updated externally each frame or on cvar change.</summary>
    public float BgmVolume { get; set; } = 0.7f;

    // ---- live state ----
    private AudioStreamPlayer? _playerA;   // the two crossfade slots
    private AudioStreamPlayer? _playerB;
    private AudioStreamPlayer? _active;    // which slot is currently the "live" one
    private AudioStreamPlayer? _fading;    // which slot is fading out

    private string _activeTrack = "";      // the track path currently playing on _active
    private float _activeVolume = 1f;      // target volume (from the music source's .volume field)
    private float _fadeInProgress = 1f;    // 0..1, how far through the fade-in the active track is
    private float _fadeOutProgress = 1f;   // 0..1, how far through the fade-out the fading track is
    private float _fadeInTime = 2f;        // seconds for the current fade-in
    private float _fadeOutTime = 2f;       // seconds for the current fade-out
    private float _activeFadeOutSpec;      // the ACTIVE source's own fade_rate (0 = unset → DefaultFadeTime)

    // ---- music source scan ----
    // These are populated by the host (listen server) each frame or on entity state change.
    // For simplicity in the listen-server path, we scan the entity list directly.

    /// <summary>
    /// Optional: direct reference to the server world's entity list for scanning target_music / trigger_music.
    /// On the listen-server path the host sets this so we can read entity state directly.
    /// </summary>
    public IReadOnlyList<Entity>? EntityList { get; set; }

    /// <summary>Current server time (for trigger_music touch freshness check).</summary>
    public float ServerTime { get; set; }

    /// <summary>
    /// S5 (sv_threaded): the host's shared sim gate, set by NetGame ONLY when the listen server runs its
    /// simulation on a dedicated worker thread. <see cref="EntityList"/> is then the live server entity table
    /// the worker mutates, so the per-frame scan in <see cref="EvaluateMusicSources"/> must hold this gate to
    /// avoid racing a concurrent spawn/relink. Null on the default single-threaded path → no lock is taken and
    /// the scan is byte-for-byte the old behaviour.
    /// </summary>
    public object? SimGate { get; set; }

    // ---- constants ----
    private const string MusicBus = "Music";
    private const float TouchFreshnessWindow = 0.2f; // a trigger_music is "active" if touched within this many seconds

    public override void _Ready()
    {
        _playerA = new AudioStreamPlayer { Name = "MusicA", Bus = MusicBus, VolumeDb = -80f };
        _playerB = new AudioStreamPlayer { Name = "MusicB", Bus = MusicBus, VolumeDb = -80f };
        AddChild(_playerA);
        AddChild(_playerB);

        // Start the cdtrack if we have one at boot time.
        if (!string.IsNullOrEmpty(CdTrack))
            StartTrack(CdTrack, 1f, DefaultFadeTime);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // --- evaluate the priority stack to find the best music source ---
        // S5: the scan reads the live server entity list; when sv_threaded the host hands us its gate so this
        // can't race the worker's spawn/relink. SimGate is null on the default path → no lock, byte-identical.
        string bestTrack;
        float bestVolume, fadeIn, fadeOut;
        if (SimGate is not null)
        {
            lock (SimGate)
                EvaluateMusicSources(out bestTrack, out bestVolume, out fadeIn, out fadeOut);
        }
        else
        {
            EvaluateMusicSources(out bestTrack, out bestVolume, out fadeIn, out fadeOut);
        }

        // --- if the best track changed, start a crossfade ---
        // The OUTGOING track fades on ITS OWN fade_rate (captured when it started — QC TargetMusic_Advance
        // ramps each source down by frametime/its.fade_rate), the incoming one on its fade_time.
        if (!string.Equals(bestTrack, _activeTrack, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(bestTrack))
                StartTrack(bestTrack, bestVolume, fadeIn, fadeOut);
            else
                FadeOutCurrent(_activeFadeOutSpec);
        }
        else if (_active is not null)
        {
            // Same track, but volume may have changed (e.g. trigger_music volume differs from cdtrack).
            _activeVolume = bestVolume;
        }

        // --- drive the crossfade ---
        DriveFade(dt);
    }

    // ========================================================================
    //  Priority evaluation
    // ========================================================================

    // (§11 R7) The music sources are map-static (trigger_music/target_music spawn at map load), so the
    // per-frame priority scan iterates this small cached subset instead of walking EVERY entity (the old
    // full-list scan ran 3 passes × all entities × frame, with per-entity string ClassName compares —
    // under the SimGate lock when sv_threaded). Rebuilt when the EntityList reference changes (map load)
    // and on a slow safety interval in case a mod spawns one mid-match.
    private readonly List<Entity> _triggerMusic = new();
    private readonly List<Entity> _targetMusic = new();
    private IReadOnlyList<Entity>? _scannedList;
    private float _nextRescanAt = float.NegativeInfinity;
    private const float RescanInterval = 5f;

    private void RefreshMusicEntities()
    {
        bool stale = !ReferenceEquals(EntityList, _scannedList)
                     || ServerTime >= _nextRescanAt
                     || _nextRescanAt > ServerTime + RescanInterval;   // server clock went backwards
        if (!stale)
            return;
        _scannedList = EntityList;
        _nextRescanAt = ServerTime + RescanInterval;
        _triggerMusic.Clear();
        _targetMusic.Clear();
        if (EntityList is null)
            return;
        for (int i = 0; i < EntityList.Count; i++)
        {
            Entity e = EntityList[i];
            if (e.IsFreed) continue;
            if (e.ClassName == "trigger_music") _triggerMusic.Add(e);
            else if (e.ClassName == "target_music") _targetMusic.Add(e);
        }
    }

    private void EvaluateMusicSources(out string bestTrack, out float bestVolume, out float fadeIn, out float fadeOut)
    {
        bestTrack = "";
        bestVolume = 1f;
        fadeIn = DefaultFadeTime;
        fadeOut = DefaultFadeTime;

        // Highest priority: trigger_music (player inside a brush volume)
        if (EntityList is not null)
        {
            RefreshMusicEntities();

            // Scan for active trigger_music whose touch is fresh (player inside it this frame).
            Entity? bestTrigger = null;
            for (int i = 0; i < _triggerMusic.Count; i++)
            {
                Entity e = _triggerMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                // The touch handler stamps PushLTime each frame the player overlaps.
                // Consider it "active" if touched within the freshness window.
                if (ServerTime - e.PushLTime <= TouchFreshnessWindow)
                {
                    bestTrigger = e;
                    // Don't break — last one wins if multiple overlap (QC uses the last-touched),
                    // but for simplicity we take the first found.
                    break;
                }
            }
            if (bestTrigger is not null)
            {
                bestTrack = ResolveMusicPath(bestTrigger.Noise);
                bestVolume = bestTrigger.Volume > 0f ? bestTrigger.Volume : 1f;
                (fadeIn, fadeOut) = Fades(bestTrigger); // the REAL fade_time/fade_rate map keys (not .speed)
                return;
            }

            // Medium priority: a TRIGGERED target_music whose .lifetime window is still open.
            // QC (music.qc TargetMusic_Advance + Net_TargetMusic): a used target_music with lifetime>0
            // becomes music_target and outranks the default only while time < activation + lifetime; a used
            // one with lifetime 0 instead REPLACES the music_default slot (handled in the default pass below).
            Entity? bestTarget = null;
            for (int i = 0; i < _targetMusic.Count; i++)
            {
                Entity e = _targetMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                if (string.IsNullOrEmpty(e.TargetName)) continue;     // untargeted = default slot
                if (e.MusicActivationTime < 0f) continue;             // never triggered
                if (e.MusicLifetime <= 0f) continue;                  // lifetime 0 => default slot
                if (ServerTime >= e.MusicActivationTime + e.MusicLifetime) continue; // expired
                bestTarget = e;
                break;
            }
            if (bestTarget is not null)
            {
                bestTrack = ResolveMusicPath(bestTarget.Noise);
                bestVolume = bestTarget.Volume > 0f ? bestTarget.Volume : 1f;
                (fadeIn, fadeOut) = Fades(bestTarget);
                return;
            }

            // Default slot — overrides cdtrack. Prefer the most recently ACTIVATED lifetime-0 targeted
            // track (QC: Net_TargetMusic with tim==0 reassigns music_default), else the untargeted default.
            Entity? def = null;
            for (int i = 0; i < _targetMusic.Count; i++)
            {
                Entity e = _targetMusic[i];
                if (e.IsFreed) continue;
                if (e.Active != MapMover.ActiveActive) continue;
                if (string.IsNullOrEmpty(e.Noise)) continue;
                if (!string.IsNullOrEmpty(e.TargetName))
                {
                    // A used lifetime-0 track claims the default slot; latest activation wins.
                    if (e.MusicLifetime > 0f || e.MusicActivationTime < 0f) continue;
                    if (def is null || string.IsNullOrEmpty(def.TargetName)
                        || e.MusicActivationTime > def.MusicActivationTime)
                        def = e;
                }
                else if (def is null)
                {
                    def = e; // the untargeted map default (loses to any claimed lifetime-0 track)
                }
            }
            if (def is not null)
            {
                bestTrack = ResolveMusicPath(def.Noise);
                bestVolume = def.Volume > 0f ? def.Volume : 1f;
                (fadeIn, fadeOut) = Fades(def);
                return;
            }
        }

        // Lowest priority: cdtrack (the map's mapinfo default).
        if (!string.IsNullOrEmpty(CdTrack))
        {
            bestTrack = CdTrack;
            bestVolume = 1f;
            fadeIn = DefaultFadeTime;
            fadeOut = DefaultFadeTime;
        }
    }

    /// <summary>A source's fade pair: the plumbed QC <c>fade_time</c>/<c>fade_rate</c> map keys, falling back
    /// to <see cref="DefaultFadeTime"/> when unset (0). QC's 0 means "instant"; the port keeps its established
    /// gentle default crossfade instead — no shipped map carries the keys, so nothing regresses.</summary>
    private (float fadeIn, float fadeOut) Fades(Entity e) => (
        e.MusicFadeIn > 0f ? e.MusicFadeIn : DefaultFadeTime,
        e.MusicFadeOut > 0f ? e.MusicFadeOut : DefaultFadeTime);

    // ========================================================================
    //  Track management
    // ========================================================================

    /// <param name="track">Resolved VFS track path.</param>
    /// <param name="volume">The source's target volume.</param>
    /// <param name="fadeIn">Seconds to ramp the NEW track in (the source's fade_time).</param>
    /// <param name="fadeOutSpec">The NEW source's fade_rate — remembered so when THIS track is later
    /// replaced, it fades out on its own spec (QC ramps each source down by frametime/its.fade_rate).</param>
    private void StartTrack(string track, float volume, float fadeIn, float fadeOutSpec = 0f)
    {
        AudioStream? stream = LoadMusicStream(track);
        if (stream is null)
        {
            // Can't load this track — treat as silence.
            _activeTrack = track; // remember it so we don't keep retrying
            FadeOutCurrent(_activeFadeOutSpec);
            return;
        }

        // Make the stream loop.
        AudioStream looping = AudioLoop.MakeLooping(stream, out bool nativeLoop);

        // Determine which slot to use for the new track (the one NOT currently active).
        AudioStreamPlayer? newActive = (_active == _playerA) ? _playerB : _playerA;
        if (newActive is null) return;

        // Start fading out the old track (if any) — on the OLD source's fade_rate, captured when it started.
        if (_active is not null && _active.Playing)
        {
            _fading = _active;
            _fadeOutProgress = 0f;
            float fo = _activeFadeOutSpec > 0f ? _activeFadeOutSpec : DefaultFadeTime;
            _fadeOutTime = fo;
        }

        // Start the new track.
        newActive.Stream = looping;
        newActive.VolumeDb = -80f; // start silent, fade in
        if (!nativeLoop)
            newActive.Finished += () => { if (IsInstanceValid(newActive) && newActive.Stream == looping) newActive.Play(); };
        newActive.Play();

        _active = newActive;
        _activeTrack = track;
        _activeVolume = volume;
        _fadeInProgress = 0f;
        _fadeInTime = fadeIn > 0f ? fadeIn : DefaultFadeTime;
        _activeFadeOutSpec = fadeOutSpec; // this track's OWN fade_rate, used when IT later fades out
    }

    private void FadeOutCurrent(float fadeTime)
    {
        if (_active is not null && _active.Playing)
        {
            _fading = _active;
            _fadeOutProgress = 0f;
            _fadeOutTime = fadeTime > 0f ? fadeTime : DefaultFadeTime;
        }
        _active = null;
        _activeTrack = "";
    }

    // ========================================================================
    //  Crossfade driver
    // ========================================================================

    private void DriveFade(float dt)
    {
        // Fade in the active track.
        if (_active is not null && IsInstanceValid(_active) && _fadeInProgress < 1f)
        {
            _fadeInProgress += dt / _fadeInTime;
            if (_fadeInProgress > 1f) _fadeInProgress = 1f;
        }

        // Fade out the old track.
        if (_fading is not null && IsInstanceValid(_fading) && _fadeOutProgress < 1f)
        {
            _fadeOutProgress += dt / _fadeOutTime;
            if (_fadeOutProgress > 1f)
            {
                _fadeOutProgress = 1f;
                _fading.Stop();
                _fading = null;
            }
        }

        // Apply volumes.
        float bgm = Mathf.Clamp(BgmVolume, 0f, 1f);
        if (_active is not null && IsInstanceValid(_active))
        {
            float vol = _activeVolume * bgm * _fadeInProgress;
            _active.VolumeDb = vol <= 0.001f ? -80f : Mathf.LinearToDb(vol);
        }
        if (_fading is not null && IsInstanceValid(_fading))
        {
            float vol = bgm * (1f - _fadeOutProgress);
            _fading.VolumeDb = vol <= 0.001f ? -80f : Mathf.LinearToDb(vol);
        }
    }

    // ========================================================================
    //  Audio loading
    // ========================================================================

    private AudioStream? LoadMusicStream(string trackPath)
    {
        if (string.IsNullOrEmpty(trackPath))
            return null;

        // Try the AudioLoader (VFS-based, reads from mounted content packs).
        AudioStream? stream = AudioLoader?.Invoke(trackPath);
        if (stream is not null)
            return stream;

        // Fallback: try as a res:// path.
        string resPath = $"res://sound/{trackPath}";
        if (!resPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) &&
            !resPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            resPath += ".ogg";
        if (ResourceLoader.Exists(resPath))
        {
            try { return ResourceLoader.Load<AudioStream>(resPath); }
            catch { return null; }
        }
        return null;
    }

    // ========================================================================
    //  Track path resolution
    // ========================================================================

    /// <summary>
    /// Resolve a music track reference to the VFS path the AudioLoader expects.
    /// Handles the DP conventions:
    ///   - A bare number like "12" -> "cdtracks/track012" (3-digit zero-padded)
    ///   - A bare name like "neon" -> "cdtracks/neon"
    ///   - A path like "sound/cdtracks/neon.ogg" -> passed through (strip sound/ prefix for AudioLoader)
    ///   - A path like "cdtracks/neon" -> passed through
    /// </summary>
    public static string ResolveMusicPath(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        string trimmed = raw.Trim();

        // If it already has a directory separator, it's a relative path — normalize.
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            // Strip leading "sound/" if present (AudioLoader roots under sound/ itself).
            if (trimmed.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(6);
            // Strip extension (AudioLoader probes .ogg then .wav).
            if (trimmed.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 4);
            return trimmed;
        }

        // Bare value: check if it's a number (DP "cd loop 12" -> track012).
        if (int.TryParse(trimmed, out int trackNum) && trackNum > 0)
            return $"cdtracks/track{trackNum:D3}";

        // Bare name: assume it's under cdtracks/.
        return $"cdtracks/{trimmed}";
    }
}
