using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 使用Roslyn收集类型信息的服务
    /// </summary>
    public static class TypeInfoCollector
    {
        // Key: FullTypeName, Value: TypeInfo
        private static readonly ConcurrentDictionary<string, TypeInfo> _typeInfoCache = new();
        private static bool _isInitialized = false;

        /// <summary>
        /// 初始化并收集所有类型信息
        /// </summary>
        public static async Task EnsureInitialized()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            if (_isInitialized) return;

            _isInitialized = true;
            CollectAllTypeInfo();
        }

        /// <summary>
        /// 获取类型信息
        /// </summary>
        public static TypeInfo GetTypeInfo(string fullTypeName)
        {
            if (_typeInfoCache.TryGetValue(fullTypeName, out var typeInfo))
            {
                return typeInfo;
            }
            return null;
        }

        /// <summary>
        /// 获取所有类型信息
        /// </summary>
        public static IReadOnlyDictionary<string, TypeInfo> GetAllTypeInfo()
        {
            return _typeInfoCache;
        }

        /// <summary>
        /// 收集所有类型信息
        /// </summary>
        private static void CollectAllTypeInfo()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 获取所有脚本文件
            var scriptPaths = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories).ToList();
            
            // 解析所有语法树
            var syntaxTrees = new List<SyntaxTree>();
            
            foreach (var filePath in scriptPaths)
            {
                try
                {
                    var tree = ReloadHelper.GetSyntaxTree(filePath);
                    if (tree != null)
                    {
                        syntaxTrees.Add(tree);
                    }
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogDebug($"解析文件失败: {filePath}, {ex.Message}");
                }
            }

            if (syntaxTrees.Count == 0) return;

            // 创建编译
            var references = ReloadHelper.GetOrResolveAssemblyReferences();
            var compilation = CSharpCompilation.Create(
                "TypeInfoCollection",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            // 使用语义收集类型信息
            foreach (var tree in syntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                CollectTypeInfoFromFile(semanticModel);
            }

            stopwatch.Stop();
            LoggerScoped.Log($"类型信息收集完成，耗时: {stopwatch.ElapsedMilliseconds}ms, 收集类型数: {_typeInfoCache.Count}");
        }

        /// <summary>
        /// 合并类型信息到缓存中
        /// </summary>
        private static void MergeTypeInfoIntoCache(string fullTypeName, TypeInfo typeInfo)
        {
            _typeInfoCache.AddOrUpdate(fullTypeName,
                _ => typeInfo,
                (_, existingInfo) =>
                {
                    // 合并泛型方法调用
                    MergeUsedGenericMethods(existingInfo.UsedGenericMethods, typeInfo.UsedGenericMethods);
                    // 合并internal成员
                    existingInfo.InternalMember.UnionWith(typeInfo.InternalMember);
                    // 如果任一文件中的类型是internal，则标记为internal
                    if (typeInfo.IsInternalClass)
                    {
                        existingInfo.IsInternalClass = true;
                    }
                    // 合并部分类文件
                    existingInfo.PartialFiles.UnionWith(typeInfo.PartialFiles);
                    // 如果新类型的文件路径不在部分类文件中，添加它
                    if (!existingInfo.PartialFiles.Contains(typeInfo.FilePath) && existingInfo.FilePath != typeInfo.FilePath)
                    {
                        existingInfo.PartialFiles.Add(typeInfo.FilePath);
                    }
                    return existingInfo;
                });
        }

        /// <summary>
        /// 从单个文件收集类型信息
        /// </summary>
        private static void CollectTypeInfoFromFile(SemanticModel semanticModel)
        {
            if (semanticModel == null || semanticModel.SyntaxTree == null)
                return;

            var filePath = semanticModel.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                var tree = semanticModel.SyntaxTree;
                var root = tree.GetRoot();
                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDecls)
                {
                    var fullTypeName = GetTypeFullName(typeDecl);
                    
                    // 检查是否已经存在该类型（部分类情况）
                    bool isPartialClass = _typeInfoCache.TryGetValue(fullTypeName, out var existingTypeInfo);
                    
                    var typeInfo = CollectTypeInfo(typeDecl, filePath, root, tree, semanticModel);
                    if (typeInfo != null)
                    {
                        if (isPartialClass)
                        {
                            // 如果已存在，说明是部分类，添加到部分类文件列表
                            if (!existingTypeInfo.PartialFiles.Contains(filePath) && existingTypeInfo.FilePath != filePath)
                            {
                                existingTypeInfo.PartialFiles.Add(filePath);
                            }
                            // 合并类型信息
                            MergeTypeInfoIntoCache(fullTypeName, typeInfo);
                        }
                        else
                        {
                            // 新类型，检查是否应该存储
                            if (ShouldStoreTypeInfo(typeInfo))
                            {
                                MergeTypeInfoIntoCache(fullTypeName, typeInfo);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"收集类型信息失败: {filePath}, {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否应该存储类型信息（如果所有数据都是0，则不存储）
        /// 注意：PartialFiles和UsedGenericMethods在收集过程中才填充，所以这里不检查
        /// </summary>
        private static bool ShouldStoreTypeInfo(TypeInfo typeInfo)
        {
            return typeInfo.UsedGenericMethods.Count > 0
                   || typeInfo.InternalMember.Count > 0
                   || typeInfo.IsInternalClass;
        }

        /// <summary>
        /// 收集单个类型的信息
        /// </summary>
        private static TypeInfo CollectTypeInfo(TypeDeclarationSyntax typeDecl, string filePath, SyntaxNode root, SyntaxTree syntaxTree, SemanticModel semanticModel)
        {
            var typeInfo = new TypeInfo
            {
                FilePath = filePath
            };

            // 检查是否为internal类
            typeInfo.IsInternalClass = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));

            // 收集internal方法
            var methods = typeDecl.Members.OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                bool hasInternal = method.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
                if (hasInternal)
                {
                    var methodName = GetMethodFullName(method);
                    typeInfo.InternalMember.Add(methodName);
                }
            }

            // 收集internal字段
            var fields = typeDecl.Members.OfType<FieldDeclarationSyntax>();
            foreach (var field in fields)
            {
                bool hasInternal = field.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
                
                if (hasInternal)
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        typeInfo.InternalMember.Add(variable.Identifier.ValueText);
                    }
                }
            }
            
            // 收集internal属性
            var properties = typeDecl.Members.OfType<PropertyDeclarationSyntax>();
            foreach (var property in properties)
            {
                bool hasInternal = property.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
                if (hasInternal)
                {
                    typeInfo.InternalMember.Add(property.Identifier.ValueText);
                }
            }
            
            // 收集类型中使用的泛型方法（使用 GenericMethodCallWalker）
            CollectUsedGenericMethods(typeDecl, typeInfo, semanticModel);

            return typeInfo;
        }

        /// <summary>
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
        private static string GetTypeFullName(TypeDeclarationSyntax typeDecl)
        {
            var parts = new List<string>();
            var current = typeDecl;
            
            // 收集所有嵌套类型名
            while (current != null)
            {
                parts.Insert(0, current.Identifier.ValueText);
                current = current.Parent as TypeDeclarationSyntax;
            }
            
            // 获取命名空间
            var namespaceDecl = typeDecl.Parent;
            while (namespaceDecl != null && !(namespaceDecl is BaseNamespaceDeclarationSyntax))
            {
                namespaceDecl = namespaceDecl.Parent;
            }
            
            var namespaceName = (namespaceDecl as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? string.Empty;
            var typeName = string.Join(".", parts);
            
            return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
        }

        /// <summary>
        /// 获取方法全名（包含参数类型）
        /// </summary>
        private static string GetMethodFullName(MethodDeclarationSyntax method)
        {
            var methodName = method.Identifier.ValueText;
            var parameters = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));
            return $"{methodName}({parameters})";
        }

        /// <summary>
        /// 收集类型中使用的泛型方法（包括自身和其他类的泛型方法）
        /// </summary>
        private static void CollectUsedGenericMethods(TypeDeclarationSyntax typeDecl, TypeInfo typeInfo, SemanticModel semanticModel)
        {
            if (semanticModel == null)
                return;

            // 获取类名（包含命名空间）
            var className = GetTypeFullName(typeDecl);

            // 使用 GenericMethodCallWalker 收集泛型方法调用
            // 只遍历当前类型的语法节点，而不是整个语法树
            var walker = new GenericMethodCallWalker(semanticModel);
            walker.Visit(typeDecl);

            // 获取收集到的泛型方法调用
            var genericMethodCalls = walker.GetGenericMethodCalls();

            // 将结果合并到 typeInfo.UsedGenericMethods
            // 需要将调用者方法名格式化为 "ClassName::MethodName" 格式
            foreach (var kvp in genericMethodCalls)
            {
                var genericMethodFullName = kvp.Key;
                var callers = kvp.Value;

                if (!typeInfo.UsedGenericMethods.TryGetValue(genericMethodFullName, out var existingCallers))
                {
                    existingCallers = new HashSet<string>();
                    typeInfo.UsedGenericMethods[genericMethodFullName] = existingCallers;
                }

                // 将调用者方法名格式化为 "ClassName::MethodName" 格式
                foreach (var caller in callers)
                {
                    // 如果调用者方法名不包含类名，添加类名前缀
                    var formattedCaller = caller.Contains("::") ? caller : $"{className}::{caller}";
                    existingCallers.Add(formattedCaller);
                }
            }
        }
        
        /// <summary>
        /// 合并两个UsedGenericMethods字典
        /// </summary>
        private static void MergeUsedGenericMethods(Dictionary<string, HashSet<string>> target, Dictionary<string, HashSet<string>> source)
        {
            foreach (var kvp in source)
            {
                if (!target.TryGetValue(kvp.Key, out var targetCallers))
                {
                    target[kvp.Key] = new HashSet<string>(kvp.Value);
                }
                else
                {
                    targetCallers.UnionWith(kvp.Value);
                }
            }
        }

        /// <summary>
        /// 更新单个文件的类型信息（当文件变更时调用）
        /// </summary>
        public static void UpdateTypeInfoForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            try
            {
                filePath = filePath.Replace('/', '\\');
                
                // 先移除该文件中所有类型的旧信息
                var typesToRemove = _typeInfoCache
                    .Where(kv => kv.Value.FilePath == filePath)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var typeName in typesToRemove)
                {
                    _typeInfoCache.TryRemove(typeName, out _);
                }

                // 重新收集该文件的类型信息
                var tree = ReloadHelper.GetSyntaxTree(filePath);
                if (tree == null) return;

                // 创建编译（只包含当前文件）
                var references = ReloadHelper.GetOrResolveAssemblyReferences();
                var compilation = CSharpCompilation.Create(
                    "TypeInfoCollection",
                    new[] { tree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                var semanticModel = compilation.GetSemanticModel(tree);
                CollectTypeInfoFromFile(semanticModel);
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"更新类型信息失败: {filePath}, {ex.Message}");
            }
        }
    }
}
