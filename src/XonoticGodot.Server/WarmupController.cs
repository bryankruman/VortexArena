using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The warmup + ready-restart flow — the Godot-free essence of the QC warmup stage and the
/// <c>ReadyRestart</c> / <c>ReadyRestart_force</c> / <c>ReadyCount</c> / <c>reset_map</c> machinery
/// (server/command/vote.qc + server/world.qc). Warmup is the pre-match phase where players join, gear up,
/// and ready up (F4); once a majority is ready (or the warmup limit elapses) the match restarts into a
/// short countdown and goes live.
///
/// State tracked (the QC globals): the warmup stage flag (<see cref="WarmupStage"/>, QC <c>warmup_stage</c>),
/// the per-player ready set (QC <c>.ready</c> / <c>readycount</c>), the warmup time limit
/// (<see cref="WarmupLimit"/>, QC <c>warmup_limit</c>), and the match start time / countdown
/// (<see cref="GameStartTime"/>, QC <c>game_starttime</c>, owned by <see cref="GameWorld"/> but driven here).
///
/// The actual map reset (re-spawn players, reset map objects, clear scores) is delegated to
/// <see cref="ResetMap"/>, which <see cref="GameWorld"/> wires to its own reset (the QC <c>reset_map</c>
/// FOREACH_CLIENT PutClientInServer + the entity <c>.reset</c> callbacks).
///
/// Deferred: team locking, the spectate==2 demotion, the campaign 3s countdown, the Nagger network entity,
/// and timeout interaction — a host can layer those on the callbacks.
/// </summary>
public sealed class WarmupController
{
    /// <summary>QC RESTART_COUNTDOWN: seconds of countdown after warmup ends before the match goes live.</summary>
    public const float RestartCountdown = 5f;

    private readonly HashSet<Player> _ready = new();

    /// <summary>QC <c>warmup_stage</c>: true while the match is in the pre-game warmup phase.</summary>
    public bool WarmupStage { get; private set; }

    /// <summary>
    /// QC <c>warmup_limit</c>: absolute-ish warmup duration in seconds. -1 = wait until ready; 0 = use the
    /// match timelimit; &gt;0 = that many seconds. Resolved from <c>g_warmup_limit</c> at <see cref="Begin"/>.
    /// </summary>
    public float WarmupLimit { get; private set; } = -1f;

    /// <summary>QC <c>game_starttime</c>: absolute sim time the match goes live (the GameWorld mirrors this).</summary>
    public float GameStartTime { get; set; }

    /// <summary>QC <c>readycount</c>: how many players have readied up.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>
    /// Supplies the roster the ready-majority is computed against (QC FOREACH_CLIENT(IS_PLAYER)). The host
    /// wires this to its connected players.
    /// </summary>
    public Func<IReadOnlyList<Player>> Roster { get; set; } = static () => System.Array.Empty<Player>();

    /// <summary>
    /// The map-reset action (QC <c>reset_map</c>): re-spawn players, reset map objects, and (when
    /// <c>fakeRoundStart</c> is false) clear scores. The host wires this to <see cref="GameWorld"/>'s reset.
    /// </summary>
    public Action<bool>? ResetMap { get; set; }

    /// <summary>Fired when warmup ends and the match restart begins (QC sv_hook_warmupend / the countdown).</summary>
    public Action? OnMatchRestart { get; set; }

    /// <summary>
    /// T40: fired once per whole second of the pre-match (game-start) countdown — the server-side analogue of
    /// the CSQC <c>Announcer_Countdown</c> CNT_GAMESTART tick (this port has no CSQC announcer timer). The
    /// argument is the visible seconds remaining (the QC <c>countdown_rounded</c> = floor(0.5 + secondsLeft)):
    /// it counts down to 1, then fires 0 on the tick the match goes live. <see cref="GameWorld"/> maps it to the
    /// broadcasts: <c>NUM_GAMESTART_&lt;n&gt;</c> (the registry self-gates to n≤5) + <c>COUNTDOWN_GAMESTART(n)</c>
    /// for n≥1, <c>BEGIN</c> at n==0, and <c>PREPARE</c> on the first tick when the countdown exceeds 5s (QC
    /// <c>time + 5.0 &lt; startTime</c>). Fires once per second only (not every frame), so the announcer isn't
    /// spammed.
    /// </summary>
    public Action<int>? OnCountdownTick { get; set; }

    private bool _started;
    private float _warmupStartTime;

    // T40: the last whole-second value handed to OnCountdownTick, so Think() fires it once per second (not every
    // frame) and once for the terminal 0. int.MinValue = no tick emitted yet for the current countdown.
    private int _lastCountdownSecond = int.MinValue;

    /// <summary>True once <see cref="Begin"/> has run (the warmup/countdown flow is driving the start time).</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// QC the warmup setup at world spawn: enter the warmup stage if <c>g_warmup</c> is set, resolve the
    /// warmup limit, and pin <see cref="GameStartTime"/> until the match is readied. Call once at boot after
    /// the cvars are registered. When warmup is disabled, the match starts immediately (countdown only).
    /// </summary>
    public void Begin()
    {
        _started = true;
        WarmupStage = Cvars.WarmupStage;
        _ready.Clear();
        _lastCountdownSecond = int.MinValue; // T40: re-arm the game-start countdown announcer.

        if (WarmupStage)
        {
            ResolveWarmupLimit();
            _warmupStartTime = Now;
            // QC: during warmup there's no countdown; game_starttime sits in the future until ready.
            GameStartTime = WarmupLimit < 0f ? float.MaxValue : _warmupStartTime + WarmupLimit;
        }
        else
        {
            // No warmup: a short countdown then live (QC restart into match stage).
            WarmupLimit = 0f;
            GameStartTime = Now + RestartCountdown;
        }
    }

    private void ResolveWarmupLimit()
    {
        // QC: warmup_limit = g_warmup_limit; if 0 -> timelimit*60; -1 stays "until ready".
        float lim = Cvars.FloatOr("g_warmup_limit", -1f);
        if (lim == 0f)
            lim = Cvars.TimeLimitMinutes * 60f;
        WarmupLimit = lim;
    }

    /// <summary>
    /// QC <c>ClientCommand_ready</c>: toggle a player's ready flag (F4). Returns the new ready state. Re-counts
    /// and, if the ready-majority threshold is met, triggers the restart (<see cref="ReadyRestart"/>). No-op
    /// outside warmup (QC only counts ready during warmup / when sv_ready_restart is enabled).
    /// </summary>
    public bool ToggleReady(Player p)
    {
        bool nowReady = !_ready.Contains(p);
        if (nowReady) _ready.Add(p);
        else _ready.Remove(p);

        ReadyCountCheck();
        return nowReady;
    }

    /// <summary>Clear a player's ready flag (e.g. on disconnect / move to spectator), then re-check.</summary>
    public void ClearReady(Player p)
    {
        if (_ready.Remove(p))
            ReadyCountCheck();
    }

    /// <summary>
    /// QC <c>ReadyCount</c>: if enough players are ready (the <c>g_warmup_majority_factor</c> fraction, min 1),
    /// end warmup and restart the match. Called whenever the ready set changes. Only acts during warmup.
    /// </summary>
    public void ReadyCountCheck()
    {
        if (!WarmupStage)
            return;

        var roster = Roster();
        int players = roster.Count; // QC ReadyCount counts every player (bots included)
        if (players == 0)
            return;

        float factor = Cvars.FloatOr("g_warmup_majority_factor", 0.8f);
        int needed = System.Math.Max(1, (int)MathF.Ceiling(players * System.Math.Clamp(factor, 0f, 1f)));
        if (ReadyCount >= needed)
            ReadyRestart(forceWarmupEnd: true);
    }

    /// <summary>
    /// Drive warmup one frame (QC the StartFrame warmup check): when the warmup limit elapses, end warmup
    /// and restart into the match. No-op when warmup is disabled or already ended. Call once per server frame.
    /// </summary>
    public void Think()
    {
        if (!_started)
            return;

        // T40: drive the pre-match (game-start) countdown announcer at 1 Hz (QC CSQC Announcer_Countdown,
        // CNT_GAMESTART). Only outside warmup, while time < game_starttime. Mirrors countdown_rounded going
        // count..1 then a terminal 0 (BEGIN). Fires the callback at most once per whole second + once at 0.
        if (!WarmupStage)
        {
            float remaining = GameStartTime - Now;
            if (remaining > 0f)
            {
                int cr = (int)MathF.Floor(0.5f + remaining); // QC countdown_rounded
                if (cr >= 1 && cr != _lastCountdownSecond)
                {
                    _lastCountdownSecond = cr;
                    OnCountdownTick?.Invoke(cr);
                }
            }
            else if (_lastCountdownSecond != 0)
            {
                // countdown finished (time >= game_starttime): fire BEGIN exactly once.
                _lastCountdownSecond = 0;
                OnCountdownTick?.Invoke(0);
            }
        }

        if (!WarmupStage)
            return;
        if (WarmupLimit > 0f && Now - _warmupStartTime >= WarmupLimit)
            ReadyRestart(forceWarmupEnd: true);
    }

    /// <summary>
    /// QC <c>ReadyRestart</c> → <c>ReadyRestart_force</c>: leave warmup (when forced or no warmup configured),
    /// arm the match countdown via <see cref="GameStartTime"/>, clear all ready flags, fire the restart
    /// callback, and reset the map. With <c>g_warmup</c> still enabled and not forced, this restarts back into
    /// a fresh warmup instead.
    /// </summary>
    public void ReadyRestart(bool forceWarmupEnd)
    {
        _started = true; // the warmup/countdown flow now drives the match start time

        // QC: warmup_stage = forceWarmupEnd ? 0 : autocvar_g_warmup.
        WarmupStage = forceWarmupEnd ? false : Cvars.WarmupStage;

        // QC ReadyRestart_force: set game_starttime.
        GameStartTime = WarmupStage ? Now /* no countdown in warmup */ : Now + RestartCountdown;

        _ready.Clear();
        _lastCountdownSecond = int.MinValue; // T40: a restart arms a fresh game-start countdown to announce.

        if (WarmupStage)
            ResolveWarmupLimit();

        OnMatchRestart?.Invoke();

        // QC: reset_map(false) re-spawns players + resets map objects + clears scores (full restart).
        ResetMap?.Invoke(/*fakeRoundStart*/ false);
    }

    /// <summary>True if the match is currently in its pre-live countdown (QC <c>time &lt; game_starttime</c> live phase).</summary>
    public bool CountdownRunning => _started && !WarmupStage && Now < GameStartTime;

    /// <summary>Seconds remaining on the countdown (for HUD), 0 once live.</summary>
    public float CountdownSecondsLeft => System.Math.Max(0f, GameStartTime - Now);

    private static float Now => Api.Services is not null ? Api.Clock.Time : 0f;
}
