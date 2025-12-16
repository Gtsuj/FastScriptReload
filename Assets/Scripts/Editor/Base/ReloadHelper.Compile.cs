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
using Mono.Cecil.Pdb;

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
        /// <param name="assemblyName">改动文件对应的AssemblyName</param>
        /// <param name="csFilePaths">要编译的 C# 文件路径列表</param>
        /// <returns>编译结果，包含编译后的程序集内存流和诊断信息</returns>
        public static Dictionary<string, DiffResult> CompileCsFiles(string assemblyName, List<string> csFilePaths)
        {
            try
            {
                var syntaxTrees = new Dictionary<string, SyntaxTree>();

                csFilePaths.ForEach(filePath => syntaxTrees.Add(filePath, RoslynHelper.GetSyntaxTree(filePath)));

                // 创建编译（合并所有文件到一个程序集）
                var compilation = CSharpCompilation.Create(
                    assemblyName: $"{assemblyName}_{Guid.NewGuid()}",
                    syntaxTrees: syntaxTrees.Values,
                    references: GetAssemblyDependencies(assemblyName),
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

                // 更新缓存的类型信息
                TypeInfoHelper.UpdateTypeInfoForFiles(syntaxTrees);
                
                if (typeDiffs.Count == 0)
                {
                    return null;
                }

                // 收集需要编译的文件
                CollectFilesToCompile(csFilePaths, syntaxTrees, typeDiffs);

                compilation = compilation.RemoveAllSyntaxTrees();
                compilation = compilation.AddSyntaxTrees(syntaxTrees.Values);

                var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (emitResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    _assemblyDefinition = AssemblyDefinition.ReadAssembly(ms, READER_PARAMETERS);

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
            var filesToProcess = new Queue<string>(csFilePaths);

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

                var semanticModel = TypeInfoHelper.GetSemanticModel(syntaxTree);
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
            HashSet<string> handleMethod = new();
            // 遍历修改的方法，收集修改的泛型方法
            foreach (var (_, diff) in typeDiffs)
            {
                foreach (var (_, modifiedMethod) in diff.ModifiedMethods)
                {
                    if (!modifiedMethod.IsGenericMethod)
                    {
                        continue;
                    }

                    handleMethod.Add(modifiedMethod.FullName);
                }
            }

            // 预先构建映射关系：泛型方法 FullName -> HashSet<类型名>
            var genericMethodToCallerTypes = new Dictionary<string, HashSet<string>>();
            foreach (var (typeFullName, typeInfo) in TypeInfoHelper.TypeInfoCache)
            {
                foreach (var (genericMethodFullName, _) in typeInfo.UsedGenericMethods)
                {
                    if (!genericMethodToCallerTypes.TryGetValue(genericMethodFullName, out var callerTypes))
                    {
                        callerTypes = new HashSet<string>();
                        genericMethodToCallerTypes[genericMethodFullName] = callerTypes;
                    }
                    callerTypes.Add(typeFullName);
                }
            }
            
            // 将泛型方法的调用者加入修改列表
            foreach (var methodName in handleMethod)
            {
                if (!genericMethodToCallerTypes.TryGetValue(methodName, out var callerTypeNames))
                {
                    continue;
                }

                foreach (var typeFullName in callerTypeNames)
                {
                    var callTypeInfo = TypeInfoHelper.TypeInfoCache.GetValueOrDefault(typeFullName);
                    if (callTypeInfo == null)
                    {
                        continue;
                    }

                    if (!callTypeInfo.UsedGenericMethods.TryGetValue(methodName, out var useGenericInfo))
                    {
                        continue;
                    }

                    // 将调用者加入修改列表
                    foreach (var callInfo in useGenericInfo)
                    {
                        if (!typeDiffs.TryGetValue(typeFullName, out var callTypeDiff))
                        {
                            callTypeDiff = new DiffResult();
                            typeDiffs.Add(typeFullName, callTypeDiff);
                        }

                        if (!callTypeDiff.ModifiedMethods.ContainsKey(callInfo.MethodName))
                        {
                            callTypeDiff.ModifiedMethods.Add(callInfo.MethodName, new MethodDiffInfo()
                            {
                                FullName = callInfo.MethodName
                            });
                        }
                            
                    }

                    // 从泛型方法调用信息中获取文件路径
                    foreach (var filePath in callTypeInfo.FilePaths)
                    {
                        filesToProcess.Enqueue(filePath);
                    }
                }                
            }
        }
    }
}
