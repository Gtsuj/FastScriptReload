using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 热重载过程中的全局缓存管理器
    /// 缓存文件内容、语法树、程序集定义等，避免重复IO和解析
    /// </summary>
    public static class HotReloadCache
    {
        /// <summary>
        /// 全局缓存的解析选项（在编译过程中不会变化）
        /// </summary>
        public static CSharpParseOptions ParseOptions;

        /// <summary>
        /// 编译程序集
        /// </summary>
        public static MemoryStream AssemblyStream;

        private static AssemblyDefinition _assemblyDefinition;
        public static AssemblyDefinition AssemblyDefinition
        {
            get
            {
                _assemblyDefinition ??= AssemblyDefinition.ReadAssembly(AssemblyStream, new ReaderParameters { ReadWrite = true });
                return _assemblyDefinition;
            }
        }

        /// <summary>
        /// 文件内容缓存（文件路径 -> 文件内容）
        /// </summary>
        private static readonly Dictionary<string, string> _fileContentCache = new ();

        /// <summary>
        /// 语法树缓存（文件路径 -> 语法树）
        /// </summary>
        private static readonly Dictionary<string, SyntaxTree> _syntaxTreeCache = new();

        /// <summary>
        /// 程序集定义缓存（程序集路径 -> AssemblyDefinition）
        /// </summary>
        private static readonly Dictionary<string, AssemblyDefinition> _assemblyDefinitionCache = new (StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 程序集引用缓存（避免重复遍历AppDomain）
        /// </summary>
        private static List<MetadataReference> _assemblyReferencesCache;

        /// <summary>
        /// 获取或读取文件内容
        /// </summary>
        public static string GetOrReadFileContent(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            if (!_fileContentCache.TryGetValue(filePath, out var content))
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                content = File.ReadAllText(filePath);
                _fileContentCache[filePath] = content;
            }

            return content;
        }

        /// <summary>
        /// 获取或解析语法树（使用全局缓存的解析选项）
        /// </summary>
        public static SyntaxTree GetOrParseSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            // 使用文件路径作为缓存键（解析选项是全局的，不需要包含在键中）
            if (_syntaxTreeCache.TryGetValue(filePath, out var syntaxTree))
            {
                return syntaxTree;
            }
            
            var content = GetOrReadFileContent(filePath);
            if (content == null)
            {
                return null;
            }

            syntaxTree = CSharpSyntaxTree.ParseText(
                content,
                ParseOptions,
                path: filePath
            );
            _syntaxTreeCache[filePath] = syntaxTree;

            return syntaxTree;
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
                        new ReaderParameters { ReadWrite = false }
                    );
                    _assemblyDefinitionCache[assemblyPath] = assemblyDef;
                }
                catch (Exception ex)
                {
                    ImmersiveVrToolsCommon.Runtime.Logging.LoggerScoped.LogWarning(
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
                if (ReloadHelper.AssemblyCache.ContainsKey(assembly.GetName().Name))
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
        /// 清除所有缓存
        /// 在一次Hook流程完成后调用
        /// </summary>
        public static void ClearAll()
        {
            // 释放程序集定义（它们实现了IDisposable）
            foreach (var assemblyDef in _assemblyDefinitionCache.Values)
            {
                try
                {
                    assemblyDef?.Dispose();
                }
                catch
                {
                    // 忽略释放时的异常
                }
            }

            _fileContentCache.Clear();
            
            _assemblyDefinitionCache.Clear();
            _assemblyReferencesCache = null;
            _syntaxTreeCache.Clear();
            AssemblyStream?.Dispose();
            AssemblyStream = null;
            _assemblyDefinition?.Dispose();
            _assemblyDefinition = null;

            ImmersiveVrToolsCommon.Runtime.Logging.LoggerScoped.LogDebug("热重载缓存已清除");
        }
    }
}

