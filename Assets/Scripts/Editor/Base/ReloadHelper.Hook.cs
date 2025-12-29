using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FastScriptReload.Runtime;
using HarmonyLib;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Newtonsoft.Json;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Hook应用功能 - 应用热重载Hook到原有方法
    /// </summary>
    public static partial class ReloadHelper
    {
        /// <summary>
        /// 初始化时根据HookTypeInfoCache重建Hook
        /// </summary>
        public static void RebuildHooks()
        {
            if (!File.Exists(HOOK_TYPE_INFO_CACHE_PATH))
            {
                return;
            }

            var cache = File.ReadAllText(HOOK_TYPE_INFO_CACHE_PATH);
            HookTypeInfoCache = JsonConvert.DeserializeObject<Dictionary<string, HookTypeInfo>>(cache);

            foreach (var (typeFullName, hookTypeInfo) in HookTypeInfoCache)
            {
                var originType = ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.GetValueOrDefault(typeFullName);

                foreach (var (methodName, modifiedMethod) in hookTypeInfo.ModifiedMethods)
                {
                    if (modifiedMethod.HasGenericParameters)
                    {
                        continue;
                    }

                    var wrapperAssembly = Assembly.LoadFrom(modifiedMethod.AssemblyPath);
                    Type wrapperType = wrapperAssembly.GetType(typeFullName);
                    if (wrapperType == null)
                    {
                        LoggerScoped.LogWarning($"在 Wrapper 程序集中找不到类型: {typeFullName}");
                        return;
                    }

                    // 获取 Wrapper 类型中的所有静态方法（公有和私有）
                    var wrapperMethod = wrapperType.GetMethodByMethodDefinitionName(modifiedMethod.WrapperMethodName);
                    
                    MethodHelper.DisableVisibilityChecks(wrapperMethod);

                    if (modifiedMethod.MemberModifyState == MemberModifyState.Added)
                    {
                        AddedMethodHookHandle(methodName, modifiedMethod, wrapperMethod);
                    }
                    else
                    {
                        var originMethod = originType.GetMethodByMethodDefinitionName(modifiedMethod.MemberFullName);
                        if (originMethod == null)
                        {
                            return;
                        }

                        Hook(methodName, originMethod, wrapperMethod);
                    }
                }
            }
        }

        /// <summary>
        /// 应用热重载Hook，将Wrapper程序集中的静态方法Hook到原有方法上
        /// Hook成功后保存到全局容器中
        /// </summary>
        /// <param name="diffResults">差异结果</param>
        public static void ApplyHooks(Dictionary<string, DiffResult> diffResults)
        {
            foreach (var (typeFullName, result) in diffResults)
            {
                if (!HookTypeInfoCache.TryGetValue(typeFullName, out var hookTypeInfo))
                {
                    continue;
                }

                var originType = ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.GetValueOrDefault(typeFullName);
                var originMethod = originType.GetAllMethods(_assemblyDefinition.MainModule);

                foreach (var (methodName, modifiedMethod) in hookTypeInfo.ModifiedMethods)
                {
                    if (!result.AddedMethods.ContainsKey(methodName) && !result.ModifiedMethods.ContainsKey(methodName))
                    {
                        continue;
                    }

                    if (modifiedMethod.HasGenericParameters)
                    {
                        continue;
                    }

                    var wrapperAssembly = Assembly.LoadFrom(modifiedMethod.AssemblyPath);

                    // 从 Wrapper 程序集中找到对应的类型
                    Type wrapperType = wrapperAssembly.GetType(typeFullName);
                    if (wrapperType == null)
                    {
                        LoggerScoped.LogWarning($"在 Wrapper 程序集中找不到类型: {typeFullName}");
                        return;
                    }

                    // 获取 Wrapper 类型中的所有静态方法（公有和私有）
                    var wrapperMethods = wrapperType.GetAllMethods(_assemblyDefinition.MainModule);
                    if (!wrapperMethods.TryGetValue(modifiedMethod.WrapperMethodName, out var wrapperMethod))
                    {
                        continue;
                    }

                    MethodHelper.DisableVisibilityChecks(wrapperMethod);

                    if (modifiedMethod.MemberModifyState == MemberModifyState.Added)
                    {
                        AddedMethodHookHandle(methodName, modifiedMethod, wrapperMethod);
                    }
                    else
                    {
                        Hook(methodName, originMethod.GetValueOrDefault(methodName), wrapperMethod);
                    }
                }
            }
        }

        private static void AddedMethodHookHandle(string methodName, HookMethodInfo modifiedMethod, MethodBase wrapperMethod)
        {
            foreach (var historicalMethod in modifiedMethod.HistoricalHookedMethods)
            {
                if (historicalMethod.Equals(wrapperMethod))
                {
                    return;
                }
                Hook(methodName, historicalMethod, wrapperMethod);
            }

            if (modifiedMethod.HistoricalHookedMethods.Count == 0)
            {
                LoggerScoped.Log($"Hook Add Func Success: {methodName}");
            }

            modifiedMethod.AddHistoricalHookedMethod(wrapperMethod);
        }

        private static void Hook(string methodFullName, MethodBase original, MethodBase replacement)
        {
            // 执行 Hook
            var errorMessage = Memory.DetourMethod(original, replacement);

            if (string.IsNullOrEmpty(errorMessage))
            {
                LoggerScoped.Log($"Hook Success: {methodFullName}");
            }
            else
            {
                LoggerScoped.LogError($"Hook Failed: {methodFullName}, Error: {errorMessage}");
            }
        }
    }
}