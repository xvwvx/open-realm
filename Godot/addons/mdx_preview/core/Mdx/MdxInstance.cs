using System;
using System.Collections.Generic;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Core.Mdx
{
    /// <summary>Animated texture transform sampled from a TXAN record.</summary>
    public struct MdxTextureAnimState
    {
        public Vec3 Translation;
        public Quat Rotation;
        public Vec3 Scale;
    }

    /// <summary>A resolved attachment point in model space.</summary>
    public struct MdxAttachmentPose
    {
        public MdxAttachment Attachment;
        public Vec3 Origin;
        public Mat4 Transform;
    }

    /// <summary>A camera pose sampled from a CAMS record.</summary>
    public struct MdxCameraPose
    {
        public Vec3 Position;
        public Vec3 Target;
        public float Roll;
        public float FieldOfView;
        public float NearClip;
        public float FarClip;
    }

    /// <summary>
    /// Runtime animation state for one MDX model instance: sequence playback,
    /// node hierarchy evaluation, geoset/material/texture animation sampling,
    /// attachment and event queries.
    ///
    /// Port of the runtime half of games/warcraft-3/renderer/mdx/r_mdx_anim.c
    /// (R_CalculateNodeMatrix / R_GetNodeGlobalMatrix / MDLX_BindBoneMatrices /
    /// MDLX_CollectAttachmentPositions / MDLX_EventKeyCrossed). Frame times are
    /// floats so the preview animates smoothly; authored keyframe interpolation
    /// itself is unchanged.
    /// </summary>
    public sealed class MdxInstance
    {
        readonly Mat4[] _nodeLocal;
        readonly Mat4[] _nodeWorld;
        readonly bool[] _nodeComputed;
        readonly float[] _trackScratch = new float[4];

        public MdxInstance(MdxModel model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            int count = Math.Max(1, model.Nodes.Length);
            _nodeLocal = new Mat4[count];
            _nodeWorld = new Mat4[count];
            _nodeComputed = new bool[count];
            for (int i = 0; i < count; i++) _nodeWorld[i] = Mat4.Identity;

            SequenceIndex = model.Sequences.Count > 0 ? 0 : -1;
        }

        public MdxModel Model { get; }

        public int SequenceIndex { get; private set; }

        /// <summary>Authored frame time (milliseconds) inside the model timeline.</summary>
        public float Frame { get; set; }

        public float Speed { get; set; } = 1f;

        public bool Playing { get; set; } = true;

        /// <summary>Loop the current sequence (non-looping sequences still clamp when false).</summary>
        public bool Loop { get; set; } = true;

        /// <summary>Monotonic clock in milliseconds, drives global-sequence tracks.</summary>
        public float GlobalTimeMs { get; private set; }

        /// <summary>
        /// Camera view rotation expressed in model space (rows = camera right/up/back).
        /// Used by billboarded nodes; the preview updates it every frame.
        /// </summary>
        public Mat4 CameraRotationInModelSpace { get; set; } = Mat4.Identity;

        public bool EnableBillboards { get; set; } = true;

        /// <summary>Correction the engine applies to billboard nodes (Matrix4_rotate(30,0,90)).</summary>
        public static readonly Quat BillboardCorrection = Quat.FromEulerXYZ(30f, 0f, 90f);

        public MdxSequence Sequence
            => SequenceIndex >= 0 && SequenceIndex < Model.Sequences.Count ? Model.Sequences[SequenceIndex] : null;

        /// <summary>Model-space matrix of a node as evaluated for the current frame.</summary>
        public Mat4 GetNodeWorld(int nodeId)
            => nodeId >= 0 && nodeId < _nodeWorld.Length ? _nodeWorld[nodeId] : Mat4.Identity;

        public Mat4 GetNodeLocal(int nodeId)
            => nodeId >= 0 && nodeId < _nodeLocal.Length ? _nodeLocal[nodeId] : Mat4.Identity;

        /// <summary>World position of a node's pivot in model space.</summary>
        public Vec3 GetNodePivotWorld(int nodeId)
        {
            Vec3 pivot = nodeId >= 0 && nodeId < Model.Pivots.Count ? Model.Pivots[nodeId] : Vec3.Zero;
            return GetNodeWorld(nodeId).TransformPoint(pivot);
        }

        // ---- sequence playback -----------------------------------------------------------

        public void SetSequence(int index, bool resetTime = true)
        {
            if (index < -1 || index >= Model.Sequences.Count) return;
            bool changed = index != SequenceIndex;
            SequenceIndex = index;
            if (changed || resetTime)
            {
                MdxSequence sequence = Sequence;
                if (sequence != null)
                {
                    Frame = sequence.Start;
                    Loop = !sequence.NonLooping;
                }
                else
                {
                    Frame = 0f;
                }
            }
            Invalidate();
        }

        public void SetSequence(string name, bool resetTime = true)
        {
            int index = Model.FindSequenceIndex(name);
            if (index >= 0) SetSequence(index, resetTime);
        }

        /// <summary>Advances playback and evaluates the node hierarchy.</summary>
        public void Advance(float deltaSeconds)
        {
            GlobalTimeMs += deltaSeconds * 1000f;
            MdxSequence sequence = Sequence;
            if (Playing && sequence != null && sequence.Duration > 0)
            {
                float next = Frame + deltaSeconds * 1000f * Speed;
                if (next > sequence.End)
                {
                    if (Loop)
                    {
                        float length = Math.Max(1f, sequence.Duration);
                        next = sequence.Start + (next - sequence.Start) % length;
                    }
                    else
                    {
                        next = sequence.End;
                        Playing = false;
                    }
                }
                else if (next < sequence.Start)
                {
                    next = sequence.Start;
                }
                Frame = next;
            }
            Evaluate();
        }

        /// <summary>Scrubs to a normalised position in the current sequence and evaluates.</summary>
        public void SetSequenceRatio(float ratio)
        {
            MdxSequence sequence = Sequence;
            if (sequence == null) return;
            Frame = sequence.Start + Math.Clamp(ratio, 0f, 1f) * sequence.Duration;
            Evaluate();
        }

        public void Invalidate()
        {
            for (int i = 0; i < _nodeComputed.Length; i++) _nodeComputed[i] = false;
        }

        /// <summary>Builds the sample context for the current frame/sequence.</summary>
        public MdxSampleContext Context
        {
            get
            {
                MdxSequence sequence = Sequence;
                return new MdxSampleContext
                {
                    Frame = Frame,
                    SequenceStart = sequence?.Start ?? 0,
                    SequenceEnd = sequence?.End ?? int.MaxValue,
                    HasSequence = sequence != null,
                    GlobalTimeMs = GlobalTimeMs,
                };
            }
        }

        // ---- node evaluation -------------------------------------------------------------

        /// <summary>Recomputes the local and global matrix of every node (MDLX_BindBoneMatrices).</summary>
        public void Evaluate()
        {
            MdxSampleContext context = Context;
            IReadOnlyList<MdxNode> nodes = Model.NodeList;
            for (int i = 0; i < nodes.Count; i++)
            {
                MdxNode node = nodes[i];
                _nodeLocal[node.NodeId] = CalculateNodeMatrix(node, in context);
            }
            for (int i = 0; i < _nodeComputed.Length; i++) _nodeComputed[i] = false;
            for (int i = 0; i < nodes.Count; i++) ComputeGlobal(nodes[i], in context);
        }

        Mat4 CalculateNodeMatrix(MdxNode node, in MdxSampleContext context)
        {
            Vec3 translation = Vec3.Zero;
            Quat rotation = Quat.Identity;
            Vec3 scale = Vec3.One;

            if (node.Translation != null)
                translation = MdxTrackSampler.SampleVec3(Model, node.Translation, context, translation);
            if (node.Rotation != null)
                rotation = MdxTrackSampler.SampleQuat(Model, node.Rotation, context, rotation);
            if (node.Scaling != null)
                scale = MdxTrackSampler.SampleVec3(Model, node.Scaling, context, scale);

            bool hasTranslation = node.Translation != null;
            bool hasRotation = node.Rotation != null;
            bool hasScale = node.Scaling != null;

            Vec3 pivot = node.NodeId < Model.Pivots.Count ? Model.Pivots[node.NodeId] : Vec3.Zero;

            if (!hasTranslation && !hasRotation && !hasScale) return Mat4.Identity;
            if (hasTranslation && !hasRotation && !hasScale) return Mat4.FromTranslation(translation);
            if (!hasTranslation && hasRotation && !hasScale) return Mat4.ComposeOrigin(Vec3.Zero, rotation, Vec3.One, pivot);
            return Mat4.ComposeOrigin(translation, rotation, scale, pivot);
        }

        Mat4 ComputeGlobal(MdxNode node, in MdxSampleContext context)
        {
            int id = node.NodeId;
            if (id < 0 || id >= _nodeWorld.Length) return Mat4.Identity;
            if (_nodeComputed[id]) return _nodeWorld[id];

            Mat4 local = _nodeLocal[id];
            Mat4 global;
            if (node.Parent != null)
            {
                Mat4 parent = ComputeGlobal(node.Parent, in context);
                global = parent * local;
            }
            else
            {
                global = local;
            }

            if (EnableBillboards && (node.Flags & MdxNodeFlags.Billboarded) != 0)
                global = ApplyBillboard(node, global, in context);
            else if (EnableBillboards && (node.Flags & (MdxNodeFlags.BillboardedLockX | MdxNodeFlags.BillboardedLockY | MdxNodeFlags.BillboardedLockZ)) != 0)
                global = ApplyBillboardLocked(node, global, in context);

            _nodeWorld[id] = global;
            _nodeComputed[id] = true;
            return global;
        }

        /// <summary>
        /// R_GetNodeGlobalMatrix's billboard branch: strip the parent rotation,
        /// then align the node with the camera and re-anchor it on its pivot.
        /// </summary>
        Mat4 ApplyBillboard(MdxNode node, Mat4 global, in MdxSampleContext context)
        {
            Vec3 pivot = node.NodeId < Model.Pivots.Count ? Model.Pivots[node.NodeId] : Vec3.Zero;
            Vec3 pivotWorld = global.TransformPoint(pivot);

            if (node.Parent != null && _nodeComputed[node.Parent.NodeId])
            {
                Quat parentRotation = QuatUtil.FromMatrix(_nodeWorld[node.Parent.NodeId]);
                Mat4 strip = Mat4.ComposeOrigin(Vec3.Zero, parentRotation.Inverse(), Vec3.One, pivotWorld);
                global = strip * global;
            }

            Mat4 corrected = BillboardCorrection.ToMatrix() * CameraRotationInModelSpace;
            Quat viewRotation = QuatUtil.FromMatrix(corrected).Inverse();
            Mat4 face = Mat4.ComposeOrigin(Vec3.Zero, viewRotation, Vec3.One, pivotWorld);
            return face * global;
        }

        /// <summary>
        /// Axis-locked billboards keep their authored rotation around the locked
        /// axes and only billboard the remaining ones.
        /// </summary>
        Mat4 ApplyBillboardLocked(MdxNode node, Mat4 global, in MdxSampleContext context)
        {
            Mat4 full = ApplyBillboard(node, global, in context);
            bool lockX = (node.Flags & MdxNodeFlags.BillboardedLockX) != 0;
            bool lockY = (node.Flags & MdxNodeFlags.BillboardedLockY) != 0;
            bool lockZ = (node.Flags & MdxNodeFlags.BillboardedLockZ) != 0;

            // Locked axes keep the original basis row, the others take the camera-facing one.
            if (lockX) { full.M11 = global.M11; full.M12 = global.M12; full.M13 = global.M13; }
            if (lockY) { full.M21 = global.M21; full.M22 = global.M22; full.M23 = global.M23; }
            if (lockZ) { full.M31 = global.M31; full.M32 = global.M32; full.M33 = global.M33; }
            return full;
        }

        // ---- geoset / material / texture animation ---------------------------------------

        public MdxGeosetAnim FindGeosetAnim(int geosetIndex)
        {
            foreach (MdxGeosetAnim anim in Model.GeosetAnims)
                if (anim.GeosetId == geosetIndex) return anim;
            return null;
        }

        /// <summary>Geoset alpha from GEOA (KGAO) or its static value.</summary>
        public float GetGeosetAlpha(int geosetIndex)
        {
            MdxGeosetAnim anim = FindGeosetAnim(geosetIndex);
            if (anim == null) return 1f;
            return MdxTrackSampler.SampleFloat(Model, anim.Alpha, Context, anim.StaticAlpha);
        }

        /// <summary>Geoset tint from GEOA (KGAC) or its static (swizzled) value.</summary>
        public Vec3 GetGeosetColor(int geosetIndex)
        {
            MdxGeosetAnim anim = FindGeosetAnim(geosetIndex);
            if (anim == null) return Vec3.One;
            if ((anim.Flags & 2) != 0 && anim.Color != null)
                return MdxTrackSampler.SampleColor(Model, anim.Color, Context, Vec3.One);
            return MdxTrackSampler.SwizzleAuthoredColor(anim.StaticColor);
        }

        /// <summary>Layer alpha from KMTA or its static value.</summary>
        public float GetLayerAlpha(MdxMaterialLayer layer)
        {
            if (layer == null) return 1f;
            return MdxTrackSampler.SampleFloat(Model, layer.Alpha, Context, layer.StaticAlpha);
        }

        public float GetMaterialAlpha(MdxMaterial material)
        {
            if (material == null) return 1f;
            return MdxTrackSampler.SampleFloat(Model, material.Alpha, Context, 1f);
        }

        public float GetMaterialEmission(MdxMaterial material)
        {
            if (material == null) return 0f;
            return MdxTrackSampler.SampleFloat(Model, material.Emission, Context, 0f);
        }

        /// <summary>KMTF flipbook frame index (clamped to [0, frames-1]).</summary>
        public int GetLayerFlipbookFrame(MdxMaterialLayer layer, int frameCount)
        {
            if (layer == null || layer.Flipbook == null || frameCount <= 0) return 0;
            int frame = MdxTrackSampler.SampleInt(Model, layer.Flipbook, Context, 0);
            if (frame < 0) frame = 0;
            if (frame >= frameCount) frame = frameCount - 1;
            return frame;
        }

        public MdxTextureAnimState GetTextureAnim(int transformId)
        {
            var state = new MdxTextureAnimState { Translation = Vec3.Zero, Rotation = Quat.Identity, Scale = Vec3.One };
            if (transformId < 0 || transformId >= Model.TextureAnims.Count) return state;
            MdxTextureAnim anim = Model.TextureAnims[transformId];
            MdxSampleContext context = Context;
            state.Translation = MdxTrackSampler.SampleVec3(Model, anim.Translation, context, state.Translation);
            state.Rotation = MdxTrackSampler.SampleQuat(Model, anim.Rotation, context, state.Rotation);
            state.Scale = MdxTrackSampler.SampleVec3(Model, anim.Scaling, context, state.Scale);
            return state;
        }

        // ---- generic track helpers used by emitters, ribbons and lights -------------------

        public float SampleFloatTrack(MdxKeyTrack track, float fallback)
            => MdxTrackSampler.SampleFloat(Model, track, Context, fallback);

        public int SampleIntTrack(MdxKeyTrack track, int fallback)
            => MdxTrackSampler.SampleInt(Model, track, Context, fallback);

        public Vec3 SampleVec3Track(MdxKeyTrack track, Vec3 fallback)
            => MdxTrackSampler.SampleVec3(Model, track, Context, fallback);

        /// <summary>Colour tracks keep the file's BGR component order.</summary>
        public Vec3 SampleColorTrack(MdxKeyTrack track, Vec3 fallback)
            => MdxTrackSampler.SampleColor(Model, track, Context, fallback);

        // ---- attachments -----------------------------------------------------------------

        /// <summary>MDLX_CollectAttachmentPositions: resolved pivots plus world transforms.</summary>
        public List<MdxAttachmentPose> CollectAttachmentPoses(string prefix = null)
        {
            var result = new List<MdxAttachmentPose>();
            MdxSampleContext context = Context;
            foreach (MdxAttachment attachment in Model.Attachments)
            {
                if (!string.IsNullOrEmpty(prefix)
                    && !attachment.Node.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (attachment.Visibility != null)
                {
                    float visibility = MdxTrackSampler.SampleFloat(Model, attachment.Visibility, context, 1f);
                    if (visibility < 0.0001f) continue;
                }

                Vec3 pivot = attachment.Node.NodeId < Model.Pivots.Count
                    ? Model.Pivots[attachment.Node.NodeId]
                    : Vec3.Zero;
                Mat4 world = GetNodeWorld(attachment.Node.NodeId);

                result.Add(new MdxAttachmentPose
                {
                    Attachment = attachment,
                    Origin = world.TransformPoint(pivot),
                    Transform = world,
                });
            }
            return result;
        }

        // ---- cameras ---------------------------------------------------------------------

        public MdxCameraPose SampleCamera(int cameraIndex)
        {
            var pose = new MdxCameraPose();
            if (cameraIndex < 0 || cameraIndex >= Model.Cameras.Count) return pose;
            MdxCamera camera = Model.Cameras[cameraIndex];
            MdxSampleContext context = Context;

            Vec3 translation = MdxTrackSampler.SampleVec3(Model, camera.Translation, context, camera.Pivot);
            Vec3 target = MdxTrackSampler.SampleVec3(Model, camera.TargetTranslation, context, camera.TargetPivot);
            float roll = MdxTrackSampler.SampleFloat(Model, camera.Roll, context, 0f);

            pose.Position = camera.Pivot + translation;
            pose.Target = camera.TargetPivot + target;
            pose.Roll = roll;
            pose.FieldOfView = camera.FieldOfView;
            pose.NearClip = camera.NearClip;
            pose.FarClip = camera.FarClip;
            return pose;
        }

        // ---- event keys ------------------------------------------------------------------

        /// <summary>
        /// MDLX_EventKeyCrossed with floating frame times: did the authored event
        /// key fall inside the interval the playhead just swept?
        /// </summary>
        public bool EventKeyCrossed(MdxEventObject evt, int key, float previousFrame, float currentFrame, float previousTimeMs, float currentTimeMs)
        {
            if (evt == null) return false;

            if (evt.GlobalSequenceId >= 0)
            {
                if (evt.GlobalSequenceId >= Model.GlobalSequences.Count) return false;
                float duration = Model.GlobalSequences[evt.GlobalSequenceId];
                if (duration <= 0f) return false;
                if (currentTimeMs >= previousTimeMs && currentTimeMs - previousTimeMs >= duration) return true;
                float previous = previousTimeMs % duration;
                float current = currentTimeMs % duration;
                return current >= previous ? (key > previous && key <= current)
                                           : (key > previous || key <= current);
            }

            MdxSequence previousSequence = FindSequenceAtTime(previousFrame);
            MdxSequence currentSequence = FindSequenceAtTime(currentFrame);
            if (currentSequence == null || key < currentSequence.Start || key > currentSequence.End) return false;
            if (!ReferenceEquals(previousSequence, currentSequence))
                return key >= currentSequence.Start && key <= currentFrame;
            if (currentFrame >= previousFrame) return key > previousFrame && key <= currentFrame;
            return key > previousFrame || key <= currentFrame;
        }

        /// <summary>Engine R_FindSequenceAtTime: interval[0] &lt;= time &lt; interval[1].</summary>
        public MdxSequence FindSequenceAtTime(float frame)
        {
            foreach (MdxSequence sequence in Model.Sequences)
                if (frame >= sequence.Start && frame < sequence.End) return sequence;
            return null;
        }

        /// <summary>Every event whose key was crossed by this step.</summary>
        public List<MdxEventObject> CollectCrossedEvents(float previousFrame, float previousTimeMs)
        {
            var events = new List<MdxEventObject>();
            float previous = previousFrame;
            float current = Frame;
            float previousGlobal = previousTimeMs;
            float currentGlobal = GlobalTimeMs;
            foreach (MdxEventObject evt in Model.Events)
                foreach (int key in evt.Keys)
                    if (EventKeyCrossed(evt, key, previous, current, previousGlobal, currentGlobal))
                    {
                        events.Add(evt);
                        break;
                    }
            return events;
        }
    }

    /// <summary>Quaternion helpers missing from the plain math struct.</summary>
    public static class QuatUtil
    {
        public static Quat Inverse(this Quat q)
        {
            float lengthSquared = q.LengthSquared;
            if (lengthSquared < 1e-20f) return Quat.Identity;
            float inv = 1f / lengthSquared;
            return new Quat(-q.X * inv, -q.Y * inv, -q.Z * inv, q.W * inv);
        }

        /// <summary>Rotation matrix -> quaternion (Shoemake), matching Quaternion_fromMatrix.</summary>
        public static Quat FromMatrix(Mat4 m)
        {
            float m00 = m.M11, m01 = m.M12, m02 = m.M13;
            float m10 = m.M21, m11 = m.M22, m12 = m.M23;
            float m20 = m.M31, m21 = m.M32, m22 = m.M33;

            float Element(int row, int column) => (row, column) switch
            {
                (0, 0) => m00, (0, 1) => m01, (0, 2) => m02,
                (1, 0) => m10, (1, 1) => m11, (1, 2) => m12,
                (2, 0) => m20, (2, 1) => m21, (2, 2) => m22,
                _ => 0f,
            };

            float trace = m00 + m11 + m22;
            Quat q;
            if (trace > 0f)
            {
                float root = MathF.Sqrt(trace + 1f);
                q.W = 0.5f * root;
                root = 0.5f / root;
                q.X = (Element(2, 1) - Element(1, 2)) * root;
                q.Y = (Element(0, 2) - Element(2, 0)) * root;
                q.Z = (Element(1, 0) - Element(0, 1)) * root;
            }
            else
            {
                int i = 0;
                if (m11 > m00) i = 1;
                if (m22 > Element(i, i)) i = 2;
                int j = (i + 1) % 3;
                int k = (j + 1) % 3;

                float root = MathF.Sqrt(Element(i, i) - Element(j, j) - Element(k, k) + 1f);
                float inv = root > 1e-20f ? 0.5f / root : 0f;

                float[] components = new float[3];
                components[i] = 0.5f * root;
                components[j] = (Element(i, j) + Element(j, i)) * inv;
                components[k] = (Element(i, k) + Element(k, i)) * inv;
                q.X = components[0];
                q.Y = components[1];
                q.Z = components[2];
                q.W = (Element(k, j) - Element(j, k)) * inv;
            }
            return q.Normalized();
        }
    }
}
