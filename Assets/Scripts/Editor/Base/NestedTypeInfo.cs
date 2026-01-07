using System.Collections.Generic;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 编译器生成的内部类信息收集器
    /// 
    /// 用途：仅收集编译器生成的内部类（如状态机、闭包类），不处理用户自定义的内部类
    /// 
    /// 为什么需要延迟处理：
    /// 1. 编译器生成的内部类可能被多次引用，延迟处理避免重复
    /// 2. 某些方法（如构造函数）即使未被引用也需要保留，延迟处理可统一判断
    /// 3. 状态机类型需要完整保留所有成员，延迟处理可以做特殊处理
    /// 
    /// 用户自定义的内部类：
    /// - 直接在 HandleAssemblyType 主流程中处理（与外部类相同）
    /// - 可能包含新增/修改的方法，需要正常的 Hook 流程
    /// </summary>
    public class NestedTypeInfo
    {
        public string NestedTypeName { get; }

        public HashSet<string> Methods { get; } = new ();

        public HashSet<string> Fields { get; } = new ();

        public NestedTypeInfo(string nestedTypeName)
        {
            NestedTypeName = nestedTypeName;
        }

        public static Dictionary<string, NestedTypeInfo> NestedTypeInfos = new();

        public static void AddMethod(MethodReference methodRef)
        {
            if (!(methodRef.DeclaringType is TypeDefinition typeDef))
            {
                return;
            }

            var fullName = methodRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef.FullName);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            nestedTypeInfo.Methods.Add(methodRef.FullName);
        }

        public static void AddField(FieldReference fieldRef)
        {
            if (!(fieldRef.DeclaringType is TypeDefinition typeDef))
            {
                return;
            }

            var fullName = fieldRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef.FullName);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            nestedTypeInfo.Fields.Add(fieldRef.FullName);
        }

        public static void Clear()
        {
            NestedTypeInfos.Clear();
        }
    }
}