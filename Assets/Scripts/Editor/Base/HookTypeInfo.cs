using System;
using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 类型差异结果
    /// </summary>
    public class HookTypeInfo
    {
        /// <summary>
        /// 类型全名
        /// </summary>
        public string TypeFullName { get; set; }
        
        /// <summary>
        /// 原有类型
        /// </summary>
        public Type ExistingType { get; set; }
        
        /// <summary>
        /// 当前Wrapper程序集路径
        /// </summary>
        public string WrapperAssemblyPath { get; set; }        

        /// <summary>
        /// 修改的方法列表
        /// </summary>
        public Dictionary<string, HookMethodInfo> ModifiedMethods { get; } = new ();

        /// <summary>
        /// 新增的字段信息
        /// Key: 字段全名
        /// Value: 字段定义信息
        /// </summary>
        public Dictionary<string, FieldDefinition> AddedFields { get; } = new ();

        /// <summary>
        /// 尝试获取新增的方法
        /// </summary>
        /// <param name="fullName">方法全名</param>
        /// <param name="methodInfo">方法定义信息</param>
        /// <returns>是否成功</returns>
        public bool TryGetAddedMethod(string fullName, out HookMethodInfo methodInfo)
        {
            return ModifiedMethods.TryGetValue(fullName, out methodInfo) && methodInfo.HookMethodState == HookMethodState.Added;
        }
    }
}