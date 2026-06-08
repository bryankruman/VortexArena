using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// Team identity (QC NUM_TEAM_* color codes, common/teams.qh). Entity.Team holds the team value.
/// Minimal shared contract so the team gametypes (CTF/TDM/CA/...) and mutators agree.
/// </summary>
public static class Teams
{
    public const int None = 0;
    public const int Red = 4;     // NUM_TEAM_1
    public const int Blue = 13;   // NUM_TEAM_2
    public const int Yellow = 12; // NUM_TEAM_3
    public const int Pink = 9;    // NUM_TEAM_4

    public static readonly int[] All = { Red, Blue, Yellow, Pink };

    /// <summary>Human label for a team color code.</summary>
    public static string Name(int team) => team switch
    {
        Red => "Red",
        Blue => "Blue",
        Yellow => "Yellow",
        Pink => "Pink",
        _ => "Neutral",
    };

    /// <summary>The first <paramref name="count"/> teams (2..4) used by a gametype.</summary>
    public static IEnumerable<int> Active(int count)
    {
        for (int i = 0; i < count && i < All.Length; i++) yield return All[i];
    }

    public static bool SameTeam(Entity a, Entity b) => a.Team != 0 && a.Team == b.Team;
}
