using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The server-side lag-compensation hook (port of the <c>antilag_takeback</c> … <c>antilag_restore</c> bracket
/// around a hitscan trace in <c>server/antilag.qc</c>). Hitscan weapon code calls <see cref="Begin"/> before its
/// authoritative trace and <see cref="End"/> after, so other players are momentarily rewound to where the
/// shooter saw them at fire time — making hit registration fair under latency.
///
/// Gameplay lives in <c>XonoticGodot.Common</c> and must not know about the netcode, so this is an ambient hook (the
/// same pattern as <c>EffectEmitter.Sink</c>): the server installs a <see cref="ILagCompensation"/> provider; on
/// a client, in a test, or a bot-only server it stays null and the bracket is a no-op (no rewind — correct,
/// since there is no remote-view latency to compensate for).
/// </summary>
public static class LagComp
{
    /// <summary>The active provider (installed by the server's net driver); null = no compensation (no-op).</summary>
    public static ILagCompensation? Provider;

    /// <summary>Rewind other players to <paramref name="shooter"/>'s view-time. Pair with <see cref="End"/>.</summary>
    public static void Begin(Entity shooter) => Provider?.Begin(shooter);

    /// <summary>Restore the rewound players to their authoritative positions.</summary>
    public static void End() => Provider?.End();
}

/// <summary>Server-net-backed lag compensation (rewinds/restores other players around a shooter's trace).</summary>
public interface ILagCompensation
{
    void Begin(Entity shooter);
    void End();
}
