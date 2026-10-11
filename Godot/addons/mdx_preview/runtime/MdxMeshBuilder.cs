using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Turns an MDX geoset into a Godot <see cref="ArrayMesh"/> with GPU skinning
    /// data. Mirrors MDX_PackModelGeometry / mdx_pack_vertices from
    /// games/warcraft-3/renderer/mdx/r_mdx_buffer.c: every vertex belongs to one
    /// matrix group, the group's bones share equal weight, and only the four
    /// strongest influences are kept (renormalised to 1.0).
    ///
    /// One difference from the engine: Godot skins against the whole
    /// Skeleton3D instead of a per-geoset 128-entry matrix palette, so models
    /// with more than MDX_MATRIX_PALETTE unique matrices still deform correctly.
    /// </summary>
    public static class MdxMeshBuilder
    {
        public const int MaxSkinBones = 4;

        public static ArrayMesh Build(MdxGeoset geoset)
        {
            int vertexCount = geoset.VertexCount;
            if (vertexCount <= 0) return null;

            var positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                positions[i] = new Vector3(geoset.Vertices[i * 3], geoset.Vertices[i * 3 + 1], geoset.Vertices[i * 3 + 2]);

            var normals = new Vector3[vertexCount];
            bool hasNormals = geoset.Normals != null && geoset.Normals.Length >= vertexCount * 3;
            for (int i = 0; i < vertexCount; i++)
                normals[i] = hasNormals
                    ? new Vector3(geoset.Normals[i * 3], geoset.Normals[i * 3 + 1], geoset.Normals[i * 3 + 2])
                    : Vector3.Up;

            var uvs = new Vector2[vertexCount];
            bool hasUvs = geoset.Uvs != null && geoset.Uvs.Length >= vertexCount * 2;
            for (int i = 0; i < vertexCount; i++)
                uvs[i] = hasUvs ? new Vector2(geoset.Uvs[i * 2], geoset.Uvs[i * 2 + 1]) : Vector2.Zero;

            var uv2 = new Vector2[vertexCount];
            bool hasUv2 = geoset.Uvs2 != null && geoset.Uvs2.Length >= vertexCount * 2;
            for (int i = 0; i < vertexCount; i++)
                uv2[i] = hasUv2 ? new Vector2(geoset.Uvs2[i * 2], geoset.Uvs2[i * 2 + 1]) : Vector2.Zero;

            var bones = new int[vertexCount * 4];
            var weights = new float[vertexCount * 4];
            BuildSkin(geoset, bones, weights);

            var indices = new int[geoset.Indices.Length];
            for (int i = 0; i < geoset.Indices.Length; i++) indices[i] = geoset.Indices[i];

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            if (hasUv2) arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
            arrays[(int)Mesh.ArrayType.Bones] = bones;
            arrays[(int)Mesh.ArrayType.Weights] = weights;
            if (indices.Length > 0) arrays[(int)Mesh.ArrayType.Index] = indices;

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }

        /// <summary>
        /// Per-vertex bone indices and weights. Bone indices are MDX node ids
        /// because the preview creates exactly one Skeleton3D bone per node.
        /// </summary>
        static void BuildSkin(MdxGeoset geoset, int[] bones, float[] weights)
        {
            int vertexCount = geoset.VertexCount;
            int groupCount = geoset.MatrixGroups?.Length ?? 0;
            bool singleDefaultGroup = groupCount == 0;

            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int groupIndex = 0;
                if (!singleDefaultGroup && geoset.VertexGroups != null && geoset.VertexGroups.Length > vertex)
                {
                    groupIndex = geoset.VertexGroups[vertex];
                    if (groupIndex >= groupCount) groupIndex = groupCount - 1;
                    if (groupIndex < 0) groupIndex = 0;
                }

                int[] group = singleDefaultGroup || geoset.MatrixGroups == null
                    ? null
                    : geoset.MatrixGroups[groupIndex];

                if (group == null || group.Length == 0)
                {
                    bones[vertex * 4] = 0;
                    weights[vertex * 4] = 1f;
                    continue;
                }

                int count = Math.Min(group.Length, MaxSkinBones);
                float weight = 1f / count;
                for (int i = 0; i < count; i++)
                {
                    bones[vertex * 4 + i] = group[i] < 0 ? 0 : group[i];
                    weights[vertex * 4 + i] = weight;
                }
            }
        }

        /// <summary>Geoset bounds in MDX space (authored bounds when present).</summary>
        public static Aabb ComputeBounds(MdxGeoset geoset)
        {
            if (geoset.VertexCount == 0) return new Aabb();
            if (geoset.Bounds.Radius > 0f
                && (geoset.Bounds.Min.X != geoset.Bounds.Max.X
                    || geoset.Bounds.Min.Y != geoset.Bounds.Max.Y
                    || geoset.Bounds.Min.Z != geoset.Bounds.Max.Z))
            {
                Vector3 min = new Vector3(geoset.Bounds.Min.X, geoset.Bounds.Min.Y, geoset.Bounds.Min.Z);
                Vector3 max = new Vector3(geoset.Bounds.Max.X, geoset.Bounds.Max.Y, geoset.Bounds.Max.Z);
                return new Aabb(min, max - min);
            }

            var aabb = new Aabb(
                new Vector3(geoset.Vertices[0], geoset.Vertices[1], geoset.Vertices[2]),
                Vector3.Zero);
            for (int i = 1; i < geoset.VertexCount; i++)
                aabb = aabb.Expand(new Vector3(geoset.Vertices[i * 3], geoset.Vertices[i * 3 + 1], geoset.Vertices[i * 3 + 2]));
            return aabb;
        }

        /// <summary>Model-space bounds across every geoset (used to frame the camera).</summary>
        public static Aabb ComputeModelBounds(MdxModel model)
        {
            bool any = false;
            Aabb result = new Aabb();
            foreach (MdxGeoset geoset in model.Geosets)
            {
                if (geoset.VertexCount == 0) continue;
                Aabb aabb = ComputeBounds(geoset);
                result = any ? result.Merge(aabb) : aabb;
                any = true;
            }
            if (any) return result;

            if (model.Bounds.Radius > 0f)
            {
                Vector3 min = new Vector3(model.Bounds.Min.X, model.Bounds.Min.Y, model.Bounds.Min.Z);
                Vector3 max = new Vector3(model.Bounds.Max.X, model.Bounds.Max.Y, model.Bounds.Max.Z);
                return new Aabb(min, max - min);
            }
            return new Aabb(new Vector3(-32, -32, 0), new Vector3(64, 64, 64));
        }

        /// <summary>Converts a core matrix into a Godot transform.</summary>
        public static Transform3D ToTransform(Core.Math3D.Mat4 m) => new Transform3D(
            new Basis(
                m.M11, m.M12, m.M13,
                m.M21, m.M22, m.M23,
                m.M31, m.M32, m.M33),
            new Vector3(m.M14, m.M24, m.M34));

        /// <summary>Converts a Godot transform's rotation into a core matrix.</summary>
        public static Core.Math3D.Mat4 ToRotationMatrix(Basis basis)
        {
            return new Core.Math3D.Mat4
            {
                M11 = basis.X.X, M12 = basis.X.Y, M13 = basis.X.Z,
                M21 = basis.Y.X, M22 = basis.Y.Y, M23 = basis.Y.Z,
                M31 = basis.Z.X, M32 = basis.Z.Y, M33 = basis.Z.Z,
                M44 = 1f,
            };
        }
    }
}
