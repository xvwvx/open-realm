using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using MdxPreview.Core.Mdx;
using MdxPreview.Runtime;

namespace MdxPreview.EditorUi
{
    /// <summary>
    /// “信息”页：模型统计（版本/序列/几何体/顶点/材质层/骨骼/PRE2/RIBB/EVTS/CAMS）、
    /// 当前序列与时间、缺失资源列表（可点开看完整路径）以及事件日志。
    /// </summary>
    [Tool]
    public partial class MdxInfoPanel : VBoxContainer
    {
        const int MaxLogLines = 200;

        readonly List<string> _log = new List<string>();
        readonly List<string> _missing = new List<string>();

        Label _titleLabel;
        RichTextLabel _stats;
        Label _playheadLabel;
        ItemList _missingList;
        Label _missingDetail;
        RichTextLabel _logView;

        string _modelPath = string.Empty;

        public MdxInfoPanel()
        {
            Name = "InfoPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 4);
        }

        public override void _Ready()
        {
            _titleLabel = new Label
            {
                Text = "未加载模型",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TooltipText = "当前模型路径",
            };
            AddChild(_titleLabel);

            _stats = MakeReadOnlyText(140f);
            AddChild(_stats);

            _playheadLabel = new Label { Text = "序列: -　时间: -" };
            AddChild(_playheadLabel);

            var missingHeader = new Label { Text = "缺失资源（点击查看完整路径）" };
            AddChild(missingHeader);

            _missingList = new ItemList
            {
                CustomMinimumSize = new Vector2(0f, 72f),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                AllowReselect = true,
                TooltipText = "预览时未能找到的贴图/资源",
            };
            _missingList.ItemSelected += OnMissingSelected;
            AddChild(_missingList);

            _missingDetail = new Label
            {
                Text = "-",
                AutowrapMode = TextServer.AutowrapMode.Arbitrary,
                CustomMinimumSize = new Vector2(0f, 32f),
            };
            AddChild(_missingDetail);

            var logHeader = new Label { Text = "事件日志（EVTS / 加载信息）" };
            AddChild(logHeader);

            _logView = MakeReadOnlyText(110f);
            _logView.ScrollFollowing = true;
            AddChild(_logView);
        }

        static RichTextLabel MakeReadOnlyText(float minHeight) => new RichTextLabel
        {
            BbcodeEnabled = false,
            ScrollActive = true,
            FitContent = false,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(0f, minHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

        // ---- 模型信息 -------------------------------------------------------------------

        public void SetModel(MdxModelNode node, string path)
        {
            _modelPath = path ?? string.Empty;
            if (_titleLabel == null) return;

            if (node == null || node.Model == null)
            {
                _titleLabel.Text = "未加载模型";
                _titleLabel.TooltipText = string.Empty;
                _stats.Text = BuildConfigurationHint();
                _playheadLabel.Text = "序列: -　时间: -";
                return;
            }

            MdxModel model = node.Model;
            _titleLabel.Text = $"{(string.IsNullOrWhiteSpace(model.Info.Name) ? "(未命名)" : model.Info.Name)}　路径: {MdxEditorUtil.Shorten(_modelPath, 64)}";
            _titleLabel.TooltipText = _modelPath;

            int vertices = 0;
            foreach (MdxGeoset geoset in model.Geosets)
                vertices += geoset.VertexCount > 0 ? geoset.VertexCount : geoset.Vertices.Length / 3;

            int materialLayers = 0;
            foreach (MdxMaterial material in model.Materials)
                materialLayers += material.Layers.Count;

            var builder = new StringBuilder();
            builder.AppendLine($"模型名　: {(string.IsNullOrWhiteSpace(model.Info.Name) ? "(未命名)" : model.Info.Name)}");
            builder.AppendLine($"版本　　: {model.Version}");
            builder.AppendLine($"序列数　: {model.Sequences.Count}");
            builder.AppendLine($"几何体数: {model.Geosets.Count}　顶点数: {vertices}");
            builder.AppendLine($"材质/层 : {model.Materials.Count} / {materialLayers}　贴图: {model.Textures.Count}");
            builder.AppendLine($"骨骼/节点: {model.Bones.Count} / {model.NodeCount}");
            builder.AppendLine($"PRE2/PRE1: {model.ParticleEmitters2.Count} / {model.ParticleEmitters1.Count}");
            builder.AppendLine($"RIBB　　: {model.RibbonEmitters.Count}　ATCH: {model.Attachments.Count}");
            builder.AppendLine($"EVTS　　: {model.Events.Count}　CAMS: {model.Cameras.Count}");
            builder.AppendLine($"LITE/CLID: {model.Lights.Count} / {model.CollisionShapes.Count}");
            _stats.Text = builder.ToString();
        }

        string BuildConfigurationHint()
            => "尚未加载模型。\n\n"
               + "1. 打开「设置」页，填写 Warcraft III 游戏路径并「扫描 MPQ」，然后「应用并重载」。\n"
               + "2. 也可以直接拖一个 .mdx/.mdl 文件到预览区，或填写 res:// 与绝对路径后点「打开」。";

        public void SetPlayhead(float frame, float start, float duration)
        {
            if (_playheadLabel == null) return;
            _playheadLabel.Text = duration > 0f
                ? $"序列: {Mathf.Max(0f, frame - start):0} / {duration:0} ms"
                : "序列: -（该模型没有动画序列）";
        }

        // ---- 缺失资源 -------------------------------------------------------------------

        public void RefreshMissing(IReadOnlyCollection<string> missing)
        {
            if (_missingList == null) return;

            _missing.Clear();
            if (missing != null)
            {
                foreach (string path in missing) _missing.Add(path);
                _missing.Sort(StringComparer.OrdinalIgnoreCase);
            }

            _missingList.Clear();
            foreach (string path in _missing) _missingList.AddItem(MdxEditorUtil.Shorten(path, 56));
            _missingDetail.Text = _missing.Count == 0 ? "-" : $"共 {_missing.Count} 项缺失";
        }

        void OnMissingSelected(long index)
        {
            if (index < 0 || index >= _missing.Count) return;
            _missingDetail.Text = _missing[(int)index];
            _missingDetail.TooltipText = _missing[(int)index];
        }

        // ---- 日志 -----------------------------------------------------------------------

        public void AppendLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _log.Add(stamped);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            if (_logView != null) _logView.Text = string.Join("\n", _log);
        }

        public void ClearLog()
        {
            _log.Clear();
            if (_logView != null) _logView.Text = string.Empty;
        }
    }
}
