using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Particles;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Client.Particles;

// =====================================================================================================
//  Faithful particle RENDERER (planning/particles-dual-system.md §C.4). Draws the CPU-simulated pool
//  (ParticleSim.Pool) as 3 MultiMesh batches keyed by blend mode over the SINGLE particlefont atlas:
//
//      ParticleBlend.Alpha  -> blend_mix  (src-alpha)
//      ParticleBlend.Add    -> blend_add  (glow)
//      ParticleBlend.InvMod -> blend_sub  (DP's inverse-modulate; closest one-pass approximation)
//
//  Each batch is a MultiMeshInstance3D over a 1x1 QuadMesh + an INLINE ShaderMaterial that billboards
//  in the vertex stage (same camera-facing math as EffectSystem's growth shader, EffectSystem.cs:1461-
//  1471), applies the per-instance angle (Particle.Angle, already advanced by spin in the sim), and
//  samples the correct atlas cell. Per-instance data is uploaded with one
//  RenderingServer.MultimeshSetBuffer per batch: transform(12) + COLOR(4) + CUSTOM(4):
//
//      CUSTOM = (cellSlot, angleRadians, sparkFlag, 0)
//
//  where cellSlot indexes a uniform array of atlas UV-rects (built from ParticleFont's cell table) the
//  fragment stage reads, and sparkFlag != 0 means "the transform already carries a CPU-stretched spark
//  basis — do NOT billboard" (cl_particles.c:2812-2825). Billboard particles upload an identity-ish
//  transform (origin + uniform scale) and let the shader build the camera-facing quad.
//
//  Draw-time fidelity matches DP R_DrawParticles: size * cl_particles_size (cl_particles.c:2732),
//  near-clip skip (2932), drawdistance^2 cull (2935), and the Alpha batch is CPU-sorted back-to-front
//  (2935+; add/invmod are order-independent). Coordinate conversion to Godot happens here at the render
//  boundary via Coords.ToGodot — the sim stays in Quake space.
// =====================================================================================================

/// <summary>Renders the faithful CPU particle pool as 3 blend-keyed MultiMesh batches over the atlas.</summary>
public sealed partial class FaithfulParticleRenderer : Node3D
{
    // 20 floats per instance: 12 transform + 4 color + 4 custom (Godot MultiMesh buffer layout when
    // TransformFormat=Transform3D, UseColors and UseCustomData are both enabled).
    private const int FloatsPerInstance = 20;

    // The DP particlefont index ranges we pack into the render atlas: sprites 0-63 and the beam strips
    // 200-205 (ParticleFont.cs class doc). Indices outside these slots draw the fallback (slot 0).
    private static readonly (int Lo, int Hi)[] AtlasRanges = { (0, 64), (200, 206) };

    private sealed class Batch
    {
        public MultiMeshInstance3D Node = null!;
        public MultiMesh Mesh = null!;
        public ShaderMaterial Material = null!;
        public float[] Buffer = Array.Empty<float>();
        public int Count;
        // Scratch indices into the pool for this batch (filled each Sync, used by the alpha sort).
        public readonly List<int> Indices = new();
    }

    private Batch? _alpha;
    private Batch? _add;
    private Batch? _sub;

    // The packed render atlas + per-cell normalized UV rects (slot index -> rect). texnum -> slot via _slotOf.
    private Texture2D? _atlasTex;
    private readonly Dictionary<int, int> _slotOf = new();   // DP texnum -> contiguous shader slot
    private Vector4[] _cellRects = Array.Empty<Vector4>();    // slot -> (u0, v0, du, dv) in atlas UV space
    private bool _built;

    // Cached per-frame view plane (set in Sync) for the near-clip / drawdistance gates.
    private float _drawDistanceSq;       // 0 => disabled
    private float _nearClipMin = 4f;

    // Cached depth-sort state — the alpha batch's back-to-front sort reads these instead of capturing a
    // closure, so Sync allocates nothing per frame. _depthCmp is the delegate, allocated once.
    private Particle[] _sortPool = Array.Empty<Particle>();
    private NVec3 _sortVo;
    private Comparison<int>? _depthCmp;

    private int CompareDepthFarthestFirst(int ia, int ib)
    {
        NVec3 da = _sortPool[ia].Org - _sortVo, db = _sortPool[ib].Org - _sortVo;
        float fa = NVec3.Dot(da, da), fb = NVec3.Dot(db, db);
        return fb.CompareTo(fa); // farthest first
    }

    public override void _Ready()
    {
        _alpha = MakeBatch("alpha", BlendKind.Mix);
        _add = MakeBatch("add", BlendKind.Add);
        _sub = MakeBatch("sub", BlendKind.Sub);
        // BuildAtlas may have run before _Ready (the backend builds the atlas as soon as it has the font,
        // which can precede this node entering the tree). If an atlas is already packed, apply it now that
        // the batch materials exist.
        if (_atlasTex is not null)
        {
            ApplyAtlas(_alpha);
            ApplyAtlas(_add);
            ApplyAtlas(_sub);
            _built = true;
        }
    }

    private enum BlendKind { Mix, Add, Sub }

    // ---------------------------------------------------------------------------------------------
    //  Atlas build — pack ParticleFont cells into one texture + a UV-rect table the shader indexes.
    //  ParticleFont exposes only Cell(index) (a cropped ImageTexture per index); it does NOT surface
    //  the source atlas or rects. So we re-pack the cells we need into our own grid atlas and record
    //  each cell's normalized rect. Done once per font (cheap: ~70 small blits).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Build the render atlas + UV table from the loaded <paramref name="font"/>. Safe to call
    /// repeatedly (rebuilds on a new font); no-op when the font isn't loaded (renderer draws nothing).</summary>
    public void BuildAtlas(ParticleFont? font)
    {
        _built = false;
        _slotOf.Clear();
        _atlasTex = null;
        _cellRects = Array.Empty<Vector4>();
        if (font is null || !font.Loaded)
            return;

        // Gather the cell images we can resolve, in a stable slot order.
        var cells = new List<(int Index, Image Img)>();
        foreach ((int lo, int hi) in AtlasRanges)
            for (int i = lo; i < hi; i++)
            {
                ImageTexture? t = font.Cell(i);
                Image? img = t?.GetImage();
                if (img is null || img.GetWidth() <= 0 || img.GetHeight() <= 0)
                    continue;
                if (img.IsCompressed())
                    img.Decompress();
                if (img.GetFormat() != Image.Format.Rgba8)
                    img.Convert(Image.Format.Rgba8);
                cells.Add((i, img));
            }
        if (cells.Count == 0)
            return;

        // Uniform grid sized to the largest cell, padded by 1px to stop bilinear bleed between slots.
        int cellW = 0, cellH = 0;
        foreach ((_, Image img) in cells)
        {
            cellW = Math.Max(cellW, img.GetWidth());
            cellH = Math.Max(cellH, img.GetHeight());
        }
        const int pad = 1;
        int slotW = cellW + pad * 2, slotH = cellH + pad * 2;
        int cols = Mathf.CeilToInt(MathF.Sqrt(cells.Count));
        int rows = Mathf.CeilToInt(cells.Count / (float)cols);
        int atlasW = NextPow2(cols * slotW), atlasH = NextPow2(rows * slotH);

        var atlas = Image.CreateEmpty(atlasW, atlasH, false, Image.Format.Rgba8);
        atlas.Fill(new Color(0, 0, 0, 0));
        _cellRects = new Vector4[cells.Count];

        for (int slot = 0; slot < cells.Count; slot++)
        {
            (int index, Image img) = cells[slot];
            int cx = (slot % cols) * slotW + pad;
            int cy = (slot / cols) * slotH + pad;
            int w = img.GetWidth(), h = img.GetHeight();
            atlas.BlitRect(img, new Rect2I(0, 0, w, h), new Vector2I(cx, cy));
            // Inset the sampled rect by a half-texel so bilinear never reaches the padding.
            float u0 = (cx + 0.5f) / atlasW;
            float v0 = (cy + 0.5f) / atlasH;
            float du = (w - 1f) / atlasW;
            float dv = (h - 1f) / atlasH;
            _cellRects[slot] = new Vector4(u0, v0, du, dv);
            _slotOf[index] = slot;
        }

        _atlasTex = ImageTexture.CreateFromImage(atlas);

        // Push the atlas + UV table into every batch material. If _Ready hasn't built the batches yet
        // (BuildAtlas can run before this node enters the tree), defer: _Ready re-applies once they exist.
        if (_alpha is not null && _add is not null && _sub is not null)
        {
            ApplyAtlas(_alpha);
            ApplyAtlas(_add);
            ApplyAtlas(_sub);
            _built = true;
        }
    }

    private void ApplyAtlas(Batch b)
    {
        if (_atlasTex is null || b?.Material is null)
            return;
        b.Material.SetShaderParameter("albedo_tex", _atlasTex);
        // Pass the UV rects as a flat vec4 array uniform; the fragment stage indexes it by CUSTOM.x.
        var arr = new Godot.Collections.Array();
        foreach (Vector4 r in _cellRects)
            arr.Add(r);
        b.Material.SetShaderParameter("cell_rects", arr);
        b.Material.SetShaderParameter("cell_count", _cellRects.Length);
    }

    private static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return Math.Max(2, p);
    }

    // ---------------------------------------------------------------------------------------------
    //  Batch + shader construction
    // ---------------------------------------------------------------------------------------------

    private Batch MakeBatch(string name, BlendKind blend)
    {
        // 1x1 quad centered on origin; the vertex shader scales/billboards it. Two-sided so back-facing
        // billboards (and oriented sparks) still draw.
        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = quad,
            InstanceCount = 0,
        };

        var mat = new ShaderMaterial { Shader = BillboardShader(blend) };

        var node = new MultiMeshInstance3D
        {
            Name = "fp_" + name,
            Multimesh = mm,
            MaterialOverride = mat,
            // Particles are emissive sprites: never cast/receive shadows, never affected by GI.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // A generous custom AABB so the renderer doesn't cull the whole batch when instances are far
            // from the node origin (we never recompute a tight AABB per frame).
            CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
        };
        AddChild(node);

        return new Batch { Node = node, Mesh = mm, Material = mat };
    }

    /// <summary>The inline billboard shader for a blend mode. Vertex: when CUSTOM.z (sparkFlag) is 0 it
    /// builds a camera-facing quad from the instance origin + uniform scale (length of MODEL_MATRIX[0]),
    /// rotates it by CUSTOM.y (angle); when sparkFlag != 0 it draws the instance transform verbatim (the
    /// CPU already baked the velocity-stretched spark basis). Fragment: remap UV into the atlas cell
    /// selected by CUSTOM.x, then modulate by COLOR.</summary>
    private static Shader BillboardShader(BlendKind blend)
    {
        string blendMode = blend switch
        {
            BlendKind.Add => "blend_add",
            BlendKind.Sub => "blend_sub",
            _ => "blend_mix",
        };
        return new Shader
        {
            Code =
                "shader_type spatial;\n" +
                // Depth TEST on (walls occlude), depth WRITE off for transparency (matches the growth
                // shader, EffectSystem.cs:1455-1458). Unshaded emissive sprites, two-sided.
                "render_mode " + blendMode + ", unshaded, cull_disabled, shadows_disabled, depth_draw_opaque;\n" +
                "uniform sampler2D albedo_tex : source_color, filter_linear;\n" +
                "uniform vec4 cell_rects[256];\n" +   // slot -> (u0, v0, du, dv); 256 covers 0-63 + 200-205
                "uniform int cell_count = 0;\n" +
                "varying flat int v_slot;\n" +
                "varying flat int v_spark;\n" +
                "void vertex() {\n" +
                "    int slot = int(INSTANCE_CUSTOM.x + 0.5);\n" +
                "    v_slot = clamp(slot, 0, max(cell_count - 1, 0));\n" +
                "    float angle = INSTANCE_CUSTOM.y;\n" +
                "    float spark = INSTANCE_CUSTOM.z;\n" +
                "    v_spark = spark > 0.5 ? 1 : 0;\n" +
                "    if (spark > 0.5) {\n" +
                // Spark/oriented: the CPU baked the basis into MODEL_MATRIX; draw it directly.
                "        MODELVIEW_MATRIX = VIEW_MATRIX * MODEL_MATRIX;\n" +
                "    } else {\n" +
                // Billboard: camera-facing axes scaled by the per-instance uniform scale, then rolled by
                // the particle angle. Same construction as EffectSystem's growth shader (1461-1470).
                "        float sx = length(MODEL_MATRIX[0].xyz);\n" +
                "        float sy = length(MODEL_MATRIX[1].xyz);\n" +
                "        vec3 r = normalize(INV_VIEW_MATRIX[0].xyz) * sx;\n" +
                "        vec3 u = normalize(INV_VIEW_MATRIX[1].xyz) * sy;\n" +
                "        vec3 f = normalize(INV_VIEW_MATRIX[2].xyz);\n" +
                "        mat4 bb = mat4(vec4(r, 0.0), vec4(u, 0.0), vec4(f, 0.0), MODEL_MATRIX[3]);\n" +
                "        mat4 rot = mat4(vec4(cos(angle), -sin(angle), 0.0, 0.0), vec4(sin(angle), cos(angle), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));\n" +
                "        MODELVIEW_MATRIX = VIEW_MATRIX * (bb * rot);\n" +
                "    }\n" +
                "}\n" +
                "void fragment() {\n" +
                "    vec4 rect = cell_rects[v_slot];\n" +     // (u0, v0, du, dv)
                "    vec2 uv = rect.xy + UV * rect.zw;\n" +
                "    vec4 t = texture(albedo_tex, uv);\n" +
                "    ALBEDO = t.rgb * COLOR.rgb;\n" +
                "    ALPHA = t.a * COLOR.a;\n" +
                "}\n",
        };
    }

    // ---------------------------------------------------------------------------------------------
    //  Per-frame sync — cull, sort, pack, upload.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuild the 3 MultiMesh buffers from the live particles in <paramref name="pool"/> (scan
    /// [0, <paramref name="highWater"/>)). Culls by the DP near-clip (cl_particles.c:2932) and
    /// drawdistance^2 (2935) gates relative to the view, sorts the Alpha batch back-to-front (2935+),
    /// and uploads each batch via RenderingServer.MultimeshSetBuffer. <paramref name="viewOrigin"/> and
    /// <paramref name="viewForward"/> are in QUAKE space (the sim's space); conversion to Godot is done
    /// here at the render boundary.
    /// </summary>
    public void Sync(Particle[] pool, int highWater, NVec3 viewOrigin, NVec3 viewForward)
    {
        Batch? alpha = _alpha, add = _add, sub = _sub;
        if (!_built || alpha is null || add is null || sub is null || pool is null || highWater <= 0)
        {
            ClearBatch(alpha);
            ClearBatch(add);
            ClearBatch(sub);
            return;
        }

        // cl_particles_size scales every particle's drawn size (cl_particles.c:2732).
        float sizeScale = ReadCvar(ParticleCvars.Size, 1f);
        if (sizeScale <= 0f) sizeScale = 1f;
        float alphaScale = Math.Clamp(ReadCvar(ParticleCvars.Alpha, 1f), 0f, 1f);

        // Near-clip / drawdistance gates (cl_particles.c:2932, 2935).
        _nearClipMin = ReadCvar(ParticleCvars.NearClipMin, 4f);
        float drawDist = ReadCvar(ParticleCvars.DrawDistance, 0f);
        _drawDistanceSq = drawDist > 0f ? drawDist * drawDist : 0f;
        NVec3 fwd = Normalize(viewForward);
        float viewPlane = NVec3.Dot(viewOrigin, fwd);   // dot(view.org, fwd); compared per particle (2932)

        alpha.Indices.Clear();
        add.Indices.Clear();
        sub.Indices.Clear();

        // 1) Cull + bucket by blend.
        for (int i = 0; i < highWater; i++)
        {
            ref Particle p = ref pool[i];
            if (!p.Active || p.Alpha <= 0f)
                continue;
            // Beams are drawn by the dedicated beam path; the faithful renderer handles billboard/spark/
            // oriented. Skip pure beams here (orientation == Beam) — they have no billboard form.
            if (p.Orientation == ParticleOrientation.Beam)
                continue;

            float r = p.Size * sizeScale;
            // Near clip: skip if in front-of-plane distance is less than the view plane + nearclip_min,
            // expanded by the particle radius so a big sprite straddling the plane still shows (2932).
            float along = NVec3.Dot(p.Org, fwd);
            if (along < viewPlane + _nearClipMin - r)
                continue;
            if (_drawDistanceSq > 0f)
            {
                NVec3 d = p.Org - viewOrigin;
                if (NVec3.Dot(d, d) > _drawDistanceSq)
                    continue;
            }

            switch (p.BlendMode)
            {
                case ParticleBlend.Add: add.Indices.Add(i); break;
                case ParticleBlend.InvMod: sub.Indices.Add(i); break;
                default: alpha.Indices.Add(i); break;
            }
        }

        // 2) Sort the alpha batch back-to-front by squared distance to the view (2935+). Add/invmod are
        //    order-independent, so they stay in pool order. Use a CACHED comparison delegate (fields hold the
        //    pool + view) so the per-frame sort allocates no closure.
        if (alpha.Indices.Count > 1)
        {
            _sortPool = pool;
            _sortVo = viewOrigin;
            _depthCmp ??= CompareDepthFarthestFirst;
            alpha.Indices.Sort(_depthCmp);
        }

        // 3) Pack + upload each batch.
        PackAndUpload(alpha, pool, sizeScale, alphaScale, fwd);
        PackAndUpload(add, pool, sizeScale, alphaScale, fwd);
        PackAndUpload(sub, pool, sizeScale, alphaScale, fwd);
    }

    private void PackAndUpload(Batch b, Particle[] pool, float sizeScale, float alphaScale, NVec3 viewFwd)
    {
        int n = b.Indices.Count;
        if (n == 0)
        {
            ClearBatch(b);
            return;
        }

        // Reuse a grow-only buffer; InstanceCount tracks the buffer capacity and VisibleInstanceCount limits
        // what's drawn, so the per-frame path allocates NOTHING (only a native marshal copy in the upload).
        int need = n * FloatsPerInstance;
        if (b.Buffer.Length < need)
        {
            b.Buffer = new float[need];
            b.Mesh.InstanceCount = b.Buffer.Length / FloatsPerInstance;
        }
        float[] buf = b.Buffer;

        for (int k = 0; k < n; k++)
        {
            ref Particle p = ref pool[b.Indices[k]];
            int o = k * FloatsPerInstance;

            int slot = _slotOf.TryGetValue(p.TexNum, out int s) ? s : 0;
            float angle = p.Angle * (MathF.PI / 180f); // sim stores degrees (particle_t.angle); shader wants rad
            float alpha = Math.Clamp(p.Alpha / 255f * alphaScale, 0f, 1f);
            Color col = new(p.ColorR / 255f, p.ColorG / 255f, p.ColorB / 255f, alpha);

            Vector3 gpos = Coords.ToGodot(p.Org);

            if (p.Orientation == ParticleOrientation.Spark)
            {
                // Velocity-stretched spark (cl_particles.c:2812-2825): half-length along the CURRENT
                // velocity = max(stretch * 0.04 * |vel|, size * 0.5); cross width = size. Build the basis
                // CPU-side so the shader draws it verbatim (sparkFlag = 1). The quad is 1x1 centered, so
                // the X axis becomes the half-width*2 and Y the half-length*2 (full extents).
                float size = p.Size * sizeScale;
                float speed = p.Vel.Length();
                float stretch = p.Stretch > 0f ? p.Stretch : 1f;
                float halfLen = MathF.Max(stretch * 0.04f * speed, size * 0.5f);
                float width = MathF.Max(size, 0.001f);

                // Length axis = velocity direction (Godot space); width axis = a screen-ish perpendicular
                // using the view forward so the streak keeps some facing toward the camera.
                Vector3 gvel = speed > 1e-4f ? Coords.ToGodot(p.Vel).Normalized() : Vector3.Up;
                Vector3 gfwd = Coords.ToGodot(viewFwd);
                Vector3 widthAxis = gvel.Cross(gfwd);
                if (widthAxis.LengthSquared() < 1e-6f)
                    widthAxis = gvel.Cross(Vector3.Right);
                widthAxis = widthAxis.Normalized();
                Vector3 faceAxis = widthAxis.Cross(gvel).Normalized();

                Vector3 xAxis = widthAxis * (width);        // full width across the streak
                Vector3 yAxis = gvel * (halfLen * 2f);      // full length along velocity
                Vector3 zAxis = faceAxis;                   // unit normal (no scale needed)
                WriteTransform(buf, o, xAxis, yAxis, zAxis, gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 1f);
            }
            else if (p.Orientation == ParticleOrientation.Oriented)
            {
                // Oriented (double-sided) billboard fixed to a surface: orient by the velocity/normal the
                // sim carries in Vel (decal-style). Treat like a spark-flagged instance (no billboarding):
                // build a basis whose Z is the orientation normal, scaled to the particle size.
                float size2 = p.Size * sizeScale;
                Vector3 gnrm = p.Vel.LengthSquared() > 1e-6f ? Coords.ToGodot(p.Vel).Normalized() : Vector3.Up;
                Vector3 refv = MathF.Abs(gnrm.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
                Vector3 xa = refv.Cross(gnrm).Normalized() * size2;
                Vector3 ya = gnrm.Cross(xa.Normalized()).Normalized() * size2;
                WriteTransform(buf, o, xa, ya, gnrm, gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 1f);
            }
            else
            {
                // Billboard: upload origin + a uniform scale = full edge (2*half-size). The shader reads
                // the scale from length(MODEL_MATRIX[0/1]) and builds the camera-facing quad.
                float edge = MathF.Max(p.Size * sizeScale * 2f, 0.001f);
                WriteTransform(buf, o,
                    new Vector3(edge, 0, 0), new Vector3(0, edge, 0), new Vector3(0, 0, edge), gpos);
                WriteColor(buf, o, col);
                WriteCustom(buf, o, slot, angle, sparkFlag: 0f);
            }
        }

        b.Mesh.VisibleInstanceCount = n;       // draw only the n filled instances (buffer may be larger)
        b.Count = n;

        // One upload of the reused buffer (length == InstanceCount*stride, the contract MultimeshSetBuffer
        // requires). No managed allocation here — only the native marshal copy.
        RenderingServer.MultimeshSetBuffer(b.Mesh.GetRid(), buf);
        b.Node.Visible = true;
    }

    private static void ClearBatch(Batch? b)
    {
        if (b is null) return;
        b.Count = 0;
        if (b.Mesh is not null)
            b.Mesh.VisibleInstanceCount = 0;
        if (b.Node is not null)
            b.Node.Visible = false;
    }

    // --- MultiMesh buffer writers (row-major Transform3D: 3 rows of [basisRow.x, .y, .z, origin]) -----

    private static void WriteTransform(float[] buf, int o, Vector3 xAxis, Vector3 yAxis, Vector3 zAxis, Vector3 origin)
    {
        // Godot MultiMesh stores the Transform3D as 3 rows; row r = (basis.x[r], basis.y[r], basis.z[r], origin[r]).
        buf[o + 0] = xAxis.X; buf[o + 1] = yAxis.X; buf[o + 2] = zAxis.X; buf[o + 3] = origin.X;
        buf[o + 4] = xAxis.Y; buf[o + 5] = yAxis.Y; buf[o + 6] = zAxis.Y; buf[o + 7] = origin.Y;
        buf[o + 8] = xAxis.Z; buf[o + 9] = yAxis.Z; buf[o + 10] = zAxis.Z; buf[o + 11] = origin.Z;
    }

    private static void WriteColor(float[] buf, int o, Color c)
    {
        buf[o + 12] = c.R; buf[o + 13] = c.G; buf[o + 14] = c.B; buf[o + 15] = c.A;
    }

    private static void WriteCustom(float[] buf, int o, int slot, float angle, float sparkFlag)
    {
        buf[o + 16] = slot; buf[o + 17] = angle; buf[o + 18] = sparkFlag; buf[o + 19] = 0f;
    }

    private static float ReadCvar(string name, float fallback)
        => Api.Services is null ? fallback : Api.Cvars.GetFloat(name);

    private static NVec3 Normalize(NVec3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v / len : new NVec3(1f, 0f, 0f);
    }
}
