using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using CompileServer.Models;
using CompileServer.Services;
using HookInfo.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace CompileServer.Helper
{
    /// <summary>
    /// 类型信息辅助类 - 管理编译、语法分析和方法调用图
    /// </summary>
    public static class TypeInfoHelper
    {
        public static readonly EmitOptions EMIT_OPTIONS = new(debugInformationFormat: DebugInformationFormat.PortablePdb);

        public static readonly WriterParameters WRITER_PARAMETERS = new() { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() };

        #region 私有字段

        private static CSharpParseOptions _parseOptions;

        private static Dictionary<string, AssemblyContext> _assemblyContext;

        /// <summary>
        /// Assembly 解析器缓存
        /// </summary>
        private static DefaultAssemblyResolver _assemblyResolver;

        /// <summary>
        /// Assembly 名称 -> CSharpCompilation 的缓存
        /// </summary>
        private static readonly ConcurrentDictionary<string, CSharpCompilation> _assemblyCompilations = new();

        /// <summary>
        /// Assembly 名称 -> AssemblyDefinition 的缓存（用于 Mono.Cecil 分析）
        /// </summary>
        private static readonly Dictionary<string, AssemblyDefinition> _assemblyDefinitions = new();

        /// <summary>
        /// 方法调用图索引：方法签名 -> 调用者方法信息集合
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GenericMethodCallInfo>> _methodCallGraph = new(Environment.ProcessorCount, 1000);

        /// <summary>
        /// 文件路径 -> Assembly 名称
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _fileToAssembly = new();

        /// <summary>
        /// 文件路径 -> FileSnapshot 的缓存
        /// </summary>
        private static readonly ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();

        /// <summary>
        /// 所有非动态生成程序集中的类型缓存（类型全名 -> TypeDefinition）
        /// 用于在 IL 修改时查找原类型引用，替代 Unity 端的 ProjectTypeCache
        /// </summary>
        private static readonly ConcurrentDictionary<string, TypeReference> _allTypesInNonDynamicGeneratedAssemblies = new();

        #endregion

        #region 公共接口

        /// <summary>
        /// 使用 AssemblyContext 初始化（从 Unity 传递过来）
        /// </summary>
        public static async Task Initialize(Dictionary<string, AssemblyContext> assemblyContexts, string[] preprocessorDefines)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _assemblyContext = assemblyContexts;

            // 清除之前的结果
            Clear();

            _parseOptions = new CSharpParseOptions(
                preprocessorSymbols: preprocessorDefines,
                languageVersion: LanguageVersion.Latest
            );

            // 创建 AssemblyResolver
            _assemblyResolver = new DefaultAssemblyResolver();
            _assemblyResolver.AddSearchDirectory(ReloadHelper.BaseDllPath);

            // 将所有程序集及其引用拷贝到BaseDLL目录，并更新上下文中的路径
            CopyAssembliesToBaseDll();

            foreach (var context in assemblyContexts.Values)
            {
                try
                {
                    if (string.IsNullOrEmpty(context.OutputPath) || !File.Exists(context.OutputPath))
                    {
                        Console.WriteLine($"程序集 {context.Name} 的输出路径无效: {context.OutputPath}");
                        continue;
                    }

                    // 文件到程序集的映射缓存
                    foreach (var sourceFile in context.SourceFiles)
                    {
                        _fileToAssembly[sourceFile] = context.Name;
                    }

                    var assemblyDef = AssemblyDefinition.ReadAssembly(context.OutputPath);

                    await BuildMethodCallGraph(assemblyDef);

                    assemblyDef?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理程序集 {context.Name} 时出错: {ex.Message}");
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"TypeInfo 初始化完成，耗时: {stopwatch.ElapsedMilliseconds}ms, " +
                                   $"方法调用关系数: {_methodCallGraph.Count}, ");
        }

        /// <summary>
        /// 获取或构建程序集的编译缓存
        /// </summary>
        public static async Task<CSharpCompilation> GetOrBuildCompilation(string assemblyName)
        {
            if (_assemblyCompilations.TryGetValue(assemblyName, out var compilation))
            {
                return compilation;
            }

            if (_assemblyContext == null || !_assemblyContext.TryGetValue(assemblyName, out var context))
            {
                return null;
            }

            var syntaxTreesBag = new ConcurrentBag<SyntaxTree>();
            var parseTasks = context.SourceFiles.Select(sourceFile => Task.Run(() =>
            {
                if (File.Exists(sourceFile))
                {
                    var syntaxTree = GetOrCreateFileSnapshot(sourceFile)?.SyntaxTree;
                    if (syntaxTree != null)
                    {
                        syntaxTreesBag.Add(syntaxTree);
                    }
                }
            })).ToArray();

            await Task.WhenAll(parseTasks);

            var references = new List<MetadataReference>();
            foreach (var reference in context.References)
            {
                if (!string.IsNullOrEmpty(reference.Path) && File.Exists(reference.Path))
                {
                    references.Add(MetadataReference.CreateFromFile(reference.Path));
                }
            }

            compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: syntaxTreesBag.ToList(),
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: context.AllowUnsafeCode,
                    concurrentBuild: true,
                    deterministic: true
                )
            );

            _assemblyCompilations[assemblyName] = compilation;
            AddAssemblyDefinition(assemblyName, AssemblyDefinition.ReadAssembly(_assemblyContext[assemblyName].OutputPath));

            return compilation;
        }

        /// <summary>
        /// 根据文件路径获取所属的 Assembly 名称
        /// </summary>
        public static string GetAssemblyName(string filePath)
        {
            return _fileToAssembly.GetValueOrDefault(filePath);
        }

        /// <summary>
        /// 增量更新程序集（只更新改动的文件）
        /// </summary>
        public static async Task UpdateSyntaxTrees(string assemblyName, List<string> changedFiles)
        {
            var compilation = await GetOrBuildCompilation(assemblyName);
            if (compilation == null)
            {
                return;
            }

            foreach (var filePath in changedFiles)
            {
                // 更新文件快照
                var fileSnapshot = _fileSnapshots.GetValueOrDefault(filePath) ?? new FileSnapshot(filePath);
                var newSyntaxTree = GetSyntaxTree(filePath);

                // 更新 CSharpCompilation 中的 SyntaxTree
                if (newSyntaxTree != null)
                {
                    if (fileSnapshot.SyntaxTree != null && compilation.SyntaxTrees.Contains(fileSnapshot.SyntaxTree))
                    {
                        compilation = compilation.ReplaceSyntaxTree(fileSnapshot.SyntaxTree, newSyntaxTree);
                    }
                    else
                    {
                        // 如果是新文件，添加到编译中
                        compilation = compilation.AddSyntaxTrees(newSyntaxTree);
                    }
                }

                fileSnapshot.SyntaxTree = newSyntaxTree;
                _fileSnapshots[filePath] = fileSnapshot;
            }

            _assemblyCompilations[assemblyName] = compilation;
        }

        /// <summary>
        /// 根据 DiffResults 更新方法调用图
        /// </summary>
        public static void UpdateMethodCallGraph(Dictionary<string, DiffResult> diffResults)
        {
            if (diffResults == null || diffResults.Count == 0)
                return;

            // 步骤1：收集所有 Added 和 Modified 的方法
            var modifyMethods = diffResults.Values
                .SelectMany(diffResult => diffResult.ModifiedMethods.Values).ToArray();

            if (modifyMethods.Length == 0)
                return;

            // 步骤2：清理 methodGraph 中 methodsToUpdate 中的方法作为调用者的关系
            foreach (var (calledMethodName, callers) in _methodCallGraph)
            {
                foreach (var modifyMethod in modifyMethods)
                {
                    callers.TryRemove(modifyMethod.MethodDefinition.FullName, out _);
                }
            }

            // 步骤3：遍历 Added 和 Modified 中方法的 IL 指令来更新 methodGraph
            foreach (var modifyMethod in modifyMethods)
            {
                var methodDef = modifyMethod.MethodDefinition;
                AnalyzeMethodForMethodCalls(methodDef);
            }
        }

        /// <summary>
        /// 查找方法的调用者
        /// </summary>
        public static Dictionary<string, GenericMethodCallInfo> FindMethodCallers(string methodFullName)
        {
            var callers = _methodCallGraph.GetValueOrDefault(methodFullName);
            return callers != null ? new Dictionary<string, GenericMethodCallInfo>(callers) : null;
        }

        /// <summary>
        /// 获取原类型定义
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns>TypeDefinition，如果未找到则返回 null</returns>
        public static TypeReference GetOriginalTypeDefinition(TypeReference typeRef)
        {
            var assemblyName = typeRef.Scope.Name.Split("---")[0];
            var typeFullName = typeRef.FullName;

            if (_allTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeFullName, out var originalTypeDef))
            {
                return originalTypeDef;
            }

            try
            {
                var context = _assemblyContext.GetValueOrDefault(assemblyName);
                var assemblyDef = AssemblyDefinition.ReadAssembly(context.OutputPath);

                var typeDef = assemblyDef.MainModule.GetType(typeFullName);
                if (typeDef == null)
                {
                    throw new Exception($"类型 {typeFullName} 未在程序集 {assemblyName} 中找到");
                }

                _allTypesInNonDynamicGeneratedAssemblies[typeFullName] = typeDef;

                assemblyDef.Dispose();

                return typeDef;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"查找类型 {assemblyName}:{typeFullName} 时出错: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 根据文件路径列表读取缓存的语法树并返回其中定义的类型
        /// </summary>
        public static HashSet<string> GetTypesFromFiles(IEnumerable<string> filePaths)
        {
            var types = new HashSet<string>();

            foreach (var filePath in filePaths)
            {
                if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
                {
                    fileSnapshot = GetOrCreateFileSnapshot(filePath);
                }

                if (fileSnapshot?.SyntaxTree == null)
                    continue;

                var root = fileSnapshot.SyntaxTree.GetRoot();
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var typeFullName = GetTypeFullName(typeDecl);
                    if (!string.IsNullOrEmpty(typeFullName))
                    {
                        types.Add(typeFullName);
                    }
                }
            }

            return types;
        }

        /// <summary>
        /// 获取程序集的 AssemblyDefinition
        /// </summary>
        public static AssemblyDefinition GetAssemblyDefinition(string assemblyName)
        {
            return _assemblyDefinitions.GetValueOrDefault(assemblyName);
        }

        public static void AddAssemblyDefinition(string assemblyName, AssemblyDefinition assemblyDef)
        {
            // 如果已存在旧的 AssemblyDefinition，先释放它
            if (_assemblyDefinitions.TryGetValue(assemblyName, out var oldAssemblyDef))
            {
                oldAssemblyDef?.Dispose();
            }

            // 保存最新的 AssemblyDefinition
            _assemblyDefinitions[assemblyName] = assemblyDef;
        }

        public static AssemblyDefinition CloneAssemblyDefinition(string assemblyName)
        {
            var sourceAssembly = GetAssemblyDefinition(assemblyName);
            if (sourceAssembly == null)
                return null;

            var ms = new MemoryStream();
            var pdbMs = new MemoryStream();

            WRITER_PARAMETERS.SymbolStream = pdbMs;
            sourceAssembly.Write(ms, WRITER_PARAMETERS);
            WRITER_PARAMETERS.SymbolStream = null;

            ms.Seek(0, SeekOrigin.Begin);
            pdbMs.Seek(0, SeekOrigin.Begin);

            var readerParams = new ReaderParameters
            {
                ReadWrite = true,
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                AssemblyResolver = _assemblyResolver,
                SymbolStream = pdbMs
            };

            var assemblyDef = AssemblyDefinition.ReadAssembly(ms, readerParams);

            var assemblyDefName = $"{assemblyDef.Name.Name}---{Guid.NewGuid()}";
            assemblyDef.Name.Name = assemblyDefName;
            assemblyDef.MainModule.Name = assemblyDefName;

            return assemblyDef;
        }

        /// <summary>
        /// 编译程序集并返回 AssemblyDefinition
        /// </summary>
        public static async Task<AssemblyDefinition> Compile(string assemblyName)
        {
            var compilation = await GetOrBuildCompilation(assemblyName);
            if (compilation == null)
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            
            var ms = new MemoryStream();
            var pdbMs = new MemoryStream();
            var emitResult = compilation.Emit(ms, pdbMs, options: EMIT_OPTIONS);
            
            Console.WriteLine("编译耗时：" + stopwatch.ElapsedMilliseconds + "ms");

            if (emitResult.Success)
            {
                ms.Seek(0, SeekOrigin.Begin);
                pdbMs.Seek(0, SeekOrigin.Begin);

                var readerParams = new ReaderParameters
                {
                    ReadWrite = true,
                    ReadSymbols = true,
                    SymbolReaderProvider = new PortablePdbReaderProvider(),
                    AssemblyResolver = _assemblyResolver,
                    SymbolStream = pdbMs
                };

                var assemblyDef = AssemblyDefinition.ReadAssembly(ms, readerParams);

                return assemblyDef;
            }

            var errorMsg = string.Join("\n", emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));

            throw new Exception($"程序集 {assemblyName} 编译失败: {errorMsg}");
        }

        /// <summary>
        /// 检查是否是Task调用
        /// </summary>
        public static bool IsTaskCallStartMethod(MethodReference methodRef)
        {
            return (methodRef.DeclaringType.Name.Contains("AsyncTaskMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncVoidMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncValueTaskMethodBuilder"))
                    || methodRef.DeclaringType.Name.Contains("AsyncUniTaskMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncUniTaskVoidMethodBuilder")
                   && methodRef.Name.Equals("Start");
        }

        /// <summary>
        /// 查找Task调用的方法
        /// </summary>
        public static MethodDefinition FindTaskCallMethod(MethodReference methodRef)
        {
            TypeDefinition stateMachineType = (methodRef as GenericInstanceMethod)?.GenericArguments[0] as TypeDefinition;

            if (stateMachineType == null || !stateMachineType.HasMethods)
                return null;

            return stateMachineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        }

        /// <summary>
        /// 检查类型是否是Task状态机
        /// </summary>
        public static bool TypeIsTaskStateMachine(TypeDefinition typeDef)
        {
            return typeDef.Interfaces.Any(implementation =>
                implementation.InterfaceType.FullName.Equals("System.Runtime.CompilerServices.IAsyncStateMachine"));
        }

        /// <summary>
        /// 检查类型是否是编译器生成的类型
        /// </summary>
        public static bool IsCompilerGeneratedType(TypeDefinition typeDef)
        {
            if (typeDef == null || !typeDef.IsNested)
                return false;

            return typeDef.CustomAttributes.Any(attr =>
                attr.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        #endregion

        #region 私有实现方法

        /// <summary>
        /// 清除所有缓存和状态
        /// </summary>
        private static void Clear()
        {
            // 释放AssemblyDefinition资源
            foreach (var assemblyDef in _assemblyDefinitions.Values)
            {
                assemblyDef?.Dispose();
            }

            // 清除所有缓存
            _assemblyCompilations.Clear();
            _assemblyDefinitions.Clear();
            _methodCallGraph.Clear();
            _fileToAssembly.Clear();
            _fileSnapshots.Clear();
            _allTypesInNonDynamicGeneratedAssemblies.Clear();

            // 清除ReloadHelper的缓存和临时文件
            ReloadHelper.Clear();

            Console.WriteLine("已清除所有缓存和临时文件");
        }

        /// <summary>
        /// 将所有程序集及其引用拷贝到BaseDLL目录，并更新 _assemblyContext 中的路径
        /// </summary>
        private static void CopyAssembliesToBaseDll()
        {
            if (_assemblyContext == null) return;

            foreach (var context in _assemblyContext.Values)
            {
                // 1. 拷贝并更新所有引用程序集路径
                foreach (var reference in context.References)
                {
                    if (string.IsNullOrEmpty(reference.Path) || !File.Exists(reference.Path))
                        continue;

                    var targetPath = CopyAssemblyToBaseDll(reference.Path, Path.GetFileNameWithoutExtension(reference.Path));
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        reference.Path = targetPath;
                    }
                }

                // 2. 拷贝并更新主程序集自身路径
                if (!string.IsNullOrEmpty(context.OutputPath) && File.Exists(context.OutputPath))
                {
                    var targetOutputPath = CopyAssemblyToBaseDll(context.OutputPath, context.Name);
                    if (!string.IsNullOrEmpty(targetOutputPath))
                    {
                        context.OutputPath = targetOutputPath;
                    }
                }
            }
        }

        /// <summary>
        /// 将程序集DLL和PDB文件拷贝到BaseDLL目录
        /// </summary>
        /// <param name="originalAssemblyPath">原始程序集路径</param>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>拷贝后的程序集路径，如果失败则返回null</returns>
        private static string CopyAssemblyToBaseDll(string originalAssemblyPath, string assemblyName)
        {
            try
            {
                var baseDllPath = ReloadHelper.BaseDllPath;
                var dllFileName = Path.GetFileName(originalAssemblyPath);
                var targetDllPath = Path.Combine(baseDllPath, dllFileName);

                // 检查是否需要更新拷贝（通过文件修改时间比较）
                bool needCopy = true;
                if (File.Exists(targetDllPath))
                {
                    var originalDllTime = File.GetLastWriteTime(originalAssemblyPath);
                    var targetDllTime = File.GetLastWriteTime(targetDllPath);
                    if (originalDllTime <= targetDllTime)
                    {
                        needCopy = false;
                    }
                }

                if (needCopy)
                {
                    // 拷贝DLL文件
                    File.Copy(originalAssemblyPath, targetDllPath, overwrite: true);

                    // 拷贝PDB文件（如果存在）
                    var pdbPath = Path.ChangeExtension(originalAssemblyPath, ".pdb");
                    if (File.Exists(pdbPath))
                    {
                        var targetPdbPath = Path.ChangeExtension(targetDllPath, ".pdb");
                        File.Copy(pdbPath, targetPdbPath, overwrite: true);
                    }

                    Console.WriteLine($"已拷贝程序集 {assemblyName} 到 BaseDLL 目录: {targetDllPath}");
                }

                return targetDllPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"拷贝程序集 {assemblyName} 到 BaseDLL 目录时出错: {ex.Message}");
                return null;
            }
        }

        private static SyntaxTree GetSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(content))
                return null;

            return CSharpSyntaxTree.ParseText(content, _parseOptions, path: filePath, encoding: Encoding.UTF8);
        }

        private static FileSnapshot GetOrCreateFileSnapshot(string filePath)
        {
            if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
            {
                var syntaxTree = GetSyntaxTree(filePath);
                if (syntaxTree != null)
                {
                    fileSnapshot = new FileSnapshot(filePath)
                    {
                        SyntaxTree = syntaxTree,
                    };
                    _fileSnapshots[filePath] = fileSnapshot;
                }
            }

            return fileSnapshot;
        }

        /// <summary>
        /// 使用 Mono.Cecil 构建方法调用图
        /// </summary>
        private static async Task BuildMethodCallGraph(AssemblyDefinition assemblyDef)
        {
            var tasks = new List<Task>();
            foreach (var type in assemblyDef.MainModule.Types)
            {
                tasks.Add(Task.Run(() =>
                {
                    foreach (var method in type.Methods)
                    {
                        AnalyzeMethodForMethodCalls(method);
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AnalyzeMethodForMethodCalls(MethodDefinition methodDef)
        {
            if (!methodDef.HasBody)
                return;

            foreach (var instruction in methodDef.Body.Instructions)
            {
                if (instruction.OpCode.Code == Code.Call ||
                    instruction.OpCode.Code == Code.Callvirt ||
                    instruction.OpCode.Code == Code.Calli ||
                    instruction.OpCode.Code == Code.Newobj)
                {
                    if (instruction.Operand is GenericInstanceMethod calledMethodRef)
                    {
                        // 过滤掉系统方法
                        if (calledMethodRef.DeclaringType.Scope.Name.Contains("System") ||
                            calledMethodRef.DeclaringType.Scope.Name.Contains("UnityEngine"))
                        {
                            continue;
                        }

                        var calledMethodName = calledMethodRef.ElementMethod.FullName;

                        if (!_methodCallGraph.TryGetValue(calledMethodName, out var callers))
                        {
                            callers = new ConcurrentDictionary<string, GenericMethodCallInfo>();
                            _methodCallGraph[calledMethodName] = callers;
                        }

                        if (!callers.ContainsKey(methodDef.FullName))
                        {
                            callers.TryAdd(methodDef.FullName, new GenericMethodCallInfo(methodDef));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
        private static string GetTypeFullName(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl == null)
                return string.Empty;

            var parts = new List<string>();
            var current = typeDecl;

            while (current != null)
            {
                parts.Insert(0, current.Identifier.ValueText);
                current = current.Parent as TypeDeclarationSyntax;
            }

            var namespaceDecl = typeDecl.Parent;
            while (namespaceDecl != null && !(namespaceDecl is BaseNamespaceDeclarationSyntax))
            {
                namespaceDecl = namespaceDecl.Parent;
            }

            var namespaceName = (namespaceDecl as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? string.Empty;

            if (string.IsNullOrEmpty(namespaceName))
            {
                return string.Join(".", parts);
            }

            var builder = new StringBuilder(namespaceName.Length + parts.Count * 20);
            builder.Append(namespaceName);
            builder.Append('.');
            builder.Append(string.Join("/", parts));
            return builder.ToString();
        }

        #endregion
    }
}
