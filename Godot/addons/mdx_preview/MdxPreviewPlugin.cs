using System;
using Godot;
using MdxPreview.EditorUi;
using MdxPreview.Runtime;

namespace MdxPreview
{
    [Tool]
    public partial class MdxPreviewPlugin : EditorPlugin
    {
        const string DockScenePath = "res://addons/mdx_preview/editor/MdxPreviewDock.tscn";

        public const string BuildStamp = "2026-10-05 menu-window-layout";

        MdxAssetLibrary _assets;
        MdxPreviewSettings _settings;
        MenuButton _menuButton;
        Window _previewWindow;
        MdxPreviewDock _windowDock;

        public override void _EnterTree()
        {
            try
            {
                _assets = new MdxAssetLibrary();
                _settings = MdxPreviewSettings.Load();
            }
            catch (Exception ex)
            {
                GD.PushError($"[MDX Preview] 初始化资源库/设置失败: {ex.Message}");
                return;
            }

            MountMainMenu();
            GD.Print($"[MDX Preview] 已就绪（不嵌入 Dock，菜单打开窗口），构建标识={BuildStamp}。");
        }

        public override void _ExitTree()
        {
            UnmountMainMenu();

            try
            {
                _assets?.Dispose();
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 释放资源库失败: {ex.Message}");
            }
            _assets = null;
            _settings = null;
        }

        void MountMainMenu()
        {
            try
            {
                _menuButton = new MenuButton
                {
                    Name = "MdxPreviewMenu",
                    Text = "MDX 预览",
                    Flat = true,
                    TooltipText = "打开 MDX 预览窗口（左侧预览 / 右侧控制）",
                };

                PopupMenu popup = _menuButton.GetPopup();
                popup.AddItem("打开预览窗口", 0);
                popup.AddSeparator();
                popup.IdPressed += OnMenuPressed;

                if (TryMountIntoMainMenuBar(_menuButton))
                {
                    GD.Print("[MDX Preview] 已在主菜单栏挂载「MDX 预览」菜单。");
                }
                else
                {
                    AddControlToContainer(CustomControlContainer.Toolbar, _menuButton);
                    GD.Print("[MDX Preview] 主菜单栏未找到，已挂到工具栏。");
                }
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 挂载主菜单失败: {ex.Message}");
            }
        }

        void UnmountMainMenu()
        {
            try
            {
                if (_menuButton != null)
                {
                    Node parent = _menuButton.GetParent();
                    if (parent != null) parent.RemoveChild(_menuButton);
                    else RemoveControlFromContainer(CustomControlContainer.Toolbar, _menuButton);
                    _menuButton.QueueFree();
                    _menuButton = null;
                }

                ClosePreviewWindow();
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 卸载菜单失败: {ex.Message}");
            }
        }

        void OnMenuPressed(long id)
        {
            if (id == 0) OpenPreviewWindow();
        }

        /// <summary>以独立窗口打开预览界面（若已打开则聚焦，若已关闭则从零重新构建）。</summary>
        public void OpenPreviewWindow()
        {
            // 1. 如果窗口已经存在且正常显示中，直接置顶聚焦
            if (IsInstanceValid(_previewWindow) && _previewWindow.Visible)
            {
                _previewWindow.GrabFocus();
                return;
            }

            // 2. 避免任何残留脏状态，确保旧窗口彻底清理
            if (IsInstanceValid(_previewWindow))
            {
                ClosePreviewWindow();
            }

            if (!ResourceLoader.Exists(DockScenePath))
            {
                GD.PushError($"[MDX Preview] 找不到预览场景: {DockScenePath}");
                return;
            }

            var scene = GD.Load<PackedScene>(DockScenePath);
            _windowDock = scene?.Instantiate<MdxPreviewDock>();
            if (_windowDock == null)
            {
                GD.PushError($"[MDX Preview] 预览场景根节点不是 MdxPreviewDock: {DockScenePath}");
                return;
            }

            // 依赖必须在入树之前注入：面板的 _Ready 会立刻构建界面并读取设置
            _windowDock.Initialize(_assets, _settings);

            Vector2I size = _settings?.PreviewWindowSize ?? MdxPreviewSettings.DefaultPreviewWindowSize;
            if (size.X < 320 || size.Y < 240) size = MdxPreviewSettings.DefaultPreviewWindowSize;

            _previewWindow = new Window
            {
                Name = "MdxPreviewWindow",
                Title = "MDX 预览",
                Size = size,
                MinSize = new Vector2I(640, 400),
                Exclusive = false,
            };

            _previewWindow.AddChild(_windowDock);
            AddChild(_previewWindow);

            // 3. 计算位置（首次或重置时居中，否则恢复保存的位置）
            Vector2I saved = _settings?.PreviewWindowPosition ?? MdxPreviewSettings.CenteredPreviewWindow;
            bool shouldCenter = saved == MdxPreviewSettings.CenteredPreviewWindow
                                || saved == Vector2I.Zero
                                || saved.X < 0 || saved.Y < 0;

            if (shouldCenter)
            {
                CenterOnEditorScreen(_previewWindow);
            }
            else
            {
                _previewWindow.Position = ClampToScreen(saved, _previewWindow.Size);
            }

            // 4. 显示窗口
            _previewWindow.Popup();

            // 5. 显示后再绑定监听器，防止初始构建阶段的脏信号
            _previewWindow.CloseRequested += ClosePreviewWindow;
            _previewWindow.SizeChanged += RememberWindowGeometry;
            _previewWindow.VisibilityChanged += OnWindowVisibilityChanged;

            GD.Print($"[MDX Preview] 预览窗口已创建（{_previewWindow.Size.X}×{_previewWindow.Size.Y}）。");
            _previewWindow.GrabFocus();
        }

        void OnWindowVisibilityChanged()
        {
            // 只要窗口变为不可见（不管是点叉、隐藏还是快捷键关闭），立即触发彻底销毁，绝不留在后台
            if (IsInstanceValid(_previewWindow) && !_previewWindow.Visible)
            {
                ClosePreviewWindow();
            }
        }

        void RememberWindowGeometry()
        {
            if (_settings == null || !IsInstanceValid(_previewWindow)) return;
            if (_previewWindow.Position == Vector2I.Zero) return;

            _settings.RememberPreviewWindow(_previewWindow.Position, _previewWindow.Size);
        }

        /// <summary>可靠地将窗口居中在当前编辑器所在的屏幕工作区中心。</summary>
        static void CenterOnEditorScreen(Window window)
        {
            int screen = DisplayServer.WindowGetCurrentScreen();
            if (screen < 0 || screen >= DisplayServer.GetScreenCount())
            {
                screen = DisplayServer.GetPrimaryScreen();
            }

            Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
            Vector2I centerPos = usable.Position + (usable.Size - window.Size) / 2;
            window.Position = centerPos;
        }

        /// <summary>Keeps a restored window position usable after monitor layout changes.</summary>
        static Vector2I ClampToScreen(Vector2I position, Vector2I size)
        {
            int screen = DisplayServer.GetPrimaryScreen();
            int count = DisplayServer.GetScreenCount();
            for (int i = 0; i < count; i++)
            {
                Rect2I rect = DisplayServer.ScreenGetUsableRect(i);
                if (rect.HasPoint(position)) { screen = i; break; }
            }

            Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
            if (usable.Size.X <= 0 || usable.Size.Y <= 0) return position;

            int x = Math.Clamp(position.X, usable.Position.X - size.X / 4,
                               usable.Position.X + usable.Size.X - size.X / 2);
            int y = Math.Clamp(position.Y, usable.Position.Y,
                               usable.Position.Y + usable.Size.Y - 60);
            return new Vector2I(x, y);
        }

        /// <summary>彻底关闭并销毁预览窗口及其节点树，绝不常驻后台。</summary>
        public void ClosePreviewWindow()
        {
            if (!IsInstanceValid(_previewWindow))
            {
                _previewWindow = null;
                _windowDock = null;
                return;
            }

            // 1. 关闭前先记录最后的位置和大小
            RememberWindowGeometry();

            // 2. 解除所有信号绑定，避免销毁或退出树期间触发冗余回调
            _previewWindow.CloseRequested -= ClosePreviewWindow;
            _previewWindow.SizeChanged -= RememberWindowGeometry;
            _previewWindow.VisibilityChanged -= OnWindowVisibilityChanged;

            // 3. 立即切断处理循环与渲染，杜绝后台占用
            _previewWindow.ProcessMode = ProcessModeEnum.Disabled;
            _previewWindow.Hide();

            // 4. 从场景树彻底脱离并释放
            if (_previewWindow.GetParent() != null)
            {
                _previewWindow.GetParent().RemoveChild(_previewWindow);
            }
            _previewWindow.QueueFree();

            // 5. 清空本地强引用，等待 GC 与引擎完全回收
            _previewWindow = null;
            _windowDock = null;

            GD.Print("[MDX Preview] 预览窗口已彻底销毁并释放资源。");
        }

        bool TryMountIntoMainMenuBar(Control control)
        {
            try
            {
                Control baseControl = EditorInterface.Singleton?.GetBaseControl();
                MenuBar menuBar = FindMainMenuBar(baseControl);
                Node parent = menuBar?.GetParent();
                if (parent == null) return false;

                parent.AddChild(control);
                parent.MoveChild(control, Math.Min(menuBar.GetIndex() + 1, parent.GetChildCount() - 1));
                return true;
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[MDX Preview] 插入主菜单栏失败: {ex.Message}");
                return false;
            }
        }

        private static MenuBar FindMainMenuBar(Node root)
        {
            if (root == null) return null;
            var node = root.FindChild("*MenuBar*", recursive: true, owned: false);
            return node as MenuBar;
        }
    }
}