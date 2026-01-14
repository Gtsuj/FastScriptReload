using System.Collections.Generic;

namespace HookInfo.Models
{
    /// <summary>
    /// TypeInfoHelper 初始化请求
    /// </summary>
    public class InitializeRequest
    {
        /// <summary>
        /// 所有程序集上下文（程序集名 -> AssemblyContext）
        /// </summary>
        public Dictionary<string, AssemblyContext> AssemblyContexts { get; set; } = new();

        /// <summary>
        /// 预处理器定义
        /// </summary>
        public string[] PreprocessorDefines { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// 项目路径
        /// </summary>
        public string ProjectPath { get; set; } = string.Empty;
    }
}
