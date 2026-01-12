using System;
using System.Collections.Generic;

namespace HookInfo.Models
{
    /// <summary>
    /// 编译响应（CompileServer -> Unity）
    /// </summary>
    [Serializable]
    public class CompileResponse
    {
        /// <summary>
        /// 是否编译成功
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Hook 类型信息字典
        /// Key: 类型完整名称
        /// Value: Hook 类型信息
        /// </summary>
        public Dictionary<string, HookTypeInfo> HookTypeInfos { get; set; } = new Dictionary<string, HookTypeInfo>();
        
        /// <summary>
        /// 错误消息（编译失败时）
        /// </summary>
        public string ErrorMessage { get; set; }
        
        /// <summary>
        /// 编译耗时（毫秒）
        /// </summary>
        public long ElapsedMilliseconds { get; set; }
        
        /// <summary>
        /// 是否来自缓存
        /// </summary>
        public bool IsFromCache { get; set; }
    }
}
