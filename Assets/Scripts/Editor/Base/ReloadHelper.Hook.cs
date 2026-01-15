using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HookInfo.Models;
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
        /// 应用热重载Hook，将Wrapper程序集中的静态方法Hook到原有方法上
        /// Hook成功后保存到全局容器中
        /// </summary>
        /// <param name="hookTypeInfos">编译结果</param>
        public static void ApplyHooks(Dictionary<string, HookTypeInfo> hookTypeInfos)
        {
            foreach (var (typeFullName, hookTypeInfo) in hookTypeInfos)
            {
                HookField(hookTypeInfo);

                HookMethod(hookTypeInfo);
            }
        }

        private static void HookField(HookTypeInfo hookTypeInfo)
        {
            foreach (var (name, fieldInfo) in hookTypeInfo.ModifiedFields)
            {
                FastScriptReloadHookDetailsWindow.NotifyMemberHooked(name, true);
            }
        }

        private static void HookMethod(HookTypeInfo hookTypeInfo)
        {
            var typeFullName = hookTypeInfo.TypeFullName;
            var originType = Type.GetType($"{typeFullName},{hookTypeInfo.AssemblyName}");

            if (originType == null)
            {
                var errorMsg = $"找不到要Hook的原类型查找: {typeFullName},{hookTypeInfo.AssemblyName}";
                LoggerScoped.LogError(errorMsg);
                FastScriptReloadHookDetailsWindow.NotifyHookFailed(errorMsg);
                return;
            }
            
            foreach (var (methodName, modifiedMethod) in hookTypeInfo.ModifiedMethods)
            {
                if (modifiedMethod.HasGenericParameters)
                {
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(methodName, true);
                    continue;
                }

                var wrapperAssembly = Assembly.LoadFrom(modifiedMethod.AssemblyPath);

                // 从 Wrapper 程序集中找到对应的类型
                Type wrapperType = wrapperAssembly.GetType(originType.FullName);
                if (wrapperType == null)
                {
                    var errorMsg = $"在 Wrapper 程序集中找不到类型: {typeFullName}";
                    LoggerScoped.LogError(errorMsg);
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(modifiedMethod.WrapperMethodName, false, errorMsg);
                    continue;
                }

                // 获取 Wrapper 类型中的所有静态方法（公有和私有）
                var wrapperMethod = wrapperType.GetMethodByMethodDefName(modifiedMethod.WrapperMethodName);
                if (wrapperMethod == null)
                {
                    var errorMsg = $"在 Wrapper 程序集中找不到方法 {modifiedMethod.WrapperMethodName}";
                    LoggerScoped.LogError(errorMsg);
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(modifiedMethod.WrapperMethodName, false, errorMsg);
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