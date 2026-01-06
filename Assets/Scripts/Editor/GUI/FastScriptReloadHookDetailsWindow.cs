using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Hook详细信息窗口 - Timeline布局
    /// 显示热重载Hook的历史事件时间线
    /// </summary>
    public class FastScriptReloadHookDetailsWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private static readonly List<ReloadEvent> EVENT_HISTORY = new();
        private static readonly object EVENT_LOCK = new();
        
        // 折叠状态：事件索引 -> 成员名 -> 是否展开
        private readonly Dictionary<int, Dictionary<string, bool>> _expandedErrors = new();

        private GUIStyle _titleStyle;

        private GUIStyle _eventTitleStyle;

        private GUIStyle _methodStyle;

        private GUIStyle _errorStyle;

        private GUIStyle _timeStyle;

        private Texture2D _successIcon;

        private bool _stylesInitialized;

        /// <summary>
        /// 重载事件
        /// </summary>
        private class ReloadEvent
        {
            public DateTime Timestamp { get; set; }
            public List<string> HookedMembers { get; set; } = new List<string>(); // Hook的成员（方法、字段、属性等）
            public Dictionary<string, string> MemberErrors { get; set; } = new Dictionary<string, string>(); // 成员错误信息（成员名 -> 错误信息）
            public bool IsCompleted { get; set; } = false; // 标记事件是否已完成
            public bool IsCompilationFailed { get; set; } = false; // 标记是否为编译失败事件
            public string CompilationError { get; set; } = null; // 编译错误信息
        }

        // 当前正在构建的事件
        private static ReloadEvent _currentEvent;

        /// <summary>
        /// 打开窗口
        /// </summary>
        [MenuItem("Window/Fast Script Reload/Hook Details")]
        public static void ShowWindow()
        {
            var window = GetWindow<FastScriptReloadHookDetailsWindow>("Timeline");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        /// <summary>
        /// 刷新已打开的窗口（如果窗口已打开）
        /// 只刷新已打开的窗口，不自动打开窗口
        /// </summary>
        private static void RepaintIfOpen()
        {
            EditorApplication.delayCall += () =>
            {
                var windows = Resources.FindObjectsOfTypeAll<FastScriptReloadHookDetailsWindow>();
                if (windows == null)
                {
                    return;
                }

                // 检查窗口是否真的可见（没有被销毁）
                foreach (var window in windows)
                {
                    if (window != null && !window.Equals(null))
                    {
                        window.Repaint();
                        break; // 只需要刷新一个实例
                    }
                }
            };
        }

        /// <summary>
        /// 通知单个成员Hook完成（由Hook系统调用）
        /// 成员包括：方法、字段、属性等
        /// </summary>
        /// <param name="memberName">成员名（方法名、字段名、属性名等）</param>
        /// <param name="success">是否成功</param>
        /// <param name="errorMessage">错误信息（失败时提供）</param>
        public static void NotifyMemberHooked(string memberName, bool success, string errorMessage = null)
        {
            lock (EVENT_LOCK)
            {
                // 如果当前没有正在构建的事件，创建一个新事件
                if (_currentEvent == null || _currentEvent.IsCompleted)
                {
                    _currentEvent = new ReloadEvent
                    {
                        Timestamp = DateTime.Now,
                        HookedMembers = new List<string>(),
                        MemberErrors = new Dictionary<string, string>(),
                        IsCompleted = false
                    };
                    // 将新事件插入到历史记录的开头
                    EVENT_HISTORY.Insert(0, _currentEvent);
                }

                // 无论成功失败，都添加到成员列表
                if (!string.IsNullOrEmpty(memberName))
                {
                    if (!_currentEvent.HookedMembers.Contains(memberName))
                    {
                        _currentEvent.HookedMembers.Add(memberName);
                    }
                    
                    // 如果有错误信息，记录错误
                    if (!success && !string.IsNullOrEmpty(errorMessage))
                    {
                        _currentEvent.MemberErrors[memberName] = errorMessage;
                    }
                    else if (success && _currentEvent.MemberErrors.ContainsKey(memberName))
                    {
                        // 如果之前有错误但现在成功了，移除错误信息
                        _currentEvent.MemberErrors.Remove(memberName);
                    }
                }

                // 通知窗口刷新
                RepaintIfOpen();
            }
        }

        /// <summary>
        /// 通知编译失败
        /// </summary>
        /// <param name="errorMessage">编译错误信息</param>
        public static void NotifyCompilationFailed(string errorMessage)
        {
            lock (EVENT_LOCK)
            {
                // 如果当前有正在构建的事件，标记为编译失败
                if (_currentEvent != null && !_currentEvent.IsCompleted)
                {
                    _currentEvent.IsCompilationFailed = true;
                    _currentEvent.CompilationError = errorMessage;
                    _currentEvent.IsCompleted = true;
                    _currentEvent = null; // 清空当前事件
                }
                else
                {
                    // 创建一个新的编译失败事件
                    var reloadEvent = new ReloadEvent
                    {
                        Timestamp = DateTime.Now,
                        HookedMembers = new List<string>(),
                        MemberErrors = new Dictionary<string, string>(),
                        IsCompleted = true,
                        IsCompilationFailed = true,
                        CompilationError = errorMessage
                    };
                    EVENT_HISTORY.Insert(0, reloadEvent);
                }

                // 通知窗口刷新
                RepaintIfOpen();
            }
        }

        /// <summary>
        /// 添加重载完成事件（由Hook系统调用，所有Hook完成后调用）
        /// </summary>
        /// <param name="hookedMembers">Hook的成员列表（方法、字段、属性等），如果为null则使用当前事件中已记录的成员</param>
        public static void AddReloadFinishedEvent(List<string> hookedMembers)
        {
            lock (EVENT_LOCK)
            {
                // 如果当前有正在构建的事件，标记为完成并使用提供的成员列表
                if (_currentEvent != null && !_currentEvent.IsCompleted)
                {
                    _currentEvent.IsCompleted = true;
                    // 如果提供了成员列表，使用提供的列表（可能更完整）
                    if (hookedMembers != null && hookedMembers.Count > 0)
                    {
                        _currentEvent.HookedMembers = new List<string>(hookedMembers);
                    }
                    
                    // 如果没有成员，移除这个事件
                    if (_currentEvent.HookedMembers == null || _currentEvent.HookedMembers.Count == 0)
                    {
                        EVENT_HISTORY.Remove(_currentEvent);
                    }
                    
                    _currentEvent = null; // 清空当前事件
                }
                else
                {
                    // 如果没有正在构建的事件，创建一个新事件（可能没有调用NotifyMemberHooked的情况）
                    // 只有当有成员时才创建事件
                    if (hookedMembers != null && hookedMembers.Count > 0)
                    {
                        var reloadEvent = new ReloadEvent
                        {
                            Timestamp = DateTime.Now,
                            HookedMembers = new List<string>(hookedMembers),
                            MemberErrors = new Dictionary<string, string>(),
                            IsCompleted = true
                        };
                        
                        EVENT_HISTORY.Insert(0, reloadEvent); // 插入到开头，最新的在前
                    }
                }
                
                // 限制历史记录数量，避免内存占用过大
                if (EVENT_HISTORY.Count > 100)
                {
                    EVENT_HISTORY.RemoveRange(100, EVENT_HISTORY.Count - 100);
                }
                
                // 通知窗口刷新
                RepaintIfOpen();
            }
        }

        /// <summary>
        /// 通知开始Hook（由Hook系统调用，开始新的Hook批次时调用）
        /// 清理之前的未完成事件（如果有）
        /// </summary>
        public static void NotifyHookStart()
        {
            lock (EVENT_LOCK)
            {
                // 如果当前有未完成的事件，先标记为完成
                if (_currentEvent != null && !_currentEvent.IsCompleted)
                {
                    _currentEvent.IsCompleted = true;
                    _currentEvent = null;
                }
            }
        }

        /// <summary>
        /// 清除所有事件历史（内部方法，需要实例调用以清理折叠状态）
        /// </summary>
        private void ClearEventsInternal()
        {
            lock (EVENT_LOCK)
            {
                EVENT_HISTORY.Clear();
                _currentEvent = null;
            }

            // 清理折叠状态
            _expandedErrors.Clear();
        }

        private void OnEnable()
        {
            // 创建成功图标（绿色勾选）
            if (_successIcon == null)
            {
                _successIcon = CreateSuccessIcon();
            }
        }

        private Texture2D CreateSuccessIcon()
        {
            var icon = new Texture2D(16, 16);
            var pixels = new Color[16 * 16];
            
            // 绘制绿色圆形背景
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    float centerX = 7.5f;
                    float centerY = 7.5f;
                    float dist = Mathf.Sqrt((x - centerX) * (x - centerX) + (y - centerY) * (y - centerY));
                    
                    if (dist < 7)
                    {
                        pixels[y * 16 + x] = new Color(0.3f, 1f, 0.3f); // 绿色
                    }
                    else
                    {
                        pixels[y * 16 + x] = Color.clear;
                    }
                }
            }
            
            // 绘制白色勾选标记
            for (int i = 0; i < 16; i++)
            {
                // 勾选的上半部分
                if (i >= 4 && i <= 7)
                {
                    int y = 6 + (i - 4);
                    int x = 4 + (i - 4);
                    if (x < 16 && y < 16)
                        pixels[y * 16 + x] = Color.white;
                }
                // 勾选的下半部分
                if (i >= 5 && i <= 9)
                {
                    int y = 7 + (i - 5);
                    int x = 8 - (i - 5);
                    if (x >= 0 && x < 16 && y < 16)
                        pixels[y * 16 + x] = Color.white;
                }
            }
            
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }

        private void InitStyles()
        {
            if (_stylesInitialized)
                return;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };

            _eventTitleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
                alignment = TextAnchor.MiddleLeft
            };

            _methodStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) },
                padding = new RectOffset(20, 0, 1, 1),
                wordWrap = false
            };

            _errorStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                normal = { textColor = new Color(1f, 0.5f, 0.5f) }, // 红色
                padding = new RectOffset(40, 5, 2, 2),
                wordWrap = true
            };

            _timeStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                alignment = TextAnchor.MiddleRight
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            // 标题栏
            DrawHeader();

            GUILayout.Space(5);

            // 事件时间线
            DrawTimeline();
        }

        private void DrawHeader()
        {
            GUILayout.BeginHorizontal();
            
            // 标题
            GUILayout.Label("Timeline", _titleStyle);
            
            GUILayout.FlexibleSpace();
            
            // Clear 按钮
            if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(20)))
            {
                // 使用延迟调用来避免 GUI 状态不一致
                EditorApplication.delayCall += () =>
                {
                    if (EditorUtility.DisplayDialog("Clear Timeline", 
                        "Are you sure you want to clear all timeline events?", 
                        "Yes", "Cancel"))
                    {
                        ClearEventsInternal();
                        Repaint();
                    }
                };
            }
            
            GUILayout.EndHorizontal();
        }

        private void DrawTimeline()
        {
            List<ReloadEvent> events;
            lock (EVENT_LOCK)
            {
                // 过滤掉没有成员且不是编译失败的事件
                events = EVENT_HISTORY.Where(e => 
                    (e.HookedMembers != null && e.HookedMembers.Count > 0) || 
                    e.IsCompilationFailed).ToList();
            }

            if (events.Count == 0)
            {
                GUILayout.FlexibleSpace();
                var emptyStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                    fontSize = 14
                };
                GUILayout.Label("No reload events yet", emptyStyle);
                GUILayout.FlexibleSpace();
                return;
            }

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);

            for (int i = 0; i < events.Count; i++)
            {
                DrawEventEntry(events[i], i);
                GUILayout.Space(8);
            }

            GUILayout.EndScrollView();
        }

        private void DrawEventEntry(ReloadEvent reloadEvent, int eventIndex)
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Space(4);

            // 事件标题行：图标 + 标题文本 + 时间
            GUILayout.BeginHorizontal();
            
            // 根据事件类型显示不同的图标和文本
            if (reloadEvent.IsCompilationFailed)
            {
                // 编译失败：红色 X 图标
                var errorTitleStyle = new GUIStyle(_eventTitleStyle)
                {
                    normal = { textColor = new Color(1f, 0.3f, 0.3f) }
                };
                GUILayout.Label("✗", errorTitleStyle, GUILayout.Width(16));
                GUILayout.Space(4);
                GUILayout.Label("Compilation failed", errorTitleStyle);
            }
            else
            {
                // Hook成功：绿色 ✓ 图标
                if (_successIcon != null)
                {
                    GUILayout.Label(new GUIContent(_successIcon), GUILayout.Width(16), GUILayout.Height(16));
                }
                else
                {
                    GUILayout.Label("✓", _eventTitleStyle, GUILayout.Width(16));
                }
                GUILayout.Space(4);
                GUILayout.Label("Reload finished", _eventTitleStyle);
            }
            
            GUILayout.FlexibleSpace();
            
            // 时间戳
            string timeText = reloadEvent.Timestamp.ToString("HH:mm:ss");
            GUILayout.Label(timeText, _timeStyle);
            
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            
            // 如果是编译失败事件，显示编译错误信息
            if (reloadEvent.IsCompilationFailed && !string.IsNullOrEmpty(reloadEvent.CompilationError))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(-10);
                
                // 错误信息标签（红色）
                var errorLabelStyle = new GUIStyle(_methodStyle)
                {
                    normal = { textColor = new Color(1f, 0.3f, 0.3f) }
                };
                GUILayout.Label("Compilation Error", errorLabelStyle);
                
                GUILayout.EndHorizontal();
                
                // 直接显示错误详情（不折叠）
                GUILayout.BeginHorizontal();
                GUILayout.Space(20); // 缩进
                GUILayout.BeginVertical();
                
                // 显示错误信息（使用文本框以便复制）
                var errorTextStyle = new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true,
                    fontSize = 14,
                    normal = { textColor = new Color(1f, 0.5f, 0.5f) }
                };
                GUILayout.TextArea(reloadEvent.CompilationError, errorTextStyle);
                
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                
                GUILayout.Space(4);
            }

            // 成员列表（方法、字段、属性等）
            if (reloadEvent.HookedMembers != null && reloadEvent.HookedMembers.Count > 0)
            {
                foreach (var member in reloadEvent.HookedMembers)
                {
                    // 检查是否有错误
                    bool hasError = reloadEvent.MemberErrors != null && reloadEvent.MemberErrors.ContainsKey(member);
                    
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(-10); // 缩进
                    
                    // 如果有错误，显示折叠按钮
                    if (hasError)
                    {
                        // 初始化折叠状态（默认展开）
                        if (!_expandedErrors.ContainsKey(eventIndex))
                        {
                            _expandedErrors[eventIndex] = new Dictionary<string, bool>();
                        }
                        if (!_expandedErrors[eventIndex].ContainsKey(member))
                        {
                            _expandedErrors[eventIndex][member] = true; // 默认展开
                        }
                        
                        // 折叠/展开按钮
                        bool isExpanded = _expandedErrors[eventIndex][member];
                        string foldoutText = isExpanded ? "▼" : "▶";
                        var foldoutStyle = new GUIStyle(EditorStyles.label)
                        {
                            fontSize = 14,
                            normal = { textColor = new Color(0.7f, 0.7f, 0.7f) },
                            alignment = TextAnchor.MiddleLeft
                        };
                        
                        if (GUILayout.Button(foldoutText, foldoutStyle, GUILayout.Width(15), GUILayout.Height(15)))
                        {
                            _expandedErrors[eventIndex][member] = !isExpanded;
                        }
                    }
                    else
                    {
                        GUILayout.Space(15); // 没有错误时保持对齐
                    }
                    
                    // 成员名（如果有错误，显示为红色）
                    var memberDisplayStyle = hasError 
                        ? new GUIStyle(_methodStyle) { normal = { textColor = new Color(1f, 0.5f, 0.5f) } }
                        : _methodStyle;
                    GUILayout.Label($"• {member}", memberDisplayStyle);
                    
                    GUILayout.EndHorizontal();
                    
                    // 如果展开，显示错误信息
                    if (hasError && _expandedErrors[eventIndex][member])
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(40); // 缩进到错误信息位置
                        GUILayout.Label(reloadEvent.MemberErrors[member], _errorStyle);
                        GUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                var noMembersStyle = new GUIStyle(_methodStyle)
                {
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
                };
                GUILayout.Label("• No members hooked", noMembersStyle);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            GUILayout.EndVertical();
        }
    }
}
