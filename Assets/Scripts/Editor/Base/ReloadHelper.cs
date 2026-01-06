using System;
using System.Collections.Generic;
using System.IO;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 热重载辅助类 - 统一的热重载管理器
    /// - ReloadHelper.cs: 公共状态和初始化
    /// - ReloadHelper.Compile.cs: Roslyn编译和程序集差异比较
    /// - ReloadHelper.IL.cs: Mono.Cecil IL修改
    /// - ReloadHelper.Hook.cs: Hook应用和执行
    /// </summary>
    public static partial class ReloadHelper
    {
        private static string _assemblySavePath;

        public static string AssemblyPath
        {
            get
            {
                if (_assemblySavePath == null)
                {
                    // 获取工程名
                    var projectName = Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
                    if (string.IsNullOrEmpty(projectName))
                    {
                        // 如果 productName 为空，从项目路径获取
                        var projectPath = Application.dataPath;
                        if (!string.IsNullOrEmpty(projectPath))
                        {
                            var dirInfo = new DirectoryInfo(projectPath);
                            projectName = dirInfo.Parent?.Name ?? "UnityProject";
                        }
                        else
                        {
                            projectName = "UnityProject";
                        }
                    }

                    // 创建保存目录：%LOCALAPPDATA%\Temp\{工程名}
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _assemblySavePath = Path.Combine(localAppData, "Temp", "FastScriptReloadTemp", projectName);
                }

                if (!Directory.Exists(_assemblySavePath))
                {
                    Directory.CreateDirectory(_assemblySavePath);
                }

                return _assemblySavePath;
            }
        }

        public static readonly string ASSEMBLY_OUTPUT_PATH = Path.Combine(AssemblyPath, "Output");
        
        public static readonly string HOOK_TYPE_INFO_CACHE_PATH = Path.Combine(AssemblyPath, "HookTypeCache.json");

        /// <summary>
        /// 修改过的类型缓存
        /// </summary>
        public static Dictionary<string, HookTypeInfo> HookTypeInfoCache = new();
        
        /// <summary>
        /// 初始化
        /// </summary>
        [InitializeOnLoadMethod]
        public static async void Init()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            FastScriptReloadSceneOverlay.NotifyInitializationStart();
            
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (HookTypeInfoCache.Count == 0)
                {
                    return;
                }

                File.Delete(HOOK_TYPE_INFO_CACHE_PATH);
                File.WriteAllText(HOOK_TYPE_INFO_CACHE_PATH, JsonConvert.SerializeObject(HookTypeInfoCache));
            };

            CompilationPipeline.compilationStarted += o =>
            {
                HookTypeInfoCache.Clear();
                Directory.Delete(AssemblyPath, true);
            };
            
            await TypeInfoHelper.Initialize();
            
            RebuildHooks();
            
            FastScriptReloadSceneOverlay.NotifyInitializationComplete();
        }
    }
}