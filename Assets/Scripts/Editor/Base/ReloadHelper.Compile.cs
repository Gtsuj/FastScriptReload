using System;
using System.Collections.Generic;
using System.Linq;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Diff功能 - 比较编译程序集与原程序集的差异
    /// </summary>
    public static partial class ReloadHelper
    {
        /// <summary>
        /// 编译并返回Diff结果
        /// </summary>
        /// <param name="assemblyName"></param>
        /// <param name="files"></param>
        /// <returns></returns>
        public static Dictionary<string, DiffResult> CompileAndDiff(string assemblyName, List<string> files)
        {
            TypeInfoHelper.UpdateSyntaxTrees(assemblyName, files);
        
            if (TypeInfoHelper.CloneAndCompile(assemblyName) == null)
            {
                return null;
            }

            return DiffAssembly(assemblyName, files);
        }

        /// <summary>
        /// 比较编译出来的程序集中的类型和原有类型，找出其中添加、删除、修改的方法
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <param name="files">修改的文件路径列表</param>
        /// <returns>类型差异结果字典，Key为类型全名，Value为差异结果</returns>
        public static Dictionary<string, DiffResult> DiffAssembly(string assemblyName, List<string> files)
        {
            // 获取最后两个程序集版本
            var newAssemblyDef = TypeInfoHelper.GetAssemblyDefinition(assemblyName, -1);
            var oldAssemblyDef = TypeInfoHelper.GetAssemblyDefinition(assemblyName, -2);
            if (newAssemblyDef == null || oldAssemblyDef == null)
            {
                LoggerScoped.LogError($"无法获取程序集 {assemblyName} ");
                return null;
            }

            // 获取改动文件中的类型
            var changedTypes = TypeInfoHelper.GetTypesFromFiles(files);
            if (changedTypes.Count == 0)
            {
                LoggerScoped.LogDebug($"改动文件中没有找到类型定义");
                return null;
            }
            
            var diffResults = new Dictionary<string, DiffResult>();

            // 对比改动文件中的类型
            foreach (var typeFullName in changedTypes)
            {
                // 从新旧程序集中查找类型定义
                var newTypeDef = newAssemblyDef.MainModule.GetType(typeFullName);
                var oldTypeDef = oldAssemblyDef.MainModule.GetType(typeFullName);;

                if (newTypeDef == null)
                {
                    LoggerScoped.LogDebug($"在新程序集中未找到类型: {typeFullName}");
                    continue;
                }

                DiffResult diffResult;
                if (oldTypeDef == null)
                {
                    diffResult = new DiffResult();

                    // 新增类型，记录所有方法和字段
                    foreach (var method in newTypeDef.Methods)
                    {
                        if (!method.IsConstructor && !method.IsGetter && !method.IsSetter)
                        {
                            diffResult.AddedMethods[method.FullName] = new MethodDiffInfo
                            {
                                FullName = method.FullName,
                                IsGenericMethod = method.HasGenericParameters
                            };
                        }
                    }

                    foreach (var field in newTypeDef.Fields)
                    {
                        diffResult.AddedFields[field.FullName] = new FieldDiffInfo
                        {
                            FullName = field.FullName,
                            DeclaringTypeFullName = typeFullName
                        };
                    }

                    if (diffResult.AddedMethods.Count > 0 || diffResult.AddedFields.Count > 0)
                    {
                        diffResults[typeFullName] = diffResult;
                    }

                    continue;
                }

                // 对比类型差异
                diffResult = CompareTypesWithCecil(oldTypeDef, newTypeDef, typeFullName);

                if (diffResult != null && 
                    (diffResult.AddedMethods.Count > 0 || 
                     diffResult.ModifiedMethods.Count > 0 || 
                     diffResult.AddedFields.Count > 0))
                {
                    diffResults[typeFullName] = diffResult;
                }
            }

            if (diffResults.Count == 0)
            {
                return null;
            }
            
            TypeInfoHelper.UpdateMethodCallGraph(diffResults);

            // 如果泛型方法被修改，将泛型方法的调用者加入修改列表
            foreach (var (typeFullName, diffResult) in diffResults.ToArray())
            {
                foreach (var (methodName, modifiedMethod) in diffResult.ModifiedMethods.ToArray())
                {
                    if (!modifiedMethod.MethodDefinition.HasGenericParameters)
                    {
                        continue;
                    }

                    var callers = TypeInfoHelper.FindMethodCallers(methodName);
                    foreach (var (callerMethodName, methodCallInfo) in callers)
                    {
                        if (!diffResults.TryGetValue(methodCallInfo.TypeName, out var callerDiffResult))
                        {
                            callerDiffResult = new DiffResult();
                            diffResults.Add(methodCallInfo.TypeName, callerDiffResult);
                        }

                        if (!callerDiffResult.ModifiedMethods.ContainsKey(callerMethodName))
                        {
                            callerDiffResult.ModifiedMethods.Add(callerMethodName, new MethodDiffInfo()
                            {
                                FullName = callerMethodName,
                                MethodDefinition = methodCallInfo.MethodDef
                            });
                        }
                    }
                }
            }

            return diffResults;
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个类型的方法差异
        /// </summary>
        /// <param name="oldTypeDef">旧类型定义</param>
        /// <param name="newTypeDef">新类型定义</param>
        /// <param name="typeFullName">类型全名</param>
        /// <returns>差异结果</returns>
        private static DiffResult CompareTypesWithCecil(TypeDefinition oldTypeDef, TypeDefinition newTypeDef, string typeFullName)
        {
            var diffResult = new DiffResult();

            // 创建方法签名到方法的映射
            var oldMethodMap = oldTypeDef.Methods.ToDictionary(m => m.FullName, m => m);
            var newMethodMap = newTypeDef.Methods.ToDictionary(m => m.FullName, m => m);

            // 找出新增的方法
            foreach (var (methodName, newMethodDef) in newMethodMap)
            {
                if (!oldMethodMap.ContainsKey(methodName))
                {
                    diffResult.AddedMethods[methodName] = new MethodDiffInfo
                    {
                        FullName = methodName,
                        IsGenericMethod = newMethodDef.HasGenericParameters,
                        MethodDefinition = newMethodDef
                    };
                    LoggerScoped.LogDebug($"发现新增的方法: {methodName}");
                }
            }

            // 找出修改的方法
            foreach (var (methodName, oldMethodDef) in oldMethodMap)
            {
                if (!newMethodMap.TryGetValue(methodName, out var newMethodDef))
                {
                    continue;
                }

                // 比较方法体是否改变（返回 false 表示有变化）
                if (!CompareMethodDefinitions(oldMethodDef, newMethodDef))
                {
                    diffResult.ModifiedMethods[methodName] = new MethodDiffInfo
                    {
                        FullName = methodName,
                        IsGenericMethod = newMethodDef.HasGenericParameters,
                        MethodDefinition = newMethodDef
                    };
                    LoggerScoped.LogDebug($"发现修改的方法: {methodName}");
                }
            }

            // 比较字段：找出新增的字段
            var oldFieldMap = oldTypeDef.Fields.ToDictionary(f => f.Name, f => f);
            var newFieldMap = newTypeDef.Fields.ToDictionary(f => f.Name, f => f);

            foreach (var (fieldName, newFieldDef) in newFieldMap)
            {
                if (!oldFieldMap.ContainsKey(fieldName))
                {
                    // 发现新增字段
                    diffResult.AddedFields[newFieldDef.FullName] = new FieldDiffInfo
                    {
                        FullName = newFieldDef.FullName,
                        DeclaringTypeFullName = typeFullName
                    };
                    LoggerScoped.LogDebug($"发现新增的字段: {newFieldDef.FullName}");
                }
            }

            return diffResult;
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个方法定义是否相同
        /// </summary>
        /// <param name="existingMethod">现有方法定义</param>
        /// <param name="newMethod">新方法定义</param>
        /// <returns>true 表示方法相同（无变化），false 表示方法不同（有变化）</returns>
        private static bool CompareMethodDefinitions(MethodDefinition existingMethod, MethodDefinition newMethod)
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
        private static bool CompareOperands(Instruction existingInst, Instruction newInst)
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
                    return Mathf.Approximately(existingFloat, newFloat);

                case double existingDouble when newInst.Operand is double newDouble:
                    return existingDouble == newDouble;

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
                    if (existingMethodDef.DeclaringType.IsNested)
                    {
                        return CompareMethodDefinitions(existingMethodDef, newMethodDef);
                    }
                    return existingMethodDef.FullName == newMethodDef.FullName;

                case MethodReference existingMethodRef when newInst.Operand is MethodReference newMethodRef:
                    return existingMethodRef.FullName == newMethodRef.FullName;

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
    }
}

/// <summary>
/// 差异结果
/// </summary>
public class DiffResult
{
    /// <summary>
    /// 新增的方法
    /// </summary>
    public Dictionary<string, MethodDiffInfo> AddedMethods { get; } = new();

    /// <summary>
    /// 修改的方法
    /// </summary>
    public Dictionary<string, MethodDiffInfo> ModifiedMethods { get; } = new();

    /// <summary>
    /// 新增的字段
    /// </summary>
    public Dictionary<string, FieldDiffInfo> AddedFields { get; } = new();
}

/// <summary>
/// 方法差异信息
/// </summary>
public class MethodDiffInfo
{
    /// <summary>
    /// 方法全名（包含返回类型、声明类型、方法名、参数类型）
    /// </summary>
    public string FullName { get; set; }

    /// <summary>
    /// 是否为泛型方法
    /// </summary>
    public bool IsGenericMethod { get; set; }

    /// <summary>
    /// 方法声明类型
    /// </summary>
    public MethodDefinition MethodDefinition;
}

/// <summary>
/// 字段差异信息
/// </summary>
public class FieldDiffInfo
{
    /// <summary>
    /// 声明类型全名
    /// </summary>
    public string DeclaringTypeFullName { get; set; }
        
    /// <summary>
    /// 字段全名
    /// </summary>
    public string FullName { get; set; }
}