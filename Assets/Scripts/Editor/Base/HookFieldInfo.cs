using System.Windows.Markup;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public class HookFieldInfo
    {
        /// <summary>
        /// 是否被修改（dirty标记）
        /// </summary>
        public bool IsDirty { get; set; }

        /// <summary>
        /// 字段类型
        /// </summary>
        public FieldDefinition FieldType { get; set; }
    }
}