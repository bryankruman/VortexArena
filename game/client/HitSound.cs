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
/// Pitch for modes 2/3 uses the QC asymptotic gradient function from <c>view.qc:937-949</c>:
/// a customizable curve crossing (0,a), (c,1) and approaching b asymptotically, then mirrored for mode 3.
/// </summary>
public sealed class HitSound
{
    // QC defaults (view.qh:82-84): cl_hitsound_max_pitch=1.5, cl_hitsound_min_pitch=0.75, cl_hitsound_nom_damage=25.
    // These are read as cvars at play time so the player can override them.
    private const float DefaultMaxPitch  = 1.5f;
    private const float DefaultMinPitch  = 0.75f;
    private const float DefaultNomDamage = 25f;

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

        // Read the pitch curve cvars (QC view.qh:82-84 autocvar_ defaults).
        float maxPitch  = _cvars?.GetFloat("cl_hitsound_max_pitch")  ?? DefaultMaxPitch;
        float minPitch  = _cvars?.GetFloat("cl_hitsound_min_pitch")  ?? DefaultMinPitch;
        float nomDamage = _cvars?.GetFloat("cl_hitsound_nom_damage") ?? DefaultNomDamage;
        float pitch = ComputePitch(mode, damage, maxPitch, minPitch, nomDamage);

        EnsurePlayer();
        if (_player is null)
            return;

        _player.PitchScale = pitch;
        _player.Play();
    }

    /// <summary>
    /// Compute the playback pitch for the given mode and accumulated damage, using the QC asymptotic gradient
    /// function (view.qc:937-949).
    /// <para>
    /// QC formula (mode 2, the base curve):
    /// <c>pitch = (b*d*(a-1) + a*c*(1-b)) / (d*(a-1) + c*(1-b))</c>
    /// where a=max_pitch, b=min_pitch, c=nom_damage, d=damage.
    /// This is a hyperbolic curve crossing (0, a), (c, 1) and asymptotically approaching b as d→∞.
    /// </para>
    /// <para>
    /// Mode 3 mirrors the result around the midpoint <c>(a-b)/2 + b</c>:
    /// <c>pitch = mirror + (mirror - pitch_mode2)</c> — so low damage → lower pitch, high → higher.
    /// </para>
    /// </summary>
    internal static float ComputePitch(int mode, float damage, float maxPitch, float minPitch, float nomDamage)
    {
        if (mode != 2 && mode != 3)
            return 1.0f; // Mode 1 fixed, or unknown.

        // Guard against degenerate cvar values (division by zero).
        float a = maxPitch;
        float b = minPitch;
        float c = nomDamage;
        float d = damage;

        float denom = d * (a - 1f) + c * (1f - b);
        float pitch;
        if (System.MathF.Abs(denom) < 1e-6f)
        {
            // Degenerate: a==1 and b==1, or c==0. Fall back to nominal pitch.
            pitch = 1.0f;
        }
        else
        {
            // QC view.qc:942: pitch = (b*d*(a-1) + a*c*(1-b)) / (d*(a-1) + c*(1-b))
            pitch = (b * d * (a - 1f) + a * c * (1f - b)) / denom;
        }

        if (mode == 3)
        {
            // QC view.qc:946-949: mirror in (a-b)/2 + b to reverse the curve direction.
            float mirror = (a - b) * 0.5f + b;
            pitch = mirror + (mirror - pitch);
        }

        // Clamp to [min, max] to match QC's implied bound() (the function approaches but never crosses).
        return Mathf.Clamp(pitch, b, a);
    }

    private void EnsurePlayer()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player))
            return;
        if (_parent is null || !GodotObject.IsInstanceValid(_parent))
            return;

        // QC all.inc:219 registers SND(HIT, "misc/hit"); the shipped file is sound/misc/hit.wav.
        // Load the hitsound sample from that path.
        if (_stream is null)
        {
            _stream = AudioLoader?.Invoke("misc/hit");
            if (_stream is null)
            {
                // Fallback: try the res:// path convention.
                const string resPath = "res://sound/misc/hit.wav";
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
