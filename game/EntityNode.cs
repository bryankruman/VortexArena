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
        using var _enScope = XonoticGodot.Game.Client.FrameProfiler.Scope("entitynode"); // [profiling] all EntityNode syncs
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

        // [T48] Pitched/rolled entities (misc_gamemodel props on courtfun/space-elevator etc.) need the FULL
        // orientation, not just yaw. Use the proven AngleVectors → Coords.ToGodot columns basis (the same
        // convention FirstPersonView/the camera use — see NetGame's note that a negated-yaw Euler flips
        // handedness): mesh verts are ToGodot-converted, so the node basis is the Quake rotation conjugated
        // by ToGodot — columns X=ToGodot(forward), Y=ToGodot(up), Z=ToGodot(right). Gated to entities that
        // actually carry pitch/roll so the long-standing yaw-only path below stays byte-identical for
        // players/items/monsters (changing their convention is a separate, visually-verified pass).
        if (Entity.Angles.X != 0f || Entity.Angles.Z != 0f)
        {
            var angles = new System.Numerics.Vector3(Entity.Angles.X, yawDeg, Entity.Angles.Z);
            XonoticGodot.Common.Math.QMath.AngleVectors(angles,
                out System.Numerics.Vector3 fwd, out System.Numerics.Vector3 right, out System.Numerics.Vector3 up);
            Basis = new Basis(Coords.ToGodot(fwd), Coords.ToGodot(up), Coords.ToGodot(right));
        }
        else
        {
            // Quake yaw is rotation about +Z (up). Under ToGodot=(x,z,-y), +Z maps to Godot +Y, and a
            // positive Quake yaw (+X toward +Y) becomes a negative rotation about Godot +Y.
            Rotation = new Vector3(0f, -Mathf.DegToRad(yawDeg), 0f);
        }

        // [T48] Prop scale (QC .scale → Entity.ScaleFactor, models.qc g_model_init): applied for the
        // listen-server/demo paths, where the shared entity carries it. 0 = unset (remote clients miss it
        // until an EntityField.Scale protocol bump — deferred, see the T48 brief).
        float s = Entity.ScaleFactor;
        if (s > 0f && System.MathF.Abs(s - 1f) > 0.001f)
            Scale = new Vector3(s, s, s);
    }
}
