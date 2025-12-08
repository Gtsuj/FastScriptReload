using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using UnityEditor;
using UnityEngine;

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
        public static string AssemblyPath;

        /// <summary>
        /// 修改过的类型缓存
        /// </summary>
        public static Dictionary<string, HookTypeInfo> HookTypeInfoCache = new();

        /// <summary>
        /// 全局缓存已加载的 Wrapper 程序集（按程序集名称索引）
        /// </summary>
        public static Dictionary<string, Assembly> AssemblyCache = new();
        
        private static AssemblyDefinition _assemblyDefinition;

        /// <summary>
        /// 程序集定义缓存（程序集路径 -> AssemblyDefinition）
        /// </summary>
        private static readonly Dictionary<string, AssemblyDefinition> _assemblyDefinitionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 程序集引用缓存（避免重复遍历AppDomain）
        /// </summary>
        private static List<MetadataReference> _assemblyReferencesCache;
        
        /// <summary>
        /// 初始化
        /// </summary>
        [InitializeOnEnterPlayMode]
        public static async void Init()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            // 收集所有类型信息
            await TypeInfoHelper.Initialized();
            
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
            AssemblyPath = Path.Combine(localAppData, "Temp", "FastScriptReloadTemp", projectName);

            if (!Directory.Exists(AssemblyPath))
            {
                Directory.CreateDirectory(AssemblyPath);
            }
        }

        /// <summary>
        /// 获取或读取程序集定义
        /// </summary>
        public static AssemblyDefinition GetOrReadAssemblyDefinition(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
                return null;

            if (!_assemblyDefinitionCache.TryGetValue(assemblyPath, out var assemblyDef))
            {
                if (!File.Exists(assemblyPath))
                {
                    return null;
                }

                try
                {
                    assemblyDef = AssemblyDefinition.ReadAssembly(
                        assemblyPath,
                        new ReaderParameters { ReadWrite = true }
                    );
                    _assemblyDefinitionCache[assemblyPath] = assemblyDef;
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogWarning(
                        $"读取程序集失败: {assemblyPath}, 错误: {ex.Message}");
                    return null;
                }
            }

            return assemblyDef;
        }

        /// <summary>
        /// 获取或解析程序集引用列表
        /// </summary>
        public static List<MetadataReference> GetOrResolveAssemblyReferences()
        {
            if (_assemblyReferencesCache != null)
            {
                return _assemblyReferencesCache;
            }

            var references = new List<MetadataReference>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (AssemblyCache.ContainsKey(assembly.GetName().Name))
                {
                    continue;
                }

                if (assembly.GetName().Name.Contains("Mono.Cecil")
                    || assembly.GetName().Name.Contains("Unity.Cecil"))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(assembly.Location))
                {
                    continue;
                }

                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            _assemblyReferencesCache = references;
            return references;
        }

        /// <summary>
        /// 清除所有缓存（在一次Reload流程完成后调用）
        /// </summary>
        public static void ClearAll()
        {
            // 释放程序集定义（它们实现了IDisposable）
            foreach (var assemblyDef in _assemblyDefinitionCache.Values)
            {
                assemblyDef?.Dispose();
            }
            
            _assemblyDefinitionCache.Clear();
            _assemblyDefinition?.Dispose();
            _assemblyDefinition = null;

            LoggerScoped.LogDebug("热重载缓存已清除");
        }
    }
}