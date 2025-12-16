using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using HarmonyLib;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEngine;
using UnityEngine.Profiling;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 使用Roslyn收集类型信息
    /// </summary>
    public static class TypeInfoHelper
    {
        private static bool _isInitialized;
        private static CSharpCompilation _compilation;
        private static readonly ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();

        public static readonly ConcurrentDictionary<string, TypeInfo> TypeInfoCache = new();

        /// <summary>
        /// 初始化并收集所有类型信息（同步版本，保持向后兼容）
        /// </summary>
        public static void Initialized()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            // if (_isInitialized) return;
            
            CollectAllTypeInfo();

            _isInitialized = true;
        }
        
        /// <summary>
        /// 获取文件快照
        /// </summary>
        public static FileSnapshot GetFileSnapshot(string filePath)
        {
            if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
            {
                SaveFileSnapshot(filePath, out fileSnapshot);
            }

            return fileSnapshot;
        }
        
        public static SemanticModel GetSemanticModel(SyntaxTree tree)
        {
            return _compilation.GetSemanticModel(tree);
        }

        /// <summary>
        /// 更新多个文件的类型信息
        /// </summary>
        public static void UpdateTypeInfoForFiles(Dictionary<string, SyntaxTree> syntaxTrees)
        {
            foreach (var (filePath, syntaxTree) in syntaxTrees)
            {
                UpdateTypeInfoForFile(filePath, syntaxTree);
            }
        }

        /// <summary>
        /// 更新单个文件的类型信息
        /// </summary>
        public static void UpdateTypeInfoForFile(string filePath, SyntaxTree newSyntaxTree)
        {
            try
            {
                if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
                {
                    return;
                }

                var oldSyntaxTree = fileSnapshot.SyntaxTree;
                fileSnapshot.SyntaxTree = newSyntaxTree;

                _compilation = _compilation.ReplaceSyntaxTree(oldSyntaxTree, fileSnapshot.SyntaxTree);

                // 遍历新语法树中的类型，更新类型信息
                var typeDecls = newSyntaxTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();
                foreach (var typeDecl in typeDecls)
                {
                    var fullTypeName = typeDecl.FullName();
                    if (TypeInfoCache.TryRemove(fullTypeName, out var typeInfo))
                    {
                        // 重新遍历所有包含该类型的文件，更新类型信息
                        foreach (var typeInfoFilePath in typeInfo.FilePaths)
                        {
                            var typeInfoFileSnapshot = GetFileSnapshot(typeInfoFilePath);
                            var semanticModel = _compilation.GetSemanticModel(typeInfoFileSnapshot.SyntaxTree);
                            CollectTypeInfoFromFile(semanticModel);
                        }
                    }
                    else
                    {
                        var semanticModel = _compilation.GetSemanticModel(newSyntaxTree);
                        CollectTypeInfoFromFile(semanticModel);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"更新类型信息失败: {filePath}, {ex.Message}");
            }
        }        
        
        /// <summary>
        /// 收集所有类型信息（并行处理版本，阻塞主线程直到完成）
        /// </summary>
        private static void CollectAllTypeInfo()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var filePaths = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);
            
            var syntaxTrees = new ConcurrentBag<SyntaxTree>();
            
            Parallel.ForEach(filePaths, filePath =>
            {
                SaveFileSnapshot(filePath, out var fileSnapshot);
                if (fileSnapshot != null)
                {
                    syntaxTrees.Add(fileSnapshot.SyntaxTree);
                }
            });

            var syntaxTreeList = syntaxTrees.ToList();

            var metadataReferences = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location));
            _compilation = CSharpCompilation.Create(
                "TypeInfoCollection", syntaxTreeList,
                metadataReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            
            Parallel.ForEach(syntaxTreeList, tree =>
            {
                SemanticModel semanticModel = _compilation.GetSemanticModel(tree);
                CollectTypeInfoFromFile(semanticModel);
            });

            stopwatch.Stop();
            LoggerScoped.Log($"类型信息收集完成，耗时: {stopwatch.ElapsedMilliseconds}ms, 收集类型数: {TypeInfoCache.Count}");
        }

        /// <summary>
        /// 保存文件快照
        /// </summary>
        private static void SaveFileSnapshot(string filePath, out FileSnapshot fileSnapshot)
        {
            fileSnapshot = null;
            
            filePath = filePath.Replace("/", "\\");
            var syntaxTree = RoslynHelper.GetSyntaxTree(filePath);
            if (syntaxTree != null)
            {
                fileSnapshot = new FileSnapshot
                {
                    FilePath = filePath,
                    SyntaxTree = syntaxTree,
                    SnapshotTime = DateTime.UtcNow
                };

                _fileSnapshots[filePath] = fileSnapshot;

                LoggerScoped.LogDebug($"已保存文件快照: {filePath}");
            }
        }

        /// <summary>
        /// 从单个文件收集类型信息
        /// </summary>
        private static void CollectTypeInfoFromFile(SemanticModel semanticModel)
        {
            if (semanticModel == null)
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
                    var fullTypeName = typeDecl.FullName();
                    if (!TypeInfoCache.TryGetValue(fullTypeName, out var typeInfo))
                    {
                        typeInfo = new TypeInfo()
                        {
                            TypeFullName = fullTypeName
                        };

                        TypeInfoCache.TryAdd(fullTypeName, typeInfo);
                    }

                    typeInfo.FilePaths.Add(filePath);
                    
                    CollectInternalMembers(typeInfo, typeDecl, semanticModel);
                    CollectUsedGenericMethods(typeInfo, typeDecl, semanticModel);
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"收集类型信息失败: {filePath}, {ex.Message}");
            }
        }

        private static void CollectInternalMembers(TypeInfo typeInfo, TypeDeclarationSyntax typeDecl, SemanticModel semanticModel)
        {
            typeInfo.IsInternalClass = typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));

            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                {
                    typeInfo.InternalMember.Add(method.FullName(semanticModel));
                }
            }

            foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        typeInfo.InternalMember.Add(variable.Identifier.ValueText);
                    }
                }
            }
            
            foreach (var property in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (property.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
                {
                    typeInfo.InternalMember.Add(property.Identifier.ValueText);
                }
            }
        }
        
        private static void CollectUsedGenericMethods(TypeInfo typeInfo, TypeDeclarationSyntax typeDecl, SemanticModel semanticModel)
        {
            var filePath = semanticModel.SyntaxTree.FilePath;
            var collector = new GenericMethodCallWalker();
            collector.SetSemanticModel(semanticModel);
            collector.Analyze(typeDecl);

            var genericMethodCalls = collector.GetGenericMethodCalls();
            foreach (var kvp in genericMethodCalls)
            {
                if (!typeInfo.UsedGenericMethods.TryGetValue(kvp.Key, out var existingCallers))
                {
                    existingCallers = new HashSet<GenericMethodCallInfo>();
                    typeInfo.UsedGenericMethods[kvp.Key] = existingCallers;
                }

                foreach (var caller in kvp.Value)
                {
                    existingCallers.Add(new GenericMethodCallInfo(filePath, caller));
                }
            }
        }
    }
    
    /// <summary>
    /// 文件快照
    /// </summary>
    public class FileSnapshot
    {
        public string FilePath { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public DateTime SnapshotTime { get; set; }
    }

    /// <summary>
    /// 泛型方法调用信息
    /// </summary>
    public struct GenericMethodCallInfo : IEquatable<GenericMethodCallInfo>
    {
        public readonly string FilePath;
        public readonly string MethodName;

        public GenericMethodCallInfo(string filePath, string methodName)
        {
            FilePath = filePath;
            MethodName = methodName;
        }

        public bool Equals(GenericMethodCallInfo other)
        {
            return FilePath == other.FilePath && MethodName == other.MethodName;
        }

        public override bool Equals(object obj)
        {
            return obj is GenericMethodCallInfo other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((FilePath?.GetHashCode() ?? 0) * 397) ^ (MethodName?.GetHashCode() ?? 0);
            }
        }
    }

    /// <summary>
    /// 类型信息，包含使用Roslyn收集的类型元数据
    /// </summary>
    public class TypeInfo
    {
        public string TypeFullName;

        // 类型所在文件路径
        // public string FilePath;

        // 类型中使用的泛型方法（Key: 调用的泛型方法全名, Value: (调用处文件所在路径, 调用处的方法名)）
        public readonly Dictionary<string, HashSet<GenericMethodCallInfo>> UsedGenericMethods = new();

        // 类型中的internal字段
        public readonly HashSet<string> InternalMember = new();

        // 自身是否为internal类
        public bool IsInternalClass;

        public bool IsInternal => IsInternalClass || InternalMember.Count > 0;

        // 如果是部分类，记录其余部分类的文件路径
        public readonly HashSet<string> FilePaths = new();
    }
}
