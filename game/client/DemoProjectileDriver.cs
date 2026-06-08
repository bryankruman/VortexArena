using Godot;
using XonoticGodot.Common.Framework;

namespace XonoticGodot.Game.Client;

/// <summary>
/// A throwaway client-side mover used by the demo to exercise <see cref="ProjectileRenderer"/> without a
/// server: it advances a client-only projectile <see cref="Entity"/>'s Quake origin by its velocity each
/// frame (so the renderer's follow/interp has something to track), then removes the visual with an impact
/// effect + sound once its life expires.
///
/// This is purely a stand-in for the networked entity stream — in the real client, projectile entities and
/// their per-frame origins arrive over the wire and <see cref="ClientWorld.OnEntityUpdate"/> /
/// <see cref="ClientWorld.OnEntityRemove"/> drive the same <see cref="ProjectileRenderer"/>. It lives in its
/// own file (not nested in GameDemo) so the Godot source generator treats it as a normal top-level node type.
/// </summary>
public sealed partial class DemoProjectileDriver : Node
{
    /// <summary>The client-only projectile entity this driver moves.</summary>
    public required Entity Projectile;

    /// <summary>The client world that renders the projectile (and plays the impact effect/sound).</summary>
    public required ClientWorld Client;

    /// <summary>Seconds before the projectile self-removes (and "impacts").</summary>
    public float LifeSeconds = 2.0f;

    /// <summary>EFFECT_* name spawned at the impact point on removal.</summary>
    public string ImpactEffect = "EXPLOSION_SMALL";

    /// <summary>Sound sample (or registered name) played at the impact point on removal.</summary>
    public string? ImpactSound;

    private float _age;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _age += dt;

        // No collision — straight-line flight; the renderer follows Projectile.Origin each frame.
        Projectile.Origin += Projectile.Velocity * dt;
        Client.OnEntityUpdate(Projectile);

        if (_age >= LifeSeconds)
        {
            Client.Projectiles.OnRemove(Projectile.Index, Projectile.Origin, ImpactEffect);
            if (!string.IsNullOrEmpty(ImpactSound))
                Client.OnSound(ImpactSound!, Projectile.Origin);
            QueueFree();
        }
    }
}
