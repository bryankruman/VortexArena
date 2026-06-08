// Port of common/mutators/mutator/kick_teamkiller/sv_kick_teamkiller.qc

using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The Kick Teamkiller mutator — port of common/mutators/mutator/kick_teamkiller/sv_kick_teamkiller.qc. In
/// teamplay, a real client whose teamkill count exceeds <c>g_kick_teamkiller_rate</c> teamkills/minute (and is
/// at least <c>g_kick_teamkiller_lower_limit</c>) is, per <c>g_kick_teamkiller_severity</c>, kicked (1),
/// banned (2), or play-banned/observed (default). Enabled when <c>g_kick_teamkiller_rate &gt; 0</c>.
///
/// PORTED: the PlayerDies detection — the teamplay + (non-warmup) + real-client gate, the
/// <c>teamkills &gt;= lower_limit &amp;&amp; teamkills &gt;= rate * playtime / 60</c> threshold (reading the
/// attacker's SP_TEAMKILLS score via GameScores) — and an INFO notification when the threshold trips.
///
/// SUBSTRATE BLOCKER (documented P3 partial — recon §7): the punitive ACTIONS depend on server-admin plumbing
/// the headless sim lacks — <c>dropclient_schedule</c> (severity 1 kick), <c>Ban_KickBanClient</c> (severity 2
/// ban), and the play-ban path (PutObserverInServer + g_playban_list + TRANSMUTE(Observer)) for the default
/// case. None of dropclient / IP-ban / observer-transmute exists here, and there is no per-client
/// <c>startplaytime</c> (so playtime is measured from the sim clock as a stand-in). The detection is faithful
/// and the action is recorded via <see cref="LastAction"/> + a notification; wiring the real kick/ban is a
/// server-admin follow-up (the same gap kick infra leaves across the port).
/// </summary>
[Mutator]
public sealed class KickTeamkillerMutator : MutatorBase
{
    public float Rate;        // g_kick_teamkiller_rate (teamkills/minute)
    public float LowerLimit;  // g_kick_teamkiller_lower_limit
    public int   Severity;    // g_kick_teamkiller_severity (1 kick, 2 ban, else play-ban)

    public KickTeamkillerMutator() => NetName = "kick_teamkiller";

    // QC: REGISTER_MUTATOR(kick_teamkiller, (autocvar_g_kick_teamkiller_rate > 0));
    public override bool IsEnabled =>
        Api.Services is not null && Api.Cvars.GetFloat("g_kick_teamkiller_rate") > 0f;

    /// <summary>The last (attacker, severity) the threshold tripped for — the server-admin action the headless
    /// sim can't actually perform (kick/ban/playban). Lets a host/test observe the decision.</summary>
    public (Entity attacker, int severity)? LastAction { get; private set; }

    private HookHandler<MutatorHooks.PlayerDiesArgs>? _onPlayerDies;

    public override void Hook()
    {
        _onPlayerDies ??= OnPlayerDies;
        MutatorHooks.PlayerDies.Add(_onPlayerDies);

        if (Api.Services is not null)
        {
            Rate = Api.Cvars.GetFloat("g_kick_teamkiller_rate");
            LowerLimit = Api.Cvars.GetFloat("g_kick_teamkiller_lower_limit");
            Severity = (int)Api.Cvars.GetFloat("g_kick_teamkiller_severity");
        }
    }

    public override void Unhook()
    {
        if (_onPlayerDies is not null) MutatorHooks.PlayerDies.Remove(_onPlayerDies);
    }

    // MUTATOR_HOOKFUNCTION(kick_teamkiller, PlayerDies)
    private bool OnPlayerDies(ref MutatorHooks.PlayerDiesArgs args)
    {
        if (Api.Services is null) return false;
        // QC: if (!teamplay) return; if (warmup_stage) return;
        if (!GameScores.Teamplay) return false;
        // The headless sim has no warmup_stage gate here; treat the match as live.

        Entity? attacker = args.Attacker;
        // QC: if (!IS_REAL_CLIENT(attacker)) return;  (real client == a player entity in this port)
        if (attacker is null || (attacker.Flags & EntFlags.Client) == 0) return false;
        if (attacker is Player { IsBot: true }) return false;

        int teamkills = GameScores.Get(attacker, GameScores.TeamKills);
        // QC playtime = time - CS(attacker).startplaytime; the port has no per-client startplaytime, so the
        // sim clock stands in (playtime since match/level start) — documented substrate gap.
        float playtime = Api.Clock.Time;

        // rate is teamkills/minutes, playtime in seconds.
        if (teamkills >= LowerLimit && teamkills >= Rate * playtime / 60f)
        {
            // QC switch(severity): 1 = dropclient (kick), 2 = Ban_KickBanClient, default = play-ban/observe.
            // None of those actions has a headless equivalent; record the decision + notify, gate the action.
            LastAction = (attacker, Severity);
            NotificationSystem.Send(NotifBroadcast.All, null, MsgType.Info, "QUIT_KICK_TEAMKILL",
                attacker.NetName);
        }
        return false;
    }
}
