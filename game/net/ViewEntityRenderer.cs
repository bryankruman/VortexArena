using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Game.Client;
using XonoticGodot.Net;

namespace XonoticGodot.Game.Net;

/// <summary>
/// Renders the held-weapon view-entities of networked players — the C# successor to CSQC's
/// <c>CL_SpawnWeaponentity</c> / the <c>wepent</c> weapon-entity channel (lib/csqcmodel + weapons/weapons.qc).
/// A remote player carries its active weapon id in <see cref="NetEntityState.Weapon"/>; this renderer attaches
/// (and swaps, on weapon change) that weapon's world model to the player, co-located with the player's
/// interpolated pose at a hand offset. A standalone <see cref="NetEntityKind.ViewModel"/> entity (a thrown gun
/// / detached weapon model) renders its own <see cref="NetEntityState.ModelIndex"/> the same way.
///
/// It owns its weapon nodes as children of the <see cref="ClientWorld"/> (so they live in the render tree and
/// are freed when it is). The actual mesh is host-injected via <see cref="WeaponModelFactory"/> (wired to the
/// asset pipeline's weapon world models); without it, a small placeholder stands in so the attachment is visible.
/// </summary>
public sealed class ViewEntityRenderer
{
    private readonly ClientWorld _render;

    /// <summary>Builds the world model for a weapon registry id (host-wired to the asset pipeline). Null → placeholder.</summary>
    public Func<int, Node3D?>? WeaponModelFactory { get; set; }

    /// <summary>The hand offset (Godot local space, relative to the player's posed frame) the weapon sits at.</summary>
    public Vector3 HandOffset { get; set; } = new(6f, 36f, -18f);

    private sealed class Held
    {
        public Node3D Holder = null!;  // posed to the player frame each Update
        public int WeaponId = int.MinValue;
        public float LastAlpha = float.NaN; // last applied render alpha (QC exteriorweaponentity.alpha)
    }

    private readonly Dictionary<int, Held> _held = new();

    public ViewEntityRenderer(ClientWorld render) => _render = render;

    /// <summary>
    /// Update (or create) the weapon view-entity for <paramref name="e"/>: a player shows its
    /// <see cref="NetEntityState.Weapon"/>; a ViewModel entity shows its own model. Re-poses the holder to the
    /// entity's interpolated transform and rebuilds the mesh only when the weapon id actually changes.
    /// </summary>
    public void Update(Entity e, in NetEntityState s)
    {
        // A dead player or a player with no weapon shows nothing.
        int weaponId = s.Kind == NetEntityKind.ViewModel ? -2 /* sentinel: use model index */ : s.Weapon;
        bool wantWeapon = (s.Flags & NetEntityFlags.Dead) == 0
                          && (s.Kind == NetEntityKind.ViewModel || weaponId >= 0);
        if (!wantWeapon)
        {
            Remove(e.Index);
            return;
        }

        if (!_held.TryGetValue(e.Index, out Held? h))
        {
            h = new Held { Holder = new Node3D { Name = $"wepent#{e.Index}" } };
            if (GodotObject.IsInstanceValid(_render))
                _render.AddChild(h.Holder);
            _held[e.Index] = h;
        }

        // Pose the holder to the player's interpolated frame (same Quake→Godot mapping as EntityNode).
        if (GodotObject.IsInstanceValid(h.Holder))
        {
            h.Holder.Position = Coords.ToGodot(e.Origin);
            h.Holder.Rotation = new Vector3(0f, -Mathf.DegToRad(e.Angles.Y), 0f);
        }

        // Rebuild the mesh only when the weapon changed (CSQCMODEL: a weapon swap respawns the wepent).
        int key = s.Kind == NetEntityKind.ViewModel ? -1 - s.ModelIndex : weaponId;
        if (h.WeaponId != key)
        {
            h.WeaponId = key;
            FreeChildren(h.Holder);
            Node3D? model = WeaponModelFactory?.Invoke(weaponId);
            model ??= Placeholder();
            if (GodotObject.IsInstanceValid(h.Holder))
            {
                model.Position = HandOffset;
                h.Holder.AddChild(model);
            }
            h.LastAlpha = float.NaN; // fresh meshes start opaque — force a re-apply of the current alpha below
        }

        // QC CL_ExteriorWeaponentity_Think (server/weapons/weaponsystem.qc:170-175): the exterior weapon entity
        // does NOT simply copy the owner's alpha — it resolves through the three-way default rule:
        //   if (owner.alpha == default_player_alpha) alpha = default_weapon_alpha;  // owner at the spawn default
        //   else if (owner.alpha != 0)               alpha = owner.alpha;           // custom fade → match owner
        //   else                                     alpha = 1;                     // owner alpha 0 → opaque
        // The FIRST branch is what makes Running Guns work: the player spawns at default_player_alpha = -1
        // (hidden) but the gun takes default_weapon_alpha = +1 (visible) — a floating visible gun on an invisible
        // player. Copying owner.alpha unconditionally would hide the gun too, defeating the mutator. Cloaked's
        // 0.25 also routes through branch 1 (default_weapon_alpha == default_player_alpha), so the gun fades with
        // the player; a per-player Invisibility powerup fade routes through branch 2 (owner != default → match).
        float weaponAlpha = ResolveExteriorWeaponAlpha(e.Alpha);
        if (GodotObject.IsInstanceValid(h.Holder) && weaponAlpha != h.LastAlpha)
        {
            h.LastAlpha = weaponAlpha;
            // Per-INSTANCE transparency (not a material edit): the weapon world models are CACHED + SHARED by
            // the asset pipeline, so mutating their materials would fade every player's weapon (the same lesson
            // PlayerModel.ApplyAlpha / CsqcModelEffects document). Godot Transparency is the inverse of QC alpha.
            float clamped = weaponAlpha < 0f ? 0f : (weaponAlpha > 1f ? 1f : weaponAlpha);
            ApplyTransparency(h.Holder, 1f - clamped);
        }
    }

    /// <summary>
    /// Resolve the exterior weapon entity's render alpha from its owner's networked alpha, porting the QC
    /// three-way rule in <c>CL_ExteriorWeaponentity_Think</c> (weaponsystem.qc:170-175):
    /// owner at <c>default_player_alpha</c> ⇒ <c>default_weapon_alpha</c>; owner at a custom non-zero fade ⇒
    /// match the owner; owner alpha 0 ⇒ opaque. <paramref name="ownerAlpha"/> is the decoded owner alpha
    /// (-1 = the QC "hidden default" sentinel, i.e. owner == default_player_alpha under Running Guns).
    /// <see cref="MutatorHooks.DefaultPlayerAlpha"/>/<see cref="MutatorHooks.DefaultWeaponAlpha"/> carry the
    /// worldspawn seed on a listen server; on a pure remote client they default to 1 (opaque), which is the
    /// correct Running-Guns weapon default — so the gun stays visible either way.
    /// </summary>
    private static float ResolveExteriorWeaponAlpha(float ownerAlpha)
    {
        float defaultPlayerAlpha = MutatorHooks.DefaultPlayerAlpha;
        float defaultWeaponAlpha = MutatorHooks.DefaultWeaponAlpha;
        // Branch 1 — owner is at the spawn default (e.g. Running Guns -1 hidden, or Cloaked 0.25): the gun
        // takes the weapon default. The decoded -1 sentinel is treated as the hidden default_player_alpha.
        if (ownerAlpha == defaultPlayerAlpha || (ownerAlpha < 0f && defaultPlayerAlpha < 0f))
            return defaultWeaponAlpha;
        // Branch 2 — owner has a custom non-zero alpha (an Invisibility-powerup fade): the gun matches it.
        if (ownerAlpha != 0f)
            return ownerAlpha;
        // Branch 3 — owner alpha 0: opaque.
        return 1f;
    }

    /// <summary>Render the owner's QC <c>.alpha</c> as a per-instance transparency on every mesh under the held
    /// weapon (0 = opaque .. 1 = invisible), matching <c>exteriorweaponentity.alpha</c>.</summary>
    private static void ApplyTransparency(Node node, float transparency)
    {
        if (node is GeometryInstance3D gi && GodotObject.IsInstanceValid(gi))
            gi.Transparency = transparency;
        foreach (Node child in node.GetChildren())
            ApplyTransparency(child, transparency);
    }

    /// <summary>Drop the weapon view-entity for a departed/removed entity id.</summary>
    public void Remove(int id)
    {
        if (_held.Remove(id, out Held? h) && GodotObject.IsInstanceValid(h.Holder))
            h.Holder.QueueFree();
    }

    /// <summary>Per-frame hook (no-op today — the holder is re-posed in <see cref="Update"/>).</summary>
    public void Process() { }

    /// <summary>Free every weapon view-entity (client teardown).</summary>
    public void Clear()
    {
        foreach (Held h in _held.Values)
            if (GodotObject.IsInstanceValid(h.Holder))
                h.Holder.QueueFree();
        _held.Clear();
    }

    private static void FreeChildren(Node n)
    {
        if (!GodotObject.IsInstanceValid(n)) return;
        foreach (Node c in n.GetChildren())
            c.QueueFree();
    }

    private static Node3D Placeholder()
    {
        // A thin "gun" box so the attachment point is visible without the real weapon asset.
        var box = new MeshInstance3D
        {
            Name = "WeaponPlaceholder",
            Mesh = new BoxMesh { Size = new Vector3(6f, 6f, 28f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.25f, 0.3f) },
        };
        return box;
    }
}
