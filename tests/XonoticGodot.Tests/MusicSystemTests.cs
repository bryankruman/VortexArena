using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Exercises the music system map objects (target_music / trigger_music spawnfuncs) and the
/// cdtrack worldspawn extraction in GameWorld.
/// </summary>
public class MusicSystemTests
{
    /// <summary>target_music and trigger_music are registered in SpawnFuncs after RegisterAll.</summary>
    [Fact]
    public void MusicSpawnfuncs_Are_Registered()
    {
        // Boot a minimal world so RegisterAll runs.
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        // Verify both classnames are registered.
        var e1 = Api.Entities.Spawn();
        e1.ClassName = "target_music";
        Assert.True(SpawnFuncs.TrySpawn("target_music", e1));
        Assert.Equal("target_music", e1.ClassName);

        var e2 = Api.Entities.Spawn();
        e2.ClassName = "trigger_music";
        Assert.True(SpawnFuncs.TrySpawn("trigger_music", e2));
        Assert.Equal("trigger_music", e2.ClassName);
    }

    /// <summary>target_music without a targetname is the map default — always active.</summary>
    [Fact]
    public void TargetMusic_NoTargetname_IsDefault_AlwaysActive()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/neon";
        e.Volume = 0.8f;
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal("target_music", e.ClassName);
        Assert.Equal(MapMover.ActiveActive, e.Active);
        // No .Use delegate for an untargetable default.
        Assert.Null(e.Use);
    }

    /// <summary>target_music with a targetname starts inactive and toggles on Use.</summary>
    [Fact]
    public void TargetMusic_WithTargetname_TogglesOnUse()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.TargetName = "music_override";
        e.Noise = "cdtracks/heavymetal";
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal(MapMover.ActiveNot, e.Active);
        Assert.NotNull(e.Use);

        // Trigger it: should activate.
        Entity activator = Api.Entities.Spawn();
        e.Use!(e, activator);
        Assert.Equal(MapMover.ActiveActive, e.Active);

        // Trigger again: should deactivate.
        e.Use!(e, activator);
        Assert.Equal(MapMover.ActiveNot, e.Active);
    }

    /// <summary>trigger_music sets up as a trigger volume with a Touch handler.</summary>
    [Fact]
    public void TriggerMusic_SetsUpAsTriggerVolume()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/sensation3";
        e.Volume = 0.9f;
        TargetMusic.TriggerMusicSetup(e);

        Assert.Equal("trigger_music", e.ClassName);
        Assert.Equal(MapMover.ActiveActive, e.Active);
        Assert.NotNull(e.Touch);
        Assert.Equal(Solid.Trigger, e.Solid);
    }

    /// <summary>trigger_music with START_DISABLED flag starts inactive.</summary>
    [Fact]
    public void TriggerMusic_StartDisabled_InactiveBySpaawnflag()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/neon";
        e.SpawnFlags = 1; // START_DISABLED
        TargetMusic.TriggerMusicSetup(e);

        Assert.Equal(MapMover.ActiveNot, e.Active);
    }

    /// <summary>GameWorld.CdTrack is populated from worldspawn "music" key.</summary>
    [Fact]
    public void GameWorld_CdTrack_FromWorldspawn_Music()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["music"] = "cdtracks/heavymetal.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities);
        gw.Boot("dm");

        Assert.Equal("cdtracks/heavymetal.ogg", gw.CdTrack);
    }

    /// <summary>GameWorld.CdTrack falls back to worldspawn "noise" if "music" is absent.</summary>
    [Fact]
    public void GameWorld_CdTrack_FromWorldspawn_Noise()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["noise"] = "cdtracks/quiet.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities);
        gw.Boot("dm");

        Assert.Equal("cdtracks/quiet.ogg", gw.CdTrack);
    }

    /// <summary>Pre-set CdTrack is NOT overridden by worldspawn (mapinfo takes priority).</summary>
    [Fact]
    public void GameWorld_CdTrack_PreSet_NotOverriddenByWorldspawn()
    {
        var entities = new List<EntityDict>
        {
            new("worldspawn") { Fields = { ["music"] = "cdtracks/heavymetal.ogg" } }
        };
        var world = new CollisionWorld();
        var gw = new GameWorld(world, entities) { CdTrack = "cdtracks/neon" };
        gw.Boot("dm");

        // The pre-set value (from mapinfo) should win.
        Assert.Equal("cdtracks/neon", gw.CdTrack);
    }

    /// <summary>target_music volume defaults to 1 when unset (0).</summary>
    [Fact]
    public void TargetMusic_Volume_DefaultsToOne()
    {
        var world = new CollisionWorld();
        var gw = new GameWorld(world, null);
        gw.Boot("dm");

        Entity e = Api.Entities.Spawn();
        e.Noise = "cdtracks/test";
        // Volume is 0 (unset by the map).
        TargetMusic.TargetMusicSetup(e);

        Assert.Equal(1f, e.Volume);
    }
}
