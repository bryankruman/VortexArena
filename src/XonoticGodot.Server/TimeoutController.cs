using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Server;

/// <summary>
/// The timeout / timein pause system — the Godot-free essence of the timeout slice of
/// server/command/common.qc (<c>CommonCommand_timeout</c> / <c>timein</c> / <c>timeout_handler_think</c>). A
/// player may pause the match (<c>sv_timeout</c>): after a short lead-time countdown the game freezes for up to
/// <c>sv_timeout_length</c> seconds; the caller can resume early with <c>timein</c>. Each player has a limited
/// number of timeouts (<c>sv_timeout_number</c>).
///
/// Faithful to QC: the LEADTIME→ACTIVE→resume state machine, the per-player allowance, the guard chain (no
/// timeout during a vote, before the match starts, or too late in the match), and the early-resume shortening
/// to <c>sv_timeout_resumetime</c>. The engine "slowmo freeze" is modeled as <see cref="IsPaused"/>, which the
/// world honors by freezing movement/scoring exactly like intermission. Center-print countdown text is
/// presentation; this core carries the broadcast events.
/// </summary>
public sealed class TimeoutController
{
    public const int Inactive = 0, LeadTime = 1, ActivePause = 2;

    private readonly Dictionary<Player, int> _allowed = new();

    /// <summary>QC <c>timeout_status</c>: INACTIVE / LEADTIME / ACTIVE.</summary>
    public int Status { get; private set; }

    /// <summary>The player who called the timeout (QC <c>timeout_caller</c>), or null for a console call.</summary>
    public Player? Caller { get; private set; }

    /// <summary>QC <c>timeout_time</c>: seconds remaining in the active pause.</summary>
    public float Time { get; private set; }

    /// <summary>QC <c>timeout_leadtime</c>: seconds remaining before the pause begins.</summary>
    public float LeadTimeRemaining { get; private set; }

    /// <summary>True while the match is frozen (the pause is active) — the world treats this like game_stopped.</summary>
    public bool IsPaused => Status == ActivePause;

    /// <summary>True whenever a timeout is pending or active (QC <c>timeout_status</c> != INACTIVE).</summary>
    public bool Active => Status != Inactive;

    /// <summary>Broadcast sink (QC bprint). Host-wires to the chat/console broadcast.</summary>
    public Action<string>? Broadcast { get; set; }

    // ---- host-wired predicates ----
    public Func<bool> VoteActive { get; set; } = static () => false;
    public Func<bool> Warmup { get; set; } = static () => false;
    public Func<float> GameStartTime { get; set; } = static () => 0f;
    public Func<Player, bool> IsPlayerOf { get; set; } = static _ => true;

    /// <summary>The time source (QC <c>time</c>); defaults to the ambient sim clock. Overridable for tests.</summary>
    public Func<float> Clock { get; set; } = static () => Api.Services is not null ? Api.Clock.Time : 0f;

    private float _lastTick;

    private float Now => Clock();

    /// <summary>QC <c>this.allowed_timeouts</c>: remaining timeouts for a player (default <c>sv_timeout_number</c>).</summary>
    public int AllowedOf(Player p)
        => _allowed.TryGetValue(p, out int n) ? n : Cvars.Int("sv_timeout_number");

    /// <summary>Reset a player's allowance (QC on connect/spawn).</summary>
    public void ResetAllowance(Player p) => _allowed[p] = Cvars.Int("sv_timeout_number");

    public void Remove(Player p) => _allowed.Remove(p);

    /// <summary>
    /// QC <c>CommonCommand_timeout</c>: try to start a timeout. Returns true on success; on failure writes the
    /// reason to <paramref name="error"/>. <paramref name="caller"/> null = server console (bypasses the
    /// per-player allowance + the sv_timeout gate).
    /// </summary>
    public bool CallTimeout(Player? caller, out string error)
    {
        error = "";
        if (caller is not null && !Cvars.Bool("sv_timeout")) { error = "^1Timeouts are not allowed."; return false; }
        if (Active) { error = "^1A timeout is already active."; return false; }
        if (VoteActive()) { error = "^1You can not call a timeout while a vote is active."; return false; }
        if (Warmup() && !Cvars.Bool("g_warmup_allow_timeout")) { error = "^1Timeouts are not allowed during warmup."; return false; }
        if (Now < GameStartTime()) { error = "^1You can not call a timeout while the map is being restarted."; return false; }
        if (caller is not null && AllowedOf(caller) <= 0) { error = "^1You already used all your allowed timeouts."; return false; }
        if (caller is not null && !IsPlayerOf(caller)) { error = "^1Only players can call a timeout."; return false; }

        // QC "too late" guard: can't timeout within leadtime+1s of the time limit.
        float timelimit = Cvars.Float("timelimit");
        if (timelimit > 0f && (timelimit * 60f - Cvars.Float("sv_timeout_leadtime") - 1f) < Now - GameStartTime())
        { error = "^1It is too late to call a timeout now."; return false; }

        if (caller is not null) _allowed[caller] = AllowedOf(caller) - 1;
        Status = LeadTime;
        Caller = caller;
        Time = Cvars.FloatOr("sv_timeout_length", 120f);
        LeadTimeRemaining = Cvars.FloatOr("sv_timeout_leadtime", 4f);
        _lastTick = Now;
        Broadcast?.Invoke($"^2* {(caller?.NetName ?? "server")} called a timeout"
            + (caller is not null ? $" ({AllowedOf(caller)} left)" : ""));
        return true;
    }

    /// <summary>
    /// QC <c>CommonCommand_timein</c>: abort the lead-time, or shorten an active pause to
    /// <c>sv_timeout_resumetime</c>. Only the original caller (or console) may resume. Returns true on success.
    /// </summary>
    public bool CallTimein(Player? caller, out string error)
    {
        error = "";
        if (!Active) { error = "^1No active timeout."; return false; }
        if (caller is not null && !ReferenceEquals(caller, Caller)) { error = "^1You are not allowed to resume the timeout."; return false; }

        if (Status == LeadTime)
        {
            Reset();
            Broadcast?.Invoke("^2* the timeout was aborted");
        }
        else // ACTIVE
        {
            Time = Cvars.FloatOr("sv_timeout_resumetime", 3f);
            Broadcast?.Invoke("^2* the game will resume");
        }
        return true;
    }

    /// <summary>
    /// QC <c>timeout_handler_think</c>: advance the timeout one frame. LEADTIME counts down then freezes
    /// (Status→ACTIVE); ACTIVE counts down then resumes (Status→INACTIVE). Real wall-clock seconds, so the
    /// pause length is honored even though <see cref="IsPaused"/> freezes the sim.
    /// </summary>
    public void Think()
    {
        if (!Active) return;
        float now = Now;
        float dt = System.Math.Max(0f, now - _lastTick);
        _lastTick = now;

        if (Status == LeadTime)
        {
            LeadTimeRemaining -= dt;
            if (LeadTimeRemaining <= 0f)
            {
                Status = ActivePause;
                LeadTimeRemaining = 0f;
                Broadcast?.Invoke("^2* the game is paused");
            }
        }
        else if (Status == ActivePause)
        {
            Time -= dt;
            if (Time <= 0f)
            {
                Broadcast?.Invoke("^2* the game has resumed");
                Reset();
            }
        }
    }

    private void Reset()
    {
        Status = Inactive;
        Caller = null;
        Time = 0f;
        LeadTimeRemaining = 0f;
    }
}
