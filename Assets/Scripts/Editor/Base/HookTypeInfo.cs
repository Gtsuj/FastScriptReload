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
        /// 新类型定义（Mono.Cecil）
        /// </summary>
        public TypeDefinition NewTypeDefinition { get; set; }
        
        /// <summary>
        /// 当前Wrapper程序集路径
        /// </summary>
        public string WrapperAssemblyPath { get; set; }        

        /// <summary>
        /// 源代码所在文件
        /// </summary>
        public HashSet<string> SourceFilePaths { get; set; } = new ();

        /// <summary>
        /// 新增的方法信息（包含方法定义和历史Hook信息）
        /// </summary>
        public Dictionary<string, AddedMethodInfo> AddedMethods { get; } = new ();

        /// <summary>
        /// 删除的方法列表
        /// </summary>
        public List<MethodInfo> RemovedMethods { get; } = new ();

        /// <summary>
        /// 修改的方法列表（方法签名相同，但方法体可能不同）
        /// </summary>
        public Dictionary<string, UpdateMethodInfo> ModifiedMethods { get; } = new ();
    }
}