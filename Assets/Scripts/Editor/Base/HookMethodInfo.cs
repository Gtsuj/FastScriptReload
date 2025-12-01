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
        /// 是否被修改（dirty标记）
        /// </summary>
        public bool IsDirty { get; set; }
    }
    
    /// <summary>
    /// 新增方法信息（包含方法定义和历史Hook信息）
    /// </summary>
    public class AddedMethodInfo : HookMethodInfo
    {
        /// <summary>
        /// 历史Hook的方法列表（当新增方法被修改时，需要将这些方法重新Hook到新版本）
        /// </summary>
        public List<MethodBase> HistoricalHookedMethods { get; set; } = new ();
    }

    /// <summary>
    /// 方法对（原有方法和新方法定义）
    /// </summary>
    public class UpdateMethodInfo : HookMethodInfo
    {
        /// <summary>
        /// 原有方法
        /// </summary>
        public MethodBase OriginalMethod { get; set; }
    }
}