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
        /// 原始程序集名称（该类型所属的原始 Unity 程序集）
        /// </summary>
        public string OriginalAssemblyName;

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
    }
}
