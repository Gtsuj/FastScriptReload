using System.Windows.Markup;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public class HookFieldInfo
    {
        /// <summary>
        /// 字段类型
        /// </summary>
        public FieldDefinition FieldType { get; set; }
    }
}