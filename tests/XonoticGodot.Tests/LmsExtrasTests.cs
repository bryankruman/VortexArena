using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.9: LMS extra-life pickups (grant g_lms_extra_lives lives) and the leader computation
/// (lms_UpdateLeaders — max-lives players become leaders only with a big enough lead and a small enough share).
/// </summary>
[Collection("GlobalState")]
public class LmsExtrasTests
{
    private EngineServices _f = null!;
    private Player NewPlayer() => new Player { Flags = EntFlags.Client };

    public LmsExtrasTests()
    {
        _f = new EngineServices(new CollisionWorld());
        Api.Services = _f;
        // QC: LMS lives are derived from fraglimit (mapinfo lives= / g_lms_lives_override), default 5 when unset.
        _f.Cvars.Set("fraglimit", "5");
        _f.Cvars.Set("g_lms_extra_lives", "3");
    }

    [Fact]
    public void ExtraLife_GrantsConfiguredLives()
    {
        var lms = new LastManStanding();
        var p = NewPlayer();
        lms.AddPlayer(p);
        Assert.Equal(5, lms.LivesOf(p));

        int added = lms.GiveExtraLife(p);
        Assert.Equal(3, added);
        Assert.Equal(8, lms.LivesOf(p));
    }

    [Fact]
    public void ExtraLife_NoGrantOnceEliminated()
    {
        var lms = new LastManStanding();
        var p = NewPlayer();
        lms.GetState(p).Lives = 0; // eliminated
        Assert.Equal(0, lms.GiveExtraLife(p));
        Assert.Equal(0, lms.LivesOf(p));
    }

    [Fact]
    public void Leaders_RequireBigLeadAndSmallShare()
    {
        _f.Cvars.Set("g_lms_leader_lives_diff", "2");
        _f.Cvars.Set("g_lms_leader_minpercent", "0.5");

        var lms = new LastManStanding();
        var leader = NewPlayer(); var mid = NewPlayer(); var low = NewPlayer();
        lms.GetState(leader).Lives = 9;
        lms.GetState(mid).Lives = 5;
        lms.GetState(low).Lives = 4;

        lms.UpdateLeaders();
        // leader has +4 over second-best (5), is 1/3 of the field (<= 0.5) → a leader
        Assert.Contains(leader, lms.Leaders);
        Assert.DoesNotContain(mid, lms.Leaders);
        Assert.Equal(4, lms.LeadersLivesDiff);
    }

    [Fact]
    public void Leaders_NoneWhenPackIsTight()
    {
        _f.Cvars.Set("g_lms_leader_lives_diff", "2");
        _f.Cvars.Set("g_lms_leader_minpercent", "0.5");

        var lms = new LastManStanding();
        var a = NewPlayer(); var b = NewPlayer();
        lms.GetState(a).Lives = 5;
        lms.GetState(b).Lives = 4; // only +1 lead → below the diff threshold

        lms.UpdateLeaders();
        Assert.Empty(lms.Leaders);
    }
}
