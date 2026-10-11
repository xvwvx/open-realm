using System;
using Godot;
using MdxPreview.Runtime;

namespace MdxPreview.EditorUi
{
    /// <summary>
    /// 预览视口容器：内部是一个拥有独立 <see cref="World3D"/> 的 <see cref="SubViewport"/>，
    /// 3D 预览（<see cref="MdxPreviewView"/>）挂在该 SubViewport 下。
    ///
    /// 鼠标操作在这里被截获并通过 <c>AcceptEvent()</c> 吃掉，避免编辑器再处理同一事件：
    ///   左键拖拽 = 轨道旋转，中键/右键拖拽 = 平移，滚轮 = 缩放。
    /// </summary>
    [Tool]
    public partial class MdxPreviewViewport : SubViewportContainer
    {
        SubViewport _subViewport;
        bool _orbiting;
        bool _panning;

        /// <summary>3D 预览根节点（由 <see cref="CreateView"/> 创建）。</summary>
        public MdxPreviewView View { get; private set; }

        public SubViewport SubViewport3D => _subViewport;

        /// <summary>把拖入的文件路径交给 Dock 处理（返回 true 表示已接收）。</summary>
        public Func<string[], bool> DropHandler { get; set; }

        public MdxPreviewViewport()
        {
            Name = "PreviewViewport";
            MouseFilter = MouseFilterEnum.Stop;
            FocusMode = FocusModeEnum.Click;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(0f, 240f);
            TooltipText = "左键拖拽=旋转视角　中键/右键拖拽=平移　滚轮=缩放";
            // 注意：Stretch 必须在已经有 SubViewport 子节点之后才打开，
            // 否则容器在收到 NOTIFICATION_RESIZED 时会因为找不到子节点而报错。
        }

        /// <summary>创建 3D 预览（此时还没进入场景树，实际的 _Ready 在挂载后执行）。</summary>
        public MdxPreviewView CreateView(MdxAssetLibrary assets, MdxPreviewSettings settings)
        {
            View = new MdxPreviewView(assets, settings);
            // 容器已经就绪的情况下（先挂树后创建）直接挂上去。
            AttachView();
            return View;
        }

        void AttachView()
        {
            if (View == null || _subViewport == null) return;
            if (View.GetParent() != null) return;
            _subViewport.AddChild(View);
        }

        public override void _Ready()
        {
            // SubViewport 与它自己的 World3D 都在进入场景树之前配置好：
            // 挂到树上之后再替换 World3D 会触发渲染层的 scenario 切换错误。
            _subViewport = new SubViewport
            {
                Name = "PreviewSubViewport",
                OwnWorld3D = true,
                World3D = new World3D(),
                GuiDisableInput = true,
                HandleInputLocally = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = false,
                Size = new Vector2I(
                    Math.Max(2, Mathf.RoundToInt(Size.X)),
                    Math.Max(2, Mathf.RoundToInt(Size.Y))),
            };
            AddChild(_subViewport);

            // 子节点就位后再打开 Stretch，让 SubViewport 跟随容器大小。
            // （注意：Stretch 打开后不能再直接改 SubViewport.Size，否则 Godot 会警告。）
            Stretch = true;

            AttachView();
        }

        // ---- 鼠标交互 -------------------------------------------------------------------

        public override void _GuiInput(InputEvent @event)
        {
            if (View == null) return;

            switch (@event)
            {
                case InputEventMouseButton button:
                    HandleMouseButton(button);
                    break;
                case InputEventMouseMotion motion when _orbiting || _panning:
                    if (_orbiting) View.Orbit(motion.Relative);
                    else View.Pan(motion.Relative);
                    AcceptEvent();
                    break;
            }
        }

        void HandleMouseButton(InputEventMouseButton button)
        {
            switch (button.ButtonIndex)
            {
                case MouseButton.WheelUp:
                    if (!button.Pressed) return;
                    View.Zoom(1f);
                    AcceptEvent();
                    break;
                case MouseButton.WheelDown:
                    if (!button.Pressed) return;
                    View.Zoom(-1f);
                    AcceptEvent();
                    break;
                case MouseButton.Left:
                    _orbiting = button.Pressed;
                    if (button.Pressed) GrabFocus();
                    AcceptEvent();
                    break;
                case MouseButton.Middle:
                case MouseButton.Right:
                    _panning = button.Pressed;
                    AcceptEvent();
                    break;
            }
        }

        /// <summary>鼠标离开时结束拖拽，避免松开事件丢在外面导致状态卡住。</summary>
        public override void _Notification(int what)
        {
            if (what == NotificationMouseExit)
            {
                _orbiting = false;
                _panning = false;
            }
        }

        // ---- 拖拽模型文件 -----------------------------------------------------------------

        public override bool _CanDropData(Vector2 atPosition, Variant data) => DropHandler != null;

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (DropHandler == null) return;
            string[] paths = MdxEditorUtil.ExtractDropPaths(data);
            if (paths.Length > 0) DropHandler(paths);
        }
    }
}
