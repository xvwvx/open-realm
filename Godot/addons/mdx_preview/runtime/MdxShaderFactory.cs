using Godot;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Shader-material variants for MDX layers whose texture animation (TXAN)
    /// rotates the UVs. Godot's <see cref="StandardMaterial3D"/> can only scale
    /// and offset UVs, so those layers get a small generated shader that applies
    /// the authored texture matrix <c>translation * rotation * scale</c> — the same
    /// composition the engine uploads as the GL texture matrix.
    ///
    /// All other layers keep StandardMaterial3D (full lighting, shadows, and the
    /// engine-independent blend handling in <see cref="MdxMaterialBuilder"/>).
    /// </summary>
    public static class MdxShaderFactory
    {
        /// <summary>
        /// Debug material that paints each vertex's UV as a colour (u -> red, v -> green).
        /// A coherent gradient per body part means the UVs are fine; scrambled colours mean
        /// the UV stream or the vertex order is wrong.
        /// </summary>
        public static ShaderMaterial CreateUvDebugMaterial()
        {
            const string code =
                "shader_type spatial;\n" +
                "render_mode unshaded, cull_disabled;\n" +
                "void fragment() {\n" +
                "    ALBEDO = vec3(UV.x, UV.y, 0.25);\n" +
                "    ALPHA = 1.0;\n" +
                "}\n";
            var shader = new Shader { Code = code };
            return new ShaderMaterial { Shader = shader };
        }

        /// <summary>True when the layer's texture animation actually rotates the UVs.</summary>
        public static bool NeedsUvRotation(MdxModel model, MdxMaterialLayer layer)
        {
            if (model == null || layer == null) return false;
            if (layer.TransformId < 0 || layer.TransformId >= model.TextureAnims.Count) return false;
            MdxKeyTrack rotation = model.TextureAnims[layer.TransformId].Rotation;
            return rotation != null && rotation.KeyCount > 0;
        }

        public static ShaderMaterial CreateLayerMaterial(MdxAssetLibrary assets, MdxModel model, MdxMaterialLayer layer, out int textureId)
        {
            textureId = layer.TextureId;
            MdxTexture texture = textureId >= 0 && textureId < model.Textures.Count ? model.Textures[textureId] : null;

            var shader = new Shader { Code = BuildShaderCode(layer) };
            var material = new ShaderMaterial { Shader = shader };

            Texture2D albedo = texture != null
                ? (string.IsNullOrEmpty(texture.Path)
                    ? assets.LoadTexture(null, texture.ReplaceableId)
                    : assets.LoadTexture(texture.Path, texture.ReplaceableId))
                : assets.GetWhiteTexture();

            material.SetShaderParameter("albedo_tex", albedo);
            material.SetShaderParameter("albedo_color", ResolveBaseColor(assets, texture));
            material.SetShaderParameter("uv_offset", Vector2.Zero);
            material.SetShaderParameter("uv_scale", Vector2.One);
            material.SetShaderParameter("uv_rotation", 0f);
            material.SetShaderParameter("repeat_enabled", texture == null || texture.WrapWidth != 0 || texture.WrapHeight != 0);
            return material;
        }

        static Color ResolveBaseColor(MdxAssetLibrary assets, MdxTexture texture)
        {
            if (texture == null) return Colors.White;
            return texture.ReplaceableId switch
            {
                1 => assets.TeamColor.Color,
                2 => assets.TeamColor.Glow,
                _ => Colors.White,
            };
        }

        /// <summary>Builds the shader source for one layer (blend/cull/depth/unshaded baked in).</summary>
        static string BuildShaderCode(MdxMaterialLayer layer)
        {
            var modes = new System.Collections.Generic.List<string>();

            switch (layer.FilterMode)
            {
                case 1: // Transparent: 1-bit alpha
                    modes.Add("alpha_to_coverage");
                    break;
                case 2: // Blend
                    modes.Add("blend_mix");
                    break;
                case 3: // Additive
                case 4: // AddAlpha
                    modes.Add("blend_add");
                    break;
                case 5: // Modulate
                case 6: // Modulate2x
                    modes.Add("blend_mul");
                    break;
                default:
                    modes.Add("blend_mix");
                    break;
            }

            if ((layer.Flags & MdxGeoFlags.TwoSided) != 0) modes.Add("cull_disabled");
            if ((layer.Flags & MdxGeoFlags.Unshaded) != 0) modes.Add("unshaded");
            if ((layer.Flags & MdxGeoFlags.NoDepthTest) != 0) modes.Add("depth_test_disabled");
            if ((layer.Flags & MdxGeoFlags.NoDepthSet) != 0) modes.Add("depth_draw_never");

            string renderModes = modes.Count > 0 ? "render_mode " + string.Join(", ", modes) + ";\n" : string.Empty;

            return "// Generated by MDX Preview for a TXAN-rotated layer.\n" +
                   "shader_type spatial;\n" +
                   renderModes +
                   "uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;\n" +
                   "uniform vec4 albedo_color : source_color = vec4(1.0);\n" +
                   "uniform vec2 uv_offset = vec2(0.0);\n" +
                   "uniform vec2 uv_scale = vec2(1.0);\n" +
                   "uniform float uv_rotation = 0.0;\n" +
                   "uniform float alpha_scissor : hint_range(0.0, 1.0) = 0.5;\n" +
                   "varying vec2 mdx_uv;\n" +
                   "void vertex() {\n" +
                   "    vec2 uv = UV * uv_scale;\n" +                 // texture matrix: T * R * S
                   "    float s = sin(uv_rotation);\n" +
                   "    float c = cos(uv_rotation);\n" +
                   "    uv = mat2(vec2(c, -s), vec2(s, c)) * uv;\n" +
                   "    mdx_uv = uv + uv_offset;\n" +
                   "}\n" +
                   "void fragment() {\n" +
                   "    vec4 tex = texture(albedo_tex, mdx_uv) * albedo_color;\n" +
                   (layer.FilterMode == 1
                        ? "    if (tex.a < alpha_scissor) discard;\n    tex.a = 1.0;\n"
                        : string.Empty) +
                   "    ALBEDO = tex.rgb;\n" +
                   "    ALPHA = tex.a;\n" +
                   "    ROUGHNESS = 1.0;\n" +
                   "    SPECULAR = 0.08;\n" +
                   "}\n";
        }
    }
}
