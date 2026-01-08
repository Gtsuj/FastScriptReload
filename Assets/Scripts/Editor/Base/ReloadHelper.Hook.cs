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

                    if (string.IsNullOrEmpty(modifiedMethod.AssemblyPath) || !File.Exists(modifiedMethod.AssemblyPath))
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
                    var wrapperMethod = wrapperType.GetMethodByMethodDefName(modifiedMethod.WrapperMethodName);
                    
                    MethodHelper.DisableVisibilityChecks(wrapperMethod);

                    if (modifiedMethod.MemberModifyState == MemberModifyState.Added)
                    {
                        AddedMethodHookHandle(methodName, modifiedMethod, wrapperMethod);
                    }
                    else
                    {
                        var originMethod = originType.GetMethodByMethodDefName(modifiedMethod.MemberFullName);
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

                HookField(hookTypeInfo, result);
                
                HookMethod(hookTypeInfo, result);
            }
        }

        private static void HookField(HookTypeInfo hookTypeInfo, DiffResult result)
        {
            foreach (var (name, fieldDiffInfo) in result.AddedFields)
            {
                FastScriptReloadHookDetailsWindow.NotifyMemberHooked(name, true);
            }
        }

        private static void HookMethod(HookTypeInfo hookTypeInfo, DiffResult result)
        {
            var typeFullName = hookTypeInfo.TypeFullName;
            var originType = ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.GetValueOrDefault(typeFullName);

            foreach (var (methodName, modifiedMethod) in hookTypeInfo.ModifiedMethods)
            {
                if (!result.ModifiedMethods.ContainsKey(methodName))
                {
                    continue;
                }

                if (modifiedMethod.HasGenericParameters)
                {
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(methodName, true);
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
                var wrapperMethod = wrapperType.GetMethodByMethodDefName(modifiedMethod.WrapperMethodName);
                if (wrapperMethod == null)
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
                    Hook(methodName, originType.GetMethodByMethodDefName(methodName), wrapperMethod);
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
                FastScriptReloadHookDetailsWindow.NotifyMemberHooked(methodName, true);
            }
            
            modifiedMethod.AddHistoricalHookedMethod(wrapperMethod);
        }

        private static void Hook(string methodFullName, MethodBase original, MethodBase replacement)
        {
            // 执行 Hook
            var errorMessage = Memory.DetourMethod(original, replacement);

            bool success = string.IsNullOrEmpty(errorMessage);

            if (!string.IsNullOrEmpty(errorMessage))
            {
                LoggerScoped.LogError($"Hook Failed: {methodFullName}, Error: {errorMessage}");
            }

            FastScriptReloadHookDetailsWindow.NotifyMemberHooked(methodFullName, success, errorMessage);
        }
    }
}