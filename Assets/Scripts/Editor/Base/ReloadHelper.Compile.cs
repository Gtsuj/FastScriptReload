using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public static bool CompileCsFiles(List<string> csFilePaths)
        {
            try
            {
                // 使用TypeSourceIndex.GetTypeToFilesMap to filesToCompile
                var filesToCompile = TypeSourceIndex.GetTypeToFilesMap(csFilePaths).Values
                    .SelectMany(files => files)
                    .ToHashSet();

                filesToCompile.UnionWith(HookTypeInfoCache.Values
                    .Where(hookTypeInfo => hookTypeInfo.AddedMethods.Count > 0)
                    .SelectMany(hookTypeInfo => hookTypeInfo.SourceFilePaths));

                var syntaxTrees = new List<SyntaxTree>();
                var allParseDiagnostics = new List<Diagnostic>();

                foreach (var csFilePath in filesToCompile)
                {
                    var syntaxTree = GetSyntaxTree(csFilePath);
                    if (syntaxTree == null)
                    {
                        LoggerScoped.LogError($"无法解析文件: {csFilePath}");
                        continue;
                    }

                    syntaxTrees.Add(syntaxTree);

                    // 收集解析错误
                    var parseDiagnostics = syntaxTree.GetDiagnostics()
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToList();
                    allParseDiagnostics.AddRange(parseDiagnostics);
                }

                // 检查语法错误
                if (allParseDiagnostics.Any())
                {
                    LoggerScoped.LogError($"源代码解析错误:{string.Join("\n", allParseDiagnostics.Select(d => d.ToString()))}");
                    return false;
                }

                var references = GetOrResolveAssemblyReferences();

                // 生成唯一程序集名称（使用GUID）
                var uniqueGuid = Guid.NewGuid().ToString("N");
                var assemblyName = uniqueGuid;

                // 创建编译（合并所有文件到一个程序集）
                var compilation = CSharpCompilation.Create(
                    assemblyName: assemblyName,
                    syntaxTrees: syntaxTrees,
                    references: references,
                    options: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Debug,
                        allowUnsafe: true
                    )
                );

                // 执行编译到内存流
                var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (emitResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    _assemblyDefinition = AssemblyDefinition.ReadAssembly(ms, new ReaderParameters { ReadWrite = true, InMemory = true });

                    return true;
                }

                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                LoggerScoped.LogError("编译失败:" + string.Join("\n", errors.Select(d => d.ToString())));
                return false;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"编译文件时发生异常: {ex}");
                return false;
            }
        }
    }
}
