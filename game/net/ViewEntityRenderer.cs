using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
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
        }
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
