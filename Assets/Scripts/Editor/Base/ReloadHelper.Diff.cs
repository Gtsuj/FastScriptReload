using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Diff功能 - 比较编译程序集与原程序集的差异
    /// </summary>
    public static partial class ReloadHelper
    {
        /// <summary>
        /// 比较编译出来的程序集中的类型和原有类型，找出其中添加、删除、修改的方法
        /// </summary>
        /// <param name="changeFiles">修改的文件路径列表</param>
        /// <returns>程序集差异结果</returns>
        public static List<HookTypeInfo> DiffAssembly(List<string> changeFiles)
        {
            // 从内存流读取程序集定义
            AssemblyDefinition newAssemblyDef = _assemblyDefinition;
            
            // 步骤1: 遍历 changeFiles，分析出文件中包含的类型，获取完整的类型名 -> 文件路径集合映射
            var typeToFilePathMap = TypeSourceIndex.GetTypeToFilesMap(changeFiles);
            
            if (typeToFilePathMap.Count == 0)
            {
                LoggerScoped.LogWarning("未能从修改的文件中提取到任何类型");
                return null;
            }

            LoggerScoped.LogDebug($"从 {changeFiles.Count} 个文件中提取到 {typeToFilePathMap.Count} 个类型: {string.Join(", ", typeToFilePathMap.Keys)}");

            var hookTypeInfos = new List<HookTypeInfo>();
            
            // 步骤2: 使用获取到的完整类型从编译出的Assembly中进行Diff
            foreach (var kvp in typeToFilePathMap)
            {
                var typeFullName = kvp.Key;
                var filePaths = kvp.Value;

                // 从编译出的程序集中查找类型定义
                var newTypeDef = newAssemblyDef.MainModule.GetType(typeFullName);
                if (newTypeDef == null)
                {
                    LoggerScoped.LogDebug($"在编译出的程序集中未找到类型: {typeFullName}");
                    continue;
                }

                // 查找原有类型（使用反射）
                if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeFullName, out var existingType))
                {
                    var typeDiff = CompareTypesWithCecil(existingType, newTypeDef);
                    if (typeDiff != null)
                    {
                        typeDiff.SourceFilePaths = filePaths;
                        hookTypeInfos.Add(typeDiff);
                    }
                }
                else
                {
                    // 新添加的类型（不在原有程序集中）
                    LoggerScoped.LogDebug($"类型 {typeFullName} 是新添加的类型，不在原有程序集中");
                }

            }

            return hookTypeInfos;
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个类型的方法差异
        /// </summary>
        private static HookTypeInfo CompareTypesWithCecil(Type existingType, TypeDefinition newTypeDef)
        {
            if (!HookTypeInfoCache.TryGetValue(existingType.FullName, out var typeDiff))
            {
                typeDiff = new HookTypeInfo
                {
                    TypeFullName = existingType.FullName,
                    ExistingType = existingType,
                    NewTypeDefinition = newTypeDef // 保存新类型定义
                };
            }
        
            var existingAssemblyPath = existingType.Assembly?.Location;
            if (string.IsNullOrEmpty(existingAssemblyPath))
            {
                LoggerScoped.LogDebug($"无法读取原有程序集路径: {existingAssemblyPath}");
                return null;
            }

            var existingAssemblyDef = GetOrReadAssemblyDefinition(existingAssemblyPath);
            if (existingAssemblyDef == null)
            {
                LoggerScoped.LogDebug($"无法读取原有程序集: {existingAssemblyPath}");
                return null;
            }
            var existingTypeDef = existingAssemblyDef.MainModule.Types.FirstOrDefault(t => t.FullName == existingType.FullName);

            if (existingTypeDef == null)
            {
                return null;
            }

            // 创建方法签名到方法的映射
            var existingMethodMap = existingTypeDef.Methods.ToDictionary(
                m => m.FullName, m => m
            );

            var newMethodMap = newTypeDef.Methods.ToDictionary(
                m => m.FullName, m => m
            );

            // 找出添加的方法
            foreach (var (name, def) in newMethodMap)
            {
                if (!existingMethodMap.ContainsKey(name))
                {
                    // 检查是否是之前新增的方法
                    if (typeDiff.AddedMethods.TryGetValue(name, out var addedMethodInfo))
                    {
                        // 这是一个之前新增的方法，需要比较是否被修改了
                        var previousMethodDef = addedMethodInfo.MethodDefinition;
                        addedMethodInfo.IsDirty = CompareMethodDefinitions(previousMethodDef, def);
                    }
                    else
                    {
                        addedMethodInfo = new AddedMethodInfo() { IsDirty = true };
                        LoggerScoped.LogDebug($"发现新增的方法: {name}");
                    }

                    typeDiff.AddedMethods[name] = addedMethodInfo;
                }
            }

            // 找出修改的方法
            var methods = existingType.GetAllMethods().ToArray();
            foreach (var (name, def) in existingMethodMap)
            {
                if (!newMethodMap.TryGetValue(name, out var newMethodDef))
                {
                    continue;
                }

                var existingMethodDef = def;
                
                // 方法修改过，使用修改后的方法定义比较
                if (typeDiff.ModifiedMethods.TryGetValue(name, out var modifiedMethodInfo))
                { 
                    existingMethodDef = modifiedMethodInfo.MethodDefinition;
                }

                if (!CompareMethodDefinitions(existingMethodDef, newMethodDef))
                {
                    continue;
                }

                if (!typeDiff.ModifiedMethods.TryGetValue(def.FullName, out var modifiedMethod))
                {
                    modifiedMethod = new UpdateMethodInfo
                    {
                        OriginalMethod = methods.FirstOrDefault((info => info.FullName().Equals(existingMethodDef.FullName)))
                    };

                    typeDiff.ModifiedMethods.Add(existingMethodDef.FullName, modifiedMethod);
                }

                modifiedMethod.IsDirty = true;

                LoggerScoped.LogDebug($"发现修改的方法: {existingMethodDef.FullName}");
            }

            if (typeDiff.AddedMethods.Count == 0 &&
                typeDiff.RemovedMethods.Count == 0 &&
                typeDiff.ModifiedMethods.Count == 0)
            {
                return null;
            }

            return typeDiff;
        }

        /// <summary>
        /// 使用 Mono.Cecil 比较两个方法定义
        /// </summary>
        private static bool CompareMethodDefinitions(MethodDefinition existingMethod, MethodDefinition newMethod)
        {
            // 检查方法体是否存在
            if (!existingMethod.HasBody && !newMethod.HasBody)
            {
                return false; // 都没有方法体，认为相同
            }

            if (!existingMethod.HasBody || !newMethod.HasBody)
            {
                return true; // 一个有方法体，一个没有，认为不同
            }

            var existingBody = existingMethod.Body;
            var newBody = newMethod.Body;

            // 比较局部变量数量
            if (existingBody.Variables.Count != newBody.Variables.Count)
            {
                return true;
            }

            // 比较异常处理块数量
            if (existingBody.ExceptionHandlers.Count != newBody.ExceptionHandlers.Count)
            {
                return true;
            }

            // 比较 IL 指令序列
            var existingInstructions = existingBody.Instructions.ToList();
            var newInstructions = newBody.Instructions.ToList();

            if (existingInstructions.Count != newInstructions.Count)
            {
                return true;
            }

            // 逐条比较指令
            for (int i = 0; i < existingInstructions.Count; i++)
            {
                var existingInst = existingInstructions[i];
                var newInst = newInstructions[i];

                // 比较操作码
                if (existingInst.OpCode.Code != newInst.OpCode.Code)
                {
                    return true;
                }

                // 比较操作数（忽略元数据令牌的差异）
                if (!CompareOperands(existingInst, newInst))
                {
                    return true;
                }
            }

            return false; // 所有比较都通过，认为方法体没有变化
        }

        /// <summary>
        /// 比较两个指令的操作数，忽略元数据令牌的差异
        /// </summary>
        private static bool CompareOperands(Instruction existingInst, Instruction newInst)
        {
            // 如果都没有操作数，认为相同
            if (existingInst.Operand == null && newInst.Operand == null)
            {
                return true;
            }

            if (existingInst.Operand == null || newInst.Operand == null)
            {
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
                    return existingFloat == newFloat;

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

                case MethodReference existingMethodRef when newInst.Operand is MethodReference newMethodRef:
                    return existingMethodRef.FullName == newMethodRef.FullName;

                case FieldReference existingFieldRef when newInst.Operand is FieldReference newFieldRef:
                    return existingFieldRef.FullName == newFieldRef.FullName;

                // 对于指令引用（跳转目标），比较跳转的偏移量
                case Instruction existingTarget when newInst.Operand is Instruction newTarget:
                    // 跳转目标可能不同，但跳转的逻辑应该相同
                    // 这里简化处理：如果操作码相同，认为跳转逻辑相同
                    return true;

                default:
                    // 其他类型，使用默认比较
                    return existingInst.Operand.Equals(newInst.Operand);
            }
        }
    }
}
