using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Sidecars;
using SN = System.Numerics;

namespace XonoticGodot.Game.Loaders.Models;

/// <summary>
/// Turns a parsed <see cref="IqmData"/> (the engine-neutral IQM importer output) into a live Godot scene:
/// a <see cref="Skeleton3D"/>, a skinned <see cref="MeshInstance3D"/> bound to it, and an
/// <see cref="AnimationPlayer"/> holding every clip in an <see cref="AnimationLibrary"/>. IQM is the format
/// for all of Xonotic's animated content (players, monsters, vehicles, view weapons): one shared vertex
/// buffer skinned to a bone hierarchy, with animation stored as per-frame bone-LOCAL transforms.
///
/// === Coordinate convention (the crux) ===
/// The sim/asset layer is Quake space (X fwd, Y left, Z up); Godot is Y-up (X right, Y up, Z back). The
/// boundary map is <see cref="Coords.ToGodot"/> = (x, z, -y), which as a 3x3 is the proper rotation
///   M = [[1,0,0],[0,0,1],[0,-1,0]]   (a -90 deg rotation about the world X axis, det = +1).
///
/// A bone transform takes points from bone-local space to MODEL/world space, so it is a transform expressed
/// IN model space — to re-express it in Godot's model space we conjugate by M (a similarity transform):
///   W_godot = M * W_quake * M^-1.
/// Conjugation is a homomorphism, so it commutes with the parent chain:
///   W_godot[i] = M (W_q[parent] * L_q[i]) M^-1 = (M W_q[parent] M^-1)(M L_q[i] M^-1) = W_godot[parent] * conj(L_q[i]).
/// We therefore compute each bone's *world* Quake rest, conjugate it to Godot, then localize
///   L_godot[i] = W_godot[parent]^-1 * W_godot[i]
/// and feed that as the bone REST. This is provably consistent with the per-vertex map (which is just the
/// translation half of the same M), so the rest pose is undistorted. NOTE: a bone-LOCAL transform cannot be
/// conjugated on its own and re-parented — M does not commute with the parent's translation — which is why
/// both the rest pose AND every animation frame are converted through world space here, never per-local.
///
/// Quaternions: <see cref="SN.Quaternion"/> (x,y,z,w) maps component-for-component to
/// <see cref="Godot.Quaternion"/> (x,y,z,w); the basis change is then applied via the matrix conjugation
/// above (built once as <c>Mxf</c>/<c>MxfInv</c>), not by hand-deriving a rotated quaternion.
///
/// === Bind pose / skin ===
/// Bone rests carry the full Godot-space world pose (localized), so the canonical Godot bind pose
/// (rest_global^-1 per bone) is exactly what the skinned vertices expect. We hand skinning entirely to
/// <see cref="Skeleton3D.CreateSkinFromRestTransforms"/>, which builds that Skin from the rests we set — no
/// hand-rolled bind-pose inverse that could fight the convention.
///
/// === Animations / framegroups ===
/// IQM ships named clips (<see cref="IqmData.Anims"/>); a sibling <c>.iqm.framegroups</c> sidecar may instead
/// define/override the ranges (see <see cref="FrameGroup"/>). When framegroups are supplied they win, named
/// by their 5th token or by a recognised label (idle/run/jump/death/...) derived per index. Each clip becomes
/// one <see cref="Animation"/> with a position+rotation+scale 3D track per bone, keyed across its frame span at
/// 1/fps, looped per the flag, registered into one <see cref="AnimationLibrary"/> on the
/// <see cref="AnimationPlayer"/>.
/// </summary>
public static class IqmBuilder
{
    /// <summary>Name of the single <see cref="AnimationLibrary"/> registered on the player. The default
    /// (empty) library lets callers address clips by bare name — <c>player.Play("idle")</c> — matching the MD3
    /// and DPM builders so the client drives any model format identically.</summary>
    public const string LibraryName = "";

    // The Quake->Godot basis change M (a proper rotation, det +1) and its inverse, as Godot Transform3Ds with
    // zero translation. M is sourced directly from Coords.ToGodot so it can never drift from the project's one
    // source of truth: Godot's Basis(x,y,z) ctor takes the COLUMN vectors, and column j is M*e_j = the image of
    // Quake axis j. (ToGodot(1,0,0)=(1,0,0); ToGodot(0,1,0)=(0,0,-1); ToGodot(0,0,1)=(0,1,0).)
    private static readonly Transform3D Mxf;
    private static readonly Transform3D MxfInv;

    static IqmBuilder()
    {
        var m = new Basis(
            Coords.ToGodot(new SN.Vector3(1f, 0f, 0f)),  // column 0 = image of Quake +X
            Coords.ToGodot(new SN.Vector3(0f, 1f, 0f)),  // column 1 = image of Quake +Y
            Coords.ToGodot(new SN.Vector3(0f, 0f, 1f))); // column 2 = image of Quake +Z
        Mxf = new Transform3D(m, Vector3.Zero);
        MxfInv = Mxf.AffineInverse(); // pure rotation => transpose, but AffineInverse is exact and clear.
    }

    /// <summary>
    /// Conjugate a bone's WORLD transform from Quake model space into Godot model space
    /// (<c>M · W · M⁻¹</c>, the similarity transform from the class summary). The runtime skeletal poser
    /// (<c>PlayerSkeleton</c>/CPU bones) computes bone matrices in Quake world space; pushing them onto the
    /// <see cref="Skeleton3D"/> built here must go through this exact conjugation so the posed mesh stays
    /// consistent with the world-conjugated rests + skin.
    /// </summary>
    public static Transform3D ConjugateQuakeWorldToGodot(in Transform3D quakeWorld) => Mxf * quakeWorld * MxfInv;

    // =================================================================================================
    //  Public entry point
    // =================================================================================================

    /// <summary>
    /// Build a posed, skinned, animated model from <paramref name="iqm"/>. Returns a root <see cref="Node3D"/>
    /// whose children are the <see cref="Skeleton3D"/> (carrying the <see cref="MeshInstance3D"/>) and an
    /// <see cref="AnimationPlayer"/>. Materials come from <c>assets.ResolveMaterial(mesh.Material)</c>; if
    /// <paramref name="framegroups"/> is non-empty it defines the animation clips (overriding the IQM's own).
    /// </summary>
    public static Node3D Build(IqmData iqm, AssetSystem assets, IReadOnlyList<FrameGroup>? framegroups = null, SkinFile? skin = null)
    {
        ArgumentNullException.ThrowIfNull(iqm);

        var root = new Node3D { Name = "IqmModel" };

        // ---- 1. Skeleton (bones + Godot-space rests) -------------------------------------------------
        Skeleton3D skeleton;
        using (Prof.Sample("iqm.skeleton"))
        {
            skeleton = BuildSkeleton(iqm);
            root.AddChild(skeleton);
        }

        // ---- 2. Skinned mesh -------------------------------------------------------------------------
        var meshInstance = new MeshInstance3D { Name = "Mesh" };
        using (Prof.Sample("iqm.mesh"))
            meshInstance.Mesh = BuildMesh(iqm, assets, skin);

        // Skin: bind poses = rest_global^-1 for each bone, generated from the rests we just set. This is the
        // canonical Godot binding and matches our world-conjugated rests exactly (no manual inverse).
        if (HasSkinning(iqm))
        {
            meshInstance.Skin = skeleton.CreateSkinFromRestTransforms();
        }
        // The MeshInstance must live under the Skeleton3D so its Skeleton NodePath resolves to it (here "..").
        skeleton.AddChild(meshInstance);
        meshInstance.Skeleton = meshInstance.GetPathTo(skeleton);

        // ---- 3. Animations ---------------------------------------------------------------------------
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        // Bone-track paths are "Skeleton3D:bone" — relative to the MODEL ROOT (Skeleton3D is a child of it),
        // not the skeleton itself. RootNode must therefore be the model root (= the player's parent).
        player.RootNode = player.GetPathTo(root);

        AnimationLibrary library;
        string? defaultClip;
        using (Prof.Sample("iqm.anims"))
            library = BuildAnimations(iqm, skeleton, framegroups, out defaultClip);
        player.AddAnimationLibrary(LibraryName, library);

        // Auto-play a sensible default (idle if present, else the first clip) so a freshly built model moves.
        // With the default (empty) library the play name is just the clip name (no "lib/" prefix).
        if (defaultClip is not null)
        {
            player.Autoplay = string.IsNullOrEmpty(LibraryName) ? defaultClip : $"{LibraryName}/{defaultClip}";
        }

        return root;
    }

    // =================================================================================================
    //  1. Skeleton
    // =================================================================================================

    /// <summary>
    /// Create one bone per <see cref="IqmJoint"/> and set its REST to the joint's local TRS converted into
    /// Godot space via the world-conjugation described in the class summary. Bone GLOBAL rests are then
    /// recoverable from the skeleton itself (<see cref="Skeleton3D.GetBoneGlobalRest"/>), which the attachment
    /// helper and the skin both rely on, so this returns just the populated skeleton.
    /// </summary>
    private static Skeleton3D BuildSkeleton(IqmData iqm)
    {
        var skeleton = new Skeleton3D { Name = "Skeleton3D" };
        IqmJoint[] joints = iqm.Joints;
        int n = joints.Length;
        // Each bone's Godot-space WORLD rest, kept locally so children can localize against their parent.
        var worldGodotRest = new Transform3D[n];

        // First pass: add bones + parent links. IQM guarantees parent < child, so a single forward pass works.
        // Bone names are sanitised because they double as animation track sub-paths ("Skeleton3D:bone"), where
        // ':' '/' '%' '@' would break the NodePath. Attachment lookups sanitise their query the same way.
        for (int i = 0; i < n; i++)
        {
            string boneName = string.IsNullOrEmpty(joints[i].Name) ? $"bone_{i}" : SanitizeBoneName(joints[i].Name);
            skeleton.AddBone(boneName);
        }
        for (int i = 0; i < n; i++)
        {
            int parent = joints[i].Parent;
            if (parent >= 0 && parent < n)
                skeleton.SetBoneParent(i, parent);
        }

        // Second pass: world Quake rest -> conjugate to Godot world -> localize -> SetBoneRest.
        var worldQuake = new Transform3D[n];
        for (int i = 0; i < n; i++)
        {
            Transform3D localQuake = QuakeTrsToTransform(joints[i].Translate, joints[i].Rotate, joints[i].Scale);
            int parent = joints[i].Parent;
            worldQuake[i] = (parent >= 0 && parent < n) ? worldQuake[parent] * localQuake : localQuake;

            // Conjugate the WORLD transform into Godot space (similarity transform M * W * M^-1).
            Transform3D worldGodot = Mxf * worldQuake[i] * MxfInv;
            worldGodotRest[i] = worldGodot;

            // Localize against the (already-Godot) parent world so the rest is bone-local, as Godot expects.
            Transform3D localGodot = (parent >= 0 && parent < n)
                ? worldGodotRest[parent].AffineInverse() * worldGodot
                : worldGodot;

            skeleton.SetBoneRest(i, localGodot);
            // Pose to rest initially so the bound mesh shows the rest pose before any animation plays.
            skeleton.SetBonePosePosition(i, localGodot.Origin);
            skeleton.SetBonePoseRotation(i, localGodot.Basis.GetRotationQuaternion());
            skeleton.SetBonePoseScale(i, localGodot.Basis.Scale);
        }

        return skeleton;
    }

    // =================================================================================================
    //  2. Skinned mesh
    // =================================================================================================

    /// <summary>
    /// Build one <see cref="ArrayMesh"/> surface per <see cref="IqmMesh"/>, slicing the shared vertex/triangle
    /// buffers. Positions/normals go through <see cref="Coords.ToGodot"/>; BlendIndexes fill the Bones array as
    /// int4 (skeleton-global joint indices — NOT re-based), BlendWeights fill the Weights array normalized to
    /// sum 1; the surface material is resolved from the mesh's shader name. Note only the triangle *vertex*
    /// indices are re-based into the surface-local window, since each Godot surface indexes its own Vertex array.
    /// </summary>
    private static ArrayMesh BuildMesh(IqmData iqm, AssetSystem assets, SkinFile? skin)
    {
        var mesh = new ArrayMesh { ResourceName = "IqmMesh" };
        bool skinned = HasSkinning(iqm);
        int totalVerts = iqm.Positions.Length;

        int surfaceIndex = 0;
        foreach (IqmMesh sub in iqm.Meshes)
        {
            int vcount = sub.VertexCount;
            int first = sub.FirstVertex;
            if (vcount <= 0 || sub.TriangleCount <= 0)
                continue;
            if (first < 0 || first + vcount > totalVerts)
                continue;

            // _N.skin per-mesh material override (DP skin remap), keyed by the IQM mesh NAME. A mesh mapped to
            // common/nodraw renders nothing; otherwise the remap replaces the baked shader name. Mirrors how
            // Md3Builder applies its SkinFile, so every model format honors the same sidecar (RC5: IQM ignored it).
            string materialName = sub.Material;
            bool remapped = false;
            if (skin is not null && !string.IsNullOrEmpty(sub.Name)
                && skin.MeshToTexture.TryGetValue(sub.Name, out string? remap) && !string.IsNullOrEmpty(remap))
            {
                if (SkinFile.IsNoDraw(remap))
                    continue; // skin hides this mesh
                materialName = remap;
                remapped = true;
            }

            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            // Godot wants 4 bone indices + 4 weights per vertex when skinned.
            int[]? bones = skinned ? new int[vcount * 4] : null;
            float[]? weights = skinned ? new float[vcount * 4] : null;

            bool haveNormals = iqm.Normals is { Length: > 0 };
            for (int v = 0; v < vcount; v++)
            {
                int g = first + v; // global vertex index into the shared arrays.
                positions[v] = Coords.ToGodot(iqm.Positions[g]);

                if (haveNormals && g < iqm.Normals!.Length)
                {
                    Vector3 gn = Coords.ToGodot(iqm.Normals[g]);
                    normals[v] = gn.LengthSquared() > 1e-12f ? gn.Normalized() : Vector3.Up;
                }
                else
                {
                    normals[v] = Vector3.Up; // synthesized later by the engine if needed; placeholder for now.
                }

                uvs[v] = g < iqm.TexCoords.Length
                    ? new Vector2(iqm.TexCoords[g].X, iqm.TexCoords[g].Y)
                    : Vector2.Zero;

                if (skinned)
                {
                    FillSkinVertex(iqm, g, v, bones!, weights!);
                }
            }

            // Triangles are GLOBAL indices; re-base into this surface's [0, vcount) window so the surface is
            // self-contained (Godot surfaces index their own Vertex array).
            int triStart = sub.FirstTriangle * 3;
            int triEnd = (sub.FirstTriangle + sub.TriangleCount) * 3;
            triEnd = Math.Min(triEnd, iqm.Triangles.Length);
            var indices = new int[Math.Max(0, triEnd - triStart)];
            int upper = first + vcount;
            for (int t = triStart, k = 0; t < triEnd; t++, k++)
            {
                int gi = iqm.Triangles[t];
                int local = gi - first;
                // Guard against a triangle that references outside this mesh's vertex window.
                indices[k] = (gi >= first && gi < upper) ? local : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            if (skinned)
            {
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }
            arrays[(int)Mesh.ArrayType.Index] = indices;

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            // Resolve and attach the surface material from the (possibly skin-remapped) shader name. If a
            // remap resolved to the magenta fallback (a variant-only name with no shader/texture), keep the
            // baked material so a bad remap can't blank an otherwise-valid surface.
            Material? material;
            using (Prof.Sample("iqm.materials"))   // texture decode + upload live in here (skin/_norm/_gloss)
            {
                material = ResolveMaterialSafe(assets, materialName);
                if (remapped && material is not null && ReferenceEquals(material, assets.FallbackMaterial()))
                    material = ResolveMaterialSafe(assets, sub.Material);
            }
            if (material is not null)
                mesh.SurfaceSetMaterial(surfaceIndex, material);

            // Name the surface for debugging/skin remap tooling.
            mesh.SurfaceSetName(surfaceIndex, string.IsNullOrEmpty(sub.Name) ? $"surface_{surfaceIndex}" : sub.Name);
            surfaceIndex++;
        }

        return mesh;
    }

    /// <summary>
    /// Write the 4 bone indices + 4 weights for one vertex. BlendIndexes are joint indices (already global to
    /// the skeleton, so no re-basing); weights are 0..255 bytes normalized to 0..1 floats. Falls back to a
    /// rigid bind to bone 0 if the file lacks per-vertex skin data for this index.
    /// </summary>
    private static void FillSkinVertex(IqmData iqm, int globalVertex, int localVertex, int[] bones, float[] weights)
    {
        int b = localVertex * 4;
        byte[]? bi = (iqm.BlendIndexes is { } bix && globalVertex < bix.Length) ? bix[globalVertex] : null;
        byte[]? bw = (iqm.BlendWeights is { } bwx && globalVertex < bwx.Length) ? bwx[globalVertex] : null;

        if (bi is null || bw is null)
        {
            // Unskinned vertex inside a skinned model: pin fully to the root bone so it stays attached.
            bones[b] = 0; bones[b + 1] = 0; bones[b + 2] = 0; bones[b + 3] = 0;
            weights[b] = 1f; weights[b + 1] = 0f; weights[b + 2] = 0f; weights[b + 3] = 0f;
            return;
        }

        int jointCount = iqm.Joints.Length;
        float sum = 0f;
        for (int j = 0; j < 4; j++)
        {
            int idx = j < bi.Length ? bi[j] : 0;
            // BlendIndexes index into Joints (== skeleton bones); clamp defensively against malformed data so
            // Godot's skin lookup never dereferences a bone that doesn't exist.
            if (idx < 0 || idx >= jointCount)
                idx = 0;
            float w = (j < bw.Length ? bw[j] : 0) / 255f;
            bones[b + j] = idx;
            weights[b + j] = w;
            sum += w;
        }

        // Renormalize so the four weights sum to exactly 1 (IQM stores ~255 total but rounding leaves slack;
        // Godot does not renormalize for us). If all-zero (degenerate), pin to the first listed bone.
        if (sum > 1e-6f)
        {
            float inv = 1f / sum;
            weights[b] *= inv; weights[b + 1] *= inv; weights[b + 2] *= inv; weights[b + 3] *= inv;
        }
        else
        {
            weights[b] = 1f; weights[b + 1] = 0f; weights[b + 2] = 0f; weights[b + 3] = 0f;
        }
    }

    // =================================================================================================
    //  3. Animations
    // =================================================================================================

    /// <summary>
    /// Build one <see cref="AnimationLibrary"/> holding every clip. Each clip is one
    /// <see cref="IqmAnim"/> (or, when supplied, one <see cref="FrameGroup"/> which overrides the ranges),
    /// expressed as per-bone position/rotation/scale 3D tracks keyed across its frame span at 1/fps.
    /// </summary>
    private static AnimationLibrary BuildAnimations(
        IqmData iqm, Skeleton3D skeleton, IReadOnlyList<FrameGroup>? framegroups, out string? defaultClip)
    {
        defaultClip = null;
        var library = new AnimationLibrary();
        IqmFrame[] frames = iqm.Frames;
        int boneCount = skeleton.GetBoneCount();
        if (frames.Length == 0 || boneCount == 0)
            return library; // static model: no clips, the rest pose stands.

        // Build the working clip list: framegroups win when present and non-empty.
        var clips = new List<ClipSpec>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (framegroups is { Count: > 0 })
        {
            for (int i = 0; i < framegroups.Count; i++)
            {
                FrameGroup fg = framegroups[i];
                string baseName = !string.IsNullOrEmpty(fg.Name) ? fg.Name : DefaultClipName(i);
                clips.Add(new ClipSpec(UniqueName(baseName, usedNames), fg.FirstFrame, fg.FrameCount, fg.Fps, fg.Loop));
            }
        }
        else if (iqm.Anims.Length > 0)
        {
            for (int i = 0; i < iqm.Anims.Length; i++)
            {
                IqmAnim a = iqm.Anims[i];
                string baseName = !string.IsNullOrEmpty(a.Name) ? a.Name : DefaultClipName(i);
                float fps = a.FrameRate > 0f ? a.FrameRate : 20f;
                clips.Add(new ClipSpec(UniqueName(baseName, usedNames), a.FirstFrame, a.FrameCount, fps, a.Loop));
            }
        }
        else
        {
            // No clip metadata at all: expose the whole frame stack as one looping "all" clip at 20 fps.
            clips.Add(new ClipSpec("all", 0, frames.Length, 20f, true));
        }

        foreach (ClipSpec clip in clips)
        {
            Animation anim = BuildOneAnimation(iqm, skeleton, clip, boneCount);
            library.AddAnimation(clip.Name, anim);

            // Choose an autoplay default: prefer any "idle"-named clip, else fall back to the first clip.
            bool isIdle = clip.Name.Contains("idle", StringComparison.OrdinalIgnoreCase);
            if (defaultClip is null || isIdle)
                defaultClip = clip.Name;
        }

        return library;
    }

    /// <summary>
    /// Build a single <see cref="Animation"/> for one clip: a position+rotation+scale track per bone, keyed at
    /// each frame in the clip's span at 1/fps. Each frame's bone-LOCAL Quake TRS is converted to a Godot
    /// bone-LOCAL transform through the same world-conjugation as the rest pose (computed fresh per frame so
    /// the non-commuting parent translation is handled correctly).
    /// </summary>
    private static Animation BuildOneAnimation(IqmData iqm, Skeleton3D skeleton, ClipSpec clip, int boneCount)
    {
        IqmFrame[] frames = iqm.Frames;
        IqmJoint[] joints = iqm.Joints;

        // Clamp the clip span into the real frame range (framegroups are not pre-clamped to this model).
        int firstFrame = Math.Clamp(clip.FirstFrame, 0, frames.Length - 1);
        int frameCount = Math.Clamp(clip.FrameCount, 1, frames.Length - firstFrame);
        float fps = clip.Fps > 0f ? clip.Fps : 20f;
        float dt = 1f / fps;

        var anim = new Animation
        {
            // Length is the time of the LAST keyed frame; the player handles loop wrap.
            Length = Math.Max(dt, (frameCount - 1) * dt),
            LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None,
        };

        // Create the 3 tracks per bone up front and remember their indices.
        var posTrack = new int[boneCount];
        var rotTrack = new int[boneCount];
        var sclTrack = new int[boneCount];
        for (int bone = 0; bone < boneCount; bone++)
        {
            string boneName = skeleton.GetBoneName(bone);
            // Paths are relative to the AnimationPlayer.RootNode (the Skeleton3D): "<skeleton>:<bone>".
            // Because RootNode points AT the skeleton, the leading node is just the skeleton itself.
            var basePath = $"{skeleton.Name}:{boneName}";

            posTrack[bone] = anim.AddTrack(Animation.TrackType.Position3D);
            anim.TrackSetPath(posTrack[bone], basePath);

            rotTrack[bone] = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(rotTrack[bone], basePath);

            sclTrack[bone] = anim.AddTrack(Animation.TrackType.Scale3D);
            anim.TrackSetPath(sclTrack[bone], basePath);
        }

        // Scratch arrays reused across every frame of this clip (one alloc per clip, not per frame).
        var worldGodot = new Transform3D[boneCount]; // each bone's Godot-space WORLD transform this frame
        var worldQuake = new Transform3D[boneCount]; // its Quake-space WORLD transform (pre-conjugation)

        for (int f = 0; f < frameCount; f++)
        {
            IqmFrame frame = frames[firstFrame + f];
            IqmBonePose[] poses = frame.Bones;
            float time = f * dt;

            // Pass 1: chain frame bone-local TRS through parents into Quake world, then conjugate by M into
            // Godot world. Computing the world fresh each frame is what makes the (non-commuting) parent
            // translation convert correctly — a per-bone-local conjugation would distort the pose.
            ComputeFrameWorlds(joints, poses, boneCount, worldQuake, worldGodot);

            // Pass 2: localize each bone against its Godot parent world and key the local TRS.
            for (int bone = 0; bone < boneCount; bone++)
            {
                int parent = bone < joints.Length ? joints[bone].Parent : -1;
                Transform3D localGodot = (parent >= 0 && parent < boneCount)
                    ? worldGodot[parent].AffineInverse() * worldGodot[bone]
                    : worldGodot[bone];

                Vector3 pos = localGodot.Origin;
                Quaternion rot = localGodot.Basis.GetRotationQuaternion();
                Vector3 scale = localGodot.Basis.Scale;

                anim.PositionTrackInsertKey(posTrack[bone], time, pos);
                anim.RotationTrackInsertKey(rotTrack[bone], time, rot);
                anim.ScaleTrackInsertKey(sclTrack[bone], time, scale);
            }
        }

        return anim;
    }

    private static readonly Transform3D Identity = Transform3D.Identity;

    /// <summary>
    /// Fill <paramref name="worldGodot"/> with each bone's Godot-space WORLD transform for one frame: build the
    /// Quake world (into the caller's <paramref name="worldQuake"/> scratch) by chaining frame bone-local TRS
    /// through parents, then conjugate by M. Parents precede children (IQM guarantees parent &lt; child), so one
    /// forward pass suffices. Both scratch arrays are owned by the caller and reused across frames.
    /// </summary>
    private static void ComputeFrameWorlds(
        IqmJoint[] joints, IqmBonePose[] poses, int boneCount,
        Transform3D[] worldQuake, Transform3D[] worldGodot)
    {
        for (int bone = 0; bone < boneCount; bone++)
        {
            Transform3D localQuake = bone < poses.Length
                ? QuakeTrsToTransform(poses[bone].Translate, poses[bone].Rotate, poses[bone].Scale)
                : (bone < joints.Length
                    ? QuakeTrsToTransform(joints[bone].Translate, joints[bone].Rotate, joints[bone].Scale)
                    : Identity);

            int parent = bone < joints.Length ? joints[bone].Parent : -1;
            worldQuake[bone] = (parent >= 0 && parent < boneCount)
                ? worldQuake[parent] * localQuake
                : localQuake;

            worldGodot[bone] = Mxf * worldQuake[bone] * MxfInv;
        }
    }

    // =================================================================================================
    //  4. Attachment query helper
    // =================================================================================================

    /// <summary>
    /// World-space (model-relative) REST transform of a named bone, for attaching weapons/effects to a socket
    /// (the IQM analogue of an MD3 tag; this is what the <c>gettaginfo</c>/<c>setattachment</c> facade queries).
    /// Returns <see cref="Transform3D.Identity"/> if the bone name is unknown. For the LIVE animated transform,
    /// use the skeleton's <c>GetBoneGlobalPose(idx)</c> at runtime; this returns the static bind/rest pose.
    /// </summary>
    public static Transform3D GetBoneGlobalRest(Skeleton3D skeleton, string boneName)
    {
        if (skeleton is null || string.IsNullOrEmpty(boneName))
            return Transform3D.Identity;
        // Bones were added under sanitised names; sanitise the query so callers can pass the raw IQM joint name.
        int idx = skeleton.FindBone(SanitizeBoneName(boneName));
        if (idx < 0)
            idx = skeleton.FindBone(boneName); // fall back to an exact match if the caller pre-sanitised.
        if (idx < 0)
            return Transform3D.Identity;
        // GetBoneGlobalRest composes the per-bone rests up the parent chain into model space — exactly the
        // Godot-space world rest we built, so attachment sockets line up with the conjugated bone convention.
        return skeleton.GetBoneGlobalRest(idx);
    }

    /// <summary>
    /// Convenience overload: find the model's <see cref="Skeleton3D"/> under <paramref name="modelRoot"/> (as
    /// returned by <see cref="Build"/>) and query a bone's global rest in one call.
    /// </summary>
    public static Transform3D GetBoneGlobalRest(Node3D modelRoot, string boneName)
    {
        Skeleton3D? skel = FindSkeleton(modelRoot);
        return skel is null ? Transform3D.Identity : GetBoneGlobalRest(skel, boneName);
    }

    /// <summary>Locate the <see cref="Skeleton3D"/> child of a model root built by <see cref="Build"/>.</summary>
    public static Skeleton3D? FindSkeleton(Node3D modelRoot)
    {
        if (modelRoot is null)
            return null;
        foreach (Node child in modelRoot.GetChildren())
            if (child is Skeleton3D s)
                return s;
        return null;
    }

    // =================================================================================================
    //  Shared math + utilities
    // =================================================================================================

    /// <summary>
    /// Compose a bone-local Quake TRS (translate * rotate * scale) into a <see cref="Transform3D"/> carrying
    /// RAW Quake numbers (no axis swap yet — the swap happens once at world level via conjugation). The
    /// quaternion maps component-for-component (x,y,z,w)->(x,y,z,w).
    /// </summary>
    private static Transform3D QuakeTrsToTransform(SN.Vector3 translate, SN.Quaternion rotate, SN.Vector3 scale)
    {
        var q = new Quaternion(rotate.X, rotate.Y, rotate.Z, rotate.W);
        // Guard a zero/denormal quaternion (some exporters write (0,0,0,0)) -> identity rotation.
        if (!(q.LengthSquared() > 1e-12f))
            q = Quaternion.Identity;
        else
            q = q.Normalized();

        // Compose R * S (rotation THEN non-uniform scale, applied in the bone's local space). Note Godot's
        // Basis.Scaled pre-multiplies (S * B), which only equals R * S for uniform scale; build R * S
        // explicitly via post-multiplication so non-uniform bone scale is not sheared.
        Basis basis = new Basis(q) * Basis.FromScale(new Vector3(scale.X, scale.Y, scale.Z));
        return new Transform3D(basis, new Vector3(translate.X, translate.Y, translate.Z));
    }

    /// <summary>True if the model carries per-vertex skin data (so the mesh should be bound to the skeleton).</summary>
    private static bool HasSkinning(IqmData iqm)
        => iqm.Joints.Length > 0 && iqm.BlendIndexes is { Length: > 0 } && iqm.BlendWeights is { Length: > 0 };

    /// <summary>Resolve a material through the asset facade, tolerating a null facade (build-order safety).</summary>
    private static Material? ResolveMaterialSafe(AssetSystem assets, string materialName)
    {
        if (assets is null || string.IsNullOrEmpty(materialName))
            return null;
        return assets.ResolveMaterial(materialName);
    }

    /// <summary>A normalized clip definition independent of whether it came from an IQM anim or a framegroup.</summary>
    private readonly record struct ClipSpec(string Name, int FirstFrame, int FrameCount, float Fps, bool Loop);

    /// <summary>Synthesize a clip name from its index, biased toward Xonotic's canonical anim order when it
    /// lines up (idle/run/jump/...); otherwise <c>anim_&lt;i&gt;</c>. Pure naming convenience — ranges are
    /// authoritative.</summary>
    private static string DefaultClipName(int i) => i >= 0 && i < CanonicalAnimNames.Length
        ? CanonicalAnimNames[i]
        : $"anim_{i}";

    // Common Xonotic player-anim ordering used when a framegroup/anim has no explicit name. Not load-bearing.
    private static readonly string[] CanonicalAnimNames =
    {
        "idle", "run", "runbackwards", "strafeleft", "straferight",
        "jump", "duck", "duckwalk", "duckjump", "draw", "pain", "death1", "death2",
    };

    /// <summary>De-duplicate clip names (Godot library keys must be unique) by appending an index suffix.</summary>
    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string name = string.IsNullOrEmpty(baseName) ? "anim" : baseName;
        if (used.Add(name))
            return name;
        int k = 1;
        while (!used.Add($"{name}_{k}"))
            k++;
        return $"{name}_{k}";
    }

    /// <summary>
    /// Replace characters Godot forbids in node/bone names — the NodePath separators <c>: / @ %</c> and the
    /// dot — with underscores, so a bone name can be embedded verbatim in an animation track sub-path
    /// (<c>"Skeleton3D:bone"</c>). Matches the sibling MD3/DPM builders' sanitiser.
    /// </summary>
    private static string SanitizeBoneName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            buf[n++] = c is ':' or '/' or '@' or '%' or '.' ? '_' : c;
        return new string(buf[..n]);
    }
}
