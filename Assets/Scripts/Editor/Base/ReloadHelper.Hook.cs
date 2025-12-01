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
            var wrapperMethods = wrapperType.GetAllMethods().ToArray();

            MethodBase FindWrapperMethod(string methodName)
            {
                var wrapperMethod = wrapperMethods.FirstOrDefault(m => m.FullName() == methodName);

                if (wrapperMethod == null)
                {
                    return null;
                }
                
                MethodHelper.DisableVisibilityChecks(wrapperMethod);

                return wrapperMethod;
            }

            void Hook(string methodFullName, HookMethodInfo hookMethodInfo, MethodBase original, MethodBase replacement)
            {
                // 执行 Hook
                var errorMessage = Memory.DetourMethod(original, replacement);
                
                if (string.IsNullOrEmpty(errorMessage))
                {
                    // Hook 成功
                    hookMethodInfo.IsDirty = false;
                    
                    LoggerScoped.Log($"Hook Success: {methodFullName}");
                }
                else
                {
                    LoggerScoped.LogError($"Hook Failed: {methodFullName}, Error: {errorMessage}");
                }
            }

            // 遍历修改的方法，进行Hook
            foreach (var (methodFullName, updateMethodInfo) in typeInfo.ModifiedMethods)
            {
                if (!updateMethodInfo.IsDirty)
                {
                    continue;
                }

                var wrapperMethod = FindWrapperMethod(updateMethodInfo.ModifyMethodName);
                if (wrapperMethod == null)
                {
                    LoggerScoped.LogError($"Can't find wrapper method: {updateMethodInfo.ModifyMethodName}");
                    continue;
                }
                
                Hook(methodFullName, updateMethodInfo, updateMethodInfo.OriginalMethod, wrapperMethod);
            }

            // 遍历新增的方法，进行Hook
            foreach (var (methodFullName, addedMethodInfo) in typeInfo.AddedMethods)
            {
                if (!addedMethodInfo.IsDirty)
                {
                    continue;
                }
                
                var wrapperMethod = FindWrapperMethod(addedMethodInfo.ModifyMethodName);
                if (wrapperMethod == null)
                {
                    continue;
                }
                
                foreach (var historicalMethod in addedMethodInfo.HistoricalHookedMethods)
                {
                    Hook(methodFullName, addedMethodInfo, historicalMethod, wrapperMethod);
                }
                
                addedMethodInfo.HistoricalHookedMethods.Add(wrapperMethod);
            }
        }
    }
}
