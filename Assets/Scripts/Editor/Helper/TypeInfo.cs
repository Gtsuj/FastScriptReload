using System;
using System.Collections.Generic;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 类型信息，包含使用Roslyn收集的类型元数据
    /// </summary>
    public class TypeInfo
    {
        // 类型所在文件路径
        public string FilePath;

        // 类型中使用的泛型方法（Key: 调用的泛型方法全名, Value: 调用处的方法名）
        public Dictionary<string, HashSet<string>> UsedGenericMethods = new();

        // 类型中的internal字段
        public HashSet<string> InternalMember = new();

        // 自身是否为internal类
        public bool IsInternalClass;

        public bool IsInternal => IsInternalClass || InternalMember.Count > 0;

        // 如果是部分类，记录其余部分类的文件路径
        public HashSet<string> PartialFiles = new();
    }
}