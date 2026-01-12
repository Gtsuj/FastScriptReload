using System;
using System.Collections.Generic;

namespace HookInfo.Models
{
    /// <summary>
    /// 编译请求（Unity -> CompileServer）
    /// </summary>
    [Serializable]
    public class CompileRequest
    {
        /// <summary>
        /// 变更的文件列表
        /// Key: 文件路径（绝对路径）
        /// Value: 文件最后修改时间（ISO 8601 格式）
        /// </summary>
        public Dictionary<string, string> ChangedFiles { get; set; } = new Dictionary<string, string>();
    }
}
