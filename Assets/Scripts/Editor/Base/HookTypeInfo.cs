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
        /// 修改的方法列表
        /// </summary>
        public Dictionary<string, UpdateMethodInfo> ModifiedMethods { get; } = new ();

        /// <summary>
        /// 新增的字段信息
        /// Key: 字段全名
        /// Value: 字段定义信息
        /// </summary>
        public Dictionary<string, HookFieldInfo> AddedFields { get; } = new ();

        /// <summary>
        /// 是否被修改（dirty标记）
        /// </summary>
        public bool IsDirty { get; set; }
    }
}