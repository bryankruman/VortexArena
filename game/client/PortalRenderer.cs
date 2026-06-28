using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Gameplay;  // WarpzoneTrace, WarpzoneManager, Warpzone, WarpzoneTransform
using XonoticGodot.Common.Services;  // Api
using NVec3 = System.Numerics.Vector3;
// Coords (Quake<->Godot axis swap) lives in the parent XonoticGodot.Game namespace — reachable implicitly.

namespace XonoticGodot.Game.Client;

/// <summary>
/// The client-side warpzone PORTAL render — the C# stand-in for DarkPlaces' engine <c>r_water</c> portal pass
/// (Base renders warpzone surfaces in the engine via Q3 surfaceflags + a <c>setcamera_transform</c> callback;
/// Godot has no equivalent, so we do it with a <see cref="SubViewport"/> per portal surface).
///
/// <para>For each portal "window" mesh that <see cref="MapLoader.BuildPortalSurfaces"/> emitted (tagged with its
/// QUAKE plane via node metadata), this matches it to a linked <see cref="Warpzone"/> in
/// <see cref="WarpzoneTrace.AmbientManager"/> (the same manager the listen host's prediction already reads), then
/// creates a SubViewport that renders the SHARED live <see cref="World3D"/> from a second camera placed at the
/// warp-transformed main-camera pose. A screen-UV <see cref="ShaderMaterial"/> on the surface samples that
/// SubViewport texture, so the surface reads as a window into the linked exit. Each frame the portal camera is
/// re-derived from the main camera through the zone's <see cref="WarpzoneTransform"/> (full basis via
/// <see cref="WarpzoneTransform.Rotate"/>, so roll is preserved).</para>
///
/// <para><b>Listen-host only</b> (the warpzone link transforms live in <see cref="WarpzoneTrace.AmbientManager"/>,
/// which a pure remote/dedicated-server client does not have — networking them is a follow-up). When the manager
/// is absent, or a surface matches no zone, the surface keeps the dark-mirror placeholder material (no
/// regression). Gated by <c>cl_portal_render</c> (default 1). Caps the active portal count to bound the per-frame
/// extra scene renders.</para>
///
/// <para><b>Unverified in-engine.</b> The transform math is unit-tested (<c>WarpzonePortalTests</c>) but the
/// rendered result (handedness through the Quake↔Godot conversion, near-clip, recursion) needs an in-game eyeball;
/// set <c>cl_portal_render 0</c> to fall back to the placeholder.</para>
/// </summary>
public partial class PortalRenderer : Node3D
{
    private const int MaxPortals = 6; // cap the per-frame extra scene renders

    private sealed class Portal
    {
        public MeshInstance3D Surface = null!;
        public WarpzoneTransform Transform;
        public SubViewport Viewport = null!;
        public Camera3D Cam = null!;
        public ShaderMaterial Material = null!;
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

            var cam = new Camera3D { Name = "PortalCam", Current = true, Near = mainCamera.Near, Far = mainCamera.Far };
            vp.AddChild(cam);

            var shader = new Shader { Code = PortalShader };
            var mat = new ShaderMaterial { Shader = shader };
            mat.SetShaderParameter("portal_tex", vp.GetTexture());
            surface.MaterialOverride = mat;

            _portals.Add(new Portal { Surface = surface, Transform = t, Viewport = vp, Cam = cam, Material = mat });
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

    public override void _Process(double delta)
    {
        if (_portals.Count == 0 || _mainCamera is null)
            return;

        Camera3D main = _mainCamera;
        // Main camera pose → Quake. Camera looks down -Z (Godot); right = +X, up = +Y.
        NVec3 camPosQ = Coords.ToQuake(main.GlobalPosition);
        NVec3 fwdQ = Coords.ToQuake(-main.GlobalBasis.Z);
        NVec3 rightQ = Coords.ToQuake(main.GlobalBasis.X);
        NVec3 upQ = Coords.ToQuake(main.GlobalBasis.Y);

        foreach (Portal p in _portals)
        {
            WarpzoneTransform t = p.Transform;
            // Place the portal camera at the warp-transformed main-camera pose (the exit view). Rotate() carries
            // the full basis (incl. roll) through the portal; TransformOrigin shifts the position.
            NVec3 pPos = t.TransformOrigin(camPosQ);
            NVec3 pFwd = t.Rotate(fwdQ);
            NVec3 pRight = t.Rotate(rightQ);
            NVec3 pUp = t.Rotate(upQ);

            p.Cam.GlobalBasis = new Basis(Coords.ToGodot(pRight), Coords.ToGodot(pUp), -Coords.ToGodot(pFwd));
            p.Cam.GlobalPosition = Coords.ToGodot(pPos);
            p.Cam.Fov = main.Fov;
        }
    }

    /// <summary>Match a portal surface to the linked warpzone whose IN plane it is: nearest IN-origin within a
    /// tolerance AND surface-normal aligned with the IN forward (either facing, since the brush normal may point
    /// either way). Returns that zone's IN→OUT transform — the warp a portal camera applies to the main view.</summary>
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
