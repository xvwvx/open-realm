using System;
using System.Collections.Generic;
using Godot;
using MdxPreview.Runtime;

namespace MdxPreview.EditorUi
{
    /// <summary>
    /// “设置”页：游戏路径 / MPQ 归档（优先级可调）/ BLP 贴图搜索路径 / Mipmap 开关，
    /// 以及导出目录与“导出到 Godot 子文件夹”按钮。
    ///
    /// 本面板直接写 <see cref="MdxPreviewSettings"/>，并在每次改动后 <c>Save()</c>；
    /// 「应用并重载」会调用 <c>settings.ApplyTo(assets, out error)</c>，
    /// 失败通过 <see cref="ErrorReported"/> 交给 Dock 弹中文错误框。
    /// </summary>
    [Tool]
    public partial class MdxSettingsPanel : VBoxContainer
    {
        enum DialogPurpose
        {
            GameFolder,
            MpqFile,
            BlpFolder,
            OutputFolder,
        }

        // Non-null defaults: the panel is also rebuilt by Godot on assembly reload,
        // where the injected references from the dock are not available yet.
        MdxPreviewSettings _settings = MdxPreviewSettings.Load();
        MdxAssetLibrary _assets = new MdxAssetLibrary();

        LineEdit _gamePathEdit;
        ItemList _mpqList;
        ItemList _blpList;
        CheckBox _mipmapCheck;
        LineEdit _outputFolderEdit;
        TextEdit _exportOutput;
        Label _mpqSummary;

        FileDialog _dialog;
        DialogPurpose _purpose;

        /// <summary>
        /// 游戏路径只在本次会话里保留（<see cref="MdxPreviewSettings"/> 没有该字段，
        /// 而 runtime/ 由别的任务维护，这里不去改动它）；重启后从第一个 MPQ 的目录推断。
        /// </summary>
        string _gameFolder = string.Empty;

        /// <summary>字段被改动（用于保存设置）。</summary>
        public event Action SettingsChanged;

        /// <summary><c>ApplyTo</c> 成功。</summary>
        public event Action SettingsApplied;

        /// <summary>需要向用户展示的错误（Dock 负责弹框）。</summary>
        public event Action<string> ErrorReported;

        /// <summary>请求导出当前模型到指定目录（Dock 负责调用导出模块）。</summary>
        public event Action<string> ExportRequested;

        /// <summary>拖入路径的处理（交给 Dock 统一分类：目录/MPQ/模型）。</summary>
        public Func<string[], bool> DropHandler { get; set; }

        /// <summary>
        /// Godot 的脚本类必须有可无参构造（编辑器重载程序集时会用无参构造重建脚本实例），
        /// 因此真正的依赖通过 <see cref="Initialize"/> 注入，注入必须发生在加入场景树之前。
        /// </summary>
        public MdxSettingsPanel()
        {
            Name = "SettingsPanel";
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddThemeConstantOverride("separation", 4);
        }

        /// <summary>注入设置与资源库（必须在 AddChild 之前调用）。</summary>
        public void Initialize(MdxPreviewSettings settings, MdxAssetLibrary assets)
        {
            _settings = settings;
            _assets = assets;
        }

        public override void _Ready()
        {
            if (_settings == null || _assets == null)
            {
                AddChild(new Label { Text = "设置面板未初始化（缺少 settings/assets）。" });
                return;
            }
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            AddChild(scroll);

            var content = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            content.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(content);

            // ---- 游戏路径 -----------------------------------------------------------------
            content.AddChild(new Label { Text = "游戏路径（Warcraft III 安装目录）" });
            var gameRow = new HBoxContainer();
            content.AddChild(gameRow);
            _gamePathEdit = new LineEdit
            {
                PlaceholderText = @"例如 D:\Games\Warcraft III",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "用于「扫描 MPQ」自动寻找 war3.mpq / War3x.mpq 等归档",
            };
            _gamePathEdit.TextChanged += _ => SaveSettings();
            gameRow.AddChild(_gamePathEdit);

            var browseGame = new Button { Text = "浏览…" };
            browseGame.Pressed += () => OpenDialog(DialogPurpose.GameFolder, "选择游戏目录");
            gameRow.AddChild(browseGame);

            var scanRow = new HBoxContainer();
            content.AddChild(scanRow);
            var scanButton = new Button { Text = "扫描 MPQ", TooltipText = "按引擎优先级（War3Patch → War3xLocal → War3x → War3Local → war3）排序" };
            scanButton.Pressed += OnScanMpqPressed;
            scanRow.AddChild(scanButton);
            _mpqSummary = new Label { Text = string.Empty, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scanRow.AddChild(_mpqSummary);

            content.AddChild(new HSeparator());

            // ---- MPQ 列表 -----------------------------------------------------------------
            content.AddChild(new Label { Text = "MPQ 归档（靠上者优先级更高）" });
            _mpqList = new ItemList
            {
                CustomMinimumSize = new Vector2(0f, 96f),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                AllowReselect = true,
                TooltipText = "从上到下依次查找；第一个命中的归档生效。可拖入 .mpq 文件。",
            };
            content.AddChild(_mpqList);

            var mpqButtons = new HBoxContainer();
            content.AddChild(mpqButtons);
            AddButton(mpqButtons, "添加文件…", () => OpenDialog(DialogPurpose.MpqFile, "选择 MPQ 归档"));
            AddButton(mpqButtons, "移除", OnRemoveMpq);
            AddButton(mpqButtons, "上移", () => MoveMpq(-1));
            AddButton(mpqButtons, "下移", () => MoveMpq(1));

            content.AddChild(new HSeparator());

            // ---- BLP 搜索路径 -------------------------------------------------------------
            content.AddChild(new Label { Text = "贴图搜索路径（BLP/TGA/PNG 目录，可拖入文件夹）" });
            _blpList = new ItemList
            {
                CustomMinimumSize = new Vector2(0f, 96f),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                AllowReselect = true,
                TooltipText = "这些目录会在 MPQ 之前被查找，方便覆盖/调试贴图",
            };
            content.AddChild(_blpList);

            var blpButtons = new HBoxContainer();
            content.AddChild(blpButtons);
            AddButton(blpButtons, "添加文件夹…", () => OpenDialog(DialogPurpose.BlpFolder, "选择贴图目录"));
            AddButton(blpButtons, "移除", OnRemoveBlp);
            AddButton(blpButtons, "上移", () => MoveBlp(-1));
            AddButton(blpButtons, "下移", () => MoveBlp(1));

            content.AddChild(new HSeparator());

            // ---- 应用 ---------------------------------------------------------------------
            var applyRow = new HBoxContainer();
            content.AddChild(applyRow);
            var applyButton = new Button
            {
                Text = "应用并重载",
                TooltipText = "重新打开 MPQ / 重建搜索路径，并重新载入当前模型",
            };
            applyButton.Pressed += OnApplyPressed;
            applyRow.AddChild(applyButton);

            _mipmapCheck = new CheckBox { Text = "生成 Mipmap" };
            _mipmapCheck.Toggled += on =>
            {
                _settings.GenerateMipmaps = on;
                SaveSettings();
            };
            applyRow.AddChild(_mipmapCheck);

            content.AddChild(new HSeparator());

            // ---- 导出 ---------------------------------------------------------------------
            content.AddChild(new Label { Text = "导出目录（默认 res://Godot）" });
            var outputRow = new HBoxContainer();
            content.AddChild(outputRow);
            _outputFolderEdit = new LineEdit
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "导出的 Godot 资源会写入该目录下的子文件夹",
            };
            _outputFolderEdit.TextChanged += _ => SaveSettings();
            outputRow.AddChild(_outputFolderEdit);

            var browseOutput = new Button { Text = "浏览…" };
            browseOutput.Pressed += () => OpenDialog(DialogPurpose.OutputFolder, "选择导出目录");
            outputRow.AddChild(browseOutput);

            var exportButton = new Button
            {
                Text = "导出到 Godot 子文件夹",
                TooltipText = "把当前模型的网格/材质/动画导出为 Godot 资源",
            };
            exportButton.Pressed += OnExportPressed;
            content.AddChild(exportButton);

            content.AddChild(new Label { Text = "导出结果" });
            _exportOutput = new TextEdit
            {
                Editable = false,
                CustomMinimumSize = new Vector2(0f, 96f),
                SizeFlagsVertical = SizeFlags.ExpandFill,
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                Text = "-",
            };
            content.AddChild(_exportOutput);

            SyncFromSettings();
        }

        static Button AddButton(HBoxContainer parent, string text, Action onPressed)
        {
            var button = new Button { Text = text };
            button.Pressed += onPressed;
            parent.AddChild(button);
            return button;
        }

        // ---- 设置 <-> 界面 ---------------------------------------------------------------

        public void SyncFromSettings()
        {
            if (_settings == null) return;
            if (_gamePathEdit != null && _gamePathEdit.Text != GameFolderHint())
                _gamePathEdit.Text = GameFolderHint();
            if (_mipmapCheck != null) _mipmapCheck.ButtonPressed = _settings.GenerateMipmaps;
            if (_outputFolderEdit != null) _outputFolderEdit.Text = _settings.OutputFolder;
            RefreshMpqList(-1);
            RefreshBlpList(-1);
            UpdateSummary();
        }

        /// <summary>游戏路径输入框在没填过时用第一个 MPQ 所在目录做提示。</summary>
        string GameFolderHint()
        {
            if (!string.IsNullOrWhiteSpace(_gameFolder)) return _gameFolder;
            if (_settings.MpqPaths.Count > 0) return MdxEditorUtil.DirectoryOf(_settings.MpqPaths[0]);
            return string.Empty;
        }

        void SaveSettings()
        {
            if (_settings == null) return;
            _gameFolder = _gamePathEdit?.Text?.Trim() ?? _gameFolder;
            _settings.OutputFolder = string.IsNullOrWhiteSpace(_outputFolderEdit?.Text)
                ? MdxPreviewSettings.DefaultOutputFolder
                : _outputFolderEdit.Text.Trim();
            try { _settings.Save(); }
            catch (Exception ex) { GD.PushWarning($"[MDX Preview] 保存设置失败: {ex.Message}"); }
            SettingsChanged?.Invoke();
        }

        void UpdateSummary()
        {
            if (_mpqSummary == null || _settings == null) return;
            _mpqSummary.Text = $"MPQ {_settings.MpqPaths.Count} 个　贴图路径 {_settings.BlpSearchPaths.Count} 个";
            var archiveInfo = new List<string>();
            foreach (string path in _assets.ArchivePaths) archiveInfo.Add(System.IO.Path.GetFileName(path));
            _mpqSummary.TooltipText = archiveInfo.Count == 0
                ? "当前没有已打开的归档"
                : "已打开: " + string.Join(", ", archiveInfo);
        }

        // ---- MPQ / BLP 列表编辑 -----------------------------------------------------------

        void RefreshMpqList(int select)
        {
            if (_mpqList == null || _settings == null) return;
            _mpqList.Clear();
            foreach (string path in _settings.MpqPaths) _mpqList.AddItem(MdxEditorUtil.Shorten(path, 60));
            if (select >= 0 && select < _settings.MpqPaths.Count) _mpqList.Select(select);
            for (int i = 0; i < _settings.MpqPaths.Count && i < _mpqList.ItemCount; i++)
                _mpqList.SetItemTooltip(i, _settings.MpqPaths[i]);
            UpdateSummary();
        }

        void RefreshBlpList(int select)
        {
            if (_blpList == null || _settings == null) return;
            _blpList.Clear();
            foreach (string path in _settings.BlpSearchPaths) _blpList.AddItem(MdxEditorUtil.Shorten(path, 60));
            if (select >= 0 && select < _settings.BlpSearchPaths.Count) _blpList.Select(select);
            for (int i = 0; i < _settings.BlpSearchPaths.Count && i < _blpList.ItemCount; i++)
                _blpList.SetItemTooltip(i, _settings.BlpSearchPaths[i]);
            UpdateSummary();
        }

        void OnScanMpqPressed()
        {
            if (_settings == null) return;
            try
            {
                string folder = _gamePathEdit?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    ErrorReported?.Invoke("请先填写游戏路径，或点「浏览…」选择 Warcraft III 安装目录。");
                    return;
                }
                if (!System.IO.Directory.Exists(folder))
                {
                    ErrorReported?.Invoke($"游戏路径不存在：{folder}");
                    return;
                }

                List<string> found = MdxPreviewSettings.DiscoverMpq(folder);
                if (found.Count == 0)
                {
                    ErrorReported?.Invoke($"在 {folder} 下没有找到 .mpq 文件。");
                    return;
                }

                _settings.MpqPaths.Clear();
                _settings.MpqPaths.AddRange(found);
                _gameFolder = folder;
                RefreshMpqList(0);
                SaveSettings();
            }
            catch (Exception ex)
            {
                ErrorReported?.Invoke($"扫描 MPQ 失败：{ex.Message}");
            }
        }

        int SelectedMpq()
        {
            int[] selected = _mpqList?.GetSelectedItems() ?? Array.Empty<int>();
            return selected.Length > 0 ? selected[0] : -1;
        }

        int SelectedBlp()
        {
            int[] selected = _blpList?.GetSelectedItems() ?? Array.Empty<int>();
            return selected.Length > 0 ? selected[0] : -1;
        }

        void OnRemoveMpq()
        {
            int index = SelectedMpq();
            if (index < 0) return;
            _settings.MpqPaths.RemoveAt(index);
            RefreshMpqList(Math.Min(index, _settings.MpqPaths.Count - 1));
            SaveSettings();
        }

        void MoveMpq(int delta)
        {
            int index = SelectedMpq();
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _settings.MpqPaths.Count) return;
            (_settings.MpqPaths[index], _settings.MpqPaths[target]) = (_settings.MpqPaths[target], _settings.MpqPaths[index]);
            RefreshMpqList(target);
            SaveSettings();
        }

        void OnRemoveBlp()
        {
            int index = SelectedBlp();
            if (index < 0) return;
            _settings.BlpSearchPaths.RemoveAt(index);
            RefreshBlpList(Math.Min(index, _settings.BlpSearchPaths.Count - 1));
            SaveSettings();
        }

        void MoveBlp(int delta)
        {
            int index = SelectedBlp();
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _settings.BlpSearchPaths.Count) return;
            (_settings.BlpSearchPaths[index], _settings.BlpSearchPaths[target]) = (_settings.BlpSearchPaths[target], _settings.BlpSearchPaths[index]);
            RefreshBlpList(target);
            SaveSettings();
        }

        public void AddMpqPath(string path)
        {
            if (_settings == null || string.IsNullOrWhiteSpace(path)) return;
            foreach (string existing in _settings.MpqPaths)
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            _settings.MpqPaths.Add(path);
            RefreshMpqList(_settings.MpqPaths.Count - 1);
            SaveSettings();
        }

        public void AddBlpPath(string path)
        {
            if (_settings == null || string.IsNullOrWhiteSpace(path)) return;
            foreach (string existing in _settings.BlpSearchPaths)
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            _settings.BlpSearchPaths.Add(path);
            RefreshBlpList(_settings.BlpSearchPaths.Count - 1);
            SaveSettings();
        }

        // ---- 应用 -----------------------------------------------------------------------

        /// <summary>暂时静默应用（编辑器启动恢复配置时不弹框，只返回失败）。</summary>
        public bool SilentApply { get; set; }

        void OnApplyPressed() => ApplyNow();

        /// <summary>把设置应用到资源库；成功返回 true（失败通过 <see cref="ErrorReported"/> 上报）。</summary>
        public bool ApplyNow()
        {
            if (_settings == null || _assets == null) return false;
            SaveSettings();
            try
            {
                _settings.ApplyTo(_assets, out string error);
                if (!string.IsNullOrEmpty(error))
                {
                    if (!SilentApply) ErrorReported?.Invoke(error);
                    UpdateSummary();
                    return false;
                }
                UpdateSummary();
                SettingsApplied?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                if (!SilentApply) ErrorReported?.Invoke($"应用设置时发生异常：{ex.Message}");
                return false;
            }
        }

        // ---- 导出 -----------------------------------------------------------------------

        void OnExportPressed()
        {
            if (_settings == null) return;
            string folder = _outputFolderEdit?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder)) folder = MdxPreviewSettings.DefaultOutputFolder;
            _settings.OutputFolder = folder;
            SaveSettings();
            SetExportResult(false, $"正在导出到 {folder} …");
            ExportRequested?.Invoke(folder);
        }

        public void SetExportResult(bool success, string message)
        {
            if (_exportOutput == null) return;
            string body = string.IsNullOrWhiteSpace(message) ? (success ? "导出完成" : "导出失败") : message;
            _exportOutput.Text = (success ? "✔ 成功\n" : "✘ 失败\n") + body;
        }

        // ---- 文件对话框与拖拽 -------------------------------------------------------------

        void OpenDialog(DialogPurpose purpose, string title)
        {
            _purpose = purpose;
            if (_dialog == null)
            {
                _dialog = new FileDialog
                {
                    Access = FileDialog.AccessEnum.Filesystem,
                    UseNativeDialog = true,
                    Size = new Vector2I(760, 520),
                };
                _dialog.FileSelected += OnDialogFileSelected;
                // Directory mode reports through dir_selected, not file_selected;
                // without this the game path / BLP path pickers never delivered a result.
                _dialog.DirSelected += OnDialogDirSelected;
                AddChild(_dialog);
            }

            _dialog.Title = title;
            switch (purpose)
            {
                case DialogPurpose.MpqFile:
                    _dialog.FileMode = FileDialog.FileModeEnum.OpenFile;
                    _dialog.Filters = new[] { MdxEditorUtil.MpqFilter };
                    break;
                case DialogPurpose.GameFolder:
                case DialogPurpose.BlpFolder:
                case DialogPurpose.OutputFolder:
                    _dialog.FileMode = FileDialog.FileModeEnum.OpenDir;
                    break;
            }

            string start = purpose switch
            {
                DialogPurpose.GameFolder => _gamePathEdit?.Text?.Trim() ?? string.Empty,
                DialogPurpose.MpqFile => _settings.MpqPaths.Count > 0
                    ? MdxEditorUtil.DirectoryOf(_settings.MpqPaths[0])
                    : _gamePathEdit?.Text?.Trim() ?? string.Empty,
                _ => _outputFolderEdit?.Text?.Trim() ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(start) && System.IO.Directory.Exists(MdxEditorUtil.ToDiskPath(start)))
                _dialog.CurrentDir = MdxEditorUtil.ToDiskPath(start);

            _dialog.PopupCentered(new Vector2I(760, 520));
        }

        void OnDialogFileSelected(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            switch (_purpose)
            {
                case DialogPurpose.GameFolder:
                    _gamePathEdit.Text = path;
                    SaveSettings();
                    break;
                case DialogPurpose.MpqFile:
                    AddMpqPath(path);
                    break;
                case DialogPurpose.BlpFolder:
                    AddBlpPath(path);
                    break;
                case DialogPurpose.OutputFolder:
                    _outputFolderEdit.Text = path.Replace('\\', '/');
                    SaveSettings();
                    break;
            }
        }

        /// <summary>
        /// Folder pickers (game path / BLP search path / export folder) arrive here.
        /// Godot's FileDialog emits dir_selected for OpenDir mode.
        /// </summary>
        void OnDialogDirSelected(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string normalized = dir.Replace('/', '\\').TrimEnd('\\');
            switch (_purpose)
            {
                case DialogPurpose.GameFolder:
                    _gamePathEdit.Text = normalized;
                    _gameFolder = normalized;
                    SaveSettings();
                    // Filling the field is only half the job: scan straight away so the
                    // user sees the MPQ list without a second click.
                    OnScanMpqPressed();
                    break;
                case DialogPurpose.BlpFolder:
                    AddBlpPath(normalized);
                    break;
                case DialogPurpose.OutputFolder:
                    _outputFolderEdit.Text = dir.Replace('\\', '/');
                    SaveSettings();
                    break;
                default:
                    // An OpenFile dialog may still route through here on some platforms.
                    OnDialogFileSelected(normalized);
                    break;
            }
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) => DropHandler != null;

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (DropHandler == null) return;
            string[] paths = MdxEditorUtil.ExtractDropPaths(data);
            if (paths.Length > 0) DropHandler(paths);
        }
    }
}
