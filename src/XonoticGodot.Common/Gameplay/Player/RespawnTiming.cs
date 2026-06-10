using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Port of <c>calculate_player_respawn_time</c> (server/client.qc:1399-1485): compute a dead player's
/// respawn delay, scaling between <c>g_respawn_delay_small</c> and <c>g_respawn_delay_large</c> by the live
/// player count, quantizing to <c>g_respawn_waves</c>, deriving the forced-respawn ceiling
/// (<c>respawn_time_max</c> = <c>g_respawn_delay_max</c>), the announcer countdown start, and the
/// <see cref="RespawnFlag.Force"/> flag from <c>g_forced_respawn</c>. Replaces the old flat 2 s schedule
/// (which was only coincidentally right at stock defaults).
/// </summary>
public static class RespawnTiming
{
    /// <summary>
    /// Fill <paramref name="p"/>'s respawn timers (<see cref="Player.RespawnTime"/>/<see cref="Player.RespawnTimeMax"/>/
    /// <see cref="Player.RespawnCountdown"/>/<see cref="Player.RespawnFlags"/>) for a player who just died.
    /// <paramref name="roster"/> is the connected-player list (for the player-count scaling);
    /// <paramref name="teamplay"/> selects same-team vs all-others counting.
    /// </summary>
    public static void Calculate(Player p, IReadOnlyList<Player> roster, bool teamplay)
    {
        float now = Api.Services is not null ? Api.Clock.Time : 0f;

        float sdelayMax    = Cvar("g_respawn_delay_max", 5f);
        float sdelaySmall  = Cvar("g_respawn_delay_small", 2f);
        float sdelayLarge  = Cvar("g_respawn_delay_large", 2f);
        float smallCount   = Cvar("g_respawn_delay_small_count", 0f);
        float largeCount   = Cvar("g_respawn_delay_large_count", 8f);
        float waves        = Cvar("g_respawn_waves", 0f);

        // QC: pcount = 1 (include myself). Then count the OTHER in-game players (same team in teamplay).
        int pcount = 1;
        for (int i = 0; i < roster.Count; i++)
        {
            Player it = roster[i];
            if (ReferenceEquals(it, p) || it.IsObserver || it.FragsStatus == Player.FragsSpectator)
                continue; // QC IS_PLAYER(it) && it != this
            if (!teamplay || it.Team == p.Team)
                pcount++;
        }

        // QC: an unset (<=0) small/large count means "the minimum to have gameplay" (2 in FFA, 1 in teamplay).
        if (smallCount <= 0f) smallCount = teamplay ? 1f : 2f;
        if (largeCount <= 0f) largeCount = teamplay ? 1f : 2f;

        float sdelay;
        if (pcount <= smallCount)
            sdelay = sdelaySmall;
        else if (pcount >= largeCount)
            sdelay = sdelayLarge;
        else // smallCount < pcount < largeCount  (implies largeCount > smallCount)
            sdelay = sdelaySmall + (sdelayLarge - sdelaySmall) * (pcount - smallCount) / (largeCount - smallCount);

        if (waves > 0f)
            p.RespawnTime = MathF.Ceiling((now + sdelay) / waves) * waves;
        else
            p.RespawnTime = now + sdelay;

        p.RespawnTimeMax = sdelay < sdelayMax ? now + sdelayMax : p.RespawnTime;

        // QC: arm the 10-9-8… announcer only for a long-enough wait.
        p.RespawnCountdown = (sdelay + waves >= 5f && p.RespawnTime - now > 1.75f) ? 10 : -1;

        // QC: forced respawn auto-respawns at respawn_time_max even without a fire press. Bots have no input
        // stream in this port (they never "press fire"), so they must be force-respawned too or they'd lie dead
        // forever under the button-gated machine.
        RespawnFlag flags = RespawnFlag.None;
        if (Cvar("g_forced_respawn", 0f) != 0f || p.IsBot)
            flags |= RespawnFlag.Force;
        p.RespawnFlags = flags;

        // A bot has no input stream to "press fire", so it relies on RESPAWN_FORCE — which respawns at
        // respawn_time_max (the 5 s ceiling). Collapse the bot's ceiling to the base delay so bots respawn on the
        // normal schedule (matching the old timer-driven path) instead of always waiting the full forced ceiling.
        if (p.IsBot)
            p.RespawnTimeMax = p.RespawnTime;
    }

    private static float Cvar(string name, float fallback)
    {
        if (Api.Services is null) return fallback;
        string s = Api.Cvars.GetString(name);
        return string.IsNullOrEmpty(s) ? fallback : Api.Cvars.GetFloat(name);
    }
}
