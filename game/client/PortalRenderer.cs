using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;  // WarpzoneTrace, WarpzoneManager, Warpzone, WarpzoneTransform
using XonoticGodot.Common.Services;  // Api
using NVec3 = System.Numerics.Vector3;
// Coords (Quake<->Godot axis swap) lives in the parent XonoticGodot.Game namespace — reachable implicitly.

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side warpzone PORTAL render — the C# stand-in for DarkPlaces' engine <c>r_water</c> portal pass
/// (Base renders <c>dpcamera</c> surfaces in the engine via a <c>setcamera_transform</c> callback that warps the
/// view through the SAME <c>WarpZone_TransformOrigin/Velocity</c> the teleport uses; Godot has no equivalent, so
/// we do it with a <see cref="SubViewport"/> per portal window).
///
/// <para>For each portal "window" mesh that <see cref="MapLoader.BuildPortalSurfaces"/> emitted (only true
/// <c>dpcamera</c> shaders — e.g. <c>effects_warpzone/wavy</c> — the rim/backdrop decor stays in the normal map
/// mesh), this matches it to a linked <see cref="Warpzone"/> in <see cref="WarpzoneTrace.AmbientManager"/>, then
/// creates a SubViewport that renders the SHARED live <see cref="World3D"/> from a second camera placed at the
/// warp-transformed main-camera pose. A screen-UV <see cref="ShaderMaterial"/> on the surface samples that
/// SubViewport texture — the standard planar-portal trick, exact when the portal camera carries the main
/// camera's projection (FOV/aspect/keep-aspect synced per frame).</para>
///
/// <para>Two things keep the exit view CLEAN (both were missing in the first cut, which is why the projection
/// showed portal-in-portal feedback / the exit zone's own backdrop instead of the exit room):</para>
/// <list type="bullet">
///   <item><b>Portal-surface cull layer</b> — every portal window mesh is ALSO placed on the dedicated render
///   layer <see cref="PortalSurfaceLayerBit"/>, and every portal camera EXCLUDES that layer. The warp-transformed
///   camera sits BEHIND the exit plane, i.e. inside the exit zone's own box, staring straight through the exit's
///   own window quad — without the exclusion it renders that quad's (one frame stale) portal texture back into
///   itself. Excluding window quads from portal cameras also makes a distant portal seen THROUGH a portal show
///   its authored backdrop — exactly DP's no-recursion fallback.</item>
///   <item><b>Conservative near-clip at the exit window</b> — everything between the portal camera and the exit
///   plane (the exit zone's box interior/backdrop) must not occlude. Godot has no oblique-plane projection, so
///   per frame the camera near is pushed to the closest exit-window corner's forward distance: any point visible
///   THROUGH the window lies beyond its own window crossing, whose camera-space depth is ≥ the nearest corner's
///   (depth is linear over the planar window, so the min over the quad is attained at a corner).</item>
/// </list>
///
/// <para><b>Listen-host only</b> (the warpzone link transforms live in <see cref="WarpzoneTrace.AmbientManager"/>,
/// which a pure remote/dedicated-server client does not have — networking them is a follow-up). When the manager
/// is absent, or a surface matches no zone, the surface keeps the dark-mirror placeholder material (no
/// regression). Gated by <c>cl_portal_render</c> (default 1). Caps the active portal count to bound the per-frame
/// extra scene renders; a window is only re-rendered while its surface is on screen
/// (<see cref="VisibleOnScreenNotifier3D"/>) AND the main camera is on the FRONT side of its plane.</para>
/// </summary>
public partial class PortalRenderer : Node3D
{
    private const int MaxPortals = 6; // cap the per-frame extra scene renders

    /// <summary>Render layer 20 — portal window quads live here (in ADDITION to layer 1) so portal cameras can
    /// exclude them (no portal-in-portal feedback; the exit's own coplanar window quad never occludes its view).
    /// The main camera's default cull mask includes layer 20, so the windows stay visible to the player.</summary>
    private const uint PortalSurfaceLayerBit = 1u << 19;

    /// <summary>The ACTIVE portals' exit-side viewpoints (Quake) — one point 8qu in front of each rendering
    /// portal's exit window. The PVS cullers (<see cref="WorldPvsCuller"/> / ClientWorld's entity cull) UNION
    /// these with the main camera's cluster: they hide nodes via <c>Visible=false</c>, which applies to EVERY
    /// viewport sharing the World3D — without the union the exit room's cells are hidden for the portal camera
    /// too (the main camera can't see them) and the portal renders BLACK. Anything visible THROUGH the window
    /// is covered by the window-point cluster (every through-window sightline crosses the window plane).
    /// Rebuilt every frame by the live renderer; empty when no portal is rendering (menus, remote clients).</summary>
    public static readonly List<NVec3> ActiveExitViewsQuake = new();

    private sealed class Portal
    {
        public MeshInstance3D Surface = null!;
        public WarpzoneTransform Transform;
        public SubViewport Viewport = null!;
        public Camera3D Cam = null!;
        public ShaderMaterial Material = null!;
        public VisibleOnScreenNotifier3D Notifier = null!;
        public NVec3 InOriginQ, InForwardQ;        // the matched zone's IN plane (Quake) — the facing gate
        public Vector3[] ExitCorners = null!;      // exit-side window corners (Godot) — the near-clip bound
        public NVec3 ExitViewQuake;                // exit window center +8qu out — the PVS-union viewpoint
    }

    private readonly List<Portal> _portals = new();
    private Camera3D? _mainCamera;
    private bool _built;

    /// <summary>The shared portal-window shader: unshaded, two-sided, samples the SubViewport at the fragment's
    /// MAIN-viewport screen UV so the surface shows exactly the slice of the exit view that lines up with where
    /// the surface sits on screen (the standard planar-portal trick).</summary>
    private const string PortalShader =
        "shader_type spatial;\n" +
        "render_mode unshaded, cull_disabled, depth_draw_opaque;\n" +
        "uniform sampler2D portal_tex : source_color, filter_linear;\n" +
        "void fragment() {\n" +
        "    ALBEDO = texture(portal_tex, SCREEN_UV).rgb;\n" +
        "}\n";

    /// <summary>Wire the renderer: the map root (whose "Portals" child holds the window meshes) and the live
    /// first-person camera. Builds the per-portal SubViewports once; safe to call when there are no portals.</summary>
    public void Setup(Node3D mapRoot, Camera3D mainCamera)
    {
        _mainCamera = mainCamera;
        if (_built || mapRoot is null || mainCamera is null)
            return;
        _built = true;

        if (Api.Services is null || !PortalRenderEnabled())
            return; // disabled → portal surfaces keep their placeholder material

        WarpzoneManager? zones = WarpzoneTrace.AmbientManager;
        if (zones is null)
            return; // pure remote/dedicated client: no zone transforms (listen-host-only feature)

        Node? portalsRoot = mapRoot.GetNodeOrNull("Portals");
        if (portalsRoot is null)
            return; // map has no warpzone surfaces

        Viewport? mainVp = GetViewport();
        Vector2I size = mainVp is not null ? (Vector2I)mainVp.GetVisibleRect().Size : new Vector2I(1280, 720);

        foreach (Node child in portalsRoot.GetChildren())
        {
            if (child is not MeshInstance3D surface || !surface.HasMeta("wz_origin"))
                continue;
            if (_portals.Count >= MaxPortals)
            {
                GD.Print($"[PortalRenderer] portal cap {MaxPortals} reached — remaining surfaces keep the placeholder");
                break;
            }

            // Surface plane in QUAKE space (stored raw in the Vector3 metas by MapLoader).
            Vector3 og = (Vector3)surface.GetMeta("wz_origin");
            Vector3 ng = (Vector3)surface.GetMeta("wz_normal");
            NVec3 surfOrigin = new(og.X, og.Y, og.Z);
            NVec3 surfNormal = new(ng.X, ng.Y, ng.Z);

            if (!TryMatchZone(zones, surfOrigin, surfNormal, out WarpzoneTransform t))
                continue; // no linked zone for this surface — keep the placeholder

            // SubViewport rendering the SAME live world from the portal camera (mirrors GpuWarmPass's share path).
            var vp = new SubViewport
            {
                Name = $"PortalView_{_portals.Count}",
                Size = size,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            if (mainVp is not null)
            {
                vp.World3D = mainVp.World3D;     // share the live scene (map, players, effects, sun)
                vp.Msaa3D = mainVp.Msaa3D;
                vp.ScreenSpaceAA = mainVp.ScreenSpaceAA;
                vp.UseTaa = mainVp.UseTaa;
            }
            else
            {
                vp.OwnWorld3D = true;
            }
            AddChild(vp);

            // The portal camera must NEVER draw portal window quads (PortalSurfaceLayerBit exclusion — see the
            // class doc): it sits behind the exit plane looking straight through the exit zone's own window.
            var cam = new Camera3D
            {
                Name = "PortalCam",
                Current = true,
                Near = mainCamera.Near,
                Far = mainCamera.Far,
                CullMask = mainCamera.CullMask & ~PortalSurfaceLayerBit,
            };
            vp.AddChild(cam);

            var shader = new Shader { Code = PortalShader };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("portal_tex", vp.GetTexture());
            surface.MaterialOverride = mat;
            surface.Layers |= PortalSurfaceLayerBit; // visible to the main camera, excluded from portal cameras

            // Exit-side window corners (Godot): the entry window's AABB corners warped through the zone — the
            // portal opening as seen from the exit side, bounding the per-frame conservative near-clip.
            Aabb local = surface.GetAabb();
            var exit = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 cornerG = surface.GlobalTransform * local.GetEndpoint(i);
                exit[i] = Coords.ToGodot(t.TransformOrigin(Coords.ToQuake(cornerG)));
            }

            // On-screen gate: only re-render the exit view while the window itself is visible to the main camera
            // (the notifier also honors occlusion culling, so a wall-hidden portal costs nothing).
            var notifier = new VisibleOnScreenNotifier3D { Aabb = local };
            surface.AddChild(notifier);

            _portals.Add(new Portal
            {
                Surface = surface, Transform = t, Viewport = vp, Cam = cam, Material = mat,
                Notifier = notifier, InOriginQ = t.InOrigin, InForwardQ = t.InForward, ExitCorners = exit,
                ExitViewQuake = t.OutOrigin + t.OutForward * 8f, // just inside the exit room's airspace
            });
        }

        if (_portals.Count > 0)
            GD.Print($"[PortalRenderer] {_portals.Count} live portal(s)");
    }

    /// <summary><c>cl_portal_render</c> — default ON (unset/empty enables it); explicit <c>0</c> falls back to the
    /// dark-mirror placeholder.</summary>
    private static bool PortalRenderEnabled()
    {
        if (Api.Services is null)
            return false;
        string s = Api.Cvars.GetString("cl_portal_render");
        return string.IsNullOrWhiteSpace(s) || Api.Cvars.GetFloat("cl_portal_render") != 0f;
    }

    public override void _ExitTree()
    {
        // Drop the published exit viewpoints with the renderer (a stale entry would feed the NEXT map's PVS
        // cullers clusters from the WRONG tree).
        ActiveExitViewsQuake.Clear();
    }

    public override void _Process(double delta)
    {
        // Rebuilt every frame: only portals that actually render this frame contribute a PVS-union viewpoint.
        ActiveExitViewsQuake.Clear();
        if (_portals.Count == 0 || _mainCamera is null)
            return;

        Camera3D main = _mainCamera;
        // Main camera pose → Quake. Camera looks down -Z (Godot); right = +X, up = +Y. NetGame.UpdateCamera has
        // already posed the camera this frame (parent _Process runs before this child's), so there is no lag.
        NVec3 camPosQ = Coords.ToQuake(main.GlobalPosition);
        NVec3 fwdQ = Coords.ToQuake(-main.GlobalBasis.Z);
        NVec3 rightQ = Coords.ToQuake(main.GlobalBasis.X);
        NVec3 upQ = Coords.ToQuake(main.GlobalBasis.Y);

        // Track the live main-viewport size so the portal projection stays aspect-identical after a window
        // resize (a mismatched aspect shifts/scales the screen-UV sampled image).
        Vector2I wanted = GetViewport() is { } mainVp ? (Vector2I)mainVp.GetVisibleRect().Size : Vector2I.Zero;

        foreach (Portal p in _portals)
        {
            // Render gate: the window must be on screen AND the main camera on the FRONT side of the IN plane
            // (from behind you see the window's back face, which shows whatever the texture last held; once the
            // eye actually crosses, WarpzoneFixView owns the whole screen). Disabled viewports keep their last
            // texture — a one-frame-stale image on re-entry, imperceptible.
            bool facing = NVec3.Dot(camPosQ - p.InOriginQ, p.InForwardQ) > 0f;
            bool visible = facing && p.Notifier.IsOnScreen();
            p.Viewport.RenderTargetUpdateMode = visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (!visible)
                continue;

            ActiveExitViewsQuake.Add(p.ExitViewQuake); // PVS-union viewpoint for the cullers (see the field doc)

            if (wanted.X > 0 && p.Viewport.Size != wanted)
                p.Viewport.Size = wanted;

            WarpzoneTransform t = p.Transform;
            // Place the portal camera at the warp-transformed main-camera pose (the exit view). Rotate() carries
            // the full basis (incl. roll) through the portal; TransformOrigin shifts the position.
            NVec3 pPos = t.TransformOrigin(camPosQ);
            NVec3 pFwd = t.Rotate(fwdQ);
            NVec3 pRight = t.Rotate(rightQ);
            NVec3 pUp = t.Rotate(upQ);

            p.Cam.GlobalBasis = new Basis(Coords.ToGodot(pRight), Coords.ToGodot(pUp), -Coords.ToGodot(pFwd));
            p.Cam.GlobalPosition = Coords.ToGodot(pPos);
            // The projection must match the main camera EXACTLY for the screen-UV trick to line up.
            p.Cam.Fov = main.Fov;
            p.Cam.KeepAspect = main.KeepAspect;
            p.Cam.Far = main.Far;

            // Conservative near-clip at the exit window (see the class doc): camera-space depth of the nearest
            // exit-window corner. Clips the exit zone's box interior (backdrop/rim) without oblique projection.
            Vector3 camG = p.Cam.GlobalPosition;
            Vector3 fwdG = -p.Cam.GlobalBasis.Z;
            float minDepth = float.MaxValue;
            for (int i = 0; i < p.ExitCorners.Length; i++)
            {
                float d = (p.ExitCorners[i] - camG).Dot(fwdG);
                if (d < minDepth) minDepth = d;
            }
            p.Cam.Near = Mathf.Clamp(minDepth + 0.01f, 0.05f, main.Far * 0.5f);
        }
    }

    /// <summary>Match a portal surface to the linked warpzone whose IN plane it is: the zone's IN origin must lie
    /// (near) the surface plane with an aligned normal — nearest such zone within a distance bound. (The old
    /// centroid-radius-only match bound the zone's interior BACKDROP face, 64qu behind the window, as a portal
    /// too.) Returns that zone's IN→OUT transform — the warp a portal camera applies to the main view.</summary>
    private static bool TryMatchZone(WarpzoneManager zones, NVec3 surfOrigin, NVec3 surfNormal, out WarpzoneTransform best)
    {
        best = default;
        float bestDist = 96f * 96f; // within 96 units of the surface centroid
        bool found = false;
        NVec3 sn = surfNormal.LengthSquared() > 1e-9f ? NVec3.Normalize(surfNormal) : surfNormal;
        foreach (Warpzone wz in zones.Zones)
        {
            if (!wz.Linked || !wz.Transform.Valid)
                continue;
            NVec3 inFwd = wz.Transform.InForward;
            float align = System.MathF.Abs(NVec3.Dot(sn, inFwd.LengthSquared() > 1e-9f ? NVec3.Normalize(inFwd) : inFwd));
            if (align < 0.7f)
                continue; // not the same plane orientation
            // The zone's IN plane must be (near-)coplanar with the surface — an offset parallel face (the zone
            // box's backdrop) is decor, not the window.
            if (System.MathF.Abs(NVec3.Dot(wz.Transform.InOrigin - surfOrigin, sn)) > 8f)
                continue;
            float d = (wz.Transform.InOrigin - surfOrigin).LengthSquared();
            if (d < bestDist)
            {
                bestDist = d;
                best = wz.Transform;
                found = true;
            }
        }
        return found;
    }
}
