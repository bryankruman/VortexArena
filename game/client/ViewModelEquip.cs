using Godot;
using XonoticGodot.Game.Loaders;            // AssetLoader
using XonoticGodot.Game.Loaders.Models;     // IqmBuilder

namespace XonoticGodot.Game.Client;

/// <summary>
/// Builds the first-person weapon view-model node for a <c>v_*</c> weapon path, faithful to Base
/// <c>CL_WeaponEntity_SetModel</c> (all.qc:367-424). The single source of truth shared by every equip path —
/// the networked <see cref="Net.NetGame"/> client feeds it the active weapon's <c>v_</c> model each switch.
///
/// <para>The value itself is the model + attach offset to hand the <see cref="ViewModel"/>, plus whether the
/// rendered node is the <c>h_</c> HAND RIG itself (full-model DPM weapons) rather than the <c>v_</c> visual
/// model (invisible-hand IQM rigs). The <see cref="Build"/> / <see cref="WeaponAttachTransform"/> statics do the
/// rig classification + node construction.</para>
/// </summary>
internal readonly struct ViewModelEquip
{
    /// <summary>The built node to render as the first-person weapon (either the v_ model or the h_ rig).</summary>
    public Node3D? Model { get; init; }
    /// <summary>The local attach transform applied to <see cref="Model"/> under the ViewBasis.</summary>
    public Transform3D Attach { get; init; }
    /// <summary>True when <see cref="Model"/> is the h_ rig itself (full-model DPM weapons); false = v_ model.</summary>
    public bool IsHandRig { get; init; }

    /// <summary>The placeholder/missing equip (no model, identity attach, v_ path).</summary>
    public static ViewModelEquip None => new() { Model = null, Attach = Transform3D.Identity, IsHandRig = false };

    /// <summary>
    /// Build the first-person weapon node for a <c>v_*</c> model path, faithful to Base
    /// <c>CL_WeaponEntity_SetModel</c> (all.qc:367-424). The branch is keyed off the sibling <c>h_*</c> HAND
    /// RIG's bones:
    /// <list type="number">
    ///   <item><b>INVISIBLE-HAND</b> (the h_ rig exposes a <c>weapon</c>/<c>tag_weapon</c> bone — the IQM rigs
    ///   h_arc/h_nex/h_shotgun/…): render the h_ rig node (dummy <c>nodraw</c> mesh hidden) with the <c>v_</c>
    ///   VISUAL model riding a live <see cref="BoneAttachment3D"/> on that bone, exactly as Base attaches the
    ///   v_ <c>weaponchild</c> to the <c>weapon</c> bone — the rig's idle/fire/reload animation sways the gun.
    ///   Falls back to the static bone-rest attach when the rig has no usable skeleton/bone.</item>
    ///   <item><b>FULL-MODEL</b> (the h_ rig has NO such bone — the DPM rigs h_rl/h_crylink/h_electro/h_gl/
    ///   h_hagar/ok_*): render the <b>h_ RIG ITSELF</b> (its own gun+hand mesh) at <c>attach = identity</c>, and
    ///   IGNORE the v_ model — Base leaves the weaponchild NULL and the rig IS the viewmodel. The rig is authored
    ///   in the same Quake→Godot model space as the v_ models, so its drawn gun and its own <c>tag_shot</c> shot
    ///   origin coincide (no rotation strip, no <c>tag_handle</c> offset — that hack is gone for these).</item>
    /// </list>
    /// When the h_ rig is missing/unparsable it degrades to the invisible-hand v_ path (legacy behaviour).
    /// </summary>
    public static ViewModelEquip Build(AssetLoader? assets, string vModelPath)
    {
        if (assets is null || string.IsNullOrEmpty(vModelPath))
            return None;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm by name; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");

        // Classify the rig WITHOUT building a Godot node (Godot-free parsed-data probe). Missing/unparsable rig
        // or a path with no h_ sibling -> treat as invisible-hand so we keep the legacy v_ render.
        bool? invisibleHand = hPath == vModelPath ? null : assets.WeaponRigIsInvisibleHand(hPath);

        // FULL-MODEL (DPM rigs, no weapon bone): the h_ rig itself is the viewmodel. attach = identity.
        if (invisibleHand == false)
        {
            Node3D? hand = assets.LoadModel(hPath);
            if (hand is not null)
                return new ViewModelEquip { Model = hand, Attach = Transform3D.Identity, IsHandRig = true };
            // Rig classified full-model but failed to build — fall through to the v_ path as a last resort.
        }

        // INVISIBLE-HAND (IQM rigs with a weapon bone): Base renders the h_ rig entity (whose only mesh is a
        // 2-triangle `nodraw` dummy plane) and attaches the v_ model to its `weapon` bone
        // (setattachment(weaponchild, this, "weapon"), all.qc:381-400) — so the rig's idle/fire/reload BONE
        // animation is what sways the gun in your hands. Reproduce that: build the rig node (its
        // AnimationPlayer autoplays the looping idle clip), hide its dummy mesh, and hang the v_ model off a
        // live BoneAttachment3D on the weapon bone. The composite (rig root) is the equip model at identity.
        Node3D? built = assets.LoadModel(vModelPath);
        if (invisibleHand == true && built is not null)
        {
            Node3D? rig = assets.LoadModel(hPath);
            if (rig is not null)
            {
                Skeleton3D? skel = IqmBuilder.FindSkeleton(rig);
                string? boneName = null;
                if (skel is not null)
                {
                    foreach (string b in new[] { "weapon", "tag_weapon" })
                    {
                        if (skel.FindBone(b) >= 0) { boneName = b; break; }
                    }
                }
                if (skel is not null && boneName is not null)
                {
                    HideRigMeshes(rig);
                    var boneFollow = new BoneAttachment3D { Name = "WeaponBoneAttach" };
                    skel.AddChild(boneFollow);           // parent FIRST so the attachment resolves its skeleton
                    boneFollow.BoneName = boneName;
                    built.Transform = Transform3D.Identity; // QC weaponchild rides the bone at zero offset
                    boneFollow.AddChild(built);
                    return new ViewModelEquip { Model = rig, Attach = Transform3D.Identity, IsHandRig = false };
                }
                rig.QueueFree(); // classified invisible-hand but no usable skeleton/bone — fall back to static
            }
        }

        // Fallback (missing/unbuildable rig, or no weapon bone resolved): render the v_ model alone, attached
        // at the rig's STATIC weapon-bone rest (position-only — see WeaponAttachTransform). No idle sway.
        Transform3D attach = WeaponAttachTransform(assets, vModelPath);
        return new ViewModelEquip { Model = built, Attach = attach, IsHandRig = false };
    }

    /// <summary>
    /// Hide every mesh of the hand rig — Base's invisible-hand rigs carry a single 2-triangle plane with the
    /// <c>nodraw</c> material (verified across h_shotgun/h_nex/h_uzi/h_arc), so the rendered result in Base is
    /// "no hands". Hiding is the deterministic equivalent regardless of how the material pipeline resolves the
    /// <c>nodraw</c> name. The skeleton keeps animating hidden meshes' bones (BoneAttachment3D still follows).
    /// </summary>
    private static void HideRigMeshes(Node node)
    {
        if (node is MeshInstance3D mesh)
            mesh.Visible = false;
        foreach (Node child in node.GetChildren())
            HideRigMeshes(child);
    }

    /// <summary>
    /// The held-gun attach offset for a <c>v_*</c> weapon path: load the sibling <c>h_*</c> hand model and read
    /// its <c>weapon</c> / <c>tag_weapon</c> bone rest (in built Godot space). This mirrors Xonotic's
    /// <c>setattachment(weaponchild, this, "weapon")</c> — the hand model is the rig that positions the
    /// <c>v_</c> visual model in the lower-right hand. Used only on the INVISIBLE-HAND path now (full-model DPM
    /// weapons render the h_ rig itself, so they never reach here); the <c>tag_handle</c> socket of the DPM rigs
    /// is therefore no longer consulted. Returns identity (gun at the eye) if no hand model / bone resolves.
    /// </summary>
    public static Transform3D WeaponAttachTransform(AssetLoader? assets, string vModelPath)
    {
        if (assets is null)
            return Transform3D.Identity;

        // v_laser.md3 -> h_laser.iqm (the hand rigs are .iqm; magic dispatch handles IQM vs DPM).
        string hPath = vModelPath
            .Replace("/v_", "/h_")
            .Replace(".md3", ".iqm");
        if (hPath == vModelPath || !assets.Vfs.Exists(hPath))
            return Transform3D.Identity;

        Node3D? hand = assets.LoadModel(hPath);
        if (hand is null)
            return Transform3D.Identity;

        // The attachment socket the v_ model rides on, in order of the QC's preference. Only invisible-hand
        // (IQM) rigs reach this helper now, and they all expose a `weapon` bone (identity rotation), so the
        // legacy `tag_handle` DPM fallback is removed — a full-model DPM rig renders the h_ rig itself and never
        // gets here, so attaching the (wrong) v_ model to its `tag_handle` socket (the "twisted crylink" hack)
        // is no longer reachable.
        Transform3D attach = Transform3D.Identity;
        foreach (string bone in new[] { "weapon", "tag_weapon" })
        {
            Transform3D rest = IqmBuilder.GetBoneGlobalRest(hand, bone);
            if (rest != Transform3D.Identity)
            {
                attach = rest;
                break;
            }
        }
        hand.QueueFree();

        // POSITION the gun only — never re-orient it. The v_ model geometry is already authored pointing
        // "forward" (the same IQM the world pickup builds correctly via Coords.ToGodot), and ViewModel's
        // ViewBasis re-aims that built-in forward into the camera frame. The socket's bind ROTATION must not be
        // layered on top (the IQM `weapon` bones carry identity rotation, so this is a no-op for them); keep the
        // socket's translation (the held-hand offset) and any uniform scale, drop the rotation/shear.
        return new Transform3D(Basis.Identity.Scaled(attach.Basis.Scale), attach.Origin);
    }
}
