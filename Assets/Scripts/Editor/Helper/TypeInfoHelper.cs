using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using FastScriptReload.Editor;
using ImmersiveVrToolsCommon.Runtime.Logging;
using MessagePack;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FastScriptReload.Editor
{
    public static class TypeInfoHelper
    {
        public static readonly EmitOptions EMIT_OPTIONS = new (debugInformationFormat:DebugInformationFormat.PortablePdb);
        
        public static readonly WriterParameters WRITER_PARAMETERS = new() { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() };

        public static ReaderParameters ReaderParameters = new() { ReadWrite = true, InMemory = true, ReadSymbols = true, SymbolReaderProvider = new PortablePdbReaderProvider() };
        
        /// <summary>
        /// 解析选项
        /// </summary>
        public static CSharpParseOptions ParseOptions;
        
        #region 私有字段

        private static bool _isInitialized;

        /// <summary>
        /// Assembly 名称 -> CSharpCompilation 的缓存
        /// </summary>
        private static readonly ConcurrentDictionary<string, CSharpCompilation> _assemblyCompilations = new();

        /// <summary>
        /// Assembly 名称 -> AssemblyDefinition 列表的缓存（用于 Mono.Cecil 分析）
        /// </summary>
        private static readonly Dictionary<string, List<AssemblyDefinition>> _assemblyDefinitions = new();

        /// <summary>
        /// 方法调用图索引：方法签名 -> 调用者方法信息集合
        /// Key: 被调用方法的完整签名
        /// Value: 调用者方法信息集合（类型名, 方法完整签名）
        /// </summary>
        private static readonly ConcurrentDictionary<string, Dictionary<string, GenericMethodCallInfo>> _methodCallGraph = new();

        /// <summary>
        /// 泛型方法调用图索引：泛型方法签名 -> 调用者方法信息集合
        /// Key: 被调用的泛型方法完整签名（包含类型参数）
        /// Value: 调用者方法信息集合（类型名, 方法完整签名）
        /// 通过 Roslyn 语义分析收集
        /// </summary>
        private static readonly ConcurrentDictionary<string, Dictionary<string, GenericMethodCallInfo>> _genericMethodCallGraph = new();

        /// <summary>
        /// 文件路径 -> Assembly 名称
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _fileToAssembly = new();

        /// <summary>
        /// 文件路径 -> FileSnapshot 的缓存
        /// </summary>
        private static ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();

        /// <summary>
        /// 代码生成器缓存
        /// </summary>
        private static readonly List<IIncrementalGenerator> _sourceGenerators = new();

        /// <summary>
        /// 代码生成器 DLL 文件名(先写死)
        /// </summary>
        private const string SourceGeneratorDllName = "CSharpCodeAnalysis.dll";

        /// <summary>
        /// 类型全名 -> 类型分析结果的缓存
        /// Key: 类型全名
        /// Value: 类型分析结果（Partial文件、Internal成员、泛型方法）
        /// </summary>
        private static readonly ConcurrentDictionary<string, TypeAnalysisResult> _typeAnalysisCache = new();

        #endregion

        #region 公共查询接口

        /// <summary>
        /// 初始化并构建所有索引
        /// </summary>
        public static async void Initialize()
        {
            if (_isInitialized)
                return;

            ParseOptions = new CSharpParseOptions(
                preprocessorSymbols: EditorUserBuildSettings.activeScriptCompilationDefines,
                languageVersion: LanguageVersion.Latest
            );
            
            Deserialize();

            // 加载代码生成器
            LoadSourceGenerators();

            var stopwatch = Stopwatch.StartNew();

            // 获取所有 Unity 程序集
            var allAssemblies = CompilationPipeline.GetAssemblies();

            // 过滤：只处理 Application.dataPath 下的程序集（只要有任意一个文件不在 dataPath 下则排除整个程序集）
            var dataPathAssemblies = allAssemblies
                .Where(assembly => assembly.sourceFiles.All(sourceFile => sourceFile.StartsWith("Assets"))).ToList();

            List<Task> tasks = new();
            foreach (var assembly in dataPathAssemblies)
            {
                try
                {
                    // 先加载 AssemblyDefinition
                    if (string.IsNullOrEmpty(assembly.outputPath) || !File.Exists(assembly.outputPath))
                        continue;

                    var assemblyDef = AssemblyDefinition.ReadAssembly(
                        assembly.outputPath, new() { InMemory = true });

                    _assemblyDefinitions[assembly.name] = new List<AssemblyDefinition> { assemblyDef };

                    var assemblyCopy = assembly;
                    var assemblyDefCopy = assemblyDef;
                    tasks.Add(Task.Run(async () =>
                    {
                        // await Task.WhenAll(BuildAssemblyCompilation(assemblyCopy), BuildMethodCallGraph(assemblyDefCopy));
                        await BuildAssemblyCompilation(assemblyCopy);
                    }));
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogWarning($"处理程序集 {assembly.name} 时出错: {ex.Message}");
                }
                
            }
            await Task.WhenAll(tasks);

            stopwatch.Stop();
            LoggerScoped.LogDebug($"TypeInfo 初始化完成，耗时: {stopwatch.ElapsedMilliseconds}ms, " +
                             $"程序集数: {_assemblyCompilations.Count}, " +
                             $"方法调用关系数: {_methodCallGraph.Count}");

            _isInitialized = true;
        }

        /// <summary>
        /// 获取程序集的 CSharpCompilation
        /// </summary>
        public static CSharpCompilation GetCompilation(string assemblyName)
        {
            return _assemblyCompilations.GetValueOrDefault(assemblyName);
        }

        /// <summary>
        /// 根据文件路径获取所属的 Assembly 名称
        /// </summary>
        public static string GetAssemblyName(string filePath)
        {
            return _fileToAssembly.GetValueOrDefault(filePath);
        }

        /// <summary>
        /// 获取类型的分析结果（从缓存）
        /// </summary>
        /// <param name="typeFullName">类型的全名</param>
        /// <returns>类型分析结果，如果不存在则返回 null</returns>
        public static TypeAnalysisResult GetTypeAnalysisResult(string typeFullName)
        {
            return _typeAnalysisCache.GetValueOrDefault(typeFullName);
        }

        /// <summary>
        /// 根据类型全名递归收集所有依赖的文件
        /// </summary>
        /// <param name="typeFullName">类型全名</param>
        /// <param name="allFiles">收集的所有文件（输出）</param>
        /// <param name="processedTypes">已处理的类型（避免循环依赖）</param>
        private static void CollectDependentFilesRecursive(string typeFullName, HashSet<string> allFiles, HashSet<string> processedTypes)
        {
            // 避免循环依赖
            if (!processedTypes.Add(typeFullName))
                return;

            // 获取类型分析结果
            var analysisResult = GetTypeAnalysisResult(typeFullName);
            if (analysisResult == null)
                return;
            
            // 添加当前类型的所有部分类文件
            foreach (var filePath in analysisResult.FilePaths)
            {
                allFiles.Add(filePath);
            }
            
            // 递归处理 Internal 依赖类型
            foreach (var internalTypeFullName in analysisResult.InternalTypes)
            {
                CollectDependentFilesRecursive(internalTypeFullName, allFiles, processedTypes);
            }
            
            // 递归处理泛型方法依赖类型
            foreach (var genericMethodTypeFullName in analysisResult.GenericMethodTypes)
            {
                CollectDependentFilesRecursive(genericMethodTypeFullName, allFiles, processedTypes);
            }
        }

        /// <summary>
        /// 根据多个文件路径获取所有依赖的文件
        /// </summary>
        /// <param name="filePaths">源文件路径集合</param>
        /// <returns>所有依赖的文件路径集合（包括输入文件）</returns>
        public static HashSet<string> GetAllDependentFiles(List<string> filePaths)
        {
            var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedTypes = new HashSet<string>();
            
            // 获取所有文件中定义的所有类型
            var allTypes = GetTypesFromFiles(filePaths);
            
            // 递归分析每个类型的依赖
            foreach (var typeFullName in allTypes)
            {
                CollectDependentFilesRecursive(typeFullName, allFiles, processedTypes);
            }
            
            return allFiles;
        }

        /// <summary>
        /// 根据改动文件列表进行增量编译并保存
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="changedFiles">改动的文件路径列表</param>
        /// <returns>编译后的 AssemblyDefinition，如果编译失败返回 null</returns>
        public static AssemblyDefinition CompileIncrementalChanges(string assemblyName, List<string> changedFiles)
        {
            var stopwatch = Stopwatch.StartNew();
            
            // 从缓存中收集语法树
            var syntaxTrees = new List<SyntaxTree>();

            foreach (var filePath in changedFiles)
            {
                var fileSnapshot = _fileSnapshots.GetValueOrDefault(filePath);
                if (fileSnapshot?.SyntaxTree != null)
                {
                    syntaxTrees.Add(fileSnapshot.SyntaxTree);
                }
            }
            
            // 获取已有的 Compilation
            if (!_assemblyCompilations.TryGetValue(assemblyName, out var existingCompilation))
            {
                LoggerScoped.LogError($"找不到程序集 {assemblyName} 的 Compilation 缓存，请先调用 BuildAssemblyCompilation");
                return null;
            }

            // 从已有的 Compilation 中复用引用
            var references = existingCompilation.References.ToList();
            
            var existingAssemblyDef = GetAssemblyDefinition(assemblyName, 0);
            references.Add(MetadataReference.CreateFromFile(existingAssemblyDef.MainModule.FileName));

            // 创建 CSharpCompilation
            var compilation = CSharpCompilation.Create(
                assemblyName: assemblyName,
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: existingCompilation.Options.AllowUnsafe,
                    concurrentBuild:true,
                    deterministic:false
                )
            );
            
            // 检查编译错误
            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (diagnostics.Any())
            {
                LoggerScoped.LogError($"程序集 {assemblyName} 编译失败，错误数: {diagnostics.Count}");
                foreach (var diagnostic in diagnostics)
                {
                    LoggerScoped.LogError($"  {diagnostic}");
                }
                return null;
            }
            
            // 编译到内存流
            using var peStream = new MemoryStream();
            using var pdbStream = new MemoryStream();
            
            var emitResult = compilation.Emit(peStream, pdbStream, options: EMIT_OPTIONS);
            
            if (!emitResult.Success)
            {
                LoggerScoped.LogError($"程序集 {assemblyName} Emit 失败");
                foreach (var diagnostic in emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    LoggerScoped.LogError($"  {diagnostic}");
                }
                return null;
            }
            
            // 加载为 AssemblyDefinition
            peStream.Seek(0, SeekOrigin.Begin);
            pdbStream.Seek(0, SeekOrigin.Begin);
            
            var readerParameters = new ReaderParameters
            {
                ReadSymbols = true,
                SymbolStream = pdbStream,
                ReadingMode = ReadingMode.Immediate
            };
            
            var assemblyDef = AssemblyDefinition.ReadAssembly(peStream, readerParameters);
            
            stopwatch.Stop();
            LoggerScoped.LogDebug($"程序集 {assemblyName} 增量编译完成，耗时: {stopwatch.ElapsedMilliseconds}ms");
            
            return assemblyDef;
        }

        /// <summary>
        /// 增量更新程序集（只更新改动的文件）
        /// </summary>
        public static void UpdateSyntaxTrees(List<string> changedFiles)
        {
            foreach (var filePath in changedFiles)
            {
                var assemblyName = _fileToAssembly[filePath];
                if (!_assemblyCompilations.TryGetValue(assemblyName, out var compilation))
                {
                    LoggerScoped.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
                    return;
                }
                // 更新文件快照
                var fileSnapshot = _fileSnapshots.GetValueOrDefault(filePath) ?? new FileSnapshot(filePath);
                var oldTree = fileSnapshot.SyntaxTree;
                fileSnapshot.Update();

                // 更新 CSharpCompilation 中的 SyntaxTree
                compilation = compilation.ReplaceSyntaxTree(oldTree, fileSnapshot.SyntaxTree);
                _assemblyCompilations[assemblyName] = compilation;
            }
        }

        /// <summary>
        /// 根据 DiffResults 更新方法调用图
        /// </summary>
        /// <param name="diffResults">差异结果字典，Key为类型全名，Value为差异结果</param>
        public static void UpdateMethodCallGraph(Dictionary<string, DiffResult> diffResults)
        {
            if (diffResults == null || diffResults.Count == 0)
            {
                return;
            }

            // 步骤1：收集所有 Added 和 Modified 的方法
            var modifyMethods = diffResults.Values
                .SelectMany(diffResult => diffResult.AddedMethods.Values.Concat(diffResult.ModifiedMethods.Values)).ToArray();

            if (modifyMethods.Length == 0)
            {
                return;
            }

            // 步骤2：清理 methodGraph 中 methodsToUpdate 中的方法作为调用者的关系
            // 因为 methodsToUpdate 中的方法被修改了，它们可能不再调用某些方法，
            // 所以需要先从所有被调用方法的调用者列表中移除这些方法
            foreach (var (calledMethodName, callers) in _methodCallGraph)
            {
                // 从调用者集合中移除 methodsToUpdate 中的方法
                foreach (var modifyMethod in modifyMethods)
                {
                    if (callers.ContainsKey(modifyMethod.MethodDefinition.FullName)) callers.Remove(modifyMethod.MethodDefinition.FullName);
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
        /// <param name="methodFullName">方法的完整签名，格式：TypeName::MethodName(Param1,Param2)</param>
        /// <returns>调用者方法信息集合（类型名, 方法完整签名）</returns>
        public static Dictionary<string, GenericMethodCallInfo> FindMethodCallers(string methodFullName)
        {
            var callers = _methodCallGraph.GetValueOrDefault(methodFullName);
            // 返回一个副本以保持线程安全和向后兼容
            return callers != null ? new Dictionary<string, GenericMethodCallInfo>(callers) : null;
        }

        /// <summary>
        /// 查找泛型方法的调用者
        /// </summary>
        /// <param name="genericMethodFullName">泛型方法的完整签名（包含类型参数）</param>
        /// <returns>调用者方法信息集合（类型名, 方法完整签名）</returns>
        public static Dictionary<string, GenericMethodCallInfo> FindGenericMethodCallers(string genericMethodFullName)
        {
            var callers = _genericMethodCallGraph.GetValueOrDefault(genericMethodFullName);
            return callers;
        }

        /// <summary>
        /// 添加泛型方法调用关系
        /// </summary>
        /// <param name="genericMethodFullName">被调用的泛型方法全名</param>
        /// <param name="callerMethodFullName">调用者方法全名</param>
        /// <param name="callerTypeName">调用者类型全名</param>
        public static void AddGenericMethodCall(string genericMethodFullName, string callerMethodFullName, string callerTypeName)
        {
            // 筛选：排除通用库的泛型方法
            if (ShouldExcludeGenericMethod(genericMethodFullName))
            {
                return;
            }
            
            var callers = _genericMethodCallGraph.GetOrAdd(genericMethodFullName, _ => new Dictionary<string, GenericMethodCallInfo>());
            
            if (!callers.ContainsKey(callerMethodFullName))
            {
                callers[callerMethodFullName] = new GenericMethodCallInfo(callerTypeName, callerMethodFullName);
            }
        }

        /// <summary>
        /// 判断是否应该排除某个泛型方法（通用库）
        /// </summary>
        private static bool ShouldExcludeGenericMethod(string genericMethodFullName)
        {
            if (string.IsNullOrEmpty(genericMethodFullName))
                return true;

            // 排除 System 命名空间（包括容器类）
            if (genericMethodFullName.StartsWith("System.", StringComparison.Ordinal))
                return true;

            // 排除 Unity 相关命名空间
            if (genericMethodFullName.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                genericMethodFullName.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
                genericMethodFullName.StartsWith("Unity.", StringComparison.Ordinal))
                return true;

            // 排除常见的第三方库
            if (genericMethodFullName.StartsWith("Newtonsoft.", StringComparison.Ordinal) ||
                genericMethodFullName.StartsWith("Mono.", StringComparison.Ordinal) ||
                genericMethodFullName.StartsWith("Microsoft.", StringComparison.Ordinal))
                return true;

            return false;
        }

        /// <summary>
        /// 根据文件路径列表读取缓存的语法树并返回其中定义的类型
        /// </summary>
        /// <param name="filePaths">文件路径列表</param>
        /// <returns>类型全名集合</returns>
        public static HashSet<string> GetTypesFromFiles(IEnumerable<string> filePaths)
        {
            var types = new HashSet<string>();

            foreach (var filePath in filePaths)
            {
                // 从缓存中获取文件快照
                if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
                {
                    // 如果缓存中没有，尝试创建快照
                    fileSnapshot = GetOrCreateFileSnapshot(filePath);
                }

                if (fileSnapshot?.SyntaxTree == null)
                    continue;

                // 从语法树中提取所有类型声明
                var root = fileSnapshot.SyntaxTree.GetRoot();
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    // 使用 RoslynHelper 扩展方法获取类型全名
                    var typeFullName = typeDecl.FullName();
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
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="index">索引，-1 表示最后一个，-2 表示倒数第二个，以此类推。正数表示从0开始的索引</param>
        /// <returns>AssemblyDefinition，如果不存在或索引超出范围则返回 null</returns>
        public static AssemblyDefinition GetAssemblyDefinition(string assemblyName, int index)
        {
            var list = _assemblyDefinitions.GetValueOrDefault(assemblyName);
            if (list == null || list.Count == 0)
            {
                return null;
            }

            index = index < 0 ? list.Count + index : index;
            if (index < 0 || index >= list.Count)
            {
                return null;
            }

            return list[index];
        }

        /// <summary>
        /// 拷贝并编译程序集，返回新的 AssemblyDefinition
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="isCache"></param>
        /// <returns>编译后的 AssemblyDefinition，如果编译失败返回 null</returns>
        public static AssemblyDefinition Compile(string assemblyName, bool isCache = true)
        {
            var compilation = GetCompilation(assemblyName);
            if (compilation == null)
            {
                LoggerScoped.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
                return null;
            }

            var ms = new MemoryStream();
            var pdbMs = new MemoryStream();
            var emitResult = compilation.Emit(ms, pdbMs, options: EMIT_OPTIONS);

            if (emitResult.Success)
            {
                ms.Seek(0, SeekOrigin.Begin);
                pdbMs.Seek(0, SeekOrigin.Begin);
                ReaderParameters.SymbolStream = pdbMs;
                var assemblyDef = AssemblyDefinition.ReadAssembly(ms, ReaderParameters);
                
                var assemblyDefName = $"{assemblyName}_{Guid.NewGuid()}";
                assemblyDef.Name.Name = assemblyDefName;
                assemblyDef.MainModule.Name = assemblyDefName;

                if (isCache)
                {
                    var list = _assemblyDefinitions.GetValueOrDefault(assemblyName, new List<AssemblyDefinition>());
                    list.Add(assemblyDef);
                }

                return assemblyDef;
            }
            else
            {
                var errorMsg = string.Join("\n", emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

                LoggerScoped.LogError($"程序集 {assemblyName} 编译失败: {errorMsg}");
                return null;
            }
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
            // 提取状态机类型并比较 MoveNext 方法
            TypeDefinition stateMachineType = (methodRef as GenericInstanceMethod)?.GenericArguments[0] as TypeDefinition;

            if (stateMachineType == null || !stateMachineType.HasMethods)
                return null;

            return stateMachineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        }

        /// <summary>
        /// 分析程序集中的所有类型
        /// </summary>
        /// <param name="compilation">程序集</param>
        public static void AnalyzeAssembly(CSharpCompilation compilation)
        {
            var stopwatch = Stopwatch.StartNew();
            
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var typeDecl in typeDeclarations)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
                    if (typeSymbol == null)
                        continue;

                    var typeFullName = typeSymbol.FullName();

                    // 从缓存获取或创建新的结果对象
                    var result = _typeAnalysisCache.GetOrAdd(typeFullName, _ => new TypeAnalysisResult
                    {
                        TypeFullName = typeFullName,
                        AssemblyName = compilation.AssemblyName,
                        FilePaths = new HashSet<string>(),
                        InternalTypes = new HashSet<string>(),
                        GenericMethodTypes = new HashSet<string>()
                    });
                    
                    result.FilePaths.Add(syntaxTree.FilePath);

                    // 分析该声明中使用的成员（internal 和泛型方法）
                    result.AnalyzeTypeDeclaration(typeDecl, semanticModel);
                }
            }

            stopwatch.Stop();
            LoggerScoped.LogDebug($"程序集  {compilation.AssemblyName} 类型分析完成，耗时: {stopwatch.ElapsedMilliseconds}ms, " +
                             $"类型声明数: {_typeAnalysisCache.Count}");
        }

        /// <summary>
        /// 检查类型是否是Task状态机
        /// </summary>
        public static bool TypeIsTaskStateMachine(TypeDefinition typeDef)
        {
            return typeDef.Interfaces.Any(implementation => implementation.InterfaceType.FullName.Equals("System.Runtime.CompilerServices.IAsyncStateMachine"));
        }

        private static string _fileSnapshotsPath = Path.Combine(ReloadHelper.AssemblyPath, "FileSnapshots.json");

        public static void Serialize()
        {
            if (_fileSnapshots.Count != 0)
            {
                if (File.Exists(_fileSnapshotsPath))
                {
                    File.Delete(_fileSnapshotsPath);
                }
                var fileSnapshots = JsonConvert.SerializeObject(_fileSnapshots);
                File.WriteAllText(_fileSnapshotsPath, fileSnapshots);
            }
        }

        public static void Deserialize()
        {
            if (File.Exists(_fileSnapshotsPath))
            {
                var fileSnapshots = File.ReadAllText(_fileSnapshotsPath);
                _fileSnapshots = JsonConvert.DeserializeObject<ConcurrentDictionary<string, FileSnapshot>>(fileSnapshots);
            }
        }
        #endregion

        #region 私有实现方法

        /// <summary>
        /// 加载代码生成器
        /// </summary>
        private static void LoadSourceGenerators()
        {
            _sourceGenerators.Clear();

            if (string.IsNullOrEmpty(SourceGeneratorDllName))
            {
                LoggerScoped.LogDebug("未配置代码生成器 DLL 名称，跳过代码生成器加载");
                return;
            }

            // 使用 AssetDatabase 在 Unity 项目中查找 DLL 文件
            string dllPath = null;
            var guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(SourceGeneratorDllName));
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith(SourceGeneratorDllName, StringComparison.OrdinalIgnoreCase))
                {
                    dllPath = Path.GetFullPath(assetPath);
                }
            }

            if (dllPath == null)
            {
                return;
            }

            var generatorAssembly = System.Reflection.Assembly.LoadFile(dllPath);
            var generatorTypes = new List<IIncrementalGenerator>();

            foreach (var type in generatorAssembly.GetTypes())
            {
                if (type.IsNested || type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (typeof(IIncrementalGenerator).IsAssignableFrom(type))
                {
                    var generator = Activator.CreateInstance(type) as IIncrementalGenerator;
                    if (generator != null)
                    {
                        generatorTypes.Add(generator);
                        LoggerScoped.LogDebug($"加载代码生成器: {type.FullName}");
                    }
                }
            }

            _sourceGenerators.AddRange(generatorTypes);
        }

        private static SyntaxTree GetSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            var syntaxTree = CSharpSyntaxTree.ParseText(content, ParseOptions, path: filePath, encoding: Encoding.UTF8);

            return syntaxTree;
        }

        /// <summary>
        /// 获取或创建文件快照
        /// </summary>
        private static FileSnapshot GetOrCreateFileSnapshot(string filePath)
        {
            if (!_fileSnapshots.TryGetValue(filePath, out var fileSnapshot))
            {
                fileSnapshot = new FileSnapshot(filePath);
                _fileSnapshots[filePath] = fileSnapshot;
            }

            return fileSnapshot;
        }

        /// <summary>
        /// 为程序集构建 CSharpCompilation 并缓存
        /// </summary>
        private static async Task BuildAssemblyCompilation(Assembly assembly)
        {
            // 使用 ConcurrentBag 来安全地收集语法树
            var syntaxTrees = new ConcurrentBag<SyntaxTree>();

            List<Task> tasks = new();
            // 收集程序集的所有源文件
            foreach (var sourceFile in assembly.sourceFiles)
            {
                tasks.Add(Task.Run(() =>
                {
                    var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", sourceFile));

                    if (!File.Exists(fullPath))
                    {
                        return;
                    }

                    // 获取或创建文件快照
                    var syntaxTree = GetOrCreateFileSnapshot(fullPath)?.SyntaxTree;
                    if (syntaxTree != null)
                    {
                        syntaxTrees.Add(syntaxTree);
                        _fileToAssembly[fullPath] = assembly.name;
                    }
                }));
            }

            await Task.WhenAll(tasks);

            if (syntaxTrees.Count == 0)
            {
                return;
            }

            // 构建引用
            var references = new List<MetadataReference>();

            // 添加程序集的依赖引用
            foreach (var assemblyReference in assembly.assemblyReferences)
            {
                if (!string.IsNullOrEmpty(assemblyReference.outputPath) && File.Exists(assemblyReference.outputPath))
                {
                    references.Add(MetadataReference.CreateFromFile(assemblyReference.outputPath));
                }
            }

            foreach (var compiledAssemblyReference in assembly.compiledAssemblyReferences)
            {
                if (!string.IsNullOrEmpty(compiledAssemblyReference) && File.Exists(compiledAssemblyReference))
                {
                    references.Add(MetadataReference.CreateFromFile(compiledAssemblyReference));
                }
            }

            // 创建 CSharpCompilation
            var compilation = CSharpCompilation.Create(
                assemblyName: assembly.name,
                syntaxTrees: syntaxTrees,
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Debug,
                    allowUnsafe: assembly.compilerOptions.AllowUnsafeCode,
                    concurrentBuild:true,
                    deterministic:false
                )
            );

            // 如果存在代码生成器，使用 GeneratorDriver 运行它们
            if (_sourceGenerators.Count > 0)
            {
                var driver = CSharpGeneratorDriver.Create(_sourceGenerators.ToArray());
                driver.RunGeneratorsAndUpdateCompilation(compilation, out Microsoft.CodeAnalysis.Compilation newCompilation, out var diagnostics);
                _assemblyCompilations[assembly.name] = (newCompilation as CSharpCompilation);
            }
            else
            {
                _assemblyCompilations[assembly.name] = compilation;
            }

            // 分析程序集中的所有类型
            AnalyzeAssembly(compilation);
        }

        /// <summary>
        /// 使用 Mono.Cecil 构建方法调用图
        /// </summary>
        private static async Task BuildMethodCallGraph(AssemblyDefinition assemblyDef)
        {
            // 分析所有类型
            List<Task> tasks = new();
            foreach (var type in assemblyDef.MainModule.Types)
            {
                tasks.Add(Task.Run(() => AnalyzeTypeForMethodCalls(type)));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 分析类型中的泛型方法调用
        /// </summary>
        private static void AnalyzeTypeForMethodCalls(TypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                AnalyzeMethodForMethodCalls(method);
            }
        }

        /// <summary>
        /// 分析方法中的泛型方法调用，更新调用图
        /// </summary>
        /// <param name="methodDef">要分析的方法定义</param>
        private static void AnalyzeMethodForMethodCalls(MethodDefinition methodDef)
        {
            if (!methodDef.HasBody)
                return;

            // 分析方法体中的指令
            foreach (var instruction in methodDef.Body.Instructions)
            {
                // 查找方法调用指令
                if (instruction.OpCode.Code == Code.Call ||
                    instruction.OpCode.Code == Code.Callvirt ||
                    instruction.OpCode.Code == Code.Calli ||
                    instruction.OpCode.Code == Code.Newobj)
                {
                    if (instruction.Operand is GenericInstanceMethod calledMethodRef)
                    {
                        // 过滤掉一些不需要记录的方法
                        if (calledMethodRef.DeclaringType.Scope.Name.Contains("System")
                            || calledMethodRef.DeclaringType.Scope.Name.Contains("UnityEngine"))
                        {
                            continue;
                        }

                        var calledMethodName = calledMethodRef.ElementMethod.FullName;

                        // 添加到调用图索引
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
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
        private static string FullName(this TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl == null)
                return "";

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
            builder.Append(string.Join(".", parts));
            return builder.ToString();
        }
        
        /// <summary>
        /// 获取类型的全名（包括命名空间）
        /// </summary>
        public static string FullName(this INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.ToDisplayString();
        }
        #endregion
    }
}


/// <summary>
/// 文件快照
/// </summary>
// [MessagePackObject]
[Serializable]
public class FileSnapshot
{
    public string FilePath;

    [JsonIgnore]
    public SyntaxTree SyntaxTree;

    public string FileContent;

    public FileSnapshot() { }
    
    public FileSnapshot(string path)
    {
        FilePath = path;
        Update();
    }

    public void Update()
    {
        FileContent = File.ReadAllText(FilePath);
        if (!string.IsNullOrEmpty(FileContent))
        {
            SyntaxTree = CSharpSyntaxTree.ParseText(FileContent, TypeInfoHelper.ParseOptions, path: FilePath, encoding: Encoding.UTF8);
        }
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        if (!string.IsNullOrEmpty(FileContent))
        {
            SyntaxTree = CSharpSyntaxTree.ParseText(FileContent, TypeInfoHelper.ParseOptions, path: FilePath, encoding: Encoding.UTF8);
        }
    }
}

 /// <summary>
 /// 泛型方法调用信息
 /// </summary>
 public readonly struct GenericMethodCallInfo : IEquatable<GenericMethodCallInfo>
 {
     public readonly string TypeName;
     public readonly string MethodName;

     public readonly MethodDefinition MethodDef;

     public GenericMethodCallInfo(string typeName, string methodName)
     {
         TypeName = typeName;
         MethodName = methodName;
         MethodDef = null;
     }

     public GenericMethodCallInfo(MethodDefinition methodDef)
     {
         MethodDef = methodDef;
         TypeName = methodDef.DeclaringType.IsGenericInstance ? methodDef.DeclaringType.GetElementType().FullName : methodDef.DeclaringType.FullName;
         MethodName = methodDef.Name;
     }

     public bool Equals(GenericMethodCallInfo other)
     {
         return TypeName == other.TypeName && MethodName == other.MethodName;
     }

     public override bool Equals(object obj)
     {
         return obj is GenericMethodCallInfo other && Equals(other);
     }

     public override int GetHashCode()
     {
         unchecked
         {
             return ((TypeName?.GetHashCode() ?? 0) * 397) ^ (MethodName?.GetHashCode() ?? 0);
         }
     }
 }

 /// <summary>
 /// 类型分析结果
 /// </summary>
 public class TypeAnalysisResult
 {
     /// <summary>
     /// 类型全名
     /// </summary>
     public string TypeFullName { get; set; }
     
     /// <summary>
     /// 程序集名称
     /// </summary>
     public string AssemblyName { get; set; }
     
    /// <summary>
    /// 类型所在文件路径集合
    /// </summary>
    public HashSet<string> FilePaths { get; set; }
    
   /// <summary>
   /// 类中使用的 internal 成员所在的类型全名集合
   /// </summary>
   public HashSet<string> InternalTypes { get; set; }
   
   /// <summary>
   /// 类中使用的泛型方法所在的类型全名集合
   /// </summary>
   public HashSet<string> GenericMethodTypes { get; set; }
   
   /// <summary>
   /// 分析类型声明中使用的成员
   /// </summary>
   public void AnalyzeTypeDeclaration(TypeDeclarationSyntax typeDecl, SemanticModel semanticModel)
    {
        // 遍历类型声明中的所有成员
        foreach (var member in typeDecl.Members)
        {
            // 分析方法成员
            if (member is MethodDeclarationSyntax methodDecl)
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol != null)
                {
                    var callerMethodFullName = GetMethodFullName(methodSymbol);
                    AnalyzeMethodForInternalAndGeneric(methodDecl, semanticModel, callerMethodFullName);
                }
            }
            // 分析构造函数
            else if (member is ConstructorDeclarationSyntax ctorDecl)
            {
                var ctorSymbol = semanticModel.GetDeclaredSymbol(ctorDecl);
                if (ctorSymbol != null)
                {
                    var callerMethodFullName = GetMethodFullName(ctorSymbol);
                    AnalyzeMethodBodyForInternalAndGeneric(ctorDecl.Body, semanticModel, callerMethodFullName);
                    
                    // 分析表达式体构造函数
                    if (ctorDecl.ExpressionBody != null)
                    {
                        AnalyzeExpressionForInternalAndGeneric(ctorDecl.ExpressionBody.Expression, semanticModel, callerMethodFullName);
                    }
                }
            }
            // 分析属性
            else if (member is PropertyDeclarationSyntax propertyDecl)
            {
                var propertySymbol = semanticModel.GetDeclaredSymbol(propertyDecl);
                if (propertySymbol != null)
                {
                    // 分析属性访问器
                    if (propertyDecl.AccessorList != null)
                    {
                        foreach (var accessor in propertyDecl.AccessorList.Accessors)
                        {
                            var accessorSymbol = semanticModel.GetDeclaredSymbol(accessor);
                            if (accessorSymbol != null)
                            {
                                var callerMethodFullName = GetMethodFullName(accessorSymbol);
                                AnalyzeMethodBodyForInternalAndGeneric(accessor.Body, semanticModel, callerMethodFullName);
                                
                                if (accessor.ExpressionBody != null)
                                {
                                    AnalyzeExpressionForInternalAndGeneric(accessor.ExpressionBody.Expression, semanticModel, callerMethodFullName);
                                }
                            }
                        }
                    }
                    
                    // 分析表达式体属性
                    if (propertyDecl.ExpressionBody != null)
                    {
                        // 表达式体属性使用 getter 方法名
                        var getterFullName = $"{TypeFullName}.get_{propertySymbol.Name}()";
                        AnalyzeExpressionForInternalAndGeneric(propertyDecl.ExpressionBody.Expression, semanticModel, getterFullName);
                    }
                }
            }
            // 分析字段初始化器
            else if (member is FieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    if (variable.Initializer != null)
                    {
                        // 字段初始化器使用字段名作为"调用者"
                        var fieldName = $"{TypeFullName}..ctor::{variable.Identifier.ValueText}";
                        AnalyzeExpressionForInternalAndGeneric(variable.Initializer.Value, semanticModel, fieldName);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 分析方法声明中使用的 internal 成员和泛型方法
    /// </summary>
    private void AnalyzeMethodForInternalAndGeneric(MethodDeclarationSyntax methodDecl, SemanticModel semanticModel, string callerMethodFullName)
    {
        // 分析方法体
        AnalyzeMethodBodyForInternalAndGeneric(methodDecl.Body, semanticModel, callerMethodFullName);
        
        // 分析表达式体方法
        if (methodDecl.ExpressionBody != null)
        {
            AnalyzeExpressionForInternalAndGeneric(methodDecl.ExpressionBody.Expression, semanticModel, callerMethodFullName);
        }
    }

    /// <summary>
    /// 分析方法体中使用的 internal 成员和泛型方法
    /// </summary>
    private void AnalyzeMethodBodyForInternalAndGeneric(BlockSyntax body, SemanticModel semanticModel, string callerMethodFullName)
    {
        if (body == null)
            return;

        // 遍历方法体中的所有表达式
        var expressions = body.DescendantNodes().OfType<ExpressionSyntax>();
        
        foreach (var expression in expressions)
        {
            AnalyzeExpressionForInternalAndGeneric(expression, semanticModel, callerMethodFullName);
        }
    }

    /// <summary>
    /// 分析表达式中使用的 internal 成员和泛型方法
    /// </summary>
    private void AnalyzeExpressionForInternalAndGeneric(ExpressionSyntax expression, SemanticModel semanticModel, string callerMethodFullName)
    {
        if (expression == null)
            return;

        var symbolInfo = semanticModel.GetSymbolInfo(expression);
        var symbol = symbolInfo.Symbol;

        if (symbol == null)
            return;

        // 检查是否是 internal 成员
        if (symbol.DeclaredAccessibility == Accessibility.Internal)
        {
            var containingType = symbol.ContainingType;
            if (containingType != null)
            {
                var typeFullName = containingType.FullName();
                if (!string.IsNullOrEmpty(typeFullName))
                {
                    InternalTypes.Add(typeFullName);
                }
            }
        }

        // 检查是否是泛型方法调用
        if (symbol is IMethodSymbol methodSymbol)
        {
            bool isGenericCall = false;
            string genericMethodFullName = null;
            
            // 检查方法本身是否是泛型的
            if (methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length > 0)
            {
                isGenericCall = true;
                genericMethodFullName = GetGenericMethodFullName(methodSymbol);
            }

            // 检查方法所在类型是否是泛型的
            else if (methodSymbol.ContainingType.IsGenericType && !methodSymbol.ContainingType.IsUnboundGenericType)
            {
                isGenericCall = true;
                genericMethodFullName = GetGenericMethodFullName(methodSymbol);
            }
            
            // 如果是泛型方法调用，添加到全局调用图和类型集合
            if (isGenericCall && !string.IsNullOrEmpty(genericMethodFullName) && !string.IsNullOrEmpty(callerMethodFullName))
            {
                TypeInfoHelper.AddGenericMethodCall(genericMethodFullName, callerMethodFullName, TypeFullName);
                
                // 添加泛型方法所在的类型到 GenericMethodTypes（粗粒度）
                var genericMethodTypeFullName = methodSymbol.ContainingType.FullName();
                if (!string.IsNullOrEmpty(genericMethodTypeFullName))
                {
                    GenericMethodTypes.Add(genericMethodTypeFullName);
                }
            }
        }
    }

    /// <summary>
    /// 获取泛型方法的全名（包含类型参数）
    /// </summary>
    private static string GetGenericMethodFullName(IMethodSymbol methodSymbol)
    {
        return methodSymbol.ConstructedFrom.ToString();
    }

    /// <summary>
    /// 获取方法的全名（用于识别调用者）
    /// </summary>
    private static string GetMethodFullName(IMethodSymbol methodSymbol)
    {
        return methodSymbol.ConstructedFrom.ToString();
    }
 }
