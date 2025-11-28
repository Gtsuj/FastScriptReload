using System;
using System.Collections.Generic;
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
        /// <param name="hookTypeInfos">程序集差异结果</param>
        public static void ApplyHooks(List<HookTypeInfo> hookTypeInfos)
        {
            // 直接遍历所有类型差异，应用Hook
            foreach (var typeInfo in hookTypeInfos)
            {
                // 从路径获取程序集名称
                var assemblyName = System.IO.Path.GetFileNameWithoutExtension(typeInfo.WrapperAssemblyPath);
                    
                // 从全局缓存获取或加载程序集
                if (!AssemblyCache.TryGetValue(assemblyName, out var wrapperAssembly))
                {
                    wrapperAssembly = Assembly.LoadFrom(typeInfo.WrapperAssemblyPath);
                    AssemblyCache[assemblyName] = wrapperAssembly;
                }

                // 应用Hook
                ApplyHooksForType(typeInfo, wrapperAssembly);
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
            foreach (var (methodFullName, updateMethodInfo) in typeInfo.ModifiedMethods)
            {
                // 直接通过 NewMethodDefinition.Name 查找匹配的方法
                var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.FullName() == updateMethodInfo.ModifyMethodName);

                if (wrapperMethod == null)
                {
                    updateMethodInfo.ErrorMessage = $"在 Wrapper 程序集中找不到方法: {methodFullName}";
                    LoggerScoped.LogWarning(updateMethodInfo.ErrorMessage);
                    continue;
                }
                
                MethodHelper.DisableVisibilityChecks(wrapperMethod);
                
                // 执行 Hook
                var errorMessage = Memory.DetourMethod(updateMethodInfo.ExistingMethod, wrapperMethod);
                
                if (string.IsNullOrEmpty(errorMessage))
                {
                    // Hook 成功
                    updateMethodInfo.NewMethod = wrapperMethod;
                    LoggerScoped.Log($"Hook Success: {methodFullName}");
                }
                else
                {
                    // Hook 失败
                    updateMethodInfo.ErrorMessage = errorMessage;
                    LoggerScoped.LogError($"Hook Failed: {methodFullName}, Error: {errorMessage}");
                }
            }

            // 遍历新增的方法，进行Hook
            foreach (var addedMethod in typeInfo.AddedMethods)
            {
                var addedMethodInfo = addedMethod.Value;

                var methodName = addedMethodInfo.ModifyMethodName;
                var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.FullName() == methodName);

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
