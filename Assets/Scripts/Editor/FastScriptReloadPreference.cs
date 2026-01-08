using System;
using System.Collections.Generic;
using FastScriptReload.Runtime;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVRTools.Editor.Common.WelcomeScreen;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.PreferenceDefinition;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    public class FastScriptReloadPreference : ProductPreferenceBase
    {
        public const string BuildSymbol_DetailedDebugLogging = "ImmersiveVrTools_DebugEnabled";
        
        public const string ProductName = "Fast Script Reload";
        private static string[] ProductKeywords = new[] { "productivity", "tools" };
        
        /// <summary>Used to know when file watchers have changed from project window contextual menu (so when to update file watchers)</summary>
        public static bool FileWatcherSetupEntriesChanged = false;

        // SessionState 键名
        private const string SessionKey_HotReloadEnabled = "FastScriptReload.IsHotReloadEnabledThisSession";

        /// <summary>
        /// 检查当前会话的 HotReload 是否启用
        /// 默认为 false，需要手动开启
        /// Unity 编辑器重启后自动重置为 false
        /// </summary>
        public static bool EnableAutoReloadForChangedFiles
        {
            get => SessionState.GetBool(SessionKey_HotReloadEnabled, false);
            set
            {
                bool oldValue = EnableAutoReloadForChangedFiles;
                if (oldValue != value)
                {
                    SessionState.SetBool(SessionKey_HotReloadEnabled, value);
                    OnHotReloadStateChanged(value);
                }
            }
        }

        /// <summary>
        /// 当 HotReload 状态变化时的处理逻辑
        /// </summary>
        /// <param name="isEnabled">是否启用</param>
        private static void OnHotReloadStateChanged(bool isEnabled)
        {
            // 触发初始化或重置
            if (isEnabled)
            {
                EditorApplication.delayCall += ReloadHelper.Init;
            }
            else
            {
                ReloadHelper.Dispose();
                FastScriptReloadSceneOverlay.ResetState();
            }
        }

        public static readonly IntProjectEditorPreferenceDefinition BatchScriptChangesAndReloadEveryNSeconds = new IntProjectEditorPreferenceDefinition(
            "批处理脚本变更，每 N 秒重载一次", "BatchScriptChangesAndReloadEveryNSeconds", 1);
    
        public static readonly StringListProjectEditorPreferenceDefinition FilesExcludedFromHotReload = new StringListProjectEditorPreferenceDefinition(
            "排除在热重载之外的文件", "FilesExcludedFromHotReload", new List<string> {}, isReadonly: true);
        
        public static readonly ToggleProjectEditorPreferenceDefinition StopShowingAutoReloadEnabledDialogBox = new ToggleProjectEditorPreferenceDefinition(
            "停止显示资源/脚本自动重载已启用的警告", "StopShowingAutoReloadEnabledDialogBox", false);
        public static readonly ToggleProjectEditorPreferenceDefinition EnableDetailedDebugLogging = new ToggleProjectEditorPreferenceDefinition(
            "启用详细调试日志", "EnableDetailedDebugLogging", false,
            (object newValue, object oldValue) =>
            {
                BuildDefineSymbolManager.SetBuildDefineSymbolState(BuildSymbol_DetailedDebugLogging, (bool)newValue);
            },
            (value) =>
            {
                BuildDefineSymbolManager.SetBuildDefineSymbolState(BuildSymbol_DetailedDebugLogging, (bool)value);
            }
        );
        
        public static readonly ToggleProjectEditorPreferenceDefinition IsVisualHotReloadIndicationShownInProjectWindow = new ToggleProjectEditorPreferenceDefinition(
            "在项目窗口中显示红/绿条以指示文件的热重载状态", "IsVisualHotReloadIndicationShownInProjectWindow", true);
        
        public static readonly ToggleProjectEditorPreferenceDefinition IsForceLockAssembliesViaCode = new ToggleProjectEditorPreferenceDefinition(
            "强制阻止 Play Mode 下的程序集重载", "IsForceLockAssembliesViaCode", false);
        
        public static readonly JsonObjectListProjectEditorPreferenceDefinition<FileWatcherSetupEntry> FileWatcherSetupEntries = new JsonObjectListProjectEditorPreferenceDefinition<FileWatcherSetupEntry>(
            "文件监控器设置", "FileWatcherSetupEntries", new List<string>
            {
                JsonUtility.ToJson(new FileWatcherSetupEntry(FastScriptReloadManager.FileWatcherReplacementTokenForApplicationDataPath, "*.cs", true))
            }, 
            () => new FileWatcherSetupEntry(FastScriptReloadManager.FileWatcherReplacementTokenForApplicationDataPath, "*.cs", true)
        );
        
        [Obsolete("Editor Hot-Reload is now enabled by default. This option is no longer needed.")]
        public static readonly ToggleProjectEditorPreferenceDefinition EnableCustomFileWatcher = new ToggleProjectEditorPreferenceDefinition(
            "(Experimental) Use custom file watchers", "EnableCustomFileWatcher", false);
        
        public static readonly EnumProjectEditorPreferenceDefinition FileWatcherImplementationInUse = new EnumProjectEditorPreferenceDefinition(
            "文件监控器实现方式", "FileWatcherImplementationInUse", FileWatcherImplementation.UnityDefault, typeof(FileWatcherImplementation));

        public static readonly IntProjectEditorPreferenceDefinition TriggerDomainReloadIfOverNDynamicallyLoadedAssembles = new IntProjectEditorPreferenceDefinition(
            "Trigger full domain reload after N hot-reloads (when not in play mode)", "TriggerDomainReloadIfOverNDynamicallyLoadedAssembles", 50);

        public static readonly ToggleProjectEditorPreferenceDefinition WatchOnlySpecified = new ToggleProjectEditorPreferenceDefinition(
            "手动指定监控的文件/文件夹", "WatchOnlySpecified", false);

        public static List<ProjectEditorPreferenceDefinitionBase> PreferenceDefinitions
        {
            get
            {
                var list = new List<ProjectEditorPreferenceDefinitionBase>();
                
                var defaultOption = CreateDefaultShowOptionPreferenceDefinition();
                if (defaultOption != null)
                {
                    list.Add(defaultOption);
                }
                
                list.Add(BatchScriptChangesAndReloadEveryNSeconds);
                list.Add(StopShowingAutoReloadEnabledDialogBox);
                list.Add(FileWatcherSetupEntries);
                list.Add(TriggerDomainReloadIfOverNDynamicallyLoadedAssembles);
                list.Add(IsForceLockAssembliesViaCode);
                
                return list;
            }
        }

        private static bool PrefsLoaded = false;


#if !LiveScriptReload_Enabled
    #if UNITY_2019_1_OR_NEWER
        [SettingsProvider]
        public static SettingsProvider ImpostorsSettings()
        {
            return GenerateProvider(ProductName, ProductKeywords, PreferencesGUI);
        }

    #else
	[PreferenceItem(ProductName)]
    #endif
#endif
        public static void PreferencesGUI()
        {
            if (!PrefsLoaded)
            {
                LoadDefaults(PreferenceDefinitions);
                PrefsLoaded = true;
            }

            RenderGuiCommon(PreferenceDefinitions);
        }

        public enum ShadersMode
        {
            HDRP,
            URP,
            Surface
        }
    }
}

