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
        /// 原有类型
        /// </summary>
        [JsonIgnore]
        public Type ExistingType { get; set; }

        /// <summary>
        /// 修改的方法列表
        /// </summary>
        public Dictionary<string, HookMethodInfo> ModifiedMethods = new ();

        /// <summary>
        /// 新增的字段信息
        /// </summary>
        public Dictionary<string, HookFieldInfo> AddedFields { get; } = new ();

        /// <summary>
        /// 尝试获取新增的方法
        /// </summary>
        /// <param name="fullName">方法全名</param>
        /// <param name="methodInfo">方法定义信息</param>
        /// <returns>是否成功</returns>
        public bool TryGetAddedMethod(string fullName, out HookMethodInfo methodInfo)
        {
            return ModifiedMethods.TryGetValue(fullName, out methodInfo) && methodInfo.MemberModifyState == MemberModifyState.Added;
        }

        public void SetAssemblyPath(string assemblyPath, DiffResult diffResult)
        {
            foreach (var (_, methodInfo) in diffResult.AddedMethods)
            {
                if (ModifiedMethods.TryGetValue(methodInfo.FullName, out var modifiedMethodInfo))
                {
                    modifiedMethodInfo.AssemblyPath = assemblyPath;
                }
            }

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