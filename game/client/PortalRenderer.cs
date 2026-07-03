using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;  // WarpzoneTrace, WarpzoneManager, Warpzone, WarpzoneTransform
using XonoticGodot.Common.Services;  // Api
using NVec3 = System.Numerics.Vector3;
// Coords (Quake<->Godot axis swap) lives in the parent XonoticGodot.Game namespace — reachable implicitly.

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side warpzone PORTAL render — the C# stand-in for DarkPlaces' engine <c>r_water</c> portal pass
/// (Base renders <c>dpcamera</c> surfaces with a <c>setcamera_transform</c> callback + a true oblique clip at
/// the exit plane; Godot has neither, so we use a <see cref="SubViewport"/> per portal window).
///
/// <para><b>Technique: the window-anchored off-axis frustum</b> (the classic planar-portal/mirror method —
/// strictly better here than the screen-UV "warped clone" this replaced). Per frame, the portal camera is
/// positioned at the WARP-TRANSFORMED main-camera eye (correct parallax), but its ORIENTATION is fixed
/// perpendicular to the exit plane, and <see cref="Camera3D.SetFrustum"/> pins the frustum's near rectangle to
/// be EXACTLY the exit window:</para>
/// <list type="bullet">
///   <item>the near plane lies ON the portal plane → a true oblique-equivalent clip: nothing behind or beside
///   the exit plane (wall pockets, the map exterior, other rooms) can pollute the view at ANY angle/distance —
///   the failure of the previous screen-UV approach, whose warped camera drifted outside the map shell and
///   whose perpendicular near plane provably could not exclude the pocket at grazing angles;</item>
///   <item>the rendered image maps 1:1 onto the window rectangle, so the quad samples it by its own in-plane
///   position (no screen-UV alignment/aspect coupling with the main viewport at all);</item>
///   <item>the frustum tightly bounds the through-window volume — the SubViewport renders only what can
///   actually be seen through the portal (cheaper than a full warped view).</item>
/// </list>
///
/// <para>Windows come from <see cref="MapLoader.BuildPortalSurfaces"/> (only true <c>dpcamera</c> shaders; the
/// pocket decor — backdrop/rims — is layered off portal cameras, and all portal windows live on
/// <see cref="PortalSurfaceLayerBit"/> which portal cameras exclude: no portal-in-portal feedback, a distant
/// portal seen through a portal shows its authored backdrop, DP's no-recursion fallback). Each window matches a
/// linked <see cref="Warpzone"/> in <see cref="WarpzoneTrace.AmbientManager"/> by plane coincidence.</para>
///
/// <para><b>Listen-host only</b> (the zone transforms live in <see cref="WarpzoneTrace.AmbientManager"/>, which
/// a pure remote/dedicated-server client does not have — networking them is a follow-up). When the manager is
/// absent, or a surface matches no zone, the surface keeps the dark-mirror placeholder (no regression). Gated by
/// <c>cl_portal_render</c> (default 1). A window is only re-rendered while its surface is on screen
/// (<see cref="VisibleOnScreenNotifier3D"/>) AND the main camera is on the FRONT side of its plane.</para>
///
/// <para><b>Debug</b>: <c>sv_warpzone_trace 1</c> logs a 1Hz gate/pose line per portal; <c>wz_portal_dump 1</c>
/// additionally saves each portal texture + the main view to <c>screenshots/portal-debug/</c>;
/// <c>wz_portal_force 1</c> bypasses the visibility gates for scripted runs.</para>
/// </summary>
public partial class PortalRenderer : Node3D
{
    private const int MaxPortals = 6; // cap the per-frame extra scene renders

    /// <summary>Render layer 20 — portal window quads live here (in ADDITION to layer 1) so portal cameras can
    /// exclude them (no portal-in-portal feedback). The pocket DECOR nodes join the same layer. The main
    /// camera's default cull mask includes layer 20, so everything stays visible to the player.</summary>
    private const uint PortalSurfaceLayerBit = 1u << 19;

    /// <summary>The ACTIVE portals' exit-side viewpoints (Quake) — one point 8qu in front of each rendering
    /// portal's exit window. The PVS cullers (<see cref="WorldPvsCuller"/> / ClientWorld's entity cull) UNION
    /// these with the main camera's cluster: they hide nodes via <c>Visible=false</c>, which applies to EVERY
    /// viewport sharing the World3D — without the union the exit room's cells are hidden for the portal camera
    /// (the main camera can't see them) and the portal renders black. Anything visible THROUGH the window is
    /// covered by the window-point cluster (every through-window sightline crosses the window plane).
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
        public NVec3 InOriginQ, InForwardQ;   // the matched zone's IN plane (Quake) — the facing gate
        public NVec3 ExitCenterQ;             // the window center mapped onto the EXIT plane (Quake)
        public NVec3 OutFwdQ, OutRightQ, OutUpQ; // the exit plane basis (Quake) — the fixed camera orientation
        public float HalfR, HalfU;            // window half-extents along OutRight/OutUp (world units)
        public NVec3 ExitViewQuake;           // exit window center +8qu out — the PVS-union viewpoint
        public Basis ExitBasisG;              // precomputed Godot camera basis (fixed per portal)
        public float NextResizeOk;            // adaptive-resolution hysteresis clock (wall seconds)
    }

    private readonly List<Portal> _portals = new();
    private Camera3D? _mainCamera;
    private bool _built;
    private float _lastTrace = -1f;   // 1Hz gate-trace clock (sv_warpzone_trace)

    /// <summary>wz_portal_scan (debug, one-shot): list every MeshInstance3D in the tree whose world AABB
    /// intersects the first portal's window region — identifies WHICH node actually draws there.</summary>
    private bool _scanned;
    private void ScanWindowRegion(Portal p)
    {
        if (_scanned) return;
        _scanned = true;
        Vector3 c = Coords.ToGodot(p.InOriginQ);
        var probe = new Aabb(c - new Vector3(70, 70, 70), new Vector3(140, 140, 140));
        var stack = new Stack<Node>();
        stack.Push(GetTree().Root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            foreach (Node ch in n.GetChildren()) stack.Push(ch);
            if (n is MeshInstance3D mi && GodotObject.IsInstanceValid(mi))
            {
                Aabb world = mi.GlobalTransform * mi.GetAabb();
                if (!world.Intersects(probe)) continue;
                string mat = mi.MaterialOverride is not null ? mi.MaterialOverride.GetType().Name + "(override)"
                    : (mi.Mesh is not null && mi.Mesh.GetSurfaceCount() > 0 && mi.Mesh.SurfaceGetMaterial(0) is { } m0
                        ? m0.GetType().Name : "none");
            GD.Print($"[portal-scan] '{mi.GetPath()}' visible={mi.IsVisibleInTree()} layers={mi.Layers:X} "
                    + $"aabb={world.Position}+{world.Size} mat={mat}");
            }
        }
    }

    /// <summary>wz_portal_dump: save this portal's viewport image (+ the main view once per sweep) to
    /// screenshots/portal-debug/ — always-latest filenames, no accumulation — and log the portal camera's pose
    /// so the exit-camera's actual view can be inspected offline.</summary>
    private void DumpDebugImages(Portal p)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("res://screenshots/portal-debug");
            System.IO.Directory.CreateDirectory(dir);
            int idx = _portals.IndexOf(p);
            p.Viewport.GetTexture().GetImage().SavePng($"{dir}/portal{idx}.png");
            if (idx == 0 && GetViewport() is { } mv)
                mv.GetTexture().GetImage().SavePng($"{dir}/main.png");
            GD.Print($"[portal] dump portal{idx}: camG={p.Cam.GlobalPosition} near={p.Cam.Near:F2} "
                + $"size={p.Viewport.Size} halfR={p.HalfR:F0} halfU={p.HalfU:F0} exitC={p.ExitCenterQ}");
        }
        catch (System.Exception e)
        {
            GD.Print($"[portal] dump failed: {e.Message}");
        }
    }

    /// <summary>The portal-window shader: unshaded, two-sided, samples the exit image by the fragment's
    /// IN-PLANE position on the window rectangle (the frustum's near rect IS the exit window, so the texture
    /// maps 1:1 — no screen-UV coupling). The horizontal axis mirrors through the seam (the warp maps
    /// inRight → −outRight), folded into the U formula. <c>source_color</c> IS required: the SubViewport's
    /// render target stores tonemapped, sRGB-ENCODED data — the hint's sRGB→linear decode plus the main
    /// viewport's re-encode is the identity round trip. Sampling raw double-encodes and washes the exit view
    /// out too bright (user-verified both ways; the earlier "too dark" that prompted removing the hint was
    /// actually the opaque blueedge rim covering the window).</summary>
    private const string PortalShader =
        "shader_type spatial;\n" +
        "render_mode unshaded, cull_disabled, depth_draw_opaque;\n" +
        "uniform sampler2D portal_tex : source_color, filter_linear;\n" +
        "uniform vec3 wz_center;\n" +   // entry window center (Godot world)
        "uniform vec3 wz_right;\n" +    // entry plane right (unit, Godot world)
        "uniform vec3 wz_up;\n" +       // entry plane up (unit, Godot world)
        "uniform vec2 wz_half;\n" +     // window half extents (right, up)
        "uniform float wz_uvtest = 0.0;\n" +   // debug: paint the computed UV as color (red=u, green=v)
        "varying vec3 wpos;\n" +
        "void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }\n" +
        "void fragment() {\n" +
        "    vec3 d = wpos - wz_center;\n" +
        "    float a = dot(d, wz_right);\n" +
        "    float b = dot(d, wz_up);\n" +
        "    vec2 uv = vec2(0.5 - a / (2.0 * wz_half.x), 0.5 - b / (2.0 * wz_half.y));\n" +
        "    if (wz_uvtest > 1.5) { ALBEDO = vec3(1.0, 0.0, 1.0); }\n" +           // 2 = solid magenta (which-quad probe)
        "    else if (wz_uvtest > 0.5) { ALBEDO = vec3(uv, 0.0); } else {\n" +      // 1 = paint UVs
        "    ALBEDO = texture(portal_tex, uv).rgb; }\n" +
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

        foreach (Node child in portalsRoot.GetChildren())
        {
            if (child is not MeshInstance3D surface)
                continue;
            // Warpzone pocket DECOR (the authored backdrop + rims, see MapLoader.BuildPortalSurfaces): visible
            // to the player as authored, EXCLUDED from every portal camera (they sit inside the pocket volume
            // the frustum's near plane grazes).
            if (surface.HasMeta("wz_decor"))
            {
                surface.Layers |= PortalSurfaceLayerBit;
                continue;
            }
            if (!surface.HasMeta("wz_origin"))
                continue;
            if (_portals.Count >= MaxPortals)
            {
                GD.Print($"[PortalRenderer] portal cap {MaxPortals} reached — remaining surfaces keep the placeholder");
                break;
            }

            // Surface plane in QUAKE space (stored raw in the Vector3 metas by MapLoader).
            Vector3 og = (Vector3)surface.GetMeta("wz_origin");
            NVec3 surfOrigin = new(og.X, og.Y, og.Z);
            Vector3 ng = (Vector3)surface.GetMeta("wz_normal");
            NVec3 surfNormal = new(ng.X, ng.Y, ng.Z);

            if (!TryMatchZone(zones, surfOrigin, surfNormal, out WarpzoneTransform t))
                continue; // no linked zone for this surface — keep the placeholder

            // The window rectangle on the EXIT plane: warp the entry mesh's corners through the zone and
            // measure their spread along the exit plane's in-plane basis. (A rotation preserves extents, so
            // these also serve the entry-side shader mapping.)
            NVec3 exitCenter = t.TransformOrigin(surfOrigin);
            Aabb local = surface.GetAabb();
            float halfR = 1f, halfU = 1f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 cornerG = surface.GlobalTransform * local.GetEndpoint(i);
                NVec3 cQ = t.TransformOrigin(Coords.ToQuake(cornerG)) - exitCenter;
                halfR = Mathf.Max(halfR, Mathf.Abs(NVec3.Dot(cQ, t.OutRight)));
                halfU = Mathf.Max(halfU, Mathf.Abs(NVec3.Dot(cQ, t.OutUp)));
            }

            // Render-target size: fixed height, width from the WINDOW's aspect (the frustum near rect is the
            // window, so the image aspect must match the window, not the screen).
            var size = new Vector2I(Mathf.Clamp(Mathf.RoundToInt(384f * halfR / halfU), 8, 2048), 384);

            var vp = new SubViewport
            {
                Name = $"PortalView_{_portals.Count}",
                Size = size,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                // The portal camera hugs the exit plane from behind: occlusion culling would cull the whole
                // room whenever a config enables it (the camera is inside the wall volume). The frustum clip
                // does the real work.
                UseOcclusionCulling = false,
            };
            if (mainVp is not null)
            {
                vp.World3D = mainVp.World3D;     // share the live scene (map, players, effects, sun)
                vp.Msaa3D = mainVp.Msaa3D;
                vp.ScreenSpaceAA = mainVp.ScreenSpaceAA;
            }
            else
            {
                vp.OwnWorld3D = true;
            }
            AddChild(vp);

            // Fixed orientation: perpendicular to the exit plane, looking INTO the exit room. Basis columns =
            // (right, up, -forward) in Godot space.
            var basis = new Basis(
                Coords.ToGodot(t.OutRight), Coords.ToGodot(t.OutUp), -Coords.ToGodot(t.OutForward));
            var cam = new Camera3D
            {
                Name = "PortalCam",
                Current = true,
                CullMask = mainCamera.CullMask & ~PortalSurfaceLayerBit,
            };
            vp.AddChild(cam);
            cam.GlobalBasis = basis;

            var shader = new Shader { Code = PortalShader };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("portal_tex", vp.GetTexture());
            mat.SetShaderParameter("wz_center", Coords.ToGodot(surfOrigin));
            mat.SetShaderParameter("wz_right", Coords.ToGodot(t.InRight));
            mat.SetShaderParameter("wz_up", Coords.ToGodot(t.InUp));
            mat.SetShaderParameter("wz_half", new Vector2(halfR, halfU));
            surface.MaterialOverride = mat;
            surface.Layers |= PortalSurfaceLayerBit; // visible to the main camera, excluded from portal cameras

            // On-screen gate: only re-render the exit view while the window itself is visible to the main
            // camera. The window mesh is a FLAT quad — grow the notifier box so the culling test never sees a
            // degenerate AABB.
            var notifier = new VisibleOnScreenNotifier3D { Aabb = local.Grow(0.5f) };
            surface.AddChild(notifier);

            _portals.Add(new Portal
            {
                Surface = surface, Transform = t, Viewport = vp, Cam = cam, Material = mat, Notifier = notifier,
                InOriginQ = t.InOrigin, InForwardQ = t.InForward,
                ExitCenterQ = exitCenter, OutFwdQ = t.OutForward, OutRightQ = t.OutRight, OutUpQ = t.OutUp,
                HalfR = halfR, HalfU = halfU, ExitBasisG = basis,
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

    /// <summary><c>cl_portal_resolution</c> — scale on the adaptive portal render resolution (default/unset 1.0;
    /// 0.5 = half detail for weak GPUs, 2 = supersample). Clamped to a sane range.</summary>
    private static float PortalResolutionScale()
    {
        if (Api.Services is null)
            return 1f;
        string s = Api.Cvars.GetString("cl_portal_resolution");
        if (string.IsNullOrWhiteSpace(s))
            return 1f;
        return Mathf.Clamp(Api.Cvars.GetFloat("cl_portal_resolution"), 0.25f, 2f);
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
        // NetGame.UpdateCamera has already posed the camera this frame (parent _Process runs before this
        // child's), so there is no lag.
        NVec3 camPosQ = Coords.ToQuake(main.GlobalPosition);

        // 1Hz gate trace (sv_warpzone_trace); wz_portal_force bypasses the gates for scripted debug runs;
        // wz_portal_dump saves the portal + main images on each trace tick.
        bool trace = false;
        if (Api.Services is not null && Api.Cvars.GetFloat("sv_warpzone_trace") != 0f)
        {
            float nowS = Time.GetTicksMsec() * 0.001f;
            if (nowS - _lastTrace >= 1f) { _lastTrace = nowS; trace = true; }
        }
        bool force = Api.Services is not null && Api.Cvars.GetFloat("wz_portal_force") != 0f;
        bool dump = trace && Api.Services is not null && Api.Cvars.GetFloat("wz_portal_dump") != 0f;

        // wz_portal_lookat 1 (debug): park the MAIN camera head-on in front of the FIRST portal's window every
        // frame (after NetGame posed it), so scripted dump runs capture the on-quad result deterministically.
        if (_portals.Count > 0 && Api.Services is not null && Api.Cvars.GetFloat("wz_portal_lookat") != 0f)
        {
            Portal t0 = _portals[0];
            NVec3 eye = t0.InOriginQ + t0.InForwardQ * 90f + new NVec3(0f, 0f, 8f);
            NVec3 fwd = -t0.InForwardQ;
            NVec3 up = new(0f, 0f, 1f);
            NVec3 right = NVec3.Normalize(NVec3.Cross(fwd, up));
            main.GlobalBasis = new Basis(Coords.ToGodot(right), Coords.ToGodot(up), -Coords.ToGodot(fwd));
            main.GlobalPosition = Coords.ToGodot(eye);
            camPosQ = eye;
        }

        foreach (Portal p in _portals)
        {
            // Render gate: the window must be on screen AND the main camera on the FRONT side of the IN plane
            // (from behind you see the window's back face with whatever the texture last held; once the eye
            // actually crosses, WarpzoneFixView owns the whole screen). Disabled viewports keep their last
            // texture — a one-frame-stale image on re-entry, imperceptible.
            bool facing = NVec3.Dot(camPosQ - p.InOriginQ, p.InForwardQ) > 0f;
            bool onScreen = p.Notifier.IsOnScreen();
            bool visible = (facing && onScreen) || force;
            p.Viewport.RenderTargetUpdateMode = visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (trace)
            {
                GD.Print($"[portal] '{p.Surface.Name}' facing={facing} onScreen={onScreen} -> {(visible ? "RENDER" : "off")}"
                    + $" near={p.Cam.Near:F2} size={p.Viewport.Size} camQ={camPosQ} inO={p.InOriginQ} inF={p.InForwardQ}");
                // debug: paint the quad's computed UVs (1 → red=u, green=v; 2 → solid magenta) instead of the
                // texture. Read per frame — a Setup-time read can precede the cvar-store bridge sync.
                float uvtest = Api.Services is not null ? Api.Cvars.GetFloat("wz_portal_uvtest") : 0f;
                p.Material.SetShaderParameter("wz_uvtest", uvtest);
                if (uvtest != 0f)
                    GD.Print($"[portal] uvtest={uvtest} applied to '{p.Surface.Name}' mat={p.Surface.MaterialOverride == p.Material}");
            }
            if (dump && visible)
            {
                DumpDebugImages(p);
                ScanWindowRegion(_portals[0]);
            }
            if (!visible)
                continue;

            ActiveExitViewsQuake.Add(p.ExitViewQuake); // PVS-union viewpoint for the cullers (see the field doc)

            // Window-anchored frustum: eye at the warped main-camera position; orientation fixed perpendicular
            // to the exit plane; the frustum's near rectangle pinned to the exit window. near = perpendicular
            // distance from the eye to the exit plane (the eye is BEHIND it while the viewer is in front of the
            // entry — the facing gate guarantees that); offset = the window center's lateral offset from the
            // camera axis in camera-local X/Y (camera X == OutRight, Y == OutUp, world units carry 1:1).
            NVec3 pPos = p.Transform.TransformOrigin(camPosQ);
            // Perpendicular distance from the (behind-plane) eye to the exit plane. In the last couple of units
            // before the crossing the frustum's shear (lateral offset ÷ near) explodes and Godot's light culler
            // rejects the camera ("prepare_camera: Condition !res" spam, lights fall back unculled) — freeze the
            // viewport instead: the 1-2 frame-old image fills the screen at that range, and WarpzoneFixView owns
            // the view the moment the eye crosses.
            float planeDist = NVec3.Dot(p.ExitCenterQ - pPos, p.OutFwdQ);
            if (planeDist < 2f)
            {
                p.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                continue;
            }
            var offset = new Vector2(
                NVec3.Dot(p.ExitCenterQ - pPos, p.OutRightQ),
                NVec3.Dot(p.ExitCenterQ - pPos, p.OutUpQ));

            // ADAPTIVE RESOLUTION: render the exit view at (roughly) the window's projected on-screen pixel
            // height — sharp when the player is close, cheap when it is far. planeDist doubles as the viewer's
            // distance to the ENTRY window (rotations preserve it). Quantized to 128px buckets with a cooldown so
            // the render target isn't reallocated every frame, scaled by cl_portal_resolution (default 1).
            if (GetViewport() is { } mvp)
            {
                float screenH = mvp.GetVisibleRect().Size.Y;
                float projPx = screenH * (2f * p.HalfU / planeDist)
                    / (2f * Mathf.Tan(Mathf.DegToRad(main.Fov) * 0.5f));
                float resScale = PortalResolutionScale();
                int bucket = Mathf.Clamp(Mathf.CeilToInt(projPx * resScale / 128f), 1, 8) * 128;
                float nowR = Time.GetTicksMsec() * 0.001f;
                if (bucket != p.Viewport.Size.Y && nowR >= p.NextResizeOk)
                {
                    p.NextResizeOk = nowR + 0.3f;
                    p.Viewport.Size = new Vector2I(
                        Mathf.Clamp(Mathf.RoundToInt(bucket * p.HalfR / p.HalfU), 8, 2048), bucket);
                }
            }

            // The frustum's ray cone is defined by the WINDOW rectangle at planeDist. The actual clip plane sits
            // a hair PAST the exit plane (the warpzone surface is painted on a SOLID wall — a world face is
            // COINCIDENT with the plane, and near == planeDist z-fights it into garbage/black; verified live).
            // Everything on the cone scales linearly with distance, so the near rect is the window scaled by
            // near/planeDist — same projection, clip 0.5qu into the room.
            float nearClip = planeDist + 0.5f;
            float s = nearClip / planeDist;

            p.Cam.GlobalBasis = p.ExitBasisG;
            p.Cam.GlobalPosition = Coords.ToGodot(pPos);
            p.Cam.SetFrustum(p.HalfU * 2f * s, offset * s, nearClip, main.Far);
        }
    }

    /// <summary>Match a portal surface to the linked warpzone whose IN plane it is: the zone's IN origin must lie
    /// (near) the surface plane with an aligned normal — nearest such zone within a distance bound. Returns that
    /// zone's IN→OUT transform — the warp a portal camera applies to the main view.</summary>
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
