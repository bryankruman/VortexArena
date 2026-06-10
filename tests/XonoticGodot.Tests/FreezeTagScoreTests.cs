using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Scoring;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Pins the Freeze Tag freeze-score matrix (QC <c>freezetag_Add_Score</c>, sv_freezetag.qc:162-181). The three
/// QC branches are:
///  - <c>attacker == targ</c>  → victim SCORE −1 (froze your own dumb self, counted as suicide already);
///  - <c>IS_PLAYER(attacker)</c> → teammate: attacker −1 + victim −1; enemy: attacker +1 + victim −1;
///  - else (NULL / non-player) → NOTHING ("got frozen by the gametype rules themselves").
/// The regression this guards: folding the NULL case into the self branch wrongly cost the victim 1 point on a
/// rules-driven freeze.
/// </summary>
[Collection("GlobalState")]
public class FreezeTagScoreTests
{
    public FreezeTagScoreTests()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameScores.RegisterAll(); // ScoreFrags routes through GameScores.Score; needs the registry
    }

    private static Player P(int team) => new Player { Team = team, Flags = EntFlags.Client };

    [Fact]
    public void NullAttacker_RulesFreeze_AwardsNoScore()
    {
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker: null); // QC else branch: gametype-rules freeze, no attacker

        // QC "// else nothing - got frozen by the gametype rules themselves": victim keeps its score.
        Assert.Equal(0, victim.ScoreFrags);
        Assert.True(ft.IsFrozen(victim)); // still frozen — only the SCORE side differs from a player freeze
    }

    [Fact]
    public void SelfFreeze_CostsVictimOnePoint()
    {
        var ft = new FreezeTag();
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker: victim); // QC attacker == targ

        Assert.Equal(-1, victim.ScoreFrags);
    }

    [Fact]
    public void EnemyFreeze_AttackerPlusOne_VictimMinusOne()
    {
        var ft = new FreezeTag();
        Player attacker = P(Teams.Blue);
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker); // QC IS_PLAYER && !SAME_TEAM

        Assert.Equal(+1, attacker.ScoreFrags);
        Assert.Equal(-1, victim.ScoreFrags);
    }

    [Fact]
    public void TeammateFreeze_BothMinusOne()
    {
        var ft = new FreezeTag();
        Player attacker = P(Teams.Red);
        Player victim = P(Teams.Red);

        ft.Freeze(victim, attacker); // QC IS_PLAYER && SAME_TEAM

        Assert.Equal(-1, attacker.ScoreFrags);
        Assert.Equal(-1, victim.ScoreFrags);
    }
}
