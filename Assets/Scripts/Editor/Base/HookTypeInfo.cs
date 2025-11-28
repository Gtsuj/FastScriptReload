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

    public interface IModifyMethodInfo
    {
        /// <summary>
        /// 当前方法信息
        /// </summary>
        public MethodDefinition MethodDefinition { get; set; }

        /// <summary>
        /// Hook操作失败时的错误信息
        /// </summary>
        public string ErrorMessage { get; set; }
    }
    
    /// <summary>
    /// 新增方法信息（包含方法定义和历史Hook信息）
    /// </summary>
    public class AddedMethodInfo : IModifyMethodInfo
    {
        /// <summary>
        /// 当前的方法定义（Mono.Cecil），用于生成Wrapper
        /// </summary>
        public MethodDefinition MethodDefinition { get; set; }
        
        /// <summary>
        /// Hook操作失败时的错误信息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 历史Hook的方法列表（当新增方法被修改时，需要将这些方法重新Hook到新版本）
        /// </summary>
        public List<MethodInfo> HistoricalHookedMethods { get; set; } = new ();

        /// <summary>
        /// 当前从Wrapper程序集加载的MethodInfo（用于Hook操作）
        /// </summary>
        public MethodInfo CurrentMethod { get; set; }

        /// <summary>
        /// 是否被修改（dirty标记），表示之前新增的方法是否有改动
        /// </summary>
        public bool IsDirty { get; set; }
    }

    /// <summary>
    /// 方法对（原有方法和新方法定义）
    /// </summary>
    public class UpdateMethodInfo : IModifyMethodInfo
    {
        /// <summary>
        /// 新方法定义（Mono.Cecil）
        /// </summary>
        public MethodDefinition MethodDefinition { get; set; }
        
        /// <summary>
        /// Hook操作失败时的错误信息
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// 原有方法
        /// </summary>
        public MethodInfo ExistingMethod { get; set; }

        /// <summary>
        /// 新方法（从程序集加载后的MethodInfo，用于Hook操作）
        /// </summary>
        public MethodInfo NewMethod { get; set; }
    }

}