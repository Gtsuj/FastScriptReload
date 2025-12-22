using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 新的类型信息辅助类
    /// - 按 Assembly 缓存 CSharpCompilation（只缓存 Application.dataPath 下的程序集）
    /// - 使用 Mono.Cecil 缓存方法调用信息
    /// </summary>
    public static class TypeInfoHelper
    {
        #region 私有字段

        private static bool _isInitialized;

        /// <summary>
        /// 解析选项
        /// </summary>
        private static CSharpParseOptions _parseOptions;

        /// <summary>
        /// Assembly 名称 -> CSharpCompilation 的缓存
        /// </summary>
        private static readonly ConcurrentDictionary<string, CSharpCompilation> _assemblyCompilations = new();

        /// <summary>
        /// Assembly 名称 -> AssemblyDefinition 列表的缓存（用于 Mono.Cecil 分析）
        /// </summary>
        private static readonly ConcurrentDictionary<string, List<AssemblyDefinition>> _assemblyDefinitions = new();

        /// <summary>
        /// 方法调用图索引：方法签名 -> 调用者方法信息集合
        /// Key: 被调用方法的完整签名
        /// Value: 调用者方法信息集合（类型名, 方法完整签名）
        /// </summary>
        private static readonly ConcurrentDictionary<string, Dictionary<string, GenericMethodCallInfo>> _methodCallGraph = new();

        /// <summary>
        /// 文件路径 -> Assembly 名称
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _fileToAssembly = new();

        /// <summary>
        /// 文件路径 -> FileSnapshot 的缓存
        /// </summary>
        private static readonly ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();

        /// <summary>
        /// 代码生成器缓存
        /// </summary>
        private static readonly List<IIncrementalGenerator> _sourceGenerators = new();

        /// <summary>
        /// 代码生成器 DLL 文件名(先写死)
        /// </summary>
        private const string SourceGeneratorDllName = "CSharpCodeAnalysis.dll";

        #endregion

        #region 公共查询接口

        /// <summary>
        /// 初始化并构建所有索引
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized)
                return;

            _parseOptions = new CSharpParseOptions(
                preprocessorSymbols: EditorUserBuildSettings.activeScriptCompilationDefines,
                languageVersion: LanguageVersion.Latest
            );

            // 加载代码生成器
            LoadSourceGenerators();

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // 获取所有 Unity 程序集
            var allAssemblies = CompilationPipeline.GetAssemblies();

            // 过滤：只处理 Application.dataPath 下的程序集（只要有任意一个文件不在 dataPath 下则排除整个程序集）
            var dataPathAssemblies = allAssemblies
                .Where(assembly => assembly.sourceFiles.All(sourceFile => sourceFile.StartsWith("Assets"))).ToList();

            // 并行处理每个程序集
            Parallel.ForEach(dataPathAssemblies, assembly =>
            {
                try
                {
                    // 先加载 AssemblyDefinition
                    if (string.IsNullOrEmpty(assembly.outputPath) || !File.Exists(assembly.outputPath))
                        return;

                    var assemblyDef = AssemblyDefinition.ReadAssembly(
                        assembly.outputPath,
                        new ReaderParameters { ReadWrite = false, InMemory = true }
                    );

                    _assemblyDefinitions[assembly.name] = new List<AssemblyDefinition> { assemblyDef };

                    // 构建 CSharpCompilation 和方法调用图
                    BuildAssemblyCompilation(assembly);
                    BuildMethodCallGraph(assemblyDef);
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogWarning($"处理程序集 {assembly.name} 时出错: {ex.Message}");
                }
            });

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
        /// 增量更新程序集（只更新改动的文件）
        /// </summary>
        public static void UpdateSyntaxTrees(string assemblyName, List<string> changedFiles)
        {
            if (!_assemblyCompilations.TryGetValue(assemblyName, out var compilation))
            {
                LoggerScoped.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
                return;
            }

            foreach (var filePath in changedFiles)
            {
                // 更新文件快照
                var fileSnapshot = _fileSnapshots.GetValueOrDefault(filePath) ?? new FileSnapshot(filePath);
                var newSyntaxTree = GetSyntaxTree(filePath);

                // 更新 CSharpCompilation 中的 SyntaxTree
                if (fileSnapshot.SyntaxTree != null)
                {
                    compilation = compilation.ReplaceSyntaxTree(fileSnapshot.SyntaxTree, newSyntaxTree);
                }
                else
                {
                    // 如果是新文件，添加到编译中
                    compilation = compilation.AddSyntaxTrees(newSyntaxTree);
                }

                fileSnapshot.SyntaxTree = newSyntaxTree;
            }

            _assemblyCompilations[assemblyName] = compilation;
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
            return _methodCallGraph.GetValueOrDefault(methodFullName);
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
        public static AssemblyDefinition CloneAndCompile(string assemblyName, bool isCache = true)
        {
            var compilation = GetCompilation(assemblyName);
            if (compilation == null)
            {
                LoggerScoped.LogWarning($"找不到程序集 {assemblyName} 的编译缓存");
                return null;
            }

            // 使用新的程序集名称（添加 GUID 确保唯一性）
            compilation = compilation.WithAssemblyName($"{assemblyName}_{Guid.NewGuid()}");

            var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms, options: ReloadHelper.EMIT_OPTIONS);

            if (emitResult.Success)
            {
                ms.Seek(0, SeekOrigin.Begin);
                var assemblyDef = AssemblyDefinition.ReadAssembly(ms, ReloadHelper.ReaderParameters);

                // 添加到缓存列表
                if (isCache)
                {
                    var list = _assemblyDefinitions.GetOrAdd(assemblyName, _ => new List<AssemblyDefinition>());
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
        /// 清除所有缓存
        /// </summary>
        public static void Clear()
        {
            foreach (var assemblyDefList in _assemblyDefinitions.Values)
            {
                foreach (var assemblyDef in assemblyDefList)
                {
                    assemblyDef?.Dispose();
                }
            }

            _assemblyCompilations.Clear();
            _assemblyDefinitions.Clear();
            _methodCallGraph.Clear();
            _fileToAssembly.Clear();
            _fileSnapshots.Clear();
            _sourceGenerators.Clear();
            _isInitialized = false;

            LoggerScoped.LogDebug("NewTypeInfoHelper 缓存已清除");
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

        public static SyntaxTree GetSyntaxTree(string filePath)
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

            var syntaxTree = CSharpSyntaxTree.ParseText(content, _parseOptions, path: filePath, encoding: Encoding.UTF8);

            return syntaxTree;
        }

        /// <summary>
        /// 获取或创建文件快照
        /// </summary>
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
        /// 为程序集构建 CSharpCompilation 并缓存
        /// </summary>
        private static void BuildAssemblyCompilation(Assembly assembly)
        {
            var syntaxTrees = new List<SyntaxTree>();
            var filePaths = new HashSet<string>();

            // 收集程序集的所有源文件
            foreach (var sourceFile in assembly.sourceFiles)
            {
                var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", sourceFile));

                if (!File.Exists(fullPath))
                    continue;

                // 获取或创建文件快照
                var syntaxTree = GetOrCreateFileSnapshot(fullPath).SyntaxTree;
                if (syntaxTree != null)
                {
                    syntaxTrees.Add(syntaxTree);
                    filePaths.Add(fullPath);
                }
            }

            if (syntaxTrees.Count == 0)
                return;

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
                    allowUnsafe: assembly.compilerOptions.AllowUnsafeCode
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

            // 存储文件路径到 Assembly 名称的映射
            foreach (var filePath in filePaths)
            {
                _fileToAssembly[filePath] = assembly.name;
            }
        }

        /// <summary>
        /// 使用 Mono.Cecil 构建方法调用图
        /// </summary>
        private static void BuildMethodCallGraph(AssemblyDefinition assemblyDef)
        {
            // 分析所有类型
            foreach (var type in assemblyDef.MainModule.Types)
            {
                AnalyzeTypeForMethodCalls(type);
            }
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
        #endregion
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

    public FileSnapshot(string path)
    {
        FilePath = path;
        SnapshotTime = DateTime.UtcNow;
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
