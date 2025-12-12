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
        /// 文件路径到程序集的映射缓存（用于确定文件所属程序集）
        /// </summary>
        private static Dictionary<string, UnityEditor.Compilation.Assembly> _fileToAssemblyCache = new();
        
        /// <summary>
        /// 程序集名称到程序集的映射缓存（用于确定程序集名称对应的程序集）
        /// </summary>
        private static Dictionary<string, UnityEditor.Compilation.Assembly> _nameToAssemblyCache = new();
        
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

            InitializeFileToAssemblyCache();

            _ = RoslynHelper.PARSE_OPTIONS;
            // 收集所有类型信息
            TypeInfoHelper.Initialized();
            
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

                if (string.IsNullOrEmpty(assembly.Location))
                {
                    continue;
                }

                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            _assemblyReferencesCache = references;
            return references;
        }

        #region 程序集识别和分组

        /// <summary>
        /// 初始化文件到程序集的映射缓存
        /// </summary>
        private static void InitializeFileToAssemblyCache()
        {
            // 获取所有程序集（包括 Editor 和 Player）
            var allAssemblies = CompilationPipeline.GetAssemblies();
            foreach (var assembly in allAssemblies)
            {
                foreach (var sourceFile in assembly.sourceFiles)
                {
                    var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", sourceFile));
                    _fileToAssemblyCache[path] = assembly;
                }

                _nameToAssemblyCache[assembly.name] = assembly;
            }
        }

        /// <summary>
        /// 根据文件路径获取所属程序集名称
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>程序集名称，如果找不到则返回 Assembly-CSharp</returns>
        public static string GetAssemblyNameForFile(string filePath)
        {
            if (_fileToAssemblyCache.TryGetValue(filePath, out var assembly))
            {
                return assembly.name;
            }
            
            LoggerScoped.LogWarning($"无法确定文件 {Path.GetFileName(filePath)} 所属的程序集，默认使用 Assembly-CSharp");

            return null;
        }

        /// <summary>
        /// 根据程序集名称获取程序集的依赖
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>程序集的依赖</returns>
        public static List<MetadataReference> GetAssemblyDependencies(string assemblyName)
        {
            List<MetadataReference> references = new ();
            if (_nameToAssemblyCache.TryGetValue(assemblyName, out var assembly))
            {
                foreach (var assemblyReference in assembly.assemblyReferences)
                {
                    references.Add(MetadataReference.CreateFromFile(assemblyReference.outputPath));
                }
                
                foreach (var compiledAssemblyReference in assembly.compiledAssemblyReferences)
                {
                    references.Add(MetadataReference.CreateFromFile(compiledAssemblyReference));
                }
                
                references.Add(MetadataReference.CreateFromFile(assembly.outputPath));
            }

            return references;
        }
        #endregion

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