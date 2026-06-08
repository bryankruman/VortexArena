// Port of qcsrc/client/view.qc HitSound() — the client-side hit-confirmation feedback sound.
// Driven by the cl_hitsound cvar (0=off, 1=fixed, 2=decreasing pitch, 3=increasing pitch).

using Godot;
using XonoticGodot.Engine.Simulation;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Client-side hit-confirmation sound (QC <c>HitSound()</c> in <c>view.qc</c>). Plays a non-positional
/// feedback beep when the local player's shot deals damage, with pitch varying by mode:
/// <list type="bullet">
///   <item>Mode 0: disabled.</item>
///   <item>Mode 1: fixed pitch (1.0) — default.</item>
///   <item>Mode 2: decreasing pitch with more damage (high pitch = low damage, low pitch = high damage).</item>
///   <item>Mode 3: increasing pitch with more damage (higher pitch = more damage).</item>
/// </list>
/// The pitch formulas mirror the QC reference (cl_hitsound_nom_pitch / min_pitch / max_pitch).
/// </summary>
public sealed class HitSound
{
    // QC defaults: cl_hitsound_nom_pitch, cl_hitsound_min_pitch, cl_hitsound_max_pitch
    private const float NomPitch = 1.0f;
    private const float MinPitch = 0.75f;
    private const float MaxPitch = 1.5f;

    /// <summary>Nominal damage for pitch scaling (QC approximation). Damage above this clips to max/min pitch.</summary>
    private const float NomDamage = 25f;

    /// <summary>Maximum damage for the pitch ramp (approx one Devastator direct).</summary>
    private const float MaxDamage = 100f;

    /// <summary>Anti-spam interval in seconds (QC cl_hitsound_antispam_time default 0.05).</summary>
    private const float AntiSpamInterval = 0.05f;

    private readonly CvarService? _cvars;
    private AudioStreamPlayer? _player;
    private AudioStream? _stream;
    private double _lastPlayTime = -1.0;
    private Node? _parent;

    /// <summary>Load the hitsound stream from the mounted VFS (host-set loader).</summary>
    public System.Func<string, AudioStream?>? AudioLoader { get; set; }

    public HitSound(CvarService? cvars)
    {
        _cvars = cvars;
    }

    /// <summary>Attach to a parent node (so the AudioStreamPlayer can live in the scene tree).</summary>
    public void Attach(Node parent)
    {
        _parent = parent;
    }

    /// <summary>
    /// Fire the hitsound for a confirmed hit dealing <paramref name="damage"/> points. Reads the
    /// <c>cl_hitsound</c> cvar for the mode (0=off, 1=fixed, 2=decreasing, 3=increasing).
    /// </summary>
    public void OnHit(float damage)
    {
        int mode = (int)(_cvars?.GetFloat("cl_hitsound") ?? 1f);
        if (mode <= 0)
            return;

        // Anti-spam: don't stack multiple hitsounds within a tiny window (shotgun pellet spray).
        double now = Time.GetTicksMsec() / 1000.0;
        if ((now - _lastPlayTime) < AntiSpamInterval)
            return;
        _lastPlayTime = now;

        float pitch = ComputePitch(mode, damage);

        EnsurePlayer();
        if (_player is null)
            return;

        _player.PitchScale = pitch;
        _player.Play();
    }

    /// <summary>Compute the pitch for the given mode and damage amount.</summary>
    private static float ComputePitch(int mode, float damage)
    {
        // Clamp damage into [0, MaxDamage] for the ramp.
        float d = Mathf.Clamp(damage, 0f, MaxDamage);
        float frac = d / MaxDamage; // 0..1

        switch (mode)
        {
            case 2:
                // Decreasing: more damage = lower pitch. QC formula:
                // pitch = bound(min, nom + (nom - min) * (1 - damage/maxdamage), max)
                return Mathf.Clamp(NomPitch + (NomPitch - MinPitch) * (1f - frac), MinPitch, MaxPitch);

            case 3:
                // Increasing: more damage = higher pitch. QC formula:
                // pitch = bound(min, nom + (max - nom) * (damage/maxdamage), max)
                return Mathf.Clamp(NomPitch + (MaxPitch - NomPitch) * frac, MinPitch, MaxPitch);

            default:
                // Mode 1 (fixed) or any unknown mode: always 1.0.
                return NomPitch;
        }
    }

    private void EnsurePlayer()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player))
            return;
        if (_parent is null || !GodotObject.IsInstanceValid(_parent))
            return;

        // Load the hitsound sample (the stock Xonotic hitsound lives at sound/misc/hitconfirm.ogg).
        if (_stream is null)
        {
            _stream = AudioLoader?.Invoke("misc/hitconfirm");
            if (_stream is null)
            {
                // Fallback: try the res:// path convention.
                const string resPath = "res://sound/misc/hitconfirm.ogg";
                if (ResourceLoader.Exists(resPath))
                {
                    try { _stream = ResourceLoader.Load<AudioStream>(resPath); }
                    catch { /* silent */ }
                }
            }
        }
        if (_stream is null)
            return;

        _player = new AudioStreamPlayer { Name = "HitSound", Bus = "SFX", Stream = _stream };
        _player.VolumeDb = Mathf.LinearToDb(0.7f); // slightly quieter than full to avoid masking announcer
        _parent.AddChild(_player);
    }
}
