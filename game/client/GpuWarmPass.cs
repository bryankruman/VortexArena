using System.Collections.Generic;
using Godot;

namespace XonoticGodot.Game.Client;

/// <summary>
/// The offscreen GPU pipeline warm pass (PERFORMANCE_REPORT.md §1.1 Plan A2). Godot compiles a material's
/// shader + render pipeline lazily on its FIRST draw — so the first rocket / explosion / gib / projectile of a
/// match hitches while the GPU stalls compiling. This parents one hidden instance of every runtime-created
/// material family into a tiny isolated 64×64 <see cref="SubViewport"/> (its OWN <c>World3D</c>, so nothing
/// touches the main scene — no visible flash) and lets it render for a few frames, forcing those compiles up
/// front. The first real effect in play then hits a warm pipeline.
///
/// The warm instances reference the SAME cached materials/meshes real spawns use (the A1 resource caches in
/// <see cref="EffectSystem"/> / <see cref="ProjectileRenderer"/>), so a single render frame compiles the exact
/// pipelines play will need. Self-managing: it counts a few rendered frames (one to let the one-shot particles
/// emit + draw, a couple to flush) then frees itself — and with it the SubViewport and every warm instance. The
/// cached Resources survive (the live renderers hold their own references); only this pass's extra refs drop.
/// </summary>
public partial class GpuWarmPass : Node
{
    // Frames to keep the warm viewport alive: one for the one-shot particles to emit + first-draw (compile), the
    // rest to flush. Counted down in _Process (robust whether the load was synchronous or frame-yielding).
    private int _framesLeft = 4;
    private List<Node3D> _instances = new();

    /// <summary>
    /// Build + run a warm pass under <paramref name="parent"/>. Gathers warm instances from the effect and
    /// projectile renderers and renders them offscreen for a few frames. No-op-safe when a subsystem is null or
    /// has nothing to warm (a missing particle atlas / empty catalog simply yields fewer nodes). Call once at
    /// map load, AFTER <see cref="EffectSystem.Warmup"/> / <see cref="ProjectileRenderer.WarmupTrails"/> (which
    /// populate the resource caches this renders).
    /// </summary>
    public static GpuWarmPass Run(Node parent, EffectSystem? effects, ProjectileRenderer? projectiles)
    {
        var pass = new GpuWarmPass { Name = "GpuWarmPass" };
        // Headless (dedicated server / CI): there is no GPU and no pipelines to warm, and the dummy renderer
        // must not be handed a SubViewport render job. Gather nothing so _Ready drops the pass immediately.
        if (DisplayServer.GetName() != "headless")
        {
            // Gather the instances before entering the tree; building them is pure CPU (uses the populated caches).
            if (effects is not null) pass._instances.AddRange(effects.BuildWarmupInstances());
            if (projectiles is not null) pass._instances.AddRange(projectiles.BuildWarmupInstances());
        }
        parent.AddChild(pass);
        return pass;
    }

    public override void _Ready()
    {
        // Nothing to warm (no atlas mounted / headless) — drop immediately.
        if (_instances.Count == 0)
        {
            QueueFree();
            return;
        }

        // Isolated 64×64 viewport with its OWN World3D so the warm instances never render into the main scene.
        var vp = new SubViewport
        {
            Name = "WarmViewport",
            Size = new Vector2I(64, 64),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            OwnWorld3D = true,
        };
        AddChild(vp);

        // A camera looking at the origin (where the warm instances sit). Current is scoped to this SubViewport's
        // world, so it never steals the main window's camera.
        var cam = new Camera3D { Name = "WarmCam", Position = new Vector3(0f, 20f, 140f), Current = true };
        vp.AddChild(cam);
        cam.LookAt(Vector3.Zero, Vector3.Up);

        // Park every warm instance in front of the camera. One render frame compiles each referenced pipeline;
        // the one-shot particles (Explosiveness near 1) emit + draw within the first couple of frames.
        foreach (Node3D n in _instances)
            if (GodotObject.IsInstanceValid(n))
                vp.AddChild(n);
    }

    public override void _Process(double delta)
    {
        if (--_framesLeft <= 0)
            QueueFree(); // frees the SubViewport, camera, and every warm instance
    }
}
