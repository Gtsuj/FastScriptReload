using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CompileServer.Helper;
using CompileServer.Models;
using CompileServer.Rewriters;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using HookInfo.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace CompileServer.Services
{
    /// <summary>
    /// 差异分析和编译服务
    /// </summary>
    public class CompileDiffService
    {
        private readonly ILogger<CompileDiffService> _logger;

        public CompileDiffService(ILogger<CompileDiffService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 编译并返回Diff结果
        /// </summary>
        public Dictionary<string, DiffResult> CompileAndDiff(Dictionary<string,List<string>> filesByAssembly)
        {
            Dictionary<string, DiffResult> diffResults = new Dictionary<string, DiffResult>();

            // 对每个程序集进行编译和差异计算
            foreach (var (assemblyName, filesInAssembly) in filesByAssembly)
            {
                _logger.LogInformation(
                    $"Processing compile request for assembly: {assemblyName}, {filesInAssembly.Count} changed files");

                // 提取一次改动文件中的类型
                var changedTypes = TypeInfoHelper.GetTypesFromFiles(filesInAssembly);
                if (changedTypes.Count == 0)
                {
                    _logger.LogDebug("改动文件中没有找到类型定义");
                    continue;
                }

                // 独立编译改动文件
                var patchAssembly = CompileChangedFiles(assemblyName, changedTypes);
                if (patchAssembly == null)
                {
                    continue;
                }

                // 获取到差异部分
                DiffAssembly(assemblyName, changedTypes, patchAssembly, diffResults);
            }

            // 泛型调用者加入Diff列表
            foreach (var (_, diffResult) in diffResults.ToArray())
            {
                foreach (var (methodName, modifiedMethod) in diffResult.ModifiedMethods.ToArray())
                {
                    if (!modifiedMethod.HasGenericParameters)
                    {
                        continue;
                    }

                    var callers = TypeInfoHelper.GetGenericMethodCallers(methodName);
                    if (callers == null)
                    {
                        continue;
                    }

                    foreach (var (callerMethodName, methodCallInfo) in callers)
                    {
                        if (!diffResults.TryGetValue(methodCallInfo.TypeName, out var callerDiffResult))
                        {
                            callerDiffResult = new DiffResult()
                            {
                                AssemblyName = TypeInfoHelper.GetAssemblyNameFromType(methodCallInfo.TypeName),
                            };
                            diffResults.Add(methodCallInfo.TypeName, callerDiffResult);
                        }

                        callerDiffResult.AddModifiedMethod(methodCallInfo.TypeName, callerMethodName);
                    }
                }
            }

            return diffResults;
        }

        /// <summary>
        /// 只编译改动文件生成补丁 DLL
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="changedTypes">改动文件中的类型集合</param>
        /// <returns>补丁 AssemblyDefinition</returns>
        private AssemblyDefinition CompileChangedFiles(string assemblyName, HashSet<string> changedTypes)
        {
            HashSet<string> compileFiles = new(changedTypes);

            // 遍历 HookTypeInfoCache，收集有新增方法或字段的类型对应的文件
            foreach (var (typeFullName, hookTypeInfo) in ReloadHelper.HookTypeInfoCache)
            {
                // 只处理当前程序集的类型
                if (hookTypeInfo.AssemblyName != assemblyName)
                    continue;

                // 如果有一个方法或字段是添加的，就可以将整个类型加入
                if (hookTypeInfo.ModifiedMethods.Values.Any(m => m.MemberModifyState == MemberModifyState.Added) || hookTypeInfo.ModifiedFields.Count > 0)
                {
                    compileFiles.Add(typeFullName);
                }
            }
            
            // 收集改动类型对应的文件路径
            var allFiles = TypeInfoHelper.GetFilesFromTypes(compileFiles);

            // 解析语法树 + 重命名扩展方法类
            var syntaxTrees = new List<SyntaxTree>();
            var extensionRename = new ExtensionClassRename();

            foreach (var file in allFiles)
            {
                var tree = TypeInfoHelper.GetSyntaxTree(file);
                if (tree == null) continue;

                var newRoot = extensionRename.Visit(tree.GetRoot());
                syntaxTrees.Add(tree.WithRootAndOptions(newRoot, tree.Options));
            }

            // 注入 IgnoresAccessChecksToAttribute
            syntaxTrees.Add(CreateIgnoreAccessibilityTree(assemblyName, ReloadHelper.ParseOptions));

            // 创建 Compilation 并 Emit
            var patchAssemblyName = ReloadHelper.GetWrapperAssemblyName(assemblyName);
            var compilation = CSharpCompilation.Create(
                patchAssemblyName,
                syntaxTrees,
                ReloadHelper.GetMetadataReferences(assemblyName),
                CreateIgnoreAccessibilityOptions(ReloadHelper.AssemblyContext[assemblyName].AllowUnsafeCode)
            );

            var filePath = Path.Combine(ReloadHelper.AssemblyOutputTempPath, $"{patchAssemblyName}.dll");
            var pdbPath = Path.Combine(ReloadHelper.AssemblyOutputTempPath, $"{patchAssemblyName}.pdb");
            var emitResult = compilation.Emit(filePath, pdbPath);

            if (!emitResult.Success)
            {
                var errors = string.Join("\n", emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                throw new Exception(errors);
            }

            var assemblyDef = AssemblyDefinition.ReadAssembly(filePath, ReloadHelper.READER_PARAMETERS);

            // 移除扩展方法名中的 __Patch__ 后缀
            ExtensionMethodRenameRemover.RemovePatchSuffix(assemblyDef);

            return assemblyDef;
        }

        /// <summary>
        /// 生成 IgnoresAccessChecksToAttribute 的语法树
        /// </summary>
        private SyntaxTree CreateIgnoreAccessibilityTree(string targetAssemblyName, CSharpParseOptions parseOptions)
        {
            var code = $@"
using System;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(""{targetAssemblyName}"")]

namespace System.Runtime.CompilerServices
{{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {{
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName {{ get; }}
    }}
}}";
            return CSharpSyntaxTree.ParseText(code, parseOptions);
        }

        /// <summary>
        /// 创建忽略访问检查的编译选项
        /// </summary>
        private CSharpCompilationOptions CreateIgnoreAccessibilityOptions(bool allowUnsafe)
        {
            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: allowUnsafe,
                metadataImportOptions: MetadataImportOptions.All
            );

            // 通过反射设置 TopLevelBinderFlags.IgnoreAccessibility
            var topLevelBinderFlagsProperty = typeof(CSharpCompilationOptions)
                .GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
            topLevelBinderFlagsProperty?.SetValue(options, (uint)(1 << 22));

            return options;
        }

        /// <summary>
        /// 比较编译出来的程序集中的类型和原有类型，找出其中添加、删除、修改的方法
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="changedTypes">改动文件中的类型集合</param>
        /// <param name="patchAssemblyDef"></param>
        /// <param name="allDiffResults"></param>
        private void DiffAssembly(string assemblyName, HashSet<string> changedTypes, AssemblyDefinition patchAssemblyDef, Dictionary<string, DiffResult> allDiffResults)
        {
            if (patchAssemblyDef == null)
            {
                _logger.LogError($"无法获取新程序集 {assemblyName}");
                return;
            }

            // 读取原始程序集（Unity 编译的完整版本）
            using var originalAssembly = ReloadHelper.GetOriginalAssembly(assemblyName);
            if (originalAssembly == null)
            {
                _logger.LogError($"无法获取原始程序集: {assemblyName}");
                return;
            }

            // 对比改动文件中的类型
            foreach (var typeFullName in changedTypes)
            {
                var newTypeDef = patchAssemblyDef.MainModule.GetType(typeFullName);
                var originalTypeDef = originalAssembly.MainModule.GetType(typeFullName);

                DiffResult diffResult = new DiffResult()
                {
                    AssemblyName = assemblyName,
                    AssemblyDef = patchAssemblyDef
                };
                
                // 只有当 HookTypeInfoCache 和 originalAssembly 中都不存在该类型时才识别为新增类型
                if (originalTypeDef == null && !ReloadHelper.HookTypeInfoCache.ContainsKey(typeFullName))
                {
                    // 新增类型，记录所有方法和字段
                    foreach (var method in newTypeDef.Methods)
                    {
                        if (!method.IsConstructor && !method.IsGetter && !method.IsSetter)
                        {
                            diffResult.AddModifiedMethod(method, MemberModifyState.Added);
                        }
                    }

                    foreach (var field in newTypeDef.Fields)
                    {
                        diffResult.AddModifiedField(typeFullName, field.FullName);
                    }
                }
                else
                {
                    // 对比类型差异
                    CompareTypesWithCecil(typeFullName, originalTypeDef, newTypeDef, diffResult);
                }

                if (diffResult.ModifiedMethods.Count > 0 || diffResult.ModifiedFields.Count > 0)
                {
                    allDiffResults[typeFullName] = diffResult;
                }
            }
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个类型的方法差异
        /// </summary>
        /// <param name="typeFullName">类型全名</param>
        /// <param name="originalTypeDef">原类型定义</param>
        /// <param name="modifyTypeDef">新类型定义</param>
        /// <param name="diffResult"></param>
        /// <returns>差异结果</returns>
        private void CompareTypesWithCecil(string typeFullName, TypeDefinition originalTypeDef, TypeDefinition modifyTypeDef, DiffResult diffResult)
        {
            var hookTypeInfo = ReloadHelper.HookTypeInfoCache.GetValueOrDefault(typeFullName);
            
            CompareMethod(originalTypeDef, modifyTypeDef, diffResult);

            CompareField(originalTypeDef, modifyTypeDef, hookTypeInfo, diffResult);
        }

        private void CompareMethod(TypeDefinition originalTypeDef, TypeDefinition modifyTypeDef, DiffResult diffResult)
        {
            // 创建方法签名到方法的映射
            var modifyMethodMap = modifyTypeDef.Methods.ToDictionary(m => m.FullName, m => m);

            // 比较方法
            foreach (var (methodName, methodDef) in modifyMethodMap)
            {
                MethodDefinition existingMethod = ReloadHelper.GetLatestMethodDefinition(modifyTypeDef.FullName, methodName, originalTypeDef);

                // 没有改动记录且原类型中不存在，则认为是新增方法
                if (existingMethod == null)
                {
                    diffResult.AddModifiedMethod(methodDef, MemberModifyState.Added);
                    _logger.LogDebug($"发现新增的方法: {methodName}");
                    continue;
                }
                
                // 比较方法体是否改变（返回 false 表示有变化）
                if (!CompareMethodDefinitions(existingMethod, methodDef))
                {
                    diffResult.AddModifiedMethod(methodDef, MemberModifyState.Modified);
                    _logger.LogDebug($"发现修改的方法: {methodName}");
                }
            }
        }        
        
        private void CompareField(TypeDefinition originalTypeDef, TypeDefinition modifyTypeDef, HookTypeInfo hookTypeInfo, DiffResult diffResult)
        {
            // 比较字段：找出新增的字段
            var oldFieldMap = originalTypeDef.Fields.ToDictionary(f => f.FullName, f => f);
            var modifyFieldMap = modifyTypeDef.Fields.ToDictionary(f => f.FullName, f => f);

            foreach (var (fieldName, modifyFieldDef) in modifyFieldMap)
            {
                if (!oldFieldMap.ContainsKey(fieldName) && !hookTypeInfo.ModifiedFields.ContainsKey(fieldName))
                {
                    // 发现新增字段
                    diffResult.AddModifiedField(modifyFieldDef.DeclaringType.FullName, modifyFieldDef.FullName);
                    _logger.LogDebug($"发现新增的字段: {modifyFieldDef.FullName}");
                }
            }
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个方法定义是否相同
        /// </summary>
        /// <param name="existingMethod">现有方法定义</param>
        /// <param name="newMethod">新方法定义</param>
        /// <returns>true 表示方法相同（无变化），false 表示方法不同（有变化）</returns>
        private bool CompareMethodDefinitions(MethodDefinition existingMethod, MethodDefinition newMethod)
        {
            // 检查方法体是否存在
            if (!existingMethod.HasBody && !newMethod.HasBody)
            {
                // 都没有方法体，认为相同
                return true;
            }

            if (!existingMethod.HasBody || !newMethod.HasBody)
            {
                // 一个有方法体，一个没有，认为不同
                return false;
            }

            var existingBody = existingMethod.Body;
            var newBody = newMethod.Body;

            // 比较局部变量数量
            if (existingBody.Variables.Count != newBody.Variables.Count)
            {
                // 局部变量数量不同，方法不同
                return false;
            }

            // 比较异常处理块数量
            if (existingBody.ExceptionHandlers.Count != newBody.ExceptionHandlers.Count)
            {
                // 异常处理块数量不同，方法不同
                return false;
            }

            // 比较 IL 指令序列
            var existingInstructions = existingBody.Instructions.ToList();
            var newInstructions = newBody.Instructions.ToList();

            if (existingInstructions.Count != newInstructions.Count)
            {
                // 指令数量不同，方法不同
                return false;
            }

            // 逐条比较指令
            for (int i = 0; i < existingInstructions.Count; i++)
            {
                var existingInst = existingInstructions[i];
                var newInst = newInstructions[i];

                // 比较操作数（忽略元数据令牌的差异）
                if (!CompareOperands(existingInst, newInst))
                {
                    // 操作数不同，方法不同
                    return false;
                }
            }

            // 所有比较都通过，认为方法相同（无变化）
            return true;
        }

        /// <summary>
        /// 比较两个指令的操作数是否相同，忽略元数据令牌的差异
        /// </summary>
        /// <param name="existingInst">现有指令</param>
        /// <param name="newInst">新指令</param>
        /// <returns>true 表示操作数相同，false 表示操作数不同</returns>
        private bool CompareOperands(Instruction existingInst, Instruction newInst)
        {
            if (existingInst.OpCode != newInst.OpCode)
            {
                // 操作码不同，操作数不同
                return false;
            }

            // 如果都没有操作数，认为相同
            if (existingInst.Operand == null && newInst.Operand == null)
            {
                // 都没有操作数，操作数相同
                return true;
            }

            if (existingInst.Operand == null || newInst.Operand == null)
            {
                // 操作数存在性不同，操作数不同
                return false;
            }

            // 根据操作数类型进行比较
            switch (existingInst.Operand)
            {
                case int existingInt when newInst.Operand is int newInt:
                    return existingInt == newInt;

                case long existingLong when newInst.Operand is long newLong:
                    return existingLong == newLong;

                case float existingFloat when newInst.Operand is float newFloat:
                    return Math.Abs(existingFloat - newFloat) < 0.0001f;

                case double existingDouble when newInst.Operand is double newDouble:
                    return existingDouble.Equals(newDouble);

                case string existingString when newInst.Operand is string newString:
                    return existingString == newString;

                case byte existingByte when newInst.Operand is byte newByte:
                    return existingByte == newByte;

                case sbyte existingSByte when newInst.Operand is sbyte newSByte:
                    return existingSByte == newSByte;
                // 对于类型引用、方法引用等，比较它们的全名而不是元数据令牌
                case TypeReference existingTypeRef when newInst.Operand is TypeReference newTypeRef:
                    return existingTypeRef.FullName == newTypeRef.FullName;

                case ParameterDefinition existingParamDef when newInst.Operand is ParameterDefinition newParamDef:
                    return existingParamDef.ParameterType.FullName == newParamDef.ParameterType.FullName;

                case MethodDefinition existingMethodDef when newInst.Operand is MethodDefinition newMethodDef:
                    return CompareMethodDefinitionOperands(existingMethodDef, newMethodDef);

                case MethodReference existingMethodRef when newInst.Operand is MethodReference newMethodRef:
                    return CompareMethodReferenceOperands(existingMethodRef, newMethodRef);

                case FieldReference existingFieldRef when newInst.Operand is FieldReference newFieldRef:
                    return existingFieldRef.FullName == newFieldRef.FullName;

                // 对于指令引用（跳转目标），比较跳转的偏移量
                case Instruction existingTarget when newInst.Operand is Instruction newTarget:
                    // 跳转目标可能不同，但跳转的逻辑应该相同
                    // 这里简化处理：如果操作码相同，认为跳转逻辑相同
                    return true;

                case VariableDefinition existingVar when newInst.Operand is VariableDefinition newVar:
                    return existingVar.VariableType.FullName == newVar.VariableType.FullName;

                case Instruction existingSubInst when newInst.Operand is Instruction newSubInst:
                    return CompareOperands(existingSubInst, newSubInst);

                case Instruction[] existingSubInstArr when newInst.Operand is Instruction[] newSubInstArr:
                    if (existingSubInstArr.Length != newSubInstArr.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < existingSubInstArr.Length; i++)
                    {
                        if (!CompareOperands(existingSubInstArr[i], newSubInstArr[i]))
                        {
                            return false;
                        }
                    }

                    return true;

                default:
                    // 其他类型，使用默认比较
                    return existingInst.Operand.Equals(newInst.Operand);
            }
        }

        /// <summary>
        /// 比较两个 MethodDefinition 操作数是否相同
        /// </summary>
        /// <param name="existingMethodDef">现有的方法定义</param>
        /// <param name="newMethodDef">新的方法定义</param>
        /// <returns>true 表示操作数相同，false 表示操作数不同</returns>
        private bool CompareMethodDefinitionOperands(MethodDefinition existingMethodDef, MethodDefinition newMethodDef)
        {
            if (existingMethodDef.FullName != newMethodDef.FullName)
            {
                return false;
            }

            if (existingMethodDef.DeclaringType.IsNested && TypeInfoHelper.IsCompilerGeneratedType(existingMethodDef.DeclaringType))
            {
                return CompareMethodDefinitions(existingMethodDef, newMethodDef);
            }

            return true;
        }

        /// <summary>
        /// 比较两个 MethodReference 操作数是否相同
        /// </summary>
        /// <param name="existingMethodRef">现有的方法引用</param>
        /// <param name="newMethodRef">新的方法引用</param>
        /// <returns>true 表示操作数相同，false 表示操作数不同</returns>
        private bool CompareMethodReferenceOperands(MethodReference existingMethodRef, MethodReference newMethodRef)
        {
            // 先比较 FullName 是否一致
            if (existingMethodRef.FullName != newMethodRef.FullName)
            {
                return false;
            }

            // FullName 一致时，检查是否是Task状态机调用
            if (TypeInfoHelper.IsTaskCallStartMethod(existingMethodRef))
            {
                // 查找状态机类型的 MoveNext 方法
                var existingMoveNext = TypeInfoHelper.GetTaskCallMethod(existingMethodRef);
                var newMoveNext = TypeInfoHelper.GetTaskCallMethod(newMethodRef);

                // 比较 MoveNext 方法定义
                var res = CompareMethodDefinitions(existingMoveNext, newMoveNext);

                return res;
            }

            return true;
        }
    }
}
