using System;

namespace HookInfo.Models
{
    /// <summary>
    /// 程序集引用信息
    /// </summary>
    [Serializable]
    public class AssemblyReference
    {
        /// <summary>
        /// 程序集名称
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// 程序集路径
        /// </summary>
        public string Path { get; set; }
    }
}
