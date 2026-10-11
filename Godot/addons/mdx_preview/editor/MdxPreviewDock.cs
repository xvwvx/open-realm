using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Core.Mdx;
using MdxPreview.Export;
using MdxPreview.Runtime;

namespace MdxPreview.EditorUi
{
    /// <summary>
    /// MDX 预览面板（编辑器左侧 Dock 的根节点，对应 editor/MdxPreviewDock.tscn）。
    ///
    /// 布局：
    ///   工具栏        —— 打开模型 / 重新加载 / 适配视图
    ///   3D 预览区      —— SubViewportContainer + 独立 World3D 的 SubViewport + MdxPreviewView
    ///   动画控制条     —— 序列下拉、播放/暂停、循环、速度、时间轴（scrubbing）
    ///   分页(TabContainer)：模型 / 显示 / 信息 / 设置
    ///     · 模型：路径输入、旋转滑条、自动旋转、最近打开（支持拖拽 .mdx）
    ///     · 显示：粒子/飘带/灯光/附着点/碰撞体/地面、队伍颜色
    ///     · 信息：模型统计、缺失资源、事件日志
    ///     · 设置：游戏路径 / MPQ / BLP 搜索路径 / 导出
    ///
    /// 本类不创建 <see cref="MdxAssetLibrary"/>，实例由插件持有后通过
    /// <see cref="Initialize"/> 注入，保证 Dock 被重建时资源库仍然复用。
    /// </summary>
    [Tool]
    public partial class MdxPreviewDock : VBoxContainer
    {
        // These are normally injected by MdxPreviewPlugin before the control enters
        // the dock, but Godot can also rebuild the script instance itself (assembly
        // reload), in which case the injected references are gone. Non-null defaults
        // keep every toolbar/dialog handler safe in that situation.
        MdxAssetLibrary _assets = new MdxAssetLibrary();
        MdxPreviewSettings _settings = MdxPreviewSettings.Load();

        MdxPreviewViewport _viewport;
        MdxPreviewView _view;

        // 动画
        OptionButton _sequenceOption;
        Button _playButton;
        CheckBox _loopCheck;
        HSlider _speedSlider;
        Label _speedLabel;
        HSlider _timelineSlider;
        Label _timeLabel;

        // 模型页
        LineEdit _modelPathEdit;
        ItemList _recentList;
        HSlider _rotX;
        HSlider _rotY;
        HSlider _rotZ;
        Label _rotXValue;
        Label _rotYValue;
        Label _rotZValue;
        CheckBox _autoRotateCheck;
        HSlider _autoRotateSpeedSlider;
        Label _autoRotateSpeedLabel;

        // 显示页
        CheckBox _showParticles;
        HSlider _particleBrightness;
        HSlider _particleDensity;
        CheckBox _showRibbons;
        CheckBox _showLights;
        CheckBox _showAttachments;
        CheckBox _showCollision;
        CheckBox _showGround;
        CheckBox _replaceTeamTextures;
        CheckBox _hideShellCheck;
        CheckBox _plainLight;
        OptionButton _cullOption;
        CheckBox _viewerOrder;
        CheckBox _useModelCamera;
        OptionButton _teamColorOption;

        // 信息 / 设置
        MdxInfoPanel _infoPanel;
        MdxSettingsPanel _settingsPanel;

        AcceptDialog _dialog;
        FileDialog _modelDialog;

        /// <summary>程序化改控件时置位，避免把 UI 同步当成用户操作。</summary>
        bool _uiGuard;

        TabContainer _tabs;

        /// <summary>时间轴拖拽期间暂缓从模型回写滑条（避免抖动）。</summary>
        double _scrubHoldUntilMs;

        string _currentModelPath = string.Empty;

        public MdxPreviewDock()
        {
            Name = "MdxPreviewDock";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(264f, 320f);
        }

        /// <summary>由插件在加入 Dock 之前调用。</summary>
        public void Initialize(MdxAssetLibrary assets, MdxPreviewSettings settings)
        {
            _assets = assets;
            _settings = settings;
        }

        public override void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            SetProcess(true);

            if (_assets == null || _settings == null)
            {
                GD.PushError("[MDX Preview] Dock 未初始化（assets/settings 为空），面板功能不可用。");
                AddChild(new Label { Text = "MDX 预览：初始化失败（assets/settings 为空）。" });
                return;
            }

            BuildUi();
            ApplySettingsToUi();
            HookEvents();
            RefreshAfterLoad();
            _infoPanel.AppendLog("MDX 预览面板已就绪。");
            GD.Print($"[MDX Preview] 面板构建完成：视口={(_viewport?.SubViewport3D != null ? "SubViewport+独立 World3D" : "缺失")}，"
                     + $"预览节点={(_view != null)}，分页={_tabs?.GetTabCount() ?? 0} 个。");

            // 配置与上次模型放到编辑器启动之后再恢复，避免拖慢编辑器启动。
            Callable.From(RestoreLastSession).CallDeferred();
        }

        // ---- 界面构建 -------------------------------------------------------------------

        void BuildUi()
        {
            AddThemeConstantOverride("separation", 4);

            // 工具栏
            var toolbar = new HBoxContainer();
            AddChild(toolbar);
            AddButton(toolbar, "打开模型…", () => Safe("打开模型", BrowseForModel), "从本机选择 .mdx / .mdl（默认从游戏目录开始）");
            AddButton(toolbar, "重新加载", () => Safe("重新加载", ReloadCurrentModel), "重新载入当前模型（贴图/材质全部重建）");
            AddButton(toolbar, "适配视图", () => Safe("适配视图", () => _view?.RequestFrame()), "把相机重新对准模型");

            // 左右结构：左侧 3D 预览，右侧动画控制 + 分页（可由分隔条拖拽调整宽度）。
            var split = new HSplitContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SplitOffset = 560,
            };
            AddChild(split);

            var rightColumn = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(320f, 0f),
            };
            rightColumn.AddThemeConstantOverride("separation", 4);

            // 3D 预览（SubViewport + 独立 World3D）
            // 注意顺序：先把 MdxPreviewView 交给容器，再把容器挂进场景树，
            // 这样容器 _Ready 时就能把预览挂到自己的 SubViewport 下。
            _viewport = new MdxPreviewViewport();
            _view = _viewport.CreateView(_assets, _settings);
            _viewport.DropHandler = HandleDroppedPaths;
            split.AddChild(_viewport);
            split.AddChild(rightColumn);

            // 动画控制条
            var animation = new VBoxContainer();
            animation.AddThemeConstantOverride("separation", 2);
            rightColumn.AddChild(animation);

            var sequenceRow = new HBoxContainer();
            animation.AddChild(sequenceRow);
            sequenceRow.AddChild(new Label { Text = "序列" });
            _sequenceOption = new OptionButton
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "模型自带的动画序列（名称 / 帧区间 / 时长）",
            };
            _sequenceOption.ItemSelected += OnSequenceSelected;
            sequenceRow.AddChild(_sequenceOption);

            _playButton = new Button { Text = "暂停", TooltipText = "播放 / 暂停当前序列" };
            _playButton.Pressed += OnPlayPressed;
            sequenceRow.AddChild(_playButton);

            _loopCheck = new CheckBox { Text = "循环" };
            _loopCheck.Toggled += OnLoopToggled;
            sequenceRow.AddChild(_loopCheck);

            var speedRow = new HBoxContainer();
            animation.AddChild(speedRow);
            speedRow.AddChild(new Label { Text = "速度" });
            _speedSlider = new HSlider
            {
                MinValue = 0.1,
                MaxValue = 4.0,
                Step = 0.05,
                Value = 1.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "播放速度 0.1x – 4x",
            };
            _speedSlider.ValueChanged += OnSpeedChanged;
            speedRow.AddChild(_speedSlider);
            _speedLabel = new Label { Text = "1.00x", CustomMinimumSize = new Vector2(46f, 0f) };
            speedRow.AddChild(_speedLabel);

            var timelineRow = new HBoxContainer();
            animation.AddChild(timelineRow);
            timelineRow.AddChild(new Label { Text = "时间轴" });
            _timelineSlider = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.001,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "拖动可手动逐帧查看（scrubbing）",
            };
            _timelineSlider.ValueChanged += OnTimelineChanged;
            timelineRow.AddChild(_timelineSlider);
            _timeLabel = new Label { Text = "-", CustomMinimumSize = new Vector2(96f, 0f) };
            timelineRow.AddChild(_timeLabel);

            // 分页（与动画控制条一起放在右侧栏）
            var tabs = new TabContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            rightColumn.AddChild(tabs);
            _tabs = tabs;

            tabs.AddChild(BuildModelTab());
            tabs.AddChild(BuildDisplayTab());

            _infoPanel = new MdxInfoPanel { Name = "信息" };
            tabs.AddChild(_infoPanel);

            _settingsPanel = new MdxSettingsPanel { Name = "设置" };
            _settingsPanel.Initialize(_settings, _assets);
            _settingsPanel.DropHandler = HandleDroppedPaths;
            _settingsPanel.SettingsApplied += OnSettingsApplied;
            _settingsPanel.ErrorReported += message => ShowMessage("MDX 预览", message);
            _settingsPanel.ExportRequested += OnExportRequested;
            tabs.AddChild(_settingsPanel);

            var dialog = new AcceptDialog { Title = "MDX 预览" };
            AddChild(dialog);
            _dialog = dialog;
        }

        Control BuildModelTab()
        {
            var tab = new VBoxContainer { Name = "模型" };
            tab.AddThemeConstantOverride("separation", 4);

            tab.AddChild(new Label { Text = "模型路径（虚拟路径 / res:// / 绝对路径）" });

            var pathRow = new HBoxContainer();
            tab.AddChild(pathRow);
            _modelPathEdit = new LineEdit
            {
                PlaceholderText = @"units\orc\Peon\Peon.mdx、res://... 或 D:\...\Peon.mdx",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "支持 MPQ 虚拟路径、res:// 工程路径与本机绝对路径；也可直接把 .mdx 拖进面板",
            };
            _modelPathEdit.TextSubmitted += text => OpenModel(text, false);
            pathRow.AddChild(_modelPathEdit);

            var browse = new Button { Text = "浏览…" };
            browse.Pressed += BrowseForModel;
            pathRow.AddChild(browse);

            var open = new Button { Text = "打开" };
            open.Pressed += () => OpenModel(_modelPathEdit.Text, false);
            pathRow.AddChild(open);

            var actionRow = new HBoxContainer();
            tab.AddChild(actionRow);
            AddButton(actionRow, "重置视角", () => _view?.ResetCamera(), "相机回到默认角度并重新适配模型");
            AddButton(actionRow, "重置旋转", () => SetModelRotation(Vector3.Zero), "模型自身旋转归零");
            AddButton(actionRow, "关闭模型", CloseModel);

            tab.AddChild(new HSeparator());
            tab.AddChild(new Label { Text = "模型自身旋转（度）" });

            _rotX = AddRotationRow(tab, "X", out _rotXValue);
            _rotY = AddRotationRow(tab, "Y", out _rotYValue);
            _rotZ = AddRotationRow(tab, "Z", out _rotZValue);

            var autoRow = new HBoxContainer();
            tab.AddChild(autoRow);
            _autoRotateCheck = new CheckBox { Text = "自动旋转" };
            _autoRotateCheck.Toggled += OnAutoRotateToggled;
            autoRow.AddChild(_autoRotateCheck);

            _autoRotateSpeedSlider = new HSlider
            {
                MinValue = 0.0,
                MaxValue = 180.0,
                Step = 1.0,
                Value = 25.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "自动旋转速度（度/秒）",
            };
            _autoRotateSpeedSlider.ValueChanged += OnAutoRotateSpeedChanged;
            autoRow.AddChild(_autoRotateSpeedSlider);
            _autoRotateSpeedLabel = new Label { Text = "25°/s", CustomMinimumSize = new Vector2(52f, 0f) };
            autoRow.AddChild(_autoRotateSpeedLabel);

            tab.AddChild(new HSeparator());
            tab.AddChild(new Label { Text = "最近打开（双击/单击打开）" });
            _recentList = new ItemList
            {
                CustomMinimumSize = new Vector2(0f, 80f),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                AllowReselect = true,
            };
            _recentList.ItemSelected += OnRecentSelected;
            tab.AddChild(_recentList);

            var recentRow = new HBoxContainer();
            tab.AddChild(recentRow);
            AddButton(recentRow, "清空最近", () =>
            {
                _settings.RecentModels.Clear();
                _settings.LastModel = string.Empty;
                SaveSettings();
                RefreshRecentList();
            });

            return tab;
        }

        /// <summary>显示页用的带数值标签的滑条行（亮度/密度等）。</summary>
        HSlider AddValueRow(Control parent, string label, float min, float max, float step, float value,
            string unit, Action<float> apply)
        {
            var row = new HBoxContainer();
            parent.AddChild(row);
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(64f, 0f) });

            var slider = new HSlider
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = value,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.AddChild(slider);

            var valueLabel = new Label { Text = $"{value:0.00}{unit}", CustomMinimumSize = new Vector2(52f, 0f) };
            row.AddChild(valueLabel);

            slider.ValueChanged += changed =>
            {
                valueLabel.Text = $"{changed:0.00}{unit}";
                apply((float)changed);
            };
            return slider;
        }

        HSlider AddRotationRow(Control parent, string axis, out Label valueLabel)
        {
            var row = new HBoxContainer();
            parent.AddChild(row);
            row.AddChild(new Label { Text = axis, CustomMinimumSize = new Vector2(16f, 0f) });

            var slider = new HSlider
            {
                MinValue = -180.0,
                MaxValue = 180.0,
                Step = 1.0,
                Value = 0.0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = $"绕 {axis} 轴旋转模型",
            };
            slider.ValueChanged += _ => OnRotationSliderChanged();
            row.AddChild(slider);

            var label = new Label { Text = "0°", CustomMinimumSize = new Vector2(42f, 0f) };
            row.AddChild(label);
            valueLabel = label;
            return slider;
        }

        Control BuildDisplayTab()
        {
            var tab = new VBoxContainer { Name = "显示" };
            tab.AddThemeConstantOverride("separation", 3);

            _showParticles = AddToggle(tab, "显示粒子 (PRE2/PRE1)", on => _settings.ShowParticles = on);

            // 加性粒子叠加后容易过亮，峰值还取决于贴图与混合细节：给出可调亮度/密度，
            // 便于把预览的"涌起"峰值调到与游戏/Retera 一致。
            _particleBrightness = AddValueRow(tab, "粒子亮度", 0.05f, 3f, 0.05f, _settings.ParticleBrightness, "×",
                on => { _settings.ParticleBrightness = on; _view?.Current?.ParticleBrightness = on; });
            _particleDensity = AddValueRow(tab, "粒子密度", 0.05f, 3f, 0.05f, _settings.ParticleDensity, "×",
                on => { _settings.ParticleDensity = on; _view?.Current?.ParticleDensity = on; });
            _showRibbons = AddToggle(tab, "显示飘带 (RIBB)", on => _settings.ShowRibbons = on);
            _showLights = AddToggle(tab, "显示灯光 (LITE)", on => _settings.ShowLights = on);
            _showAttachments = AddToggle(tab, "显示附着点 (ATCH)", on => _settings.ShowAttachments = on);
            _showCollision = AddToggle(tab, "显示碰撞体 (CLID)", on => _settings.ShowCollision = on);
            _showGround = AddToggle(tab, "显示地面网格", on => _settings.ShowGround = on);
            // Exposes the exact preview setup used by the mdxtool -o comparison (flat
            // ambient, black background, no ground) so a model can be inspected with the
            // same lighting the C reference renderer uses.
            _plainLight = AddToggle(tab, "预览光照：扁平参考（黑底）", on => { _settings.ShowGround = !on; _view?.SetPlainBackground(); });
            _hideShellCheck = AddToggle(tab, "隐藏队伍色外壳几何 (露出内层纹理)", on => { _settings.HideReplaceableShellGeosets = on; _view?.Current?.SetHideReplaceableShellGeosets(on); });
            _replaceTeamTextures = AddToggle(tab, "队伍色替换 (TeamColor/TeamGlow)",
                on => { _settings.ReplaceTeamTextures = on; _assets.SubstituteReplaceablePaths = on; _assets.InvalidateReplaceableTextures(); _view?.Current?.RebuildMaterials(); });
            _viewerOrder = AddToggle(tab, "材质优先级绘制 (查看器风格)", on => { _settings.ViewerStyleMaterialOrder = on; _view?.Current?.SetViewerStyleMaterialOrder(on); });
            _viewerOrder.TooltipText =
                "按 MDX 材质 priority 排序绘制，并让高层级材质压过下层（Retera Model Studio 那种观感）；\n" +
                "默认关闭，因为高层级材质会绕过深度测试。";
            _replaceTeamTextures.TooltipText =
                "开（默认，引擎语义）：ReplaceableTextures\\TeamColor|TeamGlow 路径由玩家颜色生成；\n" +
                "关（查看器风格）：同路径按原文件显示遮罩像素，便于与 Retera Model Studio 等工具对照。";

            // Warcraft assets were authored for Direct3D (clockwise front faces) while
            // Godot follows OpenGL winding, so a model can end up showing its interior
            // when viewed from the front. Exposed as a manual switch per asset.
            var cullRow = new HBoxContainer();
            tab.AddChild(cullRow);
            cullRow.AddChild(new Label { Text = "背面剔除" });
            _cullOption = new OptionButton
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "按文件 = 遵循 MDX 层 flags；若正面看起来被剔除（看到内部/背面），请试「剔除正面」或「双面」",
            };
            _cullOption.AddItem("按文件 (默认)");
            _cullOption.AddItem("剔除背面");
            _cullOption.AddItem("剔除正面");
            _cullOption.AddItem("双面");
            _cullOption.ItemSelected += index =>
            {
                MdxPreview.Runtime.MdxModelNode.CullOverride = (int)index;
                _view?.Current?.RebuildMaterials();
            };
            cullRow.AddChild(_cullOption);

            tab.AddChild(new HSeparator());
            var colorRow = new HBoxContainer();
            tab.AddChild(colorRow);
            colorRow.AddChild(new Label { Text = "队伍颜色" });
            _teamColorOption = new OptionButton
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "替换 TeamColor / TeamGlow 贴图后重建材质",
            };
            _teamColorOption.ItemSelected += OnTeamColorSelected;
            colorRow.AddChild(_teamColorOption);
            foreach (MdxTeamColor color in MdxTeamColor.All)
                _teamColorOption.AddItem(color.Name);

            tab.AddChild(new Label
            {
                Text = "提示：切换队伍颜色会重建材质；附着点/碰撞体需要勾选后才会出现在预览里。",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            });

            tab.AddChild(new HSeparator());
            var cameraRow = new HBoxContainer();
            tab.AddChild(cameraRow);
            _useModelCamera = new CheckBox
            {
                Text = "使用模型自带相机 (CAMS)",
                TooltipText = "勾选后相机由模型里的 CAMS 记录驱动",
            };
            _useModelCamera.Toggled += on =>
            {
                if (_uiGuard) return;
                _settings.UseModelCamera = on;
                if (_view != null)
                {
                    _view.UseModelCamera = on;
                    if (!on) _view.ResetCamera();
                }
                SaveSettings();
            };
            cameraRow.AddChild(_useModelCamera);

            return tab;
        }

        CheckBox AddToggle(Control parent, string text, Action<bool> apply)
        {
            var check = new CheckBox { Text = text };
            check.Toggled += on =>
            {
                if (_uiGuard) return;
                apply(on);
                _view?.RefreshFromSettings();
                SaveSettings();
            };
            parent.AddChild(check);
            return check;
        }

        static Button AddButton(Control parent, string text, Action onPressed, string tooltip = null)
        {
            var button = new Button { Text = text };
            if (!string.IsNullOrEmpty(tooltip)) button.TooltipText = tooltip;
            button.Pressed += onPressed;
            parent.AddChild(button);
            return button;
        }

        void HookEvents()
        {
            if (_view == null) return;
            _view.EventLogged += message => _infoPanel?.AppendLog(message);
            _view.ModelLoaded += path => _infoPanel?.AppendLog($"模型已载入: {path}");
            _view.LoadFailed += message => _infoPanel?.AppendLog("载入失败: " + message);
        }

        // ---- 设置 <-> 界面同步 -----------------------------------------------------------

        void ApplySettingsToUi()
        {
            _uiGuard = true;

            _showParticles.ButtonPressed = _settings.ShowParticles;
            if (_particleBrightness != null) _particleBrightness.Value = _settings.ParticleBrightness;
            if (_particleDensity != null) _particleDensity.Value = _settings.ParticleDensity;
            _showRibbons.ButtonPressed = _settings.ShowRibbons;
            _showLights.ButtonPressed = _settings.ShowLights;
            _showAttachments.ButtonPressed = _settings.ShowAttachments;
            _showCollision.ButtonPressed = _settings.ShowCollision;
            _showGround.ButtonPressed = _settings.ShowGround;
            if (_replaceTeamTextures != null) _replaceTeamTextures.ButtonPressed = _settings.ReplaceTeamTextures;
            if (_viewerOrder != null) _viewerOrder.ButtonPressed = _settings.ViewerStyleMaterialOrder;
            _useModelCamera.ButtonPressed = _settings.UseModelCamera;

            _autoRotateCheck.ButtonPressed = _settings.AutoRotate;
            _autoRotateSpeedSlider.Value = Mathf.Clamp(_settings.AutoRotateSpeed, 0f, 180f);
            _autoRotateSpeedLabel.Text = $"{_settings.AutoRotateSpeed:0}°/s";

            int teamIndex = Mathf.Clamp(_settings.TeamColorIndex, 0, MdxTeamColor.All.Length - 1);
            _teamColorOption.Select(teamIndex);

            _uiGuard = false;

            if (_view != null)
            {
                _view.AutoRotate = _settings.AutoRotate;
                _view.AutoRotateSpeed = _settings.AutoRotateSpeed;
            }
        }

        void SyncPlaybackUi()
        {
            MdxModelNode node = _view?.Current;
            _uiGuard = true;

            if (node == null || node.Model == null || node.Model.Sequences.Count == 0)
            {
                _playButton.Disabled = true;
                _loopCheck.Disabled = true;
                _timelineSlider.Editable = false;
                _speedSlider.Editable = false;
                _sequenceOption.Disabled = true;
                _timeLabel.Text = "-";
                _uiGuard = false;
                return;
            }

            _playButton.Disabled = false;
            _loopCheck.Disabled = false;
            _timelineSlider.Editable = true;
            _speedSlider.Editable = true;
            _sequenceOption.Disabled = false;

            _playButton.Text = node.IsPlaying ? "暂停" : "播放";
            _loopCheck.ButtonPressed = node.GetLoop();
            _speedSlider.Value = Mathf.Clamp(node.GetSpeed(), 0.1f, 4f);
            _speedLabel.Text = $"{node.GetSpeed():0.00}x";
            _timelineSlider.Value = node.SequenceRatio;

            _uiGuard = false;
        }

        void SyncRotationUi()
        {
            if (_view == null) return;
            Vector3 rotation = _view.ModelRotationDegrees;
            _uiGuard = true;
            _rotX.Value = Mathf.Clamp(rotation.X, -180f, 180f);
            _rotY.Value = Mathf.Clamp(rotation.Y, -180f, 180f);
            _rotZ.Value = Mathf.Clamp(rotation.Z, -180f, 180f);
            _uiGuard = false;
            _rotXValue.Text = $"{rotation.X:0}°";
            _rotYValue.Text = $"{rotation.Y:0}°";
            _rotZValue.Text = $"{rotation.Z:0}°";
        }

        void RefreshSequenceList()
        {
            MdxModelNode node = _view?.Current;
            _uiGuard = true;
            _sequenceOption.Clear();

            if (node == null || node.Model == null || node.Model.Sequences.Count == 0)
            {
                _sequenceOption.AddItem("（无动画序列）");
                _sequenceOption.Select(0);
                _uiGuard = false;
                SyncPlaybackUi();
                return;
            }

            for (int i = 0; i < node.Model.Sequences.Count; i++)
            {
                MdxSequence sequence = node.Model.Sequences[i];
                string name = string.IsNullOrWhiteSpace(sequence.Name) ? "(未命名)" : sequence.Name;
                _sequenceOption.AddItem($"{i}: {name} [{sequence.Start}..{sequence.End}] {sequence.Duration / 1000f:0.00}s");
                _sequenceOption.SetItemTooltip(i,
                    $"{name}\n帧区间: {sequence.Start} .. {sequence.End}（{sequence.Duration} ms）\n" +
                    $"循环: {(sequence.NonLooping ? "否（播放到结尾后停止）" : "是")}");
            }

            int current = node.Instance?.SequenceIndex ?? 0;
            _sequenceOption.Select(Mathf.Clamp(current, 0, node.Model.Sequences.Count - 1));
            _uiGuard = false;
            SyncPlaybackUi();
        }

        void RefreshRecentList()
        {
            if (_recentList == null) return;
            _recentList.Clear();
            foreach (string path in _settings.RecentModels) _recentList.AddItem(MdxEditorUtil.Shorten(path, 52));
            for (int i = 0; i < _settings.RecentModels.Count && i < _recentList.ItemCount; i++)
                _recentList.SetItemTooltip(i, _settings.RecentModels[i]);
        }

        /// <summary>载入完成（或清空）后刷新所有依赖模型的界面。</summary>
        void RefreshAfterLoad()
        {
            if (_modelPathEdit != null && _modelPathEdit.Text != _currentModelPath)
                _modelPathEdit.Text = _currentModelPath;

            _infoPanel?.SetModel(_view?.Current, _currentModelPath);
            _infoPanel?.RefreshMissing(ProblemAssets());
            RefreshSequenceList();
            SyncRotationUi();
            RefreshRecentList();
        }

        // ---- 动画交互 -------------------------------------------------------------------

        void OnSequenceSelected(long index)
        {
            if (_uiGuard) return;
            MdxModelNode node = _view?.Current;
            if (node?.Model == null || node.Model.Sequences.Count == 0) return;

            node.SetSequence((int)index);
            node.SetPlaying(true);
            _uiGuard = true;
            _loopCheck.ButtonPressed = node.GetLoop();
            _playButton.Text = "暂停";
            _uiGuard = false;
            _view.RequestFrame();
            _infoPanel?.AppendLog($"切换到序列 {index}: {node.Model.Sequences[(int)index].Name}");
        }

        void OnPlayPressed()
        {
            MdxModelNode node = _view?.Current;
            if (node == null) return;

            bool playing = !node.IsPlaying;
            if (playing && !node.GetLoop() && node.SequenceRatio >= 0.999f) node.SequenceRatio = 0f;
            node.SetPlaying(playing);
            _playButton.Text = playing ? "暂停" : "播放";
        }

        void OnLoopToggled(bool on)
        {
            if (_uiGuard) return;
            _view?.Current?.SetLoop(on);
        }

        void OnSpeedChanged(double value)
        {
            if (_uiGuard) return;
            _view?.Current?.SetSpeed((float)value);
            _speedLabel.Text = $"{value:0.00}x";
        }

        void OnTimelineChanged(double value)
        {
            if (_uiGuard) return;
            MdxModelNode node = _view?.Current;
            if (node == null) return;
            node.SequenceRatio = (float)value;
            // 拖拽期间不让 _Process 回写滑条，避免来回抖动。
            _scrubHoldUntilMs = Time.GetTicksMsec() + 500.0;
        }

        void OnRotationSliderChanged()
        {
            if (_uiGuard) return;
            SetModelRotation(new Vector3((float)_rotX.Value, (float)_rotY.Value, (float)_rotZ.Value));
        }

        void SetModelRotation(Vector3 degrees)
        {
            if (_view != null) _view.ModelRotationDegrees = degrees;
            _rotXValue.Text = $"{degrees.X:0}°";
            _rotYValue.Text = $"{degrees.Y:0}°";
            _rotZValue.Text = $"{degrees.Z:0}°";
        }

        void OnAutoRotateToggled(bool on)
        {
            if (_uiGuard) return;
            _settings.AutoRotate = on;
            if (_view != null) _view.AutoRotate = on;
            SaveSettings();
        }

        void OnAutoRotateSpeedChanged(double value)
        {
            if (_uiGuard) return;
            _settings.AutoRotateSpeed = (float)value;
            if (_view != null) _view.AutoRotateSpeed = (float)value;
            _autoRotateSpeedLabel.Text = $"{value:0}°/s";
            SaveSettings();
        }

        void OnTeamColorSelected(long index)
        {
            if (_uiGuard) return;
            int clamped = Mathf.Clamp((int)index, 0, MdxTeamColor.All.Length - 1);
            _settings.TeamColorIndex = clamped;
            if (_assets != null)
            {
                _assets.TeamColor = MdxTeamColor.All[clamped];
                _assets.InvalidateReplaceableTextures();
            }
            _view?.Current?.RebuildMaterials();
            SaveSettings();
        }

        // ---- 模型打开 / 关闭 --------------------------------------------------------------

        void BrowseForModel()
        {
            if (_modelDialog == null)
            {
                _modelDialog = new FileDialog
                {
                    Title = "选择 MDX / MDL 模型",
                    FileMode = FileDialog.FileModeEnum.OpenFile,
                    Access = FileDialog.AccessEnum.Filesystem,
                    UseNativeDialog = true,
                    Filters = new[] { MdxEditorUtil.ModelFilter, "*.* ; 所有文件" },
                    Size = new Vector2I(820, 560),
                };
                _modelDialog.FileSelected += path => OpenModel(path, false);
                AddChild(_modelDialog);
            }

            string start = MdxEditorUtil.DirectoryOf(_currentModelPath);
            if (string.IsNullOrWhiteSpace(start) && _settings?.MpqPaths is { Count: > 0 })
                start = MdxEditorUtil.DirectoryOf(_settings.MpqPaths[0]);
            if (!string.IsNullOrWhiteSpace(start) && System.IO.Directory.Exists(start))
                _modelDialog.CurrentDir = start;

            _modelDialog.PopupCentered(new Vector2I(820, 560));
        }

        /// <summary>
        /// <summary>
        /// Assets worth reporting in the info panel: files that were not found plus
        /// files that exist but failed to decode (for example an unsupported JPEG
        /// variant inside a custom BLP).
        /// </summary>
        List<string> ProblemAssets()
        {
            var list = new List<string>();
            if (_assets == null) return list;
            foreach (string path in _assets.MissingAssets) list.Add($"缺失  {path}");
            foreach (KeyValuePair<string, string> failure in _assets.FailedAssets)
                list.Add($"解码失败  {failure.Key} — {failure.Value}");
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// Runs a toolbar action and turns any unexpected exception into a visible
        /// message instead of an unhandled error spamming the editor console.
        /// </summary>
        void Safe(string action, Action callback)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PushError($"[MDX Preview] {action} 失败: {ex}");
                ShowMessage("MDX 预览", $"{action} 失败：{ex.Message}");
            }
        }

        void ReloadCurrentModel()
        {
            if (string.IsNullOrWhiteSpace(_currentModelPath))
            {
                ShowMessage("MDX 预览", "还没有打开任何模型。");
                return;
            }
            OpenModel(_currentModelPath, false);
        }

        void CloseModel()
        {
            try
            {
                _view?.ClearModel();
                _currentModelPath = string.Empty;
                RefreshAfterLoad();
                _infoPanel?.AppendLog("已关闭模型。");
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 关闭模型失败: {ex.Message}");
            }
        }

        /// <summary>打开模型；<paramref name="silent"/> 为 true 时只写日志不弹框（启动恢复用）。</summary>
        void OpenModel(string path, bool silent)
        {
            if (_view == null) return;
            path = path?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(path))
            {
                if (!silent) ShowMessage("打开模型", "请先填写或选择一个模型路径。");
                return;
            }

            try
            {
                string error = _view.LoadModel(path);
                if (!string.IsNullOrEmpty(error))
                {
                    _infoPanel?.AppendLog("打开失败: " + error);
                    if (silent) GD.PushWarning($"[MDX Preview] 恢复上次模型失败: {error}");
                    else ShowMessage("打开模型失败", error);
                    return;
                }

                _currentModelPath = path;
                _settings.AddRecent(path);
                SaveSettings();
                RefreshAfterLoad();
                _infoPanel?.AppendLog($"已载入模型: {path}");
            }
            catch (Exception ex)
            {
                string message = $"打开模型时发生异常：{ex.Message}";
                GD.PushError($"[MDX Preview] {message}");
                _infoPanel?.AppendLog(message);
                if (!silent) ShowMessage("打开模型失败", message);
            }
        }

        // ---- 拖拽 -----------------------------------------------------------------------

        /// <summary>目录 → BLP 搜索路径；.mpq → MPQ 列表；.mdx/.mdl → 直接打开。</summary>
        bool HandleDroppedPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            bool handled = false;
            var models = new List<string>();

            foreach (string raw in paths)
            {
                if (MdxEditorUtil.IsDirectoryPath(raw))
                {
                    string folder = MdxEditorUtil.ToDiskPath(raw);
                    _settingsPanel?.AddBlpPath(folder);
                    _infoPanel?.AppendLog($"已添加贴图搜索路径: {folder}（点「应用并重载」生效）");
                    handled = true;
                }
                else if (MdxEditorUtil.IsMpqPath(raw))
                {
                    string mpq = MdxEditorUtil.ToDiskPath(raw);
                    _settingsPanel?.AddMpqPath(mpq);
                    _infoPanel?.AppendLog($"已添加 MPQ: {mpq}（点「应用并重载」生效）");
                    handled = true;
                }
                else if (MdxEditorUtil.IsModelPath(raw))
                {
                    models.Add(raw);
                    handled = true;
                }
            }

            if (models.Count > 0) OpenModel(models[0], false);
            else if (!handled) _infoPanel?.AppendLog("拖入的内容无法识别（支持目录、.mpq、.mdx/.mdl）。");
            return handled;
        }

        void OnRecentSelected(long index)
        {
            if (index < 0 || index >= _settings.RecentModels.Count) return;
            OpenModel(_settings.RecentModels[(int)index], false);
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) => true;

        public override void _DropData(Vector2 atPosition, Variant data)
            => HandleDroppedPaths(MdxEditorUtil.ExtractDropPaths(data));

        // ---- 设置应用 / 启动恢复 ----------------------------------------------------------

        void OnSettingsApplied()
        {
            if (_assets != null)
            {
                _assets.TeamColor = MdxTeamColor.All[Mathf.Clamp(_settings.TeamColorIndex, 0, MdxTeamColor.All.Length - 1)];
                _assets.InvalidateReplaceableTextures();
            }
            _view?.ApplySettings();
            _view?.RefreshFromSettings();
            _view?.Current?.RebuildMaterials();
            _infoPanel?.RefreshMissing(ProblemAssets());
            SaveSettings();
            _infoPanel?.AppendLog($"配置已应用：MPQ {_assets?.ArchivePaths.Count ?? 0} 个，贴图路径 {_assets?.TextureSearchPaths.Count ?? 0} 个。");

            if (!string.IsNullOrWhiteSpace(_currentModelPath)) OpenModel(_currentModelPath, false);
        }

        void RestoreLastSession()
        {
            try
            {
                if (_settings.MpqPaths.Count > 0 || _settings.BlpSearchPaths.Count > 0)
                {
                    _settingsPanel.SilentApply = true;
                    bool applied = _settingsPanel.ApplyNow();
                    _settingsPanel.SilentApply = false;
                    if (applied)
                        _infoPanel?.AppendLog("已应用保存的配置。");
                    else
                        _infoPanel?.AppendLog("保存的配置应用失败（已忽略，可到「设置」页修正）。");
                }
                else
                {
                    _infoPanel?.AppendLog("尚未配置游戏路径 / MPQ：请在「设置」页配置后点「应用并重载」。");
                }

                if (!string.IsNullOrWhiteSpace(_settings.LastModel))
                    OpenModel(_settings.LastModel, true);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 恢复上次会话失败: {ex.Message}");
                _infoPanel?.AppendLog("恢复上次会话失败: " + ex.Message);
            }
        }

        void SaveSettings()
        {
            try { _settings?.Save(); }
            catch (Exception ex) { GD.PushWarning($"[MDX Preview] 保存设置失败: {ex.Message}"); }
        }

        void ShowMessage(string title, string message)
        {
            GD.PushWarning($"[MDX Preview] {title}: {message}");
            if (_dialog == null)
            {
                _dialog = new AcceptDialog { Title = title };
                AddChild(_dialog);
            }
            _dialog.Title = title;
            _dialog.DialogText = message;
            _dialog.PopupCentered(new Vector2I(620, 240));
        }

        // ---- 导出 -----------------------------------------------------------------------

        /// <summary>
        /// 导出当前模型到 Godot 子文件夹：
        /// <c>MdxExporter.Export(MdxModelNode, MdxAssetLibrary, string)</c> 返回
        /// <c>Result { Success, Message, Files, ScenePath, InfoPath }</c>。
        /// 导出模块自身不抛异常，这里仍然整体包一层 try/catch，保证不会打断编辑器。
        /// </summary>
        void OnExportRequested(string outputFolder)
        {
            MdxModelNode node = _view?.Current;
            if (node == null)
            {
                _settingsPanel.SetExportResult(false, "请先打开一个模型再导出。");
                return;
            }

            try
            {
                MdxExporter.Result result = MdxExporter.Export(node, _assets, outputFolder, 30, _settings.ExportScale);
                if (result == null)
                {
                    _settingsPanel.SetExportResult(false, "导出模块返回了空结果。");
                    return;
                }

                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.Message)) lines.Add(result.Message);
                if (!string.IsNullOrWhiteSpace(result.OutputFolder)) lines.Add("输出目录: " + result.OutputFolder);
                if (!string.IsNullOrWhiteSpace(result.ScenePath)) lines.Add("场景: " + result.ScenePath);
                if (!string.IsNullOrWhiteSpace(result.InfoPath)) lines.Add("信息: " + result.InfoPath);
                if (result.Files != null && result.Files.Count > 0)
                {
                    lines.Add($"输出文件（{result.Files.Count} 个）:");
                    foreach (string file in result.Files) lines.Add("  " + file);
                }

                _settingsPanel.SetExportResult(result.Success, string.Join("\n", lines));
                _infoPanel?.AppendLog(result.Success
                    ? $"导出成功: {result.ScenePath}"
                    : $"导出失败: {result.Message}");

                if (result.Success) RescanEditorFileSystem();
            }
            catch (Exception ex)
            {
                string message = $"导出失败：{ex.Message}";
                GD.PushError($"[MDX Preview] {message}");
                _settingsPanel.SetExportResult(false, message);
                _infoPanel?.AppendLog(message);
            }
        }

        /// <summary>导出到 res:// 之后让编辑器的文件系统重新扫描，资源立刻可见。</summary>
        static void RescanEditorFileSystem()
        {
            try
            {
                EditorInterface.Singleton?.GetResourceFilesystem()?.Scan();
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 刷新编辑器文件系统失败: {ex.Message}");
            }
        }

        // ---- 每帧刷新 -------------------------------------------------------------------

        public override void _Process(double delta)
        {
            // _view/_settings 为空说明脚本实例被编辑器重载程序集后重建过（没有重新注入依赖），
            // 这时什么也不做，避免空引用打断编辑器。
            if (_view == null || _settings == null) return;

            MdxModelNode node = _view.Current;
            if (node == null || node.Model == null) return;

            // 时间轴（拖拽期间不回写，避免与用户输入打架）
            double now = Time.GetTicksMsec();
            if (now > _scrubHoldUntilMs)
            {
                float ratio = node.SequenceRatio;
                if (Mathf.Abs((float)_timelineSlider.Value - ratio) > 0.0005f)
                {
                    _uiGuard = true;
                    _timelineSlider.Value = ratio;
                    _uiGuard = false;
                }
            }

            float duration = node.SequenceDurationMs;
            _timeLabel.Text = duration > 0f
                ? $"{Mathf.Max(0f, node.SequencePositionMs - node.SequenceStartMs):0} / {duration:0} ms"
                : "-";

            string playText = node.IsPlaying ? "暂停" : "播放";
            if (_playButton.Text != playText) _playButton.Text = playText;

            bool loop = node.GetLoop();
            if (_loopCheck.ButtonPressed != loop)
            {
                _uiGuard = true;
                _loopCheck.ButtonPressed = loop;
                _uiGuard = false;
            }

            float speed = node.GetSpeed();
            if (Mathf.Abs((float)_speedSlider.Value - speed) > 0.001f)
            {
                _uiGuard = true;
                _speedSlider.Value = Mathf.Clamp(speed, 0.1f, 4f);
                _uiGuard = false;
                _speedLabel.Text = $"{speed:0.00}x";
            }

            // 角度标签始终跟随模型实际旋转；滑条只在自动旋转关闭时回写，
            // 否则会和用户拖动滑条互相打架。
            Vector3 rotation = _view.ModelRotationDegrees;
            _rotXValue.Text = $"{rotation.X:0}°";
            _rotYValue.Text = $"{rotation.Y:0}°";
            _rotZValue.Text = $"{rotation.Z:0}°";
            if (!_settings.AutoRotate && !_uiGuard)
            {
                _uiGuard = true;
                _rotX.Value = Mathf.Clamp(rotation.X, -180f, 180f);
                _rotY.Value = Mathf.Clamp(rotation.Y, -180f, 180f);
                _rotZ.Value = Mathf.Clamp(rotation.Z, -180f, 180f);
                _uiGuard = false;
            }

            _infoPanel?.SetPlayhead(node.SequencePositionMs, node.SequenceStartMs, duration);
        }
    }
}
