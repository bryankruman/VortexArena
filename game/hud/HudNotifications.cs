using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The client-side notification router — the Godot successor to CSQC's <c>Local_Notification</c> /
/// <c>Net_Notification</c> dispatch (Base/.../qcsrc/client/announcer.qc + the MSG_* handlers in
/// common/notifications/all.qc). The server serializes each <see cref="NotificationDispatch"/> and the
/// client (<c>game/net/ClientNet</c>) decodes it into (notification, type, text, string-args, float-args);
/// this class is what turns that decoded event into the actual on-screen / audible result, closing the
/// "server→client notification channel" gap:
///
/// <list type="bullet">
///   <item><b>MSG_CENTER</b> → <see cref="CenterPrintPanel.Push(string,float,string,int)"/> (centerprint,
///         with the QC <c>cpid</c> replace/kill id and the live <c>^COUNT</c> countdown token).</item>
///   <item><b>MSG_INFO</b> → <see cref="NotifyPanel"/>: a kill-feed obituary when the notification carries a
///         kill-notify <see cref="Notification.Icon"/> (attacker = s2, victim = s1, like QC), or a plain
///         info line otherwise (connects/scores/item lines).</item>
///   <item><b>MSG_ANNCE</b> → the announcer voice sample (a non-positional UI sound), resolved by
///         <see cref="AnnouncerResolver"/> (host-provided, VFS-backed) or the stock
///         <c>res://sound/announcer/default/&lt;snd&gt;.ogg</c> convention.</item>
/// </list>
///
/// It is decoupled from the net layer on purpose (it takes the already-decoded fields, not a
/// <c>ClientNet</c> type), so either the real network path OR the in-process gameplay path can feed it. For
/// the single-process demo, <see cref="InstallLocalSink"/> hooks <see cref="NotificationSystem.Sink"/> so a
/// server-side <c>Send_Notification</c> in this process is mirrored straight onto the HUD (mirrors how
/// <c>ClientWorld</c> installs an <see cref="IEffectSink"/> for effects).
/// </summary>
public sealed class HudNotifications
{
    private readonly Hud _hud;

    /// <summary>Resolves an announcer sound name (QC <c>snd</c>, e.g. "headshot") to a Godot <c>res://</c> audio
    /// resource path; host-provided. Used only as the fallback when <see cref="AudioLoader"/> is unset or misses.</summary>
    public Func<string, string?>? AnnouncerResolver { get; set; }

    /// <summary>Loads an announcer sample straight to an <see cref="AudioStream"/> from the mounted asset VFS
    /// (host-set to <c>AssetLoader.LoadSound</c>). The primary path — reads <c>sound/announcer/&lt;voice&gt;/&lt;snd&gt;.ogg</c>
    /// out of the mounted content. Tried before the <see cref="AnnouncerResolver"/> <c>res://</c> fallback.</summary>
    public Func<string, AudioStream?>? AudioLoader { get; set; }

    /// <summary>The announcer voice pack (QC <c>cl_announcer</c>); art lives at <c>sound/announcer/&lt;voice&gt;/</c>.
    /// Xonotic ships only "default".</summary>
    public string AnnouncerVoice { get; set; } = "default";

    /// <summary>Master gain for announcer voices (QC <c>cl_autotaunt</c>/<c>cl_announcer</c> volume), 0..1.</summary>
    public float AnnouncerVolume { get; set; } = 1f;

    private AudioStreamPlayer? _announcer;
    private INotificationSink? _previousSink;

    // ---- Announcer queue + anti-spam (QC cl_announcer_antispam) ----

    /// <summary>Anti-spam window in seconds: if the same sound is requested within this interval, skip it.</summary>
    public float AntiSpamInterval { get; set; } = 2f;

    /// <summary>Maximum queued announcer sounds (prevents buildup in extreme cases).</summary>
    private const int MaxQueueSize = 5;

    private readonly List<string> _announcerQueue = new();
    private string _lastAnnouncerSound = "";
    private double _lastAnnouncerTime = -100.0;

    public HudNotifications(Hud hud) => _hud = hud ?? throw new ArgumentNullException(nameof(hud));

    // =====================================================================================
    //  Entry point — one decoded notification (from ClientNet or the local sink)
    // =====================================================================================

    /// <summary>
    /// Route one decoded notification to the HUD. <paramref name="notif"/> may be null if the registry id
    /// didn't resolve (then we fall back to <paramref name="text"/> alone). <paramref name="text"/> is the
    /// server-formatted message (INFO/CENTER) or the announcer sound name (ANNCE); <paramref name="strs"/>/
    /// <paramref name="flts"/> are the raw s1..s4 / f1..f4 args (so the client can re-derive attacker/victim
    /// and the countdown count).
    /// </summary>
    public void OnNotification(Notification? notif, MsgType type, string text, string[] strs, float[] flts)
    {
        switch (type)
        {
            case MsgType.Center:
                ShowCenter(notif, text, flts);
                break;
            case MsgType.Info:
                ShowInfo(notif, text, strs);
                break;
            case MsgType.Annce:
                // The announcer "text" is the sound name (the server sent notif.Sound as the text).
                PlayAnnouncer(!string.IsNullOrEmpty(text) ? text : notif?.Sound ?? "");
                break;
            default:
                // MSG_MULTI/MSG_CHOICE are fanned out server-side into INFO/CENTER/ANNCE before the wire, so
                // they never arrive here; ignore defensively.
                break;
        }
    }

    /// <summary>Convenience overload taking the same tuple <c>ClientNet.NotificationEvent</c> carries.</summary>
    public void OnNotification(in DecodedNotification ev)
        => OnNotification(ev.Notification, ev.Type, ev.Text, ev.StringArgs, ev.FloatArgs);

    /// <summary>A net-decoupled mirror of <c>ClientNet.NotificationEvent</c> the host can adapt to.</summary>
    public readonly record struct DecodedNotification(
        Notification? Notification, MsgType Type, string Text, string[] StringArgs, float[] FloatArgs);

    // =====================================================================================
    //  MSG_CENTER
    // =====================================================================================

    private void ShowCenter(Notification? notif, string text, float[] flts)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        string cpid = notif?.Cpid ?? "";
        // QC durcnt encodes an optional COUNT token + duration; we don't carry it on the wire, so drive the
        // countdown from the first float arg when the message actually contains the ^COUNT placeholder.
        int count = -1;
        if (text.Contains(CenterPrintPanel.CountToken, StringComparison.Ordinal) && flts.Length > 0)
            count = Math.Max(0, (int)flts[0]);

        _hud.CenterPrint.Push(text, CenterPrintPanel.DefaultDuration, cpid, count);
    }

    // =====================================================================================
    //  MSG_INFO  (kill feed + info lines)
    // =====================================================================================

    private void ShowInfo(Notification? notif, string text, string[] strs)
    {
        // A kill-notify icon marks an obituary (QC: MSG_INFO entries with an icon go to HUD_Notify with the
        // weapon/death pic). Murders carry s1=victim, s2=attacker (notifications/all.inc DEATH_MURDER args);
        // self-deaths carry s1=victim only.
        string icon = notif?.Icon ?? "";
        if (!string.IsNullOrEmpty(icon) && strs.Length >= 1)
        {
            bool murder = strs.Length >= 2 && !string.IsNullOrEmpty(strs[1]);
            string victim = strs[0];
            string? attacker = murder ? strs[1] : null;
            _hud.Notify.Push(attacker, victim, icon);
            return;
        }

        // Non-kill info (connects, scores, item lines): show as a plain right-aligned line (attacker-less),
        // which NotifyPanel renders as the victim slot alone. Color codes in the text are honored.
        if (!string.IsNullOrWhiteSpace(text))
            _hud.Notify.Push(null, text, "");
    }

    // =====================================================================================
    //  MSG_ANNCE  (announcer voice)
    // =====================================================================================

    /// <summary>Play an announcer voice sample by its bare name (QC <c>announcer_play</c>). No-op if missing.
    /// Applies anti-spam (same sound within <see cref="AntiSpamInterval"/> is skipped) and queues if the
    /// announcer is already playing (up to <see cref="MaxQueueSize"/> entries).</summary>
    public void PlayAnnouncer(string soundName)
    {
        if (string.IsNullOrWhiteSpace(soundName) || AnnouncerVolume <= 0f)
            return;

        // Anti-spam: skip if the same sound was requested within the anti-spam window.
        double now = Time.GetTicksMsec() / 1000.0;
        if (soundName == _lastAnnouncerSound && (now - _lastAnnouncerTime) < AntiSpamInterval)
            return;

        // If the announcer is currently playing, queue the new sound (capped).
        EnsureAnnouncer();
        if (_announcer!.Playing)
        {
            if (_announcerQueue.Count < MaxQueueSize)
                _announcerQueue.Add(soundName);
            return;
        }

        PlayAnnouncerImmediate(soundName);
    }

    /// <summary>Advance the announcer queue: if the player finished and the queue is not empty, dequeue and play.</summary>
    public void ProcessAnnouncerQueue()
    {
        if (_announcer is null || !GodotObject.IsInstanceValid(_announcer))
            return;
        if (_announcer.Playing || _announcerQueue.Count == 0)
            return;

        string next = _announcerQueue[0];
        _announcerQueue.RemoveAt(0);
        PlayAnnouncerImmediate(next);
    }

    /// <summary>Immediately start playing an announcer sound (no queue check, no anti-spam check).</summary>
    private void PlayAnnouncerImmediate(string soundName)
    {
        AudioStream? stream = LoadAnnouncerStream(soundName);
        if (stream is null)
            return;

        EnsureAnnouncer();
        _announcer!.Stream = stream;
        _announcer.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(AnnouncerVolume, 0.001f, 1f));
        _announcer.Play();

        _lastAnnouncerSound = soundName;
        _lastAnnouncerTime = Time.GetTicksMsec() / 1000.0;
    }

    /// <summary>
    /// Resolve an announcer voice name to a stream: the VFS <see cref="AudioLoader"/> first
    /// (<c>sound/announcer/&lt;voice&gt;/&lt;snd&gt;.ogg</c> from the mounted content), then the
    /// <see cref="ResolveAnnouncer"/> <c>res://</c> fallback. Null = silent (missing voice/sample).
    /// </summary>
    private AudioStream? LoadAnnouncerStream(string soundName)
    {
        AudioStream? stream = AudioLoader?.Invoke($"announcer/{AnnouncerVoice}/{soundName}");
        if (stream is not null)
            return stream;

        string? resPath = ResolveAnnouncer(soundName);
        if (string.IsNullOrEmpty(resPath) || !ResourceLoader.Exists(resPath))
            return null;
        try { return ResourceLoader.Load<AudioStream>(resPath); }
        catch { return null; }
    }

    private string? ResolveAnnouncer(string soundName)
    {
        if (AnnouncerResolver is not null)
            return AnnouncerResolver(soundName);
        // Stock layout: sound/announcer/<voice>/<snd>.ogg — default voice when the host hasn't chosen one.
        return $"res://sound/announcer/default/{soundName}.ogg";
    }

    private void EnsureAnnouncer()
    {
        if (_announcer is not null && GodotObject.IsInstanceValid(_announcer))
            return;
        _announcer = new AudioStreamPlayer { Name = "Announcer", Bus = "Master" };
        _hud.AddChild(_announcer);
    }

    // =====================================================================================
    //  In-process bridge (single-process demo) — mirror NotificationSystem.Sink onto the HUD
    // =====================================================================================

    /// <summary>
    /// Install a <see cref="INotificationSink"/> that mirrors every in-process <c>Send_Notification</c> onto
    /// this HUD (chaining the previously installed sink so recording/networking still happen). Call from the
    /// host once the HUD exists; <see cref="UninstallLocalSink"/> on teardown. This is the notification
    /// analogue of <c>ClientWorld.RenderSink</c> for effects — it makes the demo show notifications without a
    /// network round-trip. A real networked client instead feeds <see cref="OnNotification"/> from ClientNet.
    /// </summary>
    public void InstallLocalSink()
    {
        if (NotificationSystem.Sink is HudSink)
            return;
        _previousSink = NotificationSystem.Sink;
        NotificationSystem.Sink = new HudSink(this, _previousSink);
    }

    /// <summary>Restore the notification sink that was active before <see cref="InstallLocalSink"/>.</summary>
    public void UninstallLocalSink()
    {
        if (NotificationSystem.Sink is HudSink hs)
            NotificationSystem.Sink = hs.Inner;
    }

    /// <summary>
    /// A notification sink that forwards each dispatch to whatever sink was previously installed (so the
    /// recorder/network sink keeps working) and then mirrors it onto the HUD. Building Godot nodes must happen
    /// on the scene thread, so HUD work is deferred via <see cref="Callable"/> (a dispatch may originate from
    /// a sim tick).
    /// </summary>
    private sealed class HudSink : INotificationSink
    {
        private readonly HudNotifications _owner;
        public INotificationSink Inner { get; }

        public HudSink(HudNotifications owner, INotificationSink inner)
        {
            _owner = owner;
            Inner = inner;
        }

        public void Dispatch(in NotificationDispatch d)
        {
            Inner.Dispatch(d);

            // Capture by value (struct copy of the immutable arrays' references — they're not mutated).
            Notification n = d.Notification;
            MsgType type = n.Type;
            string text = d.Text;
            string[] strs = d.StringArgs;
            float[] flts = d.FloatArgs;
            HudNotifications owner = _owner;

            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(owner._hud))
                    owner.OnNotification(n, type, text, strs, flts);
            }).CallDeferred();
        }
    }
}
