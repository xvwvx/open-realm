using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using MdxPreview.Core.Mdx;
using MdxPreview.Runtime;

namespace MdxPreview.Export
{
    /// <summary>
    /// Bakes the live preview into plain Godot resources: one <c>.tscn</c> scene
    /// (geometry + skeleton + one baked <see cref="Animation"/> per MDX sequence),
    /// the texture PNGs it references plus a Chinese <c>.info.txt</c> report.
    ///
    /// Everything the scene needs is written next to it, so the exported files keep
    /// working after the Warcraft III archives (and this addon) are gone.
    /// </summary>
    public static class MdxExporter
    {
        /// <summary>Sampling rate of the baked animations.</summary>
        public const int DefaultAnimationFps = 30;

        /// <summary>Sub folder holding the exported texture PNGs.</summary>
        public const string TextureSubFolder = "textures";

        /// <summary>One exported artifact: the scene, every PNG and the info text.</summary>
        public sealed class Result
        {
            public bool Success;
            /// <summary>Chinese, human readable summary of what happened.</summary>
            public string Message = string.Empty;
            /// <summary><c>res://</c> paths of every generated file (scene first).</summary>
            public Godot.Collections.Array<string> Files = new Godot.Collections.Array<string>();

            public string ScenePath = string.Empty;
            public string InfoPath = string.Empty;
            public string OutputFolder = string.Empty;

            public static Result Failure(string message) => new Result { Success = false, Message = message };
        }

        /// <summary>Which texture (and which baked PNG) one material layer uses.</summary>
        sealed class TextureJob
        {
            public Texture2D Source;
            public string ResourcePath = string.Empty;
            /// <summary>Path stored in the scene, relative to the scene file (e.g. "textures/x.png").</summary>
            public string SceneReference = string.Empty;
            public string GlobalPath = string.Empty;
            public long Size;
            public string VirtualPath = string.Empty;
            public uint ReplaceableId;
            public string FileName = string.Empty;
        }

        sealed class GeosetExport
        {
            public MdxGeoset Geoset;
            public MeshInstance3D Node;
            public List<StandardMaterial3D> Materials = new List<StandardMaterial3D>();
        }

        static string Stringify(Variant value) => value.VariantType switch
        {
            Variant.Type.Nil => string.Empty,
            Variant.Type.String => value.AsString(),
            Variant.Type.StringName => value.AsString(),
            _ => value.ToString(),
        };

        /// <summary>
        /// Bakes <paramref name="modelNode"/> into <paramref name="outputFolder"/>,
        /// which may be a <c>res://</c> path or an absolute folder. A null/empty
        /// folder means <see cref="MdxPreviewSettings.DefaultOutputFolder"/>.
        /// Never throws: failures come back as <c>Success = false</c> plus a Chinese
        /// message.
        /// </summary>
        public static Result Export(MdxModelNode modelNode, MdxAssetLibrary assets, string outputFolder)
            => Export(modelNode, assets, outputFolder, DefaultAnimationFps);

        /// <summary>Same as <see cref="Export(MdxModelNode, MdxAssetLibrary, string)"/> with an explicit sample rate.</summary>
        public static Result Export(MdxModelNode modelNode, MdxAssetLibrary assets, string outputFolder, int fps, float scale = 1f)
        {
            if (modelNode == null) return Result.Failure("导出失败：没有可导出的模型节点。");
            if (modelNode.Model == null || modelNode.Instance == null)
                return Result.Failure("导出失败：模型节点尚未构建（请先加载模型）。");

            MdxModel model = modelNode.Model;
            if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = MdxPreviewSettings.DefaultOutputFolder;
            if (fps <= 0) fps = DefaultAnimationFps;

            // Save the preview playhead so the dock keeps showing what the user had.
            int restoreSequence = modelNode.Instance.SequenceIndex;
            float restoreFrame = modelNode.Instance.Frame;
            bool restorePlaying = modelNode.Instance.Playing;
            bool restoreLoop = modelNode.Instance.Loop;

            // Everything below runs inside a try/catch: the editor dock must never see
            // an exception from an export.
            var job = new TextureJobSet { Assets = assets };
            var exported = new ExportedScene();
            var result = new Result();

            try
            {
                string resourceFolder = MdxExportPaths.ToResourcePath(outputFolder);
                Error directoryError = MdxExportPaths.MakeDirectory(resourceFolder, out string globalFolder);
                if (directoryError != Error.Ok)
                    return Result.Failure($"导出失败：无法创建输出目录 {outputFolder}（{directoryError}）。");

                string baseName = ResolveBaseName(model, modelNode.SourcePath);
                string sceneFile = baseName + ".tscn";
                string infoFile = baseName + ".info.txt";
                string scenePath = MdxExportPaths.CombineResource(resourceFolder, sceneFile);
                string infoPath = MdxExportPaths.CombineResource(resourceFolder, infoFile);
                string sceneGlobalPath = Path.Combine(globalFolder, sceneFile);
                string textureFolder = MdxExportPaths.CombineResource(resourceFolder, TextureSubFolder);
                bool overwritten = File.Exists(sceneGlobalPath);

                result.OutputFolder = resourceFolder;
                result.ScenePath = scenePath;
                result.InfoPath = infoPath;

                // ---- 1. node graph: root + skeleton + one MeshInstance3D per geoset --------
                BuildScene(modelNode, model, baseName, job, exported, scale);

                // ---- 2. bake one Animation per MDX sequence --------------------------------
                string animationWarning;
                int animationCount = BuildAnimations(model, exported, fps, out animationWarning);

                // ---- 3. write the texture PNGs and repoint the materials -------------------
                string textureWarning;
                List<TextureJob> savedTextures = SaveTextures(job, textureFolder, out textureWarning);
                ApplyBakedTexturesToScene(exported, job);

                // ---- 4. save the scene ------------------------------------------------------
                // Two conditions have to hold before PackedScene.Pack keeps the hierarchy:
                //   * every descendant's owner must be the packed root (Godot's AddChild assigns
                //     the direct parent instead, which would strip whole subtrees);
                //   * the root must be inside a tree.
                exported.Root.Owner = null;
                modelNode.AddChild(exported.Root);
                SetOwnership(exported.Root);
                var packed = new PackedScene();
                Error packError = packed.Pack(exported.Root);
                if (packError != Error.Ok)
                    return Result.Failure($"导出失败：打包场景失败（{packError}）。");

                Error saveError = ResourceSaver.Save(packed, scenePath);
                if (saveError != Error.Ok)
                    return Result.Failure($"导出失败：保存场景 {scenePath} 失败（{saveError}）。");

                // ---- 5. info file + file list ------------------------------------------------
                result.Files.Add(scenePath);
                foreach (TextureJob texture in savedTextures) result.Files.Add(texture.ResourcePath);
                result.Files.Add(infoPath);

                string infoWarning = string.Empty;
                try
                {
                    WriteInfoFile(model, modelNode, infoPath, resourceFolder, scenePath, baseName,
                        exported, savedTextures, animationCount, fps);
                }
                catch (Exception ex)
                {
                    infoWarning = $"信息文件写入失败：{ex.Message}";
                }

                result.Success = true;
                result.Message = BuildMessage(model, baseName, sceneFile, resourceFolder, overwritten,
                    exported, savedTextures, animationCount, fps,
                    CombineWarnings(exported.Warning, animationWarning, textureWarning, infoWarning));
                return result;
            }
            catch (Exception ex)
            {
                return Result.Failure($"导出失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { exported.Root?.Free(); }
                catch (Exception) { /* releasing a half built scene must not fail the export */ }

                // Never disturb what the user is looking at in the preview.
                try
                {
                    modelNode.Instance.Playing = false;
                    modelNode.Instance.SetSequence(restoreSequence, false);
                    modelNode.Instance.Frame = restoreFrame;
                    modelNode.Instance.Loop = restoreLoop;
                    modelNode.Instance.Evaluate();
                    modelNode.Instance.Playing = restorePlaying;
                }
                catch (Exception)
                {
                    // Restoring preview state must not mask the real result.
                }
            }
        }

        // ---- scene graph ---------------------------------------------------------------------

        /// <summary>
        /// Assigns the exported root as the owner of every node below it. Without this a
        /// <see cref="PackedScene"/> would contain only the root node, because Godot only
        /// serialises descendants that belong to the packed root.
        /// </summary>
        static void SetOwnership(Node root)
        {
            SetOwnership(root, root);
        }

        static void SetOwnership(Node node, Node root)
        {
            foreach (Node child in node.GetChildren())
            {
                child.Owner = root;
                SetOwnership(child, root);
            }
        }

        /// <summary>
        /// Compact "name(child,child)" dump of an owner-annotated subtree; used by the probe when
        /// a packed scene comes out smaller than expected.
        /// </summary>
        public static string DescribeTree(Node node, Node root)
        {
            var text = new System.Text.StringBuilder();
            text.Append(node.Name).Append('[').Append(node.GetClass());
            text.Append(node.Owner == root ? ",owned" : ",owner=").Append(node.Owner == root ? string.Empty : node.Owner?.Name.ToString() ?? "-");
            text.Append(']');
            int count = node.GetChildCount();
            if (count == 0) return text.ToString();
            text.Append('(');
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(DescribeTree(node.GetChild(i), root));
            }
            text.Append(')');
            return text.ToString();
        }

        sealed class ExportedScene
        {
            public Node3D Root;
            public Skeleton3D Skeleton;
            public Node3D MeshRoot;
            public readonly List<GeosetExport> Geosets = new List<GeosetExport>();
            public string Warning = string.Empty;
        }

        static void BuildScene(MdxModelNode modelNode, MdxModel model, string baseName, TextureJobSet job, ExportedScene exported, float scale = 1f)
        {
            // Same Z-up -> Y-up correction the preview uses, so the scene opens upright.
            var root = new Node3D
            {
                Name = baseName,
                RotationDegrees = new Vector3(-90f, 0f, 0f),
                // MDX 是 WC3 单位（模型上百单位高），按导出缩放换算到 Godot 的米制；
                // 粒子/骨骼/网格都在这个根节点下，因此整体等比缩放。
                Scale = Vector3.One * MathF.Max(0.0001f, scale),
            };
            // （原先此处 root.SetProcess(false)：Godot 4 的处理开关是逐节点的，对导出的普通场景没有意义，
            //   且与播放器那次误用同源，故一并移除。）
            exported.Root = root;

            int nodeCount = Math.Max(1, model.Nodes.Length);
            var skeleton = new Skeleton3D { Name = "Skeleton" };
            root.AddChild(skeleton);
            exported.Skeleton = skeleton;

            for (int i = 0; i < nodeCount; i++)
            {
                MdxNode node = model.GetNode(i);
                string name = node?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = $"node_{i}";
                // Godot 4.7 rejects bone names containing ':' or '/', so the MDX node id is
                // separated with '_' instead of the preview's "id:name".
                int bone = skeleton.AddBone($"{i}_{MdxExportPaths.Sanitize(name, $"node_{i}")}");
                // Rest poses are identity: the baked animation drives the bones directly.
                skeleton.SetBoneRest(bone, Transform3D.Identity);
            }
            for (int i = 0; i < nodeCount; i++)
            {
                MdxNode node = model.GetNode(i);
                if (node == null) continue;
                if (node.ParentId >= 0 && node.ParentId < nodeCount && model.GetNode(node.ParentId) != null)
                    skeleton.SetBoneParent(i, node.ParentId);
            }

            // Meshes sit under the skeleton so MeshInstance3D.Skeleton can point at it.
            var meshRoot = new Node3D { Name = "Meshes" };
            skeleton.AddChild(meshRoot);
            exported.MeshRoot = meshRoot;

            // 粒子/飘带：导出为 Godot 原生粒子节点（挂在对应骨骼的 BoneAttachment3D 上）。
            var particleNotes = new System.Collections.Generic.List<string>();
            int nativeParticles = BuildNativeParticles(model, skeleton, job, particleNotes);
            if (nativeParticles > 0) GD.Print($"[MDX Preview] 已导出 {nativeParticles} 个原生粒子节点。");
            foreach (string note in particleNotes) GD.Print($"[MDX Preview] 粒子：{note}");

            // 附着点（ATCH）导出为 Marker3D：挂在对应骨骼的 BoneAttachment3D 上，
            // 位置 = 该节点 pivot；并把 MDX 的附着点名（如 "overhead ref"）写进分组便于脚本查找。
            int markerCount = BuildAttachmentMarkers(model, skeleton);
            if (markerCount > 0) GD.Print($"[MDX Preview] 已导出 {markerCount} 个附着点 Marker3D。");
            for (int geosetIndex = 0; geosetIndex < model.Geosets.Count; geosetIndex++)
            {
                MdxGeoset geoset = model.Geosets[geosetIndex];
                ArrayMesh mesh = MdxMeshBuilder.Build(geoset);
                if (mesh == null) continue;

                var meshNode = new MeshInstance3D
                {
                    Name = $"geoset_{geosetIndex}",
                    Mesh = mesh,
                };

                var entry = new GeosetExport { Geoset = geoset, Node = meshNode };

                MdxMaterial material = model.GetMaterial(geoset.MaterialId);
                StandardMaterial3D previous = null;
                if (material != null)
                {
                    foreach (MdxMaterialLayer layer in material.Layers)
                    {
                        StandardMaterial3D layerMaterial = MdxMaterialBuilder.CreateLayerMaterial(
                            job.Assets, model, layer, out int textureId);
                        // Bake the frame-independent state the preview applies every tick.
                        ApplyStaticMaterialState(modelNode.Instance, geosetIndex, layer, layerMaterial);
                        job.Register(layerMaterial, model, textureId);

                        entry.Materials.Add(layerMaterial);
                        if (previous == null) meshNode.MaterialOverride = layerMaterial;
                        else previous.NextPass = layerMaterial;
                        previous = layerMaterial;
                    }
                }

                if (meshNode.MaterialOverride == null)
                    meshNode.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 1f, 1f, 0.5f) };

                meshRoot.AddChild(meshNode);
                // Skeleton link: MeshInstance3D resolves the path relative to itself, so it has to
                // be computed once the node sits in the hierarchy (Meshes/geoset_N -> Skeleton).
                meshNode.Skeleton = SkeletonPath(meshNode, skeleton);
                exported.Geosets.Add(entry);
            }

            if (exported.Geosets.Count == 0) exported.Warning = "模型没有任何可导出的几何体。";
        }

        /// <summary>
        /// Path the MeshInstance3D uses for <see cref="MeshInstance3D.Skeleton"/>. Godot resolves
        /// it relative to the mesh node itself, so <c>GetPathTo</c> on the mesh gives the right
        /// answer ("../.." for the Meshes/geoset_0/Skeleton layout the exporter builds).
        /// </summary>
        static string SkeletonPath(Node3D meshNode, Skeleton3D skeleton)
            => meshNode.GetPathTo(skeleton).ToString();

        /// <summary>
        /// Folds the geoset/layer alpha and tint the preview recomputes every frame
        /// into the exported material, so the baked look matches the preview.
        /// </summary>
        static void ApplyStaticMaterialState(MdxInstance instance, int geosetIndex, MdxMaterialLayer layer, StandardMaterial3D material)
        {
            if (instance == null || material == null || layer == null) return;
            bool hasGeosetAnim = instance.FindGeosetAnim(geosetIndex) != null;
            float geosetAlpha = hasGeosetAnim ? instance.GetGeosetAlpha(geosetIndex) : 1f;
            Core.Math3D.Vec3 geosetColor = hasGeosetAnim ? instance.GetGeosetColor(geosetIndex) : Core.Math3D.Vec3.One;
            float alpha = geosetAlpha * instance.GetLayerAlpha(layer);

            Color baseColor = material.AlbedoColor;
            material.AlbedoColor = new Color(
                baseColor.R * geosetColor.X,
                baseColor.G * geosetColor.Y,
                baseColor.B * geosetColor.Z,
                baseColor.A * alpha);
        }

        // ---- animation baking ----------------------------------------------------------------

        static int BuildAnimations(MdxModel model, ExportedScene exported, int fps, out string warning)
        {
            warning = string.Empty;
            if (model.Sequences.Count == 0)
            {
                warning = "模型没有动画序列，仅导出几何与材质。";
                return 0;
            }

            var player = new AnimationPlayer
            {
                Name = "AnimationPlayer",
                // The player is a child of the exported root and the bone tracks are relative to
                // "..". An absolute path cannot be used here: the root has no path yet while the
                // scene is being built, and an empty path would disable pose playback entirely.
                RootNode = new NodePath(".."),
            };
            // 这里原本调用 player.SetProcess(false)：那会让导出的场景里播放器不推进时间，
            // 打开场景后无论怎么点播放都不会动。导出的是给人用的普通场景，必须保持处理开启。
            exported.Root.AddChild(player);

            var library = new AnimationLibrary();
            int count = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int sequenceIndex = 0; sequenceIndex < model.Sequences.Count; sequenceIndex++)
            {
                MdxSequence sequence = model.Sequences[sequenceIndex];
                Animation animation = BakeSequence(model, exported.Skeleton, sequenceIndex, fps);
                if (animation == null) continue;

                animation.LoopMode = sequence.NonLooping ? Animation.LoopModeEnum.None : Animation.LoopModeEnum.Linear;
                library.AddAnimation(UniqueAnimationName(sequence, sequenceIndex, usedNames), animation);
                count++;
            }

            // Godot 4 always plays animations out of a library; "" is the default one.
            player.AddAnimationLibrary(string.Empty, library);

            if (count == 0) warning = "所有序列时长为 0，未生成有效的动画轨道。";
            return count;
        }

        static string UniqueAnimationName(MdxSequence sequence, int index, HashSet<string> used)
        {
            string name = MdxExportPaths.Sanitize(sequence?.Name, $"sequence_{index}");
            if (string.IsNullOrWhiteSpace(name)) name = $"sequence_{index}";
            if (used.Add(name)) return name;
            string candidate = $"{name}_{index}";
            int counter = 1;
            while (!used.Add(candidate)) candidate = $"{name}_{index}_{counter++}";
            return candidate;
        }

        /// <summary>
        /// Samples one MDX sequence into an <see cref="Animation"/> at
        /// <paramref name="fps"/> frames per second.
        ///
        /// The preview drives its Skeleton3D through <c>SetBoneGlobalPoseOverride</c>;
        /// the baked animation instead stores the <b>local</b> pose per bone
        /// (<c>pose_local = inverse(parentGlobal) * global</c>). Because every rest pose
        /// is identity, Godot composes the same globals back out of those locals
        /// (skin matrix = global pose), so the deformed result is equivalent.
        /// </summary>
        static Animation BakeSequence(MdxModel model, Skeleton3D skeleton, int sequenceIndex, int fps)
        {
            MdxSequence sequence = model.Sequences[sequenceIndex];
            int boneCount = skeleton?.GetBoneCount() ?? 0;

            int durationMs = Math.Max(0, sequence.Duration);
            int sampleRate = Math.Max(1, fps);
            // Always at least two samples so the curve has a well defined length.
            int sampleCount = Math.Max(2, (int)MathF.Round(durationMs * sampleRate / 1000f) + 1);
            float length = Math.Max(1f / sampleRate, durationMs / 1000f);

            var animation = new Animation { Length = length, Step = 1f / sampleRate };
            if (boneCount == 0) return animation; // geometry-only export

            // Godot 4.7 的 Skeleton3D 暴露的骨骼属性是三条独立属性：
            //   bones/<i>/position (Vector3) · bones/<i>/rotation (Quaternion) · bones/<i>/scale (Vector3)
            // 没有 "pose" 形式的单条属性（bone_pose_<i> 与 bone_<i>/pose 都解析不到，动画面板里
            // 会显示为“叉”）。因此每根骨骼要写三条轨道。
            // 轨道相对 AnimationPlayer 的 RootNode（".."）解析，所以节点名必须用导出场景里
            // 骨骼节点的真实名字（导出器叫 "Skeleton"，不是 "Skeleton3D"）。
            string skeletonName = string.IsNullOrEmpty(skeleton.Name) ? "Skeleton" : skeleton.Name.ToString();
            var positionTracks = new int[boneCount];
            var rotationTracks = new int[boneCount];
            var scaleTracks = new int[boneCount];
            for (int bone = 0; bone < boneCount; bone++)
            {
                positionTracks[bone] = animation.AddTrack(Animation.TrackType.Value);
                animation.TrackSetPath(positionTracks[bone], new NodePath($"{skeletonName}:bones/{bone}/position"));

                rotationTracks[bone] = animation.AddTrack(Animation.TrackType.Value);
                animation.TrackSetPath(rotationTracks[bone], new NodePath($"{skeletonName}:bones/{bone}/rotation"));

                scaleTracks[bone] = animation.AddTrack(Animation.TrackType.Value);
                animation.TrackSetPath(scaleTracks[bone], new NodePath($"{skeletonName}:bones/{bone}/scale"));
            }

            // A private instance keeps the preview's own playhead untouched.
            var instance = new MdxInstance(model) { Playing = false, EnableBillboards = false };
            instance.SetSequence(sequenceIndex, true);

            // 关键帧去重：只在数值发生变化时插入关键帧，连续相同的中间帧全部省略。
            // 线性插值下这不改变曲线形状（恒定段仍是恒定段），但能大幅减少关键帧数量
            // —— 导出文件体积与加载时间都随之下降。
            var lastPosition = new Vector3[boneCount];
            var lastRotation = new Quaternion[boneCount];
            var lastScale = new Vector3[boneCount];
            var lastPositionSet = new bool[boneCount];
            var finalPosition = new Vector3[boneCount];
            var finalRotation = new Quaternion[boneCount];
            var finalScale = new Vector3[boneCount];

            for (int sample = 0; sample < sampleCount; sample++)
            {
                float time = length * sample / (sampleCount - 1);
                float frame = durationMs <= 0
                    ? sequence.Start
                    : sequence.Start + durationMs * (sample / (float)(sampleCount - 1));

                instance.Frame = frame;
                instance.Evaluate();

                for (int bone = 0; bone < boneCount; bone++)
                {
                    Transform3D global = MdxMeshBuilder.ToTransform(instance.GetNodeWorld(bone));
                    int parent = skeleton.GetBoneParent(bone);
                    Transform3D pose = parent >= 0
                        ? MdxMeshBuilder.ToTransform(instance.GetNodeWorld(parent)).AffineInverse() * global
                        : global;

                    Vector3 position = pose.Origin;
                    Quaternion rotation = pose.Basis.GetRotationQuaternion();
                    Vector3 scale = pose.Basis.Scale;

                    finalPosition[bone] = position;
                    finalRotation[bone] = rotation;
                    finalScale[bone] = scale;

                    bool changed = !lastPositionSet[bone]
                        || !ApproximatelyEqual(position, lastPosition[bone])
                        || !ApproximatelyEqual(rotation, lastRotation[bone])
                        || !ApproximatelyEqual(scale, lastScale[bone]);
                    if (!changed) continue;

                    animation.TrackInsertKey(positionTracks[bone], time, position);
                    animation.TrackInsertKey(rotationTracks[bone], time, rotation);
                    animation.TrackInsertKey(scaleTracks[bone], time, scale);

                    lastPosition[bone] = position;
                    lastRotation[bone] = rotation;
                    lastScale[bone] = scale;
                    lastPositionSet[bone] = true;
                }
            }

            // 末尾补一帧：保证动画长度由最后一个采样点定义（去重后可能没有末尾关键帧）。
            for (int bone = 0; bone < boneCount; bone++)
            {
                InsertFinalKeyIfNeeded(animation, positionTracks[bone], length, finalPosition[bone]);
                InsertFinalKeyIfNeeded(animation, rotationTracks[bone], length, finalRotation[bone]);
                InsertFinalKeyIfNeeded(animation, scaleTracks[bone], length, finalScale[bone]);
            }

            for (int bone = 0; bone < boneCount; bone++)
            {
                foreach (int track in new[] { positionTracks[bone], rotationTracks[bone], scaleTracks[bone] })
                {
                    animation.TrackSetInterpolationType(track, Animation.InterpolationType.Linear);
                    animation.TrackSetInterpolationLoopWrap(track, !sequence.NonLooping);
                    animation.ValueTrackSetUpdateMode(track, Animation.UpdateMode.Continuous);
                }
                // 旋转用四元数线性插值，避免欧拉分解带来的抖动。
                animation.TrackSetInterpolationType(rotationTracks[bone], Animation.InterpolationType.Linear);
            }

            return animation;
        }

        /// <summary>
        /// 把模型的粒子发射器导出为 Godot 原生粒子节点（不再使用插件的自研粒子系统）：
        /// PRE2 → <see cref="GpuParticles3D"/> + <see cref="ParticleProcessMaterial"/> + 广告牌四边形绘制；
        /// 每个发射器挂在 <see cref="BoneAttachment3D"/> 上，因此跟随烘焙动画的对应骨骼。
        /// </summary>
        /// <summary>
        /// 去重后补上末尾关键帧：若最后一次插入的关键帧未到动画末尾，则在末尾插入最终值，
        /// 保证动画长度与曲线终点正确。
        /// </summary>
        static void InsertFinalKeyIfNeeded(Animation animation, int track, float length, Variant value)
        {
            int keyCount = animation.TrackGetKeyCount(track);
            if (keyCount > 0 && Mathf.IsEqualApprox(animation.TrackGetKeyTime(track, keyCount - 1), length)) return;
            animation.TrackInsertKey(track, length, value);
        }

        /// <summary>关键帧去重用的近似比较（位置/缩放 1e-5、四元数 1e-6，足以滤掉浮点噪声）。</summary>
        static bool ApproximatelyEqual(Vector3 a, Vector3 b)
            => Mathf.Abs(a.X - b.X) < 1e-5f && Mathf.Abs(a.Y - b.Y) < 1e-5f && Mathf.Abs(a.Z - b.Z) < 1e-5f;

        static bool ApproximatelyEqual(Quaternion a, Quaternion b)
            => Mathf.Abs(a.X - b.X) < 1e-6f && Mathf.Abs(a.Y - b.Y) < 1e-6f
               && Mathf.Abs(a.Z - b.Z) < 1e-6f && Mathf.Abs(a.W - b.W) < 1e-6f;

        static int BuildNativeParticles(MdxModel model, Skeleton3D skeleton, TextureJobSet job, List<string> warnings)
        {
            int created = 0;
            for (int i = 0; i < model.ParticleEmitters2.Count; i++)
            {
                MdxParticleEmitter2 emitter = model.ParticleEmitters2[i];
                if (emitter?.Node == null) continue;

                float lifespan = MathF.Max(0.05f, emitter.LifeSpan);
                float rate = MathF.Max(0f, emitter.EmissionRate);
                bool squirt = emitter.Squirt != 0;

                var particles = new GpuParticles3D
                {
                    Name = $"particles2_{i}",
                    LocalCoords = true,
                    Lifetime = lifespan,
                    // GPUParticles3D 默认 emitting = false（不发射），导出场景必须显式打开，
                    // 否则打开场景后粒子一直不动。
                    Emitting = true,
                    // squirt = 每个 lifespan 循环爆发一批（原生下最接近游戏里"一涌一收"的表达）；
                    // 连续发射时 Amount = 同时存在数 = rate × lifespan。
                    OneShot = false,
                    Explosiveness = squirt ? 1f : 0f,
                    Preprocess = lifespan,
                    Amount = Math.Clamp((int)MathF.Round(squirt ? rate : rate * lifespan), 1, 4000),
                    // Godot 按 visibility_aabb 剔除 GPU 粒子；默认只有 ±4 单位，而 MDX 粒子的
                    // 初速/重力常常把粒子推出这个盒子，于是整批粒子被剔除、看起来"不播放"。
                    VisibilityAabb = ParticleVisibilityAabb(emitter.Speed, emitter.Variation, emitter.Gravity, lifespan),
                };

                float baseSize = emitter.ParticleScaling != null && emitter.ParticleScaling.Length > 1
                    ? MathF.Max(0.0001f, emitter.ParticleScaling[1])
                    : 1f;

                var process = new ParticleProcessMaterial
                {
                    EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                    // MDX 的发射体是节点局部空间里 X=width、Y=length 的矩形。
                    EmissionBoxExtents = new Vector3(emitter.Width * 0.5f, emitter.Length * 0.5f, 0.01f),
                    Direction = new Vector3(0f, 0f, 1f),
                    Spread = MathF.Max(0f, emitter.Latitude),
                    InitialVelocityMin = MathF.Max(0f, emitter.Speed - emitter.Variation),
                    InitialVelocityMax = MathF.Max(0f, emitter.Speed + emitter.Variation),
                    // 与预览/引擎一致：MDX 空间 Z 朝上（节点位于做过 Z-up→Y-up 旋转的根节点下）。
                    Gravity = new Vector3(0f, 0f, -emitter.Gravity),
                    ColorRamp = BuildParticleColorRamp(emitter),
                    ScaleCurve = BuildParticleScaleCurve(emitter, baseSize),
                };
                particles.ProcessMaterial = process;

                // 粒子的 filter 语义与几何层不同（0=Blend / 1=Additive / 2=Modulate /
                // 3=Modulate2x / 4=AlphaKey）。用几何层的映射会把 filter=0 变成"不透明"，
                // 贴图的透明区就会渲染成纯黑 —— 所以这里必须走粒子专用映射。
                var drawMaterial = new StandardMaterial3D();
                MdxMaterialBuilder.ApplyParticleFilter(drawMaterial, emitter.FilterMode);
                drawMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                int textureId = emitter.TextureId;
                if (textureId >= 0 && textureId < model.Textures.Count)
                    drawMaterial.AlbedoTexture = job.Assets.LoadTexture(
                        model.Textures[textureId].Path, model.Textures[textureId].ReplaceableId);
                drawMaterial.BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles;
                drawMaterial.BillboardKeepScale = true;
                drawMaterial.VertexColorUseAsAlbedo = true;
                drawMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                int rows = Math.Max(1, emitter.Rows);
                int columns = Math.Max(1, emitter.Columns);
                drawMaterial.ParticlesAnimHFrames = columns;
                drawMaterial.ParticlesAnimVFrames = rows;
                drawMaterial.ParticlesAnimLoop = true;
                job.Register(drawMaterial, model, textureId);

                // 尺寸：MDX 的 ParticleScaling 是"世界单位下的绝对尺寸"，而 Godot 的
                // ScaleCurve 会再乘到四边形上。把中段尺寸放到四边形、曲线归一化到 1，
                // 这样最终尺寸就等于作者的数值（不再出现与原始大小不一致）。
                particles.DrawPass1 = new QuadMesh
                {
                    Size = new Vector2(baseSize, baseSize),
                    Material = drawMaterial,
                };

                var attachment = new BoneAttachment3D
                {
                    Name = $"particles2_{i}_attach",
                    BoneIdx = emitter.Node.NodeId,
                };
                attachment.AddChild(particles);
                particles.Position = new Vector3(emitter.Node.Pivot.X, emitter.Node.Pivot.Y, emitter.Node.Pivot.Z);
                skeleton.AddChild(attachment);
                created++;
            }

            if (model.ParticleEmitters1.Count > 0 && warnings != null)
                warnings.Add($"PREM 精灵粒子暂未导出（{model.ParticleEmitters1.Count} 个）；子模型粒子按引擎语义不发射。");
            if (model.RibbonEmitters.Count > 0 && warnings != null)
                warnings.Add($"RIBB 飘带暂未导出（{model.RibbonEmitters.Count} 个）。");

            return created;
        }

        /// <summary>
        /// 给 GPU 粒子一个足够大的可见包围盒：Godot 用 visibility_aabb 做剔除，默认 ±4 单位太小，
        /// 粒子飞出后会被整批剔除（表现为“粒子不播放”）。按初速、重力与寿命估算运动范围。
        /// </summary>
        static Aabb ParticleVisibilityAabb(float speed, float variation, float gravity, float lifespan)
        {
            float reach = (MathF.Abs(speed) + MathF.Abs(variation)) * lifespan
                          + 0.5f * MathF.Abs(gravity) * lifespan * lifespan;
            float extent = MathF.Max(64f, reach + 32f);
            return new Aabb(new Vector3(-extent, -extent, -extent), new Vector3(extent * 2f, extent * 2f, extent * 2f));
        }
        /// <summary>
        /// 把模型的附着点（ATCH）导出为 <see cref="Marker3D"/>：每个附着点挂在它所属节点的
        /// <see cref="BoneAttachment3D"/> 下，位置为该节点的 pivot，因此会跟随烘焙动画。
        /// 节点名用 MDX 的附着点名（净化后），同时加入同名分组，方便脚本按 "overhead ref"
        /// 这类名字查找。
        /// </summary>
        static int BuildAttachmentMarkers(MdxModel model, Skeleton3D skeleton)
        {
            int created = 0;
            for (int i = 0; i < model.Attachments.Count; i++)
            {
                MdxAttachment attachment = model.Attachments[i];
                if (attachment?.Node == null) continue;

                string refName = MdxExportPaths.Sanitize(attachment.Path, $"attachment_{i}");
                var marker = new Marker3D
                {
                    Name = $"attach_{i}_{refName}",
                    Position = new Vector3(attachment.Node.Pivot.X, attachment.Node.Pivot.Y, attachment.Node.Pivot.Z),
                };
                // 原始附着点名（含空格，如 "overhead ref"）放进分组，脚本可用
                // GetTree().GetNodesInGroup("overhead ref") 精确查找。
                if (!string.IsNullOrWhiteSpace(attachment.Path)) marker.AddToGroup(attachment.Path);

                var boneAttachment = new BoneAttachment3D
                {
                    Name = $"attach_{i}_attach",
                    BoneIdx = attachment.Node.NodeId,
                };
                boneAttachment.AddChild(marker);
                skeleton.AddChild(boneAttachment);
                created++;
            }
            return created;
        }
        /// <summary>三段颜色/alpha → Godot 的粒子颜色渐变（中间停靠点 = 作者的 midtime）。</summary>
        static GradientTexture1D BuildParticleColorRamp(MdxParticleEmitter2 emitter)
        {
            float mid = emitter.Time <= 0f || emitter.Time >= 1f ? 0.5f : emitter.Time;
            mid = Math.Clamp(mid, 0.001f, 0.999f);
            var gradient = new Gradient
            {
                Offsets = new[] { 0f, mid, 1f },
                Colors = new[] { ParticleSegmentColor(emitter, 0), ParticleSegmentColor(emitter, 1), ParticleSegmentColor(emitter, 2) },
            };
            return new GradientTexture1D { Gradient = gradient };
        }

        static Color ParticleSegmentColor(MdxParticleEmitter2 emitter, int index)
        {
            float[] colors = emitter.SegmentColor ?? Array.Empty<float>();
            int offset = index * 3;
            float r = colors.Length > offset ? colors[offset] : 1f;
            float g = colors.Length > offset + 1 ? colors[offset + 1] : 1f;
            float b = colors.Length > offset + 2 ? colors[offset + 2] : 1f;
            byte[] alphas = emitter.Alpha;
            float a = alphas != null && alphas.Length > index ? alphas[index] / 255f : 1f;
            return new Color(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), Math.Clamp(a, 0f, 1f));
        }

        /// <summary>三段尺寸 → Godot 的粒子缩放曲线（用作者原始 ParticleScaling）。</summary>
        static CurveTexture BuildParticleScaleCurve(MdxParticleEmitter2 emitter, float baseSize)
        {
            float[] scaling = emitter.ParticleScaling ?? new[] { 1f, 1f, 1f };
            float mid = emitter.Time <= 0f || emitter.Time >= 1f ? 0.5f : Math.Clamp(emitter.Time, 0.001f, 0.999f);
            var curve = new Curve();
            curve.AddPoint(new Vector2(0f, scaling.Length > 0 ? scaling[0] : 1f));
            curve.AddPoint(new Vector2(mid, scaling.Length > 1 ? scaling[1] : 1f));
            curve.AddPoint(new Vector2(1f, scaling.Length > 2 ? scaling[2] : 1f));
            return new CurveTexture { Curve = curve };
        }

        // ---- textures ------------------------------------------------------------------------

        /// <summary>Collects the textures used by the exported materials and writes them as PNG.</summary>
        sealed class TextureJobSet
        {
            readonly Dictionary<string, TextureJob> _jobs = new Dictionary<string, TextureJob>(StringComparer.Ordinal);
            readonly List<TextureJob> _order = new List<TextureJob>();
            readonly Dictionary<string, string> _usedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public MdxAssetLibrary Assets { get; set; }

            public IReadOnlyList<TextureJob> Jobs => _order;

            /// <summary>Remembers one layer material and the texture it references.</summary>
            public void Register(StandardMaterial3D material, MdxModel model, int textureId)
            {
                if (material?.AlbedoTexture == null) return;
                MdxTexture texture = textureId >= 0 && textureId < model.Textures.Count ? model.Textures[textureId] : null;
                string virtualPath = texture?.Path ?? string.Empty;
                uint replaceableId = texture?.ReplaceableId ?? 0;

                string key = Key(virtualPath, replaceableId);
                if (!_jobs.TryGetValue(key, out TextureJob entry))
                {
                    string fileName = MdxExportPaths.UniqueFileName(
                        MdxExportPaths.TextureFileName(virtualPath, replaceableId), _usedNames, key);
                    entry = new TextureJob
                    {
                        Source = material.AlbedoTexture,
                        VirtualPath = virtualPath,
                        ReplaceableId = replaceableId,
                        FileName = fileName,
                    };
                    _jobs[key] = entry;
                    _order.Add(entry);
                }
                material.SetMeta(TextureMetaKey, key);
            }

            public TextureJob Find(string key) => key != null && _jobs.TryGetValue(key, out TextureJob job) ? job : null;

            static string Key(string virtualPath, uint replaceableId)
            {
                string normalized = (virtualPath ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
                return normalized.Length == 0 ? $"<replaceable:{replaceableId}>" : normalized;
            }
        }

        public const string TextureMetaKey = "mdx_export_texture";

        static List<TextureJob> SaveTextures(TextureJobSet job, string textureFolder, out string warning)
        {
            warning = string.Empty;
            var saved = new List<TextureJob>();
            if (job.Jobs.Count == 0) return saved;

            Error folderError = MdxExportPaths.MakeDirectory(textureFolder, out string globalFolder);
            if (folderError != Error.Ok)
                throw new IOException($"无法创建贴图目录 {textureFolder}（{folderError}）");

            foreach (TextureJob entry in job.Jobs)
            {
                Image image = entry.Source?.GetImage();
                if (image == null)
                {
                    warning = JoinWarning(warning, $"贴图 {Describe(entry)} 无法读取像素，已跳过。");
                    continue;
                }

                entry.ResourcePath = MdxExportPaths.CombineResource(textureFolder, entry.FileName);
                entry.SceneReference = $"{TextureSubFolder}/{entry.FileName}";
                entry.GlobalPath = Path.Combine(globalFolder, entry.FileName);
                Error error = image.SavePng(entry.GlobalPath);
                if (error != Error.Ok)
                {
                    warning = JoinWarning(warning, $"贴图 {Describe(entry)} 保存失败（{error}）。");
                    continue;
                }
                entry.Size = MdxExportPaths.FileSize(entry.GlobalPath);
                saved.Add(entry);
            }
            return saved;
        }

        static string Describe(TextureJob entry)
            => entry.ReplaceableId != 0
                ? $"<可替换贴图 {entry.ReplaceableId}>"
                : (string.IsNullOrEmpty(entry.VirtualPath) ? "(未知来源)" : entry.VirtualPath);

        /// <summary>
        /// Repoints the built materials at the freshly written PNGs, so the scene does
        /// not depend on the plugin runtime or on the MPQ archives any more.
        /// </summary>
        static void ApplyBakedTexturesToScene(ExportedScene exported, TextureJobSet job)
        {
            // One loaded texture instance is shared by every material that used the same source:
            // loading the same path repeatedly hands out instances that no longer carry the
            // resource path, which would silently embed the pixels in the scene instead of
            // referencing the PNG.
            var cache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

            foreach (GeosetExport geoset in exported.Geosets)
            {
                foreach (StandardMaterial3D material in geoset.Materials)
                {
                    if (material == null || !material.HasMeta(TextureMetaKey)) continue;
                    TextureJob entry = job.Find(material.GetMeta(TextureMetaKey).AsString());
                    if (entry == null || string.IsNullOrEmpty(entry.ResourcePath))
                    {
                        // The texture could not be written as a PNG. Dropping it keeps the scene
                        // free of inlined pixels (the materials stay valid, just untextured).
                        material.AlbedoTexture = null;
                        continue;
                    }

                    if (!cache.TryGetValue(entry.GlobalPath, out Texture2D baked))
                    {
                        baked = LoadBakedTexture(entry);
                        cache[entry.GlobalPath] = baked;
                    }
                    if (baked != null) material.AlbedoTexture = baked;
                }
            }
        }

        /// <summary>
        /// Loads one exported PNG as a <see cref="Texture2D"/> that references the file instead of
        /// embedding its pixels in the scene.
        ///
        /// <c>ResourceLoader.Load(path)</c> is deliberately avoided: in a <c>--headless</c> run the
        /// editor's texture importer is not available ("No loader found for resource"), which would
        /// silently leave the material pointing at the runtime texture and blow the .tscn up with
        /// inlined image data.
        /// </summary>
        static Texture2D LoadBakedTexture(TextureJob entry)
        {
            try
            {
                if (!File.Exists(entry.GlobalPath)) return null;
                var image = new Image();
                Error error = image.Load(entry.GlobalPath);
                if (error != Error.Ok)
                {
                    GD.PushWarning($"[MDX Preview] 导出贴图回读失败 {entry.ResourcePath}: {error}");
                    return null;
                }
                var texture = ImageTexture.CreateFromImage(image);
                texture.ResourcePath = entry.SceneReference;
                texture.SetMeta("mdx_source", entry.ResourcePath);
                return texture;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 导出贴图回读异常 {entry.ResourcePath}: {ex.Message}");
                return null;
            }
        }

        // ---- info file -----------------------------------------------------------------------

        static void WriteInfoFile(
            MdxModel model,
            MdxModelNode modelNode,
            string infoPath,
            string resourceFolder,
            string scenePath,
            string baseName,
            ExportedScene exported,
            List<TextureJob> textures,
            int animationCount,
            int fps)
        {
            var text = new StringBuilder();
            string globalInfo = ProjectSettings.GlobalizePath(infoPath);

            int vertices = 0, triangles = 0, layers = 0;
            foreach (MdxGeoset geoset in model.Geosets)
            {
                vertices += geoset.VertexCount;
                triangles += geoset.Indices.Length / 3;
                layers += model.GetMaterial(geoset.MaterialId)?.Layers.Count ?? 0;
            }

            int particles = model.ParticleEmitters1.Count + model.ParticleEmitters2.Count;

            text.AppendLine("MDX 导出信息");
            text.AppendLine("========================================");
            text.AppendLine($"模型名称        : {(string.IsNullOrWhiteSpace(model.Info.Name) ? baseName : model.Info.Name)}");
            text.AppendLine($"源模型虚拟路径  : {(string.IsNullOrEmpty(model.SourcePath) ? (string.IsNullOrEmpty(modelNode.SourcePath) ? "(未知)" : modelNode.SourcePath) : model.SourcePath)}");
            text.AppendLine($"MDX 版本        : {model.Version}");
            text.AppendLine($"动画文件 (MODL) : {(string.IsNullOrWhiteSpace(model.Info.AnimationFile) ? "(无)" : model.Info.AnimationFile)}");
            text.AppendLine($"导出时间        : {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            text.AppendLine($"输出目录        : {resourceFolder}");
            text.AppendLine($"场景文件        : {scenePath}");
            text.AppendLine($"采样帧率        : {fps} fps");
            text.AppendLine();
            text.AppendLine("---- 数量统计 ----");
            text.AppendLine($"动画序列        : {model.Sequences.Count}");
            text.AppendLine($"几何体 (Geoset) : {model.Geosets.Count}（顶点 {vertices}，三角面 {triangles}）");
            text.AppendLine($"材质            : {model.Materials.Count}（层 {layers}）");
            text.AppendLine($"贴图引用        : {model.Textures.Count}（已导出 PNG {textures.Count}）");
            text.AppendLine($"骨骼 (BONE)     : {model.Bones.Count}");
            text.AppendLine($"辅助节点        : {model.Helpers.Count}");
            text.AppendLine($"骨骼总数 (节点) : {model.NodeCount}");
            text.AppendLine($"粒子发射器      : {particles}（PRE2 {model.ParticleEmitters2.Count}，PRE1 {model.ParticleEmitters1.Count}）");
            text.AppendLine($"飘带发射器      : {model.RibbonEmitters.Count}");
            text.AppendLine($"事件对象        : {model.Events.Count}");
            text.AppendLine($"相机            : {model.Cameras.Count}");
            text.AppendLine($"光源            : {model.Lights.Count}");
            text.AppendLine($"附着点          : {model.Attachments.Count}");
            text.AppendLine($"碰撞体          : {model.CollisionShapes.Count}");
            text.AppendLine($"已烘焙动画      : {animationCount}");
            text.AppendLine();

            text.AppendLine("---- 序列清单 ----");
            if (model.Sequences.Count == 0)
            {
                text.AppendLine("(模型未定义任何序列)");
            }
            else
            {
                for (int i = 0; i < model.Sequences.Count; i++)
                {
                    MdxSequence sequence = model.Sequences[i];
                    float seconds = Math.Max(0, sequence.Duration) / 1000f;
                    text.AppendLine($"[{i}] {sequence.Name}  帧 {sequence.Start}..{sequence.End}" +
                                    $"  时长 {seconds.ToString("0.###", CultureInfo.InvariantCulture)} 秒" +
                                    $"  {(sequence.NonLooping ? "不循环" : "循环")}" +
                                    (sequence.MoveSpeed != 0f
                                        ? $"  移动速度 {sequence.MoveSpeed.ToString("0.###", CultureInfo.InvariantCulture)}"
                                        : string.Empty));
                }
            }
            text.AppendLine();

            text.AppendLine("---- 导出贴图 ----");
            if (textures.Count == 0)
            {
                text.AppendLine("(无贴图)");
            }
            else
            {
                foreach (TextureJob texture in textures)
                {
                    text.AppendLine($"{texture.FileName}  <=  {Describe(texture)}  （{texture.Size} 字节）");
                }
            }
            text.AppendLine();

            text.AppendLine("---- 已知限制 ----");
            text.AppendLine("1. 粒子发射器（PRE1/PRE2）与飘带（RIBB）未烘焙进场景，仅导出几何、骨骼动画与材质。");
            text.AppendLine("2. UV 动画（TXAN 平移/旋转/缩放）与翻页贴图（KMTF）取导出瞬间的静帧，未做成轨道动画。");
            text.AppendLine("3. 贴图替换使用导出时的队伍颜色/辉光纯色，之后修改队伍颜色不会影响已导出的 PNG。");
            text.AppendLine("4. 广告牌（Billboarded）节点按作者姿态烘焙，不再随摄像机朝向变化。");
            text.AppendLine("5. 事件对象（EVTS）、附着点、碰撞体、光源与相机只统计数量，未生成节点。");
            text.AppendLine("6. 透明度/颜色动画只烘焙了导出瞬间的静帧（写入材质 albedo）。");
            text.AppendLine();

            if (!string.IsNullOrEmpty(exported.Warning))
            {
                text.AppendLine("---- 导出警告 ----");
                text.AppendLine(exported.Warning);
            }

            File.WriteAllText(globalInfo, text.ToString(), new UTF8Encoding(false));
        }

        // ---- messaging -------------------------------------------------------------------------

        static string BuildMessage(
            MdxModel model,
            string baseName,
            string sceneFile,
            string resourceFolder,
            bool overwritten,
            ExportedScene exported,
            List<TextureJob> textures,
            int animationCount,
            int fps,
            string warning)
        {
            int vertices = 0, triangles = 0;
            foreach (MdxGeoset geoset in model.Geosets)
            {
                vertices += geoset.VertexCount;
                triangles += geoset.Indices.Length / 3;
            }

            var text = new StringBuilder();
            text.Append(overwritten ? "已覆盖原有场景，导出完成" : "导出完成");
            text.Append($"：{sceneFile}（{resourceFolder}）");
            text.Append($"\n模型：{(string.IsNullOrWhiteSpace(model.Info.Name) ? baseName : model.Info.Name)}");
            text.Append($"\n几何：{exported.Geosets.Count} 个 MeshInstance3D（{vertices} 顶点 / {triangles} 三角面）");
            text.Append($"\n骨骼：{model.NodeCount} 根（Skeleton3D，rest 全为 Identity）");
            text.Append($"\n动画：{animationCount} 个（{fps} fps，模型共 {model.Sequences.Count} 个序列）");
            text.Append($"\n贴图：{textures.Count} 张 PNG -> {MdxExportPaths.CombineResource(resourceFolder, TextureSubFolder)}/");
            text.Append($"\n附带：{baseName}.info.txt");
            if (!string.IsNullOrEmpty(warning)) text.Append($"\n注意：{warning}");
            return text.ToString();
        }

        static string JoinWarning(string current, string addition)
            => string.IsNullOrEmpty(current) ? addition : $"{current} {addition}";

        static string CombineWarnings(params string[] warnings)
        {
            var text = new StringBuilder();
            foreach (string warning in warnings)
            {
                if (string.IsNullOrWhiteSpace(warning)) continue;
                if (text.Length > 0) text.Append(' ');
                text.Append(warning);
            }
            return text.ToString();
        }

        // ---- naming ----------------------------------------------------------------------------

        /// <summary>Scene/info base name: the model name, else the source file name.</summary>
        public static string ResolveBaseName(MdxModel model, string sourcePath)
        {
            string name = model?.Info?.Name;
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(sourcePath))
            {
                string file = sourcePath.Replace('\\', '/');
                int slash = file.LastIndexOf('/');
                if (slash >= 0) file = file.Substring(slash + 1);
                name = Path.GetFileNameWithoutExtension(file);
            }
            return MdxExportPaths.Sanitize(name, "model");
        }
    }
}
