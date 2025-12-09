using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using HarmonyLib;
using ImmersiveVrToolsCommon.Runtime.Logging;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Hook应用功能 - 应用热重载Hook到原有方法
    /// </summary>
    public static partial class ReloadHelper
    {
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

                var wrapperAssemblyPath = hookTypeInfo.WrapperAssemblyPath;
                if (wrapperAssemblyPath == null)
                {
                    continue;
                }

                if (!AssemblyCache.TryGetValue(wrapperAssemblyPath, out var wrapperAssembly))
                {
                    wrapperAssembly = Assembly.LoadFrom(wrapperAssemblyPath);
                    AssemblyCache[wrapperAssemblyPath] = wrapperAssembly;
                }

                // 从 Wrapper 程序集中找到对应的类型
                Type wrapperType = wrapperAssembly.GetType(typeFullName);
                if (wrapperType == null)
                {
                    LoggerScoped.LogWarning($"在 Wrapper 程序集中找不到类型: {typeFullName}");
                    return;
                }

                // 获取 Wrapper 类型中的所有静态方法（公有和私有）
                var wrapperMethods = wrapperType.GetAllMethods();
                foreach (var (methodName, methodDiffInfo) in result.ModifiedMethods)
                {
                    if (!hookTypeInfo.ModifiedMethods.TryGetValue(methodName, out var modifiedMethod))
                    {
                        continue;
                    }

                    if (modifiedMethod.MethodDefinition.IsGenericInstance)
                    {
                        continue;
                    }

                    var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.FullName() == modifiedMethod.WrapperMethodName);
                    if (wrapperMethod == null)
                    {
                        continue;
                    }

                    MethodHelper.DisableVisibilityChecks(wrapperMethod);

                    if (modifiedMethod.HookMethodState == HookMethodState.Added)
                    {
                        AddedMethodHookHandle(methodName, modifiedMethod, wrapperMethod);
                    }
                    else
                    {
                        Hook(methodName, modifiedMethod.OriginalMethod, wrapperMethod);
                    }
                }
            }
        }

        private static void AddedMethodHookHandle(string methodName, HookMethodInfo modifiedMethod, MethodBase wrapperMethod)
        {
            foreach (var historicalMethod in modifiedMethod.HistoricalHookedMethods)
            {
                Hook(methodName, historicalMethod, wrapperMethod);
            }

            if (modifiedMethod.HistoricalHookedMethods.Count == 0)
            {
                LoggerScoped.Log($"Hook Add Func Success: {methodName}");
            }

            modifiedMethod.HistoricalHookedMethods.Add(wrapperMethod);
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