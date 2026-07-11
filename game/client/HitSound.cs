// Port of qcsrc/client/view.qc HitSound() + UpdateDamage() — the client-side hit/typehit/kill feedback sounds.
// The state machine (accumulate → antispam window → pitch; stat-time-advance typehit/kill) lives in the
// testable XonoticGodot.Net.HitSoundLogic; this wrapper owns the cvar reads and the Godot audio player.

using Godot;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The three client hit-feedback sounds (QC <c>HitSound()</c> in <c>view.qc</c>), driven per frame by the
/// owner's networked/local feedback stats via <see cref="Update"/>:
/// <list type="bullet">
///   <item><b>misc/hit</b> — the damage-confirm beep. Damage accumulates across the antispam window
///   (<c>cl_hitsound_antispam_time</c>, 0.05s) and plays ONE beep per window — never dropped, at most one
///   window late. <c>cl_hitsound</c>: 0 off, 1 fixed pitch, 2 pitch falls with damage, 3 rises.</item>
///   <item><b>misc/typehit</b> — the team-hit / chatting-victim "dink" (TYPEHIT_TIME advance).</item>
///   <item><b>misc/kill</b> — the kill confirm (KILL_TIME advance). The server's flush gives the kill
///   priority over the hit beep, so the killing blow plays ONLY this.</item>
/// </list>
/// All three share one non-positional player on the SFX bus — QC plays them on the same CH_INFO channel of
/// the world entity, so a new cue REPLACES a still-ringing one (the kill sound cuts a trailing beep).
/// Typehit/kill are NOT gated by <c>cl_hitsound</c>, exactly like QC.
/// </summary>
public sealed class HitSound
{
    private readonly CvarService? _cvars;
    private readonly HitSoundLogic _logic = new();
    private AudioStreamPlayer? _player;
    private AudioStream? _hitStream, _typeHitStream, _killStream;
    private bool _hitProbed, _typeHitProbed, _killProbed; // one probe per sample; a miss stays silent
    private Node? _parent;

    /// <summary>Load a feedback sample from the mounted VFS (host-set loader; probes .ogg then .wav).</summary>
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

    /// <summary>Forget the stat baselines: the next <see cref="Update"/> re-seeds silently (reconnect).</summary>
    public void Reset() => _logic.Reset();

    /// <summary>
    /// Per-frame update with the owner's feedback stats — a listen host passes the live server Player's
    /// fields, a pure client the networked <c>ClientNet.LocalState</c> slice. Returns true when NEW confirmed
    /// damage registered this frame (the crosshair hit-indication flash — QC crosshair.qc:387 reads the same
    /// accumulator).
    /// </summary>
    public bool Update(bool haveArc, int spectatee, float hitTime, float damageTotal, float typeHitTime, float killTime)
    {
        int mode = (int)CvarOr("cl_hitsound", 1f);
        float antispam = CvarOr("cl_hitsound_antispam_time", HitSoundLogic.DefaultAntispamTime);
        float maxPitch = CvarOr("cl_hitsound_max_pitch", HitSoundLogic.DefaultMaxPitch);
        float minPitch = CvarOr("cl_hitsound_min_pitch", HitSoundLogic.DefaultMinPitch);
        float nomDamage = CvarOr("cl_hitsound_nom_damage", HitSoundLogic.DefaultNomDamage);

        float now = Time.GetTicksMsec() / 1000f;
        HitSoundCues cues = _logic.Update(now, mode, antispam, maxPitch, minPitch, nomDamage,
            haveArc, spectatee, hitTime, damageTotal, typeHitTime, killTime);

        // At most one fires per server frame (the flush's typehit > kill > hit priority); play in that order
        // anyway so a same-client-frame pileup resolves like QC's channel-replace would.
        if (cues.PlayHit)
            Play(ref _hitStream, ref _hitProbed, "misc/hit", cues.HitPitch);
        if (cues.PlayKill)
            Play(ref _killStream, ref _killProbed, "misc/kill", 1f);
        if (cues.PlayTypeHit)
            Play(ref _typeHitStream, ref _typeHitProbed, "misc/typehit", 1f);
        return cues.NewDamage;
    }

    /// <summary>Read a float cvar, falling back to the QC default when the cvar isn't registered/set
    /// (the store returns 0 for unknown names, which would silently disable/degenerate the curve).</summary>
    private float CvarOr(string name, float fallback)
    {
        if (_cvars is null) return fallback;
        string s = _cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : _cvars.GetFloat(name);
    }

    private void Play(ref AudioStream? stream, ref bool probed, string sample, float pitch)
    {
        if (stream is null)
        {
            if (probed) return; // known-missing sample — stay silent instead of re-probing every beep
            probed = true;
            stream = AudioLoader?.Invoke(sample);
            if (stream is null)
            {
                // Fallback: the res:// path convention (the loader probes .ogg then .wav; mirror with .wav).
                string resPath = $"res://sound/{sample}.wav";
                if (ResourceLoader.Exists(resPath))
                {
                    try { stream = ResourceLoader.Load<AudioStream>(resPath); }
                    catch { /* silent */ }
                }
            }
            if (stream is null) return;
        }

        EnsurePlayer();
        if (_player is null)
            return;

        // One shared player = QC's single CH_INFO channel: a new cue replaces a still-playing one.
        _player.Stop();
        _player.Stream = stream;
        _player.PitchScale = pitch;
        _player.Play();
    }

    private void EnsurePlayer()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player))
            return;
        if (_parent is null || !GodotObject.IsInstanceValid(_parent))
            return;

        _player = new AudioStreamPlayer { Name = "HitSound", Bus = "SFX" };
        // QC plays VOL_BASE; kept slightly quieter (the port's existing deliberate tweak) so the beep
        // doesn't mask the announcer.
        _player.VolumeDb = Mathf.LinearToDb(0.7f);
        _parent.AddChild(_player);
    }
}
