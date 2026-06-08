using System;
using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Verifies the per-weapon weapon-shot-origin (QC w_shotorg muzzle = <c>ent.(weaponentity).movedir</c>, the
/// weapon model's <c>tag_shot</c> set in <c>CL_WeaponEntity_SetModel</c>): the registry +
/// <see cref="WeaponFiring.SetupShot"/> consumption (a registered per-weapon offset is applied via the view
/// basis; an un-registered weapon falls back to the generic <see cref="WeaponFiring.DefaultMuzzleOffset"/>),
/// and the Godot-free <see cref="MuzzleTag"/> tag-extraction for MD3/IQM/DPM in model-local Quake coords.
///
/// Installs the ambient <see cref="Api.Services"/> (a free-space trace so the muzzle tracebox legs land exactly
/// at the offset), so it runs in the serialized GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public class MuzzleOffsetTests
{
    // A player actor at the origin facing +X (Angles 0). Eye = origin + view_ofs. With Angles 0 the Quake basis
    // is forward=(1,0,0), right=(0,-1,0), up=(0,0,1), so SetupShot's `right*(-md.y) + up*md.z` then forward-by-md.x
    // places w_shotorg at eye + (md.x, md.y, md.z) — i.e. the model-local offset read straight off in world axes.
    private static Entity MakePlayer(int weaponId)
        => new()
        {
            Flags = EntFlags.Client,
            Origin = new Vector3(0, 0, 0),
            Angles = Vector3.Zero,
            ViewOfs = new Vector3(0, 0, 20),
            ActiveWeaponId = weaponId,
        };

    [Fact]
    public void SetupShot_Uses_Registered_PerWeapon_Offset()
    {
        using var _ = new MuzzleEnv();
        WeaponFiring.ClearMuzzleOffsets();
        const int weaponId = 7;
        // A distinctive non-default tag_shot (model-local Quake: forward, +left, up).
        var movedir = new Vector3(25f, 6f, -3f);
        WeaponFiring.RegisterMuzzleOffset(weaponId, movedir);

        Entity actor = MakePlayer(weaponId);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        ShotInfo shot = WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0));

        // With Angles 0 the offset maps to world axes directly: +X forward, +Y left, +Z up.
        Assert.Equal(eye.X + movedir.X, shot.Origin.X, 3);
        Assert.Equal(eye.Y + movedir.Y, shot.Origin.Y, 3);
        Assert.Equal(eye.Z + movedir.Z, shot.Origin.Z, 3);
    }

    [Fact]
    public void SetupShot_Falls_Back_To_Generic_When_Unregistered()
    {
        using var _ = new MuzzleEnv();
        WeaponFiring.ClearMuzzleOffsets();
        const int weaponId = 3; // nothing registered for it
        Vector3 md = WeaponFiring.DefaultMuzzleOffset;

        Entity actor = MakePlayer(weaponId);
        Vector3 eye = actor.Origin + actor.ViewOfs;

        ShotInfo shot = WeaponFiring.SetupShot(actor, new Vector3(1, 0, 0));

        Assert.Equal(eye.X + md.X, shot.Origin.X, 3);
        Assert.Equal(eye.Y + md.Y, shot.Origin.Y, 3);
        Assert.Equal(eye.Z + md.Z, shot.Origin.Z, 3);
    }

    [Fact]
    public void MuzzleOffsetFor_Picks_Active_Weapon()
    {
        WeaponFiring.ClearMuzzleOffsets();
        WeaponFiring.RegisterMuzzleOffset(1, new Vector3(10f, 0f, 0f));
        WeaponFiring.RegisterMuzzleOffset(2, new Vector3(20f, 0f, 0f));

        Assert.Equal(new Vector3(20f, 0f, 0f), WeaponFiring.MuzzleOffsetFor(MakePlayer(2)));
        Assert.Equal(new Vector3(10f, 0f, 0f), WeaponFiring.MuzzleOffsetFor(MakePlayer(1)));
        // Unregistered id -> generic.
        Assert.Equal(WeaponFiring.DefaultMuzzleOffset, WeaponFiring.MuzzleOffsetFor(MakePlayer(99)));
    }

    // ---- MuzzleTag extraction (Godot-free, model-local Quake coords) --------------------------------

    [Fact]
    public void MuzzleTag_Md3_Reads_Raw_Tag_Origin()
    {
        var shot = new Vector3(23.5f, 0.5f, -1.25f); // a plausible forward muzzle
        var tag = new Md3Tag
        {
            Name = "tag_shot",
            Transforms = new[] { new Md3TagTransform(shot, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ) },
        };
        var md3 = new Md3Data
        {
            FrameCount = 1,
            Tags = new[] { tag },
            TagsByName = new Dictionary<string, Md3Tag>(StringComparer.Ordinal) { ["tag_shot"] = tag },
        };

        Vector3? got = MuzzleTag.Extract(md3);
        Assert.NotNull(got);
        Assert.Equal(shot, got!.Value);
        Assert.True(got.Value.X > 1f, "a real shot tag sits well forward of the eye (non-default forward offset)");
    }

    [Fact]
    public void MuzzleTag_Md3_Null_When_No_Shot_Tag()
    {
        var md3 = new Md3Data { FrameCount = 1 }; // no tags at all
        Assert.Null(MuzzleTag.Extract(md3));
    }

    [Fact]
    public void MuzzleTag_Iqm_Composes_Bind_Pose_Up_The_Parent_Chain()
    {
        // Two-joint chain: root translated +X 10, child "tag_shot" translated +X 15 (local). The model-local
        // bind position of the child is the composed translation = (25, 0, 0) (no rotation/scale).
        var iqm = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("root", -1, new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
                new IqmJoint("tag_shot", 0, new Vector3(15, 0, 0), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.Extract(iqm);
        Assert.NotNull(got);
        Assert.Equal(25f, got!.Value.X, 3);
        Assert.Equal(0f, got.Value.Y, 3);
        Assert.Equal(0f, got.Value.Z, 3);
    }

    [Fact]
    public void MuzzleTag_Iqm_Honors_Parent_Rotation()
    {
        // root yawed +90deg about Z, child offset +X 10 in the root's LOCAL frame -> in model-local space the
        // child sits along +Y (the rotated forward). Verifies the full TRS chain (not just translation sums).
        Quaternion yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var iqm = new IqmData
        {
            Version = 2,
            Joints = new[]
            {
                new IqmJoint("root", -1, Vector3.Zero, yaw90, Vector3.One),
                new IqmJoint("shot", 0, new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
            },
        };

        Vector3? got = MuzzleTag.Extract(iqm);
        Assert.NotNull(got);
        Assert.Equal(0f, got!.Value.X, 2);
        Assert.Equal(10f, got.Value.Y, 2);
        Assert.Equal(0f, got.Value.Z, 2);
    }

    [Fact]
    public void MuzzleTag_Dpm_Composes_Bind_Frame_Pose()
    {
        // Identity-basis poses (Right=+X, Up=+Y, Forward=+Z) with translations; child "tag_shot" under root.
        DpmBonePose Pose(Vector3 origin) =>
            new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, origin);

        var dpm = new DpmData
        {
            Bones = new[]
            {
                new DpmBone("root", -1, 0),
                new DpmBone("tag_shot", 0, DpmBone.AttachmentFlag),
            },
            Frames = new[]
            {
                new DpmFrame
                {
                    Name = "base",
                    BonePoses = new[] { Pose(new Vector3(5, 0, 0)), Pose(new Vector3(12, 0, 0)) },
                },
            },
        };

        Vector3? got = MuzzleTag.Extract(dpm);
        Assert.NotNull(got);
        Assert.Equal(17f, got!.Value.X, 3);
        Assert.Equal(0f, got.Value.Y, 3);
        Assert.Equal(0f, got.Value.Z, 3);
    }

    // ---- real-asset regression (guards the v_-vs-h_ wiring: the shot tag lives on the h_ HAND RIG) --

    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";

    /// <summary>
    /// The real shipped weapon HAND RIGS (h_*.iqm) carry the <c>shot</c>/<c>tag_shot</c> tag and yield a sensible
    /// forward muzzle offset; the v_ VISUAL models do NOT (they're attached to the rig). This is the regression
    /// that a synthetic-only suite missed — the feature was silently inert when wired to the v_ path. CI-portable:
    /// no-ops when the asset checkout is absent (mirrors WeaponBalanceTests/ConfigTests).
    /// </summary>
    [Theory]
    [InlineData("models/weapons/h_rl.iqm")]   // Devastator (rocket launcher) — the reported weapon
    [InlineData("models/weapons/h_arc.iqm")]
    [InlineData("models/weapons/h_crylink.iqm")]
    [InlineData("models/weapons/h_electro.iqm")]
    [InlineData("models/weapons/h_hagar.iqm")]
    public void MuzzleTag_RealHandRig_Yields_Forward_Muzzle(string rel)
    {
        string path = System.IO.Path.Combine(Pk3Dir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(path)) return; // no checkout → no-op

        Vector3? got = ExtractFromFile(path);
        Assert.True(got is not null, $"{rel} has no shot tag — the per-weapon muzzle would be silently inert");
        Vector3 md = got!.Value;
        // tag_shot sits well forward of the weapon origin (the verifier measured X≈18-61 across all 27 rigs),
        // and forward must dominate the lateral/vertical components (it's a muzzle down the barrel).
        Assert.True(md.X > 5f, $"{rel}: expected a forward muzzle offset, got {md}");
        Assert.True(md.X > MathF.Abs(md.Y) && md.X > MathF.Abs(md.Z), $"{rel}: forward should dominate, got {md}");
        // And it must NOT equal the generic fallback (proves a real per-weapon value was read).
        Assert.NotEqual(WeaponFiring.DefaultMuzzleOffset, md);
    }

    /// <summary>Magic-byte model dispatch (extensions lie) → <see cref="MuzzleTag"/>, mirroring AssetLoader.</summary>
    private static Vector3? ExtractFromFile(string path)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        string magic = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Extract(Md3Reader.Read(bytes));
        return null;
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    /// <summary>A trace that never obstructs: every leg lands at the requested end (free space).</summary>
    private sealed class FreeTrace : ITraceService
    {
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
            => TraceResult.Miss(end);
        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    private sealed class MuzzleEnv : IDisposable
    {
        private readonly IEngineServices _prev;
        public MuzzleEnv() { _prev = Api.Services; Api.Services = new MuzzleServices(new FreeTrace()); }
        public void Dispose() { WeaponFiring.ClearMuzzleOffsets(); Api.Services = _prev; }
    }

    private sealed class MuzzleServices : IEngineServices
    {
        public ITraceService Trace { get; }
        public IGameClock Clock { get; } = new ZeroClock();
        public ICvarService Cvars { get; } = new EmptyCvars();
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
        public MuzzleServices(ITraceService trace) => Trace = trace;

        private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }
        private sealed class EmptyCvars : ICvarService
        {
            public float GetFloat(string name) => 0f;
            public string GetString(string name) => "";
            public void Set(string name, string value) { }
            public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => Array.Empty<Entity>();
        }
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
