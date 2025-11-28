using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 全局类型到源文件的索引，用于支持 Partial 类查找
    /// </summary>
    public static class TypeSourceIndex
    {
        // Key: FullTypeName, Value: FilePaths
        private static readonly ConcurrentDictionary<string, HashSet<string>> _typeToFileIndex = new();
        private static bool _isInitialized = false;

        /// <summary>
        /// 初始化索引
        /// </summary>
        public static async Task EnsureInitialized()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            if (_isInitialized) return;

            _isInitialized = true;
            await Task.Run(BuildIndex);
        }

        /// <summary>
        /// 获取类型定义的所有源文件路径
        /// </summary>
        public static HashSet<string> GetFilesForType(string fullTypeName)
        {
            if (_typeToFileIndex.TryGetValue(fullTypeName, out var files))
            {
                lock (files)
                {
                    return files;
                }
            }
            return null;
        }

        /// <summary>
        /// 从文件列表中提取类型名，并解析出对应的所有 partial 文件路径
        /// </summary>
        /// <param name="filePaths">变更的文件列表</param>
        /// <returns>类型全名 -> 该类型所有源文件路径集合</returns>
        public static Dictionary<string, HashSet<string>> GetTypeToFilesMap(List<string> filePaths)
        {
            var typeToFilesMap = new Dictionary<string, HashSet<string>>();

            foreach (var filePath in filePaths)
            {
                var tree = ReloadHelper.GetOrParseSyntaxTree(filePath);
                if (tree == null) continue;

                var root = tree.GetRoot();
                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDecls)
                {
                    var fullTypeName = GetTypeFullName(typeDecl);

                    if (!typeToFilesMap.TryGetValue(fullTypeName, out var fileSet))
                    {
                        fileSet = new HashSet<string>();
                        typeToFilesMap[fullTypeName] = fileSet;
                    }

                    var allPartialFiles = GetFilesForType(fullTypeName);
                    if (allPartialFiles != null)
                    {
                        fileSet.UnionWith(allPartialFiles);
                        continue;
                    }

                    fileSet.Add(filePath);
                }
            }

            return typeToFilesMap;
        }        

        private static void BuildIndex()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 获取所有脚本文件
            var scriptPaths = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);

            Parallel.ForEach(scriptPaths, filePath =>
            {
                IndexFile(filePath);
            });

            stopwatch.Stop();
            LoggerScoped.LogDebug($"类型索引构建完成，耗时: {stopwatch.ElapsedMilliseconds}ms, 索引类型数: {_typeToFileIndex.Count}");
        }

        /// <summary>
        /// 为单个文件建立索引
        /// </summary>
        private static void IndexFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            // try
            // {
                filePath = filePath.Replace('/', '\\');
                var tree = ReloadHelper.GetOrParseSyntaxTree(filePath);
                var root = tree.GetRoot();

                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDecls)
                {
                    var fullTypeName = GetTypeFullName(typeDecl);

                    _typeToFileIndex.AddOrUpdate(fullTypeName,
                        _ => new HashSet<string> { filePath },
                        (_, files) =>
                        {
                            lock (files)
                            {
                                files.Add(filePath);
                            }
                            return files;
                        });
                }
            // }
            // catch (Exception ex)
            // {
            //     LoggerScoped.LogWarning($"建立索引失败: {filePath}, {ex.Message}");
            // }
        }

        /// <summary>
        /// 获取类型全名
        /// </summary>
        /// <param name="typeDecl">类型声明</param>
        /// <returns>类型全名</returns>
        private static string GetTypeFullName(TypeDeclarationSyntax typeDecl)
        {
            var namespaceDecl = typeDecl.Parent as BaseNamespaceDeclarationSyntax;
            var namespaceName = namespaceDecl?.Name.ToString() ?? string.Empty;
            var typeName = typeDecl.Identifier.ValueText;
            return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
        }
    }
}

