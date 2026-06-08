using System;
using System.Collections.Generic;
using Godot;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Common.Framework;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Loaders;

namespace XonoticGodot.Game.Client;

/// <summary>
/// Drives MD3 vertex-morph animation playback for a model — the Godot successor to the CSQC
/// frame/anim playback the libs flag with <c>TODO(port,client): … walk/strafe/idle/jump …</c> and
/// <c>tur_head.frame = N</c> style frame sets (players, monsters, turret heads, vehicles).
///
/// MD3 stores geometry as a stack of per-frame vertex positions (see <see cref="Md3Data"/>); there is no
/// skeleton — "animation" is selecting and blending between two frames. Godot's <see cref="AnimationPlayer"/>
/// can't morph an arbitrary ArrayMesh per-frame cheaply, so this does it directly: it keeps the parsed
/// <see cref="Md3Data"/>, advances a playhead through a frame range at a frame rate, and on each tick rebuilds
/// the <see cref="MeshInstance3D"/>'s <see cref="ArrayMesh"/> by linearly interpolating vertex positions and
/// normals between the two bracketing frames (the exact thing DarkPlaces' R_AliasLerpVerts does). Attachment
/// <see cref="Md3Tag"/> markers are re-posed the same way so weapons/effects parented to a tag track the anim.
///
/// Animations are addressed by name through a small frame-range table (<see cref="AnimClip"/>): a model that
/// ships a frame layout (idle 0-29, run 30-59, …) registers clips, then callers <see cref="Play"/> "run" /
/// "idle" / "attack". When an <see cref="Entity"/> is bound, its <see cref="Entity.Frame"/> can also directly
/// drive the playhead (the raw networked frame index), matching QC's <c>self.frame</c> path.
///
/// Coordinates convert Quake -> Godot per vertex via <see cref="Coords"/>, identical to
/// <see cref="ModelLoader.BuildModel"/> (this is effectively its animated form).
/// </summary>
public partial class ModelAnimator : Node3D
{
    /// <summary>A named animation = a contiguous MD3 frame range played at a rate, optionally looped.</summary>
    public readonly record struct AnimClip(string Name, int FirstFrame, int FrameCount, float Fps, bool Loop)
    {
        public int LastFrame => FirstFrame + Math.Max(1, FrameCount) - 1;
    }

    private Md3Data _md3 = null!;
    private AssetSystem? _assets;
    // Per-surface resolved materials (mirrors Md3Morph): re-applied on every morph rebuild in ApplyFrame.
    private readonly List<(Md3Surface surface, Material? material, bool visible)> _surfaces = new();
    private MeshInstance3D _mesh = null!;
    private Node3D? _tagsRoot;
    private readonly List<(Md3Tag tag, Marker3D marker)> _tagMarkers = new();

    private readonly Dictionary<string, AnimClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private AnimClip _current;
    private bool _hasClip;
    private float _playhead;     // fractional frame position WITHIN the current clip (0..FrameCount)
    private bool _playing;

    /// <summary>Optional bound entity: when set, <see cref="Entity.Frame"/> can drive the raw playhead.</summary>
    public Entity? Entity { get; set; }

    /// <summary>If true and an <see cref="Entity"/> is bound, follow <see cref="Entity.Frame"/> instead of a clip.</summary>
    [Export] public bool FollowEntityFrame { get; set; }

    /// <summary>
    /// When set (non-local player MD3 models — the CSQC <c>!isplayer</c> skeleton branch), the raw networked
    /// frame is routed through <see cref="CsqcFallbackFrame.Remap"/> before playback so a model missing the
    /// melee / duckwalk-variant anims falls back to a frame it has (csqcmodel_hooks.qc:702). Off by default.
    /// </summary>
    [Export] public bool UseFallbackFrame { get; set; }

    /// <summary>Global playback speed multiplier.</summary>
    [Export] public float TimeScale { get; set; } = 1f;

    /// <summary>The mesh node this animator rebuilds each tick (exposed so callers can attach materials).</summary>
    public MeshInstance3D MeshInstance => _mesh;

    /// <summary>The currently playing clip name, or "" when idle / frame-driven.</summary>
    public string CurrentClip => _hasClip ? _current.Name : "";

    // =================================================================================================
    //  Construction
    // =================================================================================================

    /// <summary>
    /// Build an animator for a parsed MD3. Creates the initial mesh (frame 0) and tag markers, and
    /// auto-registers a sensible default clip set named from the MD3's frame names when recognisable
    /// (idle/run/walk/jump/death/attack), so a model is animatable out of the box.
    /// </summary>
    public static ModelAnimator Create(Md3Data md3, string? name = null, AssetSystem? assets = null, SkinFile? skin = null)
    {
        var a = new ModelAnimator { Name = string.IsNullOrEmpty(name) ? (md3.Name.Length > 0 ? md3.Name : "Model") : name };
        a.Initialize(md3, assets, skin);
        return a;
    }

    private void Initialize(Md3Data md3, AssetSystem? assets, SkinFile? skin)
    {
        _md3 = md3 ?? throw new ArgumentNullException(nameof(md3));
        _assets = assets;
        ResolveSurfaces(skin);

        _mesh = ModelLoader.BuildModel(_md3, 0, _assets, skin);
        _mesh.Name = "Mesh";
        AddChild(_mesh);

        _tagsRoot = ModelLoader.BuildTags(_md3, 0);
        AddChild(_tagsRoot);
        foreach (Node child in _tagsRoot.GetChildren())
            if (child is Marker3D m)
            {
                Md3Tag? tag = FindTag(m.Name);
                if (tag is not null)
                    _tagMarkers.Add((tag, m));
            }

        AutoRegisterClips();

        // Default to the first registered clip (idle if present), else just hold frame 0.
        if (_clips.TryGetValue("idle", out AnimClip idle)) Play(idle.Name);
        else if (_clips.Count > 0)
        {
            foreach (AnimClip c in _clips.Values) { Play(c.Name); break; }
        }
    }

    /// <summary>
    /// Resolve each surface's material once (skin remap by mesh name, else the surface's own first shader;
    /// a nodraw remap hides it), so <see cref="ApplyFrame"/> can re-attach per-surface materials on every
    /// morph rebuild. Mirrors <c>Md3Morph.ResolveSurfaces</c>. With no <see cref="AssetSystem"/> the materials
    /// stay null (untextured — legacy behavior).
    /// </summary>
    private void ResolveSurfaces(SkinFile? skin)
    {
        foreach (Md3Surface surface in _md3.Surfaces)
        {
            string shader = surface.Shaders.Length > 0 ? surface.Shaders[0] : surface.Name;
            bool visible = true;
            if (skin is not null && skin.MeshToTexture.TryGetValue(surface.Name, out string? remap))
            {
                if (SkinFile.IsNoDraw(remap))
                    visible = false;
                else if (!string.IsNullOrEmpty(remap))
                    shader = remap;
            }
            Material? material = visible ? _assets?.ResolveMaterial(shader) : null;
            _surfaces.Add((surface, material, visible));
        }
    }

    /// <summary>Look up the attachment marker for a tag by name (for parenting weapons/effects).</summary>
    public Marker3D? GetTag(string tagName)
    {
        foreach ((Md3Tag tag, Marker3D marker) in _tagMarkers)
            if (string.Equals(tag.Name, tagName, StringComparison.Ordinal))
                return marker;
        return null;
    }

    // =================================================================================================
    //  Clip registration + playback control
    // =================================================================================================

    /// <summary>Register (or replace) a named animation clip. Frame indices are clamped to the model's range.</summary>
    public void AddClip(string name, int firstFrame, int frameCount, float fps = 10f, bool loop = true)
    {
        int max = Math.Max(1, _md3.FrameCount);
        firstFrame = Math.Clamp(firstFrame, 0, max - 1);
        frameCount = Math.Clamp(frameCount, 1, max - firstFrame);
        _clips[name] = new AnimClip(name, firstFrame, frameCount, fps <= 0f ? 10f : fps, loop);
    }

    /// <summary>Start playing a registered clip from its first frame. No-op if the name is unknown.</summary>
    public bool Play(string clipName)
    {
        if (!_clips.TryGetValue(clipName, out AnimClip clip))
            return false;
        _current = clip;
        _hasClip = true;
        _playhead = 0f;
        _playing = true;
        FollowEntityFrame = false;
        ApplyFrame(clip.FirstFrame, clip.FirstFrame, 0f);
        return true;
    }

    /// <summary>Switch clips only if a different one is requested (avoids restarting the same loop each frame).</summary>
    public bool PlayIfChanged(string clipName)
    {
        if (_hasClip && string.Equals(_current.Name, clipName, StringComparison.OrdinalIgnoreCase))
            return true;
        return Play(clipName);
    }

    public void Pause() => _playing = false;
    public void Resume() => _playing = true;

    /// <summary>Jump straight to a raw MD3 frame index (QC <c>self.frame = N</c>), holding it (no playback).</summary>
    public void SetRawFrame(int frame)
    {
        _hasClip = false;
        _playing = false;
        int f = Math.Clamp(frame, 0, Math.Max(0, _md3.FrameCount - 1));
        ApplyFrame(f, f, 0f);
    }

    // =================================================================================================
    //  Per-frame advance
    // =================================================================================================

    public override void _Process(double delta)
    {
        Advance((float)delta);
    }

    /// <summary>Advance the playhead and rebuild the morph mesh for this frame (call once per frame).</summary>
    public void Advance(float delta)
    {
        if (_md3.FrameCount <= 1)
            return;

        // Entity-frame-driven mode: take the raw networked frame as the playhead directly.
        if (FollowEntityFrame && Entity is not null)
        {
            float ef = Entity.Frame;
            int f0 = (int)MathF.Floor(ef);
            int f1 = f0 + 1;
            float t = ef - f0;
            // Non-local player MD3 models: remap a missing anim frame to a present one (CSQC FallbackFrame).
            // QC runs CSQCPlayer_FallbackFrame on BOTH frame and frame2 independently (csqcmodel_hooks.qc:435-443),
            // so remap each bracketing endpoint — not just the floor frame — before interpolating between them.
            if (UseFallbackFrame)
            {
                f0 = CsqcFallbackFrame.Remap(f0, FrameDuration);
                f1 = CsqcFallbackFrame.Remap(f1, FrameDuration);
            }
            ApplyFrame(WrapRaw(f0), WrapRaw(f1), t);
            return;
        }

        if (!_playing || !_hasClip)
            return;

        _playhead += delta * _current.Fps * Math.Max(0f, TimeScale);

        float span = _current.FrameCount;
        if (_playhead >= span)
        {
            if (_current.Loop)
                _playhead = span <= 0f ? 0f : _playhead % span;
            else
            {
                _playhead = span - 0.0001f;
                _playing = false;
            }
        }

        // Bracket the two frames inside the clip and the interpolation weight between them.
        int local0 = (int)MathF.Floor(_playhead);
        int local1 = _current.Loop ? (local0 + 1) % _current.FrameCount : Math.Min(local0 + 1, _current.FrameCount - 1);
        float frac = _playhead - MathF.Floor(_playhead);

        int frameA = _current.FirstFrame + Math.Clamp(local0, 0, _current.FrameCount - 1);
        int frameB = _current.FirstFrame + Math.Clamp(local1, 0, _current.FrameCount - 1);
        ApplyFrame(frameA, frameB, frac);
    }

    private int WrapRaw(int f)
    {
        int n = _md3.FrameCount;
        if (n <= 0) return 0;
        return ((f % n) + n) % n;
    }

    /// <summary>
    /// QC <c>frameduration(modelindex, f)</c> probe for the fallback-frame remap: a positive duration when the
    /// model has frame <paramref name="f"/> (in <c>[0, FrameCount)</c>), 0 otherwise. MD3 morph has no stored
    /// per-frame duration, so a present frame returns a nominal 1/10s and an absent frame returns 0 — the only
    /// distinction <see cref="CsqcFallbackFrame.Remap"/> cares about (it tests <c>&gt; 0</c>).
    /// </summary>
    public float FrameDuration(int f) => (f >= 0 && f < _md3.FrameCount) ? 0.1f : 0f;

    /// <summary>True when the model has frame <paramref name="f"/> (QC <c>frameduration(...) &gt; 0</c>).</summary>
    public bool FrameExists(int f) => FrameDuration(f) > 0f;

    // =================================================================================================
    //  The morph itself — rebuild the ArrayMesh by lerping frame A -> frame B
    // =================================================================================================

    /// <summary>
    /// Rebuild the mesh surfaces interpolating each vertex between <paramref name="frameA"/> and
    /// <paramref name="frameB"/> by <paramref name="t"/> (0 = A, 1 = B), and re-pose the tag markers.
    /// This is the per-tick cost of MD3 morph playback; surfaces are small (player models are a few
    /// hundred verts) so a fresh ArrayMesh per frame is fine for the entity counts a match has.
    /// </summary>
    private void ApplyFrame(int frameA, int frameB, float t)
    {
        var mesh = new ArrayMesh();

        int surfaceIndex = 0;
        foreach ((Md3Surface surface, Material? material, bool visible) in _surfaces)
        {
            if (!visible)
                continue;
            int vcount = surface.VertexCount;
            if (vcount <= 0 || surface.Triangles.Length == 0 || surface.FrameVertices.Length == 0)
                continue;

            int fa = Math.Clamp(frameA, 0, surface.FrameVertices.Length - 1);
            int fb = Math.Clamp(frameB, 0, surface.FrameVertices.Length - 1);
            Md3Vertex[] va = surface.FrameVertices[fa];
            Md3Vertex[] vb = surface.FrameVertices[fb];
            if (va.Length < vcount || vb.Length < vcount)
                continue;

            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            for (int v = 0; v < vcount; v++)
            {
                // Lerp in Quake space, then convert — equivalent to lerping the converted values.
                System.Numerics.Vector3 p = System.Numerics.Vector3.Lerp(va[v].Position, vb[v].Position, t);
                System.Numerics.Vector3 nrm = System.Numerics.Vector3.Lerp(va[v].Normal, vb[v].Normal, t);
                positions[v] = Coords.ToGodot(p);
                Vector3 gn = Coords.ToGodot(nrm);
                normals[v] = gn.LengthSquared() > 1e-8f ? gn.Normalized() : Vector3.Up;
                uvs[v] = v < surface.TexCoords.Length
                    ? new Vector2(surface.TexCoords[v].X, surface.TexCoords[v].Y)
                    : Vector2.Zero;
            }

            var indices = new int[surface.Triangles.Length];
            for (int i = 0; i < surface.Triangles.Length; i++)
            {
                int idx = surface.Triangles[i];
                indices[i] = (idx >= 0 && idx < vcount) ? idx : 0;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (material is not null)
                mesh.SurfaceSetMaterial(surfaceIndex, material);
            surfaceIndex++;
        }

        // Preserve any material the caller set on surface 0 onto the rebuilt mesh.
        Material? matOverride = _mesh.MaterialOverride;
        _mesh.Mesh = mesh;
        if (matOverride is not null)
            _mesh.MaterialOverride = matOverride;

        PoseTags(frameA, frameB, t);
    }

    /// <summary>Re-pose each attachment tag marker by interpolating its transform between the two frames.</summary>
    private void PoseTags(int frameA, int frameB, float t)
    {
        foreach ((Md3Tag tag, Marker3D marker) in _tagMarkers)
        {
            if (tag.Transforms.Length == 0)
                continue;
            int fa = Math.Clamp(frameA, 0, tag.Transforms.Length - 1);
            int fb = Math.Clamp(frameB, 0, tag.Transforms.Length - 1);
            Md3TagTransform a = tag.Transforms[fa];
            Md3TagTransform b = tag.Transforms[fb];

            Vector3 origin = Coords.ToGodot(System.Numerics.Vector3.Lerp(a.Origin, b.Origin, t));
            // Slerp the basis by lerping axes then re-orthonormalising (cheap, good enough for tags).
            var basis = new Basis(
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisX, b.AxisX, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisY, b.AxisY, t)),
                Coords.ToGodot(System.Numerics.Vector3.Lerp(a.AxisZ, b.AxisZ, t))).Orthonormalized();

            marker.Transform = new Transform3D(basis, origin);
        }
    }

    // =================================================================================================
    //  Auto clip detection from MD3 frame names
    // =================================================================================================

    /// <summary>
    /// Build clips from the MD3's frame names when they look like Quake/Xonotic anim labels. MD3 frame
    /// names typically read like "idle1", "run1", "death1"; we group contiguous same-prefix runs into a
    /// clip. Falls back to a single full-range "all" clip when names are absent/uninformative.
    /// </summary>
    private void AutoRegisterClips()
    {
        Md3Frame[] frames = _md3.Frames;
        if (frames.Length == 0)
        {
            AddClip("all", 0, Math.Max(1, _md3.FrameCount), 10f, true);
            return;
        }

        string Prefix(string s)
        {
            int end = s.Length;
            while (end > 0 && char.IsDigit(s[end - 1])) end--;
            return s[..end].ToLowerInvariant().Trim();
        }

        int runStart = 0;
        string runName = Prefix(frames[0].Name);
        bool anyNamed = false;

        void Flush(int start, int endExclusive, string label)
        {
            if (string.IsNullOrEmpty(label) || endExclusive <= start)
                return;
            anyNamed = true;
            (float fps, bool loop) = ClipPlayback(label);
            // De-dup names by appending the start frame if a clip with that name already exists.
            string name = _clips.ContainsKey(label) ? $"{label}@{start}" : label;
            AddClip(name, start, endExclusive - start, fps, loop);
        }

        for (int i = 1; i < frames.Length; i++)
        {
            string p = Prefix(frames[i].Name);
            if (p != runName)
            {
                Flush(runStart, i, runName);
                runStart = i;
                runName = p;
            }
        }
        Flush(runStart, frames.Length, runName);

        if (!anyNamed)
            AddClip("all", 0, _md3.FrameCount, 10f, true);
    }

    /// <summary>Per-anim-name default playback (fps + loop). Mirrors the QC frame-rate conventions roughly.</summary>
    private static (float fps, bool loop) ClipPlayback(string name)
    {
        if (name.Contains("idle") || name.Contains("stand") || name.Contains("wait")) return (8f, true);
        if (name.Contains("run") || name.Contains("walk") || name.Contains("strafe")) return (15f, true);
        if (name.Contains("jump") || name.Contains("fall")) return (12f, false);
        if (name.Contains("death") || name.Contains("die") || name.Contains("dead")) return (12f, false);
        if (name.Contains("attack") || name.Contains("shoot") || name.Contains("fire")) return (20f, false);
        if (name.Contains("pain") || name.Contains("hit")) return (15f, false);
        return (10f, true);
    }

    private Md3Tag? FindTag(string markerName)
    {
        foreach (Md3Tag tag in _md3.Tags)
            if (string.Equals(tag.Name, markerName, StringComparison.Ordinal))
                return tag;
        return null;
    }
}
