using System;
using System.Collections.Generic;
using Mono.Cecil;

namespace HookInfo.Models
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
        /// 该类型所属的程序集名称
        /// </summary>
        public string AssemblyName;

        /// <summary>
        /// 修改的方法列表
        /// </summary>
        public Dictionary<string, HookMethodInfo> ModifiedMethods = new();

        /// <summary>
        /// 修改的字段列表
        /// </summary>
        public Dictionary<string, HookFieldInfo> ModifiedFields { get; } = new();

        public void AddOrModifyMethod(string fullName, MethodDefinition wrapperMethodDef, MemberModifyState hookMethodState)
        {
            if (!ModifiedMethods.TryGetValue(fullName, out var hookMethodInfo))
            {
                hookMethodInfo = new HookMethodInfo(fullName, wrapperMethodDef, hookMethodState);
                ModifiedMethods.Add(fullName, hookMethodInfo);
            }
            else
            {
                hookMethodInfo.WrapperMethodDef = wrapperMethodDef;
            }
        }
    }
}
