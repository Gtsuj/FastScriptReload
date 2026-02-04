using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HookInfo.Models;
using HookInfo.Runtime;
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
        /// <param name="hookTypeInfos">编译结果</param>
        public static void ApplyHooks(Dictionary<string, HookTypeInfo> hookTypeInfos)
        {
            foreach (var (typeFullName, hookTypeInfo) in hookTypeInfos)
            {
                var originType = Type.GetType($"{typeFullName},{hookTypeInfo.AssemblyName}");

                if (originType == null)
                {
                    var errorMsg = $"找不到要Hook的原类型查找: {typeFullName},{hookTypeInfo.AssemblyName}";
                    LoggerScoped.LogError(errorMsg);
                    FastScriptReloadHookDetailsWindow.NotifyHookFailed(errorMsg);
                    return;
                }

                HookField(originType, hookTypeInfo);

                HookMethod(originType, hookTypeInfo);
            }
        }

        private static void HookField(Type originType, HookTypeInfo hookTypeInfo)
        {
            foreach (var (name, fieldInfo) in hookTypeInfo.ModifiedFields)
            {
                if (!string.IsNullOrEmpty(fieldInfo.InitializerMethodName))
                {
                    // 获取字段初始化方法
                    var initializerMethod = LoadWrapperMethod(fieldInfo.AssemblyPath, fieldInfo.TypeName, fieldInfo.InitializerMethodName, out string errorMsg);
                    if (initializerMethod == null)
                    {
                        LoggerScoped.LogError(errorMsg);
                        FastScriptReloadHookDetailsWindow.NotifyMemberHooked(name, false, errorMsg);
                        continue;
                    }

                    var initialValue = initializerMethod.Invoke(null, null);
                    var registerFieldFunc = typeof(FieldResolver<>).MakeGenericType(originType).GetMethod("RegisterFieldInitializer");
                    registerFieldFunc?.Invoke(null, new[] { fieldInfo.FieldName, initialValue });
                }

                FastScriptReloadHookDetailsWindow.NotifyMemberHooked(name, true);
            }
        }

        private static void HookMethod(Type originType, HookTypeInfo hookTypeInfo)
        {            
            foreach (var (methodName, modifiedMethod) in hookTypeInfo.ModifiedMethods)
            {
                if (modifiedMethod.HasGenericParameters)
                {
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(methodName, true);
                    continue;
                }

                var wrapperMethod = LoadWrapperMethod(modifiedMethod.AssemblyPath, originType.FullName, modifiedMethod.WrapperMethodName,
                out string errorMsg);

                if (wrapperMethod == null)
                {
                    LoggerScoped.LogError(errorMsg);
                    FastScriptReloadHookDetailsWindow.NotifyMemberHooked(modifiedMethod.WrapperMethodName, false, errorMsg);
                    continue;
                }

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

        /// <summary>
        /// 从指定的程序集路径加载 Wrapper 方法
        /// </summary>
        /// <param name="assemblyPath">程序集文件路径</param>
        /// <param name="typeFullName">类型全名</param>
        /// <param name="methodName">方法名</param>
        /// <param name="errorMsg">错误信息(如果失败)</param>
        /// <returns>找到的方法,失败则返回 null</returns>
        private static MethodBase LoadWrapperMethod(string assemblyPath, string typeFullName, string methodName, out string errorMsg)
        {
            errorMsg = null;

            // 加载程序集
            var wrapperAssembly = Assembly.LoadFrom(assemblyPath);

            // 从 Wrapper 程序集中找到对应的类型
            Type wrapperType = wrapperAssembly.GetType(typeFullName);
            if (wrapperType == null)
            {
                errorMsg = $"在 Wrapper 程序集中找不到类型: {typeFullName}";
                return null;
            }

            // 获取 Wrapper 类型中的方法
            var wrapperMethod = wrapperType.GetMethodByMethodDefName(methodName);
            if (wrapperMethod == null)
            {
                errorMsg = $"在 Wrapper 程序集中找不到方法 {methodName}";
                return null;
            }

            // 禁用可见性检查
            MethodHelper.DisableVisibilityChecks(wrapperMethod);
            foreach (var nestedType in wrapperType.GetNestedTypes(AccessTools.allDeclared))
            {
                foreach (var nestedMethod in nestedType.GetMethods(AccessTools.allDeclared))
                {
                    MethodHelper.DisableVisibilityChecks(nestedMethod);
                }
            }

            return wrapperMethod;
        }

        private static void AddedMethodHookHandle(string methodName, HookMethodInfo modifiedMethod, MethodBase wrapperMethod)
        {
            for (int i = 0; i < modifiedMethod.HistoricalHookedMethods.Count - 1; i++)
            {
                var historicalMethod = modifiedMethod.HistoricalHookedMethods[i];
                if (historicalMethod.Equals(wrapperMethod))
                {
                    return;
                }
                Hook(methodName, historicalMethod, wrapperMethod);
            }

            if (modifiedMethod.HistoricalHookedMethods.Count == 1)
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