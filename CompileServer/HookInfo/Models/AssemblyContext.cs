using System;

namespace HookInfo.Models
{
    /// <summary>
    /// 程序集上下文信息（替代 Unity 的 CompilationPipeline.Assembly）
    /// </summary>
    [Serializable]
    public class AssemblyContext
    {
        /// <summary>
        /// 程序集名称（如 Assembly-CSharp）
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 程序集输出路径（如 Library/ScriptAssemblies/Assembly-CSharp.dll）
        /// </summary>
        public string OutputPath { get; set; }
        
        /// <summary>
        /// 源文件路径列表（相对于项目根目录）
        /// </summary>
        public string[] SourceFiles { get; set; }
        
        /// <summary>
        /// 程序集引用列表
        /// </summary>
        public AssemblyReference[] References { get; set; }
        
        /// <summary>
        /// 预处理器定义（如 UNITY_EDITOR, UNITY_2022_3）
        /// </summary>
        public string[] PreprocessorDefines { get; set; }
        
        /// <summary>
        /// 是否允许不安全代码
        /// </summary>
        public bool AllowUnsafeCode { get; set; }
    }
}
