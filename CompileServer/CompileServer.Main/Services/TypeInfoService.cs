using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using CompileServer.Models;
using HookInfo.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace CompileServer.Services
{
    /// <summary>
    /// 类型信息服务 - 管理编译、语法分析和方法调用图
    /// </summary>
    public class TypeInfoService
    {
        private readonly ILogger<TypeInfoService> _logger;
        private readonly string _projectPath;
        
        public static readonly EmitOptions EMIT_OPTIONS = new(debugInformationFormat: DebugInformationFormat.PortablePdb);
        public static readonly WriterParameters WRITER_PARAMETERS = new() { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() };

        #region 私有字段

        private CSharpParseOptions _parseOptions;

        /// <summary>
        /// Assembly 名称 -> CSharpCompilation 的缓存
        /// </summary>
        private readonly ConcurrentDictionary<string, CSharpCompilation> _assemblyCompilations = new();

        /// <summary>
        /// Assembly 名称 -> AssemblyDefinition 列表的缓存（用于 Mono.Cecil 分析）
        /// </summary>
        private readonly Dictionary<string, List<AssemblyDefinition>> _assemblyDefinitions = new();

        /// <summary>
        /// 方法调用图索引：方法签名 -> 调用者方法信息集合
        /// </summary>
        private readonly ConcurrentDictionary<string, Dictionary<string, GenericMethodCallInfo>> _methodCallGraph = new();

        /// <summary>
        /// 文件路径 -> Assembly 名称
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _fileToAssembly = new();

        /// <summary>
        /// 文件路径 -> FileSnapshot 的缓存
        /// </summary>
        private readonly ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();

        /// <summary>
        /// Assembly 解析器缓存
        /// </summary>
        private DefaultAssemblyResolver _assemblyResolver;

        /// <summary>
        /// 所有非动态生成程序集中的类型缓存（类型全名 -> TypeDefinition）
        /// 用于在 IL 修改时查找原类型引用，替代 Unity 端的 ProjectTypeCache
        /// </summary>
        private readonly ConcurrentDictionary<string, TypeDefinition> _allTypesInNonDynamicGeneratedAssemblies = new();

        #endregion

        public TypeInfoService(ILogger<TypeInfoService> logger, string projectPath)
        {
            _logger = logger;
            _projectPath = projectPath;
        }

        #region 公共接口

        /// <summary>
        /// 使用 AssemblyContext 初始化（从 Unity 传递过来）
        /// </summary>
        public async Task Initialize(Dictionary<string, AssemblyContext> assemblyContexts, string[] preprocessorDefines)
        {
            // 清除之前的结果
            Clear();

            _parseOptions = new CSharpParseOptions(
                preprocessorSymbols: preprocessorDefines,
                languageVersion: LanguageVersion.Latest
            );

            // 创建 AssemblyResolver
            _assemblyResolver = new DefaultAssemblyResolver();
            _assemblyResolver.AddSearchDirectory(ReloadHelper.BaseDllPath);

            // 收集所有唯一的引用程序集路径
            var referencePaths = new HashSet<string>();
            foreach (var context in assemblyContexts.Values)
            {
                // 收集所有引用程序集路径
                foreach (var reference in context.References)
                {
                    if (string.IsNullOrEmpty(reference.Path) || !File.Exists(reference.Path))
                        continue;

                    referencePaths.Add(reference.Path);
                }
            }

            // 将所有引用程序集拷贝到BaseDLL目录
            foreach (var referencePath in referencePaths)
            {
                try
                {
                    var referenceName = Path.GetFileNameWithoutExtension(referencePath);
                    CopyAssemblyToBaseDll(referencePath, referenceName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"拷贝引用程序集 {referencePath} 到 BaseDLL 目录时出错: {ex.Message}");
                }
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 并行构建所有程序集的编译和调用图
            var tasks = new List<Task>();
            foreach (var context in assemblyContexts.Values)
            {
                try
                {
                    // 先加载 AssemblyDefinition
                    if (string.IsNullOrEmpty(context.OutputPath) || !File.Exists(context.OutputPath))
                        continue;

                    // 将DLL和PDB文件拷贝到BaseDLL目录
                    var baseDllPath = CopyAssemblyToBaseDll(context.OutputPath, context.Name);
                    if (string.IsNullOrEmpty(baseDllPath))
                    {
                        _logger.LogWarning($"无法拷贝程序集 {context.Name} 到 BaseDLL 目录");
                        continue;
                    }

                    var readerParams = new ReaderParameters
                    {
                        ReadWrite = true,
                        InMemory = true,
                        ReadSymbols = true,
                        SymbolReaderProvider = new PortablePdbReaderProvider(),
                        AssemblyResolver = _assemblyResolver
                    };

                    var assemblyDef = AssemblyDefinition.ReadAssembly(baseDllPath, readerParams);

                    var contextCopy = context;
                    var assemblyDefCopy = assemblyDef;
                    tasks.Add(Task.Run(async () =>
                    {
                        await Task.WhenAll(
                            BuildAssemblyCompilation(contextCopy),
                            BuildMethodCallGraph(assemblyDefCopy)
                        );
                    }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"处理程序集 {context.Name} 时出错: {ex.Message}");
                }
            }

            await Task.WhenAll(tasks);

            // 构建类型缓存（从所有已加载的 AssemblyDefinition 中收集类型）
            BuildTypeCache();

            stopwatch.Stop();
            _logger.LogInformation($"TypeInfo 初始化完成，耗时: {stopwatch.ElapsedMilliseconds}ms, " +
                                   $"程序集数: {_assemblyCompilations.Count}, " +
                                   $"方法调用关系数: {_methodCallGraph.Count}, " +
                                   $"类型缓存数: {_allTypesInNonDynamicGeneratedAssemblies.Count}");
        }

        /// <summary>
        /// 获取程序集的 CSharpCompilation
        /// </summary>
        public CSharpCompilation GetCompilation(string assemblyName)
        {
            return _assemblyCompilations.GetValueOrDefault(assemblyName);
        }

        /// <summary>
        /// 根据文件路径获取所属的 Assembly 名称
        /// </summary>
        public string GetAssemblyName(string filePath)
        {
            return _fileToAssembly.GetValueOrDefault(filePath);
        }

        /// <summary>
        /// 增量更新程序集（只更新改动的文件）
        /// </summary>
        public void UpdateSyntaxTrees(string assemblyName, List<string> changedFiles)
        {
            if (!_assemblyCompilations.TryGetValue(assemblyName, out var compilation))
            {
                _logger.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
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
                    if (fileSnapshot.SyntaxTree != null)
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
            }

            _assemblyCompilations[assemblyName] = compilation;
        }

        /// <summary>
        /// 根据 DiffResults 更新方法调用图
        /// </summary>
        public void UpdateMethodCallGraph(Dictionary<string, DiffResult> diffResults)
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
                    if (callers.ContainsKey(modifyMethod.MethodDefinition.FullName))
                        callers.Remove(modifyMethod.MethodDefinition.FullName);
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
        public Dictionary<string, GenericMethodCallInfo> FindMethodCallers(string methodFullName)
        {
            var callers = _methodCallGraph.GetValueOrDefault(methodFullName);
            return callers != null ? new Dictionary<string, GenericMethodCallInfo>(callers) : null;
        }

        /// <summary>
        /// 获取原类型定义（从类型缓存中查找）
        /// 替代 Unity 端的 ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies
        /// </summary>
        /// <param name="typeFullName">类型全名</param>
        /// <returns>TypeDefinition，如果未找到则返回 null</returns>
        public TypeDefinition GetOriginalTypeDefinition(string typeFullName)
        {
            _allTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeFullName, out var typeDef);
            return typeDef;
        }

        /// <summary>
        /// 根据文件路径列表读取缓存的语法树并返回其中定义的类型
        /// </summary>
        public HashSet<string> GetTypesFromFiles(IEnumerable<string> filePaths)
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
        public AssemblyDefinition GetAssemblyDefinition(string assemblyName, int index)
        {
            var list = _assemblyDefinitions.GetValueOrDefault(assemblyName);
            if (list == null || list.Count == 0)
                return null;

            index = index < 0 ? list.Count + index : index;
            if (index < 0 || index >= list.Count)
                return null;

            return list[index];
        }

        public void AddAssemblyDefinition(string assemblyName, AssemblyDefinition assemblyDef)
        {
            var list = _assemblyDefinitions.GetValueOrDefault(assemblyName, new List<AssemblyDefinition>());
            list.Add(assemblyDef);
        }

        public AssemblyDefinition CloneAssemblyDefinition(string assemblyName)
        {
            var sourceAssembly = GetAssemblyDefinition(assemblyName, -1);
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
                InMemory = true,
                ReadSymbols = true,
                SymbolReaderProvider = new PortablePdbReaderProvider(),
                AssemblyResolver = _assemblyResolver,
                SymbolStream = pdbMs
            };

            var assemblyDef = AssemblyDefinition.ReadAssembly(ms, readerParams);
            return assemblyDef;
        }

        /// <summary>
        /// 编译程序集并返回 AssemblyDefinition
        /// </summary>
        public AssemblyDefinition Compile(string assemblyName, bool isModifyName = true)
        {
            var compilation = GetCompilation(assemblyName);
            if (compilation == null)
            {
                _logger.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
                return null;
            }

            var ms = new MemoryStream();
            var pdbMs = new MemoryStream();
            var emitResult = compilation.Emit(ms, pdbMs, options: EMIT_OPTIONS);

            if (emitResult.Success)
            {
                ms.Seek(0, SeekOrigin.Begin);
                pdbMs.Seek(0, SeekOrigin.Begin);

                var readerParams = new ReaderParameters
                {
                    ReadWrite = true,
                    InMemory = true,
                    ReadSymbols = true,
                    SymbolReaderProvider = new PortablePdbReaderProvider(),
                    AssemblyResolver = _assemblyResolver,
                    SymbolStream = pdbMs
                };

                var assemblyDef = AssemblyDefinition.ReadAssembly(ms, readerParams);
                if (isModifyName)
                {
                    var assemblyDefName = $"{assemblyName}_{Guid.NewGuid()}";
                    assemblyDef.Name.Name = assemblyDefName;
                    assemblyDef.MainModule.Name = assemblyDefName;
                }

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
            return (methodRef.DeclaringType.FullName.Contains("System.Runtime.CompilerServices.AsyncTaskMethodBuilder")
                    || methodRef.DeclaringType.FullName.Contains("Cysharp.Threading.Tasks.CompilerServices.AsyncUniTaskMethodBuilder"))
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
        private void Clear()
        {
            // 释放AssemblyDefinition资源
            foreach (var assemblyDefList in _assemblyDefinitions.Values)
            {
                foreach (var assemblyDef in assemblyDefList)
                {
                    assemblyDef?.Dispose();
                }
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
            
            _logger.LogDebug("已清除所有缓存和临时文件");
        }

        /// <summary>
        /// 将程序集DLL和PDB文件拷贝到BaseDLL目录
        /// </summary>
        /// <param name="originalAssemblyPath">原始程序集路径</param>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>拷贝后的程序集路径，如果失败则返回null</returns>
        private string CopyAssemblyToBaseDll(string originalAssemblyPath, string assemblyName)
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

                    _logger.LogDebug($"已拷贝程序集 {assemblyName} 到 BaseDLL 目录: {targetDllPath}");
                }

                return targetDllPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"拷贝程序集 {assemblyName} 到 BaseDLL 目录时出错: {ex.Message}");
                return null;
            }
        }

        private SyntaxTree GetSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(content))
                return null;

            return CSharpSyntaxTree.ParseText(content, _parseOptions, path: filePath, encoding: Encoding.UTF8);
        }

        private FileSnapshot GetOrCreateFileSnapshot(string filePath)
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
        /// 为程序集构建 CSharpCompilation 并缓存
        /// </summary>
        private async Task BuildAssemblyCompilation(AssemblyContext assemblyContext)
        {
            var syntaxTrees = new ConcurrentBag<SyntaxTree>();

            var tasks = new List<Task>();
            foreach (var sourceFile in assemblyContext.SourceFiles)
            {
                tasks.Add(Task.Run(() =>
                {
                    var fullPath = Path.IsPathRooted(sourceFile)
                        ? sourceFile
                        : Path.GetFullPath(Path.Combine(_projectPath, sourceFile));

                    if (!File.Exists(fullPath))
                        return;

                    var syntaxTree = GetOrCreateFileSnapshot(fullPath)?.SyntaxTree;
                    if (syntaxTree != null)
                    {
                        syntaxTrees.Add(syntaxTree);
                        _fileToAssembly[fullPath] = assemblyContext.Name;
                    }
                }));
            }

            await Task.WhenAll(tasks);

            var syntaxTreesList = syntaxTrees.ToList();
            if (syntaxTreesList.Count == 0)
                return;

            // 构建引用
            var references = new List<MetadataReference>();
            foreach (var reference in assemblyContext.References)
            {
                if (!string.IsNullOrEmpty(reference.Path) && File.Exists(reference.Path))
                {
                    references.Add(MetadataReference.CreateFromFile(reference.Path));
                }
            }

            // 创建 CSharpCompilation
            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyContext.Name,
                syntaxTrees: syntaxTreesList,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: assemblyContext.AllowUnsafeCode,
                    concurrentBuild: true,
                    deterministic: false
                )
            );

            _assemblyCompilations[assemblyContext.Name] = compilation;
            
            var assemblyDef = Compile(assemblyContext.Name, false);
            _assemblyDefinitions[assemblyContext.Name] = [assemblyDef];
        }

        /// <summary>
        /// 使用 Mono.Cecil 构建方法调用图
        /// </summary>
        private async Task BuildMethodCallGraph(AssemblyDefinition assemblyDef)
        {
            var tasks = new List<Task>();
            foreach (var type in assemblyDef.MainModule.Types)
            {
                tasks.Add(Task.Run(() => AnalyzeTypeForMethodCalls(type)));
            }

            await Task.WhenAll(tasks);
        }

        private void AnalyzeTypeForMethodCalls(TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                AnalyzeMethodForMethodCalls(method);
            }
        }

        private void AnalyzeMethodForMethodCalls(MethodDefinition methodDef)
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
                            callers = new Dictionary<string, GenericMethodCallInfo>();
                            _methodCallGraph[calledMethodName] = callers;
                        }

                        if (!callers.ContainsKey(methodDef.FullName))
                        {
                            callers.Add(methodDef.FullName, new GenericMethodCallInfo(methodDef));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 构建类型缓存：从所有已加载的 AssemblyDefinition 中收集类型
        /// 替代 Unity 端的 ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies
        /// </summary>
        private void BuildTypeCache()
        {
            foreach (var (assemblyName, assemblyDefList) in _assemblyDefinitions)
            {
                foreach (var assemblyDef in assemblyDefList)
                {
                    if (assemblyDef == null || assemblyDef.MainModule == null)
                        continue;

                    // 遍历程序集中的所有类型（包括嵌套类型）
                    CollectTypesFromAssembly(assemblyDef.MainModule.Types);
                }
            }
        }

        /// <summary>
        /// 递归收集类型（包括嵌套类型）
        /// </summary>
        private void CollectTypesFromAssembly(Collection<TypeDefinition> types)
        {
            foreach (var typeDef in types)
            {
                // 跳过编译器生成的类型
                if (IsCompilerGeneratedType(typeDef))
                    continue;

                var typeFullName = typeDef.FullName;

                // 如果已存在，跳过（保留第一个）
                if (!_allTypesInNonDynamicGeneratedAssemblies.ContainsKey(typeFullName))
                {
                    _allTypesInNonDynamicGeneratedAssemblies[typeFullName] = typeDef;
                }

                // 递归处理嵌套类型
                if (typeDef.HasNestedTypes)
                {
                    CollectTypesFromAssembly(typeDef.NestedTypes);
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
