using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Editor.Compilation.CodeRewriting;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                var filesToCompile = new HashSet<string>(csFilePaths);
                var syntaxTrees = new Dictionary<string, SyntaxTree>();
                
                // Key: 类型全名
                var typeDiffs = new Dictionary<string, DiffResult>();
                
                // 收集改动文件的差异
                foreach (var csFilePath in csFilePaths)
                {
                    DiffAnalyzerHelper.AnalyzeDiff(csFilePath, typeDiffs);
                }

                if (typeDiffs.Count == 0)
                {
                    return null;
                }
                
                // 根据diffs判断修改内容中是否有泛型方法
                foreach (var diff in typeDiffs.Values)
                {
                    foreach (var modifiedMethod in diff.ModifiedMethods)
                    {
                        if (!modifiedMethod.IsGenericMethod)
                        {
                            continue;
                        }

                        // 收集泛型方法的调用者
                        foreach (var (typeName, info) in TypeInfoCollector.GetAllTypeInfo())
                        {
                            if (!info.UsedGenericMethods.TryGetValue(modifiedMethod.FullName, out var useGenericInfo))
                            {
                                continue;
                            }

                            // 获取或创建该类型的差异结果
                            if (!typeDiffs.TryGetValue(typeName, out var genericDiff))
                            {
                                genericDiff = new DiffResult();
                                typeDiffs[typeName] = genericDiff;
                            }

                            // 将调用者方法添加到 ModifiedMethods 中
                            DiffAnalyzerHelper.AddCallerMethodsToModified(useGenericInfo, genericDiff, info.FilePath);
                        }
                    }
                }

                // 将部分类加入编译
                foreach (var (typeName, _) in typeDiffs)
                {
                    var info = TypeInfoCollector.GetTypeInfo(typeName);
                    filesToCompile.Add(info.FilePath);
                    foreach (var infoPartialFile in info.PartialFiles)
                    {
                        filesToCompile.Add(infoPartialFile);
                    }
                }
                
                // 将新增过方法、字段的类也默认加入编译
                foreach (var (_, hookTypeInfo) in HookTypeInfoCache)
                {
                    if (!(hookTypeInfo.AddedMethods.Count > 0 || hookTypeInfo.AddedFields.Count > 0))
                    {
                        continue;
                    }

                    foreach (var filePath in hookTypeInfo.SourceFilePaths)
                    {
                        filesToCompile.Add(filePath);
                    }
                }

                foreach (var filePath in filesToCompile)
                {
                    syntaxTrees.Add(filePath, GetSyntaxTree(filePath));
                }
                
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

                // 检查编译文件中是否有调用internal成员
                foreach (var csFilePath in filesToCompile)
                {
                    if (!syntaxTrees.TryGetValue(csFilePath, out var syntaxTree))
                    {
                        continue;
                    }

                    var typeDependency = new TypeDependencyWalker(compilation.GetSemanticModel(syntaxTree));
                    typeDependency.Visit(syntaxTree.GetRoot());
                    foreach (var typeName in typeDependency.GetFullTypeNames())
                    {
                        var info = TypeInfoCollector.GetTypeInfo(typeName);
                        if (info is { IsInternal: true })
                        {
                            compilation = compilation.AddSyntaxTrees(GetSyntaxTree(info.FilePath));
                        }
                    }
                }

                // 执行编译到内存流
                var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (emitResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    _assemblyDefinition = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { ReadWrite = true, InMemory = true });

                    return typeDiffs;
                }

                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                LoggerScoped.LogError("编译失败:" + string.Join("\n", errors.Select(d => d.ToString())));
                return null;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"编译文件时发生异常: {ex}");
                return null;
            }
        }
    }
}
