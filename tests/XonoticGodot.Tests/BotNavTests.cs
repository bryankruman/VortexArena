using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server.Bot;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.14: waypoint auto-generation from map entities (items / spawns / teleporters → a navigable
/// graph) and the per-gametype bot objective-role dispatch (now incl. Nexball + Assault).
/// </summary>
[Collection("GlobalState")]
public class BotNavTests
{
    public BotNavTests() => Api.Services = new EngineServices(new CollisionWorld());

    [Fact]
    public void AutoGenerate_MakesWaypointsForItemsSpawnsAndTeleporters()
    {
        var es = (EngineServices)Api.Services!;

        // an item, a spawn point, and a teleporter trigger → destination.
        Entity item = es.EntityTable.Spawn(); item.ClassName = "item_health_medium"; item.Flags = EntFlags.Item; item.Origin = new Vector3(100, 0, 0);
        Entity spawn = es.EntityTable.Spawn(); spawn.ClassName = "info_player_deathmatch"; spawn.Origin = new Vector3(0, 200, 0);
        Entity dst = es.EntityTable.Spawn(); dst.ClassName = "info_teleport_destination"; dst.TargetName = "tdest"; dst.Origin = new Vector3(500, 500, 0);
        Entity tele = es.EntityTable.Spawn(); tele.ClassName = "trigger_teleport"; tele.Target = "tdest"; tele.Origin = new Vector3(-100, 0, 0);
        tele.Mins = new Vector3(-32, -32, -32); tele.Maxs = new Vector3(32, 32, 32);

        var net = new WaypointNetwork();
        int n = net.GenerateFromEntities(es.EntityTable.All, autoLink: false);

        Assert.True(n >= 4); // item + spawn + teleporter box + teleport destination
        // the teleporter box waypoint carries the Teleport flag and a one-way link to its destination.
        Waypoint? teleWp = null;
        foreach (var w in net.Nodes) if (w.HasFlag(WaypointFlags.Teleport)) { teleWp = w; break; }
        Assert.NotNull(teleWp);
        Assert.NotEmpty(teleWp!.Links);
    }

    [Fact]
    public void LoadFromText_ParsesQuotedVtosVectors_IncludingLeadingSpaceComponents()
    {
        // The shipped .waypoints/.waypoints.cache files single-quote each vector (DP vtos), and vtos
        // right-aligns components so a small value carries a leading space inside the quotes
        // (' 46.8 ...'). Both forms must parse — the regression that broke this dropped the whole graph to
        // zero nodes and forced the expensive AutoLink tracewalk fallback.
        const string wp =
            "//WAYPOINT_VERSION 1.04\n" +
            "//WAYPOINT_SYMMETRY 0\n" +
            "'-507.0 1112.6 409.0'\n'-507.0 1112.6 409.0'\n0\n" +     // normal negative component
            "' 46.8 -380.6 536.0'\n' 46.8 -380.6 536.0'\n0\n" +       // leading-space (right-aligned) component
            "'1298.4 -59.8 217.0'\n'1298.4 -59.8 217.0'\n16384\n";    // a jump waypoint (flag 1<<14)

        var net = WaypointNetwork.LoadFromText(wp);
        Assert.Equal(3, net.Count);
        Assert.Equal(new Vector3(46.8f, -380.6f, 536.0f), net.Nodes[1].Origin);
        Assert.True(net.Nodes[2].HasFlag(WaypointFlags.Jump));

        // the .cache link form is 'from'*'to' with the same quoting; loading it must add the edge.
        net.LoadLinks("//WAYPOINT_VERSION 1.04\n'-507.0 1112.6 409.0'*' 46.8 -380.6 536.0'\n");
        Assert.Single(net.Nodes[0].Links);
        Assert.Same(net.Nodes[1], net.Nodes[0].Links[0].To);
    }

    [Fact]
    public void RoleDispatch_CoversNexballAndAssault()
    {
        Assert.Equal("RoleNexball", BotRoles.ChooseRole("nexball").Method.Name);
        Assert.Equal("RoleNexball", BotRoles.ChooseRole("nb").Method.Name);
        Assert.Equal("RoleAssault", BotRoles.ChooseRole("assault").Method.Name);
        Assert.Equal("RoleAssault", BotRoles.ChooseRole("as").Method.Name);
        // existing modes still dispatch
        Assert.Equal("RoleCtf", BotRoles.ChooseRole("ctf").Method.Name);
    }
}
