using XonoticGodot.Common.Physics;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Net;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for <see cref="PhysicsPreset"/> — the port of <c>Physics_Valid</c>/<c>Physics_ClientOption</c>
/// (qcsrc/common/physics/player.qc:18-42) and the vector-level per-peer resolution
/// (<see cref="MoveVarsBlock.CaptureResolved"/>) used by the v7 per-client physics block (T54).
/// </summary>
[Collection("GlobalState")]
public class PhysicsPresetTests
{
    public PhysicsPresetTests()
    {
        // statics other tests (or a leaked net session) may have left behind
        MovementParameters.PredictionOverride = null;
        MovementParameters.PresetProvider = null;
    }

    /// <summary>A store with the clientselect knobs + the real cpma/xdf preset rows from physics.cfg.</summary>
    private static CvarService NewStore(string clientSelect = "1")
    {
        var c = new CvarService();
        c.Set("g_physics_clientselect", clientSelect);
        c.Set("g_physics_clientselect_options", "xonotic nexuiz vecxis quake quake2 quake3 cpma bones xdf");
        // cpma rows (physics.cfg:256-291) — the spot-check targets
        c.Set("g_physics_cpma_aircontrol", "150");
        c.Set("g_physics_cpma_aircontrol_flags", "5");
        c.Set("g_physics_cpma_accelerate", "15");
        c.Set("g_physics_cpma_maxspeed", "320");
        c.Set("g_physics_cpma_gravity", "750");
        c.Set("g_physics_cpma_jumpspeedcap_min", "0");      // REAL zero — must NOT become NaN
        c.Set("g_physics_cpma_jumpspeedcap_max", "nan");    // the literal "nan" → NaN (cap disabled)
        c.Set("g_physics_cpma_track_canjump", "1");
        // xdf rows (physics.cfg:207-208)
        c.Set("g_physics_xdf_jumpspeedcap_min", "0");
        c.Set("g_physics_xdf_jumpspeedcap_max", "0.5");
        // the sv_* globals presets fall back to
        c.Set("sv_maxspeed", "360");
        c.Set("sv_gravity", "800");
        c.Set("sv_aircontrol", "100");
        return c;
    }

    [Fact]
    public void ClientSelectOff_ReturnsTheFallback_ForEveryOption()
    {
        CvarService c = NewStore(clientSelect: "0");
        foreach ((string option, string fallbackCvar) in PhysicsPreset.Options)
        {
            float fallback = c.GetFloat(fallbackCvar);
            Assert.Equal(fallback, PhysicsPreset.Resolve(c, "cpma", option, fallback, c.Has));
        }
    }

    [Fact]
    public void ValidPreset_ResolvesThePresetCvar()
    {
        CvarService c = NewStore();
        Assert.Equal(150f, PhysicsPreset.Resolve(c, "cpma", "aircontrol", 100f, c.Has));
        Assert.Equal(5f, PhysicsPreset.Resolve(c, "cpma", "aircontrol_flags", 0f, c.Has));
        Assert.Equal(320f, PhysicsPreset.Resolve(c, "cpma", "maxspeed", 360f, c.Has));
        Assert.Equal(750f, PhysicsPreset.Resolve(c, "cpma", "gravity", 800f, c.Has));
    }

    [Fact]
    public void JumpSpeedCap_NanString_IsNaN_And_RealZero_StaysZero()
    {
        CvarService c = NewStore();
        // cpma: min is a REAL 0 (a genuine 0×jumpheight floor), max is the "nan" disable sentinel.
        Assert.Equal(0f, PhysicsPreset.Resolve(c, "cpma", "jumpspeedcap_min", float.NaN, c.Has));
        Assert.True(float.IsNaN(PhysicsPreset.Resolve(c, "cpma", "jumpspeedcap_max", float.NaN, c.Has)));
        // xdf: both REAL numbers.
        Assert.Equal(0f, PhysicsPreset.Resolve(c, "xdf", "jumpspeedcap_min", float.NaN, c.Has));
        Assert.Equal(0.5f, PhysicsPreset.Resolve(c, "xdf", "jumpspeedcap_max", float.NaN, c.Has));
    }

    [Fact]
    public void InvalidOrUnlistedSet_FallsThroughToTheDefaultChain()
    {
        CvarService c = NewStore();
        // "bogus" is not in g_physics_clientselect_options → chain 2 skipped, no clientselect_default → fallback.
        Assert.Equal(360f, PhysicsPreset.Resolve(c, "bogus", "maxspeed", 360f, c.Has));
        // ""/"default" are never valid (Physics_Valid, player.qc:18-21).
        Assert.False(PhysicsPreset.Valid(c, ""));
        Assert.False(PhysicsPreset.Valid(c, "default"));
        Assert.True(PhysicsPreset.Valid(c, "cpma"));
        Assert.False(PhysicsPreset.Valid(c, "cp")); // whole-word match (strhasword), not substring
    }

    [Fact]
    public void ClientselectDefault_AppliesToUnlistedSets_AndToClientsWithoutAPreset()
    {
        CvarService c = NewStore();
        // a "secret" set NOT in the options list, forced via g_physics_clientselect_default — QC deliberately
        // skips Physics_Valid for the default (player.qc:36).
        c.Set("g_physics_secret_maxspeed", "555");
        c.Set("g_physics_clientselect_default", "secret");
        Assert.Equal(555f, PhysicsPreset.Resolve(c, "", "maxspeed", 360f, c.Has));        // no client preset
        Assert.Equal(555f, PhysicsPreset.Resolve(c, "bogus", "maxspeed", 360f, c.Has));   // invalid client preset
        // a VALID client preset still wins over the default (chain order, player.qc:28-40).
        Assert.Equal(320f, PhysicsPreset.Resolve(c, "cpma", "maxspeed", 360f, c.Has));
    }

    [Fact]
    public void MissingPresetCvar_FallsThroughTheChain()
    {
        CvarService c = NewStore();
        // cpma defines no "friction" row in this store → EXISTS fails → fallback (the sv_* default).
        Assert.Equal(6f, PhysicsPreset.Resolve(c, "cpma", "friction", 6f, c.Has));
        // …and with a default set that DOES define it, the default chain catches it.
        c.Set("g_physics_secret_friction", "8");
        c.Set("g_physics_clientselect_default", "secret");
        Assert.Equal(8f, PhysicsPreset.Resolve(c, "cpma", "friction", 6f, c.Has));
    }

    [Fact]
    public void OptionFor_MapsExactlyThePresetResolvableMovevars()
    {
        // The two non-obvious option names (player.qc:141,148).
        Assert.Equal("stepdown", PhysicsPreset.OptionFor("sv_gameplayfix_stepdown"));
        Assert.Equal("doublejump", PhysicsPreset.OptionFor("sv_doublejump"));
        Assert.Equal("maxspeed", PhysicsPreset.OptionFor("sv_maxspeed"));
        // Globals (stats.qh entries WITH a cvar expression) are NOT preset-resolvable.
        Assert.Null(PhysicsPreset.OptionFor("sv_jumpstep"));
        Assert.Null(PhysicsPreset.OptionFor("sv_friction_on_land"));
        Assert.Null(PhysicsPreset.OptionFor("sv_wallfriction"));
        Assert.Null(PhysicsPreset.OptionFor("sv_gameplayfix_stepdown_maxspeed"));
        Assert.Null(PhysicsPreset.OptionFor("g_movement_highspeed"));
        Assert.Null(PhysicsPreset.OptionFor("sv_gameplayfix_nudgeoutofsolid"));
        // exactly the 36-option preset block (physics.cfg per-set rows)
        Assert.Equal(36, PhysicsPreset.Options.Length);
    }

    [Fact]
    public void CaptureResolved_ResolvesPresetEntries_AndCopiesGlobals()
    {
        CvarService c = NewStore();
        c.Set("sv_jumpstep", "1");
        float[] globals = MoveVarsBlock.Capture(c);
        float[] resolved = MoveVarsBlock.CaptureResolved(c, "cpma", globals, c.Has);
        Assert.Equal(globals.Length, resolved.Length);

        int IndexOf(string name) => System.Array.IndexOf(MoveVarsBlock.MovementCvars, name);
        Assert.Equal(320f, resolved[IndexOf("sv_maxspeed")]);                       // preset hit
        Assert.Equal(750f, resolved[IndexOf("sv_gravity")]);                        // preset hit
        Assert.Equal(150f, resolved[IndexOf("sv_aircontrol")]);                     // preset hit
        Assert.Equal(0f, resolved[IndexOf("sv_jumpspeedcap_min")]);                 // REAL zero preserved
        Assert.True(float.IsNaN(resolved[IndexOf("sv_jumpspeedcap_max")]));         // "nan" → NaN
        Assert.Equal(1f, resolved[IndexOf("sv_track_canjump")]);                    // preset hit
        Assert.Equal(globals[IndexOf("sv_jumpstep")], resolved[IndexOf("sv_jumpstep")]);             // global copied
        Assert.Equal(globals[IndexOf("g_movement_highspeed")], resolved[IndexOf("g_movement_highspeed")]); // global copied
        Assert.Equal(6f, resolved[IndexOf("sv_friction")]);                         // missing preset row → sv_ fallback

        // FromValues on the resolved vector carries the preset (incl. the real-0 cap) into the parameter struct.
        MovementParameters mp = MovementParameters.FromValues(resolved);
        Assert.Equal(320f, mp.MaxSpeed);
        Assert.Equal(750f, mp.Gravity);
        Assert.Equal(0f, mp.JumpSpeedCapMin);
        Assert.True(float.IsNaN(mp.JumpSpeedCapMax));
        Assert.True(mp.TrackCanJump);
    }

    [Fact]
    public void CaptureResolved_WithClientselectOff_EqualsTheGlobalVector()
    {
        CvarService c = NewStore(clientSelect: "0");
        float[] globals = MoveVarsBlock.Capture(c);
        float[] resolved = MoveVarsBlock.CaptureResolved(c, "cpma", globals, c.Has);
        Assert.Equal(MoveVarsBlock.Hash(globals), MoveVarsBlock.Hash(resolved)); // the wire's "no deviation" gate
    }

    [Fact]
    public void PredictionOverride_DrivesThePredictedResolve_AndDefaultsAreInert()
    {
        var player = new XonoticGodot.Common.Framework.Entity();
        // default-inert: no override, no provider → both legs read the ambient cvars (here: no services → a
        // FromCvars call would throw, so prove inertness via the override path only).
        MovementParameters mp = MovementParameters.Defaults;
        mp.MaxSpeed = 320f;
        MovementParameters.PredictionOverride = mp;
        try
        {
            Assert.Equal(320f, MovementParameters.Resolve(player, predicted: true).MaxSpeed);
        }
        finally
        {
            MovementParameters.PredictionOverride = null;
        }

        // the authoritative leg consults the provider with the entity.
        MovementParameters.PresetProvider = e =>
        {
            var c = NewStore();
            return MoveVarsBlock.CaptureResolved(c, "cpma", MoveVarsBlock.Capture(c), c.Has);
        };
        try
        {
            Assert.Equal(320f, MovementParameters.Resolve(player, predicted: false).MaxSpeed);
        }
        finally
        {
            MovementParameters.PresetProvider = null;
        }
    }
}
