using System;
using UnityEditor;
using UnityEngine;
using UnityEditor.Overlays;
using UnityEngine.UIElements;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Hot Reload 状态枚举
    /// </summary>
    public enum HotReloadState
    {
        Idle, // 空闲
        Initializing, // 初始化中
        WaitingForChanges, // 等待代码改变
        Hooking, // Hook中
        Completed, // 完成
        CompilationFailed // 编译失败
    }

    /// <summary>
    /// Hot Reload 状态信息
    /// </summary>
    public class HotReloadStateInfo
    {
        public HotReloadState State { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    /// <summary>
    /// Scene视图中的Hot Reload信息面板 (Unity 2021.2+)
    /// 显示当前Hook的方法和字段信息
    /// </summary>
    [Overlay(typeof(SceneView), "Hot Reload", true)]
    internal class FastScriptReloadSceneOverlay : Overlay
    {
        // 静态状态信息，供外部调用
        private static HotReloadStateInfo _currentStateInfo = new (){ State = HotReloadState.Idle };
        private static readonly object STATE_LOCK = new object();

        // 动画相关
        private int _spinnerFrame = 0;
        private static readonly string[] SPINNER_FRAMES = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        // UI 元素
        private VisualElement _root;
        private Label _statusLabel;
        private Label _hookCountLabel;
        private Button _detailsButton;
        private Label _spinnerElement;
        
        private bool _initialized;

        #region 公共静态接口

        /// <summary>
        /// 通知开始初始化
        /// </summary>
        public static void NotifyInitializationStart()
        {
            lock (STATE_LOCK)
            {
                _currentStateInfo.State = HotReloadState.Initializing;
                _currentStateInfo.LastUpdateTime = DateTime.Now;
            }
        }

        /// <summary>
        /// 通知初始化完成，进入等待代码改变状态
        /// </summary>
        public static void NotifyInitializationComplete()
        {
            lock (STATE_LOCK)
            {
                if (_currentStateInfo.State == HotReloadState.Initializing)
                {
                    _currentStateInfo.State = HotReloadState.WaitingForChanges;
                    _currentStateInfo.LastUpdateTime = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// 通知开始Hook
        /// </summary>
        public static void NotifyHookStart()
        {
            lock (STATE_LOCK)
            {
                _currentStateInfo.State = HotReloadState.Hooking;
                _currentStateInfo.LastUpdateTime = DateTime.Now;
            }

            // 通知DetailsWindow开始Hook
            FastScriptReloadHookDetailsWindow.NotifyHookStart();
        }

        /// <summary>
        /// 通知Hook完成（所有Hook执行完毕）
        /// </summary>
        public static void NotifyHookComplete()
        {
            lock (STATE_LOCK)
            {
                if (_currentStateInfo.State == HotReloadState.Hooking)
                {
                    _currentStateInfo.State = HotReloadState.Completed;
                    _currentStateInfo.LastUpdateTime = DateTime.Now;
                }
            }

            // 通知DetailsWindow Hook完成
            FastScriptReloadHookDetailsWindow.AddReloadFinishedEvent(null);
        }

        /// <summary>
        /// 通知编译失败
        /// </summary>
        /// <param name="errorMessage">编译错误信息</param>
        public static void NotifyCompilationFailed(string errorMessage)
        {
            lock (STATE_LOCK)
            {
                _currentStateInfo.State = HotReloadState.CompilationFailed;
                _currentStateInfo.LastUpdateTime = DateTime.Now;
            }

            // 通知DetailsWindow编译失败
            FastScriptReloadHookDetailsWindow.NotifyCompilationFailed(errorMessage);
        }

        /// <summary>
        /// 重置状态到空闲
        /// </summary>
        public static void ResetState()
        {
            lock (STATE_LOCK)
            {
                _currentStateInfo = new HotReloadStateInfo { State = HotReloadState.Idle };
            }
        }

        /// <summary>
        /// 获取当前状态信息（供外部窗口使用）
        /// </summary>
        public static HotReloadStateInfo GetCurrentStateInfo()
        {
            lock (STATE_LOCK)
            {
                return new HotReloadStateInfo
                {
                    State = _currentStateInfo.State,
                    LastUpdateTime = _currentStateInfo.LastUpdateTime
                };
            }
        }

        #endregion

        public override VisualElement CreatePanelContent()
        {
            _root = new VisualElement { name = "Fast Script Reload Info" };
            _root.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.9f);
            _root.style.paddingLeft = 8;
            _root.style.paddingRight = 8;
            _root.style.paddingTop = 6;
            _root.style.paddingBottom = 6;
            _root.style.borderBottomLeftRadius = 4;
            _root.style.borderBottomRightRadius = 4;
            _root.style.borderTopLeftRadius = 4;
            _root.style.borderTopRightRadius = 4;
            _root.style.minWidth = 200;

            // 流程状态标签容器（替换原来的_statusLabel）
            var stateContainer = new VisualElement();
            stateContainer.style.flexDirection = FlexDirection.Row;
            stateContainer.style.marginBottom = 4;

            _spinnerElement = new Label { text = "" };
            _spinnerElement.style.fontSize = 12;
            _spinnerElement.style.marginRight = 5;
            _spinnerElement.style.display = DisplayStyle.None;
            _spinnerElement.style.unityFontStyleAndWeight = FontStyle.Bold;

            _statusLabel = new Label { text = "Hot Reload Idle" };
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _statusLabel.style.fontSize = 12;
            _statusLabel.style.color = Color.gray;

            stateContainer.Add(_spinnerElement);
            stateContainer.Add(_statusLabel);

            // Hook数量统计
            _hookCountLabel = new Label { text = "" };
            _hookCountLabel.style.fontSize = 10;
            _hookCountLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            _hookCountLabel.style.marginBottom = 2;

            // 打开详细信息窗口按钮
            _detailsButton = new Button(() => FastScriptReloadHookDetailsWindow.ShowWindow()) { text = "Details" };
            _detailsButton.style.marginTop = 4;
            _detailsButton.style.fontSize = 10;
            _detailsButton.style.display = DisplayStyle.None;

            _root.Add(stateContainer);
            _root.Add(_hookCountLabel);
            _root.Add(_detailsButton);

            _initialized = true;
            UpdateContent();

            return _root;
        }

        public FastScriptReloadSceneOverlay()
        {
            EditorApplication.update += OnEditorUpdate;
            // 初始化时检查AutoReload是否启用
            UpdateVisibility();
        }

        ~FastScriptReloadSceneOverlay()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void UpdateVisibility()
        {
            // 根据EnableAutoReloadForChangedFiles配置决定是否显示Overlay
            bool shouldDisplay = (bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles
                .GetEditorPersistedValueOrDefault();
            if (displayed != shouldDisplay)
            {
                displayed = shouldDisplay;
            }
        }

        private void OnEditorUpdate()
        {
            UpdateVisibility();

            if (_initialized && displayed)
            {
                UpdateContent();
            }
        }


        private void UpdateContent()
        {
            if (!_initialized || _root == null)
                return;

            HotReloadStateInfo stateInfo;
            lock (STATE_LOCK)
            {
                // 复制状态信息，避免锁定太久
                stateInfo = new HotReloadStateInfo
                {
                    State = _currentStateInfo.State,
                    LastUpdateTime = _currentStateInfo.LastUpdateTime
                };
            }

            // 根据状态更新UI
            switch (stateInfo.State)
            {
                case HotReloadState.Idle:
                    UpdateIdleState();
                    break;

                case HotReloadState.Initializing:
                    UpdateInitializingState();
                    break;

                case HotReloadState.WaitingForChanges:
                    UpdateWaitingForChangesState();
                    break;

                case HotReloadState.Hooking:
                    UpdateHookingState(stateInfo);
                    break;

                case HotReloadState.Completed:
                    UpdateCompletedState(stateInfo);
                    break;

                case HotReloadState.CompilationFailed:
                    UpdateCompilationFailedState();
                    break;
            }
        }

        private void UpdateCompilationFailedState()
        {
            // 隐藏转圈动画
            _spinnerElement.style.display = DisplayStyle.None;

            _statusLabel.text = "✗ Compilation Failed";
            _statusLabel.style.color = new Color(1f, 0.3f, 0.3f); // 红色

            _hookCountLabel.text = "";

            // 显示"查看详情"按钮
            _detailsButton.style.display = DisplayStyle.Flex;

            _root.style.backgroundColor = new Color(0.25f, 0.15f, 0.15f, 0.9f); // 深红色背景
        }

        private void UpdateIdleState()
        {
            _spinnerElement.style.display = DisplayStyle.None;
            _statusLabel.text = "Hot Reload Idle";
            _statusLabel.style.color = Color.gray;
            _hookCountLabel.text = "";
            _detailsButton.style.display = DisplayStyle.None;
            _root.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.9f);
        }

        private void UpdateInitializingState()
        {
            // 显示转圈动画
            _spinnerElement.style.display = DisplayStyle.Flex;
            _spinnerElement.text = SPINNER_FRAMES[_spinnerFrame % SPINNER_FRAMES.Length];
            _spinnerElement.style.color = new Color(0.3f, 0.8f, 1f); // 蓝色
            _spinnerFrame++;

            _statusLabel.text = "Initializing...";
            _statusLabel.style.color = new Color(0.3f, 0.8f, 1f);

            _hookCountLabel.text = "";
            _detailsButton.style.display = DisplayStyle.None;
            _root.style.backgroundColor = new Color(0.15f, 0.2f, 0.25f, 0.9f); // 深蓝色背景
        }

        private void UpdateWaitingForChangesState()
        {
            // 隐藏转圈动画
            _spinnerElement.style.display = DisplayStyle.None;

            _statusLabel.text = "Waiting for changes...";
            _statusLabel.style.color = new Color(0.3f, 1f, 0.3f); // 绿色

            _hookCountLabel.text = "Ready to hot reload";
            _detailsButton.style.display = DisplayStyle.None;
            _root.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.9f); // 深灰色背景
        }

        private void UpdateHookingState(HotReloadStateInfo stateInfo)
        {
            // 显示转圈动画
            _spinnerElement.style.display = DisplayStyle.Flex;
            _spinnerElement.text = SPINNER_FRAMES[_spinnerFrame % SPINNER_FRAMES.Length];
            _spinnerElement.style.color = new Color(1f, 0.8f, 0.3f); // 橙色
            _spinnerFrame++;

            _statusLabel.text = "Hooking...";
            _statusLabel.style.color = new Color(1f, 0.8f, 0.3f);

            _hookCountLabel.text = "Processing...";

            _detailsButton.style.display = DisplayStyle.None;
            _root.style.backgroundColor = new Color(0.25f, 0.2f, 0.15f, 0.9f); // 深橙色背景
        }

        private void UpdateCompletedState(HotReloadStateInfo stateInfo)
        {
            // 隐藏转圈动画
            _spinnerElement.style.display = DisplayStyle.None;

            _statusLabel.text = "✓ Hook Completed";
            _statusLabel.style.color = new Color(0.3f, 1f, 0.3f); // 绿色

            _hookCountLabel.text = "";

            // 显示"查看详情"按钮
            _detailsButton.style.display = DisplayStyle.Flex;

            _root.style.backgroundColor = new Color(0.15f, 0.25f, 0.15f, 0.9f); // 深绿色背景
        }
    }
}