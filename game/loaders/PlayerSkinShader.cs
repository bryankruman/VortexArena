using Godot;

namespace XonoticGodot.Game.Loaders;

/// <summary>
/// The Darkplaces "skin" material shader: a diffuse surface plus the team-colorable <c>_shirt</c>/<c>_pants</c>
/// masks and the <c>_reflect</c> reflection mask that the channel-suffix companions carry.
///
/// <para>Darkplaces loads, alongside the diffuse, a family of sibling images by filename suffix
/// (<c>darkplaces.txt</c>): <c>_pants</c>/<c>_shirt</c> are <b>greyscale masks additively ("Screen") blended
/// over the diffuse, tinted by the player's pants/shirt color</b> (DP <c>gl_rmain.c</c>
/// <c>Color_Pants</c>/<c>Color_Shirt</c> uniforms over <c>Texture_Pants</c>/<c>Texture_Shirt</c>), and
/// <c>_reflect</c> is a reflection mask that modulates an environment cubemap added to the diffuse
/// (<c>Texture_ReflectMask</c> × <c>Texture_ReflectCube</c> in DP's GLSL). The stock
/// <see cref="StandardMaterial3D"/> can express none of these — two independent tinted additive masks have
/// no slot — so a model that uses them must compile to this dedicated shader.</para>
///
/// <para>The shirt/pants colors are bound as uniforms (<see cref="ShirtColorUniform"/>/
/// <see cref="PantsColorUniform"/>), defaulting to black exactly as DP does when an entity has no colormap
/// (so the masks contribute nothing until a team color is applied). The player renderer can set them per
/// entity from the colormap to drive team coloring; the masks are wired regardless, so they are no longer
/// dropped. The reflection mask drives a metallic-style environment reflection (a true cubemap reflection
/// from <c>dpreflectcube</c> would bind <see cref="ReflectCubeUniform"/>, otherwise the surface reflects the
/// world environment where the mask is bright).</para>
/// </summary>
public static class PlayerSkinShader
{
    /// <summary>Uniform: the diffuse/albedo texture (sampled with UV).</summary>
    public const string AlbedoUniform = "albedo_tex";

    /// <summary>Uniform: the <c>_shirt</c> greyscale mask.</summary>
    public const string ShirtMaskUniform = "shirt_mask";

    /// <summary>Uniform: the <c>_pants</c> greyscale mask.</summary>
    public const string PantsMaskUniform = "pants_mask";

    /// <summary>Uniform: the team shirt color (RGB). Default black = no contribution (DP no-colormap default).</summary>
    public const string ShirtColorUniform = "shirt_color";

    /// <summary>Uniform: the team pants color (RGB). Default black = no contribution.</summary>
    public const string PantsColorUniform = "pants_color";

    /// <summary>Uniform: the <c>_reflect</c> reflection mask (greyscale, modulates the environment reflection).</summary>
    public const string ReflectMaskUniform = "reflect_mask";

    /// <summary>Uniform: optional explicit reflection cubemap (<c>dpreflectcube</c>); unbound → world environment.</summary>
    public const string ReflectCubeUniform = "reflect_cube";

    /// <summary>Uniform: scalar reflection strength applied through the mask (0..1).</summary>
    public const string ReflectStrengthUniform = "reflect_strength";

    /// <summary>Uniform: the <c>_glow</c>/<c>_luma</c> emission map (added unlit).</summary>
    public const string GlowUniform = "glow_tex";

    /// <summary>Instance uniform: per-entity albedo tint (DP <c>colormod</c>); default white = identity.</summary>
    public const string ColormodUniform = "colormod";

    /// <summary>Instance uniform: per-entity glow/emission tint (DP <c>glowmod</c>); default white.</summary>
    public const string GlowmodUniform = "glowmod";

    /// <summary>
    /// The GDShader source. Lit (the skin reacts to scene lighting like a normal model surface), with the
    /// shirt/pants masks added as tinted terms over the albedo and the reflect mask driving the metallic
    /// channel so Godot's environment reflection shows through where the mask is bright. Optional
    /// normal/roughness(gloss)/glow are honored when their samplers are bound.
    /// </summary>
    public const string Code = @"// XonoticGodot player-skin shader (Darkplaces _shirt/_pants/_reflect masks). Generated in C#.
shader_type spatial;
// OPAQUE skin. These DP alias-model skins (players + weapon viewmodels) are solid: real translucency comes
// from a shader blendFunc (which compiles elsewhere), never from the diffuse alpha — in DP a model skin's
// alpha channel is a spec/gloss mask, not transparency. We must NOT write ALPHA below: in Godot 4 any
// assignment to ALPHA sets the shader's `uses_alpha` flag, which moves the material into the transparent
// pass (alpha-blended, and with the default depth_draw_opaque it stops writing depth). Depth-write off on a
// self-overlapping mesh lets its own back faces draw over its front faces in submission order — the
// ""I can see vertices through other vertices"" see-through artifact on every weapon model. Leaving ALPHA at
// its default (1.0) keeps the skin opaque with normal depth testing.
render_mode cull_back;

uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D shirt_mask : hint_default_black;
uniform sampler2D pants_mask : hint_default_black;
uniform sampler2D reflect_mask : hint_default_black;
uniform sampler2D normal_tex : hint_normal;
uniform sampler2D gloss_tex : hint_default_white;
uniform sampler2D glow_tex : hint_default_black;
uniform bool has_normal = false;
uniform bool has_gloss = false;
uniform bool has_glow = false;
uniform bool has_reflect = false;
// Per-entity tints are instance uniforms so the shared (cached) skin material can still be reused while
// each model instance carries its own colors (set via MeshInstance3D.set_instance_shader_parameter).
instance uniform vec3 shirt_color : source_color = vec3(0.0);
instance uniform vec3 pants_color : source_color = vec3(0.0);
instance uniform vec3 colormod : source_color = vec3(1.0);
instance uniform vec3 glowmod : source_color = vec3(1.0);
uniform float reflect_strength = 1.0;
// Dynamic scene tint (XonoticGodot.Game.WorldTint) — a GLOBAL shader parameter applied to every model/skin
// (players, weapon viewmodels, pickups) so the ""everything else"" grade can differ from the whole-map tint.
// Strength is folded in on the C# side; default (1,1,1) is identity. Distinct from the per-entity colormod.
global uniform vec3 entity_tint;

void fragment() {
    vec4 base = texture(albedo_tex, UV);
    // DP _shirt/_pants: greyscale mask, additively (Screen-ish) blended with the team color tint.
    float shirt = texture(shirt_mask, UV).r;
    float pants = texture(pants_mask, UV).r;
    vec3 col = base.rgb + shirt * shirt_color + pants * pants_color;
    // colormod: the per-entity tint DP folds into the lighting term (default white = identity).
    // entity_tint: the global, dynamic scene grade (default white = identity), applied on top.
    ALBEDO = col * colormod * entity_tint;
    // NOTE: deliberately NOT writing ALPHA — see the opaque-skin note in the render_mode header. base.a is a
    // spec/mask channel here, not transparency, and writing it would force the transparent (no-depth) pass.

    if (has_normal) {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
    }

    // Resolve roughness/metallic locally (avoid reading write-only builtins), then assign once.
    float rough = has_gloss ? (1.0 - texture(gloss_tex, UV).g) : 1.0; // gloss is the inverse of roughness
    float metal = 0.0;
    if (has_reflect) {
        // _reflect mask gates an environment reflection: bright mask = metallic/smooth so the world reflects.
        float m = texture(reflect_mask, UV).r * reflect_strength;
        metal = m;
        rough = mix(rough, 0.1, m);
        SPECULAR = mix(0.5, 1.0, m);
    }
    ROUGHNESS = rough;
    METALLIC = metal;

    if (has_glow) {
        // glowmod: per-entity glow tint (DP render_glowmod); viewmodels/players use the pants color.
        EMISSION = texture(glow_tex, UV).rgb * glowmod;
    }
}
";

    private static Shader? _shared;

    /// <summary>The shared <see cref="Shader"/> instance compiled from <see cref="Code"/>.</summary>
    public static Shader Shader => _shared ??= new Shader { Code = Code };
}
