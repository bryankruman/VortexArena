// Port of qcsrc/server/round_handler.qc — the shared round-elimination state machine used by the
// round-based gametypes (Clan Arena, Freeze Tag, and the round-based variants of others). In QuakeC this
// was a single `round_handler` edict whose think function (round_handler_Think) cycled through:
//   wait-for-players  → countdown  → round-in-progress  → end-delay  → (next round)
// driven by three callbacks the gametype installs (round_handler_Spawn): canRoundStart, canRoundEnd,
// roundStart. We reproduce that driver headlessly as a plain object the gametype owns and ticks; it is
// Godot-free and depends only on the engine clock via the gametype's own time source.

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The round-elimination driver — the Godot-free port of QuakeC's <c>round_handler</c> (server/round_handler.qc).
/// A gametype creates one and ticks it every frame; it sequences the wait/countdown/round/end-delay phases and
/// invokes the supplied callbacks:
///  - <see cref="CanRoundStart"/> (QC canRoundStart): may the countdown proceed? (every team has players);
///  - <see cref="CanRoundEnd"/> (QC canRoundEnd): is the round decided? (a team won / stalemate) — returns true
///    when the round is over;
///  - <see cref="OnRoundStart"/> (QC roundStart): called once when the countdown hits zero and the round begins.
/// The countdown length, end-delay, and per-round time limit mirror the QC <c>round_handler_Init</c> args.
/// </summary>
public sealed class RoundHandler
{
    /// <summary>The phase the handler is in (QC round_handler think branches).</summary>
    public enum RoundPhase
    {
        /// <summary>Awaiting the countdown start gate (QC wait == true between rounds).</summary>
        WaitingToStart,
        /// <summary>Counting down to the round start (QC cnt &gt; 0).</summary>
        Countdown,
        /// <summary>The round is live (QC cnt == 0).</summary>
        InProgress,
    }

    /// <summary>QC time accessor (the gametype supplies the sim clock so this stays Godot-free).</summary>
    private readonly Func<float> _now;

    /// <summary>QC canRoundStart: true once a round may begin (every active team has a live player).</summary>
    public Func<bool> CanRoundStart { get; set; } = () => true;

    /// <summary>QC canRoundEnd: true once the current round is decided (a winner or stalemate exists).</summary>
    public Func<bool> CanRoundEnd { get; set; } = () => false;

    /// <summary>QC roundStart: invoked once when the round actually begins (the countdown reached zero).</summary>
    public Action OnRoundStart { get; set; } = () => { };

    /// <summary>QC .count: the countdown length in seconds (default 5).</summary>
    public float CountdownLength = 5f;

    /// <summary>QC .delay: seconds between a round ending and the next countdown starting (default 5).</summary>
    public float EndDelay = 5f;

    /// <summary>QC .round_timelimit: per-round time limit in seconds (0 = none).</summary>
    public float RoundTimeLimit = 180f;

    public RoundPhase Phase { get; private set; } = RoundPhase.WaitingToStart;

    /// <summary>Absolute sim time the countdown completes and the round starts (QC round_starttime).</summary>
    public float RoundStartTime { get; private set; }

    /// <summary>Absolute sim time the round's time limit expires (QC round_endtime); 0 = none.</summary>
    public float RoundEndTime { get; private set; }

    /// <summary>1-based count of rounds that have started (QC rounds_played).</summary>
    public int RoundsPlayed { get; private set; }

    public RoundHandler(Func<float> now) => _now = now;

    /// <summary>QC round_handler_Init: (re)configure the timing for the next round.</summary>
    public void Init(float endDelay, float countdownLength, float roundTimeLimit)
    {
        EndDelay = endDelay > 0f ? endDelay : 0f;
        CountdownLength = MathF.Abs(MathF.Floor(countdownLength));
        RoundTimeLimit = roundTimeLimit > 0f ? roundTimeLimit : 0f;
    }

    /// <summary>Begin a fresh wait→countdown cycle (QC the initial wait state).</summary>
    public void Reset()
    {
        Phase = RoundPhase.WaitingToStart;
        RoundStartTime = 0f;
        RoundEndTime = 0f;
    }

    /// <summary>True once the round is live (QC round_handler_IsRoundStarted).</summary>
    public bool IsRoundStarted => Phase == RoundPhase.InProgress;

    /// <summary>True while the countdown is running (QC round_handler_CountdownRunning).</summary>
    public bool CountdownRunning => Phase == RoundPhase.Countdown;

    /// <summary>True while awaiting the next round (QC round_handler_AwaitingNextRound).</summary>
    public bool AwaitingNextRound => Phase == RoundPhase.WaitingToStart;

    /// <summary>
    /// Advance the handler one frame (QC round_handler_Think). Returns true on the frame the round actually
    /// ends (so the caller can resolve scoring once), mirroring the QC canRoundEnd → schedule-next-round edge.
    /// </summary>
    public bool Tick()
    {
        float now = _now();
        switch (Phase)
        {
            case RoundPhase.WaitingToStart:
                // QC: leaving the wait state arms the countdown (cnt = count + 1) and sets round_starttime.
                Phase = RoundPhase.Countdown;
                RoundStartTime = now + CountdownLength;
                return false;

            case RoundPhase.Countdown:
                // QC: the countdown only ticks while canRoundStart holds; otherwise it stalls (reset).
                if (!CanRoundStart())
                {
                    RoundStartTime = now + CountdownLength; // hold the countdown until players arrive
                    return false;
                }
                if (now >= RoundStartTime)
                {
                    Phase = RoundPhase.InProgress;
                    RoundEndTime = RoundTimeLimit > 0f ? now + RoundTimeLimit : 0f;
                    RoundsPlayed++;
                    OnRoundStart();
                }
                return false;

            case RoundPhase.InProgress:
                // QC: once canRoundEnd holds, schedule the next round after the end-delay.
                if (CanRoundEnd())
                {
                    Phase = RoundPhase.WaitingToStart;
                    RoundStartTime = now + EndDelay;
                    return true; // the round just ended this frame
                }
                return false;
        }
        return false;
    }
}
