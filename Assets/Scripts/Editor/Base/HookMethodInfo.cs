using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public abstract class HookMethodInfo
    {
        /// <summary>
        /// 封装过后的静态方法名称
        /// </summary>
        public string ModifyMethodName { get; set; }
        
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
    public class AddedMethodInfo : HookMethodInfo
    {
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
    public class UpdateMethodInfo : HookMethodInfo
    {
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