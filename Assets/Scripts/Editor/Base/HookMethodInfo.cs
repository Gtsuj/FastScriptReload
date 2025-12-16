using System.Collections.Generic;
using System.Reflection;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public enum HookMethodState
    {
        Added,
        Modified,
    }

    public class HookMethodInfo
    {
        public HookMethodInfo(MethodDefinition wrapperMethodDefinition, HookMethodState hookMethodState, MethodBase originalMethod = null)
        {
            HookMethodState = hookMethodState;
            MethodDefinition = wrapperMethodDefinition;
            WrapperMethodName = wrapperMethodDefinition.FullName;
            OriginalMethod = originalMethod;
        }

        public HookMethodState HookMethodState { get; }

        /// <summary>
        /// 封装过后的静态方法名称
        /// </summary>
        public string WrapperMethodName { get; }
        
        /// <summary>
        /// 当前方法信息
        /// </summary>
        public MethodDefinition MethodDefinition { get; set; }
        
        /// <summary>
        /// 历史Hook的方法列表（当新增方法被修改时，需要将这些方法重新Hook到新版本）
        /// </summary>
        public List<MethodBase> HistoricalHookedMethods { get; } = new ();
        
        /// <summary>
        /// 原有方法
        /// </summary>
        public MethodBase OriginalMethod { get; set; }

        /// <summary>
        /// 源方法的Mono.Cecil定义（用于IL修改）
        /// </summary>
        public MethodDefinition SourceMethodDefinition { get; set; }
    }
}