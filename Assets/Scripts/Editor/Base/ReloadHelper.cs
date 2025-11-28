using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FastScriptReload.Runtime;
using HarmonyLib;
using ImmersiveVRTools.Runtime.Common.Extensions;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using UnityEditor;

namespace FastScriptReload.Editor
{
    public static class ReloadHelper
    {
        public static string AssemblyPath;

        /// <summary>
        /// 修改过的类型
        /// </summary>
        public static Dictionary<string, HookTypeInfo> HookTypeInfoCache = new();

        /// <summary>
        /// 全局缓存已加载的 Wrapper 程序集（按程序集名称索引）
        /// </summary>
        public static Dictionary<string, Assembly> AssemblyCache = new();

        [InitializeOnEnterPlayMode]
        public static void Init()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault())
            {
                return;
            }

            // 获取工程名
            var projectName = Path.GetFileName(Path.GetDirectoryName(Application.dataPath));
            if (string.IsNullOrEmpty(projectName))
            {
                // 如果 productName 为空，从项目路径获取
                var projectPath = Application.dataPath;
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var dirInfo = new DirectoryInfo(projectPath);
                    projectName = dirInfo.Parent?.Name ?? "UnityProject";
                }
                else
                {
                    projectName = "UnityProject";
                }
            }

            // 创建保存目录：%LOCALAPPDATA%\Temp\{工程名}
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AssemblyPath = Path.Combine(localAppData, "Temp", "FastScriptReloadTemp", projectName);

            if (!Directory.Exists(AssemblyPath))
            {
                Directory.CreateDirectory(AssemblyPath);
            }


            HotReloadCache.ParseOptions = new CSharpParseOptions(
                preprocessorSymbols: EditorUserBuildSettings.activeScriptCompilationDefines,
                languageVersion: LanguageVersion.Latest
            );
        }

        /// <summary>
        /// 比较编译出来的程序集中的类型和原有类型，找出其中添加、删除、修改的方法
        /// </summary>
        /// <param name="changeFiles">修改的文件路径列表</param>
        /// <returns>程序集差异结果</returns>
        public static List<HookTypeInfo> DiffAssembly(List<string> changeFiles)
        {
            // 从内存流读取程序集定义
            AssemblyDefinition newAssemblyDef = HotReloadCache.AssemblyDefinition;
            
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
        /// 应用热重载Hook，将Wrapper程序集中的静态方法Hook到原有方法上
        /// Hook成功后保存到全局容器中
        /// </summary>
        /// <param name="hookTypeInfos">程序集差异结果</param>
        public static void ApplyHooks(List<HookTypeInfo> hookTypeInfos)
        {
            // 直接遍历所有类型差异，应用Hook
            foreach (var typeInfo in hookTypeInfos)
            {
                try
                {
                    // 从路径获取程序集名称
                    var assemblyName = Path.GetFileNameWithoutExtension(typeInfo.WrapperAssemblyPath);
                    
                    // 从全局缓存获取或加载程序集
                    if (!AssemblyCache.TryGetValue(assemblyName, out var wrapperAssembly))
                    {
                        wrapperAssembly = Assembly.LoadFrom(typeInfo.WrapperAssemblyPath);
                        AssemblyCache[assemblyName] = wrapperAssembly;
                    }

                    // 应用Hook
                    ApplyHooksForType(typeInfo, wrapperAssembly);
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogError($"处理类型差异时发生异常: {typeInfo.TypeFullName}, 错误: {ex.Message}");
                }
            }
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

            var existingAssemblyDef = HotReloadCache.GetOrReadAssemblyDefinition(existingAssemblyPath);
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
                m => m.FullDescription(), m => m
            );

            var newMethodMap = newTypeDef.Methods.ToDictionary(
                m => m.FullDescription(), m => m
            );

            // 找出添加的方法
            foreach (var newMethodPair in newMethodMap)
            {
                if (!existingMethodMap.ContainsKey(newMethodPair.Key))
                {
                    // 检查是否是之前新增的方法
                    if (typeDiff.AddedMethods.TryGetValue(newMethodPair.Key, out var addedMethodInfo))
                    {
                        // 这是一个之前新增的方法，需要比较是否被修改了
                        var previousMethodDef = addedMethodInfo.MethodDefinition;
                        var isModified = CompareMethodDefinitions(previousMethodDef, newMethodPair.Value);

                        addedMethodInfo.MethodDefinition = newMethodPair.Value;
                        addedMethodInfo.IsDirty = isModified; // 标记是否有改动
                    }
                    else
                    {
                        addedMethodInfo = new AddedMethodInfo
                        {
                            MethodDefinition = newMethodPair.Value,
                            IsDirty = false // 新方法默认为未修改
                        };
                        LoggerScoped.LogDebug($"发现新增的方法: {newMethodPair.Value.FullName}");
                    }

                    typeDiff.AddedMethods[newMethodPair.Key] = addedMethodInfo;
                }
            }

            // 找出修改的方法
            var methods = existingType.GetMethods(AssemblyChangesLoader.ALL_DECLARED_METHODS_BINDING_FLAGS);
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
                
                var existingMethodInfo = methods.FirstOrDefault((info => info.FullDescription().Equals(existingMethodDef.FullDescription())));
                if (existingMethodInfo == null)
                {
                    continue;
                }

                typeDiff.ModifiedMethods[existingMethodDef.FullDescription()] = new UpdateMethodInfo
                {
                    ExistingMethod = existingMethodInfo,
                    MethodDefinition = newMethodDef
                };

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

            // 比较最大栈大小
            if (existingBody.MaxStackSize != newBody.MaxStackSize)
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

            // 比较操作数类型
            var existingType = existingInst.Operand.GetType();
            var newType = newInst.Operand.GetType();

            if (existingType != newType)
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

        /// <summary>
        /// 为单个类型应用Hook
        /// </summary>
        private static void ApplyHooksForType(HookTypeInfo typeInfo, Assembly wrapperAssembly)
        {
            // 从 Wrapper 程序集中找到对应的类型
            Type wrapperType = wrapperAssembly.GetType(typeInfo.TypeFullName);
            if (wrapperType == null)
            {
                LoggerScoped.LogWarning($"在 Wrapper 程序集中找不到类型: {typeInfo.TypeFullName}");
                return;
            }

            // 获取 Wrapper 类型中的所有静态方法（公有和私有）
            var wrapperMethods = wrapperType.GetMethods(AssemblyChangesLoader.ALL_DECLARED_METHODS_BINDING_FLAGS);

            // 遍历修改的方法，进行Hook
            foreach (var (methodFullName, methodPair) in typeInfo.ModifiedMethods)
            {
                // 直接通过 NewMethodDefinition.Name 查找匹配的方法
                if (methodPair.MethodDefinition == null || string.IsNullOrEmpty(methodPair.MethodDefinition.Name))
                {
                    methodPair.ErrorMessage = $"NewMethodDefinition 或方法名为空，无法查找方法";
                    LoggerScoped.LogWarning(methodPair.ErrorMessage);
                    continue;
                }

                var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.FullDescription() == methodFullName);

                if (wrapperMethod == null)
                {
                    methodPair.ErrorMessage = $"在 Wrapper 程序集中找不到方法: {methodFullName}";
                    LoggerScoped.LogWarning(methodPair.ErrorMessage);
                    continue;
                }
                
                MethodHelper.DisableVisibilityChecks(wrapperMethod);
                
                // 执行 Hook
                var errorMessage = Memory.DetourMethod(methodPair.ExistingMethod, wrapperMethod);
                
                if (string.IsNullOrEmpty(errorMessage))
                {
                    // Hook 成功
                    methodPair.NewMethod = wrapperMethod;
                    LoggerScoped.Log($"Hook Success: {methodFullName}");
                }
                else
                {
                    // Hook 失败
                    methodPair.ErrorMessage = errorMessage;
                    LoggerScoped.LogError($"Hook Failed: {methodFullName}, Error: {errorMessage}");
                }
            }

            // 遍历新增的方法，进行Hook
            foreach (var addedMethod in typeInfo.AddedMethods)
            {
                var addedMethodInfo = addedMethod.Value;

                var methodName = addedMethodInfo.MethodDefinition.Name;
                var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.Name == methodName);

                MethodHelper.DisableVisibilityChecks(wrapperMethod);

                if (addedMethodInfo.CurrentMethod != null)
                {
                    addedMethodInfo.HistoricalHookedMethods.Add(addedMethodInfo.CurrentMethod);
                }

                addedMethodInfo.CurrentMethod = wrapperMethod;

                // 如果 IsDirty = true，需要重新Hook历史Hook的方法
                if (addedMethodInfo.IsDirty && addedMethodInfo.HistoricalHookedMethods != null && addedMethodInfo.HistoricalHookedMethods.Count > 0)
                {
                    foreach (var historicalMethod in addedMethodInfo.HistoricalHookedMethods)
                    {
                        if (historicalMethod == null)
                        {
                            continue;
                        }

                        // 重新Hook历史方法到新的Wrapper方法
                        var errorMessage = Memory.DetourMethod(historicalMethod, wrapperMethod);

                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            LoggerScoped.LogWarning($"Hook Failed: {historicalMethod.DeclaringType?.FullName}.{historicalMethod.Name} -> {methodName}, Error: {errorMessage}");
                            return;
                        }
                    }

                    addedMethodInfo.IsDirty = false; // 重置dirty标记
                }

                LoggerScoped.Log($"Hook Success: {typeInfo.TypeFullName}.{methodName}");
            }
        }
    }
}