using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Formats.Bsp;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client.Particles;

// =====================================================================================================
//  Faithful decal SPLATS — the C# mirror of Darkplaces' R_DecalSystem (gl_rmain.c:9134-9710), the "new
//  decal system" every particle decal routes through (CL_SpawnDecalParticleForSurface/ForPoint and
//  CL_ImmediateBloodStain all call R_DecalSystem_SplatEntities).
//
//  DP decals are NOT projected boxes: the decal quad is CLIPPED against the real surface triangles
//  around the impact, producing geometry that CONFORMS to the wall — it wraps across edges and short
//  ledges, and has no projection streaking. The clipped triangles draw with
//  GL_ZERO / GL_ONE_MINUS_SRC_COLOR (gl_rmain.c:9705):
//
//      wall' = wall · (1 − tex·color)
//
//  i.e. the splat color is the amount of light REMOVED — multiplicative darkening that can never
//  brighten the surface (a teal stain on a dark wall reads as a subtle teal-tinted burn, not a bright
//  cyan patch), with GL_PolygonOffset for the surface bias (:9702) and double-sided, depth-tested,
//  no-depth-write state. This node reproduces all of that:
//
//   * Geometry: brush faces from the static CollisionWorld overlapping the splat box are reconstructed
//     (the corner points lying on each side plane), clipped Sutherland–Hodgman against the splat's
//     6 box planes, fanned into triangles, and biased slightly off the surface (no polygon-offset in
//     Godot shaders — a small geometric push reads the same).
//   * Blend: an inline blend_mul shader outputs (1 − tex·color·fade) == DP's exact blendfunc.
//   * Color: callers pass the INVMOD removal color exactly as DP feeds SplatEntities — staintex stains
//     pre-complemented (1 − staincolor), blood/decal-block splats the raw particle color.
//   * Lifecycle: full strength for DecalTime, fading over FadeTime (cl_decals_time/_fadetime), capped.
//
//  When no CollisionWorld is wired (pure --connect client) the splat falls back to a single flat quad
//  perpendicular to the hit normal — still multiplicative, still streak-free, just not conforming.
// =====================================================================================================

/// <summary>Surface-conforming multiplicative decal splats (DP R_DecalSystem). One node per map session;
/// the faithful particle backend routes every pt_decal / stain / blood mark here.</summary>
public sealed partial class DecalSplats : Node3D
{
    /// <summary>Full-strength hold before fading (DP cl_decals_time 20, trimmed like the legacy Decals).</summary>
    [Export] public float DecalTime { get; set; } = 12f;

    /// <summary>Fade-out duration (DP cl_decals_fadetime).</summary>
    [Export] public float FadeTime { get; set; } = 2f;

    /// <summary>Hard cap on live splats; oldest culled past this (DP cl_decals_max, scaled down).</summary>
    [Export] public int MaxSplats { get; set; } = 256;

    /// <summary>DP cl_decals_newsystem_intensitymultiplier (default 2): boosts splat intensity so the
    /// multiplicative marks read at gameplay brightness.</summary>
    [Export] public float IntensityMultiplier { get; set; } = 2f;

    /// <summary>Geometric push off the surface standing in for DP's GL_PolygonOffset (gl_rmain.c:9702).</summary>
    private const float SurfaceBias = 0.3f;

    /// <summary>The static collision world supplying brush faces to conform to (wired at map load via
    /// <see cref="EffectSystem.SetCollisionWorld"/>). Null → flat-quad fallback.</summary>
    public CollisionWorld? World { get; set; }

    /// <summary>The particlefont atlas — splat textures are its raw cells (DP samples the same cells; the
    /// black background contributes zero removal under the multiplicative blend, so no alpha is needed).</summary>
    public ParticleFont? Font { get; set; }

    private readonly Queue<MeshInstance3D> _live = new();
    private Shader? _shader;

    // Scratch buffers reused per splat (single-threaded scene calls).
    private readonly List<Brush> _brushScratch = new(32);
    private readonly List<NVec3> _polyA = new(16);
    private readonly List<NVec3> _polyB = new(16);

    // ---------------------------------------------------------------------------------------------
    //  Public API
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Splat a mark onto the geometry around <paramref name="org"/> (Quake space), facing along
    /// <paramref name="dir"/> (the surface normal, or the impact velocity for blood smears — DP accepts
    /// either, cl_particles.c:3007). <paramref name="halfSize"/> is the mark half-extent (effectinfo
    /// size/stainsize); <paramref name="removal"/> is the INVMOD removal color (0..1, what the mark
    /// subtracts); <paramref name="alpha"/> 0..1 scales intensity; <paramref name="texnum"/> selects the
    /// particlefont cell.
    /// </summary>
    public void Splat(NVec3 org, NVec3 dir, float halfSize, Color removal, float alpha, int texnum)
    {
        alpha = Math.Clamp(alpha * IntensityMultiplier, 0f, 1f);
        if (alpha <= 0.01f)
            return;
        // A near-zero removal color subtracts nothing — invisible in DP, skip the geometry work.
        if (removal.R + removal.G + removal.B < 0.02f)
            return;
        halfSize = Math.Clamp(halfSize <= 0f ? 8f : halfSize, 1f, 256f);

        NVec3 n = Normalize(dir, new NVec3(0f, 0f, 1f));
        // DP VectorVectors: an arbitrary orthonormal right/up pair around the splat axis.
        NVec3 right = Normalize(MathF.Abs(n.Z) > 0.95f
            ? NVec3.Cross(new NVec3(1f, 0f, 0f), n)
            : NVec3.Cross(new NVec3(0f, 0f, 1f), n), new NVec3(1f, 0f, 0f));
        NVec3 up = NVec3.Cross(n, right);

        var verts = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);

        if (World is not null)
            ClipBrushFaces(org, n, right, up, halfSize, verts, uvs);

        // Fallback (no collision world, or the box clipped to nothing — e.g. a hit on an entity or a
        // displaced patch): one flat quad perpendicular to the axis, DP's pre-newsystem look.
        if (verts.Count == 0)
            EmitQuad(org + n * SurfaceBias, right, up, halfSize, verts, uvs);

        AddSplatMesh(verts, uvs, removal, alpha, texnum);
    }

    /// <summary>
    /// DP CL_SpawnDecalParticleForPoint (cl_particles.c:981): probe 32 random rays out to
    /// <paramref name="maxDist"/>, keep the nearest non-NOMARKS hit, and splat on that surface. Used by
    /// effectinfo <c>type decal</c> blocks (originjitter[0] is the reach). No surface → no mark.
    /// </summary>
    public void SplatPoint(NVec3 org, float maxDist, float halfSize, Color removal, float alpha, int texnum)
    {
        if (Api.Services is null)
            return;
        float dist = MathF.Max(maxDist, 4f);

        bool found = false;
        float bestFrac = 1f;
        NVec3 bestPos = org, bestNormal = new(0f, 0f, 1f);
        for (int i = 0; i < 32; i++)
        {
            NVec3 d = RandomUnitVector();
            TraceResult tr = Api.Trace.Trace(org, NVec3.Zero, NVec3.Zero, org + d * dist, MoveFilter.WorldOnly, null);
            if (tr.Fraction >= bestFrac)
                continue;
            if ((tr.DpHitContents & SuperContents.Sky) != 0 ||
                (tr.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoMarks) != 0)
                continue;
            bestFrac = tr.Fraction;
            bestPos = tr.EndPos;
            bestNormal = tr.PlaneNormal;
            found = true;
        }
        if (found)
            Splat(bestPos, bestNormal, halfSize, removal, alpha, texnum);
    }

    /// <summary>Remove every live splat (map change).</summary>
    public void Clear()
    {
        while (_live.TryDequeue(out MeshInstance3D? m))
            if (GodotObject.IsInstanceValid(m))
                m.QueueFree();
    }

    // ---------------------------------------------------------------------------------------------
    //  Geometry — brush-face reconstruction + box clip (DP R_DecalSystem_SplatTriangle equivalent).
    // ---------------------------------------------------------------------------------------------

    private void ClipBrushFaces(NVec3 org, NVec3 n, NVec3 right, NVec3 up, float halfSize,
        List<Vector3> verts, List<Vector2> uvs)
    {
        var mins = new System.Numerics.Vector3(org.X - halfSize, org.Y - halfSize, org.Z - halfSize);
        var maxs = new System.Numerics.Vector3(org.X + halfSize, org.Y + halfSize, org.Z + halfSize);
        _brushScratch.Clear();
        World!.Query(mins, maxs, _brushScratch);

        foreach (Brush brush in _brushScratch)
        {
            if ((brush.Contents & SuperContents.Solid) == 0)
                continue;   // clip/trigger volumes leave no marks

            foreach (BrushPlane side in brush.Sides)
            {
                // Sky / NOMARKS faces never take marks (DP filters hitq3surfaceflags).
                if ((side.SurfaceFlags & (Q3SurfaceFlags.NoMarks | Q3SurfaceFlags.Sky)) != 0)
                    continue;
                // Skip faces nearly edge-on to the splat axis: they'd receive a sliver-stretched smear
                // (the streak artifact). |dot| accepts both normal- and velocity-direction conventions.
                if (MathF.Abs(NVec3.Dot(side.Normal, n)) < 0.06f)
                    continue;
                // Face plane must pass within the splat box at all.
                float planeDist = NVec3.Dot(side.Normal, org) - side.Dist;
                if (MathF.Abs(planeDist) > halfSize)
                    continue;

                // Reconstruct the face polygon: the brush corner points lying on this side's plane.
                _polyA.Clear();
                foreach (NVec3 p in brush.Points)
                    if (MathF.Abs(NVec3.Dot(side.Normal, p) - side.Dist) < 0.1f)
                        _polyA.Add(p);
                if (_polyA.Count < 3)
                    continue;
                WindAroundCentroid(_polyA, side.Normal);

                // Clip against the splat's 6 box planes (DP clips each surface triangle the same way).
                if (!ClipPolyAgainstBox(org, n, right, up, halfSize))
                    continue;

                // Fan-triangulate; bias off the face along ITS normal (the polygon-offset stand-in) and
                // project each vertex into the splat plane for UVs.
                NVec3 push = side.Normal * SurfaceBias;
                for (int i = 2; i < _polyA.Count; i++)
                {
                    EmitVertex(_polyA[0] + push, org, right, up, halfSize, verts, uvs);
                    EmitVertex(_polyA[i - 1] + push, org, right, up, halfSize, verts, uvs);
                    EmitVertex(_polyA[i] + push, org, right, up, halfSize, verts, uvs);
                }
            }
        }
    }

    /// <summary>Clip the polygon in <see cref="_polyA"/> against the splat box (4 side planes + front/
    /// back along the axis). Result back in <see cref="_polyA"/>; false when fully clipped away.</summary>
    private bool ClipPolyAgainstBox(NVec3 org, NVec3 n, NVec3 right, NVec3 up, float halfSize)
    {
        // Each plane keeps the half-space dot(p, axis) <= dot(org, axis) + halfSize — its mirrored axis
        // covers the opposite side, so together the six bound the splat box.
        Span<(NVec3 N, float D)> planes = stackalloc (NVec3, float)[6]
        {
            (right, NVec3.Dot(right, org) + halfSize),
            (-right, -NVec3.Dot(right, org) + halfSize),
            (up, NVec3.Dot(up, org) + halfSize),
            (-up, -NVec3.Dot(up, org) + halfSize),
            (n, NVec3.Dot(n, org) + halfSize),
            (-n, -NVec3.Dot(n, org) + halfSize),
        };
        foreach ((NVec3 pn, float pd) in planes)
        {
            _polyB.Clear();
            int count = _polyA.Count;
            for (int i = 0; i < count; i++)
            {
                NVec3 a = _polyA[i];
                NVec3 b = _polyA[(i + 1) % count];
                float da = NVec3.Dot(pn, a) - pd;
                float db = NVec3.Dot(pn, b) - pd;
                bool ia = da <= 0f, ib = db <= 0f;
                if (ia)
                    _polyB.Add(a);
                if (ia != ib)
                {
                    float t = da / (da - db);
                    _polyB.Add(a + (b - a) * t);
                }
            }
            _polyA.Clear();
            _polyA.AddRange(_polyB);
            if (_polyA.Count < 3)
                return false;
        }
        return true;
    }

    /// <summary>Sort the on-plane corner points into a convex winding around their centroid.</summary>
    private static void WindAroundCentroid(List<NVec3> poly, NVec3 normal)
    {
        NVec3 c = default;
        foreach (NVec3 p in poly) c += p;
        c /= poly.Count;
        NVec3 axisA = Normalize(poly[0] - c, new NVec3(1f, 0f, 0f));
        NVec3 axisB = NVec3.Cross(normal, axisA);
        poly.Sort((p, q) =>
        {
            float ap = MathF.Atan2(NVec3.Dot(p - c, axisB), NVec3.Dot(p - c, axisA));
            float aq = MathF.Atan2(NVec3.Dot(q - c, axisB), NVec3.Dot(q - c, axisA));
            return ap.CompareTo(aq);
        });
    }

    private static void EmitVertex(NVec3 p, NVec3 org, NVec3 right, NVec3 up, float halfSize,
        List<Vector3> verts, List<Vector2> uvs)
    {
        verts.Add(Coords.ToGodot(p));
        // Project into the splat plane: [-halfSize, +halfSize] → [0,1] (DP texcoord projection).
        float u = NVec3.Dot(p - org, right) / (2f * halfSize) + 0.5f;
        float v = NVec3.Dot(p - org, up) / (2f * halfSize) + 0.5f;
        uvs.Add(new Vector2(u, v));
    }

    private static void EmitQuad(NVec3 center, NVec3 right, NVec3 up, float halfSize,
        List<Vector3> verts, List<Vector2> uvs)
    {
        NVec3 r = right * halfSize, u = up * halfSize;
        Span<NVec3> c = stackalloc NVec3[4] { center - r - u, center - r + u, center + r + u, center + r - u };
        Span<Vector2> t = stackalloc Vector2[4] { new(0, 0), new(0, 1), new(1, 1), new(1, 0) };
        int[] fan = { 0, 1, 2, 0, 2, 3 };
        foreach (int i in fan)
        {
            verts.Add(Coords.ToGodot(c[i]));
            uvs.Add(t[i]);
        }
    }

    // ---------------------------------------------------------------------------------------------
    //  Mesh + material + lifecycle
    // ---------------------------------------------------------------------------------------------

    private void AddSplatMesh(List<Vector3> verts, List<Vector2> uvs, Color removal, float alpha, int texnum)
    {
        if (verts.Count < 3)
            return;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // The raw particlefont cell — under (1 − tex·color) its black background removes nothing, exactly
        // DP's "the particlefont does not need alpha on most textures". Solid fallback keeps marks alive
        // without the atlas.
        Texture2D? cell = Font?.Cell(texnum);

        var mat = new ShaderMaterial { Shader = _shader ??= SplatShader(), RenderPriority = 2 };
        mat.SetShaderParameter("splat_color", new Color(
            Math.Clamp(removal.R * alpha, 0f, 1f),
            Math.Clamp(removal.G * alpha, 0f, 1f),
            Math.Clamp(removal.B * alpha, 0f, 1f)));
        mat.SetShaderParameter("fade", 1f);
        if (cell is not null)
            mat.SetShaderParameter("splat_tex", cell);
        mat.SetShaderParameter("has_tex", cell is not null);

        var node = new MeshInstance3D
        {
            Name = "splat",
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        AddChild(node);

        // Hold, fade the multiplicative factor to nothing, free — cl_decals_time/_fadetime.
        var tween = node.CreateTween();
        tween.TweenInterval(DecalTime);
        tween.TweenMethod(Callable.From((float f) =>
        {
            if (GodotObject.IsInstanceValid(mat))
                mat.SetShaderParameter("fade", f);
        }), 1f, 0f, FadeTime);
        tween.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
        }));

        _live.Enqueue(node);
        while (_live.Count > MaxSplats && _live.TryDequeue(out MeshInstance3D? old))
            if (GodotObject.IsInstanceValid(old))
                old.QueueFree();
    }

    /// <summary>DP's decal draw state (gl_rmain.c:9699-9706): GL_ZERO/ONE_MINUS_SRC_COLOR == blend_mul of
    /// (1 − tex·color·fade), depth test on / write off, double-sided, unshaded.</summary>
    private static Shader SplatShader() => new()
    {
        Code =
            "shader_type spatial;\n" +
            "render_mode blend_mul, unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
            "uniform sampler2D splat_tex : source_color, filter_linear;\n" +
            "uniform bool has_tex = true;\n" +
            "uniform vec3 splat_color = vec3(1.0);\n" +
            "uniform float fade = 1.0;\n" +
            "void fragment() {\n" +
            "    vec3 t = has_tex ? texture(splat_tex, UV).rgb : vec3(1.0 - smoothstep(0.3, 0.5, distance(UV, vec2(0.5))));\n" +
            "    ALBEDO = vec3(1.0) - t * splat_color * fade;\n" +
            "    ALPHA = 1.0;\n" +
            "}\n",
    };

    private static NVec3 RandomUnitVector()
    {
        for (int tries = 0; tries < 16; tries++)
        {
            float x = (float)GD.RandRange(-1.0, 1.0);
            float y = (float)GD.RandRange(-1.0, 1.0);
            float z = (float)GD.RandRange(-1.0, 1.0);
            float l2 = x * x + y * y + z * z;
            if (l2 > 0.0001f && l2 <= 1f)
                return new NVec3(x, y, z) / MathF.Sqrt(l2);
        }
        return new NVec3(0f, 0f, 1f);
    }

    private static NVec3 Normalize(NVec3 v, NVec3 fallback)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : fallback;
    }
}
