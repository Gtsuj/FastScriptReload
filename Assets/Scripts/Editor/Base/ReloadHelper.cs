using System;
using System.Collections.Generic;
using System.IO;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 热重载辅助类 - 统一的热重载管理器
    /// - ReloadHelper.cs: 公共状态和初始化
    /// - ReloadHelper.Compile.cs: Roslyn编译
    /// - ReloadHelper.IL.cs: Mono.Cecil IL修改
    /// - ReloadHelper.Diff.cs: 程序集差异比较
    /// - ReloadHelper.Hook.cs: Hook应用和执行
    /// </summary>
    public static partial class ReloadHelper
    {
        public static readonly EmitOptions EMIT_OPTIONS = new (debugInformationFormat:DebugInformationFormat.PortablePdb);

        public static WriterParameters WriterParameters => new()
            { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() };

        public static ReaderParameters ReaderParameters => new()
            { ReadWrite = true, InMemory = true };
        
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

                    if (!Directory.Exists(_assemblySavePath))
                    {
                        Directory.CreateDirectory(_assemblySavePath);
                    }
                }

                return _assemblySavePath;
            }
        }        

        /// <summary>
        /// 修改过的类型缓存
        /// </summary>
        public static Dictionary<string, HookTypeInfo> HookTypeInfoCache = new();

        /// <summary>
        /// 全局缓存已加载的 Wrapper 程序集（按程序集名称索引）
        /// </summary>
        public static Dictionary<string, Assembly> AssemblyCache = new();
        
        /// <summary>
        /// 当前Hook流程中编译的程序集
        /// </summary>
        private static AssemblyDefinition _assemblyDefinition;
        
        /// <summary>
        /// 初始化
        /// </summary>
        [InitializeOnEnterPlayMode]
        public static void Init()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            TypeInfoHelper.Initialize();
        }

        /// <summary>
        /// 清除所有缓存（在一次Reload流程完成后调用）
        /// </summary>
        public static void ClearAll()
        {
            _assemblyDefinition?.Dispose();
            _assemblyDefinition = null;
        }
    }
}