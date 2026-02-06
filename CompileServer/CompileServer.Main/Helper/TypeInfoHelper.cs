using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CompileServer.Models;
using CompileServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CompileServer.Helper
{
    /// <summary>
    /// 类型信息辅助类 - 管理编译、语法分析和方法调用图
    /// </summary>
    public static class TypeInfoHelper
    {
        #region 私有字段

        /// <summary>
        /// 方法调用图索引：方法签名 -> 调用者方法信息集合
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GenericMethodCallInfo>> _methodCallGraph = new(Environment.ProcessorCount, 1000);

        /// <summary>
        /// 泛型方法名称映射：!!0格式 -> 定义的格式如：T1、T2
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _genericMethodNameRefToDef = new();
        
        /// <summary>
        /// 泛型方法名称映射：定义的格式如：T1、T2 -> !!0格式
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _genericMethodNameDefToRef = new();
        
        /// <summary>
        /// 文件路径 -> Assembly 名称
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _fileToAssembly = new();

        /// <summary>
        /// 所有非动态生成程序集中的类型缓存（类型名 -> TypeDefinition）
        /// 用于在 IL 修改时查找原类型引用
        /// </summary>
        private static readonly ConcurrentDictionary<string, TypeReference> _allTypesInNonDynamicGeneratedAssemblies = new();

        /// <summary>
        /// 所有类型 → 文件路径列表的索引
        /// 用于查找任意类型所在的文件
        /// </summary>
        private static readonly ConcurrentDictionary<string, HashSet<string>> _typeToFiles = new();

        /// <summary>
        /// 每个程序集的 global using 语句列表
        /// Key: 程序集名称, Value: global using 语句文本列表
        /// </summary>
        private static readonly ConcurrentDictionary<string, List<string>> _assemblyGlobalUsings = new();

        #endregion

        #region 生命周期方法

        /// <summary>
        /// 使用 AssemblyContext 初始化（从 Unity 传递过来）
        /// </summary>
        public static async Task Initialize()
        {
            var stopwatch = Stopwatch.StartNew();

            // 清除之前的结果
            Clear();

            foreach (var (_, context) in ReloadHelper.AssemblyContext)
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
                        BuildTypeIndex(sourceFile);
                    }

                    using var assemblyDef = AssemblyDefinition.ReadAssembly(context.OutputPath);

                    await BuildMethodCallGraph(assemblyDef);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"处理程序集 {context.Name} 时出错: {ex.Message}");
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"TypeInfo 初始化完成，耗时: {stopwatch.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// 清除所有缓存和状态
        /// </summary>
        private static void Clear()
        {
            // 清除所有缓存
            _methodCallGraph.Clear();
            _genericMethodNameRefToDef.Clear();
            _genericMethodNameDefToRef.Clear();
            _fileToAssembly.Clear();
            _allTypesInNonDynamicGeneratedAssemblies.Clear();
            _typeToFiles.Clear();
            _assemblyGlobalUsings.Clear();

            Console.WriteLine("已清除所有缓存和临时文件");
        }

        #endregion

        #region Public 方法

        /// <summary>
        /// 根据文件路径获取所属的 Assembly 名称
        /// </summary>
        public static string GetFileToAssemblyName(string filePath)
        {
            return _fileToAssembly.GetValueOrDefault(filePath);
        }
        
        /// <summary>
        /// 获取指定程序集的所有 global using 语句
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>global using 语句文本列表</returns>
        public static List<string> GetGlobalUsings(string assemblyName)
        {
            return _assemblyGlobalUsings.GetValueOrDefault(assemblyName) ?? new List<string>();
        }
        
        /// <summary>
        /// 根据文件路径获取类型名集合
        /// </summary>
        /// <param name="filePaths">文件路径列表</param>
        /// <returns>类型名集合</returns>
        public static HashSet<string> GetTypesFromFiles(List<string> filePaths)
        {
            var types = new HashSet<string>();

            foreach (var filePath in filePaths)
            {
                var tree = GetSyntaxTree(filePath);
                if (tree == null)
                {
                    continue;
                }

                var root = tree.GetRoot();
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
        /// 根据类型名集合获取文件路径集合
        /// </summary>
        /// <param name="typeFullNames"></param>
        /// <returns></returns>
        public static HashSet<string> GetFilesFromTypes(HashSet<string> typeFullNames)
        {
            var result = new HashSet<string>();

            foreach (var typeFullName in typeFullNames)
            {
                GetFilesFromType(typeFullName, result);
            }

            return result;
        }
        
        /// <summary>
        /// 根据类型名获取文件路径集合
        /// </summary>
        /// <param name="typeFullName">类型名</param>
        /// <param name="filePaths"></param>
        /// <returns>文件路径集合</returns>
        public static HashSet<string> GetFilesFromType(string typeFullName, HashSet<string> filePaths = null)
        {
            filePaths ??= new HashSet<string>();

            // 直接从 _typeToFiles 获取文件路径
            if (_typeToFiles.TryGetValue(typeFullName, out var relatedFiles))
            {
                // 只添加同一程序集的文件
                foreach (var file in relatedFiles)
                {
                    filePaths.Add(file);
                }
            }

            return filePaths;
        }

        /// <summary>
        /// 根据类型名获取程序集名称
        /// </summary>
        /// <param name="typeFullName">类型名</param>
        /// <returns>程序集名称</returns>
        public static string GetAssemblyNameFromType(string typeFullName)
        {
            var filePaths = GetFilesFromType(typeFullName);

            if (filePaths.Count == 0)
            {
                return string.Empty;
            }

            return _fileToAssembly.GetValueOrDefault(filePaths.First());
        }

        /// <summary>
        /// 获取语法树
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>语法树，如果文件不存在或内容为空则返回 null</returns>
        public static SyntaxTree GetSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(content))
                return null;

            return CSharpSyntaxTree.ParseText(content, ReloadHelper.ParseOptions, path: filePath, encoding: Encoding.UTF8);
        }
        public static void UpdateMethodCallGraph(MethodDefinition methodDef)
        {
            foreach (var (calledMethodName, callers) in _methodCallGraph)
            {
                callers.TryRemove(methodDef.FullName, out _);
            }

            AnalyzeMethodForMethodCalls(methodDef);
        }

        /// <summary>
        /// 查找泛型方法的调用者
        /// </summary>
        public static Dictionary<string, GenericMethodCallInfo> GetGenericMethodCallers(string methodName)
        {
            Dictionary<string, GenericMethodCallInfo> result = new();
            foreach (var name in new[] {methodName, _genericMethodNameDefToRef.GetValueOrDefault(methodName)})
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var callers = _methodCallGraph.GetValueOrDefault(name);
                if (callers != null)
                {
                    foreach (var keyValuePair in callers) result.Add(keyValuePair.Key, keyValuePair.Value);
                }
            }

            return result;
        }

        public static string GetGenericMethodDefName(string methodName)
        {
            return _genericMethodNameRefToDef.GetValueOrDefault(methodName, methodName);
        }

        /// <summary>
        /// 获取原类型定义
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns>TypeReference，如果未找到则返回 null</returns>
        public static TypeReference GetOriginalTypeReference(TypeReference typeRef)
        {
            var assemblyName = typeRef.Scope.Name.Split("---")[0];
            var typeFullName = typeRef.FullName;

            if (_allTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeFullName, out var originalTypeRef))
            {
                return originalTypeRef;
            }

            try
            {
                using var assemblyDef = ReloadHelper.GetOriginalAssembly(assemblyName);
                if (assemblyDef == null)
                {
                    return null;
                }
                
                var module = assemblyDef.MainModule;
                
                originalTypeRef = module.GetType(typeFullName);

                if (originalTypeRef == null)
                {
                    throw new Exception($"类型 {typeFullName} 未在程序集 {assemblyName} 中找到");
                }

                _allTypesInNonDynamicGeneratedAssemblies[typeFullName] = originalTypeRef;

                return originalTypeRef;
            }
            catch (Exception)
            {
                // ignored
            }

            return null;
        }
        
        public static MethodReference GetOriginalMethodReference(MethodReference methodRef, ModuleDefinition module)
        {
            var typeRef = GetOriginalTypeReference(methodRef.DeclaringType);
            if (typeRef == null)
            {
                return null;
            }

            var methodDef = ((TypeDefinition)typeRef)?.Methods.FirstOrDefault(m => m.Name == methodRef.Name && m.Parameters.Count == methodRef.Parameters.Count);
            if (methodDef == null)
            {
                return null;
            }

            return module.ImportReference(methodDef);
        }

        /// <summary>
        /// 检查是否是Task调用
        /// </summary>
        public static bool IsTaskCallStartMethod(MethodReference methodRef)
        {
            return (methodRef.DeclaringType.Name.Contains("AsyncTaskMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncVoidMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncValueTaskMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncUniTaskMethodBuilder")
                    || methodRef.DeclaringType.Name.Contains("AsyncUniTaskVoidMethodBuilder"))
                   && methodRef.Name.Equals("Start");
        }

        /// <summary>
        /// 查找Task调用的方法
        /// </summary>
        public static MethodDefinition GetTaskCallMethod(MethodReference methodRef)
        {
            TypeDefinition stateMachineType = (methodRef as GenericInstanceMethod)?.GenericArguments[0] as TypeDefinition;

            if (stateMachineType == null || !stateMachineType.HasMethods)
                return null;

            return stateMachineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        }

        /// <summary>
        /// 检查类型是否是Task状态机
        /// </summary>
        public static bool IsTaskStateMachine(TypeDefinition typeDef)
        {
            return typeDef.Interfaces.Any(implementation =>
                implementation.InterfaceType.FullName.Equals("System.Runtime.CompilerServices.IAsyncStateMachine"));
        }

        /// <summary>
        /// 检查类型是否是编译器生成的类型
        /// </summary>
        public static bool IsCompilerGeneratedType(TypeDefinition typeDef)
        {
            if (typeDef == null)
            {
                return false;
            }

            return typeDef.CustomAttributes.Any(attr =>
                attr.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        #endregion

        #region Private 方法

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
                            calledMethodRef.DeclaringType.Scope.Name.Contains("mscorlib") ||
                            calledMethodRef.DeclaringType.Scope.Name.Contains("UnityEngine"))
                        {
                            continue;
                        }

                        var calledMethodName = calledMethodRef.ElementMethod.FullName;

                        // 尝试构建泛型方法名称映射
                        TryBuildGenericMethodNameMapping(calledMethodRef.ElementMethod);

                        if (!_methodCallGraph.TryGetValue(calledMethodName, out var callers))
                        {
                            callers = new ConcurrentDictionary<string, GenericMethodCallInfo>();
                            _methodCallGraph.TryAdd(calledMethodName, callers);
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
        /// 尝试构建泛型方法名称映射（!!0格式 和 T0格式 的映射）
        /// </summary>
        /// <param name="elementMethod">ElementMethod定义</param>
        private static void TryBuildGenericMethodNameMapping(MethodReference elementMethod)
        {
            if (!elementMethod.HasGenericParameters || !(elementMethod is MethodDefinition))
            {
                return;
            }

            // 获取ElementMethod的FullName（T0格式）
            var defName = elementMethod.FullName;

            if (_genericMethodNameDefToRef.ContainsKey(defName) || _genericMethodNameRefToDef.ContainsKey(defName))
            {
                return;
            }
            
            // 转换为!!0格式
            string refName = elementMethod.FullName;
            for (int i = 0; i < elementMethod.GenericParameters.Count; i++)
            {
                refName = refName.Replace(elementMethod.GenericParameters[i].Name, $"!!{i}");
            }

            _genericMethodNameRefToDef.TryAdd(refName, defName);
            _genericMethodNameDefToRef.TryAdd(defName, refName);
        }        

        /// <summary>
        /// 获取类型名
        /// </summary>
        private static string GetTypeFullName(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl == null)
                return string.Empty;

            // 构建嵌套类型名
            var parts = new List<string>();
            var current = typeDecl;

            while (current != null)
            {
                parts.Insert(0, current.Identifier.ValueText);
                current = current.Parent as TypeDeclarationSyntax;
            }

            // 直接拼接所有嵌套的命名空间
            var builder = new StringBuilder();
            var parent = typeDecl.Parent;
            var hasNamespace = false;
            
            while (parent != null)
            {
                if (parent is BaseNamespaceDeclarationSyntax namespaceDecl)
                {
                    if (hasNamespace)
                    {
                        builder.Insert(0, '.');
                    }
                    builder.Insert(0, namespaceDecl.Name.ToString());
                    hasNamespace = true;
                }
                parent = parent.Parent;
            }

            // 拼接类型名
            if (hasNamespace)
            {
                builder.Append('.');
            }
            builder.Append(string.Join("/", parts));

            return builder.ToString();
        }

        /// <summary>
        /// 构建类型索引（类型名 → 文件路径列表）
        /// </summary>
        private static void BuildTypeIndex(string sourceFile)
        {
            var tree = GetSyntaxTree(sourceFile);
            if (tree == null)
            {
                return;
            }

            var root = tree.GetRoot();
            
            // 提取 global using 语句
            var globalUsings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => !u.GlobalKeyword.IsKind(SyntaxKind.None))
                .Select(u => u.ToString())
                .ToList();
            
            if (globalUsings.Count > 0)
            {
                var assemblyName = _fileToAssembly.GetValueOrDefault(sourceFile);
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    _assemblyGlobalUsings.AddOrUpdate(
                        assemblyName,
                        _ => new List<string>(globalUsings),
                        (_, existing) => { existing.AddRange(globalUsings); return existing; }
                    );
                }
            }
            
            // 构建类型索引
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeFullName = GetTypeFullName(typeDecl);
                        
                // 添加到全类型索引
                _typeToFiles.AddOrUpdate(
                    typeFullName,
                    _ => [sourceFile],
                    (_, set) => { set.Add(sourceFile); return set; }
                );
            }
        }

        #endregion
    }
}
