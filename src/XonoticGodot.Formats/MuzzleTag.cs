using System;
using System.Numerics;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;

namespace XonoticGodot.Formats;

/// <summary>
/// Extracts a weapon view-model's <c>tag_shot</c> position in <b>model-LOCAL Quake coordinates</b>
/// (x = forward, y = +left, z = up) — the C# successor to Base's
/// <c>movedir = gettaginfo(weapon, "shot")</c> in <c>CL_WeaponEntity_SetModel</c>
/// (qcsrc/common/weapons/all.qc). Base places the weapon view-model at origin/angles 0 and reads the
/// <c>tag_shot</c> (a.k.a. <c>"shot"</c>) bone's returned position, so the tag's value IS its model-local
/// offset; that offset becomes <c>w_shotorg</c>'s muzzle slide in <c>W_SetupShot</c>.
///
/// <para>This is a pure parsed-data computation (no Godot, no render pipeline): for MD3 the tag origin is
/// already stored raw model-local; for the skeletal IQM/DPM rigs the bind-pose bone is resolved by chaining
/// parent transforms (joints/bones are stored parent-first) to a model-local translation. The result feeds
/// <c>WeaponFiring.SetupShot</c>'s existing <c>right*(-md.y) + up*md.z</c> / forward-by-<c>md.x</c> formula
/// with NO conversion.</para>
///
/// <para>Tag-name search order matches Base exactly: the muzzle (QC <c>movedir</c>) comes ONLY from the
/// <c>shot</c>/<c>tag_shot</c> tag (all.qc:369,416). The <c>weapon</c>/<c>tag_weapon</c> socket is a DIFFERENT
/// quantity (the v_-model attach point / <c>oldorigin</c>, all.qc:448) — using it as a muzzle would mis-place
/// the shot at the hand. Returns <c>null</c> when the model carries no shot tag, so the caller keeps the
/// generic muzzle fallback (Base uses <c>'0 0 0'</c>; the port's fallback is the generic offset, then re-aims
/// the direction at the crosshair anyway).</para>
/// </summary>
public static class MuzzleTag
{
    /// <summary>The tag/bone names a weapon muzzle can live under, in Base's resolution order (all.qc:369).</summary>
    private static readonly string[] ShotTagNames = { "shot", "tag_shot" };

    /// <summary>MD3: the tag origin is stored raw in model-local Quake coords (frame 0 = bind pose).</summary>
    public static Vector3? Extract(Md3Data md3)
    {
        if (md3 is null) return null;
        foreach (string name in ShotTagNames)
        {
            if (md3.TagsByName.TryGetValue(name, out Md3Tag? tag) && tag.Transforms.Length > 0)
                return tag.Transforms[0].Origin;
        }
        return null;
    }

    /// <summary>
    /// IQM: resolve the bind-pose model-local translation of the shot bone by composing its rest TRS chain
    /// up the parent hierarchy (joints are stored parent-first, parent index &lt; child index).
    /// </summary>
    public static Vector3? Extract(IqmData iqm)
    {
        if (iqm is null || iqm.Joints.Length == 0) return null;
        foreach (string name in ShotTagNames)
        {
            for (int i = 0; i < iqm.Joints.Length; i++)
            {
                if (!NameMatches(iqm.Joints[i].Name, name)) continue;
                return IqmBoneWorldRest(iqm, i).Translation;
            }
        }
        return null;
    }

    /// <summary>
    /// DPM: resolve the bind-pose (frame 0) model-local translation of the shot bone by composing its
    /// parent-relative 3x4 pose chain up the hierarchy (bones are stored parent-first).
    /// </summary>
    public static Vector3? Extract(DpmData dpm)
    {
        if (dpm is null || dpm.Bones.Length == 0 || dpm.Frames.Length == 0) return null;
        DpmFrame bind = dpm.Frames[0];
        if (bind.BonePoses.Length < dpm.Bones.Length) return null;
        foreach (string name in ShotTagNames)
        {
            for (int i = 0; i < dpm.Bones.Length; i++)
            {
                if (!NameMatches(dpm.Bones[i].Name, name)) continue;
                return DpmBoneWorldRest(dpm, bind, i).Translation;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    //  Hierarchy composition (parent-first storage → a single forward chain to world/model-local)
    // ---------------------------------------------------------------------------------------------

    /// <summary>World (model-local) rest matrix of IQM joint <paramref name="idx"/> = world(parent) * Local.</summary>
    private static Matrix4x4 IqmBoneWorldRest(IqmData iqm, int idx)
    {
        IqmJoint j = iqm.Joints[idx];
        Matrix4x4 local = Trs(j.Translate, j.Rotate, j.Scale);
        return j.Parent >= 0 ? local * IqmBoneWorldRest(iqm, j.Parent) : local;
    }

    /// <summary>World (model-local) rest matrix of DPM bone <paramref name="idx"/> at the bind frame.</summary>
    private static Matrix4x4 DpmBoneWorldRest(DpmData dpm, DpmFrame bind, int idx)
    {
        Matrix4x4 local = bind.BonePoses[idx].ToMatrix();
        int parent = dpm.Bones[idx].Parent;
        return parent >= 0 ? local * DpmBoneWorldRest(dpm, bind, parent) : local;
    }

    /// <summary>TRS compose in System.Numerics' row-vector convention (matches <c>DpmBonePose.ToMatrix</c>).</summary>
    private static Matrix4x4 Trs(Vector3 t, Quaternion r, Vector3 s)
        => Matrix4x4.CreateScale(s)
         * Matrix4x4.CreateFromQuaternion(r)
         * Matrix4x4.CreateTranslation(t);

    private static bool NameMatches(string boneName, string want)
        => string.Equals(boneName, want, StringComparison.OrdinalIgnoreCase);
}
