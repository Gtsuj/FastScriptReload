using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Editor.Compilation.CodeRewriting;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 编译功能 - 使用 Roslyn 编译 C# 文件
    /// </summary>
    public static partial class ReloadHelper
    {
        /// <summary>
        /// 使用 Roslyn 编译 C# 文件到一个程序集（支持单个或多个文件）
        /// </summary>
        /// <param name="csFilePaths">要编译的 C# 文件路径列表</param>
        /// <returns>编译结果，包含编译后的程序集内存流和诊断信息</returns>
        public static Dictionary<string, DiffResult> CompileCsFiles(List<string> csFilePaths)
        {
            try
            {
                var syntaxTrees = new Dictionary<string, SyntaxTree>();

                csFilePaths.ForEach(filePath => syntaxTrees.Add(filePath, RoslynHelper.GetSyntaxTree(filePath)));

                // 创建编译（合并所有文件到一个程序集）
                var compilation = CSharpCompilation.Create(
                    assemblyName: Guid.NewGuid().ToString("N"),
                    syntaxTrees: syntaxTrees.Values,
                    references: GetOrResolveAssemblyReferences(),
                    options: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Debug,
                        allowUnsafe: true
                    )
                );

                // Key: 类型全名
                var typeDiffs = new Dictionary<string, DiffResult>();

                // 收集改动文件的差异
                foreach (var (_, syntaxTree) in syntaxTrees)
                {
                    DiffAnalyzerHelper.AnalyzeDiff(compilation, syntaxTree, typeDiffs);
                }

                if (typeDiffs.Count == 0)
                {
                    return null;
                }

                // 更新缓存的类型信息
                TypeInfoHelper.UpdateTypeInfoForFiles(csFilePaths);

                // 收集需要编译的文件
                CollectFilesToCompile(csFilePaths, syntaxTrees, typeDiffs);

                compilation = compilation.RemoveAllSyntaxTrees();
                compilation = compilation.AddSyntaxTrees(syntaxTrees.Values);

                var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (emitResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    _assemblyDefinition = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { ReadWrite = true, InMemory = true });

                    return typeDiffs;
                }

                var errorMsg = string.Join("\n", emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

                LoggerScoped.LogError($"编译失败:{errorMsg}");
                return null;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"编译文件时发生异常  : {ex}");
                return null;
            }
        }

        // 收集需要重新编译的文件
        private static void CollectFilesToCompile(List<string> csFilePaths, Dictionary<string, SyntaxTree> syntaxTrees, Dictionary<string, DiffResult> typeDiffs)
        {
            var processedFiles = new HashSet<string>();
            // 待处理文件队列
            var filesToProcess = new Queue<string>();

            // 从 csFilePaths 收集初始文件
            foreach (var filePath in csFilePaths)
            {
                filesToProcess.Enqueue(filePath);
            }

            // 添加过方法、属性的类型加入编译列表
            foreach (var (_, hookTypeInfo) in HookTypeInfoCache)
            {
                if (hookTypeInfo.ModifiedMethods.Any(pair => pair.Value.HookMethodState == HookMethodState.Added) || hookTypeInfo.AddedFields.Count > 0)
                {
                    var typeInfo = TypeInfoHelper.TypeInfoCache.GetValueOrDefault(hookTypeInfo.TypeFullName);
                    if (typeInfo == null)
                    {
                        continue;
                    }

                    foreach (var filePath in typeInfo.FilePaths)
                    {
                        filesToProcess.Enqueue(filePath);
                    }
                }
            }

            // 收集泛型方法的调用者
            CollectionGenericMethodCaller(typeDiffs, filesToProcess);

            // 递归处理队列中的文件
            while (filesToProcess.Count > 0)
            {
                var filePath = filesToProcess.Dequeue();

                // 防止重复处理
                if (!processedFiles.Add(filePath))
                {
                    continue;
                }

                // 已修改的文件，使用 syntaxTrees 中的语法树
                if (!syntaxTrees.TryGetValue(filePath, out var syntaxTree))
                {
                    // 未修改的文件，从 FileSnapshot 缓存中获取
                    var fileSnapshot = TypeInfoHelper.GetFileSnapshot(filePath);
                    syntaxTree = fileSnapshot.SyntaxTree;
                }

                syntaxTrees.TryAdd(filePath, syntaxTree);

                // 创建临时编译以获取语义模型
                // 只需要当前文件的语法树和程序集引用即可，TypeDependencyWalker 主要用于获取类型名称
                var tempCompilation = CSharpCompilation.Create(
                    "Temp",
                    new[] { syntaxTree },
                    GetOrResolveAssemblyReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                var semanticModel = tempCompilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                // 获取文件中定义的所有类型
                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

                // 对于每个类型，收集部分类文件和 Internal 依赖
                foreach (var typeDecl in typeDecls)
                {
                    var fullTypeName = typeDecl.FullName();
                    var typeInfo = TypeInfoHelper.TypeInfoCache.GetValueOrDefault(fullTypeName);
                    
                    // 收集部分类文件
                    if (typeInfo != null)
                    {
                        foreach (var partialFile in typeInfo.FilePaths)
                        {
                            if (File.Exists(partialFile) && !processedFiles.Contains(partialFile))
                            {
                                filesToProcess.Enqueue(partialFile);
                            }
                        }
                    }

                    // 处理 Internal 依赖
                    var typeDependency = new TypeDependencyWalker(semanticModel);
                    typeDependency.Visit(typeDecl);

                    foreach (var dependentTypeName in typeDependency.GetFullTypeNames())
                    {
                        var dependentTypeInfo = TypeInfoHelper.TypeInfoCache.GetValueOrDefault(dependentTypeName);
                        if (dependentTypeInfo == null)
                        {
                            continue;
                        }

                        // 检查是否为 Internal 类型或包含 Internal 成员
                        if (!dependentTypeInfo.IsInternal)
                        {
                            continue;
                        }

                        // 添加该类型的所有部分类文件（FilePaths）
                        foreach (var dependentFile in dependentTypeInfo.FilePaths)
                        {
                            if (File.Exists(dependentFile) && !processedFiles.Contains(dependentFile))
                            {
                                filesToProcess.Enqueue(dependentFile);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 收集泛型方法的调用者
        /// </summary>
        /// <param name="typeDiffs"></param>
        /// <param name="filesToProcess"></param>
        private static void CollectionGenericMethodCaller(Dictionary<string, DiffResult> typeDiffs, Queue<string> filesToProcess)
        {
            foreach (var (_, diff) in typeDiffs)
            {
                foreach (var modifiedMethod in diff.ModifiedMethods)
                {
                    if (!modifiedMethod.IsGenericMethod)
                    {
                        continue;
                    }

                    foreach (var (_, typeInfo) in TypeInfoHelper.TypeInfoCache)
                    {
                        if (!typeInfo.UsedGenericMethods.TryGetValue(modifiedMethod.FullName, out var useGenericInfo))
                        {
                            continue;
                        }

                        foreach (var filePath in typeInfo.FilePaths)
                        {
                            filesToProcess.Enqueue(filePath);
                        }
                    }
                }
            }
        }
    }
}
