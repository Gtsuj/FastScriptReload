using System.Collections.Generic;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
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