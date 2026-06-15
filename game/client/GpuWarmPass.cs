using System;
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

    /// <summary>
    /// (§12.6) Warm arbitrary already-built nodes by rendering them offscreen for a few frames, then invoke
    /// <paramref name="onDone"/> (which owns their lifetime — the pass does NOT free them; they're removed
    /// from the warm viewport first). Used by the idle model warmer: a built-then-immediately-freed model
    /// never rendered, so its material variants' pipelines never compiled — and the FIRST player wearing
    /// that model on screen paid the compile (`pipe +N` mid-fight). Headless: onDone runs immediately.
    /// </summary>
    public static GpuWarmPass WarmNodes(Node parent, List<Node3D> nodes, Action onDone)
    {
        var pass = new GpuWarmPass { Name = "GpuWarmNodes", _onDone = onDone, _returnInstances = true };
        if (DisplayServer.GetName() != "headless")
            pass._instances.AddRange(nodes);
        parent.AddChild(pass);
        return pass;
    }

    private Action? _onDone;
    private bool _returnInstances;

    public override void _Ready()
    {
        // Nothing to warm (no atlas mounted / headless) — drop immediately. A WarmNodes caller still gets
        // its completion callback (it owns the nodes' lifetime).
        if (_instances.Count == 0)
        {
            _onDone?.Invoke();
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
        // (hitch-fix 2026-06-15) A Vulkan graphics pipeline is keyed by its MULTISAMPLE state, among other things.
        // A SubViewport defaults to MSAA-disabled, but the main window viewport runs 4× MSAA (project.godot) — so
        // warming in a 1× viewport compiled the WRONG pipeline variant and the main viewport recompiled the 4×
        // variant on first draw anyway (mid-match PIPELINE-COMPILE hitches persisted despite warming). Match the
        // main viewport's MSAA / AA / scaling so the pipelines compiled here are the exact ones play will reuse.
        if (GetViewport() is { } mainVp)
        {
            vp.Msaa3D = mainVp.Msaa3D;
            vp.ScreenSpaceAA = mainVp.ScreenSpaceAA;
            vp.UseTaa = mainVp.UseTaa;
            vp.UseDebanding = mainVp.UseDebanding;
            vp.Scaling3DMode = mainVp.Scaling3DMode;
        }
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
        if (--_framesLeft > 0)
            return;
        XonoticGodot.Common.Diagnostics.Prof.Event($"warm: GPU warm pass done ({_instances.Count} instances)");
        if (_returnInstances)
        {
            // WarmNodes mode: hand the (now pipeline-warm) nodes back to the caller instead of freeing them.
            foreach (Node3D n in _instances)
                if (GodotObject.IsInstanceValid(n) && n.GetParent() is { } p)
                    p.RemoveChild(n);
            _onDone?.Invoke();
        }
        QueueFree(); // frees the SubViewport + camera (and, in the classic mode, every warm instance)
    }
}
