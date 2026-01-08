using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;
using Newtonsoft.Json;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 类型差异结果
    /// </summary>
    [Serializable]
    public class HookTypeInfo
    {
        /// <summary>
        /// 类型全名
        /// </summary>
        public string TypeFullName;

        /// <summary>
        /// 修改的方法列表
        /// </summary>
        public Dictionary<string, HookMethodInfo> ModifiedMethods = new();

        /// <summary>
        /// 新增的字段信息
        /// </summary>
        public Dictionary<string, HookFieldInfo> AddedFields { get; } = new();

        /// <summary>
        /// 尝试获取方法信息
        /// </summary>
        /// <param name="wrapperFullName">添加this参数后的方法全名</param>
        /// <param name="methodInfo">方法定义信息</param>
        /// <returns>是否成功</returns>
        public bool TryGetMethod(string wrapperFullName, out HookMethodInfo methodInfo)
        {
            methodInfo = null;
            foreach (var (_, modifiedMethod) in ModifiedMethods)
            {
                if (modifiedMethod.WrapperMethodName.Equals(wrapperFullName))
                {
                    methodInfo = modifiedMethod;
                    return true;
                }
            }

            return false;
        }

        public void AddOrModifyMethod(string fullName, MethodDefinition methodDef, MemberModifyState hookMethodState)
        {
            if (!ModifiedMethods.TryGetValue(fullName, out var hookMethodInfo))
            {
                hookMethodInfo = new HookMethodInfo(fullName, methodDef, hookMethodState);
                ModifiedMethods.Add(fullName, hookMethodInfo);
            }
            else
            {
                hookMethodInfo.WrapperMethodDef = methodDef;
            }
        }

        public void SetAssemblyPath(string assemblyPath, DiffResult diffResult)
        {
            // foreach (var (_, methodInfo) in diffResult.AddedMethods)
            // {
            //     if (ModifiedMethods.TryGetValue(methodInfo.FullName, out var modifiedMethodInfo))
            //     {
            //         modifiedMethodInfo.AssemblyPath = assemblyPath;
            //     }
            // }

            foreach (var (_, methodInfo) in diffResult.ModifiedMethods)
            {
                if (ModifiedMethods.TryGetValue(methodInfo.FullName, out var modifiedMethodInfo))
                {
                    modifiedMethodInfo.AssemblyPath = assemblyPath;
                }
            }

            foreach (var (_, fieldInfo) in diffResult.AddedFields)
            {
                if (AddedFields.TryGetValue(fieldInfo.FullName, out var addedFieldInfo))
                {
                    addedFieldInfo.AssemblyPath = assemblyPath;
                }
            }
        }
    }
}