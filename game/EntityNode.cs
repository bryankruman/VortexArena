using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;

namespace XonoticGodot.Game;

/// <summary>
/// The presentation-side binding between a simulation <see cref="Entity"/> (Quake coords, Godot-free)
/// and a Godot <see cref="Node3D"/>. Implements <see cref="IEntityPresence"/> so it can be hung off
/// <see cref="Entity.Presence"/> (the client-only link declared in Framework/Entity.cs, kept as an
/// interface so the sim stays Godot-free per ADR-0008).
///
/// Each frame it pulls the entity's authoritative origin/yaw and writes them to the node's transform,
/// converting Quake (Z-up) -> Godot (Y-up) at the boundary. The sim owns the state; this node only
/// reflects it.
/// </summary>
public partial class EntityNode : Node3D, IEntityPresence
{
    /// <summary>The bound simulation entity. Set this (and optionally <see cref="Entity.Presence"/>) after construction.</summary>
    public Entity? Entity { get; set; }

    /// <summary>Attach to an entity and register this node as its presence link.</summary>
    public void Bind(Entity entity)
    {
        Entity = entity;
        entity.Presence = this;
        SyncFromEntity();
    }

    public override void _Process(double delta)
    {
        SyncFromEntity();
    }

    /// <summary>Copy the bound entity's origin and yaw onto this node's transform (Quake -> Godot).</summary>
    public void SyncFromEntity()
    {
        if (Entity is null)
            return;

        Vector3 position = Coords.ToGodot(Entity.Origin);
        float yawDeg = Entity.Angles.Y;

        // QC ItemDraw bob+spin (client/items/items.qc): a world pickup with an animation class floats on a sine
        // wave and spins about yaw, all client-side. The bob's base offset also lifts the model clear of the
        // floor it rests on (otherwise a tall item like megahealth renders half-sunk). Resting items use absolute
        // client time so a class bobs in phase, matching QC's anim_start_time == 0 path.
        if (Entity.ItemAnimate != 0)
        {
            float time = Api.Services is not null ? Api.Clock.Time : 0f;
            (float bobHeight, float yawSpinDeg) = ItemBobAnim.Sample(Entity.ItemAnimate, time);
            position.Y += bobHeight; // Quake +Z (up) maps to Godot +Y under ToGodot=(x,z,-y)
            yawDeg += yawSpinDeg;
        }

        Position = position;

        // Quake yaw is rotation about +Z (up). Under ToGodot=(x,z,-y), +Z maps to Godot +Y, and a
        // positive Quake yaw (+X toward +Y) becomes a negative rotation about Godot +Y.
        Rotation = new Vector3(0f, -Mathf.DegToRad(yawDeg), 0f);
    }
}
