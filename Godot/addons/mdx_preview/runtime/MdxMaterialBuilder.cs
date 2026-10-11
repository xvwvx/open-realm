using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>Runtime state for one material layer of one geoset.</summary>
    public sealed class MdxLayerBinding
    {
        public MdxMaterialLayer Layer;
        /// <summary>StandardMaterial3D, or a generated ShaderMaterial when the
        /// layer's TXAN rotation cannot be expressed by UV scale/offset alone.</summary>
        public Material Material;
        public bool UsesShader;
        public int ResolvedTextureId = -1;
        /// <summary>KMTF animates the texture slot, so the albedo texture changes per frame.</summary>
        public bool TextureAnimated;
        public bool HasUvTransform;
        public bool HasAlphaTrack;
        public Color BaseColor = Colors.White;
    }

    /// <summary>Runtime state for one geoset (one MeshInstance3D, layers chained).</summary>
    public sealed class MdxGeosetBinding
    {
        public MdxGeoset Geoset;
        public MeshInstance3D MeshNode;
        /// <summary>One MeshInstance3D per MDX layer, in authored layer order.</summary>
        public readonly List<MeshInstance3D> Nodes = new List<MeshInstance3D>();
        public readonly List<MdxLayerBinding> Layers = new List<MdxLayerBinding>();
        public bool HasGeosetAnimation;
    }

    /// <summary>
    /// Builds Godot materials from MDX material layers.
    ///
    /// Filter modes follow the MDX file values (the engine's MDLX_SetLayerBlend
    /// consumes the same authored field):
    ///   0 None        opaque
    ///   1 Transparent alpha test / scissor
    ///   2 Blend       alpha blend
    ///   3 Additive    additive
    ///   4 AddAlpha    additive with source alpha
    ///   5 Modulate    multiply
    ///   6 Modulate2x  multiply with doubled source
    /// </summary>
    public static class MdxMaterialBuilder
    {
        public static StandardMaterial3D CreateLayerMaterial(
            MdxAssetLibrary assets,
            MdxModel model,
            MdxMaterialLayer layer,
            out int textureId)
        {
            textureId = layer.TextureId;
            MdxTexture texture = textureId >= 0 && textureId < model.Textures.Count ? model.Textures[textureId] : null;

            var material = new StandardMaterial3D
            {
                // WC3 models are low-poly, texture driven art: keep them matte.
                Roughness = 1.0f,
                MetallicSpecular = 0.1f,
                Metallic = 0.0f,
                // Warcraft assets are Direct3D-era: their triangles are wound clockwise as
                // seen from the front, while Godot (like OpenGL) treats counter clockwise as
                // front facing. Culling Godot's "back" faces therefore culls the authored
                // FRONT and the model looks inside out (e.g. the head shows its interior).
                // Verified against mdxtool: retail models still render correctly with the
                // flipped winding, custom exports (zq\*.mdx) require it.
                CullMode = (layer.Flags & MdxGeoFlags.TwoSided) != 0
                    ? BaseMaterial3D.CullModeEnum.Disabled
                    : BaseMaterial3D.CullModeEnum.Front,
                ShadingMode = (layer.Flags & MdxGeoFlags.Unshaded) != 0
                    ? BaseMaterial3D.ShadingModeEnum.Unshaded
                    : BaseMaterial3D.ShadingModeEnum.PerPixel,
                TextureRepeat = texture == null || texture.WrapWidth != 0 || texture.WrapHeight != 0,
                VertexColorUseAsAlbedo = false,
                AlbedoColor = Colors.White,
            };

            // Layers are chained with Material.NextPass, so a surface whose first layer is
            // translucent puts the whole stack in Godot's transparent pass, which does not
            // write depth by default. MDX filter 0 means "opaque", and without a depth write
            // the back faces blend through the front ones (they look merged). Blended layers
            // keep no depth write, exactly like the engine.
            if (layer.FilterMode == 0)
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            // The authored NoDepthTest bit means "draw on top of the world". Mapping it to
            // Godot's NoDepthTest also disables the depth TEST for this pass, which lets the
            // layer's back faces add on top of its front faces (custom models with an
            // additive overlay then look like front and back are merged). Keep the depth
            // test on and only skip the depth WRITE, which preserves the overlay look.
            if ((layer.Flags & MdxGeoFlags.NoDepthTest) != 0)
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
            if ((layer.Flags & MdxGeoFlags.NoDepthSet) != 0) material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;

            ApplyFilter(material, layer.FilterMode);

            if (texture != null && !string.IsNullOrEmpty(texture.Path))
            {
                // filter 0 means fully opaque in MDX: the engine ignores the texture's
                // alpha there. Layers are chained with NextPass, and a surface whose
                // first layer is translucent renders the whole stack in the transparent
                // pass, so an opaque layer must use the alpha-stripped variant or the
                // model looks washed out / see-through.
                // Each MDX layer now has its own MeshInstance3D, so a filter 0 layer already
                // lands in Godot's opaque queue where texture alpha is ignored - no need for an
                // alpha-stripped variant (that variant turned the surface white).
                material.AlbedoTexture = assets.LoadTexture(texture.Path, texture.ReplaceableId);
            }
            else if (texture != null && texture.ReplaceableId != 0)
            {
                material.AlbedoTexture = assets.LoadTexture(null, texture.ReplaceableId);
            }

            // Team-colour (1) / team-glow (2) layers are tinted per player. The engine
            // also substitutes textures referenced by path
            // ("ReplaceableTextures\TeamColor\TeamColorNN.blp"), which the asset library
            // already turned into a flat white texture, so resolve the kind from both.
            uint replaceableKind = texture?.ReplaceableId ?? 0;
            if (replaceableKind == 0 && assets.SubstituteReplaceablePaths && texture != null && !string.IsNullOrEmpty(texture.Path) &&
                MdxAssetLibrary.ClassifyReplaceablePath(texture.Path, out uint pathKind))
            {
                replaceableKind = pathKind;
            }
            if (replaceableKind == 1)
            {
                // Team colour keeps the layer's authored shading flag: WC3 unit
                // patches are lit like the rest of the model.
                material.AlbedoColor = assets.TeamColor.Color;
            }
            else if (replaceableKind == 2)
            {
                material.AlbedoColor = assets.TeamColor.Glow;
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.EmissionEnabled = true;
                material.Emission = assets.TeamColor.Glow;
                material.EmissionEnergyMultiplier = 1.5f;
            }

            return material;
        }

        public static void ApplyFilter(StandardMaterial3D material, int filterMode)
        {
            switch (filterMode)
            {
                case 0: // None: fully opaque, depth written
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
                case 1: // Transparent: 1-bit alpha (alpha test)
                    material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    material.AlphaScissorThreshold = 0.5f;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
                case 2: // Blend
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
                case 3: // Additive
                case 4: // AddAlpha
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                    break;
                case 5: // Modulate
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                    break;
                case 6: // Modulate2x (approximated by a brightened multiply)
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                    material.AlbedoColor = new Color(2f, 2f, 2f, 1f);
                    break;
                default:
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
            }
        }

        /// <summary>Particle/emitter blend modes use the PRE2 enum order.</summary>
        public static void ApplyParticleFilter(StandardMaterial3D material, int filterMode)
        {
            switch (filterMode)
            {
                case 0: // Blend
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
                case 1: // Additive
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                    break;
                case 2: // Modulate
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                    break;
                case 3: // Modulate2x
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                    break;
                case 4: // AlphaKey
                    material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    material.AlphaScissorThreshold = 0.5f;
                    break;
                default:
                    material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    material.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
                    break;
            }
        }
    }
}
