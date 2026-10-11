using MdxPreview.Core.Math3D;
using MdxPreview.Core.Mdx;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Pure placement math for MDX event objects (no Godot types, so it can be
    /// exercised by the plain-console probes).
    ///
    /// <see cref="LocalMatrix"/> is MDLX_EventWorldTransform
    /// (games/warcraft-3/renderer/mdx/r_mdx_anim.c) without the entity matrix:
    /// the animated node's matrix keeps its rotation/scale basis, and its
    /// translation is replaced by the node's transformed authored pivot so the
    /// child lands on the event point rather than the parent origin.
    ///
    /// <see cref="GodotWorld"/> completes the chain with the preview's root
    /// rotation. `MdxModelNode` maps MDX Z-up onto Godot Y-up by rotating
    /// -90 degrees about X, and `MdxMeshBuilder.ToTransform` copies a Mat4 into a
    /// Godot Transform3D component for component, so a world point is the root
    /// rotation applied to the MDX point.
    ///
    /// Why the spawned child is parented to the `Events` node with
    /// <see cref="LocalMatrix"/> as its local transform: `MdxModelNode.Build`
    /// always applies its own -90 degree X root rotation, and the `Events` node
    /// inherits it. The child's final Godot world matrix is therefore
    /// `RootRotation * Local * ChildRootRotation`. With
    /// `Local = LocalMatrix` the two root rotations are equal, so the child's
    /// authored -90 degree X cancels the parent's and the result is
    /// `RootRotation * EventMatrix`: the child is placed in the parent's frame
    /// with the authored event rotation applied exactly once.
    /// </summary>
    public static class MdxEventPlacement
    {
        /// <summary>
        /// The preview's MDX Z-up -> Godot Y-up root rotation (-90 degrees about
        /// X), the same transform `MdxModelNode.Build` assigns to `RotationDegrees`.
        /// </summary>
        public static Mat4 RootRotation => Mat4.FromQuat(Quat.FromEulerXYZ(-90f, 0f, 0f));

        /// <summary>
        /// MDLX_EventWorldTransform in MDX model space: the animated node matrix
        /// with its translation set to the node's transformed pivot.
        /// </summary>
        public static Mat4 LocalMatrix(MdxModel model, MdxEventObject evt, MdxInstance instance)
        {
            if (evt?.Node == null || instance == null) return Mat4.Identity;

            int nodeId = evt.Node.NodeId;
            Mat4 nodeWorld = instance.GetNodeWorld(nodeId);
            Vec3 pivot = model != null && nodeId >= 0 && nodeId < model.Pivots.Count
                ? model.Pivots[nodeId]
                : Vec3.Zero;
            Vec3 origin = nodeWorld.TransformPoint(pivot);

            nodeWorld.M14 = origin.X;
            nodeWorld.M24 = origin.Y;
            nodeWorld.M34 = origin.Z;
            return nodeWorld;
        }

        /// <summary>MDX model-space position an event places its child at.</summary>
        public static Vec3 LocalOrigin(MdxModel model, MdxEventObject evt, MdxInstance instance)
            => LocalMatrix(model, evt, instance).Translation;

        /// <summary>Godot world-space position of an MDX model-space point.</summary>
        public static Vec3 GodotWorld(Vec3 mdxPoint)
        {
            Mat4 root = RootRotation;
            return new Vec3(
                root.M11 * mdxPoint.X + root.M12 * mdxPoint.Y + root.M13 * mdxPoint.Z,
                root.M21 * mdxPoint.X + root.M22 * mdxPoint.Y + root.M23 * mdxPoint.Z,
                root.M31 * mdxPoint.X + root.M32 * mdxPoint.Y + root.M33 * mdxPoint.Z);
        }

        /// <summary>
        /// The Godot world matrix a spawned child ends up with, computed without
        /// Godot: `RootRotation * LocalMatrix`. The child's own root rotation is
        /// cancelled by the parent's, which is exactly what this product encodes.
        /// </summary>
        public static Mat4 ChildGodotWorld(MdxModel model, MdxEventObject evt, MdxInstance instance)
            => RootRotation * LocalMatrix(model, evt, instance);
    }
}
