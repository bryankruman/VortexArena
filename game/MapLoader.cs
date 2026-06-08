using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Bsp;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Game.Loaders;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game;

/// <summary>
/// Turns a parsed <see cref="BspData"/> (the Godot-free IBSP loader output) into Godot scene geometry
/// and an engine <see cref="CollisionWorld"/>. Four products:
///
///  * <see cref="BuildMap"/>       — render geometry: an <see cref="ArrayMesh"/> surface per material
///    (texture + lightmap page), textured via the <see cref="AssetSystem"/> material facade, with
///    bezier patches tessellated (<see cref="BezierPatch"/>) and lightmaps wired as UV2 + a lightmap
///    <see cref="ShaderMaterial"/>.
///  * <see cref="BuildCollision"/> — the brush set fed to the trace service (Quake coords, contentflags),
///    plus a secondary Godot <see cref="StaticBody3D"/> trimesh for engine-physics needs.
///  * <see cref="SpawnPoints"/>    — info_player_* spawn entities parsed from the entity lump.
///  * <see cref="BuildWorldspawn"/>— fog/gravity/sky keys from the worldspawn entity.
///
/// Render geometry is converted to Godot's Y-up space at the vertex level (<see cref="Coords.ToGodot"/>);
/// collision stays in Quake space because the whole sim/trace stack runs in Quake coords.
/// </summary>
public static class MapLoader
{
    // -------------------------------------------------------------------------------------------------
    //  Render geometry
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Identifies one ArrayMesh surface. Faces are grouped by their texture (so each surface gets one
    /// material) AND their lightmap page (a lightmapped surface needs its page's texture baked into a
    /// per-page <see cref="ShaderMaterial"/>; faces that share a texture but land on different pages must
    /// therefore split). Non-lightmapped faces all share <see cref="LightmapIndex"/> = -1.
    /// </summary>
    private readonly record struct SurfaceKey(int TextureIndex, int LightmapIndex);

    /// <summary>
    /// Sentinel <see cref="SurfaceKey.LightmapIndex"/> for a q3map2 <i>vertex-lit</i> surface — a face with a
    /// negative lightmap index (e.g. -3) and no <c>.shader</c> entry, which DP shades from the per-vertex RGB
    /// (MODE_VERTEXCOLOR) rather than a lightmap page. Kept distinct from -1 (truly unlit / shader-owned) so
    /// these faces bucket into their own vertex-color material with a packed COLOR array.
    /// </summary>
    private const int VertexLitKey = -2;

    /// <summary>Per-surface accumulation buffers (Quake-space positions; converted at pack time).</summary>
    private sealed class SurfaceBuilder
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> Uvs = new();
        public readonly List<Vector2> Uv2 = new();   // lightmap UVs (only meaningful for lightmapped surfaces)
        public readonly List<Color> Colors = new();  // per-vertex RGBA (only packed for vertex-lit surfaces)
        public readonly List<int> Indices = new();
    }

    /// <summary>
    /// Build the map's render geometry. Renderable faces (polygon/mesh + tessellated bezier patches) are
    /// grouped into surfaces keyed by texture+lightmap; each surface becomes one <see cref="ArrayMesh"/>
    /// surface with its material assigned via the <paramref name="assets"/> facade. Faces flagged nodraw,
    /// sky-with-no-surface, or flares are skipped.
    ///
    /// <paramref name="mapName"/> (e.g. "maps/foo" or just "foo") is used only for the external-lightmap
    /// fallback (<c>lm_NNNN</c>) when the BSP carries no internal lightmap pages; pass empty to disable it.
    ///
    /// <paramref name="droppedSubmodels"/> (optional) names inline-model indices the active gametype filters
    /// out (see <see cref="XonoticGodot.Engine.Collision.MapEntityFilter.DroppedSubmodels"/>): the faces owned by
    /// those <c>"*N"</c> brush entities are skipped so a gametype-conditional barrier doesn't render in a
    /// gametype it doesn't belong to. Null/empty → render every face (prior behavior).
    /// </summary>
    public static Node3D BuildMap(BspData bsp, AssetSystem assets, string mapName = "",
        IReadOnlySet<int>? droppedSubmodels = null)
    {
        var root = new Node3D { Name = "Map" };
        var mesh = new ArrayMesh();

        var surfaces = new Dictionary<SurfaceKey, SurfaceBuilder>();

        // Faces belonging to a filtered-out "*N" brush model (resolved from the models lump's face ranges).
        bool[]? dropFace = BuildDroppedFaceMask(bsp, droppedSubmodels);

        // Whether the map's lightmaps are paired with deluxe (light-direction) pages. BspReader can't resolve
        // this for an external-lightmap map (no VFS at parse time), so refine the decision here (see
        // EffectiveDeluxemapped). Computed once and threaded into the per-surface material build.
        bool deluxe = EffectiveDeluxemapped(bsp, assets, mapName);

        // --- bucket polygon/mesh faces ---
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];

            if (face.Type != BspFaceType.Flat && face.Type != BspFaceType.Mesh)
                continue; // patches handled below; flares have no geometry
            if (face.IndexCount <= 0 || face.VertexCount <= 0)
                continue;
            if (dropFace is not null && dropFace[fi])
                continue; // gametype-filtered brush entity
            if (ShouldSkip(bsp, assets, face.TextureIndex))
                continue;

            int lm = LightmapKeyForFace(bsp, assets, face);
            SurfaceBuilder sb = GetSurface(surfaces, face.TextureIndex, lm);
            AppendPolygonFace(sb, bsp, face);
        }

        // --- bucket bezier patches (tessellated) into the same surface keys ---
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;
            if (dropFace is not null && dropFace[fi])
                continue; // gametype-filtered brush entity
            if (ShouldSkip(bsp, assets, face.TextureIndex))
                continue;

            BezierPatch.Tessellation? tess = BezierPatch.Tessellate(face, bsp.Vertices);
            if (tess is null)
                continue;

            int lm = LightmapKeyForFace(bsp, assets, face);
            SurfaceBuilder sb = GetSurface(surfaces, face.TextureIndex, lm);
            AppendTessellation(sb, tess);
        }

        // --- pack each surface into the mesh and assign its material ---
        int surfaceIndex = 0;
        int nLit = 0, nLitMissing = 0, nVtx = 0, nPlain = 0, nGlow = 0, nTrans = 0; // surface-material tally (logged below)
        foreach (var kv in surfaces)
        {
            SurfaceBuilder sb = kv.Value;
            if (sb.Indices.Count == 0 || sb.Positions.Count == 0)
                continue;

            bool lightmapped = kv.Key.LightmapIndex >= 0;
            bool vertexLit = kv.Key.LightmapIndex == VertexLitKey;
            // The lightmap shader reads a per-surface tangent frame for the deluxemap directional diffuse term
            // (DP MODE_LIGHTDIRECTIONMAP_MODELSPACE). Generate tangents for every lightmapped surface so that
            // shader's TANGENT/BINORMAL inputs are always backed by real mesh data; on a non-deluxemapped map
            // they go unused (use_deluxemap is off) for only a little load-time work. Vertex-lit surfaces carry
            // a COLOR array instead (their modulation source); lightmapped surfaces carry UV2.
            PackSurface(mesh, sb, lightmapped, withTangents: lightmapped, withColor: vertexLit);
            Material mat = ResolveSurfaceMaterial(bsp, assets, kv.Key, mapName, deluxe);
            mesh.SurfaceSetMaterial(surfaceIndex, mat); // facade never returns null (checkerboard fallback)
            surfaceIndex++;

            // Tally for the load-time log: a surface keyed lightmapped that did NOT come back on the lightmap
            // shader means its page failed to load (the regression signature of the external-lightmap bug).
            bool onLightmapShader = mat is ShaderMaterial sm && LightmapShader.IsLightmapShader(sm.Shader);
            if (vertexLit) nVtx++;
            else if (lightmapped) { if (onLightmapShader) nLit++; else nLitMissing++; }
            else nPlain++;
            if (onLightmapShader && mat is ShaderMaterial g &&
                g.GetShaderParameter(LightmapShader.UseGlowUniform).AsBool()) nGlow++;
            // Translucent (alpha-blended) lightmap surfaces — glass etc. (Q3 blendFunc blend). A regression
            // that re-opaques these (e.g. losing the LightmapDiffuse.Translucent thread) drops this to 0.
            if (mat is ShaderMaterial tr && tr.Shader == LightmapShader.TranslucentShader) nTrans++;
        }

        // Surface-material summary — guards the external-lightmap wiring (lightmapped=0 or a non-zero
        // lightmapMissing on a stock map means lightmaps stopped binding; see LoadLightmap / the map-name thread).
        GD.Print($"[MapLoader] '{mapName}' surfaces: lightmapped={nLit} vertexLit={nVtx} plain={nPlain}" +
                 (nLitMissing > 0 ? $" lightmapMissing={nLitMissing}" : string.Empty) +
                 (nTrans > 0 ? $" translucent={nTrans}" : string.Empty) +
                 $" glow={nGlow} (deluxe={deluxe}, internalPages={bsp.Lightmaps.Length})");

        var mi = new MeshInstance3D { Name = "Geometry", Mesh = mesh };
        root.AddChild(mi);
        return root;
    }

    /// <summary>
    /// Mark every render face that belongs to a filtered-out inline brush model. The BSP models lump gives each
    /// <c>"*N"</c> model a contiguous face range <c>[FirstFace, FirstFace+FaceCount)</c>; a face index in a
    /// dropped model's range is masked so <see cref="BuildMap"/> skips it. Returns null when nothing is dropped
    /// (the common case) so the per-face check is free. Model 0 (worldspawn) is never dropped.
    /// </summary>
    private static bool[]? BuildDroppedFaceMask(BspData bsp, IReadOnlySet<int>? droppedSubmodels)
    {
        if (droppedSubmodels is null || droppedSubmodels.Count == 0 || bsp.Models.Length == 0)
            return null;

        var mask = new bool[bsp.Faces.Length];
        bool any = false;
        foreach (int mi in droppedSubmodels)
        {
            if (mi < 1 || mi >= bsp.Models.Length)
                continue;
            BspModel m = bsp.Models[mi];
            int end = m.FirstFace + m.FaceCount;
            for (int fi = m.FirstFace; fi < end; fi++)
                if (fi >= 0 && fi < mask.Length)
                {
                    mask[fi] = true;
                    any = true;
                }
        }
        return any ? mask : null;
    }

    private static SurfaceBuilder GetSurface(Dictionary<SurfaceKey, SurfaceBuilder> surfaces, int texture, int lightmap)
    {
        // LightmapKeyForFace already returns the canonical key (>=0 page slot, -1 unlit, or VertexLitKey),
        // so pass it through verbatim — do NOT collapse negatives, that would merge vertex-lit with unlit.
        var key = new SurfaceKey(texture, lightmap);
        if (!surfaces.TryGetValue(key, out var sb))
        {
            sb = new SurfaceBuilder();
            surfaces[key] = sb;
        }
        return sb;
    }

    /// <summary>Append one polygon/mesh face's vertex window + index window into a surface (Quake->Godot).</summary>
    private static void AppendPolygonFace(SurfaceBuilder sb, BspData bsp, BspFace face)
    {
        int baseIndex = sb.Positions.Count;

        // Copy this face's vertex range [FirstVertex, FirstVertex+VertexCount).
        for (int v = 0; v < face.VertexCount; v++)
        {
            int src = face.FirstVertex + v;
            if (src < 0 || src >= bsp.Vertices.Length)
            {
                sb.Positions.Add(Vector3.Zero);
                sb.Normals.Add(Vector3.Up);
                sb.Uvs.Add(Vector2.Zero);
                sb.Uv2.Add(Vector2.Zero);
                sb.Colors.Add(Colors.White); // keep COLOR aligned with the other channels
                continue;
            }
            BspVertex bv = bsp.Vertices[src];
            sb.Positions.Add(Coords.ToGodot(bv.Position));
            sb.Normals.Add(Coords.ToGodot(bv.Normal));
            sb.Uvs.Add(new Vector2(bv.TexCoord.X, bv.TexCoord.Y));
            sb.Uv2.Add(new Vector2(bv.LightmapCoord.X, bv.LightmapCoord.Y));
            sb.Colors.Add(new Color(bv.Color.R / 255f, bv.Color.G / 255f, bv.Color.B / 255f, bv.Color.A / 255f));
        }

        // Indices are mesh-local into Triangles, relative to FirstVertex; rebase onto baseIndex.
        int end = face.FirstIndex + face.IndexCount;
        for (int e = face.FirstIndex; e < end; e++)
        {
            if (e < 0 || e >= bsp.Triangles.Length)
                continue;
            int local = bsp.Triangles[e]; // 0-based within this face's vertex window
            if (local < 0 || local >= face.VertexCount)
                local = 0;
            sb.Indices.Add(baseIndex + local);
        }
    }

    /// <summary>Append a tessellated bezier patch (Quake-space vertices) into a surface (Quake->Godot).</summary>
    private static void AppendTessellation(SurfaceBuilder sb, BezierPatch.Tessellation tess)
    {
        int baseIndex = sb.Positions.Count;

        for (int i = 0; i < tess.Vertices.Count; i++)
        {
            BezierPatch.PatchVertex pv = tess.Vertices[i];
            sb.Positions.Add(Coords.ToGodot(pv.Position));
            sb.Normals.Add(Coords.ToGodot(pv.Normal));
            sb.Uvs.Add(new Vector2(pv.TexCoord.X, pv.TexCoord.Y));
            sb.Uv2.Add(new Vector2(pv.LightmapCoord.X, pv.LightmapCoord.Y));
            sb.Colors.Add(Colors.White); // patches carry no per-vertex color; a vertex-lit patch renders fullbright
        }

        for (int i = 0; i < tess.Indices.Count; i++)
            sb.Indices.Add(baseIndex + tess.Indices[i]);
    }

    /// <summary>
    /// Add the accumulated buffers as one ArrayMesh surface (UV2 only when lightmapped). When
    /// <paramref name="withTangents"/> is set, a per-vertex tangent array is generated and attached so the
    /// lightmap shader can rotate the deluxe light direction into the surface tangent frame (DP
    /// MODE_LIGHTDIRECTIONMAP_MODELSPACE); every lightmapped surface requests it, and the deluxe diffuse
    /// path is its only consumer.
    /// </summary>
    private static void PackSurface(ArrayMesh mesh, SurfaceBuilder sb, bool lightmapped,
        bool withTangents = false, bool withColor = false)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = sb.Positions.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = sb.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = sb.Uvs.ToArray();
        if (lightmapped)
            arrays[(int)Mesh.ArrayType.TexUV2] = sb.Uv2.ToArray();
        if (withTangents)
            arrays[(int)Mesh.ArrayType.Tangent] = BuildTangents(sb);
        if (withColor)
            arrays[(int)Mesh.ArrayType.Color] = sb.Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = sb.Indices.ToArray();

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>
    /// Build Godot's packed per-vertex tangent array (4 floats/vertex: xyz tangent + handedness w) for a
    /// lightmapped surface. The lightmap shader needs the tangent frame (DP VectorS/T/R =
    /// tangent/binormal/normal) to rotate the modelspace deluxe light direction into tangentspace and apply
    /// the <c>dot(N, lightdir)</c> diffuse term (<c>shader_glsl.h</c> MODE_LIGHTDIRECTIONMAP_MODELSPACE).
    ///
    /// Tangents are accumulated per triangle from the albedo-UV gradient (Lengyel's method) and then
    /// Gram-Schmidt orthonormalized against the vertex normal, so {tangent, binormal, normal} is an
    /// orthonormal basis. For a flat surface (no per-surface normalmap) only the normal axis affects the
    /// shaded result, so the in-plane tangent direction is not critical — but a valid, orthonormal frame is.
    /// All inputs are already Godot-space (positions/normals were converted at append time).
    /// </summary>
    private static float[] BuildTangents(SurfaceBuilder sb)
    {
        int n = sb.Positions.Count;
        var tangentAccum = new Vector3[n];   // ∂P/∂u direction, summed over adjacent triangles
        var bitangentAccum = new Vector3[n]; // ∂P/∂v direction, summed (used only for the handedness sign)

        for (int i = 0; i + 2 < sb.Indices.Count; i += 3)
        {
            int i0 = sb.Indices[i], i1 = sb.Indices[i + 1], i2 = sb.Indices[i + 2];
            Vector3 e1 = sb.Positions[i1] - sb.Positions[i0];
            Vector3 e2 = sb.Positions[i2] - sb.Positions[i0];
            Vector2 du1 = sb.Uvs[i1] - sb.Uvs[i0];
            Vector2 du2 = sb.Uvs[i2] - sb.Uvs[i0];

            float det = du1.X * du2.Y - du2.X * du1.Y;
            if (Mathf.Abs(det) < 1e-12f)
                continue; // degenerate UV mapping on this triangle; neighbors define the vertex tangent
            float r = 1.0f / det;
            Vector3 t = (e1 * du2.Y - e2 * du1.Y) * r;
            Vector3 b = (e2 * du1.X - e1 * du2.X) * r;

            tangentAccum[i0] += t; tangentAccum[i1] += t; tangentAccum[i2] += t;
            bitangentAccum[i0] += b; bitangentAccum[i1] += b; bitangentAccum[i2] += b;
        }

        var tangents = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            Vector3 nrm = sb.Normals[v];
            // Gram-Schmidt: project the accumulated tangent onto the plane perpendicular to the normal.
            Vector3 t = tangentAccum[v] - nrm * nrm.Dot(tangentAccum[v]);
            if (t.LengthSquared() < 1e-12f)
            {
                // No usable UV gradient here (flat-shaded/degenerate): synthesize any in-plane tangent so the
                // frame stays orthonormal. The shaded result for a flat surface does not depend on which one.
                Vector3 axis = Mathf.Abs(nrm.Z) < 0.99f ? Vector3.Back : Vector3.Right;
                t = nrm.Cross(axis);
            }
            t = t.Normalized();
            // Handedness w so Godot reconstructs BINORMAL = cross(NORMAL, TANGENT) * w in the UV-v direction.
            float w = nrm.Cross(t).Dot(bitangentAccum[v]) < 0.0f ? -1.0f : 1.0f;
            tangents[v * 4 + 0] = t.X;
            tangents[v * 4 + 1] = t.Y;
            tangents[v * 4 + 2] = t.Z;
            tangents[v * 4 + 3] = w;
        }
        return tangents;
    }

    /// <summary>
    /// Resolve the material for a surface. Lightmapped surfaces get a lightmap shader material built from
    /// the page texture (internal page uploaded as an <see cref="ImageTexture"/>, or an external
    /// <c>lm_NNNN</c> image) combined with the shader's albedo. Non-lightmapped surfaces resolve straight
    /// from the shader name. Falls back to a plain shader-name material if a lightmap texture is missing.
    ///
    /// On a deluxemapped map the matching deluxe page is also bound so the lightmap shader can apply the
    /// directional re-modulation (DP MODE_LIGHTDIRECTIONMAP_MODELSPACE). A static <c>tcMod scale</c> on the
    /// albedo stage (e.g. <c>map_catharsis/chain</c> — <c>tcMod scale 2 2</c>) is folded into the albedo UV
    /// scale (DP Q3TCMOD_SCALE), leaving the baked lightmap UV2 untouched.
    /// </summary>
    private static Material ResolveSurfaceMaterial(BspData bsp, AssetSystem assets, SurfaceKey key, string mapName, bool deluxe)
    {
        string shaderName = (key.TextureIndex >= 0 && key.TextureIndex < bsp.Textures.Length)
            ? bsp.Textures[key.TextureIndex].ShaderName
            : string.Empty;

        if (key.LightmapIndex == VertexLitKey)
        {
            // q3map2 vertex-lit surface (negative lightmap index, no .shader entry): modulate the diffuse by
            // the per-vertex COLOR, unshaded (DP MODE_VERTEXCOLOR). No shader exists for this name — that is
            // what classified it vertex-lit — so the albedo is the texture of that name; a null albedo falls
            // back to white (pure vertex lighting, the same as a missing diffuse on the lightmap path).
            return LightmapShader.MakeVertexLitMaterial(assets.LoadTexture(shaderName));
        }

        if (key.LightmapIndex >= 0)
        {
            Texture2D? lightmapTex = LoadLightmap(bsp, assets, key.LightmapIndex, mapName, deluxe);
            if (lightmapTex is not null)
            {
                // Albedo resolves through the shader's DIFFUSE STAGE, not a bare LoadTexture(shaderName): a Q3
                // shader's color image lives in a stage, so a shadered surface would otherwise come back white
                // (untextured). This also carries the diffuse stage's alpha-test cutoff (masked grates/foliage)
                // and static tcMod scale (DP Q3TCMOD_SCALE). A pure-shader/$lightmap surface yields a null
                // texture → the lightmap shader falls back to white (lighting only, no diffuse).
                AssetSystem.LightmapDiffuse diffuse = assets.ResolveLightmapDiffuse(shaderName);

                // Deluxemapped maps: bind the matching light-direction page so the lightmap shader applies
                // the 1/max(0.25, lightnormal.z) directional rescale (DP shader_glsl.h). Faces address the
                // de-interleaved Deluxemaps array with the same (already-halved) index as Lightmaps.
                Texture2D? deluxeTex = LoadDeluxemap(bsp, assets, key.LightmapIndex, mapName, deluxe);

                // Built directly (not via the AssetSystem facade) so the deluxe page / UV scale / alpha cutoff
                // / glow page / translucency reach the lightmap shader without widening the facade signature.
                // A blendFunc-blend diffuse (glass) routes to the translucent variant so it renders see-through.
                return LightmapShader.MakeMaterial(diffuse.Texture, lightmapTex, deluxemap: deluxeTex,
                    albedoUvScale: diffuse.UvScale, alphaCutoff: diffuse.AlphaCutoff, glow: diffuse.Glow,
                    translucent: diffuse.Translucent);
            }
            // No lightmap available — degrade to the plain material rather than dropping the surface.
        }

        return assets.ResolveMaterial(shaderName);
    }

    /// <summary>
    /// Get the lightmap page texture for the de-interleaved slot <paramref name="lightmapIndex"/>: upload the
    /// internal 128x128 RGB page as an <see cref="ImageTexture"/>, or — when the BSP has no internal pages —
    /// try the external <c>maps/&lt;mapname&gt;/lm_NNNN</c> image. Returns null if neither is available (clean
    /// fallback). Pages are cached on the <see cref="BspData"/> instance so repeated surfaces reuse one texture.
    ///
    /// On a deluxemapped map the on-disk <c>lm_NNNN</c> files still interleave lightmap/deluxe pages, so the
    /// de-interleaved slot <c>k</c> maps to file <c>lm_{2k}</c> (the even page) — mirroring DP, which merges the
    /// external set the same way it merges the internal lump (<c>Mod_Q3BSP_LoadLightmaps</c>).
    /// </summary>
    private static Texture2D? LoadLightmap(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName, bool deluxe)
    {
        // Internal lightmap pages.
        if (bsp.Lightmaps.Length > 0)
        {
            if (lightmapIndex < 0 || lightmapIndex >= bsp.Lightmaps.Length)
                return null;
            if (_lightmapCache.TryGetValue((bsp, lightmapIndex), out Texture2D? cached))
                return cached;

            ImageTexture tex = UploadLightmapPage(bsp.Lightmaps[lightmapIndex], bsp.LightmapWidth, bsp.LightmapHeight);
            _lightmapCache[(bsp, lightmapIndex)] = tex;
            return tex;
        }

        // External lm_NNNN images (only if we know the map name and the facade can find them).
        if (!string.IsNullOrEmpty(mapName))
        {
            // Deluxemapped external set: lightmap slot k is the even file lm_{2k} (odd files are deluxe).
            int fileIndex = deluxe ? lightmapIndex * 2 : lightmapIndex;
            return assets.LoadTexture($"maps/{ExternalLightmapBaseName(mapName)}/lm_{fileIndex:0000}");
        }

        return null;
    }

    /// <summary>Reduce a map name ("maps/foo", "foo", or a path with extension) to the bare map basename.</summary>
    private static string ExternalLightmapBaseName(string mapName)
    {
        string baseName = mapName;
        int slash = baseName.Replace('\\', '/').LastIndexOf('/');
        if (slash >= 0)
            baseName = baseName[(slash + 1)..];
        int dot = baseName.LastIndexOf('.');
        if (dot >= 0)
            baseName = baseName[..dot];
        return baseName;
    }

    /// <summary>
    /// Get the deluxe (light-direction) page that pairs with the de-interleaved slot
    /// <paramref name="lightmapIndex"/>, or null when the map is not deluxemapped. The slot index matches the
    /// (already-halved) lightmap index because <see cref="BspData.Deluxemaps"/> is de-interleaved in lockstep
    /// with <see cref="BspData.Lightmaps"/> (DP <c>Mod_Q3BSP_LoadLightmaps</c>: even page = lightmap, odd =
    /// deluxe). Internal pages upload from the byte buffer; an external deluxemapped set reads file
    /// <c>lm_{2k+1}</c> (the odd page) through the VFS. Uploaded as plain RGB (a packed direction, not a
    /// color), cached per slot.
    /// </summary>
    private static Texture2D? LoadDeluxemap(BspData bsp, AssetSystem assets, int lightmapIndex, string mapName, bool deluxe)
    {
        if (!deluxe)
            return null;
        if (lightmapIndex < 0)
            return null;
        if (_deluxemapCache.TryGetValue((bsp, lightmapIndex), out Texture2D? cached))
            return cached;

        // Internal deluxe pages (de-interleaved out of the lightmap lump).
        if (bsp.Deluxemaps.Length > 0)
        {
            if (lightmapIndex >= bsp.Deluxemaps.Length)
                return null;
            byte[]? page = bsp.Deluxemaps[lightmapIndex];
            if (page is null)
                return null;
            ImageTexture tex = UploadLightmapPage(page, bsp.LightmapWidth, bsp.LightmapHeight);
            _deluxemapCache[(bsp, lightmapIndex)] = tex;
            return tex;
        }

        // External deluxemapped set: deluxe slot k is the odd file lm_{2k+1}.
        if (!string.IsNullOrEmpty(mapName))
        {
            Texture2D? ext = assets.LoadTexture(
                $"maps/{ExternalLightmapBaseName(mapName)}/lm_{lightmapIndex * 2 + 1:0000}");
            _deluxemapCache[(bsp, lightmapIndex)] = ext;
            return ext;
        }

        return null;
    }

    /// <summary>Upload a raw 128x128 RGB lightmap page to an <see cref="ImageTexture"/> (RGB8, no mips).</summary>
    private static ImageTexture UploadLightmapPage(byte[] page, int width, int height)
    {
        // BSP pages are tightly-packed RGB (3 bytes/texel); Godot's Rgb8 format matches that layout.
        int needed = width * height * 3;
        byte[] data = page;
        if (page.Length != needed)
        {
            // Pad/clip defensively so a short/long page still produces a valid texture.
            data = new byte[needed];
            Array.Copy(page, data, Math.Min(page.Length, needed));
        }

        Image img = Image.CreateFromData(width, height, false, Image.Format.Rgb8, data);
        return ImageTexture.CreateFromImage(img);
    }

    // Cache of uploaded internal lightmap pages, keyed by (map, page index), so the many surfaces that
    // share one page reuse a single GPU texture rather than re-uploading the 48 KB image each time.
    private static readonly Dictionary<(BspData, int), Texture2D?> _lightmapCache = new();

    // Same idea for the de-interleaved deluxe (light-direction) pages on a deluxemapped map.
    private static readonly Dictionary<(BspData, int), Texture2D?> _deluxemapCache = new();

    /// <summary>
    /// The effective "is this map deluxemapped" decision, refined for external-lightmap maps that
    /// <see cref="XonoticGodot.Formats.Bsp.BspReader"/> could not resolve at parse time (no VFS there, so it can't
    /// probe the external pages). DP detects deluxemapping on the external set the same way as the internal
    /// lump: an even page count whose faces only reference even lightmap indices, minus the q3map2 "blank
    /// padding" special case. For a single-lightmap external map (faces index only slot 0) the count-1-vs-2
    /// case is ambiguous without the files, so BspReader conservatively reports "not deluxemapped"; here we have
    /// the VFS, so probe whether the <c>lm_0001</c> page exists and is a real (non-blank) deluxe page and
    /// upgrade the decision to match DP.
    ///
    /// Only the single-lightmap (max referenced slot 0) case can be upgraded — a map with a referenced slot
    /// >= 2 or any odd index was already decided authoritatively by BspReader — and for slot 0 the
    /// <see cref="BspData.RealLightmapIndex"/> halving is a no-op, so this refinement only governs whether the
    /// deluxe page is bound, never the lightmap slot a face resolves to.
    /// </summary>
    private static bool EffectiveDeluxemapped(BspData bsp, AssetSystem assets, string mapName)
    {
        if (bsp.IsDeluxemapped)
            return true;
        if (bsp.Lightmaps.Length != 0)
            return false;                       // internal-page map: BspReader's decision was authoritative
        if (string.IsNullOrEmpty(mapName))
            return false;
        if (!AllFaceLightmapIndicesEven(bsp))
            return false;                       // an odd lightmap index means lm_0001 is a real lightmap page
        // DP keeps deluxemapping when the paired page exists and is not a blank q3map2 pad.
        Texture2D? d1 = assets.LoadTexture($"maps/{ExternalLightmapBaseName(mapName)}/lm_0001");
        return d1 is not null && !IsTextureNearBlack(d1);
    }

    /// <summary>True if no face references an ODD lightmap index (which would make lm_0001 a real lightmap page,
    /// not a deluxe page). Negative indices (unlit / vertex-lit) are ignored.</summary>
    private static bool AllFaceLightmapIndicesEven(BspData bsp)
    {
        foreach (BspFace f in bsp.Faces)
            if (f.LightmapIndex >= 0 && (f.LightmapIndex & 1) != 0)
                return false;
        return true;
    }

    /// <summary>
    /// True if a candidate deluxe page is effectively all-black — q3map2's blank padding rather than a real
    /// light-direction page (a real deluxe page encodes directions around (128,128,255)). Sampled sparsely; an
    /// unreadable image is assumed real (returns false) so a decode quirk never silently drops deluxe lighting.
    /// </summary>
    private static bool IsTextureNearBlack(Texture2D tex)
    {
        Image img = tex.GetImage();
        if (img is null)
            return false;
        if (img.IsCompressed())
            img.Decompress();
        int w = img.GetWidth(), h = img.GetHeight();
        if (w <= 0 || h <= 0)
            return false;
        int step = Math.Max(1, Math.Min(w, h) / 16);
        for (int y = 0; y < h; y += step)
        for (int x = 0; x < w; x += step)
        {
            Color c = img.GetPixel(x, y);
            if (c.R > 0.02f || c.G > 0.02f || c.B > 0.02f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True if the face's texture should be skipped from the render mesh: NODRAW (caulk/clip/trigger
    /// shaders) and sky draw nothing here. The decision unions two authorities — the Q3 surface bits the
    /// reader stored on <see cref="BspTexture.SurfaceFlags"/>, AND the shader's <c>surfaceparm</c> set
    /// (resolved through <paramref name="assets"/>), because many Xonotic surfaces declare
    /// <c>nodraw</c>/<c>sky</c> only in the <c>.shader</c> with no BSP bit set.
    /// </summary>
    private static bool ShouldSkip(BspData bsp, AssetSystem assets, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;

        // BSP lump bits.
        int flags = bsp.Textures[textureIndex].SurfaceFlags;
        const int skipMask = Q3SurfaceFlags.NoDraw | Q3SurfaceFlags.Sky;
        if ((flags & skipMask) != 0)
            return true;

        // Shader surfaceparm bits (nodraw / sky).
        SurfaceFlags.SurfaceInfo info = assets.GetSurfaceInfo(bsp.Textures[textureIndex].ShaderName);
        return info.NoDraw || info.Sky;
    }

    /// <summary>
    /// The de-interleaved lightmap page a face renders with, or -1 if it should render unlit (no lightmap).
    /// Returns -1 when the face has no valid <see cref="BspFace.LightmapIndex"/> OR the shader suppresses
    /// lightmaps (<c>surfaceparm nolightmap</c>/sky/translucent/fullbright) — those surfaces must not split
    /// into a lightmap material, and feeding them a page would double-light.
    ///
    /// On a deluxemapped map the face's stored index counts both lightmap and deluxe pages, so it is halved
    /// (<see cref="BspData.RealLightmapIndex"/>) to address the de-interleaved <see cref="BspData.Lightmaps"/>
    /// array — this is what keeps the light-direction pages out of the lightmap atlas.
    /// </summary>
    private static int LightmapForFace(BspData bsp, AssetSystem assets, BspFace face)
    {
        if (face.LightmapIndex < 0)
            return -1;
        if (face.TextureIndex >= 0 && face.TextureIndex < bsp.Textures.Length)
        {
            SurfaceFlags.SurfaceInfo info = assets.GetSurfaceInfo(bsp.Textures[face.TextureIndex].ShaderName);
            if (info.NoLightmap)
                return -1;
        }
        return bsp.RealLightmapIndex(face.LightmapIndex);
    }

    /// <summary>
    /// The bucketing key for a face's lighting: a real de-interleaved lightmap slot (>= 0), the vertex-lit
    /// sentinel (<see cref="VertexLitKey"/>), or -1 (unlit / shader-owned). A face with no lightmap page that
    /// is a plain default-shaded world surface (no <c>.shader</c> entry) is vertex-lit — q3map2 baked its
    /// lighting into the per-vertex RGB (DP MODE_VERTEXCOLOR) — so it must not be dropped into a realtime-lit
    /// StandardMaterial3D; surfaces WITH a shader keep that shader (it carries its own rgbGen/lighting).
    /// </summary>
    private static int LightmapKeyForFace(BspData bsp, AssetSystem assets, BspFace face)
    {
        int lm = LightmapForFace(bsp, assets, face);
        if (lm >= 0)
            return lm;
        return IsVertexLitCandidate(bsp, assets, face) ? VertexLitKey : -1;
    }

    /// <summary>
    /// True if a face with no lightmap page should render vertex-lit (DP MODE_VERTEXCOLOR): it has no
    /// <c>.shader</c> entry, so it falls on Q3's default shading where, lacking a lightmap, the per-vertex RGB
    /// is the lighting. Sky/nodraw faces are already removed by <see cref="ShouldSkip"/> before bucketing, so
    /// the only discriminator left is whether a shader owns the surface's coloring.
    /// </summary>
    private static bool IsVertexLitCandidate(BspData bsp, AssetSystem assets, BspFace face)
    {
        if (face.TextureIndex < 0 || face.TextureIndex >= bsp.Textures.Length)
            return false;
        return assets.GetShader(bsp.Textures[face.TextureIndex].ShaderName) is null;
    }

    // -------------------------------------------------------------------------------------------------
    //  Collision
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Convert every BSP brush into an engine <see cref="Brush"/> and build the broadphase grid. Each
    /// brush side's plane comes from <see cref="BspData.Planes"/>; the brush carries the content flags
    /// from its texture entry (lava/slime/clip/solid) which drive the trace's SUPERCONTENTS mask. Brushes
    /// with no derivable geometry (fewer than 4 valid planes / degenerate windings) are skipped.
    /// Everything is in Quake coords — the trace service consumes it directly.
    ///
    /// <paramref name="assets"/> is accepted for signature symmetry (surface-flag lookups, future shader
    /// content overrides); brush contents are the BSP texture's RAW Q3 native flags converted to SUPERCONTENTS
    /// inside <see cref="BspCollisionBuilder"/> (DP's Mod_Q3BSP_SuperContentsFromNativeContents) — NOT the raw
    /// on-disk bits, which would alias the trace masks.
    /// </summary>
    public static CollisionWorld BuildCollision(BspData bsp, AssetSystem? assets = null)
    {
        _ = assets;
        // The brush-building moved to the Godot-free engine layer (BspCollisionBuilder) so the headless server
        // and unit tests can build collision without Godot. This returns only the static world (worldspawn);
        // the inline "*N" brush models are registered separately via BspCollisionBuilder.RegisterSubmodels so
        // SOLID_BSP entities (doors/plats) clip against their real moving brushes instead of an AABB.
        return BspCollisionBuilder.Build(bsp).World;
    }

    /// <summary>
    /// Build a secondary Godot collision body from the render triangles (a single concave trimesh). This
    /// is optional and independent of the engine <see cref="CollisionWorld"/> — it exists only for Godot's
    /// own physics (e.g. ragdolls, dropped pickups, area overlaps) that want a <see cref="StaticBody3D"/>
    /// in the scene tree. Sky/nodraw faces are skipped; patches are included (tessellated). Returns a
    /// <see cref="StaticBody3D"/> ready to add to the scene, or null if there is nothing to collide.
    /// </summary>
    public static StaticBody3D? BuildCollisionMesh(BspData bsp)
    {
        var faces = new List<Vector3>();

        // Polygon/mesh faces: expand each index into a triangle vertex (concave shapes take a flat soup).
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Flat && face.Type != BspFaceType.Mesh)
                continue;
            if (ShouldSkipCollision(bsp, face.TextureIndex))
                continue;

            int end = face.FirstIndex + face.IndexCount;
            for (int e = face.FirstIndex; e < end; e++)
            {
                if (e < 0 || e >= bsp.Triangles.Length)
                    continue;
                int local = bsp.Triangles[e];
                int src = face.FirstVertex + local;
                if (src < 0 || src >= bsp.Vertices.Length)
                    continue;
                faces.Add(Coords.ToGodot(bsp.Vertices[src].Position));
            }
        }

        // Bezier patches: triangulated.
        for (int fi = 0; fi < bsp.Faces.Length; fi++)
        {
            BspFace face = bsp.Faces[fi];
            if (face.Type != BspFaceType.Patch)
                continue;
            if (ShouldSkipCollision(bsp, face.TextureIndex))
                continue;
            BezierPatch.Tessellation? tess = BezierPatch.Tessellate(face, bsp.Vertices);
            if (tess is null)
                continue;
            for (int i = 0; i < tess.Indices.Count; i++)
                faces.Add(Coords.ToGodot(tess.Vertices[tess.Indices[i]].Position));
        }

        if (faces.Count < 3)
            return null;

        var shape = new ConcavePolygonShape3D { Data = faces.ToArray() };
        var body = new StaticBody3D { Name = "MapCollision" };
        body.AddChild(new CollisionShape3D { Name = "Trimesh", Shape = shape });
        return body;
    }

    /// <summary>Trimesh skip mask: drop nodraw and non-solid hint surfaces (but keep sky as a wall).</summary>
    private static bool ShouldSkipCollision(BspData bsp, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= bsp.Textures.Length)
            return false;
        return (bsp.Textures[textureIndex].SurfaceFlags & (Q3SurfaceFlags.NoDraw | Q3SurfaceFlags.NonSolid)) != 0;
    }

    /// <summary>
    /// Compute the corner vertices of a convex brush from its bounding planes: intersect every triple of
    /// planes and keep the points that lie inside (or on) all planes. This is the standard Quake
    /// brush-winding derivation; the trace service uses these points for its SAT projections.
    /// </summary>
    // -------------------------------------------------------------------------------------------------
    //  Spawn points
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parse player spawn entities from the entity lump. Matches any classname beginning with
    /// <c>info_player_</c> (covers <c>info_player_deathmatch</c>, <c>info_player_start</c>,
    /// <c>info_player_team*</c>). Yields each spawn's Quake-space origin and yaw angle (the entity
    /// <c>"angle"</c> key, default 0). Origin/angle stay in Quake coords for the sim.
    /// </summary>
    public static IEnumerable<(string classname, NVec3 origin, float angle)> SpawnPoints(BspData bsp)
    {
        foreach (var ent in bsp.Entities)
        {
            if (!ent.TryGetValue("classname", out string? classname) || classname is null)
                continue;
            if (!classname.StartsWith("info_player_", StringComparison.Ordinal))
                continue;

            NVec3 origin = NVec3.Zero;
            if (ent.TryGetValue("origin", out string? originStr) && originStr is not null)
                origin = ParseVec3(originStr);

            float angle = 0f;
            if (ent.TryGetValue("angle", out string? angleStr) && angleStr is not null)
                angle = ParseFloat(angleStr);

            yield return (classname, origin, angle);
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Worldspawn
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Map-wide environment settings pulled from the <c>worldspawn</c> entity (the first entity in the
    /// lump). All fields are optional; unset keys keep their defaults. Colors are linear RGB 0..1, the
    /// gravity is the Quake scalar (Xonotic default 800), and <see cref="Sky"/> is the skybox basename.
    /// </summary>
    public readonly struct Worldspawn
    {
        public readonly string Message;       // map title ("message")
        public readonly string Sky;           // skybox basename ("sky" / "_skybox" / "skyname")
        public readonly float Gravity;        // "gravity" (default 800)
        public readonly bool HasFog;
        public readonly Color FogColor;       // parsed from "fog" (r g b ...)
        public readonly float FogDensity;     // density term from "fog"

        public Worldspawn(string message, string sky, float gravity, bool hasFog, Color fogColor, float fogDensity)
        {
            Message = message;
            Sky = sky;
            Gravity = gravity;
            HasFog = hasFog;
            FogColor = fogColor;
            FogDensity = fogDensity;
        }
    }

    /// <summary>
    /// Read the worldspawn entity for environment keys (sky, gravity, fog, title). Quake/Xonotic store the
    /// skybox under <c>sky</c>/<c>_skybox</c>/<c>skyname</c>, gravity under <c>gravity</c>, and a fog
    /// directive under <c>fog</c> as <c>"density r g b ..."</c> (DP's R_GetFogFromShader-style string).
    /// Returns defaults (gravity 800, no fog) when worldspawn is absent.
    /// </summary>
    public static Worldspawn BuildWorldspawn(BspData bsp)
    {
        string message = string.Empty;
        string sky = string.Empty;
        float gravity = 800f;
        bool hasFog = false;
        Color fogColor = new Color(0.3f, 0.3f, 0.3f);
        float fogDensity = 0f;

        if (bsp.Entities.Count > 0)
        {
            IReadOnlyDictionary<string, string> ws = FindWorldspawn(bsp);

            if (ws.TryGetValue("message", out string? m) && m is not null)
                message = m;

            sky = FirstNonEmpty(ws, "sky", "_skybox", "skyname", "skybox");

            if (ws.TryGetValue("gravity", out string? g) && g is not null)
            {
                float parsed = ParseFloat(g);
                if (parsed > 0f)
                    gravity = parsed;
            }

            if (ws.TryGetValue("fog", out string? fog) && !string.IsNullOrWhiteSpace(fog))
            {
                // Xonotic/DP fog string: "<density> <r> <g> <b> [...]" (density first, then color 0..1).
                var parts = fog.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    hasFog = true;
                    fogDensity = ParseFloat(parts[0]);
                    if (parts.Length >= 4)
                        fogColor = new Color(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
                }
            }
        }

        return new Worldspawn(message, sky, gravity, hasFog, fogColor, fogDensity);
    }

    /// <summary>Find the worldspawn entity (by classname; falls back to the first entity, Quake convention).</summary>
    private static IReadOnlyDictionary<string, string> FindWorldspawn(BspData bsp)
    {
        foreach (var ent in bsp.Entities)
        {
            if (ent.TryGetValue("classname", out string? cn) && cn == "worldspawn")
                return ent;
        }
        return bsp.Entities[0];
    }

    private static string FirstNonEmpty(IReadOnlyDictionary<string, string> dict, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (dict.TryGetValue(k, out string? v) && !string.IsNullOrWhiteSpace(v))
                return v;
        }
        return string.Empty;
    }

    // -------------------------------------------------------------------------------------------------
    //  Parsing helpers
    // -------------------------------------------------------------------------------------------------

    private static NVec3 ParseVec3(string s)
    {
        var parts = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 ? ParseFloat(parts[0]) : 0f;
        float y = parts.Length > 1 ? ParseFloat(parts[1]) : 0f;
        float z = parts.Length > 2 ? ParseFloat(parts[2]) : 0f;
        return new NVec3(x, y, z);
    }

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
}
